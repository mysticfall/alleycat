using AlleyCat.Core.Logging;
using AlleyCat.UI;
using Godot;
using Microsoft.Extensions.Logging;

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
    private ILogger<SpeechGenerator>? _logger;

    /// <summary>
    /// Emitted when a speech-generation request completes successfully.
    /// </summary>
    [Signal]
    public delegate void SpeechGenerationCompletedEventHandler(byte[] audio);

    /// <summary>
    /// Emitted when a speech-generation backend streams an incremental audio chunk.
    /// </summary>
    /// <remarks>
    /// Chunks and the completion audio are both raw backend output.
    /// </remarks>
    [Signal]
    public delegate void SpeechGenerationChunkReceivedEventHandler(byte[] audioChunk);

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
    /// <returns>Raw generated audio bytes from the backend.</returns>
    public async Task<byte[]> Generate(string text, string? instruction = null)
        => await GenerateCore(text, instruction);

    /// <summary>
    /// Generates speech audio and optionally reports backend-provided audio chunks before completion.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <param name="audioChunkHandler">
    /// Optional asynchronous callback for incremental backend chunks. Chunks are raw backend output and use the same
    /// encoding as the final result.
    /// </param>
    /// <returns>Raw generated audio bytes from the backend.</returns>
    public async Task<byte[]> GenerateStreaming(
        string text,
        string? instruction = null,
        Func<byte[], Task>? audioChunkHandler = null)
        => await GenerateStreamingCore(text, instruction, audioChunkHandler ?? NoOpAudioChunkHandler);

    /// <summary>
    /// Backend-specific speech generation implementation.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <returns>Raw generated audio bytes from the backend.</returns>
    protected abstract Task<byte[]> GenerateCore(string text, string? instruction = null);

    /// <summary>
    /// Backend-specific streaming speech generation implementation.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <param name="audioChunkHandler">Callback for raw backend audio chunks emitted during generation.</param>
    /// <returns>Raw generated audio bytes from the backend.</returns>
    protected virtual Task<byte[]> GenerateStreamingCore(
        string text,
        string? instruction,
        Func<byte[], Task> audioChunkHandler)
    {
        _ = audioChunkHandler;
        return GenerateCore(text, instruction);
    }

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
    /// Hook invoked when the backend streams an incremental audio chunk.
    /// </summary>
    /// <param name="audioChunk">Raw backend-provided audio chunk.</param>
    protected virtual void OnSpeechGenerationChunkReceived(byte[] audioChunk)
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
            byte[] audio = await GenerateStreaming(text, instruction, DispatchGenerationChunkAsync);
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

    private Task DispatchGenerationChunkAsync(byte[] audioChunk)
        => DispatchGodotActionAsync(() => HandleGenerationChunk(audioChunk));

    private void HandleGenerationChunk(byte[] audioChunk)
    {
        _ = EmitSignal(SignalName.SpeechGenerationChunkReceived, audioChunk);
        OnSpeechGenerationChunkReceived(audioChunk);
    }

    private void HandleGenerationFailure(Exception ex)
    {
        // Failure UX and signal emission must still run in isolated integration scenes without the Game provider;
        // diagnostics are explicitly optional only for this recovery path.
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<SpeechGenerator>? logger))
        {
            _logger = logger;
        }

        if (_logger is { } resolvedLogger)
        {
            resolvedLogger.LogError(
                ex,
                "Speech generation failed while synthesising requested text.");
        }

        _ = EmitSignal(SignalName.SpeechGenerationFailed, ex.Message);
        _ = this.PostNotification(DefaultFriendlyErrorMessage);
        OnSpeechGenerationFailed(ex.Message);
    }

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }

    private static Task NoOpAudioChunkHandler(byte[] audioChunk)
    {
        _ = audioChunk;
        return Task.CompletedTask;
    }
}
