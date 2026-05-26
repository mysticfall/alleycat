using Godot;

namespace AlleyCat.Body.Voice;

/// <summary>
/// Base speech-voice component that converts speech text into synchronised spoken playback.
/// </summary>
[GlobalClass]
public abstract partial class Voice : Node3D, IVoice
{
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private bool _deferredGodotActionFlushQueued;

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

    /// <summary>
    /// Enables voice playback.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get;
        set;
    } = true;

    /// <inheritdoc />
    public Vector3 Origin => GlobalPosition;

    /// <summary>
    /// Starts speech generation and playback for the supplied speech text.
    /// </summary>
    /// <param name="speech">Speech text to speak.</param>
    public abstract void Speak(string speech);

    /// <summary>
    /// Invokes the post-generation hook when speech is currently enabled.
    /// </summary>
    /// <param name="speech">Speech that completed generation or playback handoff.</param>
    /// <returns>True when the hook was invoked; otherwise false.</returns>
    protected bool TryNotifySpeechGeneratedWhenEnabled(string speech)
    {
        if (!Enabled)
        {
            return false;
        }

        OnSpeechGenerated(speech);
        return true;
    }

    /// <summary>
    /// Called after a speech request has completed its generation or playback handoff boundary.
    /// </summary>
    /// <param name="speech">Speech that completed generation or playback handoff.</param>
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

        foreach (Node node in sceneTree.GetNodesInGroup(IVoiceListener.GroupName))
        {
            if (node is IVoiceListener listener)
            {
                listener.ReceiveVoice(speech, this);
            }
        }
    }

    /// <summary>
    /// Dispatches a Godot action through the deferred main-thread queue.
    /// </summary>
    /// <param name="action">Action to execute on the Godot thread.</param>
    /// <returns>Completion task for the queued action.</returns>
    protected virtual Task DispatchDeferredGodotActionAsync(Action action)
        => DispatchGodotActionAsync(action);

    private Task DispatchGodotActionAsync(Action action)
    {
        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_deferredGodotActionsLock)
        {
            _deferredGodotActions.Enqueue(new DeferredGodotAction(action, completionSource));

            if (!_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }

        return completionSource.Task;
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
            if (_deferredGodotActions.Count > 0 && !_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }
    }

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }
}
