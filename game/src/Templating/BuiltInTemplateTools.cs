using System.Globalization;
using System.Text;

namespace AlleyCat.Templating;

/// <summary>
/// Built-in template helper/tool registrations.
/// </summary>
public static class BuiltInTemplateTools
{
    /// <summary>
    /// All built-in tools in deterministic registration order.
    /// </summary>
    public static IReadOnlyList<ITemplateTool> All
    {
        get;
    } =
    [
        new DelegateTemplateTool("add", Add),
        new DelegateTemplateTool("eq", Eq),
        new DelegateTemplateTool("nf", NumberFormat),
        new DelegateTemplateTool("repeat", Repeat),
    ];

    private static string Add(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2)
        {
            return string.Empty;
        }

        long total = ParseInteger(arguments[0]) + ParseInteger(arguments[1]);
        return total.ToString(CultureInfo.InvariantCulture);
    }

    private static string Eq(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2)
        {
            return string.Empty;
        }

        string left = Convert.ToString(arguments[0], CultureInfo.CurrentCulture) ?? string.Empty;
        string right = Convert.ToString(arguments[1], CultureInfo.CurrentCulture) ?? string.Empty;

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? "true" : string.Empty;
    }

    private static string NumberFormat(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count == 0 || !TryParseNumber(arguments[0], out double value))
        {
            return string.Empty;
        }

        int precision = 3;
        if (arguments.Count > 1)
        {
            precision = (int)Math.Clamp(ParseInteger(arguments[1]), 0, 99);
        }

        return value.ToString($"F{precision}", CultureInfo.CurrentCulture);
    }

    private static string Repeat(IReadOnlyList<object?> arguments)
    {
        if (arguments.Count < 2)
        {
            return string.Empty;
        }

        string value = Convert.ToString(arguments[0], CultureInfo.CurrentCulture) ?? string.Empty;
        int count = (int)Math.Max(0, Math.Min(int.MaxValue, ParseInteger(arguments[1])));
        if (count == 0 || value.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(value.Length * count);
        for (int index = 0; index < count; index++)
        {
            _ = builder.Append(value);
        }

        return builder.ToString();
    }

    private static long ParseInteger(object? value)
    {
        if (value is null)
        {
            return 0;
        }

        if (value is string text)
        {
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out long currentResult)
                ? currentResult
                : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long invariantResult)
                ? invariantResult
                : 0;
        }

        if (value is IConvertible convertible)
        {
            try
            {
                return convertible.ToInt64(CultureInfo.CurrentCulture);
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        string converted = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        return long.TryParse(converted, NumberStyles.Integer, CultureInfo.CurrentCulture, out long result)
            ? result
            : long.TryParse(converted, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : 0;
    }

    private static bool TryParseNumber(object? value, out double result)
    {
        if (value is null)
        {
            result = 0;
            return false;
        }

        if (value is string text)
        {
            return double.TryParse(
                    text,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.CurrentCulture,
                    out result)
                || double.TryParse(
                    text,
                    NumberStyles.Float | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture,
                    out result);
        }

        if (value is IConvertible convertible)
        {
            try
            {
                result = convertible.ToDouble(CultureInfo.CurrentCulture);
                return true;
            }
            catch (FormatException)
            {
            }
            catch (InvalidCastException)
            {
            }
            catch (OverflowException)
            {
            }
        }

        string converted = Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
        return double.TryParse(
                converted,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.CurrentCulture,
                out result)
            || double.TryParse(
                converted,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture,
                out result);
    }
}
