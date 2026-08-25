using HinoGateway.Core;

namespace HinoGateway.Expressions;

/// <summary>
/// A command script that has been checked and turned into statements, ready to run whenever its
/// trigger tag is written.
///
/// <para>
/// What matters here is what is known <em>before</em> it runs: every tag it reads and every tag it
/// writes, because both are named by string literals. That is why the language refuses a computed
/// tag path. It lets the project validator refuse a command pointing at a tag that does not exist or
/// at one whose AllowWrite switch is off, and it lets the screen list what a command touches — on a
/// gateway that moves setpoints into a PLC, "what can this thing write to" must be answerable
/// without running it.
/// </para>
/// </summary>
public sealed class CompiledScript
{
    internal CompiledScript(BlockStatement body, IReadOnlySet<string> readTags,
        IReadOnlySet<string> writtenTags, string source)
    {
        Body = body;
        ReadTags = readTags;
        WrittenTags = writtenTags;
        Source = source;
    }

    internal BlockStatement Body { get; }

    /// <summary>Tag paths the script reads through <c>Tag("…")</c>.</summary>
    public IReadOnlySet<string> ReadTags { get; }

    /// <summary>Tag paths the script writes through <c>Write("…", …)</c>.</summary>
    public IReadOnlySet<string> WrittenTags { get; }

    /// <summary>The script as it was written.</summary>
    public string Source { get; }

    /// <summary>
    /// Run once and report what it wants written. Nothing is written here — see
    /// <see cref="ScriptWrite"/> — so this is also the dry run the UI's "test" button uses.
    /// </summary>
    /// <param name="tags">The live values the script may read.</param>
    /// <param name="value">What was just written into the trigger tag.</param>
    public ScriptRun Run(ITagValueSource tags, ExpressionValue value, DateTimeOffset now, int maxWrites = 32)
    {
        var context = new ScriptContext(tags, now, value, maxWrites);
        try
        {
            Body.Execute(context);
            return new ScriptRun(true, context.Writes, null);
        }
        catch (ScriptRuntimeException ex)
        {
            // Whatever it had already asked for is reported alongside the failure: a command that
            // wrote two of its three setpoints and then stopped is a state somebody has to see.
            return new ScriptRun(false, context.Writes, ex.Message);
        }
    }
}

/// <summary>The outcome of one run: what it wants written, and why it stopped if it did.</summary>
public sealed record ScriptRun(bool Completed, IReadOnlyList<ScriptWrite> Writes, string? Error);

/// <summary>Text in, <see cref="CompiledScript"/> out — the script layer's entry point.</summary>
public static class ScriptCompiler
{
    /// <summary>
    /// The longest command this compiler will look at. Larger than a formula's limit because a
    /// command is several lines rather than one expression, and still far below anything that could
    /// trouble the parser's own ceilings.
    /// </summary>
    public const int MaxScriptLength = 8192;

    public static CompiledScript Compile(string script)
    {
        var text = script ?? "";
        if (text.Length > MaxScriptLength)
            throw new ExpressionSyntaxException(
                $"the command is {text.Length} characters long; the limit is {MaxScriptLength}",
                MaxScriptLength);

        var tokens = new Lexer(text, script: true).Tokenize();
        var parser = new Parser(tokens, script: true);
        var body = new ScriptParser(parser).ParseProgram();
        return new CompiledScript(body, parser.ReferencedTags, parser.WrittenTags, text);
    }

    /// <summary>
    /// Compile without throwing, for the two callers that expect failure as a normal outcome: the
    /// project validator, and the editor checking a half-typed command on every keystroke.
    /// </summary>
    public static bool TryCompile(string script, out CompiledScript? compiled, out string? error, out int position)
    {
        try
        {
            compiled = Compile(script);
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
}
