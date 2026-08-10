using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HinoGateway.Core;

namespace HinoGateway.Expressions;

/// <summary>
/// One built-in function: what it is called, how many arguments it takes, and what it does.
///
/// <para>
/// <see cref="Invoke"/> receives the argument <em>nodes</em> rather than their values, because
/// several of these functions must decide whether an argument is evaluated at all — <c>if()</c>
/// evaluates one branch, <c>coalesce()</c> stops at the first good input, and <c>age()</c> needs the
/// tag reference itself and not the number it produced. Functions that simply need values call
/// <c>Evaluate</c> on every argument first, which is what the helpers in <see cref="Functions"/> do.
/// </para>
///
/// <para>
/// <see cref="Signature"/> and <see cref="Help"/> exist so the formula editor can show the same
/// description the evaluator actually implements. Keeping them on the definition is what stops the
/// UI's function list from drifting away from the code, the way a hand-written help page always does.
/// </para>
/// </summary>
public sealed class FunctionDefinition
{
    internal FunctionDefinition(
        string name,
        string category,
        int minArgs,
        int maxArgs,
        string signature,
        string help,
        Func<ExpressionNode[], EvaluationContext, ExpressionValue> invoke,
        Func<ExpressionNode[], string?>? checkArguments = null)
    {
        Name = name;
        Category = category;
        MinArgs = minArgs;
        MaxArgs = maxArgs;
        Signature = signature;
        Help = help;
        Invoke = invoke;
        CheckArguments = checkArguments;
    }

    public string Name { get; }

    /// <summary>Grouping for the editor's function list ("Math", "Quality", "Time", …).</summary>
    public string Category { get; }

    public int MinArgs { get; }

    /// <summary><see cref="int.MaxValue"/> for a function that takes any number of arguments.</summary>
    public int MaxArgs { get; }

    /// <summary>How the call is written, e.g. <c>round(x[, digits])</c>.</summary>
    public string Signature { get; }

    /// <summary>One sentence, in the language of the person writing the formula.</summary>
    public string Help { get; }

    public Func<ExpressionNode[], EvaluationContext, ExpressionValue> Invoke { get; }

    /// <summary>
    /// An extra check the parser runs on the argument <em>expressions</em>, returning an error
    /// message or null. Only <c>age()</c> needs one so far: it reads a tag's source timestamp, which
    /// an <see cref="ExpressionValue"/> does not carry, so its argument has to be a tag reference and
    /// the operator deserves to be told that while typing rather than by a Bad tag at runtime.
    /// </summary>
    public Func<ExpressionNode[], string?>? CheckArguments { get; }

    public bool IsVariadic => MaxArgs == int.MaxValue;

    /// <summary>Phrase describing the arity, for the parser's error message.</summary>
    public string DescribeArity() =>
        MaxArgs == int.MaxValue ? $"at least {Count(MinArgs)}"
        : MinArgs == MaxArgs ? $"exactly {Count(MinArgs)}"
        : $"between {MinArgs} and {MaxArgs} arguments";

    private static string Count(int n) => n == 1 ? "1 argument" : $"{n} arguments";
}

/// <summary>
/// The built-in function table.
///
/// <para>
/// Deliberately a fixed set rather than anything extensible: a formula is stored in a project file
/// that has to mean the same thing on the engineer's laptop and on the industrial PC at site, and a
/// per-installation function library would break that quietly. Adding a function here is a code
/// change, reviewed and tested, which is the right weight for something every derived value in a
/// plant may end up depending on.
/// </para>
///
/// <para>
/// Note what is <em>not</em> here: <c>prev()</c>. It needs a memory slot allocated when the
/// expression is compiled, so the parser recognises the name itself and builds a
/// <see cref="PreviousNode"/> before ever consulting this table.
/// </para>
/// </summary>
public static class Functions
{
    private static readonly Dictionary<string, FunctionDefinition> Table;

