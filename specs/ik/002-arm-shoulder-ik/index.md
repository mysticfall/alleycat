---
id: IK-002
title: Arm And Shoulder IK System
---

# Arm And Shoulder IK System

## Requirement

Provide an IK system that drives arm bones from shoulder to hand towards target hand positions derived from VR
headset and controller inputs, with integrated shoulder correction that prevents deformation and ensures natural
upper-body poses.

## Goal

Define a spec-first, testable contract for implementing and verifying a TwoBoneIK3D-based arm IK system with predictive
elbow pole-target calculation and an integrated pre-IK shoulder-correction path, reusable across humanoid character
setups.

## User Requirements

1. Players must see natural arm reach behaviour that follows head and hand intent across common and extreme poses.
2. Shoulder behaviour must remain visually stable without deformation while arms move through key interaction poses.
3. Arm behaviour must remain consistent when the character body orientation changes, such as standing, stooping, or
   lying down.
4. Provider influence of 0 on a hand must deactivate all corresponding arm and shoulder modifiers for that side.

## Technical Requirements

1. Implementation must provide per-arm TwoBoneIK3D solving and deterministic elbow pole-target prediction from head
   and hand references.
2. Pre-IK shoulder correction must be integrated into ArmIKController and execute before arm IK modifiers.
3. Shoulder correction must use pose and body-basis anatomical decomposition into elevation and protraction components,
   with side-aware sign conventions.
4. Shoulder correction must support forward-elevation damping and overhead elevation boost for raised-overhead poses.
5. Hand-rotation-based elbow correction must be defined as a first-class contract with configurable weighting.
6. Elbow pole-target placement must include a guarded compression safeguard with tunable ratio and floor inputs,
   while remaining non-invasive to hand-target solving and shoulder correction.
7. Baseline pole-direction prediction must be continuous across all positional pose boundaries, in addition to the
   hand-rotation layer's continuity under AC-05.
8. Validation must include both reusable photobooth scenarios and C# non-visual integration assertions, including a
   compressed folded-arm case that verifies enforced pole-offset floor behaviour.
9. Anchor configuration must be resource-driven, with runtime consuming data from configurable ArmPoleAnchorSetResource
   assets authored through an editor bake workflow rather than hardcoded anchor tables.
10. Authoring workflow must bake from editor node markers into resource assets, with one authoring root determining
    both the input pose container and the output resource file path.
11. Runtime symmetry behaviour must be fixed: the same resource used on both arms produces symmetric results, and no
    runtime mirror toggle is provided.
12. All pose-independent calculations must derive body-local basis each frame from skeleton landmarks using Hips to
    Neck for up, LeftShoulder to RightShoulder orthonormalised against up for right, and the cross product of right
    and up for forward.
13. **Provider influence gating**: When the corresponding `IKTargetIntentProvider` reports desired influence of 0,
    the arm `TwoBoneIK3D` and all shoulder correction modifiers for that side must be deactivated.
14. Provider influence propagates to both the arm solver and the shoulder correction path as a coupled contract.
15. Arm and shoulder IK must accept target transforms from `IKTargetIntentProvider` in the same manner as the hand
    target body, via the provider contract defined in [IK Implementation Notes](../implementation-notes.md).

## In Scope

- A TwoBoneIK3D-based arm IK setup per arm that solves shoulder-to-hand bone chains towards hand-target positions.
- Elbow pole-target position prediction derived from head and hand locations and rotations.
- Baseline elbow pole-directions for a defined set of key hand poses, with hand-rotation-based adjustments layered
  on top.
- Shoulder correction integrated into ArmIKController, computed before TwoBoneIK3D solves, using anatomical
  decomposition in body-basis space to prevent deformation and maintain natural appearance.
- Forward-elevation damping to reduce correction weight during forward-reach poses.
- Overhead elevation boost enabling raised-overhead poses to exceed T-pose shoulder lift while preserving T-pose
  baseline.
- Consistent behaviour regardless of character body pose.
- A standalone reusable IK scene for reuse in character scenes.
- Provider-driven target and influence support for hands via IKTargetIntentProvider.
- Provider influence gating that deactivates arm and shoulder modifiers when influence is 0.

## Out Of Scope

- Full-body IK beyond the shoulder-to-hand chain.
- Finger IK or hand-gesture solving.
- Locomotion blending, animation state machine design, or animation-layer mixing.
- Physics-based secondary motion such as spring bones or ragdoll behaviour.
- Retargeting rigs across different skeleton topologies.
- Subjective animation polish beyond objective natural-pose checks defined in this spec.
- Adaptive ElevationWeight path, replaced with explicit anatomical decomposition.
- Arm pole twist experiments; pole_direction intended to remain disabled.
- Runtime mirror toggle for anchor data; symmetry is governed by authoring.
- Arbitrary runtime anchor editing; anchors are defined at authoring time via bake workflow.

