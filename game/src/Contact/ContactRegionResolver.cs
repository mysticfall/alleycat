using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// Resolves authored sub-part regions for a contacted body part (INTR-005 TR5, TR16): given the bone-local
/// contact point the monitor already transformed, reports every region anchored to that bone whose volume
/// contains the point, ordered so the first match is the primary. Contacts matching no region resolve to an
/// empty list and stay at body-part granularity.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Ordering (INTR-005 TR18d — final choice).</strong> The spec's default rule applies: the highest
/// authored priority wins, ties break to the smallest volume, and all matching regions are reported with the
/// primary marked. When priority <em>and</em> volume both tie, the region name's ordinal order decides, so
/// resolution stays deterministic for any authoring permutation.
/// </para>
/// <para>
/// <strong>Purity.</strong> Pure C# so ordering, containment, and primary-marking stay unit-testable below the
/// Godot boundary; the monitor owns the bone-local transform that produces the input point.
/// </para>
/// </remarks>
public sealed class ContactRegionResolver
{
    private readonly Dictionary<string, List<ContactRegionDefinition>> _regionsByBoneName;

    /// <summary>
    /// Creates a resolver indexed by anchor bone name from the authored region definitions.
    /// </summary>
    /// <param name="regions">All authored region definitions; bone-name lookups are ordinal.</param>
    public ContactRegionResolver(IReadOnlyList<ContactRegionDefinition> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);

        _regionsByBoneName = [];
        foreach (ContactRegionDefinition region in regions)
        {
            ArgumentNullException.ThrowIfNull(region, nameof(regions));
            if (!_regionsByBoneName.TryGetValue(region.BoneName, out List<ContactRegionDefinition>? anchored))
            {
                anchored = [];
                _regionsByBoneName[region.BoneName] = anchored;
            }

            anchored.Add(region);
        }

        RegionCount = regions.Count;
    }

    /// <summary>Total number of authored region definitions the resolver holds.</summary>
    public int RegionCount
    {
        get;
    }

    /// <summary>
    /// Resolves the matching regions for one bone-local contact point: every region anchored to
    /// <paramref name="boneName" /> whose volume contains the point, highest-priority first with the primary
    /// marked; empty when no region matched, leaving the contact at body-part granularity.
    /// </summary>
    /// <param name="boneName">Anchor bone name of the contacted body part.</param>
    /// <param name="boneLocalPoint">The contact point in the anchor bone's local space.</param>
    public IReadOnlyList<ContactRegionMatch> Resolve(string boneName, Vector3 boneLocalPoint)
    {
        if (string.IsNullOrWhiteSpace(boneName)
            || !_regionsByBoneName.TryGetValue(boneName, out List<ContactRegionDefinition>? candidates))
        {
            return [];
        }

        List<ContactRegionDefinition> containing = [];
        foreach (ContactRegionDefinition candidate in candidates)
        {
            if (candidate.Contains(boneLocalPoint))
            {
                containing.Add(candidate);
            }
        }

        if (containing.Count == 0)
        {
            return [];
        }

        containing.Sort(CompareMatches);

        var matches = new ContactRegionMatch[containing.Count];
        for (int index = 0; index < containing.Count; index++)
        {
            matches[index] = new ContactRegionMatch(containing[index].Name, index == 0, containing[index].Tags);
        }

        return matches;
    }

    private static int CompareMatches(ContactRegionDefinition left, ContactRegionDefinition right)
    {
        int byPriority = right.Priority.CompareTo(left.Priority);
        if (byPriority != 0)
        {
            return byPriority;
        }

        int byVolume = left.Volume.CompareTo(right.Volume);
        return byVolume != 0 ? byVolume : string.CompareOrdinal(left.Name, right.Name);
    }
}
