using AlleyCat.Templating;
using Godot;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Scenario manager that returns one fixed description authored in a text file on every turn.
/// </summary>
/// <remarks>
/// The authored body may reference core render-context keys such as <c>{{player.FullId}}</c> and
/// <c>{{character.FullId}}</c>; the manager renders the body through the game's template compiler against the turn's
/// core render context before creating the <see cref="Scenario" />.
/// </remarks>
[GlobalClass]
public partial class FixedScenarioManager : ScenarioManager
{
    private static readonly MarkdownPipeline _frontmatterPipeline = new MarkdownPipelineBuilder()
        .UseYamlFrontMatter()
        .Build();

    /// <summary>
    /// Godot resource path to the scenario narrative text, for example
    /// <c>res://lore/wiki/scenarios/interrogation.md</c>.
    /// </summary>
    [Export(PropertyHint.File, "*.md,*.txt")]
    public string DescriptionPath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override Scenario? GetCurrentScenario(ScenarioContext previous, IReadOnlyDictionary<string, object?> coreContext)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(coreContext);

        if (string.IsNullOrWhiteSpace(DescriptionPath))
        {
            throw new InvalidOperationException(
                $"{nameof(FixedScenarioManager)} requires a non-empty Godot resource path to the scenario description.");
        }

        using var file = Godot.FileAccess.Open(DescriptionPath, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException(
                $"{nameof(FixedScenarioManager)} could not read scenario description file '{DescriptionPath}'. Godot FileAccess error: {error}.");
        }

        string body = StripLeadingFrontmatter(file.GetAsText());
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException(
                $"{nameof(FixedScenarioManager)} requires a non-empty scenario description in '{DescriptionPath}'.");
        }

        string rendered;
        try
        {
            ITemplateCompiler compiler = Game.Instance.GetRequiredService<ITemplateCompiler>();
            rendered = compiler.Compile(body).Render(coreContext);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{nameof(FixedScenarioManager)} failed to compile or render the scenario description template in '{DescriptionPath}'.",
                exception);
        }

        return new Scenario(rendered);
    }

    /// <summary>
    /// Strips one leading well-formed front-matter block using Markdig's YAML front-matter extension, returning the
    /// exact body content sliced from the original text after the block's closing delimiter line.
    /// </summary>
    /// <remarks>
    /// A file without a leading well-formed block — including an unterminated leading <c>---</c> — is parsed by
    /// Markdig as ordinary content and returned unchanged so it flows through the ordinary content checks.
    /// </remarks>
    private static string StripLeadingFrontmatter(string content)
    {
        MarkdownDocument document = Markdown.Parse(content, _frontmatterPipeline);
        YamlFrontMatterBlock? frontmatter = document.Descendants<YamlFrontMatterBlock>().FirstOrDefault();
        if (frontmatter is null)
        {
            return content;
        }

        int bodyStart = frontmatter.Span.End + 1;
        if (bodyStart < content.Length && content[bodyStart] == '\r')
        {
            bodyStart++;
        }

        if (bodyStart < content.Length && content[bodyStart] == '\n')
        {
            bodyStart++;
        }

        return content[bodyStart..];
    }
}
