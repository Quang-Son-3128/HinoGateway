using HinoGateway.LoadTests;
using System.Text.Json;

if (args.Length > 0 && args[0] == "slave-farm-child")
{
    var kindsArg = args[Array.IndexOf(args, "--kinds") + 1];
    var latencyArg = args[Array.IndexOf(args, "--latency") + 1];
    var kinds = kindsArg.Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(value => (SlaveKind)int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
        .ToList();
    var latency = int.Parse(latencyArg, System.Globalization.CultureInfo.InvariantCulture);
    await using var childFarm = new SlaveFarm();
    childFarm.Start(kinds, latency);
    Console.WriteLine(JsonSerializer.Serialize(childFarm.Ports));
    Console.Out.Flush();
    await Console.In.ReadLineAsync();
    return 0;
}

if (args.Length == 0 || args[0] is "-h" or "--help" or "help" or "list")
{
    Console.WriteLine("usage: HinoGateway.LoadTests <profile> [--devices N] [--tags N] [--scan MS] [--seconds N] [--latency MS] [--minthreads N] [--reload-every S]");
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
        "--reload-every" => profile with { ReloadEverySeconds = int.Parse(value) },
        _ => throw new ArgumentException($"unknown option '{args[i]}'"),
    };
}

Console.WriteLine($"profile '{profile.Name}': devices={profile.Devices}, tags={profile.TagsPerDevice}, scan={profile.ScanMs}ms, run={profile.RunSeconds}s");
var result = await LoadRunner.RunAsync(profile);
Console.WriteLine($"{ "slave",-12} {"devs",5} {"connected",10} {"polls/dev",10} {"good",8} {"bad",8} {"missing",8}");
foreach (var s in result.Scenarios)
    Console.WriteLine($"{s.Kind,-12} {s.Devices,5} {s.Connected,10} {s.PollsPerDevice,10:F1} {s.GoodTags,8} {s.BadTags,8} {s.MissingTags,8}");
Console.WriteLine($"elapsed={result.ElapsedSeconds:F1}s cpu={result.CpuSeconds:F1}s gateway-rss={result.WorkingSetMb}MB gateway-threads={result.OsThreads}");
Console.WriteLine($"peak-rss={result.PeakWorkingSetMb}MB allocated={result.AllocatedMb}MB gc={result.Gen0Collections}/{result.Gen1Collections}/{result.Gen2Collections}");
Console.WriteLine($"managed={result.ManagedHeapMb}MB peak-managed={result.PeakManagedHeapMb}MB growth={result.ManagedGrowthMb}MB fragmented={result.FragmentedHeapMb}MB private={result.PrivateMemoryMb}MB handles={result.HandleCount}");
Console.WriteLine($"reloads={result.Reloads.Count} last/min/p95/max={result.Reloads.LastMs}/{result.Reloads.MinMs}/{result.Reloads.P95Ms}/{result.Reloads.MaxMs}ms managed-checkpoints=[{string.Join(',', result.Reloads.ManagedHeapCheckpointsMb)}]MB");
Console.WriteLine($"slave-farm-rss={result.SlaveWorkingSetMb}MB slave-farm-threads={result.SlaveThreads}");
Console.WriteLine($"northbound batches={result.NorthboundBatches} samples={result.NorthboundSamples} dropped={result.NorthboundDropped}");
if (result.FullStack is { } fs)
{
    Console.WriteLine($"signalr batches={fs.SignalRBatches} samples={fs.SignalRSamples}; http stats={fs.HttpStatsCalls} failures={fs.HttpStatsFailures}");
    Console.WriteLine($"alarms events={fs.AlarmEvents} dropped={fs.AlarmDropped}; mqtt pending={fs.MqttPendingBatches} input-drops={fs.MqttInputDrops} spool-drops={fs.MqttSpoolDrops}");
    Console.WriteLine($"iec104 received-points={fs.IecReceivedAsdus} spontaneous-points={fs.IecSpontaneousSent} source-drops={fs.IecDroppedUpdates} capacity-drops={fs.IecCapacityDrops}; hot-reload={result.HotReloadMs}ms");
}

var reachable = result.Scenarios.Where(s => s.Kind is SlaveKind.Healthy or SlaveKind.Slow).ToList();
var connected = reachable.Sum(s => s.Connected) == reachable.Sum(s => s.Devices);
var pollRatio = reachable.Count == 0 || reachable.Min(s => s.PollsPerDevice) / result.ExpectedPolls >= profile.MinPollRatio;
var clean = result.NorthboundDropped == 0 && result.DiagnosticsFaults == 0 && result.ObserverErrors == 0;
if (result.FullStack is { } full)
    clean &= full.SignalRBatches > 0 && full.SignalRSamples > 0
             && full.HttpStatsCalls > 0 && full.HttpStatsFailures == 0
             && full.AlarmEvents > 0 && full.AlarmDropped == 0
             && full.MqttPendingBatches > 0 && full.MqttInputDrops == 0
             && (profile.AllowMqttSpoolTrim || full.MqttSpoolDrops == 0)
             && full.IecReceivedAsdus > 0 && full.IecSpontaneousSent > 0
             && full.IecReceivedAsdus / (double)full.IecSpontaneousSent >= 0.8
             && full.IecDroppedUpdates == 0
             && full.IecCapacityDrops
                <= profile.MaxIecCapacityDropRatio * (full.IecSpontaneousSent + full.IecCapacityDrops)
             && result.HotReloadMs > 0;
var pass = connected && pollRatio && clean;
Console.WriteLine(pass ? "RESULT: PASS" : "RESULT: FAIL");
return pass ? 0 : 1;
