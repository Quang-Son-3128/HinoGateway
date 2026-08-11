using System.Diagnostics;
using HinoGateway.Core;
using HinoGateway.Drivers.Modbus;
using HinoGateway.Engine;
using Microsoft.Extensions.Logging;

namespace HinoGateway.LoadTests;

public sealed record ScenarioStats(SlaveKind Kind, int Devices, int Connected, double PollsPerDevice,
    int GoodTags, int BadTags, int MissingTags);

public sealed record ReloadStats(int Count, long LastMs, long MinMs, long MaxMs, long P95Ms,
    IReadOnlyList<long> ManagedHeapCheckpointsMb);

public sealed record LoadResult(
    LoadProfile Profile, IReadOnlyList<ScenarioStats> Scenarios, double ExpectedPolls,
    double ElapsedSeconds, double MeasuredSeconds, double CpuSeconds, long WorkingSetMb,
    int OsThreads, long SlaveWorkingSetMb, int SlaveThreads,
    long PeakWorkingSetMb, int Gen0Collections, int Gen1Collections, int Gen2Collections, long AllocatedMb,
    long ManagedHeapMb, long PeakManagedHeapMb, long ManagedGrowthMb, long FragmentedHeapMb,
    long PrivateMemoryMb, int HandleCount, ReloadStats Reloads,
    int PeakPoolBusy, double MaxSampleLatenessSeconds, long StartupMs, long HotReloadMs,
    int Rejected, int DroppedTags, long NorthboundBatches, long NorthboundSamples, long NorthboundDropped,
    long DiagnosticsCalls, long DiagnosticsFaults, string? DiagnosticsLastFault, long ObserverErrors,
    FullStackStats? FullStack)
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
        await using var farm = await ExternalSlaveFarm.StartAsync(population, profile.LinkLatencyMs);
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
        var managedBaseline = GC.GetTotalMemory(forceFullCollection: true);
        var peakManaged = managedBaseline;
        var cpu0 = proc.TotalProcessorTime;
        var gc0 = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocated0 = GC.GetTotalAllocatedBytes();
        var peakWorkingSet = proc.WorkingSet64;
        var total = Stopwatch.StartNew();
        var startup = Stopwatch.StartNew();
        var apply = await engine.LoadAsync(configs);
        FullStackProbe? fullStack = null;
        if (profile.FullStack)
            fullStack = await FullStackProbe.StartAsync(engine, store, configs, loggerFactory);
        engine.StartAll();
        startup.Stop();

        var peakPoolBusy = 0;
        var maxLateness = 0d;
        var samplePeriod = TimeSpan.FromSeconds(1);
        var nextSample = Stopwatch.GetTimestamp() + (long)(samplePeriod.TotalSeconds * Stopwatch.Frequency);
        var endAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, profile.RunSeconds));
        var reloadAt = DateTimeOffset.UtcNow.AddSeconds(profile.ReloadEverySeconds > 0
            ? profile.ReloadEverySeconds : Math.Max(1, profile.RunSeconds) / 2d);
        var hotReloadMs = 0L;
        var reloadDurations = new List<long>();
        var managedCheckpoints = new List<long>();
        var pollsBeforeReload = new Dictionary<string, long>(StringComparer.Ordinal);
        while (DateTimeOffset.UtcNow < endAt)
        {
            await Task.Delay(samplePeriod);
            var nowTicks = Stopwatch.GetTimestamp();
            maxLateness = Math.Max(maxLateness, Math.Max(0, (nowTicks - nextSample) / (double)Stopwatch.Frequency));
            nextSample = nowTicks + (long)(samplePeriod.TotalSeconds * Stopwatch.Frequency);
            ThreadPool.GetAvailableThreads(out var available, out _);
            ThreadPool.GetMaxThreads(out var max, out _);
            peakPoolBusy = Math.Max(peakPoolBusy, max - available);
            proc.Refresh();
            peakWorkingSet = Math.Max(peakWorkingSet, proc.WorkingSet64);
            peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(forceFullCollection: false));
            if ((profile.FullStack || profile.ReloadEverySeconds > 0) && DateTimeOffset.UtcNow >= reloadAt)
            {
                foreach (var device in engine.Health)
                    pollsBeforeReload[device.DeviceId] = pollsBeforeReload.GetValueOrDefault(device.DeviceId) + device.PollCount;
                var reload = Stopwatch.StartNew();
                await engine.ReloadAsync(configs);
                reload.Stop();
                hotReloadMs = Math.Max(1, reload.ElapsedMilliseconds);
                reloadDurations.Add(Math.Max(1, reload.ElapsedMilliseconds));
                var forceGc = profile.ForcedGcEveryReloads > 0
                              && reloadDurations.Count % profile.ForcedGcEveryReloads == 0;
                var checkpoint = GC.GetTotalMemory(forceGc);
                peakManaged = Math.Max(peakManaged, checkpoint);
                managedCheckpoints.Add(checkpoint / 1024 / 1024);
                reloadAt = profile.ReloadEverySeconds > 0
                    ? DateTimeOffset.UtcNow.AddSeconds(profile.ReloadEverySeconds)
                    : DateTimeOffset.MaxValue;
            }
        }

        // A sample taken on the same millisecond as a hot reload only measures how fast the status
        // endpoint can observe Connecting. Give reachable links a bounded recovery window and then
        // judge their steady state; failure to recover inside it still fails the profile.
        var reachableIds = population.Select((kind, index) => (kind, id: $"load-{index:D4}"))
            .Where(x => x.kind is SlaveKind.Healthy or SlaveKind.Slow)
            .Select(x => x.id).ToHashSet(StringComparer.Ordinal);
        var recoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < recoveryDeadline
               && engine.Health.Where(h => reachableIds.Contains(h.DeviceId)).Any(h => h.State != DeviceState.Connected))
            await Task.Delay(100);

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
                    deviceHealth.Count == 0 ? 0 : deviceHealth.Average(h => h.PollCount
                        + pollsBeforeReload.GetValueOrDefault(h.DeviceId)),
                    present.Count(id => values[id].Quality == Quality.Good),
                    present.Count(id => values[id].Quality == Quality.Bad),
                    tagIds.Count - present.Count);
            }).OrderBy(s => s.Kind).ToList();

        proc.Refresh();
        var finalManaged = GC.GetTotalMemory(forceFullCollection: reloadDurations.Count > 0);
        peakManaged = Math.Max(peakManaged, finalManaged);
        var gcInfo = GC.GetGCMemoryInfo();
        var orderedReloads = reloadDurations.OrderBy(x => x).ToArray();
        var p95Index = orderedReloads.Length == 0 ? 0
            : Math.Clamp((int)Math.Ceiling(orderedReloads.Length * 0.95) - 1, 0, orderedReloads.Length - 1);
        var reloadStats = new ReloadStats(orderedReloads.Length,
            reloadDurations.LastOrDefault(), orderedReloads.FirstOrDefault(), orderedReloads.LastOrDefault(),
            orderedReloads.Length == 0 ? 0 : orderedReloads[p95Index], managedCheckpoints);
        var cpuSeconds = (proc.TotalProcessorTime - cpu0).TotalSeconds;
        var fullStackStats = fullStack?.Snapshot();
        if (fullStack is not null)
            await fullStack.DisposeAsync();
        return new LoadResult(profile, scenarios, profile.RunSeconds * 1000d / profile.ScanMs,
            total.Elapsed.TotalSeconds, profile.RunSeconds, cpuSeconds, proc.WorkingSet64 / 1024 / 1024,
            proc.Threads.Count, farm.WorkingSetMb, farm.Threads,
            peakWorkingSet / 1024 / 1024, GC.CollectionCount(0) - gc0[0], GC.CollectionCount(1) - gc0[1],
            GC.CollectionCount(2) - gc0[2], (GC.GetTotalAllocatedBytes() - allocated0) / 1024 / 1024,
            finalManaged / 1024 / 1024, peakManaged / 1024 / 1024,
            (finalManaged - managedBaseline) / 1024 / 1024, gcInfo.FragmentedBytes / 1024 / 1024,
            proc.PrivateMemorySize64 / 1024 / 1024, SafeHandleCount(proc), reloadStats,
            peakPoolBusy, maxLateness, startup.ElapsedMilliseconds, hotReloadMs,
            apply.Rejected.Count, apply.DroppedTags.Count, northboundBatches, northboundSamples,
            northbound.Dropped, diagnosticsCalls, diagnosticsFaults, diagnosticsLastFault, store.ObserverErrors,
            fullStackStats);
    }

    private static int SafeHandleCount(Process process)
    {
        try { return process.HandleCount; }
        catch (PlatformNotSupportedException) { return -1; }
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
