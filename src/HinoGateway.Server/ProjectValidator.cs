using System.Globalization;
using HinoGateway.Core;
using HinoGateway.Expressions;

namespace HinoGateway.Server;

/// <summary>
/// One validation boundary for persisted projects. Controllers, import, startup and runtime reload
/// all call this class so a configuration cannot be accepted by one path and rejected by another.
/// Driver-specific checks remain owned by the driver factory; this class supplies the common shape,
/// descriptor bounds and cross-entity reference checks around them.
/// </summary>
public sealed class ProjectValidator
{
    private const int MaxIoa = 0xFFFFFF;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factories;

    public ProjectValidator(IEnumerable<IDeviceDriverFactory> factories)
    {
        _factories = factories.ToDictionary(f => f.DriverType, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> ValidateDevice(DeviceDto dto, IReadOnlyList<TagDefinition>? tags = null)
    {
        var problems = new List<string>();
        ValidateDeviceShape(dto, problems);

        if (!_factories.TryGetValue(dto.Driver?.Trim() ?? "", out var factory))
        {
            problems.Add($"Unknown driver '{dto.Driver}'. Available: {string.Join(", ", _factories.Keys)}.");
            return problems;
        }

        var descriptor = SafeDescribe(factory);
        ValidateSettings(dto.Settings ?? new Dictionary<string, string>(), descriptor, problems);

        var config = BuildConfig(dto, descriptor, tags);

        try
        {
            problems.AddRange(factory.Validate(config));
        }
        catch (Exception ex)
        {
            problems.Add($"Driver validation failed: {ex.Message}");
        }

        ValidateDriverTags(factory, descriptor, config, problems);
        return problems.Distinct(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Validate(HinoProject project)
    {
        var problems = new List<string>();
        var devices = project.Devices ?? new List<DeviceDto>();
        var groups = project.Groups ?? new List<TagGroupDto>();
        var tags = project.Tags ?? new List<TagDto>();
        var alarms = project.Alarms ?? new List<AlarmDto>();

        AddDuplicates(devices, d => d.Id, StringComparer.OrdinalIgnoreCase,
            id => $"Duplicate device ID '{id}'.", problems);
        AddDuplicates(groups, g => g.Id, StringComparer.Ordinal,
            id => $"Duplicate folder ID '{id}'.", problems);
        AddDuplicates(tags, t => t.Id, StringComparer.Ordinal,
            id => $"Duplicate tag storage ID '{id}'.", problems);
        AddDuplicates(alarms, a => a.Id, StringComparer.Ordinal,
            id => $"Duplicate alarm ID '{id}'.", problems);

        var groupById = groups.Where(g => !string.IsNullOrWhiteSpace(g.Id))
            .GroupBy(g => g.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id)) problems.Add("A folder has an empty ID.");
            if (string.IsNullOrWhiteSpace(group.Name)) problems.Add($"Folder '{group.Id}' has an empty name.");
            if (group.Name?.Contains(TagPaths.Separator) == true)
                problems.Add($"Folder '{group.Id}' contains '/' in its name.");
            if (group.ParentId is not null && !groupById.ContainsKey(group.ParentId))
                problems.Add($"Folder '{group.Id}' references missing parent '{group.ParentId}'.");
        }
        ValidateGroupCycles(groups, groupById, problems);
        foreach (var duplicate in groups.GroupBy(g => (g.ParentId, Name: g.Name?.Trim() ?? ""),
                     new FolderNameComparer()).Where(g => g.Count() > 1))
            problems.Add($"Folder name '{duplicate.Key.Name}' is duplicated at the same level.");

        var deviceById = devices.Where(d => !string.IsNullOrWhiteSpace(d.Id))
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var tagById = tags.Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var paths = TagPaths.BuildGroupPaths(groups);
        var definitionsByDevice = new Dictionary<string, List<TagDefinition>>(StringComparer.OrdinalIgnoreCase);

        // Expression tags, by runtime path, with the tags each one reads — collected here and checked
        // for dead references and cycles once every path is known.
        var formulas = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Id)) problems.Add("A tag has an empty storage ID.");
            if (string.IsNullOrWhiteSpace(tag.Name)) problems.Add($"Tag '{tag.Id}' has an empty name.");
            if (tag.Name?.Contains(TagPaths.Separator) == true)
                problems.Add($"Tag '{tag.Id}' contains '/' in its name.");
            if (!Enum.IsDefined(tag.DataType)) problems.Add($"Tag '{tag.Id}' has an unknown data type.");
            if (!Enum.IsDefined(tag.Kind)) problems.Add($"Tag '{tag.Id}' has an unknown kind.");
            if (!Enum.IsDefined(tag.EvalMode)) problems.Add($"Tag '{tag.Id}' has an unknown evaluation mode.");
            if (!double.IsFinite(tag.Scale) || !double.IsFinite(tag.Offset))
                problems.Add($"Tag '{tag.Id}' has non-finite scaling.");
            // 0 = follow the connection. A rate faster than the connection's own interval is not
            // rejected: the connection is the clock, so such a tag is simply read every scan.
            if (tag.ScanIntervalMs is < 0 or > 86_400_000)
                problems.Add($"Tag '{tag.Id}' has a scan rate outside 0..86400000 ms (0 = follow the connection).");
            if (tag.GroupId is not null && !groupById.ContainsKey(tag.GroupId))
                problems.Add($"Tag '{tag.Name}' references missing folder '{tag.GroupId}'.");

            // Everything below is per kind: what is required of a device tag (an address, a
            // connection) is meaningless for a tag the gateway produces itself, and demanding it was
            // the reason a memory tag could not be saved at all.
            if (tag.Kind == TagKind.Device)
            {
                if (string.IsNullOrWhiteSpace(tag.Address)) problems.Add($"Tag '{tag.Id}' has an empty address.");
                if (!deviceById.ContainsKey(tag.DeviceId ?? ""))
                    problems.Add($"Tag '{tag.Name}' references missing connection '{tag.DeviceId}'.");

                if (!deviceById.ContainsKey(tag.DeviceId ?? "") || string.IsNullOrWhiteSpace(tag.Name))
                    continue;
                var definition = new TagDefinition
                {
                    Id = TagPaths.TagPath(paths, tag.GroupId, tag.Name.Trim()),
                    Name = tag.Name.Trim(), Address = tag.Address ?? "", DataType = tag.DataType,
                    Unit = tag.Unit, Description = tag.Description, Scale = tag.Scale,
                    Offset = tag.Offset, Monotonic = tag.Monotonic,
                    ScanInterval = TagScanRate.FromMilliseconds(tag.ScanIntervalMs),
                };
                var deviceId = tag.DeviceId ?? "";
                if (!definitionsByDevice.TryGetValue(deviceId, out var list))
                    definitionsByDevice[deviceId] = list = new List<TagDefinition>();
                list.Add(definition);
                continue;
            }

            if (string.IsNullOrWhiteSpace(tag.Name))
                continue;
            var path = TagPaths.TagPath(paths, tag.GroupId, tag.Name.Trim());

            if (tag.Kind == TagKind.Memory)
            {
                // Refused here rather than at runtime: a memory tag whose initial value does not fit
                // its type would come up Bad on every restart with nobody watching.
                if (!string.IsNullOrWhiteSpace(tag.InitialValue)
                    && !InternalTagValues.TryParseInitial(tag.InitialValue, tag.DataType, out _, out var initialError))
                    problems.Add($"Tag '{path}': initial value {initialError?.TrimEnd('.')}.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(tag.Expression))
            {
                problems.Add($"Tag '{path}' is an expression tag but has no formula.");
                continue;
            }
            if (!ExpressionCompiler.TryCompile(tag.Expression, out var compiled, out var error, out var position))
            {
                problems.Add($"Tag '{path}': {error} (at character {position + 1}).");
                continue;
            }
            formulas[path] = compiled!.ReferencedTags;

            if (tag.EvalMode == EvalMode.Scan && tag.ScanIntervalMs > 0
                && tag.ScanIntervalMs < InternalTagDefaults.MinimumExpressionScanInterval.TotalMilliseconds)
                problems.Add($"Tag '{path}' recomputes every {tag.ScanIntervalMs} ms; " +
                             $"the fastest allowed is {InternalTagDefaults.MinimumExpressionScanInterval.TotalMilliseconds} ms.");
        }

        ValidateFormulas(formulas, tags, paths, problems);

        foreach (var duplicate in tags.Where(t => !string.IsNullOrWhiteSpace(t.Name))
                     .GroupBy(t => TagPaths.TagPath(paths, t.GroupId, t.Name.Trim()), StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
            problems.Add($"Duplicate runtime tag path '{duplicate.Key}'.");

        foreach (var device in devices)
        {
            var deviceProblems = ValidateDevice(device,
                definitionsByDevice.GetValueOrDefault(device.Id) ?? new List<TagDefinition>());
            problems.AddRange(deviceProblems.Select(p => $"Device '{device.Id}': {p}"));
        }

        ValidateAcrossDevices(devices, definitionsByDevice, problems);

        foreach (var alarm in alarms)
            problems.AddRange(ValidateAlarm(alarm, tagById.Keys.ToHashSet(StringComparer.Ordinal)));
        foreach (var duplicate in alarms.GroupBy(a => (a.TagId, a.Condition)).Where(g => g.Count() > 1))
            problems.Add($"Tag '{duplicate.Key.TagId}' has {duplicate.Count()} alarms for condition {duplicate.Key.Condition}.");

        if (project.Iec104 is not null)
            problems.AddRange(ValidateIec104(project.Iec104, tags));
        if (project.Mqtt is not null)
            problems.AddRange(ValidateMqtt(project.Mqtt));

        return problems.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Everything that can be decided about one memory or expression tag as it is being saved. The
    /// device path goes through <see cref="ValidateDevice"/>, which asks the driver; an internal tag
    /// has no driver to ask, so this is where its rules live.
    /// </summary>
    /// <param name="allTags">The tag database <em>including</em> the tag being saved.</param>
    public static IReadOnlyList<string> ValidateInternalTag(
        TagDto tag, IReadOnlyList<TagDto> allTags, IReadOnlyList<TagGroupDto> groups)
    {
        var problems = new List<string>();
        var paths = TagPaths.BuildGroupPaths(groups);
        var path = TagPaths.TagPath(paths, tag.GroupId, tag.Name?.Trim() ?? "");

        if (!Enum.IsDefined(tag.DataType)) problems.Add("Unknown data type.");
        if (tag.ScanIntervalMs is < 0 or > 86_400_000)
            problems.Add("The evaluation period must be between 0 and 86400000 ms (0 = default).");

        if (tag.Kind == TagKind.Memory)
        {
            if (!string.IsNullOrWhiteSpace(tag.InitialValue)
                && !InternalTagValues.TryParseInitial(tag.InitialValue, tag.DataType, out _, out var initialError))
                problems.Add($"Initial value: {initialError?.TrimEnd('.')}.");
            return problems;
        }

        if (string.IsNullOrWhiteSpace(tag.Expression))
        {
            problems.Add("An expression tag needs a formula.");
            return problems;
        }
        if (!ExpressionCompiler.TryCompile(tag.Expression, out _, out var error, out var position))
        {
            problems.Add($"{error} (at character {position + 1}).");
            return problems;
        }
        if (tag.EvalMode == EvalMode.Scan && tag.ScanIntervalMs > 0
            && tag.ScanIntervalMs < InternalTagDefaults.MinimumExpressionScanInterval.TotalMilliseconds)
            problems.Add($"The fastest evaluation period is " +
                         $"{InternalTagDefaults.MinimumExpressionScanInterval.TotalMilliseconds} ms.");

        // Compiled against the whole tag database, because a formula's remaining two failure modes —
        // reading a tag that does not exist, and closing a circle — only exist in relation to the
        // others. Problems that do not involve this tag are left alone; they are not this edit's
        // fault and blocking on them would trap the operator.
        var formulas = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var other in allTags.Where(t => t.Kind == TagKind.Expression && !string.IsNullOrWhiteSpace(t.Name)))
        {
            if (ExpressionCompiler.TryCompile(other.Expression ?? "", out var compiled, out _, out _))
                formulas[TagPaths.TagPath(paths, other.GroupId, other.Name.Trim())] = compiled!.ReferencedTags;
        }
        ValidateFormulas(formulas, allTags, paths, problems, focus: path);

        return problems;
    }

    public async Task<IReadOnlyList<string>> ValidateStoredAsync(IConfigStore store, IAlarmStore alarms,
        CancellationToken ct = default)
    {
        var devices = (await store.GetDevicesAsync(ct)).Select(ConfigMapping.ToDto).ToList();
        foreach (var device in devices) device.Tags.Clear();
        return Validate(new HinoProject
        {
            Devices = devices,
            Groups = (await store.GetGroupsAsync(ct)).ToList(),
            Tags = (await store.GetTagsAsync(ct)).ToList(),
            Alarms = (await alarms.GetAlarmsAsync(ct)).ToList(),
            Iec104 = await store.GetIec104Async(ct),
            Mqtt = await store.GetMqttAsync(ct),
        });
    }

    public static List<string> ValidateMqtt(MqttPublisherOptions o)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.Broker)) errors.Add("Broker is required.");
        if (o.Port is < 1 or > 65535) errors.Add("Port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(o.ClientId)) errors.Add("Client ID is required.");
        if (string.IsNullOrWhiteSpace(o.PublishTopic) || o.PublishTopic.Contains('#') || o.PublishTopic.Contains('+'))
            errors.Add("Publish topic is required and cannot contain MQTT wildcards.");
        if (o.QoS is < 0 or > 2) errors.Add("QoS must be 0, 1, or 2.");
        if (o.BatchIntervalMs is < 50 or > 60_000) errors.Add("Batch interval must be 50-60000 ms.");
        if (o.QueueCapacity is < 64 or > 100_000) errors.Add("Queue capacity must be 64-100000 batches.");
        if (o.MaxSpoolBytes is < 1_048_576 or > 10L * 1024 * 1024 * 1024) errors.Add("Spool size must be 1 MB-10 GB.");
        if (o.ReconnectMinMs is < 100 or > 60_000 || o.ReconnectMaxMs < o.ReconnectMinMs || o.ReconnectMaxMs > 300_000)
            errors.Add("Reconnect range is invalid.");
        return errors;
    }

    public static List<string> ValidateAlarm(AlarmDto alarm, IReadOnlySet<string> tagIds)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(alarm.Id)) problems.Add("An alarm has an empty ID.");
        if (string.IsNullOrWhiteSpace(alarm.Name)) problems.Add($"Alarm '{alarm.Id}' has an empty name.");
        if (!tagIds.Contains(alarm.TagId ?? ""))
            problems.Add($"Alarm '{alarm.Name}' references missing tag '{alarm.TagId}'.");
        if (!Enum.IsDefined(alarm.Condition)) problems.Add($"Alarm '{alarm.Name}' has an unknown condition.");
        if (!Enum.IsDefined(alarm.Severity)) problems.Add($"Alarm '{alarm.Name}' has an unknown severity.");
        if (!double.IsFinite(alarm.Limit) || !double.IsFinite(alarm.Hysteresis) || alarm.Hysteresis < 0)
            problems.Add($"Alarm '{alarm.Name}' has invalid limit/hysteresis.");
        if (alarm.OnDelayMs is < 0 or > 86_400_000 || alarm.OffDelayMs is < 0 or > 86_400_000)
            problems.Add($"Alarm '{alarm.Name}' delay must be between 0 and 86400000 ms.");
        return problems;
    }

    public static List<string> ValidateIec104(Iec104ServerOptions options, IReadOnlyList<TagDto> tags)
    {
        var problems = new List<string>();
        if (options.Port is < 1 or > 65535) problems.Add($"Port must be between 1 and 65535 (got {options.Port}).");
        if (string.IsNullOrWhiteSpace(options.BindAddress)) problems.Add("A bind address is required (use 0.0.0.0 for every interface).");
        if (options.CommonAddress is < 1 or > 65534) problems.Add($"Common address must be between 1 and 65534 (got {options.CommonAddress}).");
        if (options.MaxConnections is < 1 or > 32) problems.Add($"Max connections must be between 1 and 32 (got {options.MaxConnections}).");
        if (options.CommandQueueCapacity is < 1 or > 10_000) problems.Add("Command queue capacity must be between 1 and 10000.");
        if (options.CommandTimeoutMs is < 100 or > 600_000) problems.Add("Command timeout must be between 100 and 600000 milliseconds.");
        if (options.K is < 1 or > 32767) problems.Add("k must be between 1 and 32767.");
        if (options.W is < 1 or > 32767) problems.Add("w must be between 1 and 32767.");
        if (options.W > options.K) problems.Add($"w ({options.W}) must not exceed k ({options.K}).");
        if (options.T2 >= options.T1) problems.Add($"t2 ({options.T2}s) must be shorter than t1 ({options.T1}s).");
        foreach (var (name, value) in new[] { ("t0", options.T0), ("t1", options.T1), ("t2", options.T2), ("t3", options.T3) })
            if (value is < 1 or > 255) problems.Add($"{name} must be between 1 and 255 seconds (got {value}).");
        if (options.PeriodicIntervalSeconds is < 0 or > 86400)
            problems.Add("The periodic interval must be between 0 (off) and 86400 seconds.");

        var tagsById = tags.Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var duplicate in options.Points.GroupBy(p => p.Ioa).Where(g => g.Count() > 1))
            problems.Add($"IOA {duplicate.Key} is mapped {duplicate.Count()} times — each address must be unique.");
        foreach (var point in options.Points)
        {
            if (point.Ioa is < 1 or > MaxIoa)
            {
                problems.Add($"IOA {point.Ioa} is out of range (1 … {MaxIoa}).");
                continue;
            }
            if (!tagsById.TryGetValue(point.TagId, out var tag))
            {
                problems.Add($"IOA {point.Ioa} references a tag that does not exist.");
                continue;
            }
            if (!Enum.IsDefined(point.Type)) problems.Add($"IOA {point.Ioa}: unknown point type.");
            if (!Enum.IsDefined(point.Command)) problems.Add($"IOA {point.Ioa}: unknown command type.");
            if (!double.IsFinite(point.Deadband) || point.Deadband < 0) problems.Add($"IOA {point.Ioa}: the deadband cannot be negative or non-finite.");
            if (!double.IsFinite(point.ReportScale) || point.ReportScale == 0) problems.Add($"IOA {point.Ioa}: report scale must be finite and non-zero.");

            var isStatusType = point.Type is Iec104PointType.SinglePoint or Iec104PointType.DoublePoint;
            if (isStatusType && tag.DataType != DataType.Bool)
                problems.Add($"IOA {point.Ioa} ('{tag.Path}'): {point.Type} carries a status bit, but the tag is {tag.DataType}.");
            if (!isStatusType && tag.DataType == DataType.Bool)
                problems.Add($"IOA {point.Ioa} ('{tag.Path}'): {point.Type} carries a measurand, but the tag is Bool — use SinglePoint or DoublePoint.");
            var isStatusCommand = point.Command is Iec104CommandType.SingleCommand or Iec104CommandType.DoubleCommand;
            if (isStatusCommand && tag.DataType != DataType.Bool)
                problems.Add($"IOA {point.Ioa} ('{tag.Path}'): {point.Command} writes a status bit, but the tag is {tag.DataType}.");
        }
        return problems;
    }

    /// <summary>
    /// The two things about a set of formulas that cannot be seen from any one of them: a reference
    /// to a tag that does not exist, and a circle.
    ///
    /// <para>
    /// Both are refused at save time on purpose. A dead reference would only show up as a Bad tag
    /// once the gateway is running — typically after a rename, and typically noticed by whoever is
    /// looking at the number it feeds rather than by whoever renamed the tag. A circle has no answer
    /// at all, and the alternative to refusing it is evaluating one member against last pass's value,
    /// which turns a configuration mistake into a drifting number that looks like a measurement.
    /// </para>
    /// </summary>
    /// <param name="focus">
    /// When set, only problems involving that tag path are reported. Used when a single tag is being
    /// saved: a dead reference somewhere else in the project is a real problem, but refusing this
    /// edit because of it would leave the operator unable to fix anything.
    /// </param>
    private static void ValidateFormulas(
        IReadOnlyDictionary<string, IReadOnlySet<string>> formulas,
        IReadOnlyList<TagDto> tags,
        IReadOnlyDictionary<string, string> paths,
        List<string> problems,
        string? focus = null)
    {
        if (formulas.Count == 0)
            return;

        var known = tags
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .Select(t => TagPaths.TagPath(paths, t.GroupId, t.Name.Trim()))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (path, references) in formulas.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (focus is not null && !string.Equals(path, focus, StringComparison.Ordinal))
                continue;
            foreach (var reference in references.Where(r => !known.Contains(r)).OrderBy(r => r, StringComparer.Ordinal))
                problems.Add($"Tag '{path}' reads '{reference}', which does not exist.");
        }

        // Depth-first with three states: 0 not seen, 1 on the current path, 2 finished. Meeting a tag
        // that is still on the current path is the cycle, and the path itself names its members.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();

        foreach (var path in formulas.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (state.GetValueOrDefault(path) == 0)
                Visit(path);
        }

        void Visit(string node)
        {
            state[node] = 1;
            stack.Add(node);

            if (formulas.TryGetValue(node, out var references))
            {
                foreach (var next in references.Where(formulas.ContainsKey).OrderBy(r => r, StringComparer.Ordinal))
                {
                    switch (state.GetValueOrDefault(next))
                    {
                        case 1:
                            var members = stack.Skip(stack.IndexOf(next)).Append(next).ToList();
                            if (focus is null || members.Contains(focus, StringComparer.Ordinal))
                                problems.Add("Circular reference between expression tags: " +
                                             string.Join(" → ", members) + ".");
                            break;
                        case 0:
                            Visit(next);
                            break;
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[node] = 2;
        }
    }

    private static void ValidateDeviceShape(DeviceDto dto, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(dto.Id)) problems.Add("Device ID is required.");
        if (string.IsNullOrWhiteSpace(dto.Driver)) problems.Add("Driver type is required.");
        if (dto.ScanIntervalMs is < 50 or > 3_600_000)
            problems.Add("Scan interval must be between 50 and 3600000 ms.");
        if (dto.FailureThreshold is < 1 or > 100)
            problems.Add("Failure threshold must be between 1 and 100.");
    }

    /// <summary>
    /// Invalid intervals have already been reported by <see cref="ValidateDeviceShape"/>. Harmless
    /// values are used here so the driver's own errors are still collected, instead of letting
    /// TimeSpan construction hide them behind an exception.
    /// </summary>
    private static DeviceConfig BuildConfig(DeviceDto dto, DriverDescriptor descriptor,
        IReadOnlyList<TagDefinition>? tags) => new()
    {
        DeviceId = dto.Id?.Trim() ?? "",
        Driver = dto.Driver?.Trim() ?? "",
        ScanInterval = TimeSpan.FromMilliseconds(dto.ScanIntervalMs is >= 50 and <= 3_600_000
            ? dto.ScanIntervalMs : 1000),
        FailureThreshold = dto.FailureThreshold is >= 1 and <= 100 ? dto.FailureThreshold : 3,
        Settings = SanitizeSecrets(dto.Settings ?? new Dictionary<string, string>(), descriptor),
        Tags = tags ?? Array.Empty<TagDefinition>(),
    };

    /// <summary>
    /// Asks each driver about its connections as a group. A shared link belongs to no single
    /// connection, so a conflict over one — two RS-485 connections that disagree about the baud rate
    /// of the port they both name — cannot be seen by validating them one at a time.
    /// </summary>
    private void ValidateAcrossDevices(IReadOnlyList<DeviceDto> devices,
        IReadOnlyDictionary<string, List<TagDefinition>> tagsByDevice, List<string> problems)
    {
        foreach (var group in devices
                     .Where(d => !string.IsNullOrWhiteSpace(d.Driver))
                     .GroupBy(d => d.Driver!.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (!_factories.TryGetValue(group.Key, out var factory))
                continue;   // the unknown driver has already been reported per device

            var descriptor = SafeDescribe(factory);
            var configs = group
                .Select(dto => BuildConfig(dto, descriptor, tagsByDevice.GetValueOrDefault(dto.Id ?? "")))
                .ToList();

            try
            {
                problems.AddRange(factory.ValidateTogether(configs));
            }
            catch (Exception ex)
            {
                problems.Add($"Driver '{group.Key}' cross-connection validation failed: {ex.Message}");
            }
        }
    }

    private static DriverDescriptor SafeDescribe(IDeviceDriverFactory factory)
    {
        try { return factory.Describe(); }
        catch { return DriverDescriptor.Minimal(factory.DriverType); }
    }

    private static void ValidateSettings(IReadOnlyDictionary<string, string> settings,
        DriverDescriptor descriptor, List<string> problems)
    {
        if (descriptor.Settings.Count > 0)
        {
            foreach (var unknown in settings.Keys.Where(k => !descriptor.Knows(k)))
                problems.Add($"Unknown setting '{unknown}' for driver '{descriptor.DriverType}'.");
        }

        foreach (var setting in descriptor.Settings)
        {
            var supplied = settings.TryGetValue(setting.Key, out var raw) ? raw?.Trim() : null;
            var value = string.IsNullOrWhiteSpace(supplied) ? setting.Default : supplied;
            if (setting.Required && string.IsNullOrWhiteSpace(value))
            {
                problems.Add($"Setting '{setting.Key}' is required.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(value) || SecretRef.IsReference(value) || setting.IsSecret)
                continue;

            switch (setting.Kind)
            {
                case DriverSettingKind.Number:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number))
                        problems.Add($"Setting '{setting.Key}' must be a finite number.");
                    else
                    {
                        if (setting.Min is { } min && number < min) problems.Add($"Setting '{setting.Key}' must be at least {min}.");
                        if (setting.Max is { } max && number > max) problems.Add($"Setting '{setting.Key}' must be at most {max}.");
                    }
                    break;
                case DriverSettingKind.Bool:
                    if (!bool.TryParse(value, out _)) problems.Add($"Setting '{setting.Key}' must be true or false.");
                    break;
                case DriverSettingKind.Choice:
                    if (!setting.Choices.Any(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)))
                        problems.Add($"Setting '{setting.Key}' must be one of: {string.Join(", ", setting.Choices)}.");
                    break;
            }
        }
    }

    private static IReadOnlyDictionary<string, string> SanitizeSecrets(IReadOnlyDictionary<string, string> settings,
        DriverDescriptor descriptor)
    {
        if (!descriptor.Settings.Any(s => s.IsSecret)) return new Dictionary<string, string>(settings);
        var copy = new Dictionary<string, string>(settings);
        foreach (var secret in descriptor.Settings.Where(s => s.IsSecret))
            if (copy.TryGetValue(secret.Key, out var value) && !SecretRef.IsReference(value) && !string.IsNullOrWhiteSpace(value))
                copy[secret.Key] = new SecretRef(SecretSource.Store, "validation-only").ToString();
        return copy;
    }

    private static void ValidateDriverTags(IDeviceDriverFactory factory, DriverDescriptor descriptor,
        DeviceConfig config, List<string> problems)
    {
        foreach (var tag in config.Tags)
            if (descriptor.SupportedTypes.Count > 0 && !descriptor.SupportedTypes.Contains(tag.DataType))
                problems.Add($"Tag '{tag.Id}' uses unsupported type {tag.DataType}.");
        try
        {
            foreach (var (tag, reason) in factory.ValidateTags(config))
                problems.Add($"Tag '{tag}': {reason}");
        }
        catch (Exception ex)
        {
            problems.Add($"Driver tag validation failed: {ex.Message}");
        }
    }

    private static void ValidateGroupCycles(IReadOnlyList<TagGroupDto> groups,
        IReadOnlyDictionary<string, TagGroupDto> byId, List<string> problems)
    {
        foreach (var group in groups)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = group;
            while (current is not null)
            {
                if (!seen.Add(current.Id))
                {
                    problems.Add($"Folder hierarchy contains a cycle involving '{current.Id}'.");
                    break;
                }
                current = current.ParentId is not null && byId.TryGetValue(current.ParentId, out var parent)
                    ? parent : null;
            }
        }
    }

    private static void AddDuplicates<T>(IEnumerable<T> values, Func<T, string> key,
        IEqualityComparer<string> comparer, Func<string, string> message, List<string> problems)
    {
        foreach (var duplicate in values.Where(v => !string.IsNullOrWhiteSpace(key(v)))
                     .GroupBy(key, comparer).Where(g => g.Count() > 1))
            problems.Add(message(duplicate.Key));
    }

    private sealed class FolderNameComparer : IEqualityComparer<(string? ParentId, string Name)>
    {
        public bool Equals((string? ParentId, string Name) x, (string? ParentId, string Name) y) =>
            string.Equals(x.ParentId, y.ParentId, StringComparison.Ordinal)
            && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string? ParentId, string Name) obj) =>
            HashCode.Combine(obj.ParentId is null ? 0 : StringComparer.Ordinal.GetHashCode(obj.ParentId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
    }
}
