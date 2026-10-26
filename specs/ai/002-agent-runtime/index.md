---
id: AI-002
title: Agent Runtime
---

# Agent Runtime

## Requirement

The runtime must execute one logical AgenticMind session through stateless provider requests whose context is causal,
fresh, and confirmed only by locally valid accepted provider responses, with every accepted tool exchange retained in
the provider transcript and pending scene observations delivered through runtime-authored synthetic tool exchanges.

## Goal

Let an NPC reason continuously with a provider transcript it can recognise as its own — retained tool exchanges
present its prior actions and delivered scene events in the ordinary tool-call and tool-result shape models are
trained on — without leaking observation text through scheduling, without confirming scene context that the provider
did not validly accept, and with model-facing tool guidance that describes these contracts accurately. Mind's
persistent event timeline remains canonical memory; the provider transcript is derived presentation.

## User Requirements

1. An NPC sees its own accepted tool exchanges and every scene event delivered since it last reasoned, plus an
   up-to-date scene status, whenever it makes a logical request.
2. An NPC recognises incoming scene events as delivered information: a delivered reply — such as an answer to the
   NPC's question — reads as available to respond to within the conversation's chronology, not as pending arrival
   through some other channel the NPC must wait for.
3. The NPC can wait for an appropriate reason; a wait outcome is bounded to a wake reason, elapsed game time, and
   current game time, and `wait` is not an observation-text delivery channel.
4. The NPC's model-facing session-tool guidance matches runtime behaviour: scene updates arrive automatically as
   tool results the NPC did not initiate, fresh scene status arrives with every logical request without waiting,
   `wait` yields for future developments without promising observation text, and the NPC's own `speak` and `wait`
   calls remain in its context as the record of what it said and did.
5. A fresh event gets priority over importance pressure, an attended speaker completion, and timeout. Routine pressure
   does not interrupt active reasoning.
6. A continued utterance cannot cause a response to incomplete text: its context is presented, then awaits confirmation
   before settling.
7. Failed, cancelled, stale, or invalid provider work does not make an NPC forget or falsely confirm scene context.
8. An NPC's working context is a truthful, stable record: its own accepted exchanges and every delivered scene event
   remain visible for the whole session. In this revision the context grows unboundedly within a session; bounding
   that growth is a separately planned revision, not hidden behaviour.
9. An NPC's own committed speech is visible in its context exactly as spoken through its retained `speak` call, and
   cancelled or failed speech leaves no invented memory or character-visible trace.
10. An NPC always knows the current game time when it reasons, carried by current scene status under AI-003 on the same
    clock as its delivered event text.
11. An NPC can read discovered lore before choosing an action; retrieved knowledge remains available to subsequent
    reasoning without creating remembered events or changing the world.
12. The user can configure AgenticMind's reasoning effort and reasoning summary verbosity through the `AI` section of
    the shipped `AlleyCat.yaml`, and override both settings per user through `user://AlleyCat.yaml` under CORE-006's
    merge precedence: reasoning effort accepts `none`, `low`, `medium`, `high`, or `xhigh`, shaping how much
    computation the model invests before answering; reasoning summary verbosity accepts `auto`, `concise`, or
    `detailed`, shaping the detail level of the reasoning summary the provider is asked to return alongside its
    answer — it requests summary detail only and neither requests nor limits reasoning content the provider may
    return separately; and unset settings defer to provider defaults. Neither setting directly controls answer
    length, NPC speech frequency, or tool-use frequency; unchanged model outputs or behaviour are not guaranteed.
    Returned reasoning — a summary, reasoning content the provider returned directly, or both — is the provider's
    own text, surfaced as returned and never fabricated, and never a guarantee of raw or internal chain-of-thought.
13. A blank user override clears a shipped setting, and a configured value governs every provider request of the
    session, including retries and continuations. A misconfigured nonblank value fails locally with a diagnostic
    naming the configuration key and the permitted values.

## Technical Requirements

