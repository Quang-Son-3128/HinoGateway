using System.Diagnostics;
using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;
using HinoGateway.Engine;
using Microsoft.Extensions.Logging;

namespace HinoGateway.LoadTests;

public sealed record ScenarioStats(SlaveKind Kind, int Devices, int Connected, double PollsPerDevice,
    int GoodTags, int BadTags, int MissingTags);

public sealed record LoadResult(
    LoadProfile Profile, IReadOnlyList<ScenarioStats> Scenarios, double ExpectedPolls,
    double ElapsedSeconds, double MeasuredSeconds, double CpuSeconds, long WorkingSetMb,
    int OsThreads, int PeakPoolBusy, double MaxSampleLatenessSeconds, long StartupMs, long HotReloadMs,
    int Rejected, int DroppedTags, long NorthboundBatches, long NorthboundSamples, long NorthboundDropped,
    long DiagnosticsCalls, long DiagnosticsFaults, string? DiagnosticsLastFault, long ObserverErrors)
{
    public double CpuPercentOfMachine => ElapsedSeconds <= 0 ? 0
        : CpuSeconds / ElapsedSeconds / Environment.ProcessorCount * 100;
    public ScenarioStats? this[SlaveKind kind] => Scenarios.FirstOrDefault(s => s.Kind == kind);
}

public static class LoadRunner
{
    public static async Task<LoadResult> RunAsync(LoadProfile profile)
    {
        if (profile.MinThreads > 0)
        {
            ThreadPool.GetMinThreads(out var workers, out var io);
            ThreadPool.SetMinThreads(Math.Max(workers, profile.MinThreads), io);
        }

        var population = profile.BuildPopulation();
        await using var farm = new SlaveFarm();
        farm.Start(population, profile.LinkLatencyMs);
        var configs = BuildConfigs(profile, population, farm.Ports);

        using var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var store = new TagStore(Math.Max(1024, profile.Devices * 4));
        await using var engine = new DeviceEngine([new ModbusTcpDriverFactory()], store, loggerFactory);
        using var northbound = store.Subscribe("load-northbound", Math.Max(1024, profile.Devices * 8), false);
        using var runCts = new CancellationTokenSource();
        long northboundBatches = 0, northboundSamples = 0;
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var batch in northbound.Reader.ReadAllAsync(runCts.Token))
                {
                    Interlocked.Increment(ref northboundBatches);
                    Interlocked.Add(ref northboundSamples, batch.Count);
                }
            }
            catch (OperationCanceledException) { }
        });

        long diagnosticsCalls = 0, diagnosticsFaults = 0;
        string? diagnosticsLastFault = null;
        var diagnostics = Task.Run(async () =>
        {
            while (!runCts.IsCancellationRequested)
            {
                try { _ = engine.Diagnostics.Sum(d => d.Blocks.Count); Interlocked.Increment(ref diagnosticsCalls); }
                catch (Exception ex) { Interlocked.Increment(ref diagnosticsFaults); diagnosticsLastFault = ex.Message; }
                try { await Task.Delay(100, runCts.Token); } catch (OperationCanceledException) { }
            }
        });

        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var total = Stopwatch.StartNew();
        var startup = Stopwatch.StartNew();
        var apply = await engine.LoadAsync(configs);
        engine.StartAll();
        startup.Stop();

        var peakPoolBusy = 0;
        var maxLateness = 0d;
        var samplePeriod = TimeSpan.FromSeconds(1);
        var nextSample = Stopwatch.GetTimestamp() + (long)(samplePeriod.TotalSeconds * Stopwatch.Frequency);
        var endAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, profile.RunSeconds));
        while (DateTimeOffset.UtcNow < endAt)
        {
            await Task.Delay(samplePeriod);
            var nowTicks = Stopwatch.GetTimestamp();
            maxLateness = Math.Max(maxLateness, Math.Max(0, (nowTicks - nextSample) / (double)Stopwatch.Frequency));
            nextSample = nowTicks + (long)(samplePeriod.TotalSeconds * Stopwatch.Frequency);
            ThreadPool.GetAvailableThreads(out var available, out _);
            ThreadPool.GetMaxThreads(out var max, out _);
            peakPoolBusy = Math.Max(peakPoolBusy, max - available);
        }

        total.Stop();
        runCts.Cancel();
        try { await Task.WhenAll(consumer, diagnostics); } catch (OperationCanceledException) { }

        var health = engine.Health;
        var values = store.Snapshot();
        var scenarios = population.Select((kind, index) => (kind, index))
            .GroupBy(x => x.kind)
            .Select(group =>
            {
                var deviceIds = group.Select(x => $"load-{x.index:D4}").ToHashSet(StringComparer.Ordinal);
                var deviceHealth = health.Where(h => deviceIds.Contains(h.DeviceId)).ToList();
                var tagIds = configs.Where(c => deviceIds.Contains(c.DeviceId)).SelectMany(c => c.Tags.Select(t => t.Id)).ToList();
                var present = tagIds.Where(values.ContainsKey).ToList();
                return new ScenarioStats(group.Key, deviceIds.Count,
                    deviceHealth.Count(h => h.State == DeviceState.Connected),
                    deviceHealth.Count == 0 ? 0 : deviceHealth.Average(h => h.PollCount),
                    present.Count(id => values[id].Quality == Quality.Good),
                    present.Count(id => values[id].Quality == Quality.Bad),
                    tagIds.Count - present.Count);
            }).OrderBy(s => s.Kind).ToList();

        proc.Refresh();
        var cpuSeconds = (proc.TotalProcessorTime - cpu0).TotalSeconds;
        return new LoadResult(profile, scenarios, profile.RunSeconds * 1000d / profile.ScanMs,
            total.Elapsed.TotalSeconds, profile.RunSeconds, cpuSeconds, proc.WorkingSet64 / 1024 / 1024,
            proc.Threads.Count, peakPoolBusy, maxLateness, startup.ElapsedMilliseconds, 0,
            apply.Rejected.Count, apply.DroppedTags.Count, northboundBatches, northboundSamples,
            northbound.Dropped, diagnosticsCalls, diagnosticsFaults, diagnosticsLastFault, store.ObserverErrors);
    }

    private static List<DeviceConfig> BuildConfigs(LoadProfile profile, IReadOnlyList<SlaveKind> population,
        IReadOnlyList<int> ports)
    {
        var result = new List<DeviceConfig>(profile.Devices);
        for (var i = 0; i < profile.Devices; i++)
        {
            var tags = Enumerable.Range(0, profile.TagsPerDevice).Select(t => new TagDefinition
            {
                Id = $"load-{i:D4}/tag-{t:D4}", Name = $"Tag {t}", Address = $"HR:{t * 2}", DataType = DataType.UInt32,
            }).ToList();
            result.Add(new DeviceConfig
            {
                DeviceId = $"load-{i:D4}", Driver = "ModbusTCP",
                ScanInterval = TimeSpan.FromMilliseconds(profile.ScanMs), FailureThreshold = 1,
                Settings = new Dictionary<string, string>
                {
                    ["Host"] = "127.0.0.1", ["Port"] = ports[i].ToString(), ["UnitId"] = "1",
                    ["TimeoutMs"] = Math.Clamp(profile.ScanMs / 2, 100, 1000).ToString(), ["Retries"] = "0",
                    ["PollDeadlineMs"] = Math.Max(100, profile.ScanMs).ToString(), ["QuarantineMs"] = "1000",
                },
                Tags = tags,
            });
        }
        return result;
    }
}
