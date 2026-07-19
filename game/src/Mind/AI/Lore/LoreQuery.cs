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

    /// <summary>Gets the canonical, normalised subject ID, when the collection is subject-scoped.</summary>
    public string? SubjectID
    {
        get;
    }

    /// <summary>Creates a baseline world-lore request.</summary>
    public static LoreSubjectRequest World() => new(LoreSubjectKind.World, subjectID: null);

    /// <summary>Creates a location-lore request.</summary>
    public static LoreSubjectRequest Location(string subjectID)
        => new(LoreSubjectKind.Location, LoreQuery.NormaliseID(subjectID, nameof(subjectID)));

    /// <summary>Creates a character-lore request.</summary>
    public static LoreSubjectRequest Character(string subjectID)
        => new(LoreSubjectKind.Character, LoreQuery.NormaliseID(subjectID, nameof(subjectID)));
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
        ObserverID = NormaliseID(observerID, nameof(observerID));
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

    /// <summary>Gets the canonical, normalised observer ID.</summary>
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

    internal static string NormaliseID(string id, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id, parameterName);
        string normalised = id.Trim().ToLowerInvariant();
        return normalised is "." or ".." || normalised.Contains('/') || normalised.Contains('\\')
            ? throw new ArgumentException("Lore IDs cannot be traversal segments or contain path separators.", parameterName)
            : normalised;
    }
}