### Session And Request Context

1. There is one logical session per AgenticMind, ending only on node exit or contained unrecoverable failure. The
   transient provider transcript is never Mind memory: it is derived presentation of Mind's canonical state, and
   watches and time-based tasks read Mind's observation store, never the provider transcript.
2. The static system instruction is rendered once at session start under AI-003. Every logical provider request then
   uses, in order: the static system instruction; the bootstrap user input; every retained accepted exchange in
   chronological order, real and synthetic interleaved; and one fresh user current-scene message rendered last. Each
   request carries exactly one current-scene message, and delivered content is never duplicated elsewhere in the
   request.
3. The request context contains no event-history message and no model-facing newly-presented marking. Model-visible
   event text reaches the model only through retained exchanges: the NPC's own tool calls and results, and the
   synthetic scene-update exchanges of TR-17–TR-22.
4. A logical request materialises its complete context once, appending the pending synthetic delivery exchange
   (TR-18) to the retained transcript before building the frozen request. Exact transport retry reuses that
   materialisation; invalid response recovery and a fresh replacement request rematerialise context over the
   retained transcript, consolidating any observations that became pending since (TR-19). Materialisation must not
   change session-fixed `ScenarioContext` or scenario.
5. Accepting a locally valid provider response atomically appends its valid protocol results and confirms that request
   snapshot's watermark. Failure, cancellation, stale response, and invalid response confirm nothing.
6. `CurrentSceneStatus` is rendered freshly for every logical request under AI-003, as that request's final user
   message. Its inputs include initial attended characters, active watches, and the request snapshot's current game
   time (AI-003 TR-6).

### Scheduling And Wait

7. Scheduling is payload-free and ordered: fresh event, threshold-qualified pressure, attended-speaker completion, then
   timeout. It uses Mind's accepted metadata only.
8. `wait` returns a reason, elapsed game time, and current game time only. It neither returns observation text nor
   advances the delivered watermark or a timeline cursor.
9. Completing or resetting a wait must not clear a timeline cursor. Scheduling pressure outside wait clears only when a
   provider response has been accepted and confirms the corresponding request snapshot.
10. Automatic delivery is payload-free: injected messages and `wait` results must contain no observation text.
      Model-visible observation text appears only in the tool results of synthetic scene-update exchanges.

### Continuation And Failure

11. Continuation sequence `S` has two stages: it may admit a request whose watermark includes `S`, then becomes
    `PresentedAwaitingConfirmation`. A valid accepted response confirms and settles it. Blank, failed, abandoned,
    stale, invalid, or cancelled work releases it without confirmation.
12. Provider responses must carry at least one valid tool call; a response with no tool call is invalid. Responses may
     carry multiple tool calls, validated and executed serially as one batch. Text content alongside tool calls is
     tolerated: the response remains valid, the text is stripped from the retained exchange, and the stripped text is
     recorded in the transcript sink (AI-011). Every response is completely validated before any tool effect; invalid
     responses have no transcript, observation, action, or watermark effect and use bounded recovery; transport retry
     and recovery remain contained.
13. The model-facing tool inventory includes `speak`, `wait`, `read_lore` from AI-004, and authorable watch tools from
     AI-010. The synthetic delivery tool (TR-18) is runtime-authored only: it is not offered to the model, and a model
     attempt to call it is rejected as an unknown tool under existing validation. Tool delegates send any
     resulting observations through Mind's atomic queue. Tool validation and typed tool binding occur only at
     AgenticMind composition; the common runtime has no feature service bag.
14. Speech admitted to its voice pipeline remains committed at playback hand-off and uses Mind's node-lifetime
    exact-once commit identity. Node exit remains terminal and creates no replacement request or synthetic
    confirmation.

### Model-Facing Tool Guidance

