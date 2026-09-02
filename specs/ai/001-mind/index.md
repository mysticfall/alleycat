---
id: AI-001
title: Mind Component
---

# Mind Component

## Requirement

 The system must provide a Mind component that records an NPC's subjective observations in order, scores them through
 one contextual-importance pipeline, evaluates contextual freshness, and accumulates notable observations for
 delivery to the NPC's agent session.

## Goal

 Give NPCs coherent node-lifetime experience without treating transient provider protocol as memory, while keeping
 ordered perception, atomic observation ingestion, and node-lifetime shutdown safe.

## User Requirements

1. An NPC must remember, for its node lifetime, the ordered observations it perceived or produced through successful
   actions.
2. Observations are retained by default. An observation type may suppress the latest equivalent retained observation
   in its semantic scope. Importance is accumulated delivery pressure: ordinary observations that cross the
   importance threshold complete an active `wait` or coalesce for delivery at the next natural session boundary,
   while observations below the threshold remain recorded and browsable unless a fresh turn delivers them. Ordinary
   observations must never cancel the NPC's generation, tools, or speech.
3. Speech history must attribute a speaker by matching the received voice ID to current-scene characters. It must
   distinguish the NPC, a recognised other character, and an unknown speaker without rendering the voice ID as
   identity wording. No match must remain unknown, while ambiguous matches must fail clearly.
4. Spoken responses must use the NPC's character-owned in-world voice rather than normal chat text.
5. Missing configuration and backend failures must be contained and logged without crashing the scene.
6. Removing an NPC's Mind from the scene must prevent delayed actions and other post-destruction effects from that Mind.
7. An NPC's Mind must accept sense-owned percepts and faculty-emitted observations immediately, interpret them
   asynchronously in enqueue order, and commit each observation's attention and durable record atomically without
   blocking the publisher.
8. Character context assembled for the NPC's session prompt must contain self and every currently resolvable
   attention-eligible character, rather than every scene character unconditionally.
9. Every accepted non-self speech observation — recognised or unknown speaker, attended or not — must immediately
   reach the NPC's session as a fresh turn that replaces stale reasoning, regardless of the importance threshold.
   Ambiguous speech that fails attribution produces no observation and therefore no fresh turn. Attention
   membership governs turn-taking cues and start/resume suppression (UR-13) only and must never gate fresh-turn
   delivery; this all-hearer, attention-independent completed-speech delivery is intended.
10. Every remembered event must carry the game time at which it was observed, in seconds elapsed since the game began.
11. Looking at a valid visual subject may add its focused description to memory, without retaining repeated equivalent
     descriptions of that same subject.
12. When a speaker pauses briefly and continues, the NPC experiences one contiguous conversation: every completed
    spoken part is remembered exactly as observed, while the dialogue the NPC reasons over presents the joined parts
    as a single utterance.
13. When a speaker the NPC attends to at that moment begins or resumes speaking, the NPC immediately stops its
    in-flight reaction and holds its reasoning until the utterance settles — before any continued words exist — and
    never replies to a half-finished utterance. The hold is source-generic — any attended non-self character, never
    assumed to be the player — and is decided once at cue receipt, so attention changes afterwards neither release
    nor retroactively create it. A speaker not attended at cue receipt causes no hold, and their completed speech
    still arrives as a fresh turn. The hold never cuts the NPC's own speech already admitted to the voice pipeline,
    which settles naturally (AI-002). Effects the NPC already committed remain part of history.
14. A blank, failed, or abandoned automatic segment — or a manual session that ends without committed text — settles
    any matching paused reaction without inventing speech. It never becomes NPC memory, attention, a wait result,
    freshness input, or a transcript.

## Technical Requirements

1. Each Mind must own a private, synchronised, ordered timeline of subjective `Observation`
   records. The timeline lasts for the Mind node's lifetime and is the authoritative memory record of raw
   observations; the agent session's tools read it, and transient provider protocol must never serve as memory. The
   timeline is append-only at the raw observation level: model-facing projection that coalesces grouped speech
   (TR-48, AI-003) is rendering and never mutates timeline records.
