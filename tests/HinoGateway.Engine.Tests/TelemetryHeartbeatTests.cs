using HinoGateway.Core;
using HinoGateway.Drivers.Simulator;
using HinoGateway.Engine;
using HinoGateway.Server.Hubs;
using HinoGateway.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// A polling gateway whose values are not moving is the healthiest state there is, and it used to be
/// the state that made readiness fail: the tag store publishes every poll batch whether or not a value
/// changed, so the broadcaster's idle wait never fires, and heartbeating only on a successful send
/// left the component stale after 30 s. A watchdog wired to /health/ready then restarts a plant that
/// is running perfectly — repeatedly, because the plant is still quiet after the restart.
/// </summary>
public class TelemetryHeartbeatTests
{
    private static DeviceConfig ConstantDevice(string id) => new()
    {
        DeviceId = id,
        Driver = "Simulator",
        ScanInterval = TimeSpan.FromMilliseconds(20),
        // A literal address makes the simulator return the same number on every scan.
        Tags = [new TagDefinition { Id = $"{id}.Constant", Name = "Constant", Address = "42.5", DataType = DataType.Float64 }],
    };

    [Fact]
    public async Task A_healthy_gateway_whose_values_never_change_keeps_heartbeating()
    {
        var store = new TagStore();
        var engine = new DeviceEngine([new SimulatorDriverFactory()], store, NullLoggerFactory.Instance);
        await engine.LoadAsync([ConstantDevice("SIM-STATIC")]);
        engine.StartAll();

        var health = new ComponentHealthRegistry(new ConfigurationBuilder().Build());
        var broadcaster = new TelemetryBroadcaster(engine, new SilentHubContext(), health,
            NullLogger<TelemetryBroadcaster>.Instance);

        await broadcaster.StartAsync(CancellationToken.None);
        try
        {
            // Long enough for the very first (changed-from-nothing) sample to be sent and for many
            // further scans to arrive carrying the same value.
            await Task.Delay(TimeSpan.FromSeconds(3));

            var beforeQuietPeriod = HeartbeatOf(health);
            await Task.Delay(TimeSpan.FromSeconds(3));
            var afterQuietPeriod = HeartbeatOf(health);

            Assert.True(afterQuietPeriod > beforeQuietPeriod,
                "the telemetry loop stopped heart-beating once tag values settled, so readiness would " +
                $"report it stale ({beforeQuietPeriod:O} -> {afterQuietPeriod:O})");
            Assert.All(health.Snapshot, component => Assert.False(component.Stale));
        }
        finally
        {
            await broadcaster.StopAsync(CancellationToken.None);
            await engine.DisposeAsync();
        }
    }

    private static DateTimeOffset HeartbeatOf(ComponentHealthRegistry health) =>
        health.Snapshot.Single(x => x.Name == "signalr-telemetry").LastHeartbeat;

    /// <summary>A hub with no clients: SendAsync succeeds and goes nowhere, as in a browserless plant.</summary>
    private sealed class SilentHubContext : IHubContext<TelemetryHub>
    {
        public IHubClients Clients { get; } = new SilentClients();
        public IGroupManager Groups { get; } = new SilentGroups();

        private sealed class SilentClients : IHubClients
        {
            public IClientProxy All { get; } = new SilentProxy();
            public IClientProxy AllExcept(IReadOnlyList<string> excluded) => All;
            public IClientProxy Client(string connectionId) => All;
            public IClientProxy Clients(IReadOnlyList<string> connectionIds) => All;
            public IClientProxy Group(string groupName) => All;
            public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excluded) => All;
            public IClientProxy Groups(IReadOnlyList<string> groupNames) => All;
            public IClientProxy User(string userId) => All;
            public IClientProxy Users(IReadOnlyList<string> userIds) => All;
        }

        private sealed class SilentProxy : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }

        private sealed class SilentGroups : IGroupManager
        {
            public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
            public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;
        }
    }
}
