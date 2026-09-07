using System.Text.Json;
using AlleyCat.IK;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Rigging;

/// <summary>
/// Runtime evidence for the anatomical wrist bend (RIG-002 TR8/TR10) on the REAL imported reference templates,
/// female and male, both sides, at runtime twist weights 0 and 0.50, through the REAL
/// <see cref="ForearmTwistModifier" /> and the runtime CharacterIK hand-authority adapter. The fixture is
/// manifest-independent: it runs on the checked-in templates and weights, and every expectation is derived
/// independently from rest geometry — the production twist maths is never used to build an expectation.
/// Under bend the helper stays axial-only (or rests when the commanded pronation is zero) and the hand bends
/// through its authoritative chain with its achieved global pose preserved; wrist-bend cosmetics are
/// explicitly outside this contract (RIG-002 User Requirement 4).
/// </summary>
public sealed class ForearmTwistAnatomicalBendRuntimeIntegrationTests
{
    private const string FemaleTemplatePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string MaleTemplatePath = "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string EvidenceResPath = "res://temp/RIG-002/runtime/twist_only/anatomical_bend_runtime_evidence.json";
    private const float PositionToleranceMetres = 2.0e-5f;
    private const float TwistToleranceRadians = 0.002f;
    private const float CanonicalInputToleranceRadians = 0.002f;
    // The finger hierarchy round-trips the posed hand through several local-rest composition levels inside the
    // engine's float32 pose pipeline; the observed chain noise is ~7e-4 rad while a rest-finger bug (a finger left
    // at rest under a posed hand) diverges by the full pronation magnitude, three orders larger.
    private const float FingerChainRotationToleranceRadians = 0.002f;
    private const float RestToleranceRadians = 0.001f;
    private const float PalmForwardMinimumDot = 0.999f;
    private const float PalmGeometryMinimumAlignment = 0.5f;
    private const float LittleGeometryMinimumAlignment = 0.8f;
    private const float SubjectForwardMaximumUpTilt = 0.15f;
    private const float SubjectForwardWorldAnchorMinimumDot = 0.99f;
    private const float BendSignConventionMinimumAlignmentDelta = 0.25f;

    private static readonly float[] _weights = [0.0f, ForearmTwistModifier.DefaultTwistWeight];

    private static readonly (string Name, float BendDegrees, bool PoseApplied)[] _scenarios =
    [
        ("rest", 0.0f, false),
        ("palm_forward", 0.0f, true),
        ("flexion_60", 60.0f, true),
        ("extension_60", -60.0f, true),
    ];

