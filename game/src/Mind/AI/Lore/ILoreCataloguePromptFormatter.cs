namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Formats catalogue lore listings for prompt-section injection: exact entry ID, title, and optional description
/// without bodies, grouped under World, Characters, and Locations headings in that order with empty groups omitted
/// (AI-004 requirement 45).
/// </summary>
public interface ILoreCataloguePromptFormatter
{
    /// <summary>
    /// Formats entries in their supplied deterministic group and within-group order.
    /// </summary>
    string Format(IReadOnlyList<LoreEntry> entries);
}
