using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using AlleyCat.Core.Logging;
using AlleyCat.Diagnostics;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Voice;

/// <summary>
/// Voice implementation that generates speech audio and hands it off to lip-sync playback.
/// </summary>
[GlobalClass]
public partial class AIVoice : Voice
{
    private const int ExpectedWaveFormatCode = 1;
    private const short ExpectedChannelCount = 1;
    private const short ExpectedBitsPerSample = 16;
    private const int ExpectedSampleRate = 16000;
    private const string AudioFormatIncompatibleMessage = "Audio format incompatible";

    private readonly Lock _submissionLock = new();
    private readonly Queue<string> _pendingSpeech = [];
    private bool _pumpRunning;
    private TaskCompletionSource? _pumpSettlement;
    private ILogger<AIVoice>? _logger;

    /// <summary>
    /// Speech generator used to create spoken audio bytes.
    /// </summary>
    [Export]
    public SpeechGenerator? SpeechGenerator
    {
        get;
        set;
    }

    /// <summary>
    /// Lip-sync player that owns synchronised playback.
    /// </summary>
    [Export]
    public LipSyncPlayer? LipSyncPlayer
    {
        get;
        set;
    }

    internal Task PumpSettlement
    {
        get
        {
            lock (_submissionLock)
            {
                return _pumpSettlement?.Task ?? Task.CompletedTask;
            }
        }
    }

