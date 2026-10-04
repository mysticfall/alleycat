using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>Every pre-IK continuity channel required for an applicable locomotion composition sample.</summary>
public enum PreIKContinuityDimension
{
    Root,
    Hips,
    LeftFoot,
    RightFoot,
    NormalisedPhase,
    SeamHandoff,
    RetimingPlaybackRate,
    LeftContactTransition,
    RightContactTransition,
}

/// <summary>Authored contact lifecycle value as observed before any IK modifier runs.</summary>
public enum PreIKContactTransition
{
    Unknown,
    Acquire,
    FullSupport,
    Release,
}

/// <summary>ANIM semantic role whose admitted package evidence authorises a pre-IK contact transition.</summary>
public enum PreIKAnimationSemanticRole
{
    ForwardWalk,
    SideStep,
}

/// <summary>
/// Independently observed role emitted by the evaluated runtime graph. This is deliberately distinct from ANIM
/// admission evidence: an unavailable runtime observation blocks the sample rather than inferring a role from ANIM.
/// </summary>
public sealed class PreIKObservedAnimationRole
{
    private PreIKObservedAnimationRole(
        bool isAvailable,
        PreIKAnimationSemanticRole? semanticRole,
        string sourceDescription)
    {
        IsAvailable = isAvailable;
        SemanticRole = semanticRole;
        SourceDescription = sourceDescription;
    }

    /// <summary>Whether the runtime graph exposed an exact active semantic role.</summary>
    public bool IsAvailable
    {
        get;
    }

    /// <summary>The exact graph-observed role, when <see cref="IsAvailable"/>.</summary>
    public PreIKAnimationSemanticRole? SemanticRole
    {
        get;
    }

    /// <summary>Exact graph node, state, or property consulted for this observation.</summary>
    public string SourceDescription
    {
        get;
    }

    public static PreIKObservedAnimationRole Available(
        PreIKAnimationSemanticRole semanticRole,
        string sourceDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDescription);
        return new PreIKObservedAnimationRole(true, semanticRole, sourceDescription);
    }

    public static PreIKObservedAnimationRole Unavailable(string sourceDescription)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDescription);
        return new PreIKObservedAnimationRole(false, null, sourceDescription);
    }
}

/// <summary>Anatomical foot to which an admitted contact permission applies.</summary>
public enum PreIKAnatomicalFoot
{
    Left,
    Right,
}

/// <summary>Required pre-IK body channels captured from the evaluated animation graph.</summary>
public sealed record PreIKBodyPose(
    Vector3? RootWorld,
    Vector3? HipsWorld,
    Vector3? LeftFootWorld,
    Vector3? RightFootWorld);

/// <summary>One immutable role-foot permission supplied by admitted ANIM authoring/package evidence.</summary>
public sealed record PreIKRoleFootPermission(
    PreIKAnatomicalFoot Foot,
    IReadOnlySet<PreIKContactTransition> PermittedTransitions);

/// <summary>
/// Test-only admitted ANIM authoring/package evidence. Its constructor is private so a contact observation cannot
/// manufacture authority with a caller-selected flag or source string.
/// </summary>
public sealed class PreIKAdmittedAnimationEvidence
{
    private readonly IReadOnlyDictionary<PreIKAnatomicalFoot, IReadOnlySet<PreIKContactTransition>> _permissions;

    private PreIKAdmittedAnimationEvidence(
        PreIKAnimationSemanticRole semanticRole,
        string immutableProvenanceIdentity,
        string admissionIdentity,
        IReadOnlyDictionary<PreIKAnatomicalFoot, IReadOnlySet<PreIKContactTransition>> permissions)
    {
        SemanticRole = semanticRole;
        ImmutableProvenanceIdentity = immutableProvenanceIdentity;
        AdmissionIdentity = admissionIdentity;
        _permissions = permissions;
    }

    /// <summary>Semantic role admitted by the ANIM package.</summary>
    public PreIKAnimationSemanticRole SemanticRole
    {
        get;
    }

    /// <summary>Immutable identity of the admitted authoring/package provenance.</summary>
    public string ImmutableProvenanceIdentity
    {
        get;
    }

