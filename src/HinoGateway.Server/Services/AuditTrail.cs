using System.Text.Json;
using System.Threading.Channels;

namespace HinoGateway.Server.Services;

public sealed record GatewayAuditEvent(
    DateTimeOffset Timestamp,
    string Action,
    string Actor,
    string Source,
    string? RemoteAddress,
    string Target,
    string Outcome,
    string? CommandId = null,
    object? Value = null,
    string? Reason = null,
    long? ElapsedMs = null);

public sealed class AuditTrailOptions
{
    public int QueueCapacity { get; set; } = 4096;
    public int BatchSize { get; set; } = 100;
    public int BatchWindowMs { get; set; } = 20;
    public int RetentionDays { get; set; } = 90;
    public int MaxFiles { get; set; } = 180;
    public long MaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// Append-only JSONL audit writer. Concurrent requests are committed in short batches, but each
/// caller completes only after its batch has been flushed to durable storage.
/// </summary>
public sealed class AuditTrail : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly GatewayDataPaths _paths;
    private readonly ComponentHealthRegistry _health;
    private readonly ILogger<AuditTrail> _logger;
    private readonly AuditTrailOptions _options;
    private readonly Channel<Queued> _queue;
    private int _accepting = 1;
    private FileStream? _stream;
    private StreamWriter? _writer;
    private string? _openPath;

    public AuditTrail(GatewayDataPaths paths, IConfiguration configuration,
        ComponentHealthRegistry health, ILogger<AuditTrail> logger)
    {
        _paths = paths;
        _health = health;
        _logger = logger;
        _options = configuration.GetSection("Audit").Get<AuditTrailOptions>() ?? new();
        _queue = Channel.CreateBounded<Queued>(new BoundedChannelOptions(
            Math.Clamp(_options.QueueCapacity, 64, 100_000))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public async Task WriteAsync(GatewayAuditEvent entry, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _accepting) == 0)
            throw new InvalidOperationException("Audit trail is stopping.");
        var queued = new Queued(entry);
        try { await _queue.Writer.WriteAsync(queued, ct).ConfigureAwait(false); }
        catch (ChannelClosedException) { throw new InvalidOperationException("Audit trail is stopping."); }
        await queued.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _health.Running("audit-writer");
        Directory.CreateDirectory(_paths.Audit);
        EnforceRetention();
        var batch = new List<Queued>(Math.Clamp(_options.BatchSize, 1, 10_000));
        try
        {
            // StopAsync completes the writer, so this drains admitted entries without depending on
            // the host cancellation token.
            while (await WaitForAuditAsync())
            {
                batch.Clear();
                if (_queue.Reader.TryRead(out var first)) batch.Add(first);
                if (batch.Count == 0) continue;

                var window = TimeSpan.FromMilliseconds(Math.Clamp(_options.BatchWindowMs, 0, 1000));
                if (window > TimeSpan.Zero)
                    await Task.Delay(window, CancellationToken.None);
                while (batch.Count < Math.Clamp(_options.BatchSize, 1, 10_000)
                       && _queue.Reader.TryRead(out var next))
                    batch.Add(next);
                Commit(batch);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _accepting, 0);
            _queue.Writer.TryComplete(ex);
            _health.Failed("audit-writer", ex);
            _logger.LogCritical(ex, "Audit writer stopped unexpectedly");
            Fail(batch, ex);
            while (_queue.Reader.TryRead(out var queued)) queued.Completion.TrySetException(ex);
        }
        finally
        {
            DisposeOpenFile();
        }
    }

    private async Task<bool> WaitForAuditAsync()
    {
        while (true)
        {
            using var heartbeat = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { return await _queue.Reader.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) { _health.Heartbeat("audit-writer"); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _accepting, 0);
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken);
    }

    private void Commit(List<Queued> batch)
    {
        try
        {
            var path = Path.Combine(_paths.Audit, $"audit-{batch[0].Entry.Timestamp:yyyyMMdd}.jsonl");
            EnsureOpen(path);
            foreach (var item in batch)
                _writer!.WriteLine(JsonSerializer.Serialize(item.Entry, Json));
            _writer!.Flush();
            _stream!.Flush(flushToDisk: true);
            _health.Heartbeat("audit-writer");
            foreach (var item in batch) item.Completion.TrySetResult();
        }
        catch (Exception ex)
        {
            _health.Failed("audit-writer", ex);
            _logger.LogError(ex, "Could not persist {Count} audit event(s)", batch.Count);
            DisposeOpenFile();
            Fail(batch, ex);
        }
    }

    private void EnsureOpen(string path)
    {
        if (string.Equals(path, _openPath, StringComparison.OrdinalIgnoreCase)) return;
        DisposeOpenFile();
        Directory.CreateDirectory(_paths.Audit);
        if (!File.Exists(path))
        {
            var create = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read,
                BufferSize = 1, Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
                create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (new FileStream(path, create)) { }
        }
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            64 * 1024, FileOptions.WriteThrough);
        _writer = new StreamWriter(_stream, leaveOpen: true);
        _openPath = path;
        EnforceRetention();
    }

    private void EnforceRetention()
    {
        try
        {
            var files = new DirectoryInfo(_paths.Audit).EnumerateFiles("audit-*.jsonl")
                .OrderByDescending(x => x.Name, StringComparer.Ordinal).ToList();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(_options.RetentionDays, 1, 3650));
            long retainedBytes = 0;
            var retainedFiles = 0;
            foreach (var file in files)
            {
                var keep = file.FullName == _openPath
                           || file.LastWriteTimeUtc >= cutoff.UtcDateTime
                           && retainedFiles < Math.Clamp(_options.MaxFiles, 1, 10_000)
                           && retainedBytes + file.Length <= Math.Max(1_048_576, _options.MaxTotalBytes);
                if (keep) { retainedFiles++; retainedBytes += file.Length; }
                else file.Delete();
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not enforce audit retention policy"); }
    }

    private void DisposeOpenFile()
    {
        _writer?.Dispose();
        _stream?.Dispose();
        _writer = null;
        _stream = null;
        _openPath = null;
    }

    private static void Fail(IEnumerable<Queued> batch, Exception ex)
    {
        foreach (var item in batch) item.Completion.TrySetException(ex);
    }

    private sealed record Queued(GatewayAuditEvent Entry)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
