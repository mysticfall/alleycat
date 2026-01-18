using System.Globalization;
using System.Text.RegularExpressions;

namespace AlleyCat.Common;

public static partial class StringUtils
{
    public static string ExtractJson(this string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var firstBrace = text.IndexOf('{');
        var lastBrace = text.LastIndexOf('}');

        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            return text.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return text;
    }

    [GeneratedRegex("[_-]")]
    private static partial Regex DashOrUnderscore();

    public static string ToTitleCase(this string text)
    {
        var culture = CultureInfo.InvariantCulture;
        var textInfo = culture.TextInfo;

        return textInfo.ToTitleCase(DashOrUnderscore().Replace(text, " "));
    }
}