    /// <summary>Immutable identity of this package-admission decision.</summary>
    public string AdmissionIdentity
    {
        get;
    }

    internal bool Permits(PreIKAnatomicalFoot foot, PreIKContactTransition transition)
        => _permissions.TryGetValue(foot, out IReadOnlySet<PreIKContactTransition>? permitted)
           && permitted.Contains(transition);

    internal static PreIKAdmittedAnimationEvidence Create(
        PreIKAnimationSemanticRole semanticRole,
        string immutableProvenanceIdentity,
        string admissionIdentity,
        IReadOnlyDictionary<PreIKAnatomicalFoot, IReadOnlySet<PreIKContactTransition>> permissions)
        => new(semanticRole, immutableProvenanceIdentity, admissionIdentity, permissions);
}

/// <summary>
/// Test-only ANIM admission package keyed by semantic role and anatomical foot. It is the sole issuer of evidence
/// accepted by this diagnostic oracle.
/// </summary>
public sealed class PreIKAdmittedAnimationEvidencePackage
{
    private readonly IReadOnlyDictionary<PreIKAnimationSemanticRole, PreIKAdmittedAnimationEvidence> _evidence;

    private PreIKAdmittedAnimationEvidencePackage(
        IReadOnlyDictionary<PreIKAnimationSemanticRole, PreIKAdmittedAnimationEvidence> evidence)
    {
        _evidence = evidence;
    }

