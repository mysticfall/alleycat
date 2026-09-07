using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.Interaction.Hands;

/// <summary>
/// Minimal observable lifecycle of a hand's active grab (INTR-002 R58-59; INTR-003 TR20).
/// </summary>
public enum HandGrabLifecycleState
{
    /// <summary>No grab is pending or held.</summary>
    None = 0,

    /// <summary>A grab intent is approaching: the IK target moves toward the grab point; commit is deferred.</summary>
    Pending = 1,

    /// <summary>The grab committed: the object is held and the authored grab pose owns the hand.</summary>
    Held = 2,
}

/// <summary>
/// The input source that originated a hand's active grab (INTR-002 R58; CTRL-002 TR17).
/// </summary>
public enum HandGrabInputSource
{
    /// <summary>No grab is active, or the originating source is unknown to the provenance seam.</summary>
    None = 0,

    /// <summary>The grab originated from an XR controller grab edge in <c>Controller</c> mode.</summary>
    Controller = 1,

    /// <summary>The grab originated from recognised optical hand closure in <c>Optical</c> mode.</summary>
    Optical = 2,
}

/// <summary>
/// Why the most recent pending grab was abandoned (IK-005 TR22). Distinct from a commit-time freshness
/// rejection, which never began an abandonment path.
/// </summary>
public enum HandGrabAbandonmentReason
{
    /// <summary>No pending grab has been abandoned since the last grab attempt began.</summary>
    None = 0,

    /// <summary>
    /// The pending grab lost its selected candidate — reach, occupancy, content validity, or identity — during
    /// same-source refresh.
    /// </summary>
    CandidateLoss = 1,

    /// <summary>
    /// The commanded approach could not converge within its bounded interval: the direct-attachment residual
    /// stopped shrinking while remaining outside the commit gate, indicating an unreachable or collision-limited
    /// destination (INTR-002 recovery boundaries; IK-005 TR22).
    /// </summary>
    NonConvergence = 2,

    /// <summary>
    /// The pending candidate's physics body moved persistently — its measured linear speed stayed above the
    /// moving-candidate threshold for the bounded sustained-motion interval — so the commanded approach chased
    /// a live destination rather than converging onto a settled one.
    /// </summary>
    MovingCandidate = 3,
}

/// <summary>
/// Side-correct observation of the current best eligible grab candidate: the deterministic discovery and
/// selection result <see cref="HandPoseBehaviour.Grab()" /> would act on, without initiating anything
/// (INTR-003 TR20; CTRL-002 TR9-TR11).
/// </summary>
/// <param name="Grabbable">The grabbable that produced the best candidate.</param>
/// <param name="GrabPointSource">The grab-point component that produced the candidate — its identity, with
/// <paramref name="Grabbable" />, is the recognition candidate identity (CTRL-002 TR11).</param>
/// <param name="Animation">The candidate's mandatory grab-pose animation — the gesture-reference source
/// (XR-002 TR48, TR52).</param>
/// <param name="Reference">The validated instance-exact reference shared by recognition and presentation.</param>
/// <param name="GripRecognitionStrategyName">The candidate-selected strategy identifier.</param>
public sealed record GrabCandidateObservation(
    IGrabbable Grabbable,
    IGrabPoint GrabPointSource,
    Animation Animation,
    GrabPoseReference Reference,
    string GripRecognitionStrategyName);

/// <summary>
/// Read-only measurement of the current best eligible grab candidate. Unlike <see cref="GrabCandidateObservation" />,
/// this includes the acquisition and direct-attachment transforms used by selection and the Movable commit gate.
/// </summary>
/// <param name="Grabbable">The selected grabbable identity.</param>
/// <param name="GrabPointSource">The selected grab-point identity.</param>
/// <param name="Animation">The candidate's authored grip-reference animation.</param>
/// <param name="AcquisitionDistance">The selection metric from the querying hand to the accepted point.</param>
/// <param name="GrabPointTransform">The selected grab point in world space.</param>
/// <param name="ExpectedAttachmentTransform">The direct BoneAttachment3D transform required for a Movable commit.</param>
public sealed record GrabCandidateMeasurement(
    IGrabbable Grabbable,
    IGrabPoint GrabPointSource,
    Animation Animation,
    float AcquisitionDistance,
    Transform3D GrabPointTransform,
    Transform3D ExpectedAttachmentTransform);

