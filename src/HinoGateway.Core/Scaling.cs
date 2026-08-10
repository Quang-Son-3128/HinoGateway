namespace HinoGateway.Core;

/// <summary>
/// Linear conversion between a device's raw value and the engineering value operators see
/// (<c>eng = raw * Scale + Offset</c>), applied generically by the engine so scaling is defined
/// once and works for every protocol. Non-numeric values (bool/string) and non-Good qualities pass
/// through untouched.
///
/// Reads use <see cref="Apply"/>; writes MUST use <see cref="ToRaw"/> — an operator types an
/// engineering value, and sending it to the device unscaled would silently write the wrong number
/// (with scale 0.1, "50" would land as 50 instead of 500 and read back as 5).
/// </summary>
public static class Scaling
{
    public static bool IsIdentity(TagDefinition tag) => tag.Scale == 1.0 && tag.Offset == 0.0;

    public static TagValue Apply(TagDefinition tag, TagValue value)
    {
        if (IsIdentity(tag) || value.Quality != Quality.Good || value.Value is null)
            return value;

        if (!TryToDouble(value.Value, out var raw))
            return value; // bool/string — not scalable

        return value with { Value = Rescale(raw, tag.Scale, tag.Offset, invert: false) };
    }

    /// <summary>
    /// The inverse of <see cref="Apply"/>: turn the engineering value an operator entered into the
    /// raw value to send to the device (<c>raw = (eng - Offset) / Scale</c>), then verify it is
    /// actually representable in the tag's data type. Returns false with a human-readable
    /// <paramref name="error"/> rather than letting a bad command reach a PLC.
    /// </summary>
    public static bool ToRaw(TagDefinition tag, object engineering, out object raw, out string? error)
    {
        raw = engineering;
        error = null;

        if (tag.DataType == DataType.String)
        {
            error = "Writing string tags is not supported.";
            return false;
        }

        // Booleans are not scaled in either direction; hand them straight to the driver.
        if (tag.DataType == DataType.Bool || engineering is bool)
            return true;

        if (!TryToDouble(engineering, out var eng))
        {
            if (engineering is string s && double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            {
                eng = parsed;
            }
            else
            {
                error = $"'{engineering}' is not a number, and tag '{tag.Name}' is {tag.DataType}.";
                return false;
            }
        }

        if (double.IsNaN(eng) || double.IsInfinity(eng))
        {
            error = "Value must be a finite number.";
            return false;
        }

        var value = eng;
        if (!IsIdentity(tag))
        {
            if (tag.Scale == 0.0)
            {
                // eng = raw*0 + offset has no inverse: every raw value maps to the same reading.
                error = $"Tag '{tag.Name}' has Scale = 0, so an engineering value cannot be converted back to a raw value.";
                return false;
            }
            value = Rescale(eng, tag.Scale, tag.Offset, invert: true);
        }

        if (!FitsDataType(tag.DataType, value, out var typed, out error))
            return false;

        raw = typed!;
        return true;
    }

    /// <summary>
    /// Is <paramref name="value"/> representable in <paramref name="dataType"/>? Integer types are
    /// rounded to nearest (a scaled value rarely lands exactly on an integer) and then range-checked,
    /// so an out-of-range command is refused instead of silently wrapping around on the device.
    /// </summary>
    public static bool FitsDataType(DataType dataType, double value, out object? typed, out string? error)
    {
        typed = null;
        error = null;

        switch (dataType)
        {
            case DataType.Bool:
                typed = value != 0;
                return true;

            case DataType.Float32:
                if (Math.Abs(value) > float.MaxValue)
                {
                    error = $"{value} is out of range for Float32.";
                    return false;
                }
                typed = (float)value;
                return true;

            case DataType.Float64:
                typed = value;
                return true;

            case DataType.Int16:
                return FitsInteger(value, short.MinValue, short.MaxValue, "Int16", v => (short)v, out typed, out error);

            case DataType.Int32:
                return FitsInteger(value, int.MinValue, int.MaxValue, "Int32", v => (int)v, out typed, out error);

            case DataType.Int64:
                return FitsInteger(value, long.MinValue, long.MaxValue, "Int64", v => (long)v, out typed, out error);

            case DataType.UInt16:
                return FitsInteger(value, ushort.MinValue, ushort.MaxValue, "UInt16", v => (ushort)v, out typed, out error);

            case DataType.UInt32:
                return FitsInteger(value, uint.MinValue, uint.MaxValue, "UInt32", v => (uint)v, out typed, out error);

            case DataType.UInt64:
                return FitsInteger(value, ulong.MinValue, ulong.MaxValue, "UInt64", v => (ulong)v, out typed, out error);

            default:
                error = $"Data type {dataType} cannot be written.";
                return false;
        }
    }

    private static bool FitsInteger(double value, double min, double max, string name,
        Func<double, object> convert, out object? typed, out string? error)
    {
        typed = null;
        error = null;
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded < min || rounded > max)
        {
            error = $"{value} is out of range for {name} ({min} … {max}).";
            return false;
        }
        typed = convert(rounded);
        return true;
    }

    /// <summary>
    /// Apply (or undo) <c>eng = raw * Scale + Offset</c>, doing the arithmetic in <c>decimal</c>
    /// where the magnitudes allow it.
    ///
    /// This is not pedantry: scale factors and the values operators type are decimal quantities, and
    /// in binary floating point <c>5.05 / 0.1</c> is 50.499999999999993 — which rounds to 50 and
    /// writes a different number to the PLC than the operator asked for. Converting to decimal first
    /// snaps 5.05 and 0.1 back to the values they were written as. Falls back to double for
    /// magnitudes decimal cannot represent.
    /// </summary>
    private static double Rescale(double value, double scale, double offset, bool invert)
    {
        try
        {
            var v = (decimal)value;
            var s = (decimal)scale;
            var o = (decimal)offset;
            return (double)(invert ? (v - o) / s : v * s + o);
        }
        catch (OverflowException)
        {
            return invert ? (value - offset) / scale : value * scale + offset;
        }
    }

    private static bool TryToDouble(object v, out double d)
    {
        switch (v)
        {
            case double x: d = x; return true;
            case float x: d = x; return true;
            case long x: d = x; return true;
            case int x: d = x; return true;
            case short x: d = x; return true;
            case ulong x: d = x; return true;
            case uint x: d = x; return true;
            case ushort x: d = x; return true;
            case sbyte x: d = x; return true;
            case byte x: d = x; return true;
            default: d = 0; return false;
        }
    }
}
