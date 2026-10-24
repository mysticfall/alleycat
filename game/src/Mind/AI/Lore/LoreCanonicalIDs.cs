namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// The canonical lore-ID grammar shared by query construction and backend resolution (AI-004 requirement 43):
/// canonical IDs have the form <c>[type]:[wiki-relative path]</c> with the type→directory map
/// <c>char</c>→<c>characters</c>, <c>loc</c>→<c>locations</c>, and <c>world</c>→<c>world</c>. Two canonical IDs
/// match by exact ordinal equality of the type and each path segment, with hyphens and underscores equivalent
/// within segments and no case folding or trimming.
/// </summary>
internal static class LoreCanonicalIDs
{
    /// <summary>Composes the canonical ID for one kind and wiki-relative path.</summary>
    internal static string Compose(LoreSubjectKind kind, string wikiRelativePath)
        => $"{GetTypePrefix(kind)}:{wikiRelativePath}";

    /// <summary>Gets the canonical type prefix for one lore subject kind.</summary>
    internal static string GetTypePrefix(LoreSubjectKind kind) => kind switch
    {
        LoreSubjectKind.World => "world",
        LoreSubjectKind.Location => "loc",
        LoreSubjectKind.Character => "char",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported lore subject kind."),
    };

    /// <summary>
    /// Resolves the lore subject kind a canonical ID's type prefix addresses, or <c>null</c> for unsupported
    /// types such as <c>scenario:</c>, <c>item:</c>, and malformed strings — all of which are unavailable, never
    /// errors (AI-004 requirement 48).
    /// </summary>
    internal static LoreSubjectKind? TryResolveKind(string canonicalID)
    {
        int separator = canonicalID.IndexOf(':', StringComparison.Ordinal);
        return separator <= 0
            ? null
            : canonicalID[..separator] switch
            {
                "world" => LoreSubjectKind.World,
                "loc" => LoreSubjectKind.Location,
                "char" => LoreSubjectKind.Character,
                _ => null,
            };
    }

    /// <summary>
    /// Determines whether two canonical-ID strings address the same entry: ordinal type equality plus
    /// hyphen/underscore equivalence within each path segment, without case folding or trimming.
    /// </summary>
    internal static bool Equivalent(string left, string right)
        => string.Equals(left, right, StringComparison.Ordinal) || PathFormsEquivalent(left, right);

    private static bool PathFormsEquivalent(string left, string right)
    {
        int leftSeparator = left.IndexOf(':', StringComparison.Ordinal);
        int rightSeparator = right.IndexOf(':', StringComparison.Ordinal);
        return leftSeparator >= 0
            && rightSeparator >= 0
            && string.Equals(left[..leftSeparator], right[..rightSeparator], StringComparison.Ordinal)
            && PathsEquivalent(left[(leftSeparator + 1)..], right[(rightSeparator + 1)..]);
    }

    /// <summary>
    /// Determines whether two type-less paths address the same entry: hyphen/underscore equivalence within each
    /// path segment, without case folding or trimming.
    /// </summary>
    internal static bool PathsEquivalent(string left, string right)
    {
        string[] leftSegments = left.Split('/');
        string[] rightSegments = right.Split('/');
        if (leftSegments.Length != rightSegments.Length)
        {
            return false;
        }

        for (int index = 0; index < leftSegments.Length; index++)
        {
            if (!SegmentsEquivalent(leftSegments[index], rightSegments[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SegmentsEquivalent(string left, string right)
        => string.Equals(NormaliseSegment(left), NormaliseSegment(right), StringComparison.Ordinal);

    /// <summary>
    /// Normalises one path segment for load-time collision detection by collapsing the hyphen/underscore
    /// equivalence: two files in one directory whose normalised stems match fail load validation (AI-004
    /// requirement 43).
    /// </summary>
    internal static string NormaliseSegment(string segment) => segment.Replace('-', '_');
}
