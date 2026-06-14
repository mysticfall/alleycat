namespace AlleyCat.Mind.Observation;

/// <summary>
/// Base contract for sensory data perceived by an agent.
/// </summary>
public abstract record Observation(float Weight)
{
    /// <summary>
    /// Significance used by the runtime to decide whether processing should run promptly.
    /// </summary>
    public float Weight { get; } = Weight < 0f ? throw new ArgumentOutOfRangeException(nameof(Weight)) : Weight;

    /// <summary>
    /// Renders this observation for an agent prompt without requiring concrete-type switches by the runtime.
    /// </summary>
    /// <returns>Prompt-ready text describing this observation.</returns>
    public abstract string ToPromptString();
}

/// <summary>
/// Observation produced when speech is heard from another voice.
/// </summary>
public sealed record SpeechObservation(string SpeakerId, string Content, float Weight = 1f) : Observation(Weight)
{
    /// <inheritdoc />
    public override string ToPromptString() => $"Speech from {SpeakerId}: {Content}";
}
