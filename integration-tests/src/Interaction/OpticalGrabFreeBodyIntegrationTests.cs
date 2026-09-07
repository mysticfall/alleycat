using AlleyCat.Control;
using AlleyCat.Control.Hands;
using AlleyCat.IK;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Free-body regression coverage for the pending-grab approach against a REAL unfrozen
/// <c>test_ball.tscn</c> resting on an authored static table — the resting-item condition every existing
/// photobooth fixture freezes. Verifies the pending-candidate collision protection contract (INTR-002
/// pending approach protection): while a movable grab is pending, the authorised approach must not push the
/// candidate (rig proxies exempted, hand IK target exempted, explicit interaction channel suppressed), the
/// protection must fully revert on abandon, commit→release, and candidate switch, open hands must still push
/// non-candidate items, and a persistently moving candidate must abandon with the distinct
/// <see cref="HandGrabAbandonmentReason.MovingCandidate" /> reason surfaced through the coordinator trace.
/// </summary>
/// <remarks>
/// <para>
/// The fixture reuses the real reference-player photobooth rig (production coordinator, modifier, hands,
/// VRIK, physical proxy rig) with the frozen showcase items moved away, a static table authored in the
/// fixture, and a freshly instanced unfrozen ball. The table sits on the item layer (layer 2) with an
/// item-layer mask so the ball's layer 2 physically rests on it, while the hand IK target body (mask 5)
/// never sweeps against it — the approach corridor stays clear by the layer contract. The ball rests clear
/// of the character's body-proxy envelope, with no walls in the corridor.
/// </para>
/// <para>
/// Inputs are frozen authored optical samples (wrist trajectory plus flex ramp constants identical to the
/// photobooth frozen-input trial); the wrist approaches with the authored side/cupping orientation rather
/// than palm-down-from-above.
/// </para>
/// </remarks>
public sealed class OpticalGrabFreeBodyIntegrationTests
{
    private const string PhotoboothScenePath = "res://tests/interaction/optical_grab_photobooth.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string TestBallScenePath = "res://assets/items/test_ball.tscn";

    private const float AuthoredTrialOpenFlex = 0.15f;

    private const float AuthoredBallOpenFlex = 1.10f;

    private const float AuthoredBallClosedFlex = 1.70f;

    /// <summary>Velocity bound the protected candidate must stay below while pending, after warm-up ticks.</summary>
    private const float PendingCandidateSpeedBoundMetresPerSecond = 0.05f;

    /// <summary>Displacement bound from rest while any attempt is pending (grasp reach window).</summary>
    private const float PendingCandidateDisplacementBoundMetres = 0.05f;

    /// <summary>Physics-tick budget within which the authored free-ball closure must commit.</summary>
    private const int CommitPhysicsTickBudget = 300;

    /// <summary>Adjacent-item discontinuity bound across the commit boundary (existing continuity contract).</summary>
    private const float CommitBoundaryDiscontinuityMetres = 0.010f;

    /// <summary>Warm-up pending samples skipped before the velocity bound applies (settle transients).</summary>
    private const int PendingVelocityWarmUpSamples = 5;

    /// <summary>
    /// Sustained vibration speed used to keep a candidate persistently moving in the P4 scenario. The
    /// direction alternates every physics tick; after table-contact friction (≈0.165 m/s per tick) the
    /// per-tick destination swing peaks beyond the 8 mm settlement tolerance on both sides, so the approach
    /// cannot accumulate the two settle frames needed to commit onto the vibrating candidate.
    /// </summary>
    private const float ScriptedCandidateSpeedMetresPerSecond = 1.6f;

    /// <summary>Physics-tick budget within which the persistently moving candidate must abandon.</summary>
    private const int MovingCandidateBudgetTicks = 240;

    private static readonly Vector3 _rightWristRest = new(0.25f, 1.08f, -0.43f);

    /// <summary>
    /// Authored rest wrist orientation: the wrist stays inside the ball's recognition reach (0.12 m), but the
    /// open hand is rotated away from the ball so its proxies cannot rest against the free item before the
    /// grip closes — without this the parked open hand itself drifts the ball off the table.
    /// </summary>
    private static readonly Basis _rightWristRestBasis = Basis.Identity.Rotated(Vector3.Right, Mathf.Pi);

