---
id: CTX-001
title: Contextual Information API
---

# Contextual Information API

## Requirement

Game systems need a shared, non-AI-specific API for retrieving contextual information from runtime subjects when that
information is available.

## Goal

Define the top-level `AlleyCat.Context` contract for neutral key/value context, separate from scene membership and
consumer APIs, while providing the narrow character-card source and scene-character context required by this slice.

## User Requirements

1. Game systems can request contextual data from a subject through one shared API.
2. Contextual data is returned as neutral key/value entries that are independent of any consumer API.
3. Context retrieval may consider the current scene and an optional contextual observer when that matters to the
   subject.
4. Player-visible behaviour can use available context without exposing source wiring, Godot export details, or data
   assembly details.
5. Names, aliases, and display labels are contextual data when sources provide them, not fixed character properties.
6. Character identity shown in generated conversation context must use the canonical authored character `FullId`.

## Technical Requirements

1. Contextual information contracts live under the top-level `AlleyCat.Context` namespace.
2. `IContextual` is the public subject trait. Callers ask a subject for its own context and do not pass a separate
   subject argument.
3. The active context result contract is `IReadOnlyDictionary<string, object?>`.
4. Context result dictionaries expose stable string keys and nullable object values without depending on AI, prompt,
   templating, or presentation APIs. Producers must treat returned dictionaries and nested values as immutable after
   publication.
5. General context signatures on `IContextual`, `IContextSource`, and `IContextSource<TContextual>` accept the current
   `AlleyCat.Scene.ISceneContext` and an optional `IContextual? observer`.
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
12. `AlleyCat.Context` must not depend on `AlleyCat.Body.Eyes` or `AlleyCat.Character`, and must not contain
    character-specific source APIs or character-specific source resource bases.
13. Character source wiring, where specified by character-owned specs, uses one source collection rather than separate
    authored and runtime collections.
14. Under CHAR-002, `ICharacter` extends `IContextual` for the first character-focused slice without creating a
    dependency from `AlleyCat.Context` to `AlleyCat.Character`.
15. CORE-009 `IIdentifiable` is not required to extend `IContextual` in the first slice.
16. Do not introduce `ContextData`, titled-fragment result objects, `ContextRequest`, `ContextRequestKind`, typed
    request filters, or a detailed item taxonomy.
17. `CharacterCardContextSource` is the concrete narrow character source for this slice and returns exactly
    `{ FullId: subject.FullId }`.
18. Names, aliases, and display labels must not be added as fixed properties on `ICharacter` for this slice.
19. `CreateRenderContext` is AgenticMind's foreground-only aggregation operation. It must create AgenticMind's own
    complete top-level read-only render dictionary containing `character` for the owner, deterministically ordered
    `characters` keyed by each exact `Character.FullId`, the read-only `observations` snapshot, and all
    authored ContextWorker projections.
20. `CreateRenderContext` must call every subject with `observer` set to the owning character.
21. The owner must appear in both `character` and `characters[owner.FullId]`, and both entries must reference the exact
    same context dictionary instance.
22. The `characters` dictionary must be inserted in ordinal order by exact `Character.FullId`.
23. `CreateRenderContext` must fail for invalid character identity, duplicate exact scene character `FullId` values, or
    an owner absent from the scene context.
24. Existing observer-relative semantics remain unchanged: when an observer is supplied, sources receive that same
    observer; when omitted, sources receive `null`.
25. Context sources that need narrower observer capabilities may explicitly pattern-match the supplied `IContextual`;
    the general contracts must not require visual inspection capabilities from every observer.
26. `ICharacter` remains a valid contextual observer through its CHAR-002 aggregation of BODY-004's
    `IVisualObserver : IEyesHolder, IContextual` role.
27. A ContextWorker run under AI-005 returns `IReadOnlyDictionary<string, object?>` directly. No public
    `ContextualSnapshot`, worker-specific `IContextual` wrapper, `ContextWorkerState`, or alternative worker-state
    boundary is introduced.
28. ContextWorker atomically stores and returns the exact `IReadOnlyDictionary<string, object?>` returned by a worker.
    Post-publication mutation of that dictionary or its nested values violates the producer contract, and resulting
    behaviour may be undefined or stale.
29. `IReadOnlyDictionary` alone does not prove deep immutability. Aggregation and publication require no recursive
    defensive copying or freezing, cycle detection, scalar allowlist, reflection observation projection, or rejection
    of live Godot objects. This convention changes no scenario-model, neutral API, observer, scene, or eyes boundary.

## In Scope

