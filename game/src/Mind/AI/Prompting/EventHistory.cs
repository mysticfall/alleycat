using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Standalone authoring resource for exact-dispatch event-history rendering (AI-003 TR-12): it is not a prompt
/// section and never enters the session-start prompt stack. Its fragments and fallback feed the on-demand
/// <see cref="ObservationHistoryRenderer" /> — which compiles each one individually as a standalone template —
/// for AI-002 wait results, timeline history tool results, and interruption injections.
/// </summary>
[GlobalClass]
public sealed partial class EventHistory : Resource
{
    /// <summary>Ordered authored fragments dispatched by exact semantic key.</summary>
    [Export]
    public EventHistoryPromptFragment[] Fragments { get; set; } = [];

    /// <summary>Mandatory authored fallback source used when no fragment's exact <c>TypeKey</c> matches.</summary>
    [Export(PropertyHint.MultilineText)]
    public string FallbackSource { get; set; } = "((Received {{TypeKey}} event.))";

    /// <summary>
    /// Validates the event-history authoring contract: nonblank fallback, nonblank fragment keys, and no duplicate
    /// exact keys.
    /// </summary>
    /// <param name="fragments">Ordered authored fragments dispatched by exact semantic key.</param>
    /// <param name="fallbackSource">Authored fallback source used when no exact fragment key matches.</param>
    /// <exception cref="InvalidOperationException">Thrown with clear authoring guidance when invalid.</exception>
    internal static void ValidateAuthoring(
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
}
