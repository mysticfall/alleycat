---
id: CORE-011
title: Shared Model Services
---

# Shared Model Services

## Requirement

The project must load each AI model it runs once per game session and share that instance among every consumer,
through a model-agnostic service layer that queues concurrent requests fairly and stays transparent to consumers.
Version 1 covers MotionBricks, Supertonic 3 and Wav2Arkit.

## Goal

Stop video and system memory from growing with each NPC that moves, speaks or lip-syncs. Today each consumer
constructs its own model instance; this layer replaces that with one shared backend per model and a per-backend
scheduler. Consumers keep their existing interfaces and per-consumer state, so sharing changes resource usage, not
consumer behaviour. The base layer is independent of ONNX and of in-process execution so remote services can adopt it
later. It also provides the seam for future priority and budget control without prescribing either now.

## User Requirements

1. Adding NPCs that move, speak or lip-sync does not add another copy of a model to video or system memory.
2. When several characters request the same model at once, every character keeps being served; one busy character
   cannot indefinitely delay another.
3. Movement, speech and lip-sync behave as before for each character: the same outputs, failure reporting, request
   replacement and cancellation outcomes. Waiting behind other characters' requests may add latency under contention;
   no latency budget is promised.
4. If a model cannot be loaded, every character that depends on it reports the same truthful failure instead of
   some characters silently working and others not.
5. Leaving a scene or quitting the game does not freeze the game while model work finishes.

## Technical Requirements

### Layering

1. Provide a model-agnostic base layer made of a shared backend registry, a per-backend scheduler, and work items.
   Model-specific layers derive from or extend it. Exact type names are implementation choices.
2. The base layer must not reference ONNX Runtime, execution providers, file paths or Godot types, and must not assume
   in-process execution. A backend is an opaque, disposable resource that a work item runs against asynchronously.
3. Model-specific layers own model loading, key construction, per-call execution and declared concurrency.
4. Backends and work items must not access Godot APIs or live scene nodes. Consumers resolve Godot-dependent inputs,
   such as globalised `res://` paths, on the scene thread before constructing keys or submitting work.

### Shared Backend Registry

5. `Game` owns one registry for its autoload lifetime and registers it through dependency injection under
   [CORE-004](../004-global-service-resolution/index.md). Production consumers resolve it from `Game`; consumers also
   expose an injection seam so tests and standalone fixtures can supply a registry directly.
6. The registry resolves a backend by a model key. Each model-specific layer defines key equality covering everything
   that changes the loaded artefact, such as the resolved absolute model path and the requested execution provider or
   backend. Consumers with equal keys share one backend; unequal keys never share.
7. Loading is lazy and happens exactly once per key. The first request starts the load off the scene thread;
   concurrent first requests for the same key wait on that single load.
8. A load failure is cached for its key and delivered to every current and future requester of that key. The registry
   does not retry silently; a new attempt requires a new game session. The cache covers only the shared backend load.
   Failures from a consumer's own per-consumer state are reported by that consumer's handle and never cached under
   the model key.
9. A successful load that degrades internally, such as Supertonic's CUDA-to-CPU fallback under SPCH-007, is a
   successful load; its diagnostics are reported once per backend, not once per consumer.
10. Loaded backends stay loaded until `Game` shutdown. Releasing a consumer never unloads or disposes a shared backend.

### Scheduler and Work Items

11. Each backend has one scheduler with a bounded concurrency limit. The default is 1. A model-specific layer may
    declare a higher limit only where the backend's thread-safety for concurrent calls has been verified.
12. A work item carries the consumer identity, an optional supersession key, a cancellation token, and an extensible
    metadata bag. v1 scheduling reads only the consumer identity and supersession key; other metadata is carried
    unchanged for future priority or budget policies.
13. The scheduler chooses the next item through a scheduling-policy seam whenever a concurrency slot is free. The v1
    policy is fair round-robin across consumers with pending items, and FIFO within one consumer.
14. Supersession is per consumer and latest-wins: a newly submitted item replaces that consumer's queued item with the
    same supersession key. The replaced item settles as cancelled without running. If that consumer's item with the
    same key is already running, it is not interrupted, but its result is discarded on completion under TR16.
    MotionBricks requires this.
15. Items that are cancelled or superseded before they start are dropped without running and settle as cancelled.
16. In-flight backend calls are never interrupted. Cancelling a running item, or submitting a newer item from the same
    consumer with the same supersession key, discards the running item's result on completion; the consumer observes
    cancellation, never a stale result.
17. A failure during one work item's execution faults only that item. The scheduler keeps serving other items.
18. Item completions are delivered asynchronously, never inline on the scheduler's dispatch path. Submission is
    thread-safe; consumers keep any stricter thread rules their own specs define.

