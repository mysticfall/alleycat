namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// One authored event-history fragment parsed from the standalone event-history file (AI-003 TR-12): a template
/// source dispatched by an observation record's exact, case-sensitive semantic key.
/// </summary>
/// <param name="TypeKey">Exact, case-sensitive observation semantic key opening the section.</param>
/// <param name="Source">Authored template source rendered with the matching observation record as root context.</param>
internal sealed record EventHistoryFragment(string TypeKey, string Source);
