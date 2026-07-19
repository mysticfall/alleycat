using AlleyCat.Templating;

namespace AlleyCat.Mind.Observation;

/// <summary>
/// Narrow prompt renderer for observations currently supported by AgenticMind.
/// </summary>
public interface IObservationPromptRenderer
{
    /// <summary>Renders a speech observation without inferring identity from raw voice provenance.</summary>
    string Render(SpeechObservation observation, ITemplateCompiler templateCompiler);
}
