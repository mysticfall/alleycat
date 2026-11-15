using System.Numerics;
using AlleyCat.Contact;

namespace AlleyCat.Tests.Contact;

/// <summary>
/// Builds internally consistent scripted contact measurements for the deterministic raw stream and
/// classification scenarios (INTR-005 TR19): the receiver surface faces +Z (outward normal
/// <see cref="OutwardNormal" />), the counterpart approaches along −Z and slides along +X, so a sample's press
/// rate equals its scripted normal closing speed and its slip speed equals its scripted tangential speed.
/// Timelines use 0.0625 s steps, which are binary-exact in single precision, so window comparisons stay
/// deterministic.
/// </summary>
internal static class ContactScenarioScript
{
    public static readonly ContactParty SourceHand = new("player/right_hand", "Right Hand", "Mochi");

    public static readonly ContactParty ReceiverHead = new("npc/head", "Head", "Sky");

    public static readonly Vector3 OutwardNormal = Vector3.UnitZ;

    /// <summary>One scripted physics-tick step; binary-exact so accumulated windows compare deterministically.</summary>
    public const float TickSeconds = 0.0625f;

    public static Vector3 Velocity(float normalSpeed, float tangentialSpeed) =>
        (tangentialSpeed * Vector3.UnitX) - (normalSpeed * OutwardNormal);

    public static RawContactFrame Frame(
        float normalSpeed = 0f,
        float tangentialSpeed = 0f,
        float penetrationDepth = 0.001f,
        float contactBreadth = 0.02f,
        Vector3? receiverVelocity = null) =>
        new(
            ContactPoint: new Vector3(0.2f, 1.5f, 0.3f),
            ContactNormal: OutwardNormal,
            CounterpartVelocity: Velocity(normalSpeed, tangentialSpeed) + (receiverVelocity ?? Vector3.Zero),
            ReceiverVelocity: receiverVelocity ?? Vector3.Zero,
            PenetrationDepth: penetrationDepth,
            ContactBreadth: contactBreadth);

    public static RawContactSample Sample(
        float durationSeconds,
        float normalSpeed = 0f,
        float tangentialSpeed = 0f,
        float penetrationDepth = 0.001f,
        float contactBreadth = 0.02f) =>
        new(
            ContactPoint: new Vector3(0.2f, 1.5f, 0.3f),
            ContactNormal: OutwardNormal,
            RelativeVelocity: Velocity(normalSpeed, tangentialSpeed),
            PenetrationDepth: penetrationDepth,
            PressRate: normalSpeed,
            TangentialSlipSpeed: tangentialSpeed,
            Duration: TimeSpan.FromSeconds(durationSeconds),
            ContactBreadth: contactBreadth);

    public static RawContactEvent Raw(RawContactPhase phase, ContactId contact, RawContactSample sample) =>
        new(phase, contact, SourceHand, ReceiverHead, sample);
}
