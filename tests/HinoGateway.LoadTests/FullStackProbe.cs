using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HinoGateway.Core;
using HinoGateway.Engine;
using HinoGateway.Northbound.Iec104;
using HinoGateway.Northbound.Mqtt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HinoGateway.LoadTests;

public sealed record FullStackStats(
    long SignalRBatches,
    long SignalRSamples,
    long HttpStatsCalls,
    long HttpStatsFailures,
    long AlarmEvents,
    long AlarmDropped,
    long MqttPendingBatches,
    long MqttInputDrops,
    long MqttSpoolDrops,
    long IecReceivedAsdus,
    long IecSpontaneousSent,
    long IecDroppedUpdates,
    long IecCapacityDrops);

/// <summary>
/// Exercises the northbound paths missing from the field-protocol load: alarm evaluation, the
/// production telemetry delta/coalescing logic over a real Kestrel/SignalR WebSocket, REST stats,
/// an offline MQTT durable spool, and IEC-104 spontaneous ASDUs over a real TCP connection. The
/// Modbus slave farm remains a separate process, so gateway and simulator resource figures cannot
/// hide each other.
/// </summary>
internal sealed class FullStackProbe : IAsyncDisposable
{
    private readonly string _tempDirectory;
    private readonly CancellationTokenSource _cts = new();
    private readonly AlarmEngine _alarms;
    private readonly MqttPublisher _mqtt;
    private readonly Iec104Server _iec;
    private readonly TcpClient _iecClient;
    private readonly ClientWebSocket _signalRClient;
    private readonly WebApplication _web;
    private readonly HttpClient _http;
    private readonly TagSubscription _telemetrySubscription;
    private readonly TagStore _tags;
    private readonly IReadOnlyList<string> _churnTagIds;
    private Task _telemetryLoop = Task.CompletedTask;
    private Task _signalRLoop = Task.CompletedTask;
    private Task _httpLoop = Task.CompletedTask;
    private Task _alarmLoop = Task.CompletedTask;
    private Task _iecLoop = Task.CompletedTask;
    private Task _churnLoop = Task.CompletedTask;
    private long _signalRBatches;
    private long _signalRSamples;
    private long _httpCalls;
    private long _httpFailures;
    private long _alarmEvents;
    private long _iecReceived;

    private FullStackProbe(string tempDirectory, AlarmEngine alarms, MqttPublisher mqtt,
        Iec104Server iec, TcpClient iecClient, WebApplication web, HttpClient http,
        ClientWebSocket signalRClient, TagSubscription telemetrySubscription, TagStore tags,
        IReadOnlyList<string> churnTagIds)
    {
        _tempDirectory = tempDirectory;
        _alarms = alarms;
        _mqtt = mqtt;
        _iec = iec;
        _iecClient = iecClient;
        _web = web;
        _http = http;
        _signalRClient = signalRClient;
        _telemetrySubscription = telemetrySubscription;
        _tags = tags;
        _churnTagIds = churnTagIds;
    }

