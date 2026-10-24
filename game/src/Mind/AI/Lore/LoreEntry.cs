namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Lore entry returned by runtime lore queries. Lower priorities sort first; at equal priority, entries sort by
/// authoring identity — the authored dot-handle <c>id</c> or its path-derived fallback — then title, then the
/// backend's source path. <see cref="ID" /> is the entry's path-derived canonical full ID (for example
/// <c>char:vadim</c> or <c>world:notes/nested</c>, AI-004 requirement 43): the Markdown backend populates it for
/// every loaded entry, it is the only identity model-visible surfaces render, and authored dot-handle ids never
/// reach it. The property stays nullable to keep the storage-agnostic query contract. The optional description is
/// authored single-line metadata that is <c>null</c> when absent or blank and never falls back to body excerpts
/// or summaries (AI-004 requirement 42).
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
/// One exact canonical-ID lore lookup outcome: the requested canonical ID associated with either its found entry
/// or an explicit unavailable status (AI-004 requirement 48). Unknown IDs, including IDs available only outside
/// the bound observer/content scope, produce a <c>null</c> entry without revealing whether another scope contains
/// them. When the requested string equals an in-scope entry's authored dot-handle id, the unavailable result
/// carries <see cref="HintCanonicalID" /> naming that entry's canonical form so the model can retry correctly.
/// </summary>
public sealed record LoreEntryLookup(string RequestedID, LoreEntry? Entry, string? HintCanonicalID = null)
{
    /// <summary>
    /// Gets whether <see cref="Entry" /> was found in the bound scope; <c>false</c> marks the requested ID
    /// explicitly unavailable.
    /// </summary>
    public bool Found => Entry is not null;
}
