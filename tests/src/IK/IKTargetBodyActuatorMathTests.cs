using AlleyCat.IK;
using Godot;
using Xunit;

namespace AlleyCat.Tests.IK;

/// <summary>
/// Unit coverage for damped IK target actuator motion helpers.
/// </summary>
public sealed class IKTargetBodyActuatorMathTests
{
    private const float Epsilon = 1e-4f;

    /// <summary>
    /// Verifies settle-distance scaling reduces desired velocity near the target.
    /// </summary>
    [Fact]
    public void ComputeDesiredVelocity_InsideSettleDistance_ReducesCatchUpSpeed()
    {
        Vector3 displacement = new(0.01f, 0.0f, 0.0f);

        Vector3 desiredVelocity = IKTargetBodyActuatorMath.ComputeDesiredVelocity(
            displacement,
            maximumSpeed: 28.0f,
            positionResponsiveness: 14.0f,
            settleDistance: 0.03f);

        Assert.True(desiredVelocity.Length() < displacement.Length() * 14.0f,
            $"Expected settle scaling to reduce desired speed. Raw={displacement.Length() * 14.0f:F4}, actual={desiredVelocity.Length():F4}.");
        Assert.InRange(desiredVelocity.X, 0.0f, 0.05f);
    }

    /// <summary>
    /// Verifies velocity changes respect the configured acceleration limit.
    /// </summary>
    [Fact]
    public void ComputeFollowVelocity_RespectsMaximumAccelerationPerStep()
    {
        Vector3 nextVelocity = IKTargetBodyActuatorMath.ComputeFollowVelocity(
            currentVelocity: Vector3.Zero,
            desiredVelocity: new Vector3(10.0f, 0.0f, 0.0f),
            deltaSeconds: 0.1f,
            maximumAcceleration: 20.0f);

        Assert.InRange(nextVelocity.Length(), 1.999f, 2.001f);
    }

    /// <summary>
    /// Verifies zero displacement resolves to zero desired velocity.
    /// </summary>
    [Fact]
    public void ComputeDesiredVelocity_ZeroDisplacement_ReturnsZero()
    {
        Vector3 desiredVelocity = IKTargetBodyActuatorMath.ComputeDesiredVelocity(
            Vector3.Zero,
            maximumSpeed: 28.0f,
            positionResponsiveness: 14.0f,
            settleDistance: 0.03f);

        Assert.True(desiredVelocity.IsEqualApprox(Vector3.Zero), $"Expected zero desired velocity, got {desiredVelocity}.");
    }

    /// <summary>
    /// Verifies basis smoothing produces a partial rotation step for larger errors.
    /// </summary>
    [Fact]
    public void ComputeFollowBasis_LargeAngularError_SmoothsInsteadOfSnapping()
    {
        Basis current = Basis.Identity;
        Basis target = new(new Quaternion(Vector3.Up, Mathf.DegToRad(90.0f)));

        Basis smoothed = IKTargetBodyActuatorMath.ComputeFollowBasis(
            current,
            target,
            deltaSeconds: 1.0f / 60.0f,
            rotationResponsiveness: 24.0f,
            rotationSnapAngleRadians: 0.01f);

        float smoothedAngle = new Quaternion(smoothed.Orthonormalized()).AngleTo(new Quaternion(target.Orthonormalized()));
        float originalAngle = new Quaternion(current).AngleTo(new Quaternion(target.Orthonormalized()));

        Assert.True(smoothedAngle > Epsilon, $"Expected a non-zero remaining angle after smoothing, got {smoothedAngle:F6}.");
        Assert.True(smoothedAngle < originalAngle, $"Expected smoothing to reduce the angle. Original={originalAngle:F6}, smoothed={smoothedAngle:F6}.");
    }

    /// <summary>
    /// Verifies tiny angular errors still snap cleanly to the target basis.
    /// </summary>
    [Fact]
    public void ComputeFollowBasis_WithinSnapAngle_SnapsToTarget()
    {
        Basis current = Basis.Identity;
        Basis target = new(new Quaternion(Vector3.Up, 0.005f));

        Basis result = IKTargetBodyActuatorMath.ComputeFollowBasis(
            current,
            target,
            deltaSeconds: 1.0f / 60.0f,
            rotationResponsiveness: 24.0f,
            rotationSnapAngleRadians: 0.01f);

        Quaternion resultRotation = new(result.Orthonormalized());
        Quaternion targetRotation = new(target.Orthonormalized());
        Assert.True(resultRotation.AngleTo(targetRotation) <= Epsilon,
            $"Expected snap-to-target rotation. Remaining angle={resultRotation.AngleTo(targetRotation):F6}.");
    }
}
