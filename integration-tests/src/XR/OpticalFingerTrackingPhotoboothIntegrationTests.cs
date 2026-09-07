using AlleyCat.IK;
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
/// Non-visual integration coverage mirroring the optical finger tracking photobooth scenarios (XR-002
/// TR11-TR25), loading the same visual fixture scene and asserting the behaviour behind each screenshot
/// scenario through effective skeleton-pose observations.
/// </summary>
/// <remarks>
/// Effective finger output is observed through the <c>skeleton_updated</c> pose capture shared with the
/// modifier tests, because skeleton modifier writes are per-frame overlays restored after each pass. Arm and
/// hand observations cover the VRIK wrist path exercised by the <c>wrist_rotation_active</c> scenario.
/// Rotation expectations follow the constrained anatomical semantics (XR-002 TR20-TR22, TR26): every mock joint
/// is injected as <c>wristRotation * cumulativeLocal</c> — <c>cumulative = parentCumulative * local</c> — so the
/// parent-relative tracked relation supplies <c>S</c>. Thumbs map it through the test-owned
/// authored-animation oracle — the roll-free metacarpal swing through the Reset-local bend/splay frame and
/// independent signed proximal/distal hinge flexion about the authored axes — while non-thumbs use signed hinge flexion
/// at the intermediate/distal joints and a roll-free directional swing at the proximals, with <c>N</c>, the
/// desired globals, and the per-hand frame independently derived from observable skeleton rests and
/// independent of session entry, authored/live pose, wrist motion, and production mapping helpers.
/// </remarks>
public sealed class OpticalFingerTrackingPhotoboothIntegrationTests
{
    private const string PhotoboothScenePath = "res://tests/xr/optical_finger_tracking_photobooth.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";

    private const float RotationToleranceRadians = 1e-3f;

    private const float LeftWristYawRadians = 0.8f;

    private const float RightWristYawRadians = -1.1f;

    private const float FlexOpen = 0.15f;
    private const float FlexMid = 1.0f;
    private const float FlexCurl = 2.2f;
    private const float FlexFreezeUpdate = 2.4f;

    private const float LargeVisibleChangeRadians = 0.3f;

    private const float MaterialTrackedOutputChangeRadians = 0.05f;

    private const float IdleDriftToleranceRadians = 5e-3f;

    // VRIK legitimately re-settles arm and hand bones slightly (for example through the finger collision
    // mirrors); the guard only bounds finger-driven motion far below the visible finger change magnitude.
    private const float VrikSettleToleranceRadians = 0.2f;

    // VRIK and the skeleton modifier pipeline solve on physics ticks, while the project runs windowed sessions
    // without vsync: process-frame settle windows then contain an environment-dependent number of solver ticks.
    // Bounding the settle in physics frames keeps the re-settled comparison deterministic on every display.
    private const int VrikSettlePhysicsFrames = 40;

    private static readonly Vector3 _rightWristRest = new(0.26f, 1.02f, -0.28f);
    private static readonly Vector3 _leftWristRest = new(-0.26f, 1.02f, -0.28f);

    private const float WristRotationYaw = 1.3f;

    /// <summary>
    /// The photobooth fixture loads with both hand camera rigs and installs the production player rig whose
    /// finger modifier resolves the full canonical topology (XR-002 TR12, AC14).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PhotoboothFixture_LoadsHandCamerasAndValidFingerTopology()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            Assert.NotNull(fixture.Photobooth.Call("get_camera_rig", "LeftHandCamera").AsGodotObject());
            Assert.NotNull(fixture.Photobooth.Call("get_camera_rig", "RightHandCamera").AsGodotObject());

            Assert.True(fixture.Modifier.Active);
            Assert.True(fixture.Modifier.IsFingerTopologyValid);
            Assert.Empty(fixture.Modifier.MissingFingerBoneNames);

            foreach (string boneName in CanonicalFingerBoneNames())
            {
                Assert.True(fixture.Skeleton.FindBone(boneName) >= 0, $"Expected {boneName} on the fixture skeleton.");
            }

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                bool hasNonIdentityRestNeutral = OpticalFingerTrackingTestTopology.NonThumbDestinationJoints.Any(joint =>
                    Quaternion.Identity.AngleTo(
                        ExpectedDestinationRotation(fixture, side, joint, flexScale: 0.0f).Normalized())
                    > RotationToleranceRadians * 10.0f);
                Assert.True(
                    hasNonIdentityRestNeutral,
                    $"The {side} photobooth hand must exercise a material rest-derived non-thumb neutral.");

