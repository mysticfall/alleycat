using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// One physics tick's objective observation of a contact pair, measured by a sweeper's query shapes and the
/// receiver-side velocity sampler (INTR-005 TR3, TR18b, TR18f). All vectors are world space; the raw stream
/// derives the carried measurement set from successive frames. Frames are input to the raw layer only — no
/// classification logic reads them.
/// </summary>
/// <param name="ContactPoint">World-space contact point of the coalesced contact.</param>
/// <param name="ContactNormal">
/// World-space receiver-surface outward normal at the contact point — pointing away from the receiver's body
/// into the counterpart. Non-zero; the stream normalises it.
/// </param>
/// <param name="CounterpartVelocity">World-space velocity of the contacting counterpart at the contact.</param>
/// <param name="ReceiverVelocity">World-space velocity of the receiver at the contact point.</param>
/// <param name="PenetrationDepth">Contact penetration depth in metres; negative solver jitter clamps to zero.</param>
/// <param name="ContactBreadth">Spatial extent of the contact in metres, aggregated over contributing shapes.</param>
public readonly record struct RawContactFrame(
    Vector3 ContactPoint,
    Vector3 ContactNormal,
    Vector3 CounterpartVelocity,
    Vector3 ReceiverVelocity,
    float PenetrationDepth,
    float ContactBreadth);
