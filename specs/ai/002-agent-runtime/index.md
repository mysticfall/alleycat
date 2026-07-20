---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

The system must execute each Mind turn as a fresh, stateless Agent Framework session whose tools return one standard
result envelope for Mind-owned observation ingestion.

## Goal

Keep framework protocol transient while allowing agents to perform real-time actions whose committed effects become
ordered subjective observations without granting tools direct mutation access to Mind.

## User Requirements

1. An NPC may perform zero or more actions during one turn and may repeat an action when appropriate.
2. Speaking must be optional and must not complete the current turn.
3. Successful actions that matter to the NPC's experience must be available to later turns as self-observations.
4. Failed, cancelled, or invalid actions must not be remembered as successful events.
5. Tool feedback needed by the active model may remain transient and must not become player-visible chat or durable
   character memory by default.
6. Voice availability must constrain speech only, not whether the NPC can run a turn or use other actions.
7. An enabled high-importance interruption must cancel the active response as expected pre-emption, then produce one
   fresh replacement only after active invocation and action work settle.
8. Removing an NPC's Mind from the scene must not allow active or queued actions to produce delayed in-world effects.

## Technical Requirements

1. AgenticMind must create and configure a fresh Agent Framework agent and session for every turn. It must not cache an
   agent, session, first-turn prompt snapshot, or completed assistant/tool transcript across turns.
2. At turn start, AgenticMind must resolve the current scene and owning character, compile and render the configured
   `PromptStack`, create the fresh agent and session, and invoke the typed no-input run for `EndTurnResult`.
3. The rendered prompt stack must be the sole system instruction. No prior transcript or per-batch observation-summary
   user message may cross turn boundaries.
4. A turn must preserve exactly one rendered instruction snapshot throughout its active function-calling loop.
   Observations arriving mid-turn remain recorded by Mind but do not alter the active snapshot.
5. A turn may execute an arbitrary sequence of zero or more tool calls before end of turn. No action, including `speak`,
   may implicitly complete the turn.
6. The only accepted final non-tool output is a typed, closed `EndTurnResult`. Its initial schema is property-free, and
   successful deserialisation marks end of turn.
7. Every `AgentTool` delegate must return the standard `AgentToolResult`, containing an optional model-facing `Message`
   and an ordered observation collection. A null message and an empty collection are valid.
8. The common `AgentTool` wrapper must await and validate the complete result before exposing any part of it. It must
   ask the owning Mind to atomically ingest the ordered observations, then return only `Message` to Agent Framework.
9. Mind owns all observation mutation. Tool invocation services must expose the owning Mind boundary and
   action-specific capabilities, but must not expose a public observation recorder or sink.
10. Tools must not mutate Mind directly. Tool-result ingestion must stamp every `ObservedAction` with the owning
    character's exact actor ID before contextual importance is calculated, preventing actor spoofing.
11. A throwing, cancelled, malformed, wrong-shaped, or otherwise invalid tool result must contribute no observations.
    Batch validation and ingestion must be all-or-nothing and preserve authored result order.
12. Only the optional `AgentToolResult.Message` may reach the active framework tool loop. Structured envelopes and
    observations must not be exposed to the model or retained as cross-turn framework protocol.
13. `SpeechTool` must:
    - reject blank input through the voice contract without producing a result observation;
    - await successful admission through the configured character-owned `IVoice.SpeakAsync(...)`;
    - return exactly one actorless `ObservedSpeech` in its `AgentToolResult` after admission; and
    - optionally return a transient model-facing acknowledgement.
14. Speech admission, not playback completion, is the successful tool-action boundary. Failure or cancellation before
    admission must produce no observed speech.
15. The configured output voice must remain excluded from external listening so dispatched self-speech is not recorded
    a second time as perceived speech.
16. Voice is a SpeechTool capability and must not be a generic AgenticMind runtime prerequisite.
17. Request and response diagnostics may observe a turn but must not change tool iteration, cancellation, completion, or
    end-of-turn behaviour.
18. Backend or malformed-end-result failures must remain contained and logged. Automatic retry and backoff behaviour is
    not defined by this specification.
