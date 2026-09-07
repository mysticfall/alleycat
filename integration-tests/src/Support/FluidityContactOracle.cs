using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>Confidence supplied by the authored/contact-lock evidence path, never inferred from pose height.</summary>
public enum FluidityContactConfidence
{
    Unknown,
    Low,
    Authoritative,
}

/// <summary>Contact lifecycle state supplied by the authoritative evidence source.</summary>
public enum FluidityContactPhase
{
    Unknown,
    Acquire,
    FullSupport,
    Release,
}

/// <summary>Whether a recorded case is expected to supply any support population.</summary>
public enum FluiditySupportApplicability
{
    Unknown,
    Inapplicable,
    Required,
}

/// <summary>
/// Evidence observed at one distinct validation stage. Availability, validity, provenance, and position are kept
/// together so a default finite position cannot stand in for an observation.
/// </summary>
public sealed record FluidityContactStageEvidence(
    bool IsAvailable,
    bool IsValid,
    int SourceGeneration,
    Vector3 FootWorld);

/// <summary>
/// One foot's separately observable pre-IK and post-IK contact evidence. Stage 1 deliberately accepts only
/// authoritative full-support evidence for support-slip measurements.
/// </summary>
public sealed record FluidityFootContactEvidence(
    string Foot,
    FluidityContactPhase Phase,
    FluidityContactConfidence Confidence,
    int Generation,
    int SourceGeneration,
    Vector3 AnchorWorld,
    FluidityContactStageEvidence? PreIK,
    FluidityContactStageEvidence? PostIK);

/// <summary>Both feet's evidence at one sample. Null means the runner could not supply authoritative evidence.</summary>
public sealed record FluidityContactFrame(
    FluidityFootContactEvidence? Left,
    FluidityFootContactEvidence? Right);

/// <summary>
/// A declared interval in which the named foot must remain in authoritative full support. It is test input, not
/// a production contact-lock policy or a role-specific correction cap.
/// </summary>
public sealed record FluidityExpectedSupportInterval(string Foot, float StartTimeSeconds, float EndTimeSeconds, int Generation);

/// <summary>
/// Declares whether authoritative support evidence is applicable to a recorded case and, when it is, its complete
/// required population. Inapplicability must be explicit rather than represented by an empty interval list.
/// </summary>
public sealed record FluidityExpectedSupportPopulation(
    FluiditySupportApplicability Applicability,
    IReadOnlyList<FluidityExpectedSupportInterval>? Intervals);

/// <summary>One rejected contact sample retained for deterministic failure reports.</summary>
public sealed record FluidityContactRejection(int SampleIndex, float TimeSeconds, string Foot, string Reason);

/// <summary>Validated support masks and every reason evidence could not be admitted to the oracle.</summary>
public sealed record FluidityContactValidation(
    bool[] LeftFullSupport,
    bool[] RightFullSupport,
    IReadOnlyList<FluidityContactRejection> Rejections)
{
    public bool IsValid => Rejections.Count == 0;
}

/// <summary>
/// Fail-closed test oracle for supplied contact evidence. It deliberately has no ankle-height, terrain, or
/// current-pose inference: absent evidence is not evidence of a planted foot.
/// </summary>
public static class FluidityContactOracle
{
    public static FluidityContactValidation Validate<TSample>(
        IReadOnlyList<TSample> samples,
        Func<TSample, float> getTime,
        Func<TSample, FluidityContactFrame?> getContacts,
        FluidityExpectedSupportPopulation? expectedSupport)
    {
        bool[] leftSupport = new bool[samples.Count];
        bool[] rightSupport = new bool[samples.Count];
        List<FluidityContactRejection> rejections = [];

        if (expectedSupport is null)
        {
            rejections.Add(new FluidityContactRejection(-1, 0f, "both", "expected support applicability was not declared"));
            return new FluidityContactValidation(leftSupport, rightSupport, rejections);
        }

        if (expectedSupport.Applicability == FluiditySupportApplicability.Inapplicable)
        {
            if (expectedSupport.Intervals is { Count: > 0 })
            {
                rejections.Add(new FluidityContactRejection(-1, 0f, "both", "inapplicable expected support population declared support intervals"));
            }

            return new FluidityContactValidation(leftSupport, rightSupport, rejections);
        }

        if (expectedSupport.Applicability != FluiditySupportApplicability.Required)
        {
            rejections.Add(new FluidityContactRejection(-1, 0f, "both", "expected support applicability was unknown"));
            return new FluidityContactValidation(leftSupport, rightSupport, rejections);
        }

        if (expectedSupport.Intervals is not { Count: > 0 } intervals)
        {
            rejections.Add(new FluidityContactRejection(-1, 0f, "both", "applicable expected support population was empty"));
            return new FluidityContactValidation(leftSupport, rightSupport, rejections);
        }

        foreach (FluidityExpectedSupportInterval interval in intervals)
        {
            if (!IsKnownFoot(interval.Foot)
                || !float.IsFinite(interval.StartTimeSeconds)
                || !float.IsFinite(interval.EndTimeSeconds)
                || interval.EndTimeSeconds < interval.StartTimeSeconds
                || interval.Generation < 0)
            {
                rejections.Add(new FluidityContactRejection(-1, interval.StartTimeSeconds, interval.Foot, "invalid expected support interval"));
                continue;
            }

            bool sawSample = false;
            for (int index = 0; index < samples.Count; index++)
            {
                float time = getTime(samples[index]);
                if (!float.IsFinite(time))
                {
                    rejections.Add(new FluidityContactRejection(index, time, interval.Foot, "non-finite sample time"));
                    continue;
                }

                if (time < interval.StartTimeSeconds || time > interval.EndTimeSeconds)
                {
                    continue;
                }

                sawSample = true;
                FluidityContactFrame? frame = getContacts(samples[index]);
                if (frame is null)
                {
                    rejections.Add(new FluidityContactRejection(index, time, interval.Foot, "missing authoritative contact frame"));
                    continue;
                }

                FluidityFootContactEvidence? evidence = interval.Foot == "left" ? frame?.Left : frame?.Right;
                if (!ValidateEvidence(evidence, interval, index, time, rejections))
                {
                    continue;
                }

                if (interval.Foot == "left")
                {
                    leftSupport[index] = true;
                }
                else
                {
                    rightSupport[index] = true;
                }
            }

            if (!sawSample)
            {
                rejections.Add(new FluidityContactRejection(-1, interval.StartTimeSeconds, interval.Foot, "expected support interval contained zero samples"));
            }
        }

        return new FluidityContactValidation(leftSupport, rightSupport, rejections);
    }

