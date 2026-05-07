---
id: CHAR-000
title: Character Skeleton Profile
---

# Character Skeleton Profile

## Requirement

All characters in AlleyCat use Godot's built-in `SkeletonProfileHumanoid` as their skeleton
profile, ensuring standardised humanoid bone naming and hierarchy across the project.

## Goal

Keep character rigs interoperable across IK, retargeting, animation, and tooling by standardising
on a single humanoid skeletal contract.

## User Requirements

1. Character animation and pose behaviour should remain consistent across supported characters.
2. Features that depend on humanoid anatomy (IK, speech facial mapping) should work without
   per-character skeleton schema rewrites.

## Technical Requirements

1. Character skeleton profiles must use Godot `SkeletonProfileHumanoid` naming and hierarchy.
2. The reference profile must remain aligned with `SkeletonProfileHumanoid` without incompatible
   overrides.
3. Downstream systems must treat the documented bone hierarchy as the canonical integration
   contract.

## In Scope

- Canonical humanoid skeleton-profile definition and hierarchy.
- Reference character profile resource alignment.
- Normative bone naming for dependent systems.

## Out Of Scope

- Per-feature IK solver setup and runtime node topology.
- Character mesh/weighting authoring workflows.
- Animation-style tuning and subjective motion polish.

## Acceptance Criteria

1. The specification defines user-facing interoperability outcomes.
2. The canonical hierarchy references `SkeletonProfileHumanoid` with documented bone structure.
3. The reference profile resource path is defined and documented.
4. Technical skeleton contracts are verified through resource alignment.

## References

- @game/assets/characters/reference/skeleton_profiles/skeleton_profile_makehuman.tres
- @game/assets/characters/reference/female/reference_female.tscn