using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

[Flags]
public enum NPCTerminalCandidateRejection
{
    None = 0,
    UnsupportedBlend = 1 << 0,
    ContactMismatch = 1 << 1,
    VelocityMismatch = 1 << 2,
    YawUnreachable = 1 << 3,
    EndpointUnserviceable = 1 << 4,
    WaitBeyondBound = 1 << 5,
}

public readonly record struct NPCTerminalPublishedState(
    Vector2 BlendPoint,
    float PlaybackRate,
    Vector2 RootVelocity,
    float RootYawRate,
    float Phase,
    NPCAnimationPhaseLane SupportLane);

public readonly record struct NPCTerminalReleaseCandidate(
    int Index,
    NPCAnimationPhaseLane Lane,
    Vector2 BlendPoint,
    float TimeToRelease,
    float ForecastResidual,
    float YawResidual,
    bool SupportedBlend,
    bool ContactCompatible,
    bool VelocityCompatible,
    bool EndpointServiceable,
    bool WouldOvershootTerminal = false,
    float RouteRelativeOvershootMetres = 0f,
    float SustainedPlaybackRate = 1f);

public readonly record struct NPCTerminalSchedulerDecision(
    bool Commit,
    int CandidateIndex,
    NPCTerminalCandidateRejection Rejection);

/// <summary>
/// Single deterministic state owner for imminent terminal release selection and atomic commit. One check evaluates
/// at most the next same-lane and opposite-lane candidates; a candidate whose projected lead-in exceeds
/// <see cref="MaximumWaitSeconds" /> is not imminent yet and is rejected <c>WaitBeyondBound</c> until its lead-in
/// shrinks inside the bound. Rejected pairs repeat through the bounded imminent retry gate rather than an owned
/// wait window.
/// </summary>
public sealed class NPCTerminalReleaseScheduler
{
    public const float MaximumYawRate = 1.2f;
    public const float RootRotationTolerance = 0.035f;

    /// <summary>
    /// Upper bound on one imminent candidate's projected lead-in. An otherwise feasible occurrence becomes
    /// committable only once its time-to-release shrinks inside this window, mirroring the arm machinery's
    /// release-wait bound.
    /// </summary>
    public const float MaximumWaitSeconds = 1f;

    private readonly int[] _rejectionCounts = new int[6];
    private static readonly NPCTerminalCandidateRejection[] _reasons =
    [
        NPCTerminalCandidateRejection.UnsupportedBlend,
        NPCTerminalCandidateRejection.ContactMismatch,
        NPCTerminalCandidateRejection.VelocityMismatch,
        NPCTerminalCandidateRejection.YawUnreachable,
        NPCTerminalCandidateRejection.EndpointUnserviceable,
        NPCTerminalCandidateRejection.WaitBeyondBound,
    ];

    public NPCTerminalPublishedState PublishedState
    {
        get; private set;
    }
    public int SupportedPublicationCount
    {
        get; private set;
    }

    public void Publish(NPCTerminalPublishedState state, bool supported)
    {
        PublishedState = state;
        if (supported)
        {
            SupportedPublicationCount++;
        }
    }

    public NPCTerminalSchedulerDecision Evaluate(NPCTerminalReleaseCandidate first, NPCTerminalReleaseCandidate second)
    {
        NPCTerminalCandidateRejection firstRejection = RejectionFor(first);
        NPCTerminalCandidateRejection secondRejection = RejectionFor(second);
        bool firstValid = firstRejection == NPCTerminalCandidateRejection.None;
        bool secondValid = secondRejection == NPCTerminalCandidateRejection.None;
        if (!firstValid && !secondValid)
        {
            NPCTerminalCandidateRejection combined = firstRejection | secondRejection;
            Record(combined);
            return new(false, -1, combined);
        }

        NPCTerminalReleaseCandidate selected = Select(first, second, firstValid, secondValid);
        NPCTerminalCandidateRejection rejection = selected.Index == first.Index ? firstRejection : secondRejection;
        Record(rejection);
        return new(true, selected.Index, rejection);
    }

    public string RejectionSummary()
        => $"unsupported_blend={_rejectionCounts[0]};contact={_rejectionCounts[1]};velocity={_rejectionCounts[2]};"
            + $"yaw={_rejectionCounts[3]};endpoint={_rejectionCounts[4]};wait={_rejectionCounts[5]}";

    public static float MonotonicPhaseAdvance(float previous, float current)
        => float.IsFinite(previous) && float.IsFinite(current)
            ? Mathf.PosMod(current - previous, 1f)
            : 0f;

    public void Reset()
    {
        PublishedState = default;
        SupportedPublicationCount = 0;
        Array.Clear(_rejectionCounts);
    }

    private static NPCTerminalReleaseCandidate Select(
        NPCTerminalReleaseCandidate first,
        NPCTerminalReleaseCandidate second,
        bool firstValid,
        bool secondValid)
    {
        return firstValid != secondValid
            ? firstValid ? first : second
            : first.TimeToRelease != second.TimeToRelease
                ? first.TimeToRelease < second.TimeToRelease ? first : second
                // Production supplies the current-lane candidate first. Equal crossings therefore preserve the
                // NAV-001 current-lane tie before the stable authored event identity of that candidate.
                : first;
    }

    private static NPCTerminalCandidateRejection RejectionFor(NPCTerminalReleaseCandidate candidate)
        => NPCTerminalCandidateDecisionFactors.From(candidate).SchedulerRejection;

    private void Record(NPCTerminalCandidateRejection rejection)
    {
        for (int index = 0; index < _reasons.Length; index++)
        {
            if ((rejection & _reasons[index]) != 0)
            {
                _rejectionCounts[index]++;
            }
        }
    }
}
