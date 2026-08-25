using HinoGateway.Core;

namespace HinoGateway.Expressions;

/// <summary>One write a script asked for: a tag path and the value to put in it.</summary>
/// <remarks>
/// Writes are collected, not performed. A script therefore computes and decides; something else does
/// the writing, with the gateway's own write policy, per-device queue, deadline and audit trail
/// applied to it. That split is what makes a dry run possible — the same script, the same values,
/// the same list at the end, with nothing sent to a PLC.
/// </remarks>
public sealed record ScriptWrite(string TagPath, object? Value);

/// <summary>
/// Everything one run of one script may see and change. Not reused between runs: a command's state
/// lives in tags, never in the script.
/// </summary>
public sealed class ScriptContext : IScriptVariables
{
    /// <summary>The name a script uses for what was just written into its trigger tag.</summary>
    public const string TriggerVariable = "value";

    private readonly Dictionary<string, ExpressionValue> _variables = new(StringComparer.Ordinal);
    private readonly List<ScriptWrite> _writes = new();
    private readonly int _maxWrites;

    /// <param name="value">
    /// The <c>value</c> a script sees: what was just written into the tag that triggered it. This is
    /// the whole input of a command - the control centre sets a tag, the command decides what that
    /// means for the plant. It is a pre-set variable rather than a keyword, so the same lookup and
    /// the same "used before it was set" check cover it.
    /// </param>
    public ScriptContext(ITagValueSource tags, DateTimeOffset now, ExpressionValue value, int maxWrites = 32)
    {
        Evaluation = new EvaluationContext(tags, now, memory: null, variables: this);
        Value = value;
        _maxWrites = maxWrites;
        _variables[TriggerVariable] = value;
    }

    public EvaluationContext Evaluation { get; }

    public ExpressionValue Value { get; }

    /// <summary>The writes the script asked for, in the order it asked for them.</summary>
    public IReadOnlyList<ScriptWrite> Writes => _writes;

    /// <summary>Set when a <c>return</c> has run: every statement after it is skipped.</summary>
    public bool Returned { get; private set; }

    public void Return() => Returned = true;

    public bool TryGet(string name, out ExpressionValue value) => _variables.TryGetValue(name, out value);

    public void SetVariable(string name, ExpressionValue value) => _variables[name] = value;

    /// <summary>
    /// Record a write, refusing past the ceiling. There are no loops in this language, so the number
    /// of writes a script can ask for is already bounded by its own length; the ceiling is the
    /// backstop for a command written by somebody who did not read that sentence.
    /// </summary>
    public void AddWrite(string tagPath, object? value)
    {
        if (_writes.Count >= _maxWrites)
            throw new ScriptRuntimeException(
                $"the command tried to write more than {_maxWrites} values in one run");
        _writes.Add(new ScriptWrite(tagPath, value));
    }
}

/// <summary>
/// A script that ran but could not finish: a value that is not a number where one was needed, a
/// variable used before it was set, too many writes. Refused at run time, reported on the command.
/// </summary>
public sealed class ScriptRuntimeException : Exception
{
    public ScriptRuntimeException(string message) : base(message) { }
}

/// <summary>One statement of a command script.</summary>
public abstract class ScriptStatement
{
    public abstract void Execute(ScriptContext context);

    /// <summary>Rebuild readable source — used to echo a command back in the UI and in errors.</summary>
    public abstract string ToSource(int indent = 0);

    private protected static string Pad(int indent) => new(' ', indent * 4);
}

/// <summary>A run of statements. Stops at the first <c>return</c>.</summary>
public sealed class BlockStatement : ScriptStatement
{
    public BlockStatement(IReadOnlyList<ScriptStatement> statements) => Statements = statements;

    public IReadOnlyList<ScriptStatement> Statements { get; }

    public override void Execute(ScriptContext context)
    {
        foreach (var statement in Statements)
        {
            if (context.Returned) return;
            statement.Execute(context);
        }
    }

    public override string ToSource(int indent = 0) =>
        string.Join(Environment.NewLine, Statements.Select(s => s.ToSource(indent)));
}

