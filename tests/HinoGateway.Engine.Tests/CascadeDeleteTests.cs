using HinoGateway.Core;
using HinoGateway.Server.Services;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// Deleting used to be refused while anything still pointed at the tag, which left an operator
/// removing two hundred tags by hand before a connection could go. Now the dependants go with it.
///
/// <para>
/// These tests exist as much for the old silent failure as for the new behaviour: <c>DeleteTag</c>
/// checked alarms but never IEC-104 points, so deleting a mapped tag succeeded and every later apply
/// died with "IOA 102 references a tag that does not exist" — with nothing on screen connecting the
/// two events.
/// </para>
/// </summary>
public class CascadeDeleteTests : IDisposable
{
    private readonly string _dir;
    private readonly JsonProjectStore _store;

    public CascadeDeleteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hino-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new JsonProjectStore(Path.Combine(_dir, "hino.json"), NullLogger<JsonProjectStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static TagDto Tag(string id, string name, string? groupId = null,
        TagKind kind = TagKind.Device, DataType type = DataType.Float64) => new()
        {
            Id = id, Name = name, GroupId = groupId, Kind = kind,
            DeviceId = kind == TagKind.Device ? "MB-1" : "",
            Address = kind == TagKind.Device ? "40001" : "",
            DataType = type, AllowWrite = true,
        };

    private async Task SeedAsync()
    {
        await _store.UpsertDeviceAsync(new DeviceConfig
        {
            DeviceId = "MB-1", Driver = "ModbusTCP", ScanInterval = TimeSpan.FromMilliseconds(500),
            Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1", ["Port"] = "502" },
        });
        await _store.UpsertGroupAsync(new TagGroupDto { Id = "g1", Name = "Plant" });
        await _store.UpsertTagAsync(Tag("t-power", "Power", "g1"));
        await _store.UpsertTagAsync(Tag("t-other", "Voltage", "g1"));
        await _store.UpsertAlarmAsync(new AlarmDto
        {
            Id = "a1", Name = "Power high", TagId = "t-power",
            Condition = AlarmCondition.Hi, Limit = 100,
        });
        var iec = await _store.GetIec104Async();
        iec.Points.Add(new Iec104PointDto { TagId = "t-power", Ioa = 102, Type = Iec104PointType.ShortFloat });
        iec.Points.Add(new Iec104PointDto { TagId = "t-other", Ioa = 103, Type = Iec104PointType.ShortFloat });
        await _store.SaveIec104Async(iec);
    }

    [Fact]
    public async Task Deleting_a_tag_takes_its_alarms_and_its_iec104_points()
    {
        await SeedAsync();

        var result = await CascadeDelete.RemoveTagsAsync(_store, _store, new[] { "t-power" }, default);

        Assert.Equal(1, result.Tags);
        Assert.Equal(1, result.Alarms);
        Assert.Equal(1, result.Iec104Points);

        Assert.DoesNotContain(await _store.GetTagsAsync(), t => t.Id == "t-power");
        Assert.Empty(await _store.GetAlarmsAsync());
        var points = (await _store.GetIec104Async()).Points;
        Assert.Equal(new[] { 103 }, points.Select(p => p.Ioa));
    }

    [Fact]
    public async Task What_belongs_to_other_tags_is_left_alone()
    {
        await SeedAsync();
        await _store.UpsertAlarmAsync(new AlarmDto
        {
            Id = "a2", Name = "Voltage high", TagId = "t-other", Condition = AlarmCondition.Hi, Limit = 250,
        });

        await CascadeDelete.RemoveTagsAsync(_store, _store, new[] { "t-power" }, default);

        Assert.Equal(new[] { "a2" }, (await _store.GetAlarmsAsync()).Select(a => a.Id));
        Assert.Contains(await _store.GetTagsAsync(), t => t.Id == "t-other");
    }

    [Fact]
    public async Task An_accumulator_goes_with_the_counter_it_adds_up()
    {
        await SeedAsync();
        var accumulator = Tag("t-daily", "Energy today", "g1", TagKind.Accumulator);
        accumulator.SourceTagKey = "t-power";
        accumulator.AllowWrite = false;
        await _store.UpsertTagAsync(accumulator);

        var result = await CascadeDelete.RemoveTagsAsync(_store, _store, new[] { "t-power" }, default);

        // Two tags: the counter itself and the total that only existed to add it up.
        Assert.Equal(2, result.Tags);
        Assert.Equal(1, result.Accumulators);
        Assert.DoesNotContain(await _store.GetTagsAsync(), t => t.Id == "t-daily");
    }

    /// <summary>
    /// A formula is the operator's own work and may read ten tags, only one of which is going, so it
    /// is reported rather than deleted. It still has to be reported: the project will not apply until
    /// somebody fixes it, and finding out at that point is finding out too late.
    /// </summary>
    [Fact]
    public async Task A_formula_reading_the_deleted_tag_is_reported_but_not_deleted()
    {
        await SeedAsync();
        var formula = Tag("t-sum", "Total", "g1", TagKind.Expression);
        formula.Expression = "{Plant/Power} + {Plant/Voltage}";
        formula.AllowWrite = false;
        await _store.UpsertTagAsync(formula);

        var result = await CascadeDelete.RemoveTagsAsync(_store, _store, new[] { "t-power" }, default);

        Assert.Equal(new[] { "Plant/Total" }, result.BrokenFormulas);
        Assert.Contains(await _store.GetTagsAsync(), t => t.Id == "t-sum");
    }

    [Fact]
    public async Task A_preview_reports_the_same_numbers_and_changes_nothing()
    {
        await SeedAsync();

        var preview = await CascadeDelete.PreviewAsync(_store, _store, new[] { "t-power" }, default);

        Assert.Equal(1, preview.Tags);
        Assert.Equal(1, preview.Alarms);
        Assert.Equal(1, preview.Iec104Points);
        Assert.Equal(2, (await _store.GetTagsAsync()).Count);
        Assert.Single(await _store.GetAlarmsAsync());
        Assert.Equal(2, (await _store.GetIec104Async()).Points.Count);
    }

    [Fact]
    public async Task Deleting_a_tag_that_does_not_exist_removes_nothing()
    {
        await SeedAsync();

        var result = await CascadeDelete.RemoveTagsAsync(_store, _store, new[] { "no-such-tag" }, default);

        Assert.Equal(0, result.Tags);
        Assert.Equal(2, (await _store.GetTagsAsync()).Count);
    }
}
