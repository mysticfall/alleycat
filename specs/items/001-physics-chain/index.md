---
id: ITEM-001
title: Physics Chain Asset
---

# Physics Chain Asset

## Requirement

Deliver a reusable physics chain asset composed of an arbitrary number of chain links (using `@game/assets/items/chain/chain_link.tscn`) that maintains structural integrity under physics simulation and provides attachment points for external physics bodies at both chain ends.

## Goal

Enable gameplay scenarios where players can interact with, pick up, swing, or attach flexible chain-like objects in the VR environment. The chain must behave believably under physics forces without breaking apart, and designers must be able to easily integrate the chain as a component in larger interactive systems.

## User Requirements

1. **Physics Responsiveness**: When forces are applied to any part of the chain, the entire chain reacts smoothly and realistically, with links pulling and pushing each other through the chain.
2. **Structural Integrity**: Under normal gameplay physics forces, the chain links remain connected and do not separate, pass through each other, or exhibit unstable jitter.
3. **End Attachment**: Each chain end provides a reliable attachment point where a physics body can be connected and will remain firmly attached while the chain moves.
4. **Arbitrary Length**: The chain can be configured to contain any reasonable number of links (minimum 2) to suit different gameplay needs.

## Technical Requirements

1. **Chain Composition**: The chain must be composed of two or more instances of the `chain_link.tscn` scene, instantiated as child nodes in a container node.
2. **Link Connection**: Consecutive links must be connected with a 90-degree rotational offset around the Z-axis (local), creating a visually authentic chain appearance where links interlock.
3. **Physics Joints**: Each consecutive pair of links must be connected via a `PinJoint3D` (or equivalent physics joint) that permits rotational freedom while maintaining positional constraint.
4. **Joint Configuration**: Inter-link joints must be configured for stable chain behaviour:
   - Use `PinJoint3D` joints between consecutive links.
   - Enable collision exclusion between linked bodies to prevent self-collision instability.
   - Apply damping to reduce oscillation while preserving chain flexibility.
   - Allow rotational freedom around all three local axes (PinJoint3D default).
5. **Attachment Points**: The first and last links in the chain must expose a clearly identifiable attachment mechanism (e.g., a dedicated `Marker3D` or `RemoteTransform3D` node) positioned at the outermost extent of the chain for attaching external `PhysicsBody3D` nodes.
6. **Physics Body Mass**: All chain links must be `RigidBody3D` nodes with appropriately scaled mass to produce believable chain dynamics (links should not be overly heavy or light relative to each other).
7. **Collision Layers**: Links must be configured with appropriate collision layers to interact with the game environment while avoiding self-collision issues that could cause instability.

## In Scope

- Chain instantiation with configurable link count.
- Physics-based movement and interaction.
- End attachment points for external objects.
- Joint configuration for stable chain behaviour.
- Collision handling to prevent instability.

## Out Of Scope

- Specific tuning values for mass, damping, joint parameters (deferred to implementation with reasonable defaults).
- Visual polish beyond the base chain link model (materials, effects).
- Animation or procedural movement (purely physics-driven).
- Audio feedback for chain interactions.
- Multi-chain entanglement or complex chain-on-chain physics beyond individual chain integrity.

## Acceptance Criteria

1. **UR-1 Verification**: Apply a lateral force to one end of a chain with 10+ links; the force propagates through the chain with visible delay and the chain swings without links separating.
2. **UR-2 Verification**: Subject the chain to rapid movement and gravity; after 30 seconds of simulation, all joints remain connected and no link has drifted from its expected position relative to neighbours.
3. **UR-3 Verification**: Attach a physics body object to one chain end using the designated attachment point; when the chain swings, the attached object follows the chain end without detaching.
4. **TR-1 Verification**: Instantiate a chain with 5 links; inspect the scene tree and verify each link is a chain_link.tscn instance with correct 90-degree offset orientation relative to its neighbour.
5. **TR-2 Verification**: Inspect joint nodes between consecutive links; verify a PinJoint3D exists for each pair and is correctly configured for rotational freedom.
6. **TR-3 Verification**: Verify the first and last links each expose a named Marker3D (or equivalent) at the chain ends that can serve as a parent node for external objects.

## References

- **Chain Link Asset**: `@game/assets/items/chain/chain_link.tscn`
- **Main Implementation**: `@game/src/Items/PhysicsChain.cs`
- **Authored Scene**: `@game/assets/items/chain/physics_chain.tscn`
- **Integration Tests**: `@integration-tests/src/Items/PhysicsChainIntegrationTests.cs`
- **Visual Verification Test**:
  - Scene: `@game/tests/items/chain/physics_chain_visual_test.tscn`
  - Script: `@game/tests/items/chain/physics_chain_visual_test.gd`
