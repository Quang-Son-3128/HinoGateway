using System.Diagnostics;
using System.Net.Sockets;
using HinoGateway.Core;
using NModbus;

namespace HinoGateway.Drivers.Modbus;

/// <summary>
/// Modbus TCP master driver. Reads are issued as block requests built by <see cref="ModbusReadPlan"/>
/// so only address ranges that are actually needed go on the wire.
///
/// Three behaviours here exist because of how real devices misbehave:
/// <list type="bullet">
/// <item>A block the device <em>refuses</em> (a protocol-level SlaveException such as illegal data
/// address) is split in half and retried, narrowing down to the single offending point instead of
/// condemning every tag that shared the request. The bad point is then quarantined for a while and
/// re-probed, so the gateway stops paying for a request that always fails.</item>
/// <item>Each poll has a <em>deadline</em>. With retries and timeouts across several blocks, a sick
/// device could otherwise stretch a 500 ms scan into many seconds without anything looking wrong.</item>
/// <item>The plan is rebuilt on every connect, so a device that comes back with a different memory
/// map gets a clean slate.</item>
/// </list>
///
/// Settings (<see cref="DeviceConfig.Settings"/>): <c>Host</c>, <c>Port</c> (502), <c>UnitId</c> (1),
/// <c>TimeoutMs</c> (1000), <c>Retries</c> (1), <c>WordSwap</c> (false), <c>ByteSwap</c> (false),
/// <c>MaxRegisterGap</c> (8), <c>MaxBitGap</c> (64), <c>PollDeadlineMs</c> (5000),
/// <c>QuarantineMs</c> (60000).
/// </summary>
public sealed class ModbusTcpDriver : IDeviceDriver, IDriverDiagnostics, ITransportFaultClassifier
{
    private readonly DeviceConfig _config;
    private readonly string _host;
    private readonly int _port;
    private readonly byte _unitId;
    private readonly int _timeoutMs;
    private readonly int _retries;
    private readonly bool _wordSwap;
    private readonly bool _byteSwap;
    private readonly int _maxRegisterGap;
    private readonly int _maxBitGap;
    private readonly int _pollDeadlineMs;
    private readonly int _quarantineMs;

    private readonly List<ModbusPoint> _points;
    private readonly Dictionary<string, ModbusPoint> _pointsByTag;

    private ModbusReadBlock[] _blocks;
    private TcpClient? _tcp;
    private IModbusMaster? _master;

    public ModbusTcpDriver(DeviceConfig config)
    {
        _config = config;
        _host = Get("Host", "127.0.0.1");
        _port = GetInt("Port", 502);
        _unitId = byte.TryParse(Get("UnitId", "1"), out var u) ? u : (byte)1;
        _timeoutMs = GetInt("TimeoutMs", 1000);
        _retries = GetInt("Retries", 1);
        _wordSwap = bool.TryParse(Get("WordSwap", "false"), out var w) && w;
        _byteSwap = bool.TryParse(Get("ByteSwap", "false"), out var b) && b;
        _maxRegisterGap = Math.Max(0, GetInt("MaxRegisterGap", 8));
        _maxBitGap = Math.Max(0, GetInt("MaxBitGap", 64));
        _pollDeadlineMs = Math.Max(100, GetInt("PollDeadlineMs", 5000));
        _quarantineMs = Math.Max(1000, GetInt("QuarantineMs", 60000));

        _points = config.Tags
            .Select(tag => new ModbusPoint(tag, ModbusAddress.Parse(tag.Address),
                ModbusAddress.RegisterCount(tag.DataType)))
            .ToList();
        _pointsByTag = _points.ToDictionary(p => p.Tag.Id);
        _blocks = ModbusReadPlan.Build(_points, _maxRegisterGap, _maxBitGap).ToArray();
    }

    private string Get(string key, string dflt) =>
        _config.Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : dflt;

    private int GetInt(string key, int dflt) => int.TryParse(Get(key, ""), out var v) ? v : dflt;

