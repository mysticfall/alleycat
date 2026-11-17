---
id: IK-003
title: Leg and Feet IK System
---

# Leg and Feet IK System

## Requirement

Retain rig-neutral lower-limb target resolution and solving independently of locomotion generation.

## Goal

Player and NPC legs follow applicable targets predictably without giving IK ownership of actor movement.

## User Requirements

1. Both reference rigs retain stable knee direction and natural target following.
2. Invalid or inapplicable target providers do not disturb the other leg or ordinary animation following.
3. Lower-limb IK never moves or steers the character.

## Technical Requirements

1. The [Leg-Feet IK Contract](leg-feet-ik-contract.md) normatively owns target synchronisation, provider composition,
   sole actuation, modifier ordering, pole prediction, two-bone solving, and post-modifier observation.
2. The same generic contracts apply to NPC and player rigs without making one rig's scene topology normative for the
   other. `CharacterIK` remains the generic resolver and sole actuator of foot targets, gates, and modifier influence.
3. MotionBricks navigation does not require metadata-driven foot planting, seam anchors, or contact snapshots for
   movement correction. Do not add a foot-IK rescue system to satisfy the prototype; motion quality and realised-pose
   feedback are owned by [NAV-001](../../nav/001-npc-navigation/index.md) and the shared animation contract.
4. Preserve unrelated VR posing, hip reconciliation, and limb-target behaviour. Generic IK validation remains required;
   removal of locomotion-specific planting is not evidence that those behaviours pass.

## In Scope

- Generic lower-limb providers, target actuation, pole prediction, two-bone solving, and both-rig validation.

## Out Of Scope

- Terrain probing, foot-contact adaptation, actor movement policy, and player locomotion restoration.

## Acceptance Criteria

### User Requirement Acceptance

1. Both-rig checks show stable knee direction and target following, including invalid-provider fallback and side
   isolation.
2. Traces show no actor movement caused by lower-limb IK.

### Technical Requirement Acceptance

1. The child contract's fixtures cover generic resolution, modifier ordering, sole actuation, poles, and solving on
   both rigs.
2. Source/composition review finds no locomotion seam-planting dependency or foot-IK rescue in the MotionBricks path.
3. Checks preserve unrelated VR/limb behaviour and report remaining failures without treating removal as a pass.

User Requirements 1–3 map to user acceptance 1–2; Technical Requirements 1–4 map to technical acceptance 1–3.

## References

- [Project Specifications](../../index.md)
- [Leg-Feet IK Contract](leg-feet-ik-contract.md)
- [Shared Animation Contract](../../animation/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
