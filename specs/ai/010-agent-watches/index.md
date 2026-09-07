---
id: AI-010
title: Agent Watches
---

# Agent Watches

## Requirement

AgenticMind must let an NPC author and persist bounded typed condition watches. Each condition owns evidence filtering,
state transitions, and the ordinary Mind observations emitted for meaningful transitions.

## Goal

Let an NPC ask Mind to notice a future condition without polling through provider requests, while keeping watch
lifetime, identity, and transition semantics explicit. Model-facing watch guidance must describe this contract
accurately so the NPC neither re-arms needlessly nor mistakes a registration for current condition truth.

## User Requirements

1. An NPC can arm a proximity watch for an exact character and later explicitly remove it.
2. Arming a watch immediately reports its ID and current evidence without pretending that the condition has activated.
    `WatchId` appears only in arm feedback, active-watch status, and `unwatch`; it is absent from durable event text and
    payloads.
3. Active-watch current-scene status is a generic process list for remembering and managing active watches. Each entry
   exposes its `WatchId`, condition ID, and subject ID, but not state or condition-specific evidence. An empty list
   contributes no active-watch section or fallback.
4. Entering and leaving the watched proximity are memorable typed transitions; remaining in the same state and
   unavailable evidence are quiet.
5. A user can have at most 32 active watches per Mind, and removed IDs are never reused during that Mind lifetime.
6. An NPC's model-facing watch guidance presents arming accurately: registration is immediate and returns a watch ID
   with currently available evidence, monitoring persists without re-arming, and listed watches are registrations to
   manage rather than assertions of current condition truth.
7. Watch guidance states that transitions arrive as ordinary remembered events, that watch evidence is limited by
   current perception, and that explicit removal uses the returned or listed watch ID.

## Technical Requirements

### Authoring And Lifecycle

1. Watch conditions are authorable typed Resources owned by a WatchRegistry's `Conditions` collection. Each condition
   creates exactly one watch function and owns evidence filtering, runtime state, transition evaluation, and optional
   concrete `Observation` construction. The watch framework does not hard-code condition-specific matching or emitted
   observation types.
2. AgenticMind discovers zero or one direct-child WatchRegistry. When a watch is authored, its registry is the sole
   direct Mind child for watch authoring; condition Resources are registry-owned, not Mind children. The registry
   validates and binds its conditions only at AgenticMind composition, never through a common runtime service bag.
3. Each WatchRegistry maintains runtime state only for its bound Mind session. Condition Resources remain authored
   definitions, while each armed watch has isolated runtime state.
4. Watches persist until explicit `unwatch`, Mind teardown, or another condition-defined terminal rule. A Mind has a
   hard
   cap of 32 active watches. IDs are `w1`, `w2`, and so on, monotonically allocated and never reused for that Mind
   lifetime.
5. `unwatch` accepts an ID. An unknown ID is not an error: it returns a clear message and creates no observation.

### Proximity Watch

6. `watch_proximity(subject_id, maximum_distance)` accepts an exact canonical character ID and a finite, non-negative,
   inclusive maximum distance. Invalid IDs or distances fail validation before a watch is armed.
7. Arming is non-blocking and returns the allocated watch ID plus current evidence. It creates no activation event.
8. Proximity evaluation consumes only focus-limited, non-expired `ObservedRelativePosition` evidence for the exact
    subject. It does not force perception, scan the scene, retain expired evidence, or infer a position from another
    subject. Raw source-position evidence is retained only in this condition's evaluation.
9. Its state machine is `Unknown`, `Outside`, and `Inside`: no evidence gives `Unknown`; `Unknown -> Outside` is silent;
   `Unknown -> Inside` and `Outside -> Inside` emit `entered`; `Inside -> Outside` emits `left`; same-state transitions
   are silent; expiry or loss of evidence changes to `Unknown` without an event.

### Outcomes And Scheduling

10. A condition transition that emits an observation supplies its own concrete type, semantics, importance, and
    immediate-reconsideration decision. Mind accepts that ordinary observation through its normal source-neutral queue;
    no generic watch outcome or evidence envelope exists.
11. The proximity condition emits a dedicated typed proximity-transition observation with `entered` or `left` semantics.
    It contains no `WatchId`; its canonical text contains no watch-management identity. It is event-eligible and
    `NeverExpire` under AI-001, and participates in normal scheduling and the persistent event timeline.
12. The registry forwards retained-observation changes and snapshots to each runtime, then enqueues its optional
    ordinary observation. It must not filter or dispatch specifically for relative-position observations.
