using HinoGateway.Core;
using HinoGateway.Server;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// Tag kinds as they are stored and validated: what a project file carries, what an older one does
/// when this version reads it, and which configurations are refused before they can reach the engine.
/// </summary>
public class TagKindStorageTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;

    public TagKindStorageTests()
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
        ScanInterval = TimeSpan.FromSeconds(1),
        Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1", ["Port"] = "502" },
    };

    [Fact]
    public async Task Memory_and_expression_tags_survive_a_restart_with_their_settings()
    {
        var store = NewStore();
        await store.UpsertGroupAsync(new TagGroupDto { Id = "g", Name = "Plant" });
        await store.UpsertTagAsync(new TagDto
        {
            Id = "m", Name = "Setpoint", GroupId = "g", Kind = TagKind.Memory,
            InitialValue = "20", Retentive = true, DataType = DataType.Float64,
        });
        await store.UpsertTagAsync(new TagDto
        {
            Id = "e", Name = "Total", GroupId = "g", Kind = TagKind.Expression,
            Expression = "{Plant/Setpoint} * 2", EvalMode = EvalMode.OnChange, ScanIntervalMs = 500,
        });

        var internals = await NewStore().GetInternalTagsAsync();

        var memory = Assert.Single(internals, t => t.Kind == TagKind.Memory);
        Assert.Equal("Plant/Setpoint", memory.Id);   // runtime id is the folder path, as for device tags
        Assert.Equal("20", memory.InitialValue);
        Assert.True(memory.Retentive);

        var expression = Assert.Single(internals, t => t.Kind == TagKind.Expression);
        Assert.Equal("{Plant/Setpoint} * 2", expression.Expression);
        Assert.Equal(EvalMode.OnChange, expression.EvalMode);
        Assert.Equal(TimeSpan.FromMilliseconds(500), expression.ScanInterval);
    }

    /// <summary>
    /// An internal tag has no connection, and a driver asked to read one would try to poll an empty
    /// address. Filtering by kind (rather than trusting an empty DeviceId) also covers a tag that was
    /// converted from a device tag and kept the connection it used to have.
    /// </summary>
    [Fact]
    public async Task Internal_tags_are_never_handed_to_a_connection()
    {
        var store = NewStore();
        await store.UpsertDeviceAsync(Device());
        await store.UpsertTagAsync(new TagDto
        {
            Id = "d", Name = "Temp", DeviceId = "MB-1", Address = "40001", DataType = DataType.Int16,
        });
        await store.UpsertTagAsync(new TagDto
        {
            Id = "m", Name = "Setpoint", DeviceId = "MB-1", Kind = TagKind.Memory, InitialValue = "5",
        });

        var device = Assert.Single(await store.GetDevicesAsync());
        Assert.Equal(new[] { "Temp" }, device.Tags.Select(t => t.Name));
        Assert.Single(await store.GetInternalTagsAsync());
    }

    /// <summary>A project written before tag kinds existed has none, and every tag is a device tag.</summary>
    [Fact]
    public async Task A_version_5_project_file_still_loads()
    {
        // Property names as a version-5 gateway wrote them (the serializer uses the CLR names).
        File.WriteAllText(_path, """
        {
          "Format": "hino-project",
          "Version": 5,
          "Devices": [ { "Id": "MB-1", "Driver": "ModbusTCP", "ScanIntervalMs": 1000,
                         "FailureThreshold": 3, "Settings": { "Host": "127.0.0.1", "Port": "502" },
                         "Tags": [] } ],
          "Groups": [ { "Id": "g", "ParentId": null, "Name": "Plant" } ],
          "Tags": [ { "Id": "t", "GroupId": "g", "DeviceId": "MB-1", "Name": "Temp",
                      "Address": "40001", "DataType": 1, "Scale": 1, "Offset": 0 } ],
          "Alarms": []
        }
        """);

        var store = NewStore();
        var tag = Assert.Single(await store.GetTagsAsync());

        Assert.Equal(TagKind.Device, tag.Kind);
        Assert.Empty(await store.GetInternalTagsAsync());
        Assert.Single((await store.GetDevicesAsync()).Single().Tags);
    }

    // --- validation -----------------------------------------------------------

    private static IReadOnlyList<string> Validate(params TagDto[] tags) =>
        new ProjectValidator(Array.Empty<IDeviceDriverFactory>()).Validate(new HinoProject
        {
            Groups = [new TagGroupDto { Id = "g", Name = "Plant" }],
            Tags = tags.ToList(),
        });

    private static TagDto Expression(string name, string formula) => new()
    {
        Id = name, Name = name, GroupId = "g", Kind = TagKind.Expression, Expression = formula,
    };

    /// <summary>
    /// Address and connection are required of a device tag and meaningless for the others. Before
    /// this was per kind, a memory tag simply could not be saved.
    /// </summary>
    [Fact]
    public void A_memory_tag_needs_neither_address_nor_connection()
    {
        var problems = Validate(new TagDto
        {
            Id = "m", Name = "Setpoint", GroupId = "g", Kind = TagKind.Memory, InitialValue = "20",
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void An_initial_value_that_does_not_fit_the_type_is_refused()
    {
        var problems = Validate(new TagDto
        {
            Id = "m", Name = "Count", GroupId = "g", Kind = TagKind.Memory,
            DataType = DataType.Int16, InitialValue = "70000",
        });

        Assert.Contains(problems, p => p.Contains("Plant/Count") && p.Contains("Int16"));
    }

    [Fact]
    public void A_formula_that_does_not_compile_is_refused_with_its_position()
    {
        var problems = Validate(Expression("Total", "{A} +"));
        Assert.Contains(problems, p => p.Contains("Plant/Total") && p.Contains("character"));
    }

    [Fact]
    public void A_formula_naming_a_tag_that_does_not_exist_is_refused()
    {
        var problems = Validate(Expression("Total", "{Plant/Nowhere} * 2"));
        Assert.Contains(problems, p => p.Contains("Plant/Nowhere") && p.Contains("does not exist"));
    }

    [Fact]
    public void A_circle_between_formulas_is_refused_before_it_can_be_saved()
    {
        var problems = Validate(
            Expression("A", "{Plant/B} + 1"),
            Expression("B", "{Plant/A} + 1"));

        Assert.Contains(problems, p => p.StartsWith("Circular reference"));
    }

    [Fact]
    public void A_formula_reading_itself_is_a_circle_of_one()
    {
        var problems = Validate(Expression("A", "{Plant/A} + 1"));
        Assert.Contains(problems, p => p.StartsWith("Circular reference"));
    }

    [Fact]
    public void An_evaluation_period_below_the_floor_is_refused()
    {
        var tag = Expression("Fast", "1 + 1");
        tag.ScanIntervalMs = 10;

        Assert.Contains(Validate(tag), p => p.Contains("fastest allowed"));
    }

    /// <summary>
    /// Saving one tag must not be blocked by a problem somewhere else in the project: the operator
    /// would have no way out of it.
    /// </summary>
    [Fact]
    public void Saving_one_tag_only_reports_that_tag_s_problems()
    {
        var edited = Expression("Good", "1 + 1");
        var broken = Expression("Broken", "{Plant/Nowhere} + 1");
        var groups = new List<TagGroupDto> { new() { Id = "g", Name = "Plant" } };

        Assert.Empty(ProjectValidator.ValidateInternalTag(edited, [edited, broken], groups));
        Assert.NotEmpty(ProjectValidator.ValidateInternalTag(broken, [edited, broken], groups));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
