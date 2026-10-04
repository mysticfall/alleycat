using AlleyCat.IK;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Stage 2 pre-IK runtime-composition oracle coverage.</summary>
public sealed class PreIKRuntimeCompositionOracleIntegrationTests
{
    private const string NpcScenePath = "res://assets/characters/templates/reference_female/reference_female_npc.tscn";
    private const string PlayerScenePath = "res://assets/characters/templates/reference_female/reference_female_player.tscn";

    private static readonly PreIKUnavailableChannelEvidence _unavailableGraphChannels = new(
        new Dictionary<PreIKContinuityDimension, string>
        {
            [PreIKContinuityDimension.NormalisedPhase] = "the evaluated AnimationTree exposes no authoritative normalised-phase channel",
            [PreIKContinuityDimension.SeamHandoff] = "the evaluated AnimationTree exposes no seam/handoff identity channel",
            [PreIKContinuityDimension.RetimingPlaybackRate] = "the evaluated AnimationTree exposes no retiming/playback-rate identity channel",
            [PreIKContinuityDimension.LeftContactTransition] = "no admitted ANIM contact-transition evidence is bound to the evaluated graph",
            [PreIKContinuityDimension.RightContactTransition] = "no admitted ANIM contact-transition evidence is bound to the evaluated graph",
        },
        "the evaluated graph exposes no admitted continuity-delta evidence");

