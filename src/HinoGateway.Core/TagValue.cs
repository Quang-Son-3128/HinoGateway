namespace HinoGateway.Core;

/// <summary>
/// An immutable, self-describing tag reading: the value together with its quality and the
/// instant it was sampled <em>at the device</em>. This is the atomic unit that flows through
/// the whole gateway (engine → store → SignalR → UI). Modelled after the OPC UA DataValue so
/// that accuracy (quality + source timestamp) is never lost between layers.
/// </summary>
public sealed record TagValue
{
    /// <summary>The engineering value. <c>null</c> when <see cref="Quality"/> is <see cref="Quality.Bad"/>.</summary>
    public object? Value { get; init; }

    public Quality Quality { get; init; }

    /// <summary>When the value was sampled at the device — NOT when the server received it.</summary>
    public DateTimeOffset SourceTimestamp { get; init; }

    /// <summary>Optional human-readable reason, mainly for Bad/Uncertain quality (e.g. "timeout").</summary>
    public string? StatusText { get; init; }

    /// <summary>
    /// When the value (or quality) last actually changed. Maintained by the TagStore: a fresh
    /// sample that carries the same value/quality as the previous one keeps the previous
    /// LastChanged, so consumers can show "value has been 42 since 09:13:05" honestly.
    /// </summary>
    public DateTimeOffset LastChanged { get; init; }

    public static TagValue Good(object value, DateTimeOffset sourceTimestamp) => new()
    {
        Value = value,
        Quality = Quality.Good,
        SourceTimestamp = sourceTimestamp,
        LastChanged = sourceTimestamp,
    };

    public static TagValue Bad(string reason, DateTimeOffset sourceTimestamp) => new()
    {
        Value = null,
        Quality = Quality.Bad,
        SourceTimestamp = sourceTimestamp,
        LastChanged = sourceTimestamp,
        StatusText = reason,
    };
}
