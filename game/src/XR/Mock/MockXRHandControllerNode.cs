using AlleyCat.Common;
using Godot;

namespace AlleyCat.XR.Mock;

/// <summary>
/// Mock hand-controller component implementing XR hand-controller abstraction.
/// </summary>
[GlobalClass]
public partial class MockXRHandControllerNode : Node3D, IXRHandController
{
    private Node3D? HandPositionNodeResolved
    {
        get;
        set;
    }

    /// <inheritdoc />
    public event Action<string>? ActionButtonPressed;

    /// <inheritdoc />
    public event Action<string>? ActionButtonReleased;

    /// <inheritdoc />
    public event Action<string, float>? ActionFloatInputChanged;

    /// <inheritdoc />
    public event Action<string, Vector2>? ActionVector2InputChanged;

    /// <inheritdoc />
    public Node3D ControllerNode => this;

    /// <inheritdoc />
    public Node3D HandPositionNode
        => HandPositionNodeResolved
            ?? throw new InvalidOperationException("Mock hand position node is not available before _Ready.");

    /// <inheritdoc />
    public override void _Ready()
        => HandPositionNodeResolved = this.RequireNode<Node3D>("HandPosition");

    /// <summary>
    /// Emits a mock XR action-button press for integration tests.
    /// </summary>
    public void TriggerActionButtonPressed(string actionName)
        => ActionButtonPressed?.Invoke(actionName);

    /// <summary>
    /// Emits a mock XR action-button release for integration tests.
    /// </summary>
    public void TriggerActionButtonReleased(string actionName)
        => ActionButtonReleased?.Invoke(actionName);

    /// <summary>
    /// Emits a mock XR float input change for integration tests.
    /// </summary>
    public void TriggerActionFloatInputChanged(string actionName, float value)
        => ActionFloatInputChanged?.Invoke(actionName, value);

    /// <summary>
    /// Emits a mock XR Vector2 input change for integration tests.
    /// </summary>
    public void TriggerActionVector2InputChanged(string actionName, Vector2 value)
        => ActionVector2InputChanged?.Invoke(actionName, value);
}
