---
id: INTR-001
title: Grabbable Interface
---

# Grabbable Interface

## Purpose

Define the contract for objects that characters can grab, supporting natural
item-specific grab behaviour through the CORE-003 Component/Trait System.

## Requirement

Implement an `IGrabbable` holder trait that delegates to grab-capability
components, enabling characters to discover and grab objects with validation
and item-specific behaviour.

## Goal

Provide a reusable grabbable interface that:

- Evaluates reach and angle eligibility before commitment.
- Delegates to owned grab-point components for execution.
- Handles grab attempts deterministically based on object type and state.

## User Requirements

1. Characters discover grabbable objects within interaction range.
2. Grab requests resolve deterministically, completing or rejecting gracefully
   when object or character conditions change between discovery and grab.
3. The system rejects requests gracefully when conditions are no longer met.
4. Characters can discover multiple grab options per object and select
   deterministically.

## Technical Requirements

1. Define `IGrabbable : IComponentHolder` holder trait in the
   `AlleyCat.Interaction` namespace, explicitly extending CORE-003
   `IComponentHolder` pattern.
2. Define `IGrabPoint` component capability interface in
   `AlleyCat.Interaction`. Concrete grab-point implementations
   are CORE-003 components that implement both `IComponent` and `IGrabPoint`,
   exposing:
   - `GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)`
     — evaluates reach, angle, and state; returns `GrabPointCandidate`
     if eligible, or `null` if this grab point cannot be used.
3. Define immutable `GrabPointCandidate` record containing:
      - Source `IGrabPoint` component reference (for ownership verification).
      - Hand target `Transform3D` (IK/settling pose; may differ from acquisition point when authored offsets exist).
      - Acquisition distance `float` — distance from the querying hand origin to the
        accepted acquisition reference's selected point. Used for candidate ranking and
        filtering, not `HandTarget.Origin`. Must be produced by the grab point implementation
        based on the accepted acquisition reference and its selected closest point.
      - Animation resource (e.g., grab animation clip reference).
      - Query hand side and hand transform used for execution-time freshness validation.
      - Query-time grab-point transform used to reject moved/stale candidates before commitment.
4. `IGrabbable` exposes
   `GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)`
   using the same argument order and nullable-return semantics as
   `IGrabPoint.GetGrabPoint`.
5. `IGrabbable.GetGrabPoint` queries owned `IGrabPoint` components via
    CORE-003 `GetComponents<IGrabPoint>()` in deterministic holder order,
    asks every owned grab point for a candidate, and returns the closest
    eligible candidate by comparing each candidate's `AcquisitionDistance` to
    determine ranking. Holder order is the deterministic tie-breaker:
    equal-distance candidates do not replace the current best candidate.
    `AcquisitionDistance` is the canonical distance metric; `HandTarget.Origin`
    must not be used for ranking.
6. `IGrabPoint.GetGrabPoint` is the only grab-point eligibility query; grab
   points that can reject cheaply may shortcut their own work inside that
   method.
7. `IGrabbable.Grab` accepts a `GrabPointCandidate` from query phase,
   validates the source component identified in the candidate is still owned
   by the holder and the object state is unchanged, and completes or
   rejects gracefully.
8. Configurable reach distance and angle thresholds per grab-point component.
9. Define `LimbSide` enum in `AlleyCat.Body` (`Left = 0`, `Right = 1`),
   unifying existing hand-side representations.
10. Multiple grab-point components per holder are supported; query order is
    deterministic as defined by the holder. This deterministic order only acts
    as a tie-breaker when two or more eligible candidates are equally close.
11. Execution validation must verify the selected component/result is still
    owned/valid and object/point state has not changed since query.
12. Define `GrabbableMobility` enum in `AlleyCat.Interaction` with two values:
    - `Movable = 0` — object can be carried and moved freely by the hand.
    - `Immovable = 1` — object is a fixed prop; hand must stay constrained to
      the grab point while holding.
13. `IGrabbable` exposes a `GrabbableMobility Mobility { get; }` property.
    - `Movable` indicates the object can be picked up and will follow the hand.
    - `Immovable` indicates the object is fixed in place; the hand must remain
      constrained to the grab point throughout the hold.

## In Scope

- `IGrabbable` holder trait extending CORE-003 `IComponentHolder`.
- `IGrabPoint` component capability interface.
- `GrabPointCandidate` immutable result type with source component reference.
- Reach and angle validation in query phase via component methods.
- Internal validation in execution phase.
- Configurable thresholds.
- Deterministic resolution from multiple grab components.