    private static bool ValidateEvidence(
        FluidityFootContactEvidence? evidence,
        FluidityExpectedSupportInterval interval,
        int sampleIndex,
        float time,
        List<FluidityContactRejection> rejections)
    {
        if (evidence is null)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, "missing authoritative contact evidence"));
            return false;
        }

        if (evidence.Foot != interval.Foot)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"wrong-foot label '{evidence.Foot}'"));
        }

        if (evidence.Confidence != FluidityContactConfidence.Authoritative)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"non-authoritative confidence '{evidence.Confidence}'"));
        }

        if (evidence.Generation != interval.Generation || evidence.SourceGeneration != evidence.Generation)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, "stale contact generation"));
        }

        bool anchorIsValid = IsFinite(evidence.AnchorWorld);
        if (!anchorIsValid)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, "non-finite anchor"));
        }

        bool preIKIsValid = ValidateStageEvidence(evidence.PreIK, "pre-IK", evidence.Generation, interval, sampleIndex, time, rejections);
        bool postIKIsValid = ValidateStageEvidence(evidence.PostIK, "post-IK", evidence.Generation, interval, sampleIndex, time, rejections);

        if (evidence.Phase != FluidityContactPhase.FullSupport)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, evidence.Phase == FluidityContactPhase.Release
                ? "release before required full support completed"
                : $"support was not full ('{evidence.Phase}')"));
        }

        FluidityContactStageEvidence? postIK = evidence.PostIK;
        bool postIKMatchesAnchor = postIK is { IsAvailable: true }
            && anchorIsValid
            && IsFinite(postIK.FootWorld)
            && postIK.FootWorld == evidence.AnchorWorld;
        if (!postIKMatchesAnchor && postIK is { IsAvailable: true } && anchorIsValid && IsFinite(postIK.FootWorld))
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, "translated planted foot diverged from anchor"));
        }

        return evidence.Foot == interval.Foot
            && evidence.Confidence == FluidityContactConfidence.Authoritative
            && evidence.Generation == interval.Generation
            && evidence.SourceGeneration == evidence.Generation
            && anchorIsValid
            && preIKIsValid
            && postIKIsValid
            && evidence.Phase == FluidityContactPhase.FullSupport;
    }

    private static bool ValidateStageEvidence(
        FluidityContactStageEvidence? stage,
        string stageName,
        int expectedGeneration,
        FluidityExpectedSupportInterval interval,
        int sampleIndex,
        float time,
        List<FluidityContactRejection> rejections)
    {
        if (stage is null)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"missing {stageName} contact evidence"));
            return false;
        }

        if (!stage.IsAvailable)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"unavailable {stageName} contact evidence"));
        }

        if (!stage.IsValid)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"invalid {stageName} contact evidence"));
        }

        if (stage.SourceGeneration != expectedGeneration)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"stale {stageName} contact evidence"));
        }

        bool positionIsFinite = IsFinite(stage.FootWorld);
        if (!positionIsFinite)
        {
            rejections.Add(new FluidityContactRejection(sampleIndex, time, interval.Foot, $"non-finite {stageName} foot position"));
        }

        return stage.IsAvailable
            && stage.IsValid
            && stage.SourceGeneration == expectedGeneration
            && positionIsFinite;
    }

    private static bool IsKnownFoot(string foot) => foot is "left" or "right";

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
