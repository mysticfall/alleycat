---
id: AI-015
title: Coordinate Navigation Tools
---

# Coordinate Navigation Tools

## Requirement

An NPC driven by `AgenticMind` must be able to move itself through three model-facing tools — `walk_to`, `turn_to`
and `stop` — that take absolute world coordinates and compass bearings read off the AI minimap (AI-013, SCN-002) and
drive the character's NAV-001 navigation. Calls never block: each returns at once with a start confirmation or a
contained refusal, and every started move later receives exactly one outcome through the AI-002 scene-update
exchange.

## Goal

Let players watch NPCs walk to places and face directions on their own initiative. This is the **supplementary
coordinate mode**: a later name-based tool ("move to the door") is intended as the main mode, so tool names,
descriptions and result texts here speak only of coordinates and bearings, leaving name-based targets free for that
tool without overlap.

## User Requirements

1. An NPC can start walking to a ground spot given as absolute world X/Z metres, as labelled on the minimap grid
   (+X right, +Z down; the map never rotates), optionally naming the direction to face on arrival and a named pace.
2. Without an arrival direction, the NPC ends facing roughly the way its route arrives; a small adjustment turn on
   the spot after reaching the destination is acceptable.
3. An NPC can turn on the spot to face a compass bearing without walking, and can stop, standing still where it is.
4. Bearings are compass degrees: 0 = map up (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), clockwise.
5. Paces are `stroll`, `walk` (default) and `hurry`; the NPC never chooses a raw speed.
6. Movement never freezes the NPC's reasoning: the NPC learns at once whether a move started or was refused, keeps
   speaking and using other tools while moving, and is told later — exactly once per started move — whether it
   arrived, failed (and why), was stopped, or was replaced by a newer move, together with where it now stands and
   which way it faces. When a move arrives or fails, the NPC reacts at once — even if it was waiting — as it would to
   a late tool result, without cutting short reasoning already under way.
7. A new move during motion replaces the current one smoothly, without returning to idle first, and the NPC is told
   that an earlier move was replaced.
8. A refused move — an invalid value, an unreachable spot, or navigation not yet ready — changes nothing: any move
   already in progress continues.
9. Only NPCs that can navigate see these tools.

## Technical Requirements

### Tool Inventory and Binding

1. `AgenticMind` composes `walk_to`, `turn_to` and `stop` only when the subject character exposes navigation
   (`INavigator` resolving an `INavigation` component) at composition time; otherwise the three tools are absent and a
   model call to any of them is rejected as an unknown tool (AI-002 TR-13). Composition time means after the owning
   character's component projection exists ([AI-002 TR-1](../002-agent-runtime/index.md#session-and-request-context)),
   so a role-installed NPC whose navigation is added during installation still receives the tools. The tools bind the
   subject's navigation at composition, following AI-002's trusted-binding rule: the model supplies no identity.
2. Tool names are normative. They name coordinate and bearing operations only; target-name parameters are reserved
   for the future name-based tool and must not be added here.

### Tool Descriptions

3. The model-facing descriptions below are normative (AI-002 TR-15–TR-16 accuracy applies); only light editorial
   changes that keep their meaning are allowed.
   - `walk_to`: "Start walking to a spot on the ground. Give the destination as absolute world coordinates read from
     the minimap grid labels: `x` in metres (increases to the right on the map) and `z` in metres (increases
     downwards on the map). Optional `heading` is the direction to face once you arrive, as a compass bearing in
     degrees: 0 = up on the map (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), measured clockwise; any
     finite value is normalised into [0, 360). Without `heading` you keep facing the way you were walking when you
     arrive. Optional `pace` is `stroll`, `walk` (default) or `hurry`. Returns at once when the walk has started (or
     with a refusal if the spot cannot be reached); you are told later when you arrive or if the walk fails. A new
     `walk_to` replaces any walk already in progress. To face something you can only judge from your destination,
     such as a person seen from where you will stand, leave `heading` out and call `turn_to` after you arrive."
     - `x`: "Destination world X in metres, as labelled on the minimap grid."
     - `z`: "Destination world Z in metres, as labelled on the minimap grid."
     - `heading`: "Optional compass bearing in degrees to face on arrival. It is the direction to face after
       arriving, judged from the destination point on the map, not from where you stand now."
     - `pace`: "Optional walking pace." — an enum of `stroll`, `walk` and `hurry`.
   - `turn_to`: "Turn on the spot to face a direction, without walking. `heading` is a compass bearing in degrees:
     0 = up on the map (−Z), 90 = right (+X), 180 = down (+Z), 270 = left (−X), measured clockwise; any finite value
     is normalised into [0, 360). Returns at once when the turn has started."
     - `heading`: "Compass bearing in degrees to face."
   - `stop`: "Stop walking: cancels your current destination and you stand still where you are. Returns at once."

