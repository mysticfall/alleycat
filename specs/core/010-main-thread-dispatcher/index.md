---
id: CORE-010
title: Main-Thread Dispatcher
---

# Main-Thread Dispatcher

## Requirement

The project must provide one global dispatcher for safely starting Godot-bound work on the main thread, with deferred,
ordered execution and deterministic cancellation and completion behaviour across game shutdown.

## Goal

Give game systems an awaitable boundary for main-thread work without coupling consumers to scene paths, while preserving
Godot's event-loop ordering and the `Game` autoload lifetime. `GodotMainThreadDispatcher` names the concrete boundary
for Godot-deferred execution without making the `Game` host itself the dispatcher contract implementation.

## User Requirements

1. Game systems can request Godot-bound work from any thread without directly coordinating with the global scene.
2. Accepted work runs predictably in submission order and never begins inline with the submission.
3. Callers can await completion and observe cancellation or failures instead of losing them in fire-and-forget work.
4. Game shutdown does not leave accepted dispatcher operations unresolved or allow new work to start.

## Technical Requirements

1. The dispatcher contract must be named `IMainThreadDispatcher`.
2. Dispatcher queue, work-item, batching, cancellation, completion, and error machinery must live in the concrete
   `GodotMainThreadDispatcher` class in the `AlleyCat.Core.Threading` namespace.
3. `Game` must own one dedicated `GodotMainThreadDispatcher` member for its autoload lifetime and provide its Godot
   `CallDeferred` scheduling and lifecycle boundary to that instance. `Game` must not contain dispatcher queue or
   work-item machinery and must not implement `IMainThreadDispatcher`.
4. Dependency injection must register `Game`'s dedicated owned dispatcher instance under `IMainThreadDispatcher`.
   Resolving the interface must return that exact instance, not `Game`.
5. No authored dispatcher scene child is required or expected. The dispatcher is a plain service created by and owned by
   `Game`; a scene child may be introduced only when a separately approved requirement makes it necessary.
6. The API must accept both:
   - a synchronous `Action`; and
   - an asynchronous `Func<CancellationToken, ValueTask>`.
7. Each submission must accept caller cancellation and return an awaitable `ValueTask` representing that invocation.
8. A submission whose caller token is already cancelled must be rejected as cancelled and must not enter the queue.
9. Every accepted invocation, including one submitted from the main thread, must be queued and scheduled through Godot's
   deferred main event dispatch. No accepted delegate may be invoked inline with submission.
10. Accepted invocations must begin globally in FIFO acceptance order.
11. Each deferred flush must snapshot its batch. Work submitted while that batch is flushing must run in a later
    deferred callback rather than joining the active batch.
12. Caller cancellation before a queued delegate starts must settle its returned `ValueTask` as cancelled without
   invoking the delegate.
13. An asynchronous delegate that starts must receive a cancellation token linked to the caller token and the `Game`
   lifetime token.
14. Delegate invocation must begin on Godot's main thread. The dispatcher must not claim that continuations after an
   incomplete asynchronous await remain on the main thread.
15. Exceptions from synchronous and asynchronous delegates must fault and propagate through their returned `ValueTask`.
16. Completion of dispatcher operations must schedule awaiter continuations asynchronously rather than running them
   inline with queue processing.
17. `Game._ExitTree` must notify its owned dispatcher to atomically stop admission, cancel queued work and active
    asynchronous work, and settle every accepted dispatcher awaitable. Submissions after admission stops must return as
    cancelled.
18. AgenticMind outbound production-tool invocation must use the shared `IMainThreadDispatcher` through `AgentTool`.
    AgenticMind must not retain local deferred voice or Godot-action scheduling, queueing, or settlement machinery.

## In Scope

- The `IMainThreadDispatcher` contract and both delegate forms.
- `GodotMainThreadDispatcher` as the dedicated plain dispatcher service owned by `Game`.
- Dependency-injection registration of the exact dispatcher instance owned by `Game`.
- Deferred FIFO batching, cancellation, error propagation, asynchronous continuations, and shutdown semantics.
- AgenticMind migration of outbound production-tool invocation to the shared dispatcher.
- Automated verification of queue ordering, thread-of-invocation, cancellation, completion, error, and shutdown
  contracts.

## Out Of Scope

- Migrating unrelated deferred-action implementations in `Voice`, `SpeechGenerator`, or `Transcriber`.
- Requiring arbitrary asynchronous continuations within submitted work to remain on Godot's main thread.
- Additional dispatcher priorities, parallel queues, or scene-local dispatcher instances.

## Acceptance Criteria

1. A consumer can resolve `IMainThreadDispatcher`, submit either supported delegate form from any thread, and await its
   completion, satisfying User Requirements 1 and 3 and Technical Requirements 1 and 6-7.
2. `GodotMainThreadDispatcher` in `AlleyCat.Core.Threading` owns dispatcher machinery. `Game` owns one instance,
   supplies its Godot deferred-scheduling and lifecycle boundary, and does not implement `IMainThreadDispatcher`
   (Technical Requirements 2-3).
3. Resolving `IMainThreadDispatcher` returns the exact dedicated dispatcher instance owned by `Game`, not `Game`; no
   authored dispatcher scene child exists (Technical Requirements 4-5).
4. Automated verification confirms that every accepted invocation is deferred, begins on the Godot main thread, and
   begins in global FIFO acceptance order (User Requirement 2 and Technical Requirements 9-10 and 14).
5. Automated verification confirms that a flush processes only its snapshotted batch and schedules submissions made
   during that flush through a later deferred callback (Technical Requirement 11).
6. Automated verification confirms that pre-cancelled submissions are rejected, cancellation before queue execution
   prevents invocation, and started asynchronous work receives caller and `Game` lifetime cancellation
   (Technical Requirements 7-8 and 12-13).
7. Automated verification confirms that synchronous and asynchronous failures propagate to awaiters and that awaiter
   continuations are not run inline with queue processing (User Requirement 3 and Technical Requirements 15-16).
8. Automated verification confirms that `Game._ExitTree` closes dispatcher admission, cancels queued and active work,
   settles all accepted awaitables, and returns subsequent submissions as cancelled (User Requirement 4 and Technical
   Requirement 17).
9. AgenticMind production tools start through the shared dispatcher via `AgentTool`, and AgenticMind retains no local
   deferred voice or Godot-action scheduling, queueing, or settlement machinery (Technical Requirement 18).
10. Delivery includes the dispatcher implementation, dependency-injection wiring, AgenticMind migration, and mandatory
    verification (Technical Requirements 2-5 and 18).

## References

### Implementation

- @game/src/Game.cs
- @game/src/Core/Threading/GodotMainThreadDispatcher.cs

### Related Specs

- [CORE-001: Global Singleton](../001-global-scene/index.md)
- [CORE-004: Global Service Resolution](../004-global-service-resolution/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)
