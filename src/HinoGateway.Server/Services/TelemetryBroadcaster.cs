using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace HinoGateway.Server.Services;

/// <summary>
/// Bridges the engine's tag stream to SignalR: reads tag-update batches produced by the device
/// workers and pushes them to all connected browsers as <c>TagsUpdated</c>. It holds its own
/// subscription — a keep-newest one, because a live screen wants the current values rather than a
/// backlog — so a slow network can never back up the poll loops nor starve any other consumer.
/// </summary>
public sealed class TelemetryBroadcaster : BackgroundService
{
    private readonly DeviceEngine _engine;
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly ILogger<TelemetryBroadcaster> _logger;

    public TelemetryBroadcaster(DeviceEngine engine, IHubContext<TelemetryHub> hub, ILogger<TelemetryBroadcaster> logger)
    {
        _engine = engine;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _engine.Tags.Subscribe("signalr", dropOldest: true);
        try
        {
            await foreach (var batch in subscription.Reader.ReadAllAsync(stoppingToken))
            {
                var samples = batch
                    .Select(r => new TagSample(r.TagId, r.Value.Value, r.Value.Quality, r.Value.SourceTimestamp, r.Value.LastChanged, r.Value.StatusText))
                    .ToList();

                await _hub.Clients.All.SendAsync("TagsUpdated", samples, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TelemetryBroadcaster stopped unexpectedly");
        }
    }
}