### Coordinates, Bearings and Pace

4. `x` and `z` are absolute world metres in SCN-002's axis convention; the tools apply no offset, rotation or
   subject-relative transform. The destination height comes from the navigation map, not from the model.
5. One shared conversion maps between bearings and world facing: bearing `b` faces world direction
   (sin b, 0, −cos b), and a world facing converts back by the inverse. Any finite heading is normalised into
   [0, 360) before use (for example −90 → 270, 360 → 0); a non-finite heading, `x` or `z` is refused.
6. Pace maps to NAV-001 UR4 normalised speed: `stroll` = 0.5, `walk` = 0.75, `hurry` = 1.0. These values are
   documented tuning constants; the names and the default `walk` are fixed. An unknown pace is refused.

### Request Semantics

7. `walk_to` submits one NAV-001 destination request at (`x`, `z`) with the mapped pace. With `heading`, the request
   carries that terminal facing. Without it, the request carries the bearing of the planned route's final segment
   as its terminal facing, falling back to the straight line from the current position to the destination, then
   to the current facing, since every NAV-001 destination carries a facing. The realised approach can differ from
   the planned final segment, so a turn on the spot to the submitted facing after the position is reached is
   permitted (NAV-001 finite terminal-facing turn plan).
8. `turn_to` submits a destination at the character's current position with the requested facing, so NAV-001's
   finite terminal-facing turn plan performs a stationary turn; it requests no translation.
9. `stop` clears the current destination (NAV-001 cancellation): the character transitions to idle where it stands.
   With no move in progress, `stop` succeeds as a no-op and no outcome follows. When the session ends (AI-002
   TR-1), the tools' controller is released on the main thread after the session has finished: a move it started
   that is still active is cancelled through the same destination clear, and the terminal records of its moves
   are acknowledged without publishing outcomes, since no session remains to receive them.
10. Calls are non-blocking. Each returns as soon as NAV-001 accepts or refuses the request, with a short textual
    result: "started" (naming pace, and heading when given), or a contained refusal naming the reason. Invalid,
    unreachable, map-not-ready and outcome-backpressure refusals leave navigation state unchanged (`INavigation`
    refusal contract), including any move in progress. Tool results carry no world-state observation and no image.
11. A move accepted while another is active replaces it from current realised motion without an idle reset
    (NAV-001 UR3). The start result states that an earlier move was replaced.
12. Other tools, including `speak`, stay callable while a move runs. The tools add no wait, lock or scheduling
    effect to the AI-002 runtime beyond the TR-14 outcome wake.

### Outcome Delivery

13. Every started `walk_to` or `turn_to` request receives exactly one outcome, mapped from its NAV-001 terminal
    outcome: `arrived` (completed), `failed` with a reason (failed, blocked, or any cancellation not caused by
    `stop`), `stopped` (cancelled by `stop`), or `replaced` (superseded). Within a running session no started move
    ends without an outcome, and none receives two. A move still active or unreported when the session ends receives
    no outcome: it is cancelled and its record acknowledged at controller release (TR-9).
14. Outcomes are observations sent through Mind's atomic queue (AI-002 TR-13) and delivered to the model in the
    AI-002 TR-18 `scene_update` exchange in timeline order; this spec adds no delivery path. `arrived` and `failed`
    outcomes opt in to the AI-002 TR-18 wake contract, so the model reacts straight away, as to a late tool result:
    - Parked — in a model-called `wait` or in automatic parking (the decision wait): the wait ends at once with the
      existing act-now wait reason, and the next request delivers the outcome through `scene_update`.
    - A turn already running: the outcome raises no fresh-turn signal, so the turn is not interrupted, and delivery
      follows AI-002 TR-18 unchanged, exactly once. Until an accepted request has presented it, the outcome stays
      wake-pending, so a wait that begins before then ends immediately; an outcome already presented ends no
      later wait.
    - `stopped` and `replaced` outcomes do not wake: the NPC's own call caused them, and its tool result already
      says so. They are delivered with the next ordinary materialisation.
15. Each outcome text ends with the character's final own position and heading in the form
    "You are at (x, z), facing N°.": position in world metres rounded to 0.1 m, heading as a whole-degree bearing
    from TR-5, formatted with invariant culture. One shared formatter produces this text for every outcome kind.
    The leading outcome sentence per kind is tunable wording but must name the kind and, for `failed`, the reason.
