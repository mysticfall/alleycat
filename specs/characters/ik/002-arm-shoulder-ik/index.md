---
id: IK-002
title: Arm And Shoulder IK System
---

# Arm And Shoulder IK System

## Requirement

Provide an IK system that drives the arm bones (shoulder-to-hand) of a humanoid character towards target hand positions
derived from VR headset and controller inputs, with integrated shoulder correction that prevents deformation and ensures
natural upper-body poses.

## Goal

Define a spec-first, testable contract for implementing and verifying a TwoBoneIK3D-based arm IK system with a
predictive elbow-pole-target calculation and an integrated pre-IK shoulder-correction path, reusable across humanoid
character setups.

## User Requirements

1. Players must see natural arm reach behaviour that follows head/hand intent across common and extreme poses.
2. Shoulder behaviour must remain visually stable without deformation while arms move through key interaction poses.
3. Arm behaviour must remain consistent when the character body orientation changes (for example standing, stooping,
   lying).

## Technical Requirements

1. Implementation must provide per-arm `TwoBoneIK3D` solving and deterministic elbow pole-target prediction from
   head/hand references.
2. Pre-IK shoulder correction must be integrated into `ArmIKController` and execute before arm IK modifiers.
3. Hand-rotation-based elbow correction must be defined as a first-class contract with configurable weighting.
4. Elbow pole-target placement must include a guarded compression safeguard with tunable ratio/floor inputs and explicit
   compression/folded gating, while remaining non-invasive to hand-target solving and shoulder correction.
5. Validation must include both reusable photobooth scenarios and C# non-visual integration assertions, including a
   compressed folded-arm case that verifies enforced pole-offset floor behaviour.

## Specification Structure

This page is the source-of-truth overview for IK-002. Detailed component contracts are split into focused pages:

- [Arm IK Contract](arm-ik-contract.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)

Use this page for overall scope and acceptance traceability, then use the component pages for implementation detail.

## In Scope

- A `TwoBoneIK3D`-based arm IK setup per arm (left and right) that solves shoulder-to-hand bone chains towards
  hand-target positions.
- Elbow pole-target position prediction derived from head (headset) and hand (controller) locations and rotations.
- Baseline elbow pole-directions for a defined set of key hand poses, with hand-rotation-based adjustments layered
  on top.
- Shoulder correction integrated into `ArmIKController`, computed before `TwoBoneIK3D` solves, using a look-at delta
  from rest to current shoulder→elbow direction in body space to prevent deformation and maintain natural appearance.
- Consistent behaviour regardless of character body pose (standing, lying down, stooping, etc.).
- A standalone reusable IK scene for reuse in character scenes.

## Out Of Scope

- Full-body IK beyond the shoulder-to-hand chain.
- Finger IK or hand-gesture solving.
- Locomotion blending, animation state machine design, or animation-layer mixing.
- Physics-based secondary motion (for example spring bones or ragdoll behaviour).
- Retargeting rigs across different skeleton topologies.
- Subjective animation polish beyond objective natural-pose checks defined in this spec.

## Context

### VR Input Assumptions

The system operates in a VR context where the following IK target nodes are provided by the consuming scene:

| Target      | Description                                        |
|-------------|----------------------------------------------------|
| Head target | Position and rotation derived from the VR headset  |
| Hand target | Position and rotation derived from XR hand pose markers |

There is one hand target per arm (left and right).

Runtime player integration is expected to drive these targets through the player XR-to-IK bridge (`PlayerVRIK`) using
`XRManager` abstractions.

### Reference Character

The implementation must work correctly with the reference character at:

- `@game/assets/characters/reference/female/reference_female.tscn`

### Pose-Independence Requirement

The elbow pole-target prediction must produce correct results regardless of the character's overall body pose. The
same algorithm must handle upright standing, lying down, stooping forward, and any other orientation without requiring
pose-specific tuning.

## Component Contracts

- **Arm IK Contract:** Baseline poses, pole-target prediction, and arm-specific constraints are defined in
  [Arm IK Contract](arm-ik-contract.md).
- **Shoulder Correction Contract:** Shoulder correction algorithm, exported parameters, and pre-IK pipeline behaviour
  are defined in [Shoulder Correction Contract](shoulder-adjustment-contract.md).
- **Hand-Rotation Elbow Correction Contract:** Hand-rotation-based elbow pole-target correction, reference rotation
  interpolation, and `HandRotationWeight` parameterisation are defined in
  [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md).

## Acceptance Criteria

All criteria remain normative. IDs are provided for traceability to component contracts.

