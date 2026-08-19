using System.Text;
using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Server.Security;
using HinoGateway.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HinoGateway.Server.Controllers;

/// <summary>
/// CRUD over the configuration — connections (devices), tag folders and tags — plus an explicit
/// "apply" that reloads the running engine from the store. Edits are persisted immediately; they
/// take effect on the live engine when <see cref="Reload"/> is called (the UI's Apply button).
/// </summary>
[ApiController]
[Route("api/config")]
[Authorize(Policy = GatewayPolicies.Admin)]
public sealed class ConfigController : ControllerBase
{
    private readonly IConfigStore _store;
    private readonly ISecretStore _secrets;
    private readonly ProjectValidator _validator;

    public ConfigController(IConfigStore store, ISecretStore secrets, ProjectValidator validator)
    {
        _store = store;
        _secrets = secrets;
        _validator = validator;
    }

    /// <summary>Driver types available for the connection editor's dropdown.</summary>
    [HttpGet("drivers")]
    public IReadOnlyList<string> GetDrivers([FromServices] IEnumerable<IDeviceDriverFactory> factories) =>
        factories.Select(f => f.DriverType).OrderBy(x => x).ToList();

    /// <summary>
    /// What every installed driver says about itself: its settings with defaults, bounds and help,
    /// its address syntax and the types it can decode. The connection editor builds its whole form
    /// from this, so a driver that declares a Rack and a Slot gets a Rack and a Slot box without any
    /// change in the client — which is the point of the descriptor.
    /// </summary>
    [HttpGet("driver-descriptors")]
    public IReadOnlyList<DriverDescriptor> GetDriverDescriptors(
        [FromServices] IEnumerable<IDeviceDriverFactory> factories) =>
        factories
            .Select(f => f.SafeDescribe())
            .OrderBy(d => d.DriverType, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // --- connections ---------------------------------------------------------

    [HttpGet("devices")]
    public async Task<IReadOnlyList<DeviceDto>> GetDevices(CancellationToken ct)
    {
        var devices = await _store.GetDevicesAsync(ct);
        return devices.Select(ConfigMapping.ToDto).ToList();
    }

    [HttpGet("devices/{id}")]
    public async Task<ActionResult<DeviceDto>> GetDevice(string id, CancellationToken ct)
    {
        var d = await _store.GetDeviceAsync(id, ct);
        return d is null ? NotFound() : ConfigMapping.ToDto(d);
    }

    [HttpPut("devices/{id}")]
    public async Task<IActionResult> UpsertDevice(string id, [FromBody] DeviceDto dto,
        [FromServices] IEnumerable<IDeviceDriverFactory> factories, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || dto.Id.Trim() != id)
            return BadRequest("Device id in body must match the URL.");

        var factory = factories.FirstOrDefault(f => string.Equals(f.DriverType, dto.Driver,
            StringComparison.OrdinalIgnoreCase));
        if (factory is null)
        {
            var known = factories.Select(f => f.DriverType).ToList();
            return BadRequest($"Unknown driver '{dto.Driver}'. Available: {string.Join(", ", known)}.");
        }

        var protectedDevice = SecretConfiguration.ProtectDevice(
            ConfigMapping.ToConfig(dto), factory, _secrets, clearEmpty: true, out _);
        await _store.UpsertDeviceAsync(protectedDevice, ct);
        return NoContent();
    }

