---
id: CHAR-000
title: Character Skeleton Profile
---

# Character Skeleton Profile

## Requirement

All characters in AlleyCat use Godot's built-in `SkeletonProfileHumanoid` as their skeleton profile, ensuring a
standardised humanoid bone naming convention and hierarchy across the project.

## Goal

Keep character rigs interoperable across IK, retargeting, animation, and tooling by standardising on a single humanoid
skeletal contract.

## User Requirements

1. Character animation and pose behaviour should remain consistent across supported characters.
2. Features that depend on humanoid anatomy (for example IK and speech facial mapping) should work without per-character
   skeleton schema rewrites.

## Technical Requirements

1. Character skeleton profiles must use Godot `SkeletonProfileHumanoid` naming and hierarchy.
2. The reference profile resource must remain aligned with `SkeletonProfileHumanoid` without incompatible overrides.
3. Downstream systems must treat the documented bone hierarchy as the canonical integration contract.

## In Scope

- Canonical humanoid skeleton-profile definition and hierarchy.
- Reference character profile resource alignment.
- Normative bone naming expectations for dependent systems.

## Out Of Scope

- Per-feature IK solver setup and runtime-node topology.
- Character mesh/weighting authoring workflows.
- Animation-style tuning and subjective motion polish.

## Bone Structure

A humanoid skeleton profile contains 56 bones divided into 4 groups: `Body`, `Face`, `LeftHand`, and `RightHand`. It is
structured as follows:

```
Root
└─ Hips
    ├─ LeftUpperLeg
    │  └─ LeftLowerLeg
    │     └─ LeftFoot
    │        └─ LeftToes
    ├─ RightUpperLeg
    │  └─ RightLowerLeg
    │     └─ RightFoot
    │        └─ RightToes
    └─ Spine
        └─ Chest
            └─ UpperChest
                ├─ Neck
                │   └─ Head
                │       ├─ Jaw
                │       ├─ LeftEye
                │       └─ RightEye
                ├─ LeftShoulder
                │  └─ LeftUpperArm
                │     └─ LeftLowerArm
                │        └─ LeftHand
                │           ├─ LeftThumbMetacarpal
                │           │  └─ LeftThumbProximal
                │           │    └─ LeftThumbDistal
                │           ├─ LeftIndexProximal
                │           │  └─ LeftIndexIntermediate
                │           │    └─ LeftIndexDistal
                │           ├─ LeftMiddleProximal
                │           │  └─ LeftMiddleIntermediate
                │           │    └─ LeftMiddleDistal
                │           ├─ LeftRingProximal
                │           │  └─ LeftRingIntermediate
                │           │    └─ LeftRingDistal
                │           └─ LeftLittleProximal
                │              └─ LeftLittleIntermediate
                │                └─ LeftLittleDistal
                └─ RightShoulder
                   └─ RightUpperArm
                      └─ RightLowerArm
                         └─ RightHand
                            ├─ RightThumbMetacarpal
                            │  └─ RightThumbProximal
                            │     └─ RightThumbDistal
                            ├─ RightIndexProximal
                            │  └─ RightIndexIntermediate
                            │     └─ RightIndexDistal
                            ├─ RightMiddleProximal
                            │  └─ RightMiddleIntermediate
                            │     └─ RightMiddleDistal
                            ├─ RightRingProximal
                            │  └─ RightRingIntermediate
                            │     └─ RightRingDistal
                            └─ RightLittleProximal
                               └─ RightLittleIntermediate
                                 └─ RightLittleDistal
```

### Reference Character Profile

The reference character (MakeHuman-based) uses a custom profile resource that inherits from `SkeletonProfileHumanoid`:

- `@game/assets/characters/reference/skeleton_profiles/skeleton_profile_makehuman.tres`

This resource is a `SkeletonProfileHumanoid` instance with no additional overrides, serving as the project's retargeting
profile.

## Acceptance Criteria

1. The specification defines both user-facing interoperability outcomes and technical skeleton contracts.
2. The canonical hierarchy references `SkeletonProfileHumanoid` with the documented humanoid bone structure.
3. The reference profile resource path is defined and documented as aligned with the canonical profile contract.

## References

- @game/assets/characters/reference/skeleton_profiles/skeleton_profile_makehuman.tres
- @game/assets/characters/reference/female/reference_female.tscn
