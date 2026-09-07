using System.Text.Json;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK.Prototype;

/// <summary>
/// Windowed Stage 5 synthetic evidence. Direct reference-rig imports and the disposable fixture are the only runtime inputs.
/// </summary>
public sealed class LocomotionContactLockPrototypeIntegrationTests
{
    private const string FemaleRigPath = "res://assets/characters/reference/female/reference_female.blend";
    private const string MaleRigPath = "res://assets/characters/reference/male/reference_male.blend";
    private const string VisualSnapshotResourcePath = "res://temp/IK-003/stage5_contact_lock_prototype/visual_snapshots.json";
    private const string VisualCaptureOutputPath = "res://temp/IK-003/stage5_contact_lock_prototype/stage5_review_final/";
    private static readonly PrototypeRig[] _referenceRigs =
    [
        new("Female", FemaleRigPath, "Female/GeneralSkeleton", new Vector3(-0.32f, 0f, 0f)),
        new("Male", MaleRigPath, "Male/GeneralSkeleton", new Vector3(0.32f, 0f, 0f)),
    ];

    /// <summary>
    /// Every unavailable pre-IK evidence state prevents synthetic target publication on both direct reference rigs.
    /// </summary>
    [Fact]
    public async Task BothReferenceRigs_InvalidMissingAndStalePreIKEvidenceBlocksSyntheticTargetPublication()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (PrototypeRig referenceRig in _referenceRigs)
        {
            Node rig = LoadPackedScene(referenceRig.Path).Instantiate();
            sceneTree.Root.AddChild(rig);
            await WaitForFramesAsync(sceneTree, 2);
            try
            {
                Skeleton3D skeleton = RequireSkeleton(rig, referenceRig);
                Transform3D anchor = skeleton.GetBoneGlobalPose(RequireBoneIndex(skeleton, "LeftFoot"));
                LocomotionContactLockPrototype prototype = new();
                _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, anchor, SyntheticContactLifecycle.Acquire)), None());

                foreach ((string name, SyntheticContactEvidence evidence) in new[]
                {
                    ("missing", SyntheticContactEvidence.Missing()),
                    ("empty", SyntheticContactEvidence.Empty()),
                    ("stale", Evidence(Contact(PrototypeFoot.Left, anchor, SyntheticContactLifecycle.FullSupport, stale: true))),
                    ("invalid", Evidence(Contact(PrototypeFoot.Left, Transform3D.Identity, SyntheticContactLifecycle.FullSupport, valid: false))),
                })
                {
                    SyntheticCorrection request = new(new Vector3(0.01f, 0f, 0f), 0.05f);
                    ContactLockSnapshot snapshot = prototype.Resolve(
                        evidence,
                        new SyntheticCorrectionRequest(name, request, FixtureFeasibility.Admitted, request));
                    PrototypeFinalOutput output = PrototypeLowerLegTargetReconciliation.Reconcile(
                        snapshot,
                        PrototypeFoot.Left,
                        anchor);

                    Assert.False(output.IsPublished, $"{referenceRig.Name}/{name}: invalid pre-IK input must not publish a solve target.");
                    Assert.False(PrototypePostIKValidation.IsCoherent(snapshot, PrototypeFoot.Left, output));
                    Assert.True(snapshot.SyntheticOutput.IsZero);
                    Assert.Equal(request, snapshot.Residual);
                }
            }
            finally
            {
                rig.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void UnprovenFixtureFeasibility_DefersRecoveryAndPreservesResidual()
    {
        LocomotionContactLockPrototype prototype = new();
        _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None());
        SyntheticCorrection request = new(new Vector3(0.012f, 0f, -0.006f), 0.07f);

        ContactLockSnapshot result = prototype.Resolve(NoEvent(), new SyntheticCorrectionRequest(
            "unproven-feasibility",
            request,
            FixtureFeasibility.Unproven,
            SyntheticCorrection.Zero));

        PrototypeFinalOutput output = PrototypeLowerLegTargetReconciliation.Reconcile(result, PrototypeFoot.Left, Anchor(-0.45f));
        Assert.Equal(ContactLockDisposition.DeferredRecoveryRequired, result.Disposition);
        Assert.Equal("FeasibilityUnproven", result.InvalidOrStaleReason);
        Assert.True(result.SyntheticOutput.IsZero);
        Assert.Equal(request, result.Residual);
        Assert.False(output.IsPublished);
    }

    /// <summary>
    /// Exports only tested synthetic snapshots plus pending-review capture provenance. The photobooth consumes this file
    /// without recreating contact logic or representing a direct-rig solve as native-pipeline evidence.
    /// </summary>
    [Fact]
    public void VisualSnapshots_AreExportedFromTestedSnapshotCreationWithRequiredProvenance()
    {
        List<VisualScenario> scenarios =
        [
            CreateVisualScenario("01_acquire_left", "Acquire Left", "Lifecycle", null, Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()),
            CreateVisualScenario("02_acquire_right", "Acquire Right", "Lifecycle", null, Evidence(Contact(PrototypeFoot.Right, Anchor(0.16f), SyntheticContactLifecycle.Acquire)), None()),
            CreateVisualScenario("03_full_support", "Full Support", "Lifecycle", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), Evidence(Contact(PrototypeFoot.Left, Anchor(-0.30f), SyntheticContactLifecycle.FullSupport)), None()),
            CreateVisualScenario("04_release", "Release", "Lifecycle", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Release)), None()),
            CreateVisualScenario("05_double_support", "Double Support", "RejectedRequest", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), Evidence(Contact(PrototypeFoot.Right, Anchor(0.16f), SyntheticContactLifecycle.Acquire)), Request("double-support", new Vector3(0.01f, 0f, 0f), 0.05f, FixtureFeasibility.Admitted)),
            CreateVisualScenario("06_permitted_pivot", "Permitted Pivot", "ReducedRequest", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), NoEvent(), Request("permitted-pivot", Vector3.Zero, 0.08f, FixtureFeasibility.Reduced, 0.03f), yawReference: "ActiveLeftAnchor"),
            CreateVisualScenario("07_stale_evidence", "Stale Evidence", "InvalidEvidence", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.FullSupport, stale: true)), None()),
            CreateVisualScenario("08_invalid_anchor", "Invalid Anchor", "InvalidEvidence", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), Evidence(Contact(PrototypeFoot.Left, Transform3D.Identity, SyntheticContactLifecycle.FullSupport, valid: false)), None()),
            CreateVisualScenario("09_small_translation", "Small Translation", "AdmittedRequest", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), NoEvent(), Request("small-translation", new Vector3(0.035f, 0f, 0f), 0f, FixtureFeasibility.Admitted)),
            CreateVisualScenario("10_small_yaw", "Small Yaw", "AdmittedRequest", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Right, Anchor(0.16f), SyntheticContactLifecycle.Acquire)), None()), NoEvent(), Request("small-yaw", Vector3.Zero, 0.05f, FixtureFeasibility.Admitted)),
            CreateVisualScenario("11_excessive", "Deliberately Excessive", "RejectedRequest", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), NoEvent(), Request("deliberately-excessive", new Vector3(2f, 0f, 0f), 1.5f, FixtureFeasibility.Rejected)),
            CreateVisualScenario("12_active_replacement", "Active Replacement", "InvalidEvidence", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), Evidence(Contact(PrototypeFoot.Left, Anchor(-0.22f), SyntheticContactLifecycle.Acquire)), None()),
            CreateVisualScenario("13_missing_evidence", "Missing Contact Evidence", "InvalidEvidence", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), SyntheticContactEvidence.Missing(), Request("missing", new Vector3(0.01f, 0f, 0f), 0f, FixtureFeasibility.Admitted)),
            CreateVisualScenario("14_empty_evidence", "Empty Contact Evidence", "InvalidEvidence", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), SyntheticContactEvidence.Empty(), Request("empty", new Vector3(0.01f, 0f, 0f), 0f, FixtureFeasibility.Admitted)),
            CreateVisualScenario("15_unproven_feasibility", "Unproven Feasibility", "DeferredRecovery", prototype => _ = prototype.Resolve(Evidence(Contact(PrototypeFoot.Left, Anchor(-0.16f), SyntheticContactLifecycle.Acquire)), None()), NoEvent(), Request("unproven-feasibility", new Vector3(0.012f, 0f, -0.006f), 0.07f, FixtureFeasibility.Unproven)),
        ];

        VisualSnapshotDocument document = new(
            new VisualSnapshotProvenance(
                nameof(VisualSnapshots_AreExportedFromTestedSnapshotCreationWithRequiredProvenance),
                VisualSnapshotResourcePath,
                VisualCaptureOutputPath,
                "Pending"),
            [.. _referenceRigs.Select(rig => new VisualRig(rig.Name, rig.Path, rig.SkeletonPath, ToArray(rig.RootPosition), 0.78f))],
            scenarios);
        string path = ProjectSettings.GlobalizePath(VisualSnapshotResourcePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        }));

        using var parsed = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(File.Exists(path));
        Assert.Equal(15, scenarios.Count);
        AssertRequiredSnapshotProvenance(parsed.RootElement);
        JsonElement exportedScenarios = parsed.RootElement.GetProperty("scenarios");
        Assert.Equal(15, exportedScenarios.GetArrayLength());
        foreach (JsonElement scenario in exportedScenarios.EnumerateArray())
        {
            AssertRequiredVisualProvenance(scenario);
            Assert.NotEqual("ActualTwoBoneSolve", scenario.GetProperty("scenarioType").GetString());
        }
    }

    private static void AssertRequiredVisualProvenance(JsonElement scenario)
    {
        foreach (string required in new[]
        {
            "title", "disposition", "evidenceText", "evidenceState", "requestTranslation", "residualTranslation",
            "requestYaw", "residualYaw", "yawReference", "referencePosition", "scenarioType", "labelText", "labelPlacement",
        })
        {
            Assert.True(scenario.TryGetProperty(required, out _), $"Visual snapshot is missing required provenance field '{required}'.");
        }
    }

    private static void AssertRequiredSnapshotProvenance(JsonElement document)
    {
        Assert.True(document.TryGetProperty("provenance", out JsonElement provenance));
        Assert.Equal(
            nameof(VisualSnapshots_AreExportedFromTestedSnapshotCreationWithRequiredProvenance),
            provenance.GetProperty("createdByTest").GetString());
        Assert.Equal(VisualSnapshotResourcePath, provenance.GetProperty("snapshotPath").GetString());
        Assert.Equal(VisualCaptureOutputPath, provenance.GetProperty("captureOutputPath").GetString());
        Assert.Equal("Pending", provenance.GetProperty("visualReviewStatus").GetString());
    }

    private static VisualScenario CreateVisualScenario(
        string slug,
        string title,
        string scenarioType,
        Action<LocomotionContactLockPrototype>? setup,
        SyntheticContactEvidence evidence,
        SyntheticCorrectionRequest request,
        string yawReference = "RigOrigin")
    {
        LocomotionContactLockPrototype prototype = new();
        setup?.Invoke(prototype);
        ContactLockSnapshot snapshot = prototype.Resolve(evidence, request);
        var anchors = snapshot.Anchors.ToDictionary(
            pair => pair.Key.ToString(),
            pair => ToArray(pair.Value.Origin));
        Vector3 referencePosition = yawReference == "ActiveLeftAnchor" && snapshot.Anchors.TryGetValue(PrototypeFoot.Left, out Transform3D leftAnchor)
            ? leftAnchor.Origin + new Vector3(0f, 0.025f, 0f)
            : new Vector3(0f, 0.055f, -0.04f);
        string evidenceText = snapshot.InvalidOrStaleReason ?? (evidence.State + " fixture evidence");
        string labelText = $"{title}\n{snapshot.Disposition} | {evidenceText}";
        PrototypeFinalOutput target = PrototypeLowerLegTargetReconciliation.Reconcile(snapshot, PrototypeFoot.Left, Transform3D.Identity);
        return new(
            slug,
            title,
            snapshot.Version,
            snapshot.AnchorLifecycleState.ToString(),
            snapshot.Disposition.ToString(),
            snapshot.InvalidOrStaleReason,
            evidence.State.ToString(),
            evidenceText,
            anchors,
            [.. snapshot.Anchors.Keys.Select(foot => foot.ToString())],
            ToArray(request.Correction.Translation),
            request.Correction.YawRadians,
            ToArray(snapshot.SyntheticOutput.Translation),
            snapshot.SyntheticOutput.YawRadians,
            ToArray(snapshot.Residual.Translation),
            snapshot.Residual.YawRadians,
            yawReference,
            ToArray(referencePosition),
            scenarioType,
            target.IsPublished,
            labelText,
            [0f, 0.78f, 0.22f]);
    }

    private static Transform3D Anchor(float x) => new(Basis.Identity, new Vector3(x, 0.03f, -0.04f));

    private static SyntheticContactRecord Contact(
        PrototypeFoot foot,
        Transform3D anchor,
        SyntheticContactLifecycle lifecycle,
        bool stale = false,
        bool valid = true)
        => new(foot, anchor, 1f, lifecycle, stale, valid, "Stage5SyntheticFixture");

    private static SyntheticCorrectionRequest None()
        => Request("none", Vector3.Zero, 0f, FixtureFeasibility.Admitted);

    private static SyntheticContactEvidence Evidence(params SyntheticContactRecord[] contacts)
        => SyntheticContactEvidence.Present(contacts);

    private static SyntheticContactEvidence NoEvent() => SyntheticContactEvidence.NoEvent();

    private static SyntheticCorrectionRequest Request(
        string name,
        Vector3 translation,
        float yaw,
        FixtureFeasibility feasibility,
        float admittedYaw = 0f)
        => new(name, new SyntheticCorrection(translation, yaw), feasibility, new SyntheticCorrection(Vector3.Zero, admittedYaw));

    private static Skeleton3D RequireSkeleton(Node rig, PrototypeRig referenceRig)
        => Assert.IsType<Skeleton3D>(rig.GetNodeOrNull(referenceRig.SkeletonPath), exactMatch: false);

    private static int RequireBoneIndex(Skeleton3D skeleton, string boneName)
        => skeleton.FindBone(boneName) is int boneIndex and not -1
            ? boneIndex
            : throw new Xunit.Sdk.XunitException($"Reference skeleton has no '{boneName}' bone.");

    private static float[] ToArray(Vector3 value) => [value.X, value.Y, value.Z];

    private sealed record PrototypeRig(string Name, string Path, string SkeletonPath, Vector3 RootPosition);

    private sealed record VisualSnapshotDocument(VisualSnapshotProvenance Provenance, VisualRig[] Rigs, List<VisualScenario> Scenarios);

    private sealed record VisualSnapshotProvenance(
        string CreatedByTest,
        string SnapshotPath,
        string CaptureOutputPath,
        string VisualReviewStatus);

    private sealed record VisualRig(string Name, string Path, string SkeletonPath, float[] Position, float Scale);

    private sealed record VisualScenario(
        string Slug,
        string Title,
        long Version,
        string AnchorLifecycle,
        string Disposition,
        string? Reason,
        string EvidenceState,
        string EvidenceText,
        Dictionary<string, float[]> Anchors,
        string[] ActiveContacts,
        float[] RequestTranslation,
        float RequestYaw,
        float[] OutputTranslation,
        float OutputYaw,
        float[] ResidualTranslation,
        float ResidualYaw,
        string YawReference,
        float[] ReferencePosition,
        string ScenarioType,
        bool TargetPublished,
        string LabelText,
        float[] LabelPlacement);
}
