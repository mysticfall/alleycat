using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// One authored sub-part contact region (INTR-005 TR16): a named, tagged annotation volume anchored to a bone
/// and expressed entirely in that bone's local space — centre, orientation, and half extents of an oriented
/// box. Definitions are annotation data only: a region carries no physics body, collision shape, or collision
/// layer/mask presence, and <see cref="ContactRegionResolver" /> resolves them against the bone-local contact
/// points the monitor already transformed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Volume.</strong> Containment is boundary-inclusive, and the box volume feeds the smallest-volume
/// tie-break (INTR-005 TR18d).
/// </para>
/// <para>
/// <strong>Purity.</strong> This type is pure C# over <see cref="System.Numerics" /> so the resolution
/// arithmetic stays unit-testable below the Godot boundary; Godot-side authoring lives in the source-scene
/// marker and profile that produce definitions.
/// </para>
/// </remarks>
public sealed record ContactRegionDefinition
{
    private readonly string[] _tags;

    /// <summary>
    /// Creates a validated region definition.
    /// </summary>
    /// <param name="name">Authored region name, such as "Right Cheek"; non-blank.</param>
    /// <param name="boneName">Anchor bone name resolved through the source-scene workflow; non-blank.</param>
    /// <param name="center">Region box centre in the anchor bone's local space, in metres.</param>
    /// <param name="halfExtents">Region box half extents in the oriented frame, in metres; every component
    /// positive and finite.</param>
    /// <param name="orientation">Region box orientation in the anchor bone's local space; normalised on
    /// construction. A default (all-zero) quaternion means no rotation.</param>
    /// <param name="priority">Authored priority — the highest-priority matching region is the primary
    /// (INTR-005 TR18d); defaults to zero.</param>
    /// <param name="tags">Authored tags carried verbatim on matches; each non-blank. Defaults to none.
    /// Defensively copied.</param>
    public ContactRegionDefinition(
        string name,
        string boneName,
        Vector3 center,
        Vector3 halfExtents,
        Quaternion orientation,
        int priority = 0,
        IReadOnlyList<string>? tags = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The region name must be a non-blank string.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(boneName))
        {
            throw new ArgumentException("The anchor bone name must be a non-blank string.", nameof(boneName));
        }

        if (!IsFinite(center))
        {
            throw new ArgumentOutOfRangeException(nameof(center), center,
                "The region centre must be finite in every component.");
        }

        if (!IsFinite(halfExtents) || halfExtents.X <= 0f || halfExtents.Y <= 0f || halfExtents.Z <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(halfExtents), halfExtents,
                "The region half extents must be finite and positive in every component.");
        }

        if (!float.IsFinite(orientation.X) || !float.IsFinite(orientation.Y)
            || !float.IsFinite(orientation.Z) || !float.IsFinite(orientation.W))
        {
            throw new ArgumentOutOfRangeException(nameof(orientation), orientation,
                "The region orientation must be finite in every component.");
        }

        Name = name;
        BoneName = boneName;
        Center = center;
        HalfExtents = halfExtents;
        Orientation = orientation == default ? Quaternion.Identity : Quaternion.Normalize(orientation);
        Priority = priority;
        _tags = tags is null ? [] : CopyTags(tags);
    }

    /// <summary>Authored region name, such as "Right Cheek".</summary>
    public string Name
    {
        get;
    }

    /// <summary>Anchor bone name the region volume is expressed in.</summary>
    public string BoneName
    {
        get;
    }

    /// <summary>Region box centre in the anchor bone's local space, in metres.</summary>
    public Vector3 Center
    {
        get;
    }

    /// <summary>Region box half extents in the oriented frame, in metres; every component positive.</summary>
    public Vector3 HalfExtents
    {
        get;
    }

    /// <summary>Region box orientation in the anchor bone's local space; normalised.</summary>
    public Quaternion Orientation
    {
        get;
    }

    /// <summary>
    /// Authored priority: the highest-priority matching region is the primary, ties break to the smallest
    /// volume (INTR-005 TR18d).
    /// </summary>
    public int Priority
    {
        get;
    }

    /// <summary>Authored tags carried verbatim on region matches; empty when none were authored.</summary>
    public IReadOnlyList<string> Tags => _tags;

    /// <summary>The oriented box's volume in cubic metres — the TR18d smallest-volume tie-break measure.</summary>
    public float Volume => 8f * HalfExtents.X * HalfExtents.Y * HalfExtents.Z;

    /// <summary>
    /// Returns whether a bone-local point lies inside the region volume; boundary-inclusive.
    /// </summary>
    /// <param name="boneLocalPoint">A point in the anchor bone's local space.</param>
    public bool Contains(Vector3 boneLocalPoint)
    {
        var localPoint = Vector3.Transform(boneLocalPoint - Center, Quaternion.Conjugate(Orientation));
        return MathF.Abs(localPoint.X) <= HalfExtents.X
            && MathF.Abs(localPoint.Y) <= HalfExtents.Y
            && MathF.Abs(localPoint.Z) <= HalfExtents.Z;
    }

    /// <summary>Value equality across all authoring fields, tags compared element-wise.</summary>
    public bool Equals(ContactRegionDefinition? other) =>
        other is not null
        && other.Name == Name
        && other.BoneName == BoneName
        && other.Center == Center
        && other.HalfExtents == HalfExtents
        && other.Orientation == Orientation
        && other.Priority == Priority
        && other._tags.AsSpan().SequenceEqual(_tags);

    /// <summary>Hash code consistent with <see cref="Equals(ContactRegionDefinition)" />.</summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Name);
        hash.Add(BoneName);
        hash.Add(Center);
        hash.Add(HalfExtents);
        hash.Add(Orientation);
        hash.Add(Priority);
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

    private static bool IsFinite(Vector3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}