15. Model-facing session-tool descriptions must accurately distinguish each tool from automatic delivery (TR-2, TR-3,
      and TR-6): no description may state that waiting is required for new context to arrive or imply that `wait`
      results contain event or observation text (TR-8, TR-10). Because every accepted exchange is retained, no
      description may claim that a `speak` or `wait` exchange leaves no record in the NPC's context: `speak` guidance
      states that the call remains in context as the record of the speech, and `wait` guidance states that its
      exchange persists in context like any other exchange.
16. `wait` must be described as intentionally yielding until future developments or remaining silent, and `speak` as
      taking effect in the world with its call remaining in context as the record of the speech; timing is carried by
      current scene status. Watch-tool descriptions follow AI-010's model-facing watch guidance; shared-instruction
      guidance follows AI-003's Shared Context Interpretation Guidance (TR-12–TR-16).

### Exchange Retention And Scene Delivery

17. Every accepted tool exchange is retained in the provider transcript for the whole session: there is no disposal
    policy and no removal path. Retention changes only transcript presence — whole-batch validation before effects,
    atomic acceptance with watermark confirmation, invalidation arbitration, serial execution, speech admission, and
    terminal cancellation guarantees are unchanged. The transcript therefore grows unboundedly within a session in
    this revision; compaction is deliberately out of scope and motivates a future revision (Out Of Scope).
18. Pending scene observations are delivered by runtime-authored synthetic exchanges. At each request
    materialisation, every event-timeline observation not yet delivered — excluding the NPC's own persisted
    self-speech observation (TR-21) — consolidates into exactly one synthetic exchange: an assistant message
    carrying a single tool call to the delivery tool (working name `scene_update`, unmistakably runtime-authored)
    plus its tool-result message rendering the observations in observation-owned canonical text under AI-001 and
    AI-003, in timeline order, joined with exactly one newline (AI-003 TR-8). When nothing is pending, no
    synthetic exchange is created; the session's
    first materialisation needs no special case and delivers whatever has accumulated. The exact tool name stays
    tunable; the one-exchange consolidation and runtime authorship are the contract. Synthetic call IDs use a
    distinct namespace, and session-wide duplicate-call-ID validation spans real and synthetic exchanges: a consumed
    call ID stays consumed for the whole session.
19. Delivery is marked when the synthetic exchange is appended to the retained transcript and is irrevocable: exact
    transport retries, invalid-response recovery, and fresh replacement requests never redeliver an already-delivered
    observation. Rematerialisation after recovery or replacement consolidates only observations that became pending
    after the previous materialisation, appended as a new synthetic exchange in chronological position.
20. The retained exchange unit is the accepted assistant message's tool calls together with any reasoning content,
    plus the tool-result message; accompanying text content is stripped and recorded (TR-12). Retention is
    whole-exchange, leaving no orphaned tool call or result and no partial removal. The transcript always begins
    with the bootstrap user input, which is never removed.
21. The model's own committed speech reaches it through its retained `speak` call, exactly as accepted. The persisted
    self-speech observation (AI-001) stays in Mind's timeline as canonical memory, is never delivered as a scene
    update, and `I said:` wording is not model-facing.
22. Committed speech is remembered exactly once as the NPC's own event in Mind's timeline (AI-001) in addition to its
    retained `speak` call; speech cancelled or failed before playback hand-off leaves no character-visible trace;
    infrastructure failures stay in runtime diagnostics and are never promoted to delivery, timeline, or scene text.

### Read-Only Lore Tool

23. Compose `read_lore` at AgenticMind with the associated character's trusted observer identity, current content
    context, and existing asynchronous lore query and formatting dependencies. Only canonical lore IDs are model
    inputs: the model cannot supply an observer, content root, or source path. AI-004's Discovery and Canonical-ID
    Retrieval section is normative for validation, ordering, deduplication, unavailable results, isolation,
    cleaning, and cancellation.
24. `read_lore` is read-only and emits no Mind observations or lore mutations. Its successful exchange is retained
    (TR-17), so the next provider request includes the results before a subsequent action decision. Do not add
    a memory subsystem, special scheduling path, or retention override; all exchanges are retained uniformly.
