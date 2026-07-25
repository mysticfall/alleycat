---
id: AI-005
title: Context Worker
---

# Context Worker

## Requirement

AgenticMind must host editor-authored ContextWorker child nodes that independently maintain contextual projections from
the latest successfully rendered foreground context without delaying foreground turns.

## Goal

Allow NPC context to be projected in the background from a stable foreground-published snapshot while preventing worker
execution from constructing context, refreshing the timeline, or feeding projections directly into other workers.

## User Requirements

1. Content authors can configure each context worker beneath an AgenticMind with one direct trigger child that selects
   when the worker refreshes.
2. NPC foreground turns remain responsive while context workers refresh independently and concurrently.
3. A worker retains its last successful contextual projection when a later refresh fails.
4. Before the first foreground prompt render, worker behaviour may skip, use defaults, or run against empty context
   without affecting foreground behaviour.

## Technical Requirements

1. AgenticMind owns editor-authored `ContextWorker` child nodes. Each worker must have exactly one direct `Node` child
   whose type derives from the abstract `ContextWorkerTrigger`; no other direct trigger child or trigger-discovery route
   is valid.
2. The trigger exclusively decides when to request worker execution. Concrete trigger types support one authored policy:
   - every N settled foreground turns;
   - an interval, with an initial delay defaulting to `0`; or
   - an observation trigger derived from abstract `ObservationContextWorkerTrigger`.
   Each concrete `ObservationContextWorkerTrigger` subtype must implement its own cheap, synchronous, side-effect-free
   observation predicate.
3. AgenticMind publishes general typed C# events after an observation is committed and after a foreground turn is
   genuinely successful. Relevant trigger nodes subscribe directly to the event needed by their policy and unsubscribe
   at the corresponding lifecycle boundary. Events must not represent uncommitted, failed, cancelled, contained, or
   merely started foreground work.
4. AgenticMind must not discover or loop through ContextWorkers to notify their triggers. It may retain its authored
   ContextWorkers solely so foreground `CreateRenderContext` can include their projections deterministically.
5. `ContextWorkerTrigger` publishes a typed `RunRequested` event. Its owning `ContextWorker` subscribes and unsubscribes
   directly. The trigger must not hold a worker reference, and the worker and trigger must not form a mutual reference.
6. Trigger evaluation and worker execution must never block, delay, or change foreground-turn scheduling. Workers run
   independently, and different workers may run in parallel.
7. A worker exclusively executes requested work. It allows one active run, cancels with the Mind lifetime, and
   coalesces requests received while it runs to at most one fresh follow-up run after the active run settles.
8. AgenticMind must start with an empty top-level read-only latest render dictionary. Only foreground prompt execution
   may call `CreateRenderContext` to create AgenticMind's own complete top-level read-only dictionary from current
   character context, deterministic scene-character context, the complete observation timeline, and every authored
   worker projection.
9. The foreground template must render with the dictionary returned by `CreateRenderContext`. Only after rendering
   succeeds may AgenticMind atomically publish that exact dictionary as latest. Context construction or rendering
   failure must retain the previously published snapshot.
10. Each worker must atomically capture the currently published snapshot when its run starts and use only that snapshot
    for the complete run. It must never request `CreateRenderContext`, context-source aggregation, worker-projection
    aggregation, or a timeline refresh. Before the first successful foreground render, the captured snapshot is the
    empty dictionary; each worker implementation may skip, use defaults, or run against it.
11. Worker output must not feed the same worker or another worker directly. A successful worker projection becomes
    available to workers only after a later foreground prompt calls `CreateRenderContext`, renders successfully, and
    publishes the resulting context. `ContextWorkerRunInput` must not exist.
12. Observation timeline snapshot records included by foreground context construction must pass directly to Handlebars.
    Exact `TypeKey` dispatch, record property visibility, and fallback record data must be preserved under AI-003.
13. A worker run returns `IReadOnlyDictionary<string, object?>`; no public `ContextualSnapshot` or worker-specific
    `IContextual` wrapper is introduced. ContextWorker must atomically store and return the exact dictionary returned by
    the worker. The producer must treat that dictionary and its nested values as immutable after return. Mutation after
    publication violates the producer contract, and resulting behaviour may be undefined or stale.
14. `IReadOnlyDictionary` must not be described as proof of deep immutability. Aggregation and publication require no
    recursive defensive copying or freezing, cycle detection, scalar allowlist, reflection observation projection, or
    rejection of live Godot objects. `ContextWorkerState` must not exist. A failed run must emit error diagnostics and
    retain the prior successful projection.
15. An LLM-backed worker captures an immutable `PromptStack` reference for its node lifetime. After AgenticMind
    attachment, it starts one compilation task from that stack and caches the result for its lifetime. It never
    invalidates or recompiles the cache. Each request renders the cached template with the snapshot captured at run
    start. A compilation failure logs once, invokes no provider, and leaves the worker inactive with its prior
    projection as fallback. LLM workers expose no tools or actions. They map their typed schema response into an
    `IReadOnlyDictionary<string, object?>` for direct atomic publication.
