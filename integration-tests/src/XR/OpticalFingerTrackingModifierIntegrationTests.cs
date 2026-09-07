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
/// Integration coverage for the player-only optical finger retargeting modifier using the deterministic mock
/// runtime joint provider (XR-002 TR11-TR32).
/// </summary>
/// <remarks>
/// Skeleton modifiers apply as per-frame overlays over the authored pose: the engine restores the authored
/// local pose after each modification pass. Effective finger output is therefore observed through a probe
/// modifier ordered after the modifier under test, which captures the live pipeline state during its own
/// pass.
/// Source-delta expectations follow the constrained anatomical semantics (XR-002 TR20-TR22, TR26): every mock
/// joint is injected as <c>wristRotation * cumulativeLocal</c>, so the wrist-rotation, metacarpal, and chain terms
/// cancel in the parent-relative quotient that supplies <c>S</c>. Thumbs map <c>S</c> through the test-owned
/// authored-animation oracle — the roll-free metacarpal swing through the Reset-local bend/splay frame and
/// independent signed hinge flexion about the authored axes — while non-thumbs use signed hinge flexion at the intermediate and distal joints and a
/// roll-free directional swing at the proximals, with <c>N</c>, the desired globals, and the per-hand frame
/// independently derived from observable skeleton rests.
/// </remarks>
public sealed class OpticalFingerTrackingModifierIntegrationTests
{
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/ally_player.tscn";
    private const string ReferenceFemaleBaseScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string ReferenceFemalePlayerTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_player.tscn";
    private const string CalibrationProfilePath =
        "res://assets/xr/calibration/fingers_calibration_quest3.tres";
    private const string GrabBallAnimationPath = "res://assets/characters/reference/female/animations/Grab-ball-40.tres";

    private const float RotationToleranceRadians = 1e-3f;

    private const float DirectionTolerance = 1e-4f;

    private const float LeftWristYawRadians = 0.8f;

    private const float RightWristYawRadians = -1.1f;

    /// <summary>
    /// Authored finger rotations, including an applied grab pose, remain byte-identical while the committed
    /// mode is controller even when valid optical samples are available (XR-002 TR17-TR18, AC6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ControllerMode_LeavesAuthoredFingerRotationsByteIdenticalIncludingGrabPose()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            ApplyAuthoredGrabRotations(fixture);

            // Valid optical samples exist but the committed mode stays controller, so the modifier must
            // never write.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.False(fixture.Modifier.IsOpticalSessionActive);

            foreach (string boneName in CanonicalFingerBoneNames())
            {
                Assert.Equal(
                    fixture.AuthoredRotations[boneName],
                    fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices));
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Valid optical samples write all 30 finger bones' tracked outputs — direct source deltas for thumbs and
    /// rest-corrected destination rotations for non-thumbs — while hand, arm, positions, and scales remain
    /// untouched (XR-002 TR12-TR14, TR21, AC14-AC16).
    /// </summary>
    [Headless(false)]
    [Fact]
    public async Task OpticalSamples_RotateAllThirtyFingerBones_WithoutTouchingHandsArmsPositionsOrScales()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            Dictionary<string, Vector3> positionsBefore = CapturePositions(fixture);
            Dictionary<string, Vector3> scalesBefore = CaptureScales(fixture);
            Dictionary<string, Quaternion> nonFingerRotationsBefore = CaptureNonFingerRotations(fixture);

