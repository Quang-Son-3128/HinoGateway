namespace HinoGateway.Core;

/// <summary>
/// One stored baseline: what the counter read when a period began, and how well that is known.
/// </summary>
/// <param name="Start">Local instant the period began.</param>
/// <param name="Baseline">The source counter's effective value at <paramref name="Start"/>.</param>
/// <param name="Quality">
/// How the baseline was obtained. <see cref="Quality.Good"/> means it was observed — the gateway was
/// running at the boundary, the history holds a sample close enough to it, or the samples either side
/// of a gap were identical so the counter provably did not move. <see cref="Quality.Uncertain"/> means
/// it was inferred, and every figure derived from it inherits that.
/// </param>
/// <param name="Source">How it was obtained: "live", "history", "still-counter", "approximate", "gap".</param>
public sealed record AccumulatorBaseline(
    DateTimeOffset Start,
    double Baseline,
    Quality Quality,
    string Source);

/// <summary>
/// A settled period. Stored as the <em>computed figure</em> rather than as a second baseline, so that
/// a counter replaced this morning cannot retroactively corrupt yesterday's number: once a period is
/// closed its answer no longer depends on anything that happens to the counter afterwards.
/// </summary>
public sealed record AccumulatorPeriodResult(
    DateTimeOffset Start,
    DateTimeOffset End,
    double Energy,
    Quality Quality);

/// <summary>The period in progress on one boundary, and the last one that closed.</summary>
public sealed record AccumulatorBoundaryState(
    AccumulatorBaseline Current,
    AccumulatorPeriodResult? Previous);

/// <summary>
/// Everything durable about one <em>source counter</em>.
///
/// <para>
/// Filed per source rather than per accumulator tag, because "today" and "yesterday" are two readings
/// of the same boundary and because adding a monthly tag to a counter that already has a daily one
/// should cost nothing and be correct from the first tick.
/// </para>
/// </summary>
public sealed record AccumulatorSourceState
{
    /// <summary>The last raw engineering reading accepted from the source, before wrap compensation.</summary>
    public double LastRaw { get; init; }

    /// <summary>The last reading with wraps added back on — the number the subtractions use.</summary>
    public double LastEffective { get; init; }

    public DateTimeOffset LastAt { get; init; }

    /// <summary>How many times the counter has wrapped since the baselines were last rebased.</summary>
    public long Wraps { get; init; }

    public IReadOnlyDictionary<AccumulationBoundary, AccumulatorBoundaryState> Boundaries { get; init; }
        = new Dictionary<AccumulationBoundary, AccumulatorBoundaryState>();
}

/// <summary>
/// The energy baselines that must outlive the process. Keyed by the source tag's <b>storage key</b>.
///
/// <para>
/// A singleton in its own right, deliberately not part of the engine runtime: pressing "Apply to
/// engine" rebuilds that runtime, and a baseline held only there would take the day's energy with it.
/// The engine rehydrates from this store after every reload.
/// </para>
/// </summary>
public interface IAccumulatorStateStore
{
    bool TryGet(string sourceKey, out AccumulatorSourceState state);

    /// <summary>
    /// Record the new state. <paramref name="immediate"/> flushes to disk before returning, and is
    /// used at the only moments where losing one second costs a whole period: a boundary roll, a
    /// detected counter reset, a rebase. Ordinary ticks are coalesced.
    /// </summary>
    void Save(string sourceKey, AccumulatorSourceState state, bool immediate = false);

    /// <summary>Forget sources no accumulator references any more.</summary>
    void RetainOnly(IEnumerable<string> sourceKeys);

    /// <summary>Everything held, for the diagnostics endpoint.</summary>
    IReadOnlyDictionary<string, AccumulatorSourceState> Snapshot();
}

/// <summary>
/// One line in the settled-periods journal — the record taken out to reconcile a figure with the
/// utility, and the reason this gateway needs no database to answer "what did we generate on the 12th".
/// </summary>
public sealed record EnergyPeriodEvent(
    DateTimeOffset Timestamp,
    string Event,
    string Source,
    string SourceKey,
    AccumulationBoundary Boundary,
    DateTimeOffset? Start = null,
    DateTimeOffset? End = null,
    double? StartRaw = null,
    double? EndRaw = null,
    double? Energy = null,
    Quality Quality = Quality.Good,
    string? Note = null);

/// <summary>Event names used in <see cref="EnergyPeriodEvent.Event"/>.</summary>
public static class EnergyEvents
{
    public const string PeriodClosed = "period-closed";
    public const string CounterReset = "counter-reset";
    public const string Rollover = "rollover";
    public const string Gap = "gap";
    public const string SpikeRejected = "spike-rejected";
}

/// <summary>Append-only record of every closed period and every anomaly on the way to one.</summary>
public interface IEnergyJournal
{
    void Append(EnergyPeriodEvent entry);

    /// <summary>Newest first, filtered. Backs the reconciliation endpoint.</summary>
    IReadOnlyList<EnergyPeriodEvent> Read(DateTimeOffset? from, DateTimeOffset? to,
        string? sourceKey, AccumulationBoundary? boundary, int limit);
}
