using System.Net;
using System.Net.Http.Json;
using HinoGateway.Core;

namespace HinoGateway.Api.Tests;

/// <summary>
/// Commands end to end: write the tag the control centre writes, and check the value arrives where
/// the PLC would read it.
///
/// <para>
/// This is the path that was broken in the field. In the EVN project, IOA 102 and 103 map to memory
/// tags — the curtailment percentage the control centre sends — and nothing carried those numbers on
/// to <c>PLCControl/SettingPEVN</c>, the register the PLC actually reads. The value arrived, general
/// interrogation read it back, and the plant did nothing.
/// </para>
/// </summary>
public class CommandEndpointTests
{
    private const string Percent = "t-percent";
    private const string Setpoint = "t-setpoint";

    private static async Task SeedAsync(HttpClient client)
    {
        (await client.PutAsJsonAsync("/api/config/groups/g1", new { Id = "g1", Name = "PLCControl" }))
            .EnsureSuccessStatusCode();

        // What the control centre writes: a memory tag, exactly as an IEC-104 setpoint point maps.
        (await client.PutAsJsonAsync($"/api/config/tags/{Percent}", new
        {
            Id = Percent, Name = "SetControlPowerPercent", GroupId = "g1",
            Kind = TagKind.Memory, DataType = DataType.Float64, AllowWrite = true,
        })).EnsureSuccessStatusCode();

        // What the PLC reads. Memory here so the test needs no device; the write path is the same.
        (await client.PutAsJsonAsync($"/api/config/tags/{Setpoint}", new
        {
            Id = Setpoint, Name = "SettingPEVN", GroupId = "g1",
            Kind = TagKind.Memory, DataType = DataType.Float64, AllowWrite = true,
        })).EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> SaveCommandAsync(HttpClient client, string script,
        string trigger = Percent, string id = "c1") =>
        client.PutAsJsonAsync($"/api/commands/{id}", new
        {
            Id = id, Name = "Curtailment", Enabled = true, TriggerTagId = trigger, Script = script,
        });

    private static Task<HttpResponseMessage> WriteTagAsync(HttpClient client, string path, object value) =>
        client.PostAsJsonAsync("/api/tags/write", new { TagId = path, Value = value });

    private static async Task<TagSample?> ReadAsync(HttpClient client, string path)
    {
        var values = await client.GetFromJsonAsync<List<TagSample>>("/api/tags/values") ?? new();
        return values.FirstOrDefault(v => v.TagId == path);
    }

    [Fact]
    public async Task A_command_carries_the_control_centres_setpoint_on_to_the_plant()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        (await SaveCommandAsync(client, """Write("PLCControl/SettingPEVN", 165 * value / 100);"""))
            .EnsureSuccessStatusCode();
        (await client.PostAsync("/api/config/reload", content: null)).EnsureSuccessStatusCode();

        (await WriteTagAsync(client, "PLCControl/SetControlPowerPercent", 50.0)).EnsureSuccessStatusCode();

        // The command runs on its own loop, off the back of the tag store's next batch.
        var setpoint = await Eventually(async () =>
        {
            var sample = await ReadAsync(client, "PLCControl/SettingPEVN");
            return sample?.Value is not null && Convert.ToDouble(sample.Value.ToString()) > 0 ? sample : null;
        });

        Assert.Equal(82.5, Convert.ToDouble(setpoint!.Value!.ToString()), 3);
    }

    /// <summary>
    /// A gateway coming back from a power cut must not send a setpoint nobody asked for.
    ///
    /// <para>
    /// The trigger tag has a starting value, and the engine publishes it as soon as it is built —
    /// which looks exactly like the control centre writing that value. It ran: a curtailment command
    /// wrote its trigger's initial 0 to the plant on every restart and every apply, which on a solar
    /// farm is an order to stop generating. A command now only runs on a change it witnessed while
    /// it was running.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Loading_a_command_does_not_make_it_run_on_the_value_that_was_already_there()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        // A trigger that already holds a value before the command exists, as after a restart.
        (await client.PutAsJsonAsync($"/api/config/tags/{Percent}", new
        {
            Id = Percent, Name = "SetControlPowerPercent", GroupId = "g1",
            Kind = TagKind.Memory, DataType = DataType.Float64, AllowWrite = true, InitialValue = "80",
        })).EnsureSuccessStatusCode();
        (await SaveCommandAsync(client, """Write("PLCControl/SettingPEVN", 165 * value / 100);"""))
            .EnsureSuccessStatusCode();
        (await client.PostAsync("/api/config/reload", content: null)).EnsureSuccessStatusCode();

        await Task.Delay(500);

