using System.Text;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Standalone authoring resource for exact-dispatch event-history rendering (AI-003 TR-12): it is not a prompt
/// section and never enters the session-start prompt stack. Its fragments and fallback feed the on-demand
/// <see cref="ObservationHistoryRenderer" /> for AI-002 wait results, timeline history tool results, and
/// interruption injections.
/// </summary>
[GlobalClass]
public sealed partial class EventHistory : Resource
{
    /// <summary>Top-level render-context key carrying one ordered observation batch to the event-history template.</summary>
    public const string ObservationsContextKey = "observations";

    /// <summary>Ordered authored fragments dispatched by exact semantic key.</summary>
    [Export]
    public EventHistoryPromptFragment[] Fragments { get; set; } = [];

    /// <summary>Mandatory Handlebars source used when no exact fragment key matches.</summary>
    [Export(PropertyHint.MultilineText)]
    public string FallbackSource { get; set; } = "((Received {{TypeKey}} event.))";

    /// <summary>
    /// Builds the exact-dispatch Handlebars source for an ordered observation history, validated against the
    /// authoring contract.
    /// </summary>
    /// <param name="fragments">Ordered authored fragments dispatched by exact semantic key.</param>
    /// <param name="fallbackSource">Handlebars source used when no exact fragment key matches.</param>
    /// <returns>Template source iterating the <c>observations</c> key with exact keyed dispatch.</returns>
    internal static string BuildEventHistorySource(
        IReadOnlyList<EventHistoryPromptFragment> fragments,
        string? fallbackSource)
    {
        ValidateAuthoring(fragments, fallbackSource);

        StringBuilder source = new("{{#each observations}}");
        foreach (EventHistoryPromptFragment fragment in fragments)
        {
            _ = source.Append("{{#if (eqOrdinal TypeKey \"")
                .Append(EscapeStringLiteral(fragment.TypeKey))
                .Append("\")}}")
                .Append(fragment.Source)
                .Append("{{else}}");
        }

        _ = source.Append(fallbackSource);
        for (int index = 0; index < fragments.Count; index++)
        {
            _ = source.Append("{{/if}}");
        }

        _ = source.Append("{{/each}}");
        return source.ToString();
    }

    private static void ValidateAuthoring(
        IReadOnlyList<EventHistoryPromptFragment> fragments,
        string? fallbackSource)
    {
        if (string.IsNullOrWhiteSpace(fallbackSource))
        {
            throw new InvalidOperationException("Event history requires a nonblank fallback template.");
        }

        HashSet<string> keys = new(StringComparer.Ordinal);
        for (int index = 0; index < fragments.Count; index++)
        {
            EventHistoryPromptFragment fragment = fragments[index]
                ?? throw new InvalidOperationException($"Event history fragment at index {index} cannot be null.");

            if (string.IsNullOrWhiteSpace(fragment.TypeKey))
            {
                throw new InvalidOperationException($"Event history fragment at index {index} requires a nonblank TypeKey.");
            }

            if (!keys.Add(fragment.TypeKey))
            {
                throw new InvalidOperationException(
                    $"Event history contains duplicate exact TypeKey '{fragment.TypeKey}'.");
            }
        }
    }

    private static string EscapeStringLiteral(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}
