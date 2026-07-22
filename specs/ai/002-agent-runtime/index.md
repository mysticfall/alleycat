---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

The system must execute each Mind turn through a bounded, tool-only request sequence whose successful actions return
standard results for Mind-owned observation ingestion.

## Goal

Let NPCs take real-time in-world actions while keeping provider protocol transient, turn completion explicit, and
failures contained without treating assistant text or provider history as character memory.

## User Requirements

1. An NPC may take zero actions, one action, or multiple actions during one turn and may repeat an action when
   appropriate.
2. `speak` must be an optional in-world action and must not end the turn.
3. A turn with no actions must end directly without producing player-visible assistant text.
4. Successful actions that matter to the NPC's experience must be available to later turns as self-observations.
5. Failed, cancelled, malformed, or invalid actions must not be remembered as successful events.
6. Tool feedback needed during the active turn may remain transient and must not become player-visible chat or durable
   character memory by default.
7. Voice availability must constrain speech only, not whether the NPC can run a turn or use other actions.
8. Invalid model output, unknown calls, action failures, backend failures, and exhausted safety bounds must stop the
   turn safely without a model repair attempt or automatic retry.
9. An enabled high-importance interruption must cancel the active turn as expected pre-emption, then produce one fresh
   replacement only after active request and action work settle.
10. Removing an NPC's Mind from the scene must not allow active or queued actions to produce delayed in-world effects.
11. Every action configured for a turn must remain available while the NPC decides how to complete that turn.
12. During development, developers can inspect `LoggingChatClient` request and response representations at the
    Microsoft.Extensions.AI abstraction for every model request when explicitly enabled. These diagnostics do not
    promise complete HTTP wire bodies.
13. Sensitive AI request and response detail must remain suppressed unless both dedicated diagnostics controls permit
    it, without changing NPC behaviour.

## Technical Requirements

1. AgenticMind must create a fresh provider client execution context for every turn. It must not retain an agent,
   provider response identifier, completed assistant/tool transcript, or first-turn prompt snapshot across turns.
2. At turn start, AgenticMind must resolve the current scene and owning character, compile and render the configured
   `PromptStack`, resolve the current action tools, and start one explicit tool-only request loop.
3. The rendered prompt stack must remain the turn's sole system instruction. Provider-required bootstrap input may be
   sent, but no prior transcript or per-batch observation-summary message may cross turn boundaries.
4. Every model request in the loop must require at least one tool call and must send no provider `response_format` or
   equivalent terminal-output schema. Ordinary assistant text is never an accepted turn result.
5. The runtime must register every configured production action plus the exact reserved synthetic marker `end_turn` on
   every request. A configured action must not use the reserved name.
6. `end_turn` is protocol control, not an action, action result, or observation. It must accept no arguments, must never
   invoke a production tool delegate, and must never be ingested by Mind.
7. A response containing `end_turn` is valid only when it contains exactly that one call and no ordinary assistant text
   or other callable protocol content. The sole marker completes the turn without another provider request.
8. A response without `end_turn` is valid only when it contains one or more well-formed calls to configured production
   actions and no ordinary assistant text, unknown content, unknown tool, duplicate call identifier, or other malformed
   item.
9. Zero actions must therefore be represented by `end_turn` in the first response. One or more actions must be followed
   by a later sequential request whose valid response either supplies more actions or the sole `end_turn` marker.
10. The runtime must validate a complete response batch before invoking any call in that batch. Valid all-action batches
    must be supported locally and their calls must execute serially in provider order.
11. `AllowMultipleToolCalls` must be a configurable runtime or provider preference and must default to `false`. It may
    guide provider generation but must not make an otherwise valid multi-action batch fail local validation.
12. After each successful all-action batch, the runtime must append the assistant function calls and corresponding tool
    results to transient per-turn history, then issue the next request by replaying the complete history in order.
13. Transient request history must be discarded when the turn settles. The Mind timeline under
    [AI-001](../001-mind/index.md) is the only cross-turn memory.
14. The runtime must enforce the named bounds `MaxModelRequests` and `MaxToolActions`, with normative defaults of `8`
    requests and `8` production actions per turn. These may remain constants when no settings surface exists. If
    exposed, both bounds must be configurable positive integers with the stated defaults.
15. `MaxModelRequests` counts every provider request, including the request that returns `end_turn`.
    `MaxToolActions` counts production action calls but not `end_turn`.
16. A batch that would exceed `MaxToolActions` must fail before any call in that batch executes. Reaching
    `MaxModelRequests` without a valid `end_turn` must fail once another request would be required.
17. Malformed output, assistant text, unknown content or tools, mixed `end_turn` and action calls, invalid arguments,
    duplicate call identifiers, tool errors, and bound exhaustion must fail closed. The runtime must not ask the model
    to repair output, retry the failed request or action, or continue the turn.
18. Valid sequential requests after successful actions are protocol continuation, not repair or retry. Actions and
    observations committed before a later failure or pre-emption remain committed.
