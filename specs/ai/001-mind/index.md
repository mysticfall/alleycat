---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

The system must provide a Mind component that records an NPC's subjective observations in order and schedules
stateless provider turns through one contextual-importance pipeline.

## Goal

Give NPCs coherent node-lifetime experience without treating transient provider protocol as memory, while preserving
responsive and interruption-safe in-world behaviour.

## User Requirements

1. An NPC must remember, for its node lifetime, the ordered observations it perceived or produced through successful
   actions.
2. Every observation must influence scheduling according to its importance; even an observation with zero importance
   must receive bounded processing.
3. An important new observation may interrupt an active response when interruption is enabled, without overlapping
   responses or discarding actions and observations that were already committed.
4. Several important arrivals during one active response must produce at most one immediate replacement response.
5. Each response must reflect the NPC's complete observation history, current authored character context, and worker
   projections captured for that foreground prompt.
6. Speech history must attribute a speaker by matching the received voice ID to current-scene characters. It must
   distinguish the NPC, a recognised other character, and an unknown speaker without rendering the voice ID as identity
   wording. No match must remain unknown, while ambiguous matches must fail clearly.
7. An NPC may take no action, one action, or several actions before ending a turn; speaking must not end the turn.
8. Spoken responses must use the NPC's character-owned in-world voice rather than normal chat text.
9. Missing configuration and backend failures must be contained and logged without crashing the scene.
10. Removing an NPC's Mind from the scene must prevent delayed actions, replacement responses, and other
    post-destruction effects from that Mind.
11. A turn must end through protocol control rather than ordinary assistant text, and invalid output must not trigger a
    model repair attempt or automatic retry.
12. An NPC's Mind must synchronously interpret sense-owned percepts into attention and zero or more ordered durable
    observations without delaying normal gameplay.
13. Foreground character context must contain self and every currently resolvable contextual subject whose attention
    meets the context threshold, rather than every scene character unconditionally.
14. NPC speech uses the voice authored on the owning Character; contributors do not author a second output voice on
    Mind.
15. An NPC must not begin a response while a speaker it attends to, or the NPC itself, is speaking; a response that
    became due during that speaker's speaking window starts as soon as the window closes and scheduling permits.
16. When an attended speaker begins speaking during the NPC's active response, the NPC's audible speech is cut off
    immediately and exactly one fresh response replaces the interrupted one, reflecting the new speech.
17. Speech from speakers the NPC does not currently attend to, and speech that cannot be attributed to a character,
    must neither delay nor interrupt the NPC's responses.

## Technical Requirements

1. Each Mind must own a private, synchronised, ordered timeline of subjective `Observation` records. The timeline lasts
   for the Mind node's lifetime and is the authoritative cross-turn memory.
2. `Observation` must calculate importance through `CalculateImportance(ObservationContext)`. `ObservationContext` must
   initially contain the owning `ICharacter` and remain extensible for future contextual scoring.
3. Mind must calculate and validate importance exactly once at ingestion, before mutation, and store the calculated
   value with the pending entry. Negative, non-finite, or otherwise invalid importance must reject the entire ingestion.
4. Every successfully ingested observation must enter both the timeline and the pending scheduling queue. There must be
   no public recorder or sink contract and no timeline-only ingestion path.
5. Pending observations must retain FIFO order. Disabling Mind must stop scheduling while preserving pending entries for
   processing after re-enable.
6. Scheduling must use configurable cumulative importance threshold, maximum observation wait, and minimum interval
   after the previous turn completes:
   - cumulative importance at or above the threshold becomes eligible when the minimum interval permits;
   - maximum-wait expiry makes every pending entry eligible, including entries with zero importance; and
   - eligibility reached during an active turn remains pending until that turn settles.
7. `MinimumTurnIntervalSeconds` must have a Godot editor range of 0–5 seconds. Runtime handling must remain
   non-negative.
8. Mind must support a configurable, disabled-by-default high-importance interruption policy. One newly ingested
   observation interrupts only when its individual stored importance meets the configured threshold; cumulative pending
   importance alone must not interrupt.
