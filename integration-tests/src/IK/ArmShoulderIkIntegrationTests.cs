using System.Text;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration tests for IK-002 arm and shoulder IK pole-target placement.
/// Each pose verifies that the pole target points in the expected anatomical
/// direction and that the IK solver brings the hand close to the target.
/// </summary>
public sealed class ArmShoulderIkIntegrationTests
{
    private const string VerificationScenePath = "res://tests/characters/ik/arm_shoulder_ik_test.tscn";
    private const string HandTargetPosesPath = "Markers/HandTargetPoses";
    private const string LeftHandTargetPath = "Markers/LeftHandTarget";
    private const string RightHandTargetPath = "Markers/RightHandTarget";
    private const string LeftPoleTargetPath = "Markers/LeftPoleTarget";
    private const string RightPoleTargetPath = "Markers/RightPoleTarget";

    private const float MinimumPoleDirectionAlignment = 0.3f;
    private const float MaximumHandResidualDistance = 0.15f;
    private const float PoleOffsetFloorToleranceMetres = 0.005f;

    private enum ExpectedPoleDirection
    {
        LaterallyOutward,
        Posterior,
    }

    private readonly record struct PoseDefinition(
        string LeftMarkerName,
        string RightMarkerName,
        string Slug,
        ExpectedPoleDirection PoleDirection);

    private static readonly PoseDefinition[] _poses =
    [
        new("LeftLowered", "RightLowered", "lowered", ExpectedPoleDirection.LaterallyOutward),
        new("LeftForward", "RightForward", "forward", ExpectedPoleDirection.LaterallyOutward),
        new("LeftOverhead", "RightOverhead", "overhead", ExpectedPoleDirection.LaterallyOutward),
        new("LeftSide", "RightSide", "side", ExpectedPoleDirection.Posterior),
        new("LeftBehindHead", "RightBehindHead", "behind-head", ExpectedPoleDirection.LaterallyOutward),
        new("LeftChest", "RightChest", "chest", ExpectedPoleDirection.LaterallyOutward),
    ];

    /// <summary>
    /// Loads the IK-002 verification scene, iterates all required arm poses, and
    /// asserts that each pole target aligns with its expected anatomical direction
    /// while the IK solver keeps the hand within tolerance of the target.
    /// </summary>
    [Fact]
    public async Task ArmIk_VerificationScene_PoleTargetsTrackExpectedDirectionsForRequiredPoses()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException(
                "Expected verification scene to become current scene.");

