namespace HinoGateway.Expressions;

/// <summary>
/// The statement layer: <c>var</c>, assignment, <c>if</c>/<c>else</c>, blocks, <c>return</c> and
/// <c>Write(…)</c>, wrapped around the expression parser rather than replacing it.
///
/// <para>
/// <b>There are no loops, on purpose.</b> Twenty-four real NanoX command scripts were read before
/// this was written and not one of them loops. Leaving loops out turns "how long can a command run"
/// from a halting problem into arithmetic: a script's cost is bounded by its own length, which is
/// bounded when it is saved. On a gateway where a command arrives from the utility's control centre
/// and runs inside a poll loop, that is worth more than the expressiveness.
/// </para>
///
/// <para>
/// The depth and node ceilings are the expression parser's, reused rather than re-invented — a
/// statement is parsed through the same <c>Descend</c>/<c>Ascend</c> pair, so the stack guard added
/// after the 2026-08-21 security review covers this grammar as well. Anything that finds its way
/// around them is a stack overflow, which .NET cannot catch: it ends the gateway.
/// </para>
/// </summary>
internal sealed class ScriptParser
{
    /// <summary>
    /// How many statements one command may be. Not a style rule: with no loops in the language, this
    /// is what bounds a run — every statement executes at most once.
    /// </summary>
    internal const int MaxStatements = 200;

    private readonly Parser _parser;
    private readonly HashSet<string> _declared = new(StringComparer.Ordinal) { ScriptContext.TriggerVariable };
    private int _statements;

    public ScriptParser(Parser parser) => _parser = parser;

    private Token Current => _parser.Current;

    public BlockStatement ParseProgram()
    {
        var statements = new List<ScriptStatement>();
        while (Current.Type != TokenType.End)
            statements.Add(ParseStatement());

        if (statements.Count == 0)
            throw new ExpressionSyntaxException("the command does nothing", Current.Position);

        return new BlockStatement(statements);
    }

    private ScriptStatement ParseStatement()
    {
        if (++_statements > MaxStatements)
            throw new ExpressionSyntaxException(
                $"the command is more than {MaxStatements} statements long, which is far more than a command needs",
                Current.Position);

        _parser.Descend();
        try
        {
            return Current.Type switch
            {
                TokenType.LeftBrace => ParseBlock(),
                TokenType.Semicolon => Skip(),
                TokenType.Identifier => ParseIdentifierStatement(),
                _ => throw new ExpressionSyntaxException(
                    $"{Current} does not start a statement — expected var, if, return or Write(…)", Current.Position),
            };
        }
        finally { _parser.Ascend(); }
    }

    /// <summary>A stray <c>;</c> is nothing, not an error. Nobody should have to hunt for it.</summary>
    private ScriptStatement Skip()
    {
        _parser.Advance();
        return new BlockStatement(Array.Empty<ScriptStatement>());
    }

    private ScriptStatement ParseBlock()
    {
        Expect(TokenType.LeftBrace, "'{' is missing");
        var statements = new List<ScriptStatement>();
        while (Current.Type != TokenType.RightBrace)
        {
            if (Current.Type == TokenType.End)
                throw new ExpressionSyntaxException("'}' is missing — a block was left open", Current.Position);
            statements.Add(ParseStatement());
        }
        _parser.Advance();   // '}'
        return new BlockStatement(statements);
    }

    private ScriptStatement ParseIdentifierStatement()
    {
        var word = Current;
        switch (word.Text.ToLowerInvariant())
        {
            case "var": return ParseVar();
            case "if": return ParseIf();
            case "return": return ParseReturn();
            case "else":
                throw new ExpressionSyntaxException("'else' without an 'if' before it", word.Position);

            // Named explicitly so the answer is the reason rather than "not declared". Somebody will
            // try to loop; they deserve to be told that the language has no loops on purpose, and
            // that the shape they want is one command per thing to write.
            case "while":
            case "for":
            case "foreach":
            case "do":
            case "loop":
                throw new ExpressionSyntaxException(
                    $"'{word.Text}' does not start a statement — a command has no loops, so that how long "
                    + "it can run is decided when it is saved", word.Position);
        }

        if (string.Equals(word.Text, "Write", StringComparison.OrdinalIgnoreCase))
            return ParseWrite();

        return ParseAssignment();
    }

    private ScriptStatement ParseVar()
    {
        _parser.Advance();   // 'var'
        var name = ExpectName("a name is missing after 'var'");
        if (!_declared.Add(name.Text))
            throw new ExpressionSyntaxException(
                $"'{name.Text}' is already declared in this command", name.Position);
        Expect(TokenType.Assign, $"'=' is missing — write var {name.Text} = …");
        var value = _parser.ParseExpression();
        Expect(TokenType.Semicolon, "';' is missing at the end of the statement");
        return new VarStatement(name.Text, value);
    }

    private ScriptStatement ParseAssignment()
    {
        var name = _parser.Advance();
        if (!_declared.Contains(name.Text))
            throw new ExpressionSyntaxException(
                $"'{name.Text}' has not been declared — write var {name.Text} = … the first time", name.Position);
        if (string.Equals(name.Text, ScriptContext.TriggerVariable, StringComparison.Ordinal))
            throw new ExpressionSyntaxException(
                $"'{ScriptContext.TriggerVariable}' is what the control centre sent and cannot be assigned to",
                name.Position);
        Expect(TokenType.Assign, $"'=' is missing — write {name.Text} = …");
        var value = _parser.ParseExpression();
        Expect(TokenType.Semicolon, "';' is missing at the end of the statement");
        return new AssignStatement(name.Text, value);
    }

    private ScriptStatement ParseWrite()
    {
        var name = _parser.Advance();   // 'Write'
        var (path, value) = _parser.ParseWriteArguments(name);
        Expect(TokenType.Semicolon, "';' is missing at the end of the statement");
        return new WriteStatement(path, value);
    }

    private ScriptStatement ParseIf()
    {
        _parser.Advance();   // 'if'
        Expect(TokenType.LeftParen, "'(' is missing after 'if'");
        var condition = _parser.ParseExpression();
        Expect(TokenType.RightParen, "')' is missing");

        var then = ParseStatement();
        ScriptStatement? otherwise = null;
        if (Current.Type == TokenType.Identifier
            && string.Equals(Current.Text, "else", StringComparison.OrdinalIgnoreCase))
        {
            _parser.Advance();
            otherwise = ParseStatement();
        }
        return new IfStatement(condition, then, otherwise);
    }

    private ScriptStatement ParseReturn()
    {
        _parser.Advance();   // 'return'
        // No value: a command answers by writing tags, not by returning something nobody reads.
        if (Current.Type != TokenType.Semicolon)
            throw new ExpressionSyntaxException(
                "'return' stops the command and takes no value — write return;", Current.Position);
        _parser.Advance();
        return new ReturnStatement();
    }

    private Token ExpectName(string message)
    {
        if (Current.Type != TokenType.Identifier)
            throw new ExpressionSyntaxException($"{message}, found {Current}", Current.Position);
        return _parser.Advance();
    }

    private void Expect(TokenType type, string message)
    {
        if (Current.Type != type)
            throw new ExpressionSyntaxException($"{message}, found {Current}", Current.Position);
        _parser.Advance();
    }
}
