---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

 The system must provide a Mind component that records an NPC's subjective observations in order, scores them through
 one contextual-importance pipeline, and accumulates notable observations for delivery to the NPC's agent session.

## Goal

 Give NPCs coherent node-lifetime experience without treating transient provider protocol as memory, while keeping
 observation ingestion synchronous, interruption-free, and safe across the node lifetime.

## User Requirements

1. An NPC must remember, for its node lifetime, the ordered observations it perceived or produced through successful
   actions.
2. Every observation must be retained and scored; important observations must reach the NPC promptly through the
   session, while observations below the importance threshold remain recorded and browsable rather than being pushed.
3. Speech history must attribute a speaker by matching the received voice ID to current-scene characters. It must
   distinguish the NPC, a recognised other character, and an unknown speaker without rendering the voice ID as
   identity wording. No match must remain unknown, while ambiguous matches must fail clearly.
4. Spoken responses must use the NPC's character-owned in-world voice rather than normal chat text.
5. Missing configuration and backend failures must be contained and logged without crashing the scene.
6. Removing an NPC's Mind from the scene must prevent delayed actions and other post-destruction effects from that Mind.
7. An NPC's Mind must synchronously interpret sense-owned percepts into attention and zero or more ordered durable
   observations without delaying normal gameplay.
8. Character context assembled for the NPC's session prompt must contain self and every currently resolvable
   contextual subject whose attention meets the context threshold, rather than every scene character unconditionally.
9. Speech from speakers the NPC does not currently attend to, and speech that cannot be attributed to a character,
   must neither wake the NPC's waits nor interrupt the NPC's session.
10. Every remembered event must carry the game time at which it was observed, in seconds elapsed since the game began.

## Technical Requirements

1. Each Mind must own a private, synchronised, ordered timeline of subjective `Observation`
   records. The timeline lasts for the Mind node's lifetime and is the authoritative memory record; the agent
   session's tools read it, and transient provider protocol must never serve as memory.
2. `Observation` must calculate importance through `CalculateImportance(ObservationContext)`. `ObservationContext`
   must initially contain the owning `ICharacter` and remain extensible for future contextual scoring.
3. Mind must calculate and validate importance exactly once at ingestion, before mutation, and store the calculated
   value with the entry. Negative, non-finite, or otherwise invalid importance must reject the entire ingestion.
4. Every successfully ingested observation must enter both the timeline and the notable-observation accumulation.
   There must be no public recorder or sink contract and no timeline-only ingestion path.
5. Accumulated observations must retain FIFO ingestion order. Disabling Mind must pause notable-observation delivery
   and wake signalling while preserving accumulated entries for delivery after re-enable; timeline ingestion itself is
   unaffected.
6. Mind must maintain the notable-observation accumulation with configurable cumulative importance threshold and
   maximum observation wait:
    - the accumulation covers observations ingested since the previous wait completion;
    - when the accumulated importance reaches the threshold, the accumulated observations become notable: an active
      `wait`
      completes early with them, and pending notable observations are delivered by the next `wait` call; and
    - when no wait is active and the session is generating, newly notable observations must signal the session runtime
      to interrupt as defined by AI-002.
7. The maximum observation wait must bound a single `wait`
   call and default to 10 seconds. Threshold and wait values remain configurable; final tuning stays flexible.
8. Delivery must not exempt observations by source. Owning-character actions achieve calm through contextual
   importance rather than a separate ingestion or delivery policy.
9. `ObservedAction`
   must be the actor-aware observation base and retain an exact stable actor ID rather than a scene-node reference.
10. `ObservedSpeech : ObservedAction`
    must represent owning-character, recognised-other, and unknown speech through the exact case-sensitive
    `speech.observed`
    key. It must retain content and nullable raw `VoiceId` separately from `ActorId` identity.
11. `VoiceId`
    must never appear in rendered identity wording or be treated as authenticated provenance. Recognition must be
    relative to the observing Mind; unknown speech must remain representable without inventing an actor identity.
12. Owning-character speech must calculate importance `0`. Recognised-external and unknown speech must retain
    effective importance `1`, while final broader importance models remain tunable and deferred.
13. Mind must stamp tool-produced `ObservedAction` actor IDs with the owning character's exact `FullId`
    before calculating importance. A tool-supplied actor ID must not spoof another character.
14. Mind must atomically ingest each ordered observation batch produced by a tool result. Validation failure must
    append none of the batch to either timeline or accumulation state.
