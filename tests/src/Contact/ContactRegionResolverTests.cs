using System.Numerics;
using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of sub-part region resolution (INTR-005 TR5, TR16, TR18d): no-match passthrough at body-part
/// granularity, authored-priority ordering with the smallest-volume tie-break, deterministic full-tie ordering,
/// primary marking, and bone anchoring scope.
/// </summary>
public sealed class ContactRegionResolverTests
{
    private static readonly Vector3 _cheekPoint = new(0.05f, 0f, -0.09f);

    private static ContactRegionDefinition Region(
        string name,
        string boneName = "Head",
        int priority = 0,
        float halfExtent = 0.2f,
        string[]? tags = null)
        => new(
            name,
            boneName,
            center: _cheekPoint,
            halfExtents: new Vector3(halfExtent),
            Quaternion.Identity,
            priority,
            tags);

    /// <summary>A point inside no authored region resolves to no matches — body-part granularity (TR16).</summary>
    [Fact]
    public void Resolve_OutsideAllRegions_ReturnsNoMatches()
    {
        ContactRegionResolver resolver = new([Region("Right Cheek", halfExtent: 0.02f)]);

        Assert.Empty(resolver.Resolve("Head", new Vector3(5f, 5f, 5f)));
    }

    /// <summary>An unknown or blank bone name resolves to no matches rather than throwing.</summary>
    [Fact]
    public void Resolve_UnknownBone_ReturnsNoMatches()
    {
        ContactRegionResolver resolver = new([Region("Right Cheek")]);

        Assert.Empty(resolver.Resolve("Chest", _cheekPoint));
        Assert.Empty(resolver.Resolve(" ", _cheekPoint));
    }

    /// <summary>Regions anchored to other bones are never consulted, even when geometrically containing.</summary>
    [Fact]
    public void Resolve_ScopesToTheAnchorBone()
    {
        ContactRegionResolver resolver =
            new([Region("Chest Patch", boneName: "Chest"), Region("Right Cheek", boneName: "Head")]);

        IReadOnlyList<ContactRegionMatch> matches = resolver.Resolve("Head", _cheekPoint);

        ContactRegionMatch match = Assert.Single(matches);
        Assert.Equal("Right Cheek", match.Name);
    }

    /// <summary>A single containing region is reported as the primary with its authored tags carried verbatim.</summary>
    [Fact]
    public void Resolve_SingleMatch_MarksPrimaryAndCarriesTags()
    {
        ContactRegionResolver resolver = new([Region("Right Cheek", tags: ["face", "cheek"])]);

        IReadOnlyList<ContactRegionMatch> matches = resolver.Resolve("Head", _cheekPoint);

        ContactRegionMatch match = Assert.Single(matches);
        Assert.Equal("Right Cheek", match.Name);
        Assert.True(match.IsPrimary);
        Assert.Equal(["face", "cheek"], match.Tags);
    }

    /// <summary>The highest authored priority wins the primary mark regardless of volume; all matches report.</summary>
    [Fact]
    public void Resolve_PriorityWinsOverVolume()
    {
        ContactRegionResolver resolver = new(
        [
            Region("Cheek Patch", priority: 0, halfExtent: 0.05f),
            Region("Right Cheek", priority: 10, halfExtent: 0.5f),
        ]);

        IReadOnlyList<ContactRegionMatch> matches = resolver.Resolve("Head", _cheekPoint);

        Assert.Equal(2, matches.Count);
        Assert.Equal("Right Cheek", matches[0].Name);
        Assert.True(matches[0].IsPrimary);
        Assert.Equal("Cheek Patch", matches[1].Name);
        Assert.False(matches[1].IsPrimary);
    }

    /// <summary>
    /// Equal priorities break to the smallest volume: the smaller containing region is primary, and both
    /// matches are reported with exactly one primary (INTR-005 TR18d).
    /// </summary>
    [Fact]
    public void Resolve_EqualPriority_BreaksToSmallestVolume()
    {
        ContactRegionResolver resolver = new(
        [
            Region("Face", priority: 5, halfExtent: 0.5f),
            Region("Right Cheek", priority: 5, halfExtent: 0.05f),
        ]);

        IReadOnlyList<ContactRegionMatch> matches = resolver.Resolve("Head", _cheekPoint);

        Assert.Equal(2, matches.Count);
        Assert.Equal("Right Cheek", matches[0].Name);
        Assert.True(matches[0].IsPrimary);
        Assert.Equal("Face", matches[1].Name);
        Assert.False(matches[1].IsPrimary);
        Assert.Equal(1, matches.Count(match => match.IsPrimary));
    }

    /// <summary>When priority and volume both tie, the name's ordinal order decides — deterministically for any
    /// authoring permutation (final TR18d choice beyond the spec's rule).</summary>
    [Theory]
    [InlineData("Cheek A", "Cheek B")]
    [InlineData("Cheek B", "Cheek A")]
    public void Resolve_FullTie_IsDeterministicByName(string firstName, string secondName)
    {
        ContactRegionResolver resolver = new(
        [
            Region(firstName, priority: 3, halfExtent: 0.1f),
            Region(secondName, priority: 3, halfExtent: 0.1f),
        ]);

        IReadOnlyList<ContactRegionMatch> matches = resolver.Resolve("Head", _cheekPoint);

        Assert.Equal(2, matches.Count);
        string expectedPrimary = string.CompareOrdinal("Cheek A", "Cheek B") <= 0 ? "Cheek A" : "Cheek B";
        Assert.Equal(expectedPrimary, matches[0].Name);
        Assert.True(matches[0].IsPrimary);
        Assert.False(matches[1].IsPrimary);
    }

    /// <summary>Three overlapping regions order by priority, then volume, and report all matches primary-first.</summary>
    [Fact]
    public void Resolve_MultipleOverlaps_OrderDeterministically()
    {
        ContactRegionResolver resolver = new(
        [
            Region("Broad Face", priority: 1, halfExtent: 0.5f),
            Region("Precise Cheek", priority: 1, halfExtent: 0.05f),
            Region("Priority Patch", priority: 2, halfExtent: 0.3f),
        ]);

        IReadOnlyList<ContactRegionMatch> matches = resolver.Resolve("Head", _cheekPoint);

        Assert.Equal(
        [
            "Priority Patch", "Precise Cheek", "Broad Face",
        ], matches.Select(match => match.Name));
        Assert.True(matches[0].IsPrimary);
        Assert.All(matches.Skip(1), match => Assert.False(match.IsPrimary));
    }

    /// <summary>The resolver reports the total authored region count for diagnostics and test wiring.</summary>
    [Fact]
    public void RegionCount_ReportsAllAuthoredRegions()
    {
        ContactRegionResolver resolver = new(
        [
            Region("Right Cheek"),
            Region("Crown"),
            Region("Chest Patch", boneName: "Chest"),
        ]);

        Assert.Equal(3, resolver.RegionCount);
    }
}
