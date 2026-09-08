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
        ToolDescription = "Deliberately yield until future developments make responding worthwhile. Fresh context — "
            + "event history and the current scene — arrives with every request, so waiting is never needed to "
            + "receive information. A wait delivers no observation text — never what was observed — and a completed "
            + "wait leaves no record in your context: its outcome is internal bookkeeping, and what happened "
            + "meanwhile reaches you through fresh events and scene status, which also state the current game time. "
            + "Wait when giving something time to develop, for example awaiting a reply that has not been given "
            + "yet; an answer already visible in your context needs no wait.";
        // The wait result is protocol bookkeeping only; the settled exchange is never replayed to the model.
        DisposesExchangeOnCompletion = true;
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

        return new AgentToolResult(ComposeResultMessage(outcome, elapsedSeconds, finishedAtSeconds));
    }

    private static string ComposeResultMessage(
        MindBase.WaitOutcome outcome,
        double elapsedSeconds,
        double finishedAtSeconds)
    {
        string elapsed = elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture);
        string now = finishedAtSeconds.ToString("F1", CultureInfo.InvariantCulture);
        string reason = outcome.Wake switch
        {
            MindBase.ObservationWaitWake.FreshObservation => "fresh event",
            MindBase.ObservationWaitWake.ThresholdCrossed => "importance threshold",
            MindBase.ObservationWaitWake.AttendedSpeakerFinished => "attended speaker finished",
            MindBase.ObservationWaitWake.QuietExpiry => "timeout",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        return $"Wait ended: {reason}. Elapsed game time: {elapsed} seconds. Current game time: {now}s.";
    }
}
