using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Common;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Speech.Generation;
using AlleyCat.UI;
using AlleyCat.XR;
using AlleyCat.XR.Mock;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.Tests.SharedModels;

/// <summary>
/// Shutdown evidence probe for CORE-011-UAC-004: three real NPCs run MotionBricks generation and Supertonic synthesis
/// on the real shared models, then the process quits through the production menu-exit or root-window-close path while
/// that work is in flight. Launched as its own Godot process by <c>SharedModelShutdownTests</c>; the real
/// <see cref="Game" /> autoload owns the shutdown and quits the process itself.
/// </summary>
/// <remarks>
/// User arguments: <c>--shutdown-path menu|window|scene</c> selects the exit path (<c>scene</c> leaves the NPC scene
/// through the loading screen's scene change, observes the drain, then quits through the root-window path) and <c>--shutdown-report &lt;file&gt;</c>
/// names the JSON report written when the probe leaves the tree during application quit. The probe never calls
/// <see cref="Game.RequestExit" /> or any shutdown internal directly: the menu path presses the mock XR controller
/// buttons the real <see cref="GameMenu" /> listens to, and the window path delivers the root window's close request.
/// </remarks>
[GlobalClass]
public partial class SharedModelShutdownProbe : Node3D
{
    /// <summary>Printed when the exit path has been triggered.</summary>
    public const string TriggerMarker = "ALLEYCAT_SHUTDOWN_PROBE_TRIGGER";

    /// <summary>Printed after the report is written, when the probe leaves the tree during application quit.</summary>
    public const string ExitMarker = "ALLEYCAT_SHUTDOWN_PROBE_EXIT_TREE";

    /// <summary>Printed on the scene-exit path when the drain has been observed and the quit is requested.</summary>
    public const string QuitMarker = "ALLEYCAT_SHUTDOWN_PROBE_QUIT";

    private const string PathArg = "--shutdown-path";
    private const string ReportArg = "--shutdown-report";
    private const string NpcFile = "res://assets/testing/navigation/navigation_test_npc.tscn";
    private const string Utterance =
        "Good evening. I have a rather long story to tell you about the alley behind the old cinema, " +
        "the cat that lives there, and the night the lights went out across the whole street.";
    private const int SetupFailureExitCode = 3;
    // The configured (empty) main scene: the scene change itself, not the next scene's content, is under test.
    private const string NextScene = "res://assets/scenes/empty.tscn";
    // Hang guard for the scene-exit drain only, never a criterion.
    private static readonly TimeSpan _drainBudget = TimeSpan.FromSeconds(60);
    // Read-only access to the generator's per-consumer handle, captured before the scene exit to observe its release.
    private static readonly System.Reflection.FieldInfo _pipelineField =
        typeof(SupertonicSpeechGenerator).GetField("_pipeline", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(SupertonicSpeechGenerator), "_pipeline");
    // Wait headroom for cold CUDA loads only, never a criterion.
    private static readonly TimeSpan _setupBudget = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan _inFlightBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _baselineWindow = TimeSpan.FromSeconds(2);
    private static readonly Vector3[] _spawnPositions = [new(-2, 0, -3), new(0, 0, -3), new(2, 0, -3)];

    private readonly List<NpcState> _npcs = [];
    private readonly List<long> _frameTicks = new(capacity: 100_000);
    private readonly List<long> _baselineTicks = new(capacity: 100_000);
    private string _path = "";
    private string _reportFile = "";
    private string? _setupError;
    private bool _triggered;
    private bool _baselineSampling;
    private long _triggerTicks;
    private int _motionInFlight;
    private int _motionCallsStarted;
    private int _motionReturnsAfterTrigger;
    private long _lastMotionReturnTicks;
    private int _motionInFlightAtTrigger;
    private int _speechPendingAtTrigger;
    private int _speechRounds;
    private bool _menuOpened;
    private string _menuSelection = "";
    private bool _reported;
    private Node3D? _npcScene;
    private bool _sceneChangeCompleted;
    private string _sceneAfterExit = "";
    private long _drainTicks;
    private int _drainFrameCount;
    private bool _drainSettled;
    private bool _npcSceneFreed;
    private int _motionInFlightAfterDrain;

