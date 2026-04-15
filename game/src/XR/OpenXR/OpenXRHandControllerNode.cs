using AlleyCat.Common;
using Godot;

namespace AlleyCat.XR.OpenXR;

/// <summary>
/// OpenXR hand-controller component implementing XR hand-controller abstraction.
/// </summary>
[GlobalClass]
public partial class OpenXRHandControllerNode : XRController3D, IXRHandController
{
    private ButtonPressedEventHandler _buttonPressedHandler = _ => { };
    private ButtonReleasedEventHandler _buttonReleasedHandler = _ => { };
    private InputFloatChangedEventHandler _inputFloatChangedHandler = (_, _) => { };
    private InputVector2ChangedEventHandler _inputVector2ChangedHandler = (_, _) => { };
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
            ?? throw new InvalidOperationException("OpenXR hand position node is not available before _Ready.");

    /// <inheritdoc />
    public override void _Ready()
    {
        HandPositionNodeResolved = this.RequireNode<Node3D>("HandPosition");

        _buttonPressedHandler = actionName => ActionButtonPressed?.Invoke(actionName);
        _buttonReleasedHandler = actionName => ActionButtonReleased?.Invoke(actionName);
        _inputFloatChangedHandler = (actionName, value) => ActionFloatInputChanged?.Invoke(actionName, (float)value);
        _inputVector2ChangedHandler = (actionName, value) => ActionVector2InputChanged?.Invoke(actionName, value);

        ButtonPressed += _buttonPressedHandler;
        ButtonReleased += _buttonReleasedHandler;
        InputFloatChanged += _inputFloatChangedHandler;
        InputVector2Changed += _inputVector2ChangedHandler;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        ButtonPressed -= _buttonPressedHandler;
        ButtonReleased -= _buttonReleasedHandler;
        InputFloatChanged -= _inputFloatChangedHandler;
        InputVector2Changed -= _inputVector2ChangedHandler;
    }
}
