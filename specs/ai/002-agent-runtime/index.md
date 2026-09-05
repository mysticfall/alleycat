---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

 The system must execute each AgenticMind as one long-running agent session — a transcript that is append-only after
 each accepted response, driven through a sequence of bounded, stateless provider requests — through which the NPC
 observes the scene, deliberates, and acts with tools.

## Goal

 Let NPCs participate continuously in the scene through a single coherent provider conversation, while keeping every
 request stateless, keeping world information flowing only through tool results, and containing failures — without
 turn scheduling, synthetic completion markers, or request bounds.

## User Requirements

1. From the moment an NPC's Mind activates until it leaves the scene or suffers an unrecoverable failure, the NPC
   sustains one continuous session and behaves consistently with everything it observed, said, and did during that
   session.
2. Ordinary important scene events reach the NPC through the `wait`
   tool and, when no wait owns them, through one coalesced injected message at the next model request the session
   would make naturally — so an NPC that never waits still receives them, without extra requests and without
   cancelling work in progress. Newly observed non-self speech instead immediately replaces the NPC's stale
   reasoning as a fresh turn, regardless of importance or attention (AI-001).
3. A wait may finish early — when accumulated observations become important, when a fresh observation arrives
   regardless of importance, or when a speaker the NPC attends to finishes speaking — and otherwise completes after
   its requested duration. Its result states the delivered observations, how long the wait lasted, and the current
   game time.
4. The NPC can recall its own past at any time through the timeline history tool — exposed to the model as `history` —
   including minor events that wait results did not surface.
5. The NPC must not talk over a speaker it attends to: speech submitted while such a speaker's speaking window is open
   waits until that window closes.
6. When the NPC's speech is withdrawn before it becomes audible — because a fresh observation replaced the stale
   turn, or because an attended speaker began or resumed speaking before the submission was admitted — the NPC
   learns this through the speak result rather than an error, and may react. Speech successfully admitted into the
   voice pipeline before an attended speaker's onset or resume cue arrives is protected: the matching suppression
   never cuts it, and it settles naturally into audible, remembered speech. Speech that has become audible is
   committed and is never cut by observation freshness; playback hand-off remains the point of no return.
7. Spoken responses must use the NPC's character-owned in-world voice rather than normal chat text, and successful
   speech must be remembered exactly once as the NPC's own observed speech.
8. Voice availability must constrain speech only, not whether the NPC can run the session or use other tools.
9. Failed, cancelled, malformed, or invalid actions must not be remembered as successful events.
10. The NPC decides how to handle failed actions: tool errors are returned as tool results, and the NPC chooses
    whether, when, and how to retry.
11. Temporary provider or network problems must not disturb the NPC: the runtime retries transport failures
    transparently, and discards and recovers from a transient invalid provider response with a fresh request so the NPC
    conversation continues. Only exhaustion of the applicable bounded recovery budget ends the session through the
    contained failure path.
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
18. A user can launch the game with the `--no-ai` user argument — passed after Godot's `--` separator — so NPCs keep
    perceiving, attending, and orienting while never contacting an AI provider: a playtesting mode with zero LLM
    traffic and a single clear notice that AI is disabled.
19. When an observed speaker resumes after a brief pause, the NPC immediately stops any reaction built on the
    incomplete utterance — before the continued words are transcribed — and its next request contains the complete
    utterance as one message, so the NPC never replies to a half-finished sentence and never sees duplicate partial
    messages. Reactions the NPC already committed remain visible and are explained to it as history.
20. After a brief endpoint pause, the NPC can begin reacting to the spoken segment promptly, without waiting for
    the longer continuation interval to elapse; if the speaker then continues, that early reaction is withdrawn
    before it lands and is redone against the complete utterance.
21. When an NPC's in-world voice does not implement the speech-admission capability, its speech follows ordinary
    withdrawal semantics instead of admission arbitration: speech that is not yet audible is withdrawn silently —
    reported through the speak result so the NPC may react — when a fresh turn or an attended speaker's onset or
    resume invalidates it, while speech that has become audible stays committed and is never cut. This ordinary-voice
    exception is intentional and specified, not a fault.

## Technical Requirements

### Session Lifecycle

1. Exactly one logical agent session must exist per AgenticMind. The session starts when the Mind activates — after
   `_Ready()`, once perception subscriptions are in place — and ends on node exit or on fatal unrecoverable failure.
2. This iteration defines no session restart, re-anchoring, or re-prompting mechanism. A session ended by containment
   stays ended for the remainder of the Mind node's lifetime, while timeline ingestion and perception continue
   independently under AI-001.
3. The session transcript is append-only after response acceptance and is executed as a sequence of bounded,
   stateless provider requests: one request, the tool calls it produced, and their results. Before an assistant
   response is accepted, a pending user-turn injection may be replaced under TR-56–TR-61; after acceptance, the
   transcript only grows. No `end_turn`
   synthetic marker or similar completion protocol exists, and no `MaxModelRequests` or `MaxToolActions`
   bounds apply: the session is long-running.
