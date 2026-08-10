namespace HinoGateway.Core;

/// <summary>
/// What makes an alarm go active. Level conditions compare the tag's engineering value against
/// <see cref="AlarmDefinition.Limit"/>; <see cref="Digital"/> watches a boolean/on-off point and
/// <see cref="BadQuality"/> watches the data quality itself (comms loss, decode error).
/// HiHi/LoLo are separate conditions rather than a severity so a tag can carry the classic
/// four-limit set (LoLo/Lo/Hi/HiHi) as four independent alarms.
/// </summary>
public enum AlarmCondition
{
    Hi = 0,
    HiHi = 1,
    Lo = 2,
    LoLo = 3,
    Digital = 4,
    BadQuality = 5,
}

/// <summary>Operator-facing priority, ordered so higher is more urgent.</summary>
public enum AlarmSeverity
{
    Info = 0,
    Warning = 1,
    Minor = 2,
    Major = 3,
    Critical = 4,
}

/// <summary>
/// The classic SCADA alarm lifecycle (a simplification of ISA-18.2). An alarm that requires
/// acknowledgement stays visible after the condition clears until an operator acknowledges it,
/// so a transient trip can never go unnoticed.
/// </summary>
public enum AlarmStatus
{
    /// <summary>Condition not met and nothing outstanding — the alarm is not in the active list.</summary>
    Normal = 0,

    /// <summary>Condition met, not yet acknowledged (the state that demands attention).</summary>
    Active = 1,

    /// <summary>Condition still met, but an operator has acknowledged it.</summary>
    ActiveAcknowledged = 2,

    /// <summary>Condition went away while still unacknowledged — kept in the list until acked.</summary>
    ClearedUnacknowledged = 3,
}

public enum AlarmEventType
{
    Activated = 0,
    Cleared = 1,
    Acknowledged = 2,
}

/// <summary>
/// Domain definition of one alarm, as the engine evaluates it. Immutable: the engine swaps a whole
/// definition set atomically on reload.
/// </summary>
public sealed record AlarmDefinition
{
    /// <summary>Stable id (GUID). Route-safe, unlike tag ids.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The <em>runtime</em> tag id — i.e. the tag's full folder path, the same key the engine and
    /// SignalR use. Resolved from the stored tag key when definitions are loaded, so renaming a
    /// folder or tag keeps the alarm attached.
    /// </summary>
    public required string TagId { get; init; }

    public AlarmCondition Condition { get; init; } = AlarmCondition.Hi;

    /// <summary>Threshold for the level conditions (ignored by Digital/BadQuality).</summary>
    public double Limit { get; init; }

    /// <summary>
    /// Clearing deadband in engineering units: a Hi alarm only clears below <c>Limit - Hysteresis</c>
    /// (Lo: above <c>Limit + Hysteresis</c>), so a value sitting on the limit cannot chatter.
    /// </summary>
    public double Hysteresis { get; init; }

    /// <summary>The condition must hold this long before the alarm activates (0 = immediately).</summary>
    public int OnDelayMs { get; init; }

    /// <summary>The condition must be gone this long before the alarm clears (0 = immediately).</summary>
    public int OffDelayMs { get; init; }

    public AlarmSeverity Severity { get; init; } = AlarmSeverity.Warning;

    /// <summary>Operator message; falls back to a generated description when empty.</summary>
    public string? Message { get; init; }

    /// <summary>Disabled alarms are kept in the configuration but never evaluated.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When true the alarm latches: it stays in the list as <see cref="AlarmStatus.ClearedUnacknowledged"/>
    /// after the condition goes away, until acknowledged.
    /// </summary>
    public bool RequiresAck { get; init; } = true;

    /// <summary>Digital only: activate when the point is true (default) or when it is false.</summary>
    public bool TriggerOnTrue { get; init; } = true;
}

