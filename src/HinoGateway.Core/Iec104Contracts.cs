namespace HinoGateway.Core;

/// <summary>
/// How a tag is reported to the controlling station — i.e. which IEC 60870-5-101/104 ASDU type
/// carries it. The utility's signal list dictates this per point, so it is per-tag configuration
/// rather than something the gateway may choose.
/// </summary>
public enum Iec104PointType
{
    /// <summary>M_SP_NA_1 — single-point status (one bit).</summary>
    SinglePoint = 0,

    /// <summary>M_DP_NA_1 — double-point status (off/on plus the two indeterminate states).</summary>
    DoublePoint = 1,

    /// <summary>M_ME_NA_1 — normalised measurand, a fraction of full scale (-1 … +1).</summary>
    Normalized = 2,

    /// <summary>M_ME_NB_1 — scaled measurand, a 16-bit integer.</summary>
    Scaled = 3,

    /// <summary>M_ME_NC_1 — short floating point. The usual choice for analogue values.</summary>
    ShortFloat = 4,

    /// <summary>M_IT_NA_1 — integrated totals (energy counters).</summary>
    IntegratedTotals = 5,
}

/// <summary>What the controlling station is allowed to send to a point, if anything.</summary>
public enum Iec104CommandType
{
    None = 0,

    /// <summary>C_SC_NA_1 — single command (on/off).</summary>
    SingleCommand = 1,

    /// <summary>C_DC_NA_1 — double command (off/on, e.g. a breaker).</summary>
    DoubleCommand = 2,

    /// <summary>C_SE_NA_1 — normalised setpoint.</summary>
    SetpointNormalized = 3,

    /// <summary>C_SE_NB_1 — scaled setpoint.</summary>
    SetpointScaled = 4,

    /// <summary>C_SE_NC_1 — short-float setpoint. Used for active-power curtailment orders.</summary>
    SetpointShort = 5,
}

/// <summary>
/// One line of the signal list: which tag appears at which information object address, in which
/// ASDU type, and whether the controlling station may command it.
/// Mutable so it can be two-way bound in the configuration UI.
/// </summary>
public sealed class Iec104PointDto
{
    /// <summary>Storage key of the tag (as in <see cref="TagDto.Id"/>) — survives renames.</summary>
    public string TagId { get; set; } = "";

    /// <summary>Runtime tag path, computed server-side. Read-only for clients.</summary>
    public string TagPath { get; set; } = "";

    /// <summary>Information object address. Unique within the gateway's common address.</summary>
    public int Ioa { get; set; }

    public Iec104PointType Type { get; set; } = Iec104PointType.ShortFloat;

    /// <summary>
    /// Report-by-exception threshold in engineering units: a measurand is only sent again once it has
    /// moved by at least this much. 0 sends every change. Status points ignore it.
    /// </summary>
    public double Deadband { get; set; }

    /// <summary>Include a CP56Time2a timestamp (the utility usually asks for it).</summary>
    public bool WithTimestamp { get; set; } = true;

    public Iec104CommandType Command { get; set; } = Iec104CommandType.None;
    public bool RequireSelect { get; set; }

    /// <summary>
    /// For scaled/normalised types: engineering value = raw × this. Lets a 0–100 % measurand be
    /// carried as the 0–10000 integer the utility asked for without changing the tag itself.
    /// </summary>
    public double ReportScale { get; set; } = 1.0;
}

/// <summary>
/// The gateway acting as an IEC 60870-5-104 <em>controlled station</em>: it listens, and the
/// utility's control centre connects in, interrogates, receives spontaneous data and issues
/// commands. Stored in the project file alongside connections and tags.
/// </summary>
public sealed class Iec104ServerOptions
{
    /// <summary>
    /// Off unless deliberately switched on. The link only exists when a utility control centre is
    /// actually connected, and a gateway should not open a listening port that nobody asked for.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Interface to listen on. "0.0.0.0" = every interface.</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port. 2404 is the registered IEC-104 port.</summary>
    public int Port { get; set; } = 2404;

    /// <summary>Common address of ASDU (the "station address") the utility assigned to this gateway.</summary>
    public int CommonAddress { get; set; } = 1;

    /// <summary>How many control centres may be connected at once (redundant links are normal).</summary>
    public int MaxConnections { get; set; } = 4;
    /// <summary>
    /// Permitted control-centre IPs/CIDRs. Loopback-only by default; use 0.0.0.0/0 and ::/0 only
    /// when a firewall provides the source restriction. An empty list denies every connection.
    /// </summary>
    public List<string> AllowedClients { get; set; } = ["127.0.0.1/32", "::1/128"];
    public int OutboundQueueCapacity { get; set; } = 4096;
    public int MaxSpontaneousPerSecond { get; set; } = 500;

