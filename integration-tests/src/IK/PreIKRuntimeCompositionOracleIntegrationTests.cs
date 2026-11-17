using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Stage 2 pre-IK runtime-composition oracle coverage.</summary>
public sealed class PreIKRuntimeCompositionOracleIntegrationTests
{
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

    /// <summary>Unavailable channels cannot be replaced by post-IK output, independently of any production graph.</summary>
    [Headless]
    [Fact]
    public void RuntimeCompositionOracle_UnavailableChannels_RejectsPostIKSubstitution()
    {
        var sample = new PreIKRuntimeCompositionSample("synthetic:missing-channels", null,
            new PreIKBodyPose(Vector3.Zero, Vector3.Up, Vector3.Left, Vector3.Right),
            NormalisedPhase: null, SeamHandoffIdentity: null, RetimingPlaybackRateIdentity: null,
            Contacts: null, Continuity: null, AdmittedAnimationEvidence: null,
            ObservedAnimationRole: PreIKObservedAnimationRole.Unavailable("No semantic role supplied"),
            UnavailableChannels: _unavailableGraphChannels);
        AssertBlockedForUnavailableGraphChannels(PreIKRuntimeCompositionOracle.Evaluate(
            [sample, sample with { SourceFrameIdentity = "synthetic:next-missing-channels" }], PreIKContinuityLimitPopulation.AllApplicable));
        PreIKRuntimeCompositionResult substituted = PreIKRuntimeCompositionOracle.Evaluate(
            [sample with { PostIK = new PostIKOutputEvidence(IsPresent: true, IsValid: true) }],
            PreIKContinuityLimitPopulation.AllApplicable);
        Assert.False(substituted.Passed);
        Assert.Contains(substituted.Rejections, rejection => rejection.Reason.Contains(
            "valid post-IK output cannot substitute for failed pre-IK evidence", StringComparison.Ordinal));
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

    private static string FormatRejections(PreIKRuntimeCompositionResult result)
        => string.Join("; ", result.Rejections.Select(rejection => $"{rejection.SampleIndex}: {rejection.Reason}"));
}
