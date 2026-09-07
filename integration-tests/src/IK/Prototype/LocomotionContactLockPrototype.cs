using System.Collections.Immutable;
using Godot;

namespace AlleyCat.IntegrationTests.IK.Prototype;

/// <summary>
/// Disposable Stage 5 contact-lock model. It deliberately accepts only fixture-authored evidence;
/// it has no production metadata, solver, locomotion, terrain, or pelvis dependency.
/// </summary>
internal sealed class LocomotionContactLockPrototype
{
    private readonly Dictionary<PrototypeFoot, Transform3D> _anchors = [];
    private long _version;

    public ContactLockSnapshot Resolve(
        SyntheticContactEvidence evidence,
        SyntheticCorrectionRequest request)
    {
        if (evidence.State is SyntheticEvidenceState.Missing or SyntheticEvidenceState.Empty)
        {
            return Publish(
                request,
                ContactLockDisposition.DeferredRecoveryRequired,
                request.Correction,
                evidence.State == SyntheticEvidenceState.Missing ? "MissingContactEvidence" : "EmptyContactEvidence");
        }

        foreach (SyntheticContactRecord contact in evidence.Contacts)
        {
            if (contact.IsStale)
            {
                return Publish(request, ContactLockDisposition.DeferredRecoveryRequired, request.Correction, "StaleEvidence");
            }

            if (!contact.IsAnchorValid || !contact.WorldAnchor.Origin.IsFinite())
            {
                return Publish(request, ContactLockDisposition.Rejected, request.Correction, "InvalidAnchor");
            }

            switch (contact.Lifecycle)
            {
                case SyntheticContactLifecycle.Acquire when _anchors.ContainsKey(contact.Foot):
                    return Publish(request, ContactLockDisposition.DeferredRecoveryRequired, request.Correction, "ActiveContactReplacement");
                case SyntheticContactLifecycle.Acquire:
                    _anchors.Add(contact.Foot, contact.WorldAnchor);
                    break;
                case SyntheticContactLifecycle.FullSupport when !_anchors.ContainsKey(contact.Foot):
                    return Publish(request, ContactLockDisposition.Rejected, request.Correction, "FullSupportWithoutAcquire");
                case SyntheticContactLifecycle.FullSupport:
                    break;
                case SyntheticContactLifecycle.Release:
                    if (!_anchors.Remove(contact.Foot))
                    {
                        return Publish(request, ContactLockDisposition.Rejected, request.Correction, "ReleaseWithoutAcquire");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contact), contact.Lifecycle, "Unknown synthetic lifecycle.");
            }
        }

        return _anchors.Count > 1 && !request.Correction.IsZero
            ? Publish(request, ContactLockDisposition.Rejected, request.Correction, "DoubleSupportDefersSyntheticCorrection")
            : request.Feasibility switch
            {
                FixtureFeasibility.Admitted => Publish(request, ContactLockDisposition.Applied, SyntheticCorrection.Zero, null, request.Correction),
                FixtureFeasibility.Reduced => Publish(
                    request,
                    ContactLockDisposition.Reduced,
                    request.Correction - request.FixtureAdmittedCorrection,
                    "FixtureReduced",
                    request.FixtureAdmittedCorrection),
                FixtureFeasibility.Unproven => Publish(request, ContactLockDisposition.DeferredRecoveryRequired, request.Correction, "FeasibilityUnproven"),
                FixtureFeasibility.Rejected => Publish(request, ContactLockDisposition.Rejected, request.Correction, "FixtureRejected"),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Feasibility, "Unknown fixture feasibility."),
            };
    }

    private ContactLockSnapshot Publish(
        SyntheticCorrectionRequest request,
        ContactLockDisposition disposition,
        SyntheticCorrection residual,
        string? reason,
        SyntheticCorrection? output = null)
        => new(
            ++_version,
            _anchors.ToImmutableDictionary(),
            _anchors.Count switch
            {
                0 => AnchorLifecycleState.Released,
                1 => AnchorLifecycleState.SingleSupport,
                _ => AnchorLifecycleState.DoubleSupport,
            },
            disposition,
            output ?? SyntheticCorrection.Zero,
            residual,
            reason,
            request.Name);
}

internal enum PrototypeFoot
{
    Left,
    Right,
}

internal enum SyntheticContactLifecycle
{
    Acquire,
    FullSupport,
    Release,
}