25. Model-facing retrieval guidance explains exact canonical-ID inputs, the backticked presentation of IDs and
    tool names with bare tool-call argument values (AI-004 UR-25/TR-51), explicit unavailable results, and retained
    returned knowledge. Encourage a retrieval request followed by reasoning over the result before choosing a
    relevant action; a later call in the same provider response cannot reason over a result not yet received.
    Retrieval is not mandatory before every action, and `speak`/`wait` guidance remains unchanged.

### Reasoning Effort and Reasoning Summary Configuration

26. `AIOptions` owns two optional settings — `AI:ReasoningEffort` and `AI:ReasoningVerbosity` — bound by the AI
    subsystem from the core-provided `IConfiguration` under CORE-006, whose sourcing, merge precedence, and
    subsystem ownership rules (UR-1–3, TR-2–4, TR-6–7) are normative here. The shipped `res://AlleyCat.yaml` documents
    both settings through comments only, with neither set by default, so unconfigured installations defer entirely to
    provider defaults, and `user://AlleyCat.yaml` overrides take standard merge precedence. No other key aliases these
    settings, and no answer-length control exists.
27. Configured values are trimmed and parsed case-insensitively. Reasoning effort accepts `none`, `low`, `medium`,
    `high`, or `xhigh`; reasoning summary verbosity accepts `auto`, `concise`, or `detailed`. Unset or blank values
    are omitted from provider requests — no corresponding field is sent — and a blank user override clears a shipped
    value. An unrecognised nonblank value is rejected locally, before any provider request, with a diagnostic naming
    the configuration key and the permitted values.
28. Configured values take effect at AI provider/client construction and remain in force for that client's lifetime:
    every provider request made through the configured client is built under them, including exact transport retries
    and continuation requests, on both the Responses and Chat Completions paths, with per-protocol field emission and
    unsupported-setting handling defined by TR-29. An unset setting defers to the provider default by sending no
    field.
29. Emitted request fields follow each endpoint's documented shape and nothing else: Responses carries
    `reasoning.effort` and, when a summary value is configured, `reasoning.summary`; Chat Completions carries
    top-level `reasoning_effort`. Values are emitted in the lowercase vocabulary of TR-27. Chat Completions support
    for a reasoning-summary field is not established: if the endpoint documents an exact equivalent, it is emitted
    with the configured value unchanged; otherwise a configured summary value fails containment locally before
    dispatch with a diagnostic naming the setting and the unsupported path — no invented field, no silent omission,
    and no effect when the summary setting is unset. Neither `text.verbosity` nor Chat Completions top-level
    `verbosity` is ever emitted by these controls. The emission mechanism — typed options, raw-representation
    policy, or patch-style hooks — is an implementation choice, but it must be lossless: the emitted value equals the
    configured value from the TR-27 vocabulary, and remapping it through a coarser intermediary vocabulary is
    not an acceptable substitute.
30. The settings change only these request fields. Stateless Responses configuration (`store=false` with no
    `previous_response_id`), exact retry and recovery semantics, continuation and failure handling, response
    validation, context materialisation, diagnostics, cancellation, and timeouts are unchanged. Forwarding a value is
    not a guarantee of backend support, of summary production, or of any model behaviour: backend rejection of an
    explicitly configured setting follows the existing contained provider-failure behaviour (TR-12) rather than
    silent omission, and no reasoning, speech, or tool-use frequency change is promised or verified.
31. Reasoning summaries returned by the provider are parsed-response data only. When the configured client receives
    a non-empty reasoning summary, it flows through the existing response-content handling and appears in AI-011
    session transcripts under AI-011's parsed-only privacy boundary, its distinctly marked reasoning-content
    rendering, and its per-character invocation index, which are normative here and unchanged. An absent or empty
    reasoning summary is never fabricated, synthesised, or reconstructed from token counts or any other response
    metadata, and no reasoning text is invented where the provider returned none: all transcript and diagnostic
    reasoning text comes only from reasoning the provider itself returned — summary text and recognised reasoning
    content parts (TR-32–TR-33). A returned summary is the provider's rendering of its reasoning — not raw or
    internal chain-of-thought — and no provider is guaranteed to produce summaries or reasoning content for any
    request.

