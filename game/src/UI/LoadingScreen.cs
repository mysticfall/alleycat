using AlleyCat.Common;
using Godot;
using Array = Godot.Collections.Array;

namespace AlleyCat.UI;

/// <summary>
/// Displays loading progress while asynchronously preparing a scene transition.
/// </summary>
[GlobalClass]
public partial class LoadingScreen : Control
{
    private readonly Array _threadedProgress = [0.0f];

    private Label? _loadingMessage;
    private ProgressBar? _progressBar;
    private SceneTree? _sceneTree;
    private string? _pendingScenePath;
    private bool _isLoading;

    /// <summary>
    /// Message shown above the loading progress indicator.
    /// </summary>
    [Export]
    public string LoadingMessage { get; set; } = "Loading…";

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
    public Error LoadSceneAsync(string scenePath)
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

        _pendingScenePath = scenePath;
        _progressBar!.Value = 0.0;
        _threadedProgress[0] = 0.0f;

        Error requestError = ResourceLoader.LoadThreadedRequest(scenePath);
        if (requestError != Error.Ok)
        {
            _pendingScenePath = null;
            return requestError;
        }

        _isLoading = true;
        return Error.Ok;
    }

    private void CompleteSceneChange(string scenePath)
    {
        Resource loadedResource = ResourceLoader.LoadThreadedGet(scenePath);
        if (loadedResource is not PackedScene packedScene)
        {
            GD.PushError($"Loaded resource at '{scenePath}' was not a PackedScene.");
            _isLoading = false;
            _pendingScenePath = null;
            return;
        }

        SceneTree sceneTree = ResolveSceneTree();
        Error changeSceneError = sceneTree.ChangeSceneToPacked(packedScene);
        if (changeSceneError != Error.Ok)
        {
            GD.PushError($"Failed to change scene to '{scenePath}' with error '{changeSceneError}'.");
            _isLoading = false;
            _pendingScenePath = null;
            return;
        }

        _isLoading = false;
        _pendingScenePath = null;

        _progressBar!.Value = _progressBar.MaxValue;
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
            CompleteSceneChange(scenePath);
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

        _loadingMessage.Text = LoadingMessage;

        _progressBar.MinValue = 0.0;
        _progressBar.MaxValue = 1.0;

        if (!_isLoading)
        {
            _progressBar.Value = 0.0;
        }
    }

    private SceneTree ResolveSceneTree()
        => _sceneTree
            ?? GetTree()
            ?? Engine.GetMainLoop() as SceneTree
            ?? throw new InvalidOperationException("Expected a valid SceneTree while loading scenes.");
}
