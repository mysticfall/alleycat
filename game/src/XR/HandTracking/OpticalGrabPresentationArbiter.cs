using AlleyCat.Interaction;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>One hand's optical-grab presentation state (XR-002 TR30-TR31; INTR-003 TR19).</summary>
public enum OpticalGrabPresentationState : byte
{
    /// <summary>Live optical tracking owns this hand's presentation.</summary>
    Tracking = 0,

    /// <summary>The hand is optically approaching a candidate's authored reference pose.</summary>
    PendingAssistance = 1,

    /// <summary>The authored AnimationTree grab pose owns this committed hand.</summary>
    Held = 2,
}

/// <summary>
/// Immutable owner token for one optical-grab presentation publication. The token contains only a Godot instance ID,
/// never a <see cref="GodotObject" /> reference, so the global arbiter cannot retain a torn-down hand node.
/// </summary>
/// <param name="Side">The side owned by this publication.</param>
/// <param name="PublisherInstanceId">The publishing <c>HandPoseBehaviour</c>'s Godot instance ID.</param>
/// <param name="Generation">The monotonic grab-attempt generation allocated by the publisher.</param>
public readonly record struct OpticalGrabPresentationOwner(
    LimbSide Side,
    ulong PublisherInstanceId,
    long Generation);

/// <summary>
/// One hand's optical-grab presentation arbitration state (XR-002 TR30-TR32; INTR-003 TR19).
/// </summary>
/// <param name="State">The current per-hand presentation owner.</param>
/// <param name="GrabReference">
/// The validated instance-exact candidate reference; <see langword="null" /> while live tracking owns the hand.
/// </param>
/// <param name="Owner">The current publisher; <see langword="null" /> while live tracking owns the hand.</param>
public sealed record OpticalGrabHandPresentation(
    OpticalGrabPresentationState State,
    GrabPoseReference? GrabReference,
    OpticalGrabPresentationOwner? Owner = null)
{
    /// <summary>The exact authored AnimationTree pose instance, retained for presentation diagnostics.</summary>
    public Animation? GrabPoseAnimation => GrabReference?.Animation;
    /// <summary>Whether the committed authored pose owns this hand.</summary>
    public bool IsOpticalGrabHeld => State == OpticalGrabPresentationState.Held;

    /// <summary>Whether this hand is visibly assisting an optical pending grab.</summary>
    public bool IsOpticalGrabPendingAssistance => State == OpticalGrabPresentationState.PendingAssistance;
}

/// <summary>
/// Per-hand finger-pose arbitration seam (XR-002 TR30-TR31; INTR-003 TR19): the single authoritative answer to
/// whether each hand's finger presentation must defer to a committed optical grab, and which authored reference
/// animation owns that hand while held. <c>HandPoseBehaviour</c> is the
/// single authoritative writer — it owns the exact commit, release, and abandonment transitions — while the
/// finger retargeting modifier queries this state read-only before writing each hand (phase 2b consumption).
/// Dependency direction stays <c>Control → {XR, Interaction}</c> and <c>Interaction → XR</c>; the modifier never
/// references interaction or control types.
/// </summary>
public interface IOpticalGrabPresentationArbiter
{
    /// <summary>Reads one hand's presentation state; defaults to live tracking.</summary>
    /// <param name="side">Hand side to query.</param>
    OpticalGrabHandPresentation GetPresentation(LimbSide side);

    /// <summary>
    /// Publishes an optical-originated pending grab and its candidate authored reference animation. This replaces a
    /// previous publication for the same side, making <paramref name="owner" /> the current owner.
    /// </summary>
    void SetOpticalGrabPendingAssistance(OpticalGrabPresentationOwner owner, GrabPoseReference? grabReference);

    /// <summary>
    /// Publishes that the owner side holds an optical-originated committed grab with the authored
    /// reference <paramref name="grabReference" />. The promotion is ignored when another publisher
    /// has already replaced <paramref name="owner" />.
    /// </summary>
    bool TrySetOpticalGrabHeld(OpticalGrabPresentationOwner owner, GrabPoseReference? grabReference);

    /// <summary>
    /// Returns the owner's side to live optical tracking only when it still owns the current publication.
    /// </summary>
    bool TryClearOpticalGrab(OpticalGrabPresentationOwner owner);
}

/// <summary>
/// Default in-process <see cref="IOpticalGrabPresentationArbiter" />: two per-side state records, written on
/// the main/physics thread and read by the finger modifier in the same frame's process pass. No locking is
/// required because both the writer and reader run on the Godot main thread.
/// </summary>
public sealed class OpticalGrabPresentationArbiter : IOpticalGrabPresentationArbiter
{
    private readonly OpticalGrabHandPresentation[] _presentations =
    [
        new(OpticalGrabPresentationState.Tracking, GrabReference: null),
        new(OpticalGrabPresentationState.Tracking, GrabReference: null),
    ];

    /// <inheritdoc />
    public OpticalGrabHandPresentation GetPresentation(LimbSide side)
        => _presentations[(int)side];

    /// <inheritdoc />
    public void SetOpticalGrabPendingAssistance(OpticalGrabPresentationOwner owner, GrabPoseReference? grabReference)
        => _presentations[(int)owner.Side] = new(OpticalGrabPresentationState.PendingAssistance, grabReference, owner);

    /// <inheritdoc />
    public bool TrySetOpticalGrabHeld(OpticalGrabPresentationOwner owner, GrabPoseReference? grabReference)
    {
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        _presentations[(int)owner.Side] = new(OpticalGrabPresentationState.Held, grabReference, owner);
        return true;
    }

    /// <inheritdoc />
    public bool TryClearOpticalGrab(OpticalGrabPresentationOwner owner)
    {
        if (!IsCurrentOwner(owner))
        {
            return false;
        }

        _presentations[(int)owner.Side] = new(OpticalGrabPresentationState.Tracking, GrabReference: null);
        return true;
    }

    private bool IsCurrentOwner(OpticalGrabPresentationOwner owner)
        => _presentations[(int)owner.Side].Owner == owner;
}