    /// <summary>
    /// Checks the entire adapter/writer observation window on both imported rigs and both sides:
    /// translated, rotated lower arm, bent wrist, and the resulting engine-read helper and hand transforms.
    /// The helper's global anchor must be the independent axial transport of its rest anchor, never a
    /// bend-driven pivot, and the hand's achieved global pose is unchanged by the writer pass.
    /// </summary>
    [Fact]
    public async Task TranslatedLowerArm_BentHand_KeepsHelperAxialOnlyAndPreservesHandInSamePass()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach ((string label, string path) in TemplateContracts())
        {
            using Node template = Assert.IsType<PackedScene>(ResourceLoader.Load(path), exactMatch: false).Instantiate();
            sceneTree.Root.AddChild(template);
            await WaitForNextFrameAsync(sceneTree);
            try
            {
                RuntimeRig rig = await SetUpRuntimeRigAsync(sceneTree, template);
                foreach (LimbSide limbSide in new[] { LimbSide.Left, LimbSide.Right })
                {
                    RigSide side = rig.Resolve(limbSide);
                    Skeleton3D skeleton = rig.Skeleton;
                    ResetPose(skeleton);
                    ForceUpdateAllBoneTransforms(skeleton);
                    rig.Modifier.TwistWeight = 0.5f;
                    Transform3D lowerRest = skeleton.GetBoneGlobalRest(side.Lower);
                    Transform3D handRest = skeleton.GetBoneGlobalRest(side.Hand);
                    Vector3 axis = (handRest.Origin - lowerRest.Origin).Normalized();
                    Vector3 bendAxis = axis.Cross(Vector3.Up).Normalized();
                    Assert.True(bendAxis.LengthSquared() > 0.9f, $"{label}/{limbSide}: degenerate bend fixture.");
                    Transform3D lowerPose = new(
                        new Basis(new Quaternion(Vector3.Up, 0.43f)) * lowerRest.Basis,
                        lowerRest.Origin + new Vector3(0.37f, -0.23f, 0.19f));
                    skeleton.SetBoneGlobalPose(side.Lower, lowerPose);
                    ForceUpdateAllBoneTransforms(skeleton);
                    Vector3 wrist = lowerPose * (lowerRest.AffineInverse() * handRest.Origin);
                    Vector3 posedAxis = (lowerPose.Basis * lowerRest.Basis.Inverse() * axis).Normalized();
                    Vector3 posedBendAxis = (lowerPose.Basis * lowerRest.Basis.Inverse() * bendAxis).Normalized();
                    Basis bend = new(posedBendAxis, 0.72f);
                    Basis axial = new(posedAxis, 0.81f);
                    Transform3D commandedHand = new(bend * axial * lowerPose.Basis * lowerRest.Basis.Inverse() * handRest.Basis, wrist);
                    skeleton.SetBoneGlobalPose(side.Hand, commandedHand);
                    ForceUpdateAllBoneTransforms(skeleton);
                    Transform3D achievedHand = skeleton.GetBoneGlobalPose(side.Hand);
                    Transform3D achievedLower = skeleton.GetBoneGlobalPose(side.Lower);

                    rig.Adapter._ProcessModificationWithDelta(0.0d);
                    Assert.True(rig.Modifier.GetLastAuthoritySample(limbSide).IsUsable,
                        $"{label}/{limbSide}: hand authority was not ready.");
                    rig.Modifier._ProcessModificationWithDelta(0.0d);

                    Transform3D actualTwist = skeleton.GetBoneGlobalPose(side.Helper);
                    Transform3D actualHand = skeleton.GetBoneGlobalPose(side.Hand);
                    // Independently transport the rest anchors through the posed lower arm and the weighted
                    // axial rotation only: the helper follows the axial transport, never the bend.
                    Vector3 transportedWrist = achievedLower * (lowerRest.AffineInverse() * handRest.Origin);
                    Basis partialTwist = new(posedAxis, 0.81f * 0.5f);
                    Vector3 twistAnchor = transportedWrist + (partialTwist *
                        ((achievedLower * (lowerRest.AffineInverse() * skeleton.GetBoneGlobalRest(side.Helper).Origin)) - transportedWrist));
                    Assert.InRange(achievedHand.Origin.DistanceTo(transportedWrist), 0.0f, PositionToleranceMetres);
                    Assert.InRange(actualTwist.Origin.DistanceTo(twistAnchor), 0.0f, PositionToleranceMetres);
                    Assert.InRange(actualHand.Origin.DistanceTo(achievedHand.Origin), 0.0f, PositionToleranceMetres);
                    Assert.InRange(actualHand.Basis.GetRotationQuaternion().AngleTo(achievedHand.Basis.GetRotationQuaternion()), 0.0f, TwistToleranceRadians);
                    Assert.InRange(actualTwist.Basis.GetRotationQuaternion().AngleTo(
                        (partialTwist * achievedLower.Basis * lowerRest.Basis.Inverse() * skeleton.GetBoneGlobalRest(side.Helper).Basis).GetRotationQuaternion()),
                        0.0f,
                        TwistToleranceRadians);
                }
            }
            finally
            {
                template.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }
    }

    /// <summary>
    /// Drives every template, side, runtime weight, and anatomical scenario through the production
    /// adapter-before-twist pipeline and proves: the derived anatomy matches the runner's checks, the commanded
    /// pronation is recovered, the helper tracks weight x pronation on the correct axis while staying axial-only
    /// under bend, the wrist stays put, canonical inputs survive the writer, same-pass tokens gate the writer
    /// while stale tokens rest the helper with the hand still re-asserted, and fingers follow the posed hand
    /// through the real hierarchy.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AnatomicalBend_AuthorityAndHierarchyEvidence_CoversTemplatesSidesWeightsAndScenarios()
    {
        SceneTree sceneTree = GetSceneTree();
        var evidenceAssets = new Dictionary<string, object>();
        foreach ((string label, string templatePath) in TemplateContracts())
        {
            using Node template = Assert.IsType<PackedScene>(ResourceLoader.Load(templatePath), exactMatch: false).Instantiate();
            sceneTree.Root.AddChild(template);
            await WaitForNextFrameAsync(sceneTree);
            try
            {
                RuntimeRig rig = await SetUpRuntimeRigAsync(sceneTree, template);
                var sideEvidence = new Dictionary<string, object>();
                foreach (LimbSide limbSide in new[] { LimbSide.Left, LimbSide.Right })
                {
                    AnatomicalWristFrame frame = CreateAnatomicalWristFrame(rig.Skeleton, rig.Resolve(limbSide), limbSide);
                    var weightEvidence = new Dictionary<string, object>();
                    foreach (float weight in _weights)
                    {
                        rig.Modifier.TwistWeight = weight;
                        var scenarioEvidence = new Dictionary<string, object>();
                        foreach ((string scenarioName, float bendDegrees, bool poseApplied) in _scenarios)
                        {
                            scenarioEvidence.Add(scenarioName, RunAnatomicalBendScenario(
                                rig, limbSide, frame, bendDegrees, poseApplied, $"{label}/{limbSide}/{weight:0.000}/{scenarioName}"));
                        }

                        // Same-pass token control: an adapter sample from a previous same-frame adapter execution
                        // is stale and must rest the helper (RIG-002 TR7 fail-closed contract).
                        object staleControl = RunStaleTokenControl(rig, limbSide, frame);
                        weightEvidence.Add(weight.ToString("0.000"), new
                        {
                            scenarios = scenarioEvidence,
                            stale_token_control = staleControl,
                        });
                    }

                    sideEvidence.Add(limbSide.ToString().ToLowerInvariant(), weightEvidence);
                }

                evidenceAssets.Add(label, new
                {
                    template = templatePath,
                    adapter_before_twist = true,
                    sides = sideEvidence,
                });
            }
            finally
            {
                template.QueueFree();
                await WaitForNextFrameAsync(sceneTree);
            }
        }

        WriteEvidence(new
        {
            schema_version = 2,
            status = "runtime_evidence_collected",
            contract = "twist-only: helper axial-only under bend; hand authority preserved",
            fixtures = "real imported reference templates through CharacterIK hand-authority adapter",
            weights = _weights,
            scenarios = _scenarios.Select(scenario => scenario.Name),
            assets = evidenceAssets,
        });
    }

