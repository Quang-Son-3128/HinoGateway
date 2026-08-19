using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Storage;

/// <summary>
/// The energy baselines on disk — <c>hino.energy.json</c> beside the project file.
///
/// <para>
/// This is the file that makes a day's generation survive a power cut, so it is written with the
/// atomic recipe the project file uses (temp, fsync, replace, keep the previous version as
/// <c>.bak</c>) rather than appended to. It is small: a handful of numbers per counter.
/// </para>
///
/// <para>
/// Ordinary ticks are coalesced on a one-second debounce, the same mechanism
/// <see cref="RetainedValueStore"/> uses, because rewriting it every second on an industrial PC's
/// flash card would wear the card out for no benefit. The three moments where a lost second costs a
/// whole period — a boundary rolling, a counter reset, a rebase — bypass the debounce and write
/// before returning.
/// </para>
/// </summary>
public sealed class AccumulatorStateStore : IAccumulatorStateStore, IDisposable
{
    private const string FormatTag = "hino-energy";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeSpan _debounce;
    private readonly ConcurrentDictionary<string, AccumulatorSourceState> _sources = new(StringComparer.Ordinal);
    private readonly object _writeGate = new();
    private readonly Timer _flushTimer;
    private int _flushScheduled;

    /// <summary>Delay before retrying a flush that failed. See <see cref="Retry"/>.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    private bool _disposed;

    public AccumulatorStateStore(string path, ILogger logger, TimeSpan? debounce = null)
    {
        _path = Path.GetFullPath(path);
        _logger = logger;
        _debounce = debounce ?? TimeSpan.FromSeconds(1);
        _flushTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
    }

    public string FilePath => _path;

    public bool TryGet(string sourceKey, out AccumulatorSourceState state) =>
        _sources.TryGetValue(sourceKey, out state!);

    public void Save(string sourceKey, AccumulatorSourceState state, bool immediate = false)
    {
        _sources[sourceKey] = state;
        if (immediate)
        {
            Interlocked.Exchange(ref _flushScheduled, 0);
            Flush();
            return;
        }
        Schedule();
    }

    public void RetainOnly(IEnumerable<string> sourceKeys)
    {
        var keep = sourceKeys as HashSet<string> ?? new HashSet<string>(sourceKeys, StringComparer.Ordinal);
        var removed = false;
        foreach (var key in _sources.Keys)
        {
            if (!keep.Contains(key))
                removed |= _sources.TryRemove(key, out _);
        }
        if (removed)
            Schedule();
    }