    public static PreIKAdmittedAnimationEvidencePackage AdmitForTesting(
        string immutableProvenanceIdentity,
        string admissionIdentity,
        IReadOnlyDictionary<PreIKAnimationSemanticRole, IReadOnlyList<PreIKRoleFootPermission>> roleFootPermissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(immutableProvenanceIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(admissionIdentity);
        ArgumentNullException.ThrowIfNull(roleFootPermissions);

        Dictionary<PreIKAnimationSemanticRole, PreIKAdmittedAnimationEvidence> admitted = [];
        foreach ((PreIKAnimationSemanticRole role, IReadOnlyList<PreIKRoleFootPermission> permissions) in roleFootPermissions)
        {
            Dictionary<PreIKAnatomicalFoot, IReadOnlySet<PreIKContactTransition>> byFoot = [];
            foreach (PreIKRoleFootPermission permission in permissions)
            {
                if (!byFoot.TryAdd(permission.Foot, new HashSet<PreIKContactTransition>(permission.PermittedTransitions)))
                {
                    throw new ArgumentException($"Duplicate ANIM role-foot admission for '{role}:{permission.Foot}'.", nameof(roleFootPermissions));
                }
            }

            admitted.Add(role, PreIKAdmittedAnimationEvidence.Create(
                role,
                immutableProvenanceIdentity,
                $"{admissionIdentity}:{role}",
                byFoot));
        }

        return new PreIKAdmittedAnimationEvidencePackage(admitted);
    }

    public PreIKAdmittedAnimationEvidence Require(PreIKAnimationSemanticRole semanticRole)
        => _evidence.TryGetValue(semanticRole, out PreIKAdmittedAnimationEvidence? evidence)
            ? evidence
            : throw new InvalidOperationException($"No admitted ANIM evidence exists for semantic role '{semanticRole}'.");
}

/// <summary>Contact transition observed before IK, bound to immutable admitted ANIM evidence.</summary>
public sealed record PreIKContactTransitionEvidence(
    bool IsAvailable,
    PreIKContactTransition Transition,
    PreIKAdmittedAnimationEvidence? AdmittedEvidence);

/// <summary>Both applicable role-foot contact-transition observations at one pre-IK graph sample.</summary>
public sealed record PreIKContactTransitionFrame(
    PreIKContactTransitionEvidence? Left,
    PreIKContactTransitionEvidence? Right);

/// <summary>
/// Exact continuity evidence supplied for a concrete sample relationship. These are expected graph-output deltas,
/// not correction, slip, or anchor limits; numeric acceptance policy remains deferred to the prototype gate.
/// </summary>
public sealed record PreIKContinuityEvidence(
    Vector3? ExpectedRootDelta,
    Vector3? ExpectedHipsDelta,
    Vector3? ExpectedLeftFootDelta,
    Vector3? ExpectedRightFootDelta,
    float? ExpectedNormalisedPhaseDelta,
    string? ExpectedSeamHandoffIdentity,
    string? ExpectedRetimingPlaybackRateIdentity,
    PreIKContactTransition? ExpectedLeftTransition,
    PreIKContactTransition? ExpectedRightTransition);

/// <summary>Explicit reason a required graph channel could not be captured without fabricating it.</summary>
public sealed record PreIKUnavailableChannelEvidence(
    IReadOnlyDictionary<PreIKContinuityDimension, string>? Reasons,
    string? ContinuityReason = null)
{
    public string? GetReason(PreIKContinuityDimension dimension)
        => Reasons is not null && Reasons.TryGetValue(dimension, out string? reason) && !string.IsNullOrWhiteSpace(reason)
            ? reason
            : null;
}

/// <summary>Optional downstream observation retained only to prove it cannot replace pre-IK evidence.</summary>
public sealed record PostIKOutputEvidence(bool IsPresent, bool IsValid);

/// <summary>One runtime composition sample taken after AnimationTree evaluation and before all modifiers.</summary>
public sealed record PreIKRuntimeCompositionSample(
    string? SourceFrameIdentity,
    double? MonotonicTimeSeconds,
    PreIKBodyPose? Pose,
    float? NormalisedPhase,
    string? SeamHandoffIdentity,
    string? RetimingPlaybackRateIdentity,
    PreIKContactTransitionFrame? Contacts,
    PreIKContinuityEvidence? Continuity,
    PreIKAdmittedAnimationEvidence? AdmittedAnimationEvidence = null,
    PreIKObservedAnimationRole? ObservedAnimationRole = null,
    PreIKUnavailableChannelEvidence? UnavailableChannels = null,
    PostIKOutputEvidence? PostIK = null);

/// <summary>
/// Declared population of applicable continuity channels. A caller cannot silently shrink this population: all
/// required pre-IK channels are validated for every locomotion sample in this Stage 2 oracle.
/// </summary>
public sealed record PreIKContinuityLimitPopulation(IReadOnlyList<PreIKContinuityDimension>? Dimensions)
{
    public static PreIKContinuityLimitPopulation AllApplicable
    {
        get;
    } = new(Enum.GetValues<PreIKContinuityDimension>());
}

/// <summary>One deterministic pre-IK composition rejection.</summary>
public sealed record PreIKRuntimeCompositionRejection(int SampleIndex, string Reason);

/// <summary>Strict aggregate result; diagnostics are test-only observations with no steering authority.</summary>
public sealed record PreIKRuntimeCompositionResult(IReadOnlyList<PreIKRuntimeCompositionRejection> Rejections)
{
    public bool Passed => Rejections.Count == 0;
}

/// <summary>
/// Pure, fail-closed Stage 2 oracle for runtime animation composition before IK modifiers. It validates
/// supplied evidence only and has no dependency on ANIM package admission, final-output validation, NAV, or CTRL.
/// </summary>
public static class PreIKRuntimeCompositionOracle
{
    public static PreIKRuntimeCompositionResult Evaluate(
        IReadOnlyList<PreIKRuntimeCompositionSample> samples,
        PreIKContinuityLimitPopulation? continuityPopulation)
    {
        List<PreIKRuntimeCompositionRejection> rejections = [];
        ValidatePopulation(continuityPopulation, rejections);
        if (samples.Count == 0)
        {
            rejections.Add(new PreIKRuntimeCompositionRejection(-1, "pre-IK sample population was empty"));
            return new PreIKRuntimeCompositionResult(rejections);
        }

        HashSet<string> sourceFrames = new(StringComparer.Ordinal);
        double? previousTime = null;
        for (int index = 0; index < samples.Count; index++)
        {
            PreIKRuntimeCompositionSample sample = samples[index];
            int rejectionCount = rejections.Count;
            ValidateSample(sample, index, sourceFrames, ref previousTime, rejections);
            if (index > 0)
            {
                ValidateContinuity(samples[index - 1], sample, index, rejections);
            }

            if (sample.PostIK is { IsPresent: true, IsValid: true } && rejections.Count > rejectionCount)
            {
                rejections.Add(new PreIKRuntimeCompositionRejection(
                    index,
                    "valid post-IK output cannot substitute for failed pre-IK evidence"));
            }
        }

        return new PreIKRuntimeCompositionResult(rejections);
    }

