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
    the existing timeline, pending FIFO, scheduling, and interruption path.
37. AgenticMind must own only provider, prompt, render-context, and tool concerns. Incoming sensory interpretation must
    remain on Mind. The existing speech output tool and exactly-once self-action observation path must remain unchanged.

## In Scope

- Mind-owned node-lifetime observation timeline and pending importance queue.
- Contextual importance calculation, validation, and stored scheduling values.
- Unified external and tool-result observation ingestion.
- Threshold, maximum-wait, minimum-interval, disable, and active-turn interruption behaviour.
- Actor-relative observed speech, current-scene voice-ID attribution, and separately stored voice IDs.
- Synchronous percept interpretation, exact faculty dispatch, and Mind-owned attention under AI-006.
- Attention-filtered foreground contextual-subject selection.
- AgenticMind orchestration through AI-002 and AI-003.
- AgenticMind ownership and lifetime integration of AI-005 ContextWorker child nodes.
- Foreground-only render-context construction and success-only publication of the latest top-level read-only dictionary.
- Tool-only turn settlement without assistant-text completion or synthetic-marker observations.
- Irreversible node-lifetime shutdown of scheduling, provider requests, tools, and deferred action work.

## Out Of Scope

- Richer importance models beyond owning-character-relative speech and test observations.
- Additional production action tools beyond speech.
- Cancelling or reversing world actions already admitted before interruption.
- Timeline summarisation, compaction, token budgeting, or persistence beyond the Mind node lifetime.
- Automatic retry or backoff policy beyond existing failure containment.
- Multi-agent orchestration and long-term relationship state.
- Final tuning values for importance thresholds and wait durations.
- Perception-driven changes to eye presentation; unchanged eye presentation remains mandatory acceptance scope.

## Acceptance Criteria

1. An NPC records owning-character, recognised-other, and unknown speech in timeline order through one
   `ObservedSpeech : ObservedAction` contract with exact key `speech.observed`.
2. Rendered speech history uses actor-relative self, recognised-other, and unknown wording and never renders `VoiceId`
   as identity wording or treats it as authenticated provenance.
3. Tests verify every observation enters both timeline and pending FIFO, with importance calculated and validated
   exactly once before atomic mutation.
4. Tests verify self-speech stores importance `0`, external and unknown speech store effective importance `1`, and zero
   importance still processes through maximum wait.
5. Tests verify cumulative-importance threshold, maximum wait, the 0–5-second minimum-interval authoring range,
   disable/re-enable, pre-`_Ready()` intake, and atomic snapshots.
6. Tests verify one qualifying observation cancels an active invocation and starts exactly one fresh replacement after
   full settlement, with one minimum-interval bypass and no overlap.
7. Tests verify cumulative sub-threshold entries do not interrupt, multiple qualifying arrivals coalesce, FIFO order is
   retained, and committed events survive interruption.
8. Tests verify natural completion, cancellation, disable, and node-exit races neither duplicate a replacement nor emit
   erroneous failure diagnostics.
9. Tests verify Mind stamps tool-produced actors, prevents spoofing, atomically ingests ordered batches, and exposes no
   public observation recorder, sink, or timeline-only path.
10. A later turn's sole system instruction reflects the complete ordered timeline without carrying forward transient
    provider transcripts or observation-summary messages.
11. Missing configuration and genuine backend failures are logged and contained without crashing the scene.
12. After tree exit, tests verify no new intake, delayed action, replacement turn, timer, or node-service access occurs.
13. Tests verify `source.Id` is captured as `VoiceId` and compared ordinally with every current-scene character's
    composed `IVoice.Id`, without comparing voice object references.
14. Tests verify blank received and configured IDs do not match during attribution, zero matches remain unknown, one
    exact match yields the character's exact case-sensitive `Character.FullId`, and multiple exact matches fail clearly
    without choosing an actor.
15. Tests verify same-ID spoofing follows the configured attribution, while `ActorId` and nullable `VoiceId` remain
    separate and rendered speech history never exposes `VoiceId` as identity wording or authenticated provenance.
16. Tests verify AI-002's `end_turn` marker never enters Mind, invalid output causes no model repair or automatic retry,
    and observations from actions committed before a later turn failure remain in timeline order.
17. Acceptance verifies both the user-visible bounded, privacy-safe, non-overlapping behaviour and the importance,
    ingestion, speaker-attribution, scheduling, interruption, cancellation, and lifetime contracts.
18. Tests verify ContextWorker child-node ownership and foreground-only `CreateRenderContext` assembly. AgenticMind
    starts with an empty top-level read-only latest dictionary and retains authored workers only for deterministic
    projection aggregation, not trigger notification.
19. Tests verify general typed C# events are published only after observation commitment and genuinely successful
    foreground completion, never for contained failures or cancellations. Relevant triggers subscribe and unsubscribe
    directly without delaying foreground turns; AI-005 remains normative for trigger, projection, and lifetime
    contracts.
20. Tests verify the foreground template renders with AgenticMind's exact complete top-level read-only dictionary from
    `CreateRenderContext`. AgenticMind publishes that exact dictionary atomically only after successful rendering and
    retains the previous published dictionary after construction or rendering failure.
21. Tests verify workers capture only the currently published snapshot and never initiate context construction,
    aggregation, or timeline refresh. A worker projection reaches workers only through a subsequent successfully
    rendered and published foreground context.
22. Tests verify Mind subscribes to configured senses, requires one exact faculty per declared exact percept type before
    activation, and synchronously dispatches immutable percepts without inheritance fallback, queues, or background
    processing.
23. Tests verify complete `PerceptionResult` and calculated-importance validation occurs before mutation, duplicate
    attention effects apply sequentially in order, and ordered observations use the existing atomic ingestion and
    scheduling path.
24. Tests verify foreground context contains self plus all currently resolvable attention-eligible `IContextual`
    subjects, with no unconditional all-scene-character inclusion, second visual scan, hidden subject cache, or Mind
    state passed to `IContextual.GetContext`.
25. Tests verify Mind, not AgenticMind, owns incoming interpretation; AgenticMind retains provider, prompt, render, and
    tool concerns; and successful speech output still creates exactly one self-action observation.

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
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [BODY-006: Voice Component](../../body/006-voice/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)

### Design Background

- [AI Context Management Memo](../../../docs/ai-context-management-memo.md)
