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
/// configuration behind. Use <see cref="BeginBatch"/> around bulk edits to write only once.
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

    private HinoProject _project = new();
    private HinoProject? _rollbackTo;
    private int _batchDepth;
    private bool _pendingWrite;
    private bool _transactionFailed;

    public JsonProjectStore(string path, ILogger<JsonProjectStore> logger)
    {
        _path = System.IO.Path.GetFullPath(path);
        _logger = logger;
        Load();
    }

    /// <summary>Absolute path of the project file backing this store.</summary>
    public string FilePath => _path;

    /// <summary>True when the file did not exist at startup (a fresh install to be seeded).</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return _project.Devices.Count == 0 && _project.Tags.Count == 0; }
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
    private static void Normalise(HinoProject project)
    {
        if (project.Version >= 2)
            return;

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
        project.Version = HinoProject.CurrentVersion;
    }

    public IConfigTransaction BeginTransaction()
    {
        lock (_gate)
        {
            if (_batchDepth++ == 0)
            {
                // Keep a pristine copy so an abandoned transaction can put everything back.
                _rollbackTo = JsonSerializer.Deserialize<HinoProject>(JsonSerializer.Serialize(_project, Json), Json);
                _transactionFailed = false;
            }
        }
        return new TransactionScope(this);
    }

    /// <summary>End the innermost scope. The outermost one decides: persist, or restore and discard.</summary>
    private void EndTransaction(bool committed)
    {
        var save = false;
        lock (_gate)
        {
            if (!committed)
                _transactionFailed = true;

            if (--_batchDepth > 0)
                return;   // an inner scope closed; the outermost one still decides

            if (_transactionFailed)
            {
                if (_rollbackTo is not null)
                {
                    _project = _rollbackTo;
                    _logger.LogWarning("Configuration change was rolled back — nothing was written to {Path}", _path);
                }
                _pendingWrite = false;
            }
            else
            {
                save = _pendingWrite;
            }

            _rollbackTo = null;
        }

        if (save)
            Save();
    }

    /// <summary>Persist now, unless a transaction is open — then the write happens on commit.</summary>
    private void SaveOrDefer()
    {
        lock (_gate)
        {
            if (_batchDepth > 0)
            {
                _pendingWrite = true;
                return;
            }
        }
        Save();
    }

    private void Save()
    {
        string json;
        lock (_gate)
        {
            _project.Format = HinoProject.FormatTag;
            _project.Version = HinoProject.CurrentVersion;
            _project.ExportedAt = DateTimeOffset.UtcNow;
            json = JsonSerializer.Serialize(_project, Json);
            _pendingWrite = false;
        }

        var temp = _path + ".tmp";
        var backup = _path + ".bak";
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(temp, json);
            if (File.Exists(_path))
                File.Replace(temp, _path, backup, ignoreMetadataErrors: true);  // atomic, keeps a backup
            else
                File.Move(temp, _path);
        }
        catch (Exception ex)
        {
            // The in-memory configuration stays authoritative and the gateway keeps polling; the
            // operator needs to know the change will not survive a restart.
            _logger.LogError(ex, "Could not write the project file {Path} — the change is live but NOT persisted", _path);
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private sealed class TransactionScope : IConfigTransaction
    {
        private readonly JsonProjectStore _store;
        private bool _committed;
        private bool _closed;

        public TransactionScope(JsonProjectStore store) => _store = store;

        public void Commit()
        {
            if (_closed)
                throw new InvalidOperationException("This configuration transaction has already been closed.");
            _committed = true;
            _closed = true;
            _store.EndTransaction(committed: true);
        }

        // Disposing without a commit — including when an exception unwinds the using block — rolls back.
        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            _store.EndTransaction(_committed);
        }
    }

    // --- connections ---------------------------------------------------------

    public Task<IReadOnlyList<DeviceConfig>> GetDevicesAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var paths = TagPaths.BuildGroupPaths(_project.Groups);
            IReadOnlyList<DeviceConfig> devices = _project.Devices
                .Select(d => ToConfig(d, _project.Tags.Where(t => Same(t.DeviceId, d.Id)), paths))
                .ToList();
            return Task.FromResult(devices);
        }
    }

    public Task<DeviceConfig?> GetDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var device = _project.Devices.FirstOrDefault(d => Same(d.Id, deviceId));
            if (device is null)
                return Task.FromResult<DeviceConfig?>(null);
            var paths = TagPaths.BuildGroupPaths(_project.Groups);
            return Task.FromResult<DeviceConfig?>(
                ToConfig(device, _project.Tags.Where(t => Same(t.DeviceId, deviceId)), paths));
        }
    }

    public Task UpsertDeviceAsync(DeviceConfig device, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _project.Devices.FirstOrDefault(d => Same(d.Id, device.DeviceId));
            if (existing is null)
            {
                _project.Devices.Add(new DeviceDto
                {
                    Id = device.DeviceId,
                    Driver = device.Driver,
                    ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds,
                    Settings = new Dictionary<string, string>(device.Settings),
                });
            }
            else
            {
                existing.Driver = device.Driver;
                existing.ScanIntervalMs = (int)device.ScanInterval.TotalMilliseconds;
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
            removed = _project.Devices.RemoveAll(d => Same(d.Id, deviceId)) > 0;
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- folders -------------------------------------------------------------

    public Task<IReadOnlyList<TagGroupDto>> GetGroupsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<TagGroupDto> groups = _project.Groups.Select(Clone).ToList();
            return Task.FromResult(groups);
        }
    }

    public Task UpsertGroupAsync(TagGroupDto group, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _project.Groups.FirstOrDefault(g => g.Id == group.Id);
            if (existing is null)
                _project.Groups.Add(Clone(group));
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
            if (_project.Groups.All(g => g.Id != groupId))
                return Task.FromResult(false);

            // Take the whole subtree with it, tags included.
            var doomed = new HashSet<string> { groupId };
            var queue = new Queue<string>();
            queue.Enqueue(groupId);
            while (queue.Count > 0)
            {
                var parent = queue.Dequeue();
                foreach (var child in _project.Groups.Where(g => g.ParentId == parent))
                    if (doomed.Add(child.Id))
                        queue.Enqueue(child.Id);
            }

            _project.Tags.RemoveAll(t => t.GroupId is not null && doomed.Contains(t.GroupId));
            _project.Groups.RemoveAll(g => doomed.Contains(g.Id));
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
            var paths = TagPaths.BuildGroupPaths(_project.Groups);
            IReadOnlyList<TagDto> tags = _project.Tags
                .Select(t => { var copy = Clone(t); copy.Path = TagPaths.TagPath(paths, t.GroupId, t.Name); return copy; })
                .ToList();
            return Task.FromResult(tags);
        }
    }

    public Task UpsertTagAsync(TagDto tag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _project.Tags.FirstOrDefault(t => t.Id == tag.Id);
            if (existing is null)
            {
                var copy = Clone(tag);
                copy.Id = string.IsNullOrWhiteSpace(tag.Id) ? Guid.NewGuid().ToString("N") : tag.Id.Trim();
                copy.Path = "";   // derived on read, never stored
                _project.Tags.Add(copy);
            }
            else
            {
                existing.GroupId = tag.GroupId;
                existing.DeviceId = tag.DeviceId;
                existing.Name = tag.Name;
                existing.Address = tag.Address;
                existing.DataType = tag.DataType;
                existing.Unit = tag.Unit;
                existing.Description = tag.Description;
                existing.Scale = tag.Scale;
                existing.Offset = tag.Offset;
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
            removed = _project.Tags.RemoveAll(t => t.Id == tagId) > 0;
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- alarms --------------------------------------------------------------

    public Task<IReadOnlyList<AlarmDto>> GetAlarmsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var paths = TagPaths.BuildGroupPaths(_project.Groups);
            var tagPaths = _project.Tags.ToDictionary(t => t.Id, t => TagPaths.TagPath(paths, t.GroupId, t.Name));
            IReadOnlyList<AlarmDto> alarms = _project.Alarms
                .Select(a => { var copy = Clone(a); copy.TagPath = tagPaths.GetValueOrDefault(a.TagId, ""); return copy; })
                .ToList();
            return Task.FromResult(alarms);
        }
    }

    public Task UpsertAlarmAsync(AlarmDto alarm, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var existing = _project.Alarms.FirstOrDefault(a => a.Id == alarm.Id);
            if (existing is null)
            {
                var copy = Clone(alarm);
                copy.Id = string.IsNullOrWhiteSpace(alarm.Id) ? Guid.NewGuid().ToString("N") : alarm.Id.Trim();
                copy.TagPath = "";   // derived on read
                _project.Alarms.Add(copy);
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
            removed = _project.Alarms.RemoveAll(a => a.Id == alarmId) > 0;
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
    }

    // --- northbound: IEC-104 -------------------------------------------------

    public Task<Iec104ServerOptions> GetIec104Async(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var stored = _project.Iec104;
            if (stored is null)
                return Task.FromResult(new Iec104ServerOptions());

            var paths = TagPaths.BuildGroupPaths(_project.Groups);
            var tagPaths = _project.Tags.ToDictionary(t => t.Id, t => TagPaths.TagPath(paths, t.GroupId, t.Name));

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
            var copy = Clone(options);
            foreach (var point in copy.Points)
                point.TagPath = "";   // derived on read, never stored
            _project.Iec104 = copy;
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    // --- helpers -------------------------------------------------------------

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Deep copy through JSON — the options tree is small and this cannot miss a new field.</summary>
    private static Iec104ServerOptions Clone(Iec104ServerOptions options) =>
        JsonSerializer.Deserialize<Iec104ServerOptions>(JsonSerializer.Serialize(options, Json), Json)!;

    private static DeviceConfig ToConfig(DeviceDto d, IEnumerable<TagDto> tags,
        IReadOnlyDictionary<string, string> groupPaths) => new()
    {
        DeviceId = d.Id,
        Driver = d.Driver,
        ScanInterval = TimeSpan.FromMilliseconds(d.ScanIntervalMs <= 0 ? 1000 : d.ScanIntervalMs),
        Settings = new Dictionary<string, string>(d.Settings),
        Tags = tags.Select(t => new TagDefinition
        {
            Id = TagPaths.TagPath(groupPaths, t.GroupId, t.Name),
            Name = t.Name,
            Address = t.Address,
            DataType = t.DataType,
            Unit = t.Unit,
            Description = t.Description,
            Scale = t.Scale,
            Offset = t.Offset,
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
        Name = t.Name,
        Path = t.Path,
        Address = t.Address,
        DataType = t.DataType,
        Unit = t.Unit,
        Description = t.Description,
        Scale = t.Scale,
        Offset = t.Offset,
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
