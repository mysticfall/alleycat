---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

The runtime must execute one logical AgenticMind session through stateless provider requests whose context is causal,
fresh, and confirmed only by locally valid accepted provider responses, with settled `speak` and `wait` tool exchanges
disposed from the provider transcript rather than retained.

## Goal

Let an NPC reason continuously without treating provider protocol as memory — settled `speak` and `wait` exchanges are
disposed rather than accumulated — without leaking observation text through scheduling, without confirming scene
context that the provider did not validly accept, and with model-facing tool guidance that describes these contracts
accurately.

## User Requirements

1. An NPC receives established event history and an up-to-date scene status whenever it makes a logical request.
2. New event history since the NPC's previous valid response is clearly distinguished from established history.
3. The NPC can wait for an appropriate reason; a wait outcome is bounded to a wake reason, elapsed game time, and
   current game time, and `wait` is not an observation-text delivery channel.
4. The NPC's model-facing session-tool guidance matches runtime behaviour: fresh context arrives with every logical
   request without waiting, and `wait` yields for future developments without promising observation text or observable
   tool feedback.
5. A fresh event gets priority over importance pressure, an attended speaker completion, and timeout. Routine pressure
   does not interrupt active reasoning.
6. A continued utterance cannot cause a response to incomplete text: its context is presented, then awaits confirmation
   before settling.
7. Failed, cancelled, stale, or invalid provider work does not make an NPC forget or falsely confirm scene context.
8. An NPC's working context stays lean and stable across long sessions: once a `speak` or `wait` exchange settles, its
   tool messages stop occupying later request contexts.
9. An NPC's coherence never depends on seeing tool acknowledgements: committed speech is remembered as its own event,
   and cancelled or failed speech leaves no invented memory.
10. An NPC always knows the current game time when it reasons, carried by current scene status under AI-003 on the same
    clock as its event history.

## Technical Requirements

### Session And Request Context

1. There is one logical session per AgenticMind, ending only on node exit or contained unrecoverable failure. The
   transient
   provider transcript is never Mind memory.
2. The static system instruction is rendered once at session start under AI-003. Every logical provider request then
   uses, in order: the static system instruction; one user event-timeline message; one user current-scene message; and
   the bootstrap input plus retained accepted exchanges as applicable. When no exchange is retained, the tail is the
   bootstrap input alone (TR-21).
3. The timeline message contains established event history and a `--- New Since Your Previous Response ---` tail. The
   tail contains event-timeline entries beyond the watermark confirmed by the last locally valid accepted provider
   response. Each selected event uses its observation-owned canonical text under AI-001 and AI-003. An empty timeline
   still has a coherent watermark.
4. A logical request materialises its complete context once. Exact transport retry reuses that materialisation; invalid
   response recovery and a fresh replacement request rematerialise it. Materialisation must not change session-fixed
   `ScenarioContext` or scenario.
5. Accepting a locally valid provider response atomically appends its valid protocol results and confirms that request
   snapshot's timeline watermark. Failure, cancellation, stale response, and invalid response confirm nothing.
6. `CurrentSceneStatus` is rendered freshly for every logical request under AI-003. Its inputs include initial
   attended characters, active watches, and the request snapshot's current game time (AI-003 TR-6).

### Scheduling And Wait

7. Scheduling is payload-free and ordered: fresh event, threshold-qualified pressure, attended-speaker completion, then
   timeout. It uses Mind's accepted metadata only.
8. `wait` returns a reason, elapsed game time, and current game time only. It neither returns observation text nor
   advances
   the event-timeline watermark or cursor.
9. Completing or resetting a wait must not clear a timeline cursor. Scheduling pressure outside wait clears only when a
   provider response has been accepted and confirms the corresponding request snapshot.
10. Automatic delivery is payload-free: injected messages and `wait` results must contain no observation text.
     Model-visible event text appears only in the per-request event-timeline message rendered by AI-003.

