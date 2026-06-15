---
id: IK-001
title: Reusable Neck-Spine CCDIK Setup
---

# Reusable Neck-Spine CCDIK Setup

## Requirement

Provide a reusable neck-spine IK system using CCDIK that drives bones towards a target head position with
natural constraints and no implausible twisting.

## Goal

Define a portable, spec-first contract for a neck-spine IK scene testable across character rigs.

## User Requirements

1. Neck and spine motion follows head intent without implausible twisting.
2. Behaviour remains visually stable across head-target poses.

## Technical Requirements

1. Use a reusable `CCDIK3D` node with explicit joint constraints.
2. Consuming scenes provide head targets externally.
3. Include visual and non-visual verification paths.

## In Scope

- Reusable `CCDIK3D` neck-spine chain with joint limits.
- Standalone scene with unbound target reference.
- Visual verification via reference poses.

## Out Of Scope

- Full-body IK, limb IK, locomotion blending, or animation state machine.
- Retargeting across skeleton topologies.
- Physics-based secondary motion.
- Subjective polish beyond objective natural-pose checks.

## Acceptance Criteria

1. Spec defines user-visible outcomes and technical contracts.
2. Implementation uses `CCDIK3D` receiving head target from consuming scene.
3. Joint constraints are serialised to prevent implausible rotations.
4. Reusable scene saved at `@game/assets/characters/templates/ik/neck_spine_ccdik.tscn` with target left
   unbound.
5. Photobooth scene exists at `@game/tests/characters/ik/head_hips_ik_test.tscn`.
6. Test defines markers for poses: forward, left, right, up, down, stoop-forward, lean-back.
7. Runner confirms marker visibility before pose capture.
8. Screenshots captured for all poses; pose remains natural without over-rotation or inversion.
9. C# test validates IK behaviour via non-visual assertions.

## References

- @game/assets/characters/templates/ik/neck_spine_ccdik.tscn
- @game/tests/characters/ik/head_hips_ik_test.tscn
- @specs/testing/002-visual-verification-scope/index.md
- @specs/characters/000-character-skeleton/index.md