2. `Observation` must calculate importance through `CalculateImportance(ObservationContext)`. `ObservationContext`
   must initially contain the owning `ICharacter` and remain extensible for future contextual scoring.
3. Mind must calculate and validate importance exactly once at ingestion, before mutation, and store the calculated
   value with the entry. Negative, non-finite, or otherwise invalid importance must reject the entire ingestion.
4. Every successfully ingested observation must enter both the timeline and the notable-observation accumulation.
   There must be no public recorder or sink contract and no timeline-only ingestion path.
5. Accumulated observations must retain FIFO ingestion order. Disabling Mind must pause notable-observation delivery
   and wake signalling while preserving accumulated entries — including any retained fresh urgency — for delivery
   after re-enable; timeline ingestion itself is unaffected. Fresh-turn delivery guarantees apply while the Mind is
   enabled; broader disable-mode semantics are deferred (Out Of Scope).
6. Mind must maintain the notable-observation accumulation with configurable cumulative importance threshold and
   maximum observation wait:
    - the accumulation covers observations ingested since the previous wait completion;
    - when the accumulated importance reaches the threshold, the accumulated observations become notable: an active
      `wait`
       completes early with them, and pending notable observations are delivered by the next `wait` call; and
     - when no wait is active, newly notable observations must be coalesced and injected at the next natural
       model-request boundary of the always-running session (AI-002); ordinary notable observations must never
       cancel generation, tool execution, or speech.
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
19. Node-lifetime cancellation must propagate through active agent and tool work and is terminal: it takes precedence
    over fresh-turn invalidation and must issue no replacement request, synthetic tool result, or other follow-up
    session activity. Expected interruption and lifetime cancellation must not be reported as backend failures or
    trigger retries or unintended session activity.
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
     [AI-008](../008-scenario/index.md). The owner appears in both `character` and `characters[owner.FullId]` as the
     exact same view instance, and assembly fails clearly for an invalid included identity, a duplicate exact included
      `FullId`, or an owner absent from the scene context. The dictionary defines no `observations` key: observations
      reach the model exclusively through the AI-002 session's tool results, `wait` results, and injected messages.
      AI-006
     normatively defines
     attention eligibility and scene resolution; AI-008
     normatively defines the two-phase construction order in which the core context is built first and completed with
     the `scenario`
     key after the manager query. The session prompt must render with the exact dictionary returned.
26. AgenticMind must publish a general typed C# event after each committed observation; the base Mind exposes only
    the protected `OnObservationIngested` observation hook, which AgenticMind overrides to publish. Relevant consumers
    subscribe and unsubscribe directly. Contained failures and cancellations must not publish events for uncommitted
    work.
27. Mind must subscribe to configured `ISense` components and discover authorable `IPerception` Node faculties among
    its direct children in scene order. It must subscribe to each configured faculty's observation-emission event for
    its node lifetime, unsubscribing on rebind and tree exit. It owns percept registration, asynchronous
    interpretation, attention application, per-observation validation, and observation ingestion. AI-006 is the
    normative percept, sense, faculty, observation, and attention contract.
28. Before activation, Mind must require at least one assignability-compatible faculty for every exact concrete percept
    type declared by its configured senses. Multiple matching faculties are intentional and execute in direct-child
    order. Missing, incompatible, duplicate declared, or undeclared publisher types must fail clearly.
29. On publication, Mind must synchronously validate that the publisher declared the percept's exact concrete type,
    snapshot the current ordered matching-faculty binding, and enqueue interpretation without blocking the sense.
    Faculty-emitted observations must enqueue into the same serial worker alongside percept work in enqueue order, and
    publication callbacks must never block on interpretation or commit. Mind must process the queue serially in
    enqueue order and await each matching faculty sequentially.
30. Mind must commit each observation as one independent atomic unit, applying its attention effects with its own
    attention settings and ingesting durable records through the ordinary timeline and notable-observation
    accumulation path. Faults and invalid observations are contained and logged and roll back only that observation:
    earlier commits stand and later queued items continue; cancellation and a final lifetime guard prevent post-exit
    commits. This path must not select or assign an `IVision` look target.
