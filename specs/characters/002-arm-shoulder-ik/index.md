---
id: CHAR-002
title: Arm And Shoulder IK System
---

# Arm And Shoulder IK System

## Requirement

Provide an IK system that drives the arm bones (shoulder-to-hand) of a humanoid character towards target hand positions
derived from VR headset and controller inputs, with a complementary shoulder-adjustment layer that prevents deformation
and ensures natural upper-body poses.

## Goal

Define a spec-first, testable contract for implementing and verifying a TwoBoneIK3D-based arm IK system with a
predictive elbow-pole-target calculation and an accompanying shoulder-bone correction component, reusable across
humanoid character setups.

## In Scope

- A `TwoBoneIK3D`-based arm IK setup per arm (left and right) that solves shoulder-to-hand bone chains towards
  hand-target positions.
- Elbow pole-target position prediction derived from head (headset) and hand (controller) locations and rotations.
- Baseline elbow pole-directions for a defined set of key hand poses, with hand-rotation-based adjustments layered
  on top.
- A shoulder-adjustment component that corrects shoulder-bone rotations based on the solved arm pose to prevent
  deformation and maintain natural appearance.
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

The system operates in a VR context where the following IK target nodes are assumed to exist as `Marker3D` nodes
provided by the consuming scene:

| Target       | Description                                        |
| ------------ | -------------------------------------------------- |
| Head target  | Position and rotation derived from the VR headset  |
| Hand target  | Position and rotation derived from a VR controller  |

There is one hand target per arm (left and right).

### Reference Character

The implementation must work correctly with the reference character at:

- `@game/assets/characters/reference/female/reference_female.tscn`

### Pose-Independence Requirement

The elbow pole-target prediction must produce correct results regardless of the character's overall body pose. The
same algorithm must handle upright standing, lying down, stooping forward, and any other orientation without requiring
pose-specific tuning.

## Arm IK Component

### Mechanism

Each arm uses a Godot `TwoBoneIK3D` node configured to solve the upper-arm → lower-arm chain. The key implementation
challenge is computing the correct elbow pole-target position so that the solved arm bend direction appears natural.

### Pole-Target Prediction

The elbow pole-target position is predicted from the head and hand targets. The prediction has two layers:

1. **Baseline pole direction** — calculated from the hand position relative to the head, based on a set of key poses.
2. **Hand-rotation adjustment** — applied on top of the baseline to account for wrist orientation.

#### Key Hand Poses (Baselines)

The system defines baseline elbow pole-directions for the following key hand positions. In all cases, when the hand
rotation matches the baseline rotation listed below, the baseline pole direction is **laterally outward** (away from
the body midline), except where noted:

| Pose                           | Hand Position              | Baseline Hand Rotation   | Baseline Pole Direction          |
| ------------------------------ | -------------------------- |--------------------------| -------------------------------- |
| Arms lowered                   | Hands at sides             | Palms facing each other  | Laterally outward                |
| Arms raised forward            | Hands in front of body     | Palms facing each other  | Laterally outward                |
| Arms raised straight overhead  | Hands above head           | Palms facing forward     | Laterally outward                |
| Arms raised to each side       | Hands extended to sides    | Palms facing downward    | **Posterior** (toward the back)  |
| Hands behind the head          | Hands behind head          | Palms facing forward     | Laterally outward                |
| Hands covering the chest       | Hands in front of chest    | Palms facing backward    | Laterally outward (optional)     |

The last pose (hands covering the chest) is optional and may be deferred.

#### Hand-Rotation Adjustment

The actual pole direction is computed by blending or offsetting the baseline direction with a contribution derived
from the hand target's rotation. This ensures that, for example, a wrist twist or forearm pronation/supination
influences the elbow's outwards or backwards lean in a natural way.

The hand-rotation adjustment must produce smooth, continuous transitions across the full range of hand rotations
without discontinuities or sudden flips.

## Shoulder Adjustment Component

### Purpose

The shoulder adjustment component is not an IK solver. Instead, it observes the solved arm pose (after the arm IK
has resolved) and applies corrective rotations to the shoulder bones to:

- Prevent mesh deformation at the shoulder joint when the arm is raised or rotated to extreme angles.
- Maintain a natural silhouette (for example clavicle raise when arms are elevated, shoulder roll when arms are
  behind the body).

### Mechanism

The shoulder adjustment should derive its corrections from the arm bone transforms produced by the IK solve — for
example by comparing the upper-arm direction against the rest pose to determine how far the arm has deviated, and
applying a proportional or curve-mapped rotation to the shoulder bone.

The exact mapping (linear, curve-driven, or bone-space procedural) is an implementation detail to be refined during
development, but must be deterministic and produce consistent results for the same input arm pose.

## Acceptance Criteria

1. Each arm uses a `TwoBoneIK3D` node to solve the upper-arm → lower-arm chain towards the hand target position.
2. The elbow pole-target prediction is derived from head and hand target positions and rotations, without relying on
   any external state or animation data.
3. The pole-target prediction produces correct, natural elbow positions for all six key hand poses when the character
   is upright.
4. The pole-target prediction produces correct results when the character is in non-upright poses (lying down,
   stooping, etc.) without requiring pose-specific branches.
