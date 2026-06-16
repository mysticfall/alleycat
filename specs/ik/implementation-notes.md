---
id: IK-NOTES
title: IK Implementation Notes
---

# IK Implementation Notes

## Parent Specification

Child guidance under [IK: VRIK System](index.md).

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

## Character IK Architecture

### General Character IK Class

- A reusable concrete `CharacterIK` class supports NPC characters and any non-XR-driven
  humanoid IK without implying VR-specific input.
- Operates independently of input source; accepts `IKTargetIntentProvider` for limb targets.
- Provides the core IK modifier orchestration without XR-specific logic.
- Modifier influence wiring uses per-limb modifier groups only; each group includes the direct
  solver and any side-effect modifiers for that body part or side.

### Player XR Integration Layer

- `PlayerVRIK` extends `CharacterIK` with player-specific XR integration.
- Exposes optional fallback provider properties for head, hands, and feet.
- Falls back gracefully when XR is unavailable.

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

- `PlayerVRIK` performs one-time world-scale calibration when it binds to XR origin and
  camera services.
- Calibration uses the ratio between avatar rest viewpoint-local head height and XR
  camera local head height.
- If either height is near zero, calibration is skipped and the current XR world
  scale is retained.

## IKTargetIntentProvider Architecture

### General Provider Contract

**User Requirements:**

1. Head, hands, feet, and other limbs must all support provider-driven target and
   influence control in the same manner.
2. All IK modifiers controlled directly or indirectly by character IK must be tied to
   corresponding provider influence.

**Technical Contract:**

1. `IKTargetIntentProvider` is a Godot `Node` global class so provider nodes can be
   assigned through exported character IK inspector properties.
2. Providers always return an `IKTargetIntent` containing an explicit world-space
   target transform and desired influence for the corresponding IK modifier.
3. The provider abstraction applies uniformly to: head, left hand, right hand, left
   foot, right foot, and any additional limbs configured in the character IK setup.
4. When provider desired influence is 0, the corresponding IK modifier and all
   side-effect modifiers must be deactivated.
5. Each limb side has a separate provider slot (for example left hand vs right hand).

### Provider Influence Gating

- Provider influence gates the corresponding IK modifier and its side effects.
- Example: right hand provider desired influence of 0 deactivates the right arm
  `TwoBoneIK3D`, right shoulder correction modifier, and any other right-hand-side
  modifiers.
- Influence propagation is deterministic and follows the modifier dependency graph.

### XR Fallback Provider

**User Requirements:**

1. XR hand-controller fallback must behave identically to the default case when no
   custom provider is assigned.

**Technical Contract:**

1. XR hand-controller fallback behaviour moves into an `IKTargetIntentProvider` subclass.
2. `XRControllerTargetProvider` extends `IKTargetIntentProvider` and derives target
   transform from XR controller state.
3. `PlayerVRIK` exposes optional fallback intent provider properties (for example
   `LeftHandFallbackIntentProvider`, `RightHandFallbackIntentProvider`, `HeadFallbackIntentProvider`).
4. `XRControllerTargetProvider` exposes a `LimbSide` property and resolves the corresponding
   XR controller hand-position node through XR services instead of receiving controller nodes
   from `PlayerVRIK`.
5. The `player.tscn` scene wires fallback providers to the appropriate character IK properties
   and assigns each provider side explicitly.
6. Generic fallback-provider selection is owned by `CharacterIK`; `PlayerVRIK` does not
   distribute XR runtime or `XRManager` to XR fallback providers.
7. **XR target providers resolve required XR services themselves** via the global service
   resolution (see [CORE-004: Global Service Resolution](../core/004-global-service-resolution/index.md)).
   For example, `XRControllerTargetProvider` resolves `XRManager` via
   `Game.Instance.GetService<XRManager>()` rather than receiving it as a constructor argument.
8. When no provider is assigned, the fallback provider is used if available; otherwise
   the character IK uses a safe idle state.
9. **No normal fallback uses the target body as its own follow source.** Such self-follow
   creates no-op behaviour and breaks VR physical limiting semantics.

### Provider Property Mapping

| Limb | Provider Property | IK Modifier Target |
|------|-------------------|-------------------|
| Head | `HeadTargetIntentProvider` | Neck-Spine IK (IK-001) |
| Left Hand | `LeftHandIKTargetIntentProvider` | Left Arm TwoBoneIK3D + shoulder correction (IK-002) |
| Right Hand | `RightHandIKTargetIntentProvider` | Right Arm TwoBoneIK3D + shoulder correction (IK-002) |
| Left Foot | `LeftFootTargetIntentProvider` | Left Leg TwoBoneIK3D (IK-003) |
| Right Foot | `RightFootTargetIntentProvider` | Right Leg TwoBoneIK3D (IK-003) |

### Validation Expectations

- **Provider override:** When a provider is assigned to any limb property, the VRIK
  must read the returned `IKTargetIntent` and drive the target body to the provider's
  world-space transform.
- **Influence gating:** When provider desired influence is 0, all corresponding side
  effects must be disabled.
- **XR fallback:** With fallback provider wired in `player.tscn`, the system behaves as
  if the XR controller is the source when no custom provider overrides it.
- **Scene wiring:** `player.tscn` must populate per-limb modifier groups so provider
  influence gates direct solvers and side-effect modifiers without duplicate per-limb exports.
- **Side resolution:** XR fallback providers must prove left/right controller selection from
  their own `LimbSide` setting rather than a manually assigned source node.
- **No self-follow:** Confirm that target bodies never use themselves as follow sources;
  the fallback provider must be the final source in all normal cases.

## Leg And Foot Provider Coexistence

- IK-003 defines a `FootTargetSyncController` that synchronises foot targets from
  animated foot transforms before each IK solve cycle.
- Provider-driven foot targets coexist with `FootTargetSyncController` through a
  staged pipeline:
  - **Stage 1**: `FootTargetSyncController` runs first to synchronise animation
    foot targets from animated foot transforms.
  - **Stage 2**: `CharacterIKFootProviderStage` evaluates the foot provider contract;
    when a provider is assigned and has non-zero influence, it overrides the
    animation-synced target.
  - **Stage 3**: When no foot provider is assigned or influence is 0, the
    animation-synced target from Stage 1 is preserved.
- This allows NPC characters to use animation-driven foot targets while player
  characters can override with provider-driven targets.

## References

- @specs/character/001-character-skeleton/index.md
- @specs/ik/001-neck-spine-ik/index.md
- @specs/ik/002-arm-shoulder-ik/index.md
- @specs/ik/003-leg-feet-ik/index.md
- @specs/core/004-global-service-resolution/index.md
- @specs/xr/001-xr-manager/index.md
- @game/src/IK/CharacterIK.cs
- @game/src/IK/PlayerVRIK.cs
- @game/src/IK/PlayerVRIKStartupBinder.cs
- @game/src/IK/IKTargetIntentProvider.cs
- @game/src/IK/XRControllerTargetProvider.cs
- Godot 4.6 documentation —
  [TwoBoneIK3D](https://docs.godotengine.org/en/stable/classes/class_twoboneik3d.html)