16. Own-position text appears only in outcome texts: the AI-013 `minimap` result and automatic minimap delivery
    carry none. Outcome updates attach no minimap image.

### Minimap Grid

17. The AI minimap composition (AI-013 TR-3, shared by tools and auto modes) requests a fixed documented
    `GridMinimumSpacingPixels` override (SCN-002 TR-9, TR-14) chosen so the grid resolves to a 1 m step at the AI
    framing; the SCN-002 adaptive step rule itself is unchanged.

### Known Limitation

18. Facing a target that can only be judged from the destination — for example a person seen from where the NPC will
    stand — is not reliably achieved in coordinate mode, with or without a `turn_to` after arrival. The descriptions
    steer the model to leave `heading` out and `turn_to` after arriving; the `arrived` wake (TR-14) prompts that
    follow-up turn, but its bearing remains unreliable. A reliable fix is deferred to the name-based tool.
    Acceptance measures this task type without gating on it.

### Validation Contract

19. Deterministic tests cover: refusals leaving navigation state unchanged, including during an active move; exactly
    one outcome per started move, including replacement and `stop`; `arrived` and `failed` waking a mind parked in
    an explicit `wait` and in automatic parking with the act-now wait reason, `stopped` and `replaced` not waking
    it, an outcome committed during a running turn not interrupting it yet ending the next wait while still
    unpresented, an already presented outcome ending no later wait, and each outcome delivered exactly once;
    bearing conversion at 0, 90, 180 and 270 plus normalisation of negative, ≥ 360 and non-finite values; pace
    mapping and default; the headingless `walk_to` facing order of TR-7 (final route segment, then straight line,
    then current facing); controller release at session end cancelling its active move and acknowledging its
    records without publishing outcomes; the shared self-position text and its rounding; and composition only for
    navigation-capable subjects.
20. An integration test in `game/tests/agent_navigation/` drives the real tools on a minimal reference-character
    subject: a `walk_to` with `heading` reaching the coordinates with the requested bearing within NAV-001's
    configured position and facing tolerances; a headingless `walk_to` whose final facing is (a) within NAV-001's
    configured facing tolerance of the bearing of the planned route's final segment, which the test computes
    itself from the navigation map path, and (b) within 30° of the realised arrival travel direction — the TR-21
    heading accuracy bound — as a check that it ends facing roughly the way it walked, with no gate on the turn
    after arrival; a `turn_to` turning in place; and a `stop` mid-walk ending in a `stopped` outcome. A separate
    regression, `integration-tests/src/Mind/AI/RoleInstalledSessionToolCompositionIntegrationTests.cs`, loads the
    shipped reference-female NPC scene through the production role installer and checks that the first session
    request's tool inventory includes `walk_to`, `turn_to` and `stop` (TR-1).
21. A live-LLM acceptance run executes the final gate in `game/tests/agent_navigation/`: the task set and criteria
    in `integration-tests/src/Mind/AI/Navigation/CoordinateNavigationGate.cs`, run by
    `CoordinateNavigationFinalGateTests.cs`. Its fixed five-task set is: walk next to an object, walk next to a
    person, turn to face an object, walk to a spot and face a direction, and walk over by an object and face a
    person judged from the destination. Each trial receives the production model-facing contract — composed
    instructions and tools, live minimap renders, the production `walk_to` results, and the production `arrived`
    outcome in a `scene_update` exchange — while movement itself is simulated: navigation calls are recorded and
    scored, never walked. Scoring:
    - **Validity:** every navigation call is schema-valid; a JSON `null` for an optional parameter counts as
      omitted, while a missing required argument, a non-number or non-finite number, or an unknown pace is invalid.
    - **Position** (the four walking tasks): the last `walk_to` of the trial's final tool batch is measured by
      planar distance from the target's scene position. A task whose target point is occupied — next to an object,
      next to a person, and over by the object in the person-from-destination task — passes within 0.75 m plus a
      0.75 m stand-off allowance (≤ 1.5 m); the unoccupied spot passes within 0.75 m. A trial without a valid
      `walk_to` fails.
    - **Heading:** a bearing passes within ±30° of the scene-derived bearing. The turn task reads the last
      `turn_to.heading` of the final batch. Walk-and-face tasks read `walk_to.heading` when given; when `walk_to`
      omits it, the first `turn_to` in the follow-up after arrival counts instead. A missing heading fails.
    - **Gates:** validity 100 %; position ≥ 80 % over the four walking tasks; heading ≥ 80 % over the turn and
      walk-and-face-direction tasks. The person-from-destination heading is measured and reported, not gated
      (TR-18).
