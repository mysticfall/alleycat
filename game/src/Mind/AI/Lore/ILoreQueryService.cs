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
}
