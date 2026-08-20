---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

 The system must execute each AgenticMind as one long-running agent session — an append-only transcript driven through
 a sequence of bounded, stateless provider requests — through which the NPC observes the scene, deliberates, and acts
 with tools.

## Goal

 Let NPCs participate continuously in the scene through a single coherent provider conversation, while keeping every
 request stateless, keeping world information flowing only through tool results, and containing failures — without
 turn scheduling, synthetic completion markers, or request bounds.

## User Requirements

1. From the moment an NPC's Mind activates until it leaves the scene or suffers an unrecoverable failure, the NPC
   sustains one continuous session and behaves consistently with everything it observed, said, and did during that
   session.
2. Apart from runtime-driven interruption while it is already generating, the NPC receives updates about important
   scene events only through the `wait`
   tool. Waiting returns the notable observations accumulated since the previous wait, so an NPC that never waits
   receives no updates about important scene events.
3. A wait may finish early — when something important happens, or when a speaker the NPC attends to finishes speaking
   — and otherwise completes after its requested duration. Its result states the notable observations, how long the
   wait lasted, and the current game time.
4. The NPC can recall its own past at any time through the timeline history tool — exposed to the model as `history` —
   including minor events that wait results did not surface.
5. The NPC must not talk over a speaker it attends to: speech submitted while such a speaker's speaking window is open
   waits until that window closes.
6. When the NPC's speech is cut short by another event, the NPC learns this through the speak result rather than an
   error, and may react to the interruption.
7. Spoken responses must use the NPC's character-owned in-world voice rather than normal chat text, and successful
   speech must be remembered exactly once as the NPC's own observed speech.
8. Voice availability must constrain speech only, not whether the NPC can run the session or use other tools.
9. Failed, cancelled, malformed, or invalid actions must not be remembered as successful events.
10. The NPC decides how to handle failed actions: tool errors are returned as tool results, and the NPC chooses
    whether, when, and how to retry.
11. Temporary provider or network problems must not disturb the NPC: the runtime retries them transparently, and only
    retry exhaustion ends the session through the contained failure path.
12. All time-sensitive tool results report time in one consistent format: seconds of in-game time elapsed since the
    game began.
13. Removing an NPC's Mind from the scene must not allow active or queued actions to produce delayed in-world effects.
14. During development, developers can inspect `LoggingChatClient`
    request and response representations at the Microsoft.Extensions.AI abstraction for every model request when the
    dedicated request/response diagnostics controls permit it. These diagnostics do not promise complete HTTP wire
    bodies.
15. Sensitive AI request and response detail must remain suppressed unless both dedicated request/response diagnostics
    controls permit it, without changing NPC behaviour.
16. An action must never execute against a character other than the character that owns the Mind.
17. During development, developers can observe speech-pipeline latency diagnostics through CORE-007 logging — with the
    speak-tool invocation marker surfaced as an opt-in notification — without changing NPC behaviour.

## Technical Requirements

### Session Lifecycle

1. Exactly one logical agent session must exist per AgenticMind. The session starts when the Mind activates — after
   `_Ready()`, once perception subscriptions are in place — and ends on node exit or on fatal unrecoverable failure.
2. This iteration defines no session restart, re-anchoring, or re-prompting mechanism. A session ended by containment
   stays ended for the remainder of the Mind node's lifetime, while timeline ingestion and perception continue
   independently under AI-001.
3. The session is an append-only transcript executed as a sequence of bounded, stateless provider requests: one
   request, the tool calls it produced, and their results. No `end_turn`
   synthetic marker or similar completion protocol exists, and no `MaxModelRequests` or `MaxToolActions`
   bounds apply: the session is long-running.
4. The runtime must build on the already-referenced `Microsoft.Agents.AI`
   package wherever it provides the required session and agent abstractions without significant deviation. Custom
   behaviour must remain where the framework does not provide it, notably tool-only response validation and `wait`'s
   wake semantics.

### Session Prompt

