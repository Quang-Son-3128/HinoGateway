using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Storage;

/// <summary>
/// <see cref="ISampleHistory"/> on plain append-only JSONL, one file per day:
///
/// <code>
/// &lt;DataRoot&gt;/history/hist-20260814.jsonl
/// {"t":"2026-08-14T00:01:00+07:00","v":{"Inverter/Inv1Energy":[123456.78,2],"PowerMeter/Hz":[50.01,2]}}
/// </code>
///
/// <para>
/// Text rather than a binary log, because the person who will need this most is an engineer standing
/// at a substation with nothing but Notepad and <c>findstr</c>, on the day the utility disputes a
/// number. It is also cheap enough not to need cleverness: 32 tags is roughly 1.2 KB a line, 1440
/// lines a day, about 12 MB a week. The byte ceiling is there for the site with 500 tags, not for
/// the ordinary one.
/// </para>
///
/// <para>
/// Durability follows the audit trail's recipe: append mode, write-through, and an
/// explicit flush to disk per frame, so a power cut costs at most the frame being written. A torn
/// last line is expected after such a cut and is skipped on read — losing one minute of history is
/// not a reason to refuse the other seven days.
/// </para>
/// </summary>
public sealed class JsonlSampleHistory : ISampleHistory, IDisposable
{
    private const string FilePrefix = "hist-";
    private const string FileSuffix = ".jsonl";

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly SampleHistoryOptions _options;
    private readonly object _gate = new();

    private FileStream? _stream;
    private string? _openPath;
    private long _frames;
    private long _dropped;
    private bool _disposed;

    public JsonlSampleHistory(string directory, SampleHistoryOptions options, ILogger logger)
    {
        _directory = Path.GetFullPath(directory);
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(_directory);
        EnforceRetention();
    }

    public string DirectoryPath => _directory;

    // =====================================================================================
    //  Writing
    // =====================================================================================

