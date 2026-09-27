using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Runtime-backed prompt section that renders the grouped catalogue of remaining available lore for the active
/// content root and observer perspective. Entries the shared stack's automatic essential-world and scene-character
/// injection already selected are excluded by entry ID through the same shared selection logic, never by duplicated
/// rules or whole-category removal (AI-004 requirements 44 to 46).
/// </summary>
[GlobalClass]
public partial class LoreCataloguePromptSection : PromptSection
{
    /// <inheritdoc />
    public override async Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);

        // Reuse the automatic sections' actual selection logic so the exclusion set is exactly what automatic
        // injection supplies, and the same identity validation protects the catalogue (AI-004 requirement 44). The
        // essential query already validates the observer lore identity, so the catalogue query cannot see an
        // invalid observer.
        LoreQuery essentialQuery = LorePromptSelection.CreateEssentialQuery(
            buildContext,
            nameof(LoreCataloguePromptSection));
        LoreQuery sceneCharacterQuery = LorePromptSelection.CreateSceneCharacterQuery(
            buildContext,
            nameof(LoreCataloguePromptSection));
        LoreCatalogueQuery catalogueQuery = new(buildContext.Character.FullId);

        ILoreQueryService queryService = buildContext.Services.GetRequiredService<ILoreQueryService>();
        ILoreCataloguePromptFormatter formatter =
            buildContext.Services.GetRequiredService<ILoreCataloguePromptFormatter>();
        ContentContext content = buildContext.Scene.Content;
        IReadOnlyList<LoreEntry> essentialEntries = await queryService.QueryAsync(
            content,
            essentialQuery,
            cancellationToken);
        IReadOnlyList<LoreEntry> sceneCharacterEntries = await queryService.QueryAsync(
            content,
            sceneCharacterQuery,
            cancellationToken);
        IReadOnlyList<LoreEntry> catalogueEntries = await queryService.QueryCatalogueAsync(
            content,
            catalogueQuery,
            cancellationToken);

        HashSet<string> automaticallyInjectedIDs = new(StringComparer.Ordinal);
        foreach (LoreEntry entry in essentialEntries.Concat(sceneCharacterEntries))
        {
            if (entry.ID is not null)
            {
                _ = automaticallyInjectedIDs.Add(entry.ID);
            }
        }

        List<LoreEntry> remainingEntries = new(catalogueEntries.Count);
        foreach (LoreEntry entry in catalogueEntries)
        {
            if (entry.ID is null || !automaticallyInjectedIDs.Contains(entry.ID))
            {
                remainingEntries.Add(entry);
            }
        }

        return formatter.Format(remainingEntries);
    }
}
