using AlleyCat.Core;
using AlleyCat.Core.Content;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Resolves one character's authored lore description (<see cref="LoreEntry.Description" />) from the observer's
/// perspective, for character summaries that must come from lore rather than observed visual cues (playtest R1's
/// multi-cue work: attended-character lines and gaze evidence candidate summaries).
/// </summary>
/// <remarks>
/// <para>
/// Results are cached process-wide per (observer, subject): lore is static authored content, so each pair resolves
/// at most once per process. The first non-blank <see cref="LoreEntry.Description" /> in the query's priority order
/// wins; a subject with no entry or no description resolves to null and every consumer falls back honestly — the
/// bare backticked canonical ID, never fabricated text.
/// </para>
/// <para>
/// The query service's markdown backend completes synchronously (its <c>Task</c>-returning API wraps synchronous
/// file reads), so <see cref="ResolveSynchronously" /> guards rather than blocks: if a future backend ever returns
/// an incomplete task, the synchronous facade degrades to a null description instead of blocking its caller.
/// </para>
/// </remarks>
internal static class CharacterLoreDescriptions
{
    private static readonly Lock _cacheLock = new();
    private static readonly Dictionary<(string ObserverId, string SubjectId), string?> _cache = [];

    /// <summary>
    /// Resolves one character's lore description asynchronously, from cache when already resolved.
    /// </summary>
    /// <param name="queryService">The lore query service resolving the character's entries.</param>
    /// <param name="observerFullId">The observing character's canonical full ID.</param>
    /// <param name="content">The content context selecting the lore base.</param>
    /// <param name="subjectFullId">The described character's canonical full ID.</param>
    /// <param name="cancellationToken">Cancellation that abandons the resolution.</param>
    /// <returns>The authored description, or null when absent, blank, or the query failed.</returns>
    public static async Task<string?> ResolveAsync(
        ILoreQueryService queryService,
        string observerFullId,
        ContentContext content,
        string subjectFullId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryService);
        IdentityValidator.ValidateFullId(observerFullId, nameof(observerFullId));
        IdentityValidator.ValidateFullId(subjectFullId, nameof(subjectFullId));

        (string observerId, string subjectId) key = (observerFullId, subjectFullId);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out string? cached))
            {
                return cached;
            }
        }

        string? description = null;
        try
        {
            LoreQuery query = new(observerFullId, [LoreSubjectRequest.Character(subjectFullId)]);
            IReadOnlyList<LoreEntry> entries = await queryService
                .QueryAsync(content, query, cancellationToken)
                .ConfigureAwait(false);
            description = entries
                .Select(static entry => entry.Description)
                .FirstOrDefault(static candidate => !string.IsNullOrWhiteSpace(candidate));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Contained: a lore failure must never break the composition asking for the description.
            return null;
        }

        lock (_cacheLock)
        {
            _cache[key] = description;
        }

        return description;
    }

    /// <summary>
    /// Resolves one character's lore description for a synchronous composition point: the shared cache first, and
    /// an in-flight-synchronous query second — an incomplete task degrades to null instead of blocking.
    /// </summary>
    /// <param name="queryService">The lore query service resolving the character's entries.</param>
    /// <param name="observerFullId">The observing character's canonical full ID.</param>
    /// <param name="content">The content context selecting the lore base.</param>
    /// <param name="subjectFullId">The described character's canonical full ID.</param>
    /// <param name="logger">Optional logger receiving the degradation diagnostic.</param>
    /// <returns>The authored description, or null when absent, blank, asynchronous, or failed.</returns>
    public static string? ResolveSynchronously(
        ILoreQueryService queryService,
        string observerFullId,
        ContentContext content,
        string subjectFullId,
        ILogger? logger = null)
    {
        (string observerId, string subjectId) key = (observerFullId, subjectFullId);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out string? cached))
            {
                return cached;
            }
        }

        Task<string?> resolution = ResolveAsync(queryService, observerFullId, content, subjectFullId);
        if (!resolution.IsCompleted)
        {
            logger?.LogDebug(
                "The lore query service did not resolve character '{SubjectId}' synchronously; the description "
                    + "stays absent rather than blocking the composition.",
                subjectFullId);
            return null;
        }

        return resolution.Result;
    }

    /// <summary>Clears the process-wide cache (test isolation).</summary>
    internal static void ClearCacheForTesting()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }
}
