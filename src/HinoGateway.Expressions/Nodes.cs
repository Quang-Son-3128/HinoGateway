using System.Globalization;
using HinoGateway.Core;

namespace HinoGateway.Expressions;

internal sealed class LiteralNode : ExpressionNode
{
    private readonly ExpressionValue _value;
    private readonly string _source;

    public LiteralNode(object? value, string source)
    {
        _value = ExpressionValue.Good(value);
        _source = source;
    }

    public override ExpressionValue Evaluate(EvaluationContext context) => _value;
    public override string ToSource() => _source;
}

/// <summary>
/// A <c>{tag}</c> reference. The only node that can introduce a quality other than Good, which is
/// why every other node's job is simply not to lose it.
/// </summary>
internal sealed class TagNode : ExpressionNode
{
    public TagNode(string tagId) => TagId = tagId;

    public string TagId { get; }

    public override ExpressionValue Evaluate(EvaluationContext context) =>
        context.Tags.TryGet(TagId, out var value)
            ? ExpressionValue.FromTag(TagId, value)
            // Not the same as a bad reading: the tag is not in the store at all. Validation refuses
            // to save such a reference, so seeing this at runtime means the tag was deleted or
            // renamed under a formula that still names it, and saying so beats "bad".
            : ExpressionValue.Bad($"{{{TagId}}} does not exist");

    public override string ToSource() => $"{{{TagId}}}";
}

/// <summary>
/// A local of a command script (<c>var load = …</c>, or the built-in <c>value</c>). Only reachable in
/// script mode: in a formula a bare word is refused by the parser, with a message offering both of
/// the things it was probably meant to be.
/// </summary>
internal sealed class VariableNode : ExpressionNode
{
    private readonly int _position;

    public VariableNode(string name, int position)
    {
        Name = name;
        _position = position;
    }

    public string Name { get; }

    public override ExpressionValue Evaluate(EvaluationContext context) =>
        context.Variables is { } variables && variables.TryGet(Name, out var value)
            ? value
            // The compiler refuses an undeclared name, so this is the belt to that braces: it can
            // only be reached by evaluating a script tree outside a script context.
            : ExpressionValue.Bad($"'{Name}' has no value here");

    public override string ToSource() => Name;
}

internal sealed class UnaryNode : ExpressionNode
{
    private readonly string _op;
    private readonly ExpressionNode _operand;

    public UnaryNode(string op, ExpressionNode operand)
    {
        _op = op;
        _operand = operand;
    }

    public override ExpressionValue Evaluate(EvaluationContext context)
    {
        var value = _operand.Evaluate(context);
        if (!value.IsGood && value.Value is null)
            return value;   // nothing to negate; keep the reason

        switch (_op)
        {
            case "-":
                if (!value.TryAsNumber(out var n))
                    return ExpressionValue.Bad($"cannot negate {value.TypeName()}");
                return ExpressionValue.Derive(-n, value);
            case "+":
                if (!value.TryAsNumber(out var p))
                    return ExpressionValue.Bad($"unary '+' needs a number, got {value.TypeName()}");
                return ExpressionValue.Derive(p, value);
            case "!":
                if (!value.TryAsBool(out var b))
                    return ExpressionValue.Bad($"'!' needs a condition, got {value.TypeName()}");
                return ExpressionValue.Derive(!b, value);
            default:
                return ExpressionValue.Bad($"unknown operator '{_op}'");
        }
    }

    public override string ToSource() => $"{_op}{_operand.ToSource()}";
}

internal sealed class BinaryNode : ExpressionNode
{
    private readonly string _op;
    private readonly ExpressionNode _left;
    private readonly ExpressionNode _right;

    public BinaryNode(string op, ExpressionNode left, ExpressionNode right)
    {
        _op = op;
        _left = left;
        _right = right;
    }

