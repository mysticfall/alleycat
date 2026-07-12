using System.Text;
using AlleyCat.Core;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Writes prompt sections as a simple pseudo-XML text block.
/// </summary>
[Tool]
[GlobalClass]
public sealed partial class PseudoXmlPromptWriter : Resource, IPromptWriter, IServiceRegistrar
{
    /// <inheritdoc />
    public string Write(IReadOnlyCollection<PromptSection> sections) => Format(sections);

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services)
        => services.AddSingleton<IPromptWriter>(this);

    internal static string Format(IReadOnlyCollection<PromptSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);

        if (sections.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (PromptSection? section in sections)
        {
            if (section is null)
            {
                throw new InvalidOperationException("Prompt writer cannot serialise a null section entry.");
            }

            AppendSection(builder, section);
        }

        return builder.ToString();
    }

    private static void AppendSection(StringBuilder builder, PromptSection section)
    {
        string tagName = FormatSectionTagName(section.Name);
        string content = section.GetContent() ?? string.Empty;

        _ = builder.Append('<').Append(tagName).AppendLine(">");
        _ = builder.Append(content);
        if (content.Length == 0 || content[^1] != '\n')
        {
            _ = builder.AppendLine();
        }

        _ = builder.Append("</").Append(tagName).AppendLine(">");
        _ = builder.AppendLine();
    }

    private static string FormatSectionTagName(string? sectionName)
    {
        return string.IsNullOrWhiteSpace(sectionName)
            ? throw new InvalidOperationException("Prompt sections must have a non-empty name.")
            : sectionName
                .Replace('<', '_')
                .Replace('>', '_')
                .Replace('/', '_');
    }
}
