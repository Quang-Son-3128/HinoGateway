namespace HinoGateway.Server.Services;

/// <summary>
/// Component-specific recovery: northbound links are reapplied independently; an abandoned device
/// worker is not duplicated and instead requests a clean process restart from the service manager.
/// </summary>
public sealed class GatewaySupervisorService(
    HinoGateway.Engine.DeviceEngine engine,
    Iec104HostedService iec104,
    MqttHostedService mqtt,
    ComponentHealthRegistry health,
    IHostApplicationLifetime lifetime,
    IServiceProvider services,
    ILogger<GatewaySupervisorService> logger) : BackgroundService
{
    /// <summary>Exit code for "the process must be restarted", read by the service manager.</summary>
    public const int RestartRequiredExitCode = 2;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(5);

    private readonly Backoff _iecBackoff = new();
    private readonly Backoff _mqttBackoff = new();
    private List<BackgroundService>? _watched;

    /// <summary>
    /// Set by the tests to observe a restart request without taking the test host down with it.
    /// Null in production, where the only correct response is for the process to end.
    /// </summary>
    internal Action? RestartRequested { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Every other background loop in the gateway is wrapped in a supervisor that restarts it.
            // This one was not — and it is the loop whose entire job is to notice that something else
            // died and ask for a restart. One ObjectDisposedException from a status property during a
            // reload was enough to kill it silently, after which nothing was watching anything and the
            // gateway sat there looking healthy with a dead device worker behind it.
            try
            {
                if (engine.RequiresRestart)
                {
                    RequestProcessRestart("a device worker was abandoned");
                    return;
                }

                if (DeadBackgroundService() is { } dead)
                {
                    RequestProcessRestart($"background service '{dead}' is no longer running");
                    return;
                }

                await CheckIecAsync(stoppingToken);
                await CheckMqttAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                health.Failed("supervisor", ex);
                logger.LogError(ex, "The gateway supervisor failed a cycle; it will try again in {Interval}", Interval);
            }
        }
    }

    /// <summary>
    /// The name of a <see cref="BackgroundService"/> whose loop has stopped, or null while all of
    /// them are still running.
    ///
    /// <para>
    /// The host is configured with <see cref="BackgroundServiceExceptionBehavior.Ignore"/>, so a
    /// faulting loop no longer takes the process down behind everyone's back — which sounds worse
    /// and is better. The default, <c>StopHost</c>, unwound the host cleanly and the service then
    /// reported a successful stop, so the recovery actions never fired and the gateway stayed down.
    /// Ignoring the fault leaves the process alive and makes it this loop's job to notice, which is
    /// the one place that can end the process in a way a service manager acts on.
    /// </para>
    ///
    /// <para>
    /// Only a <em>faulted</em> loop counts. Returning early is a legitimate thing for a hosted service
    /// to do — <see cref="HistorySamplerService"/> returns immediately when the historian is switched
    /// off — and treating that as death would put every gateway with history disabled into a restart
    /// loop, which is the exact failure this class exists to prevent, arrived at from the other end.
    /// Nothing is lost by the narrower rule: every supervised loop in this project already converts
    /// an unexpected return into a throw ("loop returned unexpectedly"), so a silent stop reaches
    /// here as a fault anyway.
    /// </para>
    /// </summary>
    private string? DeadBackgroundService()
    {
        _watched ??= services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Where(service => !ReferenceEquals(service, this))
            .ToList();

        foreach (var service in _watched)
        {
            if (service.ExecuteTask is not { IsFaulted: true } task)
                continue;
            if (lifetime.ApplicationStopping.IsCancellationRequested)
                continue;   // shutting down: a loop tearing itself down is not a fault worth restarting for

            var name = service.GetType().Name;
            logger.LogCritical(task.Exception, "Background service {Service} faulted and is no longer running", name);
            return name;
        }
        return null;
    }

    /// <summary>
    /// End the process so the service manager starts a fresh one.
    ///
    /// <para>
    /// <see cref="IHostApplicationLifetime.StopApplication"/> is not enough on Windows. It unwinds the
    /// host cleanly, the service reports SERVICE_STOPPED with exit code 0, and the recovery actions
    /// configured by <c>sc failure</c> only fire on a non-zero code or on a process that vanishes
    /// without reporting a stop. Setting <c>Environment.ExitCode</c> does not help either: the code
    /// the SCM records comes from <c>ServiceBase.ExitCode</c>, which the hosting lifetime leaves at
    /// zero. The net effect was a gateway that asked to be restarted, was heard by nobody, and stayed
    /// down until somebody visited the site.
    /// </para>
    ///
    /// <para>
    /// Ending the process outright is the one signal every service manager reads the same way, on
    /// Windows and under systemd alike. Logs are flushed first, because the line explaining why is
    /// the line most worth having afterwards.
    /// </para>
    /// </summary>
    private void RequestProcessRestart(string because)
    {
        logger.LogCritical(
            "Requesting a process restart because {Reason}; ending the process so the service manager " +
            "starts a clean one rather than running on in a degraded state", because);

        if (RestartRequested is { } hook)
        {
            hook();
            lifetime.StopApplication();
            return;
        }

        Serilog.Log.CloseAndFlush();
        Environment.Exit(RestartRequiredExitCode);
    }

    private async Task CheckIecAsync(CancellationToken ct)
    {
        var status = iec104.CurrentStatus();
        if (!status.Enabled || status.Running)
        {
            _iecBackoff.Reset();
            health.Heartbeat("iec104");
            return;
        }
        await ReapplyAsync("iec104", _iecBackoff, status.LastError ?? "IEC-104 station is not running.",
            () => iec104.ApplyAsync(ct));
    }

    private async Task CheckMqttAsync(CancellationToken ct)
    {
        var status = mqtt.Status;
        if (!status.Enabled || status.Running)
        {
            _mqttBackoff.Reset();
            health.Heartbeat("mqtt");
            return;
        }
        await ReapplyAsync("mqtt", _mqttBackoff, status.LastError ?? "MQTT publisher is not running.",
            () => mqtt.ApplyAsync(ct));
    }

    /// <summary>
    /// Reapply a northbound link's configuration, backing off as attempts keep failing.
    ///
    /// <para>
    /// This used to retry every five seconds forever and log a warning plus a stack trace each time.
    /// A cause that does not clear on its own — a port held by another process, a bind address that
    /// no longer exists on the machine — therefore produced around seventeen thousand log lines a
    /// day, and the retained log files roll by size: within hours the only thing left on disk was the
    /// same message repeated, and the entries explaining what actually happened were gone. An
    /// unattended gateway needs its logs to survive its own failures.
    /// </para>
    /// </summary>
    private async Task ReapplyAsync(string component, Backoff backoff, string error, Func<Task> apply)
    {
        var failure = new InvalidOperationException(error);
        health.Failed(component, failure);
        if (!backoff.DueNow())
            return;

        var delay = backoff.Next();
        // Loud the first few times, quiet once it is clearly a standing condition. The state stays
        // visible on /health/ready and /api/health/components either way.
        var loud = backoff.Attempts <= 3;
        logger.Log(loud ? LogLevel.Warning : LogLevel.Debug,
            "{Component} is not running ({Error}); reapplying its configuration, next attempt in {Delay}",
            component, error, delay);
        try
        {
            await apply();
        }
        catch (Exception ex)
        {
            logger.Log(loud ? LogLevel.Error : LogLevel.Debug, ex,
                "{Component} supervised restart failed", component);
        }
    }

    /// <summary>Exponential backoff between supervised restart attempts of one component.</summary>
    private sealed class Backoff
    {
        private DateTimeOffset _dueAt = DateTimeOffset.MinValue;
        private TimeSpan _delay = Interval;

        public int Attempts { get; private set; }

        public bool DueNow() => DateTimeOffset.UtcNow >= _dueAt;

        public TimeSpan Next()
        {
            Attempts++;
            var delay = _delay;
            _delay = TimeSpan.FromTicks(Math.Min(_delay.Ticks * 2, BackoffMax.Ticks));
            _dueAt = DateTimeOffset.UtcNow + delay;
            return delay;
        }

        public void Reset()
        {
            Attempts = 0;
            _delay = Interval;
            _dueAt = DateTimeOffset.MinValue;
        }
    }
}
