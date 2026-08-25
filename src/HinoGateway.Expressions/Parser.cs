namespace HinoGateway.Expressions;

/// <summary>
/// Turns tokens into a tree, by recursive descent — one method per precedence level, each one
/// calling the level above it.
///
/// <para>
/// The precedence ladder, loosest first: <c>?:</c> · <c>||</c> · <c>&amp;&amp;</c> · <c>== !=</c> ·
/// <c>&lt; &lt;= &gt; &gt;=</c> · <c>+ -</c> · <c>* / %</c> · unary <c>- + !</c> · <c>^</c> ·
/// primary. It is C's ladder with exponentiation added, because that is the one an engineer already
/// has in their fingers from PLC and spreadsheet formulas.
/// </para>
///
/// <para>
/// Two of those placements are worth stating out loud, because getting them wrong produces formulas
/// that compute the wrong number in silence rather than failing:
/// <c>^</c> binds <em>tighter</em> than unary minus, so <c>-2^2</c> is <c>-(2^2) = -4</c> and not
/// <c>4</c>; and <c>^</c> associates to the right, so <c>2^3^2</c> is <c>2^(3^2) = 512</c>. Both
/// match mathematical convention and Excel.
/// </para>
///
/// <para>
/// The parser also collects, as it goes, the two facts the rest of the gateway needs about a formula
/// before it is ever evaluated: which tags it reads (for the dependency graph, the evaluation order
/// and for refusing to save a reference to a tag that does not exist) and how many
/// <c>prev()</c> slots it needs. Gathering them here costs nothing, whereas walking the finished tree
/// again to find them means writing a second traversal that can disagree with the first.
/// </para>
/// </summary>
internal sealed class Parser
{
    /// <summary>
    /// How deeply the grammar may nest. Every level of <c>(</c> costs eleven stack frames on the way
    /// down the precedence ladder, so without this a few thousand of them overflow the stack — and a
    /// <c>StackOverflowException</c> is not catchable in .NET, it ends the process. The formula
    /// editor's check endpoint compiles whatever text it is sent, which makes that a remote answer
    /// to a local question.
    /// </summary>
    private const int MaxDepth = 64;

    /// <summary>
    /// How many nodes a formula may be built from.
    ///
    /// <para>
    /// This is <em>not</em> the same guard as <see cref="MaxDepth"/>, and one without the other
    /// leaves the hole open. <c>ParseAdditive</c> and its neighbours consume a run of operators in a
    /// <c>while</c> loop, so <c>1+1+1+…</c> parses at constant recursion depth while building a tree
    /// that leans left as far as there are terms. Nothing overflows while parsing it; the overflow
    /// arrives later, in <c>Evaluate</c>, once per poll — and if that formula was saved to
    /// <c>hino.json</c> the gateway then dies on startup too, and only hand-editing the file
    /// recovers it. Tree height can never exceed the node count, so bounding this bounds evaluation.
    /// </para>
    /// </summary>
    private const int MaxNodes = 1024;

    private readonly List<Token> _tokens;
    private readonly HashSet<string> _referencedTags = new(StringComparer.Ordinal);
    private readonly HashSet<string> _writtenTags = new(StringComparer.Ordinal);
    private readonly bool _script;
    private int _index;
    private int _previousSlots;
    private int _depth;
    private int _nodes;

    /// <param name="script">
    /// Parse for a command script rather than a formula: bare words are local variables, and
    /// <c>Tag("path")</c> takes the place of <c>{path}</c>. See <see cref="ScriptParser"/>, which
    /// drives this one for the statements around the expressions.
    /// </param>
    public Parser(List<Token> tokens, bool script = false)
    {
        _tokens = tokens;
        _script = script;
    }

    /// <summary>Tags a command script writes to, by path. Empty for a formula, which cannot write.</summary>
    public IReadOnlySet<string> WrittenTags => _writtenTags;

    internal bool IsScript => _script;

    internal void RecordWrite(string tagPath) => _writtenTags.Add(tagPath);

    /// <summary>Every tag id named by a <c>{...}</c> reference, each one once.</summary>
    public IReadOnlySet<string> ReferencedTags => _referencedTags;

    /// <summary>How many independent <c>prev()</c> histories this expression needs.</summary>
    public int PreviousSlotCount => _previousSlots;

    public ExpressionNode ParseAll()
    {
        if (Current.Type == TokenType.End)
            throw new ExpressionSyntaxException("the expression is empty", Current.Position);

        var node = ParseExpression();
        if (Current.Type != TokenType.End)
            throw new ExpressionSyntaxException($"unexpected {Current} after the end of the expression", Current.Position);
        return node;
    }

    internal Token Current => _tokens[_index];

    internal Token Advance() => _tokens[_index++];

    private bool IsOperator(params string[] operators) =>
        Current.Type == TokenType.Operator && operators.Contains(Current.Text);

