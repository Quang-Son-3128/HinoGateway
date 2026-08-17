using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Engine;

public sealed class AccumulatorOptions
{
    /// <summary>How often the totals are recomputed. A second is a subtraction, not a measurement.</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How far from a period boundary a history sample may be and still count as "the reading at
    /// midnight". Two minutes at a one-minute sampling rate tolerates a missed frame without
    /// tolerating a guess.
    /// </summary>
    public TimeSpan BoundaryTolerance { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// A backwards jump larger than this is treated as the machine clock being corrected (a dead RTC
    /// battery, NTP stepping the clock after boot) rather than as jitter.
    /// </summary>
    public TimeSpan BackwardClockJump { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The shortest window the spike guard measures a rise over. Without it, a counter with a 0.01
    /// resolution ticking once a second would be judged against a millisecond's worth of allowance
    /// and every real increment would look like a spike.
    /// </summary>
    public TimeSpan MinimumRateWindow { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Produces the <see cref="TagKind.Accumulator"/> tags: energy per calendar period, taken from a
/// device's own cumulative counter.
///
/// <para>
/// The whole design follows from one fact the user established: the inverter's kWh register keeps
/// counting while the gateway is switched off. That makes a day's energy a <b>subtraction</b> —
/// <c>counter(now) − counter(midnight)</c> — and not an accumulation. Only the baseline has to be
/// durable; the current reading is always available from the device. A bad sample therefore costs one
/// instant and heals itself, where <c>previous + P·dt</c> would be wrong forever after one bad
/// sample. It is also why this is not an expression.
/// </para>
///
/// <para>
/// A third producer beside <see cref="DeviceWorker"/> and <see cref="InternalTagEngine"/>, on its own
/// thread, reading the latest values out of the <see cref="TagStore"/> rather than observing every
/// batch: the answer is a subtraction on the newest number, so missing an intermediate sample costs
/// nothing — and publishing from inside an observer of <c>Apply</c> would be recursion into it.
/// </para>
/// </summary>
public sealed class AccumulatorEngine : IAsyncDisposable
{
    private static readonly AccumulationBoundary[] AllBoundaries =
        [AccumulationBoundary.Day, AccumulationBoundary.Month, AccumulationBoundary.Year];

    private readonly TagStore _tags;
    private readonly ILogger _logger;
    private readonly IAccumulatorStateStore _state;
    private readonly ISampleHistory? _history;
    private readonly IEnergyJournal? _journal;
    private readonly Func<DateTimeOffset> _clock;
    private readonly AccumulatorOptions _options;

    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private readonly List<Entry> _entries = new();
    private readonly List<(string TagId, string Reason)> _faults = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private bool _started;
    private long _passes;

    public AccumulatorEngine(
        TagStore tags,
        ILogger logger,
        IAccumulatorStateStore state,
        ISampleHistory? history = null,
        IEnergyJournal? journal = null,
        Func<DateTimeOffset>? clock = null,
        AccumulatorOptions? options = null)
    {
        _tags = tags;
        _logger = logger;
        _state = state;
        _history = history;
        _journal = journal;
        // Local time on purpose: a plant's day, and the utility's, starts at local midnight.
        _clock = clock ?? (() => DateTimeOffset.Now);
        _options = options ?? new AccumulatorOptions();
    }

    public IReadOnlyCollection<string> TagIds => _entries.Select(e => e.Definition.Id).ToList();

    public bool Owns(string tagId) => _entries.Any(e => string.Equals(e.Definition.Id, tagId, StringComparison.Ordinal));

    /// <summary>Tags this engine cannot run — reported on an apply, exactly like a broken formula.</summary>
    public IReadOnlyList<(string TagId, string Reason)> Faults => _faults;

    public AccumulatorStats Stats => new(_entries.Count, _sources.Count, _faults.Count,
        Interlocked.Read(ref _passes), _sources.Values.Sum(s => s.SpikesRejected));

    /// <summary>The live baselines, for the diagnostics endpoint.</summary>
    public IReadOnlyList<AccumulatorSourceInfo> Sources => _sources.Values
        .Select(s => new AccumulatorSourceInfo(s.Key, s.Path, s.RolloverAt, s.MaxRatePerHour,
            s.State, s.SpikesRejected, s.LastStatus))
        .ToList();

    // =====================================================================================
    //  Loading
    // =====================================================================================

    /// <summary>
    /// Take the configuration, rehydrate the durable baselines, and publish immediately.
    ///
    /// <para>
    /// The publish before the first device read is the point of this method, not a detail: every
    /// completed period is already fully determined by what was saved, so yesterday's energy is
    /// <b>Good on the first millisecond after a restart</b>. Without it, the utility's IOA 3 would
    /// read zero — or invalid — for as long as it takes the first poll to come back, on every restart.
    /// </para>
    /// </summary>
    public void Load(IReadOnlyList<TagDefinition> definitions)
    {
        var now = _clock();

        foreach (var definition in definitions)
        {
            if (definition.Kind != TagKind.Accumulator)
                continue;
            if (_entries.Any(e => string.Equals(e.Definition.Id, definition.Id, StringComparison.Ordinal)))
                continue;   // duplicates are refused by validation; ignoring is the safe reaction

            var key = definition.SourceTagKey;
            var path = definition.SourceTagId;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(path))
            {
                Fault(definition.Id, "no source counter is configured");
                continue;
            }

            if (!_sources.TryGetValue(key, out var source))
            {
                _sources[key] = source = new Source(key, path)
                {
                    RolloverAt = definition.RolloverAt,
                    MaxRatePerHour = definition.MaxRatePerHour,
                };
                source.State = Restore(key, now);
            }
            else
            {
                // Two accumulators on one counter must agree about the counter itself. Taking the
                // stricter of the two is the safe reading of a disagreement, and it is logged so the
                // configuration gets fixed rather than quietly reinterpreted.
                Reconcile(source, definition);
            }

            _entries.Add(new Entry(definition, source, definition.Period));
            source.Tags.Add(_entries[^1]);
        }

        _state.RetainOnly(_sources.Keys);

        // Roll forward through every boundary missed while the process was down, using the history
        // alone — no device has been read yet at this point, and none needs to have been.
        foreach (var source in _sources.Values)
            RollBoundaries(source, now, effective: null);

        var batch = new List<TagRead>();
        foreach (var entry in _entries)
            batch.Add(Emit(entry, now, effective: null, Quality.Uncertain, "restoring after a restart"));
        foreach (var (tagId, reason) in _faults)
            batch.Add(new TagRead(tagId, TagValue.Bad(reason, now)));
        if (batch.Count > 0)
            _tags.Apply(batch);

        if (_entries.Count > 0)
            _logger.LogInformation(
                "Accumulators loaded: {Tags} tag(s) over {Sources} counter(s), {Faulted} unusable",
                _entries.Count, _sources.Count, _faults.Count);
    }

    private void Reconcile(Source source, TagDefinition definition)
    {
        if (definition.RolloverAt > 0 && Math.Abs(definition.RolloverAt - source.RolloverAt) > 1e-9)
        {
            if (source.RolloverAt <= 0)
                source.RolloverAt = definition.RolloverAt;
            else
                _logger.LogWarning(
                    "Accumulators on counter {Source} disagree about the rollover point ({Existing} vs {New}); keeping {Kept}",
                    source.Path, source.RolloverAt, definition.RolloverAt, source.RolloverAt);
        }
        if (definition.MaxRatePerHour is { } rate && (source.MaxRatePerHour is not { } existing || rate < existing))
            source.MaxRatePerHour = rate;
    }

    /// <summary>Load the durable state, or start a source that has never been seen before.</summary>
    private AccumulatorSourceState Restore(string key, DateTimeOffset now)
    {
        if (_state.TryGet(key, out var stored) && stored.Boundaries.Count > 0)
            return stored;

        var boundaries = new Dictionary<AccumulationBoundary, AccumulatorBoundaryState>();
        foreach (var boundary in AllBoundaries)
        {
            // Unknown until the first reading arrives; marked so, so nothing derived from it claims
            // to be measured. The first Good sample replaces it (see RebaseUnknown).
            boundaries[boundary] = new AccumulatorBoundaryState(
                new AccumulatorBaseline(boundary.Start(now), 0, Quality.Bad, "never-read"), null);
        }
        return new AccumulatorSourceState { Boundaries = boundaries };
    }

    private void Fault(string tagId, string reason)
    {
        _faults.Add((tagId, reason));
        _logger.LogWarning("Accumulator tag {TagId} is unusable — {Reason}", tagId, reason);
    }

    // =====================================================================================
    //  Running
    // =====================================================================================

    public void Start()
    {
        if (_started || _entries.Count == 0)
            return;
        _started = true;
        _cancellation = new CancellationTokenSource();
        _loop = WorkerThread.Run("accumulators", () => RunAsync(_cancellation.Token),
            ex => _logger.LogError(ex, "Accumulator engine callback failed"));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.Tick);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                Pass(_clock());
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A dead loop here would freeze the plant's energy figures at their last value with
            // nothing to say so — the exact failure this gateway has already been bitten by once.
            _logger.LogCritical(ex, "Accumulator engine stopped unexpectedly; energy totals are no longer updating");
            var now = _clock();
            _tags.Apply(_entries
                .Select(e => new TagRead(e.Definition.Id, TagValue.Bad($"accumulator engine stopped: {ex.Message}", now)))
                .ToList());
        }
    }

    /// <summary>One evaluation of every accumulator, published as a single batch.</summary>
    internal void Pass(DateTimeOffset now)
    {
        Interlocked.Increment(ref _passes);
        var batch = new List<TagRead>(_entries.Count);

        foreach (var source in _sources.Values)
        {
            var (effective, quality, status) = Sample(source, now);
            if (effective is not null)
                RollBoundaries(source, now, effective);
            source.LastStatus = status;

            foreach (var entry in source.Tags)
                batch.Add(Emit(entry, now, effective, quality, status));
        }

        if (batch.Count > 0)
            _tags.Apply(batch);
    }

    /// <summary>
    /// Read the counter and turn it into an effective value, or explain why there isn't one. Nothing
    /// here ever answers zero: a zero on the utility's Ainv_D-1 point reads as "this plant generated
    /// nothing yesterday", which is a statement, not a gap.
    /// </summary>
    private (double? Effective, Quality Quality, string? Status) Sample(Source source, DateTimeOffset now)
    {
        var state = source.State;

        // --- clock discipline -------------------------------------------------
        if (source.ClockHoldUntil is { } hold)
        {
            if (now < hold)
                return (null, Quality.Uncertain, "the machine clock moved backwards; totals are held until it catches up");
            source.ClockHoldUntil = null;
            _logger.LogInformation("Counter {Source}: the clock has caught up; accumulation resumes", source.Path);
        }
        else if (state.LastAt != default && now < state.LastAt - _options.BackwardClockJump)
        {
            // Rolling a period on a clock that is about to be corrected would close today twice.
            source.ClockHoldUntil = state.LastAt;
            _logger.LogWarning(
                "Counter {Source}: the machine clock moved backwards from {Was} to {Now}; " +
                "periods are frozen until it passes {Was} again",
                source.Path, state.LastAt, now, state.LastAt);
            return (null, Quality.Uncertain, "the machine clock moved backwards; totals are held until it catches up");
        }

        // --- the reading ------------------------------------------------------
        if (!_tags.TryGet(source.Path, out var value))
            return (null, Quality.Uncertain, $"counter '{source.Path}' has not been read yet");
        if (value.Quality != Quality.Good)
            return (null, Quality.Uncertain,
                $"counter '{source.Path}' is {value.Quality}{(value.StatusText is { } s ? $": {s}" : "")}");
        if (AsNumber(value.Value) is not { } raw)
            return (null, Quality.Uncertain, $"counter '{source.Path}' did not produce a number");

        // --- normalisation ----------------------------------------------------
        var wraps = state.Wraps;
        var elapsed = state.LastAt == default ? _options.Tick : now - state.LastAt;

        if (state.LastAt != default && raw < state.LastRaw)
        {
            var wrapRise = source.RolloverAt > 0 ? source.RolloverAt - state.LastRaw + raw : double.NaN;
            if (source.RolloverAt > 0 && wrapRise >= 0 && Plausible(source, wrapRise, elapsed))
            {
                wraps++;
                Journal(source, EnergyEvents.Rollover, AccumulationBoundary.Day, now,
                    startRaw: state.LastRaw, endRaw: raw,
                    note: $"counter wrapped at {source.RolloverAt}");
                _logger.LogInformation("Counter {Source}: wrapped at {Rollover} ({Was} → {Now})",
                    source.Path, source.RolloverAt, state.LastRaw, raw);
            }
            else
            {
                // The counter went backwards and it is not a wrap: the meter was replaced, or the PLC
                // program was reloaded. Every baseline is now expressed in a scale that no longer
                // exists, so they are rebased and the period in progress is marked Uncertain.
                Rebase(source, now, raw, "counter-reset");
                return (raw, Quality.Uncertain, "the source counter was reset; today's total restarts from here");
            }
        }
        else if (state.LastAt != default)
        {
            var rise = raw - state.LastRaw;
            if (!Plausible(source, rise, elapsed))
            {
                source.SpikesRejected++;

                // Once per episode, not once per tick. A register stuck at an implausible value is
                // re-read and re-rejected every second, and the journal is a settlement record that
                // an auditor reads — a thousand identical lines would bury the day it belongs to.
                // (The episode ends by itself: LastAt is not advanced, so the allowance grows with
                // the elapsed time until the reading becomes plausible or a better one arrives.)
                if (source.SpikeRun++ == 0)
                {
                    Journal(source, EnergyEvents.SpikeRejected, AccumulationBoundary.Day, now,
                        startRaw: state.LastRaw, endRaw: raw, quality: Quality.Uncertain,
                        note: $"rise of {rise} in {elapsed.TotalSeconds:0.#} s exceeds {source.MaxRatePerHour}/h");
                    _logger.LogWarning("Counter {Source}: rejecting a rise of {Rise} in {Seconds:0.#} s as a spike",
                        source.Path, rise, elapsed.TotalSeconds);
                }

                // Deliberately does not become the new LastRaw: a baseline must never be taken from a
                // sample that was not believed.
                return (null, Quality.Uncertain, "the last reading was rejected as a spike");
            }

            if (source.SpikeRun > 0)
            {
                _logger.LogInformation("Counter {Source}: accepted {Value} after rejecting {Count} reading(s)",
                    source.Path, raw, source.SpikeRun);
                source.SpikeRun = 0;
            }
        }

        var effective = raw + wraps * source.RolloverAt;
        source.State = state with
        {
            LastRaw = raw,
            LastEffective = effective,
            LastAt = now,
            Wraps = wraps,
        };
        _state.Save(source.Key, source.State);

        // A counter seen for the first time (fresh install, or a source that had never been read)
        // gives the baselines their real value instead of the zero they were created with.
        RebaseUnknown(source, now, effective);

        return (effective, Quality.Good, null);
    }

    /// <summary>Is a rise of this size over this long believable for this counter?</summary>
    private bool Plausible(Source source, double rise, TimeSpan elapsed)
    {
        if (source.MaxRatePerHour is not { } max || max <= 0)
            return true;
        if (rise < 0)
            return false;
        var window = elapsed < _options.MinimumRateWindow ? _options.MinimumRateWindow : elapsed;
        return rise <= max * window.TotalHours;
    }

    /// <summary>The counter was replaced or reset: start every period again from the value it reads now.</summary>
    private void Rebase(Source source, DateTimeOffset now, double raw, string reason)
    {
        var boundaries = new Dictionary<AccumulationBoundary, AccumulatorBoundaryState>();
        foreach (var boundary in AllBoundaries)
        {
            // Previous is dropped rather than kept: it was computed in the old counter's scale, and a
            // settled figure that silently changes meaning is worse than one that says it is unknown.
            boundaries[boundary] = new AccumulatorBoundaryState(
                new AccumulatorBaseline(boundary.Start(now), raw, Quality.Uncertain, reason), null);
        }

        _logger.LogWarning("Counter {Source}: went backwards from {Was} to {Now} with no rollover configured " +
                           "that explains it; treating it as a counter reset and rebasing every period",
            source.Path, source.State.LastRaw, raw);
        Journal(source, EnergyEvents.CounterReset, AccumulationBoundary.Day, now,
            startRaw: source.State.LastRaw, endRaw: raw, quality: Quality.Uncertain, note: reason);

        source.State = source.State with
        {
            LastRaw = raw, LastEffective = raw, LastAt = now, Wraps = 0, Boundaries = boundaries,
        };
        _state.Save(source.Key, source.State, immediate: true);
    }

    /// <summary>
    /// Give the "never-read" baselines a real value the first time the counter answers.
    ///
    /// <para>
    /// It goes through <see cref="BaselineAt"/> rather than simply taking the reading in hand, so that
    /// an accumulator added to a gateway that has been recording history for a week starts out with a
    /// midnight baseline that was actually observed — today's figure is then correct from the moment
    /// the tag is created, instead of counting only from whenever somebody happened to add it.
    /// </para>
    /// </summary>
    private void RebaseUnknown(Source source, DateTimeOffset now, double effective)
    {
        var unknown = source.State.Boundaries
            .Where(b => b.Value.Current.Quality == Quality.Bad)
            .Select(b => b.Key)
            .ToList();
        if (unknown.Count == 0)
            return;

        var boundaries = new Dictionary<AccumulationBoundary, AccumulatorBoundaryState>(source.State.Boundaries);
        foreach (var boundary in unknown)
        {
            var existing = boundaries[boundary];
            var baseline = BaselineAt(source, boundary, existing.Current.Start, now, effective)
                           ?? new AccumulatorBaseline(existing.Current.Start, effective, Quality.Uncertain, "first-reading");
            boundaries[boundary] = existing with { Current = baseline };
        }
        source.State = source.State with { Boundaries = boundaries };
        _state.Save(source.Key, source.State, immediate: true);
    }

    // =====================================================================================
    //  Boundaries
    // =====================================================================================

    /// <summary>
    /// Close every period that has ended since the last pass, one at a time. Several at once is the
    /// ordinary case after a power cut lasting days, and each one gets its own journal line so a
    /// reconciliation can be done day by day rather than as one lump.
    /// </summary>
    private void RollBoundaries(Source source, DateTimeOffset now, double? effective)
    {
        var boundaries = new Dictionary<AccumulationBoundary, AccumulatorBoundaryState>(source.State.Boundaries);
        var changed = false;

        foreach (var boundary in AllBoundaries)
        {
            var target = boundary.Start(now);
            var entry = boundaries[boundary];
            if (target <= entry.Current.Start)
                continue;

            var open = entry.Current;
            var previous = entry.Previous;
            var next = NextStart(boundary, open.Start);

            while (next <= target)
            {
                if (BaselineAt(source, boundary, next, now, effective) is not { } baseline)
                    break;   // nothing to work from yet — leave the period open and try again next tick

                var energy = Math.Max(0, baseline.Baseline - open.Baseline);
                var quality = Worst(open.Quality, baseline.Quality);
                previous = new AccumulatorPeriodResult(open.Start, next, energy, quality);

                Journal(source, EnergyEvents.PeriodClosed, boundary, next,
                    start: open.Start, end: next,
                    startRaw: open.Baseline, endRaw: baseline.Baseline,
                    energy: energy, quality: quality,
                    note: open.Quality == Quality.Good && baseline.Quality == Quality.Good
                        ? null
                        : $"baselines: {open.Source} → {baseline.Source}");

                _logger.LogInformation("Counter {Source}: closed {Boundary} {Start:yyyy-MM-dd} at {Energy} ({Quality})",
                    source.Path, boundary, open.Start, energy, quality);

                open = baseline;
                next = NextStart(boundary, next);
            }

            if (!ReferenceEquals(open, entry.Current) || !ReferenceEquals(previous, entry.Previous))
            {
                boundaries[boundary] = new AccumulatorBoundaryState(open, previous);
                changed = true;
            }
        }

        if (!changed)
            return;

        source.State = source.State with { Boundaries = boundaries };
        // Immediate: this is one of the three instants where losing a second loses a whole period.
        _state.Save(source.Key, source.State, immediate: true);
    }

    private static DateTimeOffset NextStart(AccumulationBoundary boundary, DateTimeOffset start) => boundary switch
    {
        AccumulationBoundary.Day => start.AddDays(1),
        AccumulationBoundary.Month => start.AddMonths(1),
        _ => start.AddYears(1),
    };

    /// <summary>
    /// What the counter read at <paramref name="instant"/>, in the order that keeps as many periods
    /// as possible exactly known rather than merely estimated. Null means "not determinable yet",
    /// which leaves the period open instead of guessing.
    /// </summary>
    private AccumulatorBaseline? BaselineAt(Source source, AccumulationBoundary boundary,
        DateTimeOffset instant, DateTimeOffset now, double? effective)
    {
        // 1. The gateway was running and has just crossed the boundary: the reading in hand IS the
        //    reading at the boundary, to within one tick.
        if (effective is { } live && now - instant <= _options.Tick + _options.Tick)
            return new AccumulatorBaseline(instant, live, Quality.Good, "live");

        var offset = source.State.Wraps * source.RolloverAt;

        if (_history is not null)
        {
            // 2. A recorded sample close enough to the boundary to be the boundary.
            if (_history.TryGetNearest(source.Path, instant, _options.BoundaryTolerance, out var near, out _)
                && near.Quality == Quality.Good)
                return new AccumulatorBaseline(instant, near.Value + offset, Quality.Good, "history");

            _history.TryGetAround(source.Path, instant, out var before, out var after);

            // 3. The counter did not move across the gap, so its value at the boundary is not an
            //    estimate at all — it is that value. Overnight on a solar plant this is the normal
            //    case: the gateway can be off from 20:00 to 06:00 and the day still comes out exact.
            if (before is { } b && after is { } a
                && b.Sample.Quality == Quality.Good && a.Sample.Quality == Quality.Good
                && Math.Abs(a.Sample.Value - b.Sample.Value) < 1e-9)
                return new AccumulatorBaseline(instant, b.Sample.Value + offset, Quality.Good, "still-counter");

            // 4. The last thing recorded before the boundary. Honest, and marked Uncertain because it
            //    is not the boundary — the user's own choice for this case.
            if (before is { } last)
                return new AccumulatorBaseline(instant, last.Sample.Value + offset, Quality.Uncertain, "approximate");
        }

        // 5. Nothing recorded at all — an outage longer than the history retention, or a gateway that
        //    has only just been installed. Start the period from what the counter reads now and say so.
        if (effective is { } fallback)
        {
            Journal(source, EnergyEvents.Gap, boundary, now, start: instant, endRaw: fallback,
                quality: Quality.Uncertain,
                note: "no recorded sample near the period boundary; the period starts from the current reading");
            return new AccumulatorBaseline(instant, fallback, Quality.Uncertain, "gap");
        }

        return null;
    }

    // =====================================================================================
    //  Publishing
    // =====================================================================================

    private TagRead Emit(Entry entry, DateTimeOffset now, double? effective, Quality sourceQuality, string? status)
    {
        var definition = entry.Definition;
        var state = entry.Source.State;
        if (!state.Boundaries.TryGetValue(entry.Period.Boundary(), out var boundary))
            return new TagRead(definition.Id, TagValue.Bad("no baseline for this period", now));

        double value;
        Quality quality;
        string? reason;

        if (entry.Period.IsPrevious())
        {
            // A settled period does not depend on the counter at all any more, which is exactly why
            // it can be published Good before the first device read after a restart.
            if (boundary.Previous is not { } settled)
                return new TagRead(definition.Id,
                    TagValue.Bad("no completed period has been recorded for this counter yet", now));
            value = settled.Energy;
            quality = settled.Quality;
            reason = settled.Quality == Quality.Good ? null : "the period's baselines were not fully observed";
        }
        else
        {
            var current = effective ?? state.LastEffective;
            if (state.LastAt == default && effective is null)
                return new TagRead(definition.Id,
                    TagValue.Bad(status ?? "the source counter has not been read yet", now));

            value = current - boundary.Current.Baseline;
            quality = Worst(sourceQuality, boundary.Current.Quality);
            reason = status ?? (quality == Quality.Good ? null : "the period's baseline was not observed");

            if (value < 0)
            {
                // Should be unreachable — a wrap or a reset is handled before this — so treat it as a
                // symptom rather than as a number to publish. Never a negative energy northbound.
                value = 0;
                quality = Quality.Uncertain;
                reason = "the counter reads below this period's baseline";
            }
        }

        if (!InternalTagValues.TryCoerce(definition.DataType, value, out var typed, out var error))
            return new TagRead(definition.Id, TagValue.Bad(error ?? $"{value} does not fit {definition.DataType}", now));

        return new TagRead(definition.Id, quality == Quality.Good
            ? TagValue.Good(typed!, now)
            : new TagValue
            {
                Value = typed,
                Quality = quality,
                StatusText = reason,
                SourceTimestamp = now,
                LastChanged = now,
            });
    }

    private void Journal(Source source, string name, AccumulationBoundary boundary, DateTimeOffset at,
        DateTimeOffset? start = null, DateTimeOffset? end = null,
        double? startRaw = null, double? endRaw = null, double? energy = null,
        Quality quality = Quality.Good, string? note = null) =>
        _journal?.Append(new EnergyPeriodEvent(at, name, source.Path, source.Key, boundary,
            start, end, startRaw, endRaw, energy, quality, note));

    private static Quality Worst(Quality a, Quality b) => a < b ? a : b;

    private static double? AsNumber(object? value) => value switch
    {
        null => null,
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? f : null,
        bool flag => flag ? 1d : 0d,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
            Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        string text => double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : null,
        _ => null,
    };

    // =====================================================================================
    //  Lifetime
    // =====================================================================================

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Accumulator engine did not stop within 5 s");
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cancellation?.Dispose();
    }

