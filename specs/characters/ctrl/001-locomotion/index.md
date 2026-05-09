---
id: CTRL-001
title: Locomotion
---

# Locomotion

## Requirement

Define VR character movement and rotation control, including the
permission-source API that enables external systems to gate locomotion
behaviour.

## Goal

Players experience responsive, grounded character locomotion via XR
controller stick input, with simultaneous movement and rotation that
respects pose-based permission gating.

## User Requirements

1. Left stick input drives immediate character movement.
2. Right stick input drives precise rotation control.
3. Movement and rotation operate simultaneously without interference.
4. Rotation supports both snap turn (discrete configurable angles) and
   smooth turn (continuous rotation).
5. Default scheme: left stick movement, right stick rotation.
6. Character movement feels responsive and grounded.
7. Movement is restricted when pose does not support locomotion (for
   example, kneeling, sitting, all-fours transition).
8. Rotation remains available across all poses for MVP.

## Technical Requirements

### Core Locomotion Interface

1. Define `ILocomotion` interface:
   - `SetMovementInput(Vector2)` — unitless locomotion intent, not velocity.
     In root-motion states, input is animation blend factor.
   - `SetRotationInput(Vector2)` — right stick rotation control.

2. Implement `CharacterLocomotion` class:
   - Accepts movement and rotation input as `Vector2`.
   - Applies movement via `MoveAndSlide`.
   - Derives velocity from root motion output in locomotion states.
   - Returns zero planar velocity when movement is not allowed.
   - Aggregates permission sources separately for movement vs rotation.

3. Add `CharacterLocomotion` as child of root node in `player.tscn`.

### Permission Source API

4. Define `LocomotionPermissions` record:
   - `MovementAllowed`: bool
   - `RotationAllowed`: bool

5. Define `ILocomotionPermissionSource` interface:
   - `LocomotionPermissions` property.

6. `LocomotionBase` must:
   - Export `PermissionSourceNodes` for scene-authored wiring.
   - Validate nodes implement `ILocomotionPermissionSource`.
   - Aggregate permissions using logical AND.
   - Expose `GetCurrentLocomotionPermissions()`.

### Player Controller Integration

7. Implement `PlayerController` class in `Control` namespace:
   - Reads XR controller stick input.
   - Forwards input to locomotion component.

8. Add `PlayerController` as child node in `player.tscn`.

### Animation Integration

9. Wire animation blend parameter to `parameters/States/Walking/blend_position` when the
   reference character uses the hand-pose blend tree (BODY-001). Legacy/simple state-machine-only test rigs may
   continue to use `parameters/Walking/blend_position`.
10. Consume root motion only when top-level state is Walking or active override.

### Configurable Parameters

11. Support configurable:
    - Rotation speed multiplier
    - Snap turn angle increment
    - Snap turn cooldown duration
    - Smooth turn sensitivity

### Locomotion Animation-State Override

12. `IPoseState` defines `GetLocomotionStateTarget(PoseStateContext)`.
13. Pose-state-machine implements `ILocomotionAnimationSource`.
14. `CharacterLocomotion` queries `ILocomotionAnimationSource` providers.
15. Standing pose family preserves default fallback behaviour.
16. AllFours Crawling sub-state returns target pair `(AllFours, AllFoursForward)`.

## In Scope

- Left stick movement and right stick rotation control
- Simultaneous movement and rotation without interference
- Snap turn and smooth turn modes with configurable parameters
- Root motion consumption from Walking or active override state
- Pose-based locomotion permission gating
- Scene-authored permission source wiring
- Animation blend parameter wiring
- StandingCrouching to Walking state transitions

## Out Of Scope

- Full XR controller input mapping beyond stick reading
- Haptic feedback integration
- Network replication
- Platform certification
- Bespoke animation sets for kneeling
- Bespoke animation sets for AllFours transitioning
- Permission-gated teleportation or other non-locomotion systems

## Acceptance Criteria

1. Locomotion component interface is defined.
2. Concrete implementation applies movement and rotation via `MoveAndSlide`.
3. Player controller reads XR controller stick input and forwards.
4. Both snap turn and smooth turn modes supported with configurable params.
5. Root motion consumed when top-level state is Walking or active override.
6. Animation blend parameter wired to the active state-machine path, currently
   `parameters/States/Walking/blend_position` on the reference character.
7. Transitions between StandingCrouching and Walking work based on input.
8. Pose-state delegated locomotion animation-state override supported.
9. Velocity derives from animation root motion output in root-motion states.
10. Zero planar velocity returned when movement not allowed.
11. Implementation added to `player.tscn`.
12. Control scheme: left stick movement, right stick rotation.
13. Permission source API implemented and documented.
14. Permission sources wired via exported node references.
15. Movement gated based on aggregated permission state.
16. Rotation gated based on aggregated permission state.
17. Pose-state-machine wired as permission source in `player.tscn`.
18. User requirements verifiable through acceptance testing.
19. Technical requirements verifiable through code review.

## References

- [Project Specifications Index](../../../index.md)
- [CTRL: Player Character Control System](../index.md)
- [XR-001: XRManager](../../../xr/001-xr-manager/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../ik/004-vrik-pose-state-machine/index.md)
- `game/assets/characters/reference/player.tscn`
- `game/src/XR/XRManagerAbstractions.cs`
