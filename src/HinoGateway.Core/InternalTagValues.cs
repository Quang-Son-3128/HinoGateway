using System.Globalization;

namespace HinoGateway.Core;

/// <summary>
/// Fitting a value produced <em>inside</em> the gateway — an expression's result, an operator's write
/// into a memory tag, a configured initial value — onto the tag's declared
/// <see cref="DataType"/>.
///
/// <para>
/// A device tag gets its type from the driver, which decodes the exact bytes the protocol defines. An
/// internal tag has no such authority: the arithmetic is done in double and the operator types text,
/// so the declared type is the only statement of what the tag is supposed to hold, and it has to be
/// enforced here or not at all. It matters downstream — an IEC-104 measurand, a Modbus write and an
/// MQTT payload all ask the tag what type it is and believe the answer.
/// </para>
///
/// <para>
/// Text is never quietly read as a number (that is what <c>tonumber()</c> is for) and a value outside
/// the type's range is refused rather than wrapped, for the same reason the write path refuses it: a
/// wrapped number is a wrong number that looks like a real one.
/// </para>
/// </summary>
public static class InternalTagValues
{
    /// <summary>
    /// Coerce a computed value onto <paramref name="dataType"/>. Accepts the three types an
    /// expression works in — number, boolean, text — plus the CLR numeric types a driver may have put
    /// into the store.
    /// </summary>
    public static bool TryCoerce(DataType dataType, object? value, out object? typed, out string? error)
    {
        typed = null;
        error = null;

        if (value is null)
        {
            error = "there is no value";
            return false;
        }

        if (dataType == DataType.String)
        {
            typed = AsText(value);
            return true;
        }

        if (dataType == DataType.Bool)
        {
            switch (value)
            {
                case bool b:
                    typed = b;
                    return true;
                case string text when TryParseBool(text, out var parsed):
                    typed = parsed;
                    return true;
                case string text:
                    error = $"'{text}' is not true or false";
                    return false;
                default:
                    if (!TryToDouble(value, out var number))
                    {
                        error = $"a {Describe(value)} cannot be stored in a Bool tag";
                        return false;
                    }
                    typed = number != 0;
                    return true;
            }
        }

        if (value is string s)
        {
            // Deliberate: the formula language refuses this too. A tag that starts reading "12A" as 12
            // produces a number nobody can trace back to a mistake.
            error = $"'{s}' is text, and this tag is {dataType} — use tonumber() to convert it on purpose";
            return false;
        }

        if (value is bool flag)
        {
            // true/false into a numeric tag is the one implicit conversion worth having: a status bit
            // summed into a count is exactly what an operator means by it.
            return Scaling.FitsDataType(dataType, flag ? 1 : 0, out typed, out error);
        }

        if (!TryToDouble(value, out var d))
        {
            error = $"a {Describe(value)} cannot be stored in a {dataType} tag";
            return false;
        }

        if (!double.IsFinite(d))
        {
            error = $"{d} is not a finite number";
            return false;
        }

        return Scaling.FitsDataType(dataType, d, out typed, out error);
    }

    /// <summary>
    /// Read a memory tag's configured initial value, written the way an operator would type it
    /// ("0", "false", "auto"). Null or empty means "nothing was configured", which is not an error:
    /// the tag simply starts with no value.
    /// </summary>
    public static bool TryParseInitial(string? text, DataType dataType, out object? value, out string? error)
    {
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (dataType == DataType.String)
        {
            value = trimmed;
            return true;
        }

        if (dataType == DataType.Bool)
        {
            if (TryParseBool(trimmed, out var flag))
            {
                value = flag;
                return true;
            }
            error = $"'{trimmed}' is not true or false";
            return false;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            error = $"'{trimmed}' is not a number";
            return false;
        }

        return Scaling.FitsDataType(dataType, number, out value, out error);
    }

    /// <summary>The invariant text form, so a project file means the same on any machine.</summary>
    public static string AsText(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "on" or "yes": value = true; return true;
            case "false" or "0" or "off" or "no": value = false; return true;
            default: value = false; return false;
        }
    }

    private static string Describe(object value) => value switch
    {
        string => "text",
        bool => "true/false value",
        _ => value.GetType().Name,
    };

    private static bool TryToDouble(object value, out double number)
    {
        switch (value)
        {
            case double x: number = x; return true;
            case float x: number = x; return true;
            case long x: number = x; return true;
            case int x: number = x; return true;
            case short x: number = x; return true;
            case ulong x: number = x; return true;
            case uint x: number = x; return true;
            case ushort x: number = x; return true;
            case sbyte x: number = x; return true;
            case byte x: number = x; return true;
            case decimal x: number = (double)x; return true;
            default: number = 0; return false;
        }
    }
}