            // Session entry at flex 1.0: every valid bone writes its tracked destination output immediately
            // (XR-002 TR14, TR21).
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 1.0f),
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    $"{FingerBoneName(side, joint)} at entry flex");
            }

            // A later sample writes the stronger tracked destination output.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 4);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 1.9f),
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    FingerBoneName(side, joint));
            }

            foreach ((string boneName, Vector3 position) in positionsBefore)
            {
                Assert.Equal(position, fixture.PoseCapture.Positions[fixture.BoneIndices[boneName]]);
            }

            foreach ((string boneName, Vector3 scale) in scalesBefore)
            {
                Assert.Equal(scale, fixture.PoseCapture.Scales[fixture.BoneIndices[boneName]]);
            }

            foreach ((string boneName, Quaternion rotation) in nonFingerRotationsBefore)
            {
                Assert.Equal(rotation, fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices));
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Source transforms with conspicuous translations never change destination bone positions while their
    /// parent-relative rotations still apply (XR-002 TR13, TR15).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ConspicuousJointTranslations_NeverChangeDestinationBonePositions()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            Dictionary<string, Vector3> positionsBefore = CapturePositions(fixture);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f, conspicuousTranslations: true);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f, conspicuousTranslations: true);

            await WaitForFramesAsync(sceneTree, 4);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, conspicuousTranslations: true);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f, conspicuousTranslations: true);

            await WaitForFramesAsync(sceneTree, 4);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                string boneName = FingerBoneName(side, joint);
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 1.9f),
                    fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices),
                    boneName);
                Assert.Equal(
                    positionsBefore[boneName],
                    fixture.PoseCapture.Positions[fixture.BoneIndices[boneName]]);
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Position-invalid but orientation-valid samples remain live through the unchanged rotation-only provider gate
    /// and therefore update every destination instead of selecting the freeze cache.
    /// </summary>
    [Headless]
    [Fact]
    public async Task OrientationValidWithoutPositionFlags_UpdatesDestinationsWithoutFreezing()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);
            SetOrientationOnlyFlags(fixture.Runtime, LimbSide.Left);
            SetOrientationOnlyFlags(fixture.Runtime, LimbSide.Right);

            Assert.True(fixture.Runtime.TryGetJoint(
                LimbSide.Left,
                XRHandJoint.IndexProximal,
                out XRHandJointSourceSample sample));
            Assert.True(sample.OrientationValid);
            Assert.False(sample.PositionValid);
            Assert.False(sample.PositionTracked);
            Assert.Equal((long)XRHandTracker.HandJointFlags.OrientationValid, sample.RawFlags);
            Assert.True(sample.ProductionAccepted);

            await WaitForFramesAsync(sceneTree, 4);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);
            SetOrientationOnlyFlags(fixture.Runtime, LimbSide.Left);
            SetOrientationOnlyFlags(fixture.Runtime, LimbSide.Right);
            await WaitForFramesAsync(sceneTree, 4);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 1.9f),
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    $"{FingerBoneName(side, joint)} with position flags absent");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static void SetOrientationOnlyFlags(MockXRRuntimeNode runtime, LimbSide side)
    {
        foreach (XRHandJoint joint in XRHandJoints.TrackedJoints)
        {
            runtime.SetHandJointFlags(side, joint, XRHandTracker.HandJointFlags.OrientationValid);
        }
    }

    /// <summary>
    /// With a real grab pose applied through the player AnimationTree, tracked optical rotations win over the
    /// authored pose — writing the tracked destination outputs while blend parameters and the selected
    /// authored pose stay intact (XR-002 TR17, TR21, AC6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task AuthoredGrabPose_TrackedRotationsWinAndAuthoredSelectionRemainsIntact()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);
        using MockRuntimeFixture runtimeFixture = await MockRuntimeFixture.CreateAsync(sceneTree);

        Node playerRoot = LoadPackedScene(PlayerScenePath).Instantiate();

        try
        {
            // The real player lives under the test runtime root so its finger modifier and VRIK resolve the
            // fixture's XR manager, mirroring the passing photobooth fixture's wiring.
            runtimeFixture.Root.AddChild(playerRoot);
            EnsureCharacterRuntimeInstalled(playerRoot);

            AnimationTree tree = playerRoot.GetNode<AnimationTree>("AnimationTree");
            Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            OpticalFingerTrackingModifier modifier =
                skeleton.GetNode<OpticalFingerTrackingModifier>("OpticalFingerTrackingModifier");
            SkeletonPoseCapture poseCapture = AttachPoseCapture(skeleton);
            Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);

            await WaitForFramesAsync(sceneTree, 2);
            tree.Active = true;
            ResolvePlayback(tree).Start(new StringName("StandingCrouching"), true);
            HandPoseBehaviour rightHand = playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand");
            HandPoseBehaviour leftHand = playerRoot.GetNode<HandPoseBehaviour>("Hands/LeftHand");

            rightHand.SetPose(grabBall, weight: 1f, immediate: true);
            leftHand.SetPose(grabBall, weight: 1f, immediate: true);
            await WaitForFramesAsync(sceneTree, 3);

            // Bind VRIK to the fixture's XR services after installation and animation activation: the
            // installer defers a pose state machine restart that invalidates earlier bindings.
            PlayerVRIK playerVrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
            Assert.True(playerVrik.BindToXRServices(), "Expected the fixture player VRIK to bind to the mock runtime.");

            // Freeze the animation so the grab pose is a byte-stable reference for the visible-change guard.
            tree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);

            int rightIndexProximal = RequireBone(skeleton, "RightIndexProximal");
            int leftIndexProximal = RequireBone(skeleton, "LeftIndexProximal");
            Quaternion authoredGrabRight = poseCapture.Rotations[rightIndexProximal];
            Quaternion authoredGrabLeft = poseCapture.Rotations[leftIndexProximal];

            // Session entry at flex 1.0: the first valid sample writes the tracked output immediately — the
            // authored entry pose never enters the mapping (XR-002 TR21). The identity profile is assigned only
            // now: the deferred template installation re-applies exported properties across frames and would
            // clobber an earlier assignment with the authored production profile.
            OpticalFingerTrackingCalibrationProfile identityProfile = CreateIdentityCalibrationProfile();
            modifier.CalibrationProfile = identityProfile;
            Assert.Same(identityProfile, modifier.CalibrationProfile);
            _ = runtimeFixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(
                modifier.IsOpticalSessionActive,
                "Expected the optical session to activate before player mapping assertions.");

            AssertRotationApproximately(
                ExpectedRestDerivedRotation(skeleton, LimbSide.Right, XRHandJoint.IndexProximal, 1.0f),
                poseCapture.Rotations[rightIndexProximal],
                "RightIndexProximal first valid sample");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(skeleton, LimbSide.Left, XRHandJoint.IndexProximal, 1.0f),
                poseCapture.Rotations[leftIndexProximal],
                "LeftIndexProximal first valid sample");

            // A later sample writes the stronger tracked output while optical tracking owns the value.
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Left, 2.2f);
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Right, 2.2f);

            await WaitForFramesAsync(sceneTree, 4);

            AssertRotationApproximately(
                ExpectedRestDerivedRotation(skeleton, LimbSide.Right, XRHandJoint.IndexProximal, 2.2f),
                poseCapture.Rotations[rightIndexProximal],
                "RightIndexProximal");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(skeleton, LimbSide.Left, XRHandJoint.IndexProximal, 2.2f),
                poseCapture.Rotations[leftIndexProximal],
                "LeftIndexProximal");
            Assert.True(
                authoredGrabRight.AngleTo(poseCapture.Rotations[rightIndexProximal]) > 0.05f,
                "Expected tracked rotation to differ visibly from the authored grab pose.");
            Assert.True(
                authoredGrabLeft.AngleTo(poseCapture.Rotations[leftIndexProximal]) > 0.05f,
                "Expected tracked rotation to differ visibly from the authored grab pose.");

            // The modifier never mutates authored AnimationTree state: the grab pose stays selected at full
            // blend, routed to the exact pose instance under its instance-exact key so the authored library
            // entry is not substituted by name (XR-002 TR19, OG17).
            AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(tree.TreeRoot, exactMatch: false);
            AnimationNodeAnimation rightPoseNode = Assert.IsType<AnimationNodeAnimation>(
                rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(LimbSide.Right)),
                exactMatch: false);
            AnimationPlayer animationPlayer = tree.GetNode<AnimationPlayer>(tree.AnimPlayer);

            Assert.Equal(1.0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle(), 5);
            StringName poseKey = rightPoseNode.Animation;
            Assert.StartsWith("__alleycat_hand_pose_", poseKey.ToString(), StringComparison.Ordinal);
            Assert.Same(grabBall, animationPlayer.GetAnimation(poseKey));
            StringName authoredKey = new("Grab-ball-40");
            Assert.True(animationPlayer.HasAnimation(authoredKey), "Expected the authored grab-ball key to remain registered.");
            Assert.NotSame(grabBall, animationPlayer.GetAnimation(authoredKey));
        }
        finally
        {
            playerRoot.QueueFree();
            await runtimeFixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Leaving optical mode performs no bone writes and clears the session cache, so the still-selected
    /// authored grab pose immediately regains authority (XR-002 TR18, TR24, AC19).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ModeExit_RestoresAuthoredAuthorityWithoutClearingThePose()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);
        using MockRuntimeFixture runtimeFixture = await MockRuntimeFixture.CreateAsync(sceneTree);

        Node playerRoot = LoadPackedScene(PlayerScenePath).Instantiate();

        try
        {
            // The real player lives under the test runtime root so its finger modifier and VRIK resolve the
            // fixture's XR manager, mirroring the passing photobooth fixture's wiring.
            runtimeFixture.Root.AddChild(playerRoot);
            EnsureCharacterRuntimeInstalled(playerRoot);

            AnimationTree tree = playerRoot.GetNode<AnimationTree>("AnimationTree");
            Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            OpticalFingerTrackingModifier modifier =
                skeleton.GetNode<OpticalFingerTrackingModifier>("OpticalFingerTrackingModifier");
            SkeletonPoseCapture poseCapture = AttachPoseCapture(skeleton);
            Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);

            await WaitForFramesAsync(sceneTree, 2);
            tree.Active = true;
            ResolvePlayback(tree).Start(new StringName("StandingCrouching"), true);
            HandPoseBehaviour rightHand = playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand");

            rightHand.SetPose(grabBall, weight: 1f, immediate: true);
            await WaitForFramesAsync(sceneTree, 3);

            // Bind VRIK to the fixture's XR services after installation and animation activation: the
            // installer defers a pose state machine restart that invalidates earlier bindings.
            PlayerVRIK playerVrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
            Assert.True(playerVrik.BindToXRServices(), "Expected the fixture player VRIK to bind to the mock runtime.");

            int rightIndexProximal = RequireBone(skeleton, "RightIndexProximal");

            // Freeze the animation so the authored grab pose is a stable reference: the grab animation
            // advances over time, and the exit assertion must compare against this exact pose.
            tree.Active = false;
            await WaitForFramesAsync(sceneTree, 2);
            Quaternion authoredGrabRight = poseCapture.Rotations[rightIndexProximal];

            // The identity profile is assigned only now: the deferred template installation re-applies
            // exported properties across frames and would clobber an earlier assignment with the authored
            // production profile.
            OpticalFingerTrackingCalibrationProfile identityProfile = CreateIdentityCalibrationProfile();
            modifier.CalibrationProfile = identityProfile;
            Assert.Same(identityProfile, modifier.CalibrationProfile);
            _ = runtimeFixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(
                modifier.IsOpticalSessionActive,
                "Expected the optical session to activate before player mapping assertions.");

            // A later sample at a stronger flex writes the stronger tracked output (XR-002 TR21).
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Left, 2.2f);
            InjectTrackedHandPose(runtimeFixture.Runtime, LimbSide.Right, 2.2f);

            await WaitForFramesAsync(sceneTree, 4);

            AssertRotationApproximately(
                ExpectedRestDerivedRotation(skeleton, LimbSide.Right, XRHandJoint.IndexProximal, 2.2f),
                poseCapture.Rotations[rightIndexProximal],
                "RightIndexProximal displaced from the authored pose");
            Assert.True(
                authoredGrabRight.AngleTo(poseCapture.Rotations[rightIndexProximal]) > 0.05f,
                "Expected optical tracking to displace the authored grab pose first.");

            // Just leaving optical mode must restore authored authority; the modifier performs no exit
            // writes, so the animation tree's grab pose becomes effective again on its own.
            _ = runtimeFixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);

            await WaitForFramesAsync(sceneTree, 4);

            AssertRotationApproximately(
                authoredGrabRight,
                poseCapture.Rotations[rightIndexProximal],
                "RightIndexProximal after leaving optical");
            Assert.Equal(1.0f, tree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle(), 5);
        }
        finally
        {
            playerRoot.QueueFree();
            await runtimeFixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An invalid joint freezes at its cached rotation while a valid sibling continues updating in the same
    /// frame (XR-002 TR22, AC18).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidJoint_FreezesIndependentlyWhileSiblingUpdates()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenRotation = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.LittleDistal),
                fixture.BoneIndices);

            // Set B: one joint invalid while an unrelated sibling receives a clearly different update.
            fixture.Runtime.ClearHandJointSample(LimbSide.Left, XRHandJoint.LittleDistal);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.75f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.75f, skipJoints: [XRHandJoint.LittleDistal]);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenRotation,
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.LittleDistal), fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexProximal, 1.75f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal), fixture.BoneIndices),
                "LeftIndexProximal updated sibling");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.IndexProximal, 1.75f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.IndexProximal), fixture.BoneIndices),
                "RightIndexProximal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// A joint whose required source parent is invalid retains its cached rotation even though the child
    /// sample itself is present and tracked (XR-002 TR20, TR22).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidSourceParent_FreezesChildJointDespiteValidChildSample()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenRotation = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal),
                fixture.BoneIndices);

            // The parent (thumb proximal) goes untracked; the child sample itself stays valid.
            fixture.Runtime.SetHandJointTracked(LimbSide.Left, XRHandJoint.ThumbProximal, false);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, skipJoints: [XRHandJoint.ThumbProximal]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenRotation,
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal), fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.ThumbDistal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.ThumbDistal), fixture.BoneIndices),
                "RightThumbDistal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// A non-thumb proximal derives from its metacarpal source parent, never from the wrist: the injected
    /// metacarpal local flexion is excluded from the proximal source delta before its rest neutral is applied
    /// (XR-002 TR14, AC16).
    /// </summary>
    /// <remarks>
    /// The injected index proximal local is <c>Rot(X, 0.30 · flex)</c> while the collapsed wrist-relative
    /// orientation would carry the metacarpal's flexion too (<c>Rot(X, (0.12 + 0.30) · flex)</c>); the two
    /// differ materially, so this scenario discriminates the metacarpal-parent rule from any wrist-derived
    /// fallback.
    /// </remarks>
    [Headless]
    [Fact]
    public async Task NonThumbProximal_FollowsMetacarpalParentNotWrist()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.8f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.8f);

            await WaitForFramesAsync(sceneTree, 4);

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                Quaternion written = fixture.PoseCapture.FingerRotation(
                    FingerBoneName(side, XRHandJoint.IndexProximal),
                    fixture.BoneIndices);

                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, XRHandJoint.IndexProximal, 1.8f),
                    written,
                    $"{FingerBoneName(side, XRHandJoint.IndexProximal)} metacarpal-parented");

                // The collapsed wrist-relative alternative would carry the metacarpal's flexion into the source
                // relation; mapping that delta through the same anatomical transfer produces a materially
                // different proximal write, so the discriminator excludes any wrist-derived fallback.
                Quaternion collapsedSourceDelta = Basis.Identity
                    .Rotated(Vector3.Right, (JointLocalFlexRadians(XRHandJoint.IndexMetacarpal)
                        + JointLocalFlexRadians(XRHandJoint.IndexProximal)) * 1.8f)
                    .GetRotationQuaternion();
                Quaternion collapsedWristRelative = OpticalFingerTrackingTestTopology.ExpectedAnatomicalRotation(
                    fixture.Skeleton,
                    side,
                    XRHandJoint.IndexProximal,
                    collapsedSourceDelta,
                    Quaternion.Identity);

                Assert.True(
                    written.Normalized().AngleTo(collapsedWristRelative) > 0.15f,
                    $"Expected {FingerBoneName(side, XRHandJoint.IndexProximal)} to exclude the metacarpal flexion.");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An invalid metacarpal source parent freezes that finger's proximal — there is no wrist fallback —
    /// while the proximal's own sample stays valid, its intermediate child keeps updating through its valid
    /// proximal parent, and other fingers continue (XR-002 TR14, TR20, TR22, AC16).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidMetacarpalSourceParent_FreezesProximalWithoutWristFallback()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenProximal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal),
                fixture.BoneIndices);

            // The index metacarpal goes untracked; the proximal sample itself stays valid and tracked.
            fixture.Runtime.SetHandJointTracked(LimbSide.Left, XRHandJoint.IndexMetacarpal, false);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, skipJoints: [XRHandJoint.IndexMetacarpal]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenProximal,
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal), fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexIntermediate, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.IndexIntermediate), fixture.BoneIndices),
                "LeftIndexIntermediate through its still-valid proximal parent");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.MiddleProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.MiddleProximal), fixture.BoneIndices),
                "LeftMiddleProximal other finger");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.IndexProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.IndexProximal), fixture.BoneIndices),
                "RightIndexProximal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Whole-hand sample loss freezes every finger of that hand at its cached rotations while the other hand
    /// keeps updating (XR-002 TR23, AC4).
    /// </summary>
    [Headless]
    [Fact]
    public async Task WholeHandLoss_FreezesThatHandWhileOtherContinues()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Dictionary<string, Quaternion> frozenLeft = [];
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints().Where(entry => entry.Side == LimbSide.Left))
            {
                frozenLeft[FingerBoneName(side, joint)] = fixture.PoseCapture.FingerRotation(
                    FingerBoneName(side, joint),
                    fixture.BoneIndices);
            }

            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Left, joint);
            }

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.6f);

            await WaitForFramesAsync(sceneTree, 3);

            foreach ((string boneName, Quaternion frozen) in frozenLeft)
            {
                Assert.Equal(frozen, fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices));
            }

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints().Where(entry => entry.Side == LimbSide.Right))
            {
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 1.6f),
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    FingerBoneName(side, joint));
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Entering optical mode with no valid samples keeps the entry snapshot fixed while authored animation
    /// keeps advancing (XR-002 TR21, AC18).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InitialInvalidOpticalFrame_EntrySnapshotHoldsWhileAuthoredAdvances()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            ApplyAuthoredGrabRotations(fixture);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);

            // Authored animation advances beneath the optical session...
            Quaternion advancing = Basis.Identity.Rotated(Vector3.Right, 1.2f).GetRotationQuaternion();
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                fixture.Skeleton.SetBonePoseRotation(fixture.BoneIndices[FingerBoneName(side, joint)], advancing);
            }

            await WaitForFramesAsync(sceneTree, 3);

            // ...but the entry snapshot stays authoritative while no valid sample exists.
            foreach ((string boneName, Quaternion snapshot) in fixture.AuthoredRotations)
            {
                AssertRotationApproximately(
                    snapshot,
                    fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices),
                    boneName);
                Assert.True(
                    RotationAngleRadians(snapshot, advancing) > 0.05f,
                    $"Expected the authored advancing pose to differ from the entry snapshot for {boneName}.");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Re-entering optical mode snapshots the then-current authored rotations instead of replaying stale
    /// tracking from the previous session (XR-002 TR21, TR24).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CacheResetAcrossSessions_UsesFreshAuthoredSnapshotOnReentry()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            // Session one: tracked pose applied.
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion trackedSample = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Right, XRHandJoint.IndexProximal),
                fixture.BoneIndices);

            // Leave optical mode and author a different pose while in controller mode.
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);

            await WaitForFramesAsync(sceneTree, 2);

            Quaternion reauthored = Basis.Identity.Rotated(Vector3.Up, 0.6f).GetRotationQuaternion();
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                fixture.Skeleton.SetBonePoseRotation(fixture.BoneIndices[FingerBoneName(side, joint)], reauthored);
            }

            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Modifier.IsOpticalSessionActive);

            // Session two: no optical data at all, so the session-one samples must be removed before
            // re-entering optical mode.
            foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
            {
                fixture.Runtime.ClearHandJointSample(LimbSide.Left, joint);
                fixture.Runtime.ClearHandJointSample(LimbSide.Right, joint);
            }

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);
            Assert.True(RotationAngleRadians(trackedSample, reauthored) > 0.05f, "Expected tracked and re-authored poses to differ.");

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    reauthored,
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    FingerBoneName(side, joint));
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// A joint whose first samples are invalid keeps the authored entry snapshot frozen; at its first valid
    /// sample later in the session it writes the then-current tracked destination output — identical to what an
    /// early-valid joint would write at that flex, because no capture-time baseline exists — while
    /// earlier-valid siblings track the latest sample (XR-002 TR21-TR22).
    /// </summary>
    [Headless]
    [Fact]
    public async Task LateFirstValidSample_WritesTrackedLocalWithoutCaptureTimeBaseline()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            // An authored grab pose ahead of entry: the frozen joints must hold it, and the valid joints
            // must ignore it completely.
            ApplyAuthoredGrabRotations(fixture);

            // The left index intermediate joint is invalid from session entry; its distal child is invalid
            // too because its required source parent is missing.
            fixture.Runtime.ClearHandJointSample(LimbSide.Left, XRHandJoint.IndexIntermediate);
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f, skipJoints: [XRHandJoint.IndexIntermediate]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);

            string intermediateBone = FingerBoneName(LimbSide.Left, XRHandJoint.IndexIntermediate);
            string distalBone = FingerBoneName(LimbSide.Left, XRHandJoint.IndexDistal);

            // While invalid, the intermediate and its distal child hold the authored entry snapshot.
            AssertRotationApproximately(
                fixture.AuthoredRotations[intermediateBone],
                fixture.PoseCapture.FingerRotation(intermediateBone, fixture.BoneIndices),
                "LeftIndexIntermediate frozen at the authored snapshot");
            AssertRotationApproximately(
                fixture.AuthoredRotations[distalBone],
                fixture.PoseCapture.FingerRotation(distalBone, fixture.BoneIndices),
                "LeftIndexDistal frozen through its invalid parent");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.MiddleProximal, 1.0f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.MiddleProximal), fixture.BoneIndices),
                "LeftMiddleProximal valid sibling at entry");

            // The late joints' first valid sample arrives at a stronger flex; their write is exactly the
            // rest-corrected tracked output at that flex — no entry, baseline, or capture-time term exists.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.6f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.6f);

            await WaitForFramesAsync(sceneTree, 3);

            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexIntermediate, 1.6f),
                fixture.PoseCapture.FingerRotation(intermediateBone, fixture.BoneIndices),
                "LeftIndexIntermediate first valid sample");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexDistal, 1.6f),
                fixture.PoseCapture.FingerRotation(distalBone, fixture.BoneIndices),
                "LeftIndexDistal first valid sample");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.MiddleProximal, 1.6f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.MiddleProximal), fixture.BoneIndices),
                "LeftMiddleProximal early sibling");

            // Later samples keep updating every joint alike.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 2.2f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 2.2f);

            await WaitForFramesAsync(sceneTree, 3);

            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexIntermediate, 2.2f),
                fixture.PoseCapture.FingerRotation(intermediateBone, fixture.BoneIndices),
                "LeftIndexIntermediate after late capture");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.MiddleProximal, 2.2f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.MiddleProximal), fixture.BoneIndices),
                "LeftMiddleProximal after late capture");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Rebinding the modifier to another skeleton clears the optical session and cache; rebinding back
    /// re-enters the session writing the then-current tracked outputs instead of continuing the previous
    /// session's state (XR-002 TR21, TR24).
    /// </summary>
    [Headless]
    [Fact]
    public async Task SkeletonRebind_ClearsSessionStateAndRewritesCurrentTrackedLocals()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            Dictionary<string, Quaternion> entryPose = CaptureAuthoredFingerRotations(fixture);

            // Session one: tracked outputs at flex 1.0, then displaced by a stronger sample.
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 2.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 2.0f);

            await WaitForFramesAsync(sceneTree, 3);

            string indexProximal = FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal);
            Assert.True(
                RotationAngleRadians(entryPose[indexProximal], fixture.PoseCapture.FingerRotation(indexProximal, fixture.BoneIndices)) > 0.05f,
                "Expected session one to displace the entry pose before the rebind.");

            // Rebind the modifier to a bare skeleton: binding state and the session reset, and the authored
            // pose of the test skeleton becomes authoritative again.
            Skeleton3D rebindTarget = new()
            {
                Name = "RebindTargetSkeleton",
            };
            fixture.Root.AddChild(rebindTarget);
            fixture.Skeleton.RemoveChild(fixture.Modifier);
            rebindTarget.AddChild(fixture.Modifier);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.False(fixture.Modifier.IsOpticalSessionActive);
            Assert.False(fixture.Modifier.IsFingerTopologyValid);

            // Author a conspicuous pose while no modifier writes the test skeleton; the fresh session must
            // write tracked outputs, not hold this authored pose.
            Quaternion reauthored = Basis.Identity.Rotated(Vector3.Up, 0.9f).GetRotationQuaternion();
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                fixture.Skeleton.SetBonePoseRotation(fixture.BoneIndices[FingerBoneName(side, joint)], reauthored);
            }

            await WaitForFramesAsync(sceneTree, 2);

            // Rebind back: the session re-enters and the still-present flex-2.0 samples write the tracked
            // outputs for that flex — no session-one state survives the rebind.
            rebindTarget.RemoveChild(fixture.Modifier);
            fixture.Skeleton.AddChild(fixture.Modifier);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 2.0f),
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    $"{FingerBoneName(side, joint)} fresh session");
            }

            // Later samples keep tracking.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 2.6f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 2.6f);

            await WaitForFramesAsync(sceneTree, 3);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                string boneName = FingerBoneName(side, joint);
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 2.6f),
                    fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices),
                    $"{boneName} after fresh session update");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Re-entering optical mode after leaving it clears the previous session's cache: a first valid sample
    /// of the second session writes the tracked output for the second session's samples instead of
    /// continuing the first session's output (XR-002 TR21, TR24).
    /// </summary>
    [Headless]
    [Fact]
    public async Task ModeReentry_SessionStateClearedAndRewritesCurrentTrackedLocals()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            // Session one: tracked output at flex 1.0, then a stronger sample.
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 2.2f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 2.2f);

            await WaitForFramesAsync(sceneTree, 3);

            // Leave optical mode and author a fresh pose while in controller mode.
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);

            await WaitForFramesAsync(sceneTree, 2);

            Quaternion sessionTwoEntry = Basis.Identity.Rotated(Vector3.Up, 0.9f).GetRotationQuaternion();
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                fixture.Skeleton.SetBonePoseRotation(fixture.BoneIndices[FingerBoneName(side, joint)], sessionTwoEntry);
            }

            await WaitForFramesAsync(sceneTree, 2);
            Assert.False(fixture.Modifier.IsOpticalSessionActive);

            // Session two: a first valid sample at flex 1.3 must write that flex's tracked outputs — any
            // leaked session-one cache would instead freeze the session-one pose.
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.3f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.3f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 1.3f),
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    $"{FingerBoneName(side, joint)} fresh session");
            }

            // And later samples keep tracking.
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 2.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 2.0f);

            await WaitForFramesAsync(sceneTree, 3);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                string boneName = FingerBoneName(side, joint);
                AssertRotationApproximately(
                    ExpectedRestDerivedRotation(fixture.Skeleton, side, joint, 2.0f),
                    fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices),
                    $"{boneName} after fresh session update");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Non-finite joint samples are rejected by the provider contract so the cached rotation is retained
    /// (XR-002 TR20).
    /// </summary>
    [Headless]
    [Fact]
    public async Task NonFiniteSamples_AreRejectedAndCacheRetained()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion cachedMiddle = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Right, XRHandJoint.MiddleIntermediate),
                fixture.BoneIndices);

            // Corrupt one joint with a non-finite basis while a sibling receives a valid update.
            Basis nanBasis = new(
                new Vector3(float.NaN, 0.0f, 0.0f),
                Vector3.Up,
                Vector3.Back);
            fixture.Runtime.SetHandJointSample(
                LimbSide.Right,
                XRHandJoint.MiddleIntermediate,
                new Transform3D(nanBasis, new Vector3(float.PositiveInfinity, 0.0f, 0.0f)));
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.4f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.4f, skipJoints: [XRHandJoint.MiddleIntermediate]);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                cachedMiddle,
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.MiddleIntermediate), fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.RingProximal, 1.4f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.RingProximal), fixture.BoneIndices),
                "RightRingProximal valid sibling");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An asymmetric non-identity rest fixture exercises the effective neutral through the actual skeleton modifier
    /// pipeline. At identity source delta, all non-middle chains receive one chain-wide swing towards the same-hand
    /// middle proximal direction while preserving imported curvature and rotation-only ownership (XR-002 R30, AC29-30).
    /// </summary>
    [Headless]
    [Fact]
    public async Task AsymmetricRestNeutral_AlignsProximalsAndPreservesWholeChainGeometryAtIdentityDelta()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(
            sceneTree,
            includeLeftRingDistal: true,
            asymmetricRest: true);

        try
        {
            Dictionary<string, Vector3> positionsBefore = CapturePositions(fixture);
            Dictionary<string, Vector3> scalesBefore = CaptureScales(fixture);
            var globalRest = CanonicalFingerBoneNames()
                .ToDictionary(
                    boneName => boneName,
                    boneName => fixture.Skeleton.GetBoneGlobalRest(fixture.BoneIndices[boneName]));

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 0.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);
            Assert.True(fixture.Modifier.IsFingerTopologyValid);

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                foreach (IReadOnlyList<XRHandJoint> chain in OpticalFingerTrackingTestTopology.NonThumbChains)
                {
                    AssertObservedSegmentMatchesRestLength(fixture, globalRest, side, chain[0], chain[1]);
                    AssertObservedSegmentMatchesRestLength(fixture, globalRest, side, chain[1], chain[2]);
                }

                Vector3 target = RestSegmentDirection(globalRest, side, XRHandJoint.MiddleProximal, XRHandJoint.MiddleIntermediate);
                foreach (IReadOnlyList<XRHandJoint> chain in OpticalFingerTrackingTestTopology.NonThumbChains)
                {
                    XRHandJoint proximal = chain[0];
                    XRHandJoint intermediate = chain[1];
                    XRHandJoint distal = chain[2];
                    Vector3 restDirection = RestSegmentDirection(globalRest, side, proximal, intermediate);
                    if (proximal != XRHandJoint.MiddleProximal)
                    {
                        Assert.True(
                            restDirection.AngleTo(target) > 0.1f,
                            $"Fixture {side} {proximal} must begin materially unequal to the middle reference.");
                    }

                    Vector3 actualDirection = PoseSegmentDirection(fixture, side, proximal, intermediate);
                    AssertDirectionApproximately(target, actualDirection, $"{side} {proximal} proximal direction");

                    Quaternion restProximal = GlobalRestRotation(globalRest, side, proximal);
                    Quaternion restIntermediate = GlobalRestRotation(globalRest, side, intermediate);
                    Quaternion restDistal = GlobalRestRotation(globalRest, side, distal);
                    Quaternion actualProximal = GlobalPoseRotation(fixture, side, proximal);
                    Quaternion actualIntermediate = GlobalPoseRotation(fixture, side, intermediate);
                    Quaternion actualDistal = GlobalPoseRotation(fixture, side, distal);

                    AssertRotationApproximately(
                        restProximal.Inverse() * restIntermediate,
                        actualProximal.Inverse() * actualIntermediate,
                        $"{side} {proximal} preserved proximal/intermediate relative rotation");
                    AssertRotationApproximately(
                        restIntermediate.Inverse() * restDistal,
                        actualIntermediate.Inverse() * actualDistal,
                        $"{side} {proximal} preserved intermediate/distal relative rotation");

                    Vector3 restSecondSegment = RestSegmentDirection(globalRest, side, intermediate, distal);
                    Vector3 actualSecondSegment = PoseSegmentDirection(fixture, side, intermediate, distal);
                    Vector3 swungRestSecondSegment =
                        new Basis(IndependentRestSwing(globalRest, side, proximal)) * restSecondSegment;
                    float restBend = target.AngleTo(swungRestSecondSegment);
                    float actualBend = actualDirection.AngleTo(actualSecondSegment);
                    Assert.InRange(Mathf.Abs(restBend - actualBend), 0.0f, DirectionTolerance);
                    AssertDirectionApproximately(
                        target.Cross(swungRestSecondSegment),
                        actualDirection.Cross(actualSecondSegment),
                        $"{side} {proximal} preserved bend plane");

                    Assert.False(
                        HasBoneChild(fixture.Skeleton, fixture.BoneIndices[FingerBoneName(side, distal)]),
                        $"The {side} {distal} fixture bone must have no invented tip child.");
                }

                AssertRotationApproximately(
                    GlobalRestRotation(globalRest, side, XRHandJoint.MiddleProximal),
                    GlobalPoseRotation(fixture, side, XRHandJoint.MiddleProximal),
                    $"{side} middle global orientation unchanged");
            }

            Quaternion expectedIndexNeutral = ExpectedRestDerivedRotation(
                fixture.Skeleton,
                LimbSide.Left,
                XRHandJoint.IndexProximal,
                flexScale: 0.0f);
            Assert.True(
                Quaternion.Identity.AngleTo(expectedIndexNeutral) > 0.1f,
                "The fixture must require a materially non-identity effective N at identity Delta.");
            AssertRotationApproximately(
                expectedIndexNeutral,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal),
                    fixture.BoneIndices),
                "LeftIndexProximal non-identity N at identity Delta");

            foreach ((string boneName, Vector3 position) in positionsBefore)
            {
                Assert.Equal(position, fixture.PoseCapture.Positions[fixture.BoneIndices[boneName]]);
            }

            foreach ((string boneName, Vector3 scale) in scalesBefore)
            {
                Assert.Equal(scale, fixture.PoseCapture.Scales[fixture.BoneIndices[boneName]]);
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Configuring a replacement authored-reference path is honoured without code or asset changes
    /// (XR-002 TR25, A15): the modifier loads exactly the configured paths directly, so pointing the flexion
    /// reference at the neutral Reset resource — a valid single-key Animation with the wrong semantics —
    /// fails the authored-angle gate with the exact contract reason, while the pinned defaults bind.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AuthoredReferencePaths_ReplacementConfigurationIsHonoured()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture replacementFixture = await FingerFixture.CreateAsync(
            sceneTree,
            includeLeftRingDistal: true,
            expectTopologyValid: false,
            configureModifier: modifier => modifier.AuthoredFlexionReferenceAnimationPath =
                OpticalFingerTrackingTestTopology.DefaultNeutralReferencePath);

        try
        {
            await WaitForFramesAsync(sceneTree, 3);

            // The replacement path is a real, valid, immutable Animation — but flexion equals Reset, so the
            // authored reference angle is zero and the single bilateral binding transaction fails closed.
            Assert.False(replacementFixture.Modifier.IsFingerTopologyValid);
            foreach ((string boneName, int boneIndex) in replacementFixture.BoneIndices)
            {
                if (CanonicalFingerBoneNames().Contains(boneName))
                {
                    Assert.Equal(
                        Quaternion.Identity,
                        replacementFixture.Skeleton.GetBonePoseRotation(boneIndex));
                }
            }

            using FingerFixture defaultFixture = await FingerFixture.CreateAsync(
                sceneTree,
                includeLeftRingDistal: true);
            await WaitForFramesAsync(sceneTree, 3);
            Assert.True(defaultFixture.Modifier.IsFingerTopologyValid);
        }
        finally
        {
            await replacementFixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An overall profile-resolution failure clears every record before processing. No valid first duplicate or
    /// stale prior-session record may continue driving; thumbs freeze at their authored entry snapshot exactly
    /// like non-thumbs (XR-002 TR34, AC25).
    /// </summary>
    [Headless]
    [Fact]
    public async Task DuplicateProfileResolutionFailure_FreezesAllDestinationsWithoutUsingPartialOrStaleRecords()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.8f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.8f);
            await WaitForFramesAsync(sceneTree, 3);

            Quaternion staleTracked = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal),
                fixture.BoneIndices);
            Quaternion staleThumbTracked = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                fixture.BoneIndices);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Controller,
                XRHandSourceObservation.Controller);
            await WaitForFramesAsync(sceneTree, 2);

            Quaternion authored = Basis.Identity.Rotated(Vector3.Up, 0.73f).GetRotationQuaternion();
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                fixture.Skeleton.SetBonePoseRotation(fixture.BoneIndices[FingerBoneName(side, joint)], authored);
            }

            fixture.Modifier.CalibrationProfile = CreateDuplicateCalibrationProfile();
            await WaitForFramesAsync(sceneTree, 2);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.2f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.2f);
            await WaitForFramesAsync(sceneTree, 4);

            Assert.True(fixture.Modifier.IsOpticalSessionActive);
            Assert.True(staleTracked.AngleTo(authored) > 0.05f, "Fixture must distinguish stale tracking from the authored snapshot.");
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                AssertRotationApproximately(
                    authored,
                    fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                    $"{side} {joint} fail-closed profile snapshot");
            }

            Assert.True(
                staleThumbTracked.AngleTo(authored) > 0.05f,
                "Fixture must distinguish the stale thumb tracking from the authored snapshot.");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An absent profile freezes every destination — thumbs included — at its authored entry snapshot: thumbs are
    /// record-driven through the constrained Stage 1 model and never fall back to a direct mapping
    /// (XR-002 TR34, AC25).
    /// </summary>
    [Headless]
    [Fact]
    public async Task AbsentProfile_FreezesEveryDestinationAtAuthoredSnapshot()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            fixture.Modifier.CalibrationProfile = null;
            Quaternion authoredIndex = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal),
                fixture.BoneIndices);
            Quaternion authoredThumbProximal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                fixture.BoneIndices);
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.8f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.8f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.Equal(
                authoredIndex,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal),
                    fixture.BoneIndices));
            Assert.Equal(
                authoredThumbProximal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                    fixture.BoneIndices));
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// All 30 canonical finger-bone names resolve on the reference skeleton, the player template carries
    /// exactly one optical modifier, and a missing required bone produces a clear validation failure without
    /// writes (XR-002 TR12, AC14).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CanonicalSkeletonValidation_ResolvesThirtyBonesAndMissingBoneFailsClearly()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);

        using Node baseRoot = LoadPackedScene(ReferenceFemaleBaseScenePath).Instantiate();
        try
        {
            Skeleton3D baseSkeleton = baseRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");

            string[] expectedBoneNames = [.. CanonicalFingerBoneNames()];
            Assert.Equal(expectedBoneNames, OpticalFingerTrackingModifier.CanonicalFingerBoneNames);

            // This observes the production topology API itself; stimulus and rotation oracles use only the
            // explicit test-owned topology.
            foreach (string boneName in expectedBoneNames)
            {
                Assert.True(baseSkeleton.FindBone(boneName) >= 0, $"Expected reference skeleton to contain {boneName}.");
            }

            Assert.Equal(30, expectedBoneNames.Length);
        }
        finally
        {
            baseRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }

        using Node templateRoot = LoadPackedScene(ReferenceFemalePlayerTemplatePath).Instantiate();
        try
        {
            Skeleton3D templateSkeleton = templateRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            OpticalFingerTrackingModifier templateModifier = Assert.IsType<OpticalFingerTrackingModifier>(
                templateSkeleton.GetNode("OpticalFingerTrackingModifier"),
                exactMatch: false);

            Assert.True(templateModifier.Active);
            OpticalFingerTrackingCalibrationProfile profile = Assert.IsType<OpticalFingerTrackingCalibrationProfile>(
                templateModifier.CalibrationProfile,
                exactMatch: false);
            Assert.Equal(CalibrationProfilePath, profile.ResourcePath);
            var resolved = new ResolvedOpticalFingerCalibration[OpticalFingerTrackingCalibrationProfile.RecordCount];
            bool[] valid = new bool[OpticalFingerTrackingCalibrationProfile.RecordCount];
            var resolvedMetacarpal = new ResolvedThumbMetacarpalCalibration[2];
            Assert.True(
                profile.TryResolve(resolved, valid, resolvedMetacarpal, out string validationError),
                validationError);
            Assert.Equal(OpticalFingerTrackingCalibrationProfile.RecordCount, valid.Count(value => value));
            Assert.Equal(
                OpticalFingerTrackingTestTopology.ExpectedMetacarpalSwingGains[0],
                resolvedMetacarpal[0].SwingGain);
            Assert.Equal(
                OpticalFingerTrackingTestTopology.ExpectedMetacarpalSwingGains[1],
                resolvedMetacarpal[1].SwingGain);
        }
        finally
        {
            templateRoot.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }

        // A missing required destination bone must fail validation loudly and disable writes.
        using FingerFixture fixture = await FingerFixture.CreateAsync(
            sceneTree,
            includeLeftRingDistal: false,
            expectTopologyValid: false);

        try
        {
            ApplyAuthoredGrabRotations(fixture);

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 4);

            Assert.False(fixture.Modifier.IsFingerTopologyValid);
            Assert.Equal("LeftRingDistal", Assert.Single(fixture.Modifier.MissingFingerBoneNames));
            Assert.False(fixture.Modifier.IsOpticalSessionActive);

            foreach ((string boneName, Quaternion authored) in fixture.AuthoredRotations)
            {
                Assert.Equal(authored, fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices));
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Deliberate tracked spread — a proximal-only local rotation about the source palm axis — transfers as a
    /// signed, visible proximal swing of the exact tracked magnitude towards the mapped hinge side, while the
    /// PIP/DIP destinations stay exactly at their effective neutrals and the neutral is recovered when the spread
    /// is released (XR-002 TR22, AC24).
    /// </summary>
    [Headless]
    [Fact]
    public async Task DeliberateSpread_TiltsProximalsSignedByExactAmountWhileHingesHoldNeutral()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 0.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);

            await WaitForFramesAsync(sceneTree, 4);

            const float spreadRadians = 0.42f;
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 0.0f, proximalSpreadRadians: spreadRadians);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f, proximalSpreadRadians: -spreadRadians);

            await WaitForFramesAsync(sceneTree, 4);

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                float signedSpread = side == LimbSide.Left ? spreadRadians : -spreadRadians;
                Quaternion spreadRelation = new(Vector3.Back, signedSpread);
                foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.NonThumbDestinationJoints)
                {
                    Quaternion expected = OpticalFingerTrackingTestTopology.IsProximalDestination(joint)
                        ? OpticalFingerTrackingTestTopology.ExpectedAnatomicalRotation(
                            fixture.Skeleton,
                            side,
                            joint,
                            spreadRelation,
                            Quaternion.Identity)
                        // The intermediate/distal locals are pure identity deltas, so they hold the effective
                        // neutral exactly (XR-002 TR23).
                        : OpticalFingerTrackingTestTopology.ExpectedDestinationNeutral(fixture.Skeleton, side, joint);
                    AssertRotationApproximately(
                        expected,
                        fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                        $"{FingerBoneName(side, joint)} deliberate spread");
                }

                // The visible magnitude equals the tracked spread and tilts the proximal direction towards the
                // mapped hinge side (d_s = l cos φ − h sin φ), not into the palm (XR-002 TR22, H3).
                Quaternion neutral = OpticalFingerTrackingTestTopology.ExpectedDestinationNeutral(
                    fixture.Skeleton,
                    side,
                    XRHandJoint.IndexProximal);
                Quaternion written = fixture.PoseCapture.FingerRotation(
                    FingerBoneName(side, XRHandJoint.IndexProximal),
                    fixture.BoneIndices);
                Assert.True(
                    neutral.Normalized().AngleTo(written.Normalized()) is > 0.3f and < 0.55f,
                    $"Expected the deliberate spread to tilt {FingerBoneName(side, XRHandJoint.IndexProximal)} by the tracked amount.");
            }

            // Releasing the spread returns exactly to the effective neutral (XR-002 TR23, H2).
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 0.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 0.0f);

            await WaitForFramesAsync(sceneTree, 4);

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.NonThumbDestinationJoints)
                {
                    AssertRotationApproximately(
                        OpticalFingerTrackingTestTopology.ExpectedDestinationNeutral(fixture.Skeleton, side, joint),
                        fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                        $"{FingerBoneName(side, joint)} spread released");
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Combined partial curl and deliberate spread stays coherent: the proximal receives both motion
    /// components through one roll-free swing while the intermediate/distal receive only the hinge flexion of
    /// their own tracked locals (XR-002 TR21-TR22, AC24, H4).
    /// </summary>
    [Headless]
    [Fact]
    public async Task CombinedCurlAndSpread_MapsProximalSwingAndHingeFlexionCoherently()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            const float flex = 1.15f;
            const float spread = 0.35f;

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, flex, proximalSpreadRadians: spread);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, flex, proximalSpreadRadians: -spread);

            await WaitForFramesAsync(sceneTree, 4);

            foreach (LimbSide side in OpticalFingerTrackingTestTopology.Sides)
            {
                float signedSpread = side == LimbSide.Left ? spread : -spread;
                foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.NonThumbDestinationJoints)
                {
                    Quaternion relation = OpticalFingerTrackingTestTopology.IsProximalDestination(joint)
                        ? new Quaternion(Vector3.Right, JointLocalFlexRadians(joint) * flex)
                            * new Quaternion(Vector3.Back, signedSpread)
                        : ExpectedSourceDelta(joint, flex);
                    AssertRotationApproximately(
                        OpticalFingerTrackingTestTopology.ExpectedAnatomicalRotation(
                            fixture.Skeleton,
                            side,
                            joint,
                            relation,
                            Quaternion.Identity),
                        fixture.PoseCapture.FingerRotation(FingerBoneName(side, joint), fixture.BoneIndices),
                        $"{FingerBoneName(side, joint)} combined curl/spread");
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// Artificial source-roll contamination — a local roll about the source longitudinal direction on every
    /// tracked joint — never reaches any destination: the non-thumb proximal swing and hinge joints discard it,
    /// and the Stage 1 thumb discards the metacarpal axial opposition roll and the hinge joints' roll exactly
    /// like the non-thumb model (XR-002 TR21-TR22, TR24, TR26, A3, H6).
    /// </summary>
    [Headless]
    [Fact]
    public async Task SourceRollContamination_LeavesEveryDestinationOutputUnchanged()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            const float flex = 1.3f;
            const float roll = 1.05f;

            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, flex);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, flex);

            await WaitForFramesAsync(sceneTree, 4);

            Dictionary<string, Quaternion> rollFree = [];
            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                rollFree[FingerBoneName(side, joint)] = fixture.PoseCapture.FingerRotation(
                    FingerBoneName(side, joint),
                    fixture.BoneIndices);
            }

            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, flex, longitudinalRollRadians: roll);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, flex, longitudinalRollRadians: roll);

            await WaitForFramesAsync(sceneTree, 4);

            foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
            {
                string boneName = FingerBoneName(side, joint);
                Quaternion current = fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices);
                AssertRotationApproximately(
                    rollFree[boneName],
                    current,
                    $"{boneName} discards longitudinal roll");
            }
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An invalid wrist joint freezes the thumb metacarpal and proximal destinations — their required source
    /// parent — while the thumb distal, the non-thumb chains, and the other hand continue tracking
    /// (XR-002 TR28, A11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidWristJoint_FreezesThumbMetacarpalAndProximalWhileOtherDestinationsContinue()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenMetacarpal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbMetacarpal),
                fixture.BoneIndices);
            Quaternion frozenProximal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                fixture.BoneIndices);

            // The wrist joint goes untracked while every finger joint stays valid.
            fixture.Runtime.SetHandJointTracked(LimbSide.Left, XRHandJoint.Wrist, false);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, skipJoints: [XRHandJoint.Wrist]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenMetacarpal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbMetacarpal),
                    fixture.BoneIndices));
            Assert.Equal(
                frozenProximal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                    fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.ThumbDistal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal), fixture.BoneIndices),
                "LeftThumbDistal keeps tracking through its valid thumb proximal parent");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal), fixture.BoneIndices),
                "LeftIndexProximal non-thumb chain stays live without a wrist dependency");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.ThumbMetacarpal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.ThumbMetacarpal), fixture.BoneIndices),
                "RightThumbMetacarpal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An invalid thumb metacarpal freezes the thumb metacarpal and proximal destinations while the thumb distal
    /// keeps tracking through its still-valid thumb proximal parent and the non-thumb chains stay live
    /// (XR-002 TR28, A11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidThumbMetacarpal_FreezesMetacarpalAndProximalWhileDistalAndNonThumbContinue()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenMetacarpal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbMetacarpal),
                fixture.BoneIndices);
            Quaternion frozenProximal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                fixture.BoneIndices);

            fixture.Runtime.SetHandJointTracked(LimbSide.Left, XRHandJoint.ThumbMetacarpal, false);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, skipJoints: [XRHandJoint.ThumbMetacarpal]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenMetacarpal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbMetacarpal),
                    fixture.BoneIndices));
            Assert.Equal(
                frozenProximal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                    fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.ThumbDistal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal), fixture.BoneIndices),
                "LeftThumbDistal through its valid thumb proximal parent");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.LittleProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.LittleProximal), fixture.BoneIndices),
                "LeftLittleProximal non-thumb chain");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An invalid thumb proximal freezes the thumb proximal and distal destinations while the thumb
    /// metacarpal keeps tracking through its still-valid wrist parent and the non-thumb chains stay live
    /// (XR-002 TR28, A11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidThumbProximal_FreezesProximalAndDistalWhileMetacarpalAndNonThumbContinue()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenProximal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                fixture.BoneIndices);
            Quaternion frozenDistal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal),
                fixture.BoneIndices);

            // The thumb proximal goes untracked; the metacarpal's wrist parent stays valid.
            fixture.Runtime.SetHandJointTracked(LimbSide.Left, XRHandJoint.ThumbProximal, false);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, skipJoints: [XRHandJoint.ThumbProximal]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenProximal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal),
                    fixture.BoneIndices));
            Assert.Equal(
                frozenDistal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal),
                    fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.ThumbMetacarpal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.ThumbMetacarpal), fixture.BoneIndices),
                "LeftThumbMetacarpal keeps tracking through its valid wrist parent");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal), fixture.BoneIndices),
                "LeftIndexProximal non-thumb chain");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.ThumbProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.ThumbProximal), fixture.BoneIndices),
                "RightThumbProximal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    /// <summary>
    /// An invalid thumb distal freezes only the thumb distal destination while the thumb metacarpal and
    /// proximal keep tracking alongside the non-thumb chains (XR-002 TR28, A11).
    /// </summary>
    [Headless]
    [Fact]
    public async Task InvalidThumbDistal_FreezesOnlyDistalWhileThumbAndNonThumbContinue()
    {
        SceneTree sceneTree = GetSceneTree();
        using FingerFixture fixture = await FingerFixture.CreateAsync(sceneTree, includeLeftRingDistal: true);

        try
        {
            _ = fixture.Runtime.SetHandObservations(
                XRHandSourceObservation.Optical,
                XRHandSourceObservation.Optical);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.0f);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.0f);

            await WaitForFramesAsync(sceneTree, 3);

            Quaternion frozenDistal = fixture.PoseCapture.FingerRotation(
                FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal),
                fixture.BoneIndices);

            // The thumb distal goes untracked; its proximal parent and every other source stay valid.
            fixture.Runtime.SetHandJointTracked(LimbSide.Left, XRHandJoint.ThumbDistal, false);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Left, 1.9f, skipJoints: [XRHandJoint.ThumbDistal]);
            InjectTrackedHandPose(fixture.Runtime, LimbSide.Right, 1.9f);

            await WaitForFramesAsync(sceneTree, 3);

            Assert.Equal(
                frozenDistal,
                fixture.PoseCapture.FingerRotation(
                    FingerBoneName(LimbSide.Left, XRHandJoint.ThumbDistal),
                    fixture.BoneIndices));
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.ThumbMetacarpal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.ThumbMetacarpal), fixture.BoneIndices),
                "LeftThumbMetacarpal keeps tracking");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.ThumbProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.ThumbProximal), fixture.BoneIndices),
                "LeftThumbProximal keeps tracking through its valid metacarpal parent");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Left, XRHandJoint.IndexProximal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Left, XRHandJoint.IndexProximal), fixture.BoneIndices),
                "LeftIndexProximal non-thumb chain");
            AssertRotationApproximately(
                ExpectedRestDerivedRotation(fixture.Skeleton, LimbSide.Right, XRHandJoint.ThumbDistal, 1.9f),
                fixture.PoseCapture.FingerRotation(FingerBoneName(LimbSide.Right, XRHandJoint.ThumbDistal), fixture.BoneIndices),
                "RightThumbDistal other hand");
        }
        finally
        {
            await fixture.DisposeAsync(sceneTree);
        }
    }

    private static SkeletonPoseCapture AttachPoseCapture(Skeleton3D skeleton)
        => SkeletonPoseCapture.Attach(skeleton);

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
            XRHandJoint.Wrist => throw new NotImplementedException(),
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Not a finger joint."),
        };

    /// <summary>
    /// Hand-derived source relation: each joint is injected as <c>wristRotation * cumulativeLocal</c> —
    /// <c>cumulative = parentCumulative * local</c> — so its parent-relative quotient is exactly the injected
    /// local flexion, regardless of wrist yaw, metacarpal offsets, chain state, translations, or session history.
    /// This supplies S. Every destination maps S through the test-owned anatomical oracle — the Stage 1 thumb
    /// model for thumbs and the constrained non-thumb model for the rest — with this fixture's identity S0
    /// profile (XR-002 TR20-TR29).
    /// </summary>
    private static Quaternion ExpectedSourceDelta(XRHandJoint joint, float flexScale)
        => Basis.Identity.Rotated(Vector3.Right, JointLocalFlexRadians(joint) * flexScale).GetRotationQuaternion();

    private static Quaternion ExpectedRestDerivedRotation(
        Skeleton3D skeleton,
        LimbSide side,
        XRHandJoint joint,
        float flexScale)
        => OpticalFingerTrackingTestTopology.ExpectedAnatomicalRotation(
            skeleton,
            side,
            joint,
            ExpectedSourceDelta(joint, flexScale),
            Quaternion.Identity);

    private static Quaternion IndependentRestSwing(
        IReadOnlyDictionary<string, Transform3D> globalRest,
        LimbSide side,
        XRHandJoint proximal)
        => OpticalFingerTrackingTestTopology.IndependentShortestArc(
            RestSegmentDirection(
                globalRest,
                side,
                proximal,
                OpticalFingerTrackingTestTopology.GetNonThumbChain(proximal)[1]),
            RestSegmentDirection(globalRest, side, XRHandJoint.MiddleProximal, XRHandJoint.MiddleIntermediate));

    private static Vector3 RestSegmentDirection(
        IReadOnlyDictionary<string, Transform3D> globalRest,
        LimbSide side,
        XRHandJoint from,
        XRHandJoint to)
        => (globalRest[FingerBoneName(side, to)].Origin - globalRest[FingerBoneName(side, from)].Origin).Normalized();

    private static Vector3 PoseSegmentDirection(
        FingerFixture fixture,
        LimbSide side,
        XRHandJoint from,
        XRHandJoint to)
    {
        Vector3 fromOrigin = fixture.PoseCapture.GlobalPoses[fixture.BoneIndices[FingerBoneName(side, from)]].Origin;
        Vector3 toOrigin = fixture.PoseCapture.GlobalPoses[fixture.BoneIndices[FingerBoneName(side, to)]].Origin;
        return (toOrigin - fromOrigin).Normalized();
    }

    private static void AssertObservedSegmentMatchesRestLength(
        FingerFixture fixture,
        IReadOnlyDictionary<string, Transform3D> globalRest,
        LimbSide side,
        XRHandJoint from,
        XRHandJoint to)
    {
        Vector3 restSegment = globalRest[FingerBoneName(side, to)].Origin - globalRest[FingerBoneName(side, from)].Origin;
        Vector3 observedSegment = fixture.PoseCapture.GlobalPoses[fixture.BoneIndices[FingerBoneName(side, to)]].Origin
            - fixture.PoseCapture.GlobalPoses[fixture.BoneIndices[FingerBoneName(side, from)]].Origin;
        float restLength = restSegment.Length();
        float observedLength = observedSegment.Length();

        Assert.True(restLength > 0.0f, $"Fixture rest segment {side} {from}/{to} must be non-zero.");
        Assert.True(observedLength > 0.0f, $"Observed pose segment {side} {from}/{to} must be non-zero.");
        Assert.InRange(
            Mathf.Abs(observedLength - restLength),
            0.0f,
            DirectionTolerance);
    }

    private static Quaternion GlobalRestRotation(
        IReadOnlyDictionary<string, Transform3D> globalRest,
        LimbSide side,
        XRHandJoint joint)
        => globalRest[FingerBoneName(side, joint)].Basis.GetRotationQuaternion().Normalized();

    private static Quaternion GlobalPoseRotation(FingerFixture fixture, LimbSide side, XRHandJoint joint)
        => fixture.PoseCapture.GlobalPoses[fixture.BoneIndices[FingerBoneName(side, joint)]]
            .Basis.GetRotationQuaternion().Normalized();

    private static bool HasBoneChild(Skeleton3D skeleton, int parentIndex)
    {
        for (int boneIndex = 0; boneIndex < skeleton.GetBoneCount(); boneIndex++)
        {
            if (skeleton.GetBoneParent(boneIndex) == parentIndex)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertDirectionApproximately(Vector3 expected, Vector3 actual, string context)
    {
        float angle = expected.Normalized().AngleTo(actual.Normalized());
        Assert.True(angle <= DirectionTolerance, $"Direction mismatch for {context}: angle {angle} radians.");
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
            Provenance = "Synthetic deterministic identity-S0 profile for optical finger integration fixtures.",
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

    private static OpticalFingerTrackingCalibrationProfile CreateDuplicateCalibrationProfile()
    {
        OpticalFingerTrackingCalibrationProfile profile = CreateIdentityCalibrationProfile();
        OpticalFingerTrackingCalibrationEntry original = profile.Entries[0];
        profile.Entries[^1] = new OpticalFingerTrackingCalibrationEntry
        {
            Side = original.Side,
            Joint = original.Joint,
            SourceNeutral = original.SourceNeutral,
            DestinationNeutral = original.DestinationNeutral,
            BasisCorrespondence = original.BasisCorrespondence,
            Gain = original.Gain,
        };
        return profile;
    }

    /// <summary>
    /// Captures the authored finger rotations observed through the pose probe while no modifier writes —
    /// the pose an optical session entry snapshots as its initial freeze cache.
    /// </summary>
    private static Dictionary<string, Quaternion> CaptureAuthoredFingerRotations(FingerFixture fixture)
        => CanonicalFingerBoneNames().ToDictionary(
            boneName => boneName,
            boneName => fixture.PoseCapture.FingerRotation(boneName, fixture.BoneIndices));

    private static void InjectTrackedHandPose(
        MockXRRuntimeNode runtime,
        LimbSide side,
        float flexScale,
        bool conspicuousTranslations = false,
        HashSet<XRHandJoint>? skipJoints = null,
        float proximalSpreadRadians = 0.0f,
        float longitudinalRollRadians = 0.0f)
    {
        float wristYaw = side == LimbSide.Left ? LeftWristYawRadians : RightWristYawRadians;
        Basis wristRotation = Basis.Identity.Rotated(Vector3.Up, wristYaw);
        Vector3 wristOrigin = new(side == LimbSide.Left ? -0.25f : 0.25f, 1.0f, -0.35f);

        // A skipped wrist must keep its invalidated flags: re-injecting the sample would silently mark it
        // tracked again and destroy the invalid-wrist stimulus (XR-002 TR28).
        if (skipJoints is null || !skipJoints.Contains(XRHandJoint.Wrist))
        {
            runtime.SetHandJointSample(side, XRHandJoint.Wrist, new Transform3D(wristRotation, wristOrigin));
        }

        // The test-owned stimulus order is anatomically ordered, so every explicit parent cumulative exists first.
        Dictionary<XRHandJoint, Basis> cumulative = [];
        foreach (XRHandJoint joint in OpticalFingerTrackingTestTopology.TrackedJoints)
        {
            if (joint == XRHandJoint.Wrist)
            {
                cumulative[joint] = Basis.Identity;
                continue;
            }

            // The injected local is a right-composed product — flexion about the delivered hinge +X, then a
            // deliberate spread about the delivered palmward +Z on proximals, then an artificial roll about the
            // delivered longitudinal +Y (XR-002 TR17) — so contamination scenarios apply the roll on the local
            // side the anatomical mapping must discard.
            var local = new Quaternion(Vector3.Right, JointLocalFlexRadians(joint) * flexScale);
            if (proximalSpreadRadians != 0.0f && OpticalFingerTrackingTestTopology.IsProximalDestination(joint))
            {
                local *= new Quaternion(Vector3.Back, proximalSpreadRadians);
            }

            if (longitudinalRollRadians != 0.0f)
            {
                local *= new Quaternion(Vector3.Up, longitudinalRollRadians);
            }

            Basis localBasis = new(local);
            XRHandJoint parent = OpticalFingerTrackingTestTopology.GetTrackedParent(joint)
                ?? throw new InvalidOperationException($"Joint {joint} unexpectedly lacks a source parent.");
            cumulative[joint] = cumulative[parent] * localBasis;

            if (skipJoints is not null && skipJoints.Contains(joint))
            {
                continue;
            }

            Vector3 translation = conspicuousTranslations
                ? new Vector3(7.0f, -5.0f, 3.0f) * ((int)joint * 0.5f)
                : wristOrigin + new Vector3(0.0f, -0.03f * (int)joint, 0.02f);
            runtime.SetHandJointSample(side, joint, new Transform3D(wristRotation * cumulative[joint], translation));
        }
    }

    private static void ApplyAuthoredGrabRotations(FingerFixture fixture)
    {
        Animation grabBall = Assert.IsType<Animation>(ResourceLoader.Load(GrabBallAnimationPath), exactMatch: false);

        foreach ((LimbSide side, XRHandJoint joint) in FingerJoints())
        {
            string boneName = FingerBoneName(side, joint);
            if (!fixture.BoneIndices.ContainsKey(boneName))
            {
                // Missing-topology fixtures deliberately omit one required bone.
                continue;
            }

            NodePath trackPath = new($"%GeneralSkeleton:{boneName}");
            int trackIndex = RequireRotationTrack(grabBall, trackPath);
            Quaternion authored = grabBall.RotationTrackInterpolate(trackIndex, 0.0, backward: false);
            fixture.Skeleton.SetBonePoseRotation(fixture.BoneIndices[boneName], authored);
            fixture.AuthoredRotations[boneName] = authored;
        }
    }

    private static int RequireRotationTrack(Animation animation, NodePath path)
    {
        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            if (animation.TrackGetType(trackIndex) == Animation.TrackType.Rotation3D
                && animation.TrackGetPath(trackIndex) == path)
            {
                return trackIndex;
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected animation '{animation.ResourcePath}' to contain rotation track '{path}'.");
    }

    private static Dictionary<string, Vector3> CapturePositions(FingerFixture fixture)
        => fixture.BoneIndices.ToDictionary(
            entry => entry.Key,
            entry => fixture.PoseCapture.Positions[entry.Value]);

    private static Dictionary<string, Vector3> CaptureScales(FingerFixture fixture)
        => fixture.BoneIndices.ToDictionary(
            entry => entry.Key,
            entry => fixture.PoseCapture.Scales[entry.Value]);

    private static Dictionary<string, Quaternion> CaptureNonFingerRotations(FingerFixture fixture)
        => fixture.BoneIndices
            .Where(entry => !CanonicalFingerBoneNames().Contains(entry.Key))
            .ToDictionary(
                entry => entry.Key,
                entry => fixture.PoseCapture.Rotations[entry.Value]);

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int boneIndex = skeleton.FindBone(boneName);
        return boneIndex >= 0
            ? boneIndex
            : throw new Xunit.Sdk.XunitException($"Expected skeleton to contain {boneName}.");
    }

    private static AnimationNodeStateMachinePlayback ResolvePlayback(AnimationTree animationTree)
        => animationTree.Get(HandPoseAnimationTreePaths.GetNestedStateMachinePlaybackParameter()).As<AnimationNodeStateMachinePlayback>()
           ?? throw new InvalidOperationException("AnimationTree is missing the hand-pose upstream state machine playback.");

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual, string context)
    {
        float angle = expected.Normalized().AngleTo(actual.Normalized());
        Assert.True(
            angle <= RotationToleranceRadians,
            $"Rotation mismatch for {context}: expected {expected} ({expected.GetEuler()}), actual {actual} ({actual.GetEuler()}), angle {angle}.");
    }

    private static float RotationAngleRadians(Quaternion from, Quaternion to)
        => from.Normalized().AngleTo(to.Normalized());

    /// <summary>
    /// Synthetic skeleton fixture with the canonical finger topology, a bound modifier, a pipeline probe
    /// ordered after it, and the deterministic mock XR runtime exposed through a test game service provider.
    /// </summary>
    private sealed class FingerFixture : IDisposable
    {
        private FingerFixture(
            TestGame root,
            MockXRRuntimeNode runtime,
            Skeleton3D skeleton,
            OpticalFingerTrackingModifier modifier,
            SkeletonPoseCapture poseCapture,
            Dictionary<string, int> boneIndices)
        {
            Root = root;
            Runtime = runtime;
            Skeleton = skeleton;
            Modifier = modifier;
            PoseCapture = poseCapture;
            BoneIndices = boneIndices;
        }

        public TestGame Root
        {
            get;
        }

        public MockXRRuntimeNode Runtime
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

        public SkeletonPoseCapture PoseCapture
        {
            get;
        }

        public Dictionary<string, int> BoneIndices
        {
            get;
        }

        public Dictionary<string, Quaternion> AuthoredRotations { get; } = [];

        public static async Task<FingerFixture> CreateAsync(
            SceneTree sceneTree,
            bool includeLeftRingDistal,
            bool asymmetricRest = false,
            bool expectTopologyValid = true,
            Action<OpticalFingerTrackingModifier>? configureModifier = null)
        {
            // The integration runner starts test bodies inside TestRuntimeRunner._Ready, where scene-tree
            // attachment does not propagate until the first process frame.
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalFingerFixture",
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

            Skeleton3D skeleton = CreateFingerSkeleton(includeLeftRingDistal, asymmetricRest);
            rigHolder.AddChild(skeleton);

            OpticalFingerTrackingModifier modifier = new()
            {
                Name = "OpticalFingerTrackingModifier",
                CalibrationProfile = CreateIdentityCalibrationProfile(),
            };
            configureModifier?.Invoke(modifier);
            skeleton.AddChild(modifier);

            // The pose capture listens for the skeleton's post-modification update signal, observing the
            // effective finger output of the whole pipeline.
            var poseCapture = SkeletonPoseCapture.Attach(skeleton);

            Node3D originHolder = new()
            {
                Name = "OriginHolder",
            };
            root.AddChild(originHolder);

            MockXRRuntimeNode runtime = LoadPackedScene(MockRuntimeScenePath).Instantiate<MockXRRuntimeNode>();
            originHolder.AddChild(runtime);
            _ = runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
            xrManager.SetRuntime(runtime);

            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);

            Dictionary<string, int> boneIndices = [];
            for (int boneIndex = 0; boneIndex < skeleton.GetBoneCount(); boneIndex++)
            {
                boneIndices[skeleton.GetBoneName(boneIndex)] = boneIndex;
            }

            Assert.True(poseCapture.CaptureCount > 0, "Expected the finger pose capture to run at least once.");
            if (expectTopologyValid)
            {
                Assert.True(
                    modifier.IsFingerTopologyValid,
                    "The synthetic fixture must satisfy the production bilateral rest-geometry binding gates.");
            }

            return new FingerFixture(root, runtime, skeleton, modifier, poseCapture, boneIndices);
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

        private static Skeleton3D CreateFingerSkeleton(bool includeLeftRingDistal, bool asymmetricRest)
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
                Basis handBasis = asymmetricRest
                    ? Basis.Identity.Rotated(Vector3.Forward, side == LimbSide.Left ? 0.11f : -0.08f)
                    : Basis.Identity;
                int hand = AddBone(
                    skeleton,
                    prefix + "Hand",
                    lowerArm,
                    new Transform3D(handBasis, new Vector3(0.0f, 0.2050707f, 0.0f)));

                for (int chainIndex = 0; chainIndex < fingerChains.Length; chainIndex += 3)
                {
                    int parent = hand;
                    int nonThumbChain = (chainIndex / 3) - 1;
                    for (int depth = 0; depth < 3; depth++)
                    {
                        string chainBone = fingerChains[chainIndex + depth];

                        // Deliberately omit one required bone for the missing-topology fixture.
                        if (!includeLeftRingDistal && side == LimbSide.Left && chainBone == "RingDistal")
                        {
                            continue;
                        }

                        // The default rest carries a natural palmward bend and a root spread so the binding-time
                        // anatomical frame consensus gates (XR-002 TR19) pass with margin: root span ratio is
                        // about 1.5, natural bends about 9 degrees, and the four curvature directions cluster.
                        // The thumb chain keeps a dedicated rest — retained unchanged so the fixtures keep
                        // exercising the scale-tolerant thumb rest-basis extraction — while the authored
                        // thumb binding (XR-002 TR25-TR28) consumes the rest bases for the neutrals and the
                        // thumb-proximal rest origin for the frame longitudinal, never segment-centre
                        // geometry.
                        Transform3D rest = nonThumbChain < 0
                            ? CreateThumbRest(side, depth)
                            : asymmetricRest
                                ? CreateAsymmetricFingerRest(side, chainIndex, depth)
                                : CreateNaturalFingerRest(side, nonThumbChain, depth);
                        parent = AddBone(skeleton, prefix + chainBone, parent, rest);
                    }
                }
            }

            skeleton.ResetBonePoses();
            return skeleton;
        }

        /// <summary>
        /// Natural-bend finger rest with the reference rig's mirrored root geometry: the Reset palm plane and
        /// thumb frame therefore satisfy the strict bilateral palm-alignment binding gates, while each deeper
        /// segment retains a shared natural palmward bend.
        /// </summary>
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
                1 => new Transform3D(
                    Basis.Identity,
                    new Vector3(0.001f * handedness, -0.038f, 0.010f)),
                _ => new Transform3D(
                    Basis.Identity,
                    new Vector3(0.0f, -0.030f, 0.013f)),
            };
        }

        /// <summary>
        /// Thumb-chain rest mirrors the reference Reset geometry in both sides. In particular, the proximal
        /// offset is the strict thumb-frame longitudinal input and must align with the mirrored palm normals;
        /// its previous synthetic downward direction failed the production 0.8 palm-alignment gate.
        /// </summary>
        private static Transform3D CreateThumbRest(LimbSide side, int depth)
        {
            float handedness = side == LimbSide.Left ? -1.0f : 1.0f;
            return depth switch
            {
                0 => new Transform3D(
                    Basis.Identity,
                    new Vector3(-0.055f * handedness, -0.002f, -0.015f)),
                1 => new Transform3D(
                    Basis.Identity,
                    new Vector3(0.0201366f * handedness, 0.0177871f, -0.0114732f)),
                _ => new Transform3D(
                    Basis.Identity,
                    new Vector3(0.00326f * handedness, -0.02933f, 0.01238f)),
            };
        }

        private static Transform3D CreateAsymmetricFingerRest(LimbSide side, int chainIndex, int depth)
        {
            int nonThumbChain = (chainIndex / 3) - 1;
            float handedness = side == LimbSide.Left ? 1.0f : -1.0f;
            Vector3[] leftRootOrigins =
            [
                new(-0.036526f, 0.094007f, 0.010289f),
                new(-0.011721f, 0.096455f, 0.006210f),
                new(0.007185f, 0.093367f, 0.005249f),
                new(0.025564f, 0.086033f, 0.009411f),
            ];
            float[] splay = [-0.38f, 0.09f, 0.31f, 0.52f];
            float[] roll = [0.17f, -0.12f, 0.21f, -0.19f];
            Vector3 leftRoot = leftRootOrigins[nonThumbChain];
            Vector3 root = side == LimbSide.Left
                ? leftRoot
                : new Vector3(-leftRoot.X, leftRoot.Y, leftRoot.Z);

            return depth switch
            {
                0 => new Transform3D(
                    Basis.Identity
                        .Rotated(Vector3.Forward, handedness * splay[nonThumbChain])
                        .Rotated(Vector3.Down, roll[nonThumbChain]),
                    // The root origins retain the reference palm plane and exact bilateral mirrors. Only the
                    // non-root bases and segments are asymmetric, so the test still exercises chain neutral
                    // swings without invalidating the thumb binding precondition.
                    root),
                1 => new Transform3D(
                    Basis.Identity
                        .Rotated(Vector3.Right, 0.18f + (0.04f * nonThumbChain))
                        .Rotated(Vector3.Down, handedness * (0.05f + (0.02f * nonThumbChain))),
                    new Vector3(0.003f * handedness, -0.044f - (0.003f * nonThumbChain), 0.009f)),
                2 => new Transform3D(
                    Basis.Identity
                        .Rotated(Vector3.Right, 0.24f + (0.03f * nonThumbChain))
                        .Rotated(Vector3.Back, handedness * (0.04f + (0.015f * nonThumbChain))),
                    new Vector3(-0.002f * handedness, -0.033f - (0.002f * nonThumbChain), 0.011f)),
                _ => throw new ArgumentOutOfRangeException(nameof(depth)),
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
    }

    /// <summary>
    /// Mock runtime fixture without a skeleton, used for real player-scene tests where the modifier resolves
    /// the test XR manager service.
    /// </summary>
    private sealed class MockRuntimeFixture : IDisposable
    {
        private MockRuntimeFixture(TestGame root, MockXRRuntimeNode runtime)
        {
            Root = root;
            Runtime = runtime;
        }

        public TestGame Root
        {
            get;
        }

        public MockXRRuntimeNode Runtime
        {
            get;
        }

        public static async Task<MockRuntimeFixture> CreateAsync(SceneTree sceneTree)
        {
            await WaitForNextFrameAsync(sceneTree);

            TestGame root = new()
            {
                Name = "OpticalFingerRuntimeFixture",
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

            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 2);

            return new MockRuntimeFixture(root, runtime);
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


    /// <summary>
    /// Captures the effective per-frame bone poses by listening for the skeleton's <c>skeleton_updated</c>
    /// signal, which fires once all <see cref="SkeletonModifier3D" /> processing is complete and before the
    /// engine restores the authored local pose. Skeleton modifier writes are per-frame overlays, so this is
    /// the only point where the effective pipeline output is readable.
    /// </summary>
    internal sealed class SkeletonPoseCapture
    {
        private readonly Skeleton3D _skeleton;

        private readonly Callable _callable;

        private SkeletonPoseCapture(Skeleton3D skeleton)
        {
            _skeleton = skeleton;
            Rotations = new Quaternion[skeleton.GetBoneCount()];
            Positions = new Vector3[skeleton.GetBoneCount()];
            Scales = new Vector3[skeleton.GetBoneCount()];
            GlobalPoses = new Transform3D[skeleton.GetBoneCount()];
            _callable = Callable.From(Capture);
            _ = skeleton.Connect(Skeleton3D.SignalName.SkeletonUpdated, _callable);
        }

        public Quaternion[] Rotations
        {
            get;
        }

        public Vector3[] Positions
        {
            get;
        }

        public Vector3[] Scales
        {
            get;
        }

        public Transform3D[] GlobalPoses
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

        public Quaternion FingerRotation(string boneName, IReadOnlyDictionary<string, int> boneIndices)
            => Rotations[boneIndices[boneName]];

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
                Positions[boneIndex] = _skeleton.GetBonePosePosition(boneIndex);
                Scales[boneIndex] = _skeleton.GetBonePoseScale(boneIndex);
                GlobalPoses[boneIndex] = _skeleton.GetBoneGlobalPose(boneIndex);
            }

            CaptureCount++;
        }
    }
}
