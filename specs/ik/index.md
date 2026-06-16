---
id: IK
title: VRIK System
---

# VRIK System

## Purpose

Parent specification for the VR humanoid IK spec family. Defines system-level scope,
capability levels, and delivery boundaries across IK sub-specifications. Covers both
reusable character IK for NPCs and player-specific XR integration.

## User Requirements

1. Players must experience full-body VR humanoid IK that follows headset and controller inputs.
2. Characters must have a reusable character IK foundation that is agnostic to input source.
3. Head, hands, feet, and other limbs must all support provider-driven target and influence control.

## Technical Requirements

1. The IK system must provide a reusable base architecture supporting any humanoid character.
2. Player-specific XR integration must attach to the base as a complementary layer.
3. IKTargetIntentProvider must be extensible to all limbs in the same manner as hands.
4. Provider influence must gate corresponding IK modifiers including indirect side effects.

## In Scope

- Reusable full-body humanoid IK system for any humanoid character.
- IKTargetIntentProvider architecture covering head, hands, feet, and extensible to other limbs.
- Player-specific XR-to-IK bridging for head and hand target driving.
- Runtime XR-to-IK bridging for head and hand target driving.
- Baseline support using headset and two controllers.
- Optional refinement paths for additional tracking input when available.
- Wide pose coverage beyond upright-only (kneeling, sitting, crawling, transitions).

## Out Of Scope

- Feature-level solver-node configuration and rig-level implementation (deferred to child specs).
- XR runtime implementation details beyond the XRManager contract.
- Network replication and backend concerns.

## Input And Capability Levels

| Level | Input | Expected Outcome |
|-------|-------|------------------|
| 1 | Headset + left/right controllers | Robust upper-body control with stable full-body pose approximation. |
| 2 | Level 1 + hand tracking | Improved hand/arm intent interpretation. Must degrade to Level 1. |
| 3 | Level 1 + body tracking | Improved whole-body pose fidelity. Must degrade to lower levels. |

## Runtime Integration Boundary

- XR runtime contracts are defined in [XR-001: XRManager](../xr/001-xr-manager/index.md).
- Character IK runtime bridge is defined in [IK Implementation Notes](implementation-notes.md).
- Player XR integration layer is defined in [IK Implementation Notes](implementation-notes.md).
- IK component behaviour is defined by child IK specifications.

## Layered Architecture

### General Character IK Layer
- Reusable `CharacterIK` component applicable to any humanoid character.
- Operates independently of input source.
- Accepts IKTargetIntentProvider for all configurable limbs.

### Player XR Integration Layer
- Attaches to `CharacterIK` as a complementary provider layer.
- Exposes fallback provider properties for XR hand-controller target providers.
- Falls back gracefully when XR is unavailable.

### Provider Architecture
- IKTargetIntentProvider is a general abstraction for any limb.
- Provider influence gates corresponding IK modifiers and side effects.
- Fallback providers are implemented as provider subclasses.

## Child Specifications

- [IK Implementation Notes](implementation-notes.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](001-neck-spine-ik/index.md)
- [IK-002: Arm And Shoulder IK System](002-arm-shoulder-ik/index.md)
- [IK-003: Leg And Feet IK System](003-leg-feet-ik/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](004-vrik-pose-state-machine/index.md)
