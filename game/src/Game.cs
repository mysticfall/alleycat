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
    public string StartScene { get; set; } = string.Empty;

    private readonly TaskCompletionSource<bool> _xrInitialisationCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private XRManager? _xrManager;
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

    /// <inheritdoc />
    public override void _Ready()
    {
        if (RuntimeContext.IsIntegrationTest())
        {
            return;
        }

        _splashScreen = this.RequireNode<SplashScreen>("XR/SubViewport/SplashScreen");
        _loadingScreen = this.RequireNode<LoadingScreen>("XR/SubViewport/LoadingScreen");

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
        _ = await ToSignal(_splashScreen!, "SplashFinished");

        bool xrInitialised = await _xrInitialisationCompletionSource.Task;
        if (!xrInitialised)
        {
            GD.PushError("XR initialisation failed. Quitting the game.");
            QuitGame(1);
            return;
        }

        _loadingScreen!.Show();

        Node? splashParent = _splashScreen!.GetParent();
        splashParent?.RemoveChild(_splashScreen);
        _splashScreen.QueueFree();

        Callable loadCompletedCallable = _loadCompletedCallable ??= Callable.From(OnLoadCompleted);
        if (!_loadingScreen.IsConnected("LoadCompleted", loadCompletedCallable))
        {
            _ = _loadingScreen.Connect("LoadCompleted", loadCompletedCallable);
        }

        Error loadStartError = _loadingScreen.LoadSceneAsync(StartScene);
        if (loadStartError != Error.Ok)
        {
            GD.PushError($"Failed to start loading start scene '{StartScene}' with error '{loadStartError}'. Quitting the game.");
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
