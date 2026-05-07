---
id: IK
title: Player VRIK System
---

# Player VRIK System

## Purpose

Parent specification for the player VR humanoid IK spec family. Defines system-level scope,
capability levels, and delivery boundaries across IK sub-specifications.

## In Scope

- Full-body VR humanoid IK system for the player character.
- Runtime XR-to-IK bridging for head and hand target driving.
- Baseline support using headset and two controllers.
- Optional refinement paths for additional tracking input when available.
- Wide pose coverage beyond upright-only (kneeling, sitting, crawling, transitions).

## Out Of Scope

- Feature-level solver-node configuration and rig-level implementation (deferred to child specs).
- XR runtime implementation details beyond the XRManager contract.
- Network replication and backend concerns.

## Runtime Integration Boundary

- XR runtime contracts are defined in [XR-001: XRManager](../../xr/001-xr-manager/index.md).
- Player XR-to-IK runtime bridge is defined in [IK Implementation Notes](implementation-notes.md).
- IK component behaviour is defined by child IK specifications.

## Input And Capability Levels

| Level | Input | Expected Outcome |
|-------|-------|------------------|
| 1 | Headset + left/right controllers | Robust upper-body control with stable full-body pose approximation. |
| 2 | Level 1 + hand tracking | Improved hand/arm intent interpretation. Must degrade to Level 1. |
| 3 | Level 1 + body tracking | Improved whole-body pose fidelity. Must degrade to lower levels. |

## Child Specifications

- [IK Implementation Notes](implementation-notes.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](003-leg-feet-ik/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)