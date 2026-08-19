namespace HinoGateway.Core;

/// <summary>One tag's value in a history frame. Numeric only — see <see cref="ISampleHistory"/>.</summary>
public sealed record HistorySample(double Value, Quality Quality);

/// <summary>
/// Every tag the gateway knew about at one instant. A frame is written whole: the values in it were
/// all read within the same sampling tick, so a consumer comparing two tags in one frame is comparing
/// two numbers that were true at the same moment.
/// </summary>
public sealed record HistoryFrame(DateTimeOffset Timestamp, IReadOnlyDictionary<string, HistorySample> Values);

/// <summary>What the store holds and what it failed to hold, for the health endpoint and the UI.</summary>
public sealed record HistoryStats(int Files, long Bytes, long Frames, long Dropped);

public sealed class SampleHistoryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Seconds between frames. EVN decision 378/QĐ-EVN requires local storage at a resolution of at
    /// most 5 minutes; one minute is the shipped default because it costs about 12 MB a week on a
    /// 32-tag site and gives the accumulators a boundary sample they can actually trust.
    /// </summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Days to keep. 378/QĐ-EVN requires at least 7.</summary>
    public int RetentionDays { get; set; } = 7;

    public int MaxFiles { get; set; } = 14;

    /// <summary>A safety valve for a site with many tags, not the primary limit.</summary>
    public long MaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Empty means the default folder under the gateway data root.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// The gateway's local record of what every tag was doing, minute by minute.
///
/// <para>
/// It exists three times over: 378/QĐ-EVN requires a site to keep its measurements locally at a
/// resolution of at most five minutes for at least seven days; the accumulators need a sample from
/// either side of midnight to recover a day's baseline after a power cut; and the store-and-forward
/// lane needs something to replay once a broker comes back. One file serves all three.
/// </para>
///
/// <para>
/// Samples are <b>numeric</b>. A boolean is stored as 0 or 1; a tag whose value is neither a number
/// nor a boolean is simply absent from the frame, as is a tag that has never been read. That keeps
/// the file a table of numbers an engineer can plot or import, and everything this history is
/// actually for — energy baselines, trends, replay of measurements — is numeric anyway.
/// </para>
/// </summary>
public interface ISampleHistory
{
    /// <summary>Append one frame durably. Returns after the bytes have reached the disk.</summary>
    Task AppendAsync(HistoryFrame frame, CancellationToken ct = default);

    /// <summary>Frames in [from, to], oldest first. Unreadable lines are skipped, never thrown on.</summary>
    IAsyncEnumerable<HistoryFrame> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>
    /// The same, keeping only <paramref name="tags"/> in each frame — null for all of them.
    ///
    /// <para>
    /// The filter belongs down here rather than at the caller because the cost being avoided is the
    /// parse, not the copy. A frame on a large site names hundreds of tags; building a dictionary of
    /// all of them so that the API can keep two is most of the work of the request, repeated for
    /// every frame in the range. The default implementation exists so that an <c>ISampleHistory</c>
    /// which cannot do better is still correct.
    /// </para>
    /// </summary>
    async IAsyncEnumerable<HistoryFrame> ReadAsync(DateTimeOffset from, DateTimeOffset to,
        IReadOnlySet<string>? tags,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var frame in ReadAsync(from, to, ct).ConfigureAwait(false))
        {
            if (tags is null)
            {
                yield return frame;
                continue;
            }
            var kept = frame.Values
                .Where(v => tags.Contains(v.Key))
                .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
            if (kept.Count > 0)
                yield return frame with { Values = kept };
        }
    }

    /// <summary>
    /// The sample of <paramref name="tagId"/> closest to <paramref name="at"/>, if one exists within
    /// <paramref name="tolerance"/> either side. This is how a period boundary gets a value that is
    /// honestly "the reading at midnight" rather than "the first reading after we came back up".
    /// </summary>
    bool TryGetNearest(string tagId, DateTimeOffset at, TimeSpan tolerance,
        out HistorySample sample, out DateTimeOffset actualAt);

    /// <summary>
    /// The samples immediately before and after <paramref name="at"/>. When the two are equal the
    /// counter did not move across the gap, which makes the boundary value exactly known even though
    /// the gateway was switched off at the time — the ordinary case for solar overnight.
    /// </summary>
    bool TryGetAround(string tagId, DateTimeOffset at,
        out (DateTimeOffset At, HistorySample Sample)? before,
        out (DateTimeOffset At, HistorySample Sample)? after);

    /// <summary>Timestamp of the oldest frame still on disk, or null when there is no history.</summary>
    DateTimeOffset? OldestRetained { get; }

    HistoryStats Stats { get; }
}
