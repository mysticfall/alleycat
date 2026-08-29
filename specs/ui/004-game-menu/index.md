---
id: UI-004
---

# Game Menu

## Requirement

A controller-driven in-game menu, hosted in the global UI overlay, that pauses the game world while open and lets the
player resume gameplay or quit the game using XR controller input, with gameplay input suppressed across the pause
boundary.

## Goal

Give players a dependable way to suspend the simulation and exit the game from inside VR, reusing the global overlay's
widget hosting, while guaranteeing that inputs held across the pause boundary never leak into gameplay.

## User Requirements

1. Pressing the menu button on the left controller toggles the menu open and closed. The right controller's menu
   button has no effect.
2. The open menu appears as a centred panel in the head-following global UI overlay (the same overlay that hosts the
   debug and notification widgets), showing the title "Menu" and the options "Resume" and "Exit Game". "Resume" is
   selected whenever the menu opens.
3. While open, the thumbstick (`primary` action) on either controller moves the selection one step per gesture: the
   stick must return to neutral before the next step registers, navigation wraps around at both ends, and pushing the
   stick up moves the selection visually up (OpenXR +Y-up convention).
4. The trigger (`trigger_click`) on either controller confirms the selected option. Resume closes the menu. Exit Game
   quits the game with exit code 0, without a confirmation dialogue.
5. While the menu is open the game world is paused: physics and processing stop, and player gameplay input (locomotion
   movement and rotation, hand grabbing, voice-recording start) has no effect.
6. A microphone recording active when the menu opens is stopped and finalised through the normal stop path; it is
   neither discarded silently nor left running.
7. Inputs held through the unpause do not leak into gameplay: the trigger press that confirmed Resume cannot start a
   voice recording, and movement does not resume from stale held-stick values.

## Technical Requirements

1. **Widget Contract**: `GameMenu` is a `[GlobalClass]` `Control` implementing the UI-003 `IUIWidget` marker contract,
   instanced in `game/assets/ui/ui_overlay.tscn` hidden by default, so it is discoverable through the overlay's typed
   widget resolution at `/root/Global/XR/SubViewport/UIOverlay/GameMenu`. The widget processes with
   `ProcessMode.Always`, ignores pointer input, and is driven by raw XR action events rather than Godot `ui_*` focus
   navigation.

2. **Input Configuration**: `GameMenu` exports `MenuHand` (default `Left`), `ToggleActionName` (`menu_button`),
   `NavigateActionName` (`primary`), `ConfirmActionName` (`trigger_click`), and `NavigationDeadZone` (default 0.6,
   also the neutral threshold for the navigation latch). The toggle honours only `MenuHand`; navigation and
   confirmation honour either hand.

3. **Navigation Latch**: One navigation step registers only when the stick's vertical component reaches
   `NavigationDeadZone` while the latch is armed, and the latch re-arms only after the axis magnitude falls back below
   `NavigationDeadZone`. Opening the menu with either stick already deflected starts disarmed. Selection wraps in both
   directions; +Y steps toward the previous (visually upper) option.

4. **Pause Model**: Opening the menu sets `Game.Paused = true`, a thin mirror of `SceneTree.Paused`. The menu keeps
   processing because it runs with `ProcessMode.Always`, and the XR runtime scene roots (`openxr_runtime.tscn`,
   `mock_runtime.tscn`) are authored with `process_mode = 4` (Always) so controller events keep flowing while the tree
   is paused.

5. **Gameplay Input Suppression**: Suppression is Godot-native; no bespoke gating service exists. `PlayerController`
   and `Transcriber` guard every XR controller event handler with `CanProcess()` and hook `NOTIFICATION_PAUSED`:
   `PlayerController` zeroes locomotion movement and rotation input at the pause boundary, and `Transcriber` stops an
   active recording through the normal stop path. A trigger still held across the unpause does not start a recording;
   only a fresh press edge does.

6. **Grab-Release Suppression**: While paused, grab release events are dropped as well as presses; this is intended.
   The world, including hands and held objects, resumes as a whole, and a dropped release leaves recoverable state: a
   fresh squeeze-and-release clears it, and the analogue grip tracker self-heals after unpause.

7. **Deferred Close-Path Unpause**: Closing the menu defers the unpause to the end of the frame (`CallDeferred` on a
   callable capturing the `SceneTree`), so the input edge that closed the menu — for example the trigger press that
   confirmed Resume — is still observed as paused by gameplay consumers whose controller subscriptions were
   registered after this widget's. Known limitation, not a requirement: a same-frame close-and-reopen would be
   stomped by the deferred unpause; this is unreachable with real OpenXR button edges.

8. **Teardown Safety**: `_ExitTree` disconnects the `XRManager.Initialised` subscription and both hands' controller
   subscriptions, and synchronously unpauses the tree when the menu still owns the pause, so a freed menu can never
   leave the tree stuck paused.

