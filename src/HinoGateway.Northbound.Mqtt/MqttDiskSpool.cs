using System.Buffers.Binary;
using System.Globalization;
using HinoGateway.Core;

namespace HinoGateway.Northbound.Mqtt;

internal interface IMqttDiskSpool
{
    long Dropped { get; }
    Task EnqueueAsync(byte[] payload, CancellationToken ct);
    Task<SpoolItem?> PeekAsync(CancellationToken ct);
    Task CompleteAsync(SpoolItem item, CancellationToken ct);
    (long Count, long Bytes) Stats();
}

/// <summary>
/// Recoverable segmented FIFO for at-least-once delivery. Records are appended to a force-flushed
/// segment; a small durable checkpoint records the consumed offset. A torn final record is detected
/// by length/checksum and truncated on recovery. This keeps the directory bounded instead of making
/// one filesystem entry per telemetry batch.
/// </summary>
public sealed class MqttDiskSpool : IMqttDiskSpool
{
    private const int HeaderSize = 8;
    private const int DefaultMaxSegmentFiles = 1024;
    private const long DefaultSegmentBytes = 4L * 1024 * 1024;
    private const int MaxRecordsPerSegment = 4096;
    private const int MaxRecordBytes = 256 * 1024 * 1024;

    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly long _segmentTargetBytes;
    private readonly int _maxSegmentFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<RecordIndex> _records = new();
    private readonly LinkedList<SegmentInfo> _segments = new();
    private readonly Dictionary<string, LinkedListNode<SegmentInfo>> _segmentsByPath =
        new(StringComparer.Ordinal);
    private SegmentInfo? _tail;
    private long _nextSegment;
    private long _dropped;
    private long _bytes;
    private long _indexedCount;

    // Damage found while recovering, as distinct from _dropped, which counts records the spool threw
    // away on purpose to stay inside its size limit. Conflating the two would hide the one that means
    // something is wrong with the disk.
    private long _corruptRecords;
    private long _corruptBytes;

    private readonly Action<string>? _logger;

    public MqttDiskSpool(string directory, long maxBytes, int maxSegmentFiles = DefaultMaxSegmentFiles,
        long segmentTargetBytes = DefaultSegmentBytes, Action<string>? logger = null)
    {
        _logger = logger;
        _directory = Path.GetFullPath(directory);
        _maxBytes = Math.Max(1024 * 1024, maxBytes);
        _maxSegmentFiles = Math.Clamp(maxSegmentFiles, 2, 100_000);
        _segmentTargetBytes = Math.Clamp(segmentTargetBytes, 64 * 1024, _maxBytes);
        Directory.CreateDirectory(_directory);

        CleanupTemporaryFiles();
        RecoverLegacyMessages();
        RecoverSegments();
        CleanupOrphanCheckpoints();
        TrimToLimit();
        _tail = _segments.Last?.Value is { Legacy: false } candidate
                && candidate.Length < _segmentTargetBytes
                && candidate.TotalRecords < MaxRecordsPerSegment
            ? candidate
            : null;
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Records lost to a failed checksum or an unreadable header since this spool opened.</summary>
    public long CorruptRecords => Interlocked.Read(ref _corruptRecords);

    /// <summary>Bytes discarded as unreadable, including the tails that could not be resynchronised.</summary>
    public long CorruptBytes => Interlocked.Read(ref _corruptBytes);

    public async Task EnqueueAsync(byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaxRecordBytes)
            throw new InvalidDataException($"MQTT spool record is {payload.Length} bytes; maximum is {MaxRecordBytes}.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var recordBytes = checked(HeaderSize + payload.LongLength);
            var segment = _tail;
            var isNew = segment is null
                        || segment.Length + recordBytes > _segmentTargetBytes
                        || segment.TotalRecords >= MaxRecordsPerSegment;
            if (isNew)
                segment = NewSegment();

            var recordOffset = segment!.Length;
            try
            {
                await AppendRecordAsync(segment.Path, recordOffset, payload, isNew, ct).ConfigureAwait(false);
            }
            catch
            {
                if (isNew)
                {
                    TryDelete(segment.Path);
                    _tail = null;
                }
                else if (!TryTruncate(segment.Path, recordOffset))
                {
                    // Never append behind an unverified torn tail in this process. A fresh segment is
                    // safe now; startup recovery will truncate the old one at the last indexed record.
                    _tail = null;
                }
                throw;
            }

            if (isNew)
            {
                var node = _segments.AddLast(segment);
                _segmentsByPath.Add(segment.Path, node);
                _tail = segment;
            }

            segment.Length += recordBytes;
            segment.TotalRecords++;
            segment.PendingRecords++;
            _bytes += recordBytes;
            _records.Enqueue(new RecordIndex(segment.Path, recordOffset, recordOffset + HeaderSize,
                payload.Length, recordOffset + recordBytes, Legacy: false));
            Volatile.Write(ref _indexedCount, _records.Count);
            TrimToLimit();
        }
        finally { _gate.Release(); }
    }