    static Functions()
    {
        var all = new List<FunctionDefinition>();
        all.AddRange(Math());
        all.AddRange(Logic());
        all.AddRange(QualityFunctions());
        all.AddRange(Types());
        all.AddRange(Text());
        all.AddRange(Time());
        all.AddRange(Special());

        Table = all.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        All = all;
    }

    /// <summary>Every function, in the order they are presented to the operator.</summary>
    public static IReadOnlyList<FunctionDefinition> All { get; }

    /// <summary>Case-insensitive: <c>ABS</c> and <c>abs</c> are the same function.</summary>
    public static bool TryGet(string name, [MaybeNullWhen(false)] out FunctionDefinition function) =>
        Table.TryGetValue(name, out function);

    // =====================================================================================
    //  Maths
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> Math()
    {
        yield return Numeric1("abs", "the value without its sign", System.Math.Abs);
        yield return Numeric1("sqrt", "square root", System.Math.Sqrt,
            x => x < 0 ? "the square root of a negative number is not a real number" : null);
        yield return Numeric1("exp", "e raised to x", System.Math.Exp);
        yield return Numeric1("ln", "natural logarithm (base e)", System.Math.Log,
            x => x <= 0 ? "the logarithm needs a value greater than zero" : null);
        yield return Numeric1("log10", "logarithm base 10", System.Math.Log10,
            x => x <= 0 ? "the logarithm needs a value greater than zero" : null);
        yield return Numeric1("sin", "sine of an angle in radians — use rad() for degrees", System.Math.Sin);
        yield return Numeric1("cos", "cosine of an angle in radians", System.Math.Cos);
        yield return Numeric1("tan", "tangent of an angle in radians", System.Math.Tan);
        yield return Numeric1("asin", "arc sine, in radians", System.Math.Asin,
            x => x is < -1 or > 1 ? "asin needs a value between -1 and 1" : null);
        yield return Numeric1("acos", "arc cosine, in radians", System.Math.Acos,
            x => x is < -1 or > 1 ? "acos needs a value between -1 and 1" : null);
        yield return Numeric1("atan", "arc tangent, in radians", System.Math.Atan);
        yield return Numeric1("floor", "the largest whole number that is not greater than x", System.Math.Floor);
        yield return Numeric1("ceil", "the smallest whole number that is not less than x", System.Math.Ceiling);
        yield return Numeric1("trunc", "x with the fractional part cut off (towards zero)", System.Math.Truncate);
        yield return Numeric1("sign", "-1, 0 or 1 according to the sign of x", x => System.Math.Sign(x));
        yield return Numeric1("deg", "radians converted to degrees", x => x * 180.0 / System.Math.PI);
        yield return Numeric1("rad", "degrees converted to radians", x => x * System.Math.PI / 180.0);

        yield return Numeric2("pow", "pow(x, y)", "x raised to the power y", System.Math.Pow);
        yield return Numeric2("atan2", "atan2(y, x)",
            "the angle in radians of the point (x, y) — unlike atan it knows the quadrant",
            System.Math.Atan2);
        yield return Numeric2("log", "log(x, base)", "logarithm of x in the given base",
            (x, b) => System.Math.Log(x, b),
            (x, b) => x <= 0 ? "the logarithm needs a value greater than zero"
                : b is <= 0 or 1 ? "the base must be greater than zero and not 1"
                : null);

        yield return new FunctionDefinition("round", "Math", 1, 2, "round(x[, digits])",
            "x rounded to the given number of decimals (0 by default); .5 always rounds away from zero",
            (args, ctx) =>
            {
                var values = EvaluateAll(args, ctx);
                if (!TryNumbers("round", values, out var numbers, out var error))
                    return error;
                var digits = 0;
                if (numbers.Length == 2)
                {
                    digits = (int)numbers[1];
                    if (digits is < 0 or > 15)
                        return ExpressionValue.Bad("round(): the number of decimals must be between 0 and 15");
                }
                // Away from zero, not the .NET default: an engineer reading 2.5 expects 3, and
                // banker's rounding turning it into 2 is the kind of surprise that gets blamed on the
                // instrument rather than on the formula.
                return Finite("round", System.Math.Round(numbers[0], digits, MidpointRounding.AwayFromZero), values);
            });

        yield return Aggregate("min", "the smallest of its arguments", values => values.Min());
        yield return Aggregate("max", "the largest of its arguments", values => values.Max());
        yield return Aggregate("sum", "the total of its arguments", values => values.Sum());
        yield return Aggregate("avg", "the average of its arguments", values => values.Average());

        yield return new FunctionDefinition("clamp", "Math", 3, 3, "clamp(x, low, high)",
            "x held inside the range low…high",
            (args, ctx) =>
            {
                var values = EvaluateAll(args, ctx);
                if (!TryNumbers("clamp", values, out var n, out var error))
                    return error;
                if (n[1] > n[2])
                    return ExpressionValue.Bad("clamp(): the low limit is above the high limit");
                return Finite("clamp", System.Math.Clamp(n[0], n[1], n[2]), values);
            });

        yield return new FunctionDefinition("scale", "Math", 5, 5, "scale(x, inLow, inHigh, outLow, outHigh)",
            "x mapped linearly from one range onto another, e.g. 4-20 mA onto 0-100 %",
            (args, ctx) =>
            {
                var values = EvaluateAll(args, ctx);
                if (!TryNumbers("scale", values, out var n, out var error))
                    return error;
                var span = n[2] - n[1];
                if (span == 0)
                    return ExpressionValue.Bad("scale(): the input range is empty (inLow equals inHigh)");
                return Finite("scale", (n[0] - n[1]) / span * (n[4] - n[3]) + n[3], values);
            });
    }

