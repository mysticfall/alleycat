---
id: CTRL-001
title: Locomotion
---

# Locomotion

## Requirement

Define player-owned responsive locomotion, optional-navigation arbitration, contact-lock adoption, and authoritative
travel-release consumption without creating animation-contact or IK actuation policy.

## Goal

The player receives immediate input response, retains stable authored support, and leaves travel only at one validated
live release occurrence.

## User Requirements

1. Player movement and yaw remain responsive, and optional navigation yields before non-neutral manual movement.
2. A movement bout starts naturally, changes direction without needless restarts, and stops through one contact-safe
   authored release rather than an arbitrary phase cut.
3. Mismatched source, live animation identity, exit provenance, clock/cycle, or release interval evidence leaves
   ordinary player motion and travel intact, as does cached, delayed, consumed, cancelled, replaced, or replayed
   authority.
4. Valid planted support remains stable on both production rigs. Missing or refused contact evidence leaves ordinary
   animation-synchronised motion intact without forcing a discontinuity.
5. Player and NPC locomotion remain independently controllable while respecting the same ANIM-002 contact permission.
6. A valid support correction may reduce contact drift without introducing a second move, steering response, or release
   handoff correction.

## Technical Requirements

1. `ILocomotion` remains player-owned and independent of `INavigation`; neither implementation delegates movement
   execution, route policy, or animation scheduling to the other.
2. A non-neutral manual movement sample cancels optional navigation before locomotion runs. Neutral or transient
   neutralisation, waiting, permission loss, and braking do not count as manual cancellation.
3. The graph may enter its sole `*2i` exit only by exact-once consumption of an admitted occurrence defined by the
   normative [Player Travel-Release Occurrence Contract](player-travel-release-occurrence-contract.md). Immediate intent
   and authored braking do not authorise the graph hop. Its closed refusal set rejects source-identity; evaluated
   role/clip/event/side; clock-generation/cycle-identity; exit-role/exit-clip/catalogue-provenance; and interval
   mismatch.
   Invalid, non-contiguous, non-crossing, stale, cached, delayed, duplicate, replayed, consumed, cancelled, or replaced
   authority also fails closed while ordinary travel continues.
4. Travel-release occurrence authority and per-foot contact-lock lifecycle evidence are separate. The occurrence permits
   only the CTRL graph hop. ANIM-002 `Acquire`, `Support`, and `Release` evidence permits per-foot anchor, support,
   release, handoff, and optional current-support correction through IK-003. Neither authority substitutes for,
   reconstructs, or repairs the other.
5. ANIM-002 remains the sole contact-permission owner. Player production providers consume the central permission and
   immutable authored per-foot evidence. CTRL and IK neither infer, override, relax, nor duplicate that permission.
6. Player composition adopts the reusable
   [Contact-Lock Coordinator Contract](../../ik/003-leg-feet-ik/contact-lock-coordinator-contract.md), including two
   side-specific providers and independent per-foot acquire, support, release, handoff, refusal, and fallback.
   `CharacterIK` remains the sole target, activity-gate, and influence actuator.
7. Current valid support may authorise at most one bounded translation/yaw correction. CTRL consumes that
   already-authorised value exactly once in its existing coherent player movement operation. CTRL cannot calculate,
   select, feed back, compensate, steer from, or override it. Release handoff and absent, refused, stale, or infeasible
   states return zero correction. Collision-realised divergence fails closed to zero applied correction and diagnostics.
8. `ImmediateSliced`, `TravelExitStrategy`, seam waivers, arbitrary-phase exits, static-schedule or phase-only release
   shortcuts, and content-immutability waivers are prohibited. Insufficient authored content blocks the affected contact
   or graph transition while ordinary travel continues.
9. The player controller does not select, invoke, or actuate a foot provider; override foot targets, gates, modifiers,
   hips, or skeleton; or treat validation metrics as steering feedback.
10. CTRL introduces no terrain probing or adaptation, gait synthesis, motion matching, motion warping, whole-body
    repositioning, emergency contact interruption, stopped-pose glide, or NAV route-policy change.

## In Scope

- Player intent, movement/yaw permission, movement authority arbitration, and authoritative travel-release consumption.
- Production player contact-lock composition and exact-once bounded support-correction consumption.
- Optional player-navigation cancellation and player-specific animation scheduling.

