using System.Globalization;

namespace Desicon.Workflow.Core.Guards;

/// <summary>
/// Supplies field values to the guard evaluator. Implemented by the request
/// aggregate so that a guard can only see fields the aggregate chooses to
/// expose -- never arbitrary properties via reflection.
/// </summary>
public interface IGuardFieldSource
{
    bool TryGetField(string name, out object? value);
}

/// <summary>
/// Evaluates a parsed guard expression against a field source.
///
/// Security posture:
///   * functions are restricted to a fixed allowlist
///   * fields come from an explicit dictionary, not reflection
///   * an unknown field or function is an error, never a silent null
///
/// That last point matters. A guard that silently evaluates to false when a
/// field name is misspelled would block every request in that state and look
/// like a workflow bug; a guard that silently evaluates to true would wave
/// them all through. Failing loudly at definition-publish time is the only
/// safe behaviour.
/// </summary>
public sealed class GuardEvaluator
{
    private static readonly HashSet<string> AllowedFunctions = new(StringComparer.Ordinal)
    {
        "Abs",
        "Min",
        "Max",
        "Round",
        "IsNull",
        "NotNull",
        "Len",
        "Lower",
        "Upper",
        "DaysBetween"
    };

    public static IReadOnlyCollection<string> FunctionAllowlist => AllowedFunctions;

    public bool EvaluateBoolean(GuardNode node, IGuardFieldSource fields)
    {
        var result = Evaluate(node, fields);

        if (result is bool b)
        {
            return b;
        }

        throw new GuardEvaluationException(
            $"Guard expression returned {Describe(result)} where a boolean was required.");
    }

    public object? Evaluate(GuardNode node, IGuardFieldSource fields)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(fields);

