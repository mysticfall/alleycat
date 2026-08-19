---
id: AI-003
title: Prompt API
domain: AI
status: draft
---

# AI-003: Prompt API

## Requirement

Provide an authorable prompt-composition API that compiles ordered sections into the sole system instruction of an
NPC's agent session, and an authorable event-history contract that renders observation records on demand for tool
results and interruption injections.

## Goal

Keep prompt construction separate from template rendering while giving content authors deterministic, privacy-safe
control over how concrete observation types appear in chronological event history.

## User Requirements

1. Content authors can compose a prompt from ordered, named inline, file-backed, and lore sections.
2. Content authors can define exact event-history wording for known observation types and mandatory safe wording for
   unknown types, including one actor-relative observed-speech fragment.
3. An NPC receives its observation history through on-demand renderings — `wait` results, timeline history (`history`)
   tool results, and interruption injections — presented in chronological order with authored wording, rather than
   through the session system instruction.
4. Speech history distinguishes the NPC, a recognised other character, and an unknown speaker without exposing raw
   voice provenance as recognised identity or rendered wording.
5. Shared NPC prompt assets keep the session prompt cross-cutting — identity, session frame, and lore — without
   per-tool mechanics or observation history.
6. Shared guidance teaches the NPC only cross-cutting session conduct: act through tools, and read the game-time
   seconds carried by tool results. Per-tool mechanics and etiquette — using `wait` to observe the scene rather than
   to pass time, and waiting a reasonable duration after asking another character a question before assuming refusal
   and reacting — are carried by the respective tool descriptions.
7. The session prompt includes the NPC and all currently resolvable contextual characters meeting its attention
   threshold, without unconditionally including every scene character.

## Technical Requirements

1. Prompt API types must live under `AlleyCat.Mind.AI.Prompting` and use Godot `Resource` types for `PromptStack` and
   prompt sections.
2. `PromptSection` must be abstract, expose a `Name`, and define one public asynchronous content method equivalent to
   `GetContentAsync(PromptSectionBuildContext buildContext, CancellationToken cancellationToken)`.
3. `PromptSectionBuildContext` must contain required `Services`, current `ISceneContext Scene`, and owning
   `ICharacter Character`. It must not contain observations, lore-query state, or template render context.
4. Prompt construction and template rendering must remain separate phases. Runtime observation records must be
   supplied only at render time — never compiled into section content — and must never enter the session-start render
   dictionary: observations reach the model exclusively through AI-002 tool results and interruption injections.
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
12. Event history must be authored as the standalone `EventHistory` resource — no longer a `PromptSection` and not
    part of the session-start prompt stack — exported by `AgenticMind` (for example, `[Export] EventHistory?`). It must
    own:
    - an exported ordered array of authored fragments;
    - one exact, case-sensitive `TypeKey` per fragment; and
    - a mandatory authored fallback template.
    Its fragments and fallback feed the on-demand `ObservationHistoryRenderer`, which renders observation records for
    AI-002 `wait` results, timeline history (`history`) tool results, and interruption injections.
13. Event-history authoring must fail clearly for a blank key, duplicate exact key, or missing or blank fallback.
14. Event history must dispatch each concrete observation by exact `TypeKey` in every rendered history. It must not use
    global mutable partial registration, an observation visitor, or observation-owned formatting.
15. Each observation record from the timeline snapshot must pass directly to Handlebars as the current context when its
    selected fragment renders. This must preserve the record's fragment-visible properties. Unknown concrete
    observations must render the fallback with the same record data.
16. The fallback must keep terse event wording equivalent to `((Received {{TypeKey}} event.))` and may append the same
    game-time label; it must still never render raw voice provenance.
17. The shared `EventHistory` resource must use exactly one actor-relative fragment for `ObservedSpeech`, selected by
    the exact `speech.observed` key. Separate heard-speech and self-spoken fragments or semantic keys must not be
    authored.
18. The observed-speech fragment must compare `ObservedAction.ActorId` with the owning `ICharacter.FullId` to render
    owning-character speech as self speech, a recognised other actor by character identity, and an absent or unknown
    actor with privacy-safe wording.
19. Raw `VoiceId` provenance must never be rendered by the observed-speech fragment or used as fallback identity
    wording.
20. Exactly once per agent session, at session start, AgenticMind must assemble the render context on demand, compile
     the configured `PromptStack`, and render the template with the exact top-level read-only dictionary returned. The
     complete context includes current character context, the player character's context under
     [SCN-001](../../scene/001-scene-context-api/index.md) — mandatory and unconditional, resolved via
     `ISceneContext.Player`, never attention-gated — deterministic attention-eligible character context under AI-006,
     which may omit the player, and the current scenario under [AI-008](../008-scenario/index.md). The dictionary
     defines no `observations` key.
21. The session prompt must render with the exact dictionary returned by `CreateRenderContext`; nothing is re-rendered,
     refreshed, or frozen later in the session. Mid-session observation access flows through the AI-002 `wait` and
     timeline history paths rather than through re-rendered prompts.
22. The rendered stack must become the session's sole system instruction under AI-002. No observation-summary user
     message or re-rendered instruction may supplement it.
23. The shared generic NPC prompt stack must not contain an event-history section. Event-history authoring lives in
     the standalone `EventHistory` resource exported by `AgenticMind` (TR-12); the stack carries only static guidance
     and lore.
24. Male and female NPC role templates must reference one shared generic prompt stack containing the `mind.md` file
     section — context-driven identity and the tool-call-only frame, game-time literacy, and subject references —
     essential lore, character lore, and the scenario section ([AI-008](../008-scenario/index.md)).
