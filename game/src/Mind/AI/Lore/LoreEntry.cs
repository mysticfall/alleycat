namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Lore entry returned by runtime lore queries. Lower priorities sort first; at equal priority, entries with IDs sort
/// before entries that use title and source-path fallbacks.
/// </summary>
public sealed record LoreEntry(
    string? ID,
    string Title,
    string Body,
    int Priority = 0,
    LoreSubjectKind Kind = LoreSubjectKind.World,
    string? SubjectID = null);
