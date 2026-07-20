using System.Text;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Builds template-scoped exact dispatch for an ordered observation history.
/// </summary>
[GlobalClass]
public sealed partial class EventHistoryPromptSection : PromptSection
{
    /// <summary>Stable top-level render-context key containing the observation history.</summary>
    public const string ObservationsContextKey = "observations";

    /// <summary>Ordered authored fragments dispatched by exact semantic key.</summary>
    [Export]
    public EventHistoryPromptFragment[] Fragments { get; set; } = [];

    /// <summary>Mandatory Handlebars source used when no exact fragment key matches.</summary>
    [Export(PropertyHint.MultilineText)]
    public string FallbackSource { get; set; } = "((Received {{TypeKey}} event.))";

    /// <inheritdoc />
    public override Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        cancellationToken.ThrowIfCancellationRequested();

        EventHistoryPromptFragment[] fragments = Fragments ?? [];
        ValidateAuthoring(fragments, FallbackSource);

        StringBuilder source = new("{{#each observations}}");
        foreach (EventHistoryPromptFragment fragment in fragments)
        {
            _ = source.Append("{{#if (eqOrdinal TypeKey \"")
                .Append(EscapeStringLiteral(fragment.TypeKey))
                .Append("\")}}")
                .Append(fragment.Source)
                .Append("{{else}}");
        }

        _ = source.Append(FallbackSource);
        for (int index = 0; index < fragments.Length; index++)
        {
            _ = source.Append("{{/if}}");
        }

        _ = source.Append("{{/each}}");
        return Task.FromResult(source.ToString());
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
