using System.Reflection;
using AlleyCat.Control;
using AlleyCat.Control.Hands;
using AlleyCat.Core;
using AlleyCat.IK;
using AlleyCat.IntegrationTests.XR;
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

namespace AlleyCat.IntegrationTests.Control;

/// <summary>
/// Integration coverage for the mode-aware optical grab input coordinator (CTRL-002; XR-002 TR46-TR54;
/// INTR-002 R59): candidate-aware closure recognition, the pending/held lifecycle table, tracking-loss and
/// mode-switch policies, pause suppression, per-hand independence, and controller grab edge mode independence.
/// </summary>
/// <remarks>
/// <para>
/// The fixture combines the deterministic mock XR runtime (optical mode commit plus per-joint injection) with
/// the shared projection binding staged by a real <see cref="OpticalFingerTrackingModifier" /> on the synthetic
/// canonical finger skeleton, and a two-sided <see cref="HandPoseBehaviour" /> hand rig with generic
/// <see cref="GrabbableNode" /> candidates carrying the authored grab animations.
/// </para>
/// <para>
/// Closure and opening ramps are calibrated per candidate animation: the injected flex scale is chosen by
/// projecting the injected samples through the live binding and evaluating the same production strategy
/// against the derived profile until the aggregate crosses the configured threshold bands — no magic flex
/// constants and no timing assumptions. Assertions remain behavioural (lifecycle state, provenance, held
/// object, pose application) rather than re-deriving the recognition maths.
/// </para>
/// <para>
/// The coordinator runs in <c>_PhysicsProcess</c>, so stability windows are advanced with
/// <c>WaitForPhysicsFramesAsync</c>; pending-grab commits happen in
/// <see cref="HandPoseBehaviour._Process" />, so settle steps wait process frames.
/// </para>
/// </remarks>
public sealed class HandGrabInputCoordinatorIntegrationTests
{
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string GrabBallAnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";
    private const string GrabPipeAnimationPath = "res://assets/characters/reference/female/animations/Grab-pipe-10.tres";

    private const int TrackedJointCount = 20;

    private const float LeftWristYawRadians = 0.8f;

    private const float RightWristYawRadians = -1.1f;


    /// <summary>
    /// Scenario 1: with no eligible candidate near the hand there is no grab recognition regardless of closure
    /// (CTRL-002 TR10; XR-002 TR47).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ClosureRamp_WithoutCandidate_NeverGrabs()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);

        try
        {
            await InjectClosureAndHold(sceneTree, fixture.Runtime, LimbSide.Right, calibration.ClosedFlex, physicsFrames: 30);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Null(fixture.RightHand.ActiveGrabAnimation);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// A rejected tick replaces, rather than retains, the preceding successful grip measurement. This keeps
    /// diagnostics neutral when candidate discovery rejects content or profile derivation stops succeeding.
    /// </summary>
    [Headless]
    [Fact]
    public async Task SuccessfulMeasurement_FollowedByCandidateRejections_DoesNotRetainStaleScoreOrEdge()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode ball = fixture.AddRightBall();

        try
        {
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            OpticalGripMeasurement valid = default;
            for (int frame = 0; frame < 30; frame++)
            {
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                if (fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out valid)
                    && valid.Score.HasValue)
                {
                    break;
                }
            }

            _ = Assert.NotNull(valid.Score);
            Assert.True(valid.SufficientValidity);

            CoordinatorFixture.MoveGrabbable(ball, new Vector3(5.0f, 0.0f, 0.0f));
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.True(fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement absent));
            Assert.Equal(OpticalGrabEvaluationReason.NoCandidate, absent.RejectionCategory);
            Assert.Null(absent.Score);
            Assert.False(absent.SufficientValidity);
            Assert.Equal(0.0f, absent.StabilitySeconds);
            Assert.Equal(GripEdge.None, absent.PendingEdge);
            Assert.Equal(GripEdge.None, absent.EmittedEdge);

