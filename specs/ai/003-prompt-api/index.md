---
id: AI-003
title: Prompt API
---

# Prompt API

## Requirement

Provide authorable static-instruction and current-scene-status contracts, the shared context-interpretation guidance
that static instruction must carry, and canonical, type-owned event rendering for each AgenticMind session.

## Goal

Keep static guidance stable while presenting fresh typed scene state and canonically delivered scene updates on
every logical provider request. The shared guidance must be sufficient for the model to interpret that context and to
act on information it has already been supplied.

## User Requirements

1. Authors can provide one stable system instruction and a separately authored, fresh current-scene status.
2. NPCs see current attended characters in current scene status without changing their fixed
   scenario.
3. Delivered scene events use the canonical text owned by each observation type. Unknown types receive safe fallback
   text that discloses only their `TypeKey`.
4. An NPC's prompt does not expose raw voice provenance or scheduling-only data as observation text.
5. The shared instruction gives each NPC character-neutral guidance sufficient to interpret its supplied context —
   its own retained exchanges, automatic scene updates, and the fresh current-scene status — without any tool call.
6. Shared guidance distinguishes acting on an available reply from waiting for a future one: an NPC is never told that
   waiting is necessary to receive context. World actions automatically make room for others; parts submitted together
   need no pause between them. Initiative, silence, and choosing quiet time remain valid in-character choices.
8. An NPC always sees the current game time in its current scene status, even when no character is attended. Delivered
   event ages state how long ago events happened when their scene update was composed, using the same game clock.
9. An NPC keeps its automatic lore context and receives a stable, grouped catalogue of remaining available lore, with
   guidance to retrieve relevant entries before acting without making retrieval mandatory.
10. Speech events may state whom the speaker addressed. Shared guidance teaches an NPC to use that wording — treating
    speech addressed to itself as its reply opportunity and speech between other characters as overheard context — and
    to infer the addressee from context when the wording makes no claim.

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
5. Initial current-scene projections include attended characters. Future projectors must use the same
   typed, immutable, stable-ID contract.
6. `CurrentSceneStatus` renders the request snapshot's current game time unconditionally, including when the attended
   list is empty, as an absolute game-clock value in invariant one-decimal (`F1`) form. Event ages use the same
   clock but refer to their own composition moment (TR-9). The scene-status capture is frozen with its request:
   exact transport retries keep it, and a rematerialised request captures afresh.
7. Static instruction rendering uses the session-fixed render context, including `ScenarioContext` and scenario. Fresh
   current-scene snapshots must not mutate, replace, or re-resolve either session-fixed value.

### Event Timeline Rendering

8. Event-history rendering is separate from both prompt stacks. It projects the selected persistent event timeline,
   invokes each observation's canonical renderer with owner context, and joins entries with exactly one newline for
   the tool result of AI-002's runtime-authored scene-update exchange (AI-002 TR-18).
9. Event text is owned by the concrete `Observation`, using AI-001's public framing method, type-owned body, safe
   `TypeKey`-only base fallback, and shared event-age suffix. Derive the age when composing the exchange from the
   original observation time and the current game time; omit the suffix if either is unavailable (AI-001 TR-12).
   The retained exchange keeps that composed text, including its age suffix, without re-ageing in later requests.
   No `event_history.md` asset, event-history parser, fragment catalogue, `EventHistoryPath`, or authored `TypeKey`
   dispatch exists.
10. Speech rendering remains actor-relative and must not render raw `VoiceId` or continuation transport metadata.
    `ContinuationProjection` remains responsible for speech-segment grouping, ordering, correlation, and latest-event
    placement for delivered observations: a projected speech group's segments join into one line at its latest
    position, and projected speech uses the same observation-owned rendering contract. Model-facing newly-presented
    marking is retired: no `[new]` prefix or watermark-derived marker exists (AI-002 TR-3).
11. Event rendering receives only the event records selected by its caller: AI-002's delivered-watermark rules for
    scene-update delivery (AI-002 TR-18, TR-19). Prompt stacks must not accept observations as general render-context
    values.

### Shared Context Interpretation Guidance

12. The authored shared instruction content — currently the `game/prompts/mind.md` file section of the static
    instruction — must carry character-neutral guidance covering how the scene reaches the NPC (its own retained
    exchanges and automatic scene updates), speech-addressee interpretation, current-scene interpretation,
    action selection, and lore discovery/retrieval. These are mandatory content; exact prose stays tunable. Guidance
    must not contradict AI-002's automatic scene-update delivery, payload-free wait semantics, or exchange retention
    (AI-002 TR-17–TR-22).
