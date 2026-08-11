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

    /// <summary>
    /// Links shared by several connections (an RS-485 port, a gateway socket). Owned by the engine
    /// rather than by a runtime snapshot, so a connection that survives a reload keeps its link open.
    /// </summary>
    private readonly TransportRegistry _transports;

    /// <summary>Turns secret references into the real values, for drivers that need credentials.</summary>
    private readonly ISecretStore? _secrets;

    /// <summary>Where retentive memory tags keep their value across a restart. Null = not retained.</summary>
    private readonly IRetainedValueStore? _retained;

    /// <summary>The live runtime. Volatile: readers must see a fully built snapshot or the old one.</summary>
    private volatile Runtime _runtime = Runtime.Empty;
    private long _abandonedWorkers;

    /// <param name="secrets">
    /// Resolves <c>${secret:...}</c> / <c>${env:...}</c> settings just before a driver is built. Null
    /// (the default) means a gateway with no secret store: references then resolve to nothing and the
    /// connection is told why.
    /// </param>
    /// <param name="retained">
    /// Persists the memory tags marked <see cref="TagDefinition.Retentive"/>. Null (the default)
    /// means nothing survives a restart, which is the safe position for a control setpoint.
    /// </param>
    public DeviceEngine(IEnumerable<IDeviceDriverFactory> factories, TagStore store, ILoggerFactory loggerFactory,
        ISecretStore? secrets = null, IRetainedValueStore? retained = null)
    {
        _secrets = secrets;
        _retained = retained;
        _factories = factories.ToDictionary(f => f.DriverType, StringComparer.OrdinalIgnoreCase);
        Tags = store;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DeviceEngine>();
        _transports = new TransportRegistry(loggerFactory.CreateLogger<TransportRegistry>());
    }

    public TagStore Tags { get; }
    public long AbandonedWorkers => Interlocked.Read(ref _abandonedWorkers)
                                    + _runtime.Workers.LongCount(w => w.IsAbandoned);
    public bool RequiresRestart => AbandonedWorkers > 0;

    /// <summary>The shared links currently open, and how many connections are using each.</summary>
    public IReadOnlyList<TransportInfo> Transports => _transports.Snapshot;

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

    /// <summary>Counters of the memory and expression tags this engine is running.</summary>
    public InternalTagStats InternalTags => _runtime.Internal?.Stats ?? new InternalTagStats(0, 0, 0, 0, 0);

    /// <summary>
    /// Build and publish a configuration without starting the workers (startup path — the host calls
    /// <see cref="StartAll"/> once everything else is wired up).
    /// </summary>
    /// <param name="internalTags">
    /// The memory and expression tags. Optional so that every existing caller and test — which knew
    /// only of device tags — still compiles and behaves identically.
    /// </param>
    public Task<EngineApplyResult> LoadAsync(
        IEnumerable<DeviceConfig> devices, IReadOnlyList<TagDefinition>? internalTags = null) =>
        ApplyAsync(devices, internalTags, start: false);

    public void StartAll()
    {
        var runtime = _runtime;
        foreach (var worker in runtime.Workers)
            worker.Start();
        runtime.Internal?.Start();
        _logger.LogInformation("DeviceEngine started {Count} device worker(s)", runtime.Workers.Count);
    }

    /// <summary>
    /// Apply a new configuration at runtime: build and validate the new runtime, then stop the old
    /// workers, swap, and start. Serialized so overlapping applies cannot race. For the modest
    /// device counts of a gateway, a full rebuild is simpler and safer than per-device diffing.
    /// </summary>
    public Task<EngineApplyResult> ReloadAsync(
        IEnumerable<DeviceConfig> devices, IReadOnlyList<TagDefinition>? internalTags = null) =>
        ApplyAsync(devices, internalTags, start: true);

    private async Task<EngineApplyResult> ApplyAsync(
        IEnumerable<DeviceConfig> devices, IReadOnlyList<TagDefinition>? internalTags, bool start)
    {
        await _applyLock.WaitAsync();
        try
        {
            // 1. Build the new runtime first. Nothing here touches the running one, so if this
            //    throws (a driver bug), the gateway carries on with the configuration it has.
            var next = await BuildAsync(devices, internalTags);

            // 2. Publish the new routing snapshot first, then drain commands already routed to the
            //    previous one before disposing its drivers. New writes can no longer acquire the old
            //    runtime, so a command can never race a driver's disposal.
            var previous = _runtime;
            _runtime = next;
            await previous.RetireAndDrainAsync();
            await StopAsync(previous);
            if (RequiresRestart)
                throw new GatewayRestartRequiredException(
                    "A previous device worker ignored its shutdown deadline. The replacement runtime was not started; " +
                    "the service supervisor must restart this process to prevent duplicate workers.");

            // 3. Drop values of tags that no longer exist, and tell the operator about the tags of
            //    every rejected device instead of letting them silently vanish from the UI.
            Tags.RetainOnly(next.ConfiguredTagIds);
            foreach (var rejected in next.RejectedTags)
                Tags.MarkBad(rejected.Tags, rejected.Reason);

            if (start)
                StartAll();

            var result = new EngineApplyResult(next.Workers.Count, next.Rejected, next.DroppedTags);
            if (next.Rejected.Count > 0)
                _logger.LogWarning("Applied configuration with {Rejected} rejected connection(s): {Ids}",
                    next.Rejected.Count, string.Join(", ", next.Rejected.Select(r => r.DeviceId)));
            if (next.DroppedTags.Count > 0)
                _logger.LogWarning("Applied configuration with {Dropped} unusable tag(s): {Tags}",
                    next.DroppedTags.Count, string.Join(", ", next.DroppedTags.Select(t => t.TagId)));
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
        while (true)
        {
            var runtime = _runtime;
            if (!runtime.TryAcquireWrite(out var lease))
            {
                await Task.Yield();
                continue; // reload retired it between the reference read and lease acquisition
            }

            using (lease)
            {
                // A memory tag belongs to no device, so it is looked up before giving up on the write.
                if (runtime.Internal is { } internals && internals.Owns(tagId))
                {
                    await internals.WriteAsync(tagId, value, ct);
                    return;
                }

                if (!runtime.WorkersByTag.TryGetValue(tagId, out var worker))
                    throw new KeyNotFoundException($"No running device owns tag '{tagId}'.");
                await worker.WriteAsync(tagId, value, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Stable serialization key for control commands. Commands for one physical connection must
    /// retain admission order even when the command service uses multiple workers; unrelated
    /// connections may still execute concurrently.
    /// </summary>
    public string WritePartitionKey(string tagId)
    {
        var runtime = _runtime;
        if (runtime.Internal is { } internals && internals.Owns(tagId))
            return InternalDeviceId;
        return runtime.WorkersByTag.TryGetValue(tagId, out var worker) ? worker.DeviceId : tagId;
    }

    // --- building -------------------------------------------------------------

    /// <summary>
    /// Construct (but do not start) a complete runtime. Every device is validated by its driver
    /// factory first; an unusable one becomes a faulted health entry with the reasons, and the rest
    /// of the configuration is unaffected.
    /// </summary>
    private async Task<Runtime> BuildAsync(IEnumerable<DeviceConfig> devices, IReadOnlyList<TagDefinition>? internalTags)
    {
        var configured = devices.ToList();
        var workers = new List<DeviceWorker>();
        var leases = new List<TransportRegistry.TransportLease>();
        var workersByTag = new Dictionary<string, DeviceWorker>(StringComparer.Ordinal);
        var rejected = new List<DeviceHealth>();
        var rejectedTags = new List<RejectedDevice>();
        var droppedTags = new List<DroppedTag>();
        var configuredTagIds = new HashSet<string>(StringComparer.Ordinal);

        // Validate identifiers across the complete project before constructing a single driver.
        // Ambiguous routing is unsafe for reads and especially for control writes, so every device
        // participating in a duplicate is rejected rather than letting "last one wins" decide.
        var globalProblems = ValidateGlobalIdentifiers(configured);

        foreach (var device in configured)
        {
            foreach (var tag in device.Tags)
                configuredTagIds.Add(tag.Id);

            if (globalProblems.TryGetValue(device, out var identifierProblems))
            {
                Reject(device, string.Join(" | ", identifierProblems));
                continue;
            }

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

            // A tag the driver cannot use is the tag's problem, not the connection's: drop those
            // tags, keep the connection polling the rest, and show each dropped tag as Bad with the
            // reason. One mistyped address used to blind every tag behind the same connection.
            var effective = device;
            var tagProblems = SafeValidateTags(factory, device);
            if (tagProblems.Count > 0)
            {
                foreach (var (tagId, reason) in tagProblems)
                {
                    var tag = device.Tags.FirstOrDefault(t => string.Equals(t.Id, tagId, StringComparison.Ordinal));
                    if (tag is null)
                        continue;   // a driver blaming a tag that is not ours — ignore rather than trust it
                    _logger.LogWarning("Device {DeviceId}: tag {TagId} dropped — {Reason}",
                        device.DeviceId, tagId, reason);
                    droppedTags.Add(new DroppedTag(device.DeviceId, tagId, reason));
                    rejectedTags.Add(new RejectedDevice([tag], reason));
                }

                var unusable = droppedTags.Where(d => d.DeviceId == device.DeviceId)
                    .Select(d => d.TagId).ToHashSet(StringComparer.Ordinal);
                effective = device with { Tags = device.Tags.Where(t => !unusable.Contains(t.Id)).ToList() };
            }

            // Passwords and keys live outside the project file as ${secret:...} / ${env:...}
            // references; the driver is handed the real values and never sees the reference. Done
            // after validation on purpose — a validator that echoes a rejected value in its message
            // would otherwise put a password into the log and onto the operator's screen.
            var resolved = Secrets.Resolve(effective.Settings, _secrets,
                problem => _logger.LogWarning("Device {DeviceId}: {Problem}", device.DeviceId, problem));
            if (!ReferenceEquals(resolved, effective.Settings))
                effective = effective with { Settings = resolved };

            // A connection that names a shared link (an RS-485 port, a gateway socket) gets the one
            // instance the engine keeps for that key; every other connection with the same key gets
            // the same object and their requests are serialised on it.
            TransportRegistry.TransportLease? lease;
            try
            {
                lease = AcquireTransport(factory, effective);
            }
            catch (Exception ex)
            {
                Reject(device, $"Shared link could not be prepared: {ex.Message}");
                continue;
            }

            IDeviceDriver driver;
            try
            {
                driver = factory.Create(effective, lease?.Transport);
            }
            catch (Exception ex)
            {
                // Validation should have caught this; a driver that still throws must not be able to
                // take the gateway down.
                ReleaseQuietly(lease);
                Reject(device, $"Driver could not be created: {ex.Message}");
                continue;
            }

            if (lease is not null)
                leases.Add(lease);

            var worker = new DeviceWorker(effective, driver, Tags, _loggerFactory.CreateLogger($"Device.{device.DeviceId}"));
            workers.Add(worker);
            foreach (var tagId in worker.TagIds)
                workersByTag[tagId] = worker;
        }

        // The internal tags are built last, and deliberately before the old runtime is stopped: their
        // memory values are seeded from what the tag store still holds, which is how an operator's
        // setpoint survives an "Apply to engine".
        InternalTagEngine? internals = null;
        try
        {
            if (internalTags is { Count: > 0 })
            {
                internals = new InternalTagEngine(Tags, _loggerFactory.CreateLogger("Tags.Internal"), _retained);
                internals.Load(internalTags);
                foreach (var tagId in internals.TagIds)
                    configuredTagIds.Add(tagId);

                // A formula that does not compile is reported the same way a tag its driver refused is:
                // the operator sees it on the apply, not only as a Bad tag on the Live page.
                foreach (var (tagId, reason) in internals.Faults)
                    droppedTags.Add(new DroppedTag(InternalDeviceId, tagId, reason));
            }

            return new Runtime(workers, workersByTag, rejected, rejectedTags, droppedTags, configuredTagIds, leases, internals);
        }
        catch
        {
            if (internals is not null)
                try { await internals.DisposeAsync(); } catch { }
            foreach (var worker in workers)
                try { await worker.DisposeAsync(); } catch { }
            foreach (var lease in leases)
                try { await lease.DisposeAsync(); } catch { }
            throw;
        }

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

    private static Dictionary<DeviceConfig, List<string>> ValidateGlobalIdentifiers(
        IReadOnlyList<DeviceConfig> devices)
    {
        var problems = new Dictionary<DeviceConfig, List<string>>(ReferenceEqualityComparer.Instance);

        void Add(DeviceConfig device, string message)
        {
            if (!problems.TryGetValue(device, out var list))
                problems[device] = list = new List<string>();
            list.Add(message);
        }

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceId))
                Add(device, "Device ID is required.");
            if (string.IsNullOrWhiteSpace(device.Driver))
                Add(device, "Driver type is required.");

            foreach (var tag in device.Tags)
                if (string.IsNullOrWhiteSpace(tag.Id))
                    Add(device, $"Tag '{tag.Name}' has an empty runtime ID.");
        }

        foreach (var group in devices
                     .Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
                     .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            foreach (var device in group)
                Add(device, $"Duplicate device ID '{group.Key}'.");
        }

        foreach (var group in devices
                     .SelectMany(d => d.Tags.Select(t => (Device: d, Tag: t)))
                     .Where(x => !string.IsNullOrWhiteSpace(x.Tag.Id))
                     .GroupBy(x => x.Tag.Id, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            var owners = string.Join(", ", group.Select(x => x.Device.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (var device in group.Select(x => x.Device).Distinct())
                Add(device, $"Duplicate runtime tag ID '{group.Key}' (owners: {owners}).");
        }

        return problems;
    }

    /// <summary>
    /// Asks the driver whether this connection shares a link, and takes a claim on it if so. The key
    /// is namespaced by driver type so two protocols can never collide on the same string, and a
    /// factory that throws while answering is treated as "no sharing" — an optional feature must not
    /// be able to stop a connection from running.
    /// </summary>
    private TransportRegistry.TransportLease? AcquireTransport(IDeviceDriverFactory factory, DeviceConfig device)
    {
        string? key;
        try
        {
            key = factory.TransportKey(device);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device {DeviceId}: driver failed to report its transport key; " +
                                 "the connection will use a link of its own", device.DeviceId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(key))
            return null;

        var scoped = $"{factory.DriverType}|{key.Trim()}";
        var lease = _transports.Acquire(scoped, () => factory.CreateTransport(key.Trim(), device));
        _logger.LogInformation("Device {DeviceId}: using shared link {Key}", device.DeviceId, scoped);
        return lease;
    }

    private void ReleaseQuietly(TransportRegistry.TransportLease? lease)
    {
        if (lease is null)
            return;

        // Fire and forget on the failure path: releasing only decrements a counter, and the last
        // release closes a link that was never used.
        _ = lease.DisposeAsync().AsTask().ContinueWith(
            t => _logger.LogWarning(t.Exception, "Error releasing shared link {Key}", lease.Key),
            TaskContinuationOptions.OnlyOnFaulted);
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

    /// <summary>
    /// Per-tag validation, defensively: a factory that throws here must not cost the connection its
    /// other tags, so a failure is reported as a problem on every tag it was asked about.
    /// </summary>
    private IReadOnlyDictionary<string, string> SafeValidateTags(IDeviceDriverFactory factory, DeviceConfig device)
    {
        try
        {
            return factory.ValidateTags(device);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Device {DeviceId}: tag validation threw", device.DeviceId);
            return device.Tags.ToDictionary(t => t.Id, _ => $"Driver tag validation failed: {ex.Message}",
                StringComparer.Ordinal);
        }
    }

    private async Task StopAsync(Runtime runtime)
    {
        // The internal tags stop first: their evaluations read the values the device workers publish,
        // and there is no reason to keep computing from a fleet that is being taken down.
        if (runtime.Internal is { } internals)
            await internals.DisposeAsync();

        // Stop all workers concurrently so shutdown isn't serialized across slow devices.
        await Task.WhenAll(runtime.Workers.Select(w => w.DisposeAsync().AsTask()));
        var abandoned = runtime.Workers.Count(w => w.IsAbandoned);
        if (abandoned > 0)
        {
            Interlocked.Add(ref _abandonedWorkers, abandoned);
            _logger.LogCritical(
                "{Count} device worker(s) could not be stopped. The gateway process requires restart; " +
                "further reloads may leak threads.", abandoned);
        }

        // Only then let go of the shared links: a lease released while its driver is still shutting
        // down could close the socket under the driver's own disconnect.
        // Release independent links concurrently. If several physical endpoints are slow to close,
        // shutdown/reload must pay the longest close deadline once, not once per endpoint in series.
        await Task.WhenAll(runtime.Leases.Select(lease => lease.DisposeAsync().AsTask()));
    }

    public async ValueTask DisposeAsync()
    {
        await _applyLock.WaitAsync();
        try
        {
            var previous = _runtime;
            _runtime = Runtime.Empty;
            await previous.RetireAndDrainAsync();
            await StopAsync(previous);
            await _transports.DisposeAsync();
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private sealed record RejectedDevice(IReadOnlyList<TagDefinition> Tags, string Reason);

    /// <summary>A tag its own driver refused, while the connection it belongs to keeps running.</summary>
    public sealed record DroppedTag(string DeviceId, string TagId, string Reason);

    /// <summary>
    /// Stands in for the connection of a memory or expression tag, which belongs to none. Used only
    /// where a message has to name something — a dropped-tag report, a log line.
    /// </summary>
    public const string InternalDeviceId = "(internal)";

    /// <summary>
    /// An immutable point-in-time runtime. Never mutated after publication — a change means building
    /// a new one and swapping the reference.
    /// </summary>
    private sealed record Runtime(
        IReadOnlyList<DeviceWorker> Workers,
        IReadOnlyDictionary<string, DeviceWorker> WorkersByTag,
        IReadOnlyList<DeviceHealth> Rejected,
        IReadOnlyList<RejectedDevice> RejectedTags,
        IReadOnlyList<DroppedTag> DroppedTags,
        IReadOnlySet<string> ConfiguredTagIds,
        IReadOnlyList<TransportRegistry.TransportLease> Leases,
        InternalTagEngine? Internal)
    {
        private readonly object _leaseGate = new();
        private int _activeWrites;
        private bool _acceptingWrites = true;
        private TaskCompletionSource? _drained;

        public static Runtime Empty => new(
            Array.Empty<DeviceWorker>(),
            new Dictionary<string, DeviceWorker>(),
            Array.Empty<DeviceHealth>(),
            Array.Empty<RejectedDevice>(),
            Array.Empty<DroppedTag>(),
            new HashSet<string>(),
            Array.Empty<TransportRegistry.TransportLease>(),
            null);

        public bool TryAcquireWrite(out IDisposable lease)
        {
            lock (_leaseGate)
            {
                if (!_acceptingWrites)
                {
                    lease = null!;
                    return false;
                }
                _activeWrites++;
                lease = new WriteLease(this);
                return true;
            }
        }

        public Task RetireAndDrainAsync()
        {
            lock (_leaseGate)
            {
                _acceptingWrites = false;
                if (_activeWrites == 0)
                    return Task.CompletedTask;
                _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }

        private void ReleaseWrite()
        {
            lock (_leaseGate)
            {
                _activeWrites--;
                if (!_acceptingWrites && _activeWrites == 0)
                    _drained?.TrySetResult();
            }
        }

        private sealed class WriteLease(Runtime runtime) : IDisposable
        {
            private Runtime? _runtime = runtime;
            public void Dispose() => Interlocked.Exchange(ref _runtime, null)?.ReleaseWrite();
        }
    }
}

/// <summary>
/// Outcome of applying a configuration: how many connections are running, and every connection that
/// was refused with the reason — so the API and UI can tell the engineer exactly what to fix.
/// </summary>
public sealed record EngineApplyResult(
    int Started,
    IReadOnlyList<DeviceHealth> Rejected,
    IReadOnlyList<DeviceEngine.DroppedTag> DroppedTags);

/// <summary>The request blocks one device's driver is issuing, and how each is behaving.</summary>
public sealed record DeviceBlockDiagnostics(string DeviceId, IReadOnlyList<DriverBlockStat> Blocks);

public sealed class GatewayRestartRequiredException(string message) : Exception(message);
