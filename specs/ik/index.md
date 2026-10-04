---
id: IK
title: VRIK System
---

# VRIK System

## Requirement

Provide a discoverable parent for reusable character IK, player XR integration, and child component contracts.

## Goal

Characters retain a rig-neutral IK foundation while specialised child specifications define their own ownership and
validation boundaries.

## User Requirements

1. Players experience coherent full-body VR posing from available headset and controller input.
2. NPC and player characters can share reusable IK foundations without forcing common movement policy.
3. Feet plant through IK-003 foot planting on both reference rigs.

## Technical Requirements

1. Reusable `CharacterIK` remains input-source agnostic; player XR integration is a complementary provider layer.
2. Target-intent providers can control applicable limbs, with influence gating their corresponding IK behaviour.
3. IK-003 is the rig-neutral lower-limb parent. It owns foot planting and both-rig validation; its leg child owns
   generic target/provider/pole/two-bone solve.
4. Foot planting takes when a foot is planted from ANIM-002 per-foot planted intervals and pins the ball joint (the
   toe-bone rest-pose origin) horizontally, as defined by IK-003. The metadata grants nothing and authorises no actor
   movement.
5. IK-003's generic lower-limb order is `sync → provider → hip reconciliation (when present) → lower-leg consumers`.
   `CharacterIK` performs generic provider resolution and sole actuation. Lower-limb guidance lives only in IK-003
   and its leg contract; the [IK Implementation Notes](implementation-notes.md) carry no lower-limb guidance beyond
   pointers to them, and unrelated spine or player-VRIK guidance is unchanged.

## In Scope

- IK-family navigation, reusable character IK boundaries, XR integration boundaries, and child-contract ownership.

## Out Of Scope

- Child solver configuration, navigation/player policy, terrain adaptation, and planted-interval authoring (ANIM-002).

## Acceptance Criteria

### User Requirement Acceptance

| Level | Input | Expected Outcome |
|-------|-------|------------------|
| 1 | Headset + left/right controllers | Robust upper-body control with stable full-body pose approximation. |
| 2 | Level 1 + optical hand tracking | Optical wrist drives hand targets; finger-only presentation. |
| 3 | Level 1 + body tracking | Improved whole-body pose fidelity. Must degrade to lower levels. |

1. The project index reaches this page and its IK-003 child contracts.
2. Readers can distinguish reusable IK, player XR integration, and locomotion-policy responsibilities.

Level 2 delivery ([XR-002: Optical Hand Tracking](../xr/002-optical-hand-tracking/index.md)): the optical wrist drives
hand targets through the existing target pipeline, optical presentation is finger-only, VRIK retains wrist/arm
intent, and delivery must degrade to Level 1.

### Technical Requirement Acceptance

1. IK-003 ownership links identify unique planted-interval, foot-planting, solve, and evidence responsibilities.
2. The navigation path does not imply terrain adaptation or a shared NPC/player movement controller.
3. The IK-003 path identifies generic provider resolution before hip and lower-leg consumers, and the implementation
   notes contain no superseded lower-limb guidance.

## Runtime Integration Boundary

- XR runtime contracts are defined in [XR-001: XRManager](../xr/001-xr-manager/index.md).
- The global hand-pose mode and optical hand-pose sources are defined in
  [XR-002: Optical Hand Tracking](../xr/002-optical-hand-tracking/index.md).
- Character IK runtime bridge is defined in [IK Implementation Notes](implementation-notes.md).
- Player XR integration layer is defined in [IK Implementation Notes](implementation-notes.md).
- IK component behaviour is defined by child IK specifications.

## Child Specifications

- [IK Implementation Notes](implementation-notes.md)
- [IK-001: Neck-Spine CCDIK Configuration](001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](002-arm-shoulder-ik/index.md)
- [IK-003: Leg and Feet IK System](003-leg-feet-ik/index.md)
  - [Leg-Feet IK Contract](003-leg-feet-ik/leg-feet-ik-contract.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
- [IK-005: Target Pipeline](005-target-pipeline/index.md)
