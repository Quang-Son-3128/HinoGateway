using HinoGateway.Core;

namespace HinoGateway.Drivers.Modbus;

/// <summary>One configured tag resolved to a Modbus location.</summary>
public sealed record ModbusPoint(TagDefinition Tag, ModbusAddress Addr, int RegisterCount)
{
    /// <summary>First address the point needs (inclusive).</summary>
    public int Start => Addr.Address;

    /// <summary>One past the last address the point needs.</summary>
    public int End => Addr.Address + RegisterCount;
}

/// <summary>
/// One physical read request: a contiguous address range plus the points it serves.
///
/// Mutable on purpose — a block that the device refuses is split into smaller ones and, once it is
/// down to a single point, quarantined for a while. The driver's access is already serialised with
/// its poll loop, so no extra locking is needed here.
/// </summary>
public sealed class ModbusReadBlock
{
    public ModbusReadBlock(ModbusRegisterType area, ushort start, ushort count, List<ModbusPoint> points)
    {
        Area = area;
        Start = start;
        Count = count;
        Points = points;
    }

    public ModbusRegisterType Area { get; }
    public ushort Start { get; }
    public ushort Count { get; }
    public List<ModbusPoint> Points { get; }

    public long Reads { get; private set; }
    public long Errors { get; private set; }
    public double LastLatencyMs { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? QuarantinedUntil { get; private set; }

    public bool IsQuarantined(DateTimeOffset now) => QuarantinedUntil is { } until && now < until;

    public void RecordSuccess(double latencyMs)
    {
        Reads++;
        LastLatencyMs = latencyMs;
        LastError = null;
        QuarantinedUntil = null;
    }

    public void RecordError(string error)
    {
        Errors++;
        LastError = error;
    }

    /// <summary>Stop asking for this range until <paramref name="until"/> — the device refuses it.</summary>
    public void Quarantine(DateTimeOffset until, string reason)
    {
        QuarantinedUntil = until;
        LastError = reason;
    }

    public DriverBlockStat ToStat(DateTimeOffset now) => new(
        Area: AreaName(Area),
        Start: Start,
        Count: Count,
        Tags: Points.Count,
        Reads: Reads,
        Errors: Errors,
        LastLatencyMs: LastLatencyMs,
        LastError: LastError,
        Quarantined: IsQuarantined(now));

    public static string AreaName(ModbusRegisterType area) => area switch
    {
        ModbusRegisterType.HoldingRegister => "Holding registers",
        ModbusRegisterType.InputRegister => "Input registers",
        ModbusRegisterType.Coil => "Coils",
        _ => "Discrete inputs",
    };
}

/// <summary>
/// Turns a device's tag list into the set of requests to issue.
///
/// The naive approach — read from the lowest to the highest address of each area — is what most
/// quick implementations do, and it collapses on real devices: two tags at HR:0 and HR:10000 become
/// ~81 maximum-size requests every scan, most of them fetching registers nobody asked for, and any
/// unsupported address in between poisons the lot. So points are sorted and merged into blocks only
/// while the gap between them stays under a threshold: crossing a small gap is cheaper than paying
/// for another round trip, while a large gap earns its own request.
/// </summary>
public static class ModbusReadPlan
{
    /// <summary>Protocol ceiling for one request (FC 03/04 return at most 125 registers).</summary>
    public const int MaxRegistersPerRead = 125;

    /// <summary>Protocol ceiling for one bit request (FC 01/02).</summary>
    public const int MaxBitsPerRead = 2000;

    public static List<ModbusReadBlock> Build(IEnumerable<ModbusPoint> points, int maxRegisterGap, int maxBitGap)
    {
        var blocks = new List<ModbusReadBlock>();
        foreach (var area in new[]
                 {
                     ModbusRegisterType.HoldingRegister,
                     ModbusRegisterType.InputRegister,
                     ModbusRegisterType.Coil,
                     ModbusRegisterType.DiscreteInput,
                 })
        {
            var bits = area is ModbusRegisterType.Coil or ModbusRegisterType.DiscreteInput;
            blocks.AddRange(BuildArea(
                points.Where(p => p.Addr.Type == area),
                area,
                maxGap: bits ? maxBitGap : maxRegisterGap,
                maxSpan: bits ? MaxBitsPerRead : MaxRegistersPerRead));
        }
        return blocks;
    }

    private static List<ModbusReadBlock> BuildArea(IEnumerable<ModbusPoint> areaPoints,
        ModbusRegisterType area, int maxGap, int maxSpan)
    {
        var blocks = new List<ModbusReadBlock>();
        var ordered = areaPoints.OrderBy(p => p.Start).ThenBy(p => p.End).ToList();
        if (ordered.Count == 0)
            return blocks;

        var current = new List<ModbusPoint> { ordered[0] };
        var start = ordered[0].Start;
        var end = ordered[0].End;

        foreach (var point in ordered.Skip(1))
        {
            var gap = point.Start - end;           // negative when points overlap
            var span = point.End - start;

            if (gap > maxGap || span > maxSpan)
            {
                blocks.Add(Close(area, start, end, current));
                current = new List<ModbusPoint> { point };
                start = point.Start;
                end = point.End;
                continue;
            }

            current.Add(point);
            end = Math.Max(end, point.End);
        }

        blocks.Add(Close(area, start, end, current));
        return blocks;
    }

    private static ModbusReadBlock Close(ModbusRegisterType area, int start, int end, List<ModbusPoint> points) =>
        new(area, (ushort)start, (ushort)(end - start), points);

    /// <summary>
    /// Split a refused block in two so the next attempt narrows down which point the device dislikes.
    /// Halving by point count converges in log2(n) polls and needs no knowledge of why it was refused.
    /// Returns an empty list for a single-point block — there is nothing left to divide.
    /// </summary>
    public static List<ModbusReadBlock> Split(ModbusReadBlock block)
    {
        if (block.Points.Count < 2)
            return new List<ModbusReadBlock>();

        var half = block.Points.Count / 2;
        return new List<ModbusReadBlock>
        {
            Tighten(block.Area, block.Points.Take(half).ToList()),
            Tighten(block.Area, block.Points.Skip(half).ToList()),
        };
    }

    /// <summary>A block covering exactly the given points, with no slack at either end.</summary>
    private static ModbusReadBlock Tighten(ModbusRegisterType area, List<ModbusPoint> points)
    {
        var start = points.Min(p => p.Start);
        var end = points.Max(p => p.End);
        return new ModbusReadBlock(area, (ushort)start, (ushort)(end - start), points);
    }
}
