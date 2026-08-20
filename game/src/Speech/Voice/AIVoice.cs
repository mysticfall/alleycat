using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using AlleyCat.Core.Logging;
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
    private Queue<AdmittedSpeech> _pendingSpeech = [];
    private bool _pumpRunning;
    private TaskCompletionSource? _pumpSettlement;
    private int _outstandingItems;
    private bool _playbackPending;
    private LipSyncPlayer? _playbackWatchPlayer;
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
        _ = AdmitSpeech(acceptedSpeech, turnCancellation: null, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask SpeakCancellableAsync(
        string speech,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string acceptedSpeech = ValidateSubmission(speech);
        var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        AdmittedSpeech item;
        try
        {
            item = AdmitSpeech(acceptedSpeech, turnCancellation, cancellationToken);
        }
        catch
        {
            // Admission never committed, so the linked source would otherwise leak its registration on the
            // caller-supplied token.
            turnCancellation.Dispose();
            throw;
        }

        item.SetCancellationRegistration(cancellationToken.Register(
            () => HandleTurnCancellationRequested(item),
            useSynchronizationContext: false));
        return new ValueTask(item.HandOffCompletion!.Task);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        base._ExitTree();

        AdmittedSpeech[] queuedItems;
        lock (_submissionLock)
        {
            queuedItems = [.. _pendingSpeech];
            _pendingSpeech.Clear();
            _outstandingItems = 0;
            _playbackPending = false;
        }

        foreach (AdmittedSpeech item in queuedItems)
        {
            item.DisposeCancellationRegistration();
            _ = item.HandOffCompletion?.TrySetCanceled(NodeLifetimeCancellationToken);
        }

        if (_playbackWatchPlayer is { } watchedPlayer)
        {
            watchedPlayer.PlaybackCompleted -= HandleLipSyncPlaybackCompleted;
            _playbackWatchPlayer = null;
        }
    }

    /// <summary>
    /// Cuts active playback immediately, halting audio and lip-sync, and settles the speaking window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the interruption-driven cut (AI-001 TR-44): the lip-sync player's stop does not raise
    /// <see cref="LipSyncPlayer.PlaybackCompleted" />, so the speaking-window bookkeeping that the notification would
    /// have performed is settled here exactly once. Queued FIFO submissions are not retracted; the window stays open
    /// while any remain outstanding. Must be called on the Godot thread.
    /// </para>
    /// <para>
    /// This capability is intentionally a concrete member rather than an <see cref="IVoice" /> default-interface
    /// member: interface mapping is established on the <see cref="Voice" /> base class, so a derived override would
    /// never dispatch through an <see cref="IVoice" />-typed reference and a default body would silently swallow the
    /// cut. Callers type-test for <see cref="AIVoice" /> instead.
    /// </para>
    /// </remarks>
    public void CutSpeech()
    {
        bool closeWindow;
        lock (_submissionLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            _playbackPending = false;
            closeWindow = _outstandingItems == 0;
        }

        LipSyncPlayer?.Stop();

        if (closeWindow)
        {
            CloseSpeakingWindow();
        }
    }

    /// <summary>
    /// Atomically admits a validated speech request as the next FIFO queue item and opens the speaking window.
    /// </summary>
    /// <param name="speech">Validated speech text to admit.</param>
    /// <param name="turnCancellation">Linked cancellation source for explicitly cancellable submissions, or null for
    /// ordinary admission-only submissions.</param>
    /// <param name="callerToken">Caller-supplied cancellation observed until admission commits.</param>
    /// <returns>The admitted queue item.</returns>
    private AdmittedSpeech AdmitSpeech(
        string speech,
        CancellationTokenSource? turnCancellation,
        CancellationToken callerToken)
    {
        AdmittedSpeech item = new(
            speech,
            turnCancellation,
            turnCancellation is null ? null : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            callerToken);
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

            callerToken.ThrowIfCancellationRequested();
            _pendingSpeech.Enqueue(item);
            _outstandingItems++;
            startPump = !_pumpRunning;
            _pumpRunning = true;
            if (startPump)
            {
                _pumpSettlement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            EnsurePlaybackCompletionSubscriptionLocked();
        }

        OpenSpeakingWindow();

        if (startPump)
        {
            _ = DrainSpeechQueueAsync();
        }

        return item;
    }

    /// <summary>
    /// Subscribes to the configured lip-sync player's playback-completed notification exactly once per player.
    /// </summary>
    /// <remarks>Must be called while holding <see cref="_submissionLock" />.</remarks>
    private void EnsurePlaybackCompletionSubscriptionLocked()
    {
        LipSyncPlayer? player = LipSyncPlayer;
        if (player is null || ReferenceEquals(_playbackWatchPlayer, player))
        {
            return;
        }

        _playbackWatchPlayer?.PlaybackCompleted -= HandleLipSyncPlaybackCompleted;

        player.PlaybackCompleted += HandleLipSyncPlaybackCompleted;
        _playbackWatchPlayer = player;
    }

    private void HandleLipSyncPlaybackCompleted()
    {
        bool closeWindow;
        lock (_submissionLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            _playbackPending = false;
            closeWindow = _outstandingItems == 0;
        }

        if (closeWindow)
        {
            CloseSpeakingWindow();
        }
    }

    /// <summary>
    /// Handles caller cancellation of an explicitly cancellable submission before playback hand-off.
    /// </summary>
    /// <param name="item">Admitted item whose caller token was cancelled.</param>
    private void HandleTurnCancellationRequested(AdmittedSpeech item)
    {
        bool removedFromQueue;
        lock (_submissionLock)
        {
            if (IsNodeLifetimeEnded || item.HandOffCommitted || item.Settled)
            {
                return;
            }

            item.CancelRequested = true;
            removedFromQueue = RemoveQueuedItemLocked(item);
        }

        item.TurnCancellation?.Cancel();

        if (removedFromQueue)
        {
            AbortAdmittedItemSilently(item);
        }

        // Items already dequeued observe the cancelled turn token at their next pipeline boundary, or the
        // playback hand-off refusal check aborts them before committing.
    }

    private bool RemoveQueuedItemLocked(AdmittedSpeech item)
    {
        if (!_pendingSpeech.Contains(item))
        {
            return false;
        }

        _pendingSpeech = new Queue<AdmittedSpeech>(_pendingSpeech.Where(pending => !ReferenceEquals(pending, item)));
        return true;
    }

    private static bool IsTurnCancellation(AdmittedSpeech item)
        => item.CancelRequested || item.TurnCancellation is { IsCancellationRequested: true };

    /// <summary>
    /// Silently aborts an explicitly cancellable submission without failure signalling or listener notification.
    /// </summary>
    /// <param name="item">Admitted item to abort.</param>
    private void AbortAdmittedItemSilently(AdmittedSpeech item)
    {
        item.DisposeCancellationRegistration();
        SettleAdmittedItemWithoutPlayback(item);
        _ = item.HandOffCompletion?.TrySetCanceled(item.CallerToken);
    }

    /// <summary>
    /// Settles an admitted item that will never reach playback, closing the window when no work remains.
    /// </summary>
    /// <param name="item">Admitted item to settle.</param>
    private void SettleAdmittedItemWithoutPlayback(AdmittedSpeech item)
    {
        bool closeWindow;
        lock (_submissionLock)
        {
            if (item.Settled)
            {
                return;
            }

            item.Settled = true;
            if (_outstandingItems > 0)
            {
                _outstandingItems--;
            }

            closeWindow = _outstandingItems == 0 && !_playbackPending;
        }

        if (closeWindow)
        {
            CloseSpeakingWindow();
        }
    }

    private async Task DrainSpeechQueueAsync()
    {
        await Task.Yield();

        while (true)
        {
            AdmittedSpeech item;
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

                item = _pendingSpeech.Dequeue();
            }

            await ProcessAdmittedSpeechAsync(item);
        }
    }

    private async Task ProcessAdmittedSpeechAsync(AdmittedSpeech item)
    {
        Stopwatch totalStopwatch = PipelineDebugLog.StartTimer();
        CancellationTokenSource? pipelineCancellationSource = item.TurnCancellation is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(NodeLifetimeCancellationToken, item.TurnCancellation.Token);
        CancellationToken pipelineCancellation = pipelineCancellationSource?.Token ?? NodeLifetimeCancellationToken;

        try
        {
            NodeLifetimeCancellationToken.ThrowIfCancellationRequested();
            if (PipelineDebugLog.IsEnabled)
            {
                PipelineDebugLog.Stage("TTS request received", $"{item.Text.Length} chars");
            }

            Stopwatch generationStopwatch = PipelineDebugLog.StartTimer();
            byte[] generatedAudio = await GenerateSpeechAudioAsync(item.Text)
                .WaitAsync(pipelineCancellation);
            pipelineCancellation.ThrowIfCancellationRequested();
            if (PipelineDebugLog.IsEnabled)
            {
                PipelineDebugLog.Latency("TTS audio generated in", generationStopwatch, $"{generatedAudio.Length} bytes");
            }

            Stopwatch parseStopwatch = PipelineDebugLog.StartTimer();
            AudioStreamWav speechStream = CreatePlayableSpeech(generatedAudio);
            if (PipelineDebugLog.IsEnabled)
            {
                PipelineDebugLog.LogOnlyLatency("TTS audio parsed in", parseStopwatch, $"{speechStream.Data.Length} PCM bytes");
            }

            Stopwatch lipSyncStopwatch = PipelineDebugLog.StartTimer();
            LipSyncPlayer.PreparedPlayback preparedPlayback = await PrepareGeneratedSpeechAsync(
                speechStream,
                pipelineCancellation);
            pipelineCancellation.ThrowIfCancellationRequested();

            // The mapped mesh count only exists once playback hand-off binds the prepared frames to the character
            // meshes, so the stage is emitted after the hand-off dispatch using the elapsed snapshot taken at the
            // preparation boundary. The count is seeded with the last-known mapping so a refused hand-off still
            // reports it (zero on the first utterance). The console detail stays on one line, while the toast keeps
            // only the frame count to stay short.
            TimeSpan lipSyncElapsed = lipSyncStopwatch.Elapsed;
            int mappedMeshCount = LipSyncPlayer?.MappedMeshCount ?? 0;
            try
            {
                await DispatchDeferredGodotActionAsync(() =>
                {
                    CommitPlaybackHandOff(item, preparedPlayback);
                    mappedMeshCount = LipSyncPlayer?.MappedMeshCount ?? 0;
                });
            }
            finally
            {
                if (PipelineDebugLog.IsEnabled)
                {
                    PipelineDebugLog.Latency(
                        "TTS lip-sync prepared in",
                        lipSyncElapsed,
                        $"{preparedPlayback.Frames.Length} frames, {mappedMeshCount} mesh(es)",
                        $"{preparedPlayback.Frames.Length} frames");
                }
            }

            PipelineDebugLog.LogOnlyLatency("TTS playback started after", totalStopwatch);
        }
        catch (OperationCanceledException) when (IsNodeLifetimeEnded
            || NodeLifetimeCancellationToken.IsCancellationRequested
            || LipSyncPlayer is { IsLifetimeEnded: true })
        {
            item.DisposeCancellationRegistration();
            _ = item.HandOffCompletion?.TrySetCanceled(NodeLifetimeCancellationToken);
        }
        catch (OperationCanceledException) when (IsTurnCancellation(item))
        {
            AbortAdmittedItemSilently(item);
        }
        catch (AudioConversionException ex)
        {
            PipelineDebugLog.LogOnlyLatency("TTS failed after", totalStopwatch);
            await ReportAdmittedFailureAsync(item, AudioFormatIncompatibleMessage, ex);
        }
        catch (Exception ex)
        {
            PipelineDebugLog.LogOnlyLatency("TTS failed after", totalStopwatch);
            await ReportAdmittedFailureAsync(item, ex.Message, ex);
        }
        finally
        {
            pipelineCancellationSource?.Dispose();
        }
    }

    /// <summary>
    /// Commits an admitted item at the playback hand-off boundary on the Godot thread.
    /// </summary>
    /// <param name="item">Admitted item reaching playback hand-off.</param>
    /// <param name="preparedPlayback">Prepared speech stream and lip-sync inference data.</param>
    private void CommitPlaybackHandOff(AdmittedSpeech item, LipSyncPlayer.PreparedPlayback preparedPlayback)
    {
        NodeLifetimeCancellationToken.ThrowIfCancellationRequested();

        bool commit;
        lock (_submissionLock)
        {
            commit = !item.CancelRequested;
            item.HandOffCommitted = commit;
        }

        if (!commit)
        {
            // The submission was cancelled while this hand-off was queued; abort before playback.
            throw new OperationCanceledException(item.TurnCancellation?.Token ?? CancellationToken.None);
        }

        try
        {
            PlayGeneratedSpeech(preparedPlayback);
            EnsurePlaybackStarted();
        }
        catch (Exception)
        {
            lock (_submissionLock)
            {
                item.HandOffCommitted = false;
            }

            throw;
        }

        lock (_submissionLock)
        {
            if (!item.Settled)
            {
                item.Settled = true;
                if (_outstandingItems > 0)
                {
                    _outstandingItems--;
                }
            }

            _playbackPending = true;
        }

        OnSpeechGenerated(item.Text);
        item.DisposeCancellationRegistration();
        _ = item.HandOffCompletion?.TrySetResult();
    }

    /// <summary>
    /// Verifies the lip-sync player actually started playback for the handed-off prepared speech.
    /// </summary>
    private void EnsurePlaybackStarted()
    {
        if (LipSyncPlayer is { } player && !string.IsNullOrEmpty(player.PlaybackError))
        {
            throw new InvalidOperationException($"AI voice playback hand-off failed: {player.PlaybackError}");
        }
    }

    private async Task ReportAdmittedFailureAsync(
        AdmittedSpeech item,
        string emittedError,
        Exception exception)
    {
        item.DisposeCancellationRegistration();
        SettleAdmittedItemWithoutPlayback(item);
        _ = item.HandOffCompletion?.TrySetException(exception);

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

    /// <summary>
    /// One admitted FIFO submission tracked from admission until playback hand-off, abort, failure, or teardown.
    /// </summary>
    private sealed class AdmittedSpeech(
        string text,
        CancellationTokenSource? turnCancellation,
        TaskCompletionSource? handOffCompletion,
        CancellationToken callerToken)
    {
        private CancellationTokenRegistration _cancellationRegistration;

        /// <summary>
        /// Validated speech text submitted for this item.
        /// </summary>
        public string Text { get; } = text;

        /// <summary>
        /// Linked turn-cancellation source for explicitly cancellable submissions; null for ordinary submissions.
        /// </summary>
        public CancellationTokenSource? TurnCancellation { get; } = turnCancellation;

        /// <summary>
        /// Completion source settling when playback hand-off commits; null for ordinary submissions.
        /// </summary>
        public TaskCompletionSource? HandOffCompletion { get; } = handOffCompletion;

        /// <summary>
        /// Caller-supplied token reproduced on silent pre-hand-off cancellation.
        /// </summary>
        public CancellationToken CallerToken { get; } = callerToken;

        /// <summary>
        /// Indicates the playback hand-off boundary has been crossed, after which cancellation cannot retract.
        /// Guarded by the owning voice's submission lock.
        /// </summary>
        public bool HandOffCommitted
        {
            get; set;
        }

        /// <summary>
        /// Indicates caller cancellation was observed before playback hand-off.
        /// Guarded by the owning voice's submission lock.
        /// </summary>
        public bool CancelRequested
        {
            get; set;
        }

        /// <summary>
        /// Indicates the item has reached exactly one of its terminal outcomes.
        /// Guarded by the owning voice's submission lock.
        /// </summary>
        public bool Settled
        {
            get; set;
        }

        public void SetCancellationRegistration(CancellationTokenRegistration registration)
            => _cancellationRegistration = registration;

        public void DisposeCancellationRegistration() => _cancellationRegistration.Dispose();
    }
}
