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
   render relevant non-expired relative-position evidence, including relative position or distance and its timestamp.
   Unchanged information does not repeatedly flood the NPC.
3. Material relative-position changes can support stable focus and hysteresis while stale evidence disappears after its
   evidence window.
4. Sensing and semantic interpretation do not select gaze or alter eye presentation.
5. Invalid sensory or faculty configuration fails before sensory processing begins.
6. In scenes with multiple characters, each hearing NPC interprets completed speech through the speaker's explicitly
   declared recipients: addressed to it when it is a declared recipient, addressed to other characters when the
   declaration names others, and undecided when the speaker declared none. Player speech declares no recipients and
   stays undecided, and every hearing NPC still perceives speech regardless of whom it addresses.

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
4. Non-self completed speech produces `ObservedSpeech`; self filtering, speaker attribution, and speech lifecycle
   remain as specified by the speech contracts. Speech interpretation carries the speaker's explicitly declared
   recipients from the percept into the semantic addressee set on `ObservedSpeech` (AI-001 TR-13, TR-16), defensively
   deduplicated in declaration order. Unrecognised-speaker speech is always undecided because declared recipients
   cannot be attributed without a recognised character. Its initial AI-001 policy is `NeverExpire` and event-eligible.
5. A valid focused visual description produces `ObservedVisualDescription`. Its AI-001 policy suppresses equal values
   and
   supersedes changed values; it is normally current-scene input and event-ineligible.
6. `RelativePositionPerception` emits focus-limited `ObservedRelativePosition` evidence only while its subject is active
   and the evidence is non-expired. Its policy suppresses equal values, supersedes changed values, and retains changed
   evidence for a finite window adequate for hysteresis. The [AI-003](../003-prompt-api/index.md) attended-character
   projection may retain and render relevant position or distance evidence and its timestamp. This evidence is
   event-ineligible.
7. Relative-position equality, material-change thresholds, finite evidence duration, and direction thresholds are
   tunable
   policy or faculty details. They must be finite and validated before use; their precise values are not normative.
8. Each accepted observation applies attention and Mind acceptance atomically. AI-001 owns retention, timestamps,
   scheduler metadata, event eligibility, expiry, and persistent timeline append.
9. Attention remains an immutable snapshot keyed by canonical character ID. It is the source for attended-character
   scene status, which may retain and render relevant non-expired position or distance evidence and its timestamp.
   Attention does not itself create event history.
10. Sensing, faculties, and attention mutation must not call look-target assignment APIs. AI-007 remains the separately
    composed gaze consumer.
11. Declared recipients are declarations, never a hearing filter: every listener in hearing reach receives the speech
    and its recipient metadata regardless of whom it addresses ([SPCH-006](../../speech/006-hearing/index.md) bounds
    reach). Perception performs no addressee inference of its own — no orientation, facing, vocative (name-mention),
    or model-based attribution exists on the perception path — and it must not assign look targets or alter eye
    presentation (TR-10) or make any model request.
12. Perception stamps recognised player-origin speech with trusted internal player provenance, resolved from scene
    membership — the scene context's player group — never from model-supplied data. Player speech never carries
    declared recipients: its addressee set stays blank (undecided), while the private provenance drives AI-001 TR-14's
    temporary fresh-turn rule. The flag never appears in a public payload or any model-visible rendering.

## In Scope

- Immutable percept publication, direct-child faculty discovery, ordered interpretation, and contained failure handling.
- Attention-only visual presence, focused descriptions, and focus-limited relative-position evidence.
- Declared-recipient speech interpretation at perception time, with trusted player provenance from scene membership.
- Policy-directed suppression, supersession, finite evidence, and event eligibility through AI-001.
- Attention snapshots used by [AI-003](../003-prompt-api/index.md) attended-character current-scene status.

## Out Of Scope

- Expressions, planner agent behaviour, compaction, and broader perception changes.
- Gaze selection, direct look-target assignment, and final perception or hysteresis tuning values.
- Addressee inference beyond explicit declarations: vocative (name-mention) matching, dyadic-recency cues,
  orientation- or gaze-based inference, and decision-model (LLM) inference; addressing follows declared recipients
  only.
- Hearing-reach filtering by declared recipients; hearing reach stays bounded by
  [SPCH-006](../../speech/006-hearing/index.md) alone.

## Acceptance Criteria

### User Requirements

1. Acceptance shows visual surveys update attention without creating memory or event history.
2. Acceptance shows a focused subject's unchanged description and position do not repeatedly reach the NPC, while
   changed description state and relevant retained position or distance evidence with its timestamp may appear in
   attended-character current-scene status.
3. Acceptance shows position evidence is unavailable after expiry or loss of focus and does not become an expiry event.
4. Acceptance shows sensing leaves gaze and eye presentation unchanged.
6. Acceptance shows a hearing NPC's addressee conclusion follows the speaker's declared recipients — addressed to it
   when included, addressed to others when named, undecided when none were declared — that unrecognised speech is
   undecided, and that bystanders outside the declaration still perceive the speech.

### Technical Requirements

1. Tests verify immutable exact-type percept publication, non-blocking intake, direct-child ordered faculty discovery,
   binding snapshots, serial interpretation, and per-observation fault containment.
2. Tests verify visual presence is attention-only and has none of the retained-log, timeline, scheduler, or
   rendered-event
   effects.
3. Tests verify the initial description and relative-position policy outcomes: equal suppression, changed supersession,
   finite non-expired position evidence, ordinary event ineligibility, attended-character current-scene availability of
   relevant position or distance evidence and its timestamp.
4. Tests verify relative-position evidence is focus-limited and that configuration validation rejects non-finite values.
5. Tests verify attention snapshots are immutable and no sensing or attention path assigns a look target.
6. Tests verify declared-recipient interpretation — single and multiple declared recipients resolving to the correct
   observer-relative states, omitted recipients staying undecided, unrecognised-speaker declarations ignored, and
   bystanders outside the declaration still perceiving the speech — that player speech keeps blank recipients with
   trusted private provenance, and that the perception path assigns no look target and makes no model request
   (AI-001 TR-13–TR-17).

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [SPCH-006: Hearing Component](../../speech/006-hearing/index.md)