25. Shared session guidance must cover only cross-cutting material: identity and the tool-call-only frame, game-time
     timestamp literacy — tool results report game-time seconds since the game began — and subject references. It must
     not request ordinary assistant text or a terminal response schema. Per-tool mechanics and etiquette live solely in
     the respective tool descriptions (`ToolDescription` exports): `speak` optional and never terminal; `wait` framed
     as the way to observe the scene — the NPC receives no updates about important scene events without it — rather
     than a way to pass time, including question-then-wait etiquette; and memory recall through the timeline history
     tool.
26. AI-002 is normative for the tool inventory and the tool-only session protocol; the shared guidance here must stay
     aligned with it.
27. AgenticMind owns provider, prompt compilation, render-context construction, and tool orchestration. It must
     consume Mind's committed observations and attention eligibility without interpreting incoming percepts,
     subscribing to senses, or owning perception faculties.
28. Each `Observation` record in the timeline snapshot exposes an `ObservedAt` timestamp in game-time seconds from
     the game-scoped game-time source (AI-002), stamped exactly once at ingestion by the owning Mind. The timestamp is a
     fragment-visible record property, nullable when the record was not ingested through Mind.
29. Event-history entries may render an absolute game-time label derived from the record's `ObservedAt` game-time
    seconds, conventionally guarded by `{{#if ObservedAt}}` in authored fragments so unstamped records render without
    a label. Relative-time labels are not available to authored fragments: deriving one would require either a `now`
    top-level context key (forbidden by AC-17) or a game-time-aware extension of TMPL-001's `ago` helper (out of
    scope; see [TMPL-001](../../templating/001-templating-system/index.md)). The label must not leak voice provenance
    or other private payloads.

## In Scope

- Ordered prompt composition and asynchronous section building.
- Separate prompt compilation and ordinary-context rendering phases.
- Exact keyed event-history fragments, direct record rendering, and mandatory fallback rendering.
- One actor-relative `speech.observed` fragment for every observed-speech perspective.
- Session-start `CreateRenderContext` assembly and exact-context rendering for the session system instruction.
- Shared generic NPC prompt-stack authoring: `mind.md` file section, lore, and scenario, with no event-history
  section in the stack.
- AI-006 attention-filtered character context without prompt-owned scanning or attention policy.
- Cross-cutting session guidance aligned with AI-002: tool-call-only frame and game-time timestamp literacy, with
  per-tool mechanics and etiquette carried by tool descriptions.
- On-demand event-history rendering through the standalone `EventHistory` resource for AI-002 `wait` results,
  timeline history tool results, and interruption injections.
- Default pseudo-XML prompt writer and existing templating-system integration.
- AgenticMind's prompt/render/tool boundary from AI-006, excluding incoming percept interpretation.

## Out Of Scope

- Timeline summarisation, compaction, token budgeting, or persistence beyond node lifetime.
- Alternative prompt writers beyond the default pseudo-XML writer.
- New template compilers, localisation workflows, and editor preview tooling.
- Global mutable event-fragment registration.
- Static-versus-dynamic prompt-section enforcement in the type system.
- Detailed lore querying and retrieval behaviour, which is specified by AI-004.

## Acceptance Criteria

1. Authors can create ordered prompt stacks containing inline, file-backed, and lore sections; the shared generic NPC
   stack contains no event-history section.
2. Prompt build tests verify typed build context, asynchronous authored-order writing, trimming, service resolution, and
   compiler delegation without placing observations or render context in `PromptSectionBuildContext`.
3. Writer tests verify matching pseudo-XML tags, existing lax authored names, replacement of only `<`, `>`, and `/` in
   tag names, exact content preservation, and clear invalid-authoring failures.
4. Exactly once per agent session, AgenticMind assembles the render context on demand, compiles its prompt stack, and
   renders with its exact top-level read-only dictionary: `character`, a mandatory unconditional `player` character
   context under SCN-001, deterministic attention-eligible `characters`, which may omit the player, and the current
   `scenario` under AI-008. The dictionary defines no `observations` key. No later request re-renders or supplements
   the instruction.
5. Capturing-client tests verify the rendered stack is the session's sole system instruction and no observation-summary
   user message or re-rendered instruction accompanies it.
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
10. Male and female NPC role templates use one shared prompt stack containing the `mind.md` file section, lore, and
    scenario; asset tests verify the stack contains no event-history section and that event history is authored as the
    standalone `EventHistory` resource exported by AgenticMind.
11. Tests verify shared session guidance covers only cross-cutting material — identity and the tool-call-only frame,
    game-time timestamp literacy, subject references — without requesting ordinary assistant text or a terminal
    response schema, and that per-tool framing is carried by tool descriptions: `speak` optional and never terminal;
    `wait` as scene observation rather than passing time, including question-then-wait etiquette before assuming
    refusal; and history recall.
12. Tests verify the session guidance stays aligned with the AI-002 tool inventory and tool-only session protocol.
13. Acceptance verifies both author-visible composition behaviour and the compilation, actor-relative rendering,
     privacy, ordering, and runtime integration contracts.
14. Tests verify session rendering includes self and every currently resolvable attention-eligible contextual
     character, omits ineligible or unresolved subjects, and does not trigger a second scan or prompt-owned attention
     update.
15. Tests verify AgenticMind uses Mind-owned observations and attention eligibility without sense subscriptions,
     percept-type dispatch, perception faculties, or incoming sensory interpretation.
16. Event-history tests verify absolute game-time labels render for stamped observations from `ObservedAt` game-time
    seconds, and unstamped observations render no label, without changing exact TypeKey dispatch, chronological
    ordering, privacy, or the exact render-context dictionary contract (no new top-level key).
17. Tests verify the prompt API adds no top-level `now` key to the render context; absolute labels derive from the
     game-scoped game-time source.

## References

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-004: Lore And Backstory Source Compilation](../004-lore-backstory/index.md)
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
