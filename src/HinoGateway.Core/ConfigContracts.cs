namespace HinoGateway.Core;

/// <summary>
/// Persistence abstraction for the gateway's configuration. Connections (devices), tag folders
/// (a tree used to organise the tag database) and tags (each referencing a connection and an
/// optional folder) are managed independently. The concrete implementation (a single JSON project
/// file) lives in HinoGateway.Storage; keeping the interface here lets the engine/host depend only
/// on the abstraction.
/// </summary>
public interface IConfigStore
{
    /// <summary>
    /// Group many edits into one all-or-nothing change. The store keeps serving reads from memory
    /// throughout and persists once on <see cref="IConfigTransaction.Commit"/>; a scope disposed
    /// without committing restores the configuration exactly as it was and writes nothing.
    ///
    /// This is what makes a project import safe: a failure halfway through can no longer leave a
    /// partially replaced configuration behind — and it means one file write instead of one per tag.
    /// </summary>
    IConfigTransaction BeginTransaction();

    // --- Connections (devices) ------------------------------------------------
    // GetDevicesAsync assembles the runtime view: each DeviceConfig carries the tags that
    // reference it, with TagDefinition.Id set to the tag's full folder path (the runtime tag id).
    Task<IReadOnlyList<DeviceConfig>> GetDevicesAsync(CancellationToken ct = default);
    Task<DeviceConfig?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
    /// <summary>Persists connection fields only; tags are managed separately.</summary>
    Task UpsertDeviceAsync(DeviceConfig device, CancellationToken ct = default);
    Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);

    // --- Tag folders (tree) ---------------------------------------------------
    Task<IReadOnlyList<TagGroupDto>> GetGroupsAsync(CancellationToken ct = default);
    Task UpsertGroupAsync(TagGroupDto group, CancellationToken ct = default);
    /// <summary>Deletes a folder, all its sub-folders and every tag inside them.</summary>
    Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct = default);

    // --- Tags (flat list; folder membership via GroupId) ----------------------
    Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default);

    /// <summary>
    /// The memory and expression tags, as the engine needs them: <see cref="TagDefinition.Id"/> is
    /// the full folder path, exactly like the device tags carried by <see cref="GetDevicesAsync"/>.
    /// They are fetched separately because they belong to no connection — that is the whole point of
    /// them — and the engine runs them through a different producer.
    /// </summary>
    Task<IReadOnlyList<TagDefinition>> GetInternalTagsAsync(CancellationToken ct = default);

    Task UpsertTagAsync(TagDto tag, CancellationToken ct = default);
    Task<bool> DeleteTagAsync(string tagId, CancellationToken ct = default);
    /// <summary>Atomically clears devices, folders and tags; intended for project replacement.</summary>
    Task<int> ClearTopologyAsync(CancellationToken ct = default);

    // --- Northbound: IEC 60870-5-104 controlled station -----------------------
    /// <summary>Never null: an unconfigured gateway returns disabled defaults.</summary>
    Task<Iec104ServerOptions> GetIec104Async(CancellationToken ct = default);
    Task SaveIec104Async(Iec104ServerOptions options, CancellationToken ct = default);

    // --- Northbound: MQTT publisher ------------------------------------------
    Task<MqttPublisherOptions> GetMqttAsync(CancellationToken ct = default);
    Task SaveMqttAsync(MqttPublisherOptions options, CancellationToken ct = default);

    // --- Commands -------------------------------------------------------------
    /// <summary>
    /// Scripts that run when a tag is written. <see cref="CommandDto.TriggerTagPath"/> is filled in
    /// on read, the same way an IEC point's TagPath is: paths are derived, keys are stored.
    /// </summary>
    Task<IReadOnlyList<CommandDto>> GetCommandsAsync(CancellationToken ct = default);
    Task UpsertCommandAsync(CommandDto command, CancellationToken ct = default);
    Task<bool> DeleteCommandAsync(string commandId, CancellationToken ct = default);
}

/// <summary>
/// An open configuration change. Commit to persist everything in it as one write; dispose without
/// committing (including when an exception unwinds the scope) to discard every change in it.
/// </summary>
public interface IConfigTransaction : IDisposable
{
    void Commit();
}