    public async Task<SpoolItem?> PeekAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RemoveMissingHeads();
            if (!_records.TryPeek(out var record))
                return null;

            byte[] payload;
            if (record.Legacy)
            {
                payload = await File.ReadAllBytesAsync(record.Path, ct).ConfigureAwait(false);
            }
            else
            {
                payload = new byte[record.PayloadLength];
                await using var stream = new FileStream(record.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                stream.Position = record.PayloadOffset;
                await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            }
            return new SpoolItem(record.Path, payload, record.RecordOffset, record.NextOffset);
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(SpoolItem item, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            RemoveMissingHeads();
            if (!_records.TryPeek(out var record)
                || !StringComparer.Ordinal.Equals(record.Path, item.Path)
                || record.RecordOffset != item.Offset)
                throw new InvalidOperationException("MQTT spool completion is not the current FIFO head.");

            var segment = _segmentsByPath[record.Path].Value;
            if (segment.PendingRecords <= 1)
            {
                // Deleting the completed final record is itself the durable checkpoint. A crash
                // before deletion causes an allowed duplicate; a crash after it cannot resurrect it.
                File.Delete(record.Path);
                TryDelete(CheckpointPath(record.Path));
            }
            else if (!record.Legacy)
            {
                DurableAtomicFile.WriteAllText(CheckpointPath(record.Path),
                    record.NextOffset.ToString(CultureInfo.InvariantCulture));
            }

            _records.Dequeue();
            segment.PendingRecords--;
            if (segment.PendingRecords == 0)
                RemoveSegment(segment);
            Volatile.Write(ref _indexedCount, _records.Count);
        }
        finally { _gate.Release(); }
    }

    public (long Count, long Bytes) Stats() =>
        (Volatile.Read(ref _indexedCount), Interlocked.Read(ref _bytes));

    private SegmentInfo NewSegment()
    {
        var sequence = checked(++_nextSegment);
        var path = Path.Combine(_directory, $"spool-{sequence:D20}.seg");
        return new SegmentInfo(path, Legacy: false);
    }

