using HinoGateway.Core;

namespace HinoGateway.Expressions;

/// <summary>
/// A formula that has been checked and turned into a tree, ready to be evaluated over and over.
///
/// <para>
/// Compiling once and evaluating many times is not only about speed. It is what lets the gateway
/// <em>refuse to save</em> a broken formula: everything that can be known without running it — the
/// syntax, the function names and their arities, which tags it needs — is known here, at
/// configuration time, where a person is present to fix it. What is left for runtime is only what
/// genuinely depends on the values.
/// </para>
/// </summary>
public sealed class CompiledExpression
{
    internal CompiledExpression(ExpressionNode root, IReadOnlySet<string> referencedTags, int previousSlotCount, string source)
    {
        Root = root;
        ReferencedTags = referencedTags;
        PreviousSlotCount = previousSlotCount;
        Source = source;
    }

    public ExpressionNode Root { get; }

    /// <summary>
    /// The tags this formula reads. The dependency graph that decides evaluation order is built from
    /// these, and so is the check that refuses a formula naming a tag which does not exist.
    /// </summary>
    public IReadOnlySet<string> ReferencedTags { get; }

    /// <summary>Size of the <see cref="ExpressionMemory"/> this expression needs, for <c>prev()</c>.</summary>
    public int PreviousSlotCount { get; }

    /// <summary>The formula as it was written, kept for error messages and for the UI.</summary>
    public string Source { get; }

    /// <summary>
    /// Evaluate once.
    ///
    /// <para>
    /// The context's <see cref="ExpressionMemory"/> is committed here, at the end — never inside a
    /// node. Staging during the walk and committing once at the end is what keeps several
    /// <c>prev()</c> calls in one formula referring to the same past evaluation, and what makes
    /// <c>dt()</c> report the interval between evaluations rather than a value that shifts while the
    /// tree is being walked.
    /// </para>
    /// </summary>
    public ExpressionValue Evaluate(EvaluationContext context)
    {
        var result = Root.Evaluate(context);
        context.Memory?.Commit(context.Now);
        return result;
    }

    /// <summary>A fresh memory block sized for this expression, for the engine to keep per tag.</summary>
    public ExpressionMemory CreateMemory() => new(PreviousSlotCount);
}

/// <summary>The public entry point of this project: text in, <see cref="CompiledExpression"/> out.</summary>
public static class ExpressionCompiler
{
    /// <summary>
    /// Compile a formula, throwing <see cref="ExpressionSyntaxException"/> — which carries the
    /// character position — when it cannot be read.
    /// </summary>
    public static CompiledExpression Compile(string expression)
    {
        var text = expression ?? "";
        var tokens = new Lexer(text).Tokenize();
        var parser = new Parser(tokens);
        var root = parser.ParseAll();
        return new CompiledExpression(root, parser.ReferencedTags, parser.PreviousSlotCount, text);
    }

    /// <summary>
    /// Compile without throwing, for the two callers that expect failure as a normal outcome: the
    /// project validator, which turns it into a message beside the tag, and the editor's
    /// as-you-type check, which sees a half-typed formula on nearly every keystroke.
    /// </summary>
    public static bool TryCompile(
        string expression, out CompiledExpression? compiled, out string? error, out int position)
    {
        try
        {
            compiled = Compile(expression);
            error = null;
            position = 0;
            return true;
        }
        catch (ExpressionSyntaxException ex)
        {
            compiled = null;
            error = ex.Message;
            position = ex.Position;
            return false;
        }
    }

    /// <summary>
    /// Evaluate a formula once against a set of tag values — the shortest path from text to answer,
    /// used by tests and by the editor's "try it" button.
    /// </summary>
    public static ExpressionValue Evaluate(string expression, ITagValueSource tags, DateTimeOffset now)
    {
        var compiled = Compile(expression);
        return compiled.Evaluate(new EvaluationContext(tags, now, compiled.CreateMemory()));
    }
}
