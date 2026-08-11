using System.Text.Json;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Storage;

/// <summary>
/// The gateway's whole configuration — connections, tag folders, tags and alarms — held in memory
/// and persisted as one JSON project file (the same <c>.hino</c> envelope used for export/import).
///
/// A gateway is installed on an industrial PC and must start, run and be recovered with nothing but
/// a copied folder, so there is deliberately <em>no database</em>: no engine to install, no native
/// libraries per architecture, no schema migrations to get wrong in the field, and a configuration
/// an engineer can read, diff and back up by hand.
///
/// Reads are served from memory (the engine and the UI poll them constantly); every mutation
/// rewrites the file atomically — temp file first, then an atomic replace that also leaves a
/// <c>.bak</c> of the previous good version, so a power cut mid-write cannot leave a half-written
/// configuration behind. Use <see cref="BeginTransaction"/> around bulk edits to write only once.
/// </summary>
public sealed class JsonProjectStore : IConfigStore, IAlarmStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private readonly ILogger<JsonProjectStore> _logger;
    private readonly object _gate = new();
    private readonly object _saveGate = new();

    private HinoProject _project = new();
    private readonly AsyncLocal<TransactionContext?> _ambientTransaction = new();
    private TransactionContext? _activeTransaction;

    public JsonProjectStore(string path, ILogger<JsonProjectStore> logger)
    {
        _path = System.IO.Path.GetFullPath(path);
        _logger = logger;
        WasMissingAtStartup = !File.Exists(_path);
        Load();
    }

    /// <summary>Absolute path of the project file backing this store.</summary>
    public string FilePath => _path;

    /// <summary>True only when no project file existed before this process started.</summary>
    public bool WasMissingAtStartup { get; }

    /// <summary>True when the file did not exist at startup (a fresh install to be seeded).</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return CurrentProject.Devices.Count == 0 && CurrentProject.Tags.Count == 0; }
    }

    // --- loading / saving ----------------------------------------------------

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No project file at {Path} — starting with an empty configuration", _path);
            return;
        }

        try
        {
            var project = JsonSerializer.Deserialize<HinoProject>(File.ReadAllText(_path), Json);
            if (project is null)
                throw new InvalidDataException("The project file is empty.");
            if (!string.Equals(project.Format, HinoProject.FormatTag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Not a HinoGateway project file (format = '{project.Format}').");
            if (project.Version > HinoProject.CurrentVersion)
                throw new InvalidDataException(
                    $"Project version {project.Version} is newer than this gateway supports ({HinoProject.CurrentVersion}).");

            Normalise(project);
            _project = project;
            _logger.LogInformation(
                "Loaded configuration from {Path}: {Devices} connection(s), {Groups} folder(s), {Tags} tag(s), {Alarms} alarm(s)",
                _path, project.Devices.Count, project.Groups.Count, project.Tags.Count, project.Alarms.Count);
        }
        catch (Exception ex)
        {
            // Refuse to run on a configuration we cannot understand: silently starting with an empty
            // one would take a live gateway off the air and then overwrite the operator's file.
            throw new InvalidOperationException(
                $"Could not load the project file '{_path}': {ex.Message}. Fix or remove the file (a previous version may be in '{_path}.bak').", ex);
        }
    }

    /// <summary>Version 1 files nested tags under their device; lift them into one folder per device.</summary>
    public static void Normalise(HinoProject project)
    {
        project.Devices ??= new();
        project.Groups ??= new();
        project.Tags ??= new();
        project.Alarms ??= new();

        if (project.Version < 2)
        {
            foreach (var device in project.Devices)
            {
                if (device.Tags.Count == 0)
                    continue;

                var group = new TagGroupDto { Id = $"imp-{device.Id}", Name = device.Id };
                project.Groups.Add(group);
                foreach (var tag in device.Tags)
                {
                    tag.Id = string.IsNullOrWhiteSpace(tag.Id) ? Guid.NewGuid().ToString("N") : tag.Id;
                    tag.GroupId = group.Id;
                    tag.DeviceId = device.Id;
                    project.Tags.Add(tag);
                }
                device.Tags.Clear();
            }
        }

        // Version 7 split "this point is commandable" into two explicit switches: select-before-operate
        // on the IEC point, and AllowWrite on the tag itself. Both halves have to be migrated together.
        // In a version 6 file, mapping a point as commandable WAS the authorisation — so carrying only
        // RequireSelect across left every such tag read-only, and the project validator then rejected
        // the whole file. That turned a routine restore from a .hino backup into a dead end, with an
        // error message about a safety switch rather than about the file's age.
        if (project.Version < 7 && project.Iec104 is not null)
        {
            var commandedTagIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var point in project.Iec104.Points.Where(p => p.Command != Iec104CommandType.None))
            {
                if (point.Command is Iec104CommandType.SingleCommand or Iec104CommandType.DoubleCommand)
                    point.RequireSelect = true;
                if (!string.IsNullOrEmpty(point.TagId))
                    commandedTagIds.Add(point.TagId);
            }
            foreach (var tag in project.Tags.Where(t => commandedTagIds.Contains(t.Id)))
                tag.AllowWrite = true;
        }

        project.Version = HinoProject.CurrentVersion;
    }

    public IConfigTransaction BeginTransaction()
    {
        lock (_gate)
        {
            if (_ambientTransaction.Value is not null)
                throw new InvalidOperationException("Nested configuration transactions are not supported.");
            if (_activeTransaction is not null)
                throw new InvalidOperationException("Another configuration transaction is already in progress.");

            var context = new TransactionContext(Clone(_project));
            _activeTransaction = context;
            _ambientTransaction.Value = context;
            return new TransactionScope(this, context);
        }
    }

    /// <summary>End the innermost scope. The outermost one decides: persist, or restore and discard.</summary>
    private void EndTransaction(TransactionContext context, bool committed)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeTransaction, context))
                throw new InvalidOperationException("The configuration transaction is no longer active.");

            if (committed)
                _project = context.Project;

            if (!committed)
            {
                if (_project is not null)
                {
                    // The committed project was never modified; abandoning the private snapshot is enough.
                    _logger.LogWarning("Configuration change was rolled back — nothing was written to {Path}", _path);
                }
                _activeTransaction = null;
            }
            else
            {
                _activeTransaction = null;
            }

            if (ReferenceEquals(_ambientTransaction.Value, context))
                _ambientTransaction.Value = null;
        }

        if (committed && context.Dirty)
            Save();
    }

    /// <summary>Persist now, unless a transaction is open — then the write happens on commit.</summary>
    private void SaveOrDefer()
    {
        lock (_gate)
        {
            if (_ambientTransaction.Value is { } transaction)
            {
                transaction.Dirty = true;
                return;
            }
            if (_activeTransaction is not null)
                throw new InvalidOperationException(
                    "Configuration is being changed by another request. Retry after that transaction completes.");
        }
        Save();
    }

    private void Save()
    {
        lock (_saveGate)
            SaveCore();
    }

    private void SaveCore()
    {
        string json;
        lock (_gate)
        {
            _project.Format = HinoProject.FormatTag;
            _project.Version = HinoProject.CurrentVersion;
            _project.ExportedAt = DateTimeOffset.UtcNow;
            json = JsonSerializer.Serialize(_project, Json);
        }

        var backup = _path + ".bak";
        try
        {
            DurableAtomicFile.WriteAllText(_path, json, backup);
        }
        catch (Exception ex)
        {
            // The in-memory configuration stays authoritative and the gateway keeps polling; the
            // request must still fail: returning success here tells the operator that a change is
            // durable when it will disappear at the next restart.
            _logger.LogError(ex, "Could not write the project file {Path} — the change is live but NOT persisted", _path);
            throw new ConfigurationPersistenceException(_path,
                $"Could not persist the gateway project to '{_path}'. The change is live in memory but will not survive a restart.", ex);
        }
    }

    private sealed class TransactionScope : IConfigTransaction
    {
        private readonly JsonProjectStore _store;
        private readonly TransactionContext _context;
        private bool _committed;
        private bool _closed;

        public TransactionScope(JsonProjectStore store, TransactionContext context)
        {
            _store = store;
            _context = context;
        }

        public void Commit()
        {
            if (_closed)
                throw new InvalidOperationException("This configuration transaction has already been closed.");
            _committed = true;
            _closed = true;
            _store.EndTransaction(_context, committed: true);
        }

        // Disposing without a commit — including when an exception unwinds the using block — rolls back.
        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            _store.EndTransaction(_context, _committed);
        }
    }

    private sealed class TransactionContext(HinoProject project)
    {
        public HinoProject Project { get; } = project;
        public bool Dirty { get; set; }
        public Dictionary<string, TagDto> TagsById { get; } =
            project.Tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    private HinoProject CurrentProject => _ambientTransaction.Value?.Project ?? _project;

    private HinoProject ProjectForMutation()
    {
        var ambient = _ambientTransaction.Value;
        if (_activeTransaction is not null && !ReferenceEquals(_activeTransaction, ambient))
            throw new InvalidOperationException(
                "Configuration is being changed by another request. Retry after that transaction completes.");
        return ambient?.Project ?? _project;
    }

    // --- connections ---------------------------------------------------------

    public Task<IReadOnlyList<DeviceConfig>> GetDevicesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var paths = TagPaths.BuildGroupPaths(project.Groups);
            var tagsByDevice = project.Tags
                .GroupBy(t => t.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IEnumerable<TagDto>)g, StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<DeviceConfig> devices = project.Devices
                .Select(d => ToConfig(d, tagsByDevice.GetValueOrDefault(d.Id) ?? [], paths))
                .ToList();
            return Task.FromResult(devices);
        }
    }

    public Task<DeviceConfig?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var device = project.Devices.FirstOrDefault(d => Same(d.Id, deviceId));
            if (device is null)
                return Task.FromResult<DeviceConfig?>(null);
            var paths = TagPaths.BuildGroupPaths(project.Groups);
            return Task.FromResult<DeviceConfig?>(
                ToConfig(device, project.Tags.Where(t => Same(t.DeviceId, deviceId)), paths));
        }
    }

    public Task UpsertDeviceAsync(DeviceConfig device, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = ProjectForMutation();
            var existing = project.Devices.FirstOrDefault(d => Same(d.Id, device.DeviceId));
            if (existing is null)
            {
                project.Devices.Add(new DeviceDto
                {
                    Id = device.DeviceId,
                    Driver = device.Driver,
                    ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds,
                    FailureThreshold = device.FailureThreshold,
                    Settings = new Dictionary<string, string>(device.Settings),
                });
            }
            else
            {
                existing.Driver = device.Driver;
                existing.ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds;
                existing.FailureThreshold = device.FailureThreshold;
                existing.Settings = new Dictionary<string, string>(device.Settings);
                existing.Tags = new List<TagDto>();   // tags live at the top level
            }
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        bool removed;
        lock (_gate)
            removed = ProjectForMutation().Devices.RemoveAll(d => Same(d.Id, deviceId)) > 0;
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- folders -------------------------------------------------------------

    public Task<IReadOnlyList<TagGroupDto>> GetGroupsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TagGroupDto> groups = CurrentProject.Groups.Select(Clone).ToList();
            return Task.FromResult(groups);
        }
    }

    public Task UpsertGroupAsync(TagGroupDto group, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = ProjectForMutation();
            var existing = project.Groups.FirstOrDefault(g => g.Id == group.Id);
            if (existing is null)
                project.Groups.Add(Clone(group));
            else
            {
                existing.ParentId = group.ParentId;
                existing.Name = group.Name;
            }
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteGroupAsync(string groupId, CancellationToken ct = default)
    {
        bool removed;
        lock (_gate)
        {
            var project = ProjectForMutation();
            if (project.Groups.All(g => g.Id != groupId))
                return Task.FromResult(false);

            // Take the whole subtree with it, tags included.
            var doomed = new HashSet<string> { groupId };
            var queue = new Queue<string>();
            queue.Enqueue(groupId);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                foreach (var child in project.Groups.Where(g => g.ParentId == parent))
                    if (doomed.Add(child.Id))
                        queue.Enqueue(child.Id);
            }

            project.Tags.RemoveAll(t => t.GroupId is not null && doomed.Contains(t.GroupId));
            project.Groups.RemoveAll(g => doomed.Contains(g.Id));
            removed = true;
        }
        SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- tags ----------------------------------------------------------------

    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var paths = TagPaths.BuildGroupPaths(project.Groups);
            IReadOnlyList<TagDto> tags = project.Tags
                .Select(t => { var copy = Clone(t); copy.Path = TagPaths.TagPath(paths, t.GroupId, t.Name); return copy; })
                .ToList();
            return Task.FromResult(tags);
        }
    }

    public Task<IReadOnlyList<TagDefinition>> GetInternalTagsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var paths = TagPaths.BuildGroupPaths(project.Groups);
            IReadOnlyList<TagDefinition> tags = project.Tags
                .Where(t => t.Kind.IsInternal())
                .Select(t => new TagDefinition
                {
                    Id = TagPaths.TagPath(paths, t.GroupId, t.Name),
                    Name = t.Name,
                    Kind = t.Kind,
                    Address = "",
                    Expression = t.Expression,
                    InitialValue = t.InitialValue,
                    Retentive = t.Retentive,
                    EvalMode = t.EvalMode,
                    DataType = t.DataType,
                    Unit = t.Unit,
                    Description = t.Description,
                    AllowWrite = t.AllowWrite,
                    WriteMin = t.WriteMin,
                    WriteMax = t.WriteMax,
                    // Scale/Offset are deliberately not carried: they are applied by the device
                    // worker on a raw device reading, and an internal tag has none. An expression
                    // does its own arithmetic; a memory tag holds what was written to it.
                    ScanInterval = TagScanRate.FromMilliseconds(t.ScanIntervalMs),
                })
                .ToList();
            return Task.FromResult(tags);
        }
    }

    public Task UpsertTagAsync(TagDto tag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = ProjectForMutation();
            var transaction = _ambientTransaction.Value;
            var existing = transaction is not null
                ? transaction.TagsById.GetValueOrDefault(tag.Id)
                : project.Tags.FirstOrDefault(t => t.Id == tag.Id);
            if (existing is null)
            {
                var copy = Clone(tag);
                copy.Id = string.IsNullOrWhiteSpace(tag.Id) ? Guid.NewGuid().ToString("N") : tag.Id.Trim();
                copy.Path = "";   // derived on read, never stored
                project.Tags.Add(copy);
                transaction?.TagsById.TryAdd(copy.Id, copy);
            }
            else
            {
                existing.GroupId = tag.GroupId;
                existing.DeviceId = tag.DeviceId;
                existing.Kind = tag.Kind;
                existing.Name = tag.Name;
                existing.Address = tag.Address;
                existing.Expression = tag.Expression;
                existing.InitialValue = tag.InitialValue;
                existing.Retentive = tag.Retentive;
                existing.EvalMode = tag.EvalMode;
                existing.DataType = tag.DataType;
                existing.Unit = tag.Unit;
                existing.Description = tag.Description;
                existing.Scale = tag.Scale;
                existing.Offset = tag.Offset;
                existing.Monotonic = tag.Monotonic;
                existing.ScanIntervalMs = tag.ScanIntervalMs;
                existing.Path = "";
            }
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteTagAsync(string tagId, CancellationToken ct = default)
    {
        bool removed;
        lock (_gate)
        {
            removed = ProjectForMutation().Tags.RemoveAll(t => t.Id == tagId) > 0;
            if (removed)
                _ambientTransaction.Value?.TagsById.Remove(tagId);
        }
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    public Task<int> ClearTopologyAsync(CancellationToken ct = default)
    {
        int removed;
        lock (_gate)
        {
            var project = ProjectForMutation();
            removed = project.Devices.Count + project.Groups.Count + project.Tags.Count;
            project.Devices.Clear();
            project.Groups.Clear();
            project.Tags.Clear();
            _ambientTransaction.Value?.TagsById.Clear();
        }
        if (removed > 0)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- alarms --------------------------------------------------------------

    public Task<IReadOnlyList<AlarmDto>> GetAlarmsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var paths = TagPaths.BuildGroupPaths(project.Groups);
            var tagPaths = project.Tags.ToDictionary(t => t.Id, t => TagPaths.TagPath(paths, t.GroupId, t.Name));
            IReadOnlyList<AlarmDto> alarms = project.Alarms
                .Select(a => { var copy = Clone(a); copy.TagPath = tagPaths.GetValueOrDefault(a.TagId, ""); return copy; })
                .ToList();
            return Task.FromResult(alarms);
        }
    }

    public Task UpsertAlarmAsync(AlarmDto alarm, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = ProjectForMutation();
            var existing = project.Alarms.FirstOrDefault(a => a.Id == alarm.Id);
            if (existing is null)
            {
                var copy = Clone(alarm);
                copy.Id = string.IsNullOrWhiteSpace(alarm.Id) ? Guid.NewGuid().ToString("N") : alarm.Id.Trim();
                copy.TagPath = "";   // derived on read
                project.Alarms.Add(copy);
            }
            else
            {
                existing.Name = alarm.Name;
                existing.TagId = alarm.TagId;
                existing.Condition = alarm.Condition;
                existing.Limit = alarm.Limit;
                existing.Hysteresis = alarm.Hysteresis;
                existing.OnDelayMs = alarm.OnDelayMs;
                existing.OffDelayMs = alarm.OffDelayMs;
                existing.Severity = alarm.Severity;
                existing.Message = alarm.Message;
                existing.Enabled = alarm.Enabled;
                existing.RequiresAck = alarm.RequiresAck;
                existing.TriggerOnTrue = alarm.TriggerOnTrue;
                existing.TagPath = "";
            }
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAlarmAsync(string alarmId, CancellationToken ct = default)
    {
        bool removed;
        lock (_gate)
            removed = ProjectForMutation().Alarms.RemoveAll(a => a.Id == alarmId) > 0;
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- northbound: IEC-104 -------------------------------------------------

    public Task<Iec104ServerOptions> GetIec104Async(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var stored = project.Iec104;
            if (stored is null)
                return Task.FromResult(new Iec104ServerOptions());

            var paths = TagPaths.BuildGroupPaths(project.Groups);
            var tagPaths = project.Tags.ToDictionary(t => t.Id, t => TagPaths.TagPath(paths, t.GroupId, t.Name));

            var copy = Clone(stored);
            foreach (var point in copy.Points)
                point.TagPath = tagPaths.GetValueOrDefault(point.TagId, "");
            return Task.FromResult(copy);
        }
    }

    public Task SaveIec104Async(Iec104ServerOptions options, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = ProjectForMutation();
            var copy = Clone(options);
            foreach (var point in copy.Points)
                point.TagPath = "";   // derived on read, never stored
            project.Iec104 = copy;
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    // --- northbound: MQTT ----------------------------------------------------

    public Task<MqttPublisherOptions> GetMqttAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(CurrentProject.Mqtt is { } stored
                ? Clone(stored)
                : new MqttPublisherOptions());
    }

    public Task SaveMqttAsync(MqttPublisherOptions options, CancellationToken ct = default)
    {
        lock (_gate)
            ProjectForMutation().Mqtt = Clone(options);
        SaveOrDefer();
        return Task.CompletedTask;
    }

    // --- helpers -------------------------------------------------------------

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Deep copy through JSON — the options tree is small and this cannot miss a new field.</summary>
    private static Iec104ServerOptions Clone(Iec104ServerOptions options) =>
        JsonSerializer.Deserialize<Iec104ServerOptions>(JsonSerializer.Serialize(options, Json), Json)!;

    private static HinoProject Clone(HinoProject project) =>
        JsonSerializer.Deserialize<HinoProject>(JsonSerializer.Serialize(project, Json), Json)!;

    private static MqttPublisherOptions Clone(MqttPublisherOptions options) =>
        JsonSerializer.Deserialize<MqttPublisherOptions>(JsonSerializer.Serialize(options, Json), Json)!;

    private static DeviceConfig ToConfig(DeviceDto d, IEnumerable<TagDto> tags,
        IReadOnlyDictionary<string, string> groupPaths) => new()
    {
        DeviceId = d.Id,
        Driver = d.Driver,
        ScanInterval = TimeSpan.FromMilliseconds(d.ScanIntervalMs <= 0 ? 1000 : d.ScanIntervalMs),
        // A project file written before this setting existed deserialises to 0; keep those devices
        // on the shipped default rather than on a threshold that would fault them on the first blip.
        FailureThreshold = d.FailureThreshold <= 0 ? 3 : d.FailureThreshold,
        Settings = new Dictionary<string, string>(d.Settings),
        // Only device tags belong to a connection. Without this filter a memory tag that still
        // carries the connection it was converted from would be handed to the driver, which would
        // dutifully try to read an empty address off a PLC.
        Tags = tags.Where(t => t.Kind == TagKind.Device).Select(t => new TagDefinition
        {
            Id = TagPaths.TagPath(groupPaths, t.GroupId, t.Name),
            Name = t.Name,
            Address = t.Address,
            DataType = t.DataType,
            Unit = t.Unit,
            Description = t.Description,
            Scale = t.Scale,
            Offset = t.Offset,
            AllowWrite = t.AllowWrite,
            WriteMin = t.WriteMin,
            WriteMax = t.WriteMax,
            Monotonic = t.Monotonic,
            ScanInterval = TagScanRate.FromMilliseconds(t.ScanIntervalMs),
        }).ToList(),
    };

    // Callers get copies: the in-memory project is the single source of truth and must not be
    // mutated behind the store's back (which would also skip persistence).
    private static TagGroupDto Clone(TagGroupDto g) => new() { Id = g.Id, ParentId = g.ParentId, Name = g.Name };

    private static TagDto Clone(TagDto t) => new()
    {
        Id = t.Id,
        GroupId = t.GroupId,
        DeviceId = t.DeviceId,
        Kind = t.Kind,
        Name = t.Name,
        Path = t.Path,
        Address = t.Address,
        Expression = t.Expression,
        InitialValue = t.InitialValue,
        Retentive = t.Retentive,
        EvalMode = t.EvalMode,
        DataType = t.DataType,
        Unit = t.Unit,
        Description = t.Description,
        Scale = t.Scale,
        Offset = t.Offset,
        AllowWrite = t.AllowWrite,
        WriteMin = t.WriteMin,
        WriteMax = t.WriteMax,
        Monotonic = t.Monotonic,
        ScanIntervalMs = t.ScanIntervalMs,
    };

    private static AlarmDto Clone(AlarmDto a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        TagId = a.TagId,
        TagPath = a.TagPath,
        Condition = a.Condition,
        Limit = a.Limit,
        Hysteresis = a.Hysteresis,
        OnDelayMs = a.OnDelayMs,
        OffDelayMs = a.OffDelayMs,
        Severity = a.Severity,
        Message = a.Message,
        Enabled = a.Enabled,
        RequiresAck = a.RequiresAck,
        TriggerOnTrue = a.TriggerOnTrue,
    };
}
