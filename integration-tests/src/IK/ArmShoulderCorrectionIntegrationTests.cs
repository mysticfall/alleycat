using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for shoulder look-at correction behaviour.
/// </summary>
public sealed class ArmShoulderCorrectionIntegrationTests
{
    private const string VerificationScenePath = "res://tests/characters/ik/arm_shoulder_ik_test.tscn";
    private const string HandTargetPosesPath = "Markers/HandTargetPoses";
    private const string LeftHandTargetPath = "Markers/LeftHandTarget";
    private const string RightHandTargetPath = "Markers/RightHandTarget";
    private const string LeftArmControllerPath = "Subject/Female/Female_export/GeneralSkeleton/LeftArmIKController";
    private const string RightArmControllerPath = "Subject/Female/Female_export/GeneralSkeleton/RightArmIKController";

    private const int SettleSkeletonUpdates = 4;
    private const int DeterminismSamples = 5;
    private const float MaximumDeterminismErrorRadians = 0.001f;
    private const float MaximumForwardPoseSymmetryDifferenceRadians = 0.08f;
    private const float MinimumOverheadGainOverLoweredRadians = 0.05f;
    private const float MinimumForwardGainOverLoweredRadians = 0.01f;
    private const float MinimumOverheadGainOverForwardRadians = 0.01f;
    private const float MinimumElevationWeightResponsivenessRadians = 0.03f;

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

        Node3D handTargetPoses = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(HandTargetPosesPath));
        Node3D leftHandTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(LeftHandTargetPath));
        Node3D rightHandTarget = Assert.IsAssignableFrom<Node3D>(sceneRoot.GetNodeOrNull(RightHandTargetPath));
        Node leftController = Assert.IsAssignableFrom<Node>(sceneRoot.GetNodeOrNull(LeftArmControllerPath));
        Node rightController = Assert.IsAssignableFrom<Node>(sceneRoot.GetNodeOrNull(RightArmControllerPath));
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

        float leftDefaultElevationWeight = leftController.Get("ElevationWeight").AsSingle();
        float rightDefaultElevationWeight = rightController.Get("ElevationWeight").AsSingle();

        leftController.Set("ElevationWeight", 0f);
        rightController.Set("ElevationWeight", 0f);

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftHandTarget,
            rightHandTarget,
            leftOverheadMarker,
            rightOverheadMarker);
        float leftOverheadZeroElevation = ExtractShoulderChangeMagnitude(skeleton, leftArm);
        float rightOverheadZeroElevation = ExtractShoulderChangeMagnitude(skeleton, rightArm);

        leftController.Set("ElevationWeight", leftDefaultElevationWeight);
        rightController.Set("ElevationWeight", rightDefaultElevationWeight);

        AssertElevationWeightResponsiveness("left", leftOverheadChange, leftOverheadZeroElevation);
        AssertElevationWeightResponsiveness("right", rightOverheadChange, rightOverheadZeroElevation);
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

    private static void AssertElevationWeightResponsiveness(
        string sideLabel,
        float overheadDefault,
        float overheadZeroElevationWeight)
    {
        Assert.True(
            overheadDefault >= overheadZeroElevationWeight + MinimumElevationWeightResponsivenessRadians,
            $"Overhead pose ({sideLabel}) should respond to elevation weight. " +
            $"Default overhead change: {overheadDefault:F6} rad, " +
            $"zero-elevation overhead change: {overheadZeroElevationWeight:F6} rad, " +
            $"minimum expected reduction: {MinimumElevationWeightResponsivenessRadians:F6} rad."
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
        Assert.IsAssignableFrom<Node3D>(handTargetPoses.GetNodeOrNull(markerName));

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
