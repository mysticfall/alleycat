using AlleyCat.Mind.Attention;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>
/// Geometric-contract coverage for <see cref="OrientingPolicy"/> (AI-009 HEAD-2). These tests replace the
/// scalar-era expectations carried by the unratified frame-subtraction patch: comfort classification and
/// saturation/centring targets are now asserted against actual solved-face-local geometry through the
/// reviewed <see cref="GeometricGazeSaturation"/> contract, never against per-axis
/// <c>measured − commanded</c> subtraction — the recorded head-review counterexample (head pitched −40°,
/// true solved-face-local yaw 14°, scalar reads 18.03° crossing the 17° engage bound) is asserted directly.
/// </summary>
public sealed class OrientingPolicyTests
{
    private const double FrameDelta = 1d / 60d;
    private const double Degrees = Math.PI / 180d;

    private readonly OrientingSettings _settings = OrientingSettings.Default with
    {
        CentringDelaySeconds = 0.2d,
        ReactionDelaySeconds = 0.05d,
    };

    /// <summary>
    /// Glance-regime settings: a long centring delay keeps every frame of the saturation/hysteresis/glance
    /// tests firmly below the sustained threshold, so the observed mode cannot silently climb to centring.
    /// </summary>
    private readonly OrientingSettings _glanceSettings = OrientingSettings.Default with
    {
        CentringDelaySeconds = 5d,
        ReactionDelaySeconds = 0.05d,
    };

