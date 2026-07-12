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
    /// <param name="serviceProvider">Service provider used to resolve prompt writer and template compiler.</param>
    /// <returns>The compiled template returned by the resolved template compiler.</returns>
    public ITemplate Compile(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        IPromptWriter writer = serviceProvider.GetRequiredService<IPromptWriter>();
        ITemplateCompiler compiler = serviceProvider.GetRequiredService<ITemplateCompiler>();
        string source = writer.Write(Sections ?? []).Trim();
        return compiler.Compile(source);
    }
}
