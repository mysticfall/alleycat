using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for IK-003 knee pole and knee positioning behaviour.
/// </summary>
public sealed class LegFeetIKIntegrationTests
{
    private const string VerificationScenePath = "res://tests/characters/ik/leg_feet_ik_test.tscn";

    private const string LeftFootTargetPath = "Markers/LeftFootTarget";
    private const string RightFootTargetPath = "Markers/RightFootTarget";
    private const string LeftPoleTargetPath = "Markers/LeftKneePoleTarget";
    private const string RightPoleTargetPath = "Markers/RightKneePoleTarget";
    private const string LeftLegIKControllerPath = "Subject/Female/Female_export/GeneralSkeleton/LeftLegIKController";
    private const string RightLegIKControllerPath = "Subject/Female/Female_export/GeneralSkeleton/RightLegIKController";

    private const string FootPoseMarkersPath = "Markers/FootTargetPoses";
    private const string HipsPoseMarkersPath = "Markers/HipsOverridePoses";

    private const string HipsHarnessPath = "Subject/Female/Female_export/GeneralSkeleton/HipsOverrideHarness";

    private const int SettleSkeletonUpdates = 4;

    private const float MinimumPoleContinuityDot = 0.2f;
    private const float MinimumKneeContinuityDot = 0.2f;
    private const float MinimumPoleKneeAlignmentDot = 0.2f;
    private const float MaximumHeldPosePoleDriftRadians = 0.03f;
    private const float MaximumHeldPoseKneeDriftRadians = 0.05f;
    private const float MinimumKneeSideConsistencyDot = 0.01f;
    private const float MinimumCornerForwardPreferenceDot = 0.01f;
    private const float MinimumCornerPlaneChangeRadians = 0.12f;
    private const float MinimumLeftLegUpKneeOffsetRatio = 0.035f;
    private const float MinimumLeftLegUpKneeFlexionRadians = 0.20f;
    private const float TargetTransformPositionToleranceMetres = 0.001f;
    private const float TargetTransformRotationToleranceRadians = 0.01f;
    private const float PoleOffsetFloorToleranceMetres = 0.001f;

