---
id: INTR-001
title: Grabbable Interface
---

# Grabbable Interface

## Purpose

Define the contract for objects that characters can grab, supporting natural
item-specific grab behaviour.

## Requirement

Implement an `IGrabbable` interface enabling characters to discover and grab
objects with validation and item-specific behaviour.

## Goal

Provide a reusable grabbable interface that:
- Evaluates reach and angle eligibility before commitment.
- Returns a stable grab point for execution.
- Handles grab attempts deterministically based on object type and state.

## User Requirements

1. Characters discover grabbable objects within interaction range.
2. Grab requests resolve deterministically, completing or rejecting gracefully
   when object or character conditions change between discovery and grab.
3. The system rejects requests gracefully when conditions are no longer met.

## Technical Requirements

1. Define `IGrabbable` in the `AlleyCat.Interaction` namespace.
2. `GetGrabPoint`: evaluates the closest eligible grab point for a given hand
   transform and side; returns `null` if the hand is out of reach, at an
   unsupported angle, or the object is already held.
3. `Grab`: accepts the `GrabPoint` returned from `GetGrabPoint`; validates
   internal state before completing; returns `false` if conditions are no
   longer satisfied.
4. Define `LimbSide` enum in `AlleyCat.Common` (`Left = 0`, `Right = 1`),
   unifying existing hand-side representations.
5. Define `GrabPoint` record containing the hand target transform and animation
   resource.
6. Configurable reach distance and angle thresholds.

## In Scope

- `IGrabbable` interface with `GetGrabPoint` and `Grab` methods.
- Reach and angle validation in query phase.
- Internal validation in execution phase.
- `GrabPoint` result type and `LimbSide` enum.

## Out Of Scope

- Grab mechanics (how the object behaves when held).
- Multi-hand grab, physics constraints, network replication, animation
  blending, grab release, throwable extensions, inventory integration.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IGrabbable` is defined with `GetGrabPoint` and `Grab`. |
| 2  | Technical         | `GetGrabPoint` returns `null` when out of reach, at an |
|    |                   | unsupported angle, or the object is already held. |
| 3  | Technical         | Valid queries return the closest eligible `GrabPoint` |
|    |                   | with hand target and animation. |
| 4  | Technical         | `Grab` accepts a `GrabPoint` directly from `GetGrabPoint`. |
| 5  | Technical         | `Grab` returns `false` when conditions changed since query. |
| 6  | Technical         | `LimbSide` enum exists in `AlleyCat.Common`. |
| 7  | User              | Characters discover grabbable objects and receive |
|    |                   | deterministic completion or graceful rejection. |

## References

- [Project Specifications Index](../../index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [IK-002: Arm And Shoulder IK System](../../characters/ik/002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](../../characters/ik/003-leg-feet-ik/index.md)
- `game/src/Common/LimbSide.cs`
- `game/src/Interaction/IGrabbable.cs`
