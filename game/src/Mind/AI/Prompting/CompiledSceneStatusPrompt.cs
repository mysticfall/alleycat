using System.Collections.ObjectModel;
using System.Text;
using AlleyCat.Common;
using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Templating;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>Session-start compiled current-scene-status stack that renders every projection from a fresh typed snapshot.</summary>
internal sealed class CompiledSceneStatusPrompt(
    IReadOnlyList<CompiledSceneStatusSection> sections,
    SceneStatusProjectorRegistry projectors)
{
    private readonly IReadOnlyList<CompiledSceneStatusSection> _sections = new ReadOnlyCollection<CompiledSceneStatusSection>(
            [.. sections ?? throw new ArgumentNullException(nameof(sections))]);
    private readonly SceneStatusProjectorRegistry _projectors = projectors ?? throw new ArgumentNullException(nameof(projectors));

    /// <summary>Renders the stack without swallowing projection or template failures.</summary>
    public async Task<string> RenderAsync(SceneStatusBuildContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        StringBuilder builder = new();
        foreach (CompiledSceneStatusSection section in _sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content = section.Section is ProjectionPromptSection projectionSection
                ? await ProjectionPromptSection.RenderAsync(
                    section.Template,
                    _projectors.Project(
                        projectionSection.ProjectorID,
                        projectionSection.ResolveExpectedRootType(),
                        context))
                : await section.Template.RenderAsync(EmptyRenderContext.Instance);
            if (content.Length > 0)
            {
                PseudoXmlFormatter.AppendBlock(builder, section.Section.Name, content, "Current scene status sections");
            }
        }

        return builder.ToString().Trim();
    }

    private static class EmptyRenderContext
    {
        public static IReadOnlyDictionary<string, object?> Instance
        {
            get;
        } =
            new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());
    }
}

/// <summary>One independently compiled status section.</summary>
internal sealed record CompiledSceneStatusSection(PromptSection Section, ITemplate Template);
