---
id: AI-003
title: Prompt API
domain: AI
status: draft
---

# AI-003: Prompt API

## Requirement

Provide an authorable prompt-composition API that compiles ordered sections and renders a Mind's complete observation
timeline into the sole system instruction for each turn.

## Goal

Keep prompt construction separate from template rendering while giving content authors deterministic, privacy-safe
control over how concrete observation types appear in chronological event history.

## User Requirements

1. Content authors can compose a prompt from ordered, named inline, file-backed, lore, and event-history sections.
2. Content authors can define exact event-history wording for known observation types and mandatory safe wording for
   unknown types, including one actor-relative observed-speech fragment.
3. An NPC's system instruction presents its complete subjective observation history in chronological order on every
   turn.
4. Speech history distinguishes the NPC, a recognised other character, and an unknown speaker without exposing raw
   voice provenance as recognised identity or rendered wording.
5. Shared NPC prompt assets present stable instructions and lore before dynamic event history.
6. Shared action guidance lets an NPC complete chosen actions efficiently in one response when it does not need their
   results, while preserving result-dependent continuation when it does.
7. Foreground prompts include the NPC and all currently resolvable contextual characters meeting its attention
   threshold, without unconditionally including every scene character.

## Technical Requirements

1. Prompt API types must live under `AlleyCat.Mind.AI.Prompting` and use Godot `Resource` types for `PromptStack` and
   prompt sections.
2. `PromptSection` must be abstract, expose a `Name`, and define one public asynchronous content method equivalent to
   `GetContentAsync(PromptSectionBuildContext buildContext, CancellationToken cancellationToken)`.
3. `PromptSectionBuildContext` must contain required `Services`, current `ISceneContext Scene`, and owning
   `ICharacter Character`. It must not contain observations, lore-query state, or template render context.
4. Prompt construction and template rendering must remain separate phases. Runtime observations must be supplied in the
   ordinary top-level dictionary passed to `ITemplate.Render(...)`, under a stable key such as `observations`.
5. `TextPromptSection` and `FilePromptSection` must contribute their authored text through the asynchronous build
   contract without altering content.
6. `PromptStack` must expose an ordered array of `PromptSection` resources and asynchronous compilation through
   build-context services.
7. Compilation must resolve `IPromptWriter` and `ITemplateCompiler`, write `Sections ?? []` in authored order, trim the
   complete source, and return the compiler's `ITemplate`. The stack must not cache rendered output or mutable context.
8. `IPromptWriter` must asynchronously serialise the ordered sections. `PseudoXmlPromptWriter` must remain the default
   startup-registered implementation and delegate section content generation to each section.
9. `PseudoXmlPromptWriter` must reject null collections, null entries, and blank section names with clear authoring
   errors. It must wrap content in matching authored-name tags, replacing only `<`, `>`, and `/` with `_` in tag names.
10. Prompt content, including slashes and multiline output, must otherwise remain unchanged. Formatting must remain in
    `IPromptWriter`, not `PromptStack`.
11. The API must reuse `AlleyCat.Templating.ITemplate` and `ITemplateCompiler` rather than define competing
    abstractions.
12. `EventHistoryPromptSection` must own:
    - an exported ordered array of authored fragments;
    - one exact, case-sensitive `TypeKey` per fragment; and
    - a mandatory authored fallback template.
13. Event-history authoring must fail clearly for a blank key, duplicate exact key, or missing or blank fallback.
14. Event history must dispatch each concrete observation by exact `TypeKey` within the compiled prompt. It must not use
    global mutable partial registration, an observation visitor, or observation-owned formatting.
15. Each observation record from the timeline snapshot must pass directly to Handlebars as the current context when its
    selected fragment renders. This must preserve the record's prompt-visible properties. Unknown concrete observations
    must render the fallback with the same record data.
16. The fallback must keep terse event wording equivalent to `((Received {{TypeKey}} event.))` and may append the same
    relative-time label; it must still never render raw voice provenance.
17. The shared prompt stack must use exactly one actor-relative fragment for `ObservedSpeech`, selected by the exact
    `speech.observed` key. Separate heard-speech and self-spoken fragments or semantic keys must not be authored.
18. The observed-speech fragment must compare `ObservedAction.ActorId` with the owning `ICharacter.FullId` to render
    owning-character speech as self speech, a recognised other actor by character identity, and an absent or unknown
    actor with privacy-safe wording.
