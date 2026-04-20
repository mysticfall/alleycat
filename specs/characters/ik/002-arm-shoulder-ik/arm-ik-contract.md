---
title: Arm IK Contract
---

# Arm IK Contract

## Purpose

Define the arm-only contract for IK-002: `TwoBoneIK3D` arm solving, elbow pole-target prediction, and pose
independence constraints.

## Contract Scope

- One `TwoBoneIK3D` chain per arm (left and right), solving shoulder-to-hand.
- Pole-target prediction from head and hand targets.
- Guarded elbow pole-offset compression safeguard with tunable gates and floors.
- Shoulder correction execution in `ArmIKController` before IK solve using anatomical decomposition into elevation and
  protraction components in body-basis space (algorithm details in shoulder contract).
- Baseline pose mapping plus hand-rotation adjustment (detailed in the [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)).
- **Resource-driven anchor configuration**: pole-anchor data authored via editor bake workflow and loaded at runtime from
  `ArmPoleAnchorSetResource` assets.
- Behaviour consistent across upright and non-upright body orientations.

## Resource-Driven Anchor Configuration

### Resource Data Structure

#### ArmPoleAnchorResource (Per-Anchor Entry)

Each anchor entry represents a pole-direction sample for a specific authoring pose and contains:

| Property | Type | Description |
|----------|------|-------------|
| `ArmDirBody` | `Vector3` (unit vector) | Arm direction sample in body-basis space (normalised). |
| `PoleIntentBody` | `Vector3` (unit vector) | Desired elbow pole intent direction in body-basis space (normalised). |
| `ReachRatio` | `float` | Normalised reach ratio relative to rest-arm length (0.0 = fully folded, 1.0 = full extension). Values may exceed 1.0 for authored overreach poses. |

#### ArmPoleAnchorSetResource (Collection)

The resource asset contains a collection of anchor entries:

| Property | Type | Description |
|----------|------|-------------|
| `Anchors` | `Array[ArmPoleAnchorResource]` | Ordered list of anchor entries indexed by authoring pose. |

### Runtime Consumption

- `ArmIKController` exposes an exported `PoleAnchorSet` property of type `ArmPoleAnchorSetResource`.
- At runtime, the controller loads and interpolates anchor data from the assigned resource.
- **No mirror toggle exists**: when the same resource is assigned to both left and right arms, the resulting
  pole directions are symmetric (left arm mirrors automatically via the side-aware sign convention in
  body-basis space).
- To achieve asymmetric behaviour, a different (pre-mirrored) resource must be authored and assigned.

### Authoring Bake Workflow

The authoring workflow proceeds as follows:

1. **Authoring Scene**: Open the dedicated authoring scene at `res://assets/characters/ik/arm_pole_anchor_set_authoring.tscn`.
   This scene contains `ArmPoleAnchorAuthoringPose` marker nodes placed on a character in T-pose, encoding the desired
   hand position and corresponding pole direction for each authoring pose.
2. **Authoring Root**: The scene's root node (`ArmPoleAnchorSetAuthoringRoot`) contains:
   - The marker subtree acting as the input pose container (referenced via `PoseContainerPath`).
   - An exported `OutputResourcePath: String` defining the file path for the baked asset. Default output is
     `res://assets/characters/ik/arm_ik_target_set.tres`.
3. **Bake Command**: Set the `BakeNow` property to `true` on the authoring root node:
   - Scans the designated pose container for all `ArmPoleAnchorAuthoringPose` children.
   - Extracts each pose's arm direction and pole intent in body space and computes the normalised reach ratio.
   - Serialises the data into a new `ArmPoleAnchorSetResource` asset.
   - Saves the asset to the specified `OutputResourcePath`.
   - Sets `BakeNow` back to `false` automatically.
4. **Output**: The baked `.tres` file is saved to the configured output path. The canonical resource used by player
   and test scenes is `res://assets/characters/ik/arm_ik_target_set.tres`.

### Ownership Mapping

