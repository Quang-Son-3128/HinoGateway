using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;
using HinoGateway.Drivers.Simulator;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// A gateway applies configuration while it is running and while operators are watching it. These
/// tests cover the two ways that used to break: readers observing a runtime mid-rebuild, and one
/// unusable connection taking everything else down with it.
/// </summary>
public class RuntimeReloadTests
{
    private static DeviceConfig Sim(string id, string driver = "Simulator", int tags = 1)
    {
        var definitions = Enumerable.Range(1, tags).Select(i => new TagDefinition
        {
            Id = $"{id}.Tag{i}",
            Name = $"Tag{i}",
            Address = "sine",
            DataType = DataType.Float64,
        }).ToList();

        return new DeviceConfig
        {
            DeviceId = id,
            Driver = driver,
            ScanInterval = TimeSpan.FromMilliseconds(20),
            Tags = definitions,
        };
    }

    private static DeviceEngine NewEngine(TagStore store) =>
        new([new SimulatorDriverFactory(), new ModbusTcpDriverFactory()], store, NullLoggerFactory.Instance);

    [Fact]
    public async Task Health_and_writes_stay_safe_while_the_configuration_is_reloaded_repeatedly()
    {
        var store = new TagStore();
        await using var engine = NewEngine(store);
        await engine.LoadAsync([Sim("SIM-1", tags: 5), Sim("SIM-2", tags: 5)]);
        engine.StartAll();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var readerFailures = new List<Exception>();

        // Two readers hammering the runtime exactly as the UI does (health poll + control writes).
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    _ = engine.Health.Count;
                    try
                    {
                        await engine.WriteTagAsync("SIM-1.Tag1", 1.0, CancellationToken.None);
                    }
                    catch (KeyNotFoundException) { /* tag momentarily not owned — a valid answer */ }
                    catch (InvalidOperationException) { /* device not connected yet — also valid */ }
                    catch (NotSupportedException) { /* the simulator refuses writes */ }
                }
                catch (Exception ex)
                {
                    // Anything else (notably "Collection was modified") is the bug under test.
                    lock (readerFailures)
                        readerFailures.Add(ex);
                }
                await Task.Yield();
            }
        })).ToArray();

        var reloads = 0;
        while (!cts.IsCancellationRequested)
        {
            await engine.ReloadAsync([Sim("SIM-1", tags: 5), Sim("SIM-2", tags: 5)]);
            reloads++;
            await Task.Delay(10);
        }

        await Task.WhenAll(readers);

        Assert.True(reloads > 5, $"expected the test to exercise several reloads, got {reloads}");
        Assert.Empty(readerFailures);
    }

    [Fact]
    public async Task An_unusable_connection_is_rejected_while_the_rest_keeps_running()
    {
        var store = new TagStore();
        await using var engine = NewEngine(store);

        var broken = new DeviceConfig
        {
            DeviceId = "MB-BAD",
            Driver = "ModbusTCP",
            ScanInterval = TimeSpan.FromMilliseconds(50),
            Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1" },
            // 'not-an-address' cannot be parsed — this used to throw out of the driver constructor.
            Tags = [new TagDefinition { Id = "MB-BAD.X", Name = "X", Address = "not-an-address" }],
        };

        var applied = await engine.LoadAsync([Sim("SIM-1"), broken]);
        engine.StartAll();

        Assert.Equal(1, applied.Started);
        var rejected = Assert.Single(applied.Rejected);
        Assert.Equal("MB-BAD", rejected.DeviceId);
        Assert.Equal(DeviceState.Faulted, rejected.State);
        Assert.Contains("not-an-address", rejected.LastError);

        // The healthy connection is unaffected...
        var value = await WaitForQualityAsync(store, "SIM-1.Tag1", Quality.Good, TimeSpan.FromSeconds(5));
        Assert.Equal(Quality.Good, value.Quality);

        // ...and the rejected connection's tag is visibly Bad rather than silently missing.
        Assert.True(store.TryGet("MB-BAD.X", out var bad));
        Assert.Equal(Quality.Bad, bad.Quality);
        Assert.Contains("not-an-address", bad.StatusText);

        // Both appear in health, so the UI can show what needs fixing.
        Assert.Equal(2, engine.Health.Count);
    }

    [Fact]
    public async Task A_bad_reload_does_not_take_the_previously_running_connections_down()
    {
        var store = new TagStore();
        await using var engine = NewEngine(store);
        await engine.LoadAsync([Sim("SIM-1")]);
        engine.StartAll();
        await WaitForQualityAsync(store, "SIM-1.Tag1", Quality.Good, TimeSpan.FromSeconds(5));

        var applied = await engine.ReloadAsync([
            Sim("SIM-1"),
            new DeviceConfig { DeviceId = "GHOST", Driver = "NoSuchDriver", ScanInterval = TimeSpan.FromSeconds(1) },
        ]);

        Assert.Equal(1, applied.Started);
        Assert.Contains("Unknown driver", Assert.Single(applied.Rejected).LastError);

        // SIM-1 is still polling after the reload.
        var after = await WaitForFreshValueAsync(store, "SIM-1.Tag1", TimeSpan.FromSeconds(5));
        Assert.Equal(Quality.Good, after.Quality);
    }

    [Fact]
    public async Task Tags_removed_by_a_reload_stop_appearing_in_the_snapshot()
    {
        var store = new TagStore();
        await using var engine = NewEngine(store);
        await engine.LoadAsync([Sim("SIM-1", tags: 3)]);
        engine.StartAll();
        await WaitForQualityAsync(store, "SIM-1.Tag3", Quality.Good, TimeSpan.FromSeconds(5));

        await engine.ReloadAsync([Sim("SIM-1", tags: 1)]);

        Assert.False(store.TryGet("SIM-1.Tag3", out _));
        Assert.True(store.Snapshot().ContainsKey("SIM-1.Tag1"));
    }

    private static async Task<TagValue> WaitForQualityAsync(TagStore store, string tagId, Quality quality, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (store.TryGet(tagId, out var v) && v.Quality == quality)
                return v;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Tag '{tagId}' did not reach quality {quality} within {timeout}.");
    }

    private static async Task<TagValue> WaitForFreshValueAsync(TagStore store, string tagId, TimeSpan timeout)
    {
        var since = DateTimeOffset.UtcNow;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (store.TryGet(tagId, out var v) && v.Quality == Quality.Good && v.SourceTimestamp > since)
                return v;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Tag '{tagId}' produced no fresh value within {timeout}.");
    }
}
