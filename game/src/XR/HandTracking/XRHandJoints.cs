namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Static topology helpers for the tracked <see cref="XRHandJoint" /> chains used by finger retargeting
/// (XR-002 TR14, TR20-TR23).
/// </summary>
public static class XRHandJoints
{
    /// <summary>
    /// All tracked joints — the wrist, the thumb chain, and each non-thumb finger's metacarpal plus
    /// proximal/intermediate/distal chain — in anatomical enum order (parents before children).
    /// </summary>
    public static readonly XRHandJoint[] TrackedJoints =
    [
        XRHandJoint.Wrist,
        XRHandJoint.ThumbMetacarpal,
        XRHandJoint.ThumbProximal,
        XRHandJoint.ThumbDistal,
        XRHandJoint.IndexMetacarpal,
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
        XRHandJoint.MiddleMetacarpal,
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
        XRHandJoint.RingMetacarpal,
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
        XRHandJoint.LittleMetacarpal,
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    /// <summary>
    /// The 15 finger joints that own destination bones on the character skeleton, in canonical per-side
    /// processing order: the thumb metacarpal/proximal/distal chain followed by each non-thumb finger's
    /// proximal/intermediate/distal chain (XR-002 TR12). The wrist and the non-thumb metacarpals are
    /// excluded — they are source-only reference parents, never written.
    /// </summary>
    public static readonly XRHandJoint[] DestinationJoints =
    [
        XRHandJoint.ThumbMetacarpal,
        XRHandJoint.ThumbProximal,
        XRHandJoint.ThumbDistal,
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    /// <summary>The 12 non-thumb destinations addressed by calibration records, in per-side buffer order.</summary>
    public static readonly XRHandJoint[] NonThumbDestinationJoints =
    [
        XRHandJoint.IndexProximal,
        XRHandJoint.IndexIntermediate,
        XRHandJoint.IndexDistal,
        XRHandJoint.MiddleProximal,
        XRHandJoint.MiddleIntermediate,
        XRHandJoint.MiddleDistal,
        XRHandJoint.RingProximal,
        XRHandJoint.RingIntermediate,
        XRHandJoint.RingDistal,
        XRHandJoint.LittleProximal,
        XRHandJoint.LittleIntermediate,
        XRHandJoint.LittleDistal,
    ];

    /// <summary>
    /// Resolves a supported destination joint — thumb or non-thumb — to its per-side calibration-buffer index in
    /// canonical <see cref="DestinationJoints" /> order: the thumb metacarpal/proximal/distal chain first, then each
    /// non-thumb proximal/intermediate/distal chain (XR-002 TR12, TR44).
    /// </summary>
    public static bool TryGetDestinationIndex(XRHandJoint joint, out int index)
    {
        index = joint switch
        {
            XRHandJoint.ThumbMetacarpal => 0,
            XRHandJoint.ThumbProximal => 1,
            XRHandJoint.ThumbDistal => 2,
            XRHandJoint.IndexProximal => 3,
            XRHandJoint.IndexIntermediate => 4,
            XRHandJoint.IndexDistal => 5,
            XRHandJoint.MiddleProximal => 6,
            XRHandJoint.MiddleIntermediate => 7,
            XRHandJoint.MiddleDistal => 8,
            XRHandJoint.RingProximal => 9,
            XRHandJoint.RingIntermediate => 10,
            XRHandJoint.RingDistal => 11,
            XRHandJoint.LittleProximal => 12,
            XRHandJoint.LittleIntermediate => 13,
            XRHandJoint.LittleDistal => 14,
            XRHandJoint.Wrist
                or XRHandJoint.IndexMetacarpal
                or XRHandJoint.MiddleMetacarpal
                or XRHandJoint.RingMetacarpal
                or XRHandJoint.LittleMetacarpal => -1,
            _ => -1,
        };
        return index >= 0;
    }

    /// <summary>
    /// Resolves a supported non-thumb destination to its per-side calibration-buffer index.
    /// </summary>
    public static bool TryGetNonThumbDestinationIndex(XRHandJoint joint, out int index)
    {
        index = joint switch
        {
            XRHandJoint.IndexProximal => 0,
            XRHandJoint.IndexIntermediate => 1,
            XRHandJoint.IndexDistal => 2,
            XRHandJoint.MiddleProximal => 3,
            XRHandJoint.MiddleIntermediate => 4,
            XRHandJoint.MiddleDistal => 5,
            XRHandJoint.RingProximal => 6,
            XRHandJoint.RingIntermediate => 7,
            XRHandJoint.RingDistal => 8,
            XRHandJoint.LittleProximal => 9,
            XRHandJoint.LittleIntermediate => 10,
            XRHandJoint.LittleDistal => 11,
            XRHandJoint.Wrist
                or XRHandJoint.ThumbMetacarpal
                or XRHandJoint.ThumbProximal
                or XRHandJoint.ThumbDistal
                or XRHandJoint.IndexMetacarpal
                or XRHandJoint.MiddleMetacarpal
                or XRHandJoint.RingMetacarpal
                or XRHandJoint.LittleMetacarpal => -1,
            _ => -1,
        };
        return index >= 0;
    }

    /// <summary>
    /// Gets the tracked joint a joint's rotation is derived relative to — its actual anatomical parent
    /// joint — or <see langword="null" /> for the wrist root (XR-002 TR20). Non-thumb proximal joints derive
    /// relative to their finger's metacarpal, which is a source-only reference parent without a destination
    /// bone.
    /// </summary>
    /// <param name="joint">Joint whose required source parent is queried.</param>
    /// <returns>The required source-parent joint, or <see langword="null" /> for the wrist root.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown for an undefined joint value.</exception>
    public static XRHandJoint? GetRequiredSourceParent(XRHandJoint joint)
        => joint switch
        {
            XRHandJoint.Wrist => null,
            XRHandJoint.ThumbMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.ThumbProximal => XRHandJoint.ThumbMetacarpal,
            XRHandJoint.ThumbDistal => XRHandJoint.ThumbProximal,
            XRHandJoint.IndexMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.IndexProximal => XRHandJoint.IndexMetacarpal,
            XRHandJoint.IndexIntermediate => XRHandJoint.IndexProximal,
            XRHandJoint.IndexDistal => XRHandJoint.IndexIntermediate,
            XRHandJoint.MiddleMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.MiddleProximal => XRHandJoint.MiddleMetacarpal,
            XRHandJoint.MiddleIntermediate => XRHandJoint.MiddleProximal,
            XRHandJoint.MiddleDistal => XRHandJoint.MiddleIntermediate,
            XRHandJoint.RingMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.RingProximal => XRHandJoint.RingMetacarpal,
            XRHandJoint.RingIntermediate => XRHandJoint.RingProximal,
            XRHandJoint.RingDistal => XRHandJoint.RingIntermediate,
            XRHandJoint.LittleMetacarpal => XRHandJoint.Wrist,
            XRHandJoint.LittleProximal => XRHandJoint.LittleMetacarpal,
            XRHandJoint.LittleIntermediate => XRHandJoint.LittleProximal,
            XRHandJoint.LittleDistal => XRHandJoint.LittleIntermediate,
            _ => throw new ArgumentOutOfRangeException(nameof(joint), joint, "Unknown hand joint."),
        };
}
