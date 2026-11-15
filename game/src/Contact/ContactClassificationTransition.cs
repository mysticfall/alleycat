using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// One classification transition emitted by the classification engine (INTR-005 TR6): classified events fire
/// on classification transitions only, never per physics tick, and steady contact emits nothing after its
/// entry transition. The monitor combines a transition with region resolution, the bone-local direction
/// transform, and any authored description to build the delivered <see cref="ContactEvent" />.
/// </summary>
/// <param name="Contact">Stable identity of the classified contact pair.</param>
/// <param name="Source">The contacting counterpart party — whose hand, foot, or object caused the contact.</param>
/// <param name="Receiver">The receiving body-part or surface party.</param>
/// <param name="PreviousClass">
/// The classification the contact held before this transition; <see langword="null" /> on the entry transition
/// at contact Begin.
/// </param>
/// <param name="Class">The newly confirmed class.</param>
/// <param name="Intensity">
/// Class-appropriate intensity for <paramref name="Class" />: approach normal speed at Begin for Impact/Hit
/// and Touch, penetration depth for Press and Rest/Lean, tangential slip speed for Caress/Stroke.
/// </param>
/// <param name="ContactNormal">
/// World-space unit receiver-surface outward normal at the emission sample; the monitor re-expresses it in the
/// bone-local space of the contacted body part for the delivered event.
/// </param>
/// <param name="Duration">Contact duration accumulated at the emission sample.</param>
public sealed record ContactClassificationTransition(
    ContactId Contact,
    ContactParty Source,
    ContactParty Receiver,
    ContactClass? PreviousClass,
    ContactClass Class,
    float Intensity,
    Vector3 ContactNormal,
    TimeSpan Duration);
