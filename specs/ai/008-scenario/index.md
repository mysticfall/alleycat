---
id: AI-008
title: Scenario
---

# Scenario

## Requirement

 The system must provide a character-bound scenario that supplies the objectives and additional context framing an
 NPC's current interactions, and must own the trusted session-context binding that carries the owning character, the
 session-captured scene snapshot, and the current scenario into prompt rendering and tool invocation.

## Goal

 Let contributors steer what an NPC is currently trying to achieve without confusing narrative context with physical
 scene membership, while keeping scenario resolution a single session-start step, the session binding trusted, and
 rendering inside the existing shared prompt stack.

## User Requirements

1. Contributors can author a scenario for an NPC so that the NPC's behaviour pursues authored objectives and
   additional interaction context instead of relying only on ambient observation history.
2. An NPC with no configured scenario manager, and a session for which the manager supplies no scenario, must behave
   exactly like an NPC without the scenario feature: absence yields empty prompt content, never a failed session or a
   stale carry-over.
3. A scenario applies to one character's current interactions only; scenario authoring must not alter scene
   membership, other characters, or physical world state.
4. Scenario text reaches the prompt as plain authored narrative; it is never executed as a template at render time and
   never generated or rewritten by the model.
5. Scenario resolution failures must be contained and logged like other session failures, without crashing the scene,
   retrying the session, or corrupting the observation timeline.

## Technical Requirements

1. A scenario is a character-bound narrative and interaction context: the objectives and additional context for what
   is currently happening to that character. It is deliberately distinct from SCN-001 `ISceneContext`, which remains
   the physical scene membership snapshot of what exists in the Godot scene tree. There is no is-a relationship in
   either direction: `ScenarioContext`
   composes an `ISceneContext`
   by reference, and neither type inherits from the other. AI-008's scenario types must live under `AlleyCat.Mind.AI`.
2. `Scenario`
   must be a plain C# record, not a Godot `Resource`. Instances are created at runtime by manager implementations and
   must never be Godot-serialised. Its exact public surface is one `string Description`
   property. The stored `Description`
   is plain composed text; it must never be re-evaluated as a template at prompt render time, while manager-side
   composition at `Scenario`-creation time is sanctioned (TR-4).
3. `IScenarioManager`
   must define exactly `Scenario? GetCurrentScenario(IReadOnlyDictionary<string, object?> coreContext)`, where
   `coreContext`
   is the phase-1 core render context (TR-7) and a null return means no scenario is available. `ScenarioContext`
   remains the pure session binding; the core context is passed per invocation and must not become part of the binding.
4. `FixedScenarioManager` must be the first manager implementation: a `[GlobalClass]` Godot `Resource`
   carrying an authored `DescriptionPath` string — a `res://`
   path, exported with a Godot file hint, to the scenario description document. The document location is authored per
   manager instance. `GetCurrentScenario`
   must load the referenced file and strip any leading well-formed front-matter block — `---`-delimited per the lore
   corpus convention, with structural front-matter parsing performed by the Markdig Markdown parsing library —
   treating the remainder as the body; a file without a leading block uses its full content. The body may use the
   TMPL-001 templating vocabulary and is compiled through the existing `ITemplateCompiler`
   and rendered exactly once at `Scenario`-creation time against the phase-1 core render context received from the
   manager query. Scenario body templates bind phase-1 keys only; the `scenario`
    key is not available to the body — no self-reference and no phase-2 keys (TR-12). Identity tokens such as
   `{{player.FullId}}`
   and `{{character.FullId}}` resolve to the raw canonical `FullId`
   values published in the corresponding core-context dictionaries, for example `char:ally`. A token-free body passes
   through unchanged, and the composed body is otherwise preserved exactly with no trimming. A missing file, an
   unreadable file, a blank body after stripping, or a template compilation or render failure is invalid authoring and
   must fail clearly through the TR-9 containment path, naming the offending document path. Scenario documents
   reference the participating characters through FullId identity tokens rather than hardcoded names.
5. `ScenarioContext`
   must remain the trusted, immutable session binding. Its exact public surface is `ICharacter Character`,
   `ISceneContext SceneContext`, and `Scenario? Scenario`.
