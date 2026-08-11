using HinoGateway.Server.Services;
using Microsoft.Extensions.Configuration;

namespace HinoGateway.Engine.Tests;

public class ComponentHealthRegistryTests
{
    [Fact]
    public void Restart_storm_remains_an_alarm_after_the_loop_restarts()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Health:Components:MaxRestartsInWindow"] = "2",
            ["Health:Components:RecoveryGraceSeconds"] = "0",
        }).Build();
        var registry = new ComponentHealthRegistry(configuration);

        registry.Failed("worker", new IOException("one"));
        registry.Running("worker");
        registry.Failed("worker", new IOException("two"));
        registry.Running("worker");

        var health = Assert.Single(registry.Snapshot);
        Assert.True(health.Running);
        Assert.True(health.RestartStorm);
        Assert.True(health.AlarmActive);
        Assert.Contains("failures", health.AlarmReason);
    }
}