5. Exactly once, at session start, the runtime must assemble the render context on demand, compile the configured
   `PromptStack`, and render the system prompt under AI-003. The rendered prompt stack is the session's sole system
   instruction. It is never re-rendered or refreshed, and nothing is frozen per turn: the previous
   freeze-at-turn-start behaviour is explicitly rejected as over-defensive, and render-context construction is on
   demand at session start only.
6. Session start must resolve the scenario once by querying the configured scenario manager with the freshly assembled
   core context, as normatively defined by [AI-008](../008-scenario/index.md), and must capture one SCN-001 scene
   snapshot that is retained for the complete session.
7. The session owner — `AgenticMind` — may supply a bootstrap input message (for example, `Begin. Participate in the
   scene using the available tools.`) with the first request. Both chat-client kinds must carry it. The provider
   supplies only the chat client: no run-message factory belongs on the provider. No observation-summary user message
   and no re-rendered instruction may supplement the session system instruction later in the session.

### Transcript Execution

8. Every provider request must send the complete ordered session transcript. OpenAI Responses must be the default
   provider transport: every Responses request must be stateless, set `store`
   to `false`, omit `previous_response_id`, and replay the full ordered history instead.
9. OpenAI Chat Completions may remain only as an explicitly selected rollback transport. The runtime must not fall
   back to it automatically, and it must preserve the same tool-only validation, ordering, and failure semantics.
10. Every provider request must require at least one tool call and must send no provider `response_format`
    or equivalent terminal-output schema. Every model response must act through tool calls: ordinary assistant text is
    never an accepted session result and never becomes player-visible chat.
11. The runtime must validate the complete response batch — every call, identifier, argument, and content item —
    before invoking any tool in that batch. Valid tool calls must execute serially in provider order.
12. Empty or malformed output, ordinary assistant text, unknown content or tools, invalid arguments, and duplicate
    call identifiers must fail before any batch effect, without a model repair attempt or automatic request retry.
    Model reasoning content in an assistant message is tolerated and skipped during validation.
13. `AllowMultipleToolCalls`
    must be a configurable runtime or provider preference and must default to `false`. It may guide provider
    generation but must not make an otherwise valid multi-call batch fail local validation.
14. After each successful batch, the runtime must append all assistant tool calls and the corresponding tool results
    to the transcript in order, then issue the next request by replaying the complete transcript. Only the system
    instruction, the optional bootstrap input message (TR-7), assistant tool calls, tool-result messages, and injected
    messages (TR-40) may enter the transcript;
    structured envelopes and ingested observations must not be exposed to the model beyond the tool-result message.
15. The transcript is session-scoped transient protocol and must be discarded when the session ends. The Mind timeline
    under [AI-001](../001-mind/index.md) is the only durable memory.

### Tool Inventory And Common Contracts

16. The production tool inventory is `speak`, `wait`, and the timeline history tool — exposed to the model as
    `history` (`HistoryTool`). Additional tools are explicitly
    deferred, and no legacy framework-managed generic terminal-result route may remain selectable.
17. Every `AgentTool`
    delegate must return the standard `AgentToolResult`, containing an optional model-facing `Message`
    and an ordered observation collection. A null message and an empty collection are valid.
18. For every production tool, the common `AgentTool`
    wrapper must submit the delegate exactly once through the shared `IMainThreadDispatcher`, then await and validate
    the complete result before exposing any part of it. The dispatcher guarantees main-thread affinity only for the
    delegate's initial invocation, not continuations after an incomplete await. The wrapper must ask the owning Mind
    to atomically ingest the ordered observations, then return only `Message`
    as the tool result.
19. Mind owns all observation mutation. The common `AgentTool` wrapper must keep Mind and `IMainThreadDispatcher`
    private. `ScenarioContext`
    ([AI-008](../008-scenario/index.md)) must expose only the typed `Character`, `SceneContext`, and nullable `Scenario`
    runtime bindings; concrete tool capabilities must come through `Character`. No invocation service bag, public
    observation recorder, or sink may be exposed.
