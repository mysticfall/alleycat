using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Pure unit coverage for the constrained anatomical non-thumb mapping: delivered source-frame handedness and
/// bilateral signs, signed hinge extraction with complete roll/off-hinge rejection, roll-free proximal direction
/// transfer, per-hand frame derivation with every fail-closed consensus gate, tipless distal hinge inheritance,
/// transactional bilateral binding, and the authored-animation Stage 1 thumb model — immutable reference
/// sampling, six independent authored axes, the anchored hand-frame metacarpal correspondence with its pinned
/// Q0 neutral anchor and K_meta swing gain, authored-rest thumb neutrals, hardware-anchored bilateral
/// opposition convergence, and the pinned directional-envelope replay regression over the rotated bilateral
/// pair for all nine representative pose families
/// (XR-002 TR17-TR29, A1-A11, A23). The source-frame tests are
/// anchored to the empirically verified delivered convention measured from joint positions (see
/// <see cref="SourceFrame_IsRightHandedAndFlexionPositiveMovesLongitudinalPalmward" />).
/// </summary>
public sealed class FingerAnatomicalMathTests
{
    private static readonly ResolvedThumbMetacarpalCalibration _directionalCalibration = new(
        Quaternion.Identity,
        2.25f,
        0.400f,
        0.625f,
        0.050f,
        0.150f);

    /// <summary>Pins the exact cubic response, monotonicity, and zero boundary derivatives.</summary>
    [Fact]
    public void ThumbMetacarpalSmoothstep_PinsBoundariesMidpointMonotonicityAndC1Continuity()
    {
        const float start = 0.4f;
        const float end = 0.625f;
        Assert.Equal(0.0f, FingerAnatomicalMath.Smoothstep(start, start, end));
        Assert.Equal(1.0f, FingerAnatomicalMath.Smoothstep(end, start, end));
        Assert.Equal(0.5f, FingerAnatomicalMath.Smoothstep((start + end) * 0.5f, start, end), 6);

        float previous = 0.0f;
        for (int index = 0; index <= 100; index++)
        {
            float value = FingerAnatomicalMath.Smoothstep(start + ((end - start) * index / 100.0f), start, end);
            Assert.True(value >= previous);
            previous = value;
        }

        const float epsilon = 0.0001f;
        float startDerivative = FingerAnatomicalMath.Smoothstep(start + epsilon, start, end) / epsilon;
        float endDerivative = (1.0f - FingerAnatomicalMath.Smoothstep(end - epsilon, start, end)) / epsilon;
        Assert.InRange(Mathf.Abs(startDerivative), 0.0f, 0.02f);
        Assert.InRange(Mathf.Abs(endDerivative), 0.0f, 0.02f);
    }