        var setpoint = await ReadAsync(client, "PLCControl/SettingPEVN");
        Assert.True(setpoint?.Value is null || Convert.ToDouble(setpoint.Value.ToString()) == 0,
            $"the gateway wrote {setpoint?.Value} to the plant without being asked");

        // ...and a real change still runs it.
        (await WriteTagAsync(client, "PLCControl/SetControlPowerPercent", 50.0)).EnsureSuccessStatusCode();
        var after = await Eventually(async () =>
        {
            var sample = await ReadAsync(client, "PLCControl/SettingPEVN");
            return sample?.Value is not null && Convert.ToDouble(sample.Value.ToString()) > 0 ? sample : null;
        });
        Assert.Equal(82.5, Convert.ToDouble(after!.Value!.ToString()), 3);
    }

    [Fact]
    public async Task A_dry_run_says_what_would_be_written_without_writing_it()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);
        (await SaveCommandAsync(client, """Write("PLCControl/SettingPEVN", 165 * value / 100);"""))
            .EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync("/api/commands/c1/test", new { Value = "50" });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TestResult>();

        Assert.True(result!.Completed);
        var write = Assert.Single(result.Writes);
        Assert.Equal("PLCControl/SettingPEVN", write.TagPath);
        Assert.Equal("82.5", write.Value);

        // Nothing was written: that is the point of a dry run.
        var sample = await ReadAsync(client, "PLCControl/SettingPEVN");
        Assert.True(sample?.Value is null || Convert.ToDouble(sample.Value.ToString()) == 0);
    }

    /// <summary>
    /// AllowWrite is the one interlock the configuration has. A command that could write past it
    /// would be a way around it for anyone who can edit a script.
    /// </summary>
    [Fact]
    public async Task A_command_writing_to_a_tag_that_is_not_writable_is_refused_when_it_is_saved()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);
        (await client.PutAsJsonAsync($"/api/config/tags/{Setpoint}", new
        {
            Id = Setpoint, Name = "SettingPEVN", GroupId = "g1",
            Kind = TagKind.Memory, DataType = DataType.Float64, AllowWrite = false,
        })).EnsureSuccessStatusCode();

        var response = await SaveCommandAsync(client, """Write("PLCControl/SettingPEVN", value);""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("AllowWrite", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_command_naming_a_tag_that_does_not_exist_is_refused()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        var response = await SaveCommandAsync(client, """Write("PLCControl/Nowhere", value);""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not exist", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_script_that_does_not_compile_is_refused_with_the_position()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        var response = await SaveCommandAsync(client, """Write("PLCControl/SettingPEVN" value)""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at character", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_check_endpoint_lists_what_a_script_reads_and_writes()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);

        var response = await client.PostAsJsonAsync("/api/commands/check", new
        {
            Id = "c1", Name = "Curtailment", TriggerTagId = Percent,
            Script = """
                var enabled = Tag("PLCControl/SetControlPowerPercent");
                Write("PLCControl/SettingPEVN", enabled);
                """,
        });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CheckResult>();

        Assert.True(result!.Ok, result.Error);
        Assert.Equal(new[] { "PLCControl/SetControlPowerPercent" }, result.Reads);
        Assert.Equal(new[] { "PLCControl/SettingPEVN" }, result.Writes);
    }

    [Fact]
    public async Task A_command_survives_export_and_import()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();
        await SeedAsync(client);
        (await SaveCommandAsync(client, """Write("PLCControl/SettingPEVN", value);""")).EnsureSuccessStatusCode();

        var exported = await client.GetStringAsync("/api/config/export");
        Assert.Contains("SettingPEVN", exported);

        (await client.DeleteAsync("/api/commands/c1")).EnsureSuccessStatusCode();
        Assert.Empty(await client.GetFromJsonAsync<List<CommandView>>("/api/commands") ?? []);

        var import = await client.PostAsync("/api/config/import?replace=true",
            new StringContent(exported, System.Text.Encoding.UTF8, "application/json"));
        import.EnsureSuccessStatusCode();

        var commands = await client.GetFromJsonAsync<List<CommandView>>("/api/commands") ?? [];
        Assert.Equal("Curtailment", Assert.Single(commands).Command.Name);
    }

    private static async Task<T?> Eventually<T>(Func<Task<T?>> probe, int timeoutMs = 5000) where T : class
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException("The expected value never arrived.");
    }

    private sealed record TestResult(bool Completed, IReadOnlyList<TestWrite> Writes, string? Error);

    private sealed record TestWrite(string TagPath, string? Value);

    private sealed record CheckResult(bool Ok, string? Error, int Position,
        IReadOnlyList<string> Reads, IReadOnlyList<string> Writes);

    private sealed record CommandView(CommandDto Command, IReadOnlyList<string> Reads,
        IReadOnlyList<string> Writes, CommandRunInfo? LastRun);
}