### Continuation And Failure

11. Continuation sequence `S` has two stages: it may admit a request whose watermark includes `S`, then becomes
    `PresentedAwaitingConfirmation`. A valid accepted response confirms and settles it. Blank, failed, abandoned, stale,
    invalid, or cancelled work releases it without confirmation.
12. Provider responses remain tool-only and are completely validated before any tool effect. Invalid responses have no
    transcript, observation, action, or watermark effect and use bounded recovery; transport retry and recovery remain
    contained.
13. The tool inventory includes `speak`, `wait`, and authorable watch tools from AI-010. Tool delegates send any
     resulting observations through Mind's atomic queue. Tool validation and typed tool binding occur only at
     AgenticMind composition; the common runtime has no feature service bag.
14. Speech admitted to its voice pipeline remains committed at playback hand-off and uses Mind's node-lifetime
     exact-once
     commit identity. Node exit remains terminal and creates no replacement request or synthetic confirmation.

### Model-Facing Tool Guidance

15. Model-facing session-tool descriptions must accurately distinguish each tool from automatic per-request context
      delivery (TR-2, TR-3, and TR-6): no description may state that waiting is required for new context to arrive or
      imply that `wait` results contain event or observation text (TR-8, TR-10). No `speak` or `wait` description may
      promise observable tool feedback: completed actions are carried by remembered events and fresh scene status,
      never by retained tool messages (TR-22).
16. `wait` must be described as intentionally yielding until future developments or remaining silent, and `speak` as
      taking effect in the world; neither description may promise that its result or acknowledgement is observable —
      committed speech is remembered as the NPC's own event, and timing is carried by current scene status. Watch-tool
      descriptions follow AI-010's model-facing watch guidance; shared-instruction guidance follows AI-003's Shared
      Context Interpretation Guidance (TR-12–TR-16).

### Tool Exchange Disposal

17. An accepted response's tool exchange is disposed after its batch fully settles — every call executed or
    canonically skipped, and every resulting tool observation ingested — and before the next request context
    materialises. Disposal changes only transcript retention: whole-batch validation before effects, atomic acceptance
    with watermark confirmation, invalidation arbitration, serial execution, speech admission, and terminal
    cancellation guarantees are unchanged.
18. Disposal never mutates an already-materialised request. Exact transport retries reuse the frozen request as
    materialised; invalid-response recovery and fresh replacement requests rematerialise context under the retained
    transcript (TR-4).
19. The disposal unit is the complete exchange: the accepted assistant message or messages — including any text and
    reasoning content — together with their tool-result message. Disposal is all-or-nothing per exchange, leaving no
    partial removal and no orphaned tool call or result.
20. An exchange is disposed iff every tool call in its batch targets a disposal-opted tool. `speak` and `wait` opt in;
    every other tool — authorable watch tools from AI-010 and authored extras — defaults to retention. A batch mixing
    opted and non-opted calls is retained whole.
21. The bootstrap input is never removed, so a fully disposed transcript reduces to the bootstrap input alone.
    Session-wide tool call-ID duplicate validation is independent of transcript retention: a consumed call ID stays
    consumed for the whole session even after its exchange is disposed.
22. Disposed tool results never appear in any request context. Committed speech survives through the persisted
    self-speech observation in the per-request event timeline (AI-001); speech cancelled or failed before playback
    hand-off leaves no character-visible trace; infrastructure failures stay in runtime diagnostics and are never
    promoted to timeline or scene text.

## In Scope

- One-session lifecycle, stateless provider requests, tool-only validation, and contained retry/recovery.
- Causal event-timeline watermarks, fresh per-request scene status, and atomic response confirmation.
- Canonical observation-owned text for per-request timeline history.
- Payload-free scheduling and wait semantics.
- `speak`/`wait` tool-exchange disposal: settlement boundary, whole-exchange unit, opt-in policy, and the bootstrap and
  call-ID invariants.
