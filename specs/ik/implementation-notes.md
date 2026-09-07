---
id: IK-NOTES
title: IK Implementation Notes
---

# IK Implementation Notes

## Parent Specification

Child guidance under [IK: VRIK System](index.md).

Implementation guidance for `planner`, `coder`, and `reviewer`; the linked feature and child contracts are normative.

## User Requirements

- Full-body posing and reusable player/NPC IK outcomes follow the [IK user requirements](index.md#user-requirements).
- Upper-body motion and authority follow [IK-001](001-neck-spine-ik/index.md#user-requirements),
  [IK-002](002-arm-shoulder-ik/index.md#user-requirements), [IK-005](005-target-pipeline/index.md#user-requirements),
  and [RIG-002](../rigging/002-forearm-twist/index.md#user-requirements).
- Both roles' lower-limb outcomes follow [IK-003](003-leg-feet-ik/index.md#user-requirements) and its leg-feet and
  coordinator contracts.

## Technical Requirements

Apply the [IK technical requirements](index.md#technical-requirements), including TR5: IK-003 child contracts supersede
lower-limb-only notes without changing unrelated spine or player-VRIK guidance. Normative dependencies are:

- [IK-001 local neck-spine authoring](001-neck-spine-ik/index.md#technical-requirements), including per-template
  skeleton bindings and verification without a shared CCDIK scene.
- [IK-002 arm/shoulder contracts](002-arm-shoulder-ik/index.md#technical-requirements),
  [IK-005 hand authority](005-target-pipeline/index.md#technical-requirements), and
  [RIG-002 forearm twist](../rigging/002-forearm-twist/index.md#technical-requirements).
- [IK-003 production composition](003-leg-feet-ik/index.md#technical-requirements), including TR7–10, with generic
  resolution and sole actuation in the [Leg-Feet IK Contract](003-leg-feet-ik/leg-feet-ik-contract.md), and role-neutral
  production coordination in the
  [Contact-Lock Coordinator Contract](003-leg-feet-ik/contact-lock-coordinator-contract.md).

Required implementation, production integration, and validation remain mandatory under these contracts.

## TwoBoneIK3D Configuration

- Set all bone names explicitly. The solver requires `root_bone_name`, `middle_bone_name`,
  and `end_bone_name`. Missing `middle_bone_name` causes the solver to run with no visible
  effect and no error.
- Use canonical humanoid solver endpoints: arm chain is
  `LeftUpperArm → LeftLowerArm → LeftHand` (right-arm equivalents for right side).
  RIG-002 adds `LeftForearmTwist`/`RightForearmTwist` as deform-only chain bones, not solver-chain bones or
  endpoints; the hand remains the solver end bone. Shoulder, hips, and neck bones are needed for the body-space
  frame used by pole controllers.
- Bind target and pole nodes by path. Store target markers inside the test scene to keep
  paths stable (see Test Scene Self-Containment Rule in TEST-002).

## Skeleton Modifier Ordering

- `IKModifier3D` subclasses (including `TwoBoneIK3D`, `CCDIK3D`, and custom
  `SkeletonModifier3D` controllers) must be **direct children** of the `Skeleton3D`.
  Nesting under `Node3D` containers prevents them from running.
- Godot executes skeleton modifiers in child order. Place custom controllers (for example
  the pole-target driver) **before** the IK solver nodes so that inputs are updated
  before the solver runs.
- `ForearmTwistModifier` runs after hand-copy modifiers that establish final hand pose, so it consumes rather
  than competes with arm IK. It writes only the twist helpers and never originates or alters the authoritative
  hand pose. It runs before the optical-last finger-retargeting modifier; see
  [RIG-002: Forearm Twist](../rigging/002-forearm-twist/index.md).
- The [IK-005 authority adapter](005-target-pipeline/index.md#technical-requirements) submits same-pass hand authority
  after canonical hand/copy/animation work and immediately before forearm twist (TR23–26).
- For IK-003 lower limbs, the specific [leg-feet ordering](003-leg-feet-ik/leg-feet-ik-contract.md) supersedes
  lower-limb-only guidance: `sync → provider → hip reconciliation (when present) → lower-leg consumers`. `CharacterIK`
  performs generic provider resolution and sole actuation.
- These are relative dependencies, not a contiguous lower-limb block. Preserve authored neck/arm/hand-copy order and
  relative leg-consumer order. Sync precedes the pre-IK/provider stage; provider resolution precedes participating
  hip/leg consumers. Twist precedes leg consumers; the end/compensation stage follows solve/twist and precedes optical
  finger retargeting, which remains last.

The IK-005 TR26 versus RIG-002 TR7 hand-fallback disagreement remains subject to the
[RIG-002 evidence boundary](../rigging/002-forearm-twist/index.md#verification-and-evidence). This ordering guidance
does not choose a stale/invalid-hand fallback interpretation.

## Lower-Limb Convention Alignment

- IK-003 must follow IK-002 naming and role conventions: per-side
  `SkeletonModifier3D` controller instances before per-side solver instances.
- For lower-limb photobooth verification, use
  `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn` as the test-scene
  basis.
- The legacy lower-limb `BoneAttachment3D` hips-override harness, required pose matrix,
  and C# scene-loading integration test remain mandatory, fixture-only deferred
  verification artefacts; see [Deferred And Unaccepted
  Requirements](003-leg-feet-ik/test-setup-contract.md#deferred-and-unaccepted-requirements).
  IK-003 prohibits production lower-limb pelvis or body intervention. Do not use or
  recreate this harness in production implementation; deliver it only in its deferred
  verification fixture.

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
- Exposes optional fallback provider properties for head and hands.
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
3. The provider abstraction applies uniformly to configured limbs, including
   lower-limb providers through the generic resolution boundary.
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
2. `XRHandPoseTargetProvider` extends `IKTargetIntentProvider` and derives its target transform from the per-side
   runtime hand-pose source (`IXRHandPoseSource`, XR-002). The controller is the default committed hand-pose source;
   when the global hand-pose mode commits `Optical` (XR-002), the provider transparently serves the calibrated
   optical wrist — with freeze-on-loss retention — at the same fallback-source seam without changing the provider
   contract. It supersedes the earlier controller-only `XRControllerTargetProvider` seam, which was removed.
3. `PlayerVRIK` exposes optional fallback intent provider properties (for example
   `LeftHandFallbackIntentProvider`, `RightHandFallbackIntentProvider`, `HeadFallbackIntentProvider`).
4. `XRHandPoseTargetProvider` exposes a `LimbSide` property and resolves the corresponding
   runtime hand-pose source through XR services instead of receiving controller nodes
   from `PlayerVRIK`.
5. For upper-body XR target following, `ally_player.tscn` may wire head and hand fallback providers to the appropriate
   character IK properties and assigns each provider side explicitly. Lower-limb production wiring follows the
   [IK-003 child contracts](003-leg-feet-ik/index.md#technical-requirements).
6. Generic fallback-provider selection is owned by `CharacterIK`; `PlayerVRIK` does not
   distribute XR runtime or `XRManager` to XR fallback providers.
7. **XR target providers resolve required XR services themselves** via the global service
   resolution (see [CORE-004: Global Service Resolution](../core/004-global-service-resolution/index.md)).
   For example, `XRHandPoseTargetProvider` resolves `XRManager` via
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

### Validation Expectations

- **Provider override:** When a provider is assigned to any limb property, the VRIK
  must read the returned `IKTargetIntent` and drive the target body to the provider's
  world-space transform.
- **Influence gating:** When provider desired influence is 0, all corresponding side
  effects must be disabled.
- **XR fallback:** With head or hand fallback providers wired in `ally_player.tscn`, the system behaves as if the
  relevant XR source (controller by default; optical per XR-002) is active when no custom provider overrides it.
- **Scene wiring (upper-body XR):** `ally_player.tscn` may populate head and hand modifier groups so provider influence
  gates direct solvers and side-effect modifiers without duplicate per-limb exports. Required player and NPC lower-limb
  composition and validation follow the [IK-003 production contracts](003-leg-feet-ik/index.md#technical-requirements).
- **Side resolution:** XR fallback providers must prove left/right controller selection from
  their own `LimbSide` setting rather than a manually assigned source node.
- **No self-follow:** Confirm that target bodies never use themselves as follow sources;
  the fallback provider must be the final source in all normal cases.

## Generic Provider Resolution and Lower-Limb Ordering

`CharacterIK` is a generic provider resolver and target/modifier actuator, not a locomotion or contact-policy owner.

- `CharacterIK` resolves the explicit provider before the fallback provider. Existing providers retain terminal
  zero-influence semantics by default. A provider that cannot apply explicitly refuses through the generic resolution
  API, allowing an ordinary or fallback provider to resolve the limb.
- An ordered composite uses authored first-to-last order and selects the first applicable finite value-only resolution;
  numeric priorities are not used.
- Providers do not mutate targets, gates, modifiers, root, hips, or skeleton. Only `CharacterIK` applies the selected
  target transform, activity gate, and modifier influence.
- The Phase 1 side-specific, evidence-owning provider is a per-foot provider. Invalid, non-finite, stale, cross-side,
  or missing evidence must refuse rather than invoke special `CharacterIK` behaviour.
- `CharacterIK` repairs the lower-limb modifier order idempotently on initialisation or retry.
- Gate A test-only native provider-pipeline control and visual evidence are accepted. Gate B has passed only as
  disposable, test-only fixture evidence; neither establishes production delivery.
- Production lower-limb provider resolution, coordination, composition, integration, and validation follow the
  [Leg-Feet IK Contract](003-leg-feet-ik/leg-feet-ik-contract.md) and
  [Contact-Lock Coordinator Contract](003-leg-feet-ik/contact-lock-coordinator-contract.md), under IK-003 TR7–10.
  These role-neutral contracts apply to player and NPC compositions without transferring contact or locomotion policy
  to `CharacterIK` or introducing terrain/pelvis authority.

## Acceptance Criteria

Acceptance must cover user-visible outcomes and technical delivery at the linked feature and child layers:

- **Upper body:** [IK-001 acceptance](001-neck-spine-ik/index.md#acceptance-criteria),
  [IK-002 acceptance](002-arm-shoulder-ik/index.md#acceptance-criteria),
  [IK-005 acceptance](005-target-pipeline/index.md#acceptance-criteria), and
  [RIG-002 acceptance](../rigging/002-forearm-twist/index.md#acceptance-criteria).
- **Lower limbs:** IK-003 [user acceptance](003-leg-feet-ik/index.md#user-requirement-acceptance) and
  [technical acceptance](003-leg-feet-ik/index.md#technical-requirement-acceptance), with
  [leg-feet traceability](003-leg-feet-ik/leg-feet-ik-contract.md#acceptance-traceability) and
  [coordinator traceability](003-leg-feet-ik/contact-lock-coordinator-contract.md#acceptance-traceability).
- **Evidence scope:** [Gate A/B evidence](003-leg-feet-ik/test-setup-contract.md#evidence-status) remains fixture-only;
  [deferred verification requirements](003-leg-feet-ik/test-setup-contract.md#deferred-and-unaccepted-requirements)
  stay mandatory and unaccepted until their own criteria are met. Fixture acceptance does not substitute for production
  acceptance.

## References

- @specs/character/001-character-skeleton/index.md
- @specs/ik/001-neck-spine-ik/index.md
- @specs/ik/002-arm-shoulder-ik/index.md
- @specs/ik/003-leg-feet-ik/index.md
- @specs/core/004-global-service-resolution/index.md
- @specs/xr/001-xr-manager/index.md
- @specs/xr/002-optical-hand-tracking/index.md
- @specs/rigging/002-forearm-twist/index.md
- @game/src/IK/CharacterIK.cs
- @game/src/IK/PlayerVRIK.cs
- @game/src/IK/PlayerVRIKStartupBinder.cs
- @game/src/IK/IKTargetIntentProvider.cs
- @game/src/IK/XRHandPoseTargetProvider.cs
- Godot 4.6 documentation —
  [TwoBoneIK3D](https://docs.godotengine.org/en/stable/classes/class_twoboneik3d.html)
