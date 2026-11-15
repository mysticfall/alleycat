using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// The objective measurement set every raw contact event carries (INTR-005 TR3). The raw layer carries
/// measurements only — no classification logic lives below the raw/classified seam.
/// </summary>
/// <param name="ContactPoint">World-space contact point.</param>
/// <param name="ContactNormal">World-space unit receiver-surface outward normal at the contact point.</param>
/// <param name="RelativeVelocity">
/// Relative velocity of the counterpart with respect to the receiver, world space.
/// </param>
/// <param name="PenetrationDepth">Contact penetration depth in metres, never negative.</param>
/// <param name="PressRate">
/// The rate at which normal pressure builds in metres per second — the instantaneous normal closing speed of
/// the counterpart along the contact normal, zero when separating.
/// </param>
/// <param name="TangentialSlipSpeed">How fast the counterpart slides along the receiver's surface, in m/s.</param>
/// <param name="Duration">Contact duration accumulated since Begin; never decreases within a lifecycle.</param>
/// <param name="ContactBreadth">Spatial extent of the contact in metres, aggregated over contributing shapes.</param>
public readonly record struct RawContactSample(
    Vector3 ContactPoint,
    Vector3 ContactNormal,
    Vector3 RelativeVelocity,
    float PenetrationDepth,
    float PressRate,
    float TangentialSlipSpeed,
    TimeSpan Duration,
    float ContactBreadth);
