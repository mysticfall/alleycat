using AlleyCat.Core.Logging;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

/// <summary>
/// XR input bridge for player hand interactions.
/// </summary>
/// <remarks>
/// <para>
/// While the <see cref="SceneTree" /> is paused (for example while the main menu is open), incoming XR
/// events are ignored through the built-in <see cref="Node.CanProcess()" /> guard. XR controller relays
/// keep emitting during a pause (their runtime subtree processes with
/// <see cref="Node.ProcessModeEnum.Always" />), so each gameplay handler must return early while this node
/// cannot process.
/// </para>
/// <para>
/// Grab <em>presses and releases</em> issue no <see cref="IHand.Grab()" /> or <see cref="IHand.Release()" />
/// action while paused. Instead, the grab handlers record the latest <em>valid</em> physical grip state per
/// side — digital press/release edges and analogue values through the unchanged hysteresis thresholds —
/// without treating absent, invalid, or unobserved state as open (CTRL-002 TR19).
/// </para>
/// <para>
/// On resume, each side reconciles exactly once through the ordinary unpaused release path: a known-open
/// physical grip cancels a pending grab or releases a held one, a known-held grip preserves either state,
/// and a press/release cycle completed during the pause creates no grab because paused presses are never
/// replayed (CTRL-002 UR9, TR20-TR22).
/// </para>
/// <para>
/// Controller grab edges act regardless of the committed hand-tracking mode: a press begins a grab with
/// <see cref="HandGrabInputSource.Controller" /> provenance and a release ends whatever that hand holds,
/// so a committed <c>Optical</c> mode never silently drops physical controller input.
/// </para>
/// </remarks>
[GlobalClass]
public partial class PlayerController : Node
{
    /// <summary>Player-only generated motion owner; absent in hand-only fixtures.</summary>
    [Export]
    public PlayerMotionBricks? Motion
    {
        get; set;
    }
    private XRManager? _xrManager;
    private IXRHandController? _leftHandController;
    private IXRHandController? _rightHandController;
    private IHasHands? _hands;
    private ILogger<PlayerController>? _logger;
    private bool _xrInitialised;
    private bool _isBound;
    private bool _leftFloatGrabPressed;
    private bool _rightFloatGrabPressed;
    private bool _handResolutionRetryQueued;
    private PausedGripState _leftPausedGrip;
    private PausedGripState _rightPausedGrip;

    /// <summary>
    /// Latest valid physical grip state observed for one side during the current pause (CTRL-002 TR19-TR20).
    /// </summary>
    private enum PausedGripState
    {
        /// <summary>No valid grip observation has arrived during the current pause.</summary>
        Unobserved = 0,

        /// <summary>The latest valid observation is a closed grip.</summary>
        Held = 1,

        /// <summary>The latest valid observation is an open grip.</summary>
        Open = 2,
    }

    /// <summary>Analogue grip edge classified with the unchanged press/release hysteresis thresholds.</summary>
    private enum AnalogueGripEdge
    {
        /// <summary>The value holds no threshold condition for the current latch state.</summary>
        None = 0,

        /// <summary>The value crossed the press threshold from a released latch.</summary>
        Pressed = 1,

        /// <summary>The value crossed the release threshold from a pressed latch.</summary>
        Released = 2,
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

    /// <inheritdoc />
    public override void _Ready()
    {
        _hands = ResolveHands();
        QueueHandsResolutionRetryIfNeeded();
        _xrManager = ResolveXRManager();

        _xrManager.Initialised += OnXRInitialised;
        if (_xrManager is XRManager xrManager)
        {
            xrManager.HandTrackingModeChanged += OnHandTrackingModeChanged;
        }

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
            xrManager.HandTrackingModeChanged -= OnHandTrackingModeChanged;
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

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what == NotificationPaused)
        {
            Motion?.ClearInput();
            BeginPausedGripObservation();
        }
        else if (what == NotificationUnpaused)
        {
            // Deferred to the first idle message of the unpaused tree: restoring a held object's parenting
            // from inside the pause notification itself leaves the reparent half-applied (a detached node).
            _ = CallDeferred(MethodName.ReconcilePausedGripsAfterResume);
        }
    }

    private void ReconcilePausedGripsAfterResume()
    {
        if (!CanProcess())
        {
            // The tree paused again before the deferred reconciliation ran; the retained observations
            // reconcile on the next resume instead.
            return;
        }

        ReconcilePausedGrips();
    }

    /// <summary>
    /// Opens a fresh per-side grip-observation window for this pause (CTRL-002 TR19): absent, invalid, or
    /// unobserved state is never treated as open, so each pause starts from
    /// <see cref="PausedGripState.Unobserved" /> and only valid observations recorded during the pause count.
    /// </summary>
    private void BeginPausedGripObservation()
    {
        _leftPausedGrip = PausedGripState.Unobserved;
        _rightPausedGrip = PausedGripState.Unobserved;
    }

