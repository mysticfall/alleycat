using AlleyCat.Templating;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Renders ordered observation records through the AI-003 event-history contract for on-demand paths such as the
/// AI-002 <c>wait</c> and timeline history tools.
/// </summary>
/// <remarks>
/// The renderer compiles the exact-dispatch event-history source once per session from the Mind's authored
/// standalone <see cref="EventHistory" /> resource and renders each observation batch with the owning character
/// context, so actor-relative fragment wording matches the session system instruction exactly.
/// </remarks>
internal sealed class ObservationHistoryRenderer
{
    private readonly ITemplate _template;
    private readonly IReadOnlyDictionary<string, object?> _characterContext;

    private ObservationHistoryRenderer(ITemplate template, IReadOnlyDictionary<string, object?> characterContext)
    {
        _template = template;
        _characterContext = characterContext;
    }

    /// <summary>
    /// Creates a session renderer from the authored standalone event-history resource, or from the default
    /// authoring contract when the Mind declares no event history.
    /// </summary>
    /// <param name="eventHistory">Authored event history supplying fragments and fallback, or null.</param>
    /// <param name="compiler">Template compiler used to compile the event-history source.</param>
    /// <param name="characterContext">
    /// Owning character context dictionary from the sealed session render context, used for actor-relative wording.
    /// </param>
    public static ObservationHistoryRenderer Create(
        EventHistory? eventHistory,
        ITemplateCompiler compiler,
        IReadOnlyDictionary<string, object?> characterContext)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(characterContext);

        EventHistoryPromptFragment[] fragments = eventHistory?.Fragments ?? [];
        string fallbackSource = eventHistory?.FallbackSource ?? new EventHistory().FallbackSource;
        string source = EventHistory.BuildEventHistorySource(fragments, fallbackSource);
        return new ObservationHistoryRenderer(compiler.Compile(source), characterContext);
    }

    /// <summary>Renders the ordered observation records through the compiled event-history contract.</summary>
    /// <param name="observations">Observation records in timeline order.</param>
    /// <returns>The rendered event-history text for the supplied records.</returns>
    public string Render(IReadOnlyList<AgentObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        Dictionary<string, object?> context = new(StringComparer.Ordinal)
        {
            ["character"] = _characterContext,
            [EventHistory.ObservationsContextKey] = observations,
        };
        return _template.Render(context);
    }
}