- Top-level `AlleyCat.Context` API contracts for contextual subjects, context sources, and key/value context data.
- `IReadOnlyDictionary<string, object?>` as the active result contract for returned context data.
- Dual non-generic and generic source contracts for heterogeneous aggregation and typed reuse.
- Neutral `ContextSource` resource base as the exported Godot type for authored source collections.
- Integration boundary with `AlleyCat.Scene.ISceneContext` from SCN-001.
- `ICharacter : IContextual` for the first character-focused slice and its CORE-009 identity integration.
- Optional observer-relative context via `IContextual? observer`.
- Internal source aggregation by subjects or owning systems.
- `CharacterCardContextSource` as the narrow identity source returning only the subject's canonical `FullId`.
- Deterministic, foreground-only `CreateRenderContext` assembly for prompt rendering and later snapshot publication.
- Names, aliases, and display labels as possible context entries when future sources provide them.
- Convention-based direct ContextWorker dictionary publication without a public wrapper or scenario-model requirement.

## Out Of Scope

- Character-specific source base classes or APIs under `AlleyCat.Context`.
- Final authored context content, fixture data, character biographies, names, aliases, or display labels.
- Consumer-specific placement, final serialisation format, renderer ownership, and consumer content structure.
- Direct dependencies from `AlleyCat.Context` to AI retrieval, prompt, or templating APIs.
- Budgeting, ranking, summarisation, omission policy, context-content diagnostics, and evaluation metadata.
- AI retrieval, memory, perception, lore, relationship, inventory, planner, or other backend architectures.
- Non-character contextual subjects such as items, scenes, memories, or lore records.
- Requiring `IIdentifiable : IContextual`.
- Adding `ContextData`, titled-fragment result objects, `ContextRequest`, `ContextRequestKind`, typed request filters,
  or detailed context item taxonomy.
- Redefining SCN-001 scene membership, `Actors` group discovery, or scene-context provider access.

## Acceptance Criteria

### User Requirements

1. A requester can ask a contextual subject for key/value context data through the shared API.
2. Context retrieval can use the current SCN-001 scene context and optional observer when provided.
3. Names, aliases, and display labels are representable as dictionary entries from future sources.
4. No player-facing behaviour exposes source aggregation, Godot export details, or data assembly details.
5. Generated character context identifies the owner and scene characters by their exact authored `FullId` values.
6. Existing context remains observer-relative when a contextual observer is supplied and remains available without one.

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
10. `ICharacter` extends `IContextual`. CORE-009 `IIdentifiable` does not need to extend `IContextual` for CTX-001.
11. No `ContextRequest`, `ContextRequestKind`, fixed character label property, AI retrieval backend, memory backend,
    lore backend, or perception backend is required by this slice.
12. `CharacterCardContextSource` returns exactly one `FullId` entry whose value is `subject.FullId`.
13. Foreground-only `CreateRenderContext` returns AgenticMind's complete top-level read-only dictionary containing
    `character`, ordinally inserted `characters[exact Character.FullId]`, read-only `observations`, and all authored
    ContextWorker projections, with every subject queried using the owning character as observer.
14. The owner appears in both locations using the same dictionary instance.
15. Invalid identity, exact duplicate scene character `FullId` values, and owner absence fail context assembly clearly.
16. General context contracts use optional `IContextual? observer` parameters rather than narrower visual- or
    character-domain observer parameters.
17. The observer contract preserves existing supplied-observer and omitted-observer semantics across contextual subjects
    and context sources.
18. `AlleyCat.Context` has no dependency on `AlleyCat.Body.Eyes` or `AlleyCat.Character`; sources may explicitly
    pattern-match an observer when they need narrower domain capabilities.
19. A ContextWorker run returns `IReadOnlyDictionary<string, object?>` directly, with no public `ContextualSnapshot`,
    worker-specific `IContextual` wrapper, `ContextWorkerState`, or alternative worker-state boundary.
20. Tests verify a ContextWorker atomically stores and returns the exact dictionary returned by the worker. Producer
    fixtures treat that dictionary and its nested values as immutable after return; contract tests identify later
    mutation as a producer violation with potentially undefined or stale behaviour.
21. The specification and API do not claim `IReadOnlyDictionary` proves deep immutability. Tests require no recursive
    copying or freezing, cycle detection, scalar allowlist, reflection observation projection, or live Godot-object
    rejection at aggregation or publication. The convention changes no scenario-model, scene, observer, or eyes
    contract.

## References

- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [BODY-004: Eyes](../../body/004-eyes/index.md)
- [AI-005: Context Worker](../../ai/005-context-worker/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- `game/src/Context/`
- `game/src/Character/Character.cs`
