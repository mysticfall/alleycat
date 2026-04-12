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
    /// Raised when the runtime reports pose recentering.
    /// </summary>
    event Action? PoseRecentered;

    /// <summary>
    /// Initialises the runtime using the given UI viewport and refresh-rate cap.
    /// </summary>
    bool Initialise(SubViewport viewport, int maximumRefreshRate);
}
