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
  so items sit naturally in the hand without spherical rotation offsets changing the hand target rotation.
- Supports physical RigidBody3D objects with physics suspended while held.

## User Requirements

1. Spherical centre-origin grab points are approachable from any hand
   direction and yield a valid candidate when the hand is within reach and the
   palm side faces the centre.
2. Characters receive a valid grab candidate only when both reach and
   palm-facing conditions are satisfied; otherwise the candidate is null.
3. The hand orientation at grab time (including approach direction) is
   preserved so items do not snap to a canonical orientation regardless of
   how the player approached.
4. Grabbables support separate authored `GrabPointPositionOffsetFromHand`
    and `GrabPointRotationOffsetFromHand` to correct how items sit in the hand
    for a given animation or grab point. For spherical grabs, the rotation offset
    affects held-object composition only; it must not make the hand rotate after
    palm-facing acquisition has already succeeded.
5. Authors can select a `SphericalGrabPoint` node in the Godot editor and see
   visual cues for the centre or origin, reach sphere, palm-facing direction
   cue, and authored hand offset or frame to tune the grab point more easily.

## Technical Requirements

1. `SphericalGrabPoint` extends `Marker3D` and implements `IGrabPoint`; its
   grab centre is `GlobalTransform.Origin`.
2. `SphericalGrabPoint.GetGrabPoint` places the effective grab point
     at its own `GlobalTransform.Origin`, enabling any approach direction.
     When a position offset is authored, `HandTarget.Origin` differs from the
     sphere centre while preserving the hand's approach basis.
3. `HandTarget.Basis` is derived from the querying hand's current transform;
     the component does NOT snap to a canonical orientation. The approach
     direction and palm orientation are preserved through the hand's transform,
     and `GrabPointRotationOffsetFromHand` does not alter `HandTarget.Basis`.
4. The candidate's `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
     (authored from hand attachment space to grab point) can optionally correct how the item
     sits in the hand. These are retained for commit as a combined transform:
     position from the vector, rotation from the euler angles. Spherical hand
     target acquisition uses the position offset to keep the grab point at the
     sphere centre, but folds the rotation offset into the candidate grab-point
     frame so the inverse offset composition still reconstructs a hand target
     with the querying hand's approach basis.
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
     Combined with position for held-object composition, but ignored as a hand-target
     rotation driver by spherical acquisition.
6. `SphericalGrabPoint.GetGrabPoint` rejects (returns null) when:
   - `GrabAnimation` is null.
   - `ReachDistanceMetres` is not positive.
   - `PalmLocalDirection` has zero length.
   - Hand-to-centre direction has zero length.
   - Palm dot falls below `PalmFacingMinimumDot`.
7. Distance check is performed first, then palm-facing check; null is returned
    on the first failure.
8. The candidate returned includes the authored position and rotation offsets.
9. `SphericalGrabPoint` must provide editor-only debug visualisation that draws
   visual cues to assist with authoring and tuning. Visualisation must be
   implemented using Godot editor-only drawing mechanisms (such as
   `_Draw` in a `EditorNode3DGizmo` or `EditorNode3DGizmoPlugin`, or
   `draw_*` methods invoked only in the editor via `@tool` or editor-conditional
   checks) that do not execute at runtime.
10. Visualisation must be scoped to the selected node by default, drawing only
    when the `SphericalGrabPoint` node is currently selected in the Godot editor.
    If the Godot editor API cannot guarantee strict selected-only drawing in all
    contexts (for example, gizmo drawing that persists regardless of selection),
    then unselected drawing must be absent or unobtrusive, and the selected state
    must provide the full useful authoring view.
11. Visualisation must not affect runtime behaviour in any way: it must not
    modify the node's transform, add runtime children or meshes, affect scene
    semantics, influence physics or collision, impact grab eligibility checks, or
    persist to saved scenes. The visual cues exist purely for the author's
    editor workflow.
