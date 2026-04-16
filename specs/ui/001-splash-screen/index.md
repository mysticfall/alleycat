---
id: UI-001
---

# Splash Screen

## Requirement

The splash screen must show the project logo at approximately 50% of the viewport width.

- After the scene starts, the logo must fade in from transparent to fully visible.
- Fade-in must begin after an initial configurable delay (`FadeInDelaySeconds`).
- Fade-in must use `FadeDurationSeconds`.
- After fade-in completes, the splash must wait for a configurable post-fade-in delay (`FadeOutDelaySeconds`).
- After `FadeOutDelaySeconds`, the logo must fade out automatically using `FadeDurationSeconds`.
- When fade-out completes, the splash must emit `SplashFinished` so consuming flows can continue.

## Goal

Provide a predictable branded startup transition that cleanly hands control to the next flow stage.

## User Requirements

1. Players must see a centred project logo that fades in and out smoothly during startup.
2. The splash sequence must complete automatically and hand off to the next flow without manual input.

## Technical Requirements

1. Splash timing must be controlled through configurable delays and duration fields (`FadeInDelaySeconds`,
   `FadeDurationSeconds`, `FadeOutDelaySeconds`).
2. Fade animation must transition alpha from transparent to visible (fade in) and back to transparent (fade out).
3. Completion must emit `SplashFinished` as the integration contract for downstream flow control.

## In Scope

- Splash logo sizing and centring behaviour.
- Fade-in/fade-out timing contract with configurable delay/duration parameters.
- Completion signalling contract (`SplashFinished`).

## Out Of Scope

- Loading-progress display and asynchronous scene loading behaviour.
- Broader menu/navigation flow design.
- Branding asset creation and art-direction iterations.

## Acceptance Criteria

1. The splash screen displays the project logo at approximately 50% viewport width, centred in the view.
2. Fade-in starts after `FadeInDelaySeconds`, uses `FadeDurationSeconds`, and reaches full visibility.
3. Fade-out starts after `FadeOutDelaySeconds`, uses `FadeDurationSeconds`, and reaches full transparency.
4. `SplashFinished` is emitted when fade-out completes.
5. The specification defines both user-visible behaviour outcomes and technical implementation contracts.

## References

### Implementation

- @game/src/UI/SplashScreen.cs

### Tests

- @integration-tests/src/UI/SplashScreenIntegrationTests.cs

### Godot

Scenes:
- @game/assets/ui/splash_screen.tscn

Resources:
- @game/assets/images/logo.svg