19. High-importance pre-emption under [AI-001](../001-mind/index.md) must cancel the active invocation as expected
    cancellation. Invocation and tool work must settle before exactly one replacement can start, and turns must not
    overlap.
20. Actions and tool observations committed before pre-emption must remain committed. Cancellation does not reverse
    admitted actions.
21. Node-lifetime cancellation from AI-001 must propagate through active invocation and tool work. Expected pre-emption
    and lifetime cancellation must not trigger retry, another unintended turn, or misleading failure diagnostics.
22. Queued or deferred action tasks must settle when Mind exits, without dispatch or successful observation. Deferred
    callbacks must not access services from the exited node.
23. AI-001 is normative for node lifetime, actor stamping, atomic ingestion, scheduling, and interruption. AI-003 is
    normative for per-turn prompt compilation and event-history rendering.

## In Scope

- Fresh Agent Framework agent and session creation for each turn.
- Typed no-input invocation and closed, initially empty `EndTurnResult` completion.
- Arbitrary zero-or-more tool iterations before end of turn.
- Standard `AgentToolResult` validation, projection, and atomic Mind hand-off.
- Invocation-time action capabilities without public observation mutation services.
- Speech admission, transient acknowledgement, and exactly-once observed-speech production.
- Expected interruption and node-lifetime cancellation settlement.
- Failure containment and diagnostics that do not alter runtime semantics.

## Out Of Scope

- Automatic retry or backoff policy for backend or malformed-end-result failures.
- Additional production action tools beyond speech and the generic tool-result contract.
- Cancelling or reversing world actions already admitted before interruption.
- Speech playback-finished success semantics.
- Timeline compaction, persistence, and cross-turn framework transcript retention.
- Voice as a requirement for generic non-speech turn execution.
- Multi-agent orchestration and guidance-agent APIs.

## Acceptance Criteria

1. A turn can reach a typed `EndTurnResult` without calling any tool or producing player-visible assistant text.
2. A turn can invoke multiple different tools or repeat tools before producing exactly one accepted typed end result;
   calling `speak` neither completes the turn nor prevents later tools.
3. Capturing-client tests verify every turn uses a fresh agent and session, one sole system instruction, and no prior
   transcript or observation-summary user message.
4. Tests verify observations arriving during an invocation appear in the next turn's reconstructed context without
   altering the active instruction snapshot.
5. Tool tests verify synchronous and asynchronous delegates return one `AgentToolResult`, whose null message and empty
   or ordered multiple-observation collection are valid.
6. Tool tests verify the common wrapper awaits and validates the result, atomically hands observations to Mind in order,
   and exposes only the optional message to Agent Framework.
7. Tests verify Mind stamps tool action actors with the owning character ID, prevents spoofing, and provides no public
   observation recorder, sink, or direct tool-mutation path.
8. Tests verify throwing, cancelled, malformed, wrong-shaped, and atomically invalid tool calls contribute no
   observations.
9. Speech tests verify exactly one self-relative `ObservedSpeech` after successful voice admission and none for blank,
   unavailable, unconfigured, failed-before-admission, or cancelled requests.
10. Speech tests verify admission does not await playback and self-listener exclusion prevents duplicate observed
    speech.
11. Interruption tests verify expected cancellation settles invocation and tools before one fresh replacement starts,
    committed tool observations survive, and no turns overlap.
12. Diagnostics settings do not change invocation, cancellation, action, or completion semantics; genuine failures
    remain logged and contained.
13. Node-exit tests verify active and queued work settles without delayed dispatch, successful observation, retry,
    replacement, exited-node service access, or erroneous expected-cancellation diagnostics.
14. Acceptance verifies both player-visible action, interruption, and post-destruction behaviour and the fresh-session,
    result-envelope, atomic-ingestion, cancellation, settlement, and typed end-of-turn contracts.

## References

### Implementation

- `game/src/Mind/AI/AgenticMind.cs`
- `game/src/Mind/AI/Tool/AgentTool.cs`
- `game/src/Mind/AI/Tool/AgentToolResult.cs`
- `game/src/Mind/AI/Tool/SpeechTool.cs`
- `game/src/Mind/Mind.cs`
- `game/src/Mind/Observation/Observation.cs`

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)

### External Dependencies

- Microsoft Agent Framework