    /// <summary>When false, every command is answered with a negative confirmation.</summary>
    public bool AllowCommands { get; set; } = true;

    /// <summary>
    /// How many commands may be waiting to reach their devices at once. A control centre can issue
    /// setpoints faster than a serial link can carry them; beyond this the gateway refuses rather
    /// than queueing work it will never get through, because a command executed minutes late is
    /// worse than one that was visibly rejected.
    ///
    /// <para>Reconstructed 2026-08-10; the default is the value in the surviving project export.</para>
    /// </summary>
    public int CommandQueueCapacity { get; set; } = 256;

    /// <summary>
    /// How long one command may take on the wire before the control centre is told it failed. Must
    /// be shorter than the centre's own patience, or it will re-send a command that is still running.
    /// </summary>
    public int CommandTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Accept C_CS_NA_1 clock synchronisation. The offset is recorded and reported; the gateway does
    /// not set the machine clock (that belongs to the operating system, and silently jumping a
    /// gateway's clock would corrupt every timestamp it has already produced).
    /// </summary>
    public bool AllowClockSync { get; set; } = true;

    /// <summary>
    /// CP56Time2a in UTC instead of the station's local time. IEC-104 leaves this to agreement with
    /// the control centre, so it has to be switchable.
    /// </summary>
    public bool TimestampsInUtc { get; set; }

    /// <summary>
    /// Also send every mapped point on this cycle (seconds), regardless of change. 0 = report by
    /// exception only, which is what most control centres expect.
    /// </summary>
    public int PeriodicIntervalSeconds { get; set; }

    // --- APCI parameters (IEC 60870-5-104 clause 5) ---------------------------
    /// <summary>Max unacknowledged APDUs in transit.</summary>
    public int K { get; set; } = 12;

    /// <summary>Acknowledge at the latest after this many received APDUs.</summary>
    public int W { get; set; } = 8;

    /// <summary>t0 — connection establishment timeout (s).</summary>
    public int T0 { get; set; } = 30;

    /// <summary>t1 — send/test APDU acknowledgement timeout (s).</summary>
    public int T1 { get; set; } = 15;

    /// <summary>t2 — acknowledgement timeout for received APDUs (s), must be less than t1.</summary>
    public int T2 { get; set; } = 10;

    /// <summary>t3 — idle period before sending a TESTFR (s).</summary>
    public int T3 { get; set; } = 20;

    public List<Iec104PointDto> Points { get; set; } = new();
}

/// <summary>Live state of the controlled station, for the operator page and troubleshooting.</summary>
public sealed record Iec104Status(
    bool Enabled,
    bool Running,
    string Endpoint,
    int CommonAddress,
    int MappedPoints,
    int OpenConnections,
    // Control centres currently connected, counted per source address.
    IReadOnlyList<Iec104ConnectionInfo> Connections,
    long AsdusSent,
    long AsdusReceived,
    // When traffic last crossed the link in each direction — the practical answer to
    // "is data actually flowing?", and more trustworthy than any per-connection state flag.
    DateTimeOffset? LastSentAt,
    DateTimeOffset? LastReceivedAt,
    long CommandsAccepted,
    long CommandsRejected,
    long SpontaneousSent,
    long InterrogationsServed,
    // Batches of tag updates dropped because the link could not keep up (should stay 0).
    long DroppedUpdates,
    // Offset the control centre last tried to set, if any: their time minus ours.
    double? LastClockOffsetSeconds,
    string? LastError,
    // Something is wrong with the environment even though the station started — most importantly,
    // another program already serving this port.
    string? Warning)
{
    /// <summary>Commands accepted by ACT_CON and still waiting for the device dispatcher.</summary>
    public int PendingCommands { get; init; }

    /// <summary>Configured upper bound for pending commands.</summary>
    public int CommandCapacity { get; init; }

    /// <summary>Commands terminated negatively because their end-to-end deadline expired.</summary>
    public long CommandTimeouts { get; init; }
    public int PendingOutbound { get; init; }
    public long CoalescedUpdates { get; init; }
    /// <summary>Points rejected because a gateway/lib60870 queue was at capacity or unrepresentable.</summary>
    public long CapacityDrops { get; init; }
    public long LoopRestarts { get; init; }
}

/// <summary>
/// A control centre currently holding one or more connections open.
///
/// There is deliberately no "data transfer started" flag: lib60870.NET 2.3.0 never raises its OPENED
/// or ACTIVE connection events (verified against the library — only CLOSED arrives), so any such flag
/// would be invented rather than observed. Whether the link is genuinely working is answered by
/// <see cref="Iec104Status.LastReceivedAt"/> / <see cref="Iec104Status.LastSentAt"/>.
/// </summary>
public sealed record Iec104ConnectionInfo(string RemoteAddress, int Connections, DateTimeOffset FirstSeenAt);