12. The debug visual cues must include at minimum:
    - A centre or origin marker at the spherical grab point centre.
    - A reach sphere or wire sphere using `ReachDistanceMetres` as the radius,
      centred on the grab point, showing the acquisition volume.
    - A palm-facing direction cue derived from `PalmLocalDirection` as a
      hand-local preview or hint (the exact runtime palm-facing still depends
      on the querying hand transform at grab time).
    - An authored hand offset vector or frame derived from
      `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`,
      shown at the marker centre or a representative grab point.

## In Scope

- `SphericalGrabPoint` extending `Marker3D`.
- `IGrabPoint` implementation with reach and angle validation.
- Authored configuration via exported properties.
- Approach rotation preserved via hand transform basis. Authored rotation offset is
  retained for held-item composition without rotating the spherical hand target.
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
|    |                   | hand without spherical rotation offsets adding hand rotation after acquisition. |
| 5  | Technical         | `SphericalGrabPoint` extends `Marker3D` and implements `IGrabPoint`, placing |
|    |                   | the effective grab point at its own `GlobalTransform.Origin`. |
| 6  | Technical         | `HandTarget.Basis` is derived from the querying hand's transform, not snapped |
|    |                   | to a canonical orientation or altered by `GrabPointRotationOffsetFromHand`. |
| 7  | Technical         | Position and rotation offsets are authored separately and combined on commit |
|    |                   | to form the effective hand-relative grab-point transform; spherical acquisition |
|    |                   | uses the position offset for `HandTarget` placement while preserving hand basis. |
| 8  | Technical         | Exported properties exist for `ReachDistanceMetres`, `PalmLocalDirection`, |
|    |                   | `PalmFacingMinimumDot`, `GrabAnimation`, `GrabPointPositionOffsetFromHand`, |
|    |                   | and `GrabPointRotationOffsetFromHand`. |
| 9  | Technical         | `PalmLocalDirection` defaults to local negative Y. |
| 10 | Technical         | `GetGrabPoint` returns null when `GrabAnimation` is null, `ReachDistanceMetres` |
|    |                   | is not positive, `PalmLocalDirection` has zero length, hand-to-centre direction |
|    |                   | has zero length, or palm dot falls below `PalmFacingMinimumDot`. |
| 11 | Technical         | Distance check precedes palm-facing check; null is returned on first failure. |
| 12 | Technical         | Candidate includes the authored position and rotation offsets. |
| 13 | User              | Selecting a `SphericalGrabPoint` node in the Godot editor displays visual |
|    |                   | cues for the centre, reach sphere, palm-facing direction, and authored |
|    |                   | hand offset, enabling easier verification and tuning. |
| 14 | Technical         | Visualisation is implemented using editor-only drawing mechanisms that do |
|    |                   | not execute at runtime, using `@tool` or editor-conditional code paths. |
| 15 | Technical         | Visualisation draws when the `SphericalGrabPoint` node is selected; if the |
|    |                   | Godot editor API cannot guarantee strict selected-only drawing, unselected |
|    |                   | drawing is absent or unobtrusive, and selected state provides the full |
|    |                   | authoring view. |
| 16 | Technical         | Visualisation does not modify node transforms, add runtime children or meshes, |
|    |                   | affect scene semantics, influence physics or grab eligibility, or persist |
|    |                   | to saved scenes; it exists purely as an author-time debugging aid. |
| 17 | Technical         | Visual cues include: centre or origin marker at the grab point centre; reach |
|    |                   | sphere using ReachDistanceMetres as radius; palm-facing direction cue |
|    |                   | derived from PalmLocalDirection; authored hand offset vector or frame at the |
|    |                   | marker centre derived from GrabPointPositionOffsetFromHand and |
|    |                   | GrabPointRotationOffsetFromHand. |

## References

- [INTR-001: Grabbable Interface](index.md)
- [INTR-002: Hand Grab Execution](../002-hand-grab-execution/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- `game/src/Interaction/IGrabPoint.cs`
- `game/src/Interaction/SphericalGrabPoint.cs`
