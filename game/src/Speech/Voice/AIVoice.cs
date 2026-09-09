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
public partial class AIVoice : Voice, IAdmissionCapableVoice
{
    private const int ExpectedWaveFormatCode = 1;
    private const short ExpectedChannelCount = 1;
    private const short ExpectedBitsPerSample = 16;
    private const string AudioFormatIncompatibleMessage = "Audio format incompatible";

    private readonly Lock _submissionLock = new();
    private readonly Queue<AdmittedSpeech> _pendingSpeech = [];
    private bool _pumpRunning;
    private TaskCompletionSource? _pumpSettlement;
    private int _outstandingItems;

    /// <summary>
    /// Identity of the current admitted FIFO queue generation (SPCH-005 TR-32, TR-39), incremented each time the
    /// queue is flushed — through <see cref="CutSpeech" />, pre-hand-off caller cancellation, or node teardown —
    /// so items admitted into a flushed generation are recognised as stale and their deferred hand-offs refused.
    /// Submissions admitted after a flush start a fresh generation. Guarded by
    /// <see cref="_submissionLock" />.
    /// </summary>
    private int _queueGeneration;

    /// <summary>
    /// Voice-owned pipeline cancellation source for the queue item currently being processed (SPCH-005 TR-39):
    /// linked to no caller token — only the node lifetime — and cancelled exclusively by a queue-generation
    /// flush, never by an ordinary queued successor. Created when the pump dequeues an item and retired at that
    /// item's playback hand-off commit — or, for an item that never commits, by its pipeline's settlement —
    /// so a flush can only ever cancel uncommitted in-flight work and never a committed playback's stream.
    /// Guarded by <see cref="_submissionLock" />.
    /// </summary>
    private CancellationTokenSource? _activePipelineCancellation;

    /// <summary>
    /// Gate held by the utterance that has crossed its playback hand-off and has not yet raised
    /// <see cref="LipSyncPlayer.PlaybackCompleted" />; null when playback is available. Successor items await this
    /// gate before their own hand-off so direct-replacement playback never cuts the active utterance short (TR-30).
    /// Guarded by <see cref="_submissionLock" />.
    /// </summary>
    private TaskCompletionSource? _activePlaybackGate;

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
        _ = AdmitSpeech(acceptedSpeech, cancellable: false, cancellationToken);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public override ValueTask SpeakCancellableAsync(
        string speech,
        CancellationToken cancellationToken = default)
        => SpeakCancellableAsync(speech, cancellationToken, admission: null);

