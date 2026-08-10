using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace HinoGateway.Server.Services;

/// <summary>
/// Drains the alarm engine's event stream: records each batch in the local trail and pushes it
/// (plus the refreshed outstanding list) to the alarm screens. Forwarding these events northbound
/// (MQTT) will hook in here.
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

    public AlarmService(AlarmEngine alarms, IAlarmJournal journal, IHubContext<AlarmHub> hub,
        ILogger<AlarmService> logger)
    {
        _alarms = alarms;
        _journal = journal;
        _hub = hub;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => JournalLoopAsync(stoppingToken);

    private async Task JournalLoopAsync(CancellationToken ct)
    {
        var batch = new List<AlarmEvent>(MaxBatch);
        try
        {
            while (await _alarms.Events.WaitToReadAsync(ct))
            {
                // Coalesce a burst (an alarm storm on one scan) into a single write + push.
                await Task.Delay(CoalesceWindow, ct);

                batch.Clear();
                while (batch.Count < MaxBatch && _alarms.Events.TryRead(out var evt))
                    batch.Add(evt);
                if (batch.Count == 0)
                    continue;

                await RecordAsync(batch, ct);

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
        }
    }

    private async Task RecordAsync(List<AlarmEvent> batch, CancellationToken ct)
    {
        try
        {
            await _journal.AppendAsync(batch, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Losing a trail entry must not stop alarm processing; the live state is unaffected.
            _logger.LogError(ex, "Failed to record {Count} alarm event(s)", batch.Count);
        }
    }
}