13. Scene-delivery guidance must match AI-002's delivery contract: the NPC's own prior tool calls and their results
    remain in its context as its record of what it said and did; scene updates it did not initiate arrive
    automatically as runtime-authored tool results; and the fresh current-scene message ends every request. Guidance
    must state honestly that scene updates arrive without any action by the NPC, and that neither a delivered event
    nor an own prior action means a conversational contribution has been answered or resolved. It must also cover
    addressee-annotated speech entries: wording may state that the speaker addressed the NPC or name other characters,
    and neutral entries make no addressee claim, leaving the addressee to be inferred from context.
14. Current-scene guidance must present the per-request status as a fresh, evidence-limited view rather than an
    exhaustive scene inventory: event ages reflect their composition moment, absent evidence does not establish
    absence, and historical events do not establish current positions. Scene status describes its request's captured
    moment rather than a live view (TR-6, TR-9).
15. Action-selection guidance must tell the NPC to consider available history, including relevant unresolved replies,
    before acting. Use chronology, the NPC's conversation record, and addressee wording to distinguish such replies
    from already-addressed speech; treat speech to others as overheard context rather than a reply opportunity.
    Explain that the runtime parks automatically after committed actions and allows same-contribution calls to run
    together before that pause. Explicit `wait(seconds)` chooses quiet time that may end early; it is never needed
    to receive context. [AI-002 TR-8][runtime-wait] and [TR-39][runtime-parking] are normative for these behaviours.
    Preserve retained `speak`/`wait` records, automatic scene updates, and fresh status as action context under
    AI-002 TR-17–TR-22. A provider request or wake is an opportunity, not a conversational turn or a command to act.
    Startup, empty history, repeated requests, wait expiry, or silence alone require neither speech nor repeated
    prompting. Initiative and silence remain valid according to character and scenario.
17. The shared NPC static stack preserves essential-world bodies followed by scene-character bodies, then renders
    AI-004's grouped catalogue of all remaining eligible observer/content-scoped entries. AI-004 normatively owns
    metadata, shared selection/exclusion, ordering, canonical-ID presentation, empty output, formatting, and
    isolation. These are session-start lore inputs, not a dynamic scene projection; no automatic location section
    or catalogue rebuild is required.
18. Shared lore guidance distinguishes automatically supplied bodies from catalogue listings, identifies canonical
    lore IDs — backticked `char:`/`loc:`/`world:` full IDs — as `read_lore` handles rather than subject `FullId`
    values, and encourages retrieving relevant entries before choosing an action. It teaches canonical referencing
    and presents IDs and tool names in backticks while teaching that `read_lore` argument values are exact bare
    strings (AI-004 UR-25/TR-51). Returned bodies are the character's available beliefs, not omniscient facts;
    unavailable results do not justify invention. Retrieval results reach subsequent reasoning through AI-002's
    retained exchanges, not observations. Do not require retrieval before every action or promise same-response
    reasoning over tool results.

## In Scope

- Two exported prompt stacks: static `SystemInstruction` and fresh `CurrentSceneStatus`.
- Typed, immutable direct-child scene-status projection and strict section wiring.
- Unconditional current game-time rendering in `CurrentSceneStatus`, on the game clock shared with event timestamps.
- Type-owned canonical event-timeline rendering, safe fallback, and shared timestamp framing.
- Session-fixed scenario context and fresh request-scene snapshot separation.
- Character-neutral shared context-interpretation guidance (scene delivery, speech-addressee interpretation,
  current scene, action selection, lore) in the authored static instruction.
- Session-start grouped lore catalogue after unchanged automatic lore bodies, under AI-004's normative contracts.

## Out Of Scope

- Expressions, planner agent behaviour, compaction, and broader perception changes.
- Prompt-editor preview tooling and exact final authored wording. This exclusion covers prose styling only: the
  guidance concepts required under Shared Context Interpretation Guidance are core delivery content and cannot be
  deferred by this section.
- Custom event-text templates and content-pack overrides. This deferral does not prohibit a future customisation layer.

## Acceptance Criteria

### User Requirements

1. Acceptance shows an NPC receives stable guidance alongside fresh attended-character status on each
   request without its scenario changing mid-session.
