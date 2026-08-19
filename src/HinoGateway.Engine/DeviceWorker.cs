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

    /// <summary>
    /// Highest value accepted so far on each <see cref="TagDefinition.Monotonic"/> tag, and how many
    /// readings in a row have come in below it. Only ever touched from the poll loop.
    /// </summary>
    private readonly Dictionary<string, CounterGuard> _counters;

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
        _counters = config.Tags.Where(t => t.Monotonic)
            .ToDictionary(t => t.Id, _ => new CounterGuard(double.NegativeInfinity, 0), StringComparer.Ordinal);
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

    /// <summary>
    /// The poll loop would not stop within <see cref="StopDeadline"/> and has been left running.
    /// The engine counts these and reports that the process needs restarting: the thread, its
    /// driver and its link are all still held, so further reloads would leak more of them.
    /// </summary>
    public bool IsAbandoned => _abandoned;

    private volatile bool _abandoned;

    /// <summary>
    /// How long a reload or shutdown waits for one device loop. Long enough for a poll in flight on
    /// a slow link to finish, short enough that one dead device cannot hold up the whole fleet.
    /// Shared with the drivers, whose configuration validation is written against the same number.
    /// </summary>
    private static readonly TimeSpan StopDeadline = DriverLimits.StopDeadline;

    public void Start()
    {
        if (_runTask is not null)
            return;
        _cts = new CancellationTokenSource();

        // A dedicated thread, NOT Task.Run: industrial protocol clients block inside their *Async
        // methods, and a few dozen of those on the thread pool starve it — the pool grows by about
        // one thread per second, so timers stop firing and unrelated devices go Bad. Measured
        // 2026-08-01: 150 devices behind a 200 ms link managed 12.8 of 65 expected polls on the
        // pool, and 59 of 65 once the loops were kept off it. See WorkerThread.
        _runTask = WorkerThread.Run($"device-{_config.DeviceId}", () => RunAsync(_cts.Token),
            ex => _logger.LogError(ex, "Device {DeviceId}: callback failed", _config.DeviceId));
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
                var consecutiveFailures = 0;
                var failureThreshold = Math.Max(1, _config.FailureThreshold);

                using var timer = new PeriodicTimer(_config.ScanInterval);
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var sw = Stopwatch.StartNew();
                    IReadOnlyList<TagRead> reads;
                    try
                    {
                        await _driverLock.WaitAsync(ct);
                        try
                        {
                            reads = await _driver.PollAsync(ct);
                        }
                        finally
                        {
                            _driverLock.Release();
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        consecutiveFailures++;
                        var transportGone = _driver is ITransportFaultClassifier classifier
                            ? classifier.IsTransportFault(ex)
                            : TransportFaults.IsConnectionGone(ex);

                        if (transportGone || consecutiveFailures >= failureThreshold)
                            throw;

                        RecordTransientError(ex);
                        _store.MarkUncertain(_config.Tags,
                            $"Poll failed ({consecutiveFailures}/{failureThreshold}): {ex.Message}");
                        _logger.LogWarning(ex,
                            "Device {DeviceId}: transient poll failure {Failures}/{Threshold}; keeping the link open",
                            _config.DeviceId, consecutiveFailures, failureThreshold);
                        continue;
                    }
                    sw.Stop();

                    consecutiveFailures = 0;
                    _store.Apply(ApplyScaling(reads));
                    RecordSuccess(sw.Elapsed);
                    // A connected socket is not proof that the PLC recovered; a successful
                    // protocol transaction is. Preserve exponential backoff until this point.
                    backoff = _backoffInitial;
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
        TagWritePolicy.EnsureAllowed(tag, value);
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
        // Fast path: nothing configured with scaling and no counter to guard.
        if (_counters.Count == 0 && _tagsById.Values.All(Scaling.IsIdentity))
            return reads;

        var scaled = new List<TagRead>(reads.Count);
        foreach (var read in reads)
        {
            if (!_tagsById.TryGetValue(read.TagId, out var tag))
            {
                scaled.Add(read);
                continue;
            }
            var value = Scaling.Apply(tag, read.Value);
            if (tag.Monotonic)
                value = GuardCounter(tag, value);
            scaled.Add(new TagRead(read.TagId, value));
        }
        return scaled;
    }

    /// <summary>
    /// Enforce <see cref="TagDefinition.Monotonic"/>.
    ///
    /// <para>
    /// The setting has been offered on the tag editor since the tag database existed, and until now
    /// nothing anywhere acted on it — an operator could tick "cumulative counter" on an energy
    /// register and be told nothing was protecting it. On such a register a value below the highest
    /// one already accepted is not a smaller measurement, it is a bad read: a mis-framed response, the
    /// wrong word order, a decode error. It is published <see cref="Quality.Uncertain"/> with the
    /// reason instead of Good, so that everything downstream — alarms, the northbound points, the
    /// accumulators — can see that this number is not to be built on.
    /// </para>
    ///
    /// <para>
    /// Three consecutive drops are accepted as a genuine reset instead: a meter really can be replaced
    /// or a PLC reloaded, and refusing forever would leave the tag dead until somebody noticed. Three
    /// in a row cannot be a transient decode error, and one poll of hesitation is a cheap price.
    /// </para>
    /// </summary>
    private TagValue GuardCounter(TagDefinition tag, TagValue value)
    {
        if (value.Quality != Quality.Good || AsNumber(value.Value) is not { } number)
            return value;

        // Pre-populated for every monotonic tag at construction, so this cannot miss.
        var counter = _counters[tag.Id];
        if (number >= counter.Peak)
        {
            _counters[tag.Id] = new CounterGuard(number, 0);
            return value;
        }

        if (counter.Drops + 1 >= MonotonicResetAfter)
        {
            _logger.LogWarning(
                "Device {DeviceId}: counter {TagId} read below its peak {Peak} {Count} times in a row; " +
                "accepting {Value} as a counter reset",
                _config.DeviceId, tag.Id, counter.Peak, MonotonicResetAfter, number);
            _counters[tag.Id] = new CounterGuard(number, 0);
            return value;
        }

        _counters[tag.Id] = counter with { Drops = counter.Drops + 1 };
        return value with
        {
            Quality = Quality.Uncertain,
            StatusText = $"cumulative counter read {number}, below the highest accepted value {counter.Peak}",
        };
    }

    private static double? AsNumber(object? value) => value switch
    {
        double d => double.IsFinite(d) ? d : null,
        float f => float.IsFinite(f) ? f : null,
        sbyte or byte or short or ushort or int or uint or long or ulong or decimal =>
            Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>Consecutive drops accepted before a monotonic tag is treated as having been reset.</summary>
    private const int MonotonicResetAfter = 3;

    private readonly record struct CounterGuard(double Peak, int Drops);

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
        LastError = null,
    };

    private void RecordTransientError(Exception ex) => _health = _health with
    {
        State = DeviceState.Connected,
        LastError = ex.Message,
        ErrorCount = _health.ErrorCount + 1,
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

            // Cancelling only helps a driver that can see the token. Ask the driver to break its
            // link as well, before the clock below starts: for a protocol library that reads with a
            // socket timeout and no token, closing the socket is the only thing that ends the call,
            // and the difference is between stopping in milliseconds and being abandoned.
            try
            {
                await _driver.AbortAsync().WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Device {DeviceId}: abort request failed (ignored)", _config.DeviceId);
            }

            if (_runTask is not null)
            {
                // Bounded, because an unbounded await here is a hang: a driver that ignores
                // cancellation — a synchronous socket read with no timeout, a serial port whose
                // device was unplugged — would otherwise stop the gateway from ever reloading or
                // shutting down. Giving up is worse than waiting only in the sense that it leaks a
                // thread, and that is why it is reported rather than swallowed.
                await _runTask.WaitAsync(StopDeadline);
            }
        }
        catch (TimeoutException)
        {
            _abandoned = true;
            _logger.LogCritical(
                "Device {DeviceId}: the poll loop did not stop within {Deadline}. Its thread, driver " +
                "and link are still held; the gateway process needs restarting.",
                _config.DeviceId, StopDeadline);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device {DeviceId}: error during dispose (ignored)", _config.DeviceId);
        }
        finally
        {
            _cts?.Dispose();
            if (_abandoned)
            {
                // The loop is still inside the driver. Disposing it from here would block on the
                // same call, so let it go on its own and keep the reload moving.
                _ = Task.Run(async () =>
                {
                    try { await _driver.DisposeAsync(); } catch { /* nothing left to do */ }
                });
            }
            else
            {
                await _driver.DisposeAsync();
            }
        }
    }
}
