---
id: IK-001
title: Neck-Spine CCDIK Configuration
---

# Neck-Spine CCDIK Configuration

## Requirement

Provide character-local Godot `CCDIK3D` configurations that adjust neck-spine bones towards a
head target with constrained, natural-looking motion.

## Goal

Define a spec-first, testable contract for neck-spine IK authored locally in every supported
base character template.

## User Requirements

1. Players should see neck and spine motion that follows head intent without implausible
   twisting.
2. Neck/spine behaviour should remain visually stable across representative head-target
   poses.

## Technical Requirements

1. Every supported base character template must locally author a `NeckSpineIK` `CCDIK3D` node
   with the required target binding, five-joint chain, and explicit joint constraints.
2. Each local configuration must bind to its template's designated head-solve target and
   resolve the canonical `Spine`-to-`Head` chain for its own generated skeleton. Cached
   `root_bone`/`end_bone` indices are authored per template alongside canonical bone-name
   keys; templates must not share a scene-level CCDIK definition because bone indices differ
   between skeletons.
3. The chain must run from the canonical `Spine` root bone to the canonical `Head` end bone,
   contain exactly five joints, and preserve its serialised per-joint constraint configuration,
   including any deliberately unrestricted joint.
4. Verification must exercise the locally authored configurations through the head-hips
   photobooth verification flow and C# non-visual integration assertions; it must not depend
   on a separate shared IK scene.

## In Scope

- A `CCDIK3D`-based neck-spine IK chain driving neck and spine bones towards a head target
  position.
- Joint constraint configuration to prevent implausible neck/spine rotations.
- Local `NeckSpineIK` authoring in every supported base character template.
- Automated photobooth and integration verification using representative target head poses.

## Out Of Scope

- Full-body IK, limb IK, locomotion blending, or animation state machine design.
- Retargeting rigs across different skeleton topologies.
- Physics-based secondary motion (for example, spring bones or ragdoll behaviour).
- Subjective animation polish beyond objective natural-pose checks in acceptance criteria.

## Acceptance Criteria

1. The spec defines both user-visible motion outcomes and technical implementation contracts.
   (TRACES UR-1, UR-2, TR-1, TR-2, TR-3, TR-4)
2. Each supported base character template contains a locally authored `NeckSpineIK` Godot
   `CCDIK3D` node that drives neck-spine adjustment towards its designated head-solve target.
   (TRACES TR-1, TR-2)
3. Every local configuration resolves the canonical `Head` end bone for its own generated
   skeleton, with per-template cached indices that match that skeleton's name-keyed bones.
   (TRACES TR-2)
4. Each local configuration serialises a canonical `Spine`-to-`Head` chain with exactly five
   joints and explicit per-joint constraint configuration, including any deliberately
   unrestricted joint, to keep rotations within plausible ranges. (TRACES TR-1, TR-3)
5. Photobooth verification runs through the head-hips verification scene:
   - `@game/tests/ik/head_hips_ik_test.tscn`
   - runner script: `@game/tests/ik/head_hips_ik_test.gd`
   (INHERITS `@game/assets/testing/photobooth/templates/full_body_5_cams.tscn`)
6. The verification scene defines target markers for visual and non-visual checks using
   `DebugMarker` for each required pose. (TRACES TR-4)
7. Visual checks cover moderate and extreme target poses: forward, left, right, up,
   down, stoop-forward, lean-back. (TRACES UR-1, UR-2)
8. Before feature-level capture, runner performs a camera/marker framing pass to
   confirm required markers and subject regions are visible.
9. The automated runner exercises the locally authored `NeckSpineIK` configuration for each
   pose scenario and captures screenshots using `Photobooth.capture_screenshots(...)`.
   (TRACES TR-4)
10. Visual checks confirm resulting pose remains natural without obvious over-rotation,
    inversion, or discontinuous neck-spine deformation. (TRACES UR-1, UR-2)
11. C# integration tests validate the locally authored neck-spine IK without a dedicated IK
    scene: base-template authoring assertions resolve each template's cached indices against
    its own skeleton, and verification-scene behaviour is asserted non-visually through the
    head-hips coverage. (TRACES TR-2, TR-4)

## References

- @game/assets/testing/photobooth/templates/full_body_5_cams.tscn
- @game/tests/ik/head_hips_ik_test.tscn
- @game/tests/ik/head_hips_ik_test.gd
- @specs/testing/002-visual-verification-scope/index.md
- @specs/character/001-character-skeleton/index.md