20. Tools must not mutate Mind directly. Tool-result ingestion must stamp every `ObservedAction`
    with the owning character's exact actor ID before contextual importance is calculated, preventing actor spoofing.
21. A throwing, cancelled, malformed, wrong-shaped, or otherwise invalid tool result must contribute no observations.
    Validation and ingestion of that tool's observation batch must be all-or-nothing and preserve authored order.
22. Every production tool invocation must receive a `ScenarioContext`
    ([AI-008](../008-scenario/index.md)) containing the exact public properties `ICharacter Character`,
    `ISceneContext SceneContext`, and `Scenario? Scenario`. `SceneContext`
    must hold the snapshot captured once for the session and retain SCN-001's fixed-membership and live-reference
    semantics.
23. `ScenarioContext`
    is a trusted runtime binding. It must be excluded from the model-visible tool schema and must not be supplied,
    replaced, or overridden by model arguments. It must not implement or expose `IServiceProvider`, duplicate
    component query APIs, or act as a general service bag; concrete tools decide whether to consume typed Character
    traits or extensions, or use `ICharacter`'s CORE-003 `IServiceProvider`
    contract. Mind and `IMainThreadDispatcher` remain private to the common `AgentTool`
    wrapper, and cancellation remains a per-invocation wrapper input.
24. Before dispatcher submission or any world effect, the wrapper must verify that the context Character is the exact
    Character owned by the Mind boundary. An ownership mismatch must fail closed.

### The `speak` Tool

25. `SpeechTool` must:
    - reject blank input through the voice contract without producing a result observation;
    - block while an attended speaker is speaking: a voice attends iff its owning character's canonical `FullId`
      is present in Mind's current attention snapshot at or above the retention threshold (AI-006), regardless of
      weight or score. Voices whose speaker cannot be attributed to a current-scene character must not block; this is
      an accepted limitation of the attribution model. This blocking is the turn-taking guard and replaces the former
      turn-start speaking gate;
    - await the explicitly cancellable submission (SPCH-005 TR-25) through playback hand-off, passing the session
      cancellation token to the configured character-owned `IVoice.SpeakAsync(...)`;
    - return exactly one actorless `ObservedSpeech` in its `AgentToolResult` at hand-off, not at admission; and
    - optionally return a transient model-facing acknowledgement.
26. Playback hand-off, not admission, is the successful tool-action boundary. Failure or cancellation before hand-off
    must produce no observed speech (silent abort, SPCH-005 TR-25); cancellation after hand-off does not retract the
    committed item.
27. On interruption while speak is in flight, the tool must return early with a result stating that the speech was cut
    short by another event; it must not throw. The explicitly cancellable pre-hand-off submission must be cancelled
    silently — no `SpeechFailed`, no `IHearing`
    broadcast, no listener notification — and speech already at or past hand-off must be cut: audio and lip-sync stop
    through the shared `LipSyncPlayer`
    stop/cut capability (SPCH-001/SPCH-002). Ordinary non-tool callers retain admission-only semantics (SPCH-005 TR-25).
28. `SpeechTool` must resolve the raw `IVoice`
    from the context Character's authored component projection. It must not depend on an AgenticMind voice property,
    special case, or duplicate voice binding.
29. The configured output voice must remain excluded from external listening so dispatched self-speech is not recorded
    a second time as perceived speech.
30. Voice is a `SpeechTool` capability and must not be a generic session-runtime prerequisite.

### The `wait` Tool

31. `wait`
    must accept an optional duration argument with a sensible default of 10 seconds (today's `MaxObservationWaitSeconds`
    default).
32. A `wait` call must return the notable observations accumulated since the previous `wait`
    call, the elapsed wait duration, and a current game timestamp (TR-37).
33. A wait in progress must finish early when AI-001's cumulative-importance machinery makes accumulated observations
    notable, and when an attended speaker finishes speaking (the attended-speaker-finished cue, AI-001). The same
    attention-snapshot membership rule as speak blocking (TR-25) decides which speakers wake the wait.
