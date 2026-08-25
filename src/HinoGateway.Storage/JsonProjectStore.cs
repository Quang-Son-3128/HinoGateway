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

    /// <summary>
    /// Set when the primary file could not be read and the configuration came from the <c>.bak</c>
    /// instead. Surfaced so an operator finds out from the gateway rather than from a log nobody
    /// opened: the gateway is running, but on a configuration one save older than the last one made.
    /// </summary>
    public string? RecoveredFromBackup { get; private set; }

    /// <summary>True when the file did not exist at startup (a fresh install to be seeded).</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return CurrentProject.Devices.Count == 0 && CurrentProject.Tags.Count == 0; }
    }

    // --- loading / saving ----------------------------------------------------

    /// <summary>
    /// Read the project file, and if it cannot be read, the <c>.bak</c> that every save has been
    /// leaving beside it.
    ///
    /// <para>
    /// Every save has written a backup since this class existed, and nothing ever read one. The error
    /// message on the failure path even told the operator to go and look for it by hand — which is a
    /// reasonable instruction for a workstation and the wrong one for an unattended IPC, because the
    /// one moment the backup matters is the moment nobody is on site. A gateway that will not start
    /// until someone drives out to it is down for hours, not seconds.
    /// </para>
    ///
    /// <para>
    /// Both files unreadable is still fatal, and deliberately so: starting with an empty configuration
    /// would take a live gateway off the air and then overwrite the operator's file with the nothing
    /// it had invented. Refusing to start keeps the evidence and keeps the outage legible.
    /// </para>
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("No project file at {Path} — starting with an empty configuration", _path);
            return;
        }

        if (TryLoad(_path, primary: true, out var primaryFailure))
            return;

        var backup = _path + ".bak";
        if (File.Exists(backup) && TryLoad(backup, primary: false, out _))
        {
            RecoveredFromBackup = backup;
            _logger.LogCritical(
                "The project file {Path} was unusable and the configuration was recovered from {Backup}. " +
                "The gateway is running one save behind: any change made after that backup was written " +
                "is gone. Review the configuration before trusting it",
                _path, backup);
            QuarantineUnreadablePrimary();
            return;
        }

        // The primary failure is what the operator needs to read — "project version 9 is newer than
        // this gateway supports" says what to do, where "could not be read" says only that something
        // is wrong. The backup is a footnote to it, not a replacement for it.
        throw new InvalidOperationException(
            $"Could not load the project file '{_path}': {primaryFailure?.Message} " +
            $"The backup '{backup}' could not be used either. Restore a .hino export, or remove the " +
            "file to start with an empty configuration.", primaryFailure);
    }

    private bool TryLoad(string path, bool primary, out Exception? failure)
    {
        failure = null;
        try
        {
            var project = JsonSerializer.Deserialize<HinoProject>(File.ReadAllText(path), Json);
            if (project is null)
                throw new InvalidDataException("The project file is empty.");
            if (!string.Equals(project.Format, HinoProject.FormatTag, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Not a HinoGateway project file (format = '{project.Format}').");
            if (project.Version > HinoProject.CurrentVersion)
                throw new InvalidDataException(
                    $"Project version {project.Version} is newer than this gateway supports ({HinoProject.CurrentVersion}).");

            Normalise(project, out var migrationNotes);
            foreach (var note in migrationNotes)
                _logger.LogWarning("Project migration: {Note}", note);
            _project = project;
            _logger.LogInformation(
                "Loaded configuration from {Path}: {Devices} connection(s), {Groups} folder(s), {Tags} tag(s), {Alarms} alarm(s)",
                path, project.Devices.Count, project.Groups.Count, project.Tags.Count, project.Alarms.Count);
            return true;
        }
        catch (Exception ex)
        {
            failure = ex;
            _logger.Log(primary ? LogLevel.Warning : LogLevel.Error, ex,
                "Could not load the project file {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Move the unreadable primary aside after recovering from the backup.
    ///
    /// <para>
    /// Without this the next save would destroy the copy that just rescued the gateway:
    /// <see cref="DurableAtomicFile"/> publishes by replacing the primary and moving the file it
    /// displaced into the <c>.bak</c> slot — so the corrupt primary would overwrite the good backup
    /// on the very first configuration change. Renaming it leaves no primary to displace, keeps the
    /// good backup where it is, and preserves the damaged bytes for whoever has to explain them.
    /// </para>
    /// </summary>
    private void QuarantineUnreadablePrimary()
    {
        var quarantine = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(_path, quarantine);
            _logger.LogWarning("The unreadable project file was preserved as {Path}", quarantine);
        }
        catch (Exception ex)
        {
            // Best effort. Failing startup over the rename would undo the recovery it is protecting.
            _logger.LogError(ex,
                "Could not move the unreadable project file aside; the next configuration save will " +
                "overwrite the backup it was just recovered from");
        }
    }

    /// <summary>
    /// Replace the in-memory project with whatever the file actually contains, after a write that
    /// did not land. A missing file means the very first save failed, and the honest state for that
    /// is an empty project — not the change the caller was told had been rejected.
    /// </summary>
    private void RevertToDisk()
    {
        if (!File.Exists(_path))
        {
            lock (_gate)
                _project = new HinoProject();
            return;
        }

        var project = JsonSerializer.Deserialize<HinoProject>(File.ReadAllText(_path), Json)
                      ?? throw new InvalidDataException("The project file is empty.");
        Normalise(project);
        lock (_gate)
            _project = project;
    }

    /// <summary>Version 1 files nested tags under their device; lift them into one folder per device.</summary>
    public static void Normalise(HinoProject project) => Normalise(project, out _);

    /// <summary>
    /// The same, reporting what the upgrade changed about the project's control surface.
    ///
    /// <para>
    /// The notes are not decoration. The version 7 step below carries a tag's authority to be
    /// commanded across from the old format, which is the only correct reading of a version 6 file
    /// — but it means a restore can end with writable points that whoever performs it never chose,
    /// and had no way to see. Naming them is what turns that into a decision.
    /// </para>
    /// </summary>
    public static void Normalise(HinoProject project, out IReadOnlyList<string> notes)
    {
        var collected = new List<string>();
        notes = collected;
        NormaliseCore(project, collected);
    }

    private static void NormaliseCore(HinoProject project, List<string> notes)
    {
        project.Devices ??= new();
        project.Groups ??= new();
        project.Tags ??= new();
        project.Alarms ??= new();
        project.Commands ??= new();

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
            var enabled = project.Tags.Where(t => commandedTagIds.Contains(t.Id) && !t.AllowWrite).ToList();
            foreach (var tag in enabled)
                tag.AllowWrite = true;

            if (enabled.Count > 0)
                notes.Add(
                    $"Upgrading from project version {project.Version} kept the write permission that " +
                    $"{enabled.Count} tag(s) already had as IEC command points, because in that format " +
                    "mapping a point as commandable was the permission. Review each one before applying: " +
                    string.Join(", ", enabled.Select(t => t.Name ?? t.Id).OrderBy(n => n, StringComparer.Ordinal)));
        }

        // Version 9 added commands. A version 8 file has none, and the list defaults to empty; the
        // branch is here so that raising CurrentVersion stays a deliberate act with somewhere to put
        // the conversion when one is finally needed.
        if (project.Version < 9)
        {
            // No conversion required.
        }

        // Version 8 added accumulator tags. Nothing to convert — a version 7 file has no tag of that
        // kind, and every new field defaults — but the branch exists so that raising CurrentVersion
        // is a deliberate act with a place to put the migration when one is eventually needed.
        if (project.Version < 8)
        {
            // No conversion required.
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
            // The write is atomic, so the file still holds the last good configuration. Re-reading it
            // puts memory back in step with the disk, which is the whole point: the request used to
            // fail while the change stayed live in memory, so every subsequent GET showed a
            // configuration that a restart would silently undo — and an operator who retried, saw
            // their change, and walked away had no way to know it was never saved.
            //
            // Done here rather than in the transaction scope because the non-transactional mutators
            // have the same problem: each one edits the project and then asks for a save.
            _logger.LogError(ex, "Could not write the project file {Path} — reverting the in-memory copy", _path);
            try
            {
                RevertToDisk();
            }
            catch (Exception revert)
            {
                // Now both the write and the read back have failed. Say so plainly and leave memory
                // alone; the exception below is still what the caller acts on.
                _logger.LogCritical(revert,
                    "Could not re-read {Path} after a failed write — the in-memory configuration may " +
                    "differ from the file on disk until this gateway is restarted", _path);
            }

            throw new ConfigurationPersistenceException(_path,
                $"Could not persist the gateway project to '{_path}'. The change has been rolled back.", ex);
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
            var pathsByKey = PathsByStorageKey(project, paths);
            IReadOnlyList<TagDto> tags = project.Tags
                .Select(t =>
                {
                    var copy = Clone(t);
                    copy.Path = TagPaths.TagPath(paths, t.GroupId, t.Name);
                    copy.SourcePath = t.SourceTagKey is { } key ? pathsByKey.GetValueOrDefault(key, "") : "";
                    return copy;
                })
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
            var pathsByKey = PathsByStorageKey(project, paths);
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
                    // An accumulator carries both halves of its source reference: the path to read
                    // the counter with, and the storage key its durable baselines are filed under.
                    SourceTagKey = t.SourceTagKey,
                    SourceTagId = t.SourceTagKey is { } sourceKey
                        ? pathsByKey.GetValueOrDefault(sourceKey)
                        : null,
                    Period = t.Period,
                    RolloverAt = t.RolloverAt,
                    MaxRatePerHour = t.MaxRatePerHour,
                    // An accumulator is never writable, whatever the stored flag says: the number is
                    // derived, and the counter behind it is the authority.
                    AllowWrite = t.Kind == TagKind.Accumulator ? false : t.AllowWrite,
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
                copy.Path = "";         // derived on read, never stored
                copy.SourcePath = "";   // likewise: only the storage key is durable
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
                // Command authorisation is part of the tag like everything else. Leaving these three
                // out meant the first edit of an already-commandable tag silently revoked its write
                // permission — and made the write limits of the EVN setpoint points unreachable from
                // the UI, because saving the tag was the very thing that cleared them.
                existing.AllowWrite = tag.AllowWrite;
                existing.WriteMin = tag.WriteMin;
                existing.WriteMax = tag.WriteMax;
                existing.Monotonic = tag.Monotonic;
                existing.ScanIntervalMs = tag.ScanIntervalMs;
                existing.SourceTagKey = tag.SourceTagKey;
                existing.Period = tag.Period;
                existing.RolloverAt = tag.RolloverAt;
                existing.MaxRatePerHour = tag.MaxRatePerHour;
                existing.Path = "";
                existing.SourcePath = "";
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

    // --- commands ------------------------------------------------------------

    public Task<IReadOnlyList<CommandDto>> GetCommandsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = CurrentProject;
            var paths = TagPaths.BuildGroupPaths(project.Groups);
            var tagPaths = project.Tags.ToDictionary(t => t.Id, t => TagPaths.TagPath(paths, t.GroupId, t.Name));

            var commands = project.Commands.Select(Clone).ToList();
            foreach (var command in commands)
                command.TriggerTagPath = tagPaths.GetValueOrDefault(command.TriggerTagId, "");
            return Task.FromResult<IReadOnlyList<CommandDto>>(commands);
        }
    }

    public Task UpsertCommandAsync(CommandDto command, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var project = ProjectForMutation();
            var copy = Clone(command);
            copy.TriggerTagPath = "";   // derived on read, never stored
            var index = project.Commands.FindIndex(c => c.Id == copy.Id);
            if (index >= 0) project.Commands[index] = copy;
            else project.Commands.Add(copy);
        }
        SaveOrDefer();
        return Task.CompletedTask;
    }

    public Task<bool> DeleteCommandAsync(string commandId, CancellationToken ct = default)
    {
        bool removed;
        lock (_gate)
            removed = ProjectForMutation().Commands.RemoveAll(c => c.Id == commandId) > 0;
        if (removed)
            SaveOrDefer();
        return Task.FromResult(removed);
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

    /// <summary>
    /// Tag storage key → runtime path. This is the lookup that lets an accumulator name its source
    /// counter by a key that never changes while still reading it by a path that does.
    /// </summary>
    private static Dictionary<string, string> PathsByStorageKey(
        HinoProject project, IReadOnlyDictionary<string, string> groupPaths)
    {
        var byKey = new Dictionary<string, string>(project.Tags.Count, StringComparer.Ordinal);
        foreach (var tag in project.Tags)
            byKey[tag.Id] = TagPaths.TagPath(groupPaths, tag.GroupId, tag.Name);
        return byKey;
    }

    /// <summary>Deep copy through JSON — the options tree is small and this cannot miss a new field.</summary>
    private static Iec104ServerOptions Clone(Iec104ServerOptions options) =>
        JsonSerializer.Deserialize<Iec104ServerOptions>(JsonSerializer.Serialize(options, Json), Json)!;

    private static HinoProject Clone(HinoProject project) =>
        JsonSerializer.Deserialize<HinoProject>(JsonSerializer.Serialize(project, Json), Json)!;

    private static MqttPublisherOptions Clone(MqttPublisherOptions options) =>
        JsonSerializer.Deserialize<MqttPublisherOptions>(JsonSerializer.Serialize(options, Json), Json)!;

    private static CommandDto Clone(CommandDto command) =>
        JsonSerializer.Deserialize<CommandDto>(JsonSerializer.Serialize(command, Json), Json)!;

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
        SourceTagKey = t.SourceTagKey,
        SourcePath = t.SourcePath,
        Period = t.Period,
        RolloverAt = t.RolloverAt,
        MaxRatePerHour = t.MaxRatePerHour,
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
