using HinoGateway.Core;

namespace HinoGateway.Expressions;

/// <summary>
/// Everything one evaluation of one expression tag is allowed to see. Passed by reference through
/// the tree so a node never reaches for ambient state — which is what makes the evaluator testable
/// against a dictionary and a fixed clock, and what makes <c>now()</c> return the same instant
/// everywhere inside a single evaluation instead of drifting between two uses of it.
/// </summary>
public sealed class EvaluationContext
{
    public EvaluationContext(ITagValueSource tags, DateTimeOffset now, ExpressionMemory? memory = null,
        IScriptVariables? variables = null)
    {
        Tags = tags;
        Now = now;
        Memory = memory;
        Variables = variables;
    }

    /// <summary>
    /// The locals of the command script this expression sits inside, or null in a formula - where a
    /// bare word is not a value at all and the parser says so.
    /// </summary>
    public IScriptVariables? Variables { get; }

    public ITagValueSource Tags { get; }

    /// <summary>The instant this evaluation is "at". Local time — operators reason in local time.</summary>
    public DateTimeOffset Now { get; }

    /// <summary>
    /// What this particular tag remembers from its previous evaluation, for <c>prev()</c> and
    /// <c>dt()</c>. Null means "no history is being kept", and those functions then report Bad
    /// rather than inventing a previous value.
    /// </summary>
    public ExpressionMemory? Memory { get; }
}

/// <summary>
/// The per-tag history behind <c>prev()</c> and <c>dt()</c>.
///
/// <para>
/// Rate of change is the calculation a gateway is asked for immediately after a sum — kW from a kWh
/// register, litres per minute from a totaliser — and it cannot be written without last time's
/// value. Each <c>prev(x)</c> in a formula gets its own slot, allocated when the expression is
/// compiled, so two of them in the same formula cannot overwrite each other's history.
/// </para>
///
/// <para>
/// History belongs to the expression tag, not to the tag being read: <c>prev({A})</c> means "what A
/// was when <em>this</em> tag last evaluated", which is the interval <c>dt()</c> reports. Two
/// expression tags reading A at different rates therefore see different previous values, and each
/// one's rate calculation is internally consistent — which is the property that matters.
/// </para>
/// </summary>
public sealed class ExpressionMemory
{
    private readonly ExpressionValue?[] _slots;
    private readonly ExpressionValue?[] _staged;

    public ExpressionMemory(int slotCount)
    {
        var count = Math.Max(0, slotCount);
        _slots = new ExpressionValue?[count];
        _staged = new ExpressionValue?[count];
    }

    /// <summary>When this tag last completed an evaluation; null before the first one.</summary>
    public DateTimeOffset? LastEvaluatedAt { get; private set; }

    /// <summary>Seconds since the previous evaluation, or null before there was one.</summary>
    public double? SecondsSinceLast(DateTimeOffset now) =>
        LastEvaluatedAt is { } last ? (now - last).TotalSeconds : null;

    public ExpressionValue? Read(int slot) =>
        slot >= 0 && slot < _slots.Length ? _slots[slot] : null;

    /// <summary>
    /// Values are staged during evaluation and only become "previous" in <see cref="Commit"/>. Doing
    /// it in one step at the end is what keeps two <c>prev()</c> calls consistent with each other:
    /// they all refer to the same past evaluation, never to a mixture of this one and the last.
    /// </summary>
    public void Stage(int slot, ExpressionValue value)
    {
        if (slot >= 0 && slot < _staged.Length)
            _staged[slot] = value;
    }

    public void Commit(DateTimeOffset now)
    {
        for (var i = 0; i < _slots.Length; i++)
            _slots[i] = _staged[i];
        LastEvaluatedAt = now;
    }

    /// <summary>Forget everything — used when a tag's formula changes, so old history cannot leak in.</summary>
    public void Reset()
    {
        Array.Clear(_slots);
        Array.Clear(_staged);
        LastEvaluatedAt = null;
    }
}

/// <summary>
/// Where a command script's locals are read from. Implemented by <see cref="ScriptContext"/>; a
/// formula has none, which is what makes a bare word a syntax error there and a variable here.
/// </summary>
public interface IScriptVariables
{
    bool TryGet(string name, out ExpressionValue value);
}

/// <summary>One node of a parsed expression. Evaluated by walking the tree.</summary>
public abstract class ExpressionNode
{
    public abstract ExpressionValue Evaluate(EvaluationContext context);

    /// <summary>Rebuild readable source, used to echo a formula back in error messages and the UI.</summary>
    public abstract string ToSource();
}
