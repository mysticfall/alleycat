---
id: SCN-001
title: Scene Context API
---

# Scene Context API

## Requirement

Define an independent scene-context API under `AlleyCat.Scene` that exposes the current scene's character membership to
game systems without coupling the contract to AI prompt requests or static global accessors.

## Goal

Provide a small, stable runtime snapshot of current characters so AI and non-AI consumers can inspect scene membership
through dependency injection while character and Godot node objects remain live.

## User Requirements

1. Game systems can identify the current characters in the loaded scene through one shared runtime API.
2. Contributors can author character discovery by placing character nodes in one explicit Godot group.
3. Incorrect actor-group membership fails clearly during development instead of silently corrupting context.
4. Items and future non-human actor categories are not misclassified as current humanoid characters.

## Technical Requirements

1. Scene-context implementation concepts live under the `AlleyCat.Scene` namespace, not under `AlleyCat.AI`.
2. `ISceneContextProvider.GetCurrent()` returns an `ISceneContext` membership snapshot.
3. `ISceneContext` contains only the current characters collection for the first API.
4. `ISceneContext.Characters` is exposed as an `IReadOnlyCollection<ICharacter>` so callers do not infer ordering.
5. Snapshot collection membership is fixed when the context is built, but referenced `ICharacter` and Godot node objects
   remain live mutable objects.
6. Consumers resolve the provider only through dependency injection, for example
   `Game.Instance.GetService<ISceneContextProvider>()` or constructor/property injection.
7. `Game.SceneContextProvider`, static convenience accessors, and other non-DI scene-context entry points are forbidden.
8. Initial discovery scans `SceneTree.GetNodesInGroup("Actors")`.
9. `Actors` is a strict character-discovery group: every node in the group must implement `ICharacter`.
10. Discovering a non-`ICharacter` node in `Actors` is an authoring error and must throw immediately.
11. Items must never be treated as actors by scene context.
12. Non-human actors are future work and require revisiting or refactoring `ICharacter` before inclusion.
13. `IEntity.Id` must not be empty, but ID validation remains a generic entity or character authoring responsibility;
    SCN-001 does not add scene-context-specific ID validation.

## In Scope

- `ISceneContextProvider` and `ISceneContext` as independent `AlleyCat.Scene` contracts.
- Current humanoid character membership via `ICharacter`.
- DI-only provider access through CORE-004 service resolution.
- Godot `Actors` group scanning as the initial discovery mechanism.
- Immediate validation failure for non-`ICharacter` nodes in `Actors`.
- Membership-snapshot semantics with live referenced objects.

## Out Of Scope

- AI-specific request semantics, prompt placement, requesting character identity, and interaction-target selection.
- Contextual information retrieval, memory, perception, relationship, inventory, lore, or RAG provider contracts.
- Static convenience accessors or `Game` properties for scene-context access.
- Treating items as actors.
- Non-human actor support before the `ICharacter` model is revisited.
- Scene-context-specific validation for `IEntity.Id` values.

## Acceptance Criteria

### User Requirements

1. A consumer can retrieve the current scene's characters from one scene-context API without knowing group-scan details.
2. A character scene can opt into discovery by placing its root `ICharacter` node in the `Actors` group.
3. A node in `Actors` that does not implement `ICharacter` produces an immediate authoring error.
4. Items are absent from scene-context character membership, even if they are interactable scene objects.

### Technical Requirements

1. Scene-context interfaces and implementations are named under `AlleyCat.Scene`, not `AlleyCat.AI`.
2. `ISceneContextProvider.GetCurrent()` returns `ISceneContext`.
3. `ISceneContext.Characters` is an `IReadOnlyCollection<ICharacter>` and no ordering contract is documented.
4. Provider consumers use `Game.Instance.GetService<ISceneContextProvider>()` or injected equivalents only.
5. No `Game.SceneContextProvider` property or static scene-context convenience accessor exists.
6. Discovery uses `SceneTree.GetNodesInGroup("Actors")` and rejects every non-`ICharacter` group member immediately.
7. Context instances preserve membership from creation time while returning live `ICharacter` object references.
8. ID validation for `IEntity.Id` remains delegated to generic entity or character authoring validation.

## References

- [CORE-004: Global Service Resolution](../../core/004-global-service-resolution/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
