using AlleyCat.Character;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.Observation;
using Microsoft.Extensions.AI;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Builds the runtime-authored synthetic scene-update exchange for pending scene observations (AI-002 TR-18): one
/// assistant message carrying a single call to the delivery tool plus its tool-result message rendering the
/// observations in observation-owned canonical text, in timeline order, joined with exactly one newline (AI-003
/// TR-8).
/// </summary>
/// <remarks>
/// The delivery tool is runtime-authored only: it is never offered to the model, and a model attempt to call it is
/// rejected as an unknown tool under existing validation (AI-002 TR-13). Synthetic call IDs use a distinct
/// namespace, and session-wide duplicate-call-ID validation spans real and synthetic exchanges (AI-002 TR-18).
/// </remarks>
internal static class SyntheticSceneUpdateExchange
{
    /// <summary>Working name of the runtime-authored delivery tool; never part of the model-facing inventory.</summary>
    internal const string DeliveryToolName = "scene_update";

    /// <summary>Distinct namespace prefix of synthetic call IDs (AI-002 TR-18).</summary>
    internal const string SyntheticCallIDPrefix = "runtime-scene-update-";

    /// <summary>Formats one synthetic call ID from its session-scoped monotonic sequence number.</summary>
    internal static string FormatCallId(long sequence) => $"{SyntheticCallIDPrefix}{sequence}";

    /// <summary>
    /// Selects the event-timeline observations pending delivery beyond the delivered watermark, excluding the
    /// owning character's persisted self-speech observation, which reaches the model through its own retained
    /// <c>speak</c> call and is never delivered as a scene update (AI-002 TR-21).
    /// </summary>
    internal static IReadOnlyList<AcceptedObservationEntry> SelectPending(
        IReadOnlyList<AcceptedObservationEntry> timeline,
        long deliveredSequenceID,
        ICharacter owner)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(owner);
        return
        [
            .. timeline.Where(entry => entry.SequenceID > deliveredSequenceID
                && !IsOwnSelfSpeech(entry.Payload, owner)),
        ];
    }

    /// <summary>
    /// Builds the consolidated synthetic exchange for the pending observations selected from
    /// <paramref name="timeline" /> beyond <paramref name="deliveredSequenceID" />, or an empty list when nothing is
    /// pending — no synthetic exchange is created then (AI-002 TR-18). Grouped speech keeps its continuation
    /// projection over the complete timeline, so a partially delivered group joins into one line at its latest
    /// position (AI-003 TR-10).
    /// </summary>
    internal static async Task<IReadOnlyList<ChatMessage>> BuildAsync(
        IReadOnlyList<AcceptedObservationEntry> timeline,
        long deliveredSequenceID,
        ICharacter owner,
        string callId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(callId);

        IReadOnlyList<AcceptedObservationEntry> pending = SelectPending(timeline, deliveredSequenceID, owner);
        if (pending.Count == 0)
        {
            return [];
        }

        string rendered = await new ObservationHistoryRenderer(owner).RenderAsync(pending, timeline);
        return
        [
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, DeliveryToolName)]),
            new ChatMessage(
                ChatRole.Tool,
                [new FunctionResultContent(callId, rendered)]),
        ];
    }

    private static bool IsOwnSelfSpeech(AgentObservation observation, ICharacter owner)
        => observation is ObservedSpeech { ActorId: { } actorId }
            && string.Equals(actorId, owner.FullId, StringComparison.Ordinal);
}
