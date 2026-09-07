using System.Diagnostics;
using AlleyCat.IK;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.XR;

/// <summary>
/// Integration coverage of per-hand optical-grab pose arbitration inside <see cref="OpticalFingerTrackingModifier" />
/// (XR-002 UR16, TR30-TR32, OG11-OG12; INTR-003 TR19-TR21; INTR-002 R60-61): the commit blend-in window, zero
/// writes while held, the release blend-out, whole-hand-loss freeze mid-blend, and opposite-hand isolation.
/// </summary>
/// <remarks>
/// <para>
/// The fixture combines the deterministic mock XR runtime with a real <see cref="OpticalFingerTrackingModifier" />
/// bound to the synthetic canonical finger skeleton — published to authored animations as
/// <c>%GeneralSkeleton</c> — and a two-sided <see cref="HandPoseBehaviour" /> rig whose AnimationTree mirrors the
/// production hand-pose topology (Reset upstream, filtered per-hand <c>AnimationNodeBlend2</c> stages), so the
/// authored grab pose genuinely owns the bones once the modifier stops writing.
/// </para>
/// <para>
/// Grabs are driven directly through <c>BeginGrab</c>/<c>SettleAndCommit</c>/<c>Release</c> rather than the
/// recognition coordinator: arbitration publication is the seam under test, and direct control keeps injected
/// articulation changes from releasing anything — which is exactly what the zero-write scenarios need. The blend
/// window is stretched to 1 s through the exported duration in the transition-observation scenarios; assertions
/// remain behavioural (bone values, stability, bounded per-frame movement) rather than re-deriving the blend
/// maths.
/// </para>
/// </remarks>
public sealed class OpticalGrabPoseArbitrationIntegrationTests
{
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string GrabBallAnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";

    private const int TrackedJointCount = 20;

    private const int FingerCount = OpticalFingerProjectionBinding.FingerBonesPerSide;

    private const float LeftWristYawRadians = 0.8f;

    private const float RightWristYawRadians = -1.1f;

    /// <summary>Comparison tolerance for near-exact rotation agreement (radians).</summary>
    private const float RotationToleranceRadians = 1e-3f;

