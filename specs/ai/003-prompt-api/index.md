---
id: AI-003
title: Prompt API
---

# Prompt API

## Requirement

Provide authorable static-instruction and current-scene-status contracts, the shared context-interpretation guidance
that static instruction must carry, and canonical, type-owned event rendering for each AgenticMind session.

## Goal

Keep static guidance stable while presenting fresh typed scene state and causally watermarked event history on every
logical provider request. The shared guidance must be sufficient for the model to interpret that context and to act on
information it has already been supplied.

## User Requirements

1. Authors can provide one stable system instruction and a separately authored, fresh current-scene status.
2. NPCs see current attended characters and active watches in current scene status without changing their fixed
   scenario.
3. Event history uses the canonical text owned by each observation type. Unknown types receive safe fallback text that
   discloses only their `TypeKey`.
4. An NPC's prompt does not expose raw voice provenance or scheduling-only data as observation text.
5. The shared instruction gives each NPC character-neutral guidance sufficient to interpret its supplied context —
   established versus newly presented event history and the fresh current-scene status — without any tool call.
6. Shared guidance distinguishes acting on an available reply from waiting for a future one: an NPC is never told that
   waiting is necessary to receive context, while silence and waiting remain legitimate in-character choices.
7. Shared watch guidance refers to available watch tools rather than a fixed tool set and presents listed watches as
   monitoring registrations, not assertions that a condition is currently true.
8. An NPC always sees the current game time in its current scene status — including when no character is attended — on
    the same clock as its event-history timestamps.
9. An NPC keeps its automatic lore context and receives a stable, grouped catalogue of remaining available lore, with
   guidance to retrieve relevant entries before acting without making retrieval mandatory.

## Technical Requirements

### Static Instruction And Scene Status

1. `SystemInstruction` remains an exported `PromptStack`, compiled, validated, and rendered once at session start. It is
   the sole static system instruction and contains no dynamic scene or observation content.
2. `CurrentSceneStatus` is a second exported `PromptStack`. It is compiled and validated at session start, then rendered
   freshly for each logical provider request.
3. Mind discovers `ISceneStatusProjector` direct children in scene order. Each projector has a stable identifier and
   produces a typed immutable projection from current Mind state. Duplicate IDs, missing required projectors, invalid
   projection data, or invalid stack wiring fail session start clearly.
4. `ProjectionPromptSection` binds an explicitly typed projector root. It must reject an unavailable, wrongly typed, or
   otherwise invalid root rather than silently rendering unrelated data.
5. Initial current-scene projections include attended characters and active watches. Future projectors must use the same
   typed, immutable, stable-ID contract.
6. `CurrentSceneStatus` renders the request snapshot's current game time unconditionally, including when the attended
   list is empty. The rendered time uses the game clock's invariant one-decimal (`F1`) form — the same clock that
   event-timestamp suffixes use (TR-9) — while rendered events keep their original observation times. The capture is
   frozen with its request: exact transport retries keep it, and a rematerialised request captures afresh.
7. Static instruction rendering uses the session-fixed render context, including `ScenarioContext` and scenario. Fresh
   current-scene snapshots must not mutate, replace, or re-resolve either session-fixed value.

### Event Timeline Rendering

8. Event-history rendering is separate from both prompt stacks. It projects the selected persistent event timeline,
   invokes each observation's canonical renderer with owner context, and joins entries with exactly one newline for
   the sections of AI-002's per-request timeline message.
9. Event text is owned by the concrete `Observation`, using AI-001's public framing method, type-owned body, safe
   `TypeKey`-only base fallback, and shared timestamp suffix. No `event_history.md` asset, event-history parser,
   fragment catalogue, `EventHistoryPath`, or authored `TypeKey` dispatch exists.
10. Speech rendering remains actor-relative and must not render raw `VoiceId` or continuation transport metadata.
    `ContinuationProjection` remains responsible only for speech-segment grouping, ordering, correlation, and
    latest-event placement; projected speech uses the same observation-owned rendering contract.