19. Every `AgentTool` delegate must return the standard `AgentToolResult`, containing an optional model-facing `Message`
    and an ordered observation collection. A null message and an empty collection are valid.
20. The common `AgentTool` wrapper must await and validate the complete result before exposing any part of it. It must
    ask the owning Mind to atomically ingest the ordered observations, then return only `Message` as the tool result.
21. Mind owns all observation mutation. Tool invocation services must expose the owning Mind boundary and
    action-specific capabilities, but must not expose a public observation recorder or sink.
22. Tools must not mutate Mind directly. Tool-result ingestion must stamp every `ObservedAction` with the owning
    character's exact actor ID before contextual importance is calculated, preventing actor spoofing.
23. A throwing, cancelled, malformed, wrong-shaped, or otherwise invalid tool result must contribute no observations.
    Validation and ingestion of that tool's observation batch must be all-or-nothing and preserve authored order.
24. Only the optional `AgentToolResult.Message` may enter transient per-turn tool protocol. Structured envelopes and
    observations must not be exposed to the model or retained as cross-turn protocol.
25. `SpeechTool` must:
    - reject blank input through the voice contract without producing a result observation;
    - await successful admission through the configured character-owned `IVoice.SpeakAsync(...)`;
    - return exactly one actorless `ObservedSpeech` in its `AgentToolResult` after admission; and
    - optionally return a transient model-facing acknowledgement.
26. Speech admission, not playback completion, is the successful tool-action boundary. Failure or cancellation before
    admission must produce no observed speech.
27. The configured output voice must remain excluded from external listening so dispatched self-speech is not recorded
    a second time as perceived speech.
28. Voice is a `SpeechTool` capability and must not be a generic AgenticMind runtime prerequisite.
29. OpenAI Responses must be the default provider transport. Every Responses request must be stateless, set `store` to
    `false`, omit `previous_response_id`, and replay the complete ordered per-turn history instead.
30. OpenAI Chat Completions may remain only as an explicitly selected rollback transport. The runtime must not fall back
    to it automatically, and it must preserve the same tool-only validation, ordering, bounds, and failure semantics.
31. Development-only Microsoft.Extensions.AI request and response diagnostics must require both
    `Diagnostics:AI:EnableRequestResponseLogging` and the dedicated
    `Microsoft.Extensions.AI.LoggingChatClient` category enabled at `Trace`. Either control being disabled must
    suppress sensitive payload detail.
32. The runtime must decorate its AI `IChatClient` with Microsoft.Extensions.AI `LoggingChatClient` before the tool-only
    loop. This placement must observe every sequential provider request in a turn.
33. `LoggingChatClient` diagnostics represent requests and responses at the Microsoft.Extensions.AI abstraction and must
    not be described as complete HTTP wire-body capture. Serialisation must be deferred until the complete diagnostics
    gate in requirement 31 is satisfied. CORE-007 is normative for reusable logging and deferred serialisation.
34. AI request and response diagnostics must not use shared `System.ClientModel` body logging. Their scope must remain
    the agent-runtime client so speech transcription and generation traffic is unaffected.
35. Diagnostics must not change request count, tool calls, results, cancellation, actions, completion, validation, or
    failure behaviour.
36. High-importance pre-emption under AI-001 must cancel the active request loop as expected cancellation. Request and
    tool work must settle before exactly one replacement can start, and turns must not overlap.
37. Node-lifetime cancellation from AI-001 must propagate through active requests and tool work. Expected pre-emption
    and lifetime cancellation must not trigger retry, another unintended turn, or misleading failure diagnostics.
38. Queued or deferred action tasks must settle when Mind exits, without dispatch or successful observation. Deferred
    callbacks must not access services from the exited node.
39. AI-001 is normative for node lifetime, actor stamping, atomic ingestion, scheduling, and interruption. AI-003 is
    normative for per-turn prompt compilation and event-history rendering.
40. Development-only structural transport evidence may report configured tool names, required tool choice,
    `AllowMultipleToolCalls`, and response-format absence when explicitly gated. It must exclude message bodies,
    generated content, credentials, and other secrets and must not be presented as complete wire logging.
41. The explicit tool-only loop must be the sole production turn route, not a diagnostic or feature-gated alternative.
    No legacy framework-managed generic terminal-result route may remain selectable.

## In Scope

- Permanent production use of fresh, bounded tool-only execution for every turn.
- Zero, one, or multiple serial actions followed by the sole synthetic `end_turn` marker.
- Required tool choice, local batch validation, transient full-history replay, and fail-closed handling.
- Configurable multiple-call preference and named request and action bounds.
- Responses-default stateless transport and explicitly selected Chat Completions rollback.
- Standard `AgentToolResult` validation, projection, and atomic Mind hand-off.
- Speech admission, transient acknowledgement, and exactly-once observed-speech production.
- Expected interruption and node-lifetime cancellation settlement.
- Development-only MEAI diagnostics and non-secret structural transport evidence with explicit gating.

