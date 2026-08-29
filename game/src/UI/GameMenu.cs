using AlleyCat.Common;
using AlleyCat.Core.Logging;
using AlleyCat.Rigging;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.Logging;
using UIControl = Godot.Control;

namespace AlleyCat.UI;

/// <summary>
/// Modal main-menu widget hosted in the global UI overlay and driven by XR controller input.
/// </summary>
/// <remarks>
/// <para>
/// The left controller's <c>menu_button</c> toggles the menu. While open, either controller's
/// <c>primary</c> thumbstick navigates with discrete, gesture-armed steps that wrap around at both
/// ends of the option list, and either controller's <c>trigger_click</c> confirms the selected
/// option. The widget deliberately avoids Godot <c>ui_*</c> focus navigation because it is driven
/// by raw XR action events.
/// </para>
/// <para>
/// Opening the menu pauses the <see cref="SceneTree" /> and closing it unpauses the tree, so every
/// pausable gameplay system stops while the menu is modal. The widget itself processes with
/// <see cref="Node.ProcessModeEnum.Always" /> and the XR runtime scene roots do the same, which keeps
/// controller events flowing to this widget and head/hand tracking alive during the pause.
/// </para>
/// <para>
/// Binding is late: the widget subscribes to <see cref="XRManager.Initialised" /> because it enters the
/// scene tree before the XR runtime selection completes, and it disconnects fully in
/// <see cref="_ExitTree" />.
/// </para>
/// </remarks>
[GlobalClass]
public partial class GameMenu : UIControl, IUIWidget
{
    private static readonly Color _selectedOptionModulate = Colors.White;

    private static readonly Color _unselectedOptionModulate = new(0.55f, 0.55f, 0.55f);

    private readonly Vector2[] _primaryAxes = new Vector2[2];

    private XRManager? _xrManager;

    private IXRHandController? _leftHandController;

    private IXRHandController? _rightHandController;

    private ILogger<GameMenu>? _logger;

    private Button[] _optionButtons = [];

    private bool _navigationArmed = true;

    /// <summary>
    /// Indicates whether the menu is currently shown and the scene tree is paused for it.
    /// </summary>
    public bool IsOpen
    {
        get;
        private set;
    }

    /// <summary>
    /// Option currently highlighted for confirmation.
    /// </summary>
    public GameMenuOption SelectedOption
    {
        get;
        private set;
    } = GameMenuOption.Resume;

    /// <summary>
    /// Controller hand whose menu button toggles this menu.
    /// </summary>
    [ExportGroup("XR Input")]
    [Export]
    public LimbSide MenuHand
    {
        get;
        set;
    } = LimbSide.Left;

    /// <summary>
    /// XR button action that toggles the menu open and closed.
    /// </summary>
    [Export]
    public StringName ToggleActionName
    {
        get;
        set;
    } = new("menu_button");

    /// <summary>
    /// XR vector2 action used for discrete option navigation while the menu is open.
    /// </summary>
    [Export]
    public StringName NavigateActionName
    {
        get;
        set;
    } = new("primary");

    /// <summary>
    /// XR button action that confirms the selected option while the menu is open.
    /// </summary>
    [Export]
    public StringName ConfirmActionName
    {
        get;
        set;
    } = new("trigger_click");

    /// <summary>
    /// Thumbstick magnitude below which an axis counts as neutral for navigation arming.
    /// </summary>
    [ExportGroup("Navigation")]
    [Export(PropertyHint.Range, "0.1,0.95,0.01")]
    public float NavigationDeadZone
    {
        get;
        set;
    } = 0.6f;

    /// <inheritdoc />
    public override void _Ready()
    {
        // The menu owns the pause it creates, so it must keep processing (and keep hearing XR
        // controller events) while the rest of the tree is paused.
        ProcessMode = ProcessModeEnum.Always;

        IsOpen = false;
        Hide();
        BindOptionButtons();
        ApplySelectionVisuals();

        if (!TryResolveXRManager(out XRManager? xrManager) || xrManager is null)
        {
            ResolveLogger()?.LogWarning(
                "Game menu could not resolve the XR manager; the widget stays dormant until it is re-added under an initialised game hierarchy.");
            return;
        }

        _xrManager = xrManager;
        xrManager.Initialised += OnXRInitialised;

        if (xrManager.InitialisationAttempted)
        {
            if (!xrManager.InitialisationSucceeded)
            {
                ResolveLogger()?.LogWarning("Game menu skipped XR controller binding because XR initialisation failed.");
                return;
            }

            _ = TryBindControllers();
        }
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xrManager is XRManager xrManager)
        {
            xrManager.Initialised -= OnXRInitialised;
        }