34. Observations whose accumulated importance stays below the configured threshold must not be pushed into wait
    results. They remain in the timeline and are reachable through the `history` tool.
35. The `wait`
    tool description is the sole carrier of the tool's mechanics and etiquette. It must make clear that the tool's
    purpose is to observe the scene, not to pass time: without invoking it, the agent receives no updates about
    important scene events. It must include wait etiquette — for example, after asking another character a question,
    wait a reasonable duration before assuming refusal and reacting. The session prompt carries no per-tool mechanics;
    its guidance is cross-cutting only.

### The Timeline History Tool

36. The timeline history tool — `history`, implemented by `HistoryTool` — must let the agent query the Mind's committed
    observation records under AI-001 without relying on provider message logs. It must be read-only, preserve timeline
    order, and render records through the AI-003 event-history contract (authored through the standalone `EventHistory`
    resource exported by `AgenticMind`).

### Timestamps

37. All time-sensitive tool results must carry timestamps in one consistent format: seconds elapsed since the game
    began (in-game time; no timezones, no date-times). Observation `ObservedAt`
    stamps use the same format under AI-001.
38. A game-scoped game-time source (game clock) must exist as a Game-registered service contract — `IGameClock`
    under `AlleyCat.Core.Time`, exposing elapsed in-game seconds. For now in-game time advances with real time; no
    day/night cycle exists.

### Interruption

39. During a tool invocation, interruption must make the tool return early with a cut-short or interrupted result (for
    example speak, TR-27); committed actions and observations remain committed.
40. During model generation, interruption must cancel the in-flight request, discard partial assistant output, append
    the new information to the transcript as an injected message — observation content rendered through the AI-003
    event-history contract, with no prompt-stack dependency — and resume with a fresh request replaying the
    complete transcript. Partial assistant output must never be retained.
41. When AI-001's machinery makes new observations notable, it must signal the session runtime so the runtime applies
    TR-39 or TR-40 as applicable. Expected interruption must not be reported as a backend failure and must not trigger
    transport retry.

### Failure And Cancellation

42. Tool errors must be reported through the tool result so the agent decides whether, when, and how to retry.
43. Transport-level failures — network errors, rate limits such as 429, and timeouts — must be handled transparently
    by the runtime with bounded retry. They must not be surfaced to the agent as tool results or transcript entries.
    Retry exhaustion must end the session through the contained failure path: logged and contained without crashing
    the scene, with no model repair attempt and no automatic session restart.
44. Node-lifetime cancellation from AI-001 must propagate through active requests and tool work. Expected interruption
    and lifetime cancellation must not trigger retry, further unintended session activity, or misleading failure
    diagnostics.
45. Queued or deferred tool tasks must settle when Mind exits, without dispatch or successful observation. Deferred
    callbacks must not access services from the exited node.
46. AI-001 is normative for node lifetime, actor stamping, atomic ingestion, notable-observation accumulation, and
    wake signalling. AI-003 is normative for session-start prompt compilation and event-history rendering. AI-008 is
    normative for session-start scenario resolution.

### Diagnostics

47. Development-only Microsoft.Extensions.AI request and response diagnostics must require both
    `Diagnostics:AI:EnableRequestResponseLogging`
    and the dedicated `Microsoft.Extensions.AI.LoggingChatClient`
    category enabled at `Trace`. The option is enabled by default and acts as an off-switch: setting it to `false`
    suppresses sensitive payload detail even when the `Microsoft.Extensions.AI.LoggingChatClient`
    category is enabled at `Trace`. Either control being disabled must suppress sensitive payload detail.
48. The runtime must decorate its AI `IChatClient` with Microsoft.Extensions.AI `LoggingChatClient`
    before session execution. This placement must observe every sequential provider request of the session.
49. `LoggingChatClient`
    diagnostics represent requests and responses at the Microsoft.Extensions.AI abstraction and must not be described
    as complete HTTP wire-body capture. Serialisation must be deferred until the complete diagnostics gate in TR-47 is
    satisfied. CORE-007 is normative for reusable logging and deferred serialisation.
