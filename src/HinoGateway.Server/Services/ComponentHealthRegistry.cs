using HinoGateway.Engine;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HinoGateway.Server.Services;

public sealed class ComponentHealthOptions
{
    public int HeartbeatTimeoutSeconds { get; init; } = 30;
    public int RestartWindowSeconds { get; init; } = 300;
    public int MaxRestartsInWindow { get; init; } = 3;
    public int RecoveryGraceSeconds { get; init; } = 15;
}

public sealed record ComponentHealth(
    string Name,
    bool Running,
    long Restarts,
    int RecentRestarts,
    DateTimeOffset LastHeartbeat,
    DateTimeOffset? LastFailure,
    string? LastError,
    bool Stale,
    bool RestartStorm,
    bool AlarmActive,
    string? AlarmReason);

/// <summary>
/// Tracks the supervised background loops. A component is not declared healthy merely because its
/// replacement task has started: its last failure remains visible for a recovery grace period, and
/// repeated failures in a rolling window raise a persistent restart-storm alarm.
/// </summary>
public sealed class ComponentHealthRegistry
{
    private sealed class Entry(string name, DateTimeOffset now)
    {
        public string Name { get; } = name;
        public bool Running { get; set; } = true;
        public long Restarts { get; set; }
        public DateTimeOffset LastHeartbeat { get; set; } = now;
        public DateTimeOffset? LastFailure { get; set; }
        public string? LastError { get; set; }
        public Queue<DateTimeOffset> RestartTimes { get; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TimeSpan _restartWindow;
    private readonly int _maxRestarts;
    private readonly TimeSpan _recoveryGrace;

    public ComponentHealthRegistry(IConfiguration configuration)
    {
        var options = configuration.GetSection("Health:Components").Get<ComponentHealthOptions>() ?? new();
        _heartbeatTimeout = TimeSpan.FromSeconds(Math.Clamp(options.HeartbeatTimeoutSeconds, 5, 3600));
        _restartWindow = TimeSpan.FromSeconds(Math.Clamp(options.RestartWindowSeconds, 10, 86_400));
        _maxRestarts = Math.Clamp(options.MaxRestartsInWindow, 1, 10_000);
        _recoveryGrace = TimeSpan.FromSeconds(Math.Clamp(options.RecoveryGraceSeconds, 0, 3600));
    }

    public void Running(string name) => MarkAlive(name);

    public void Heartbeat(string name) => MarkAlive(name);

    private void MarkAlive(string name)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_items.TryGetValue(name, out var entry))
                _items[name] = entry = new Entry(name, now);
            entry.Running = true;
            entry.LastHeartbeat = now;
        }
    }

    public void Failed(string name, Exception ex)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (!_items.TryGetValue(name, out var entry))
                _items[name] = entry = new Entry(name, now);
            entry.Running = false;
            entry.Restarts++;
            entry.LastHeartbeat = now;
            entry.LastFailure = now;
            entry.LastError = ex.Message;
            entry.RestartTimes.Enqueue(now);
            Prune(entry, now);
        }
    }

    public IReadOnlyList<ComponentHealth> Snapshot
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            lock (_gate)
                return _items.Values.Select(x => SnapshotOf(x, now)).OrderBy(x => x.Name).ToList();
        }
    }

    private ComponentHealth SnapshotOf(Entry entry, DateTimeOffset now)
    {
        Prune(entry, now);
        var stale = entry.Running && now - entry.LastHeartbeat > _heartbeatTimeout;
        var storm = entry.RestartTimes.Count >= _maxRestarts;
        var recovering = entry.LastFailure is { } failure && now - failure < _recoveryGrace;
        var alarm = !entry.Running || stale || storm || recovering;
        var reason = !entry.Running ? entry.LastError ?? "Component is stopped."
            : stale ? $"No heartbeat for {(now - entry.LastHeartbeat).TotalSeconds:0} seconds."
            : storm ? $"{entry.RestartTimes.Count} failures in the last {_restartWindow.TotalMinutes:0.#} minutes."
            : recovering ? $"Recovering from: {entry.LastError}"
            : null;
        return new ComponentHealth(entry.Name, entry.Running, entry.Restarts, entry.RestartTimes.Count,
            entry.LastHeartbeat, entry.LastFailure, entry.LastError, stale, storm, alarm, reason);
    }

    private void Prune(Entry entry, DateTimeOffset now)
    {
        var cutoff = now - _restartWindow;
        while (entry.RestartTimes.TryPeek(out var occurred) && occurred < cutoff)
            entry.RestartTimes.Dequeue();
    }
}

public sealed class GatewayLivenessCheck(DeviceEngine engine) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(engine.RequiresRestart
            ? HealthCheckResult.Unhealthy("A device worker ignored shutdown; process restart is required.")
            : HealthCheckResult.Healthy());
}

public sealed class GatewayReadinessCheck(DeviceEngine engine, ComponentHealthRegistry components) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (engine.RequiresRestart) return Task.FromResult(HealthCheckResult.Unhealthy("Process restart required."));
        var failed = components.Snapshot.Where(x => x.AlarmActive).Select(x => $"{x.Name}: {x.AlarmReason}").ToList();
        return Task.FromResult(failed.Count == 0
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy(string.Join("; ", failed)));
    }
}