/// <summary>
/// Narrow per-hand grab-lifecycle seam for the CTRL-002 grab input coordinator (INTR-003 TR20): current-best
/// candidate observation, pending/held input provenance, provenance-aware grab initiation, and deterministic
/// pending-grab cancellation. Implemented by <see cref="HandPoseBehaviour" /> and resolved through the hands
/// holder; <see cref="IHand" />'s public surface stays <c>Side</c>, <c>CurrentGrabbed</c>, <c>Grab()</c>, and
/// <c>Release()</c> — no pose-control APIs are exposed here.
/// </summary>
public interface IHandGrabLifecycle
{
    /// <summary>Hand side this lifecycle belongs to.</summary>
    LimbSide Side
    {
        get;
    }

    /// <summary>Minimal lifecycle state of the active grab: none, pending, or held.</summary>
    HandGrabLifecycleState GrabLifecycle
    {
        get;
    }

    /// <summary>
    /// Input source that originated the active pending/held grab; <see cref="HandGrabInputSource.None" /> while
    /// no grab is active (INTR-002 R58; CTRL-002 TR17).
    /// </summary>
    HandGrabInputSource GrabInputSource
    {
        get;
    }

    /// <summary>
    /// The pending or held grab's candidate animation — the fixed recognition reference while a grab intent is
    /// active (INTR-002 R59-60); <see langword="null" /> while idle.
    /// </summary>
    Animation? ActiveGrabAnimation
    {
        get;
    }

    /// <summary>
    /// The active candidate's validated instance-exact descriptor. Recognition, pending assistance, held playback,
    /// and diagnostics must use this rather than re-resolving an animation by name or path.
    /// </summary>
    GrabPoseReference? ActiveGrabReference
    {
        get;
    }

    /// <summary>The active candidate's strategy selection, fixed for the pending/held lifecycle.</summary>
    string? ActiveGrabRecognitionStrategyName
    {
        get;
    }

    /// <summary>
    /// Why the most recent pending grab was abandoned, or <see cref="HandGrabAbandonmentReason.None" /> while no
    /// abandonment has occurred since the grab attempt began. Lets input layers distinguish bounded
    /// non-convergence abandonment from candidate loss without observing solver internals (IK-005 TR22).
    /// </summary>
    HandGrabAbandonmentReason LastPendingGrabAbandonmentReason
    {
        get;
    }

    /// <summary>
    /// Initiates a grab, recording <paramref name="inputSource" /> as the originating provenance when the grab
    /// begins (INTR-002 R58). Routes through the exact <see cref="IHand.Grab()" /> path: the approach phase may
    /// begin and commit stays deferred until IK settles.
    /// </summary>
    /// <param name="inputSource">Input source that originates this grab intent.</param>
    /// <returns>The held object, or <see langword="null" /> when no valid grabbable is in range.</returns>
    IGrabbable? BeginGrab(HandGrabInputSource inputSource);

    /// <summary>
    /// Observes the current best eligible grab candidate through the same deterministic discovery and
    /// <see cref="HandGrabCandidateSelector" /> ranking <see cref="IHand.Grab()" /> uses, side-correct and
    /// side-effect-free: no grab is initiated and no state moves (INTR-003 TR20; CTRL-002 TR9-TR10).
    /// </summary>
    /// <param name="candidate">The observed best candidate, or <see langword="null" /> with
    /// <see langword="false" /> when no eligible candidate is in range.</param>
    /// <returns><see langword="true" /> when an eligible candidate was observed.</returns>
    bool TryGetCurrentGrabCandidate(out GrabCandidateObservation? candidate);

    /// <summary>
    /// Cancels a pending grab deterministically through the same cleanup as release-abandonment — provider
    /// override released and pending state cleared — without touching a held object or applying any pose
    /// (INTR-002 R16, R59; CTRL-002 TR14). Idempotent: a no-op when no grab is pending.
    /// </summary>
    /// <returns><see langword="true" /> when a pending grab was cancelled.</returns>
    bool CancelPendingGrab();
}
