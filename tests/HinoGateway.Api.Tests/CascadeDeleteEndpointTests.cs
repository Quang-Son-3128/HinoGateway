using System.Net;
using System.Net.Http.Json;
using HinoGateway.Core;

namespace HinoGateway.Api.Tests;

/// <summary>
/// The delete endpoints, over real HTTP, with the thing they broke: an apply straight afterwards.
///
/// <para>
/// Deleting a tag that an IEC-104 point mapped used to return 204 and leave the point behind. The
/// damage only appeared at the next apply, which refused the whole project — including every
/// unrelated edit made since — with "IOA 102 references a tag that does not exist". With Save now
/// applying by itself, that error would arrive on the very next keystroke, so the cascade is not a
/// convenience here: it is what makes automatic apply safe.
/// </para>
/// </summary>
public class CascadeDeleteEndpointTests
{
    private static async Task SeedAsync(HttpClient client)
    {
        var device = await client.PutAsJsonAsync("/api/config/devices/MB-1", new
        {
            Id = "MB-1",
            Driver = "Simulator",
            ScanIntervalMs = 500,
            Settings = new Dictionary<string, string>(),
        });
        device.EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/config/groups/g1", new { Id = "g1", Name = "Plant" }))
            .EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/config/tags/t-power", new
        {
            Id = "t-power", Name = "Power", GroupId = "g1", DeviceId = "MB-1",
            Address = "Ramp", DataType = DataType.Float64, Scale = 1.0, AllowWrite = true,
        })).EnsureSuccessStatusCode();

        (await client.PutAsJsonAsync("/api/alarms/a1", new
        {
            Id = "a1", Name = "Power high", TagId = "t-power",
            Condition = AlarmCondition.Hi, Limit = 100.0,
        })).EnsureSuccessStatusCode();

        var iec = await client.GetFromJsonAsync<Iec104ServerOptions>("/api/iec104");
        iec!.Points.Add(new Iec104PointDto
        {
            TagId = "t-power", Ioa = 102, Type = Iec104PointType.ShortFloat,
        });
        (await client.PutAsJsonAsync("/api/iec104", iec)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Deleting_a_tag_reports_what_went_with_it_and_leaves_the_project_appliable()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        var impact = await client.GetFromJsonAsync<CascadeSummary>("/api/config/tags/t-power/impact");
        Assert.Equal(1, impact!.Tags);
        Assert.Equal(1, impact.Alarms);
        Assert.Equal(1, impact.Iec104Points);

        var deleted = await client.DeleteAsync("/api/config/tags/t-power");
        deleted.EnsureSuccessStatusCode();
        var result = await deleted.Content.ReadFromJsonAsync<CascadeSummary>();
        Assert.Equal(1, result!.Iec104Points);

        // The point that used to be orphaned here is gone, so the project still applies.
        var applied = await client.PostAsync("/api/config/reload", content: null);
        Assert.True(applied.StatusCode != HttpStatusCode.BadRequest, await applied.Content.ReadAsStringAsync());

        var iec = await client.GetFromJsonAsync<Iec104ServerOptions>("/api/iec104");
        Assert.Empty(iec!.Points);
        Assert.Empty(await client.GetFromJsonAsync<List<AlarmDto>>("/api/alarms") ?? []);
    }

    [Fact]
    public async Task Deleting_a_connection_takes_its_tags_instead_of_refusing()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        var deleted = await client.DeleteAsync("/api/config/devices/MB-1");
        deleted.EnsureSuccessStatusCode();
        var result = await deleted.Content.ReadFromJsonAsync<CascadeSummary>();

        Assert.Equal(1, result!.Tags);
        Assert.Equal(1, result.Alarms);
        Assert.Equal(1, result.Iec104Points);
        Assert.Empty(await client.GetFromJsonAsync<List<TagDto>>("/api/config/tags") ?? []);

        var applied = await client.PostAsync("/api/config/reload", content: null);
        Assert.True(applied.StatusCode != HttpStatusCode.BadRequest, await applied.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Deleting_something_that_is_not_there_is_still_a_404()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/config/tags/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/config/devices/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/config/tags/nope/impact")).StatusCode);
    }

    private sealed record CascadeSummary(int Tags, int Alarms, int Iec104Points, int Accumulators,
        IReadOnlyList<string> BrokenFormulas, IReadOnlyList<string> BrokenCommands);
}
