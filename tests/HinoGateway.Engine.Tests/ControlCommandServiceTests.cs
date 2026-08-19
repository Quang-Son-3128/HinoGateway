using HinoGateway.Core;
using HinoGateway.Server;
using HinoGateway.Server.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

public class ControlCommandServiceTests
{
    [Fact]
    public async Task Commands_for_one_device_keep_admission_order_with_multiple_workers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:BatchWindowMs"] = "0",
            }).Build();
            var driver = new RecordingDriver();
            using var loggers = LoggerFactory.Create(_ => { });
            var engine = new DeviceEngine([new RecordingFactory(driver)], new TagStore(), loggers);
            await engine.LoadAsync([new DeviceConfig
            {
                DeviceId = "plc-1", Driver = "Recording", ScanInterval = TimeSpan.FromHours(1),
                Tags =
                [
                    new TagDefinition { Id = "plc-1/a", Name = "a", Address = "a", AllowWrite = true },
                    new TagDefinition { Id = "plc-1/b", Name = "b", Address = "b", AllowWrite = true },
                ],
            }]);
            engine.StartAll();

            var health = new ComponentHealthRegistry(configuration);
            var audit = new AuditTrail(new GatewayDataPaths(directory), configuration, health,
                NullLogger<AuditTrail>.Instance);
            var commands = new ControlCommandService(engine, audit, health,
                new ControlCommandOptions { QueueCapacity = 16 },
                NullLogger<ControlCommandService>.Instance);

            // Admission is intentionally before worker startup, making both commands visible at
            // once; their shared device partition must still process them in this order.
            var first = commands.ExecuteAsync(new ControlCommand("plc-1/a", 1, "test", "tester", null));
            var second = commands.ExecuteAsync(new ControlCommand("plc-1/b", 2, "test", "tester", null));
            await audit.StartAsync(CancellationToken.None);
            await commands.StartAsync(CancellationToken.None);

            Assert.Equal(ControlOutcome.Successful, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            Assert.Equal(ControlOutcome.Successful, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            Assert.Equal(["plc-1/a", "plc-1/b"], driver.Writes);

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await commands.StopAsync(stop.Token);
            await audit.StopAsync(stop.Token);
            await engine.DisposeAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// The failure this class exists to prevent, reproduced against a device that has stopped
    /// answering. Commands used to be hashed into eight fixed queues, so two unrelated devices
    /// shared one as soon as their hashes collided, and a command to a healthy PLC then waited out
    /// the full deadline of every queued command to the dead one before it was even attempted.
    /// </summary>
    [Fact]
    public async Task A_dead_device_does_not_delay_commands_to_a_healthy_one()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:BatchWindowMs"] = "0",
            }).Build();

            var blackHole = new RecordingDriver { WriteDelay = TimeSpan.FromMinutes(5) };
            var healthy = new RecordingDriver();
            using var loggers = LoggerFactory.Create(_ => { });
            var engine = new DeviceEngine(
                [new RecordingFactory(blackHole, "BlackHole"), new RecordingFactory(healthy, "Healthy")],
                new TagStore(), loggers);
            await engine.LoadAsync(
            [
                new DeviceConfig
                {
                    DeviceId = "dead", Driver = "BlackHole", ScanInterval = TimeSpan.FromHours(1),
                    Tags = [new TagDefinition { Id = "dead/a", Name = "a", Address = "a", AllowWrite = true }],
                },
                new DeviceConfig
                {
                    DeviceId = "alive", Driver = "Healthy", ScanInterval = TimeSpan.FromHours(1),
                    Tags = [new TagDefinition { Id = "alive/a", Name = "a", Address = "a", AllowWrite = true }],
                },
            ]);
            engine.StartAll();

            var health = new ComponentHealthRegistry(configuration);
            var audit = new AuditTrail(new GatewayDataPaths(directory), configuration, health,
                NullLogger<AuditTrail>.Instance);
            var commands = new ControlCommandService(engine, audit, health,
                new ControlCommandOptions { QueueCapacity = 64, TimeoutMs = 30_000 },
                NullLogger<ControlCommandService>.Instance);
            await audit.StartAsync(CancellationToken.None);
            await commands.StartAsync(CancellationToken.None);

            // Enough to have filled several of the old eight partitions, and far more than the
            // healthy device could have waited through: 32 x 30 s is sixteen minutes.
            var stuck = Enumerable.Range(0, 32)
                .Select(_ => commands.ExecuteAsync(new ControlCommand("dead/a", 1, "test", "tester", null)))
                .ToList();

            var started = System.Diagnostics.Stopwatch.StartNew();
            var result = await commands
                .ExecuteAsync(new ControlCommand("alive/a", 1, "test", "tester", null))
                .WaitAsync(TimeSpan.FromSeconds(5));
            started.Stop();

            Assert.Equal(ControlOutcome.Successful, result.Outcome);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2),
                $"the healthy device waited {started.ElapsedMilliseconds} ms behind a dead one");
            Assert.Equal(["alive/a"], healthy.Writes);

            blackHole.Release();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await commands.StopAsync(stop.Token);
            await audit.StopAsync(stop.Token);
            await engine.DisposeAsync();

            // Every admitted command is answered. Which answer depends on whether it was still
            // queued when the service stopped, and that is not what this test is about — what
            // matters is that none of them is left hanging, because a caller blocked forever on a
            // command to a device that went away is how a control centre stops issuing any at all.
            foreach (var pending in stuck)
                await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class RecordingFactory(RecordingDriver driver, string driverType = "Recording") : IDeviceDriverFactory
    {
        public string DriverType => driverType;
        public IDeviceDriver Create(DeviceConfig config) => driver;
    }

    private sealed class RecordingDriver : IDeviceDriver
    {
        private readonly CancellationTokenSource _released = new();

        public List<string> Writes { get; } = new();

        /// <summary>How long a write blocks. Long enough stands in for a PLC that stopped answering.</summary>
        public TimeSpan WriteDelay { get; init; } = TimeSpan.FromMilliseconds(10);

        /// <summary>Let the blocked writes go, so the test does not have to wait out the delay.</summary>
        public void Release() => _released.Cancel();

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagRead>>([]);

        public async Task WriteAsync(string tagId, object value, CancellationToken ct)
        {
            lock (Writes) Writes.Add(tagId);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _released.Token);
            try { await Task.Delay(WriteDelay, wait.Token); }
            catch (OperationCanceledException) when (_released.IsCancellationRequested && !ct.IsCancellationRequested) { }
        }

        public ValueTask DisposeAsync() { _released.Cancel(); return ValueTask.CompletedTask; }
    }
}