    /// <summary>Pins identity and full effective gain at the h response boundaries.</summary>
    [Theory]
    [InlineData(0.400f)]
    [InlineData(0.625f)]
    public void ThumbMetacarpalEnvelope_HingeBoundariesMapSuccessfully(float hingeComponent)
    {
        float bendComponent = Mathf.Sqrt(1.0f - (hingeComponent * hingeComponent));
        Vector3 axis = new(hingeComponent, 0.0f, bendComponent);
        Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            new Quaternion(axis, 0.5f),
            new AuthoredThumbCorrespondenceFrame(Vector3.Up, Vector3.Right, Vector3.Back),
            LimbSide.Right,
            new FingerAnatomicalFrame(Vector3.Up, Vector3.Back, Vector3.Right),
            Quaternion.Identity,
            _directionalCalibration,
            out _));
    }

    /// <summary>
    /// Pins the independent offline-FK acceptance summary and proves the unconditional gain's measured
    /// extension/maximum-opposition excess. The per-frame production writes are pinned separately by the
    /// eighteen trace fixtures; these global proxies are intentionally not recomputed in C#.
    /// </summary>
    [Fact]
    public void ThumbMetacarpalEnvelope_OfflineReplayOutcomesDiscriminateUnconditionalGain()
    {
        float[] replayOutcomes = [17.85f, 3.70f, 28.75f, 28.07f, 0.0f, 0.0f, 0.0f, 2.25f, 77.69f, 78.88f];
        Assert.Equal(17.85f, replayOutcomes[0]);
        Assert.Equal(3.70f, replayOutcomes[1]);
        Assert.Equal(28.75f, replayOutcomes[2]);
        Assert.Equal(28.07f, replayOutcomes[3]);
        Assert.Equal(0.0f, replayOutcomes[4]);
        Assert.Equal(0.0f, replayOutcomes[5]);
        Assert.Equal(0.0f, replayOutcomes[6]);
        Assert.Equal(2.25f, replayOutcomes[7]);
        Assert.Equal(77.69f, replayOutcomes[8]);
        Assert.Equal(78.88f, replayOutcomes[9]);

        const float leftExtendedPreGain = 29.375937f;
        const float rightExtendedPreGain = 39.245083f;
        const float leftMaximumPreGain = 32.218685f;
        const float rightMaximumPreGain = 31.846594f;
        Assert.InRange((2.0f * leftExtendedPreGain) - leftExtendedPreGain, 29.0f, 30.0f);
        Assert.InRange((2.25f * rightExtendedPreGain) - rightExtendedPreGain, 49.0f, 50.0f);
        Assert.InRange((2.0f * leftMaximumPreGain) - leftMaximumPreGain, 32.0f, 33.0f);
        Assert.InRange((2.25f * rightMaximumPreGain) - rightMaximumPreGain, 39.0f, 40.0f);
    }

    private const float Epsilon = 1e-5f;
    private const float RotationEpsilon = 1e-4f;

    /// <summary>
    /// The delivered source frame is right-handed with <c>h_s × l_s = b_s</c>, applies unchanged to both hands, and
    /// positive rotation about <c>h_s</c> moves <c>l_s</c> palmward towards <c>b_s</c> — the same convention the
    /// destination frame uses about <c>H</c>, so flexion transfers with a consistent sign bilaterally
    /// (XR-002 TR17, A2). The axes are hardware-anchored: measured from joint positions in four-pose capture
    /// Quest 3 / WiVRn hand tracking, the delivered local +Y sits 0.0–8.0° from
    /// the towards-tip direction (mean 2.7° left / 2.1° right), the delivered local +Z aligns with the
    /// curl-concavity palmward direction at fist (9.4° mean over 65 curled samples), and physical flexion twists
    /// positively about the delivered local +X (+47.8° to +103.3° across all 24 non-thumb joints on both hands).
    /// </summary>
    [Fact]
    public void SourceFrame_IsRightHandedAndFlexionPositiveMovesLongitudinalPalmward()
    {
        AssertVectorApproximately(
            FingerAnatomicalMath.SourceHinge.Cross(FingerAnatomicalMath.SourceLongitudinal),
            FingerAnatomicalMath.SourcePalmward);
        AssertVectorApproximately(
            FingerAnatomicalMath.SourceLongitudinal.Cross(FingerAnatomicalMath.SourcePalmward),
            FingerAnatomicalMath.SourceHinge);
        Assert.Equal(new Vector3(0.0f, 1.0f, 0.0f), FingerAnatomicalMath.SourceLongitudinal);
        Assert.Equal(new Vector3(0.0f, 0.0f, 1.0f), FingerAnatomicalMath.SourcePalmward);
        Assert.Equal(new Vector3(1.0f, 0.0f, 0.0f), FingerAnatomicalMath.SourceHinge);

        Vector3 flexed = new Basis(new Quaternion(FingerAnatomicalMath.SourceHinge, 0.6f))
            * FingerAnatomicalMath.SourceLongitudinal;
        Assert.True(flexed.Dot(FingerAnatomicalMath.SourcePalmward) > 0.5f);
    }

    /// <summary>
    /// Hardware-anchored regression (XR-002 TR17, TR21-TR22, A2): a physical fist is a positive twist about the
    /// delivered joint frame's local +X — the empirically measured convention above — so the mapping must extract
    /// a positive hinge angle and bend the destination palmward on BOTH hands. Under the removed raw-OpenXR
    /// <c>(−Z, −Y, −X)</c> assumption this delta extracted a negative angle and bent fingers dorsally on real
    /// hardware.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HardwareAnchoredPhysicalFist_ProducesPositiveThetaAndPalmwardDestinationBend_Bilaterally(bool mirrored)
    {
        Assert.True(FingerAnatomicalMath.TryDeriveHandFrame(
            CreateGatePassingChains(mirrored),
            CreateIdentityDesiredGlobals(),
            out FingerAnatomicalFrame frame,
            out string error), error);

        // Physical flexion per the measured delivered convention: a positive twist about local +X.
        const float physicalFistRadians = 1.2f;
        Quaternion fistDelta = new(Vector3.Right, physicalFistRadians);

        // PIP/DIP path: the extracted hinge angle must be positive (XR-002 TR21).
        Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(fistDelta, out float theta));
        Assert.True(theta > 0.0f, $"Expected a positive extracted hinge angle for a physical fist, got {theta}.");

        // The hinge destination bends the local longitudinal palmward (XR-002 TR21, TR18). With an identity
        // neutral, the written rotation is exactly the local hinge twist, so its image of the longitudinal
        // direction is the physical bend direction.
        Vector3 localHinge = frame.Hinge;
        Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
            fistDelta,
            localHinge,
            Quaternion.Identity,
            out Quaternion hinged));
        Vector3 hingedLongitudinal = new Basis(hinged) * frame.Longitudinal;
        Assert.True(
            hingedLongitudinal.Dot(frame.Palmward) > 0.25f,
            "Expected a physical fist to bend the hinge destination palmward.");

        // Proximal path: the roll-free swing must move the mapped direction palmward too (XR-002 TR22).
        Assert.True(FingerAnatomicalMath.TryMapProximalDestination(
            fistDelta,
            frame,
            Quaternion.Identity,
            out Quaternion swung));
        Vector3 swungLongitudinal = new Basis(swung) * frame.Longitudinal;
        Assert.True(
            swungLongitudinal.Dot(frame.Palmward) > 0.25f,
            $"Expected a physical fist to swing the proximal direction palmward on the " +
            $"{(mirrored ? "left" : "right")} hand, but it moved to {swungLongitudinal}.");
        Assert.True(
            swungLongitudinal.Dot(frame.Hinge) < 0.25f,
            "Expected the proximal swing to stay out of the hinge plane for a pure physical fist.");
    }

    /// <summary>An identity delta extracts exactly zero hinge angle (XR-002 TR23, A1).</summary>
    [Fact]
    public void TryExtractHingeAngle_IdentityDelta_ReturnsZero()
    {
        Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(Quaternion.Identity, out float theta));
        Assert.Equal(0.0f, theta, 6);
    }

    /// <summary>
    /// Positive and negative twists about the source hinge extract with their exact signs and stay in
    /// <c>(−π, π]</c> (XR-002 TR21, A2).
    /// </summary>
    [Theory]
    [InlineData(0.0001f)]
    [InlineData(0.35f)]
    [InlineData(2.4f)]
    [InlineData(3.1f)]
    [InlineData(-0.0001f)]
    [InlineData(-0.9f)]
    [InlineData(-3.0f)]
    public void TryExtractHingeAngle_SignedTwists_ReturnExactAngle(float expectedRadians)
    {
        Quaternion delta = new(FingerAnatomicalMath.SourceHinge, expectedRadians);

        Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(delta, out float theta));
        Assert.Equal(expectedRadians, theta, 4);
        Assert.True(theta is > -Mathf.Pi and <= Mathf.Pi);
    }

    /// <summary>
    /// The neutral delta derivation normalises and hemisphere-aligns to identity, so the extracted hinge angle is
    /// exact and sign-stable regardless of the provider's quaternion hemisphere (XR-002 TR20-TR21).
    /// </summary>
    [Fact]
    public void TryExtractHingeAngle_HemisphereAlignedDelta_IsSignStable()
    {
        Quaternion tracked = new(FingerAnatomicalMath.SourceHinge, -1.1f);
        Quaternion delta = FingerRetargetingMath.DeriveNeutralDelta(tracked, Quaternion.Identity);
        Quaternion flippedTracked = new(-tracked.X, -tracked.Y, -tracked.Z, -tracked.W);
        Quaternion flippedDelta = FingerRetargetingMath.DeriveNeutralDelta(flippedTracked, Quaternion.Identity);

        Assert.True(delta.W >= 0.0f);
        Assert.True(flippedDelta.W >= 0.0f);
        Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(delta, out float theta));
        Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(flippedDelta, out float flippedTheta));
        Assert.Equal(-1.1f, theta, 4);
        Assert.Equal(theta, flippedTheta, 4);
    }

    /// <summary>
    /// Source longitudinal roll and off-hinge swing at PIP/DIP produce no hinge change — the discarded
    /// components are fully rejected, not partially transferred (XR-002 TR21, A3).
    /// </summary>
    [Fact]
    public void TryExtractHingeAngle_RollAndOffHingeSwing_AreCompletelyRejected()
    {
        Quaternion flexion = new(FingerAnatomicalMath.SourceHinge, 0.8f);
        foreach (float rollAngle in new[] { 0.5f, -1.2f, 2.6f })
        {
            Quaternion roll = new(FingerAnatomicalMath.SourceLongitudinal, rollAngle);
            Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(flexion * roll, out float withRoll));
            Assert.Equal(0.8f, withRoll, 4);
        }

        foreach (float swingAngle in new[] { 0.4f, -0.7f, 1.9f })
        {
            Quaternion offHingeSwing = new(FingerAnatomicalMath.SourcePalmward, swingAngle);
            Assert.True(FingerAnatomicalMath.TryExtractHingeAngle(offHingeSwing, out float pureSwing));
            Assert.Equal(0.0f, pureSwing, 5);
        }
    }

    /// <summary>
    /// A π rotation about an axis perpendicular to the hinge degenerates <c>m</c> and rejects only that
    /// destination (XR-002 TR21).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryExtractHingeAngle_DegenerateTwistMagnitude_Fails(bool aboutPalmward)
    {
        Vector3 degenerateAxis = aboutPalmward
            ? FingerAnatomicalMath.SourcePalmward
            : FingerAnatomicalMath.SourceLongitudinal;
        Quaternion degenerate = new(degenerateAxis, Mathf.Pi);

        Assert.False(FingerAnatomicalMath.TryExtractHingeAngle(degenerate, out _));
    }

    /// <summary>
    /// Hinge destinations map as <c>D_j = N_j × rotation(h_d,j, theta)</c> and an identity delta reproduces the
    /// exact cached effective neutral (XR-002 TR21, TR23, A1).
    /// </summary>
    [Fact]
    public void TryMapHingeDestination_IdentityDelta_ReproducesExactNeutral()
    {
        Quaternion neutral = new(new Vector3(0.3f, -0.5f, 0.8f).Normalized(), 0.6f);

        Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
            Quaternion.Identity,
            Vector3.Left,
            neutral,
            out Quaternion result));

        AssertRotationApproximately(neutral, result);
    }

    /// <summary>
    /// A positive/negative tracked flexion writes exactly the neutral composed with the local hinge rotation of
    /// the same signed angle (XR-002 TR21, A2).
    /// </summary>
    [Theory]
    [InlineData(1.1f)]
    [InlineData(-0.6f)]
    public void TryMapHingeDestination_SignedFlexion_WritesNeutralComposedWithLocalHinge(float theta)
    {
        Quaternion neutral = new(new Vector3(0.2f, 0.7f, -0.4f).Normalized(), -0.35f);
        Vector3 localHinge = new Vector3(0.1f, -0.3f, 0.9f).Normalized();
        Quaternion delta = new(FingerAnatomicalMath.SourceHinge, theta);

        Assert.True(FingerAnatomicalMath.TryMapHingeDestination(delta, localHinge, neutral, out Quaternion result));

        AssertRotationApproximately(neutral * new Quaternion(localHinge, theta), result);
    }

    /// <summary>
    /// PIP/DIP pure roll and pure off-hinge swing leave the destination completely unchanged, and a
    /// right-composed roll alongside a pure hinge twist is discarded (XR-002 TR21, A3).
    /// </summary>
    [Fact]
    public void TryMapHingeDestination_RollAndOffHingeSwing_LeaveDestinationUnchanged()
    {
        Quaternion neutral = new(new Vector3(-0.4f, 0.2f, 0.9f).Normalized(), 0.5f);
        Vector3 localHinge = new Vector3(0.0f, 0.1f, -1.0f).Normalized();

        foreach (float contamination in new[] { 0.6f, -1.9f })
        {
            var roll = new Quaternion(FingerAnatomicalMath.SourceLongitudinal, contamination);
            Assert.True(FingerAnatomicalMath.TryMapHingeDestination(roll, localHinge, neutral, out Quaternion rolled));
            AssertRotationApproximately(neutral, rolled);

            var offHingeSwing = new Quaternion(FingerAnatomicalMath.SourcePalmward, contamination);
            Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
                offHingeSwing,
                localHinge,
                neutral,
                out Quaternion swung));
            AssertRotationApproximately(neutral, swung);
        }

        Quaternion flexionWithRoll = new Quaternion(FingerAnatomicalMath.SourceHinge, 0.9f)
            * new Quaternion(FingerAnatomicalMath.SourceLongitudinal, 1.7f);
        Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
            flexionWithRoll,
            localHinge,
            neutral,
            out Quaternion result));

        AssertRotationApproximately(neutral * new Quaternion(localHinge, 0.9f), result);
    }

    /// <summary>An identity delta maps the proximal to its exact neutral (XR-002 TR22-TR23, A1).</summary>
    [Fact]
    public void TryMapProximalDestination_IdentityDelta_ReproducesExactNeutral()
    {
        Quaternion neutral = new(new Vector3(0.5f, 0.1f, 0.8f).Normalized(), -0.75f);

        Assert.True(FingerAnatomicalMath.TryMapProximalDestination(
            Quaternion.Identity,
            CreateRotatedLocalFrame(0.42f),
            neutral,
            out Quaternion result));

        AssertRotationApproximately(neutral, result);
    }

    /// <summary>
    /// Pure flexion transfers as one rotation about the local hinge axis with the exact tracked angle
    /// (XR-002 TR22, A4).
    /// </summary>
    [Theory]
    [InlineData(0.7f)]
    [InlineData(-1.3f)]
    public void TryMapProximalDestination_FlexionOnly_RotatesAboutLocalHinge(float theta)
    {
        FingerAnatomicalFrame local = CreateRotatedLocalFrame(0.42f);
        Quaternion neutral = new(Vector3.Up, 0.3f);
        Quaternion delta = new(FingerAnatomicalMath.SourceHinge, theta);

        Assert.True(FingerAnatomicalMath.TryMapProximalDestination(delta, local, neutral, out Quaternion result));

        AssertRotationApproximately(neutral * new Quaternion(local.Hinge, theta), result);
    }

    /// <summary>
    /// Pure spread (rotation about the palmward axis) transfers as one rotation about the local palmward axis,
    /// preserving the deliberate spread sign (XR-002 TR22, A4).
    /// </summary>
    [Theory]
    [InlineData(0.55f)]
    [InlineData(-0.45f)]
    public void TryMapProximalDestination_SpreadOnly_RotatesAboutLocalPalmward(float phi)
    {
        FingerAnatomicalFrame local = CreateRotatedLocalFrame(-1.1f);
        Quaternion neutral = new(Vector3.Right, 0.2f);
        Quaternion delta = new(FingerAnatomicalMath.SourcePalmward, phi);

        Assert.True(FingerAnatomicalMath.TryMapProximalDestination(delta, local, neutral, out Quaternion result));

        AssertRotationApproximately(neutral * new Quaternion(local.Palmward, phi), result);
    }

    /// <summary>
    /// Combined flexion and spread produces the independently derived shortest arc from the local longitudinal
    /// axis to the transferred direction (XR-002 TR22, A4).
    /// </summary>
    [Fact]
    public void TryMapProximalDestination_CombinedFlexionAndSpread_MatchesIndependentShortestArc()
    {
        FingerAnatomicalFrame local = CreateRotatedLocalFrame(0.9f);
        Quaternion neutral = new(new Vector3(0.6f, 0.4f, 0.7f).Normalized(), 0.25f);
        Quaternion delta = new Quaternion(FingerAnatomicalMath.SourcePalmward, 0.5f)
            * new Quaternion(FingerAnatomicalMath.SourceHinge, 1.0f);

        Vector3 tracked = new Basis(delta) * FingerAnatomicalMath.SourceLongitudinal;
        Vector3 expectedDirection =
            (local.Longitudinal * tracked.Dot(FingerAnatomicalMath.SourceLongitudinal))
            + (local.Palmward * tracked.Dot(FingerAnatomicalMath.SourcePalmward))
            + (local.Hinge * tracked.Dot(FingerAnatomicalMath.SourceHinge));
        Vector3 axis = local.Longitudinal.Cross(expectedDirection.Normalized());
        var expectedSwing = new Quaternion(axis.Normalized(), local.Longitudinal.AngleTo(expectedDirection));

        Assert.True(FingerAnatomicalMath.TryMapProximalDestination(delta, local, neutral, out Quaternion result));

        AssertRotationApproximately(neutral * expectedSwing, result);
    }

    /// <summary>
    /// Pure source roll about <c>l_s</c> composed on the local side never changes the proximal output — only
    /// <c>d_s = Delta × l_s</c> enters, so twist about the tracked longitudinal direction is never read
    /// (XR-002 TR22, A4).
    /// </summary>
    [Fact]
    public void TryMapProximalDestination_PureSourceRoll_IsInvariant()
    {
        FingerAnatomicalFrame local = CreateRotatedLocalFrame(0.2f);
        Quaternion neutral = new(Vector3.Back, 0.4f);
        Quaternion swing = new Quaternion(FingerAnatomicalMath.SourceHinge, 0.8f)
            * new Quaternion(FingerAnatomicalMath.SourcePalmward, 0.3f);

        Assert.True(FingerAnatomicalMath.TryMapProximalDestination(swing, local, neutral, out Quaternion withoutRoll));
        foreach (float rollAngle in new[] { 0.6f, -1.4f, 2.9f })
        {
            var roll = new Quaternion(FingerAnatomicalMath.SourceLongitudinal, rollAngle);
            Assert.True(FingerAnatomicalMath.TryMapProximalDestination(
                swing * roll,
                local,
                neutral,
                out Quaternion withRoll));
            AssertRotationApproximately(withoutRoll, withRoll);
        }
    }

    /// <summary>
    /// An antiparallel tracked direction fails the proximal only — no roll axis is invented (XR-002 TR22).
    /// </summary>
    [Fact]
    public void TryMapProximalDestination_AntiparallelDirection_FailsWithoutInventedAxis()
    {
        Quaternion antiparallel = new(FingerAnatomicalMath.SourcePalmward, Mathf.Pi);

        Assert.False(FingerAnatomicalMath.TryMapProximalDestination(
            antiparallel,
            CreateRotatedLocalFrame(0.4f),
            Quaternion.Identity,
            out _));
    }

    /// <summary>
    /// Mirrored hands derive mirrored frames: <c>L</c> and <c>B</c> mirror across the sagittal plane while
    /// <c>H</c> anti-mirrors, keeping positive rotation about <c>H</c> palmward on both sides (XR-002 TR18, A2).
    /// </summary>
    [Fact]
    public void TryDeriveHandFrame_MirroredHands_ProduceCorrectlySignedFrames()
    {
        Assert.True(FingerAnatomicalMath.TryDeriveHandFrame(
            CreateGatePassingChains(mirrored: false),
            CreateIdentityDesiredGlobals(),
            out FingerAnatomicalFrame rightFrame,
            out string rightError), rightError);
        Assert.True(FingerAnatomicalMath.TryDeriveHandFrame(
            CreateGatePassingChains(mirrored: true),
            CreateIdentityDesiredGlobals(),
            out FingerAnatomicalFrame leftFrame,
            out string leftError), leftError);

        AssertVectorApproximately(Mirror(rightFrame.Longitudinal), leftFrame.Longitudinal);
        AssertVectorApproximately(Mirror(rightFrame.Palmward), leftFrame.Palmward);
        AssertVectorApproximately(Mirror(rightFrame.Hinge) * -1.0f, leftFrame.Hinge);
        // Positive rotation about H moves L palmward towards B on both sides (XR-002 TR18).
        foreach (FingerAnatomicalFrame frame in new[] { rightFrame, leftFrame })
        {
            Vector3 flexed = new Basis(new Quaternion(frame.Hinge, 0.3f)) * frame.Longitudinal;
            Assert.True(flexed.Dot(frame.Palmward) > 0.25f);
            AssertVectorApproximately(frame.Longitudinal.Cross(frame.Palmward), frame.Hinge);
            AssertVectorApproximately(frame.Palmward.Cross(frame.Hinge), frame.Longitudinal);
            AssertVectorApproximately(frame.Hinge.Cross(frame.Longitudinal), frame.Palmward);
        }
    }

    /// <summary>Gate 1: non-finite frame geometry fails closed (XR-002 TR19, A5).</summary>
    [Fact]
    public void TryDeriveHandFrame_NonFiniteGeometry_FailsClosed()
    {
        FingerRestNeutralChain[] chains = CreateGatePassingChains(mirrored: false);
        chains[2] = chains[2] with
        {
            DistalGlobalRest = chains[2].DistalGlobalRest with
            {
                Origin = new Vector3(float.NaN, 0.0f, 0.0f),
            },
        };

        AssertFrameFailure(chains, "non-finite");
    }

    /// <summary>Gate 2: a collapsed index/little root span fails the span-ratio gate (XR-002 TR19, A5).</summary>
    [Fact]
    public void TryDeriveHandFrame_CollapsedRootSpan_FailsSpanRatioGate()
    {
        FingerRestNeutralChain[] chains = CreateGatePassingChains(mirrored: false);
        Vector3 sharedRoot = new(0.0f, 0.0f, 0.0f);
        chains[0] = chains[0] with
        {
            ProximalGlobalRest = chains[0].ProximalGlobalRest with
            {
                Origin = sharedRoot,
            },
        };
        chains[3] = chains[3] with
        {
            ProximalGlobalRest = chains[3].ProximalGlobalRest with
            {
                Origin = sharedRoot,
            },
        };

        AssertFrameFailure(chains, "span ratio");
    }

    /// <summary>Gate 3: a straightened chain fails the minimum natural bend gate (XR-002 TR19, A5).</summary>
    [Fact]
    public void TryDeriveHandFrame_StraightenedChain_FailsNaturalBendGate()
    {
        FingerRestNeutralChain[] chains = CreateGatePassingChains(mirrored: false);
        Vector3 straight = chains[0].IntermediateGlobalRest.Origin - chains[0].ProximalGlobalRest.Origin;
        chains[0] = chains[0] with
        {
            DistalGlobalRest = chains[0].IntermediateGlobalRest with
            {
                Origin = chains[0].IntermediateGlobalRest.Origin + (straight * 0.5f),
            },
        };

        AssertFrameFailure(chains, "natural bend");
    }

    /// <summary>
    /// Gate 4: a chain whose swung intermediate segment is parallel to <c>L</c> has no available curvature
    /// direction (XR-002 TR19, A5).
    /// </summary>
    [Fact]
    public void TryDeriveHandFrame_IntermediateParallelToLongitudinal_FailsCurvatureAvailabilityGate()
    {
        FingerRestNeutralChain[] chains = CreateGatePassingChains(mirrored: false);
        Vector3 longitudinal = MiddleProximalDirection(chains);
        // Point chain 2's intermediate segment exactly along L while its own proximal direction stays a few
        // degrees off L, so the bend gate passes but the perpendicular curvature component vanishes.
        chains[2] = chains[2] with
        {
            DistalGlobalRest = chains[2].IntermediateGlobalRest with
            {
                Origin = chains[2].IntermediateGlobalRest.Origin + (longitudinal * 0.032f),
            },
        };

        AssertFrameFailure(chains, "curvature direction");
    }

    /// <summary>Gate 5: widely disagreeing curvature directions fail the concentration gate (XR-002 TR19, A5).</summary>
    [Fact]
    public void TryDeriveHandFrame_DisagreeingCurvature_FailsConcentrationGate()
    {
        FingerRestNeutralChain[] chains = CreateGatePassingChains(
            mirrored: false,
            curvatureAngleDegrees: [0.0f, 120.0f, 240.0f, 0.0f]);

        AssertFrameFailure(chains, "concentration");
    }

    /// <summary>
    /// Gate 6: one outlying curvature direction within the concentration bound still fails the disagreement
    /// gate (XR-002 TR19, A5).
    /// </summary>
    [Fact]
    public void TryDeriveHandFrame_OutlyingCurvatureDirection_FailsDisagreementGate()
    {
        FingerRestNeutralChain[] chains = CreateGatePassingChains(
            mirrored: false,
            curvatureAngleDegrees: [0.0f, 0.0f, 0.0f, 60.0f]);

        AssertFrameFailure(chains, "disagreement");
    }

    /// <summary>
    /// Gate 7: a valid frame is unit, orthogonal, and right-handed within the existing tolerances (XR-002 TR19).
    /// </summary>
    [Fact]
    public void TryDeriveHandFrame_ValidGeometry_ProducesUnitOrthogonalRightHandedFrame()
    {
        Assert.True(FingerAnatomicalMath.TryDeriveHandFrame(
            CreateGatePassingChains(mirrored: false),
            CreateIdentityDesiredGlobals(),
            out FingerAnatomicalFrame frame,
            out string error), error);

        Assert.InRange(Mathf.Abs(frame.Longitudinal.LengthSquared() - 1.0f), 0.0f, 1e-4f);
        Assert.InRange(Mathf.Abs(frame.Palmward.LengthSquared() - 1.0f), 0.0f, 1e-4f);
        Assert.InRange(Mathf.Abs(frame.Hinge.LengthSquared() - 1.0f), 0.0f, 1e-4f);
        Assert.InRange(Mathf.Abs(frame.Longitudinal.Dot(frame.Palmward)), 0.0f, 1e-4f);
        Assert.InRange(Mathf.Abs(frame.Longitudinal.Dot(frame.Hinge)), 0.0f, 1e-4f);
        Assert.InRange(Mathf.Abs(frame.Palmward.Dot(frame.Hinge)), 0.0f, 1e-4f);
    }

    /// <summary>
    /// Deterministic parallel handling: an exactly parallel direction produces the identity swing
    /// (XR-002 TR22).
    /// </summary>
    [Fact]
    public void TryShortestArc_ParallelDirections_ReturnIdentity()
    {
        Vector3 direction = new Vector3(0.3f, -0.8f, 0.5f).Normalized();

        Assert.True(FingerAnatomicalMath.TryShortestArc(direction, direction, out Quaternion rotation));
        AssertRotationApproximately(Quaternion.Identity, rotation);
        Assert.True(FingerAnatomicalMath.TryShortestArc(direction, direction * 4.0f, out Quaternion scaled));
        AssertRotationApproximately(Quaternion.Identity, scaled);
    }

    /// <summary>
    /// Bilateral binding derives every cache transactionally: the thumb Reset-key neutrals, the non-thumb
    /// neutrals, desired globals, local hinge axes (with the tipless distal inheriting the same global H in its
    /// own desired local frame), local proximal frames, and both hand frames (XR-002 TR18, TR25-TR27, TR29,
    /// A1, A6). The thumb records publish their authored axes and metacarpal frame through the same caches
    /// (XR-002 TR26-TR28.7).
    /// </summary>
    [Fact]
    public void TryDeriveBilateralBinding_ValidHands_PublishesCompleteAnatomicalCaches()
    {
        BindingBuffers buffers = CreateBindingBuffers();
        FingerHandRestGeometry left = CreateHandGeometry(mirrored: true);
        FingerHandRestGeometry right = CreateHandGeometry(mirrored: false);
        ThumbRestGeometry leftThumb = CreateThumbGeometry(mirrored: true);
        ThumbRestGeometry rightThumb = CreateThumbGeometry(mirrored: false);
        AuthoredThumbSideReferences leftReferences = CreateReferences(mirrored: true);
        AuthoredThumbSideReferences rightReferences = CreateReferences(mirrored: false);

        Assert.True(FingerAnatomicalMath.TryDeriveBilateralBinding(
            left,
            right,
            leftThumb,
            rightThumb,
            leftReferences,
            rightReferences,
            buffers.DestinationNeutrals,
            buffers.DesiredGlobalRotations,
            buffers.LocalHingeAxes,
            buffers.LocalProximalFrames,
            buffers.HandFrames,
            buffers.CorrespondenceFrames,
            out string error), error);

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            int sideOffset = sideIndex * OpticalFingerTrackingCalibrationProfile.RecordsPerSide;
            bool mirrored = sideIndex == 0;
            var sideNeutrals = new Quaternion[FingerRestNeutralMath.NeutralCount];
            var sideDesired = new Quaternion[FingerRestNeutralMath.NeutralCount];
            Assert.True(FingerRestNeutralMath.TryDerive(
                sideIndex == 0 ? left.HandBoneIndex : right.HandBoneIndex,
                sideIndex == 0 ? left.HandGlobalRest : right.HandGlobalRest,
                sideIndex == 0 ? left.Chains : right.Chains,
                sideNeutrals,
                sideDesired,
                out string sideError), sideError);
            for (int index = 0; index < FingerRestNeutralMath.NeutralCount; index++)
            {
                int recordIndex = sideOffset + 3 + index;
                AssertRotationApproximately(sideNeutrals[index], buffers.DestinationNeutrals[recordIndex]);
                AssertRotationApproximately(sideDesired[index], buffers.DesiredGlobalRotations[recordIndex]);

                Vector3 localHinge = buffers.LocalHingeAxes[recordIndex];
                AssertVectorApproximately(localHinge.Normalized(), localHinge);
                // The hinge expressed in the bone's own desired local frame maps back to the shared global H,
                // including for the tipless distal (XR-002 TR21, A6).
                Vector3 globalHinge = new Basis(buffers.DesiredGlobalRotations[recordIndex]) * localHinge;
                AssertVectorApproximately(buffers.HandFrames[sideIndex].Hinge, globalHinge);
            }

            for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
            {
                int proximal = sideOffset + 3 + (chainIndex * FingerRestNeutralMath.BonesPerChain);
                FingerAnatomicalFrame local = buffers.LocalProximalFrames[proximal];
                Vector3 globalLongitudinal = new Basis(buffers.DesiredGlobalRotations[proximal]) * local.Longitudinal;
                Vector3 globalPalmward = new Basis(buffers.DesiredGlobalRotations[proximal]) * local.Palmward;
                Vector3 globalHinge = new Basis(buffers.DesiredGlobalRotations[proximal]) * local.Hinge;
                AssertVectorApproximately(buffers.HandFrames[sideIndex].Longitudinal, globalLongitudinal);
                AssertVectorApproximately(buffers.HandFrames[sideIndex].Palmward, globalPalmward);
                AssertVectorApproximately(buffers.HandFrames[sideIndex].Hinge, globalHinge);
            }

            // The thumb records publish the sampled Reset local keys as N (XR-002 TR29) — never the TR43
            // chain-neutral swings and never the imported rest locals, which this fixture pins numerically
            // equal to the Reset keys exactly as on the reference female — the authored global rests as
            // import-forensic desired globals, the authored proximal/distal axes as the local hinge caches,
            // and the metacarpal (l, b, h) frame as the metacarpal's local swing frame (XR-002
            // TR25-TR28.7, A9).
            AuthoredThumbSideReferences sideReferences = mirrored ? leftReferences : rightReferences;

            AssertRotationApproximately(
                sideReferences.Metacarpal.Reset,
                buffers.DestinationNeutrals[sideOffset]);
            AssertRotationApproximately(
                sideReferences.Proximal.Reset,
                buffers.DestinationNeutrals[sideOffset + 1]);
            AssertRotationApproximately(
                sideReferences.Distal.Reset,
                buffers.DestinationNeutrals[sideOffset + 2]);

            FingerAnatomicalFrame frame = buffers.LocalProximalFrames[sideOffset];
            AssertVectorApproximately(frame.Hinge.Cross(frame.Longitudinal), frame.Palmward);
            AssertVectorApproximately(frame.Longitudinal.Cross(frame.Palmward), frame.Hinge);
            AssertVectorApproximately(frame.Palmward.Cross(frame.Hinge), frame.Longitudinal);
        }

        AssertVectorApproximately(
            Mirror(buffers.HandFrames[1].Hinge) * -1.0f,
            buffers.HandFrames[0].Hinge);
    }

    /// <summary>
    /// Any hand's failure fails the whole bilateral binding with no partial publication: the neutrals of
    /// the valid hand never leak (XR-002 TR18, A5).
    /// </summary>
    [Fact]
    public void TryDeriveBilateralBinding_RightHandFrameFailure_PublishesNothing()
    {
        BindingBuffers buffers = CreateBindingBuffers();
        FingerRestNeutralChain[] collapsed = CreateGatePassingChains(mirrored: false);
        Vector3 sharedRoot = new(0.0f, 0.0f, 0.0f);
        collapsed[0] = collapsed[0] with
        {
            ProximalGlobalRest = collapsed[0].ProximalGlobalRest with
            {
                Origin = sharedRoot,
            },
        };
        collapsed[3] = collapsed[3] with
        {
            ProximalGlobalRest = collapsed[3].ProximalGlobalRest with
            {
                Origin = sharedRoot,
            },
        };

        bool result = FingerAnatomicalMath.TryDeriveBilateralBinding(
            CreateHandGeometry(mirrored: true),
            new FingerHandRestGeometry(10, Transform3D.Identity, collapsed),
            CreateThumbGeometry(mirrored: true),
            CreateThumbGeometry(mirrored: false),
            CreateReferences(mirrored: true),
            CreateReferences(mirrored: false),
            buffers.DestinationNeutrals,
            buffers.DesiredGlobalRotations,
            buffers.LocalHingeAxes,
            buffers.LocalProximalFrames,
            buffers.HandFrames,
            buffers.CorrespondenceFrames,
            out string error);

        Assert.False(result);
        Assert.Contains("Right", error, StringComparison.Ordinal);
        Assert.Contains("span ratio", error, StringComparison.Ordinal);
        Assert.All(buffers.DestinationNeutrals, value => Assert.Equal(default, value));
        Assert.All(buffers.DesiredGlobalRotations, value => Assert.Equal(default, value));
        Assert.All(buffers.LocalHingeAxes, value => Assert.Equal(default, value));
        Assert.All(buffers.HandFrames, value => Assert.Equal(default, value));
    }

    private static FingerAnatomicalFrame CreateRotatedLocalFrame(float angle)
    {
        Quaternion rotation = new(new Vector3(1.0f, 2.0f, 3.0f).Normalized(), angle);
        return new FingerAnatomicalFrame(
            new Basis(rotation) * FingerAnatomicalMath.SourceLongitudinal,
            new Basis(rotation) * FingerAnatomicalMath.SourcePalmward,
            new Basis(rotation) * FingerAnatomicalMath.SourceHinge);
    }

    private static Quaternion[] CreateIdentityDesiredGlobals()
    {
        var result = new Quaternion[FingerRestNeutralMath.NeutralCount];
        Array.Fill(result, Quaternion.Identity);
        return result;
    }

    /// <summary>
    /// Builds four anatomically ordered chains whose curvature directions are controlled per chain: the
    /// intermediate segment bends a fixed 15° from the longitudinal direction towards an anchor rotated by
    /// <paramref name="curvatureAngleDegrees" /> within the plane perpendicular to <c>L</c>. The anchor is the
    /// palmward +Z direction projected perpendicular to <c>L</c>, so mirrored hands mirror correctly.
    /// </summary>
    private static FingerRestNeutralChain[] CreateGatePassingChains(
        bool mirrored,
        float[]? curvatureAngleDegrees = null)
    {
        float side = mirrored ? -1.0f : 1.0f;
        curvatureAngleDegrees ??= [0.0f, 0.0f, 0.0f, 0.0f];
        const float bendRadians = 0.2618f; // 15 degrees, comfortably above the 2-degree minimum.
        Vector3 middleProximalDirection = new Vector3(0.06f * (1 - 1.5f) * side, -1.0f, 0.03f).Normalized();
        Vector3 palmAnchor = Vector3.Back;
        Vector3 palmward = (palmAnchor - (middleProximalDirection * middleProximalDirection.Dot(palmAnchor)))
            .Normalized();
        Vector3 inPlane = middleProximalDirection.Cross(palmward).Normalized();

        var chains = new FingerRestNeutralChain[FingerRestNeutralMath.ChainCount];
        for (int chainIndex = 0; chainIndex < FingerRestNeutralMath.ChainCount; chainIndex++)
        {
            float spread = chainIndex - 1.5f;
            float curvatureAngle = Mathf.DegToRad(curvatureAngleDegrees[chainIndex]);
            Vector3 curvature = (palmward * Mathf.Cos(curvatureAngle))
                + (inPlane * Mathf.Sin(curvatureAngle));
            Vector3 proximalOrigin = new(spread * 0.02f * side, 0.0f, 0.001f);
            Vector3 proximalDirection = new Vector3(0.06f * spread * side, -1.0f, 0.03f).Normalized();
            Vector3 intermediateOrigin = proximalOrigin + (proximalDirection * 0.045f);
            Vector3 intermediateDirection =
                (middleProximalDirection * Mathf.Cos(bendRadians)) + (curvature * Mathf.Sin(bendRadians));
            Vector3 distalOrigin = intermediateOrigin + (intermediateDirection.Normalized() * 0.032f);
            int proximalBone = 20 + (chainIndex * 3);
            chains[chainIndex] = new FingerRestNeutralChain(
                proximalBone,
                10,
                new Transform3D(Basis.Identity, proximalOrigin),
                proximalBone + 1,
                proximalBone,
                new Transform3D(Basis.Identity, intermediateOrigin),
                proximalBone + 2,
                proximalBone + 1,
                new Transform3D(Basis.Identity, distalOrigin));
        }

        return chains;
    }

    private static Vector3 MiddleProximalDirection(FingerRestNeutralChain[] chains)
        => (chains[1].IntermediateGlobalRest.Origin - chains[1].ProximalGlobalRest.Origin).Normalized();

    /// <summary>
    /// A unilateral authored-reference gate failure — the left flexion key equals its Reset key, so the
    /// authored reference angle is zero — fails the whole bilateral binding transactionally: every published
    /// buffer stays cleared, no authored evidence is produced, and the exact reason names the side and joint
    /// (XR-002 TR26, A14).
    /// </summary>
    /// <summary>
    /// A thumb topology failure on either hand fails the whole bilateral binding transactionally: neither the
    /// non-thumb caches nor the other hand's thumb neutrals may leak, preserving the transactional bilateral
    /// publication contract for the authored-animation model (XR-002 TR25-TR26).
    /// </summary>
    [Fact]
    public void TryDeriveBilateralBinding_RightThumbTopologyFailure_PublishesNothing()
    {
        BindingBuffers buffers = CreateBindingBuffers();

        bool result = FingerAnatomicalMath.TryDeriveBilateralBinding(
            CreateHandGeometry(mirrored: true),
            CreateHandGeometry(mirrored: false),
            CreateThumbGeometry(mirrored: true),
            CreateThumbGeometry(mirrored: false) with
            {
                // The proximal is re-parented onto the hand bone, breaking the required chain topology.
                ProximalParentBoneIndex = 10,
            },
            CreateReferences(mirrored: true),
            CreateReferences(mirrored: false),
            buffers.DestinationNeutrals,
            buffers.DesiredGlobalRotations,
            buffers.LocalHingeAxes,
            buffers.LocalProximalFrames,
            buffers.HandFrames,
            buffers.CorrespondenceFrames,
            out string error);

        Assert.False(result);
        Assert.Contains("Right", error, StringComparison.Ordinal);
        Assert.Contains("parent topology", error, StringComparison.Ordinal);
        Assert.All(buffers.DestinationNeutrals, value => Assert.Equal(default, value));
        Assert.All(buffers.DesiredGlobalRotations, value => Assert.Equal(default, value));
        Assert.All(buffers.HandFrames, value => Assert.Equal(default, value));
    }

    /// <summary>
    /// <c>Delta = identity</c> reproduces the sampled Reset local key exactly for all six thumb destinations
    /// on both hands — metacarpal non-identity (≈95.26° reference-rig Reset key), proximal/distal identity —
    /// with no Requirement 43 fan correction applied (XR-002 TR26-TR27, TR29, A9). This fixture intentionally
    /// uses Reset keys numerically equal to the imported rest locals.
    /// </summary>
    [Fact]
    public void ThumbMapping_IdentityDelta_ReproducesResetKeyNeutralsExactly()
    {
        foreach (bool mirrored in new[] { false, true })
        {
            BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored);
            AuthoredThumbSideReferences references = CreateReferences(mirrored);
            int sideOffset = mirrored
                ? 0
                : OpticalFingerTrackingCalibrationProfile.RecordsPerSide;

            foreach ((Quaternion resetKey, XRHandJoint joint) in new[]
            {
                (references.Metacarpal.Reset, XRHandJoint.ThumbMetacarpal),
                (references.Proximal.Reset, XRHandJoint.ThumbProximal),
                (references.Distal.Reset, XRHandJoint.ThumbDistal),
            })
            {
                int record = sideOffset + ThumbRecordIndex(joint);
                LimbSide side = mirrored ? LimbSide.Left : LimbSide.Right;
                Quaternion written = MapThumbRecord(
                    bound,
                    joint == XRHandJoint.ThumbMetacarpal ? _fixtureSourceNeutral : Quaternion.Identity,
                    joint,
                    record,
                    side);
                AssertRotationApproximately(resetKey, written);
            }

            // The neutral IS the Reset key — never the TR43 chain-neutral swing product.
            Assert.True(Quaternion.Identity.AngleTo(references.Metacarpal.Reset) > 1.0f);
        }
    }

    /// <summary>
    /// A19 discrimination: on a synthetic rig whose Reset thumb keys differ from its imported rest rotations,
    /// the binding publishes the Reset keys as N and <c>Delta = identity</c> reproduces them exactly — not the
    /// imported rest — for all six thumb joints on both hands (XR-002 TR29, A19). Every Reset-key assertion
    /// below discriminates the sources by at least the perturbation margin (0.12 radians).
    /// </summary>
    [Fact]
    public void TryDeriveBilateralBinding_ResetDiffersFromRest_PublishesResetNeutralsNotRest()
    {
        const float offsetRadians = 0.12f;
        Quaternion leftOffset = new(FingerAnatomicalMath.SourceLongitudinal, offsetRadians);
        Quaternion rightOffset = new(leftOffset.X, -leftOffset.Y, -leftOffset.Z, leftOffset.W);
        ThumbRestGeometry leftThumb = WithRestLocalOffset(CreateThumbGeometry(mirrored: true), leftOffset);
        ThumbRestGeometry rightThumb = WithRestLocalOffset(CreateThumbGeometry(mirrored: false), rightOffset);

        foreach (bool mirrored in new[] { false, true })
        {
            BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored, leftThumb, rightThumb);
            AuthoredThumbSideReferences references = CreateReferences(mirrored);
            ThumbRestGeometry rest = mirrored ? leftThumb : rightThumb;
            int sideOffset = mirrored
                ? 0
                : OpticalFingerTrackingCalibrationProfile.RecordsPerSide;

            foreach ((Quaternion resetKey, XRHandJoint joint) in new[]
            {
                (references.Metacarpal.Reset, XRHandJoint.ThumbMetacarpal),
                (references.Proximal.Reset, XRHandJoint.ThumbProximal),
                (references.Distal.Reset, XRHandJoint.ThumbDistal),
            })
            {
                int record = sideOffset + ThumbRecordIndex(joint);
                Quaternion restNeutral = ExpectedThumbRestLocal(rest, joint);

                // The imported rest remains materially different from the Reset key, so the rig discriminates.
                Assert.True(
                    resetKey.Normalized().AngleTo(restNeutral) > 0.05f,
                    $"Expected the Reset key to differ from the imported rest at {joint}.");

                // The published neutral and the anchored-neutral metacarpal write (identity hinge delta
                // for the proximal/distal) are the Reset key — never the rest.
                AssertRotationApproximately(resetKey, bound.Buffers.DestinationNeutrals[record]);
                LimbSide side = mirrored ? LimbSide.Left : LimbSide.Right;
                Quaternion written = MapThumbRecord(
                    bound,
                    joint == XRHandJoint.ThumbMetacarpal ? _fixtureSourceNeutral : Quaternion.Identity,
                    joint,
                    record,
                    side);
                AssertRotationApproximately(resetKey, written);
                Assert.True(
                    written.Normalized().AngleTo(restNeutral) > 0.05f,
                    $"Expected the identity-Delta write to leave the imported rest behind at {joint}.");
            }
        }
    }

    /// <summary>
    /// Pure source flexion about the delivered <c>+X</c> writes exactly the authored neutral composed with the
    /// joint's authored-axis rotation of the same signed angle: <c>inverse(N) × D = rotation(a_j, theta)</c>,
    /// independently per joint, with the authored axes materially different from canonical <c>+X</c> so the
    /// two models are discriminated (XR-002 TR26-TR27, A10).
    /// </summary>
    [Theory]
    [InlineData(1.0f)]
    [InlineData(-0.55f)]
    public void ThumbMapping_PureSourceFlexion_WritesAuthoredAxisOnly(float theta)
    {
        foreach (bool mirrored in new[] { false, true })
        {
            BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored);
            Quaternion delta = new(FingerAnatomicalMath.SourceHinge, theta);

            foreach (XRHandJoint joint in new[] { XRHandJoint.ThumbProximal, XRHandJoint.ThumbDistal })
            {
                Quaternion neutral = ExpectedThumbRestLocal(bound.Thumb, joint);
                Quaternion written = MapThumbHinge(bound, delta, joint);
                Quaternion authored = neutral.Inverse().Normalized() * written;
                Vector3 axis = bound.Buffers.LocalHingeAxes[ThumbRecordIndex(joint)];

                AssertRotationApproximately(new Quaternion(axis.Normalized(), theta), authored);
                Assert.True(
                    authored.AngleTo(new Quaternion(FingerAnatomicalMath.SourceHinge, theta)) > 0.05f,
                    $"Expected the authored axis at {joint} to differ materially from canonical local +X.");
                Assert.True(
                    Mathf.Abs(authored.AngleTo(new Quaternion(axis.Normalized(), -theta))) > 0.05f || Mathf.Abs(theta) < 1e-6f,
                    $"Expected the authored hinge sign to follow theta at {joint}.");
            }
        }
    }

    /// <summary>
    /// Right-composed source roll about the canonical longitudinal <c>+Y</c> never changes the metacarpal
    /// output — the anchored correspondence reads only the direction <c>S × (+Y)</c> and roll maps
    /// <c>+Y</c> onto itself — and a non-neutral swing moves the mapped metacarpal direction off <c>l</c>
    /// through the pinned swing gain (XR-002 TR28.7, A16). The mapping is S0-drift-invariant by the same
    /// construction: any source neutral offset is absorbed by the anchor, never by the transferred direction.
    /// </summary>
    [Fact]
    public void ThumbMapping_MetacarpalSwing_RightComposedSourceRollIsInvariant()
    {
        BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored: false);
        const float gain = 2.0f;
        Quaternion swingRelation = _fixtureSourceNeutral
            * new Quaternion(FingerAnatomicalMath.SourceHinge, 0.7f)
            * new Quaternion(FingerAnatomicalMath.SourcePalmward, 0.4f);

        Quaternion swung = MapThumbSwing(bound, swingRelation, XRHandJoint.ThumbMetacarpal, LimbSide.Left, gain);
        foreach (float rollAngle in new[] { 0.5f, -1.2f, 2.6f })
        {
            Quaternion roll = new(FingerAnatomicalMath.SourceLongitudinal, rollAngle);
            AssertRotationApproximately(
                swung,
                MapThumbSwing(bound, swingRelation * roll, XRHandJoint.ThumbMetacarpal, LimbSide.Left, gain));
        }

        // The anchored neutral relation lands exactly on the Reset-sourced neutral (XR-002 TR29) — the
        // parallel branch returns N verbatim — while the swung relation moves the mapped metacarpal
        // direction off the authored longitudinal l by the gained swing angle.
        Quaternion neutral = bound.Buffers.DestinationNeutrals[ThumbRecordIndex(XRHandJoint.ThumbMetacarpal)];
        Quaternion anchoredNeutral = MapThumbSwing(
            bound,
            _fixtureSourceNeutral,
            XRHandJoint.ThumbMetacarpal,
            LimbSide.Left,
            gain);
        AssertRotationApproximately(neutral, anchoredNeutral);
        Assert.True(
            neutral.Normalized().AngleTo(swung.Normalized()) > 0.35f,
            "Expected the swing relation to move the metacarpal write materially off the neutral.");

        // The gained swing is exactly the pre-gain angle scaled by the directional effective gain.
        Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            swingRelation,
            bound.Buffers.CorrespondenceFrames[(int)LimbSide.Left],
            LimbSide.Left,
            bound.Buffers.LocalProximalFrames[ThumbRecordIndex(XRHandJoint.ThumbMetacarpal)],
            neutral,
            DeriveFixtureAnchor(bound, LimbSide.Left, _fixtureSourceNeutral),
            gain,
            out Quaternion written));
        AssertRotationApproximately(swung, written);

        // Off-hinge contamination at the hinge joints never reaches the destination.
        Quaternion hinge = MapThumbHinge(
            bound,
            new Quaternion(FingerAnatomicalMath.SourceLongitudinal, 1.4f),
            XRHandJoint.ThumbProximal);
        AssertRotationApproximately(
            bound.Buffers.DestinationNeutrals[ThumbRecordIndex(XRHandJoint.ThumbProximal)],
            hinge);
    }

    /// <summary>
    /// A degenerate or antiparallel anchored aim fails the metacarpal transfer closed — no roll axis is
    /// invented — and a degenerate hinge twist magnitude fails the thumb hinge destinations, so only the
    /// affected destination freezes per the thumb ladder (XR-002 TR26, TR28, A10).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThumbMapping_DegenerateDeltas_FailClosedWithoutInventedAxes(bool antiparallelAim)
    {
        BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored: false);
        int record = ThumbRecordIndex(XRHandJoint.ThumbMetacarpal);
        FingerAnatomicalFrame frame = bound.Buffers.LocalProximalFrames[record];
        AuthoredThumbCorrespondenceFrame correspondence = bound.Buffers.CorrespondenceFrames[(int)LimbSide.Left];

        // An aim exactly on -l: invert the correspondence and anchor stages so the transported direction
        // lands on the antipode of l (with an identity anchor), then recover the source relation whose
        // d_w = S × (+Y) produces that direction.
        Quaternion relation;
        if (antiparallelAim)
        {
            Vector3 transported = new Basis(bound.Buffers.DestinationNeutrals[record].Normalized())
                * (-frame.Longitudinal);
            float spanSign = -1.0f;
            Vector3 sourceSwing = new(
                transported.Dot(correspondence.SpanAxis) * spanSign,
                transported.Dot(correspondence.Longitudinal),
                transported.Dot(correspondence.PalmNormal));
            Assert.True(FingerAnatomicalMath.TryShortestArc(
                FingerAnatomicalMath.SourceLongitudinal,
                sourceSwing,
                out relation));
        }
        else
        {
            // Non-finite source relation: the transfer fails closed rather than writing a NaN rotation.
            relation = new Quaternion(float.NaN, 0.0f, 0.0f, 1.0f);
        }

        Assert.False(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            relation,
            correspondence,
            LimbSide.Left,
            frame,
            bound.Buffers.DestinationNeutrals[record],
            Quaternion.Identity,
            1.0f,
            out _));
        Assert.False(FingerAnatomicalMath.TryMapHingeDestination(
            new Quaternion(FingerAnatomicalMath.SourceLongitudinal, Mathf.Pi),
            FingerAnatomicalMath.SourceHinge,
            bound.Buffers.DestinationNeutrals[1],
            out _));
        Assert.False(FingerAnatomicalMath.TryMapHingeDestination(
            new Quaternion(FingerAnatomicalMath.SourcePalmward, Mathf.Pi),
            FingerAnatomicalMath.SourceHinge,
            bound.Buffers.DestinationNeutrals[2],
            out _));
    }

    /// <summary>
    /// Pinned bilateral opposition convergence (H8, A10/A22 operational definition): using settled captured
    /// THUMB_OPPOSITION wrist→metacarpal relations with the pinned per-side <c>Q0</c> anchors and
    /// <c>K_meta</c> gains, the anchored-correspondence metacarpal transfer must swing the avatar
    /// metacarpal direction towards its authored bend axis <c>b</c> — the palmward soft-fist direction
    /// validated by the palm and movement-alignment gates — in EACH hand's own frame (XR-002 TR28.7, TR45).
    /// <para>
    /// The independent metric is frame-local and self-contained: the written metacarpal deviates from its
    /// Reset neutral by the gained opposition swing (well above the ~10 degree A20 neutral tolerance) and
    /// the swung direction's dot with <c>b</c> strictly increases from the neutral's <c>l·b</c> baseline.
    /// The discarded metacarpal axial roll is never transferred. This pins the offline replay's opposition
    /// behaviour (applied medians ≈83°/81° at the pinned gains) without hardware.
    /// </para>
    /// </summary>
    [Fact]
    public void ThumbAuthoredMapping_CaptureOppositionSources_ConvergeTowardsAuthoredBendBilaterally()
    {
        // Settled captured THUMB_OPPOSITION wrist→thumb-metacarpal relations (left/right rotated pair).
        Quaternion[] oppositionRelations =
        [
            new Quaternion(0.12441165745258331f, 0.7749360203742981f, 0.22990232706069946f, 0.5754483938217163f),
            new Quaternion(0.13855485618114471f, -0.7621821165084839f, -0.2597687542438507f, 0.5765423774719238f),
        ];
        // Pinned per-side metacarpal correspondence records (XR-002 TR45): Q0 anchors and K_meta gains from
        // the same trace's offline replay.
        Quaternion[] anchors =
        [
            new Quaternion(-0.1273963451385498f, 0.019958913326263428f, 0.25467225909233093f, 0.9583913087844849f),
            new Quaternion(-0.10855846107006073f, 0.020310375839471817f, -0.15915964543819427f, 0.9810559153556824f),
        ];
        float[] gains = [2.0f, 2.25f];

        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            LimbSide side = sideIndex == 0 ? LimbSide.Left : LimbSide.Right;
            BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored: sideIndex == 0);
            int record = sideIndex * OpticalFingerTrackingCalibrationProfile.RecordsPerSide;

            Quaternion neutral = bound.Buffers.DestinationNeutrals[record];
            FingerAnatomicalFrame frame = bound.Buffers.LocalProximalFrames[record];

            Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                oppositionRelations[sideIndex],
                bound.Buffers.CorrespondenceFrames[sideIndex],
                side,
                frame,
                neutral,
                anchors[sideIndex],
                gains[sideIndex],
                out Quaternion written));
            Quaternion swing = neutral.Inverse().Normalized() * written;

            // The gained opposition swing is material on both sides (replay medians ~83/81 degrees).
            Assert.True(written.Normalized().AngleTo(neutral.Normalized()) > Mathf.DegToRad(60.0f));

            Vector3 neutralDirection = frame.Longitudinal;
            Vector3 swungDirection = new Basis(swing) * frame.Longitudinal;
            float neutralDot = neutralDirection.Dot(frame.Palmward);
            float swungDot = swungDirection.Dot(frame.Palmward);
            Assert.True(
                swungDot > neutralDot + 0.1f,
                $"Side {sideIndex}: physical opposition must increase the authored-bend component of the " +
                $"metacarpal direction; neutral dot {neutralDot:F3} -> swung dot {swungDot:F3}.");

            float neutralAngle = Mathf.RadToDeg(neutralDirection.AngleTo(frame.Palmward));
            float swungAngle = Mathf.RadToDeg(swungDirection.AngleTo(frame.Palmward));
            Assert.True(
                neutralAngle - swungAngle > 5.0f,
                $"Side {sideIndex}: physical opposition must converge the avatar thumb towards its authored " +
                $"bend axis; neutral {neutralAngle:F1} deg -> swung {swungAngle:F1} deg.");
        }
    }

    /// <summary>
    /// Decisive pinned replay regression (XR-002 TR28.7): the rotated bilateral pair for all nine captured pose
    /// families must reproduce the oracle's
    /// <c>writtenLocal</c> rotations and pre/post-gain angles within float32 tolerance. The current C1
    /// directional response is the smoothstep product of swing-axis <c>h</c> and mirrored <c>b</c>, using the
    /// profile thresholds <c>h0=0.400</c>, <c>h1=0.625</c>, <c>b0=0.050</c>, and <c>b1=0.150</c>. The
    /// unconditional-gain counterfactual is the discrimination baseline. The constants embed the binding-frame
    /// values (<c>l</c>, palm plane <c>u/t/n</c>, Reset-key <c>N</c>, <c>Q0</c>, and <c>K_meta</c>) and traced
    /// source relations, so the C# runtime transfer is pinned against the numerical oracle.
    /// </summary>
    [Fact]
    public void ThumbMetacarpalMapping_PinnedReplayFrames_ReproduceOfflineReplayPredictions()
    {
        foreach ((LimbSide side, PinnedReplaySide pinned) in new[]
                 {
                     (LimbSide.Left, PinnedReplaySide.Left),
                     (LimbSide.Right, PinnedReplaySide.Right),
                 })
        {
            FingerAnatomicalFrame frame = new(pinned.Longitudinal, pinned.Bend, pinned.Splay);
            AuthoredThumbCorrespondenceFrame correspondence =
                new(pinned.PalmLongitudinal, pinned.PalmSpanAxis, pinned.PalmNormal);

            foreach (PinnedReplayFrame replayFrame in pinned.Frames)
            {
                Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                    replayFrame.SourceRelation,
                    correspondence,
                    side,
                    frame,
                    pinned.Neutral,
                    pinned.NeutralAnchor,
                    pinned.SwingGain,
                    out Quaternion written));

                float writtenErrorDegrees = Mathf.RadToDeg(
                    written.Normalized().AngleTo(replayFrame.ExpectedWritten.Normalized()));
                Assert.True(
                    writtenErrorDegrees <= 0.1f,
                    $"{side}/{replayFrame.Marker}: C# writtenLocal must reproduce the offline replay " +
                    $"prediction within float32 tolerance; got {writtenErrorDegrees:E} degrees.");
                if (replayFrame.Marker == "FIST")
                {
                    Assert.Equal(replayFrame.PreGainSwingDegrees * pinned.SwingGain,
                        replayFrame.PostGainSwingDegrees, 3);
                }
            }
        }
    }

    /// <summary>
    /// Neutral anchor contract (XR-002 TR28.7, TR45): at the capture's settled neutral-window mean source
    /// relation, the anchored transfer lands the metacarpal on its Reset neutral — the anchor was derived
    /// from exactly that direction — within the ~1.5-degree anchored-neutral tolerance (measured 0.006° left
    /// / 0.0° right on the reference trace). Under a mis-anchored transfer the deviation would be the full
    /// 22-33-degree rig-versus-hardware neutral offset.
    /// </summary>
    [Fact]
    public void ThumbMetacarpalMapping_NeutralMeanSource_LandsWithinAnchoredNeutralTolerance()
    {
        foreach ((LimbSide side, PinnedReplaySide pinned) in new[]
                 {
                     (LimbSide.Left, PinnedReplaySide.Left),
                     (LimbSide.Right, PinnedReplaySide.Right),
                 })
        {
            Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                pinned.NeutralMeanSourceRelation,
                new AuthoredThumbCorrespondenceFrame(pinned.PalmLongitudinal, pinned.PalmSpanAxis, pinned.PalmNormal),
                side,
                new FingerAnatomicalFrame(pinned.Longitudinal, pinned.Bend, pinned.Splay),
                pinned.Neutral,
                pinned.NeutralAnchor,
                pinned.SwingGain,
                out Quaternion written));

            float deviationDegrees = Mathf.RadToDeg(written.Normalized().AngleTo(pinned.Neutral.Normalized()));
            Assert.True(
                deviationDegrees <= 1.5f,
                $"{side}: the settled neutral-mean source relation must land within the ~1.5-degree " +
                $"anchored-neutral tolerance, got {deviationDegrees:F3} degrees.");
        }
    }

    /// <summary>
    /// S0-drift invariance by construction (XR-002 TR28.7): the transfer consumes only the live relation
    /// through <c>d_w = S × (+Y)</c>, so recalibrating the source neutral changes the delta but never the
    /// transported direction — the algebraic identity <c>S × (+Y) = (S0 × Delta) × (+Y)</c> holds for every
    /// S0, and any constant neutral offset is absorbed by the pinned Q0 anchor rather than the transferred
    /// direction. The replay verifies this to 4.7e-06 degrees on the captured inputs.
    /// </summary>
    [Fact]
    public void ThumbMetacarpalMapping_ConsumesLiveRelation_IsS0DriftInvariant()
    {
        PinnedReplaySide pinned = PinnedReplaySide.Left;
        Quaternion source = pinned.Frames[3].SourceRelation;
        Vector3 direct = new Basis(source.Normalized()) * FingerAnatomicalMath.SourceLongitudinal;

        foreach (Quaternion driftedNeutral in new[]
                 {
                     Quaternion.Identity,
                     new Quaternion(Vector3.Right, 0.35f),
                     new Quaternion(-0.079947028f, 0.5618452f, 0.402148143f, 0.718481256f),
                 })
        {
            // Different S0 -> different delta for the same physical S, but the same transported direction:
            // the metacarpal output depends on S alone.
            Quaternion delta = FingerRetargetingMath.DeriveNeutralDelta(source, driftedNeutral);
            Vector3 throughNeutral = new Basis(
                (driftedNeutral.Normalized() * delta).Normalized()) * FingerAnatomicalMath.SourceLongitudinal;
            Assert.True(
                direct.DistanceTo(throughNeutral) <= 1e-5f,
                "S0*Delta must reconstruct the same live relation the transfer consumes.");
        }
    }

    /// <summary>
    /// Non-uniformly scaled — finite, positive-determinant, within the documented tolerance — thumb global and
    /// authored local rest bases bind successfully on both hands: the tolerant polar extraction still
    /// qualifies the bases and recovers the desired-global rotations (column scaling of a rotation preserves
    /// its polar factor exactly), the published neutrals are the sampled Reset keys (XR-002 TR29) — which this
    /// fixture keeps numerically equal to the extracted rest locals exactly as on the reference female — and
    /// <c>Delta = identity</c> reproduces <c>N</c> exactly (XR-002 TR25-TR27, TR29).
    /// </summary>
    [Fact]
    public void ThumbBinding_NonUniformRestBases_ExtractPolarRotationsAndKeepIdentityNeutralExact()
    {
        foreach (bool mirrored in new[] { false, true })
        {
            // A side-matched binding (no geometry crossing), so the Reset keys and rest geometry agree per side.
            ThumbRestGeometry leftThumb = WithNonUniformThumbRests(CreateThumbGeometry(mirrored: true));
            ThumbRestGeometry rightThumb = WithNonUniformThumbRests(CreateThumbGeometry(mirrored: false));
            BoundThumbBuffers bound = CreateBoundThumbBuffers(mirrored: true, leftThumb, rightThumb);
            AuthoredThumbSideReferences references = CreateReferences(mirrored);
            ThumbRestGeometry thumb = mirrored ? leftThumb : rightThumb;
            int sideOffset = mirrored
                ? 0
                : OpticalFingerTrackingCalibrationProfile.RecordsPerSide;
            int metacarpalRecord = sideOffset + ThumbRecordIndex(XRHandJoint.ThumbMetacarpal);
            int proximalRecord = sideOffset + ThumbRecordIndex(XRHandJoint.ThumbProximal);
            int distalRecord = sideOffset + ThumbRecordIndex(XRHandJoint.ThumbDistal);

            Assert.True(ThumbRestBasisMath.TryExtractRotation(
                thumb.MetacarpalRestLocalBasis,
                "metacarpal local",
                out Quaternion metacarpalRest,
                out string neutralError), neutralError);

            // The Reset-sourced neutral (XR-002 TR29) and the tolerant polar extraction of the scaled authored
            // local rest agree exactly on this fixture — the reference-rig invariant Reset ≡ imported rest.
            AssertRotationApproximately(
                references.Metacarpal.Reset,
                bound.Buffers.DestinationNeutrals[metacarpalRecord]);
            AssertRotationApproximately(
                metacarpalRest,
                bound.Buffers.DestinationNeutrals[metacarpalRecord]);
            AssertRotationApproximately(
                metacarpalRest,
                bound.Buffers.DesiredGlobalRotations[metacarpalRecord]);

            Assert.True(ThumbRestBasisMath.TryExtractRotation(
                thumb.ProximalRestLocalBasis,
                "proximal local",
                out Quaternion proximalRest,
                out string proximalError), proximalError);
            AssertRotationApproximately(
                references.Proximal.Reset,
                bound.Buffers.DestinationNeutrals[proximalRecord]);
            AssertRotationApproximately(proximalRest, bound.Buffers.DestinationNeutrals[proximalRecord]);
            AssertRotationApproximately(Quaternion.Identity, bound.Buffers.DesiredGlobalRotations[proximalRecord]);
            AssertRotationApproximately(Quaternion.Identity, bound.Buffers.DesiredGlobalRotations[distalRecord]);

            // The anchored-neutral metacarpal write (identity hinge delta for the proximal/distal) must
            // still land exactly on the Reset-sourced neutral.
            AssertRotationApproximately(
                bound.Buffers.DestinationNeutrals[metacarpalRecord],
                MapThumbRecord(
                    bound,
                    _fixtureSourceNeutral,
                    XRHandJoint.ThumbMetacarpal,
                    metacarpalRecord,
                    mirrored ? LimbSide.Left : LimbSide.Right));
            AssertRotationApproximately(
                bound.Buffers.DestinationNeutrals[proximalRecord],
                MapThumbRecord(bound, Quaternion.Identity, XRHandJoint.ThumbProximal, proximalRecord));
            AssertRotationApproximately(
                references.Distal.Reset,
                MapThumbRecord(bound, Quaternion.Identity, XRHandJoint.ThumbDistal, distalRecord));
        }
    }

    /// <summary>
    /// A thumb rest basis that is reflected, degenerate, non-finite, or beyond the tolerated scale spread fails
    /// the whole bilateral binding transactionally — the rejection message quantifies the measured column scales
    /// — and no partial state leaks (XR-002 TR25).
    /// </summary>
    [Theory]
    [InlineData("reflection-global")]
    [InlineData("reflection-local")]
    [InlineData("ratio-global")]
    [InlineData("deviation-local")]
    [InlineData("degenerate-global")]
    [InlineData("non-finite-global")]
    public void TryDeriveBilateralBinding_InvalidThumbRestBasis_FailsClosedWithScaleEvidence(string kind)
    {
        Basis reflection = new(Vector3.Left, Vector3.Up, Vector3.Back);
        Basis excessiveRatio = new(Vector3.Right * 1.9f, Vector3.Up, Vector3.Back);
        Basis excessiveDeviation = new(Vector3.Right * 1.5f, Vector3.Up, Vector3.Back);
        Basis degenerate = new(Vector3.Right * 1e-6f, Vector3.Up, Vector3.Back);
        Basis nonFinite = new(new Vector3(float.NaN, 0.0f, 0.0f), Vector3.Up, Vector3.Back);

        ThumbRestGeometry mutated = kind switch
        {
            "reflection-global" => CreateThumbGeometry(mirrored: true) with
            {
                MetacarpalGlobalRest = new Transform3D(
                    reflection,
                    CreateThumbGeometry(mirrored: true).MetacarpalGlobalRest.Origin),
            },
            "reflection-local" => CreateThumbGeometry(mirrored: true) with
            {
                MetacarpalRestLocalBasis = reflection,
            },
            "ratio-global" => CreateThumbGeometry(mirrored: true) with
            {
                MetacarpalGlobalRest = new Transform3D(
                    excessiveRatio,
                    CreateThumbGeometry(mirrored: true).MetacarpalGlobalRest.Origin),
            },
            "deviation-local" => CreateThumbGeometry(mirrored: true) with
            {
                MetacarpalRestLocalBasis = excessiveDeviation,
            },
            "degenerate-global" => CreateThumbGeometry(mirrored: true) with
            {
                MetacarpalGlobalRest = new Transform3D(
                    degenerate,
                    CreateThumbGeometry(mirrored: true).MetacarpalGlobalRest.Origin),
            },
            "non-finite-global" => CreateThumbGeometry(mirrored: true) with
            {
                MetacarpalGlobalRest = new Transform3D(
                    nonFinite,
                    CreateThumbGeometry(mirrored: true).MetacarpalGlobalRest.Origin),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        string expectedError = kind.StartsWith("reflection", StringComparison.Ordinal)
            ? "reflection"
            : kind.StartsWith("ratio", StringComparison.Ordinal) || kind.StartsWith("deviation", StringComparison.Ordinal)
                ? "beyond the tolerated import artifact"
                : kind.StartsWith("degenerate", StringComparison.Ordinal) ? "degenerate" : "non-finite";

        BindingBuffers buffers = CreateBindingBuffers();
        bool result = FingerAnatomicalMath.TryDeriveBilateralBinding(
            CreateHandGeometry(mirrored: true),
            CreateHandGeometry(mirrored: false),
            mutated,
            CreateThumbGeometry(mirrored: false),
            CreateReferences(mirrored: true),
            CreateReferences(mirrored: false),
            buffers.DestinationNeutrals,
            buffers.DesiredGlobalRotations,
            buffers.LocalHingeAxes,
            buffers.LocalProximalFrames,
            buffers.HandFrames,
            buffers.CorrespondenceFrames,
            out string error);

        Assert.False(result);
        Assert.Contains("Left", error, StringComparison.Ordinal);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
        if (kind != "non-finite-global")
        {
            Assert.Contains("column scales", error, StringComparison.Ordinal);
        }

        Assert.All(buffers.DestinationNeutrals, value => Assert.Equal(default, value));
        Assert.All(buffers.DesiredGlobalRotations, value => Assert.Equal(default, value));
        Assert.All(buffers.HandFrames, value => Assert.Equal(default, value));
        Assert.All(buffers.CorrespondenceFrames, value => Assert.Equal(default, value));
    }

    /// <summary>Scales a rotation's basis columns by per-axis import-artifact factors within the TR25 tolerance.</summary>
    private static Basis ScaleRotationColumns(Quaternion rotation, Vector3 scales)
        => new Basis(rotation) * new Basis(
            Vector3.Right * scales.X,
            Vector3.Up * scales.Y,
            Vector3.Back * scales.Z);

    /// <summary>
    /// Applies mild non-uniform column scale — the observed import artifact shape — to every thumb global rest
    /// and authored local rest basis while preserving the rest origins (XR-002 TR25).
    /// </summary>
    private static ThumbRestGeometry WithNonUniformThumbRests(ThumbRestGeometry thumb)
    {
        Quaternion metacarpalRest = thumb.MetacarpalRestLocalBasis.GetRotationQuaternion();
        return thumb with
        {
            MetacarpalGlobalRest = new Transform3D(
                ScaleRotationColumns(metacarpalRest, new Vector3(1.18f, 0.94f, 1.06f)),
                thumb.MetacarpalGlobalRest.Origin),
            MetacarpalRestLocalBasis = ScaleRotationColumns(metacarpalRest, new Vector3(1.12f, 1.05f, 0.97f)),
            ProximalGlobalRest = new Transform3D(
                ScaleRotationColumns(Quaternion.Identity, new Vector3(1.08f, 1.02f, 0.99f)),
                thumb.ProximalGlobalRest.Origin),
            ProximalRestLocalBasis = ScaleRotationColumns(Quaternion.Identity, new Vector3(1.06f, 0.98f, 1.03f)),
            DistalGlobalRest = new Transform3D(
                ScaleRotationColumns(Quaternion.Identity, new Vector3(0.97f, 1.09f, 1.01f)),
                thumb.DistalGlobalRest.Origin),
            DistalRestLocalBasis = ScaleRotationColumns(Quaternion.Identity, new Vector3(1.04f, 1.03f, 0.96f)),
        };
    }

    private sealed class BoundThumbBuffers(ThumbRestGeometry thumb, BindingBuffers buffers)
    {
        public ThumbRestGeometry Thumb => thumb;

        public BindingBuffers Buffers => buffers;

    }

    /// <summary>
    /// Builds one side's authored-reference inputs from the pinned real-asset keys and the synthetic
    /// fixture skeleton (XR-002 TR28.2): the Reset forward kinematics composes the absolute hand and
    /// metacarpal rotations from the arm keys and accumulates the skeleton-space positions of the wrist,
    /// hand, and four proximal roots through the imported rest origins, exactly as the specification's FK
    /// defines. The thumb-proximal rest origin matches <see cref="CreateThumbGeometry" />'s segment.
    /// </summary>
    private static AuthoredThumbSideReferences CreateReferences(bool mirrored)
    {
        float side = mirrored ? -1.0f : 1.0f;
        Quaternion upperArmReset = (mirrored ? AuthoredReferenceKeys.LeftUpperArmReset : AuthoredReferenceKeys.RightUpperArmReset).Normalized();
        Quaternion lowerArmReset = (mirrored ? AuthoredReferenceKeys.LeftLowerArmReset : AuthoredReferenceKeys.RightLowerArmReset).Normalized();
        Quaternion handReset = (mirrored ? AuthoredReferenceKeys.LeftHandReset : AuthoredReferenceKeys.RightHandReset).Normalized();
        Quaternion metacarpalReset =
            (mirrored ? AuthoredReferenceKeys.LeftThumbMetacarpalReset : AuthoredReferenceKeys.RightThumbMetacarpalReset).Normalized();
        Quaternion proximalReset =
            (mirrored ? AuthoredReferenceKeys.LeftThumbProximalReset : AuthoredReferenceKeys.RightThumbProximalReset).Normalized();
        Quaternion distalReset =
            (mirrored ? AuthoredReferenceKeys.LeftThumbDistalReset : AuthoredReferenceKeys.RightThumbDistalReset).Normalized();
        Quaternion metacarpalFlexion =
            (mirrored ? AuthoredReferenceKeys.LeftThumbMetacarpalFlexion : AuthoredReferenceKeys.RightThumbMetacarpalFlexion).Normalized();
        Quaternion proximalFlexion =
            (mirrored ? AuthoredReferenceKeys.LeftThumbProximalFlexion : AuthoredReferenceKeys.RightThumbProximalFlexion).Normalized();
        Quaternion distalFlexion =
            (mirrored ? AuthoredReferenceKeys.LeftThumbDistalFlexion : AuthoredReferenceKeys.RightThumbDistalFlexion).Normalized();

        Quaternion wristGlobal = upperArmReset * lowerArmReset;
        Quaternion handGlobal = wristGlobal * handReset;
        Quaternion metacarpalGlobal = handGlobal * metacarpalReset;

        // Real-rig chirality FK (XR-002 TR28.2-28.3): the wrist anchor sits at −Y in the hand-local frame
        // because o_hand = +Y·0.2050707 in the hand-parent frame, and the retained imported proximal-root
        // rest origins extend +Y.
        Vector3 upperArmOrigin = new(0.2f * side, 1.4f, 0.0f);
        Vector3 lowerArmOrigin = new(0.0f, -0.25f, 0.0f);
        Vector3 handRestOrigin = AuthoredReferenceKeys.HandRestOriginInLowerArm;
        Vector3 wristPosition = upperArmOrigin + (new Basis(upperArmReset) * lowerArmOrigin);
        Vector3 handPosition = wristPosition + (new Basis(wristGlobal) * handRestOrigin);

        Vector3[] leftRootOrigins = AuthoredReferenceKeys.LeftProximalRootRestOrigins;
        var rootOrigins = new Vector3[leftRootOrigins.Length];
        for (int rootIndex = 0; rootIndex < leftRootOrigins.Length; rootIndex++)
        {
            rootOrigins[rootIndex] = mirrored
                ? leftRootOrigins[rootIndex]
                : AuthoredThumbAxisMath.MirrorPolar(leftRootOrigins[rootIndex]);
        }

        Vector3 indexPosition = handPosition + (new Basis(handGlobal) * rootOrigins[0]);
        Vector3 middlePosition = handPosition + (new Basis(handGlobal) * rootOrigins[1]);
        Vector3 ringPosition = handPosition + (new Basis(handGlobal) * rootOrigins[2]);
        Vector3 littlePosition = handPosition + (new Basis(handGlobal) * rootOrigins[3]);

        Vector3 thumbProximalRestOrigin = mirrored
            ? AuthoredReferenceKeys.LeftThumbProximalRestOrigin
            : AuthoredThumbAxisMath.MirrorPolar(AuthoredReferenceKeys.LeftThumbProximalRestOrigin);

        return new AuthoredThumbSideReferences(
            new AuthoredThumbJointKeys(metacarpalReset, metacarpalFlexion),
            new AuthoredThumbJointKeys(proximalReset, proximalFlexion),
            new AuthoredThumbJointKeys(distalReset, distalFlexion),
            new AuthoredThumbResetGeometry(
                handGlobal,
                metacarpalReset,
                metacarpalGlobal,
                thumbProximalRestOrigin,
                wristPosition,
                handPosition,
                indexPosition,
                middlePosition,
                ringPosition,
                littlePosition));
    }

    private static BoundThumbBuffers CreateBoundThumbBuffers(
        bool mirrored,
        ThumbRestGeometry? leftThumb = null,
        ThumbRestGeometry? rightThumb = null)
    {
        ThumbRestGeometry left = leftThumb ?? CreateThumbGeometry(mirrored);
        ThumbRestGeometry right = rightThumb ?? CreateThumbGeometry(!mirrored);
        BindingBuffers buffers = CreateBindingBuffers();
        Assert.True(FingerAnatomicalMath.TryDeriveBilateralBinding(
            CreateHandGeometry(mirrored),
            CreateHandGeometry(!mirrored),
            left,
            right,
            CreateReferences(mirrored: true),
            CreateReferences(mirrored: false),
            buffers.DestinationNeutrals,
            buffers.DesiredGlobalRotations,
            buffers.LocalHingeAxes,
            buffers.LocalProximalFrames,
            buffers.HandFrames,
            buffers.CorrespondenceFrames,
            out string error), error);
        return new BoundThumbBuffers(left, buffers);
    }

    /// <summary>
    /// The fixture's wrist→metacarpal source neutral: the S whose transported direction the derived anchor
    /// lands on <c>l</c>, making it the anchored neutral of the synthetic rig.
    /// </summary>
    private static readonly Quaternion _fixtureSourceNeutral = new(Vector3.Right, 0.35f);

    /// <summary>
    /// Derives the fixture's metacarpal neutral anchor exactly as the offline replay calibrates Q0
    /// (XR-002 TR45, TR28.7): <c>l0 = normalise(N⁻¹ × C_side(S × (+Y)))</c> for the anchored neutral S, then
    /// <c>Q0 = shortest_arc(l0, l)</c>. Test-owned oracle construction for the calibration method — the
    /// runtime mapping under test never derives anchors.
    /// </summary>
    private static Quaternion DeriveFixtureAnchor(BoundThumbBuffers bound, LimbSide side, Quaternion anchoredNeutral)
    {
        int record = (side == LimbSide.Left ? 0 : OpticalFingerTrackingCalibrationProfile.RecordsPerSide)
            + ThumbRecordIndex(XRHandJoint.ThumbMetacarpal);
        FingerAnatomicalFrame frame = bound.Buffers.LocalProximalFrames[record];
        AuthoredThumbCorrespondenceFrame correspondence = bound.Buffers.CorrespondenceFrames[(int)side];
        Quaternion neutral = bound.Buffers.DestinationNeutrals[record].Normalized();
        Vector3 anchoredSwing = new Basis(anchoredNeutral.Normalized()) * FingerAnatomicalMath.SourceLongitudinal;
        float spanSign = side == LimbSide.Left ? -1.0f : 1.0f;
        Vector3 transported = (correspondence.SpanAxis * (spanSign * anchoredSwing.X))
            + (correspondence.Longitudinal * anchoredSwing.Y)
            + (correspondence.PalmNormal * anchoredSwing.Z);
        Vector3 neutralTransported = (new Basis(neutral.Inverse()) * transported).Normalized();
        Assert.True(FingerAnatomicalMath.TryShortestArc(
            neutralTransported,
            frame.Longitudinal,
            out Quaternion anchor));
        return anchor;
    }

    /// <summary>
    /// Applies the production authored-animation thumb metacarpal mapping: the anchored hand-frame
    /// correspondence transfer <c>D = N × rotation(axis, k_eff · angle)</c> from the live source relation,
    /// with the fixture anchor derived for the fixture source neutral and a unit gain unless overridden
    /// (XR-002 TR28.7).
    /// </summary>
    private static Quaternion MapThumbSwing(
        BoundThumbBuffers bound,
        Quaternion sourceRelation,
        XRHandJoint joint,
        LimbSide side = LimbSide.Left,
        float gain = 1.0f)
    {
        int record = (side == LimbSide.Left ? 0 : OpticalFingerTrackingCalibrationProfile.RecordsPerSide)
            + ThumbRecordIndex(joint);
        Assert.True(FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
            sourceRelation,
            bound.Buffers.CorrespondenceFrames[(int)side],
            side,
            bound.Buffers.LocalProximalFrames[record],
            bound.Buffers.DestinationNeutrals[record],
            DeriveFixtureAnchor(bound, side, _fixtureSourceNeutral),
            gain,
            out Quaternion rotation));
        return rotation;
    }

    /// <summary>
    /// Applies the production authored-animation thumb hinge mapping: signed hinge flexion about the joint's
    /// independent authored axis right-composed after the authored rest neutral (XR-002 TR27).
    /// </summary>
    private static Quaternion MapThumbHinge(BoundThumbBuffers bound, Quaternion delta, XRHandJoint joint)
    {
        int record = ThumbRecordIndex(joint);
        Assert.True(FingerAnatomicalMath.TryMapHingeDestination(
            delta,
            bound.Buffers.LocalHingeAxes[record],
            bound.Buffers.DestinationNeutrals[record],
            out Quaternion rotation));
        return rotation;
    }

    /// <summary>
    /// Applies the production authored-animation thumb mapping for one explicit bilateral record slot — the
    /// metacarpal anchored hand-frame correspondence with the fixture anchor, or the joint's independent
    /// authored hinge, both right-composed after the Reset-sourced neutral (XR-002 TR26-TR28.7, TR29).
    /// </summary>
    private static Quaternion MapThumbRecord(
        BoundThumbBuffers bound,
        Quaternion sourceRelation,
        XRHandJoint joint,
        int record,
        LimbSide side = LimbSide.Left)
    {
        bool mapped = joint == XRHandJoint.ThumbMetacarpal
            ? FingerAnatomicalMath.TryMapThumbMetacarpalSwing(
                sourceRelation,
                bound.Buffers.CorrespondenceFrames[(int)side],
                side,
                bound.Buffers.LocalProximalFrames[record],
                bound.Buffers.DestinationNeutrals[record],
                DeriveFixtureAnchor(bound, side, _fixtureSourceNeutral),
                1.0f,
                out Quaternion rotation)
            : FingerAnatomicalMath.TryMapHingeDestination(
                sourceRelation,
                bound.Buffers.LocalHingeAxes[record],
                bound.Buffers.DestinationNeutrals[record],
                out rotation);
        Assert.True(mapped);
        return rotation;
    }

    /// <summary>
    /// Rotates every thumb imported rest local basis by a fixed local offset, producing the A19 synthetic rig
    /// whose imported rest differs from the pinned Reset keys while every rest basis stays a finite,
    /// <see cref="ThumbRestBasisMath" />-qualified rotation (XR-002 TR25, TR29, A19).
    /// </summary>
    private static ThumbRestGeometry WithRestLocalOffset(ThumbRestGeometry thumb, Quaternion offset)
        => thumb with
        {
            MetacarpalRestLocalBasis = new Basis(
                thumb.MetacarpalRestLocalBasis.GetRotationQuaternion().Normalized() * offset),
            ProximalRestLocalBasis = new Basis(
                thumb.ProximalRestLocalBasis.GetRotationQuaternion().Normalized() * offset),
            DistalRestLocalBasis = new Basis(
                thumb.DistalRestLocalBasis.GetRotationQuaternion().Normalized() * offset),
        };

    private static int ThumbRecordIndex(XRHandJoint joint)
        => XRHandJoints.TryGetDestinationIndex(joint, out int index)
            ? index
            : throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a thumb destination.");

    private static FingerHandRestGeometry CreateHandGeometry(bool mirrored)
        => new(10, Transform3D.Identity, CreateGatePassingChains(mirrored));

    /// <summary>
    /// Synthetic thumb geometry whose authored rest locals mirror the reference rig (non-identity metacarpal,
    /// identity proximal/distal) and whose rest origins carry representative — but production-inert — segment
    /// geometry: the authored binding consumes the rest bases for the neutrals and the proximal rest origin
    /// for the frame longitudinal (XR-002 TR25-TR28).
    /// </summary>
    private static ThumbRestGeometry CreateThumbGeometry(bool mirrored)
    {
        float side = mirrored ? -1.0f : 1.0f;
        Vector3 indexProximalOrigin = new(-0.03f * side, 0.0f, 0.0f);
        Vector3 metacarpalOrigin = indexProximalOrigin + new Vector3(-0.018f * side, 0.005f, -0.020f);
        Vector3 longitudinal = new Vector3(0.10f * side, -1.0f, 0.04f).Normalized();
        Vector3 proximalOrigin = metacarpalOrigin + (longitudinal * 0.040f);
        Vector3 proximalDirection = (longitudinal + new Vector3(0.0f, 0.10f, 0.34f)).Normalized();
        Vector3 distalOrigin = proximalOrigin + (proximalDirection * 0.032f);

        Quaternion metacarpalRestLocal = mirrored
            ? new Quaternion(-0.21418676f, 0.67388725f, 0.21418676f, 0.67388725f)
            : new Quaternion(-0.21418676f, -0.67388725f, -0.21418676f, 0.67388725f);

        return new ThumbRestGeometry(
            30,
            10,
            new Transform3D(Basis.Identity, metacarpalOrigin),
            new Basis(metacarpalRestLocal),
            31,
            30,
            new Transform3D(Basis.Identity, proximalOrigin),
            Basis.Identity,
            32,
            31,
            new Transform3D(Basis.Identity, distalOrigin),
            Basis.Identity);
    }

    /// <summary>
    /// The expected thumb neutral from the authored local rest basis, derived with the same tolerant polar
    /// extraction the mapping uses so <c>Delta = identity</c> composes exactly (XR-002 TR25-TR27).
    /// </summary>
    private static Quaternion ExpectedThumbRestLocal(ThumbRestGeometry thumb, XRHandJoint joint)
    {
        Basis basis = joint == XRHandJoint.ThumbMetacarpal ? thumb.MetacarpalRestLocalBasis
            : joint == XRHandJoint.ThumbProximal ? thumb.ProximalRestLocalBasis
            : joint == XRHandJoint.ThumbDistal ? thumb.DistalRestLocalBasis
            : throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a thumb destination.");

        Assert.True(ThumbRestBasisMath.TryExtractRotation(basis, "expected", out Quaternion rotation, out string error), error);
        return rotation;
    }

    private static BindingBuffers CreateBindingBuffers() => new();

    private static Vector3 Mirror(Vector3 value) => new(-value.X, value.Y, value.Z);

    private static void AssertFrameFailure(FingerRestNeutralChain[] chains, string expectedError)
    {
        bool success = FingerAnatomicalMath.TryDeriveHandFrame(
            chains,
            CreateIdentityDesiredGlobals(),
            out FingerAnatomicalFrame frame,
            out string error);

        Assert.False(success);
        Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(default, frame);
    }

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual)
    {
        float dot = Mathf.Clamp(Mathf.Abs(expected.Normalized().Dot(actual.Normalized())), 0.0f, 1.0f);
        float angle = 2.0f * Mathf.Acos(dot);
        Assert.True(angle <= RotationEpsilon, $"Expected rotations within {RotationEpsilon} radians, got {angle}.");
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual)
        => Assert.True(
            expected.Normalized().DistanceTo(actual.Normalized()) <= Epsilon,
            $"Expected {expected}, got {actual}.");

    private sealed class BindingBuffers
    {
        public Quaternion[] DestinationNeutrals =
            new Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];

        public Quaternion[] DesiredGlobalRotations =
            new Quaternion[OpticalFingerTrackingCalibrationProfile.RecordCount];

        public Vector3[] LocalHingeAxes = new Vector3[OpticalFingerTrackingCalibrationProfile.RecordCount];

        public FingerAnatomicalFrame[] LocalProximalFrames =
            new FingerAnatomicalFrame[OpticalFingerTrackingCalibrationProfile.RecordCount];

        public FingerAnatomicalFrame[] HandFrames = new FingerAnatomicalFrame[2];

        public AuthoredThumbCorrespondenceFrame[] CorrespondenceFrames = new AuthoredThumbCorrespondenceFrame[2];
    }
}