    public static async Task<FullStackProbe> StartAsync(DeviceEngine engine, TagStore tags,
        IReadOnlyList<DeviceConfig> configs, ILoggerFactory loggerFactory, CancellationToken ct = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"hino-load-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        var alarms = new AlarmEngine(loggerFactory.CreateLogger<AlarmEngine>());
        alarms.Reload(configs.Take(1000).Select((device, index) => new AlarmDefinition
        {
            Id = $"load-alarm-{index}",
            Name = $"Load alarm {index}",
            TagId = device.Tags[0].Id,
            Condition = AlarmCondition.Hi,
            Limit = -1,
            RequiresAck = false,
        }));
        tags.AddObserver(alarms.OnReads);

        var mqtt = new MqttPublisher(new MqttPublisherOptions
        {
            Enabled = true,
            Broker = "127.0.0.1",
            Port = FreePort(), // No broker: exercise bounded memory and the durable spool.
            ClientId = $"hino-load-{Environment.ProcessId}",
            PublishTopic = "load/telemetry",
            BatchIntervalMs = 5000,
            QueueCapacity = 8192,
            MaxSpoolBytes = 256L * 1024 * 1024,
            ReconnectMinMs = 100,
            ReconnectMaxMs = 1000,
        }, tags, Path.Combine(temp, "mqtt-spool"), loggerFactory.CreateLogger<MqttPublisher>());
        mqtt.Start();

        var iecOptions = new Iec104ServerOptions
        {
            Enabled = true,
            BindAddress = "127.0.0.1",
            Port = FreePort(),
            CommonAddress = 1,
            AllowCommands = false,
            MaxSpontaneousPerSecond = 500,
            OutboundQueueCapacity = 4096,
            // Exercise a broad mapping so ASDU packing, the point limiter and the lib60870 queue are
            // all under load instead of validating the link with only a handful of points.
            Points = configs.SelectMany(c => c.Tags).Take(2000).Select((tag, index) =>
                new Iec104PointDto
                {
                    TagId = tag.Id,
                    TagPath = tag.Id,
                    Ioa = index + 1,
                    Type = Iec104PointType.ShortFloat,
                    WithTimestamp = false,
                }).ToList(),
        };
        var iec = new Iec104Server(iecOptions, tags,
            (_, _, _) => Task.FromException(new NotSupportedException("load probe is read-only")),
            loggerFactory.CreateLogger<Iec104Server>());
        iec.Start();

        var iecClient = new TcpClient { NoDelay = true };
        await iecClient.ConnectAsync(IPAddress.Loopback, iecOptions.Port, ct);
        var iecStream = iecClient.GetStream();
        await iecStream.WriteAsync(new byte[] { 0x68, 0x04, 0x07, 0x00, 0x00, 0x00 }, ct); // STARTDT act
        var startConfirmation = await ReadIecFrameAsync(iecStream, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        if (startConfirmation.Length < 4 || startConfirmation[0] != 0x0b)
            throw new InvalidOperationException("IEC-104 load client did not receive STARTDT confirmation");

        var webPort = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{webPort}");
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSignalR();
        var web = builder.Build();
        web.MapHub<LoadTelemetryHub>("/telemetry");
        web.MapGet("/stats", () => new
        {
            Devices = engine.Health.Count,
            Tags = tags.QualityCounts(),
            Alarms = alarms.Summary(),
        });
        await web.StartAsync(ct);

        var http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{webPort}/"),
            Timeout = TimeSpan.FromSeconds(5),
        };
        var signalRClient = await ConnectSignalRAsync(http, webPort, ct);
        var subscription = tags.Subscribe("load-signalr", Math.Max(1024, configs.Count * 8), dropOldest: true);
        var churnTagIds = configs.SelectMany(c => c.Tags).Take(2000).Select(t => t.Id).ToList();
        var probe = new FullStackProbe(temp, alarms, mqtt, iec, iecClient, web, http,
            signalRClient, subscription, tags, churnTagIds);
        probe.StartLoops(web.Services.GetRequiredService<IHubContext<LoadTelemetryHub>>());
        return probe;
    }

    private void StartLoops(IHubContext<LoadTelemetryHub> hub)
    {
        var ct = _cts.Token;
        _telemetryLoop = Task.Run(() => TelemetryLoopAsync(hub, ct));
        _signalRLoop = Task.Run(() => SignalRLoopAsync(ct));
        _httpLoop = Task.Run(() => HttpLoopAsync(ct));
        _alarmLoop = Task.Run(() => AlarmLoopAsync(ct));
        _iecLoop = Task.Run(() => IecLoopAsync(ct));
        _churnLoop = Task.Run(() => ChurnLoopAsync(ct));
    }

