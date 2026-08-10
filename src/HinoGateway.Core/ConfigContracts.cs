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
    Task UpsertTagAsync(TagDto tag, CancellationToken ct = default);
    Task<bool> DeleteTagAsync(string tagId, CancellationToken ct = default);

    // --- Northbound: IEC 60870-5-104 controlled station -----------------------
    /// <summary>Never null: an unconfigured gateway returns disabled defaults.</summary>
    Task<Iec104ServerOptions> GetIec104Async(CancellationToken ct = default);
    Task SaveIec104Async(Iec104ServerOptions options, CancellationToken ct = default);
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

    /// <summary>The connection (device) this tag reads from.</summary>
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>
    /// Full folder path + name (e.g. "Area 1/Line 2/Temp") — the runtime tag id used by the
    /// engine, SignalR and the historian. Computed server-side; read-only for clients.
    /// </summary>
    public string Path { get; set; } = "";

    public string Address { get; set; } = "";
    public DataType DataType { get; set; } = DataType.Float64;
    public string? Unit { get; set; }
    public string? Description { get; set; }
    public double Scale { get; set; } = 1.0;
    public double Offset { get; set; } = 0.0;
}
