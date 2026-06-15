using AlleyCat.Body;
using AlleyCat.Body.Hands;
using AlleyCat.Control.Locomotion;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;

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
    private bool _leftFloatGrabPressed;
    private bool _rightFloatGrabPressed;
    private bool _handResolutionRetryQueued;

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
    /// XR analogue action used as a grab fallback when controllers expose grip as a float instead of a click button.
    /// </summary>
    [Export]
    public StringName GrabFloatActionName { get; set; } = new("grip");

    /// <summary>
    /// Analogue grab threshold for <see cref="GrabFloatActionName" /> press/release transitions.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float GrabFloatPressThreshold { get; set; } = 0.55f;

    /// <summary>
    /// Analogue grab release threshold for <see cref="GrabFloatActionName" />.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float GrabFloatReleaseThreshold { get; set; } = 0.35f;

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
        _ = TryResolveLocomotion(out _locomotion);
        _hands = ResolveHands();
        QueueHandsResolutionRetryIfNeeded();
        _xrManager = ResolveXRManager();

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

    private bool TryResolveLocomotion(out ILocomotion? locomotion)
    {
        locomotion = null;

        Node? locomotionNode = LocomotionNode;
        if (locomotionNode is null || !IsInstanceValid(locomotionNode))
        {
            locomotionNode = GetParent()?.GetNodeOrNull<Node>("Locomotion");
        }

        if (locomotionNode is null)
        {
            return false;
        }

        locomotion = locomotionNode as ILocomotion
            ?? throw new InvalidOperationException(
                $"Node '{locomotionNode.GetPath()}' must implement {nameof(ILocomotion)}.");
        return true;
    }

    private static XRManager ResolveXRManager()
        => Game.Instance.GetRequiredService<XRManager>();

    private IHasHands? ResolveHands()
    {
        Node? handHolderNode = HandHolderNode ?? GetParent()?.GetNodeOrNull<Node>("Hands");
        return handHolderNode as IHasHands;
    }

    private bool TryResolveHands(out IHasHands? hands)
    {
        hands = ResolveHands();
        return hands is not null;
    }

    private bool EnsureHandsResolved()
    {
        if (_hands is not null && (_hands is not Node handsNode || IsInstanceValid(handsNode)))
        {
            return true;
        }

        bool resolved = TryResolveHands(out _hands);
        if (!resolved)
        {
            QueueHandsResolutionRetryIfNeeded();
        }

        return resolved;
    }

    private void QueueHandsResolutionRetryIfNeeded()
    {
        if (_hands is not null || _handResolutionRetryQueued || !IsInsideTree())
        {
            return;
        }

        _handResolutionRetryQueued = true;
        _ = CallDeferred(MethodName.ResolveHandsAfterTreeSettled);
    }

    private void ResolveHandsAfterTreeSettled()
    {
        _handResolutionRetryQueued = false;
        if (!IsInstanceValid(this) || !IsInsideTree() || _hands is not null)
        {
            return;
        }

        _ = EnsureHandsResolved();
    }

    private bool TryBindControllers()
    {
        XRManager? xrManager = _xrManager;
        if (xrManager is null || !TryResolveRuntimeDependencies())
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
        _leftHandController.ActionFloatInputChanged += OnLeftControllerFloatChanged;
        _rightHandController.ActionButtonPressed += OnRightControllerButtonPressed;
        _rightHandController.ActionButtonReleased += OnRightControllerButtonReleased;
        _rightHandController.ActionFloatInputChanged += OnRightControllerFloatChanged;
        _isBound = true;
        SetProcess(false);
        return true;
    }

    private bool TryResolveRuntimeDependencies()
        => (_locomotion is not null || TryResolveLocomotion(out _locomotion)) && EnsureHandsResolved();

    private void DisconnectControllers()
    {
        if (_leftHandController is IXRHandController leftHandController)
        {
            leftHandController.ActionVector2InputChanged -= OnLeftControllerVector2Changed;
            leftHandController.ActionButtonPressed -= OnLeftControllerButtonPressed;
            leftHandController.ActionButtonReleased -= OnLeftControllerButtonReleased;
            leftHandController.ActionFloatInputChanged -= OnLeftControllerFloatChanged;
            _leftHandController = null;
        }

        if (_rightHandController is IXRHandController rightHandController)
        {
            rightHandController.ActionVector2InputChanged -= OnRightControllerVector2Changed;
            rightHandController.ActionButtonPressed -= OnRightControllerButtonPressed;
            rightHandController.ActionButtonReleased -= OnRightControllerButtonReleased;
            rightHandController.ActionFloatInputChanged -= OnRightControllerFloatChanged;
            _rightHandController = null;
        }

        _isBound = false;
        _leftFloatGrabPressed = false;
        _rightFloatGrabPressed = false;
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
        _ = EnsureHandsResolved();
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

    private void OnLeftControllerFloatChanged(string actionName, float value)
        => HandleGrabFloatChanged(LimbSide.Left, actionName, value, ref _leftFloatGrabPressed);

    private void OnRightControllerFloatChanged(string actionName, float value)
        => HandleGrabFloatChanged(LimbSide.Right, actionName, value, ref _rightFloatGrabPressed);

    private void OnLeftControllerButtonPressed(string actionName) => HandleGrabButtonPressed(LimbSide.Left, actionName);

    private void OnLeftControllerButtonReleased(string actionName) => HandleGrabButtonReleased(LimbSide.Left, actionName);
    private void OnRightControllerButtonPressed(string actionName) => HandleGrabButtonPressed(LimbSide.Right, actionName);
    private void OnRightControllerButtonReleased(string actionName) => HandleGrabButtonReleased(LimbSide.Right, actionName);

    private void HandleGrabButtonPressed(LimbSide side, string actionName)
    {
        if (actionName == GrabActionName
            && EnsureHandsResolved()
            && _hands?.TryGetHand(side, out IHand? hand) == true
            && hand is not null)
        {
            _ = hand.Grab();
        }
    }

    private void HandleGrabButtonReleased(LimbSide side, string actionName)
    {
        if (actionName == GrabActionName
            && EnsureHandsResolved()
            && _hands?.TryGetHand(side, out IHand? hand) == true
            && hand is not null)
        {
            hand.Release();
        }
    }

    private void HandleGrabFloatChanged(LimbSide side, string actionName, float value, ref bool pressed)
    {
        float pressThreshold = Mathf.Clamp(GrabFloatPressThreshold, 0.0f, 1.0f);
        float releaseThreshold = Mathf.Min(Mathf.Clamp(GrabFloatReleaseThreshold, 0.0f, 1.0f), pressThreshold);
        if (actionName != GrabFloatActionName)
        {
            return;
        }

        if (!pressed && value >= pressThreshold)
        {
            pressed = true;
            HandleGrabButtonPressed(side, GrabActionName);
        }
        else if (pressed && value <= releaseThreshold)
        {
            pressed = false;
            HandleGrabButtonReleased(side, GrabActionName);
        }
    }

    private void UpdateMovementInput(Vector2 value)
        => _locomotion?.Move(value);

    private void UpdateRotationInput(Vector2 value)
        => _locomotion?.Rotate(value);
}