    private static void ValidatePopulation(
        PreIKContinuityLimitPopulation? population,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        if (population?.Dimensions is not { Count: > 0 } dimensions)
        {
            rejections.Add(new PreIKRuntimeCompositionRejection(-1, "continuity limit population was empty"));
            return;
        }

        HashSet<PreIKContinuityDimension> declared = [];
        foreach (PreIKContinuityDimension dimension in dimensions)
        {
            if (!declared.Add(dimension))
            {
                rejections.Add(new PreIKRuntimeCompositionRejection(-1, $"duplicate continuity dimension '{dimension}'"));
            }
        }

        foreach (PreIKContinuityDimension dimension in Enum.GetValues<PreIKContinuityDimension>())
        {
            if (!declared.Contains(dimension))
            {
                rejections.Add(new PreIKRuntimeCompositionRejection(-1, $"missing applicable continuity dimension '{dimension}'"));
            }
        }
    }

    private static void ValidateSample(
        PreIKRuntimeCompositionSample sample,
        int index,
        HashSet<string> sourceFrames,
        ref double? previousTime,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        bool hasSourceFrame = !string.IsNullOrWhiteSpace(sample.SourceFrameIdentity);
        bool hasMonotonicTime = sample.MonotonicTimeSeconds is double suppliedTime && double.IsFinite(suppliedTime);
        if (!hasSourceFrame && !hasMonotonicTime)
        {
            Reject(index, "missing source/frame identity or monotonic time", rejections);
        }

        if (hasSourceFrame && !sourceFrames.Add(sample.SourceFrameIdentity!))
        {
            Reject(index, "repeated source/frame identity", rejections);
        }

        if (sample.MonotonicTimeSeconds is double time)
        {
            if (!double.IsFinite(time))
            {
                Reject(index, "non-finite monotonic time", rejections);
            }
            else
            {
                if (previousTime is double prior && time <= prior)
                {
                    Reject(index, "monotonic time did not advance", rejections);
                }

                previousTime = time;
            }
        }

        ValidatePose(sample.Pose, index, rejections);
        ValidateObservedRole(sample.ObservedAnimationRole, index, rejections);
        if (sample.NormalisedPhase is not float phase || !float.IsFinite(phase) || phase < 0f || phase > 1f)
        {
            RejectUnavailableOrMissing(sample, PreIKContinuityDimension.NormalisedPhase, "missing or invalid normalised phase", index, rejections);
        }

        if (string.IsNullOrWhiteSpace(sample.SeamHandoffIdentity))
        {
            RejectUnavailableOrMissing(sample, PreIKContinuityDimension.SeamHandoff, "missing seam/handoff identity", index, rejections);
        }

        if (string.IsNullOrWhiteSpace(sample.RetimingPlaybackRateIdentity))
        {
            RejectUnavailableOrMissing(sample, PreIKContinuityDimension.RetimingPlaybackRate, "missing retiming/playback-rate identity", index, rejections);
        }

        ValidateContact(sample, sample.Contacts?.Left, PreIKAnatomicalFoot.Left, index, rejections);
        ValidateContact(sample, sample.Contacts?.Right, PreIKAnatomicalFoot.Right, index, rejections);
    }

    private static void ValidateObservedRole(
        PreIKObservedAnimationRole? observedRole,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        if (observedRole is null)
        {
            Reject(index, "missing independently observed runtime composition role", rejections);
            return;
        }

        if (!observedRole.IsAvailable || observedRole.SemanticRole is null)
        {
            Reject(index, $"observed runtime composition role unavailable: {observedRole.SourceDescription}", rejections);
        }
    }