/// <summary>
/// <c>var name = expression;</c> — a local, for the length of one run. Declaring the same name twice
/// is refused at compile time: in a language this small, a shadowed variable is a mistake.
/// </summary>
public sealed class VarStatement : ScriptStatement
{
    public VarStatement(string name, ExpressionNode value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public ExpressionNode Value { get; }

    public override void Execute(ScriptContext context) =>
        context.SetVariable(Name, Value.Evaluate(context.Evaluation));

    public override string ToSource(int indent = 0) => $"{Pad(indent)}var {Name} = {Value.ToSource()};";
}

/// <summary><c>name = expression;</c> on a variable that already exists.</summary>
public sealed class AssignStatement : ScriptStatement
{
    public AssignStatement(string name, ExpressionNode value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public ExpressionNode Value { get; }

    public override void Execute(ScriptContext context) =>
        context.SetVariable(Name, Value.Evaluate(context.Evaluation));

    public override string ToSource(int indent = 0) => $"{Pad(indent)}{Name} = {Value.ToSource()};";
}

/// <summary>
/// <c>Write("path", expression);</c> — the only thing a command can do to the plant.
///
/// <para>
/// A value that is not Good is <em>not</em> written. Everything in this evaluator carries quality,
/// and the one place where ignoring it would do real damage is here: writing a setpoint computed
/// from a dead reading is how a plant ends up at the wrong limit with nothing on screen to say why.
/// </para>
/// </summary>
public sealed class WriteStatement : ScriptStatement
{
    public WriteStatement(string tagPath, ExpressionNode value)
    {
        TagPath = tagPath;
        Value = value;
    }

    public string TagPath { get; }
    public ExpressionNode Value { get; }

    public override void Execute(ScriptContext context)
    {
        var result = Value.Evaluate(context.Evaluation);
        if (result.Quality != Quality.Good)
            throw new ScriptRuntimeException(
                $"refused to write to '{TagPath}': the value is {result.Quality.ToString().ToLowerInvariant()}"
                + (result.Status is { Length: > 0 } why ? $" ({why})" : ""));
        context.AddWrite(TagPath, result.Value);
    }

    public override string ToSource(int indent = 0) =>
        $"{Pad(indent)}Write(\"{TagPath}\", {Value.ToSource()});";
}

/// <summary><c>if (condition) … else …</c>. A condition that is not Good does not run either branch.</summary>
public sealed class IfStatement : ScriptStatement
{
    public IfStatement(ExpressionNode condition, ScriptStatement then, ScriptStatement? otherwise)
    {
        Condition = condition;
        Then = then;
        Otherwise = otherwise;
    }

    public ExpressionNode Condition { get; }
    public ScriptStatement Then { get; }
    public ScriptStatement? Otherwise { get; }

    public override void Execute(ScriptContext context)
    {
        var result = Condition.Evaluate(context.Evaluation);
        if (result.Quality != Quality.Good)
            throw new ScriptRuntimeException(
                "the condition could not be decided: "
                + (result.Status is { Length: > 0 } why ? why : "a value it reads is not good"));

        if (result.Value is not bool decision)
            throw new ScriptRuntimeException(
                $"the condition is {ScriptValues.Describe(result.Value)}, not a yes/no answer");

        if (decision) Then.Execute(context);
        else Otherwise?.Execute(context);
    }

    public override string ToSource(int indent = 0)
    {
        var text = $"{Pad(indent)}if ({Condition.ToSource()}) {{{Environment.NewLine}"
                   + Then.ToSource(indent + 1) + Environment.NewLine + Pad(indent) + "}";
        if (Otherwise is null) return text;
        return text + $" else {{{Environment.NewLine}"
               + Otherwise.ToSource(indent + 1) + Environment.NewLine + Pad(indent) + "}";
    }
}

/// <summary><c>return;</c> — stop here. The commonest line in a real command, as an early guard.</summary>
public sealed class ReturnStatement : ScriptStatement
{
    public override void Execute(ScriptContext context) => context.Return();

    public override string ToSource(int indent = 0) => $"{Pad(indent)}return;";
}

/// <summary>Small helpers shared by the statement layer's error messages.</summary>
internal static class ScriptValues
{
    public static string Describe(object? value) => value switch
    {
        null => "nothing",
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        string s => $"the text \"{s}\"",
        _ => value.ToString() ?? "nothing",
    };
}
