using System.Numerics;
using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of the authored region definition and its match payload (INTR-005 TR16, TR18d): oriented-box
/// containment in bone-local space, volume, tags, immutability, and construction validation.
/// </summary>
public sealed class ContactRegionDefinitionTests
{
    private const float Pi = MathF.PI;

    /// <summary>Interior points, every inclusive boundary face, and corners are contained.</summary>
    [Fact]
    public void Contains_AcceptsInteriorBoundaryAndCornerPoints()
    {
        // Binary-exact centre and half extents so face-boundary comparisons hit exact equality.
        ContactRegionDefinition region = new(
            "Right Cheek", "Head", new Vector3(0.25f, 0.5f, -0.5f), new Vector3(0.125f, 0.25f, 0.0625f),
            Quaternion.Identity);

        Assert.True(region.Contains(region.Center), "The centre is contained.");
        Assert.True(region.Contains(new Vector3(0.375f, 0.5f, -0.5f)), "The +X face boundary is inclusive.");
        Assert.True(region.Contains(new Vector3(0.25f, 0.75f, -0.5f)), "The +Y face boundary is inclusive.");
        Assert.True(region.Contains(new Vector3(0.25f, 0.5f, -0.4375f)), "The +Z face boundary is inclusive.");
        Assert.True(region.Contains(new Vector3(0.375f, 0.75f, -0.4375f)), "A far corner is inclusive.");
    }

    /// <summary>Points beyond any face — even marginally — are not contained.</summary>
    [Fact]
    public void Contains_RejectsPointsOutsideEveryFace()
    {
        ContactRegionDefinition region = new(
            "Crown", "Head", new Vector3(0.25f, 0.5f, -0.5f), new Vector3(0.125f, 0.25f, 0.0625f),
            Quaternion.Identity);

        Assert.False(region.Contains(new Vector3(0.3751f, 0.5f, -0.5f)), "Just past +X is outside.");
        Assert.False(region.Contains(new Vector3(0.1249f, 0.5f, -0.5f)), "Just past −X is outside.");
        Assert.False(region.Contains(new Vector3(0.25f, 0.7501f, -0.5f)), "Just past +Y is outside.");
        Assert.False(region.Contains(new Vector3(0.25f, 0.2499f, -0.5f)), "Just past −Y is outside.");
        Assert.False(region.Contains(new Vector3(0.25f, 0.5f, -0.4374f)), "Just past +Z is outside.");
        Assert.False(region.Contains(new Vector3(0.25f, 0.5f, -0.5626f)), "Just past −Z is outside.");
    }

    /// <summary>An authored orientation rotates the box within the bone-local frame before containment.</summary>
    [Fact]
    public void Contains_RespectsAuthoredOrientation()
    {
        // A 90° yaw about +Y maps the box's local long +X axis onto the bone's −Z axis and its thin local +Z
        // axis onto the bone's +X axis, so the thin dimension now spans bone-local X.
        var yawQuarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Pi / 2f);
        ContactRegionDefinition region = new(
            "Right Cheek", "Head", Vector3.Zero, new Vector3(0.1f, 0.1f, 0.02f), yawQuarter);

