using AlleyCat.IK.Pose;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK.Pose;

/// <summary>
/// Unit coverage for <see cref="HeadTrackingHipProfile"/>, exercised through its pure static
/// helper <see cref="HeadTrackingHipProfile.ComputeHipLocalPosition(Vector3, Vector3, Vector3)"/>.
/// </summary>
/// <remarks>
/// The profile itself is a Godot <see cref="Resource"/> subclass and therefore cannot be
/// instantiated outside the engine. These tests cover the deterministic math that backs the
/// 1:1 head-tracking hip heuristic used by the Standing↔Crouching pose family:
/// <list type="bullet">
///   <item><description>A rest-pose head offset snaps to <c>hipLocalRest</c>.</description></item>
///   <item><description>A head descent of <c>N</c> metres yields <c>hipLocalRest + (0, -N, 0)</c>.</description></item>
///   <item><description>Lateral head shifts project through identity basis unchanged.</description></item>
///   <item><description>A rotated skeleton basis is consistent: the test pre-rotates the
///     inputs into skeleton-local space before calling the helper, matching how the profile
///     computes its inputs at runtime.</description></item>
///   <item><description>A sub-epsilon head offset snaps cleanly to <c>hipLocalRest</c>.</description></item>
/// </list>
/// </remarks>
public sealed class HeadTrackingHipProfileTests
{
    private const float Tolerance = 1e-4f;

    /// <summary>
    /// A zero head offset must return the rest hip position exactly, so the animated hip rest
    /// pose is preserved when calibration is on target.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_ZeroOffset_ReturnsHipRest()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHeadLocal: restHead);

        Assert.Equal(hipRest, result);
    }

    /// <summary>
    /// A pure vertical head descent by N metres in identity skeleton-local space must produce
    /// <c>hipLocalRest + (0, -N, 0)</c>. This is the core fix for spine stretching: the hip
    /// now tracks vertical head motion 1:1 instead of lagging at the standing Y.
    /// </summary>
    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.3f)]
    [InlineData(0.6f)]
    public void ComputeHipLocalPosition_VerticalDescent_TranslatesHipByFullOffset(float descent)
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);
        Vector3 currentHead = restHead + new Vector3(0f, -descent, 0f);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHead);

        Vector3 expected = hipRest + new Vector3(0f, -descent, 0f);
        AssertClose(expected, result);
    }

    /// <summary>
    /// Lateral head shifts in the skeleton-local X/Z plane must project onto the hip bone
    /// unchanged (identity basis), preserving the 1:1 head-tracking heuristic for lateral
    /// lean as well as for vertical crouch.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_LateralShift_TranslatesHipByFullOffset()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);
        Vector3 offset = new(0.08f, 0f, -0.04f);
        Vector3 currentHead = restHead + offset;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHead);

        AssertClose(hipRest + offset, result);
    }

    /// <summary>
    /// A combined vertical-plus-lateral head offset must translate the hip by the full 3D
    /// offset. No component is stripped.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_CombinedOffset_TranslatesHipByFullOffset()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);
        Vector3 offset = new(0.05f, -0.30f, 0.02f);
        Vector3 currentHead = restHead + offset;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHead);

        AssertClose(hipRest + offset, result);
    }

    /// <summary>
    /// When the skeleton's global basis is rotated, the profile pre-projects the world-space
    /// viewpoints into skeleton-local space before calling the helper. This test simulates
    /// that projection and verifies the helper operates correctly in the rotated frame.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_RotatedSkeletonBasis_ConsistentInLocalSpace()
    {
        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHeadWorld = new(0f, 1.65f, 0f);
        Vector3 worldOffset = new(0.10f, -0.25f, 0.05f);
        Vector3 currentHeadWorld = restHeadWorld + worldOffset;

        // Skeleton rotated 90 degrees around world Y; simulate the inverse projection the
        // profile applies at runtime via skeleton.GlobalTransform.AffineInverse().
        var skeletonTransform = new Transform3D(
            new Basis(Vector3.Up, Mathf.Pi * 0.5f),
            Vector3.Zero);
        Transform3D inverse = skeletonTransform.AffineInverse();

        Vector3 restHeadLocal = inverse * restHeadWorld;
        Vector3 currentHeadLocal = inverse * currentHeadWorld;

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHeadLocal,
            currentHeadLocal);

        Vector3 expectedLocalOffset = inverse.Basis * worldOffset;
        AssertClose(hipRest + expectedLocalOffset, result);
    }

    /// <summary>
    /// A sub-epsilon head offset must snap to <c>hipLocalRest</c>, not drift a tiny distance
    /// away from it. This is the jitter-suppression contract and prevents sub-millimetre
    /// calibration noise from wobbling the hip.
    /// </summary>
    [Fact]
    public void ComputeHipLocalPosition_SubEpsilonOffset_SnapsToHipRest()
    {
        const float SubEpsilon = 1e-5f;

        Vector3 hipRest = new(0f, 0.95f, 0f);
        Vector3 restHead = new(0f, 1.65f, 0f);
        Vector3 currentHead = restHead + new Vector3(SubEpsilon, -SubEpsilon, SubEpsilon);

        Vector3 result = HeadTrackingHipProfile.ComputeHipLocalPosition(
            hipRest,
            restHead,
            currentHead);

        Assert.Equal(hipRest, result);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        float delta = (expected - actual).Length();
        Assert.True(
            delta <= Tolerance,
            $"Expected {expected}, got {actual} (|delta|={delta}).");
    }
}
