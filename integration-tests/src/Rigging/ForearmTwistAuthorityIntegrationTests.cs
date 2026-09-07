using AlleyCat.IK;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Rigging;

/// <summary>Focused runtime coverage for authority-stamped forearm twist consumption.</summary>
public sealed class ForearmTwistAuthorityIntegrationTests
{
    private const float ToleranceRadians = 0.002f;
    // Compare basis vectors rather than Quaternion.AngleTo: its acos implementation returns about 0.00069 radians
    // when comparing a non-identity float32 quaternion with itself.
    private const float HandReassertionTolerance = 1.0e-5f;
    private const float HandReassertionToleranceMetres = 1.0e-6f;
    private const string FemaleTemplatePath = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
    private const string MaleTemplatePath = "res://assets/characters/templates/reference_male/reference_male_base.tscn";
    private const string MockRuntimeScenePath = "res://assets/xr/mock_runtime.tscn";

    /// <summary>
    /// Proves each side independently rests for unready/stale input and starts fresh from every ownership epoch,
    /// with the single twist helper driven by that side's authority sample.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AuthoritySamples_PerSideOwnershipTransitions_ResetOnlyThatSideAndPreserveFreshTwist()
    {
        SceneTree sceneTree = GetSceneTree();
        using Node root = new()
        {
            Name = "ForearmTwistAuthorityFixture"
        };
        Skeleton3D skeleton = CreateSkeleton();
        ForearmTwistModifier modifier = new()
        {
            Name = "ForearmTwistModifier"
        };
        skeleton.AddChild(modifier);
        root.AddChild(skeleton);
        sceneTree.Root.AddChild(root);
        await WaitForNextFrameAsync(sceneTree);

        try
        {
            modifier._ProcessModificationWithDelta(0.0d);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.0f);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);

