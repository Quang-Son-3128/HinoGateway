using System.Diagnostics;
using System.Net.Sockets;
using HinoGateway.Core;
using S7.Net;
using S7DataType = S7.Net.DataType;

namespace HinoGateway.Drivers.SiemensS7;

/// <summary>S7 ISO-on-TCP driver for S7-300/400/1200/1500 via S7netplus.</summary>
public sealed class SiemensS7Driver : IDeviceDriver, IDriverDiagnostics
{
    private readonly DeviceConfig _config;
    private readonly Plc _plc;
    private readonly List<S7Point> _points;
    private readonly Dictionary<string, S7Point> _byTag;
    private readonly int _maxGapBytes;
    private readonly int _maxBlockBytes;
    private readonly int _pollDeadlineMs;
    private readonly int _quarantineMs;
    private volatile S7ReadBlock[] _blocks;

    public SiemensS7Driver(DeviceConfig config)
    {
        _config = config;
        var cpu = Enum.Parse<CpuType>(Get("CpuType", "S71200"), true);
        var host = Get("Host", "127.0.0.1");
        var port = GetInt("Port", 102);
        var rack = checked((short)GetInt("Rack", 0));
        var slot = checked((short)GetInt("Slot", cpu is CpuType.S7300 or CpuType.S7400 ? 2 : 1));
        _plc = new Plc(cpu, host, port, rack, slot);
        _plc.ReadTimeout = GetInt("TimeoutMs", 3000);
        _plc.WriteTimeout = GetInt("TimeoutMs", 3000);
        _maxGapBytes = Math.Max(0, GetInt("MaxGapBytes", 8));
        _maxBlockBytes = Math.Clamp(GetInt("MaxBlockBytes", 200), 16, 960);
        _pollDeadlineMs = Math.Max(100, GetInt("PollDeadlineMs", 5000));
        _quarantineMs = Math.Max(1000, GetInt("QuarantineMs", 60000));

        _points = config.Tags.Select(t =>
        {
            var address = S7Address.Parse(t.Address);
            return new S7Point(t, address, address.Bit is null ? S7Codec.SizeOf(t.DataType) : 1);
        }).ToList();
        _byTag = _points.ToDictionary(p => p.Tag.Id, StringComparer.Ordinal);
        _blocks = [.. S7ReadPlan.Build(_points, _maxGapBytes, _maxBlockBytes)];
    }