    private sealed class Source(string key, string path)
    {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public double RolloverAt { get; set; }
        public double? MaxRatePerHour { get; set; }
        public List<Entry> Tags { get; } = new();
        public AccumulatorSourceState State { get; set; } = new();

        /// <summary>Set while the clock is behind where it has already been; nothing rolls until it passes.</summary>
        public DateTimeOffset? ClockHoldUntil { get; set; }

        public long SpikesRejected { get; set; }

        /// <summary>Readings rejected in the current unbroken run, so an episode is journalled once.</summary>
        public int SpikeRun { get; set; }

        public string? LastStatus { get; set; }
    }

    private sealed record Entry(TagDefinition Definition, Source Source, AccumulationPeriod Period);
}

/// <summary>Counters for the accumulator engine, for the health endpoint and the UI.</summary>
public sealed record AccumulatorStats(
    int Tags,
    int Sources,
    int FaultedTags,
    long Passes,
    long SpikesRejected);

/// <summary>One counter's live baselines, for <c>/api/energy/state</c>.</summary>
public sealed record AccumulatorSourceInfo(
    string SourceKey,
    string SourcePath,
    double RolloverAt,
    double? MaxRatePerHour,
    AccumulatorSourceState State,
    long SpikesRejected,
    string? LastStatus);
