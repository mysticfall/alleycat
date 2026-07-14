namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Lore entry returned by runtime lore queries.
/// </summary>
public sealed record LoreEntry(string ID, string Title, string Body, string SourcePath);
