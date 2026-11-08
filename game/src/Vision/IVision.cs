using AlleyCat.Sense;
using Godot;

namespace AlleyCat.Vision;

/// <summary>
/// Component capability representing paired eye look and blink control.
/// </summary>
public interface IVision : ISense<IVisualPercept>
{
    /// <summary>
    /// Gets or sets the optional target node the eyes should look towards.
    /// </summary>
    VisualCue? LookTarget
    {
        get;
        set;
    }

    /// <summary>
    /// Gets the character's vision camera used for image capture, or <c>null</c> while unconfigured.
    /// For the player it is the active XR runtime camera; for NPCs it is the template-authored
    /// vision rig camera.
    /// </summary>
    Camera3D? Camera
    {
        get;
    }

    /// <summary>
    /// Sets the optional target node the eyes should look towards.
    /// </summary>
    void SetLookTarget(VisualCue? target);

    /// <summary>
    /// Clears the current directed look target.
    /// </summary>
    void ClearLookTarget();

    /// <summary>
    /// Captures one fresh frame from the vision camera's viewport at call time and returns the
    /// readback image. Capture without a configured camera fails clearly; it never silently yields
    /// an empty or stale image. Capture publishes no percepts and changes no gaze, eye
    /// presentation, or live-view state.
    /// </summary>
    ValueTask<Image> CaptureImage();
}
