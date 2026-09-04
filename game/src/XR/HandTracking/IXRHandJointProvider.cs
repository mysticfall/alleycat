using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Optical hand-joint provider consumed by the finger retargeting modifier (XR-002 TR12, TR28).
/// </summary>
public interface IXRHandJointProvider
{
    /// <summary>
    /// Tries to get the world-space transform of a tracked optical hand joint.
    /// </summary>
    /// <remarks>
    /// A joint is accepted only when its transform is finite and orientation-usable (orientation-tracked or
    /// orientation-valid) and its required source parent is accepted the same way (XR-002 TR20, TR22); inferred
    /// positions are never consumed because finger retargeting is rotation-only. Joint transforms are world-space
    /// with the XR origin transform and world scale applied exactly once; wrist-frame calibration anchors do not
    /// apply to finger joints because the source-relative rotations derived from them are frame-invariant.
    /// </remarks>
    /// <param name="side">Limb side of the hand.</param>
    /// <param name="joint">Tracked joint to read.</param>
    /// <param name="sample">
    /// Exact source sample observed by production, including the tracker-local and composed world transforms.
    /// </param>
    /// <returns>
    /// <see langword="true" /> when the joint and its required source parent are finite and orientation-usable;
    /// otherwise <see langword="false" />.
    /// </returns>
    bool TryGetJoint(LimbSide side, XRHandJoint joint, out XRHandJointSourceSample sample);
}

/// <summary>
/// Allocation-free reason why an optical source sample did not pass the production orientation/parent gate.
/// </summary>
public enum XRHandJointSourceRejection
{
    /// <summary>The source sample passed the production gate.</summary>
    None = 0,

    /// <summary>No hand tracker is registered for the requested side.</summary>
    NoTracker = 1,

    /// <summary>The tracker exists but has no tracking data.</summary>
    NoTrackingData = 2,

    /// <summary>The requested joint is unavailable from a non-OpenXR provider.</summary>
    JointUnavailable = 3,

    /// <summary>The joint reports neither orientation-tracked nor orientation-valid.</summary>
    JointOrientationUnusable = 4,

    /// <summary>The joint's tracker-local transform is non-finite.</summary>
    JointNonFinite = 5,

    /// <summary>The required source parent is unavailable from a non-OpenXR provider.</summary>
    ParentUnavailable = 6,

    /// <summary>The required source parent reports neither orientation-tracked nor orientation-valid.</summary>
    ParentOrientationUnusable = 7,

    /// <summary>The required source-parent tracker-local transform is non-finite.</summary>
    ParentNonFinite = 8,
}

/// <summary>
/// Allocation-free source sample returned at the production hand-joint boundary.
/// </summary>
/// <remarks>
/// Position flags and composed-world finiteness do not affect <see cref="ProductionAccepted" />. That gate
/// requires tracker data, a finite orientation-usable tracker-local joint, and the same requirements for its
/// source parent. The composed world transform remains available even when the gate rejects the joint.
/// </remarks>
public readonly record struct XRHandJointSourceSample(
    LimbSide Side,
    XRHandJoint Joint,
    bool HasTracker,
    bool HasTrackingData,
    long RawFlags,
    bool OrientationValid,
    bool OrientationTracked,
    bool PositionValid,
    bool PositionTracked,
    Transform3D TrackerLocalTransform,
    Transform3D ProductionWorldTransform,
    bool TrackerLocalTransformFinite,
    bool ProductionWorldTransformFinite,
    bool ProductionAccepted,
    XRHandJointSourceRejection RejectionReason)
{
    /// <summary>Whether both retained transforms are finite.</summary>
    public bool TransformFinite => TrackerLocalTransformFinite && ProductionWorldTransformFinite;
}