    /// <summary>
    /// Resume reconciliation, exactly once per pause (CTRL-002 UR9, TR20-TR22): a known-open physical grip
    /// cancels an existing pending grab or releases an existing held grab through the ordinary unpaused
    /// release path; a known-held grip preserves either state; unobserved or invalid state acts neither way.
    /// A complete press/release cycle during the pause leaves the observation open but no grab was created,
    /// so reconciliation has nothing to act on and the paused press is never replayed as a new grab.
    /// </summary>
    private void ReconcilePausedGrips()
    {
        ReconcilePausedGrip(LimbSide.Left, _leftPausedGrip);
        ReconcilePausedGrip(LimbSide.Right, _rightPausedGrip);
        BeginPausedGripObservation();
    }

    private void ReconcilePausedGrip(LimbSide side, PausedGripState observed)
    {
        if (observed != PausedGripState.Open)
        {
            return;
        }

        ResolveLogger()?.LogInformation(
            "Resume reconciliation: the {Side} controller grip is open; cancelling a pending or held grab once.",
            side);
        HandleGrabButtonReleased(side, GrabActionName);
    }

    /// <summary>
    /// Records one valid digital grip observation while paused (CTRL-002 TR19). No grab or release action is
    /// issued; the observation is reconciled exactly once on resume.
    /// </summary>
    private void RecordPausedDigitalGrip(LimbSide side, string actionName, bool isHeld)
    {
        if (actionName != GrabActionName)
        {
            return;
        }

        SetPausedGripObservation(side, isHeld ? PausedGripState.Held : PausedGripState.Open);
    }

    /// <summary>
    /// Records one valid analogue grip observation while paused through the same hysteresis thresholds and
    /// latch semantics as the unpaused path (CTRL-002 TR19). Non-finite values are invalid observations and
    /// are ignored rather than treated as open.
    /// </summary>
    private void RecordPausedAnalogueGrip(LimbSide side, string actionName, float value)
    {
        if (actionName != GrabFloatActionName)
        {
            return;
        }

        ref bool pressed = ref GetGrabFloatLatch(side);
        AnalogueGripEdge edge = ClassifyAnalogueGrip(value, pressed);
        if (edge == AnalogueGripEdge.Pressed)
        {
            pressed = true;
            SetPausedGripObservation(side, PausedGripState.Held);
        }
        else if (edge == AnalogueGripEdge.Released)
        {
            pressed = false;
            SetPausedGripObservation(side, PausedGripState.Open);
        }
    }

    private void SetPausedGripObservation(LimbSide side, PausedGripState observation)
    {
        if (side == LimbSide.Left)
        {
            _leftPausedGrip = observation;
        }
        else
        {
            _rightPausedGrip = observation;
        }
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

        // Deferred calls queued from deferred calls run in the same idle flush. Do not route this
        // attempt through EnsureHandsResolved(), which would enqueue another retry when the holder
        // is still unavailable. _Process retries controller binding on later frames when needed.
        _ = TryResolveHands(out _hands);
    }

    private bool TryBindControllers()
    {
        XRManager? xrManager = _xrManager;
        if (xrManager is null || !EnsureHandsResolved())
        {
            return false;
        }

        DisconnectControllers();

        _leftHandController = xrManager.Runtime.LeftHandController;
        _rightHandController = xrManager.Runtime.RightHandController;

        _leftHandController.ActionButtonPressed += OnLeftControllerButtonPressed;
        _leftHandController.ActionButtonReleased += OnLeftControllerButtonReleased;
        _leftHandController.ActionFloatInputChanged += OnLeftControllerFloatChanged;
        _leftHandController.ActionVector2InputChanged += OnLeftStick;
        _rightHandController.ActionButtonPressed += OnRightControllerButtonPressed;
        _rightHandController.ActionButtonReleased += OnRightControllerButtonReleased;
        _rightHandController.ActionFloatInputChanged += OnRightControllerFloatChanged;
        _rightHandController.ActionVector2InputChanged += OnRightStick;
        _isBound = true;
        SetProcess(false);

        return true;
    }

    private void DisconnectControllers()
    {
        Motion?.ClearInput();
        if (_leftHandController is IXRHandController leftHandController)
        {
            leftHandController.ActionButtonPressed -= OnLeftControllerButtonPressed;
            leftHandController.ActionButtonReleased -= OnLeftControllerButtonReleased;
            leftHandController.ActionFloatInputChanged -= OnLeftControllerFloatChanged;
            leftHandController.ActionVector2InputChanged -= OnLeftStick;
            _leftHandController = null;
        }

        if (_rightHandController is IXRHandController rightHandController)
        {
            rightHandController.ActionButtonPressed -= OnRightControllerButtonPressed;
            rightHandController.ActionButtonReleased -= OnRightControllerButtonReleased;
            rightHandController.ActionFloatInputChanged -= OnRightControllerFloatChanged;
            rightHandController.ActionVector2InputChanged -= OnRightStick;
            _rightHandController = null;
        }

        _isBound = false;
        _leftFloatGrabPressed = false;
        _rightFloatGrabPressed = false;
        BeginPausedGripObservation();
        SetProcess(_xrInitialised);

    }

