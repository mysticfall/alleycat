using AlleyCat.Navigation.Arrival;
using Godot;
using Xunit;
using Xunit.Abstractions;

namespace AlleyCat.Tests.Navigation.Arrival;

/// <summary>Analytic swing correction budgets, pivot geometry and feedback contracts.</summary>
public class SwingCorrectionAllocatorTests(ITestOutputHelper output)
{
    private static SwingCorrectionCaps Caps => new();
    private readonly SwingCorrectionAllocator _allocator = new(Caps);

    /// <summary>Every non-active playback state refuses correction.</summary>
    [Theory]
    [InlineData(SwingCorrectionState.Idle)]
    [InlineData(SwingCorrectionState.Frozen)]
    [InlineData(SwingCorrectionState.Finished)]
    [InlineData(SwingCorrectionState.AfterFinalSwing)]
    public void InactivePosesNeverCorrect(SwingCorrectionState state) => Assert.Equal(default, _allocator.Step(new(new Vector2(1, 1), 1), Window(SupportPhase.LeftSwing), 0, [], Vector2.One, Vector2.Zero, SupportPhase.LeftSwing, state));

    /// <summary>No correction leaks outside the permitted support window.</summary>
    [Fact]
    public void DoubleSupportAndTicksOutsideWindowNeverCorrect()
    {
        var residual = new SwingCorrectionResidual(Vector2.One, 1);
        Assert.Equal(default, _allocator.Step(residual, Window(SupportPhase.BothPlanted), 0, [], Vector2.One, Vector2.Zero, SupportPhase.BothPlanted));
        Assert.Equal(default, _allocator.Step(residual, Window(SupportPhase.LeftSwing), -1, [], Vector2.One, Vector2.Zero, SupportPhase.LeftSwing));
        Assert.Equal(default, _allocator.Step(residual, Window(SupportPhase.LeftSwing), 10, [], Vector2.One, Vector2.Zero, SupportPhase.LeftSwing));
    }

    /// <summary>The live Planted Combination Rule gates the predicted support phase.</summary>
    [Theory]
    [InlineData(SupportPhase.BothPlanted)]
    [InlineData(SupportPhase.RightSwing)]
    [InlineData(SupportPhase.BothSwing)]
    public void LivePhaseMismatchRefusesCorrection(SupportPhase livePhase) =>
        Assert.Equal(default, _allocator.Step(new(Vector2.One, 1f), Window(SupportPhase.LeftSwing), 0,
            [], Vector2.Zero, Vector2.One, livePhase));

    /// <summary>Only the last eligible windows needed to cover yaw receive a share.</summary>
    [Fact]
    public void YawIsReservedForLateEligibleWindows()
    {
        SupportWindow early = Window(SupportPhase.LeftSwing);
        SupportWindow late = Window(SupportPhase.RightSwing, first: 10);
        var residual = new SwingCorrectionResidual(Vector2.Zero, MathF.PI / 60f);
        SwingCorrection correction = _allocator.Step(residual, early, 0,
            [Window(SupportPhase.BothPlanted), late], Vector2.Zero, Vector2.One, early.Phase);
        Near(0f, correction.Yaw);
        correction = _allocator.Step(residual, late, 10, [], Vector2.Zero, Vector2.One, late.Phase);
        Near(MathF.PI / 600f, correction.Yaw);
        // Five degrees require two degrees in the early window, three in the late one.
        correction = _allocator.Step(residual with
        {
            Yaw = 5f * MathF.PI / 180f
        }, early, 0,
            [late], Vector2.Zero, Vector2.One, early.Phase);
        Near(2f * MathF.PI / 1800f, correction.Yaw);
    }

