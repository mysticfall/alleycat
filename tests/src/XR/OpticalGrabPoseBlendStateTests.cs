using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage of the pure per-hand optical-grab pose-arbitration blend state machine (XR-002 UR16,
/// TR30-TR31; INTR-002 R61): phase transitions, progress monotonicity, the whole-hand-loss clock freeze,
/// and the exact endpoint semantics of the commit and release blend evaluation.
/// </summary>
public sealed class OpticalGrabPoseBlendStateTests
{
    private const float RotationToleranceRadians = 1e-5f;

    private const float WindowSeconds = 0.5f;

    private static Quaternion RotationAboutX(float radians)
        => new(Vector3.Right, radians);

    /// <summary>Commit blends advance monotonically, then await owner readiness before zero-write suppression.</summary>
    [Fact]
    public void BeginCommitBlend_AdvancesMonotonicallyAndCompletesExactlyOnce()
    {
        OpticalGrabPoseBlendState state = CreateCommitBlend();
        float previousProgress = 0f;
        int completions = 0;

        for (int frame = 0; frame < 120; frame++)
        {
            if (state.Advance(1f / 60f, clockAdvances: true))
            {
                completions++;
            }

            Assert.True(
                state.Progress >= previousProgress - 1e-6f,
                $"Progress must stay monotonic; regressed from {previousProgress:R} to {state.Progress:R}.");
            previousProgress = state.Progress;
        }

        Assert.Equal(1, completions);
        Assert.Equal(OpticalGrabPoseBlendPhase.CommitBlend, state.Phase);
        Assert.True(state.IsCommitBlendMinimumSatisfied);
        Assert.False(state.IsWriteSuppressed);
        Assert.Equal(WindowSeconds, state.ElapsedSeconds, 5);
        Assert.Equal(1f, state.Progress, 6);
        Assert.True(state.TrySuppressCommitBlend());
        Assert.Equal(OpticalGrabPoseBlendPhase.HeldSuppressed, state.Phase);
        Assert.True(state.IsWriteSuppressed);
    }

    /// <summary>Pending assistance settles at the candidate reference but remains write-enabled until an actual commit.</summary>
    [Fact]
    public void PendingAssistance_CompletesAtReferenceAndRemainsActiveForContinuousWrites()
    {
        Quaternion authored = RotationAboutX(Mathf.DegToRad(75f));
        var state = new OpticalGrabPoseBlendState();
        state.BeginPendingAssistance(CreateUniformPoses(Quaternion.Identity), CreateUniformPoses(authored), WindowSeconds);

        Assert.True(state.Advance(WindowSeconds, clockAdvances: true));
        Assert.Equal(OpticalGrabPoseBlendPhase.PendingAssistance, state.Phase);
        Assert.False(state.IsWriteSuppressed);
        Assert.Equal(authored, state.EvaluatePendingAssistance(0));

        // The completed pending state remains stable and write-enabled on later frames; only held owns zero writes.
        Assert.False(state.Advance(1f / 60f, clockAdvances: true));
        Assert.Equal(authored, state.EvaluatePendingAssistance(0));
    }

    /// <summary>A pending-to-held handoff starts from the assisted output and suppresses only after readiness.</summary>
    [Fact]
    public void PendingAssistance_ToHeldHandoff_UsesAssistedOutputAndThenSuppressesWrites()
    {
        Quaternion tracked = RotationAboutX(Mathf.DegToRad(-20f));
        Quaternion authored = RotationAboutX(Mathf.DegToRad(65f));
        var state = new OpticalGrabPoseBlendState();
        state.BeginPendingAssistance(CreateUniformPoses(tracked), CreateUniformPoses(authored), WindowSeconds);
        _ = state.Advance(WindowSeconds * 0.5f, clockAdvances: true);
        Quaternion assisted = state.EvaluatePendingAssistance(0);

        state.BeginCommitBlend(CreateUniformPoses(assisted), CreateUniformPoses(authored), WindowSeconds);
        Assert.Equal(assisted, state.EvaluateCommitBlend(0));
        _ = state.Advance(WindowSeconds, clockAdvances: true);

        Assert.Equal(OpticalGrabPoseBlendPhase.CommitBlend, state.Phase);
        Assert.True(state.IsCommitBlendMinimumSatisfied);
        Assert.True(state.TrySuppressCommitBlend());
        Assert.True(state.IsWriteSuppressed);
        Assert.Equal(authored, state.EvaluateCommitBlend(0));
    }

    /// <summary>Cancellation returns smoothly from both a partial and a fully settled pending assisted pose.</summary>
    [Theory]
    [InlineData(0.25f)]
    [InlineData(1.0f)]
    public void PendingAssistance_CancellationBlendsFromCurrentAssistedOutput(float pendingProgress)
    {
        Quaternion tracked = RotationAboutX(Mathf.DegToRad(-30f));
        Quaternion authored = RotationAboutX(Mathf.DegToRad(70f));
        Quaternion recoveredTracking = RotationAboutX(Mathf.DegToRad(15f));
        var state = new OpticalGrabPoseBlendState();
        state.BeginPendingAssistance(CreateUniformPoses(tracked), CreateUniformPoses(authored), WindowSeconds);
        _ = state.Advance(WindowSeconds * pendingProgress, clockAdvances: true);
        Quaternion assisted = state.EvaluatePendingAssistance(0);

        state.BeginReleaseBlend(CreateUniformPoses(assisted), WindowSeconds);
        Assert.Equal(assisted, state.EvaluateReleaseBlend(0, recoveredTracking));
        _ = state.Advance(WindowSeconds, clockAdvances: true);

        Assert.Equal(OpticalGrabPoseBlendPhase.Tracking, state.Phase);
        Assert.Equal(
            FingerRetargetingMath.StabiliseRotationHemisphere(recoveredTracking, assisted),
            state.EvaluateReleaseBlend(0, recoveredTracking));
    }

    /// <summary>Loss cancellation freezes the assisted output and clock until valid current tracking returns.</summary>
    [Fact]
    public void PendingLossCancellation_FreezesThenResumesTowardCurrentTrackedTarget()
    {
        Quaternion authored = RotationAboutX(Mathf.DegToRad(80f));
        Quaternion recoveredTracking = RotationAboutX(Mathf.DegToRad(-40f));
        var state = new OpticalGrabPoseBlendState();
        state.BeginPendingAssistance(CreateUniformPoses(Quaternion.Identity), CreateUniformPoses(authored), WindowSeconds);
        _ = state.Advance(WindowSeconds * 0.4f, clockAdvances: true);
        Quaternion assisted = state.EvaluatePendingAssistance(0);
        state.BeginReleaseBlend(CreateUniformPoses(assisted), WindowSeconds);

        for (int frame = 0; frame < 20; frame++)
        {
            Assert.False(state.Advance(1f / 60f, clockAdvances: false));
            Assert.Equal(assisted, state.EvaluateReleaseBlend(0, recoveredTracking));
        }

        _ = state.Advance(WindowSeconds, clockAdvances: true);
        Assert.Equal(OpticalGrabPoseBlendPhase.Tracking, state.Phase);
        Assert.Equal(
            FingerRetargetingMath.StabiliseRotationHemisphere(recoveredTracking, assisted),
            state.EvaluateReleaseBlend(0, recoveredTracking));
    }

    /// <summary>Whole-hand tracking loss freezes the release blend clock at its current value until samples recover, then completes into tracking.</summary>
    [Fact]
    public void WholeHandLoss_FreezesBlendClockUntilSamplesRecover()
    {
        OpticalGrabPoseBlendState state = CreateReleaseBlend();

        // Partially advance the blend, then lose the whole hand: the clock freezes at the current value.
        _ = state.Advance(0.1f, clockAdvances: true);
        float frozenProgress = state.Progress;

        for (int frame = 0; frame < 30; frame++)
        {
            Assert.False(state.Advance(1f / 60f, clockAdvances: false));
            Assert.Equal(frozenProgress, state.Progress, 6);
            Assert.Equal(OpticalGrabPoseBlendPhase.ReleaseBlend, state.Phase);
        }

        // Recovery resumes blending and eventually completes into normal tracking.
        int completions = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            if (state.Advance(1f / 60f, clockAdvances: true))
            {
                completions++;
            }
        }

        Assert.Equal(1, completions);
        Assert.Equal(OpticalGrabPoseBlendPhase.Tracking, state.Phase);
        Assert.False(state.IsWriteSuppressed);
    }

    /// <summary>A zero frame delta never advances the blend clock.</summary>
    [Fact]
    public void ZeroDelta_DoesNotAdvanceTheBlendClock()
    {
        OpticalGrabPoseBlendState state = CreateCommitBlend();

        for (int frame = 0; frame < 10; frame++)
        {
            Assert.False(state.Advance(0f, clockAdvances: true));
            Assert.Equal(0f, state.ElapsedSeconds, 6);
        }
    }

    /// <summary>A non-positive window duration completes the blend on its first advance.</summary>
    [Fact]
    public void NonPositiveDuration_CompletesOnFirstAdvance()
    {
        var state = new OpticalGrabPoseBlendState();
        Span<Quaternion> poses = CreateUniformPoses(RotationAboutX(1.0f));
        state.BeginCommitBlend(poses, poses, durationSeconds: 0f);

        Assert.Equal(1f, state.Progress, 6);
        Assert.True(state.Advance(1f / 60f, clockAdvances: true));
        Assert.Equal(OpticalGrabPoseBlendPhase.CommitBlend, state.Phase);
        Assert.True(state.IsCommitBlendMinimumSatisfied);
        Assert.True(state.TrySuppressCommitBlend());
        Assert.Equal(OpticalGrabPoseBlendPhase.HeldSuppressed, state.Phase);
    }

    /// <summary>The commit evaluation follows the slerp arc and lands byte-exactly on the hemisphere-aligned authored target at completion.</summary>
    [Fact]
    public void EvaluateCommitBlend_FollowsSlerpTrajectoryAndLandsExactlyOnAuthoredTarget()
    {
        Quaternion from = Quaternion.Identity;
        Quaternion authored = RotationAboutX(Mathf.DegToRad(90f));
        OpticalGrabPoseBlendState state = CreateCommitBlend(from, authored);

        // Halfway through the window the rotation sits at half the arc.
        _ = state.Advance(WindowSeconds * 0.5f, clockAdvances: true);
        Quaternion halfway = state.EvaluateCommitBlend(0);
        Assert.True(
            halfway.AngleTo(RotationAboutX(Mathf.DegToRad(45f))) < RotationToleranceRadians,
            $"Expected the halfway blend near 45°; got {Mathf.RadToDeg(halfway.AngleTo(Quaternion.Identity)):R}°.");

        // Completing the window lands byte-exactly on the hemisphere-aligned authored target.
        _ = state.Advance(WindowSeconds, clockAdvances: true);
        Quaternion expected = FingerRetargetingMath.StabiliseRotationHemisphere(authored, from);
        Assert.Equal(expected, state.EvaluateCommitBlend(0));
    }

    /// <summary>A negated authored target still blends along the short arc.</summary>
    [Fact]
    public void EvaluateCommitBlend_AlignsHemisphereOfNegatedAuthoredTarget()
    {
        Quaternion from = Quaternion.Identity;
        Quaternion authored = RotationAboutX(Mathf.DegToRad(90f));
        OpticalGrabPoseBlendState state = CreateCommitBlend(from, -authored);

        _ = state.Advance(WindowSeconds * 0.5f, clockAdvances: true);
        Quaternion halfway = state.EvaluateCommitBlend(0);
        Assert.True(
            halfway.AngleTo(RotationAboutX(Mathf.DegToRad(45f))) < RotationToleranceRadians,
            "A negated authored target must still slerp along the short arc.");
    }

    /// <summary>The release evaluation blends from the captured pose to the live target and lands byte-exactly on the aligned target at completion.</summary>
    [Fact]
    public void EvaluateReleaseBlend_BlendsFromCapturedPoseToLiveTargetExactly()
    {
        Quaternion authored = RotationAboutX(Mathf.DegToRad(80f));
        Quaternion liveTarget = RotationAboutX(Mathf.DegToRad(-30f));
        var state = new OpticalGrabPoseBlendState();
        state.BeginReleaseBlend(CreateUniformPoses(authored), WindowSeconds);

        _ = state.Advance(WindowSeconds * 0.5f, clockAdvances: true);
        Quaternion halfway = state.EvaluateReleaseBlend(0, liveTarget);
        float expectedHalfway = Mathf.DegToRad(25f);
        Assert.True(
            halfway.AngleTo(RotationAboutX(expectedHalfway)) < RotationToleranceRadians,
            $"Expected the halfway release blend near 25°; got " +
                $"{Mathf.RadToDeg(halfway.AngleTo(Quaternion.Identity)):R}°.");

        _ = state.Advance(WindowSeconds, clockAdvances: true);
        Quaternion expected = FingerRetargetingMath.StabiliseRotationHemisphere(liveTarget, authored);
        Assert.Equal(expected, state.EvaluateReleaseBlend(0, liveTarget));
    }

    /// <summary>A release begun after settled suppression starts exactly from the captured authored reference pose.</summary>
    [Fact]
    public void BeginReleaseBlend_AfterSuppression_CapturesAuthoredReferenceFromPose()
    {
        Quaternion authored = RotationAboutX(Mathf.DegToRad(50f));
        OpticalGrabPoseBlendState state = CreateCommitBlend(Quaternion.Identity, authored);

        while (!state.Advance(0.1f, clockAdvances: true))
        {
        }

        Assert.True(state.TrySuppressCommitBlend());
        Assert.Equal(OpticalGrabPoseBlendPhase.HeldSuppressed, state.Phase);
        Assert.True(state.HasAuthoredReference);

        // The modifier hands the authored reference in as the release start pose; blending resumes from it.
        // (Compared componentwise: AngleTo carries ~0.04° of noise on slightly non-unit test quaternions.)
        state.BeginReleaseBlend(state.AuthoredReferencePoses.ToArray(), WindowSeconds);
        Assert.Equal(OpticalGrabPoseBlendPhase.ReleaseBlend, state.Phase);
        Quaternion start = state.EvaluateReleaseBlend(0, RotationAboutX(Mathf.DegToRad(10f)));
        Assert.Equal(authored, start);
    }

    /// <summary>Resetting to tracking clears the window and the authored reference.</summary>
    [Fact]
    public void ResetToTracking_ClearsWindowAndReference()
    {
        OpticalGrabPoseBlendState state = CreateCommitBlend();
        state.ResetToTracking();

        Assert.Equal(OpticalGrabPoseBlendPhase.Tracking, state.Phase);
        Assert.Equal(0f, state.ElapsedSeconds, 6);
        Assert.Equal(0f, state.DurationSeconds, 6);
        Assert.False(state.HasAuthoredReference);
        Assert.False(state.Advance(1f, clockAdvances: true));
    }

    /// <summary>Buffers of the wrong length are rejected.</summary>
    [Fact]
    public void Buffers_OfWrongLength_AreRejected()
    {
        var state = new OpticalGrabPoseBlendState();
        var shortPoses = new Quaternion[OpticalGrabPoseBlendState.FingerCount - 1];
        var validPoses = new Quaternion[OpticalGrabPoseBlendState.FingerCount];

        _ = Assert.Throws<ArgumentException>(
            () => state.BeginCommitBlend(shortPoses, validPoses, WindowSeconds));
        _ = Assert.Throws<ArgumentException>(
            () => state.BeginReleaseBlend(shortPoses, WindowSeconds));
    }

    private static OpticalGrabPoseBlendState CreateCommitBlend()
        => CreateCommitBlend(Quaternion.Identity, RotationAboutX(Mathf.DegToRad(90f)));

    private static OpticalGrabPoseBlendState CreateCommitBlend(Quaternion from, Quaternion authored)
    {
        var state = new OpticalGrabPoseBlendState();
        state.BeginCommitBlend(CreateUniformPoses(from), CreateUniformPoses(authored), WindowSeconds);
        return state;
    }

    private static OpticalGrabPoseBlendState CreateReleaseBlend()
    {
        var state = new OpticalGrabPoseBlendState();
        state.BeginReleaseBlend(CreateUniformPoses(RotationAboutX(1.0f)), WindowSeconds);
        return state;
    }

    private static Quaternion[] CreateUniformPoses(Quaternion rotation)
    {
        var poses = new Quaternion[OpticalGrabPoseBlendState.FingerCount];
        Array.Fill(poses, rotation);
        return poses;
    }
}