    [Headless]
    [Fact]
    public void RuntimeCompositionOracle_AdmittedPreIKEvidence_ClosesEveryMutationInTheFinalAggregate()
    {
        PreIKAdmittedAnimationEvidence admitted = CreateAdmittedEvidence().Require(PreIKAnimationSemanticRole.ForwardWalk);
        Assert.Equal("anim-002:catalogue:reference-female-forward-walk", admitted.ImmutableProvenanceIdentity);
        Assert.Equal("anim-002:admission:reference-female:ForwardWalk", admitted.AdmissionIdentity);
        AssertPasses(CreateCleanSamples(admitted), PreIKContinuityLimitPopulation.AllApplicable);

        AssertFails(Mutate(admitted, 1, sample => sample with { NormalisedPhase = 0.75f }), "normalised phase jump");
        AssertFails(Mutate(admitted, 1, sample => sample with { SourceFrameIdentity = "frame-0", MonotonicTimeSeconds = 0d }), "repeated source/frame identity");
        AssertFails(Mutate(admitted, 1, sample => sample with { Pose = sample.Pose! with { RootWorld = Vector3.Zero } }), "Root reset");
        AssertFails(Mutate(admitted, 1, sample => sample with { Pose = sample.Pose! with { HipsWorld = Vector3.Zero } }), "Hips continuity delta mismatch");
        AssertFails(
            Mutate(admitted, 1, sample => sample with { Pose = sample.Pose! with { LeftFootWorld = new Vector3(9f, 0f, 0f) } }),
            "left-foot blend-seam continuity");
        AssertFails(
            Mutate(admitted, 1, sample => sample with { Pose = sample.Pose! with { RightFootWorld = new Vector3(9f, 0f, 0f) } }),
            "right-foot blend-seam continuity");
        AssertFails(Mutate(admitted, 1, sample => sample with { SeamHandoffIdentity = "entry->mutated-travel" }), "seam/handoff identity mismatch");
        AssertFails(Mutate(admitted, 1, sample => sample with { RetimingPlaybackRateIdentity = "rate:1.5" }), "retiming/playback-rate identity mismatch");
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Left = sample.Contacts.Left! with
                    {
                        Transition = PreIKContactTransition.Release
                    }
                },
            }),
            "wrong left support transition");
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Right = sample.Contacts.Right! with
                    {
                        Transition = PreIKContactTransition.FullSupport
                    }
                },
            }),
            "wrong right support transition");
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Left = new PreIKContactTransitionEvidence(
                        IsAvailable: false,
                        Transition: PreIKContactTransition.Unknown,
                        AdmittedEvidence: admitted)
                },
            }),
            "invalid authoritative left contact transition evidence");
        AssertFails(CreateCleanSamples(admitted), new PreIKContinuityLimitPopulation([]), "continuity limit population was empty");

        PreIKAdmittedAnimationEvidence mismatchedProvenance = CreateAdmittedEvidence(
            "anim-002:catalogue:other-provenance").Require(PreIKAnimationSemanticRole.ForwardWalk);
        Assert.NotEqual(admitted.ImmutableProvenanceIdentity, mismatchedProvenance.ImmutableProvenanceIdentity);
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Left = sample.Contacts.Left! with
                    {
                        AdmittedEvidence = mismatchedProvenance
                    },
                },
            }),
            "admission evidence reference or immutable identity tuple does not match");

        PreIKAdmittedAnimationEvidence mismatchedAdmissionIdentity = CreateAdmittedEvidence(
            admissionIdentity: "anim-002:admission:other-decision").Require(PreIKAnimationSemanticRole.ForwardWalk);
        Assert.Equal(admitted.ImmutableProvenanceIdentity, mismatchedAdmissionIdentity.ImmutableProvenanceIdentity);
        Assert.NotEqual(admitted.AdmissionIdentity, mismatchedAdmissionIdentity.AdmissionIdentity);
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Left = sample.Contacts.Left! with
                    {
                        AdmittedEvidence = mismatchedAdmissionIdentity
                    },
                },
            }),
            "admission evidence reference or immutable identity tuple does not match");

        PreIKAdmittedAnimationEvidence sameIdentityDifferentEvidenceReference = CreateAdmittedEvidence().Require(PreIKAnimationSemanticRole.ForwardWalk);
        Assert.NotSame(admitted, sameIdentityDifferentEvidenceReference);
        Assert.Equal(admitted.AdmissionIdentity, sameIdentityDifferentEvidenceReference.AdmissionIdentity);
        Assert.Equal(admitted.ImmutableProvenanceIdentity, sameIdentityDifferentEvidenceReference.ImmutableProvenanceIdentity);
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Left = sample.Contacts.Left! with
                    {
                        AdmittedEvidence = sameIdentityDifferentEvidenceReference
                    },
                },
            }),
            "admission evidence reference or immutable identity tuple does not match");

        PreIKAdmittedAnimationEvidence leftPermissionMismatch = CreateAdmittedEvidence(
            "anim-002:catalogue:restricted-left-support",
            leftTransitions: new HashSet<PreIKContactTransition> { PreIKContactTransition.Acquire })
            .Require(PreIKAnimationSemanticRole.ForwardWalk);
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                AdmittedAnimationEvidence = leftPermissionMismatch,
                Contacts = sample.Contacts! with
                {
                    Left = sample.Contacts.Left! with
                    {
                        AdmittedEvidence = leftPermissionMismatch
                    },
                    Right = sample.Contacts.Right! with
                    {
                        AdmittedEvidence = leftPermissionMismatch
                    },
                },
            }),
            "role-foot permission does not permit left contact transition");

        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                ObservedAnimationRole = PreIKObservedAnimationRole.Available(
                    PreIKAnimationSemanticRole.SideStep,
                    "synthetic runtime graph selected SideStep")
            }),
            "observed runtime composition role 'SideStep' does not match admitted ANIM evidence role 'ForwardWalk'");

        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Pose = sample.Pose! with
                {
                    RootWorld = null
                },
                PostIK = new PostIKOutputEvidence(IsPresent: true, IsValid: true),
            }),
            "valid post-IK output cannot substitute for failed pre-IK evidence");
    }

    [Headless]
    [Fact]
    public void RuntimeCompositionOracle_MissingAdmittedEvidenceOrFabricatedCallerFlags_CannotPassFinalAggregate()
    {
        PreIKAdmittedAnimationEvidence admitted = CreateAdmittedEvidence().Require(PreIKAnimationSemanticRole.ForwardWalk);
        AssertFails(
            Mutate(admitted, 1, sample => sample with { AdmittedAnimationEvidence = null }),
            "contact admission evidence reference or immutable identity tuple does not match");
        AssertFails(
            Mutate(admitted, 1, sample => sample with
            {
                Contacts = sample.Contacts! with
                {
                    Left = new PreIKContactTransitionEvidence(
                        IsAvailable: true,
                        Transition: PreIKContactTransition.FullSupport,
                        AdmittedEvidence: null)
                },
            }),
            "left contact admission evidence reference or immutable identity tuple does not match");
    }

    /// <summary>Both player and NPC graph fixtures are sampled manually before every modifier and fail closed.</summary>
    [Headless]
    [Fact]
    public async Task RuntimeCompositionOracle_NpcGraphSamplesBeforeModifiers_RemainsBlockedWithoutAdmittedChannels()
        => await AssertGraphCaptureBlockedAsync(NpcScenePath, "NPCWalkF");

    /// <summary>Both player and NPC graph fixtures are sampled manually before every modifier and fail closed.</summary>
    [Headless]
    [Fact]
    public async Task RuntimeCompositionOracle_PlayerGraphSamplesBeforeModifiers_RemainsBlockedWithoutAdmittedChannels()
        => await AssertGraphCaptureBlockedAsync(PlayerScenePath, "PlayerTravel");

    private static async Task AssertGraphCaptureBlockedAsync(string scenePath, string stateName)
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(scenePath).Instantiate();
        List<SkeletonModifier3D> modifiers = DisableModifiersBeforeFixtureEntersSceneTree(root);
        sceneTree.Root.AddChild(root);
        try
        {
            await WaitForNextFrameAsync(sceneTree);
            AssertModifiersInactive(modifiers, "after _Ready and scene entry");

            AnimationTree tree = root.GetNode<AnimationTree>("AnimationTree");
            Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");
            tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Manual;
            tree.SetProcess(false);
            tree.SetPhysicsProcess(false);
            tree.Active = true;
            AnimationNodeStateMachinePlayback playback = tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
                ?? throw new Xunit.Sdk.XunitException($"Expected '{scenePath}' to expose state-machine playback.");
            playback.Start(stateName, reset: true);

            AdvanceBeforeModifiers(tree, modifiers, 0d, "initial state evaluation");
            int rootBone = RequireBone(skeleton, "Root");
            int hipsBone = RequireBone(skeleton, "Hips");
            int leftFootBone = RequireBone(skeleton, "LeftFoot");
            int rightFootBone = RequireBone(skeleton, "RightFoot");
            List<PreIKRuntimeCompositionSample> captured = CaptureGraphSamples(
                tree,
                skeleton,
                rootBone,
                hipsBone,
                leftFootBone,
                rightFootBone,
                modifiers);

            PreIKRuntimeCompositionResult result = PreIKRuntimeCompositionOracle.Evaluate(
                captured,
                PreIKContinuityLimitPopulation.AllApplicable);
            PreIKRuntimeCompositionResult postIKSubstitutionResult = PreIKRuntimeCompositionOracle.Evaluate(
                [.. captured.Select(sample => sample with { PostIK = new PostIKOutputEvidence(IsPresent: true, IsValid: true) })],
                PreIKContinuityLimitPopulation.AllApplicable);

            Assert.Equal(stateName, playback.GetCurrentNode().ToString());
            AssertGraphChannelsWereCapturedHonestly(captured);
            AssertModifiersInactive(modifiers, "after all manual AnimationTree.Advance calls");
            AssertBlockedForUnavailableGraphChannels(result);
            Assert.False(postIKSubstitutionResult.Passed);
            Assert.Contains(
                postIKSubstitutionResult.Rejections,
                rejection => rejection.Reason.Contains("valid post-IK output cannot substitute for failed pre-IK evidence", StringComparison.Ordinal));
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static List<PreIKRuntimeCompositionSample> CaptureGraphSamples(
        AnimationTree tree,
        Skeleton3D skeleton,
        int rootBone,
        int hipsBone,
        int leftFootBone,
        int rightFootBone,
        IReadOnlyList<SkeletonModifier3D> modifiers)
    {
        const double tick = 1d / 60d;
        List<PreIKRuntimeCompositionSample> captured = [];
        for (int sampleIndex = 0; sampleIndex < 3; sampleIndex++)
        {
            AdvanceBeforeModifiers(tree, modifiers, tick, $"captured sample {sampleIndex}");
            PreIKObservedAnimationRole observedRole = ObserveUnavailableRoleFromRuntimeState(tree);
            captured.Add(new PreIKRuntimeCompositionSample(
                $"manual-advance:{sampleIndex}",
                null,
                new PreIKBodyPose(
                    BoneWorldPosition(skeleton, rootBone),
                    BoneWorldPosition(skeleton, hipsBone),
                    BoneWorldPosition(skeleton, leftFootBone),
                    BoneWorldPosition(skeleton, rightFootBone)),
                NormalisedPhase: null,
                SeamHandoffIdentity: null,
                RetimingPlaybackRateIdentity: null,
                Contacts: null,
                Continuity: null,
                AdmittedAnimationEvidence: null,
                ObservedAnimationRole: observedRole,
                UnavailableChannels: _unavailableGraphChannels));
        }

        return captured;
    }

    private static void AssertGraphChannelsWereCapturedHonestly(IReadOnlyList<PreIKRuntimeCompositionSample> samples)
    {
        Assert.Equal(3, samples.Count);
        Assert.All(samples, sample =>
        {
            Assert.StartsWith("manual-advance:", sample.SourceFrameIdentity);
            Assert.Null(sample.MonotonicTimeSeconds);
            _ = Assert.NotNull(sample.Pose?.RootWorld);
            _ = Assert.NotNull(sample.Pose?.HipsWorld);
            _ = Assert.NotNull(sample.Pose?.LeftFootWorld);
            _ = Assert.NotNull(sample.Pose?.RightFootWorld);
            Assert.Null(sample.NormalisedPhase);
            Assert.Null(sample.SeamHandoffIdentity);
            Assert.Null(sample.RetimingPlaybackRateIdentity);
            Assert.Null(sample.Contacts);
            Assert.Null(sample.Continuity);
            Assert.Null(sample.AdmittedAnimationEvidence);
            PreIKObservedAnimationRole observedRole = Assert.IsType<PreIKObservedAnimationRole>(sample.ObservedAnimationRole);
            Assert.False(observedRole.IsAvailable);
            Assert.Null(observedRole.SemanticRole);
            Assert.Contains("parameters/States/playback", observedRole.SourceDescription, StringComparison.Ordinal);
            Assert.Same(_unavailableGraphChannels, sample.UnavailableChannels);
        });
    }

    private static void AssertBlockedForUnavailableGraphChannels(PreIKRuntimeCompositionResult result)
    {
        Assert.False(result.Passed);
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("NormalisedPhase channel unavailable", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("SeamHandoff channel unavailable", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("RetimingPlaybackRate channel unavailable", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("LeftContactTransition channel unavailable", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("RightContactTransition channel unavailable", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("continuity evidence unavailable", StringComparison.Ordinal));
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains("observed runtime composition role unavailable", StringComparison.Ordinal));
    }

    private static PreIKAdmittedAnimationEvidencePackage CreateAdmittedEvidence(
        string provenanceIdentity = "anim-002:catalogue:reference-female-forward-walk",
        string admissionIdentity = "anim-002:admission:reference-female",
        IReadOnlySet<PreIKContactTransition>? leftTransitions = null)
        => PreIKAdmittedAnimationEvidencePackage.AdmitForTesting(
            provenanceIdentity,
            admissionIdentity,
            new Dictionary<PreIKAnimationSemanticRole, IReadOnlyList<PreIKRoleFootPermission>>
            {
                [PreIKAnimationSemanticRole.ForwardWalk] =
                [
                    new(PreIKAnatomicalFoot.Left, leftTransitions ?? new HashSet<PreIKContactTransition>
                    {
                        PreIKContactTransition.Acquire,
                        PreIKContactTransition.FullSupport,
                        PreIKContactTransition.Release,
                    }),
                    new(PreIKAnatomicalFoot.Right, new HashSet<PreIKContactTransition>
                    {
                        PreIKContactTransition.Acquire,
                        PreIKContactTransition.FullSupport,
                        PreIKContactTransition.Release,
                    }),
                ],
            });

    private static List<PreIKRuntimeCompositionSample> CreateCleanSamples(PreIKAdmittedAnimationEvidence admitted)
    {
        List<PreIKRuntimeCompositionSample> samples =
        [
            CreateSample(admitted, "frame-0", 0d, 0f, Vector3.Zero, Vector3.Zero, Vector3.Left, Vector3.Right,
                PreIKContactTransition.Acquire, PreIKContactTransition.FullSupport),
            CreateSample(admitted, "frame-1", 0.1d, 0.1f, new Vector3(0.1f, 0f, 0f), new Vector3(0.1f, 1f, 0f),
                new Vector3(-0.9f, 0f, 0f), new Vector3(1.1f, 0f, 0f),
                PreIKContactTransition.FullSupport, PreIKContactTransition.Release),
            CreateSample(admitted, "frame-2", 0.2d, 0.2f, new Vector3(0.2f, 0f, 0f), new Vector3(0.2f, 1f, 0f),
                new Vector3(-0.8f, 0f, 0f), new Vector3(1.2f, 0f, 0f),
                PreIKContactTransition.Release, PreIKContactTransition.Acquire),
        ];
        return AddObservedContinuity(samples);
    }

    private static PreIKRuntimeCompositionSample CreateSample(
        PreIKAdmittedAnimationEvidence admitted,
        string frame,
        double time,
        float phase,
        Vector3 root,
        Vector3 hips,
        Vector3 leftFoot,
        Vector3 rightFoot,
        PreIKContactTransition leftTransition,
        PreIKContactTransition rightTransition)
        => new(
            frame,
            time,
            new PreIKBodyPose(root, hips, leftFoot, rightFoot),
            phase,
            "entry->travel",
            "rate:1",
            new PreIKContactTransitionFrame(
                new PreIKContactTransitionEvidence(true, leftTransition, admitted),
                new PreIKContactTransitionEvidence(true, rightTransition, admitted)),
            Continuity: null,
            AdmittedAnimationEvidence: admitted,
            ObservedAnimationRole: PreIKObservedAnimationRole.Available(
                PreIKAnimationSemanticRole.ForwardWalk,
                "synthetic runtime graph selected the ForwardWalk leaf"));

    private static List<SkeletonModifier3D> DisableModifiersBeforeFixtureEntersSceneTree(Node root)
    {
        Assert.False(root.IsInsideTree(), "Every SkeletonModifier3D must be disabled before fixture scene entry.");
        List<SkeletonModifier3D> modifiers = [.. root.FindChildren("*", nameof(SkeletonModifier3D), recursive: true, owned: false)
            .OfType<SkeletonModifier3D>()];
        Assert.NotEmpty(modifiers);
        foreach (SkeletonModifier3D modifier in modifiers)
        {
            modifier.Active = false;
        }

        // CharacterIK is the sole actuator of leg-modifier activity (IK-003 TR2) and re-activates the leg solve
        // whenever a foot provider applies, as NPC foot planting does every tick; stop it so modifiers stay disabled.
        foreach (CharacterIK characterIK in root.FindChildren("*", nameof(Node3D), recursive: true, owned: false).OfType<CharacterIK>())
        {
            characterIK.Active = false;
        }

        AssertModifiersInactive(modifiers, "before fixture scene entry");
        return modifiers;
    }

    private static void AdvanceBeforeModifiers(
        AnimationTree tree,
        IReadOnlyList<SkeletonModifier3D> modifiers,
        double delta,
        string captureStep)
    {
        AssertModifiersInactive(modifiers, $"immediately before manual AnimationTree.Advance for {captureStep}");
        tree.Advance(delta);
        AssertModifiersInactive(modifiers, $"immediately after manual AnimationTree.Advance for {captureStep}");
    }

    private static PreIKObservedAnimationRole ObserveUnavailableRoleFromRuntimeState(AnimationTree tree)
    {
        AnimationNodeStateMachinePlayback playback = tree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
            ?? throw new Xunit.Sdk.XunitException("Expected evaluated AnimationTree to expose state-machine playback.");
        string currentState = playback.GetCurrentNode().ToString();
        return PreIKObservedAnimationRole.Unavailable(
            $"AnimationTree property 'parameters/States/playback' reports current state '{currentState}' via " +
            "AnimationNodeStateMachinePlayback.GetCurrentNode(); it exposes no public active leaf-role property.");
    }

    private static void AssertModifiersInactive(IReadOnlyList<SkeletonModifier3D> modifiers, string checkpoint)
        => Assert.All(modifiers, modifier => Assert.False(modifier.Active, $"Skeleton modifier '{modifier.Name}' was active {checkpoint}."));

    private static List<PreIKRuntimeCompositionSample> AddObservedContinuity(IReadOnlyList<PreIKRuntimeCompositionSample> samples)
    {
        List<PreIKRuntimeCompositionSample> result = [samples[0]];
        for (int index = 1; index < samples.Count; index++)
        {
            PreIKRuntimeCompositionSample previous = samples[index - 1];
            PreIKRuntimeCompositionSample current = samples[index];
            PreIKBodyPose previousPose = previous.Pose!;
            PreIKBodyPose currentPose = current.Pose!;
            result.Add(current with
            {
                Continuity = new PreIKContinuityEvidence(
                    currentPose.RootWorld!.Value - previousPose.RootWorld!.Value,
                    currentPose.HipsWorld!.Value - previousPose.HipsWorld!.Value,
                    currentPose.LeftFootWorld!.Value - previousPose.LeftFootWorld!.Value,
                    currentPose.RightFootWorld!.Value - previousPose.RightFootWorld!.Value,
                    current.NormalisedPhase!.Value - previous.NormalisedPhase!.Value,
                    current.SeamHandoffIdentity,
                    current.RetimingPlaybackRateIdentity,
                    current.Contacts?.Left?.Transition,
                    current.Contacts?.Right?.Transition),
            });
        }

        return result;
    }

    private static List<PreIKRuntimeCompositionSample> Mutate(
        PreIKAdmittedAnimationEvidence admitted,
        int index,
        Func<PreIKRuntimeCompositionSample, PreIKRuntimeCompositionSample> mutation)
    {
        List<PreIKRuntimeCompositionSample> samples = CreateCleanSamples(admitted);
        samples[index] = mutation(samples[index]);
        return samples;
    }

    private static void AssertPasses(IReadOnlyList<PreIKRuntimeCompositionSample> samples, PreIKContinuityLimitPopulation population)
    {
        PreIKRuntimeCompositionResult result = PreIKRuntimeCompositionOracle.Evaluate(samples, population);
        Assert.True(result.Passed, FormatRejections(result));
    }

    private static void AssertFails(IReadOnlyList<PreIKRuntimeCompositionSample> samples, string reason)
        => AssertFails(samples, PreIKContinuityLimitPopulation.AllApplicable, reason);

    private static void AssertFails(
        IReadOnlyList<PreIKRuntimeCompositionSample> samples,
        PreIKContinuityLimitPopulation population,
        string reason)
    {
        PreIKRuntimeCompositionResult result = PreIKRuntimeCompositionOracle.Evaluate(samples, population);
        Assert.False(result.Passed, "Each mutation must reach the final aggregate result.");
        Assert.Contains(result.Rejections, rejection => rejection.Reason.Contains(reason, StringComparison.Ordinal));
    }

    private static int RequireBone(Skeleton3D skeleton, string name)
    {
        int index = skeleton.FindBone(name);
        return index >= 0
            ? index
            : throw new Xunit.Sdk.XunitException($"Expected pre-IK graph skeleton to expose '{name}'.");
    }

    private static Vector3 BoneWorldPosition(Skeleton3D skeleton, int bone)
        => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone).Origin;

    private static string FormatRejections(PreIKRuntimeCompositionResult result)
        => string.Join("; ", result.Rejections.Select(rejection => $"{rejection.SampleIndex}: {rejection.Reason}"));
}
