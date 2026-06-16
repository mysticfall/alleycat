---
id: CTRL-002
title: Hand Grab Input
parent: CTRL
---

# Hand Grab Input

## Purpose

Define the contract for mapping XR controller input to hand grab and release
actions, supporting two-phase grab (approach then commit) where grab input
initiates the approach and commit is deferred until IK settles.

## Requirement

Implement controller input handling that calls `IHand.Grab()` on grab button
press and `IHand.Release()` on release button press, supporting independent
left/right hand control. The press initiates the approach phase; commit occurs
automatically once IK settles at the grab point.

## Goal

Provide a hand grab input system that:

- Maps XR controller grab trigger or button to hand grab action.
- Maps XR controller release to hand release action.
- Supports independent left/right hand control.
- Integrates with PlayerController or dedicated Control namespace component.
- Maintains loose coupling between input handling and interaction logic.
- Handles the two-phase nature of grab (approach initiated on press, commit
  deferred until IK settles).

## User Requirements

1. Pressing the grab button on an XR controller initiates the approach phase,
   moving the hand to the selected grab point while the item stays still.
2. Releasing the grab button triggers release of the held object.
3. Left and right hands operate independently.
4. Commit occurs automatically once IK settles; no second press required.

## Technical Requirements

1. Define grab input mapping in `Control` namespace (e.g., `HandGrabInputHandler`).
2. The handler references left and right XR controller nodes from the XR rig.
3. On grab button press (e.g., grip button or trigger), call `IHand.Grab()` on the
   corresponding hand component.
   - This initiates the approach phase; the hand moves to the grab point via IK.
   - The item does not move during approach.
   - Commit is deferred until IK settles; the input handler takes no further action.
4. On grab button release, call `IHand.Release()` on the corresponding hand.
   - If the hand has not yet committed (e.g., still approaching), the grab is
     abandoned and the hand returns to idle.
   - If already holding, release proceeds normally.
5. Grab input handling must:
   - Identify which hand side triggered the input (left/right controller).
   - Resolve the correct `IHand` component via `IHasHands.TryGetHand()`.
   - Forward grab/release calls to the resolved hand component.
6. Input handler does not implement grab mechanics; it merely translates input to
   method calls and does not monitor IK settling directly.
7. If no hand component exists for the input side, the input is ignored (no error).
8. If `Grab()` returns null (no valid grabbable), no visual or haptic feedback is
   required for this spec (future expansion may add feedback).

## In Scope

- XR controller grab button press -> IHand.Grab() call.
- XR controller release -> IHand.Release() call.
- Independent left/right hand control.
- Integration with PlayerController or Control namespace handler.
- Loose coupling between input and interaction systems.
- Handling of two-phase grab (press initiates approach; commit is automatic).

## Out Of Scope

- Haptic feedback on grab attempt or success.
- Visual feedback (e.g., highlight valid grabbables).
- Grab input for non-XR input sources (keyboard, gamepad).
- Network replication of input events.
- IK settling detection (handled by the hand component or IK system).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | User              | Pressing grab button initiates approach phase for the |
|    |                   | corresponding hand. |
| 2  | User              | Releasing grab button triggers release of held object, |
|    |                   | or abandons approach if not yet committed. |
| 3  | User              | Left and right hands operate independently. |
| 4  | User              | Commit occurs automatically once IK settles; no second |
|    |                   | press is required. |
| 5  | Technical         | Handler uses XR controller input to detect press/release. |
| 6  | Technical         | Grab calls IHand.Grab() on the correct hand side. |
| 7  | Technical         | Release calls IHand.Release() on the correct hand side. |
| 8  | Technical         | Hand resolution uses IHasHands.TryGetHand() for the |
|    |                   | input side. |
| 9  | Technical         | Input handler maintains loose coupling with interaction logic. |
| 10 | Technical         | Input handler does not monitor IK settling; commit is |
|    |                   | handled by the hand component. |

## References

- [Project Specifications Index](../../index.md)
- [CTRL: Player Character Control System](../index.md)
- [CTRL-001: Locomotion](../001-locomotion/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [BODY-001: Hands](../../body/001-hands/index.md)
- [INTR-002: Hand Grab Execution](../../interaction/002-hand-grab-execution/index.md)
- `game/src/Control/` (implementation namespace)
