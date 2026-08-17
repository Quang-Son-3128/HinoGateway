using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Storage;

public sealed class EnergyJournalOptions
{
    /// <summary>
    /// Months to keep. Two years by default, because reconciliation with a utility is an annual
    /// argument and one line a day per counter costs almost nothing to keep.
    /// </summary>
    public int RetentionMonths { get; set; } = 24;

    public long MaxTotalBytes { get; set; } = 64L * 1024 * 1024;
}

/// <summary>
/// The settled-periods journal: <c>&lt;DataRoot&gt;/energy/energy-YYYYMM.jsonl</c>, one line per
/// closed period and per anomaly on the way to one.
///
/// <para>
/// This is the artefact that answers "what did this plant generate on the 12th of August" without a
/// database — the thing taken out when the utility's figure and the gateway's disagree. It records
/// not only the number but how the number was arrived at: which baselines were observed and which
/// were inferred, and whether the counter wrapped, was reset, or produced a reading that was thrown
/// away. A figure with its provenance can be defended; a bare number cannot.
/// </para>
/// </summary>
public sealed class JsonlEnergyJournal : IEnergyJournal, IDisposable
{
    private const string FilePrefix = "energy-";
    private const string FileSuffix = ".jsonl";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly EnergyJournalOptions _options;
    private readonly object _gate = new();

    private FileStream? _stream;
    private StreamWriter? _writer;
    private string? _openPath;
    private bool _disposed;

    public JsonlEnergyJournal(string directory, EnergyJournalOptions options, ILogger logger)
    {
        _directory = Path.GetFullPath(directory);
        _options = options;
        _logger = logger;
        Directory.CreateDirectory(_directory);
        EnforceRetention();
    }

    public string DirectoryPath => _directory;

    public void Append(EnergyPeriodEvent entry)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            try
            {
                EnsureOpen(Path.Combine(_directory, $"{FilePrefix}{entry.Timestamp:yyyyMM}{FileSuffix}"));
                _writer!.WriteLine(JsonSerializer.Serialize(entry, Json));
                _writer.Flush();
                _stream!.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                // Never thrown at the accumulator: losing an audit line must not stop the plant's
                // energy from being computed and reported.
                _logger.LogError(ex, "Could not append to the energy journal in {Directory}", _directory);
                CloseOpenFile();
            }
        }
    }

    public IReadOnlyList<EnergyPeriodEvent> Read(DateTimeOffset? from, DateTimeOffset? to,
        string? sourceKey, AccumulationBoundary? boundary, int limit)
    {
        var results = new List<EnergyPeriodEvent>();
        var wanted = Math.Clamp(limit, 1, 100_000);

        // Newest file first, and newest line first inside it, so a small limit costs a small read.
        foreach (var file in Files().OrderByDescending(f => f.Name, StringComparer.Ordinal))
        {
            List<string> lines;
            try
            {
                lines = ReadLines(file.FullName);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not read the energy journal file {File}", file.Name);
                continue;
            }

            for (var i = lines.Count - 1; i >= 0 && results.Count < wanted; i--)
            {
                if (TryParse(lines[i]) is not { } entry)
                    continue;
                if (from is { } start && entry.Timestamp < start)
                    continue;
                if (to is { } end && entry.Timestamp > end)
                    continue;
                if (sourceKey is not null && !string.Equals(entry.SourceKey, sourceKey, StringComparison.Ordinal))
                    continue;
                if (boundary is { } wantedBoundary && entry.Boundary != wantedBoundary)
                    continue;
                results.Add(entry);
            }

            if (results.Count >= wanted)
                break;
        }

        return results;
    }

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
            32 * 1024, FileOptions.WriteThrough);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false), leaveOpen: true);
        _openPath = path;
        EnforceRetention();
    }

    private static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 32 * 1024);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    private static EnergyPeriodEvent? TryParse(string line)
    {
        try
        {
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<EnergyPeriodEvent>(line, Json);
        }
        catch (JsonException)
        {
            return null;   // a torn last line after a power cut
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

    private void EnforceRetention()
    {
        try
        {
            var cutoff = DateTimeOffset.Now.AddMonths(-Math.Clamp(_options.RetentionMonths, 1, 600));
            var oldest = $"{FilePrefix}{cutoff:yyyyMM}{FileSuffix}";
            long bytes = 0;
            var maxBytes = Math.Max(1_048_576, _options.MaxTotalBytes);

            foreach (var file in Files().OrderByDescending(f => f.Name, StringComparer.Ordinal))
            {
                var keep = string.Equals(file.FullName, _openPath, StringComparison.OrdinalIgnoreCase)
                           || string.CompareOrdinal(file.Name, oldest) >= 0 && bytes + file.Length <= maxBytes;
                if (keep)
                    bytes += file.Length;
                else
                    file.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enforce the energy journal retention policy in {Directory}", _directory);
        }
    }

    private void CloseOpenFile()
    {
        _writer?.Dispose();
        _stream?.Dispose();
        _writer = null;
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