| ID    | Requirement                                                                                                                                                                                                                                                                                           | Primary Contract                                                |
|-------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------------------------|
| AC-00 | The specification defines both user-visible arm/shoulder behaviour outcomes and technical implementation contracts required for delivery and validation.                                                                                                                                               | This Page                                                       |
| AC-01 | Each arm uses a `TwoBoneIK3D` node to solve the upper-arm → lower-arm chain towards the hand target position.                                                                                                                                                                                         | [Arm IK Contract](arm-ik-contract.md)                           |
| AC-02 | Elbow pole-target prediction is derived from head and hand target positions and rotations, without relying on external state or animation data.                                                                                                                                                       | [Arm IK Contract](arm-ik-contract.md)                           |
| AC-03 | Pole-target prediction produces correct, natural elbow positions for all six key hand poses when the character is upright.                                                                                                                                                                            | [Arm IK Contract](arm-ik-contract.md)                           |
| AC-04 | Pole-target prediction produces correct results in non-upright poses (lying down, stooping, etc.) without pose-specific branches.                                                                                                                                                                     | [Arm IK Contract](arm-ik-contract.md)                           |
| AC-05 | Hand-rotation adjustment modifies the baseline pole direction smoothly and continuously across the full range of hand rotations.                                                                                                                                                                      | [Arm IK Contract](arm-ik-contract.md)                           |
| AC-06 | `ArmIKController` applies per-arm pre-IK shoulder correction, preventing visible deformation in all key poses.                                                                                                                                                                                        | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-07 | IK configuration is saved as a reusable scene with a clear file path (to be determined during implementation), with head/hand targets left unbound for consuming scenes.                                                                                                                              | This Page                                                       |
| AC-08 | A photobooth verification scene exists under `@game/tests/` and validates arm IK plus integrated shoulder correction across required key poses and body orientations, following `@specs/testing/002-visual-verification-scope/index.md`.                                                              | This Page                                                       |
| AC-09 | Visual checks confirm natural arm and shoulder poses without obvious over-rotation, inversion, discontinuous elbow movement, or shoulder deformation across required poses.                                                                                                                           | This Page                                                       |
| AC-10 | A C# integration test loads the same verification scene and validates behaviour using non-visual assertions (for example target proximity, pole-target bounds, shoulder rotation limits).                                                                                                             | This Page                                                       |
| AC-11 | Each arm controller applies shoulder correction to its corresponding shoulder bone proportional to upper-arm angular deviation from rest pose.                                                                                                                                                        | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-12 | Shoulder corrections are deterministic: the same arm pose always produces the same shoulder rotation.                                                                                                                                                                                                 | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-13 | Shoulder corrections are smooth across key poses, with no discontinuities or sudden jumps during pose transitions.                                                                                                                                                                                    | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-14 | Exported parameters (`Side`, `ShoulderWeight`, `ElevationWeight`) on `ArmIKController` are configurable in the Godot editor and overridable per instance.                                                                                                                                             | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-15 | A static `ShoulderCorrectionComputer` helper exists with no Godot scene-tree dependencies and provides pure math helpers for look-at basis construction, damped delta computation, and adaptive weighting, with dedicated C# unit tests in `@tests/src/IK/` against known input/output pairs. | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-16 | The photobooth verification scene inherits from the arm-shoulder IK test scene and uses the required modifier order with shoulder correction inside `ArmIKController`; integration tests verify expected shoulder rotation ranges per key pose.                                                       | [Shoulder Correction Contract](shoulder-adjustment-contract.md) |
| AC-17 | Hand-rotation correction rotates the elbow pole target around the shoulder-to-hand axis, pivoting at the closest point on that axis to the current pole target, with the rotation magnitude determined by the signed angular difference between actual and reference hand rotations scaled by `HandRotationWeight`. | [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md) |
| AC-18 | Reference hand rotations are interpolated from key pose markers using inverse-distance weighting in body-basis space, producing smooth and continuous neutral rotations across all hand positions. | [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md) |
| AC-19 | `HandRotationWeight` is exported on `ArmIKController` and is configurable per instance in the Godot editor. | [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md) |
| AC-20 | Per arm, base elbow pole-offset distance is computed from current arm length with a ratio tunable and a minimum offset floor tunable before any compression override is applied. | [Arm IK Contract](arm-ik-contract.md) |
| AC-21 | Compression safeguard activation uses both a clamped current/rest arm-length ratio gate and a folded-pose gate from body-local vertical relation (`hand Y <= shoulder Y` in body basis, equivalently `(hand - shoulder) · bodyUp <= 0`); when active, enforced floor is `max(minimum floor, restArmLength * 0.5 + margin)`. | [Arm IK Contract](arm-ik-contract.md) |
| AC-22 | Compression safeguard is non-invasive: it only constrains pole-target placement distance and does not alter hand target positions, shoulder correction logic, or hand-rotation correction logic. | [Arm IK Contract](arm-ik-contract.md) |
| AC-23 | Integration coverage includes a compressed folded-arm pose asserting that pole-target distance honours the compressed enforced floor contract. | [Arm IK Contract](arm-ik-contract.md) |

## Implementation Notes

These notes retain cross-component constraints. Component-specific notes are in each contract page.

### Modifier Pipeline Ordering

The required child order under `Skeleton3D` is:

1. `ArmIKController` (left)
2. `ArmIKController` (right)
3. `TwoBoneIK3D` (left)
4. `TwoBoneIK3D` (right)
5. `CopyTransformModifier3D` (hand rotation copies)

Ordering is mandatory because `SkeletonModifier3D` children execute in tree order.

Shoulder correction executes inside each `ArmIKController` before IK solve.

### Test-First Verification Workflow

C# integration tests are the primary acceptance gate for IK correctness.

The GDScript photobooth runner still generates multi-camera screenshots for all poses, but screenshots are a
diagnostic aid reviewed when assertions fail.

## Open Questions

1. **Pole-Target Interpolation Strategy** — How should baseline directions interpolate between key poses?
2. **Hand-Rotation Influence Weight** — How strongly should hand rotation influence pole direction relative to the
   positional baseline?
3. **Optional Chest Pose** — Should the "hands covering the chest" pose be included in initial delivery or deferred?
4. **Two-Arm Coordination** — Should both arms share state (for example symmetric bias) or remain independent?

## References

- @game/assets/characters/reference/female/reference_female.tscn
- @specs/characters/ik/001-neck-spine-ik/index.md
- @specs/testing/002-visual-verification-scope/index.md
- @specs/characters/000-character-skeleton/index.md
- @specs/characters/ik/002-arm-shoulder-ik/arm-ik-contract.md
- @specs/characters/ik/002-arm-shoulder-ik/shoulder-adjustment-contract.md
- @specs/characters/ik/002-arm-shoulder-ik/hand-rotation-correction-contract.md