- Current game-time delivery through AI-003 current scene status.
- Accurate model-facing session-tool descriptions for automatic delivery, `wait`, and `speak`.
- Continuation admission and confirmation settlement.
- Composition-time binding of watch tools with AI-010.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, and broader perception changes.
- Summarisation-based transcript compaction and any generic retained-result or working-memory channel. Retention — and
  therefore accumulation — remains for non-opted tools: watch arming and acknowledgement feedback stays model-visible
  per AI-010's current contracts.
- Watch-exchange disposal, lore tooling and lore working context, and new retry subsystems.
- Final timeout, threshold, and retry tuning values.

## Acceptance Criteria

### User Requirements

1. Acceptance shows each NPC request contains established history, a clearly headed new-history tail, and current scene
   status. Selected events use observation-owned canonical text. Automatic delivery supplies no observation text through
   `wait`, injected, or scheduling messages.
2. Acceptance shows fresh events win over pressure, attended-speaker completion, and timeout, while routine pressure
   does
   not interrupt an active request.
3. Acceptance shows incomplete continuation context is never falsely settled by a failed, blank, abandoned, or invalid
   provider interaction.
4. Acceptance shows each session-tool description accurately distinguishes automatic per-request delivery from
   deliberate waiting, and none claims that waiting is required for new context or that wait results carry observation
   text.
5. Acceptance shows a long session with many completed `speak` and `wait` batches keeps later request contexts lean —
   no tool messages from those exchanges accumulate — while retained watch feedback keeps reaching the model.
6. Acceptance shows committed speech is remembered exactly once as the NPC's own event, and neither cancelled nor
   failed speech leaves a fabricated memory or character-visible trace.
7. Acceptance shows the NPC can always read the current game time from its current scene status, on the same clock as
   its event timestamps.

### Technical Requirements

1. Tests verify request order, coherent empty-history watermarks, and exactly-once context materialisation per logical
   request, with exact retries reusing it and recovery or replacement rematerialising it.
2. Tests verify only locally valid accepted responses atomically confirm a snapshot watermark and clear outside-wait
   pressure; all failure, cancellation, stale, and invalid paths leave both unconfirmed.
3. Tests verify `wait` returns only reason, elapsed time, and current time; wait reset never moves a timeline cursor.
4. Tests verify no observation text occurs in `wait` results or injected messages, that model-visible event text
   appears only in the per-request event-timeline message, and that scheduling signals carry no payload.
5. Tests verify the `S` admission and `PresentedAwaitingConfirmation` stages, including release on every non-confirming
   outcome.
6. Tests verify watch-tool validation and typed binding occur only at AgenticMind composition.
7. Tests verify the `wait` description presents yielding semantics, with no description text contradicting the
   payload-free wait contract, automatic per-request delivery, or the no-observable-feedback requirement for `speak`
   and `wait` (TR-15, TR-16).
8. Tests verify disposal fires exactly at the settlement boundary and before the next request materialises, never
   mutates an already-materialised request, and leaves exact transport retries reusing the frozen request with recovery
   and replacement rematerialising as before.
9. Tests verify disposal removes whole exchanges only — accepted assistant messages including text and reasoning plus
   the tool-result message — never orphaning a call or result, and never removing the bootstrap input.
10. Tests verify the disposal policy: batches whose every call targets `speak` or `wait` are disposed, mixed batches
    are retained whole, and non-opted tool exchanges remain model-visible.
11. Tests verify session-wide call-ID duplicate rejection still rejects a replayed ID after its exchange was disposed.
12. Tests verify disposed tool results appear in no request context, committed speech appears exactly once as a
    persisted self-speech timeline observation, and pre-hand-off cancelled or failed speech leaves no timeline or
    scene trace.
13. Tests verify every logical request's current scene status carries its snapshot's current game time (AI-003 TR-6).

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
