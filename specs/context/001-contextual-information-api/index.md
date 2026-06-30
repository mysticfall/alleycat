---
id: CTX-001
title: Contextual Information API
---

# Contextual Information API

## Requirement

Game systems need a shared, non-AI-specific API for retrieving contextual information from runtime subjects when that
information is available.

## Goal

Define the first top-level `AlleyCat.Context` contract for contextual subjects and context sources to provide titled
context fragments, separate from scene membership, character-specific APIs, concrete source implementations, and
presentation systems.

## User Requirements

1. Game systems can request contextual descriptions from a subject through one shared API.
2. Contextual descriptions are returned as titled fragments so requesters can handle them consistently.
3. Context retrieval may consider the current scene and an observing character when that matters to the subject.
4. Player-visible behaviour can use available context without exposing source wiring, Godot export details, or data
   assembly details.
5. Names, aliases, and display labels are contextual data when sources provide them, not fixed character properties.

## Technical Requirements

1. Contextual information contracts live under the top-level `AlleyCat.Context` namespace.
2. `IContextual` is the public subject trait. Callers ask a subject for its own context and do not pass a separate
   subject argument.
3. `ContextData` is the shared titled-fragment result type. It carries a title and content only in the first slice.
4. Context requests accept the current `AlleyCat.Scene.ISceneContext` and an optional observing `ICharacter`.
5. `AlleyCat.Scene` owns current scene membership through SCN-001. CTX-001 must not redefine membership, actor
   discovery, or scene snapshot semantics.
6. Source aggregation is internal to contextual subjects or owning systems, not a public requester responsibility.
7. `IContextSource` is the non-generic source contract for Godot-export-friendly and heterogeneous source aggregation.
8. `IContextSource<TContextual> : IContextSource` is the typed source contract for reusable implementations that require
   a specific contextual subject type.
9. Typed sources bridge non-generic calls by checking the supplied contextual subject before delegation to the typed
   implementation path.
10. `ContextSource` is a neutral abstract Godot resource base under `AlleyCat.Context` and is the exported property type
    for Godot-authored source collections.
11. `AlleyCat.Context` must not contain character-specific source APIs or character-specific source resource bases.
12. Character source wiring, where specified by character-owned specs, uses one source collection rather than separate
    authored and runtime collections.
13. `ICharacter` extends `IContextual` for the first character-focused slice.
14. `IEntity` is not required to extend `IContextual` in the first slice.
15. Do not introduce `ContextRequest`, `ContextRequestKind`, typed request filters, or a detailed item taxonomy.
16. Do not require any concrete source implementation, static identity provider, authored content, or fixture data in
    the initial CTX-001 implementation.
17. Names, aliases, and display labels must not be added as fixed properties on `ICharacter` for this slice.

## In Scope

- Top-level `AlleyCat.Context` API contracts for contextual subjects, context sources, and context fragments.
- Dual non-generic and generic source contracts for heterogeneous aggregation and typed reuse.
- Neutral `ContextSource` resource base as the exported Godot type for authored source collections.
- Integration boundary with `AlleyCat.Scene.ISceneContext` from SCN-001.
- `ICharacter : IContextual` for the first character-focused slice.
- Optional observer-relative context via `ICharacter? observer`.
- Internal source aggregation by subjects or owning systems.
- Names, aliases, and display labels as possible contextual fragments when future sources provide them.

## Out Of Scope

- Concrete context source implementations, including static identity sources.
- Character-specific source base classes or APIs under `AlleyCat.Context`.
- Final authored context content, fixture data, character biographies, names, aliases, or display labels.
- Prompt placement, final serialisation format, renderer ownership, and prompt template structure.
- Budgeting, ranking, summarisation, omission policy, diagnostics, and evaluation metadata.
- AI retrieval, memory, perception, lore, relationship, inventory, planner, or other backend architectures.
- Non-character contextual subjects such as items, scenes, memories, or lore records.
- Requiring `IEntity : IContextual`.
- Adding `ContextRequest`, `ContextRequestKind`, typed request filters, or detailed context item taxonomy.
- Redefining SCN-001 scene membership, `Actors` group discovery, or scene-context provider access.

## Acceptance Criteria

### User Requirements

1. A requester can ask a contextual subject for titled context fragments through the shared API.
2. Context retrieval can use the current SCN-001 scene context and optional observer when provided.
3. Names, aliases, and display labels are representable as `ContextData` from future sources.
4. No player-facing behaviour exposes source aggregation, Godot export details, or data assembly details.

### Technical Requirements

1. `IContextual`, `IContextSource`, `IContextSource<TContextual>`, `ContextSource`, and `ContextData` exist under
   `AlleyCat.Context`.
2. Public context calls are made on the subject itself and do not accept a separate subject parameter.
3. Context APIs accept `ISceneContext` from SCN-001 and do not duplicate scene membership or actor discovery contracts.
4. Non-generic `IContextSource` supports Godot-export-friendly and heterogeneous source aggregation.
5. `IContextSource<TContextual>` extends the non-generic source contract and delegates non-generic calls only after a
   successful contextual-subject type check.
6. `ContextSource` is the neutral abstract exported resource base for Godot-authored source collections.
7. No character-specific source API under `AlleyCat.Context` or separate authored source collection is required.
8. `ICharacter` extends `IContextual`, while `IEntity` does not need to extend `IContextual` for CTX-001.
9. No `ContextRequest`, `ContextRequestKind`, concrete source implementation, fixed character label property, final
   renderer, AI retrieval backend, memory backend, lore backend, or perception backend is required by this slice.

## References

- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
