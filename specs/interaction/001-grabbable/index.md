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
   - Hand target `Transform3D`.
   - Animation resource (e.g., grab animation clip reference).
4. `IGrabbable` exposes
   `GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)`
   using the same argument order and nullable-return semantics as
   `IGrabPoint.GetGrabPoint`.
5. `IGrabbable.GetGrabPoint` queries owned `IGrabPoint` components via
   CORE-003 `GetComponents<IGrabPoint>()` in deterministic holder order,
   asks every owned grab point for a candidate, and returns the closest
   eligible candidate by comparing each candidate's `HandTarget.Origin` to
   `handTransform.Origin`. Holder order is the deterministic tie-breaker:
   equal-distance candidates do not replace the current best candidate.
6. `IGrabPoint.GetGrabPoint` is the only grab-point eligibility query; grab
   points that can reject cheaply may shortcut their own work inside that
   method.
7. `IGrabbable.Grab` accepts a `GrabPointCandidate` from query phase,
   validates the source component identified in the candidate is still owned
   by the holder and the object state is unchanged, and completes or
   rejects gracefully.
8. Configurable reach distance and angle thresholds per grab-point component.
9. Define `LimbSide` enum in `AlleyCat.Common` (`Left = 0`, `Right = 1`),
   unifying existing hand-side representations.
10. Multiple grab-point components per holder are supported; query order is
    deterministic as defined by the holder. This deterministic order only acts
    as a tie-breaker when two or more eligible candidates are equally close.
11. Execution validation must verify the selected component/result is still
    owned/valid and object/point state has not changed since query.

## In Scope

- `IGrabbable` holder trait extending CORE-003 `IComponentHolder`.
- `IGrabPoint` component capability interface.
- `GrabPointCandidate` immutable result type with source component reference.
- Reach and angle validation in query phase via component methods.
- Internal validation in execution phase.
- Configurable thresholds.
- Deterministic resolution from multiple grab components.

## Out Of Scope

- Grab mechanics (how the object behaves when held).
- Release mechanics (how the object is released).
- Physics constraints while held.
- Multi-hand grab (multiple characters grabbing the same object).
- Network replication.
- Animation blending.
- Throwable extensions.
- Inventory integration.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IGrabbable : IComponentHolder` is defined explicitly |
|    |                   | extending CORE-003 `IComponentHolder`. |
| 2  | Technical         | `IGrabPoint` component capability is defined with |
|    |                   | `GetGrabPoint(LimbSide handSide, Transform3D handTransform)` |
|    |                   | returning a nullable candidate and no separate `CanGrab` method. |
| 3  | Technical         | `GrabPointCandidate` immutable record contains |
|    |                   | source component reference, hand target, and animation. |
| 4  | Technical         | `GetComponents<IGrabPoint>()` returns grab-point |
|    |                   | components in deterministic order. |
| 5  | Technical         | `IGrabbable.GetGrabPoint` uses the same argument |
|    |                   | order and nullable-return semantics as `IGrabPoint.GetGrabPoint`, |
|    |                   | returning `null` when no component yields an eligible pose. |
| 6  | Technical         | `Grab` validates source component is still |
|    |                   | owned and object state unchanged. |
| 7  | Technical         | `LimbSide` enum exists in `AlleyCat.Common`. |
| 8  | Technical         | Multiple grab-point components per holder are |
|    |                   | all queried, with the closest eligible candidate selected. |
| 9  | Technical         | Equal-distance eligible candidates keep holder |
|    |                   | order as the deterministic tie-breaker. |
| 10 | User              | Characters discover grabbable objects and receive |
|    |                   | deterministic completion or graceful rejection. |
| 11 | User              | Multiple grab options resolve deterministically |
|    |                   | to the closest eligible target, with stable ties. |

## References

- [Project Specifications Index](../../index.md)
- [CORE-003: Component/Trait System](../../003-component-system/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [IK-002: Arm And Shoulder IK System](../../characters/ik/002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../../characters/ik/003-leg-feet-ik/index.md)
- `game/src/Common/LimbSide.cs`
- `game/src/Interaction/IGrabbable.cs`
- `game/src/Interaction/IGrabPoint.cs`
- `game/src/Interaction/GrabPointCandidate.cs`
