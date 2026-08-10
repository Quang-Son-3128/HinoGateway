using HinoGateway.Core;
using HinoGateway.Drivers.Simulator;
using HinoGateway.Engine;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

public class DeviceEngineTests
{
    private static DeviceConfig SimDevice(string id, string driver = "Simulator") => new()
    {
        DeviceId = id,
        Driver = driver,
        ScanInterval = TimeSpan.FromMilliseconds(50),
        Tags = [new TagDefinition { Id = $"{id}.Sine", Name = "Sine", Address = "sine", DataType = DataType.Float64 }],
    };

    [Fact]
    public async Task Engine_produces_good_values_from_a_driver()
    {
        var store = new TagStore();
        var engine = new DeviceEngine([new SimulatorDriverFactory()], store, NullLoggerFactory.Instance);
        await engine.LoadAsync([SimDevice("SIM-1")]);
        engine.StartAll();

        var value = await WaitForGoodAsync(store, "SIM-1.Sine", TimeSpan.FromSeconds(5));

        Assert.Equal(Quality.Good, value.Quality);
        Assert.NotNull(value.Value);

        await engine.DisposeAsync();
    }

    [Fact]
    public async Task A_faulting_device_is_isolated_and_its_tags_go_bad()
    {
        var store = new TagStore();
        var engine = new DeviceEngine(
            [new SimulatorDriverFactory(), new FaultingDriverFactory()],
            store,
            NullLoggerFactory.Instance);

        await engine.LoadAsync([SimDevice("GOOD"), SimDevice("BAD", driver: "Faulting")]);
        engine.StartAll();

        // The healthy device keeps producing Good values despite the other one faulting...
        var good = await WaitForGoodAsync(store, "GOOD.Sine", TimeSpan.FromSeconds(5));
        Assert.Equal(Quality.Good, good.Quality);

        // ...and the faulting device's tag is marked Bad, not left stale.
        var bad = await WaitForQualityAsync(store, "BAD.Sine", Quality.Bad, TimeSpan.FromSeconds(5));
        Assert.Equal(Quality.Bad, bad.Quality);

        await engine.DisposeAsync();
    }

    private static Task<TagValue> WaitForGoodAsync(TagStore store, string tagId, TimeSpan timeout) =>
        WaitForQualityAsync(store, tagId, Quality.Good, timeout);

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

    // A driver that always fails on poll, to exercise fault isolation + Bad-quality marking.
    private sealed class FaultingDriver : IDeviceDriver
    {
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct) =>
            throw new IOException("simulated comms failure");
        public Task WriteAsync(string tagId, object value, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FaultingDriverFactory : IDeviceDriverFactory
    {
        public string DriverType => "Faulting";
        public IDeviceDriver Create(DeviceConfig config) => new FaultingDriver();
    }
}
