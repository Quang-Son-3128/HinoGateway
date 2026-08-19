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

    /// <summary>Never more than this many tags in one request, however many are asked for.</summary>
    private const int MaxTagsPerQuery = 200;

    /// <summary>Frames per request. The 7-day, 1-minute historian holds about 10,000 in total.</summary>
    private const int MaxFrames = 20_000;

    /// <summary>
    /// Values across all frames of one response. 200 tags × 20,000 frames would be four million,
    /// so this is the limit that actually binds; the two above bound each axis separately.
    /// </summary>
    private const int MaxSamples = 500_000;

    /// <summary>
    /// Concurrent history reads. Each one walks JSONL files off the disk of an industrial PC, and
    /// two at a time is already more than an operator or a dashboard will ever need — while ten at
    /// a time is an easy way to take the gateway off the air from a browser tab.
    /// </summary>
    private static readonly SemaphoreSlim Readers = new(2, 2);

    /// <summary>
    /// Raw recorded samples for the named tags.
    ///
    /// <para>
    /// <paramref name="tags"/> is required, and that is a deliberate change from returning every tag
    /// when it was left out. This endpoint needs only the Viewer role — which, on a gateway with
    /// security switched off, is what every request from the network gets — and one call without a
    /// tag list asked the process to hold every value of every tag over the requested window in
    /// memory at once, in a List, before writing a byte. On a large site that is millions of values
    /// per request with no limit on how many requests. The reply is now streamed rather than
    /// assembled, the tag list is capped, and the sample count is capped.
    /// </para>
    /// </summary>
    [HttpGet("/api/history")]
    public async Task<IActionResult> GetHistory(
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

        var wanted = (tags ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return BadRequest(
                "'tags' is required: give a comma-separated list of runtime tag paths. Reading every " +
                "tag at once is refused because the response is unbounded in the number of tags configured.");
        if (wanted.Count > MaxTagsPerQuery)
            return BadRequest($"'tags' names {wanted.Count} tags; at most {MaxTagsPerQuery} may be read in one request.");

        if (!await Readers.WaitAsync(TimeSpan.FromSeconds(2), ct))
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                "Too many history queries are already running. Retry in a moment.");

        // The slot has to be held until the response has actually been written, not until this
        // method returns — Ok() only hands the formatter an enumerable that has not run yet, so
        // releasing here would make the limit above meaningless. The lease is disposed by the
        // iterator when it finishes, and registered with the response as well so that a request
        // aborted before the formatter ever enumerates does not leak a permit. Disposal is
        // idempotent precisely because both of those can happen.
        var lease = new SemaphoreLease(Readers);
        HttpContext.Response.RegisterForDispose(lease);

        // Streamed: frames go out as they are read instead of being collected first, so the memory
        // held at any moment is one frame rather than the whole answer.
        return Ok(StreamAsync(start, end, wanted, Math.Clamp(limit, 1, MaxFrames), lease, ct));
    }

    private async IAsyncEnumerable<HistoryFrameView> StreamAsync(
        DateTimeOffset start, DateTimeOffset end, IReadOnlySet<string> wanted, int limit,
        SemaphoreLease lease,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            var frames = 0;
            var samples = 0;
            await foreach (var frame in _history.ReadAsync(start, end, wanted, ct))
            {
                yield return new HistoryFrameView(frame.Timestamp,
                    frame.Values.ToDictionary(v => v.Key, v => new[] { v.Value.Value, (double)v.Value.Quality }));

                samples += frame.Values.Count;
                if (++frames >= limit || samples >= MaxSamples)
                    yield break;
            }
        }
        finally
        {
            lease.Dispose();
        }
    }

    private sealed class SemaphoreLease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
        }
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