    // =====================================================================================
    //  Logic
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> Logic()
    {
        yield return new FunctionDefinition("if", "Logic", 3, 3, "if(condition, whenTrue, whenFalse)",
            "the second argument when the condition is true, the third when it is false",
            (args, ctx) =>
            {
                var condition = args[0].Evaluate(ctx);
                if (condition.Value is null)
                    return ExpressionValue.Bad(condition.Status ?? "if(): the condition has no value");
                if (!condition.TryAsBool(out var flag))
                    return ExpressionValue.Bad($"if(): the condition must be true/false, got {condition.TypeName()}");

                // Only the chosen branch is evaluated, so the other one may be Bad without spoiling
                // anything — that is what makes if() usable as a fallback. The condition's own quality
                // still counts: a choice made on an uncertain input gives an uncertain answer.
                var branch = (flag ? args[1] : args[2]).Evaluate(ctx);
                return ExpressionValue.Derive(branch.Value, condition, branch);
            });
    }

    // =====================================================================================
    //  Quality — the part that has no equivalent in a general-purpose expression library
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> QualityFunctions()
    {
        // These four return Good whatever their argument's quality was: they are questions *about*
        // quality, and a question about a dead sensor still has a perfectly good answer. That
        // absorption is precisely what makes `if(bad({T1}), {T2}, {T1})` work — without it the
        // condition itself would drag the result to Bad and the fallback could never be reached.
        yield return Inspect("bad", "Quality", "true when the argument's quality is Bad",
            value => value.Quality == Quality.Bad);
        yield return Inspect("good", "Quality", "true when the argument's quality is Good",
            value => value.Quality == Quality.Good);
        yield return Inspect("uncertain", "Quality", "true when the argument's quality is Uncertain",
            value => value.Quality == Quality.Uncertain);
        yield return Inspect("quality", "Quality",
            "the argument's quality as a number: 0 = Bad, 1 = Uncertain, 2 = Good",
            value => value.Quality switch
            {
                Quality.Good => 2.0,
                Quality.Uncertain => 1.0,
                _ => 0.0,
            });

        yield return new FunctionDefinition("coalesce", "Quality", 2, int.MaxValue, "coalesce(a, b, …)",
            "the first argument whose quality is Good — a redundant sensor, then its stand-by, then a constant",
            (args, ctx) =>
            {
                ExpressionValue last = default;
                foreach (var argument in args)
                {
                    last = argument.Evaluate(ctx);
                    // Stops at the first good one, so the stand-by sources cost nothing while the
                    // primary is healthy.
                    if (last.IsGood)
                        return last;
                }
                return ExpressionValue.Bad(
                    $"coalesce(): none of the {args.Length} inputs is good — {last.Status ?? "the last one has no reason recorded"}");
            });

        yield return new FunctionDefinition("forcegood", "Quality", 1, 2, "forcegood(x[, fallback])",
            "x with its quality forced to Good — the deliberate way to say a stale or doubtful value may be used anyway",
            (args, ctx) =>
            {
                var value = args[0].Evaluate(ctx);
                if (value.Value is not null)
                    return ExpressionValue.Good(value.Value);

                // Bad carries no value at all, so there is nothing to force; the fallback is the only
                // way this can still produce an answer.
                if (args.Length == 2)
                {
                    var fallback = args[1].Evaluate(ctx);
                    if (fallback.Value is not null)
                        return ExpressionValue.Good(fallback.Value);
                }
                return ExpressionValue.Bad(
                    $"forcegood(): there is no value to keep — {value.Status ?? "the input is bad"}");
            });
    }

