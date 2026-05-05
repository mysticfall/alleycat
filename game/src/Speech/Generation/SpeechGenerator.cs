using AlleyCat.UI;
using Godot;

namespace AlleyCat.Speech.Generation;

/// <summary>
/// Base speech-generation component that dispatches asynchronous text-to-speech requests.
/// </summary>
public abstract partial class SpeechGenerator : Node
{
    private const string DefaultFriendlyErrorMessage = "Speech generation failed. Please try again.";

    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private readonly Lock _generationStateLock = new();
    private bool _deferredGodotActionFlushQueued;

    /// <summary>
    /// Emitted when a speech-generation request completes successfully.
    /// </summary>
    [Signal]
    public delegate void SpeechGenerationCompletedEventHandler(byte[] audio);

    /// <summary>
    /// Emitted when a speech-generation request fails.
    /// </summary>
    [Signal]
    public delegate void SpeechGenerationFailedEventHandler(string error);

    /// <summary>
    /// Enables speech-generation request dispatch.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get;
        set;
    } = true;

    /// <summary>
    /// Indicates whether a speech-generation request is currently in flight.
    /// </summary>
    public bool IsGenerating
    {
        get;
        private set;
    }

    /// <summary>
    /// Generates speech audio for the supplied input text.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <returns>Raw generated audio bytes.</returns>
    public abstract Task<byte[]> Generate(string text, string? instruction = null);

    /// <summary>
    /// Dispatches a speech-generation request and emits completion or failure signals.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    public void GenerateSpeech(string text, string? instruction = null)
        => _ = InvokeGenerationAsync(text, instruction);

    /// <summary>
    /// Hook invoked after speech generation succeeds.
    /// </summary>
    /// <param name="audio">Generated audio bytes.</param>
    protected virtual void OnSpeechGenerationCompleted(byte[] audio)
    {
    }

    /// <summary>
    /// Hook invoked after speech generation fails.
    /// </summary>
    /// <param name="error">Backend error message.</param>
    protected virtual void OnSpeechGenerationFailed(string error)
    {
    }

    /// <summary>
    /// Dispatches a Godot action through the deferred queue.
    /// </summary>
    /// <param name="action">Action to execute on the Godot thread.</param>
    /// <returns>Completion task for the queued action.</returns>
    protected Task DispatchDeferredGodotActionAsync(Action action)
        => DispatchGodotActionAsync(action);

    private async Task InvokeGenerationAsync(string text, string? instruction)
    {
        if (!Enabled || !TryBeginGeneration())
        {
            return;
        }

        try
        {
            byte[] audio = await Generate(text, instruction);
            await DispatchGodotActionAsync(() => HandleGenerationSuccess(audio));
        }
        catch (Exception ex)
        {
            await DispatchGodotActionAsync(() => HandleGenerationFailure(ex));
        }
        finally
        {
            EndGeneration();
        }
    }

    private bool TryBeginGeneration()
    {
        lock (_generationStateLock)
        {
            if (IsGenerating)
            {
                return false;
            }

            IsGenerating = true;
            return true;
        }
    }

    private void EndGeneration()
    {
        lock (_generationStateLock)
        {
            IsGenerating = false;
        }
    }

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

    private void HandleGenerationSuccess(byte[] audio)
    {
        _ = EmitSignal(SignalName.SpeechGenerationCompleted, audio);
        OnSpeechGenerationCompleted(audio);
    }

    private void HandleGenerationFailure(Exception ex)
    {
        GD.PushError(ex.ToString());
        _ = EmitSignal(SignalName.SpeechGenerationFailed, ex.Message);
        _ = this.PostNotification(DefaultFriendlyErrorMessage);
        OnSpeechGenerationFailed(ex.Message);
    }

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }
}
