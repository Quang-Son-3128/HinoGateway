namespace HinoGateway.Northbound.Mqtt;

internal interface IMqttDiskSpool
{
    long Dropped { get; }
    Task EnqueueAsync(byte[] payload, CancellationToken ct);
    Task<SpoolItem?> PeekAsync(CancellationToken ct);
    Task CompleteAsync(SpoolItem item, CancellationToken ct);
    (long Count, long Bytes) Stats();
}

/// <summary>Recoverable FIFO made of atomic files for at-least-once delivery.</summary>
public sealed class MqttDiskSpool : IMqttDiskSpool
{
    private readonly string _directory;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _files = new();
    private readonly Dictionary<string, long> _lengths = new(StringComparer.Ordinal);
    private long _sequence;
    private long _dropped;
    private long _bytes;
    private long _indexedCount;

    public MqttDiskSpool(string directory, long maxBytes)
    {
        _directory = Path.GetFullPath(directory);
        _maxBytes = Math.Max(1024 * 1024, maxBytes);
        Directory.CreateDirectory(_directory);
        _sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var temp in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            try { File.Delete(temp); }
            catch (FileNotFoundException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.msg")
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            try
            {
                var length = new FileInfo(path).Length;
                _files.Enqueue(path);
                _lengths[path] = length;
                _bytes += length;
            }
            catch (FileNotFoundException) { }
        }
        TrimToLimit();
    }

    public long Dropped => Interlocked.Read(ref _dropped);

    public async Task EnqueueAsync(byte[] payload, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var id = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Interlocked.Increment(ref _sequence):D20}";
            var final = Path.Combine(_directory, id + ".msg");
            var temp = final + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, payload, ct);
                File.Move(temp, final);
            }
            catch
            {
                try { File.Delete(temp); }
                catch (FileNotFoundException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
            _files.Enqueue(final);
            _lengths[final] = payload.LongLength;
            _bytes += payload.LongLength;
            TrimToLimit();
        }
        finally { _gate.Release(); }
    }

    public async Task<SpoolItem?> PeekAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            RemoveMissingHeads();
            var path = _files.TryPeek(out var head) ? head : null;
            return path is null ? null : new SpoolItem(path, await File.ReadAllBytesAsync(path, ct));
        }
        finally { _gate.Release(); }
    }

    public async Task CompleteAsync(SpoolItem item, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(item.Path)) File.Delete(item.Path);
            RemoveIndexed(item.Path);
            RemoveMissingHeads();
        }
        finally { _gate.Release(); }
    }

    public (long Count, long Bytes) Stats() =>
        (Volatile.Read(ref _indexedCount), Interlocked.Read(ref _bytes));

    private void TrimToLimit()
    {
        Volatile.Write(ref _indexedCount, _files.Count);
        while (_bytes > _maxBytes && _files.TryDequeue(out var path))
        {
            if (_lengths.Remove(path, out var length)) _bytes -= length;
            try { File.Delete(path); }
            catch (FileNotFoundException) { }
            Interlocked.Increment(ref _dropped);
        }
        Volatile.Write(ref _indexedCount, _files.Count);
    }

    private void RemoveMissingHeads()
    {
        while (_files.TryPeek(out var path) && !File.Exists(path))
        {
            _files.Dequeue();
            if (_lengths.Remove(path, out var length)) _bytes -= length;
        }
        Volatile.Write(ref _indexedCount, _files.Count);
    }

    private void RemoveIndexed(string path)
    {
        if (_files.TryPeek(out var head) && StringComparer.Ordinal.Equals(head, path))
            _files.Dequeue();
        else if (_lengths.ContainsKey(path))
        {
            var retained = _files.Where(p => !StringComparer.Ordinal.Equals(p, path)).ToArray();
            _files.Clear();
            foreach (var entry in retained) _files.Enqueue(entry);
        }

        if (_lengths.Remove(path, out var length)) _bytes -= length;
        Volatile.Write(ref _indexedCount, _files.Count);
    }
}

public sealed record SpoolItem(string Path, byte[] Payload);