    public override ExpressionValue Evaluate(EvaluationContext context)
    {
        // && and || short-circuit, which is not an optimisation but a correctness rule: an operator
        // writes `{Enabled} && {Flow} > 0` precisely so that Flow is not consulted when disabled.
        if (_op is "&&" or "||")
            return EvaluateLogical(context);

        var left = _left.Evaluate(context);
        var right = _right.Evaluate(context);

        if (_op is "==" or "!=")
            return EvaluateEquality(left, right);

        // A Bad operand has no value to compute with. Report it rather than pretending.
        if (left.Value is null || right.Value is null)
            return ExpressionValue.Bad(FirstReason(left, right) ?? "an input has no value");

        if (_op == "+" && (left.Value is string || right.Value is string))
            return ExpressionValue.Derive(left.AsText() + right.AsText(), left, right);

        if (!left.TryAsNumber(out var a))
            return ExpressionValue.Bad($"'{_op}' needs a number on the left, got {left.TypeName()}");
        if (!right.TryAsNumber(out var b))
            return ExpressionValue.Bad($"'{_op}' needs a number on the right, got {right.TypeName()}");

        switch (_op)
        {
            case "+": return Numeric(a + b, left, right);
            case "-": return Numeric(a - b, left, right);
            case "*": return Numeric(a * b, left, right);
            case "/":
                // Not an exception and not infinity: a divisor that reached zero is a fact about the
                // process (a stopped meter, a missing denominator), and the tag should go Bad with a
                // reason an operator can act on rather than publish ∞ downstream.
                return b == 0
                    ? ExpressionValue.Bad("division by zero")
                    : Numeric(a / b, left, right);
            case "%":
                return b == 0
                    ? ExpressionValue.Bad("remainder by zero")
                    : Numeric(a % b, left, right);
            case "^": return Numeric(Math.Pow(a, b), left, right);
            case "<": return ExpressionValue.Derive(a < b, left, right);
            case "<=": return ExpressionValue.Derive(a <= b, left, right);
            case ">": return ExpressionValue.Derive(a > b, left, right);
            case ">=": return ExpressionValue.Derive(a >= b, left, right);
            default: return ExpressionValue.Bad($"unknown operator '{_op}'");
        }
    }

    /// <summary>
    /// Arithmetic that produced NaN or ±∞ (0^-1, an overflowing product) is refused here rather than
    /// downstream: those are not values a device, a report or an IEC-104 measurand can carry, and
    /// the tag that holds one becomes a puzzle for whoever finds it.
    /// </summary>
    private static ExpressionValue Numeric(double result, ExpressionValue left, ExpressionValue right) =>
        double.IsFinite(result)
            ? ExpressionValue.Derive(result, left, right)
            : ExpressionValue.Bad($"the result is not a finite number ({result})");

    private ExpressionValue EvaluateLogical(EvaluationContext context)
    {
        var left = _left.Evaluate(context);
        if (left.Value is null)
            return ExpressionValue.Bad(left.Status ?? "the left side has no value");
        if (!left.TryAsBool(out var a))
            return ExpressionValue.Bad($"'{_op}' needs a condition on the left, got {left.TypeName()}");

        // The decided case: the right side is never evaluated, so its quality cannot spoil a result
        // it had no part in.
        if (_op == "&&" && !a) return ExpressionValue.Derive(false, left);
        if (_op == "||" && a) return ExpressionValue.Derive(true, left);

        var right = _right.Evaluate(context);
        if (right.Value is null)
            return ExpressionValue.Bad(right.Status ?? "the right side has no value");
        if (!right.TryAsBool(out var b))
            return ExpressionValue.Bad($"'{_op}' needs a condition on the right, got {right.TypeName()}");
        return ExpressionValue.Derive(b, left, right);
    }