    /// <summary>
    /// Separately authored terminal wrist pose for the free-ball trial — the diagnosed side/cupping
    /// orientation, identical to the photobooth frozen-input convention; frozen fixture input, never
    /// recomputed from runtime state.
    /// </summary>
    private static readonly Vector3 _rightWristHeld = new(0.26f, 1.02f, -0.28f);

    private static readonly Vector3 _leftWristRest = new(-0.25f, 1.08f, -0.43f);

    /// <summary>Table-top surface height; the ball (radius 0.04 m) rests with its centre at 1.163 m.</summary>
    private const float TableTopMetres = 1.123f;

    private static readonly Vector3 _tableCentre = new(0.309f, TableTopMetres - 0.03f, -0.427f);

    private static readonly Vector3 _freeBallRest = new(0.309f, TableTopMetres + 0.0405f, -0.427f);

    /// <summary>Rest placement of the replacement ball used by the candidate-switch lifecycle test.</summary>
    private static readonly Vector3 _replacementBallRest = new(0.28f, TableTopMetres + 0.0405f, -0.36f);

    /// <summary>
    /// (a) While a recognised closure drives the approach, the free resting ball is not pushed: its linear
    /// velocity stays below the pending bound after the warm-up ticks and its displacement from rest stays
    /// within the grasp reach window — without protection the approach ejects the ball outright.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingApproach_FreeBallStaysAtRestUnderProtection()
    {
        SceneTree sceneTree = GetSceneTree();
        using FreeBodyFixture fixture = await FreeBodyFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            Vector3 restPosition = fixture.Ball.GlobalPosition;
            List<FreeBodySample> samples = await DriveAuthoredClosureAsync(
                sceneTree,
                fixture,
                new FlexCalibration(AuthoredBallClosedFlex, AuthoredBallOpenFlex),
                _rightWristRest,
                _rightWristHeld,
                fixture.Ball,
                maximumFrames: CommitPhysicsTickBudget);

            List<FreeBodySample> pendingSamples = [.. samples.Where(sample => sample.Lifecycle == HandGrabLifecycleState.Pending)];
            Assert.True(pendingSamples.Count > PendingVelocityWarmUpSamples, $"Expected a sustained pending window.\n{FormatSamples(samples)}");

            foreach (FreeBodySample sample in pendingSamples.Skip(PendingVelocityWarmUpSamples))
            {
                Assert.True(
                    sample.ItemVelocity.Length() <= PendingCandidateSpeedBoundMetresPerSecond,
                    $"The protected free ball must stay below {PendingCandidateSpeedBoundMetresPerSecond:F3} m/s while "
                    + $"pending; observed {sample.ItemVelocity.Length():F4} m/s at tick {sample.PhysicsTick}.\n{FormatSamples(samples)}");
                Assert.True(
                    restPosition.DistanceTo(sample.ItemTransform.Origin) <= PendingCandidateDisplacementBoundMetres,
                    $"The protected free ball must stay within {PendingCandidateDisplacementBoundMetres:F3} m of rest while "
                    + $"pending; observed {restPosition.DistanceTo(sample.ItemTransform.Origin):F4} m at tick {sample.PhysicsTick}.\n{FormatSamples(samples)}");
            }

            // The attempt must also end committed — the calm approach converged instead of churning retries.
            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.Equal(HandGrabAbandonmentReason.None, fixture.RightHand.LastPendingGrabAbandonmentReason);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// (b)+(c) A suitable-orientation authored closure commits <see cref="HandGrabLifecycleState.Held" />
    /// within a bounded physics-tick budget — with protection the destination is no longer chased away —
    /// and the commit boundary shows at most 10 mm adjacent item discontinuity, matching the existing
    /// continuity contracts.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AuthoredClosure_FreeBallCommitsHeldWithinBudgetWithContinuousBoundary()
    {
        SceneTree sceneTree = GetSceneTree();
        using FreeBodyFixture fixture = await FreeBodyFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            ulong closureStartTick = Engine.GetPhysicsFrames();
            List<FreeBodySample> samples = await DriveAuthoredClosureAsync(
                sceneTree,
                fixture,
                new FlexCalibration(AuthoredBallClosedFlex, AuthoredBallOpenFlex),
                _rightWristRest,
                _rightWristHeld,
                fixture.Ball,
                maximumFrames: CommitPhysicsTickBudget);

            int heldIndex = samples.FindIndex(sample => sample.Lifecycle == HandGrabLifecycleState.Held);
            Assert.True(heldIndex >= 2, $"Expected the authored free-ball closure to commit Held.\n{FormatSamples(samples)}");
            Assert.Equal(HandGrabLifecycleState.Pending, samples[heldIndex - 1].Lifecycle);

            ulong commitTick = samples[heldIndex].PhysicsTick;
            Assert.True(
                commitTick - closureStartTick <= CommitPhysicsTickBudget,
                $"The free-ball closure must commit within {CommitPhysicsTickBudget} physics ticks; observed "
                + $"{commitTick - closureStartTick} ticks.\n{FormatSamples(samples)}");
            Assert.Same(fixture.Ball, fixture.RightHand.CurrentGrabbed);
            Assert.Equal(HandGrabInputSource.Optical, fixture.RightHand.GrabInputSource);

            float boundaryDiscontinuity = samples[heldIndex - 1].ItemTransform.Origin.DistanceTo(
                samples[heldIndex].ItemTransform.Origin);
            Assert.True(
                boundaryDiscontinuity <= CommitBoundaryDiscontinuityMetres,
                $"The commit boundary must move the item at most {CommitBoundaryDiscontinuityMetres:F3} m; observed "
                + $"{boundaryDiscontinuity:F4} m.\n{FormatSamples(samples)}");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// (d, part 1) Protection lifecycle: while pending, the free ball carries mutual collision exceptions
    /// against the hand IK target body and the same-side rig proxies and is marked approach-protected;
    /// abandonment fully reverts, and a candidate switch across attempts moves the protection to the newly
    /// selected ball while the old body stays reverted.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingProtection_AbandonAndCandidateSwitch_FullyRevertedAndMoved()
    {
        SceneTree sceneTree = GetSceneTree();
        using FreeBodyFixture fixture = await FreeBodyFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            await BeginClosureAndWaitForPendingAsync(sceneTree, fixture);

            Assert.True(fixture.Ball.IsInGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName));
            AssertBallProtectedAgainstHand(fixture, fixture.Ball);

            // Abandonment reverts everything synchronously.
            Assert.True(fixture.RightHand.CancelPendingGrab());
            Assert.False(fixture.Ball.IsInGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName));
            AssertBallUnprotectedAgainstHand(fixture, fixture.Ball);

            // Candidate switch across attempts: the moved-away ball loses candidacy; a newly rested ball
            // gains it, and protection follows the new candidate while the old stays reverted.
            fixture.Ball.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(10.0f, 0.5f, 10.0f));
            fixture.Ball.ForceUpdateTransform();
            await WaitForPhysicsFramesAsync(sceneTree, 5);
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            AssertBallUnprotectedAgainstHand(fixture, fixture.Ball);