4. The runtime must build on the already-referenced `Microsoft.Agents.AI`
   package wherever it provides the required session and agent abstractions without significant deviation. Custom
   behaviour must remain where the framework does not provide it, notably tool-only response validation and `wait`'s
   wake semantics.

### AI Suppression Switch

55. The exact user argument `--no-ai` — passed among the user args after Godot's `--` separator — must suppress every
    agent session process-wide. The switch is a plain presence check over the process's command-line user arguments
    (`OS.GetCmdlineUserArgs()`) with no precedence interaction with `--integration-test-session` or
    `--integration-probe`;
    it resolves lazily on first access and is memoised once per process. The gate point is an early return in
    `AgenticMind.StartSession()` before `PrepareSessionAsync()`, so prompt compilation, scenario resolution, and
    chat-client creation never run and provider-configuration failures are not logged for suppressed minds. The
    one-shot `_sessionStarted` guard is still consumed, so a suppressed Mind can never start a session later in its
    node lifetime. Suppression emits exactly one Information-level notice per process naming the switch — never Error,
    so log-asserting tests stay clean. An internal test seam (force/reset, also resetting the notice guard) enables
    targeted integration tests without launch plumbing, and suppression has no effect on `Mind.Enabled` or AI-001
    perception and attention.

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
    call identifiers must fail validation before any batch effect. Invalid-response recovery is governed by TR-43;
    no model repair attempt or invalid-output feedback message may be added. Model reasoning content in an assistant
    message is tolerated and skipped during validation.
13. `AllowMultipleToolCalls`
    must be a configurable runtime or provider preference and must default to `false`. It may guide provider
    generation but must not make an otherwise valid multi-call batch fail local validation.
14. After each successful batch, the runtime must append all assistant tool calls and the corresponding tool results
    to the transcript in order, then issue the next request by replaying the complete transcript. Only the system
    instruction, the optional bootstrap input message (TR-7), assistant tool calls, tool-result messages, and
    injected messages (TR-39, TR-40, TR-58, TR-60) may enter the transcript;
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
    observation recorder, or sink may be exposed. The common tool-session binding is held to the same boundary: it
    must expose no feature services, and concrete capabilities bind typed to their concrete tool only, at
    composition — speech-admission arbitration to the `speak` tool, wait-delivery acknowledgement to the `wait`
    tool — never through the shared session, a nullable capability bag, a service locator, or a keyed capability
    dictionary.
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
    wrapper, and cancellation remains a per-invocation wrapper input. This no-service-bag rule bounds every
    session-scoped tool binding, not only `ScenarioContext` (TR-19).
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
    - arbitrate admission against attended start/resume suppression when the resolved voice implements the
      SPCH-005 admission capability — otherwise the ordinary-voice fallback of TR-63 applies: the submission takes
      the voice pipeline's
      submission lock and then the agent-runner state lock — the normative order (TR-56). When a matching attended
      start or resume hold linearised first, admission is refused with no TTS request, queue item, `IHearing` event,
      or self-observation, and the tool returns the not-delivered result of TR-27 rather than throwing; when
      admission completes first, the submission is protected for its whole pipeline life (TR-26, TR-40);
    - await the explicitly cancellable submission (SPCH-005 TR-25) through playback hand-off, passing a cancellation
      token that covers session lifetime and fresh-turn invalidation — except that a matching attended start or
      resume hold must never cancel an admitted submission of a capability voice (TR-26, TR-63) — to the configured
      character-owned
      `IVoice.SpeakAsync(...)`;
    - return exactly one actorless `ObservedSpeech` in its `AgentToolResult` at hand-off, not at admission; and
    - optionally return a transient model-facing acknowledgement.
26. Playback hand-off, not admission, is the successful tool-action boundary. Admission into the voice pipeline
    (AIVoice queue admission, SPCH-005) is separately the protection boundary: a submission successfully admitted
    before a matching attended start or resume cue linearises (TR-56) must settle naturally — TTS, playback hand-off,
    self-observation, natural tool result — and the matching onset, resume, or completed-text invalidation must never
    cancel it, while unrelated fresh observations and node-lifetime cancellation retain their ordinary pre-hand-off
    cancellation. Failure or cancellation before hand-off must otherwise produce no observed speech (silent abort,
    SPCH-005 TR-25); cancellation after hand-off does not retract the committed item.
27. When fresh-turn invalidation withdraws a speak in flight before playback hand-off, the tool must return early
    with a result stating that the speech was not delivered; it must not throw. The explicitly cancellable
    pre-hand-off submission must be cancelled silently — no `SpeechFailed`, no `IHearing`
    broadcast, no listener notification. An admitted submission is withdrawn this way only by unrelated fresh
    invalidation or node lifetime; the matching attended-source suppression never withdraws an admitted submission
    (TR-25, TR-26). Speech at or past playback hand-off is committed: freshness must not cut
    audible speech or retract the committed item and its self-observation. Ordinary non-tool callers retain
    admission-only semantics (SPCH-005 TR-25).
28. `SpeechTool` must resolve the raw `IVoice`
    from the context Character's authored component projection. It must not depend on an AgenticMind voice property,
    special case, or duplicate voice binding.
