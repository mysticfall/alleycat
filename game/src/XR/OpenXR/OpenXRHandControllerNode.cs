using Godot;

namespace AlleyCat.XR.OpenXR;

/// <summary>
/// OpenXR hand-controller component implementing XR hand-controller abstraction.
/// </summary>
[GlobalClass]
public partial class OpenXRHandControllerNode : XRController3D, IXRHandController
{
    private ButtonPressedEventHandler _buttonPressedHandler = null!;

    private ButtonReleasedEventHandler _buttonReleasedHandler = null!;

    private InputFloatChangedEventHandler _inputFloatChangedHandler = null!;

    private InputVector2ChangedEventHandler _inputVector2ChangedHandler = null!;

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
    public override void _Ready()
    {
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
