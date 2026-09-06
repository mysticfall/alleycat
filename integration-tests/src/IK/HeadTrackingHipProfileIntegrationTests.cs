using AlleyCat.IK.Pose;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Non-visual regression coverage for IK-004 direction-weighted hip reconciliation.
/// </summary>
public sealed class HeadTrackingHipProfileIntegrationTests
{
    private const string VerificationScenePath = "res://tests/ik/head_tracking_hip_profile_test.tscn";
    private const string SubjectPath = "Subject";
    private const string DriverPath = "PoseStateMachineDriver";
    private const string HipModifierPath = "Subject/Skeleton/HipReconciliationModifier";
    private const string ScenarioMarkersRootPath = "Markers/PoseStateMachine/Scenarios";
    private const string HeadRestMarkerPath = "Markers/PoseStateMachine/RestHeadTarget";
    private const string LeftHandRestMarkerPath = "Markers/PoseStateMachine/HandTargetRestLeft";
    private const string RightHandRestMarkerPath = "Markers/PoseStateMachine/HandTargetRestRight";
    private const string LeftFootTargetPath = "Subject/Targets/LeftFoot";
    private const string RightFootTargetPath = "Subject/Targets/RightFoot";
    private const string HeadIKTargetPath = "Subject/Targets/Head";
    private const string HeadIKSolveTargetPath = "Subject/Targets/HeadSolve";
    private const string SkeletonPath = "Subject/Skeleton";
    private const string ViewpointPath = "Subject/Targets/HeadSolve/Viewpoint";
    private const float MinimumVerticalHipDropMetres = 0.15f;
    private const float MinimumVerticalVsStoopHipDropDeltaMetres = 0.07f;
    private const float MaximumVerticalForwardHipTravelMetres = 0.08f;
    private const float MinimumHeadFollowFraction = 0.45f;
    private const float MinimumStoopVsLeanForwardOffsetMetres = 0.08f;
    // Alignment-driven vertical damping with MinimumAlignmentWeight=0.1 leaves a small residual
    // vertical shortfall (~0.05 m in practice) when a forward lean is added to a deep crouch.
    // This tolerance accommodates that residual while remaining far tighter than the buggy
    // spring-up behaviour it regression-guards against (which would exceed 0.15 m).
    private const float MaximumCrouchDepthLossOnForwardLeanMetres = 0.08f;
    private const float MinimumCrouchThenStoopForwardHeadOffsetMetres = 0.08f;
    private const float MaximumRepeatedVerticalCrouchForwardDriftMetres = 0.015f;
    private const float MaximumRepeatedVerticalCrouchHipOscillationMetres = 0.02f;

    /// <summary>
    /// Verifies the standing-family hip profile keeps a visibly stronger downward response for a
    /// vertical crouch than for a forward stoop, while also guarding against the stoop hips
    /// travelling so far forward that the torso loses its head-leading lean silhouette.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DirectionWeightedHipProfile_StoopVsVerticalCrouch_PreservesVerticalResponseAndConstrainsForwardTravel()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");
        await PrepareVerificationSceneAsync(sceneTree, sceneRoot);

