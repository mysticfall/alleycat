---
id: CTRL
title: Player Character Control System
---

# Player Character Control System

## Purpose

Define the parent specification for player VR character control.
This page is the source of truth for system-level scope and capability
boundaries across character control sub-specifications. Control input spans
XR controller input and optical hand input; hand grab input accepts XR
controller grab buttons in every committed mode and optical closure only in
`Optical` mode
([XR-002: Optical Hand Tracking](../xr/002-optical-hand-tracking/index.md),
routed by [CTRL-002: Hand Grab Input](002-hand-grab-input/index.md)).

## User Requirements

1. Players must experience responsive VR character movement using intuitive
   controller input.
2. Players must experience smooth and precise rotation control that does not
   induce motion sickness.
3. Movement and rotation controls must be independent and simultaneously
   achievable.
4. Control scheme must support configurable sensitivity and smoothing.
5. Rotation must remain smooth and continuous; snap turns are not supported in
   the current control scope.
6. Players must be able to grab and release objects using XR controller input
   in every committed mode, and additionally by natural optical hand closure
   while the committed hand-pose mode is `Optical`.

## Technical Requirements

1. This parent spec must define system-level capability boundaries and
   normative links to child CTRL contracts.
2. Runtime XR-to-control integration boundaries must remain explicit
   via [XR-001: XRManager](../xr/001-xr-manager/index.md).
3. Child specifications carry feature-level implementation contracts.
4. Incremental delivery preserves established smooth control semantics.
5. Grab input routing is defined by
    [CTRL-002: Hand Grab Input](002-hand-grab-input/index.md): controller grab
    edges are honoured in every committed mode while optical grab recognition
    acts only in `Optical` mode, and non-grab controller consumers are
    unaffected in both modes. Locomotion remains controller-based (CTRL-001).

## In Scope

- A character control system for the player character in VR.
- Runtime XR-to-control bridging for controller input and optical hand input.
- Movement control via left controller stick.
- Rotation control via right controller stick.
- Smooth, continuous rotation control.
- Grab input across controller and optical sources (CTRL-002).

## Out Of Scope

- Feature-level implementation details defined by child CTRL specifications.
- Network replication and backend concerns.
- Platform certification and optimisation planning.
- Snap turning. It may be reconsidered only as a new player-specific feature.

## Child Specifications

- [CTRL-001: Locomotion](001-locomotion/index.md)
- [CTRL-002: Hand Grab Input](002-hand-grab-input/index.md)

## Runtime Integration Boundary

- XR runtime contracts are defined in [XR-001: XRManager](../xr/001-xr-manager/index.md).
- Player XR-to-control runtime bridge is defined in child CTRL specs.
- Control component behaviour is defined by child CTRL specifications.

## Acceptance Criteria

### User Requirement Acceptance

1. Control outcomes specify independent, simultaneous movement and smooth,
   continuous rotation without snap turns.
2. Control sensitivity and smoothing remain configurable at the system level.
3. Grab input is available through controller grab buttons in every committed
   mode, with optical closure available additionally in `Optical` mode.

### Technical Requirement Acceptance

1. System-level runtime boundaries explicitly reference XR contracts.
2. Child CTRL specifications are identified as normative sources for
   feature-level implementation contracts.
3. Capability boundaries define snap turning as out of scope for the current
   control system.
4. Grab input routing is delegated to CTRL-002: controller edges are
   mode-independent, optical recognition is `Optical`-only, and non-grab
   controller consumers are unaffected in both modes.

## References

- [Project Specifications Index](../index.md)
- [XR-001: XRManager](../xr/001-xr-manager/index.md)
- [CTRL-001: Locomotion](001-locomotion/index.md)