    /// <summary>
    /// Submits speech as an explicitly cancellable submission whose queue admission is arbitrated against attended
    /// start/resume suppression holds (SPCH-005 TR-37). Implements the optional voice admission
    /// capability discovered through <see cref="IAdmissionCapableVoice" /> — never a concrete-voice cast.
    /// </summary>
    /// <param name="speech">Speech text to submit.</param>
    /// <param name="cancellationToken">Caller-supplied cancellation observed through generation, conversion, and
    /// preparation until playback hand-off.</param>
    /// <param name="admission">Runner-owned admission transaction committing queue admission and protected state
    /// atomically under the normative lock order.</param>
    /// <returns>True when the submission was admitted and playback hand-off completed; false when a matching
    /// attended cue linearised first, in which case nothing was admitted — no TTS request, queue item, hearing
    /// event, or self-observation — and the caller reports its not-delivered outcome.</returns>
    async ValueTask<bool> IAdmissionCapableVoice.SpeakCancellableAdmittedAsync(
        string speech,
        CancellationToken cancellationToken,
        SpeechAdmissionTransaction admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        cancellationToken.ThrowIfCancellationRequested();
        string acceptedSpeech = ValidateSubmission(speech);
        AdmittedSpeech? item = AdmitSpeech(acceptedSpeech, cancellable: true, cancellationToken, admission);
        if (item is null)
        {
            // The attended cue won the arbitration (SPCH-005 TR-37): no queue item exists, so nothing further can
            // settle and the caller surfaces its not-delivered result rather than throwing.
            return false;
        }

        RegisterTurnCancellationCallback(item, cancellationToken);
        await new ValueTask(item.HandOffCompletion!.Task).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        base._ExitTree();

        AdmittedSpeech[] queuedItems;
        TaskCompletionSource? activePlaybackGate;
        lock (_submissionLock)
        {
            queuedItems = [.. _pendingSpeech];
            _pendingSpeech.Clear();
            _outstandingItems = 0;
            // Teardown is an authoritative terminal flush: the in-flight pipeline source is already cancelled
            // through its node-lifetime link, and the generation bump refuses any deferred hand-off that races
            // with teardown (TR-18, TR-32).
            _queueGeneration++;
            activePlaybackGate = _activePlaybackGate;
            _activePlaybackGate = null;
        }

        // Wake any item waiting behind the active playback with the node-lifetime token so the pump and its
        // cancellable submissions settle without post-lifetime dispatch.
        _ = activePlaybackGate?.TrySetCanceled(NodeLifetimeCancellationToken);

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
    /// Cuts active playback immediately, halting audio and lip-sync, and silently flushes every pending and
    /// in-progress queued utterance already admitted to this voice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the interruption-driven cut (SPCH-005 TR-32; the underlying stop/cut capability is defined in
    /// SPCH-001 and SPCH-002): the lip-sync player's stop does not raise
    /// <see cref="LipSyncPlayer.PlaybackCompleted" />, so the playback gate and speaking-window bookkeeping the
    /// notification would have performed are settled here exactly once. The whole admitted queue generation is
    /// invalidated — queued items are discarded as expected silent cancellation, with no <c>SpeechFailed</c>,
    /// no <see cref="IHearing" /> publication, no self-observation, and no retry — and speech submitted
    /// afterwards starts a fresh queue generation. Unlike caller cancellation, a cut intentionally stops
    /// committed playback. Must be called on the Godot thread.
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
        List<AdmittedSpeech> discarded;
        CancellationTokenSource? pipelineCancellation;
        bool closeWindow;
        lock (_submissionLock)
        {
            if (IsNodeLifetimeEnded)
            {
                return;
            }

            (discarded, pipelineCancellation) = FlushQueueLocked();
            // The lip-sync player's stop never raises PlaybackCompleted, so the gate must be released here: a
            // flushed item would otherwise wait forever for a notification that cannot arrive, and the window
            // must not stay pinned to a cut playback session.
            ReleaseActivePlaybackGateLocked();
            closeWindow = _outstandingItems == 0 && _activePlaybackGate is null;
        }

        CancelRetiredPipelineSource(pipelineCancellation);
        LipSyncPlayer?.Stop();

        foreach (AdmittedSpeech item in discarded)
        {
            AbortAdmittedItemSilently(item);
        }

        if (closeWindow)
        {
            CloseSpeakingWindow();
        }
    }