9. An interruption must request expected cancellation of the active invocation. The invocation and all tool work must
   settle before exactly one fresh replacement starts; turns must never overlap.
10. The replacement must bypass the minimum interval exactly once. Multiple qualifying arrivals before settlement must
    coalesce into one replacement, while pending entries retain FIFO order.
11. Committed actions and observations must remain in the timeline after interruption. Disabling Mind or ending its node
    lifetime must suppress replacement safely, including during natural-completion and cancellation races.
12. Scheduling must not exempt observations by source. Owning-character actions avoid interruption through contextual
    importance rather than a separate ingestion or scheduling policy.
13. `ObservedAction` must be the actor-aware observation base and retain an exact stable actor ID rather than a
    scene-node reference.
14. `ObservedSpeech : ObservedAction` must represent owning-character, recognised-other, and unknown speech through the
    exact case-sensitive `speech.observed` key. It must retain content and nullable raw `VoiceId` separately from
    `ActorId` identity.
15. `VoiceId` must never appear in rendered identity wording or be treated as authenticated provenance. Recognition must
    be relative to the observing Mind; unknown speech must remain representable without inventing an actor identity.
16. Owning-character speech must calculate importance `0`. Recognised-external and unknown speech must retain effective
    importance `1`, while final broader importance models remain tunable and deferred.
17. Mind must stamp tool-produced `ObservedAction` actor IDs with the owning character's exact `FullId` before
    calculating importance. A tool-supplied actor ID must not spoof another character.
18. Mind must atomically ingest each ordered observation batch produced by a tool result. Validation failure must append
    none of the batch to either timeline or pending state.
19. Mind must expose read-only, atomic timeline snapshots while keeping mutable timeline storage private. Observation
    records in a published snapshot are treated as immutable. Each turn must render the complete, unbounded snapshot;
    transient provider request history must not provide cross-turn memory.
20. Observations received before `_Ready()` must become schedulable when scheduling is available.
21. AgenticMind must execute each turn through [AI-002](../002-agent-runtime/index.md) and render it through
    [AI-003](../003-prompt-api/index.md).
22. Tree exit must establish one irreversible node-lifetime boundary that stops intake and scheduling, stops timers, and
    cancels active observation processing. Deferred callbacks must not access Mind services after exit.
23. Node-lifetime cancellation must propagate through active agent and tool work. Expected interruption and lifetime
    cancellation must not be reported as backend failures or trigger retries or unintended turns.
24. Mind's `SpeechPerception` faculty must resolve attribution only against `ICharacter` instances in the current scene.
    It must compare the percept's raw source voice `Id` ordinally with each character's composed `IVoice.Id`. Voice
    object-reference identity, lore prose, character names, and aliases must not participate in matching.
25. During character attribution, blank received IDs and blank configured character voice IDs must not match. Zero
    matches must leave `ActorId` null;
    exactly one match must set `ActorId` to that character's exact case-sensitive `Character.FullId`; multiple matches
    must fail clearly without selecting an actor.
26. Voice-ID matching is configured, operational attribution rather than authenticated provenance. A source that
    presents another character's voice ID can therefore be attributed to that character; same-ID spoofing is an accepted
    limitation of this model.
27. AI-002's action calls and synthetic `end_turn` marker must remain transient turn protocol. `end_turn` must never
    become an action result or timeline observation, while successfully committed action observations must use the
    ordinary Mind ingestion path.
28. A failed tool-only turn must settle through the existing containment path without model repair or automatic retry.
    Any observations committed by earlier successful actions in that turn must remain in timeline order.
29. AgenticMind must own configured ContextWorker child nodes as specified by [AI-005](../005-context-worker/index.md)
    and may retain them solely for deterministic projection aggregation during foreground `CreateRenderContext` calls.
