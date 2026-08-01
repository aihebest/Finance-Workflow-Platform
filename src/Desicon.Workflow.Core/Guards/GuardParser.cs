using System.Globalization;

namespace Desicon.Workflow.Core.Guards;

public abstract record GuardNode;

public sealed record LiteralNode(object? Value) : GuardNode;

public sealed record FieldNode(string Name) : GuardNode;

public sealed record UnaryNode(string Operator, GuardNode Operand) : GuardNode;

public sealed record BinaryNode(string Operator, GuardNode Left, GuardNode Right) : GuardNode;

public sealed record FunctionNode(string Name, IReadOnlyList<GuardNode> Arguments) : GuardNode;

/// <summary>
/// Recursive-descent parser for guard expressions.
///
/// Precedence, lowest to highest:
///   ||
///   &amp;&amp;
///   == !=
///   &gt; &gt;= &lt; &lt;=
///   + -            (binary)
///   ! -            (unary)
///   primary        (literal, field, function call, parenthesised expression)
///
/// The output is a plain object graph. Nothing is compiled, emitted or
/// reflected over, so a malformed or hostile definition can only ever produce
/// a parse failure -- never code execution.
/// </summary>
public sealed class GuardParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _index;

    private GuardParser(IReadOnlyList<Token> tokens) => _tokens = tokens;

    public static GuardNode Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new GuardSyntaxException("Guard expression is empty.");
        }

        var tokens = new GuardTokenizer(expression).Tokenize();
        var parser = new GuardParser(tokens);
        var node = parser.ParseOr();

        if (parser.Current.Kind != TokenKind.End)
        {
            throw new GuardSyntaxException(
                $"Unexpected token '{parser.Current.Text}' at position {parser.Current.Position}.");
        }

        return node;
    }

    private Token Current => _tokens[_index];

    private bool MatchOperator(params string[] operators)
    {
        if (Current.Kind != TokenKind.Operator)
        {
            return false;
        }

        return Array.IndexOf(operators, Current.Text) >= 0;
    }

    private GuardNode ParseOr()
    {
        var left = ParseAnd();

        while (MatchOperator("||"))
        {
            _index++;
            var right = ParseAnd();
            left = new BinaryNode("||", left, right);
        }

        return left;
    }

    private GuardNode ParseAnd()
    {
        var left = ParseEquality();

        while (MatchOperator("&&"))
        {
            _index++;
            var right = ParseEquality();
            left = new BinaryNode("&&", left, right);
        }

        return left;
    }

    private GuardNode ParseEquality()
    {
        var left = ParseComparison();

        while (MatchOperator("==", "!="))
        {
            var op = Current.Text;
            _index++;
            var right = ParseComparison();
            left = new BinaryNode(op, left, right);
        }

        return left;
    }

    private GuardNode ParseComparison()
    {
        var left = ParseAdditive();

        while (MatchOperator(">", ">=", "<", "<="))
        {
            var op = Current.Text;
            _index++;
            var right = ParseAdditive();
            left = new BinaryNode(op, left, right);
        }

        return left;
    }

    private GuardNode ParseAdditive()
    {
        var left = ParseUnary();

        while (MatchOperator("+", "-"))
        {
            var op = Current.Text;
            _index++;
            var right = ParseUnary();
            left = new BinaryNode(op, left, right);
        }

        return left;
    }

    private GuardNode ParseUnary()
    {
        if (MatchOperator("!", "-"))
        {
            var op = Current.Text;
            _index++;
            return new UnaryNode(op, ParseUnary());
        }

        return ParsePrimary();
    }

    private GuardNode ParsePrimary()
    {
        var token = Current;

        switch (token.Kind)
        {
            case TokenKind.Number:
                _index++;
                return new LiteralNode(
                    decimal.Parse(token.Text, NumberStyles.Number, CultureInfo.InvariantCulture));

            case TokenKind.StringLiteral:
                _index++;
                return new LiteralNode(token.Text);

            case TokenKind.Null:
                _index++;
                return new LiteralNode(null);

            case TokenKind.True:
                _index++;
                return new LiteralNode(true);

            case TokenKind.False:
                _index++;
                return new LiteralNode(false);

            case TokenKind.LeftParen:
            {
                _index++;
                var inner = ParseOr();
                Expect(TokenKind.RightParen, ")");
                return inner;
            }

            case TokenKind.Identifier:
            {
                _index++;

                if (Current.Kind == TokenKind.LeftParen)
                {
                    return ParseFunctionCall(token.Text);
                }

                return new FieldNode(token.Text);
            }

            default:
                throw new GuardSyntaxException(
                    $"Unexpected token '{token.Text}' at position {token.Position}.");
        }
    }

    private FunctionNode ParseFunctionCall(string name)
    {
        Expect(TokenKind.LeftParen, "(");

        var arguments = new List<GuardNode>();

        if (Current.Kind != TokenKind.RightParen)
        {
            arguments.Add(ParseOr());

            while (Current.Kind == TokenKind.Comma)
            {
                _index++;
                arguments.Add(ParseOr());
            }
        }

        Expect(TokenKind.RightParen, ")");
        return new FunctionNode(name, arguments);
    }

    private void Expect(TokenKind kind, string display)
    {
        if (Current.Kind != kind)
        {
            throw new GuardSyntaxException(
                $"Expected '{display}' at position {Current.Position} but found '{Current.Text}'.");
        }

        _index++;
    }
}