/// <summary>
/// UI/REST transport for an alarm. Deliberately mutable so it can be two-way bound in Blazor forms.
/// <see cref="TagId"/> here is the tag's <em>storage</em> key (as in <see cref="TagDto.Id"/>), not
/// the runtime path — that is what makes alarms survive renames; <see cref="TagPath"/> is the
/// server-computed runtime path for display.
/// </summary>
public sealed class AlarmDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Storage key of the tag this alarm watches.</summary>
    public string TagId { get; set; } = "";

    /// <summary>Runtime tag path ("Area 1/Line 2/Temp"). Computed server-side; read-only for clients.</summary>
    public string TagPath { get; set; } = "";

    public AlarmCondition Condition { get; set; } = AlarmCondition.Hi;
    public double Limit { get; set; }
    public double Hysteresis { get; set; }
    public int OnDelayMs { get; set; }
    public int OffDelayMs { get; set; }
    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;
    public string? Message { get; set; }
    public bool Enabled { get; set; } = true;
    public bool RequiresAck { get; set; } = true;
    public bool TriggerOnTrue { get; set; } = true;
}

/// <summary>Live state of one alarm, as pushed to operator screens.</summary>
public sealed record AlarmState(
    string AlarmId,
    string Name,
    string TagId,
    AlarmCondition Condition,
    AlarmSeverity Severity,
    AlarmStatus Status,
    double Limit,
    string? Message,
    // When the condition last activated (null if it never has), and when it last went away.
    DateTimeOffset? ActiveSince,
    DateTimeOffset? ClearedAt,
    DateTimeOffset? AckedAt,
    string? AckedBy,
    // The tag value that tripped the alarm, or the latest evaluated value.
    object? Value,
    // How many times this alarm has activated since the server started.
    long ActivationCount);

/// <summary>One line in the alarm journal. <see cref="Id"/> is assigned by the store.</summary>
public sealed record AlarmEvent(
    long Id,
    DateTimeOffset Timestamp,
    string AlarmId,
    string Name,
    string TagId,
    AlarmCondition Condition,
    AlarmSeverity Severity,
    AlarmEventType Type,
    double? Value,
    string? Message,
    // Operator who acknowledged (Acknowledged events only).
    string? User);

public sealed record SeverityCount(AlarmSeverity Severity, int Count);

/// <summary>Compact counts for the status bar / KPI tiles.</summary>
public sealed record AlarmSummary(
    int Configured,
    int Active,
    int Unacknowledged,
    AlarmSeverity? HighestActiveSeverity,
    IReadOnlyList<SeverityCount> ActiveBySeverity);

/// <summary>
/// Alarm <em>configuration</em>. Part of the gateway's project file, alongside connections and tags.
/// </summary>
public interface IAlarmStore
{
    Task<IReadOnlyList<AlarmDto>> GetAlarmsAsync(CancellationToken ct = default);
    Task UpsertAlarmAsync(AlarmDto alarm, CancellationToken ct = default);
    Task<bool> DeleteAlarmAsync(string alarmId, CancellationToken ct = default);
}

/// <summary>
/// The alarm journal — what happened, in order. This is a <em>gateway</em>: the durable record of
/// alarm history belongs to the upstream server, so the journal here is a short local trail (for
/// the on-box UI and, later, for forwarding northbound), not a database.
/// </summary>
public interface IAlarmJournal
{
    /// <summary>Append journal lines (called in batches by the alarm background service).</summary>
    Task AppendAsync(IReadOnlyList<AlarmEvent> events, CancellationToken ct = default);

    /// <summary>Journal query, newest first.</summary>
    Task<IReadOnlyList<AlarmEvent>> GetAsync(DateTimeOffset? from, DateTimeOffset? to,
        string? alarmId, int limit, CancellationToken ct = default);

    /// <summary>How many lines the trail currently holds, and how many were dropped to stay bounded.</summary>
    (int Count, long Dropped) Stats { get; }
}