The authoring root node enforces explicit ownership:

- **Input Pose Container**: The subtree rooted at the authoring root contains all marker nodes that feed the
  bake process. Markers outside this subtree are not included.
- **Output Resource Path**: The `OutputResourcePath` exported property on the authoring root determines where the baked
  resource is saved. This path is absolute or relative to the project `res://` folder.

This one-to-one mapping ensures that:
- The same authoring root always produces the same output resource.
- There is no ambiguity about which markers contributed to which resource.

## Mechanism

Each arm uses a Godot `TwoBoneIK3D` node configured for the upper-arm → lower-arm → hand chain.

The key algorithmic requirement is elbow pole-target prediction that keeps bend direction natural across key poses.

## Pole-Target Prediction

Prediction is composed of two layers:

1. **Baseline Pole Direction** from hand position relative to head, which varies continuously across the unit sphere of arm directions in body basis.
2. **Hand-Rotation Adjustment** applied on top of the baseline.

### Compression Safeguard For Pole Offset

Pole-target placement must include a guarded distance floor for compressed folded-arm poses.

#### Required Tunables

- **Pole Offset Ratio**: scales current arm length into a base pole offset.
- **Pole Offset Minimum**: hard minimum floor for baseline pole offset.
- **Compression Threshold**: maximum clamped compression ratio that enables compressed-floor enforcement.
- **Compression Margin**: additive margin applied on top of half rest-arm length for compressed floor enforcement.

Exact numeric values are intentionally tunable.

#### Required Computation And Gates

Per arm, each frame:

1. **Apply shoulder correction** (via `ApplyShoulderCorrectionPreIK`) to obtain the post-correction shoulder position.
2. Compute `currentArmLength = distance(hand, postCorrectionShoulder)`.
3. Compute `baseOffset = max(currentArmLength * poleOffsetRatio, poleOffsetMinimum)`.
4. Compute `compressionRatio = clamp(currentArmLength / restArmLength, 0.0, 1.0)`.
5. Determine folded gate from body-local vertical relation using the **post-correction shoulder position**: folded gate is true when `hand Y <= shoulder Y` in body basis (equivalently `(hand - shoulder) · bodyUp <= 0`).
6. Activate compressed-floor enforcement only when:
   - `compressionRatio <= compressionThreshold`, and
   - folded gate is true.
7. When active, compute `compressedFloor = max(poleOffsetMinimum, restArmLength * 0.5 + compressionMargin)` and
   enforce `finalOffset >= compressedFloor`.
8. When inactive, `finalOffset = baseOffset`.

#### Non-Invasive Boundary

The safeguard only constrains pole-target placement distance. It must not alter:

- hand-target position inputs,
- shoulder-correction computation path,
- hand-rotation correction logic, or
- `TwoBoneIK3D` chain membership and solve targets.

### Key Hand Poses

When hand rotation matches the baseline below, baseline pole direction is laterally outward (away from body midline),
except where noted.

| Pose | Hand Position | Baseline Hand Rotation | Baseline Pole Direction |
| ---- | ------------- | ---------------------- | ----------------------- |
| Arms Lowered | Hands at sides | Palms facing each other | Laterally outward |
| Arms Raised Forward | Hands in front of body | Palms facing each other | Laterally outward |
| Arms Raised Straight Overhead | Hands above head | Palms facing forward | Laterally outward |
| Arms Raised To Each Side | Hands extended to sides | Palms facing downward | Posterior (toward the back) |
| Hands Behind The Head | Hands behind head | Palms facing forward | Laterally outward |
| Hands Covering The Chest | Hands in front of chest | Palms facing backward | Laterally outward (optional) |

The "Hands Covering The Chest" pose remains optional and may be deferred.

### Baseline Pole Direction Continuity

The baseline pole-direction function of arm direction in body basis must be continuous (C0) across the unit sphere
of arm directions in body basis, ensuring that small changes in hand position produce correspondingly small, visually smooth
changes in elbow pole-target direction.

#### User Outcome

