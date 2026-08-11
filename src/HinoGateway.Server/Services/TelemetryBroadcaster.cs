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
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(100);
    private readonly DeviceEngine _engine;
    private readonly IHubContext<TelemetryHub> _hub;
    private readonly ILogger<TelemetryBroadcaster> _logger;
    private readonly ComponentHealthRegistry _health;

    public TelemetryBroadcaster(DeviceEngine engine, IHubContext<TelemetryHub> hub,
        ComponentHealthRegistry health, ILogger<TelemetryBroadcaster> logger)
    {
        _engine = engine;
        _hub = hub;
        _logger = logger;
        _health = health;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _health.Running("signalr-telemetry");
                using var subscription = _engine.Tags.Subscribe("signalr", dropOldest: true);
                var deltas = new TelemetryDeltaBuffer();
                var nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
                while (await WaitForTelemetryAsync(subscription.Reader, stoppingToken))
                {
                    // Let device batches arriving in the same scan wave accumulate, then send one
                    // latest-per-tag message instead of one SignalR invocation per device.
                    await Task.Delay(CoalesceWindow, stoppingToken);
                    while (subscription.Reader.TryRead(out var batch))
                        deltas.Add(batch);

                    var samples = deltas.Drain();
                    if (DateTimeOffset.UtcNow >= nextPrune)
                    {
                        deltas.RetainOnly(_engine.Tags.Snapshot().Keys.ToHashSet(StringComparer.Ordinal));
                        nextPrune = DateTimeOffset.UtcNow.AddMinutes(1);
                    }

                    // Completing an iteration is what proves this loop is alive — NOT having something
                    // to send. The tag store publishes every poll batch whether or not a value moved,
                    // so on a plant sitting still this loop spins here with nothing to broadcast and
                    // never reaches the idle wait below. Heartbeating only on a successful send made a
                    // perfectly healthy gateway go stale after 30 s and answer /health/ready with 503,
                    // which is how a watchdog ends up restarting a plant that is running fine.
                    _health.Heartbeat("signalr-telemetry");
                    if (samples.Count == 0)
                        continue;

                    try
                    {
                        await _hub.Clients.All.SendAsync("TagsUpdated", samples, stoppingToken);
                        deltas.MarkSent(samples);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Keep the sent state unchanged: the next device sample is considered unsent and
                        // retried. One transient hub failure must not permanently stop live telemetry.
                        _logger.LogWarning(ex, "Could not broadcast {Count} telemetry delta(s); retrying on the next sample",
                            samples.Count);
                    }
                }
                if (!stoppingToken.IsCancellationRequested)
                    throw new InvalidOperationException("SignalR telemetry subscription closed unexpectedly.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _health.Failed("signalr-telemetry", ex);
                _logger.LogError(ex, "TelemetryBroadcaster stopped unexpectedly; restarting");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task<bool> WaitForTelemetryAsync(
        System.Threading.Channels.ChannelReader<IReadOnlyList<TagRead>> reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(5));
            try { return await reader.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _health.Heartbeat("signalr-telemetry");
            }
        }
        return false;
    }

}