    private static IEnumerable<(string Label, string TemplatePath)> TemplateContracts()
    {
        yield return ("female", FemaleTemplatePath);
        yield return ("male", MaleTemplatePath);
    }

    /// <summary>
    /// Installs the production CharacterIK hand-authority wiring on the real template — the same convention
    /// bindings the photobooth runner uses — waits for the runtime adapter, disables every other IK and
    /// animation node, and returns the resolved rig handles.
    /// </summary>
    private static async Task<RuntimeRig> SetUpRuntimeRigAsync(SceneTree sceneTree, Node template)
    {
        Skeleton3D skeleton = Assert.Single(template.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>());
        ForearmTwistModifier twist = Assert.IsType<ForearmTwistModifier>(
            skeleton.GetNode("ForearmTwistModifier"),
            exactMatch: false);
        Marker3D viewpoint = Assert.IsType<Marker3D>(skeleton.GetNode("Head/Viewpoint"), exactMatch: false);
        DynamicPhysicalRig physicalRig = Assert.IsType<DynamicPhysicalRig>(skeleton.GetNode("DynamicPhysicalRig"), exactMatch: false);
        CharacterBody3D headTarget = Assert.IsType<CharacterBody3D>(template.GetNode("IKTargets/Head"), exactMatch: false);
        Marker3D headSolveTarget = Assert.IsType<Marker3D>(template.GetNode("IKTargets/HeadSolve"), exactMatch: false);
        AnimatableBody3D rightHandTarget = Assert.IsType<AnimatableBody3D>(template.GetNode("IKTargets/RightHand"), exactMatch: false);
        AnimatableBody3D leftHandTarget = Assert.IsType<AnimatableBody3D>(template.GetNode("IKTargets/LeftHand"), exactMatch: false);
        CharacterIK characterIk = new()
        {
            Viewpoint = viewpoint,
            HeadIKTarget = headTarget,
            HeadIKSolveTarget = headSolveTarget,
            RightHandIKTarget = rightHandTarget,
            LeftHandIKTarget = leftHandTarget,
            PhysicalRig = physicalRig,
        };
        template.AddChild(characterIk);
        SkeletonModifier3D adapter;
        for (int frame = 0; frame < 12; frame++)
        {
            await WaitForNextFrameAsync(sceneTree);
            if (skeleton.GetNodeOrNull("CharacterIKHandAuthorityStage") is SkeletonModifier3D installed)
            {
                adapter = installed;
                Assert.True(adapter.GetIndex() < twist.GetIndex(),
                    "The hand-authority adapter must execute immediately before the ForearmTwistModifier.");
                DisableNonTwistRigSystems(template, twist, adapter);
                ResetPose(skeleton);
                ForceUpdateAllBoneTransforms(skeleton);
                return new RuntimeRig(template, skeleton, twist, adapter);
            }
        }

        throw new InvalidOperationException(
            $"CharacterIK under '{template.GetPath()}' never installed CharacterIKHandAuthorityStage under '{skeleton.GetPath()}'.");
    }

    private static void DisableNonTwistRigSystems(Node template, SkeletonModifier3D twist, SkeletonModifier3D adapter)
    {
        // Mirror the photobooth runner: every IK-ish node is disabled except the forearm-twist chain (the writer
        // and its hand-authority adapter), and animation playback is stopped so rest poses stay authoritative.
        foreach (Node node in template.FindChildren("*", string.Empty, recursive: true, owned: false))
        {
            bool isTwistChain = ReferenceEquals(node, twist) || ReferenceEquals(node, adapter);
            if (!isTwistChain && (node is AnimationPlayer or AnimationTree || IsIkNode(node)))
            {
                node.ProcessMode = Node.ProcessModeEnum.Disabled;
                if (HasProperty(node, "active"))
                {
                    node.Set("active", false);
                }

                if (HasProperty(node, "enabled"))
                {
                    node.Set("enabled", false);
                }
            }
        }

        foreach (Node node in template.FindChildren("*", string.Empty, recursive: true, owned: false))
        {
            bool isTwistChain = ReferenceEquals(node, twist) || ReferenceEquals(node, adapter);
            if (!isTwistChain && IsIkNode(node))
            {
                Assert.False(node.IsProcessing() || IsActiveOrEnabled(node),
                    $"An active IK node remained enabled: {node.GetPath()}.");
            }
        }
    }

