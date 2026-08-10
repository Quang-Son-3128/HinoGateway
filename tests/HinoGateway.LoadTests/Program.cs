using HinoGateway.LoadTests;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "list")
{
    Console.WriteLine("usage: HinoGateway.LoadTests <profile> [--devices N] [--tags N] [--scan MS] [--seconds N] [--latency MS] [--minthreads N]");
    Console.WriteLine("profiles:");
    foreach (var p in LoadProfile.All) Console.WriteLine($"  {p.Name,-20} {p.Description}");
    return args.Length == 0 ? 1 : 0;
}

var profile = LoadProfile.ByName(args[0]);
if (profile is null)
{
    Console.Error.WriteLine($"unknown profile '{args[0]}'");
    return 1;
}
for (var i = 1; i < args.Length - 1; i += 2)
{
    var value = args[i + 1];
    profile = args[i] switch
    {
        "--devices" => profile with { Devices = int.Parse(value) },
        "--tags" => profile with { TagsPerDevice = int.Parse(value) },
        "--scan" => profile with { ScanMs = int.Parse(value) },
        "--seconds" => profile with { RunSeconds = int.Parse(value) },
        "--latency" => profile with { LinkLatencyMs = int.Parse(value) },
        "--minthreads" => profile with { MinThreads = int.Parse(value) },
        _ => throw new ArgumentException($"unknown option '{args[i]}'"),
    };
}

Console.WriteLine($"profile '{profile.Name}': devices={profile.Devices}, tags={profile.TagsPerDevice}, scan={profile.ScanMs}ms, run={profile.RunSeconds}s");
var result = await LoadRunner.RunAsync(profile);
Console.WriteLine($"{ "slave",-12} {"devs",5} {"connected",10} {"polls/dev",10} {"good",8} {"bad",8} {"missing",8}");
foreach (var s in result.Scenarios)
    Console.WriteLine($"{s.Kind,-12} {s.Devices,5} {s.Connected,10} {s.PollsPerDevice,10:F1} {s.GoodTags,8} {s.BadTags,8} {s.MissingTags,8}");
Console.WriteLine($"elapsed={result.ElapsedSeconds:F1}s cpu={result.CpuSeconds:F1}s rss={result.WorkingSetMb}MB threads={result.OsThreads}");
Console.WriteLine($"northbound batches={result.NorthboundBatches} samples={result.NorthboundSamples} dropped={result.NorthboundDropped}");

var reachable = result.Scenarios.Where(s => s.Kind is SlaveKind.Healthy or SlaveKind.Slow).ToList();
var connected = reachable.Sum(s => s.Connected) == reachable.Sum(s => s.Devices);
var pollRatio = reachable.Count == 0 || reachable.Min(s => s.PollsPerDevice) / result.ExpectedPolls >= profile.MinPollRatio;
var clean = result.NorthboundDropped == 0 && result.DiagnosticsFaults == 0 && result.ObserverErrors == 0;
var pass = connected && pollRatio && clean;
Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
return pass ? 0 : 1;
