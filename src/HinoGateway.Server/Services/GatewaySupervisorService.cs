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
    ILogger<GatewaySupervisorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (engine.RequiresRestart)
            {
                logger.LogCritical("A device worker was abandoned; requesting process restart instead of starting a duplicate worker");
                Environment.ExitCode = 2;
                lifetime.StopApplication();
                return;
            }

            await CheckIecAsync(stoppingToken);
            await CheckMqttAsync(stoppingToken);
        }
    }

    private async Task CheckIecAsync(CancellationToken ct)
    {
        var status = iec104.CurrentStatus();
        if (!status.Enabled || status.Running)
        {
            health.Heartbeat("iec104");
            return;
        }
        var failure = new InvalidOperationException(status.LastError ?? "IEC-104 station is not running.");
        health.Failed("iec104", failure);
        logger.LogWarning("IEC-104 supervisor is reapplying the station configuration");
        try { await iec104.ApplyAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "IEC-104 supervised restart failed"); }
    }

    private async Task CheckMqttAsync(CancellationToken ct)
    {
        var status = mqtt.Status;
        if (!status.Enabled || status.Running)
        {
            health.Heartbeat("mqtt");
            return;
        }
        var failure = new InvalidOperationException(status.LastError ?? "MQTT publisher is not running.");
        health.Failed("mqtt", failure);
        logger.LogWarning("MQTT supervisor is reapplying the publisher configuration");
        try { await mqtt.ApplyAsync(ct); }
        catch (Exception ex) { logger.LogError(ex, "MQTT supervised restart failed"); }
    }
}
