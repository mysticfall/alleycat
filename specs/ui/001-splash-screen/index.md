---
id: UI-001
---

# Splash Screen

## Requirement

The splash screen must show the project logo at approximately 50% of the viewport width.
After the scene starts, the screen must remain white for 2 seconds.
The logo must then fade in from transparent to fully visible over 3 seconds.

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
