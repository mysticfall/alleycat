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
/// Selects the lore base a canonical retrieval reads (AI-004 requirement 52): the canonical wiki base — read
/// without an observer for non-LLM consumers — or one observer's perspective base, which is the only base any
/// LLM-facing path resolves against.
/// </summary>
public enum LoreBase
{
    /// <summary>The canonical wiki base under <c>lore/wiki/</c>, read without an observer.</summary>
    Wiki,

    /// <summary>One observer's perspective base under <c>lore/perspectives/&lt;type&gt;/&lt;id&gt;/</c>.</summary>
    Perspective,
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
/// Describes an exact canonical-ID batch lookup. Canonical IDs are strings of the form
/// <c>[type]:[wiki-relative path]</c> — for example <c>char:vadim</c>, <c>loc:interrogation_room</c>, or
/// <c>world:notes/nested</c> — matched by exact ordinal equality of the type and each path segment, with hyphens
/// and underscores equivalent within segments and no case folding, trimming, subject-ID interpretation, or alias
/// lookup (AI-004 requirement 43). The complete batch is validated before any read: it must be non-empty with no
/// null or blank IDs, and repeated IDs are deduplicated by ordinal equality while preserving first-request order
/// (AI-004 requirement 47).
/// </summary>
/// <remarks>
/// The query selects its resolution base explicitly (observer-scoped perspective or canonical wiki) or defers to
/// the service's configured scope (AI-004 requirement 52). The LLM-facing <c>read_lore</c> path always supplies
/// the active observer, so it never reads the canonical wiki.
/// </remarks>
public sealed record LoreEntryIDQuery
{
    /// <summary>
    /// Creates an observer-scoped canonical-ID batch lookup against one observer's perspective base.
    /// </summary>
    public LoreEntryIDQuery(string observerID, IEnumerable<string> entryIDs)
    {
        IdentityValidator.ValidateFullId(observerID, nameof(observerID));
        Base = LoreBase.Perspective;
        ObserverID = observerID;
        EntryIDs = ValidateEntryIDs(entryIDs);
    }

    /// <summary>Creates a canonical-ID batch lookup against one explicit lore base.</summary>
    public LoreEntryIDQuery(LoreBase loreBase, IEnumerable<string> entryIDs)
    {
        Base = loreBase;
        EntryIDs = ValidateEntryIDs(entryIDs);
    }

    /// <summary>
    /// Creates a canonical-ID batch lookup that defers base selection to the service's configured scope
    /// (AI-004 requirement 52).
    /// </summary>
    public LoreEntryIDQuery(IEnumerable<string> entryIDs)
    {
        Base = null;
        EntryIDs = ValidateEntryIDs(entryIDs);
    }

    /// <summary>
    /// Creates a canonical-ID lookup from a short id with an explicit kind — for example kind
    /// <see cref="LoreSubjectKind.Character" /> with short id <c>vadim</c> addresses <c>char:vadim</c> — against
    /// one observer's perspective base.
    /// </summary>
    public static LoreEntryIDQuery FromShortID(string observerID, LoreSubjectKind kind, string shortID)
        => new(observerID, [ComposeCanonicalID(kind, shortID)]);

    /// <summary>
    /// Creates a canonical-ID lookup from a short id with an explicit kind against one explicit lore base.
    /// </summary>
    public static LoreEntryIDQuery FromShortID(LoreBase loreBase, LoreSubjectKind kind, string shortID)
        => new(loreBase, [ComposeCanonicalID(kind, shortID)]);

    /// <summary>
    /// Gets the explicitly selected lore base, or <c>null</c> when the query defers to the service's configured
    /// scope.
    /// </summary>
    public LoreBase? Base
    {
        get;
    }

    /// <summary>Gets the canonical observer FullId for perspective-base queries; <c>null</c> otherwise.</summary>
    public string? ObserverID
    {
        get;
    }

    /// <summary>
    /// Gets the distinct requested canonical IDs in first-request order. Lookup results align with this order.
    /// </summary>
    public IReadOnlyList<string> EntryIDs
    {
        get;
    }

    private static IReadOnlyList<string> ValidateEntryIDs(IEnumerable<string> entryIDs)
    {
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

        return uniqueIDs.Count > 0
            ? uniqueIDs.AsReadOnly()
            : throw new ArgumentException("At least one lore entry ID is required.", nameof(entryIDs));
    }

    private static string ComposeCanonicalID(LoreSubjectKind kind, string shortID)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shortID);
        if (shortID.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"A lore short id is the canonical ID without its type prefix; received '{shortID}'.",
                nameof(shortID));
        }

        foreach (string segment in shortID.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException(
                    $"A lore short id cannot contain empty path segments; received '{shortID}'.",
                    nameof(shortID));
            }
        }

        return LoreCanonicalIDs.Compose(kind, shortID);
    }
}
