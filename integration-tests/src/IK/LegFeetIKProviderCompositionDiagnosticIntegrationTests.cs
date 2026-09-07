using AlleyCat.IK;
using AlleyCat.Rigging.Physics;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Stage 5 diagnostic coverage for the provider-to-native-leg-solve path on the established leg IK fixture.
/// </summary>
public sealed class LegFeetIKProviderCompositionDiagnosticIntegrationTests
{
    private const string FixtureScenePath = "res://tests/ik/leg_feet_ik_test.tscn";
    private const float TargetToleranceMetres = 0.0001f;
    private const float NativeSolveToleranceMetres = 0.006f;

    /// <summary>
    /// Proves that a valid priority-provider anchor reaches the native left-leg solve through Skeleton3D's modifier pipeline.
    /// </summary>
    [Fact]
    public async Task Stage5_PriorityAnchorReachesCharacterIKTargetAndNativeLeftLegSolve()
    {
        SceneTree sceneTree = GetSceneTree();

        await using LegFeetIKDiagnosticFixture providerFixture = await LegFeetIKDiagnosticFixture.CreateAsync(sceneTree);
        StageSnapshot provider = await providerFixture.RunPriorityProviderScenarioAsync();

        Assert.True(
            provider.ProviderStageResolvedAnchor
            && provider.ProviderStageBeforeAllLowerLegConsumers
            && provider.NativeSignalObserved
            && provider.TargetAtSignalMatchesAnchor
            && provider.NativeEndpointReachesSignalTarget,
            BuildFailureMessage(provider));
    }

    private static string BuildFailureMessage(StageSnapshot provider)
        => "Stage 5 leg-IK provider diagnostic failed. "
           + $"Provider [{provider}]. "
           + "This diagnostic observes TwoBoneIK3D.ModificationProcessed from Skeleton3D's modifier pipeline; it does not invoke native modifiers directly.";

    private sealed class LegFeetIKDiagnosticFixture : IAsyncDisposable
    {
        private const string SkeletonPath = "Subject/Female/Female/GeneralSkeleton";
        private const string AnimationTreePath = "Subject/Female/AnimationTree";
        private const string LeftFootTargetPath = "Markers/LeftFootTarget";
        private const string RightFootTargetPath = "Markers/RightFootTarget";

        private readonly Node _root;
        private readonly SceneTree _sceneTree;
        private readonly Skeleton3D _skeleton;
        private readonly FootTargetSyncController _syncOwner;
        private readonly SkeletonModifier3D _beginStage;
        private readonly SkeletonModifier3D _providerStage;
        private readonly Node3D _leftFootTarget;
        private readonly LegIKController _leftPole;
        private readonly TwoBoneIK3D _leftTwoBone;
        private readonly SkeletonModifier3D _leftFootRotation;
        private readonly int _leftUpperLegBoneIndex;
        private readonly int _leftFootBoneIndex;
        private readonly PriorityIKTargetIntentProvider _priorityProvider;
        private readonly CountingAnchorIntentProvider _anchorProvider;
        private NativeTwoBonePipelineEvidence _nativeTwoBonePipelineEvidence = NativeTwoBonePipelineEvidence.Empty;

        private LegFeetIKDiagnosticFixture(
            Node root,
            SceneTree sceneTree,
            Skeleton3D skeleton,
            FootTargetSyncController syncOwner,
            SkeletonModifier3D beginStage,
            SkeletonModifier3D providerStage,
            Node3D leftFootTarget,
            LegIKController leftPole,
            TwoBoneIK3D leftTwoBone,
            SkeletonModifier3D leftFootRotation,
            int leftUpperLegBoneIndex,
            int leftFootBoneIndex,
            PriorityIKTargetIntentProvider priorityProvider,
            CountingAnchorIntentProvider anchorProvider)
        {
            _root = root;
            _sceneTree = sceneTree;
            _skeleton = skeleton;
            _syncOwner = syncOwner;
            _beginStage = beginStage;
            _providerStage = providerStage;
            _leftFootTarget = leftFootTarget;
            _leftPole = leftPole;
            _leftTwoBone = leftTwoBone;
            _leftFootRotation = leftFootRotation;
            _leftUpperLegBoneIndex = leftUpperLegBoneIndex;
            _leftFootBoneIndex = leftFootBoneIndex;
            _priorityProvider = priorityProvider;
            _anchorProvider = anchorProvider;
        }

