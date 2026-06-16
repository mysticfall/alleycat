---
id: BODY-002
title: Character Physical Response System
---

# Character Physical Response System

## Requirement

The game must provide natural, believable character responses to physical interactions
including impacts, pushes, grabs, restraints, and recovery, while preserving authored
animation and IK readability.

## Goal

Define an umbrella architecture for character physical response that enables transient
pose modification, physical constraint enforcement, and recovery integration. This spec
establishes the high-level intent and data-flow contracts while child specs detail
individual response modes.

## User Requirements

1. Player must experience believable directional head and hand pushes from NPCs
   without breaking VR comfort or tracking stability.
2. Characters must exhibit appropriate flinches, partial staggers, and recovery
   transitions based on impact severity.
3. Grabbed or restrained limbs must respond to physical limits while remaining
   composable with other constraints.
4. Chain and rope restraints must enforce limits while allowing natural movement
   within those limits.
5. Recovery from transient responses must smoothly reintegrate authored poses and
   animations.

## Technical Requirements

1. The system shall implement a pipeline: source intent → transient target modification
   → relationship/legal-space constraints → physical target realisation/actuation →
   feedback → recovery/pose integration.
2. Source contributors shall include XR hand/head targets, animation poses, AI reach,
   procedural foot placement, and look-at intents.
3. Modifier contributors shall include impact recoil, stagger drift, fatigue/weakness,
   fear/flinch, recovery springs, animation noise, and pain/injury states.
4. Constraint contributors shall include chain length, grab anchors, joint/anatomical
   ranges, leash radii, surface/ground planes, body exclusion zones, and two-hand
   object relations.
5. The source/modifier/constraint distinction shall remain a mental frame for design;
   implementation may initially use a single contributor interface for modifier and
   constraint, deferring separate interfaces to implementation when justified.
6. Actuators (chains, joints, grabs, collisions) must compose rather than operate as
   mutually exclusive modes—for example, a chained hand must still collide with walls
   and body parts within chain limits.
7. Player virtual head/IK target may be physically displaced by NPC head grabs and
   shoves under comfort gates. XR origin/world compensation may move the world briefly
   as an intended effect. Raw XR hardware tracking input remains authoritative for
   player pose; only virtual response targets and temporary XR origin/world compensation
   may be displaced under comfort gates.
8. DynamicPhysicalRig, BODY collision proxy generation, and physical-response BODY
   slices shall not depend on IK. The IK/pose/target pipeline may consume Body
   profiles, proxies, contact, and actuator information. Existing specialised
   hand-pose/body-hand integration code is outside this proxy-generation dependency
   boundary unless or until it is separately cleaned up.
9. Effect magnitude shall depend on actor/target strength, stance, fatigue, severity,
   and break thresholds. Exact tuning is deferred to child specs.
10. Debugging and validation shall expose raw source target, modified target,
    constrained request, realised target, and feedback reasons.

## In Scope

- Umbrella architecture for transient pose modification and constraint enforcement.
- Source/modifier/constraint mental model and data-flow contract.
- Composable actuator design (chains, joints, grabs, collisions).
- Player head displacement by NPC interaction with XR origin compensation.
- Recovery integration with authored animation and IK.
- Debugging/validation instrumentation contracts.
- Child spec boundaries for impact reaction, stagger, grab constraints, and ragdoll
  bridge.
- BODY-003 boundary: BODY-003 handles collision/proxy/hand-obstruction decisions.
  Full reactive target pipeline details are future BODY-002 child work.

## Out Of Scope

- Specific ragdoll joint configuration and constraint tuning (deferred to child spec).
- Detailed locomotion response under collision (handled by CTRL-001).
- Network synchronisation for multiplayer character physics.
- Concrete implementation details for individual reactive target pipeline stages
  (delegated to future BODY-002 child specs).
- Cleanup of existing specialised hand-pose/body-hand integration code outside the
  collision proxy-generation dependency boundary.

## Acceptance Criteria

1. Umbrella spec documents the intent, data-flow architecture, and contract boundaries.
2. Design notes preserve detailed brainstorming, examples, concerns, and mental models.
3. Source/modifier/constraint distinction is documented as a mental frame, with
   implementation flexibility noted.
4. Composable actuator principle is stated and referenced by child specs; actuators
   must compose rather than operate as mutually exclusive modes.
5. User Requirement 2 (flinch/stagger behaviour and recovery transitions) is traced
   to pipeline stage contracts in Technical Requirement 1.
6. User Requirements 3-4 (restraints/chains/recovery) are traced to constraint
   contributor types in Technical Requirement 4.
7. Technical Requirement 6 (composable actuators) is validated by acceptance criteria
   in child specs addressing chains, joints, grabs, and collisions.
8. Technical Requirement 8 (DynamicPhysicalRig/IK dependency direction) is validated
   by confirming DynamicPhysicalRig, BODY collision proxy generation, and
   physical-response BODY slices do not depend on IK; IK may consume Body profiles,
   proxies, contact, and actuator data.
9. Technical Requirement 7 (XR authority) is validated: raw XR tracking input remains
   authoritative; only virtual response targets and temporary XR origin/world
   compensation may be displaced under comfort gates.
10. Debugging instrumentation (Technical Requirement 10) is validated by child spec
    acceptance criteria requiring raw source, modified, constrained, realised, and
    feedback output exposure.
11. Child spec candidates are identified for impact, stagger, grab, and ragdoll work,
    with traceability matrix linking each to parent User/Technical Requirements.

## References

- [BODY-002 Design Notes](design-notes.md)
- [BODY-005: IK Target Pipeline Foundation](../005-ik-target-pipeline-foundation/index.md)
- [BODY-008: Character Physical Interaction API](../008-character-stimulus-detection-and-routing/index.md)
- [BODY-003: Full-Body Collision Decision](../003-full-body-collision-decision/index.md)
- [DynamicPhysicalRig Implementation](../../../game/src/Body/DynamicPhysicalRig.cs)
- [Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- [ITEM-001: Physics Chain Asset](../../item/001-physics-chain/index.md)
