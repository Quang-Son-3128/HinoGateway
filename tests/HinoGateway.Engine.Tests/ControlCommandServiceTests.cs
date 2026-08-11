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
                new ControlCommandOptions { WorkerCount = 8, QueueCapacity = 16 },
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

    private sealed class RecordingFactory(RecordingDriver driver) : IDeviceDriverFactory
    {
        public string DriverType => "Recording";
        public IDeviceDriver Create(DeviceConfig config) => driver;
    }

    private sealed class RecordingDriver : IDeviceDriver
    {
        public List<string> Writes { get; } = new();
        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TagRead>>([]);
        public async Task WriteAsync(string tagId, object value, CancellationToken ct)
        {
            Writes.Add(tagId);
            await Task.Delay(10, ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
