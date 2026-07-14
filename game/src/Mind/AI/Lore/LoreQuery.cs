namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Describes runtime lore query intent.
/// </summary>
public sealed record LoreQuery
{
    private LoreQuery(bool essentialOnly)
    {
        EssentialOnly = essentialOnly;
    }

    /// <summary>
    /// Gets whether the query selects only lore marked as essential.
    /// </summary>
    public bool EssentialOnly
    {
        get;
    }

    /// <summary>
    /// Query selecting all essential lore entries.
    /// </summary>
    public static LoreQuery Essential() => new(essentialOnly: true);
}