    /// <summary>
    /// The scalar-family counterexample: with the solved head pitched −40°, an anchor whose true
    /// solved-face-local yaw is 14° sits inside the comfort cone (engage bound 15° + 2°) and must classify
    /// as a glance (Hold), while the scalar subtraction read 18.03° and engaged saturation.
    /// </summary>
    [Fact]
    public void PitchedHead_InConeGlance_ClassifiesGeometrically()
    {
        OrientingPolicy policy = new(_glanceSettings);
        Basis solvedFace = Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(-40f)).Orthonormalized();
        Vector3 anchorWorld = solvedFace * new Vector3(
            -Mathf.Sin(14f * Mathf.DegToRad(1f)),
            0,
            -Mathf.Cos(14f * Mathf.DegToRad(1f)));
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: anchorWorld, solvedFace: solvedFace);

        _ = Run(policy, feedback, OrientingAnchorState.NewAnchor, 1);
        _ = Run(policy, feedback, OrientingAnchorState.SameAnchor, 30);

        // In-cone glance: the aim keeps easing toward the captured pre-glance command (zero here), never
        // toward the anchor; the scalar patch would have driven it toward the anchor's residual.
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.SameAnchor, 1);
        Assert.True(
            Math.Abs(aim.HorizontalRadians) < Degrees,
            $"An in-cone glance must hold (aim {Mathf.RadToDeg((float)aim.HorizontalRadians):F2}°), "
            + "not chase the anchor the scalar subtraction misclassified as saturated.");
        Assert.True(aim.Influence > 0.5d, "The glance still engages influence.");
    }

    /// <summary>Out-of-cone glances engage saturation geometrically and aim a bounded residual.</summary>
    [Fact]
    public void OutOfConeGlance_SaturatesWithBoundedResidual()
    {
        OrientingPolicy policy = new(_glanceSettings);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));

        _ = Run(policy, feedback, OrientingAnchorState.NewAnchor, 1);
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.SameAnchor, 10);

        Assert.True(aim.Influence > 0.5d, "Saturation engages influence.");
        double aimYawDegrees = aim.HorizontalRadians / Degrees;
        Assert.True(
            aimYawDegrees is > 6d and < 20d,
            $"Saturation must aim a bounded residual toward the anchor (aim {aimYawDegrees:F2}° for a "
            + "30° anchor against a 15° cone), not full centring and not nothing.");
    }

    /// <summary>Per-axis hysteresis: engage beyond cone+margin, persist between thresholds, release inside cone−margin.</summary>
    [Fact]
    public void SaturationHysteresis_PersistsBetweenThresholds()
    {
        OrientingPolicy policy = new(_glanceSettings);

        // 30° engages well beyond the 15°+2° engage bound and the reaction delay elapses.
        GeometricFeedbackSnapshot engaged = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));
        _ = Run(policy, engaged, OrientingAnchorState.NewAnchor, 1);
        _ = Run(policy, engaged, OrientingAnchorState.SameAnchor, 10);
        double assistingAim = Run(policy, engaged, OrientingAnchorState.SameAnchor, 20).HorizontalRadians;
        Assert.True(assistingAim > 3d * Degrees, "The engaged axis must keep assisting.");

        // 14° stays between release (13°) and cone (15°): the latch persists.
        GeometricFeedbackSnapshot between = Feedback(anchorPosition: DirectionFromYawPitch(14d * Degrees, 0));
        double heldAim = Run(policy, between, OrientingAnchorState.SameAnchor, 10).HorizontalRadians;
        Assert.True(
            heldAim > 3d * Degrees,
            "Between the hysteresis thresholds the engaged state must persist, not flap.");

        // 10° releases well inside.
        GeometricFeedbackSnapshot inside = Feedback(anchorPosition: DirectionFromYawPitch(10d * Degrees, 0));
        _ = Run(policy, inside, OrientingAnchorState.SameAnchor, 30);
        double releasedAim = Run(policy, inside, OrientingAnchorState.SameAnchor, 1).HorizontalRadians;
        Assert.True(
            releasedAim < 1d * Degrees,
            $"Deep inside the cone the latch must release and hold (aim {releasedAim / Degrees:F2}°).");
    }

    /// <summary>Sustained centring aims the head at the anchor geometrically, inside the envelope.</summary>
    [Fact]
    public void SustainedAnchor_CentresGeometrically()
    {
        OrientingPolicy policy = new(_settings);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));

        _ = Run(policy, feedback, OrientingAnchorState.NewAnchor, 1);
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.SameAnchor, 90);

        Assert.True(aim.Influence > 0.99d, "Sustained attention reaches full influence.");
        double aimYawDegrees = aim.HorizontalRadians / Degrees;
        Assert.True(
            aimYawDegrees is > 26d and < 34d,
            $"Sustained centring must aim at the anchor (30°), got {aimYawDegrees:F2}°.");
    }

    /// <summary>
    /// Captured-command Hold: a brief in-cone glance after sustained centring keeps easing toward the
    /// captured pre-glance command (character-relative), never toward the glance anchor.
    /// </summary>
    [Fact]
    public void BriefGlance_KeepsCapturedCommand_NeverChasesGlanceAnchor()
    {
        OrientingPolicy policy = new(_settings);
        GeometricFeedbackSnapshot sustained = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));
        _ = Run(policy, sustained, OrientingAnchorState.NewAnchor, 1);
        OrientingAim centred = Run(policy, sustained, OrientingAnchorState.SameAnchor, 90);
        Assert.True(centred.HorizontalRadians / Degrees is > 26d and < 34d, "Premise: centred on the anchor.");

        // A new anchor 5° beyond the centred head: solved-face-locally in-cone, so a glance.
        Vector3 glanceDirection = DirectionFromYawPitch(35d * Degrees, 0);
        GeometricFeedbackSnapshot glance = Feedback(
            anchorPosition: glanceDirection,
            solvedFace: YawBasis(centred.HorizontalRadians));
        OrientingAim held = Run(policy, glance, OrientingAnchorState.NewAnchor, 25);

        double heldYawDegrees = held.HorizontalRadians / Degrees;
        Assert.True(
            heldYawDegrees is > 26d and < 34d,
            $"The glance must keep easing toward the captured 30° command, got {heldYawDegrees:F2}°.");
    }

    /// <summary>UR3/TR7: Saturation has its own active destination, not the older held field or eased output.</summary>
    [Fact]
    public void InterruptedSaturation_TwoInConeReassignments_KeepEasingToActiveDestination()
    {
        // Zero lever and zero pitch: a 30° target against the authored 15° comfort cone has an
        // independent least-change destination of 15°. Advance through the existing reaction wait.
        OrientingPolicy policy = new(_glanceSettings);
        GeometricFeedbackSnapshot saturated = Feedback(
            anchorPosition: DirectionFromYawPitch(30 * Degrees, 0),
            upstreamHeadOrigin: Vector3.Zero, viewpointOffset: Vector3.Zero);
        OrientingAim initial = policy.Evaluate(MakeEvaluation(saturated, OrientingAnchorState.NewAnchor, FrameDelta, 1));
        for (ulong sequence = 2; sequence <= 5; sequence++)
        {
            initial = policy.Evaluate(MakeEvaluation(saturated, OrientingAnchorState.SameAnchor, FrameDelta, sequence));
        }
        Assert.InRange(initial.HorizontalRadians / Degrees, 0.1, 14);

        OrientingAim output = initial;
        for (ulong sequence = 6; sequence <= 25; sequence++)
        {
            // A second new in-cone identity interrupts Hold itself halfway through the same observation.
            double glanceYaw = sequence < 16 ? 5 : 10;
            GeometricFeedbackSnapshot glance = Feedback(
                anchorPosition: DirectionFromYawPitch(glanceYaw * Degrees, 0),
                solvedFace: YawBasis(output.HorizontalRadians),
                upstreamHeadOrigin: Vector3.Zero, viewpointOffset: Vector3.Zero);
            OrientingAim previous = output;
            output = policy.Evaluate(MakeEvaluation(glance,
                sequence is 6 or 16 ? OrientingAnchorState.NewAnchor : OrientingAnchorState.SameAnchor,
                FrameDelta, sequence));
            Assert.True(output.HorizontalRadians > previous.HorizontalRadians,
                "Every unfinished Hold evaluation must continue approaching the pre-glance saturation destination.");
            Assert.InRange(output.HorizontalRadians / Degrees, 0, 15.01);
        }
        Assert.InRange(output.HorizontalRadians / Degrees, 14.5, 15.01);
    }

    /// <summary>Release eases toward the upstream animation pose while influence decays — never a solved-neutral re-anchor.</summary>
    [Fact]
    public void Release_EasesTowardUpstreamPose()
    {
        OrientingPolicy policy = new(_settings);
        Basis upstreamPitched = Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(20f)).Orthonormalized();
        GeometricFeedbackSnapshot feedback = Feedback(upstreamFace: upstreamPitched);

        _ = Run(policy, feedback, OrientingAnchorState.None, 40);
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.None, 1);

        Assert.True(aim.Influence <= 0d, "Influence must decay to zero without an anchor.");
        Assert.True(
            aim.VerticalRadians / Degrees is > 16d and < 24d,
            $"Release must ease toward the 20°-pitched upstream pose, got {aim.VerticalRadians / Degrees:F2}°.");
    }

    /// <summary>Clear followed by a new brief glance never revives the cleared episode's command.</summary>
    [Fact]
    public void Reassignment_AfterClear_NeverRevivesStaleCommand()
    {
        OrientingPolicy policy = new(_settings);
        GeometricFeedbackSnapshot sustained = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));
        _ = Run(policy, sustained, OrientingAnchorState.NewAnchor, 1);
        _ = Run(policy, sustained, OrientingAnchorState.SameAnchor, 90);

        // Clear and let the release decay the command back toward the (identity) upstream pose.
        GeometricFeedbackSnapshot cleared = Feedback();
        _ = Run(policy, cleared, OrientingAnchorState.None, 90);

        // A new brief glance: the captured command must be the post-release current state (~0°), not the
        // cleared episode's 30°.
        GeometricFeedbackSnapshot glance = Feedback(anchorPosition: DirectionFromYawPitch(6d * Degrees, 0));
        OrientingAim held = Run(policy, glance, OrientingAnchorState.NewAnchor, 25);
        double heldYawDegrees = held.HorizontalRadians / Degrees;
        Assert.True(
            heldYawDegrees < 5d,
            $"A post-clear glance must not revive the stale 30° episode command, got {heldYawDegrees:F2}°.");
    }

    /// <summary>Partial influence classifies on the actual solved geometry (mid-transit observation).</summary>
    [Fact]
    public void PartialInfluence_ClassifiesActualSolvedGeometry()
    {
        OrientingPolicy policy = new(_glanceSettings);
        // Solved head mid-transit at 20°; anchor world direction at 40° — face-locally 20°, beyond the
        // 15°+2° engage bound regardless of the aim still ramping: the actual observation classifies.
        GeometricFeedbackSnapshot feedback = Feedback(
            anchorPosition: DirectionFromYawPitch(40d * Degrees, 0),
            solvedFace: YawBasis(20d * Degrees));

        _ = Run(policy, feedback, OrientingAnchorState.NewAnchor, 1);
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.SameAnchor, 7);

        Assert.True(
            aim.Influence is > 0.3d and < 1d,
            "Premise: mid-ramp partial influence at this horizon.");
        Assert.True(
            aim.HorizontalRadians > 3d * Degrees,
            "The out-of-cone classification must engage on the actual solved geometry during partial influence.");
    }

    /// <summary>
    /// Coupled axes: a pitched solved head with a lateral out-of-cone anchor resolves through the geometric
    /// helper — the achieved command's predicted face-local target re-enters the cone, which per-axis scalar
    /// residuals cannot guarantee.
    /// </summary>
    [Fact]
    public void CoupledAxes_SaturationResolvesThroughGeometry()
    {
        OrientingPolicy policy = new(_glanceSettings);
        Basis solvedFace = Basis.Identity.Rotated(Vector3.Right, Mathf.DegToRad(-30f)).Orthonormalized();
        Vector3 anchor = solvedFace * DirectionFromYawPitch(30d * Degrees, 10d * Degrees);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: anchor, solvedFace: solvedFace);

        _ = Run(policy, feedback, OrientingAnchorState.NewAnchor, 1);
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.SameAnchor, 60);

        // Independent verification: compose the achieved command with the upstream geometry and measure the
        // anchor's face-local angles at the predicted eye origin (the reviewed prediction formula, recomputed
        // here from first principles).
        Basis command = GeometricGazeSaturation.ComposeCommandBasis(aim.HorizontalRadians, aim.VerticalRadians);
        Basis upstream = Basis.Identity;
        Vector3 pivot = Vector3.Zero;
        Vector3 lever = new(0f, 0.06f, 0f);
        Vector3 viewpointOffset = new(0f, 0.04f, -0.06f);
        Vector3 predictedEye = pivot + (command * (upstream.Inverse() * lever)) + (command * viewpointOffset);
        Vector3 faceLocal = command.Inverse() * (anchor - predictedEye).Normalized();
        (double yaw, double pitch) = ExtractAngles(faceLocal);

        Assert.True(
            Math.Abs(yaw) <= (15d + 0.5d) * Degrees && Math.Abs(pitch) <= (15d + 0.5d) * Degrees,
            $"The saturation command must bring the anchor back inside the cone geometrically, got face-local "
            + $"({yaw / Degrees:F2}°, {pitch / Degrees:F2}°).");
    }

    /// <summary>A repeated evaluation sequence (zero-delta recomposition) returns the previous aim verbatim and ages nothing.</summary>
    [Fact]
    public void RepeatedEvaluationSequence_DoesNotAgeOrRecompute()
    {
        OrientingPolicy policy = new(_settings);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));

        OrientingAim first = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.NewAnchor, FrameDelta, 100));
        OrientingAim repeated = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor, FrameDelta, 100));
        Assert.Equal(first, repeated);

        // Zero-delta with a fresh sequence also ages nothing: many recompositions cannot elapse the reaction
        // delay or the centring clock.
        for (int i = 0; i < 600; i++)
        {
            _ = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor, 0d, 200 + (ulong)i));
        }

        Assert.Equal(
            first,
            policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor, 0d, 1000)));
    }

    /// <summary>The reaction delay holds the aim still on fresh engagement before it starts moving.</summary>
    [Fact]
    public void ReactionDelay_HoldsFreshEngagement()
    {
        OrientingPolicy policy = new(_settings);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: DirectionFromYawPitch(30d * Degrees, 0));

        OrientingAim early = Run(policy, feedback, OrientingAnchorState.NewAnchor, 2);
        Assert.True(
            Math.Abs(early.HorizontalRadians) < 0.5d * Degrees,
            "The reaction delay must hold the aim before moving.");

        OrientingAim moving = Run(policy, feedback, OrientingAnchorState.SameAnchor, 10);
        Assert.True(
            moving.HorizontalRadians > 1d * Degrees,
            "After the reaction delay the aim must start toward the engaged target.");
    }

    /// <summary>Extreme anchors keep the command inside the authored envelope.</summary>
    [Fact]
    public void ExtremeAnchor_CommandStaysInsideEnvelope()
    {
        OrientingPolicy policy = new(_settings);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: DirectionFromYawPitch(150d * Degrees, 0));

        _ = Run(policy, feedback, OrientingAnchorState.NewAnchor, 1);
        OrientingAim aim = Run(policy, feedback, OrientingAnchorState.SameAnchor, 120);

        Assert.True(
            Math.Abs(aim.HorizontalRadians) <= (75d * Degrees) + 1e-6d,
            $"The command must respect the 75° envelope, got {aim.HorizontalRadians / Degrees:F2}°.");
    }

    /// <summary>AC11: exact eligibility boundary, with assignment and clear resetting the clock.</summary>
    [Fact]
    public void CentringDelay_EngagesAtExactBoundaryAndResetsOnReassignmentOrClear()
    {
        OrientingSettings settings = OrientingSettings.Default;
        OrientingPolicy policy = new(settings);
        GeometricFeedbackSnapshot feedback = Feedback(
            anchorPosition: DirectionFromYawPitch(10 * Degrees, 0),
            upstreamHeadOrigin: Vector3.Zero, viewpointOffset: Vector3.Zero);
        _ = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.NewAnchor, 0, 1));
        double before = Math.BitDecrement(settings.CentringDelaySeconds);
        OrientingAim held = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor, before, 2));
        Assert.Equal(0, held.HorizontalRadians);
        // Eligibility arms the normal reaction delay. Observe movement afterwards with zero additional
        // assignment age beyond the reaction interval, distinguishing >= from > at the exact boundary.
        _ = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor,
            settings.CentringDelaySeconds - before, 3));
        OrientingAim moving = policy.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor,
            settings.ReactionDelaySeconds, 4));
        Assert.True(moving.HorizontalRadians > 0);

        foreach (OrientingAnchorState reset in new[] { OrientingAnchorState.NewAnchor, OrientingAnchorState.None })
        {
            OrientingPolicy fresh = new(settings);
            _ = fresh.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.NewAnchor, 0, 1));
            _ = fresh.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor, before, 2));
            _ = fresh.Evaluate(MakeEvaluation(feedback, reset, 0, 3));
            OrientingAim afterReset = fresh.Evaluate(MakeEvaluation(feedback, OrientingAnchorState.SameAnchor,
                settings.ReactionDelaySeconds, 4));
            Assert.Equal(0, afterReset.HorizontalRadians);
        }
    }

    /// <summary>TR11/15: bounded steps and ramps, with identical geometric inputs reproducing every output.</summary>
    [Fact]
    public void GeometricSequence_RespectsRateAndInfluenceRampsAndIsDeterministic()
    {
        OrientingSettings settings = OrientingSettings.Default;
        OrientingPolicy first = new(settings);
        OrientingPolicy second = new(settings);
        GeometricFeedbackSnapshot feedback = Feedback(anchorPosition: DirectionFromYawPitch(40 * Degrees, -25 * Degrees));
        OrientingAim previous = default;
        bool moved = false;
        for (ulong sequence = 1; sequence <= 180; sequence++)
        {
            OrientingAnchorState state = sequence == 1 ? OrientingAnchorState.NewAnchor
                : sequence <= 100 ? OrientingAnchorState.SameAnchor : OrientingAnchorState.None;
            OrientingEvaluation evaluation = MakeEvaluation(feedback, state, FrameDelta, sequence);
            OrientingAim aim = first.Evaluate(evaluation);
            Assert.Equal(aim, second.Evaluate(evaluation));
            Assert.InRange(Math.Abs(aim.HorizontalRadians - previous.HorizontalRadians), 0,
                (settings.MaxHorizontalRateRadiansPerSecond * FrameDelta) + 1e-12);
            Assert.InRange(Math.Abs(aim.VerticalRadians - previous.VerticalRadians), 0,
                (settings.MaxVerticalRateRadiansPerSecond * FrameDelta) + 1e-12);
            double expectedInfluence = state == OrientingAnchorState.None
                ? Math.Max(0, previous.Influence - (settings.InfluenceReleasePerSecond * FrameDelta))
                : Math.Min(1, previous.Influence + (settings.InfluenceEngagePerSecond * FrameDelta));
            Assert.Equal(expectedInfluence, aim.Influence, 12);
            moved |= aim.HorizontalRadians > 0 && aim.VerticalRadians < 0;
            previous = aim;
        }
        Assert.True(moved);
        Assert.Equal(0, previous.Influence);
    }

    /// <summary>AC14: invalid timing/state inputs are rejected at the pure evaluation boundary.</summary>
    [Fact]
    public void Evaluation_RejectsInvalidDeltaAndAnchorState()
    {
        OrientingPolicy policy = new(OrientingSettings.Default);
        foreach (double delta in new[] { double.NaN, double.PositiveInfinity, -0.01 })
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(
                MakeEvaluation(Feedback(), OrientingAnchorState.NewAnchor, delta, 1)));
        }
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => policy.Evaluate(
            MakeEvaluation(Feedback(), (OrientingAnchorState)99, FrameDelta, 1)));
    }

    /// <summary>AC14: defaults validate and each axis enforces an envelope larger than its comfort cone.</summary>
    [Fact]
    public void Settings_ValidateDefaultsEnvelopeAxesAndRateAuthoring()
    {
        OrientingSettings defaults = OrientingSettings.Default;
        Assert.True(defaults.Validate().IsValid);
        Assert.Null(defaults.Validate().FailureReason);
        foreach (OrientingSettings invalid in new[]
                 {
                     defaults with { EnvelopeHorizontalRadians = defaults.ComfortConeHorizontalRadians },
                     defaults with { EnvelopeUpRadians = defaults.ComfortConeUpRadians },
                     defaults with { EnvelopeDownRadians = defaults.ComfortConeDownRadians },
                     defaults with { MaxHorizontalRateRadiansPerSecond = double.PositiveInfinity },
                     defaults with { MaxVerticalRateRadiansPerSecond = 0 },
                 })
        {
            Assert.False(invalid.Validate().IsValid);
            Assert.False(string.IsNullOrWhiteSpace(invalid.Validate().FailureReason));
            _ = Assert.Throws<InvalidOperationException>(() => new OrientingPolicy(invalid));
        }
    }

    private static GeometricFeedbackSnapshot Feedback(
        Vector3? anchorPosition = null,
        Basis? solvedFace = null,
        Vector3? solvedEyeOrigin = null,
        Basis? upstreamFace = null,
        Vector3? upstreamHeadOrigin = null,
        Vector3? pivotPosition = null,
        Vector3? viewpointOffset = null)
        => new(
            anchorPosition ?? new Vector3(0f, 0f, -1f),
            solvedEyeOrigin ?? Vector3.Zero,
            (solvedFace ?? Basis.Identity).Orthonormalized(),
            upstreamHeadOrigin ?? new Vector3(0f, 0.06f, 0f),
            pivotPosition ?? Vector3.Zero,
            (upstreamFace ?? Basis.Identity).Orthonormalized(),
            viewpointOffset ?? new Vector3(0f, 0.04f, -0.06f));

    private static OrientingEvaluation MakeEvaluation(
        GeometricFeedbackSnapshot feedback,
        OrientingAnchorState anchorState,
        double deltaSeconds,
        ulong sequence)
        => new(deltaSeconds, anchorState, sequence, feedback);

    private static OrientingAim Run(
        OrientingPolicy policy,
        GeometricFeedbackSnapshot feedback,
        OrientingAnchorState anchorState,
        int frames,
        ulong sequence = 0)
    {
        OrientingAim aim = default;
        for (int frame = 0; frame < frames; frame++)
        {
            ulong evaluationSequence = sequence == 0 ? (ulong)(100 + frame) : sequence;
            aim = policy.Evaluate(MakeEvaluation(feedback, anchorState, FrameDelta, evaluationSequence));
        }

        return aim;
    }

    private static Basis YawBasis(double yawRadians)
        => Basis.Identity.Rotated(Vector3.Up, (float)yawRadians).Orthonormalized();

    private static Vector3 DirectionFromYawPitch(double yawRadians, double pitchRadians)
    {
        double cosPitch = Math.Cos(pitchRadians);
        return new(
            (float)(-cosPitch * Math.Sin(yawRadians)),
            (float)Math.Sin(pitchRadians),
            (float)(-cosPitch * Math.Cos(yawRadians)));
    }

    private static (double Yaw, double Pitch) ExtractAngles(Vector3 direction)
    {
        double horizontal = Math.Sqrt((direction.X * direction.X) + (direction.Z * direction.Z));
        if (horizontal < 1e-9d)
        {
            return (0d, direction.Y >= 0d ? Math.PI / 2d : -Math.PI / 2d);
        }

        return (Math.Atan2(-direction.X, -direction.Z), Math.Atan2(direction.Y, horizontal));
    }
}