    private void OnXRInitialised(bool succeeded)
    {
        if (!succeeded)
        {
            DisconnectControllers();
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

    private void OnLeftStick(string action, Vector2 value)
    {
        if (action == "primary")
        {
            bool accepted = CanProcess();
            Motion?.RecordControllerInput(true, value, accepted);
            if (accepted)
            {
                Motion?.SetStick(true, value);
            }
        }
    }

    private void OnRightStick(string action, Vector2 value)
    {
        if (action == "primary")
        {
            bool accepted = CanProcess();
            Motion?.RecordControllerInput(false, value, accepted);
            if (accepted)
            {
                Motion?.SetStick(false, value);
            }
        }
    }

    private void OnLeftControllerFloatChanged(string actionName, float value)
    {
        if (!CanProcess())
        {
            RecordPausedAnalogueGrip(LimbSide.Left, actionName, value);
            return;
        }

        HandleGrabFloatChanged(LimbSide.Left, actionName, value);
    }

    private void OnRightControllerFloatChanged(string actionName, float value)
    {
        if (!CanProcess())
        {
            RecordPausedAnalogueGrip(LimbSide.Right, actionName, value);
            return;
        }

        HandleGrabFloatChanged(LimbSide.Right, actionName, value);
    }

    private void OnLeftControllerButtonPressed(string actionName)
    {
        if (!CanProcess())
        {
            RecordPausedDigitalGrip(LimbSide.Left, actionName, isHeld: true);
            return;
        }

        HandleGrabButtonPressed(LimbSide.Left, actionName);
    }

    private void OnLeftControllerButtonReleased(string actionName)
    {
        if (!CanProcess())
        {
            RecordPausedDigitalGrip(LimbSide.Left, actionName, isHeld: false);
            return;
        }

        HandleGrabButtonReleased(LimbSide.Left, actionName);
    }

    private void OnRightControllerButtonPressed(string actionName)
    {
        if (!CanProcess())
        {
            RecordPausedDigitalGrip(LimbSide.Right, actionName, isHeld: true);
            return;
        }

        HandleGrabButtonPressed(LimbSide.Right, actionName);
    }

    private void OnRightControllerButtonReleased(string actionName)
    {
        if (!CanProcess())
        {
            RecordPausedDigitalGrip(LimbSide.Right, actionName, isHeld: false);
            return;
        }

        HandleGrabButtonReleased(LimbSide.Right, actionName);
    }

    private void HandleGrabButtonPressed(LimbSide side, string actionName)
    {
        if (actionName == GrabActionName
            && EnsureHandsResolved()
            && _hands?.TryGetHand(side, out IHand? hand) == true
            && hand is not null)
        {
            _ = hand is IHandGrabLifecycle lifecycle
                ? lifecycle.BeginGrab(HandGrabInputSource.Controller)
                : hand.Grab();
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

    private void OnHandTrackingModeChanged()
    {
        // Reset the analogue/button grab latches so no stale pressed state leaks edges across a mode switch
        // (CTRL-002 TR6): grab ownership is never implicitly transferred to the newly active source.
        _leftFloatGrabPressed = false;
        _rightFloatGrabPressed = false;

        ResolveLogger()?.LogInformation(
            "Committed hand-pose mode changed to {HandTrackingMode}; controller grab latches reset.",
            _xrManager?.HandTrackingMode);
    }

    private void HandleGrabFloatChanged(LimbSide side, string actionName, float value)
    {
        if (actionName != GrabFloatActionName)
        {
            return;
        }

        ref bool pressed = ref GetGrabFloatLatch(side);
        AnalogueGripEdge edge = ClassifyAnalogueGrip(value, pressed);
        if (edge == AnalogueGripEdge.Pressed)
        {
            pressed = true;
            HandleGrabButtonPressed(side, GrabActionName);
        }
        else if (edge == AnalogueGripEdge.Released)
        {
            pressed = false;
            HandleGrabButtonReleased(side, GrabActionName);
        }
    }

    /// <summary>
    /// Classifies one analogue grip value against the unchanged press/release hysteresis thresholds for the
    /// current latch state (CTRL-002 TR5: no new thresholds or hysteresis). Non-finite values classify as no
    /// edge — an invalid observation, never an implicit release.
    /// </summary>
    private AnalogueGripEdge ClassifyAnalogueGrip(float value, bool pressed)
    {
        if (!float.IsFinite(value))
        {
            return AnalogueGripEdge.None;
        }

        float pressThreshold = Mathf.Clamp(GrabFloatPressThreshold, 0.0f, 1.0f);
        float releaseThreshold = Mathf.Min(Mathf.Clamp(GrabFloatReleaseThreshold, 0.0f, 1.0f), pressThreshold);
        return !pressed
            ? (value >= pressThreshold ? AnalogueGripEdge.Pressed : AnalogueGripEdge.None)
            : (value <= releaseThreshold ? AnalogueGripEdge.Released : AnalogueGripEdge.None);
    }

    private ref bool GetGrabFloatLatch(LimbSide side)
        => ref (side == LimbSide.Left ? ref _leftFloatGrabPressed : ref _rightFloatGrabPressed);

    private ILogger<PlayerController>? ResolveLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<PlayerController>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }
}
