using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;

namespace AlleyCat.XR;

/// <summary>
/// XR runtime contract used by <see cref="XRManager"/>.
/// </summary>
public interface IXRRuntime
{
    /// <summary>
    /// Gets the active runtime origin abstraction.
    /// </summary>
    IXROrigin Origin
    {
        get;
    }

    /// <summary>
    /// Gets the active runtime camera abstraction.
    /// </summary>
    IXRCamera Camera
    {
        get;
    }

    /// <summary>
    /// Gets the active right-hand controller abstraction.
    /// </summary>
    IXRHandController RightHandController
    {
        get;
    }

    /// <summary>
    /// Gets the active left-hand controller abstraction.
    /// </summary>
    IXRHandController LeftHandController
    {
        get;
    }

    /// <summary>
    /// Gets the committed global hand-pose mode shared by both hands (XR-002 TR1, TR27).
    /// </summary>
    XRHandTrackingMode HandTrackingMode
    {
        get;
    }

    /// <summary>
    /// Gets the optical hand-joint provider consumed by the finger retargeting modifier (XR-002 TR28).
    /// </summary>
    IXRHandJointProvider OpticalHandJoints
    {
        get;
    }

    /// <summary>
    /// Gets the per-side hand-pose source selected by the committed global hand-pose mode (XR-002 TR27).
    /// </summary>
    /// <param name="side">Limb side of the hand.</param>
    /// <returns>The hand-pose source for the requested side.</returns>
    IXRHandPoseSource GetHandPoseSource(LimbSide side);

    /// <summary>
    /// Raised when the runtime reports pose recentering.
    /// </summary>
    event Action? PoseRecentered;

    /// <summary>
    /// Raised when the committed global hand-pose mode changes.
    /// </summary>
    event Action? HandTrackingModeChanged;

    /// <summary>
    /// Initialises the runtime using the given UI viewport and refresh-rate cap.
    /// </summary>
    bool Initialise(SubViewport viewport, int maximumRefreshRate);
}
