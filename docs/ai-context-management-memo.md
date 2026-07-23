# AI Context Management Memo

## Purpose

This temporary design memo preserves context-management background for future agents without requiring the original chat
log. It is explanatory only; the linked specifications are authoritative.

## Consumers

- `planner`: use this memo to seed future context-management work without revisiting settled turn architecture.
- `coder`: follow the authoritative specifications and use this memo only for design background and explicit deferrals.
- `reviewer`: reject changes that reintroduce framework history, tool-owned Mind mutation, or source-specific
  scheduling.

## Authoritative Specifications And Boundaries

- [AI-001](../specs/ai/001-mind/index.md) defines observation representation, Mind-owned ingestion, contextual
  importance, scheduling, interruption, and node lifetime.
- [AI-002](../specs/ai/002-agent-runtime/index.md) defines bounded tool-only turns, transient request replay, provider
  transport, standard action results, and the synthetic `end_turn` marker.
- [AI-003](../specs/ai/003-prompt-api/index.md) defines complete timeline rendering and actor-relative observed speech.
- [BODY-006](../specs/body/006-voice/index.md) defines FIFO speech admission, serial production, and voice teardown.
- [CTX-001](../specs/context/001-contextual-information-api/index.md) defines non-AI-specific contextual data and scene
  requests through `AlleyCat.Context`.
- CTX-001 excludes prompt placement, rendering, ranking, summarisation, AI retrieval, memory, lore, perception backends,
  and detailed context taxonomy.
- A future AI context-provider API may define presentation-neutral retrieval, but this memo establishes no such API.

## Approved Current Architecture

1. Mind's ordered subjective observation timeline is authoritative cross-turn memory for its node lifetime.
2. Every successfully ingested observation enters both the timeline and pending scheduling queue.
3. Each observation calculates contextual importance exactly once at ingestion from an `ObservationContext` that
   initially contains the owning `ICharacter`. The validated value is stored with its pending entry.
4. Importance controls normal eligibility and optional active-turn interruption. Zero importance still processes through
   maximum wait; no source receives a separate scheduling exemption.
5. Every turn receives a complete immutable timeline snapshot in ordinary prompt render context.
6. The prompt stack is compiled and rendered each turn into the sole system instruction.
7. Every turn uses a fresh tool-only provider loop. No prior transcript, observation-summary message, response ID, or
   raw assistant/tool protocol crosses turn boundaries.
8. The active loop keeps one instruction snapshot. New observations remain recorded for the next turn.
9. Every tool returns `AgentToolResult` with an optional model-facing message and ordered observations. The common
   wrapper validates the complete result, asks Mind to ingest observations atomically, and returns only the message.
10. Mind owns mutation and actor stamping. Tools have no public observation recorder or sink and do not mutate Mind
    directly.
11. An enabled high-importance interruption cancels the active invocation as expected pre-emption. Invocation and tools
    settle before exactly one fresh replacement starts, with no turn overlap.
12. Committed events survive interruption. Multiple qualifying arrivals coalesce, and disable or node exit suppresses
    unsafe replacement.

## Tool-Only Turn Protocol

- The explicit tool-only loop is the permanent production route, not a diagnostic alternative to a generic typed
  terminal-result path.
- Every request requires at least one tool call and carries all configured actions plus reserved synthetic `end_turn`.
- `end_turn` is neither an action nor an observation and is never invoked locally. A zero-action response must contain
  sole `end_turn`.
- One or more production actions followed by exactly one final `end_turn` form a terminal batch. After complete batch
  validation and successful serial action execution, the turn finishes without result replay or another model request.
- An action-only batch is the result replay and continuation path when the model needs action results before deciding
  whether to act again or finish. Successful calls and results enter transient history for the next sequential request.
- `AllowMultipleToolCalls` is configurable and defaults to `false`, but local validation accepts valid all-action
  batches and executes them serially.
- Non-final, repeated, malformed, or otherwise invalid markers or batches fail closed. Assistant text, unknown content,
  tool errors, and exhausted request or action bounds also fail without model repair or automatic retry.
- `MaxModelRequests` and `MaxToolActions` have normative defaults of `8`. They may remain constants until a settings
  surface is exposed; an exposed surface keeps the names, positive-integer validation, and defaults.
- OpenAI Responses is the default transport. Each request uses `store: false`, omits `previous_response_id`, and fully
  replays transient history. Chat Completions is available only as an explicitly selected rollback transport.