50. AI request and response diagnostics must not use shared `System.ClientModel`
    body logging. Their scope must remain the agent-runtime client so speech transcription and generation traffic is
    unaffected.
51. Diagnostics must not change request count, tool calls, results, cancellation, actions, validation, or failure
    behaviour.
52. Development-only structural transport evidence may report configured tool names, required tool choice, and
    response-format absence when explicitly gated. It must exclude message bodies, generated content, credentials, and
    other secrets and must not be presented as complete wire logging.
53. Model reasoning content (`TextReasoningContent`) in an assistant message is tolerated and skipped during
    validation. It is never treated as ordinary assistant text, never becomes player-visible chat, and is never stored
    as memory, remaining transient session protocol. Reasoning text may be logged at trace level as a development-only
    diagnostic only when `Diagnostics:AI:EnableReasoningLogging`
    is enabled and the `AlleyCat.Mind.AI.AgenticMind`
    logger category is enabled at `Trace`. The option is enabled by default and acts as an off-switch: setting it to
    `false`
    suppresses reasoning logging regardless of the trace level. Reasoning logging is governed by its own dedicated
    control, distinct from the `EnableRequestResponseLogging`
    gate for MEAI `LoggingChatClient`
    payload logging in TR-47. It must not change NPC behaviour, validation, action execution, or failure semantics.
54. `SpeechTool` must record a pipeline marker through the shared pipeline diagnostic log (CORE-007) once the final
    speech text is accepted and before the turn-taking wait begins, so latency measured from the preceding model
    response to the speak invocation is not polluted by time spent waiting for another speaker. The marker is
    diagnostics-only and must not change tool behaviour. Session-end latency measurements must remain log-only and
    never become notifications.

## In Scope

- One long-running agent session per AgenticMind: append-only transcript, bounded stateless requests, no restart
  mechanism.
- Once-per-session prompt compilation and rendering with on-demand render-context assembly and one session-captured
  scene snapshot.
- Tool-only validation without completion markers or request and action bounds; full-transcript replay on every request.
- The `speak`, `wait`, and timeline history (`history`) tool inventory, including the standard `AgentToolResult`
  contract.
- `speak`
  blocking turn-taking, cut-short interruption results, playback hand-off as the success boundary, and exactly-once
  observed-speech production.
- `wait`
  notable-observation delivery, early finish on importance and attended-speech end, and observe-not-sleep guidance.
- Read-only timeline recall through the `history` tool.
- The game-time convention for all time-sensitive tool results and the game-scoped game clock.
- Interruption semantics for tool invocations and model generation, including injected-message resumption.
- Tool errors as tool results, transparent bounded transport retry, and contained session-ending failure.
- Trusted typed `ScenarioContext`
  binding, ownership verification, shared-dispatcher tool start, actor stamping, and atomic Mind hand-off.
- Responses-default stateless transport and explicitly selected Chat Completions rollback.
- Adoption of `Microsoft.Agents.AI` within the stated deviation boundary.
- Development-only MEAI diagnostics and non-secret structural transport evidence with explicit gating.
- Speech-pipeline latency diagnostics through the shared pipeline diagnostic log: the speak-boundary marker before the
  turn-taking wait and log-only session-end latency (CORE-007 is normative for routing).

## Out Of Scope

- Context exhaustion handling and transcript compaction; explicitly deferred — short testing sessions only for now.
- Session restart, re-anchoring, or mid-session re-prompting.
- Additional production tools beyond `speak`, `wait`, and the timeline history tool.
- Model repair or automatic retry of invalid model output.
- Cancelling or reversing non-speech world actions already admitted before interruption or a later failure.
- Speech playback-finished success semantics.
- Timeline summarisation, compaction, token budgeting, persistence, and provider transcript retention beyond the
  session.
- A day/night cycle or non-real-time game clock advancement.
- Voice as a requirement for generic non-speech session execution.
- Multi-agent orchestration and guidance-agent APIs.
- Complete or production HTTP wire-body logging.

