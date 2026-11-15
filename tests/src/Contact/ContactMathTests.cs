using System.Numerics;
using AlleyCat.Contact;
using Xunit;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Unit coverage of the pure measurement helpers behind the raw stream's derived quantities (INTR-005 TR3)
/// and the coalesced contact breadth aggregation default (INTR-005 TR18b).
/// </summary>
public sealed class ContactMathTests
{
    /// <summary>Approach speed is the negated normal projection, clamped at zero for separating contact.</summary>
    [Fact]
    public void NormalApproachSpeed_ProjectsAndClampsAlongTheOutwardNormal()
    {
        Vector3 outward = Vector3.UnitZ;

        // Approaching along −Z at 2 m/s with 0.5 m/s of sideways slide.
        Assert.Equal(2.0f, ContactMath.NormalApproachSpeed(new Vector3(0.5f, 0f, -2f), outward), 5);

        // Separating contact never reports negative pressure build.
        Assert.Equal(0f, ContactMath.NormalApproachSpeed(new Vector3(0f, 0f, 1.5f), outward), 5);

        // A non-unit normal is normalised before projection.
        Assert.Equal(2.0f, ContactMath.NormalApproachSpeed(new Vector3(0f, 0f, -2f), outward * 3f), 5);
    }

    /// <summary>Tangential speed strips the normal component, leaving the along-surface slide magnitude.</summary>
    [Fact]
    public void TangentialSpeed_StripsTheNormalComponent()
    {
        Assert.Equal(0.5f, ContactMath.TangentialSpeed(new Vector3(0.5f, 0f, -2f), Vector3.UnitZ), 5);
        Assert.Equal(0f, ContactMath.TangentialSpeed(new Vector3(0f, 0f, -2f), Vector3.UnitZ), 5);
        Assert.Equal(1.0f, ContactMath.TangentialSpeed(new Vector3(0.6f, 0.8f, 0f), Vector3.UnitZ), 5);
    }

    /// <summary>Coalesced breadth is the maximum pairwise contact-point distance — the extent diameter (TR18b).</summary>
    [Fact]
    public void AggregateContactBreadth_ReturnsTheMaximumPairwiseDistance()
    {
        Assert.Equal(0f, ContactMath.AggregateContactBreadth([]));
        Assert.Equal(0f, ContactMath.AggregateContactBreadth([new Vector3(1f, 2f, 3f)]));
        Assert.Equal(5f, ContactMath.AggregateContactBreadth(
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(3f, 4f, 0f),
        ]), 5);

        // The diameter is the largest gap, not the sum of smaller gaps.
        Assert.Equal(10f, ContactMath.AggregateContactBreadth(
        [
            new Vector3(0f, 0f, 0f),
            new Vector3(4f, 0f, 0f),
            new Vector3(10f, 0f, 0f),
        ]), 5);
    }

    /// <summary>Normalisation of a zero vector yields zero rather than NaN.</summary>
    [Fact]
    public void Normalised_ReturnsZeroForAZeroVector()
    {
        Assert.Equal(Vector3.Zero, ContactMath.Normalised(Vector3.Zero));
        Assert.Equal(Vector3.UnitZ, ContactMath.Normalised(new Vector3(0f, 0f, 3f)));
    }
}
