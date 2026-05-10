---
id: BODY-002
title: Character Physical Response System
---

# Character Physical Response System

## Requirement

The game must provide natural, believable physical responses when character bodies
interact with each other and with the environment, including collision detection,
ragdoll physics for deceased or incapacitated characters, and impact force handling.

## Goal

Define a unified system architecture for character physical response that covers
collision detection via proxy rigs, physics-driven ragdoll states, and impact
force transfer. This spec establishes the high-level contracts while child specs
detail individual components.

## User Requirements

1. Player hands must not pass through the player's own body during normal
   interaction.
2. Collision handling must not cause visible jitter or instability in VR tracking.
3. Character bodies must respond believably when colliding with environmental
   obstacles and other characters.
4. Deceased or incapacitated characters must transition smoothly to physics-driven
   ragdoll states.
5. Impact forces must transfer naturally between characters and between characters
   and objects, without excessive bouncing or clipping.

## Technical Requirements

1. Character physical response system shall comprise three primary subsystems:
   - **Collision Detection**: Proxy-based collision for animated characters
   - **Ragdoll Physics**: Physics-driven body states for incapacitated characters
   - **Impact Response**: Force transfer handling for collision events
2. The system shall support both player characters and non-player characters with
   consistent collision contracts.
3. Collision detection shall use bone-attached proxy bodies that follow animated
   poses while maintaining physical collision presence.
4. Ragdoll activation shall disable IK-driven pose control and enable physics-driven
   body simulation with appropriate joint constraints.
5. Impact response shall provide configurable force transfer channels that can be
   tuned per character type without hardcoded prop-specific rules.
6. All subsystems shall respect VR tracking stability requirements; physical
   responses must not override or conflict with target-driven head/hand tracking.

## In Scope

- System architecture for character physical response (collision, ragdoll,
  impact).
- Unified collision-layer contract for character proxy bodies.
- Integration boundaries between IK-driven animation and physics-driven ragdoll.
- Force transfer channels for impact response.
- Configuration parameters for character-type-specific tuning.

## Out Of Scope

- Detailed ragdoll joint configuration and constraint tuning (deferred to child
  spec).
- Specific prop interaction physics (handled by ITEM-001: Physics Chain Asset).
- Locomotion responsiveness under collision response (handled by CTRL-001).
- Network synchronization for multiplayer character physics.

## Acceptance Criteria

1. Character physical response system architecture is documented and covers
   collision, ragdoll, and impact subsystems.
2. Unified collision-layer contract exists and is applied consistently across
   character types.
3. Transition contract between IK-driven and physics-driven states is defined
   and preserves VR tracking stability.
4. Force transfer channels are configurable without prop-specific hardcoding.
5. Child specifications exist for each primary subsystem and reference this
   umbrella spec as their parent.

## References

- [BODY-003: Full-Body Collision Decision](@specs/body/003-full-body-collision-decision/index.md)
- [DynamicPhysicalRig Implementation](@game/src/Body/DynamicPhysicalRig.cs)
- [Character Skeleton Profile](@specs/characters/000-character-skeleton/index.md)
- [CTRL-001: Locomotion](@specs/characters/ctrl/001-locomotion/index.md)
- [ITEM-001: Physics Chain Asset](@specs/items/001-physics-chain/index.md)
