using System.Globalization;
using System.Text;
using AlleyCat.Common;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging.Installation;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Testing.Navigation;

public readonly record struct NAV001ManualHarnessSnapshot(
    string PresetKey,
    NAV001ManualContactMode ContactMode,
    NAV001ManualRunState RunState,
    int RunNumber,
    ulong ActorInstanceID,
    Vector3 Destination,
    float YawRadians,
    Vector3 AcceptedDestination,
    float AcceptedYawRadians,
    bool ProductionVectorConvergenceActive,
    bool ContactConfiguredBeforeTreeEntry,
    bool ContactModeVerified,
    bool MarkerVisible,
    bool HasDestination,
    string ProgrammeStage,
    float NormalisedPhase,
    float PositionErrorMetres,
    float YawErrorRadians,
    string ObjectiveResult,
    string HumanPerceptualResult,
    string RecordSource,
    string LastRecordPath,
    bool UnrelatedAIRuntimeRemoved,
    bool ManualDestinationPlacementEnabled,
    string StatusText);

/// <summary>
/// Editor/test-only manual acceptance harness for the accepted NAV-001 Stage 4 executor; never an automated test
/// runner dependency.
/// </summary>
[GlobalClass]
public partial class NAV001Stage4ManualPlaytestHarness : Node
{
    public const string HarnessScenePath = "res://tests/navigation/nav001_stage4_manual_playtest.tscn";
    public const string AuthoredScenePath = "res://assets/testing/navigation/navigation_test.tscn";
    public const string RecordDirectory = "res://temp/nav001-vector-convergence/manual-playtest";
    public const string AutomatedRecordDirectory = $"{RecordDirectory}/automated-tests";
    private const int StartupTimeoutPhysicsFrames = 600;
    private const int NavigationWarmUpPhysicsFrames = 8;
    private const int PostBoundaryPhysicsFrames = 30;
    private const float IdentityTolerance = 0.00001f;
    private const float SyntheticZeroTolerance = 0.00000001f;

    private readonly NAV001Stage4ManualPlaytestSession _session = new();
    private readonly string _sessionID = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    private DirectTransformNavigationPlaytestController? _authoredScene;
    private NPCNavigation? _navigation;
    private CharacterIK? _characterIK;
    private NPCContactLockPlaytestComposition? _contactComposition;
    private Node3D? _actor;
    private Node3D? _targetMarker;
    private OptionButton? _routeSelector;
    private OptionButton? _contactSelector;
    private Button? _runButton;
    private Button? _repeatButton;
    private Button? _resetButton;
    private OptionButton? _humanResultSelector;
    private Label? _statusLabel;
    private bool _transitionInProgress;
    private bool _contactModeVerified;
    private bool _contactConfiguredBeforeTreeEntry;
    private bool _terminalBoundaryCaptured;
    private float _terminalBoundaryPhase = float.NaN;
    private float _maximumPostBoundarySyntheticMovement;
    private int _postBoundaryTicks;
    private float _positionError = float.NaN;
    private float _yawError = float.NaN;
    private string _objectiveResult = "PENDING";
    private NAV001HumanPerceptualResult _humanPerceptualResult;
    private NAV001RecordSource _recordSource = NAV001RecordSource.ManualHarness;
    private string _lastRecordPath = string.Empty;
    private bool _unrelatedAIRuntimeRemoved;

    public NAV001ManualHarnessSnapshot Snapshot => new(
        _session.Preset.Key,
        _session.ContactMode,
        _session.State,
        _session.RunNumber,
        _actor?.GetInstanceId() ?? 0,
        _session.Preset.Destination,
        _session.Preset.YawRadians,
        _navigation?.Destination.Origin ?? new(float.NaN, float.NaN, float.NaN),
        _navigation is null
            ? float.NaN
            : NPCNavigationRoutePlan.YawFromDirection(
                _navigation.Destination.Basis.Orthonormalized() * Vector3.Forward),
        true,
        _contactConfiguredBeforeTreeEntry,
        _contactModeVerified,
        _targetMarker?.Visible ?? false,
        _navigation?.HasDestination ?? false,
        _navigation?.ProgrammeStage.ToString() ?? "Unavailable",
        _navigation?.LastEvaluatedNormalisedPhase ?? float.NaN,
        _positionError,
        _yawError,
        _objectiveResult,
        HumanPerceptualResultText,
        RecordSourceText,
        _lastRecordPath,
        _unrelatedAIRuntimeRemoved,
        _authoredScene?.ManualDestinationPlacementEnabled ?? true,
        _statusLabel?.Text ?? string.Empty);

