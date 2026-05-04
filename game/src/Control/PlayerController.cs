using AlleyCat.Common;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.Control;

/// <summary>
/// XR input bridge that drives a player locomotion component.
/// </summary>
[GlobalClass]
public partial class PlayerController : Node
{
    private XRManager? _xrManager;
    private IXRHandController? _leftHandController;
    private IXRHandController? _rightHandController;
    private ILocomotion? _locomotion;
    private bool _xrInitialised;
    private bool _isBound;

    /// <summary>
    /// Optional direct locomotion node reference.
    /// </summary>
    [Export]
    public Node? LocomotionNode
    {
        get;
        set;
    }

    /// <summary>
    /// XR vector2 action consumed from the left controller.
    /// </summary>
    [Export]
    public StringName MovementActionName
    {
        get;
        set;
    } = new("primary");

    /// <summary>
    /// XR vector2 action consumed from the right controller.
    /// </summary>
    [Export]
    public StringName RotationActionName
    {
        get;
        set;
    } = new("primary");

    /// <inheritdoc />
    public override void _Ready()
    {
        _locomotion = ResolveLocomotion();
        _xrManager = ResolveXRManager();

        if (_xrManager is null)
        {
            GD.PushWarning($"{nameof(PlayerController)} could not find an {nameof(XRManager)} in the current scene tree.");
            SetProcess(false);
            return;
        }

        _xrManager.Initialised += OnXRInitialised;

        if (_xrManager.InitialisationAttempted)
        {
            _xrInitialised = _xrManager.InitialisationSucceeded;

            if (!_xrInitialised)
            {
                GD.PushWarning($"{nameof(PlayerController)} skipped XR controller binding because XR initialisation failed.");
                SetProcess(false);
                return;
            }
        }

        if (_xrInitialised)
        {
            _isBound = TryBindControllers();
        }

        SetProcess(!_isBound);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xrManager is not null)
        {
            _xrManager.Initialised -= OnXRInitialised;
        }

        DisconnectControllers();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _ = delta;

        if (!_isBound)
        {
            if (_xrInitialised)
            {
                _isBound = TryBindControllers();
            }
        }
    }

    private ILocomotion ResolveLocomotion()
    {
        Node locomotionNode = LocomotionNode ?? this.RequireNode<Node>("../Locomotion");
        return locomotionNode as ILocomotion
               ?? throw new InvalidOperationException(
                   $"Node '{locomotionNode.GetPath()}' must implement {nameof(ILocomotion)}.");
    }

    private XRManager? ResolveXRManager()
    {
        foreach (Node node in GetTree().Root.FindChildren(pattern: "*", type: nameof(XRManager), recursive: true, owned: false))
        {
            if (node is XRManager manager)
            {
                return manager;
            }
        }

        return null;
    }

    private bool TryBindControllers()
    {
        XRManager? xrManager = _xrManager;
        if (xrManager is null)
        {
            return false;
        }

        DisconnectControllers();

        _leftHandController = xrManager.Runtime.LeftHandController;
        _rightHandController = xrManager.Runtime.RightHandController;

        _leftHandController.ActionVector2InputChanged += OnLeftControllerVector2Changed;
        _rightHandController.ActionVector2InputChanged += OnRightControllerVector2Changed;
        _isBound = true;
        SetProcess(false);
        return true;
    }

    private void DisconnectControllers()
    {
        if (_leftHandController is not null)
        {
            _leftHandController.ActionVector2InputChanged -= OnLeftControllerVector2Changed;
            _leftHandController = null;
        }

        if (_rightHandController is not null)
        {
            _rightHandController.ActionVector2InputChanged -= OnRightControllerVector2Changed;
            _rightHandController = null;
        }

        _isBound = false;
        SetProcess(_xrInitialised);

        UpdateMovementInput(Vector2.Zero);
        UpdateRotationInput(Vector2.Zero);
    }

    private void OnXRInitialised(bool succeeded)
    {
        if (!succeeded)
        {
            GD.PushWarning($"{nameof(PlayerController)} skipped XR controller binding because XR initialisation failed.");
            SetProcess(false);
            return;
        }

        _xrInitialised = true;
        _isBound = TryBindControllers();

        if (!_isBound)
        {
            SetProcess(true);
        }
    }

    private void OnLeftControllerVector2Changed(string actionName, Vector2 value)
    {
        if (actionName == MovementActionName)
        {
            UpdateMovementInput(value);
        }
    }

    private void OnRightControllerVector2Changed(string actionName, Vector2 value)
    {
        if (actionName == RotationActionName)
        {
            UpdateRotationInput(value);
        }
    }

    private void UpdateMovementInput(Vector2 value)
        => _locomotion?.SetMovementInput(value);

    private void UpdateRotationInput(Vector2 value)
        => _locomotion?.SetRotationInput(value);
}