    // =====================================================================================
    //  Types
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> Types()
    {
        yield return new FunctionDefinition("tonumber", "Type", 1, 2, "tonumber(x[, fallback])",
            "text (or true/false) read as a number; the fallback is used when the text is not a number",
            (args, ctx) =>
            {
                var value = args[0].Evaluate(ctx);
                if (value.Value is null)
                    return ExpressionValue.Bad(value.Status ?? "tonumber(): there is no value to convert");
                if (value.TryAsNumber(out var number))
                    return ExpressionValue.Derive(number, value);
                if (value.Value is string text &&
                    double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    return ExpressionValue.Derive(parsed, value);

                // The fallback covers text that is not a number. It deliberately does NOT cover a Bad
                // input: hiding a dead sensor behind a default is a quality decision, and that is what
                // coalesce() and forcegood() are for.
                if (args.Length == 2)
                {
                    var fallback = args[1].Evaluate(ctx);
                    if (!TryNumber("tonumber", 2, fallback, out var alternative, out var error))
                        return error;
                    return ExpressionValue.Derive(alternative, value, fallback);
                }
                return ExpressionValue.Bad($"tonumber(): '{value.AsText()}' is not a number");
            });

        yield return new FunctionDefinition("tobool", "Type", 1, 1, "tobool(x)",
            "x as true/false — a number is true when it is not zero, and the words true/false, 1/0, on/off, yes/no are understood",
            (args, ctx) =>
            {
                var value = args[0].Evaluate(ctx);
                if (value.Value is null)
                    return ExpressionValue.Bad(value.Status ?? "tobool(): there is no value to convert");
                if (value.TryAsBool(out var flag))
                    return ExpressionValue.Derive(flag, value);
                var text = value.AsText().Trim().ToLowerInvariant();
                return text switch
                {
                    "true" or "1" or "on" or "yes" => ExpressionValue.Derive(true, value),
                    "false" or "0" or "off" or "no" => ExpressionValue.Derive(false, value),
                    _ => ExpressionValue.Bad($"tobool(): '{value.AsText()}' is not true or false"),
                };
            });

        yield return new FunctionDefinition("totext", "Type", 1, 1, "totext(x)",
            "x written out as text",
            (args, ctx) =>
            {
                var value = args[0].Evaluate(ctx);
                if (value.Value is null)
                    return ExpressionValue.Bad(value.Status ?? "totext(): there is no value to convert");
                return ExpressionValue.Derive(value.AsText(), value);
            });

        yield return Inspect("isnumber", "Type", "true when x is a number",
            value => value.Value is double or bool);
        yield return Inspect("istext", "Type", "true when x is text", value => value.Value is string);
    }

    // =====================================================================================
    //  Text
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> Text()
    {
        yield return Text1("len", "len(s)", "how many characters s has", s => (double)s.Length);
        yield return Text1("upper", "upper(s)", "s in upper case", s => s.ToUpperInvariant());
        yield return Text1("lower", "lower(s)", "s in lower case", s => s.ToLowerInvariant());
        yield return Text1("trim", "trim(s)", "s without leading and trailing spaces", s => s.Trim());

        yield return new FunctionDefinition("substr", "Text", 2, 3, "substr(s, start[, length])",
            "part of s, counting the first character as 1; without a length it runs to the end",
            (args, ctx) =>
            {
                var values = EvaluateAll(args, ctx);
                if (!TryText("substr", 1, values[0], out var text, out var error))
                    return error;
                if (!TryNumber("substr", 2, values[1], out var startNumber, out error))
                    return error;

                // 1-based, like a spreadsheet's MID and like every operator's mental model of "the
                // third character". Out-of-range never throws: asking for characters past the end of a
                // shorter-than-expected string is a normal thing for a device to cause, and an empty
                // answer beats a Bad tag.
                var start = (int)startNumber;
                if (start < 1)
                    return ExpressionValue.Bad("substr(): the first character is 1");
                var offset = System.Math.Min(start - 1, text.Length);
                var available = text.Length - offset;
                var length = available;
                if (values.Length == 3)
                {
                    if (!TryNumber("substr", 3, values[2], out var lengthNumber, out error))
                        return error;
                    if (lengthNumber < 0)
                        return ExpressionValue.Bad("substr(): the length cannot be negative");
                    length = System.Math.Min((int)lengthNumber, available);
                }
                return ExpressionValue.Derive(text.Substring(offset, length), values);
            });

        yield return Text2("contains", "contains(s, part)", "true when s contains part",
            (s, part) => s.Contains(part, StringComparison.Ordinal));
        yield return Text2("startswith", "startswith(s, prefix)", "true when s begins with prefix",
            (s, prefix) => s.StartsWith(prefix, StringComparison.Ordinal));
        yield return Text2("endswith", "endswith(s, suffix)", "true when s ends with suffix",
            (s, suffix) => s.EndsWith(suffix, StringComparison.Ordinal));

        yield return new FunctionDefinition("replace", "Text", 3, 3, "replace(s, find, with)",
            "s with every occurrence of find replaced",
            (args, ctx) =>
            {
                var values = EvaluateAll(args, ctx);
                if (!TryText("replace", 1, values[0], out var text, out var error))
                    return error;
                if (!TryText("replace", 2, values[1], out var find, out error))
                    return error;
                if (!TryText("replace", 3, values[2], out var with, out error))
                    return error;
                if (find.Length == 0)
                    return ExpressionValue.Bad("replace(): there is nothing to find");
                return ExpressionValue.Derive(text.Replace(find, with, StringComparison.Ordinal), values);
            });

        yield return new FunctionDefinition("concat", "Text", 1, int.MaxValue, "concat(a, b, …)",
            "its arguments joined into one piece of text",
            (args, ctx) =>
            {
                var values = EvaluateAll(args, ctx);
                for (var i = 0; i < values.Length; i++)
                {
                    if (!TryText("concat", i + 1, values[i], out _, out var error))
                        return error;
                }
                return ExpressionValue.Derive(string.Concat(values.Select(v => v.AsText())), values);
            });
    }

    // =====================================================================================
    //  Time — local time throughout, because a shift, a tariff window and a daily total are all
    //  local facts. The instant comes from the evaluation context, so every time function inside one
    //  evaluation agrees with every other one, and tests can pin it.
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> Time()
    {
        yield return Clock("now", "the moment of this evaluation, in seconds since 1970 (useful for differences)",
            now => now.ToUnixTimeMilliseconds() / 1000.0);
        yield return Clock("hour", "the hour of the local clock, 0-23", now => now.Hour);
        yield return Clock("minute", "the minute of the local clock, 0-59", now => now.Minute);
        yield return Clock("second", "the second of the local clock, 0-59", now => now.Second);
        yield return Clock("day", "the day of the month, 1-31", now => now.Day);
        yield return Clock("month", "the month, 1-12", now => now.Month);
        yield return Clock("year", "the year", now => now.Year);
        yield return Clock("dayofweek", "the day of the week, 1 = Monday … 7 = Sunday",
            now => now.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)now.DayOfWeek);
        yield return Clock("dayofyear", "the day of the year, 1-366", now => now.DayOfYear);
        yield return Clock("secondofday", "seconds since local midnight, 0-86399",
            now => now.Hour * 3600 + now.Minute * 60 + now.Second);

        yield return new FunctionDefinition("timestamp", "Time", 0, 1, "timestamp([format])",
            "the moment of this evaluation as local text, by default yyyy-MM-dd HH:mm:ss",
            (args, ctx) =>
            {
                var format = "yyyy-MM-dd HH:mm:ss";
                if (args.Length == 1)
                {
                    var value = args[0].Evaluate(ctx);
                    if (!TryText("timestamp", 1, value, out format, out var error))
                        return error;
                }
                try
                {
                    return ExpressionValue.Good(ctx.Now.ToString(format, CultureInfo.InvariantCulture));
                }
                catch (FormatException)
                {
                    return ExpressionValue.Bad($"timestamp(): '{format}' is not a valid date format");
                }
            });
    }