/// <summary>
/// UI/REST transport for a connection (device). Deliberately mutable (get/set) so it can be
/// two-way bound in Blazor forms, and uses a friendly <see cref="ScanIntervalMs"/> instead of a
/// <see cref="TimeSpan"/>. Mapped to/from the domain <see cref="DeviceConfig"/> at the edge.
/// </summary>
public sealed class DeviceDto
{
    public string Id { get; set; } = "";
    public string Driver { get; set; } = "Simulator";
    public int ScanIntervalMs { get; set; } = 1000;

    /// <summary>See <see cref="DeviceConfig.FailureThreshold"/>. 1 = drop the link on the first failed poll.</summary>
    public int FailureThreshold { get; set; } = 3;

    public Dictionary<string, string> Settings { get; set; } = new();

    /// <summary>
    /// Read-only convenience on GET (tags referencing this connection) and the legacy carrier
    /// for version-1 <c>.hino</c> imports. Ignored when saving a connection.
    /// </summary>
    public List<TagDto> Tags { get; set; } = new();
}

/// <summary>A folder in the tag tree. <see cref="ParentId"/> null means it sits at the root.</summary>
public sealed class TagGroupDto
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public string Name { get; set; } = "";
}

public sealed class TagDto
{
    /// <summary>Stable storage key (GUID for new tags). NOT the runtime tag id — see <see cref="Path"/>.</summary>
    public string Id { get; set; } = "";

    /// <summary>Folder this tag lives in; null = root.</summary>
    public string? GroupId { get; set; }

    /// <summary>
    /// What produces this tag's value. <see cref="TagKind.Device"/> is 0, so a project file written
    /// before the other kinds existed loads exactly as it always did.
    /// </summary>
    public TagKind Kind { get; set; } = TagKind.Device;

    /// <summary>The connection (device) this tag reads from. Empty for a memory or expression tag.</summary>
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Full folder path + name (e.g. "Area 1/Line 2/Temp") — the runtime tag id used by the
    /// engine, SignalR and the historian. Computed server-side; read-only for clients.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>Driver address. Empty for a memory or expression tag, which has no device.</summary>
    public string Address { get; set; } = "";

    /// <summary>The formula of an expression tag, e.g. <c>({Plant/P1} + {Plant/P2}) * 0.001</c>.</summary>
    public string? Expression { get; set; }

    /// <summary>What a memory tag holds before anything is written to it. See <see cref="TagDefinition.InitialValue"/>.</summary>
    public string? InitialValue { get; set; }

    /// <summary>Keep a memory tag's value across a restart. Off by default — see <see cref="TagDefinition.Retentive"/>.</summary>
    public bool Retentive { get; set; }

    /// <summary>When an expression tag recomputes: on its scan interval, or when an input changes.</summary>
    public EvalMode EvalMode { get; set; } = EvalMode.Scan;

    /// <summary>
    /// An accumulator's source counter, by <b>storage key</b> — never by path. A path is a folder
    /// tree position, and one rename would silently disconnect the tag reporting yesterday's energy
    /// to the utility. See <see cref="TagDefinition.SourceTagKey"/>.
    /// </summary>
    public string? SourceTagKey { get; set; }

    /// <summary>The source's runtime path, computed server-side from <see cref="SourceTagKey"/>. Read-only.</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>Which calendar period an accumulator reports. See <see cref="AccumulationPeriod"/>.</summary>
    public AccumulationPeriod Period { get; set; } = AccumulationPeriod.Day;

    /// <summary>Engineering value at which the source counter wraps; 0 = never. See <see cref="TagDefinition.RolloverAt"/>.</summary>
    public double RolloverAt { get; set; }

    /// <summary>Spike guard on the source, in engineering units per hour; null = off.</summary>
    public double? MaxRatePerHour { get; set; }

    public DataType DataType { get; set; } = DataType.Float64;
    public string? Unit { get; set; }
    public string? Description { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
    public bool AllowWrite { get; set; }
    public double? WriteMin { get; set; }
    public double? WriteMax { get; set; }

    /// <summary>
    /// Cumulative counter: a read lower than the highest accepted so far is refused and the previous
    /// value kept. See <see cref="TagDefinition.Monotonic"/>.
    /// </summary>
    public bool Monotonic { get; set; }

    /// <summary>
    /// Per-tag scan rate in milliseconds; <b>0 means "follow the connection"</b>, which is the normal
    /// case. Slower than the connection's own interval saves bandwidth and PLC time on values that
    /// change rarely; faster is meaningless — the connection's interval is the clock everything
    /// ticks on. See <see cref="TagDefinition.ScanInterval"/>.
    /// </summary>
    public int ScanIntervalMs { get; set; }
}
