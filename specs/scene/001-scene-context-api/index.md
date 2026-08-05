---
id: SCN-001
title: Scene Context API
---

# Scene Context API

## Requirement

Define an independent scene-context API under `AlleyCat.Scene` that exposes current-scene character membership,
identifiable lookup, and content context without coupling the contract to AI prompt requests or static global accessors.

## Goal

Provide a small, stable runtime snapshot of current characters and active content identity so systems can inspect
membership, resolve canonical object identities, and access the content root through dependency injection while
character and Godot node objects remain live.

## User Requirements

1. Game systems can identify the current characters in the loaded scene through one shared runtime API.
2. Contributors can author character discovery by placing character nodes in one explicit Godot group.
3. Incorrect actor-group membership fails clearly during development instead of silently corrupting context.
4. Items and future non-human actor categories are not misclassified as current humanoid characters.
5. Game systems can read the active content identity and root from scene context without resolving CORE services again.
6. Scene authors receive a clear failure when character identity would make conversation context ambiguous.
7. Contributors can author visual subjects in one explicit group without making those subjects actors.
8. Game systems can find or require a current-scene object by its canonical `FullId` without knowing its scene group.

## Technical Requirements

1. Scene-context implementation concepts live under the `AlleyCat.Scene` namespace, not under `AlleyCat.AI`.
2. `ISceneContextProvider.GetCurrent()` returns an `ISceneContext` membership snapshot.
3. `ISceneContext` contains the current characters collection and the current content context sourced from CORE-008.
4. `ISceneContext.Characters` is exposed as an `IReadOnlyCollection<ICharacter>` so callers do not infer ordering.
5. Snapshot collection membership and identifiable lookup membership are fixed when the context is built, but referenced
   `ICharacter`, `IIdentifiable`, and Godot node objects remain live mutable objects.
6. Consumers resolve the provider only through dependency injection, for example
   `Game.Instance.GetService<ISceneContextProvider>()` or constructor/property injection.
7. `Game.SceneContextProvider`, static convenience accessors, and other non-DI scene-context entry points are forbidden.
8. Initial discovery scans `SceneTree.GetNodesInGroup("Actors")`.
9. `Actors` is a strict character-discovery group: every node in the group must implement `ICharacter`.
10. Discovering a non-`ICharacter` node in `Actors` is an authoring error and must throw immediately.
11. Items must never be treated as actors by scene context.
12. Non-human actors are future work and require revisiting or refactoring `ICharacter` before inclusion.
13. Scene-context construction must reject empty character IDs and exact duplicate character `FullId` values. Runtime
    character identities remain exact and case-sensitive; case-only differences are not duplicates at this boundary.
14. `ISceneContext` must expose the current content context for convenience, preserving CORE-008 as the source of truth
    for content identity and root resolution.
15. SCN-001 must not define lore, AI, prompt, retrieval, or content-domain path semantics on top of the CORE content
    context.
16. Every character role template must join `Actors` and `VisualSubjects` at the lowest shared male/female character
    base. Higher role or concrete character scenes must not add redundant compensating membership.
17. `VisualSubjects` is the group for nodes authored as `IVisualSubject` instances. BODY-004 owns direct group
    querying, member validation, and scan authoring-failure behaviour.
18. `VisualSubjects` membership is independent of `Actors`. A scene node may be a visual subject without being a
    current humanoid character, and `Actors` retains its `ICharacter`-only contract.
19. `ISceneContext` must expose `Find(FullId)` and `Resolve(FullId)` operations returning current-scene
    `IIdentifiable` objects. `FullId` is the canonical CORE-009 cross-object reference; callers must not use local
    `Id` for either operation.
20. Lookup matches the supplied canonical `FullId` using exact ordinal comparison. `Find` returns `null` when no
    current-scene match exists. `Resolve` throws when no current-scene match exists.
21. Scene-context implementation must map `IIdentifiable.Type` internally to its discovery group. The initial mapping
    contains only `char` to `Actors`; it must not add initial scene membership for locations, items, visual subjects, or
    other identifiable types.
22. A type without a scene-group mapping has no current-scene match: `Find` returns `null` and `Resolve` throws. The
    implementation must not scan arbitrary groups or infer membership for an unmapped type.

## In Scope

