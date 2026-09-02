using System.Text;
using AlleyCat.Character;
using AlleyCat.Templating;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Renders ordered observation records through the AI-003 event-history contract for on-demand paths such as the
/// AI-002 <c>wait</c> and timeline history tools.
/// </summary>
/// <remarks>
/// The renderer compiles each authored fragment and the fallback once per session as standalone templates from the
/// authored <see cref="EventHistoryDocument" /> parsed out of the configured event-history file. Every record
/// selects its template by exact, case-sensitive <c>TypeKey</c> dictionary lookup — unknown types render the
/// fallback — and passes directly to that template as the rooted render context, so fragment-visible record
/// properties resolve at the template's top level exactly like the pre-migration current-context semantics. No
/// dispatch source is generated at runtime, no reflection projection happens here, and no global partial
/// registration exists; the owning character rides along as a named value — its template surface curated to
/// <c>FullId</c> by the engine's member-access policy — so actor-relative wording matches the session system
/// instruction.
/// </remarks>
internal sealed class ObservationHistoryRenderer
{
    private const string CharacterContextKey = "character";

    private readonly IReadOnlyDictionary<string, ITemplate> _fragments;
    private readonly ITemplate _fallback;
    private readonly ICharacter _character;

    private ObservationHistoryRenderer(
        IReadOnlyDictionary<string, ITemplate> fragments,
        ITemplate fallback,
        ICharacter character)
    {
        _fragments = fragments;
        _fallback = fallback;
        _character = character;
    }

    /// <summary>
    /// Creates a session renderer from the parsed authored event-history document, or from the default
    /// authoring contract when the Mind declares no event-history file.
    /// </summary>
    /// <param name="eventHistory">Parsed authored event history supplying fragments and fallback, or null.</param>
    /// <param name="compiler">Template compiler used to compile each fragment and the fallback individually.</param>
    /// <param name="character">
    /// Owning character from the sealed session render context, used for actor-relative wording; its template
    /// surface is curated by the engine's member-access policy.
    /// </param>
    public static ObservationHistoryRenderer Create(
        EventHistoryDocument? eventHistory,
        ITemplateCompiler compiler,
        ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(character);

        IReadOnlyList<EventHistoryFragment> fragments = eventHistory?.Fragments ?? [];
        string fallbackSource = eventHistory?.FallbackSource ?? EventHistoryDocument.DefaultFallbackSource;

        Dictionary<string, ITemplate> compiledFragments = new(StringComparer.Ordinal);
        foreach (EventHistoryFragment fragment in fragments)
        {
            compiledFragments.Add(fragment.TypeKey, compiler.Compile(fragment.Source));
        }

        return new ObservationHistoryRenderer(compiledFragments, compiler.Compile(fallbackSource), character);
    }

    /// <summary>Renders the ordered observation records through their individually compiled templates.</summary>
    /// <param name="observations">Observation records in timeline order.</param>
    /// <returns>The rendered event-history text for the supplied records.</returns>
    public ValueTask<string> RenderAsync(IReadOnlyList<AgentObservation> observations)
        => RenderAsync(observations, observations);

    /// <summary>
    /// Renders a selected window using the full timeline to expand any selected grouped speech into its complete,
    /// currently-known model-facing utterance.
    /// </summary>
    public ValueTask<string> RenderAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timeline)
        => RenderProjectedAsync(ContinuationProjection.Project(timeline, observations));

    /// <summary>Projects a timeline or selected timeline window for model-facing callers that need event counting.</summary>
    internal static IReadOnlyList<ContinuationProjection.Event> Project(
        IReadOnlyList<AgentObservation> timeline,
        IReadOnlyList<AgentObservation>? selected = null)
        => ContinuationProjection.Project(timeline, selected);

    /// <summary>Renders pre-projected model events through their individually compiled templates.</summary>
    internal async ValueTask<string> RenderProjectedAsync(IReadOnlyList<ContinuationProjection.Event> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return string.Empty;
        }

        Dictionary<string, object?> namedValues = new(StringComparer.Ordinal)
        {
            [CharacterContextKey] = _character,
        };
        StringBuilder rendered = new();
        foreach (ContinuationProjection.Event @event in events)
        {
            AgentObservation observation = @event.Observation;
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