    private static async Task AppendRecordAsync(string path, long offset, byte[] payload, bool createNew,
        CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(sizeof(int)), Checksum(payload));
        await using var stream = new FileStream(path, createNew ? FileMode.CreateNew : FileMode.Open,
            FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.Position = offset;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private void RecoverLegacyMessages()
    {
        // Upgrade path from the previous one-file-per-batch implementation. Legacy files remain at
        // the FIFO head and are deleted normally; every new write uses a segment.
        foreach (var path in Directory.EnumerateFiles(_directory, "*.msg")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            try
            {
                var length = new FileInfo(path).Length;
                if (length > MaxRecordBytes)
                    throw new InvalidDataException($"Legacy MQTT spool record '{path}' is too large.");
                var segment = new SegmentInfo(path, Legacy: true)
                {
                    Length = length,
                    TotalRecords = 1,
                    PendingRecords = 1,
                };
                var node = _segments.AddLast(segment);
                _segmentsByPath.Add(path, node);
                _records.Enqueue(new RecordIndex(path, 0, 0, checked((int)length), length, Legacy: true));
                _bytes += length;
            }
            catch (FileNotFoundException) { }
        }
        Volatile.Write(ref _indexedCount, _records.Count);
    }

    private void RecoverSegments()
    {
        var paths = Directory.EnumerateFiles(_directory, "spool-*.seg")
            .Select(path => (Path: path, Sequence: ParseSequence(path)))
            .Where(item => item.Sequence >= 0)
            .OrderBy(item => item.Sequence)
            .ToList();
        if (paths.Count > 0)
            _nextSegment = paths.Max(item => item.Sequence);

        foreach (var item in paths)
            RecoverSegment(item.Path);
        Volatile.Write(ref _indexedCount, _records.Count);
    }

    private void RecoverSegment(string path)
    {
        var parsed = new List<RecordIndex>();
        long length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read,
                   64 * 1024, FileOptions.SequentialScan))
        {
            var header = new byte[HeaderSize];
            var corruptRecords = 0;
            while (stream.Position + HeaderSize <= stream.Length)
            {
                var recordOffset = stream.Position;
                stream.ReadExactly(header);
                var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                var expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(sizeof(int)));

                // An unusable length is unrecoverable: the format is length-prefixed, so without a
                // trustworthy length there is no way to find where the next record starts. Everything
                // from here on is lost whether it is intact or not.
                if (payloadLength < 0 || payloadLength > MaxRecordBytes || stream.Position + payloadLength > stream.Length)
                {
                    Discard(stream, path, recordOffset,
                        recordOffset + HeaderSize >= stream.Length
                            ? null                                    // the ordinary torn tail
                            : $"record header at offset {recordOffset} is unreadable (length {payloadLength})");
                    break;
                }

                var payload = new byte[payloadLength];
                stream.ReadExactly(payload);
                if (Checksum(payload) != expectedChecksum)
                {
                    // A bad checksum with a good length is different, and it used to be treated the
                    // same: the loop stopped and the rest of the segment was cut off. One flipped bit
                    // near the start of an 8 MB segment therefore destroyed every intact record
                    // behind it, silently. The length tells us exactly where this record ends, so
                    // skip past just this one and keep reading.
                    corruptRecords++;
                    _corruptRecords++;
                    _corruptBytes += HeaderSize + payloadLength;
                    continue;
                }
                parsed.Add(new RecordIndex(path, recordOffset, recordOffset + HeaderSize,
                    payloadLength, stream.Position, Legacy: false));
            }
            if (stream.Position != stream.Length)
                Discard(stream, path, stream.Position, null);

