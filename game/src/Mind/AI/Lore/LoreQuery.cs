using AlleyCat.Core;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Identifies a perspective lore collection.
/// </summary>
public enum LoreSubjectKind
{
    /// <summary>Baseline world knowledge.</summary>
    World,

    /// <summary>Knowledge about a location.</summary>
    Location,

    /// <summary>Knowledge about a character.</summary>
    Character,
}

/// <summary>
/// A typed subject requested from an observer's perspective lore.
/// </summary>
public sealed record LoreSubjectRequest
{
    private LoreSubjectRequest(LoreSubjectKind kind, string? subjectID)
    {
        Kind = kind;
        SubjectID = subjectID;
    }

    /// <summary>Gets the requested lore collection.</summary>
    public LoreSubjectKind Kind
    {
        get;
    }

    /// <summary>Gets the canonical subject FullId, when the collection is subject-scoped.</summary>
    public string? SubjectID
    {
        get;
    }

    /// <summary>Creates a baseline world-lore request.</summary>
    public static LoreSubjectRequest World() => new(LoreSubjectKind.World, subjectID: null);

    /// <summary>Creates a location-lore request.</summary>
    public static LoreSubjectRequest Location(string subjectID)
        => CreateSubjectRequest(LoreSubjectKind.Location, "loc", subjectID);

    /// <summary>Creates a character-lore request.</summary>
    public static LoreSubjectRequest Character(string subjectID)
        => CreateSubjectRequest(LoreSubjectKind.Character, "char", subjectID);

    private static LoreSubjectRequest CreateSubjectRequest(
        LoreSubjectKind kind,
        string requiredType,
        string fullSubjectID)
    {
        IdentityValidator.ValidateFullId(fullSubjectID, nameof(fullSubjectID));
        int separator = fullSubjectID.IndexOf(':', StringComparison.Ordinal);
        string type = fullSubjectID[..separator];
        _ = !string.Equals(type, requiredType, StringComparison.Ordinal)
            ? throw new ArgumentException(
                $"{kind} lore subject IDs must have the '{requiredType}' type; received '{fullSubjectID}'.",
                nameof(fullSubjectID))
            : false;

        return new LoreSubjectRequest(kind, fullSubjectID);
    }
}

/// <summary>
/// Describes a batched perspective lore query. Results retain this request order, with each request sorted internally.
/// </summary>
public sealed record LoreQuery
{
    /// <summary>
    /// Creates a query for one observer and an ordered batch of world, location, or character subjects.
    /// Duplicate requests are removed while preserving their first occurrence.
    /// </summary>
    public LoreQuery(string observerID, IEnumerable<LoreSubjectRequest> subjects)
    {
        IdentityValidator.ValidateFullId(observerID, nameof(observerID));
        ObserverID = observerID;
        ArgumentNullException.ThrowIfNull(subjects);

        List<LoreSubjectRequest> uniqueSubjects = [];
        HashSet<LoreSubjectRequest> seen = [];
        foreach (LoreSubjectRequest subject in subjects)
        {
            ArgumentNullException.ThrowIfNull(subject);
            if (seen.Add(subject))
            {
                uniqueSubjects.Add(subject);
            }
        }

        Subjects = uniqueSubjects.AsReadOnly();
    }

    /// <summary>Gets the canonical observer FullId.</summary>
    public string ObserverID
    {
        get;
    }

    /// <summary>
    /// Gets the ordered subject batch. Results are grouped in this order.
    /// </summary>
    public IReadOnlyList<LoreSubjectRequest> Subjects
    {
        get;
    }

    /// <summary>
    /// Creates the baseline query for an observer. Only world entries marked essential are selected.
    /// </summary>
    public static LoreQuery Essential(string observerID) => new(observerID, [LoreSubjectRequest.World()]);

}

/// <summary>
/// Describes an observer-scoped catalogue query: every runtime-eligible entry in the bound content root and
/// observer perspective across the world, character, and location collections (AI-004 requirement 44). Entries
/// return in World, Characters, Locations group order with deterministic ordering inside each group. Selection
/// driven by prompt-side automatic injection stays a prompt concern; the catalogue query itself excludes
/// nothing.
/// </summary>
public sealed record LoreCatalogueQuery
{
    /// <summary>Creates a catalogue query for one observer perspective.</summary>
    public LoreCatalogueQuery(string observerID)
    {
        IdentityValidator.ValidateFullId(observerID, nameof(observerID));
        ObserverID = observerID;
    }

    /// <summary>Gets the canonical observer FullId.</summary>
    public string ObserverID
    {
        get;
    }
}

/// <summary>
/// Describes an exact entry-ID batch lookup for one observer perspective. Entry IDs are opaque exact strings
/// distinct from subject FullIds: they match by ordinal equality without case folding, trimming, subject-ID
/// interpretation, or alias lookup (AI-004 requirement 43). The complete batch is validated before any read: it
/// must be non-empty with no null or blank IDs, and repeated IDs are deduplicated by ordinal equality while
/// preserving first-request order (AI-004 requirement 47).
/// </summary>
public sealed record LoreEntryIDQuery
{
    /// <summary>Creates an entry-ID batch lookup for one observer perspective.</summary>
    public LoreEntryIDQuery(string observerID, IEnumerable<string> entryIDs)
    {
        IdentityValidator.ValidateFullId(observerID, nameof(observerID));
        ObserverID = observerID;
        ArgumentNullException.ThrowIfNull(entryIDs);

        List<string> uniqueIDs = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string? entryID in entryIDs)
        {
            if (string.IsNullOrWhiteSpace(entryID))
            {
                throw new ArgumentException(
                    "Lore entry IDs must be exact non-blank strings; the batch rejects null or blank IDs.",
                    nameof(entryIDs));
            }

            if (seen.Add(entryID))
            {
                uniqueIDs.Add(entryID);
            }
        }

        if (uniqueIDs.Count == 0)
        {
            throw new ArgumentException("At least one lore entry ID is required.", nameof(entryIDs));
        }

        EntryIDs = uniqueIDs.AsReadOnly();
    }

    /// <summary>Gets the canonical observer FullId.</summary>
    public string ObserverID
    {
        get;
    }

    /// <summary>
    /// Gets the distinct requested entry IDs in first-request order. Lookup results align with this order.
    /// </summary>
    public IReadOnlyList<string> EntryIDs
    {
        get;
    }
}
