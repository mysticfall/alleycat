using System.Reflection;
using AlleyCat.IK;
using AlleyCat.IK.Pose;
using AlleyCat.Rigging;
using AlleyCat.Rigging.Physics;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Combination coverage for generic foot stages and the same-pass hand-authority adapter.</summary>
public sealed class CharacterIKStageLifecycleIntegrationTests
{
    private const double StepSeconds = 1.0d / 60.0d;
    private static readonly string[] _stageNames =
    [
        "CharacterIKBeginStage",
        "CharacterIKFootProviderStage",
        "CharacterIKHandAuthorityStage",
        "CharacterIKEndStage",
    ];
    private static readonly string[] _canonicalOrder =
    [
        "NeckSpineIK", "HeadCopyRotation", "NeckTwistDisperser",
        "RightArmIKController", "LeftArmIKController",
        "RightArmTwoBoneIKController", "LeftArmTwoBoneIKController",
        "RightHandCopyRotation", "LeftHandCopyRotation",
    ];

    /// <summary>Repeated full initialisation keeps the same four stages and preserves non-contiguous authored work.</summary>
    [Fact]
    public async Task RepeatedInitialisation_PreservesFourStagesAndNonContiguousAuthoredOrder()
    {
        StageFixture fixture = await StageFixture.CreateAsync();
        try
        {
            SkeletonFixture rig = fixture.Rig;
            AssertPipeline(rig);
            ulong[] initialOrder = ChildIDs(rig.Skeleton);
            SkeletonModifier3D[] stages = OwnedStages(rig.Skeleton);
            for (int attempt = 0; attempt < 4; attempt++)
            {
                Initialise(fixture.CharacterIK);
                Assert.Equal(initialOrder, ChildIDs(rig.Skeleton));
                Assert.Equal(stages, OwnedStages(rig.Skeleton));
                AssertPipeline(rig);
            }

            // Relative leg order, not adjacency, is the contract. An unrelated authored modifier stays in place.
            Assert.True(rig.Legs[0].GetIndex() < rig.InterleavedModifier.GetIndex());
            Assert.True(rig.InterleavedModifier.GetIndex() < rig.Legs[1].GetIndex());
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Reset severs the previous skeleton's callbacks before a new skeleton receives the four stages.</summary>
    [Fact]
    public async Task ResetAndRebind_RemovesOldStagesAndCallbacksAndClearsHandAndFootState()
    {
        StageFixture fixture = await StageFixture.CreateAsync();
        try
        {
            SkeletonFixture oldRig = fixture.Rig;
            ConstantIntentProvider handProvider = new()
            {
                Name = "HandProvider"
            };
            fixture.Root.AddChild(handProvider);
            fixture.CharacterIK.RightHandIKTargetIntentProvider = handProvider;
            fixture.CharacterIK.LeftHandIKTargetIntentProvider = handProvider;
            fixture.CharacterIK.SampleHandAuthorities();
            // The provider call counter is the current in-chain foot-resolution observable; the superseded
            // preparation/trace APIs no longer exist in the restored product.
            int preResetFootResolutions = fixture.FootProvider.Calls;
            await AdvanceAsync(oldRig.Skeleton);
            Assert.True(fixture.FootProvider.Calls > preResetFootResolutions);
            Assert.Equal(ForearmTwistAuthorityKind.IKProvider, oldRig.Twist!.GetLastAuthoritySample(LimbSide.Right).AuthorityKind);

            SkeletonModifier3D[] oldStages = OwnedStages(oldRig.Skeleton);
            fixture.CharacterIK.ResetRuntimeBindings();
            foreach (string stageName in _stageNames)
            {
                Assert.Null(oldRig.Skeleton.GetNodeOrNull(stageName));
            }
            foreach (string fieldName in new[]
                     { "_beginStageModifier", "_footProviderStageModifier", "_handAuthorityStageModifier", "_endStageModifier", "_footTargetSyncStage" })
            {
                Assert.Null(CachedField(fixture.CharacterIK, fieldName));
            }
            Assert.All(
                ["_footSyncStages", "_hipReconciliationModifiers", "_lowerLegConsumers"],
                fieldName => Assert.Empty((System.Collections.IEnumerable)CachedField(fixture.CharacterIK, fieldName)!));
            AssertAnimationAuthorityReset(fixture.CharacterIK, "_leftHandAuthority", 1UL);
            AssertAnimationAuthorityReset(fixture.CharacterIK, "_rightHandAuthority", 2UL);

            int beginCount = fixture.CharacterIK.BeginCount;
            int endCount = fixture.CharacterIK.EndCount;
            int providerCalls = fixture.FootProvider.Calls;
            ForearmTwistAuthoritySample oldSample = oldRig.Twist.GetLastAuthoritySample(LimbSide.Right);
            foreach (SkeletonModifier3D oldStage in oldStages)
            {
                Assert.Null(oldStage.GetParent());
                Assert.False(oldStage.Active);
                Assert.True(oldStage.IsQueuedForDeletion());
                Assert.Null(oldStage.GetType().GetProperty("Callback")?.GetValue(oldStage));
                Assert.Null(oldStage.GetType().GetProperty("CharacterIK")?.GetValue(oldStage));
                Assert.Null(oldStage.GetType().GetProperty("ForearmTwistModifier")?.GetValue(oldStage));
                oldStage._ProcessModificationWithDelta(StepSeconds);
            }
            Assert.Equal(oldSample, oldRig.Twist.GetLastAuthoritySample(LimbSide.Right));
            await AdvanceAsync(oldRig.Skeleton);
            // The unchanged provider call count proves the detached old stages and severed callbacks resolve
            // nothing after reset, replacing the removed actuation-trace observation.
            Assert.Equal(beginCount, fixture.CharacterIK.BeginCount);
            Assert.Equal(endCount, fixture.CharacterIK.EndCount);
            Assert.Equal(providerCalls, fixture.FootProvider.Calls);

            SkeletonFixture newRig = fixture.AddRig("ReboundRig", includeTwist: true);
            fixture.Bind(newRig);
            fixture.CharacterIK.RightHandIKTargetIntentProvider = null;
            fixture.CharacterIK.LeftHandIKTargetIntentProvider = null;
            Initialise(fixture.CharacterIK);
            await WaitForFramesAsync(GetSceneTree(), 2);
            newRig.Twist!.ValidateProductionTopology(newRig.Skeleton);
            AssertPipeline(newRig);
            foreach (string stageName in _stageNames)
            {
                Assert.Null(oldRig.Skeleton.GetNodeOrNull(stageName));
            }
            await AdvanceAsync(newRig.Skeleton);
            Assert.True(fixture.CharacterIK.BeginCount > beginCount);
            Assert.True(fixture.CharacterIK.EndCount > endCount);
            Assert.True(fixture.FootProvider.Calls > providerCalls);
            Assert.Equal(ForearmTwistAuthorityKind.Animation, newRig.Twist.GetLastAuthoritySample(LimbSide.Right).AuthorityKind);
            Assert.Equal(0UL, newRig.Twist.GetLastAuthoritySample(LimbSide.Right).AuthorityEpoch);
            Assert.Equal(0UL, newRig.Twist.GetLastAuthoritySample(LimbSide.Left).AuthorityEpoch);
            Assert.Equal(oldSample, oldRig.Twist.GetLastAuthoritySample(LimbSide.Right));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Generic pole, native solve, rotation-copy and configured foot consumers remain downstream of twist.</summary>
    [Fact]
    public async Task MisorderedFootConsumers_AreRepairedWithoutMovingCanonicalWorkAheadOfTwist()
    {
        StageFixture fixture = await StageFixture.CreateAsync();
        try
        {
            SkeletonFixture rig = fixture.Rig;
            LegIKController poleConsumer = new()
            {
                Name = "ConfiguredLegPole",
                FootTarget = rig.RightFoot,
                Active = false
            };
            TwoBoneIK3D nativeConsumer = new()
            {
                Name = "ConfiguredNativeLeg",
                Active = false
            };
            CopyTransformModifier3D rotationConsumer = new()
            {
                Name = "ConfiguredFootRotation",
                Active = false
            };
            rig.Skeleton.AddChild(poleConsumer);
            rig.Skeleton.AddChild(nativeConsumer);
            rig.Skeleton.AddChild(rotationConsumer);
            nativeConsumer.Set("setting_count", 1);
            rotationConsumer.Set("setting_count", 1);
            nativeConsumer.Set("settings/0/target_node", nativeConsumer.GetPathTo(rig.RightFoot));
            rotationConsumer.Set("settings/0/reference_node", rotationConsumer.GetPathTo(rig.RightFoot));
            SkeletonModifier3D[] consumers = [poleConsumer, nativeConsumer, rotationConsumer, .. rig.Legs];
            for (int i = consumers.Length - 1; i >= 0; i--)
            {
                rig.Skeleton.MoveChild(consumers[i], 0);
            }
            rig.Skeleton.MoveChild(rig.Hips, rig.Sync.GetIndex() + 1);
            Assert.All(consumers, consumer => Assert.True(consumer.GetIndex() < rig.Sync.GetIndex()));
            Assert.True(rig.Sync.GetIndex() < rig.Hips.GetIndex());

            Initialise(fixture.CharacterIK);
            AssertPipeline(rig);
            Assert.All(consumers, consumer => Assert.True(rig.Hips.GetIndex() < consumer.GetIndex()));
            Assert.All(consumers, consumer => Assert.True(rig.Twist!.GetIndex() < consumer.GetIndex()));
            Assert.Equal(consumers, [.. rig.Skeleton.GetChildren().OfType<SkeletonModifier3D>().Where(consumers.Contains)]);
            ulong[] repairedOrder = ChildIDs(rig.Skeleton);
            Initialise(fixture.CharacterIK);
            Assert.Equal(repairedOrder, ChildIDs(rig.Skeleton));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Four explicit engine passes publish current hand-copy poses even at zero arm influence.</summary>
    [Fact]
    public async Task ManualPass_PublishesCurrentCanonicalHandsImmediatelyBeforeTwistAfterInChainFootResolution()
    {
        StageFixture fixture = await StageFixture.CreateAsync();
        try
        {
            SkeletonFixture rig = fixture.Rig;
            List<string> traversal = [];
            List<(SkeletonModifier3D Modifier, Action Callback)> subscriptions = [];
            foreach (string name in new[]
                     { "FootTargetSyncController", _stageNames[0], _stageNames[1], "RightHandCopyRotation", "LeftHandCopyRotation", _stageNames[2], "ForearmTwistModifier", "RightLegConsumer", "LeftLegConsumer", _stageNames[3] })
            {
                SkeletonModifier3D modifier = rig.Skeleton.GetNode<SkeletonModifier3D>(name);
                void RecordTraversal()
                {
                    traversal.Add(name);
                }

                modifier.ModificationProcessed += RecordTraversal;
                subscriptions.Add((modifier, RecordTraversal));
            }

            try
            {
                AssertPipeline(rig);
                // The foot provider now resolves only inside the skeleton modifier chain: each pass's traversal
                // assertion proves the provider stage ran in-chain immediately before the hand copies, and the
                // call counter below proves genuine evaluation during the same four-pass window.
                int footResolutionsBeforePasses = fixture.FootProvider.Calls;
                ulong previousToken = 0UL;
                for (int pass = 0; pass < 4; pass++)
                {
                    rig.RightHandCopy.CanonicalPose = new Transform3D(
                        new Basis(Vector3.Up, 0.15f * (pass + 1)),
                        new Vector3(0.4f, 1.2f + (0.01f * pass), 0.1f));
                    traversal.Clear();
                    await AdvanceAsync(rig.Skeleton);
                    ForearmTwistAuthoritySample right = rig.Twist!.GetLastAuthoritySample(LimbSide.Right);
                    ForearmTwistAuthoritySample left = rig.Twist.GetLastAuthoritySample(LimbSide.Left);
                    Assert.True(right.Ready);
                    Assert.True(left.Ready);
                    Assert.True(right.ModificationPassToken > previousToken);
                    Assert.Equal(right.ModificationPassToken, left.ModificationPassToken);
                    Assert.Equal(ForearmTwistAuthorityKind.Animation, right.AuthorityKind);
                    Assert.Equal(ForearmTwistAuthorityKind.Animation, left.AuthorityKind);
                    Assert.Equal(0.0f, right.Influence);
                    Assert.True(right.CanonicalHandTransform.IsEqualApprox(rig.RightHandCopy.CanonicalPose));
                    Assert.True(left.CanonicalHandTransform.IsEqualApprox(rig.LeftHandCopy.CanonicalPose));
                    Assert.Equal(subscriptions.Select(subscription => subscription.Modifier.Name.ToString()), traversal);
                    previousToken = right.ModificationPassToken;
                }

                Assert.True(fixture.FootProvider.Calls > footResolutionsBeforePasses);
            }
            finally
            {
                foreach ((SkeletonModifier3D modifier, Action callback) in subscriptions)
                {
                    modifier.ModificationProcessed -= callback;
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Optional twist wiring creates, refreshes and removes just one authority adapter.</summary>
    [Fact]
    public async Task OptionalTwist_ReusesAndRefreshesAuthorityStageAndAllowsNoTwistFixture()
    {
        StageFixture fixture = await StageFixture.CreateAsync(includeTwist: false);
        try
        {
            SkeletonFixture rig = fixture.Rig;
            Assert.Null(rig.Skeleton.GetNodeOrNull(_stageNames[2]));
            Assert.Equal(3, OwnedStages(rig.Skeleton, includeAuthority: false).Length);
            rig.Twist = new ForearmTwistModifier { Name = "ForearmTwistModifier" };
            rig.Skeleton.AddChild(rig.Twist);
            Initialise(fixture.CharacterIK);
            SkeletonModifier3D authorityStage = rig.Skeleton.GetNode<SkeletonModifier3D>(_stageNames[2]);
            AssertPipeline(rig);

            ForearmTwistModifier oldTwist = rig.Twist;
            rig.Skeleton.RemoveChild(oldTwist);
            oldTwist.QueueFree();
            rig.Twist = new ForearmTwistModifier { Name = "ForearmTwistModifier" };
            rig.Skeleton.AddChild(rig.Twist);
            Initialise(fixture.CharacterIK);
            Assert.Same(authorityStage, rig.Skeleton.GetNode(_stageNames[2]));
            Assert.Same(rig.Twist, authorityStage.GetType().GetProperty("ForearmTwistModifier")!.GetValue(authorityStage));
            AssertPipeline(rig);

            rig.Skeleton.RemoveChild(rig.Twist);
            rig.Twist.QueueFree();
            rig.Twist = null;
            Initialise(fixture.CharacterIK);
            Assert.Null(rig.Skeleton.GetNodeOrNull(_stageNames[2]));
            Assert.Null(CachedField(fixture.CharacterIK, "_handAuthorityStageModifier"));
            Assert.Null(authorityStage.GetParent());
            Assert.Null(authorityStage.GetType().GetProperty("CharacterIK")!.GetValue(authorityStage));
            Assert.Null(authorityStage.GetType().GetProperty("ForearmTwistModifier")!.GetValue(authorityStage));
            Assert.Equal(3, OwnedStages(rig.Skeleton, includeAuthority: false).Length);
            Initialise(fixture.CharacterIK);
            Assert.Equal(3, OwnedStages(rig.Skeleton, includeAuthority: false).Length);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static void Initialise(CharacterIK characterIK)
    {
        // Exercise the complete production initialise/retry entry point, including cached stage ensure, without
        // changing readiness or introducing a public test-only initialisation API.
        MethodInfo method = typeof(CharacterIK).GetMethod("TryInitialiseRuntimeBindings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            Assert.Equal(true, method.Invoke(characterIK, null));
        }
        finally
        {
            characterIK.SetPhysicsProcess(false);
        }
    }

    private static object? CachedField(CharacterIK characterIK, string name)
        => typeof(CharacterIK).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(characterIK);

    private static void AssertAnimationAuthorityReset(CharacterIK characterIK, string name, ulong identity)
    {
        object state = CachedField(characterIK, name)!;
        Assert.Equal(ForearmTwistAuthorityKind.Animation, state.GetType().GetProperty("Kind")!.GetValue(state));
        Assert.Equal(identity, state.GetType().GetProperty("SourceIdentity")!.GetValue(state));
        Assert.Equal(0UL, state.GetType().GetProperty("Epoch")!.GetValue(state));
        Assert.Equal(0.0f, state.GetType().GetProperty("Influence")!.GetValue(state));
    }

    private static SkeletonModifier3D[] OwnedStages(Skeleton3D skeleton, bool includeAuthority = true)
        => [.. _stageNames.Where(name => includeAuthority || name != _stageNames[2]).Select(name =>
            Assert.Single(skeleton.GetChildren().OfType<SkeletonModifier3D>(), child => child.Name == name))];

    private static ulong[] ChildIDs(Skeleton3D skeleton)
        => [.. skeleton.GetChildren().Select(child => child.GetInstanceId())];

    private static void AssertPipeline(SkeletonFixture rig)
    {
        SkeletonModifier3D[] stages = OwnedStages(rig.Skeleton);
        Assert.True(rig.Sync.GetIndex() < stages[0].GetIndex());
        Assert.True(stages[0].GetIndex() < stages[1].GetIndex());
        Assert.True(stages[1].GetIndex() < rig.Hips.GetIndex());
        Assert.True(stages[0].GetIndex() < rig.Skeleton.GetNode("NeckSpineIK").GetIndex());
        Assert.Equal(_canonicalOrder, rig.Skeleton.GetChildren().Select(child => child.Name.ToString()).Where(_canonicalOrder.Contains));
        Assert.True(rig.LeftHandCopy.GetIndex() < stages[2].GetIndex());
        Assert.Equal(stages[2].GetIndex() + 1, rig.Twist!.GetIndex());
        Assert.All(rig.Legs, leg =>
        {
            Assert.True(rig.Hips.GetIndex() < leg.GetIndex());
            Assert.True(rig.Twist.GetIndex() < leg.GetIndex());
            Assert.True(leg.GetIndex() < stages[3].GetIndex());
        });
        Assert.Equal(stages[3].GetIndex() + 1, rig.Optical.GetIndex());
        Assert.Equal(rig.Skeleton.GetChildCount() - 1, rig.Optical.GetIndex());
    }

    private static async Task AdvanceAsync(Skeleton3D skeleton)
    {
        skeleton.Advance(StepSeconds);
        // Advance accumulates a manual solver step for the deferred update; frame pumps flush it, not a timed solve.
        await WaitForFramesAsync(GetSceneTree(), 2);
    }

    private sealed class StageFixture
    {
        public Node3D Root { get; } = new() { Name = "CharacterIKStageLifecycleFixture" };
        public LifecycleCharacterIK CharacterIK { get; } = new() { Name = "CharacterIK" };
        public ConstantIntentProvider FootProvider { get; } = new() { Name = "FootProvider" };
        public SkeletonFixture Rig { get; private set; } = null!;
        private readonly List<SkeletonFixture> _rigs = [];

        public static async Task<StageFixture> CreateAsync(bool includeTwist = true)
        {
            StageFixture fixture = new();
            try
            {
                fixture.Root.AddChild(fixture.FootProvider);
                fixture.Bind(fixture.AddRig("OriginalRig", includeTwist));
                fixture.Root.AddChild(fixture.CharacterIK);
                fixture.CharacterIK.SetPhysicsProcess(false);
                GetSceneTree().Root.AddChild(fixture.Root);
                Initialise(fixture.CharacterIK);
                await WaitForFramesAsync(GetSceneTree(), 2);
                fixture.Rig.Twist?.ValidateProductionTopology(fixture.Rig.Skeleton);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public SkeletonFixture AddRig(string name, bool includeTwist)
        {
            SkeletonFixture rig = new(name, includeTwist);
            _rigs.Add(rig);
            Root.AddChild(rig.Container);
            return rig;
        }

        public void Bind(SkeletonFixture rig)
        {
            Rig = rig;
            CharacterIK.PhysicalRig = rig.PhysicalRig;
            CharacterIK.Viewpoint = rig.Viewpoint;
            CharacterIK.HeadIKTarget = rig.Head;
            CharacterIK.HeadIKSolveTarget = rig.HeadSolve;
            CharacterIK.RightHandIKTarget = rig.RightHand;
            CharacterIK.LeftHandIKTarget = rig.LeftHand;
            CharacterIK.RightFootIKTarget = rig.RightFoot;
            CharacterIK.LeftFootIKTarget = rig.LeftFoot;
            CharacterIK.HeadModifierGroup = [];
            CharacterIK.RightHandModifierGroup = [];
            CharacterIK.LeftHandModifierGroup = [];
            CharacterIK.RightFootModifierGroup = [rig.Legs[0]];
            CharacterIK.LeftFootModifierGroup = [rig.Legs[1]];
            CharacterIK.RightFootTargetIntentProvider = FootProvider;
            CharacterIK.LeftFootTargetIntentProvider = FootProvider;
        }

        public async Task DisposeAsync()
        {
            CharacterIK.ResetRuntimeBindings();
            foreach (SkeletonFixture rig in _rigs)
            {
                ForearmTwistModificationPass.Reset(rig.Skeleton);
            }
            Root.QueueFree();
            await WaitForNextFrameAsync(GetSceneTree());
        }
    }

    private sealed class SkeletonFixture
    {
        public Node3D Container
        {
            get;
        }
        public Skeleton3D Skeleton { get; } = new() { Name = "GeneralSkeleton", ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual };
        public DynamicPhysicalRig PhysicalRig { get; } = new() { Name = "DynamicPhysicalRig", Enabled = false };
        public Marker3D Viewpoint { get; } = new() { Name = "Viewpoint" };
        public CharacterBody3D Head { get; } = new() { Name = "Head", CollisionLayer = 0, CollisionMask = 0 };
        public Node3D HeadSolve { get; } = new() { Name = "HeadSolve" };
        public AnimatableBody3D RightHand { get; } = new() { Name = "RightHand", SyncToPhysics = false };
        public AnimatableBody3D LeftHand { get; } = new() { Name = "LeftHand", SyncToPhysics = false };
        public Node3D RightFoot { get; } = new() { Name = "RightFoot" };
        public Node3D LeftFoot { get; } = new() { Name = "LeftFoot" };
        public FootTargetSyncController Sync { get; } = new() { Name = "FootTargetSyncController" };
        public HipReconciliationModifier Hips { get; } = new() { Name = "HipReconciliationModifier", Active = false };
        public CanonicalHandCopy RightHandCopy { get; } = new() { Name = "RightHandCopyRotation", BoneName = "RightHand" };
        public CanonicalHandCopy LeftHandCopy { get; } = new() { Name = "LeftHandCopyRotation", BoneName = "LeftHand" };
        public SkeletonModifier3D[] Legs { get; } = [new() { Name = "RightLegConsumer" }, new() { Name = "LeftLegConsumer" }];
        public SkeletonModifier3D InterleavedModifier { get; } = new() { Name = "UnrelatedInterleavedModifier" };
        public ForearmTwistModifier? Twist
        {
            get; set;
        }
        public OpticalFingerTrackingModifier Optical { get; } = new() { Name = "OpticalFingerTrackingModifier", Active = false };

        public SkeletonFixture(string name, bool includeTwist)
        {
            Container = new Node3D { Name = name, Rotation = new Vector3(0.0f, Mathf.Pi, 0.0f) };
            Container.AddChild(Skeleton);
            Skeleton.AddChild(PhysicalRig);
            foreach (Node node in new Node[] { Viewpoint, Head, HeadSolve, RightHand, LeftHand, RightFoot, LeftFoot })
            {
                Container.AddChild(node);
            }
            _ = AddBone("Head", -1, new Vector3(0.0f, 1.6f, 0.0f));
            _ = AddBone("LeftFoot", -1, new Vector3(-0.15f, 0.05f, 0.0f));
            _ = AddBone("RightFoot", -1, new Vector3(0.15f, 0.05f, 0.0f));
            foreach (string side in new[] { "Left", "Right" })
            {
                int lowerArm = AddBone(side + "LowerArm", -1, new Vector3(side == "Left" ? -0.4f : 0.4f, 1.0f, 0.0f));
                int helper = AddBone(side + "ForearmTwist", lowerArm, new Vector3(0.0f, 0.1f, 0.0f));
                _ = AddBone(side + "Hand", helper, new Vector3(0.0f, 0.1f, 0.0f));
            }
            RightHandCopy.CanonicalPose = Skeleton.GetBoneGlobalRest(Skeleton.FindBone("RightHand"));
            LeftHandCopy.CanonicalPose = Skeleton.GetBoneGlobalRest(Skeleton.FindBone("LeftHand"));
            Sync.LeftFootTarget = LeftFoot;
            Sync.RightFootTarget = RightFoot;
            Skeleton.AddChild(Sync);
            Skeleton.AddChild(Hips);
            foreach (string modifierName in _canonicalOrder.Take(_canonicalOrder.Length - 2))
            {
                Skeleton.AddChild(new SkeletonModifier3D { Name = modifierName });
            }
            Skeleton.AddChild(RightHandCopy);
            Skeleton.AddChild(LeftHandCopy);
            if (includeTwist)
            {
                Twist = new ForearmTwistModifier { Name = "ForearmTwistModifier" };
                Skeleton.AddChild(Twist);
            }
            Skeleton.AddChild(Legs[0]);
            Skeleton.AddChild(InterleavedModifier);
            Skeleton.AddChild(Legs[1]);
            Skeleton.AddChild(Optical);
        }

        private int AddBone(string name, int parent, Vector3 position)
        {
            int index = Skeleton.AddBone(name);
            Skeleton.SetBoneParent(index, parent);
            Skeleton.SetBoneRest(index, new Transform3D(Basis.Identity, position));
            Skeleton.SetBonePosePosition(index, position);
            return index;
        }
    }

    private sealed partial class LifecycleCharacterIK : CharacterIK
    {
        public int BeginCount
        {
            get; private set;
        }
        public int EndCount
        {
            get; private set;
        }
        protected override bool CanProcessPhysicalActuators => false;
        protected override void AfterProviderTargetProcessing(Skeleton3D skeleton, double delta) => BeginCount++;
        protected override void AfterEndStage(Skeleton3D skeleton, double delta) => EndCount++;
        public void SampleHandAuthorities()
        {
            _ = BuildRightHandTargetTransform();
            _ = BuildLeftHandTargetTransform();
        }
    }

    private sealed partial class ConstantIntentProvider : IKTargetIntentProvider
    {
        public int Calls
        {
            get; private set;
        }
        public override IKTargetIntent GetTargetIntent()
        {
            Calls++;
            return new IKTargetIntent(new Transform3D(Basis.Identity, new Vector3(0.2f, 0.1f, 0.0f)), 1.0f);
        }
    }

    private sealed partial class CanonicalHandCopy : CopyTransformModifier3D
    {
        private readonly Node3D _canonicalPose = new() { Name = "CanonicalPose" };

        public string BoneName
        {
            get => GetApplyBoneName(0);
            set => SetApplyBoneName(0, value);
        }
        public Transform3D CanonicalPose
        {
            get => _canonicalPose.Transform;
            set => _canonicalPose.Transform = value;
        }

        public CanonicalHandCopy()
        {
            // The marker supplies a skeleton-local input; Godot's native traversal performs the bone write.
            AddChild(_canonicalPose);
            SettingCount = 1;
            SetReferenceType(0, ReferenceType.Node);
            SetReferenceNode(0, new NodePath("CanonicalPose"));
            SetAmount(0, 1.0f);
            SetCopyFlags(0, TransformFlag.All);
            SetAxisFlags(0, AxisFlag.All);
            SetRelative(0, false);
            SetAdditive(0, false);
        }
    }
}
