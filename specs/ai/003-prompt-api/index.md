---
id: AI-003
title: Prompt API
---

# Prompt API

## Requirement

Provide authorable static-instruction and current-scene-status contracts plus canonical, type-owned event rendering for
each AgenticMind session.

## Goal

Keep static guidance stable while presenting fresh typed scene state and causally watermarked event history on every
logical provider request.

## User Requirements

1. Authors can provide one stable system instruction and a separately authored, fresh current-scene status.
2. NPCs see current attended characters and active watches in current scene status without changing their fixed
   scenario.
3. Event history uses the canonical text owned by each observation type. Unknown types receive safe fallback text that
   discloses only their `TypeKey`.
4. An NPC's prompt does not expose raw voice provenance or scheduling-only data as observation text.

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
6. Static instruction rendering uses the session-fixed render context, including `ScenarioContext` and scenario. Fresh
   current-scene snapshots must not mutate, replace, or re-resolve either session-fixed value.

### Event Timeline Rendering

7. Event-history rendering is separate from both prompt stacks. It projects the selected persistent event timeline,
   invokes each observation's canonical renderer with owner context, and joins entries with exactly one newline for
   AI-002's per-request timeline message and the `history` tool.
8. Event text is owned by the concrete `Observation`, using AI-001's public framing method, type-owned body, safe
   `TypeKey`-only base fallback, and shared timestamp suffix. No `event_history.md` asset, event-history parser,
   fragment catalogue, `EventHistoryPath`, or authored `TypeKey` dispatch exists.
9. Speech rendering remains actor-relative and must not render raw `VoiceId` or continuation transport metadata.
   `ContinuationProjection` remains responsible only for speech-segment grouping, ordering, correlation, and
   latest-event placement; projected speech uses the same observation-owned rendering contract.
10. Event rendering receives only the event records selected by its caller: AI-002's watermark rules for the canonical
    per-request timeline message, or `history`'s selected persistent-timeline snapshot for on-demand recall. Prompt
    stacks must not accept observations as general render-context values.
11. `history` is a read-only query of the persistent event timeline. Calling it must neither ingest nor enqueue an
    observation, nor advance any request-context or timeline watermark. Automatic event text reaches the model only
    through AI-002's canonical per-request timeline message; `history` is the intentional explicit-recall exception.

## In Scope

- Two exported prompt stacks: static `SystemInstruction` and fresh `CurrentSceneStatus`.
- Typed, immutable direct-child scene-status projection and strict section wiring.
- Type-owned canonical event-timeline rendering, safe fallback, and shared timestamp framing.
- Session-fixed scenario context and fresh request-scene snapshot separation.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, compaction, and broader perception changes.
- Prompt-editor preview tooling and final authored prose tuning.
- Custom event-text templates and content-pack overrides. This deferral does not prohibit a future customisation layer.

## Acceptance Criteria

### User Requirements

1. Acceptance shows an NPC receives stable guidance alongside fresh attended-character and active-watch status on each
   request without its scenario changing mid-session.
2. Acceptance shows event wording is supplied by each observation type, and an unknown future observation type has
   safe fallback wording that exposes no payload fields.
3. Acceptance shows speech text remains actor-relative without exposing raw voice provenance or continuation metadata.

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
7. Tests verify `history` reads its selected persistent-timeline snapshot without ingesting or enqueuing an observation
   or advancing a request-context or timeline watermark. They also verify automatic event text reaches the model only
   through AI-002's canonical per-request timeline message, with `history` as the explicit-recall exception.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
