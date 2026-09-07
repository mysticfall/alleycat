---
id: IK-003
title: Leg And Feet IK System
---

# Leg and Feet IK System

## Requirement

Define rig-neutral lower-limb IK and reusable production contact-lock composition for player and NPC rigs.

## Goal

Both reference rigs retain stable ordinary foot targeting while valid authored contacts can lock and release without
moving animation permission, locomotion policy, or target actuation into IK providers.

## User Requirements

1. Player and NPC legs remain stable and natural when animation or a valid external foot-target provider is in
   control.
2. A valid authored planted-foot constraint may preserve contact and return continuously to ordinary fallback at
   release.
3. Invalid, missing, stale, or refused evidence leaves ordinary foot-target behaviour intact without hidden locomotion
   intervention or forced discontinuity.
4. Both production rigs expose enough same-tick per-foot evidence to diagnose contact, correction, handoff, and
   fallback.
5. Finite transitions retain package-declared polarity and valid partial lifecycle semantics without inventing a
   boundary-censored endpoint or claiming broad animation/IK continuity.

## Technical Requirements

1. IK-003 is rig-neutral and applies the same lower-limb and contact-lock contracts to NPC and player rigs. Neither
   rig's topology, scene, provider, scheduler, or movement policy is normative for the other.
2. `CharacterIK` is the generic resolver and sole actuator of the selected foot target, target activity gate, and
   modifier influence. It owns no contact or locomotion policy.
3. [Leg-Feet IK Contract](leg-feet-ik-contract.md) normatively owns target synchronisation, generic provider
   composition, actuation boundaries, pole prediction, and two-bone solving.
4. [Leg-Feet IK Test Setup Contract](test-setup-contract.md) owns the two-rig fixture and Gate A/B evidence topology.
   Fixture diagnostics neither install nor establish production contact composition.
