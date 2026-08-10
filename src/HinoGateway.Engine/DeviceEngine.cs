using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Engine;

/// <summary>
/// Owns the set of device workers and the shared <see cref="TagStore"/>. Resolves the right driver
/// factory per device, starts/stops the workers, and exposes aggregate health. This is the
/// top-level entry point the host (ASP.NET Core) drives.
///
/// The running state is a single <em>immutable</em> snapshot swapped atomically. Readers — health
/// polling from the UI, a write command being routed — take one reference and work from it, so they
/// can never observe a runtime that is halfway through being rebuilt. Applying a configuration
/// validates and constructs everything <em>before</em> the old workers are stopped, so a bad
/// connection can neither abort startup nor leave a reload half-applied: it is reported as a faulted
/// device while every healthy connection keeps running.
/// </summary>
public sealed class DeviceEngine : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, IDeviceDriverFactory> _factories;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DeviceEngine> _logger;
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    /// <summary>The live runtime. Volatile: readers must see a fully built snapshot or the old one.</summary>
    private volatile Runtime _runtime = Runtime.Empty;

    public DeviceEngine(IEnumerable<IDeviceDriverFactory> factories, TagStore store, ILoggerFactory loggerFactory)
    {
        _factories = factories.ToDictionary(f => f.DriverType, StringComparer.OrdinalIgnoreCase);
        Tags = store;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DeviceEngine>();
    }

    public TagStore Tags { get; }

    /// <summary>Health of every configured device: the running ones plus the rejected ones.</summary>
    public IReadOnlyList<DeviceHealth> Health
    {
        get
        {
            var runtime = _runtime;   // single read — the snapshot cannot change under us
            var health = new List<DeviceHealth>(runtime.Workers.Count + runtime.Rejected.Count);
            foreach (var worker in runtime.Workers)
                health.Add(worker.Health);
            health.AddRange(runtime.Rejected);
            return health;
        }
    }

    /// <summary>
    /// Build and publish a configuration without starting the workers (startup path — the host calls
    /// <see cref="StartAll"/> once everything else is wired up).
    /// </summary>
    public Task<EngineApplyResult> LoadAsync(IEnumerable<DeviceConfig> devices) =>
        ApplyAsync(devices, start: false);

    public void StartAll()
    {
        var runtime = _runtime;
        foreach (var worker in runtime.Workers)
            worker.Start();
        _logger.LogInformation("DeviceEngine started {Count} device worker(s)", runtime.Workers.Count);
    }

    /// <summary>
    /// Apply a new configuration at runtime: build and validate the new runtime, then stop the old
    /// workers, swap, and start. Serialized so overlapping applies cannot race. For the modest
    /// device counts of a gateway, a full rebuild is simpler and safer than per-device diffing.
    /// </summary>
    public Task<EngineApplyResult> ReloadAsync(IEnumerable<DeviceConfig> devices) =>
        ApplyAsync(devices, start: true);

    private async Task<EngineApplyResult> ApplyAsync(IEnumerable<DeviceConfig> devices, bool start)
    {
        await _applyLock.WaitAsync();
        try
        {
            // 1. Build the new runtime first. Nothing here touches the running one, so if this
            //    throws (a driver bug), the gateway carries on with the configuration it has.
            var next = Build(devices);

            // 2. Stop the old workers, then publish the new snapshot in one assignment.
            var previous = _runtime;
            await StopAsync(previous);
            _runtime = next;

            // 3. Drop values of tags that no longer exist, and tell the operator about the tags of
            //    every rejected device instead of letting them silently vanish from the UI.
            Tags.RetainOnly(next.ConfiguredTagIds);
            foreach (var rejected in next.RejectedTags)
                Tags.MarkBad(rejected.Tags, rejected.Reason);

            if (start)
                StartAll();

            var result = new EngineApplyResult(next.Workers.Count, next.Rejected);
            if (next.Rejected.Count > 0)
                _logger.LogWarning("Applied configuration with {Rejected} rejected connection(s): {Ids}",
                    next.Rejected.Count, string.Join(", ", next.Rejected.Select(r => r.DeviceId)));
            return result;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    /// <summary>
    /// Per-device request diagnostics: what the drivers are actually putting on the wire. Reading a
    /// single snapshot keeps this consistent even while a reload is in progress.
    /// </summary>
    public IReadOnlyList<DeviceBlockDiagnostics> Diagnostics
    {
        get
        {
            var runtime = _runtime;
            return runtime.Workers
                .Select(w => new DeviceBlockDiagnostics(w.DeviceId, w.Blocks))
                .Where(d => d.Blocks.Count > 0)
                .ToList();
        }
    }

    /// <summary>Write a value to a tag, routed to the device that owns it.</summary>
    public async Task WriteTagAsync(string tagId, object value, CancellationToken ct = default)
    {
        if (!_runtime.WorkersByTag.TryGetValue(tagId, out var worker))
            throw new KeyNotFoundException($"No running device owns tag '{tagId}'.");
        await worker.WriteAsync(tagId, value, ct);
    }

    // --- building -------------------------------------------------------------

    /// <summary>
    /// Construct (but do not start) a complete runtime. Every device is validated by its driver
    /// factory first; an unusable one becomes a faulted health entry with the reasons, and the rest
    /// of the configuration is unaffected.
    /// </summary>
    private Runtime Build(IEnumerable<DeviceConfig> devices)
    {
        var workers = new List<DeviceWorker>();
        var workersByTag = new Dictionary<string, DeviceWorker>(StringComparer.Ordinal);
        var rejected = new List<DeviceHealth>();
        var rejectedTags = new List<RejectedDevice>();
        var configuredTagIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            foreach (var tag in device.Tags)
                configuredTagIds.Add(tag.Id);

            if (!_factories.TryGetValue(device.Driver, out var factory))
            {
                Reject(device, $"Unknown driver '{device.Driver}'. Available: {string.Join(", ", _factories.Keys)}.");
                continue;
            }

            var problems = SafeValidate(factory, device);
            if (problems.Count > 0)
            {
                Reject(device, string.Join(" | ", problems));
                continue;
            }

            IDeviceDriver driver;
            try
            {
                driver = factory.Create(device);
            }
            catch (Exception ex)
            {
                // Validation should have caught this; a driver that still throws must not be able to
                // take the gateway down.
                Reject(device, $"Driver could not be created: {ex.Message}");
                continue;
            }

            var worker = new DeviceWorker(device, driver, Tags, _loggerFactory.CreateLogger($"Device.{device.DeviceId}"));
            workers.Add(worker);
            foreach (var tagId in worker.TagIds)
                workersByTag[tagId] = worker;
        }

        return new Runtime(workers, workersByTag, rejected, rejectedTags, configuredTagIds);

        void Reject(DeviceConfig device, string reason)
        {
            _logger.LogError("Device {DeviceId}: rejected — {Reason}", device.DeviceId, reason);
            rejected.Add(new DeviceHealth
            {
                DeviceId = device.DeviceId,
                State = DeviceState.Faulted,
                LastError = reason,
                ErrorCount = 1,
            });
            rejectedTags.Add(new RejectedDevice(device.Tags, reason));
        }
    }

    private IReadOnlyList<string> SafeValidate(IDeviceDriverFactory factory, DeviceConfig device)
    {
        try
        {
            return factory.Validate(device);
        }
        catch (Exception ex)
        {
            return [$"Driver validation failed: {ex.Message}"];
        }
    }

    private async Task StopAsync(Runtime runtime)
    {
        // Stop all workers concurrently so shutdown isn't serialized across slow devices.
        await Task.WhenAll(runtime.Workers.Select(w => w.DisposeAsync().AsTask()));
    }

    public async ValueTask DisposeAsync()
    {
        await _applyLock.WaitAsync();
        try
        {
            await StopAsync(_runtime);
            _runtime = Runtime.Empty;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private sealed record RejectedDevice(IReadOnlyList<TagDefinition> Tags, string Reason);

    /// <summary>
    /// An immutable point-in-time runtime. Never mutated after publication — a change means building
    /// a new one and swapping the reference.
    /// </summary>
    private sealed record Runtime(
        IReadOnlyList<DeviceWorker> Workers,
        IReadOnlyDictionary<string, DeviceWorker> WorkersByTag,
        IReadOnlyList<DeviceHealth> Rejected,
        IReadOnlyList<RejectedDevice> RejectedTags,
        IReadOnlySet<string> ConfiguredTagIds)
    {
        public static readonly Runtime Empty = new(
            Array.Empty<DeviceWorker>(),
            new Dictionary<string, DeviceWorker>(),
            Array.Empty<DeviceHealth>(),
            Array.Empty<RejectedDevice>(),
            new HashSet<string>());
    }
}

/// <summary>
/// Outcome of applying a configuration: how many connections are running, and every connection that
/// was refused with the reason — so the API and UI can tell the engineer exactly what to fix.
/// </summary>
public sealed record EngineApplyResult(int Started, IReadOnlyList<DeviceHealth> Rejected);

/// <summary>The request blocks one device's driver is issuing, and how each is behaving.</summary>
public sealed record DeviceBlockDiagnostics(string DeviceId, IReadOnlyList<DriverBlockStat> Blocks);
