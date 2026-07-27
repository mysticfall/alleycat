---
id: CTRL-001
title: Locomotion
---

# Locomotion

## Requirement

Define character movement and rotation control, animation-owned standing root motion, and permission sources that let
external systems gate locomotion behaviour.

## Goal

VR players and locomotive NPCs move responsively through one locomotion contract. Standing movement and physical yaw
follow coherent animation root motion without disturbing unrelated player pose states.

## User Requirements

1. Left-stick input drives immediate player movement and right-stick input drives precise, smooth rotation.
2. NPC navigation uses the same locomotion input contract without replacing tracker-driven player control.
3. Standing locomotion supports idle, forward and backward walking, smooth left and right walking arcs, short local
   rear/lateral correction, and ordinary unarmed left and right 90-degree in-place pivots.
4. Movement and rotation operate simultaneously without interference or double rotation.
5. Standing translation and physical yaw follow selected animation Root motion, with interruptible walking turns and
    held looped signed bilateral pivots.
6. Movement feels responsive and grounded through smooth walking direction changes, short local corrections, and
   bilateral deliberate pivots. NAV-001 owns the route policy for distant rear destinations; locomotion supplies the
    supported forward-arc and held looped signed-pivot responses without changing their root-motion response data or
    blend weights.
7. Unsupported poses gate movement while rotation remains available for the MVP.
8. Only `Walking` changes for standing locomotion; standing/crouching, kneeling, all-fours, hand, eye, and blink
   behaviour remains unchanged.
9. NPC route revisions, corner anticipation, braking, and drift correction remain continuous through existing walking
   blend trees without animation-phase resets or discrete corrective turns.
10. With zero locomotion input, NPCs and player-controlled characters using shared locomotion visibly hold an animated
    idle pose rather than the skeleton rest pose.

## Technical Requirements

### Core Locomotion Contract

1. `ILocomotion` retains `SetMovementInput(Vector2)` for unitless local lateral and forward intent and
   `SetRotationInput(Vector2)` for signed horizontal turn intent.
2. `CharacterLocomotion` implements `ILocomotive`, accepts movement and rotation together, and applies resulting
   translation and yaw through one coherent `MoveAndSlide` step.
3. In root-motion states, the component derives planar velocity from `AnimationTree` root motion and returns zero planar
   velocity when movement is not allowed.
4. Movement and rotation permission channels remain independent and aggregate permission sources separately.
5. The component uses template-authored exported references, not a hard-coded base-scene inheritance. It requires the
   root-level `AnimationTree` and `DynamicPhysicalRig`; consuming player scenes wire VRIK, pose-state permissions, and
   `PlayerController`.

### Animation-Owned Standing Motion

6. Movement or rotation intent enters existing `Walking`; no additional top-level locomotion state is required.
7. Only `Walking` in each NPC, player, and reference-male graph becomes a directional `AnimationNodeBlendTree`.
8. Each walking tree supplies the nine ANIM-003 roles: idle, forward walk, backward walk, left/right walk arcs,
   left/right sidesteps, and left/right 90-degree pivots. Both stationary pivot graph roles are required; it has no run,
   start/stop, combat, weapon, sad, hunched, raised-arm, or unused alternative branch.
9. `CharacterLocomotion` exposes explicit movement and signed-turn blend paths. Reference graphs use
   `parameters/States/Walking`; simple test rigs may use equivalent `parameters/Walking` paths.
10. While `Walking` is active, the component takes translation and yaw from one animation-tree root-motion sample,
    applies each permitted valid component once, and does not apply direct smooth yaw. Its `AnimationTree` resolves
    the exact `GeneralSkeleton:Root` root-motion track through the unique-name `GeneralSkeleton` binding. Corrected
    imported Root yaw maps directly to actor yaw, without a post-import coordinate, sign, or compensating-yaw
    conversion.
11. Smooth walking yaw and bilateral in-place yaw are animation owned for NPCs and players. Input may change walking
    direction without waiting for a clip to finish. Pivots are selected only for stationary deliberate facing changes,
    remain held and looped with the Root-yaw sign matching the requested turn, and release directly to route movement
    without a neutral or `Idle` interlude; moving changes use walk arcs. Runtime mirroring or synthetic yaw is not
    permitted; ANIM-003 may supply a fully processed derived mirror under the ANIM-001 provenance contract.
12. Non-finite root motion is ignored. Movement and rotation gates apply independently, and root motion is consumed only
     in `Walking` or an explicitly selected pose-state locomotion root-motion state. Loop-wrap sampling remains
     continuous and must not reset, suppress, or double-apply Root translation or yaw.
13. Root motion is the sole standing physical translation and yaw authority. Navigation influences blend intent only and
    does not add transform displacement, yaw, path snapping, or motion warping.
14. Movement and turn controls use one predictable mapping without cascaded navigation and locomotion gains. NPC
    supplementary correction remaps continuously around neutral without accumulating dead-zone error; player tracker
    input remains isolated.
15. The walk-arc regions interpolate coherently in every NPC, player, and reference-male graph without changing player
     control ownership. The pivot regions select and hold the matching looped signed 90-degree role only while
     stationary, preserve continuous Root yaw across every loop wrap, then exit near the target facing directly to route
     movement without a neutral or `Idle` interlude; they must not restart a pivot while stationary.
16. The graph exposes a rear/lateral diagonal blend using only `BackwardWalk` and the matching `SideStep` role. It is
    available to navigation for a local correction at or below 1 m and remains active until the correction exceeds or
    resolves below the 1.25 m release threshold. NAV-001 selects route-directed forward arcs and, only when unsuitable,
    held looped signed pivots for distant rear intent; it must not change this response data or graph blend weights.
    Threshold values are contract values, not tuning defaults.

