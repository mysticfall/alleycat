using AlleyCat.Character;
using AlleyCat.Scene;
using AlleyCat.Vision;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Composes the visible-cue description lines presented beside a captured view image (playtest R1's multi-cue
/// work): one owner-anchored line per cue the vision layer's own visual survey currently reports visible — the
/// cue's authored description through <see cref="VisualCue.Describe" /> — so the agent reading the image also
/// learns what each visible feature is.
/// </summary>
/// <remarks>
/// Visibility comes exclusively from the vision layer's most recent survey snapshot
/// (<see cref="IVisibleCueSurveySource" />): reading it runs no new geometric test, and it may lag the live
/// frustum by up to one survey interval. Lines are bounded and ordinal (subjects by full ID, cues in authored
/// order); a vision component without survey knowledge, a missing survey, a blank description, or any failure
/// composes nothing — honest omission, never fabricated detail.
/// </remarks>
internal static class VisibleCueDescriptions
{
    /// <summary>The maximum description lines one presentation carries.</summary>
    internal const int MaximumDescriptionLines = 8;

    /// <summary>
    /// Composes the visible-cue description lines for the observer's most recent visual survey, or an empty list
    /// when no survey knowledge exists or no description renders.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ComposeLinesAsync(
        ICharacter observer,
        ISceneContext scene,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scene);

        if (!observer.TryGetVision(out IVision? vision)
            || vision is not IVisibleCueSurveySource { LastVisibleCueSurvey: { } survey })
        {
            return [];
        }

        List<VisualScanResult> ordered = [.. survey
            .OrderBy(static result => result.Subject.FullId, StringComparer.Ordinal)];
        List<string> lines = [];
        foreach (VisualScanResult result in ordered)
        {
            foreach (VisualCue cue in result.VisibleCues)
            {
                if (lines.Count >= MaximumDescriptionLines)
                {
                    return lines;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    string description = await cue.Describe(scene, observer).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        lines.Add($"`{result.Subject.FullId}`'s {cue.ID}: {description}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    // One cue's failed description never degrades the others or the presentation carrying them.
                }
            }
        }

        return lines;
    }

    /// <summary>Formats the composed lines as one labelled list block, or an empty string when there are none.</summary>
    public static string FormatListBlock(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 0
            ? string.Empty
            : "\nVisible details:\n- " + string.Join("\n- ", lines);
    }

    /// <summary>
    /// Appends the visible-cue description list to one delivered-capture message; a message with no visible cues
    /// is returned unchanged. The tool delivered-result and the auto-mode fabricated exchange compose through this
    /// single formatting path, so the delivery mode can never drift the presented content.
    /// </summary>
    public static async Task<string> AppendToMessageAsync(
        string message,
        ICharacter observer,
        ISceneContext scene,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        IReadOnlyList<string> lines = await ComposeLinesAsync(observer, scene, cancellationToken)
            .ConfigureAwait(false);
        return message + FormatListBlock(lines);
    }
}