15. Mind must expose read-only, atomic timeline snapshots while keeping mutable timeline storage private. Observation
    records in a published snapshot are treated as immutable. The agent session reads the snapshot through the AI-002
    `history` and `wait` tools; transient provider request history must not provide memory.
16. Observations received before `_Ready()`
    must be retained and enter the notable-observation accumulation when the session runtime becomes available.
17. AgenticMind must run its agent session through [AI-002](../002-agent-runtime/index.md) and render its prompts
    through [AI-003](../003-prompt-api/index.md).
18. Tree exit must establish one irreversible node-lifetime boundary that stops intake, session activity, timers, and
    cue subscriptions, and cancels active observation processing. Deferred callbacks must not access Mind services
    after exit.
19. Node-lifetime cancellation must propagate through active agent and tool work. Expected interruption and lifetime
    cancellation must not be reported as backend failures or trigger retries or unintended session activity.
20. Mind's `SpeechPerception` faculty must resolve attribution only against `ICharacter`
    instances in the current scene. It must compare the percept's raw source voice `Id`
    ordinally with each character's composed `IVoice.Id`. Voice object-reference identity, lore prose, character
    names, and aliases must not participate in matching.
21. During character attribution, blank received IDs and blank configured character voice IDs must not match. Zero
    matches must leave `ActorId`
    null; exactly one match must set `ActorId`
    to that character's exact case-sensitive `Character.FullId`; multiple matches must fail clearly without selecting
    an actor.
22. Voice-ID matching is configured, operational attribution rather than authenticated provenance. A source that
    presents another character's voice ID can therefore be attributed to that character; same-ID spoofing is an
    accepted limitation of this model.
23. AI-002's tool protocol is transient session protocol. Successfully committed action observations must use the
    ordinary Mind ingestion path, while provider protocol content must never become timeline memory.
24. Tool errors surface through tool results under AI-002 and settle without model repair or automatic retry. Any
    observations committed by earlier successful actions must remain in timeline order.
25. AgenticMind must initialise its render dictionary to an empty top-level read-only dictionary. Only session start
     may call `CreateRenderContext`
     to create AgenticMind's own top-level read-only render dictionary from current character context, the player
     character's context under [SCN-001](../../scene/001-scene-context-api/index.md) — a mandatory, unconditional key
     resolved via `ISceneContext.Player`, never attention-gated — deterministic attention-eligible subject context,
     which may omit the player, and the current scenario under
     [AI-008](../008-scenario/index.md). The dictionary defines no `observations` key: observations reach the model
     exclusively through the AI-002 session's tool results and interruption injections. AI-006 normatively defines
     attention eligibility and scene resolution; AI-008
     normatively defines the two-phase construction order in which the core context is built first and completed with
     the `scenario`
     key after the manager query. The session prompt must render with the exact dictionary returned.
26. AgenticMind must publish a general typed C# event after each committed observation; the base Mind exposes only the
    protected `OnObservationIngested` hook, which AgenticMind overrides to publish. Relevant consumers subscribe and
    unsubscribe directly. Contained failures and cancellations must not publish events for uncommitted work.
27. Mind must subscribe to configured `ISense`
    components and own authorable Resource faculties, exact percept-type registration, synchronous interpretation,
    attention, result validation, and observation ingestion. AI-006 is the normative percept, sense, faculty,
    attention, and result contract.
28. Before activation, Mind must require exactly one exact faculty mapping for every exact percept type declared by
    its configured senses. Missing, duplicate, incompatible, or undeclared mappings must fail clearly.
29. Mind must validate a complete `PerceptionResult`, including every calculated observation importance, before any
    mutation. It then applies ordered attention effects sequentially and atomically ingests ordered observations
    through the existing timeline and notable-observation accumulation path. This sensing and perception path must not
    select or assign an IVision look target.
30. AgenticMind must own only provider, prompt, render-context, and tool concerns. Incoming sensory interpretation
    remains synchronous through Mind's `IPerception`
    faculties. Outbound production-tool invocation must start once through `AgentTool`
    and the shared `IMainThreadDispatcher`; cancellation remains linked to session and Mind lifetime. The Game-scoped
    dispatcher owns accepted-work queueing and settlement, and AgenticMind must not retain local deferred voice or
    Godot-action machinery. The actor-stamped self-action speech observation commits exactly once at playback hand-off
    (SPCH-005 TR-26), not at admission, through ordinary Mind ingestion.