    private async Task TelemetryLoopAsync(IHubContext<LoadTelemetryHub> hub, CancellationToken ct)
    {
        var deltas = new TelemetryDeltaBuffer();
        try
        {
            while (await _telemetrySubscription.Reader.WaitToReadAsync(ct))
            {
                await Task.Delay(100, ct);
                while (_telemetrySubscription.Reader.TryRead(out var batch))
                    deltas.Add(batch);
                var samples = deltas.Drain();
                if (samples.Count == 0)
                    continue;
                await hub.Clients.All.SendAsync("TagsUpdated", samples, ct);
                deltas.MarkSent(samples);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task SignalRLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_signalRClient.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await ReceiveWebSocketMessageAsync(_signalRClient, ct);
                foreach (var payload in message.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var json = JsonDocument.Parse(payload);
                    var root = json.RootElement;
                    if (!root.TryGetProperty("type", out var type) || type.GetInt32() != 1
                        || !root.TryGetProperty("target", out var target)
                        || target.GetString() != "TagsUpdated"
                        || !root.TryGetProperty("arguments", out var arguments)
                        || arguments.GetArrayLength() == 0
                        || arguments[0].ValueKind != JsonValueKind.Array)
                        continue;
                    Interlocked.Increment(ref _signalRBatches);
                    Interlocked.Add(ref _signalRSamples, arguments[0].GetArrayLength());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (WebSocketException) when (ct.IsCancellationRequested) { }
    }

    private async Task HttpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync("stats", ct);
                response.EnsureSuccessStatusCode();
                _ = await response.Content.ReadAsByteArrayAsync(ct);
                Interlocked.Increment(ref _httpCalls);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { Interlocked.Increment(ref _httpFailures); }
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task AlarmLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _alarms.Events.ReadAllAsync(ct))
                Interlocked.Increment(ref _alarmEvents);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task IecLoopAsync(CancellationToken ct)
    {
        var stream = _iecClient.GetStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadIecFrameAsync(stream, ct);
                if (frame.Length < 4 || (frame[0] & 1) != 0)
                    continue;

                Interlocked.Add(ref _iecReceived, frame.Length > 5 ? frame[5] & 0x7f : 1);
                // The first control word is the server's send sequence. The second is its ACK of
                // client I-frames (always zero here because this probe sends no I-frames).
                var nextReceiveSequence = ((frame[0] | frame[1] << 8) >> 1) + 1;
                var encoded = nextReceiveSequence << 1;
                await stream.WriteAsync(new byte[]
                {
                    0x68, 0x04, 0x01, 0x00, (byte)(encoded & 0xff), (byte)(encoded >> 8),
                }, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (IOException) when (ct.IsCancellationRequested) { }
        catch (SocketException) when (ct.IsCancellationRequested) { }
    }

    private async Task ChurnLoopAsync(CancellationToken ct)
    {
        var sequence = 0u;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(ct))
            {
                sequence++;
                var now = DateTimeOffset.UtcNow;
                var reads = _churnTagIds.Select((tagId, index) =>
                    new TagRead(tagId, TagValue.Good(sequence * 100_000u + (uint)index, now))).ToList();
                _tags.Apply(reads);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public FullStackStats Snapshot()
    {
        var mqtt = _mqtt.Status;
        var iec = _iec.Status;
        return new FullStackStats(
            Interlocked.Read(ref _signalRBatches), Interlocked.Read(ref _signalRSamples),
            Interlocked.Read(ref _httpCalls), Interlocked.Read(ref _httpFailures),
            Interlocked.Read(ref _alarmEvents), _alarms.DroppedEvents,
            mqtt.PendingBatches, mqtt.DroppedInputBatches, mqtt.DroppedSpoolBatches,
            Interlocked.Read(ref _iecReceived), iec.SpontaneousSent, iec.DroppedUpdates, iec.CapacityDrops);
    }

    private static async Task<ClientWebSocket> ConnectSignalRAsync(HttpClient http, int port,
        CancellationToken ct)
    {
        using var response = await http.PostAsync("telemetry/negotiate?negotiateVersion=1", null, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var token = json.RootElement.GetProperty("connectionToken").GetString()
                    ?? throw new InvalidOperationException("SignalR negotiate response has no connection token");

        var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/telemetry?id={Uri.EscapeDataString(token)}"), ct);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"),
            WebSocketMessageType.Text, true, ct);
        var handshake = await ReceiveWebSocketMessageAsync(socket, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        if (!handshake.Contains("{}\u001e", StringComparison.Ordinal))
            throw new InvalidOperationException($"SignalR handshake failed: {handshake}");
        return socket;
    }

    private static async Task<string> ReceiveWebSocketMessageAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("SignalR WebSocket closed");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    private static async Task<byte[]> ReadIecFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[2];
        await stream.ReadExactlyAsync(header, ct);
        if (header[0] != 0x68 || header[1] < 4)
            throw new InvalidDataException("Invalid IEC-104 APDU header");
        var frame = new byte[header[1]];
        await stream.ReadExactlyAsync(frame, ct);
        return frame;
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _telemetrySubscription.Dispose();
        _signalRClient.Abort();
        _iecClient.Close();
        try
        {
            await Task.WhenAll(_telemetryLoop, _signalRLoop, _httpLoop, _alarmLoop, _iecLoop, _churnLoop)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { /* best-effort shutdown of load-only consumers */ }

        _signalRClient.Dispose();
        _http.Dispose();
        using (var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            try { await _web.StopAsync(stopCts.Token); } catch { }
        }
        await _web.DisposeAsync();
        await _iec.DisposeAsync();
        await _mqtt.DisposeAsync();
        _cts.Dispose();

        var full = Path.GetFullPath(_tempDirectory);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("hino-load-", StringComparison.Ordinal))
        {
            try { Directory.Delete(full, recursive: true); } catch { }
        }
    }
}

public sealed class LoadTelemetryHub : Hub;