    internal CharacterIK? GetCurrentCharacterIK() => _characterIK;

    internal NPCContactLockPlaytestComposition? GetCurrentContactComposition() => _contactComposition;

    internal NPCNavigation? GetCurrentNavigation() => _navigation;

    internal DirectTransformNavigationPlaytestController? GetCurrentController() => _authoredScene;

    internal Node3D? GetCurrentActor() => _actor;

    private string HumanPerceptualResultText => _humanPerceptualResult switch
    {
        NAV001HumanPerceptualResult.Unrecorded => "UNRECORDED",
        NAV001HumanPerceptualResult.Improved => "IMPROVED",
        NAV001HumanPerceptualResult.Unchanged => "UNCHANGED",
        NAV001HumanPerceptualResult.Worse => "WORSE",
        _ => throw new InvalidOperationException($"Unknown human result '{_humanPerceptualResult}'."),
    };

    private string RecordSourceText => _recordSource switch
    {
        NAV001RecordSource.ManualHarness => "manual_harness",
        NAV001RecordSource.AutomatedTest => "automated_test",
        _ => throw new InvalidOperationException($"Unknown record source '{_recordSource}'."),
    };

    public override void _Ready()
    {
        ProcessPhysicsPriority = 100;
        BuildTargetMarker();
        BuildUI();
        SetPhysicsProcess(true);
        _ = ResetFreshAsync();
    }

    public override void _PhysicsProcess(double delta)
    {
        _ = delta;
        UpdateTelemetry();
    }

    public void Select(string presetKey, NAV001ManualContactMode contactMode)
    {
        _session.Select(presetKey, contactMode);
        _routeSelector!.Selected = NAV001Stage4DiagnosticPresets.All
            .Select((preset, index) => (preset, index))
            .Single(item => item.preset.Key == presetKey).index;
        _contactSelector!.Selected = (int)contactMode;
        UpdateTargetMarker();
        UpdateStatusText();
    }

    internal void UseAutomatedTestRecordSource()
    {
        if (_session.State is NAV001ManualRunState.Loading or NAV001ManualRunState.Running)
        {
            throw new InvalidOperationException("Cannot change record source while a harness transition or run is active.");
        }

        _recordSource = NAV001RecordSource.AutomatedTest;
        UpdateStatusText();
    }

    public async Task RunFreshAsync()
    {
        if (_transitionInProgress)
        {
            return;
        }
        _transitionInProgress = true;
        SetControlsEnabled(false);
        _session.BeginFresh();
        ResetMetrics();
        try
        {
            await CreateFreshAuthoredSceneAsync(submitRoute: true);
        }
        catch (Exception exception)
        {
            _session.MarkFailed(exception.Message);
            _objectiveResult = "FAIL";
            WriteRunRecord();
        }
        finally
        {
            _transitionInProgress = false;
            SetControlsEnabled(true);
            UpdateStatusText();
        }
    }

    public Task RepeatFreshAsync() => RunFreshAsync();

    public async Task ResetFreshAsync()
    {
        if (_transitionInProgress)
        {
            return;
        }
        _transitionInProgress = true;
        SetControlsEnabled(false);
        _session.Reset();
        ResetMetrics();
        try
        {
            await CreateFreshAuthoredSceneAsync(submitRoute: false);
        }
        catch (Exception exception)
        {
            _session.MarkFailed(exception.Message);
            _objectiveResult = "FAIL";
        }
        finally
        {
            _transitionInProgress = false;
            SetControlsEnabled(true);
            UpdateStatusText();
        }
    }