- `ISceneContextProvider` and `ISceneContext` as independent `AlleyCat.Scene` contracts.
- Current humanoid character membership via `ICharacter`.
- Current-scene `IIdentifiable` lookup by canonical `FullId`.
- DI-only provider access through CORE-004 service resolution.
- Type-to-group discovery mapping, initially `char` to the Godot `Actors` group.
- Immediate validation failure for non-`ICharacter` nodes in `Actors`.
- Immediate validation failure for empty character IDs or exact duplicate character `FullId` values.
- Shared-base `Actors` membership for all character role templates.
- Shared-base `VisualSubjects` membership for all character role templates, as delegated to CHAR-002 and BODY-004.
- Membership-snapshot semantics with live referenced objects.
- Convenience exposure of the CORE current-content context.
- `VisualSubjects` group-membership semantics for BODY-004 visual scans.

## Out Of Scope

- AI-specific request semantics, prompt placement, requesting character identity, and interaction-target selection.
- Contextual information retrieval, memory, perception, relationship, inventory, lore, or RAG provider contracts.
- Lore-specific paths, AI prompt construction, prompt rendering, or retrieval semantics.
- Static convenience accessors or `Game` properties for scene-context access.
- Treating items as actors.
- Non-human actor support before the `ICharacter` model is revisited.
- Scene-group mappings or membership support for identifiable types other than `char`.
- Lore-normalised identity collision validation, which belongs to AI-004 character-lore construction.
- Visibility policy, bounds sampling, field-of-view calculations, and raycast behaviour, which belong to BODY-004.

## Acceptance Criteria

### User Requirements

1. A consumer can retrieve the current scene's characters from one scene-context API without knowing group-scan details.
2. A character scene can opt into discovery by placing its root `ICharacter` node in the `Actors` group.
3. A node in `Actors` that does not implement `ICharacter` produces an immediate authoring error.
4. Items are absent from scene-context character membership, even if they are interactable scene objects.
5. A consumer can read the active content id and root from the scene context for content-relative loading.
6. Empty character IDs or exact duplicate character `FullId` values fail scene-context construction clearly.
7. A contributor can place a visual subject in `VisualSubjects` without making it an actor.
8. A consumer can find a current character by its canonical `FullId`, or require it and receive a clear failure when it
   is absent.
9. A lookup for a location, item, or other type not mapped into scene context does not make that object a member.

### Technical Requirements

1. Scene-context interfaces and implementations are named under `AlleyCat.Scene`, not `AlleyCat.AI`.
2. `ISceneContextProvider.GetCurrent()` returns `ISceneContext`.
3. `ISceneContext.Characters` is an `IReadOnlyCollection<ICharacter>` and no ordering contract is documented.
4. `ISceneContext` exposes the CORE current-content context without re-declaring content resolution rules.
5. Provider consumers use `Game.Instance.GetService<ISceneContextProvider>()` or injected equivalents only.
6. No `Game.SceneContextProvider` property or static scene-context convenience accessor exists.
7. Discovery uses `SceneTree.GetNodesInGroup("Actors")` and rejects every non-`ICharacter` group member immediately.
8. Context instances preserve membership from creation time while returning live `ICharacter` object references.
9. Scene-context construction rejects empty character IDs and exact duplicate character `FullId` values without
   normalising case.
10. Scene context contains no lore-specific path, AI prompt, or retrieval contract.
11. Male and female character bases add their character root to `Actors` and `VisualSubjects` at the lowest shared
    level, with no redundant higher-scene compensation. BODY-004 and CHAR-002 normatively define visual-subject and
    cue contracts.
12. `VisualSubjects` is reserved for nodes authored as `IVisualSubject` instances; BODY-004 owns direct querying,
    member validation, and invalid-member failure for visual scans.
13. `VisualSubjects` does not relax, replace, or imply `Actors` membership.
14. `ISceneContext.Find(FullId)` and `ISceneContext.Resolve(FullId)` accept canonical CORE-009 `FullId` values and
    return `IIdentifiable` matches using exact ordinal comparison.
15. `Find` returns `null` for an absent or unmapped identity. `Resolve` throws for the same no-match condition.
16. The internal type-to-group mapping initially contains only `char` to `Actors`. It neither scans arbitrary groups
    nor includes locations, items, visual subjects, or other identifiable types in current-scene membership.
17. Lookup membership is captured with the context snapshot, while returned object references remain live.

## References

- [CORE-004: Global Service Resolution](../../core/004-global-service-resolution/index.md)
- [CORE-008: Content Pack Resolution](../../core/008-content-pack-resolution/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [BODY-004: Eyes](../../body/004-eyes/index.md)
