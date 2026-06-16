---
id: IK-001
title: Reusable Neck-Spine CCDIK Setup
---

# Reusable Neck-Spine CCDIK Setup

## Requirement

Provide a reusable character IK setup using Godot `CCDIK3D` to adjust neck-spine bones
towards a target head position with constrained, natural-looking motion.

## Goal

Define a spec-first, testable contract for a reusable neck-spine IK scene that can be
instantiated across character setups.

## User Requirements

1. Players should see neck and spine motion that follows head intent without implausible
   twisting.
2. Neck/spine behaviour should remain visually stable across representative head-target
   poses.

## Technical Requirements

1. Implementation must define a reusable `CCDIK3D` neck-spine setup with explicit joint
   constraints.
2. Consuming scenes must provide head targets externally so the reusable setup remains portable.
3. Verification workflow must include photobooth visual checks and C# non-visual
   integration assertions.

## In Scope

- A `CCDIK3D`-based neck-spine IK chain driving neck and spine bones towards a head target
  position.
- Joint constraint configuration to prevent implausible neck/spine rotations.
- A standalone reusable IK setup scene for reuse in character scenes.
- Visual verification using reference character scene and representative target head poses.

## Out Of Scope

- Full-body IK, limb IK, locomotion blending, or animation state machine design.
- Retargeting rigs across different skeleton topologies.
- Physics-based secondary motion (for example, spring bones or ragdoll behaviour).
- Subjective animation polish beyond objective natural-pose checks in acceptance criteria.

## Acceptance Criteria

1. The spec defines both user-visible motion outcomes and technical implementation
   contracts. (TRACES UR-1, UR-2, TR-1, TR-2)
2. The implementation uses a Godot `CCDIK3D` node to drive neck-spine adjustment
   towards a target head position supplied by the consuming scene. (TRACES TR-1,
   TR-2)
3. The reusable IK scene includes explicit joint constraints on the neck-spine
   chain (serialised in saved `CCDIK3D` settings) to keep rotations within plausible
   ranges. (TRACES TR-1, TR-3)
4. The IK configuration is saved as a reusable scene at
   `@game/assets/characters/templates/ik/neck_spine_ccdik.tscn`, with the target node left
   unbound so consuming scenes provide/bind the head target externally. (TRACES TR-2)
5. A photobooth verification scene exists at:
   - `@game/tests/ik/neck_spine_ccdik_test.tscn`
   - runner script: `@game/tests/ik/neck_spine_ccdik_test.gd`
   (INHERITS `@game/assets/testing/photobooth/templates/full_body_5_cams.tscn`)
6. The verification scene defines target markers for visual and non-visual checks
   using `DebugMarker` for each required pose. (TRACES TR-3)
7. Visual checks cover moderate and extreme target poses: forward, left, right, up,
   down, stoop-forward, lean-back. (TRACES UR-1, UR-2)
8. Before feature-level capture, runner performs a camera/marker framing pass to
   confirm required markers and subject regions are visible.
9. Runner executes pose scenarios and captures screenshots using
   `Photobooth.capture_screenshots(...)`. (TRACES TR-3)
10. Visual checks confirm resulting pose remains natural without obvious over-rotation,
    inversion, or discontinuous neck-spine deformation. (TRACES UR-1, UR-2)
11. A C# integration test loads the same verification scene and validates neck-spine
    IK behaviour using non-visual assertions (for example, target proximity/transform
    checks). (TRACES TR-3)

## References

- @game/assets/characters/templates/ik/neck_spine_ccdik.tscn
- @game/assets/testing/photobooth/templates/full_body_5_cams.tscn
- @game/tests/ik/neck_spine_ccdik_test.tscn
- @game/tests/ik/neck_spine_ccdik_test.gd
- @specs/testing/002-visual-verification-scope/index.md
- @specs/character/001-character-skeleton/index.md
