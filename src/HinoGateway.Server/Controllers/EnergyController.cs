using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Server.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

/// <summary>
/// What the energy totals are built on: the live baselines, the settled periods, and the raw history
/// behind both.
///
/// <para>
/// These exist so that a number sent to the utility can be defended rather than merely asserted. When
/// the control centre's figure and the gateway's disagree, the argument is settled by showing which
/// baselines were observed and which were inferred, and by pointing at the recorded samples.
/// </para>
/// </summary>
[ApiController]
[Route("api/energy")]
[Authorize(Policy = GatewayPolicies.Viewer)]
public sealed class EnergyController : ControllerBase
{
    private readonly DeviceEngine _engine;
    private readonly IEnergyJournal _journal;
    private readonly ISampleHistory _history;

    public EnergyController(DeviceEngine engine, IEnergyJournal journal, ISampleHistory history)
    {
        _engine = engine;
        _journal = journal;
        _history = history;
    }

    /// <summary>The period in progress on every counter: its baseline, how that baseline was obtained,
    /// and the last period that closed.</summary>
    [HttpGet("state")]
    public EnergyStateResponse GetState() =>
        new(_engine.Accumulators, _engine.AccumulatorSources.Select(EnergySourceView.From).ToList());

    /// <summary>
    /// The settled-periods journal — the reconciliation record. Newest first.
    /// </summary>
    [HttpGet("periods")]
    public IReadOnlyList<EnergyPeriodView> GetPeriods(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? sourceKey,
        [FromQuery] AccumulationBoundary? boundary,
        [FromQuery] int limit = 500) =>
        _journal.Read(from, to, sourceKey, boundary, limit).Select(EnergyPeriodView.From).ToList();

    /// <summary>
    /// Raw recorded samples. <paramref name="tags"/> is a comma-separated list of runtime tag paths;
    /// leaving it out returns every tag in each frame.
    /// </summary>
    [HttpGet("/api/history")]
    public async Task<ActionResult<IReadOnlyList<HistoryFrameView>>> GetHistory(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? tags,
        [FromQuery] int limit = 2000,
        CancellationToken ct = default)
    {
        var end = to ?? DateTimeOffset.Now;
        var start = from ?? end.AddHours(-1);
        if (start > end)
            return BadRequest("'from' must not be after 'to'.");

        var wanted = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        var capped = Math.Clamp(limit, 1, 100_000);

        var frames = new List<HistoryFrameView>();
        await foreach (var frame in _history.ReadAsync(start, end, ct))
        {
            var values = wanted is null
                ? frame.Values
                : frame.Values.Where(v => wanted.Contains(v.Key))
                    .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            if (values.Count == 0)
                continue;
            frames.Add(new HistoryFrameView(frame.Timestamp,
                values.ToDictionary(v => v.Key, v => new[] { v.Value.Value, (double)v.Value.Quality })));
            if (frames.Count >= capped)
                break;
        }
        return frames;
    }

    /// <summary>How much history exists, and what the sampler has managed to write.</summary>
    [HttpGet("/api/history/status")]
    public HistoryStatusResponse GetHistoryStatus() => new(_history.OldestRetained, _history.Stats);
}

public sealed record EnergyStateResponse(AccumulatorStats Stats, IReadOnlyList<EnergySourceView> Sources);

/// <summary>
/// A journal line on the wire. The only difference from the stored record is that the boundary is
/// named rather than numbered — the file on disk names it, and a reconciliation report that says
/// <c>"boundary": 0</c> where the file says <c>"Day"</c> is a needless thing to have to explain.
/// </summary>
public sealed record EnergyPeriodView(
    DateTimeOffset Timestamp,
    string Event,
    string Source,
    string SourceKey,
    string Boundary,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    double? StartRaw,
    double? EndRaw,
    double? Energy,
    Quality Quality,
    string? Note)
{
    public static EnergyPeriodView From(EnergyPeriodEvent e) => new(
        e.Timestamp, e.Event, e.Source, e.SourceKey, e.Boundary.ToString(),
        e.Start, e.End, e.StartRaw, e.EndRaw, e.Energy, e.Quality, e.Note);
}

/// <summary>
/// One counter, flattened for the wire. The nested dictionary keyed by an enum that the domain record
/// uses serialises to numeric keys, which is unreadable in a browser's network tab and in a report.
/// </summary>
public sealed record EnergySourceView(
    string SourceKey,
    string SourcePath,
    double RolloverAt,
    double? MaxRatePerHour,
    double LastRaw,
    double LastEffective,
    DateTimeOffset LastAt,
    long Wraps,
    long SpikesRejected,
    string? Status,
    IReadOnlyList<EnergyBoundaryView> Boundaries)
{
    public static EnergySourceView From(AccumulatorSourceInfo info) => new(
        info.SourceKey, info.SourcePath, info.RolloverAt, info.MaxRatePerHour,
        info.State.LastRaw, info.State.LastEffective, info.State.LastAt, info.State.Wraps,
        info.SpikesRejected, info.LastStatus,
        info.State.Boundaries
            .Select(b => new EnergyBoundaryView(
                b.Key.ToString(),
                b.Value.Current.Start, b.Value.Current.Baseline,
                b.Value.Current.Quality, b.Value.Current.Source,
                b.Value.Previous?.Start, b.Value.Previous?.End,
                b.Value.Previous?.Energy, b.Value.Previous?.Quality))
            .OrderBy(b => b.Boundary, StringComparer.Ordinal)
            .ToList());
}

public sealed record EnergyBoundaryView(
    string Boundary,
    DateTimeOffset CurrentStart,
    double CurrentBaseline,
    Quality CurrentQuality,
    string CurrentBaselineSource,
    DateTimeOffset? PreviousStart,
    DateTimeOffset? PreviousEnd,
    double? PreviousEnergy,
    Quality? PreviousQuality);

/// <summary>A history frame on the wire: <c>{ "tag": [value, quality] }</c>, as it is on disk.</summary>
public sealed record HistoryFrameView(DateTimeOffset Timestamp, IReadOnlyDictionary<string, double[]> Values);

public sealed record HistoryStatusResponse(DateTimeOffset? OldestRetained, HistoryStats Stats);