- No provider `response_format`, ordinary assistant text, or generic typed terminal result completes a turn.

## Context-Layer Boundaries

Keep these concepts separate:

- authoritative game and domain state;
- Mind's ordered subjective observation timeline;
- current scene, character, and lore context used to build and render the prompt;
- transient protocol for the active tool loop; and
- audit and diagnostic logs.

Current snapshots and retrieved snippets may inform prompt construction or rendering without becoming observations.
Only domain events intentionally and successfully ingested by Mind belong in the authoritative timeline.

## Observation Representation

- `ObservedAction` carries actor-relative action identity through an exact stable actor ID, not a scene-node reference.
- `ObservedSpeech : ObservedAction` represents owning-character, recognised-other, and unknown speech with the one exact
  semantic key `speech.observed`.
- Actor identity and nullable raw `VoiceId` remain separate. Ordinal ID matching provides configured, operational
  attribution rather than authenticated provenance, and `VoiceId` is never rendered as identity wording.
- One actor-relative prompt fragment renders self, recognised-other, and unknown speech perspectives.
- Tool-produced action actors are stamped by Mind before contextual importance is calculated.
- Tool-result observation batches remain ordered and are ingested all-or-nothing.
- Failed, cancelled, malformed, or invalid tools contribute no observations.
- Timeline observations are not mapped to durable framework messages, and raw tool protocol is not retained after a
  turn.

## Scheduling And Interruption

- Pending entries retain FIFO order and their once-calculated importance.
- Cumulative importance, maximum wait, and a minimum interval govern normal scheduling.
- The minimum-turn interval authoring range is 0–5 seconds.
- Interruption is configurable and disabled by default. One newly ingested observation must individually meet the
  interruption threshold; cumulative sub-threshold entries do not interrupt.
- Replacement starts only after active invocation and tool settlement, bypasses the minimum interval exactly once, and
  never overlaps another turn.
- Natural completion, cancellation, disable, and node-exit races must not duplicate cancellation or replacement.

## Voice Submission

- `IVoice.SpeakAsync` returns non-result `ValueTask`; successful completion means FIFO admission, not playback
  completion.
- Blank input is an `ArgumentException`; disabled or unconfigured operation is an `InvalidOperationException`.
- Cancellation before admission cancels without work. Cancellation after admission does not retract committed work.
- Busy AI speech queues FIFO and runs through one serial generation and playback-hand-off pipeline.
- One failed item is isolated and does not block later items.
- Compatibility `void Speak` is deliberately lossy but must validate synchronously where possible and observe every
  asynchronous fault.
- Voice teardown settles active and queued work without callbacks accessing freed Godot nodes.

## Deferred Context Management

The current timeline is complete and unbounded for the Mind node's lifetime. The following remain deferred:

- richer importance models beyond owning-character-relative speech and test observations;
- more production action tools beyond speech;
- cancelling or reversing world actions or speech already admitted before pre-emption;
- timeline summarisation, compaction, token budgeting, persistence, and cross-turn framework transcript retention;
- retrieval caching, freshness checks, source versioning, ranking, and context budgets;
- reducer contracts and metadata used to preserve, compact, expire, or drop context;
- durable memory ingestion and long-term relationship state;
- parallel speech generation or playback; and
- visual verification, because the refactor has no visual acceptance surface.

A future design may investigate metadata such as source, salience, freshness, or context kind. These names are
provisional and must not become implementation requirements without an approved specification update.

## Provider History Findings

Provider and framework abstractions can retain message sequences, tool protocol, or response identifiers. Those
capabilities do not change the approved architecture: only the active turn may retain protocol history, and Responses
receives a complete replay rather than stored state or `previous_response_id` chaining.

## Remaining Open Questions

1. What compaction or summarisation contract should apply if complete timeline rendering becomes impractical?
2. Which retrieved context belongs in prompt construction, ordinary render context, or on-demand tools?
3. Which attribution, salience, and freshness metadata should a future retrieval or persistence design standardise?
4. What migration and validation would be required before a reduced projection replaced the complete timeline snapshot?

## Specification Follow-Up

Future context-management proposals must preserve distinct user and technical requirements and explicitly amend AI-001,
AI-002, AI-003, or BODY-006 before changing the approved observation, tool-result, prompt, or voice architecture.