        Node3D subject = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(SubjectPath), exactMatch: false);
        PoseStateMachineMarkerDriver driver = Assert.IsType<PoseStateMachineMarkerDriver>(
            sceneRoot.GetNodeOrNull(DriverPath),
            exactMatch: false);
        HipReconciliationModifier hipModifier = Assert.IsType<HipReconciliationModifier>(
            sceneRoot.GetNodeOrNull(HipModifierPath),
            exactMatch: false);
        Skeleton3D skeleton = Assert.IsType<Skeleton3D>(sceneRoot.GetNodeOrNull(SkeletonPath), exactMatch: false);

        Assert.Same(driver.GetDrivenStateMachine(), hipModifier.StateMachine);

        int hipsIndex = RequireBoneIndex(skeleton, "Hips");

        AssertScenarioMarkerSemantics(sceneRoot);

        ScenarioSnapshot standing = await ApplyScenarioAndCaptureAsync(sceneTree, sceneRoot, driver, skeleton, "Standing", hipsIndex);
        ScenarioSnapshot verticalCrouch = await ApplyScenarioAndCaptureAsync(sceneTree, sceneRoot, driver, skeleton, "VerticalCrouchStrong", hipsIndex);
        ScenarioSnapshot stoopForward = await ApplyScenarioAndCaptureAsync(sceneTree, sceneRoot, driver, skeleton, "StoopForward", hipsIndex);
        ScenarioSnapshot leanBack = await ApplyScenarioAndCaptureAsync(sceneTree, sceneRoot, driver, skeleton, "LeanBack", hipsIndex);
        ScenarioSnapshot crouchThenStoop = await ApplyScenarioAndCaptureAsync(sceneTree, sceneRoot, driver, skeleton, "CrouchThenStoopForward", hipsIndex);

        Assert.Equal("Standing", verticalCrouch.StateId);
        Assert.Equal("Standing", stoopForward.StateId);
        Assert.Equal("Standing", leanBack.StateId);
        Assert.Equal("Standing", crouchThenStoop.StateId);

        // The fixture viewpoint is a child of the downstream head solve target. This keeps the
        // marker-to-solve hand-off observable without introducing a full-character pipeline.
        AssertViewpointFollowsMarkerDelta(standing, verticalCrouch, "VerticalCrouchStrong");
        AssertViewpointFollowsMarkerDelta(standing, stoopForward, "StoopForward");
        AssertViewpointFollowsMarkerDelta(standing, leanBack, "LeanBack");
        AssertViewpointFollowsMarkerDelta(standing, crouchThenStoop, "CrouchThenStoopForward");

        float verticalHipDrop = standing.HipsWorldPosition.Y - verticalCrouch.HipsWorldPosition.Y;
        float stoopHipDrop = standing.HipsWorldPosition.Y - stoopForward.HipsWorldPosition.Y;
        Assert.True(
            verticalHipDrop >= MinimumVerticalHipDropMetres,
            $"Vertical crouch should preserve strong downward hip response (observed drop {verticalHipDrop:F4} m).");
        Assert.True(
            verticalHipDrop >= stoopHipDrop + MinimumVerticalVsStoopHipDropDeltaMetres,
            $"Vertical crouch should drop hips more than stoop-forward by at least {MinimumVerticalVsStoopHipDropDeltaMetres:F2} m. " +
            $"Observed vertical={verticalHipDrop:F4} m, stoop={stoopHipDrop:F4} m.");

        float verticalForwardHipTravel = Mathf.Abs(verticalCrouch.HipsWorldPosition.Z - standing.HipsWorldPosition.Z);
        Assert.True(
            verticalForwardHipTravel <= MaximumVerticalForwardHipTravelMetres,
            $"Vertical crouch should remain mostly vertical rather than drifting strongly forward. " +
            $"Observed forward hip travel={verticalForwardHipTravel:F4} m.");

        // Convert world-space head positions into the minimal subject's local frame so forward/back
        // sign is independent of the subject's world orientation. Its local -Z is forward.
        Transform3D subjectInverse = subject.GlobalTransform.AffineInverse();
        Vector3 standingHeadLocal = subjectInverse * standing.ViewpointWorldPosition;
        Vector3 stoopHeadLocal = subjectInverse * stoopForward.ViewpointWorldPosition;
        Vector3 leanHeadLocal = subjectInverse * leanBack.ViewpointWorldPosition;
        Vector3 crouchThenStoopHeadLocal = subjectInverse * crouchThenStoop.ViewpointWorldPosition;

        float stoopForwardOffset = standingHeadLocal.Z - stoopHeadLocal.Z;
        Assert.True(
            stoopForwardOffset >= MinimumStoopVsLeanForwardOffsetMetres,
            $"Stoop-forward should place the head noticeably in front of standing in the subject's local frame. " +
            $"standing.z_local={standingHeadLocal.Z:F4}, stoop.z_local={stoopHeadLocal.Z:F4}.");

        float leanBackOffset = leanHeadLocal.Z - standingHeadLocal.Z;
        Assert.True(
            leanBackOffset >= MinimumStoopVsLeanForwardOffsetMetres,
            $"Lean-back should place the head noticeably behind standing in the subject's local frame. " +
            $"standing.z_local={standingHeadLocal.Z:F4}, lean.z_local={leanHeadLocal.Z:F4}.");

        // IK-004 regression: adding a forward lean to an existing crouch must NOT spring the
        // hips back up to the standing height. The crouch-then-stoop hip drop must remain at
        // least as deep as the pure-vertical crouch drop (within a small residual tolerance for
        // alignment-driven damping), not shrink back towards zero.
        float crouchThenStoopHipDrop = standing.HipsWorldPosition.Y - crouchThenStoop.HipsWorldPosition.Y;
        Assert.True(
            crouchThenStoopHipDrop >= verticalHipDrop - MaximumCrouchDepthLossOnForwardLeanMetres,
            $"Forward lean while crouched must not restore hip height. " +
            $"verticalHipDrop={verticalHipDrop:F4} m, crouchThenStoopHipDrop={crouchThenStoopHipDrop:F4} m.");

        float crouchThenStoopForwardOffset = standingHeadLocal.Z - crouchThenStoopHeadLocal.Z;
        Assert.True(
            crouchThenStoopForwardOffset >= MinimumCrouchThenStoopForwardHeadOffsetMetres,
            $"Crouch-then-stoop should place the head noticeably in front of standing in the subject's local frame. " +
            $"standing.z_local={standingHeadLocal.Z:F4}, crouchThenStoop.z_local={crouchThenStoopHeadLocal.Z:F4}.");
    }

    /// <summary>
    /// Verifies repeated pure vertical crouch input remains stable rather than drifting forward or oscillating.
    /// </summary>
    [Headless]
    [Fact]
    public async Task DirectionWeightedHipProfile_RepeatedVerticalCrouch_RemainsStable()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(VerificationScenePath));
        Assert.Equal(Error.Ok, changeSceneError);

        await WaitForFramesAsync(sceneTree, 2);

        Node sceneRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected verification scene to become current scene.");
        await PrepareVerificationSceneAsync(sceneTree, sceneRoot);

        PoseStateMachineMarkerDriver driver = Assert.IsType<PoseStateMachineMarkerDriver>(
            sceneRoot.GetNodeOrNull(DriverPath),
            exactMatch: false);
        Skeleton3D skeleton = Assert.IsType<Skeleton3D>(sceneRoot.GetNodeOrNull(SkeletonPath), exactMatch: false);
        int hipsIndex = RequireBoneIndex(skeleton, "Hips");

        var samples = new List<Vector3>();
        for (int iteration = 0; iteration < 12; iteration++)
        {
            _ = TickScenario(sceneRoot, driver, "VerticalCrouchStrong");
            await WaitForFramesAsync(sceneTree, 2);
            _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);
            samples.Add(ResolveBoneWorldPosition(skeleton, hipsIndex));
        }

        Vector3 first = samples[0];
        float forwardDrift = 0f;
        float verticalOscillation = 0f;

        for (int i = 1; i < samples.Count; i++)
        {
            forwardDrift = Mathf.Max(forwardDrift, Mathf.Abs(samples[i].Z - first.Z));
            verticalOscillation = Mathf.Max(verticalOscillation, Mathf.Abs(samples[i].Y - first.Y));
        }

        Assert.True(
            forwardDrift <= MaximumRepeatedVerticalCrouchForwardDriftMetres,
            $"Repeated vertical crouch should not drift forward over time. Observed drift={forwardDrift:F4} m.");
        Assert.True(
            verticalOscillation <= MaximumRepeatedVerticalCrouchHipOscillationMetres,
            $"Repeated vertical crouch should remain vertically stable. Observed oscillation={verticalOscillation:F4} m.");
    }

    private static void AssertViewpointFollowsMarkerDelta(
        ScenarioSnapshot standing,
        ScenarioSnapshot scenario,
        string scenarioName)
    {
        Vector3 markerDelta = scenario.MarkerWorldPosition - standing.MarkerWorldPosition;
        Vector3 viewpointDelta = scenario.ViewpointWorldPosition - standing.ViewpointWorldPosition;
        float markerDeltaLength = markerDelta.Length();

        Assert.True(
            markerDeltaLength > 1e-3f,
            $"Test setup expected scenario '{scenarioName}' marker to differ from standing marker.");

        // If the fixture head solve target is not driven by the scenario marker, the viewpoint
        // stays near its rest location and the projected follow-through collapses towards zero.
        float projection = viewpointDelta.Dot(markerDelta) / markerDeltaLength;
        float followFraction = projection / markerDeltaLength;

        Assert.True(
            followFraction >= MinimumHeadFollowFraction,
            $"Scenario '{scenarioName}' viewpoint must follow the marker delta direction by at least " +
            $"{MinimumHeadFollowFraction:F2}× the marker magnitude. Observed follow fraction={followFraction:F4} " +
            $"(viewpointDelta={viewpointDelta}, markerDelta={markerDelta}).");
    }

    private static async Task PrepareVerificationSceneAsync(SceneTree sceneTree, Node sceneRoot)
    {
        await WaitForFramesAsync(sceneTree, 2);

        PoseStateMachineMarkerDriver driver = Assert.IsType<PoseStateMachineMarkerDriver>(
            sceneRoot.GetNodeOrNull(DriverPath),
            exactMatch: false);
        HipReconciliationModifier hipModifier = Assert.IsType<HipReconciliationModifier>(
            sceneRoot.GetNodeOrNull(HipModifierPath),
            exactMatch: false);

        hipModifier.StateMachine = driver.GetDrivenStateMachine();
        Assert.Equal("Standing", driver.GetCurrentStateId().ToString());
        Assert.Same(driver.GetDrivenStateMachine(), hipModifier.StateMachine);
    }

    private static async Task<ScenarioSnapshot> ApplyScenarioAndCaptureAsync(
        SceneTree sceneTree,
        Node sceneRoot,
        PoseStateMachineMarkerDriver driver,
        Skeleton3D skeleton,
        string scenarioName,
        int hipsIndex)
    {
        Vector3 markerWorldPosition = TickScenario(sceneRoot, driver, scenarioName);
        Node3D viewpoint = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ViewpointPath), exactMatch: false);

        await WaitForFramesAsync(sceneTree, 6);
        _ = await sceneTree.ToSignal(skeleton, Skeleton3D.SignalName.SkeletonUpdated);

        return new ScenarioSnapshot(
            driver.GetCurrentStateId().ToString(),
            ResolveBoneWorldPosition(skeleton, hipsIndex),
            viewpoint.GlobalPosition,
            markerWorldPosition);
    }

    private static Vector3 TickScenario(Node sceneRoot, PoseStateMachineMarkerDriver driver, string scenarioName)
    {
        Node3D scenariosRoot = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ScenarioMarkersRootPath), exactMatch: false);
        Node3D scenarioNode = Assert.IsType<Node3D>(
            scenariosRoot.GetNodeOrNull(new NodePath(scenarioName)),
            exactMatch: false);
        Node3D headRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HeadRestMarkerPath), exactMatch: false);
        Node3D leftHandRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftHandRestMarkerPath), exactMatch: false);
        Node3D rightHandRestMarker = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightHandRestMarkerPath), exactMatch: false);
        Node3D leftFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(LeftFootTargetPath), exactMatch: false);
        Node3D rightFootTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(RightFootTargetPath), exactMatch: false);
        Node3D headIKTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HeadIKTargetPath), exactMatch: false);
        Node3D headIKSolveTarget = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(HeadIKSolveTargetPath), exactMatch: false);

        // Drive both the authored head target and its direct fixture solve target with the scenario
        // marker transform. The fixture intentionally represents only this target hand-off.
        headIKTarget.GlobalTransform = scenarioNode.GlobalTransform;
        headIKSolveTarget.GlobalTransform = scenarioNode.GlobalTransform;

        driver.TickPoseTargets(
            scenarioNode.GlobalTransform,
            leftHandRestMarker.GlobalTransform,
            rightHandRestMarker.GlobalTransform,
            leftFootTarget.GlobalTransform,
            rightFootTarget.GlobalTransform,
            headRestMarker.GlobalTransform,
            -1,
            -1.0);

        return scenarioNode.GlobalTransform.Origin;
    }

    private static int RequireBoneIndex(Skeleton3D skeleton, string boneName)
    {
        int boneIndex = skeleton.FindBone(boneName);
        Assert.True(boneIndex >= 0, $"Expected skeleton bone '{boneName}' to exist.");
        return boneIndex;
    }

    private static Vector3 ResolveBoneWorldPosition(Skeleton3D skeleton, int boneIndex)
        => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIndex).Origin;

    private static void AssertScenarioMarkerSemantics(Node sceneRoot)
    {
        Node3D scenariosRoot = Assert.IsType<Node3D>(sceneRoot.GetNodeOrNull(ScenarioMarkersRootPath), exactMatch: false);
        Node3D standing = Assert.IsType<Node3D>(scenariosRoot.GetNodeOrNull(new NodePath("Standing")), exactMatch: false);
        Node3D stoopForward = Assert.IsType<Node3D>(scenariosRoot.GetNodeOrNull(new NodePath("StoopForward")), exactMatch: false);
        Node3D leanBack = Assert.IsType<Node3D>(scenariosRoot.GetNodeOrNull(new NodePath("LeanBack")), exactMatch: false);

        Assert.True(
            stoopForward.GlobalPosition.Z < standing.GlobalPosition.Z,
            $"StoopForward marker should sit in front of standing along negative Z. standing.z={standing.GlobalPosition.Z:F4}, stoop.z={stoopForward.GlobalPosition.Z:F4}.");
        Assert.True(
            leanBack.GlobalPosition.Z > standing.GlobalPosition.Z,
            $"LeanBack marker should sit behind standing along positive Z. standing.z={standing.GlobalPosition.Z:F4}, lean.z={leanBack.GlobalPosition.Z:F4}.");
        Assert.True(
            stoopForward.GlobalPosition.Y < standing.GlobalPosition.Y,
            $"StoopForward marker should sit lower than standing. standing.y={standing.GlobalPosition.Y:F4}, stoop.y={stoopForward.GlobalPosition.Y:F4}.");
        Assert.True(
            leanBack.GlobalPosition.Y > stoopForward.GlobalPosition.Y,
            $"LeanBack marker should sit higher than StoopForward. lean.y={leanBack.GlobalPosition.Y:F4}, stoop.y={stoopForward.GlobalPosition.Y:F4}.");
    }

    private sealed record ScenarioSnapshot(
        string StateId,
        Vector3 HipsWorldPosition,
        Vector3 ViewpointWorldPosition,
        Vector3 MarkerWorldPosition);
}
