using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Voice;

/// <summary>
/// Base speech-voice component that converts speech text into synchronised spoken playback.
/// </summary>
[GlobalClass]
public abstract partial class Voice : Node3D, IVoice
{
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly CancellationTokenSource _nodeLifetimeCancellation = new();
    private bool _deferredGodotActionFlushQueued;
    private int _nodeLifetimeEnded;

    /// <summary>
    /// Emitted when speech generation or audio conversion fails.
    /// </summary>
    [Signal]
    public delegate void SpeechFailedEventHandler(string error);

    /// <summary>
    /// Stable voice identifier used by characters and authoring tools.
    /// </summary>
    [Export]
    public string Id { get; set; } = string.Empty;

    /// <inheritdoc />
    public string Type => "voice";

    /// <inheritdoc />
    public string FullId => $"{Type}:{Id}";

    /// <summary>
    /// Enables voice playback.
    /// </summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <inheritdoc />
    public Vector3 Origin => GlobalPosition;

    /// <summary>
    /// Indicates whether this voice has crossed its irreversible node-lifetime boundary.
    /// </summary>
    protected bool IsNodeLifetimeEnded => Volatile.Read(ref _nodeLifetimeEnded) != 0;

    /// <summary>
    /// Cancellation bounded by this voice node's scene-tree lifetime.
    /// </summary>
    protected CancellationToken NodeLifetimeCancellationToken => _nodeLifetimeCancellation.Token;

    /// <inheritdoc />
    public virtual void Speak(string speech)
    {
        try
        {
            ValueTask submission = SpeakAsync(speech);
            if (!submission.IsCompletedSuccessfully)
            {
                _ = ObserveCompatibilitySubmissionAsync(submission);
            }
        }
        catch (Exception ex)
        {
            ReportCompatibilityFailure(ex);
        }
    }

    /// <inheritdoc />
    public virtual ValueTask SpeakAsync(
        string speech,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string acceptedSpeech = ValidateSubmission(speech);
        _ = TryNotifySpeechGeneratedWhenEnabled(acceptedSpeech);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (Interlocked.Exchange(ref _nodeLifetimeEnded, 1) != 0)
        {
            return;
        }

        _nodeLifetimeCancellation.Cancel();

        DeferredGodotAction[] actions;
        lock (_deferredGodotActionsLock)
        {
            actions = [.. _deferredGodotActions];
            _deferredGodotActions.Clear();
            _deferredGodotActionFlushQueued = false;
        }

        foreach (DeferredGodotAction action in actions)
        {
            _ = action.CompletionSource.TrySetCanceled(NodeLifetimeCancellationToken);
        }
    }

    /// <summary>
    /// Validates common submission state and returns trimmed speech.
    /// </summary>
    protected string ValidateSubmission(string speech)
        => string.IsNullOrWhiteSpace(speech)
            ? throw new ArgumentException("Speech cannot be blank.", nameof(speech))
            : !Enabled
            ? throw new InvalidOperationException("Voice output is disabled.")
            : IsNodeLifetimeEnded ? throw new InvalidOperationException("Voice output is unavailable after node teardown.") : speech.Trim();

    /// <summary>
    /// Invokes the post-generation hook when speech is currently enabled.
    /// </summary>
    protected bool TryNotifySpeechGeneratedWhenEnabled(string speech)
    {
        if (!Enabled || IsNodeLifetimeEnded)
        {
            return false;
        }

        OnSpeechGenerated(speech);
        return true;
    }

    /// <summary>
    /// Called after a speech request has completed its generation or playback handoff boundary.
    /// </summary>
    protected virtual void OnSpeechGenerated(string speech)
    {
        if (!IsInsideTree())
        {
            return;
        }

        SceneTree? sceneTree = GetTree();
        if (sceneTree is null)
        {
            return;
        }

        foreach (Node node in sceneTree.GetNodesInGroup(IHearing.GroupName))
        {
            if (node is IHearing listener)
            {
                listener.ReceiveVoice(speech, this);
            }
        }
    }

    /// <summary>
    /// Dispatches a Godot action through the deferred main-thread queue.
    /// </summary>
    protected virtual Task DispatchDeferredGodotActionAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsNodeLifetimeEnded)
        {
            return Task.FromCanceled(NodeLifetimeCancellationToken);
        }

        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_deferredGodotActionsLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return Task.FromCanceled(NodeLifetimeCancellationToken);
            }

            _deferredGodotActions.Enqueue(new DeferredGodotAction(action, completionSource));
            if (!_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }

        return completionSource.Task;
    }

    /// <summary>
    /// Emits the voice failure signal.
    /// </summary>
    protected virtual void EmitSpeechFailedSignal(string error)
        => _ = EmitSignal(new StringName("SpeechFailed"), error);

    private async Task ObserveCompatibilitySubmissionAsync(ValueTask submission)
    {
        try
        {
            await submission;
        }
        catch (Exception ex)
        {
            ReportCompatibilityFailure(ex);
        }
    }

    private void ReportCompatibilityFailure(Exception exception)
    {
        if (exception is OperationCanceledException && IsNodeLifetimeEnded)
        {
            return;
        }

        if (GameLoggerResolver.TryResolve(out ILogger<Voice>? logger) && logger is not null)
        {
            logger.LogError(exception, "Voice compatibility submission failed: {Error}", exception.Message);
        }

        EmitSpeechFailedSignal(exception.Message);
    }

    private void FlushDeferredGodotActions()
    {
        DeferredGodotAction[] actions;
        lock (_deferredGodotActionsLock)
        {
            actions = [.. _deferredGodotActions];
            _deferredGodotActions.Clear();
            _deferredGodotActionFlushQueued = false;
        }

        foreach (DeferredGodotAction action in actions)
        {
            if (IsNodeLifetimeEnded)
            {
                _ = action.CompletionSource.TrySetCanceled(NodeLifetimeCancellationToken);
                continue;
            }

            try
            {
                action.Action();
                _ = action.CompletionSource.TrySetResult();
            }
            catch (Exception ex)
            {
                _ = action.CompletionSource.TrySetException(ex);
            }
        }

        lock (_deferredGodotActionsLock)
        {
            if (!IsNodeLifetimeEnded && _deferredGodotActions.Count > 0 && !_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }
    }

    private sealed record DeferredGodotAction(Action Action, TaskCompletionSource CompletionSource);
}
