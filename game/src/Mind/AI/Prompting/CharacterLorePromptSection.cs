using AlleyCat.Mind.AI.Lore;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed lore for every scene character from the owning character's perspective.
/// </summary>
[GlobalClass]
public partial class CharacterLorePromptSection : PromptSection
{
    /// <inheritdoc />
    public override Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);

        LoreQuery query = LorePromptSelection.CreateSceneCharacterQuery(
            buildContext,
            nameof(CharacterLorePromptSection));

        return LorePromptSelection.QueryAndFormatAsync(buildContext, query, cancellationToken);
    }
}
