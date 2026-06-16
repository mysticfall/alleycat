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
3. **Tension Stability**: Under sustained tension,
   the chain does not visibly or physically stretch
   beyond a bounded tolerance. Attached weights or
   endpoints do not drop due to links straightening
   beyond the configured angular envelope.
4. **End Attachment**: Each chain end provides a
   reliable attachment point where a physics body
   can be connected and will remain firmly
   attached while the chain moves.
5. **Arbitrary Length**: The chain supports any
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
    must connect via a rotation-limited joint that
    constrains angular travel while maintaining
    positional constraint. Free-rotation joints
    (for example, unconstrained `PinJoint3D`) are
    not permitted for chain link connections.
4. **Joint Configuration**:
    - Use a rotation-limited joint type with
      configurable angular limits (for example,
      `ConeTwistJoint3D` is the preferred/default
      candidate for chain links).
    - Implementation may select an alternative
      Godot joint type if evidence demonstrates
      superior stability or performance.
    - Enable collision exclusion between
      linked bodies.
    - Apply damping to reduce oscillation
      while preserving flexibility.
    - Configure angular limits on at least
      the swing axis to preserve link interlock
      under tension.
    - Configure linear limits if supported
      to bound structural extension.
    - Note: unsupported Jolt soft-limit
      properties must not be relied upon as
      the sole stability mechanism.
5. **Rotation-Limited Joint Contract**:
    - Joint parameters must expose configurable
      angular limits (for example, swing_span,
      twist_span) that bound link rotation.
    - Default values must preserve chain flexibility
      while preventing unrealistic straightening
      under end-weight tension.
    - Reasonable defaults: swing_span between
      15-45 degrees, twist_span between 30-60
      degrees, with bias/softness tuned to resist
      stretching without introducing instability.
6. **Attachment Points**: The first and last links
    must expose a named `Marker3D` at the
    outermost extent for external body attachment.
7. **Link Mass**: All links must be `RigidBody3D`
    nodes with appropriately scaled mass for
    believable dynamics.
8. **Collision Layers**: Configure collision
    layers to interact with the environment
    while avoiding self-collision instability.

## In Scope

- Configurable chain instantiation with link count.
- Physics-based movement and interaction.
- End attachment points for external objects.
- Joint configuration for stable behaviour.
- Collision handling to prevent instability.
- Rotation-limited joints with bounded extension.

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
3. **UR-3 Verification**: Apply sustained tension
   to the chain (for example, attach a weight at
   one end and hold the other); verify the chain
   maintains bounded extension and the attached
   weight does not drop due to link straightening.
4. **UR-4 Verification**: Attach a physics body object
   to one chain end using the designated attachment
   point; when the chain swings, the attached object
   follows the chain end without detaching.
5. **TR-1 Verification**: Instantiate a chain with
   5 links; inspect the scene tree and verify each
   link is a chain_link.tscn instance with correct
   90-degree offset orientation relative to its
   neighbour.
6. **TR-2 Verification**: Inspect joint nodes between
   consecutive links; verify a joint with configurable
   limits exists for each pair and is configured with
   angular limits that preserve link interlock.
7. **TR-3 Verification**: Verify the joint configuration
    includes linear limits or equivalent constraints
    that bound structural extension under tension.
8. **TR-4 Verification**: Verify the first and last
    links each expose a named Marker3D at the chain
    ends that can serve as a parent node for external
    objects.
9. **TR-5 Verification**: Verify the joint type used
    between consecutive links supports configurable
    angular limits (for example, swing_span, twist_span
    for `ConeTwistJoint3D`, or equivalent parameters
    for the selected joint type).
10. **TR-6 Verification**: Verify the joint defaults
    preserve chain flexibility while bounding
    straightening: under 5 kg end-weight tension, the
    chain endpoint span increase must remain below
    15% of the rest length.

## Rationale

The previous `PinJoint3D` implementation allowed
excessive rotation at each joint. Under sustained
tension, links could straighten beyond their
configured envelope, causing the chain endpoint
span to grow (for example, from 0.78 m to 0.88 m
under 5 kg end-weight). The attached endpoint then
dropped as the chain straightened. Rotation-limited
joints with bounded linear extension resolve this
while preserving flexibility. `ConeTwistJoint3D`
is the preferred candidate as it exposes swing_span
and twist_span parameters that directly constrain
angular travel, though implementation may select an
alternative joint type if evidence demonstrates
superior stability.

## References

- **Chain Link Asset**: `@game/assets/items/chain/chain_link.tscn`
- **Main Implementation**: `@game/src/Items/PhysicsChain.cs`
- **Authored Scene**: `@game/assets/items/chain/physics_chain.tscn`
- **Integration Tests**: `@integration-tests/src/Items/PhysicsChainIntegrationTests.cs`
- **Visual Verification Test**:
  - Scene: `@game/tests/item/chain/physics_chain_visual_test.tscn`
  - Script: `@game/tests/item/chain/physics_chain_visual_test.gd`