22. Tests and fixtures use reference characters only (`reference_female`, `reference_male`).

## In Scope

- The three tools, their normative descriptions, conditional composition and subject binding.
- Bearing, coordinate and pace contracts with one shared conversion and one shared self-position formatter.
- Non-blocking start/refusal results, replacement, and exactly-once outcome delivery through `scene_update`.
- The AI minimap 1 m grid override.
- The deterministic, integration and live-LLM validation contract.

## Out Of Scope

- Name-based navigation ("move to the door"), planned as the main mode, including its fix for facing targets judged
  from the destination.
- Via-points through the tools, following moving characters, and multi-NPC movement coordination.
- Raw speed parameters and model-supplied tuning of navigation tolerances.

## Acceptance Criteria

### User Requirements

1. In the agent-navigation scene, an AgenticMind NPC walks to minimap coordinates, faces a requested bearing on
   arrival, ends facing roughly the way its route arrives when no bearing is given (a small adjustment turn after
   arriving is acceptable), turns in place, and stops mid-walk (UR-1–UR-4).
2. Pace names produce visibly different walking speeds, with `walk` as the default (UR-5).
3. Transcripts show start results returning at once, speech during a walk, and exactly one later outcome per
   started move naming arrived, failed with reason, stopped or replaced, each ending with own position and heading
   (UR-6, UR-7).
4. A waiting NPC reacts at once when its move arrives or fails, a move ending mid-turn does not cut that turn
   short, and a stopped or replaced move gives no extra turn (UR-6).
5. A refused move leaves a move in progress running; an NPC without navigation has no navigation tools
   (UR-8, UR-9).
6. The live-LLM run meets the TR-21 gates and reports the person-from-destination result.

### Technical Requirements

1. Composition tests verify the tools appear only for navigation-capable subjects and unknown-tool rejection
   otherwise (TR-1, TR-2); the TR-20 role-installed reference NPC regression shows its first session request
   carrying all three tools (TR-1; AI-002 TR-1).
2. Description tests verify the shipped descriptions match TR-3 (TR-3).
3. Deterministic tests of TR-19 pass, covering TR-4–TR-16, including the TR-14 outcome wake, its exclusions, the
   pending-until-presented rule and the unchanged wait reason (AI-002 TR-18).
4. A minimap composition test verifies the grid override and that the `minimap` result carries no own-position text
   (TR-16, TR-17).
5. The TR-20 integration test passes, with the headingless `walk_to` checked against the test's own
   final-segment bearing from the navigation map path (NAV-001 facing tolerance) and the realised arrival travel
   direction (30°), and no gate on the turn after arrival (TR-7, TR-20); the TR-21 final-gate report records
   validity, position and heading rates per task, with fixtures limited to reference characters (TR-20–TR-22).
6. Formatting verification, the warning-as-error solution build, unit tests and affected integration suites pass;
   an independent review checks code–spec consistency in both directions.

## References

Normative dependencies:

- [AI-002: Agent Runtime](../002-agent-runtime/index.md) — amended by this spec: conditional tool inventory
  (TR-13), navigation outcomes delivered through the `scene_update` exchange, and wake-eligible observations ending
  a parked wait (TR-7, TR-18, TR-41); its session-start component-projection wait (TR-1) defines when composition
  happens.
- [AI-013: Visual AI Context](../013-visual-context/index.md) — amended by this spec: the 1 m grid override in the
  AI minimap composition (TR-3).
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md) — consumed unchanged: destination requests with
  facing intent, normalised speed (UR4), replacement without idle reset (UR3), the refusal contract, terminal
  outcomes and the finite terminal-facing turn plan.
- [SCN-002: Spatial Minimap](../../scene/002-spatial-minimap/index.md) — axis convention, world-zero grid labels in
  reserved gutters (TR-14) and per-request grid spacing (TR-9).

Implementation surfaces:

- `game/src/Mind/AI/AgenticMind.cs` — tool composition.
- `game/src/Mind/AI/Tool/MinimapTool.cs` — existing tool pattern; `game/src/Mind/AI/VisualContextCapture.cs` — AI
  minimap settings.
- `game/src/Navigation/INavigation.cs` and `game/src/Navigation/INavigator.cs` — navigation capability.
- `game/tests/agent_navigation/` — reference-character test scene.
