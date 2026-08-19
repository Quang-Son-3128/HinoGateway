using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Northbound.Mqtt;

namespace HinoGateway.Server.Services;

public sealed class MqttHostedService : IHostedService, IAsyncDisposable
{
    private readonly IConfigStore _store;
    private readonly TagStore _tags;
    private readonly ISecretStore _secrets;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<MqttHostedService> _logger;
    private readonly GatewayDataPaths _paths;
    private readonly ComponentHealthRegistry _health;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MqttPublisher? _publisher;
    private MqttPublisherOptions _options = new();
    private string? _startError;

    public MqttHostedService(IConfigStore store, TagStore tags, ISecretStore secrets, GatewayDataPaths paths,
        ComponentHealthRegistry health, ILoggerFactory loggers,
        ILogger<MqttHostedService> logger)
    {
        _store = store;
        _tags = tags;
        _secrets = secrets;
        _loggers = loggers;
        _logger = logger;
        _paths = paths;
        _health = health;
    }

    public MqttPublisherStatus Status => _publisher?.Status
        ?? new MqttPublisherStatus(_options.Enabled, false, false, $"{_options.Broker}:{_options.Port}",
            _options.PublishTopic, 0, 0, 0, 0, 0, 0, null, null, _startError);

    public Task StartAsync(CancellationToken cancellationToken) => ApplyAsync(cancellationToken);

    /// <summary>
    /// Forward alarm events northbound. Never throws and never blocks on the broker: the publisher
    /// hands them to a durable spool and returns, so an alarm storm during a network outage costs
    /// disk rather than delaying the alarm engine. A gateway with MQTT switched off does nothing.
    /// </summary>
    public async Task ForwardAlarmsAsync(IReadOnlyList<AlarmEvent> events, CancellationToken ct)
    {
        var publisher = _publisher;
        if (publisher is null || events.Count == 0)
            return;
        try
        {
            await publisher.PublishAlarmsAsync(events, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Could not spool {Count} alarm event(s) for MQTT", events.Count);
        }
    }

    public async Task<MqttPublisherStatus> ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_publisher is not null)
            {
                await _publisher.DisposeAsync();
                _publisher = null;
            }

            _options = await _store.GetMqttAsync(ct);
            _startError = null;
            try
            {
                // One-time compatibility migration for projects written before the secret store was
                // wired back in. Persist the reference only after the sidecar write succeeds.
                if (SecretConfiguration.ProtectMqtt(_options, _secrets, clearEmpty: false))
                {
                    await _store.SaveMqttAsync(_options, ct);
                    _logger.LogInformation("Moved the MQTT password out of the project file into the secret store");
                }

                if (!SecretConfiguration.TryCreateMqttRuntime(_options, _secrets, out var runtimeOptions,
                        out var secretError))
                    throw new InvalidOperationException(secretError);

                var spool = Path.Combine(_paths.Mqtt, "spool");
                _publisher = new MqttPublisher(runtimeOptions, _tags, spool,
                    _loggers.CreateLogger<MqttPublisher>(), ObserveLoop);
                _publisher.Start();
            }
            catch (Exception ex)
            {
                _publisher = null;
                _startError = ex.Message;
                _logger.LogError(ex, "MQTT publisher could not start");
            }
            return Status;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_publisher is not null) await _publisher.DisposeAsync();
            _publisher = null;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null) await _publisher.DisposeAsync();
        _gate.Dispose();
    }

    private void ObserveLoop(string loop, Exception? failure)
    {
        var name = $"mqtt-{loop.Replace(' ', '-')}";
        if (failure is null) _health.Heartbeat(name);
        else _health.Failed(name, failure);
    }
}
