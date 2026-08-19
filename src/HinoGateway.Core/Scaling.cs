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
        if (value.Quality != Quality.Good || value.Value is null)
            return value;

        if (!TryToDouble(value.Value, out var raw))
            return value; // bool/string — not scalable

        if (!double.IsFinite(raw))
            return NotFinite(value, raw, scaled: false);

        if (IsIdentity(tag))
            return value;

        var scaled = Rescale(raw, tag.Scale, tag.Offset, invert: false);

        // Scaling can produce an infinity the device never sent: a huge raw value times a large
        // scale factor overflows, and the result is then perfectly well-formed nonsense.
        return double.IsFinite(scaled)
            ? value with { Value = scaled }
            : NotFinite(value, raw, scaled: true);
    }

    /// <summary>
    /// A reading that is not a number is not a good reading.
    ///
    /// <para>
    /// A Float32 register holding a NaN — an uninitialised value, a sensor the PLC has marked
    /// invalid, a divide by zero in the device's own logic — used to arrive with
    /// <see cref="Quality.Good"/> and travel unchanged into alarm evaluation, MQTT and IEC-104.
    /// Every comparison against NaN is false, so a high-limit alarm on that tag simply never fires:
    /// the point reads as healthy and in range precisely because its value is meaningless. Marking
    /// it <see cref="Quality.Bad"/> is what turns a silent hole into something the operator sees.
    /// </para>
    /// </summary>
    private static TagValue NotFinite(TagValue value, double raw, bool scaled)
    {
        var what = double.IsNaN(raw) ? "not a number" : "infinite";
        return value with
        {
            Value = null,
            Quality = Quality.Bad,
            StatusText = scaled
                ? $"Scaling overflowed: the raw value {raw} does not convert to a finite engineering value."
                : $"The device returned a value that is {what}.",
        };
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

        // Booleans are not scaled in either direction, but they are type-checked here rather than
        // handed to the driver as-is. See ToRawBool.
        if (tag.DataType == DataType.Bool)
            return ToRawBool(tag, engineering, out raw, out error);

        // The mirror image, and the reason this is a refusal rather than a conversion: true reaching
        // a holding register used to be written as 1. That is a guess about what the caller meant,
        // made at the last moment before the value leaves for the PLC, and on a setpoint the guess
        // is indistinguishable from a deliberate command to set it to one.
        if (engineering is bool)
        {
            error = $"Tag '{tag.Name}' is {tag.DataType}, so true/false is not a value it can take. Send a number.";
            return false;
        }

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
    /// The value a Bool tag may be commanded with — <c>true</c>/<c>false</c>, the numbers 0 and 1,
    /// or those four written as text. Everything else is refused.
    ///
    /// <para>
    /// The refusal is the point. This used to pass the caller's value straight through, and the
    /// Modbus codec ends in <c>Convert.ToDouble(value) != 0</c> — so a 2 typed into the write dialog,
    /// an off-by-one index, or a stale reading fed back through an automation script all closed the
    /// coil. On a breaker or a start/stop point that is an energisation nobody asked for, and the
    /// audit trail would faithfully record it as a successful write of the value 2.
    /// </para>
    ///
    /// <para>
    /// 0 and 1 are accepted because a coil is universally written that way in the field and by every
    /// Modbus tool an engineer already owns; refusing them would be pedantry that gets worked around.
    /// 2 is not "on" in any convention, and neither is 0.5 or NaN.
    /// </para>
    /// </summary>
    private static bool ToRawBool(TagDefinition tag, object engineering, out object raw, out string? error)
    {
        raw = false;
        error = null;

        switch (engineering)
        {
            case bool value:
                raw = value;
                return true;

            case string text when bool.TryParse(text.Trim(), out var parsed):
                raw = parsed;
                return true;

            case string text when text.Trim() is "0" or "1":
                raw = text.Trim() == "1";
                return true;

            case string text:
                error = $"'{text}' is not a value for the Bool tag '{tag.Name}'. Use true, false, 1 or 0.";
                return false;

            default:
                if (!TryToDouble(engineering, out var number))
                {
                    error = $"A {engineering.GetType().Name} cannot be written to the Bool tag '{tag.Name}'. " +
                            "Use true, false, 1 or 0.";
                    return false;
                }
                if (number is 0 or 1)
                {
                    raw = number == 1;
                    return true;
                }
                error = $"{number} is not a value for the Bool tag '{tag.Name}'. Use true, false, 1 or 0 — " +
                        "any other number would silently be treated as ON.";
                return false;
        }
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