19. Raw `VoiceId` provenance must never be rendered by the observed-speech fragment or used as fallback identity
    wording.
20. AgenticMind must compile the foreground `PromptStack` on every turn, call `CreateRenderContext`, and render the
    template with the exact top-level read-only dictionary returned. The complete context includes current character
    context, the player character's context under [SCN-001](../../scene/001-scene-context-api/index.md) — mandatory
    and unconditional, resolved via `ISceneContext.Player`, never attention-gated — deterministic attention-eligible
    character context under AI-006, which may omit the player, the complete unbounded observation timeline, the
    current scenario under [AI-008](../008-scenario/index.md), and authored worker projections.
21. AgenticMind must atomically publish that exact dictionary as its latest render snapshot only after template
    rendering succeeds. Context construction or rendering failure must retain the previously published snapshot.
22. The rendered stack must become the turn's sole system instruction under AI-002. No observation-summary user message
    or prior-turn transcript may supplement it.
23. Static instructions and lore should precede dynamic `EventHistoryPromptSection` in authored shared assets. This is
    an authoring policy verified for the shared NPC assets, not a generic runtime ordering restriction or type-system
    rule.
24. Male and female NPC role templates must reference one shared generic prompt stack containing context-driven
    identity, tool-only action and `end_turn` guidance, essential lore, character lore, then event history.
25. Shared action guidance must permit zero, one, or multiple actions, make `speak` optional and non-terminal, and
    identify `end_turn` as the reserved non-action protocol marker. It must instruct the model to place `end_turn`
    exactly once as the final call, either alone for zero actions or after one or more production actions when the turn
    can finish without inspecting their results.
26. Shared action guidance must instruct the model to omit `end_turn` from an action-only response when it needs action
    results before deciding whether to continue or finish. It must not request ordinary assistant text or a terminal
    response schema.
27. AI-005 LLM-backed ContextWorkers own separate immutable lifetime `PromptStack` references. After AgenticMind
    attachment, each worker compiles its captured stack once, caches the result for its lifetime, and renders it per
    request with the latest foreground-published snapshot captured at run start. It must not request context
    construction or aggregation, invalidate the cache, or recompile it. A compile failure logs once,
    invokes no provider, and leaves the worker inactive with its prior projection as fallback. Typed schema responses
     map to worker dictionary output for direct publication under AI-005; they are not foreground-turn output and must
     not alter the foreground sole-system-instruction or tool-only contracts.
28. AgenticMind owns provider, prompt compilation, render-context construction and publication, and tool orchestration.
    It must consume Mind's committed observations and attention eligibility without interpreting incoming percepts,
    subscribing to senses, or owning perception faculties.
29. Each `Observation` record in the timeline snapshot exposes a UTC `ObservedAt` timestamp stamped exactly once at
    ingestion by the owning Mind. The timestamp is a prompt-visible record property, nullable when the record was not
    ingested through Mind.
30. Event-history entries may render a relative-time label through the built-in `ago` template tool under TMPL-001,
    authored as `({{ago ObservedAt}})` guarded by `{{#if ObservedAt}}` so unstamped records render without a label. The
    label must not leak voice provenance or other private payloads.

## In Scope

- Ordered prompt composition and asynchronous section building.
- Separate prompt compilation and ordinary-context rendering phases.
- Exact keyed event-history fragments, direct record rendering, and mandatory fallback rendering.
- One actor-relative `speech.observed` fragment for every observed-speech perspective.
- Foreground-only `CreateRenderContext`, exact-context rendering, and success-only snapshot publication.
- AI-006 attention-filtered character context without prompt-owned scanning or attention policy.
- Shared generic NPC prompt-stack authoring and static-before-dynamic asset policy.
- Tool-only action, result-dependent continuation, and final `end_turn` guidance aligned with AI-002.
- Default pseudo-XML prompt writer and existing templating-system integration.
- Immutable PromptStack lifetime and compilation-cache boundary for AI-005 LLM-backed ContextWorkers.
- AgenticMind's prompt/render/tool boundary from AI-006, excluding incoming percept interpretation.

## Out Of Scope

- Timeline summarisation, compaction, token budgeting, or persistence beyond node lifetime.
- Alternative prompt writers beyond the default pseudo-XML writer.
- New template compilers, localisation workflows, and editor preview tooling.
- Global mutable event-fragment registration.
- Static-versus-dynamic prompt-section enforcement in the type system.
- Detailed lore querying and retrieval behaviour, which is specified by AI-004.

