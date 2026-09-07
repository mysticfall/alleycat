---
title: Player Travel-Release Occurrence Contract
---

# Player Travel-Release Occurrence Contract

## Requirement

Define the immutable live occurrence authority that alone permits CTRL-001's sole travel-to-idle graph hop.

## Goal

Leave ordinary player travel exactly once at a compatible authored release crossing without inferring authority from
phase, schedules, catalogue metadata, or foot contact state.

## User Requirements

1. A player movement bout exits travel once at the intended authored release without slicing or replay.
2. Identity, provenance, clock, cycle, or interval disagreement never causes a graph transition.
3. Missing, cached, late, consumed, cancelled, replaced, or otherwise refused authority retains ordinary motion and
   travel without a forced transition.

## Technical Requirements

1. The player animation scheduler publishes an immutable `PlayerTravelReleaseOccurrenceV1`. CTRL is its sole consumer,
   and the record permits only the existing `*2i` graph hop. It grants no per-foot contact, anchor, support, handoff, or
   correction authority.
2. Each record contains one non-empty source identity; evaluated player role and clip; authored release-event identity
   and anatomical side; compatible exit role, clip, and catalogue provenance; clock generation; cycle identity and
   occurrence identity; evaluation physics tick; freshness disposition; previous/current clock interval; crossing and
   admission dispositions; and consumption state.
3. Admission requires a finite live evaluation from the current physics tick. Its `(previous, current]` interval must be
   ordered, contiguous with the immediately preceding evaluation for the same source, clock generation, and cycle, and
   cross the named authored event. Source identity, evaluated player role and clip, event, side, clock generation, cycle
   identity, compatible exit role and clip, and catalogue provenance must each match the selected authored candidate and
   live clock. The occurrence identity is unique within its generation and cycle.
4. ANIM-002 catalogue and event metadata identifies authored candidates and compatible exits. It does not prove a live
   crossing, admission, freshness, or consumable occurrence.
5. CTRL atomically consumes an admitted occurrence at most once in its evaluation physics tick. Consumed, cached,
   delayed, duplicated, replayed, missed, cancelled, or replaced authority can never be admitted or consumed again.
6. Cancellation invalidates the unconsumed occurrence. Replacement cancels the prior occurrence before publishing a new
   identity and never transfers admission or consumption state. Disable, role or clip departure, clock reset, and source
   replacement cancel incompatible pending authority.
7. The `Player Travel-Release Occurrence Refusal Set` is closed. Admission refuses:
   - missing or empty fields and malformed, unknown, or non-finite values;
   - source-identity mismatch;
   - evaluated role, evaluated clip, release-event, or anatomical-side mismatch;
   - clock-generation or cycle-identity mismatch;
   - exit-role, exit-clip, or catalogue-provenance mismatch;
   - an invalid, unordered, non-contiguous, non-crossing, or stale `(previous, current]` interval;
   - a stale evaluation physics tick;
   - missed, duplicate, or replayed crossings or occurrence identities;
   - cached, delayed, consumed, cancelled, or replaced authority; and
   - inferred, phase-only, or static-schedule-only evidence.
8. Any member of the closed refusal set returns no graph-hop permission. CTRL retains ordinary motion and travel, reuses
   no cached authority, and forces no discontinuity.
9. Travel-release authority is independent of ANIM-002 per-foot lifecycle evidence. Contact `Acquire`, `Support`, and
   `Release`, anchors, handoff, or support correction cannot create an occurrence. An occurrence cannot create or extend
   contact permission or repair lifecycle evidence.

## In Scope

- Player travel-release publication, admission, exact-once consumption, cancellation, replacement, and refusal.
- Compatibility checks against the authored ANIM-002 event and exit catalogue.

## Out Of Scope

- Per-foot contact permission, anchors, target handoff, support correction, and IK actuation.
- NPC NAV occurrence selection, reservation, route policy, replanning, convergence, and outcomes.
- Broad animation seam quality and exact tuning values.

## Acceptance Criteria

### User Requirement Acceptance

1. Production player composition exits a travel bout once at the admitted authored crossing without slicing or replay.
2. Source, evaluated role/clip/event/side, clock generation, cycle identity, exit role/clip, or catalogue provenance
   mismatch retains ordinary motion and travel without a visible forced transition.
3. Invalid, unordered, non-contiguous, non-crossing, stale, or missed intervals retain ordinary motion and travel.
4. Cached, delayed, duplicate, replayed, consumed, cancelled, replaced, inferred, phase-only, and static-schedule-only
   authority retains ordinary motion and travel without a visible forced transition.

### Technical Requirement Acceptance

1. Schema and admission tests verify every immutable field; finite values; same-tick freshness; and matching source,
   evaluated role/clip/event/side, clock generation, cycle identity, exit role/clip, and catalogue provenance.
2. Interval tests verify a valid ordered, contiguous, crossing `(previous, current]` interval and reject invalid,
   unordered, non-contiguous, non-crossing, stale, and missed intervals.
3. Exact-once tests prove one admitted occurrence permits one sole `*2i` hop and reject cached, delayed, consumed,
   duplicate, replayed, cancelled, or replaced authority and occurrence identities.
4. Cancellation and replacement tests prove old authority is invalidated without transfer or cached reuse.
5. Refusal tests cover every member of the closed Technical Requirement 7 refusal set and prove ordinary travel
   continues.
6. Boundary tests prove catalogue metadata alone, phase-only evidence, static schedules, and per-foot lifecycle evidence
   cannot admit an occurrence; occurrence authority cannot admit contact or support correction.

### Acceptance Traceability

- User Requirements 1–3 → User Requirement Acceptance 1–4.
- Technical Requirements 1–4 → Technical Requirement Acceptance 1–2, 6.
- Technical Requirements 5–6 → Technical Requirement Acceptance 3–4.
- Technical Requirements 7–8 → Technical Requirement Acceptance 1–5.
- Technical Requirement 9 → Technical Requirement Acceptance 6.

## References

- [CTRL-001: Locomotion](index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [Contact-Lock Coordinator Contract](../../ik/003-leg-feet-ik/contact-lock-coordinator-contract.md)