    private static bool IsIkNode(Node node)
        => node is SkeletonModifier3D || node.Name.ToString().ToLowerInvariant().Contains("ik");

    private static bool IsActiveOrEnabled(Node node)
        => (HasProperty(node, "active") && node.Get("active").AsBool())
            || (HasProperty(node, "enabled") && node.Get("enabled").AsBool());

    private static bool HasProperty(Node node, string propertyName)
    {
        foreach (Godot.Collections.Dictionary property in node.GetPropertyList())
        {
            if (string.Equals(property["name"].AsString(), propertyName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static object RunAnatomicalBendScenario(
        RuntimeRig rig,
        LimbSide limbSide,
        AnatomicalWristFrame frame,
        float bendDegrees,
        bool poseApplied,
        string context)
    {
        Skeleton3D skeleton = rig.Skeleton;
        RigSide side = rig.Resolve(limbSide);
        ResetPose(skeleton);
        ForceUpdateAllBoneTransforms(skeleton);
        Vector3 wristRestOrigin = skeleton.GetBoneGlobalRest(side.Hand).Origin;

        // Every scenario except rest applies the palm-forward pronation stage plus its commanded bend; rest
        // keeps the skeleton untouched so the writer must rest its helper from a canonical rest sample.
        float commandedPronation = 0.0f;
        float palmForwardDot = float.NaN;
        if (poseApplied)
        {
            palmForwardDot = ApplyAnatomicalBendGlobalPose(skeleton, side, frame, bendDegrees);
            commandedPronation = frame.PronationRadians;
        }

        // Canonical inputs as supplied to the writer: captured after the pose application so any writer-induced
        // drift is isolated from the commanded pose itself.
        Quaternion lowerBefore = skeleton.GetBoneGlobalPose(side.Lower).Basis.GetRotationQuaternion();
        Quaternion handBefore = skeleton.GetBoneGlobalPose(side.Hand).Basis.GetRotationQuaternion();

        // Production ordering: the adapter samples the just-posed canonical hand with a fresh same-pass token,
        // then the twist writer consumes it immediately (adapter-before-twist, RIG-002 TR7).
        rig.Adapter._ProcessModificationWithDelta(0.0d);
        rig.Modifier._ProcessModificationWithDelta(0.0d);

        // Canonical lower-arm and hand inputs are unchanged by the writer pass. This includes the writer's
        // hand global-pose re-assertion: the helper write perturbs the hand through the re-parented chain and
        // the same pass restores the upstream-authored pose (RIG-002 TR6).
        Assert.InRange(
            lowerBefore.AngleTo(skeleton.GetBoneGlobalPose(side.Lower).Basis.GetRotationQuaternion()),
            0.0f,
            CanonicalInputToleranceRadians);
        Assert.InRange(
            handBefore.AngleTo(skeleton.GetBoneGlobalPose(side.Hand).Basis.GetRotationQuaternion()),
            0.0f,
            CanonicalInputToleranceRadians);

        // The RIG-002 TR1 authored chain is present on the imported reference templates: the hand is the
        // twist helper's direct child, and the twist helper's only child is the hand (no second helper).
        Assert.Equal(side.Helper, skeleton.GetBoneParent(side.Hand));
        Assert.Equal(
            [side.Hand],
            [.. Enumerable.Range(0, skeleton.GetBoneCount()).Where(bone => skeleton.GetBoneParent(bone) == side.Helper)]);

        // Wrist/hand origin stability across the whole scenario.
        float wristOriginError = skeleton.GetBoneGlobalPose(side.Hand).Origin.DistanceTo(wristRestOrigin);
        Assert.InRange(wristOriginError, 0.0f, PositionToleranceMetres);

        // Commanded versus extracted signed pronation. The read-back pose quaternion may sit on either
        // double-cover branch, so canonicalise before the product; the twist quaternion built from the
        // principal angle is canonical by construction.
        Quaternion handDelta = CanonicaliseDoubleCover(
            (skeleton.GetBoneGlobalPose(side.Hand).Basis.GetRotationQuaternion()
                * skeleton.GetBoneGlobalRest(side.Hand).Basis.GetRotationQuaternion().Inverse()).Normalized());
        float extractedPronation = SignedPrincipalTwist(handDelta, frame.Longitudinal);
        Assert.InRange(MathF.Abs(extractedPronation - commandedPronation), 0.0f, TwistToleranceRadians);

        // The signed helper rotation equals weight x pronation about the helper-rest longitudinal axis; the
        // bend never reaches the helper regardless of its magnitude or sign. The writer composes
        // pose = rest x Twist, so the twist is recovered by LEFT-multiplying with rest^-1; the signed angle
        // about the axis carries both the axis agreement and the direction.
        float weight = rig.Modifier.TwistWeight;
        Quaternion helperLocalRest = skeleton.GetBoneRest(side.Helper).Basis.GetRotationQuaternion();
        Vector3 helperAxisLocal = (skeleton.GetBoneGlobalRest(side.Helper).Basis.Inverse().GetRotationQuaternion() * frame.Longitudinal).Normalized();
        Quaternion expectedHelper = helperLocalRest * new Quaternion(helperAxisLocal, weight * commandedPronation);
        Quaternion actualHelper = skeleton.GetBonePoseRotation(side.Helper);
        float helperError = expectedHelper.AngleTo(actualHelper);
        Assert.InRange(helperError, 0.0f, TwistToleranceRadians);
        Quaternion helperTwistLocal = helperLocalRest.Inverse() * actualHelper;
        Vector3 helperTwistVector = new(helperTwistLocal.X, helperTwistLocal.Y, helperTwistLocal.Z);
        float signedHelperRotation = 2.0f * MathF.Atan2(helperTwistVector.Dot(helperAxisLocal), helperTwistLocal.W);
        Assert.InRange(MathF.Abs(signedHelperRotation - (weight * commandedPronation)), 0.0f, TwistToleranceRadians);

        // The helper's complete local pose is the axial transport, not a rotation-only write: its origin
        // equals the transported rest anchor under the posed lower arm and the weighted axial rotation.
        Transform3D lowerRest = skeleton.GetBoneGlobalRest(side.Lower);
        Transform3D helperGlobalRest = skeleton.GetBoneGlobalRest(side.Helper);
        Transform3D lowerPose = skeleton.GetBoneGlobalPose(side.Lower);
        Vector3 helperOriginExpected = lowerPose
            * new Transform3D(new Basis(frame.Longitudinal, weight * commandedPronation), Vector3.Zero)
            * (lowerRest.AffineInverse() * helperGlobalRest.Origin);
        Assert.InRange(
            skeleton.GetBoneGlobalPose(side.Helper).Origin.DistanceTo(helperOriginExpected),
            0.0f,
            PositionToleranceMetres);

        // Finger hierarchy propagation: descendants of the posed hand follow its rigid delta rather than
        // staying at rest, which catches rest-finger bugs in any Godot-side pose helper. The bent fingertip
        // direction is thereby checked independently of the commanded transform (RIG-002 TR13).
        Transform3D handRigidDelta = skeleton.GetBoneGlobalPose(side.Hand)
            * skeleton.GetBoneGlobalRest(side.Hand).AffineInverse();
        foreach (string digit in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
        {
            int distal = skeleton.FindBone($"{limbSide}{digit}Distal");
            Assert.True(distal >= 0, $"{context}: missing {limbSide}{digit}Distal.");
            Transform3D fingerPose = skeleton.GetBoneGlobalPose(distal);
            Transform3D expected = handRigidDelta * skeleton.GetBoneGlobalRest(distal);
            Assert.InRange(fingerPose.Origin.DistanceTo(expected.Origin), 0.0f, PositionToleranceMetres);
            Assert.InRange(
                fingerPose.Basis.GetRotationQuaternion().AngleTo(expected.Basis.GetRotationQuaternion()),
                0.0f,
                FingerChainRotationToleranceRadians);
        }

        return new
        {
            bend_degrees = bendDegrees,
            pose_applied = poseApplied,
            pronation_radians = commandedPronation,
            extracted_pronation_radians = extractedPronation,
            twist_weight = weight,
            expected_helper_rotation_radians = MathF.Abs(weight * commandedPronation),
            signed_helper_rotation_radians = signedHelperRotation,
            helper_rotation_error_radians = helperError,
            helper_origin_error_metres = skeleton.GetBoneGlobalPose(side.Helper).Origin.DistanceTo(helperOriginExpected),
            wrist_origin_error_metres = wristOriginError,
            palm_thumb_alignment = frame.ThumbAlignment,
            palm_little_alignment = frame.LittleAlignment,
            palm_forward_dot = poseApplied ? palmForwardDot : (float?)null,
        };
    }

    private static object RunStaleTokenControl(RuntimeRig rig, LimbSide limbSide, AnatomicalWristFrame frame)
    {
        Skeleton3D skeleton = rig.Skeleton;
        RigSide side = rig.Resolve(limbSide);
        ResetPose(skeleton);
        ForceUpdateAllBoneTransforms(skeleton);
        _ = ApplyAnatomicalBendGlobalPose(skeleton, side, frame, 60.0f);
        rig.Adapter._ProcessModificationWithDelta(0.0d);
        ForearmTwistAuthoritySample priorSample = rig.Modifier.GetLastAuthoritySample(limbSide);
        Assert.True(priorSample.IsUsable, "The adapter must emit a usable same-pass authority sample.");
        // The active pass is the production adapter-before-writer pair: consume the fresh
        // token with the twist writer so the helper is genuinely posed before the stale
        // replay below must rest it. The write is judged against the independently
        // derived rest x weight x pronation expectation, never against the identity
        // frame — the helper's local rest can sit near identity on the regenerated
        // imports, so only a rest-relative comparison is meaningful.
        rig.Modifier._ProcessModificationWithDelta(0.0d);
        float weight = rig.Modifier.TwistWeight;
        Quaternion helperLocalRest = skeleton.GetBoneRest(side.Helper).Basis.GetRotationQuaternion();
        Vector3 helperAxisLocal = (skeleton.GetBoneGlobalRest(side.Helper).Basis.Inverse().GetRotationQuaternion() * frame.Longitudinal).Normalized();
        Quaternion expectedActive = helperLocalRest * new Quaternion(helperAxisLocal, weight * frame.PronationRadians);
        Quaternion activeHelper = skeleton.GetBonePoseRotation(side.Helper);
        Assert.InRange(activeHelper.AngleTo(expectedActive), 0.0f, TwistToleranceRadians);
        Assert.True(weight <= 0.0f || activeHelper.AngleTo(helperLocalRest) > 0.01f,
            "The active adapter pass must have written a non-rest helper for the stale control.");

        // The active pass preserves the authoritative hand global pose.
        Basis activeHandBasis = skeleton.GetBoneGlobalPose(side.Hand).Basis;
        Vector3 activeHandOrigin = skeleton.GetBoneGlobalPose(side.Hand).Origin;

        // A second adapter execution in the same engine frame invalidates the earlier token; replaying the
        // first sample must fail closed and rest the helper while the hand is re-asserted.
        rig.Adapter._ProcessModificationWithDelta(0.0d);
        rig.Modifier.SubmitAuthoritySample(limbSide, priorSample);
        rig.Modifier._ProcessModificationWithDelta(0.0d);
        Quaternion restedHelper = skeleton.GetBonePoseRotation(side.Helper);
        Quaternion helperRest = skeleton.GetBoneRest(side.Helper).Basis.GetRotationQuaternion();
        Assert.InRange(restedHelper.AngleTo(helperRest), 0.0f, RestToleranceRadians);
        Assert.InRange(
            activeHandBasis.GetRotationQuaternion().AngleTo(skeleton.GetBoneGlobalPose(side.Hand).Basis.GetRotationQuaternion()),
            0.0f,
            RestToleranceRadians);
        Assert.InRange(
            activeHandOrigin.DistanceTo(skeleton.GetBoneGlobalPose(side.Hand).Origin),
            0.0f,
            PositionToleranceMetres);
        return new
        {
            active_helper_angle_radians = activeHelper.AngleTo(helperLocalRest),
            rested_helper_error_radians = restedHelper.AngleTo(helperRest),
        };
    }

    /// <summary>Applies the anatomical pose <c>Swing(bend) x Twist(pronation)</c> with an invariant wrist origin and
    /// returns the posed palm's palm-forward alignment dot.</summary>
    private static float ApplyAnatomicalBendGlobalPose(Skeleton3D skeleton, RigSide side, AnatomicalWristFrame frame, float bendDegrees)
    {
        Transform3D lowerRest = skeleton.GetBoneGlobalRest(side.Lower);
        Transform3D handRest = skeleton.GetBoneGlobalRest(side.Hand);
        float bendRadians = Mathf.DegToRad(bendDegrees);
        if (MathF.Abs(bendRadians) > 0.0f)
        {
            AssertBendSignConvention(frame, bendRadians);
        }

        Basis lowerInverse = lowerRest.Basis.Inverse();
        Vector3 longitudinalLower = (lowerInverse * frame.Longitudinal).Normalized();
        Vector3 flexAxisLower = (lowerInverse * frame.FlexAxis).Normalized();
        Transform3D relativeRest = lowerRest.AffineInverse() * handRest;
        Basis composed = new Basis(flexAxisLower, bendRadians)
            * new Basis(longitudinalLower, frame.PronationRadians)
            * relativeRest.Basis;
        // Global pose = lowerRest * (rotation composition in the lower-local frame, invariant local origin),
        // exactly the bridge's m_mul(lower_arm_rest, posed_relative).
        Transform3D posed = lowerRest * new Transform3D(composed, relativeRest.Origin);
        ForceUpdateAllBoneTransforms(skeleton);
        int handParent = skeleton.GetBoneParent(side.Hand);
        Transform3D parentGlobalPose = handParent < 0 ? Transform3D.Identity : skeleton.GetBoneGlobalPose(handParent);
        skeleton.SetBonePose(side.Hand, parentGlobalPose.AffineInverse() * posed);
        ForceUpdateAllBoneTransforms(skeleton);

        Transform3D handPose = skeleton.GetBoneGlobalPose(side.Hand);
        Assert.InRange(handPose.Origin.DistanceTo(handRest.Origin), 0.0f, PositionToleranceMetres);
        Vector3 posedPalmPerpendicular = PerpendicularComponent(handPose.Basis.Z.Normalized(), frame.Longitudinal).Normalized();
        float palmForwardDot = posedPalmPerpendicular.Dot(frame.PalmTarget);
        Assert.True(palmForwardDot >= PalmForwardMinimumDot,
            $"The posed palm does not face the subject forward target (dot={palmForwardDot:F9}).");
        return palmForwardDot;
    }

    private static AnatomicalWristFrame CreateAnatomicalWristFrame(Skeleton3D skeleton, RigSide side, LimbSide limbSide)
    {
        string prefix = limbSide.ToString();
        int middleDistal = RequireBone(skeleton, $"{prefix}MiddleDistal");
        int thumbProximal = RequireBone(skeleton, $"{prefix}ThumbProximal");
        int littleDistal = RequireBone(skeleton, $"{prefix}LittleDistal");
        int hips = RequireBone(skeleton, "Hips");
        int head = RequireBone(skeleton, "Head");
        int leftUpperArm = RequireBone(skeleton, "LeftUpperArm");
        int rightUpperArm = RequireBone(skeleton, "RightUpperArm");

        Vector3 up = (skeleton.GetBoneGlobalRest(head).Origin - skeleton.GetBoneGlobalRest(hips).Origin).Normalized();
        Vector3 right = (skeleton.GetBoneGlobalRest(rightUpperArm).Origin - skeleton.GetBoneGlobalRest(leftUpperArm).Origin).Normalized();
        Vector3 forward = up.Cross(right).Normalized();
        Assert.True(MathF.Abs(forward.Dot(up)) <= SubjectForwardMaximumUpTilt,
            $"The derived subject forward is tilted into the rest up axis (forward={forward}, up={up}).");
        Vector3 worldForward = (skeleton.GlobalBasis * forward).Normalized();
        Assert.True(worldForward.Dot(Vector3.Forward) >= SubjectForwardWorldAnchorMinimumDot,
            $"The derived subject forward {worldForward} disagrees with the scene forward convention.");

        Transform3D lowerRest = skeleton.GetBoneGlobalRest(side.Lower);
        Transform3D handRest = skeleton.GetBoneGlobalRest(side.Hand);
        Vector3 elbow = lowerRest.Origin;
        Vector3 wrist = handRest.Origin;
        Vector3 longitudinal = (wrist - elbow).Normalized();
        Vector3 palmRest = handRest.Basis.Z.Normalized();
        Vector3 fingers = (skeleton.GetBoneGlobalRest(middleDistal).Origin - wrist).Normalized();
        Vector3 thumb = PerpendicularComponent(skeleton.GetBoneGlobalRest(thumbProximal).Origin - wrist, longitudinal).Normalized();
        Vector3 little = PerpendicularComponent(skeleton.GetBoneGlobalRest(littleDistal).Origin - wrist, longitudinal).Normalized();
        float sideSign = limbSide == LimbSide.Left ? 1.0f : -1.0f;
        Vector3 palmFromThumb = fingers.Cross(thumb).Normalized() * sideSign;
        Vector3 palmFromLittle = little.Cross(fingers).Normalized() * sideSign;
        float thumbAlignment = palmFromThumb.Dot(palmRest);
        float littleAlignment = palmFromLittle.Dot(palmRest);
        Assert.True(thumbAlignment >= PalmGeometryMinimumAlignment && littleAlignment >= LittleGeometryMinimumAlignment,
            $"Hand-rest +Z is not the palm normal according to rest finger geometry (thumb={thumbAlignment:F9}, little={littleAlignment:F9}).");

        Vector3 palmTarget = PerpendicularComponent(forward, longitudinal).Normalized();
        Vector3 palmPerpendicular = PerpendicularComponent(palmRest, longitudinal).Normalized();
        float pronation = MathF.Atan2(
            longitudinal.Dot(palmPerpendicular.Cross(palmTarget)),
            palmPerpendicular.Dot(palmTarget));
        Vector3 flexAxis = longitudinal.Cross(palmTarget).Normalized();
        return new AnatomicalWristFrame(
            longitudinal,
            palmTarget,
            flexAxis,
            pronation,
            thumbAlignment,
            littleAlignment,
            fingers);
    }

    private static void AssertBendSignConvention(AnatomicalWristFrame frame, float bendRadians)
    {
        Vector3 tipAfterPronation = (new Basis(frame.Longitudinal, frame.PronationRadians) * frame.FingersDirection).Normalized();
        Vector3 tipAfterBend = (new Basis(frame.FlexAxis, bendRadians) * tipAfterPronation).Normalized();
        float delta = tipAfterBend.Dot(frame.PalmTarget) - tipAfterPronation.Dot(frame.PalmTarget);
        Assert.True(
            (bendRadians > 0.0f && delta >= BendSignConventionMinimumAlignmentDelta)
                || (bendRadians < 0.0f && delta <= -BendSignConventionMinimumAlignmentDelta),
            $"The bend direction violates the anatomical sign convention (delta={delta:F9}, bend={bendRadians:F6} rad).");
    }

    private static Vector3 PerpendicularComponent(Vector3 vector, Vector3 axis)
    {
        Vector3 unit = axis.Normalized();
        return vector - (unit * vector.Dot(unit));
    }

    private static float SignedPrincipalTwist(Quaternion rotation, Vector3 axis)
    {
        Quaternion normalised = CanonicaliseDoubleCover(rotation.Normalized());
        Vector3 unitAxis = axis.Normalized();
        Vector3 vector = new(normalised.X, normalised.Y, normalised.Z);
        float signedSinHalfAngle = vector.Dot(unitAxis);
        Quaternion twist = CanonicaliseDoubleCover(new Quaternion(
            unitAxis.X * signedSinHalfAngle,
            unitAxis.Y * signedSinHalfAngle,
            unitAxis.Z * signedSinHalfAngle,
            normalised.W).Normalized());
        float twistVectorLength = new Vector3(twist.X, twist.Y, twist.Z).Length();
        float angle = 2.0f * MathF.Atan2(twistVectorLength, twist.W);
        return signedSinHalfAngle < 0.0f ? -angle : angle;
    }

    private static Quaternion CanonicaliseDoubleCover(Quaternion rotation)
    {
        const float tolerance = 1.0e-5f;
        if (rotation.W < -tolerance)
        {
            return new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W);
        }

        if (MathF.Abs(rotation.W) > tolerance)
        {
            return rotation;
        }

        float x = MathF.Abs(rotation.X);
        float y = MathF.Abs(rotation.Y);
        float z = MathF.Abs(rotation.Z);
        float selected = x >= y && x >= z ? rotation.X : y >= z ? rotation.Y : rotation.Z;
        return selected < 0.0f ? new Quaternion(-rotation.X, -rotation.Y, -rotation.Z, -rotation.W) : rotation;
    }

    private static int RequireBone(Skeleton3D skeleton, string boneName)
    {
        int bone = skeleton.FindBone(boneName);
        Assert.True(bone >= 0, $"The imported reference skeleton is missing bone '{boneName}'.");
        return bone;
    }

    private static void ResetPose(Skeleton3D skeleton)
    {
        for (int bone = 0; bone < skeleton.GetBoneCount(); bone++)
        {
            skeleton.ResetBonePose(bone);
        }
    }

#pragma warning disable CS0618, IDE0022 // Required to synchronise test-only skeleton inspection.
    private static void ForceUpdateAllBoneTransforms(Skeleton3D skeleton)
    {
        skeleton.ForceUpdateAllBoneTransforms();
    }
#pragma warning restore CS0618, IDE0022

    private static void WriteEvidence(object evidence)
    {
        string output = ProjectSettings.GlobalizePath(EvidenceResPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + System.Environment.NewLine);
    }

    private sealed class RuntimeRig(Node template, Skeleton3D skeleton, ForearmTwistModifier modifier, SkeletonModifier3D adapter)
    {
        public Node Template { get; } = template;
        public Skeleton3D Skeleton { get; } = skeleton;
        public ForearmTwistModifier Modifier { get; } = modifier;
        public SkeletonModifier3D Adapter { get; } = adapter;

        private readonly Dictionary<LimbSide, RigSide> _sides = [];

        public RigSide Resolve(LimbSide side)
        {
            if (_sides.TryGetValue(side, out RigSide cached))
            {
                return cached;
            }

            string prefix = side.ToString();
            RigSide resolved = new(
                side,
                RequireBone(Skeleton, $"{prefix}LowerArm"),
                RequireBone(Skeleton, $"{prefix}Hand"),
                RequireBone(Skeleton, $"{prefix}ForearmTwist"));
            _sides[side] = resolved;
            return resolved;
        }
    }

    private readonly record struct RigSide(LimbSide Side, int Lower, int Hand, int Helper);

    /// <summary>The anatomical wrist-bend frame derived from imported rest geometry (RIG-002 TR8).</summary>
    private sealed class AnatomicalWristFrame(
        Vector3 longitudinal,
        Vector3 palmTarget,
        Vector3 flexAxis,
        float pronationRadians,
        float thumbAlignment,
        float littleAlignment,
        Vector3 fingersDirection)
    {
        public Vector3 Longitudinal { get; } = longitudinal;
        public Vector3 PalmTarget { get; } = palmTarget;
        public Vector3 FlexAxis { get; } = flexAxis;
        public float PronationRadians { get; } = pronationRadians;
        public float ThumbAlignment { get; } = thumbAlignment;
        public float LittleAlignment { get; } = littleAlignment;
        public Vector3 FingersDirection { get; } = fingersDirection;
    }
}