    // =====================================================================================
    //  Special
    // =====================================================================================

    private static IEnumerable<FunctionDefinition> Special()
    {
        yield return new FunctionDefinition("dt", "Special", 0, 0, "dt()",
            "seconds since this tag was last evaluated — the divisor of every rate calculation",
            (_, ctx) =>
            {
                // Bad on the first evaluation rather than 0: a rate computed by dividing by zero
                // elapsed time is not a small error, and a rate computed by assuming one second when
                // the real interval was unknown is a plausible-looking lie.
                if (ctx.Memory?.SecondsSinceLast(ctx.Now) is not { } seconds)
                    return ExpressionValue.Bad("dt() has no previous evaluation yet");
                return ExpressionValue.Good(seconds);
            });

        yield return new FunctionDefinition("age", "Special", 1, 1, "age({tag})",
            "seconds since that tag was last sampled at the device — age({T}) > 60 finds a stale reading",
            (args, ctx) =>
            {
                if (args[0] is not TagNode tag)
                    return ExpressionValue.Bad("age() needs a tag reference, e.g. age({Plant/P1})");
                if (!ctx.Tags.TryGet(tag.TagId, out var value))
                    return ExpressionValue.Bad($"{{{tag.TagId}}} does not exist");

                // Good whatever the tag's quality is: how old a reading is remains a fact even when
                // the reading itself is worthless, and that is exactly the case one wants to test.
                return ExpressionValue.Good((ctx.Now - value.SourceTimestamp).TotalSeconds);
            },
            checkArguments: args => args[0] is TagNode
                ? null
                : "age() needs a tag reference in braces, e.g. age({Plant/P1}) — it reads that tag's timestamp, which a computed value does not have");
    }

