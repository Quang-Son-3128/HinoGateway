using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using HinoGateway.Core;
using HinoGateway.Engine;
using lib60870;
using lib60870.CS101;
using lib60870.CS104;
using Microsoft.Extensions.Logging;
using CS104Server = lib60870.CS104.Server;

namespace HinoGateway.Northbound.Iec104;

/// <summary>Writes a value to a tag on behalf of a command from the control centre.</summary>
public delegate Task Iec104TagWriter(string tagPath, object value, CancellationToken ct);
public delegate Task Iec104CommandWriter(string tagPath, object value, string? remoteAddress, CancellationToken ct);
public sealed record Iec104ControlAudit(DateTimeOffset Timestamp, string RemoteAddress, string Action,
    int Ioa, string? TagPath, object? Value, string Outcome, string? Reason);
public delegate Task Iec104AuditWriter(Iec104ControlAudit entry);
public delegate void Iec104LoopHealth(string loop, Exception? failure);

/// <summary>
/// Which commands must not overtake each other: two tag paths that map to the same key are executed
/// one at a time, in admission order. The host answers with the device behind the tag, because the
/// thing that cannot be shared is one physical link — two points on the same PLC are one queue, and
/// two PLCs are two queues however many control centres the commands arrived from.
/// </summary>
public delegate string Iec104CommandPartitioner(string tagPath);

/// <summary>
/// The gateway as an IEC 60870-5-104 <b>controlled station</b>: it listens on 2404 and the utility's
/// control centre connects in, interrogates the station, receives spontaneous changes and issues
/// commands. This is the northbound direction — the opposite of a device driver.
///
/// Design notes worth keeping in mind when changing this:
/// <list type="bullet">
/// <item>It owns its own <see cref="TagSubscription"/>. It must never read the same queue as the UI,
/// or the two would split the stream between them.</item>
/// <item>The subscription keeps the <em>newest</em> batches. IEC-104 reports state, so if the link
/// falls behind, the current value is what the control centre needs — not a backlog of history.</item>
/// <item>Reporting is by exception with a per-point deadband, and every change of quality is reported
/// even when the number did not move.</item>
/// <item>Commands are answered before they are acted on (ACT_CON), then confirmed when the write has
/// actually happened (ACT_TERM) or refused negatively. A silent command is the one thing a dispatcher
/// must never see.</item>
/// </list>
/// </summary>
public sealed class Iec104Server : IAsyncDisposable
{
    private static readonly TimeSpan SelectWindow = TimeSpan.FromSeconds(30);

    private readonly Iec104ServerOptions _options;
    private readonly TagStore _tags;
    private readonly Iec104CommandWriter _writer;
    private readonly ILogger<Iec104Server> _logger;
    private readonly Iec104AuditWriter? _audit;
    private readonly Iec104LoopHealth? _loopHealth;
    private readonly Iec104CommandPartitioner _partition;

    private readonly Dictionary<int, Iec104PointDto> _byIoa;
    private readonly Dictionary<string, List<Iec104PointDto>> _byTagPath;
    private readonly ConcurrentDictionary<int, TagValue> _lastReported = new();
    private readonly ConcurrentDictionary<int, Selection> _selected = new();

