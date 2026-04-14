using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Non-visual integration coverage for IK-001 neck-spine CCDIK behaviour.
/// </summary>
public sealed class NeckSpineCcdikIntegrationTests
{
    private const string VerificationScenePath = "res://tests/characters/ik/neck_spine_ccdik_test.tscn";
    private const string ReusableIkScenePath = "res://assets/characters/ik/neck_spine_ccdik.tscn";
    private const string MarkersRootPath = "Markers";
    private const string HeadTargetPath = "Markers/HeadTarget";
    private const string TargetPosesPath = "Markers/TargetPoses";
    private const string IkNodeName = "NeckSpineCCDIK3D";
    private static readonly string[] _headBoneNameCandidates = ["Head"];
    private static readonly string[] _neckBoneNameCandidates = ["Neck"];
    private static readonly string[] _spineBoneNameCandidates = ["Spine3", "Spine2", "Spine1", "Spine"];
    private const float MaximumDistanceRegression = 0.01f;
    private const float MaximumResidualDistance = 1.5f;
    private const float MinimumDistanceImprovement = 0.001f;
    private const float MinimumDirectionalAlignment = 0.0f;
    private const float MinimumExpectedMovement = 0.0001f;

    private static readonly string[] _requiredPoseMarkerNames =
    [
        "TargetForward",
        "TargetLeft",
        "TargetRight",
        "TargetStoopForward",
        "TargetLeanBack",
    ];

    /// <summary>
    /// Loads the IK-001 verification scene and validates required marker, CCDIK binding, and deterministic neck motion.
    /// </summary>
    [Fact]
    public async Task NeckSpineCcdik_VerificationScene_BindsTargetAndTracksRequiredPoses()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node verificationSceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");

        Assert.NotNull(verificationSceneRoot.GetNodeOrNull(MarkersRootPath));

        Node3D headTarget = Assert.IsType<Node3D>(verificationSceneRoot.GetNodeOrNull(HeadTargetPath));
        Node3D targetPoses = Assert.IsType<Node3D>(verificationSceneRoot.GetNodeOrNull(TargetPosesPath));

        Dictionary<string, Node3D> poseMarkers = ResolveRequiredPoseMarkers(targetPoses);
        Skeleton3D skeleton = FindFirstSkeleton(verificationSceneRoot)
            ?? throw new Xunit.Sdk.XunitException("Expected at least one Skeleton3D in the verification scene.");

        Node ikNode = BindOrCreateIkNode(skeleton, headTarget);
        NodePath expectedTargetPath = ikNode.GetPathTo(headTarget);

        Assert.False(expectedTargetPath.IsEmpty, "Expected a non-empty CCDIK target path to HeadTarget.");

        var configuredTargetPath = (NodePath)ikNode.Get("settings/0/target_node");
        Assert.Equal(expectedTargetPath, configuredTargetPath);

        IReadOnlyList<int> trackedBoneIndices = ResolveTrackedBoneIndices(skeleton, ikNode);

        await SetHeadTargetToMarkerAsync(sceneTree, headTarget, poseMarkers["TargetForward"]);
        Vector3 neutralMarkerPosition = poseMarkers["TargetForward"].GlobalPosition;
        Dictionary<int, Vector3> neutralBonePositions = await CaptureTrackedBoneWorldPositionsAsync(
            sceneTree,
            skeleton,
            trackedBoneIndices);
        float neutralResidualDistance = ResolveClosestDistanceToTarget(neutralBonePositions, neutralMarkerPosition);
        Assert.InRange(neutralResidualDistance, 0.0f, MaximumResidualDistance);

