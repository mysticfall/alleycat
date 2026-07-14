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
}
