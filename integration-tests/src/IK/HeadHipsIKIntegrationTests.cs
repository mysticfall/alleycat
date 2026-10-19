using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Non-visual integration coverage for IK-001 neck-spine CCDIK behaviour, sampled through a deterministic
/// synthetic solver-time budget so results do not depend on host frame pacing.
/// </summary>
public sealed class HeadHipsIKIntegrationTests
{
    private const string VerificationScenePath = "res://tests/ik/head_hips_ik_test.tscn";
    private const string MarkersRootPath = "Markers";
    private const string HeadTargetPath = "Markers/HeadTarget";
    private const string TargetPosesPath = "Markers/TargetPoses";
    private const string IkNodeName = "NeckSpineIK";
    private static readonly string[] _headBoneNameCandidates = ["Head"];
    private static readonly string[] _neckBoneNameCandidates = ["Neck"];
    private static readonly string[] _spineBoneNameCandidates = ["Spine3", "Spine2", "Spine1", "Spine"];
    private const float MaximumDistanceRegression = 0.01f;
    private const float MaximumResidualDistance = 1.5f;

    // Sampling contract: each pose is driven through a fixed synthetic solver budget of
    // SyntheticSolveStepsPerPose steps x CCDIKTargetSynchronisation.SyntheticSolveDeltaSeconds (4 x 1/60 s),
    // matching the pre-rewrite four-frame settling contract. Response thresholds below are the historical
    // single-pass values (0.001 / 0.0001 per ~16 ms pass) scaled by the same factor, so the required
    // measurable response per unit of solver time is unchanged. The long-standing pre-rewrite contract (at
    // 1ed2ded6) was 4 engine frames of settling with unscaled 0.001/0.0001 thresholds, so the scaled values
    // (0.004/0.0004 over 4 synthetic passes) are strictly tighter than that oldest contract, not merely
    // per-pass-equivalent.
    private const int SyntheticSolveStepsPerPose = 4;
    private const int SyntheticStepFramePumps = 2;
    private const float MinimumDistanceImprovement = 0.004f;
    private const float MinimumDirectionalAlignment = 0.0f;
    private const float MinimumExpectedMovement = 0.0004f;
    private const string StoopForwardPoseName = "TargetStoopForward";

    // Compare travel only after independently verifying cap contact for both saturated inputs.
    // The ordinary forward and stoop markers are not assumed to saturate the current chain.
    private const float MaximumStoopForwardSaturatedTravelMetres = 0.001f;

    // Engagement teeth for the stoop pose: the curl itself must be solver-driven, not a resting
    // skeleton. The end-chain bones must sit measurably off their engine rest pose toward the stoop
    // demand. A dead or
    // unbound solver leaves the bones at rest (0 m) and fails; the sibling poses' positive-signal
    // assertions (Left/Right/LeanBack) additionally pin solver liveness in both pose-order variants.
    private const float MinimumStoopForwardRestTravelMetres = 0.05f;
    private const float MinimumStoopForwardRestTravelAlignment = 0.6f;

    private static readonly string[] _requiredPoseMarkerNames =
    [
        "TargetForward",
        "TargetLeft",
        "TargetRight",
        "TargetStoopForward",
        "TargetLeanBack",
    ];

