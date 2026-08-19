namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Lore entry returned by runtime lore queries. Lower priorities sort first; at equal priority, entries sort by
/// ID, then title, then the backend's source path. The Markdown backend guarantees a non-null ID at runtime
/// because pages without one are skipped at read time (AI-004 requirement 36); the property stays nullable to
/// keep the storage-agnostic query contract.
/// </summary>
public sealed record LoreEntry(
    string? ID,
    string Title,
    string Body,
    int Priority = 0,
    LoreSubjectKind Kind = LoreSubjectKind.World,
    string? SubjectID = null);