    public IReadOnlyList<DriverBlockStat> Blocks
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            var blocks = _blocks;
            return blocks.Select(b => b.ToStat(now)).ToList();
        }
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _plc.OpenAsync(ct);
        if (!_plc.IsConnected)
            throw new InvalidOperationException("S7 ISO-on-TCP connection did not reach connected state.");
        _blocks = [.. S7ReadPlan.Build(_points, _maxGapBytes, _maxBlockBytes)];
    }

    public async Task<IReadOnlyList<TagRead>> PollAsync(CancellationToken ct)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("S7 PLC is not connected.");

        var now = DateTimeOffset.UtcNow;
        var data = new Dictionary<(S7Area Area, int Db, int Offset), byte>();
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        var notDue = new HashSet<string>(StringComparer.Ordinal);
        var budget = Stopwatch.StartNew();
        var queue = new Queue<S7ReadBlock>(_blocks);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var block = queue.Dequeue();

            if (!block.IsDue(now))
            {
                foreach (var point in block.Points) notDue.Add(point.Tag.Id);
                continue;
            }
            if (block.IsQuarantined(now))
            {
                Fail(block, block.LastError ?? "Address refused by the PLC; retrying later.");
                continue;
            }
            if (budget.ElapsedMilliseconds > _pollDeadlineMs)
            {
                Fail(block, $"Poll deadline of {_pollDeadlineMs} ms exceeded before this block was read.");
                continue;
            }

            var started = budget.Elapsed;
            try
            {
                var bytes = await _plc.ReadBytesAsync(ToLibraryArea(block.Area), block.DbNumber,
                    block.Start, block.Count, ct);
                for (var i = 0; i < bytes.Length; i++)
                    data[(block.Area, block.DbNumber, block.Start + i)] = bytes[i];
                block.RecordSuccess((budget.Elapsed - started).TotalMilliseconds);
                block.MarkPolled(now);
            }
            catch (Exception ex) when (IsAddressRefusal(ex))
            {
                var reason = $"PLC rejected {S7ReadBlock.AreaName(block.Area, block.DbNumber)} " +
                             $"bytes {block.Start}+{block.Count}: {ex.Message}";
                block.RecordError(reason);
                var halves = S7ReadPlan.Split(block);
                if (halves.Count == 0)
                {
                    block.Quarantine(now.AddMilliseconds(_quarantineMs), reason);
                    Fail(block, reason);
                }
                else
                {
                    ReplaceBlock(block, halves);
                    foreach (var half in halves) queue.Enqueue(half);
                }
            }
            catch (Exception ex)
            {
                block.RecordError(ex.Message);
                throw;
            }
        }

        var reads = new List<TagRead>(_points.Count);
        foreach (var point in _points)
        {
            if (notDue.Contains(point.Tag.Id)) continue;
            if (failures.TryGetValue(point.Tag.Id, out var failure))
            {
                reads.Add(new TagRead(point.Tag.Id, TagValue.Bad(failure, now)));
                continue;
            }
            try
            {
                reads.Add(new TagRead(point.Tag.Id, TagValue.Good(Decode(point, data), now)));
            }
            catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or NotSupportedException)
            {
                reads.Add(new TagRead(point.Tag.Id, TagValue.Bad($"S7 decode failed: {ex.Message}", now)));
            }
        }
        return reads;

        void Fail(S7ReadBlock block, string reason)
        {
            foreach (var point in block.Points) failures[point.Tag.Id] = reason;
        }
    }

    public async Task WriteAsync(string tagId, object value, CancellationToken ct)
    {
        if (!_byTag.TryGetValue(tagId, out var point)) throw new KeyNotFoundException($"Unknown S7 tag '{tagId}'.");
        if (point.Address.Area == S7Area.Input)
            throw new NotSupportedException($"S7 input tag '{tagId}' is read-only.");
        if (!_plc.IsConnected) throw new InvalidOperationException("S7 PLC is not connected.");
        var area = ToLibraryArea(point.Address.Area);
        if (point.Address.Bit is { } bit)
        {
            await _plc.WriteBitAsync(area, point.Address.DbNumber, point.Address.ByteOffset, bit,
                Convert.ToBoolean(value), ct);
            return;
        }
        await _plc.WriteBytesAsync(area, point.Address.DbNumber, point.Address.ByteOffset,
            S7Codec.Encode(point.Tag.DataType, value), ct);
    }

    public bool IsTransportFault(Exception exception)
    {
        if (IsAddressRefusal(exception)) return false;
        if (TransportFaults.IsConnectionGone(exception)) return true;
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is PlcException { ErrorCode: ErrorCode.ConnectionError }) return true;
        return !_plc.IsConnected;
    }

    public Task DisconnectAsync(CancellationToken ct) { _plc.Close(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _plc.Close(); return ValueTask.CompletedTask; }

    private object Decode(S7Point point, Dictionary<(S7Area, int, int), byte> data)
    {
        var bytes = new byte[point.Size];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = data[(point.Address.Area, point.Address.DbNumber, point.Start + i)];
        return S7Codec.Decode(point.Tag.DataType, bytes, point.Address.Bit);
    }

    private bool IsAddressRefusal(Exception ex) => IsAddressRefusal(ex, _plc.IsConnected);

    internal static bool IsAddressRefusal(Exception ex, bool plcConnected)
    {
        if (!plcConnected) return false;
        if (ex is PlcException or OperationCanceledException or TimeoutException
            or SocketException or IOException or ObjectDisposedException) return false;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            if (inner is PlcException or OperationCanceledException or TimeoutException
                or SocketException or IOException or ObjectDisposedException) return false;

        var message = ex.Message;
        return message.Contains("object does not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("address out of range", StringComparison.OrdinalIgnoreCase)
            || message.Contains("address is out of range", StringComparison.OrdinalIgnoreCase)
            || message.Contains("requested address", StringComparison.OrdinalIgnoreCase)
            || message.Contains("data block does not exist", StringComparison.OrdinalIgnoreCase)
            || message.Contains("db does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private void ReplaceBlock(S7ReadBlock block, List<S7ReadBlock> halves)
    {
        var current = _blocks;
        var index = Array.IndexOf(current, block);
        if (index < 0) return;
        var updated = new S7ReadBlock[current.Length - 1 + halves.Count];
        Array.Copy(current, 0, updated, 0, index);
        halves.CopyTo(updated, index);
        Array.Copy(current, index + 1, updated, index + halves.Count, current.Length - index - 1);
        _blocks = updated;
    }

    private string Get(string key, string fallback) =>
        _config.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
    private int GetInt(string key, int fallback) => int.TryParse(Get(key, ""), out var value) ? value : fallback;
    private static S7DataType ToLibraryArea(S7Area area) => area switch
    {
        S7Area.Input => S7DataType.Input,
        S7Area.Output => S7DataType.Output,
        S7Area.Merker => S7DataType.Memory,
        _ => S7DataType.DataBlock,
    };
}