    /// <summary>Only the first writer arms the timer; later ones ride along with that flush.</summary>
    private void Schedule()
    {
        if (_disposed)
            return;
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
            _flushTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    public IReadOnlyDictionary<string, AccumulatorSourceState> Snapshot() =>
        new Dictionary<string, AccumulatorSourceState>(_sources, StringComparer.Ordinal);

    private void Flush()
    {
        Interlocked.Exchange(ref _flushScheduled, 0);

        var file = new EnergyFile
        {
            SavedAt = DateTimeOffset.Now,
            Sources = _sources.ToDictionary(kv => kv.Key, kv => Persisted.From(kv.Value), StringComparer.Ordinal),
        };

        lock (_writeGate)
        {
            try
            {
                DurableAtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, Json), _path + ".bak");
            }
            catch (Exception ex) when (Retry(ex))
            {
                // Not thrown at the caller: the accumulation itself is unaffected and still correct in
                // memory, and taking the poll loop down over a failed sidecar write would be a much
                // larger failure than the one being reported.
                _logger.LogError(ex, "Could not write the energy baselines to {Path}; " +
                                     "today's totals are live but would not survive a restart", _path);
            }
        }
    }

    /// <summary>
    /// Schedule another attempt, then let the exception be handled by the block below. A failed
    /// flush left nothing to try again: the timer is armed only by a new reading, so a counter that
    /// had settled for the night could lose the whole day's baseline to one busy moment on the disk.
    /// </summary>
    private bool Retry(Exception ex)
    {
        if (_disposed)
            return true;
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) == 0)
        {
            try { _flushTimer.Change(RetryDelay, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }
        return true;
    }

    /// <summary>
    /// Read the baselines, falling back to the <c>.bak</c> that <see cref="Flush"/> has been writing
    /// all along and that nothing ever read. These numbers are the ones a utility bill is argued
    /// from; losing a day of them because the primary file was truncated by a power cut, while an
    /// intact copy sat unread beside it, is the failure this exists to prevent.
    /// </summary>
    private void Load()
    {
        if (TryLoad(_path, primary: true))
            return;
        if (File.Exists(_path + ".bak") && TryLoad(_path + ".bak", primary: false))
            _logger.LogWarning("Energy baselines were restored from the backup {Path}.bak; " +
                               "the primary file was unusable", _path);
    }

    private bool TryLoad(string path, bool primary)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var file = JsonSerializer.Deserialize<EnergyFile>(File.ReadAllText(path), Json);
            if (file is null || !string.Equals(file.Format, FormatTag, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("{Path} is not an energy-baseline file; ignoring it", path);
                return false;
            }

            foreach (var (key, stored) in file.Sources)
                _sources[key] = stored.ToState();

            _logger.LogInformation("Restored energy baselines for {Count} counter(s) from {Path}",
                _sources.Count, path);
            return true;
        }
        catch (Exception ex)
        {
            // Deliberately not fatal. A gateway that refuses to start because it cannot read
            // yesterday's kWh is off the air entirely; one that starts and reports its totals as
            // Uncertain until the next midnight is still doing its job.
            _logger.Log(primary ? LogLevel.Warning : LogLevel.Error, ex,
                "Could not read the energy baselines from {Path}", path);
            return false;
        }
    }

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

    // The persisted shape is deliberately its own type: the domain records use a dictionary keyed by
    // an enum, which System.Text.Json writes as numbers, and a baseline file that reads "0", "1", "2"
    // instead of "Day", "Month", "Year" is exactly the sort of thing nobody can debug at a substation.
    private sealed class EnergyFile
    {
        public string Format { get; set; } = FormatTag;
        public DateTimeOffset SavedAt { get; set; }
        public Dictionary<string, Persisted> Sources { get; set; } = new();
    }

    private sealed class Persisted
    {
        public double LastRaw { get; set; }
        public double LastEffective { get; set; }
        public DateTimeOffset LastAt { get; set; }
        public long Wraps { get; set; }
        public Dictionary<string, PersistedBoundary> Boundaries { get; set; } = new();

        public static Persisted From(AccumulatorSourceState state) => new()
        {
            LastRaw = state.LastRaw,
            LastEffective = state.LastEffective,
            LastAt = state.LastAt,
            Wraps = state.Wraps,
            Boundaries = state.Boundaries.ToDictionary(
                b => b.Key.ToString(),
                b => new PersistedBoundary
                {
                    CurrentStart = b.Value.Current.Start,
                    CurrentBaseline = b.Value.Current.Baseline,
                    CurrentQuality = b.Value.Current.Quality,
                    CurrentSource = b.Value.Current.Source,
                    PreviousStart = b.Value.Previous?.Start,
                    PreviousEnd = b.Value.Previous?.End,
                    PreviousEnergy = b.Value.Previous?.Energy,
                    PreviousQuality = b.Value.Previous?.Quality,
                }),
        };

        public AccumulatorSourceState ToState()
        {
            var boundaries = new Dictionary<AccumulationBoundary, AccumulatorBoundaryState>();
            foreach (var (name, stored) in Boundaries)
            {
                if (!Enum.TryParse<AccumulationBoundary>(name, ignoreCase: true, out var boundary))
                    continue;   // a boundary this build does not know; ignoring it is safer than guessing
                boundaries[boundary] = new AccumulatorBoundaryState(
                    new AccumulatorBaseline(stored.CurrentStart, stored.CurrentBaseline,
                        stored.CurrentQuality, stored.CurrentSource ?? "restored"),
                    stored.PreviousEnergy is { } energy && stored.PreviousStart is { } start
                        && stored.PreviousEnd is { } end
                        ? new AccumulatorPeriodResult(start, end, energy,
                            stored.PreviousQuality ?? Quality.Uncertain)
                        : null);
            }
            return new AccumulatorSourceState
            {
                LastRaw = LastRaw,
                LastEffective = LastEffective,
                LastAt = LastAt,
                Wraps = Wraps,
                Boundaries = boundaries,
            };
        }
    }

    private sealed class PersistedBoundary
    {
        public DateTimeOffset CurrentStart { get; set; }
        public double CurrentBaseline { get; set; }
        public Quality CurrentQuality { get; set; }
        public string? CurrentSource { get; set; }
        public DateTimeOffset? PreviousStart { get; set; }
        public DateTimeOffset? PreviousEnd { get; set; }
        public double? PreviousEnergy { get; set; }
        public Quality? PreviousQuality { get; set; }
    }
}
