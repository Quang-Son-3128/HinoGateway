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

    [Fact]
    public async Task Transient_poll_failures_are_tolerated_up_to_the_configured_threshold()
    {
        var store = new TagStore();
        var driver = new SequenceDriver(fatal: false);
        var observed = new List<Quality>();
        store.AddObserver(reads =>
        {
            lock (observed)
                observed.AddRange(reads.Select(r => r.Value.Quality));
        });
        await using var worker = new DeviceWorker(
            SimDevice("TOL", "Sequence") with { FailureThreshold = 3 },
            driver, store, NullLogger.Instance);

        worker.Start();
        await WaitForAsync(() => driver.Polls >= 3, TimeSpan.FromSeconds(2));

        Assert.Equal(1, driver.Connects);
        lock (observed)
        {
            Assert.Contains(Quality.Uncertain, observed);
            Assert.DoesNotContain(Quality.Bad, observed);
        }
        Assert.True(store.TryGet("TOL.Sine", out var latest));
        Assert.Equal(Quality.Good, latest.Quality);
    }

    [Fact]
    public async Task A_proven_dead_transport_reconnects_without_waiting_for_the_threshold()
    {
        var store = new TagStore();
        var driver = new SequenceDriver(fatal: true);
        await using var worker = new DeviceWorker(
            SimDevice("FATAL", "Sequence") with { FailureThreshold = 100 },
            driver, store, NullLogger.Instance);

        worker.Start();
        await WaitForAsync(() => driver.Connects >= 2, TimeSpan.FromSeconds(3));

        Assert.Equal(2, driver.Polls);
        Assert.True(driver.Disconnects >= 1);
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

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition was not reached in time.");
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

    private sealed class SequenceDriver(bool fatal) : IDeviceDriver, ITransportFaultClassifier
    {
        private int _connects, _disconnects, _polls;
        public int Connects => Volatile.Read(ref _connects);
        public int Disconnects => Volatile.Read(ref _disconnects);
        public int Polls => Volatile.Read(ref _polls);

        public Task ConnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _connects);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _disconnects);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct)
        {
            var poll = Interlocked.Increment(ref _polls);
            if (poll == 2)
                throw new IOException("one missed response");
            IReadOnlyList<TagRead> reads =
                [new("TOL.Sine", TagValue.Good((double)poll, DateTimeOffset.UtcNow))];
            return Task.FromResult(reads);
        }

        public bool IsTransportFault(Exception exception) => fatal;
        public Task WriteAsync(string tagId, object value, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
