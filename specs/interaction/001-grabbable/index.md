---
id: INTR-001
title: Grabbable Interface
---

# Grabbable Interface

## Purpose

Define the contract for objects that can be grabbed by characters, providing an interface that supports natural,
item-specific grabbing behaviour.

## Requirement

Implement an `IGrabbable` interface that enables characters to discover and grab objects with appropriate validation and
item-specific behaviour.

## Goal

Provide a reusable interface for grabbable game objects that supports distance and angle validation, returns appropriate
hand targets and animations, and handles grab attempts based on the object's type and current state.

## User Requirements

1. Characters must be able to discover grabbable objects within interaction range.
2. Grab requests must resolve deterministically by completing or rejecting gracefully when object or character
   conditions change between discovery and grab.
3. The grab system must reject requests gracefully when conditions are no longer satisfied.

## Technical Requirements

1. Define an `IGrabbable` interface in the `AlleyCat.Interaction` namespace.
2. The interface must provide a `GetGrabPoint` method that evaluates closest eligible grab point for the given hand
   transform and side.
3. `GetGrabPoint` must return `null` when the hand is beyond maximum reach distance, at an unsupported angle, or the
   object is held by another entity.
4. Define a `Grab` method that accepts the `GrabPoint` returned from `GetGrabPoint` directly.
5. `Grab` must validate internal state before completing and return `false` if conditions are no longer satisfied.
6. Define a `LimbSide` enum in `AlleyCat.Common` (Left = 0, Right = 1) unifying existing hand-side representations.
7. Define a `GrabPoint` record containing the hand target transform and animation resource.
8. Grabbable implementations should support configurable reach distance and angle thresholds.

## In Scope

- `IGrabbable` interface definition.
- `GetGrabPoint` query method with distance and angle validation.
- `Grab` execution method with internal validation and rejection capability.
- `GrabPoint` result type.
- `LimbSide` enum in Common namespace.
- Configurable reach and angle parameters.

## Out Of Scope

- Specific grab mechanics (how the object behaves when held).
- Multi-hand grab support.
- Physics-based grab constraints.
- Network replication.
- Animation blending.
- Grab release mechanics.
- Throwable object extensions.
- Inventory integration.

## Acceptance Criteria

1. **Interface Contract**: `IGrabbable` interface is defined with `GetGrabPoint` and `Grab` methods.
2. **Query Validation**: `GetGrabPoint` returns null when hand is beyond maximum reach or at unsupported angle.
3. **Query Result**: Valid queries return the closest eligible grab point containing hand target and animation.
4. **Grab Execution**: `Grab` accepts the result directly from `GetGrabPoint` and attempts completion.
5. **Grab Rejection**: `Grab` returns false when conditions have changed since the query.
6. **LimbSide Enum**: Defined in `AlleyCat.Common` as `Left = 0`, `Right = 1`.
7. **User Requirements**: Characters can discover grabbable objects and receive deterministic grab completion or
   graceful rejection through gameplay testing.
8. **Technical Requirements**: Interface contract, supporting types, and rejection capability are verifiable through
   code review.

## References

- [Project Specifications Index](../../index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [IK-002: Arm And Shoulder IK System](../../characters/ik/002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../../characters/ik/003-leg-feet-ik/index.md)
- `game/src/Common/LimbSide.cs`
- `game/src/Interaction/IGrabbable.cs`
