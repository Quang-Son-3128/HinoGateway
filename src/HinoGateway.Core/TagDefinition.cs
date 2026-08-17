namespace HinoGateway.Core;

/// <summary>
/// Static configuration of a single tag: what to read and how to interpret it.
/// The <see cref="Address"/> is driver-specific (e.g. a Modbus register, an S7 DB address,
/// or a simulator waveform name). Kept intentionally small for Phase 0; scaling/deadband/etc.
/// will be added later.
/// </summary>
public sealed record TagDefinition
{
    /// <summary>Globally unique, stable identifier used as the key everywhere downstream.</summary>
    public required string Id { get; init; }

    /// <summary>Human-friendly display name.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// What produces this tag's value. <see cref="TagKind.Device"/> (the default, and 0 in every
    /// project file written before the other kinds existed) means the rest of this record is read
    /// the way it always was: <see cref="Address"/> through a driver on a connection.
    /// </summary>
    public TagKind Kind { get; init; } = TagKind.Device;

    /// <summary>Driver-specific address string. Empty for a tag that is not read from a device.</summary>
    public required string Address { get; init; }

    /// <summary>
    /// The formula of a <see cref="TagKind.Expression"/> tag, e.g.
    /// <c>({Plant/P1} + {Plant/P2}) * 0.001</c>. Null for every other kind.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// What a <see cref="TagKind.Memory"/> tag holds before anything is written to it, written the
    /// way an operator would type it ("0", "false", "auto"). Null/empty starts the tag Bad with
    /// "never written", which is honest: a setpoint nobody has set has no value, and quietly
    /// serving 0 for it would be indistinguishable from someone having chosen 0.
    /// </summary>
    public string? InitialValue { get; init; }

    /// <summary>
    /// Keep a <see cref="TagKind.Memory"/> tag's value across a restart of the gateway.
    ///
    /// <para>
    /// Off by default, and that default is a safety position rather than a convenience: a gateway
    /// restarts because something went wrong, and a control setpoint that silently restores itself
    /// afterwards can put a plant back into a state nobody re-authorised. Turn it on for the values
    /// where losing them is the worse outcome — a manual meter offset, a shift target, a chosen
    /// operating mode — and leave it off for anything that commands equipment.
    /// </para>
    /// </summary>
    public bool Retentive { get; init; }

    /// <summary>When an <see cref="TagKind.Expression"/> tag recomputes. Ignored by other kinds.</summary>
    public EvalMode EvalMode { get; init; } = EvalMode.Scan;

    // --- TagKind.Accumulator ---------------------------------------------------

    /// <summary>
    /// The <b>runtime path</b> of the cumulative counter an accumulator subtracts from — normally a
    /// device tag reading an energy register. Null for every other kind.
    /// </summary>
    public string? SourceTagId { get; init; }

    /// <summary>
    /// The same source, by its <b>stable storage key</b>. The durable baselines are filed under this,
    /// so renaming the counter tag or moving it to another folder does not lose the energy of the day
    /// in progress. Exactly the split alarms already use (<see cref="AlarmDto.TagId"/> plus
    /// <see cref="AlarmDto.TagPath"/>).
    /// </summary>
    public string? SourceTagKey { get; init; }

    /// <summary>Which calendar period this accumulator reports. Ignored by every other kind.</summary>
    public AccumulationPeriod Period { get; init; } = AccumulationPeriod.Day;

    /// <summary>
    /// The engineering value at which the source counter wraps back to zero, or 0 for a counter that
    /// never wraps. A UInt32 register scaled by 0.01 wraps at <c>42949672.95</c>.
    ///
    /// <para>
    /// It has to be configured rather than inferred, because the difference between a wrap and a
    /// replaced meter is not visible in the numbers alone — both look like the counter went backwards.
    /// With this set, a drop that lands within one plausible increment of the wrap point is treated as
    /// a wrap; anything else is treated as a reset and the baselines are rebased.
    /// </para>
    /// </summary>
    public double RolloverAt { get; init; }

    /// <summary>
    /// The largest believable rise in the source per hour, in engineering units, or null to accept
    /// anything. A sample that climbs faster is discarded rather than accepted: a single mis-decoded
    /// register on an energy counter otherwise inflates the day's total permanently, and the next
    /// good sample repairs the gap by itself because the answer is a subtraction.
    /// </summary>
    public double? MaxRatePerHour { get; init; }

    public DataType DataType { get; init; } = DataType.Float64;

    /// <summary>Optional engineering unit for display (e.g. "kg", "°C").</summary>
    public string? Unit { get; init; }

    /// <summary>Optional free-text description of what this tag measures/does.</summary>
    public string? Description { get; init; }

    /// <summary>Linear scaling applied to raw numeric reads: engineering = raw * Scale + Offset.</summary>
    public double Scale { get; init; } = 1.0;

    public double Offset { get; init; } = 0.0;

    /// <summary>Controls are denied unless this exact tag is explicitly opted in.</summary>
    public bool AllowWrite { get; init; }

    /// <summary>Optional lower engineering-unit limit applied before inverse scaling.</summary>
    public double? WriteMin { get; init; }