        Assert.True(region.Contains(new Vector3(0.015f, 0f, 0f)), "The thin rotated axis still contains near points.");
        Assert.False(region.Contains(new Vector3(0.05f, 0f, 0f)), "The thin rotated axis rejects far points.");
        Assert.True(region.Contains(new Vector3(0f, 0f, 0.05f)), "The long rotated axis contains the same offset.");
        Assert.False(region.Contains(new Vector3(0f, 0f, 0.15f)), "The long rotated axis rejects out-of-box points.");
    }

    /// <summary>The box volume is eight times the product of the half extents.</summary>
    [Fact]
    public void Volume_ComputesOrientedBoxVolume()
    {
        ContactRegionDefinition region = new(
            "Crown", "Head", Vector3.Zero, new Vector3(0.1f, 0.2f, 0.3f), Quaternion.Identity);

        Assert.Equal(0.048f, region.Volume, 5);
    }

    /// <summary>A non-unit orientation is normalised on construction; a default quaternion means no rotation.</summary>
    [Fact]
    public void Orientation_IsNormalisedOnConstruction()
    {
        Quaternion doubled = Quaternion.CreateFromAxisAngle(Vector3.UnitY, Pi / 2f) * 2f;
        ContactRegionDefinition normalised = new("Cheek", "Head", Vector3.Zero, Vector3.One, doubled);

        Assert.True(
            MathF.Abs(normalised.Orientation.Length() - 1f) < 1e-5f,
            $"The orientation should be normalised; length was {normalised.Orientation.Length()}.");
        ContactRegionDefinition rotated = new(
            "Cheek", "Head", Vector3.Zero, Vector3.One, Quaternion.CreateFromAxisAngle(Vector3.UnitY, Pi / 2f));
        Assert.True(rotated.Contains(new Vector3(0.5f, 0f, 0.5f)));
        Assert.True(normalised.Contains(new Vector3(0.5f, 0f, 0.5f)), "Scaling a quaternion does not change rotation.");

        ContactRegionDefinition defaultOrientation = new("Cheek", "Head", Vector3.Zero, Vector3.One, default);
        Assert.Equal(Quaternion.Identity, defaultOrientation.Orientation);
        Assert.True(defaultOrientation.Contains(new Vector3(0.9f, 0.9f, 0.9f)));
        Assert.False(defaultOrientation.Contains(new Vector3(1.1f, 0f, 0f)));
    }

    /// <summary>Tags are carried verbatim, order-preserving, and defensively copied.</summary>
    [Fact]
    public void Tags_AreCarriedVerbatimAndDefensivelyCopied()
    {
        string[] tags = ["face", "cheek"];
        ContactRegionDefinition region = new("Right Cheek", "Head", Vector3.Zero, Vector3.One, Quaternion.Identity, tags: tags);

        Assert.Equal(["face", "cheek"], region.Tags);
        tags[0] = "mutated";
        Assert.Equal("face", region.Tags[0]);

        ContactRegionDefinition untagged = new("Crown", "Head", Vector3.Zero, Vector3.One, Quaternion.Identity);
        Assert.Empty(untagged.Tags);
    }

    /// <summary>Malformed authoring — blank names, bad extents, non-finite data, blank tags — is rejected.</summary>
    [Fact]
    public void Constructor_RejectsMalformedAuthoring()
    {
        _ = Assert.Throws<ArgumentException>(
            () => new ContactRegionDefinition(" ", "Head", Vector3.Zero, Vector3.One, Quaternion.Identity));
        _ = Assert.Throws<ArgumentException>(
            () => new ContactRegionDefinition("Right Cheek", " ", Vector3.Zero, Vector3.One, Quaternion.Identity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContactRegionDefinition(
                "Right Cheek", "Head", new Vector3(float.NaN, 0f, 0f), Vector3.One, Quaternion.Identity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContactRegionDefinition(
                "Right Cheek", "Head", Vector3.Zero, new Vector3(0.1f, 0f, 0.1f), Quaternion.Identity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContactRegionDefinition(
                "Right Cheek", "Head", Vector3.Zero, new Vector3(float.PositiveInfinity, 0.1f, 0.1f),
                Quaternion.Identity));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new ContactRegionDefinition(
                "Right Cheek", "Head", Vector3.Zero, Vector3.One,
                new Quaternion(float.NaN, 0f, 0f, 1f)));
        _ = Assert.Throws<ArgumentException>(
            () => new ContactRegionDefinition(
                "Right Cheek", "Head", Vector3.Zero, Vector3.One, Quaternion.Identity, tags: ["face", " "]));
        _ = Assert.Throws<ArgumentException>(
            () => new ContactRegionDefinition(
                "Right Cheek", "Head", Vector3.Zero, Vector3.One, Quaternion.Identity, tags: ["face", null!]));
    }

    /// <summary>Definitions compare by value, tags element-wise, despite separately built tag arrays.</summary>
    [Fact]
    public void Equality_ComparesByValueIncludingTags()
    {
        static ContactRegionDefinition Build(params string[] tags)
        {
            return new("Right Cheek", "Head", new Vector3(0.05f, 0f, -0.09f), new Vector3(0.05f, 0.09f, 0.02f),
                        Quaternion.Identity, 3, tags);
        }

        Assert.Equal(Build("face", "cheek"), Build("face", "cheek"));
        Assert.NotEqual(Build("face"), Build("face", "cheek"));
        Assert.NotEqual(Build("face", "cheek"), Build("cheek", "face"));
    }

    /// <summary>Match slots carry authored tags verbatim, defensively copied, and reject blank entries.</summary>
    [Fact]
    public void ContactRegionMatch_CarriesTagsVerbatim()
    {
        string[] tags = ["face", "cheek"];
        ContactRegionMatch match = new("Right Cheek", isPrimary: true, tags);

        Assert.Equal(["face", "cheek"], match.Tags);
        tags[1] = "mutated";
        Assert.Equal("cheek", match.Tags[1]);

        ContactRegionMatch untagged = new("Crown", isPrimary: false);
        Assert.Empty(untagged.Tags);

        Assert.Equal(new ContactRegionMatch("Right Cheek", true, ["face"]), new ContactRegionMatch(
            "Right Cheek", true, ["face"]));
        Assert.NotEqual(new ContactRegionMatch("Right Cheek", true, ["face"]), new ContactRegionMatch(
            "Right Cheek", true, ["cheek"]));

        _ = Assert.Throws<ArgumentException>(() => new ContactRegionMatch("Right Cheek", true, [" "]));
    }
}