5. ANIM-002 remains the sole contact-permission owner. Each side-specific, evidence-owning provider consumes the
   central permission and immutable authored role-foot sample, returning a typed applicable result or refusal. It
   refuses every member of the
   [ANIM-002 Refusal Set](../../animation/002-locomotive-animation-set/index.md#canonical-contact-evidence).
6. Modifier order is `sync → provider → hip reconciliation (when present) → lower-leg consumers`. `CharacterIK` repairs
   ordering idempotently and performs sole actuation.
7. The [Contact-Lock Coordinator Contract](contact-lock-coordinator-contract.md) is the normative production lifecycle,
   anchor, correction, handoff, composition, identity, timing, diagnostics, and validation contract for NPC and player.
   Each foot independently acquires, supports, releases, hands off, refuses, and falls back.
8. Travel-release occurrence authority is separate from per-foot lifecycle evidence. IK lifecycle, anchor, support,
   release, handoff, and correction state cannot permit CTRL-001's graph hop, while CTRL occurrence authority cannot
   admit contact or repair IK evidence.
9. Production composition installs two side-specific providers and one reusable coordinator after the relevant
   character installers. Same-tick evaluation binds player role, clip, phase, foot, identity, and generation. Diagnostic
   paths remain read-only and install no production provider or coordinator. A terminal animation-package validation
   failure during installation is reported and latched once per installation attempt per the ANIM-002 delivery
   contract; readiness retries remain legitimate only while required installers or nodes are genuinely missing.
10. A current valid support may return at most one coordinator-authorised bounded translation/yaw correction. The
    applicable movement owner consumes that value exactly once in its existing coherent movement operation. Release
    handoff and absent, refused, stale, infeasible, or collision-divergent states produce zero correction.
11. Each production physics tick emits immutable diagnostic-only per-foot records with the same-tick evaluated role,
    clip, phase, foot, identity, generation, lifecycle, anchor, provider decision, selected target, gate, influence,
    correction, handoff/fallback, collision realisation, and native/post-IK observations. Diagnostics never accept
    contact, install composition, steer movement, or authorise a graph transition.
12. Synthetic declared-package regressions cover cyclic wrap, idle-to-travel `Release`, travel-to-idle `Acquire`, finite
    partial lifecycles, package replacement, fallback, and translation-only live-fallback handoff. Current production
    packages are structural and immutability inputs, not behavioural contact oracles.

## In Scope

- Generic per-foot provider resolution, sole actuation, pole/two-bone solve, and both-rig production composition.
- Independent contact lifecycle, anchor, support correction, release handoff, refusal, fallback, and diagnostics.
- Existing Gate A/B controls and synthetic declared-package lifecycle regressions within their recorded evidence scope.

## Out Of Scope

- Terrain probing or adaptation, pelvis or whole-body repositioning, motion warping, emergency interruption, gait
  synthesis, and movement-policy ownership.
- NAV route, destination, stopping, hard-boundary, or durable-outcome policy changes.
- Broad fluidity or Oblique animation/IK seam-quality acceptance and exact tuning values.

## Acceptance Criteria

### User Requirement Acceptance

1. Both production reference rigs demonstrate stable animation, applicable-provider, refusal, and ordinary-fallback
   lower-limb behaviour for both feet.
2. Valid support preserves its anchor and releases through a continuous non-contact handoff without forced release,
   synthetic gliding, wrong-foot clearing, or a one-frame target discontinuity.
3. Missing, invalid, stale, refused, or infeasible evidence retains ordinary fallback and causes no forced correction.
4. Player and NPC production traces expose same-tick per-foot contact, correction, handoff, fallback, collision, and
   post-IK evidence without affecting behaviour.
5. Synthetic finite-transition regressions preserve declared polarity and partial lifecycles while broad animation/IK
   continuity remains outside their claim.

### Technical Requirement Acceptance

1. Both-rig production checks prove rig neutrality, generic modifier order, two side-specific providers, post-installer
   coordinator composition, one-shot latched reporting of a terminal package failure during installation, no diagnostic
   installation, and `CharacterIK`-only target, gate, and influence actuation.
2. Permission and refusal tests prove both roles consume the same central ANIM-002 permission and immutable authored
   evidence without inference, override, symmetry substitution, or downstream repair.
3. Per-foot tests prove independent acquire, support, release, handoff, refusal, fallback, disable, cancellation, and
   replacement. Same-tick role, clip, phase, foot, identity, and generation mismatches fail closed.
4. Correction tests prove at most one currently supported contribution, exact-once consumption in the existing movement
   operation, and zero correction for release handoff, absence, refusal, staleness, infeasibility, cap breach, and
   collision-realised divergence. No consumer calculates, selects, feeds back, compensates, or steers from the value.
5. Boundary tests prove per-foot lifecycle evidence cannot authorise the CTRL graph hop and travel-release occurrence
   evidence cannot admit contact, anchors, handoff, or correction.
6. Trace checks prove immutable concurrent per-foot same-tick evidence and read-only diagnostics for both production
   rigs.
7. Synthetic declared-package checks prove both finite polarities, phase `1` retention where declared, no finite wrap,
   no invented endpoint, package replacement, fallback, and translation-only live-fallback handoff without a broad
   seam-quality claim.

### Acceptance Traceability

- User Requirements 1–5 → User Requirement Acceptance 1–5.
- Technical Requirements 1–4, 6 → Technical Requirement Acceptance 1.
- Technical Requirement 5 → Technical Requirement Acceptance 2–3.
- Technical Requirements 7, 9 → Technical Requirement Acceptance 1, 3, 6.
- Technical Requirement 8 → Technical Requirement Acceptance 5.
- Technical Requirement 10 → Technical Requirement Acceptance 4.
- Technical Requirement 11 → Technical Requirement Acceptance 6.
- Technical Requirement 12 → Technical Requirement Acceptance 7.

## Decision Trace

1. NAV-001 route-policy and NPC-only NAV consumption boundaries remain unchanged.
2. ANIM-002 centralises contact permission; player and NPC providers consume rather than duplicate it.
3. `CharacterIK` remains policy-free and the sole target, gate, and influence actuator.
4. Gate A/B fixture evidence remains test-only; production adoption is accepted only through the production composition
   and both-rig evidence above.

## References

- [Project Specifications Index](../../index.md)
- [ANIM-002: Locomotive Animation Set](../../animation/002-locomotive-animation-set/index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [Contact-Lock Coordinator Contract](contact-lock-coordinator-contract.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