30. AgenticMind must initialise its latest render dictionary to an empty top-level read-only dictionary. Only foreground
    prompt execution may call `CreateRenderContext` to create AgenticMind's own top-level read-only render dictionary
    from current character context, deterministic attention-eligible subject context, the complete timeline snapshot,
    and authored worker projections. AI-006 normatively defines attention eligibility and scene resolution.
31. The foreground template must use the exact dictionary returned by `CreateRenderContext`. AgenticMind must atomically
    publish that exact dictionary as the cached latest foreground context only after rendering succeeds. Construction or
    rendering failure must retain the previous published dictionary.
32. AgenticMind must publish general typed C# events after committed observations and genuinely successful foreground
    turns. Relevant trigger nodes subscribe and unsubscribe directly. AgenticMind must not loop through ContextWorkers
    for trigger notification, and contained failures and cancellations must not publish a successful-turn event.
33. ContextWorker event handling and activity must remain independent of foreground scheduling and within the Mind
    node-lifetime boundary. Workers may only capture the published render snapshot; they must not initiate context
    construction, aggregation, or timeline refresh.
34. Mind must subscribe to configured `ISense` components and own authorable Resource faculties, exact percept-type
    registration, synchronous interpretation, attention, result validation, and observation ingestion. AI-006 is the
    normative percept, sense, faculty, attention, and result contract.
35. Before activation, Mind must require exactly one exact faculty mapping for every exact percept type declared by its
    configured senses. Missing, duplicate, incompatible, or undeclared mappings must fail clearly.
36. Mind must validate a complete `PerceptionResult`, including every calculated observation importance, before any
    mutation. It then applies ordered attention effects sequentially and atomically ingests ordered observations through
    the existing timeline, pending FIFO, scheduling, and interruption path. This sensing and perception path must not
    select or assign an IVision look target.
37. AgenticMind must own only provider, prompt, render-context, and tool concerns. Incoming sensory interpretation
    remains synchronous through Mind's `IPerception` faculties. Outbound production-tool invocation must start once
    through `AgentTool` and the shared `IMainThreadDispatcher`; cancellation remains linked to turn and Mind lifetime.
    The Game-scoped dispatcher owns accepted-work queueing and settlement, and AgenticMind must not retain local
    deferred voice or Godot-action machinery. The actor-stamped self-action speech observation commits exactly once at
    playback hand-off (SPCH-005 TR-26), not at admission, through ordinary Mind ingestion.
38. Mind must not own or export an output-voice reference. Character-owned capabilities required by tools must enter
    through AI-002's typed `AgentToolContext`; Character remains the sole authored voice source under CHAR-002.
39. AI-007 separately defines the direct Mind-child post-attention consumer that may assign a look target. It consumes
    Mind's published attention snapshot after perception has completed; Mind's sensing and attention-mutation contracts
    remain gaze-neutral.
40. Mind must stamp each committed observation exactly once with a UTC `ObservedAt` timestamp (`DateTimeOffset?`,
    init-only) at ingestion, before the record enters the timeline or pending queue. The identical stamped record must
    be used for the timeline, pending FIFO, and ingestion notification. Observations are otherwise unchanged and remain
    immutable after publication.
41. Speaking-activity turn-start gate: Mind must not start a new turn while any gating voice reports `IsSpeaking`
    (SPCH-005 TR-2). A voice gates iff its owning character's canonical `FullId` is present in Mind's current
    attention snapshot at or above the retention threshold (AI-006 TR-33), regardless of weight or score. The Mind's
    own character voice gates unconditionally. Voices whose speaker cannot be attributed to a current-scene character
    (unknown or blank voice `Id`s) must not gate; this is an accepted limitation of the attribution model. Gate
    membership is deliberately decoupled from the context threshold that governs foreground context eligibility
    (AI-006 TR-33).
42. Block-with-wake: when a blocking `SpeechEnded` fires (SPCH-005 TR-2), Mind must immediately re-run its scheduling
    evaluation rather than poll. A turn whose eligibility was reached while the gate was closed — cumulative
    importance at or above the threshold, or `MaxObservationWait` expiry — must start right away, still respecting
    `MinimumTurnIntervalSeconds`. Eligibility reached while the gate is closed remains pending until the gate opens,
    mirroring eligibility reached during an active turn (TR-6).
