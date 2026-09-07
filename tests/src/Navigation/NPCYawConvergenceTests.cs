using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCYawConvergenceTests
{
    [Theory]
    [InlineData(0f, -0.2f, -0.5f, -0.199901f)]
    [InlineData(0f, 0.2f, 0.5f, 0.199901f)]
    public void ClampRotationDelta_StopsInsideNumericalBoundaryBeforeCrossing(
        float current,
        float target,
        float proposed,
        float expected)
    {
        float actual = NPCYawConvergence.ClampRotationDelta(current, target, proposed);

        Assert.Equal(expected, actual, 5);
        float nextError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(target - current - actual));
        Assert.True(nextError <= NPCYawConvergence.NumericalToleranceRadians);
    }

    [Theory]
    [InlineData(3.13f, -3.13f, 0.2f, 0.02308625f)]
    [InlineData(-3.13f, 3.13f, -0.2f, -0.02308601f)]
    public void ClampRotationDelta_HandlesWrapAroundWithoutSideExcursion(
        float current,
        float target,
        float proposed,
        float expected)
    {
        float actual = NPCYawConvergence.ClampRotationDelta(current, target, proposed);

        Assert.Equal(expected, actual, 4);
    }

    [Fact]
    public void ClampRotationDelta_AllowsLandingInsideNumericalTolerance()
    {
        float actual = NPCYawConvergence.ClampRotationDelta(0f, 0.2f, 0.19995f);

        Assert.Equal(0.19995f, actual, 6);
    }

    [Fact]
    public void ClampRotationDelta_AllowsMovementAwayFromTerminalYaw()
    {
        float actual = NPCYawConvergence.ClampRotationDelta(0f, 0.2f, -0.05f);

        Assert.Equal(-0.05f, actual, 6);
    }

    [Fact]
    public void StepTerminalRotation_ConvergesWithoutCrossingAndPreservesAppliedRateState()
    {
        const float target = 0.2f;
        const float delta = 1f / 60f;
        float yaw = 0f;
        float rate = 0f;
        float previousError = target;

        for (int tick = 0; tick < 120; tick++)
        {
            yaw += NPCYawConvergence.StepTerminalRotation(yaw, target, ref rate, delta);
            float error = NPCNavigationRoutePlan.WrapAngle(target - yaw);
            Assert.InRange(
                Mathf.Abs(error),
                0f,
                Math.Max(Mathf.Abs(previousError), NPCYawConvergence.NumericalToleranceRadians));
            Assert.InRange(Mathf.Abs(rate - ((yaw - (target - previousError)) / delta)), 0f, 0.00005f);
            previousError = error;
        }

        Assert.InRange(Mathf.Abs(previousError), 0f, NPCYawConvergence.NumericalToleranceRadians);
    }

    [Fact]
    public void StepTerminalRotation_InvalidDeltaClearsRateAndDoesNotRotate()
    {
        float rate = 1f;

        float applied = NPCYawConvergence.StepTerminalRotation(0f, 1f, ref rate, 0f);

        Assert.Equal(0f, applied);
        Assert.Equal(0f, rate);
    }

    [Fact]
    public void StepTerminalRotation_OutsideBoundaryProducesCorrectionWhenAuthoredDeltaWouldBeNegligible()
    {
        const float target = 0.048633344f;
        const float delta = 1f / 60f;
        float current = target - 0.00010037f;
        float rate = 0f;

        float applied = NPCYawConvergence.StepTerminalRotation(
            current,
            target,
            ref rate,
            delta,
            out NPCYawConvergenceStepTrace trace);
        float realisedYaw = NPCNavigationRoutePlan.YawFromDirection(
            new Basis(Vector3.Up, current + applied) * Vector3.Forward);
        float realisedError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(target - realisedYaw));

        Assert.True(trace.IsValid);
        Assert.True(trace.BrakingTargetYawRate > 0f);
        Assert.True(applied > 0.000001f, $"applied={applied:R}; trace={trace}");
        Assert.True(realisedError <= NPCYawConvergence.NumericalToleranceRadians,
            $"realisedError={realisedError:R}; trace={trace}");
    }

    [Theory]
    [InlineData(-0.4f, -0.00004792f)]
    [InlineData(0.4f, 0.00004792f)]
    public void RealiseRuntimeYaw_MatchesPositiveAndNegativeBasisRoundTrip(float current, float delta)
    {
        float expected = NPCNavigationRoutePlan.YawFromDirection(
            (new Basis(Vector3.Up, delta) * new Basis(Vector3.Up, current)).Orthonormalized()
                * Vector3.Forward);

        float actual = NPCYawConvergence.RealiseRuntimeYaw(current, delta);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(-1f, -0.0000005f)]
    [InlineData(1f, 0.0000005f)]
    public void RealiseRuntimeYaw_IgnoresSubActionableRotationWithoutPersistentDrift(float current, float delta)
    {
        float yaw = current;

        for (int tick = 0; tick < 120; tick++)
        {
            yaw = NPCYawConvergence.RealiseRuntimeYaw(yaw, delta);
        }

        Assert.Equal(current, yaw);
        Assert.False(NPCYawConvergence.IsActionableRotation(delta));
    }

    [Theory]
    [InlineData(-1f, -0.0000005f, -1)]
    [InlineData(-1f, -0.0000005f, 0)]
    [InlineData(-1f, -0.0000005f, 1)]
    [InlineData(-1f, 0f, -1)]
    [InlineData(-1f, 0f, 0)]
    [InlineData(-1f, 0f, 1)]
    [InlineData(-1f, 0.0000005f, -1)]
    [InlineData(-1f, 0.0000005f, 0)]
    [InlineData(-1f, 0.0000005f, 1)]
    [InlineData(1f, -0.0000005f, -1)]
    [InlineData(1f, -0.0000005f, 0)]
    [InlineData(1f, -0.0000005f, 1)]
    [InlineData(1f, 0f, -1)]
    [InlineData(1f, 0f, 0)]
    [InlineData(1f, 0f, 1)]
    [InlineData(1f, 0.0000005f, -1)]
    [InlineData(1f, 0.0000005f, 0)]
    [InlineData(1f, 0.0000005f, 1)]
    public void StepTerminalRotation_SymmetricBoundaryStatesSettleWithoutExcursionOrOscillation(
        float side,
        float boundaryOffset,
        int carriedRateDirection)
    {
        const float deltaSeconds = 1f / 60f;
        float target = side * 0.4f;
        float initialError = NPCYawConvergence.NumericalToleranceRadians + boundaryOffset;
        float yaw = target - (side * initialError);
        float rate = side * carriedRateDirection * 0.02f;
        int stableTicks = 0;

        for (int tick = 0; tick < 240; tick++)
        {
            float before = yaw;
            float applied = NPCYawConvergence.StepTerminalRotation(yaw, target, ref rate, deltaSeconds);
            yaw = NPCYawConvergence.RealiseRuntimeYaw(yaw, applied);
            float error = NPCNavigationRoutePlan.WrapAngle(target - yaw);

            Assert.True(Mathf.Sign(error) == side
                    || Mathf.Abs(error) <= NPCYawConvergence.NumericalToleranceRadians,
                $"tick={tick}; side={side}; offset={boundaryOffset:R}; carried={carriedRateDirection}; error={error:R}");
            Assert.True(applied == 0f || NPCYawConvergence.IsActionableRotation(applied));

            if (rate == 0f && yaw == before)
            {
                stableTicks++;
            }
            else
            {
                stableTicks = 0;
            }
        }

        Assert.InRange(Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(target - yaw)), 0f,
            NPCYawConvergence.NumericalToleranceRadians);
        Assert.Equal(0f, rate);
        Assert.True(stableTicks >= 8, $"stableTicks={stableTicks}; yaw={yaw:R}; rate={rate:R}");
    }
}