            // One sample drives the single helper write: the twist helper tracks weight x twist about the
            // longitudinal axis, and the opposite side's helper stays at rest (RIG-002 TR6 side isolation).
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.8f, 0.6f, ForearmTwistAuthorityKind.IKProvider, 10, 1);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.8f * ForearmTwistModifier.DefaultTwistWeight);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);

            // Optical loss retains a ready hand pose with frozen authority; the opposite hand remains untouched.
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.4f, -0.45f, ForearmTwistAuthorityKind.FrozenTracking, 10, 2);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.4f * ForearmTwistModifier.DefaultTwistWeight);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);

            // Reacquisition, provider replacement, repeated epochs, animation at zero provider influence and grab
            // ownership each begin a fresh branch rather than retaining a prior continuous turn.
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.6f, 0.0f, ForearmTwistAuthorityKind.IKProvider, 10, 3);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.6f * ForearmTwistModifier.DefaultTwistWeight);
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.5f, 0.55f, ForearmTwistAuthorityKind.IKProvider, 11, 4);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.5f * ForearmTwistModifier.DefaultTwistWeight);
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.7f, -0.7f, ForearmTwistAuthorityKind.IKProvider, 11, 5);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.7f * ForearmTwistModifier.DefaultTwistWeight);
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.3f, 0.0f, ForearmTwistAuthorityKind.Animation, 1, 6, 0.0f);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.3f * ForearmTwistModifier.DefaultTwistWeight);
            ProcessSample(modifier, skeleton, LimbSide.Left, 0.9f, 0.65f, ForearmTwistAuthorityKind.Grab, 20, 7);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.9f * ForearmTwistModifier.DefaultTwistWeight);

            // A token issued by an earlier adapter execution in this engine frame fails closed and rests the
            // side's helper while the hand is still re-asserted (RIG-002 TR7/TR17).
            ulong staleToken = ForearmTwistModificationPass.Begin(skeleton);
            _ = ForearmTwistModificationPass.Begin(skeleton);
            SubmitSample(modifier, skeleton, LimbSide.Left, 0.9f, 0.65f, ForearmTwistAuthorityKind.Grab, 20, 7, 1.0f,
                staleToken);
            AssertHandGlobalPoseStableAcrossWriterPass(modifier, skeleton, LimbSide.Left);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.0f);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>Role templates inherit the C# shipped twist default without serialising a redundant override.</summary>
    [Headless]
    [Fact]
    public void LoadedRoleTemplates_ForearmTwistWeightInheritsShippedDefault()
    {
        foreach (string templatePath in new[] { FemaleTemplatePath, MaleTemplatePath })
        {
            using Node template = Assert.IsType<PackedScene>(ResourceLoader.Load(templatePath), exactMatch: false).Instantiate();
            ForearmTwistModifier modifier = Assert.IsType<ForearmTwistModifier>(
                template.FindChild("ForearmTwistModifier", recursive: true, owned: false),
                exactMatch: false);

            Assert.Equal(ForearmTwistModifier.DefaultTwistWeight, modifier.TwistWeight);
            Assert.Equal(0.50f, modifier.TwistWeight);
        }
    }

    /// <summary>Production CharacterIK wiring puts the dynamic authority stage in the required modifier order.</summary>
    [Headless]
    [Fact]
    public async Task CharacterIK_InstallsAuthorityStageBetweenHandCopyTwistAndOptical()
    {
        SceneTree sceneTree = GetSceneTree();
        using Node3D root = new()
        {
            Name = "CharacterIKAuthorityFixture"
        };
        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            TargetSkeleton = skeleton,
            Enabled = false
        };
        SkeletonModifier3D rightCopy = new()
        {
            Name = "RightHandCopyRotation"
        };
        SkeletonModifier3D leftCopy = new()
        {
            Name = "LeftHandCopyRotation"
        };
        ForearmTwistModifier twist = new()
        {
            Name = "ForearmTwistModifier"
        };
        OpticalFingerTrackingModifier optical = new()
        {
            Name = "OpticalFingerTrackingModifier"
        };
        skeleton.AddChild(rig);
        skeleton.AddChild(rightCopy);
        skeleton.AddChild(leftCopy);
        skeleton.AddChild(twist);
        skeleton.AddChild(optical);

        Marker3D viewpoint = new();
        CharacterBody3D headTarget = new();
        Node3D headSolveTarget = new();
        AnimatableBody3D rightTarget = new();
        AnimatableBody3D leftTarget = new();
        CharacterIK ik = new()
        {
            Viewpoint = viewpoint,
            HeadIKTarget = headTarget,
            HeadIKSolveTarget = headSolveTarget,
            RightHandIKTarget = rightTarget,
            LeftHandIKTarget = leftTarget,
            PhysicalRig = rig,
            HeadModifierGroup = [rightCopy],
            RightHandModifierGroup = [rightCopy],
            LeftHandModifierGroup = [leftCopy],
        };
        root.AddChild(skeleton);
        root.AddChild(viewpoint);
        root.AddChild(headTarget);
        root.AddChild(headSolveTarget);
        root.AddChild(rightTarget);
        root.AddChild(leftTarget);
        root.AddChild(ik);
        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 6);

        try
        {
            SkeletonModifier3D adapter = Assert.IsType<SkeletonModifier3D>(
                skeleton.GetNodeOrNull("CharacterIKHandAuthorityStage"),
                exactMatch: false);
            Assert.True(rightCopy.GetIndex() < adapter.GetIndex());
            Assert.True(leftCopy.GetIndex() < adapter.GetIndex());
            Assert.True(adapter.GetIndex() < twist.GetIndex());
            Assert.True(twist.GetIndex() < optical.GetIndex());
            Assert.Equal(skeleton.GetChildCount() - 1, optical.GetIndex());

            adapter._ProcessModificationWithDelta(0.0d);
            twist._ProcessModificationWithDelta(0.0d);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.0f);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);

            // Production wiring drives the single helper from one adapter sample: a composed twist+bend
            // canonical hand pose yields weight x twist on the twist helper with the swing never driven,
            // the opposite side isolated, and the ordering unchanged (adapter -> twist -> optical).
            SetCanonicalHandTwist(skeleton, LimbSide.Left, 0.7f, 0.5f);
            Basis handBasisBefore = skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftHand")).Basis;
            adapter._ProcessModificationWithDelta(0.0d);
            twist._ProcessModificationWithDelta(0.0d);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.7f * ForearmTwistModifier.DefaultTwistWeight);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);
            AssertHandGlobalBasisStable(handBasisBefore, skeleton.GetBoneGlobalPose(skeleton.FindBone("LeftHand")).Basis);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Drives real target providers, CharacterIK's physical target pipeline, canonical copy stages, and the installed
    /// authority adapter.  The only direct submission replays an adapter-emitted stale sample as an explicit control.
    /// </summary>
    [Fact]
    public async Task CharacterIK_ProductionHandAuthorityPipeline_TracksProviderOwnershipAndPerSideTokens()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForNextFrameAsync(sceneTree);
        using TestGame root = new()
        {
            Name = "CharacterIKProductionAuthorityFixture"
        };
        TestXRManager xrManager = new()
        {
            Name = "XR"
        };
        root.AddChild(xrManager);
        Node3D origin = new()
        {
            Name = "Origin"
        };
        root.AddChild(origin);
        MockXRRuntimeNode runtime = Assert.IsType<PackedScene>(ResourceLoader.Load(MockRuntimeScenePath), exactMatch: false)
            .Instantiate<MockXRRuntimeNode>();
        origin.AddChild(runtime);
        _ = runtime.Initialise(new SubViewport(), maximumRefreshRate: 90);
        xrManager.SetRuntime(runtime);

        Skeleton3D skeleton = CreateSkeleton();
        DynamicPhysicalRig rig = new()
        {
            TargetSkeleton = skeleton,
            Enabled = false
        };
        AnimatableBody3D rightTarget = new()
        {
            Name = "RightHandTarget"
        };
        AnimatableBody3D leftTarget = new()
        {
            Name = "LeftHandTarget"
        };
        CanonicalHandCopyModifier rightCopy = new()
        {
            Name = "RightHandCopyRotation",
            Side = LimbSide.Right,
            Target = rightTarget
        };
        CanonicalHandCopyModifier leftCopy = new()
        {
            Name = "LeftHandCopyRotation",
            Side = LimbSide.Left,
            Target = leftTarget
        };
        ForearmTwistModifier twist = new()
        {
            Name = "ForearmTwistModifier"
        };
        OpticalFingerTrackingModifier optical = new()
        {
            Name = "OpticalFingerTrackingModifier"
        };
        skeleton.AddChild(rig);
        skeleton.AddChild(rightCopy);
        skeleton.AddChild(leftCopy);
        skeleton.AddChild(twist);
        skeleton.AddChild(optical);

        XRHandPoseTargetProvider rightOptical = new()
        {
            Name = "RightOptical",
            Side = LimbSide.Right
        };
        XRHandPoseTargetProvider leftOptical = new()
        {
            Name = "LeftOptical",
            Side = LimbSide.Left
        };
        HandGrabTargetProvider rightGrab = new()
        {
            Name = "RightGrab",
            DefaultProvider = rightOptical,
            Responsiveness = 60.0f
        };
        HandGrabTargetProvider leftGrab = new()
        {
            Name = "LeftGrab",
            DefaultProvider = leftOptical,
            Responsiveness = 60.0f
        };
        Marker3D viewpoint = new();
        CharacterBody3D headTarget = new();
        Node3D headSolveTarget = new();
        CharacterIK ik = new()
        {
            Viewpoint = viewpoint,
            HeadIKTarget = headTarget,
            HeadIKSolveTarget = headSolveTarget,
            RightHandIKTarget = rightTarget,
            LeftHandIKTarget = leftTarget,
            RightHandIKTargetIntentProvider = rightGrab,
            LeftHandIKTargetIntentProvider = leftGrab,
            PhysicalRig = rig,
            HeadModifierGroup = [rightCopy],
            RightHandModifierGroup = [rightCopy],
            LeftHandModifierGroup = [leftCopy],
            HandTargetMaximumSpeed = float.MaxValue,
            HandTargetSettleDistance = float.MaxValue,
        };
        root.AddChild(skeleton);
        root.AddChild(rightTarget);
        root.AddChild(leftTarget);
        root.AddChild(rightOptical);
        root.AddChild(leftOptical);
        root.AddChild(rightGrab);
        root.AddChild(leftGrab);
        root.AddChild(viewpoint);
        root.AddChild(headTarget);
        root.AddChild(headSolveTarget);
        root.AddChild(ik);
        sceneTree.Root.AddChild(root);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            SkeletonModifier3D adapter = Assert.IsType<SkeletonModifier3D>(
                skeleton.GetNode("CharacterIKHandAuthorityStage"), exactMatch: false);
            AssertRuntimeStageOrder(skeleton, rightCopy, leftCopy, adapter, twist, optical);

            // 1. A zero-influence provider yields ready Animation metadata while the authored canonical hand twist
            // still drives the helper.
            rightOptical.DesiredInfluence = 0.0f;
            SetCanonicalHandTwist(skeleton, LimbSide.Right, 0.8f);
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample animation = twist.GetLastAuthoritySample(LimbSide.Right);
            Assert.True(animation.IsUsable);
            Assert.Equal(ForearmTwistAuthorityKind.Animation, animation.AuthorityKind);
            Assert.Equal(0.0f, animation.Influence);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.8f * ForearmTwistModifier.DefaultTwistWeight);

            // 2. Actual XR provider live -> frozen -> live changes kind/epoch but each emitted canonical pose is a
            // principal-twist branch, never helper rest or a retained revolution.
            rightOptical.DesiredInfluence = 1.0f;
            SetOpticalWrist(runtime, LimbSide.Right, 0.7f);
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample live = twist.GetLastAuthoritySample(LimbSide.Right);
            Assert.Equal(ForearmTwistAuthorityKind.IKProvider, live.AuthorityKind);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.7f * ForearmTwistModifier.DefaultTwistWeight);

            runtime.MarkOpticalWristLost(LimbSide.Right);
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample frozen = twist.GetLastAuthoritySample(LimbSide.Right);
            Assert.True(frozen.IsUsable);
            Assert.Equal(ForearmTwistAuthorityKind.FrozenTracking, frozen.AuthorityKind);
            Assert.NotEqual(live.AuthorityEpoch, frozen.AuthorityEpoch);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.7f * ForearmTwistModifier.DefaultTwistWeight);

            SetOpticalWrist(runtime, LimbSide.Right, -0.5f);
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample reacquired = twist.GetLastAuthoritySample(LimbSide.Right);
            Assert.Equal(ForearmTwistAuthorityKind.IKProvider, reacquired.AuthorityKind);
            Assert.NotEqual(frozen.AuthorityEpoch, reacquired.AuthorityEpoch);
            AssertHelperAngle(skeleton, LimbSide.Right, -0.5f * ForearmTwistModifier.DefaultTwistWeight);

            // 3. The grab wrapper retains its source identity and epoch while interpolating a held target, then
            // explicitly transitions ownership when released to its optical default provider.
            rightGrab.SetGrabTarget(CreateWrist(0.3f));
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample grabStart = twist.GetLastAuthoritySample(LimbSide.Right);
            rightGrab.SetGrabTarget(CreateWrist(0.45f));
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample grabMove = twist.GetLastAuthoritySample(LimbSide.Right);
            Assert.Equal(ForearmTwistAuthorityKind.Grab, grabStart.AuthorityKind);
            Assert.Equal(grabStart.SourceIdentity, grabMove.SourceIdentity);
            Assert.Equal(grabStart.AuthorityEpoch, grabMove.AuthorityEpoch);
            rightGrab.ReleaseGrabTarget();
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            ForearmTwistAuthoritySample released = twist.GetLastAuthoritySample(LimbSide.Right);
            Assert.NotEqual(grabMove.SourceIdentity, released.SourceIdentity);
            Assert.NotEqual(grabMove.AuthorityEpoch, released.AuthorityEpoch);

            // 4. Control assertion: replaying the actual first adapter sample after a second same-frame adapter
            // execution proves the real emitted token is stale and fails closed.
            adapter._ProcessModificationWithDelta(0.0d);
            ForearmTwistAuthoritySample priorAdapterSample = twist.GetLastAuthoritySample(LimbSide.Right);
            adapter._ProcessModificationWithDelta(0.0d);
            twist.SubmitAuthoritySample(LimbSide.Right, priorAdapterSample);
            twist._ProcessModificationWithDelta(0.0d);
            AssertHelperAngle(skeleton, LimbSide.Right, 0.0f);

            // 6. Establish a ready, non-rest right helper. Then replay an actual left adapter sample after a
            // subsequent same-frame adapter pass makes that opposite side stale; it must fail closed without
            // changing the active right helper.
            rightOptical.DesiredInfluence = 0.0f;
            SetCanonicalHandTwist(skeleton, LimbSide.Right, 0.6f);
            SetOpticalWrist(runtime, LimbSide.Left, 0.35f);
            ProcessProductionPipeline(ik, rightCopy, leftCopy, adapter, twist);
            Quaternion activeRight = skeleton.GetBonePoseRotation(skeleton.FindBone("RightForearmTwist"));
            Assert.True(activeRight.AngleTo(Quaternion.Identity) > 0.01f);
            ForearmTwistAuthoritySample priorLeftAdapterSample = twist.GetLastAuthoritySample(LimbSide.Left);
            adapter._ProcessModificationWithDelta(0.0d);
            twist.SubmitAuthoritySample(LimbSide.Left, priorLeftAdapterSample);
            twist._ProcessModificationWithDelta(0.0d);
            Quaternion rightAfterLeftFailure = skeleton.GetBonePoseRotation(skeleton.FindBone("RightForearmTwist"));
            Assert.InRange(activeRight.AngleTo(rightAfterLeftFailure), 0.0f, 0.0001f);
            Assert.True(rightAfterLeftFailure.AngleTo(Quaternion.Identity) > 0.01f);
            AssertHelperAngle(skeleton, LimbSide.Left, 0.0f);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static void ProcessProductionPipeline(
        CharacterIK ik,
        SkeletonModifier3D rightCopy,
        SkeletonModifier3D leftCopy,
        SkeletonModifier3D adapter,
        ForearmTwistModifier twist)
    {
        ik._PhysicsProcess(1.0d / 60.0d);
        rightCopy._ProcessModificationWithDelta(0.0d);
        leftCopy._ProcessModificationWithDelta(0.0d);
        adapter._ProcessModificationWithDelta(0.0d);
        twist._ProcessModificationWithDelta(0.0d);
    }

    private static void AssertRuntimeStageOrder(
        Skeleton3D skeleton,
        SkeletonModifier3D rightCopy,
        SkeletonModifier3D leftCopy,
        SkeletonModifier3D adapter,
        ForearmTwistModifier twist,
        OpticalFingerTrackingModifier optical)
    {
        Node begin = skeleton.GetNode("CharacterIKBeginStage");
        Node foot = skeleton.GetNode("CharacterIKFootProviderStage");
        Node end = skeleton.GetNode("CharacterIKEndStage");
        Assert.True(begin.GetIndex() < rightCopy.GetIndex());
        Assert.True(foot.GetIndex() < rightCopy.GetIndex());
        Assert.True(rightCopy.GetIndex() < adapter.GetIndex());
        Assert.True(leftCopy.GetIndex() < adapter.GetIndex());
        Assert.True(adapter.GetIndex() < twist.GetIndex());
        Assert.True(twist.GetIndex() < end.GetIndex());
        Assert.True(end.GetIndex() < optical.GetIndex());
        Assert.Equal(skeleton.GetChildCount() - 1, optical.GetIndex());
    }

    private static void SetOpticalWrist(MockXRRuntimeNode runtime, LimbSide side, float twist)
    {
        _ = runtime.SetHandObservations(XRHandSourceObservation.Optical, XRHandSourceObservation.Optical);
        runtime.SetOpticalWristSample(side, CreateWrist(twist));
    }

    private static Transform3D CreateWrist(float twist)
        => new(new Basis(Vector3.Forward, twist), Vector3.Zero);

    private static void SetCanonicalHandTwist(Skeleton3D skeleton, LimbSide side, float twist, float bend = 0.0f)
    {
        int lowerArm = skeleton.FindBone($"{side}LowerArm");
        int hand = skeleton.FindBone($"{side}Hand");
        Transform3D handRest = skeleton.GetBoneGlobalRest(hand);
        skeleton.SetBoneGlobalPose(
            hand,
            skeleton.GetBoneGlobalPose(lowerArm) * new Transform3D(
                ComposedWristBasis(twist, bend),
                handRest.Origin));
    }

    /// <summary>The anatomical composition order <c>Swing(bend) x Twist(twist)</c> in lower-arm local space.</summary>
    private static Basis ComposedWristBasis(float twist, float bend)
        => new Basis(Vector3.Right, bend) * new Basis(Vector3.Forward, twist);

    private static Skeleton3D CreateSkeleton()
    {
        Skeleton3D skeleton = new()
        {
            Name = "AuthoritySkeleton"
        };
        _ = AddBone(skeleton, "Head", -1, Transform3D.Identity);
        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            string prefix = side.ToString();
            // RIG-002 TR1 authored chain: LowerArm -> ForearmTwist -> Hand with exactly one helper bone.
            int lowerArm = AddBone(skeleton, $"{prefix}LowerArm", -1, Transform3D.Identity);
            int helper = AddBone(skeleton, $"{prefix}ForearmTwist", lowerArm, new Transform3D(Basis.Identity, Vector3.Forward * 0.5f));
            _ = AddBone(skeleton, $"{prefix}Hand", helper, new Transform3D(Basis.Identity, Vector3.Forward * 0.5f));
        }

        return skeleton;
    }

    private static int AddBone(Skeleton3D skeleton, string name, int parent, Transform3D rest)
    {
        int index = skeleton.GetBoneCount();
        _ = skeleton.AddBone(name);
        skeleton.SetBoneRest(index, rest);
        skeleton.SetBoneParent(index, parent);
        return index;
    }

    private static void ProcessSample(
        ForearmTwistModifier modifier,
        Skeleton3D skeleton,
        LimbSide side,
        float twist,
        float bend,
        ForearmTwistAuthorityKind kind,
        ulong sourceIdentity,
        ulong epoch,
        float influence = 1.0f)
    {
        ulong token = ForearmTwistModificationPass.Begin(skeleton);
        SubmitSample(
            modifier,
            skeleton,
            side,
            twist,
            bend,
            kind,
            sourceIdentity,
            epoch,
            influence,
            token);
        AssertHandGlobalPoseStableAcrossWriterPass(modifier, skeleton, side);
    }

    /// <summary>
    /// Runs the writer pass and asserts the canonical hand global pose established before it is preserved
    /// across the helper write and the same-pass re-assertion (RIG-002 TR6), including fail-closed
    /// passes that rest the helper.
    /// </summary>
    private static void AssertHandGlobalPoseStableAcrossWriterPass(ForearmTwistModifier modifier, Skeleton3D skeleton, LimbSide side)
    {
        int hand = skeleton.FindBone($"{side}Hand");
        Basis basisBefore = skeleton.GetBoneGlobalPose(hand).Basis;
        Vector3 originBefore = skeleton.GetBoneGlobalPose(hand).Origin;
        modifier._ProcessModificationWithDelta(0.0d);
        AssertHandGlobalBasisStable(basisBefore, skeleton.GetBoneGlobalPose(hand).Basis);
        Assert.InRange(
            originBefore.DistanceTo(skeleton.GetBoneGlobalPose(hand).Origin),
            0.0f,
            HandReassertionToleranceMetres);
    }

    private static void AssertHandGlobalBasisStable(Basis expected, Basis actual)
    {
        Assert.InRange(expected.X.DistanceTo(actual.X), 0.0f, HandReassertionTolerance);
        Assert.InRange(expected.Y.DistanceTo(actual.Y), 0.0f, HandReassertionTolerance);
        Assert.InRange(expected.Z.DistanceTo(actual.Z), 0.0f, HandReassertionTolerance);
    }

    private static void SubmitSample(
        ForearmTwistModifier modifier,
        Skeleton3D skeleton,
        LimbSide side,
        float twist,
        float bend,
        ForearmTwistAuthorityKind kind,
        ulong sourceIdentity,
        ulong epoch,
        float influence,
        ulong stamp)
    {
        int lowerArm = skeleton.FindBone($"{side}LowerArm");
        int hand = skeleton.FindBone($"{side}Hand");
        Transform3D lowerArmPose = skeleton.GetBoneGlobalPose(lowerArm);
        Transform3D handRest = skeleton.GetBoneGlobalRest(hand);
        Transform3D handPose = lowerArmPose * new Transform3D(
            ComposedWristBasis(twist, bend) * handRest.Basis,
            handRest.Origin);
        skeleton.SetBoneGlobalPose(hand, handPose);
        modifier.SubmitAuthoritySample(
            side,
            new ForearmTwistAuthoritySample(handPose, influence, true, kind, sourceIdentity, epoch, stamp));
    }

    private static void AssertHelperAngle(Skeleton3D skeleton, LimbSide side, float expectedAngle)
    {
        int helper = skeleton.FindBone($"{side}ForearmTwist");
        Quaternion actual = skeleton.GetBonePoseRotation(helper);
        Quaternion expected = new(Vector3.Forward, expectedAngle);
        Assert.False(float.IsNaN(actual.X) || float.IsInfinity(actual.X));
        Assert.InRange(actual.AngleTo(expected), 0.0f, ToleranceRadians);
    }

    private sealed partial class CanonicalHandCopyModifier : SkeletonModifier3D
    {
        public LimbSide Side
        {
            get; init;
        }

        public Node3D? Target
        {
            get; init;
        }

        public override void _ProcessModificationWithDelta(double delta)
        {
            _ = delta;
            Skeleton3D? skeleton = GetSkeleton();
            Node3D? target = Target;
            if (!Active || skeleton is null || target is null || !IsInstanceValid(target))
            {
                return;
            }

            int hand = skeleton.FindBone($"{Side}Hand");
            if (hand >= 0)
            {
                Transform3D rest = skeleton.GetBoneGlobalRest(hand);
                skeleton.SetBoneGlobalPose(hand, new Transform3D(target.GlobalTransform.Basis, rest.Origin));
            }
        }
    }

    private sealed partial class TestXRManager : XRManager
    {
        public override void _Ready()
        {
        }

        public void SetRuntime(IXRRuntime runtime) => Runtime = runtime;
    }

    private sealed partial class TestGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