31. AgenticMind must own only provider, prompt, render-context, and tool concerns. Incoming sensory interpretation
    remains asynchronous through Mind's `IPerception` faculties. Outbound production-tool invocation must start once
     through `AgentTool`
     and the shared `IMainThreadDispatcher`; cancellation remains linked to session activity, fresh-turn invalidation
     (AI-002), and Mind lifetime. The Game-scoped
    dispatcher owns accepted-work queueing and settlement, and AgenticMind must not retain local deferred voice or
    Godot-action machinery. The actor-stamped self-action speech observation commits exactly once at playback hand-off
    (SPCH-005 TR-26), not at admission, through ordinary Mind ingestion.
32. Mind must not own or export an output-voice reference. Character-owned capabilities required by tools must enter
    through AI-002's typed `ScenarioContext`; Character remains the sole authored voice source under CHAR-002.
33. AI-007 separately defines the direct Mind-child post-attention consumer that may assign a look target. It consumes
    Mind's published attention snapshot after perception has completed; Mind's sensing and attention-mutation
    contracts remain gaze-neutral.
34. Mind must stamp each committed observation exactly once with an `ObservedAt`
    timestamp in game-time seconds from the game-scoped game-time source (AI-002), stamped at ingestion before the
    record enters the timeline or accumulation. Stamps must be monotonically non-decreasing. The identical stamped
    record must be used for the timeline, the notable accumulation, and ingestion notification. Observations are
    otherwise unchanged and remain immutable after publication.
35. Attended-speaker-finished cue: Mind must monitor the speaking windows of attended speakers — voices whose owning
    character's canonical `FullId`
    is present in Mind's current attention snapshot at or above the retention threshold (AI-006), regardless of weight
    or score — and signal the session runtime when such a speaker's window closes (`SpeechEnded`, SPCH-005 TR-2),
    waking an active `wait`
    and unblocking a blocked `speak`
     under AI-002. Voices whose speaker cannot be attributed to a current-scene character must not signal; this is an
     accepted limitation of the attribution model. This cue is a turn-taking and wait-wake cue only; it must not
     constrain fresh-turn speech invalidation (TR-43). The same retention-threshold snapshot membership rule governs
     speech-start and speech-resume suppression (TR-47): only a source voice that resolves, at cue receipt, to a
     unique non-self current-scene character present in the snapshot may hold the session runtime (AI-002).
36. Notable-observation and freshness signalling must never interrupt observation ingestion itself: ingestion is
    synchronous and atomic, and wake or delivery signalling happens only after the batch has committed.
37. `ObservationDuplicatePolicy` must default to `Allow`, retaining every submitted observation. An observation that
    selects `IgnoreEquivalent` must expose a stable duplicate scope and semantic equality that both exclude
    `ObservedAt`.
38. Mind must apply duplicate filtering to the complete staged batch before importance calculation, timestamping,
    timeline or notable-accumulation mutation, and ingestion notification. Earlier accepted entries in the same staged
    batch participate in filtering. A suppressed entry causes none of those effects.
39. Duplicate comparison must scan backwards to the latest retained observation of the same concrete type and ordinal
    scope. Removed or summarised observations no longer participate. Timeline summarisation remains out of scope.
40. `ObservedVisualDescription` must use exact key `vision.description`, scope duplicates by ordinal subject `FullId`,
    and compare ordinal subject identity plus description against the latest retained entry in that scope.
41. An observation marked transient under AI-006 must apply its attention effects atomically and otherwise leave no
    record: no `ObservedAt` stamp, no duplicate-history comparison, no timeline entry, no notable-observation
    accumulation, no session prompt history, and no committed-observation notification. Durable observations alone
    follow the ordinary ingestion path that requirements 34-40 govern.
42. `Observation` must expose contextual freshness through virtual `RequiresFreshTurn(ObservationContext)`, which
    defaults to `false`. Mind must evaluate freshness exactly once during the same staged, duplicate-filtered
    ingestion pass as importance — before timestamping, mutation, or commitment — and store the result with the
    entry. Rejected duplicates must contribute neither importance nor freshness.
