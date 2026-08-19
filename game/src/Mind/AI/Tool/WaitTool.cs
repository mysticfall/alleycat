using System.ComponentModel;
using System.Globalization;
using AlleyCat.Core.Time;
using Godot;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Action tool that watches the scene for notable observations while the character waits.
/// </summary>
[Tool]
[GlobalClass]
public partial class WaitTool : AgentTool
{
    /// <summary>
    /// Creates a wait tool with the default model-facing metadata.
    /// </summary>
    public WaitTool()
    {
        ToolName = "wait";
        ToolDescription = "Watch the scene for what happens next. This is how you receive updates about important "
            + "scene events — without waiting, nothing new reaches you. Waiting is observation, not idling: after "
            + "asking another character a question, wait a reasonable duration for their answer before assuming "
            + "refusal. Returns the notable observations that arrived while waiting, how long you waited, and the "
            + "current game time.";
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Wait;

    private async ValueTask<AgentToolResult> Wait(
        ScenarioContext context,
        [Description(
            "Optional duration to watch, in seconds of game time. Omit to use the default duration of 10 seconds.")]
        float? seconds = null,
        CancellationToken cancellationToken = default)
    {
        AgentToolSession session = Session!;
        IGameClock clock = session.Clock
            ?? throw new InvalidOperationException("The wait tool requires a session game clock.");

        TimeSpan duration = seconds is > 0f
            ? TimeSpan.FromSeconds(seconds.Value)
            : TimeSpan.FromSeconds(session.Mind.MaxObservationWaitSeconds);

        double startedAtSeconds = clock.NowSeconds;
        MindBase.WaitOutcome outcome = await session.Mind.WaitForNotableObservationsAsync(duration, cancellationToken);
        double finishedAtSeconds = clock.NowSeconds;
        double elapsedSeconds = Math.Max(0d, finishedAtSeconds - startedAtSeconds);

        return new AgentToolResult(ComposeResultMessage(session, outcome, elapsedSeconds, finishedAtSeconds));
    }

    private static string ComposeResultMessage(
        AgentToolSession session,
        MindBase.WaitOutcome outcome,
        double elapsedSeconds,
        double finishedAtSeconds)
    {
        string elapsed = elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture);
        string now = finishedAtSeconds.ToString("F1", CultureInfo.InvariantCulture);
        if (outcome.Notable.Count == 0)
        {
            return outcome.AttendedSpeakerFinished
                ? $"Waited {elapsed} seconds. An attended speaker finished speaking. Current game time: {now}s. Nothing notable happened."
                : $"Waited {elapsed} seconds. Current game time: {now}s. Nothing notable happened.";
        }

        string history = session.HistoryRenderer is { } renderer
            ? renderer.Render(outcome.Notable)
            : string.Join('\n', outcome.Notable.Select(static observation => observation.TypeKey));
        return $"Waited {elapsed} seconds. Current game time: {now}s. "
            + (outcome.AttendedSpeakerFinished ? "An attended speaker finished speaking. " : string.Empty)
            + $"Notable observations:\n{history}";
    }
}
