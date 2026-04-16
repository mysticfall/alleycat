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

## Goal

Provide a responsive loading experience that communicates progress while transitioning safely to the target scene.

## User Requirements

1. Players must see clear loading feedback while a target scene is being prepared.
2. Loading UI layout must remain readable, with centred status message and progress indicator.
3. Scene transition must complete automatically once loading succeeds.

## Technical Requirements

1. The loading screen must expose `LoadSceneAsync(string scenePath)` as the public trigger for asynchronous loading.
2. Progress updates must consume threaded-loading status/progress values from `Godot.ResourceLoader`.
3. Successful load completion must transition via `SceneTree.ChangeSceneToPacked` and emit `LoadCompleted`.

## In Scope

- Loading message and progress-bar layout requirements.
- Asynchronous packed-scene load orchestration through `LoadSceneAsync`.
- Completion transition and `LoadCompleted` signal contract.

## Out Of Scope

- Splash/branding animation sequences.
- Error-screen UX for unrecoverable load failures.
- Content-specific scene initialisation logic after transition.

## Acceptance Criteria

1. The loading screen displays a centred loading message above a centred progress bar.
2. `LoadSceneAsync(string scenePath)` initiates asynchronous loading of the requested scene resource.
3. Loading progress reflects threaded loader status/progress updates.
4. On successful load, scene transition uses `SceneTree.ChangeSceneToPacked` and emits `LoadCompleted`.
5. The specification defines both user-visible behaviour outcomes and technical implementation contracts.

## References

### Implementation

- @game/src/UI/LoadingScreen.cs

### Tests

- @integration-tests/src/UI/LoadingScreenIntegrationTests.cs

### Godot

Scenes:
- @game/assets/ui/loading_screen.tscn
