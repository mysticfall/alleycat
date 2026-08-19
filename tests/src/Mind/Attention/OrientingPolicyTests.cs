using AlleyCat.Mind.Attention;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>Unit coverage for the Godot-free AI-009 head-orientation decision seam.</summary>
public sealed class OrientingPolicyTests
{
    private const double FrameDelta = 1d / 60d;

    /// <summary>Saturation engages only on the residual that returns the direction just inside the comfort cone.</summary>
    [Fact]
    public void Evaluate_SaturatesToResidualThatReachesJustInsideConeBoundary()
    {
        OrientingPolicy policy = new(GlanceIsolationSettings);
        _ = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(25d)));
        OrientingAim aim = RunFrames(policy, 180, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(25d));

        Assert.Equal(Degrees(10d), aim.HorizontalRadians, 6);
        Assert.Equal(Degrees(15d), Degrees(25d) - aim.HorizontalRadians, 4);
        Assert.True(aim.HorizontalRadians < Degrees(20d));
        Assert.True(aim.Influence > 0.99d);

        // Sign convention: the residual follows the error direction, and the vertical cone is asymmetric.
        OrientingPolicy upward = new(GlanceIsolationSettings);
        _ = upward.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, 0d, Degrees(25d)));
        OrientingAim upwardAim = RunFrames(upward, 180, FrameDelta, OrientingAnchorState.SameAnchor, 0d, Degrees(25d));
        Assert.Equal(Degrees(15d), upwardAim.VerticalRadians, 6);

        OrientingPolicy downward = new(GlanceIsolationSettings);
        _ = downward.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, 0d, Degrees(-25d)));
        OrientingAim downwardAim = RunFrames(downward, 180, FrameDelta, OrientingAnchorState.SameAnchor, 0d, Degrees(-25d));
        Assert.Equal(Degrees(-10d), downwardAim.VerticalRadians, 6);
    }

    /// <summary>A sustained in-cone anchor receives full centring even though the eyes alone could carry it.</summary>
    [Fact]
    public void Evaluate_CentresFullyOnSustainedAnchorWellInsideCone()
    {
        OrientingPolicy policy = new(OrientingSettings.Default);
        _ = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(10d)));
        OrientingAim aim = RunFrames(policy, 30, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(10d));
        Assert.Equal(0d, aim.HorizontalRadians, 10);

        aim = RunFrames(policy, 150, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(10d));
        Assert.Equal(Degrees(10d), aim.HorizontalRadians, 6);
        Assert.True(aim.Influence > 0.99d);
    }

    /// <summary>Centring engages at exactly the centring delay of continuous same-anchor assignment, not before.</summary>
    [Fact]
    public void Evaluate_EngagesCentringAtExactlyTheCentringDelayBoundary()
    {
        // Binary-exact durations keep the boundary assertion free of accumulation rounding.
        OrientingPolicy policy = new(OrientingSettings.Default with
        {
            CentringDelaySeconds = 0.59375d,
            ReactionDelaySeconds = 1e-6d,
        });

        _ = policy.Evaluate(Step(0.5d, OrientingAnchorState.NewAnchor, Degrees(10d)));

        OrientingAim below = policy.Evaluate(Step(0.5d, OrientingAnchorState.SameAnchor, Degrees(10d)));
        Assert.Equal(0d, below.HorizontalRadians, 10);

        below = policy.Evaluate(Step(0.0625d, OrientingAnchorState.SameAnchor, Degrees(10d)));
        Assert.Equal(0d, below.HorizontalRadians, 10);

        below = policy.Evaluate(Step(0.015625d, OrientingAnchorState.SameAnchor, Degrees(10d)));
        Assert.Equal(0d, below.HorizontalRadians, 10);

        OrientingAim boundary = policy.Evaluate(Step(0.015625d, OrientingAnchorState.SameAnchor, Degrees(10d)));
        Assert.Equal(0d, boundary.HorizontalRadians, 10);

        OrientingAim moving = policy.Evaluate(Step(0.5d, OrientingAnchorState.SameAnchor, Degrees(10d)));
        Assert.True(moving.HorizontalRadians > 0d);
    }

    /// <summary>Anchor change and clear both restart the continuous-assignment timer before centring can re-engage.</summary>
    [Fact]
    public void Evaluate_ResetsCentringTimingOnAnchorChangeAndClear()
    {
        OrientingPolicy changePolicy = new(OrientingSettings.Default);
        _ = changePolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(12d)));
        _ = RunFrames(changePolicy, 179, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(12d));

        _ = changePolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(2d)));
        OrientingAim glance = RunFrames(changePolicy, 29, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(2d));
        Assert.Equal(Degrees(12d), glance.HorizontalRadians, 5);

        OrientingAim recentred = RunFrames(changePolicy, 90, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(2d));
        Assert.Equal(Degrees(2d), recentred.HorizontalRadians, 4);

        OrientingPolicy clearPolicy = new(OrientingSettings.Default);
        _ = clearPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(8d)));
        _ = RunFrames(clearPolicy, 179, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(8d));

        _ = clearPolicy.Evaluate(Step(0d, OrientingAnchorState.None, 0d));
        _ = clearPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(3d)));
        OrientingAim reassigned = RunFrames(clearPolicy, 29, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(3d));
        Assert.Equal(Degrees(8d), reassigned.HorizontalRadians, 5);

        OrientingAim resustained = RunFrames(clearPolicy, 90, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(3d));
        Assert.Equal(Degrees(3d), resustained.HorizontalRadians, 4);
    }

    /// <summary>Brief in-cone glances stay eyes-only: the head holds neutral, or the last sustained aim.</summary>
    [Fact]
    public void Evaluate_HoldsHeadAtNeutralOrLastSustainedAimDuringInConeGlance()
    {
        OrientingPolicy freshPolicy = new(GlanceIsolationSettings);
        _ = freshPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(8d)));
        OrientingAim fresh = RunFrames(freshPolicy, 60, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(8d));
        Assert.Equal(0d, fresh.HorizontalRadians, 10);
        Assert.True(fresh.Influence > 0.99d);

        OrientingPolicy heldPolicy = new(OrientingSettings.Default);
        _ = heldPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(12d)));
        _ = RunFrames(heldPolicy, 179, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(12d));

        _ = heldPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(5d)));
        OrientingAim glance = RunFrames(heldPolicy, 30, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(5d));
        Assert.Equal(Degrees(12d), glance.HorizontalRadians, 5);
        Assert.True(Math.Abs(glance.HorizontalRadians - Degrees(5d)) > Degrees(4d));
    }

    /// <summary>A brief out-of-cone glance moves the head only by the cone residual, never toward full centring.</summary>
    [Fact]
    public void Evaluate_MovesOnlyMinimalResidualForBriefOutOfConeGlance()
    {
        OrientingPolicy policy = new(OrientingSettings.Default);
        _ = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(30d)));
        OrientingAim brief = RunFrames(policy, 34, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(30d));
        Assert.InRange(brief.HorizontalRadians, Degrees(10d), Degrees(16d));
        Assert.True(brief.HorizontalRadians < Degrees(20d));

        OrientingPolicy glancePolicy = new(GlanceIsolationSettings);
        _ = glancePolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(30d)));
        OrientingAim converged = RunFrames(glancePolicy, 180, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(30d));
        Assert.Equal(Degrees(15d), converged.HorizontalRadians, 6);
    }

    /// <summary>On glance end the head eases back towards the sustained anchor, or towards neutral after a clear.</summary>
    [Fact]
    public void Evaluate_EasesBackAfterGlanceEndsTowardHeldAimOrNeutral()
    {
        OrientingPolicy clearPolicy = new(OrientingSettings.Default);
        SustainAt(clearPolicy, Degrees(12d), 179);
        _ = clearPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(25d)));
        OrientingAim duringGlance = RunFrames(clearPolicy, 23, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(25d));
        Assert.InRange(duringGlance.HorizontalRadians, Degrees(9.5d), Degrees(11.5d));

        OrientingAim afterClear = RunFrames(clearPolicy, 60, FrameDelta, OrientingAnchorState.None, 0d);
        Assert.True(Math.Abs(afterClear.HorizontalRadians) < Degrees(1d));
        Assert.Equal(0d, afterClear.Influence, 10);

        OrientingPolicy returnPolicy = new(OrientingSettings.Default);
        SustainAt(returnPolicy, Degrees(12d), 179);
        _ = returnPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(25d)));
        _ = RunFrames(returnPolicy, 23, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(25d));
        _ = returnPolicy.Evaluate(Step(0d, OrientingAnchorState.None, 0d));
        _ = returnPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(12d)));
        OrientingAim returned = RunFrames(returnPolicy, 60, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(12d));
        Assert.InRange(returned.HorizontalRadians, Degrees(11.5d), Degrees(12.1d));
        Assert.True(returned.Influence > 0.99d);
    }

    /// <summary>Clearing the anchor eases the head to neutral while the influence ramps down to exactly zero.</summary>
    [Fact]
    public void Evaluate_ReleasesToNeutralWithZeroInfluenceWhenAnchorClears()
    {
        OrientingPolicy policy = new(OrientingSettings.Default);
        SustainAt(policy, Degrees(12d), 179);

        OrientingAim easing = RunFrames(policy, 10, FrameDelta, OrientingAnchorState.None, 0d);
        Assert.InRange(easing.HorizontalRadians, Degrees(1d), Degrees(12d));
        Assert.InRange(easing.Influence, 0d, 0.99d);

        OrientingAim released = RunFrames(policy, 120, FrameDelta, OrientingAnchorState.None, 0d);
        Assert.True(Math.Abs(released.HorizontalRadians) < Degrees(0.1d));
        Assert.True(Math.Abs(released.VerticalRadians) < Degrees(0.1d));
        Assert.Equal(0d, released.Influence, 10);
    }

    /// <summary>Engage and release hysteresis keeps a boundary-hovering target from flapping the head aim.</summary>
    [Fact]
    public void Evaluate_KeepsSaturationStableWithHysteresisAtTheConeBoundary()
    {
        // From below: alternating 14°/16° never reaches the 17° engage threshold, so the head never reacts.
        OrientingPolicy belowPolicy = new(GlanceIsolationSettings);
        _ = belowPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(14d)));
        double belowMaximum = 0d;
        for (int frame = 0; frame < 60; frame++)
        {
            double error = Degrees(frame % 2 == 0 ? 14d : 16d);
            OrientingAim aim = belowPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.SameAnchor, error));
            belowMaximum = Math.Max(belowMaximum, Math.Abs(aim.HorizontalRadians));
        }

        Assert.Equal(0d, belowMaximum, 10);

        // From above: engaged at 20°, then hovering 14°/16° stays latched at the residual instead of flapping
        // back to the held aim of the previously sustained anchor.
        OrientingPolicy abovePolicy = new(GlanceIsolationSettings);
        SustainAt(abovePolicy, Degrees(12d), 719);
        _ = abovePolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(20d)));
        _ = RunFrames(abovePolicy, 24, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(20d));

        double largestFrameStep = 0d;
        double previous = abovePolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.SameAnchor, Degrees(14d))).HorizontalRadians;
        for (int frame = 1; frame < 60; frame++)
        {
            double error = Degrees(frame % 2 == 0 ? 14d : 16d);
            double current = abovePolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.SameAnchor, error)).HorizontalRadians;
            largestFrameStep = Math.Max(largestFrameStep, Math.Abs(current - previous));
            previous = current;
        }

        Assert.True(previous < Degrees(2d), $"Hovering latched aim should settle near the residual, but found '{previous}'.");
        Assert.True(largestFrameStep < Degrees(1.5d), $"Hovering aim should not flap, but a frame moved '{largestFrameStep}'.");

        // Dropping well inside the release threshold frees the latch and the head returns to the held aim.
        OrientingAim released = RunFrames(abovePolicy, 90, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(10d));
        Assert.InRange(released.HorizontalRadians, Degrees(11d), Degrees(13d));
    }

    /// <summary>Sustained aims strain best-effort to the per-axis envelope edge, asymmetric vertically.</summary>
    [Fact]
    public void Evaluate_ClampsSustainedAimToAsymmetricOrientationEnvelope()
    {
        OrientingPolicy upwardPolicy = new(ResponsiveSettings);
        _ = upwardPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(120d), Degrees(90d)));
        OrientingAim upward = RunFrames(
            upwardPolicy,
            240,
            FrameDelta,
            OrientingAnchorState.SameAnchor,
            Degrees(120d),
            Degrees(90d));
        Assert.Equal(Degrees(75d), upward.HorizontalRadians, 6);
        Assert.Equal(Degrees(40d), upward.VerticalRadians, 6);
        Assert.True(upward.Influence > 0.99d);

        OrientingPolicy downwardPolicy = new(ResponsiveSettings);
        _ = downwardPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, 0d, Degrees(-90d)));
        OrientingAim downward = RunFrames(
            downwardPolicy,
            240,
            FrameDelta,
            OrientingAnchorState.SameAnchor,
            0d,
            Degrees(-90d));
        Assert.Equal(0d, downward.HorizontalRadians, 6);
        Assert.Equal(Degrees(-55d), downward.VerticalRadians, 6);
    }

    /// <summary>Newly engaged aims wait out the reaction delay before the head starts moving; influence does not.</summary>
    [Fact]
    public void Evaluate_AppliesReactionDelayBeforeNewlyEngagedAim()
    {
        // Binary-exact steps: 3 × 0.0625 s of frozen frames equal the 0.1875 s reaction delay.
        OrientingPolicy saturationPolicy = new(OrientingSettings.Default with
        {
            ReactionDelaySeconds = 0.1875d
        });
        Assert.Equal(0d, saturationPolicy.Evaluate(Step(0.0625d, OrientingAnchorState.NewAnchor, Degrees(25d))).HorizontalRadians, 10);
        Assert.Equal(0d, saturationPolicy.Evaluate(Step(0.0625d, OrientingAnchorState.SameAnchor, Degrees(25d))).HorizontalRadians, 10);
        OrientingAim frozen = saturationPolicy.Evaluate(Step(0.0625d, OrientingAnchorState.SameAnchor, Degrees(25d)));
        Assert.Equal(0d, frozen.HorizontalRadians, 10);
        Assert.True(frozen.Influence > 0d);
        OrientingAim moving = saturationPolicy.Evaluate(Step(0.0625d, OrientingAnchorState.SameAnchor, Degrees(25d)));
        Assert.True(moving.HorizontalRadians > 0d);

        // The centring engagement at the delay boundary also reacts before the head starts towards full centring.
        OrientingPolicy centringPolicy = new(OrientingSettings.Default);
        _ = centringPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(10d)));
        _ = RunFrames(centringPolicy, 44, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(10d));
        OrientingAim stillFrozen = centringPolicy.Evaluate(Step(FrameDelta, OrientingAnchorState.SameAnchor, Degrees(10d)));
        Assert.Equal(0d, stillFrozen.HorizontalRadians, 10);
        OrientingAim centring = RunFrames(centringPolicy, 3, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(10d));
        Assert.True(centring.HorizontalRadians > 0d);
    }

    /// <summary>Aim steps never exceed the per-axis rate cap, ease below it near the target, and never overshoot.</summary>
    [Fact]
    public void Evaluate_RateLimitsAimTowardTheTarget()
    {
        // The long glance isolation keeps the anchor a saturation glance instead of a sustained centring, and the
        // negligible reaction delay puts the rate cap within reach of the first measured frame.
        OrientingSettings settings = GlanceIsolationSettings with
        {
            ReactionDelaySeconds = 1e-6d
        };
        OrientingPolicy policy = new(settings);
        _ = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(40d)));

        double cap = settings.MaxHorizontalRateRadiansPerSecond * FrameDelta;
        OrientingAim first = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.SameAnchor, Degrees(40d)));
        Assert.Equal(cap, first.HorizontalRadians, 9);
        OrientingAim second = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.SameAnchor, Degrees(40d)));
        Assert.Equal(2d * cap, second.HorizontalRadians, 9);
        Assert.True(second.HorizontalRadians - first.HorizontalRadians <= cap + 1e-12d);

        OrientingAim converged = RunFrames(policy, 240, FrameDelta, OrientingAnchorState.SameAnchor, Degrees(40d));
        Assert.Equal(Degrees(25d), converged.HorizontalRadians, 6);
        Assert.True(converged.HorizontalRadians <= Degrees(25d) + 1e-6d);
    }

    /// <summary>Influence ramps between 0 and 1 at the configured rates without stepping in either direction.</summary>
    [Fact]
    public void Evaluate_RampsInfluenceSmoothlyAndNeverSteps()
    {
        OrientingSettings settings = OrientingSettings.Default;
        OrientingPolicy policy = new(settings);

        double previous = 0d;
        OrientingAim aim = default;
        for (int frame = 1; frame <= 20; frame++)
        {
            OrientingAnchorState anchorState = frame == 1 ? OrientingAnchorState.NewAnchor : OrientingAnchorState.SameAnchor;
            aim = policy.Evaluate(Step(FrameDelta, anchorState, Degrees(25d)));
            Assert.InRange(aim.Influence, previous, previous + (settings.InfluenceEngagePerSecond * FrameDelta) + 1e-12d);
            previous = aim.Influence;
        }

        Assert.Equal(1d, aim.Influence, 10);

        for (int frame = 1; frame <= 25; frame++)
        {
            aim = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.None, 0d));
            Assert.InRange(aim.Influence, previous - (settings.InfluenceReleasePerSecond * FrameDelta) - 1e-12d, previous);
            previous = aim.Influence;
        }

        Assert.Equal(0d, aim.Influence, 10);
    }

    /// <summary>Settings validation passes the defaults and rejects each contract violation with a clear reason.</summary>
    [Fact]
    public void Settings_ValidateAcceptsDefaultsAndRejectsInvalidAuthoring()
    {
        OrientingSettingsValidation pass = OrientingSettings.Default.Validate();
        Assert.True(pass.IsValid);
        Assert.Null(pass.FailureReason);

        AssertRejected(
            OrientingSettings.Default with
            {
                ComfortConeHorizontalRadians = double.NaN
            },
            nameof(OrientingSettings.ComfortConeHorizontalRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                ComfortConeUpRadians = 0d
            },
            nameof(OrientingSettings.ComfortConeUpRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                EnvelopeDownRadians = Degrees(-5d)
            },
            nameof(OrientingSettings.EnvelopeDownRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                EnvelopeHorizontalRadians = OrientingSettings.Default.ComfortConeHorizontalRadians
            },
            "must exceed");
        AssertRejected(
            OrientingSettings.Default with
            {
                EnvelopeUpRadians = Degrees(9d)
            },
            nameof(OrientingSettings.EnvelopeUpRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                CentringDelaySeconds = 0d
            },
            nameof(OrientingSettings.CentringDelaySeconds));
        AssertRejected(
            OrientingSettings.Default with
            {
                ReactionDelaySeconds = double.PositiveInfinity
            },
            nameof(OrientingSettings.ReactionDelaySeconds));
        AssertRejected(
            OrientingSettings.Default with
            {
                MaxVerticalRateRadiansPerSecond = 0d
            },
            nameof(OrientingSettings.MaxVerticalRateRadiansPerSecond));
        AssertRejected(
            OrientingSettings.Default with
            {
                AimSmoothingSeconds = -0.1d
            },
            nameof(OrientingSettings.AimSmoothingSeconds));
        AssertRejected(
            OrientingSettings.Default with
            {
                InfluenceEngagePerSecond = 0d
            },
            nameof(OrientingSettings.InfluenceEngagePerSecond));
        AssertRejected(
            OrientingSettings.Default with
            {
                SaturationReleaseMarginRadians = -0.01d
            },
            nameof(OrientingSettings.SaturationReleaseMarginRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                SaturationEngageMarginRadians = Degrees(6d),
                SaturationReleaseMarginRadians = Degrees(6d),
            },
            "hysteresis margins");
        AssertRejected(
            OrientingSettings.Default with
            {
                ResidualEccentricityHorizontalRadians = -0.01d
            },
            nameof(OrientingSettings.ResidualEccentricityHorizontalRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                ResidualEccentricityHorizontalRadians = OrientingSettings.Default.ComfortConeHorizontalRadians,
            },
            nameof(OrientingSettings.ResidualEccentricityHorizontalRadians));
        AssertRejected(
            OrientingSettings.Default with
            {
                ResidualEccentricityVerticalRadians = Degrees(12d)
            },
            nameof(OrientingSettings.ResidualEccentricityVerticalRadians));

        _ = Assert.Throws<InvalidOperationException>(
            () => new OrientingPolicy(OrientingSettings.Default with { EnvelopeUpRadians = Degrees(5d) }));
    }

    /// <summary>Evaluation inputs fail clearly on non-finite or negative deltas, errors, and unknown anchor states.</summary>
    [Fact]
    public void Evaluate_ValidatesEvaluationInputs()
    {
        OrientingPolicy policy = new(OrientingSettings.Default);

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.Evaluate(Step(double.NaN, OrientingAnchorState.NewAnchor, 0d)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.Evaluate(Step(-0.01d, OrientingAnchorState.NewAnchor, 0d)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, double.PositiveInfinity, 0d)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, 0d, double.NaN)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => policy.Evaluate(new OrientingEvaluation(FrameDelta, (OrientingAnchorState)99, 0d, 0d)));

        Assert.True(policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, Degrees(8d))).Influence > 0d);
    }

    /// <summary>The defaults keep centring above the AI-007 secondary glance dwell with a safe margin.</summary>
    [Fact]
    public void Defaults_KeepCentringDelayAboveTheSecondaryGlanceDwell()
    {
        // AI-007 authors a 0.5 s default secondary dwell (AttentionGazeTargetSelector.SecondaryDwellSeconds);
        // AI-009 requires the centring-delay default to exceed it so brief secondary glances never centre.
        const double secondaryDwellSeconds = 0.5d;

        Assert.True(OrientingSettings.Default.CentringDelaySeconds >= 0.6d);
        Assert.True(OrientingSettings.Default.CentringDelaySeconds > secondaryDwellSeconds);
        Assert.InRange(OrientingSettings.Default.ReactionDelaySeconds, 0.15d, 0.2d);
        Assert.Equal(Degrees(15d), OrientingSettings.Default.ComfortConeHorizontalRadians, 9);
        Assert.Equal(Degrees(10d), OrientingSettings.Default.ComfortConeUpRadians, 9);
        Assert.Equal(Degrees(15d), OrientingSettings.Default.ComfortConeDownRadians, 9);
        Assert.Equal(Degrees(75d), OrientingSettings.Default.EnvelopeHorizontalRadians, 9);
        Assert.Equal(Degrees(40d), OrientingSettings.Default.EnvelopeUpRadians, 9);
        Assert.Equal(Degrees(55d), OrientingSettings.Default.EnvelopeDownRadians, 9);
    }

    /// <summary>Identical evaluation sequences produce bit-identical aim and influence trajectories.</summary>
    [Fact]
    public void Evaluate_IsDeterministicForIdenticalSequences()
    {
        List<OrientingEvaluation> script = BuildMixedScript();

        List<OrientingAim> firstRun = RunScript(new OrientingPolicy(OrientingSettings.Default), script);
        List<OrientingAim> secondRun = RunScript(new OrientingPolicy(OrientingSettings.Default), script);

        Assert.Equal(firstRun.Count, secondRun.Count);
        for (int frame = 0; frame < firstRun.Count; frame++)
        {
            Assert.Equal(firstRun[frame].HorizontalRadians, secondRun[frame].HorizontalRadians);
            Assert.Equal(firstRun[frame].VerticalRadians, secondRun[frame].VerticalRadians);
            Assert.Equal(firstRun[frame].Influence, secondRun[frame].Influence);
        }

        Assert.Contains(firstRun, aim => aim.HorizontalRadians != 0d);
        Assert.Contains(firstRun, aim => aim.VerticalRadians != 0d);
        Assert.Contains(firstRun, aim => aim.Influence >= 1d);
        Assert.Contains(firstRun, aim => aim.Influence <= 0d);
    }

    private static OrientingSettings GlanceIsolationSettings
        => OrientingSettings.Default with
        {
            CentringDelaySeconds = 10d
        };

    private static OrientingSettings ResponsiveSettings
        => OrientingSettings.Default with
        {
            ReactionDelaySeconds = 1e-6d
        };

    private static double Degrees(double degrees)
        => degrees * Math.PI / 180d;

    private static OrientingEvaluation Step(
        double deltaSeconds,
        OrientingAnchorState anchorState,
        double horizontalErrorRadians,
        double verticalErrorRadians = 0d)
        => new(deltaSeconds, anchorState, horizontalErrorRadians, verticalErrorRadians);

    private static OrientingAim RunFrames(
        OrientingPolicy policy,
        int frameCount,
        double deltaSeconds,
        OrientingAnchorState anchorState,
        double horizontalErrorRadians,
        double verticalErrorRadians = 0d)
    {
        OrientingAim aim = default;
        for (int frame = 0; frame < frameCount; frame++)
        {
            aim = policy.Evaluate(Step(deltaSeconds, anchorState, horizontalErrorRadians, verticalErrorRadians));
        }

        return aim;
    }

    private static void SustainAt(OrientingPolicy policy, double horizontalErrorRadians, int sameAnchorFrameCount)
    {
        _ = policy.Evaluate(Step(FrameDelta, OrientingAnchorState.NewAnchor, horizontalErrorRadians));
        _ = RunFrames(policy, sameAnchorFrameCount, FrameDelta, OrientingAnchorState.SameAnchor, horizontalErrorRadians);
    }

    private static List<OrientingAim> RunScript(OrientingPolicy policy, IReadOnlyList<OrientingEvaluation> script)
    {
        List<OrientingAim> aims = new(script.Count);
        foreach (OrientingEvaluation evaluation in script)
        {
            aims.Add(policy.Evaluate(evaluation));
        }

        return aims;
    }

    private static List<OrientingEvaluation> BuildMixedScript()
    {
        List<OrientingEvaluation> script = [];

        // Idle, then an in-cone anchor that becomes sustained.
        AddSpan(script, 5, OrientingAnchorState.None, 0d, 0d, firstAnchorState: OrientingAnchorState.None);
        AddSpan(script, 120, OrientingAnchorState.SameAnchor, Degrees(9d), Degrees(4d), firstAnchorState: OrientingAnchorState.NewAnchor);

        // Brief out-of-cone glance, then a clear.
        AddSpan(script, 30, OrientingAnchorState.SameAnchor, Degrees(-40d), Degrees(-30d), firstAnchorState: OrientingAnchorState.NewAnchor);
        AddSpan(script, 10, OrientingAnchorState.None, 0d, 0d, firstAnchorState: OrientingAnchorState.None);

        // Return to a sustained downward anchor that strains the vertical envelope.
        AddSpan(script, 150, OrientingAnchorState.SameAnchor, Degrees(20d), Degrees(-80d), firstAnchorState: OrientingAnchorState.NewAnchor);

        // Final glance hovering at the cone boundary, then release.
        AddSpan(script, 40, OrientingAnchorState.SameAnchor, Degrees(16d), 0d, firstAnchorState: OrientingAnchorState.NewAnchor);
        AddSpan(script, 30, OrientingAnchorState.None, 0d, 0d, firstAnchorState: OrientingAnchorState.None);

        return script;
    }

    private static void AddSpan(
        List<OrientingEvaluation> script,
        int frameCount,
        OrientingAnchorState anchorState,
        double horizontalErrorRadians,
        double verticalErrorRadians,
        OrientingAnchorState firstAnchorState)
    {
        for (int frame = 0; frame < frameCount; frame++)
        {
            OrientingAnchorState state = frame == 0 ? firstAnchorState : anchorState;
            script.Add(Step(FrameDelta, state, horizontalErrorRadians, verticalErrorRadians));
        }
    }

    private static void AssertRejected(OrientingSettings settings, string expectedFragment)
    {
        OrientingSettingsValidation validation = settings.Validate();
        Assert.False(validation.IsValid);
        Assert.NotNull(validation.FailureReason);
        Assert.Contains(expectedFragment, validation.FailureReason);
    }
}
