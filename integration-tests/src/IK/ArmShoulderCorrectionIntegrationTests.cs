using AlleyCat.IK;
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
    private const float MaximumBaselineInputDriftMetres = 0.0001f;
    private const float MaximumNeutralOverrideErrorRadians = 0.18f;
    private const float MinimumRoundedBaselineReductionRatio = 0.5f;
    private const float MinimumAnimatedShoulderOriginDelta = 0.04f;

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

    /// <summary>
    /// Verifies a held target uses a fixed pose-frame arm-origin baseline rather than feeding the
    /// previously solved upper-arm position back into the next shoulder-correction sample.
    /// </summary>
    [Fact]
    public async Task ArmShoulderCorrection_HeldTargetUsesStablePreCorrectionArmBaseline()
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
        ArmIKController rightController = Assert.IsType<ArmIKController>(
            sceneRoot.GetNodeOrNull(RightArmControllerPath),
            exactMatch: false);
        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in the verification scene.");
        int rightShoulderIndex = RequireBone(skeleton, "RightShoulder");

        await ApplyPoseAndSettleAsync(
            sceneTree,
            skeleton,
            leftHandTarget,
            rightHandTarget,
            RequirePoseMarker(handTargetPoses, "LeftOverhead"),
            RequirePoseMarker(handTargetPoses, "RightOverhead"));

        ArmShoulderCorrectionTrace reference = rightController.LastShoulderCorrectionTrace;
        Assert.True(
            reference.InputUpperArmPosition.DistanceTo(reference.SolvedUpperArmPosition) > MaximumBaselineInputDriftMetres,
            "The overhead scenario must visibly move the solved upper arm away from the fixed correction baseline.");

        for (int sampleIndex = 0; sampleIndex < DeterminismSamples; sampleIndex++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
            ArmShoulderCorrectionTrace sample = rightController.LastShoulderCorrectionTrace;
            float outputDrift = QuaternionAngularDistance(
                reference.AppliedShoulderPoseRotation,
                sample.AppliedShoulderPoseRotation);
            float appliedPoseError = QuaternionAngularDistance(
                sample.AppliedShoulderPoseRotation,
                skeleton.GetBonePoseRotation(rightShoulderIndex).Normalized());

            Assert.True(
                outputDrift <= MaximumDeterminismErrorRadians,
                $"Held-target shoulder correction must preserve its baseline-pose output instead of resampling a prior " +
                $"solved arm pose. Sample {sampleIndex + 1} output drift: {outputDrift:F8} rad " +
                $"(tolerance {MaximumDeterminismErrorRadians:F8} rad).");
            Assert.True(
                appliedPoseError <= MaximumDeterminismErrorRadians,
                $"The right shoulder's runtime pose must match its ArmIKController baseline correction. " +
                $"Sample {sampleIndex + 1} error: {appliedPoseError:F8} rad.");
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
