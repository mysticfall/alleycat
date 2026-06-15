using System.Buffers.Binary;
using System.Text;
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
    /// Emitted when a speech-generation backend streams an incremental audio chunk.
    /// </summary>
    /// <remarks>
    /// Chunks are backend-provided data emitted before generator-level whole-file normalisation is applied.
    /// Consumers that require the configured <see cref="TargetSampleRate" /> must use
    /// <see cref="SpeechGenerationCompletedEventHandler" /> instead.
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
    /// Optional target sample rate for WAV PCM speech output normalisation.
    /// </summary>
    [Export]
    public int TargetSampleRate
    {
        get;
        set;
    }

    /// <summary>
    /// Indicates whether a speech-generation request is currently in flight.
    /// </summary>
    public bool IsGenerating
    {
        get;
        private set;
    }

    /// <summary>
    /// Generates speech audio for the supplied input text and applies any configured output normalisation.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <returns>Generated audio bytes after generator-level normalisation.</returns>
    public async Task<byte[]> Generate(string text, string? instruction = null)
        => NormaliseGeneratedAudio(await GenerateCore(text, instruction));

    /// <summary>
    /// Generates speech audio and optionally reports backend-provided audio chunks before completion.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <param name="audioChunkHandler">
    /// Optional asynchronous callback for incremental backend chunks. Chunks are emitted before generator-level
    /// whole-file normalisation and may therefore differ in sample rate or container structure from the final result.
    /// </param>
    /// <returns>Generated audio bytes after generator-level normalisation.</returns>
    public async Task<byte[]> GenerateStreaming(
        string text,
        string? instruction = null,
        Func<byte[], Task>? audioChunkHandler = null)
        => NormaliseGeneratedAudio(await GenerateStreamingCore(text, instruction, audioChunkHandler ?? NoOpAudioChunkHandler));

    /// <summary>
    /// Backend-specific speech generation implementation prior to generator-level normalisation.
    /// </summary>
    /// <param name="text">Text to synthesise.</param>
    /// <param name="instruction">Optional backend-specific instruction or prompt.</param>
    /// <returns>Raw generated audio bytes from the backend.</returns>
    protected abstract Task<byte[]> GenerateCore(string text, string? instruction = null);

    /// <summary>
    /// Backend-specific streaming speech generation implementation prior to generator-level normalisation.
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
    /// <param name="audioChunk">Backend-provided audio chunk before generator-level normalisation.</param>
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

    /// <summary>
    /// Applies generator-level audio normalisation when configured.
    /// </summary>
    /// <param name="audio">Generated audio bytes.</param>
    /// <returns>Original or resampled audio bytes.</returns>
    /// <exception cref="AudioNormalisationException">Thrown when configured resampling cannot be applied.</exception>
    protected byte[] NormaliseGeneratedAudio(byte[] audio)
        => NormaliseGeneratedAudio(audio, TargetSampleRate);

    internal static byte[] NormaliseGeneratedAudio(byte[] audio, int targetSampleRate)
        => targetSampleRate > 0
            ? ResamplePcmWave(audio, targetSampleRate)
            : audio;

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

    private static Task NoOpAudioChunkHandler(byte[] audioChunk)
    {
        _ = audioChunk;
        return Task.CompletedTask;
    }

    private static byte[] ResamplePcmWave(byte[] audio, int targetSampleRate)
    {
        if (targetSampleRate <= 0)
        {
            throw new AudioNormalisationException($"Audio resampling failed: target sample rate must be greater than zero. Got {targetSampleRate}.");
        }

        PcmWaveData wave = ParsePcmWave(audio);
        if (wave.SampleRate == targetSampleRate)
        {
            return audio;
        }

        byte[] resampledPcm = ResamplePcm16Audio(wave.PcmData, wave.ChannelCount, wave.SampleRate, targetSampleRate);
        return WriteWaveFile(resampledPcm, targetSampleRate, wave.ChannelCount, wave.BitsPerSample);
    }

    private static PcmWaveData ParsePcmWave(byte[] audio)
    {
        if (audio.Length < 44)
        {
            throw new AudioNormalisationException("Audio resampling failed: generated audio was too short to contain a valid WAV file.");
        }

        if (!HasAscii(audio, 0, "RIFF") || !HasAscii(audio, 8, "WAVE"))
        {
            throw new AudioNormalisationException("Audio resampling failed: generated audio was not a RIFF/WAVE file.");
        }

        int offset = 12;
        FmtChunkData? fmtChunk = null;
        byte[]? pcmData = null;

        while (offset <= audio.Length - 8)
        {
            string chunkId = Encoding.ASCII.GetString(audio, offset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(audio.AsSpan(offset + 4, 4));
            offset += 8;

            if (chunkSize < 0 || offset + chunkSize > audio.Length)
            {
                throw new AudioNormalisationException("Audio resampling failed: generated audio contained a malformed WAV chunk.");
            }

            ReadOnlySpan<byte> chunkData = audio.AsSpan(offset, chunkSize);

            switch (chunkId)
            {
                case "fmt ":
                    fmtChunk = ParseFmtChunk(chunkData);
                    break;
                case "data":
                    pcmData = chunkData.ToArray();
                    break;
                default:
                    break;
            }

            offset += chunkSize;
            if ((chunkSize & 1) != 0)
            {
                offset++;
            }
        }

        if (fmtChunk is null)
        {
            throw new AudioNormalisationException("Audio resampling failed: generated audio was missing the WAV fmt chunk.");
        }

        if (fmtChunk.FormatCode != 1)
        {
            throw new AudioNormalisationException($"Audio resampling failed: expected PCM WAV audio, got format code {fmtChunk.FormatCode}.");
        }

        if (fmtChunk.ChannelCount <= 0)
        {
            throw new AudioNormalisationException($"Audio resampling failed: expected a positive channel count, got {fmtChunk.ChannelCount}.");
        }

        if (fmtChunk.BitsPerSample != 16)
        {
            throw new AudioNormalisationException($"Audio resampling failed: only 16-bit PCM WAV audio is supported, got {fmtChunk.BitsPerSample}-bit.");
        }

        if (fmtChunk.SampleRate <= 0)
        {
            throw new AudioNormalisationException($"Audio resampling failed: expected a positive sample rate, got {fmtChunk.SampleRate}.");
        }

        if (pcmData is null || pcmData.Length == 0)
        {
            throw new AudioNormalisationException("Audio resampling failed: generated audio was missing the WAV data chunk.");
        }

        byte[] resolvedPcmData = pcmData;

        int expectedBlockAlign = fmtChunk.ChannelCount * (fmtChunk.BitsPerSample / 8);
        bool isPcmAlignmentInvalid = expectedBlockAlign <= 0 || (resolvedPcmData.Length % expectedBlockAlign) != 0;
        return isPcmAlignmentInvalid
            ? throw new AudioNormalisationException("Audio resampling failed: PCM data length was not aligned to the WAV frame size.")
            : new PcmWaveData(resolvedPcmData, fmtChunk.ChannelCount, fmtChunk.SampleRate, fmtChunk.BitsPerSample);
    }

    private static FmtChunkData ParseFmtChunk(ReadOnlySpan<byte> chunkData)
        => chunkData.Length < 16
            ? throw new AudioNormalisationException("Audio resampling failed: generated audio contained an incomplete WAV fmt chunk.")
            : new FmtChunkData(
                BinaryPrimitives.ReadInt16LittleEndian(chunkData[..2]),
                BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(2, 2)),
                BinaryPrimitives.ReadInt32LittleEndian(chunkData.Slice(4, 4)),
                BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(14, 2)));

    private static byte[] ResamplePcm16Audio(byte[] pcmData, short channelCount, int sourceSampleRate, int targetSampleRate)
    {
        int bytesPerSample = sizeof(short);
        int frameSize = channelCount * bytesPerSample;
        int sourceFrameCount = pcmData.Length / frameSize;
        if (sourceFrameCount == 0)
        {
            throw new AudioNormalisationException("Audio resampling failed: PCM data contained no audio frames.");
        }

        int targetFrameCount = Math.Max(1, (int)Math.Round(sourceFrameCount * (double)targetSampleRate / sourceSampleRate, MidpointRounding.AwayFromZero));
        byte[] output = new byte[targetFrameCount * frameSize];

        for (int targetFrameIndex = 0; targetFrameIndex < targetFrameCount; targetFrameIndex++)
        {
            double sourcePosition = targetFrameIndex * (double)sourceSampleRate / targetSampleRate;
            int sourceFrameIndex = Math.Min((int)Math.Floor(sourcePosition), sourceFrameCount - 1);
            int nextSourceFrameIndex = Math.Min(sourceFrameIndex + 1, sourceFrameCount - 1);
            double frameFraction = sourcePosition - sourceFrameIndex;

            for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
            {
                int sourceOffset = ((sourceFrameIndex * channelCount) + channelIndex) * bytesPerSample;
                int nextSourceOffset = ((nextSourceFrameIndex * channelCount) + channelIndex) * bytesPerSample;
                short sourceSample = BinaryPrimitives.ReadInt16LittleEndian(pcmData.AsSpan(sourceOffset, bytesPerSample));
                short nextSourceSample = BinaryPrimitives.ReadInt16LittleEndian(pcmData.AsSpan(nextSourceOffset, bytesPerSample));
                int interpolatedSample = (int)Math.Round(sourceSample + ((nextSourceSample - sourceSample) * frameFraction), MidpointRounding.AwayFromZero);
                short clampedSample = (short)Math.Clamp(interpolatedSample, short.MinValue, short.MaxValue);

                int outputOffset = ((targetFrameIndex * channelCount) + channelIndex) * bytesPerSample;
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(outputOffset, bytesPerSample), clampedSample);
            }
        }

        return output;
    }

    private static byte[] WriteWaveFile(byte[] pcmData, int sampleRate, short channelCount, short bitsPerSample)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        short blockAlign = (short)(channelCount * (bitsPerSample / 8));
        int byteRate = sampleRate * blockAlign;

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + pcmData.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(pcmData.Length);
        writer.Write(pcmData);
        writer.Flush();

        return stream.ToArray();
    }

    private static bool HasAscii(IReadOnlyList<byte> data, int offset, string text)
    {
        if (offset < 0 || offset + text.Length > data.Count)
        {
            return false;
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (data[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }

    internal sealed record PcmWaveData(byte[] PcmData, short ChannelCount, int SampleRate, short BitsPerSample);

    private sealed record FmtChunkData(short FormatCode, short ChannelCount, int SampleRate, short BitsPerSample);

    internal sealed class AudioNormalisationException(string message) : InvalidOperationException(message);
}