    private ExpressionValue EvaluateEquality(ExpressionValue left, ExpressionValue right)
    {
        var equal = (left.Value, right.Value) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal),
            (string, _) or (_, string) => false,
            _ when left.TryAsNumber(out var a) && right.TryAsNumber(out var b) => a == b,
            _ => Equals(left.Value, right.Value),
        };
        return ExpressionValue.Derive(_op == "==" ? equal : !equal, left, right);
    }

    private static string? FirstReason(ExpressionValue left, ExpressionValue right) =>
        left.Value is null ? left.Status : right.Status;

    public override string ToSource() => $"({_left.ToSource()} {_op} {_right.ToSource()})";
}

/// <summary>
/// <c>condition ? a : b</c>. Only the taken branch is evaluated, so the branch not chosen can be
/// Bad without affecting anything — which is exactly what makes it usable as a fallback.
/// </summary>
internal sealed class ConditionalNode : ExpressionNode
{
    private readonly ExpressionNode _condition;
    private readonly ExpressionNode _whenTrue;
    private readonly ExpressionNode _whenFalse;

    public ConditionalNode(ExpressionNode condition, ExpressionNode whenTrue, ExpressionNode whenFalse)
    {
        _condition = condition;
        _whenTrue = whenTrue;
        _whenFalse = whenFalse;
    }

    public override ExpressionValue Evaluate(EvaluationContext context)
    {
        var condition = _condition.Evaluate(context);
        if (condition.Value is null)
            return ExpressionValue.Bad(condition.Status ?? "the condition has no value");
        if (!condition.TryAsBool(out var flag))
            return ExpressionValue.Bad($"the condition must be true/false, got {condition.TypeName()}");

        var branch = (flag ? _whenTrue : _whenFalse).Evaluate(context);
        // The condition's own quality still counts: choosing a branch on an uncertain input makes
        // the answer uncertain, however good the branch itself was.
        return ExpressionValue.Derive(branch.Value, condition, branch);
    }

    public override string ToSource() =>
        $"({_condition.ToSource()} ? {_whenTrue.ToSource()} : {_whenFalse.ToSource()})";
}

/// <summary>A call to one of the built-in functions; see <see cref="Functions"/>.</summary>
internal sealed class FunctionNode : ExpressionNode
{
    private readonly FunctionDefinition _function;
    private readonly ExpressionNode[] _arguments;

    public FunctionNode(FunctionDefinition function, ExpressionNode[] arguments)
    {
        _function = function;
        _arguments = arguments;
    }

    public override ExpressionValue Evaluate(EvaluationContext context)
    {
        try
        {
            return _function.Invoke(_arguments, context);
        }
        catch (Exception ex)
        {
            // A built-in that throws is a bug in this gateway, not in the operator's formula. It must
            // still cost only the one tag: the rest of the expressions carry on being evaluated.
            return ExpressionValue.Bad($"{_function.Name}() failed: {ex.Message}");
        }
    }

    public override string ToSource() =>
        $"{_function.Name}({string.Join(", ", _arguments.Select(a => a.ToSource()))})";
}

/// <summary>
/// <c>prev(x)</c> — what <paramref name="x"/> was at this tag's previous evaluation. Its own node
/// rather than a function because it needs the slot assigned to it at compile time.
/// </summary>
internal sealed class PreviousNode : ExpressionNode
{
    private readonly ExpressionNode _operand;
    private readonly int _slot;

    public PreviousNode(ExpressionNode operand, int slot)
    {
        _operand = operand;
        _slot = slot;
    }

    public override ExpressionValue Evaluate(EvaluationContext context)
    {
        // Evaluated every time even when the previous value is what gets returned: skipping it would
        // mean this evaluation stages nothing, and the "previous" value would then be two, three or
        // twenty evaluations old without anything saying so.
        var current = _operand.Evaluate(context);
        context.Memory?.Stage(_slot, current);

        if (context.Memory?.Read(_slot) is not { } previous)
            return ExpressionValue.Bad("prev() has no previous value yet");
        return previous;
    }

    public override string ToSource() => $"prev({_operand.ToSource()})";
}

/// <summary>Formats a number the way the rest of the gateway does, for text building.</summary>
internal static class NumberText
{
    public static string Invariant(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
