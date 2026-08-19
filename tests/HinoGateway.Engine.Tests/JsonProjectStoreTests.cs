using HinoGateway.Core;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The gateway keeps its whole configuration in one JSON file, so these tests exercise the real
/// thing on disk: what survives a restart, what the file looks like, and that a crash mid-write
/// cannot leave the gateway without a configuration.
/// </summary>
public class JsonProjectStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public JsonProjectStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hino-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "hino.json");
    }

    private JsonProjectStore NewStore() => new(_path, NullLogger<JsonProjectStore>.Instance);

    private static DeviceConfig Device(string id = "MB-1") => new()
    {
        DeviceId = id,
        Driver = "ModbusTCP",
        ScanInterval = TimeSpan.FromMilliseconds(500),
        Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1", ["Port"] = "502" },
    };

    private static TagDto Tag(string id, string name, string? groupId = null, string device = "MB-1") => new()
    {
        Id = id, Name = name, GroupId = groupId, DeviceId = device,
        Address = "40001", DataType = DataType.Int16, Unit = "kg", Scale = 0.1,
    };

    [Fact]
    public async Task Configuration_survives_a_restart()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Area 1" });
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g2", ParentId = "g1", Name = "Line 2" });
        await store.UpsertTagAsync(Tag("t1", "Temp", "g2"));
        await store.UpsertAlarmAsync(new AlarmDto
        {
            Id = "a1", Name = "Temp high", TagId = "t1",
            Condition = AlarmCondition.HiHi, Limit = 90, Severity = AlarmSeverity.Critical,
        });

        // A new instance reads the file the previous one wrote — as after a gateway restart.
        var reloaded = NewStore();

        var devices = await reloaded.GetDevicesAsync();
        Assert.Single(devices);
        Assert.Equal("ModbusTCP", devices[0].Driver);
        Assert.Equal(TimeSpan.FromMilliseconds(500), devices[0].ScanInterval);
        Assert.Equal("127.0.0.1", devices[0].Settings["Host"]);

        // The runtime tag id is the folder path, rebuilt on read.
        Assert.Equal("Area 1/Line 2/Temp", devices[0].Tags.Single().Id);
        Assert.Equal(0.1, devices[0].Tags.Single().Scale);

        var alarm = (await reloaded.GetAlarmsAsync()).Single();
        Assert.Equal("Area 1/Line 2/Temp", alarm.TagPath);
        Assert.Equal(AlarmCondition.HiHi, alarm.Condition);
        Assert.Equal(90, alarm.Limit);
    }

    [Fact]
    public async Task The_file_is_a_hino_project_that_can_be_read_by_hand()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());

        var json = await File.ReadAllTextAsync(_path);

        Assert.Contains("\"Format\": \"hino-project\"", json);
        Assert.Contains($"\"Version\": {HinoProject.CurrentVersion}", json);
        Assert.Contains("MB-1", json);
        Assert.Contains("\n", json);   // indented, not minified
    }

    /// <summary>
    /// Editing a tag used to drop its command authorisation, because the update path copied every
    /// field except the three that decide whether a northbound command may be written at all. The
    /// symptom in the field was an EVN setpoint that stopped accepting commands the first time
    /// somebody corrected its description.
    /// </summary>
    [Fact]
    public async Task Editing_a_tag_keeps_its_write_permission_and_limits()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());

        var tag = Tag("t1", "Setpoint");
        tag.AllowWrite = true;
        tag.WriteMin = 0;
        tag.WriteMax = 100;
        await store.UpsertTagAsync(tag);

        var edited = Tag("t1", "Setpoint");
        edited.AllowWrite = true;
        edited.WriteMin = 0;
        edited.WriteMax = 100;
        edited.Description = "P setpoint in percent";
        await store.UpsertTagAsync(edited);

        var stored = (await NewStore().GetTagsAsync()).Single();
        Assert.True(stored.AllowWrite);
        Assert.Equal(0, stored.WriteMin);
        Assert.Equal(100, stored.WriteMax);
        Assert.Equal("P setpoint in percent", stored.Description);
    }

    /// <summary>Revoking it must work too — the fix has to copy the value, not just never clear it.</summary>
    [Fact]
    public async Task Revoking_write_permission_on_an_edit_is_persisted()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());

        var tag = Tag("t1", "Setpoint");
        tag.AllowWrite = true;
        tag.WriteMax = 100;
        await store.UpsertTagAsync(tag);

        await store.UpsertTagAsync(Tag("t1", "Setpoint"));   // AllowWrite defaults to false

        var stored = (await NewStore().GetTagsAsync()).Single();
        Assert.False(stored.AllowWrite);
        Assert.Null(stored.WriteMax);
    }

    [Fact]
    public async Task Renaming_a_folder_moves_every_tag_and_alarm_with_it()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Area 1" });
        await store.UpsertTagAsync(Tag("t1", "Temp", "g1"));
        await store.UpsertAlarmAsync(new AlarmDto { Id = "a1", Name = "High", TagId = "t1" });

        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Area 9" });

        Assert.Equal("Area 9/Temp", (await store.GetTagsAsync()).Single().Path);
        Assert.Equal("Area 9/Temp", (await store.GetAlarmsAsync()).Single().TagPath);
    }

    [Fact]
    public async Task Deleting_a_folder_takes_its_subtree_of_tags()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Area 1" });
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g2", ParentId = "g1", Name = "Line 2" });
        await store.UpsertTagAsync(Tag("t1", "Temp", "g2"));
        await store.UpsertTagAsync(Tag("t2", "Loose"));

        Assert.True(await store.DeleteGroupAsync("g1"));

        var tags = await store.GetTagsAsync();
        Assert.Equal("Loose", tags.Single().Path);
        Assert.Empty(await store.GetGroupsAsync());
        Assert.False(await store.DeleteGroupAsync("g1"));
    }

    [Fact]
    public async Task A_committed_transaction_writes_the_file_once_and_serves_reads_while_open()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());          // file exists from here on
        var writesBefore = File.GetLastWriteTimeUtc(_path);

        using (var tx = store.BeginTransaction())
        {
            for (var i = 0; i < 50; i++)
                await store.UpsertTagAsync(Tag($"t{i}", $"Tag{i}"));

            // Reads inside the transaction see the pending state; the file does not yet.
            Assert.Equal(50, (await store.GetTagsAsync()).Count);
            Assert.DoesNotContain("Tag49", await File.ReadAllTextAsync(_path));

            tx.Commit();
        }

        Assert.Contains("Tag49", await File.ReadAllTextAsync(_path));
        Assert.True(File.GetLastWriteTimeUtc(_path) >= writesBefore);
        Assert.Equal(50, (await NewStore().GetTagsAsync()).Count);
    }

    [Fact]
    public async Task An_abandoned_transaction_restores_the_configuration_and_writes_nothing()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());
        await store.UpsertTagAsync(Tag("keep", "Keeper"));
        var fileBefore = await File.ReadAllTextAsync(_path);

        using (store.BeginTransaction())
        {
            await store.DeleteTagAsync("keep");
            await store.UpsertTagAsync(Tag("gone", "Ghost"));
            Assert.Equal("Ghost", (await store.GetTagsAsync()).Single().Name);
            // no Commit() — the scope is abandoned, as it would be if an import threw here
        }

        var tags = await store.GetTagsAsync();
        Assert.Equal("Keeper", tags.Single().Name);
        Assert.Equal(fileBefore, await File.ReadAllTextAsync(_path));
        Assert.Equal("Keeper", (await NewStore().GetTagsAsync()).Single().Name);
    }

    [Fact]
    public async Task A_failing_replace_import_leaves_the_previous_configuration_intact()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device("LIVE-1"));
        await store.UpsertTagAsync(Tag("t1", "Temp", device: "LIVE-1"));

        // The shape of a replace import: wipe everything, then fail while adding the new project.
        try
        {
            using var tx = store.BeginTransaction();
            foreach (var d in await store.GetDevicesAsync())
                await store.DeleteDeviceAsync(d.DeviceId);
            foreach (var t in await store.GetTagsAsync())
                await store.DeleteTagAsync(t.Id);
            throw new InvalidOperationException("import blew up half way");
        }
        catch (InvalidOperationException)
        {
            // expected
        }

        Assert.Equal("LIVE-1", (await store.GetDevicesAsync()).Single().DeviceId);
        Assert.Single(await store.GetTagsAsync());
    }

    [Fact]
    public async Task A_previous_version_is_kept_as_a_backup_on_every_write()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device("FIRST"));
        await store.UpsertDeviceAsync(Device("SECOND"));

        var backup = _path + ".bak";
        Assert.True(File.Exists(backup));
        Assert.Contains("FIRST", await File.ReadAllTextAsync(backup));
    }

    [Fact]
    public void A_corrupt_project_file_refuses_to_start_rather_than_silently_wiping_it()
    {
        File.WriteAllText(_path, "{ this is not json");

        var ex = Assert.Throws<InvalidOperationException>(() => NewStore());

        Assert.Contains(_path, ex.Message);
        // The operator's file is left exactly as it was.
        Assert.Equal("{ this is not json", File.ReadAllText(_path));
    }

    [Fact]
    public void A_project_from_a_newer_gateway_is_refused()
    {
        File.WriteAllText(_path, $$"""
            { "Format": "hino-project", "Version": {{HinoProject.CurrentVersion + 1}} }
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => NewStore());

        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public async Task A_version_1_project_is_lifted_into_folders_per_connection()
    {
        // Version 1 nested each device's tags inside the device.
        File.WriteAllText(_path, """
            {
              "Format": "hino-project",
              "Version": 1,
              "Devices": [
                {
                  "Id": "OLD-1",
                  "Driver": "Simulator",
                  "ScanIntervalMs": 1000,
                  "Tags": [ { "Id": "old-tag", "Name": "Sine", "Address": "sine", "DataType": 5 } ]
                }
              ]
            }
            """);

        var store = NewStore();

        var tag = (await store.GetTagsAsync()).Single();
        Assert.Equal("OLD-1/Sine", tag.Path);
        Assert.Equal("OLD-1", tag.DeviceId);
        Assert.Equal("OLD-1/Sine", (await store.GetDevicesAsync()).Single().Tags.Single().Id);
    }

    [Fact]
    public void Version_6_status_controls_migrate_to_select_before_operate()
    {
        var project = new HinoProject
        {
            Version = 6,
            Iec104 = new Iec104ServerOptions
            {
                Points =
                [
                    new Iec104PointDto { Ioa = 1, Command = Iec104CommandType.SingleCommand },
                    new Iec104PointDto { Ioa = 2, Command = Iec104CommandType.SetpointShort },
                ],
            },
        };

        JsonProjectStore.Normalise(project);

        Assert.Equal(HinoProject.CurrentVersion, project.Version);
        Assert.True(project.Iec104.Points[0].RequireSelect);
        Assert.False(project.Iec104.Points[1].RequireSelect);
    }

    /// <summary>
    /// Version 7 split "commandable" into RequireSelect on the point and AllowWrite on the tag. In a
    /// version 6 file the mapping itself was the authorisation, so both halves have to be carried
    /// across — otherwise the project validator refuses the file and a routine restore from a .hino
    /// backup becomes a dead end.
    /// </summary>
    [Fact]
    public void Version_6_commandable_points_keep_their_tags_writable()
    {
        var project = new HinoProject
        {
            Version = 6,
            Tags =
            [
                new TagDto { Id = "t-breaker", Name = "Breaker" },
                new TagDto { Id = "t-setpoint", Name = "Setpoint" },
                new TagDto { Id = "t-readonly", Name = "Reading" },
            ],
            Iec104 = new Iec104ServerOptions
            {
                Points =
                [
                    new Iec104PointDto { Ioa = 1, TagId = "t-breaker", Command = Iec104CommandType.DoubleCommand },
                    new Iec104PointDto { Ioa = 2, TagId = "t-setpoint", Command = Iec104CommandType.SetpointShort },
                    new Iec104PointDto { Ioa = 3, TagId = "t-readonly", Command = Iec104CommandType.None },
                ],
            },
        };

        JsonProjectStore.Normalise(project);

        Assert.True(project.Tags.Single(t => t.Id == "t-breaker").AllowWrite);
        Assert.True(project.Tags.Single(t => t.Id == "t-setpoint").AllowWrite);
        Assert.False(project.Tags.Single(t => t.Id == "t-readonly").AllowWrite);
    }

    /// <summary>
    /// Carrying the permission across is correct, but doing it quietly is not: whoever restores an
    /// old backup has to be told which points came back writable, by name, or the review the README
    /// asks for cannot actually be performed.
    /// </summary>
    [Fact]
    public void Every_tag_the_upgrade_makes_writable_is_named_in_a_migration_note()
    {
        var project = new HinoProject
        {
            Version = 6,
            Tags =
            [
                new TagDto { Id = "t-breaker", Name = "Breaker 271" },
                new TagDto { Id = "t-setpoint", Name = "Setpoint P" },
                new TagDto { Id = "t-readonly", Name = "Reading" },
            ],
            Iec104 = new Iec104ServerOptions
            {
                Points =
                [
                    new Iec104PointDto { Ioa = 1, TagId = "t-breaker", Command = Iec104CommandType.DoubleCommand },
                    new Iec104PointDto { Ioa = 2, TagId = "t-setpoint", Command = Iec104CommandType.SetpointShort },
                    new Iec104PointDto { Ioa = 3, TagId = "t-readonly", Command = Iec104CommandType.None },
                ],
            },
        };

        JsonProjectStore.Normalise(project, out var notes);

        var note = Assert.Single(notes);
        Assert.Contains("Breaker 271", note);
        Assert.Contains("Setpoint P", note);
        Assert.DoesNotContain("Reading", note);
        Assert.Contains("2 tag(s)", note);
    }

    /// <summary>Nothing changed means nothing to warn about; a note on every load would stop being read.</summary>
    [Fact]
    public void An_upgrade_that_changes_no_permission_says_nothing()
    {
        var project = new HinoProject
        {
            Version = 6,
            Tags = [new TagDto { Id = "t-breaker", Name = "Breaker", AllowWrite = true }],
            Iec104 = new Iec104ServerOptions
            {
                Points = [new Iec104PointDto { Ioa = 1, TagId = "t-breaker", Command = Iec104CommandType.DoubleCommand }],
            },
        };

        JsonProjectStore.Normalise(project, out var notes);

        Assert.Empty(notes);
        Assert.True(project.Tags.Single().AllowWrite);
    }

    /// <summary>A project already at the current version must not have its safety switches touched.</summary>
    [Fact]
    public void Current_version_projects_keep_their_write_switches_as_configured()
    {
        var project = new HinoProject
        {
            Version = HinoProject.CurrentVersion,
            Tags = [new TagDto { Id = "t-breaker", Name = "Breaker", AllowWrite = false }],
            Iec104 = new Iec104ServerOptions
            {
                Points = [new Iec104PointDto { Ioa = 1, TagId = "t-breaker", Command = Iec104CommandType.DoubleCommand }],
            },
        };

        JsonProjectStore.Normalise(project);

        Assert.False(project.Tags.Single().AllowWrite);
    }

    [Fact]
    public async Task Callers_cannot_mutate_the_stored_configuration_behind_the_stores_back()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());
        await store.UpsertTagAsync(Tag("t1", "Temp"));

        var tag = (await store.GetTagsAsync()).Single();
        tag.Name = "Tampered";

        Assert.Equal("Temp", (await store.GetTagsAsync()).Single().Name);
    }

    [Fact]
    public async Task An_empty_store_reports_itself_as_fresh_and_writes_no_file()
    {
        var store = NewStore();

        Assert.True(store.IsEmpty);
        Assert.False(File.Exists(_path));

        await store.UpsertDeviceAsync(Device());
        Assert.False(store.IsEmpty);
        Assert.True(File.Exists(_path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>The local alarm trail is deliberately bounded and in memory — a gateway, not a historian.</summary>
public class InMemoryAlarmJournalTests
{
    private static AlarmEvent Event(DateTimeOffset at, string alarmId = "a1") =>
        new(0, at, alarmId, "High", "Area/Temp", AlarmCondition.Hi, AlarmSeverity.Major,
            AlarmEventType.Activated, 91.5, "too high", null);

    [Fact]
    public async Task Events_come_back_newest_first_with_stable_ids()
    {
        var journal = new InMemoryAlarmJournal();
        var now = DateTimeOffset.UtcNow;

        await journal.AppendAsync([Event(now.AddMinutes(-2)), Event(now.AddMinutes(-1)), Event(now)]);

        var all = await journal.GetAsync(null, null, null, 10);
        Assert.Equal(3, all.Count);
        Assert.Equal(3, all[0].Id);
        Assert.True(all[0].Timestamp > all[1].Timestamp);
    }

    [Fact]
    public async Task Filters_by_window_and_alarm()
    {
        var journal = new InMemoryAlarmJournal();
        var now = DateTimeOffset.UtcNow;
        await journal.AppendAsync([Event(now.AddHours(-48)), Event(now.AddHours(-2), "a2"), Event(now)]);

        Assert.Equal(2, (await journal.GetAsync(now.AddHours(-24), null, null, 10)).Count);
        Assert.Single(await journal.GetAsync(null, null, "a2", 10));
        Assert.Single(await journal.GetAsync(null, null, null, 1));
    }

    [Fact]
    public async Task The_trail_stays_bounded_and_reports_what_rolled_off()
    {
        var journal = new InMemoryAlarmJournal(capacity: 50);
        var now = DateTimeOffset.UtcNow;

        for (var i = 0; i < 80; i++)
            await journal.AppendAsync([Event(now.AddSeconds(i))]);

        var (count, dropped) = journal.Stats;
        Assert.Equal(50, count);
        Assert.Equal(30, dropped);
        Assert.Equal(80, (await journal.GetAsync(null, null, null, 100))[0].Id);   // newest kept
    }
}
