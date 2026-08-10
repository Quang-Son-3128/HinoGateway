using HinoGateway.Core;
using lib60870;
using lib60870.CS101;

namespace HinoGateway.Northbound.Iec104;

/// <summary>
/// Translation between the gateway's tag values and IEC 60870-5 information objects.
///
/// Two things matter more than they look:
/// <list type="bullet">
/// <item><b>Quality never gets lost.</b> A value the gateway could not read must arrive at the control
/// centre flagged invalid, not as a plausible number. A dispatcher acting on a stale reading is the
/// worst failure this component can produce.</item>
/// <item><b>Range is clamped, not wrapped.</b> A measurand outside what the chosen ASDU type can carry
/// is pinned to the limit and marked overflow, which is what the standard's overflow bit is for —
/// letting it wrap would turn a high value into a low one.</item>
/// </list>
/// </summary>
public static class Iec104ValueMapping
{
    /// <summary>Map the gateway's quality onto the IEC quality descriptor.</summary>
    public static QualityDescriptor ToQuality(TagValue? value, bool overflow = false)
    {
        var quality = new QualityDescriptor();

        if (value is null)
        {
            // Never read: the control centre must not treat "no data" as a measurement.
            quality.Invalid = true;
            return quality;
        }

        switch (value.Quality)
        {
            case Quality.Good:
                break;
            case Quality.Uncertain:
                // The value may be usable but is not current — exactly what "not topical" means.
                quality.NonTopical = true;
                break;
            default:
                quality.Invalid = true;
                break;
        }

        quality.Overflow = overflow;
        return quality;
    }

