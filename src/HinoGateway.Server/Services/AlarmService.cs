using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace HinoGateway.Server.Services;

/// <summary>
/// Drains the alarm engine's event stream: records each batch in the local trail, forwards it
/// northbound over MQTT, and pushes it (plus the refreshed outstanding list) to the alarm screens.
///
/// The engine itself never touches storage or the network — it only writes to a bounded in-memory
/// channel — so a slow consumer can never delay alarm evaluation on the polling threads.
/// </summary>
public sealed class AlarmService : BackgroundService
{
    private const int MaxBatch = 250;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(200);

    private readonly AlarmEngine _alarms;
    private readonly IAlarmJournal _journal;
    private readonly IHubContext<AlarmHub> _hub;
    private readonly ILogger<AlarmService> _logger;
    private readonly ComponentHealthRegistry _health;
    private readonly MqttHostedService _mqtt;

    public AlarmService(AlarmEngine alarms, IAlarmJournal journal, IHubContext<AlarmHub> hub,
        ComponentHealthRegistry health, MqttHostedService mqtt,
        ILogger<AlarmService> logger)
    {
        _mqtt = mqtt;
        _alarms = alarms;
        _journal = journal;
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
                _health.Running("alarm-journal");
                await JournalLoopAsync(stoppingToken);
                if (!stoppingToken.IsCancellationRequested)
                    throw new InvalidOperationException("Alarm journal loop returned unexpectedly.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _health.Failed("alarm-journal", ex);
                _logger.LogError(ex, "Alarm journal loop stopped; restarting");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task JournalLoopAsync(CancellationToken ct)
    {
        var batch = new List<AlarmEvent>(MaxBatch);
        try
        {
            while (await WaitForAlarmAsync(ct))
            {
                // Coalesce a burst (an alarm storm on one scan) into a single write + push.
                await Task.Delay(CoalesceWindow, ct);

                batch.Clear();
                while (batch.Count < MaxBatch && _alarms.Events.TryRead(out var evt))
                    batch.Add(evt);
                if (batch.Count == 0)
                    continue;

                if (await RecordAsync(batch, ct))
                    _health.Heartbeat("alarm-journal");

                // Northbound before the UI push: the local trail is a bounded ring in memory and the
                // screens are transient, so this is the only copy that outlives a restart.
                await _mqtt.ForwardAlarmsAsync(batch, ct);

                try
                {
                    await _hub.Clients.All.SendAsync("AlarmEvents", batch, ct);
                    await _hub.Clients.All.SendAsync("AlarmsChanged", _alarms.States(), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Failed to push alarm updates to clients");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alarm journal loop stopped unexpectedly");
            throw;
        }
    }

    private async Task<bool> RecordAsync(List<AlarmEvent> batch, CancellationToken ct)
    {
        try
        {
            await _journal.AppendAsync(batch, ct);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Losing a trail entry must not stop alarm processing; the live state is unaffected.
            _health.Failed("alarm-journal", ex);
            _logger.LogError(ex, "Failed to record {Count} alarm event(s)", batch.Count);
            return false;
        }
    }

    private async Task<bool> WaitForAlarmAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(5));
            try { return await _alarms.Events.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _health.Heartbeat("alarm-journal");
            }
        }
        return false;
    }
}