    /// <summary>The current read plan and how each block is behaving.</summary>
    public IReadOnlyList<DriverBlockStat> Blocks
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            return Volatile.Read(ref _blocks).Select(b => b.ToStat(now)).ToList();
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port).WaitAsync(TimeSpan.FromMilliseconds(_timeoutMs), ct);

        var factory = new ModbusFactory();
        _master = factory.CreateMaster(_tcp);
        _master.Transport.ReadTimeout = _timeoutMs;
        _master.Transport.WriteTimeout = _timeoutMs;
        _master.Transport.Retries = _retries;

        // Fresh connection, fresh plan: forget every split and quarantine learned from the old one.
        Volatile.Write(ref _blocks, ModbusReadPlan.Build(_points, _maxRegisterGap, _maxBitGap).ToArray());
    }

    public async Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct)
    {
        if (_master is null)
            throw new InvalidOperationException("Modbus master not connected.");

        var now = DateTimeOffset.UtcNow;
        var registers = new Dictionary<ushort, ushort>();
        var bits = new Dictionary<ushort, bool>();
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        var notDue = new HashSet<string>(StringComparer.Ordinal);

        var budget = Stopwatch.StartNew();
        var queue = new Queue<ModbusReadBlock>(Volatile.Read(ref _blocks));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var block = queue.Dequeue();

            if (!block.IsDue(now))
            {
                foreach (var point in block.Points)
                    notDue.Add(point.Tag.Id);
                continue;
            }
            if (block.IsQuarantined(now))
            {
                Fail(block, block.LastError ?? "Address refused by the device; retrying later.");
                continue;
            }

            if (budget.ElapsedMilliseconds > _pollDeadlineMs)
            {
                // Out of time: report what we could not reach honestly instead of silently
                // over-running the scan interval poll after poll.
                Fail(block, $"Poll deadline of {_pollDeadlineMs} ms exceeded before this block was read.");
                continue;
            }

            var started = budget.Elapsed;
            try
            {
                if (block.Area is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput)
                {
                    var values = block.Area == ModbusRegisterType.DiscreteInput
                        ? await _master.ReadInputsAsync(_unitId, block.Start, block.Count)
                        : await _master.ReadCoilsAsync(_unitId, block.Start, block.Count);
                    for (var i = 0; i < values.Length; i++)
                        bits[(ushort)(block.Start + i)] = values[i];
                }
                else
                {
                    var values = block.Area == ModbusRegisterType.InputRegister
                        ? await _master.ReadInputRegistersAsync(_unitId, block.Start, block.Count)
                        : await _master.ReadHoldingRegistersAsync(_unitId, block.Start, block.Count);
                    for (var i = 0; i < values.Length; i++)
                        registers[(ushort)(block.Start + i)] = values[i];
                }

                block.RecordSuccess((budget.Elapsed - started).TotalMilliseconds);
                block.MarkPolled(now);
            }
            catch (SlaveException ex)
            {
                // Protocol-level refusal: the device answered, it just won't serve this range.
                var reason = $"Slave rejected {ModbusReadBlock.AreaName(block.Area)} {block.Start}+{block.Count} " +
                             $"(function {ex.FunctionCode}, exception code {ex.SlaveExceptionCode})";
                block.RecordError(reason);

                var halves = ModbusReadPlan.Split(block);
                if (halves.Count == 0)
                {
                    // Down to one point — that point is the problem.
                    block.Quarantine(now.AddMilliseconds(_quarantineMs), reason);
                    Fail(block, reason);
                }
                else
                {
                    ReplaceBlock(block, halves);
                    foreach (var half in halves)
                        queue.Enqueue(half);
                }
            }
            // Connection-level failures (socket, timeout) deliberately propagate: the worker owns
            // reconnecting, and pretending otherwise would hide a dead link.
        }

        var reads = new List<TagRead>(_points.Count);
        foreach (var point in _points)
        {
            if (notDue.Contains(point.Tag.Id))
                continue;
            if (failures.TryGetValue(point.Tag.Id, out var failure))
            {
                reads.Add(new TagRead(point.Tag.Id, TagValue.Bad(failure, now)));
                continue;
            }

            try
            {
                reads.Add(new TagRead(point.Tag.Id, TagValue.Good(Decode(point, registers, bits), now)));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or NotSupportedException)
            {
                reads.Add(new TagRead(point.Tag.Id, TagValue.Bad($"Decode failed: {ex.Message}", now)));
            }
        }

        return reads;

        void Fail(ModbusReadBlock block, string reason)
        {
            foreach (var point in block.Points)
                failures[point.Tag.Id] = reason;
        }
    }

    private object Decode(ModbusPoint point, Dictionary<ushort, ushort> registers, Dictionary<ushort, bool> bits)
    {
        switch (point.Addr.Type)
        {
            case ModbusRegisterType.Coil:
            case ModbusRegisterType.DiscreteInput:
                return bits[point.Addr.Address];

            default:
                if (point.Addr.Bit is { } bit)
                    return ModbusCodec.DecodeBit(registers[point.Addr.Address], bit, _byteSwap);

                var words = new ushort[point.RegisterCount];
                for (var i = 0; i < words.Length; i++)
                    words[i] = registers[(ushort)(point.Addr.Address + i)];
                return ModbusCodec.DecodeRegisters(point.Tag.DataType, words, _wordSwap, _byteSwap);
        }
    }

    /// <summary>Swap a refused block for its halves, keeping the plan's order stable.</summary>
    private void ReplaceBlock(ModbusReadBlock block, List<ModbusReadBlock> halves)
    {
        var current = Volatile.Read(ref _blocks);
        var index = Array.IndexOf(current, block);
        if (index < 0)
            return;
        var replacement = new ModbusReadBlock[current.Length - 1 + halves.Count];
        Array.Copy(current, 0, replacement, 0, index);
        halves.CopyTo(replacement, index);
        Array.Copy(current, index + 1, replacement, index + halves.Count, current.Length - index - 1);
        Volatile.Write(ref _blocks, replacement);
    }

    public async Task WriteAsync(string tagId, object value, CancellationToken ct)
    {
        // Whether a point is writable at all is a property of the configuration, so it is answered
        // before the link state — "you can never write this" beats "the device is offline".
        if (!_pointsByTag.TryGetValue(tagId, out var pt))
            throw new KeyNotFoundException($"Unknown tag '{tagId}'.");

        if (pt.Addr.Bit is not null)
        {
            // Writing one bit of a register means read-modify-write, which would race the PLC's own
            // updates to the other 15 bits. Refuse rather than corrupt a status word.
            throw new NotSupportedException(
                $"Tag '{tagId}' addresses a single bit inside a register, which cannot be written safely. " +
                "Write the whole register, or use a coil.");
        }

        if (pt.Addr.Type is not (ModbusRegisterType.Coil or ModbusRegisterType.HoldingRegister))
            throw new NotSupportedException($"Tag '{tagId}' maps to a read-only Modbus area ({pt.Addr.Type}).");

        if (_master is null || _tcp is null)
            throw new InvalidOperationException("Modbus master not connected.");

        ct.ThrowIfCancellationRequested();
        // NModbus 3.x does not expose cancellation tokens for writes. Closing the socket is the
        // only reliable way to stop an in-flight command at its caller's deadline; the next poll
        // then observes the broken transport and lets DeviceWorker reconnect it.
        using var cancellation = ct.Register(static state => ((TcpClient)state!).Dispose(), _tcp);
        try
        {
            switch (pt.Addr.Type)
            {
                case ModbusRegisterType.Coil:
                    await _master.WriteSingleCoilAsync(_unitId, pt.Addr.Address, ModbusCodec.ToBool(value));
                    break;
                case ModbusRegisterType.HoldingRegister:
                    var regs = ModbusCodec.EncodeRegisters(pt.Tag.DataType, value, _wordSwap, _byteSwap);
                    if (regs.Length == 1)
                        await _master.WriteSingleRegisterAsync(_unitId, pt.Addr.Address, regs[0]);
                    else
                        await _master.WriteMultipleRegistersAsync(_unitId, pt.Addr.Address, regs);
                    break;
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("Modbus write was cancelled; the connection was closed.", ex, ct);
        }
    }

    public bool IsTransportFault(Exception exception) => TransportFaults.IsConnectionGone(exception);

    public Task DisconnectAsync(CancellationToken ct)
    {
        _master?.Dispose();
        _master = null;
        _tcp?.Close();
        _tcp?.Dispose();
        _tcp = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Close the socket out from under whatever NModbus is doing on the poll thread.
    ///
    /// <para>
    /// NModbus reads with <c>Transport.ReadTimeout</c> and no cancellation token, so a poll aimed at
    /// a PLC that stopped answering is unreachable from the engine for <c>TimeoutMs × (Retries + 1)</c>.
    /// Closing the handle makes that pending read fail at once; the exception surfaces out of
    /// <see cref="PollAsync"/>, the loop sees its cancelled token and exits. Without this a reload
    /// against a black-holed device left an abandoned worker and cost a process restart.
    /// </para>
    ///
    /// <para>
    /// Unlike every other member this runs on a different thread from the poll, so the fields are
    /// read once into locals: <see cref="DisconnectAsync"/> may be nulling them at the same moment,
    /// and disposing an already-disposed socket is fine while dereferencing a field that just became
    /// null is not. Anything thrown is swallowed — this is the shutdown path, and there is nothing
    /// left to report it to.
    /// </para>
    /// </summary>
    public Task AbortAsync()
    {
        var socket = _tcp;
        try { socket?.Close(); } catch { }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _master?.Dispose();
        _tcp?.Dispose();
        return ValueTask.CompletedTask;
    }
}
