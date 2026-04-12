using Godot;

namespace AlleyCat.XR.Mock;

/// <summary>
/// Mock hand-controller component implementing XR hand-controller abstraction.
/// </summary>
[GlobalClass]
public partial class MockXRHandControllerNode : Node3D, IXRHandController
{
#pragma warning disable CS0067 // Event is never used
    /// <inheritdoc />
    public event Action<string>? ActionButtonPressed;

    /// <inheritdoc />
    public event Action<string>? ActionButtonReleased;

    /// <inheritdoc />
    public event Action<string, float>? ActionFloatInputChanged;

    /// <inheritdoc />
    public event Action<string, Vector2>? ActionVector2InputChanged;
#pragma warning restore CS0067

    /// <inheritdoc />
    public Node3D ControllerNode => this;
}