        public static async Task<LegFeetIKDiagnosticFixture> CreateAsync(SceneTree sceneTree)
        {
            Error changeSceneError = sceneTree.ChangeSceneToPacked(LoadPackedScene(FixtureScenePath));
            Assert.Equal(Error.Ok, changeSceneError);
            await WaitForFramesAsync(sceneTree, 2);
            Node root = sceneTree.CurrentScene
                ?? throw new Xunit.Sdk.XunitException("Expected leg-feet diagnostic fixture to become the current scene.");

            Skeleton3D skeleton = RequireNode<Skeleton3D>(root, SkeletonPath);
            skeleton.Set("modifier_callback_mode_process", 0);
            AnimationTree animationTree = RequireNode<AnimationTree>(root, AnimationTreePath);
            animationTree.Active = false;
            skeleton.ResetBonePoses();

            Node3D leftFootTarget = RequireNode<Node3D>(root, LeftFootTargetPath);
            Node3D rightFootTarget = RequireNode<Node3D>(root, RightFootTargetPath);
            LegIKController leftPole = RequireNode<LegIKController>(skeleton, "LeftLegIKController");
            TwoBoneIK3D leftTwoBone = RequireNode<TwoBoneIK3D>(skeleton, "LeftLegTwoBoneIKController");
            SkeletonModifier3D leftFootRotation = RequireNode<SkeletonModifier3D>(skeleton, "CopyLeftFootRotation");
            leftTwoBone.Active = true;
            leftTwoBone.Influence = 1.0f;

            FootTargetSyncController syncOwner = RequireNode<FootTargetSyncController>(skeleton, "FootTargetSyncController");
            syncOwner.LeftFootTarget = leftFootTarget;
            syncOwner.RightFootTarget = rightFootTarget;
            syncOwner.Active = true;
            syncOwner.Influence = 1.0f;

            DynamicPhysicalRig physicalRig = new()
            {
                Name = "Stage5DiagnosticPhysicalRig",
                Enabled = false,
                TargetSkeleton = skeleton,
            };
            skeleton.AddChild(physicalRig);

            Node3D targetRoot = new()
            {
                Name = "Stage5DiagnosticTargets",
            };
            root.AddChild(targetRoot);
            Marker3D viewpoint = new()
            {
                Name = "Viewpoint",
            };
            CharacterBody3D headTarget = new()
            {
                Name = "Head",
            };
            Node3D headSolveTarget = new()
            {
                Name = "HeadSolve",
            };
            AnimatableBody3D rightHandTarget = new()
            {
                Name = "RightHand",
            };
            AnimatableBody3D leftHandTarget = new()
            {
                Name = "LeftHand",
            };
            targetRoot.AddChild(viewpoint);
            targetRoot.AddChild(headTarget);
            targetRoot.AddChild(headSolveTarget);
            targetRoot.AddChild(rightHandTarget);
            targetRoot.AddChild(leftHandTarget);

            CountingAnchorIntentProvider anchorProvider = new()
            {
                Name = "Stage5ValidLeftFootAnchor",
            };
            PriorityIKTargetIntentProvider priorityProvider = new()
            {
                Name = "Stage5LeftFootPriorityProvider",
                Providers = [anchorProvider],
            };
            targetRoot.AddChild(anchorProvider);
            targetRoot.AddChild(priorityProvider);

            CharacterIK characterIK = new()
            {
                Name = "Stage5CharacterIK",
                Viewpoint = viewpoint,
                HeadIKTarget = headTarget,
                HeadIKSolveTarget = headSolveTarget,
                RightHandIKTarget = rightHandTarget,
                LeftHandIKTarget = leftHandTarget,
                LeftFootIKTarget = leftFootTarget,
                RightFootIKTarget = rightFootTarget,
                LeftFootTargetIntentProvider = priorityProvider,
                LeftFootModifierGroup = [leftPole, leftTwoBone, leftFootRotation],
                PhysicalRig = physicalRig,
            };
            root.AddChild(characterIK);
            characterIK._PhysicsProcess(1.0d / 60.0d);
            await WaitForFramesAsync(sceneTree, 2);

            SkeletonModifier3D beginStage = RequireNode<SkeletonModifier3D>(skeleton, "CharacterIKBeginStage");
            SkeletonModifier3D providerStage = RequireNode<SkeletonModifier3D>(skeleton, "CharacterIKFootProviderStage");
            int leftUpperLegBoneIndex = RequireBone(skeleton, "LeftUpperLeg");
            int leftFootBoneIndex = RequireBone(skeleton, "LeftFoot");

            Assert.Equal(1, CountActiveSyncOwners(skeleton));
            Assert.True(syncOwner.GetIndex() < beginStage.GetIndex());
            Assert.True(beginStage.GetIndex() < providerStage.GetIndex());
            Assert.True(
                providerStage.GetIndex() < leftPole.GetIndex()
                && providerStage.GetIndex() < leftTwoBone.GetIndex()
                && providerStage.GetIndex() < leftFootRotation.GetIndex()
                && leftPole.GetIndex() < leftTwoBone.GetIndex());
            Assert.Same(leftFootTarget, characterIK.LeftFootIKTarget);

            LegFeetIKDiagnosticFixture fixture = new(
                root,
                sceneTree,
                skeleton,
                syncOwner,
                beginStage,
                providerStage,
                leftFootTarget,
                leftPole,
                leftTwoBone,
                leftFootRotation,
                leftUpperLegBoneIndex,
                leftFootBoneIndex,
                priorityProvider,
                anchorProvider);
            leftTwoBone.ModificationProcessed += fixture.RecordNativeTwoBonePipelineProcessing;
            return fixture;
        }

