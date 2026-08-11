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
    private readonly ProjectValidator _validator;
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factories;
    private readonly ISecretStore _secrets;
    private readonly bool _seedDemo;
    private readonly ILogger<EngineHostedService> _logger;

    public EngineHostedService(JsonProjectStore project, IConfigStore store, IAlarmStore alarmStore,
        DeviceEngine engine, AlarmEngine alarms, ProjectValidator validator,
        IEnumerable<IDeviceDriverFactory> factories, ISecretStore secrets,
        IConfiguration configuration,
        ILogger<EngineHostedService> logger)
    {
        _project = project;
        _store = store;
        _alarmStore = alarmStore;
        _engine = engine;
        _alarms = alarms;
        _validator = validator;
        _factories = factories.ToDictionary(f => f.DriverType, StringComparer.OrdinalIgnoreCase);
        _secrets = secrets;
        _seedDemo = configuration.GetValue<bool>("Data:SeedDemo");
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_project.WasMissingAtStartup && _seedDemo)
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
        else if (_project.WasMissingAtStartup)
        {
            _logger.LogWarning(
                "No project file exists at {Path}. Production demo seeding is disabled; the gateway is starting empty so an unexpected data-root change cannot silently run a demo plant",
                _project.FilePath);
        }

        var devices = (await _store.GetDevicesAsync(cancellationToken)).ToList();

        // Projects predating the sidecar may still contain a literal driver password. Move those
        // values before validation/engine construction so exports from this point onward are safe.
        var migrated = new List<DeviceConfig>();
        for (var i = 0; i < devices.Count; i++)
        {
            if (!_factories.TryGetValue(devices[i].Driver, out var factory))
                continue;
            var protectedDevice = SecretConfiguration.ProtectDevice(
                devices[i], factory, _secrets, clearEmpty: false, out var changed);
            if (!changed)
                continue;
            devices[i] = protectedDevice;
            migrated.Add(protectedDevice);
        }
        if (migrated.Count > 0)
        {
            using var migration = _store.BeginTransaction();
            foreach (var device in migrated)
                await _store.UpsertDeviceAsync(device, cancellationToken);
            migration.Commit();
            _logger.LogInformation("Moved credentials for {Count} connection(s) out of the project file",
                migrated.Count);
        }

        var validation = await _validator.ValidateStoredAsync(_store, _alarmStore, cancellationToken);
        foreach (var problem in validation)
            _logger.LogError("Stored project validation: {Problem}", problem);

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
        var applied = await _engine.LoadAsync(devices, await _store.GetInternalTagsAsync(cancellationToken));
        _engine.StartAll();

        _logger.LogInformation(
            "HinoGateway engine started with {Count} connection(s), {Alarms} alarm(s) and {Internal} internal tag(s)",
            applied.Started, definitions.Count, _engine.InternalTags.Total);
        foreach (var rejected in applied.Rejected)
            _logger.LogError("Connection {DeviceId} is not running: {Error}", rejected.DeviceId, rejected.LastError);
        foreach (var dropped in applied.DroppedTags.Where(t => t.DeviceId == DeviceEngine.InternalDeviceId))
            _logger.LogError("Tag {TagId} is not running: {Error}", dropped.TagId, dropped.Reason);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
