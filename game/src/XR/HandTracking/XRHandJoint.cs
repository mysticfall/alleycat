namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Tracked optical hand joints needed to retarget the 30 canonical finger bones (XR-002 TR12, TR14).
/// </summary>
/// <remarks>
/// <para>
/// Covers the wrist, the thumb metacarpal/proximal/distal chain, and the metacarpal plus
/// proximal/intermediate/distal chain of each non-thumb finger. Values are ordered anatomically — parent
/// joints always precede their children — so chain walks can rely on enum order; buffer indices use the
/// raw enum value.
/// </para>
/// <para>
/// The four non-thumb metacarpal joints are <see cref="XRHandJoints.TrackedJoints" /> source-only reference
/// parents: they anchor the non-thumb proximal rotations to their real anatomical parent but have no
/// destination bones on the character skeleton, which has no non-thumb metacarpal bones (XR-002 TR14).
/// </para>
/// </remarks>
public enum XRHandJoint
{
    /// <summary>
    /// Wrist joint; root of the tracked hand chains.
    /// </summary>
    Wrist = 0,

    /// <summary>
    /// Thumb metacarpal joint.
    /// </summary>
    ThumbMetacarpal = 1,

    /// <summary>
    /// Thumb proximal joint.
    /// </summary>
    ThumbProximal = 2,

    /// <summary>
    /// Thumb distal joint.
    /// </summary>
    ThumbDistal = 3,

    /// <summary>
    /// Index-finger metacarpal joint; source-only reference parent of the index proximal rotation.
    /// </summary>
    IndexMetacarpal = 4,

    /// <summary>
    /// Index proximal joint.
    /// </summary>
    IndexProximal = 5,

    /// <summary>
    /// Index intermediate joint.
    /// </summary>
    IndexIntermediate = 6,

    /// <summary>
    /// Index distal joint.
    /// </summary>
    IndexDistal = 7,

    /// <summary>
    /// Middle-finger metacarpal joint; source-only reference parent of the middle proximal rotation.
    /// </summary>
    MiddleMetacarpal = 8,

    /// <summary>
    /// Middle proximal joint.
    /// </summary>
    MiddleProximal = 9,

    /// <summary>
    /// Middle intermediate joint.
    /// </summary>
    MiddleIntermediate = 10,

    /// <summary>
    /// Middle distal joint.
    /// </summary>
    MiddleDistal = 11,

    /// <summary>
    /// Ring-finger metacarpal joint; source-only reference parent of the ring proximal rotation.
    /// </summary>
    RingMetacarpal = 12,

    /// <summary>
    /// Ring proximal joint.
    /// </summary>
    RingProximal = 13,

    /// <summary>
    /// Ring intermediate joint.
    /// </summary>
    RingIntermediate = 14,

    /// <summary>
    /// Ring distal joint.
    /// </summary>
    RingDistal = 15,

    /// <summary>
    /// Little-finger metacarpal joint; source-only reference parent of the little proximal rotation.
    /// </summary>
    LittleMetacarpal = 16,

    /// <summary>
    /// Little proximal joint.
    /// </summary>
    LittleProximal = 17,

    /// <summary>
    /// Little intermediate joint.
    /// </summary>
    LittleIntermediate = 18,

    /// <summary>
    /// Little distal joint.
    /// </summary>
    LittleDistal = 19,
}
