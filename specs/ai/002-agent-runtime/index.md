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
   appropriate. When no action result is needed, it may perform its chosen actions and complete the turn in one model
   response, avoiding an unnecessary follow-up request.
2. `speak` must be an optional in-world action and must not end the turn.
3. A turn with no actions must end directly without producing player-visible assistant text.
4. Successful actions that matter to the NPC's experience must be available to later turns as self-observations.
5. Failed, cancelled, malformed, or invalid actions must not be remembered as successful events.
6. When an NPC needs action results to decide whether to act again or finish, those results must be returned during the
   active turn for a later model response. This feedback may remain transient and must not become player-visible chat or
   durable character memory by default.
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
14. An action must never execute against a character other than the character that owns the active Mind turn.

## Technical Requirements

1. AgenticMind must create a fresh provider client execution context for every turn. It must not retain an agent,
   provider response identifier, completed assistant/tool transcript, or first-turn prompt snapshot across turns.
2. At turn start, AgenticMind must resolve the current scene and owning character, compile the configured `PromptStack`,
   call foreground `CreateRenderContext`, render the template with that exact dictionary, resolve current action tools,
   and start one explicit tool-only request loop. It must publish that exact dictionary as the latest snapshot only
   after rendering succeeds. Construction or rendering failure must retain the prior snapshot and enter the containment
   path.
3. The rendered prompt stack must remain the turn's sole system instruction. Provider-required bootstrap input may be
   sent, but no prior transcript or per-batch observation-summary message may cross turn boundaries.
4. Every model request in the loop must require at least one tool call and must send no provider `response_format` or
   equivalent terminal-output schema. Ordinary assistant text is never an accepted turn result.
5. The runtime must register every configured production action plus the exact reserved synthetic marker `end_turn` on
   every request. A configured action must not use the reserved name.
6. `end_turn` is protocol control, not an action, action result, or observation. It must accept no arguments, must never
   invoke a production tool delegate, produce a tool result, be ingested by Mind, or count towards `MaxToolActions`.
7. A response containing `end_turn` is valid only when the marker occurs exactly once as the final call and the response
   contains no ordinary assistant text or other non-call protocol content. The marker may be the sole call for zero
   actions or may follow one or more configured production action calls.
8. A response without `end_turn` is valid only when it contains one or more well-formed calls to configured production
   actions and no ordinary assistant text, unknown content, unknown tool, duplicate call identifier, or other malformed
   item.
9. Zero actions must be represented by sole `end_turn`. One or more actions may be followed by final `end_turn` in the
   same response when no result-dependent continuation is needed. An action-only response requires result replay and a
   later sequential model request.
10. The runtime must validate the complete response batch, including all calls, marker placement, identifiers,
    arguments, content, and bounds, before invoking any production action in that batch. Valid production actions must
    execute serially in provider order, including those before a final `end_turn` marker.
11. `AllowMultipleToolCalls` must be a configurable runtime or provider preference and must default to `false`. It may
    guide provider generation but must not make an otherwise valid multi-action batch fail local validation.
12. After each successful action-only batch, the runtime must append all assistant function calls and corresponding tool
    results to transient per-turn history, then issue the next request by replaying the complete history in order. After
    all production actions in a valid final-marker batch succeed, the turn must finish without replaying that batch or
    issuing another provider request.
13. Transient request history must be discarded when the turn settles. The Mind timeline under
    [AI-001](../001-mind/index.md) is the only cross-turn memory.
14. The runtime must enforce the named bounds `MaxModelRequests` and `MaxToolActions`, with normative defaults of `8`
    requests and `8` production actions per turn. These may remain constants when no settings surface exists. If
    exposed, both bounds must be configurable positive integers with the stated defaults.
15. `MaxModelRequests` counts every provider request, including the request that returns `end_turn`.
    `MaxToolActions` counts production action calls but not `end_turn`.
16. A batch that would exceed `MaxToolActions` must fail before any call in that batch executes. Reaching
    `MaxModelRequests` without a valid `end_turn` must fail once another request would be required.
17. Empty or malformed output, assistant text, unknown content or tools, invalid arguments, duplicate call identifiers,
    and a non-final, repeated, or malformed `end_turn` marker must fail before any batch effect. Tool errors and bound
    exhaustion must also fail closed. The runtime must not ask the model to repair output, retry the failed request or
    action, or continue the turn.
18. Valid sequential requests after successful actions are protocol continuation, not repair or retry. Actions and
    observations committed before a later failure or pre-emption remain committed.
19. Every `AgentTool` delegate must return the standard `AgentToolResult`, containing an optional model-facing `Message`
    and an ordered observation collection. A null message and an empty collection are valid.
