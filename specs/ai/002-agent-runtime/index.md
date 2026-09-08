---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

The runtime must execute one logical AgenticMind session through stateless provider requests whose context is causal,
fresh, and confirmed only by locally valid accepted provider responses.

## Goal

Let an NPC reason continuously without treating provider protocol as memory, leaking observation text through
scheduling, or confirming scene context that the provider did not validly accept, and with model-facing tool guidance
that describes these contracts accurately.

## User Requirements

1. An NPC receives established event history and an up-to-date scene status whenever it makes a logical request.
2. New event history since the NPC's previous valid response is clearly distinguished from established history.
3. The NPC can wait for an appropriate reason and receives only the reason, elapsed game time, and current game time;
   `wait` is not an observation-text delivery channel.
4. The NPC's model-facing session-tool guidance matches runtime behaviour: fresh context arrives with every logical
   request without waiting, and `wait` yields for future developments and reports only its wake reason and timing.
5. A fresh event gets priority over importance pressure, an attended speaker completion, and timeout. Routine pressure
   does not interrupt active reasoning.
6. A continued utterance cannot cause a response to incomplete text: its context is presented, then awaits confirmation
   before settling.
7. Failed, cancelled, stale, or invalid provider work does not make an NPC forget or falsely confirm scene context.

## Technical Requirements

### Session And Request Context

1. There is one logical session per AgenticMind, ending only on node exit or contained unrecoverable failure. The
   transient
   provider transcript is never Mind memory.
2. The static system instruction is rendered once at session start under AI-003. Every logical provider request then
   uses,
   in order: the static system instruction; one user event-timeline message; one user current-scene message; and the
   bootstrap input or accepted transcript as applicable.
3. The timeline message contains established event history and a `--- New Since Your Previous Response ---` tail. The
   tail contains event-timeline entries beyond the watermark confirmed by the last locally valid accepted provider
   response. Each selected event uses its observation-owned canonical text under AI-001 and AI-003. An empty timeline
   still has a coherent watermark.
4. A logical request materialises its complete context once. Exact transport retry reuses that materialisation; invalid
   response recovery and a fresh replacement request rematerialise it. Materialisation must not change session-fixed
   `ScenarioContext` or scenario.
5. Accepting a locally valid provider response atomically appends its valid protocol results and confirms that request
   snapshot's timeline watermark. Failure, cancellation, stale response, and invalid response confirm nothing.
6. `CurrentSceneStatus` is rendered freshly for every logical request under AI-003. Its projection inputs include
   initial
   attended characters and active watches.

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
     imply that `wait` results contain event or observation text (TR-8, TR-10).
16. `wait` must be described as intentionally yielding until future developments or remaining silent, with a result
     limited to wake reason, elapsed game time, and current game time. Watch-tool descriptions follow AI-010's
     model-facing watch guidance; shared-instruction guidance follows AI-003's Shared Context Interpretation Guidance
     (TR-11–TR-15).

## In Scope

- One-session lifecycle, stateless provider requests, tool-only validation, and contained retry/recovery.
- Causal event-timeline watermarks, fresh per-request scene status, and atomic response confirmation.
- Canonical observation-owned text for per-request timeline history.
- Payload-free scheduling and wait semantics.
- Accurate model-facing session-tool descriptions for automatic delivery and `wait`.
- Continuation admission and confirmation settlement.
- Composition-time binding of watch tools with AI-010.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, compaction, and broader perception changes.
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
   payload-free wait contract or automatic per-request delivery.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
