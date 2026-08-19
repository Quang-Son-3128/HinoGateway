using System.Collections.Concurrent;
using System.Threading.Channels;
using HinoGateway.Engine;

namespace HinoGateway.Server.Services;

public enum ControlOutcome { Successful, Rejected, Failed, Unknown }

public sealed record ControlCommand(
    string TagId, object Value, string Source, string Actor, string? RemoteAddress,
    string? CorrelationId = null);

public sealed record ControlResult(string CommandId, ControlOutcome Outcome, string? Reason,
    DateTimeOffset AcceptedAt, DateTimeOffset CompletedAt)
{
    /// <summary>The device outcome and audit outcome are separate facts.</summary>
    public bool AuditPersisted { get; init; } = true;
    public string? AuditError { get; init; }
}

public sealed class ControlCommandOptions
{
    public int QueueCapacity { get; set; } = 256;

    /// <summary>
    /// Retained so an existing appsettings.json still binds, and no longer used: there is now one
    /// queue per device rather than a fixed pool that devices were hashed into, so there is no
    /// number here to get right. Removing the property outright would fail configuration binding on
    /// every gateway already deployed.
    /// </summary>
    [Obsolete("Commands are queued per device; this no longer has an effect.")]
    public int WorkerCount { get; set; } = 8;

    public int TimeoutMs { get; set; } = 10_000;
}

/// <summary>
/// Globally bounded command admission with a single-reader queue per device. This preserves FIFO on
/// a physical PLC connection while allowing independent devices to make progress in parallel. Once
/// admitted, every command is completed explicitly, including during shutdown.
///
/// <para>
/// "Per device" was previously approximate: the device id was hashed into a fixed eight partitions,
/// so two unrelated devices shared a queue as soon as their hashes collided — with eight slots that
/// is more likely than not by the fifth device. The consequence was the one this class exists to
/// prevent: a command to a healthy PLC waiting out the full deadline of a command to a dead one.
/// A dictionary keyed by the device itself has no collisions to reason about, and the number of
/// queues is bounded by the number of devices in the project rather than by a constant that had to
/// be guessed.
/// </para>
/// </summary>
public sealed class ControlCommandService : BackgroundService
{
    private readonly ConcurrentDictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private readonly DeviceEngine _engine;
    private readonly AuditTrail _audit;
    private readonly ComponentHealthRegistry _health;
    private readonly ILogger<ControlCommandService> _logger;
    private readonly ControlCommandOptions _options;
    private readonly int _capacity;
    private readonly CancellationTokenSource _lifetime = new();
    private int _pendingCount;
    private int _accepting = 1;

    public ControlCommandService(DeviceEngine engine, AuditTrail audit, ComponentHealthRegistry health,
        ControlCommandOptions options, ILogger<ControlCommandService> logger)
    {
        _engine = engine;
        _audit = audit;
        _health = health;
        _options = options;
        _logger = logger;
        _capacity = Math.Clamp(options.QueueCapacity, 1, 100_000);
    }

    /// <summary>A device's queue and the single task that drains it.</summary>
    private sealed record Lane(Channel<Pending> Queue, Task Pump);

    private Lane LaneFor(string key) =>
        _lanes.GetOrAdd(key, static (id, self) => self.CreateLane(id), this);

    private Lane CreateLane(string key)
    {
        var queue = Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        // Unbounded per lane because admission is counted globally in TryReserve; a second limit here
        // could only reject a command the service has already told the caller it accepted.
        return new Lane(queue, Task.Run(() => WorkerAsync(key, queue, _lifetime.Token)));
    }

    public async Task<ControlResult> ExecuteAsync(ControlCommand command, int? timeoutMs = null,
        CancellationToken deadlineToken = default)
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var id = command.CorrelationId ?? Guid.NewGuid().ToString("N");
        var pending = new Pending(id, command, acceptedAt,
            Math.Clamp(timeoutMs ?? _options.TimeoutMs, 100, 300_000), deadlineToken);

