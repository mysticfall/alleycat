using AlleyCat.IK;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.IK;

/// <summary>
/// Pure IK-003 foot-planting state-machine tests on a synthetic straight leg: interval-start capture without a jump,
/// planar hold with animated height and orientation, roll about the ball, blend-out ending on the animated target,
/// crossfade-style continuity, unreachable pins, no recapture within an interval, and spawn capture.
/// </summary>
public sealed class FootPlantingSolverTests
{
    private const double Tick = 1d / 60d;
    private const float ChainLength = 0.8f;
    private const float Tolerance = 1e-5f;

    // Foot bone at 0.1 m height, leg root 0.75 m above it; the ball sits 0.12 m ahead (-Z) and 0.04 m below the ankle.
    private static readonly Vector3 _ballInFoot = new(0f, -0.04f, -0.12f);
    private static readonly Vector3 _legRoot = new(0f, 0.85f, 0f);

    [Fact]
    public void IntervalStart_CapturesCurrentBallWithoutJump_AndHoldsPlanarPosition()
    {
        var solver = new FootPlantingSolver();
        FootPlantingOutput first = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));
        Assert.Equal(FootPlantingPhase.Pinned, first.Phase);
        AssertClose(first.AnimatedBall, first.TargetBall);

        // The animated foot slides 2 cm backwards and rises 1 cm: the ball stays planar-pinned but follows the height.
        FootPlantingOutput held = solver.Step(Input(true, Foot(new Vector3(0f, 0.11f, 0.02f))));
        Assert.Equal(FootPlantingPhase.Pinned, held.Phase);
        Assert.Equal(first.TargetBall.X, held.TargetBall.X, Tolerance);
        Assert.Equal(first.TargetBall.Z, held.TargetBall.Z, Tolerance);
        Assert.Equal(held.AnimatedBall.Y, held.TargetBall.Y, Tolerance);
        Assert.Equal(0.11f, held.Target.Origin.Y, Tolerance);
    }

    [Fact]
    public void HeelLift_RollsAboutPinnedBall_WithoutPlanarDisplacement()
    {
        var solver = new FootPlantingSolver();
        FootPlantingOutput first = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));

        // Heel lift: rotate the foot 25° about the ball's lateral axis in the animation.
        Basis roll = new(Vector3.Right, Mathf.DegToRad(25f));
        Vector3 ball = first.AnimatedBall;
        Transform3D lifted = new(roll, ball - (roll * _ballInFoot));
        FootPlantingOutput rolled = solver.Step(Input(true, lifted));

        Assert.Equal(first.TargetBall.X, rolled.TargetBall.X, Tolerance);
        Assert.Equal(first.TargetBall.Z, rolled.TargetBall.Z, Tolerance);
        Assert.True(rolled.Target.Basis.IsEqualApprox(roll), "The target orientation follows the animation.");
    }

    /// <summary>
    /// No single-tick jump at lift-off. A correct blend-out is <c>offset(t) = O₀ · (1 − smoothstep(t / T))</c>, and
    /// smoothstep's steepest slope is 1.5 per unit progress, so no tick of it may move the offset by more than
    /// <c>1.5 · |O₀| · Δt / T</c>. That bound applies to every tick, including the last, so a blend that stops short and
    /// then snaps onto the animated target fails it.
    /// </summary>
    [Fact]
    public void IntervalEnd_BlendsOutWithoutSingleTickJump_AndEndsExactlyOnAnimatedTarget()
    {
        const double blendSeconds = 0.2d;
        var solver = new FootPlantingSolver { BlendOutSeconds = blendSeconds };
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));
        FootPlantingOutput held = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0.03f))));
        Vector2 heldOffset = OffsetVector(held);
        Assert.Equal(0.03f, heldOffset.Length(), Tolerance);
        float maximumStep = (float)(1.5d * heldOffset.Length() * Tick / blendSeconds) + Tolerance;

        Vector2 previous = heldOffset;
        FootPlantingOutput output;
        int ticks = 0;
        do
        {
            output = solver.Step(Input(false, Foot(new Vector3(0f, 0.12f, 0.03f + (0.005f * ticks)))));
            Vector2 offset = OffsetVector(output);
            Assert.True(offset.Length() <= previous.Length() + Tolerance, "The blend-out offset never grows.");
            float step = offset.DistanceTo(previous);
            Assert.True(
                step <= maximumStep,
                $"Blend-out tick {ticks + 1} moved the offset {step * 1000:0.###} mm, above the smoothstep bound {maximumStep * 1000:0.###} mm.");
            previous = offset;
            ticks++;
        }
        while (output.Phase == FootPlantingPhase.BlendingOut && ticks < 100);

        Assert.Equal(FootPlantingPhase.Free, output.Phase);
        Assert.Equal((int)Math.Round(blendSeconds / Tick), ticks);
        AssertClose(output.AnimatedBall, output.TargetBall);
    }

    [Fact]
    public void SkippedTicks_KeepThePin_AndAdvanceTheBlendByElapsedTime()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.1d };
        FootPlantingOutput first = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));

        // Three ticks elapse at once while the foot stays planted: the pin holds.
        FootPlantingOutput held = solver.Step(new FootPlantingInput(true, Foot(new Vector3(0f, 0.1f, 0.03f)), _ballInFoot, _legRoot, ChainLength, 3 * Tick));
        Assert.Equal(FootPlantingPhase.Pinned, held.Phase);
        AssertClose(first.TargetBall, held.TargetBall);

        // A release followed by a long gap finishes the blend by elapsed time, not by tick count.
        _ = solver.Step(Input(false, Foot(new Vector3(0f, 0.1f, 0.03f))));
        FootPlantingOutput late = solver.Step(new FootPlantingInput(false, Foot(new Vector3(0f, 0.1f, 0.03f)), _ballInFoot, _legRoot, ChainLength, 0.2d));
        Assert.Equal(FootPlantingPhase.Free, late.Phase);
    }

    [Fact]
    public void Preview_DoesNotAdvanceState()
    {
        var solver = new FootPlantingSolver();
        FootPlantingOutput preview = solver.Preview(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));
        Assert.Equal(FootPlantingPhase.Pinned, preview.Phase);
        Assert.Equal(FootPlantingPhase.Free, solver.Phase);

        // A previewed interval start does not count as the swing-to-planted edge of the real step.
        _ = solver.Step(Input(false, Foot(new Vector3(0f, 0.1f, 0f))));
        _ = solver.Preview(Input(true, Foot(new Vector3(0f, 0.1f, 0.05f))));
        FootPlantingOutput stepped = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0.02f))));
        Assert.Equal(FootPlantingPhase.Pinned, stepped.Phase);
        AssertClose(stepped.AnimatedBall, stepped.TargetBall);
    }

    [Fact]
    public void NewIntervalDuringBlendOut_PinsAtCurrentOutputBall()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.2d };
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0.04f))));
        FootPlantingOutput blending = solver.Step(Input(false, Foot(new Vector3(0f, 0.1f, 0.05f))));
        Assert.Equal(FootPlantingPhase.BlendingOut, blending.Phase);

        FootPlantingOutput repinned = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0.05f))));
        Assert.Equal(FootPlantingPhase.Pinned, repinned.Phase);
        AssertClose(blending.TargetBall, repinned.TargetBall);
    }

    [Fact]
    public void UnreachablePin_BlendsOutWithoutHyperextension_AndIsNotRecapturedInSameInterval()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.1d, ReachMarginMetres = 0.01f };
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));

        // The leg root and its animated foot travel 0.4 m forward while the foot is still planted, so holding the pin
        // would need up to sqrt(0.75² + 0.4²) ≈ 0.85 m of leg.
        int reachReleases = 0;
        float maxDistance = 0f;
        for (int tick = 1; tick <= 40; tick++)
        {
            Vector3 travel = new(0f, 0f, -0.01f * tick);
            FootPlantingOutput output = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f) + travel), _legRoot + travel));
            reachReleases += output.ReachBlendOutStarted ? 1 : 0;
            maxDistance = Math.Max(maxDistance, output.Target.Origin.DistanceTo(_legRoot + travel));
            Assert.NotEqual(FootPlantingPhase.Pinned, reachReleases > 0 ? output.Phase : FootPlantingPhase.Free);
        }

        Assert.Equal(1, reachReleases);
        Assert.True(maxDistance <= ChainLength - 0.01f + Tolerance, $"Target distance {maxDistance} exceeds the reach limit.");
        Assert.Equal(FootPlantingPhase.Free, solver.Phase);

        // A swing tick ends the interval; the next interval pins again.
        Vector3 end = new(0f, 0f, -0.4f);
        _ = solver.Step(Input(false, Foot(new Vector3(0f, 0.1f, 0f) + end), _legRoot + end));
        Assert.Equal(FootPlantingPhase.Pinned, solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f) + end), _legRoot + end)).Phase);
    }

    [Fact]
    public void NearFullAnimatedExtension_IsNotTreatedAsOutOfReach()
    {
        var solver = new FootPlantingSolver { ReachMarginMetres = 0.05f };

        // The animated leg is already beyond chain − margin and keeps extending; a pin that extends the leg no further
        // than the animation itself does is kept.
        Vector3 ankle = new(0f, 0.1f, 0.2f);
        _ = solver.Step(Input(true, Foot(ankle)));
        FootPlantingOutput held = solver.Step(Input(true, Foot(ankle + new Vector3(0f, 0f, 0.01f))));
        Assert.Equal(FootPlantingPhase.Pinned, held.Phase);
        Assert.False(held.ReachBlendOutStarted);
    }

    [Fact]
    public void Spawn_PinsAtCurrentAnimatedPosition_AndResetForgetsThePin()
    {
        var solver = new FootPlantingSolver();
        FootPlantingOutput spawn = solver.Step(Input(true, Foot(new Vector3(0.5f, 0.1f, 0.5f))));
        AssertClose(spawn.AnimatedBall, spawn.TargetBall);

        // Teleport: after a reset the pin is recaptured at the new animated position with no swing in between.
        solver.Reset();
        FootPlantingOutput teleported = solver.Step(Input(true, Foot(new Vector3(3f, 0.1f, 3f))));
        Assert.Equal(FootPlantingPhase.Pinned, teleported.Phase);
        AssertClose(teleported.AnimatedBall, teleported.TargetBall);
    }

    [Fact]
    public void FloatingOrSinkingSource_KeepsAnimatedHeight_WhileHoldingPlanarPin()
    {
        var solver = new FootPlantingSolver();
        FootPlantingOutput first = solver.Step(Input(true, Foot(new Vector3(0f, 0.13f, 0f))));
        foreach (float height in new[] { 0.14f, 0.05f, 0.1f })
        {
            FootPlantingOutput output = solver.Step(Input(true, Foot(new Vector3(0.004f, height, 0.006f))));
            Assert.Equal(first.TargetBall.X, output.TargetBall.X, Tolerance);
            Assert.Equal(first.TargetBall.Z, output.TargetBall.Z, Tolerance);
            Assert.Equal(output.AnimatedBall.Y, output.TargetBall.Y, Tolerance);
        }
    }

    [Fact]
    public void ShortSkip_WithModerateSkeletonTravel_ContinuesThePin()
    {
        var solver = new FootPlantingSolver { MaxContinuationGapSeconds = 0.25d };
        FootPlantingOutput first = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))) with
        {
            SkeletonOrigin = Vector3.Zero
        });

        // Four ticks elapse at once while the skeleton walks 6 cm: well inside the gap and the leg's reach.
        Vector3 travel = new(0f, 0f, -0.06f);
        FootPlantingOutput resumed = solver.Step(
            Input(true, Foot(new Vector3(0f, 0.1f, 0f) + travel), _legRoot + travel) with
            {
                DeltaSeconds = 4 * Tick,
                SkeletonOrigin = travel
            });
        Assert.False(resumed.ContinuityReset);
        Assert.Equal(FootPlantingPhase.Pinned, resumed.Phase);
        AssertClose(first.Pin, resumed.Pin);
        AssertClose(Planar(first.TargetBall), Planar(resumed.TargetBall));
    }

    [Fact]
    public void LongGap_ResetsThePin_AndPinsAtTheCurrentAnimatedPosition()
    {
        var solver = new FootPlantingSolver { MaxContinuationGapSeconds = 0.25d };
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))));

        // After a 0.5 s outage the skeleton has crept 30 cm (inside reach); the stale pin is not continued.
        Vector3 travel = new(0f, 0f, -0.3f);
        FootPlantingOutput resumed = solver.Step(
            Input(true, Foot(new Vector3(0f, 0.1f, 0f) + travel), _legRoot + travel) with
            {
                DeltaSeconds = 0.5d,
                SkeletonOrigin = travel
            });
        Assert.True(resumed.ContinuityReset);
        Assert.False(resumed.ReachBlendOutStarted);
        Assert.Equal(FootPlantingPhase.Pinned, resumed.Phase);
        AssertClose(resumed.AnimatedBall, resumed.TargetBall);
        AssertClose(Planar(resumed.AnimatedBall), resumed.Pin);

        // A gap just at the threshold still continues.
        FootPlantingOutput next = solver.Step(
            Input(true, Foot(new Vector3(0f, 0.1f, 0.02f) + travel), _legRoot + travel) with
            {
                DeltaSeconds = 0.25d,
                SkeletonOrigin = travel
            });
        Assert.False(next.ContinuityReset);
        AssertClose(resumed.Pin, next.Pin);
    }

    [Fact]
    public void TeleportBeyondLegReach_ResetsThePin_EvenAfterAShortGap()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.2d };
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))) with
        {
            SkeletonOrigin = Vector3.Zero
        });
        _ = solver.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0.04f))) with
        {
            SkeletonOrigin = Vector3.Zero
        });

        // The skeleton jumps 2 m, further than the 0.8 m chain, within two ticks.
        Vector3 teleport = new(2f, 0f, 0f);
        FootPlantingOutput teleported = solver.Step(
            Input(true, Foot(new Vector3(0f, 0.1f, 0f) + teleport), _legRoot + teleport) with
            {
                DeltaSeconds = 2 * Tick,
                SkeletonOrigin = teleport
            });
        Assert.True(teleported.ContinuityReset);
        Assert.False(teleported.ReachBlendOutStarted);
        Assert.Equal(FootPlantingPhase.Pinned, teleported.Phase);
        AssertClose(teleported.AnimatedBall, teleported.TargetBall);

        // Preview detects the same condition without consuming it.
        var previewed = new FootPlantingSolver();
        _ = previewed.Step(Input(true, Foot(new Vector3(0f, 0.1f, 0f))) with
        {
            SkeletonOrigin = Vector3.Zero
        });
        FootPlantingInput jump = Input(true, Foot(new Vector3(0f, 0.1f, 0f) + teleport), _legRoot + teleport) with
        {
            SkeletonOrigin = teleport
        };
        Assert.True(previewed.Preview(jump).ContinuityReset);
        Assert.True(previewed.Step(jump).ContinuityReset);
    }

    [Theory]
    [InlineData(0f, 65f)]
    [InlineData(180f, -179f)]
    [InlineData(-179.8f, 179.8f)]
    public void WorldHeadingHold_RotatesAroundBall_WithoutFreezingTiltOrHeight(float initialYaw, float animatedYaw)
    {
        var solver = new FootPlantingSolver { ReachMarginMetres = 0f };
        Vector3 lever = new(0.03f, -0.04f, -0.12f);
        Vector3 hip = new(0f, 0.85f, 0f);
        Basis acquiredBasis = new(Vector3.Up, Mathf.DegToRad(initialYaw));
        Transform3D acquisition = new(acquiredBasis, new Vector3(0f, 0.1f, 0f));
        FootPlantingInput acquired = new(true, acquisition, lever, hip, 2f, Tick);
        FootPlantingOutput first = solver.Step(acquired);
        Basis tilted = new Basis(Vector3.Up, Mathf.DegToRad(animatedYaw))
            * new Basis(Vector3.Right, Mathf.DegToRad(18f))
            * new Basis(lever.Normalized(), Mathf.DegToRad(12f));
        Transform3D animated = new(tilted, new Vector3(0.02f, 0.13f, 0f));
        FootPlantingInput input = acquired with
        {
            AnimatedFoot = animated
        };
        FootPlantingOutput held = solver.Step(input);
        Assert.Equal(FootPlantingPhase.Pinned, held.Phase);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(first.TargetHeading, held.TargetHeading))), 0f, 0.001f);
        Assert.Equal(first.TargetBall.X, held.TargetBall.X, Tolerance);
        Assert.Equal(first.TargetBall.Z, held.TargetBall.Z, Tolerance);
        Assert.Equal(held.AnimatedBall.Y, held.TargetBall.Y, Tolerance);
        AssertClose(held.TargetBall - (held.Target.Basis * lever), held.Target.Origin);
        AssertClose(held.TargetBall, held.Target * lever);
        Assert.Equal(tilted.Column1.Y, held.Target.Basis.Column1.Y, Tolerance);
        Assert.Equal(tilted.Column0.Y, held.Target.Basis.Column0.Y, Tolerance);
        Assert.Equal(tilted.Column2.Y, held.Target.Basis.Column2.Y, Tolerance);

        FootPlantingOutput preview = solver.Preview(input with
        {
            AnimatedFoot = acquisition
        });
        Assert.Equal(first.TargetHeading, preview.TargetHeading, Tolerance);
        FootPlantingOutput restored = solver.Step(input);
        Assert.Equal(held.TargetHeading, restored.TargetHeading, Tolerance);
        AssertClose(held.TargetBall, restored.TargetBall);

        FootPlantingOutput released = default;
        for (int tick = 0; tick < 20; tick++)
        {
            released = solver.Step(input with
            {
                Planted = false
            });
        }

        Assert.Equal(FootPlantingPhase.Free, released.Phase);
        AssertClose(animated.Origin, released.Target.Origin);
        Assert.True(animated.Basis.IsEqualApprox(released.Target.Basis));
    }

    [Fact]
    public void RotationOnlyAnkleOverreach_ReleasesInsteadOfExtendingOrRecapturing()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.15d };
        Vector3 lever = new(0.1f, 0f, 0f);
        Basis acquiredBasis = new(Vector3.Up, Mathf.Pi);
        Transform3D first = new(acquiredBasis, new Vector3(0.8f, 0f, 0f));
        FootPlantingOutput acquired = solver.Step(new FootPlantingInput(true, first, lever, Vector3.Zero, 0.81f, Tick));
        Assert.Equal(0.7f, acquired.TargetBall.X, Tolerance);
        Assert.Equal(0.8f, acquired.Target.Origin.X, Tolerance);

        Vector3 hip = new(-0.03f, 0f, 0f);
        Transform3D animated = new(Basis.Identity, new Vector3(0.74f, 0f, 0f));
        // Hand geometry: native .77 m; translation-only target .6 - (-.03)=.63 m < .81 m, whereas a
        // world-held yaw of pi about the .7 m pin demands ankle .8 - (-.03)=.83 m > .81 m.
        Assert.Equal(0.77f, animated.Origin.DistanceTo(hip), Tolerance);
        Assert.Equal(0.63f, new Vector3(0.6f, 0f, 0f).DistanceTo(hip), Tolerance);
        Assert.Equal(0.83f, new Vector3(0.8f, 0f, 0f).DistanceTo(hip), Tolerance);
        FootPlantingInput input = new(true, animated, lever, hip, 0.81f, Tick);
        FootPlantingOutput output = solver.Step(input);
        Assert.True(output.ReachBlendOutStarted);
        Assert.InRange(output.Target.Origin.DistanceTo(hip), 0f, 0.81001f);
        Assert.Equal(FootPlantingPhase.BlendingOut, output.Phase);
        int releaseCount = 1;
        for (int i = 0; i < 16; i++)
        {
            // The animated heading and ball move during the unfinished release. A fresh orientation-compensated
            // ankle bound is measured from the CURRENT hip and animated stance, not the previous target position.
            Basis changing = new(Vector3.Up, Mathf.DegToRad(5f * i));
            Transform3D current = new(changing, new Vector3(0.74f + (i * 0.001f), 0f, 0f));
            Vector3 currentHip = hip + new Vector3(i * 0.001f, 0f, 0f);
            FootPlantingInput tickInput = input with
            {
                AnimatedFoot = current,
                LegRoot = currentHip
            };
            output = solver.Step(tickInput);
            releaseCount += output.ReachBlendOutStarted ? 1 : 0;
            float authorisedReach = Mathf.Max(0.81f, current.Origin.DistanceTo(currentHip));
            Assert.InRange(output.Target.Origin.DistanceTo(currentHip), 0f, authorisedReach + Tolerance);
            Assert.True(output.TargetBall.IsEqualApprox(output.Target * lever));
        }

        Assert.Equal(1, releaseCount);
        Assert.Equal(FootPlantingPhase.Free, output.Phase);
        Assert.False(output.ReachBlendOutStarted);
        Assert.True(output.Target.Basis.IsEqualApprox(new Basis(Vector3.Up, Mathf.DegToRad(75f))));
        Assert.Equal(0.755f, output.Target.Origin.X, Tolerance);
    }

    [Fact]
    public void InterruptedYawRelease_CapturesEffectiveHeadingAndBall_WithoutPreviewMutation()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.2d };
        Vector3 lever = new(0f, -0.04f, -0.12f);
        FootPlantingInput initial = new(true, Foot(new Vector3(0f, 0.1f, 0f)), lever, _legRoot, 2f, Tick);
        _ = solver.Step(initial);
        Basis turned = new(Vector3.Up, Mathf.DegToRad(80f));
        FootPlantingInput moved = initial with
        {
            AnimatedFoot = new Transform3D(turned, new Vector3(0.02f, 0.1f, 0.01f))
        };
        _ = solver.Step(moved);
        FootPlantingOutput releasing = solver.Step(moved with
        {
            Planted = false
        });
        FootPlantingOutput anticipated = solver.Preview(moved);
        FootPlantingOutput repinned = solver.Step(moved);
        Assert.Equal(FootPlantingPhase.Pinned, repinned.Phase);
        AssertClose(releasing.TargetBall, repinned.TargetBall);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(releasing.TargetHeading, repinned.TargetHeading))), 0f, 0.001f);
        AssertClose(anticipated.TargetBall, repinned.TargetBall);
        Assert.Equal(anticipated.TargetHeading, repinned.TargetHeading, 0.00001f);
    }

    [Fact]
    public void VerticalAnatomicalAxis_PreservesAnimationAndMetadataPlanting()
    {
        var solver = new FootPlantingSolver();
        Vector3 verticalAxis = new(0f, 0.12f, 0f);
        FootPlantingInput input = new(true, Foot(new Vector3(0f, 0.1f, 0f)), verticalAxis, _legRoot, 2f, Tick);
        _ = solver.Step(input);
        Basis turned = new(Vector3.Up, Mathf.DegToRad(135f));
        FootPlantingOutput held = solver.Step(input with
        {
            AnimatedFoot = new Transform3D(turned, new Vector3(0.01f, 0.1f, 0f))
        });
        Assert.Equal(FootPlantingPhase.Pinned, held.Phase);
        Assert.True(float.IsNaN(held.CapturedHeading));
        Assert.True(float.IsNaN(held.TargetHeading));
        Assert.True(held.Target.Basis.IsEqualApprox(turned));
        Assert.Equal(0f, held.TargetBall.X, Tolerance);
    }

    [Fact]
    public void DegenerateProjection_RecoversOnce_AndInterruptedReleaseDoesNotInventYaw()
    {
        var solver = new FootPlantingSolver { BlendOutSeconds = 0.2d };
        Vector3 forward = new(0f, 0f, -0.12f);
        Vector3 ankle = new(0f, 0.1f, 0f);
        Basis vertical = new(Vector3.Right, Mathf.Pi / 2f);
        FootPlantingInput input = new(true, new Transform3D(vertical, ankle), forward, _legRoot, 2f, Tick);
        FootPlantingOutput acquired = solver.Step(input);
        Assert.Equal(FootPlantingPhase.Pinned, acquired.Phase);
        Assert.True(float.IsNaN(acquired.CapturedHeading));
        Assert.True(acquired.Target.Basis.IsEqualApprox(vertical));

        Basis recovered = new(Vector3.Up, Mathf.DegToRad(65f));
        FootPlantingOutput firstFinite = solver.Step(input with
        {
            AnimatedFoot = new Transform3D(recovered, ankle)
        });
        Assert.Equal(FootPlantingPhase.Pinned, firstFinite.Phase);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(Mathf.DegToRad(65f), firstFinite.TargetHeading))), 0f, 0.001f);
        Assert.True(firstFinite.Target.Basis.IsEqualApprox(recovered));

        Basis turned = new(Vector3.Up, Mathf.DegToRad(-135f));
        FootPlantingOutput held = solver.Step(input with
        {
            AnimatedFoot = new Transform3D(turned, ankle)
        });
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(firstFinite.TargetHeading, held.TargetHeading))), 0f, 0.001f);
        FootPlantingOutput degenerateAgain = solver.Step(input);
        Assert.Equal(FootPlantingPhase.Pinned, degenerateAgain.Phase);
        Assert.True(degenerateAgain.Target.Basis.IsEqualApprox(vertical));
        FootPlantingOutput regained = solver.Step(input with
        {
            AnimatedFoot = new Transform3D(turned, ankle)
        });
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(firstFinite.TargetHeading, regained.TargetHeading))), 0f, 0.001f);

        FootPlantingOutput releasing = solver.Step(input with
        {
            Planted = false
        });
        Assert.Equal(FootPlantingPhase.BlendingOut, releasing.Phase);
        FootPlantingOutput reacquired = solver.Step(input);
        Assert.Equal(FootPlantingPhase.Pinned, reacquired.Phase);
        // A vertical axis remains vertical even under a residual yaw during an interrupted release.
        // Metadata permits capture, but not invention of a horizontal heading.
        Assert.True(float.IsNaN(reacquired.CapturedHeading));
        Assert.True(reacquired.Target.Basis.IsEqualApprox(vertical));
        FootPlantingOutput recoveredAgain = solver.Step(input with
        {
            AnimatedFoot = new Transform3D(recovered, ankle)
        });
        Assert.Equal(FootPlantingPhase.Pinned, recoveredAgain.Phase);
        Assert.InRange(Mathf.Abs(Mathf.RadToDeg(Mathf.AngleDifference(Mathf.DegToRad(65f), recoveredAgain.TargetHeading))), 0f, 0.001f);
    }

    private static FootPlantingInput Input(bool planted, Transform3D foot) => Input(planted, foot, _legRoot);

    private static FootPlantingInput Input(bool planted, Transform3D foot, Vector3 legRoot) =>
        new(planted, foot, _ballInFoot, legRoot, ChainLength, Tick);

    private static Vector3 Planar(Vector3 value) => new(value.X, 0f, value.Z);

    private static Transform3D Foot(Vector3 origin) => new(Basis.Identity, origin);

    private static Vector2 OffsetVector(FootPlantingOutput output) =>
        new(output.TargetBall.X - output.AnimatedBall.X, output.TargetBall.Z - output.AnimatedBall.Z);

    private static void AssertClose(Vector3 expected, Vector3 actual) =>
        Assert.True(expected.DistanceTo(actual) <= Tolerance, $"Expected {expected}, got {actual}.");
}
