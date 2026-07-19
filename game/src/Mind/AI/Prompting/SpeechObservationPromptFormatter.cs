using AlleyCat.Mind.Observation;
using AlleyCat.Templating;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Godot-authorable Handlebars source used to present heard speech to an agent.
/// </summary>
[GlobalClass]
public sealed partial class SpeechObservationPromptFormatter : Resource, IObservationPromptRenderer
{
    private readonly Lock _templateLock = new();
    private ITemplate? _template;

    /// <summary>Handlebars source rendered once for each speech observation.</summary>
    [Export(PropertyHint.MultilineText)]
    public string Source
    {
        get; set;
    } =
        "{{#if CharacterId}}Speech from {{CharacterId}}: {{Content}}{{else}}Speech from an unknown voice: {{Content}}{{/if}}";

    /// <inheritdoc />
    public string Render(SpeechObservation observation, ITemplateCompiler templateCompiler)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(templateCompiler);

        ITemplate template;
        lock (_templateLock)
        {
            template = _template ??= templateCompiler.Compile(Source);
        }

        Dictionary<string, object?> context = new(StringComparer.Ordinal)
        {
            [nameof(SpeechObservation.VoiceId)] = observation.VoiceId,
            [nameof(SpeechObservation.CharacterId)] = observation.CharacterId,
            [nameof(SpeechObservation.Content)] = observation.Content,
            [nameof(SpeechObservation.Weight)] = observation.Weight,
        };

        return template.Render(context);
    }
}
