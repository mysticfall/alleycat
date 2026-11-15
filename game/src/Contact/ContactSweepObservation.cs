using NumericsVector3 = System.Numerics.Vector3;

namespace AlleyCat.Contact;

/// <summary>
/// One sweeper's coalesced observation of a receiver body for one physics tick (INTR-005 TR2, TR3, TR18b): the
/// query shapes' merged contact geometry plus the counterpart's measured velocity, before the monitor contributes
/// the receiver-side velocity sample (TR18f). All vectors are world space.
/// </summary>
/// <param name="ContactPoint">World-space contact point of the coalesced contact.</param>
/// <param name="ContactNormal">World-space receiver-surface outward normal at the contact point.</param>
/// <param name="CounterpartVelocity">World-space velocity of the contacting counterpart at the contact.</param>
/// <param name="PenetrationDepth">Contact penetration depth in metres, measured by the sweeper's query shapes.</param>
/// <param name="ContactBreadth">Spatial extent of the contact in metres, aggregated over contributing shapes.</param>
public readonly record struct ContactSweepObservation(
    NumericsVector3 ContactPoint,
    NumericsVector3 ContactNormal,
    NumericsVector3 CounterpartVelocity,
    float PenetrationDepth,
    float ContactBreadth);