        if (Volatile.Read(ref _accepting) == 0 || !TryReserve())
            return await RejectAdmissionAsync(pending, Volatile.Read(ref _accepting) == 0
                ? "Command service is stopping." : "Command queue is full.");

        if (!LaneFor(_engine.WritePartitionKey(command.TagId)).Queue.Writer.TryWrite(pending))
        {
            Interlocked.Decrement(ref _pendingCount);
            return await RejectAdmissionAsync(pending, "Command service is stopping.");
        }
        try
        {
            return await pending.Completion.Task.WaitAsync(
                TimeSpan.FromMilliseconds(pending.TimeoutMs) + TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            try { pending.ServerDeadline.Cancel(); }
            catch (ObjectDisposedException) { return await pending.Completion.Task.ConfigureAwait(false); }
            var fallback = Complete(pending, ControlOutcome.Unknown,
                "Command completion watchdog expired; execution is cancelled and the device outcome cannot be proven.")
                with { AuditPersisted = false, AuditError = "Audit completion is pending." };
            pending.Completion.TrySetResult(fallback);
            _health.Failed("control-commands", new TimeoutException(fallback.Reason));
            return await pending.Completion.Task.ConfigureAwait(false);
        }
    }

    private bool TryReserve()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _pendingCount);
            if (observed >= _capacity) return false;
            if (Interlocked.CompareExchange(ref _pendingCount, observed + 1, observed) == observed)
                return true;
        }
    }

    /// <summary>
    /// Nothing to drain here — every lane runs its own task — so this is the component's heartbeat.
    /// It has to exist independently of the lanes: a gateway that has never been sent a command has
    /// no lanes at all, and a health registry that hears nothing from a component eventually reports
    /// it stale. "No commands yet" is not the same fact as "the command service is dead".
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health.Running("control-commands");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                _health.Heartbeat("control-commands");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        foreach (var lane in _lanes.Values)
            lane.Queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        // Each lane's finally block completes whatever it had admitted, so the caller waiting on a
        // command gets an answer rather than a hung request. Awaiting them is what makes that
        // ordering hold: without it the process can exit while a lane is still draining.
        _lifetime.Cancel();
        foreach (var lane in _lanes.Values)
        {
            try { await lane.Pump.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
    }

    public override void Dispose()
    {
        _lifetime.Dispose();
        base.Dispose();
    }

    private async Task WorkerAsync(string key, Channel<Pending> lane, CancellationToken stoppingToken)
    {
        try
        {
            while (await WaitForCommandAsync(lane.Reader, stoppingToken).ConfigureAwait(false))
            {
                while (lane.Reader.TryRead(out var pending))
                {
                    Interlocked.Decrement(ref _pendingCount);
                    await ExecuteOneAsync(pending, stoppingToken).ConfigureAwait(false);
                    _health.Heartbeat("control-commands");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // One lane failing is not grounds for refusing commands to every other device, but it is
            // grounds for saying so: this lane's device can no longer be commanded until a restart.
            _health.Failed("control-commands", ex);
            _logger.LogError(ex, "Command lane for {Device} stopped unexpectedly", key);
            lane.Writer.TryComplete(ex);
        }
        finally
        {
            while (lane.Reader.TryRead(out var pending))
            {
                Interlocked.Decrement(ref _pendingCount);
                var result = Complete(pending, ControlOutcome.Unknown,
                    "Gateway stopped before the admitted command could be attempted.");
                result = await TryAuditAsync(pending, result, CancellationToken.None).ConfigureAwait(false);
                pending.Completion.TrySetResult(result);
                pending.ServerDeadline.Dispose();
            }
        }
    }

    private async Task<bool> WaitForCommandAsync(ChannelReader<Pending> reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(5));
            try { return await reader.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { _health.Heartbeat("control-commands"); }
        }
        return false;
    }

    private async Task ExecuteOneAsync(Pending pending, CancellationToken stoppingToken)
    {
        var remaining = TimeSpan.FromMilliseconds(pending.TimeoutMs) - (DateTimeOffset.UtcNow - pending.AcceptedAt);
        if (remaining <= TimeSpan.Zero || pending.DeadlineToken.IsCancellationRequested)
        {
            var expired = Complete(pending, ControlOutcome.Rejected,
                "Command expired in the queue before any device write was attempted.");
            expired = await TryAuditAsync(pending, expired, stoppingToken).ConfigureAwait(false);
            pending.Completion.TrySetResult(expired);
            pending.ServerDeadline.Dispose();
            return;
        }

        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, pending.DeadlineToken, pending.ServerDeadline.Token);
        operationCts.CancelAfter(remaining);
        var operation = _engine.WriteTagAsync(pending.Command.TagId, pending.Command.Value, operationCts.Token);
        ControlResult result;
        try
        {
            await operation.WaitAsync(operationCts.Token).ConfigureAwait(false);
            result = Complete(pending, ControlOutcome.Successful, null);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            result = Complete(pending, ControlOutcome.Unknown,
                "Command deadline expired; the device outcome cannot be proven.");
            _ = operation.ContinueWith(t =>
                _logger.Log(t.IsFaulted ? LogLevel.Warning : LogLevel.Information, t.Exception,
                    "Late command completion {CommandId}: {Status}", pending.Id, t.Status),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            result = Complete(pending, ControlOutcome.Unknown,
                "Gateway stopped while the command was in progress; the device outcome cannot be proven.");
        }
        catch (UnauthorizedAccessException ex) { result = Complete(pending, ControlOutcome.Rejected, ex.Message); }
        catch (KeyNotFoundException ex) { result = Complete(pending, ControlOutcome.Rejected, ex.Message); }
        catch (ArgumentException ex) { result = Complete(pending, ControlOutcome.Rejected, ex.Message); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Command {CommandId} failed for {TagId}", pending.Id, pending.Command.TagId);
            result = Complete(pending, ControlOutcome.Failed, ex.Message);
        }

        result = await TryAuditAsync(pending, result, stoppingToken).ConfigureAwait(false);
        pending.Completion.TrySetResult(result);
        pending.ServerDeadline.Dispose();
    }

    private async Task<ControlResult> RejectAdmissionAsync(Pending pending, string reason)
    {
        var rejected = Complete(pending, ControlOutcome.Rejected, reason);
        try { return await TryAuditAsync(pending, rejected, CancellationToken.None).ConfigureAwait(false); }
        finally { pending.ServerDeadline.Dispose(); }
    }

    private async Task<ControlResult> TryAuditAsync(Pending pending, ControlResult result, CancellationToken ct)
    {
        try
        {
            await AuditAsync(pending, result, ct).ConfigureAwait(false);
            _health.Heartbeat("audit-trail");
            return result;
        }
        catch (Exception ex)
        {
            _health.Failed("audit-trail", ex);
            _logger.LogError(ex, "Audit persistence failed for command {CommandId}", pending.Id);
            return result with { AuditPersisted = false, AuditError = ex.Message };
        }
    }

    private static ControlResult Complete(Pending p, ControlOutcome outcome, string? reason) =>
        new(p.Id, outcome, reason, p.AcceptedAt, DateTimeOffset.UtcNow);

    private Task AuditAsync(Pending pending, ControlResult result, CancellationToken ct) =>
        _audit.WriteAsync(new GatewayAuditEvent(result.CompletedAt, "tag.write", pending.Command.Actor,
            pending.Command.Source, pending.Command.RemoteAddress, pending.Command.TagId,
            result.Outcome.ToString(), result.CommandId, pending.Command.Value, result.Reason,
            (long)(result.CompletedAt - result.AcceptedAt).TotalMilliseconds), ct);

    private sealed record Pending(string Id, ControlCommand Command, DateTimeOffset AcceptedAt, int TimeoutMs,
        CancellationToken DeadlineToken)
    {
        public CancellationTokenSource ServerDeadline { get; } = new();
        public TaskCompletionSource<ControlResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
