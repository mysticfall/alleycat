using System.Text;
using AlleyCat.Templating;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Renders ordered observation records through the AI-003 event-history contract for on-demand paths such as the
/// AI-002 <c>wait</c> and timeline history tools.
/// </summary>
/// <remarks>
/// The renderer compiles each authored fragment and the fallback once per session as standalone templates from the
/// Mind's authored <see cref="EventHistory" /> resource. Every record selects its template by exact, case-sensitive
/// <c>TypeKey</c> dictionary lookup — unknown types render the fallback — and passes directly to that template as
/// the rooted render context, so fragment-visible record properties resolve at the template's top level exactly
/// like the pre-migration current-context semantics. No dispatch source is generated at runtime, no reflection
/// projection happens here, and no global partial registration exists; the owning character context rides along as
/// a named value so actor-relative wording matches the session system instruction.
/// </remarks>
internal sealed class ObservationHistoryRenderer
{
    private const string CharacterContextKey = "character";

    private readonly IReadOnlyDictionary<string, ITemplate> _fragments;
    private readonly ITemplate _fallback;
    private readonly IReadOnlyDictionary<string, object?> _characterContext;

    private ObservationHistoryRenderer(
        IReadOnlyDictionary<string, ITemplate> fragments,
        ITemplate fallback,
        IReadOnlyDictionary<string, object?> characterContext)
    {
        _fragments = fragments;
        _fallback = fallback;
        _characterContext = characterContext;
    }

    /// <summary>
    /// Creates a session renderer from the authored standalone event-history resource, or from the default
    /// authoring contract when the Mind declares no event history.
    /// </summary>
    /// <param name="eventHistory">Authored event history supplying fragments and fallback, or null.</param>
    /// <param name="compiler">Template compiler used to compile each fragment and the fallback individually.</param>
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
        EventHistory.ValidateAuthoring(fragments, fallbackSource);

        Dictionary<string, ITemplate> compiledFragments = new(StringComparer.Ordinal);
        foreach (EventHistoryPromptFragment fragment in fragments)
        {
            compiledFragments.Add(fragment.TypeKey, compiler.Compile(fragment.Source));
        }

        return new ObservationHistoryRenderer(compiledFragments, compiler.Compile(fallbackSource), characterContext);
    }

    /// <summary>Renders the ordered observation records through their individually compiled templates.</summary>
    /// <param name="observations">Observation records in timeline order.</param>
    /// <returns>The rendered event-history text for the supplied records.</returns>
    public async ValueTask<string> RenderAsync(IReadOnlyList<AgentObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count == 0)
        {
            return string.Empty;
        }

        Dictionary<string, object?> namedValues = new(StringComparer.Ordinal)
        {
            [CharacterContextKey] = _characterContext,
        };
        StringBuilder rendered = new();
        foreach (AgentObservation observation in observations)
        {
            ArgumentNullException.ThrowIfNull(observation);
            ITemplate template = _fragments.TryGetValue(observation.TypeKey, out ITemplate? fragment)
                ? fragment
                : _fallback;
            _ = rendered.Append(await RenderRecordAsync(template, observation, namedValues));
        }

        return rendered.ToString();
    }

    private static ValueTask<string> RenderRecordAsync(
        ITemplate template,
        AgentObservation observation,
        IReadOnlyDictionary<string, object?> namedValues)
        => template is IRootedTemplate rooted
            ? rooted.RenderRootedAsync(observation, namedValues)
            : throw new InvalidOperationException(
                $"The compiled template type '{template.GetType().FullName}' cannot render an observation record "
                + "as its root context.");
}
