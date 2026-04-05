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
| ------------------------------ | -------------------------- | ------------------------ | -------------------------------- |
| Arms lowered                   | Hands at sides             | Palms facing each other  | Laterally outward                |
| Arms raised forward            | Hands in front of body     | Palms facing each other  | Laterally outward                |
| Arms raised straight overhead  | Hands above head           | Palms facing each other  | Laterally outward                |
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

## Open Questions

1. **Pole-target interpolation strategy** — How should baseline directions interpolate between key poses? Options
   include spherical linear interpolation (slerp) between nearest key-pose directions, a weighted blend based on
   angular proximity, or a continuous analytical function. This should be resolved during brainstorming before
   implementation.
2. **Hand-rotation influence weight** — How strongly should hand rotation affect the pole direction relative to the
   positional baseline? Should this be a fixed ratio, pose-dependent, or configurable?
3. **Shoulder correction mapping** — Should shoulder adjustments use a simple proportional mapping, a set of
   artist-tunable curves, or a procedural bone-space approach? The choice affects configurability and tuning cost.
4. **Reference character skeleton topology** — Resolved. The reference character uses `SkeletonProfileHumanoid`; see
   [CHAR-000: Character Skeleton Profile](@specs/characters/000-character-skeleton/index.md) for the full profile
   details and bone naming convention.
5. **Optional chest pose** — Should the "hands covering the chest" pose be included in the initial delivery or deferred
   to a follow-up iteration?
6. **Two-arm coordination** — Should the two arms share any state (for example a symmetric-pose bias) or remain fully
   independent?

## References

- @game/assets/characters/reference/female/reference_female.tscn
- @specs/characters/001-neck-spine-ik/index.md
- @specs/testing/002-visual-verification-scope/index.md
- @specs/characters/000-character-skeleton/index.md