## Out Of Scope

- Model repair, automatic retry, or backoff after backend, protocol, action, or bound failures.
- Additional production action tools beyond speech and the generic action-result contract.
- Cancelling or reversing world actions already admitted before interruption or a later failure.
- Speech playback-finished success semantics.
- Timeline compaction, persistence, and cross-turn provider transcript retention.
- Voice as a requirement for generic non-speech turn execution.
- Multi-agent orchestration and guidance-agent APIs.
- Complete or production HTTP wire-body logging.

## Acceptance Criteria

1. Tests verify a zero-action turn returns sole `end_turn` on its first response, executes no action, creates no
   observation, and produces no accepted or player-visible assistant text.
2. Tests verify one action followed by `end_turn`, repeated sequential action responses, and locally accepted
   multi-action batches; all actions execute serially in provider order and `speak` never completes the turn.
3. Tests verify every request requires a tool call, registers all configured actions plus `end_turn`, and sends no
   provider response format or terminal schema.
4. Tests verify `end_turn` is reserved, argument-free, never invoked, never returned as an action result, never ingested
   as an observation, and accepted only as the sole call in its response.
5. Tests reject empty or malformed responses, assistant text, unknown content and tools, duplicate call identifiers,
   invalid arguments, mixed action and `end_turn` calls, and non-sole `end_turn` without executing the invalid batch.
6. Tests verify `AllowMultipleToolCalls` is configurable and defaults to `false`, while local validation still accepts a
   valid all-action batch and executes it serially.
7. Tests verify successful calls and results are replayed in full on each later request, then discarded at turn end. A
   later turn receives only its newly rendered instruction, provider bootstrap input, and Mind timeline context.
8. Tests verify `MaxModelRequests` and `MaxToolActions` default to `8`, count requests and production actions
   respectively, reject a batch that would exceed the action bound before execution, and stop when the request bound is
   exhausted without `end_turn`.
9. Tests verify malformed, unknown, text, mixed, tool-error, backend-error, and bounds failures stop without model
   repair, request or action retry, or continued execution; earlier committed actions and observations remain committed.
10. Responses transport tests verify it is the default and that every sequential request sets `store: false`, omits
    `previous_response_id`, and replays complete ordered per-turn history.
11. Configuration and transport tests verify Chat Completions is available only through explicit selection, is never an
    automatic fallback, and preserves the tool-only protocol semantics.
12. Tool tests verify delegates return one `AgentToolResult`; the common wrapper validates it, atomically hands ordered
    observations to Mind, and exposes only the optional transient message as the tool result.
13. Tests verify Mind stamps tool action actors with the owning character ID, prevents spoofing, and provides no public
    observation recorder, sink, or direct tool-mutation path.
14. Speech tests verify exactly one self-relative `ObservedSpeech` after successful voice admission and none for blank,
    unavailable, unconfigured, failed-before-admission, or cancelled requests.
15. Speech tests verify admission does not await playback and self-listener exclusion prevents duplicate observed
    speech.
16. Interruption tests verify expected cancellation settles requests and tools before one fresh replacement starts,
    committed action observations survive, and no turns overlap.
17. A capturing client verifies every sequential request is logged at the Microsoft.Extensions.AI abstraction only when
    the diagnostics option and `Microsoft.Extensions.AI.LoggingChatClient` `Trace` category are both enabled.
18. Suppression tests verify either disabled diagnostics control prevents serialisation of sensitive payload detail.
19. Integration tests verify `LoggingChatClient` decorates the tool-only client, shared `System.ClientModel` body
    logging is not used, and STT and TTS request and response logging is unaffected.
20. Equivalent runs with diagnostics enabled and disabled preserve requests, tool calls, results, cancellation,
    validation, actions, completion, and failure handling.
21. Node-exit tests verify active and queued work settles without delayed dispatch, successful observation, retry,
    replacement, exited-node service access, or erroneous expected-cancellation diagnostics.
22. Acceptance verifies both NPC-visible action and speech behaviour and the tool-only protocol, provider transport,
    transient-history, observation-ingestion, diagnostics, cancellation, settlement, and failure contracts.
23. Tests verify every production turn uses the explicit tool-only loop and no diagnostic flag or legacy generic
    terminal-result route can select a competing execution path.

## References

### Implementation

- `game/src/Mind/AI/AgenticMind.cs`
- `game/src/Mind/AI/ToolOnlyTurnRunner.cs`
- `game/src/Mind/AI/AIChatClientDiagnostics.cs`
- `game/src/Mind/AI/AIDiagnosticsOptions.cs`
- `game/src/Mind/AI/Provider/ClientProvider.cs`
- `game/src/Mind/AI/Provider/OpenAIClientProvider.cs`
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
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)

### External Dependencies

- Microsoft.Extensions.AI
- OpenAI .NET SDK
