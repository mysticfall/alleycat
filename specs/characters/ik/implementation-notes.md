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

### Target State Provider Contract

**Player-Visible Behaviour:**

- Hand IK target bodies follow XR controller transforms by default when bound.
- Scene-authored or runtime systems may redirect a hand follow target without modifying
  the `Body/Hands/IHand` hierarchy.

**Technical Contract:**

- `IKTargetStateProvider` is a Godot `Node` global class so provider nodes can be
  assigned through exported `PlayerVRIK` inspector properties.
- Providers always return an `IKTargetState` containing an explicit world-space
  target transform and desired influence for the corresponding IK modifier.
- `PlayerVRIK` exposes nullable per-hand provider properties. When a provider is
  assigned, the hand target body follows the provider state; otherwise the XR
  controller `HandPositionNode` remains the fallback source when bound.
- If neither a provider nor an XR fallback source is available, `PlayerVRIK` may
  use a private safe legacy/unbound fallback value. This fallback is internal to
  `PlayerVRIK`; it is not a provider state concept and must not treat world
  origin as a meaningful hand target.
- When the corresponding `TwoBoneIK3D` modifier reference is available, `PlayerVRIK`
  applies provider/fallback desired influence to that modifier.
- **No normal fallback uses the hand IK target body as its own follow source.**
  Such self-follow creates no-op behaviour and breaks VR physical limiting semantics.

### Validation Expectations

- **Default XR fallback:** With no provider assigned, hand targets follow XR
  controller `HandPositionNode` transforms via the standard XR binding pipeline.
- **Provider override:** When a provider is assigned to a hand property,
  `PlayerVRIK` must read the returned `IKTargetState` and drive the hand target
  body to the provider's world-space transform.
- **Influence application:** When the corresponding `TwoBoneIK3D` modifier
  reference exists on the affected limb, `PlayerVRIK` applies the desired
  influence from the provider state (or fallback XR state) to that modifier.
- **No self-follow:** Confirm that the hand target body never uses itself as a
  follow source when providers are absent; the XR controller fallback must be
  the final source in all normal cases.

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
