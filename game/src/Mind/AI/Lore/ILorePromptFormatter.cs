namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Formats lore query results for prompt-section injection.
/// </summary>
public interface ILorePromptFormatter
{
    /// <summary>
    /// Formats entries in their supplied deterministic query order.
    /// </summary>
    string Format(IReadOnlyList<LoreEntry> entries);
}
