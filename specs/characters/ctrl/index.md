---
id: CTRL
title: Player Character Control System
---

# Player Character Control System

## Purpose

Define the parent specification for player VR character control.
This page is the source of truth for system-level scope and capability
boundaries across character control sub-specifications.

## User Requirements

1. Players must experience responsive VR character movement using intuitive
   controller input.
2. Players must experience smooth and precise rotation control that does not
   induce motion sickness.
3. Movement and rotation controls must be independent and simultaneously
   achievable.
4. Control scheme must support configurable sensitivity and smoothing.

## Technical Requirements

1. This parent spec must define system-level capability boundaries and
   normative links to child CTRL contracts.
2. Runtime XR-to-control integration boundaries must remain explicit
   via [XR-001: XRManager](../../xr/001-xr-manager/index.md).
3. Child specifications carry feature-level implementation contracts.
4. Incremental delivery preserves backwards-compatible control semantics.

## In Scope

- A character control system for the player character in VR.
- Runtime XR-to-control bridging for controller input.
- Movement control via left controller stick.
- Rotation control via right controller stick.
- Support for snap turn and smooth turn rotation modes.

## Out Of Scope

- Feature-level implementation details defined by child CTRL specifications.
- Network replication and backend concerns.
- Platform certification and optimisation planning.

## Child Specifications

- [CTRL-001: Locomotion](001-locomotion/index.md)
- [CTRL-002: Hand Grab Input](002-hand-grab-input/index.md)

## Runtime Integration Boundary

- XR runtime contracts are defined in [XR-001: XRManager](../../xr/001-xr-manager/index.md).
- Player XR-to-control runtime bridge is defined in child CTRL specs.
- Control component behaviour is defined by child CTRL specifications.

## Acceptance Criteria

1. The specification defines user-visible control outcomes and
   technical governance contracts.
2. System-level runtime boundaries explicitly reference XR contracts.
3. Child CTRL specifications are identified as normative sources.
4. Capability boundaries are defined for implementation planning.

## References

- [Project Specifications Index](../../index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CTRL-001: Locomotion](001-locomotion/index.md)