    // Connections are tracked per source address, not per socket: lib60870.NET 2.3.0 raises only the
    // CLOSED connection event (never OPENED or ACTIVE — verified against the library), so the only
    // reliable signals available are the accept callback, which carries just the IP, and the close
    // event, which carries the endpoint. Counting per address is therefore what can honestly be
    // reported; "is the link working" is answered by the traffic timestamps instead.
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset FirstSeen)> _connections = new();

    private CS104Server? _server;
    private TagSubscription? _subscription;
    private CancellationTokenSource? _cts;
    private Task? _pump;
    private Task? _periodic;

    // One lane per physical destination, created on first use. See CommandLane.
    private ConcurrentDictionary<string, CommandLane>? _commandLanes;
    private int _pendingCommands;
    private int _stopping;

    private long _asdusSent, _asdusReceived, _commandsAccepted, _commandsRejected;
    private long _spontaneousSent, _interrogationsServed, _commandTimeouts;
    private long _coalescedUpdates, _capacityDrops, _loopRestarts;
    private int _pendingOutbound;
    private DateTimeOffset? _lastSentAt, _lastReceivedAt;
    private double? _lastClockOffsetSeconds;
    private string? _lastError;
    private string? _warning;

    public Iec104Server(Iec104ServerOptions options, TagStore tags, Iec104TagWriter writer,
        ILogger<Iec104Server> logger)
        : this(options, tags, (tag, value, _, ct) => writer(tag, value, ct), logger, null, null)
    {
    }

    public Iec104Server(Iec104ServerOptions options, TagStore tags, Iec104CommandWriter writer,
        ILogger<Iec104Server> logger, Iec104AuditWriter? audit = null, Iec104LoopHealth? loopHealth = null,
        Iec104CommandPartitioner? partitioner = null)
    {
        _options = options;
        _tags = tags;
        _writer = writer;
        _logger = logger;
        _audit = audit;
        _loopHealth = loopHealth;
        // Without a host to ask, every point is its own lane. That is still correct — order is only
        // ever promised between commands to the same point — it just parallelises more finely than
        // the physical link warrants.
        _partition = partitioner ?? (tagPath => tagPath);

        // Last mapping wins on a duplicated IOA; the configuration API rejects duplicates before we
        // ever get here, so this is only a safety net.
        _byIoa = options.Points.GroupBy(p => p.Ioa).ToDictionary(g => g.Key, g => g.Last());
        _byTagPath = options.Points
            .Where(p => !string.IsNullOrEmpty(p.TagPath))
            .GroupBy(p => p.TagPath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
    }

    public bool IsRunning => _server?.IsRunning() ?? false;

    public Iec104Status Status => new(
        Enabled: _options.Enabled,
        Running: IsRunning,
        Endpoint: $"{_options.BindAddress}:{_options.Port}",
        CommonAddress: _options.CommonAddress,
        MappedPoints: _byIoa.Count,
        OpenConnections: _server?.OpenConnections ?? 0,
        Connections: _connections
            .Select(kv => new Iec104ConnectionInfo(kv.Key, kv.Value.Count, kv.Value.FirstSeen))
            .Where(c => c.Connections > 0)
            .OrderBy(c => c.FirstSeenAt)
            .ToList(),
        AsdusSent: Interlocked.Read(ref _asdusSent),
        AsdusReceived: Interlocked.Read(ref _asdusReceived),
        LastSentAt: _lastSentAt,
        LastReceivedAt: _lastReceivedAt,
        CommandsAccepted: Interlocked.Read(ref _commandsAccepted),
        CommandsRejected: Interlocked.Read(ref _commandsRejected),
        SpontaneousSent: Interlocked.Read(ref _spontaneousSent),
        InterrogationsServed: Interlocked.Read(ref _interrogationsServed),
        DroppedUpdates: _subscription?.Dropped ?? 0,
        LastClockOffsetSeconds: _lastClockOffsetSeconds,
        LastError: _lastError,
        Warning: _warning)
    {
        PendingCommands = Volatile.Read(ref _pendingCommands),
        CommandCapacity = Math.Clamp(_options.CommandQueueCapacity, 1, 10_000),
        CommandTimeouts = Interlocked.Read(ref _commandTimeouts),
        PendingOutbound = Volatile.Read(ref _pendingOutbound),
        CoalescedUpdates = Interlocked.Read(ref _coalescedUpdates),
        CapacityDrops = Interlocked.Read(ref _capacityDrops),
        LoopRestarts = Interlocked.Read(ref _loopRestarts),
    };

    public void Start()
    {
        if (_server is not null)
            return;

        Interlocked.Exchange(ref _stopping, 0);
        Interlocked.Exchange(ref _pendingCommands, 0);
        _commandLanes = new ConcurrentDictionary<string, CommandLane>(StringComparer.Ordinal);

        var apci = new APCIParameters
        {
            K = _options.K,
            W = _options.W,
            T0 = _options.T0,
            T1 = _options.T1,
            T2 = _options.T2,
            T3 = _options.T3,
        };

        var server = new CS104Server(apci, new ApplicationLayerParameters())
        {
            // Redundant control-centre links are the norm; each connection gets its own queue so a
            // stalled centre cannot hold up the others.
            ServerMode = ServerMode.CONNECTION_IS_REDUNDANCY_GROUP,
            MaxOpenConnections = Math.Max(1, _options.MaxConnections),
            MaxQueueSize = Math.Clamp(_options.OutboundQueueCapacity, 64, 100_000),
            MaxHighPrioQueueSize = Math.Clamp(_options.CommandQueueCapacity * 2, 64, 10_000),
            // Make overload observable. REMOVE_OLDEST silently loses telemetry inside lib60870,
            // outside all gateway counters and before the control centre can acknowledge it.
            EnqueueMode = EnqueueMode.THROW_EXCEPTION,
        };
        server.SetLocalAddress(_options.BindAddress);
        server.SetLocalPort(_options.Port);

        server.SetConnectionRequestHandler(OnConnectionRequest, null);
        server.SetConnectionEventHandler(OnConnectionEvent, null);
        server.SetInterrogationHandler(OnInterrogation, null);
        server.SetCounterInterrogationHandler(OnCounterInterrogation, null);
        server.SetReadHandler(OnRead, null);
        server.SetClockSynchronizationHandler(OnClockSync, null);
        server.SetResetProcessHandler(OnResetProcess, null);
        server.SetDelayAcquisitionHandler(OnDelayAcquisition, null);
        server.SetASDUHandler(OnAsdu, null);
        // Leave none of these unset. lib60870.NET 2.3.0 handles a missing callback inconsistently:
        // with the interrogation/ASDU handlers absent it threw a NullReferenceException inside its own
        // receive loop and killed the process, while a missing reset-process handler merely answered
        // every C_RP_NA_1 negatively. Neither is acceptable towards a control centre.

        server.Start();
        _server = server;

        _cts = new CancellationTokenSource();
        // Keep the newest batches: state, not history, is what the control centre acts on.
        _subscription = _tags.Subscribe("iec104", capacity: Math.Clamp(_options.OutboundQueueCapacity, 64, 100_000), dropOldest: true);
        _pump = Task.Run(() => SuperviseAsync("reporting", PumpAsync, _cts.Token));
        if (_options.PeriodicIntervalSeconds > 0)
            _periodic = Task.Run(() => SuperviseAsync("periodic transmission", PeriodicAsync, _cts.Token));

        _logger.LogInformation(
            "IEC-104 controlled station listening on {Address}:{Port} (CA={Ca}, {Points} point(s))",
            _options.BindAddress, _options.Port, _options.CommonAddress, _byIoa.Count);

        _warning = DetectPortSharing();
        if (_warning is not null)
            _logger.LogWarning("IEC-104: {Warning}", _warning);
    }

    /// <summary>
    /// Is somebody else also serving this port?
    ///
    /// Binding a specific address (127.0.0.1) succeeds even when another program already listens on
    /// the wildcard (0.0.0.0) for the same port — and then which station a control centre reaches
    /// depends on the address it dialled and on who happens to be running. That produces a genuinely
    /// baffling symptom: the client connects happily while receiving nothing from this gateway.
    /// So it is detected and said out loud rather than left to be discovered in the field.
    /// </summary>
    private string? DetectPortSharing()
    {
        try
        {
            var others = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Where(endpoint => endpoint.Port == _options.Port
                                   && endpoint.Address.ToString() != _options.BindAddress)
                .Select(endpoint => endpoint.Address.ToString())
                .Distinct()
                .ToList();

            if (others.Count == 0)
                return null;

            return $"another program is also listening on port {_options.Port} " +
                   $"(at {string.Join(", ", others)}). A control centre may reach that station instead of " +
                   $"this one. Stop it, or give this gateway a port of its own.";
        }
        catch (Exception ex)
        {
            // Purely diagnostic — never let it affect the link.
            _logger.LogDebug(ex, "IEC-104: could not enumerate TCP listeners");
            return null;
        }
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);

        // Snapshot before cancelling: the dictionary is closed to new lanes by _stopping, and every
        // lane already created has to be completed and awaited or an admitted command would be left
        // without the negative termination its control centre is waiting for.
        var lanes = _commandLanes;
        var lanePumps = Array.Empty<Task>();
        if (lanes is not null)
        {
            foreach (var lane in lanes.Values)
                lane.Queue.Writer.TryComplete();
            lanePumps = lanes.Values.Select(l => l.Pump).ToArray();
        }

        _cts?.Cancel();
        foreach (var task in new[] { _pump, _periodic }.Concat(lanePumps))
        {
            if (task is null)
                continue;
            try { await task; } catch (OperationCanceledException) { /* expected */ }
        }
        _pump = _periodic = null;
        _commandLanes = null;
        Interlocked.Exchange(ref _pendingCommands, 0);

        _subscription?.Dispose();
        _subscription = null;

        if (_server is not null)
        {
            try { _server.Stop(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error stopping the IEC-104 server (ignored)"); }
            _server = null;
        }

        _connections.Clear();
        _cts?.Dispose();
        _cts = null;
        _logger.LogInformation("IEC-104 controlled station stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task SuperviseAsync(string name, Func<CancellationToken, Task> loop, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Pulse(name);
                await loop(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                throw new InvalidOperationException($"IEC-104 {name} loop returned unexpectedly.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _loopRestarts);
                _lastError = $"{name} restarted: {ex.Message}";
                Pulse(name, ex);
                _logger.LogError(ex, "IEC-104 {Loop} loop stopped; restarting", name);
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
    }

    // --- outgoing: spontaneous reporting -------------------------------------

    private async Task PumpAsync(CancellationToken ct)
    {
        var subscription = _subscription!;
        var pending = new Dictionary<int, (Iec104PointDto Point, TagValue Value)>();
        var capacity = Math.Clamp(_options.OutboundQueueCapacity, 64, 100_000);
        var limiter = new PointRateLimiter(Math.Clamp(_options.MaxSpontaneousPerSecond, 1, 100_000));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (pending.Count == 0 && !await WaitForUpdatesAsync(subscription.Reader, "reporting", ct))
                {
                    if (ct.IsCancellationRequested) break;
                    var closed = new InvalidOperationException("IEC-104 reporting subscription closed unexpectedly.");
                    _lastError = closed.Message;
                    Pulse("reporting", closed);
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                while (subscription.Reader.TryRead(out var batch))
                {
                    foreach (var read in batch)
                    {
                        if (!_byTagPath.TryGetValue(read.TagId, out var points))
                            continue;
                        foreach (var point in points)
                        {
                            _lastReported.TryGetValue(point.Ioa, out var previous);
                            if (!Iec104ValueMapping.ExceedsDeadband(point, previous, read.Value))
                                continue;
                            if (pending.ContainsKey(point.Ioa))
                                Interlocked.Increment(ref _coalescedUpdates);
                            else if (pending.Count >= capacity)
                            {
                                Interlocked.Increment(ref _capacityDrops);
                                continue;
                            }
                            pending[point.Ioa] = (point, read.Value);
                        }
                    }
                }

                Volatile.Write(ref _pendingOutbound, pending.Count);

                // Same rule as the telemetry bridge: the pulse says "this loop completed a pass", not
                // "a point moved". The tag channel is never idle on a polling gateway, so the 5 s idle
                // heartbeat in WaitForUpdatesAsync is unreachable here; a station whose points all sit
                // inside their deadbands would otherwise be reported stale while working perfectly.
                Pulse("reporting");
                if (pending.Count == 0)
                    continue;

                var permitted = await limiter.TakeAsync(Math.Min(pending.Count, 512), ct);
                var outgoing = pending.Take(permitted).Select(x => (x.Value.Point, (TagValue?)x.Value.Value)).ToList();
                foreach (var item in outgoing)
                {
                    pending.Remove(item.Point.Ioa);
                }
                var sent = SendMany(CauseOfTransmission.SPONTANEOUS, outgoing);
                Interlocked.Add(ref _spontaneousSent, sent);
                Volatile.Write(ref _pendingOutbound, pending.Count);
                Pulse("reporting");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104 reporting loop stopped unexpectedly");
            throw;
        }
    }

    /// <summary>
    /// Cyclic transmission of every mapped point, regardless of whether it changed.
    ///
    /// Report-by-exception alone cannot promise a refresh interval: a value that sits still is simply
    /// never sent again. EVN 378/QĐ-EVN requires the control centre to be updated at least every five
    /// minutes, so that guarantee has to come from here (set <c>PeriodicIntervalSeconds</c> to 300).
    /// </summary>
    private async Task PeriodicAsync(CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(_options.PeriodicIntervalSeconds);
        try
        {
            var nextCycle = DateTimeOffset.UtcNow + period;
            while (!ct.IsCancellationRequested)
            {
                await DelayWithHeartbeatAsync(nextCycle - DateTimeOffset.UtcNow, "periodic transmission", ct);
                var snapshot = _tags.Snapshot();
                var points = _byIoa.Values.OrderBy(p => p.Ioa).ToArray();
                var spread = points.Length == 0 ? TimeSpan.Zero
                    : TimeSpan.FromTicks(Math.Max(1, period.Ticks / points.Length));
                foreach (var batch in points.Chunk(64))
                {
                    SendMany(CauseOfTransmission.PERIODIC,
                        batch.Select(point => (point, Lookup(snapshot, point))).ToList());
                    await DelayWithHeartbeatAsync(TimeSpan.FromTicks(spread.Ticks * batch.Length),
                        "periodic transmission", ct);
                }
                nextCycle += period;
                if (nextCycle < DateTimeOffset.UtcNow)
                    nextCycle = DateTimeOffset.UtcNow + period;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104 periodic transmission loop stopped unexpectedly");
            throw;
        }
    }

    /// <summary>Queue points to every active connection, packing compatible objects into ASDUs.</summary>
    private int SendMany(CauseOfTransmission cot,
        IReadOnlyList<(Iec104PointDto Point, TagValue? Value)> values)
    {
        var server = _server;
        if (server is null)
            return 0;

        var sent = 0;
        foreach (var group in values.GroupBy(x => (x.Point.Type, x.Point.WithTimestamp)))
        {
            ASDU? asdu = null;
            var included = new List<(Iec104PointDto Point, TagValue? Value)>();

            void Flush()
            {
                if (asdu is null || included.Count == 0) return;
                try
                {
                    server.EnqueueASDU(asdu);
                    MarkSent();
                    sent += included.Count;
                    foreach (var item in included)
                        if (item.Value is not null)
                            _lastReported[item.Point.Ioa] = item.Value;
                }
                catch (Exception ex)
                {
                    Interlocked.Add(ref _capacityDrops, included.Count);
                    _lastError = ex.Message;
                    _logger.LogWarning(ex, "IEC-104: outbound queue refused {Count} information object(s)", included.Count);
                }
                asdu = null;
                included.Clear();
            }

            foreach (var item in group)
            {
                var informationObject = Iec104ValueMapping.ToInformationObject(item.Point, item.Value, _options.TimestampsInUtc);
                if (informationObject is null)
                {
                    Interlocked.Increment(ref _capacityDrops);
                    continue;
                }

                asdu ??= NewAsdu(server, cot);
                if (!asdu.AddInformationObject(informationObject))
                {
                    Flush();
                    asdu = NewAsdu(server, cot);
                    if (!asdu.AddInformationObject(informationObject))
                    {
                        Interlocked.Increment(ref _capacityDrops);
                        _logger.LogWarning("IEC-104: IOA {Ioa} cannot fit in an empty ASDU", item.Point.Ioa);
                        asdu = null;
                        continue;
                    }
                }
                included.Add(item);
            }
            Flush();
        }
        return sent;
    }

    private ASDU NewAsdu(CS104Server server, CauseOfTransmission cot) =>
        new(server.GetApplicationLayerParameters(), cot,
            isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);

    private int SendOnConnection(IMasterConnection connection, CauseOfTransmission cot,
        IReadOnlyList<(Iec104PointDto Point, TagValue? Value)> values, bool updateBaseline)
    {
        var sent = 0;
        foreach (var group in values.GroupBy(x => (x.Point.Type, x.Point.WithTimestamp)))
        {
            ASDU? asdu = null;
            var included = new List<(Iec104PointDto Point, TagValue? Value)>();

            void Flush()
            {
                if (asdu is null || included.Count == 0) return;
                try
                {
                    connection.SendASDU(asdu);
                    MarkSent();
                    sent += included.Count;
                    if (updateBaseline)
                        foreach (var item in included)
                            if (item.Value is not null)
                                _lastReported[item.Point.Ioa] = item.Value;
                }
                catch
                {
                    Interlocked.Add(ref _capacityDrops, included.Count);
                    throw;
                }
                asdu = null;
                included.Clear();
            }

            foreach (var item in group)
            {
                var informationObject = Iec104ValueMapping.ToInformationObject(item.Point, item.Value, _options.TimestampsInUtc);
                if (informationObject is null)
                {
                    Interlocked.Increment(ref _capacityDrops);
                    continue;
                }

                asdu ??= new ASDU(connection.GetApplicationLayerParameters(), cot,
                    isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);
                if (!asdu.AddInformationObject(informationObject))
                {
                    Flush();
                    asdu = new ASDU(connection.GetApplicationLayerParameters(), cot,
                        isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);
                    if (!asdu.AddInformationObject(informationObject))
                    {
                        Interlocked.Increment(ref _capacityDrops);
                        _logger.LogWarning("IEC-104: IOA {Ioa} cannot fit in an empty response ASDU", item.Point.Ioa);
                        asdu = null;
                        continue;
                    }
                }
                included.Add(item);
            }
            Flush();
        }
        return sent;
    }

    // Counters and the "last traffic" stamps move together: a counter alone cannot tell an engineer
    // whether the link went quiet an hour ago.
    private void MarkSent()
    {
        Interlocked.Increment(ref _asdusSent);
        _lastSentAt = DateTimeOffset.UtcNow;
    }

    private void MarkReceived()
    {
        Interlocked.Increment(ref _asdusReceived);
        _lastReceivedAt = DateTimeOffset.UtcNow;
    }

    private TagValue? Lookup(IReadOnlyDictionary<string, TagValue> snapshot, Iec104PointDto point) =>
        !string.IsNullOrEmpty(point.TagPath) && snapshot.TryGetValue(point.TagPath, out var value) ? value : null;

    // --- incoming: interrogation, clock, commands ----------------------------

    /// <summary>
    /// Called as each TCP connection is accepted. This — not the OPENED event, which lib60870.NET
    /// 2.3.0 never raises — is where an arriving control centre becomes visible.
    /// </summary>
    private bool OnConnectionRequest(object? parameter, System.Net.IPAddress address)
    {
        if (!_options.AllowedClients.Any(rule => IpMatches(address, rule)))
        {
            _logger.LogWarning("IEC-104: refused control centre {Address}; it is not in AllowedClients", address);
            return false;
        }
        var key = address.ToString();
        _connections.AddOrUpdate(key,
            _ => (1, DateTimeOffset.UtcNow),
            (_, existing) => (existing.Count + 1, existing.FirstSeen));
        _logger.LogInformation("IEC-104: control centre {Address} connected", key);
        return true;   // accept; per-source filtering would belong in the firewall
    }

    private void OnConnectionEvent(object? parameter, ClientConnection connection, ClientConnectionEvent e)
    {
        if (e != ClientConnectionEvent.CLOSED)
            return;   // OPENED/ACTIVE/INACTIVE are never delivered by this library version

        var key = connection.RemoteEndpoint?.Address.ToString();
        if (key is null)
            return;

        _connections.AddOrUpdate(key,
            _ => (0, DateTimeOffset.UtcNow),
            (_, existing) => (Math.Max(0, existing.Count - 1), existing.FirstSeen));
        if (_connections.TryGetValue(key, out var remaining) && remaining.Count == 0)
            _connections.TryRemove(key, out _);
        _logger.LogInformation("IEC-104: control centre {Address} disconnected", key);
        foreach (var item in _selected.Where(kv => ReferenceEquals(kv.Value.Connection, connection)).ToList())
            _selected.TryRemove(item.Key, out _);
    }

    /// <summary>
    /// General interrogation (C_IC_NA_1): confirm, send the current value of every mapped point with
    /// cause "interrogated by station", then terminate. This is how a control centre fills its picture
    /// after connecting, so points that have never been read are still sent — flagged invalid.
    /// </summary>
    private bool OnInterrogation(object? parameter, IMasterConnection connection, ASDU asdu, byte qoi)
    {
        MarkReceived();

        if (asdu.Ca != _options.CommonAddress)
        {
            SendNegative(connection, asdu);
            return true;
        }

        try
        {
            connection.SendACT_CON(asdu, false);

            var snapshot = _tags.Snapshot();
            SendOnConnection(connection, CauseOfTransmission.INTERROGATED_BY_STATION,
                _byIoa.Values.OrderBy(p => p.Ioa).Select(p => (p, Lookup(snapshot, p))).ToList(),
                updateBaseline: true);

            connection.SendACT_TERM(asdu);
            Interlocked.Increment(ref _interrogationsServed);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104: general interrogation failed");
        }

        return true;
    }

    /// <summary>
    /// Counter interrogation (C_CI_NA_1): the same, for the points mapped as integrated totals.
    ///
    /// KNOWN DEVIATION: the answers go out with cause of transmission 20 ("interrogated by station")
    /// instead of the 37 ("requested by general counter") the standard specifies. lib60870.NET 2.3.0
    /// overwrites the cause we set — confirmed at byte level, and it happens whether the ASDU is sent
    /// on the requesting connection or queued afterwards, so it cannot be worked around from here. The
    /// values, type identification and addresses are correct, and most control centres key on those;
    /// confirm it is accepted during commissioning, and if the control centre insists on 37 the fix is
    /// a patched or different protocol library.
    /// </summary>
    private bool OnCounterInterrogation(object? parameter, IMasterConnection connection, ASDU asdu, byte qcc)
    {
        MarkReceived();

        var server = _server;
        var counters = _byIoa.Values.Where(p => p.Type == Iec104PointType.IntegratedTotals).OrderBy(p => p.Ioa).ToList();
        if (server is null || asdu.Ca != _options.CommonAddress || counters.Count == 0)
        {
            SendNegative(connection, asdu);
            return true;
        }

        try
        {
            connection.SendACT_CON(asdu, false);
            SendCounters(counters, _tags.Snapshot(), connection, asdu);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104: counter interrogation failed");
        }

        return true;
    }

    private void SendCounters(List<Iec104PointDto> counters, IReadOnlyDictionary<string, TagValue> snapshot,
        IMasterConnection connection, ASDU request)
    {
        var server = _server;
        if (server is null)
            return;

        try
        {
            SendOnConnection(connection, CauseOfTransmission.REQUESTED_BY_GENERAL_COUNTER,
                counters.Select(p => (p, Lookup(snapshot, p))).ToList(), updateBaseline: false);
            connection.SendACT_TERM(request);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104: counter interrogation failed");
        }
    }

    /// <summary>Read command (C_RD_NA_1): answer with the single point that was asked for.</summary>
    private bool OnRead(object? parameter, IMasterConnection connection, ASDU asdu, int ioa)
    {
        MarkReceived();

        if (!_byIoa.TryGetValue(ioa, out var point))
        {
            SendNegative(connection, asdu);
            return true;
        }

        try
        {
            var informationObject = Iec104ValueMapping.ToInformationObject(
                point, Lookup(_tags.Snapshot(), point), _options.TimestampsInUtc);
            if (informationObject is null)
            {
                SendNegative(connection, asdu);
                return true;
            }

            if (SendOnConnection(connection, CauseOfTransmission.REQUEST, [(point, Lookup(_tags.Snapshot(), point))],
                    updateBaseline: false) == 0)
                SendNegative(connection, asdu);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104: read of IOA {Ioa} failed", ioa);
        }

        return true;
    }

    /// <summary>
    /// Clock synchronisation (C_CS_NA_1). The offset is recorded and confirmed, but the machine clock
    /// is deliberately not moved: a gateway that jumps its own clock invalidates every timestamp it
    /// has already produced, and keeping system time correct belongs to the OS (NTP).
    /// </summary>
    private bool OnClockSync(object? parameter, IMasterConnection connection, ASDU asdu, CP56Time2a newTime)
    {
        MarkReceived();

        if (!_options.AllowClockSync)
        {
            SendNegative(connection, asdu);
            return false;
        }

        var theirs = newTime.GetDateTime();
        var ours = _options.TimestampsInUtc ? DateTime.UtcNow : DateTime.Now;
        _lastClockOffsetSeconds = (theirs - ours).TotalSeconds;

        if (Math.Abs(_lastClockOffsetSeconds.Value) > 5)
            _logger.LogWarning("IEC-104: control centre clock differs from ours by {Offset:0.###} s — " +
                               "check NTP on this machine", _lastClockOffsetSeconds);

        return true;
    }

    /// <summary>
    /// Reset process (C_RP_NA_1, type 105 — required by EVN 378/QĐ-EVN).
    ///
    /// What it deliberately does NOT do is touch the plant. In IEC 60870-5 the command resets the
    /// <em>telecontrol</em> station, and a gateway that restarted inverters because a control centre
    /// sent a protocol housekeeping command would be dangerous. So: confirm it, forget what we have
    /// already reported (so the next interrogation and every change are transmitted afresh), and log it.
    /// </summary>
    private bool OnResetProcess(object? parameter, IMasterConnection connection, ASDU asdu, byte qrp)
    {
        MarkReceived();

        if (asdu.Ca != _options.CommonAddress)
        {
            SendNegative(connection, asdu);
            return true;
        }

        // QRP 1 = general reset of process, 2 = reset of pending information with time tag.
        _lastReported.Clear();
        _selected.Clear();
        _logger.LogWarning("IEC-104: reset process (QRP={Qrp}) from the control centre — report cache cleared. " +
                           "The process itself is untouched.", qrp);

        try
        {
            connection.SendACT_CON(asdu, false);
            MarkSent();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "IEC-104: could not confirm the reset process command");
        }
        return true;
    }

    /// <summary>
    /// Delay acquisition (C_CD_NA_1). Only meaningful on serial links where propagation delay has to
    /// be measured; over TCP there is nothing to compute, so it is acknowledged and ignored. It is
    /// handled purely so an unset callback cannot drop the connection.
    /// </summary>
    private bool OnDelayAcquisition(object? parameter, IMasterConnection connection, ASDU asdu, CP16Time2a delay)
    {
        MarkReceived();
        _logger.LogInformation("IEC-104: delay acquisition received and acknowledged (not applicable over TCP)");
        try
        {
            connection.SendACT_CON(asdu, false);
            MarkSent();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        return true;
    }

    /// <summary>Commands. Confirmed first, then executed, then terminated — or refused negatively.</summary>
    private bool OnAsdu(object? parameter, IMasterConnection connection, ASDU asdu)
    {
        MarkReceived();

        if (!_options.AllowCommands)
        {
            _logger.LogWarning("IEC-104: {Type} refused — commands are disabled in the configuration", asdu.TypeId);
            Reject(connection, asdu);
            return true;
        }

        if (asdu.Ca != _options.CommonAddress)
        {
            Reject(connection, asdu);
            return true;
        }

        if (!TryReadCommand(asdu, out var ioa, out var value, out var select, out var reason))
        {
            _logger.LogWarning("IEC-104: {Type} refused — {Reason}", asdu.TypeId, reason);
            Reject(connection, asdu);
            return true;
        }

        if (!_byIoa.TryGetValue(ioa, out var point) || point.Command == Iec104CommandType.None)
        {
            _logger.LogWarning("IEC-104: command for IOA {Ioa} refused — not mapped as commandable", ioa);
            Reject(connection, asdu);
            return true;
        }

        if (!IsCommandTypeAllowed(point, asdu.TypeId))
        {
            _logger.LogWarning("IEC-104: {Type} refused for IOA {Ioa} — configured as {Configured}",
                asdu.TypeId, ioa, point.Command);
            Reject(connection, asdu);
            return true;
        }

        // Select/execute: a select is only confirmed and remembered; the execute does the work.
        if (select)
        {
            if (_selected.TryGetValue(ioa, out var held)
                && DateTimeOffset.UtcNow - held.SelectedAt <= SelectWindow
                && !ReferenceEquals(held.Connection, connection))
            {
                Reject(connection, asdu);
                return true;
            }
            _selected[ioa] = new Selection(connection, asdu.TypeId, value!, DateTimeOffset.UtcNow);
            connection.SendACT_CON(asdu, false);
            Audit(connection, asdu, "select", "Successful", null);
            return true;
        }

        var hasSelection = _selected.TryGetValue(ioa, out var selected);
        if (point.RequireSelect || hasSelection)
        {
            _selected.TryRemove(ioa, out _);
            var valid = hasSelection && selected is not null
                && ReferenceEquals(selected.Connection, connection)
                && selected.TypeId == asdu.TypeId
                && Equals(selected.Value, value)
                && DateTimeOffset.UtcNow - selected.SelectedAt <= SelectWindow;
            if (!valid)
            {
            _logger.LogWarning("IEC-104: execute for IOA {Ioa} refused — the select expired", ioa);
            Reject(connection, asdu);
                return true;
            }
        }
        _selected.TryRemove(ioa, out _);

        // Never let control-centre traffic create unbounded Tasks. The synchronous protocol callback
        // only admits the command; a dispatcher per destination then preserves order towards that
        // destination and applies a deadline that includes time spent queued behind earlier commands.
        var lanes = _commandLanes;
        if (lanes is null || Volatile.Read(ref _stopping) != 0)
        {
            Reject(connection, asdu);
            return true;
        }

        // Capacity is global even though the queues are not: the resource being protected is this
        // process, and a per-lane limit would multiply by however many devices the project happens
        // to have.
        if (!TryReserveCommandSlot())
        {
            _logger.LogWarning("IEC-104: command {Type} IOA {Ioa} refused — command queue is full ({Capacity})",
                asdu.TypeId, ioa, Math.Clamp(_options.CommandQueueCapacity, 1, 10_000));
            Reject(connection, asdu);
            return true;
        }

        var remote = (connection as ClientConnection)?.RemoteEndpoint?.Address.ToString();
        var command = new CommandWorkItem(connection, asdu, point, value!, remote, DateTimeOffset.UtcNow);
        if (!Lane(lanes, point).Queue.Writer.TryWrite(command))
        {
            Interlocked.Decrement(ref _pendingCommands);
            Reject(connection, asdu);
            return true;
        }

        try
        {
            connection.SendACT_CON(asdu, false);
            MarkSent();
            command.Confirmed.TrySetResult(true);
        }
        catch (Exception ex)
        {
            command.Confirmed.TrySetResult(false);
            Interlocked.Increment(ref _commandsRejected);
            _lastError = ex.Message;
            _logger.LogWarning(ex, "IEC-104: could not confirm command {Type} IOA {Ioa}", asdu.TypeId, ioa);
        }

        return true;
    }

    /// <summary>
    /// A queue and its dispatcher, for one physical destination.
    ///
    /// <para>
    /// This used to be a single queue with a single reader that awaited each command before taking
    /// the next. Every command in the station therefore queued behind every other one, and a command
    /// takes as long as the device takes to answer — so one PLC that had stopped responding held up
    /// commands to every healthy PLC for its full deadline, each. At the defaults, 10 s per command
    /// against a queue of 256, a command to a healthy device could wait about 42 minutes behind a
    /// single dead one. The README promised the opposite.
    /// </para>
    ///
    /// <para>
    /// Splitting by destination keeps the guarantee that actually matters — commands towards one
    /// device are still executed one at a time, in the order the control centre sent them — and
    /// drops the one that was never wanted, that unrelated devices wait for each other. Lanes are
    /// created on demand and live as long as the server: a reload builds a new server, so there is
    /// no separate cleanup path to get wrong.
    /// </para>
    /// </summary>
    private sealed record CommandLane(Channel<CommandWorkItem> Queue, Task Pump);

    private CommandLane Lane(ConcurrentDictionary<string, CommandLane> lanes, Iec104PointDto point)
    {
        var key = _partition(point.TagPath) ?? point.TagPath;
        return lanes.GetOrAdd(key, static (_, state) => state.Self.CreateLane(state.Token),
            (Self: this, Token: _cts?.Token ?? CancellationToken.None));
    }

    private CommandLane CreateLane(CancellationToken ct)
    {
        // Unbounded, because admission is already counted globally in TryReserveCommandSlot and a
        // second bound here could only reject a command that the station had already accepted.
        var queue = Channel.CreateUnbounded<CommandWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        // Supervised under the one shared name so that health reports a dispatcher that is up or
        // down, rather than a component per device appearing and going stale across reloads.
        var pump = Task.Run(() => SuperviseAsync("command dispatcher", token => CommandPumpAsync(queue, token), ct), ct);
        return new CommandLane(queue, pump);
    }

    /// <summary>Global admission, replacing the bounded channel the single queue used to be.</summary>
    private bool TryReserveCommandSlot()
    {
        var capacity = Math.Clamp(_options.CommandQueueCapacity, 1, 10_000);
        while (true)
        {
            var observed = Volatile.Read(ref _pendingCommands);
            if (observed >= capacity)
                return false;
            if (Interlocked.CompareExchange(ref _pendingCommands, observed + 1, observed) == observed)
                return true;
        }
    }

    private async Task CommandPumpAsync(Channel<CommandWorkItem> queue, CancellationToken ct)
    {
        try
        {
            while (await WaitForUpdatesAsync(queue.Reader, "command dispatcher", ct))
            {
                while (queue.Reader.TryRead(out var command))
                {
                    Interlocked.Decrement(ref _pendingCommands);
                    if (!await command.Confirmed.Task.WaitAsync(ct))
                        continue;
                    await ExecuteCommandAsync(command, ct);
                    Pulse("command dispatcher");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // StopAsync reports every already-confirmed command still in the queue as failed below.
        }
        finally
        {
            while (queue.Reader.TryRead(out var command))
            {
                Interlocked.Decrement(ref _pendingCommands);
                if (command.Confirmed.Task.IsCompletedSuccessfully && command.Confirmed.Task.Result)
                {
                    Interlocked.Increment(ref _commandsRejected);
                    SendFailedTermination(command, "the station is stopping");
                }
            }
        }
    }

    private async Task<bool> WaitForUpdatesAsync<T>(ChannelReader<T> reader, string loop, CancellationToken ct)
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
        var remaining = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
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
        catch (Exception ex) { _logger.LogDebug(ex, "IEC-104 loop-health observer failed"); }
    }

    private sealed class PointRateLimiter
    {
        private readonly double _rate;
        private readonly double _capacity;
        private double _tokens;
        private long _last = Stopwatch.GetTimestamp();

        public PointRateLimiter(int rate)
        {
            _rate = rate;
            _capacity = Math.Max(1, rate / 5d); // at most a 200 ms startup burst
            _tokens = _capacity;
        }

        public async Task<int> TakeAsync(int maximum, CancellationToken ct)
        {
            while (true)
            {
                var now = Stopwatch.GetTimestamp();
                _tokens = Math.Min(_capacity, _tokens + Stopwatch.GetElapsedTime(_last, now).TotalSeconds * _rate);
                _last = now;
                var available = Math.Min(maximum, (int)_tokens);
                if (available > 0)
                {
                    _tokens -= available;
                    return available;
                }
                var wait = TimeSpan.FromSeconds(Math.Max(0.001, (1 - _tokens) / _rate));
                await Task.Delay(wait, ct);
            }
        }
    }

    private async Task ExecuteCommandAsync(CommandWorkItem command, CancellationToken serverToken)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(_options.CommandTimeoutMs, 100, 600_000));
        var remaining = timeout - (DateTimeOffset.UtcNow - command.AcceptedAt);
        if (remaining <= TimeSpan.Zero)
        {
            CommandTimedOut(command);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        deadline.CancelAfter(remaining);
        try
        {
            await _writer(command.Point.TagPath, command.Value, command.RemoteAddress, deadline.Token);
            Interlocked.Increment(ref _commandsAccepted);
            command.Connection.SendACT_TERM(command.Asdu);
            MarkSent();
            _logger.LogInformation("IEC-104: command {Type} IOA {Ioa} wrote {Value} to {Tag}",
                command.Asdu.TypeId, command.Point.Ioa, command.Value, command.Point.TagPath);
        }
        catch (OperationCanceledException) when (!serverToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            CommandTimedOut(command);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _commandsRejected);
            SendFailedTermination(command, "the station is stopping");
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _commandsRejected);
            _lastError = ex.Message;
            _logger.LogError(ex, "IEC-104: command {Type} IOA {Ioa} failed", command.Asdu.TypeId, command.Point.Ioa);
            SendFailedTermination(command, ex.Message);
        }
    }

    private void CommandTimedOut(CommandWorkItem command)
    {
        Interlocked.Increment(ref _commandsRejected);
        Interlocked.Increment(ref _commandTimeouts);
        var reason = $"command exceeded {_options.CommandTimeoutMs} ms deadline";
        _lastError = reason;
        _logger.LogWarning("IEC-104: command {Type} IOA {Ioa} timed out after {Timeout} ms",
            command.Asdu.TypeId, command.Point.Ioa, _options.CommandTimeoutMs);
        SendFailedTermination(command, reason);
    }

    private void SendFailedTermination(CommandWorkItem command, string reason)
    {
        try
        {
            var termination = new ASDU(command.Connection.GetApplicationLayerParameters(),
                CauseOfTransmission.ACTIVATION_TERMINATION,
                isTest: false, isNegative: true, oa: 0, ca: command.Asdu.Ca, isSequence: false);
            if (!termination.AddInformationObject(command.Asdu.GetElement(0)))
                throw new InvalidOperationException("The failed command object cannot fit in an empty termination ASDU.");
            command.Connection.SendASDU(termination);
            MarkSent();
        }
        catch (Exception sendEx)
        {
            _lastError = $"{reason}; failure response could not be sent: {sendEx.Message}";
            _logger.LogWarning(sendEx, "IEC-104: could not report the failed command");
        }
    }

    private sealed record CommandWorkItem(IMasterConnection Connection, ASDU Asdu,
        Iec104PointDto Point, object Value, string? RemoteAddress, DateTimeOffset AcceptedAt)
    {
        public TaskCompletionSource<bool> Confirmed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Selection(IMasterConnection Connection, TypeID TypeId, object Value,
        DateTimeOffset SelectedAt);

    private static bool IpMatches(System.Net.IPAddress address, string rule)
    {
        var parts = rule.Trim().Split('/', 2);
        if (!System.Net.IPAddress.TryParse(parts[0], out var network)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (network.IsIPv4MappedToIPv6) network = network.MapToIPv4();
        var candidate = address.GetAddressBytes();
        var expected = network.GetAddressBytes();
        if (candidate.Length != expected.Length) return false;
        var bits = parts.Length == 1 ? candidate.Length * 8
            : int.TryParse(parts[1], out var parsed) ? parsed : -1;
        if (bits < 0 || bits > candidate.Length * 8) return false;
        for (var i = 0; i < candidate.Length && bits > i * 8; i++)
        {
            var remaining = bits - i * 8;
            var mask = remaining >= 8 ? 0xff : (0xff << (8 - remaining)) & 0xff;
            if ((candidate[i] & mask) != (expected[i] & mask)) return false;
        }
        return true;
    }

    private static bool IsCommandTypeAllowed(Iec104PointDto point, TypeID typeId) => point.Command switch
    {
        Iec104CommandType.SingleCommand => typeId is TypeID.C_SC_NA_1 or TypeID.C_SC_TA_1,
        Iec104CommandType.DoubleCommand => typeId is TypeID.C_DC_NA_1 or TypeID.C_DC_TA_1,
        Iec104CommandType.SetpointNormalized => typeId is TypeID.C_SE_NA_1 or TypeID.C_SE_TA_1,
        Iec104CommandType.SetpointScaled => typeId is TypeID.C_SE_NB_1 or TypeID.C_SE_TB_1,
        Iec104CommandType.SetpointShort => typeId is TypeID.C_SE_NC_1 or TypeID.C_SE_TC_1,
        _ => false,
    };

    /// <summary>Pull the address, the value and the select flag out of a command ASDU.</summary>
    private static bool TryReadCommand(ASDU asdu, out int ioa, out object? value, out bool select, out string? reason)
    {
        ioa = 0;
        value = null;
        select = false;
        reason = null;

        if (asdu.NumberOfElements < 1)
        {
            reason = "the ASDU carries no information object";
            return false;
        }

        switch (asdu.GetElement(0))
        {
            case SingleCommand single:
                ioa = single.ObjectAddress;
                value = single.State;
                select = single.Select;
                return true;

            case DoubleCommand duo:
                ioa = duo.ObjectAddress;
                select = duo.Select;
                // 1 = OFF, 2 = ON; 0 and 3 are "not permitted" for a command.
                if (duo.State is 1 or 2)
                {
                    value = duo.State == 2;
                    return true;
                }
                reason = $"double command state {duo.State} is not permitted";
                return false;

            case SetpointCommandNormalized normalized:
                ioa = normalized.ObjectAddress;
                value = (double)normalized.NormalizedValue;
                select = normalized.QOS.Select;
                return true;

            case SetpointCommandScaled scaled:
                ioa = scaled.ObjectAddress;
                value = (double)scaled.ScaledValue.Value;
                select = scaled.QOS.Select;
                return true;

            case SetpointCommandShort shortFloat:
                ioa = shortFloat.ObjectAddress;
                value = (double)shortFloat.Value;
                select = shortFloat.QOS.Select;
                return true;

            default:
                reason = $"{asdu.TypeId} is not a command this station accepts";
                return false;
        }
    }

    private void Reject(IMasterConnection connection, ASDU asdu)
    {
        Interlocked.Increment(ref _commandsRejected);
        Audit(connection, asdu, "command", "Rejected", "Rejected by IEC-104 command policy.");
        SendNegative(connection, asdu);
    }

    private void Audit(IMasterConnection connection, ASDU asdu, string action, string outcome, string? reason)
    {
        if (_audit is null) return;
        TryReadCommand(asdu, out var ioa, out var value, out _, out _);
        _byIoa.TryGetValue(ioa, out var point);
        var remote = (connection as ClientConnection)?.RemoteEndpoint?.Address.ToString() ?? "unknown";
        var task = _audit(new Iec104ControlAudit(DateTimeOffset.UtcNow, remote, action, ioa,
            point?.TagPath, value, outcome, reason));
        _ = task.ContinueWith(t => _logger.LogError(t.Exception, "IEC-104 audit persistence failed"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SendNegative(IMasterConnection connection, ASDU asdu)
    {
        try
        {
            connection.SendACT_CON(asdu, true);
            MarkSent();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "IEC-104: could not send the negative confirmation");
        }
    }
}
