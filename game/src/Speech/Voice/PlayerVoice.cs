using System.Diagnostics.CodeAnalysis;
using AlleyCat.Speech.Transcription;
using Godot;

namespace AlleyCat.Speech.Voice;

/// <summary>
/// Voice implementation that speaks completed player transcription results.
/// </summary>
[GlobalClass]
public partial class PlayerVoice : Voice
{
    [SuppressMessage("Style", "IDE0032:Use auto property", Justification = "Transcriber setter keeps the runtime signal subscription in sync.")]
    private Transcriber? _transcriber;
    private Transcriber? _connectedTranscriber;
    private readonly Transcriber.TranscriptionCompletedEventHandler _transcriptionCompletedHandler;
    private readonly Transcriber.TranscriptionFailedEventHandler _transcriptionFailedHandler;
    private readonly Transcriber.RecordingStartedEventHandler _recordingStartedHandler;
    private readonly Transcriber.RecordingAbandonedEventHandler _recordingAbandonedHandler;
    private readonly Transcriber.AutomaticSegmentCompletedEventHandler _automaticSegmentCompletedHandler;
    private readonly Transcriber.AutomaticSegmentFailedEventHandler _automaticSegmentFailedHandler;
    private readonly Transcriber.AutomaticGroupOpenedEventHandler _automaticGroupOpenedHandler;
    private readonly Transcriber.AutomaticGroupClosedEventHandler _automaticGroupClosedHandler;
    private readonly Transcriber.AutomaticGroupAbandonedEventHandler _automaticGroupAbandonedHandler;
    private readonly Transcriber.AutomaticSpeechResumedEventHandler _automaticSpeechResumedHandler;
    private readonly HashSet<string> _openAutomaticGroups = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settlingAutomaticGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<SpeechSegmentMetadata>> _unsettledAutomaticSegments = new(StringComparer.Ordinal);
    private bool _manualRecording;
    private string? _activeManualSegmentToken;

    /// <summary>
    /// Transcriber that provides player speech text for this voice.
    /// </summary>
    [Export]
    public Transcriber? Transcriber
    {
        get => _transcriber;
        set
        {
            if (ReferenceEquals(_transcriber, value))
            {
                return;
            }

            if (_connectedTranscriber is not null)
            {
                DisconnectTranscriber();
            }

            _transcriber = value;
            ConnectTranscriber();
        }
    }

    /// <summary>
    /// Creates a player voice component.
    /// </summary>
    public PlayerVoice()
    {
        _transcriptionCompletedHandler = OnTranscriptionCompleted;
        _transcriptionFailedHandler = OnTranscriptionFailed;
        _recordingStartedHandler = OnRecordingStarted;
        _recordingAbandonedHandler = OnRecordingAbandoned;
        _automaticSegmentCompletedHandler = OnAutomaticSegmentCompleted;
        _automaticSegmentFailedHandler = OnAutomaticSegmentFailed;
        _automaticGroupOpenedHandler = OnAutomaticGroupOpened;
        _automaticGroupClosedHandler = OnAutomaticGroupClosed;
        _automaticGroupAbandonedHandler = OnAutomaticGroupAbandoned;
        _automaticSpeechResumedHandler = OnAutomaticSpeechResumed;
    }

    /// <inheritdoc />
    public override void _Ready() => ConnectTranscriber();

    /// <inheritdoc />
    public override void _ExitTree()
    {
        DisconnectTranscriber();
        base._ExitTree();
    }

    private void ConnectTranscriber()
    {
        if (Transcriber is null || ReferenceEquals(_connectedTranscriber, Transcriber))
        {
            return;
        }

        DisconnectTranscriber();

        Transcriber.RecordingStarted += _recordingStartedHandler;
        Transcriber.RecordingAbandoned += _recordingAbandonedHandler;
        Transcriber.TranscriptionCompleted += _transcriptionCompletedHandler;
        Transcriber.TranscriptionFailed += _transcriptionFailedHandler;
        Transcriber.AutomaticSegmentCompleted += _automaticSegmentCompletedHandler;
        Transcriber.AutomaticSegmentFailed += _automaticSegmentFailedHandler;
        Transcriber.AutomaticGroupOpened += _automaticGroupOpenedHandler;
        Transcriber.AutomaticGroupClosed += _automaticGroupClosedHandler;
        Transcriber.AutomaticGroupAbandoned += _automaticGroupAbandonedHandler;
        Transcriber.AutomaticSpeechResumed += _automaticSpeechResumedHandler;
        _connectedTranscriber = Transcriber;
    }

