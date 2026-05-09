---
id: INTR-001-A
title: Spherical Grab Point
parent: INTR-001
---

# Spherical Grab Point

## Purpose

Define the concrete `SphericalGrabPoint` component that implements `IGrabPoint`
for centre-origin objects approachable from any direction.

## Requirement

Implement a grab-point component that enables characters to grab spherical
objects from any hand direction when within reach and with correct palm
orientation.

## Goal

Provide a grab point that:

- Uses the node's global origin as the grab centre.
- Accepts valid candidates from any approach direction.
- Validates reach distance and palm facing angle.
- Supports configurable thresholds via the Godot inspector.

## User Requirements

1. Spherical centre-origin grab points are approachable from any hand
   direction and yield a valid candidate when within centre reach and the
   palm side faces the centre.
2. Characters receive a valid grab candidate only when both reach and
   palm-facing conditions are satisfied; otherwise the candidate is null.

## Technical Requirements

1. `SphericalGrabPoint` extends `Marker3D` and implements `IGrabPoint`; its
   grab centre is `GlobalTransform.Origin`.
2. `SphericalGrabPoint.GetGrabPoint` places `HandTarget.Origin` at its own
   `GlobalTransform.Origin`, enabling any approach direction.
3. `HandTarget.Basis` is preserved from the querying hand's transform; the
   component does not modify hand orientation.
4. Expose the following as authored configuration:
   - `ReachDistanceMetres` — maximum hand-to-centre distance for eligibility.
   - `PalmLocalDirection` — hand-local palm axis (exported, tunable, defaults
     to local negative Y).
   - `PalmFacingMinimumDot` — minimum palm-to-centre dot product for validity.
   - `GrabAnimation` — animation clip for a valid candidate.
5. `SphericalGrabPoint.GetGrabPoint` rejects (returns null) when:
   - `GrabAnimation` is null.
   - `ReachDistanceMetres` is not positive.
   - `PalmLocalDirection` has zero length.
   - Hand-to-centre direction has zero length.
   - Palm dot falls below `PalmFacingMinimumDot`.
6. Distance check is performed first, then palm-facing check; null is returned
   on the first failure.

## In Scope

- `SphericalGrabPoint` extending `Marker3D`.
- `IGrabPoint` implementation with reach and angle validation.
- Authored configuration via exported properties.
- Hand basis preservation.
- Rejection conditions for invalid configuration or candidate state.

## Out Of Scope

- Grab execution (how the object behaves when held).
- Release mechanics.
- Physics while held.
- Animation blending details.
- Multi-hand grab scenarios.
- Network replication.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | User              | A spherical centre-origin grab point yields a valid |
|    |                   | candidate when the hand approaches from any direction, is within |
|    |                   | reach, and the palm faces centre. |
| 2  | User              | The grab point returns null when reach or palm-facing |
|    |                   | conditions are not met. |
| 3  | Technical         | `SphericalGrabPoint` extends `Marker3D` and implements |
|    |                   | `IGrabPoint`, placing `HandTarget.Origin` at its own |
|    |                   | `GlobalTransform.Origin`. |
| 4  | Technical         | `HandTarget.Basis` is preserved from the querying hand |
|    |                   | transform. |
| 5  | Technical         | Exported properties exist for `ReachDistanceMetres`, |
|    |                   | `PalmLocalDirection`, `PalmFacingMinimumDot`, and |
|    |                   | `GrabAnimation`. |
| 6  | Technical         | `PalmLocalDirection` defaults to local negative Y. |
| 7  | Technical         | `GetGrabPoint` returns null when `GrabAnimation` |
|    |                   | is null, `ReachDistanceMetres` is not positive, |
|    |                   | `PalmLocalDirection` has zero length, hand-to-centre |
|    |                   | direction has zero length, or palm dot falls below |
|    |                   | `PalmFacingMinimumDot`. |
| 8  | Technical         | Distance check precedes palm-facing check; null is |
|    |                   | returned on first failure. |

## References

- [INTR-001: Grabbable Interface](index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- `game/src/Interaction/IGrabPoint.cs`
- `game/src/Interaction/SphericalGrabPoint.cs`