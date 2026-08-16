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
        Transcriber.TranscriptionCompleted += _transcriptionCompletedHandler;
        Transcriber.TranscriptionFailed += _transcriptionFailedHandler;
        _connectedTranscriber = Transcriber;
    }

    private void DisconnectTranscriber()
    {
        if (_connectedTranscriber is null)
        {
            return;
        }

        _connectedTranscriber.RecordingStarted -= _recordingStartedHandler;
        _connectedTranscriber.TranscriptionCompleted -= _transcriptionCompletedHandler;
        _connectedTranscriber.TranscriptionFailed -= _transcriptionFailedHandler;
        _connectedTranscriber = null;

        // The disconnected transcriber can no longer deliver the completion or failure that closes a window opened
        // by its recording-started signal, so close it now to keep the turn-taking gate from jamming open.
        CloseSpeakingWindow();
    }

    /// <summary>
    /// Handles the transcriber's recording-started signal by opening this voice's speaking window.
    /// </summary>
    protected virtual void OnRecordingStarted() => OpenSpeakingWindow();

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
        if (string.IsNullOrWhiteSpace(text))
        {
            CloseSpeakingWindow();
            return;
        }

        Speak(text);
        CloseSpeakingWindow();
    }

    /// <summary>
    /// Handles failed transcription by closing the speaking window opened at recording start.
    /// </summary>
    /// <param name="error">Transcription failure message.</param>
    protected virtual void OnTranscriptionFailed(string error) => CloseSpeakingWindow();
}