### Returned Reasoning Content

32. Recognised provider-returned reasoning content is first-class reasoning text, not metadata. On the Responses
    path, a reasoning response item's content parts of type `reasoning_text` carry reasoning text the provider
    returned directly, independently of any configured summary setting; the AI-owned provider layer normalises
    recognised content into the parsed reasoning content before any runner handling, diagnostics, or recording. The
    mechanism is an implementation choice — supported SDK access or a narrow AI-owned mapping over the response the
    client already parsed — provided it preserves the underlying response items' identity, protected data such as
    encrypted content, and response order, with no dependency upgrade, protocol switch, request-field change, or
    runtime replay-policy change. It applies whether or not `AI:ReasoningEffort` or `AI:ReasoningVerbosity` is
    configured, and reasoning content keeps its existing place in accepted assistant messages (TR-20), so
    continuation, retention, and replay semantics are unchanged.
33. Parsed reasoning content assembles both returned sources without loss: already-surfaced summary text keeps its
    place, and recognised content parts follow in their original part order, with explicit textual separation
    between the summary text and the content parts and between consecutive parts — the exact separator is an
    implementation choice, provided tests can assert each part's boundary. When the complete recognised content
    text is exactly identical to the already-surfaced summary text, no second copy is added; substring overlap,
    intentionally repeated distinct content parts, and multi-part or multi-item responses are never deduplicated,
    paraphrased, or reordered, and multiple reasoning items keep their response order. Empty or absent parts
    contribute nothing and are never fabricated from token counts, metadata, or encrypted content; summary-only and
    content-only responses are the single-source cases of the same rule.

### Integration Provider Boundary

34. In integration contexts — the existing `RuntimeContext.IsIntegrationTest()` seam — the instance production
    client construction path (`OpenAIClientProvider.CreateChatClient`) fails closed before production settings
    loading or transport construction, with a fixed, secret-free, contained diagnostic. Normal gameplay keeps this
    construction path unchanged. The guard is unconditional inside the integration context: `--live-llm`, test
    selection, and configuration never bypass it. Explicit live test clients use the separate static construction
    path behind TEST-001's `AITest` configuration boundary, and injected offline `ClientProvider` subclasses remain
    unaffected so scripted sessions keep dispatching and completing. TEST-001 owns the framework-side gate,
    configuration boundary, and acceptance coverage this boundary serves.

## In Scope

- One-session lifecycle, stateless provider requests, at-least-one-call response validation with tolerated
  accompanying text, and contained retry/recovery.
- Retained accepted exchanges: whole-exchange retention for the session lifetime, stripped-text recording, and the
  bootstrap and call-ID invariants.
- Runtime-authored synthetic scene-update delivery: one-exchange consolidation of pending observations, the
  irrevocable delivered watermark, the distinct synthetic call-ID namespace, and exclusion from the model-facing
  tool inventory.
- Fresh per-request current-scene status rendered as each request's final user message.
- Canonical observation-owned text for delivered scene updates.
- Payload-free scheduling and wait semantics.
- Accurate model-facing session-tool descriptions for automatic delivery, `wait`, and `speak`.
- Continuation admission and confirmation settlement.
- Composition-time binding of watch tools with AI-010.
- Composition-time trusted binding of AI-004's read-only lore tool and retained retrieval exchanges.
- Optional user-configured reasoning effort and reasoning summary verbosity: AI-owned configuration under CORE-006,
  value normalisation and validation, exact request-field emission per protocol with contained Chat Completions
  unsupported-summary handling, lifetime application across retries and continuations, and parsed-only reasoning
  summary surfacing through AI-011 without fabrication.
