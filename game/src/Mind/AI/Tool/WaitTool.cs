using System.ComponentModel;
using System.Globalization;
using AlleyCat.Core.Time;
using Godot;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Action tool that chooses deliberate quiet time: the requested seconds seed the wait gate's initial deadline,
/// so the wait ends when they elapse, when an immediate verdict gives the character a reason to act, or when a
/// sooner deferred-verdict deadline expires — never later than requested.
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
        ToolDescription = "Deliberately remain silent for roughly the supplied number of seconds — invoke this "
            + "only to choose quiet time; pausing after your own actions happens automatically and needs no call. "
            + "The quiet time can end early when circumstances give you an opening to act again; that is a chance to "
            + "act, not an instruction, and you may wait again or stay silent. Scene updates you "
            + "did not initiate arrive automatically as tool results, and fresh scene status — including the "
            + "current game time — arrives with every request, so waiting is never needed to receive information. "
            + "A wait returns only why it ended, the elapsed game time, and the current game time — never what was "
            + "observed meanwhile. This call and its result remain in your context like any other exchange, as "
            + "the record of your quiet time.";
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Wait;

    private async ValueTask<AgentToolResult> Wait(
        ScenarioContext context,
        [Description(
            "Rough number of seconds to remain silent; the quiet time may end early when circumstances give you "
            + "an opening to act again.")]
        float? seconds = null,
        AgentSessionInvocationSignals? signals = null,
        CancellationToken cancellationToken = default)
    {
        AgentToolSession session = Session!;
        IGameClock clock = session.Clock
            ?? throw new InvalidOperationException("The wait tool requires a session game clock.");

        double startedAtSeconds = clock.NowSeconds;
        // Only actual gate entry marks this invocation as the session's pause (AI-002 TR-14): a rejected duration or
        // a failure before entry never suppresses the default park.
        MindBase.WaitOutcome outcome = await session.Mind.WaitForNextTurnAsync(
            cancellationToken,
            seconds,
            signals is null ? null : signals.MarkSessionParkEntered);
        double finishedAtSeconds = clock.NowSeconds;
        double elapsedSeconds = Math.Max(0d, finishedAtSeconds - startedAtSeconds);

        return new AgentToolResult(ComposeResultMessage(outcome, elapsedSeconds, finishedAtSeconds));
    }

    /// <summary>
    /// Builds the payload-free wait result message shared by the explicit wait tool and the session-park
    /// fabricated wait exchange: the wake reason, the elapsed game time, and the current game time — never what
    /// was observed meanwhile.
    /// </summary>
    internal static string ComposeResultMessage(
        MindBase.WaitOutcome outcome,
        double elapsedSeconds,
        double finishedAtSeconds)
    {
        string elapsed = elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture);
        string now = finishedAtSeconds.ToString("F1", CultureInfo.InvariantCulture);
        string reason = outcome.Wake switch
        {
            MindBase.ObservationWaitWake.DecisionAct => "circumstances may call for you to act",
            MindBase.ObservationWaitWake.PauseElapsed => "a pause gives you an opening to act",
            MindBase.ObservationWaitWake.TimerElapsed => "the chosen quiet time elapsed",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        return $"Wait ended: {reason}. Elapsed game time: {elapsed} seconds. Current game time: {now}s.";
    }
}
