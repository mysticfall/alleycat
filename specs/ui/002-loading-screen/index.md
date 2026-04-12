---
id: UI-002
---

# Loading Screen

## Requirement

The loading screen must display progress while asynchronously loading a target scene.

- The loading screen must display a centred loading message above a centred progress bar.
- The loading screen must expose a public method `LoadSceneAsync(string scenePath)` to begin asynchronous loading of a scene resource.
- Loading progress must be tracked via `Godot.ResourceLoader` threaded loading status and progress values.
- Once the target packed scene is loaded, the loading screen must switch to the new scene using `SceneTree.ChangeSceneToPacked`.
- When scene change completes successfully, the loading screen must emit `LoadCompleted` signal.

## References

### Implementation

- @game/src/UI/LoadingScreen.cs

### Tests

- @integration-tests/src/UI/LoadingScreenIntegrationTests.cs

### Godot

Scenes:
- @game/assets/ui/loading_screen.tscn