using System.Text;

namespace AlleyCat.Common;

/// <summary>
/// Formats prompt content as simple pseudo-XML blocks.
/// </summary>
public static class PseudoXmlFormatter
{
    /// <summary>
    /// Appends one pseudo-XML block using the supplied tag name and content.
    /// </summary>
    /// <param name="builder">The destination builder.</param>
    /// <param name="tagName">The authored tag name to sanitise for pseudo-XML delimiters.</param>
    /// <param name="content">The block content. Content is preserved and is not escaped.</param>
    /// <param name="tagDescription">The authored value description used in validation errors.</param>
    public static void AppendBlock(
        StringBuilder builder,
        string? tagName,
        string content,
        string tagDescription = "Pseudo-XML tags")
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(content);

        string formattedTagName = FormatTagName(tagName, tagDescription);

        _ = builder.Append('<').Append(formattedTagName).AppendLine(">");
        _ = builder.Append(content);
        if (content.Length == 0 || content[^1] != '\n')
        {
            _ = builder.AppendLine();
        }

        _ = builder.Append("</").Append(formattedTagName).AppendLine(">");
        _ = builder.AppendLine();
    }

    /// <summary>
    /// Sanitises a pseudo-XML tag name while preserving other authored punctuation and spacing.
    /// </summary>
    public static string FormatTagName(string? tagName, string tagDescription = "Pseudo-XML tags")
        => string.IsNullOrWhiteSpace(tagName)
            ? throw new InvalidOperationException($"{tagDescription} must have a non-empty name.")
            : tagName
                .Replace('<', '_')
                .Replace('>', '_')
                .Replace('/', '_');
}