    private static readonly string[][] _poseOrderVariants =
    [
        _requiredPoseMarkerNames,
        [.. _requiredPoseMarkerNames.Reverse()],
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
        Assert.Equal(Skeleton3D.ModifierCallbackModeProcessEnum.Idle, skeleton.ModifierCallbackModeProcess);

        // Sample the solver through a deterministic synthetic-time budget rather than real frame pacing:
        // real-frame sampling made the measured response depend on await-continuation timing relative to
        // the engine's deferred skeleton updates. Manual callback mode stops the engine's per-frame advance
        // so only the test's Skeleton3D.Advance steps drive the solve, and freezing the animation tree keeps
        // the base pose constant between samples.
        skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
        verificationSceneRoot.FindChild("AnimationTree", owned: true, recursive: true)?.Set("active", false);

        Node ikNode = BindLocalIkNode(skeleton, headTarget);
        NodePath expectedTargetPath = ikNode.GetPathTo(headTarget);

        Assert.False(expectedTargetPath.IsEmpty, "Expected a non-empty CCDIK target path to HeadTarget.");

        var configuredTargetPath = (NodePath)ikNode.Get("settings/0/target_node");
        Assert.Equal(expectedTargetPath, configuredTargetPath);

        IReadOnlyList<int> trackedBoneIndices = ResolveTrackedBoneIndices(skeleton, ikNode);
        SkeletonModifier3D neckSpineIK = Assert.IsAssignableFrom<SkeletonModifier3D>(ikNode);
        Dictionary<int, Vector3> restBonePositions = CaptureRestBoneWorldPositions(skeleton, trackedBoneIndices);
        int headBoneIndex = RequireTrackedBoneIndex(skeleton, trackedBoneIndices, _headBoneNameCandidates);
        int neckBoneIndex = RequireTrackedBoneIndex(skeleton, trackedBoneIndices, _neckBoneNameCandidates);

        await AssertStaleSignalTimeTargetIsRejectedAsync(
            sceneTree,
            neckSpineIK,
            headTarget,
            poseMarkers["TargetForward"]);

        foreach (IReadOnlyList<string> poseOrder in _poseOrderVariants)
        {
            await ValidatePoseOrderAsync(
                sceneTree,
                skeleton,
                headTarget,
                poseMarkers,
                poseOrder,
                trackedBoneIndices,
                restBonePositions,
                headBoneIndex,
                neckBoneIndex);
        }

        await AssertSaturatedTravelAsync(sceneTree, skeleton, headTarget, poseMarkers[StoopForwardPoseName],
            trackedBoneIndices, restBonePositions, headBoneIndex, neckBoneIndex);
    }