    private void DisconnectTranscriber()
    {
        if (_connectedTranscriber is null)
        {
            return;
        }

        // The disconnected transcriber can no longer deliver the outcome that settles an open manual segment, so
        // settle it as abandoned exactly once before its signals detach.
        SettleManualSegment(SpeechSegmentSettlementKind.Abandoned);
        _connectedTranscriber.RecordingStarted -= _recordingStartedHandler;
        _connectedTranscriber.RecordingAbandoned -= _recordingAbandonedHandler;
        _connectedTranscriber.TranscriptionCompleted -= _transcriptionCompletedHandler;
        _connectedTranscriber.TranscriptionFailed -= _transcriptionFailedHandler;
        _connectedTranscriber.AutomaticSegmentCompleted -= _automaticSegmentCompletedHandler;
        _connectedTranscriber.AutomaticSegmentFailed -= _automaticSegmentFailedHandler;
        _connectedTranscriber.AutomaticGroupOpened -= _automaticGroupOpenedHandler;
        _connectedTranscriber.AutomaticGroupClosed -= _automaticGroupClosedHandler;
        _connectedTranscriber.AutomaticGroupAbandoned -= _automaticGroupAbandonedHandler;
        _connectedTranscriber.AutomaticSpeechResumed -= _automaticSpeechResumedHandler;
        _connectedTranscriber = null;
        _openAutomaticGroups.Clear();
        _settlingAutomaticGroups.Clear();
        _unsettledAutomaticSegments.Clear();
        _manualRecording = false;

        // The disconnected transcriber can no longer deliver the completion or failure that closes a window opened
        // by its recording-started signal, so close it now to keep the turn-taking gate from jamming open.
        CloseSpeakingWindow();
    }

    /// <summary>
    /// Handles the transcriber's recording-started signal by opening this voice's speaking window.
    /// </summary>
    /// <remarks>
    /// Both manual and automatic capture paths emit the ordinary recording-started signal, and the window is
    /// idempotent, so a manual recording start cannot open a second public window while one is already open. A
    /// manual start mints a fresh opaque synthetic segment token and raises its textless start cue before the
    /// window event, mirroring how automatic onsets announce their real group identity first.
    /// </remarks>
    protected virtual void OnRecordingStarted()
    {
        // Automatic groups announce themselves immediately before this compatibility signal; otherwise this is manual.
        _manualRecording = _openAutomaticGroups.Count == 0 && _settlingAutomaticGroups.Count == 0;
        if (_manualRecording)
        {
            StartManualSegment();
        }

        OpenSpeakingWindow();
    }

    /// <summary>
    /// Handles completed transcription text from the configured transcriber.
    /// </summary>
    /// <param name="text">Completed transcription text.</param>
    /// <remarks>
    /// Nonblank text is forwarded through <see cref="Voice.Speak"/>, whose post-generation broadcast closes the
    /// speaking window. Blank transcripts, disabled output, and submission failures must never leave the window
    /// open, so the trailing window close is an idempotent safety net for every nonbroadcast outcome.
    /// </remarks>
    protected virtual void OnTranscriptionCompleted(string text)
    {
        _manualRecording = false;
        if (!string.IsNullOrWhiteSpace(text))
        {
            Speak(text);
            SettleManualSegment(SpeechSegmentSettlementKind.Published);
        }
        else
        {
            SettleManualSegment(SpeechSegmentSettlementKind.Blank);
        }

        CloseWindowIfSettled();
    }

    /// <summary>
    /// Handles failed transcription by closing the speaking window opened at recording start.
    /// </summary>
    /// <param name="error">Transcription failure message.</param>
    protected virtual void OnTranscriptionFailed(string error)
    {
        _manualRecording = false;
        SettleManualSegment(SpeechSegmentSettlementKind.Failed);
        CloseWindowIfSettled();
    }

    /// <summary>
    /// Handles the transcriber's explicit manual-abandonment signal by settling the pending synthetic segment and
    /// closing the window the silent stop would otherwise leave open.
    /// </summary>
    protected virtual void OnRecordingAbandoned()
    {
        _manualRecording = false;
        SettleManualSegment(SpeechSegmentSettlementKind.Abandoned);
        CloseWindowIfSettled();
    }

    private void OnAutomaticGroupOpened(string speechGroupID)
    {
        _ = _openAutomaticGroups.Add(speechGroupID);
        SpeechSegmentMetadata onset = new(speechGroupID, 0);
        _unsettledAutomaticSegments[speechGroupID] = [onset];
        // The qualified onset cue precedes the compatibility recording-started window event that follows this
        // signal, so listeners observe the real segment identity before the public window opens.
        RaiseSpeechSegmentStarted(onset);
    }

    private void OnAutomaticGroupClosed(string speechGroupID, bool outcomesWerePublished)
    {
        _ = _openAutomaticGroups.Remove(speechGroupID);
        if (outcomesWerePublished)
        {
            CloseWindowIfSettled();
        }
        else
        {
            _ = _settlingAutomaticGroups.Add(speechGroupID);
        }
    }

