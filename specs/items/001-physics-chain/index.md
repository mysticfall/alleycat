---
id: ITEM-001
title: Physics Chain Asset
---

# Physics Chain Asset

## Requirement

Provide a reusable physics chain with
configurable links that maintains structural
integrity and provides attachment points.

## Goal

Enable VR gameplay where players interact
with flexible chains. Chains must behave
believably under physics without breaking
apart.

## User Requirements

1. **Physics Responsiveness**: When forces apply to
   any part of the chain, the entire chain reacts
   smoothly and realistically, with links pulling
   and pushing each other through the chain.
2. **Structural Integrity**: Under normal gameplay
   physics forces, the chain links remain connected
   and do not separate, pass through each other,
   or exhibit unstable jitter.
3. **End Attachment**: Each chain end provides a
   reliable attachment point where a physics body
   can be connected and will remain firmly
   attached while the chain moves.
4. **Arbitrary Length**: The chain supports any
   reasonable number of links to suit different
   gameplay needs.

## Technical Requirements

1. **Chain Composition**: The chain must comprise
   two or more instances of `chain_link.tscn`
   as child nodes in a container.
2. **Link Orientation**: Consecutive links must
   rotate 90 degrees around the Z-axis (local)
   to interlock visually.
3. **Physics Joints**: Each consecutive link pair
   must connect via a joint permitting rotational
   freedom while maintaining positional constraint.
4. **Joint Configuration**:
   - Use `PinJoint3D` between consecutive
     links.
   - Enable collision exclusion between
     linked bodies.
   - Apply damping to reduce oscillation
     while preserving flexibility.
   - Permit rotational freedom around all
     three local axes.
5. **Attachment Points**: The first and last links
   must expose a named `Marker3D` at the
   outermost extent for external body attachment.
6. **Link Mass**: All links must be `RigidBody3D`
   nodes with appropriately scaled mass for
   believable dynamics.
7. **Collision Layers**: Configure collision
   layers to interact with the environment
   while avoiding self-collision instability.

## In Scope

- Configurable chain instantiation with link count.
- Physics-based movement and interaction.
- End attachment points for external objects.
- Joint configuration for stable behaviour.
- Collision handling to prevent instability.

## Out Of Scope

- Specific tuning values (mass, damping, joint parameters) deferred to implementation.
- Visual polish (materials, effects) beyond the base model.
- Animation or procedural movement (purely physics-driven).
- Audio feedback for chain interactions.
- Multi-chain entanglement or chain-on-chain physics beyond individual integrity.

## Acceptance Criteria

1. **UR-1 Verification**: Apply a lateral force to one
   end of a chain with 10+ links; the force propagates
   through the chain with a visible delay and the chain
   swings without links separating.
2. **UR-2 Verification**: Subject the chain to rapid
   movement and gravity; after 30 seconds of
   simulation, all joints remain connected and no
   link has drifted from its expected position
   relative to neighbours.
3. **UR-3 Verification**: Attach a physics body object
   to one chain end using the designated attachment
   point; when the chain swings, the attached object
   follows the chain end without detaching.
4. **TR-1 Verification**: Instantiate a chain with
   5 links; inspect the scene tree and verify each
   link is a chain_link.tscn instance with correct
   90-degree offset orientation relative to its
   neighbour.
5. **TR-2 Verification**: Inspect joint nodes between
   consecutive links; verify a PinJoint3D exists for
   each pair and is correctly configured for
   rotational freedom.
6. **TR-3 Verification**: Verify the first and last
   links each expose a named Marker3D at the chain
   ends that can serve as a parent node for external
   objects.

## References

- **Chain Link Asset**: `@game/assets/items/chain/chain_link.tscn`
- **Main Implementation**: `@game/src/Items/PhysicsChain.cs`
- **Authored Scene**: `@game/assets/items/chain/physics_chain.tscn`
- **Integration Tests**: `@integration-tests/src/Items/PhysicsChainIntegrationTests.cs`
- **Visual Verification Test**:
  - Scene: `@game/tests/items/chain/physics_chain_visual_test.tscn`
  - Script: `@game/tests/items/chain/physics_chain_visual_test.gd`
