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
3. Context retrieval may consider the current scene and an optional identifiable observer when that matters to a
   source.
4. Player-visible behaviour can use available context without exposing source wiring, Godot export details, or data
   assembly details.
5. Names, aliases, and display labels are contextual data when sources provide them, not fixed character properties.
6. Character identity shown in generated conversation context must use the canonical authored character `FullId`.

## Technical Requirements

1. Contextual information contracts live under the top-level `AlleyCat.Context` namespace.
2. `IContextual` is the public completed-context composer trait. Callers ask a subject for its own completed context
   and do not pass a separate subject argument.
3. The active context result contract is `IReadOnlyDictionary<string, object?>`.
4. Context result dictionaries expose stable string keys and nullable object values without depending on AI, prompt,
   templating, or presentation APIs. Producers must treat returned dictionaries and nested values as immutable after
   publication.
5. General context signatures accept the current `AlleyCat.Scene.ISceneContext`. `IContextual` composes completed
   context only; it is never an `IContextSource` or a visual-cue template input.
6. `AlleyCat.Scene` owns current scene membership through SCN-001. CTX-001 must not redefine membership, actor
   discovery, or scene snapshot semantics.
7. Source aggregation is internal to contextual subjects or owning systems, not a public requester responsibility.
8. `IContextSource` is the non-generic source contract for Godot-export-friendly and heterogeneous source aggregation.
   Its subject and optional observer inputs are `IIdentifiable`.
9. `IContextSource<TSubject> : IContextSource` is the typed source contract for reusable implementations that require a
   direct, narrow subject capability. `TSubject` is an `IIdentifiable` capability, not `IContextual` by default.
10. Typed sources bridge non-generic calls by checking the supplied identifiable subject before delegation to the typed
    implementation path.
11. `ContextSource` is a neutral abstract Godot resource base under `AlleyCat.Context` and is the exported property type
    for Godot-authored source collections.
12. `AlleyCat.Context` must not depend on `AlleyCat.Body.Eyes` or `AlleyCat.Character`, and must not contain
    character-specific source APIs or character-specific source resource bases.
13. A contextual composer owns source aggregation. Each source fully owns its local template root and uses only its
    supplied identifiable inputs and direct, narrow capabilities; sources do not call `GetContext`.
14. Character source wiring, where specified by character-owned specs, uses one source collection rather than separate
    authored and runtime collections.
15. Under CHAR-002, `ICharacter` extends `IContextual` for the first character-focused slice without creating a
    dependency from `AlleyCat.Context` to `AlleyCat.Character`.
16. CORE-009 `IIdentifiable` is not required to extend `IContextual` in the first slice.
17. Do not introduce `ContextData`, titled-fragment result objects, `ContextRequest`, `ContextRequestKind`, typed
    request filters, or a detailed item taxonomy.
18. `CharacterCardContextSource` is the concrete narrow character source for this slice and returns exactly
    `{ FullId: subject.FullId }`.
19. Names, aliases, and display labels must not be added as fixed properties on `ICharacter` for this slice.
20. `CreateRenderContext` is AgenticMind's foreground-only aggregation operation. It must create AgenticMind's own
    complete top-level read-only render dictionary containing `character` for the owner, deterministically ordered
    `characters` keyed by each exact `Character.FullId`, the read-only `observations` snapshot, and all
    authored ContextWorker projections.
21. `CreateRenderContext` must call every subject with `observer` set to the owning character.
22. The owner must appear in both `character` and `characters[owner.FullId]`, and both entries must reference the exact
    same context dictionary instance.
23. The `characters` dictionary must be inserted in ordinal order by exact `Character.FullId`.
24. `CreateRenderContext` must fail for invalid character identity, duplicate exact scene character `FullId` values, or
    an owner absent from the scene context.
25. When supplied, an `IIdentifiable` observer is passed unchanged to each source; when omitted, sources receive `null`.
    A source may require or pattern-match a narrower direct capability.
26. `IContextual` neither defines visual-observer capability nor participates in visual-cue description. BODY-004 owns
    visual inspection through `IEyesHolder` and `IVisualSubject`.
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
- Optional observer-relative source context via `IIdentifiable? observer`.
- Completed-context composition and internal source aggregation by contextual subjects or owning systems.
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
6. Source context remains observer-relative when an identifiable observer is supplied and remains available without one.

### Technical Requirements

1. `IContextual`, `IContextSource`, `IContextSource<TSubject>`, and `ContextSource` exist under `AlleyCat.Context`.
2. Public completed-context calls are made on the composer itself and do not accept a separate subject parameter.
3. Active context calls and sources return `IReadOnlyDictionary<string, object?>`, not `ContextData` or titled-fragment
   result objects.
4. Returned context dictionaries expose stable string keys and nullable object values without requiring any AI, prompt,
   templating, or presentation API dependency.
5. Context APIs accept `ISceneContext` from SCN-001 and do not duplicate scene membership or actor discovery contracts.
6. Non-generic `IContextSource` supports Godot-export-friendly and heterogeneous source aggregation.
7. `IContextSource` accepts identifiable subject and optional observer inputs. `IContextSource<TSubject>` extends it
   and delegates non-generic calls only after a successful identifiable-subject type check.
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
16. `IContextual` only composes completed context. It is neither an `IContextSource` nor a visual-cue input.
17. Sources accept optional `IIdentifiable` observers and preserve supplied-observer and omitted-observer semantics.
    Sources use direct, narrow capabilities when needed.
18. Each composer owns source aggregation, and each source owns its local template root. `AlleyCat.Context` has no
    dependency on `AlleyCat.Body.Eyes` or `AlleyCat.Character`.
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
