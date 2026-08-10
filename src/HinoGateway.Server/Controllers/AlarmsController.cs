using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

/// <summary>
/// Alarm configuration, live state, acknowledgement and the journal.
///
/// Unlike connections/tags, an alarm edit needs no explicit "apply": alarms are pure evaluation on
/// top of the tag stream, so changing one is applied to the running engine immediately (no device
/// reconnect, no interruption to acquisition).
/// </summary>
[ApiController]
[Route("api/alarms")]
public sealed class AlarmsController : ControllerBase
{
    private readonly IAlarmStore _store;
    private readonly IAlarmJournal _journal;
    private readonly IConfigStore _config;
    private readonly AlarmEngine _engine;
    private readonly ILogger<AlarmsController> _logger;

    public AlarmsController(IAlarmStore store, IAlarmJournal journal, IConfigStore config,
        AlarmEngine engine, ILogger<AlarmsController> logger)
    {
        _store = store;
        _journal = journal;
        _config = config;
        _engine = engine;
        _logger = logger;
    }

    // --- configuration -------------------------------------------------------

    [HttpGet]
    public Task<IReadOnlyList<AlarmDto>> GetAlarms(CancellationToken ct) => _store.GetAlarmsAsync(ct);

    [HttpPut("{id}")]
    public async Task<IActionResult> Upsert(string id, [FromBody] AlarmDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || dto.Id.Trim() != id)
            return BadRequest("Alarm id in body must match the URL.");

        dto.Name = dto.Name?.Trim() ?? "";
        if (dto.Name.Length == 0)
            return BadRequest("Alarm name is required.");
        if (!Enum.IsDefined(dto.Condition))
            return BadRequest($"Unknown alarm condition '{dto.Condition}'.");
        if (!Enum.IsDefined(dto.Severity))
            return BadRequest($"Unknown severity '{dto.Severity}'.");
        if (dto.Hysteresis < 0)
            return BadRequest("Hysteresis cannot be negative.");
        if (dto.OnDelayMs < 0 || dto.OffDelayMs < 0)
            return BadRequest("Delays cannot be negative.");

        var tags = await _config.GetTagsAsync(ct);
        if (!tags.Any(t => t.Id == dto.TagId))
            return BadRequest("The alarm must reference an existing tag.");

        // One alarm per (tag, condition): the classic four-limit set (LoLo/Lo/Hi/HiHi) stays
        // expressible, but a duplicated limit — almost always an editing mistake — is rejected.
        var existing = await _store.GetAlarmsAsync(ct);
        var clash = existing.FirstOrDefault(a =>
            a.Id != dto.Id && a.TagId == dto.TagId && a.Condition == dto.Condition);
        if (clash is not null)
            return BadRequest($"Alarm '{clash.Name}' already watches this tag with condition {dto.Condition}.");

        await _store.UpsertAlarmAsync(dto, ct);
        await ReloadAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        var removed = await _store.DeleteAlarmAsync(id, ct);
        if (!removed)
            return NotFound();
        await ReloadAsync(ct);
        return NoContent();
    }

    /// <summary>Re-read the alarm configuration into the engine (also called after a tag rename).</summary>
    [HttpPost("reload")]
    public async Task<IActionResult> Reload(CancellationToken ct)
    {
        await ReloadAsync(ct);
        return NoContent();
    }

    // --- live state ----------------------------------------------------------

    /// <summary>Outstanding alarms: active, or cleared but not yet acknowledged.</summary>
    [HttpGet("active")]
    public IReadOnlyList<AlarmState> GetActive() => _engine.States();

    /// <summary>Every configured alarm's state, including the ones sitting in Normal.</summary>
    [HttpGet("states")]
    public IReadOnlyList<AlarmState> GetStates() => _engine.States(includeNormal: true);

    [HttpGet("summary")]
    public AlarmSummary GetSummary() => _engine.Summary();

    /// <summary>
    /// Acknowledge one alarm (<c>alarmId</c>) or everything outstanding (<c>all: true</c>).
    /// The acknowledgement is journalled with the operator name supplied by the caller.
    /// </summary>
    [HttpPost("ack")]
    public ActionResult<AckResult> Acknowledge([FromBody] AckRequest request)
    {
        var user = string.IsNullOrWhiteSpace(request.User) ? null : request.User.Trim();

        if (request.All)
            return new AckResult(_engine.AcknowledgeAll(user));

        if (string.IsNullOrWhiteSpace(request.AlarmId))
            return BadRequest("Provide an alarmId, or all=true.");

        return new AckResult(_engine.Acknowledge(request.AlarmId, user) ? 1 : 0);
    }

    // --- journal -------------------------------------------------------------

    /// <summary>
    /// The local alarm trail. This is a gateway: only the most recent events are kept, in memory —
    /// the durable history belongs to the upstream server the events are forwarded to.
    /// </summary>
    [HttpGet("events")]
    public Task<IReadOnlyList<AlarmEvent>> GetEvents(
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to,
        [FromQuery] string? alarmId, [FromQuery] int limit, CancellationToken ct) =>
        _journal.GetAsync(from, to, alarmId, limit <= 0 ? 200 : limit, ct);

    /// <summary>How much of the local trail is held, and how much has already rolled off.</summary>
    [HttpGet("events/stats")]
    public JournalStats GetJournalStats()
    {
        var (count, dropped) = _journal.Stats;
        return new JournalStats(count, dropped);
    }

    public sealed record JournalStats(int Kept, long Dropped);

    private async Task ReloadAsync(CancellationToken ct)
    {
        var alarms = await _store.GetAlarmsAsync(ct);
        var tags = await _config.GetTagsAsync(ct);
        _engine.Reload(AlarmMapping.ToDefinitions(alarms, tags, _logger));
    }

    public sealed record AckRequest(string? AlarmId, bool All, string? User);

    public sealed record AckResult(int Acknowledged);
}
