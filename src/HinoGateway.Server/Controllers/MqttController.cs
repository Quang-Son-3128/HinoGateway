using HinoGateway.Core;
using HinoGateway.Server.Security;
using HinoGateway.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

[ApiController]
[Route("api/mqtt")]
[Authorize(Policy = GatewayPolicies.Viewer)]
public sealed class MqttController : ControllerBase
{
    private readonly IConfigStore _store;
    private readonly MqttHostedService _mqtt;

    public MqttController(IConfigStore store, MqttHostedService mqtt)
    {
        _store = store;
        _mqtt = mqtt;
    }

    [HttpGet]
    [Authorize(Policy = GatewayPolicies.Admin)]
    public Task<MqttPublisherOptions> Get(CancellationToken ct) => _store.GetMqttAsync(ct);

    [HttpGet("status")]
    public MqttPublisherStatus Status() => _mqtt.Status;

    [HttpPut]
    [Authorize(Policy = GatewayPolicies.Admin)]
    public async Task<IActionResult> Save([FromBody] MqttPublisherOptions options, CancellationToken ct)
    {
        var errors = ProjectValidator.ValidateMqtt(options);
        if (errors.Count > 0) return BadRequest(errors);
        await _store.SaveMqttAsync(options, ct);
        return NoContent();
    }

    [HttpPost("apply")]
    [Authorize(Policy = GatewayPolicies.Admin)]
    public async Task<ActionResult<MqttPublisherStatus>> Apply(CancellationToken ct) =>
        await _mqtt.ApplyAsync(ct);
}