- Provider-boundary normalisation of recognised returned reasoning content: summary text, recognised `reasoning_text`
  content parts, or both, surfaced through the same parsed-only AI-011 path in returned order without fabrication or
  duplication (TR-31–TR-33).
- Integration-context fail-closed production client construction (TR-34): the unconditional guard, normal gameplay
  unchanged, and the preserved offline injected-provider and `AITest` live pathways under TEST-001.

## Out Of Scope

- Expressions, additional watch types, planner agent behaviour, and broader perception changes.
- Summarisation-based transcript compaction and any generic retained-result or working-memory channel. In this
  revision the provider transcript grows unboundedly within a session; that accepted limitation is the recorded
  motivation for the future compaction revision, not a hidden behaviour gap. Watch arming and acknowledgement
  feedback stays model-visible per AI-010's current contracts, under the same uniform retention.
- A pull-style `observe` tool or any model-initiated scene-update pathway: delivery stays automatic and
  runtime-authored (TR-18).
- Background-task infrastructure for delivery or summarisation, repetition-case capture, multi-model live-LLM
  sweeps, and production merge of this revision.
- A separate lore working-memory subsystem, and new retry subsystems.
- Final timeout, threshold, and retry tuning values.
- Hot reload, in-game configuration UI, and model switching for these settings; configured values apply at provider
  construction and remain fixed for the client's lifetime.
- Guaranteeing backend or model support for the emitted parameters, that any provider returns reasoning summaries or
  reasoning content at all, or that either exposes raw or internal chain-of-thought; and live-model behaviour
  experiments beyond the approved single-model validation run (Acceptance Criteria). Provider-specific
  reasoning-content behaviour beyond the captured evidence — for example DeepSeek-specific shapes — remains
  unverified. Acceptance verifies emitted requests, contained local failure paths, and parsed-only transcript
  surfacing, not provider outcomes beyond that run.
- New raw-reasoning round-trip features, re-dispatching returned reasoning content to the provider, or changing
  retention, replay, or continuation policy because reasoning text is now surfaced (TR-32–TR-33 preserve them).

## Acceptance Criteria

### User Requirements

1. Acceptance shows each logical request presents the NPC's own accepted exchanges in chronological order, one
   consolidated automatic scene-update exchange for pending observations, and exactly one fresh current-scene
   message rendered last. Selected events use observation-owned canonical text, and automatic delivery supplies no
   observation text through `wait`, injected, or scheduling messages.
2. Acceptance shows fresh events win over pressure, attended-speaker completion, and timeout, while routine pressure
   does not interrupt an active request.
3. Acceptance shows incomplete continuation context is never falsely settled by a failed, blank, abandoned, or invalid
   provider interaction.
4. Acceptance shows each session-tool description accurately distinguishes automatic delivery from deliberate
   waiting, none claims that waiting is required for new context or that wait results carry observation text, and
   `speak`/`wait` descriptions match retained-exchange behaviour — including that a `speak` call remains in context
   as the record of the speech (TR-15, TR-16).
5. Acceptance shows a long session with many completed `speak` and `wait` batches keeps every exchange visible in
   later request contexts as the NPC's own action record, while each delivered scene event is delivered exactly
   once.
6. Acceptance shows committed speech is remembered exactly once as the NPC's own event, appears to the model through
   its retained `speak` call, and neither cancelled nor failed speech leaves a fabricated memory or
   character-visible trace.
7. Acceptance shows the NPC can always read the current game time from its current scene status, on the same clock as
     its event timestamps.
8. A deterministic provider fixture requests lore, consumes its returned bodies on the next reasoning request, and then
    chooses an action without lore retrieval producing remembered events (UR-11).
9. Acceptance shows configured reasoning-effort and reasoning-summary settings govern the session's provider
   requests, including retries and continuations, that unconfigured sessions send neither field, and that a
   misconfigured nonblank value is rejected locally with a diagnostic naming the key and permitted values
   (UR-12, UR-13).
