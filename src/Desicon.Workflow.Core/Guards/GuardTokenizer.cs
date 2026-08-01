using System.Globalization;
using System.Text;

namespace Desicon.Workflow.Core.Guards;

/// <summary>
/// Token kinds for the restricted guard expression language.
/// </summary>
public enum TokenKind
{
    Identifier,
    Number,
    StringLiteral,
    Null,
    True,
    False,
    LeftParen,
    RightParen,
    Comma,
    Operator,
    End
}

public readonly record struct Token(TokenKind Kind, string Text, int Position);

/// <summary>
/// Tokenizer for guard expressions such as:
///     NetPayableNgn &gt;= 0 &amp;&amp; ActorId != RequesterId
///     GlDebitTotal == GlCreditTotal &amp;&amp; JournalVoucherNumber != null
///     RefundReceivedAmountNgn == Abs(NetPayableNgn)
///
/// The grammar is deliberately small. There is no assignment, no member access,
/// no indexing and no way to name a type, so a definition author cannot reach
/// anything the evaluator does not explicitly expose.
/// </summary>
public sealed class GuardTokenizer
{
    private static readonly string[] MultiCharOperators =
    {
        "&&", "||", "==", "!=", ">=", "<="
    };

    private static readonly char[] SingleCharOperators = { '>', '<', '!', '+', '-' };

    private readonly string _source;
    private int _index;

    public GuardTokenizer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public IReadOnlyList<Token> Tokenize()
    {
        var tokens = new List<Token>();

        while (true)
        {
            SkipWhitespace();

            if (_index >= _source.Length)
            {
                tokens.Add(new Token(TokenKind.End, string.Empty, _index));
                return tokens;
            }

            var start = _index;
            var current = _source[_index];

            if (current == '(')
            {
                _index++;
                tokens.Add(new Token(TokenKind.LeftParen, "(", start));
                continue;
            }

            if (current == ')')
            {
                _index++;
                tokens.Add(new Token(TokenKind.RightParen, ")", start));
                continue;
            }

            if (current == ',')
            {
                _index++;
                tokens.Add(new Token(TokenKind.Comma, ",", start));
                continue;
            }

            if (current == '\'' || current == '"')
            {
                tokens.Add(ReadString(current));
                continue;
            }

            if (char.IsDigit(current))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                tokens.Add(ReadIdentifierOrKeyword());
                continue;
            }

            var multi = MatchMultiCharOperator();
            if (multi is not null)
            {
                _index += multi.Length;
                tokens.Add(new Token(TokenKind.Operator, multi, start));
                continue;
            }

            if (Array.IndexOf(SingleCharOperators, current) >= 0)
            {
                _index++;
                tokens.Add(new Token(TokenKind.Operator, current.ToString(), start));
                continue;
            }

            throw new GuardSyntaxException(
                $"Unexpected character '{current}' at position {start}.");
        }
    }

    private string? MatchMultiCharOperator()
    {
        foreach (var op in MultiCharOperators)
        {
            if (_index + op.Length <= _source.Length &&
                string.CompareOrdinal(_source, _index, op, 0, op.Length) == 0)
            {
                return op;
            }
        }

        return null;
    }

    private void SkipWhitespace()
    {
        while (_index < _source.Length && char.IsWhiteSpace(_source[_index]))
        {
            _index++;
        }
    }

    private Token ReadString(char quote)
    {
        var start = _index;
        _index++; // opening quote

        var builder = new StringBuilder();

        while (_index < _source.Length && _source[_index] != quote)
        {
            // Backslash escapes are intentionally not supported. A literal in a
            // workflow definition is a status code or a role name, never prose.
            builder.Append(_source[_index]);
            _index++;
        }

        if (_index >= _source.Length)
        {
            throw new GuardSyntaxException($"Unterminated string literal at position {start}.");
        }

        _index++; // closing quote
        return new Token(TokenKind.StringLiteral, builder.ToString(), start);
    }

    private Token ReadNumber()
    {
        var start = _index;
        var seenDecimalPoint = false;

        while (_index < _source.Length)
        {
            var c = _source[_index];

            if (char.IsDigit(c))
            {
                _index++;
                continue;
            }

            if (c == '.' && !seenDecimalPoint)
            {
                seenDecimalPoint = true;
                _index++;
                continue;
            }

            break;
        }

        var text = _source[start.._index];

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            throw new GuardSyntaxException($"Invalid numeric literal '{text}' at position {start}.");
        }

        return new Token(TokenKind.Number, text, start);
    }

    private Token ReadIdentifierOrKeyword()
    {
        var start = _index;

        while (_index < _source.Length &&
               (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
        {
            _index++;
        }

        var text = _source[start.._index];

        return text switch
        {
            "null" => new Token(TokenKind.Null, text, start),
            "true" => new Token(TokenKind.True, text, start),
            "false" => new Token(TokenKind.False, text, start),
            _ => new Token(TokenKind.Identifier, text, start)
        };
    }
}

public sealed class GuardSyntaxException : Exception
{
    public GuardSyntaxException(string message) : base(message) { }
}

public sealed class GuardEvaluationException : Exception
{
    public GuardEvaluationException(string message) : base(message) { }
}
