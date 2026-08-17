---
id: AI-008
title: Scenario
---

# Scenario

## Requirement

The system must provide a character-bound scenario that supplies the objectives and additional context framing an
NPC's current interactions, and must own the trusted turn-context binding that carries the owning character, the
turn-captured scene snapshot, and the current scenario into prompt rendering and tool invocation.

## Goal

Let contributors steer what an NPC is currently trying to achieve without confusing narrative context with physical
scene membership, while keeping scenario resolution per-turn, the turn binding trusted, and rendering inside the
existing shared prompt stack.

## User Requirements

1. Contributors can author a scenario for an NPC so that the NPC's responses pursue authored objectives and additional
   interaction context instead of relying only on ambient observation history.
2. An NPC with no configured scenario manager, and a turn for which the manager supplies no scenario, must behave
   exactly like an NPC without the scenario feature: absence yields empty prompt content, never a failed turn or a
   stale carry-over from an earlier turn.
3. A scenario applies to one character's current interactions only; scenario authoring must not alter scene membership,
   other characters, or physical world state.
4. Scenario text reaches the prompt as plain authored narrative; it is never executed as a template and never generated
   or rewritten by the model.
5. Scenario resolution failures must be contained and logged like other turn failures, without crashing the scene,
   retrying the turn, or corrupting the observation timeline.

## Technical Requirements

1. A scenario is a character-bound narrative and interaction context: the objectives and additional context for what is
   currently happening to that character. It is deliberately distinct from SCN-001 `ISceneContext`, which remains the
   physical scene membership snapshot of what exists in the Godot scene tree. There is no is-a relationship in either
   direction: `ScenarioContext` composes an `ISceneContext` by reference, and neither type inherits from the other.
   AI-008's scenario types must live under `AlleyCat.Mind.AI`.
2. `Scenario` must be a plain C# record, not a Godot `Resource`. Instances are created at runtime by manager
   implementations and must never be Godot-serialised. Its exact public surface is one `string Description` property.
   `Description` is plain authored text and must never be evaluated as a template.
3. `IScenarioManager` must define exactly `Scenario? GetCurrentScenario(ScenarioContext previous)`, where the argument
   is the previous turn's `ScenarioContext` and a null return means no scenario is available for the current turn.
4. `FixedScenarioManager` must be the first manager implementation: a `[GlobalClass]` Godot `Resource` carrying an
   authored multiline description string. `GetCurrentScenario` must always return a `Scenario` whose `Description` is
   exactly that authored text. A blank authored description is invalid authoring and must fail clearly through the
   TR-10 containment path.
5. `ScenarioContext` must replace AI-002's `AgentToolContext` everywhere as the trusted, immutable turn binding. Its
   exact public surface is `ICharacter Character`, `ISceneContext SceneContext`, and `Scenario? Scenario`.
6. `ScenarioContext` inherits every AI-002 trusted-context guarantee: it must be excluded from the model-visible tool
   schema; it must not be supplied, replaced, or overridden by model arguments; it must not implement or expose
   `IServiceProvider` or act as a general service bag; Mind and `IMainThreadDispatcher` remain private to the common
   `AgentTool` wrapper; and character-ownership verification before dispatcher submission is unchanged.
7. Before each foreground turn, AgenticMind must capture a fresh SCN-001 scene snapshot, query the configured manager
   passing the previous turn's `ScenarioContext` — lazily created with a null scenario when no previous context exists —
   and adopt the returned `Scenario?` as the current scenario. It must then construct the new `ScenarioContext` from the
   owning character, the captured snapshot, and the current scenario, and use that one binding for both the prompt
   render and the tool context.
8. The current scenario is per-turn state, not node-lifetime state. When the manager returns null, the Mind's current
   scenario reference must become null for that turn; a scenario never persists across turns except through the manager
   receiving the previous `ScenarioContext`.
9. A replacement turn after interruption must reuse the just-built `ScenarioContext` without re-querying the manager, so
   the replacement renders and acts within the same scenario as the turn it replaces.
10. Manager failure must follow AI-002's turn-start containment path: the prior published render snapshot is retained
    and no retry or repair is attempted. When no manager is configured, behaviour must be identical to a null-returning
    manager.
11. The shared foreground render dictionary must gain the top-level key `scenario`, whose value is the current
    `Scenario` record or null. The key is reserved exactly like `character`, `characters`, `player`, and
    `observations`: an authored ContextWorker projection colliding with `scenario` must fail with the existing
    duplicate-key error. AI-001 TR-30 and AI-003 TR-20 enumerate this key in their composition lists; AI-008 is
    normative for its value and reservation semantics.