10. With AI-011 session transcripts enabled, a provider-returned reasoning summary appears as distinctly marked
    parsed summary content, provider-returned reasoning content appears as distinctly marked reasoning text in its
    returned order, and a cycle whose response carries neither — even with reasoning token usage present — shows no
    fabricated reasoning text (UR-12).
11. Live-LLM validation through the TEST-001 `AITest` boundary replays the baseline scenario on the approved model:
    within the observation window after a player's line is delivered into model-visible context, an accepted
    response's `speak` content engages with the delivered line's substance; invalid-response discards do not exceed
    the baseline's; and per-turn provider roundtrips do not increase (UR-1, UR-2).

### Technical Requirements

1. Tests verify request order — system instruction, bootstrap input, retained exchanges in chronological order with
   real and synthetic exchanges interleaved, and exactly one fresh current-scene user message rendered last — with
   no event-history message and no `[new]` or other newly-presented marking anywhere model-facing, no duplication of
   delivered content or scene status, and exactly-once context materialisation per logical request, with exact
   retries reusing it and recovery or replacement rematerialising it.
2. Tests verify synthetic delivery: each materialisation with pending observations appends exactly one synthetic
   exchange consolidating all of them; observations render in observation-owned canonical text, in timeline order,
   joined with exactly one newline; a materialisation with nothing pending creates no exchange; the session's first
   materialisation delivers whatever accumulated; the delivery tool is absent from the model-facing inventory and a
   model attempt to call it is rejected as an unknown tool; synthetic call IDs use a distinct namespace; and
   session-wide duplicate-call-ID validation rejects a replayed ID from either namespace.
3. Tests verify the delivered watermark is irrevocable: exact transport retries, invalid-response recovery, and fresh
   replacement requests never redeliver a delivered observation, and rematerialisation consolidates only
   newly pending observations into a new synthetic exchange.
4. Tests verify only locally valid accepted responses atomically confirm a snapshot watermark and clear outside-wait
   pressure; all failure, cancellation, stale, and invalid paths leave both unconfirmed.
5. Tests verify response validation: multiple tool calls in one response are accepted and executed serially as one
   batch; text content alongside tool calls keeps the response valid, is stripped from the retained exchange, and is
   recorded in the transcript sink as a stripped-text record, with synthetic exchanges marked runtime-authored
   (AI-011); a response with no tool call is invalid and uses bounded recovery; and invalid responses have no
   transcript, observation, action, or watermark effect.
6. Tests verify `wait` returns only reason, elapsed time, and current time; wait reset never moves a timeline cursor;
   no observation text occurs in `wait` results or injected messages; model-visible observation text appears only in
   synthetic scene-update tool results; and scheduling signals carry no payload.
7. Tests verify the `S` admission and `PresentedAwaitingConfirmation` stages, including release on every non-confirming
   outcome.
8. Tests verify watch-tool validation and typed binding occur only at AgenticMind composition.
9. Tests verify the `wait` description presents yielding semantics, with no description text contradicting the
   payload-free wait contract, automatic delivery, retained-exchange persistence, or the `speak` call-record
   requirement (TR-15, TR-16).
10. Tests verify retention integrity: every accepted exchange — assistant tool calls, retained reasoning content, and
    the tool-result message — persists in every later request context; accompanying text content does not; no call
    or result is orphaned; and the bootstrap input always remains the transcript's first user message after the
    system instruction.
11. Tests verify committed speech appears to the model through its retained `speak` call, the persisted self-speech
    observation is never delivered as a scene update, no `I said:` wording is model-facing, and pre-hand-off
    cancelled or failed speech leaves no timeline or scene trace.
12. Tests verify every logical request's current scene status carries its snapshot's current game time (AI-003 TR-6).
13. Tests verify `read_lore` binds trusted observer/content dependencies at AgenticMind, exposes only canonical
    lore IDs, and follows AI-004's single/batch, missing-result, formatting, isolation, and cancellation contracts
    (TR-23).
