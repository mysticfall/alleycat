namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Lore entry returned by runtime lore queries. Lower priorities sort first; at equal priority, entries sort by
/// ID, then title, then the backend's source path. The Markdown backend guarantees a non-null ID at runtime
/// because pages without one are skipped at read time (AI-004 requirement 36); the property stays nullable to
/// keep the storage-agnostic query contract. The optional description is authored single-line metadata that is
/// <c>null</c> when absent or blank and never falls back to body excerpts or summaries (AI-004 requirement 42).
/// </summary>
public sealed record LoreEntry(
    string? ID,
    string Title,
    string Body,
    int Priority = 0,
    LoreSubjectKind Kind = LoreSubjectKind.World,
    string? SubjectID = null,
    string? Description = null);

/// <summary>
/// One exact-ID lore lookup outcome: the requested entry ID associated with either its found entry or an
/// explicit unavailable status (AI-004 requirement 48). Unknown IDs, including IDs available only outside the
/// bound observer/content scope, produce a <c>null</c> entry without revealing whether another scope contains
/// them.
/// </summary>
public sealed record LoreEntryLookup(string RequestedID, LoreEntry? Entry)
{
    /// <summary>
    /// Gets whether <see cref="Entry" /> was found in the bound scope; <c>false</c> marks the requested ID
    /// explicitly unavailable.
    /// </summary>
    public bool Found => Entry is not null;
}