20. For every production action, the common `AgentTool` wrapper must submit the action delegate exactly once through
    the shared `IMainThreadDispatcher`, then await and validate the complete result before exposing any part of it.
    The dispatcher guarantees main-thread affinity only for the delegate's initial invocation, not continuations after
    an incomplete await. The wrapper must ask the owning Mind to atomically ingest the ordered observations, then
    return only `Message` as the tool result.
21. Mind owns all observation mutation. The common `AgentTool` wrapper must keep Mind and `IMainThreadDispatcher`
    private. `AgentToolContext` must expose only the typed `Character` and `SceneContext` runtime bindings; concrete
    action capabilities must come through `Character`. No invocation service bag, public observation recorder, or sink
    may be exposed.
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
42. AI-005 ContextWorker runs are separate background projections, not AgenticMind foreground turns. They capture the
    latest foreground-published render dictionary at run start and must not construct or aggregate context. They must
    not weaken this runtime's tool-only, no-terminal-response-schema, or no-assistant-text guarantees.
43. The cancellation token supplied to each `AgentTool` dispatcher submission must remain linked to the active turn and
    Mind node lifetime. The shared Game-scoped dispatcher owns queueing and settlement of accepted submissions;
    AgenticMind must not retain local deferred voice or Godot-action queueing or settlement machinery.
44. Every production tool invocation must receive an `AgentToolContext` containing the exact public properties
    `ICharacter Character` and `ISceneContext SceneContext`. `SceneContext` must hold the snapshot captured once for the
    turn and retain SCN-001's fixed-membership and live-reference semantics.
45. `AgentToolContext` is a trusted runtime binding. It must be excluded from the model-visible tool schema and must not
    be supplied, replaced, or overridden by model arguments.
46. `AgentToolContext` must not implement or expose `IServiceProvider`, duplicate component query APIs, or act as a
    general service bag. Concrete tools decide whether to consume typed Character traits or extensions, or use
    `ICharacter`'s CORE-003 `IServiceProvider` contract.
47. Mind and `IMainThreadDispatcher` remain private to the common `AgentTool` wrapper and must not be exposed through
    `AgentToolContext`. Cancellation remains a per-invocation wrapper input and must not become shared context state.
48. Before dispatcher submission or any world effect, the wrapper must verify that the context Character is the exact
    Character owned by the Mind boundary. An ownership mismatch must fail closed.
49. `SpeechTool` must resolve the raw `IVoice` from the context Character's authored component projection. It must not
    depend on an AgenticMind voice property, special case, or duplicate voice binding.
50. `ISceneContext` may later be replaced by a dedicated turn-context contract without changing the current requirement
    to pass the turn-captured SCN-001 snapshot.

## In Scope

- Permanent production use of fresh, bounded tool-only execution for every turn.
- Zero actions represented by sole `end_turn`, or one or more serial actions optionally followed by final `end_turn` in
  the same response.
- Required tool choice, local batch validation, transient full-history replay for action-only continuation, and
  fail-closed handling.
- Configurable multiple-call preference and named request and action bounds.
- Responses-default stateless transport and explicitly selected Chat Completions rollback.
- Standard `AgentToolResult` validation, projection, and atomic Mind hand-off.
- Trusted typed `AgentToolContext` binding through `Character` and turn-captured `SceneContext` properties.
- Shared-dispatcher start of every outbound production tool, with turn- and Mind-lifetime cancellation.
- Speech admission, transient acknowledgement, and exactly-once observed-speech production.
- Expected interruption and node-lifetime cancellation settlement.
- Preservation of foreground tool-only protocol boundaries while AI-005 workers run independently.
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

### User Requirements

1. Tests verify a zero-action turn returns sole `end_turn` on its first response, executes no action, creates no
   observation, and produces no accepted or player-visible assistant text.
2. Tests verify one model response can call `speak` and then final `end_turn`, executing speech once and completing the
   turn without result replay or another provider request. Equivalent final-marker batches with multiple production
   actions execute each action serially in provider order before completion.
3. Acceptance verifies speech and other actions remain bounded, failures produce no false memory, interruption and node
   exit prevent delayed effects, and ownership mismatch produces no world effect.

### Technical Requirements

1. Tests verify every request requires a tool call, registers all configured actions plus `end_turn`, and sends no
   provider response format or terminal schema.
2. Tests verify `end_turn` is reserved, argument-free, accepted at most once and only as the final call, and may be sole
   or follow one or more production actions. It is never invoked, returned as a result, ingested, or counted as an
   action.
3. Tests reject empty or malformed responses, assistant text, unknown content and tools, duplicate call identifiers,
   invalid arguments, and non-final, repeated, or malformed `end_turn` before executing any action in the invalid batch.
4. Tests verify `AllowMultipleToolCalls` is configurable and defaults to `false`, while local validation still accepts a
   valid all-action batch and executes it serially.