        DisconnectControllers();

        // Teardown must never leave the tree paused when the menu node disappears while it still
        // owns the pause; a stuck-paused tree would freeze test runners and editor sessions.
        // Descendants exit the tree before their Game ancestor, so the singleton is still live here.
        if (IsOpen && Game.Instance.Paused)
        {
            Game.Instance.Paused = false;
        }

        IsOpen = false;
    }

    private void BindOptionButtons()
    {
        Button resumeButton = this.RequireNode<Button>("Center/Panel/Options/ResumeOption");
        Button exitButton = this.RequireNode<Button>("Center/Panel/Options/ExitOption");
        _optionButtons = [resumeButton, exitButton];
    }

    private static bool TryResolveXRManager(out XRManager? xrManager)
    {
        try
        {
            xrManager = Game.Instance.GetService<XRManager>();
            return xrManager is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            xrManager = null;
            return false;
        }
    }

    private bool TryBindControllers()
    {
        XRManager? xrManager = _xrManager;
        if (xrManager?.Runtime is not IXRRuntime runtime)
        {
            return false;
        }

        DisconnectControllers();

        _leftHandController = runtime.LeftHandController;
        _rightHandController = runtime.RightHandController;

        _leftHandController.ActionButtonPressed += OnLeftControllerButtonPressed;
        _leftHandController.ActionVector2InputChanged += OnLeftControllerVector2Changed;
        _rightHandController.ActionButtonPressed += OnRightControllerButtonPressed;
        _rightHandController.ActionVector2InputChanged += OnRightControllerVector2Changed;

        ResetTrackedControllerInput();
        ResolveLogger()?.LogDebug("Game menu bound to XR hand controllers.");
        return true;
    }

    private void DisconnectControllers()
    {
        if (_leftHandController is IXRHandController leftHandController)
        {
            leftHandController.ActionButtonPressed -= OnLeftControllerButtonPressed;
            leftHandController.ActionVector2InputChanged -= OnLeftControllerVector2Changed;
            _leftHandController = null;
        }

        if (_rightHandController is IXRHandController rightHandController)
        {
            rightHandController.ActionButtonPressed -= OnRightControllerButtonPressed;
            rightHandController.ActionVector2InputChanged -= OnRightControllerVector2Changed;
            _rightHandController = null;
        }
    }

    private void ResetTrackedControllerInput()
    {
        _primaryAxes[SideIndex(LimbSide.Left)] = Vector2.Zero;
        _primaryAxes[SideIndex(LimbSide.Right)] = Vector2.Zero;
        _navigationArmed = true;
    }

    private void OnXRInitialised(bool succeeded)
    {
        if (!succeeded)
        {
            ResolveLogger()?.LogWarning("Game menu skipped XR controller binding because XR initialisation failed.");
            return;
        }

        _ = TryBindControllers();
    }

    private void OnLeftControllerButtonPressed(string actionName)
        => HandleButtonPressed(LimbSide.Left, actionName);

    private void OnRightControllerButtonPressed(string actionName)
        => HandleButtonPressed(LimbSide.Right, actionName);

    private void OnLeftControllerVector2Changed(string actionName, Vector2 value)
        => HandleVector2InputChanged(LimbSide.Left, actionName, value);

    private void OnRightControllerVector2Changed(string actionName, Vector2 value)
        => HandleVector2InputChanged(LimbSide.Right, actionName, value);

    private void HandleButtonPressed(LimbSide side, string actionName)
    {
        if (actionName == ConfirmActionName)
        {
            if (IsOpen)
            {
                ConfirmSelection();
            }

            return;
        }

        if (actionName == ToggleActionName && side == MenuHand)
        {
            if (IsOpen)
            {
                CloseMenu();
            }
            else
            {
                OpenMenu();
            }
        }
    }

    private void HandleVector2InputChanged(LimbSide side, string actionName, Vector2 value)
    {
        if (actionName != NavigateActionName)
        {
            return;
        }

        _primaryAxes[SideIndex(side)] = value;

        if (!IsOpen)
        {
            return;
        }

        if (IsAxisInDeadZone(value))
        {
            _navigationArmed = true;
            return;
        }

        if (!_navigationArmed || Mathf.Abs(value.Y) < NavigationDeadZone)
        {
            return;
        }

        // OpenXR vector2 actions report +Y as up and -Y as down, so an upward push moves the
        // selection toward the previous option and a downward push toward the next option.
        NavigateSelection(value.Y > 0f ? -1 : 1);
    }

    private void NavigateSelection(int direction)
    {
        _navigationArmed = false;

        int optionCount = _optionButtons.Length;
        if (optionCount <= 0)
        {
            return;
        }

        // Wrap in both directions so stepping past either end cycles through the options.
        int currentIndex = (int)SelectedOption;
        int newIndex = (((currentIndex + direction) % optionCount) + optionCount) % optionCount;
        if (newIndex == currentIndex)
        {
            return;
        }

        SelectedOption = (GameMenuOption)newIndex;
        ApplySelectionVisuals();
        ResolveLogger()?.LogInformation("Game menu selection moved to {SelectedOption}.", SelectedOption);
    }

    private void OpenMenu()
    {
        if (IsOpen)
        {
            return;
        }

        IsOpen = true;
        SelectedOption = GameMenuOption.Resume;

        // A thumbstick already deflected for locomotion when the menu opens must return to neutral
        // before its first navigation step registers.
        _navigationArmed = ArePrimaryAxesNeutral();

        ApplySelectionVisuals();
        Show();
        Game.Instance.Paused = true;
        ResolveLogger()?.LogInformation(
            "Game menu opened with {SelectedOption} selected; the scene tree is paused.", SelectedOption);
    }

    private void CloseMenu()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        Hide();

        // The unpause is deferred to the end of the frame so the input edge that closed the menu
        // (for example the trigger press that confirmed Resume) is still observed as paused by
        // gameplay consumers whose controller subscriptions were registered after this widget's.
        // Unpausing synchronously would let that same press edge act on the unpaused world, starting
        // a voice recording through the record-hand trigger.
        //
        // The callable deliberately captures the SceneTree rather than the Game node: the tree has
        // process lifetime, so the deferred flush can never touch a freed node, whereas the Game
        // node (or a re-resolved Game.Instance) could already be freed between this close and the
        // end-of-frame flush. Game.Paused delegates to this same tree state.
        SceneTree tree = GetTree();
        Callable.From(() => tree.Paused = false).CallDeferred();
        ResolveLogger()?.LogInformation("Game menu closed; the scene tree unpauses at the end of the frame.");
    }

    private void ConfirmSelection()
    {
        switch (SelectedOption)
        {
            case GameMenuOption.Resume:
                ResolveLogger()?.LogInformation("Game menu 'Resume' confirmed; closing the menu.");
                CloseMenu();
                break;
            case GameMenuOption.ExitGame:
                ResolveLogger()?.LogInformation(
                    "Game menu 'Exit Game' confirmed; requesting game exit with exit code {ExitCode}.", 0);
                Game.Instance.RequestExit(0);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(SelectedOption), SelectedOption, null);
        }
    }

    private void ApplySelectionVisuals()
    {
        int selectedIndex = (int)SelectedOption;
        for (int index = 0; index < _optionButtons.Length; index++)
        {
            _optionButtons[index].Modulate = index == selectedIndex
                ? _selectedOptionModulate
                : _unselectedOptionModulate;
        }
    }

    private bool ArePrimaryAxesNeutral()
        => IsAxisInDeadZone(_primaryAxes[SideIndex(LimbSide.Left)])
            && IsAxisInDeadZone(_primaryAxes[SideIndex(LimbSide.Right)]);

    private bool IsAxisInDeadZone(Vector2 axis)
        => axis.Length() < NavigationDeadZone;

    private ILogger<GameMenu>? ResolveLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<GameMenu>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    private static int SideIndex(LimbSide side)
        => side == LimbSide.Left ? 0 : 1;
}