## Concrete Implementations

- [INTR-001-A: Spherical Grab Point](spherical-grab-point.md) — centre-origin
  grab point approachable from any direction.
- [INTR-001-B: Cylindrical Grab Point](cylindrical-grab-point.md) — cylinder length
  axis grab point approachable from any position along the length.
- [INTR-002: Hand Grab Execution](../002-hand-grab-execution/index.md) — hand discovery,
  grab execution, release, and IK integration.

## Future Grab Point Implementation Guidance

Future concrete `IGrabPoint` implementations should include editor-only selected
debug helpers showing their meaningful acquisition shape, axis, or origin, plus
eligibility gates where useful, and the authored hand offset or frame. This enables
authors to visualise and tune grab zones, reach volumes, and offset directions
without affecting runtime behaviour, scene semantics, physics, grab eligibility,
or creating saved runtime children or meshes. Keep visual cues scoped to the
selected node where feasible, and use editor-only drawing mechanisms such as
`EditorNode3DGizmo`, `EditorNode3DGizmoPlugin`, or `@tool` annotated methods.
Do not over-prescribe exact visuals for unknown future grab point shapes; focus
on showing what matters for authoring each concrete type.

## Out Of Scope

- Physics constraints while held.
- Multi-hand grab (multiple characters grabbing the same object).
- Network replication.
- Animation blending beyond grab-point animation resource.
- Throwable extensions.
- Inventory integration.
- Grab execution behaviour differences between movable and immobile grabbables
  (covered by INTR-002).

Note: Grab execution (hand discovery, candidate selection, parenting, IK integration,
hand pose from grab point) is covered by [INTR-002: Hand Grab Execution](../002-hand-grab-execution/index.md).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IGrabbable : IComponentHolder` is defined explicitly |
|    |                   | extending CORE-003 `IComponentHolder`. |
| 2  | Technical         | `IGrabPoint` component capability is defined with |
|    |                   | `GetGrabPoint(LimbSide handSide, Transform3D handTransform)` |
|    |                   | returning a nullable candidate and no separate `CanGrab` method. |
| 3  | Technical         | `GrabPointCandidate` immutable record contains |
|    |                   | source component reference, hand target, acquisition |
|    |                   | distance, and animation. |
| 4  | Technical         | `GetComponents<IGrabPoint>()` returns grab-point |
|    |                   | components in deterministic order. |
| 5  | Technical         | `IGrabbable.GetGrabPoint` uses the same argument |
|    |                   | order and nullable-return semantics as `IGrabPoint.GetGrabPoint`, |
|    |                   | returning `null` when no component yields an eligible pose. |
| 6  | Technical         | `Grab` validates source component is still |
|    |                   | owned and object state unchanged. |
| 7  | Technical         | `LimbSide` enum exists in `AlleyCat.Body`. |
| 8  | Technical         | Multiple grab-point components per holder are |
|    |                   | all queried, with the closest eligible candidate selected. |
| 9  | Technical         | Equal-distance eligible candidates keep holder |
|    |                   | order as the deterministic tie-breaker. |
| 10 | Technical         | `GrabbableMobility` enum exists with `Movable` and |
|    |                   | `Immovable` values. |
| 11 | Technical         | `IGrabbable` exposes `GrabbableMobility Mobility` property. |
| 12 | User              | Characters discover grabbable objects and receive |
|    |                   | deterministic completion or graceful rejection. |
| 13 | User              | Multiple grab options resolve deterministically |
|    |                   | to the closest eligible target, with stable ties. |
| 14 | User              | Authored-offset candidates where the acquisition point |
|    |                   | is in range but `HandTarget` is offset must still select |
|    |                   | and rank correctly using acquisition distance, not |
|    |                   | `HandTarget.Origin`. |

## References

- [Project Specifications Index](../../index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [IK-002: Arm And Shoulder IK System](../../characters/ik/002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../../characters/ik/003-leg-feet-ik/index.md)
- `game/src/Body/LimbSide.cs`
- `game/src/Interaction/IGrabbable.cs`
- `game/src/Interaction/IGrabPoint.cs`
- `game/src/Interaction/GrabPointCandidate.cs`
- [INTR-001-A: Spherical Grab Point](spherical-grab-point.md)
- [INTR-001-B: Cylindrical Grab Point](cylindrical-grab-point.md)
- [BODY-001: Hands](../../body/001-hands/index.md)