## Acceptance Criteria

### User Requirements

1. Session-continuity coverage verifies an NPC sustains exactly one session from activation to scene removal or
   unrecoverable failure, with no restart or re-anchoring, and later behaviour reflects earlier observations, speech,
   and actions of the same session.
2. Wait-delivery coverage verifies notable observations accumulated since the previous wait are returned together with
   the elapsed duration and a game timestamp, that important arrivals and an attended speaker finishing speech finish
   the wait early, and that quiet expiry returns no sub-threshold observations.
3. Acceptance verifies an NPC that has not invoked `wait`
   receives no updates about important scene events, and that the `wait`
   tool description frames waiting as observation rather than passing time, including question-then-wait etiquette.
4. Turn-taking coverage verifies an NPC does not begin speech while an attended speaker's window is open, and that
   speech cut short by another event is reported through the speak result rather than an error, allowing the NPC to
   react.
5. Speech and action coverage verifies character-owned in-world voice, exactly-once own observed speech, no false
   memory of failed or cancelled actions, and voice availability constraining speech only.
6. Failure coverage verifies tool errors surface as tool results for the NPC to act on, while transport failures are
   invisible to the NPC until bounded retry exhaustion ends the session through containment without crashing the
   scene.
7. Timestamp coverage verifies all time-sensitive tool results report seconds of in-game time elapsed since the game
   began, with no timezones or date-times.
8. Acceptance verifies containment and safety: missing configuration, backend failure, retry exhaustion, cancellation,
   and node exit never crash the scene and never produce delayed in-world effects, and an ownership mismatch produces
   no world effect.
9. Diagnostics coverage verifies speech-pipeline latency diagnostics remain opt-in through the `AlleyCat.Pipeline`
   category's log level (CORE-007) and change no NPC behaviour.

### Technical Requirements

1. Session-lifecycle tests verify one session per AgenticMind, started after `_Ready()`
   once perceptions are subscribed, ended on node exit or fatal unrecoverable failure, with no restart, re-anchoring,
   or re-prompting route.
2. Transport tests verify OpenAI Responses is the default and every request sets `store: false`, omits
   `previous_response_id`, and replays the complete ordered transcript; Chat Completions is available only through
   explicit selection, is never an automatic fallback, and preserves the tool-only semantics.
3. Protocol tests verify no `end_turn` marker, no `MaxModelRequests` or `MaxToolActions`
   bounds, and no provider response format exist, and that every request requires at least one tool call.
4. Validation tests reject empty or malformed responses, ordinary assistant text, unknown content and tools, invalid
   arguments, and duplicate call identifiers before executing any tool in an invalid batch, tolerate model reasoning
   content, and verify no model repair or automatic request retry occurs.
5. Prompt tests verify the stack is compiled and rendered exactly once per session with the render context assembled
   on demand at that point; the transcript contains only the system instruction, the optional bootstrap input message,
   assistant tool calls, tool-result messages, and injected messages; and the transcript is discarded at session end
   while the Mind timeline persists.
6. Scenario tests verify one manager query at session start with the freshly assembled core context, and one
   session-captured SCN-001 snapshot serving the prompt render and every tool invocation with fixed-membership and
   live-reference semantics.
7. Tool tests verify delegates return one `AgentToolResult`; the common wrapper validates it, submits each delegate
   exactly once through `IMainThreadDispatcher`, makes no continuation-affinity claim, atomically hands ordered
   observations to Mind, and exposes only the optional transient message as the tool result.
8. Context tests verify `ScenarioContext`'s exact public surface, its absence from the model-visible tool schema, its
   resistance to model supply or override, the absence of `IServiceProvider`
   and duplicate component APIs, wrapper privacy of Mind and `IMainThreadDispatcher`, per-invocation cancellation, and
   ownership-mismatch failure before dispatcher submission or world effects.
