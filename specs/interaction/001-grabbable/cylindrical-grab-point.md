---
id: INTR-001-B
title: Cylindrical Grab Point
parent: INTR-001
---

# Cylindrical Grab Point

## Purpose

Define the concrete `CylindricalGrabPoint` component that implements `IGrabPoint`
for cylindrical objects where the hand can grab and position along the cylinder
length axis, preserving the dynamic selected point and supporting authored
transform offsets. The acquisition distance for ranking is derived from the
accepted acquisition reference and its selected closest point on the cylinder
axis, per the acquisition-distance contract in INTR-001.

## Requirement

Implement a grab-point component that enables characters to grab cylindrical
objects along the cylinder length when within reach and with correct palm
orientation, preserving the selected axis point and cylinder length direction
while deriving X/Z axes from the querying hand's grab frame so that hand
twist around the cylinder axis is preserved while item roll is ignored.

## Goal

Provide a grab point that:

- Uses a defined length along the cylinder's local Y axis as the grab zone.
- Accepts valid candidates from any hand position when either the physical hand
  origin or the authored grip reference reaches the closest point on the
  cylinder length axis and the palm-facing gate passes.
- Preserves the selected axis point and current global local-Y length direction,
  deriving X/Z axes from the querying hand's grab frame so hand twist around the
  cylinder Y axis is preserved while marker roll around that axis is ignored,
  keeping the grab-point origin dynamic along the cylinder axis.