                // The thumb metacarpal's authored imported rest local rotation is non-identity (≈95.26° on the
                // reference rig), so the thumb neutral at identity Delta is materially non-identity too
                // (XR-002 TR27, A9).
                Assert.True(
                    Quaternion.Identity.AngleTo(
                        ExpectedDestinationRotation(fixture, side, XRHandJoint.ThumbMetacarpal, flexScale: 0.0f)
                            .Normalized())
                    > RotationToleranceRadians * 10.0f,
                    $"The {side} photobooth hand must exercise the authored non-identity thumb metacarpal rest.");
            }

            // The visual runner lives beside the scene per the photobooth convention.
            Assert.True(Godot.FileAccess.FileExists("res://tests/xr/optical_finger_tracking_photobooth.gd"));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 1: with the committed mode controller, available optical curl samples never displace the
    /// authored rest pose, and neither fingers nor the hand bone rotate (XR-002 TR17-TR18, AC6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerAuthoredRest_IgnoresAvailableOpticalCurlSamples()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            Dictionary<string, Quaternion> baseline = CaptureFingerRotations(fixture);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexCurl);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexCurl);

            await WaitForFramesAsync(sceneTree, 6);

            Assert.False(fixture.Modifier.IsOpticalSessionActive);

            // The authored rest pose stays authoritative; the idle animation's own micro-advance is bounded by
            // the comparison tolerance. VRIK-owned body bones keep settling and are out of this contract.
            foreach ((string boneName, Quaternion rotation) in baseline)
            {
                float drift = rotation.Normalized().AngleTo(
                    fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)].Normalized());
                Assert.True(
                    drift <= IdleDriftToleranceRadians,
                    $"Controller-mode drift beyond idle animation noise for {boneName}: {drift:F6}.");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 2: committing optical maps both an open and curled tracked sample to their independent expected
    /// outputs. Every destination whose expected tracked output materially changes must move between those two
    /// samples. Changing only the tracked curl never rotates hand or arm bones, which VRIK owns (XR-002 TR12-TR14,
    /// TR17, TR21, AC15-AC16).
    /// </summary>
    [Headless(false)]
    [Fact]
    public async Task OpticalCurlOverride_DisplacesAllFingersLargeMargin_WithoutTouchingHandBones()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            // Freeze the animation so the authored reference is stable across the settle frames; VRIK keeps
            // solving from the pinned wrist targets.
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            // Session entry at the open flex: the first valid sample writes the tracked open outputs
            // immediately (XR-002 TR21) — the authored entry pose never enters the mapping.
            CommitOpticalMode(fixture, FlexOpen);
            await WaitForPhysicsFramesAsync(sceneTree, VrikSettlePhysicsFrames);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);

            Dictionary<string, Quaternion> trackedOpen = [];
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                string boneName = FingerBoneName(side, joint);
                AssertRotationApproximately(
                    ExpectedDestinationRotation(fixture, side, joint, FlexOpen),
                    fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)],
                    $"{boneName} tracked open local at entry");
                trackedOpen[boneName] = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)];
            }

            // A strong curl writes the stronger tracked outputs for every finger.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexCurl);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexCurl);
            await WaitForPhysicsFramesAsync(sceneTree, VrikSettlePhysicsFrames);

            int materiallyChangingOutputs = 0;
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                string boneName = FingerBoneName(side, joint);
                Quaternion expectedOpen = ExpectedDestinationRotation(fixture, side, joint, FlexOpen);
                Quaternion expectedCurl = ExpectedDestinationRotation(fixture, side, joint, FlexCurl);
                Quaternion tracked = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)];
                AssertRotationApproximately(
                    expectedCurl,
                    tracked,
                    boneName);

                if (expectedOpen.Normalized().AngleTo(expectedCurl.Normalized()) >= MaterialTrackedOutputChangeRadians)
                {
                    materiallyChangingOutputs++;
                    Assert.True(
                        trackedOpen[boneName].Normalized().AngleTo(tracked.Normalized()) >= MaterialTrackedOutputChangeRadians,
                        $"Expected {boneName} to materially move between tracked open and curl samples.");
                }
            }
            Assert.True(materiallyChangingOutputs > 0, "Expected at least one tracked destination output to materially change.");

            // Anomaly guard: with the wrist targets held fixed, changing only the tracked finger data must
            // never rotate the hand or arm bones that VRIK owns (XR-002 TR13, AC15).
            Dictionary<string, Quaternion> curled = CaptureFingerRotations(fixture);
            Dictionary<string, Quaternion> curledVrikBones = CaptureVrikOwnedRotations(fixture);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexOpen);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexOpen);
            await WaitForPhysicsFramesAsync(sceneTree, VrikSettlePhysicsFrames);

            foreach ((string vrikBone, Quaternion rotation) in curledVrikBones)
            {
                float drift = rotation.Normalized().AngleTo(
                    fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(vrikBone)].Normalized());
                Assert.True(
                    drift <= VrikSettleToleranceRadians,
                    $"Finger-only data change moved the VRIK-owned {vrikBone} by {drift:F6}.");
            }

            Quaternion openRight = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightIndexProximal")];
            Assert.True(
                curled["RightIndexProximal"].Normalized().AngleTo(openRight.Normalized()) > LargeVisibleChangeRadians,
                "Expected the tracked shape to change between curl and open samples.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 3: the two hands retarget independently — a curled left hand and an open right hand produce
    /// clearly different tracked shapes in the same frame, each through its own rest-corrected tracked outputs
    /// (XR-002 TR22, AC3-AC4).
    /// </summary>
    [Headless]
    [Fact]
    public async Task IndependentHands_LeftCurlAndRightOpen_ProduceDifferentTrackedShapes()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            CommitOpticalMode(fixture, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexCurl);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexOpen);

            await WaitForFramesAsync(sceneTree, 4);

            Quaternion leftProximal = RotationOf(fixture, LimbSide.Left, XRHandJoint.IndexProximal);
            Quaternion rightProximal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexProximal);

            Assert.True(
                leftProximal.Normalized().AngleTo(rightProximal.Normalized()) > LargeVisibleChangeRadians,
                "Expected the curled left hand and open right hand to differ visibly.");

            foreach (XRHandJoint joint in FingerJoints().Select(entry => entry.Joint))
            {
                AssertRotationApproximately(
                    ExpectedDestinationRotation(fixture, LimbSide.Left, joint, FlexCurl),
                    RotationOf(fixture, LimbSide.Left, joint),
                    $"Left{joint}");
                AssertRotationApproximately(
                    ExpectedDestinationRotation(fixture, LimbSide.Right, joint, FlexOpen),
                    RotationOf(fixture, LimbSide.Right, joint),
                    $"Right{joint}");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 4: invalidating the right index intermediate joint freezes that segment at its cached
    /// rotation while its proximal and distal siblings and the other fingers continue to the stronger curl
    /// (XR-002 TR22, AC18).
    /// </summary>
    [Headless]
    [Fact]
    public async Task PerJointFreeze_FrozenSegmentHoldsWhileSiblingsUpdate()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            CommitOpticalMode(fixture, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);

            Quaternion frozenIntermediate = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate);
            Quaternion frozenDistal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);

            fixture.Runtime.ClearHandJointSample(LimbSide.Right, XRHandJoint.IndexIntermediate);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexFreezeUpdate, skipJoints: [XRHandJoint.IndexIntermediate]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexFreezeUpdate);

            await WaitForFramesAsync(sceneTree, 4);

            // The cleared joint and its child (whose required source parent is the cleared joint) freeze at
            // their cached rotations (XR-002 TR20, TR22).
            Assert.Equal(
                frozenIntermediate,
                RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate));
            Assert.Equal(
                frozenDistal,
                RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal));
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Right, XRHandJoint.IndexProximal, FlexFreezeUpdate),
                RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexProximal),
                "RightIndexProximal sibling");
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Right, XRHandJoint.MiddleProximal, FlexFreezeUpdate),
                RotationOf(fixture, LimbSide.Right, XRHandJoint.MiddleProximal),
                "RightMiddleProximal other finger");
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Left, XRHandJoint.IndexProximal, FlexFreezeUpdate),
                RotationOf(fixture, LimbSide.Left, XRHandJoint.IndexProximal),
                "LeftIndexProximal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Controlled visual-comparison contract: the control accepts a conspicuous right index-intermediate update,
    /// while the otherwise-identical freeze input retains the re-seeded cached intermediate and distal rotations.
    /// The left hand is re-injected at the same mid-flex pose in both captures as a static reference
    /// (XR-002 TR20, TR22, AC18).
    /// </summary>
    [Headless]
    [Fact]
    public async Task IndexIntermediateComparison_ControlUpdatesWhileFreezeRetainsCachedSegmentAndStaticLeftReference()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            CommitOpticalMode(fixture, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);

            Dictionary<string, Quaternion> entryPose = CaptureFingerRotations(fixture);

            Quaternion cachedIntermediate = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate);
            Quaternion cachedDistal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);

            // Control: IndexIntermediate alone receives the strong update, isolating that segment and its child
            // against the otherwise static right hand.
            var controlOverrides = new Dictionary<XRHandJoint, float>
            {
                [XRHandJoint.IndexIntermediate] = FlexFreezeUpdate,
            };
            InjectTrackedHandPose(
                fixture.Runtime,
                LimbSide.Right,
                FlexMid,
                jointFlexOverrides: controlOverrides);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);

            Quaternion controlIntermediate = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate);
            Quaternion controlDistal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);
            Transform3D controlDistalGlobal = GlobalPoseOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);
            Quaternion controlLeftIndexIntermediate = RotationOf(fixture, LimbSide.Left, XRHandJoint.IndexIntermediate);
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate, FlexFreezeUpdate),
                controlIntermediate,
                "control RightIndexIntermediate");
            // The distal's own source delta and rest-corrected destination output are unchanged — its
            // parent-relative source derivation excludes the intermediate update — but its global pose follows
            // the updated parent segment.
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Right, XRHandJoint.IndexDistal, FlexMid),
                controlDistal,
                "control RightIndexDistal");
            Assert.True(
                cachedIntermediate.Normalized().AngleTo(controlIntermediate.Normalized()) > LargeVisibleChangeRadians,
                "Expected the control right index intermediate rotation to differ conspicuously from the cached pose.");

            // Restore the cache that the freeze case must retain, then submit exactly the control input except
            // that IndexIntermediate is absent. IndexDistal remains sampled but must freeze because its required
            // source parent is invalid.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexMid);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);
            cachedIntermediate = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate);
            cachedDistal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);

            fixture.Runtime.ClearHandJointSample(LimbSide.Right, XRHandJoint.IndexIntermediate);
            InjectTrackedHandPose(
                fixture.Runtime,
                LimbSide.Right,
                FlexMid,
                skipJoints: [XRHandJoint.IndexIntermediate],
                jointFlexOverrides: controlOverrides);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);

            Quaternion frozenIntermediate = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexIntermediate);
            Quaternion frozenDistal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);
            Transform3D frozenDistalGlobal = GlobalPoseOf(fixture, LimbSide.Right, XRHandJoint.IndexDistal);
            Quaternion frozenLeftIndexIntermediate = RotationOf(fixture, LimbSide.Left, XRHandJoint.IndexIntermediate);
            Assert.Equal(cachedIntermediate, frozenIntermediate);
            Assert.Equal(cachedDistal, frozenDistal);
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Right, XRHandJoint.IndexProximal, FlexMid),
                RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexProximal),
                "freeze RightIndexProximal updated sibling");
            Assert.True(
                controlIntermediate.Normalized().AngleTo(frozenIntermediate.Normalized()) > LargeVisibleChangeRadians,
                "Expected control and freeze right index intermediate rotations to remain conspicuously different.");
            Assert.True(
                controlDistalGlobal.Origin.DistanceTo(frozenDistalGlobal.Origin) > 0.005f,
                "Expected the index-distal child to move visibly with the updated control intermediate segment.");
            AssertRotationApproximately(
                controlLeftIndexIntermediate,
                frozenLeftIndexIntermediate,
                "static left reference IndexIntermediate");
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Left, XRHandJoint.IndexIntermediate, FlexMid),
                frozenLeftIndexIntermediate,
                "static left reference expected IndexIntermediate");
            AssertRotationApproximately(
                entryPose[FingerBoneName(LimbSide.Left, XRHandJoint.IndexIntermediate)],
                frozenLeftIndexIntermediate,
                "static left reference held from entry");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 5: rotating the optical wrist target while tracking stays active rotates the solved hand
    /// chain by a large margin while the tracked finger locals — derived purely from the tracked pairs —
    /// persist unchanged (XR-002 TR7, TR13, TR14).
    /// </summary>
    [Headless]
    [Fact]
    public async Task WristRotationActive_HandChainRotatesWhileTrackedFingersPersist()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            // Session entry at mid flex; the curl sample then establishes the tracked shape that must
            // persist across the wrist rotation.
            CommitOpticalMode(fixture, FlexMid);
            fixture.Runtime.SetOpticalCalibrationAnchor(LimbSide.Right, Transform3D.Identity);
            fixture.Runtime.SetOpticalWristSample(LimbSide.Right, new Transform3D(Basis.Identity, _rightWristRest));
            fixture.Runtime.SetOpticalWristSample(LimbSide.Left, new Transform3D(Basis.Identity, _leftWristRest));

            await WaitForFramesAsync(sceneTree, 45);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexCurl);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexCurl);
            await WaitForFramesAsync(sceneTree, 4);

            Quaternion handBefore = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightHand")];
            Quaternion lowerArmBefore = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightLowerArm")];
            Quaternion upperArmBefore = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightUpperArm")];

            // Rotate the wrist target in world space while the finger samples stay untouched.
            Basis rotated = Basis.Identity.Rotated(Vector3.Up, WristRotationYaw);
            Transform3D originGlobal = fixture.Runtime.OriginNode.GlobalTransform;
            fixture.Runtime.SetOpticalWristSample(
                LimbSide.Right,
                originGlobal.AffineInverse() * new Transform3D(rotated, _rightWristRest));

            await WaitForFramesAsync(sceneTree, 45);

            Quaternion handAfter = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightHand")];
            Quaternion lowerArmAfter = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightLowerArm")];
            Quaternion upperArmAfter = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone("RightUpperArm")];

            float handChange = handBefore.Normalized().AngleTo(handAfter.Normalized());
            float lowerArmChange = lowerArmBefore.Normalized().AngleTo(lowerArmAfter.Normalized());
            float upperArmChange = upperArmBefore.Normalized().AngleTo(upperArmAfter.Normalized());

            Assert.True(
                Mathf.Max(Mathf.Max(handChange, lowerArmChange), upperArmChange) > 0.4f,
                $"Expected the wrist rotation to reach the solved arm chain (hand {handChange:F3}, " +
                $"lower arm {lowerArmChange:F3}, upper arm {upperArmChange:F3}).");

            // The tracked finger locals persist across the wrist rotation: the parent-relative quotients are
            // invariant to any common world rotation of the tracked pairs (XR-002 TR14).
            foreach (XRHandJoint joint in FingerJoints().Where(entry => entry.Side == LimbSide.Right).Select(e => e.Joint))
            {
                AssertRotationApproximately(
                    ExpectedDestinationRotation(fixture, LimbSide.Right, joint, FlexCurl),
                    RotationOf(fixture, LimbSide.Right, joint),
                    $"Right{joint} persisting");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 6: leaving optical mode restores the still-selected authored rest pose exactly, without any
    /// pose clearing, while the hand blend parameter stays untouched (XR-002 TR18, TR24, AC19).
    /// </summary>
    [Headless]
    [Fact]
    public async Task OpticalExit_RestoresAuthoredRestPoseExactly()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            // Freeze the animation so the authored rest pose is a byte-stable reference (Phase 4 precedent).
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            Dictionary<string, Quaternion> authored = CaptureFingerRotations(fixture);

            // Session entry at mid flex writes the tracked outputs; the curl sample then displaces them
            // before the mode exit (XR-002 TR21).
            CommitOpticalMode(fixture, FlexMid);
            await WaitForFramesAsync(sceneTree, 6);

            Quaternion midFlexProximal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexProximal);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexCurl);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, FlexCurl);
            await WaitForFramesAsync(sceneTree, 6);

            // The curl output is proven live through the independent expected output plus a visible
            // mid-to-curl movement, never through a displacement-from-authored-rest threshold: the production
            // profile's captured source neutral legitimately places some tracked outputs near the rest pose.
            Quaternion curlProximal = RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexProximal);
            Assert.True(
                midFlexProximal.Normalized().AngleTo(curlProximal.Normalized()) > MaterialTrackedOutputChangeRadians,
                "Expected the open-to-curl stimulus to visibly move the tracked output before the exit.");
            AssertRotationApproximately(
                ExpectedDestinationRotation(fixture, LimbSide.Right, XRHandJoint.IndexProximal, FlexCurl),
                curlProximal,
                "RightIndexProximal tracked curl output");

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);

            await WaitForFramesAsync(sceneTree, 6);

            Assert.False(fixture.Modifier.IsOpticalSessionActive);
            AssertRotationApproximately(
                authored["RightIndexProximal"],
                RotationOf(fixture, LimbSide.Right, XRHandJoint.IndexProximal),
                "RightIndexProximal immediately after exit");

            foreach ((string boneName, Quaternion rotation) in authored)
            {
                float drift = rotation.Normalized().AngleTo(
                    fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)].Normalized());
                Assert.True(
                    drift <= IdleDriftToleranceRadians,
                    $"Authored pose not restored within tolerance for {boneName}: {drift:F6}.");
            }

            Assert.Equal(
                0.0f,
                fixture.AnimationTree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle(),
                5);
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Scenario 7: tracked thumb opposition — the logged capture <c>c942e468…</c> THUMB_OPPOSITION
    /// window-mean wrist→metacarpal relation, whose measured decomposition is a direction change plus an axial
    /// roll about the delivered longitudinal — sweeps the thumb metacarpal towards the little-finger base
    /// through the authored-animation Stage 1 model while the axial roll is discarded and the thumb
    /// proximal/distal respond only through their own tracked hinge flexion about their independent authored
    /// axes (XR-002 TR24, TR26-TR29, H8, A10).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ThumbOpposition_SweepsPalmwardWithoutStrayTwist()
    {
        SceneTree sceneTree = GetSceneTree();
        using PhotoboothFixture fixture = await PhotoboothFixture.CreateAsync(sceneTree);

        try
        {
            fixture.AnimationTree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            CommitOpticalMode(fixture, FlexMid);
            await WaitForFramesAsync(sceneTree, 4);

            // The logged opposition relation (right hand, c942e468 THUMB_OPPOSITION window mean): physical
            // opposition decomposes into a direction swing and an axial roll about the delivered longitudinal
            // (≈20°/−19° right measured); Stage 1 transfers the swing and discards the roll (XR-002 TR24).
            Quaternion oppositionLocal = new(-0.04526078f, -0.7238631f, -0.15840442f, 0.6699864f);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, FlexMid);
            InjectTrackedHandPose(
                fixture.Runtime,
                LimbSide.Right,
                FlexMid,
                thumbMetacarpalLocal: oppositionLocal);
            await WaitForFramesAsync(sceneTree, 4);

            foreach (XRHandJoint joint in FingerJoints().Where(entry => entry.Side == LimbSide.Right).Select(e => e.Joint))
            {
                Quaternion expected = joint == XRHandJoint.ThumbMetacarpal
                    ? ExpectedDestinationRotation(
                        fixture,
                        LimbSide.Right,
                        joint,
                        FlexMid,
                        sourceRelationOverride: oppositionLocal)
                    : ExpectedDestinationRotation(fixture, LimbSide.Right, joint, FlexMid);
                AssertRotationApproximately(
                    expected,
                    RotationOf(fixture, LimbSide.Right, joint),
                    $"Right{joint} opposition");
            }

            // The relaxed left thumb column stays exactly at its authored-rest expectations.
            foreach (XRHandJoint joint in FingerJoints().Where(entry => entry.Side == LimbSide.Left).Select(e => e.Joint)
                         .Where(OpticalFingerTrackingTestTopology.IsThumbDestination))
            {
                AssertRotationApproximately(
                    ExpectedDestinationRotation(fixture, LimbSide.Left, joint, FlexMid),
                    RotationOf(fixture, LimbSide.Left, joint),
                    $"Left{joint} relaxed");
            }

            // Objective anatomical sanity (pinned A10 metric): the written metacarpal decomposes as
            // inverse(N) × written == shortest_arc(l, d) through the authored Reset-local frame, and the swung
            // longitudinal l increases its towards-little-finger-base component in the hand's own rest frame,
            // sweeping the thumbnail towards the little-finger base rather than abducting dorsally
            // (XR-002 TR28.7, H8, A10).
            Quaternion neutral = OpticalFingerTrackingTestTopology.ExpectedThumbRestLocal(
                fixture.Skeleton,
                LimbSide.Right,
                XRHandJoint.ThumbMetacarpal);
            Quaternion restGlobal = OpticalFingerTrackingTestTopology.GlobalRestRotation(
                fixture.Skeleton,
                LimbSide.Right,
                XRHandJoint.ThumbMetacarpal);
            (Vector3 frameL, Vector3 _, Vector3 _) = OpticalFingerTrackingTestTopology.ExpectedThumbMetacarpalFrame(
                fixture.Skeleton,
                LimbSide.Right);
            Vector3 metacarpalOrigin = OpticalFingerTrackingTestTopology
                .GlobalRest(fixture.Skeleton, LimbSide.Right, XRHandJoint.ThumbMetacarpal).Origin;
            Vector3 littleOrigin = OpticalFingerTrackingTestTopology
                .GlobalRest(fixture.Skeleton, LimbSide.Right, XRHandJoint.LittleProximal).Origin;
            Vector3 towardsLittleLocal = new Basis(restGlobal.Inverse()) * (littleOrigin - metacarpalOrigin).Normalized();
            Quaternion written = RotationOf(fixture, LimbSide.Right, XRHandJoint.ThumbMetacarpal);
            Quaternion swing = neutral.Inverse().Normalized() * written;
            Vector3 neutralDirection = frameL;
            Vector3 sweptDirection = new Basis(swing) * frameL;
            Assert.True(
                sweptDirection.Dot(towardsLittleLocal) > neutralDirection.Dot(towardsLittleLocal) + 0.05f,
                $"Expected opposition to sweep the right thumb metacarpal towards the little-finger base in " +
                $"its own rest frame; neutral dot {neutralDirection.Dot(towardsLittleLocal):F3} -> swept dot " +
                $"{sweptDirection.Dot(towardsLittleLocal):F3}.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static Quaternion RotationOf(PhotoboothFixture fixture, LimbSide side, XRHandJoint joint)
        => fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(FingerBoneName(side, joint))];

    private static Transform3D GlobalPoseOf(PhotoboothFixture fixture, LimbSide side, XRHandJoint joint)
        => fixture.PoseCapture.GlobalPoses[fixture.Skeleton.FindBone(FingerBoneName(side, joint))];

    private static void CommitOpticalMode(PhotoboothFixture fixture, float flexScale)
    {
        _ = fixture.Runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
        fixture.Runtime.SetOpticalCalibrationAnchor(LimbSide.Right, Transform3D.Identity);
        fixture.Runtime.SetOpticalCalibrationAnchor(LimbSide.Left, Transform3D.Identity);
        fixture.Runtime.SetOpticalWristSample(LimbSide.Right, new Transform3D(Basis.Identity, _rightWristRest));
        fixture.Runtime.SetOpticalWristSample(LimbSide.Left, new Transform3D(Basis.Identity, _leftWristRest));
        InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, flexScale);
        InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, flexScale);
    }

    private static Dictionary<string, Quaternion> CaptureFingerRotations(PhotoboothFixture fixture)
    {
        Dictionary<string, Quaternion> rotations = [];
        foreach (string boneName in CanonicalFingerBoneNames())
        {
            rotations[boneName] = fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)];
        }

        return rotations;
    }

    private static Dictionary<string, Quaternion> CaptureVrikOwnedRotations(PhotoboothFixture fixture)
        => new[]
            {
                "RightHand",
                "LeftHand",
                "RightLowerArm",
                "LeftLowerArm",
            }
            .ToDictionary(boneName => boneName, boneName => fixture.PoseCapture.Rotations[fixture.Skeleton.FindBone(boneName)]);

    private static IEnumerable<(LimbSide Side, XRHandJoint Joint)> FingerJoints()
    {
        foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
        {
            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.DestinationJoints)
            {
                yield return (side, joint);
            }
        }
    }

    private static IEnumerable<string> CanonicalFingerBoneNames()
        => FingerJoints().Select(entry => FingerBoneName(entry.Side, entry.Joint));

    private static string FingerBoneName(LimbSide side, XRHandJoint joint)
        => (side == LimbSide.Left ? "Left" : "Right") + joint;

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
            XRHandJoint.Wrist => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
        };

    /// <summary>
    /// Independently derives the destination output from observable global rest geometry and test-owned profile
    /// constants. The injected parent-relative flexion supplies S. Every destination maps S through the
    /// test-owned anatomical oracle — the authored-animation Stage 1 thumb model (roll-free metacarpal swing
    /// through the Reset-local bend/splay frame, independent signed proximal/distal hinge flexion about the
    /// authored axes, authored-rest thumb neutrals) for thumbs and the constrained non-thumb model for the
    /// rest (XR-002 TR20-TR29) — using the
    /// production-profile S0 and rest-derived N from independently swung chains. No production mapping helper
    /// is reused in the expected values.
    /// </summary>
    private static Quaternion ExpectedDestinationRotation(
        PhotoboothFixture fixture,
        LimbSide side,
        XRHandJoint joint,
        float flexScale,
        IReadOnlyDictionary<XRHandJoint, float>? jointFlexOverrides = null,
        Quaternion? sourceRelationOverride = null)
    {
        float jointScale = jointFlexOverrides is not null && jointFlexOverrides.TryGetValue(joint, out float overridden)
            ? overridden
            : flexScale;

        Quaternion sourceRelation = sourceRelationOverride ?? Basis.Identity
            .Rotated(Vector3.Right, JointLocalFlexRadians(joint) * jointScale)
            .GetRotationQuaternion();

        Quaternion expectedSourceNeutral = OpticalFingerTrackingTestTopology.GetExpectedSourceNeutral(side, joint);
        Assert.Equal(Quaternion.Identity, OpticalFingerTrackingTestTopology.ExpectedBasisCorrespondence);

        return OpticalFingerTrackingTestTopology.ExpectedAnatomicalRotation(
            fixture.Skeleton,
            side,
            joint,
            sourceRelation,
            expectedSourceNeutral);
    }

    private static void InjectTrackedHandPose(
        MockXRRuntimeNode runtime,
        LimbSide side,
        float flexScale,
        HashSet<XRHandJoint>? skipJoints = null,
        IReadOnlyDictionary<XRHandJoint, float>? jointFlexOverrides = null,
        Quaternion? thumbMetacarpalLocal = null)
    {
        float wristYaw = side == LimbSide.Left ? LeftWristYawRadians : RightWristYawRadians;
        Basis wristRotation = Basis.Identity.Rotated(Vector3.Up, wristYaw);
        Vector3 wristOrigin = new(side == LimbSide.Left ? -0.25f : 0.25f, 1.0f, -0.35f);

        runtime.SetHandJointSample(side, XRHandJoint.Wrist, new Transform3D(wristRotation, wristOrigin));

        // The test-owned stimulus order is anatomically ordered, so every explicit parent cumulative exists first.
        Dictionary<XRHandJoint, Basis> cumulative = [];
        foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
        {
            if (joint == XRHandJoint.Wrist)
            {
                cumulative[joint] = Basis.Identity;
                continue;
            }

            float jointFlexScale = jointFlexOverrides is not null
                && jointFlexOverrides.TryGetValue(joint, out float overriddenJointFlexScale)
                    ? overriddenJointFlexScale
                    : flexScale;
            Basis local = joint == XRHandJoint.ThumbMetacarpal && thumbMetacarpalLocal is { } opposition
                ? new Basis(opposition)
                : Basis.Identity.Rotated(Vector3.Right, JointLocalFlexRadians(joint) * jointFlexScale);
            XRHandJoint parent = OpticalFingerTrackingTestTopology.GetTrackedParent(joint)
                ?? throw new InvalidOperationException($"Joint {joint} unexpectedly lacks a source parent.");
            cumulative[joint] = cumulative[parent] * local;

            if (skipJoints is not null && skipJoints.Contains(joint))
            {
                continue;
            }

            Vector3 translation = wristOrigin + new Vector3(0.0f, -0.03f * (int)joint, 0.02f);
            runtime.SetHandJointSample(side, joint, new Transform3D(wristRotation * cumulative[joint], translation));
        }
    }

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual, string context)
    {
        float angle = expected.Normalized().AngleTo(actual.Normalized());
        Assert.True(
            angle <= RotationToleranceRadians,
            $"Rotation mismatch for {context}: expected {expected} ({expected.GetEuler()}), " +
            $"actual {actual} ({actual.GetEuler()}), angle {angle}.");
    }

    /// <summary>
    /// Production-shaped fixture: the shared photobooth scene with the installed player rig, the deterministic
    /// mock XR runtime exposed through a test game service provider, and the effective-pose capture probe.
    /// </summary>
    private sealed class PhotoboothFixture : IDisposable
    {
        private PhotoboothFixture(
            TestGame root,
            MockXRRuntimeNode runtime,
            Node photobooth,
            Node playerRoot,
            AnimationTree animationTree,
            Skeleton3D skeleton,
            OpticalFingerTrackingModifier modifier,
            OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture poseCapture)
        {
            Root = root;
            Runtime = runtime;
            Photobooth = photobooth;
            PlayerRoot = playerRoot;
            AnimationTree = animationTree;
            Skeleton = skeleton;
            Modifier = modifier;
            PoseCapture = poseCapture;
        }

        public TestGame Root
        {
            get;
        }

        public MockXRRuntimeNode Runtime
        {
            get;
        }

        public Node Photobooth
        {
            get;
        }

        public Node PlayerRoot
        {
            get;
        }

        public AnimationTree AnimationTree
        {
            get;
        }

        public Skeleton3D Skeleton
        {
            get;
        }

        public OpticalFingerTrackingModifier Modifier
        {
            get;
        }

        public OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture PoseCapture
        {
            get;
        }

        public static async Task<PhotoboothFixture> CreateAsync(SceneTree sceneTree)
        {
            // The integration runner starts test bodies inside TestRuntimeRunner._Ready, where scene-tree
            // attachment does not propagate until the first process frame; wait one frame before attaching.
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalFingerPhotoboothFixture",
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

            // The mock head camera drives the VRIK head target at a natural standing viewpoint; the controller
            // anchors pin the hands at the positions the visual fixture frames. Both require the runtime's
            // _Ready, so they are set after the fixture entered the tree.
            if (runtime.GetNodeOrNull<Camera3D>("MainCamera") is { } mainCamera)
            {
                mainCamera.GlobalTransform = new Transform3D(Basis.Identity, new Vector3(0.0f, 1.62f, 0.0f));
            }

            runtime.RightHandController.HandPositionNode.GlobalTransform =
                new Transform3D(Basis.Identity, _rightWristRest);
            runtime.LeftHandController.HandPositionNode.GlobalTransform =
                new Transform3D(Basis.Identity, _leftWristRest);

            AnimationTree animationTree = playerRoot.GetNode<AnimationTree>("AnimationTree");
            animationTree.Active = true;
            AnimationNodeStateMachinePlayback playback = animationTree
                .Get(HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter())
                .As<AnimationNodeStateMachinePlayback>()
                ?? throw new InvalidOperationException("Fixture AnimationTree lacks the upstream playback.");
            playback.Start(new StringName("StandingCrouching"), true);

            // Bind after installation and animation activation: the installer defers a pose state machine
            // restart that invalidates earlier bindings.
            PlayerVRIK playerVrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
            Assert.True(playerVrik.BindToXRServices(), "Expected the fixture player VRIK to bind to the mock runtime.");

            Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            OpticalFingerTrackingModifier modifier = Assert.IsType<OpticalFingerTrackingModifier>(
                skeleton.GetNode("OpticalFingerTrackingModifier"),
                exactMatch: false);
            AssertProductionProfileMatchesTestOwnedSourceNeutrals(modifier);
            var poseCapture =
                OpticalFingerTrackingModifierIntegrationTests.SkeletonPoseCapture.Attach(skeleton);

            await WaitForPhysicsFramesAsync(sceneTree, VrikSettlePhysicsFrames);

            Assert.True(poseCapture.CaptureCount > 0, "Expected the fixture pose capture to run at least once.");

            return new PhotoboothFixture(
                root,
                runtime,
                photobooth,
                playerRoot,
                animationTree,
                skeleton,
                modifier,
                poseCapture);
        }

        private static void AssertProductionProfileMatchesTestOwnedSourceNeutrals(
            OpticalFingerTrackingModifier modifier)
        {
            OpticalFingerTrackingTestTopology.ValidateExpectedSourceNeutralMappings();
            OpticalFingerTrackingCalibrationProfile profile = Assert.IsType<OpticalFingerTrackingCalibrationProfile>(
                modifier.CalibrationProfile,
                exactMatch: false);
            Assert.Equal(OpticalFingerTrackingTestTopology.ExpectedSourceNeutrals.Length, profile.Entries.Length);

            for (int index = 0; index < OpticalFingerTrackingTestTopology.ExpectedSourceNeutrals.Length; index++)
            {
                ExpectedSourceNeutral expected = OpticalFingerTrackingTestTopology.ExpectedSourceNeutrals[index];
                OpticalFingerTrackingCalibrationEntry actual = Assert.IsType<OpticalFingerTrackingCalibrationEntry>(
                    profile.Entries[index],
                    exactMatch: false);
                Assert.Equal(expected.Side, actual.Side);
                Assert.Equal(expected.Joint, actual.Joint);
                Assert.Equal(expected.SourceNeutral, actual.SourceNeutral);
                Assert.Equal(
                    OpticalFingerTrackingTestTopology.ExpectedBasisCorrespondence,
                    actual.BasisCorrespondence);
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

        public void Dispose()
        {
            if (GodotObject.IsInstanceValid(Root))
            {
                Root.Dispose();
            }
        }
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime)
            => Runtime = runtime;
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
