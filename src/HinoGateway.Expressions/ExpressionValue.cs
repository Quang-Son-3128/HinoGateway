using System.Globalization;
using HinoGateway.Core;

namespace HinoGateway.Expressions;

/// <summary>
/// One intermediate result inside an expression: a value <em>and the quality it inherited</em>.
///
/// <para>
/// Carrying quality through every node is the single design decision this evaluator is built
/// around, and it is what no general-purpose expression library can do. A gateway's whole job is to
/// be honest about what it knows, and the moment a formula reduces two readings to one number, the
/// question "is this number trustworthy" has to be answered by the formula itself. If
/// <c>{P1} + {P2}</c> silently treated a dead P2 as zero, the plant total would read half of the
/// real power and every alarm, report and northbound point built on it would be confidently wrong —
/// which is worse than reporting nothing, because nothing is visibly nothing.
/// </para>
///
/// <para>
/// So arithmetic propagates the <em>worst</em> quality of its operands, and the only way to stop
/// that propagation is to say so explicitly (<c>coalesce</c>, <c>forcegood</c>, or testing with
/// <c>bad()</c> and choosing a fallback). Deciding to trust a stale number is a decision an engineer
/// makes and writes down, not a default.
/// </para>
/// </summary>
public readonly record struct ExpressionValue
{
    private ExpressionValue(object? value, Quality quality, string? status)
    {
        Value = value;
        Quality = quality;
        Status = status;
    }

    /// <summary>A <see cref="double"/>, <see cref="bool"/>, <see cref="string"/>, or null when Bad.</summary>
    public object? Value { get; }

    public Quality Quality { get; }

    /// <summary>Why the quality is not Good — carried to the tag's <see cref="TagValue.StatusText"/>.</summary>
    public string? Status { get; }

    public bool IsGood => Quality == Quality.Good;

    public static ExpressionValue Good(double value) => new(value, Quality.Good, null);
    public static ExpressionValue Good(bool value) => new(value, Quality.Good, null);
    public static ExpressionValue Good(string value) => new(value, Quality.Good, null);
    public static ExpressionValue Good(object? value) => new(value, Quality.Good, null);

    public static ExpressionValue Bad(string reason) => new(null, Quality.Bad, reason);

    public static ExpressionValue Uncertain(object? value, string? reason) =>
        new(value, Quality.Uncertain, reason);

    /// <summary>Rebuild a result from a tag reading, keeping its quality and reason.</summary>
    public static ExpressionValue FromTag(string tagId, TagValue value) => value.Quality switch
    {
        Quality.Good => new ExpressionValue(Normalise(value.Value), Quality.Good, null),
        Quality.Uncertain => new ExpressionValue(Normalise(value.Value), Quality.Uncertain,
            value.StatusText is { Length: > 0 } s ? $"{{{tagId}}} uncertain: {s}" : $"{{{tagId}}} is uncertain"),
        _ => Bad(value.StatusText is { Length: > 0 } s ? $"{{{tagId}}} bad: {s}" : $"{{{tagId}}} is bad"),
    };

    /// <summary>
    /// Combine the qualities of the operands of one operation. Bad wins over Uncertain wins over
    /// Good — the enum is ordered Bad(0) &lt; Uncertain(1) &lt; Good(2), so the worst is the lowest —
    /// and the reason kept is the one belonging to the quality that won, so the operator is told
    /// which input spoiled the result rather than that "something" did.
    /// </summary>
    public static (Quality Quality, string? Status) Worst(params ExpressionValue[] operands)
    {
        var quality = Quality.Good;
        string? status = null;
        foreach (var operand in operands)
        {
            if (operand.Quality >= quality)
                continue;
            quality = operand.Quality;
            status = operand.Status;
        }
        return (quality, status);
    }

    /// <summary>Attach the combined quality of <paramref name="operands"/> to a freshly computed value.</summary>
    public static ExpressionValue Derive(object? value, params ExpressionValue[] operands)
    {
        var (quality, status) = Worst(operands);
        return quality == Quality.Bad
            ? new ExpressionValue(null, Quality.Bad, status ?? "an input is bad")
            : new ExpressionValue(value, quality, status);
    }

    // --- coercion -------------------------------------------------------------
    // Values arrive from tags as any numeric CLR type; inside an expression there are exactly three
    // types (number, boolean, text) so that operators need three cases and not thirty.

    /// <summary>Collapse a driver's CLR type onto the evaluator's double/bool/string.</summary>
    public static object? Normalise(object? value) => value switch
    {
        null => null,
        double or bool or string => value,
        float x => (double)x,
        int x => (double)x,
        long x => (double)x,
        short x => (double)x,
        uint x => (double)x,
        ulong x => (double)x,
        ushort x => (double)x,
        byte x => (double)x,
        sbyte x => (double)x,
        decimal x => (double)x,
        DateTimeOffset x => x.ToUnixTimeMilliseconds() / 1000.0,
        DateTime x => new DateTimeOffset(x).ToUnixTimeMilliseconds() / 1000.0,
        _ => value.ToString(),
    };

    /// <summary>
    /// The value as a number. A boolean counts as 1/0 — an operator writing <c>{Run} * {Power}</c>
    /// means exactly what it looks like — but text does <b>not</b> parse itself into a number:
    /// silently reading "12A" as 12 is how a wrong number gets into a report unnoticed. Use
    /// <c>tonumber()</c> to say the conversion is intended.
    /// </summary>
    public bool TryAsNumber(out double number)
    {
        switch (Value)
        {
            case double d: number = d; return true;
            case bool b: number = b ? 1 : 0; return true;
            default: number = 0; return false;
        }
    }

    /// <summary>
    /// The value as a condition. Numbers are true when non-zero (0 is false, as in every PLC), and
    /// text is not a condition at all.
    /// </summary>
    public bool TryAsBool(out bool flag)
    {
        switch (Value)
        {
            case bool b: flag = b; return true;
            case double d: flag = d != 0; return true;
            default: flag = false; return false;
        }
    }

    /// <summary>Text for display and for the string functions; invariant so a comma locale cannot change it.</summary>
    public string AsText() => Value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>Type name used in error messages, in the language of the expression rather than of C#.</summary>
    public string TypeName() => Value switch
    {
        double => "number",
        bool => "boolean",
        string => "text",
        null => "nothing",
        _ => Value.GetType().Name,
    };
}
