using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Northbound.Mqtt;

namespace HinoGateway.Server.Services;

public sealed class MqttHostedService : IHostedService, IAsyncDisposable
{
    private readonly IConfigStore _store;
    private readonly TagStore _tags;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<MqttHostedService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MqttPublisher? _publisher;
    private MqttPublisherOptions _options = new();
    private string? _startError;

    public MqttHostedService(IConfigStore store, TagStore tags, ILoggerFactory loggers,
        ILogger<MqttHostedService> logger)
    {
        _store = store;
        _tags = tags;
        _loggers = loggers;
        _logger = logger;
    }

    public MqttPublisherStatus Status => _publisher?.Status
        ?? new MqttPublisherStatus(_options.Enabled, false, false, $"{_options.Broker}:{_options.Port}",
            _options.PublishTopic, 0, 0, 0, 0, 0, 0, null, null, _startError);

    public Task StartAsync(CancellationToken cancellationToken) => ApplyAsync(cancellationToken);

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
                var spool = Path.Combine(AppContext.BaseDirectory, "data", "mqtt-spool");
                _publisher = new MqttPublisher(_options, _tags, spool,
                    _loggers.CreateLogger<MqttPublisher>());
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
}
