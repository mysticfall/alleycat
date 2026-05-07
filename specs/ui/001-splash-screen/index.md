---
id: UI-001
---

# Splash Screen

## Requirement

Display a centred project logo that fades in, waits, then fades out to provide a clean handoff to the next flow stage.

## Goal

Provide a predictable branded startup transition that cleanly hands control to the next flow stage.

## User Requirements

1. Players must see a centred project logo that fades in and out smoothly during startup.
2. The splash sequence must complete automatically and hand off to the next flow without manual input.
3. When `--skip-splash` is provided, the splash screen must be bypassed entirely while preserving startup
   flow continuity.

## Technical Requirements

1. Splash must implement fade-in, hold, and fade-out animation with configurable timing.
2. Completion must emit `SplashFinished` as the integration contract for downstream flow control.
3. Splash must be dynamically instantiated by `Game` via the exported `SplashScreenScene` property.
4. Instantiated splash must be added as a child of the UI SubViewport.
5. Splash must be skipped when `--skip-splash` command-line argument is present, without breaking the startup flow.

## In Scope

- Splash logo sizing and centring behaviour.
- Fade timing contract with configurable delay and duration parameters.
- Completion signalling contract (`SplashFinished`).
- Dynamic instantiation contract via `Game.SplashScreenScene` property.
- Skip flag contract (`--skip-splash`).

## Out Of Scope

- Loading-progress display and asynchronous scene loading behaviour.
- Broader menu or navigation flow design.
- Branding asset creation and art-direction iterations.
- Splash scene asset creation or modification (handled in asset pipeline).

## Acceptance Criteria

1. The splash screen displays the project logo at approximately 50% viewport width, centred in the view.
2. Fade-in starts after the configured delay, uses the configured duration, and reaches full visibility.
3. Fade-out starts after the post-fade delay, uses the configured duration, and reaches full transparency.
4. `SplashFinished` is emitted when fade-out completes.
5. When `--skip-splash` is passed, splash instantiation is skipped but startup continues to loading screen.
6. Criteria 1-5 verify user-visible behaviour; criteria 2-4 verify technical integration contracts.

## References

### Implementation

- @game/src/UI/SplashScreen.cs
- @game/src/Game.cs

### Tests

- @integration-tests/src/UI/SplashScreenIntegrationTests.cs

### Godot

Scenes:
- @game/assets/ui/splash_screen.tscn

Resources:
- @game/assets/images/logo.svg