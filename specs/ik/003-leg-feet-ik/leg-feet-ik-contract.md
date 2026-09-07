---
title: Leg-Feet IK Contract
---

# Leg-Feet IK Contract

## Requirement

Define rig-neutral generic foot-target resolution, target actuation, knee-pole, and two-bone lower-limb solving beneath
IK-003.

## Goal

Resolve each leg's target deterministically and solve it without allowing providers or solvers to rewrite unrelated
state or policy.

## User Requirements

1. Legs bend predictably and smoothly towards resolved foot targets on both reference rigs.
2. An applicable foot provider takes control without visible conflict; a provider that cannot apply lets ordinary or
   fallback behaviour continue.
3. Feet do not snap because a solver mutates an input target or a knee pole becomes degenerate.
4. Each foot remains isolated from the other foot's invalid, stale, or inapplicable provider state.

## Technical Requirements

1. Each side has an upper-leg to lower-leg to foot `TwoBoneIK3D` solve and a preceding rig-neutral pole-target stage.
2. Before resolving provider targets, target synchronisation reads the current animated foot transforms. It does not
   mutate authored animation, contact evidence, or anchors.
3. `CharacterIK` resolves the explicit provider before the fallback provider. Existing providers retain their default
   semantics: a returned zero-influence intent is applicable and terminal, disabling that side's leg solve. A provider
   may instead explicitly refuse applicability through the generic resolution API, allowing the next provider to run.
4. An ordered generic composite queries providers in authored first-to-last order and selects the first applicable,
   finite, value-only resolution. It uses no numeric priorities. Refusal, invalid providers, non-finite results, and
   stale results are not selected; a result for one side must not be applied to the other side.
5. Providers return values only and must not mutate targets, activity gates, modifiers, root, hips, or skeleton.
   Only `CharacterIK` applies the selected target transform, activity gate, and modifier influence for that side.
6. The generic modifier order is `sync → provider → hip reconciliation (when present) → lower-leg consumers`.
   `CharacterIK` performs generic provider resolution and sole actuation, and repairs this order idempotently on
   initialisation or retry. Phase 1 production wiring must preserve this generic order.
