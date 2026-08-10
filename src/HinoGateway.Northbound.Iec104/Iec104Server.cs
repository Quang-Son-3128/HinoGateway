using System.Collections.Concurrent;
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
    private readonly Iec104TagWriter _writer;
    private readonly ILogger<Iec104Server> _logger;

    private readonly Dictionary<int, Iec104PointDto> _byIoa;
    private readonly Dictionary<string, List<Iec104PointDto>> _byTagPath;
    private readonly ConcurrentDictionary<int, TagValue> _lastReported = new();
    private readonly ConcurrentDictionary<int, DateTimeOffset> _selected = new();

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

    private long _asdusSent, _asdusReceived, _commandsAccepted, _commandsRejected;
    private long _spontaneousSent, _interrogationsServed;
    private DateTimeOffset? _lastSentAt, _lastReceivedAt;
    private double? _lastClockOffsetSeconds;
    private string? _lastError;
    private string? _warning;

    public Iec104Server(Iec104ServerOptions options, TagStore tags, Iec104TagWriter writer,
        ILogger<Iec104Server> logger)
    {
        _options = options;
        _tags = tags;
        _writer = writer;
        _logger = logger;

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
        Warning: _warning);

    public void Start()
    {
        if (_server is not null)
            return;

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
        _subscription = _tags.Subscribe("iec104", capacity: 4096, dropOldest: true);
        _pump = Task.Run(() => PumpAsync(_cts.Token));
        if (_options.PeriodicIntervalSeconds > 0)
            _periodic = Task.Run(() => PeriodicAsync(_cts.Token));

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
        _cts?.Cancel();
        foreach (var task in new[] { _pump, _periodic })
        {
            if (task is null)
                continue;
            try { await task; } catch (OperationCanceledException) { /* expected */ }
        }
        _pump = _periodic = null;

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

    // --- outgoing: spontaneous reporting -------------------------------------

    private async Task PumpAsync(CancellationToken ct)
    {
        var subscription = _subscription!;
        try
        {
            await foreach (var batch in subscription.Reader.ReadAllAsync(ct))
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

                        _lastReported[point.Ioa] = read.Value;
                        if (Send(CauseOfTransmission.SPONTANEOUS, point, read.Value))
                            Interlocked.Increment(ref _spontaneousSent);
                    }
                }
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
            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(ct))
            {
                var snapshot = _tags.Snapshot();
                foreach (var point in _byIoa.Values.OrderBy(p => p.Ioa))
                {
                    var value = Lookup(snapshot, point);

                    // A cyclic send is still a report: it becomes the baseline the deadband measures
                    // the next change against. A point with no value at all leaves the baseline alone,
                    // so the first real reading is still treated as news.
                    if (value is not null)
                        _lastReported[point.Ioa] = value;

                    Send(CauseOfTransmission.PERIODIC, point, value);
                }
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
        }
    }

    /// <summary>Queue one point to every active connection. False when it could not be represented.</summary>
    private bool Send(CauseOfTransmission cot, Iec104PointDto point, TagValue? value)
    {
        var server = _server;
        if (server is null)
            return false;

        var informationObject = Iec104ValueMapping.ToInformationObject(point, value, _options.TimestampsInUtc);
        if (informationObject is null)
            return false;

        try
        {
            var asdu = new ASDU(server.GetApplicationLayerParameters(), cot,
                isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);
            asdu.AddInformationObject(informationObject);
            server.EnqueueASDU(asdu);
            MarkSent();
            return true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "IEC-104: could not queue IOA {Ioa}", point.Ioa);
            return false;
        }
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
            foreach (var point in _byIoa.Values.OrderBy(p => p.Ioa))
            {
                var value = Lookup(snapshot, point);
                _lastReported[point.Ioa] = value ?? _lastReported.GetValueOrDefault(point.Ioa)!;
                var informationObject = Iec104ValueMapping.ToInformationObject(point, value, _options.TimestampsInUtc);
                if (informationObject is null)
                    continue;

                var response = new ASDU(connection.GetApplicationLayerParameters(),
                    CauseOfTransmission.INTERROGATED_BY_STATION,
                    isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);
                response.AddInformationObject(informationObject);
                connection.SendASDU(response);
                MarkSent();
            }

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
            foreach (var point in counters)
            {
                var informationObject = Iec104ValueMapping.ToInformationObject(point, Lookup(snapshot, point), _options.TimestampsInUtc);
                if (informationObject is null)
                    continue;

                var response = new ASDU(server.GetApplicationLayerParameters(),
                    CauseOfTransmission.REQUESTED_BY_GENERAL_COUNTER,
                    isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);
                response.AddInformationObject(informationObject);
                server.EnqueueASDU(response);
                MarkSent();
            }
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

            var response = new ASDU(connection.GetApplicationLayerParameters(), CauseOfTransmission.REQUEST,
                isTest: false, isNegative: false, oa: 0, ca: _options.CommonAddress, isSequence: false);
            response.AddInformationObject(informationObject);
            connection.SendASDU(response);
            MarkSent();
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
            _selected[ioa] = DateTimeOffset.UtcNow;
            connection.SendACT_CON(asdu, false);
            return true;
        }

        if (point.Command is Iec104CommandType.SingleCommand or Iec104CommandType.DoubleCommand
            && _selected.TryGetValue(ioa, out var selectedAt)
            && DateTimeOffset.UtcNow - selectedAt > SelectWindow)
        {
            _selected.TryRemove(ioa, out _);
            _logger.LogWarning("IEC-104: execute for IOA {Ioa} refused — the select expired", ioa);
            Reject(connection, asdu);
            return true;
        }
        _selected.TryRemove(ioa, out _);

        connection.SendACT_CON(asdu, false);

        // The write happens off the protocol thread: a device that is slow to accept must not stall
        // the link, and ACT_TERM is what reports the real outcome.
        _ = Task.Run(async () =>
        {
            try
            {
                await _writer(point.TagPath, value!, CancellationToken.None);
                Interlocked.Increment(ref _commandsAccepted);
                connection.SendACT_TERM(asdu);
                _logger.LogInformation("IEC-104: command {Type} IOA {Ioa} wrote {Value} to {Tag}",
                    asdu.TypeId, ioa, value, point.TagPath);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _commandsRejected);
                _lastError = ex.Message;
                _logger.LogError(ex, "IEC-104: command {Type} IOA {Ioa} failed", asdu.TypeId, ioa);
                try
                {
                    // Terminate negatively so the dispatcher sees the command did not take effect.
                    var termination = new ASDU(connection.GetApplicationLayerParameters(),
                        CauseOfTransmission.ACTIVATION_TERMINATION,
                        isTest: false, isNegative: true, oa: 0, ca: asdu.Ca, isSequence: false);
                    termination.AddInformationObject(asdu.GetElement(0));
                    connection.SendASDU(termination);
                }
                catch (Exception sendEx)
                {
                    _logger.LogWarning(sendEx, "IEC-104: could not report the failed command");
                }
            }
        });

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
        SendNegative(connection, asdu);
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
