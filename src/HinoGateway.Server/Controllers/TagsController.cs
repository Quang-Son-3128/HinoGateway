using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using HinoGateway.Server.Security;

namespace HinoGateway.Server.Controllers;

[ApiController]
[Route("api")]
[Authorize(Policy = GatewayPolicies.Viewer)]
public sealed class TagsController : ControllerBase
{
    private readonly TagStore _store;
    private readonly IConfigStore _config;

    public TagsController(TagStore store, IConfigStore config)
    {
        _store = store;
        _config = config;
    }

    /// <summary>Definitions of all currently configured tags.</summary>
    [HttpGet("tags")]
    public async Task<IReadOnlyList<TagInfo>> GetTags(CancellationToken ct)
    {
        var devices = await _config.GetDevicesAsync(ct);
        var tags = devices
            .SelectMany(d => d.Tags.Select(t =>
                new TagInfo(t.Id, t.Name, d.DeviceId, t.DataType, t.Unit, t.Description)))
            .ToList();

        // Memory and expression tags belong to no connection, so walking the devices would leave them
        // out of the Live page entirely even though they are being published like any other tag.
        tags.AddRange((await _config.GetInternalTagsAsync(ct)).Select(t =>
            new TagInfo(t.Id, t.Name, "", t.DataType, t.Unit, t.Description, t.Kind)));
        return tags;
    }

    /// <summary>Current value snapshot for all tags (seeds a client before live updates arrive).</summary>
    [HttpGet("tags/values")]
    public IReadOnlyList<TagSample> GetValues() =>
        _store.Snapshot()
            .Select(kv => new TagSample(kv.Key, kv.Value.Value, kv.Value.Quality, kv.Value.SourceTimestamp, kv.Value.LastChanged, kv.Value.StatusText))
            .ToList();

    /// <summary>Compact status snapshot; its size is independent of the configured tag count.</summary>
    [HttpGet("stats")]
    public GatewayStats GetStats([FromServices] DeviceEngine engine, [FromServices] AlarmEngine alarms)
    {
        var health = engine.Health;
        var quality = _store.QualityCounts();
        var alarm = alarms.Summary();
        return new GatewayStats(
            health.Count,
            health.Count(h => h.State == DeviceState.Connected),
            quality.Total, quality.Good, quality.Uncertain, quality.Bad,
            alarm.Active, alarm.Unacknowledged);
    }

    /// <summary>Per-device health/diagnostics.</summary>
    [HttpGet("health")]
    public IReadOnlyList<DeviceHealth> GetHealth([FromServices] DeviceEngine engine) => engine.Health;

    /// <summary>
    /// What the drivers are actually putting on the wire: one entry per request block, with the
    /// address range it covers, how many tags it serves, its latency and whether the device is
    /// refusing it. Kept out of <c>/api/health</c> so the UI's frequent health poll stays small.
    /// </summary>
    [HttpGet("diagnostics/blocks")]
    public IReadOnlyList<DeviceBlockDiagnostics> GetBlockDiagnostics([FromServices] DeviceEngine engine) =>
        engine.Diagnostics;

    /// <summary>
    /// Links shared by several connections (a serial port, one socket to a TCP↔RTU gateway): how
    /// many connections use each, how much it has carried, and how many are queued for it right now.
    /// Empty when every connection owns its link, which is the normal case.
    /// </summary>
    [HttpGet("diagnostics/transports")]
    public IReadOnlyList<TransportInfo> GetTransportDiagnostics([FromServices] DeviceEngine engine) =>
        engine.Transports;

    /// <summary>
    /// Write (control) a value to a tag, routed to its owning device. The tag id travels in the
    /// body, not the route: runtime ids are folder paths ("Area/Line/Temp") and the '/' they
    /// contain cannot survive a route segment.
    /// </summary>
    [HttpPost("tags/write")]
    [Authorize(Policy = GatewayPolicies.Operator)]
    [EnableRateLimiting("commands")]
    public async Task<IActionResult> Write([FromBody] WriteRequest request,
        [FromServices] DeviceEngine engine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.TagId))
            return BadRequest("A tagId is required.");

        var value = Unwrap(request.Value);
        if (value is null)
            return BadRequest("A value is required.");

        try
        {
            await engine.WriteTagAsync(request.TagId, value, ct);
            return NoContent();
        }
        catch (KeyNotFoundException e)
        {
            return NotFound(e.Message);
        }
        catch (TimeoutException e)
        {
            return StatusCode(StatusCodes.Status504GatewayTimeout, e.Message);
        }
        catch (InvalidOperationException e)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, e.Message);
        }
        catch (Exception e)
        {
            return BadRequest(e.Message);
        }
    }

    public sealed record WriteRequest(string TagId, JsonElement Value);

    // Convert the JSON value into a plain primitive the drivers can Convert.To*.
    private static object? Unwrap(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        _ => null,
    };
}
