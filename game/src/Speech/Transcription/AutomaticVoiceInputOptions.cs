namespace AlleyCat.Speech.Transcription;

/// <summary>
/// Selects which player voice-input mechanisms are admitted.
/// </summary>
public enum VoiceInputMode
{
    /// <summary>Only the manual hold-to-speak button is admitted.</summary>
    ButtonOnly,

    /// <summary>Only locally detected speech is admitted.</summary>
    AutomaticOnly,

    /// <summary>Both manual and automatically detected speech are admitted.</summary>
    ButtonAndAutomatic,
}

/// <summary>
/// Immutable, validated tuning values for local automatic voice admission.
/// </summary>
public sealed class AutomaticVoiceInputOptions
{
    /// <summary>Creates the default tuning values.</summary>
    public AutomaticVoiceInputOptions()
        : this(0.5f, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(700), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), null)
    {
    }

    /// <summary>Creates validated automatic-input tuning values.</summary>
    public AutomaticVoiceInputOptions(
        float speechProbabilityThreshold,
        TimeSpan preRoll,
        TimeSpan minimumVoicedDuration,
        TimeSpan endpointSilence,
        TimeSpan continuationGap,
        TimeSpan maximumUtteranceDuration,
        TimeSpan? rearmSilence)
    {
        if (!float.IsFinite(speechProbabilityThreshold) || speechProbabilityThreshold < 0f || speechProbabilityThreshold > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speechProbabilityThreshold),
                "The speech probability threshold must be finite and between zero and one.");
        }

        ValidateDuration(preRoll, TimeSpan.FromSeconds(10), nameof(preRoll));
        ValidateDuration(minimumVoicedDuration, TimeSpan.FromSeconds(5), nameof(minimumVoicedDuration));
        ValidateDuration(endpointSilence, TimeSpan.FromSeconds(30), nameof(endpointSilence), mustBePositive: true);
        ValidateDuration(continuationGap, TimeSpan.FromSeconds(60), nameof(continuationGap), mustBePositive: true);
        ValidateDuration(maximumUtteranceDuration, TimeSpan.FromMinutes(2), nameof(maximumUtteranceDuration), mustBePositive: true);
        if (continuationGap <= endpointSilence)
        {
            throw new ArgumentOutOfRangeException(nameof(continuationGap), "Continuation gap must be greater than endpoint silence.");
        }

        if (rearmSilence is TimeSpan value)
        {
            ValidateDuration(value, TimeSpan.FromSeconds(30), nameof(rearmSilence));
        }

        SpeechProbabilityThreshold = speechProbabilityThreshold;
        PreRoll = preRoll;
        MinimumVoicedDuration = minimumVoicedDuration;
        EndpointSilence = endpointSilence;
        ContinuationGap = continuationGap;
        MaximumUtteranceDuration = maximumUtteranceDuration;
        RearmSilence = rearmSilence;
    }

    /// <summary>Gets the speech-probability threshold a frame must meet to count as voiced.</summary>
    public float SpeechProbabilityThreshold
    {
        get;
    }

    /// <summary>Gets the retained audio preceding a qualified onset.</summary>
    public TimeSpan PreRoll
    {
        get;
    }

    /// <summary>Gets the continuous voice required to qualify onset.</summary>
    public TimeSpan MinimumVoicedDuration
    {
        get;
    }

    /// <summary>Gets the silence which enters continuation.</summary>
    public TimeSpan EndpointSilence
    {
        get;
    }

    /// <summary>Gets the maximum pause that remains within one utterance.</summary>
    public TimeSpan ContinuationGap
    {
        get;
    }

    /// <summary>Gets the maximum duration of one automatic utterance.</summary>
    public TimeSpan MaximumUtteranceDuration
    {
        get;
    }

    /// <summary>Gets optional clean silence required after a force-close before rearming.</summary>
    public TimeSpan? RearmSilence
    {
        get;
    }

    private static void ValidateDuration(TimeSpan value, TimeSpan maximum, string parameterName, bool mustBePositive = false)
    {
        if (value < TimeSpan.Zero || (mustBePositive && value == TimeSpan.Zero) || value > maximum)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Duration must be {(mustBePositive ? "positive" : "non-negative")} and no greater than {maximum}.");
        }
    }
}
