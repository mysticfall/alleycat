using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for shoulder look-at correction behaviour.
/// </summary>
public sealed class ArmShoulderCorrectionIntegrationTests
{
    private const string VerificationScenePath = "res://tests/ik/arm_shoulder_ik_test.tscn";
    private const string HandTargetPosesPath = "Markers/HandTargetPoses";
    private const string LeftHandTargetPath = "Markers/LeftHandTarget";
    private const string RightHandTargetPath = "Markers/RightHandTarget";
    private const string LeftArmControllerPath = "Subject/Female/Female/GeneralSkeleton/LeftArmIKController";
    private const string RightArmControllerPath = "Subject/Female/Female/GeneralSkeleton/RightArmIKController";

    private const int SettleSkeletonUpdates = 4;
    private const int DeterminismSamples = 5;
    private const float MaximumDeterminismErrorRadians = 0.003f;
    private const float MaximumForwardPoseSymmetryDifferenceRadians = 0.08f;
    private const float MinimumOverheadGainOverLoweredRadians = 0.05f;
    private const float MinimumForwardGainOverLoweredRadians = 0.01f;
    private const float MinimumOverheadGainOverForwardRadians = 0.01f;
    private const float MinimumShoulderWeightResponsivenessRadians = 0.03f;
    private const float MaximumNeutralOverrideErrorRadians = 0.18f;
    private const float MinimumRoundedBaselineReductionRatio = 0.5f;
    private const float MinimumAnimatedShoulderOriginDelta = 0.04f;

    private readonly record struct ArmRigData(
        string SideLabel,
        int ShoulderBoneIndex,
        Quaternion ShoulderRestLocalRotation);

