using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for <see cref="PoseStateContextBuilder.ComputeNormalizedHeadLocalOffset"/>.
/// </summary>
public sealed class PoseStateContextBuilderTests
{
    /// <summary>
    /// Rest head-height measure is intrinsic to skeleton space and invariant to world offsets.
    /// </summary>
    [Fact]
    public void ComputeRestHeadHeightMeasure_IsInvariantAcrossWorldElevationAndOffset()
    {
        Vector3 restLocal = new(0.25f, 1.6f, -0.4f);

        Transform3D skeletonA = new(Basis.Identity, new Vector3(0.0f, 0.0f, 0.0f));
        Transform3D skeletonB = new(Basis.Identity, new Vector3(15.0f, 8.0f, -11.0f));

        Transform3D restA = new(Basis.Identity, skeletonA * restLocal);
        Transform3D restB = new(Basis.Identity, skeletonB * restLocal);

        float measureA = PoseStateContextBuilder.ComputeRestHeadHeightMeasure(skeletonA, restA);
        float measureB = PoseStateContextBuilder.ComputeRestHeadHeightMeasure(skeletonB, restB);

        Assert.InRange(measureA, 1.5999f, 1.6001f);
        Assert.InRange(measureB, 1.5999f, 1.6001f);
    }

    /// <summary>
    /// Rest-local to current-local offset is divided by rest local head height.
    /// </summary>
    [Fact]
    public void ComputeNormalizedHeadLocalOffset_IdentitySkeleton_NormalisesByRestHeight()
    {
        Transform3D skeletonGlobal = Transform3D.Identity;
        Transform3D restViewpoint = new(Basis.Identity, new Vector3(0.0f, 1.5f, 0.0f));
        Transform3D currentViewpoint = new(Basis.Identity, new Vector3(0.0f, 1.2f, 0.15f));

        Vector3 result = PoseStateContextBuilder.ComputeNormalizedHeadLocalOffset(
            skeletonGlobal,
            restViewpoint,
            currentViewpoint);

        AssertApproximately(result, new Vector3(0.0f, -0.2f, 0.1f));
    }

    /// <summary>
    /// Global transforms are converted through skeleton space before normalisation.
    /// </summary>
    [Fact]
    public void ComputeNormalizedHeadLocalOffset_UsesSkeletonLocalSpace()
    {
        Transform3D skeletonGlobal = new(Basis.Identity, new Vector3(10.0f, 5.0f, -2.0f));
        Vector3 restLocal = new(0.4f, 1.2f, -0.1f);
        Vector3 currentLocal = new(0.6f, 0.9f, 0.2f);

        Transform3D restViewpoint = new(Basis.Identity, skeletonGlobal * restLocal);
        Transform3D currentViewpoint = new(Basis.Identity, skeletonGlobal * currentLocal);

        Vector3 result = PoseStateContextBuilder.ComputeNormalizedHeadLocalOffset(
            skeletonGlobal,
            restViewpoint,
            currentViewpoint);

        AssertApproximately(result, new Vector3(1.0f / 6.0f, -0.25f, 0.25f));
    }

    /// <summary>
    /// Invalid normalisation baselines return zero rather than NaN or infinity.
    /// </summary>
    [Fact]
    public void ComputeNormalizedHeadLocalOffset_ZeroRestHeight_ReturnsZero()
    {
        Transform3D skeletonGlobal = Transform3D.Identity;
        Transform3D restViewpoint = new(Basis.Identity, new Vector3(0.1f, 0.0f, -0.1f));
        Transform3D currentViewpoint = new(Basis.Identity, new Vector3(0.2f, -0.3f, 0.4f));

        Vector3 result = PoseStateContextBuilder.ComputeNormalizedHeadLocalOffset(
            skeletonGlobal,
            restViewpoint,
            currentViewpoint);

        Assert.Equal(Vector3.Zero, result);
    }

    private static void AssertApproximately(Vector3 actual, Vector3 expected, float epsilon = 1e-5f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }
}