43. Speech-start interruption: when a gating voice raises `SpeechStarted` (SPCH-005 TR-2) while a turn is in flight,
    Mind must interrupt through the existing interruption machinery (TR-8–TR-11): request expected cancellation of the
    active invocation, settle the invocation and all tool work, then start exactly one fresh replacement bypassing the
    minimum interval exactly once; simultaneous arrivals coalesce; turns never overlap. Unlike the high-importance
    observation trigger (TR-8, disabled by default), this trigger is voice-activity based and enabled by default. The
    replacement must not start while the gate is closed; it waits behind the gate like any pending turn, ensuring the
    fresh turn's context includes the new speech.
44. Interruption must cut in-flight speech output. The interrupted turn's explicitly cancellable speech submission
    (SPCH-005 TR-25) must be cancelled silently before playback hand-off — no `SpeechFailed`, no `IHearing` broadcast,
    no listener notification, and the speaking window closes. Speech already at or past playback hand-off must be cut:
    audio and lip-sync stop through the shared `LipSyncPlayer` stop/cut capability (SPCH-001/SPCH-002). SpeechTool must
    pass the turn's cancellation token through the cancellable submission path; ordinary non-turn callers retain
    admission-only semantics (SPCH-005 TR-25).
45. Own-voice exclusion: the speaking window opened by a turn's own speech submission must not interrupt that turn.
    Mind must ignore `SpeechStarted` from its own character voice while that turn is in flight. Own-voice activity
    still gates the start of other turns (TR-41).

## In Scope

- Mind-owned node-lifetime observation timeline and pending importance queue.
- Contextual importance calculation, validation, and stored scheduling values.
- Unified external and tool-result observation ingestion.
- Threshold, maximum-wait, minimum-interval, disable, and active-turn interruption behaviour.
- Actor-relative observed speech, current-scene voice-ID attribution, and separately stored voice IDs.
- Synchronous percept interpretation, exact faculty dispatch, and Mind-owned attention under AI-006.
- Published attention snapshots for the separately composed, post-attention AI-007 gaze selector; direct gaze assignment
  remains outside Mind sensing and perception processing.
- Attention-filtered foreground contextual-subject selection.
- AgenticMind orchestration through AI-002 and AI-003.
- Typed tool-context hand-off of Character-owned capabilities without Mind-owned voice authoring.
- AgenticMind ownership and lifetime integration of AI-005 ContextWorker child nodes.
- Foreground-only render-context construction and success-only publication of the latest top-level read-only dictionary.
- Tool-only turn settlement without assistant-text completion or synthetic-marker observations.
- Irreversible node-lifetime shutdown of scheduling, provider requests, tools, and dispatcher-queued action work.
- Speaking-activity turn-start gate with immediate wake on `SpeechEnded` and no polling.
- Default-enabled speech-start interruption reusing the existing settlement and replacement machinery.
- Silent pre-hand-off cancellation and cut of already-audible speech on interruption.
- Attention-snapshot membership as the gate filter, including unconditional self-gating and unattributed-voice
  exclusion.

## Out Of Scope

- Richer importance models beyond owning-character-relative speech and test observations.
- Additional production action tools beyond speech.
- Cancelling or reversing world actions already admitted before interruption.
- Timeline summarisation, compaction, token budgeting, or persistence beyond the Mind node lifetime.
- Automatic retry or backoff policy beyond existing failure containment.
- Multi-agent orchestration and long-term relationship state.
- Final tuning values for importance thresholds and wait durations.
- Perception- or sensing-driven eye presentation changes, including direct gaze assignment. AI-007 alone is the
  separately composed post-attention consumer that may assign a look target; unchanged Vision presentation remains
  mandatory acceptance scope.
- Gating on speech that cannot be attributed to a current-scene character; such speakers never gate and remain an
  accepted limitation of the attribution model.