43. Accepted non-self `ObservedSpeech` must require a fresh turn: recognised-external and unknown speech return
    `true`, while exact self speech returns `false` through ordinal comparison with the observing character's exact
    full ID. A fresh observation upgrades the complete current accumulation — including preceding sub-threshold
    observations — to deliverable in FIFO order without depending on cumulative importance, and signals the session
    runtime for immediate fresh-turn replacement as defined by AI-002. Attention membership must not gate freshness;
    this all-hearer, attention-independent freshness is intended — attention gating applies only to start/resume
    suppression cues (TR-47), never to completed-speech delivery.
44. Mind's post-commit delivery signalling must carry delivery urgency — ordinary threshold-qualified delivery
    versus fresh-turn urgency — and whether an active `wait` owns the delivery, rather than a single
    notable-interruption signal. A claimed delivery must not be silently lost if asynchronous rendering fails:
    ownership is retained or restored, and no replacement request may be issued without its invalidating context.
45. Completed speech segments must ingest as separate raw observations: each completed segment of a pause-delimited
    speech group becomes its own immutable `ObservedSpeech` record carrying the generic grouping metadata —
    `SpeechGroupID`, `SegmentIndex`, and `Continued` — copied unchanged from the percept (AI-006; segment identity
    and lifecycle are normatively defined by SPCH-005 and SPCH-008). No committed record is mutated, replaced, or
    rolled back when later segments of the same group arrive, and records never carry per-word or in-flight partial
    text. Ungrouped speech — manual and AI voice publications — carries no group metadata.
46. A transport-level duplicate completion presenting an already-ingested commit identity (TR-49) must be rejected
    before timestamping: no second timeline entry, notable-accumulation entry, freshness delivery, or ingestion
    notification. The identity gate precedes duplicate filtering, importance calculation, timestamping, mutation,
    and notification, and does not alter the duplicate policy of observations that supply no commit identity —
    including ungrouped speech.
47. Mind must route the speech-start cue (`Started`), `SpeechResumed`, and non-published automatic terminal
    settlements — `Blank`, `Failed`, and `Abandoned` — as generic transient lifecycle signals for source voices whose
    publications its Hearing observes. Each carries only the source voice plus its immutable lifecycle key, never
    text. Automatic signals carry real speech-group and segment metadata — a qualified onset carries its
    `(SpeechGroupID, 0)` identity — while the manual start cue carries an opaque internal synthetic token because
    manual completed speech remains publicly ungrouped; the token never becomes public grouping metadata. Start and
    resume signals are attention-gated at cue receipt and source-generic: Mind forwards them to the session runtime
    only when the source voice resolves, at cue receipt, to a unique non-self current-scene character present in
    Mind's current attention snapshot at or above the retention threshold (TR-35; AI-006), never assuming the player.
    Attention is sampled exactly once per cue; later attention changes must neither release nor retroactively create
    a hold. Unattended, self, unattributable, and ambiguous sources create no hold. Mind forwards each signal exactly
    once to the session runtime: `Started` and `SpeechResumed` register the matching invalidation expectation, while
    a non-published settlement abandons it; a settlement matching no registered expectation is a no-op (AI-002). It
    creates no percept, faculty input, observation, timeline
    or notable-accumulation entry, attention effect, freshness delivery, wait effect, transcript, or a committed
    observation notification. `Published` is not routed as a transient release: its matching completed text follows
    ordinary perception and delivery. These signals are neither
    ordinary nor fresh observations, so the ordinary-observation no-cancellation rule (UR-2) does not apply. SPCH-005
    normatively defines publication, metadata, and node-lifetime terminality.
48. Model-facing coalescing of a speech group is rendering, not timeline summarisation: the projection that joins
    grouped segments into one synthetic utterance is owned by AI-003 and applied only when history, `wait`, or
    injected-message rendering faces the model (AI-002); `history` counting applies to projected events, not raw
    segment records. Mind must not merge, reorder, rewrite, or summarise raw timeline records when a group grows,
    and must never expose the projection back into the timeline.
49. Mind's identity-based duplicate suppression must operate through a generic, optional commit-identity contract
    under `Mind.Observation`: any observation type may supply an immutable identity tuple, and Mind enforces
    exact-once identity uniqueness atomically at ingestion — enforcement stays in Mind, never in perception. Mind's
    generic ingestion must contain no modality-specific branch: the identity gate tests the supplied identity, not
    concrete observation types. `ObservedSpeech` keeps its exact-once `(VoiceId, SpeechGroupID, SegmentIndex)`
    semantics (TR-45) by supplying that tuple through this contract, with grouping metadata normatively defined by
    AI-006, SPCH-005, SPCH-006, and SPCH-008.
