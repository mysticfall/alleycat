---
id: CORE-003
title: Component/Trait System
---

# Component/Trait System

## Requirement

Establish a compositional system for game features using component capability
traits, enabling reusable behaviour to be attached to game entities through
capability interfaces. The system must provide deterministic component
resolution with fail-fast semantics, supporting both Godot nodes and plain C#
objects as component holders.

## Goal

Enable flexible composition of game features through capability interfaces
while maintaining clear separation between component capability contracts
(what a component provides) and holder capability contracts (what an entity
can do). The system must support multiple components per holder, multiple
interfaces per component, and deterministic query ordering.

## User Requirements

1. Developers must be able to define capability interfaces that components
   implement to expose behaviour.
2. Developers must be able to define trait interfaces that holders implement
   to expose what they can do.
3. Components must be discoverable through type-safe queries on their holders.
4. The system must provide deterministic iteration order for component queries.
5. Failed component lookups must produce clear error messages indicating holder
   identity and requested type.
6. The system must support both Godot nodes and plain C# objects as component
   holders.
7. Components must be able to implement multiple capability interfaces.
8. Extension methods must provide consistent fail-fast patterns matching
   existing codebase conventions.

## Technical Requirements

1. A new C# namespace `AlleyCat.Core` must be created for all component
   system types.
2. An `IComponent` marker interface must be defined as the root interface for
   all components.
3. An `IComponentHolder` interface must be defined with
   `IReadOnlyList<IComponent> Components { get; }` and deterministic iteration.
4. Component query extension methods must be provided on `IComponentHolder`:
   - `bool TryGetComponent<T>(out T? component)` — returns true when exactly
     one match is found.
   - `IReadOnlyList<T> GetComponents<T>()` — returns all matching components.
   - `T RequireComponent<T>()` — returns the single match or throws.
5. `RequireComponent<T>` must throw `InvalidOperationException` when zero or
   more than one match is found. The message must include the holder's
   implementation type and, for Godot Nodes, `node.Name` and `node.GetPath()`,
   plus the requested type.
6. `GetComponents<T>` must return components in deterministic holder-defined
   order.
7. Components may implement multiple capability interfaces.
8. Capability trait interfaces may use C# default interface members where typed
   consumption is intended.
9. Component capability interfaces must name behaviour (e.g., `ILocomotion`,
   `ISpeechGenerator`).
10. Holder trait interfaces must use adjective/noun style (e.g., `ILocomotive`,
    `ISpeaking`, `IAnimatable`, `ISeeing`) unless `IHasX` is clearer.
11. The MVP implementation must use explicit/cached holder collections, not
    implicit recursive discovery.
12. Extension methods must use the `extension` block syntax consistent with
    `@game/src/Common/NodeExtensions.cs`.

## In Scope

- `IComponent` and `IComponentHolder` core interfaces.
- `TryGetComponent<T>`, `GetComponents<T>`, `RequireComponent<T>` extension
  methods.
- Fail-fast semantics with detailed error messages.
- Support for Node-based and plain object holders.
- Default interface member support for capability traits.
- Multiple capability interfaces per component.
- Naming conventions for capability and trait interfaces.
- Deterministic component iteration order.
- Explicit/cached holder collection strategy for MVP.

## Out Of Scope

- Implicit recursive component discovery across scene trees.
- Component lifecycle management (activation/deactivation).
- Component dependency injection or wiring.
- Hot-reloading of components at runtime.
- Component serialisation or persistence.
- Dynamic component composition (adding/removing at runtime beyond MVP).
- Component event bus or observer patterns.

## Acceptance Criteria

1. A developer can define a capability interface and a holder trait interface,
   and attach a component to a holder that implements both.
2. `TryGetComponent<T>` returns true with the component when exactly one match
   exists, and false otherwise.
3. `GetComponents<T>` returns all matching components in deterministic order.
4. `RequireComponent<T>` throws `InvalidOperationException` with holder type
   and path information when zero or multiple matches are found.
5. The same holder type supports multiple components each implementing multiple
   capability interfaces.
6. Extension methods follow the `extension` block pattern from
   `@game/src/Common/NodeExtensions.cs`.
7. The spec does not exclude mandatory resolution API through `Out Of Scope`.
8. CORE-003 is listed under Core Systems in `specs/index.md`.

## References

- `@game/src/Core/` — New namespace directory for component system types
- `@game/src/Common/NodeExtensions.cs` — Reference for extension method style
- [CORE-001: Global Singleton](../001-global-scene/index.md)
- [CORE-002: Configuration API](../002-configuration-api/index.md)
- [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md)
