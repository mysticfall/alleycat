using AlleyCat.Control.Hands;
using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Control.Hands;

/// <summary>
/// Unit coverage of the animation-derived power-grip derivation and recognition aggregate (XR-002 TR48;
/// CTRL-002 TR12-TR13): synthetic reference poses and projected live poses verify progress semantics, the
/// weighted aggregate's tolerance and monotonicity, invalid-destination exclusion with fail-closed
/// insufficiency, and quaternion hemisphere sign-invariance.
/// </summary>
public sealed class PowerGripRecognitionTests
{
    private const float ScoreTolerance = 1e-3f;

    private static readonly PowerGripRecognitionSettings _settings = PowerGripRecognitionSettings.Default;

    /// <summary>The production comfort default recognises a candidate-specific closure at 0.75 without changing release hysteresis or stability.</summary>
    [Fact]
    public void DefaultSettings_UseComfortEntryThresholdWhilePreservingSafetyHysteresisAndStability()
    {
        Assert.Equal(0.75f, _settings.GrabThreshold, 6);
        Assert.Equal(0.55f, _settings.ReleaseThreshold, 6);
        Assert.Equal(0.10f, _settings.StabilitySeconds, 6);
        Assert.True(_settings.ReleaseThreshold < _settings.GrabThreshold);
    }

    /// <summary>Settings reject a release threshold at or above the grab threshold — hysteresis is mandatory.</summary>
    [Fact]
    public void Settings_RejectReleaseAtOrAboveGrab()
    {
        _ = Assert.Throws<ArgumentException>(() => new PowerGripRecognitionSettings(grabThreshold: 0.8f, releaseThreshold: 0.8f));
        _ = Assert.Throws<ArgumentException>(() => new PowerGripRecognitionSettings(grabThreshold: 0.8f, releaseThreshold: 0.9f));
        _ = Assert.Throws<ArgumentException>(() => new PowerGripRecognitionSettings(grabThreshold: float.NaN));
        _ = Assert.Throws<ArgumentException>(() => new PowerGripRecognitionSettings(stabilitySeconds: 0.0f));
        _ = Assert.Throws<ArgumentException>(() => new PowerGripRecognitionSettings(minimumValidChainWeightFraction: 0.0f));
    }

    /// <summary>
    /// Derivation measures each destination's reference articulation from the effective neutral: identity
    /// neutrals with <c>rotation(+X, 60°)</c> references derive axis <c>+X</c>, angle <c>60°</c>, and equal
    /// chain weights.
    /// </summary>
    [Fact]
    public void TryDerive_ExtractsReferenceAxesAndWeights()
    {
        float referenceAngle = Mathf.DegToRad(60.0f);
        (PowerGripProfile profile, _) = DeriveUniformProfile(referenceAngle);

        for (int index = 0; index < 15; index++)
        {
            Assert.True(profile.ReferenceAxes[index].AngleTo(Vector3.Right) <= 1e-4f, $"axis {index}");
            Assert.Equal(referenceAngle, profile.ReferenceAnglesRadians[index], 4);
        }

        for (int chainIndex = 0; chainIndex < PowerGripRecognition.ChainCount; chainIndex++)
        {
            Assert.Equal(0.2f, profile.ChainWeights[chainIndex], 4);
        }

        Assert.Equal(1.0f, profile.FeaturedChainWeightTotal, 4);
    }

    /// <summary>Negated reference quaternions derive the identical profile — hemisphere alignment is applied first.</summary>
    [Fact]
    public void TryDerive_IsInvariantToReferenceQuaternionSign()
    {
        float referenceAngle = Mathf.DegToRad(60.0f);
        (PowerGripProfile plain, AuthoredHandPoseSideReference reference) = DeriveUniformProfile(referenceAngle);

        var negated = new Quaternion[15];
        for (int index = 0; index < 15; index++)
        {
            Quaternion pose = reference.Poses[index];
            negated[index] = new Quaternion(-pose.X, -pose.Y, -pose.Z, -pose.W);
        }

        var negatedReference = new AuthoredHandPoseSideReference(LimbSide.Left, reference.ResourcePath, negated);

        Assert.True(PowerGripProfile.TryDerive(
            negatedReference,
            Repeat(Quaternion.Identity, 15),
            _settings,
            out PowerGripProfile negatedProfile,
            out _));

        for (int index = 0; index < 15; index++)
        {
            Assert.Equal(plain.ReferenceAnglesRadians[index], negatedProfile.ReferenceAnglesRadians[index], 6);
            Assert.True(plain.ReferenceAxes[index].AngleTo(negatedProfile.ReferenceAxes[index]) <= 1e-5f);
        }
    }

