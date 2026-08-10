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
    private readonly List<Token> _tokens;
    private readonly HashSet<string> _referencedTags = new(StringComparer.Ordinal);
    private int _index;
    private int _previousSlots;

    public Parser(List<Token> tokens) => _tokens = tokens;

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

    private Token Current => _tokens[_index];

    private Token Advance() => _tokens[_index++];

    private bool IsOperator(params string[] operators) =>
        Current.Type == TokenType.Operator && operators.Contains(Current.Text);

    // --- precedence ladder ----------------------------------------------------------------

    private ExpressionNode ParseExpression() => ParseConditional();

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
        return new ConditionalNode(condition, whenTrue, whenFalse);
    }

    private ExpressionNode ParseOr()
    {
        var node = ParseAnd();
        while (IsOperator("||"))
        {
            Advance();
            node = new BinaryNode("||", node, ParseAnd());
        }
        return node;
    }

    private ExpressionNode ParseAnd()
    {
        var node = ParseEquality();
        while (IsOperator("&&"))
        {
            Advance();
            node = new BinaryNode("&&", node, ParseEquality());
        }
        return node;
    }

    private ExpressionNode ParseEquality()
    {
        var node = ParseComparison();
        while (IsOperator("==", "!="))
        {
            var op = Advance().Text;
            node = new BinaryNode(op, node, ParseComparison());
        }
        return node;
    }

    private ExpressionNode ParseComparison()
    {
        var node = ParseAdditive();
        while (IsOperator("<", "<=", ">", ">="))
        {
            var op = Advance().Text;
            node = new BinaryNode(op, node, ParseAdditive());
        }
        return node;
    }

    private ExpressionNode ParseAdditive()
    {
        var node = ParseMultiplicative();
        while (IsOperator("+", "-"))
        {
            var op = Advance().Text;
            node = new BinaryNode(op, node, ParseMultiplicative());
        }
        return node;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var node = ParseUnary();
        while (IsOperator("*", "/", "%"))
        {
            var op = Advance().Text;
            node = new BinaryNode(op, node, ParseUnary());
        }
        return node;
    }

    private ExpressionNode ParseUnary()
    {
        if (IsOperator("-", "+", "!"))
        {
            var op = Advance().Text;
            return new UnaryNode(op, ParseUnary());
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
        return new BinaryNode("^", node, ParseUnary());
    }

    // --- leaves ---------------------------------------------------------------------------

    private ExpressionNode ParsePrimary()
    {
        var token = Current;
        switch (token.Type)
        {
            case TokenType.Number:
                Advance();
                return new LiteralNode(token.Literal, token.Text);

            case TokenType.String:
                Advance();
                return new LiteralNode(token.Literal, $"\"{token.Text}\"");

            case TokenType.Boolean:
                Advance();
                return new LiteralNode(token.Literal, token.Text.ToLowerInvariant());

            case TokenType.Null:
                Advance();
                return new LiteralNode(null, "null");

            case TokenType.TagReference:
                Advance();
                _referencedTags.Add(token.Text);
                return new TagNode(token.Text);

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
            return new PreviousNode(arguments[0], _previousSlots++);
        }

        if (!Functions.TryGet(name.Text, out var function))
            throw new ExpressionSyntaxException($"there is no function called '{name.Text}'", name.Position);

        var args = ParseArguments(name);
        if (args.Length < function.MinArgs || args.Length > function.MaxArgs)
            throw new ExpressionSyntaxException(
                $"{function.Name}() takes {function.DescribeArity()}, got {args.Length} — {function.Signature}",
                name.Position);

        if (function.CheckArguments?.Invoke(args) is { } complaint)
            throw new ExpressionSyntaxException(complaint, name.Position);

        return new FunctionNode(function, args);
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

    private void Expect(TokenType type, string message)
    {
        if (Current.Type != type)
            throw new ExpressionSyntaxException($"{message}, found {Current}", Current.Position);
        Advance();
    }
}