    /// <summary>Count one node towards <see cref="MaxNodes"/> and hand it back.</summary>
    internal T Track<T>(T node) where T : ExpressionNode
    {
        if (++_nodes > MaxNodes)
            throw new ExpressionSyntaxException(
                $"the expression is built from more than {MaxNodes} parts, which is far more than a formula needs",
                Current.Position);
        return node;
    }

    /// <summary>
    /// Enter one level of nesting. Paired with <see cref="Ascend"/> in a <c>finally</c>, so a
    /// formula that throws part-way through still leaves the counter where the next parse expects it
    /// — parsers are per-compilation here, but the pairing is what makes that irrelevant.
    /// </summary>
    internal void Descend()
    {
        if (++_depth > MaxDepth)
            throw new ExpressionSyntaxException(
                $"the expression nests more than {MaxDepth} levels deep", Current.Position);
    }

    internal void Ascend() => _depth--;

    // --- precedence ladder ----------------------------------------------------------------

    internal ExpressionNode ParseExpression()
    {
        Descend();
        try { return ParseConditional(); }
        finally { Ascend(); }
    }

    private ExpressionNode ParseConditional()
    {
        var condition = ParseOr();
        if (Current.Type != TokenType.Question)
            return condition;

        Advance();
        var whenTrue = ParseExpression();
        if (Current.Type != TokenType.Colon)
            throw new ExpressionSyntaxException("':' is missing — a '?' needs both answers", Current.Position);
        Advance();
        // Right-associative: `a ? b : c ? d : e` reads as `a ? b : (c ? d : e)`, which is how a chain
        // of "otherwise if" is meant.
        var whenFalse = ParseConditional();
        return Track(new ConditionalNode(condition, whenTrue, whenFalse));
    }

    private ExpressionNode ParseOr()
    {
        var node = ParseAnd();
        while (IsOperator("||"))
        {
            Advance();
            node = Track(new BinaryNode("||", node, ParseAnd()));
        }
        return node;
    }

    private ExpressionNode ParseAnd()
    {
        var node = ParseEquality();
        while (IsOperator("&&"))
        {
            Advance();
            node = Track(new BinaryNode("&&", node, ParseEquality()));
        }
        return node;
    }

    private ExpressionNode ParseEquality()
    {
        var node = ParseComparison();
        while (IsOperator("==", "!="))
        {
            var op = Advance().Text;
            node = Track(new BinaryNode(op, node, ParseComparison()));
        }
        return node;
    }

    private ExpressionNode ParseComparison()
    {
        var node = ParseAdditive();
        while (IsOperator("<", "<=", ">", ">="))
        {
            var op = Advance().Text;
            node = Track(new BinaryNode(op, node, ParseAdditive()));
        }
        return node;
    }

    private ExpressionNode ParseAdditive()
    {
        var node = ParseMultiplicative();
        while (IsOperator("+", "-"))
        {
            var op = Advance().Text;
            node = Track(new BinaryNode(op, node, ParseMultiplicative()));
        }
        return node;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var node = ParseUnary();
        while (IsOperator("*", "/", "%"))
        {
            var op = Advance().Text;
            node = Track(new BinaryNode(op, node, ParseUnary()));
        }
        return node;
    }

    private ExpressionNode ParseUnary()
    {
        // A run of unary operators (`!!!!x`) recurses here without going through ParseExpression,
        // so this level has to be counted on its own or the depth guard has a door left open.
        if (IsOperator("-", "+", "!"))
        {
            var op = Advance().Text;
            Descend();
            try { return Track(new UnaryNode(op, ParseUnary())); }
            finally { Ascend(); }
        }
        return ParsePower();
    }

    private ExpressionNode ParsePower()
    {
        var node = ParsePrimary();
        if (!IsOperator("^"))
            return node;

        Advance();
        // The right side goes back through unary so that `2^-1` parses, and through this method
        // again so that `2^3^2` groups to the right.
        return Track(new BinaryNode("^", node, ParseUnary()));
    }

    // --- leaves ---------------------------------------------------------------------------

    private ExpressionNode ParsePrimary()
    {
        var token = Current;
        switch (token.Type)
        {
            case TokenType.Number:
                Advance();
                return Track(new LiteralNode(token.Literal, token.Text));

            case TokenType.String:
                Advance();
                return Track(new LiteralNode(token.Literal, $"\"{token.Text}\""));

            case TokenType.Boolean:
                Advance();
                return Track(new LiteralNode(token.Literal, token.Text.ToLowerInvariant()));

            case TokenType.Null:
                Advance();
                return Track(new LiteralNode(null, "null"));

            case TokenType.TagReference:
                Advance();
                _referencedTags.Add(token.Text);
                return Track(new TagNode(token.Text));

            case TokenType.LeftParen:
                Advance();
                var inner = ParseExpression();
                Expect(TokenType.RightParen, "')' is missing");
                return inner;

            case TokenType.Identifier:
                return ParseCall();

            case TokenType.End:
                throw new ExpressionSyntaxException("the expression stops here, a value was expected", token.Position);

            default:
                throw new ExpressionSyntaxException($"{token} is not a value", token.Position);
        }
    }