29. The configured output voice must remain excluded from external listening so dispatched self-speech is not recorded
    a second time as perceived speech.
30. Voice is a `SpeechTool` capability and must not be a generic session-runtime prerequisite.
63. Admission arbitration applies only when the character's resolved voice implements the SPCH-005 admission
    capability, and `SpeechTool` must resolve that capability through the authored voice projection (TR-28) without
    depending on or casting to a concrete voice class. When the voice does not implement it, `SpeechTool` must
    submit through the ordinary cancellable submission path (SPCH-005), and for that voice's speech only:
    cue-first admission refusal (TR-25, TR-56) and admitted-submission protection (TR-26, TR-40) do not apply;
    pre-hand-off withdrawal under any invalidation follows the ordinary silent-cancellation semantics of TR-27 —
    no `IHearing` event, no self-observation, the not-delivered tool result; and speech at or past playback
    hand-off remains committed and is never cut. This fallback is an intentional, specified suppression exception,
    not a degradation or error.

### The `wait` Tool

31. `wait`
    must accept an optional duration argument with a sensible default of 10 seconds (today's `MaxObservationWaitSeconds`
    default).
32. A `wait` call must return the observations its delivery window owns in FIFO ingestion order — the notable
    observations accumulated since the previous `wait`
    call, plus any sub-threshold predecessors a fresh observation upgraded — together with the elapsed wait duration
    and a current game timestamp (TR-37).
33. A wait in progress must finish early when AI-001's cumulative-importance machinery makes accumulated observations
    notable, when a fresh observation arrives regardless of the importance threshold (AI-001), and when an attended
    speaker finishes speaking (the attended-speaker-finished cue, AI-001). The same attention-snapshot membership
    rule as speak blocking (TR-25) decides which speakers wake the wait through the attended-speaker cue only;
    fresh-turn delivery is not attention-gated.
34. Observations whose accumulated importance stays below the configured threshold must not be pushed into wait
    results on their own. They remain in the timeline and are reachable through the `history`
    tool until a fresh observation upgrades the complete accumulation into the wait result (TR-32).
35. The `wait`
    tool description is the sole carrier of the tool's mechanics and etiquette. It must make clear that the tool's
    purpose is to observe the scene, not to pass time: waiting is how the agent receives scene updates promptly,
    since without invoking it updates arrive only at the session's next natural request. It must include wait
    etiquette — for example, after asking another character a question,
    wait a reasonable duration before assuming refusal and reacting. The session prompt carries no per-tool mechanics;
    its guidance is cross-cutting only.

### The Timeline History Tool

36. The timeline history tool — `history`, implemented by `HistoryTool` — must let the agent query the Mind's committed
    observation records under AI-001 without relying on provider message logs. It must be read-only, preserve timeline
    order, and render records through the AI-003 event-history contract (authored in the standalone
    `game/prompts/event_history.md` fragment file). Grouped speech reaches the result through the AI-003 continuation
    projection, so the tool's count covers projected events rather than raw segment records.

### Timestamps

37. All time-sensitive tool results must carry timestamps in one consistent format: seconds elapsed since the game
    began (in-game time; no timezones, no date-times). Observation `ObservedAt`
    stamps use the same format under AI-001.
38. A game-scoped game-time source (game clock) must exist as a Game-registered service contract — `IGameClock`
    under `AlleyCat.Core.Time`, exposing elapsed in-game seconds. For now in-game time advances with real time; no
    day/night cycle exists.

### Observation Delivery And Turn Invalidation

39. Ordinary notable observations must never cancel an in-flight model request, an active tool, or pending speech.
    When no active `wait` owns them, the runtime must coalesce every pending undelivered observation window in FIFO
    order into one injected user message — observation content rendered through the AI-003 event-history contract,
    with no prompt-stack dependency — appended to the transcript at the next natural model-request boundary the
    session reaches. The runtime must not issue a separate request solely to deliver ordinary observations.
40. A fresh observation (AI-001) must immediately invalidate the stale model response and every piece of work
    originating from it:
    - during model generation, cancel the in-flight request and discard partial assistant output; a response that
      arrives after its generation was invalidated must be discarded and never retained;
    - during a validated tool batch, cancel the active call co-operatively where possible, never invoke the
      remaining stale calls, retain every already-committed effect without rollback, and emit exactly one
      protocol-valid result for every assistant tool-call ID in the batch — synthesising canonical cancelled results
      for unstarted calls, and keeping a real result only where a non-cooperative call crossed its commit boundary;
    - append the complete assistant tool calls with one result per call ID so the provider protocol remains valid,
      then append the fresh observation and the pending accumulation as one injected message (AI-003 event-history
      rendering) and issue a single fresh request replaying the complete transcript.
    An admitted `speak` call — admission-arbitrated through its registered per-function phase policy (TR-62) — is
    excepted from suppression-driven cancellation when — and only when — the
    invalidation's speech key matches the attended-source hold its admission beat (TR-25, TR-26): the matching onset,
    resume, or completed-text invalidation must not cancel it, and it settles with its natural result. The exception
    is scoped to that matching key: unrelated fresh observations and node-lifetime cancellation retain ordinary
    cancellation, every assistant tool-call ID still receives exactly one natural or canonical result, and unadmitted
    speech withdraws under TR-27.
    Invalidation must persist across generation, response validation, and invalid-response recovery backoff until the
    fresh request is issued. For speech-group continuation, the completed segment's fresh observation follows this
    path with its replacement payload and timing governed by TR-56–TR-61: the payload is the AI-003 continuation
    projection of the complete utterance, and the request waits for the matching expectation to settle.
