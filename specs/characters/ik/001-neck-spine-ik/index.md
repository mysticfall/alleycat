---
id: IK-001
title: Reusable Neck-Spine CCDIK Setup
---

# Reusable Neck-Spine CCDIK Setup

## Requirement

Provide a reusable character IK setup that uses Godot `CCDIK3D` to adjust neck-spine bones towards a target head
position with constrained, natural-looking motion.

## Goal

Define a spec-first, testable contract for implementing and verifying a reusable neck-spine IK scene that can be
instantiated across character setups.

## In Scope

- A `CCDIK3D`-based neck-spine IK chain configured to drive relevant neck and spine bones towards a provided head target
  position.
- Joint constraint configuration for the IK chain to prevent implausible neck/spine rotations.
- A standalone reusable IK setup scene for reuse in character scenes.
- Visual verification using the reference character scene and representative target head positions.

## Out Of Scope

- Full-body IK, limb IK, locomotion blending, or animation state machine design.
- Retargeting rigs across different skeleton topologies.
- Physics-based secondary motion (for example, spring bones or ragdoll behaviour).
- Subjective animation polish beyond objective natural-pose checks defined in this spec.

## Acceptance Criteria

1. The implementation uses a Godot `CCDIK3D` node to drive neck-spine adjustment towards a target head position supplied
   by the consuming scene.
2. The reusable IK scene includes explicit joint constraints on the neck-spine chain (serialised as joint limitations in
   the saved `CCDIK3D` settings) to keep rotations within plausible ranges.
3. The IK configuration is saved as a reusable scene at `@game/assets/characters/ik/neck_spine_ccdik.tscn`, with the
   target node left unbound in the reusable scene so consuming scenes provide/bind the head target externally.
4. A photobooth verification scene that inherits
   `@game/assets/characters/reference/female/photobooth/full_body_5_cams.tscn` exists at:
    - scene: `@game/tests/characters/ik/neck_spine_ccdik_test.tscn`
    - runner script (same base name): `@game/tests/characters/ik/neck_spine_ccdik_test.gd`
5. The verification scene defines target markers for visual and non-visual checks using `DebugMarker` for each required
   pose.
6. Visual checks cover both moderate and extreme target poses at minimum:
    - forward, left, right, up, down,
    - stoop-forward (head target down + forward),
    - lean-back (head target up + back).
7. Before feature-level pose capture, the runner performs a camera/marker framing pass (per-camera captures) to confirm
   required markers and subject regions are visible from the inherited camera rigs.
8. The runner executes pose scenarios and captures screenshots for all required poses using
   `Photobooth.capture_screenshots(...)`.
9. Visual checks confirm the resulting pose remains natural without obvious over-rotation, inversion, or discontinuous
   neck-spine deformation across all required target poses.
10. A C# integration test loads the same verification scene and validates the neck-spine IK behaviour using non-visual
    assertions (for example target proximity/transform checks against the defined markers).

## References

- @game/assets/characters/ik/neck_spine_ccdik.tscn
- @game/assets/characters/reference/female/photobooth/full_body_5_cams.tscn
- @game/tests/characters/ik/neck_spine_ccdik_test.tscn
- @game/tests/characters/ik/neck_spine_ccdik_test.gd
- @specs/testing/002-visual-verification-scope/index.md
- @specs/characters/000-character-skeleton/index.md
