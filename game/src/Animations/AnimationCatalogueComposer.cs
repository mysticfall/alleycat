using Microsoft.Extensions.Logging;

namespace AlleyCat.Animations;

/// <summary>
/// One enabled pack contributed to a character catalogue.
/// </summary>
/// <param name="Manifest">The validated pack.</param>
/// <param name="Priority">Application priority; lower priorities apply first.</param>
public sealed record AnimationPackLayer(AnimationPackManifest Manifest, int Priority);

/// <summary>
/// Composes enabled packs additively into one ordered catalogue (ANIM-003 TR7, TR8, TR10).
/// </summary>
public static class AnimationCatalogueComposer
{
    /// <summary>
    /// Applies packs in ascending priority order, with ties broken by ordinal pack id. A later-applied entry with the
    /// same (category, id) replaces the earlier one, takes the overriding pack's position and logs one message.
    /// </summary>
    /// <returns>All entries in catalogue order: packs in application order, then manifest order.</returns>
    /// <exception cref="InvalidOperationException">Thrown when two enabled packs share a pack id.</exception>
    public static IReadOnlyList<AnimationEntry> Compose(IEnumerable<AnimationPackLayer> layers, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(logger);

        AnimationPackLayer[] ordered = [.. layers
            .OrderBy(layer => layer.Priority)
            .ThenBy(layer => layer.Manifest.ID, StringComparer.Ordinal)];

        string[] duplicates = [.. ordered
            .GroupBy(layer => layer.Manifest.ID, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)];
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Animation catalogue has more than one enabled pack with id {string.Join(", ", duplicates.Select(id => $"'{id}'"))} ("
                + string.Join("; ", ordered.Where(layer => duplicates.Contains(layer.Manifest.ID)).Select(layer => layer.Manifest.ManifestPath))
                + ").");
        }

        // Insertion-ordered list keyed by identity; an override removes the earlier entry and appends the new one.
        List<AnimationEntry> entries = [];
        Dictionary<(AnimationCategory, string), AnimationEntry> byIdentity = [];
        foreach (AnimationPackLayer layer in ordered)
        {
            foreach (AnimationEntry entry in layer.Manifest.Entries)
            {
                (AnimationCategory, string) key = (entry.Category, entry.ID);
                if (byIdentity.TryGetValue(key, out AnimationEntry? overridden))
                {
                    _ = entries.Remove(overridden);
                    logger.LogInformation(
                        "Animation catalogue: {Category} entry '{EntryID}' from pack '{OverridingPack}' overrides pack '{OverriddenPack}'",
                        entry.Category.ToName(), entry.ID, entry.PackID, overridden.PackID);
                }

                byIdentity[key] = entry;
                entries.Add(entry);
            }
        }

        return entries.AsReadOnly();
    }
}