    [HttpDelete("devices/{id}")]
    public async Task<IActionResult> DeleteDevice(string id, CancellationToken ct)
    {
        // A connection that tags still point at must not disappear from under them.
        var inUse = (await _store.GetTagsAsync(ct))
            .Count(t => string.Equals(t.DeviceId, id, StringComparison.OrdinalIgnoreCase));
        if (inUse > 0)
            return Conflict($"Connection '{id}' is still used by {inUse} tag(s). Reassign or delete those tags first.");

        var removed = await _store.DeleteDeviceAsync(id, ct);
        if (removed)
        {
            var prefix = $"device.{id}.";
            foreach (var name in _secrets.Names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _secrets.Delete(name);
        }
        return removed ? NoContent() : NotFound();
    }

    // --- tag folders ---------------------------------------------------------

    [HttpGet("groups")]
    public Task<IReadOnlyList<TagGroupDto>> GetGroups(CancellationToken ct) => _store.GetGroupsAsync(ct);

    [HttpPut("groups/{id}")]
    public async Task<IActionResult> UpsertGroup(string id, [FromBody] TagGroupDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || dto.Id.Trim() != id)
            return BadRequest("Folder id in body must match the URL.");
        dto.Name = dto.Name?.Trim() ?? "";
        if (dto.Name.Length == 0)
            return BadRequest("Folder name is required.");
        if (dto.Name.Contains('/'))
            return BadRequest("Folder names cannot contain '/' (it is the path separator).");

        var groups = await _store.GetGroupsAsync(ct);

        if (dto.ParentId is not null)
        {
            if (dto.ParentId == dto.Id)
                return BadRequest("A folder cannot be its own parent.");
            var byId = groups.ToDictionary(g => g.Id);
            if (!byId.ContainsKey(dto.ParentId))
                return BadRequest($"Parent folder '{dto.ParentId}' does not exist.");
            // Reject moving a folder under its own descendant (would create a cycle).
            for (var p = byId.GetValueOrDefault(dto.ParentId); p is not null; p = p.ParentId is null ? null : byId.GetValueOrDefault(p.ParentId))
            {
                if (p.Id == dto.Id)
                    return BadRequest("Cannot move a folder into one of its own sub-folders.");
            }
        }

        var duplicate = groups.FirstOrDefault(g =>
            g.Id != dto.Id && g.ParentId == dto.ParentId &&
            string.Equals(g.Name, dto.Name, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
            return BadRequest($"A folder named '{dto.Name}' already exists at this level.");

        await _store.UpsertGroupAsync(dto, ct);
        return NoContent();
    }

    /// <summary>Deletes a folder including all sub-folders and the tags inside them.</summary>
    [HttpDelete("groups/{id}")]
    public async Task<IActionResult> DeleteGroup(string id, [FromServices] IAlarmStore alarms, CancellationToken ct)
    {
        // Deleting a folder takes its whole subtree of tags with it — refuse while any of those
        // tags still has an alarm on it, mirroring the single-tag guard.
        var groups = await _store.GetGroupsAsync(ct);
        var doomedGroups = Subtree(groups, id);
        var doomedTags = (await _store.GetTagsAsync(ct))
            .Where(t => t.GroupId is not null && doomedGroups.Contains(t.GroupId))
            .Select(t => t.Id)
            .ToHashSet();
        var watching = (await alarms.GetAlarmsAsync(ct)).Where(a => doomedTags.Contains(a.TagId)).ToList();
        if (watching.Count > 0)
            return Conflict($"Tags in this folder are watched by {watching.Count} alarm(s): {string.Join(", ", watching.Select(a => a.Name))}. Delete those alarms first.");

        var removed = await _store.DeleteGroupAsync(id, ct);
        return removed ? NoContent() : NotFound();
    }

    /// <summary>The folder itself plus every descendant folder id.</summary>
    private static HashSet<string> Subtree(IReadOnlyList<TagGroupDto> groups, string rootId)
    {
        var ids = new HashSet<string> { rootId };
        var queue = new Queue<string>();
        queue.Enqueue(rootId);
        while (queue.Count > 0)
        {
            var parent = queue.Dequeue();
            foreach (var child in groups.Where(g => g.ParentId == parent))
                if (ids.Add(child.Id))
                    queue.Enqueue(child.Id);
        }
        return ids;
    }

    // --- tags ----------------------------------------------------------------

    [HttpGet("tags")]
    public Task<IReadOnlyList<TagDto>> GetTags(CancellationToken ct) => _store.GetTagsAsync(ct);

    [HttpPut("tags/{id}")]
    public async Task<IActionResult> UpsertTag(string id, [FromBody] TagDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || dto.Id.Trim() != id)
            return BadRequest("Tag id in body must match the URL.");
        dto.Name = dto.Name?.Trim() ?? "";
        if (dto.Name.Length == 0)
            return BadRequest("Tag name is required.");
        if (dto.Name.Contains('/'))
            return BadRequest("Tag names cannot contain '/' (it is the path separator).");

        if (!Enum.IsDefined(dto.Kind))
            return BadRequest("Unknown tag kind.");

        var devices = await _store.GetDevicesAsync(ct);
        if (dto.Kind == TagKind.Device)
        {
            if (!devices.Any(d => string.Equals(d.DeviceId, dto.DeviceId, StringComparison.OrdinalIgnoreCase)))
                return BadRequest($"Connection '{dto.DeviceId}' does not exist.");
        }
        else
        {
            // A memory or expression tag belongs to no connection. Clearing the fields that only mean
            // something for a device tag keeps the stored project honest — and stops a tag converted
            // from a device tag from carrying an address a driver would try to read.
            dto.DeviceId = "";
            dto.Address = "";
            dto.Scale = 1.0;
            dto.Offset = 0.0;
            dto.Monotonic = false;
            if (dto.Kind == TagKind.Expression)
                dto.InitialValue = null;
            if (dto.Kind == TagKind.Memory)
                dto.Expression = null;
        }

        var groups = await _store.GetGroupsAsync(ct);
        if (dto.GroupId is not null && !groups.Any(g => g.Id == dto.GroupId))
            return BadRequest($"Folder '{dto.GroupId}' does not exist.");

        // The runtime tag id is folder path + name, so (folder, name) must be unique.
        var storedTags = (await _store.GetTagsAsync(ct)).ToList();
        var clash = storedTags.FirstOrDefault(t =>
            t.Id != dto.Id && t.GroupId == dto.GroupId &&
            string.Equals(t.Name, dto.Name, StringComparison.OrdinalIgnoreCase));
        if (clash is not null)
            return BadRequest($"A tag named '{dto.Name}' already exists in this folder.");

        var index = storedTags.FindIndex(t => t.Id == dto.Id);
        if (index >= 0) storedTags[index] = dto;
        else storedTags.Add(dto);

        if (dto.Kind != TagKind.Device)
        {
            var internalProblems = ProjectValidator.ValidateInternalTag(dto, storedTags, groups);
            if (internalProblems.Count > 0)
                return BadRequest(internalProblems);
        }

        await _store.UpsertTagAsync(dto, ct);
        return NoContent();
    }

    [HttpDelete("tags/{id}")]
    public async Task<IActionResult> DeleteTag(string id, [FromServices] IAlarmStore alarms, CancellationToken ct)
    {
        // An alarm watching this tag must not be left pointing at nothing.
        var watching = (await alarms.GetAlarmsAsync(ct)).Where(a => a.TagId == id).ToList();
        if (watching.Count > 0)
            return Conflict($"Tag is watched by {watching.Count} alarm(s): {string.Join(", ", watching.Select(a => a.Name))}. Delete those alarms first.");

        var removed = await _store.DeleteTagAsync(id, ct);
        return removed ? NoContent() : NotFound();
    }

    // --- apply / export / import --------------------------------------------

    /// <summary>
    /// Apply the persisted configuration to the running engine (no process restart). Alarms are
    /// reloaded too: their runtime tag paths change whenever a tag or folder is renamed or moved.
    /// </summary>
    /// <remarks>
    /// Returns what actually happened: a connection whose configuration cannot be used is reported
    /// here as rejected (with the reason) while every healthy connection keeps running — applying a
    /// configuration never takes the gateway off the air.
    /// </remarks>
    [HttpPost("reload")]
    public async Task<ActionResult<EngineApplyResult>> Reload([FromServices] DeviceEngine engine,
        [FromServices] AlarmEngine alarmEngine, [FromServices] IAlarmStore alarms,
        [FromServices] ILogger<ConfigController> logger, CancellationToken ct)
    {
        var problems = await _validator.ValidateStoredAsync(_store, alarms, ct);
        if (problems.Count > 0)
            return BadRequest(new { Message = "Stored project is invalid; runtime was left unchanged.", Errors = problems });

        var devices = await _store.GetDevicesAsync(ct);
        var applied = await engine.ReloadAsync(devices, await _store.GetInternalTagsAsync(ct));
        alarmEngine.Reload(AlarmMapping.ToDefinitions(
            await alarms.GetAlarmsAsync(ct), await _store.GetTagsAsync(ct), logger));
        return applied;
    }

    private static readonly JsonSerializerOptions ExportJson = new() { WriteIndented = true };

    /// <summary>Download the whole configuration as a portable <c>.hino</c> project file (v4).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromServices] IAlarmStore alarms, CancellationToken ct)
    {
        var devices = await _store.GetDevicesAsync(ct);
        var project = new HinoProject
        {
            ExportedAt = DateTimeOffset.UtcNow,
            Devices = devices.Select(d => { var dto = ConfigMapping.ToDto(d); dto.Tags = new(); return dto; }).ToList(),
            Groups = (await _store.GetGroupsAsync(ct)).ToList(),
            Tags = (await _store.GetTagsAsync(ct)).ToList(),
            Alarms = (await alarms.GetAlarmsAsync(ct)).ToList(),
            Iec104 = await _store.GetIec104Async(ct),
            Mqtt = await _store.GetMqttAsync(ct),
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(project, ExportJson));
        var fileName = $"hinogateway-{DateTime.Now:yyyyMMdd-HHmmss}.hino";
        return File(bytes, "application/json", fileName);
    }

    /// <summary>
    /// Import a <c>.hino</c> project (v1–v3). Everything is upserted; pass <c>?replace=true</c>
    /// to first clear the existing configuration (full restore) instead of merging. Version-1
    /// files (tags nested under devices) are converted: one folder per device. Does not
    /// auto-apply — the caller reloads the engine afterwards.
    /// </summary>
    [HttpPost("import")]
    public async Task<ActionResult<ImportResult>> Import([FromBody] HinoProject project, [FromQuery] bool replace,
        [FromServices] IAlarmStore alarms, [FromServices] IEnumerable<IDeviceDriverFactory> factories,
        [FromServices] ILogger<ConfigController> logger,
        CancellationToken ct)
    {
        if (!string.Equals(project.Format, HinoProject.FormatTag, StringComparison.OrdinalIgnoreCase))
            return BadRequest($"Not a HinoGateway project file (format = '{project.Format}').");
        if (project.Version > HinoProject.CurrentVersion)
            return BadRequest($"Project version {project.Version} is newer than this server supports ({HinoProject.CurrentVersion}).");

        JsonProjectStore.Normalise(project, out var migrationNotes);
        if (replace)
        {
            project.Iec104 ??= new Iec104ServerOptions();
            project.Mqtt ??= new MqttPublisherOptions();
        }

        if (project.Devices.Any(d => string.IsNullOrWhiteSpace(d.Id)))
            return BadRequest("A device in the file has an empty Id.");

        // Version 1: tags lived under their device — convert to one folder per device.
        var groups = project.Groups.ToList();
        var tags = project.Tags.ToList();

        // Validate references before mutating anything.
        var groupIds = groups.Select(g => g.Id).ToHashSet();
        if (!replace) groupIds.UnionWith((await _store.GetGroupsAsync(ct)).Select(g => g.Id));
        var deviceIds = project.Devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!replace) deviceIds.UnionWith((await _store.GetDevicesAsync(ct)).Select(d => d.DeviceId));

        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name))
                return BadRequest("A folder in the file has an empty id or name.");
            if (g.ParentId is not null && !groupIds.Contains(g.ParentId))
                return BadRequest($"Folder '{g.Name}' references missing parent '{g.ParentId}'.");
        }
        foreach (var t in tags)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || string.IsNullOrWhiteSpace(t.Name))
                return BadRequest("A tag in the file has an empty id or name.");
            // Only a device tag has a connection to point at; the formulas and initial values of the
            // other kinds are checked by the project validation.
            if (t.Kind == TagKind.Device && !deviceIds.Contains(t.DeviceId))
                return BadRequest($"Tag '{t.Name}' references missing connection '{t.DeviceId}'.");
            if (t.GroupId is not null && !groupIds.Contains(t.GroupId))
                return BadRequest($"Tag '{t.Name}' references missing folder '{t.GroupId}'.");
        }

        // Alarms must point at a tag that exists after the import (its storage key, either from the
        // file or already in the store).
        var tagKeys = tags.Select(t => t.Id).ToHashSet();
        if (!replace)
            tagKeys.UnionWith((await _store.GetTagsAsync(ct)).Select(t => t.Id));
        foreach (var a in project.Alarms)
        {
            if (string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.Name))
                return BadRequest("An alarm in the file has an empty id or name.");
            if (!tagKeys.Contains(a.TagId))
                return BadRequest($"Alarm '{a.Name}' references missing tag '{a.TagId}'.");
        }

        var prospective = await ProspectiveProjectAsync(project, replace, alarms, ct);
        var projectProblems = _validator.Validate(prospective);
        if (projectProblems.Count > 0)
            return BadRequest(new { Message = "Imported project is invalid; nothing was changed.", Errors = projectProblems });

        var removed = 0;
        var factoryMap = factories.ToDictionary(f => f.DriverType, StringComparer.OrdinalIgnoreCase);
        var importedDevices = new List<DeviceConfig>(project.Devices.Count);
        foreach (var dto in project.Devices)
        {
            var device = ConfigMapping.ToConfig(dto);
            if (factoryMap.TryGetValue(device.Driver, out var factory))
                device = SecretConfiguration.ProtectDevice(device, factory, _secrets,
                    clearEmpty: false, out _);
            importedDevices.Add(device);
        }
        if (project.Mqtt is not null)
            SecretConfiguration.ProtectMqtt(project.Mqtt, _secrets, clearEmpty: false);

        // One transaction around the whole import: all-or-nothing (a failure part-way through cannot
        // leave a half-replaced configuration), and the file is written once at the end instead of
        // once per entity — a 10 000-tag project would otherwise rewrite it 10 000 times.
        using (var tx = _store.BeginTransaction())
        {
            if (replace)
            {
                // Alarms first: they reference tags, and tag deletion is guarded against that.
                foreach (var a in await alarms.GetAlarmsAsync(ct)) { if (await alarms.DeleteAlarmAsync(a.Id, ct)) removed++; }
                foreach (var t in await _store.GetTagsAsync(ct)) { await _store.DeleteTagAsync(t.Id, ct); removed++; }
                foreach (var g in await _store.GetGroupsAsync(ct)) { if (await _store.DeleteGroupAsync(g.Id, ct)) removed++; }
                foreach (var d in await _store.GetDevicesAsync(ct)) { await _store.DeleteDeviceAsync(d.DeviceId, ct); removed++; }
            }

            foreach (var d in importedDevices)
                await _store.UpsertDeviceAsync(d, ct);

            // Parents before children so every ParentId reference resolves as rows appear.
            foreach (var g in OrderParentsFirst(groups))
                await _store.UpsertGroupAsync(g, ct);

            foreach (var t in tags)
                await _store.UpsertTagAsync(t, ct);

            foreach (var a in project.Alarms)
                await alarms.UpsertAlarmAsync(a, ct);

            if (project.Iec104 is not null)
                await _store.SaveIec104Async(project.Iec104, ct);
            if (project.Mqtt is not null)
                await _store.SaveMqttAsync(project.Mqtt, ct);

            tx.Commit();
        }

        // Returned rather than only logged: an upgraded file can arrive with control points that
        // are writable because the old format said so, and the person importing it is the last one
        // in a position to look before the gateway starts accepting commands for them.
        foreach (var note in migrationNotes)
            logger.LogWarning("Project import: {Note}", note);

        return new ImportResult(project.Devices.Count, groups.Count, tags.Count, project.Alarms.Count, removed,
            migrationNotes);
    }

    private static List<TagGroupDto> OrderParentsFirst(List<TagGroupDto> groups)
    {
        var ordered = new List<TagGroupDto>();
        var placed = new HashSet<string>();
        var remaining = new List<TagGroupDto>(groups);
        while (remaining.Count > 0)
        {
            var batch = remaining.Where(g => g.ParentId is null || placed.Contains(g.ParentId) || !groups.Any(x => x.Id == g.ParentId)).ToList();
            if (batch.Count == 0)
                break; // cycle in file — remaining rows are appended as-is below
            foreach (var g in batch) { ordered.Add(g); placed.Add(g.Id); remaining.Remove(g); }
        }
        ordered.AddRange(remaining);
        return ordered;
    }

    private async Task<HinoProject> ProspectiveProjectAsync(HinoProject imported, bool replace,
        IAlarmStore alarms, CancellationToken ct)
    {
        if (replace)
            return imported;

        var devices = (await _store.GetDevicesAsync(ct)).Select(ConfigMapping.ToDto).ToList();
        foreach (var device in devices) device.Tags.Clear();
        var current = new HinoProject
        {
            Devices = devices,
            Groups = (await _store.GetGroupsAsync(ct)).ToList(),
            Tags = (await _store.GetTagsAsync(ct)).ToList(),
            Alarms = (await alarms.GetAlarmsAsync(ct)).ToList(),
            Iec104 = await _store.GetIec104Async(ct),
            Mqtt = await _store.GetMqttAsync(ct),
        };
        current.Devices = Merge(current.Devices, imported.Devices, x => x.Id, StringComparer.OrdinalIgnoreCase);
        current.Groups = Merge(current.Groups, imported.Groups, x => x.Id, StringComparer.Ordinal);
        current.Tags = Merge(current.Tags, imported.Tags, x => x.Id, StringComparer.Ordinal);
        current.Alarms = Merge(current.Alarms, imported.Alarms, x => x.Id, StringComparer.Ordinal);
        if (imported.Iec104 is not null) current.Iec104 = imported.Iec104;
        if (imported.Mqtt is not null) current.Mqtt = imported.Mqtt;
        return current;
    }

    private static List<T> Merge<T>(IEnumerable<T> current, IEnumerable<T> incoming,
        Func<T, string> key, IEqualityComparer<string> comparer)
    {
        var result = current.ToList();
        foreach (var item in incoming)
        {
            var index = result.FindIndex(existing => comparer.Equals(key(existing), key(item)));
            if (index >= 0) result[index] = item;
            else result.Add(item);
        }
        return result;
    }

    public sealed record ImportResult(int Devices, int Groups, int Tags, int Alarms, int Removed,
        IReadOnlyList<string> MigrationNotes);
}
