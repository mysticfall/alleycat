using AlleyCat.Body;
using AlleyCat.Body.Hands;
using AlleyCat.Common;
using AlleyCat.Control.Locomotion;
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
    private IHasHands? _hands;
    private bool _xrInitialised;
    private bool _isBound;

    /// <summary>
    /// Optional direct locomotion node reference.
    /// </summary>
    // TODO: Refactor this to target ILocomotive once a player node type owns locomotion as a holder trait.
    [Export]
    public Node? LocomotionNode
    {
        get;
        set;
    }

    /// <summary>
    /// Optional node implementing <see cref="IHasHands" /> for hand grab routing.
    /// </summary>
    [Export]
    public Node? HandHolderNode
    {
        get; set;
    }

    /// <summary>
    /// XR button action used to trigger hand grab and release.
    /// </summary>
    [Export]
    public StringName GrabActionName { get; set; } = new("grip_click");

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
        _hands = ResolveHands();
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
        if (_xrManager is XRManager xrManager)
        {
            xrManager.Initialised -= OnXRInitialised;
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

    private IHasHands? ResolveHands()
    {
        Node? handHolderNode = HandHolderNode ?? GetParent()?.GetNodeOrNull<Node>("Hands");
        return handHolderNode as IHasHands;
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
        _leftHandController.ActionButtonPressed += OnLeftControllerButtonPressed;
        _leftHandController.ActionButtonReleased += OnLeftControllerButtonReleased;
        _rightHandController.ActionButtonPressed += OnRightControllerButtonPressed;
        _rightHandController.ActionButtonReleased += OnRightControllerButtonReleased;
        _isBound = true;
        SetProcess(false);
        return true;
    }

    private void DisconnectControllers()
    {
        if (_leftHandController is IXRHandController leftHandController)
        {
            leftHandController.ActionVector2InputChanged -= OnLeftControllerVector2Changed;
            leftHandController.ActionButtonPressed -= OnLeftControllerButtonPressed;
            leftHandController.ActionButtonReleased -= OnLeftControllerButtonReleased;
            _leftHandController = null;
        }

        if (_rightHandController is IXRHandController rightHandController)
        {
            rightHandController.ActionVector2InputChanged -= OnRightControllerVector2Changed;
            rightHandController.ActionButtonPressed -= OnRightControllerButtonPressed;
            rightHandController.ActionButtonReleased -= OnRightControllerButtonReleased;
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

    private void OnLeftControllerButtonPressed(string actionName) => HandleGrabButtonPressed(LimbSide.Left, actionName);

    private void OnLeftControllerButtonReleased(string actionName) => HandleGrabButtonReleased(LimbSide.Left, actionName);
    private void OnRightControllerButtonPressed(string actionName) => HandleGrabButtonPressed(LimbSide.Right, actionName);
    private void OnRightControllerButtonReleased(string actionName) => HandleGrabButtonReleased(LimbSide.Right, actionName);

    private void HandleGrabButtonPressed(LimbSide side, string actionName)
    {
        if (actionName == GrabActionName && _hands?.TryGetHand(side, out IHand? hand) == true)
        {
            _ = hand?.Grab();
        }
    }

    private void HandleGrabButtonReleased(LimbSide side, string actionName)
    {
        if (actionName == GrabActionName && _hands?.TryGetHand(side, out IHand? hand) == true)
        {
            hand?.Release();
        }
    }

    private void UpdateMovementInput(Vector2 value)
        => _locomotion?.Move(value);

    private void UpdateRotationInput(Vector2 value)
        => _locomotion?.Rotate(value);
}