    private void OnAutomaticGroupAbandoned(string speechGroupID)
    {
        _ = _openAutomaticGroups.Remove(speechGroupID);
        _ = _settlingAutomaticGroups.Remove(speechGroupID);
        if (!_unsettledAutomaticSegments.Remove(speechGroupID, out HashSet<SpeechSegmentMetadata>? unsettled))
        {
            return;
        }

        foreach (SpeechSegmentMetadata metadata in unsettled)
        {
            RaiseSpeechSegmentSettled(new SpeechSegmentSettlement(metadata, SpeechSegmentSettlementKind.Abandoned));
        }
    }

    private void OnAutomaticSegmentCompleted(string text, string speechGroupID, int segmentIndex, bool continued)
    {
        SpeechSegmentMetadata metadata = new(speechGroupID, segmentIndex);
        RegisterAutomaticSegment(metadata);
        bool final = _settlingAutomaticGroups.Remove(speechGroupID);
        if (string.IsNullOrWhiteSpace(text))
        {
            SettleAutomaticSegment(metadata, SpeechSegmentSettlementKind.Blank);
            if (final)
            {
                CloseWindowIfSettled();
            }

            return;
        }

        if (final && !_manualRecording && _openAutomaticGroups.Count == 0 && _settlingAutomaticGroups.Count == 0)
        {
            CloseSpeakingWindow();
        }

        PublishSpeech(text.Trim(), metadata);
        SettleAutomaticSegment(metadata, SpeechSegmentSettlementKind.Published);
    }

    private void OnAutomaticSegmentFailed(string error, string speechGroupID, int segmentIndex, bool continued)
    {
        SpeechSegmentMetadata metadata = new(speechGroupID, segmentIndex);
        RegisterAutomaticSegment(metadata);
        SettleAutomaticSegment(metadata, SpeechSegmentSettlementKind.Failed);
        if (_settlingAutomaticGroups.Remove(speechGroupID))
        {
            CloseWindowIfSettled();
        }
    }

    private void OnAutomaticSpeechResumed(string speechGroupID, int segmentIndex, bool continued)
    {
        SpeechSegmentMetadata metadata = new(speechGroupID, segmentIndex);
        RegisterAutomaticSegment(metadata);
        RaiseSpeechResumed(metadata);
    }

    private void RegisterAutomaticSegment(SpeechSegmentMetadata metadata)
    {
        if (!_unsettledAutomaticSegments.TryGetValue(metadata.SpeechGroupID, out HashSet<SpeechSegmentMetadata>? unsettled))
        {
            return;
        }

        _ = unsettled.Add(metadata);
    }

    private void SettleAutomaticSegment(SpeechSegmentMetadata metadata, SpeechSegmentSettlementKind kind)
    {
        if (!_unsettledAutomaticSegments.TryGetValue(metadata.SpeechGroupID, out HashSet<SpeechSegmentMetadata>? unsettled)
            || !unsettled.Remove(metadata))
        {
            return;
        }

        if (unsettled.Count == 0)
        {
            _ = _unsettledAutomaticSegments.Remove(metadata.SpeechGroupID);
        }

        RaiseSpeechSegmentSettled(new SpeechSegmentSettlement(metadata, kind));
    }

    /// <summary>
    /// Mints the fresh opaque synthetic token for a manual recording press and raises its textless start cue.
    /// </summary>
    /// <remarks>
    /// The token stays internal correlation state: manual completed speech is published ungrouped, so the token
    /// never appears in hearing publications or percepts.
    /// </remarks>
    private void StartManualSegment()
    {
        // A stale token can only remain pending when the prior manual session never reached a public outcome; the
        // fresh press supersedes it through an abandonment settled exactly once.
        SettleManualSegment(SpeechSegmentSettlementKind.Abandoned);
        string token = Guid.NewGuid().ToString("N");
        _activeManualSegmentToken = token;
        RaiseSpeechSegmentStarted(new SpeechSegmentMetadata(token, 0));
    }

    /// <summary>Settles the pending synthetic manual segment exactly once, if one is active.</summary>
    private void SettleManualSegment(SpeechSegmentSettlementKind kind)
    {
        string? token = _activeManualSegmentToken;
        if (token is null)
        {
            return;
        }

        _activeManualSegmentToken = null;
        RaiseSpeechSegmentSettled(new SpeechSegmentSettlement(new SpeechSegmentMetadata(token, 0), kind));
    }

    private void CloseWindowIfSettled()
    {
        if (!_manualRecording && _openAutomaticGroups.Count == 0 && _settlingAutomaticGroups.Count == 0)
        {
            CloseSpeakingWindow();
        }
    }
}