    private async Task CreateFreshAuthoredSceneAsync(bool submitRoute)
    {
        NavigationPlaytestCameraState? cameraState = _authoredScene?.CaptureCameraState();
        if (_authoredScene is not null)
        {
            RemoveChild(_authoredScene);
            _authoredScene.QueueFree();
            _authoredScene = null;
            _ = await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        PackedScene packed = ResourceLoader.Load<PackedScene>(AuthoredScenePath)
            ?? throw new InvalidOperationException($"Could not load authored navigation scene '{AuthoredScenePath}'.");
        DirectTransformNavigationPlaytestController scene = packed.Instantiate<DirectTransformNavigationPlaytestController>();
        scene.ManualDestinationPlacementEnabled = false;
        InstallAuthoredRoleBeforeTreeEntry(scene);
        RemoveUnrelatedAIRuntimeBeforeTreeEntry(scene);
        NPCContactLockPlaytestComposition composition = scene.RequireNode<NPCContactLockPlaytestComposition>(
            "NPCContactLockPlaytestComposition");
        composition.EnabledForPlaytest = _session.ContactMode == NAV001ManualContactMode.Active;
        composition.SetPhysicsProcess(composition.EnabledForPlaytest);
        if (_session.ContactMode == NAV001ManualContactMode.Neutralised)
        {
            ConfigureNeutralisedContactBeforeTreeEntry(scene, composition);
        }
        _contactConfiguredBeforeTreeEntry = !scene.IsInsideTree();
        AddChild(scene);
        _authoredScene = scene;
        if (cameraState is { } preservedCamera)
        {
            scene.RestoreCameraState(preservedCamera);
        }

        await WaitForRuntimeReadinessAsync(scene, composition);
        for (int frame = 0; frame < NavigationWarmUpPhysicsFrames; frame++)
        {
            _ = await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        if (_actor!.GlobalPosition.Length() > IdentityTolerance
            || Mathf.Abs(NPCNavigationRoutePlan.YawFromDirection(
                _actor.GlobalBasis.Orthonormalized() * Vector3.Forward)) > IdentityTolerance)
        {
            throw new InvalidOperationException("Fresh authored scene did not retain the identity spawn transform.");
        }

        _contactModeVerified = ValidateContactMode(_characterIK!, _navigation!, composition, _session.ContactMode);
        if (!_contactModeVerified)
        {
            throw new InvalidOperationException($"Contact mode '{_session.ContactMode}' was not established before route submission.");
        }

        UpdateTargetMarker();
        if (!submitRoute)
        {
            return;
        }

        _navigation!.CaptureCompleteMovementDiagnostics = true;
        _navigation.CaptureVectorConvergenceDiagnostics = true;
        _session.MarkReady();
        NAV001Stage4DiagnosticPreset preset = _session.Preset;
        scene.BeginDestinationPlacementAt(preset.Destination);
        scene.UpdateDestinationFacingProbe(preset.Destination + preset.Facing);
        scene.CompleteDestinationPlacementAt(preset.Destination + preset.Facing);
        if (!_navigation.HasDestination)
        {
            throw new InvalidOperationException("Exact programmatic destination placement did not submit a route.");
        }
        _session.MarkRunning();
    }

    private async Task WaitForRuntimeReadinessAsync(
        DirectTransformNavigationPlaytestController scene,
        NPCContactLockPlaytestComposition composition)
    {
        Node3D actor = scene.RequireNode<Node3D>("NavigationTestNpc");
        NavigationRegion3D region = scene.RequireNode<NavigationRegion3D>("NavigationRegion3D");
        string detail = "not sampled";
        for (int frame = 0; frame < StartupTimeoutPhysicsFrames; frame++)
        {
            _ = await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            NPCNavigation? navigation = actor.GetNodeOrNull<NPCNavigation>("Navigation");
            CharacterIK? characterIK = actor.GetNodeOrNull<CharacterIK>("CharacterIK");
            Rid map = region.GetNavigationMap();
            bool mapReady = map.IsValid && NavigationServer3D.MapGetIterationId(map) > 0;
            bool contactReady = _session.ContactMode == NAV001ManualContactMode.Neutralised
                ? !composition.IsInstalled && !composition.IsPhysicsProcessing()
                : composition.IsInstalled && composition.IsPhysicsProcessing();
            detail = $"navigation={navigation is not null}; characterIK={characterIK is not null}; "
                + $"map={map.IsValid}/{(map.IsValid ? NavigationServer3D.MapGetIterationId(map) : 0)}; "
                + $"controller={scene.IsPhysicsProcessing()}; contact={contactReady}";
            if (navigation is not null
                && characterIK is not null
                && mapReady
                && scene.IsPhysicsProcessing()
                && contactReady)
            {
                _actor = actor;
                _navigation = navigation;
                _characterIK = characterIK;
                _contactComposition = composition;
                return;
            }
        }
        throw new InvalidOperationException($"Authored navigation scene did not become ready. Last state: {detail}");
    }

    private void RemoveUnrelatedAIRuntimeBeforeTreeEntry(DirectTransformNavigationPlaytestController scene)
    {
        Node actor = scene.RequireNode<Node>("NavigationTestNpc");
        Node authoredMind = actor.RequireNode<Node>("Mind");
        CharacterIK characterIK = actor.RequireNode<CharacterIK>("CharacterIK");
        characterIK.HeadTargetIntentProvider = null;
        characterIK.HeadFallbackIntentProvider = null;
        actor.RemoveChild(authoredMind);
        authoredMind.Free();
        _unrelatedAIRuntimeRemoved = !scene.IsInsideTree() && actor.GetNodeOrNull("Mind") is null;
        if (!_unrelatedAIRuntimeRemoved)
        {
            throw new InvalidOperationException("Could not remove the unrelated AgenticMind runtime before tree entry.");
        }
    }

    private static void InstallAuthoredRoleBeforeTreeEntry(DirectTransformNavigationPlaytestController scene)
    {
        Node actor = scene.RequireNode<Node>("NavigationTestNpc");
        RigRoleTemplateSceneInstaller installer = actor.RequireNode<RigRoleTemplateSceneInstaller>("NPCCharacterInstaller");
        installer.AutoInstallOnReady = false;
        SceneInstallationResult result = installer.Install(new SceneInstallationContext(actor));
        result.ThrowIfFailed();
    }

    private static void ConfigureNeutralisedContactBeforeTreeEntry(
        DirectTransformNavigationPlaytestController scene,
        NPCContactLockPlaytestComposition composition)
    {
        if (scene.IsInsideTree())
        {
            throw new InvalidOperationException("Neutralised contact must be configured before authored scene tree entry.");
        }

        Node3D actor = scene.RequireNode<Node3D>("NavigationTestNpc");
        CharacterIK characterIK = actor.RequireNode<CharacterIK>("CharacterIK");
        NPCNavigation navigation = actor.RequireNode<NPCNavigation>("Navigation");
        Node3D leftTarget = characterIK.LeftFootIKTarget
            ?? throw new InvalidOperationException("Neutralised contact requires the authored left foot IK target.");
        Node3D rightTarget = characterIK.RightFootIKTarget
            ?? throw new InvalidOperationException("Neutralised contact requires the authored right foot IK target.");
        var leftFallback = new AnimationSynchronizedFootTargetProvider
        {
            Name = "LeftAnimationFootFallback",
            FootTarget = leftTarget,
        };
        var rightFallback = new AnimationSynchronizedFootTargetProvider
        {
            Name = "RightAnimationFootFallback",
            FootTarget = rightTarget,
        };
        characterIK.AddChild(leftFallback);
        characterIK.AddChild(rightFallback);
        characterIK.LeftFootTargetIntentProvider = leftFallback;
        characterIK.RightFootTargetIntentProvider = rightFallback;
        characterIK.LeftFootFallbackIntentProvider = null;
        characterIK.RightFootFallbackIntentProvider = null;
        composition.EnabledForPlaytest = false;
        composition.SetPhysicsProcess(false);
        composition.Coordinator?.Teardown();
        navigation.ContactLockCoordinator = null;
    }

    private static bool ValidateContactMode(
        CharacterIK characterIK,
        NPCNavigation navigation,
        NPCContactLockPlaytestComposition composition,
        NAV001ManualContactMode mode)
    {
        bool contactProvidersBound = characterIK.LeftFootTargetIntentProvider is ContactLockFootTargetProvider
            && characterIK.RightFootTargetIntentProvider is ContactLockFootTargetProvider;
        if (mode == NAV001ManualContactMode.Active)
        {
            return composition.EnabledForPlaytest
                && composition.IsPhysicsProcessing()
                && composition.Coordinator is not null
                && ReferenceEquals(navigation.ContactLockCoordinator, composition.Coordinator)
                && contactProvidersBound;
        }

        ContactLockMotionContribution contribution = composition.Coordinator?.GetAuthorisedContribution() ?? default;
        IKTargetIntentProvider? leftProvider = characterIK.LeftFootTargetIntentProvider;
        IKTargetIntentProvider? rightProvider = characterIK.RightFootTargetIntentProvider;
        return !composition.EnabledForPlaytest
            && !composition.IsPhysicsProcessing()
            && !composition.IsInstalled
            && composition.Coordinator is null
            && composition.LeftProvider is null
            && composition.RightProvider is null
            && navigation.ContactLockCoordinator is null
            && !contactProvidersBound
            && leftProvider?.GetType() == typeof(AnimationSynchronizedFootTargetProvider)
            && rightProvider?.GetType() == typeof(AnimationSynchronizedFootTargetProvider)
            && !ReferenceEquals(leftProvider, rightProvider)
            && IsInstanceValid(leftProvider)
            && IsInstanceValid(rightProvider)
            && characterIK.LeftFootFallbackIntentProvider is null
            && characterIK.RightFootFallbackIntentProvider is null
            && !contribution.IsAuthorised
            && contribution.Authorised.Translation.LengthSquared() <= IdentityTolerance * IdentityTolerance
            && Mathf.Abs(contribution.Authorised.YawRadians) <= IdentityTolerance;
    }

    private void UpdateTelemetry()
    {
        if (_navigation is null || _actor is null)
        {
            UpdateStatusText();
            return;
        }

        NAV001Stage4DiagnosticPreset preset = _session.Preset;
        Vector3 difference = _actor.GlobalPosition - preset.Destination;
        _positionError = new Vector2(difference.X, difference.Z).Length();
        float actorYaw = NPCNavigationRoutePlan.YawFromDirection(
            _actor.GlobalBasis.Orthonormalized() * Vector3.Forward);
        _yawError = Mathf.Abs(NPCNavigationRoutePlan.WrapAngle(actorYaw - preset.YawRadians));

        if (_session.State == NAV001ManualRunState.Running)
        {
            // The terminal boundary is observed once the f2i clip has fully evaluated with no authored root
            // movement left to compose; everything after it must be stationary confirmation.
            bool terminalBoundaryReached = _navigation.LastEvaluatedCapability is
                StandingLocomotionCapability.ExitForwardLeft or StandingLocomotionCapability.ExitForwardRight
                && _navigation.LastEvaluatedNormalisedPhase >= 0.99999f
                && _navigation.LastEvaluatedPlayLength > 0f
                && _navigation.LastEvaluatedPlayPosition
                    >= _navigation.LastEvaluatedPlayLength - NPCAnimationProgramme.NumericalConvergenceThreshold
                && _navigation.LastMovementCompositionFrame.AuthoredRootWorldDelta.Length()
                    <= NPCAnimationProgramme.NumericalConvergenceThreshold;
            if (!_terminalBoundaryCaptured && terminalBoundaryReached)
            {
                _terminalBoundaryCaptured = true;
                _terminalBoundaryPhase = _navigation.LastEvaluatedNormalisedPhase;
            }
            if (_terminalBoundaryCaptured)
            {
                _maximumPostBoundarySyntheticMovement = Math.Max(
                    _maximumPostBoundarySyntheticMovement,
                    _navigation.LastMovementCompositionFrame.SyntheticWorldDelta.Length());
                _postBoundaryTicks++;
            }
            if (_terminalBoundaryCaptured && !_navigation.IsNavigationRunning && _postBoundaryTicks >= PostBoundaryPhysicsFrames)
            {
                CompleteRun();
            }
        }
        UpdateStatusText();
    }

    private void CompleteRun()
    {
        bool passed = _positionError <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && _yawError <= NPCAnimationProgramme.NumericalConvergenceThreshold
            && Mathf.Abs(_terminalBoundaryPhase - 1f) <= 0.00001f
            && _maximumPostBoundarySyntheticMovement <= SyntheticZeroTolerance;
        _objectiveResult = passed ? "PASS" : "FAIL";
        _session.MarkCompleted();
        WriteRunRecord();
        _humanResultSelector!.Disabled = false;
    }

    private void WriteRunRecord()
    {
        string recordDirectory = _recordSource == NAV001RecordSource.AutomatedTest
            ? AutomatedRecordDirectory
            : RecordDirectory;
        string directory = ProjectSettings.GlobalizePath(recordDirectory);
        _ = Directory.CreateDirectory(directory);
        string identity = $"session-{_sessionID}-run-{_session.RunNumber:000}-{_session.Preset.Key}-{_session.ContactMode.ToString().ToLowerInvariant()}";
        string path = Path.Combine(directory, $"{identity}.csv");
        _lastRecordPath = path;
        var record = new StringBuilder();
        _ = record.AppendLine("session_id,timestamp_utc,run_id,record_source,route,contact,target_x,target_y,target_z,target_yaw,actor_x,actor_y,actor_z,position_error,yaw_error,terminal_boundary_phase,max_post_boundary_synthetic,detector_events,production_vector_convergence_active,contact_verified,objective_result,human_perceptual_result");
        Vector3 actorPosition = _actor?.GlobalPosition ?? new(float.NaN, float.NaN, float.NaN);
        _ = record.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{_sessionID},{DateTimeOffset.UtcNow:O},{identity},{RecordSourceText},{_session.Preset.Key},{_session.ContactMode},{_session.Preset.Destination.X:R},{_session.Preset.Destination.Y:R},{_session.Preset.Destination.Z:R},{_session.Preset.YawRadians:R},{actorPosition.X:R},{actorPosition.Y:R},{actorPosition.Z:R},{_positionError:R},{_yawError:R},{_terminalBoundaryPhase:R},{_maximumPostBoundarySyntheticMovement:R},N/A,{true},{_contactModeVerified},{_objectiveResult},{HumanPerceptualResultText}"));
        File.WriteAllText(path, record.ToString());
    }

    private void ResetMetrics()
    {
        _contactModeVerified = false;
        _contactConfiguredBeforeTreeEntry = false;
        _terminalBoundaryCaptured = false;
        _terminalBoundaryPhase = float.NaN;
        _maximumPostBoundarySyntheticMovement = 0f;
        _postBoundaryTicks = 0;
        _positionError = float.NaN;
        _yawError = float.NaN;
        _objectiveResult = "PENDING";
        _humanPerceptualResult = NAV001HumanPerceptualResult.Unrecorded;
        _lastRecordPath = string.Empty;
        _unrelatedAIRuntimeRemoved = false;
        if (_humanResultSelector is not null)
        {
            _humanResultSelector.Selected = (int)NAV001HumanPerceptualResult.Unrecorded;
            _humanResultSelector.Disabled = true;
        }
    }

    private void BuildUI()
    {
        var canvas = new CanvasLayer { Layer = 100 };
        AddChild(canvas);
        var panel = new PanelContainer
        {
            OffsetLeft = 12f,
            OffsetTop = 12f,
            OffsetRight = 480f,
            OffsetBottom = 430f,
        };
        canvas.AddChild(panel);
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        panel.AddChild(margin);
        var content = new VBoxContainer();
        margin.AddChild(content);
        content.AddChild(new Label { Text = "NAV-001 Stage 4 — Diagnostic Manual Testing Only" });

        _routeSelector = new OptionButton();
        foreach (NAV001Stage4DiagnosticPreset preset in NAV001Stage4DiagnosticPresets.All)
        {
            _routeSelector.AddItem(preset.DisplayName);
        }
        _routeSelector.ItemSelected += index => ApplyUISelection((int)index, _contactSelector?.Selected ?? 0);
        content.AddChild(_routeSelector);

        _contactSelector = new OptionButton();
        _contactSelector.AddItem("Contact Active");
        _contactSelector.AddItem("Contact Neutralised");
        _contactSelector.ItemSelected += index => ApplyUISelection(_routeSelector?.Selected ?? 0, (int)index);
        content.AddChild(_contactSelector);

        var buttons = new HBoxContainer();
        _runButton = new Button { Text = "Run Fresh" };
        _repeatButton = new Button { Text = "Repeat Fresh" };
        _resetButton = new Button { Text = "Reset Fresh" };
        _runButton.Pressed += () => _ = RunFreshAsync();
        _repeatButton.Pressed += () => _ = RepeatFreshAsync();
        _resetButton.Pressed += () => _ = ResetFreshAsync();
        buttons.AddChild(_runButton);
        buttons.AddChild(_repeatButton);
        buttons.AddChild(_resetButton);
        content.AddChild(buttons);

        content.AddChild(new Label { Text = "Human Perceptual Result — Explicit User Judgement" });
        _humanResultSelector = new OptionButton { Disabled = true };
        _humanResultSelector.AddItem("UNRECORDED");
        _humanResultSelector.AddItem("IMPROVED");
        _humanResultSelector.AddItem("UNCHANGED");
        _humanResultSelector.AddItem("WORSE");
        _humanResultSelector.ItemSelected += index => ApplyHumanPerceptualResult((int)index);
        content.AddChild(_humanResultSelector);

        _statusLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        content.AddChild(_statusLabel);
        UpdateStatusText();
    }

    private void ApplyHumanPerceptualResult(int selectedIndex)
    {
        if (_session.State != NAV001ManualRunState.Completed)
        {
            _humanResultSelector!.Selected = (int)_humanPerceptualResult;
            return;
        }

        _humanPerceptualResult = (NAV001HumanPerceptualResult)selectedIndex;
        WriteRunRecord();
        UpdateStatusText();
    }

    private void ApplyUISelection(int routeIndex, int contactIndex)
    {
        if (_session.State is NAV001ManualRunState.Loading or NAV001ManualRunState.Running)
        {
            return;
        }
        _session.Select(
            NAV001Stage4DiagnosticPresets.All[routeIndex].Key,
            (NAV001ManualContactMode)contactIndex);
        UpdateTargetMarker();
        UpdateStatusText();
    }

    private void SetControlsEnabled(bool enabled)
    {
        _routeSelector!.Disabled = !enabled;
        _contactSelector!.Disabled = !enabled;
        _runButton!.Disabled = !enabled;
        _repeatButton!.Disabled = !enabled;
        _resetButton!.Disabled = !enabled;
        _humanResultSelector!.Disabled = !enabled || _session.State != NAV001ManualRunState.Completed;
    }

    private void UpdateStatusText()
    {
        if (_statusLabel is null)
        {
            return;
        }
        NAV001Stage4DiagnosticPreset preset = _session.Preset;
        float actorYaw = _actor is null
            ? float.NaN
            : NPCNavigationRoutePlan.YawFromDirection(
                _actor.GlobalBasis.Orthonormalized() * Vector3.Forward);
        string failure = string.IsNullOrEmpty(_session.Failure) ? string.Empty : $"\nFailure: {_session.Failure}";
        _statusLabel.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"Target: ({preset.Destination.X:F8}, {preset.Destination.Z:F8})  yaw={preset.YawRadians:F8}\n"
            + $"Actor: ({_actor?.GlobalPosition.X ?? float.NaN:F6}, {_actor?.GlobalPosition.Z ?? float.NaN:F6})  yaw={actorYaw:F7}  errors={_positionError:F7}m/{_yawError:F7}rad\n"
            + $"Stage: {_navigation?.ProgrammeStage.ToString() ?? "Unavailable"}  role={_navigation?.LastEvaluatedRole ?? "N/A"}  phase={_navigation?.LastEvaluatedNormalisedPhase ?? float.NaN:F6}\n"
            + $"Production vector convergence active: {true}  contact={_session.ContactMode}/{_contactModeVerified}\n"
            + $"Run: {_session.RunNumber:000} {_session.State}  completion={!_navigation?.IsNavigationRunning ?? false}\n"
            + $"Objective result (telemetry only): {_objectiveResult}\n"
            + $"Human perceptual result (explicit, never inferred): {HumanPerceptualResultText}\n"
            + $"Record source: {RecordSourceText}\n"
            + $"Boundary phase={_terminalBoundaryPhase:F6}  post-boundary synthetic={_maximumPostBoundarySyntheticMovement:R}\n"
            + $"Detector events: N/A (manual perceptual judgement remains separate)\n"
            + $"Camera: middle-drag pan | right-drag orbit | wheel zoom | left-click route disabled{failure}");
    }

    private void BuildTargetMarker()
    {
        _targetMarker = new Node3D { Name = "ExactTargetFacingMarker" };
        AddChild(_targetMarker);
        var material = new StandardMaterial3D
        {
            AlbedoColor = new Color(1f, 0.25f, 0.05f, 1f),
            EmissionEnabled = true,
            Emission = new Color(1f, 0.08f, 0.01f, 1f),
        };
        var target = new MeshInstance3D
        {
            Mesh = new CylinderMesh { TopRadius = 0.11f, BottomRadius = 0.11f, Height = 0.015f },
            MaterialOverride = material,
        };
        _targetMarker.AddChild(target);
        var facing = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.025f, 0.025f, 0.45f) },
            MaterialOverride = material,
            Position = new Vector3(0f, 0.03f, -0.22f),
        };
        _targetMarker.AddChild(facing);
        UpdateTargetMarker();
    }

    private void UpdateTargetMarker()
    {
        if (_targetMarker is null)
        {
            return;
        }
        NAV001Stage4DiagnosticPreset preset = _session.Preset;
        _targetMarker.GlobalTransform = new(
            Basis.LookingAt(preset.Facing.Normalized(), Vector3.Up),
            preset.Destination + new Vector3(0f, 0.015f, 0f));
        _targetMarker.Visible = true;
    }
}
