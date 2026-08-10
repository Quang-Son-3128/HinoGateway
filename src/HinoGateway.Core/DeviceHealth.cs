namespace HinoGateway.Core;

public enum DeviceState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Faulted = 3,
}

/// <summary>
/// Engine-owned health/diagnostics for a single device. The engine (not the driver) is the
/// source of truth here, so that a driver crashing or hanging still produces an accurate,
/// observable status.
/// </summary>
public sealed record DeviceHealth
{
    public required string DeviceId { get; init; }
    public DeviceState State { get; init; } = DeviceState.Disconnected;
    public string? LastError { get; init; }
    public DateTimeOffset? LastSuccess { get; init; }

    /// <summary>Duration of the most recent successful poll — a basic performance signal.</summary>
    public TimeSpan? LastPollLatency { get; init; }

    public long PollCount { get; init; }
    public long ErrorCount { get; init; }
}