    /// <summary>
    /// Verifies pole continuity, side consistency, corner-case interpolation response, and hips-pose stability.
    /// </summary>
    [Headless]
    [Fact]
    public async Task LegFeetIk_VerificationScene_KneePoleBehaviourMatchesContracts()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node3D leftFootTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath));
        Node3D rightFootTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(RightFootTargetPath));
        Node3D leftPoleTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(LeftPoleTargetPath));
        Node3D rightPoleTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(RightPoleTargetPath));

        Node3D footTargetPoses = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(FootPoseMarkersPath));
        Node3D hipsPoseMarkers = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(HipsPoseMarkersPath));

        Node3D leftNeutral = RequireNode3D(footTargetPoses, "LeftNeutral");
        Node3D rightNeutral = RequireNode3D(footTargetPoses, "RightNeutral");
        Node3D leftOutward = RequireNode3D(footTargetPoses, "LeftOutward");
        Node3D rightOutward = RequireNode3D(footTargetPoses, "RightOutward");
        Node3D leftInward = RequireNode3D(footTargetPoses, "LeftInward");
        Node3D rightInward = RequireNode3D(footTargetPoses, "RightInward");
        Node3D leftRaised = RequireNode3D(footTargetPoses, "LeftRaised");
        Node3D leftForward = RequireNode3D(footTargetPoses, "LeftForward");

        Node3D hipsNeutral = RequireNode3D(hipsPoseMarkers, "HipsNeutral");
        Node3D hipsCrouch = RequireNode3D(hipsPoseMarkers, "HipsCrouch");
        Node3D hipsLegUpLeft = RequireNode3D(hipsPoseMarkers, "HipsLegUpLeft");
        Node3D hipsAsym = RequireNode3D(hipsPoseMarkers, "HipsAsym");

        CopyTransformModifier3D hipsModifier = Assert.IsAssignableFrom<CopyTransformModifier3D>(sceneRoot.GetNodeOrNull(HipsHarnessPath));

        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in verification scene.");

        int hipsIdx = RequireBone(skeleton, "Hips");
        int leftUpperLegIdx = RequireBone(skeleton, "LeftUpperLeg");
        int rightUpperLegIdx = RequireBone(skeleton, "RightUpperLeg");
        int leftLowerLegIdx = RequireBone(skeleton, "LeftLowerLeg");
        int rightLowerLegIdx = RequireBone(skeleton, "RightLowerLeg");

        // 1) Neutral baseline and side consistency.
        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftFootTarget,
            rightFootTarget,
            leftNeutral,
            rightNeutral,
            hipsModifier,
            hipsNeutral);

        AssertKneeOutwardConsistency(
            skeleton,
            hipsIdx,
            leftUpperLegIdx,
            leftLowerLegIdx,
            "Left knee should remain on the left side in neutral pose.");
        AssertKneeOutwardConsistency(
            skeleton,
            hipsIdx,
            rightUpperLegIdx,
            rightLowerLegIdx,
            "Right knee should remain on the right side in neutral pose.");

        AssertPoleForwardOfLeg(
            skeleton,
            leftPoleTarget,
            leftFootTarget,
            leftUpperLegIdx,
            "Left pole target should be in front of the left leg in neutral pose.");
        AssertPoleForwardOfLeg(
            skeleton,
            rightPoleTarget,
            rightFootTarget,
            rightUpperLegIdx,
            "Right pole target should be in front of the right leg in neutral pose.");

        Vector3 neutralLeftPole = ComputePoleDirection(skeleton, leftPoleTarget, leftFootTarget, leftUpperLegIdx);

        // 2) Pole and knee continuity under outward <-> inward interpolation.
        Vector3 previousLeftPoleDirection = Vector3.Zero;
        Vector3 previousRightPoleDirection = Vector3.Zero;
        Vector3 previousLeftKneeDirection = Vector3.Zero;
        Vector3 previousRightKneeDirection = Vector3.Zero;

        const int interpolationSteps = 24;
        for (int step = 0; step <= interpolationSteps; step++)
        {
            float t = step / (float)interpolationSteps;
            leftFootTarget.GlobalTransform = InterpolateTransform(leftOutward.GlobalTransform, leftInward.GlobalTransform, t);
            rightFootTarget.GlobalTransform = InterpolateTransform(rightOutward.GlobalTransform, rightInward.GlobalTransform, t);

            await WaitForSkeletonUpdatesAsync(sceneTree, skeleton, 2);

            Vector3 leftPoleDirection = ComputePoleDirection(skeleton, leftPoleTarget, leftFootTarget, leftUpperLegIdx);
            Vector3 rightPoleDirection = ComputePoleDirection(skeleton, rightPoleTarget, rightFootTarget, rightUpperLegIdx);

            Vector3 leftKneeDirection = ComputeKneeBendDirection(skeleton, leftFootTarget, leftUpperLegIdx, leftLowerLegIdx);
            Vector3 rightKneeDirection = ComputeKneeBendDirection(skeleton, rightFootTarget, rightUpperLegIdx, rightLowerLegIdx);

            Assert.True(
                leftKneeDirection.Dot(leftPoleDirection) >= MinimumPoleKneeAlignmentDot,
                "Left knee bend direction should remain aligned with left pole direction during interpolation.");
            Assert.True(
                rightKneeDirection.Dot(rightPoleDirection) >= MinimumPoleKneeAlignmentDot,
                "Right knee bend direction should remain aligned with right pole direction during interpolation.");

            if (step > 0)
            {
                float leftPoleContinuity = previousLeftPoleDirection.Dot(leftPoleDirection);
                float rightPoleContinuity = previousRightPoleDirection.Dot(rightPoleDirection);
                float leftKneeContinuity = previousLeftKneeDirection.Dot(leftKneeDirection);
                float rightKneeContinuity = previousRightKneeDirection.Dot(rightKneeDirection);

                Assert.True(
                    leftPoleContinuity >= MinimumPoleContinuityDot,
                    "Left pole direction should remain continuous without flips during foot rotation interpolation. " +
                    $"Step: {step}, continuity dot: {leftPoleContinuity:F4}, minimum: {MinimumPoleContinuityDot:F4}.");
                Assert.True(
                    rightPoleContinuity >= MinimumPoleContinuityDot,
                    "Right pole direction should remain continuous without flips during foot rotation interpolation. " +
                    $"Step: {step}, continuity dot: {rightPoleContinuity:F4}, minimum: {MinimumPoleContinuityDot:F4}.");
                Assert.True(
                    leftKneeContinuity >= MinimumKneeContinuityDot,
                    "Left knee bend direction should remain continuous without flips during foot rotation interpolation. " +
                    $"Step: {step}, continuity dot: {leftKneeContinuity:F4}, minimum: {MinimumKneeContinuityDot:F4}.");
                Assert.True(
                    rightKneeContinuity >= MinimumKneeContinuityDot,
                    "Right knee bend direction should remain continuous without flips during foot rotation interpolation. " +
                    $"Step: {step}, continuity dot: {rightKneeContinuity:F4}, minimum: {MinimumKneeContinuityDot:F4}.");
            }

            previousLeftPoleDirection = leftPoleDirection;
            previousRightPoleDirection = rightPoleDirection;
            previousLeftKneeDirection = leftKneeDirection;
            previousRightKneeDirection = rightKneeDirection;
        }

        // 3) Left-leg-up corner case should favour forward-plane response over up-axis response.
        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftFootTarget,
            rightFootTarget,
            leftRaised,
            rightNeutral,
            hipsModifier,
            hipsLegUpLeft);

        Vector3 leftLegDirection = ComputeLegDirection(skeleton, leftFootTarget, leftUpperLegIdx);
        Vector3 leftLegUpPole = ComputePoleDirection(skeleton, leftPoleTarget, leftFootTarget, leftUpperLegIdx);
        Vector3 leftLegUpKnee = ComputeKneeBendDirection(skeleton, leftFootTarget, leftUpperLegIdx, leftLowerLegIdx);

        ResolveCornerCaseOracleAxes(leftRaised, out Vector3 leftFootForwardAxis, out Vector3 leftFootUpAxis);

        bool hasProjectedForward = TryProjectAndNormalise(leftFootForwardAxis, leftLegDirection, out Vector3 forwardProjected);
        bool hasProjectedUp = TryProjectAndNormalise(leftFootUpAxis, leftLegDirection, out Vector3 upProjected);
        Assert.True(hasProjectedForward, "Expected projected foot-forward axis for left-leg-up corner case.");
        Assert.True(hasProjectedUp, "Expected projected foot-up axis for left-leg-up corner case.");

        float poleForwardPreference = Mathf.Abs(leftLegUpPole.Dot(forwardProjected)) - Mathf.Abs(leftLegUpPole.Dot(upProjected));
        Assert.True(
            poleForwardPreference >= MinimumCornerForwardPreferenceDot,
            "Left-leg-up pole should favour forward-plane response over up-axis response. " +
            $"Preference dot delta: {poleForwardPreference:F4}, minimum: {MinimumCornerForwardPreferenceDot:F4}.");

        float kneeForwardPreference = Mathf.Abs(leftLegUpKnee.Dot(forwardProjected)) - Mathf.Abs(leftLegUpKnee.Dot(upProjected));
        Assert.True(
            kneeForwardPreference >= 0.0f,
            "Left-leg-up knee bend direction should not regress towards up-axis preference in corner case. " +
            $"Preference dot delta: {kneeForwardPreference:F4}.");

        float cornerResponseAngle = VectorAngle(neutralLeftPole, leftLegUpPole);
        Assert.True(
            cornerResponseAngle >= MinimumCornerPlaneChangeRadians,
            "Left-leg-up pose should produce a meaningful knee-plane response from neutral. " +
            $"Observed angle: {cornerResponseAngle:F4} rad, minimum: {MinimumCornerPlaneChangeRadians:F4} rad.");

        Assert.True(
            leftLegUpKnee.Dot(leftLegUpPole) >= MinimumPoleKneeAlignmentDot,
            "Left knee bend should remain aligned with left pole direction in left-leg-up corner case.");

        AssertLeftLegUpKneePlausibility(
            skeleton,
            leftUpperLegIdx,
            leftLowerLegIdx,
            leftFootTarget,
            leftLegUpKnee,
            leftLegUpPole);

        // 4) Stability while holding crouch and asym hips-override poses.
        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftFootTarget,
            rightFootTarget,
            leftNeutral,
            rightNeutral,
            hipsModifier,
            hipsCrouch);
        await AssertHeldPoseStabilityAsync(
            sceneTree,
            skeleton,
            leftPoleTarget,
            rightPoleTarget,
            leftFootTarget,
            rightFootTarget,
            leftUpperLegIdx,
            rightUpperLegIdx,
            leftLowerLegIdx,
            rightLowerLegIdx);

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftFootTarget,
            rightFootTarget,
            leftForward,
            rightInward,
            hipsModifier,
            hipsAsym);
        await AssertHeldPoseStabilityAsync(
            sceneTree,
            skeleton,
            leftPoleTarget,
            rightPoleTarget,
            leftFootTarget,
            rightFootTarget,
            leftUpperLegIdx,
            rightUpperLegIdx,
            leftLowerLegIdx,
            rightLowerLegIdx);

        // 5) Minimal guard: foot targets remain read-only inputs over runtime updates.
        Vector3 leftTargetPositionBefore = leftFootTarget.GlobalPosition;
        Vector3 rightTargetPositionBefore = rightFootTarget.GlobalPosition;
        Quaternion leftTargetRotationBefore = leftFootTarget.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion();
        Quaternion rightTargetRotationBefore = rightFootTarget.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion();

        await WaitForSkeletonUpdatesAsync(sceneTree, skeleton, SettleSkeletonUpdates);

        Assert.True(
            leftFootTarget.GlobalPosition.DistanceTo(leftTargetPositionBefore) <= TargetTransformPositionToleranceMetres,
            "LeftFootTarget should remain at caller-specified position across IK updates.");
        Assert.True(
            rightFootTarget.GlobalPosition.DistanceTo(rightTargetPositionBefore) <= TargetTransformPositionToleranceMetres,
            "RightFootTarget should remain at caller-specified position across IK updates.");

        float leftRotationDelta = QuaternionAngle(
            leftTargetRotationBefore,
            leftFootTarget.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion());
        float rightRotationDelta = QuaternionAngle(
            rightTargetRotationBefore,
            rightFootTarget.GlobalTransform.Basis.Orthonormalized().GetRotationQuaternion());

        Assert.True(
            leftRotationDelta <= TargetTransformRotationToleranceRadians,
            "LeftFootTarget rotation should remain unchanged by leg IK updates.");
        Assert.True(
            rightRotationDelta <= TargetTransformRotationToleranceRadians,
            "RightFootTarget rotation should remain unchanged by leg IK updates.");
    }

    /// <summary>
    /// Verifies compressed crouch poses enforce the rest-leg-derived pole-offset floor.
    /// </summary>
    [Headless]
    [Fact]
    public async Task LegFeetIk_CrouchPose_EnforcesRestLegPoleOffsetFloor()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node3D leftFootTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath));
        Node3D rightFootTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(RightFootTargetPath));
        Node3D leftPoleTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(LeftPoleTargetPath));
        Node3D rightPoleTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(RightPoleTargetPath));
        SkeletonModifier3D leftLegIKController = Assert.IsAssignableFrom<SkeletonModifier3D>(sceneRoot.GetNodeOrNull(LeftLegIKControllerPath));
        SkeletonModifier3D rightLegIKController = Assert.IsAssignableFrom<SkeletonModifier3D>(sceneRoot.GetNodeOrNull(RightLegIKControllerPath));

        Node3D footTargetPoses = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(FootPoseMarkersPath));
        Node3D hipsPoseMarkers = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(HipsPoseMarkersPath));
        Node3D leftNeutral = RequireNode3D(footTargetPoses, "LeftNeutral");
        Node3D rightNeutral = RequireNode3D(footTargetPoses, "RightNeutral");
        Node3D hipsCrouch = RequireNode3D(hipsPoseMarkers, "HipsCrouch");

        CopyTransformModifier3D hipsModifier = Assert.IsAssignableFrom<CopyTransformModifier3D>(sceneRoot.GetNodeOrNull(HipsHarnessPath));
        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in verification scene.");

        int leftUpperLegIdx = RequireBone(skeleton, "LeftUpperLeg");
        int rightUpperLegIdx = RequireBone(skeleton, "RightUpperLeg");
        int leftLowerLegIdx = RequireBone(skeleton, "LeftLowerLeg");
        int rightLowerLegIdx = RequireBone(skeleton, "RightLowerLeg");
        int leftFootIdx = RequireBone(skeleton, "LeftFoot");
        int rightFootIdx = RequireBone(skeleton, "RightFoot");

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftFootTarget,
            rightFootTarget,
            leftNeutral,
            rightNeutral,
            hipsModifier,
            hipsCrouch);

        AssertPoleOffsetFloorEnforced(
            skeleton,
            leftLegIKController,
            leftPoleTarget,
            leftFootTarget,
            leftUpperLegIdx,
            leftLowerLegIdx,
            leftFootIdx,
            "Left crouch pole should respect the rest-leg-derived minimum offset floor.");
        AssertPoleOffsetFloorEnforced(
            skeleton,
            rightLegIKController,
            rightPoleTarget,
            rightFootTarget,
            rightUpperLegIdx,
            rightLowerLegIdx,
            rightFootIdx,
            "Right crouch pole should respect the rest-leg-derived minimum offset floor.");
    }

    private static async Task ApplyPoseAndSettleAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        Node3D leftFootTarget,
        Node3D rightFootTarget,
        Node3D leftPoseMarker,
        Node3D rightPoseMarker,
        CopyTransformModifier3D hipsModifier,
        Node3D hipsPoseMarker)
    {
        leftFootTarget.GlobalTransform = leftPoseMarker.GlobalTransform;
        rightFootTarget.GlobalTransform = rightPoseMarker.GlobalTransform;
        hipsModifier.Set("settings/0/reference_node", hipsPoseMarker.GetPath());

        await WaitForSkeletonUpdatesAsync(sceneTree, skeleton, SettleSkeletonUpdates);
    }

    private static async Task WaitForSkeletonUpdatesAsync(SceneTree sceneTree, Skeleton3D skeleton, int count)
    {
        for (int update = 0; update < count; update++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        }
    }

    private static async Task AssertHeldPoseStabilityAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        Node3D leftPoleTarget,
        Node3D rightPoleTarget,
        Node3D leftFootTarget,
        Node3D rightFootTarget,
        int leftUpperLegIdx,
        int rightUpperLegIdx,
        int leftLowerLegIdx,
        int rightLowerLegIdx)
    {
        Vector3 baselineLeftPole = ComputePoleDirection(skeleton, leftPoleTarget, leftFootTarget, leftUpperLegIdx);
        Vector3 baselineRightPole = ComputePoleDirection(skeleton, rightPoleTarget, rightFootTarget, rightUpperLegIdx);
        Vector3 baselineLeftKnee = ComputeKneeBendDirection(skeleton, leftFootTarget, leftUpperLegIdx, leftLowerLegIdx);
        Vector3 baselineRightKnee = ComputeKneeBendDirection(skeleton, rightFootTarget, rightUpperLegIdx, rightLowerLegIdx);

        for (int sample = 0; sample < 10; sample++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

            Vector3 leftPoleSample = ComputePoleDirection(skeleton, leftPoleTarget, leftFootTarget, leftUpperLegIdx);
            Vector3 rightPoleSample = ComputePoleDirection(skeleton, rightPoleTarget, rightFootTarget, rightUpperLegIdx);
            Vector3 leftKneeSample = ComputeKneeBendDirection(skeleton, leftFootTarget, leftUpperLegIdx, leftLowerLegIdx);
            Vector3 rightKneeSample = ComputeKneeBendDirection(skeleton, rightFootTarget, rightUpperLegIdx, rightLowerLegIdx);

            float leftPoleDrift = VectorAngle(baselineLeftPole, leftPoleSample);
            float rightPoleDrift = VectorAngle(baselineRightPole, rightPoleSample);
            float leftKneeDrift = VectorAngle(baselineLeftKnee, leftKneeSample);
            float rightKneeDrift = VectorAngle(baselineRightKnee, rightKneeSample);

            Assert.True(
                leftPoleDrift <= MaximumHeldPosePoleDriftRadians,
                "Left pole direction should remain stable while pose is held. " +
                $"Sample: {sample + 1}, drift: {leftPoleDrift:F6} rad, maximum: {MaximumHeldPosePoleDriftRadians:F6} rad.");
            Assert.True(
                rightPoleDrift <= MaximumHeldPosePoleDriftRadians,
                "Right pole direction should remain stable while pose is held. " +
                $"Sample: {sample + 1}, drift: {rightPoleDrift:F6} rad, maximum: {MaximumHeldPosePoleDriftRadians:F6} rad.");
            Assert.True(
                leftKneeDrift <= MaximumHeldPoseKneeDriftRadians,
                "Left knee bend direction should remain stable while pose is held. " +
                $"Sample: {sample + 1}, drift: {leftKneeDrift:F6} rad, maximum: {MaximumHeldPoseKneeDriftRadians:F6} rad.");
            Assert.True(
                rightKneeDrift <= MaximumHeldPoseKneeDriftRadians,
                "Right knee bend direction should remain stable while pose is held. " +
                $"Sample: {sample + 1}, drift: {rightKneeDrift:F6} rad, maximum: {MaximumHeldPoseKneeDriftRadians:F6} rad.");

            Assert.True(
                leftKneeSample.Dot(leftPoleSample) >= MinimumPoleKneeAlignmentDot,
                "Left knee bend direction should remain aligned with pole direction while holding pose.");
            Assert.True(
                rightKneeSample.Dot(rightPoleSample) >= MinimumPoleKneeAlignmentDot,
                "Right knee bend direction should remain aligned with pole direction while holding pose.");
        }
    }

    private static Transform3D InterpolateTransform(Transform3D from, Transform3D to, float t)
    {
        Quaternion fromQ = from.Basis.GetRotationQuaternion().Normalized();
        Quaternion toQ = to.Basis.GetRotationQuaternion().Normalized();

        Basis basis = new Basis(fromQ.Slerp(toQ, t)).Orthonormalized();
        Vector3 origin = from.Origin.Lerp(to.Origin, t);

        return new Transform3D(basis, origin);
    }

    private static Vector3 ComputePoleDirection(
        Skeleton3D skeleton,
        Node3D poleTarget,
        Node3D footTarget,
        int upperLegBoneIndex)
    {
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 midpoint = (upperLegPosition + footTarget.GlobalPosition) * 0.5f;
        return (poleTarget.GlobalPosition - midpoint).Normalized();
    }

    private static Vector3 ComputeKneeBendDirection(
        Skeleton3D skeleton,
        Node3D footTarget,
        int upperLegBoneIndex,
        int lowerLegBoneIndex)
    {
        Vector3 legDirection = ComputeLegDirection(skeleton, footTarget, upperLegBoneIndex);
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 lowerLegPosition = BoneWorldPosition(skeleton, lowerLegBoneIndex);

        return TryProjectAndNormalise(lowerLegPosition - upperLegPosition, legDirection, out Vector3 bendDirection)
            ? bendDirection
            : throw new Xunit.Sdk.XunitException("Expected non-degenerate knee bend direction for current pose.");
    }

    private static Vector3 ComputeLegDirection(Skeleton3D skeleton, Node3D footTarget, int upperLegBoneIndex)
    {
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 legDirection = (footTarget.GlobalPosition - upperLegPosition).Normalized();

        return legDirection.LengthSquared() > 1e-6f
            ? legDirection
            : throw new Xunit.Sdk.XunitException("Expected non-degenerate leg direction for current pose.");
    }

    private static void AssertKneeOutwardConsistency(
        Skeleton3D skeleton,
        int hipsBoneIndex,
        int upperLegBoneIndex,
        int lowerLegBoneIndex,
        string message)
    {
        Vector3 hipsPosition = BoneWorldPosition(skeleton, hipsBoneIndex);
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 lowerLegPosition = BoneWorldPosition(skeleton, lowerLegBoneIndex);

        Vector3 kneeVector = lowerLegPosition - upperLegPosition;
        Vector3 hipsToLeg = upperLegPosition - hipsPosition;

        if (hipsToLeg.LengthSquared() <= 1e-6f || kneeVector.LengthSquared() <= 1e-6f)
        {
            throw new Xunit.Sdk.XunitException("Expected non-degenerate vectors for knee side consistency check.");
        }

        float sideOffset = kneeVector.Normalized().Dot(hipsToLeg.Normalized());

        Assert.True(
            sideOffset >= MinimumKneeSideConsistencyDot,
            message + $" Side offset dot: {sideOffset:F4}.");
    }

    private static void AssertPoleForwardOfLeg(
        Skeleton3D skeleton,
        Node3D poleTarget,
        Node3D footTarget,
        int upperLegBoneIndex,
        string message)
    {
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 midpoint = (upperLegPosition + footTarget.GlobalPosition) * 0.5f;
        Vector3 midpointToPole = poleTarget.GlobalPosition - midpoint;

        Basis footBasis = footTarget.GlobalTransform.Basis.Orthonormalized();
        Vector3 footForward = footBasis.Column1.Normalized();

        float dot = midpointToPole.Dot(footForward);

        Assert.True(
            dot > 0.0f,
            message + $" Dot product of pole vector with foot forward: {dot:F4}.");
    }

    private static void ResolveCornerCaseOracleAxes(
        Node3D raisedPoseMarker,
        out Vector3 forwardAxis,
        out Vector3 upAxis)
    {
        Basis basis = raisedPoseMarker.GlobalTransform.Basis.Orthonormalized();
        forwardAxis = basis.Column2.Normalized();
        upAxis = basis.Column1.Normalized();

        if (forwardAxis.LengthSquared() <= 1e-6f || upAxis.LengthSquared() <= 1e-6f)
        {
            throw new Xunit.Sdk.XunitException("Expected non-degenerate raised-pose oracle foot axes.");
        }
    }

    private static void AssertLeftLegUpKneePlausibility(
        Skeleton3D skeleton,
        int upperLegBoneIndex,
        int lowerLegBoneIndex,
        Node3D footTarget,
        Vector3 leftLegUpKneeDirection,
        Vector3 leftLegUpPoleDirection)
    {
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 lowerLegPosition = BoneWorldPosition(skeleton, lowerLegBoneIndex);
        Vector3 footPosition = footTarget.GlobalPosition;

        Vector3 upperToLower = lowerLegPosition - upperLegPosition;
        Vector3 lowerToFoot = footPosition - lowerLegPosition;
        Vector3 upperToFoot = footPosition - upperLegPosition;

        if (upperToLower.LengthSquared() <= 1e-6f
            || lowerToFoot.LengthSquared() <= 1e-6f
            || upperToFoot.LengthSquared() <= 1e-6f)
        {
            throw new Xunit.Sdk.XunitException("Expected non-degenerate left-leg-up vectors for anatomical plausibility guard.");
        }

        float interiorKneeAngle = VectorAngle(-upperToLower, lowerToFoot);
        float kneeFlexion = Mathf.Pi - interiorKneeAngle;

        Assert.True(
            kneeFlexion >= MinimumLeftLegUpKneeFlexionRadians,
            "Left-leg-up knee should remain meaningfully flexed to prevent anatomically implausible near-straight solves. " +
            $"Observed flexion: {kneeFlexion:F4} rad, minimum: {MinimumLeftLegUpKneeFlexionRadians:F4} rad.");

        float kneeOffsetFromLegAxis = DistancePointToLine(lowerLegPosition, upperLegPosition, footPosition);
        float kneeOffsetRatio = kneeOffsetFromLegAxis / upperToFoot.Length();

        Assert.True(
            kneeOffsetRatio >= MinimumLeftLegUpKneeOffsetRatio,
            "Left-leg-up knee should keep a measurable lateral offset from the hip-to-foot axis; collapsed offsets indicate implausible anatomy. " +
            $"Observed ratio: {kneeOffsetRatio:F4}, minimum: {MinimumLeftLegUpKneeOffsetRatio:F4}.");

        Assert.True(
            leftLegUpKneeDirection.Dot(leftLegUpPoleDirection) >= MinimumPoleKneeAlignmentDot,
            "Left-leg-up anatomical guard expects knee bend direction to remain consistent with the computed pole plane.");
    }

    private static bool TryProjectAndNormalise(Vector3 vector, Vector3 normal, out Vector3 projected)
    {
        projected = vector - (vector.Dot(normal) * normal);

        if (projected.LengthSquared() <= 1e-6f)
        {
            projected = Vector3.Zero;
            return false;
        }

        projected = projected.Normalized();
        return true;
    }

    private static float VectorAngle(Vector3 a, Vector3 b)
    {
        float dot = Mathf.Clamp(a.Normalized().Dot(b.Normalized()), -1f, 1f);
        return Mathf.Acos(dot);
    }

    private static float QuaternionAngle(Quaternion from, Quaternion to)
    {
        float dot = Mathf.Clamp(Mathf.Abs(from.Normalized().Dot(to.Normalized())), -1f, 1f);
        return 2f * Mathf.Acos(dot);
    }

    private static float DistancePointToLine(Vector3 point, Vector3 lineStart, Vector3 lineEnd)
    {
        Vector3 line = lineEnd - lineStart;
        if (line.LengthSquared() <= 1e-6f)
        {
            throw new Xunit.Sdk.XunitException("Expected non-degenerate line for distance computation.");
        }

        float t = (point - lineStart).Dot(line) / line.LengthSquared();
        Vector3 nearest = lineStart + (line * t);
        return point.DistanceTo(nearest);
    }

    private static void AssertPoleOffsetFloorEnforced(
        Skeleton3D skeleton,
        SkeletonModifier3D controller,
        Node3D poleTarget,
        Node3D footTarget,
        int upperLegBoneIndex,
        int lowerLegBoneIndex,
        int footBoneIndex,
        string message)
    {
        float poleOffset = ComputePoleOffsetDistance(skeleton, poleTarget, footTarget, upperLegBoneIndex);
        float restLegLength = ComputeRestLegLength(skeleton, upperLegBoneIndex, lowerLegBoneIndex, footBoneIndex);
        float currentLegLength = ComputeCurrentLegLength(skeleton, footTarget, upperLegBoneIndex);
        float compressionRatioForPoleFloor = Mathf.Clamp(
            ReadFloatProperty(controller, "CompressionRatioForRestPoleFloor"),
            0.1f,
            1.0f);

        Assert.True(
            currentLegLength <= restLegLength * compressionRatioForPoleFloor,
            "Crouch floor assertion requires a compressed leg configuration, but pose was not compressed enough. " +
            $"Current length: {currentLegLength:F4}m, rest length: {restLegLength:F4}m, compression threshold: {restLegLength * compressionRatioForPoleFloor:F4}m.");

        float minimumPoleOffset = ReadFloatProperty(controller, "MinimumPoleOffset");
        float restLegHalfPoleOffsetMargin = ReadFloatProperty(controller, "RestLegHalfPoleOffsetMargin");
        float expectedFloor = Mathf.Max(minimumPoleOffset, (restLegLength * 0.5f) + restLegHalfPoleOffsetMargin);

        Assert.True(
            poleOffset + PoleOffsetFloorToleranceMetres >= expectedFloor,
            message +
            $" Observed offset: {poleOffset:F4}m, expected floor: {expectedFloor:F4}m, tolerance: {PoleOffsetFloorToleranceMetres:F4}m.");
    }

    private static float ReadFloatProperty(Node node, string propertyName)
    {
        Variant propertyValue = node.Get(propertyName);
        return propertyValue.VariantType == Variant.Type.Float
            ? (float)propertyValue.AsDouble()
            : throw new Xunit.Sdk.XunitException(
                $"Expected '{node.Name}' property '{propertyName}' to be a float, but got {propertyValue.VariantType}.");
    }

    private static float ComputePoleOffsetDistance(
        Skeleton3D skeleton,
        Node3D poleTarget,
        Node3D footTarget,
        int upperLegBoneIndex)
    {
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        Vector3 midpoint = (upperLegPosition + footTarget.GlobalPosition) * 0.5f;
        return poleTarget.GlobalPosition.DistanceTo(midpoint);
    }

    private static float ComputeCurrentLegLength(Skeleton3D skeleton, Node3D footTarget, int upperLegBoneIndex)
    {
        Vector3 upperLegPosition = BoneWorldPosition(skeleton, upperLegBoneIndex);
        return upperLegPosition.DistanceTo(footTarget.GlobalPosition);
    }

    private static float ComputeRestLegLength(
        Skeleton3D skeleton,
        int upperLegBoneIndex,
        int lowerLegBoneIndex,
        int footBoneIndex)
    {
        Vector3 upperLegRestPosition = skeleton.GetBoneGlobalRest(upperLegBoneIndex).Origin;
        Vector3 lowerLegRestPosition = skeleton.GetBoneGlobalRest(lowerLegBoneIndex).Origin;
        Vector3 footRestPosition = skeleton.GetBoneGlobalRest(footBoneIndex).Origin;

        return upperLegRestPosition.DistanceTo(lowerLegRestPosition)
            + lowerLegRestPosition.DistanceTo(footRestPosition);
    }

    private static Node3D RequireNode3D(Node parent, string childName) =>
        Assert.IsAssignableFrom<Node3D>(parent.GetNodeOrNull(childName));

    private static Skeleton3D? FindFirstSkeleton(Node root)
    {
        if (root is Skeleton3D skeleton)
        {
            return skeleton;
        }

        foreach (Node child in root.GetChildren())
        {
            Skeleton3D? found = FindFirstSkeleton(child);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static Vector3 BoneWorldPosition(Skeleton3D skeleton, int boneIndex) =>
        skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int index = skeleton.FindBone(boneName);

        return index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException($"Expected bone '{boneName}' to exist in skeleton.");
    }

}
