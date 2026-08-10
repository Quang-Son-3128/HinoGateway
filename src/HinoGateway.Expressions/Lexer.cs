using System.Globalization;
using System.Text;

namespace HinoGateway.Expressions;

internal enum TokenType
{
    End,
    Number,
    String,
    Boolean,
    Null,
    Identifier,
    TagReference,
    Operator,
    LeftParen,
    RightParen,
    Comma,
    Question,
    Colon,
}

internal readonly record struct Token(TokenType Type, string Text, int Position, object? Literal = null)
{
    public override string ToString() => Type switch
    {
        TokenType.End => "end of expression",
        TokenType.TagReference => $"{{{Text}}}",
        TokenType.String => $"\"{Text}\"",
        _ => $"'{Text}'",
    };
}

/// <summary>
/// A syntax error with the character position it was found at, so the editor can point at it
/// instead of saying that something, somewhere, is wrong.
/// </summary>
public sealed class ExpressionSyntaxException : Exception
{
    public ExpressionSyntaxException(string message, int position) : base(message) => Position = position;

    /// <summary>0-based index into the expression text.</summary>
    public int Position { get; }

    /// <summary>The message with the position appended, for logs and the API.</summary>
    public string Describe() => $"{Message} (at character {Position + 1})";
}

/// <summary>
/// Turns expression text into tokens.
///
/// <para>
/// The one part worth explaining is <c>{...}</c>. Tag ids in this gateway are folder paths — they
/// contain '/' and spaces and can be written in Vietnamese — so they cannot be bare identifiers
/// without making the grammar ambiguous with division and with every function name. Braces make the
/// reference unmistakable to the parser and, just as importantly, to the person reading the formula:
/// everything inside them is a tag, everything outside is arithmetic.
/// </para>
/// </summary>
internal sealed class Lexer
{
    private readonly string _text;
    private int _index;

    public Lexer(string text) => _text = text ?? "";

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var token = Next();
            tokens.Add(token);
            if (token.Type == TokenType.End)
                return tokens;
        }
    }

    private Token Next()
    {
        SkipTrivia();
        if (_index >= _text.Length)
            return new Token(TokenType.End, "", _index);

        var start = _index;
        var c = _text[_index];

        if (char.IsDigit(c) || (c == '.' && _index + 1 < _text.Length && char.IsDigit(_text[_index + 1])))
            return ReadNumber();
        if (c == '"' || c == '\'')
            return ReadString(c);
        if (c == '{')
            return ReadTagReference();
        if (char.IsLetter(c) || c == '_')
            return ReadIdentifier();

        switch (c)
        {
            case '(': _index++; return new Token(TokenType.LeftParen, "(", start);
            case ')': _index++; return new Token(TokenType.RightParen, ")", start);
            case ',': _index++; return new Token(TokenType.Comma, ",", start);
            case '?': _index++; return new Token(TokenType.Question, "?", start);
            case ':': _index++; return new Token(TokenType.Colon, ":", start);
        }

        // Two-character operators first, so '<=' is never read as '<' followed by '='.
        if (_index + 1 < _text.Length)
        {
            var pair = _text.Substring(_index, 2);
            if (pair is "==" or "!=" or "<=" or ">=" or "&&" or "||" or "<>")
            {
                _index += 2;
                return new Token(TokenType.Operator, pair == "<>" ? "!=" : pair, start);
            }
        }

        if ("+-*/%^<>!=&|".Contains(c))
        {
            _index++;
            // A lone '=' is almost always a typo for '==' rather than an assignment attempt, and
            // saying so is more useful than "unexpected character".
            if (c == '=')
                throw new ExpressionSyntaxException("'=' is not an operator here — use '==' to compare", start);
            if (c is '&' or '|')
                throw new ExpressionSyntaxException($"use '{c}{c}' for logical {(c == '&' ? "and" : "or")}", start);
            return new Token(TokenType.Operator, c.ToString(), start);
        }

        throw new ExpressionSyntaxException($"unexpected character '{c}'", start);
    }

    /// <summary>Whitespace and <c>// ...</c> comments — a long formula deserves a note beside it.</summary>
    private void SkipTrivia()
    {
        while (_index < _text.Length)
        {
            if (char.IsWhiteSpace(_text[_index]))
            {
                _index++;
                continue;
            }
            if (_text[_index] == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
            {
                while (_index < _text.Length && _text[_index] is not ('\n' or '\r'))
                    _index++;
                continue;
            }
            return;
        }
    }

    private Token ReadNumber()
    {
        var start = _index;
        while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '.'))
            _index++;

        // Exponent, but only when it really is one: 1e3 and 1e-3 are numbers, while the 'e' of a
        // following identifier must not be swallowed.
        if (_index < _text.Length && (_text[_index] is 'e' or 'E'))
        {
            var save = _index;
            _index++;
            if (_index < _text.Length && (_text[_index] is '+' or '-'))
                _index++;
            if (_index < _text.Length && char.IsDigit(_text[_index]))
                while (_index < _text.Length && char.IsDigit(_text[_index]))
                    _index++;
            else
                _index = save;
        }

        var text = _text[start.._index];
        // InvariantCulture always: a formula is stored in a project file and must mean the same
        // number on a machine whose decimal separator is a comma.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            throw new ExpressionSyntaxException($"'{text}' is not a valid number", start);
        return new Token(TokenType.Number, text, start, value);
    }

    private Token ReadString(char quote)
    {
        var start = _index;
        _index++;   // opening quote
        var builder = new StringBuilder();
        while (true)
        {
            if (_index >= _text.Length)
                throw new ExpressionSyntaxException("unterminated text — the closing quote is missing", start);

            var c = _text[_index];
            if (c == '\\' && _index + 1 < _text.Length)
            {
                _index++;
                builder.Append(_text[_index] switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    var other => other,
                });
                _index++;
                continue;
            }
            if (c == quote)
            {
                _index++;
                return new Token(TokenType.String, builder.ToString(), start, builder.ToString());
            }
            builder.Append(c);
            _index++;
        }
    }

    private Token ReadTagReference()
    {
        var start = _index;
        _index++;   // '{'
        var builder = new StringBuilder();
        while (true)
        {
            if (_index >= _text.Length || _text[_index] is '\n' or '\r')
                throw new ExpressionSyntaxException("unterminated tag reference — the closing '}' is missing", start);
            if (_text[_index] == '}')
            {
                _index++;
                var id = builder.ToString().Trim();
                if (id.Length == 0)
                    throw new ExpressionSyntaxException("empty tag reference '{}'", start);
                return new Token(TokenType.TagReference, id, start);
            }
            builder.Append(_text[_index]);
            _index++;
        }
    }

    private Token ReadIdentifier()
    {
        var start = _index;
        while (_index < _text.Length && (char.IsLetterOrDigit(_text[_index]) || _text[_index] == '_'))
            _index++;
        var text = _text[start.._index];
        return text.ToLowerInvariant() switch
        {
            "true" => new Token(TokenType.Boolean, text, start, true),
            "false" => new Token(TokenType.Boolean, text, start, false),
            "null" => new Token(TokenType.Null, text, start),
            _ => new Token(TokenType.Identifier, text, start),
        };
    }
}