    private static void ValidatePose(
        PreIKBodyPose? pose,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        if (pose is null)
        {
            Reject(index, "missing pre-IK Root, Hips, and foot pose population", rejections);
            return;
        }

        ValidateVector(pose.RootWorld, "Root", index, rejections);
        ValidateVector(pose.HipsWorld, "Hips", index, rejections);
        ValidateVector(pose.LeftFootWorld, "left foot", index, rejections);
        ValidateVector(pose.RightFootWorld, "right foot", index, rejections);
    }

    private static void ValidateVector(
        Vector3? value,
        string name,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        if (value is not Vector3 vector || !IsFinite(vector))
        {
            Reject(index, $"missing or invalid pre-IK {name} pose", rejections);
        }
    }

    private static void ValidateContact(
        PreIKRuntimeCompositionSample sample,
        PreIKContactTransitionEvidence? contact,
        PreIKAnatomicalFoot foot,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        if (contact is null)
        {
            RejectUnavailableOrMissing(
                sample,
                foot == PreIKAnatomicalFoot.Left ? PreIKContinuityDimension.LeftContactTransition : PreIKContinuityDimension.RightContactTransition,
                $"missing authoritative {foot.ToString().ToLowerInvariant()} contact transition evidence",
                index,
                rejections);
            return;
        }

        if (!contact.IsAvailable || contact.Transition == PreIKContactTransition.Unknown)
        {
            Reject(index, $"invalid authoritative {foot.ToString().ToLowerInvariant()} contact transition evidence", rejections);
            return;
        }

        if (sample.AdmittedAnimationEvidence is null || contact.AdmittedEvidence is null
            || !ReferenceEquals(sample.AdmittedAnimationEvidence, contact.AdmittedEvidence)
            || !HasMatchingAdmissionIdentityTuple(sample.AdmittedAnimationEvidence, contact.AdmittedEvidence))
        {
            string sampleAdmission = sample.AdmittedAnimationEvidence?.AdmissionIdentity ?? "none";
            string sampleProvenance = sample.AdmittedAnimationEvidence?.ImmutableProvenanceIdentity ?? "none";
            string sampleRole = sample.AdmittedAnimationEvidence?.SemanticRole.ToString() ?? "none";
            string contactAdmission = contact.AdmittedEvidence?.AdmissionIdentity ?? "none";
            string contactProvenance = contact.AdmittedEvidence?.ImmutableProvenanceIdentity ?? "none";
            string contactRole = contact.AdmittedEvidence?.SemanticRole.ToString() ?? "none";
            Reject(
                index,
                $"{foot.ToString().ToLowerInvariant()} contact admission evidence reference or immutable identity tuple does not match the sample's admitted ANIM evidence " +
                $"(sample role '{sampleRole}' admission '{sampleAdmission}' provenance '{sampleProvenance}'; " +
                $"contact role '{contactRole}' admission '{contactAdmission}' provenance '{contactProvenance}')",
                rejections);
            return;
        }

        if (sample.ObservedAnimationRole is not { IsAvailable: true, SemanticRole: PreIKAnimationSemanticRole observedRole }
            || observedRole != sample.AdmittedAnimationEvidence.SemanticRole)
        {
            string observedRoleName = sample.ObservedAnimationRole?.SemanticRole.ToString() ?? "unavailable";
            Reject(
                index,
                $"observed runtime composition role '{observedRoleName}' does not match admitted ANIM evidence role " +
                $"'{sample.AdmittedAnimationEvidence.SemanticRole}' for {foot.ToString().ToLowerInvariant()} contact transition",
                rejections);
            return;
        }

        if (!contact.AdmittedEvidence.Permits(foot, contact.Transition))
        {
            Reject(index, $"admitted ANIM role-foot permission does not permit {foot.ToString().ToLowerInvariant()} contact transition '{contact.Transition}'", rejections);
        }
    }

