using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Templating;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

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
    /// Builds the prompt source and compiles it through services resolved from the supplied provider.
    /// </summary>
    /// <param name="buildContext">Build context used to resolve services and runtime-backed section content.</param>
    /// <param name="cancellationToken">Cancellation token for asynchronous prompt building.</param>
    /// <returns>The compiled template returned by the resolved template compiler.</returns>
    public async Task<ITemplate> CompileAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);

        IPromptWriter writer = buildContext.Services.GetRequiredService<IPromptWriter>();
        ITemplateCompiler compiler = buildContext.Services.GetRequiredService<ITemplateCompiler>();
        string source = (await writer.WriteAsync(Sections ?? [], buildContext, cancellationToken)).Trim();
        return compiler.Compile(source);
    }

    /// <summary>
    /// Compiles and validates a current-scene-status stack at session start. Projection sections are compiled
    /// independently so each one can later render from its declared typed root.
    /// </summary>
    internal async Task<CompiledSceneStatusPrompt> CompileSceneStatusAsync(
        PromptSectionBuildContext buildContext,
        SceneStatusProjectorRegistry projectors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        ArgumentNullException.ThrowIfNull(projectors);

        PromptSection[] sections = Sections ?? [];
        projectors.ValidateBindings(sections);
        ITemplateCompiler compiler = buildContext.Services.GetRequiredService<ITemplateCompiler>();
        List<CompiledSceneStatusSection> compiled = new(sections.Length);
        foreach (PromptSection? section in sections)
        {
            if (section is null)
            {
                throw new InvalidOperationException("CurrentSceneStatus prompt stack cannot contain a null section.");
            }

            string source = await section.GetContentAsync(buildContext, cancellationToken);
            compiled.Add(new CompiledSceneStatusSection(section, compiler.Compile(source)));
        }

        return new CompiledSceneStatusPrompt(compiled, projectors);
    }
}