11. Event rendering receives only the event records selected by its caller: AI-002's watermark rules for the canonical
    per-request timeline message. Prompt stacks must not accept observations as general render-context values.

### Shared Context Interpretation Guidance

12. The authored shared instruction content — currently the `game/prompts/mind.md` file section of the static
    instruction — must carry character-neutral guidance covering event-history interpretation,
    current-scene interpretation, action selection, available watch tools, and lore discovery/retrieval. These are
    mandatory content; exact prose stays tunable. Guidance must not contradict AI-002's automatic per-request delivery,
    payload-free wait semantics, or tool-exchange disposal (AI-002 TR-17–TR-22).
13. Event-history guidance must match AI-002's timeline-message contract: the `Established Event History` section
    holds prior context, the `New Since Your Previous Response` section marks entries presented since the NPC's
    previous valid response, and neither section alone means a conversational contribution has been answered or
    resolved.
14. Current-scene guidance must present the per-request status as a fresh, evidence-limited view rather than an
    exhaustive scene inventory: observation timestamps bound evidence freshness, absent evidence does not establish
    absence, and historical events do not establish current positions. It must not contradict the common game clock or
    the status's snapshot semantics (TR-6): the current time and every event timestamp read one clock, and the status
    describes its request's captured moment rather than a live view.
15. Action-selection guidance must direct the NPC to consider relevant available history — including an available
    reply — before choosing an action, and to treat `wait` as intentionally yielding to future developments or
    remaining silent, never as a precondition for receiving context (AI-002 UR-3/TR-8). It must preserve the NPC's
    freedom to act, speak, or stay silent according to character and scenario. It must not contradict tool-exchange
    disposal (AI-002 TR-17–TR-22): disposed `speak`/`wait` exchanges are never model-visible. Completed actions surface
    through remembered events and fresh scene status; non-opted exchanges, including lore retrieval, remain retained.
16. Watch guidance must refer to available watch tools because authored composition varies (AI-010 TR-2). It must
    describe them as persistent monitoring registration rather than condition truth, state that watch transitions
    arrive through ordinary event history (AI-010 TR-10/TR-11), and identify the listed or returned watch ID as the
    removal handle (AI-010 TR-5).
17. The shared NPC static stack preserves essential-world bodies followed by scene-character bodies, then renders
    AI-004's grouped catalogue of all remaining eligible observer/content-scoped entries. AI-004 normatively owns
    metadata, shared selection/exclusion, ordering, empty output, formatting, and isolation. These are session-start
    lore inputs, not a dynamic scene projection; no automatic location section or catalogue rebuild is required.
18. Shared lore guidance distinguishes automatically supplied bodies from catalogue listings, identifies exact entry IDs
    as `read_lore` handles rather than subject `FullId` values, and encourages retrieving relevant entries before
    choosing an action. Returned bodies are the character's available beliefs, not omniscient facts; unavailable results
    do not justify invention. Retrieval results reach subsequent reasoning through AI-002's retained exchanges, not
    observations. Do not require retrieval before every action or promise same-response reasoning over tool results.

## In Scope

- Two exported prompt stacks: static `SystemInstruction` and fresh `CurrentSceneStatus`.
- Typed, immutable direct-child scene-status projection and strict section wiring.
- Unconditional current game-time rendering in `CurrentSceneStatus`, on the game clock shared with event timestamps.
- Type-owned canonical event-timeline rendering, safe fallback, and shared timestamp framing.
- Session-fixed scenario context and fresh request-scene snapshot separation.
- Character-neutral shared context-interpretation guidance (history, current scene, action selection, watches, lore)
  in the authored static instruction.
