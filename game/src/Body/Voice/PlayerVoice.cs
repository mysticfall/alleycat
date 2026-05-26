using AlleyCat.Speech.Transcription;
using Godot;

namespace AlleyCat.Body.Voice;

/// <summary>
/// Voice implementation that speaks completed player transcription results.
/// </summary>
[GlobalClass]
public partial class PlayerVoice : Voice
{
    private Transcriber? _connectedTranscriber;
    private readonly Transcriber.TranscriptionCompletedEventHandler _transcriptionCompletedHandler;

    /// <summary>
    /// Transcriber that provides player speech text for this voice.
    /// </summary>
    [Export]
    public Transcriber? Transcriber
    {
        get;
        set;
    }

    /// <summary>
    /// Creates a player voice component.
    /// </summary>
    public PlayerVoice()
    {
        _transcriptionCompletedHandler = OnTranscriptionCompleted;
    }

    /// <inheritdoc />
    public override void _Ready() => ConnectTranscriber();

    /// <inheritdoc />
    public override void _ExitTree() => DisconnectTranscriber();

    /// <inheritdoc />
    public override void Speak(string speech)
        => _ = TryNotifySpeechGeneratedWhenEnabled(speech);

    private void ConnectTranscriber()
    {
        if (Transcriber is null || ReferenceEquals(_connectedTranscriber, Transcriber))
        {
            return;
        }

        DisconnectTranscriber();

        Transcriber.TranscriptionCompleted += _transcriptionCompletedHandler;
        _connectedTranscriber = Transcriber;
    }

    private void DisconnectTranscriber()
    {
        if (_connectedTranscriber is null)
        {
            return;
        }

        _connectedTranscriber.TranscriptionCompleted -= _transcriptionCompletedHandler;
        _connectedTranscriber = null;
    }

    /// <summary>
    /// Handles completed transcription text from the configured transcriber.
    /// </summary>
    /// <param name="text">Completed transcription text.</param>
    protected virtual void OnTranscriptionCompleted(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Speak(text);
    }
}
