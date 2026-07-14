using AlleyCat.Core.Content;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Asynchronous runtime access to content-scoped lore.
/// </summary>
public interface ILoreQueryService
{
    /// <summary>
    /// Queries lore for the supplied content context and intent.
    /// </summary>
    Task<IReadOnlyList<LoreEntry>> QueryAsync(
        ContentContext content,
        LoreQuery query,
        CancellationToken cancellationToken = default);
}
