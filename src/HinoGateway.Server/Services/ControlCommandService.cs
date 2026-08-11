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
    public int WorkerCount { get; set; } = 8;
    public int TimeoutMs { get; set; } = 10_000;
}

/// <summary>
/// Globally bounded command admission with a single-reader partition per device. This preserves
/// FIFO on a physical PLC connection while allowing independent devices to make progress in
/// parallel. Once admitted, every command is completed explicitly, including during shutdown.
/// </summary>
public sealed class ControlCommandService : BackgroundService
{
    private readonly Channel<Pending>[] _partitions;
    private readonly DeviceEngine _engine;
    private readonly AuditTrail _audit;
    private readonly ComponentHealthRegistry _health;
    private readonly ILogger<ControlCommandService> _logger;
    private readonly ControlCommandOptions _options;
    private readonly int _capacity;
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
        _partitions = Enumerable.Range(0, Math.Clamp(options.WorkerCount, 1, 64))
            .Select(_ => Channel.CreateUnbounded<Pending>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            }))
            .ToArray();
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

        var key = _engine.WritePartitionKey(command.TagId);
        var partition = (int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_partitions.Length);
        if (!_partitions[partition].Writer.TryWrite(pending))
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

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health.Running("control-commands");
        return Task.WhenAll(_partitions.Select((partition, index) => WorkerAsync(index, partition, stoppingToken)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        foreach (var partition in _partitions)
            partition.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private async Task WorkerAsync(int index, Channel<Pending> partition, CancellationToken stoppingToken)
    {
        try
        {
            while (await WaitForCommandAsync(partition.Reader, stoppingToken).ConfigureAwait(false))
            {
                while (partition.Reader.TryRead(out var pending))
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
            Interlocked.Exchange(ref _accepting, 0);
            foreach (var channel in _partitions) channel.Writer.TryComplete(ex);
            _health.Failed("control-commands", ex);
            _logger.LogError(ex, "Command partition {Partition} stopped unexpectedly", index);
        }
        finally
        {
            while (partition.Reader.TryRead(out var pending))
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
