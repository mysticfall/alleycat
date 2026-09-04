using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Shared composition math for calibrated world-space hand poses (XR-002 TR10, TR20).
/// </summary>
/// <remarks>
/// All helpers are pure transform math shared by the OpenXR runtime adapter and the mock runtime so both compose
/// the origin transform, reference frame, recentering state, and world scale exactly once, identically.
/// </remarks>
public static class XRHandPoseComposition
{
    /// <summary>
    /// Composes an origin-space transform into world space with the XR origin transform and non-unit world scale
    /// applied exactly once (XR-002 TR10).
    /// </summary>
    /// <param name="originGlobalTransform">World transform of the XR origin node.</param>
    /// <param name="worldScale">XR world scale.</param>
    /// <param name="originSpaceTransform">Raw transform relative to the XR origin reference space.</param>
    /// <returns>World-space transform.</returns>
    public static Transform3D ComposeWorldSpace(
        Transform3D originGlobalTransform,
        float worldScale,
        Transform3D originSpaceTransform)
        => originGlobalTransform.Scaled(new Vector3(worldScale, worldScale, worldScale)) * originSpaceTransform;

    /// <summary>
    /// Derives the palm-to-wrist relative transform from two raw origin-space tracker joint transforms.
    /// </summary>
    /// <param name="originSpacePalm">Raw palm joint transform relative to the XR origin.</param>
    /// <param name="originSpaceWrist">Raw wrist joint transform relative to the XR origin.</param>
    /// <returns>Wrist transform expressed in the palm frame, with unscaled metric translation.</returns>
    public static Transform3D ComposePalmToWristRelative(Transform3D originSpacePalm, Transform3D originSpaceWrist)
        => originSpacePalm.AffineInverse() * originSpaceWrist;

    /// <summary>
    /// Composes the calibrated world-space optical wrist from the palm-based XR node transform, the live
    /// palm-to-wrist relative transform, world scale, and the authored per-side calibration anchor
    /// (XR-002 TR9, TR10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The palm node's world transform already applies the XR origin transform, reference-frame adjustment,
    /// recentering, and world scale to the palm pose exactly once (the engine positions XRNode3D children through
    /// <c>XRPose</c>'s adjusted transform). The palm-to-wrist translation and the authored calibration anchor
    /// translation are expressed in unscaled metres, so world scale is applied to each of them exactly once here,
    /// matching the mock runtime's <c>origin.Scaled(worldScale) * raw * anchor</c> oracle.
    /// </para>
    /// <para>
    /// The final basis is orthonormalised so hand-authored calibration anchors cannot introduce scale or shear.
    /// </para>
    /// </remarks>
    /// <param name="palmWorldTransform">World transform of the palm-following XRNode3D.</param>
    /// <param name="palmToWristRelative">Wrist transform relative to the palm frame.</param>
    /// <param name="worldScale">XR world scale.</param>
    /// <param name="calibrationAnchor">Authored per-side full calibration anchor applied to the wrist.</param>
    /// <returns>Calibrated world-space wrist transform.</returns>
    public static Transform3D ComposeCalibratedWrist(
        Transform3D palmWorldTransform,
        Transform3D palmToWristRelative,
        float worldScale,
        Transform3D calibrationAnchor)
    {
        Transform3D scaledRelative = new(palmToWristRelative.Basis, palmToWristRelative.Origin * worldScale);
        Transform3D scaledAnchor = new(calibrationAnchor.Basis, calibrationAnchor.Origin * worldScale);
        Transform3D composed = palmWorldTransform * scaledRelative * scaledAnchor;

        return new Transform3D(composed.Basis.Orthonormalized(), composed.Origin);
    }
}