31. Mind must not own or export an output-voice reference. Character-owned capabilities required by tools must enter
    through AI-002's typed `ScenarioContext`; Character remains the sole authored voice source under CHAR-002.
32. AI-007 separately defines the direct Mind-child post-attention consumer that may assign a look target. It consumes
    Mind's published attention snapshot after perception has completed; Mind's sensing and attention-mutation
    contracts remain gaze-neutral.
33. Mind must stamp each committed observation exactly once with an `ObservedAt`
    timestamp in game-time seconds from the game-scoped game-time source (AI-002), stamped at ingestion before the
    record enters the timeline or accumulation. Stamps must be monotonically non-decreasing. The identical stamped
    record must be used for the timeline, the notable accumulation, and ingestion notification. Observations are
    otherwise unchanged and remain immutable after publication.
34. Attended-speaker-finished cue: Mind must monitor the speaking windows of attended speakers — voices whose owning
    character's canonical `FullId`
    is present in Mind's current attention snapshot at or above the retention threshold (AI-006), regardless of weight
    or score — and signal the session runtime when such a speaker's window closes (`SpeechEnded`, SPCH-005 TR-2),
    waking an active `wait`
    and unblocking a blocked `speak`
    under AI-002. Voices whose speaker cannot be attributed to a current-scene character must not signal; this is an
    accepted limitation of the attribution model.
35. Notable-observation signalling must never interrupt observation ingestion itself: ingestion is synchronous and
    atomic, and wake or interruption signalling happens only after the batch has committed.

## In Scope

- Mind-owned node-lifetime observation timeline and notable-observation accumulation.
- Contextual importance calculation, validation, and stored values.
- Unified external and tool-result observation ingestion.
- Threshold and maximum-wait behaviour driving `wait` early completion and notable-observation delivery.
- Actor-relative observed speech, current-scene voice-ID attribution, and separately stored voice IDs.
- Synchronous percept interpretation, exact faculty dispatch, and Mind-owned attention under AI-006.
- Published attention snapshots for the separately composed, post-attention AI-007 gaze selector; direct gaze
  assignment remains outside Mind sensing and perception processing.
- Attention-filtered session contextual-subject selection.
- AgenticMind session orchestration through AI-002 and AI-003.
- Typed tool-context hand-off of Character-owned capabilities without Mind-owned voice authoring.
- Session-start render-context construction.
- Game-time `ObservedAt` stamping through the game-scoped game-time source.
- Attended-speaker-finished cue for `wait` wake and `speak` unblocking.
- Irreversible node-lifetime shutdown of intake, session activity, tools, and dispatcher-queued action work.

## Out Of Scope

- Richer importance models beyond owning-character-relative speech and test observations.
- Additional production tools beyond the AI-002 inventory.
- Cancelling or reversing world actions already admitted.
- Timeline summarisation, compaction, token budgeting, or persistence beyond the Mind node lifetime.
- Automatic retry or backoff policy beyond existing failure containment.
- Multi-agent orchestration and long-term relationship state.
- Final tuning values for importance thresholds and wait durations.
- Perception- or sensing-driven eye presentation changes, including direct gaze assignment. AI-007 alone is the
  separately composed post-attention consumer that may assign a look target; unchanged Vision presentation remains
  mandatory acceptance scope.
- Cueing on speech that cannot be attributed to a current-scene character; such speakers never wake or block and
  remain an accepted limitation of the attribution model.

## Acceptance Criteria

### User Requirements

1. An NPC records owning-character, recognised-other, and unknown speech in timeline order through one
   `ObservedSpeech : ObservedAction`
   contract with exact key `speech.observed`.
2. Rendered speech history uses actor-relative self, recognised-other, and unknown wording and never renders `VoiceId`
   as identity wording or treats it as authenticated provenance.
3. Acceptance verifies bounded, privacy-safe behaviour, Character-owned speech, and safe containment for missing
   configuration, backend failure, cancellation, and node exit.
4. Acceptance verifies important observations reach the NPC promptly through wait delivery or session interruption,
   while sub-threshold observations stay recorded and browsable.
5. Acceptance verifies speech from unattended or unattributable speakers neither wakes the NPC's waits nor interrupts
   the NPC's session.
6. Acceptance verifies every remembered event carries a game-time stamp in seconds elapsed since the game began.

### Technical Requirements

1. Tests verify every observation enters both timeline and notable accumulation, with importance calculated and
   validated exactly once before atomic mutation.