2. Acceptance shows event wording is supplied by each observation type, and an unknown future observation type has
   safe fallback wording that exposes no payload fields.
3. Acceptance shows speech text remains actor-relative without exposing raw voice provenance or continuation metadata.
4. Acceptance shows the shared instruction explains how the scene reaches the NPC — its own retained exchanges,
   automatic scene updates, and the fresh evidence-limited current-scene status — and never
   states that waiting is necessary to receive supplied context.
5. Acceptance shows guidance uses chronology and the NPC's conversation record to distinguish an available reply
   from a future one. It explains automatic parking, same-batch pacing, and deliberate `wait(seconds)` without
   teaching manual yielding or mandatory speech on waking. Character-driven initiative and silence remain valid
   (UR-6; TR-13–TR-15).
6. Acceptance shows current scene status always presents the current game time, including with an empty attended
   list, and delivered events show their age as of composition without changing in later retained context
   (UR-8; TR-6, TR-9).
7. Acceptance shows the shared instruction never contradicts the common game clock, the snapshot capture of the
    current scene status, or completed actions surfacing through the NPC's own retained exchanges, automatic scene
    updates, and current scene status.
8. Acceptance shows unchanged automatic lore context alongside a grouped catalogue of remaining available lore, and
   guidance to retrieve relevant entries before acting without requiring retrieval for every action (UR-9).
9. Acceptance shows shared guidance uses speech addressee wording to distinguish speech addressed to the NPC from
   speech overheard between others, and directs the NPC to infer the addressee from context when the wording makes no
   claim (UR-10).

### Technical Requirements

1. Tests verify static instruction compilation, validation, and rendering occur once, while `CurrentSceneStatus`
   compiles
   and validates at session start but renders once per logical request.
2. Tests verify direct-child projector stable IDs, typed immutable output, deterministic discovery, and clear failure
   for
   duplicate, missing, invalid, or wrongly typed wiring.
3. Tests verify `ProjectionPromptSection` binds only its declared typed root.
4. Tests verify initial attended-character projections and that fresh snapshots never mutate
   session-fixed
   `ScenarioContext` or scenario.
5. Tests verify event rendering invokes observation-owned canonical text, applies exactly one newline between entries,
   and has no standalone event-history asset or parser, `EventHistoryPath`, fragment catalogue, authored `TypeKey`
   dispatch.
6. Tests verify continuation projection preserves segment grouping, ordering, correlation, and latest-event placement,
   and carries no model-facing newly-presented marking or confirmed-watermark derivation (AI-002 TR-3), while
   projected speech uses canonical observation-owned text.
7. Tests verify the authored shared instruction contains guidance covering each required concept — how the scene
   reaches the NPC, speech-addressee wording, current-scene evidence limits and time framing, action selection,
   and lore retrieval. It teaches automatic parking, same-batch pacing, and deliberate waits without contradicting
   AI-002's automatic delivery, payload-free wait results, or exchange retention (TR-12–TR-15).
9. Tests verify scene status always renders absolute snapshot game time in invariant `F1` form, including with an
   empty attended list. Event rendering derives age from the original observation time and composition time on
   that clock, omits the suffix if either time is unavailable, and does not re-age a retained exchange when the
   current-scene snapshot advances (TR-6, TR-9; AI-001 TR-12).
10. Tests verify the current-time capture is frozen with its request — exact transport retries keep it while recovery
    and replacement recapture — and that the authored shared guidance does not contradict the common game clock, the
     snapshot capture, or completed actions surfacing through the NPC's own retained exchanges, automatic scene
     updates, and fresh scene status.
11. Tests verify the catalogue renders once at session start after unchanged automatic lore sections, obeys AI-004's
    shared-selection and presentation contracts, and is not rebuilt by fresh scene status or retrieval (TR-17).
12. Tests verify shared guidance distinguishes listings from bodies and canonical lore IDs from subject IDs,
    teaches canonical referencing with backticked IDs and tool names and bare `read_lore` argument values,
    encourages relevant retrieval for subsequent reasoning, treats results as observer beliefs, and promises
    neither mandatory retrieval, invented missing lore, observations, nor same-response reasoning over results
    (TR-18).

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)

[runtime-wait]: ../002-agent-runtime/index.md#scheduling-and-wait
[runtime-parking]: ../002-agent-runtime/index.md#decision-wait-gate-and-automatic-parking