50. AgenticMind must orchestrate session delivery without interpreting concrete observation record types: it must
    not cast or alias concrete observation records or read their feature payloads — for speech, no `VoiceId`,
    `SpeechGroupID`, or `SegmentIndex` inspection in expectation matching or watchdog scans. Feature-specific
    correlation belongs to session-scoped coordinators in the Mind.AI integration layer — for speech turns and
    continuations, the coordinator pinned by AI-002 — which translate feature identity into generic runtime
    operations. The TR-35 attended-speaker cue and TR-47 lifecycle-routing contracts are unchanged.

## In Scope

- Mind-owned node-lifetime observation timeline and notable-observation accumulation.
- Contextual importance calculation, validation, and stored values.
- Unified external and tool-result observation ingestion.
- Threshold and maximum-wait behaviour driving `wait` early completion, ordinary boundary injection, and
  notable-observation delivery.
- Contextual freshness evaluation and fresh-turn delivery signalling, independent of the importance threshold and
  attention membership.
- Actor-relative observed speech, current-scene voice-ID attribution, and separately stored voice IDs.
- Raw append-only ingestion of completed speech-segment observations with generic grouping metadata, transport
  duplicate rejection through the generic observation commit-identity contract, and generic attention-gated
  transient start/resume and non-published settlement routing.
- Generic optional commit-identity contract for observation ingestion, with Mind-owned atomic exact-once enforcement.
- AgenticMind delivery orchestration delegating feature-specific correlation to session-scoped Mind.AI coordinators.
- Immediate percept intake, ordered asynchronous faculty interpretation, deterministic fan-out, and Mind-owned
  attention under AI-006.
- Default-allow observation ingestion and opt-in latest-equivalent suppression before all ingestion effects.
- Published attention snapshots for the separately composed, post-attention AI-007 gaze selector; direct gaze
  assignment remains outside Mind sensing and perception processing.
- Attention-filtered session character selection.
- AgenticMind session orchestration through AI-002 and AI-003.
- Typed tool-context hand-off of Character-owned capabilities without Mind-owned voice authoring.
- Session-start render-context construction.
- Game-time `ObservedAt` stamping through the game-scoped game-time source.
- Attended-speaker-finished cue for `wait` wake and `speak` unblocking, and attention-gated speech-start/resume
  suppression routing into AI-002 holds.
- Irreversible node-lifetime shutdown of intake, session activity, tools, and dispatcher-queued action work.

## Out Of Scope

- Richer importance models beyond owning-character-relative speech and test observations.
- Additional production tools beyond the AI-002 inventory.
- Cancelling or reversing world actions already admitted.
- Timeline summarisation, compaction, token budgeting, or persistence beyond the Mind node lifetime.
- Timeline-level speech-group merging or record rewriting; grouped-utterance coalescing exists only as model-facing
  rendering (AI-003), never as timeline mutation.
- Automatic retry or backoff policy beyond existing failure containment.
- Multi-agent orchestration and long-term relationship state.
- Final tuning values for importance thresholds and wait durations.
- Perception- or sensing-driven eye presentation changes, including direct gaze assignment. AI-007 alone is the
  separately composed post-attention consumer that may assign a look target; unchanged Vision presentation remains
  mandatory acceptance scope.
- Attended-speaker-finished cueing and speech-start/resume suppression on speech that cannot be attributed to a
  current-scene character; such speakers never block `speak`, wake through the cue, or hold the session runtime,
  while their accepted unknown speech observations still require a fresh turn.
- Gameplay policy for interrupting already-audible speech; playback hand-off commits speech and freshness must not
  cut it (AI-002).
- Reliable speaker priority, addressee, audibility, or conversational-target metadata; attention membership and
  name-text heuristics must not substitute for it.
- Distinct disable modes — such as agent-loop culling, sleep, unconsciousness, or permanent shutdown — beyond the
  existing pause-and-retain `Enabled` behaviour.