    /// <summary>
    /// Reference identity remains the non-forgeable admission token because evidence instances can only be issued by
    /// <see cref="PreIKAdmittedAnimationEvidencePackage"/>. The immutable role/admission/provenance tuple is also
    /// compared, so accepting an instance never relies on loose string identity alone.
    /// </summary>
    private static bool HasMatchingAdmissionIdentityTuple(
        PreIKAdmittedAnimationEvidence sampleEvidence,
        PreIKAdmittedAnimationEvidence contactEvidence)
        => sampleEvidence.SemanticRole == contactEvidence.SemanticRole
           && string.Equals(sampleEvidence.AdmissionIdentity, contactEvidence.AdmissionIdentity, StringComparison.Ordinal)
           && string.Equals(sampleEvidence.ImmutableProvenanceIdentity, contactEvidence.ImmutableProvenanceIdentity, StringComparison.Ordinal);

    private static void ValidateContinuity(
        PreIKRuntimeCompositionSample previous,
        PreIKRuntimeCompositionSample current,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        PreIKContinuityEvidence? expected = current.Continuity;
        if (expected is null)
        {
            string? reason = current.UnavailableChannels?.ContinuityReason;
            Reject(index, string.IsNullOrWhiteSpace(reason) ? "missing continuity evidence" : $"continuity evidence unavailable: {reason}", rejections);
            return;
        }

        ValidateDelta(previous.Pose?.RootWorld, current.Pose?.RootWorld, expected.ExpectedRootDelta, "Root reset or continuity delta mismatch", index, rejections);
        ValidateDelta(previous.Pose?.HipsWorld, current.Pose?.HipsWorld, expected.ExpectedHipsDelta, "Hips continuity delta mismatch", index, rejections);
        ValidateDelta(previous.Pose?.LeftFootWorld, current.Pose?.LeftFootWorld, expected.ExpectedLeftFootDelta, "left-foot blend-seam continuity delta mismatch", index, rejections);
        ValidateDelta(previous.Pose?.RightFootWorld, current.Pose?.RightFootWorld, expected.ExpectedRightFootDelta, "right-foot blend-seam continuity delta mismatch", index, rejections);

        if (previous.NormalisedPhase is not float previousPhase
            || current.NormalisedPhase is not float currentPhase
            || expected.ExpectedNormalisedPhaseDelta is not float expectedPhaseDelta
            || currentPhase - previousPhase != expectedPhaseDelta)
        {
            Reject(index, "normalised phase jump or missing phase continuity evidence", rejections);
        }

        if (current.SeamHandoffIdentity != expected.ExpectedSeamHandoffIdentity)
        {
            Reject(index, "seam/handoff identity mismatch", rejections);
        }

        if (current.RetimingPlaybackRateIdentity != expected.ExpectedRetimingPlaybackRateIdentity)
        {
            Reject(index, "retiming/playback-rate identity mismatch", rejections);
        }

        if (current.Contacts?.Left?.Transition != expected.ExpectedLeftTransition)
        {
            Reject(index, "wrong left support transition", rejections);
        }

        if (current.Contacts?.Right?.Transition != expected.ExpectedRightTransition)
        {
            Reject(index, "wrong right support transition", rejections);
        }
    }

    private static void ValidateDelta(
        Vector3? previous,
        Vector3? current,
        Vector3? expected,
        string reason,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        if (previous is not Vector3 before || current is not Vector3 after || expected is not Vector3 delta
            || after - before != delta)
        {
            Reject(index, reason, rejections);
        }
    }

    private static void Reject(int sampleIndex, string reason, List<PreIKRuntimeCompositionRejection> rejections)
        => rejections.Add(new PreIKRuntimeCompositionRejection(sampleIndex, reason));

    private static void RejectUnavailableOrMissing(
        PreIKRuntimeCompositionSample sample,
        PreIKContinuityDimension dimension,
        string missingReason,
        int index,
        List<PreIKRuntimeCompositionRejection> rejections)
    {
        string? unavailableReason = sample.UnavailableChannels?.GetReason(dimension);
        Reject(index, string.IsNullOrWhiteSpace(unavailableReason)
            ? missingReason
            : $"{dimension} channel unavailable: {unavailableReason}", rejections);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