Small changes in hand position and arm direction must produce correspondingly small, visually smooth changes in elbow
pole-target direction. The elbow must never visibly "jump" or "snap" between pose regions during continuous hand motion.

#### Technical Contract

The baseline pole-direction prediction must satisfy the following constraints:

- **No hard-threshold fallback branches.** Output at branch boundaries must not differ materially from nominal
  branch output at the switching threshold.
- **No `abs()`-style midline reflections.** Reflections using absolute value to map across the body midline create
  derivative discontinuities where `armDir · lateral ≈ 0`. Such constructions are not permitted.
- **Smooth degenerate case handling.** When desired pole direction is near-parallel to arm direction, weighting must
  smoothly bias toward an alternative direction rather than snapping.
- **Distributed transition bands.** Narrow smoothstep or piecewise transition bands that concentrate most directional
  change into a small input range are not compliant. Transitions must be spread smoothly across relevant arm-direction ranges
  so that reasonable hand-motion speeds do not produce perceptible "snaps".

#### Relationship To Key Poses

The six key poses and their designated baseline pole directions (as specified in "Key Hand Poses") remain authoritative
for the value of the baseline at those poses. Continuity is an additional requirement layered on top of those point
values. The baseline does not have to pass exactly through marker-designated values; it must approximate them closely
at the key poses and interpolate continuously elsewhere.

### Hand-Rotation Adjustment

Actual pole direction is derived by blending or offsetting the baseline with a hand-rotation contribution.

The hand-rotation adjustment uses the runtime-authored hand pose rotation (from XR hand target driving) around the
shoulder-to-hand axis, compared against an interpolated reference rotation from key pose markers, to rotate the elbow
pole target and shift the bend-plane direction. Full algorithmic details, parameterisation, and acceptance criteria are defined in the
[Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md).

## Implementation Notes

### TwoBoneIK3D Configuration

Set all three bone names explicitly:

- `root_bone_name` (upper arm)
- `middle_bone_name` (lower arm)
- `end_bone_name` (hand)

Missing any required bone name (especially `middle_bone_name`) can produce no visible solver effect without error.

### ArmIKController

`ArmIKController` extends `SkeletonModifier3D` and is decorated with `[GlobalClass]`.

It computes shoulder correction and pole-target positions in `_ProcessModificationWithDelta`, and must run before
`TwoBoneIK3D` nodes.

### Body Reference Frame

Derive body-local basis each frame from skeleton landmarks:

- **Up**: `Hips` → `Neck`
- **Right**: `LeftShoulder` → `RightShoulder`, orthonormalised against up
- **Forward**: cross product of right and up

This frame keeps behaviour pose-independent.

### Baseline Hand Rotation Encoding

Verification pose markers (`Marker3D`) encode target hand position and baseline hand rotation per pose.

`CopyTransformModifier3D` at the end of the stack copies hand-target rotation to hand bones so visual orientation matches
marker orientation.

### Phased Delivery

Initial phase may deliver positional baseline prediction first. Hand-rotation adjustment, now fully specified in the
[Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md), may be implemented in a subsequent
phase.

## Acceptance Criteria Coverage

This contract defines details for:

- AC-01
- AC-02
- AC-03
- AC-04
- AC-05
- AC-17
- AC-18
- AC-19
- AC-20
- AC-21
- AC-22
- AC-23
- AC-24
- AC-25 (resource-driven anchor configuration)
- AC-26 (normalised anchor representation)
- AC-27 (bake workflow)
- AC-28 (authoring ownership mapping)
- AC-29 (symmetry without runtime mirror)
- AC-30 (visual verification with resources)
- AC-31 (C# integration tests for resources)

Source-of-truth criteria wording is maintained in [IK-002 Overview](index.md#acceptance-criteria).

## References

- [IK-002 Overview](index.md)
- [Shoulder Correction Contract](shoulder-adjustment-contract.md)
- [Hand-Rotation Elbow Correction Contract](hand-rotation-correction-contract.md)