## Acceptance Criteria

### User Requirements

1. An NPC records owning-character, recognised-other, and unknown speech in timeline order through one
   `ObservedSpeech : ObservedAction`
   contract with exact key `speech.observed`.
2. Rendered speech history uses actor-relative self, recognised-other, and unknown wording and never renders `VoiceId`
   as identity wording or treats it as authenticated provenance.
3. Acceptance verifies bounded, privacy-safe behaviour, Character-owned speech, and safe containment for missing
   configuration, backend failure, cancellation, and node exit.
4. Acceptance verifies important observations reach the NPC promptly through wait delivery or boundary injection
   without cancelling active generation, tools, or speech, while sub-threshold observations stay recorded and
   browsable unless a fresh turn delivers them.
5. Acceptance verifies recognised and unknown speech — attended or not — produce an immediate fresh turn, while
   ambiguous speech produces no observation and no fresh turn.
6. Acceptance verifies every remembered event carries a game-time stamp in seconds elapsed since the game began.
7. Acceptance verifies a focused visual subject can enter memory with its description and that the latest equivalent
   description in the same subject scope is suppressed.
8. Acceptance verifies a fresh observation delivers the pending accumulation plus the fresh observation exactly once,
   wakes an active `wait` below the importance threshold, and that ordinary observations never cancel generation,
   tools, or speech.
9. Acceptance verifies a speaker who pauses and continues is heard as one contiguous conversation: each completed
   part is remembered exactly as observed, model-facing dialogue joins the parts into one utterance, and resumed
   speech promptly stops stale reasoning without the NPC replying to a half-finished utterance. Acceptance also
   verifies the hold's attention gating: an attended speaker's onset or resume holds the NPC's reaction, a
   non-attended onset or resume causes no hold while completed speech still fresh-delivers, attention changes after
   cue receipt neither release nor retroactively create a hold, and a non-player attended source holds exactly like
   the player's voice would.
10. Acceptance verifies blank, failed, and abandoned segment settlements — and a manual session ending without
    committed text — release only the matching paused reaction, without an invented utterance, memory, attention
    change, wait result, freshness effect, or transcript, and that a settlement matching no hold is a no-op.

### Technical Requirements

1. Tests verify every observation enters both timeline and notable accumulation, with importance calculated and
   validated exactly once before atomic mutation.
2. Tests verify self-speech stores importance `0` and external and unknown speech store effective importance `1`.
3. Tests verify the cumulative-importance threshold, the default 10-second maximum observation wait,
   accumulation-window reset on wait completion, disable/re-enable pause and preservation, pre-`_Ready()`
   intake, and atomic snapshots.
4. Tests verify threshold crossing makes the accumulated window notable, completes an active wait early, and is
   delivered by the next wait when none is active, and that sub-threshold observations enter wait results only when
   a fresh observation upgrades the complete accumulation, while otherwise remaining in the timeline.
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
16. Tests verify Mind subscribes to configured senses, synchronously validates each publisher's exact concrete type,
    snapshots all assignability-matched faculty bindings, and accepts immutable percepts without blocking the publisher.
17. Tests verify Mind serialises percept interpretation and faculty-emitted observations in enqueue order on one
    serial worker, invokes matching faculties sequentially in direct-child order, commits each observation as one
    independent atomic unit, and never blocks publication callbacks on interpretation or commit, without selecting or
    assigning an `IVision` look target.
18. Tests verify session context contains self plus all currently resolvable attention-eligible characters resolved as
    `ICharacter` subjects, with no unconditional all-scene-character inclusion, second visual scan, hidden subject
    cache, or Mind or attention state passed into render-context assembly. The owner appears in both `character` and
    `characters[owner.FullId]` as the exact same view instance, and invalid included identity, duplicate exact included
    `FullId`, or owner absence fails assembly clearly.
19. Tests verify Mind, not AgenticMind, owns asynchronous incoming `IPerception`
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
    unattributable voices never signal. The same membership rule gates start/resume suppression forwarding (TR-47):
    attended unique non-self sources hold, while unattended, self, unattributable, and ambiguous sources never do.
24. Tests verify wake and delivery signalling occurs only after the committing batch settles and never interrupts
    observation ingestion itself.
