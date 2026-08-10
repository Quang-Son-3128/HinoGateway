using HinoGateway.Core;
using HinoGateway.Engine;

namespace HinoGateway.Server.Hubs;

/// <summary>
/// Realtime channel for the alarm screens. Separate from <see cref="TelemetryHub"/> so an operator
/// watching alarms doesn't receive the full tag firehose (and vice versa). The server pushes
/// <c>AlarmsChanged</c> (the outstanding list) and <c>AlarmEvents</c> (fresh journal lines).
/// </summary>
public sealed class AlarmHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly AlarmEngine _alarms;

    public AlarmHub(AlarmEngine alarms) => _alarms = alarms;

    /// <summary>Everything outstanding — active, or cleared but not yet acknowledged.</summary>
    public IReadOnlyList<AlarmState> GetActive() => _alarms.States();

    public AlarmSummary GetSummary() => _alarms.Summary();
}