- Starvation patience bound for the hard gate.

## Acceptance Criteria

### User Requirements

1. An NPC records owning-character, recognised-other, and unknown speech in timeline order through one
   `ObservedSpeech : ObservedAction` contract with exact key `speech.observed`.
2. Rendered speech history uses actor-relative self, recognised-other, and unknown wording and never renders `VoiceId`
   as identity wording or treats it as authenticated provenance.
3. Acceptance verifies bounded, privacy-safe, non-overlapping behaviour, Character-owned speech, and safe containment
   for missing configuration, backend failure, cancellation, and node exit.
4. Acceptance verifies an NPC does not begin a response while an attended speaker's voice is active and starts a due
   response as soon as the speaking window closes and scheduling permits.
5. Acceptance verifies an attended speaker starting speech during an active response cuts the NPC's audible speech and
   yields exactly one fresh response that reflects the new speech.
6. Acceptance verifies speech from unattended or unattributable speakers neither delays nor interrupts the NPC's
   responses.

### Technical Requirements

1. Tests verify every observation enters both timeline and pending FIFO, with importance calculated and validated
   exactly once before atomic mutation.
2. Tests verify self-speech stores importance `0`, external and unknown speech store effective importance `1`, and zero
   importance still processes through maximum wait.
3. Tests verify cumulative-importance threshold, maximum wait, the 0–5-second minimum-interval authoring range,
   disable/re-enable, pre-`_Ready()` intake, and atomic snapshots.
4. Tests verify one qualifying observation cancels an active invocation and starts exactly one fresh replacement after
   full settlement, with one minimum-interval bypass and no overlap.
5. Tests verify cumulative sub-threshold entries do not interrupt, multiple qualifying arrivals coalesce, FIFO order is
   retained, and committed events survive interruption.
6. Tests verify natural completion, cancellation, disable, and node-exit races neither duplicate a replacement nor emit
   erroneous failure diagnostics.
7. Tests verify Mind stamps tool-produced actors, prevents spoofing, atomically ingests ordered batches, and exposes no
   public observation recorder, sink, or timeline-only path.
8. A later turn's sole system instruction reflects the complete ordered timeline without carrying forward transient
    provider transcripts or observation-summary messages.
9. Missing configuration and genuine backend failures are logged and contained without crashing the scene.
10. After tree exit, tests verify no new intake, delayed action, replacement turn, timer, or node-service access occurs.
11. Tests verify `source.Id` is captured as `VoiceId` and compared ordinally with every current-scene character's
    composed `IVoice.Id`, without comparing voice object references.
12. Tests verify blank received and configured IDs do not match during attribution, zero matches remain unknown, one
    exact match yields the character's exact case-sensitive `Character.FullId`, and multiple exact matches fail clearly
    without choosing an actor.
13. Tests verify same-ID spoofing follows the configured attribution, while `ActorId` and nullable `VoiceId` remain
    separate and rendered speech history never exposes `VoiceId` as identity wording or authenticated provenance.
14. Tests verify AI-002's `end_turn` marker never enters Mind, invalid output causes no model repair or automatic retry,
    and observations from actions committed before a later turn failure remain in timeline order.
15. Acceptance verifies both the user-visible bounded, privacy-safe, non-overlapping behaviour and the importance,
    ingestion, speaker-attribution, scheduling, interruption, cancellation, and lifetime contracts.
16. Tests verify ContextWorker child-node ownership and foreground-only `CreateRenderContext` assembly. AgenticMind
    starts with an empty top-level read-only latest dictionary and retains authored workers only for deterministic
    projection aggregation, not trigger notification.
17. Tests verify general typed C# events are published only after observation commitment and genuinely successful
    foreground completion, never for contained failures or cancellations. Relevant triggers subscribe and unsubscribe
    directly without delaying foreground turns; AI-005 remains normative for trigger, projection, and lifetime
    contracts.