- Validates reach distance and palm facing angle.
- Exposes authored `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
  so items sit naturally in the hand.
- Supports physical RigidBody3D objects with physics suspended while held.

## User Requirements

1. Cylindrical grab points are approachable from any position along the
   cylinder length and yield a valid candidate when the player's physical hand
   or controller is within reach of the closest point on the cylinder length
   axis and the palm-facing gate passes.
2. Characters receive a valid grab candidate only when both reach and palm-facing
   conditions are satisfied; otherwise the candidate is null.
3. The selected grab point remains at the accepted acquisition reference's
   closest valid point on the cylinder axis, so grabbed items do not slide or
   snap to the marker centre or a cylinder end when the player grabs elsewhere
   along the cylinder.
4. Grabbables support separate authored `GrabPointPositionOffsetFromHand` and
   `GrabPointRotationOffsetFromHand` from the hand attachment to the dynamically
   selected point on the cylinder axis, allowing a given animation or grab point
   to sit naturally in the hand without losing the along-axis sample.
5. An optional `SnapDistanceMetres` property enables early acquisition when the
   hand is offset perpendicular to the cylinder length axis. When set to a
   positive value, the grab point behaves as if it were that distance closer
   to the hand along the perpendicular direction, allowing immediate snap when
   the hand is within the configured perpendicular distance from the actual
   grab point. When set to zero (default), behaviour is unchanged: the hand must
   reach the actual grab point before acquisition succeeds.
6. The snap distance applies only to the perpendicular component; it must not
   allow acquisition when the hand is offset along the cylinder length axis,
   even if the perpendicular distance is within the configured margin.
7. The final held pose remains at the actual selected grab point on the cylinder
    axis, using the raw authored offsets. The snap distance does not shift or
    vary the mounted or held item position; it affects only acquisition timing.
8. Rolling a grabbed cylinder around its length axis must not change the hand
     pose interpretation or cause awkward rolled hand poses; the grab offset and
     held pose remain consistent regardless of cylinder roll around its length.
9. Changing the hand's twist or pitch around the cylinder length axis must be
     preserved in the grab semantics; the grab must not snap the hand pitch to a
     fixed world-derived orientation. For example, when holding a baton where the
     item length aligns with local Y and the thumb is roughly aligned with the
     item Y axis, changing the hand pitch around the item length axis must produce
     a corresponding change in the selected frame and held pose.
10. Authors can select a `CylindricalGrabPoint` in the Godot editor and see visual
    cues for the local-Y grab segment, acquisition area, and authored hand offset,
    making it easier to tune grab zones and verify offset direction.

## Technical Requirements

1. `CylindricalGrabPoint` extends `Marker3D` and implements `IGrabPoint`.
2. The cylinder uses Godot CylinderMesh convention: local Y axis is the length
   axis, centre at origin. The grab zone is a line segment along local Y between
   `-LengthMetres / 2` and `+LengthMetres / 2`.
3. Reach distance is measured against two acquisition references: the raw hand
   origin and the authored grip reference point. The authored grip reference
   point is the hand origin plus `GrabPointPositionOffsetFromHand` transformed
   by the hand basis into world space.
4. For each acquisition reference, `CylindricalGrabPoint.GetGrabPoint` computes
   the closest point on the actual `GlobalTransform` local-Y segment, validates
   reach to that clamped point, and selects the first eligible path. Raw hand
   origin acquisition is preferred when both references are within reach because
   it best represents the player's physical hand/controller position; otherwise
   the authored grip reference path may acquire when it alone is eligible.
5. Palm-facing eligibility checks whether the palm points towards the selected
   closest point on the cylinder length segment from the accepted acquisition
   reference only. Transform PalmLocalDirection by the hand transform into world
   space; compute direction-to-closest-point from the accepted acquisition
   reference to its selected point; compute
   dot(palm-world-direction, direction-to-closest-point) and compare against
   PalmFacingMinimumDot. If the accepted reference lies exactly on the selected
   point, skip the palm-dot rejection for that centred contact because no
   direction exists; do not fall back to the alternate acquisition reference for
   palm-facing.
6. The returned `GrabPointTransform` represents a cylindrical selected axis frame:
     - Origin is the selected closest point on the cylinder local-Y segment.
     - Y basis axis is the cylinder's current global local-Y line (meaningful and
       preserved as an unoriented cylinder axis). Its polarity may be flipped to
       keep the selected frame aligned with the querying hand's authored grab
       frame, avoiding a 180-degree hand roll when the cylindrical prop's local-Y
       axis is inverted.
     - X and Z basis axes are derived from the querying hand's authored grab frame:
       compose the hand basis with `GrabPointRotationOffsetFromHand` (or the offset
       basis if authored) to obtain the hand-referenced frame; project this frame's
       X and Z axes onto the plane perpendicular to the cylinder Y axis; use the
       projected X and Z as the selected frame's X/Z. When this projection is degenerate
       (collapses to zero or near-zero), a deterministic fallback must be used: the
       fallback basis must not incorporate marker or item X/Z roll and must be stable
       (same inputs produce same outputs). This fallback applies only when the hand-referenced
       projection is degenerate.
     - Marker X/Z roll around local Y is ignored: the marker `GlobalTransform.Basis`
       (including X/Z roll) is not used for the selected-frame basis.
7. `HandTarget` is derived from the selected cylindrical axis frame and the inverse
    of the authored hand-to-grab-point offset. When position or rotation offsets
    are authored, `HandTarget` can differ from the closest point while
    `HandTarget * GrabPointOffsetFromHand` must reconstruct the hand-referenced
    cylindrical axis frame (origin and Y-axis line, with X/Z derived from the
    hand's grab frame the same way as the selected frame). The marker X/Z roll
    around the cylinder Y axis and the cylinder local-Y polarity do not introduce
    a 180-degree hand roll into the reconstructed frame.
8. The authored `GrabPointPositionOffsetFromHand` remains hand-local and is the
   held-pose contract between grab selection and grab execution. For dynamic
   cylindrical grab points it is the offset from the hand attachment to the
   selected/contact grab point, not to the item root, cylinder centre, or marker
   origin. If an authoring workflow measures from the item root/centre, it must
   add the root-to-selected/contact transform for the intended contact before
   serialising this property. Cylindrical candidates must return the authored
   position offset exactly, including any along-axis component, so calibrated hand
   placement is preserved. At query time this authored offset is transformed by
   the hand basis into world space only to locate one acquisition reference; it
   must not replace or mutate the returned held-pose offset.
9. The candidate's authored `GrabPointPositionOffsetFromHand` and authored
     `GrabPointRotationOffsetFromHand` are applied on commit as a combined
     transform: position from the raw authored vector, rotation from the euler angles.
     `HandTarget * GrabPointOffsetFromHand` must reconstruct the clamped selected
     point and the hand-referenced cylindrical axis frame; the marker X/Z roll around
     the cylinder Y axis does not influence the reconstructed frame.
10. Expose the following as authored configuration:
    - `LengthMetres` — length of the grab zone along local Y. Must be positive.
    - `ReachDistanceMetres` — maximum acquisition reference distance to the selected point.
    - `SnapDistanceMetres` — maximum perpendicular distance from the cylinder axis
      that can be compensated during acquisition. Defaults to zero (no snap). When
      positive, the acquisition logic treats the grab point as being closer to the
      hand along the perpendicular direction only.
    - `PalmLocalDirection` — hand-local palm axis (exported, tunable, defaults
      to local negative Y).
    - `PalmFacingMinimumDot` — minimum palm-to-closest-point dot product for validity.
    - `GrabAnimation` — animation clip for a valid candidate.
    - `GrabPointPositionOffsetFromHand: Vector3` — authored position offset from
      hand attachment to the selected/contact grab point when held. Defaults to
      zero.
    - `GrabPointRotationOffsetFromHand: Vector3` — authored rotation offset (Euler
      radians) from hand attachment to grab point when held. Defaults to zero.
11. `CylindricalGrabPoint.GetGrabPoint` rejects (returns null) when:
     - `GrabAnimation` is null.
     - `LengthMetres` is not positive.
     - `ReachDistanceMetres` is not positive.
     - `SnapDistanceMetres` is negative.
     - `PalmLocalDirection` has zero length.
     - The accepted acquisition reference cannot provide a meaningful direction
       to the selected point for palm-facing, and it is not exactly centred on
       the selected point.
     - Palm dot falls below `PalmFacingMinimumDot`.
12. When `SnapDistanceMetres` is positive, the acquisition logic compensates for
    perpendicular offset as follows: compute the closest point on the cylinder
    axis to the acquisition reference; compute the perpendicular distance by
    projecting the reference-to-closest-point vector onto the plane orthogonal
    to the cylinder's local Y axis and taking its magnitude; if the perpendicular
    distance is less than or equal to `SnapDistanceMetres`, treat the actual
    closest point as reachable for acquisition purposes, even if the straight-line
    distance exceeds `ReachDistanceMetres`.
13. The snap distance does not apply along the cylinder length axis. If the
    projection of the acquisition reference onto the cylinder axis falls outside
    the clamped segment or the along-axis distance exceeds `ReachDistanceMetres`,
    acquisition fails regardless of perpendicular compensation. The perpendicular
    margin cannot enable acquisition when the hand is offset along the cylinder
    axis beyond the normal reach threshold.
14. The returned `GrabPointTransform` always represents the actual selected closest
    point on the cylinder segment, not a point shifted towards the hand. The snap
    distance affects only the acquisition eligibility check; it does not mutate
    the returned transform origin or the authored offsets used for the final held
    pose.
15. Distance check is performed first, then palm-facing check; null is returned
    on the first failure.
16. Candidate freshness validation compares a refreshed candidate's selected
    `GrabPointTransform` against the original candidate's selected
    `GrabPointTransform`; it must not assume a candidate transform equals the
    source node's marker-centre `GlobalTransform`.
17. The candidate returned includes the raw authored position offset and the
     authored rotation offset.
18. `CylindricalGrabPoint` must provide editor-only debug visualisation that draws
     visual cues to assist with authoring and tuning. Visualisation must be
     implemented using Godot editor-only drawing mechanisms (such as
     `_Draw` in a `EditorNode3DGizmo` or `EditorNode3DGizmoPlugin`, or
     `draw_*` methods invoked only in the editor via `@tool` or editor-conditional
     checks) that do not execute at runtime.
19. Visualisation must be scoped to the selected node by default, drawing only
    when the `CylindricalGrabPoint` node is currently selected in the Godot editor.
    If the Godot editor API cannot guarantee strict selected-only drawing in all
    contexts (for example, gizmo drawing that persists regardless of selection),
    then unselected drawing must be absent or unobtrusive, and the selected state
    must provide the full useful authoring view.
20. Visualisation must not affect runtime behaviour in any way: it must not
    modify the node's transform, add runtime children or meshes, affect scene
    semantics, influence physics or collision, impact grab eligibility checks, or
    persist to saved scenes. The visual cues exist purely for the author's
    runtime-editor workflow.
21. The debug visual cues must include at minimum:
     - The local-Y grab segment between `-LengthMetres / 2` and `+LengthMetres / 2`,
       drawn as a line, cylinder, or equivalent marker clearly showing the grab
       zone along the cylinder axis, with the +Y direction clearly indicated (for
       example, a cone, arrow, or distinct endpoint colour).
     - The reach area represented as a tube or wireframe cylinder using
       `ReachDistanceMetres` as the radius, centred on the grab segment, showing
       the acquisition volume.
     - When `SnapDistanceMetres` is greater than zero, a separate snap indicator
       drawn as a tube perpendicular to the cylinder length axis, representing
       the perpendicular compensation zone. This must be visually distinct from
       the reach area (different colour, opacity, or line style) and must not
       include rounded end caps that would imply snap extends beyond the cylinder
       ends—the snap indicator should communicate perpendicular-only margin,
       not a volumetric reach extension.
     - The authored hand offset vector or frame displayed at the marker origin or
       a representative selected point, derived from `GrabPointPositionOffsetFromHand`
       and `GrabPointRotationOffsetFromHand`. This may be drawn as a transform
       gizmo (axes showing local X/Y/Z after applying the authored rotation) or
       as a position vector arrow from the marker origin, labelled or colour-coded
       to indicate it represents the authored offset.

## In Scope

- `CylindricalGrabPoint` extending `Marker3D`.
- `IGrabPoint` implementation with reach and angle validation against closest
  point on the clamped cylinder length segment.
- Authored configuration via exported properties.
- Dynamic selected `GrabPointTransform` origin along the cylinder axis, with the
  selected point preserved and basis derived hand-referenced (Y-axis is the current
  global local-Y line with hand-aligned polarity, X/Z derived from the querying
  hand's grab frame projected onto the plane perpendicular to cylinder Y,
  preserving hand twist while ignoring marker roll around Y).
- Separate `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
  authoring for held-item correction, with authored offsets preserved in
  cylindrical candidates.