/// <summary>
/// Explicit fixture evidence envelope. A normal tick without a contact transition is not the same thing as a missing
/// or empty contact payload: only <see cref="NoEvent"/> permits an existing anchor to remain authoritative.
/// </summary>
internal readonly record struct SyntheticContactEvidence(
    SyntheticEvidenceState State,
    IReadOnlyList<SyntheticContactRecord> Contacts)
{
    private static readonly IReadOnlyList<SyntheticContactRecord> _noContacts = [];

    public static SyntheticContactEvidence NoEvent() => new(SyntheticEvidenceState.NoEvent, _noContacts);

    public static SyntheticContactEvidence Present(params SyntheticContactRecord[] contacts)
        => contacts.Length == 0
            ? throw new ArgumentException("Present evidence requires at least one lifecycle/contact record.", nameof(contacts))
            : new(SyntheticEvidenceState.Present, contacts);

    public static SyntheticContactEvidence Missing() => new(SyntheticEvidenceState.Missing, _noContacts);

    public static SyntheticContactEvidence Empty() => new(SyntheticEvidenceState.Empty, _noContacts);
}

internal enum SyntheticEvidenceState
{
    /// <summary>A valid tick with no new transition; retained anchors remain usable.</summary>
    NoEvent,

    /// <summary>A valid, non-empty lifecycle/contact payload.</summary>
    Present,

    /// <summary>The expected lifecycle/contact payload was unavailable.</summary>
    Missing,

    /// <summary>The expected lifecycle/contact payload was supplied but contained no records.</summary>
    Empty,
}

internal enum FixtureFeasibility
{
    Admitted,
    Reduced,
    Rejected,
    Unproven,
}

internal enum ContactLockDisposition
{
    Applied,
    Reduced,
    Rejected,
    DeferredRecoveryRequired,
}

internal enum AnchorLifecycleState
{
    Released,
    SingleSupport,
    DoubleSupport,
}

internal readonly record struct SyntheticContactRecord(
    PrototypeFoot Foot,
    Transform3D WorldAnchor,
    float Confidence,
    SyntheticContactLifecycle Lifecycle,
    bool IsStale,
    bool IsAnchorValid,
    string Provenance);

internal readonly record struct SyntheticCorrection(Vector3 Translation, float YawRadians)
{
    public static SyntheticCorrection Zero => new(Vector3.Zero, 0f);

    public bool IsZero => Translation.IsZeroApprox() && Mathf.IsZeroApprox(YawRadians);

    public static SyntheticCorrection operator -(SyntheticCorrection left, SyntheticCorrection right)
        => new(left.Translation - right.Translation, left.YawRadians - right.YawRadians);
}

internal readonly record struct SyntheticCorrectionRequest(
    string Name,
    SyntheticCorrection Correction,
    FixtureFeasibility Feasibility,
    SyntheticCorrection FixtureAdmittedCorrection);

internal sealed record ContactLockSnapshot(
    long Version,
    ImmutableDictionary<PrototypeFoot, Transform3D> Anchors,
    AnchorLifecycleState AnchorLifecycleState,
    ContactLockDisposition Disposition,
    SyntheticCorrection SyntheticOutput,
    SyntheticCorrection Residual,
    string? InvalidOrStaleReason,
    string RequestName);

/// <summary>
/// Disposable ordered target hand-off. A downstream candidate may be syntactically plausible, but cannot be published
/// when the authoritative pre-IK lock snapshot is invalid or stale.
/// </summary>
internal static class PrototypeLowerLegTargetReconciliation
{
    public static PrototypeFinalOutput Reconcile(ContactLockSnapshot snapshot, PrototypeFoot foot, Transform3D downstreamCandidate)
        => snapshot.InvalidOrStaleReason is not null
            ? new(false, Transform3D.Identity, "PreIKBlocked:" + snapshot.InvalidOrStaleReason)
            : snapshot.Anchors.TryGetValue(foot, out Transform3D anchor)
                ? new(true, anchor, null)
                : new(false, downstreamCandidate, "NoActiveAnchor");
}

internal readonly record struct PrototypeFinalOutput(bool IsPublished, Transform3D Target, string? BlockingReason);

/// <summary>
/// Fixture-only post-IK gate: publication is accepted only when it is coherent with an active authoritative anchor.
/// </summary>
internal static class PrototypePostIKValidation
{
    public static bool IsCoherent(ContactLockSnapshot snapshot, PrototypeFoot foot, PrototypeFinalOutput output)
        => output.IsPublished
            && snapshot.InvalidOrStaleReason is null
            && snapshot.Anchors.TryGetValue(foot, out Transform3D anchor)
            && anchor == output.Target;
}