            // Re-open the grip so a fresh closure can begin against the newly placed ball.
            OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, AuthoredBallOpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 5);
            RigidBody3D replacementBall = fixture.AddFreeBall("FreeBallB", _replacementBallRest);
            await WaitForPhysicsFramesAsync(sceneTree, 30);
            await BeginClosureAndWaitForPendingAsync(sceneTree, fixture);

            Assert.True(
                fixture.RightHand.TryGetCurrentGrabCandidate(out GrabCandidateObservation? observed),
                "Expected the replacement ball to be the observed best candidate.");
            Assert.Same(replacementBall, observed?.Grabbable);
            Assert.True(
                replacementBall.IsInGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName),
                "The newly selected candidate must be approach-protected.");
            AssertBallProtectedAgainstHand(fixture, replacementBall);
            AssertBallUnprotectedAgainstHand(fixture, fixture.Ball);

            Assert.True(fixture.RightHand.CancelPendingGrab());
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// (d, part 2) Commit transitions the protection cleanly into the held exception contract — no gap, no
    /// double application — and release fully reverts both layers.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PendingProtection_CommitHoldsExceptionsAndReleaseFullyReverts()
    {
        SceneTree sceneTree = GetSceneTree();
        using FreeBodyFixture fixture = await FreeBodyFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            _ = await DriveAuthoredClosureAsync(
                sceneTree,
                fixture,
                new FlexCalibration(AuthoredBallClosedFlex, AuthoredBallOpenFlex),
                _rightWristRest,
                _rightWristHeld,
                fixture.Ball,
                maximumFrames: CommitPhysicsTickBudget);

            Assert.Equal(HandGrabLifecycleState.Held, fixture.RightHand.GrabLifecycle);
            Assert.True(fixture.Ball.Freeze, "The committed grabbable must freeze through the held contract.");
            // The approach-protection mark is gone; the held exception contract owns the pairs now.
            Assert.False(fixture.Ball.IsInGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName));
            AssertBallProtectedAgainstHand(fixture, fixture.Ball);

            fixture.RightHand.Release();
            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.False(fixture.Ball.Freeze);
            Assert.False(fixture.Ball.IsInGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName));
            AssertBallUnprotectedAgainstHand(fixture, fixture.Ball);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// (d, positive control) With NO pending grab, an open hand driving into the free ball CAN push it — the
    /// explicit interaction channel and the kinematic hand proxies stay active for non-candidate items.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpenHand_NoPendingGrab_PushesFreeBall()
    {
        SceneTree sceneTree = GetSceneTree();
        using FreeBodyFixture fixture = await FreeBodyFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            Vector3 restPosition = fixture.Ball.GlobalPosition;
            float maximumSpeed = 0.0f;

            // Open grip (no closure edge, so no grab) while the wrist sweeps into the ball's rest position.
            const int sweepFrames = 12;
            for (int frame = 1; frame <= 90; frame++)
            {
                float sweepAlpha = Mathf.Clamp(frame / (float)sweepFrames, 0.0f, 1.0f);
                Vector3 wrist = _rightWristRest.Lerp(_freeBallRest, sweepAlpha);
                OpticalGrabPhotoboothIntegrationTests.SetExactOpticalWristWorld(fixture.Runtime, LimbSide.Right, new Transform3D(Basis.Identity, wrist));
                OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, AuthoredTrialOpenFlex);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
                await WaitForNextFrameAsync(sceneTree);

                Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
                maximumSpeed = MathF.Max(maximumSpeed, fixture.Ball.LinearVelocity.Length());
                if (maximumSpeed > PendingCandidateSpeedBoundMetresPerSecond
                    && restPosition.DistanceTo(fixture.Ball.GlobalPosition) > 0.02f)
                {
                    break;
                }
            }

            Assert.True(
                maximumSpeed > PendingCandidateSpeedBoundMetresPerSecond,
                $"An open hand with no pending grab must be able to push the free ball; observed maximum speed "
                + $"{maximumSpeed:F4} m/s.");
            Assert.True(
                restPosition.DistanceTo(fixture.Ball.GlobalPosition) > 0.02f,
                $"An open hand with no pending grab must displace the free ball; observed "
                + $"{restPosition.DistanceTo(fixture.Ball.GlobalPosition):F4} m.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// (e) P4: a deliberately moving candidate — scripted sustained oscillation above the moving-candidate
    /// threshold — abandons the pending grab with the distinct
    /// <see cref="HandGrabAbandonmentReason.MovingCandidate" /> reason, surfaced both through the lifecycle
    /// diagnostic and the coordinator's neutral evaluation trace, without committing.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PersistentlyMovingCandidate_AbandonsPendingWithDistinctReasonAndTrace()
    {
        SceneTree sceneTree = GetSceneTree();
        using FreeBodyFixture fixture = await FreeBodyFixture.CreateAsync(sceneTree);

        try
        {
            await fixture.StartOpticalAtRestAsync(sceneTree);
            List<OpticalGrabEvaluationTrace> traces = [];
            fixture.Coordinator.OpticalGrabEvaluated += traces.Add;
            await BeginClosureAndWaitForPendingAsync(sceneTree, fixture);

            ulong startTick = Engine.GetPhysicsFrames();
            while (fixture.RightHand.GrabLifecycle != HandGrabLifecycleState.None
                   && Engine.GetPhysicsFrames() - startTick < MovingCandidateBudgetTicks)
            {
                // Scripted candidate motion: a tick-alternating horizontal vibration well above the
                // moving-candidate threshold, with displacement small enough to keep the candidate
                // selectable while the settlement destination stays outside the commit gate.
                fixture.Ball.Sleeping = false;
                float direction = Engine.GetPhysicsFrames() % 2 == 0 ? 1.0f : -1.0f;
                fixture.Ball.LinearVelocity = new Vector3(direction * ScriptedCandidateSpeedMetresPerSecond, 0.0f, 0.0f);
                OpticalGrabPhotoboothIntegrationTests.SetExactOpticalWristWorld(fixture.Runtime, LimbSide.Right, new Transform3D(_rightWristRestBasis, _rightWristRest));
                OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, AuthoredBallClosedFlex);
                await WaitForPhysicsFramesAsync(sceneTree, 1);
            }

            Assert.Equal(HandGrabLifecycleState.None, fixture.RightHand.GrabLifecycle);
            Assert.Null(fixture.RightHand.CurrentGrabbed);
            Assert.Equal(HandGrabAbandonmentReason.MovingCandidate, fixture.RightHand.LastPendingGrabAbandonmentReason);
            Assert.False(fixture.Ball.Freeze, "A never-committed candidate must stay unfrozen.");
            Assert.False(fixture.Ball.IsInGroup(HandDynamicBodyInteractionController.ApproachProtectedBodyGroupName));

            // The abandonment lands in a process frame; let the coordinator's next physics evaluation publish
            // the neutral trace before asserting it.
            await WaitForPhysicsFramesAsync(sceneTree, 3);
            Assert.Contains(
                traces,
                trace => trace.Side == LimbSide.Right
                    && trace.Edge == GripEdge.None
                    && trace.LifecycleBefore == HandGrabLifecycleState.Pending
                    && trace.LifecycleAfter == HandGrabLifecycleState.None
                    && trace.Reason == OpticalGrabEvaluationReason.PendingAbandonedMovingCandidate
                    && trace.AttemptID is not null);

            // Stop the still-closed grip from immediately re-entering recognition before disposal.
            OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, AuthoredBallOpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 5);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static async Task BeginClosureAndWaitForPendingAsync(SceneTree sceneTree, FreeBodyFixture fixture)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.None && stopwatch.Elapsed.TotalSeconds < 5.0)
        {
            OpticalGrabPhotoboothIntegrationTests.SetExactOpticalWristWorld(fixture.Runtime, LimbSide.Right, new Transform3D(_rightWristRestBasis, _rightWristRest));
            OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, AuthoredBallClosedFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 1);
        }

        Assert.Equal(HandGrabLifecycleState.Pending, fixture.RightHand.GrabLifecycle);
    }

    /// <summary>
    /// Drives the frozen authored closure trial: a ten-frame flex ramp from open to closed while the wrist
    /// travels from its rest pose to the authored held pose after a delay, sampling the item every frame
    /// until the grab commits or the frame budget ends. No candidate, provider, solver, or lifecycle
    /// observation alters a later wrist or articulation sample.
    /// </summary>
    private static async Task<List<FreeBodySample>> DriveAuthoredClosureAsync(
        SceneTree sceneTree,
        FreeBodyFixture fixture,
        FlexCalibration calibration,
        Vector3 wristStart,
        Vector3 wristEnd,
        RigidBody3D item,
        int maximumFrames)
    {
        const int closureRampFrames = 10;
        const int wristTrajectoryDelayFrames = 18;
        const int wristTrajectoryFrames = 30;
        List<FreeBodySample> samples = [];

        for (int frame = 0; frame < maximumFrames; frame++)
        {
            float closureAlpha = Mathf.Clamp((frame + 1.0f) / closureRampFrames, 0.0f, 1.0f);
            float wristAlpha = Mathf.Clamp(
                (frame + 1.0f - wristTrajectoryDelayFrames) / wristTrajectoryFrames,
                0.0f,
                1.0f);
            Basis wristBasis = _rightWristRestBasis.Slerp(Basis.Identity, wristAlpha).Orthonormalized();
            OpticalGrabPhotoboothIntegrationTests.SetExactOpticalWristWorld(
                fixture.Runtime,
                LimbSide.Right,
                new Transform3D(wristBasis, wristStart.Lerp(wristEnd, wristAlpha)));
            OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(
                fixture.Runtime,
                LimbSide.Right,
                Mathf.Lerp(calibration.OpenFlex, calibration.ClosedFlex, closureAlpha));
            await WaitForPhysicsFramesAsync(sceneTree, 1);
            await WaitForNextFrameAsync(sceneTree);
            samples.Add(CaptureSample(fixture, item));

            if (fixture.RightHand.GrabLifecycle == HandGrabLifecycleState.Held)
            {
                break;
            }
        }

        return samples;
    }

    private static FreeBodySample CaptureSample(FreeBodyFixture fixture, RigidBody3D item)
        => new(
            Engine.GetPhysicsFrames(),
            fixture.RightHand.GrabLifecycle,
            item.GlobalTransform,
            item.LinearVelocity);

    private static void AssertBallProtectedAgainstHand(FreeBodyFixture fixture, RigidBody3D ball)
    {
        AssertBodiesHaveMutualCollisionException(ball, fixture.HandTargetBody);
        foreach (PhysicsBody3D proxy in fixture.EnumerateSameSideHandProxyBodies())
        {
            AssertBodiesHaveMutualCollisionException(ball, proxy);
        }
    }

    private static void AssertBallUnprotectedAgainstHand(FreeBodyFixture fixture, RigidBody3D ball)
    {
        AssertBodiesHaveNoCollisionException(ball, fixture.HandTargetBody);
        foreach (PhysicsBody3D proxy in fixture.EnumerateSameSideHandProxyBodies())
        {
            AssertBodiesHaveNoCollisionException(ball, proxy);
        }
    }

    private static void AssertBodiesHaveMutualCollisionException(PhysicsBody3D first, PhysicsBody3D second)
    {
        Assert.Contains(first.GetCollisionExceptions(), body => ReferenceEquals(body, second));
        Assert.Contains(second.GetCollisionExceptions(), body => ReferenceEquals(body, first));
    }

    private static void AssertBodiesHaveNoCollisionException(PhysicsBody3D first, PhysicsBody3D second)
    {
        Assert.DoesNotContain(first.GetCollisionExceptions(), body => ReferenceEquals(body, second));
        Assert.DoesNotContain(second.GetCollisionExceptions(), body => ReferenceEquals(body, first));
    }

    private static string FormatSamples(List<FreeBodySample> samples)
        => string.Join(
            System.Environment.NewLine,
            samples.Select((sample, index) =>
                $"[{index:D3}] pt={sample.PhysicsTick} lifecycle={sample.Lifecycle} item={sample.ItemTransform.Origin} v={sample.ItemVelocity.Length():F4}"));

    private readonly record struct FlexCalibration(float ClosedFlex, float OpenFlex);

    private readonly record struct FreeBodySample(
        ulong PhysicsTick,
        HandGrabLifecycleState Lifecycle,
        Transform3D ItemTransform,
        Vector3 ItemVelocity);

    /// <summary>
    /// Real-rig fixture: the shared interaction photobooth scene with the installed player rig, the frozen
    /// showcase items moved away, an authored static table, and a freshly instanced unfrozen test ball
    /// resting on it.
    /// </summary>
    private sealed class FreeBodyFixture : IDisposable
    {
        private FreeBodyFixture(
            TestGame root,
            TestXRManager xrManager,
            MockXRRuntimeNode runtime,
            Node photobooth,
            Node playerRoot,
            RigidBody3D ball,
            AnimatableBody3D handTargetBody,
            HandPoseBehaviour rightHand,
            OpticalFingerTrackingModifier modifier)
        {
            Root = root;
            XRManager = xrManager;
            Runtime = runtime;
            Photobooth = photobooth;
            PlayerRoot = playerRoot;
            Ball = ball;
            HandTargetBody = handTargetBody;
            RightHand = rightHand;
            Modifier = modifier;
        }

        public TestGame Root
        {
            get;
        }

        public TestXRManager XRManager
        {
            get;
        }

        public MockXRRuntimeNode Runtime
        {
            get;
        }

        private Node Photobooth
        {
            get;
        }

        public Node PlayerRoot
        {
            get;
        }

        public RigidBody3D Ball
        {
            get;
        }

        public AnimatableBody3D HandTargetBody
        {
            get;
        }

        public HandPoseBehaviour RightHand
        {
            get;
        }

        public OpticalFingerTrackingModifier Modifier
        {
            get;
        }

        public HandGrabInputCoordinator Coordinator
            => PlayerRoot.GetNode<HandGrabInputCoordinator>("HandGrabInputCoordinator");

        public static async Task<FreeBodyFixture> CreateAsync(SceneTree sceneTree)
        {
            // The integration runner starts test bodies inside TestRuntimeRunner._Ready, where scene-tree
            // attachment does not propagate until the first process frame; wait one frame before attaching.
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalGrabFreeBodyFixture",
            };

            TestXRManager xrManager = new()
            {
                Name = "XR",
            };
            root.AddChild(xrManager);

            Node3D originHolder = new()
            {
                Name = "OriginHolder",
            };
            root.AddChild(originHolder);

            MockXRRuntimeNode runtime = LoadPackedScene(MockRuntimeScenePath).Instantiate<MockXRRuntimeNode>();
            originHolder.AddChild(runtime);
            _ = runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
            xrManager.SetRuntime(runtime);

            Node photobooth = LoadPackedScene(PhotoboothScenePath).Instantiate();
            root.AddChild(photobooth);

            Node playerRoot = photobooth.GetNode("Subject/Player");
            EnsureCharacterRuntimeInstalled(playerRoot);

            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);

            // The mock head camera drives the VRIK head target at a natural standing viewpoint; the
            // controller anchors pin the hands at the authored rest positions.
            if (runtime.GetNodeOrNull<Camera3D>("MainCamera") is { } mainCamera)
            {
                mainCamera.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.0f, 1.62f, 0.0f));
            }

            runtime.RightHandController.HandPositionNode.GlobalTransform =
                new Transform3D(_rightWristRestBasis, _rightWristRest);
            runtime.LeftHandController.HandPositionNode.GlobalTransform =
                new Transform3D(Basis.Identity, _leftWristRest);

            AnimationTree animationTree = playerRoot.GetNode<AnimationTree>("AnimationTree");
            animationTree.Active = true;
            AnimationNodeStateMachinePlayback playback = animationTree
                .Get(HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter())
                .As<AnimationNodeStateMachinePlayback>()
                ?? throw new InvalidOperationException("Fixture AnimationTree lacks the upstream playback.");
            playback.Start(new StringName("StandingCrouching"), true);

            PlayerVRIK playerVrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
            Assert.True(playerVrik.BindToXRServices(), "Expected the fixture player VRIK to bind to the mock runtime.");

            // Move the frozen showcase items far away: this fixture authors its own free-body world.
            photobooth.GetNode<RigidBody3D>("Items/Ball").GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);
            photobooth.GetNode<RigidBody3D>("Items/Stick").GlobalPosition = new Vector3(10.0f, 10.0f, 10.0f);

            // Authored static table: an item-layer body (layer 2, mask 2) so the free ball's layer 2
            // physically rests on it, while the hand IK target body (mask 5) never sweeps against it — the
            // approach corridor from the authored wrist poses stays clear by the layer contract, not by
            // geometry luck.
            StaticBody3D table = new()
            {
                Name = "FreeBodyTable",
                CollisionLayer = 2,
                CollisionMask = 2,
                GlobalPosition = _tableCentre,
            };
            table.AddChild(new CollisionShape3D
            {
                Shape = new BoxShape3D { Size = new Vector3(0.4f, 0.06f, 0.4f) },
            });
            photobooth.GetNode("Items").AddChild(table);

            await WaitForFramesAsync(sceneTree, 40);

            RigidBody3D ball = AddFreeBall(photobooth, "FreeBall", _freeBallRest);
            await WaitForPhysicsFramesAsync(sceneTree, 45);

            return new FreeBodyFixture(
                root,
                xrManager,
                runtime,
                photobooth,
                playerRoot,
                ball,
                playerRoot.GetNode<AnimatableBody3D>("IKTargets/RightHand"),
                playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand"),
                playerRoot.GetNode<OpticalFingerTrackingModifier>("Female/GeneralSkeleton/OpticalFingerTrackingModifier"));
        }

        public RigidBody3D AddFreeBall(string name, Vector3 restPosition)
            => AddFreeBall(Photobooth, name, restPosition);

        private static RigidBody3D AddFreeBall(Node photobooth, string name, Vector3 restPosition)
        {
            RigidBody3D ball = LoadPackedScene(TestBallScenePath).Instantiate<RigidBody3D>();
            ball.Name = name;
            ball.Freeze = false;
            ball.GlobalPosition = restPosition;
            photobooth.GetNode("Items").AddChild(ball);
            ball.ForceUpdateTransform();
            return ball;
        }

        /// <summary>Enumerates the same-side hand, lower-arm, and finger proxy bodies the protection exempts.</summary>
        public IReadOnlyList<PhysicsBody3D> EnumerateSameSideHandProxyBodies()
        {
            DynamicPhysicalRig rig = PlayerRoot.GetNode<DynamicPhysicalRig>("Female/GeneralSkeleton/DynamicPhysicalRig");
            List<PhysicsBody3D> bodies = [];
            bodies.AddRange(rig.GetGeneratedProxyBodiesForBone("RightHand"));
            bodies.AddRange(rig.GetGeneratedProxyBodiesForBone("RightLowerArm"));
            bodies.AddRange(rig.GetGeneratedFingerProxyBodiesForHand("RightHand"));
            Assert.NotEmpty(bodies);
            return bodies;
        }

        public async Task StartOpticalAtRestAsync(SceneTree sceneTree)
        {
            _ = Runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
            OpticalGrabPhotoboothIntegrationTests.SetExactOpticalWristWorld(Runtime, LimbSide.Right, new Transform3D(_rightWristRestBasis, _rightWristRest));
            OpticalGrabPhotoboothIntegrationTests.SetExactOpticalWristWorld(Runtime, LimbSide.Left, new Transform3D(Basis.Identity, _leftWristRest));
            OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(Runtime, LimbSide.Right, AuthoredTrialOpenFlex);
            OpticalGrabPhotoboothIntegrationTests.InjectTrackedHandPose(Runtime, LimbSide.Left, AuthoredTrialOpenFlex);
            await WaitForPhysicsFramesAsync(sceneTree, 45);

            Assert.True(Modifier.IsOpticalSessionActive, "Expected the optical session to start for the fixture.");
            Assert.Equal(XRHandTrackingMode.Optical, Runtime.HandTrackingMode);
            Assert.True(
                Ball.LinearVelocity.Length() < 0.01f,
                $"The free ball must come to rest on the table before the trial; observed "
                + $"{Ball.LinearVelocity.Length():F4} m/s at {Ball.GlobalPosition}.");
            Assert.True(
                Ball.GlobalPosition.DistanceTo(_freeBallRest) <= 0.005f,
                $"The free ball must settle at its authored rest; observed {Ball.GlobalPosition}.");
            Assert.True(
                Ball.GlobalPosition.DistanceTo(_freeBallRest) <= 0.005f,
                $"The free ball must settle at its authored rest; observed {Ball.GlobalPosition}.");
        }

        public void Dispose()
        {
            if (GodotObject.IsInstanceValid(Root))
            {
                Root.Dispose();
            }
        }

        public async Task DisposeAsync(SceneTree sceneTree)
        {
            if (GodotObject.IsInstanceValid(Root) && Root.IsInsideTree())
            {
                Root.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

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
