using AlleyCat.Common;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Common;

/// <summary>
/// Unit coverage for upright yaw-preserving transform synchronisation.
/// </summary>
public sealed class UprightRemoteTransform3DTests
{
    private const float Epsilon = 1e-5f;

    /// <summary>
    /// Verifies identity source rotation keeps the target upright with zero yaw.
    /// </summary>
    [Fact]
    public void ComputeConstrainedTargetBasis_IdentitySource_RemainsUprightWithZeroYaw()
    {
        Basis result = UprightRemoteTransform3D.ComputeConstrainedTargetBasis(Basis.Identity);

        AssertVectorClose(Vector3.Up, result.Column1);
        AssertAngleClose(0f, ExtractPlanarYaw(result));
    }

    /// <summary>
    /// Verifies non-zero source yaw is preserved on the target basis.
    /// </summary>
    [Fact]
    public void ComputeConstrainedTargetBasis_NonZeroYaw_PreservesYaw()
    {
        const float sourceYaw = 0.75f;
        var source = new Basis(Vector3.Up, sourceYaw);

        Basis result = UprightRemoteTransform3D.ComputeConstrainedTargetBasis(source);

        AssertVectorClose(Vector3.Up, result.Column1);
        AssertAngleClose(sourceYaw, ExtractPlanarYaw(result));
    }

    /// <summary>
    /// Verifies source pitch and roll do not tilt the target while yaw remains preserved.
    /// </summary>
    [Fact]
    public void ComputeConstrainedTargetBasis_SourcePitchAndRoll_TargetStaysUpright()
    {
        Basis source = new Basis(Vector3.Up, 0.6f)
                       * new Basis(Vector3.Right, 0.4f)
                       * new Basis(Vector3.Forward, -0.35f);

        Basis result = UprightRemoteTransform3D.ComputeConstrainedTargetBasis(source);
        float sourceDerivedYaw = ExtractPlanarYaw(source);

        AssertVectorClose(Vector3.Up, result.Column1);
        AssertAngleClose(sourceDerivedYaw, ExtractPlanarYaw(result));
    }

    /// <summary>
    /// Verifies transform sync preserves source global position.
    /// </summary>
    [Fact]
    public void ComputeSyncedGlobalTransform_PreservesGlobalPosition()
    {
        var sourcePosition = new Vector3(1.2f, -3.0f, 4.5f);
        var source = new Transform3D(new Basis(Vector3.Up, 0.2f), sourcePosition);

        Transform3D result = UprightRemoteTransform3D.ComputeSyncedGlobalTransform(source);

        AssertVectorClose(sourcePosition, result.Origin);
        AssertVectorClose(Vector3.Up, result.Basis.Column1);
        AssertAngleClose(0.2f, ExtractPlanarYaw(result.Basis));
    }

    private static float ExtractPlanarYaw(Basis basis)
    {
        Vector3 forward = -basis.Column2;
        var planarForward = new Vector3(forward.X, 0f, forward.Z);

        if (planarForward.LengthSquared() <= Epsilon)
        {
            return 0f;
        }

        planarForward = planarForward.Normalized();

        return Mathf.Atan2(-planarForward.X, -planarForward.Z);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(
            expected.DistanceTo(actual) <= Epsilon,
            $"Expected {expected}, got {actual}.");
    }

    private static void AssertAngleClose(float expected, float actual)
    {
        float delta = Mathf.Wrap(expected - actual, -Mathf.Pi, Mathf.Pi);

        Assert.True(
            Mathf.Abs(delta) <= Epsilon,
            $"Expected angle {expected:F6} rad, got {actual:F6} rad (delta {delta:F6}).");
    }
}