16. Worker activity and pending follow-up work must use Mind node-lifetime cancellation. Mind exit must prevent further
    execution, provider invocation, and projection replacement.
17. ContextWorker must emit structured debug diagnostics for run start, request coalescing, successful publication with
    the published key count, and follow-up scheduling. Existing error diagnostics for failed runs and compilation must
    remain available.

## In Scope

- Generic ContextWorker node and direct-trigger ownership, event routing, concurrency, coalescing, projection, and
  lifetime contracts.
- Convention-based ContextWorker dictionary publication under CTX-001.
- Foreground-only `CreateRenderContext`, success-only snapshot publication, and worker snapshot-consumption boundaries.
- LLM-worker prompt-cache and typed response-validation boundaries.
- Required diagnostics and verification of success-only state replacement.

## Out Of Scope

- A scenario model or scenario-specific worker contracts.
- Optional ContextWorker implementations, prompt assets, or scene authoring beyond the required generic and LLM-backed
  contracts.
- Trigger thresholds, intervals, and filter expressions beyond the required default initial delay and filter
  constraints.
- Worker tools, world actions, or foreground-turn control.
- Timeline compaction, persistence, or changes to Mind privacy and foreground tool-only protocol.

## Acceptance Criteria

### User Requirements

1. Authoring tests show that an AgenticMind can contain workers with exactly one direct trigger child and one configured
   trigger policy each.
2. Execution tests show foreground turns continue without waiting for workers and that independent workers can run in
   parallel.
3. Failure tests show a worker retains its last successful projection after a failed refresh.
4. Before the first foreground render, tests show each worker's selected skip, default, or empty-context behaviour is
   contained and does not affect foreground scheduling.

### Technical Requirements

1. Tests verify exactly one direct child derived from abstract `ContextWorkerTrigger`, with concrete turn-count,
   interval, and observation-filter subtypes. The interval has a default zero initial delay. Each concrete subtype
   derived from abstract `ObservationContextWorkerTrigger` implements its observation predicate. Only the trigger
   requests work, and observation predicates are synchronous, side-effect-free, and inexpensive.
2. Tests verify AgenticMind publishes general typed C# events only for committed observations and genuinely successful
   foreground turns. Relevant triggers subscribe and unsubscribe directly, and receive no event for failed, cancelled,
   contained, uncommitted, or merely started work.
3. Tests verify AgenticMind does not loop through ContextWorkers for trigger notification and retains authored workers
   only for deterministic projection aggregation by foreground `CreateRenderContext`.
4. Tests verify each trigger publishes typed `RunRequested` and its worker subscribes and unsubscribes directly. The
   trigger has no worker reference, and no mutual worker-trigger reference exists.
5. Tests verify one active run per worker, coalescing to one fresh follow-up, cancellation, and no cross-worker
   serialisation.
6. Tests verify AgenticMind starts with an empty top-level read-only latest render dictionary. Only foreground prompt
   execution calls `CreateRenderContext`, and AgenticMind's returned dictionary contains `character`, deterministic
   `characters`, complete `observations`, and all authored worker projections.
7. Tests verify the foreground template uses the exact dictionary returned by `CreateRenderContext`. AgenticMind
   atomically publishes that exact dictionary only after successful rendering and retains the previous snapshot after
   context-construction or rendering failure.
8. Tests verify a worker captures the currently published snapshot at run start and uses it for the complete run without
   requesting context construction, source or projection aggregation, or timeline refresh. Before first publication it
   receives the empty dictionary, and `ContextWorkerRunInput` does not exist.
9. Tests verify worker output cannot feed the same or another worker directly. A later worker sees that output only in a
   snapshot constructed and published by a subsequent successful foreground render.
10. Tests verify ContextWorker atomically stores and returns the exact `IReadOnlyDictionary<string, object?>` returned
    by a worker. Producer fixtures treat the dictionary and nested values as immutable after return. Contract tests
    document post-publication mutation as a producer violation with potentially undefined or stale behaviour.
11. API tests verify `IReadOnlyDictionary` is not claimed to prove deep immutability and no public
    `ContextualSnapshot`, worker-specific `IContextual` wrapper, or `ContextWorkerState` exists. Aggregation and
    publication tests require no recursive copying or freezing, cycle detection, scalar allowlist, reflection
    observation projection, or live Godot-object rejection. Failed runs retain the prior projection.
12. LLM-worker tests verify immutable lifetime PromptStack capture, one cached compilation after attachment, no
    invalidation or recompilation, captured-snapshot rendering, typed schema validation, and absence of tools/actions.
    They verify compilation failure logs once, invokes no provider, and leaves the worker inactive with its fallback.
13. Tests verify observation timeline records pass directly to Handlebars while preserving exact `TypeKey` dispatch,
    record property visibility, and fallback data.
14. Diagnostic tests verify structured debug events for run start, coalescing, successful publication with key count,
    and follow-up scheduling. Failed runs and compilation retain error diagnostics.
15. Lifetime tests verify event unsubscription and Mind cancellation stop active and deferred worker activity without
    post-exit trigger handling, provider invocation, or projection replacement.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
