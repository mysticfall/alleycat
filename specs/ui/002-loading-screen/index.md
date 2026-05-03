---
id: UI-002
---

# Loading Screen

## Requirement

The loading screen must display progress while asynchronously loading a target scene.

- The loading screen must display a centred loading message above a centred progress bar.
- The loading screen must expose a public method `LoadSceneAsync(string scenePath)` to begin asynchronous loading of a scene resource.
- Loading progress must be tracked via `Godot.ResourceLoader` threaded loading status and progress values.
- Once the target packed scene is loaded, the loading screen must hide the progress bar and display a recentering instruction message.
- The loading screen must listen for the `PoseRecentered()` signal from `XRManager` and transition to the loaded scene upon receipt.
- When scene change completes successfully, the loading screen must emit `LoadCompleted` signal.

## Goal

Provide a responsive loading experience that communicates progress while transitioning safely to the target scene.

## User Requirements

1. Players must see clear loading feedback while a target scene is being prepared.
2. Loading UI layout must remain readable, with centred status message and progress indicator.
3. Once loading completes, the progress bar must be hidden and a recentering instruction message must be displayed.
4. The loading screen must not transition to the loaded scene until the player has recentred their pose via the XR interface.

## Technical Requirements

1. The loading screen must expose `LoadSceneAsync(string scenePath)` as the public trigger for asynchronous loading.
2. Progress updates must consume threaded-loading status/progress values from `Godot.ResourceLoader`.
3. On load completion, the progress bar control must be hidden and a recentering instruction text control must be displayed.
4. The loading screen must subscribe to `XRManager.PoseRecentered()` signal before transitioning.
5. Upon receipt of `PoseRecentered()`, the loading screen must transition via `SceneTree.ChangeSceneToPacked` and emit `LoadCompleted`.

## In Scope

- Loading message and progress-bar layout requirements.
- Asynchronous packed-scene load orchestration through `LoadSceneAsync`.
- Post-load recentering instruction UX and `XRManager.PoseRecentered()` signal subscription.
- Completion transition and `LoadCompleted` signal contract.

## Out Of Scope

- Splash/branding animation sequences.
- Error-screen UX for unrecoverable load failures.
- Content-specific scene initialisation logic after transition.

## Acceptance Criteria

1. The loading screen displays a centred loading message above a centred progress bar.
2. `LoadSceneAsync(string scenePath)` initiates asynchronous loading of the requested scene resource.
3. Loading progress reflects threaded loader status/progress updates.
4. On successful load, the progress bar is hidden and a recentering instruction message is displayed.
5. The loading screen transitions to the loaded scene only after receiving `PoseRecentered()` from XRManager.
6. Scene transition uses `SceneTree.ChangeSceneToPacked` and emits `LoadCompleted`.
7. The specification defines both user-visible behaviour outcomes and technical implementation contracts.

## References

### Implementation

- @game/src/UI/LoadingScreen.cs

### Tests

- @integration-tests/src/UI/LoadingScreenIntegrationTests.cs

### Godot

Scenes:
- @game/assets/ui/loading_screen.tscn