## Acceptance Criteria

1. Authors can create ordered prompt stacks containing inline, file-backed, lore, and event-history sections.
2. Prompt build tests verify typed build context, asynchronous authored-order writing, trimming, service resolution, and
   compiler delegation without placing observations or render context in `PromptSectionBuildContext`.
3. Writer tests verify matching pseudo-XML tags, existing lax authored names, replacement of only `<`, `>`, and `/` in
   tag names, exact content preservation, and clear invalid-authoring failures.
4. Every AgenticMind foreground turn compiles its prompt stack, calls `CreateRenderContext`, and renders with its exact
   top-level read-only dictionary: `character`, a mandatory unconditional `player` character context under SCN-001,
   deterministic attention-eligible `characters`, which may omit the player, complete read-only `observations`, the
   current `scenario` under AI-008, and authored worker projections. Publication occurs atomically only after
   rendering succeeds; construction or rendering failure retains the previous published dictionary.
5. Capturing-client tests verify the rendered stack is the sole system instruction and no observation-summary user
   message or prior transcript accompanies it.
6. Event-history tests cover self speech, recognised-other speech, unknown speech, empty history, chronological
   ordering, and multiline fragment output through one exact `speech.observed` fragment.
7. Event-history tests verify exact case-sensitive dispatch, clear blank and duplicate key failures, and mandatory
   nonblank fallback authoring.
8. Event-history tests verify exact `TypeKey` dispatch and pass each timeline observation record directly to Handlebars
   as the fragment context, preserving record property visibility. Unknown concrete observations render the fallback
   with the same record data, without reflection property projection, global mutable partials, a visitor, or
   observation-owned text rendering.
9. Observed-speech rendering compares `ActorId` with the owning character and never renders raw `VoiceId` provenance as
   wording or proof of identity.
10. Male and female NPC role templates use one shared prompt stack whose static instructions and lore precede event
    history; asset tests enforce this policy without imposing a generic runtime ordering restriction.
11. Prompt guidance permits zero, one, or multiple actions; makes `speak` optional and non-terminal; and permits `speak`
    followed by final `end_turn` in one response. It instructs sole-marker use for zero actions, final-marker use after
    completed action plans, and marker omission when action results are needed for continuation, without requesting
    ordinary assistant text or a terminal response schema.
12. Tests verify action-only guidance aligns with AI-002 result replay and a later model request, while final-marker
    guidance completes the turn without either.
13. Acceptance verifies both author-visible composition behaviour and the compilation, actor-relative rendering,
    privacy, ordering, and runtime integration contracts.
14. Tests verify an AI-005 LLM worker captures an immutable lifetime PromptStack reference, compiles it once after
    AgenticMind attachment, and caches it without invalidation or recompilation. Each run renders with the published
    snapshot captured at run start without requesting context construction or aggregation. A compile failure logs once,
    invokes no provider, preserves the prior projection, and does not change foreground prompt or tool-only contracts.
15. Tests verify foreground rendering includes self and every currently resolvable attention-eligible contextual
    character, omits ineligible or unresolved subjects, and does not trigger a second scan or prompt-owned attention
    update.
16. Tests verify AgenticMind uses Mind-owned observations and attention eligibility without sense subscriptions,
    percept-type dispatch, perception faculties, or incoming sensory interpretation.
17. Event-history tests verify relative-time labels render for stamped observations through the `ago` tool, and
    unstamped observations render no label, without changing exact TypeKey dispatch, chronological ordering, privacy,
    or the exact render-context dictionary contract (no new top-level key).
18. Tests verify the prompt API adds no top-level `now` key to the render context; the `ago` helper defaults to UTC now
    at render time.

## References

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [AI-005: Context Worker](../005-context-worker/index.md)
- [AI-006: Percept-Based Sensing And Attention](../006-character-perception-and-attention/index.md)
- [AI-008: Scenario](../008-scenario/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [AI System](../index.md)

### Implementation

- `game/src/Mind/AI/Prompting/`
- `game/src/Templating/ITemplate.cs`
- `game/src/Templating/ITemplateCompiler.cs`
- `game/assets/characters/prompts/generic_npc_prompt_stack.tres`
