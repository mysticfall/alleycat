---
id: CTX-001
title: Contextual Information API
---

# Contextual Information API

## Requirement

Game systems need a shared, non-AI-specific API for retrieving contextual information from runtime subjects when that
information is available.

## Goal

Define the first top-level `AlleyCat.Context` contract for contextual subjects and context sources to provide neutral
key/value contextual data, separate from scene membership, character-specific APIs, concrete source implementations,
AI systems, prompt systems, templating systems, and presentation systems.

## User Requirements

1. Game systems can request contextual data from a subject through one shared API.
2. Contextual data is returned as neutral key/value entries that are independent of any consumer API.
3. Context retrieval may consider the current scene and an observing character when that matters to the subject.
4. Player-visible behaviour can use available context without exposing source wiring, Godot export details, or data
   assembly details.
5. Names, aliases, and display labels are contextual data when sources provide them, not fixed character properties.

## Technical Requirements

1. Contextual information contracts live under the top-level `AlleyCat.Context` namespace.
2. `IContextual` is the public subject trait. Callers ask a subject for its own context and do not pass a separate
   subject argument.
3. The active context result contract is `IReadOnlyDictionary<string, object?>`.
4. Context result dictionaries expose stable string keys and nullable object values without depending on AI, prompt,
   templating, or presentation APIs.
5. Context requests accept the current `AlleyCat.Scene.ISceneContext` and an optional observing `ICharacter`.
6. `AlleyCat.Scene` owns current scene membership through SCN-001. CTX-001 must not redefine membership, actor
   discovery, or scene snapshot semantics.
7. Source aggregation is internal to contextual subjects or owning systems, not a public requester responsibility.
8. `IContextSource` is the non-generic source contract for Godot-export-friendly and heterogeneous source aggregation.
9. `IContextSource<TContextual> : IContextSource` is the typed source contract for reusable implementations that require
   a specific contextual subject type.
10. Typed sources bridge non-generic calls by checking the supplied contextual subject before delegation to the typed
   implementation path.
11. `ContextSource` is a neutral abstract Godot resource base under `AlleyCat.Context` and is the exported property type
    for Godot-authored source collections.
12. `AlleyCat.Context` must not contain character-specific source APIs or character-specific source resource bases.
13. Character source wiring, where specified by character-owned specs, uses one source collection rather than separate
    authored and runtime collections.
14. `ICharacter` extends `IContextual` for the first character-focused slice.
15. `IEntity` is not required to extend `IContextual` in the first slice.
16. Do not introduce `ContextData`, titled-fragment result objects, `ContextRequest`, `ContextRequestKind`, typed
    request filters, or a detailed item taxonomy.
17. Do not require any concrete source implementation, static identity provider, authored content, or fixture data in
    the initial CTX-001 implementation.
18. Names, aliases, and display labels must not be added as fixed properties on `ICharacter` for this slice.

## In Scope

- Top-level `AlleyCat.Context` API contracts for contextual subjects, context sources, and key/value context data.
- `IReadOnlyDictionary<string, object?>` as the active result contract for returned context data.
- Dual non-generic and generic source contracts for heterogeneous aggregation and typed reuse.
- Neutral `ContextSource` resource base as the exported Godot type for authored source collections.
- Integration boundary with `AlleyCat.Scene.ISceneContext` from SCN-001.
- `ICharacter : IContextual` for the first character-focused slice.
- Optional observer-relative context via `ICharacter? observer`.
- Internal source aggregation by subjects or owning systems.
- Names, aliases, and display labels as possible context entries when future sources provide them.

## Out Of Scope

- Concrete context source implementations, including static identity sources.
- Character-specific source base classes or APIs under `AlleyCat.Context`.
- Final authored context content, fixture data, character biographies, names, aliases, or display labels.
- Consumer-specific placement, final serialisation format, renderer ownership, and consumer content structure.
- Direct dependencies from `AlleyCat.Context` to AI retrieval, prompt, or templating APIs.
- Budgeting, ranking, summarisation, omission policy, diagnostics, and evaluation metadata.
- AI retrieval, memory, perception, lore, relationship, inventory, planner, or other backend architectures.
- Non-character contextual subjects such as items, scenes, memories, or lore records.
- Requiring `IEntity : IContextual`.
- Adding `ContextData`, titled-fragment result objects, `ContextRequest`, `ContextRequestKind`, typed request filters,
  or detailed context item taxonomy.
- Redefining SCN-001 scene membership, `Actors` group discovery, or scene-context provider access.

## Acceptance Criteria

### User Requirements

1. A requester can ask a contextual subject for key/value context data through the shared API.
2. Context retrieval can use the current SCN-001 scene context and optional observer when provided.
3. Names, aliases, and display labels are representable as dictionary entries from future sources.
4. No player-facing behaviour exposes source aggregation, Godot export details, or data assembly details.

### Technical Requirements

1. `IContextual`, `IContextSource`, `IContextSource<TContextual>`, and `ContextSource` exist under `AlleyCat.Context`.
2. Public context calls are made on the subject itself and do not accept a separate subject parameter.
3. Active context calls and sources return `IReadOnlyDictionary<string, object?>`, not `ContextData` or titled-fragment
   result objects.
4. Returned context dictionaries expose stable string keys and nullable object values without requiring any AI, prompt,
   templating, or presentation API dependency.
5. Context APIs accept `ISceneContext` from SCN-001 and do not duplicate scene membership or actor discovery contracts.
6. Non-generic `IContextSource` supports Godot-export-friendly and heterogeneous source aggregation.
7. `IContextSource<TContextual>` extends the non-generic source contract and delegates non-generic calls only after a
   successful contextual-subject type check.
8. `ContextSource` is the neutral abstract exported resource base for Godot-authored source collections.
9. No character-specific source API under `AlleyCat.Context` or separate authored source collection is required.
10. `ICharacter` extends `IContextual`, while `IEntity` does not need to extend `IContextual` for CTX-001.
11. No `ContextRequest`, `ContextRequestKind`, concrete source implementation, fixed character label property, final
    renderer, AI retrieval backend, memory backend, lore backend, or perception backend is required by this slice.

## References

- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- `game/src/Context/`
- `game/src/Character/Character.cs`
