using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Northbound.Iec104;

namespace HinoGateway.Server.Services;

/// <summary>
/// Owns the IEC-104 controlled station: starts it if the project enables it, and rebuilds it when the
/// configuration is applied.
///
/// A failure to start is logged and surfaced, never thrown: the utility link going down must not take
/// data acquisition with it. Commands arriving from the control centre are written through
/// <see cref="DeviceEngine.WriteTagAsync"/>, so they pass exactly the same validation, inverse
/// scaling and range checks as a write from the local UI.
/// </summary>
public sealed class Iec104HostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly DeviceEngine _engine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Iec104HostedService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Iec104Server? _server;
    private Iec104ServerOptions _options = new();
    private string? _startError;

    public Iec104HostedService(IServiceProvider services, DeviceEngine engine,
        ILoggerFactory loggerFactory, ILogger<Iec104HostedService> logger)
    {
        _services = services;
        _engine = engine;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => ApplyAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_server is not null)
                await _server.StopAsync();
            _server = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stop whatever is running and start from the stored configuration.</summary>
    public async Task<Iec104Status> ApplyAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_server is not null)
            {
                await _server.StopAsync();
                _server = null;
            }

            using var scope = _services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IConfigStore>();
            _options = await store.GetIec104Async(ct);
            _startError = null;

            if (!_options.Enabled)
            {
                _logger.LogInformation("IEC-104 controlled station is disabled");
                return CurrentStatus();
            }

            var server = new Iec104Server(_options, _engine.Tags, WriteTagAsync,
                _loggerFactory.CreateLogger<Iec104Server>());
            try
            {
                server.Start();
                _server = server;
            }
            catch (Exception ex)
            {
                // A port already in use or a bad bind address must not stop the gateway.
                _startError = ex.Message;
                _logger.LogError(ex, "IEC-104 controlled station could not start on {Address}:{Port}",
                    _options.BindAddress, _options.Port);
                await server.DisposeAsync();
            }

            return CurrentStatus();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Iec104Status CurrentStatus()
    {
        if (_server is not null)
            return _server.Status;

        return new Iec104Status(
            Enabled: _options.Enabled,
            Running: false,
            Endpoint: $"{_options.BindAddress}:{_options.Port}",
            CommonAddress: _options.CommonAddress,
            MappedPoints: _options.Points.Count,
            OpenConnections: 0,
            Connections: Array.Empty<Iec104ConnectionInfo>(),
            AsdusSent: 0, AsdusReceived: 0,
            LastSentAt: null, LastReceivedAt: null,
            CommandsAccepted: 0, CommandsRejected: 0,
            SpontaneousSent: 0, InterrogationsServed: 0,
            DroppedUpdates: 0,
            LastClockOffsetSeconds: null,
            LastError: _startError,
            Warning: null);
    }

    private Task WriteTagAsync(string tagPath, object value, CancellationToken ct) =>
        _engine.WriteTagAsync(tagPath, value, ct);
}
