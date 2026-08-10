namespace HinoGateway.LoadTests;

public sealed record LoadProfile
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public int Devices { get; init; } = 100;
    public int TagsPerDevice { get; init; } = 100;
    public int ScanMs { get; init; } = 1000;
    public int RunSeconds { get; init; } = 60;
    public int LinkLatencyMs { get; init; }
    public bool MixedFaults { get; init; }
    public bool FullStack { get; init; }
    public int MinThreads { get; init; }
    public double MinPollRatio { get; init; } = 0.85;
    public double MaxSampleLatenessSeconds { get; init; } = 2.0;

    public static readonly LoadProfile Mixed = new()
    {
        Name = "mixed", Description = "100 connections x 100 tags with blackhole/refused/bad-address devices",
        Devices = 100, TagsPerDevice = 100, MixedFaults = true,
    };
    public static readonly LoadProfile SlowLink = new()
    {
        Name = "slowlink", Description = "150 connections x 50 tags behind a 200 ms link",
        Devices = 150, TagsPerDevice = 50, LinkLatencyMs = 200,
    };
    public static readonly LoadProfile SlowLinkMinThreads = SlowLink with
    {
        Name = "slowlink-minthreads", Description = "slowlink + ThreadPool.SetMinThreads(400)", MinThreads = 400,
    };
    public static readonly LoadProfile Quick = new()
    {
        Name = "quick", Description = "20 connections x 20 tags for 15 s behind a 200 ms link",
        Devices = 20, TagsPerDevice = 20, RunSeconds = 15, LinkLatencyMs = 200,
    };
    public static readonly LoadProfile FullStackProfile = new()
    {
        Name = "fullstack", Description = "100 x 100 plus independent northbound/diagnostics consumers",
        Devices = 100, TagsPerDevice = 100, FullStack = true,
    };

    public static readonly IReadOnlyList<LoadProfile> All =
        [Quick, Mixed, SlowLink, SlowLinkMinThreads, FullStackProfile];

    public static LoadProfile? ByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<SlaveKind> BuildPopulation()
    {
        var kinds = new SlaveKind[Devices];
        for (var i = 0; i < Devices; i++)
            kinds[i] = !MixedFaults
                ? (LinkLatencyMs > 0 ? SlaveKind.Slow : SlaveKind.Healthy)
                : (i % 25) switch
                {
                    7 => SlaveKind.BlackHole,
                    11 => SlaveKind.Refused,
                    15 => SlaveKind.BadAddress,
                    19 => SlaveKind.Slow,
                    _ => SlaveKind.Healthy,
                };
        return kinds;
    }

    public int ExpectedGoodPerBadAddressDevice =>
        Math.Min(TagsPerDevice, (SlaveFarm.BadAddressRegisterCount + 1) / 2);
}