### Permission Source API

17. `LocomotionPermissions` records `MovementAllowed` and `RotationAllowed` independently.
18. `ILocomotionPermissionSource` exposes `LocomotionPermissions`.
19. `LocomotionBase` exports `PermissionSourceNodes`, validates their interface, aggregates them with logical AND, and
    exposes `GetCurrentLocomotionPermissions()`.

### Player Controller Integration

20. `PlayerController` in `Control` reads XR controller sticks and forwards input to locomotion.
21. The player scene composes `PlayerController` and wires tracker input to character locomotion. AI navigation is not
    installed and cannot replace or inject that input through player composition.

### Locomotion Animation-State Override

22. `IPoseState` defines `GetLocomotionStateTarget(PoseStateContext)`.
23. The pose-state machine implements `ILocomotionAnimationSource`, which `CharacterLocomotion` queries.
24. Standing preserves default fallback; `AllFours` continues to return `(AllFours, AllFoursForward)`.

### Idle and Turn Configuration

25. `CharacterLocomotion` supports configurable rotation sensitivity, proportional animation-turn response, movement
    and turn blend paths, and `IdleAnimationStateName`.
26. `IdleAnimationStateName` defaults to `Idle`, which receives the initial transition automatically. Player pose rigs
    may use `StandingCrouching`; generic NPC roots use `Idle`. On zero locomotion input, the fallback resolves against
    the runtime character `AnimationPlayer` and skeleton, and applies an animated non-rest pose for every shared
    locomotion consumer.

## In Scope

- Shared NPC and player movement and rotation contracts, directional standing Root motion, and animation-owned yaw.
- Walking blend parameters, interruptible reversible transitions, and continuous NPC control mapping.
- Pose permissions and overrides, plus tracker-driven player composition isolated from NPC navigation.

## Out Of Scope

- Full XR mapping beyond stick reading, haptics, networking, and platform certification.
- Bespoke kneeling or all-fours transitions and changes outside existing locomotion override paths.
- Teleportation and navigation path policy, which is owned by [NAV-001](../../nav/001-npc-navigation/index.md).
- Snap turning and snap-specific configuration; it requires a new player-specific feature.
- Final tuning constants, full motion matching, motion warping, and additional animation roles.

## Acceptance Criteria

### User Requirement Acceptance

1. Player playtesting confirms left-stick movement, smooth right-stick rotation, and simultaneous translation and
   turning without snap turns.
2. Player and NPC scenarios demonstrate the ANIM-003 forward, backward, walk-arc, sidestep, and bilateral pivot roles
   with coherent animation-owned translation and yaw.
3. Walking direction changes and short local rear/lateral diagonal corrections remain smooth. For a destination behind
    at a distance greater than 1.25 m, NAV-001 can use route-directed forward arcs and only conditionally use a held
    looped signed pivot. Independent visual review confirms each stationary pivot has opposing 90-degree Root yaw,
    negligible translation, remains held until release near the target, then transitions directly to route movement
    without a neutral or `Idle` interlude or a restart at rest.
4. Movement and rotation permissions independently suppress only their corresponding root-motion component.
5. Player regressions preserve non-walking pose, hand, eye, and blink behaviour and exclude NPC navigation from player
   composition.
6. NPC playtesting confirms continuous corner anticipation, braking, drift correction, and route replacement through
   `Walking`, without phase resets, snapping, or turn amplification.
7. With zero locomotion input, NPC and player scenarios visibly retain an animated idle pose rather than a T-pose or
   other skeleton rest pose.

### Technical Requirement Acceptance

1. Contract and composition tests preserve `ILocomotion`, `ILocomotive`, permission APIs, exported references,
   animation-owned turning, and tracker-driven `PlayerController` wiring.
2. Graph inspection proves only `Walking` changed and binds precisely the nine ANIM-003 roles through explicit paths,
   including both stationary pivot branches and no excluded role.
3. Root-motion tests prove one valid, permission-gated sample from `GeneralSkeleton:Root` supplies translation and yaw,
    each applied once through `MoveAndSlide` without direct smooth yaw or double rotation. They prove the unique-name
    track binding, direct corrected-imported-Root-yaw-to-actor-yaw mapping, and continuous single application across
    loop wraps.
4. Focused checks cover simultaneous translation and yaw, walk-arc direction changes, both diagonal correction signs,
    the 1 m entry and 1.25 m release thresholds, and both pivots. They reject runtime mirroring, synthetic yaw,
    stationary-pivot restarts, using a pivot for a moving change, distant rear backward travel, a neutral or `Idle`
    interlude between pivot release and route movement, loop-wrap Root-motion discontinuity, and navigation changes to
    root-motion response data or graph blend weights.
5. State tests prove intent enters `Walking`, configurable idle fallback, NPC `Idle`, optional player
    `StandingCrouching`, runtime `AnimationPlayer` and skeleton resolution, an applied animated non-rest idle pose,
    and preserved `AllFours` override behaviour for NPC and player consumers.
6. Scene and graph regressions prove no player `LocomotiveNavigation` and no changes outside `Walking` or existing
   locomotion-override paths.
7. Control tests prove finite single-mapping input, continuous near-neutral correction, no cascaded turn amplification,
   and root motion as the sole physical authority.
8. Graph tests prove coherent right walk-arc interpolation while preserving tracker control, direct-transform navigation
   baselines, and unrelated graph regions.

## References

- [Project Specifications Index](../../index.md)
- [CTRL: Player Character Control System](../index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [ANIM-003: Standing Locomotion Catalogue](../../animation/003-standing-locomotion-catalogue/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../ik/004-vrik-pose-state-machine/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
