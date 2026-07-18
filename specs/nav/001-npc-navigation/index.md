---
id: NAV-001
title: NPC Navigation
---

# NPC Navigation

## Requirement

Define an NPC-oriented navigation component API backed by Godot navigation concepts, with a minimal direct-transform
implementation for baseline testing and early behaviour authoring.

## Goal

NPC-capable actors can request destinations, inspect route-following state, and expose navigation as a component without
coupling callers to Godot `NavigationAgent3D` nodes or future animation-specific movement systems.

## User Requirements

1. NPCs and other navigable scene objects can be given a world-space destination with final facing intent.
2. The destination transform is intent: `Origin` is the desired destination and `Basis` is desired final orientation.
3. Implementations may approximate or ignore inapplicable orientation parts such as pitch or roll, but final facing is a
   first-class part of the navigation destination.
4. Navigable actors or objects own references to their navigation component; the component does not discover or own
   NPCs.
5. Existing VR player locomotion remains tracker-driven and unchanged by this navigation slice.

## Technical Requirements

1. Provide an `INavigation` component contract using Godot-native `Vector3` and `Transform3D` value types.
2. Provide an `INavigator` holder trait for objects that expose navigation through the component-holder pattern.
3. Destination-setting returns a small result enum with accepted, unreachable, and invalid outcomes; running state is
   queried through properties instead of a destination-setting boolean.
4. The public facade exposes Godot-aligned path queries: next path position, current path, current path index,
   navigation running/finished state, and core distance thresholds using destination-facing terminology.
5. The public facade includes an initial avoidance configuration shape, but does not expose safe-velocity callback
   mechanics in this slice.
6. A Godot-backed abstract base inherits directly from `NavigationAgent3D` and must not expose that concrete agent type
   through the `INavigation` facade.
7. `DirectTransformNavigation` directly moves an optional explicitly configured `Node3D` `Target`, falling back to the
   closest `Node3D` ancestor when unset, and is baseline/test-oriented only.
8. The moved `Target` should normally be an ancestor of the navigation node so the inherited `NavigationAgent3D`
   transform follows the moved object. Non-ancestor targets are unsupported unless synchronised externally, for
   example with `RemoteTransform3D`.
9. The API remains a navigation/path-finding facade and must not introduce motion-matching-specific vocabulary.

## In Scope

- NAV-001 spec and index link.
- Navigation component contract, holder trait, destination result enum, and Godot-backed base class.
- Simple direct-transform navigation implementation with exported moved target node and movement speed.
- Core path, threshold, navigation state, and initial avoidance configuration forwarding.

## Out Of Scope

- Rich motion-matching trajectory prediction APIs.
- Production-quality root-motion reconciliation or animation-quality NPC movement.
- Off-mesh link traversal and custom route semantics.
- Safe-velocity events, callbacks, or detailed avoidance steering policy beyond initial configuration forwarding.
- Final tuning values for speed, thresholds, radius, height, layers, or priority.

## Acceptance Criteria

1. User requirements pass when a navigable NPC-oriented object can accept a `Transform3D` destination while preserving
   final facing as explicit intent and leaving VR player locomotion unchanged.
2. User requirements pass when `DirectTransformNavigation` can drive a configured ancestor `Node3D`, or its closest
   `Node3D` ancestor fallback, as a simple baseline rather than a production movement solution.
3. Technical requirements pass when callers can depend on `INavigation` and `INavigator` without receiving or depending
   on a public `NavigationAgent3D` instance.
4. Technical requirements pass when the API exposes destination result, path inspection, running/finished state,
   distance thresholds, and initial avoidance configuration without motion-matching terms.
5. Technical requirements pass when the Godot-backed base centralises direct `NavigationAgent3D` inheritance and the
   concrete direct-transform class only applies the movement strategy.

## References

- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
- `game/src/Navigation/`
