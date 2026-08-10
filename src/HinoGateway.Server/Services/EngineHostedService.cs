using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Storage;

namespace HinoGateway.Server.Services;

/// <summary>
/// On startup: seed a demo configuration if this is a fresh install, wire alarm evaluation into the
/// tag stream, then load the configuration into the <see cref="DeviceEngine"/> and start the
/// workers. The engine is a singleton disposed by DI on shutdown.
/// </summary>
public sealed class EngineHostedService : IHostedService
{
    private readonly JsonProjectStore _project;
    private readonly IConfigStore _store;
    private readonly IAlarmStore _alarmStore;
    private readonly DeviceEngine _engine;
    private readonly AlarmEngine _alarms;
    private readonly ILogger<EngineHostedService> _logger;

    public EngineHostedService(JsonProjectStore project, IConfigStore store, IAlarmStore alarmStore,
        DeviceEngine engine, AlarmEngine alarms, ILogger<EngineHostedService> logger)
    {
        _project = project;
        _store = store;
        _alarmStore = alarmStore;
        _engine = engine;
        _alarms = alarms;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_project.IsEmpty)
        {
            _logger.LogInformation("No configuration found — seeding the demo project into {Path}", _project.FilePath);
            using var seed = _store.BeginTransaction();   // one file write, and nothing on failure
            foreach (var d in DemoConfiguration.Devices)
                await _store.UpsertDeviceAsync(d, cancellationToken);
            foreach (var g in DemoConfiguration.Groups)
                await _store.UpsertGroupAsync(g, cancellationToken);
            foreach (var t in DemoConfiguration.Tags)
                await _store.UpsertTagAsync(t, cancellationToken);
            foreach (var a in DemoConfiguration.Alarms)
                await _alarmStore.UpsertAlarmAsync(a, cancellationToken);
            seed.Commit();
        }

        var devices = await _store.GetDevicesAsync(cancellationToken);

        // Alarms evaluate off the tag stream, so they are wired up before the workers start:
        // AddObserver is lossless (unlike the UI's realtime channel), so no sample is missed.
        var definitions = AlarmMapping.ToDefinitions(
            await _alarmStore.GetAlarmsAsync(cancellationToken),
            await _store.GetTagsAsync(cancellationToken),
            _logger);
        _alarms.Reload(definitions);
        _engine.Tags.AddObserver(_alarms.OnReads);

        // A connection with an unusable configuration is reported as faulted, not thrown: the
        // gateway must always come up (it may be an unattended IPC recovering from a power cut).
        var applied = await _engine.LoadAsync(devices);
        _engine.StartAll();

        _logger.LogInformation("HinoGateway engine started with {Count} connection(s) and {Alarms} alarm(s)",
            applied.Started, definitions.Count);
        foreach (var rejected in applied.Rejected)
            _logger.LogError("Connection {DeviceId} is not running: {Error}", rejected.DeviceId, rejected.LastError);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