    /// <summary>Endpoint swing is compensated in full this tick, not divided over future ticks.</summary>
    [Fact]
    public void TickCompensatesEndpointSwingImmediately()
    {
        const float yaw = 0.003f;
        SwingCorrection correction = _allocator.Step(new(Vector2.Zero, yaw * 10f),
            Window(SupportPhase.LeftSwing), 0, [], new Vector2(0, 0.1f), new Vector2(0, 0.5f), SupportPhase.LeftSwing);
        // E-b=(0,0.4), so (R-I)(E-b)=(-0.4 sin(yaw), 0.4(cos(yaw)-1)).
        Near(0.4f * MathF.Sin(yaw), correction.Planar.X);
        Near(0.4f * (1f - MathF.Cos(yaw)), correction.Planar.Y);
    }

    /// <summary>A future flight without translation capacity must not multiply current endpoint compensation.</summary>
    [Fact]
    public void EndpointCompensationIsNotRedistributedIntoWindowShares()
    {
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing), Window(SupportPhase.BothSwing, first: 10)];
        Vector2 target = new(0, 0.5f);
        Vector2 origin = Vector2.Zero;
        Vector2 ball = new(0, 0.1f);
        float heading = 0f;
        float targetHeading = 5f * MathF.PI / 180f;
        float remainingTravel = 0.5f;
        SwingCorrection first = default;
        // Observe both complete windows. Two degrees in single swing need about 14 mm lateral;
        // the final zero-travel flight can supply its three degrees, but no translation.
        for (int tick = 0; tick < 20; tick++)
        {
            int index = tick / 10;
            Vector2 endpoint = new(0, remainingTravel);
            var residual = new SwingCorrectionResidual(Rotate(target - origin - Rotate(endpoint, heading), -heading), targetHeading - heading);
            SwingCorrection correction = _allocator.Step(residual, windows[index], tick, windows.AsSpan(index + 1),
                Rotate(ball - origin, -heading), endpoint, windows[index].Phase);
            if (tick == 0)
            {
                first = correction;
            }

            origin = (index == 0 ? ball + Rotate(origin - ball, correction.Yaw) : origin) + Rotate(correction.Planar, heading);
            heading += correction.Yaw;
            if (index == 0)
            {
                origin += Rotate(new Vector2(0, 0.05f), heading);
                remainingTravel = Math.Max(0f, remainingTravel - 0.05f);
            }
        }

        output.WriteLine($"Mixed endpoint compensation: final error={(target - origin).Length() * 1000f:R} mm");
        Closed(new(target - origin, targetHeading - heading));
        // First tick's E-b=(0,0.4); compensate exactly, not twice because two windows remain.
        float firstYaw = 2f * MathF.PI / 1800f;
        Near(0.4f * MathF.Sin(firstYaw), first.Planar.X);
        Near(0.4f * (1f - MathF.Cos(firstYaw)), first.Planar.Y);
        Assert.True(_allocator.CanClose(new(Vector2.Zero, targetHeading), windows,
            [new(target, ball), default]));
    }

    /// <summary>Yaw fitting its own cap can still make otherwise feasible translation impossible.</summary>
    [Fact]
    public void FeasibilityChargesEndpointLeverArm()
    {
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing)];
        SwingCorrectionGeometry[] geometry = [new(new Vector2(0, 1), Vector2.Zero)];
        Assert.True(_allocator.CanClose(default, windows, geometry));
        // Three degrees swings a one-metre endpoint left by sin(3°)=52.336 mm > 50 mm cap.
        Assert.False(_allocator.CanClose(new(Vector2.Zero, MathF.PI / 60f), windows, geometry));
        // The same yaw fits at zero lever arm.
        Assert.True(_allocator.CanClose(new(Vector2.Zero, MathF.PI / 60f), windows, [default]));
    }

    /// <summary>Unused early capacity cannot pay a late swing's endpoint cost after that window has ended.</summary>
    [Fact]
    public void FeasibilityCannotBorrowExpiredTranslationCapacity()
    {
        const int ticks = 30;
        const float travel = 1.5f;
        float targetHeading = MathF.PI / 60f;
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing, ticks), Window(SupportPhase.RightSwing, ticks, ticks)];
        SwingCorrectionGeometry[] geometry =
        [
            new(new Vector2(0, travel), new Vector2(0, 0.1f)),
            new(new Vector2(0, 1.4f), new Vector2(0, 0.1f)),
        ];
        bool feasible = _allocator.CanClose(new(Vector2.Zero, targetHeading), windows, geometry);
        Vector2 target = new(0, travel);
        Vector2 origin = Vector2.Zero;
        Vector2 ball = new(0, 0.1f);
        float heading = 0f;
        float remainingTravel = travel;
        // Complete observation window: both full swings, through the final planned movement tick.
        // First swing has no yaw; the last needs 1.3*sin(3°)=68.037 mm but has only 50 mm.
        // Separate budgets (zero position, 3° < 6°), or pooled 100 mm lateral, incorrectly accept.
        for (int tick = 0; tick < 2 * ticks; tick++)
        {
            int index = tick / ticks;
            if (tick == ticks)
            {
                ball = origin + Rotate(new Vector2(0, 0.1f), heading);
            }

            Vector2 endpoint = new(0, remainingTravel);
            Vector2 predicted = origin + Rotate(endpoint, heading);
            var residual = new SwingCorrectionResidual(Rotate(target - predicted, -heading), targetHeading - heading);
            SwingCorrection correction = _allocator.Step(residual, windows[index], tick, windows.AsSpan(index + 1),
                Rotate(ball - origin, -heading), endpoint, windows[index].Phase);
            origin = ball + Rotate(origin - ball, correction.Yaw) + Rotate(correction.Planar, heading);
            heading += correction.Yaw;
            float movement = (index == 0 ? 0.1f : 1.4f) / ticks;
            origin += Rotate(new Vector2(0, movement), heading);
            remainingTravel -= movement;
        }

        float error = (target - origin).Length();
        output.WriteLine($"Expired-capacity scenario: CanClose={feasible}, final error={error * 1000f:R} mm");
        Assert.InRange(error, 0.015f, 0.025f);
        Assert.InRange(Math.Abs(targetHeading - heading), 0f, 0.05f * MathF.PI / 180f);
        // Acceptance: CanClose must never promise closure when the final error exceeds 1 mm.
        Assert.False(feasible && error > 0.001f);
        Assert.False(feasible);
    }

    /// <summary>Feasibility uses the late window's lever and ignores flight ball data.</summary>
    [Fact]
    public void FeasibilityUsesLateGeometryAndFlightOrigin()
    {
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing), Window(SupportPhase.RightSwing, first: 10)];
        var residual = new SwingCorrectionResidual(Vector2.Zero, MathF.PI / 60f);
        Assert.True(_allocator.CanClose(residual, windows, [new(new Vector2(0, 10), Vector2.Zero), default]));
        Assert.False(_allocator.CanClose(residual, windows, [default, new(new Vector2(0, 10), Vector2.Zero)]));
        Assert.True(_allocator.CanClose(residual, [Window(SupportPhase.BothSwing, travel: 1f)],
            [new(new Vector2(0, 1), new Vector2(float.NaN, float.PositiveInfinity))]));
        _ = Assert.Throws<ArgumentException>(() => _allocator.CanClose(residual, windows, []));
    }

    /// <summary>Changing input cannot spend more than the original per-window budget.</summary>
    [Theory]
    [InlineData(SupportPhase.LeftSwing, 1f, 0.030f)]
    [InlineData(SupportPhase.RightSwing, -1f, -0.100f)]
    public void SingleSwingCapsHoldEvenWhenResidualGrows(SupportPhase phase, float sign, float expectedForward)
    {
        Vector2 total = Vector2.Zero;
        float yaw = 0f;
        for (int tick = 0; tick < 10; tick++)
        {
            SwingCorrection correction = _allocator.Step(new(new Vector2(sign * (tick + 1), sign * (tick + 1)), sign), Window(phase), tick, [], Vector2.Zero, Vector2.Zero, phase);
            Near(sign * 0.005f, correction.Planar.X);
            Near(expectedForward / 10f, correction.Planar.Y);
            Near(sign * MathF.PI / 600f, correction.Yaw);
            total += correction.Planar;
            yaw += correction.Yaw;
        }

        Near(sign * 0.050f, total.X);
        Near(expectedForward, total.Y);
        Near(sign * MathF.PI / 60f, yaw);
    }

    /// <summary>Flight uses a circular translation budget rather than a box.</summary>
    [Fact]
    public void FlightCapsUseOwnTravelAndVectorLengthNotIndependentAxes()
    {
        // A 0.4 m window permits 0.06 m total, or 0.006 m per tick along this 3:4 diagonal.
        Vector2 total = Vector2.Zero;
        float yaw = 0f;
        for (int tick = 0; tick < 10; tick++)
        {
            SwingCorrection correction = _allocator.Step(new(new Vector2(3, 4), 1), Window(SupportPhase.BothSwing, travel: 0.4f), tick, [], new Vector2(float.NaN, float.PositiveInfinity), Vector2.Zero, SupportPhase.BothSwing);
            Near(0.0036f, correction.Planar.X);
            Near(0.0048f, correction.Planar.Y);
            Assert.Equal(SwingCorrectionPivot.Origin, correction.Pivot);
            Assert.Equal(Vector2.Zero, correction.OriginShift);
            total += correction.Planar;
            yaw += correction.Yaw;
        }

        Near(0.060f, total.Length());
        Near(MathF.PI / 60f, yaw);
    }

    /// <summary>Disabled flight is absent from both allocation and feasibility.</summary>
    [Fact]
    public void DisabledFlightNeitherCorrectsNorConsumesShares()
    {
        var allocator = new SwingCorrectionAllocator(Caps with
        {
            AllowFlightCorrection = false
        });
        var residual = new SwingCorrectionResidual(new Vector2(0, 0.020f), 0.01f);
        SupportWindow flight = Window(SupportPhase.BothSwing, travel: 1f);
        Assert.Equal(default, allocator.Step(residual, flight, 0, [], Vector2.One, Vector2.Zero, SupportPhase.BothSwing));
        Assert.False(allocator.CanClose(residual, [flight], [default]));
        SwingCorrection correction = allocator.Step(residual, Window(SupportPhase.LeftSwing), 0, [flight], Vector2.Zero, Vector2.Zero, SupportPhase.LeftSwing);
        Near(0.002f, correction.Planar.Y);
        Near(0.001f, correction.Yaw);
    }

    /// <summary>Both single-swing phases use the supplied planted ball as pivot.</summary>
    [Theory]
    [InlineData(SupportPhase.LeftSwing)]
    [InlineData(SupportPhase.RightSwing)]
    public void BallPivotHasAnalyticShiftAndPreservesRadius(SupportPhase phase)
    {
        // Deliberately tune the cap to 90 degrees for exact analytic geometry. o=(0,0), b=(2,1).
        // o-b=(-2,-1); R90(o-b)=(1,-2); (R90-I)(o-b)=(3,-1).
        var allocator = new SwingCorrectionAllocator(Caps with
        {
            YawRadians = MathF.PI / 2f
        });
        SwingCorrection correction = allocator.Step(new(Vector2.Zero, MathF.PI / 2f), Window(phase, ticks: 1), 0, [], new Vector2(2, 1), Vector2.Zero, phase);
        Assert.Equal(SwingCorrectionPivot.Ball, correction.Pivot);
        Near(MathF.PI / 2f, correction.Yaw);
        Near(3f, correction.OriginShift.X);
        Near(-1f, correction.OriginShift.Y);
        // Zero endpoint offset: demand = -(3,-1) = (-3,1), clamped to (-50,30) mm.
        Near(-0.050f, correction.Planar.X);
        Near(0.030f, correction.Planar.Y);
        // Yaw alone preserves sqrt(5); independent translation is intentionally not part of this assertion.
        Near(MathF.Sqrt(5f), (correction.OriginShift - new Vector2(2, 1)).Length());
    }

    /// <summary>A feasible residual is divided evenly across all remaining window fractions.</summary>
    [Fact]
    public void EqualWindowsSpreadEvenlyAndCloseWithFeedback()
    {
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing), Window(SupportPhase.RightSwing, first: 10), Window(SupportPhase.LeftSwing, first: 20)];
        var residual = new SwingCorrectionResidual(new Vector2(0.060f, 0.045f), 0f);
        for (int index = 0; index < windows.Length; index++)
        {
            SupportWindow window = windows[index];
            for (int tick = window.FirstTick; tick <= window.LastTick; tick++)
            {
                SwingCorrection correction = _allocator.Step(residual, window, tick, windows.AsSpan(index + 1), Vector2.Zero, Vector2.Zero, window.Phase);
                // 30 ticks: 60/30 mm lateral, 45/30 mm forward. Yaw has a separate late schedule.
                Near(0.002f, correction.Planar.X);
                Near(0.0015f, correction.Planar.Y);
                Near(0f, correction.Yaw);
                residual = OriginEndpointFeedback(residual, correction);
            }
        }

        Closed(residual);
    }

    /// <summary>Single-swing and flight capacities close a diagonal residual in either order.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedUnequalWindowsCloseWithinPerTickCaps(bool flightFirst)
    {
        SupportWindow[] windows = flightFirst
            ? [Window(SupportPhase.BothSwing, ticks: 4, travel: 0.2f), Window(SupportPhase.LeftSwing, ticks: 7, first: 4)]
            : [Window(SupportPhase.LeftSwing, ticks: 7), Window(SupportPhase.BothSwing, ticks: 4, first: 7, travel: 0.2f)];
        // SS supplies at most (50,30) mm; the flight disc supplies the remaining (18,24) mm.
        var residual = new SwingCorrectionResidual(new Vector2(0.068f, 0.054f), 0f);
        for (int index = 0; index < windows.Length; index++)
        {
            SupportWindow window = windows[index];
            for (int tick = window.FirstTick; tick <= window.LastTick; tick++)
            {
                SwingCorrection correction = _allocator.Step(residual, window, tick, windows.AsSpan(index + 1), Vector2.Zero, Vector2.Zero, window.Phase);
                if (window.Phase == SupportPhase.BothSwing)
                {
                    Assert.InRange(correction.Planar.Length(), 0f, (0.030f / 4f) + 1e-7f);
                }
                else
                {
                    Assert.InRange(correction.Planar.X, (-0.050f / 7f) - 1e-7f, (0.050f / 7f) + 1e-7f);
                    Assert.InRange(correction.Planar.Y, (-0.100f / 7f) - 1e-7f, (0.030f / 7f) + 1e-7f);
                }

                Assert.InRange(Math.Abs(correction.Yaw), 0f, (MathF.PI / 60f / window.TickCount) + 1e-7f);
                residual = OriginEndpointFeedback(residual, correction);
            }
        }

        Closed(residual);
    }

    /// <summary>The realised pivot displacement is part of the next tick's residual.</summary>
    [Fact]
    public void FeedbackIncludesNonzeroPivotShift()
    {
        Vector2 target = new(0.010f, 0.010f);
        Vector2 origin = Vector2.Zero;
        Vector2 ball = new(0.1f, 0.1f);
        float heading = 0f;
        for (int tick = 0; tick < 10; tick++)
        {
            var residual = new SwingCorrectionResidual(Rotate(target - origin, -heading), 0.02f - heading);
            SwingCorrection correction = _allocator.Step(residual, Window(SupportPhase.LeftSwing), tick, [], Rotate(ball - origin, -heading), Vector2.Zero, SupportPhase.LeftSwing);
            origin = ball + Rotate(origin - ball, correction.Yaw) + Rotate(correction.Planar, heading);
            heading += correction.Yaw;
        }

        Closed(new(target - origin, 0.02f - heading));
    }

    /// <summary>Feasibility observes each independently tunable directional cap.</summary>
    [Theory]
    [InlineData(0.099f, 0.059f, 0f, true)]
    [InlineData(0f, 0f, 0.10f, true)]
    [InlineData(0.101f, 0f, 0f, false)]
    [InlineData(0f, 0.061f, 0f, false)]
    [InlineData(0f, -0.199f, 0f, true)]
    [InlineData(0f, -0.201f, 0f, false)]
    [InlineData(0f, 0f, 0.11f, false)]
    public void FeasibilityUsesTotalDirectionalAndYawCaps(float right, float forward, float yaw, bool expected) => Assert.Equal(expected, _allocator.CanClose(new(new Vector2(right, forward), yaw), [Window(SupportPhase.LeftSwing), Window(SupportPhase.RightSwing)], [default, default]));

    /// <summary>Mixed feasibility rejects diagonals outside the combined rectangle and disc.</summary>
    [Fact]
    public void MixedFeasibilityUsesDiscOutsideSingleSwingRectangle()
    {
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing), Window(SupportPhase.BothSwing, travel: 0.2f), Window(SupportPhase.BothPlanted)];
        Assert.True(_allocator.CanClose(new(new Vector2(0.067f, 0.053f), 0.1f), windows, [default, default, default]));
        Assert.False(_allocator.CanClose(new(new Vector2(0.080f, 0.060f), 0), windows, [default, default, default]));
        Assert.True(_allocator.CanClose(default, [], []));
        Assert.False(_allocator.CanClose(new(new Vector2(0.001f, 0), 0), [], []));
        Assert.False(_allocator.CanClose(new(Vector2.Zero, 0.001f), [Window(SupportPhase.BothPlanted)], [default]));
    }

    /// <summary>Feasibility follows the rotating-frame closed loop, not independent yaw and planar budgets.</summary>
    [Theory]
    [InlineData(0.099f, 0.059f, SupportPhase.RightSwing, false)]
    [InlineData(0.067f, 0.053f, SupportPhase.BothSwing, true)]
    public void FeasibilityAgreesWithRotatingFrameNearDirectionalCaps(float right, float forward, SupportPhase lastPhase, bool expected)
    {
        SupportWindow[] windows = [Window(SupportPhase.LeftSwing), Window(lastPhase, first: 10, travel: 0.2f)];
        Vector2 target = new(right, forward);
        Vector2 origin = Vector2.Zero;
        float heading = 0f;
        const float targetHeading = 0.1f;
        for (int index = 0; index < windows.Length; index++)
        {
            Vector2 ball = origin;
            SupportWindow window = windows[index];
            for (int tick = window.FirstTick; tick <= window.LastTick; tick++)
            {
                var residual = new SwingCorrectionResidual(Rotate(target - origin, -heading), targetHeading - heading);
                SwingCorrection correction = _allocator.Step(residual, window, tick, windows.AsSpan(index + 1),
                    Rotate(ball - origin, -heading), Vector2.Zero, window.Phase);
                origin = (window.Phase == SupportPhase.BothSwing ? origin : ball + Rotate(origin - ball, correction.Yaw))
                    + Rotate(correction.Planar, heading);
                heading += correction.Yaw;
            }
        }

        float error = (target - origin).Length();
        output.WriteLine($"Near-cap {lastPhase}: final error={error * 1000f:R} mm");
        if (expected)
        {
            Closed(new(target - origin, targetHeading - heading));
        }
        else
        {
            Assert.True(error > 0.001f);
        }

        Assert.Equal(expected, _allocator.CanClose(new(target, targetHeading), windows, [default, default]));
    }

    /// <summary>Configuration values replace defaults and invalid limits fail clearly.</summary>
    [Fact]
    public void CustomCapsAndZeroTravelAreRespected()
    {
        var allocator = new SwingCorrectionAllocator(new SwingCorrectionCaps
        {
            ForwardMetres = 0.01f,
            BackwardMetres = 0.02f,
            LateralMetres = 0.03f,
            YawRadians = 0.04f,
            FlightTravelShare = 0.05f,
        });
        SwingCorrection single = allocator.Step(new(Vector2.One, 1), Window(SupportPhase.LeftSwing, ticks: 1), 0, [], Vector2.Zero, Vector2.Zero, SupportPhase.LeftSwing);
        Assert.Equal(new Vector2(0.03f, 0.01f), single.Planar);
        Near(0.04f, single.Yaw);
        SwingCorrection flight = allocator.Step(new(Vector2.One, 1), Window(SupportPhase.BothSwing, ticks: 1), 0, [], Vector2.Zero, Vector2.Zero, SupportPhase.BothSwing);
        Assert.Equal(Vector2.Zero, flight.Planar);
        Near(0.04f, flight.Yaw);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SwingCorrectionAllocator(Caps with { ForwardMetres = -1 }));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SwingCorrectionAllocator(Caps with { FlightTravelShare = float.NaN }));
    }

    /// <summary>Flight shares are equal unless a window's own travel cap requires redistribution.</summary>
    [Theory]
    [InlineData(0.4f, 0.010f)]
    [InlineData(0.04f, 0.014f)]
    public void UnequalFlightBudgetsSpreadWithoutStrandingResidual(float futureTravel, float firstWindowTotal)
    {
        SupportWindow[] windows = [Window(SupportPhase.BothSwing, travel: 0.2f), Window(SupportPhase.BothSwing, first: 10, travel: futureTravel)];
        var residual = new SwingCorrectionResidual(new Vector2(0, 0.020f), 0f);
        for (int index = 0; index < windows.Length; index++)
        {
            for (int tick = windows[index].FirstTick; tick <= windows[index].LastTick; tick++)
            {
                SwingCorrection correction = _allocator.Step(residual, windows[index], tick, windows.AsSpan(index + 1), Vector2.Zero, Vector2.Zero, windows[index].Phase);
                // 20 mm across two windows: 10 mm each, or 14 + 6 mm when the second is travel-capped.
                Near((index == 0 ? firstWindowTotal : 0.020f - firstWindowTotal) / 10f, correction.Planar.Y);
                residual = OriginEndpointFeedback(residual, correction);
            }
        }

        Closed(residual);
    }

    private static SupportWindow Window(SupportPhase phase, int ticks = 10, int first = 0, float travel = 0f) =>
        new(phase, first, first + ticks - 1, new SequencePose(Vector2.Zero, 0), new SequencePose(new Vector2(0, travel), 0));

    /// <summary>All remaining travel rotates with the corrected actor, through the final swing tick.</summary>
    [Theory]
    [InlineData(0.5f, 1, 3f, 0f)]
    [InlineData(1.0f, 2, 5f, 0f)]
    [InlineData(1.5f, 2, 5f, 0.040f)]
    public void MovingEndpointClosesInRotatingFrame(float travel, int count, float degrees, float lateral)
    {
        const int ticks = 30;
        SupportWindow[] windows = [.. Enumerable.Range(0, count).Select(index => Window(SupportPhase.LeftSwing, ticks, index * ticks))];
        Vector2 target = new(lateral, travel);
        Vector2 origin = Vector2.Zero;
        Vector2 ball = new(0.1f, 0.1f);
        float heading = 0f;
        float targetHeading = degrees * MathF.PI / 180f;
        float remainingTravel = travel;
        float swingTravel = count == 1 ? travel : 0.1f;
        SwingCorrectionGeometry[] geometry = [.. Enumerable.Range(0, count)
            .Select(index => new SwingCorrectionGeometry(new Vector2(0, index == 0 ? travel : swingTravel), ball))];
        Assert.True(_allocator.CanClose(new(new Vector2(lateral, 0), targetHeading), windows, geometry));
        for (int tick = 0; tick < count * ticks; tick++)
        {
            if (tick > 0 && tick % ticks == 0)
            {
                // The next eligible swing follows uncorrected planned travel; each planted ball stays
                // fixed in world space for its own support window, not for the entire route.
                origin += Rotate(new Vector2(0, remainingTravel - swingTravel), heading);
                remainingTravel = swingTravel;
                ball = origin + Rotate(new Vector2(0.1f, 0.1f), heading);
            }

            Vector2 endpoint = new(0, remainingTravel);
            Vector2 predicted = origin + Rotate(endpoint, heading);
            var residual = new SwingCorrectionResidual(Rotate(target - predicted, -heading), targetHeading - heading);
            Vector2 localBall = Rotate(ball - origin, -heading);
            SwingCorrection correction = _allocator.Step(residual, windows[tick / ticks], tick,
                windows.AsSpan((tick / ticks) + 1), localBall, endpoint, SupportPhase.LeftSwing);
            Assert.InRange(correction.Planar.X, (-0.050f / ticks) - 1e-7f, (0.050f / ticks) + 1e-7f);
            Assert.InRange(correction.Planar.Y, (-0.100f / ticks) - 1e-7f, (0.030f / ticks) + 1e-7f);
            Assert.InRange(Math.Abs(correction.Yaw), 0f, (MathF.PI / 60f / ticks) + 1e-7f);
            // World-space pivot geometry, independent of the returned OriginShift.
            origin = ball + Rotate(origin - ball, correction.Yaw) + Rotate(correction.Planar, heading);
            heading += correction.Yaw;
            origin += Rotate(new Vector2(0, swingTravel / ticks), heading);
            remainingTravel -= swingTravel / ticks;
        }

        output.WriteLine($"Final error: {(target - origin).Length() * 1000f:R} mm, {Math.Abs(targetHeading - heading) * 180f / MathF.PI:R} degrees");
        Closed(new(target - origin, targetHeading - heading));
    }

    private static Vector2 Rotate(Vector2 value, float angle) => new(
        (value.X * MathF.Cos(angle)) - (value.Y * MathF.Sin(angle)),
        (value.X * MathF.Sin(angle)) + (value.Y * MathF.Cos(angle)));

    /// <summary>Active single-swing and flight ticks do not allocate managed memory.</summary>
    [Fact]
    public void ActiveStepDoesNotAllocate()
    {
        SupportWindow single = Window(SupportPhase.LeftSwing);
        SupportWindow flight = Window(SupportPhase.BothSwing, travel: 0.4f);
        SupportWindow[] future = [single, flight];
        var residual = new SwingCorrectionResidual(new Vector2(0.02f, 0.02f), 0.15f);
        Vector2 ball = new(0.1f, 0.1f);
        float sum = 0f;
        for (int index = 0; index < 1000; index++)
        {
            sum += _allocator.Step(residual, single, 0, future, ball, Vector2.One, single.Phase).Planar.X;
            sum += _allocator.Step(residual, flight, 0, future, ball, Vector2.One, flight.Phase).Planar.X;
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 10000; index++)
        {
            sum += _allocator.Step(residual, single, 0, future, ball, Vector2.One, single.Phase).Planar.X;
            sum += _allocator.Step(residual, flight, 0, future, ball, Vector2.One, flight.Phase).Planar.X;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(sum > 0f);
    }

    // Degenerate rotating-frame feedback for tests with endpoint and pivot both at the origin.
    // Non-zero geometry uses the independent world-space model above, never returned OriginShift.
    private static SwingCorrectionResidual OriginEndpointFeedback(SwingCorrectionResidual residual, SwingCorrection correction) =>
        new(Rotate(residual.Position - correction.Planar, -correction.Yaw), residual.Yaw - correction.Yaw);

    private static void Closed(SwingCorrectionResidual residual)
    {
        Assert.InRange(residual.Position.Length(), 0f, 0.001f);
        Assert.InRange(Math.Abs(residual.Yaw), 0f, 0.05f * MathF.PI / 180f);
    }

    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 1e-6f, expected + 1e-6f);
}
