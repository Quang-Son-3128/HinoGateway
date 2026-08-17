namespace HinoGateway.Core;

/// <summary>
/// Which calendar period a <see cref="TagKind.Accumulator"/> tag reports.
///
/// <para>
/// The "previous" members are what a utility actually asks for: 378/QĐ-EVN's Ainv_D-1 is yesterday's
/// energy, reported today. They are a separate tag rather than a mode because a plant reports both at
/// once — today's running total on one point and yesterday's settled figure on another — and both
/// come from the same pair of stored baselines at no extra cost.
/// </para>
///
/// Never renumber: these values are stored in <c>hino.json</c> and in <c>.hino</c> exports.
/// </summary>
public enum AccumulationPeriod
{
    /// <summary>Since local midnight — the running total that grows through the day.</summary>
    Day = 0,

    /// <summary>The completed previous calendar day. This is EVN's Ainv_D-1.</summary>
    PreviousDay = 1,

    /// <summary>Since 00:00 on the first of the local month.</summary>
    Month = 2,

    PreviousMonth = 3,

    /// <summary>Since 00:00 on 1 January, local time.</summary>
    Year = 4,

    PreviousYear = 5,
}

/// <summary>
/// The three baselines actually kept per source. Six reported periods, three stored boundaries:
/// "today" and "yesterday" are two subtractions over the same pair of day baselines.
/// </summary>
public enum AccumulationBoundary
{
    Day = 0,
    Month = 1,
    Year = 2,
}

public static class AccumulationPeriods
{
    /// <summary>Which stored boundary a reported period reads.</summary>
    public static AccumulationBoundary Boundary(this AccumulationPeriod period) => period switch
    {
        AccumulationPeriod.Day or AccumulationPeriod.PreviousDay => AccumulationBoundary.Day,
        AccumulationPeriod.Month or AccumulationPeriod.PreviousMonth => AccumulationBoundary.Month,
        _ => AccumulationBoundary.Year,
    };

    /// <summary>Does this period report the completed previous one rather than the running current one?</summary>
    public static bool IsPrevious(this AccumulationPeriod period) => period is
        AccumulationPeriod.PreviousDay or AccumulationPeriod.PreviousMonth or AccumulationPeriod.PreviousYear;

    /// <summary>
    /// The instant the period containing <paramref name="at"/> began, in <b>local</b> time — because
    /// a plant's day, and the utility's, starts at local midnight and not at 07:00 because the data
    /// happens to be handled in UTC somewhere along the way.
    /// </summary>
    public static DateTimeOffset Start(this AccumulationBoundary boundary, DateTimeOffset at)
    {
        var local = at.DateTime;
        var date = boundary switch
        {
            AccumulationBoundary.Day => local.Date,
            AccumulationBoundary.Month => new DateTime(local.Year, local.Month, 1),
            _ => new DateTime(local.Year, 1, 1),
        };
        return new DateTimeOffset(date, at.Offset);
    }
}