        foreach ((string markerName, Node3D markerNode) in poseMarkers)
        {
            await SetHeadTargetToMarkerAsync(sceneTree, headTarget, markerNode);

            Vector3 markerPosition = markerNode.GlobalPosition;
            Dictionary<int, Vector3> posedBonePositions = await CaptureTrackedBoneWorldPositionsAsync(
                sceneTree,
                skeleton,
                trackedBoneIndices);

            float baselineDistance = ResolveClosestDistanceToTarget(neutralBonePositions, markerPosition);
            float posedDistance = ResolveClosestDistanceToTarget(posedBonePositions, markerPosition);
            Assert.True(
                posedDistance <= baselineDistance + MaximumDistanceRegression,
                $"Pose '{markerName}' should not regress tracked-bone-to-target distance beyond tolerance. " +
                $"Baseline={baselineDistance:F4}, Posed={posedDistance:F4}.");
            Assert.InRange(posedDistance, 0.0f, MaximumResidualDistance);

            if (markerName == "TargetForward")
            {
                continue;
            }

            AssertNonForwardPosePositiveSignal(
                markerName,
                baselineDistance,
                posedDistance,
                neutralMarkerPosition,
                markerPosition,
                neutralBonePositions,
                posedBonePositions,
                trackedBoneIndices);
        }
    }

    private static IReadOnlyList<int> ResolveTrackedBoneIndices(Skeleton3D skeleton, Node ikNode)
    {
        var trackedIndices = new List<int>(capacity: 3);

        foreach (int chainBoneIndex in ResolveIkChainBoneIndices(skeleton, ikNode))
        {
            AddIfFound(trackedIndices, chainBoneIndex);
        }

        AddIfFound(trackedIndices, TryFindBoneIndexFromCandidates(skeleton, _headBoneNameCandidates));
        AddIfFound(trackedIndices, TryFindBoneIndexFromCandidates(skeleton, _neckBoneNameCandidates));
        AddIfFound(trackedIndices, TryFindBoneIndexFromCandidates(skeleton, _spineBoneNameCandidates));

        if (trackedIndices.Count > 0)
        {
            return trackedIndices;
        }

        int fallbackIndex = FindBoneIndexFromCandidates(
            skeleton,
            [.. _headBoneNameCandidates, .. _neckBoneNameCandidates, .. _spineBoneNameCandidates]);
        trackedIndices.Add(fallbackIndex);
        return trackedIndices;
    }

    private static IReadOnlyList<int> ResolveIkChainBoneIndices(Skeleton3D skeleton, Node ikNode)
    {
        int endBoneIndex = ResolveIkBoneIndex(skeleton, ikNode, "settings/0/end_bone", "settings/0/end_bone_name");
        int rootBoneIndex = ResolveIkBoneIndex(skeleton, ikNode, "settings/0/root_bone", "settings/0/root_bone_name");

        if (endBoneIndex < 0)
        {
            return [];
        }

        int maxChainLength = Math.Max(1, (int)ikNode.Get("settings/0/joint_count")) + 1;
        var chain = new List<int>(capacity: maxChainLength);

        int currentBoneIndex = endBoneIndex;
        while (currentBoneIndex >= 0 && chain.Count < maxChainLength)
        {
            chain.Add(currentBoneIndex);
            if (currentBoneIndex == rootBoneIndex)
            {
                break;
            }

            currentBoneIndex = skeleton.GetBoneParent(currentBoneIndex);
        }

        return chain;
    }

    private static int ResolveIkBoneIndex(
        Skeleton3D skeleton,
        Node ikNode,
        StringName indexPropertyName,
        StringName namePropertyName)
    {
        int configuredIndex = (int)ikNode.Get(indexPropertyName);
        if (configuredIndex >= 0)
        {
            return configuredIndex;
        }

        string configuredName = ((StringName)ikNode.Get(namePropertyName)).ToString();
        return string.IsNullOrWhiteSpace(configuredName)
            ? -1
            : skeleton.FindBone(configuredName);
    }

    private static void AddIfFound(ICollection<int> values, int value)
    {
        if (value < 0 || values.Contains(value))
        {
            return;
        }

        values.Add(value);
    }

    private static Dictionary<int, Vector3> CaptureTrackedBoneWorldPositions(
        Skeleton3D skeleton,
        IReadOnlyList<int> trackedBoneIndices)
    {
        var positions = new Dictionary<int, Vector3>(trackedBoneIndices.Count);
        foreach (int trackedBoneIndex in trackedBoneIndices)
        {
            positions[trackedBoneIndex] = ResolveTrackedBoneWorldPosition(skeleton, trackedBoneIndex);
        }

        return positions;
    }

    private static async Task<Dictionary<int, Vector3>> CaptureTrackedBoneWorldPositionsAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        IReadOnlyList<int> trackedBoneIndices)
    {
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
        return CaptureTrackedBoneWorldPositions(skeleton, trackedBoneIndices);
    }

    private static float ResolveClosestDistanceToTarget(IReadOnlyDictionary<int, Vector3> trackedBonePositions, Vector3 target)
    {
        float closestDistance = float.MaxValue;
        foreach (KeyValuePair<int, Vector3> trackedBonePosition in trackedBonePositions)
        {
            float currentDistance = trackedBonePosition.Value.DistanceTo(target);
            if (currentDistance < closestDistance)
            {
                closestDistance = currentDistance;
            }
        }

        return closestDistance;
    }

    private static void AssertNonForwardPosePositiveSignal(
        string markerName,
        float baselineDistance,
        float posedDistance,
        Vector3 neutralMarkerPosition,
        Vector3 posedMarkerPosition,
        IReadOnlyDictionary<int, Vector3> neutralBonePositions,
        IReadOnlyDictionary<int, Vector3> posedBonePositions,
        IReadOnlyList<int> trackedBoneIndices)
    {
        bool improvedDistance = posedDistance < baselineDistance - MinimumDistanceImprovement;

        bool movedInExpectedDirection = HasDirectionalMovementTowardsMarker(
            neutralMarkerPosition,
            posedMarkerPosition,
            neutralBonePositions,
            posedBonePositions,
            trackedBoneIndices,
            out float largestMovementMagnitude,
            out float largestDirectionalAlignment);

        Assert.True(
            improvedDistance || movedInExpectedDirection,
            $"Pose '{markerName}' must provide a positive non-forward signal by either reducing closest tracked-bone distance " +
            $"by more than {MinimumDistanceImprovement:F4} (baseline {baselineDistance:F4} -> posed {posedDistance:F4}) " +
            $"or moving at least one tracked bone with magnitude >= {MinimumExpectedMovement:F4} and alignment >= {MinimumDirectionalAlignment:F2}. " +
            $"Observed max movement={largestMovementMagnitude:F6}, max alignment={largestDirectionalAlignment:F6}.");
    }

    private static bool HasDirectionalMovementTowardsMarker(
        Vector3 neutralMarkerPosition,
        Vector3 posedMarkerPosition,
        IReadOnlyDictionary<int, Vector3> neutralBonePositions,
        IReadOnlyDictionary<int, Vector3> posedBonePositions,
        IReadOnlyList<int> trackedBoneIndices,
        out float largestMovementMagnitude,
        out float largestDirectionalAlignment)
    {
        Vector3 markerDelta = posedMarkerPosition - neutralMarkerPosition;
        largestMovementMagnitude = 0.0f;
        largestDirectionalAlignment = float.NegativeInfinity;

        if (markerDelta.LengthSquared() <= Mathf.Epsilon)
        {
            return false;
        }

        Vector3 expectedDirection = markerDelta.Normalized();

        foreach (int trackedBoneIndex in trackedBoneIndices)
        {
            Vector3 neutralBonePosition = neutralBonePositions[trackedBoneIndex];
            Vector3 posedBonePosition = posedBonePositions[trackedBoneIndex];
            Vector3 trackedBoneDelta = posedBonePosition - neutralBonePosition;

            float movementMagnitude = trackedBoneDelta.Length();
            if (movementMagnitude > largestMovementMagnitude)
            {
                largestMovementMagnitude = movementMagnitude;
            }

            if (movementMagnitude < MinimumExpectedMovement)
            {
                continue;
            }

            float directionalAlignment = trackedBoneDelta.Normalized().Dot(expectedDirection);
            if (directionalAlignment > largestDirectionalAlignment)
            {
                largestDirectionalAlignment = directionalAlignment;
            }

            if (directionalAlignment >= MinimumDirectionalAlignment)
            {
                return true;
            }
        }

        if (float.IsNegativeInfinity(largestDirectionalAlignment))
        {
            largestDirectionalAlignment = 0.0f;
        }

        return false;
    }

    private static Dictionary<string, Node3D> ResolveRequiredPoseMarkers(Node3D targetPoses)
    {
        var markerMap = new Dictionary<string, Node3D>(_requiredPoseMarkerNames.Length, StringComparer.Ordinal);
        foreach (string markerName in _requiredPoseMarkerNames)
        {
            Node3D marker = Assert.IsType<Node3D>(targetPoses.GetNodeOrNull(markerName));
            markerMap[markerName] = marker;
        }

        return markerMap;
    }

    private static Skeleton3D? FindFirstSkeleton(Node rootNode)
    {
        if (rootNode is Skeleton3D skeleton)
        {
            return skeleton;
        }

        foreach (Node child in rootNode.GetChildren())
        {
            Skeleton3D? childSkeleton = FindFirstSkeleton(child);
            if (childSkeleton is not null)
            {
                return childSkeleton;
            }
        }

        return null;
    }

    private static Node BindOrCreateIkNode(Skeleton3D skeleton, Node3D headTarget)
    {
        Node? ikNode = skeleton.GetNodeOrNull(IkNodeName);

        if (ikNode is null)
        {
            ikNode = LoadPackedScene(ReusableIkScenePath).Instantiate();
            skeleton.AddChild(ikNode);
        }

        NodePath targetPath = ikNode.GetPathTo(headTarget);
        ikNode.Set("settings/0/target_node", targetPath);
        SetBooleanPropertyIfAvailable(ikNode, "active", true);
        SetBooleanPropertyIfAvailable(ikNode, "enabled", true);

        return ikNode;
    }

    private static void SetBooleanPropertyIfAvailable(Node node, StringName propertyName, bool value)
    {
        foreach (Godot.Collections.Dictionary property in node.GetPropertyList())
        {
            if ((StringName)property["name"] != propertyName)
            {
                continue;
            }

            node.Set(propertyName, value);
            return;
        }
    }

    private static int FindBoneIndexFromCandidates(Skeleton3D skeleton, IReadOnlyList<string> candidates)
    {
        int index = TryFindBoneIndexFromCandidates(skeleton, candidates);
        return index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException(
                $"Expected at least one tracking bone from candidates: {string.Join(", ", candidates)}.");
    }

    private static int TryFindBoneIndexFromCandidates(Skeleton3D skeleton, IReadOnlyList<string> candidates)
    {
        foreach (string candidate in candidates)
        {
            int exactIndex = skeleton.FindBone(candidate);
            if (exactIndex >= 0)
            {
                return exactIndex;
            }
        }

        foreach (string candidate in candidates)
        {
            for (int boneIndex = 0; boneIndex < skeleton.GetBoneCount(); boneIndex++)
            {
                string boneName = skeleton.GetBoneName(boneIndex);
                if (boneName.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return boneIndex;
                }
            }
        }

        return -1;
    }

    private static Vector3 ResolveTrackedBoneWorldPosition(Skeleton3D skeleton, int boneIndex)
        => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static async Task SetHeadTargetToMarkerAsync(SceneTree sceneTree, Node3D headTarget, Node3D marker)
    {
        headTarget.GlobalTransform = marker.GlobalTransform;
        await WaitForFramesAsync(sceneTree, 1);
    }
}