    /// <summary>A reference at the neutral everywhere fails closed — no articulation means no grip definition.</summary>
    [Fact]
    public void TryDerive_FailsClosedOnNeutralReference()
    {
        AuthoredHandPoseSideReference neutralReference = new(LimbSide.Left, "test://neutral", Repeat(Quaternion.Identity, 15));

        Assert.False(PowerGripProfile.TryDerive(
            neutralReference,
            Repeat(Quaternion.Identity, 15),
            _settings,
            out _,
            out string error));
        Assert.Contains("no featured articulation", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A chain whose reference stays at the neutral is excluded from the features and the remaining chain
    /// weights renormalise over the featured chains.
    /// </summary>
    [Fact]
    public void TryDerive_ExcludesUnarticulatedChainsAndRenormalises()
    {
        float referenceAngle = Mathf.DegToRad(60.0f);
        var poses = new Quaternion[15];
        for (int index = 0; index < 15; index++)
        {
            // The thumb chain (destinations 0-2) stays at the neutral.
            poses[index] = index < 3
                ? Quaternion.Identity
                : new Quaternion(Vector3.Right, referenceAngle);
        }

        AuthoredHandPoseSideReference thumbNeutral = new(LimbSide.Left, "test://thumb-neutral", poses);

        Assert.True(PowerGripProfile.TryDerive(
            thumbNeutral,
            Repeat(Quaternion.Identity, 15),
            _settings,
            out PowerGripProfile profile,
            out _));

        Assert.Equal(0.0f, profile.ChainWeights[0], 6);
        for (int chainIndex = 1; chainIndex < PowerGripRecognition.ChainCount; chainIndex++)
        {
            Assert.Equal(0.25f, profile.ChainWeights[chainIndex], 4);
        }
    }

    /// <summary>The live neutral pose scores ~0 — the calibrated neutral is progress zero.</summary>
    [Fact]
    public void Evaluate_AtTheNeutral_ScoresZero()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: 0.0f, profile);

        float score = EvaluateScore(profile, poses, out bool sufficient);
        Assert.True(sufficient);
        Assert.True(MathF.Abs(score) <= ScoreTolerance, $"Expected ~0 at the neutral, got {score:R}.");
    }