5. The hand-rotation adjustment modifies the baseline pole direction smoothly and continuously across the full range
   of hand rotations.
6. The shoulder adjustment component applies corrective rotations to shoulder bones based on the solved arm pose,
   preventing visible deformation in all key poses.
7. The IK configuration is saved as a reusable scene with a clear file path (to be determined during implementation),
   with target nodes left unbound so consuming scenes provide/bind head and hand targets externally.
8. A photobooth verification scene exists under `@game/tests/` that validates the arm IK and shoulder adjustment
   across all required key poses and body orientations, following the visual verification workflow defined in
   `@specs/testing/002-visual-verification-scope/index.md`.
9. Visual checks confirm the resulting arm and shoulder poses appear natural without obvious over-rotation, inversion,
   discontinuous elbow movement, or shoulder deformation across all required poses.
10. A C# integration test loads the same verification scene and validates arm IK behaviour using non-visual assertions
    (for example target proximity, pole-target position bounds, shoulder rotation limits).

## Implementation Notes

These notes capture technical decisions and constraints discovered during implementation.

### TwoBoneIK3D Configuration

Godot's `TwoBoneIK3D` requires **all three bone names** to be set explicitly:

- `root_bone_name` — the upper arm bone (for example `LeftUpperArm`).
- `middle_bone_name` — the lower arm / elbow bone (for example `LeftLowerArm`).
- `end_bone_name` — the hand bone (for example `LeftHand`).

Missing any bone name (particularly `middle_bone_name`) causes the solver to silently produce no effect. There is no
error message.

`TwoBoneIK3D` and any custom `SkeletonModifier3D` nodes must be **direct children** of the `Skeleton3D` node. The
`ArmIkController` (which drives the pole-target positions) must appear **before** the `TwoBoneIK3D` nodes in child
order so that pole targets are positioned before the solver runs.

### ArmIkController

The `ArmIkController` class extends `SkeletonModifier3D` (not `Node3D`) and is decorated with `[GlobalClass]`. It
overrides `_ProcessModificationWithDelta` to compute pole-target positions within the skeleton modifier pipeline.

### Body Reference Frame

The body-local coordinate frame is derived per-frame from skeleton landmarks:

- **Up** — direction from `Hips` to `Neck`.
- **Right** — direction from `LeftShoulder` to `RightShoulder`, orthonormalised against up.
- **Forward** — cross product of right and up.

This frame adapts automatically to any body orientation.

### Baseline Hand Rotation Encoding

The verification scene's pose markers (`Marker3D` nodes) encode both the target hand position and the baseline hand
rotation for each pose. Each marker is rotated so that when a hand bone's **global rotation** matches the marker's
global rotation, the hand is in its "natural" position for that pose. This means the baseline rotation varies from
pose to pose — it is position-dependent.

For testing purposes, the hand IK target's `global_transform` is set to match the corresponding pose marker's
`global_transform`, which places the hand at both the correct position and the baseline rotation. A
`CopyTransformModifier3D` node at the end of the IK modifier stack copies rotation from the hand target to the hand
bone, ensuring the visual hand orientation matches the marker.

### Phased Delivery

The implementation is delivered in phases. The initial phase focuses on the baseline pole-target prediction from hand
position only. The hand-rotation adjustment layer — which offsets the pole direction based on how the hand is rotated
away from its baseline — is deferred to a subsequent phase. The shoulder adjustment component is also deferred to a
subsequent phase.

This allows validating that the core positional pole-target prediction works correctly before adding rotation-based
refinements.

### Test-First Verification Workflow

C# integration tests are the primary verification mechanism for arm IK correctness. These tests assert pole-target
direction, elbow bend direction, and hand proximity to targets for each key pose.

The GDScript photobooth runner still produces multi-camera screenshots for all poses, but screenshots serve as a
**diagnostic aid** — they are reviewed when tests fail but are not the primary acceptance gate.

Integration tests capture screenshots from relevant cameras when assertions fail, using `Object.Call()` to invoke
GDScript `Photobooth`/`CameraRig` methods from C#.

## Open Questions

1. **Pole-target interpolation strategy** — How should baseline directions interpolate between key poses? Options
   include spherical linear interpolation (slerp) between nearest key-pose directions, a weighted blend based on
   angular proximity, or a continuous analytical function. This should be resolved during brainstorming before
   implementation.
2. **Hand-rotation influence weight** — How strongly should hand rotation affect the pole direction relative to the
   positional baseline? Should this be a fixed ratio, pose-dependent, or configurable?
3. **Shoulder correction mapping** — Should shoulder adjustments use a simple proportional mapping, a set of
   artist-tunable curves, or a procedural bone-space approach? The choice affects configurability and tuning cost.
4. **Optional chest pose** — Should the "hands covering the chest" pose be included in the initial delivery or deferred
   to a follow-up iteration?
5. **Two-arm coordination** — Should the two arms share any state (for example a symmetric-pose bias) or remain fully
   independent?

## References

- @game/assets/characters/reference/female/reference_female.tscn
- @specs/characters/001-neck-spine-ik/index.md
- @specs/testing/002-visual-verification-scope/index.md
- @specs/characters/000-character-skeleton/index.md
