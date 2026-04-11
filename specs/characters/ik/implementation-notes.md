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

## References

- @specs/characters/000-character-skeleton/index.md
- @specs/characters/ik/001-neck-spine-ik/index.md
- @specs/characters/ik/002-arm-shoulder-ik/index.md
- Godot 4.6 documentation —
  [TwoBoneIK3D](https://docs.godotengine.org/en/stable/classes/class_twoboneik3d.html)
