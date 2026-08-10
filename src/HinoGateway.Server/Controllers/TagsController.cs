using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

[ApiController]
[Route("api")]
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
        return devices
            .SelectMany(d => d.Tags.Select(t => new TagInfo(t.Id, t.Name, d.DeviceId, t.DataType, t.Unit, t.Description)))
            .ToList();
    }

    /// <summary>Current value snapshot for all tags (seeds a client before live updates arrive).</summary>
    [HttpGet("tags/values")]
    public IReadOnlyList<TagSample> GetValues() =>
        _store.Snapshot()
            .Select(kv => new TagSample(kv.Key, kv.Value.Value, kv.Value.Quality, kv.Value.SourceTimestamp, kv.Value.LastChanged, kv.Value.StatusText))
            .ToList();

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
    /// Write (control) a value to a tag, routed to its owning device. The tag id travels in the
    /// body, not the route: runtime ids are folder paths ("Area/Line/Temp") and the '/' they
    /// contain cannot survive a route segment.
    /// </summary>
    [HttpPost("tags/write")]
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