41. When AI-001 commits observations, it must signal the session runtime with delivery urgency — ordinary versus
    fresh — and whether an active `wait` owns the delivery. The runtime applies TR-39 or TR-40 as applicable. A fresh
    observation arriving during an active `wait` must fulfil that wait through its normal completion mechanism —
    returning the complete pending accumulation plus the fresh observation in FIFO order — and must not duplicate
    that delivery as an injected user message; freshness still invalidates the surrounding stale batch and skips its
    remaining calls. A fresh wake must never surface generic action-interrupted wording. Expected invalidation must
    not be reported as a backend failure and must not trigger transport retry.

### Speech Start And Continuation Invalidation

56. Fresh-invalidation expectations must be opaque, single-use, and keyed by the cueing speech identity — the source
    voice plus its speech-group and segment identity for automatic cues, or the source voice plus the opaque internal
    synthetic token for the manual start cue (SPCH-005, SPCH-008). A transient speech-start (`Started`) or
    speech-resume signal that AI-001 attention-gated (AI-001 TR-47) registers one expectation, advances the session's
    invalidation epoch, and immediately cancels the active phase: in-flight model generation, transport retry and
    backoff, invalid-response recovery backoff, active `wait` calls, and cancellable pre-commit tool calls — except
    a tool call protected by successful admission under its registered per-function phase policy (TR-62): today the
    `speak` submission of a capability voice (TR-25, TR-26, TR-63). Start/resume cancellation must not consume
    the transport-retry or invalid-response budgets (TR-43). Cancellation is best-effort: accepted responses and
    committed tool effects remain causal history. Cue registration linearises under the agent-runner state lock, and
    the normative lock order for submission-versus-cue arbitration is voice-submission lock first, then runner state
    lock: when the cue linearises first, a racing speak submission is refused admission — no TTS request, queue item,
    hearing event, or self-observation; when admission completes first, the submission is protected and the cue must
    not cancel it. Cues AI-001 did not forward — unattended, self, unattributable, or ambiguous sources — register
    nothing and cancel nothing, and duplicate cues for a key an expectation already covers are idempotent: no second
    expectation, no second cancellation.
57. While a registered expectation's settled text is unknown — continued or initial segment text — the runtime must
    not issue the replacement request for the invalidated turn. It settles as completed only when matching rendered
    observation text has been queued for the model — injected or returned through a `wait` — and as abandoned only on
    an exactly once, identity-matched `Blank`, `Failed`, or `Abandoned` terminal settlement from SPCH-005. The match
    is the source voice plus the immutable speech-group and segment metadata, or the synthetic token for a manual
    start hold, whose completed publication settles it through ordinary delivery while remaining publicly ungrouped.
    `Published` must not create a duplicate transient release: its completed text settles the expectation through
    ordinary delivery. Abandonment injects no fabricated text and must not deadlock the runner: it clears the hold so
    the session continues through ordinary paths, and a settlement matching no registered expectation abandons
    nothing. A replacement request is issued only for a turn the cue actually interrupted; a runner that was idle
    simply resumes waiting, with no fabricated replacement turn. Multiple expectations may coexist and settle
    independently, and one coalesced rendered payload (AI-003 continuation projection) may release several
    expectations when it contains each matching projected utterance. When a manual press pre-empts an open automatic
    group (SPCH-005, SPCH-008), the automatic group's settlement releases the automatic hold before the manual start
    cue's hold begins. Session teardown remains terminal and issues no replacement or lifecycle work (TR-44).
58. Before an assistant response is accepted, the user turn is mutable and structurally keyed by the projected
    speech-group identity — source voice plus `SpeechGroupID`, or the source voice plus the synthetic token for a
    manual start hold. A newer projection for the same key replaces any earlier projected injection: the runtime
    issues exactly one replacement request containing the complete
    joined utterance (AI-003 continuation projection) and never duplicate partial user messages. After acceptance,
    the transcript is append-only (TR-59, TR-60).
59. An invalidation-epoch check immediately before appending the assistant response makes cue-versus-acceptance a
    single deterministic winner: if the epoch advanced — a start or continuation expectation registered — the
    response is discarded and the user turn stays mutable under TR-58; otherwise acceptance proceeds and the
    transcript becomes append-only from that point. The check and the append form one atomic arbitration.