18. Tests verify the foreground template renders with AgenticMind's exact complete top-level read-only dictionary from
    `CreateRenderContext`. AgenticMind publishes that exact dictionary atomically only after successful rendering and
    retains the previous published dictionary after construction or rendering failure.
19. Tests verify workers capture only the currently published snapshot and never initiate context construction,
    aggregation, or timeline refresh. A worker projection reaches workers only through a subsequent successfully
    rendered and published foreground context.
20. Tests verify Mind subscribes to configured senses, requires one exact faculty per declared exact percept type before
    activation, and synchronously dispatches immutable percepts without inheritance fallback, queues, or background
    processing.
21. Tests verify complete `PerceptionResult` and calculated-importance validation occurs before mutation, duplicate
    attention effects apply sequentially in order, and ordered observations use the existing atomic ingestion and
    scheduling path, without selecting or assigning an IVision look target.
22. Tests verify foreground context contains self plus all currently resolvable attention-eligible `IContextual`
    subjects, with no unconditional all-scene-character inclusion, second visual scan, hidden subject cache, or Mind
    state passed to `IContextual.GetContext`.
23. Tests verify Mind, not AgenticMind, owns synchronous incoming `IPerception` interpretation; every outbound
    production tool starts once through `AgentTool` and `IMainThreadDispatcher`; AgenticMind has no local deferred
    action machinery; and the actor-stamped self-action speech observation commits exactly once at playback hand-off
    (SPCH-005 TR-26), not at admission.
24. Scene and contract tests verify Mind has no exported output-voice reference and SpeechTool receives the owning
    Character through AI-002's typed context to resolve the Character-authored voice.
25. Contract tests verify that gaze assignment is outside Mind's sensing and perception path and is owned only by the
    separately composed AI-007 post-attention selector.
26. Tests verify every committed observation carries a non-null, monotonically non-decreasing `ObservedAt` stamped
    exactly once at ingestion, and that records published in snapshots are unchanged afterwards.
27. Tests verify the speaking-activity gate: no new turn starts while a gating voice reports `IsSpeaking`; a blocking
    `SpeechEnded` triggers immediate scheduling evaluation without polling; eligibility reached behind the gate stays
    pending and starts right away on wake, while the minimum interval remains respected.
28. Tests verify speech-start interruption reuses the interruption machinery: expected cancellation of the active
    invocation, full settlement of invocation and tool work, exactly one replacement with one minimum-interval bypass,
    coalescing of simultaneous arrivals, no turn overlap, and the replacement waiting behind the gate so its context
    includes the new speech.
29. Tests verify cut playback on interruption: the turn's explicitly cancellable speech submission is cancelled
    silently before playback hand-off with no `SpeechFailed`, no `IHearing` broadcast, and no listener notification;
    already-audible speech is cut through the shared `LipSyncPlayer` stop/cut capability (SPCH-001/SPCH-002);
    SpeechTool passes the turn's cancellation token through the cancellable submission path; ordinary non-turn callers
    keep admission-only semantics.
30. Tests verify the attention filter: a voice gates iff its character is present in the attention snapshot at or
    above the retention threshold regardless of weight; the Mind's own character voice gates unconditionally; a turn's
    own speech admission never cancels its own turn; unattributable voice Ids never gate.
31. Acceptance verifies both the user-visible turn-taking, interruption, and cut-speech behaviour and the gating,
    wake, interruption, cut-playback, and attention-filter contracts.

## References

### Implementation

- `game/src/Mind/Mind.cs`
- `game/src/Mind/Observation/Observation.cs`
- `game/src/Mind/AI/AgenticMind.cs`
- `game/src/Mind/AI/Tool/AgentTool.cs`
- `game/src/Mind/AI/Tool/SpeechTool.cs`

### Related Specifications

- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-005: Context Worker](../005-context-worker/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [CORE-010: Main-Thread Dispatcher](../../core/010-main-thread-dispatcher/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)

### Design Background

- [AI Context Management Memo](../../../docs/ai-context-management-memo.md)