    // =====================================================================================
    //  Shared plumbing
    // =====================================================================================

    private static ExpressionValue[] EvaluateAll(ExpressionNode[] args, EvaluationContext context)
    {
        var values = new ExpressionValue[args.Length];
        for (var i = 0; i < args.Length; i++)
            values[i] = args[i].Evaluate(context);
        return values;
    }

    private static bool TryNumber(
        string name, int position, ExpressionValue value, out double number, out ExpressionValue error)
    {
        if (value.Value is null)
        {
            number = 0;
            error = ExpressionValue.Bad(value.Status ?? $"{name}(): argument {position} has no value");
            return false;
        }
        if (!value.TryAsNumber(out number))
        {
            error = ExpressionValue.Bad(
                $"{name}(): argument {position} must be a number, got {value.TypeName()}");
            return false;
        }
        error = default;
        return true;
    }

    private static bool TryNumbers(
        string name, ExpressionValue[] values, out double[] numbers, out ExpressionValue error)
    {
        numbers = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            if (!TryNumber(name, i + 1, values[i], out numbers[i], out error))
                return false;
        }
        error = default;
        return true;
    }

    private static bool TryText(
        string name, int position, ExpressionValue value, out string text, out ExpressionValue error)
    {
        if (value.Value is null)
        {
            text = "";
            error = ExpressionValue.Bad(value.Status ?? $"{name}(): argument {position} has no value");
            return false;
        }
        text = value.AsText();
        error = default;
        return true;
    }

    /// <summary>
    /// A computed number, refused when it came out NaN or infinite. Same rule as the arithmetic
    /// operators: those are not values a device register, a report or an IEC-104 measurand can carry.
    /// </summary>
    private static ExpressionValue Finite(string name, double result, params ExpressionValue[] operands) =>
        double.IsFinite(result)
            ? ExpressionValue.Derive(result, operands)
            : ExpressionValue.Bad($"{name}(): the result is not a finite number ({result})");

    private static FunctionDefinition Numeric1(
        string name, string help, Func<double, double> compute, Func<double, string?>? domain = null) =>
        new(name, "Math", 1, 1, $"{name}(x)", help, (args, ctx) =>
        {
            var value = args[0].Evaluate(ctx);
            if (!TryNumber(name, 1, value, out var x, out var error))
                return error;
            if (domain?.Invoke(x) is { } complaint)
                return ExpressionValue.Bad($"{name}(): {complaint}");
            return Finite(name, compute(x), value);
        });

    private static FunctionDefinition Numeric2(
        string name, string signature, string help,
        Func<double, double, double> compute, Func<double, double, string?>? domain = null) =>
        new(name, "Math", 2, 2, signature, help, (args, ctx) =>
        {
            var values = EvaluateAll(args, ctx);
            if (!TryNumbers(name, values, out var n, out var error))
                return error;
            if (domain?.Invoke(n[0], n[1]) is { } complaint)
                return ExpressionValue.Bad($"{name}(): {complaint}");
            return Finite(name, compute(n[0], n[1]), values);
        });

    private static FunctionDefinition Aggregate(string name, string help, Func<double[], double> compute) =>
        new(name, "Math", 1, int.MaxValue, $"{name}(a, b, …)", help, (args, ctx) =>
        {
            var values = EvaluateAll(args, ctx);
            if (!TryNumbers(name, values, out var numbers, out var error))
                return error;
            return Finite(name, compute(numbers), values);
        });

    /// <summary>
    /// A function that asks a question about its argument instead of computing with it, and whose
    /// answer is therefore always Good. Grouped here so that the absorbing behaviour is written once
    /// and is impossible to give to a function by accident.
    /// </summary>
    private static FunctionDefinition Inspect(
        string name, string category, string help, Func<ExpressionValue, object> answer) =>
        new(name, category, 1, 1, $"{name}(x)", help,
            (args, ctx) => ExpressionValue.Good(answer(args[0].Evaluate(ctx))));

    private static FunctionDefinition Text1(
        string name, string signature, string help, Func<string, object> compute) =>
        new(name, "Text", 1, 1, signature, help, (args, ctx) =>
        {
            var value = args[0].Evaluate(ctx);
            if (!TryText(name, 1, value, out var text, out var error))
                return error;
            return ExpressionValue.Derive(compute(text), value);
        });

    private static FunctionDefinition Text2(
        string name, string signature, string help, Func<string, string, object> compute) =>
        new(name, "Text", 2, 2, signature, help, (args, ctx) =>
        {
            var values = EvaluateAll(args, ctx);
            if (!TryText(name, 1, values[0], out var first, out var error))
                return error;
            if (!TryText(name, 2, values[1], out var second, out error))
                return error;
            return ExpressionValue.Derive(compute(first, second), values);
        });

    private static FunctionDefinition Clock(string name, string help, Func<DateTimeOffset, double> read) =>
        new(name, "Time", 0, 0, $"{name}()", help, (_, ctx) => ExpressionValue.Good(read(ctx.Now)));
}
