namespace HinoGateway.Core;

/// <summary>
/// Flattened tag reading for transport to clients (REST snapshot + SignalR push).
/// Carries value, quality, source timestamp, the instant the value last changed and the
/// status text so the UI can render accuracy honestly.
/// </summary>
public sealed record TagSample(
    string TagId,
    object? Value,
    Quality Quality,
    DateTimeOffset SourceTimestamp,
    DateTimeOffset LastChanged,
    string? StatusText);

/// <summary>Static tag metadata sent to clients so they can label and unit-format values.</summary>
public sealed record TagInfo(
    string Id,
    string Name,
    string DeviceId,
    DataType DataType,
    string? Unit,
    string? Description);