2. Tests verify self-speech stores importance `0` and external and unknown speech store effective importance `1`.
3. Tests verify the cumulative-importance threshold, the default 10-second maximum observation wait,
   accumulation-window reset on wait completion, disable/re-enable pause and preservation, pre-`_Ready()`
   intake, and atomic snapshots.
4. Tests verify threshold crossing makes the accumulated window notable, completes an active wait early, and is
   delivered by the next wait when none is active, and that sub-threshold observations never enter wait results while
   remaining in the timeline.
5. Tests verify Mind stamps tool-produced actors, prevents spoofing, atomically ingests ordered batches, and exposes
   no public observation recorder, sink, or timeline-only path.
6. Tests verify a later recall of the timeline reflects the complete ordered record without carrying forward transient
   provider transcripts or observation-summary messages.
7. Missing configuration and genuine backend failures are logged and contained without crashing the scene.
8. After tree exit, tests verify no new intake, delayed action, session activity, timer, or node-service access occurs.
9. Tests verify `source.Id` is captured as `VoiceId`
   and compared ordinally with every current-scene character's composed `IVoice.Id`, without comparing voice object
   references.
10. Tests verify blank received and configured IDs do not match during attribution, zero matches remain unknown, one
    exact match yields the character's exact case-sensitive `Character.FullId`, and multiple exact matches fail
    clearly without choosing an actor.
11. Tests verify same-ID spoofing follows the configured attribution, while `ActorId` and nullable `VoiceId`
    remain separate and rendered speech history never exposes `VoiceId`
    as identity wording or authenticated provenance.
12. Tests verify invalid model output causes no model repair or automatic retry, and observations from actions
    committed before a later tool failure remain in timeline order.
13. Acceptance verifies both the user-visible bounded, privacy-safe behaviour and the importance, ingestion,
    speaker-attribution, notable-delivery, cancellation, and lifetime contracts.
14. Tests verify session-start-only `CreateRenderContext`
    assembly, with AgenticMind starting from an empty top-level read-only dictionary and the session prompt rendering
    with the exact dictionary returned.
15. Tests verify the general typed C# event is published only after observation commitment, never for contained
    failures or cancellations, with consumers subscribing and unsubscribing directly.
16. Tests verify Mind subscribes to configured senses, requires one exact faculty per declared exact percept type
    before activation, and synchronously dispatches immutable percepts without inheritance fallback, queues, or
    background processing.
17. Tests verify complete `PerceptionResult`
    and calculated-importance validation occurs before mutation, duplicate attention effects apply sequentially in
    order, and ordered observations use the existing atomic ingestion path, without selecting or assigning an IVision
    look target.
18. Tests verify session context contains self plus all currently resolvable attention-eligible `IContextual`
    subjects, with no unconditional all-scene-character inclusion, second visual scan, hidden subject cache, or Mind
    state passed to `IContextual.GetContext`.
19. Tests verify Mind, not AgenticMind, owns synchronous incoming `IPerception`
    interpretation; every outbound production tool starts once through `AgentTool`
    and `IMainThreadDispatcher`; AgenticMind has no local deferred action machinery; and the actor-stamped self-action
    speech observation commits exactly once at playback hand-off (SPCH-005 TR-26), not at admission.
20. Scene and contract tests verify Mind has no exported output-voice reference and SpeechTool receives the owning
    Character through AI-002's typed context to resolve the Character-authored voice.
21. Contract tests verify that gaze assignment is outside Mind's sensing and perception path and is owned only by the
    separately composed AI-007 post-attention selector.
22. Tests verify every committed observation carries a non-null, monotonically non-decreasing `ObservedAt`
    in game-time seconds from the game-scoped game-time source, stamped exactly once at ingestion, and that records
    published in snapshots are unchanged afterwards.
23. Tests verify the attended-speaker-finished cue: a speaker present in the attention snapshot at or above the
    retention threshold wakes an active wait and unblocks a blocked speak on `SpeechEnded`; unattended or
    unattributable voices never signal.
24. Tests verify wake and interruption signalling occurs only after the committing batch settles and never interrupts
    observation ingestion itself.
25. Tests verify the session prompt renders with a dictionary containing a mandatory `player`
     value under [SCN-001](../../scene/001-scene-context-api/index.md) — resolved unconditionally via
     `ISceneContext.Player`
     and never attention-gated, even when the attention-gated `characters`
     dictionaries omit the player — and the session's `scenario` value under [AI-008](../008-scenario/index.md), with
     no `observations` key in the dictionary.

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
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [AI-008: Scenario](../008-scenario/index.md)
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
