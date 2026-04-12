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