    /// <summary>The reference articulation scores exactly 1 — progress 1 by definition — crossing the grab threshold.</summary>
    [Fact]
    public void Evaluate_AtTheReferenceArticulation_ScoresOne()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: 1.0f, profile);

        float score = EvaluateScore(profile, poses, out bool sufficient);
        Assert.True(sufficient);
        Assert.Equal(1.0f, score, 4);
        Assert.True(score >= _settings.GrabThreshold);
    }

    /// <summary>Over-clench (progress 1.6) scores above 1 — never less closed than the reference.</summary>
    [Fact]
    public void Evaluate_OverClench_ScoresAboveTheReference()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: 1.6f, profile);

        float score = EvaluateScore(profile, poses, out bool sufficient);
        Assert.True(sufficient);
        Assert.True(score > 1.0f, $"Over-clench must stay more closed than the reference; got {score:R}.");
        Assert.True(score >= _settings.GrabThreshold);
    }

    /// <summary>The score is monotonic in the closing direction across the full progress range.</summary>
    [Fact]
    public void Evaluate_IsMonotonicInClosingDirection()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));

        float previous = float.NegativeInfinity;
        foreach (float progress in new[] { -0.4f, -0.1f, 0.0f, 0.25f, 0.5f, 0.75f, 1.0f, 1.4f })
        {
            float score = EvaluateScore(profile, BuildLivePoses(progress, profile), out _);
            Assert.True(score > previous, $"Score must increase with progress; {progress:R} gave {score:R}.");
            previous = score;
        }
    }

    /// <summary>
    /// A single extended finger does not drop the weighted aggregate below the release threshold with the
    /// default equal chain weights (XR-002 TR48; CTRL-002 UR4).
    /// </summary>
    [Fact]
    public void Evaluate_SingleExtendedFinger_StaysAboveTheReleaseThreshold()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));

        // Four chains at reference articulation, the index chain at the neutral.
        var poses = new OpticalFingerProjectedPose[15];
        for (int index = 0; index < 15; index++)
        {
            float progress = index is >= 3 and <= 5 ? 0.0f : 1.0f;
            poses[index] = LivePose(profile, index, progress);
        }

        float score = EvaluateScore(profile, poses, out bool sufficient);
        Assert.True(sufficient);
        Assert.True(
            score > _settings.ReleaseThreshold,
            $"A single extended finger must not release; aggregate {score:R} must exceed the release threshold " +
            $"{_settings.ReleaseThreshold:R}.");
    }

    /// <summary>A fully open hand (negative progress, opening away from the reference direction) scores below release.</summary>
    [Fact]
    public void Evaluate_FullyOpenHand_ScoresBelowTheReleaseThreshold()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: -0.3f, profile);

        float score = EvaluateScore(profile, poses, out bool sufficient);
        Assert.True(sufficient);
        Assert.True(score <= _settings.ReleaseThreshold, $"Expected an open hand below release; got {score:R}.");
    }

    /// <summary>
    /// Invalid destinations are excluded with renormalised weights: per-chain invalid joints leave the chain
    /// progress — and therefore the aggregate — unchanged (XR-002 TR48).
    /// </summary>
    [Fact]
    public void Evaluate_ExcludesInvalidDestinationsWithRenormalisedWeights()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] allValid = BuildLivePoses(progressPerDestination: 1.0f, profile);

        var withInvalid = (OpticalFingerProjectedPose[])allValid.Clone();
        // Invalidate one destination in every chain (the intra-chain weight renormalises).
        foreach ((int start, int _) in Enumerable.Range(0, PowerGripRecognition.ChainCount)
                     .Select(PowerGripRecognition.GetChainDestinationRange))
        {
            withInvalid[start] = OpticalFingerProjectedPose.Frozen(OpticalFingerProjectionStatus.FrozenInvalidSource);
        }

        float validScore = EvaluateScore(profile, allValid, out bool validSufficient);
        float excludedScore = EvaluateScore(profile, withInvalid, out bool excludedSufficient);
        Assert.True(validSufficient);
        Assert.True(excludedSufficient);
        Assert.Equal(validScore, excludedScore, 3);
    }

    /// <summary>
    /// When too few chains remain valid the evaluation reports insufficient validity — fail closed, no partial
    /// guess (XR-002 TR48).
    /// </summary>
    [Fact]
    public void Evaluate_TooFewValidChains_ReportsInsufficientValidity()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: 1.0f, profile);

        // Invalidate four of the five chains: 0.2 of the featured weight remains, below the 0.5 floor.
        foreach ((int start, int length) in Enumerable.Range(1, 4).Select(PowerGripRecognition.GetChainDestinationRange))
        {
            for (int offset = 0; offset < length; offset++)
            {
                poses[start + offset] = OpticalFingerProjectedPose.Frozen(OpticalFingerProjectionStatus.FrozenInvalidSource);
            }
        }

        _ = EvaluateScore(profile, poses, out bool sufficient);
        Assert.False(sufficient);
    }

    /// <summary>Negated live quaternions evaluate identically — every comparison is hemisphere-aligned (double cover).</summary>
    [Fact]
    public void Evaluate_IsInvariantToLiveQuaternionSign()
    {
        (PowerGripProfile profile, _) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: 0.7f, profile);
        var negated = (OpticalFingerProjectedPose[])poses.Clone();
        for (int index = 0; index < negated.Length; index++)
        {
            Quaternion rotation = negated[index].Rotation;
            negated[index] = negated[index] with
            {
                Rotation = new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W),
            };
        }

        float plainScore = EvaluateScore(profile, poses, out bool plainSufficient);
        float negatedScore = EvaluateScore(profile, negated, out bool negatedSufficient);
        Assert.Equal(plainSufficient, negatedSufficient);
        Assert.Equal(plainScore, negatedScore, 5);
    }

    /// <summary>The strategy seam derives and evaluates the power grip end to end through the generic interface.</summary>
    [Fact]
    public void Strategy_DerivesAndEvaluatesThroughTheSeam()
    {
        Assert.True(GripRecognitionStrategies.TryResolve(
            GripRecognitionStrategies.PowerGrip,
            out IGripRecognitionStrategy strategy,
            out string resolveError), resolveError);
        Assert.Equal(GripRecognitionStrategies.PowerGrip, strategy.Name);

        (PowerGripProfile expectedProfile, AuthoredHandPoseSideReference reference) = DeriveUniformProfile(Mathf.DegToRad(60.0f));
        Assert.True(strategy.TryDeriveProfile(
            reference,
            Repeat(Quaternion.Identity, 15),
            _settings,
            out IGripRecognitionProfile profile,
            out string deriveError), deriveError);

        PowerGripProfile powerGripProfile = Assert.IsType<PowerGripProfile>(profile);
        Assert.Equal(expectedProfile.Side, powerGripProfile.Side);
        Assert.Equal(strategy.Name, powerGripProfile.StrategyName);

        OpticalFingerProjectedPose[] poses = BuildLivePoses(progressPerDestination: 1.0f, powerGripProfile);
        Assert.True(strategy.TryEvaluate(profile, poses, out GripRecognitionEvaluation evaluation));
        Assert.True(evaluation.SufficientValidity);
        Assert.Equal(1.0f, evaluation.Score, 4);

        // A profile the strategy did not derive is rejected, not coerced.
        Assert.False(strategy.TryEvaluate(new ForeignProfile(), poses, out _));
    }

    /// <summary>An unknown strategy identifier fails explicitly with no fallback to power grip.</summary>
    [Fact]
    public void UnknownStrategy_FailsExplicitly()
    {
        Assert.False(GripRecognitionStrategies.TryResolve("precision", out IGripRecognitionStrategy strategy, out string error));
        Assert.Null(strategy);
        Assert.Contains("precision", error, StringComparison.Ordinal);
        Assert.Contains(GripRecognitionStrategies.PowerGrip, error, StringComparison.Ordinal);

        Assert.False(GripRecognitionStrategies.TryResolve(string.Empty, out strategy, out _));
        Assert.Null(strategy);
    }

    private static Quaternion[] Repeat(Quaternion value, int count)
    {
        var values = new Quaternion[count];
        Array.Fill(values, value);
        return values;
    }

    /// <summary>Builds a uniform reference: every destination articulates by the same angle about +X from identity neutrals.</summary>
    private static (PowerGripProfile Profile, AuthoredHandPoseSideReference Reference) DeriveUniformProfile(
        float referenceAngleRadians)
    {
        var poses = new Quaternion[15];
        for (int index = 0; index < 15; index++)
        {
            poses[index] = new Quaternion(Vector3.Right, referenceAngleRadians);
        }

        AuthoredHandPoseSideReference reference = new(LimbSide.Left, "test://uniform", poses);
        Assert.True(PowerGripProfile.TryDerive(
            reference,
            Repeat(Quaternion.Identity, 15),
            _settings,
            out PowerGripProfile profile,
            out string error), error);

        return (profile, reference);
    }

    /// <summary>
    /// Builds live projected poses at a uniform per-destination progress: <c>D_j = N_j × rotation(a_j,
    /// progress × alpha_j)</c> from the profile's own features.
    /// </summary>
    private static OpticalFingerProjectedPose[] BuildLivePoses(float progressPerDestination, PowerGripProfile profile)
    {
        var poses = new OpticalFingerProjectedPose[15];
        for (int index = 0; index < 15; index++)
        {
            poses[index] = LivePose(profile, index, progressPerDestination);
        }

        return poses;
    }

    private static OpticalFingerProjectedPose LivePose(PowerGripProfile profile, int destinationIndex, float progress)
        => new(
            OpticalFingerProjectionStatus.Valid,
            profile.EffectiveNeutrals[destinationIndex]
                * new Quaternion(
                    profile.ReferenceAxes[destinationIndex],
                    progress * profile.ReferenceAnglesRadians[destinationIndex]));

    private static float EvaluateScore(
        PowerGripProfile profile,
        ReadOnlySpan<OpticalFingerProjectedPose> poses,
        out bool sufficientValidity)
    {
        float[] destinationProgress = new float[15];
        float[] chainProgress = new float[PowerGripRecognition.ChainCount];

        Assert.True(PowerGripRecognition.TryEvaluate(
            profile,
            poses,
            destinationProgress,
            chainProgress,
            out float score,
            out sufficientValidity));

        return score;
    }

    private sealed class ForeignProfile : IGripRecognitionProfile
    {
        public LimbSide Side => LimbSide.Left;

        public string StrategyName => "foreign";

        public PowerGripRecognitionSettings Settings => _settings;
    }
}