60. After response acceptance — assistant response appended, tool calls and results committed, or `wait` results
    returned — the transcript must never be rewritten. When later segments of the same speech group settle, the
    runtime must append one explicit reconciliation input stating that the speaker continued, that their complete
    utterance is the joined text (AI-003 continuation projection), and that earlier responses or actions may already
    have occurred. Accepted content and committed effects are retained.
61. The causal boundary at a suppression cue — attended start or resume — is normative:

    | Phase At Cue | Behaviour |
    | --- | --- |
    | During model generation | Cancel and discard; hold, then replace the turn (TR-57, TR-58). |
    | During response validation | The epoch arbitrates; the loser is discarded (TR-59). |
    | After the assistant append | Never rewrite; reconcile once settled (TR-60). |
    | During a tool batch | TR-40 cancellation rules with the admitted-speak exception; then reconcile (TR-60). |
    | After a tool batch | Retain every result; reconcile (TR-60). |
    | While a `wait` is active | Cancel the wait; restore its claim; deliver exactly once. |
    | `wait` result already returned | Preserve it; reconcile later (TR-60). |
    | Node-lifetime end | Terminal; no replacement or reconciliation (TR-44). |

    The tool-batch row applies TR-40's protocol-safe cancellation — exactly one canonical cancellation
    result for every unstarted call and natural results for calls that crossed their commit boundary — with the
    TR-26 admitted-speak exception retaining a matching protected call's natural result, followed by
    TR-60 reconciliation once the cueing speech settles. The wait rows restore any unreturned delivery claim so the
    projected group is later delivered exactly once, with no duplicate injection.

### Runner Generic-Phase Boundary

62. The session runner must operate only on generic phase concepts — model request, transport retry and backoff,
    invalid-response recovery, tool execution, admission-arbitrated tool execution, correlation keys, invalidation
    epoch, and lifetime precedence — with per-function phase policy registered when session tools are composed. It
    must not reference, match, or name any concrete production tool, function name, or tool type: whether a function
    executes under admission arbitration is decided by its composition-time policy, never by the runner inspecting
    function names. Feature-specific correlation stays outside the runner: for speech, the session-scoped
    speech-turn/continuation coordinator at the AgenticMind composition boundary owns keyed expectations, settlement,
    watchdog, and the translation from projected speech identity into these generic operations. The TR-56–TR-61
    settlement, epoch, and reconciliation semantics are unchanged.

### Failure And Cancellation

42. Tool errors must be reported through the tool result so the agent decides whether, when, and how to retry.
43. Transport-level failures — network errors, rate limits such as 429, and timeouts — must be handled transparently
    by a bounded transport-retry policy. This policy is separate from invalid-response recovery and applies only to
    transport-level failures; transport failures must not be surfaced to the agent as tool results or transcript
    entries. Transport-retry exhaustion must end the session through the contained failure path: logged and contained
    without crashing the scene, with no model repair attempt and no automatic session restart.

    Invalid-response recovery must use its own configurable bounded consecutive-invalid-response budget and backoff,
    independent of the transport-retry policy. On each invalid response, the runtime must discard the complete
    response, issue a fresh request that replays only the last valid transcript, and produce no assistant transcript
    entry, tool invocation, tool result, observation, or other in-world effect from that response. The consecutive
    invalid-response failure streak resets only after an entire response batch validates. Only exhaustion of this
    budget may end the session through the contained failure path. Node-lifetime cancellation and fresh-turn
    invalidation take precedence over this recovery: they must not consume its budget or cause an additional
    recovery request, and TR-40's replacement-request semantics remain unchanged.
44. Node-lifetime cancellation from AI-001 must propagate through active requests and tool work and is terminal: it
    takes precedence over fresh-turn invalidation and must settle without synthetic tool results, a replacement
    request, or any other follow-up session activity. Expected interruption and lifetime cancellation must not
    trigger retry, further unintended session activity, or misleading failure diagnostics.
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

- One long-running agent session per AgenticMind: append-only-after-acceptance transcript, bounded stateless
  requests, and no session restart mechanism.
- Once-per-session prompt compilation and rendering with on-demand render-context assembly and one session-captured
  scene snapshot.
- Tool-only validation without completion markers or request and action bounds; full-transcript replay on every request.
- The `speak`, `wait`, and timeline history (`history`) tool inventory, including the standard `AgentToolResult`
  contract.
- `speak`
  blocking turn-taking, admission-versus-cue arbitration with refused-admission reporting, pre-hand-off withdrawal
  on fresh-turn invalidation, playback hand-off as the success boundary, and exactly-once observed-speech
  production.
- `wait`
  notable-observation delivery, early finish on importance, fresh observations, and attended-speech end, and
  observe-not-sleep guidance.
- Read-only timeline recall through the `history` tool.
- The game-time convention for all time-sensitive tool results and the game-scoped game clock.
- Ordinary boundary injection and fresh-turn invalidation for model generation and complete tool batches, including
  injected-message resumption and one protocol-valid result per stale call ID.
- Speech-start and speech-continuation invalidation: attention-gated opaque single-use keyed expectations — automatic
  segment identity or the manual synthetic token — with immediate cancellation, voice-pipeline admission arbitration
  and admitted-speak protection, the hold until settled text, pre-acceptance mutable user-turn replacement, the
  atomic response-acceptance epoch boundary, post-acceptance reconciliation inputs, and wait delivery-claim
  restoration.
