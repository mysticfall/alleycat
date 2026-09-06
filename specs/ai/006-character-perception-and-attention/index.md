---
id: AI-006
title: Percept-Based Sensing And Attention
---

# Percept-Based Sensing And Attention

## Requirement

NPC senses must publish immutable percepts for Mind-owned interpretation, attention updates, and policy-governed
observation acceptance without coupling sensory producers to Mind.

## Goal

Preserve immediate, ordered perception while distinguishing attention-only presence, retained scene evidence, and
event-eligible observations.

## User Requirements

1. NPCs notice eligible speech and visual subjects without routine visual presence becoming memory or event history.
2. A focused subject's description is available as current scene state. The attended-character projection may retain and
   render relevant non-expired relative-position evidence, including relative position or distance and its timestamp;
   condition-owned proximity watch evaluation may consume the same retained evidence. Unchanged information does not
   repeatedly flood the NPC.
3. Material relative-position changes can support stable focus and hysteresis while stale evidence disappears after its
   evidence window.
4. Sensing and semantic interpretation do not select gaze or alter eye presentation.
5. Invalid sensory or faculty configuration fails before sensory processing begins.

## Technical Requirements

1. Senses publish immutable, behaviour-free percepts through a synchronous non-generic event bridge and declare the
   exact
   concrete types they can publish. Mind validates publisher declarations synchronously and queues work without blocking
   publishers.
2. Mind discovers `IPerception` direct-child faculties in scene order. It validates declared percept types, snapshots
   assignability-compatible bindings at publication, serialises interpretation in enqueue order, and contains a failed
   observation without rolling back earlier or later accepted work.
3. Visual presence remains an attention-only outcome initially. It refreshes attention but creates no retained-log
   entry,
   event-timeline record, scheduling pressure, or model-facing event text.
4. Non-self completed speech produces `ObservedSpeech`; self filtering, speaker attribution, and speech lifecycle remain
   as specified by the speech contracts. Its initial AI-001 policy is `NeverExpire` and event-eligible.
5. A valid focused visual description produces `ObservedVisualDescription`. Its AI-001 policy suppresses equal values
   and
   supersedes changed values; it is normally current-scene input and event-ineligible.
6. `RelativePositionPerception` emits focus-limited `ObservedRelativePosition` evidence only while its subject is active
   and the evidence is non-expired. Its policy suppresses equal values, supersedes changed values, and retains changed
   evidence for a finite window adequate for hysteresis. The [AI-003](../003-prompt-api/index.md) attended-character
   projection may retain and render relevant position or distance evidence and its timestamp; condition-owned proximity
   watch evaluation may consume that retained evidence. It is event-ineligible. Raw relative-position evidence must not
   be wrapped in a generic watch outcome or evidence payload.
7. Relative-position equality, material-change thresholds, finite evidence duration, and direction thresholds are
   tunable
   policy or faculty details. They must be finite and validated before use; their precise values are not normative.
8. Each accepted observation applies attention and Mind acceptance atomically. AI-001 owns retention, timestamps,
   scheduler metadata, event eligibility, expiry, and persistent timeline append.
9. Attention remains an immutable snapshot keyed by canonical character ID. It is the source for attended-character
   scene status, which may retain and render relevant non-expired position or distance evidence and its timestamp, and
   for condition-owned watch evaluation; it does not itself create event history.
   [AI-010](../010-agent-watches/index.md) owns active-watch current-scene status only: a generic management/process
   list containing `WatchId`, condition ID, and subject ID, with no state or condition-specific evidence. An empty
   active-watch list emits no section or fallback.
10. Sensing, faculties, and attention mutation must not call look-target assignment APIs. AI-007 remains the separately
    composed gaze consumer.

## In Scope

- Immutable percept publication, direct-child faculty discovery, ordered interpretation, and contained failure handling.
- Attention-only visual presence, focused descriptions, and focus-limited relative-position evidence.
- Policy-directed suppression, supersession, finite evidence, and event eligibility through AI-001.
- Attention snapshots used by [AI-003](../003-prompt-api/index.md) attended-character current-scene status and
  [AI-010](../010-agent-watches/index.md) condition-owned watch evaluation.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, compaction, and broader perception changes.
- Gaze selection, direct look-target assignment, and final perception or hysteresis tuning values.

## Acceptance Criteria

### User Requirements

1. Acceptance shows visual surveys update attention without creating memory or event history.
2. Acceptance shows a focused subject's unchanged description and position do not repeatedly reach the NPC, while
   changed description state and relevant retained position or distance evidence with its timestamp may appear in
   attended-character current-scene status, and condition-owned proximity watch evaluation may consume that evidence.
3. Acceptance shows position evidence is unavailable after expiry or loss of focus and does not become an expiry event.
4. Acceptance shows sensing leaves gaze and eye presentation unchanged.
5. Acceptance shows active-watch status is a generic list containing only `WatchId`, condition ID, and subject ID,
   with no state or condition-specific evidence; an empty active-watch list emits no section or fallback.

### Technical Requirements

1. Tests verify immutable exact-type percept publication, non-blocking intake, direct-child ordered faculty discovery,
   binding snapshots, serial interpretation, and per-observation fault containment.
2. Tests verify visual presence is attention-only and has none of the retained-log, timeline, scheduler, or
   rendered-event
   effects.
3. Tests verify the initial description and relative-position policy outcomes: equal suppression, changed supersession,
   finite non-expired position evidence, ordinary event ineligibility, attended-character current-scene availability of
   relevant position or distance evidence and its timestamp, and condition-owned proximity watch-evaluation
   availability without a generic watch evidence or outcome payload. They verify [AI-010](../010-agent-watches/index.md)
   active-watch status contains only `WatchId`, condition ID, and subject ID, with no state or condition-specific
   evidence, and that an empty list emits no section or fallback.
4. Tests verify relative-position evidence is focus-limited and that configuration validation rejects non-finite values.
5. Tests verify attention snapshots are immutable and no sensing or attention path assigns a look target.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