13. Through AI-003 current-scene status, active watches are exposed as a generic process list for remembering and
    managing active watches. Each entry exposes its `WatchId`, condition ID, and subject ID, but no state or
    condition-specific evidence. An empty list contributes no active-watch section or fallback. This status is
    independent of event creation and does not create an observation.

### Model-Facing Watch Guidance

14. Watch-tool descriptions must accurately describe the lifecycle: arming is non-blocking registration that returns
    the allocated watch ID plus currently available evidence and creates no activation event (TR-7); monitoring
    persists until explicit removal or a condition-defined terminal rule (TR-4); listed active watches are
    registrations, not condition truth (TR-13); and removal takes the opaque ID returned at arming or listed in
    current-scene status (TR-5). Descriptions must not promise an activation event or suggest that re-arming is
    needed to keep monitoring.
15. Watch-tool descriptions must state that transitions arrive as ordinary remembered events through the per-request
    event timeline (TR-10, TR-11), never as wait-result text (AI-002 TR-8/TR-10), and that available evidence is
    limited by current perception (TR-8 for proximity). Shared-instruction watch concepts follow AI-003 TR-16.

## In Scope

- One direct-child WatchRegistry per authored Mind, with registry-owned typed condition Resources and one function per
  condition.
- Per-Mind-session isolation of registry and armed-watch runtime state.
- Persistent, explicitly removed watches with bounded non-reused IDs.
- `watch_proximity`, its condition-owned evidence filtering, transition table, dedicated observation, and generic
  active-watch process-list status.
- Source-neutral Mind ingestion and normal scheduling of condition-owned ordinary observations.
- Model-facing watch guidance covering registration, persistence, evidence limits, transition delivery, and removal.
- Extensibility for future typed condition Resources without a watch-engine concrete-type catalogue.

## Out Of Scope

- Expressions.
- Additional watch types.
- Planner agent behaviour.
- Compaction.
- Broader perception changes.

## Acceptance Criteria

### User Requirements

1. Acceptance shows an NPC can arm a proximity watch, receive an ID and current evidence immediately, and later remove
   it.
2. Acceptance shows entry and exit are remembered events, while an unchanged state, unknown evidence, and evidence
   expiry
   are silent.
3. Acceptance shows a Mind rejects a 33rd active watch and never reuses an ID after removal.
4. Acceptance shows a `WatchId` is available for arm feedback, the active-watch process list, and removal, but never
   appears in a remembered transition.
5. Acceptance shows each active-watch process-list entry exposes only its `WatchId`, condition ID, and subject ID;
   it exposes neither state nor condition-specific evidence, and an empty list adds no active-watch section or
   fallback.
6. Acceptance shows watch guidance presents arming as immediate registration with current evidence, persistence
   without re-arming, listed watches as registrations rather than condition truth, transitions as ordinary remembered
   events, and removal by the returned or listed watch ID.

### Technical Requirements

1. Tests verify typed condition Resources are owned by the direct-child WatchRegistry's `Conditions` collection, not
   authored as Mind children; each condition creates one function; and AgenticMind permits no more than one registry.
2. Tests verify composition-only registry validation and binding, no common-runtime feature service bag, and isolated
   registry and armed-watch runtime state for each Mind session.
3. Tests verify `watch_proximity` exact-ID, finite non-negative inclusive-distance validation; non-blocking arming; and
   focus-limited non-expired relative-position evidence only.
4. Tests verify every `Unknown`, `Outside`, and `Inside` transition, including silent `Unknown -> Outside` and silent
   expiry to `Unknown`.
5. Tests verify condition-owned evidence filtering, state, transition semantics, concrete observation construction,
   importance, and immediate reconsideration. They verify the registry forwards changes and snapshots without
   relative-position-specific filtering or dispatch.
6. Tests verify proximity emits its dedicated typed `entered` or `left` observation, which is persistent, `NeverExpire`,
   event-eligible, and scheduled through Mind's normal source-neutral queue.
7. Tests verify emitted proximity observations and rendered event history contain no `WatchId`, while arm feedback,
   active-watch status, and `unwatch` retain it.
8. Tests verify 32-watch capacity, monotonic non-reused `wN` IDs, and unknown `unwatch` returning a clear message with
   no observation.
9. Tests verify active-watch status renders a generic process-list entry with only `WatchId`, condition ID, and
   subject ID, independently of transition-observation creation; it renders no state or condition-specific evidence,
   and an empty list contributes no active-watch section or fallback.
10. Tests verify watch-tool descriptions match the lifecycle contracts: non-blocking arming with current evidence and
    no activation-event promise, persistent monitoring without re-arming, perception-limited evidence, transition
    delivery through ordinary event history, and removal by opaque watch ID.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