6. `ScenarioContext`
   inherits every AI-002 trusted-context guarantee: it must be excluded from the model-visible tool schema; it must
   not be supplied, replaced, or overridden by model arguments; it must not implement or expose `IServiceProvider`
   or act as a general service bag; Mind and `IMainThreadDispatcher` remain private to the common `AgentTool`
   wrapper; and character-ownership verification before dispatcher submission is unchanged.
7. Exactly once, at session start, AgenticMind must construct the render context in two phases. It first captures a
   fresh SCN-001 scene snapshot and builds the phase-1 core render context — everything except `scenario`: current
   character context, the unconditional player context, and deterministic attention-eligible subject context. It then
   queries the configured manager with the freshly assembled core context, adopts
   the returned `Scenario?`
   as the session's scenario, constructs the `ScenarioContext`
   from the owning character, the captured snapshot, and that scenario, and adds the `scenario`
   key to complete the dictionary, which is rendered as the session system instruction and whose `ScenarioContext`
   binding serves both the prompt render and every tool invocation of the session.
8. The current scenario is session state. Once resolved at session start, it is retained unchanged for the complete
   session; there is no per-request re-query and no mid-session refresh. When the manager returns null, the session's
   scenario reference is null.
9. Manager failure must follow AI-002's session-start containment path: the session does not start, the failure is
   logged and contained, and no retry or repair is attempted. When no manager is configured, behaviour must be
   identical to a null-returning manager.
10. The session render dictionary must gain the top-level key `scenario`
    — the phase-2 addition after the manager query (TR-7) — whose value is the session's `Scenario`
     record or null. The key is reserved exactly like `character`, `characters`, and `player`: an
     authored entry colliding with `scenario`
    must fail with the existing duplicate-key error. AI-001 TR-25 and AI-003 TR-20 enumerate this key in their
    composition lists; AI-008 is normative for its value and reservation semantics.
11. The scenario must be rendered by a plain `FilePromptSection`
    in the shared generic NPC prompt stack referencing `res://prompts/scenario.md`, authored with a `{{#if scenario}}`
    guard rendering `{{scenario.Description}}`
    with PascalCase property access matching existing fragment conventions. There is deliberately no new `PromptSection`
    type, no `IsEnabled`
    machinery, and no writer skip behaviour: absence is handled inside `scenario.md`, and the section's pseudo-XML tag
    pair appearing in the prompt with empty content when the scenario is null is an accepted quirk.
12. Phase-key availability: the phase-1 core context contains exactly `character`
    — the owning character's context dictionary, mandatory and always present; `player`
    — the player's context dictionary, mandatory and unconditional, resolved via the session-captured
    `ISceneContext.Player`
    (SCN-001) and never attention-gated; and `characters`
    — attention-gated subject context dictionaries, which may omit the player. Phase 2 adds
    `scenario`
    — the `Scenario`
    record or null — after the manager query and before the complete dictionary is rendered as the session system
    instruction. Scenario body templates bind phase-1 keys only; the `scenario`
    key is absent from the core context handed to the manager and must not appear in a scenario body.

## In Scope

- The `Scenario` record, the `IScenarioManager` manager contract, and the `FixedScenarioManager` first implementation.
- `ScenarioContext` as the trusted immutable session binding for AI-002 prompt rendering and tool invocation.
- AgenticMind session-start two-phase scenario resolution: fresh scene snapshot, phase-1 core context construction,
  null handling, and containment of manager failure and unconfigured managers.
- Session-fixed scenario state for the complete session.
- Phase-key availability for scenario body templates: phase-1 core-context keys, with the phase-2 `scenario`
  key excluded from body binding.
- The reserved top-level `scenario` render-dictionary key with its record-or-null value.
- The shared generic NPC prompt stack scenario `FilePromptSection` and `res://prompts/scenario.md`
  authoring, including the accepted null-tag quirk.

## Out Of Scope

- Event or observation history inside `ScenarioContext`; rendered event history remains above this layer and is
  deferred to a separate future session.
- Scenario-template extensibility beyond the fixed `FixedScenarioManager`
  document contract over the phase-1 core context (TR-4); managers needing other dynamic text must compose the string
  themselves at `Scenario`-creation time.