    /// <summary>The engineering value as a double, or null when it is not a number we can report.</summary>
    public static double? ToNumber(TagValue? value)
    {
        if (value?.Value is null)
            return null;

        return value.Value switch
        {
            bool b => b ? 1 : 0,
            double d => double.IsFinite(d) ? d : null,
            float f => float.IsFinite(f) ? f : null,
            IConvertible c and not string => Convert(c),
            string s => double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
            _ => null,
        };

        static double? Convert(IConvertible c)
        {
            try
            {
                var d = c.ToDouble(System.Globalization.CultureInfo.InvariantCulture);
                return double.IsFinite(d) ? d : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool ToBool(TagValue? value) => ToNumber(value) is { } n && n != 0;

    /// <summary>
    /// Build the information object for a point. Returns null when the value cannot be represented at
    /// all (e.g. text in a measurand) — the caller reports that as an invalid reading rather than
    /// inventing a number.
    /// </summary>
    public static InformationObject? ToInformationObject(Iec104PointDto point, TagValue? value, bool utc)
    {
        var number = ToNumber(value);
        var scaled = number * point.ReportScale;
        var time = point.WithTimestamp
            ? new CP56Time2a(Timestamp(value, utc))
            : null;

        switch (point.Type)
        {
            case Iec104PointType.SinglePoint:
            {
                var state = number is { } n && n != 0;
                var quality = ToQuality(value);
                return time is null
                    ? new SinglePointInformation(point.Ioa, state, quality)
                    : new SinglePointWithCP56Time2a(point.Ioa, state, quality, time);
            }

            case Iec104PointType.DoublePoint:
            {
                // 0/3 are the indeterminate states; a value we do not trust must not read as a
                // confident OFF or ON.
                var state = value is null || value.Quality == Quality.Bad || number is null
                    ? DoublePointValue.INDETERMINATE
                    : number != 0 ? DoublePointValue.ON : DoublePointValue.OFF;
                var quality = ToQuality(value);
                return time is null
                    ? new DoublePointInformation(point.Ioa, state, quality)
                    : new DoublePointWithCP56Time2a(point.Ioa, state, quality, time);
            }

            case Iec104PointType.Normalized:
            {
                if (scaled is null)
                    return Invalid(point, time);
                var clamped = Clamp(scaled.Value, -1.0, 0.999969482421875, out var overflow);   // -1 … 1-2^-15
                var quality = ToQuality(value, overflow);
                return time is null
                    ? new MeasuredValueNormalized(point.Ioa, (float)clamped, quality)
                    : new MeasuredValueNormalizedWithCP56Time2a(point.Ioa, (float)clamped, quality, time);
            }

            case Iec104PointType.Scaled:
            {
                if (scaled is null)
                    return Invalid(point, time);
                var clamped = Clamp(scaled.Value, short.MinValue, short.MaxValue, out var overflow);
                var quality = ToQuality(value, overflow);
                var rounded = (int)Math.Round(clamped, MidpointRounding.AwayFromZero);
                return time is null
                    ? new MeasuredValueScaled(point.Ioa, rounded, quality)
                    : new MeasuredValueScaledWithCP56Time2a(point.Ioa, rounded, quality, time);
            }

            case Iec104PointType.ShortFloat:
            {
                if (scaled is null)
                    return Invalid(point, time);
                var quality = ToQuality(value);
                return time is null
                    ? new MeasuredValueShort(point.Ioa, (float)scaled.Value, quality)
                    : new MeasuredValueShortWithCP56Time2a(point.Ioa, (float)scaled.Value, quality, time);
            }

            case Iec104PointType.IntegratedTotals:
            {
                if (scaled is null)
                    return Invalid(point, time);
                var clamped = Clamp(scaled.Value, int.MinValue, int.MaxValue, out _);
                var counter = new BinaryCounterReading
                {
                    Value = (int)Math.Round(clamped, MidpointRounding.AwayFromZero),
                    Invalid = value is null || value.Quality != Quality.Good,
                };
                return time is null
                    ? new IntegratedTotals(point.Ioa, counter)
                    : new IntegratedTotalsWithCP56Time2a(point.Ioa, counter, time);
            }

            default:
                return null;
        }
    }

    /// <summary>A reading the gateway cannot represent is still reported — as invalid, with no value.</summary>
    private static InformationObject Invalid(Iec104PointDto point, CP56Time2a? time)
    {
        var quality = new QualityDescriptor { Invalid = true };
        return point.Type switch
        {
            Iec104PointType.Normalized => time is null
                ? new MeasuredValueNormalized(point.Ioa, 0f, quality)
                : new MeasuredValueNormalizedWithCP56Time2a(point.Ioa, 0f, quality, time),
            Iec104PointType.Scaled => time is null
                ? new MeasuredValueScaled(point.Ioa, 0, quality)
                : new MeasuredValueScaledWithCP56Time2a(point.Ioa, 0, quality, time),
            Iec104PointType.IntegratedTotals => time is null
                ? new IntegratedTotals(point.Ioa, new BinaryCounterReading { Value = 0, Invalid = true })
                : new IntegratedTotalsWithCP56Time2a(point.Ioa, new BinaryCounterReading { Value = 0, Invalid = true }, time),
            _ => time is null
                ? new MeasuredValueShort(point.Ioa, 0f, quality)
                : new MeasuredValueShortWithCP56Time2a(point.Ioa, 0f, quality, time),
        };
    }

    private static double Clamp(double value, double min, double max, out bool overflow)
    {
        overflow = value < min || value > max;
        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// The instant to report: the moment the value was sampled at the device, not the moment we sent
    /// it. The control centre's event list is only useful if it carries the process time.
    /// </summary>
    private static DateTime Timestamp(TagValue? value, bool utc)
    {
        var instant = value?.SourceTimestamp ?? DateTimeOffset.UtcNow;
        return utc ? instant.UtcDateTime : instant.ToLocalTime().DateTime;
    }

    /// <summary>
    /// Has the value moved enough to be worth reporting? Status points report every change; measurands
    /// respect the point's deadband, which is what keeps a noisy analogue from flooding the link.
    /// </summary>
    public static bool ExceedsDeadband(Iec104PointDto point, TagValue? previous, TagValue current)
    {
        if (previous is null)
            return true;

        // A change of quality is always news, whatever the value did.
        if (previous.Quality != current.Quality)
            return true;

        var isStatus = point.Type is Iec104PointType.SinglePoint or Iec104PointType.DoublePoint;
        if (isStatus)
            return ToBool(previous) != ToBool(current);

        var before = ToNumber(previous);
        var after = ToNumber(current);
        if (before is null || after is null)
            return before is not null || after is not null;

        if (point.Deadband <= 0)
            return before.Value != after.Value;

        return Math.Abs(after.Value - before.Value) >= point.Deadband;
    }
}
