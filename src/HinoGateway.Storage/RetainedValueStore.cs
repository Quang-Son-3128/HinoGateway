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
    private int _flushFailed;
    private bool _disposed;

    /// <summary>How long to wait before trying a failed flush again. Long enough not to spin on a
    /// full disk, short enough that a transient fault costs seconds rather than the next write.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

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
            try
            {
                // The backup path was missing here while the project file and the energy baselines
                // both had one, so this was the only durable state in the gateway with nothing to
                // fall back on: a file truncated by a power cut mid-publish took every remembered
                // setpoint with it.
                DurableAtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, Json), _path + ".bak");
                Interlocked.Exchange(ref _flushFailed, 0);
            }
            catch (Exception ex)
            {
                // Never thrown at the caller: the operator's write has already taken effect in the
                // running gateway, and failing their request because a sidecar could not be written
                // would misreport what actually happened. The log is the honest place for this.
                _logger.LogError(ex, "Could not write retained tag values to {Path}; " +
                                     "they are live but will not survive a restart", _path);

                // And retry, because the timer is only ever armed by a new write. A disk that was
                // briefly full at the moment of the only write of the day would otherwise mean the
                // values never reached the disk at all, with nothing further to trigger an attempt.
                Interlocked.Exchange(ref _flushFailed, 1);
                RetryLater();
            }
        }
    }

    /// <summary>Re-arm the timer after a failed flush, without disturbing one already pending.</summary>
    private void RetryLater()
    {
        if (_disposed)
            return;
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            try { _flushTimer.Change(RetryDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Read the file, and if it cannot be read, the <c>.bak</c> beside it.
    ///
    /// <para>
    /// Writing a backup is only half of the arrangement — the half that was here. A file that
    /// survives a power cut but is never read on the way back is a file that does nothing, and the
    /// one moment it matters is the one moment nobody is watching.
    /// </para>
    /// </summary>
    private void Load()
    {
        if (TryLoad(_path, primary: true))
            return;
        if (File.Exists(_path + ".bak") && TryLoad(_path + ".bak", primary: false))
            _logger.LogWarning("Retained tag values were restored from the backup {Path}.bak; " +
                               "the primary file was unusable", _path);
    }

    private bool TryLoad(string path, bool primary)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var file = JsonSerializer.Deserialize<RetainedFile>(File.ReadAllText(path), Json);
            if (file is null || !string.Equals(file.Format, FormatTag, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("{Path} is not a retained-values file; ignoring it", path);
                return false;
            }

            foreach (var (tagId, value) in file.Values)
                _values[tagId] = Unwrap(value);

            _logger.LogInformation("Restored {Count} retained tag value(s) from {Path}", _values.Count, path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log(primary ? LogLevel.Warning : LogLevel.Error, ex,
                "Could not read retained tag values from {Path}", path);
            return false;
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