5. Tests verify a successful action-only batch replays all calls and results in full on a later model request, including
   when the model needs those results to continue. A final-marker batch performs no such replay or request. Transient
   history is discarded at turn end, and a later turn receives only its newly rendered instruction, provider bootstrap
   input, and Mind timeline context.
6. Tests verify `MaxModelRequests` and `MaxToolActions` default to `8`, count requests and production actions
   respectively, reject a batch that would exceed the action bound before execution, and stop when the request bound is
   exhausted without `end_turn`.
7. Tests verify malformed, unknown, text, invalid-marker, tool-error, backend-error, and bounds failures stop without
   model repair, request or action retry, or continued execution; earlier actions and observations committed before a
   later action fails remain committed.
8. Responses transport tests verify it is the default and that every sequential request sets `store: false`, omits
    `previous_response_id`, and replays complete ordered per-turn history.
9. Configuration and transport tests verify Chat Completions is available only through explicit selection, is never an
    automatic fallback, and preserves the tool-only protocol semantics.
10. Tool tests verify delegates return one `AgentToolResult`; the common wrapper validates it, atomically hands ordered
    observations to Mind, and exposes only the optional transient message as the tool result. They also verify each
    production delegate starts exactly once through `IMainThreadDispatcher` and make no continuation-affinity claim.
11. Tests verify Mind stamps tool action actors with the owning character ID, prevents spoofing, and provides no public
    observation recorder, sink, or direct tool-mutation path.
12. Speech tests verify exactly one Mind actor-stamped, self-relative `ObservedSpeech` after successful voice admission
    and none for blank, unavailable, unconfigured, failed-before-admission, or cancelled requests.
13. Speech tests verify admission does not await playback and self-listener exclusion prevents duplicate observed
    speech.
14. Interruption tests verify expected cancellation settles requests and tools before one fresh replacement starts,
    committed action observations survive, and no turns overlap.
15. A capturing client verifies every sequential request is logged at the Microsoft.Extensions.AI abstraction only when
    the diagnostics option and `Microsoft.Extensions.AI.LoggingChatClient` `Trace` category are both enabled.
16. Suppression tests verify either disabled diagnostics control prevents serialisation of sensitive payload detail.
17. Integration tests verify `LoggingChatClient` decorates the tool-only client, shared `System.ClientModel` body
    logging is not used, and STT and TTS request and response logging is unaffected.
18. Equivalent runs with diagnostics enabled and disabled preserve requests, tool calls, results, cancellation,
    validation, actions, completion, and failure handling.
19. Node-exit tests verify active and queued work settles without delayed dispatch, successful observation, retry,
    replacement, exited-node service access, or erroneous expected-cancellation diagnostics.
20. Acceptance verifies both NPC-visible action and speech behaviour and the tool-only protocol, provider transport,
    transient-history, observation-ingestion, diagnostics, cancellation, settlement, and failure contracts.
21. Tests verify every production turn uses the explicit tool-only loop and no diagnostic flag or legacy generic
    terminal-result route can select a competing execution path.
22. Tests verify foreground execution publishes its exact render dictionary only after successful template rendering.
    ContextWorker execution captures the latest published snapshot without constructing or aggregating context and
    cannot alter the foreground route, tool-only requirement, or absence of a terminal response schema.
23. Node-exit and interruption tests verify each `AgentTool` submission retains turn- and Mind-lifetime cancellation;
    the shared Game-scoped dispatcher settles accepted work, and AgenticMind has no local deferred action queue or
    settlement path.
24. Tests verify every tool receives a trusted `AgentToolContext` whose exact public properties are the owning
    `ICharacter Character` and turn-captured `ISceneContext SceneContext`; the context is absent from model schema and
    cannot be model supplied or overridden.
25. Tests verify `AgentToolContext` exposes neither `IServiceProvider` nor duplicate component APIs, while concrete
    tools may use typed Character capabilities or the Character's inherited CORE-003 provider contract.
26. Tests verify Mind and `IMainThreadDispatcher` remain wrapper-private, cancellation remains per invocation, and a
    Character ownership mismatch fails before dispatcher submission or world effects.
27. Speech tests verify `SpeechTool` resolves the raw Character-authored `IVoice` through its typed context and has no
    AgenticMind output-voice special case or duplicate voice binding.
28. Scene-context tests verify the supplied snapshot retains SCN-001 fixed-membership and live-reference semantics for
    the complete turn.

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
- [AI-005: Context Worker](../005-context-worker/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)
- [CORE-010: Main-Thread Dispatcher](../../core/010-main-thread-dispatcher/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)

### External Dependencies

- Microsoft.Extensions.AI
- OpenAI .NET SDK