## Acceptance Criteria

All criteria remain normative. IDs are provided for traceability to component contracts.

| ID    | Requirement | Primary Contract |
|-------|-------------|------------------|
| AC-00 | Specification defines user-visible and technical contracts for delivery. | This Page |
| AC-01 | Each arm uses TwoBoneIK3D to solve shoulder-to-hand chain. | Arm IK Contract |
| AC-02 | Elbow pole-target derived from head and hand without external state. | Arm IK Contract |
| AC-03 | Natural elbow positions for all key poses when upright. | Arm IK Contract |
| AC-04 | Correct results in non-upright poses without pose-specific branches. | Arm IK Contract |
| AC-05 | Hand-rotation adjusts pole direction smoothly across full range. | Arm IK Contract |
| AC-06 | ArmIKController applies pre-IK shoulder correction, preventing deformation. | Shoulder Adjustment Contract |
| AC-07 | Portable IK scene avoids concrete `Female` node/mesh dependencies. | This Page |
| AC-08 | Photobooth validates arm IK and shoulder correction across key poses. | This Page |
| AC-09 | Visual checks confirm natural poses without deformation or discontinuities. | This Page |
| AC-10 | C# integration tests validate behaviour with non-visual assertions. | This Page |
| AC-11 | Shoulder correction uses anatomical decomposition in body-basis. | Shoulder Adjustment Contract |
| AC-12 | Shoulder corrections are deterministic per arm pose. | Shoulder Adjustment Contract |
| AC-13 | Shoulder corrections are smooth across key poses with no jumps. | Shoulder Adjustment Contract |
| AC-14 | Exported parameters configurable per instance in editor. | Shoulder Adjustment Contract |
| AC-15 | Static ShoulderCorrectionComputer with pure math helpers and unit tests. | Shoulder Adjustment Contract |
| AC-16 | Photobooth inherits arm-shoulder IK scene with required modifier order. | Shoulder Adjustment Contract |
| AC-17 | Hand-rotation rotates elbow pole around shoulder-to-hand axis. | Hand-Rotation Correction Contract |
| AC-18 | Reference rotations interpolated via inverse-distance weighting. | Hand-Rotation Correction Contract |
| AC-19 | HandRotationWeight exported and configurable per instance. | Hand-Rotation Correction Contract |
| AC-20 | Pole-offset computed from arm length with tunable ratio and floor. | Arm IK Contract |
| AC-21 | Compression safeguard uses ratio gate and folded-pose gate with enforced floor. | Arm IK Contract |
| AC-22 | Compression safeguard only constrains pole distance, not targets or corrections. | Arm IK Contract |
| AC-23 | Integration test covers compressed folded-arm with enforced floor. | Arm IK Contract |
| AC-24 | Baseline pole-direction is C0-continuous with no hard branches. | Arm IK Contract |
| AC-25 | Runtime consumes data from ArmPoleAnchorSetResource asset. | Arm IK Contract |
| AC-26 | Resource contains entries with direction, intent, and reach ratio. | Arm IK Contract |
| AC-27 | Authoring workflow bakes from T-pose markers to resource asset. | Arm IK Contract |
| AC-28 | Authoring root defines input container and output resource path. | Arm IK Contract |
| AC-29 | Same resource on both arms produces symmetric results. | Arm IK Contract |
| AC-30 | Photobooth validates behaviour with resource-driven anchors. | Arm IK Contract |
| AC-31 | C# tests validate resource loading, completeness, and symmetry. | Arm IK Contract |
| AC-32 | Provider influence of 0 deactivates arm TwoBoneIK3D and shoulder correction for that side. | Provider Gating |
| AC-33 | Provider target transforms drive hand target bodies via IKTargetIntentProvider contract. | Provider Gating |
| AC-34 | Influence gating propagates to all side-effect modifiers on the same side. | Provider Gating Contract |

## References

- @game/assets/characters/reference/player.tscn
- @specs/ik/001-neck-spine-ik/index.md
- @specs/testing/002-visual-verification-scope/index.md
- @specs/character/001-character-skeleton/index.md
- @specs/ik/002-arm-shoulder-ik/arm-ik-contract.md
- @specs/ik/002-arm-shoulder-ik/shoulder-adjustment-contract.md
- @specs/ik/002-arm-shoulder-ik/hand-rotation-correction-contract.md