            CoordinatorFixture.MoveGrabbable(ball, new Vector3(0.55f, 0.0f, 0.0f));
            MutableGrabPoint grabPoint = ball.GetNode<MutableGrabPoint>("MutableGrabPoint");
            grabPoint.Animation = new Animation();
            await WaitForPhysicsFramesAsync(sceneTree, 2);
            Assert.True(fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement unavailable));
            Assert.Equal(OpticalGrabEvaluationReason.NoCandidate, unavailable.RejectionCategory);
            Assert.Null(unavailable.Score);
            Assert.False(unavailable.SufficientValidity);
            Assert.Equal(0.0f, unavailable.StabilitySeconds);
            Assert.Equal(GripEdge.None, unavailable.PendingEdge);
            Assert.Equal(GripEdge.None, unavailable.EmittedEdge);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Production coordinator routing is candidate-keyed: a future strategy can be selected without a grab-point
    /// class branch, while its profile settings travel through the resolved strategy.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CandidateSelectedFutureStrategy_IsResolvedAndEvaluatedByProductionCoordinator()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        GrabbableNode ball = fixture.AddRightBall();
        var strategy = new FutureStrategy();

        try
        {
            MutableGrabPoint point = ball.GetNode<MutableGrabPoint>("MutableGrabPoint");
            point.GripRecognitionStrategyName = strategy.Name;
            fixture.Coordinator.RecognitionStrategyResolver = new GripRecognitionStrategyResolver([strategy]);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);
            await WaitForPhysicsFramesAsync(sceneTree, 3);

            Assert.Equal(1, strategy.DeriveCallCount);
            Assert.True(strategy.EvaluateCallCount > 0);
            Assert.True(fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement measurement));
            Assert.Equal(OpticalGrabEvaluationReason.NoEdge, measurement.RejectionCategory);

            long generationBeforeRebind = fixture.Modifier.OpticalFingerProjectionBindingGeneration;
            OpticalFingerTrackingCalibrationProfile alteredProfile =
                Assert.IsType<OpticalFingerTrackingCalibrationProfile>(fixture.Modifier.CalibrationProfile!.Duplicate());
            alteredProfile.Entries[0].SourceNeutral = new Quaternion(Vector3.Right, 0.01f);
            fixture.Modifier.CalibrationProfile = alteredProfile;
            Skeleton3D skeleton = fixture.Modifier.GetSkeleton();
            fixture.Modifier._SkeletonChanged(skeleton, skeleton);
            await WaitForFramesAsync(sceneTree, 3);
            await WaitForPhysicsFramesAsync(sceneTree, 2);

            Assert.True(
                fixture.Modifier.OpticalFingerProjectionBindingGeneration > generationBeforeRebind,
                "Expected the controlled valid binding rebind to publish a newer generation.");
            Assert.Equal(2, strategy.DeriveCallCount);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// The resolved candidate strategy owns the recognition policy consumed by the production coordinator: an
    /// aggregate below the default entry threshold still begins a grab when the selected strategy's lower entry
    /// threshold is stable. No coordinator-wide threshold override or power-grip fallback participates.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CandidateStrategySettings_OverrideDefaultRecognitionPolicyInProductionCoordinator()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        GrabbableNode ball = fixture.AddRightBall();
        var defaultPolicy = new FutureStrategy(
            "future-default-policy",
            new PowerGripRecognitionSettings(grabThreshold: 0.75f, releaseThreshold: 0.55f, stabilitySeconds: 0.10f),
            score: 0.65f);
        var candidatePolicy = new FutureStrategy(
            "future-candidate-policy",
            new PowerGripRecognitionSettings(grabThreshold: 0.60f, releaseThreshold: 0.40f, stabilitySeconds: 0.10f),
            score: 0.65f);

        try
        {
            MutableGrabPoint point = ball.GetNode<MutableGrabPoint>("MutableGrabPoint");
            fixture.Coordinator.RecognitionStrategyResolver = new GripRecognitionStrategyResolver([defaultPolicy, candidatePolicy]);
            point.GripRecognitionStrategyName = defaultPolicy.Name;
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);

            await WaitForPhysicsFramesAsync(sceneTree, 20);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            point.GripRecognitionStrategyName = candidatePolicy.Name;
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.True(candidatePolicy.DeriveCallCount > 0);
            Assert.True(candidatePolicy.EvaluateCallCount > 0);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Changing the resolved strategy while an unchanged candidate is accumulating a closed edge discards that
    /// partial interval. The new strategy must earn a complete fresh stability window before it can begin a grab.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StrategyChange_DuringPendingClosedRecognition_ResetsStabilitySafely()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        GrabbableNode ball = fixture.AddRightBall();
        PowerGripRecognitionSettings settings = new(grabThreshold: 0.60f, releaseThreshold: 0.40f, stabilitySeconds: 0.10f);
        var firstStrategy = new FutureStrategy("future-first", settings, score: 0.65f);
        var secondStrategy = new FutureStrategy("future-second", settings, score: 0.65f);

        try
        {
            MutableGrabPoint point = ball.GetNode<MutableGrabPoint>("MutableGrabPoint");
            fixture.Coordinator.RecognitionStrategyResolver = new GripRecognitionStrategyResolver([firstStrategy, secondStrategy]);
            point.GripRecognitionStrategyName = firstStrategy.Name;
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);

            await WaitForPhysicsFramesAsync(sceneTree, 5);
            Assert.True(fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement beforeChange));
            Assert.Equal(GripEdge.Grab, beforeChange.PendingEdge);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            point.GripRecognitionStrategyName = secondStrategy.Name;
            await WaitForPhysicsFramesAsync(sceneTree, 5);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            await WaitForPhysicsFramesAsync(sceneTree, 12);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// A still-selected candidate and still-resolved strategy preserve stability across physics ticks rather than
    /// treating the repeated resolver result as a configuration change.
    /// </summary>
    [Headless]
    [Fact]
    public async Task UnchangedCandidateStrategy_PreservesStabilityAccumulation()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        GrabbableNode ball = fixture.AddRightBall();
        var strategy = new FutureStrategy(
            "future-stable",
            new PowerGripRecognitionSettings(grabThreshold: 0.60f, releaseThreshold: 0.40f, stabilitySeconds: 1.0f),
            score: 0.65f);

        try
        {
            MutableGrabPoint point = ball.GetNode<MutableGrabPoint>("MutableGrabPoint");
            fixture.Coordinator.RecognitionStrategyResolver = new GripRecognitionStrategyResolver([strategy]);
            point.GripRecognitionStrategyName = strategy.Name;
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);

            await WaitForPhysicsFramesAsync(sceneTree, 10);
            Assert.True(fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement first));
            Assert.Equal(GripEdge.Grab, first.PendingEdge);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            await WaitForPhysicsFramesAsync(sceneTree, 10);
            Assert.True(fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement second));
            Assert.Equal(GripEdge.Grab, second.PendingEdge);
            Assert.True(second.StabilitySeconds > first.StabilitySeconds);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 2: a closure ramp crossing the animation-derived threshold, held for the stability interval,
    /// begins exactly one grab: pending state with optical provenance while the item does not move.
    /// </summary>
    [Headless]
    [Fact]
    public async Task ClosureRamp_AcrossThreshold_BeginsOnePendingGrabWithOpticalProvenance()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode ball = fixture.AddRightBall();

        try
        {
            Vector3 ballPositionBefore = ball.GlobalPosition;

            _ = InjectClosureAndHold(sceneTree, fixture.Runtime, LimbSide.Right, calibration.ClosedFlex, physicsFrames: 4);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            // Crossing and holding past the stability interval begins the grab.
            await WaitForPhysicsFramesAsync(sceneTree, 16);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.True(fixture.RightProvider.IsGrabOverrideActive, "Expected the approach to activate the grab override.");
            Assert.True(
                ball.GlobalPosition.DistanceTo(ballPositionBefore) <= 0.0005f,
                "Expected the item to stay in place during the approach.");

            // Holding closed longer never begins a second grab; the hand stays pending (unsettled).
            await WaitForPhysicsFramesAsync(sceneTree, 30);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Independently supplied closed evaluations emit one grab edge, then explicit no-edge evaluations preserve
    /// the same pending attempt until a stable opening emits the sole release edge and cancels it.
    /// </summary>
    [Headless]
    [Fact]
    public async Task StableClosure_NoEdgesPreserveSamePendingAttempt_UntilExplicitReleaseCancelsOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();
        List<OpticalGrabEvaluationTrace> traces = [];
        fixture.Coordinator.OpticalGrabEvaluated += traces.Add;

        try
        {
            OpticalGrabEvaluationTrace grab = await BeginTracedPendingGrabAsync(
                sceneTree,
                fixture,
                calibration,
                traces);

            Assert.Equal(GripEdge.Grab, grab.Edge);
            Assert.Equal(HandGrabLifecycleState.None, grab.LifecycleBefore);
            Assert.Equal(HandGrabLifecycleState.Pending, grab.LifecycleAfter);
            Assert.Equal(OpticalGrabEvaluationReason.GrabStarted, grab.Reason);
            _ = Assert.NotNull(grab.AttemptID);
            _ = Assert.Single(traces, trace => trace.Reason == OpticalGrabEvaluationReason.GrabStarted);

            ulong stableClosedStart = traces[^1].EvaluationID;
            for (int evaluation = 0; evaluation < 8; evaluation++)
            {
                InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            OpticalGrabEvaluationTrace[] stableClosed =
                [.. traces.Where(trace => trace.Side == LimbSide.Right && trace.EvaluationID > stableClosedStart)];
            Assert.True(stableClosed.Length >= 8, "Expected evidence from every independently supplied closed evaluation.");
            Assert.All(stableClosed, trace =>
            {
                Assert.Equal(GripEdge.None, trace.Edge);
                Assert.Equal(OpticalGrabEvaluationReason.NoEdge, trace.Reason);
                Assert.Equal(HandGrabLifecycleState.Pending, trace.LifecycleBefore);
                Assert.Equal(HandGrabLifecycleState.Pending, trace.LifecycleAfter);
                Assert.Equal(grab.AttemptID, trace.AttemptID);
            });
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);

            ulong openingStart = traces[^1].EvaluationID;
            await SupplyPoseUntilReasonAsync(
                sceneTree,
                fixture,
                calibration.OpenFlex,
                traces,
                OpticalGrabEvaluationReason.PendingCancelled);

            OpticalGrabEvaluationTrace[] opening =
                [.. traces.Where(trace => trace.Side == LimbSide.Right && trace.EvaluationID > openingStart)];
            OpticalGrabEvaluationTrace release = Assert.Single(
                opening,
                trace => trace.Reason == OpticalGrabEvaluationReason.PendingCancelled);
            Assert.Equal(GripEdge.Release, release.Edge);
            Assert.Equal(HandGrabLifecycleState.Pending, release.LifecycleBefore);
            Assert.Equal(HandGrabLifecycleState.None, release.LifecycleAfter);
            Assert.Equal(grab.AttemptID, release.AttemptID);
            Assert.All(opening.Take(opening.Length - 1), trace =>
            {
                Assert.Equal(GripEdge.None, trace.Edge);
                Assert.Equal(HandGrabLifecycleState.Pending, trace.LifecycleBefore);
                Assert.Equal(HandGrabLifecycleState.Pending, trace.LifecycleAfter);
                Assert.Equal(grab.AttemptID, trace.AttemptID);
            });
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            _ = Assert.Single(traces, trace => trace.Edge == GripEdge.Grab);
            _ = Assert.Single(traces, trace => trace.Edge == GripEdge.Release);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// If a pending attempt commits between coordinator evaluations, the next explicit no-edge evaluation keeps
    /// that same attempt held; only a later stable opening invokes release once.
    /// </summary>
    [Headless]
    [Fact]
    public async Task CommitBetweenEvaluations_NextNoEdgePreservesSameHeldAttempt_UntilExplicitReleaseOnce()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode ball = fixture.AddRightBall();
        List<OpticalGrabEvaluationTrace> traces = [];
        fixture.Coordinator.OpticalGrabEvaluated += traces.Add;

        try
        {
            OpticalGrabEvaluationTrace grab = await BeginTracedPendingGrabAsync(
                sceneTree,
                fixture,
                calibration,
                traces);

            fixture.Coordinator.SetPhysicsProcess(false);
            SettleAndCommit(fixture, LimbSide.Right);
            await WaitForFramesAsync(sceneTree, 3);
            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(ball, fixture.RightHand.CurrentGrabbed);

            ulong beforeHeldEvaluation = traces[^1].EvaluationID;
            fixture.Coordinator.SetPhysicsProcess(true);
            for (int evaluation = 0; evaluation < 4
                && traces.All(trace => trace.EvaluationID <= beforeHeldEvaluation); evaluation++)
            {
                InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            OpticalGrabEvaluationTrace[] resumed =
                [.. traces.Where(trace => trace.EvaluationID > beforeHeldEvaluation)];
            OpticalGrabEvaluationTrace heldNoEdge = Assert.Single(resumed, trace => trace.Side == LimbSide.Right);
            Assert.Equal(GripEdge.None, heldNoEdge.Edge);
            Assert.Equal(OpticalGrabEvaluationReason.NoEdge, heldNoEdge.Reason);
            Assert.Equal(HandGrabLifecycleState.Held, heldNoEdge.LifecycleBefore);
            Assert.Equal(HandGrabLifecycleState.Held, heldNoEdge.LifecycleAfter);
            Assert.Equal(grab.AttemptID, heldNoEdge.AttemptID);
            Assert.Same(ball, fixture.RightHand.CurrentGrabbed);

            await SupplyPoseUntilReasonAsync(
                sceneTree,
                fixture,
                calibration.OpenFlex,
                traces,
                OpticalGrabEvaluationReason.HeldReleased);

            OpticalGrabEvaluationTrace release = Assert.Single(
                traces,
                trace => trace.Reason == OpticalGrabEvaluationReason.HeldReleased);
            Assert.Equal(GripEdge.Release, release.Edge);
            Assert.Equal(HandGrabLifecycleState.Held, release.LifecycleBefore);
            Assert.Equal(HandGrabLifecycleState.None, release.LifecycleAfter);
            Assert.Equal(grab.AttemptID, release.AttemptID);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            _ = Assert.Single(traces, trace => trace.Edge == GripEdge.Grab);
            _ = Assert.Single(traces, trace => trace.Edge == GripEdge.Release);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 3: a best-candidate change mid-ramp resets the stability accumulator without a premature grab;
    /// the grab occurs only once the new candidate has stayed stable (CTRL-002 TR11; XR-002 TR47).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CandidateChange_MidRamp_ResetsStabilityThenGrabsNewlyStableCandidate()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode nearBall = fixture.AddGrabbable("NearBall", new Vector3(0.55f, 0.0f, 0.0f), GrabBallAnimationPath);
        GrabbableNode farBall = fixture.AddGrabbable("FarBall", new Vector3(0.58f, 0.0f, 0.0f), GrabBallAnimationPath);

        try
        {
            // Accumulate most of a stability interval on the near ball, then make the far ball the best
            // candidate by moving the near one out of reach.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 5);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            CoordinatorFixture.MoveGrabbable(nearBall, new Vector3(2.0f, 0.0f, 0.0f));

            // The accumulated stability restarted at the candidate change: a single stability window after it
            // is still not enough because the near-ball accumulation must not carry over.
            await WaitForPhysicsFramesAsync(sceneTree, 5);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            // Holding closed on the now-stable far candidate for a full fresh interval grabs it.
            await WaitForPhysicsFramesAsync(sceneTree, 12);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Same(farBall, PendingGrabbable(fixture.RightHand));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 4: a stable aggregate opening while pending cancels the pending grab through the abandonment
    /// path — provider override released, hand idle, no held object (INTR-002 R59; CTRL-002 TR14).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingGrab_StableOpening_CancelsThroughAbandonment()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            await BeginPendingGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
            Assert.True(fixture.RightProvider.IsGrabOverrideActive);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 14);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.None, fixture.RightHand.GrabInputSource);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.False(fixture.RightProvider.IsGrabOverrideActive, "Expected the abandonment to release the grab override.");
            Assert.Null(fixture.RightHand.ActiveGrabAnimation);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 5: tracking loss while pending cancels the pending grab (INTR-002 R59; XR-002 TR51).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingGrab_TrackingLoss_CancelsPendingGrab()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();
        List<OpticalGrabEvaluationTrace> traces = [];
        fixture.Coordinator.OpticalGrabEvaluated += traces.Add;

        try
        {
            await BeginPendingGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);

            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Right, joint);
            }

            await WaitForPhysicsFramesAsync(sceneTree, 6);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.False(fixture.RightProvider.IsGrabOverrideActive);
            OpticalGrabEvaluationTrace loss = Assert.Single(traces, trace =>
                trace.Reason == OpticalGrabEvaluationReason.InsufficientValidity
                && trace.LifecycleBefore == HandGrabLifecycleState.Pending);
            Assert.Equal(GripEdge.None, loss.Edge);
            Assert.Equal(HandGrabLifecycleState.None, loss.LifecycleAfter);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 6: an explicit committed mode switch to Controller cancels the pending optical grab without
    /// transferring ownership, and a controller-originated grab works afterwards (INTR-002 R58-59).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingGrab_ExplicitModeSwitchToController_Cancels_AndControllerGrabWorksAfter()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode ball = fixture.AddRightBall();

        try
        {
            await BeginPendingGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.None, fixture.RightHand.GrabInputSource);
            Assert.False(fixture.RightProvider.IsGrabOverrideActive);

            // A controller-originated grab works after the switch, with controller provenance.
            _ = fixture.RightHand.BeginGrab(HandGrabInputSource.Controller);
            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Controller, fixture.RightHand.GrabInputSource);
            Assert.Equal(
                OpticalGrabPresentationState.Tracking,
                fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).State);

            SettleAndCommit(fixture, LimbSide.Right);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Same(ball, fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 7: settling the pending grab at the candidate hand target commits it — held with optical
    /// provenance, the authored grab animation applied through the hand pose state, and the per-hand
    /// arbitration publication set for phase 2b (INTR-002 R59-60; XR-002 TR31).
    /// </summary>
    [Headless]
    [Fact]
    public async Task SettledPendingGrab_Commits_WithAuthoredPoseAndArbitrationPublication()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode ball = fixture.AddRightBall();

        try
        {
            await BeginPendingGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);

            SettleAndCommit(fixture, LimbSide.Right);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            Assert.Same(ball, fixture.RightHand.CurrentGrabbed);
            Assert.Same(fixture.RightHandAttachment, ball.GetParent());

            Animation? appliedPose = fixture.RightHand.CurrentPose;
            Assert.NotNull(appliedPose);
            Assert.Equal("Grab-ball-40", appliedPose.ResourceName);

            OpticalGrabHandPresentation presentation =
                fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right);
            Assert.True(presentation.IsOpticalGrabHeld);
            Assert.Same(appliedPose, presentation.GrabPoseAnimation);

            // The opposite hand stays un-arbitrated.
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Left).IsOpticalGrabHeld);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 8: a stable aggregate opening while held releases through the ordinary Release restoration —
    /// object freed and unparented, authored pose cleared, arbitration withdrawn (INTR-002 R59, R61).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_StableOpening_ReleasesAndClearsAuthoredPose()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        GrabbableNode ball = fixture.AddRightBall();
        Node originalParent = fixture.Root;

        try
        {
            await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
            Assert.NotNull(fixture.RightHand.CurrentPose);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 14);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Same(originalParent, ball.GetParent());
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);

            // The authored pose clears through the ordinary 0.2s blend-out transition.
            await WaitForFramesAsync(sceneTree, 15);
            Assert.Null(fixture.RightHand.CurrentPose);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 9: tracking loss while held preserves the item and the authored pose with no synthetic
    /// release; recovery while still closed keeps the grab, and a stable open after recovery releases
    /// (INTR-002 R59; XR-002 TR51).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_TrackingLoss_PreservesThenRecoversAndReleasesOnStableOpen()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();
        List<OpticalGrabEvaluationTrace> traces = [];
        fixture.Coordinator.OpticalGrabEvaluated += traces.Add;

        try
        {
            await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
            Animation? heldPose = fixture.RightHand.CurrentPose;

            // Loss: the held object and authored pose are preserved; recognition pauses without a release.
            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Right, joint);
            }

            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.NotNull(fixture.RightHand.CurrentGrabbed);
            Assert.Same(heldPose, fixture.RightHand.CurrentPose);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.Contains(traces, trace =>
                trace.Reason == OpticalGrabEvaluationReason.InsufficientValidity
                && trace.Edge == GripEdge.None
                && trace.LifecycleBefore == HandGrabLifecycleState.Held
                && trace.LifecycleAfter == HandGrabLifecycleState.Held);

            // Recovery while still closed: the grab survives; a single stability window of closure changes
            // nothing, and the recovered stability clock means the hold must not release.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);

            // A stable open after recovery releases through the ordinary restoration.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 14);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 9b: unresolved recognition dependencies — a freed finger modifier, or a lost XR runtime —
    /// cancel a pending optical grab before <see cref="HandPoseBehaviour._Process" /> can commit it, publish no
    /// synthetic evaluation, invalidate the stale measurement, and require fresh candidate recognition after
    /// recovery (CTRL-002 TR15; XR-002 TR51; INTR-002 R59).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingGrab_DependencyLoss_CancelsInvalidatesMeasurementAndRequiresFreshRecognition()
    {
        SceneTree sceneTree = GetSceneTree();

        foreach (bool runtimeLoss in new[] { false, true })
        {
            CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
            try
            {
                FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
                _ = fixture.AddRightBall();
                await BeginPendingGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
                Assert.True(
                    fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement beforeLoss));
                Assert.True(beforeLoss.Score.HasValue, "A scored measurement must exist before the loss.");

                List<OpticalGrabEvaluationTrace> traces = [];
                fixture.Coordinator.OpticalGrabEvaluated += traces.Add;

                Node modifierParent = fixture.Modifier.GetParent();
                if (runtimeLoss)
                {
                    typeof(XRManager).GetProperty("Runtime")!.SetValue(fixture.XRManager, null);
                }
                else
                {
                    fixture.Modifier.Free();
                }

                await WaitForPhysicsFramesAsync(sceneTree, 12);

                Assert.Empty(traces);
                Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
                Assert.Equal(HandGrabInputSource.None, fixture.RightHand.GrabInputSource);
                Assert.Null(fixture.RightHand.CurrentGrabbed);
                Assert.False(fixture.RightProvider.IsGrabOverrideActive);
                Assert.True(
                    fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement lost));
                Assert.Equal(OpticalGrabEvaluationReason.DependenciesUnavailable, lost.RejectionCategory);
                Assert.Null(lost.Score);
                Assert.False(lost.SufficientValidity);
                Assert.Equal(GripEdge.None, lost.PendingEdge);
                Assert.Equal(0f, lost.StabilitySeconds);
                Assert.Equal(HandGrabLifecycleState.None, lost.Lifecycle);

                // Recovery: the pipeline returns and the still-valid closure must re-accumulate a full fresh
                // stability interval before a new pending grab begins.
                if (runtimeLoss)
                {
                    typeof(XRManager).GetProperty("Runtime")!.SetValue(fixture.XRManager, fixture.Runtime);
                }
                else
                {
                    OpticalFingerTrackingModifier replacement = new()
                    {
                        Name = "ReplacementOpticalFingerTrackingModifier",
                        CalibrationProfile = CoordinatorFixture.CreateIdentityCalibrationProfile(),
                    };
                    modifierParent.AddChild(replacement);
                }

                InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
                Assert.True(
                    await WaitForReasonAsync(
                        sceneTree,
                        traces,
                        OpticalGrabEvaluationReason.GrabStarted,
                        180),
                    $"Recognition must resume and recognise a fresh grab after {(runtimeLoss ? "runtime" : "modifier")} recovery.");
                Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
                Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);
            }
            finally
            {
                await fixture.DisposeAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Scenario 9c: unresolved recognition dependencies preserve a held optical grab with no synthetic
    /// release and invalidate the stale measurement; after recovery, release recognition restarts from a
    /// fresh stability window, so a probe immediately after recovery cannot have retained accumulation and
    /// only a sustained stable open releases (CTRL-002 TR15; XR-002 TR51; INTR-002 R59).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_DependencyLoss_PreservesGrabAndRequiresFreshStabilityToRelease()
    {
        SceneTree sceneTree = GetSceneTree();

        foreach (bool runtimeLoss in new[] { false, true })
        {
            CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
            try
            {
                FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
                _ = fixture.AddRightBall();
                await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
                Animation? heldPose = fixture.RightHand.CurrentPose;
                Assert.NotNull(fixture.RightHand.CurrentGrabbed);
                Assert.True(
                    fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement beforeLoss));
                Assert.True(beforeLoss.Score.HasValue, "A scored measurement must exist before the loss.");

                List<OpticalGrabEvaluationTrace> traces = [];
                fixture.Coordinator.OpticalGrabEvaluated += traces.Add;

                Node modifierParent = fixture.Modifier.GetParent();
                if (runtimeLoss)
                {
                    typeof(XRManager).GetProperty("Runtime")!.SetValue(fixture.XRManager, null);
                }
                else
                {
                    fixture.Modifier.Free();
                }

                await WaitForPhysicsFramesAsync(sceneTree, 12);

                Assert.Empty(traces);
                Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
                Assert.NotNull(fixture.RightHand.CurrentGrabbed);
                Assert.Same(heldPose, fixture.RightHand.CurrentPose);
                Assert.True(
                    fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement lost));
                Assert.Equal(OpticalGrabEvaluationReason.DependenciesUnavailable, lost.RejectionCategory);
                Assert.Null(lost.Score);
                Assert.False(lost.SufficientValidity);
                Assert.Equal(HandGrabLifecycleState.Held, lost.Lifecycle);
                Assert.Equal(0f, lost.StabilitySeconds);

                // Recovery with a fully open hand: recognition resumes, but the release clock must start from
                // zero — the first valid evaluation after recovery cannot already hold a full stability window.
                if (runtimeLoss)
                {
                    typeof(XRManager).GetProperty("Runtime")!.SetValue(fixture.XRManager, fixture.Runtime);
                }
                else
                {
                    OpticalFingerTrackingModifier replacement = new()
                    {
                        Name = "ReplacementOpticalFingerTrackingModifier",
                        CalibrationProfile = CoordinatorFixture.CreateIdentityCalibrationProfile(),
                    };
                    modifierParent.AddChild(replacement);
                }

                InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
                Assert.True(
                    await WaitForMeasurementAsync(
                        sceneTree,
                        fixture,
                        measurement => measurement.SufficientValidity
                            && measurement.Lifecycle == HandGrabLifecycleState.Held,
                        180),
                    $"Recognition must resume with a valid held measurement after {(runtimeLoss ? "runtime" : "modifier")} recovery.");
                Assert.True(
                    fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement recovered));
                Assert.True(
                    recovered.StabilitySeconds > 0f
                        && recovered.StabilitySeconds < PowerGripRecognitionSettings.Default.StabilitySeconds,
                    "Release recognition must restart from a fresh stability window; retained accumulation would release immediately.");
                Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);

                Assert.True(
                    await WaitForReasonAsync(
                        sceneTree,
                        traces,
                        OpticalGrabEvaluationReason.HeldReleased,
                        180),
                    "A sustained stable open after recovery must release the held grab.");
                Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
                Assert.Null(fixture.RightHand.CurrentGrabbed);
            }
            finally
            {
                await fixture.DisposeAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Waits up to <paramref name="maximumPhysicsFrames" /> physics frames for a right-hand evaluation trace
    /// with the expected reason. The injected pose persists per joint, so no re-injection is needed.
    /// </summary>
    private static async Task<bool> WaitForReasonAsync(
        SceneTree sceneTree,
        List<OpticalGrabEvaluationTrace> traces,
        OpticalGrabEvaluationReason reason,
        int maximumPhysicsFrames)
    {
        for (int frame = 0; frame < maximumPhysicsFrames; frame++)
        {
            if (traces.Any(trace => trace.Side == LimbSide.Right && trace.Reason == reason))
            {
                return true;
            }

            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        return false;
    }

    /// <summary>
    /// Waits up to <paramref name="maximumPhysicsFrames" /> physics frames for the right-hand measurement to
    /// satisfy <paramref name="predicate" />.
    /// </summary>
    private static async Task<bool> WaitForMeasurementAsync(
        SceneTree sceneTree,
        CoordinatorFixture fixture,
        Func<OpticalGripMeasurement, bool> predicate,
        int maximumPhysicsFrames)
    {
        for (int frame = 0; frame < maximumPhysicsFrames; frame++)
        {
            if (fixture.Coordinator.TryGetOpticalGripMeasurement(LimbSide.Right, out OpticalGripMeasurement measurement)
                && predicate(measurement))
            {
                return true;
            }

            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        return false;
    }

    /// <summary>
    /// Scenario 10: an explicit committed mode switch to Controller releases a held optical grab
    /// (INTR-002 R58, R59).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_ExplicitModeSwitchToController_Releases()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);
            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);
            await WaitForPhysicsFramesAsync(sceneTree, 4);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 11: over-clenching beyond the reference articulation while held never releases — only a stable
    /// aggregate opening does (CTRL-002 UR4; XR-002 TR48).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_OverClenchBeyondReference_StaysHeld()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex * 1.8f);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.NotNull(fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 12: a single finger extending while held does not release — the aggregate stays above the
    /// release threshold (CTRL-002 UR4; XR-002 TR48).
    /// </summary>
    [Headless]
    [Fact]
    public async Task HeldGrab_SingleFingerExtension_StaysHeld()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);

            // Open only the index chain; the remaining four chains hold the closed aggregate.
            var indexOpen = new Dictionary<XRHandJoint, float>
            {
                [XRHandJoint.IndexProximal] = calibration.OpenFlex,
                [XRHandJoint.IndexIntermediate] = calibration.OpenFlex,
                [XRHandJoint.IndexDistal] = calibration.OpenFlex,
            };
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex, flexOverrides: indexOpen);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.NotNull(fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 14a: while paused, no optical edges are processed — a pending grab survives an injected opening
    /// through the pause — and after unpause the recognition resumes without a burst: the pending grab is
    /// cancelled once the open pose holds its full stability interval (CTRL-002 TR18; XR-002 TR53).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PauseWhilePending_SuppressesEdges_ThenResumesWithoutBurst()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            await BeginPendingGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);

            sceneTree.Paused = true;
            await WaitForFramesAsync(sceneTree, 2);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);

            sceneTree.Paused = false;
            await WaitForPhysicsFramesAsync(sceneTree, 14);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            sceneTree.Paused = false;
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 14b: while paused with a held grab, an injected opening produces no synthetic release; after
    /// unpause the release fires exactly once the open pose holds its full stability interval.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PauseWhileHeld_PreservesGrabThroughPause_ThenReleasesOnceAfterUnpause()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            await CommitHeldGrabAsync(sceneTree, fixture, LimbSide.Right, calibration);

            sceneTree.Paused = true;
            await WaitForFramesAsync(sceneTree, 2);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForFramesAsync(sceneTree, 30);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.NotNull(fixture.RightHand.CurrentGrabbed);

            sceneTree.Paused = false;
            await WaitForPhysicsFramesAsync(sceneTree, 14);

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
        }
        finally
        {
            sceneTree.Paused = false;
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 15: the two hands operate independently — the left hand grabs a ball while the right stays
    /// idle, then the right hand grabs a stick while the left holds (CTRL-002 UR5).
    /// </summary>
    [Headless]
    [Fact]
    public async Task BothHands_GrabAndHoldIndependently()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration leftCalibration = fixture.Calibrate(GrabBallAnimationPath, LimbSide.Left);
        FlexCalibration rightCalibration = fixture.Calibrate(GrabPipeAnimationPath, LimbSide.Right);
        GrabbableNode leftBall = fixture.AddGrabbable("LeftBall", new Vector3(-0.55f, 0.0f, 0.0f), GrabBallAnimationPath);
        GrabbableNode rightStick = fixture.AddGrabbable("RightStick", new Vector3(0.55f, 0.0f, 0.0f), GrabPipeAnimationPath);

        try
        {
            // The left hand closes on its ball while the right hand stays open and idle.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, leftCalibration.ClosedFlex);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, rightCalibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.LeftHand.GrabLifecycle);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);

            SettleAndCommit(fixture, LimbSide.Left);
            await WaitForFramesAsync(sceneTree, 3);
            Assert.Same(leftBall, fixture.LeftHand.CurrentGrabbed);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Left).IsOpticalGrabHeld);
            Assert.False(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);

            // The right hand closes on its stick while the left keeps holding.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, rightCalibration.ClosedFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabLifecycleState.Held, fixture.LeftHand.GrabLifecycle);

            SettleAndCommit(fixture, LimbSide.Right);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Same(rightStick, fixture.RightHand.CurrentGrabbed);
            Assert.Same(leftBall, fixture.LeftHand.CurrentGrabbed);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Left).IsOpticalGrabHeld);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 16: a cylindrical-pose candidate (the authored pipe grab animation) drives the same generic
    /// recognition path end to end — grab, commit, release — with no grab-point-class branching
    /// (XR-002 TR50; CTRL-002 TR22).
    /// </summary>
    [Headless]
    [Fact]
    public async Task StickCandidate_FullHappyPath_GrabCommitRelease()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabPipeAnimationPath);
        GrabbableNode stick = fixture.AddGrabbable("Stick", new Vector3(0.55f, 0.0f, 0.0f), GrabPipeAnimationPath);

        try
        {
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.ClosedFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 20);

            Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);

            SettleAndCommit(fixture, LimbSide.Right);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.Same(stick, fixture.RightHand.CurrentGrabbed);
            Assert.Equal("Grab-pipe-10", fixture.RightHand.CurrentPose?.ResourceName);
            Assert.True(fixture.XRManager.OpticalGrabArbiter.GetPresentation(LimbSide.Right).IsOpticalGrabHeld);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, calibration.OpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 14);

            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 18: articulation hovering between the release and grab thresholds without stability never
    /// emits an edge (CTRL-002 TR12; XR-002 TR48).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ThresholdChatter_WithoutStability_EmitsNoEdges()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);
        FlexCalibration calibration = fixture.CalibrateRight(GrabBallAnimationPath);
        _ = fixture.AddRightBall();

        try
        {
            for (int frame = 0; frame < 40; frame++)
            {
                InjectTrackedHandPose(
                    fixture.Runtime,
                    LimbSide.Right,
                    frame % 2 == 0 ? calibration.ClosedFlex : calibration.ChatterFlex);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.False(fixture.RightProvider.IsGrabOverrideActive);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 13: controller grab edges act in both committed hand-pose modes — a press begins a grab and a
    /// release ends it even while the committed mode is Optical — while locomotion routing stays active in both
    /// modes and the mode-change analogue-latch reset keeps pressed state from leaking edges across a switch
    /// (CTRL-002 TR6, TR8).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerController_GripInput_ActsInBothCommittedModes_AnalogueLatchResetsOnModeChange()
    {
        SceneTree sceneTree = GetSceneTree();
        PlayerControllerModeFixture fixture = await CreatePlayerControllerModeFixtureAsync(sceneTree);

        try
        {
            fixture.XRManager.SetHandTrackingMode(XRHandTrackingMode.Optical);

            // The analogue press acts in Optical mode: the latch engages and the grab edge is honoured.
            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.8f);
            await WaitForFramesAsync(sceneTree, 2);
            Assert.True(RightFloatLatchIsEngaged(fixture.Controller), "Expected the analogue press to engage the latch.");
            Assert.Equal(1, fixture.RightHand.GrabCallCount);

            // The release edge acts in Optical mode as well, ending whatever that hand holds.
            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.1f);
            Assert.Equal(1, fixture.RightHand.ReleaseCallCount);

            // Locomotion — a non-grab controller consumer — stays active in optical mode.
            Assert.True(IsControllerBound(fixture.Controller), "Expected PlayerController to bind to the fake XR runtime before grab input is emitted.");
            int movesBefore = fixture.Locomotion.MoveCallCount;
            fixture.XRManager.LeftController.TriggerActionVector2Changed("primary", Vector2.One);
            Assert.Equal(movesBefore + 1, fixture.Locomotion.MoveCallCount);

            // An Optical-mode press engages the latch again before the next mode switch.
            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.85f);
            Assert.True(RightFloatLatchIsEngaged(fixture.Controller), "Expected the Optical-mode press to engage the latch.");
            Assert.Equal(2, fixture.RightHand.GrabCallCount);

            // Switching to Controller resets the latch, so the next squeeze is a fresh rising edge.
            fixture.XRManager.SetHandTrackingMode(XRHandTrackingMode.Controller);
            Assert.True(RightFloatLatchIsReset(fixture.Controller), "Expected the mode change to reset the analogue latch.");

            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.95f);
            // Without the mode-change latch reset, the Optical-mode press would leave the latch engaged and
            // suppress this rising edge.
            Assert.Equal(3, fixture.RightHand.GrabCallCount);
            Assert.Equal(1, fixture.RightHand.ReleaseCallCount);

            fixture.XRManager.RightController.TriggerActionFloatInputChanged("grip", 0.1f);
            Assert.Equal(2, fixture.RightHand.ReleaseCallCount);
        }
        finally
        {
            fixture.Global.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// Verifies a temporarily missing hand holder consumes one deferred settling attempt per frame, rather than
    /// recursively queueing deferred calls in one idle-message flush, and resolves a holder added later.
    /// </summary>
    [Headless]
    [Fact]
    public async Task MissingHandHolder_DeferredRetrySettlesThenResolvesLaterHolder()
    {
        SceneTree sceneTree = GetSceneTree();
        CoordinatorFixture fixture = await CoordinatorFixture.CreateAsync(sceneTree);

        try
        {
            HandGrabInputCoordinator coordinator = new()
            {
                Name = "LateHandGrabInputCoordinator",
            };
            fixture.Root.AddChild(coordinator);

            // The holder is absent before the first deferred retry. This used to recursively enqueue retries
            // during a single message-queue flush and exhaust Godot's message queue.
            await WaitForFramesAsync(sceneTree, 2);

            HandComponentHolder replacementHands = new()
            {
                Name = "Hands",
            };
            fixture.Root.AddChild(replacementHands);
            await WaitForFramesAsync(sceneTree, 2);

            Assert.Same(replacementHands, GetResolvedHands(coordinator));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static async Task BeginPendingGrabAsync(
        SceneTree sceneTree,
        CoordinatorFixture fixture,
        LimbSide side,
        FlexCalibration calibration)
    {
        InjectTrackedHandPose(fixture.Runtime, side, calibration.ClosedFlex);
        await WaitForPhysicsFramesAsync(sceneTree, 20);
        Assert.Equal(
            HandGrabLifecycleState.Pending,
            side == LimbSide.Left ? fixture.LeftHand.GrabLifecycle : fixture.RightHand.GrabLifecycle);
        OpticalGrabHandPresentation presentation = fixture.XRManager.OpticalGrabArbiter.GetPresentation(side);
        Assert.Equal(OpticalGrabPresentationState.PendingAssistance, presentation.State);
        Assert.True(presentation.IsOpticalGrabPendingAssistance);
        Assert.False(presentation.IsOpticalGrabHeld);
    }

    private static async Task<OpticalGrabEvaluationTrace> BeginTracedPendingGrabAsync(
        SceneTree sceneTree,
        CoordinatorFixture fixture,
        FlexCalibration calibration,
        List<OpticalGrabEvaluationTrace> traces)
    {
        await SupplyPoseUntilReasonAsync(
            sceneTree,
            fixture,
            calibration.ClosedFlex,
            traces,
            OpticalGrabEvaluationReason.GrabStarted);
        Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
        return Assert.Single(traces, trace => trace.Reason == OpticalGrabEvaluationReason.GrabStarted);
    }

    private static async Task SupplyPoseUntilReasonAsync(
        SceneTree sceneTree,
        CoordinatorFixture fixture,
        float flex,
        List<OpticalGrabEvaluationTrace> traces,
        OpticalGrabEvaluationReason expectedReason)
    {
        for (int evaluation = 0; evaluation < 30; evaluation++)
        {
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, flex);
            await WaitForPhysicsFramesAsync(sceneTree, 1);
            if (traces.Any(trace => trace.Side == LimbSide.Right && trace.Reason == expectedReason))
            {
                return;
            }
        }

        Assert.Fail($"No {expectedReason} trace was observed after 30 independently supplied evaluations.");
    }

    private static async Task CommitHeldGrabAsync(
        SceneTree sceneTree,
        CoordinatorFixture fixture,
        LimbSide side,
        FlexCalibration calibration)
    {
        await BeginPendingGrabAsync(sceneTree, fixture, side, calibration);
        SettleAndCommit(fixture, side);
        await WaitForFramesAsync(sceneTree, 3);
        Assert.Equal(
            HandGrabLifecycleState.Held,
            side == LimbSide.Left ? fixture.LeftHand.GrabLifecycle : fixture.RightHand.GrabLifecycle);
    }

    /// <summary>Keeps a closed pose injected for the given physics frames (the injection persists per joint).</summary>
    private static async Task InjectClosureAndHold(
        SceneTree sceneTree,
        MockXRRuntimeNode runtime,
        LimbSide side,
        float closedFlex,
        int physicsFrames)
    {
        InjectTrackedHandPose(runtime, side, closedFlex);
        await WaitForPhysicsFramesAsync(sceneTree, physicsFrames);
    }

    private static void SettleAndCommit(CoordinatorFixture fixture, LimbSide side)
    {
        Node3D handTarget = side == LimbSide.Left ? fixture.LeftHandTarget : fixture.RightHandTarget;
        HandGrabTargetProvider provider = side == LimbSide.Left ? fixture.LeftProvider : fixture.RightProvider;
        Transform3D grabTarget = provider.GrabTarget;

        handTarget.GlobalTransform = grabTarget;
        SetHandBoneWorldTransform(
            fixture.HandSkeleton,
            side == LimbSide.Left ? "LeftHand" : "RightHand",
            grabTarget);
    }

    private static IGrabbable? PendingGrabbable(HandPoseBehaviour hand)
        => GetPendingGrabStateGrabbable(hand);

    private static IGrabbable? GetPendingGrabStateGrabbable(HandPoseBehaviour hand)
    {
        FieldInfo? field = typeof(HandPoseBehaviour).GetField("_pendingGrabState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        object? state = field.GetValue(hand);
        if (state is null)
        {
            return null;
        }

        PropertyInfo? grabbableProperty = state.GetType().GetProperty("Grabbable");
        Assert.NotNull(grabbableProperty);
        return grabbableProperty.GetValue(state) as IGrabbable;
    }

    private static void SetHandBoneWorldTransform(Skeleton3D skeleton, string boneName, Transform3D worldTransform)
    {
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected the hand skeleton to contain bone '{boneName}'.");
        Transform3D skeletonSpaceTransform = skeleton.GlobalTransform.AffineInverse() * worldTransform;
        skeleton.SetBoneGlobalPose(boneIndex, skeletonSpaceTransform);
        skeleton.ForceUpdateTransform();
    }

    private static bool RightFloatLatchIsEngaged(PlayerController controller)
        => ReadRightFloatLatch(controller);

    private static bool RightFloatLatchIsReset(PlayerController controller)
        => !ReadRightFloatLatch(controller);

    private static bool ReadRightFloatLatch(PlayerController controller)
    {
        FieldInfo? field = typeof(PlayerController).GetField("_rightFloatGrabPressed", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (bool)(field.GetValue(controller) ?? false);
    }

    private static bool IsControllerBound(PlayerController controller)
    {
        FieldInfo? field = typeof(PlayerController).GetField("_isBound", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (bool)(field.GetValue(controller) ?? false);
    }

    private static object? GetResolvedHands(HandGrabInputCoordinator coordinator)
    {
        FieldInfo? field = typeof(HandGrabInputCoordinator).GetField("_hands", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field.GetValue(coordinator);
    }

    private static async Task<PlayerControllerModeFixture> CreatePlayerControllerModeFixtureAsync(SceneTree sceneTree)
    {
        Game global = new()
        {
            Name = "Global",
        };

        ModeSwitchingXRManager xrManager = new()
        {
            Name = "XR",
        };

        Node player = new()
        {
            Name = "Player",
        };

        FakeHands hands = new()
        {
            Name = "Hands",
        };
        FakeHand rightHand = new(LimbSide.Right)
        {
            Name = "RightHand",
        };
        FakeHand leftHand = new(LimbSide.Left)
        {
            Name = "LeftHand",
        };
        CountingLocomotion locomotion = new()
        {
            Name = "Locomotion",
        };
        PlayerController controller = new()
        {
            Name = "PlayerController",
            LocomotionNode = locomotion,
            HandHolderNode = hands,
        };

        hands.AddChild(rightHand);
        hands.AddChild(leftHand);
        player.AddChild(locomotion);
        player.AddChild(hands);
        player.AddChild(controller);
        global.AddChild(xrManager);
        global.AddChild(player);

        global._EnterTree();
        sceneTree.Root.AddChild(global);
        await WaitForFramesAsync(sceneTree, 10);
        xrManager.InitialiseForTests();
        hands._Ready();
        controller._Ready();
        xrManager.EmitInitialisedForTests();
        await WaitForFramesAsync(sceneTree, 3);

        return new PlayerControllerModeFixture(global, xrManager, controller, rightHand, leftHand, locomotion);
    }

    /// <summary>
    /// Deterministic flex-ramp synthesis adapted from the modifier integration fixtures: each joint is injected
    /// as <c>wristRotation * cumulativeLocal</c> with a per-joint local flexion of
    /// <c>Rot(X, JointLocalFlexRadians · flexScale)</c>, so wrist rotation, metacarpal offsets, and chain state
    /// all cancel in the parent-relative quotient that supplies <c>S</c> (XR-002 TR17, TR20).
    /// </summary>
    private static void InjectTrackedHandPose(
        MockXRRuntimeNode runtime,
        LimbSide side,
        float flexScale,
        Dictionary<XRHandJoint, float>? flexOverrides = null)
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

            float jointFlexScale = flexOverrides is not null && flexOverrides.TryGetValue(joint, out float overrideFlex)
                ? overrideFlex
                : flexScale;
            var local = new Quaternion(Vector3.Right, JointLocalFlexRadians(joint) * jointFlexScale);
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
    /// Profile-relative flex scales for one candidate animation: the injected ramp is projected through the
    /// live binding and evaluated by the production strategy until the aggregate crosses the configured
    /// threshold bands, so no magic flex constants exist.
    /// </summary>
    private sealed record FlexCalibration(float ClosedFlex, float OpenFlex, float ChatterFlex);

    private sealed record PlayerControllerModeFixture(
        Game Global,
        ModeSwitchingXRManager XRManager,
        PlayerController Controller,
        FakeHand RightHand,
        FakeHand LeftHand,
        CountingLocomotion Locomotion);

    /// <summary>
    /// Full coordinator rig: mock XR runtime, a bound finger modifier staging the shared projection binding,
    /// a two-sided hand rig, the coordinator, and grabbable factory helpers.
    /// </summary>
    private sealed class CoordinatorFixture(
        TestGame root,
        TestXRManager xrManager,
        MockXRRuntimeNode runtime,
        OpticalFingerTrackingModifier modifier,
        Skeleton3D handSkeleton,
        Node3D rightHandTarget,
        Node3D leftHandTarget,
        BoneAttachment3D rightHandAttachment,
        HandGrabTargetProvider rightProvider,
        HandGrabTargetProvider leftProvider,
        HandPoseBehaviour rightHand,
        HandPoseBehaviour leftHand,
        HandGrabInputCoordinator coordinator)
    {
        public TestGame Root => root;

        public TestXRManager XRManager => xrManager;

        public MockXRRuntimeNode Runtime => runtime;

        public OpticalFingerTrackingModifier Modifier => modifier;

        public Skeleton3D HandSkeleton => handSkeleton;

        public Node3D RightHandTarget => rightHandTarget;

        public Node3D LeftHandTarget => leftHandTarget;

        public BoneAttachment3D RightHandAttachment => rightHandAttachment;

        public HandGrabTargetProvider RightProvider => rightProvider;

        public HandGrabTargetProvider LeftProvider => leftProvider;

        public HandPoseBehaviour RightHand => rightHand;

        public HandPoseBehaviour LeftHand => leftHand;

        public HandGrabInputCoordinator Coordinator => coordinator;

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

        public static void MoveGrabbable(GrabbableNode grabbable, Vector3 targetOrigin)
        {
            grabbable.Position = targetOrigin;
            if (grabbable.GetNodeOrNull<MutableGrabPoint>("MutableGrabPoint") is { } grabPoint)
            {
                grabPoint.TargetOrigin = targetOrigin;
                grabPoint.HandTargetOrigin = targetOrigin;
            }
        }

        public FlexCalibration CalibrateRight(string animationPath)
            => Calibrate(animationPath, LimbSide.Right);

        public FlexCalibration Calibrate(string animationPath, LimbSide side)
        {
            Animation animation = ResourceLoader.Load<Animation>(animationPath)
                ?? throw new InvalidOperationException($"Failed to load grab animation '{animationPath}'.");
            var strategy = new PowerGripRecognitionStrategy();
            Assert.True(
                AuthoredHandPoseReferenceSampler.TrySample(
                    animation.ResourcePath,
                    side,
                    out AuthoredHandPoseSideReference reference,
                    out string sampleError),
                $"Reference sampling failed for '{animationPath}': {sampleError}");

            Span<Quaternion> neutrals = stackalloc Quaternion[OpticalFingerProjectionBinding.FingerBonesPerSide];
            Assert.True(
                Modifier.TryCopyOpticalFingerEffectiveNeutrals(side, neutrals),
                "Expected the staged projection binding to expose the side effective neutrals.");
            Assert.True(
                strategy.TryDeriveProfile(reference, neutrals, PowerGripRecognitionSettings.Default, out IGripRecognitionProfile profile, out string deriveError),
                $"Profile derivation failed for '{animationPath}': {deriveError}");

            PowerGripRecognitionSettings settings = PowerGripRecognitionSettings.Default;
            float closedFlex = -1.0f;
            float chatterFlex = -1.0f;
            float openFlex = 0.0f;
            for (int step = 0; step <= 80; step++)
            {
                float flex = step * 0.05f;
                InjectTrackedHandPose(Runtime, side, flex);
                float? score = TryEvaluateScore(this, strategy, profile, side);
                Assert.True(score.HasValue, $"Score evaluation failed for '{animationPath}' at flex {flex:R}.");
                float value = score!.Value;

                if (closedFlex < 0.0f && value >= MathF.Min(0.95f, settings.GrabThreshold + 0.05f))
                {
                    closedFlex = flex;
                }

                if (value >= settings.ReleaseThreshold + 0.05f
                    && value <= settings.GrabThreshold - 0.05f)
                {
                    chatterFlex = flex;
                }

                if (value <= settings.ReleaseThreshold - 0.15f)
                {
                    openFlex = flex;
                }
            }

            Assert.True(closedFlex > 0.0f, $"No injected flex crossed the grab threshold for '{animationPath}'.");
            Assert.True(chatterFlex > 0.0f, $"No injected flex landed between the thresholds for '{animationPath}'.");
            Assert.True(
                openFlex < closedFlex,
                $"The open flex calibration must sit below the closed flex for '{animationPath}'.");
            return new FlexCalibration(closedFlex, openFlex, chatterFlex);
        }

        private static float? TryEvaluateScore(
            CoordinatorFixture fixture,
            PowerGripRecognitionStrategy strategy,
            IGripRecognitionProfile profile,
            LimbSide side)
        {
            var samples = new XRHandJointSourceSample[TrackedJointCount];
            for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
            {
                _ = fixture.Runtime.TryGetJoint(side, (XRHandJoint)jointIndex, out samples[jointIndex]);
            }

            var poses = new OpticalFingerProjectedPose[OpticalFingerProjectionBinding.FingerBonesPerSide];
            return fixture.Modifier.TryProjectOpticalFingers(side, samples, poses)
                && strategy.TryEvaluate(profile, poses, out GripRecognitionEvaluation evaluation)
                ? evaluation.Score
                : null;
        }

        public static async Task<CoordinatorFixture> CreateAsync(SceneTree sceneTree)
        {
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalGrabCoordinatorFixture",
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

            Skeleton3D fingerSkeleton = CreateFingerSkeleton();
            rigHolder.AddChild(fingerSkeleton);

            OpticalFingerTrackingModifier modifier = new()
            {
                Name = "OpticalFingerTrackingModifier",
                CalibrationProfile = CreateIdentityCalibrationProfile(),
            };
            fingerSkeleton.AddChild(modifier);

            Node3D handRig = new()
            {
                Name = "HandRig",
            };
            root.AddChild(handRig);

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

            AnimationTree animationTree = new()
            {
                Name = "AnimationTree",
                TreeRoot = CreateHandPoseBlendTree(),
                AnimPlayer = new NodePath("../AnimationPlayer"),
                Active = true,
            };
            handRig.AddChild(animationTree);

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

            // The coordinator sits beside the rig root so its descendant modifier search covers the finger
            // skeleton, mirroring the player template where it sits beside PlayerController.
            HandGrabInputCoordinator coordinator = new()
            {
                Name = "HandGrabInputCoordinator",
                HandHolderNode = hands,
            };
            root.AddChild(coordinator);

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

            var fixture = new CoordinatorFixture(
                root,
                xrManager,
                runtime,
                modifier,
                handSkeleton,
                rightHandTarget,
                leftHandTarget,
                rightAttachment,
                rightProvider,
                leftProvider,
                rightHand,
                leftHand,
                coordinator);

            Transform3D rightHandTransform = new(Basis.Identity, new Vector3(0.5f, 0.0f, 0.0f));
            Transform3D leftHandTransform = new(Basis.Identity, new Vector3(-0.5f, 0.0f, 0.0f));
            rightHandTarget.GlobalTransform = rightHandTransform;
            leftHandTarget.GlobalTransform = leftHandTransform;
            SetHandBoneWorldTransform(handSkeleton, "RightHand", rightHandTransform);
            SetHandBoneWorldTransform(handSkeleton, "LeftHand", leftHandTransform);

            Assert.True(
                modifier.IsFingerTopologyValid,
                "The synthetic finger skeleton must satisfy the production bilateral binding gates.");

            // Commit optical mode and enter the modifier's optical session so the shared projection binding
            // stages its calibration.
            _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            InjectTrackedHandPose(runtime, LimbSide.Left, 0.0f);
            InjectTrackedHandPose(runtime, LimbSide.Right, 0.0f);
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

        private static AnimationNodeBlendTree CreateHandPoseBlendTree()
        {
            AnimationNodeBlendTree root = new();
            root.AddNode(HandPoseAnimationTreePaths.LeftHandPoseNode, new AnimationNodeAnimation(), Vector2.Zero);
            root.AddNode(HandPoseAnimationTreePaths.RightHandPoseNode, new AnimationNodeAnimation(), new Vector2(200.0f, 0.0f));
            return root;
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

        internal static OpticalFingerTrackingCalibrationProfile CreateIdentityCalibrationProfile()
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
                Provenance = "Synthetic deterministic identity-S0 profile for optical grab coordinator fixtures.",
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

        public string GripRecognitionStrategyName
        {
            get;
            set;
        } = GripRecognitionStrategies.PowerGrip;

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
                    GripRecognitionStrategyName = GripRecognitionStrategyName,
                };
        }
    }

    private sealed class FutureStrategy(
        string name = "future-precision-stub",
        PowerGripRecognitionSettings? recognitionSettings = null,
        float score = 0.0f) : IGripRecognitionStrategy
    {
        private readonly float _score = score;

        public string Name
        {
            get;
        } = name;

        public PowerGripRecognitionSettings RecognitionSettings
        {
            get;
        } = recognitionSettings ?? PowerGripRecognitionSettings.Default;

        public int DeriveCallCount
        {
            get;
            private set;
        }

        public int EvaluateCallCount
        {
            get;
            private set;
        }

        public bool TryDeriveProfile(
            AuthoredHandPoseSideReference reference,
            ReadOnlySpan<Quaternion> sideEffectiveNeutrals,
            PowerGripRecognitionSettings settings,
            out IGripRecognitionProfile profile,
            out string error)
        {
            DeriveCallCount++;
            Assert.Equal(OpticalFingerProjectionBinding.FingerBonesPerSide, sideEffectiveNeutrals.Length);
            profile = new FutureProfile(reference.Side, Name, settings);
            error = string.Empty;
            return true;
        }

        public bool TryEvaluate(
            IGripRecognitionProfile profile,
            ReadOnlySpan<OpticalFingerProjectedPose> liveDestinationPoses,
            out GripRecognitionEvaluation evaluation)
        {
            EvaluateCallCount++;
            evaluation = new GripRecognitionEvaluation(_score, SufficientValidity: true);
            return profile is FutureProfile { StrategyName: var strategyName } && strategyName == Name;
        }
    }

    private sealed record FutureProfile(
        LimbSide Side,
        string StrategyName,
        PowerGripRecognitionSettings Settings) : IGripRecognitionProfile;

    private sealed partial class FakeHands : Node, IHasHands
    {
        private IComponent[] _components = [];

        public IReadOnlyList<IComponent> Components => _components;

        public override void _Ready() => _components = [.. GetChildren().OfType<IComponent>()];
    }

    private sealed partial class FakeHand(LimbSide side) : Node, IHand
    {
        public LimbSide Side => side;

        public IGrabbable? CurrentGrabbed => null;

        public int GrabCallCount
        {
            get;
            private set;
        }

        public int ReleaseCallCount
        {
            get;
            private set;
        }

        public IGrabbable? Grab()
        {
            GrabCallCount++;
            return null;
        }

        public void Release() => ReleaseCallCount++;
    }

    private sealed partial class CountingLocomotion : Node, AlleyCat.Control.Locomotion.ILocomotion
    {
        public int MoveCallCount
        {
            get;
            private set;
        }

        public void Move(Vector2 input)
        {
            MoveCallCount++;
            _ = input;
        }

        public void Rotate(Vector2 input) => _ = input;
    }

    /// <summary>
    /// Fake XR manager whose runtime's committed hand-pose mode is test-settable and raises the runtime and
    /// manager mode-change notifications exactly like the real boundary (XR-001 TR6).
    /// </summary>
    private sealed partial class ModeSwitchingXRManager : XRManager
    {
        private static readonly FieldInfo _runtimeBackingField = typeof(XRManager)
            .GetField("<Runtime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected the XRManager runtime backing field.");

        private readonly ModeSwitchingXRRuntime _runtime = new();

        public FakeXRHandController RightController => _runtime.RightHandControllerNode;

        public FakeXRHandController LeftController => _runtime.LeftHandControllerNode;

        public override void _Ready()
        {
        }

        public void InitialiseForTests()
        {
            _runtimeBackingField.SetValue(this, _runtime);
            InitialisationAttempted = true;
            InitialisationSucceeded = true;
        }

        public void EmitInitialisedForTests()
            => _ = EmitSignal(SignalName.Initialised, true);

        public void SetHandTrackingMode(XRHandTrackingMode mode)
        {
            _runtime.SetMode(mode);
            _ = EmitSignal(SignalName.HandTrackingModeChanged);
        }
    }

    private sealed class ModeSwitchingXRRuntime : IXRRuntime
    {
        private readonly XRControllerHandTracking _handTracking;

        public ModeSwitchingXRRuntime()
        {
            OriginNode = new Node3D();
            CameraNode = new Camera3D();
            RightHandControllerNode = new FakeXRHandController();
            LeftHandControllerNode = new FakeXRHandController();
            _handTracking = new XRControllerHandTracking(RightHandControllerNode, LeftHandControllerNode);
        }

        public IXROrigin Origin => new FakeXROrigin(OriginNode);

        public IXRCamera Camera => new FakeXRCamera(CameraNode);

        public IXRHandController RightHandController => RightHandControllerNode;

        public IXRHandController LeftHandController => LeftHandControllerNode;

        public XRHandTrackingMode HandTrackingMode
        {
            get;
            private set;
        }

        public IXRHandJointProvider OpticalHandJoints => XREmptyHandJointProvider.Instance;

        public IXRHandPoseSource GetHandPoseSource(LimbSide side) => _handTracking.GetHandPoseSource(side);

#pragma warning disable CS0067
        public event Action? PoseRecentered;

        public event Action? HandTrackingModeChanged;
#pragma warning restore CS0067

        public Node3D OriginNode
        {
            get;
        }

        public Camera3D CameraNode
        {
            get;
        }

        public FakeXRHandController RightHandControllerNode
        {
            get;
        }

        public FakeXRHandController LeftHandControllerNode
        {
            get;
        }

        public bool Initialise(SubViewport viewport, int maximumRefreshRate)
        {
            _ = viewport;
            _ = maximumRefreshRate;
            return true;
        }

        public void SetMode(XRHandTrackingMode mode)
        {
            if (mode == HandTrackingMode)
            {
                return;
            }

            HandTrackingMode = mode;
            HandTrackingModeChanged?.Invoke();
        }
    }

    private sealed partial class FakeXRHandController : Node3D, IXRHandController
    {
#pragma warning disable CS0067
        public event Action<string>? ActionButtonPressed;

        public event Action<string>? ActionButtonReleased;

        public event Action<string, float>? ActionFloatInputChanged;

        public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

        public Node3D ControllerNode => this;

        public Node3D HandPositionNode => this;

        public void TriggerActionFloatInputChanged(string actionName, float value)
            => ActionFloatInputChanged?.Invoke(actionName, value);

        public void TriggerActionButtonPressed(string actionName)
            => ActionButtonPressed?.Invoke(actionName);

        public void TriggerActionVector2Changed(string actionName, Vector2 value)
            => ActionVector2InputChanged?.Invoke(actionName, value);
    }

    private sealed record FakeXROrigin(Node3D OriginNode) : IXROrigin
    {
        public float WorldScale { get; set; } = 1.0f;
    }

    private sealed record FakeXRCamera(Camera3D CameraNode) : IXRCamera;

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime)
        {
            Runtime = runtime;
            runtime.HandTrackingModeChanged += EmitHandTrackingModeChangedSignal;
        }

        private void EmitHandTrackingModeChangedSignal()
            => _ = EmitSignal(SignalName.HandTrackingModeChanged);
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
