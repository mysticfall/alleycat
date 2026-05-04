---
id: CTRL
title: Player Character Control System
---

# Player Character Control System

## Purpose

Define the parent specification for the player character control spec family.

This page is the source of truth for system-level scope, capability levels, and phased delivery intent across character control sub-specifications.

## Requirement

Define an umbrella specification for a player-focused character control system that can be delivered incrementally and expanded with dedicated child specifications.

## Goal

Provide a clear parent/child structure so character control component contracts align under one source of truth without duplicating child-level implementation detail.

## User Requirements

1. Players must experience responsive VR character movement using intuitive controller input.
2. Players must experience smooth and precise rotation control that does not induce motion sickness.
3. Movement and rotation controls must be independent and simultaneously achievable.
4. Control scheme must support configurable sensitivity and smoothing options.

## Technical Requirements

1. This parent spec must define system-level capability boundaries and normative links to child CTRL contracts.
2. Runtime XR-to-control integration boundaries must remain explicit via [XR-001: XRManager](../../xr/001-xr-manager/index.md).
3. Child specifications must carry feature-level implementation contracts and validation criteria.
4. Incremental delivery phases must preserve backwards-compatible control semantics.

## Specification Structure

- Use this page for system intent, scope, capability expectations, and delivery phases.
- Keep feature-level behaviour and acceptance detail in child specifications.

## Child Specifications

- [CTRL-001: Locomotion](001-locomotion/index.md)

## In Scope

- A character control system for the player character in VR.
- Runtime XR-to-control bridging for controller input driving.
- Movement control via left controller stick.
- Rotation control via right controller stick.
- Support for both snap turn and smooth turn rotation modes.
- Movement-driven animation blending with idle and walk states.
- Configurable control sensitivity parameters.

## Out Of Scope

- Feature-level implementation details that are normatively defined by child CTRL specifications.
- Network replication and backend concerns.
- Platform certification and optimisation planning.
- Full contract-level detail for locomotion is defined in [CTRL-001: Locomotion](001-locomotion/index.md).

## Runtime Integration Boundary

- XR runtime contracts and startup state are defined in [XR-001: XRManager](../../xr/001-xr-manager/index.md).
- Player XR-to-control runtime bridge behaviour is defined in child CTRL specifications.
- Control component behaviour remains defined by child CTRL specifications.

## Incremental Delivery Plan

1. **Phase 1: Umbrella Alignment**
   - Establish CTRL as the parent source of truth for control specifications.
   - Define CTRL-001 locomotion scope and contracts.
2. **Phase 2: Locomotion Baseline**
   - Deliver locomotion component interface and concrete implementation.
   - Wire XR controller inputs to locomotion.
3. **Phase 3: Controller Refinement**
   - Add configurable sensitivity and smoothing parameters.
   - Support both snap turn and smooth turn modes.
4. **Phase 4: Extended Control Coverage**
   - Add additional control specifications as needed.

## Acceptance Criteria

1. The specification defines both user-visible control outcomes and technical implementation-governance contracts.
2. System-level runtime boundaries explicitly reference XR contracts.
3. Capability levels and phased delivery plan are defined for implementation planning.
4. Child CTRL specifications are identified as normative sources for feature-level implementation contracts.

## References

- [Project Specifications Index](../../index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CTRL-001: Locomotion](001-locomotion/index.md)