- Optional `SnapDistanceMetres` for perpendicular early acquisition without
  affecting final held pose or authored offsets.
- Rejection conditions for invalid configuration or candidate state.
- Support for physical RigidBody3D objects with physics suspended while held.

## Out Of Scope

- Grab execution (how the object behaves when held).
- Release mechanics.
- Animation blending details.
- Multi-hand grab scenarios.
- Network replication.
- Physics state management beyond suspension awareness (handled by INTR-002).
- Cylinder diameter validation (handled by collision/visual setup).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | User              | A cylindrical grab point yields a valid candidate when the physical hand or |
|    |                   | controller approaches from any position along the cylinder length, is within |
|    |                   | reach of the closest point on the length axis, and the palm-facing gate passes. |
| 2  | User              | The grab point returns null when reach or palm-facing conditions are not met. |
| 3  | User              | Items do not slide or snap to the marker centre or an end when grabbed from a |
|    |                   | different valid point along the cylinder axis. |
| 4  | User              | Separate `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` |
|    |                   | enable per-animation or per-grab-point correction so items sit naturally in the |
|    |                   | hand while preserving the dynamic along-axis selected point. |
| 5  | Technical         | `CylindricalGrabPoint` extends `Marker3D` and implements `IGrabPoint`. |
| 6  | Technical         | Cylinder uses Godot CylinderMesh convention: local Y axis is length axis, |
|    |                   | centre at origin. Grab zone is a line segment along local Y. |
| 7  | Technical         | Reach distance evaluates both the raw hand origin and the authored grip |
|    |                   | reference against their closest points on the actual global local-Y segment, |
|    |                   | preferring raw hand-origin acquisition when both are eligible. |
| 8  | Technical         | Palm-facing checks whether palm points towards the selected closest point from |
|    |                   | the accepted acquisition reference, and centred contact is not rejected solely |
|    |                   | because the accepted-reference direction is zero length. |
| 9  | Technical         | `GrabPointTransform.Origin` is the selected closest point on the cylinder |
|    |                   | segment, not the marker centre, and its basis is a hand-referenced cylindrical |
|    |                   | axis frame where Y is the cylinder global local-Y line with hand-aligned |
|    |                   | polarity, and X/Z are derived by projecting the hand's grab frame (hand basis |
|    |                   | composed with the authored rotation offset) onto the plane perpendicular to |
|    |                   | cylinder Y, |
|    |                   | ignoring marker X/Z roll. |
| 10 | Technical         | Position and rotation offsets are authored separately; cylindrical candidates |
|    |                   | return the raw authored position offset, including along-axis components, and |
|    |                   | `HandTarget * GrabPointOffsetFromHand` reconstructs the selected clamped point |
|    |                   | with the hand-referenced cylindrical axis frame, unaffected by marker roll. |
| 11 | Technical         | Exported properties exist for `LengthMetres`, `ReachDistanceMetres`, |
|    |                   | `SnapDistanceMetres`, `PalmLocalDirection`, `PalmFacingMinimumDot`, |
|    |                   | `GrabAnimation`, `GrabPointPositionOffsetFromHand`, and |
|    |                   | `GrabPointRotationOffsetFromHand`. |
| 12 | Technical         | `PalmLocalDirection` defaults to local negative Y. |
| 13 | Technical         | `GetGrabPoint` returns null when `GrabAnimation` is null, `LengthMetres` |
|    |                   | is not positive, `ReachDistanceMetres` is not positive, `PalmLocalDirection` |
|    |                   | has zero length, no meaningful palm-facing direction exists for non-centred |
|    |                   | contact, or palm dot falls below `PalmFacingMinimumDot`. |
| 14 | Technical         | Distance check precedes palm-facing check; null is returned on first failure. |
| 15 | Technical         | Candidate freshness compares refreshed selected transforms against the |
|    |                   | original candidate selected transform, not the source node's centre transform. |
| 16 | Technical         | Candidate includes the raw authored position offset and authored rotation |
|    |                   | offset. |
| 17 | User              | When `SnapDistanceMetres` is zero (default), the hand must reach the actual |
|    |                   | grab point before acquisition succeeds, preserving unchanged behaviour. |
| 18 | User              | When `SnapDistanceMetres` is positive, acquisition succeeds when the hand |
|    |                   | is within the configured perpendicular distance from the actual grab point, |
|    |                   | allowing immediate snap without moving the hand to the exact point. |
| 19 | User              | Positive `SnapDistanceMetres` does not allow acquisition when the hand is |
|    |                   | offset along the cylinder length axis beyond normal reach, even if the |
|    |                   | perpendicular distance is within the margin. |
| 20 | User              | The final held pose remains at the actual selected grab point using the raw |
|    |                   | authored offsets; the snap distance does not shift or vary the mounted or |
|    |                   | held item position. |
| 20a| User              | Rolling a grabbed cylinder around its length axis does not cause awkward |
|    |                   | hand poses; the held pose remains consistent regardless of cylinder roll |
|    |                   | because the grab is item-roll-invariant and hand-twist-preserving. |
| 21 | Technical         | `SnapDistanceMetres` is exposed as an exported property, defaulting to zero. |
| 22 | Technical         | When `SnapDistanceMetres` is positive, the acquisition logic compensates for |
|    |                   | perpendicular offset by computing the perpendicular distance from the |
|    |                   | acquisition reference to the cylinder axis; if within the margin, the actual |
|    |                   | closest point is treated as reachable. |
| 23 | Technical         | The snap distance applies only to the perpendicular component; acquisition |
|    |                   | fails when the along-axis distance from the acquisition reference to the |
|    |                   | clamped segment exceeds `ReachDistanceMetres`, regardless of perpendicular |
|    |                   | compensation. |
| 24 | Technical         | `GrabPointTransform.Origin` always represents the actual selected closest |
|    |                   | point, not a point shifted towards the hand; the snap distance affects only |
|    |                   | acquisition eligibility, not the returned transform or authored offsets. |
| 25 | Technical         | `GetGrabPoint` returns null when `SnapDistanceMetres` is negative. |
| 26 | Technical         | Two cylinder transforms that differ only by roll around the cylinder local-Y |
|    |                   | axis (same hand, item roll-only changes) must produce identical selected |
|    |                   | frames, candidate origins, hand targets, and freshness validation results, |
|    |                   | because the marker X/Z roll is not meaningful for cylindrical grab semantics. |
| 27 | Technical         | `GrabPointTransform.Basis` uses a hand-referenced derivation for X and Z |
|    |                   | axes that ignores marker rotation around the cylinder Y axis but preserves |
|    |                   | the hand's twist around that axis; the hand basis composed with the authored |
|    |                   | rotation offset is projected onto the plane perpendicular to cylinder Y to |
|    |                   | derive X/Z. Only the Y axis line (cylinder length axis) and selected point |
|    |                   | are meaningful and preserved; polarity may flip to avoid a 180-degree hand roll. |
| 28 | Technical         | `HandTarget * GrabPointOffsetFromHand` reconstructs the hand-referenced |
|    |                   | cylindrical axis frame (selected point and Y-axis line with X/Z derived |
|    |                   | from the hand's grab frame) regardless of marker X/Z roll or local-Y polarity; |
|    |                   | neither can introduce a 180-degree hand roll into the held pose. |
| 29 | Technical         | Two grab queries with the same item transform but different hand twist |
|    |                   | around the cylinder Y axis (same item + hand twist-only changes) must |
|    |                   | produce different selected frames and hand targets, preserving the hand's |
|    |                   | twist around the cylinder axis in the grab semantics. |
| 30 | Technical         | The selected frame X/Z derivation uses the querying hand's authored grab |
|    |                   | frame (hand basis composed with `GrabPointRotationOffsetFromHand`) projected |
|    |                   | onto the plane perpendicular to cylinder Y. When this projection is degenerate, |
|    |                   | a deterministic fallback that does not use marker or item X/Z roll is used; the |
|    |                   | fallback applies only when the hand-referenced projection is degenerate. |
| 31 | User              | Selecting a `CylindricalGrabPoint` node in the Godot editor displays visual |
|    |                   | cues for the grab segment, reach area, snap zone (when configured), and |
|    |                   | authored hand offset, enabling easier verification and tuning. |
| 32 | Technical         | Visualisation is implemented using editor-only drawing mechanisms that do |
|    |                   | not execute at runtime, using `@tool` or editor-conditional code paths. |
| 33 | Technical         | Visualisation draws when the `CylindricalGrabPoint` node is selected; if the |
|    |                   | Godot editor API cannot guarantee strict selected-only drawing, unselected |
|    |                   | drawing is absent or unobtrusive, and selected state provides the full |
|    |                   | authoring view. |
| 34 | Technical         | Visualisation does not modify node transforms, add runtime children or meshes, |
|    |                   | affect scene semantics, influence physics or grab eligibility, or persist |
|    |                   | to saved scenes; it exists purely as an author-time debugging aid. |
| 35 | Technical         | Visual cues include: local-Y grab segment between ±LengthMetres/2 with +Y |
|    |                   | direction indicated; reach area as a tube using ReachDistanceMetres radius; |
|    |                   | snap indicator when SnapDistanceMetres > 0, visually distinct from reach and |
|    |                   | without rounded end caps that would imply beyond-end snap; authored hand |
|    |                   | offset vector/frame at the marker origin or representative point, derived |
|    |                   | from GrabPointPositionOffsetFromHand and GrabPointRotationOffsetFromHand. |
| 36 | Technical         | The snap indicator uses a tube perpendicular to the cylinder length axis, |
|    |                   | visually distinct from the reach tube, and does not use rounded end caps |
|    |                   | that would misrepresent snap as extending beyond the cylinder ends.

## Test Props

The test pipe prop configuration:

- **Pipe Length**: 50 cm (0.5 m)
- **Pipe Diameter**: 2 cm (0.02 m), 1 cm radius (0.01 m)
- **Grab Point Length**: 40 cm (0.4 m) — provides 5 cm margin at each end
- **Grab Animation**: `res://assets/characters/reference/female/animations/Grab-pipe-10.tres`
- **Palm Facing Minimum Dot**: `-1.0` for the current test pipe only, as prop
  tuning until the runtime hand/controller palm axis is calibrated. This does
  not change the component default.

## References

- [INTR-001: Grabbable Interface](index.md)
- [INTR-001-A: Spherical Grab Point](spherical-grab-point.md)
- [INTR-002: Hand Grab Execution](../002-hand-grab-execution/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- `game/src/Interaction/IGrabPoint.cs`
- `game/src/Interaction/CylindricalGrabPoint.cs`