        // Resolve scene nodes.
        Node3D handTargetPoses = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(HandTargetPosesPath));
        Node3D leftHandTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(LeftHandTargetPath));
        Node3D rightHandTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(RightHandTargetPath));
        Node3D leftPoleTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(LeftPoleTargetPath));
        Node3D rightPoleTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(RightPoleTargetPath));

        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException(
                "Expected at least one Skeleton3D in the verification scene.");

        Dictionary<string, Node3D> poseMarkers = ResolvePoseMarkers(handTargetPoses);

        // Resolve bone indices.
        int hipsIdx = RequireBone(skeleton, "Hips");
        int neckIdx = RequireBone(skeleton, "Neck");
        int lShoulderIdx = RequireBone(skeleton, "LeftShoulder");
        int rShoulderIdx = RequireBone(skeleton, "RightShoulder");
        int lUpperArmIdx = RequireBone(skeleton, "LeftUpperArm");
        int rUpperArmIdx = RequireBone(skeleton, "RightUpperArm");
        int lHandIdx = RequireBone(skeleton, "LeftHand");
        int rHandIdx = RequireBone(skeleton, "RightHand");

        var failures = new List<string>();

        for (int poseIndex = 0; poseIndex < _poses.Length; poseIndex++)
        {
            PoseDefinition pose = _poses[poseIndex];

            Node3D leftMarker = poseMarkers[pose.LeftMarkerName];
            Node3D rightMarker = poseMarkers[pose.RightMarkerName];

            // Drive the IK targets to the current pose markers.
            leftHandTarget.GlobalTransform = leftMarker.GlobalTransform;
            rightHandTarget.GlobalTransform = rightMarker.GlobalTransform;

            // Wait for the skeleton modifier pipeline to fully settle.
            // The pipeline runs ArmIKController → TwoBoneIK3D → CopyTransformModifier3D
            // each frame. Wait for multiple skeleton updates to ensure convergence.
            for (int settle = 0; settle < 4; settle++)
            {
                _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
            }

            // Compute body reference frame.
            Basis bodyBasisInverse = ComputeBodyBasisInverse(
                skeleton, hipsIdx, neckIdx, lShoulderIdx, rShoulderIdx);

            // --- Left arm assertions ---
            var poseFailures = new StringBuilder();

            AssertArmPole(
                poseFailures,
                skeleton,
                bodyBasisInverse,
                leftPoleTarget,
                leftHandTarget,
                lUpperArmIdx,
                lHandIdx,
                pose.PoleDirection,
                isLeftArm: true,
                pose.Slug);

            // --- Right arm assertions ---
            AssertArmPole(
                poseFailures,
                skeleton,
                bodyBasisInverse,
                rightPoleTarget,
                rightHandTarget,
                rUpperArmIdx,
                rHandIdx,
                pose.PoleDirection,
                isLeftArm: false,
                pose.Slug);

            if (poseFailures.Length > 0)
            {
                // Make the active pose markers and hand targets visible for diagnostic screenshots.
                leftMarker.Visible = true;
                rightMarker.Visible = true;
                leftHandTarget.Visible = true;
                rightHandTarget.Visible = true;

                await PhotoboothHelper.CaptureScreenshotsAsync(
                    sceneTree,
                    sceneRoot,
                    $"char002/arm_shoulder_ik/failures/{pose.Slug}.jpg");

                // Restore visibility.
                leftMarker.Visible = false;
                rightMarker.Visible = false;
                leftHandTarget.Visible = false;
                rightHandTarget.Visible = false;

                failures.Add(poseFailures.ToString());
            }
        }

        if (failures.Count > 0)
        {
            Assert.Fail(string.Join("\n", failures));
        }
    }

    /// <summary>
    /// Verifies folded/compressed arm reaches enforce the rest-arm-derived elbow pole floor.
    /// </summary>
    [Fact]
    public async Task ArmIk_CompressedFoldedReach_EnforcesRestArmPoleOffsetFloor()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException(
                "Expected verification scene to become current scene.");

        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException(
                "Expected at least one Skeleton3D in the verification scene.");

        Node3D leftHandTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(LeftHandTargetPath));
        Node3D leftPoleTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(LeftPoleTargetPath));
        SkeletonModifier3D leftArmController = Assert.IsAssignableFrom<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull("Subject/Female/Female_export/GeneralSkeleton/LeftArmIKController"));

        int leftUpperArmIndex = RequireBone(skeleton, "LeftUpperArm");
        int leftLowerArmIndex = RequireBone(skeleton, "LeftLowerArm");
        int leftHandIndex = RequireBone(skeleton, "LeftHand");

        Vector3 shoulderPosition = BoneWorldPosition(skeleton, leftUpperArmIndex);
        leftHandTarget.GlobalPosition = shoulderPosition + new Vector3(-0.06f, -0.16f, 0.04f);

        for (int settle = 0; settle < 4; settle++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        }

        float restArmLength = ComputeRestArmLength(skeleton, leftUpperArmIndex, leftLowerArmIndex, leftHandIndex);
        float currentArmLength = ComputeCurrentArmLength(skeleton, leftHandTarget, leftUpperArmIndex);
        float compressionRatioForPoleFloor = Mathf.Clamp(
            ReadFloatProperty(leftArmController, "CompressionRatioForRestPoleFloor"),
            0.1f,
            1.0f);

        Assert.True(
            currentArmLength <= restArmLength * compressionRatioForPoleFloor,
            "Arm floor assertion requires a compressed arm configuration, but pose was not compressed enough. " +
            $"Current length: {currentArmLength:F4}m, rest length: {restArmLength:F4}m, compression threshold: {restArmLength * compressionRatioForPoleFloor:F4}m.");

        Vector3 solvedShoulderPosition = BoneWorldPosition(skeleton, leftUpperArmIndex);
        Assert.True(
            leftHandTarget.GlobalPosition.Y <= solvedShoulderPosition.Y,
            "Arm floor assertion requires a folded-reach pose (hand at or below shoulder height). " +
            $"Hand Y: {leftHandTarget.GlobalPosition.Y:F4}, shoulder Y: {solvedShoulderPosition.Y:F4}.");

        float minimumPoleOffset = ReadFloatProperty(leftArmController, "MinimumPoleOffset");
        float restArmHalfPoleOffsetMargin = ReadFloatProperty(leftArmController, "RestArmHalfPoleOffsetMargin");
        float expectedFloor = Mathf.Max(minimumPoleOffset, (restArmLength * 0.5f) + restArmHalfPoleOffsetMargin);
        float observedPoleOffset = ComputePoleOffsetDistance(skeleton, leftPoleTarget, leftHandTarget, leftUpperArmIndex);

        Assert.True(
            observedPoleOffset + PoleOffsetFloorToleranceMetres >= expectedFloor,
            "Compressed folded arm pose should enforce the rest-arm-derived elbow pole floor. " +
            $"Observed offset: {observedPoleOffset:F4}m, expected floor: {expectedFloor:F4}m, tolerance: {PoleOffsetFloorToleranceMetres:F4}m.");
    }

    /// <summary>
    /// Verifies folded/compressed safeguarding uses body-local vertical relation when the body is non-upright.
    /// </summary>
    [Fact]
    public async Task ArmIk_CompressedFoldedReachWithInvertedBody_UsesBodyLocalFoldedGateAndEnforcesPoleFloor()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException(
                "Expected verification scene to become current scene.");

        Node3D subject = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull("Subject"));
        subject.Rotation = new Vector3(Mathf.Pi, 0f, 0f);

        Skeleton3D skeleton = FindFirstSkeleton(sceneRoot)
            ?? throw new Xunit.Sdk.XunitException(
                "Expected at least one Skeleton3D in the verification scene.");

        Node3D leftHandTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(LeftHandTargetPath));
        Node3D leftPoleTarget = Assert.IsAssignableFrom<Node3D>(
            sceneRoot.GetNodeOrNull(LeftPoleTargetPath));
        SkeletonModifier3D leftArmController = Assert.IsAssignableFrom<SkeletonModifier3D>(
            sceneRoot.GetNodeOrNull("Subject/Female/Female_export/GeneralSkeleton/LeftArmIKController"));

        int hipsIndex = RequireBone(skeleton, "Hips");
        int neckIndex = RequireBone(skeleton, "Neck");
        int leftShoulderIndex = RequireBone(skeleton, "LeftShoulder");
        int rightShoulderIndex = RequireBone(skeleton, "RightShoulder");
        int leftUpperArmIndex = RequireBone(skeleton, "LeftUpperArm");
        int leftLowerArmIndex = RequireBone(skeleton, "LeftLowerArm");
        int leftHandIndex = RequireBone(skeleton, "LeftHand");

        for (int settle = 0; settle < 4; settle++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        }

        Basis bodyBasis = ComputeBodyBasis(
            skeleton,
            hipsIndex,
            neckIndex,
            leftShoulderIndex,
            rightShoulderIndex);

        Vector3 shoulderPosition = BoneWorldPosition(skeleton, leftUpperArmIndex);
        Vector3 foldedCompressedOffsetBody = new(-0.06f, -0.14f, 0.03f);
        Vector3 foldedCompressedOffsetGlobal = bodyBasis * foldedCompressedOffsetBody;
        leftHandTarget.GlobalPosition = shoulderPosition + foldedCompressedOffsetGlobal;

        for (int settle = 0; settle < 4; settle++)
        {
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        }

        Vector3 solvedShoulderPosition = BoneWorldPosition(skeleton, leftUpperArmIndex);
        Vector3 bodyUp = ComputeBodyBasis(
            skeleton,
            hipsIndex,
            neckIndex,
            leftShoulderIndex,
            rightShoulderIndex).Column1;

        float bodyVerticalDelta = (leftHandTarget.GlobalPosition - solvedShoulderPosition).Dot(bodyUp);
        float worldVerticalDelta = leftHandTarget.GlobalPosition.Y - solvedShoulderPosition.Y;

        Assert.True(
            bodyVerticalDelta <= -0.01f,
            "Regression setup requires folded reach in body-local space (hand below shoulder in body-up projection). " +
            $"Body vertical delta: {bodyVerticalDelta:F4}.");

        Assert.True(
            worldVerticalDelta >= 0.01f,
            "Regression setup must diverge from world-Y gating semantics (hand globally above shoulder). " +
            $"World vertical delta: {worldVerticalDelta:F4}.");

        float restArmLength = ComputeRestArmLength(skeleton, leftUpperArmIndex, leftLowerArmIndex, leftHandIndex);
        float currentArmLength = ComputeCurrentArmLength(skeleton, leftHandTarget, leftUpperArmIndex);
        float compressionRatioForPoleFloor = Mathf.Clamp(
            ReadFloatProperty(leftArmController, "CompressionRatioForRestPoleFloor"),
            0.1f,
            1.0f);

        Assert.True(
            currentArmLength <= restArmLength * compressionRatioForPoleFloor,
            "Regression setup requires compressed arm length so safeguard should be eligible once folded gate passes. " +
            $"Current length: {currentArmLength:F4}m, rest length: {restArmLength:F4}m, compression threshold: {restArmLength * compressionRatioForPoleFloor:F4}m.");

        float minimumPoleOffset = ReadFloatProperty(leftArmController, "MinimumPoleOffset");
        float restArmHalfPoleOffsetMargin = ReadFloatProperty(leftArmController, "RestArmHalfPoleOffsetMargin");
        float expectedFloor = Mathf.Max(minimumPoleOffset, (restArmLength * 0.5f) + restArmHalfPoleOffsetMargin);
        float observedPoleOffset = ComputePoleOffsetDistance(skeleton, leftPoleTarget, leftHandTarget, leftUpperArmIndex);

        Assert.True(
            observedPoleOffset + PoleOffsetFloorToleranceMetres >= expectedFloor,
            "When body-local folded + compressed gates are both true, pole offset floor must be enforced even in non-upright poses. " +
            $"Observed offset: {observedPoleOffset:F4}m, expected floor: {expectedFloor:F4}m, tolerance: {PoleOffsetFloorToleranceMetres:F4}m.");
    }

    private static void AssertArmPole(
        StringBuilder failures,
        Skeleton3D skeleton,
        Basis bodyBasisInverse,
        Node3D poleTarget,
        Node3D handTarget,
        int upperArmIdx,
        int handIdx,
        ExpectedPoleDirection expectedDirection,
        bool isLeftArm,
        string poseSlug)
    {
        string sideLabel = isLeftArm ? "left" : "right";

        Vector3 shoulderPos = BoneWorldPosition(skeleton, upperArmIdx);
        Vector3 midpoint = (shoulderPos + handTarget.GlobalPosition) * 0.5f;
        Vector3 poleDirBody = bodyBasisInverse
            * (poleTarget.GlobalPosition - midpoint).Normalized();

        Vector3 expected = expectedDirection switch
        {
            ExpectedPoleDirection.LaterallyOutward => isLeftArm
                ? new Vector3(-1f, 0f, 0f)
                : new Vector3(1f, 0f, 0f),
            ExpectedPoleDirection.Posterior => new Vector3(0f, 0f, -1f),
            _ => throw new ArgumentOutOfRangeException(
                nameof(expectedDirection), expectedDirection, null),
        };

        float alignment = poleDirBody.Dot(expected);

        if (alignment <= MinimumPoleDirectionAlignment)
        {
            _ = failures.AppendLine(
                $"[{poseSlug}/{sideLabel}] Pole direction alignment {alignment:F3} " +
                $"<= threshold {MinimumPoleDirectionAlignment:F3}. " +
                $"Body-space pole direction: {poleDirBody}, expected: {expected}.");
        }

        // For posterior poles, also verify the pole is actually behind the body (negative Z).
        // A non-negative Z indicates the elbow is bending forward, which is anatomically incorrect
        // for arms extended to the sides.
        if (expectedDirection == ExpectedPoleDirection.Posterior && poleDirBody.Z >= -0.3f)
        {
            _ = failures.AppendLine(
                $"[{poseSlug}/{sideLabel}] Posterior pole direction has Z >= -0.3 " +
                $"(Z={poleDirBody.Z:F3}), indicating forward elbow bend. " +
                $"Body-space pole direction: {poleDirBody}.");
        }

        Vector3 handBonePos = BoneWorldPosition(skeleton, handIdx);
        float handResidual = handBonePos.DistanceTo(handTarget.GlobalPosition);

        if (handResidual >= MaximumHandResidualDistance)
        {
            _ = failures.AppendLine(
                $"[{poseSlug}/{sideLabel}] Hand residual {handResidual:F4}m " +
                $">= tolerance {MaximumHandResidualDistance:F4}m.");
        }
    }

    private static Basis ComputeBodyBasisInverse(
        Skeleton3D skeleton,
        int hipsIdx,
        int neckIdx,
        int lShoulderIdx,
        int rShoulderIdx) =>
        ComputeBodyBasis(skeleton, hipsIdx, neckIdx, lShoulderIdx, rShoulderIdx).Inverse();

    private static Basis ComputeBodyBasis(
        Skeleton3D skeleton,
        int hipsIdx,
        int neckIdx,
        int lShoulderIdx,
        int rShoulderIdx)
    {
        Vector3 hipsPos = BoneWorldPosition(skeleton, hipsIdx);
        Vector3 neckPos = BoneWorldPosition(skeleton, neckIdx);
        Vector3 lShoulderPos = BoneWorldPosition(skeleton, lShoulderIdx);
        Vector3 rShoulderPos = BoneWorldPosition(skeleton, rShoulderIdx);

        Vector3 bodyUp = (neckPos - hipsPos).Normalized();
        Vector3 bodyRight = (rShoulderPos - lShoulderPos).Normalized();
        bodyRight = (bodyRight - (bodyRight.Dot(bodyUp) * bodyUp)).Normalized();
        Vector3 bodyForward = bodyRight.Cross(bodyUp);

        Basis bodyBasis = new()
        {
            Column0 = bodyRight,
            Column1 = bodyUp,
            Column2 = -bodyForward,
        };

        return bodyBasis;
    }

    private static Dictionary<string, Node3D> ResolvePoseMarkers(Node3D handTargetPoses)
    {
        var markers = new Dictionary<string, Node3D>(_poses.Length * 2, StringComparer.Ordinal);

        foreach (PoseDefinition pose in _poses)
        {
            markers[pose.LeftMarkerName] = Assert.IsAssignableFrom<Node3D>(
                handTargetPoses.GetNodeOrNull(pose.LeftMarkerName));
            markers[pose.RightMarkerName] = Assert.IsAssignableFrom<Node3D>(
                handTargetPoses.GetNodeOrNull(pose.RightMarkerName));
        }

        return markers;
    }

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

    private static Vector3 BoneWorldPosition(Skeleton3D skeleton, int boneIdx) =>
        skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIdx).Origin;

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int index = skeleton.FindBone(boneName);

        return index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException(
                $"Expected bone '{boneName}' to exist in skeleton.");
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
        Node3D handTarget,
        int upperArmBoneIndex)
    {
        Vector3 shoulderPosition = BoneWorldPosition(skeleton, upperArmBoneIndex);
        Vector3 midpoint = (shoulderPosition + handTarget.GlobalPosition) * 0.5f;
        return poleTarget.GlobalPosition.DistanceTo(midpoint);
    }

    private static float ComputeCurrentArmLength(Skeleton3D skeleton, Node3D handTarget, int upperArmBoneIndex)
    {
        Vector3 shoulderPosition = BoneWorldPosition(skeleton, upperArmBoneIndex);
        return shoulderPosition.DistanceTo(handTarget.GlobalPosition);
    }

    private static float ComputeRestArmLength(
        Skeleton3D skeleton,
        int upperArmBoneIndex,
        int lowerArmBoneIndex,
        int handBoneIndex)
    {
        Vector3 upperArmRestPosition = skeleton.GetBoneGlobalRest(upperArmBoneIndex).Origin;
        Vector3 lowerArmRestPosition = skeleton.GetBoneGlobalRest(lowerArmBoneIndex).Origin;
        Vector3 handRestPosition = skeleton.GetBoneGlobalRest(handBoneIndex).Origin;

        return upperArmRestPosition.DistanceTo(lowerArmRestPosition)
            + lowerArmRestPosition.DistanceTo(handRestPosition);
    }
}
