using System.Collections.Concurrent;
using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Engine;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;

namespace HinoGateway.Northbound.Mqtt;

public delegate void MqttLoopHealth(string loop, Exception? failure);

public sealed class MqttPublisher : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly MqttPublisherOptions _options;
    private readonly IMqttDiskSpool _spool;
    private readonly ILogger<MqttPublisher> _logger;
    private readonly ConcurrentDictionary<string, TagRead> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _spoolReady = new(0, 1);
    private readonly IMqttClient _client;
    private readonly MqttLoopHealth? _loopHealth;
    private CancellationTokenSource? _cts;
    private TagSubscription? _subscription;
    private Task? _collector;
    private Task? _flusher;
    private Task? _sender;
    private long _publishedBatches, _publishedSamples;
    private long _loopRestarts;
    private DateTimeOffset? _lastConnectedAt, _lastPublishedAt;
    private string? _lastPublishError;
    private string? _lastSpoolError;
    private int _disposed;

    public bool ShutdownClean { get; private set; } = true;

    public MqttPublisher(MqttPublisherOptions options, TagStore tags, string spoolDirectory,
        ILogger<MqttPublisher> logger, MqttLoopHealth? loopHealth = null)
        : this(options, tags, new MqttDiskSpool(spoolDirectory, options.MaxSpoolBytes), logger, loopHealth) { }

    internal MqttPublisher(MqttPublisherOptions options, TagStore tags, IMqttDiskSpool spool,
        ILogger<MqttPublisher> logger, MqttLoopHealth? loopHealth = null)
    {
        _options = options;
        _logger = logger;
        _spool = spool;
        _client = new MqttClientFactory().CreateMqttClient();
        _loopHealth = loopHealth;
        Tags = tags;
    }

    private TagStore Tags { get; }

    public MqttPublisherStatus Status
    {
        get
        {
            var (count, bytes) = _spool.Stats();
            var loopsAlive = _cts is not null
                && new[] { _collector, _flusher, _sender }.All(t => t is { IsCompleted: false });
            return new MqttPublisherStatus(_options.Enabled, loopsAlive, _client.IsConnected,
                $"{_options.Broker}:{_options.Port}", _options.PublishTopic,
                Interlocked.Read(ref _publishedBatches), Interlocked.Read(ref _publishedSamples),
                count, bytes, _subscription?.Dropped ?? 0, _spool.Dropped,
                _lastConnectedAt, _lastPublishedAt, _lastSpoolError ?? _lastPublishError)
            { LoopRestarts = Interlocked.Read(ref _loopRestarts) };
        }
    }

    public void Start()
    {
        if (_cts is not null || !_options.Enabled) return;
        ShutdownClean = true;
        _cts = new CancellationTokenSource();
        _subscription = Tags.Subscribe("mqtt", Math.Clamp(_options.QueueCapacity, 64, 100_000), dropOldest: false);
        _collector = Task.Run(() => SuperviseAsync("collector", CollectAsync, _cts.Token));
        _flusher = Task.Run(() => SuperviseAsync("spool flusher", FlushAsync, _cts.Token));
        _sender = Task.Run(() => SuperviseAsync("sender", SendAsync, _cts.Token));
    }

    private async Task SuperviseAsync(string name, Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Pulse(name);
                await loop(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                throw new InvalidOperationException($"MQTT {name} returned unexpectedly.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _loopRestarts);
                _lastPublishError = $"MQTT {name} restarted: {ex.Message}";
                Pulse(name, ex);
                _logger.LogError(ex, "MQTT {Loop} stopped; restarting", name);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        try
        {
            var reader = _subscription!.Reader;
            while (await WaitForUpdatesAsync(reader, "collector", ct))
            {
                while (reader.TryRead(out var batch))
                    foreach (var read in batch) _pending[read.TagId] = read;
                Pulse("collector");
            }
            if (!ct.IsCancellationRequested)
            {
                var closed = new InvalidOperationException("MQTT tag subscription closed unexpectedly.");
                _lastPublishError = closed.Message;
                Pulse("collector", closed);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _lastPublishError = $"MQTT collector stopped: {ex.Message}";
            _logger.LogError(ex, "MQTT collector stopped unexpectedly");
            throw;
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        try
        {
            var interval = TimeSpan.FromMilliseconds(Math.Clamp(_options.BatchIntervalMs, 50, 60_000));
            while (!ct.IsCancellationRequested)
            {
                await DelayWithHeartbeatAsync(interval, "spool flusher", ct);
                try { await FlushPendingAsync(ct); _lastSpoolError = null; Pulse("spool"); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _lastSpoolError = $"MQTT spool write failed: {ex.Message}";
                    Pulse("spool", ex);
                    _logger.LogError(ex, "MQTT spool write failed; pending samples retained for retry");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task FlushPendingAsync(CancellationToken ct)
    {
        if (_pending.IsEmpty) return;
        var accepted = _pending.ToArray();
        var samples = accepted.Select(pair =>
        {
            var read = pair.Value;
            return new MqttTelemetrySample(read.TagId, read.Value.Value, read.Value.Quality.ToString(),
                read.Value.SourceTimestamp, read.Value.LastChanged, read.Value.StatusText);
        }).ToList();
        if (samples.Count == 0) return;

        var payload = JsonSerializer.SerializeToUtf8Bytes(new MqttTelemetryBatch(
            Guid.NewGuid().ToString("N"), Environment.MachineName, DateTimeOffset.UtcNow, samples), Json);
        await _spool.EnqueueAsync(payload, ct);

        var pendingPairs = (ICollection<KeyValuePair<string, TagRead>>)_pending;
        foreach (var pair in accepted) pendingPairs.Remove(pair);
        if (_spoolReady.CurrentCount == 0) _spoolReady.Release();
    }

    private async Task SendAsync(CancellationToken ct)
    {
        var backoff = Math.Clamp(_options.ReconnectMinMs, 100, 60_000);
        var backoffMax = Math.Clamp(_options.ReconnectMaxMs, backoff, 300_000);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    var connect = await _client.ConnectAsync(BuildClientOptions(), ct);
                    EnsureConnectSucceeded(connect.ResultCode, connect.ReasonString);
                    _lastConnectedAt = DateTimeOffset.UtcNow;
                }
                var item = await _spool.PeekAsync(ct);
                if (item is null)
                {
                    await _spoolReady.WaitAsync(TimeSpan.FromSeconds(1), ct);
                    Pulse("sender");
                    continue;
                }

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_options.PublishTopic)
                    .WithPayload(item.Payload)
                    .WithQualityOfServiceLevel((MqttQualityOfServiceLevel)Math.Clamp(_options.QoS, 0, 2))
                    .WithRetainFlag(_options.Retain)
                    .WithContentType("application/json")
                    .Build();
                var publish = await _client.PublishAsync(message, ct);
                EnsurePublishSucceeded(publish);
                await _spool.CompleteAsync(item, ct);
                // A TCP/MQTT handshake is not proof that the broker accepts this publisher. Preserve
                // exponential backoff until one message has been acknowledged and durably removed
                // from the spool; otherwise an ACL/authentication failure reconnects every second.
                backoff = Math.Clamp(_options.ReconnectMinMs, 100, 60_000);
                _lastPublishError = null;
                Interlocked.Increment(ref _publishedBatches);
                try
                {
                    using var document = JsonDocument.Parse(item.Payload);
                    if (document.RootElement.TryGetProperty("samples", out var samples)
                        && samples.ValueKind == JsonValueKind.Array)
                        Interlocked.Add(ref _publishedSamples, samples.GetArrayLength());
                }
                catch (JsonException ex) { _logger.LogWarning(ex, "Published MQTT batch could not be parsed for sample statistics"); }
                _lastPublishedAt = DateTimeOffset.UtcNow;
                Pulse("sender");
                Pulse("broker");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _lastPublishError = ex.Message;
                Pulse("broker", ex);
                _logger.LogWarning(ex, "MQTT publish failed; retrying in {Backoff} ms", backoff);
                try { if (_client.IsConnected) await _client.DisconnectAsync(); } catch { }
                Pulse("sender");
                await DelayWithHeartbeatAsync(TimeSpan.FromMilliseconds(backoff), "sender", ct);
                backoff = Math.Min(backoff * 2, backoffMax);
            }
        }
    }

    private async Task<bool> WaitForUpdatesAsync<T>(System.Threading.Channels.ChannelReader<T> reader,
        string loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
            heartbeat.CancelAfter(TimeSpan.FromSeconds(5));
            try { return await reader.WaitToReadAsync(heartbeat.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Pulse(loop); }
        }
        return false;
    }

    private async Task DelayWithHeartbeatAsync(TimeSpan delay, string loop, CancellationToken ct)
    {
        var remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            var slice = remaining > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : remaining;
            await Task.Delay(slice, ct);
            remaining -= slice;
            Pulse(loop);
        }
    }

    private void Pulse(string loop, Exception? failure = null)
    {
        try { _loopHealth?.Invoke(loop, failure); }
        catch (Exception ex) { _logger.LogDebug(ex, "MQTT loop-health observer failed"); }
    }

    internal static void EnsureConnectSucceeded(MqttClientConnectResultCode resultCode, string? reasonString)
    {
        if (resultCode == MqttClientConnectResultCode.Success)
            return;
        throw new InvalidOperationException(
            $"MQTT broker rejected CONNECT: {resultCode}" +
            (string.IsNullOrWhiteSpace(reasonString) ? "." : $" — {reasonString}"));
    }

    internal static void EnsurePublishSucceeded(MqttClientPublishResult result)
    {
        if (result.IsSuccess)
            return;
        throw new InvalidOperationException(
            $"MQTT broker rejected PUBLISH: {result.ReasonCode}" +
            (string.IsNullOrWhiteSpace(result.ReasonString) ? "." : $" — {result.ReasonString}"));
    }

    private MqttClientOptions BuildClientOptions()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(string.IsNullOrWhiteSpace(_options.ClientId) ? $"hino-{Environment.MachineName}" : _options.ClientId)
            .WithTcpServer(_options.Broker, _options.Port)
            .WithCleanStart(false)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrEmpty(_options.Username)) builder.WithCredentials(_options.Username, _options.Password);
        if (_options.UseTls) builder.WithTlsOptions(o => o.UseTls());
        return builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var cts = _cts;
        if (cts is null)
        {
            _client.Dispose();
            return;
        }
        cts.Cancel();
        _subscription?.Dispose();
        try
        {
            var loops = Task.WhenAll(new[] { _collector, _flusher, _sender }.Where(t => t is not null).Cast<Task>());
            try { await loops.WaitAsync(ShutdownTimeout); }
            catch (OperationCanceledException) { }
            catch (TimeoutException)
            {
                ShutdownClean = false;
                _lastPublishError = $"MQTT background loops exceeded shutdown timeout {ShutdownTimeout}.";
            }
            catch (Exception ex) { _lastPublishError = $"MQTT background loop failed while stopping: {ex.Message}"; }

            using var finalTimeout = new CancellationTokenSource(ShutdownTimeout);
            try { await FlushPendingAsync(finalTimeout.Token); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogWarning(ex, "Could not flush final MQTT batch"); }
            try
            {
                if (_client.IsConnected) await _client.DisconnectAsync().WaitAsync(ShutdownTimeout);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "MQTT disconnect did not complete cleanly"); }
        }
        finally
        {
            _client.Dispose();
            cts.Dispose();
            _cts = null;
        }
    }
}

public sealed record MqttTelemetryBatch(string BatchId, string Gateway, DateTimeOffset Timestamp,
    IReadOnlyList<MqttTelemetrySample> Samples);
public sealed record MqttTelemetrySample(string Tag, object? Value, string Quality,
    DateTimeOffset SourceTimestamp, DateTimeOffset LastChanged, string? Status);
