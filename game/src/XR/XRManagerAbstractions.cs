using Godot;

namespace AlleyCat.XR;

/// <summary>
/// XR origin abstraction.
/// </summary>
public interface IXROrigin
{
    /// <summary>
    /// Underlying origin node.
    /// </summary>
    Node3D OriginNode
    {
        get;
    }

    /// <summary>
    /// XR world scale.
    /// </summary>
    float WorldScale
    {
        get; set;
    }
}

/// <summary>
/// XR camera abstraction.
/// </summary>
public interface IXRCamera
{
    /// <summary>
    /// Underlying camera node.
    /// </summary>
    Camera3D CameraNode
    {
        get;
    }
}

/// <summary>
/// XR hand-controller abstraction.
/// </summary>
public interface IXRHandController
{
    /// <summary>
    /// Raised when an XR action button is pressed.
    /// </summary>
    event Action<string>? ActionButtonPressed;

    /// <summary>
    /// Raised when an XR action button is released.
    /// </summary>
    event Action<string>? ActionButtonReleased;

    /// <summary>
    /// Raised when an XR float input value changes.
    /// </summary>
    event Action<string, float>? ActionFloatInputChanged;

    /// <summary>
    /// Raised when an XR vector2 input value changes.
    /// </summary>
    event Action<string, Vector2>? ActionVector2InputChanged;

    /// <summary>
    /// Underlying controller node.
    /// </summary>
    Node3D ControllerNode
    {
        get;
    }

    /// <summary>
    /// Runtime-authored hand marker used for authoritative hand pose.
    /// </summary>
    Node3D HandPositionNode
    {
        get;
    }
}