7. The side-specific contact provider consumes ANIM-002 permission and an immutable authored per-foot sample. It returns
   a typed applicable result or refusal without writing a target, refusing every member of the
   [ANIM-002 Refusal Set](../../animation/002-locomotive-animation-set/index.md#canonical-contact-evidence).
   The provider adds no contact-specific `CharacterIK` branch, downstream target writer, or locomotion policy.
8. Pole prediction derives its primary direction from current animation geometry. If that direction is degenerate, a
   deterministic, smooth fallback uses the resolved target orientation. The implementation keeps a rest-leg-derived
   minimum pole-offset floor; its value remains tunable.
9. The solver must not write a target's position, rotation, or scale. It may publish solve diagnostics only through the
   post-IK validation path.
10. The implementation contains no terrain probe, pelvis or whole-body adjustment, contact inference, or downstream
    overwrite path.
11. The [Contact-Lock Coordinator Contract](contact-lock-coordinator-contract.md) may supply a value-only explicit
    provider result for NPC and player production composition. It owns anchors, lifecycle, feasibility, and correction
    disposition; this contract preserves `CharacterIK` as the sole target, gate, and influence actuator and grants no
    solver, provider, NAV, or CTRL contact-permission authority.
12. Before activating native or runtime modifier groups, `CharacterIKSubsystemInstaller` sets the resolved runtime
    `Skeleton3D.ModifierCallbackModeProcess` to `Physics`. On each physics tick, the native lower-leg path is
    `sync → provider resolution → native lower-leg consumers`. `CharacterIK` remains the sole target, gate, and
    influence actuator; providers and the coordinator return values only and assign no target or NAV policy. Live
    diagnostics observe the post-modifier boundary so one tick can evidence both provider resolution and native output.

## In Scope

- Animation target synchronisation, generic provider composition and actuation, read-only resolved targets, pole
  prediction, two-bone solve, and Phase 1 contact-provider failure semantics.
- Rig-neutral configuration needed to apply those responsibilities to both reference rigs.
- Value-only coordinator integration at the generic provider boundary for NPC and player production composition.

## Out Of Scope

- Contact permission and authored-track ownership, terrain adaptation, and movement policy.
- Numeric pole thresholds and exact scene-node names, which remain rig configuration rather than shared contracts.
- NAV/CTRL/ANIM policy and terrain or pelvis authority. Production contact composition remains normative in the
  coordinator contract without transferring policy to this generic solve contract.

## Acceptance Criteria

### User Requirement Acceptance

1. Both reference rigs show stable knee direction and target following through supported poses and a degenerate-pole
   case.
2. Switching between synchronised, applicable-provider, and fallback-provider targets is visibly deterministic and free
   from target conflict.
3. A refusal under the ANIM-002 canonical set does not disturb the other foot or block a valid ordinary fallback.

### Technical Requirement Acceptance

1. Gate A fixture checks prove each side has the stated two-bone solve and preceding rig-neutral pole-target stage on
   both reference rigs.
2. Gate A fixture checks prove synchronisation reads the current animated foot transform before provider resolution and
   preserves authored animation, contact evidence, and anchors.
3. Gate A fixture checks prove explicit-provider precedence over fallback, terminal zero-influence behaviour, and
   explicit-refusal fallback for each side.
4. Gate A fixture checks prove first-applicable finite, value-only composite selection in authored order and that one
   side's result cannot affect the other. Accepted Gate B fixture checks prove pre-IK and post-IK rejection with
   preserved native provenance; see the [test setup contract](test-setup-contract.md#gate-b-accepted-test-only).
5. Gate A fixture checks prove providers do not mutate targets, gates, modifiers, root, hips, or skeleton, and that
   only `CharacterIK` applies the selected target, gate, and influence for that side.
6. Gate A fixture checks prove the generic modifier order, generic `CharacterIK` provider resolution and sole
   actuation, and idempotent initialisation/retry repair. Phase 1 composition preserves this same order.
7. Gate A fixture checks prove generic per-foot provider resolution without provider-specific `CharacterIK` logic. An
   audit confirms the fixture does not establish production contact-provider, contact-lock, or scene-wiring policy.
8. Gate A fixture checks prove animation-geometry pole prediction, deterministic smooth target-orientation fallback,
   and a tunable rest-leg-derived minimum pole-offset floor.
9. Gate A fixture checks prove the solver does not write target position, rotation, or scale and publishes diagnostics
   only through post-IK validation.
10. An audit confirms the Gate A test-only control contains no terrain probe, pelvis or whole-body adjustment, contact
    inference, or downstream target overwrite path. Gate B's test-only acceptance does not alter this boundary.
11. Phase 1 checks verify the contact provider consumes ANIM-002 permission and an immutable sample, returns only a
      typed applicable result or refusal, and never writes a target. It refuses every member of the
      [ANIM-002 Refusal Set](../../animation/002-locomotive-animation-set/index.md#canonical-contact-evidence).
      Ordinary animation-synchronised fallback remains valid motion but is never contact acceptance.
12. Focused NPC-playtest checks prove the coordinator result remains value-only through this boundary, no provider or
       solver target write occurs, and only `CharacterIK` applies the selected target, gate, and influence.
13. A focused real-NPC installer/runtime trace proves the resolved skeleton is set to `Physics` before modifier-group
    activation. It captures same-tick synchronisation, provider resolution, and native lower-leg output at a
    post-modifier boundary, and confirms that only `CharacterIK` actuates targets, gates, and influence.

The [test setup contract](test-setup-contract.md) records accepted Gate A test-only native provider-pipeline control
and visual evidence for this contract, plus accepted Gate B disposable fixture evidence. Neither acceptance establishes
the Phase 1 contract; that contract is defined by IK-003 and ANIM-002. It does not authorise terrain or pelvis
intervention, release-metadata contact inference, or ANIM/NAV/CTRL policy integration.

### Acceptance Traceability

- User Requirement 1 → User Requirement Acceptance 1.
- User Requirement 2 → User Requirement Acceptance 2.
- User Requirement 3 → User Requirement Acceptance 1–2.
- User Requirement 4 → User Requirement Acceptance 3.
- Technical Requirement 1 → Technical Requirement Acceptance 1.
- Technical Requirement 2 → Technical Requirement Acceptance 2.
- Technical Requirement 3 → Technical Requirement Acceptance 3.
- Technical Requirement 4 → Technical Requirement Acceptance 4.
- Technical Requirement 5 → Technical Requirement Acceptance 5.
- Technical Requirement 6 → Technical Requirement Acceptance 6.
- Technical Requirement 7 → Technical Requirement Acceptance 7, 11.
- Technical Requirement 8 → Technical Requirement Acceptance 8.
- Technical Requirement 9 → Technical Requirement Acceptance 9.
- Technical Requirement 10 → Technical Requirement Acceptance 10.
- Technical Requirement 11 → Technical Requirement Acceptance 12.
- Technical Requirement 12 → Technical Requirement Acceptance 13.

## References

- [IK-003 Overview](index.md)
- [Contact-Lock Coordinator Contract](contact-lock-coordinator-contract.md)
- [Leg-Feet IK Test Setup Contract](test-setup-contract.md)