- Tool errors as tool results, separate bounded transport retry and invalid-response recovery, and contained
  session-ending failure after the applicable budget is exhausted.
- Trusted typed `ScenarioContext`
  binding, ownership verification, shared-dispatcher tool start, actor stamping, and atomic Mind hand-off.
- Typed per-tool capability binding at composition — speech-admission arbitration to the `speak` tool and
  wait-delivery acknowledgement to the `wait` tool — with no feature services on the common tool session.
- Runner generic-phase operation with composition-registered per-function phase policy and no concrete tool
  knowledge; session-scoped speech-turn/continuation correlation at the AgenticMind composition boundary.
- The specified ordinary-voice fallback: no admission arbitration when the resolved voice lacks the SPCH-005
  admission capability, with ordinary cancellable submission semantics instead.
- Responses-default stateless transport and explicitly selected Chat Completions rollback.
- Adoption of `Microsoft.Agents.AI` within the stated deviation boundary.
- Development-only MEAI diagnostics and non-secret structural transport evidence with explicit gating.
- Speech-pipeline latency diagnostics through the shared pipeline diagnostic log: the speak-boundary marker before the
  turn-taking wait and log-only session-end latency (CORE-007 is normative for routing).
- Process-wide agent-session suppression through the `--no-ai` user argument: lazy once-per-process resolution, the
  `StartSession()` gate before session preparation, and the single Information notice.

## Out Of Scope

- Context exhaustion handling and transcript compaction; explicitly deferred — short testing sessions only for now.
- Session restart, re-anchoring, or mid-session re-prompting.
- Additional production tools beyond `speak`, `wait`, and the timeline history tool.
- Provider-directed model repair or feedback for invalid output. Invalid-response recovery instead replays the last
  valid transcript through a fresh request.
- Cancelling or reversing world actions already admitted; fresh-turn invalidation performs no rollback of committed
  effects.
- Rewriting, editing, or reverting accepted transcript history or committed effects; continuation after acceptance
  appends a reconciliation input only.
- Gameplay policy for interrupting already-audible speech; playback hand-off commits speech.
- Speaker priority, addressee, audibility, and conversational-target metadata for classifying observed speech;
  attention membership and name-text heuristics must not substitute for it.
- Speech playback-finished success semantics.
- Timeline summarisation, compaction, token budgeting, persistence, and provider transcript retention beyond the
  session.
- A day/night cycle or non-real-time game clock advancement.
- Voice as a requirement for generic non-speech session execution.
- Multi-agent orchestration and guidance-agent APIs.
- Complete or production HTTP wire-body logging.
- Runtime toggling or per-Mind enable/disable of the suppression switch; suppression is process-wide only.
- Test-framework changes to inject or reject the flag (TEST-001); the internal test seam covers targeted testing.
- Suppressing local speech synthesis (Supertonic) or scenario rendering; only agent sessions are gated.
- Changing `Mind.Enabled` semantics; suppressed minds remain fully enabled nodes.

## Acceptance Criteria

### User Requirements

1. Session-continuity coverage verifies an NPC sustains exactly one session from activation to scene removal or
   unrecoverable failure, with no session restart or re-anchoring, and later behaviour reflects earlier observations,
   speech, and actions of the same session.
2. Wait-delivery coverage verifies notable observations accumulated since the previous wait are returned together with
   the elapsed duration and a game timestamp, that important arrivals, fresh observations below the importance
   threshold, and an attended speaker finishing speech finish the wait early, and that quiet expiry returns no
   sub-threshold observations.
3. Acceptance verifies an NPC that has not invoked `wait`
   still receives ordinary notable updates through one coalesced injected message at the next natural request
   boundary, and that the `wait`
   tool description frames waiting as observation rather than passing time, including question-then-wait etiquette.
4. Turn-taking coverage verifies an NPC does not begin speech while an attended speaker's window is open, that speech
   withdrawn before playback hand-off by fresh-turn invalidation — or refused because an attended onset or resume won
   the admission race — is reported through the speak result rather than an error — allowing the NPC to react — and
   that audible speech is never cut by observation freshness. It also verifies both admission-race outcomes: speech
   admitted before the cue completes naturally and is remembered, and speech refused at cue-first admission produces
   no audible utterance.
5. Speech and action coverage verifies character-owned in-world voice, exactly-once own observed speech, no false
   memory of failed or cancelled actions, and voice availability constraining speech only.
6. Failure coverage verifies tool errors surface as tool results for the NPC to act on, while transport failures are
    invisible to the NPC until bounded transport-retry exhaustion ends the session through containment without
    crashing the scene. It also verifies that a transient invalid provider response is discarded and the NPC
    conversation continues after recovery, while exhaustion of the separate invalid-response budget is contained.
7. Timestamp coverage verifies all time-sensitive tool results report seconds of in-game time elapsed since the game
   began, with no timezones or date-times.