    /// <summary>
    /// An optical pending approach visibly moves only its own hand toward the immutable candidate reference while
    /// the item remains pending; the other hand continues to present live projected tracking.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalPendingGrab_AssistsCandidateReferenceWhileOppositeHandRemainsLive()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Modifier.GrabPoseBlendDurationSeconds = 1.0f;
            _ = fixture.AddRightBall();
            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, flexScale: 0.25f);
            Quaternion[] trackedBeforePending = fixture.CaptureFingerRotations(LimbSide.Right);
            Quaternion[] authored = ArbitrationFixture.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath);

            _ = fixture.RightHand.BeginGrab(HandGrabInputSource.Optical);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(
                OpticalGrabPresentationState.PendingAssistance,
                fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).State);

            fixture.InjectTrackedHandPose(LimbSide.Left, flexScale: 1.3f);
            await WaitForSecondsAsync(sceneTree, 0.35);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(OpticalGrabPoseBlendPhase.PendingAssistance, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            Quaternion[] assisted = fixture.CaptureFingerRotations(LimbSide.Right);
            Assert.True(
                MaxAngle(assisted, trackedBeforePending) > 0.1f,
                "The pending hand must visibly leave its prior live-tracked output.");
            Assert.True(
                MaxAngle(assisted, authored) < MaxAngle(trackedBeforePending, authored),
                "The pending hand must approach the candidate authored reference rather than remain live tracking.");
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Left),
                fixture.ProjectCurrentPose(LimbSide.Left, 1.3f),
                RotationToleranceRadians,
                "opposite hand during pending assistance");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 1: after the commit blend window completes, the committed hand's finger bones stay byte-stable
    /// at the authored sampled values regardless of injected tracked articulation — proving zero modifier writes
    /// and that the hidden raw samples never move the fingers (XR-002 TR31; OG11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CommittedHand_AfterBlendWindow_StaysByteStableAtAuthoredPoseDespiteTrackedChanges()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Modifier.GrabPoseBlendDurationSeconds = 1.0f;
            GrabbableNode ball = fixture.AddRightBall();

            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, flexScale: 0.55f);
            await BeginGrabAndCommitAsync(sceneTree, fixture, LimbSide.Right);

            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.HeldSuppressed,
                    budgetSeconds: 5.0),
                "Expected the commit blend window to complete into zero-write suppression.");

            Quaternion[] authored = ArbitrationFixture.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath);

            // Let the AnimationTree take over for a couple of frames, then use its own output as the stability
            // baseline (the modifier's final normalised write may differ from the raw authored keys by a float
            // ULP; the tree's repeated application is deterministic).
            await WaitForFramesAsync(sceneTree, 3);
            Quaternion[] settled = fixture.CaptureFingerRotations(LimbSide.Right);
            AssertMaxAngleAtMost(settled, authored, RotationToleranceRadians, "settled held pose vs authored keys");

            // Drastic hidden articulation changes — over-clench and full open — must not move the fingers and
            // must not release anything (the grab is released only through the explicit Release path here).
            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 2.6f);
            await WaitForSecondsAsync(sceneTree, 0.25);
            Quaternion[] afterOverClench = fixture.CaptureFingerRotations(LimbSide.Right);
            AssertByteStable(afterOverClench, settled, "over-clench injection");

            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 0.0f);
            await WaitForSecondsAsync(sceneTree, 0.25);
            AssertByteStable(fixture.CaptureFingerRotations(LimbSide.Right), settled, "full-open injection");

            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 0.0f);
            await WaitForSecondsAsync(sceneTree, 0.25);
            AssertByteStable(fixture.CaptureFingerRotations(LimbSide.Right), settled, "full-open injection");

            Assert.Equal(OpticalGrabPoseBlendPhase.HeldSuppressed, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(ball, fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 2: the opposite hand keeps following injected tracked articulation throughout — during the
    /// committed hand's blend window and after its suppression began (XR-002 TR30; INTR-003 TR19).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OppositeHand_KeepsTrackingDuringAndAfterCommitBlend()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Modifier.GrabPoseBlendDurationSeconds = 1.0f;
            _ = fixture.AddRightBall();

            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, flexScale: 0.55f);
            await BeginGrabAndCommitAsync(sceneTree, fixture, LimbSide.Right);

            // While the right hand's commit blend window is still running, the left hand tracks a fresh pose.
            fixture.InjectTrackedHandPose(LimbSide.Left, flexScale: 1.4f);
            await WaitForSecondsAsync(sceneTree, 0.05);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Left),
                fixture.ProjectCurrentPose(LimbSide.Left, 1.4f),
                RotationToleranceRadians,
                "left hand during the right hand's commit blend window");

            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.HeldSuppressed,
                    budgetSeconds: 5.0),
                "Expected the commit blend window to complete into zero-write suppression.");

            // And after suppression began, the left hand still follows a different tracked pose.
            fixture.InjectTrackedHandPose(LimbSide.Left, flexScale: 0.25f);
            await WaitForSecondsAsync(sceneTree, 0.05);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Left),
                fixture.ProjectCurrentPose(LimbSide.Left, 0.25f),
                RotationToleranceRadians,
                "left hand after the right hand's suppression began");

            // The committed hand stays at the authored pose throughout.
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                ArbitrationFixture.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath),
                RotationToleranceRadians,
                "right hand stays at the authored pose");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 3: after release, the released hand resumes following tracked input once the release blend
    /// window completes — the bones match the shared projection of the injected pose (XR-002 UR16; INTR-002
    /// R61).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReleasedHand_AfterBlendWindow_ResumesFollowingTrackedProjection()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Modifier.GrabPoseBlendDurationSeconds = 1.0f;
            _ = fixture.AddRightBall();

            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, flexScale: 0.55f);
            await BeginGrabAndCommitAsync(sceneTree, fixture, LimbSide.Right);
            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.HeldSuppressed,
                    budgetSeconds: 5.0),
                "Expected the held suppression before releasing.");

            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 0.35f);
            fixture.RightHand.Release();
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(OpticalGrabPoseBlendPhase.ReleaseBlend, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.Tracking,
                    budgetSeconds: 5.0),
                "Expected the release blend window to complete back into normal tracking.");

            await WaitForFramesAsync(sceneTree, 2);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                fixture.ProjectCurrentPose(LimbSide.Right, 0.35f),
                RotationToleranceRadians,
                "released hand after the blend window vs live projection");
            Assert.True(
                fixture.CaptureFingerRotations(LimbSide.Right)[3].AngleTo(
                    ArbitrationFixture.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath)[3]) > 0.15f,
                "Expected the released hand to have visibly left the authored pose.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 4: objective transition bounds. During both blend windows the per-frame finger-bone movement
    /// stays well under the tracked→authored (and authored→tracked) snap distances measured in this test — a
    /// single-frame snap would exceed the bound, so the assertion proves the transitions are gradual (XR-002
    /// UR16; OG3).
    /// </summary>
    /// <remarks>
    /// The bound is <c>snap × 0.35</c>: with the stretched 1.0 s observation window any frame whose delta is
    /// at most ~350 ms stays under it, while a snap covers the full snap distance in one frame. The tests
    /// additionally verify both windows traverse the full distance between the exact endpoints.
    /// </remarks>
    [Headless]
    [Fact]
    public async Task BlendWindows_KeepPerFrameMovementWellUnderSnapDistance()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Modifier.GrabPoseBlendDurationSeconds = 1.0f;
            _ = fixture.AddRightBall();
            const float commitFlex = 0.55f;
            const float releaseFlex = 0.1f;

            // Commit transition: measure the tracked→authored snap distance, then bound every blend frame.
            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, commitFlex);
            Quaternion[] trackedBeforeCommit = fixture.CaptureFingerRotations(LimbSide.Right);
            Quaternion[] authored = ArbitrationFixture.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath);
            float commitSnap = MaxAngle(trackedBeforeCommit, authored);
            Assert.True(
                commitSnap > 0.3f,
                $"The tracked→authored snap distance must be meaningful for the bound; measured {commitSnap:R} rad.");

            await BeginGrabAndCommitAsync(sceneTree, fixture, LimbSide.Right);
            float commitMaxFrameDelta = await CaptureMaxFrameDeltaAsync(
                sceneTree,
                fixture,
                LimbSide.Right,
                OpticalGrabPoseBlendPhase.CommitBlend,
                OpticalGrabPoseBlendPhase.HeldSuppressed,
                trackedBeforeCommit);
            Assert.True(
                commitMaxFrameDelta < commitSnap * 0.35f,
                $"Commit-blend frames must stay well under the snap distance; max frame delta " +
                    $"{commitMaxFrameDelta:R} rad vs snap {commitSnap:R} rad.");

            // The commit blend must actually traverse to the authored pose.
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                authored,
                RotationToleranceRadians,
                "commit blend endpoint");

            // Release transition: measure the authored→tracked snap distance, then bound every blend frame.
            fixture.InjectTrackedHandPose(LimbSide.Right, releaseFlex);
            Quaternion[] authoredBeforeRelease = fixture.CaptureFingerRotations(LimbSide.Right);
            Quaternion[] trackedAfterRelease = fixture.ProjectCurrentPose(LimbSide.Right, releaseFlex);
            float releaseSnap = MaxAngle(authoredBeforeRelease, trackedAfterRelease);
            Assert.True(
                releaseSnap > 0.3f,
                $"The authored→tracked snap distance must be meaningful for the bound; measured {releaseSnap:R} rad.");

            fixture.RightHand.Release();
            float releaseMaxFrameDelta = await CaptureMaxFrameDeltaAsync(
                sceneTree,
                fixture,
                LimbSide.Right,
                OpticalGrabPoseBlendPhase.ReleaseBlend,
                OpticalGrabPoseBlendPhase.Tracking,
                authoredBeforeRelease);
            Assert.True(
                releaseMaxFrameDelta < releaseSnap * 0.35f,
                $"Release-blend frames must stay well under the snap distance; max frame delta " +
                    $"{releaseMaxFrameDelta:R} rad vs snap {releaseSnap:R} rad.");

            // The release blend must actually traverse back to the live tracked pose.
            await WaitForFramesAsync(sceneTree, 2);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                trackedAfterRelease,
                RotationToleranceRadians,
                "release blend endpoint");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 5: with arbitration held, invalidating the optical samples changes nothing — the authored pose
    /// and the write-suppression state are preserved, there is no synthetic release, and recovery keeps the
    /// hold until an explicit release blends back out (XR-002 TR31, TR36; INTR-002 R59 held-loss row).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_TrackingLoss_LeavesAuthoredPoseAndSuppressionUnchanged()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            _ = fixture.AddRightBall();

            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, flexScale: 0.55f);
            await BeginGrabAndCommitAsync(sceneTree, fixture, LimbSide.Right);
            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.HeldSuppressed,
                    budgetSeconds: 5.0),
                "Expected the held suppression before invalidating samples.");

            await WaitForFramesAsync(sceneTree, 3);
            Quaternion[] authoredPose = fixture.CaptureFingerRotations(LimbSide.Right);
            AssertMaxAngleAtMost(
                authoredPose,
                ArbitrationFixture.SampleAuthoredReference(LimbSide.Right, GrabBallAnimationPath),
                RotationToleranceRadians,
                "held pose vs authored keys before sample loss");

            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Right, joint);
            }

            await WaitForSecondsAsync(sceneTree, 0.3);

            Assert.Equal(OpticalGrabPoseBlendPhase.HeldSuppressed, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            AssertByteStable(fixture.CaptureFingerRotations(LimbSide.Right), authoredPose, "whole-hand loss while held");
            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.NotNull(fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);

            // Recovery while still holding: still no release, still suppressed, still byte-stable.
            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 1.2f);
            await WaitForSecondsAsync(sceneTree, 0.2);

            Assert.Equal(OpticalGrabPoseBlendPhase.HeldSuppressed, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            AssertByteStable(fixture.CaptureFingerRotations(LimbSide.Right), authoredPose, "recovery while held");

            // The explicit release still works and blends back out.
            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 0.2f);
            fixture.RightHand.Release();
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(OpticalGrabPoseBlendPhase.ReleaseBlend, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 6: tracking loss mid-blend-out freezes the release blend at its current value and pauses the
    /// blend clock — the window cannot complete while the whole hand is invalid — and blending resumes to
    /// completion once samples recover (XR-002 UR16, TR36 mirrored; INTR-002 R61).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ReleaseBlend_TrackingLossMidWindow_FreezesThenResumesBlending()
    {
        SceneTree sceneTree = GetSceneTree();
        ArbitrationFixture fixture = await ArbitrationFixture.CreateAsync(sceneTree);

        try
        {
            fixture.Modifier.GrabPoseBlendDurationSeconds = 1.0f;
            _ = fixture.AddRightBall();

            await fixture.InjectTrackedPoseAndWaitAsync(sceneTree, LimbSide.Right, flexScale: 0.55f);
            await BeginGrabAndCommitAsync(sceneTree, fixture, LimbSide.Right);
            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.HeldSuppressed,
                    budgetSeconds: 5.0),
                "Expected the held suppression before releasing.");

            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 0.2f);
            fixture.RightHand.Release();
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Equal(OpticalGrabPoseBlendPhase.ReleaseBlend, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));

            // Blend part-way, then lose the whole hand.
            await WaitForSecondsAsync(sceneTree, 0.25);
            Quaternion[] midBlendPose = fixture.CaptureFingerRotations(LimbSide.Right);

            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Right, joint);
            }

            // Far longer than the remaining window: the frozen clock must keep the blend incomplete.
            await WaitForSecondsAsync(sceneTree, 0.6);

            Assert.Equal(OpticalGrabPoseBlendPhase.ReleaseBlend, fixture.Modifier.GetGrabPoseBlendPhase(LimbSide.Right));
            AssertByteStable(fixture.CaptureFingerRotations(LimbSide.Right), midBlendPose, "frozen release blend");

            // Recovery resumes and completes the blend back into normal tracking at the recovered pose.
            fixture.InjectTrackedHandPose(LimbSide.Right, flexScale: 0.2f);
            Assert.True(
                await WaitForBlendPhaseAsync(
                    sceneTree,
                    fixture.Modifier,
                    LimbSide.Right,
                    OpticalGrabPoseBlendPhase.Tracking,
                    budgetSeconds: 5.0),
                "Expected the resumed release blend to complete into tracking.");

            await WaitForFramesAsync(sceneTree, 2);
            AssertMaxAngleAtMost(
                fixture.CaptureFingerRotations(LimbSide.Right),
                fixture.ProjectCurrentPose(LimbSide.Right, 0.2f),
                RotationToleranceRadians,
                "resumed release blend endpoint");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The modifier's default blend window matches the documented 0.2 s coordination value for
    /// <c>HandPoseController.TransitionDuration</c> (XR-002 UR16).
    /// </summary>
    [Headless]
    [Fact]
    public void ModifierDefault_BlendDurationIsCoordinatedWithHandPoseControllerTransition()
    {
        var modifier = new OpticalFingerTrackingModifier();

        Assert.Equal(0.2f, modifier.GrabPoseBlendDurationSeconds, 5);
    }

    private static async Task BeginGrabAndCommitAsync(
        SceneTree sceneTree,
        ArbitrationFixture fixture,
        LimbSide side)
    {
        HandPoseBehaviour hand = side == LimbSide.Left ? fixture.LeftHand : fixture.RightHand;
        _ = hand.BeginGrab(HandGrabInputSource.Optical);
        Assert.Equal(HandGrabLifecycleState.Pending, hand.GrabLifecycle);

        var stopwatch = Stopwatch.StartNew();
        while (hand.GrabLifecycle != HandGrabLifecycleState.Held && stopwatch.Elapsed.TotalSeconds < 5.0)
        {
            fixture.SettleHandTarget(side);
            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.Equal(HandGrabLifecycleState.Held, hand.GrabLifecycle);
        Assert.True(
            fixture.XRManager.OpticalGrabArbiter.GetPresentation(side).IsOpticalGrabHeld,
            "Expected the commit to publish the per-hand arbitration state.");
    }

    /// <summary>
    /// Captures the committed hand's per-frame finger-bone movement while its blend phase matches
    /// <paramref name="blendPhase" />, until the phase reaches <paramref name="completedPhase" />; returns the
    /// largest single-frame rotation delta observed across the 15 bones.
    /// </summary>
    private static async Task<float> CaptureMaxFrameDeltaAsync(
        SceneTree sceneTree,
        ArbitrationFixture fixture,
        LimbSide side,
        OpticalGrabPoseBlendPhase blendPhase,
        OpticalGrabPoseBlendPhase completedPhase,
        Quaternion[] startPose)
    {
        Quaternion[] previous = startPose;
        float maxFrameDelta = 0f;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < 6.0)
        {
            OpticalGrabPoseBlendPhase phase = fixture.Modifier.GetGrabPoseBlendPhase(side);
            if (phase == completedPhase)
            {
                break;
            }

            await WaitForNextFrameAsync(sceneTree);
            if (fixture.Modifier.GetGrabPoseBlendPhase(side) != blendPhase)
            {
                continue;
            }

            Quaternion[] capture = fixture.CaptureFingerRotations(side);
            float frameDelta = MaxAngle(capture, previous);
            maxFrameDelta = MathF.Max(maxFrameDelta, frameDelta);
            previous = capture;
        }

        return maxFrameDelta;
    }

    private static async Task<bool> WaitForBlendPhaseAsync(
        SceneTree sceneTree,
        OpticalFingerTrackingModifier modifier,
        LimbSide side,
        OpticalGrabPoseBlendPhase phase,
        double budgetSeconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < budgetSeconds)
        {
            if (modifier.GetGrabPoseBlendPhase(side) == phase)
            {
                return true;
            }

            await WaitForNextFrameAsync(sceneTree);
        }

        return modifier.GetGrabPoseBlendPhase(side) == phase;
    }

    private static float MaxAngle(Quaternion[] first, Quaternion[] second)
    {
        Assert.Equal(first.Length, second.Length);
        float maxAngle = 0f;
        for (int index = 0; index < first.Length; index++)
        {
            // Hemisphere-align the expected side to the observed one: the modifier stabilises the hemisphere
            // of its writes against its cache, so equivalent rotations may carry opposite signs.
            Quaternion aligned = FingerRetargetingMath.StabiliseRotationHemisphere(second[index], first[index]);
            maxAngle = MathF.Max(maxAngle, first[index].AngleTo(aligned));
        }

        return maxAngle;
    }

    private static void AssertMaxAngleAtMost(Quaternion[] actual, Quaternion[] expected, float tolerance, string context)
    {
        float maxAngle = MaxAngle(actual, expected);
        Assert.True(
            maxAngle <= tolerance,
            $"Expected near-exact rotations for {context}; max angle {maxAngle:R} rad exceeds {tolerance:R} rad.");
    }

    /// <summary>
    /// Asserts exact stability of the observed rotations, treating opposite quaternion hemispheres as equal —
    /// a sign flip encodes the same rotation and is not motion.
    /// </summary>
    private static void AssertByteStable(Quaternion[] actual, Quaternion[] expected, string context)
    {
        for (int index = 0; index < actual.Length; index++)
        {
            bool stable = actual[index] == expected[index] || actual[index] == -expected[index];
            Assert.True(
                stable,
                $"Expected byte-stable rotations for {context} at finger {index}: " +
                    $"({actual[index].X:R}, {actual[index].Y:R}, {actual[index].Z:R}, {actual[index].W:R}) vs " +
                    $"({expected[index].X:R}, {expected[index].Y:R}, {expected[index].Z:R}, {expected[index].W:R}).");
        }
    }

    /// <summary>
    /// Deterministic flex-ramp synthesis identical to the coordinator fixtures: each joint is injected as
    /// <c>wristRotation * cumulativeLocal</c> so wrist rotation, metacarpal offsets, and chain state all cancel
    /// in the parent-relative quotient that supplies <c>S</c> (XR-002 TR17, TR20).
    /// </summary>
    private static void InjectTrackedHandPose(MockXRRuntimeNode runtime, LimbSide side, float flexScale)
    {
        float wristYaw = side == LimbSide.Left ? LeftWristYawRadians : RightWristYawRadians;
        Basis wristRotation = Basis.Identity.Rotated(Vector3.Up, wristYaw);
        Vector3 wristOrigin = new(side == LimbSide.Left ? -0.25f : 0.25f, 1.0f, -0.35f);

        runtime.SetHandJointSample(side, XRHandJoint.Wrist, new Transform3D(wristRotation, wristOrigin));

        Dictionary<XRHandJoint, Basis> cumulative = [];
        foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
        {
            if (joint == XRHandJoint.Wrist)
            {
                cumulative[joint] = Basis.Identity;
                continue;
            }

            var local = new Quaternion(Vector3.Right, JointLocalFlexRadians(joint) * flexScale);
            Basis localBasis = new(local);
            XRHandJoint parent = OpticalFingerTrackingTestTopology.GetTrackedParent(joint)
                ?? throw new InvalidOperationException($"Joint {joint} unexpectedly lacks a source parent.");
            cumulative[joint] = cumulative[parent] * localBasis;

            Vector3 translation = wristOrigin + new Vector3(0.0f, -0.03f * (int)joint, 0.02f);
            runtime.SetHandJointSample(side, joint, new Transform3D(wristRotation * cumulative[joint], translation));
        }
    }

    private static float JointLocalFlexRadians(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.ThumbMetacarpal => 0.21f,
            XRHandJoint.ThumbProximal => 0.34f,
            XRHandJoint.ThumbDistal => 0.42f,
            XRHandJoint.IndexMetacarpal => 0.12f,
            XRHandJoint.IndexProximal => 0.30f,
            XRHandJoint.IndexIntermediate => 0.51f,
            XRHandJoint.IndexDistal => 0.40f,
            XRHandJoint.MiddleMetacarpal => 0.10f,
            XRHandJoint.MiddleProximal => 0.27f,
            XRHandJoint.MiddleIntermediate => 0.48f,
            XRHandJoint.MiddleDistal => 0.37f,
            XRHandJoint.RingMetacarpal => 0.11f,
            XRHandJoint.RingProximal => 0.24f,
            XRHandJoint.RingIntermediate => 0.45f,
            XRHandJoint.RingDistal => 0.34f,
            XRHandJoint.LittleMetacarpal => 0.09f,
            XRHandJoint.LittleProximal => 0.18f,
            XRHandJoint.LittleIntermediate => 0.39f,
            XRHandJoint.LittleDistal => 0.28f,
            XRHandJoint.Wrist => throw new NotImplementedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
        };

    /// <summary>
    /// Arbitration rig: mock XR runtime, a bound finger modifier staging the shared projection binding on a
    /// synthetic canonical skeleton published as <c>%GeneralSkeleton</c>, and a two-sided hand rig whose
    /// AnimationTree mirrors the production hand-pose blend topology so authored poses genuinely own the bones
    /// once the modifier stops writing.
    /// </summary>
    private sealed class ArbitrationFixture(
        TestGame root,
        TestXRManager xrManager,
        MockXRRuntimeNode runtime,
        OpticalFingerTrackingModifier modifier,
        Skeleton3D fingerSkeleton,
        int[][] fingerBoneIndices,
        SkeletonPoseCapture poseCapture,
        AnimationTree animationTree,
        Node3D rightHandTarget,
        Node3D leftHandTarget,
        Skeleton3D handSkeleton,
        HandGrabTargetProvider rightProvider,
        HandGrabTargetProvider leftProvider,
        HandPoseBehaviour rightHand,
        HandPoseBehaviour leftHand)
    {
        public TestGame Root => root;

        public TestXRManager XRManager => xrManager;

        public MockXRRuntimeNode Runtime => runtime;

        public OpticalFingerTrackingModifier Modifier => modifier;

        public Skeleton3D FingerSkeleton => fingerSkeleton;

        public SkeletonPoseCapture PoseCapture => poseCapture;

        public AnimationTree AnimationTree => animationTree;

        public HandGrabTargetProvider RightProvider => rightProvider;

        public HandGrabTargetProvider LeftProvider => leftProvider;

        public HandPoseBehaviour RightHand => rightHand;

        public HandPoseBehaviour LeftHand => leftHand;

        public GrabbableNode AddRightBall()
            => AddGrabbable("Ball", new Vector3(0.55f, 0.0f, 0.0f), GrabBallAnimationPath);

        public GrabbableNode AddGrabbable(string name, Vector3 targetOrigin, string animationPath)
        {
            Animation animation = ResourceLoader.Load<Animation>(animationPath)
                ?? throw new InvalidOperationException($"Failed to load grab animation '{animationPath}'.");
            GrabbableNode grabbable = new()
            {
                Name = name,
                Position = targetOrigin,
            };
            grabbable.AddChild(new MutableGrabPoint
            {
                Name = "MutableGrabPoint",
                TargetOrigin = targetOrigin,
                HandTargetOrigin = targetOrigin,
                Animation = animation,
            });
            Root.AddChild(grabbable);
            return grabbable;
        }

        public void InjectTrackedHandPose(LimbSide side, float flexScale)
            => OpticalGrabPoseArbitrationIntegrationTests.InjectTrackedHandPose(Runtime, side, flexScale);

        public async Task InjectTrackedPoseAndWaitAsync(SceneTree sceneTree, LimbSide side, float flexScale)
        {
            InjectTrackedHandPose(side, flexScale);
            await WaitForFramesAsync(sceneTree, 3);

            AssertMaxAngleAtMost(
                CaptureFingerRotations(side),
                ProjectCurrentPose(side, flexScale),
                RotationToleranceRadians,
                $"{side} tracked pose before the grab");
        }

        public void SettleHandTarget(LimbSide side)
        {
            Node3D handTarget = side == LimbSide.Left ? leftHandTarget : rightHandTarget;
            HandGrabTargetProvider provider = side == LimbSide.Left ? leftProvider : rightProvider;
            Transform3D grabTarget = provider.GrabTarget;

            handTarget.GlobalTransform = grabTarget;
            SetHandBoneWorldTransform(
                handSkeleton,
                side == LimbSide.Left ? "LeftHand" : "RightHand",
                grabTarget);
        }

        /// <summary>
        /// Captures the effective per-frame finger rotations from the skeleton's <c>skeleton_updated</c>
        /// signal — the only point where the modifier's per-frame overlay writes are readable, after all
        /// modifier processing completes and before the engine restores the authored local pose.
        /// </summary>
        public Quaternion[] CaptureFingerRotations(LimbSide side)
        {
            int[] boneIndices = fingerBoneIndices[(int)side];
            var rotations = new Quaternion[FingerCount];
            for (int fingerIndex = 0; fingerIndex < FingerCount; fingerIndex++)
            {
                rotations[fingerIndex] = poseCapture.Rotations[boneIndices[fingerIndex]];
            }

            return rotations;
        }

        /// <summary>
        /// Projects the currently injected samples through the modifier's shared binding — the independent
        /// oracle for where normal tracking writes should land (XR-002 TR49).
        /// </summary>
        public Quaternion[] ProjectCurrentPose(LimbSide side, float flexScale)
        {
            _ = flexScale;
            var samples = new XRHandJointSourceSample[TrackedJointCount];
            for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
            {
                _ = Runtime.TryGetJoint(side, (XRHandJoint)jointIndex, out samples[jointIndex]);
            }

            var poses = new OpticalFingerProjectedPose[FingerCount];
            Assert.True(
                Modifier.TryProjectOpticalFingers(side, samples, poses),
                "Expected the staged binding to project the injected samples.");

            var rotations = new Quaternion[FingerCount];
            for (int fingerIndex = 0; fingerIndex < FingerCount; fingerIndex++)
            {
                Assert.True(
                    poses[fingerIndex].IsValid,
                    $"Expected a valid projection for finger {fingerIndex} of {side}.");
                rotations[fingerIndex] = poses[fingerIndex].Rotation;
            }

            return rotations;
        }

        /// <summary>Samples a side's authored reference pose from a grab animation resource.</summary>
        public static Quaternion[] SampleAuthoredReference(LimbSide side, string animationPath)
        {
            Animation animation = ResourceLoader.Load<Animation>(animationPath)
                ?? throw new InvalidOperationException($"Failed to load grab animation '{animationPath}'.");
            Assert.True(
                AuthoredHandPoseReferenceSampler.TrySample(animation, side, out AuthoredHandPoseSideReference reference, out string error),
                $"Authored reference sampling failed for '{animationPath}': {error}");

            return reference.Poses.ToArray();
        }

        public static async Task<ArbitrationFixture> CreateAsync(SceneTree sceneTree)
        {
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalGrabArbitrationFixture",
            };

            TestXRManager xrManager = new()
            {
                Name = "XR",
            };
            root.AddChild(xrManager);

            Node3D rigHolder = new()
            {
                Name = "Rig",
            };
            root.AddChild(rigHolder);

            // The finger skeleton is published to authored animation tracks as %GeneralSkeleton — the
            // production track-path contract — so the AnimationTree can own the bones once the modifier stops
            // writing.
            Skeleton3D fingerSkeleton = CreateFingerSkeleton();
            fingerSkeleton.Name = "GeneralSkeleton";
            rigHolder.AddChild(fingerSkeleton);
            fingerSkeleton.Owner = root;
            fingerSkeleton.UniqueNameInOwner = true;
            rigHolder.Owner = root;

            OpticalFingerTrackingModifier modifier = new()
            {
                Name = "OpticalFingerTrackingModifier",
                CalibrationProfile = CreateIdentityCalibrationProfile(),
            };
            fingerSkeleton.AddChild(modifier);

            int[][] fingerBoneIndices = [
                ResolveFingerBoneIndices(fingerSkeleton, LimbSide.Left),
                ResolveFingerBoneIndices(fingerSkeleton, LimbSide.Right),
            ];

            // The pose capture listens for the skeleton's post-modification update signal, observing the
            // effective finger output of the whole pipeline.
            var poseCapture = SkeletonPoseCapture.Attach(fingerSkeleton);

            Node3D handRig = new()
            {
                Name = "HandRig",
            };
            root.AddChild(handRig);
            handRig.Owner = root;

            Node3D rightHandTarget = new()
            {
                Name = "RightHandTarget"
            };
            Node3D leftHandTarget = new()
            {
                Name = "LeftHandTarget"
            };
            handRig.AddChild(rightHandTarget);
            handRig.AddChild(leftHandTarget);

            Skeleton3D handSkeleton = new()
            {
                Name = "HandSkeleton"
            };
            _ = handSkeleton.AddBone("RightHand");
            _ = handSkeleton.AddBone("LeftHand");
            handRig.AddChild(handSkeleton);

            BoneAttachment3D rightAttachment = new()
            {
                Name = "RightHandAttachment",
                BoneName = "RightHand",
                BoneIdx = 0,
            };
            BoneAttachment3D leftAttachment = new()
            {
                Name = "LeftHandAttachment",
                BoneName = "LeftHand",
                BoneIdx = 1,
            };
            handSkeleton.AddChild(rightAttachment);
            handSkeleton.AddChild(leftAttachment);

            AnimationPlayer animationPlayer = new()
            {
                Name = "AnimationPlayer"
            };
            handRig.AddChild(animationPlayer);
            animationPlayer.Owner = root;

            // The upstream animation is deliberately trackless, mirroring production where the locomotion
            // state machine never animates finger bones: the tree writes a finger bone only while a hand pose
            // is blended in, so the modifier is unopposed whenever it writes (INTR-003 TR6-TR7; XR-002 TR30).
            AnimationLibrary library = new();
            _ = library.AddAnimation(
                new StringName(HandPoseAnimationTreePaths.ResetAnimationName),
                new Animation());
            _ = animationPlayer.AddAnimationLibrary(new StringName(string.Empty), library);

            AnimationTree animationTree = new()
            {
                Name = "AnimationTree",
                TreeRoot = CreateHandPoseBlendTree(),
                AnimPlayer = new NodePath("../AnimationPlayer"),
                Active = true,
            };
            handRig.AddChild(animationTree);
            animationTree.Owner = root;

            HandGrabTargetProvider rightProvider = new()
            {
                Name = "RightGrabProvider"
            };
            HandGrabTargetProvider leftProvider = new()
            {
                Name = "LeftGrabProvider"
            };
            handRig.AddChild(rightProvider);
            handRig.AddChild(leftProvider);

            HandComponentHolder hands = new()
            {
                Name = "Hands"
            };
            handRig.AddChild(hands);

            HandPoseBehaviour rightHand = new()
            {
                Name = "RightHand",
                Side = LimbSide.Right,
                HandTargetNode = rightHandTarget,
                HandBoneAttachment = rightAttachment,
                GrabTargetProvider = rightProvider,
                AnimationTree = animationTree,
                DiscoveryRangeMetres = 0.5f,
                GrabCommitDistanceMetres = 0.02f,
            };
            HandPoseBehaviour leftHand = new()
            {
                Name = "LeftHand",
                Side = LimbSide.Left,
                HandTargetNode = leftHandTarget,
                HandBoneAttachment = leftAttachment,
                GrabTargetProvider = leftProvider,
                AnimationTree = animationTree,
                DiscoveryRangeMetres = 0.5f,
                GrabCommitDistanceMetres = 0.02f,
            };
            hands.AddChild(rightHand);
            hands.AddChild(leftHand);

            Node3D originHolder = new()
            {
                Name = "OriginHolder"
            };
            root.AddChild(originHolder);

            MockXRRuntimeNode runtime = LoadPackedScene(MockRuntimeScenePath).Instantiate<MockXRRuntimeNode>();
            originHolder.AddChild(runtime);
            _ = runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
            xrManager.SetRuntime(runtime);

            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 4);

            var fixture = new ArbitrationFixture(
                root,
                xrManager,
                runtime,
                modifier,
                fingerSkeleton,
                fingerBoneIndices,
                poseCapture,
                animationTree,
                rightHandTarget,
                leftHandTarget,
                handSkeleton,
                rightProvider,
                leftProvider,
                rightHand,
                leftHand);

            Transform3D rightHandTransform = new(Basis.Identity, new Vector3(0.5f, 0.0f, 0.0f));
            Transform3D leftHandTransform = new(Basis.Identity, new Vector3(-0.5f, 0.0f, 0.0f));
            rightHandTarget.GlobalTransform = rightHandTransform;
            leftHandTarget.GlobalTransform = leftHandTransform;
            SetHandBoneWorldTransform(handSkeleton, "RightHand", rightHandTransform);
            SetHandBoneWorldTransform(handSkeleton, "LeftHand", leftHandTransform);

            Assert.True(
                modifier.IsFingerTopologyValid,
                "The synthetic finger skeleton must satisfy the production bilateral binding gates.");
            Assert.True(poseCapture.CaptureCount > 0, "Expected the finger pose capture to run at least once.");

            // Commit optical mode and enter the modifier's optical session so the shared projection binding
            // stages its calibration and the entry snapshot exists.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            OpticalGrabPoseArbitrationIntegrationTests.InjectTrackedHandPose(runtime, LimbSide.Left, 0.0f);
            OpticalGrabPoseArbitrationIntegrationTests.InjectTrackedHandPose(runtime, LimbSide.Right, 0.0f);
            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(modifier.IsOpticalSessionActive, "Expected the optical session to start for the fixture.");

            return fixture;
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        /// <summary>
        /// Production hand-pose blend topology in miniature: the Reset upstream node feeds the filtered left
        /// blend stage, which feeds the filtered right blend stage, with the per-side pose animation nodes on
        /// the blend inputs (INTR-003; HandPoseAnimationTreePaths).
        /// </summary>
        private static AnimationNodeBlendTree CreateHandPoseBlendTree()
        {
            AnimationNodeBlendTree root = new();

            AnimationNodeAnimation upstream = new()
            {
                Animation = new StringName(HandPoseAnimationTreePaths.ResetAnimationName),
            };
            root.AddNode(HandPoseAnimationTreePaths.UpstreamNode, upstream, new Vector2(-400.0f, 0.0f));

            AnimationNodeAnimation leftPose = new();
            root.AddNode(HandPoseAnimationTreePaths.LeftHandPoseNode, leftPose, new Vector2(-300.0f, 120.0f));
            AnimationNodeAnimation rightPose = new();
            root.AddNode(HandPoseAnimationTreePaths.RightHandPoseNode, rightPose, new Vector2(-100.0f, 120.0f));

            AnimationNodeBlend2 leftBlend = CreateFilteredHandBlend(LimbSide.Left);
            root.AddNode(HandPoseAnimationTreePaths.LeftHandBlendNode, leftBlend, new Vector2(-120.0f, -170.0f));
            AnimationNodeBlend2 rightBlend = CreateFilteredHandBlend(LimbSide.Right);
            root.AddNode(HandPoseAnimationTreePaths.RightHandBlendNode, rightBlend, new Vector2(100.0f, -160.0f));

            root.ConnectNode(HandPoseAnimationTreePaths.LeftHandBlendNode, 0, HandPoseAnimationTreePaths.UpstreamNode);
            root.ConnectNode(HandPoseAnimationTreePaths.LeftHandBlendNode, 1, HandPoseAnimationTreePaths.LeftHandPoseNode);
            root.ConnectNode(HandPoseAnimationTreePaths.RightHandBlendNode, 0, HandPoseAnimationTreePaths.LeftHandBlendNode);
            root.ConnectNode(HandPoseAnimationTreePaths.RightHandBlendNode, 1, HandPoseAnimationTreePaths.RightHandPoseNode);
            root.ConnectNode("output", 0, HandPoseAnimationTreePaths.RightHandBlendNode);

            return root;
        }

        private static AnimationNodeBlend2 CreateFilteredHandBlend(LimbSide side)
        {
            AnimationNodeBlend2 blend = new()
            {
                FilterEnabled = true,
            };

            foreach (NodePath filterPath in HandPoseAnimationTreePaths.GetFingerFilterPaths(side, "%GeneralSkeleton"))
            {
                blend.SetFilterPath(filterPath, true);
            }

            return blend;
        }

        private static int[] ResolveFingerBoneIndices(Skeleton3D skeleton, LimbSide side)
        {
            string prefix = side == LimbSide.Left ? "Left" : "Right";
            int[] boneIndices = new int[FingerCount];
            for (int fingerIndex = 0; fingerIndex < FingerCount; fingerIndex++)
            {
                string boneName = prefix + XRHandJoints.DestinationJoints[fingerIndex];
                boneIndices[fingerIndex] = skeleton.FindBone(boneName);
                Assert.True(boneIndices[fingerIndex] >= 0, $"Expected the fixture skeleton to contain '{boneName}'.");
            }

            return boneIndices;
        }

        /// <summary>Synthetic canonical finger skeleton identical to the modifier integration fixtures.</summary>
        private static Skeleton3D CreateFingerSkeleton()
        {
            Skeleton3D skeleton = new()
            {
                Name = "TestSkeleton",
            };

            string[] fingerChains =
            [
                "ThumbMetacarpal", "ThumbProximal", "ThumbDistal",
                "IndexProximal", "IndexIntermediate", "IndexDistal",
                "MiddleProximal", "MiddleIntermediate", "MiddleDistal",
                "RingProximal", "RingIntermediate", "RingDistal",
                "LittleProximal", "LittleIntermediate", "LittleDistal",
            ];

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                string prefix = side == LimbSide.Left ? "Left" : "Right";
                int upperArm = AddBone(skeleton, prefix + "UpperArm", -1, new Vector3(side == LimbSide.Left ? -0.2f : 0.2f, 1.4f, 0.0f));
                int lowerArm = AddBone(skeleton, prefix + "LowerArm", upperArm, new Vector3(0.0f, -0.25f, 0.0f));
                int hand = AddBone(skeleton, prefix + "Hand", lowerArm, new Transform3D(Basis.Identity, new Vector3(0.0f, 0.2050707f, 0.0f)));

                for (int chainIndex = 0; chainIndex < fingerChains.Length; chainIndex += 3)
                {
                    int parent = hand;
                    int nonThumbChain = (chainIndex / 3) - 1;
                    for (int depth = 0; depth < 3; depth++)
                    {
                        string chainBone = fingerChains[chainIndex + depth];
                        Transform3D rest = nonThumbChain < 0
                            ? CreateThumbRest(side, depth)
                            : CreateNaturalFingerRest(side, nonThumbChain, depth);
                        parent = AddBone(skeleton, prefix + chainBone, parent, rest);
                    }
                }
            }

            skeleton.ResetBonePoses();
            return skeleton;
        }

        private static Transform3D CreateNaturalFingerRest(LimbSide side, int nonThumbChain, int depth)
        {
            float handedness = side == LimbSide.Left ? -1.0f : 1.0f;
            Vector3[] leftRootOrigins =
            [
                new(-0.036526f, 0.094007f, 0.010289f),
                new(-0.011721f, 0.096455f, 0.006210f),
                new(0.007185f, 0.093367f, 0.005249f),
                new(0.025564f, 0.086033f, 0.009411f),
            ];
            Vector3 leftRoot = leftRootOrigins[nonThumbChain];
            Vector3 root = side == LimbSide.Left
                ? leftRoot
                : new Vector3(-leftRoot.X, leftRoot.Y, leftRoot.Z);
            return depth switch
            {
                0 => new Transform3D(Basis.Identity, root),
                1 => new Transform3D(Basis.Identity, new Vector3(0.001f * handedness, -0.038f, -0.010f)),
                _ => new Transform3D(Basis.Identity, new Vector3(0.0f, -0.030f, -0.013f)),
            };
        }

        private static Transform3D CreateThumbRest(LimbSide side, int depth)
        {
            float handedness = side == LimbSide.Left ? -1.0f : 1.0f;
            return depth switch
            {
                0 => new Transform3D(Basis.Identity, new Vector3(-0.055f * handedness, -0.002f, -0.015f)),
                1 => new Transform3D(Basis.Identity, new Vector3(0.0201366f * handedness, 0.0177871f, -0.0114732f)),
                _ => new Transform3D(Basis.Identity, new Vector3(0.00326f * handedness, -0.02933f, 0.01238f)),
            };
        }

        private static int AddBone(Skeleton3D skeleton, string name, int parent, Vector3 restOffset)
            => AddBone(skeleton, name, parent, new Transform3D(Basis.Identity, restOffset));

        private static int AddBone(Skeleton3D skeleton, string name, int parent, Transform3D rest)
        {
            int index = skeleton.GetBoneCount();
            _ = skeleton.AddBone(name);
            skeleton.SetBoneRest(index, rest);
            if (parent >= 0)
            {
                skeleton.SetBoneParent(index, parent);
            }

            return index;
        }

        private static OpticalFingerTrackingCalibrationProfile CreateIdentityCalibrationProfile()
        {
            List<OpticalFingerTrackingCalibrationEntry> entries = [];
            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.DestinationJoints)
                {
                    entries.Add(new OpticalFingerTrackingCalibrationEntry
                    {
                        Side = side,
                        Joint = joint,
                        SourceNeutral = Quaternion.Identity,
                        DestinationNeutral = Quaternion.Identity,
                        BasisCorrespondence = Quaternion.Identity,
                        Gain = 1.0f,
                    });
                }
            }

            return new OpticalFingerTrackingCalibrationProfile
            {
                SchemaVersion = OpticalFingerTrackingCalibrationProfile.CurrentSchemaVersion,
                ProfileVersion = "integration-identity-v1",
                ReferenceRig = "integration-fixture",
                Headset = "mock",
                Runtime = "mock/OpenXR",
                SourceCaptureID = "integration-identity-source-neutral-v1",
                Provenance = "Synthetic deterministic identity-S0 profile for optical grab arbitration fixtures.",
                LeftMetacarpalNeutralAnchor = OpticalFingerTrackingTestTopology.ExpectedMetacarpalNeutralAnchors[0],
                LeftMetacarpalSwingGain = OpticalFingerTrackingTestTopology.ExpectedMetacarpalSwingGains[0],
                RightMetacarpalNeutralAnchor = OpticalFingerTrackingTestTopology.ExpectedMetacarpalNeutralAnchors[1],
                RightMetacarpalSwingGain = OpticalFingerTrackingTestTopology.ExpectedMetacarpalSwingGains[1],
                MetacarpalHingeGateStart = OpticalFingerTrackingTestTopology.ExpectedMetacarpalHingeGateStart,
                MetacarpalHingeGateEnd = OpticalFingerTrackingTestTopology.ExpectedMetacarpalHingeGateEnd,
                MetacarpalBendGateStart = OpticalFingerTrackingTestTopology.ExpectedMetacarpalBendGateStart,
                MetacarpalBendGateEnd = OpticalFingerTrackingTestTopology.ExpectedMetacarpalBendGateEnd,
                Entries = [.. entries],
            };
        }
    }

    /// <summary>Generic grab point mirroring the hand-grab asset fixtures: deterministic, class-free.</summary>
    private sealed partial class MutableGrabPoint : Node, IGrabPoint
    {
        public Vector3 TargetOrigin
        {
            get;
            set;
        }

        public Vector3? HandTargetOrigin
        {
            get;
            set;
        }

        public Transform3D GrabPointOffsetFromHand
        {
            get;
            set;
        } = Transform3D.Identity;

        public Animation? Animation
        {
            get;
            set;
        }

        public float ReachDistanceMetres
        {
            get;
            set;
        } = float.PositiveInfinity;

        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
            => GetGrabPoint(handSide, handTransform, 0.0f);

        public GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform, float acquisitionToleranceMetres)
        {
            Transform3D handTarget = new(Basis.Identity, HandTargetOrigin ?? TargetOrigin);
            Transform3D grabPointTransform = new(Basis.Identity, TargetOrigin);
            float acquisitionDistance = handTransform.Origin.DistanceTo(TargetOrigin);
            float effectiveReachDistanceMetres = ReachDistanceMetres + Mathf.Max(0.0f, acquisitionToleranceMetres);
            return acquisitionDistance > effectiveReachDistanceMetres
                ? null
                : new GrabPointCandidate(
                    this,
                    handTarget,
                    Animation ?? throw new InvalidOperationException("The fixture grab point requires its authored animation."),
                    handSide,
                    handTransform,
                    grabPointTransform,
                    GrabPointOffsetFromHand,
                    acquisitionDistance)
                {
                    AcquisitionToleranceMetres = Mathf.Max(0.0f, acquisitionToleranceMetres),
                };
        }
    }

    private static void SetHandBoneWorldTransform(Skeleton3D skeleton, string boneName, Transform3D worldTransform)
    {
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected the hand skeleton to contain bone '{boneName}'.");
        Transform3D skeletonSpaceTransform = skeleton.GlobalTransform.AffineInverse() * worldTransform;
        skeleton.SetBoneGlobalPose(boneIndex, skeletonSpaceTransform);
        skeleton.ForceUpdateTransform();
    }

    /// <summary>
    /// Captures the effective per-frame bone rotations by listening for the skeleton's
    /// <c>skeleton_updated</c> signal, which fires once all <see cref="SkeletonModifier3D" /> processing is
    /// complete and before the engine restores the authored local pose. Skeleton modifier writes are
    /// per-frame overlays, so this is the only point where the effective pipeline output is readable.
    /// </summary>
    private sealed class SkeletonPoseCapture
    {
        private readonly Skeleton3D _skeleton;

        private readonly Callable _callable;

        private SkeletonPoseCapture(Skeleton3D skeleton)
        {
            _skeleton = skeleton;
            Rotations = new Quaternion[skeleton.GetBoneCount()];
            _callable = Callable.From(Capture);
            _ = skeleton.Connect(Skeleton3D.SignalName.SkeletonUpdated, _callable);
        }

        public Quaternion[] Rotations
        {
            get;
        }

        public long CaptureCount
        {
            get;
            private set;
        }

        public static SkeletonPoseCapture Attach(Skeleton3D skeleton)
            => new(skeleton);

        public void Detach()
        {
            if (GodotObject.IsInstanceValid(_skeleton)
                && _skeleton.IsConnected(Skeleton3D.SignalName.SkeletonUpdated, _callable))
            {
                _skeleton.Disconnect(Skeleton3D.SignalName.SkeletonUpdated, _callable);
            }
        }

        private void Capture()
        {
            for (int boneIndex = 0; boneIndex < Rotations.Length; boneIndex++)
            {
                Rotations[boneIndex] = _skeleton.GetBonePoseRotation(boneIndex);
            }

            CaptureCount++;
        }
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime) => Runtime = runtime;
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