            if (corruptRecords > 0)
                _logger?.Invoke(
                    $"MQTT spool: {corruptRecords} record(s) in '{Path.GetFileName(path)}' failed their " +
                    "checksum and were skipped. The messages they held are lost; the rest of the segment " +
                    "was kept.");
            length = stream.Length;
        }

        var checkpoint = ReadCheckpoint(path);
        var validOffsets = parsed.Select(r => r.RecordOffset).Append(length).ToHashSet();
        if (!validOffsets.Contains(checkpoint))
            checkpoint = 0;
        var pending = parsed.Where(record => record.RecordOffset >= checkpoint).ToList();
        if (pending.Count == 0)
        {
            TryDelete(path);
            TryDelete(CheckpointPath(path));
            return;
        }

        var segment = new SegmentInfo(path, Legacy: false)
        {
            Length = length,
            TotalRecords = parsed.Count,
            PendingRecords = pending.Count,
        };
        var node = _segments.AddLast(segment);
        _segmentsByPath.Add(path, node);
        foreach (var record in pending) _records.Enqueue(record);
        _bytes += length;
    }

    /// <summary>
    /// Cut the file back to <paramref name="goodLength"/>, keeping a copy of what was cut.
    ///
    /// <para>
    /// Two things happen here that did not before. The bytes are counted and reported, because
    /// "delivery survives a power cut" is a promise this spool makes and the moment it cannot keep
    /// it is exactly the moment worth knowing about — the old code was three lines that set the
    /// length and flushed, with no log, no counter and nothing to look at afterwards. And when the
    /// cut is not an ordinary torn tail, the discarded bytes are copied into <c>quarantine/</c>
    /// first: a segment that fails to parse is the only evidence of whatever damaged it, and
    /// deleting it in place makes the fault unreproducible.
    /// </para>
    ///
    /// <param name="reason">
    /// Null for a torn tail — a record half-written when the power went, which is expected and costs
    /// at most the batch in flight. Anything else is real corruption and is logged as an error.
    /// </param>
    /// </summary>
    private void Discard(FileStream stream, string path, long goodLength, string? reason)
    {
        var lost = stream.Length - goodLength;
        if (lost <= 0)
            return;

        if (reason is not null)
        {
            Quarantine(stream, path, goodLength, lost);
            _corruptBytes += lost;
            _logger?.Invoke(
                $"MQTT spool: {reason}. {lost} byte(s) at the end of '{Path.GetFileName(path)}' could not " +
                "be read and were discarded; a copy is in the spool's quarantine folder.");
        }

        stream.SetLength(goodLength);
        stream.Position = goodLength;
        stream.Flush(flushToDisk: true);
    }

    private void Quarantine(FileStream stream, string path, long from, long length)
    {
        try
        {
            var directory = Path.Combine(_directory, "quarantine");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory,
                $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow:yyyyMMddHHmmss}.bad");

            var position = stream.Position;
            stream.Position = from;
            var buffer = new byte[Math.Min(length, MaxRecordBytes)];
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var remaining = length;
                while (remaining > 0)
                {
                    var read = stream.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
            }
            stream.Position = position;
        }
        catch (Exception ex)
        {
            // Forensics are worth less than recovery: if the copy cannot be made, the truncation
            // still has to happen or the spool will fail to open on every restart.
            _logger?.Invoke($"MQTT spool: could not quarantine the damaged tail of '{path}': {ex.Message}");
        }
    }

    private long ReadCheckpoint(string segmentPath)
    {
        var checkpointPath = CheckpointPath(segmentPath);
        if (!File.Exists(checkpointPath)) return 0;
        try
        {
            return long.TryParse(File.ReadAllText(checkpointPath), NumberStyles.None,
                CultureInfo.InvariantCulture, out var offset) ? offset : 0;
        }
        catch (FileNotFoundException) { return 0; }
    }

    private void TrimToLimit()
    {
        while ((_bytes > _maxBytes || _segments.Count > _maxSegmentFiles) && _segments.First is { } first)
            DropSegment(first.Value);
        Volatile.Write(ref _indexedCount, _records.Count);
    }

    private void DropSegment(SegmentInfo segment)
    {
        var dropped = 0;
        while (_records.TryPeek(out var head) && StringComparer.Ordinal.Equals(head.Path, segment.Path))
        {
            _records.Dequeue();
            dropped++;
        }
        File.Delete(segment.Path);
        TryDelete(CheckpointPath(segment.Path));
        RemoveSegment(segment);
        Interlocked.Add(ref _dropped, dropped);
    }

    private void RemoveSegment(SegmentInfo segment)
    {
        if (_segmentsByPath.Remove(segment.Path, out var node))
            _segments.Remove(node);
        _bytes = Math.Max(0, _bytes - segment.Length);
        if (ReferenceEquals(_tail, segment)) _tail = null;
    }

    private void RemoveMissingHeads()
    {
        while (_records.TryPeek(out var record) && !File.Exists(record.Path))
        {
            _records.Dequeue();
            if (_segmentsByPath.TryGetValue(record.Path, out var node))
            {
                var segment = node.Value;
                var missing = _records.Count(entry => StringComparer.Ordinal.Equals(entry.Path, record.Path));
                if (missing == 0) RemoveSegment(segment);
            }
        }
        Volatile.Write(ref _indexedCount, _records.Count);
    }

    private void CleanupTemporaryFiles()
    {
        foreach (var temp in Directory.EnumerateFiles(_directory, "*.tmp"))
            TryDelete(temp);
    }

    private void CleanupOrphanCheckpoints()
    {
        foreach (var checkpoint in Directory.EnumerateFiles(_directory, "*.seg.ack"))
        {
            var segment = checkpoint[..^4];
            if (!File.Exists(segment)) TryDelete(checkpoint);
        }
    }

    private static long ParseSequence(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("spool-", StringComparison.Ordinal)
               && long.TryParse(name.AsSpan("spool-".Length), NumberStyles.None,
                   CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }

    private static string CheckpointPath(string path) => path + ".ack";

    private static uint Checksum(ReadOnlySpan<byte> payload)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var value in payload)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private static bool TryTruncate(string path, long length)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(length);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (FileNotFoundException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class SegmentInfo(string path, bool Legacy)
    {
        public string Path { get; } = path;
        public bool Legacy { get; } = Legacy;
        public long Length { get; set; }
        public int TotalRecords { get; set; }
        public int PendingRecords { get; set; }
    }

    private sealed record RecordIndex(string Path, long RecordOffset, long PayloadOffset,
        int PayloadLength, long NextOffset, bool Legacy);
}

public sealed record SpoolItem(string Path, byte[] Payload, long Offset = 0, long NextOffset = 0);