8. Acceptance verifies containment and safety: missing configuration, backend failure, transport-retry or
   invalid-response-budget exhaustion, cancellation, and node exit never crash the scene or produce delayed in-world
   effects, and an ownership mismatch produces no world effect.
9. Diagnostics coverage verifies speech-pipeline latency diagnostics remain opt-in through the `AlleyCat.Pipeline`
   category's log level (CORE-007) and change no NPC behaviour.
10. Fresh-turn coverage verifies newly observed non-self speech — recognised or unknown speaker, attended or not —
    immediately replaces the NPC's stale reasoning, even below the configured importance threshold, and that
    ordinary important observations never cancel active reasoning, tools, or pending and committed speech.
11. Acceptance verifies launching with `-- --no-ai` produces NPCs that still perceive and attend, exactly one
    Information notice naming the switch, and provably zero provider requests.
12. Continuation coverage verifies an NPC never replies to a half-finished utterance: an attended speaker's onset or
    resume stops stale reasoning before the continued text exists, the next request carries the complete utterance as
    one message with no duplicate partial user messages, reaction after an endpoint pause remains prompt, and
    already-committed reactions stay visible and are explained to the NPC through reconciliation. A non-attended
    onset or resume causes no hold, and the speaker's completed speech still arrives as a fresh turn.
13. Acceptance verifies the ordinary-voice fallback: an NPC whose voice lacks the admission capability has
    non-audible speech withdrawn silently with a not-delivered speak result under fresh-turn or attended-onset
    invalidation, audible speech stays committed, and capable voices retain refused-admission reporting and
    admitted-speech protection unchanged.

### Technical Requirements

1. Session-lifecycle tests verify one session per AgenticMind, started after `_Ready()`
   once perceptions are subscribed, ended on node exit or fatal unrecoverable failure, with no session restart,
   re-anchoring, or re-prompting route.
2. Transport tests verify OpenAI Responses is the default and every request sets `store: false`, omits
   `previous_response_id`, and replays the complete ordered transcript; Chat Completions is available only through
   explicit selection, is never an automatic fallback, and preserves the tool-only semantics.
3. Protocol tests verify no `end_turn` marker, no `MaxModelRequests` or `MaxToolActions`
   bounds, and no provider response format exist, and that every request requires at least one tool call.
4. Validation tests reject empty or malformed responses, ordinary assistant text, unknown content and tools, invalid
    arguments, and duplicate call identifiers before executing any tool in an invalid batch, tolerate model reasoning
    content, and verify that invalid output produces no assistant transcript entry, tool result, observation, or
    in-world effect before its fresh recovery request, with no model repair or invalid-output feedback.
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
    exclusion, silent pre-hand-off withdrawal under fresh-turn invalidation, the non-throwing not-delivered result,
    the playback hand-off success boundary with no retraction and no freshness-driven cutting of audible speech,
    exactly one actor-stamped self-relative `ObservedSpeech`, self-listener exclusion, and resolution of the
    Character-authored `IVoice` through the typed context. They verify both admission-arbitration winners under the
    normative lock order: cue-first refusal issues no TTS request, queue item, hearing event, or self-observation and
    returns the not-delivered result, while admission-first submissions stay protected through TTS, playback
    hand-off, self-observation, and a natural tool result.
11. Wait tests verify the default duration of 10 seconds, delivery of the notable window accumulated since the
    previous wait, early finish on the cumulative-importance threshold, on fresh observations below the threshold,
    and on the attended-speaker-finished cue, the elapsed-duration and game-timestamp result fields, FIFO delivery
    of the complete accumulation including sub-threshold predecessors on a fresh wake, delivery through the wait
    result with no duplicate injected message, and that sub-threshold observations otherwise never enter wait
    results while remaining reachable through the `history` tool.
12. History tests verify the `history` tool is read-only, preserves timeline order, and answers from the Mind timeline
    rather than provider message logs.
13. Game-clock tests verify a game-scoped game-time source (`IGameClock`) exists, is resolvable from the Game service
    provider, advances with real time, and backs every time-sensitive tool-result timestamp and `ObservedAt` stamp.
14. Delivery-and-invalidation tests verify ordinary notable observations never cancel generation, tools, or speech
    and arrive as one coalesced injected message at the next natural request boundary without a separate request;
    fresh observations cancel in-flight generation, including a provider response arriving after cancellation;
    a stale tool batch is completed with exactly one protocol-valid result for every assistant call ID —
    co-operative cancellation of the active call, canonical cancelled results for unstarted calls, and a retained
    real result only where a non-cooperative call crossed its commit boundary, without rollback; the complete
    assistant exchange precedes the fresh injected message; and expected invalidation produces no backend-failure
    diagnostics or retry. They verify a matching attended-source onset, resume, or completed-text invalidation never
    cancels an admitted speak — it settles naturally with its natural result — while an unrelated fresh observation
    still withdraws an unhand-offed admitted submission under TR-27, and every assistant call ID retains exactly one
    natural or canonical result.
