---
id: INTR-001-A
title: Spherical Grab Point
parent: INTR-001
---

# Spherical Grab Point

## Purpose

Define the concrete `SphericalGrabPoint` component that implements `IGrabPoint`
for centre-origin objects approachable from any direction, preserving approach
rotation and supporting authored transform offsets.

## Requirement

Implement a grab-point component that enables characters to grab spherical
objects from any hand direction when within reach and with correct palm
orientation, preserving the grab-point rotation so items do not snap to a
canonical orientation regardless of approach direction.

## Goal

Provide a grab point that:

- Uses the node's global origin as the grab centre.
- Accepts valid candidates from any approach direction.
- Preserves the hand's approach rotation so grabbed items retain
  orientation relative to the approach direction.
- Validates reach distance and palm facing angle.
- Exposes an authored `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
  so items sit naturally in the hand.
- Supports physical RigidBody3D objects with physics suspended while held.

## User Requirements

1. Spherical centre-origin grab points are approachable from any hand
   direction and yield a valid candidate when within centre reach and the
   palm side faces the centre.
2. Characters receive a valid grab candidate only when both reach and
   palm-facing conditions are satisfied; otherwise the candidate is null.
3. The hand orientation at grab time (including approach direction) is
   preserved so items do not snap to a canonical orientation regardless
   of how the player approached.
4. Grabbables support separate authored `GrabPointPositionOffsetFromHand`
   and `GrabPointRotationOffsetFromHand` to correct how items sit in the hand
   for a given animation or grab point.

## Technical Requirements

1. `SphericalGrabPoint` extends `Marker3D` and implements `IGrabPoint`; its
   grab centre is `GlobalTransform.Origin`.
2. `SphericalGrabPoint.GetGrabPoint` places the effective grab point
    at its own `GlobalTransform.Origin`, enabling any approach direction.
    When position or rotation offsets are authored, `HandTarget` differs from
    the sphere centre while preserving approach-relative basis.
3. `HandTarget.Basis` is derived from the querying hand's current transform;
    the component does NOT snap to a canonical orientation. The approach
    direction and palm orientation are preserved through the hand's transform.
4. The candidate's `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
    (authored from hand attachment space to grab point) can optionally correct how the item
    sits in the hand. These are applied on commit as a combined transform:
    position from the vector, rotation from the euler angles. Approach-relative
    rotation is preserved because the hand-target basis is derived from the
    querying hand's transform at query time.
5. Expose the following as authored configuration:
   - `ReachDistanceMetres` — maximum hand-to-centre distance for eligibility.
   - `PalmLocalDirection` — hand-local palm axis (exported, tunable, defaults
     to local negative Y).
   - `PalmFacingMinimumDot` — minimum palm-to-centre dot product for validity.
   - `GrabAnimation` — animation clip for a valid candidate.
   - `GrabPointPositionOffsetFromHand: Vector3` — authored position offset from
     hand attachment to grab point when held. Defaults to zero. Read by the grab
     execution system.
   - `GrabPointRotationOffsetFromHand: Vector3` — authored rotation offset (Euler
     radians) from hand attachment to grab point when held. Defaults to zero.
     Combined with position to form the effective hand-relative grab-point transform.
6. `SphericalGrabPoint.GetGrabPoint` rejects (returns null) when:
   - `GrabAnimation` is null.
   - `ReachDistanceMetres` is not positive.
   - `PalmLocalDirection` has zero length.
   - Hand-to-centre direction has zero length.
   - Palm dot falls below `PalmFacingMinimumDot`.
7. Distance check is performed first, then palm-facing check; null is returned
    on the first failure.
8. The candidate returned includes the authored position and rotation offsets.

## In Scope

- `SphericalGrabPoint` extending `Marker3D`.
- `IGrabPoint` implementation with reach and angle validation.
- Authored configuration via exported properties.
- Approach rotation preserved via hand transform basis (offset combined from
  separate position and rotation).
- Separate `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
  authoring for held-item correction.
- Rejection conditions for invalid configuration or candidate state.
- Support for physical RigidBody3D objects with physics suspended while held.

## Out Of Scope

- Grab execution (how the object behaves when held).
- Release mechanics.
- Animation blending details.
- Multi-hand grab scenarios.
- Network replication.
- Physics state management beyond suspension awareness (handled by INTR-002).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | User              | A spherical centre-origin grab point yields a valid candidate when the hand |
|    |                   | approaches from any direction, is within reach, and the palm faces centre. |
| 2  | User              | The grab point returns null when reach or palm-facing conditions are not met. |
| 3  | User              | Items do not snap to a canonical orientation regardless of approach direction; |
|    |                   | approach rotation is preserved. |
| 4  | User              | Separate `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` |
|    |                   | enable per-animation or per-grab-point correction so items sit naturally in the |
|    |                   | hand. |
| 5  | Technical         | `SphericalGrabPoint` extends `Marker3D` and implements `IGrabPoint`, placing |
|    |                   | the effective grab point at its own `GlobalTransform.Origin`. |
| 6  | Technical         | `HandTarget.Basis` is derived from the querying hand's transform, not snapped |
|    |                   | to a canonical orientation. |
| 7  | Technical         | Position and rotation offsets are authored separately and combined on commit |
|    |                   | to form the effective hand-relative grab-point transform. |
| 8  | Technical         | Exported properties exist for `ReachDistanceMetres`, `PalmLocalDirection`, |
|    |                   | `PalmFacingMinimumDot`, `GrabAnimation`, `GrabPointPositionOffsetFromHand`, |
|    |                   | and `GrabPointRotationOffsetFromHand`. |
| 9  | Technical         | `PalmLocalDirection` defaults to local negative Y. |
| 10 | Technical         | `GetGrabPoint` returns null when `GrabAnimation` is null, `ReachDistanceMetres` |
|    |                   | is not positive, `PalmLocalDirection` has zero length, hand-to-centre direction |
|    |                   | has zero length, or palm dot falls below `PalmFacingMinimumDot`. |
| 11 | Technical         | Distance check precedes palm-facing check; null is returned on first failure. |
| 12 | Technical         | Candidate includes the authored position and rotation offsets. |

## References

- [INTR-001: Grabbable Interface](index.md)
- [INTR-002: Hand Grab Execution](../002-hand-grab-execution/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- `game/src/Interaction/IGrabPoint.cs`
- `game/src/Interaction/SphericalGrabPoint.cs`
