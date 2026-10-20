using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Base Godot-authorable resource for a named prompt section.
/// </summary>
[GlobalClass]
public abstract partial class PromptSection : Resource
{
    /// <summary>
    /// Human-readable section name used to derive the generated prompt section tag.
    /// </summary>
    [Export]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the section's content is authored data that must survive template compilation unchanged rather
    /// than template source. Opaque sections are escaped at the prompt-writer boundary so their content
    /// reaches the final rendered instruction verbatim; the default keeps content template-processed.
    /// </summary>
    public virtual bool IsTemplateOpaque => false;

    /// <summary>
    /// Returns the section content contributed to a prompt stack.
    /// </summary>
    /// <param name="buildContext">Services and scene state for prompt construction.</param>
    /// <param name="cancellationToken">Cancellation token for asynchronous prompt building.</param>
    /// <returns>The prompt text for this section.</returns>
    public abstract Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default);
}
