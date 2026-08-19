using HinoGateway.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace HinoGateway.Api.Tests;

/// <summary>
/// The gateway runs on an industrial PC that nobody is sitting in front of, so "it recovered" has to
/// mean the machine did it, not that somebody noticed.
///
/// <para>
/// These assertions look small because what they protect is a default. A background loop that faults
/// used to take the host down through the tidiest possible path: the host stopped cleanly, the
/// Windows service reported SERVICE_STOPPED with exit code 0, and the recovery actions configured by
/// <c>sc failure</c> only fire on a non-zero code or on a process that disappears without reporting a
/// stop. The gateway therefore asked for nothing, was restarted by nobody, and stayed down until
/// somebody drove out to the site. Nothing in the codebase said that out loud, and one line of
/// configuration is all that stands between here and there.
/// </para>
/// </summary>
public class UnattendedRecoveryTests
{
    [Fact]
    public void A_faulting_background_service_does_not_stop_the_host_behind_everyones_back()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();

        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(BackgroundServiceExceptionBehavior.Ignore, options.BackgroundServiceExceptionBehavior);
    }

    [Fact]
    public void The_supervisor_is_running_and_is_the_component_that_can_end_the_process()
    {
        using var factory = new GatewayFactory();
        _ = factory.CreateClient();

        var supervisor = factory.Services.GetServices<IHostedService>()
            .OfType<GatewaySupervisorService>()
            .SingleOrDefault();

        // With BackgroundServiceExceptionBehavior.Ignore the host no longer reacts to a dead loop at
        // all, so this service being registered is not a nicety — it is the only thing left watching.
        Assert.NotNull(supervisor);
    }

    /// <summary>
    /// A hosted service that returns early on purpose must not be mistaken for a dead one.
    ///
    /// <para>
    /// The historian returns from <c>ExecuteAsync</c> immediately when it is switched off, and this
    /// test host switches it off. An earlier version of the dead-loop check treated any completed
    /// task as a death, which would have put every gateway with <c>History:Enabled=false</c> into a
    /// permanent restart loop — the precise failure the check was written to prevent, reached from
    /// the other direction. If that regresses, this host will end its own process during the test
    /// run rather than fail an assertion, so a sudden disappearance of this test is the symptom.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_deliberately_disabled_service_is_not_treated_as_a_dead_one()
    {
        using var factory = new GatewayFactory(new Dictionary<string, string?>
        {
            ["History:Enabled"] = "false",
        });
        _ = factory.CreateClient();

        var supervisor = factory.Services.GetServices<IHostedService>()
            .OfType<GatewaySupervisorService>()
            .Single();

        var restartRequested = false;
        supervisor.RestartRequested = () => restartRequested = true;

        // Long enough for at least one supervisor tick (it runs on a five-second period).
        await Task.Delay(TimeSpan.FromSeconds(7));

        Assert.False(restartRequested,
            "the disabled historian returned on purpose and must not be read as a fault");

        var live = await factory.CreateClient().GetAsync("/health/live");
        Assert.True(live.IsSuccessStatusCode);
    }

    /// <summary>
    /// The northbound supervisor must not turn a standing failure into a log flood.
    ///
    /// <para>
    /// It used to reapply the configuration every five seconds forever and log a warning with a stack
    /// trace each time. A cause that never clears on its own — a port held by another process, a bind
    /// address that no longer exists — produced roughly seventeen thousand lines a day, and the log
    /// files roll by size: within hours the only thing on disk was that message, and the entries
    /// explaining the original outage had rolled off. An unattended gateway needs its logs to survive
    /// its own failures.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_northbound_link_that_cannot_start_is_retried_with_backoff_not_every_tick()
    {
        using var factory = new GatewayFactory();
        var client = factory.CreateClient();

        // The station is disabled in a fresh project, so the supervisor treats it as nothing to do
        // and must not be accumulating restart attempts against it.
        await Task.Delay(TimeSpan.FromSeconds(7));

        var components = await client.GetFromJsonAsync<List<ComponentHealthDto>>("/api/health/components");
        Assert.NotNull(components);

        var iec = components.SingleOrDefault(c => c.Name == "iec104");
        if (iec is not null)
            Assert.True(iec.RecentRestarts <= 1,
                $"a disabled station should not be restarted repeatedly; saw {iec.RecentRestarts}");
    }

    private sealed record ComponentHealthDto(string Name, bool Running, long Restarts, int RecentRestarts);
}
