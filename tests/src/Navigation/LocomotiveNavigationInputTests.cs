using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// Pure deterministic coverage for NAV-001 locomotive command conversion.
/// </summary>
public sealed class LocomotiveNavigationInputTests
{
    private const float Tolerance = 0.0001f;

    /// <summary>
    /// Verifies Godot forward and backward world directions retain their actor-local signs.
    /// </summary>
    [Theory]
    [InlineData(0.0f, 0.0f, -1.0f, 0.0f, 1.0f)]
    [InlineData(0.0f, 0.0f, 1.0f, 0.0f, -1.0f)]
    [InlineData(1.0f, 0.0f, 0.0f, 1.0f, 0.0f)]
    [InlineData(-1.0f, 0.0f, 0.0f, -1.0f, 0.0f)]
    public void ToLocalMovement_IdentityActorPreservesForwardBackwardAndLateralSigns(
        float worldX,
        float worldY,
        float worldZ,
        float expectedX,
        float expectedY)
    {
        Vector2 actual = LocomotiveNavigationInput.ToLocalMovement(
            Basis.Identity,
            new Vector3(worldX, worldY, worldZ));

        AssertVectorClose(new Vector2(expectedX, expectedY), actual);
    }

    /// <summary>
    /// Verifies local conversion follows actor rotation and preserves diagonal intent.
    /// </summary>
    [Fact]
    public void ToLocalMovement_RotatedScaledActorProducesBoundedDiagonalInput()
    {
        Basis rotation = new(Vector3.Up, Mathf.Pi / 2.0f);
        Basis actorBasis = rotation * Basis.FromScale(new Vector3(2.0f, 0.5f, 3.0f));
        Vector3 worldDirection = rotation * new Vector3(1.0f, 0.0f, -1.0f).Normalized();

        Vector2 actual = LocomotiveNavigationInput.ToLocalMovement(actorBasis, worldDirection);

        AssertVectorClose(new Vector2(Mathf.Sqrt(0.5f), Mathf.Sqrt(0.5f)), actual);
        Assert.InRange(actual.Length(), 0.0f, 1.0f);
    }

    /// <summary>
    /// Verifies malformed coordinate inputs cannot leak non-finite or unbounded commands.
    /// </summary>
    [Fact]
    public void ToLocalMovement_InvalidInputsReturnNeutral()
    {
        var degenerate = Basis.FromScale(new Vector3(0.0f, 1.0f, 1.0f));

        Assert.Equal(Vector2.Zero, LocomotiveNavigationInput.ToLocalMovement(degenerate, Vector3.Forward));
        Assert.Equal(
            Vector2.Zero,
            LocomotiveNavigationInput.ToLocalMovement(
                Basis.Identity,
                new Vector3(float.NaN, 0.0f, 0.0f)));
    }

    /// <summary>
    /// Verifies yaw output uses the error sign, is proportional outside the dead zone, and clamps at both bounds.
    /// </summary>
    [Theory]
    [InlineData(0.6f, 2.0f, 0.1f, 1.0f)]
    [InlineData(-0.6f, 2.0f, 0.1f, -1.0f)]
    [InlineData(0.3f, 2.0f, 0.1f, 0.4f)]
    [InlineData(-0.3f, 2.0f, 0.1f, -0.4f)]
    [InlineData(0.1f, 2.0f, 0.1f, 0.0f)]
    [InlineData(-0.05f, 2.0f, 0.1f, 0.0f)]
    public void ToRotation_MapsProportionallyWithDeadZoneAndBounds(
        float yawError,
        float gain,
        float deadZone,
        float expected)
    {
        Vector2 actual = LocomotiveNavigationInput.ToRotation(yawError, gain, deadZone);

        Assert.InRange(actual.X, -1.0f, 1.0f);
        Assert.InRange(Mathf.Abs(actual.X - expected), 0.0f, Tolerance);
        Assert.Equal(0.0f, actual.Y);
    }

    /// <summary>
    /// Verifies the dead-zone boundary is continuous and invalid tuning cannot escape as non-finite output.
    /// </summary>
    [Fact]
    public void ToRotation_DeadZoneBoundaryIsContinuousAndInvalidInputIsNeutral()
    {
        Vector2 atBoundary = LocomotiveNavigationInput.ToRotation(0.1f, 2.0f, 0.1f);
        Vector2 justOutside = LocomotiveNavigationInput.ToRotation(0.10001f, 2.0f, 0.1f);

        Assert.Equal(Vector2.Zero, atBoundary);
        Assert.InRange(justOutside.X, 0.0f, 0.0001f);
        Assert.Equal(Vector2.Zero, LocomotiveNavigationInput.ToRotation(float.NaN, 1.0f, 0.0f));
        Assert.Equal(Vector2.Zero, LocomotiveNavigationInput.ToRotation(1.0f, float.PositiveInfinity, 0.0f));
    }

    /// <summary>
    /// Establishes semantic turn commands at the world-yaw-to-locomotion boundary.
    /// </summary>
    [Theory]
    [InlineData(-0.75f, 0.75f)]
    [InlineData(0.75f, -0.75f)]
    public void ToSemanticTurnInput_MapsPhysicalRightPositiveAndPhysicalLeftNegative(
        float worldYawCommand,
        float expectedSemanticCommand)
    {
        Assert.Equal(
            expectedSemanticCommand,
            LocomotiveNavigationInput.ToSemanticTurnInput(worldYawCommand),
            4);
    }

    private static void AssertVectorClose(Vector2 expected, Vector2 actual)
    {
        Assert.InRange(Mathf.Abs(actual.X - expected.X), 0.0f, Tolerance);
        Assert.InRange(Mathf.Abs(actual.Y - expected.Y), 0.0f, Tolerance);
    }
}
