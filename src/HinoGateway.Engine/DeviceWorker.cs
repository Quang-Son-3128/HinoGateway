using System.Diagnostics;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Engine;

/// <summary>
/// Runs exactly one device on its own background task: connect → poll on a deterministic
/// cadence → on any fault, mark the device's tags Bad, back off, and reconnect. Each worker is
/// fully isolated: an exception here is caught and handled locally, so a slow or broken device
/// can never stall or crash other devices or the engine.
/// </summary>
public sealed class DeviceWorker : IAsyncDisposable
{
    private readonly DeviceConfig _config;
    private readonly IDeviceDriver _driver;
    private readonly TagStore _store;
    private readonly ILogger _logger;

    // Serializes all access to the driver so a write from an API thread never races the poll loop.
    private readonly SemaphoreSlim _driverLock = new(1, 1);
    private readonly Dictionary<string, TagDefinition> _tagsById;

    private readonly TimeSpan _backoffInitial = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _backoffMax = TimeSpan.FromSeconds(30);

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private volatile DeviceHealth _health;

    public DeviceWorker(DeviceConfig config, IDeviceDriver driver, TagStore store, ILogger logger)
    {
        _config = config;
        _driver = driver;
        _store = store;
        _logger = logger;
        _tagsById = config.Tags.ToDictionary(t => t.Id);
        _health = new DeviceHealth { DeviceId = config.DeviceId };
    }

    public string DeviceId => _config.DeviceId;
    public DeviceHealth Health => _health;
    public IReadOnlyCollection<string> TagIds => _tagsById.Keys;

    /// <summary>
    /// How the driver is actually talking to this device (per request block), when it can report it.
    /// Empty for drivers that don't group reads.
    /// </summary>
    public IReadOnlyList<DriverBlockStat> Blocks =>
        _driver is IDriverDiagnostics diagnostics ? diagnostics.Blocks : Array.Empty<DriverBlockStat>();

    public void Start()
    {
        if (_runTask is not null)
            return;
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = _backoffInitial;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState(DeviceState.Connecting);
                _logger.LogInformation("Device {DeviceId}: connecting via {Driver}", _config.DeviceId, _config.Driver);
                await _driver.ConnectAsync(ct);
                SetState(DeviceState.Connected);
                backoff = _backoffInitial; // reset after a successful connect

                using var timer = new PeriodicTimer(_config.ScanInterval);
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var sw = Stopwatch.StartNew();
                    IReadOnlyList<TagRead> reads;
                    await _driverLock.WaitAsync(ct);
                    try
                    {
                        reads = await _driver.PollAsync(ct);
                    }
                    finally
                    {
                        _driverLock.Release();
                    }
                    sw.Stop();

                    _store.Apply(ApplyScaling(reads));
                    RecordSuccess(sw.Elapsed);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break; // normal shutdown
            }
            catch (Exception ex)
            {
                RecordError(ex);
                _store.MarkBad(_config.Tags, ex.Message);
                _logger.LogWarning(ex, "Device {DeviceId}: fault; retrying in {Backoff}", _config.DeviceId, backoff);

                await SafeDisconnectAsync();
                if (!await DelayAsync(backoff, ct))
                    break;
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _backoffMax.Ticks));
            }
        }

        await SafeDisconnectAsync();
        SetState(DeviceState.Disconnected);
    }

    /// <summary>
    /// Write a value to one of this device's tags. The caller supplies the <em>engineering</em>
    /// value (what the operator sees); it is converted back to the device's raw representation and
    /// range-checked here, so scaling is symmetric with the read path and an impossible command is
    /// refused before it reaches the device. Serialized with the poll loop.
    /// </summary>
    public async Task WriteAsync(string tagId, object value, CancellationToken ct)
    {
        if (!_tagsById.TryGetValue(tagId, out var tag))
            throw new KeyNotFoundException($"Tag '{tagId}' does not belong to device '{_config.DeviceId}'.");
        if (_health.State != DeviceState.Connected)
            throw new InvalidOperationException($"Device '{_config.DeviceId}' is not connected.");

        if (!Scaling.ToRaw(tag, value, out var raw, out var error))
            throw new ArgumentException(error, nameof(value));

        await _driverLock.WaitAsync(ct);
        try
        {
            await _driver.WriteAsync(tagId, raw, ct);
        }
        finally
        {
            _driverLock.Release();
        }

        _logger.LogInformation("Device {DeviceId}: wrote {Engineering} (raw {Raw}) to {TagId}",
            _config.DeviceId, value, raw, tagId);
    }

    private IReadOnlyList<TagRead> ApplyScaling(IReadOnlyList<TagRead> reads)
    {
        // Fast path: nothing configured with scaling.
        if (_tagsById.Values.All(Scaling.IsIdentity))
            return reads;

        var scaled = new List<TagRead>(reads.Count);
        foreach (var read in reads)
        {
            scaled.Add(_tagsById.TryGetValue(read.TagId, out var tag)
                ? new TagRead(read.TagId, Scaling.Apply(tag, read.Value))
                : read);
        }
        return scaled;
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SafeDisconnectAsync()
    {
        try
        {
            await _driver.DisconnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device {DeviceId}: error during disconnect (ignored)", _config.DeviceId);
        }
    }

    private void SetState(DeviceState state) => _health = _health with { State = state };

    private void RecordSuccess(TimeSpan latency) => _health = _health with
    {
        State = DeviceState.Connected,
        LastSuccess = DateTimeOffset.UtcNow,
        LastPollLatency = latency,
        PollCount = _health.PollCount + 1,
    };

    private void RecordError(Exception ex) => _health = _health with
    {
        State = DeviceState.Faulted,
        LastError = ex.Message,
        ErrorCount = _health.ErrorCount + 1,
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            if (_runTask is not null)
                await _runTask;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device {DeviceId}: error during dispose (ignored)", _config.DeviceId);
        }
        finally
        {
            _cts?.Dispose();
            await _driver.DisposeAsync();
        }
    }
}