    private ValueTask SpeakCancellableAsync(
        string speech,
        CancellationToken cancellationToken,
        SpeechAdmissionTransaction? admission)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string acceptedSpeech = ValidateSubmission(speech);
        AdmittedSpeech? item = AdmitSpeech(acceptedSpeech, cancellable: true, cancellationToken, admission);
        Debug.Assert(item is not null, "Ordinary submissions are never refused admission.");
        RegisterTurnCancellationCallback(item!, cancellationToken);
        return new ValueTask(item!.HandOffCompletion!.Task);
    }

    /// <summary>
    /// Registers the guarded caller-cancellation callback for an admitted cancellable submission, invoking the
    /// handler directly when cancellation already fired between admission and registration so the queue flush can
    /// never be missed (SPCH-005 TR-39).
    /// </summary>
    /// <param name="item">Admitted item whose caller token is observed.</param>
    /// <param name="cancellationToken">Caller-supplied cancellation token.</param>
    private void RegisterTurnCancellationCallback(AdmittedSpeech item, CancellationToken cancellationToken)
    {
        item.SetCancellationRegistration(cancellationToken.Register(
            () => HandleTurnCancellationRequested(item),
            useSynchronizationContext: false));
        if (cancellationToken.IsCancellationRequested)
        {
            // Cancellation raced the registration itself; the callback may not have observed it, so linearise
            // the withdrawal here. The handler is idempotent, so a callback that already ran changes nothing.
            HandleTurnCancellationRequested(item);
        }
    }

    /// <summary>
    /// Atomically admits a validated speech request as the next FIFO queue item and opens the speaking window.
    /// </summary>
    /// <param name="speech">Validated speech text to admit.</param>
    /// <param name="cancellable">Indicates an explicitly cancellable submission whose completion waits for
    /// playback hand-off.</param>
    /// <param name="callerToken">Caller-supplied cancellation observed until admission commits and, for
    /// cancellable submissions, through the guarded callback until playback hand-off.</param>
    /// <param name="admission">Runner-owned admission transaction (SPCH-005 TR-37), or null for ordinary
    /// admission-only submissions. A gated submission commits its queue item inside the transaction — under this
    /// submission lock first and then the agent-runner state lock — and returns null, committing nothing, when a
    /// matching attended cue hold linearised first.</param>
    /// <returns>The admitted queue item, or null when the admission transaction refused a gated submission.</returns>
    private AdmittedSpeech? AdmitSpeech(
        string speech,
        bool cancellable,
        CancellationToken callerToken,
        SpeechAdmissionTransaction? admission = null)
    {
        bool startPump = false;
        AdmittedSpeech? item;
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
            AdmittedSpeech admitted = new(
                speech,
                _queueGeneration,
                cancellable ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null,
                callerToken);
            item = admitted;
            if (admission is { } gate)
            {
                if (!gate.TryAdmit(() => AdmitItemLocked(admitted, ref startPump)))
                {
                    // The attended cue hold linearised first (SPCH-005 TR-37): no queue item, FIFO disturbance, or
                    // window effect exists, so FIFO draining, window state, and teardown contracts are unaffected.
                    return null;
                }
            }
            else
            {
                AdmitItemLocked(admitted, ref startPump);
            }
        }

        OpenSpeakingWindow();

        if (startPump)
        {
            _ = DrainSpeechQueueAsync();
        }

        return item;
    }

    /// <summary>Enqueues one validated item under the submission lock: FIFO bookkeeping plus pump startup.</summary>
    /// <remarks>Must be called while holding <see cref="_submissionLock"/>.</remarks>
    private void AdmitItemLocked(AdmittedSpeech item, ref bool startPump)
    {
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

            ReleaseActivePlaybackGateLocked();
            closeWindow = _outstandingItems == 0;
        }

        if (closeWindow)
        {
            CloseSpeakingWindow();
        }
    }

    /// <summary>
    /// Waits until the active utterance's playback has completed so the caller's playback hand-off cannot replace
    /// it early.
    /// </summary>
    /// <param name="cancellationToken">Pipeline cancellation that withdraws the waiting item before hand-off.</param>
    private async Task AwaitActivePlaybackGateAsync(CancellationToken cancellationToken)
    {
        Task playbackGateTask;
        lock (_submissionLock)
        {
            playbackGateTask = _activePlaybackGate?.Task ?? Task.CompletedTask;
        }

        if (!playbackGateTask.IsCompletedSuccessfully)
        {
            await playbackGateTask.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Releases the active playback gate so prepared successors may hand off, completing any waiting item.
    /// </summary>
    /// <remarks>
    /// <para>Must be called while holding <see cref="_submissionLock" />.</para>
    /// <para>Duplicate or stale completion notifications are harmless: the gate is detached by identity before
    /// completion, so a second callback finds no gate to release.</para>
    /// </remarks>
    private void ReleaseActivePlaybackGateLocked()
    {
        if (_activePlaybackGate is { } gate)
        {
            ReleaseReservedPlaybackGateLocked(gate);
        }
    }

    /// <summary>
    /// Releases a gate reserved for one specific hand-off attempt, identity-checked so already-released or
    /// re-reserved gates are left untouched.
    /// </summary>
    /// <remarks>Must be called while holding <see cref="_submissionLock" />.</remarks>
    private void ReleaseReservedPlaybackGateLocked(TaskCompletionSource gate)
    {
        if (!ReferenceEquals(_activePlaybackGate, gate))
        {
            return;
        }

        _activePlaybackGate = null;
        _ = gate.TrySetResult();
    }

    /// <summary>
    /// Handles caller cancellation of an explicitly cancellable submission, linearised against the playback
    /// hand-off under the submission lock (SPCH-005 TR-25, TR-32, TR-39).
    /// </summary>
    /// <param name="item">Admitted item whose caller token was cancelled.</param>
    /// <remarks>
    /// <para>
    /// Observed before the item's hand-off commits, the item is marked stale and the voice's whole queue
    /// generation is flushed: every pending and in-progress item settles as expected silent cancellation while
    /// committed playback is never cut and plays to completion. Observed after hand-off, the callback is a no-op
    /// that cuts no playing audio, cancels no streaming session, and retracts no publication.
    /// </para>
    /// <para>
    /// The callback may execute on the cancelling caller's thread; it stops no playback and touches no scene-tree
    /// state, so no Godot main-thread marshalling is required on this path.</para>
    /// </remarks>
    private void HandleTurnCancellationRequested(AdmittedSpeech item)
    {
        List<AdmittedSpeech> discarded;
        CancellationTokenSource? pipelineCancellation;
        lock (_submissionLock)
        {
            if (IsNodeLifetimeEnded || item.HandOffCommitted || item.Settled || item.Generation != _queueGeneration)
            {
                // Post-hand-off cancellation is a no-op (TR-25/TR-39); a settled or stale item already belongs to
                // a flushed generation and must not invalidate the fresh generation that followed it.
                return;
            }

            item.CancelRequested = true;
            (discarded, pipelineCancellation) = FlushQueueLocked();
        }

        CancelRetiredPipelineSource(pipelineCancellation);

        foreach (AdmittedSpeech queuedItem in discarded)
        {
            AbortAdmittedItemSilently(queuedItem);
        }

        // The cancelled item itself is either in the discarded list (still queued) or in flight, observing the
        // cancelled pipeline source at its next boundary or the hand-off refusal before playback commits.
    }

    /// <summary>
    /// Invalidates the current queue generation (SPCH-005 TR-32): every queued item is handed back for silent
    /// settlement and the voice-owned pipeline source of uncommitted in-flight generation, preparation, and
    /// lip-sync work is returned for cancellation by the caller. The source is null when the in-flight item
    /// already committed its playback hand-off — committed playback is never cut by a flush. Later submissions
    /// admit into a fresh generation.
    /// </summary>
    /// <remarks>
    /// <para>Must be called while holding <see cref="_submissionLock" />.</para>
    /// <para>The returned source must be cancelled only after the lock is released: cancellation callbacks and
    /// awaited-work continuations must never run inline against held voice state.</para>
    /// </remarks>
    private (List<AdmittedSpeech> Discarded, CancellationTokenSource? PipelineCancellation) FlushQueueLocked()
    {
        _queueGeneration++;
        List<AdmittedSpeech> discarded = [.. _pendingSpeech];
        _pendingSpeech.Clear();
        return (discarded, _activePipelineCancellation);
    }

    /// <summary>
    /// Cancels a pipeline source retired by a queue flush, tolerating the concurrent settlement race.
    /// </summary>
    private static void CancelRetiredPipelineSource(CancellationTokenSource? pipelineCancellation)
    {
        if (pipelineCancellation is null)
        {
            return;
        }

        try
        {
            pipelineCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The in-flight item settled concurrently and disposed its own source; there is no pipeline work
            // left to cancel.
        }
    }

    /// <summary>
    /// Indicates an item's pipeline was withdrawn as expected silent cancellation: its own caller cancelled before
    /// hand-off, or the queue generation it belonged to was flushed (SPCH-005 TR-32, TR-39).
    /// </summary>
    private static bool IsSilentWithdrawal(AdmittedSpeech item, CancellationToken pipelineCancellation)
        => item.CancelRequested || item.Settled || pipelineCancellation.IsCancellationRequested;

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

            closeWindow = _outstandingItems == 0 && _activePlaybackGate is null;
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
            CancellationTokenSource pipelineCancellationSource;
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
                // The voice owns this pipeline source (TR-39): linked to the node lifetime only — never to a
                // caller token — and cancelled exclusively when the item's queue generation is flushed.
                pipelineCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(NodeLifetimeCancellationToken);
                _activePipelineCancellation = pipelineCancellationSource;
            }

            await ProcessAdmittedSpeechAsync(item, pipelineCancellationSource);
        }
    }

    private async Task ProcessAdmittedSpeechAsync(
        AdmittedSpeech item,
        CancellationTokenSource pipelineCancellationSource)
    {
        Stopwatch totalStopwatch = PipelineDebugLog.StartTimer();
        CancellationToken pipelineCancellation = pipelineCancellationSource.Token;

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

            // Preparation may overlap the active utterance's playback; the hand-off below now waits for that
            // playback to complete so the successor cannot cut its predecessor short (TR-30).
            await AwaitActivePlaybackGateAsync(pipelineCancellation);

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
                    CommitPlaybackHandOff(item, preparedPlayback, pipelineCancellationSource);
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
                        $"{preparedPlayback.PreparedFrameCount} frames, {mappedMeshCount} mesh(es)",
                        $"{preparedPlayback.PreparedFrameCount} frames");
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
        catch (OperationCanceledException) when (IsSilentWithdrawal(item, pipelineCancellation))
        {
            AbortAdmittedItemSilently(item);
        }
        catch (Exception) when (IsSilentWithdrawal(item, pipelineCancellation))
        {
            // A backend fault racing the flush still settles silently: flushed work never surfaces a failure,
            // a listener publication, or a retry (SPCH-005 TR-32).
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
            lock (_submissionLock)
            {
                // The identity guard tolerates the field having been retired earlier at the playback hand-off
                // commit; this clear only covers pipelines that never committed.
                if (ReferenceEquals(_activePipelineCancellation, pipelineCancellationSource))
                {
                    _activePipelineCancellation = null;
                }
            }

            // Disposed only after the field is cleared under the lock, so a concurrent flush can no longer reach
            // the source through voice state; disposal stays here so the token remains observable until the
            // pipeline task truly ends.
            pipelineCancellationSource.Dispose();
        }
    }

    /// <summary>
    /// Commits an admitted item at the playback hand-off boundary on the Godot thread.
    /// </summary>
    /// <param name="item">Admitted item reaching playback hand-off.</param>
    /// <param name="preparedPlayback">Prepared speech stream and lip-sync inference data.</param>
    /// <param name="pipelineCancellationSource">Voice-owned pipeline source of this item's pipeline, retired here
    /// when the commit is accepted so a later queue flush can never cancel committed playback's stream.</param>
    private void CommitPlaybackHandOff(
        AdmittedSpeech item,
        LipSyncPlayer.PreparedPlayback preparedPlayback,
        CancellationTokenSource pipelineCancellationSource)
    {
        NodeLifetimeCancellationToken.ThrowIfCancellationRequested();

        bool commit;
        lock (_submissionLock)
        {
            // Linearised against the guarded cancellation callback (TR-39): cancellation observed first marked
            // the item stale or flushed its queue generation, refusing playback; a hand-off that linearised first
            // sets HandOffCommitted and makes any later caller cancellation a no-op.
            commit = !item.CancelRequested && item.Generation == _queueGeneration;
            item.HandOffCommitted = commit;
            if (commit && ReferenceEquals(_activePipelineCancellation, pipelineCancellationSource))
            {
                // The commit boundary retires the pipeline source (TR-32/TR-39): from here the item's playback
                // is committed, so a queued sibling's cancellation flushing the generation must not reach this
                // source and cut the committed stream mid-playback. CutSpeech stays authoritative for committed
                // playback because it stops the lip-sync player explicitly rather than through this source, and
                // disposal remains with the pipeline's settlement so the token stays observable until the
                // pipeline task truly ends.
                _activePipelineCancellation = null;
            }
        }

        if (!commit)
        {
            // The submission was cancelled, or its queue generation was flushed, while this hand-off was queued;
            // abort before playback.
            throw new OperationCanceledException(item.CallerToken);
        }

        // The successor gate is reserved before playback starts: a playback-completed notification raised
        // synchronously by PlayGeneratedSpeech then releases this gate instead of arriving before any gate exists,
        // and later FIFO items keep waiting on the correct playback session.
        TaskCompletionSource playbackGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_submissionLock)
        {
            _activePlaybackGate = playbackGate;
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
                ReleaseReservedPlaybackGateLocked(playbackGate);
            }

            throw;
        }

        bool closeWindow;
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

            // The reserved gate keeps the window open until the playback-completed notification; when that
            // notification already arrived synchronously during playback start, the window closes here instead.
            closeWindow = _outstandingItems == 0 && _activePlaybackGate is null;
        }

        if (closeWindow)
        {
            CloseSpeakingWindow();
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
        return new ParsedSpeechData(waveFile.PcmData, waveFile.SampleRate, Stereo: false, BitsPerSample: ExpectedBitsPerSample);
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
        return new WaveFileData(pcmData, fmtChunk.SampleRate);
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

    private sealed record WaveFileData(byte[] PcmData, int SampleRate);

    private sealed record FmtChunkData(short FormatCode, short ChannelCount, int SampleRate, short BitsPerSample);

    internal sealed record ParsedSpeechData(byte[] PcmData, int SampleRate, bool Stereo, short BitsPerSample);

    internal sealed class AudioConversionException(string message) : Exception(message);

    /// <summary>
    /// One admitted FIFO submission tracked from admission until playback hand-off, abort, failure, or teardown.
    /// </summary>
    private sealed class AdmittedSpeech(
        string text,
        int generation,
        TaskCompletionSource? handOffCompletion,
        CancellationToken callerToken)
    {
        private CancellationTokenRegistration _cancellationRegistration;

        /// <summary>
        /// Validated speech text submitted for this item.
        /// </summary>
        public string Text { get; } = text;

        /// <summary>
        /// Queue generation this item was admitted into; a flush invalidates it (SPCH-005 TR-32), so a mismatch
        /// with the voice's current generation marks the item stale and refuses its deferred playback hand-off.
        /// Guarded by the owning voice's submission lock.
        /// </summary>
        public int Generation { get; } = generation;

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
