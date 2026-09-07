using Godot;

#pragma warning disable CS1591, IDE0072

namespace AlleyCat.Tests.IK;

/// <summary>
/// Shared fixture-only Gate B evidence data. Both the runtime fixture and its photobooth consume this model so a
/// coloured marker always represents the same injected records and pre-IK decision that the test exercises.
/// </summary>
public enum ContactLockGateBInjectedEvidence
{
    Admitted,
    Missing,
    Stale,
    Invalid,
    Contradictory,
    FallbackMasking,
}

/// <summary>Distinct geometric identities for refused contact evidence in the Gate B photobooth.</summary>
public enum ContactLockGateBMarkerShape
{
    None,
    HollowRing,
    TallPrism,
    WarningCone,
    SplitPair,
    MaskingStack,
}

public enum ContactLockGateBPreIKDecision
{
    Accepted,
    Rejected,
}

public enum ContactLockGateBSelectedProvider
{
    ContactEvidence,
    OrdinaryFallback,
}

public enum ContactLockGateBPostIKDecision
{
    Accepted,
    PreIKRejected,
}

public enum ContactLockGateBBlockedOutcome
{
    None,
    ContactRefusedDespiteFallbackSolve,
}

public readonly record struct ContactLockGateBContact(
    string Role,
    string Foot,
    long Generation,
    Transform3D Anchor,
    bool IsStale,
    bool IsValid);

/// <summary>
/// Test-only contact-evidence attempt. It is intentionally data-driven: the audit class cannot itself authorise an
/// override, because <see cref="TryResolveCurrent"/> only accepts current, finite, valid, coherent contacts.
/// </summary>
public readonly record struct ContactLockGateBEvidence(
    string Role,
    string Foot,
    long Generation,
    ContactLockGateBInjectedEvidence InjectedEvidence,
    Transform3D? ExpectedAnchor,
    Transform3D OrdinaryFallbackTarget,
    IReadOnlyList<ContactLockGateBContact> Contacts)
{
    public ContactLockGateBPreIKDecision PreIKDecision
        => TryResolveCurrent(Role, Foot, out _)
            ? ContactLockGateBPreIKDecision.Accepted
            : ContactLockGateBPreIKDecision.Rejected;

    public ContactLockGateBSelectedProvider SelectedProvider
        => PreIKDecision == ContactLockGateBPreIKDecision.Accepted
            ? ContactLockGateBSelectedProvider.ContactEvidence
            : ContactLockGateBSelectedProvider.OrdinaryFallback;

    public ContactLockGateBPostIKDecision PostIKDecision
        => PreIKDecision == ContactLockGateBPreIKDecision.Accepted
            ? ContactLockGateBPostIKDecision.Accepted
            : ContactLockGateBPostIKDecision.PreIKRejected;

    public ContactLockGateBBlockedOutcome BlockedOutcome
        => PreIKDecision == ContactLockGateBPreIKDecision.Accepted
            ? ContactLockGateBBlockedOutcome.None
            : ContactLockGateBBlockedOutcome.ContactRefusedDespiteFallbackSolve;

    public ContactLockGateBMarkerShape MarkerShape => InjectedEvidence switch
    {
        ContactLockGateBInjectedEvidence.Missing => ContactLockGateBMarkerShape.HollowRing,
        ContactLockGateBInjectedEvidence.Stale => ContactLockGateBMarkerShape.TallPrism,
        ContactLockGateBInjectedEvidence.Invalid => ContactLockGateBMarkerShape.WarningCone,
        ContactLockGateBInjectedEvidence.Contradictory => ContactLockGateBMarkerShape.SplitPair,
        ContactLockGateBInjectedEvidence.FallbackMasking => ContactLockGateBMarkerShape.MaskingStack,
        ContactLockGateBInjectedEvidence.Admitted => ContactLockGateBMarkerShape.None,
        _ => throw new ArgumentOutOfRangeException(nameof(InjectedEvidence), InjectedEvidence, null),
    };

    public string MarkerColourName => InjectedEvidence switch
    {
        ContactLockGateBInjectedEvidence.Missing => "Red",
        ContactLockGateBInjectedEvidence.Stale => "Violet",
        ContactLockGateBInjectedEvidence.Invalid => "Amber",
        ContactLockGateBInjectedEvidence.Contradictory => "Magenta",
        ContactLockGateBInjectedEvidence.FallbackMasking => "OrangeAndBlack",
        ContactLockGateBInjectedEvidence.Admitted => "None",
        _ => throw new ArgumentOutOfRangeException(nameof(InjectedEvidence), InjectedEvidence, null),
    };

    public static ContactLockGateBEvidence Create(
        string role,
        string foot,
        long generation,
        ContactLockGateBInjectedEvidence injectedEvidence,
        Transform3D anchor,
        Transform3D ordinaryFallbackTarget)
    {
        ContactLockGateBContact admitted = new(role, foot, generation, anchor, false, true);
        IReadOnlyList<ContactLockGateBContact> contacts = injectedEvidence switch
        {
            ContactLockGateBInjectedEvidence.Missing => [],
            ContactLockGateBInjectedEvidence.Stale => [admitted with { Generation = generation - 1, IsStale = true }],
            ContactLockGateBInjectedEvidence.Invalid => [admitted with { IsValid = false }],
            ContactLockGateBInjectedEvidence.Contradictory =>
            [
                admitted,
                admitted with { Anchor = new Transform3D(anchor.Basis, anchor.Origin + new Vector3(0.055f, 0.0f, 0.0f)) },
            ],
            ContactLockGateBInjectedEvidence.FallbackMasking => [admitted with { IsValid = false }],
            ContactLockGateBInjectedEvidence.Admitted => [admitted],
            _ => throw new ArgumentOutOfRangeException(nameof(injectedEvidence), injectedEvidence, null),
        };

        return new ContactLockGateBEvidence(
            role,
            foot,
            generation,
            injectedEvidence,
            injectedEvidence == ContactLockGateBInjectedEvidence.Missing ? null : anchor,
            ordinaryFallbackTarget,
            contacts);
    }

    public bool TryResolveCurrent(string role, string foot, out Transform3D anchor)
    {
        anchor = Transform3D.Identity;
        if (Role != role || Foot != foot || Generation <= 0 || Contacts.Count == 0)
        {
            return false;
        }

        Transform3D? resolvedAnchor = null;
        foreach (ContactLockGateBContact contact in Contacts)
        {
            if (contact.Role != role
                || contact.Foot != foot
                || contact.Generation != Generation
                || contact.IsStale
                || !contact.IsValid
                || !IsFinite(contact.Anchor))
            {
                return false;
            }

            if (resolvedAnchor is Transform3D existingAnchor
                && existingAnchor.Origin.DistanceTo(contact.Anchor.Origin) > 0.0001f)
            {
                return false;
            }

            resolvedAnchor ??= contact.Anchor;
        }

        if (resolvedAnchor is not Transform3D finiteAnchor)
        {
            return false;
        }

        anchor = finiteAnchor;
        return true;
    }

    private static bool IsFinite(Transform3D transform)
        => IsFinite(transform.Origin)
            && IsFinite(transform.Basis.Column0)
            && IsFinite(transform.Basis.Column1)
            && IsFinite(transform.Basis.Column2);

    private static bool IsFinite(Vector3 vector)
        => float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}

#pragma warning restore CS1591, IDE0072
