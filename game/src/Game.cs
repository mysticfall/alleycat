using AlleyCat.Common;
using AlleyCat.Testing;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;

namespace AlleyCat;

/// <summary>
/// Represents the main entry point for the game logic in the AlleyCat namespace.
/// </summary>
[GlobalClass]
public partial class Game : Node
{
    /// <summary>
    /// Scene path loaded after splash and XR startup complete.
    /// </summary>
    [Export(PropertyHint.File, "*.tscn")]
    public string StartScenePath { get; set; } = string.Empty;

    /// <summary>
    /// Splash scene instantiated during startup when splash is enabled.
    /// </summary>
    [Export]
    public PackedScene SplashScreenScene { get; set; } = null!;

    private readonly TaskCompletionSource<bool> _xrInitialisationCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private XRManager? _xrManager;
    private SubViewport? _uiRoot;
    private SplashScreen? _splashScreen;
    private LoadingScreen? _loadingScreen;
    private Callable? _loadCompletedCallable;

    /// <inheritdoc />
    public override void _EnterTree()
    {
        if (RuntimeContext.IsIntegrationTest())
        {
            return;
        }

        _xrManager = this.RequireNode<XRManager>("XR");
        _xrManager.Initialised += OnXRInitialised;
    }

    /// <summary>
    /// Checks if the "--skip-splash" command-line argument was provided.
    /// </summary>
    /// <returns>True if the splash screen should be skipped.</returns>
    private static bool ShouldSkipSplashScreen() =>
        OS.GetCmdlineArgs().Contains("--skip-splash");

    /// <inheritdoc />
    public override void _Ready()
    {
        if (RuntimeContext.IsIntegrationTest())
        {
            return;
        }

        _uiRoot = this.RequireNode<SubViewport>("XR/SubViewport");
        _loadingScreen = this.RequireNode<LoadingScreen>("XR/SubViewport/LoadingScreen");

        if (!ShouldSkipSplashScreen())
        {
            Node splashNode = SplashScreenScene.Instantiate()
                ?? throw new InvalidOperationException("Splash scene is not configured on Game.");

            _splashScreen = splashNode as SplashScreen
                ?? throw new InvalidOperationException($"Splash scene root '{splashNode.GetType().FullName}' must be a SplashScreen.");

            _uiRoot.AddChild(_splashScreen);
        }

        _ = RunStartupFlowAsync();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xrManager is not null)
        {
            _xrManager.Initialised -= OnXRInitialised;
            _xrManager = null;
        }
    }

    private async Task RunStartupFlowAsync()
    {
        if (_splashScreen is not null)
        {
            _ = await ToSignal(_splashScreen, SplashScreen.SignalName.SplashFinished);
        }

        bool xrInitialised = await _xrInitialisationCompletionSource.Task;
        if (!xrInitialised)
        {
            GD.PushError("XR initialisation failed. Quitting the game.");
            QuitGame(1);
            return;
        }

        _loadingScreen!.Show();

        if (_splashScreen is not null)
        {
            Node? splashParent = _splashScreen.GetParent();
            splashParent?.RemoveChild(_splashScreen);
            _splashScreen.QueueFree();
            _splashScreen = null;
        }

        Callable loadCompletedCallable = _loadCompletedCallable ??= Callable.From(OnLoadCompleted);
        if (!_loadingScreen.IsConnected("LoadCompleted", loadCompletedCallable))
        {
            _ = _loadingScreen.Connect("LoadCompleted", loadCompletedCallable);
        }

        Error loadStartError = _loadingScreen.LoadSceneAsync(StartScenePath);
        if (loadStartError != Error.Ok)
        {
            GD.PushError($"Failed to start loading start scene '{StartScenePath}' with error '{loadStartError}'. Quitting the game.");
            QuitGame(1);
        }
    }

    /// <summary>
    /// Requests game shutdown with the supplied exit code.
    /// </summary>
    /// <param name="exitCode">Process exit code to return to the host.</param>
    protected virtual void QuitGame(int exitCode)
        => GetTree().Quit(exitCode);

    private void OnXRInitialised(bool succeeded)
        => _xrInitialisationCompletionSource.TrySetResult(succeeded);

    private void OnLoadCompleted()
    {
        if (_loadingScreen is null)
        {
            return;
        }

        _loadingScreen.Hide();
    }
}
