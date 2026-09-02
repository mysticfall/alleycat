namespace AlleyCat.Speech.Transcription;

/// <summary>Describes local voice activity for one captured frame.</summary>
/// <param name="IsVoiced">Indicates whether the detector judged this frame voiced at its configured threshold.</param>
/// <param name="SpeechProbability">Backend-specific speech probability of this frame, between zero and one.</param>
public readonly record struct VoiceActivityDetection(bool IsVoiced, float SpeechProbability);

/// <summary>Provides local voice activity decisions for mono audio frames.</summary>
/// <remarks>
/// The contract is deliberately backend-agnostic: implementations decide voicing per frame from a probability, while
/// utterance qualification and timing live entirely in <see cref="AutomaticUtteranceCoordinator" />.
/// </remarks>
public interface IVoiceActivityDetector
{
    /// <summary>Processes one mono frame without retaining the frame.</summary>
    VoiceActivityDetection Process(ReadOnlySpan<float> samples);

    /// <summary>Resets detector state between capture sessions.</summary>
    void Reset();
}
