using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.Attention;

/// <summary>
/// Provisional user-authorised playtest acceptance, not solver slack, anatomical proof or a
/// numerical-error estimate. Authored caps, gameplay, fixation, roll and observation windows
/// remain unchanged. All readings are unsigned and invalid inputs fail closed.
/// </summary>
internal static class DownwardFoldPlaytestAcceptance
{
    public const double HeadPositionMillimetres = 10d;
    public const double HeadRotationDegrees = 0.5d;
    public const double SpineChestSwingAllowanceDegrees = 3d;
    public const double UpperChestSwingLimitDegrees = 16d;
    public const double NeckSwingAllowanceDegrees = 0.1d;
    public const double SpineChestOffXDegrees = 1d;

    // Root-to-end order: Spine, Chest, UpperChest, Neck. Head has no authored cap.
    public static double SwingLimitDegrees(double authoredCapDegrees, int joint) => joint == 2
        ? UpperChestSwingLimitDegrees
        : authoredCapDegrees + (joint < 2 ? SpineChestSwingAllowanceDegrees : NeckSwingAllowanceDegrees);

    public static bool IsSwingWithinAllowance(double swingDegrees, double authoredCapDegrees, int joint)
        => joint is >= 0 and < 4 && IsUnsignedFinite(authoredCapDegrees)
            && IsWithin(swingDegrees, SwingLimitDegrees(authoredCapDegrees, joint));

    public static bool IsOffAxisWithinAllowance(double offAxisDegrees)
        => IsWithin(offAxisDegrees, SpineChestOffXDegrees);

    public static bool IsHeadPositionWithinBudget(double millimetres)
        => IsWithin(millimetres, HeadPositionMillimetres);

    public static bool IsHeadRotationWithinBudget(double degrees)
        => IsWithin(degrees, HeadRotationDegrees);

    private static bool IsWithin(double reading, double bound)
        => IsUnsignedFinite(reading) && double.IsFinite(bound) && reading <= bound;

    private static bool IsUnsignedFinite(double value) => double.IsFinite(value) && value >= 0d;
}

/// <summary>Synthetic contract boundaries only; these do not certify runtime behaviour.</summary>
[Headless]
public sealed class DownwardFoldAngularAcceptanceTests
{
    /// <summary>Pins the explicitly authorised provisional values.</summary>
    [Fact]
    public void ApprovedConstants_ArePinned()
    {
        Assert.Equal(10d, DownwardFoldPlaytestAcceptance.HeadPositionMillimetres);
        Assert.Equal(0.5d, DownwardFoldPlaytestAcceptance.HeadRotationDegrees);
        Assert.Equal(3d, DownwardFoldPlaytestAcceptance.SpineChestSwingAllowanceDegrees);
        Assert.Equal(16d, DownwardFoldPlaytestAcceptance.UpperChestSwingLimitDegrees);
        Assert.Equal(0.1d, DownwardFoldPlaytestAcceptance.NeckSwingAllowanceDegrees);
        Assert.Equal(1d, DownwardFoldPlaytestAcceptance.SpineChestOffXDegrees);
    }

    /// <summary>Checks the absolute UpperChest bound separately from cap-relative allowances.</summary>
    [Fact]
    public void SwingBoundaries_AreInclusiveAndJointSpecific()
    {
        double[] caps = [8d, 8d, 14d, 16d];
        double[] limits = [11d, 11d, 16d, 16.1d];
        for (int joint = 0; joint < 4; joint++)
        {
            Assert.True(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(limits[joint], caps[joint], joint));
            Assert.True(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(limits[joint] - 0.0001d, caps[joint], joint));
            Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(limits[joint] + 0.0001d, caps[joint], joint));
        }

        Assert.True(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(16d, 8d, 2));
        Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(16.0001d, 20d, 2));
        Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(16d, 14d, 3));
    }

    /// <summary>Checks equality, inside and just-outside for Head and hinge metrics.</summary>
    [Fact]
    public void HeadAndOffAxisBoundaries_AreInclusive()
    {
        AssertBoundary(DownwardFoldPlaytestAcceptance.IsHeadPositionWithinBudget, 10d);
        AssertBoundary(DownwardFoldPlaytestAcceptance.IsHeadRotationWithinBudget, 0.5d);
        AssertBoundary(DownwardFoldPlaytestAcceptance.IsOffAxisWithinAllowance, 1d);
    }

    /// <summary>Rejects invalid unsigned readings, caps and joint indices.</summary>
    [Fact]
    public void NonFiniteAndNegativeInputs_FailClosed()
    {
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -0.0001d })
        {
            Assert.False(DownwardFoldPlaytestAcceptance.IsHeadPositionWithinBudget(invalid));
            Assert.False(DownwardFoldPlaytestAcceptance.IsHeadRotationWithinBudget(invalid));
            Assert.False(DownwardFoldPlaytestAcceptance.IsOffAxisWithinAllowance(invalid));
            for (int joint = 0; joint < 4; joint++)
            {
                Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(invalid, 8d, joint));
                Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(0d, invalid, joint));
            }
        }

        Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(0d, 8d, -1));
        Assert.False(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(0d, 8d, 4));
    }

    /// <summary>Retains neutral, negative-zero and exact-cap acceptance.</summary>
    [Fact]
    public void NeutralAndCapExactReadings_Pass()
    {
        Assert.True(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(8d, 8d, 0));
        Assert.True(DownwardFoldPlaytestAcceptance.IsSwingWithinAllowance(-0d, 8d, 0));
        Assert.True(DownwardFoldPlaytestAcceptance.IsOffAxisWithinAllowance(-0d));
        Assert.True(DownwardFoldPlaytestAcceptance.IsHeadPositionWithinBudget(-0d));
        Assert.True(DownwardFoldPlaytestAcceptance.IsHeadRotationWithinBudget(-0d));
    }

    private static void AssertBoundary(Func<double, bool> classify, double boundary)
    {
        Assert.True(classify(boundary));
        Assert.True(classify(boundary - 0.0001d));
        Assert.False(classify(boundary + 0.0001d));
    }
}