25. Tests verify the session prompt renders with a dictionary containing a mandatory `player`
     value under [SCN-001](../../scene/001-scene-context-api/index.md) — resolved unconditionally via
     `ISceneContext.Player`
     and never attention-gated, even when the attention-gated `characters`
     dictionaries omit the player — and the session's `scenario` value under [AI-008](../008-scenario/index.md), with
     no `observations` key in the dictionary.
26. Tests verify observation duplicate handling defaults to allow and that ignore-equivalent scope and semantic equality
    exclude `ObservedAt`.
27. Tests verify duplicate filtering precedes importance, timestamp, ingestion, and notification; includes earlier
    accepted staged entries; and ignores removed or summarised entries.
28. Tests verify `ObservedVisualDescription` uses ordinal subject `FullId` scope and ordinal subject-plus-description
    equality against the latest retained observation of the same concrete type and scope.
29. Async tests verify enqueue-order serialisation across percepts and observations, sequential deterministic faculty
    fan-out, per-observation rollback containment, binding snapshots across component refresh, cancellation, contained
    and logged faults, and no post-lifetime commit.
30. Transient tests verify transient observations apply attention atomically while producing no `ObservedAt` stamp,
    duplicate-history participation, timeline or notable-accumulation entry, session prompt history, or
    committed-observation notification.
31. Tests verify `RequiresFreshTurn` defaults to `false`, that freshness is evaluated exactly once during staged
    ingestion before commitment, and that rejected duplicates contribute neither importance nor freshness.
32. Tests verify exact self `ObservedSpeech` returns `false` while recognised-external and unknown speech return
    `true`, using ordinal comparison with the observing character's exact full ID, regardless of attention
    membership.
33. Tests verify fresh delivery is exact-once across threshold and fresh races, wait expiry races, and node exit;
    that node exit takes precedence over fresh invalidation and issues no replacement request; and that a disabled
    Mind retains urgency without waking, cancelling, or signalling the runner.
34. Tests verify each completed speech segment ingests as its own immutable `ObservedSpeech` carrying the percept's
    grouping metadata unchanged, that later segments never mutate, replace, or roll back earlier records, and that
    manual and AI speech remain ungrouped.
35. Tests verify a duplicate transport completion presenting an already-ingested commit identity is rejected before
    timestamping with no timeline, accumulation, freshness, or notification effect — covering speech's
    `(VoiceId, SpeechGroupID, SegmentIndex)` tuple supplied through the commit-identity contract — while
    observations supplying no identity, including ungrouped speech, keep their ordinary duplicate policy.
36. Tests verify start, resumed, and non-published terminal signals reach observing minds exactly once by their
    lifecycle key — source voice plus segment metadata for automatic cues, source voice plus the opaque synthetic
    token for the manual start cue — while creating no percept, faculty input, observation, timeline or accumulation
    entry, attention, wait, freshness, transcript, or notification effect. Start and resume forwarding is
    attention-gated once at cue receipt and source-generic: attended non-self characters hold, unattended, self,
    unattributable, and ambiguous sources never do, and later attention changes neither release nor retroactively
    create a hold. `Published` settles through ordinary completed-text delivery only, and the raw timeline remains
     unmerged while model-facing rendering projects grouped segments (AI-003).
37. Tests verify the commit-identity gate is generic: an arbitrary observation type supplying the contract's
    identity tuple receives exact-once enforcement, and Mind's generic ingestion — including duplicate suppression —
    contains no concrete observation-type branch or speech-specific check.
38. Tests verify AgenticMind orchestrates without concrete observation-record dependency — no casting, aliasing, or
    feature-payload inspection of concrete observation records in expectation matching, correlation, or watchdog
    scans — while the session-scoped speech coordinator (AI-002) owns that correlation and the TR-35 cue and TR-47
    routing contracts hold unchanged.

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
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [SPCH-008: Automatic Voice Detection](../../speech/008-automatic-voice-detection/index.md)
- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [CORE-010: Main-Thread Dispatcher](../../core/010-main-thread-dispatcher/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)

### Design Background

- [AI Context Management Memo](../../../docs/ai-context-management-memo.md)