        public async Task<StageSnapshot> RunPriorityProviderScenarioAsync()
        {
            Transform3D anchor = CreateAnchorInsideReach();
            _anchorProvider.Resolution = new IKTargetIntentResolution(new IKTargetIntent(anchor, 1.0f), true);
            bool priorityResolved = _priorityProvider.TryGetTargetIntent(out IKTargetIntentResolution priorityResolution)
                                    && priorityResolution == _anchorProvider.Resolution;

            _nativeTwoBonePipelineEvidence = NativeTwoBonePipelineEvidence.Empty;
            for (int frame = 0; frame < 8 && _nativeTwoBonePipelineEvidence.ProcessCount == 0; frame++)
            {
                await WaitForNextPhysicsFrameAsync(_sceneTree);
            }

            return new StageSnapshot(
                anchor,
                _nativeTwoBonePipelineEvidence,
                _leftFootTarget.GlobalTransform,
                GetLeftFootWorldPose(),
                _anchorProvider.InvocationCount,
                priorityResolved,
                _providerStage.Active,
                _providerStage.Influence,
                _leftPole.Active,
                _leftPole.Influence,
                _leftTwoBone.Active,
                _leftTwoBone.Influence,
                _syncOwner.GetIndex(),
                _beginStage.GetIndex(),
                _providerStage.GetIndex(),
                _leftPole.GetIndex(),
                _leftTwoBone.GetIndex(),
                _leftFootRotation.GetIndex());
        }

        public async ValueTask DisposeAsync()
        {
            if (GodotObject.IsInstanceValid(_leftTwoBone))
            {
                _leftTwoBone.ModificationProcessed -= RecordNativeTwoBonePipelineProcessing;
            }

            if (GodotObject.IsInstanceValid(_root) && _root.IsInsideTree())
            {
                _root.QueueFree();
                await WaitForNextFrameAsync(_sceneTree);
            }
        }