    /// <summary>Optional upper engineering-unit limit applied before inverse scaling.</summary>
    public double? WriteMax { get; init; }

    /// <summary>
    /// The tag is a <b>cumulative counter</b> — an energy meter register, a run-hour counter — whose
    /// value can only ever rise. A read lower than the highest one accepted so far is published
    /// <see cref="Quality.Uncertain"/> with the reason rather than as a measurement, because on such a
    /// register a drop is not a smaller reading: it is a bad one (a mis-framed response, the wrong
    /// word order, a decode error), and letting it through as Good corrupts every consumption figure
    /// computed from the difference.
    ///
    /// <para>
    /// Off by default. Three consecutive drops <em>are</em> accepted, as the genuine reset they almost
    /// certainly are — a replaced meter, a reloaded PLC — because refusing forever would leave the tag
    /// dead until somebody noticed. Three in a row cannot be a transient decode error.
    /// </para>
    ///
    /// <para>
    /// A <see cref="TagKind.Accumulator"/> does not rely on this flag: it has
    /// <see cref="RolloverAt"/> and <see cref="MaxRatePerHour"/>, which is enough context to tell a
    /// wrap from a reset from a spike and to react to each differently.
    /// </para>
    /// </summary>
    public bool Monotonic { get; init; }

    /// <summary>
    /// How often this particular tag needs reading, when that is slower than the rest of its
    /// connection. Null (the normal case) means it is read on every scan of the connection.
    ///
    /// <para>
    /// A daily energy total does not need the same attention as a load current, and on a metered or
    /// congested link — 4G/5G, radio, a busy S7-1200 with its small PUT/GET connection pool — asking
    /// for it every second is bandwidth and PLC time spent on a number that has not changed. The
    /// driver groups tags into read requests by rate as well as by address, so a slow tag costs a
    /// request only when it is actually due.
    /// </para>
    ///
    /// <para>
    /// The connection's own <see cref="DeviceConfig.ScanInterval"/> is the floor: it is the clock
    /// everything ticks on, so a tag asking to be read faster than its connection is read at the
    /// connection's rate. Setting a slower rate here does delay noticing that a device has died, by
    /// at most the slowest rate configured on it — nothing is asked of the device in between, so
    /// there is nothing to fail.
    /// </para>
    ///
    /// <para>
    /// On an <see cref="TagKind.Expression"/> tag in <see cref="EvalMode.Scan"/> there is no
    /// connection to follow, so this <em>is</em> its evaluation period and null means
    /// <see cref="InternalTagDefaults.ExpressionScanInterval"/>.
    /// </para>
    /// </summary>
    public TimeSpan? ScanInterval { get; init; }
}

/// <summary>One shared safety check for every control path.</summary>
public static class TagWritePolicy
{
    public static void EnsureAllowed(TagDefinition tag, object value)
    {
        if (!tag.AllowWrite)
            throw new UnauthorizedAccessException($"Tag '{tag.Id}' is read-only. Enable AllowWrite explicitly before commanding it.");

        if (tag.WriteMin is null && tag.WriteMax is null)
            return;

        double number;
        try
        {
            number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new ArgumentException($"Tag '{tag.Id}' has numeric write limits but the supplied value is not numeric.", nameof(value));
        }

        if (!double.IsFinite(number))
            throw new ArgumentOutOfRangeException(nameof(value), "NaN and infinity cannot be written.");
        if (tag.WriteMin is { } min && number < min)
            throw new ArgumentOutOfRangeException(nameof(value), number,
                $"Value for '{tag.Id}' is below its write minimum {min}.");
        if (tag.WriteMax is { } max && number > max)
            throw new ArgumentOutOfRangeException(nameof(value), number,
                $"Value for '{tag.Id}' is above its write maximum {max}.");
    }
}

/// <summary>Defaults the internal tag kinds fall back to, in one place rather than at each caller.</summary>
public static class InternalTagDefaults
{
    /// <summary>
    /// Evaluation period of a scan-mode expression tag that does not ask for one. A second is the
    /// rate at which the rest of a gateway breathes — fast enough for a trend or an alarm, slow
    /// enough that a thousand of them cost nothing.
    /// </summary>
    public static readonly TimeSpan ExpressionScanInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Floor on an expression tag's period. Below this, evaluation stops being a calculation and
    /// starts being a busy loop over the tag store — and no source tag in this gateway is read that
    /// fast anyway, so the extra evaluations could only ever recompute the same inputs.
    /// </summary>
    public static readonly TimeSpan MinimumExpressionScanInterval = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// Conversion between the transport's milliseconds and the domain's optional interval, in one place
/// so the "0 means follow the connection" convention cannot drift between the seven mapping sites
/// a tag passes through.
/// </summary>
public static class TagScanRate
{
    public static TimeSpan? FromMilliseconds(int milliseconds) =>
        milliseconds > 0 ? TimeSpan.FromMilliseconds(milliseconds) : null;

    public static int ToMilliseconds(TimeSpan? interval) =>
        interval is { } value ? (int)value.TotalMilliseconds : 0;
}
