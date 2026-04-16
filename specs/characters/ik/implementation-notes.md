---
id: IK-NOTES
title: IK Implementation Notes
---

# IK Implementation Notes

## Parent Specification

This page is a child guidance page under [IK: Player VRIK System](index.md).

## Purpose

Document cross-feature constraints and configuration patterns for Godot inverse kinematics modifiers used by AlleyCat
characters.

## Requirement

Define shared IK implementation contracts that multiple IK feature specs depend on for consistent runtime behaviour and
integration.

## Goal

Provide a single normative guidance page for cross-feature technical constraints without duplicating the same
implementation notes across child IK specs.

## User Requirements

1. Players should experience consistent IK behaviour across features that depend on shared solver/runtime assumptions.

## Technical Requirements

1. Shared solver configuration and modifier-order constraints must be explicitly documented for reuse by child specs.
2. XR-to-IK runtime bridge and startup binding contracts must be defined as cross-feature integration boundaries.
3. Child IK feature specs that rely on these constraints must reference this page as a normative dependency.

## Acceptance Criteria

1. The page defines both user-impact intent (cross-feature consistency) and technical implementation contracts.
2. Shared constraints for solver configuration, modifier ordering, and XR-to-IK runtime bridge contracts are explicitly
   documented.
3. References to dependent specs and implementation paths are maintained.

## TwoBoneIK3D Configuration

- **Set all bone names explicitly.** The solver requires `root_bone_name`, `middle_bone_name`, and `end_bone_name`.
  Missing any of these (especially `middle_bone_name`) causes the solver to run but produce **no visible effect** and
  emits no error.
- **Use canonical humanoid bone names.** For the reference skeleton, the arm chain is
  `LeftUpperArm → LeftLowerArm → LeftHand` (and right-arm equivalents). Shoulder, hips, and neck bones are still needed
  for the body-space frame used by pole controllers.
- **Bind target and pole nodes by path.** Keep the `target_node` and `pole_node` paths stable by storing target markers
  inside the test scene (see the Test Scene Self-Containment Rule in TEST-002).

## Skeleton Modifier Ordering

- `IKModifier3D` subclasses (including `TwoBoneIK3D`, `CCDIK3D`, and custom `SkeletonModifier3D` controllers) must be
  **direct children** of the `Skeleton3D`. Nesting them under intermediary `Node3D` containers prevents them from
  running.
- Godot executes skeleton modifiers in child order. Place custom controllers (for example the pole-target driver)
  **before** the IK solver nodes so that inputs are updated before the solver runs.

## Player XR To IK Runtime Bridge

### Startup Binding Contract

- `PlayerVRIKStartupBinder` is the startup binder for XR-to-IK wiring.
- It resolves the player from SceneTree group `Player`.
- It binds only after XR initialisation succeeds.
- It must use XRManager late-subscriber state (`InitialisationAttempted`, `InitialisationSucceeded`) so binding remains correct when startup order varies.

### Bridge Node Contract

- `PlayerVRIK` is the runtime bridge node under the player scene (`VRIK`).
- It exposes and uses player `Viewpoint`, head and hand IK target bodies, and the solved `Skeleton3D`.
- It inserts two callback stages into the skeleton modifier pipeline:
  - **Pre-IK Stage:** aligns XR origin to the player transform, then drives head and hand IK target bodies towards XR-derived transforms.
  - **Post-IK Stage:** applies origin compensation from the physical XR head vs solved head-bone position delta.

### World-Scale Calibration Contract

- `PlayerVRIK` performs one-time world-scale calibration on bind.
- Calibration uses the ratio between avatar rest viewpoint-local head height and XR camera local head height.
- If either height is near zero, calibration is skipped and current XR world scale is retained.

## References

- @specs/characters/000-character-skeleton/index.md
- @specs/characters/ik/001-neck-spine-ik/index.md
- @specs/characters/ik/002-arm-shoulder-ik/index.md
- @specs/xr/001-xr-manager/index.md
- @game/src/IK/PlayerVRIK.cs
- @game/src/IK/PlayerVRIKStartupBinder.cs
- Godot 4.6 documentation —
  [TwoBoneIK3D](https://docs.godotengine.org/en/stable/classes/class_twoboneik3d.html)