    /// <summary>
    /// Verifies forward symmetry, overhead responsiveness, and deterministic output.
    /// </summary>
    [Fact]
    public async Task ArmShoulderCorrection_VerificationScene_MatchesCoreBehaviourContracts()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node3D handTargetPoses = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HandTargetPosesPath), exactMatch: false);
        Node3D leftHandTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftHandTargetPath), exactMatch: false);
        Node3D rightHandTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightHandTargetPath), exactMatch: false);
        Node leftController = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(LeftArmControllerPath), exactMatch: false);
        Node rightController = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(RightArmControllerPath), exactMatch: false);
        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in the verification scene.");

        ArmRigData leftArm = BuildArmRigData(skeleton, "Left");
        ArmRigData rightArm = BuildArmRigData(skeleton, "Right");

        Node3D leftLoweredMarker = RequirePoseMarker(handTargetPoses, "LeftLowered");
        Node3D rightLoweredMarker = RequirePoseMarker(handTargetPoses, "RightLowered");
        Node3D leftForwardMarker = RequirePoseMarker(handTargetPoses, "LeftForward");
        Node3D rightForwardMarker = RequirePoseMarker(handTargetPoses, "RightForward");
        Node3D leftOverheadMarker = RequirePoseMarker(handTargetPoses, "LeftOverhead");
        Node3D rightOverheadMarker = RequirePoseMarker(handTargetPoses, "RightOverhead");

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftHandTarget,
            rightHandTarget,
            leftLoweredMarker,
            rightLoweredMarker);
        float leftLoweredChange = ExtractShoulderChangeMagnitude(skeleton, leftArm);
        float rightLoweredChange = ExtractShoulderChangeMagnitude(skeleton, rightArm);
        await AssertShoulderDeterminismAsync(sceneTree, skeleton, leftArm, "lowered");
        await AssertShoulderDeterminismAsync(sceneTree, skeleton, rightArm, "lowered");

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftHandTarget,
            rightHandTarget,
            leftForwardMarker,
            rightForwardMarker);
        float leftForwardChange = ExtractShoulderChangeMagnitude(skeleton, leftArm);
        float rightForwardChange = ExtractShoulderChangeMagnitude(skeleton, rightArm);
        await AssertShoulderDeterminismAsync(sceneTree, skeleton, leftArm, "forward");
        await AssertShoulderDeterminismAsync(sceneTree, skeleton, rightArm, "forward");

        AssertForwardPoseSymmetry(leftForwardChange, rightForwardChange);

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftHandTarget,
            rightHandTarget,
            leftOverheadMarker,
            rightOverheadMarker);
        float leftOverheadChange = ExtractShoulderChangeMagnitude(skeleton, leftArm);
        float rightOverheadChange = ExtractShoulderChangeMagnitude(skeleton, rightArm);
        await AssertShoulderDeterminismAsync(sceneTree, skeleton, leftArm, "overhead");
        await AssertShoulderDeterminismAsync(sceneTree, skeleton, rightArm, "overhead");

        AssertOverheadIncreasesRelativeToLowered(
            "left",
            leftLoweredChange,
            leftOverheadChange);
        AssertOverheadIncreasesRelativeToLowered(
            "right",
            rightLoweredChange,
            rightOverheadChange);
        AssertPoseOrdering(
            "left",
            leftLoweredChange,
            leftForwardChange,
            leftOverheadChange);
        AssertPoseOrdering(
            "right",
            rightLoweredChange,
            rightForwardChange,
            rightOverheadChange);

        float leftDefaultShoulderWeight = leftController.Get("ShoulderWeight").AsSingle();
        float rightDefaultShoulderWeight = rightController.Get("ShoulderWeight").AsSingle();

        leftController.Set("ShoulderWeight", 0f);
        rightController.Set("ShoulderWeight", 0f);

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftHandTarget,
            rightHandTarget,
            leftOverheadMarker,
            rightOverheadMarker);
        float leftOverheadZeroElevation = ExtractShoulderChangeMagnitude(skeleton, leftArm);
        float rightOverheadZeroElevation = ExtractShoulderChangeMagnitude(skeleton, rightArm);

        leftController.Set("ShoulderWeight", leftDefaultShoulderWeight);
        rightController.Set("ShoulderWeight", rightDefaultShoulderWeight);

        AssertShoulderWeightResponsiveness("left", leftOverheadChange, leftOverheadZeroElevation);
        AssertShoulderWeightResponsiveness("right", rightOverheadChange, rightOverheadZeroElevation);
    }

    /// <summary>
    /// Verifies the modifier resolves its body frame from the current animated shoulder span, so an active shoulder
    /// override can replace a rounded animation baseline instead of using a stale rest-shoulder frame.
    /// </summary>
    [Fact]
    public async Task ArmShoulderCorrection_ActiveOverrideReplacesRoundedAnimationBaselineInPoseFrame()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Node3D handTargetPoses = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HandTargetPosesPath), exactMatch: false);
        Node3D leftHandTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftHandTargetPath), exactMatch: false);
        Node3D rightHandTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightHandTargetPath), exactMatch: false);
        Node leftController = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(LeftArmControllerPath), exactMatch: false);
        Node rightController = Assert.IsType<Node>(sceneRoot.GetNodeOrNull(RightArmControllerPath), exactMatch: false);
        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in the verification scene.");

        int leftShoulderIndex = RequireBone(skeleton, "LeftShoulder");
        int rightShoulderIndex = RequireBone(skeleton, "RightShoulder");
        int hipsIndex = RequireBone(skeleton, "Hips");
        int neckIndex = RequireBone(skeleton, "Neck");

        float leftDefaultShoulderWeight = leftController.Get("ShoulderWeight").AsSingle();
        float rightDefaultShoulderWeight = rightController.Get("ShoulderWeight").AsSingle();
        leftController.Set("ShoulderWeight", 0f);
        rightController.Set("ShoulderWeight", 0f);

        try
        {
            Basis restBodyBasis = BuildBodyBasisFromRest(skeleton, hipsIndex, neckIndex, leftShoulderIndex, rightShoulderIndex);
            Basis leftRestBasisInBody = (restBodyBasis.Inverse() * skeleton.GetBoneGlobalRest(leftShoulderIndex).Basis)
                .Orthonormalized();
            Basis rightRestBasisInBody = (restBodyBasis.Inverse() * skeleton.GetBoneGlobalRest(rightShoulderIndex).Basis)
                .Orthonormalized();

            // Emulate an animated clip/body baseline where the clavicle origins swing the shoulder span forward/back.
            // The previous bug used rest shoulder origins here, so the zero-weight override was evaluated in the wrong
            // body frame even though the active pose's shoulders had moved away from rest.
            skeleton.SetBonePosePosition(leftShoulderIndex, new Vector3(0f, 0f, 0.08f));
            skeleton.SetBonePosePosition(rightShoulderIndex, new Vector3(0f, 0f, -0.08f));

            Quaternion leftRoundedBaseline = new(Vector3.Up, Mathf.DegToRad(28f));
            Quaternion rightRoundedBaseline = leftRoundedBaseline.Inverse();
            skeleton.SetBonePoseRotation(leftShoulderIndex, leftRoundedBaseline);
            skeleton.SetBonePoseRotation(rightShoulderIndex, rightRoundedBaseline);

            AssertShoulderOriginsDifferFromRest(skeleton, leftShoulderIndex, rightShoulderIndex);

            Basis currentBodyBasis = BuildBodyBasisFromPose(skeleton, hipsIndex, neckIndex, leftShoulderIndex, rightShoulderIndex);
            Quaternion expectedLeftPoseFrameNeutral = (currentBodyBasis * leftRestBasisInBody)
                .Orthonormalized()
                .GetRotationQuaternion()
                .Normalized();
            Quaternion expectedRightPoseFrameNeutral = (currentBodyBasis * rightRestBasisInBody)
                .Orthonormalized()
                .GetRotationQuaternion()
                .Normalized();
            float leftRoundedBaselineError = ShoulderPoseFrameNeutralError(
                skeleton,
                leftShoulderIndex,
                expectedLeftPoseFrameNeutral);
            float rightRoundedBaselineError = ShoulderPoseFrameNeutralError(
                skeleton,
                rightShoulderIndex,
                expectedRightPoseFrameNeutral);

            await ApplyPoseAndSettleAsync(
                sceneTree,
                skeleton,
                leftHandTarget,
                rightHandTarget,
                RequirePoseMarker(handTargetPoses, "LeftLowered"),
                RequirePoseMarker(handTargetPoses, "RightLowered"));

            AssertShoulderPoseFrameNeutralOverride(
                skeleton,
                leftShoulderIndex,
                expectedLeftPoseFrameNeutral,
                leftRoundedBaselineError,
                "left");
            AssertShoulderPoseFrameNeutralOverride(
                skeleton,
                rightShoulderIndex,
                expectedRightPoseFrameNeutral,
                rightRoundedBaselineError,
                "right");
        }
        finally
        {
            leftController.Set("ShoulderWeight", leftDefaultShoulderWeight);
            rightController.Set("ShoulderWeight", rightDefaultShoulderWeight);
        }
    }

    private static async Task ApplyPoseAndSettleAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        Node3D leftHandTarget,
        Node3D rightHandTarget,
        Node3D leftMarker,
        Node3D rightMarker)
    {
        leftHandTarget.GlobalTransform = leftMarker.GlobalTransform;
        rightHandTarget.GlobalTransform = rightMarker.GlobalTransform;

        for (int settleStep = 0; settleStep < SettleSkeletonUpdates; settleStep++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        }
    }

    private static void AssertForwardPoseSymmetry(float leftForwardChange, float rightForwardChange)
    {
        float absoluteDifference = Mathf.Abs(leftForwardChange - rightForwardChange);
        Assert.True(
            absoluteDifference <= MaximumForwardPoseSymmetryDifferenceRadians,
            "Forward pose should produce comparable left/right shoulder correction magnitude. " +
            $"Observed difference: {absoluteDifference:F6} rad " +
            $"(left: {leftForwardChange:F6}, right: {rightForwardChange:F6}, " +
            $"tolerance: {MaximumForwardPoseSymmetryDifferenceRadians:F6} rad)."
        );
    }

    private static void AssertOverheadIncreasesRelativeToLowered(
        string sideLabel,
        float loweredChange,
        float overheadChange)
    {
        Assert.True(
            overheadChange >= loweredChange + MinimumOverheadGainOverLoweredRadians,
            $"Overhead pose ({sideLabel}) should raise shoulder more than lowered/rest pose. " +
            $"Lowered change: {loweredChange:F6} rad, overhead change: {overheadChange:F6} rad, " +
            $"minimum gain: {MinimumOverheadGainOverLoweredRadians:F6} rad."
        );
    }

    private static void AssertShoulderWeightResponsiveness(
        string sideLabel,
        float overheadDefault,
        float overheadZeroShoulderWeight)
    {
        Assert.True(
            overheadDefault >= overheadZeroShoulderWeight + MinimumShoulderWeightResponsivenessRadians,
            $"Overhead pose ({sideLabel}) should respond to shoulder weight. " +
            $"Default overhead change: {overheadDefault:F6} rad, " +
            $"zero-weight overhead change: {overheadZeroShoulderWeight:F6} rad, " +
            $"minimum expected reduction: {MinimumShoulderWeightResponsivenessRadians:F6} rad."
        );
    }

    private static void AssertPoseOrdering(
        string sideLabel,
        float loweredChange,
        float forwardChange,
        float overheadChange)
    {
        Assert.True(
            forwardChange >= loweredChange + MinimumForwardGainOverLoweredRadians,
            $"Forward pose ({sideLabel}) should produce more shoulder correction than lowered pose. " +
            $"Lowered: {loweredChange:F6} rad, forward: {forwardChange:F6} rad, " +
            $"minimum gain: {MinimumForwardGainOverLoweredRadians:F6} rad.");

        Assert.True(
            overheadChange >= forwardChange + MinimumOverheadGainOverForwardRadians,
            $"Overhead pose ({sideLabel}) should produce more shoulder correction than forward pose. " +
            $"Forward: {forwardChange:F6} rad, overhead: {overheadChange:F6} rad, " +
            $"minimum gain: {MinimumOverheadGainOverForwardRadians:F6} rad.");
    }

    private static void AssertShoulderOriginsDifferFromRest(
        Skeleton3D skeleton,
        int leftShoulderIndex,
        int rightShoulderIndex)
    {
        float leftOriginDelta = skeleton.GetBoneGlobalPose(leftShoulderIndex).Origin
            .DistanceTo(skeleton.GetBoneGlobalRest(leftShoulderIndex).Origin);
        float rightOriginDelta = skeleton.GetBoneGlobalPose(rightShoulderIndex).Origin
            .DistanceTo(skeleton.GetBoneGlobalRest(rightShoulderIndex).Origin);

        Assert.True(
            leftOriginDelta >= MinimumAnimatedShoulderOriginDelta,
            $"Synthetic animated left shoulder origin should differ from rest enough to expose stale-rest-frame bugs. " +
            $"Observed delta: {leftOriginDelta:F6}, minimum: {MinimumAnimatedShoulderOriginDelta:F6}.");
        Assert.True(
            rightOriginDelta >= MinimumAnimatedShoulderOriginDelta,
            $"Synthetic animated right shoulder origin should differ from rest enough to expose stale-rest-frame bugs. " +
            $"Observed delta: {rightOriginDelta:F6}, minimum: {MinimumAnimatedShoulderOriginDelta:F6}.");
    }

    private static void AssertShoulderPoseFrameNeutralOverride(
        Skeleton3D skeleton,
        int shoulderBoneIndex,
        Quaternion expectedPoseFrameNeutral,
        float roundedBaselineError,
        string sideLabel)
    {
        float angularError = ShoulderPoseFrameNeutralError(skeleton, shoulderBoneIndex, expectedPoseFrameNeutral);

        Assert.True(
            angularError <= MaximumNeutralOverrideErrorRadians,
            $"Active {sideLabel} shoulder override with zero correction weight should resolve to the current-pose " +
            "body-frame neutral orientation after replacing the rounded animation baseline. " +
            $"Angular error: {angularError:F6} rad, tolerance: {MaximumNeutralOverrideErrorRadians:F6} rad.");
        Assert.True(
            angularError <= roundedBaselineError * MinimumRoundedBaselineReductionRatio,
            $"Active {sideLabel} shoulder override should materially reduce the rounded animation baseline. " +
            $"Baseline error: {roundedBaselineError:F6} rad, final error: {angularError:F6} rad, " +
            $"required ratio: {MinimumRoundedBaselineReductionRatio:F3}.");
    }

    private static float ShoulderPoseFrameNeutralError(
        Skeleton3D skeleton,
        int shoulderBoneIndex,
        Quaternion expectedPoseFrameNeutral)
    {
        Quaternion actual = skeleton.GetBoneGlobalPose(shoulderBoneIndex).Basis.GetRotationQuaternion().Normalized();
        return QuaternionAngularDistance(expectedPoseFrameNeutral, actual);
    }

    private static Basis BuildBodyBasisFromRest(
        Skeleton3D skeleton,
        int hipsIndex,
        int neckIndex,
        int leftShoulderIndex,
        int rightShoulderIndex) => BuildBodyBasis(
            skeleton.GetBoneGlobalRest(hipsIndex).Origin,
            skeleton.GetBoneGlobalRest(neckIndex).Origin,
            skeleton.GetBoneGlobalRest(leftShoulderIndex).Origin,
            skeleton.GetBoneGlobalRest(rightShoulderIndex).Origin);

    private static Basis BuildBodyBasisFromPose(
        Skeleton3D skeleton,
        int hipsIndex,
        int neckIndex,
        int leftShoulderIndex,
        int rightShoulderIndex) => BuildBodyBasis(
            skeleton.GetBoneGlobalPose(hipsIndex).Origin,
            skeleton.GetBoneGlobalPose(neckIndex).Origin,
            skeleton.GetBoneGlobalPose(leftShoulderIndex).Origin,
            skeleton.GetBoneGlobalPose(rightShoulderIndex).Origin);

    private static Basis BuildBodyBasis(
        Vector3 hipsPosition,
        Vector3 neckPosition,
        Vector3 leftShoulderPosition,
        Vector3 rightShoulderPosition)
    {
        Vector3 bodyUp = (neckPosition - hipsPosition).Normalized();
        Vector3 shoulderSpan = rightShoulderPosition - leftShoulderPosition;
        Vector3 bodyRight = (shoulderSpan - (shoulderSpan.Dot(bodyUp) * bodyUp)).Normalized();
        Vector3 bodyForward = bodyRight.Cross(bodyUp).Normalized();

        return new Basis(bodyRight, bodyUp, bodyForward).Orthonormalized();
    }

    private static async Task AssertShoulderDeterminismAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        ArmRigData arm,
        string poseSlug)
    {
        Quaternion referenceRotation = skeleton.GetBonePoseRotation(arm.ShoulderBoneIndex).Normalized();

        for (int sampleIndex = 0; sampleIndex < DeterminismSamples; sampleIndex++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
            Quaternion sampleRotation = skeleton.GetBonePoseRotation(arm.ShoulderBoneIndex).Normalized();
            float angularError = QuaternionAngularDistance(referenceRotation, sampleRotation);

            Assert.True(
                angularError <= MaximumDeterminismErrorRadians,
                $"Pose '{poseSlug}' ({arm.SideLabel}) should produce deterministic shoulder rotation across repeated samples. " +
                $"Sample {sampleIndex + 1} drift: {angularError:F8} rad (tolerance {MaximumDeterminismErrorRadians:F8} rad)."
            );
        }
    }

    private static float ExtractShoulderChangeMagnitude(Skeleton3D skeleton, ArmRigData arm)
    {
        Quaternion shoulderRotation = skeleton.GetBonePoseRotation(arm.ShoulderBoneIndex).Normalized();
        return QuaternionAngularDistance(arm.ShoulderRestLocalRotation, shoulderRotation);
    }

    private static ArmRigData BuildArmRigData(Skeleton3D skeleton, string sidePrefix)
    {
        string shoulderName = $"{sidePrefix}Shoulder";
        int shoulderBoneIndex = RequireBone(skeleton, shoulderName);
        Quaternion shoulderRestLocalRotation = skeleton.GetBoneRest(shoulderBoneIndex).Basis.GetRotationQuaternion();
        string sideLabel = sidePrefix.Equals("Left", StringComparison.Ordinal) ? "left" : "right";

        return new ArmRigData(sideLabel, shoulderBoneIndex, shoulderRestLocalRotation);
    }

    private static float QuaternionAngularDistance(Quaternion a, Quaternion b)
    {
        float dot = Mathf.Abs(a.Dot(b));
        dot = Mathf.Clamp(dot, -1f, 1f);
        return 2f * Mathf.Acos(dot);
    }

    private static Node3D RequirePoseMarker(Node3D handTargetPoses, string markerName) =>
        Assert.IsType<Node3D>(handTargetPoses.GetNodeOrNull(markerName), exactMatch: false);

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

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int index = skeleton.FindBone(boneName);

        return index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException($"Expected bone '{boneName}' to exist in skeleton.");
    }
}
