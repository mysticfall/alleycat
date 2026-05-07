---
id: IK-NOTES
title: IK Implementation Notes
---

# IK Implementation Notes

## Parent Specification

Child guidance under [IK: Player VRIK System](index.md).

## TwoBoneIK3D Configuration

- Set all bone names explicitly. The solver requires `root_bone_name`, `middle_bone_name`,
  and `end_bone_name`. Missing `middle_bone_name` causes the solver to run with no visible
  effect and no error.
- Use canonical humanoid bone names: arm chain is
  `LeftUpperArm → LeftLowerArm → LeftHand` (right-arm equivalents for right side).
  Shoulder, hips, and neck bones are needed for the body-space frame used by pole
  controllers.
- Bind target and pole nodes by path. Store target markers inside the test scene to keep
  paths stable (see Test Scene Self-Containment Rule in TEST-002).

## Skeleton Modifier Ordering

- `IKModifier3D` subclasses (including `TwoBoneIK3D`, `CCDIK3D`, and custom
  `SkeletonModifier3D` controllers) must be **direct children** of the `Skeleton3D`.
  Nesting under `Node3D` containers prevents them from running.
- Godot executes skeleton modifiers in child order. Place custom controllers (for example
  the pole-target driver) **before** the IK solver nodes so that inputs are updated
  before the solver runs.

## Lower-Limb Convention Alignment

- IK-003 must follow IK-002 naming and role conventions: per-side
  `SkeletonModifier3D` controller instances before per-side solver instances.
- For lower-limb photobooth verification, use
  `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn` as the test-scene
  basis.
- When validating pelvis/hips offset behaviour for lower-limb IK, use a
  `BoneAttachment3D` harness that applies hips override position directly, without
  animation driving the same transform.

## Player XR To IK Runtime Bridge

### Startup Binding Contract

- `PlayerVRIKStartupBinder` resolves the player from the `Player` SceneTree group and
  binds only after XR initialisation succeeds.
- Use XRManager late-subscriber state (`InitialisationAttempted`,
  `InitialisationSucceeded`) so binding remains correct when startup order varies.

### Bridge Node Contract

- `PlayerVRIK` exposes player `Viewpoint`, head and hand IK target bodies, and the
  solved `Skeleton3D`.
- It inserts two callback stages into the skeleton modifier pipeline:
  - **Pre-IK Stage:** aligns XR origin to the player transform, then drives head and
    hand IK target bodies towards XR-derived transforms.
  - **Post-IK Stage:** applies origin compensation from the physical XR head vs solved
    head-bone position delta.

### World-Scale Calibration Contract

- `PlayerVRIK` performs one-time world-scale calibration on bind.
- Calibration uses the ratio between avatar rest viewpoint-local head height and XR
  camera local head height.
- If either height is near zero, calibration is skipped and the current XR world
  scale is retained.

## References

- @specs/characters/000-character-skeleton/index.md
- @specs/characters/ik/001-neck-spine-ik/index.md
- @specs/characters/ik/002-arm-shoulder-ik/index.md
- @specs/characters/ik/003-leg-feet-ik/index.md
- @specs/xr/001-xr-manager/index.md
- @game/src/IK/PlayerVRIK.cs
- @game/src/IK/PlayerVRIKStartupBinder.cs
- Godot 4.6 documentation —
  [TwoBoneIK3D](https://docs.godotengine.org/en/stable/classes/class_twoboneik3d.html)