- Session-start grouped lore catalogue after unchanged automatic lore bodies, under AI-004's normative contracts.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, compaction, and broader perception changes.
- Prompt-editor preview tooling and exact final authored wording. This exclusion covers prose styling only: the
  guidance concepts required under Shared Context Interpretation Guidance are core delivery content and cannot be
  deferred by this section.
- Custom event-text templates and content-pack overrides. This deferral does not prohibit a future customisation layer.

## Acceptance Criteria

### User Requirements

1. Acceptance shows an NPC receives stable guidance alongside fresh attended-character and active-watch status on each
   request without its scenario changing mid-session.
2. Acceptance shows event wording is supplied by each observation type, and an unknown future observation type has
   safe fallback wording that exposes no payload fields.
3. Acceptance shows speech text remains actor-relative without exposing raw voice provenance or continuation metadata.
4. Acceptance shows the shared instruction explains established versus newly presented history, the fresh
   evidence-limited current-scene status, and watch registration, and never states that waiting is necessary to receive
   supplied context.
5. Acceptance shows action-selection guidance distinguishes engaging with an available reply from waiting for a future
   one while keeping silence and waiting legitimate in-character choices.
6. Acceptance shows the current scene status always presents the current game time — including with an empty attended
   list — on the same clock as event timestamps, while event entries keep their original observation times.
7. Acceptance shows the shared instruction never contradicts the common game clock, the snapshot capture of the
    current scene status, or completed actions surfacing through event history and current scene status.
8. Acceptance shows unchanged automatic lore context alongside a grouped catalogue of remaining available lore, and
   guidance to retrieve relevant entries before acting without requiring retrieval for every action (UR-9).

### Technical Requirements

1. Tests verify static instruction compilation, validation, and rendering occur once, while `CurrentSceneStatus`
   compiles
   and validates at session start but renders once per logical request.
2. Tests verify direct-child projector stable IDs, typed immutable output, deterministic discovery, and clear failure
   for
   duplicate, missing, invalid, or wrongly typed wiring.
3. Tests verify `ProjectionPromptSection` binds only its declared typed root.
4. Tests verify initial attended-character and active-watch projections and that fresh snapshots never mutate
   session-fixed
   `ScenarioContext` or scenario.
5. Tests verify event rendering invokes observation-owned canonical text, applies exactly one newline between entries,
   and has no standalone event-history asset or parser, `EventHistoryPath`, fragment catalogue, authored `TypeKey`
   dispatch, or `ObservedWatchOutcome` requirement.
6. Tests verify continuation projection preserves segment grouping, ordering, correlation, and latest-event placement
   while projected speech uses canonical observation-owned text.
7. Tests verify the authored shared instruction contains guidance covering each required concept — history
    interpretation, current-scene evidence limits and timestamps, action selection, watches, and lore retrieval — and
    stays consistent with AI-002's automatic per-request delivery, payload-free wait semantics, and disposal.
8. Tests verify shared watch guidance is composition-neutral, referring to available watch tools and presenting listed
   watches as registrations rather than condition truth, and that it routes watch transitions through ordinary event
   history.
9. Tests verify the scene status renders its snapshot's current game time unconditionally — including with an empty
   attended list — in the invariant one-decimal form on the same clock as event-timestamp suffixes, and that rendered
   events keep their original observation times.
10. Tests verify the current-time capture is frozen with its request — exact transport retries keep it while recovery
    and replacement recapture — and that the authored shared guidance does not contradict the common game clock, the
     snapshot capture, or completed actions surfacing only through remembered events and fresh scene status.
11. Tests verify the catalogue renders once at session start after unchanged automatic lore sections, obeys AI-004's
    shared-selection and presentation contracts, and is not rebuilt by fresh scene status or retrieval (TR-17).
12. Tests verify shared guidance distinguishes listings from bodies and entry IDs from subject IDs, encourages relevant
    retrieval for subsequent reasoning, treats results as observer beliefs, and promises neither mandatory retrieval,
    invented missing lore, observations, nor same-response reasoning over results (TR-18).

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