15. Failure tests verify tool errors are returned through tool results; transport failures use only the bounded
    transport-retry policy and are never surfaced to the agent; and invalid responses use only a separate bounded
    consecutive-invalid-response recovery policy. They verify a fresh request replays the last valid transcript after
    each invalid response, the invalid-response streak resets only after complete response validation, and contained
    failure occurs only when the applicable budget is exhausted. They also verify cancellation and fresh-turn
    invalidation neither consume the invalid-response budget nor issue an additional recovery request.
16. Diagnostics tests verify the dual `LoggingChatClient`
    request/response gate with deferred serialisation and either-control suppression, decoration before session
    execution, unchanged behaviour with diagnostics enabled or disabled, isolation from STT and TTS traffic and shared
    `System.ClientModel`
    body logging, gated non-secret structural evidence, and the separate reasoning-logging gate with its off-switch
    default.
17. Node-exit tests verify active and queued work settles without delayed dispatch, successful observation, retry,
    exited-node service access, or erroneous expected-cancellation diagnostics, and that lifetime cancellation takes
    precedence over fresh-turn invalidation, issuing no synthetic tool result or replacement request.
18. Tests verify the runtime builds on `Microsoft.Agents.AI`
    where it fits and retains custom tool-only validation and wait wake semantics where the framework does not provide
    them, with no legacy generic terminal-result route selectable.
19. Diagnostics tests verify the speak-boundary pipeline marker fires after final speech acceptance and before the
    turn-taking wait, changes no tool behaviour, and that session-end latency remains log-only.
20. Suppression tests verify the parser truth table and the `--no-ai` literal at unit level, and — through the test
    seam — that a fully-wired suppressed mind never calls `CreateChatClient` or issues requests, that the
    once-per-process notice guard holds across multiple suppressed minds, that timeline ingestion is unaffected, and
    that the gate-open control path starts sessions normally.
21. Continuation tests verify expectations are opaque and single-use; that attended start and resume cues advance
    the invalidation epoch and cancel generation, transport retry and invalid-response backoff, active waits, and
    cancellable pre-commit tools — except protected admitted speak — without consuming either recovery budget; and
    that accepted responses and committed effects remain causal history. They verify the attention gating is sampled
    once at cue receipt and source-generic: non-attended, self, unattributable, and ambiguous cues register nothing
    and cancel nothing, later attention changes neither release nor retroactively create a hold, and duplicate cues
    are idempotent.
22. Hold tests verify no replacement request for the invalidated turn escapes while settled text is unknown; that
    an expectation completes when the matching rendered observation text is queued — including a manual start hold
    settled by its source voice's ungrouped completed publication; that blank or failed
    transcription, pre-emption, and teardown abandon it without fabricated text, placeholder input, or deadlock;
    that `Blank`, `Failed`, and `Abandoned` settle only the exact matching keyed hold exactly once, that a settlement
    matching no hold is a no-op, and that only an actually-interrupted turn is replayed while an idle runner resumes
    waiting; that `Published` creates no duplicate release; that automatic pre-emption releases the automatic hold
    before the manual hold begins; and that coexisting expectations settle independently while one coalesced
    payload can release several.
23. Turn tests verify a newer segment projection replaces the earlier projected injection for the same key before
    acceptance as exactly one request containing the joined utterance with no duplicate partial user messages, that
    the pre-append epoch check makes resume-versus-acceptance a single deterministic winner, and that acceptance
    ends user-turn mutability.
24. Boundary tests verify every causal-boundary row of TR-61 at start and resume cues alike — generation
    cancel-and-replace, validation arbitration, post-append reconciliation with the exact continued-utterance wording,
    tool cancellation with one canonical result per unstarted call, natural results for crossed-commit calls, and the
    admitted-speak exception retaining a protected call's natural result, wait cancellation with claim restoration
    and exactly-once later delivery of the projected group, preservation of returned wait results, and terminal
    lifetime end without replacement or reconciliation.
25. Architecture and behaviour tests verify the common tool session exposes no feature services:
    speech-admission arbitration binds typed to the `speak` tool and wait-delivery acknowledgement binds typed to
    the `wait` tool at composition, with no nullable capability bag, service locator, or keyed capability
    dictionary on the shared session and no other tool observing either capability.
26. Architecture tests verify the runner references no concrete production tool class, function name, or tool type:
    admission-phase registration arrives through per-function policy registered at tool composition, and
    speech-turn/continuation correlation lives in the session-scoped coordinator at the AgenticMind composition
    boundary rather than in the runner.
27. Fallback tests verify a voice without the admission capability: `SpeechTool` submits through the ordinary
    cancellable path without casting to a concrete voice class, cues and fresh turns withdraw a pre-hand-off
    submission silently with no `IHearing` event or self-observation and a not-delivered result, post-hand-off
    speech remains committed, and capable-voice arbitration, protection, and refusal reporting are unchanged.

## References

### Implementation

- `game/src/Mind/AI/AgenticMind.cs`
- `game/src/Mind/AI/AgentSessionSuppression.cs`
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
- [SPCH-008: Automatic Voice Detection](../../speech/008-automatic-voice-detection/index.md)
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
