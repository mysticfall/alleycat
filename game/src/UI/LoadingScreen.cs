using AlleyCat.Common;
using AlleyCat.XR;
using Godot;
using Array = Godot.Collections.Array;
using UIControl = Godot.Control;

namespace AlleyCat.UI;

/// <summary>
/// Displays loading progress while asynchronously preparing a scene transition.
/// </summary>
[GlobalClass]
public partial class LoadingScreen : UIControl
{
    private readonly Array _threadedProgress = [0.0f];

    private Label? _loadingMessage;
    private ProgressBar? _progressBar;
    private SceneTree? _sceneTree;
    private XRManager? _xrManager;
    private PackedScene? _loadedScene;
    private string? _pendingScenePath;
    private bool _isLoading;
    private bool _isWaitingForPoseRecenter;

    /// <summary>
    /// Message shown above the loading progress indicator.
    /// </summary>
    [Export]
    public string LoadingMessage { get; set; } = "Loading…";

    /// <summary>
    /// Message shown once scene loading completes and the player must recentre before continuing.
    /// </summary>
    [Export]
    public string RecenterInstructionMessage { get; set; } = "Stand up straight and recentre your headset to continue.";

    /// <summary>
    /// Path to the XR manager that provides the pose-recenter signal.
    /// </summary>
    [Export]
    public NodePath XRManagerPath { get; set; } = new("../..");

    /// <summary>
    /// Emitted when scene loading finishes and the scene has been changed successfully.
    /// </summary>
    [Signal]
    private delegate void LoadCompletedEventHandler();

    /// <inheritdoc />
    public override void _Ready()
    {
        _sceneTree = GetTree();
        EnsureUiNodesBound();
        ResetUiForLoading();
        ProcessMode = ProcessModeEnum.Always;
        SetProcess(true);
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!_isLoading || string.IsNullOrEmpty(_pendingScenePath))
        {
            return;
        }

        PollThreadedLoadStatus(_pendingScenePath);
    }

    /// <summary>
    /// Starts asynchronous loading for the supplied scene path.
    /// </summary>
    /// <param name="scenePath">Resource path of the target packed scene.</param>
    /// <returns>Godot error code indicating whether loading was started.</returns>
    public virtual Error LoadSceneAsync(string scenePath)
    {
        EnsureUiNodesBound();

        if (string.IsNullOrWhiteSpace(scenePath))
        {
            return Error.InvalidParameter;
        }

        if (_isLoading)
        {
            return Error.Busy;
        }

        ResetUiForLoading();

        _pendingScenePath = scenePath;
        _threadedProgress[0] = 0.0f;
        _loadedScene = null;

        Error requestError = ResourceLoader.LoadThreadedRequest(scenePath);
        if (requestError != Error.Ok)
        {
            _pendingScenePath = null;
            return requestError;
        }

        _isLoading = true;
        return Error.Ok;
    }

    /// <inheritdoc />
    public override void _ExitTree() => DisconnectFromPoseRecentered();

    private void CompleteLoadAndWaitForPoseRecenter(string scenePath)
    {
        Resource loadedResource = ResourceLoader.LoadThreadedGet(scenePath);
        if (loadedResource is not PackedScene packedScene)
        {
            GD.PushError($"Loaded resource at '{scenePath}' was not a PackedScene.");
            _isLoading = false;
            _pendingScenePath = null;
            return;
        }

        _loadedScene = packedScene;
        _isLoading = false;
        _pendingScenePath = null;

        _progressBar!.Value = _progressBar.MaxValue;
        _progressBar.Hide();
        _loadingMessage!.Text = RecenterInstructionMessage;
        _isWaitingForPoseRecenter = true;

        ResolveXRManager().PoseRecentered += OnPoseRecentered;
    }

    private void CompleteSceneChange()
    {
        if (_loadedScene is null)
        {
            GD.PushError("Cannot complete scene change without a loaded PackedScene.");
            return;
        }

        DisconnectFromPoseRecentered();
        _isWaitingForPoseRecenter = false;

        SceneTree sceneTree = ResolveSceneTree();
        Error changeSceneError = sceneTree.ChangeSceneToPacked(_loadedScene);
        if (changeSceneError != Error.Ok)
        {
            GD.PushError($"Failed to change scene with error '{changeSceneError}'.");
            _loadedScene = null;
            return;
        }

        _loadedScene = null;
        _ = EmitSignal(SignalName.LoadCompleted);
    }

    private void UpdateProgressValue()
    {
        if (_threadedProgress.Count <= 0)
        {
            return;
        }

        float rawProgress = (float)_threadedProgress[0];
        _progressBar!.Value = Mathf.Clamp(rawProgress, 0.0f, 1.0f);
    }

    private void PollThreadedLoadStatus(string scenePath)
    {
        ResourceLoader.ThreadLoadStatus status = ResourceLoader.LoadThreadedGetStatus(scenePath, _threadedProgress);
        UpdateProgressValue();

        bool reportedComplete = status == ResourceLoader.ThreadLoadStatus.Loaded;
        bool reachedTerminalProgress = _progressBar!.Value >= (_progressBar.MaxValue - 0.001);

        if (reportedComplete || reachedTerminalProgress)
        {
            CompleteLoadAndWaitForPoseRecenter(scenePath);
            return;
        }

        switch (status)
        {
            case ResourceLoader.ThreadLoadStatus.InProgress:
            case ResourceLoader.ThreadLoadStatus.Loaded:
                return;

            case ResourceLoader.ThreadLoadStatus.Failed:
            case ResourceLoader.ThreadLoadStatus.InvalidResource:
                GD.PushError($"Threaded scene load failed for path '{scenePath}' with status '{status}'.");
                _isLoading = false;
                _pendingScenePath = null;
                return;

            default:
                return;
        }
    }

    private void EnsureUiNodesBound()
    {
        _loadingMessage ??= this.RequireNode<Label>("CenterContent/LoadingMessage");
        _progressBar ??= this.RequireNode<ProgressBar>("CenterContent/LoadingProgressBar");

        _progressBar.MinValue = 0.0;
        _progressBar.MaxValue = 1.0;
    }

    private void ResetUiForLoading()
    {
        DisconnectFromPoseRecentered();
        _isWaitingForPoseRecenter = false;
        _loadingMessage!.Text = LoadingMessage;
        _progressBar!.Show();

        if (!_isLoading)
        {
            _progressBar.Value = 0.0;
        }
    }

    private XRManager ResolveXRManager()
        => _xrManager ??= this.RequireNode<XRManager>(XRManagerPath);

    private void DisconnectFromPoseRecentered()
    {
        if (_xrManager is not null)
        {
            _xrManager.PoseRecentered -= OnPoseRecentered;
        }
    }

    private void OnPoseRecentered()
    {
        if (!_isWaitingForPoseRecenter)
        {
            return;
        }

        CompleteSceneChange();
    }

    private SceneTree ResolveSceneTree()
        => _sceneTree
            ?? GetTree()
            ?? Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Expected a valid SceneTree while loading scenes.");
}
