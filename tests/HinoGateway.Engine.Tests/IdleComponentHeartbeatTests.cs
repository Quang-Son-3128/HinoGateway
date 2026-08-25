using HinoGateway.Core;
using HinoGateway.Server;
using HinoGateway.Server.Services;
using HinoGateway.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// A gateway nobody has sent an order to is the ordinary state of a plant, and it used to be the
/// state that painted a red banner across Live Data: <c>commands: No heartbeat for 39 seconds</c>,
/// half a minute after every start, with nothing wrong. Behind the banner /health/ready failed too,
/// so a watchdog would keep restarting a working gateway — it is still quiet after the restart.
///
/// <para>
/// There are two shapes of component here and they need different treatment. The command engine has
/// a loop, so it says "still here" while it waits. The audit trail has none — it reports on the
/// commands that pass through it — so it is declared event-driven and is never judged on silence.
/// </para>
/// </summary>
public class IdleComponentHeartbeatTests
{
    [Fact]
    public void An_event_driven_component_is_never_stale_however_quiet_it_is()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Health:Components:HeartbeatTimeoutSeconds"] = "5",
        }).Build();
        var registry = new ComponentHealthRegistry(configuration);

        registry.EventDriven("audit-trail");
        registry.Running("some-loop");

        // No component here has been touched since; only the one that promised a heartbeat is judged
        // on its silence. Time is not mocked, so this holds by construction rather than by waiting.
        var audit = registry.Snapshot.Single(x => x.Name == "audit-trail");
        Assert.False(audit.HeartbeatExpected);
        Assert.False(audit.Stale);
        Assert.False(audit.AlarmActive);
        Assert.True(registry.Snapshot.Single(x => x.Name == "some-loop").HeartbeatExpected);
    }

    [Fact]
    public void A_failing_event_driven_component_still_alarms_and_only_a_success_clears_it()
    {
        var registry = new ComponentHealthRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Health:Components:RecoveryGraceSeconds"] = "0" }).Build());

        registry.EventDriven("audit-trail");
        registry.Failed("audit-trail", new IOException("the audit disk is full"));

        var failed = registry.Snapshot.Single();
        Assert.True(failed.AlarmActive);
        Assert.Contains("disk is full", failed.AlarmReason);

        registry.Heartbeat("audit-trail");
        var recovered = registry.Snapshot.Single();
        Assert.False(recovered.AlarmActive);
        // The heartbeat must not have turned it back into a component that is judged on silence.
        Assert.False(recovered.HeartbeatExpected);
    }

    [Fact]
    public async Task An_idle_command_engine_keeps_heart_beating()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:BatchWindowMs"] = "0",
                ["Health:Components:HeartbeatTimeoutSeconds"] = "5",
            }).Build();

            var store = new JsonProjectStore(Path.Combine(directory, "project.hino"),
                NullLogger<JsonProjectStore>.Instance);
            var engine = new DeviceEngine([], new TagStore(), NullLoggerFactory.Instance);
            var health = new ComponentHealthRegistry(configuration);
            var audit = new AuditTrail(new GatewayDataPaths(directory), configuration, health,
                NullLogger<AuditTrail>.Instance);
            var control = new ControlCommandService(engine, audit, health,
                new ControlCommandOptions { QueueCapacity = 16 },
                NullLogger<ControlCommandService>.Instance);
            var commands = new CommandEngine(engine, store, control, health,
                NullLogger<CommandEngine>.Instance);

            await commands.StartAsync(CancellationToken.None);
            try
            {
                var atStart = HeartbeatOf(health);

                // Longer than the timeout above, and no command is ever triggered: this is exactly
                // the quiet plant that used to raise the alarm.
                await Task.Delay(TimeSpan.FromSeconds(7));

                var whileIdle = HeartbeatOf(health);
                Assert.True(whileIdle > atStart,
                    "the idle command loop stopped heart-beating, so a quiet gateway reports itself "
                    + $"broken ({atStart:O} -> {whileIdle:O})");
                Assert.False(health.Snapshot.Single(x => x.Name == "commands").AlarmActive);
            }
            finally
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await commands.StopAsync(stop.Token);
                await engine.DisposeAsync();
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static DateTimeOffset HeartbeatOf(ComponentHealthRegistry health) =>
        health.Snapshot.Single(x => x.Name == "commands").LastHeartbeat;
}
