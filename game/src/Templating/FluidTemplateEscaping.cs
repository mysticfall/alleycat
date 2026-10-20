using System.Text;

namespace AlleyCat.Templating;

/// <summary>
/// Escapes text so the Fluid (Liquid) parser emits it as literal output: every tag opener — <c>{{</c> and
/// <c>{%</c> — is rewritten into an output tag whose string literal is the opener itself
/// (<c>{{ "{{" }}</c> / <c>{{ "{%" }}</c>). Compiling and rendering the escaped text reproduces the original
/// text character-for-character. Authored openers occur only inside generated string literals, so escaped
/// content cannot introduce executable Fluid tags, including raw blocks, however malformed its braces are.
/// </summary>
public static class FluidTemplateEscaping
{
    private const string OutputOpenEscape = """{{ "{{" }}""";
    private const string TagOpenEscape = """{{ "{%" }}""";

    /// <summary>
    /// Escapes both Fluid tag openers in the supplied text. The rewrite is a single left-to-right pass and is
    /// injective: each two-character opener maps to exactly one escape sequence, every other character passes
    /// through verbatim, and the two escape sequences are distinguishable from each other and from pass-through
    /// text because their quoted bodies differ, so distinct inputs always produce distinct escaped outputs.
    /// </summary>
    /// <param name="text">Text to escape; may contain any characters, including malformed template syntax.</param>
    /// <returns>Template source that renders back to <paramref name="text" /> exactly.</returns>
    public static string EscapeOpeners(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder builder = new(text.Length);
        int index = 0;
        while (index < text.Length)
        {
            char current = text[index];
            if (current == '{' && index + 1 < text.Length)
            {
                char next = text[index + 1];
                if (next == '{')
                {
                    _ = builder.Append(OutputOpenEscape);
                    index += 2;
                    continue;
                }

                if (next == '%')
                {
                    _ = builder.Append(TagOpenEscape);
                    index += 2;
                    continue;
                }
            }

            _ = builder.Append(current);
            index++;
        }

        return builder.ToString();
    }
}
