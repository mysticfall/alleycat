namespace AlleyCat.Contact;

/// <summary>
/// One resolved sub-part region slot reported by a classified <see cref="ContactEvent" /> (INTR-005 TR8, TR16,
/// TR18d). This record is payload data only: region resolution — authored priority with ties broken to the
/// smallest volume, all matching regions reported with the primary marked — happens in the monitor from
/// bone-local contact points; contacts matching no region stay at body-part granularity and report an empty
/// region list.
/// </summary>
public sealed record ContactRegionMatch
{
    private readonly string[] _tags;

    /// <summary>Creates a region match slot.</summary>
    /// <param name="name">Authored region name, such as "Right Cheek".</param>
    /// <param name="isPrimary">Whether this match is the primary — highest-priority — reported region.</param>
    /// <param name="tags">
    /// Authored tags carried verbatim on the match; each non-blank. Defaults to none. Defensively copied.
    /// </param>
    public ContactRegionMatch(string name, bool isPrimary, IReadOnlyList<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The region name must be a non-blank string.", nameof(name));
        }

        Name = name;
        IsPrimary = isPrimary;
        _tags = tags is null ? [] : CopyTags(tags);
    }

    /// <summary>Authored region name, such as "Right Cheek".</summary>
    public string Name
    {
        get;
    }

    /// <summary>
    /// Whether this match is the primary reported region: the highest authored priority, ties broken to the
    /// smallest volume (INTR-005 TR18d).
    /// </summary>
    public bool IsPrimary
    {
        get;
    }

    /// <summary>Authored region tags carried verbatim; empty when none were authored.</summary>
    public IReadOnlyList<string> Tags => _tags;

    /// <summary>Value equality across all payload fields, tags compared element-wise.</summary>
    public bool Equals(ContactRegionMatch? other) =>
        other is not null
        && other.Name == Name
        && other.IsPrimary == IsPrimary
        && other._tags.AsSpan().SequenceEqual(_tags);

    /// <summary>Hash code consistent with <see cref="Equals(ContactRegionMatch)" />.</summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name);
        hash.Add(IsPrimary);
        foreach (string tag in _tags)
        {
            hash.Add(tag);
        }

        return hash.ToHashCode();
    }

    private static string[] CopyTags(IReadOnlyList<string> tags)
    {
        string[] copy = [.. tags];
        foreach (string tag in copy)
        {
            if (tag is null || string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException(
                    "Region tags must be non-blank and carried verbatim; absence is an empty list.", nameof(tags));
            }
        }

        return copy;
    }
}