## Out Of Scope

- NPC route planning, occurrence selection, terminal records, route outcomes, and NAV-001 remediation.
- Terrain adaptation, gait synthesis, motion matching, broad visual/seam-quality acceptance, and exact tuning values.
- Immediate slicing, arbitrary-phase exit, universal/exact arrival, and forced hard-contact exit.

## Acceptance Criteria

### User Requirement Acceptance

1. Both production reference rigs show responsive player movement and yaw, with manual input cancelling optional
   navigation before locomotion.
2. Both rigs show bouts ending through one authored live release occurrence without slicing, an arbitrary-phase pop, or
   forced planted-foot interruption.
3. Source, evaluated role/clip/event/side, clock generation, cycle identity, exit role/clip, or catalogue provenance
   mismatch retains ordinary player motion and travel without forcing the `*2i` hop.
4. Invalid, non-contiguous, non-crossing, stale, or missed intervals and cached, delayed, duplicate, replayed, consumed,
   cancelled, or replaced authority retain ordinary player motion and travel without a discontinuity.
5. Both rigs show independent left/right contact support and continuous release-to-fallback handoff. Missing, refused,
   stale, or infeasible evidence retains ordinary movement and travel.
6. Player and NPC production compositions consume the same ANIM-002 permission while retaining independent controller
   and NAV route policy.
7. Valid current support may reduce drift through one bounded correction; refusal, handoff, collision divergence, and
   absent support cause no forced correction or discontinuity.

### Technical Requirement Acceptance

1. Boundary and ordering checks prove the `ILocomotion`/`INavigation` separation and cancellation semantics in Technical
   Requirements 1–2.
2. Valid-occurrence tests cover every field, crossing/admission, freshness, exact-once consumption, cancellation, and
   replacement in the normative occurrence contract. Refusal tests explicitly cover source-identity; evaluated
   role/clip/event/side; clock-generation/cycle-identity; exit-role/exit-clip/catalogue-provenance; and malformed,
   unknown, non-finite, invalid, non-contiguous, non-crossing, stale, or missed interval and tick evidence.
3. Refusal tests reject cached, delayed, duplicate, replayed, consumed, cancelled, replaced, inferred, phase-only, and
   static-schedule-only authority, proving that ordinary player motion and travel continue without cached reuse.
4. Tests prove occurrence authority permits only the sole `*2i` hop and cannot admit contact; per-foot lifecycle
   evidence controls only anchor/support/release/handoff/correction and cannot permit that hop.
5. Both-rig production tests prove same-tick player role, clip, phase, foot, identity, and generation matching; two
   side-specific providers; post-installer composition; no diagnostic installation; per-foot independence; and
   `CharacterIK`-only target, gate, and influence actuation.
6. Movement-operation tests prove at most one authorised support correction is consumed exactly once. They prove zero
   correction for release handoff and absent, refused, stale, infeasible, or collision-divergent states, with no CTRL
   calculation, selection, feedback, compensation, steering, second move, or target write.
7. Regression checks prove missing or refused authority retains ordinary travel without cached reuse or discontinuity,
   and that slicing, phase-only, inferred, and static-schedule shortcuts are absent.
8. Review proves CTRL adds no NPC NAV occurrence, reservation, route, replan, terminal-tail, or outcome policy and makes
   no broad visual/seam-quality or NAV completion claim.

### Acceptance Traceability

- User Requirements 1–6 → User Requirement Acceptance 1–7.
- Technical Requirements 1–2 → Technical Requirement Acceptance 1.
- Technical Requirements 3–4 → Technical Requirement Acceptance 2–4, 7.
- Technical Requirements 5–6, 9 → Technical Requirement Acceptance 5.
- Technical Requirement 7 → Technical Requirement Acceptance 6.
- Technical Requirements 8, 10 → Technical Requirement Acceptance 7–8.

## References

- [Project Specifications Index](../../index.md)
- [Player Travel-Release Occurrence Contract](player-travel-release-occurrence-contract.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [IK-003: Leg And Feet IK System](../../ik/003-leg-feet-ik/index.md)
- [Contact-Lock Coordinator Contract](../../ik/003-leg-feet-ik/contact-lock-coordinator-contract.md)