    private ExpressionNode ParseCall()
    {
        var name = Advance();
        if (Current.Type != TokenType.LeftParen)
        {
            // In a script a bare word is a local variable — that is what `var load = …` created.
            if (_script)
                return Track(new VariableNode(name.Text, name.Position));

            // A bare word is the commonest first mistake: either a tag written without braces or a
            // function written without its brackets. Say both, rather than "unexpected identifier".
            throw new ExpressionSyntaxException(
                $"'{name.Text}' is not a value — write {{{name.Text}}} for a tag, or {name.Text}(…) for a function",
                name.Position);
        }

        // prev() is handled before the function table, because it is not a function: it needs a
        // memory slot of its own, allocated here at compile time so that two prev() calls in one
        // formula cannot overwrite each other's history.
        if (string.Equals(name.Text, "prev", StringComparison.OrdinalIgnoreCase))
        {
            var arguments = ParseArguments(name);
            if (arguments.Length != 1)
                throw new ExpressionSyntaxException(
                    $"prev() takes exactly 1 argument, got {arguments.Length}", name.Position);
            return Track(new PreviousNode(arguments[0], _previousSlots++));
        }

        // Tag("path") is the script's way of naming a tag, and it compiles to exactly the node a
        // formula's {path} produces — same reading, same quality propagation, same dependency set.
        // The path must be a literal so that the set of tags a command touches is known before it
        // runs: that is what lets the validator refuse a command naming a tag that does not exist,
        // and what lets the UI list what a command reads and writes.
        if (_script && string.Equals(name.Text, "Tag", StringComparison.OrdinalIgnoreCase))
        {
            var path = ParseLiteralPathCall(name);
            _referencedTags.Add(path);
            return Track(new TagNode(path));
        }

        if (_script && string.Equals(name.Text, "Write", StringComparison.OrdinalIgnoreCase))
            throw new ExpressionSyntaxException(
                "Write(…) is a statement, not a value — put it on a line of its own", name.Position);

        if (!Functions.TryGet(name.Text, out var function))
            throw new ExpressionSyntaxException($"there is no function called '{name.Text}'", name.Position);

        var args = ParseArguments(name);
        if (args.Length < function.MinArgs || args.Length > function.MaxArgs)
            throw new ExpressionSyntaxException(
                $"{function.Name}() takes {function.DescribeArity()}, got {args.Length} — {function.Signature}",
                name.Position);

        if (function.CheckArguments?.Invoke(args) is { } complaint)
            throw new ExpressionSyntaxException(complaint, name.Position);

        return Track(new FunctionNode(function, args));
    }

    private ExpressionNode[] ParseArguments(Token name)
    {
        Expect(TokenType.LeftParen, "'(' is missing");
        if (Current.Type == TokenType.RightParen)
        {
            Advance();
            return Array.Empty<ExpressionNode>();
        }

        var arguments = new List<ExpressionNode>();
        while (true)
        {
            arguments.Add(ParseExpression());
            if (Current.Type == TokenType.Comma)
            {
                Advance();
                continue;
            }
            if (Current.Type == TokenType.RightParen)
            {
                Advance();
                return arguments.ToArray();
            }
            throw new ExpressionSyntaxException(
                $"',' or ')' expected in {name.Text}(…), found {Current}", Current.Position);
        }
    }

    /// <summary>The single string literal of a <c>Tag("…")</c> call, with a message that says why.</summary>
    private string ParseLiteralPathCall(Token name)
    {
        Expect(TokenType.LeftParen, "'(' is missing");
        if (Current.Type != TokenType.String)
            throw new ExpressionSyntaxException(
                $"{name.Text}(…) needs the tag path written out as text, for example {name.Text}(\"Plant/Power\")",
                Current.Position);
        var path = Advance().Text.Trim();
        if (path.Length == 0)
            throw new ExpressionSyntaxException("the tag path is empty", Current.Position);
        Expect(TokenType.RightParen, "')' is missing");
        return path;
    }

    /// <summary>Path and value of a <c>Write("…", expr)</c>, parsed for the statement layer.</summary>
    internal (string Path, ExpressionNode Value) ParseWriteArguments(Token name)
    {
        Expect(TokenType.LeftParen, "'(' is missing");
        if (Current.Type != TokenType.String)
            throw new ExpressionSyntaxException(
                "Write(…) needs the tag path written out as text, for example Write(\"Plant/Setpoint\", 50)",
                Current.Position);
        var path = Advance().Text.Trim();
        if (path.Length == 0)
            throw new ExpressionSyntaxException("the tag path is empty", Current.Position);
        Expect(TokenType.Comma, "',' is missing — Write takes a tag path and a value");
        var value = ParseExpression();
        Expect(TokenType.RightParen, "')' is missing");
        _writtenTags.Add(path);
        return (path, value);
    }

    private void Expect(TokenType type, string message)
    {
        if (Current.Type != type)
            throw new ExpressionSyntaxException($"{message}, found {Current}", Current.Position);
        Advance();
    }
}
