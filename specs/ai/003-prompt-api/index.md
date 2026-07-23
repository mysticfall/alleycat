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
15. Each observation's concrete record must be the current Handlebars context when its selected fragment renders.
    Unknown concrete observations must render the fallback and remain available to it as concrete records.
16. The fallback must use terse event wording equivalent to `((Received {{TypeKey}} event.))` and must not render raw
    voice provenance.
17. The shared prompt stack must use exactly one actor-relative fragment for `ObservedSpeech`, selected by the exact
    `speech.observed` key. Separate heard-speech and self-spoken fragments or semantic keys must not be authored.
18. The observed-speech fragment must compare `ObservedAction.ActorId` with the owning `ICharacter.Id` to render
    owning-character speech as self speech, a recognised other actor by character identity, and an absent or unknown
    actor with privacy-safe wording.
19. Raw `VoiceId` provenance must never be rendered by the observed-speech fragment or used as fallback identity
    wording.
20. AgenticMind must compile and render the existing `PromptStack` on every turn with current scene and character
    context plus an immutable snapshot of the complete, unbounded observation timeline.
21. The rendered stack must become the turn's sole system instruction under AI-002. No observation-summary user message
    or prior-turn transcript may supplement it.
22. Static instructions and lore should precede dynamic `EventHistoryPromptSection` in authored shared assets. This is
    an authoring policy verified for the shared NPC assets, not a generic runtime ordering restriction or type-system
    rule.
23. Male and female NPC role templates must reference one shared generic prompt stack containing context-driven
    identity, tool-only action and `end_turn` guidance, essential lore, character lore, then event history.
24. Shared action guidance must permit zero, one, or multiple actions, make `speak` optional and non-terminal, and
    identify `end_turn` as the reserved non-action protocol marker. It must instruct the model to place `end_turn`
    exactly once as the final call, either alone for zero actions or after one or more production actions when the turn
    can finish without inspecting their results.
25. Shared action guidance must instruct the model to omit `end_turn` from an action-only response when it needs action
    results before deciding whether to continue or finish. It must not request ordinary assistant text or a terminal
    response schema.

## In Scope

- Ordered prompt composition and asynchronous section building.
- Separate prompt compilation and ordinary-context rendering phases.
- Exact keyed event-history fragments and mandatory fallback rendering.
- One actor-relative `speech.observed` fragment for every observed-speech perspective.
- Complete per-turn observation-timeline rendering.
- Shared generic NPC prompt-stack authoring and static-before-dynamic asset policy.
- Tool-only action, result-dependent continuation, and final `end_turn` guidance aligned with AI-002.
- Default pseudo-XML prompt writer and existing templating-system integration.

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
4. Every AgenticMind turn compiles and renders its prompt stack with current character and scene context and the
   complete immutable timeline in ordinary render context.
5. Capturing-client tests verify the rendered stack is the sole system instruction and no observation-summary user
   message or prior transcript accompanies it.
6. Event-history tests cover self speech, recognised-other speech, unknown speech, empty history, chronological
   ordering, and multiline fragment output through one exact `speech.observed` fragment.
7. Event-history tests verify exact case-sensitive dispatch, clear blank and duplicate key failures, and mandatory
   nonblank fallback authoring.
8. An unknown concrete observation renders the fallback with its record as context, without global mutable partials,
   reflection-based projection, a visitor, or observation-owned text rendering.
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

## References

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [AI System](../index.md)

### Implementation

- `game/src/Mind/AI/Prompting/`
- `game/src/Templating/ITemplate.cs`
- `game/src/Templating/ITemplateCompiler.cs`
- `game/assets/characters/prompts/generic_npc_prompt_stack.tres`