12. The scenario must be rendered by a plain `FilePromptSection` in the shared generic NPC prompt stack referencing
    `res://prompts/scenario.md`, authored with a `{{#if scenario}}` guard rendering `{{scenario.Description}}` with
    PascalCase property access matching existing fragment conventions. There is deliberately no new `PromptSection`
    type, no `IsEnabled` machinery, and no writer skip behaviour: absence is handled inside `scenario.md`, and the
    section's pseudo-XML tag pair appearing in the prompt with empty content when the scenario is null is an accepted
    quirk.
13. AI-005 ContextWorkers capturing the published render snapshot must see the same `scenario` key with no additional
    work; scenario values reach workers only through ordinary snapshot capture, never through worker-specific scenario
    contracts.

## In Scope

- The `Scenario` record, the `IScenarioManager` manager contract, and the `FixedScenarioManager` first implementation.
- `ScenarioContext` as the trusted immutable turn binding replacing `AgentToolContext` in AI-002 tool invocation.
- AgenticMind per-turn scenario resolution: fresh scene snapshot, previous-context hand-off, null handling,
  replacement-turn reuse, and containment of manager failure and unconfigured managers.
- The reserved top-level `scenario` render-dictionary key with its record-or-null value.
- The shared generic NPC prompt stack scenario `FilePromptSection` and `res://prompts/scenario.md` authoring, including
  the accepted null-tag quirk.
- Scenario visibility to AI-005 workers through ordinary published-snapshot capture.

## Out Of Scope

- Event or observation history inside `ScenarioContext`; rendered event history remains above this layer and is
  deferred to a separate future session.
- Dynamic or templated scenario descriptions; managers needing dynamic text must compose the string themselves at
  `Scenario`-creation time.
- Scenario progression, state machines, or manager implementations beyond `FixedScenarioManager`.
- Multiple concurrent scenarios per character.
- Non-AI consumers of scenarios.
- New production action tools.

## Acceptance Criteria

### User Requirements

1. Integration and play coverage show an NPC with an authored fixed scenario pursues that scenario's objectives and
   context in its responses, while an NPC without a configured manager behaves as it did before the feature.
2. Coverage shows a turn with no scenario renders empty scenario content — the accepted tag-pair quirk aside — without
   failing the turn or carrying a stale scenario from an earlier turn.
3. Coverage shows scenario authoring changes only the NPC's prompt context: scene membership, other characters, and
   world state are untouched.
4. Coverage shows a manager failure is contained and logged without a scene crash, turn retry, or observation-timeline
   corruption.

### Technical Requirements

1. Contract tests verify `Scenario` is a plain C# record with exactly one `string Description` property, is not a Godot
   `Resource`, and is created only by manager implementations.
2. Tests verify `IScenarioManager` exposes exactly `Scenario? GetCurrentScenario(ScenarioContext previous)` and that
   `FixedScenarioManager` is a `[GlobalClass]` `Resource` returning a `Scenario` with exactly its authored text on
   every call.
3. Contract tests verify `ScenarioContext`'s exact public surface — `ICharacter Character`, `ISceneContext
   SceneContext`, `Scenario? Scenario` — and that no `AgentToolContext` type or reference remains in the tool path.
4. Tests verify the inherited trusted-binding guarantees: `ScenarioContext` is absent from the model-visible tool
   schema, cannot be supplied or overridden by model arguments, exposes neither `IServiceProvider` nor a general
   service bag, keeps Mind and `IMainThreadDispatcher` wrapper-private, and fails closed on character-ownership
   mismatch before dispatcher submission.
5. Turn-start tests verify a fresh scene snapshot precedes the manager query, the manager receives the previous turn's
   `ScenarioContext` lazily created with a null scenario on the first turn, and the returned value — record or null —
   becomes the current scenario.
6. Interruption tests verify a replacement turn reuses the just-built `ScenarioContext` without re-querying the
   manager.
7. Containment tests verify manager failure retains the prior published render snapshot without retry or repair, and
   an unconfigured manager behaves identically to a null-returning manager.
8. Render-dictionary tests verify the top-level `scenario` value is the turn's `Scenario` record or null, and an
   authored worker projection colliding with the reserved `scenario` key fails with the existing duplicate-key error.
9. Prompt-asset tests verify the shared generic NPC prompt stack renders the scenario through a plain
   `FilePromptSection` referencing `res://prompts/scenario.md` with a `{{#if scenario}}` guard and
   `{{scenario.Description}}` PascalCase access, that no new `PromptSection` type, `IsEnabled` machinery, or writer
   skip behaviour exists, and that a null scenario renders the empty guarded section inside its tag pair.
10. Worker tests verify AI-005 ContextWorkers capture the `scenario` key through ordinary published-snapshot capture
    with no worker-specific scenario contract.

## References

### Related Specifications

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-002: Agent Runtime](../002-agent-runtime/index.md)
- [AI-003: Prompt API](../003-prompt-api/index.md)
- [AI-005: Context Worker](../005-context-worker/index.md)
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
