---
id: CORE-003
title: Component/Trait System
---

# Component/Trait System

## Requirement

Establish a compositional system for game features using component capability traits, enabling reusable behaviour to be attached to game entities through capability interfaces. The system must provide deterministic component resolution with fail-fast semantics, supporting both Godot nodes and plain C# objects as component holders.

## Goal

Enable flexible composition of game features through capability interfaces while maintaining clear separation between component capability contracts (what a component provides) and holder capability contracts (what an entity can do). The system must support multiple components per holder, multiple interfaces per component, and deterministic query ordering.

## User Requirements

1. Developers must be able to define capability interfaces that components implement to expose behaviour.
2. Developers must be able to define trait interfaces that holders implement to expose what they can do.
3. Components must be discoverable through type-safe queries on their holders.
4. The system must provide deterministic iteration order for components across multiple queries.
5. Failed component lookups must produce clear error messages indicating holder identity and requested type.
6. The system must support both Godot nodes and plain C# objects as component holders.
7. Components must be able to implement multiple capability interfaces.
8. Extension methods must provide consistent fail-fast patterns matching existing codebase conventions.

## Technical Requirements

1. A new C# namespace `AlleyCat.Component` must be created for all component system types.
2. An `IComponent` marker interface must be defined as the root interface for all components.
3. An `IComponentHolder` interface must be defined exposing `IReadOnlyList<IComponent> Components { get; }` with deterministic iteration order.
4. Component query extension methods must be provided on `IComponentHolder`:
   - `bool TryGetComponent<T>(out T? component)` returning true and setting `component` when a single match is found, false otherwise.
   - `GetComponents<T>` returning `IReadOnlyList<T>` for multiple component retrieval.
   - `RequireComponent<T>` returning `T` with fail-fast semantics.
5. `RequireComponent<T>` must throw `InvalidOperationException` when zero or more than one match is found, including meaningful holder information (implementation type and, when holder is a Godot Node, node name/path) and requested type in the message.
6. `GetComponents<T>` must return components in deterministic holder-defined order.
7. Components may implement multiple capability interfaces.
8. Capability trait interfaces may use C# default interface members where typed consumption is intended.
9. Component capability interfaces must name behaviour (e.g., `ILocomotion`, `ISpeechGenerator`).
10. Holder trait interfaces must use adjective/noun style naming (e.g., `ILocomotive`, `ISpeaking`, `IAnimatable`, `ISeeing`) unless `IHasX` is clearer.
11. The MVP implementation must use explicit/cached holder collections, not implicit recursive discovery.
12. Extension methods must use the `extension` block syntax consistent with `@game/src/Common/NodeExtensions.cs`.
13. Extension methods must use Require-style fail-fast error messages.

## In Scope

- Core interfaces: `IComponent`, `IComponentHolder`.
- Component query extension methods: `TryGetComponent<T>`, `GetComponents<T>`, `RequireComponent<T>`.
- Fail-fast semantics with detailed error messages.
- Support for both Node-based and plain object holders.
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
- Component serialization or persistence.
- Dynamic component composition (adding/removing at runtime beyond MVP).
- Component event bus or observer patterns.

## Contract

### Core Interfaces

```csharp
namespace AlleyCat.Component;

public interface IComponent { }

public interface IComponentHolder
{
    IReadOnlyList<IComponent> Components { get; }
}
```

### Extension Methods

Extension methods must be defined on `IComponentHolder` using the `extension` block syntax:

```csharp
public static class ComponentHolderExtensions
{
    extension(IComponentHolder holder)
    {
        public bool TryGetComponent<T>(out T? component)
            where T : class, IComponent
        {
            // Returns true and sets component when single match found
            // Returns false and sets component to null when no match
        }

        public IReadOnlyList<T> GetComponents<T>()
            where T : class, IComponent
        {
            // Returns all matching components in deterministic order
        }

        public T RequireComponent<T>()
            where T : class, IComponent
        {
            // Returns single matching component or throws InvalidOperationException
            // Throws when zero OR more than one match found
            // Message includes holder information (type, node name/path) and requested type
        }
    }
}
```

### Naming Conventions

| Interface Type | Example | Rationale |
|----------------|---------|------------|
| Capability (component) | `ILocomotion`, `ISpeechGenerator` | Names the behaviour the component provides |
| Trait (holder) | `ILocomotive`, `ISpeaking`, `IAnimatable`, `ISeeing` | Names what the holder can do |
| Exception case | `IHasInventory` | Used when adjective/noun style is unclear |

### Error Message Format

`RequireComponent<T>` must throw with messages that include meaningful holder identification derived from the holder's implementation type and, when the holder is a Godot Node, the node name and path:

```csharp
throw new InvalidOperationException(
    $"Required component of type {typeof(T).Name} not found on {holderType.Name}. " +
    $"Expected exactly 1, found {count}.");
```

Error messages should include:
- The holder's implementation type name
- When holder is a Godot Node: `node.Name` and `node.GetPath()`
- The requested component type
- The count of matches found

### Deterministic Order

`GetComponents<T>` must return components in the order they appear in `IComponentHolder.Components`, which must maintain deterministic insertion order.

## Acceptance Criteria

1. The spec defines explicit user requirements (developer experience, discoverability, error clarity) distinct from technical requirements (namespace, interface definitions, method signatures).
2. The spec defines both capability interface naming (behaviour-focused) and trait interface naming (holder capability) conventions.
3. The spec includes fail-fast semantics with detailed error messages including holder identity and type information.
4. The spec states that traits are capability interfaces, not true C# mixins that inject concrete members.
5. The spec specifies explicit/cached holder collections for MVP, not implicit recursive discovery.
6. The spec does not exclude mandatory implementation requirements through `Out Of Scope` - lifecycle management, dependency injection, and dynamic composition are deferred but core resolution API is specified.
7. Acceptance criteria verify both user requirement (deterministic queries, clear errors, flexible composition) and technical requirement (interface contracts, method signatures, naming conventions) layers.
8. The spec references `@game/src/Common/NodeExtensions.cs` for extension method style consistency.
9. The spec includes CORE-003 in specs/index.md under Core Systems.

## References

### Implementation

- `@game/src/Component/` - New namespace directory for component system types
- `@game/src/Common/NodeExtensions.cs` - Reference for extension method style

### Related Specs

- [CORE-001: Global Scene](../001-global-scene/index.md)
- [CORE-002: Configuration API](../002-configuration-api/index.md)
- [CTRL-001: Locomotion](../characters/ctrl/001-locomotion/index.md) - Example capability interface usage