- Scenario progression, state machines, or manager implementations beyond `FixedScenarioManager`.
- Multiple concurrent scenarios per character.
- Non-AI consumers of scenarios.
- New production tools.

## Acceptance Criteria

### User Requirements

1. Integration and play coverage show an NPC with an authored fixed scenario pursues that scenario's objectives and
   context in its behaviour, while an NPC without a configured manager behaves as it did before the feature.
2. Coverage shows a session with no scenario renders empty scenario content — the accepted tag-pair quirk aside —
   without failing the session or carrying a scenario from any earlier source.
3. Coverage shows scenario authoring changes only the NPC's prompt context: scene membership, other characters, and
   world state are untouched.
4. Coverage shows a manager failure is contained and logged without a scene crash, session retry, or
   observation-timeline corruption.

### Technical Requirements

1. Contract tests verify `Scenario` is a plain C# record with exactly one `string Description`
   property, is not a Godot `Resource`, and is created only by manager implementations.
2. Tests verify `IScenarioManager`
   exposes exactly `Scenario? GetCurrentScenario(IReadOnlyDictionary<string, object?> coreContext)`
   and that `FixedScenarioManager` is a `[GlobalClass]` `Resource` returning a `Scenario` whose `Description`
   is exactly its authored `DescriptionPath`
   document's composed body on every call — front matter never reaches `Description`, a token-bearing body yields
   exactly the text substituted against the production-shaped phase-1 core context, and a token-free body passes
   through unchanged — while a missing file, unreadable file, blank body after stripping, or compilation or render
   failure fails clearly, naming the document path.
3. Contract tests verify `ScenarioContext`'s exact public surface — `ICharacter Character`,
   `ISceneContext SceneContext`, `Scenario? Scenario`.
4. Tests verify the inherited trusted-binding guarantees: `ScenarioContext`
   is absent from the model-visible tool schema, cannot be supplied or overridden by model arguments, exposes neither
   `IServiceProvider`
   nor a general service bag, keeps Mind and `IMainThreadDispatcher`
   wrapper-private, and fails closed on character-ownership mismatch before dispatcher submission.
5. Session-start tests verify a fresh scene snapshot precedes construction of the phase-1 core render context, that
   the manager is queried exactly once per session with that core context, and that the returned value — record or
   null — becomes the session's scenario and the phase-2 `scenario`
   key of the completed dictionary used for the session system instruction.
6. Session-fixity tests verify no later provider request re-queries the manager, refreshes the scenario, or replaces
   the `ScenarioContext`, and that the same session-captured snapshot serves every tool invocation.
7. Containment tests verify manager failure prevents session start without retry or repair, and an unconfigured
   manager behaves identically to a null-returning manager.
8. Render-dictionary tests verify the top-level `scenario` value is the session's `Scenario`
   record or null, and an authored entry colliding with the reserved `scenario`
   key fails with the existing duplicate-key error.
9. Prompt-asset tests verify the shared generic NPC prompt stack renders the scenario through a plain
   `FilePromptSection`
   referencing `res://prompts/scenario.md` with a `{{#if scenario}}` guard and `{{scenario.Description}}`
   PascalCase access, that no new `PromptSection` type, `IsEnabled`
   machinery, or writer skip behaviour exists, and that a null scenario renders the empty guarded section inside its
   tag pair.
10. Render-context tests verify the phase-key contract: `character` and `player`
    are always present in the core context, with `player` resolved unconditionally via `ISceneContext.Player`
    — so a scenario body token such as `{{player.FullId}}` resolves to the raw canonical `FullId`
    even when the player is excluded from the attention-gated `characters` dictionaries — while `characters`
    may omit the player, and the `scenario` key is absent from the core context handed to the manager.

## References

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)

### Implementation

- `game/src/Mind/AI/Scenario.cs`
- `game/src/Mind/AI/IScenarioManager.cs`
- `game/src/Mind/AI/ScenarioManager.cs`
- `game/src/Mind/AI/FixedScenarioManager.cs`
- `game/src/Mind/AI/ScenarioContext.cs`
- `game/src/Mind/AI/AgenticMind.cs`
- `game/src/Mind/Mind.cs`
- `game/prompts/scenario.md`
- `game/assets/characters/prompts/generic_npc_prompt_stack.tres`

### External Dependencies

- Markdig