    private static async Task AssertSaturatedTravelAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        Node3D headTarget,
        Node3D stoopMarker,
        IReadOnlyList<int> trackedBoneIndices,
        IReadOnlyDictionary<int, Vector3> restBonePositions,
        int headBoneIndex,
        int neckBoneIndex)
    {
        string[] jointNames = ["Spine", "Chest", "UpperChest", "Neck"];
        float[] halfCapsDegrees = [8f, 8f, 14f, 16f];
        // Native limits constrain deviation from the frozen incoming pose, not authored rest
        // (IK-001 TR 6). This is a cap-contact prerequisite, not rest-relative safety acceptance.
        Basis[] incomingBases = [.. jointNames.Select(name =>
        {
            int bone = skeleton.FindBone(name);
            return (skeleton.GetBoneGlobalPose(skeleton.GetBoneParent(bone)).AffineInverse()
                * skeleton.GetBoneGlobalPose(bone)).Basis;
        })];
        int spineIndex = skeleton.FindBone("Spine");
        Vector3 origin = restBonePositions[spineIndex];
        // In the reference rig avatar-forward is skeleton +Z. A down/forward ray lies beyond
        // the entire 46-degree cumulative pitch envelope. Both demands exceed the chain's
        // total rest length; neither is derived from solved bones. Increasing distance along
        // this ray must not recruit further bend once all four pitch caps are engaged.
        Vector3 direction = (skeleton.GlobalBasis.Orthonormalized() * new Vector3(0f, -1f, 1f)).Normalized();
        float chainLength = 0f;
        int child = headBoneIndex;
        while (child != spineIndex)
        {
            int parent = skeleton.GetBoneParent(child);
            Assert.True(parent >= 0, "Expected Head to descend from Spine.");
            chainLength += skeleton.GetBoneGlobalRest(child).Origin.DistanceTo(
                skeleton.GetBoneGlobalRest(parent).Origin);
            child = parent;
        }

        using Node3D marker = new();
        skeleton.AddChild(marker);
        marker.GlobalBasis = stoopMarker.GlobalBasis;
        Dictionary<int, Vector3>? saturatedPositions = null;
        var observations = new List<(float[] Pitches, Dictionary<int, Vector3> Positions)>();
        void CaptureSaturatedOutput()
        {
            float[] pitches = new float[jointNames.Length];
            for (int joint = 0; joint < jointNames.Length; joint++)
            {
                int bone = skeleton.FindBone(jointNames[joint]);
                Transform3D local = skeleton.GetBoneGlobalPose(skeleton.GetBoneParent(bone)).AffineInverse()
                    * skeleton.GetBoneGlobalPose(bone);
                Basis deviation = incomingBases[joint].Inverse() * local.Basis;
                Vector3 up = deviation.Orthonormalized() * Vector3.Up;
                pitches[joint] = Mathf.RadToDeg(Mathf.Atan2(up.Z, up.Y));
            }

            observations.Add((pitches, CaptureTrackedBoneWorldPositions(skeleton, trackedBoneIndices)));
        }

        try
        {
            // Two distinct unreachable inputs, each with the existing four-step observation window.
            foreach (float distance in new[] { 10f, 20f })
            {
                marker.GlobalPosition = origin + (direction * distance);
                Assert.True(distance > chainLength, "Saturated input must lie beyond total chain length.");
                observations.Clear();
                skeleton.SkeletonUpdated += CaptureSaturatedOutput;
                Dictionary<int, Vector3> positions;
                try
                {
                    positions = await SetHeadTargetToMarkerAsync(
                        sceneTree, skeleton, headTarget, marker, trackedBoneIndices);
                }
                finally
                {
                    skeleton.SkeletonUpdated -= CaptureSaturatedOutput;
                }

                Assert.Equal(SyntheticSolveStepsPerPose, observations.Count);
                foreach ((float[] pitches, Dictionary<int, Vector3> sample) in observations)
                {
                    RecordMeasurement($"saturation,{distance},pitch-degrees,{string.Join(';', pitches.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}");
                    for (int joint = 0; joint < jointNames.Length; joint++)
                    {
                        Assert.True(Mathf.Abs(pitches[joint] - halfCapsDegrees[joint]) <= 0.1f,
                            $"Saturation premise: {jointNames[joint]} forward pitch {pitches[joint]:F6}° must contact " +
                            $"its authored {halfCapsDegrees[joint]:F1}° half-cap (±0.1°).");
                    }

                    if (saturatedPositions is not null)
                    {
                        foreach (int bone in trackedBoneIndices)
                        {
                            float travel = saturatedPositions[bone].DistanceTo(sample[bone]);
                            RecordMeasurement(FormattableString.Invariant($"saturation,{distance},{skeleton.GetBoneName(bone)},travel-metres,{travel:R}"));
                            Assert.True(travel <= MaximumStoopForwardSaturatedTravelMetres,
                                $"Saturated demand must preserve '{skeleton.GetBoneName(bone)}': " +
                                $"travel {travel:F6} m exceeds {MaximumStoopForwardSaturatedTravelMetres:F4} m.");
                        }
                    }
                }

                AssertStoopForwardSolverDrivenCurl(positions, restBonePositions, direction, headBoneIndex, "end (Head)");
                AssertStoopForwardSolverDrivenCurl(positions, restBonePositions, direction, neckBoneIndex, "distal (Neck)");
                saturatedPositions ??= positions;
            }

        }
        finally
        {
            skeleton.RemoveChild(marker);
        }
    }

    private static async Task ValidatePoseOrderAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        Node3D headTarget,
        IReadOnlyDictionary<string, Node3D> poseMarkers,
        IReadOnlyList<string> poseOrder,
        IReadOnlyList<int> trackedBoneIndices,
        IReadOnlyDictionary<int, Vector3> restBonePositions,
        int headBoneIndex,
        int neckBoneIndex)
    {
        Dictionary<int, Vector3> neutralBonePositions = await SetHeadTargetToMarkerAsync(
            sceneTree,
            skeleton,
            headTarget,
            poseMarkers["TargetForward"],
            trackedBoneIndices);
        Vector3 neutralMarkerPosition = poseMarkers["TargetForward"].GlobalPosition;
        float neutralResidualDistance = ResolveClosestDistanceToTarget(neutralBonePositions, neutralMarkerPosition);
        Assert.InRange(neutralResidualDistance, 0.0f, MaximumResidualDistance);

        foreach (string markerName in poseOrder)
        {
            Dictionary<int, Vector3> posedBonePositions = await SetHeadTargetToMarkerAsync(
                sceneTree,
                skeleton,
                headTarget,
                poseMarkers[markerName],
                trackedBoneIndices);
            Vector3 markerPosition = poseMarkers[markerName].GlobalPosition;

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
                trackedBoneIndices,
                restBonePositions,
                headBoneIndex,
                neckBoneIndex);
        }
    }

    private static async Task AssertStaleSignalTimeTargetIsRejectedAsync(
        SceneTree sceneTree,
        SkeletonModifier3D neckSpineIK,
        Node3D headTarget,
        Node3D marker)
    {
        Transform3D staleTransform = new(marker.GlobalBasis, marker.GlobalPosition + Vector3.Up);
        void ReplaceTargetWithStaleTransform()
        {
            headTarget.GlobalTransform = staleTransform;
            headTarget.ForceUpdateTransform();
        }

        neckSpineIK.ModificationProcessed += ReplaceTargetWithStaleTransform;
        try
        {
            Xunit.Sdk.XunitException exception = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(
                () => CCDIKTargetSynchronisation.AssignTargetAndWaitForCurrentCCDIKAsync(
                    sceneTree,
                    neckSpineIK,
                    headTarget,
                    marker));
            Assert.Contains("stale HeadTarget transform", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            neckSpineIK.ModificationProcessed -= ReplaceTargetWithStaleTransform;
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
        IReadOnlyList<int> trackedBoneIndices,
        IReadOnlyDictionary<int, Vector3> restBonePositions,
        int headBoneIndex,
        int neckBoneIndex)
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

        if (string.Equals(markerName, StoopForwardPoseName, StringComparison.Ordinal))
        {
            // These ordinary markers retain meaningful engagement coverage independently of the
            // separate cap-contact saturation scenario; neither marker is presumed saturated.
            Vector3 markerDelta = posedMarkerPosition - neutralMarkerPosition;
            AssertStoopForwardSolverDrivenCurl(
                posedBonePositions,
                restBonePositions,
                markerDelta,
                headBoneIndex,
                "end (Head)");
            AssertStoopForwardSolverDrivenCurl(
                posedBonePositions,
                restBonePositions,
                markerDelta,
                neckBoneIndex,
                "distal (Neck)");
            return;
        }

        Assert.True(
            improvedDistance || movedInExpectedDirection,
            $"Pose '{markerName}' must provide a positive non-forward signal by either reducing closest tracked-bone distance " +
            $"by more than {MinimumDistanceImprovement:F4} (baseline {baselineDistance:F4} -> posed {posedDistance:F4}) " +
            $"or moving at least one tracked bone with magnitude >= {MinimumExpectedMovement:F4} and alignment >= {MinimumDirectionalAlignment:F2}. " +
            $"Observed max movement={largestMovementMagnitude:F6}, max alignment={largestDirectionalAlignment:F6}.");
    }

    private static void AssertStoopForwardSolverDrivenCurl(
        IReadOnlyDictionary<int, Vector3> posedBonePositions,
        IReadOnlyDictionary<int, Vector3> restBonePositions,
        Vector3 markerDelta,
        int boneIndex,
        string roleLabel)
    {
        if (markerDelta.LengthSquared() <= Mathf.Epsilon)
        {
            throw new Xunit.Sdk.XunitException(
                $"Pose '{StoopForwardPoseName}' has a degenerate marker delta; the stoop contract cannot be evaluated.");
        }

        Vector3 restTravel = posedBonePositions[boneIndex] - restBonePositions[boneIndex];
        float alignment = restTravel.Normalized().Dot(markerDelta.Normalized());
        RecordMeasurement(FormattableString.Invariant($"engagement,{roleLabel},rest-travel-metres,{restTravel.Length():R},alignment,{alignment:R}"));
        Assert.True(
            restTravel.Length() >= MinimumStoopForwardRestTravelMetres && alignment >= MinimumStoopForwardRestTravelAlignment,
            $"Pose '{StoopForwardPoseName}' must sit solver-driven off its engine rest pose toward the stoop demand: " +
            $"{roleLabel} bone rest travel {restTravel.Length():F4} m (required >= {MinimumStoopForwardRestTravelMetres:F4}), " +
            $"alignment {alignment:F4} (required >= {MinimumStoopForwardRestTravelAlignment:F2}).");
    }

    private static void RecordMeasurement(string measurement)
    {
        // Optional numerical evidence for targeted fixture verification; ordinary suite runs do not write files.
        string? path = System.Environment.GetEnvironmentVariable("ALLEYCAT_HEADHIPS_MEASUREMENTS");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, measurement + System.Environment.NewLine);
        }
    }

    private static Dictionary<int, Vector3> CaptureRestBoneWorldPositions(
        Skeleton3D skeleton,
        IReadOnlyList<int> trackedBoneIndices)
    {
        Dictionary<int, Vector3> restPositions = new(trackedBoneIndices.Count);
        foreach (int trackedBoneIndex in trackedBoneIndices)
        {
            restPositions[trackedBoneIndex] =
                skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(trackedBoneIndex).Origin;
        }

        return restPositions;
    }

    private static int RequireTrackedBoneIndex(
        Skeleton3D skeleton,
        IReadOnlyList<int> trackedBoneIndices,
        string[] boneNameCandidates)
    {
        int boneIndex = TryFindBoneIndexFromCandidates(skeleton, boneNameCandidates);
        Assert.True(
            boneIndex >= 0 && trackedBoneIndices.Contains(boneIndex),
            $"Expected a tracked chain bone from candidates ({string.Join(", ", boneNameCandidates)}) " +
            "for the stoop solver-driven curl assertion.");
        return boneIndex;
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
        return HasDirectionalMovementTowardsMarker(
            neutralMarkerPosition,
            posedMarkerPosition,
            neutralBonePositions,
            posedBonePositions,
            trackedBoneIndices,
            MinimumExpectedMovement,
            MinimumDirectionalAlignment,
            out largestMovementMagnitude,
            out largestDirectionalAlignment);
    }

    private static bool HasDirectionalMovementTowardsMarker(
        Vector3 neutralMarkerPosition,
        Vector3 posedMarkerPosition,
        IReadOnlyDictionary<int, Vector3> neutralBonePositions,
        IReadOnlyDictionary<int, Vector3> posedBonePositions,
        IReadOnlyList<int> trackedBoneIndices,
        float minimumMovementMagnitude,
        float minimumDirectionalAlignment,
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

            if (movementMagnitude < minimumMovementMagnitude)
            {
                continue;
            }

            float directionalAlignment = trackedBoneDelta.Normalized().Dot(expectedDirection);
            if (directionalAlignment > largestDirectionalAlignment)
            {
                largestDirectionalAlignment = directionalAlignment;
            }

            if (directionalAlignment >= minimumDirectionalAlignment)
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

    private static Node BindLocalIkNode(Skeleton3D skeleton, Node3D headTarget)
    {
        Node ikNode = skeleton.GetNodeOrNull(IkNodeName)
            ?? throw new Xunit.Sdk.XunitException("Expected the subject skeleton to author a local NeckSpineIK node.");
        Assert.True(ikNode.IsClass("CCDIK3D"), "Expected the local NeckSpineIK node to be a CCDIK3D.");

        NodePath targetPath = ikNode.GetPathTo(headTarget);
        ikNode.Set("settings/0/target_node", targetPath);
        CopyTransformModifier3D headCopyRotation = Assert.IsType<CopyTransformModifier3D>(
            skeleton.GetNodeOrNull("HeadCopyRotation"));
        headCopyRotation.Set("settings/0/reference_node", headCopyRotation.GetPathTo(headTarget));
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

    private static async Task<Dictionary<int, Vector3>> CaptureTrackedBoneWorldPositionsAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        IReadOnlyList<int> trackedBoneIndices)
    {
        Dictionary<int, Vector3>? capturedPositions = null;
        int skeletonUpdateCount = 0;
        void CaptureOnSkeletonUpdate()
        {
            skeletonUpdateCount++;
            capturedPositions = CaptureTrackedBoneWorldPositions(skeleton, trackedBoneIndices);
        }

        skeleton.SkeletonUpdated += CaptureOnSkeletonUpdate;
        try
        {
            for (int step = 0; step < SyntheticSolveStepsPerPose; step++)
            {
                skeleton.Advance(CCDIKTargetSynchronisation.SyntheticSolveDeltaSeconds);
                await WaitForFramesAsync(sceneTree, SyntheticStepFramePumps);
            }
        }
        finally
        {
            skeleton.SkeletonUpdated -= CaptureOnSkeletonUpdate;
        }

        Assert.Equal(
            SyntheticSolveStepsPerPose,
            skeletonUpdateCount);

        return capturedPositions
            ?? throw new Xunit.Sdk.XunitException("Expected the synthetic solve to update the skeleton.");
    }

    private static Dictionary<int, Vector3> CaptureTrackedBoneWorldPositions(
        Skeleton3D skeleton,
        IReadOnlyList<int> trackedBoneIndices)
    {
        var positions = new Dictionary<int, Vector3>(trackedBoneIndices.Count);
        foreach (int trackedBoneIndex in trackedBoneIndices)
        {
            positions[trackedBoneIndex] = skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(trackedBoneIndex).Origin;
        }

        return positions;
    }

    private static Task<Dictionary<int, Vector3>> SetHeadTargetToMarkerAsync(
        SceneTree sceneTree,
        Skeleton3D skeleton,
        Node3D headTarget,
        Node3D marker,
        IReadOnlyList<int> trackedBoneIndices)
    {
        headTarget.GlobalTransform = marker.GlobalTransform;
        headTarget.ForceUpdateTransform();

        return CaptureTrackedBoneWorldPositionsAsync(sceneTree, skeleton, trackedBoneIndices);
    }
}
