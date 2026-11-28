using Microsoft.Extensions.Logging;

namespace AlleyCat.Animations;

/// <summary>
/// Resolves a category's effective entry: agent preference, then template default, then the catalogue fallback
/// (ANIM-003 TR10).
/// </summary>
public static class AnimationDefaultResolver
{
    /// <summary>
    /// Resolves the effective entry from compatible entries in catalogue order.
    /// </summary>
    /// <param name="category">The category being resolved.</param>
    /// <param name="compatible">Compatible entries of the category, in catalogue order.</param>
    /// <param name="preference">The agent preference, if any.</param>
    /// <param name="templateDefault">The character template default, if any.</param>
    /// <param name="logger">Logger for ignored sources.</param>
    /// <returns>The effective entry, or <see langword="null" /> when the category has no compatible entry.</returns>
    public static AnimationEntry? Resolve(
        AnimationCategory category,
        IReadOnlyList<AnimationEntry> compatible,
        string? preference,
        string? templateDefault,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(compatible);
        ArgumentNullException.ThrowIfNull(logger);

        return Find(preference, "agent preference")
            ?? Find(templateDefault, "template default")
            ?? (compatible.Count > 0 ? compatible[0] : null);

        AnimationEntry? Find(string? id, string source)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            foreach (AnimationEntry entry in compatible)
            {
                if (entry.ID == id)
                {
                    return entry;
                }
            }

            logger.LogWarning(
                "Animation catalogue: ignoring {Source} '{EntryID}' for category {Category}; no compatible entry has that id",
                source, id, category.ToName());
            return null;
        }
    }
}
