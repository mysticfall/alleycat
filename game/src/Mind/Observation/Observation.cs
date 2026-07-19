using AlleyCat.Templating;

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
    public abstract string ToPromptString(
        IObservationPromptRenderer renderer,
        ITemplateCompiler templateCompiler);
}

/// <summary>
/// Observation produced when speech is heard from another voice.
/// </summary>
public sealed record SpeechObservation(
    string VoiceId,
    string? CharacterId,
    string Content,
    float Weight = 1f) : Observation(Weight)
{
    /// <inheritdoc />
    public override string ToPromptString(
        IObservationPromptRenderer renderer,
        ITemplateCompiler templateCompiler)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(templateCompiler);

        return renderer.Render(this, templateCompiler);
    }
}