        return node switch
        {
            LiteralNode literal => literal.Value,
            FieldNode field => ResolveField(field, fields),
            UnaryNode unary => EvaluateUnary(unary, fields),
            BinaryNode binary => EvaluateBinary(binary, fields),
            FunctionNode function => EvaluateFunction(function, fields),
            _ => throw new GuardEvaluationException($"Unsupported node type {node.GetType().Name}.")
        };
    }

    private static object? ResolveField(FieldNode node, IGuardFieldSource fields)
    {
        if (!fields.TryGetField(node.Name, out var value))
        {
            throw new GuardEvaluationException(
                $"Guard references unknown field '{node.Name}'. " +
                "Check the workflow definition against the module's field schema.");
        }

        return value;
    }

    private object? EvaluateUnary(UnaryNode node, IGuardFieldSource fields)
    {
        var operand = Evaluate(node.Operand, fields);

        return node.Operator switch
        {
            "!" => !ToBoolean(operand),
            "-" => -ToDecimal(operand),
            _ => throw new GuardEvaluationException($"Unsupported unary operator '{node.Operator}'.")
        };
    }

    private object? EvaluateBinary(BinaryNode node, IGuardFieldSource fields)
    {
        // Short-circuit before evaluating the right-hand side, so that a guard
        // such as "RetiresAdvanceId != null && AdvanceAmountNgn > 0" is safe
        // when there is no linked advance.
        if (node.Operator == "&&")
        {
            return ToBoolean(Evaluate(node.Left, fields)) &&
                   ToBoolean(Evaluate(node.Right, fields));
        }

        if (node.Operator == "||")
        {
            return ToBoolean(Evaluate(node.Left, fields)) ||
                   ToBoolean(Evaluate(node.Right, fields));
        }

        var left = Evaluate(node.Left, fields);
        var right = Evaluate(node.Right, fields);

        switch (node.Operator)
        {
            case "==":
                return AreEqual(left, right);

            case "!=":
                return !AreEqual(left, right);

            case ">":
                return Compare(left, right) > 0;

            case ">=":
                return Compare(left, right) >= 0;

            case "<":
                return Compare(left, right) < 0;

            case "<=":
                return Compare(left, right) <= 0;

            case "+":
                if (left is string || right is string)
                {
                    return ToStringValue(left) + ToStringValue(right);
                }

                return ToDecimal(left) + ToDecimal(right);

            case "-":
                return ToDecimal(left) - ToDecimal(right);

            default:
                throw new GuardEvaluationException(
                    $"Unsupported binary operator '{node.Operator}'.");
        }
    }

    private object? EvaluateFunction(FunctionNode node, IGuardFieldSource fields)
    {
        if (!AllowedFunctions.Contains(node.Name))
        {
            throw new GuardEvaluationException(
                $"Function '{node.Name}' is not on the guard allowlist. " +
                $"Permitted: {string.Join(", ", AllowedFunctions.Order(StringComparer.Ordinal))}.");
        }

        var args = node.Arguments.Select(a => Evaluate(a, fields)).ToArray();

        return node.Name switch
        {
            "Abs" => Math.Abs(ToDecimal(Single(args, "Abs"))),
            "Min" => Math.Min(ToDecimal(Pair(args, "Min").Item1), ToDecimal(Pair(args, "Min").Item2)),
            "Max" => Math.Max(ToDecimal(Pair(args, "Max").Item1), ToDecimal(Pair(args, "Max").Item2)),
            "Round" => Math.Round(ToDecimal(Pair(args, "Round").Item1),
                                  (int)ToDecimal(Pair(args, "Round").Item2),
                                  MidpointRounding.AwayFromZero),
            "IsNull" => Single(args, "IsNull") is null,
            "NotNull" => Single(args, "NotNull") is not null,
            "Len" => (decimal)ToStringValue(Single(args, "Len")).Length,
            "Lower" => ToStringValue(Single(args, "Lower")).ToLowerInvariant(),
            "Upper" => ToStringValue(Single(args, "Upper")).ToUpperInvariant(),
            "DaysBetween" => DaysBetween(Pair(args, "DaysBetween")),
            _ => throw new GuardEvaluationException($"Function '{node.Name}' has no implementation.")
        };
    }

    private static decimal DaysBetween((object?, object?) args)
    {
        var from = ToDateTime(args.Item1);
        var to = ToDateTime(args.Item2);
        return (decimal)(to - from).TotalDays;
    }

    private static object? Single(object?[] args, string function)
    {
        if (args.Length != 1)
        {
            throw new GuardEvaluationException($"{function} expects 1 argument, got {args.Length}.");
        }

        return args[0];
    }

    private static (object?, object?) Pair(object?[] args, string function)
    {
        if (args.Length != 2)
        {
            throw new GuardEvaluationException($"{function} expects 2 arguments, got {args.Length}.");
        }

        return (args[0], args[1]);
    }

    private static bool AreEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            return ToDecimal(left) == ToDecimal(right);
        }

        if (left is bool lb && right is bool rb)
        {
            return lb == rb;
        }

        // Guid-to-string comparison is deliberate: definitions compare actor
        // identifiers such as "ActorId != PostedByUserId" where one side may
        // arrive as a Guid and the other as its string form.
        return string.Equals(ToStringValue(left), ToStringValue(right), StringComparison.Ordinal);
    }

    private static int Compare(object? left, object? right)
    {
        if (left is null || right is null)
        {
            throw new GuardEvaluationException(
                "Cannot order-compare a null value. Use IsNull or NotNull instead.");
        }

        if (left is DateTime || right is DateTime ||
            left is DateTimeOffset || right is DateTimeOffset)
        {
            return ToDateTime(left).CompareTo(ToDateTime(right));
        }

        return ToDecimal(left).CompareTo(ToDecimal(right));
    }

    private static bool IsNumeric(object value) =>
        value is decimal or int or long or double or float or short or byte;

    private static bool ToBoolean(object? value) => value switch
    {
        bool b => b,
        null => throw new GuardEvaluationException("Cannot treat null as a boolean."),
        _ => throw new GuardEvaluationException(
            $"Cannot treat {Describe(value)} as a boolean.")
    };

    private static decimal ToDecimal(object? value) => value switch
    {
        decimal d => d,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        double dd => (decimal)dd,
        float f => (decimal)f,
        string str when decimal.TryParse(
            str, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
        null => throw new GuardEvaluationException("Cannot treat null as a number."),
        _ => throw new GuardEvaluationException($"Cannot treat {Describe(value)} as a number.")
    };

    private static DateTime ToDateTime(object? value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        string s when DateTime.TryParse(
            s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            => parsed,
        null => throw new GuardEvaluationException("Cannot treat null as a date."),
        _ => throw new GuardEvaluationException($"Cannot treat {Describe(value)} as a date.")
    };

    private static string ToStringValue(object? value) =>
        value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    private static string Describe(object? value) =>
        value is null ? "null" : $"a value of type {value.GetType().Name}";
}
