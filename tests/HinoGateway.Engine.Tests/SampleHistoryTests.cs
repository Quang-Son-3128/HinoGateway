using HinoGateway.Core;
using HinoGateway.Server.Services;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// The local record decision 378/QĐ-EVN demands, and the file an accumulator reads to work out what
/// its counter read at midnight. Everything here runs against real files on disk, because the
/// failures worth catching — a torn line after a power cut, retention deleting the file being
/// written — only exist there.
/// </summary>
public class SampleHistoryTests : IDisposable
{
    private readonly string _dir;

    public SampleHistoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hino-history", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private JsonlSampleHistory NewHistory(SampleHistoryOptions? options = null) =>
        new(_dir, options ?? new SampleHistoryOptions(), NullLogger<JsonlSampleHistory>.Instance);

    private static HistoryFrame Frame(DateTimeOffset at, double value, Quality quality = Quality.Good) =>
        new(at, new Dictionary<string, HistorySample> { ["Inverter/Inv1Energy"] = new(value, quality) });

    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 8, day, hour, minute, 0, TimeSpan.FromHours(7));

    [Fact]
    public async Task A_frame_survives_a_torn_write_and_retention_never_deletes_the_open_file()
    {
        var options = new SampleHistoryOptions { RetentionDays = 1, MaxFiles = 1, MaxTotalBytes = 1 };
        var history = NewHistory(options);
        await history.AppendAsync(Frame(At(14, 0), 100));
        await history.AppendAsync(Frame(At(14, 1), 101));

        // What a power cut mid-write leaves behind: a line that is not JSON. Appended through a
        // sharing-friendly handle because the store still holds the file open, exactly as it would
        // when the power actually goes.
        var file = Directory.GetFiles(_dir, "hist-*.jsonl").Single();
        await using (var torn = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        await using (var writer = new StreamWriter(torn))
            await writer.WriteAsync("{\"t\":\"2026-08-14T02:00:00+07:0");

        var frames = new List<HistoryFrame>();
        await foreach (var frame in history.ReadAsync(At(14, 0), At(14, 23)))
            frames.Add(frame);

        Assert.Equal(2, frames.Count);
        Assert.Equal(100, frames[0].Values["Inverter/Inv1Energy"].Value);
        Assert.Equal(101, frames[1].Values["Inverter/Inv1Energy"].Value);

        // Both retention ceilings are set below what is on disk, and the open file still survives:
        // deleting today's history to satisfy a byte budget would throw away the only frames that
        // have not been consumed yet.
        await history.AppendAsync(Frame(At(14, 2), 102));
        Assert.True(File.Exists(file));
        history.Dispose();
    }

    [Fact]
    public async Task Retention_drops_the_oldest_days_and_keeps_the_newest()
    {
        var history = NewHistory(new SampleHistoryOptions { RetentionDays = 3650, MaxFiles = 2 });
        foreach (var day in new[] { 11, 12, 13, 14 })
            await history.AppendAsync(Frame(At(day, 0), day));

        var remaining = Directory.GetFiles(_dir, "hist-*.jsonl").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(["hist-20260813.jsonl", "hist-20260814.jsonl"], remaining);
        history.Dispose();
    }

    [Fact]
    public async Task The_nearest_sample_to_a_boundary_is_found_within_tolerance_and_refused_outside_it()
    {
        var history = NewHistory();
        await history.AppendAsync(Frame(At(14, 23, 58), 500));
        await history.AppendAsync(Frame(At(15, 0, 1), 502));
        await history.AppendAsync(Frame(At(15, 0, 5), 503));

        var midnight = At(15, 0);

        Assert.True(history.TryGetNearest("Inverter/Inv1Energy", midnight, TimeSpan.FromMinutes(2),
            out var sample, out var actualAt));
        Assert.Equal(502, sample.Value);          // 1 minute after beats 2 minutes before
        Assert.Equal(At(15, 0, 1), actualAt);

        Assert.False(history.TryGetNearest("Inverter/Inv1Energy", midnight, TimeSpan.FromSeconds(30),
            out _, out _));
        Assert.False(history.TryGetNearest("No/Such/Tag", midnight, TimeSpan.FromHours(1), out _, out _));
        history.Dispose();
    }

    [Fact]
    public async Task Samples_either_side_of_a_gap_are_found_across_the_day_boundary()
    {
        var history = NewHistory();
        await history.AppendAsync(Frame(At(14, 20), 800));   // last frame before the gateway died
        await history.AppendAsync(Frame(At(15, 6), 800));    // first frame after it came back

        Assert.True(history.TryGetAround("Inverter/Inv1Energy", At(15, 0), out var before, out var after));

        // Equal readings either side of midnight prove the counter did not move while the gateway was
        // off, which is what makes the missing midnight baseline exactly known rather than a guess.
        Assert.Equal(At(14, 20), before!.Value.At);
        Assert.Equal(At(15, 6), after!.Value.At);
        Assert.Equal(before.Value.Sample.Value, after.Value.Sample.Value);
        history.Dispose();
    }

    [Fact]
    public async Task A_frame_keeps_each_tags_quality_and_omits_tags_that_have_no_number()
    {
        var history = NewHistory();
        await history.AppendAsync(new HistoryFrame(At(14, 0), new Dictionary<string, HistorySample>
        {
            ["Plant/Good"] = new(1.5, Quality.Good),
            ["Plant/Doubtful"] = new(2.5, Quality.Uncertain),
            ["Plant/NotANumber"] = new(double.NaN, Quality.Good),
        }));

        var frame = await First(history.ReadAsync(At(14, 0), At(14, 1)));
        Assert.Equal(Quality.Good, frame.Values["Plant/Good"].Quality);
        Assert.Equal(Quality.Uncertain, frame.Values["Plant/Doubtful"].Quality);
        Assert.False(frame.Values.ContainsKey("Plant/NotANumber"));
        history.Dispose();
    }

    [Fact]
    public async Task The_store_reports_what_it_holds_and_what_it_lost()
    {
        var history = NewHistory();
        Assert.Null(history.OldestRetained);

        await history.AppendAsync(Frame(At(13, 0), 1));
        await history.AppendAsync(Frame(At(14, 0), 2));

        Assert.Equal(At(13, 0), history.OldestRetained);
        var stats = history.Stats;
        Assert.Equal(2, stats.Files);
        Assert.Equal(2, stats.Frames);
        Assert.Equal(0, stats.Dropped);
        Assert.True(stats.Bytes > 0);
        history.Dispose();
    }

    /// <summary>
    /// Midnight has to be a real frame, not "sixty seconds after whenever the service happened to
    /// start" — the accumulator's whole day hangs off that one sample existing.
    /// </summary>
    [Fact]
    public void Frames_are_stamped_on_the_interval_boundary_not_on_the_start_time()
    {
        var minute = TimeSpan.FromMinutes(1);
        var offset = TimeSpan.FromHours(7);

        Assert.Equal(new DateTimeOffset(2026, 8, 14, 10, 31, 0, offset),
            HistorySamplerService.NextBoundary(new DateTimeOffset(2026, 8, 14, 10, 30, 17, offset), minute));

        // Exactly on a boundary means the next one, so a tick is never written twice.
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 10, 31, 0, offset),
            HistorySamplerService.NextBoundary(new DateTimeOffset(2026, 8, 14, 10, 30, 0, offset), minute));

        // A late tick is dated by the boundary it actually crossed, not by the one it was waiting for.
        var late = new DateTimeOffset(2026, 8, 14, 10, 42, 30, offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 10, 42, 0, offset),
            HistorySamplerService.NextBoundary(late, minute).Add(-minute));

        // A five-minute interval still lands exactly on midnight, which is the frame that matters.
        var fiveMinutes = TimeSpan.FromMinutes(5);
        var beforeMidnight = new DateTimeOffset(2026, 8, 14, 23, 58, 1, offset);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, offset),
            HistorySamplerService.NextBoundary(beforeMidnight, fiveMinutes));
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 23, 55, 0, offset),
            HistorySamplerService.NextBoundary(beforeMidnight, fiveMinutes).Add(-fiveMinutes));
    }

    [Fact]
    public void A_tag_is_sampled_only_when_it_carries_a_number()
    {
        Assert.Equal(42d, HistorySamplerService.TryNumber(42));
        Assert.Equal(1d, HistorySamplerService.TryNumber(true));
        Assert.Equal(0d, HistorySamplerService.TryNumber(false));
        Assert.Equal(1.25d, HistorySamplerService.TryNumber(1.25f));
        Assert.Equal(3.5d, HistorySamplerService.TryNumber("3.5"));
        Assert.Null(HistorySamplerService.TryNumber("RUNNING"));
        Assert.Null(HistorySamplerService.TryNumber(null));
        Assert.Null(HistorySamplerService.TryNumber(double.NaN));
        Assert.Null(HistorySamplerService.TryNumber(double.PositiveInfinity));
    }

    private static async Task<HistoryFrame> First(IAsyncEnumerable<HistoryFrame> frames)
    {
        await foreach (var frame in frames)
            return frame;
        throw new InvalidOperationException("No frame was written.");
    }
}