    private Node3D NpcParent => _npcScene ?? this;

    /// <inheritdoc />
    public override void _Ready()
    {
        // The menu pauses the tree; the probe keeps sampling frames through the pause.
        ProcessMode = ProcessModeEnum.Always;
        string[] args = OS.GetCmdlineUserArgs();
        _path = ArgumentValue(args, PathArg) ?? "";
        _reportFile = ArgumentValue(args, ReportArg) ?? "";
        _ = RunAsync();
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        long now = Stopwatch.GetTimestamp();
        if (_triggered)
        {
            _frameTicks.Add(now);
        }
        else if (_baselineSampling)
        {
            _baselineTicks.Add(now);
        }
    }

    /// <inheritdoc />
    public override void _ExitTree() => WriteReport(Stopwatch.GetTimestamp());

    private async Task RunAsync()
    {
        try
        {
            if (_path is not ("menu" or "window" or "scene") || string.IsNullOrWhiteSpace(_reportFile))
            {
                throw new InvalidOperationException($"Expected {PathArg} menu|window|scene and {ReportArg} <file>.");
            }

            SceneTree tree = GetTree();
            GameMenu? menu = null;
            MockXRHandControllerNode? left = null;
            if (_path == "menu")
            {
                (menu, left) = BindMenu();
            }
            else if (_path == "scene")
            {
                // The NPCs live in their own current scene, so the scene change frees them while the probe, a plain
                // root child from then on, keeps observing. Adding a root child must wait until the root is idle.
                _ = await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                _npcScene = new Node3D { Name = "NpcScene" };
                tree.Root.AddChild(_npcScene);
                tree.CurrentScene = _npcScene;
            }

            CreateFixture();
            // The role template carrying the navigation installs after tree entry, so observe until motion starts.
            tree.NodeAdded += Configure;
            try
            {
                for (int i = 0; i < _spawnPositions.Length; i++)
                {
                    Character.Character actor = GD.Load<PackedScene>(NpcFile).Instantiate<Character.Character>();
                    actor.Name = $"Npc{i + 1}";
                    actor.Id = $"shutdown_probe_npc_{i + 1}";
                    actor.Position = _spawnPositions[i];
                    _npcs.Add(new NpcState(actor));
                    NpcParent.AddChild(actor);
                }

                await StartMotionAsync(tree);
            }
            finally
            {
                tree.NodeAdded -= Configure;
            }

            foreach (NpcState npc in _npcs)
            {
                npc.Generator = Descendants(npc.Actor).OfType<SupertonicSpeechGenerator>().FirstOrDefault()
                    ?? throw new InvalidOperationException($"{npc.Actor.Name} has no SupertonicSpeechGenerator.");
            }

            LoadingScreen? loadingScreen = _path == "scene" ? await PrepareSceneChangeAsync(tree) : null;

            // Frame-gap baseline while the NPCs walk, before any shutdown.
            _baselineSampling = true;
            var baseline = Stopwatch.StartNew();
            while (baseline.Elapsed < _baselineWindow)
            {
                KeepWalking();
                _ = await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            _baselineSampling = false;

            // Trigger only while a MotionBricks call is executing and synthesis is still pending; the NPCs keep
            // walking and speaking until both overlap.
            var wait = Stopwatch.StartNew();
            while (Volatile.Read(ref _motionInFlight) == 0 || _npcs.All(n => n.Speech is null or { IsCompleted: true }))
            {
                if (wait.Elapsed > _inFlightBudget)
                {
                    throw new InvalidOperationException(
                        $"No overlap of in-flight MotionBricks and Supertonic work within {_inFlightBudget.TotalSeconds} s " +
                        $"(motion calls {Volatile.Read(ref _motionCallsStarted)}, speech rounds {_speechRounds}).");
                }

                KeepWalking();
                if (_npcs.All(n => n.Speech is null or { IsCompleted: true }))
                {
                    _speechRounds++;
                    foreach (NpcState npc in _npcs)
                    {
                        npc.Speech = npc.Generator!.Generate(Utterance);
                    }
                }

                _ = await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            if (_path == "menu")
            {
                TriggerMenuExit(menu!, left!);
            }
            else if (_path == "scene")
            {
                await ExitSceneThenQuitAsync(tree, loadingScreen!);
            }
            else
            {
                Trigger(() => RequestWindowClose(tree));
            }
        }
        catch (Exception error)
        {
            _setupError = error.ToString();
            GD.PrintErr($"Shutdown probe setup failed: {error}");
            WriteReport(Stopwatch.GetTimestamp());
            GetTree().Quit(SetupFailureExitCode);
        }
    }

    // Open, select "Exit Game" and confirm in one frame: opening pauses the tree, which stops new motion requests, so
    // spreading the presses over frames would let the in-flight MotionBricks call finish before the exit.
    private void TriggerMenuExit(GameMenu menu, MockXRHandControllerNode left) => Trigger(() =>
    {
        left.TriggerActionButtonPressed(menu.ToggleActionName);
        _menuOpened = menu.IsOpen;
        left.TriggerActionVector2InputChanged(menu.NavigateActionName, new Vector2(0f, -0.8f));
        _menuSelection = menu.SelectedOption.ToString();
        if (!_menuOpened || menu.SelectedOption != GameMenuOption.ExitGame)
        {
            throw new InvalidOperationException($"Menu not ready to exit (open {_menuOpened}, selected {_menuSelection}).");
        }

        left.TriggerActionButtonPressed(menu.ConfirmActionName);
    });

    // What a Window does on an OS close event: notify the tree, then emit close_requested.
    private static void RequestWindowClose(SceneTree tree)
    {
        Window root = tree.Root;
        root.PropagateNotification((int)NotificationWMCloseRequest);
        _ = root.EmitSignal(Window.SignalName.CloseRequested);
    }

    // The game's scene-change path: the global loading screen loads the next scene on a worker thread, then waits for
    // the player's pose recentre before SceneTree.ChangeSceneToPacked replaces, and frees, the current scene. The load
    // is completed here, so the recentre alone performs the scene exit at the chosen moment.
    private async Task<LoadingScreen> PrepareSceneChangeAsync(SceneTree tree)
    {
        LoadingScreen loadingScreen = Game.Instance.RequireNode<LoadingScreen>("XR/SubViewport/LoadingScreen");
        Label message = loadingScreen.RequireNode<Label>("CenterContent/LoadingMessage");
        Error error = loadingScreen.LoadSceneAsync(NextScene);
        Ensure(error == Error.Ok, $"The loading screen refused '{NextScene}' with {error}.");
        var wait = Stopwatch.StartNew();
        while (message.Text != loadingScreen.RecenterInstructionMessage)
        {
            Ensure(wait.Elapsed < _setupBudget, $"The loading screen did not finish loading '{NextScene}'.");
            KeepWalking();
            _ = await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        return loadingScreen;
    }

    // Scene exit during in-flight work, observed until every consumer has released its handle, then a graceful quit
    // through the root-window close path so backend disposal and process exit are observed as for the quit paths.
    private async Task ExitSceneThenQuitAsync(SceneTree tree, LoadingScreen loadingScreen)
    {
        XRManager xr = Game.Instance.GetRequiredService<XRManager>();
        foreach (NpcState npc in _npcs)
        {
            npc.Worker = npc.Navigation!.WorkerCompletion;
            npc.PipelineHandle = _pipelineField.GetValue(npc.Generator);
        }

        int loadCompleted = 0;
        _ = loadingScreen.Connect("LoadCompleted", Callable.From(() => loadCompleted++), (uint)ConnectFlags.OneShot);
        // XRManager re-emits the runtime's recentre as this signal; the loading screen completes the change on it.
        Trigger(() => xr.EmitSignal(XRManager.SignalName.PoseRecentered));
        _sceneChangeCompleted = loadCompleted > 0;
        _sceneAfterExit = tree.CurrentScene?.SceneFilePath ?? "";

        var wait = Stopwatch.StartNew();
        while (!DrainSettled())
        {
            if (wait.Elapsed > _drainBudget)
            {
                break;
            }

            _ = await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        _drainTicks = Stopwatch.GetTimestamp();
        _drainFrameCount = _frameTicks.Count;
        _drainSettled = DrainSettled();
        _npcSceneFreed = _npcScene is not null && !IsInstanceValid(_npcScene);
        _sceneAfterExit = tree.CurrentScene?.SceneFilePath ?? _sceneAfterExit;
        _motionInFlightAfterDrain = Volatile.Read(ref _motionInFlight);
        foreach (NpcState npc in _npcs)
        {
            npc.GenerationsAfterDrain = Volatile.Read(ref npc.Generations);
            npc.WorkerAtDrain = npc.Worker?.Status.ToString();
            npc.PipelineDisposedAtDrain = npc.PipelineHandle is not null
                && (bool)npc.PipelineHandle.GetType().GetProperty("IsDisposed")!.GetValue(npc.PipelineHandle)!;
            npc.SpeechAtDrain = npc.Speech?.Status.ToString();
        }

        GD.Print(QuitMarker);
        RequestWindowClose(tree);
    }

    private bool DrainSettled() =>
        _npcScene is not null && !IsInstanceValid(_npcScene)
        && Volatile.Read(ref _motionInFlight) == 0
        && _npcs.All(n => n.Worker is null or { IsCompleted: true } && n.Speech is null or { IsCompleted: true });

    private void Trigger(Action exitPath)
    {
        _motionInFlightAtTrigger = Volatile.Read(ref _motionInFlight);
        _speechPendingAtTrigger = _npcs.Count(n => n.Speech is { IsCompleted: false });
        foreach (NpcState npc in _npcs)
        {
            npc.GenerationsAtTrigger = Volatile.Read(ref npc.Generations);
        }

        _triggerTicks = Stopwatch.GetTimestamp();
        Volatile.Write(ref _triggered, true);
        GD.Print(TriggerMarker);
        exitPath();
    }

    // Replicates XRManager's integration-context runtime install, which global startup bypass skips for test scenes,
    // so the real GameMenu in the global UI overlay binds to the mock controllers.
    private static (GameMenu Menu, MockXRHandControllerNode Left) BindMenu()
    {
        XRManager xr = Game.Instance.GetRequiredService<XRManager>();
        var runtime = (MockXRRuntimeNode)xr.MockRuntimeScene.Instantiate();
        xr.AddChild(runtime);
        _ = runtime.Initialise(xr.RequireNode<SubViewport>("SubViewport"), xr.MaximumRefreshRate);
        xr.Runtime = runtime;
        _ = xr.EmitSignal(XRManager.SignalName.Initialised, true);
        UIOverlay overlay = xr.RequireNode<UIOverlay>("SubViewport/UIOverlay");
        GameMenu menu = overlay.FindWidget<GameMenu>()
            ?? throw new InvalidOperationException("The global UI overlay has no GameMenu.");
        return (menu, (MockXRHandControllerNode)runtime.LeftHandController);
    }

    private void Configure(Node node)
    {
        if (node is not MotionBricksNavigation navigation)
        {
            return;
        }

        NpcState? owner = _npcs.FirstOrDefault(n => n.Actor.IsAncestorOf(navigation));
        if (owner is null)
        {
            return;
        }

        owner.Navigation = navigation;
        // Passive observers captured before worker start; neither replaces the loading or inference path.
        navigation.SessionDecorator = session => new ObservedSession(session, this);
        navigation.ReturnedMotion = result =>
        {
            if (result.Error is null && result.Poses is { Length: > 0 })
            {
                _ = Interlocked.Increment(ref owner.Generations);
            }
        };
    }

    private async Task StartMotionAsync(SceneTree tree)
    {
        var wait = Stopwatch.StartNew();
        while (_npcs.Any(n => n.Navigation is null || n.Navigation.Phase == "loading"))
        {
            Ensure(wait.Elapsed < _setupBudget, "MotionBricks navigation did not initialise.");
            _ = await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        }

        // Let the navigation map synchronise before routing, as the shared-model loading regression does.
        for (int frame = 0; frame < 40; frame++)
        {
            _ = await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        }

        foreach (NpcState npc in _npcs)
        {
            Ensure(npc.Navigation!.Phase != "failed", $"{npc.Actor.Name} MotionBricks failed: {npc.Navigation.Failure}");
            Transform3D goal = npc.Actor.GlobalTransform;
            goal.Origin += new Vector3(0, 0, 5f);
            int result = npc.Navigation.SelectDestination(goal, []);
            Ensure(result == 0, $"{npc.Actor.Name} refused its destination with result {result}.");
        }

        while (_npcs.Any(n => Volatile.Read(ref n.Generations) == 0))
        {
            Ensure(wait.Elapsed < _setupBudget, "Not every NPC completed a MotionBricks generation.");
            _ = await ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
        }
    }

    // Walks each NPC back and forth along its lane so MotionBricks keeps generating.
    private void KeepWalking()
    {
        for (int i = 0; i < _npcs.Count; i++)
        {
            NpcState npc = _npcs[i];
            if (npc.Navigation is not { HasDestination: false } navigation || navigation.Phase == "failed")
            {
                continue;
            }

            npc.Outbound = !npc.Outbound;
            Transform3D goal = npc.Actor.GlobalTransform;
            goal.Origin = _spawnPositions[i] + (npc.Outbound ? new Vector3(0, 0, 5f) : Vector3.Zero);
            _ = navigation.SelectDestination(goal, []);
        }
    }

    private void CreateFixture()
    {
        var ground = new StaticBody3D { Name = "Ground" };
        ground.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(20, 0.1f, 20) },
            Position = new Vector3(0, -0.05f, 0),
        });
        NpcParent.AddChild(ground);
        var mesh = new NavigationMesh
        {
            Vertices = [new(-9.8f, 0, -9.8f), new(9.8f, 0, -9.8f), new(9.8f, 0, 9.8f), new(-9.8f, 0, 9.8f)],
        };
        mesh.AddPolygon([0, 1, 2, 3]);
        NpcParent.AddChild(new NavigationRegion3D { Name = "NavigationRegion3D", NavigationMesh = mesh });
    }

    private void WriteReport(long exitTicks)
    {
        if (_reported || string.IsNullOrWhiteSpace(_reportFile))
        {
            return;
        }

        _reported = true;
        var gaps = new List<double>();
        long previous = _triggerTicks;
        if (_triggered)
        {
            foreach (long tick in _frameTicks.Append(exitTicks))
            {
                gaps.Add(Stopwatch.GetElapsedTime(previous, tick).TotalMilliseconds);
                previous = tick;
            }
        }

        var report = new
        {
            path = _path,
            setupError = _setupError,
            triggered = _triggered,
            menuOpened = _menuOpened,
            menuSelection = _menuSelection,
            motionInFlightAtTrigger = _motionInFlightAtTrigger,
            speechPendingAtTrigger = _speechPendingAtTrigger,
            speechRounds = _speechRounds,
            motionCallsStarted = Volatile.Read(ref _motionCallsStarted),
            motionReturnsAfterTrigger = Volatile.Read(ref _motionReturnsAfterTrigger),
            lastMotionReturnAfterTriggerMs = _triggered && Volatile.Read(ref _lastMotionReturnTicks) > _triggerTicks
                ? Stopwatch.GetElapsedTime(_triggerTicks, Volatile.Read(ref _lastMotionReturnTicks)).TotalMilliseconds
                : (double?)null,
            exitAfterTriggerMs = _triggered ? Stopwatch.GetElapsedTime(_triggerTicks, exitTicks).TotalMilliseconds : (double?)null,
            framesAfterTrigger = _frameTicks.Count,
            maxFrameGapAfterTriggerMs = gaps.Count > 0 ? gaps.Max() : (double?)null,
            baselineFrames = _baselineTicks.Count,
            baselineMaxFrameGapMs = MaxGap(_baselineTicks),
            sceneExit = _path != "scene" ? null : new
            {
                sceneChangeCompleted = _sceneChangeCompleted,
                sceneAfterExit = _sceneAfterExit,
                npcSceneFreed = _npcSceneFreed,
                drainSettled = _drainSettled,
                drainMs = _drainTicks > _triggerTicks ? Stopwatch.GetElapsedTime(_triggerTicks, _drainTicks).TotalMilliseconds : (double?)null,
                framesDuringDrain = _drainFrameCount,
                maxFrameGapDuringDrainMs = MaxGap([_triggerTicks, .. _frameTicks.Take(_drainFrameCount), _drainTicks]),
                motionInFlightAfterDrain = _motionInFlightAfterDrain,
            },
            npcs = _npcs.Select(n => new
            {
                name = n.Name,
                generationsAtTrigger = n.GenerationsAtTrigger,
                generationsAfterDrain = n.GenerationsAfterDrain,
                generationsTotal = Volatile.Read(ref n.Generations),
                workerAtDrain = n.WorkerAtDrain,
                pipelineDisposedAtDrain = n.PipelineDisposedAtDrain,
                speechAtDrain = n.SpeechAtDrain,
                speechAtExit = n.Speech?.Status.ToString(),
                speechFault = n.Speech?.Exception?.InnerException?.ToString(),
                speechBytes = n.Speech is { IsCompletedSuccessfully: true } done ? done.Result.Length : 0,
            }),
        };
        string directory = Path.GetDirectoryName(_reportFile)!;
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(_reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        GD.Print(ExitMarker);
    }

    private static double? MaxGap(List<long> ticks)
    {
        double? max = null;
        for (int i = 1; i < ticks.Count; i++)
        {
            double gap = Stopwatch.GetElapsedTime(ticks[i - 1], ticks[i]).TotalMilliseconds;
            max = max is null || gap > max ? gap : max;
        }

        return max;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private void OnMotionCallStarted()
    {
        _ = Interlocked.Increment(ref _motionInFlight);
        _ = Interlocked.Increment(ref _motionCallsStarted);
    }

    private void OnMotionCallReturned()
    {
        _ = Interlocked.Decrement(ref _motionInFlight);
        if (Volatile.Read(ref _triggered))
        {
            _ = Interlocked.Increment(ref _motionReturnsAfterTrigger);
            _ = Interlocked.Exchange(ref _lastMotionReturnTicks, Stopwatch.GetTimestamp());
        }
    }

    // Passive call counter around the production inference session; forwards every call and the disposal unchanged.
    private sealed class ObservedSession(SomaInferenceWorker.ISession inner, SharedModelShutdownProbe probe)
        : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            probe.OnMotionCallStarted();
            try
            {
                return inner.Generate(context, target);
            }
            finally
            {
                probe.OnMotionCallReturned();
            }
        }

        public Transform3D[][] GeneratePresented(Transform3D[][] context, Transform3D[][] target, Transform3D[] start)
        {
            probe.OnMotionCallStarted();
            try
            {
                return inner.GeneratePresented(context, target, start);
            }
            finally
            {
                probe.OnMotionCallReturned();
            }
        }

        public void Dispose() => inner.Dispose();
    }

    private sealed class NpcState(Character.Character actor)
    {
        public Character.Character Actor { get; } = actor;
        // Cached: the actor is freed on the scene-exit path before the report is written.
        public string Name { get; } = actor.Name;
        public Task? Worker
        {
            get; set;
        }
        public object? PipelineHandle
        {
            get; set;
        }
        public int GenerationsAfterDrain
        {
            get; set;
        }
        public string? WorkerAtDrain
        {
            get; set;
        }
        public bool PipelineDisposedAtDrain
        {
            get; set;
        }
        public string? SpeechAtDrain
        {
            get; set;
        }
        public MotionBricksNavigation? Navigation
        {
            get; set;
        }
        public Task<byte[]>? Speech
        {
            get; set;
        }
        public SupertonicSpeechGenerator? Generator
        {
            get; set;
        }
        // The initial destination is the outbound leg.
        public bool Outbound { get; set; } = true;
        public int Generations;
        public int GenerationsAtTrigger
        {
            get; set;
        }
    }
}
