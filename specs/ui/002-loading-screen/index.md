---
id: UI-002
---

# Loading Screen

## Requirement

This specification defines a full-screen overlay displayed during
scene transitions, providing progress feedback and pose
recentring before handing control to the loaded scene.

## Goal

Deliver a blocking loading overlay that keeps the player informed,
asks for XR pose recentring, and transitions only when ready.

## User Requirements

1. Players must see clear loading feedback while a target scene is
   being prepared.
2. Loading UI layout must remain readable, with centred status
   message and progress indicator.
3. Once loading completes, the progress bar must be hidden and a
   recentring instruction message must be displayed.
4. The loading screen must not transition to the loaded scene until the
   player has recentred their pose via the XR interface.

## Technical Requirements

1. The loading screen must expose `LoadSceneAsync(string scenePath)` as
   the public trigger for asynchronous loading.
2. Progress updates must consume threaded-loading status and progress
   values from `Godot.ResourceLoader`.
3. On load completion, the progress bar control must be hidden and a
   recentring instruction text control must be displayed.
4. The loading screen must subscribe to `XRManager.PoseRecentered()`
   signal before transitioning.
5. Upon receipt of `PoseRecentered()`, the loading screen must
   transition via `SceneTree.ChangeSceneToPacked` and emit
   `LoadCompleted`.

## In Scope

- Full-screen loading overlay for scene transitions.
- Asynchronous resource loading with progress reporting.
- XR pose recentring prompt and blocking transition.

## Out Of Scope

- Splash or branding animation sequences.
- Error-screen UX for unrecoverable load failures.
- Content-specific scene initialisation logic after transition.

## Acceptance Criteria

1. The loading screen displays a centred loading message above a
   centred progress bar.
2. `LoadSceneAsync(string scenePath)` initiates asynchronous loading
   of the requested scene resource.
3. Loading progress reflects threaded loader status and progress
   updates.
4. On successful load, the progress bar is hidden and a recentring
   instruction message is displayed.
5. The loading screen transitions to the loaded scene only after
   receiving `PoseRecentered()` from XRManager.
6. Scene transition uses `SceneTree.ChangeSceneToPacked` and emits
   `LoadCompleted`.

## References

- @game/src/UI/LoadingScreen.cs
- @integration-tests/src/UI/LoadingScreenIntegrationTests.cs
- @game/assets/ui/loading_screen.tscn