    /// <inheritdoc />
    public override ValueTask SpeakAsync(
        string speech,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string acceptedSpeech = ValidateSubmission(speech);

        bool startPump;
        lock (_submissionLock)
        {
            if (IsNodeLifetimeEnded)
            {
                throw new InvalidOperationException("AI voice is unavailable after node teardown.");
            }

            if (!Enabled || SpeechGenerator is null || LipSyncPlayer is null)
            {
                throw new InvalidOperationException(
                    "AI voice requires enabled output with configured speech generator and lip-sync player dependencies.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _pendingSpeech.Enqueue(acceptedSpeech);
            startPump = !_pumpRunning;
            _pumpRunning = true;
            if (startPump)
            {
                _pumpSettlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (startPump)
        {
            _ = DrainSpeechQueueAsync();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        base._ExitTree();
        lock (_submissionLock)
        {
            _pendingSpeech.Clear();
        }
    }

    private async Task DrainSpeechQueueAsync()
    {
        await Task.Yield();

        while (true)
        {
            string speech;
            lock (_submissionLock)
            {
                if (IsNodeLifetimeEnded || _pendingSpeech.Count == 0)
                {
                    _pendingSpeech.Clear();
                    _pumpRunning = false;
                    TaskCompletionSource? settlement = _pumpSettlement;
                    _pumpSettlement = null;
                    _ = settlement?.TrySetResult();
                    return;
                }

                speech = _pendingSpeech.Dequeue();
            }

            await ProcessAdmittedSpeechAsync(speech);
        }
    }

    private async Task ProcessAdmittedSpeechAsync(string speech)
    {
        Stopwatch totalStopwatch = AIPipelineDebugLog.StartTimer();

        try
        {
            NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Stage("TTS request received", $"{speech.Length} chars");
            }

            Stopwatch generationStopwatch = AIPipelineDebugLog.StartTimer();
            byte[] generatedAudio = await GenerateSpeechAudioAsync(speech)
                .WaitAsync(NodeLifetimeCancellationToken);
            NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("TTS audio generated in", generationStopwatch, $"{generatedAudio.Length} bytes");
            }

            Stopwatch parseStopwatch = AIPipelineDebugLog.StartTimer();
            AudioStreamWav speechStream = CreatePlayableSpeech(generatedAudio);
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("TTS audio parsed in", parseStopwatch, $"{speechStream.Data.Length} PCM bytes");
            }

            Stopwatch lipSyncStopwatch = AIPipelineDebugLog.StartTimer();
            LipSyncPlayer.PreparedPlayback preparedPlayback = await PrepareGeneratedSpeechAsync(
                speechStream,
                NodeLifetimeCancellationToken);
            NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency(
                    "TTS lip-sync prepared in",
                    lipSyncStopwatch,
                    $"{preparedPlayback.Frames.Length} frames");
            }

            await DispatchDeferredGodotActionAsync(() =>
            {
                NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
                PlayGeneratedSpeech(preparedPlayback);
                OnSpeechGenerated(speech);
            });
            AIPipelineDebugLog.Latency("TTS playback started after", totalStopwatch);
        }
        catch (OperationCanceledException) when (IsNodeLifetimeEnded
            || NodeLifetimeCancellationToken.IsCancellationRequested
            || LipSyncPlayer is { IsLifetimeEnded: true })
        {
        }
        catch (AudioConversionException ex)
        {
            AIPipelineDebugLog.Latency("TTS failed after", totalStopwatch);
            await ReportAdmittedFailureAsync(AudioFormatIncompatibleMessage, ex);
        }
        catch (Exception ex)
        {
            AIPipelineDebugLog.Latency("TTS failed after", totalStopwatch);
            await ReportAdmittedFailureAsync(ex.Message, ex);
        }
    }

    private async Task ReportAdmittedFailureAsync(string emittedError, Exception exception)
    {
        if (IsNodeLifetimeEnded)
        {
            return;
        }

        try
        {
            await FailSpeechAsync(emittedError, exception);
        }
        catch (OperationCanceledException) when (IsNodeLifetimeEnded)
        {
        }
        catch (Exception reportingException)
        {
            if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<AIVoice>? logger))
            {
                _logger = logger;
            }

            _logger?.LogError(
                reportingException,
                "AI voice could not report admitted speech failure: {Error}",
                emittedError);
        }
    }

    internal static AudioStreamWav CreatePlayableSpeech(byte[] generatedAudio)
    {
        ParsedSpeechData speechData = ParsePlayableSpeechData(generatedAudio);

        AudioStreamWav audioStream = new()
        {
            Data = speechData.PcmData,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = speechData.SampleRate,
            Stereo = speechData.Stereo,
        };

        return audioStream;
    }

    internal static ParsedSpeechData ParsePlayableSpeechData(byte[] generatedAudio)
    {
        WaveFileData waveFile = ParseWaveFile(generatedAudio);
        return new ParsedSpeechData(waveFile.PcmData, ExpectedSampleRate, Stereo: false, BitsPerSample: ExpectedBitsPerSample);
    }

    private static WaveFileData ParseWaveFile(byte[] audioBytes)
    {
        if (audioBytes.Length < 44)
        {
            throw new AudioConversionException("Generated audio was too short to contain a valid WAV file.");
        }

        if (!HasAscii(audioBytes, 0, "RIFF") || !HasAscii(audioBytes, 8, "WAVE"))
        {
            throw new AudioConversionException("Generated audio was not a RIFF/WAVE file.");
        }

        int offset = 12;
        FmtChunkData? fmtChunk = null;
        byte[]? pcmData = null;

        while (offset <= audioBytes.Length - 8)
        {
            string chunkId = Encoding.ASCII.GetString(audioBytes, offset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(audioBytes.AsSpan(offset + 4, 4));
            offset += 8;

            if (chunkSize < 0 || offset + chunkSize > audioBytes.Length)
            {
                throw new AudioConversionException("Generated audio contained a malformed WAV chunk.");
            }

            ReadOnlySpan<byte> chunkData = audioBytes.AsSpan(offset, chunkSize);

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
            throw new AudioConversionException("Generated audio was missing the WAV fmt chunk.");
        }

        if (pcmData is null || pcmData.Length == 0)
        {
            throw new AudioConversionException("Generated audio was missing the WAV data chunk.");
        }

        ValidateCompatibility(fmtChunk);
        return new WaveFileData(pcmData);
    }

    private static FmtChunkData ParseFmtChunk(ReadOnlySpan<byte> chunkData)
        => chunkData.Length < 16
            ? throw new AudioConversionException("Generated audio contained an incomplete WAV fmt chunk.")
            : new FmtChunkData(
                BinaryPrimitives.ReadInt16LittleEndian(chunkData[..2]),
                BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(2, 2)),
                BinaryPrimitives.ReadInt32LittleEndian(chunkData.Slice(4, 4)),
                BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(14, 2)));

    private static void ValidateCompatibility(FmtChunkData fmtChunk)
    {
        if (fmtChunk.FormatCode != ExpectedWaveFormatCode)
        {
            throw new AudioConversionException($"Expected PCM WAV audio, got format code {fmtChunk.FormatCode}.");
        }

        if (fmtChunk.ChannelCount != ExpectedChannelCount)
        {
            throw new AudioConversionException($"Expected mono WAV audio, got {fmtChunk.ChannelCount} channels.");
        }

        if (fmtChunk.SampleRate != ExpectedSampleRate)
        {
            throw new AudioConversionException($"Expected 16000 Hz WAV audio, got {fmtChunk.SampleRate} Hz.");
        }

        if (fmtChunk.BitsPerSample != ExpectedBitsPerSample)
        {
            throw new AudioConversionException($"Expected 16-bit WAV audio, got {fmtChunk.BitsPerSample}-bit.");
        }
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

    private Task FailSpeechAsync(string emittedError, Exception? exception = null)
        => DispatchDeferredGodotActionAsync(() => ReportSpeechFailure(emittedError, exception));

    /// <summary>
    /// Generates raw speech audio bytes for the supplied speech text.
    /// </summary>
    /// <param name="speech">Speech text to synthesise.</param>
    /// <returns>Generated speech audio bytes.</returns>
    protected virtual Task<byte[]> GenerateSpeechAudioAsync(string speech)
        => SpeechGenerator!.Generate(speech);

    /// <summary>
    /// Prepares lip-sync data for a generated WAV stream before playback starts.
    /// </summary>
    /// <param name="speechStream">Prepared speech stream.</param>
    /// <param name="cancellationToken">Voice-lifetime cancellation propagated into backend preparation.</param>
    /// <returns>Prepared speech playback data.</returns>
    protected virtual Task<LipSyncPlayer.PreparedPlayback> PrepareGeneratedSpeechAsync(
        AudioStreamWav speechStream,
        CancellationToken cancellationToken)
        => LipSyncPlayer!.PreparePlaybackAsync(speechStream, cancellationToken);

    /// <summary>
    /// Hands a prepared WAV stream off to the lip-sync playback boundary.
    /// </summary>
    /// <param name="preparedPlayback">Prepared speech stream and lip-sync inference data.</param>
    protected virtual void PlayGeneratedSpeech(LipSyncPlayer.PreparedPlayback preparedPlayback)
        => LipSyncPlayer!.PlayPrepared(preparedPlayback);

    private void ReportSpeechFailure(string emittedError, Exception? exception)
    {
        // Failure signal emission must still run in isolated integration scenes without the Game provider;
        // diagnostics are explicitly optional only for this recovery path.
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<AIVoice>? logger))
        {
            _logger = logger;
        }

        if (_logger is { } resolvedLogger)
        {
            if (exception is null)
            {
                resolvedLogger.LogError("AI voice speech failed: {Error}", emittedError);
            }
            else
            {
                resolvedLogger.LogError(exception, "AI voice speech failed: {Error}", emittedError);
            }
        }

        EmitSpeechFailedSignal(emittedError);
    }

    /// <summary>
    /// Emits the voice failure signal.
    /// </summary>
    /// <param name="error">Failure message payload.</param>
    protected override void EmitSpeechFailedSignal(string error) => base.EmitSpeechFailedSignal(error);

    private sealed record WaveFileData(byte[] PcmData);

    private sealed record FmtChunkData(short FormatCode, short ChannelCount, int SampleRate, short BitsPerSample);

    internal sealed record ParsedSpeechData(byte[] PcmData, int SampleRate, bool Stereo, short BitsPerSample);

    internal sealed class AudioConversionException(string message) : Exception(message);
}
