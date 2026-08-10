using HinoGateway.Core;

namespace HinoGateway.Drivers.SiemensS7;

public sealed record S7Point(TagDefinition Tag, S7Address Address, int Size)
{
    public int Start => Address.ByteOffset;
    public int End => Address.ByteOffset + Size;
}

public sealed class S7ReadBlock
{
    public S7ReadBlock(S7Area area, int dbNumber, int start, int count, List<S7Point> points,
        TimeSpan? scanInterval = null)
    {
        Area = area;
        DbNumber = dbNumber;
        Start = start;
        Count = count;
        Points = points;
        ScanInterval = scanInterval;
    }

    public S7Area Area { get; }
    public int DbNumber { get; }
    public int Start { get; }
    public int Count { get; }
    public List<S7Point> Points { get; }
    public TimeSpan? ScanInterval { get; }
    public long Reads { get; private set; }
    public long Errors { get; private set; }
    public double LastLatencyMs { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? QuarantinedUntil { get; private set; }
    public DateTimeOffset? NextDueAt { get; private set; }

    public bool IsQuarantined(DateTimeOffset now) => QuarantinedUntil is { } until && now < until;
    public bool IsDue(DateTimeOffset now) => ScanInterval is null || NextDueAt is not { } due || now >= due;

    public void MarkPolled(DateTimeOffset now)
    {
        if (ScanInterval is { } interval) NextDueAt = now + interval;
    }

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

    public void Quarantine(DateTimeOffset until, string reason)
    {
        QuarantinedUntil = until;
        LastError = reason;
    }

    public DriverBlockStat ToStat(DateTimeOffset now) => new(
        AreaName(Area, DbNumber), Start, Count, Points.Count, Reads, Errors,
        LastLatencyMs, LastError, IsQuarantined(now))
    {
        ScanIntervalMs = ScanInterval is { } interval ? (int)interval.TotalMilliseconds : 0,
    };

    public static string AreaName(S7Area area, int db) => area switch
    {
        S7Area.DataBlock => $"DB{db}",
        S7Area.Input => "Inputs",
        S7Area.Output => "Outputs",
        _ => "Merker",
    };
}

public static class S7ReadPlan
{
    public static List<S7ReadBlock> Build(IEnumerable<S7Point> points, int maxGap, int maxBlockBytes)
    {
        var result = new List<S7ReadBlock>();
        foreach (var group in points.GroupBy(p => (p.Address.Area, p.Address.DbNumber, p.Tag.ScanInterval)))
        {
            var rate = group.Key.ScanInterval;
            var ordered = group.OrderBy(p => p.Start).ThenBy(p => p.End).ToList();
            if (ordered.Count == 0) continue;
            var current = new List<S7Point> { ordered[0] };
            var start = ordered[0].Start;
            var end = ordered[0].End;
            foreach (var point in ordered.Skip(1))
            {
                var nextEnd = Math.Max(end, point.End);
                if (point.Start - end > maxGap || nextEnd - start > maxBlockBytes)
                {
                    result.Add(new S7ReadBlock(group.Key.Area, group.Key.DbNumber, start, end - start, current, rate));
                    current = [point]; start = point.Start; end = point.End;
                }
                else { current.Add(point); end = nextEnd; }
            }
            result.Add(new S7ReadBlock(group.Key.Area, group.Key.DbNumber, start, end - start, current, rate));
        }
        return result;
    }

    public static List<S7ReadBlock> Split(S7ReadBlock block)
    {
        if (block.Points.Count < 2) return [];
        var half = block.Points.Count / 2;
        return [Tighten(block, block.Points.Take(half).ToList()), Tighten(block, block.Points.Skip(half).ToList())];
    }

    private static S7ReadBlock Tighten(S7ReadBlock source, List<S7Point> points)
    {
        var start = points.Min(p => p.Start);
        var end = points.Max(p => p.End);
        return new S7ReadBlock(source.Area, source.DbNumber, start, end - start, points, source.ScanInterval);
    }
}
