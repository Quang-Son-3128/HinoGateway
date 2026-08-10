using System.Collections.Concurrent;
using System.Text.Json;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Storage;

/// <summary>
/// The sidecar file that keeps retentive memory tags across a restart — a small JSON map beside the
/// project file, never inside it.
///
/// <para>
/// Three properties matter here, and each one is a consequence of what this data is. It is written
/// <b>often</b> (every operator write), so writes are coalesced and never block the caller. It is
/// <b>not configuration</b>, so it stays out of <c>hino.json</c> and out of every <c>.hino</c>
/// export: a project handed to a colleague should not carry last Tuesday's setpoints. And it is
/// <b>not authoritative</b>: if the file is missing or unreadable the gateway starts anyway, with
/// those tags falling back to their configured initial values — refusing to start over an
/// unreadable list of remembered numbers would be a far worse failure than losing them.
/// </para>
/// </summary>
public sealed class RetainedValueStore : IRetainedValueStore, IDisposable
{
    private const string FormatTag = "hino-retained";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);
    private readonly object _writeGate = new();
    private readonly Timer _flushTimer;
    private int _flushScheduled;
    private bool _disposed;

    /// <param name="path">The sidecar file, normally <c>hino.retain.json</c> beside the project.</param>
    /// <param name="debounce">
    /// How long writes are gathered before hitting the disk. Coalescing (not sliding): the first
    /// write arms the timer and later ones join that flush, so a value being turned continuously
    /// still reaches the disk on a fixed cadence instead of never.
    /// </param>
    public RetainedValueStore(string path, ILogger logger, TimeSpan? debounce = null)
    {
        _path = Path.GetFullPath(path);
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromSeconds(1);
        _flushTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
    }

    public string FilePath => _path;

    public bool TryGet(string tagId, out object? value) => _values.TryGetValue(tagId, out value);

    public void Save(string tagId, object? value)
    {
        _values[tagId] = value;
        Schedule();
    }

    public void RetainOnly(IEnumerable<string> tagIds)
    {
        var keep = tagIds as HashSet<string> ?? new HashSet<string>(tagIds, StringComparer.Ordinal);
        var removed = false;
        foreach (var key in _values.Keys)
        {
            if (!keep.Contains(key))
                removed |= _values.TryRemove(key, out _);
        }
        if (removed)
            Schedule();
    }

    private void Schedule()
    {
        if (_disposed)
            return;
        // Only the first writer arms the timer; the rest ride along with that flush.
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            _flushTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        // Snapshot outside the write lock; the dictionary is free to change while we write.
        var file = new RetainedFile
        {
            SavedAt = DateTimeOffset.UtcNow,
            Values = new Dictionary<string, object?>(_values, StringComparer.Ordinal),
        };

        lock (_writeGate)
        {
            var temp = _path + ".tmp";
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(temp, JsonSerializer.Serialize(file, Json));
                if (File.Exists(_path))
                    File.Replace(temp, _path, null, ignoreMetadataErrors: true);
                else
                    File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                // Never thrown at the caller: the operator's write has already taken effect in the
                // running gateway, and failing their request because a sidecar could not be written
                // would misreport what actually happened. The log is the honest place for this.
                _logger.LogError(ex, "Could not write retained tag values to {Path}; " +
                                     "they are live but will not survive a restart", _path);
                try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        try
        {
            var file = JsonSerializer.Deserialize<RetainedFile>(File.ReadAllText(_path), Json);
            if (file is null || !string.Equals(file.Format, FormatTag, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("{Path} is not a retained-values file; ignoring it", _path);
                return;
            }

            foreach (var (tagId, value) in file.Values)
                _values[tagId] = Unwrap(value);

            _logger.LogInformation("Restored {Count} retained tag value(s) from {Path}", _values.Count, _path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read retained tag values from {Path}; " +
                                   "those tags will start from their initial values", _path);
        }
    }

    /// <summary>
    /// JSON gives back a <see cref="JsonElement"/>; the tag engine works in the same three types the
    /// expression evaluator does. Anything unexpected is dropped rather than guessed at — the tag
    /// then starts from its initial value, which is a defined state.
    /// </summary>
    private static object? Unwrap(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetDouble(out var d) ? d : null,
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement => null,
        _ => value,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _flushTimer.Dispose();
        // A pending flush would otherwise be lost exactly when it matters most: at shutdown.
        if (Interlocked.Exchange(ref _flushScheduled, 0) == 1)
            Flush();
    }

    private sealed class RetainedFile
    {
        public string Format { get; set; } = FormatTag;
        public DateTimeOffset SavedAt { get; set; }
        public Dictionary<string, object?> Values { get; set; } = new();
    }
}
