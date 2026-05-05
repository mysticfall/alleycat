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
}