        private Transform3D CreateAnchorInsideReach()
        {
            Transform3D upperLeg = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_leftUpperLegBoneIndex);
            Transform3D foot = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_leftFootBoneIndex);
            Vector3 towardUpperLeg = (upperLeg.Origin - foot.Origin).Normalized();
            return new Transform3D(foot.Basis.Orthonormalized(), foot.Origin + (towardUpperLeg * 0.018f));
        }

        private Transform3D GetLeftFootWorldPose()
            => _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(_leftFootBoneIndex);

        private void RecordNativeTwoBonePipelineProcessing()
        {
            _nativeTwoBonePipelineEvidence = new NativeTwoBonePipelineEvidence(
                _nativeTwoBonePipelineEvidence.ProcessCount + 1,
                _leftFootTarget.GlobalTransform,
                GetLeftFootWorldPose());
        }

        private static int CountActiveSyncOwners(Skeleton3D skeleton)
        {
            int count = 0;
            foreach (Node child in skeleton.GetChildren())
            {
                if (child is FootTargetSyncController { Active: true })
                {
                    count++;
                }
            }

            return count;
        }

        private static T RequireNode<T>(Node parent, string path)
            where T : Node
            => Assert.IsType<T>(parent.GetNodeOrNull(path), exactMatch: false);

        private static int RequireBone(Skeleton3D skeleton, string boneName)
        {
            int boneIndex = skeleton.FindBone(boneName);
            return boneIndex >= 0
                ? boneIndex
                : throw new Xunit.Sdk.XunitException($"Expected fixture skeleton bone '{boneName}'.");
        }
    }

    private sealed partial class CountingAnchorIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntentResolution Resolution
        {
            get; set;
        }

        public int InvocationCount
        {
            get;
            private set;
        }

        public override IKTargetIntent GetTargetIntent() => Resolution.Intent;

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            InvocationCount++;
            resolution = Resolution;
            return resolution.IsFinite;
        }
    }

    private readonly record struct NativeTwoBonePipelineEvidence(
        int ProcessCount,
        Transform3D TargetAtProcessing,
        Transform3D EndBoneAtProcessing)
    {
        public static NativeTwoBonePipelineEvidence Empty => new(0, Transform3D.Identity, Transform3D.Identity);
    }

    private readonly record struct StageSnapshot(
        Transform3D Anchor,
        NativeTwoBonePipelineEvidence NativeEvidence,
        Transform3D PostPipelineTarget,
        Transform3D PostPipelineEndBone,
        int ProviderInvocations,
        bool PriorityResolved,
        bool ProviderStageActive,
        float ProviderStageInfluence,
        bool PoleActive,
        float PoleInfluence,
        bool TwoBoneActive,
        float TwoBoneInfluence,
        int SyncIndex,
        int BeginIndex,
        int ProviderIndex,
        int PoleIndex,
        int TwoBoneIndex,
        int FootRotationIndex)
    {
        public bool ProviderStageResolvedAnchor => ProviderInvocations > 1
                                                   && PriorityResolved
                                                   && ProviderStageActive
                                                   && Mathf.IsEqualApprox(ProviderStageInfluence, 1.0f)
                                                   && PoleActive
                                                   && Mathf.IsEqualApprox(PoleInfluence, 1.0f)
                                                   && TwoBoneActive
                                                   && Mathf.IsEqualApprox(TwoBoneInfluence, 1.0f);

        public bool ProviderStageBeforeAllLowerLegConsumers
            => SyncIndex < BeginIndex
               && BeginIndex < ProviderIndex
               && ProviderIndex < PoleIndex
               && ProviderIndex < TwoBoneIndex
               && ProviderIndex < FootRotationIndex;

        public bool NativeSignalObserved => NativeEvidence.ProcessCount > 0;

        public bool TargetAtSignalMatchesAnchor
            => NativeSignalObserved
               && NativeEvidence.TargetAtProcessing.Origin.DistanceTo(Anchor.Origin) <= TargetToleranceMetres;

        public bool NativeEndpointReachesSignalTarget
            => NativeSignalObserved
               && NativeEvidence.EndBoneAtProcessing.Origin.DistanceTo(NativeEvidence.TargetAtProcessing.Origin) <= NativeSolveToleranceMetres;

        public override string ToString()
            => $"anchor={Anchor.Origin}, targetAtSignal={NativeEvidence.TargetAtProcessing.Origin}, "
               + $"endBoneAtSignal={NativeEvidence.EndBoneAtProcessing.Origin}, signalCount={NativeEvidence.ProcessCount}, "
               + $"anchorToTargetError={Anchor.Origin.DistanceTo(NativeEvidence.TargetAtProcessing.Origin):F6}, "
               + $"endpointToTargetError={NativeEvidence.EndBoneAtProcessing.Origin.DistanceTo(NativeEvidence.TargetAtProcessing.Origin):F6}, "
               + $"postPipelineTarget={PostPipelineTarget.Origin}, postPipelineEndBone={PostPipelineEndBone.Origin}, "
               + $"providerCalls={ProviderInvocations}, priorityResolved={PriorityResolved}, providerStage=({ProviderStageActive}, {ProviderStageInfluence:F3}), "
               + $"pole=({PoleActive}, {PoleInfluence:F3}), twoBone=({TwoBoneActive}, {TwoBoneInfluence:F3}), "
               + $"order=(sync={SyncIndex}, begin={BeginIndex}, provider={ProviderIndex}, pole={PoleIndex}, twoBone={TwoBoneIndex}, footRotation={FootRotationIndex})";
    }
}
