using AlleyCat.Mind.AI.Lore;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed prompt section that injects the observer-specific essential world lore batch for the active
/// content context.
/// </summary>
[GlobalClass]
public partial class EssentialLorePromptSection : PromptSection
{
    /// <inheritdoc />
    public override Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);

        LoreQuery query = LorePromptSelection.CreateEssentialQuery(
            buildContext,
            nameof(EssentialLorePromptSection));

        return LorePromptSelection.QueryAndFormatAsync(buildContext, query, cancellationToken);
    }
}