14. Tests verify no retrieval observations, retained results on the next provider request under uniform retention,
    and guidance on retrieval before a subsequent action decision (TR-24–TR-25).
15. Tests load shipped-style and user YAML through the real configuration binding: user values override matching
    shipped settings, omitted keys retain defaults, blank user overrides clear shipped values, and both settings are
    unset by default with the shipped file documenting them through comments only (TR-26).
16. Tests cover trimming and case-insensitive parsing of every permitted value for both settings, omission of unset
    and blank values from requests, and local rejection diagnostics naming the configuration key and permitted values
    for unrecognised nonblank values (TR-27).
17. Captured-request tests exercise the production provider construction path on both Responses and Chat Completions:
    effort fields located and valued exactly (`reasoning.effort`; top-level `reasoning_effort`), a configured summary
    value emitting `reasoning.summary` with the exact configured value on Responses and following TR-29's Chat
    Completions branch — documented-exact emission or contained pre-dispatch failure — on that endpoint, no fields
    emitted when unset, partial configuration emitting only the configured field, and preservation of tools,
    stateless Responses fields, and representative unrelated payload fields; `text.verbosity` and Chat Completions
    top-level `verbosity` are never emitted (TR-29).
18. Tests verify configured settings persist on successive requests through the existing exact-retry and continuation
    seams, and that the settings change no stateless Responses configuration, context materialisation, scheduling, or
    failure and cancellation behaviour (TR-28, TR-30).
19. Tests cover the parsed-summary diagnostic path: a fixture returning a non-empty SDK-parsed reasoning summary
    surfaces that content through AI-011 transcript rendering, and recognised `reasoning_text` content surfaces
    through the same rendering with order, separation, and no duplication (TR-32–TR-33), while an absent or empty
    summary — including a response carrying reasoning token usage without summary or content text — produces no
    fabricated reasoning content from token counts, other metadata, or encrypted content (TR-31).
20. A fixed full-cycle regression exercises the production provider, SDK, runner, and settled AI-011 recorder with
    the captured response shape: provider-returned reasoning content text appears distinctly, in returned order, and
    exactly once in the settled turn without duplication, while the outgoing request keeps its exact configured
    effort and summary values, stateless Responses fields, and tool configuration, and the character index links the
    turn. The regression is proven failing on the uncorrected mapping path and passes unchanged on the corrected
    candidate. The same cycle matrix covers mixed summary-and-content, content-only, summary-only, multi-part
    content, multiple reasoning items in response order, empty or absent parts, and preserved protected data that
    never appears as reasoning text, with existing continuation and cancellation settlement controls unchanged
    (TR-28, TR-30–TR-33).
21. Integration-context tests prove production client construction fails closed before production settings loading
    or transport creation with the contained, secret-free diagnostic across the complete ordinary fixture
    lifecycle, while injected offline providers still construct, dispatch, and complete sessions in the same runs
    (TR-34). Framework-side selection, opt-in, and `AITest` acceptance coverage is TEST-001's normative layer.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-010: Agent Watches](../010-agent-watches/index.md)
- [AI-011: Mind Session Transcripts](../011-mind-session-transcripts/index.md) — parsed-only transcript diagnostics,
  reasoning-content rendering, invocation index, runtime-authored synthetic-exchange marking, and stripped-text
  records normative for reasoning surfacing and tolerated-text recording (TR-12, TR-31, TR-32)
- [CORE-006: Microsoft Configuration Integration](../../core/006-microsoft-configuration-integration/index.md)
- [TEST-001: Integration Test Framework](../../testing/001-test-framework/index.md) — integration-session opt-in
  gate, `AITest` configuration boundary, live-LLM pathway, and framework-side acceptance coverage normative for
  TR-34's testing obligations and the live validation of Acceptance Criteria