    /// <summary>
    /// Synchronous work behind an asynchronous signature on purpose: this runs once a minute, the
    /// flush is the point of the method, and handing a few milliseconds of write-through I/O to a
    /// thread-pool thread is cheaper than the machinery needed to avoid it.
    /// </summary>
    public Task AppendAsync(HistoryFrame frame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed)
                return Task.CompletedTask;
            try
            {
                EnsureOpen(PathFor(frame.Timestamp));
                var line = Serialise(frame);
                _stream!.Write(line);
                _stream.WriteByte((byte)'\n');
                _stream.Flush(flushToDisk: true);
                Interlocked.Increment(ref _frames);
            }
            catch (Exception ex)
            {
                // A history frame is worth strictly less than the gateway that produces it: count the
                // loss, close the handle so the next frame reopens cleanly, and carry on polling.
                Interlocked.Increment(ref _dropped);
                _logger.LogError(ex, "Could not append a history frame to {Directory}", _directory);
                CloseOpenFile();
            }
        }
        return Task.CompletedTask;
    }

    private string PathFor(DateTimeOffset at) =>
        Path.Combine(_directory, $"{FilePrefix}{at:yyyyMMdd}{FileSuffix}");

    private void EnsureOpen(string path)
    {
        if (string.Equals(path, _openPath, StringComparison.OrdinalIgnoreCase))
            return;

        CloseOpenFile();
        Directory.CreateDirectory(_directory);
        if (!File.Exists(path))
        {
            var create = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite,
                BufferSize = 1, Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
                create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(path, create)) { }
        }
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
            64 * 1024, FileOptions.WriteThrough);
        _openPath = path;

        // Only when the day rolls over, which is also the only moment a file can become surplus.
        EnforceRetention();
    }

    private static byte[] Serialise(HistoryFrame frame)
    {
        var buffer = new ArrayBufferWriter<byte>(128 + frame.Values.Count * 48);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", frame.Timestamp);
            writer.WritePropertyName("v");
            writer.WriteStartObject();
            foreach (var (tagId, sample) in frame.Values)
            {
                // JSON has no way to write NaN or infinity as a number, and a history file that a
                // spreadsheet refuses to open is worse than one missing a nonsensical value.
                if (!double.IsFinite(sample.Value))
                    continue;
                writer.WritePropertyName(tagId);
                writer.WriteStartArray();
                writer.WriteNumberValue(sample.Value);
                writer.WriteNumberValue((int)sample.Quality);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    // =====================================================================================
    //  Reading
    // =====================================================================================

    public async IAsyncEnumerable<HistoryFrame> ReadAsync(DateTimeOffset from, DateTimeOffset to,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in FilesBetween(from, to))
        {
            using var reader = OpenText(file.FullName);
            if (reader is null)
                continue;
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (TryParse(line) is not { } frame)
                    continue;
                if (frame.Timestamp < from || frame.Timestamp > to)
                    continue;
                yield return frame;
            }
        }
    }

    public bool TryGetNearest(string tagId, DateTimeOffset at, TimeSpan tolerance,
        out HistorySample sample, out DateTimeOffset actualAt)
    {
        sample = default!;
        actualAt = default;
        if (tolerance < TimeSpan.Zero)
            tolerance = tolerance.Negate();

        var from = at - tolerance;
        var to = at + tolerance;
        var bestDistance = TimeSpan.MaxValue;
        var found = false;

        foreach (var file in FilesBetween(from, to))
        {
            foreach (var (timestamp, value) in Scan(file.FullName, tagId))
            {
                if (timestamp < from)
                    continue;
                if (timestamp > to)
                    break;   // frames are written in order, so nothing later can be closer
                var distance = timestamp > at ? timestamp - at : at - timestamp;
                if (found && distance >= bestDistance)
                    continue;
                bestDistance = distance;
                sample = value;
                actualAt = timestamp;
                found = true;
            }
        }
        return found;
    }

    public bool TryGetAround(string tagId, DateTimeOffset at,
        out (DateTimeOffset At, HistorySample Sample)? before,
        out (DateTimeOffset At, HistorySample Sample)? after)
    {
        before = null;
        after = null;

        var files = Files();
        var day = DateOnly.FromDateTime(at.LocalDateTime);

        // Backwards from the day containing `at`: the last sample at or before the instant. The scan
        // stops at the first file that has one, so the usual cost is a single day file.
        foreach (var file in files.Where(f => DateOf(f.Name) <= day).OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            foreach (var (timestamp, sample) in Scan(file.FullName, tagId))
            {
                if (timestamp > at)
                    break;
                before = (timestamp, sample);
            }
            if (before is not null)
                break;
        }

        foreach (var file in files.Where(f => DateOf(f.Name) >= day).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (var (timestamp, sample) in Scan(file.FullName, tagId))
            {
                if (timestamp <= at)
                    continue;
                after = (timestamp, sample);
                break;
            }
            if (after is not null)
                break;
        }

        return before is not null || after is not null;
    }

    public DateTimeOffset? OldestRetained
    {
        get
        {
            foreach (var file in Files())
            {
                using var reader = OpenText(file.FullName);
                if (reader is null)
                    continue;
                while (reader.ReadLine() is { } line)
                    if (TryParse(line) is { } frame)
                        return frame.Timestamp;
            }
            return null;
        }
    }

    public HistoryStats Stats
    {
        get
        {
            var files = Files();
            return new HistoryStats(files.Count, files.Sum(f => f.Length),
                Interlocked.Read(ref _frames), Interlocked.Read(ref _dropped));
        }
    }

    /// <summary>Timestamp + value of one tag, frame by frame, without materialising the whole frame.</summary>
    private static IEnumerable<(DateTimeOffset At, HistorySample Sample)> Scan(string path, string tagId)
    {
        using var reader = OpenText(path);
        if (reader is null)
            yield break;
        while (reader.ReadLine() is { } line)
        {
            if (TryParseOne(line, tagId) is { } hit)
                yield return hit;
        }
    }

    private List<FileInfo> Files()
    {
        var directory = new DirectoryInfo(_directory);
        if (!directory.Exists)
            return [];
        return directory.EnumerateFiles($"{FilePrefix}*{FileSuffix}")
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Files that could hold frames in the window. A day either side of it, because the file is named
    /// after the local date of the frames in it while the window may be expressed in another offset —
    /// the per-frame timestamp is what actually decides, this only avoids opening the other six days.
    /// </summary>
    private List<FileInfo> FilesBetween(DateTimeOffset from, DateTimeOffset to)
    {
        var first = DateOnly.FromDateTime(from.LocalDateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(to.LocalDateTime).AddDays(1);
        return Files().Where(f => DateOf(f.Name) is { } date && date >= first && date <= last).ToList();
    }

    private static DateOnly? DateOf(string fileName)
    {
        var stamp = Path.GetFileNameWithoutExtension(fileName);
        if (!stamp.StartsWith(FilePrefix, StringComparison.Ordinal))
            return null;
        return DateOnly.TryParseExact(stamp[FilePrefix.Length..], "yyyyMMdd", out var date) ? date : null;
    }

    /// <summary>
    /// Share-read-write on purpose: the newest file is normally held open by this same object for
    /// appending, and a reader that insists on exclusivity would fail on exactly the file that
    /// matters most.
    /// </summary>
    private static StreamReader? OpenText(string path)
    {
        try
        {
            return new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 64 * 1024));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static HistoryFrame? TryParse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out var stamp)
                || !stamp.TryGetDateTimeOffset(out var at))
                return null;

            var values = new Dictionary<string, HistorySample>(StringComparer.Ordinal);
            if (root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in v.EnumerateObject())
                {
                    if (TrySample(property.Value) is { } sample)
                        values[property.Name] = sample;
                }
            }
            return new HistoryFrame(at, values);
        }
        catch (JsonException)
        {
            return null;   // a torn last line after a power cut, or a hand-edited file
        }
    }

    private static (DateTimeOffset At, HistorySample Sample)? TryParseOne(string line, string tagId)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("t", out var stamp)
                || !stamp.TryGetDateTimeOffset(out var at)
                || !root.TryGetProperty("v", out var v)
                || v.ValueKind != JsonValueKind.Object
                || !v.TryGetProperty(tagId, out var entry))
                return null;
            return TrySample(entry) is { } sample ? (at, sample) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HistorySample? TrySample(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
            return null;
        var array = entry.EnumerateArray();
        array.MoveNext();
        if (!array.Current.TryGetDouble(out var value))
            return null;
        array.MoveNext();
        if (!array.Current.TryGetInt32(out var quality))
            return null;
        return new HistorySample(value, (Quality)Math.Clamp(quality, (int)Quality.Bad, (int)Quality.Good));
    }

    // =====================================================================================
    //  Retention
    // =====================================================================================

    /// <summary>
    /// Days, then file count, then total bytes — whichever bites first. The file currently open for
    /// appending is never a candidate: deleting it would leave the writer holding a handle to a file
    /// nobody can find, and today's history is the last thing anyone wants to lose.
    /// </summary>
    private void EnforceRetention()
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(_options.RetentionDays, 1, 3650)).UtcDateTime;
            var maxFiles = Math.Clamp(_options.MaxFiles, 1, 10_000);
            var maxBytes = Math.Max(1_048_576, _options.MaxTotalBytes);
            long retainedBytes = 0;
            var retainedFiles = 0;

            foreach (var file in Files().OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                var keep = string.Equals(file.FullName, _openPath, StringComparison.OrdinalIgnoreCase)
                           || file.LastWriteTimeUtc >= cutoff
                           && retainedFiles < maxFiles
                           && retainedBytes + file.Length <= maxBytes;
                if (keep)
                {
                    retainedFiles++;
                    retainedBytes += file.Length;
                }
                else
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enforce the history retention policy in {Directory}", _directory);
        }
    }

    private void CloseOpenFile()
    {
        _stream?.Dispose();
        _stream = null;
        _openPath = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CloseOpenFile();
        }
    }
}
