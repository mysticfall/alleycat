using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.XR.HandTracking;

/// <summary>
/// Per-side hand-pose source selected by the committed global hand-pose mode (XR-002 TR27).
/// </summary>
public interface IXRHandPoseSource
{
    /// <summary>
    /// Gets the limb side this source serves.
    /// </summary>
    LimbSide Side
    {
        get;
    }

    /// <summary>
    /// Gets the source currently selected for this side, derived from the committed global hand-pose mode.
    /// </summary>
    XRHandTrackingMode SelectedMode
    {
        get;
    }

    /// <summary>
    /// Gets the latest per-side observation used for mode arbitration, suitable for logging diagnostics.
    /// </summary>
    XRHandSourceObservation Observation
    {
        get;
    }

    /// <summary>
    /// Gets whether a valid optical wrist sample has been captured during the current optical session.
    /// </summary>
    bool EverCapturedWrist
    {
        get;
    }

    /// <summary>
    /// Tries to get the calibrated world-space wrist transform of the selected source for this side.
    /// </summary>
    /// <remarks>
    /// In optical mode the transform freezes at its last valid world-space value while tracking is lost
    /// (XR-002 TR5, TR31). In controller mode the live controller hand-position calibration anchor is returned
    /// unchanged (XR-002 TR8).
    /// </remarks>
    /// <param name="transform">Calibrated world-space wrist transform.</param>
    /// <returns><see langword="true" /> when a transform is available for the selected source.</returns>
    bool TryGetCalibratedWristTransform(out Transform3D transform);
}
