using System.Text;
using AlleyCat.Templating;
using Godot;

namespace AlleyCat.AI.Prompting;

/// <summary>
/// Ordered prompt stack that composes named sections and compiles the generated source.
/// </summary>
[GlobalClass]
public partial class PromptStack : Resource
{
    /// <summary>
    /// Ordered prompt sections to concatenate before template compilation.
    /// </summary>
    [Export]
    public PromptSection[] Sections { get; set; } = [];

    /// <summary>
    /// Builds the prompt source and compiles it through the supplied template compiler.
    /// </summary>
    /// <param name="compiler">Template compiler that receives the generated source.</param>
    /// <returns>The compiled template returned by <paramref name="compiler" />.</returns>
    public ITemplate Compile(ITemplateCompiler compiler)
    {
        ArgumentNullException.ThrowIfNull(compiler);

        string source = BuildSource().Trim();
        return compiler.Compile(source);
    }

    private string BuildSource()
    {
        PromptSection[] sections = Sections ?? [];
        StringBuilder builder = new();

        foreach (PromptSection? section in sections)
        {
            if (section is null)
            {
                throw new InvalidOperationException("Prompt stack contains a null section entry.");
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
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            throw new InvalidOperationException("Prompt sections must have a non-empty name.");
        }

        StringBuilder builder = new(sectionName.Length + 8);
        CharacterKind previousKind = CharacterKind.None;

        for (int index = 0; index < sectionName.Length; index++)
        {
            char character = sectionName[index];
            if (char.IsWhiteSpace(character))
            {
                AppendUnderscoreIfNeeded(builder);
                previousKind = CharacterKind.Separator;
                continue;
            }

            if (character is '_' or '-')
            {
                AppendUnderscoreIfNeeded(builder);
                previousKind = CharacterKind.Separator;
                continue;
            }

            if (!char.IsLetterOrDigit(character))
            {
                AppendUnderscoreIfNeeded(builder);
                previousKind = CharacterKind.Separator;
                continue;
            }

            CharacterKind currentKind = GetCharacterKind(character);
            if (ShouldInsertWordBoundary(sectionName, index, previousKind, currentKind))
            {
                AppendUnderscoreIfNeeded(builder);
            }

            _ = builder.Append(char.ToLowerInvariant(character));
            previousKind = currentKind;
        }

        string tagName = builder.ToString().Trim('_');
        return tagName.Length == 0
            ? throw new InvalidOperationException("Prompt section names must contain at least one letter or digit.")
            : tagName;
    }

    private static bool ShouldInsertWordBoundary(
        string sectionName,
        int index,
        CharacterKind previousKind,
        CharacterKind currentKind)
    {
        return previousKind is not CharacterKind.None and not CharacterKind.Separator
            && ((currentKind == CharacterKind.Upper && previousKind is CharacterKind.Lower or CharacterKind.Digit)
            || (currentKind == CharacterKind.Upper
                && previousKind == CharacterKind.Upper
                && index + 1 < sectionName.Length
                && char.IsLower(sectionName[index + 1]))
            || (currentKind == CharacterKind.Digit && previousKind != CharacterKind.Digit)
            || (currentKind != CharacterKind.Digit && previousKind == CharacterKind.Digit));
    }

    private static CharacterKind GetCharacterKind(char character)
    {
        return char.IsDigit(character)
            ? CharacterKind.Digit
            : char.IsUpper(character)
                ? CharacterKind.Upper
                : CharacterKind.Lower;
    }

    private static void AppendUnderscoreIfNeeded(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '_')
        {
            _ = builder.Append('_');
        }
    }

    private enum CharacterKind
    {
        None,
        Separator,
        Lower,
        Upper,
        Digit,
    }
}