9. **Exit Path**: Confirming Exit Game calls `Game.RequestExit(0)` without closing the menu first.
   `Game.RequestExit(int exitCode = 0)` is a public virtual method on `Game` that logs at Information level
   ("Game exit requested with exit code {ExitCode}.") and quits via `SceneTree.Quit`. Startup failure paths also use
   `RequestExit(1)`. The method is virtual so integration tests can override it.

10. **Late Binding**: `GameMenu` subscribes to `XRManager.Initialised` in `_Ready` and also handles the
    already-initialised case, so the menu works regardless of whether it enters the tree before or after XR runtime
    selection completes.

## In Scope

- Toggle, navigation, and confirmation input model from XR controller actions.
- Centred panel layout with the title and the Resume/Exit Game options inside the global overlay.
- Pause-based suppression of gameplay input while the menu is open (locomotion, grab, recording).
- Recording stop and finalisation at the pause boundary.
- Deferred unpause on close and synchronous unpause on teardown.
- Exit through `Game.RequestExit`, including the shared `RequestExit` contract used by startup failure paths.
- Runtime integration tests and the screenshot-based visual verification fixture.

## Out Of Scope

- Pointer/ray-based menu interaction.
- Confirmation dialogue for exit.
- Menu sounds, haptics, or animation polish.
- Additional menu entries (for example settings).
- Save-game behaviour on exit.
- Pausing individual subsystems selectively.
- Multi-menu or stacked menus.

## Acceptance Criteria

1. The packed overlay scene contains `GameMenu`, initially hidden with Resume selected, running with
   `ProcessMode.Always`, discoverable through `FindWidget<GameMenu>`, and showing the title "Menu" with the options
   "Resume" and "Exit Game".
2. The left controller's menu button opens the menu with Resume selected and pauses the scene tree; the right
   controller's menu button is ignored.
3. Toggling with the menu button, and confirming Resume, both close the menu and unpause the tree at the end of the
   closing frame.
4. Thumbstick navigation steps exactly once per gesture from either hand and requires a return to neutral between
   steps.
5. Navigation wraps in both directions: up from Resume selects Exit Game, and down from Exit Game selects Resume.
6. Confirming Exit Game from either hand requests exit code 0 through `Game.RequestExit`, while the menu stays open
   and the tree stays paused.
7. While the menu is open and paused, locomotion, grab, and transcription inputs are suppressed; the same events act
   on the world again after the menu closes and the tree unpauses.
8. A thumbstick held deflected through the pause is zeroed at the pause boundary and does not leak movement after
   unpause until a fresh axis event arrives.
9. A recording active when the menu opens stops at the pause boundary and finalises with exactly one transcription
   request after the tree unpauses.
10. A trigger held across the unpause boundary does not start a new recording; only a fresh release-then-press edge
    does.
11. The record-hand trigger press that confirms Resume does not start a recording, because the unpause is deferred to
    the end of the closing frame.
12. Grab presses and releases are both suppressed while paused; after unpause a fresh grab press works, and the
    analogue grip tracker self-heals so a subsequent release clears it.
13. A menu added to the hierarchy after XR initialisation binds and opens through the already-initialised path.
14. Freeing the menu while it is open unpauses the tree and stops the menu from consuming further input.
15. The specification defines both user-visible behaviour outcomes and technical implementation contracts.

**Traceability Map:** UR-1 → AC-2, AC-3; UR-2 → AC-1, AC-2; UR-3 → AC-4, AC-5; UR-4 → AC-3, AC-6; UR-5 → AC-7,
AC-12; UR-6 → AC-9; UR-7 → AC-8, AC-10, AC-11; TR-1 → AC-1; TR-2 → AC-2; TR-3 → AC-4, AC-5; TR-4 → AC-2, AC-7;
TR-5 → AC-7–AC-10; TR-6 → AC-12; TR-7 → AC-3, AC-11; TR-8 → AC-14; TR-9 → AC-6; TR-10 → AC-13; layer coverage →
AC-15.

## References

- Implementation: @game/src/UI/GameMenu.cs, @game/src/UI/GameMenuOption.cs, @game/src/Game.cs
- Scenes: @game/assets/ui/game_menu.tscn, @game/assets/ui/ui_overlay.tscn, @game/assets/xr/openxr_runtime.tscn,
  @game/assets/xr/mock_runtime.tscn
- Suppressed Consumers: @game/src/Control/PlayerController.cs, @game/src/Speech/Transcription/Transcriber.cs
- Tests: @integration-tests/src/UI/GameMenuIntegrationTests.cs
- Visual Fixture: @game/tests/ui/game_menu_visual.tscn, @game/tests/ui/game_menu_visual.gd,
  @game/tests/ui/GameMenuVisualStateDriver.cs, @game/tests/ui/GameMenuVisualGameStub.cs
- Related Specs: [UI-003: UI Overlay](../../ui/003-ui-overlay/index.md),
  [XR-001: XRManager](../../xr/001-xr-manager/index.md), [CTRL-001: Locomotion](../../ctrl/001-locomotion/index.md),
  [CTRL-002: Hand Grab Input](../../ctrl/002-hand-grab-input/index.md),
  [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