### Per-Consumer Handles

19. Each consumer receives a handle that implements its existing interface and routes calls through the scheduler.
    Consumer code above that interface stays unchanged. v1 interfaces are:
    - MotionBricks: `SomaInferenceWorker.ISession` and the worker's submit, cancel, take and dispose contract under
      [NAV-001](../../nav/001-npc-navigation/index.md#outcome-and-worker-contract);
    - Supertonic: `ISupertonicInferencePipeline` under
      [SPCH-007](../../speech/007-supertonic-speech-generator/index.md);
    - Wav2Arkit: the inference path of `Wav2ArkitLipSyncPlayer` under
      [SPCH-001](../../speech/001-wav2arkit-lipsync-player/index.md).
20. Per-consumer state stays in the handle or consumer, not the shared backend. Per-call inputs that differ between
    consumers, such as a Supertonic voice style, travel with the work item. A shared backend holds no state carried
    from one consumer's call into another's.
21. MotionBricks must split the shared native inference session from per-consumer codec state: model frames, rest
    pose and index maps belong to the consumer. Failures in that codec state, such as missing joints or model frames,
    are the consumer handle's failures under TR8, not cached load failures of the shared session.
22. Disposing a handle cancels that consumer's queued items, discards its in-flight results and releases its
    per-consumer state without waiting for native work and without blocking the scene thread.
23. Existing test seams keep working: session factories or decorators, failure injection and observers can still be
    substituted, at the backend or handle level, without a GPU.

### Shutdown and Diagnostics

24. `Game`'s application-shutdown boundary, shared by menu exit and root-window close, stops admission on every
    scheduler, settles queued items as cancelled, and disposes each backend only after its in-flight call or load
    returns, never concurrently with it. It returns one aggregate completion that final quit awaits asynchronously;
    the scene thread is never blocked. This generalises the current `MotionBricksWorkers.Stop` semantics. Native calls
    cannot be interrupted, so a native call that never returns still prevents graceful exit.
25. Expose load and execution events, such as load started, succeeded or failed per key and item queued, started,
    dropped or completed per consumer, through an observer seam and log-only diagnostics under
    [CORE-007](../007-microsoft-logging-integration/index.md). These must not change scheduling behaviour.

## In Scope

- The model-agnostic registry, scheduler, work item and scheduling-policy seam, with the v1 round-robin policy.
- Model keys, lazy single loading, load coalescing, cached load failure and shutdown-only disposal.
- Per-consumer handles for MotionBricks, Supertonic and Wav2Arkit that implement their existing interfaces.
- The MotionBricks split of the shared native session from per-consumer codec state.
- `Game` ownership, DI registration and the non-blocking application-shutdown boundary.
- Observer and diagnostic seams, unit tests and runtime load evidence required by the acceptance criteria.

## Out Of Scope

- Priority classes, budgets and cross-backend GPU budgeting. The policy seam and metadata bag remain required.
- Migrating Silero VAD, Audio2Face, LLM or other remote services to this layer.
- Reference-counted or idle unloading of backends.
- Batching several consumers' requests into one backend call.
- Escalating fatal device errors, such as GPU device loss, beyond TR17's per-item failure handling.
- Exact concurrency limits above the default, queue diagnostics formats, or latency targets.

## Acceptance Criteria

### User Requirement Acceptance

1. **CORE-011-UAC-001 (UR1):** On the supported NVIDIA host, with real NPC templates that move, speak and lip-sync,
   native model loads per key equal 1 regardless of NPC count, both for NPCs present at scene load and NPCs added
   later.
2. **CORE-011-UAC-002 (UR2–3):** In a three-NPC runtime session, every NPC moves, speaks and lip-syncs. Request
   replacement, cancellation and failure reporting match the consumers' own specs.
3. **CORE-011-UAC-003 (UR4):** With a deliberately unloadable model path, every consumer of that model reports the
   same load failure, and no further load attempt occurs in that session.
4. **CORE-011-UAC-004 (UR5):** Scene exit and menu or root-window quit during in-flight model work leave the scene
   thread responsive, and the process exits gracefully once native calls return.

### Technical Requirement Acceptance

1. **CORE-011-TAC-001 (TR1–4):** Source review confirms the base layer has no ONNX Runtime, execution-provider, path
   or Godot dependency, and that backends and work items do not touch scene nodes.
2. **CORE-011-TAC-002 (TR5–7, TR10):** Unit tests show that loads per model key equal 1 regardless of consumer count,
   that concurrent first requests coalesce into one load, that unequal keys load separately, and that releasing
   consumers does not dispose a backend. Runtime diagnostics show one load per key with three NPCs. Resolving the
   registry through `Game` returns the single `Game`-owned instance.
3. **CORE-011-TAC-003 (TR8–9):** Unit tests show that an injected load failure reaches every current and later
   requester without a retry, while a per-consumer state failure affects only its handle and is not cached under the
   model key. A degraded load reports its diagnostics once per backend.
4. **CORE-011-TAC-004 (TR11, TR13):** Unit tests show that running items never exceed the declared limit, that the
   default is 1, and that round-robin serves every consumer with pending items before any consumer receives a second
   turn. A substituted policy is honoured.
5. **CORE-011-TAC-005 (TR12, TR14–16):** Unit tests show latest-wins supersession per consumer and key, without
   affecting other consumers, and that cancelled or superseded queued items never run. In-flight cancellation or
   same-key supersession lets the call finish but discards its result. Metadata reaches the policy unchanged.
6. **CORE-011-TAC-006 (TR17–18):** Unit tests show that an execution failure faults only its item, that the scheduler
   continues, and that completions are not run inline on the dispatch path.
7. **CORE-011-TAC-007 (TR19–23):** Each v1 consumer passes its existing tests through a handle without changes above
   its interface. Tests show that per-call inputs such as voice style are isolated between consumers. The MotionBricks
   split keeps codec state per consumer, handle disposal cancels and releases without blocking, and existing session
   factory, failure-injection and observer seams still work without a GPU.
8. **CORE-011-TAC-008 (TR24):** Unit tests show that shutdown stops admission, cancels queued items, disposes each
   backend only after its in-flight call or load returns, and returns an aggregate completion without blocking. Runtime
   evidence covers menu exit and root-window close, each triggered through the production exit path while real model
   work is in flight. It is collected as described in [Runtime Evidence](#runtime-evidence).
9. **CORE-011-TAC-009 (TR25):** Tests observe load and item events through the observer seam. Diagnostics stay
   log-only and do not alter scheduling.
10. **CORE-011-TAC-010:** `dotnet format --verify-no-changes AlleyCat.sln` and
    `dotnet build AlleyCat.sln -warnaserror` pass.

### Runtime Evidence

The integration tests below provide the runtime evidence for these criteria. They are marked `[IsolatedProcess]` under
[TEST-001](../../testing/001-test-framework/index.md#isolated-process-tests). They load the real models on CUDA and
need a fresh process with no other Godot process running, so default and class-wide runs exclude them. They count as
evidence only when run by exact `--test-method` selection on the supported NVIDIA host. A default suite pass is not
evidence for these criteria and does not satisfy them. Being opt-in does not narrow any criterion. Parts of a criterion
that these tests do not exercise still need their own evidence.

- `SharedModelLoadingTests` is the shared-model load regression, covering UAC-001, TAC-002's three-NPC runtime
  diagnostics and UAC-002's move, speak and lip-sync outcome. It counts native model loads per key with real NPC
  templates, for NPCs present at scene load and NPCs added later.
- `SharedModelShutdownTests` covers TAC-008's runtime evidence and UAC-004's quit and scene-exit paths. Each fact gets
  its own headless Godot process running the shutdown probe scene while MotionBricks and Supertonic work is in flight.
  For the quit paths, the probe quits through the real `GameMenu`, using mock XR controller presses, or through a
  root-window close request. For scene exit, the probe changes scene through the `LoadingScreen` to an empty scene and
  verifies that the NPC scene is freed, every consumer's handle is released, pending syntheses end cancelled rather
  than faulted and in-flight motion settles, and the process then quits cleanly with backends disposed. The fact
  observes the process from outside.

## References

### Implementation

- @game/src/Game.cs
- @game/src/Motion/SomaInference.cs
- @game/src/Motion/SomaInferenceWorker.cs
- @game/src/Motion/MotionBricksWorkers.cs
- @game/src/Speech/Generation/Supertonic/
- @game/src/Speech/LipSync/Wav2ArkitLipSyncPlayer.cs

### Runtime Evidence Tests

- @integration-tests/src/Models/SharedModelLoadingTests.cs
- @integration-tests/src/Models/SharedModelShutdownTests.cs
- @game/tests/shared-ai-models/shared_model_shutdown_probe.tscn

### Related Specs

- [CORE-004: Global Service Resolution](../004-global-service-resolution/index.md)
- [CORE-007: Microsoft Logging Integration](../007-microsoft-logging-integration/index.md)
- [CORE-010: Main-Thread Dispatcher](../010-main-thread-dispatcher/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- [CTRL-001: Player Locomotion](../../ctrl/001-locomotion/index.md)
- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-007: Supertonic Speech Generator Component](../../speech/007-supertonic-speech-generator/index.md)
