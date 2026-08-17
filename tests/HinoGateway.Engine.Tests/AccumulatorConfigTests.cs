using HinoGateway.Core;
using HinoGateway.Server;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// How an accumulator is declared, checked and stored. The stored half matters as much as the
/// arithmetic: a source reference that breaks when a folder is renamed would silently disconnect the
/// tag that reports yesterday's generation to the utility.
/// </summary>
public class AccumulatorConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public AccumulatorConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hino-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "hino.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private JsonProjectStore NewStore() => new(_path, NullLogger<JsonProjectStore>.Instance);

    private static TagDto Counter(string id = "t-counter", string name = "Inv1Energy",
        string? groupId = null, TagKind kind = TagKind.Device) => new()
    {
        Id = id, Name = name, GroupId = groupId, DeviceId = kind == TagKind.Device ? "PLC" : "",
        Kind = kind, Address = "DB1[144]", DataType = DataType.UInt32, Scale = 0.01, Unit = "kWh",
    };

    private static TagDto Total(string sourceKey = "t-counter",
        AccumulationPeriod period = AccumulationPeriod.PreviousDay) => new()
    {
        Id = "t-yesterday", Name = "Inv1EnergyYesterday", Kind = TagKind.Accumulator,
        DataType = DataType.Float64, Unit = "kWh",
        SourceTagKey = sourceKey, Period = period, RolloverAt = 42949672.95, MaxRatePerHour = 500,
    };

    // --- storage ---------------------------------------------------------------

    [Fact]
    public async Task An_accumulator_survives_a_restart_with_every_field_intact()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(new DeviceConfig
        {
            DeviceId = "PLC", Driver = "SiemensS7", ScanInterval = TimeSpan.FromSeconds(1),
        });
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Inverter" });
        await store.UpsertTagAsync(Counter(groupId: "g1"));
        await store.UpsertTagAsync(Total());

        var reloaded = (await NewStore().GetTagsAsync()).Single(t => t.Kind == TagKind.Accumulator);

        Assert.Equal("t-counter", reloaded.SourceTagKey);
        Assert.Equal("Inverter/Inv1Energy", reloaded.SourcePath);
        Assert.Equal(AccumulationPeriod.PreviousDay, reloaded.Period);
        Assert.Equal(42949672.95, reloaded.RolloverAt);
        Assert.Equal(500, reloaded.MaxRatePerHour);
    }

    /// <summary>
    /// The reason the reference is stored as a key and not as a path. Renaming the folder must move
    /// the counter without disconnecting the total that is reported to the control centre.
    /// </summary>
    [Fact]
    public async Task Renaming_the_source_folder_does_not_break_the_accumulator()
    {
        var store = NewStore();
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Inverter" });
        await store.UpsertTagAsync(Counter(groupId: "g1"));
        await store.UpsertTagAsync(Total());

        await store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Inverter 1" });

        var definition = (await store.GetInternalTagsAsync()).Single(t => t.Kind == TagKind.Accumulator);
        Assert.Equal("Inverter 1/Inv1Energy", definition.SourceTagId);
        Assert.Equal("t-counter", definition.SourceTagKey);
    }

    [Fact]
    public async Task The_engine_never_sees_a_writable_accumulator()
    {
        var store = NewStore();
        await store.UpsertTagAsync(Counter());

        var total = Total();
        total.AllowWrite = true;   // whatever a hand-edited project file says
        await store.UpsertTagAsync(total);

        var definition = (await store.GetInternalTagsAsync()).Single(t => t.Kind == TagKind.Accumulator);
        Assert.False(definition.AllowWrite);
    }

    [Fact]
    public async Task A_version_7_project_still_loads_and_is_written_back_as_version_8()
    {
        await File.WriteAllTextAsync(_path, """
            {
              "Format": "hino-project",
              "Version": 7,
              "Devices": [], "Groups": [],
              "Tags": [ { "Id": "t1", "Name": "Temp", "Kind": 0, "DeviceId": "", "Address": "1" } ],
              "Alarms": []
            }
            """);

        var store = NewStore();
        Assert.Single(await store.GetTagsAsync());

        await store.UpsertTagAsync(Counter());
        Assert.Contains($"\"Version\": {HinoProject.CurrentVersion}", await File.ReadAllTextAsync(_path));
        Assert.Equal(8, HinoProject.CurrentVersion);
    }

    // --- validation ------------------------------------------------------------

    private static IReadOnlyList<string> Validate(params TagDto[] tags) =>
        new ProjectValidator([]).Validate(new HinoProject { Tags = tags.ToList() });

    [Fact]
    public void An_accumulator_pointing_at_nothing_is_refused()
    {
        var orphan = Total(sourceKey: "t-missing");
        Assert.Contains(Validate(Counter(), orphan), p => p.Contains("does not exist"));

        var sourceless = Total();
        sourceless.SourceTagKey = null;
        Assert.Contains(Validate(Counter(), sourceless), p => p.Contains("no source counter is selected"));
    }

    /// <summary>
    /// An expression source would let the two internal engines feed each other in a loop that neither
    /// one's ordering can see — and a counter that is computed rather than read is not the thing this
    /// design rests on, which is a register that keeps counting while the gateway is switched off.
    /// </summary>
    [Fact]
    public void An_accumulator_may_not_count_a_computed_value()
    {
        var computed = new TagDto
        {
            Id = "t-computed", Name = "Total", Kind = TagKind.Expression,
            Expression = "{A} + {B}", DataType = DataType.Float64,
        };
        Assert.Contains(Validate(computed, Total(sourceKey: "t-computed")),
            p => p.Contains("never a computed value"));
    }

    [Fact]
    public void An_accumulator_may_not_count_itself_or_a_non_numeric_tag()
    {
        Assert.Contains(Validate(Counter(), Total(sourceKey: "t-yesterday")),
            p => p.Contains("cannot count itself"));

        var text = Counter(id: "t-text", name: "Mode");
        text.DataType = DataType.String;
        Assert.Contains(Validate(text, Total(sourceKey: "t-text")), p => p.Contains("not a counter"));
    }

    [Fact]
    public void An_accumulator_cannot_be_made_writable_or_given_nonsense_limits()
    {
        var writable = Total();
        writable.AllowWrite = true;
        Assert.Contains(Validate(Counter(), writable), p => p.Contains("cannot be writable"));

        var negative = Total();
        negative.RolloverAt = -1;
        Assert.Contains(Validate(Counter(), negative), p => p.Contains("rollover point"));

        var zeroRate = Total();
        zeroRate.MaxRatePerHour = 0;
        Assert.Contains(Validate(Counter(), zeroRate), p => p.Contains("maximum rate per hour"));
    }

    [Fact]
    public void A_correctly_declared_accumulator_passes()
    {
        var problems = Validate(Counter(kind: TagKind.Memory), Total());
        Assert.Empty(problems);
    }
}
