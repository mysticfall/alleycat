using AlleyCat.Core.Content;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Asynchronous runtime access to content-scoped lore.
/// </summary>
public interface ILoreQueryService
{
    /// <summary>
    /// Queries lore for one observer and an ordered batch of subjects in the supplied content context.
    /// </summary>
    Task<IReadOnlyList<LoreEntry>> QueryAsync(
        ContentContext content,
        LoreQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries every runtime-eligible lore entry in the bound content root and observer perspective, including
    /// entries that prompt-side automatic injection selects (AI-004 requirement 44). Entries return in World,
    /// Characters, Locations group order with deterministic ordering inside each group. Ambiguous entry IDs
    /// within the scope fail the query (AI-004 requirement 43).
    /// </summary>
    Task<IReadOnlyList<LoreEntry>> QueryCatalogueAsync(
        ContentContext content,
        LoreCatalogueQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up lore entries by exact canonical IDs in the query's selected lore base: one observer's
    /// perspective — the only base LLM-facing composition ever selects — or the canonical wiki without an
    /// observer for non-LLM consumers (AI-004 requirements 48 and 52). Results align with the query's distinct
    /// first-request-ordered IDs and explicitly mark unknown IDs unavailable without revealing whether another
    /// scope contains them; an unavailable result carries a hint naming the canonical form when the request
    /// matched an in-scope authored dot-handle id. Ambiguous authoring identities or colliding canonical path
    /// segments within the scope fail the whole query (AI-004 requirement 43).
    /// </summary>
    Task<IReadOnlyList<LoreEntryLookup>> QueryEntriesAsync(
        ContentContext content,
        LoreEntryIDQuery query,
        CancellationToken cancellationToken = default);
}
