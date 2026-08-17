using HinoGateway.Core;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The energy totals the utility accepts a plant on. Every test here drives the engine with an
/// injected clock and real files on disk, because the failures that matter are all about what
/// survives a process ending — and none of them can be observed in a single continuous run.
/// </summary>
public class AccumulatorEngineTests : IDisposable
{
    private const string Counter = "Inverter/Inv1Energy";
    private const string CounterKey = "t-inv1energy";

    private readonly string _dir;
    private readonly AccumulatorStateStore _state;
    private readonly JsonlSampleHistory _history;
    private readonly JsonlEnergyJournal _journal;

    public AccumulatorEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hino-energy", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _state = new AccumulatorStateStore(Path.Combine(_dir, "hino.energy.json"),
            NullLogger<AccumulatorStateStore>.Instance, TimeSpan.FromMilliseconds(1));
        _history = new JsonlSampleHistory(Path.Combine(_dir, "history"), new SampleHistoryOptions(),
            NullLogger<JsonlSampleHistory>.Instance);
        _journal = new JsonlEnergyJournal(Path.Combine(_dir, "energy"), new EnergyJournalOptions(),
            NullLogger<JsonlEnergyJournal>.Instance);
    }

    public void Dispose()
    {
        _state.Dispose();
        _history.Dispose();
        _journal.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // --- fixtures --------------------------------------------------------------

    private static DateTimeOffset At(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 8, day, hour, minute, second, TimeSpan.FromHours(7));

    private static TagDefinition Accumulator(string name, AccumulationPeriod period,
        double rollover = 0, double? maxRate = null) => new()
    {
        Id = $"Energy/{name}",
        Name = name,
        Kind = TagKind.Accumulator,
        Address = "",
        DataType = DataType.Float64,
        Unit = "kWh",
        SourceTagId = Counter,
        SourceTagKey = CounterKey,
        Period = period,
        RolloverAt = rollover,
        MaxRatePerHour = maxRate,
    };

    /// <summary>A clock the test moves by hand, so a day can pass in a microsecond.</summary>
    private sealed class TestClock(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; set; } = now;
        public DateTimeOffset Read() => Now;
    }

    private (AccumulatorEngine Engine, TagStore Tags, TestClock Clock) Start(
        DateTimeOffset now, params TagDefinition[] tags)
    {
        var store = new TagStore();
        return Start(store, now, tags);
    }

    private (AccumulatorEngine Engine, TagStore Tags, TestClock Clock) Start(
        TagStore store, DateTimeOffset now, params TagDefinition[] tags)
    {
        var clock = new TestClock(now);
        var engine = new AccumulatorEngine(store, NullLogger.Instance, _state, _history, _journal,
            clock.Read, new AccumulatorOptions { Tick = TimeSpan.FromSeconds(1) });
        engine.Load(tags);
        return (engine, store, clock);
    }

    private static void Publish(TagStore store, DateTimeOffset at, double value,
        Quality quality = Quality.Good) =>
        store.Apply([new TagRead(Counter, quality == Quality.Good
            ? TagValue.Good(value, at)
            : TagValue.Bad("device fault", at))]);

    private static TagValue Read(TagStore store, string tagId)
    {
        Assert.True(store.TryGet(tagId, out var value), $"{tagId} was never published");
        return value;
    }

    private Task Record(DateTimeOffset at, double value) =>
        _history.AppendAsync(new HistoryFrame(at,
            new Dictionary<string, HistorySample> { [Counter] = new(value, Quality.Good) }));

    // --- tests -----------------------------------------------------------------

    /// <summary>
    /// The core promise: after a restart, yesterday's figure is the exact difference between the two
    /// midnight readings, and it is <em>Good</em> — not "roughly right once the device answers".
    /// </summary>
    [Fact]
    public void Yesterday_is_exact_after_the_process_restarts_across_midnight()
    {
        var today = Accumulator("Today", AccumulationPeriod.Day);
        var yesterday = Accumulator("Yesterday", AccumulationPeriod.PreviousDay);

        var (engine, tags, clock) = Start(At(13, 23, 59, 50), today, yesterday);

        // Two midnights are crossed while the gateway is running, so both baselines are observed.
        Publish(tags, clock.Now, 1000);
        engine.Pass(clock.Now);

        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 1000);
        engine.Pass(clock.Now);

        clock.Now = At(14, 12);
        Publish(tags, clock.Now, 1250);
        engine.Pass(clock.Now);

        clock.Now = At(15, 0, 0, 1);
        Publish(tags, clock.Now, 1412.5);
        engine.Pass(clock.Now);

        Assert.Equal(412.5, (double)Read(tags, "Energy/Yesterday").Value!, 6);
        Assert.Equal(Quality.Good, Read(tags, "Energy/Yesterday").Quality);

        // The process ends here. A brand new engine, a brand new tag store — only the file remains.
        var restarted = Start(At(15, 6), today, yesterday);

        var restored = Read(restarted.Tags, "Energy/Yesterday");
        Assert.Equal(412.5, (double)restored.Value!, 6);
        Assert.Equal(Quality.Good, restored.Quality);

        // And it is right before a single device read: the counter has not been published to the new
        // store at all, which is exactly the state a gateway is in for its first few seconds.
        Assert.False(restarted.Tags.TryGet(Counter, out _));
    }

    [Fact]
    public void Previous_day_is_published_Good_before_the_first_device_read_after_a_restart()
    {
        var yesterday = Accumulator("Yesterday", AccumulationPeriod.PreviousDay);
        var (engine, tags, clock) = Start(At(13, 23, 59, 50), yesterday);
        Publish(tags, clock.Now, 100);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 100);
        engine.Pass(clock.Now);
        clock.Now = At(15, 0, 0, 1);
        Publish(tags, clock.Now, 180);
        engine.Pass(clock.Now);

        var restarted = Start(At(15, 3), yesterday);

        // Load() published it — no Pass, no device, no waiting.
        var value = Read(restarted.Tags, "Energy/Yesterday");
        Assert.Equal(80, (double)value.Value!, 6);
        Assert.Equal(Quality.Good, value.Quality);
    }

    /// <summary>
    /// The overnight case on a solar plant, and the reason the history is worth its disk space: the
    /// gateway was off from 20:00 to 06:00, nobody saw midnight, and the answer is still exact —
    /// because the counter provably did not move while nobody was looking.
    /// </summary>
    [Fact]
    public async Task A_gateway_that_was_off_all_night_still_gets_the_right_baseline_because_the_counter_did_not_move()
    {
        var yesterday = Accumulator("Yesterday", AccumulationPeriod.PreviousDay);

        var (engine, tags, clock) = Start(At(13, 23, 59, 50), yesterday);
        Publish(tags, clock.Now, 4000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 4000);       // observed midnight baseline: 4000, Good
        engine.Pass(clock.Now);
        clock.Now = At(14, 20);
        Publish(tags, clock.Now, 5000);
        engine.Pass(clock.Now);

        // The gateway is switched off at 20:00 and comes back at 06:00. The sun was down in between,
        // so the last frame of the evening and the first of the morning read the same.
        await Record(At(14, 20), 5000);
        await Record(At(15, 6), 5000);

        var restarted = Start(At(15, 6, 0, 30), yesterday);

        var value = Read(restarted.Tags, "Energy/Yesterday");
        Assert.Equal(1000, (double)value.Value!, 6);
        Assert.Equal(Quality.Good, value.Quality);

        // The journal holds one line per closed day: the 13th (while running) and the 14th (recovered
        // from the history on restart). It is the 14th that had to survive the outage.
        var closed = _journal.Read(null, null, CounterKey, AccumulationBoundary.Day, 10)
            .Single(e => e.Event == EnergyEvents.PeriodClosed && e.End == At(15, 0));
        Assert.Equal(1000, closed.Energy!.Value, 6);
        Assert.Equal(Quality.Good, closed.Quality);
    }

    [Fact]
    public async Task A_missing_boundary_sample_yields_Uncertain_and_never_a_wrong_number()
    {
        var yesterday = Accumulator("Yesterday", AccumulationPeriod.PreviousDay);

        var (engine, tags, clock) = Start(At(13, 23, 59, 50), yesterday);
        Publish(tags, clock.Now, 4000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 4000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 20);
        Publish(tags, clock.Now, 5000);
        engine.Pass(clock.Now);

        // The counter DID move across the gap — a diesel ran overnight — so the midnight value is
        // genuinely unknown and must not be presented as measured.
        await Record(At(14, 20), 5000);
        await Record(At(15, 6), 5400);

        var restarted = Start(At(15, 6, 0, 30), yesterday);

        var value = Read(restarted.Tags, "Energy/Yesterday");
        Assert.Equal(Quality.Uncertain, value.Quality);
        Assert.Equal(1000, (double)value.Value!, 6);   // the last observed reading, honestly flagged
    }

    [Fact]
    public void A_counter_that_wraps_at_the_UInt32_limit_does_not_produce_a_negative_day()
    {
        const double rollover = 42949672.95;   // UInt32 at a scale of 0.01
        var today = Accumulator("Today", AccumulationPeriod.Day, rollover, maxRate: 1000);

        var (engine, tags, clock) = Start(At(14, 10), today);
        Publish(tags, clock.Now, rollover - 0.50);
        engine.Pass(clock.Now);

        clock.Now = At(14, 10, 1);
        Publish(tags, clock.Now, rollover - 0.20);
        engine.Pass(clock.Now);

        // 42949672.75 → 0.05: the counter wrapped, it did not lose 42 GWh.
        clock.Now = At(14, 10, 2);
        Publish(tags, clock.Now, 0.05);
        engine.Pass(clock.Now);

        var value = Read(tags, "Energy/Today");
        Assert.True((double)value.Value! > 0, $"the day went negative: {value.Value}");
        Assert.Equal(0.55, (double)value.Value!, 6);
        Assert.Contains(_journal.Read(null, null, CounterKey, null, 10), e => e.Event == EnergyEvents.Rollover);
    }

    [Fact]
    public void A_replaced_inverter_rebases_instead_of_reporting_a_huge_negative_energy()
    {
        var today = Accumulator("Today", AccumulationPeriod.Day);
        var yesterday = Accumulator("Yesterday", AccumulationPeriod.PreviousDay);

        var (engine, tags, clock) = Start(At(13, 23, 59, 50), today, yesterday);
        Publish(tags, clock.Now, 8000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 8000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 9);
        Publish(tags, clock.Now, 8300);
        engine.Pass(clock.Now);
        Assert.Equal(300, (double)Read(tags, "Energy/Today").Value!, 6);

        // The inverter is swapped at 14:00 and its replacement starts from nearly zero.
        clock.Now = At(14, 14);
        Publish(tags, clock.Now, 3);
        engine.Pass(clock.Now);

        var today2 = Read(tags, "Energy/Today");
        Assert.Equal(0, (double)today2.Value!, 6);
        Assert.Equal(Quality.Uncertain, today2.Quality);

        // Yesterday's figure was computed in the old counter's scale. It is reported as unknown
        // rather than recomputed into nonsense.
        Assert.Equal(Quality.Bad, Read(tags, "Energy/Yesterday").Quality);
        Assert.Contains(_journal.Read(null, null, CounterKey, null, 10), e => e.Event == EnergyEvents.CounterReset);
    }

    /// <summary>
    /// An industrial PC with a dead RTC battery boots believing it is yesterday, then NTP corrects it.
    /// Rolling the day on the way down and again on the way back up would close it twice and publish
    /// a second, wrong "yesterday".
    /// </summary>
    [Fact]
    public void A_backward_clock_jump_does_not_roll_the_day_twice()
    {
        var yesterday = Accumulator("Yesterday", AccumulationPeriod.PreviousDay);

        var (engine, tags, clock) = Start(At(13, 23, 59, 50), yesterday);
        Publish(tags, clock.Now, 200);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 5);
        Publish(tags, clock.Now, 200);
        engine.Pass(clock.Now);
        clock.Now = At(15, 0, 0, 5);
        Publish(tags, clock.Now, 260);
        engine.Pass(clock.Now);

        var settled = (double)Read(tags, "Energy/Yesterday").Value!;
        Assert.Equal(60, settled, 6);

        // The clock jumps back a full day. Nothing rolls, and the totals say they are not to be trusted.
        clock.Now = At(14, 12);
        Publish(tags, clock.Now, 265);
        engine.Pass(clock.Now);
        Assert.Equal(Quality.Uncertain, Read(tags, "Energy/Yesterday").Quality);

        // It is corrected. One closed day, not two or three.
        clock.Now = At(15, 3);
        Publish(tags, clock.Now, 290);
        engine.Pass(clock.Now);

        var closes = _journal.Read(null, null, CounterKey, AccumulationBoundary.Day, 50)
            .Count(e => e.Event == EnergyEvents.PeriodClosed);
        Assert.Equal(2, closes);   // 13→14 and 14→15, and nothing else
        Assert.Equal(60, (double)Read(tags, "Energy/Yesterday").Value!, 6);
    }

    [Fact]
    public void A_bad_source_freezes_the_total_instead_of_publishing_zero()
    {
        var today = Accumulator("Today", AccumulationPeriod.Day);
        var (engine, tags, clock) = Start(At(14, 0, 0, 1), today);
        Publish(tags, clock.Now, 700);
        engine.Pass(clock.Now);
        clock.Now = At(14, 11);
        Publish(tags, clock.Now, 780);
        engine.Pass(clock.Now);
        Assert.Equal(80, (double)Read(tags, "Energy/Today").Value!, 6);

        clock.Now = At(14, 11, 1);
        Publish(tags, clock.Now, 0, Quality.Bad);
        engine.Pass(clock.Now);

        var value = Read(tags, "Energy/Today");
        Assert.Equal(80, (double)value.Value!, 6);   // held, not zeroed
        Assert.Equal(Quality.Uncertain, value.Quality);
        Assert.Contains("Bad", value.StatusText);
    }

    [Fact]
    public void A_spike_on_the_counter_is_rejected_and_never_becomes_a_baseline()
    {
        var today = Accumulator("Today", AccumulationPeriod.Day, maxRate: 100);

        // Cross midnight while running, so the day's baseline is observed and the test is about the
        // spike alone rather than about a day that started before the gateway did.
        var (engine, tags, clock) = Start(At(13, 23, 59, 55), today);
        Publish(tags, clock.Now, 500);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 500);
        engine.Pass(clock.Now);

        clock.Now = At(14, 0, 0, 2);
        Publish(tags, clock.Now, 9_000_000);   // one mis-decoded register
        engine.Pass(clock.Now);

        Assert.Equal(Quality.Uncertain, Read(tags, "Energy/Today").Quality);
        Assert.Equal(1, engine.Stats.SpikesRejected);

        // A stuck register is re-read and re-rejected every tick. Each rejection is counted, but the
        // journal — a settlement record an auditor reads — gets one line for the episode, not one a
        // second until the fault is cleared.
        for (var i = 0; i < 5; i++)
        {
            clock.Now = clock.Now.AddSeconds(1);
            Publish(tags, clock.Now, 9_000_000);
            engine.Pass(clock.Now);
        }
        Assert.Equal(6, engine.Stats.SpikesRejected);
        Assert.Single(_journal.Read(null, null, CounterKey, null, 50)
            .Where(e => e.Event == EnergyEvents.SpikeRejected));

        // The next honest reading repairs everything, because the answer is a subtraction.
        clock.Now = At(14, 1);
        Publish(tags, clock.Now, 501);
        engine.Pass(clock.Now);

        var value = Read(tags, "Energy/Today");
        Assert.Equal(1, (double)value.Value!, 6);
        Assert.Equal(Quality.Good, value.Quality);
    }

    /// <summary>
    /// Saving a tag rebuilds the engine runtime. The baselines live in a singleton store precisely so
    /// that pressing Apply at 16:00 does not reset the plant's day to zero.
    /// </summary>
    [Fact]
    public async Task Accumulator_state_survives_an_apply_to_engine()
    {
        var today = Accumulator("Today", AccumulationPeriod.Day);
        var (engine, tags, clock) = Start(At(13, 23, 59, 55), today);
        Publish(tags, clock.Now, 2000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 0, 0, 1);
        Publish(tags, clock.Now, 2000);
        engine.Pass(clock.Now);
        clock.Now = At(14, 16);
        Publish(tags, clock.Now, 2345);
        engine.Pass(clock.Now);
        Assert.Equal(345, (double)Read(tags, "Energy/Today").Value!, 6);

        await engine.DisposeAsync();

        // The same tag store, as an apply keeps it — and a rebuilt engine over the same singleton.
        var rebuilt = Start(tags, At(14, 16, 0, 1), today);
        rebuilt.Engine.Pass(At(14, 16, 0, 1));

        var value = Read(tags, "Energy/Today");
        Assert.Equal(345, (double)value.Value!, 6);
        Assert.Equal(Quality.Good, value.Quality);
    }

    [Fact]
    public void Two_accumulators_on_one_counter_share_a_single_baseline()
    {
        var today = Accumulator("Today", AccumulationPeriod.Day);
        var month = Accumulator("ThisMonth", AccumulationPeriod.Month);

        var (engine, tags, clock) = Start(At(14, 0, 0, 1), today, month);
        Publish(tags, clock.Now, 100);
        engine.Pass(clock.Now);
        clock.Now = At(14, 18);
        Publish(tags, clock.Now, 160);
        engine.Pass(clock.Now);

        Assert.Equal(1, engine.Stats.Sources);   // one counter, two tags reading it
        Assert.Equal(60, (double)Read(tags, "Energy/Today").Value!, 6);
        Assert.Equal(60, (double)Read(tags, "Energy/ThisMonth").Value!, 6);
    }

    [Fact]
    public void An_accumulator_without_a_source_is_reported_rather_than_silently_idle()
    {
        var orphan = Accumulator("Today", AccumulationPeriod.Day) with { SourceTagKey = null, SourceTagId = null };
        var (engine, tags, _) = Start(At(14, 10), orphan);

        Assert.Single(engine.Faults);
        Assert.Equal(Quality.Bad, Read(tags, "Energy/Today").Quality);
    }
}