9. Ingestion tests verify Mind stamps tool action actors with the owning character ID, prevents spoofing, provides no
   public observation recorder, sink, or direct tool-mutation path, and ingests ordered observation batches
   atomically.
10. Speak tests verify blank-input rejection, the attended-speaker blocking filter including the unattributable-voice
    exclusion, silent pre-hand-off cancellation, cutting of already-audible speech through the shared `LipSyncPlayer`
    stop/cut capability, the non-throwing cut-short result on interruption, the playback hand-off success boundary
    with no retraction after hand-off, exactly one actor-stamped self-relative `ObservedSpeech`, self-listener
    exclusion, and resolution of the Character-authored `IVoice`
    through the typed context.
11. Wait tests verify the default duration of 10 seconds, delivery of the notable window accumulated since the
    previous wait, early finish on the cumulative-importance threshold and on the attended-speaker-finished cue, the
    elapsed-duration and game-timestamp result fields, and that sub-threshold observations never enter wait results
    while remaining reachable through the `history` tool.
12. History tests verify the `history` tool is read-only, preserves timeline order, and answers from the Mind timeline
    rather than provider message logs.
13. Game-clock tests verify a game-scoped game-time source (`IGameClock`) exists, is resolvable from the Game service
    provider, advances with real time, and backs every time-sensitive tool-result timestamp and `ObservedAt` stamp.
14. Interruption tests verify a tool in flight returns a cut-short or interrupted result, generation in flight is
    cancelled with partial assistant output discarded and the new information appended as an injected message before a
    fresh full-transcript request, committed actions and observations survive, and expected interruption produces no
    backend-failure diagnostics or retry.
15. Failure tests verify tool errors are returned through tool results, transport failures are retried transparently
    with bounded retry and never surfaced to the agent, and retry exhaustion ends the session through the contained
    failure path.
16. Diagnostics tests verify the dual `LoggingChatClient`
    request/response gate with deferred serialisation and either-control suppression, decoration before session
    execution, unchanged behaviour with diagnostics enabled or disabled, isolation from STT and TTS traffic and shared
    `System.ClientModel`
    body logging, gated non-secret structural evidence, and the separate reasoning-logging gate with its off-switch
    default.
17. Node-exit tests verify active and queued work settles without delayed dispatch, successful observation, retry,
    exited-node service access, or erroneous expected-cancellation diagnostics.
18. Tests verify the runtime builds on `Microsoft.Agents.AI`
    where it fits and retains custom tool-only validation and wait wake semantics where the framework does not provide
    them, with no legacy generic terminal-result route selectable.
19. Diagnostics tests verify the speak-boundary pipeline marker fires after final speech acceptance and before the
    turn-taking wait, changes no tool behaviour, and that session-end latency remains log-only.

## References

### Implementation

- `game/src/Mind/AI/AgenticMind.cs`
- `game/src/Mind/AI/` session runtime (replacing `ToolOnlyTurnRunner.cs`)
- `game/src/Mind/AI/AIChatClientDiagnostics.cs`
- `game/src/Mind/AI/AIDiagnosticsOptions.cs`
- `game/src/Mind/AI/Provider/ClientProvider.cs`
- `game/src/Mind/AI/Provider/OpenAIClientProvider.cs`
- `game/src/Mind/AI/Tool/AgentTool.cs`
- `game/src/Mind/AI/Tool/AgentToolResult.cs`
- `game/src/Mind/AI/Tool/SpeechTool.cs`
- `game/src/Mind/AI/Tool/` `wait` and the timeline history tool (`HistoryTool`) (new)
- `game/src/Core/Time/IGameClock.cs` (new)
- `game/src/Core/Time/GameClock.cs` (new)
- `game/src/Mind/Mind.cs`
- `game/src/Mind/Observation/Observation.cs`

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-008: Scenario](../008-scenario/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)
- [CORE-010: Main-Thread Dispatcher](../../core/010-main-thread-dispatcher/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)

### External Dependencies

- Microsoft.Extensions.AI
- Microsoft.Agents.AI
- OpenAI .NET SDK
