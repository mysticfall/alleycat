---
id: BODY-004
title: Eyes
---

# Eyes

## Requirement

The character body must expose eye movement and blinking capabilities that blend with
existing facial animations without relying on direct eye transform rotation, since the
reference character uses eye blend shapes instead of independent eye transform bones.

## Goal

Provide a reusable eye component system that:

- Exposes an optional target node representing where the eyes are currently looking.
- Drives horizontal and vertical eye rotation through blend shape animation parameters.
- Adds subtle saccade motion around the current gaze anchor without adding sight AI.
- Supports randomised blinking with configurable cadence.
- Integrates with AnimationTree partial blending analogous to hand pose setup.

## User Requirements

1. Eye look direction must be controllable via a target node reference.
2. Eye movement must use AnimationTree TimeSeek parameters, not direct transform rotation.
3. Blinking must occur at random intervals with configurable timing parameters.
4. Eye animations must blend with existing facial animations without overriding them.
5. Left and right eyes must move together as a unit.
6. The eyes must make bounded saccade movements around the active gaze anchor.

## Technical Requirements

1. Define `IEyes : IComponent` capability interface in `AlleyCat.Body.Eyes`:
   - `LookTarget: Node3D?` — optional target node the eyes are looking at.
   - `SetLookTarget(Node3D? target)` — sets the look target.
   - `ClearLookTarget()` — clears the look target.
2. Define `IEyesHolder : IComponentHolder` holder trait:
   - `TryGetEyes(out IEyes? eyes)` — resolves the eyes component.
   - `RequireEyes()` — returns the eyes component or throws if not found.
3. Implement `EyesBehaviour : Node, IEyes` in `AlleyCat.Body.Eyes`:
   - Accepts an `AnimationTree` reference or inherits from parent.
   - Exposes `LookTarget` as the assigned gaze anchor node.
   - Provides a protected target-resolution method for the world-space look point.
   - Resolves to the assigned `LookTarget` position when present.
   - Falls back to a point 1 metre directly in front of the eyes when no target is assigned.
   - Owns saccade anchor polling and offset state around the resolved look point.
4. Saccades are presentation movement around the active gaze anchor:
   - Poll the protected target-resolution method at a default 1-second interval.
   - Apply bounded offsets around the latest resolved anchor, not independent gaze selection.
   - Use constants or exported defaults for interval, speed, and amplitude tuning.
   - Keep tuning override-friendly so later emotional state can alter speed and amplitude.
5. Implement `EyesController` as the low-level AnimationTree output owner:
   - Accepts supplied world-space look points from `EyesBehaviour`.
   - Converts supplied world-space look points to horizontal and vertical TimeSeek values.
   - Owns look smoothing, look blend enforcement, and blink timing.
   - Does not resolve `Node3D` target nodes directly.
6. Eye movement uses `AnimationNodeTimeSeek` for both horizontal and vertical look
    animations:
    - Each look animation is normalised to 1 second duration.
    - Seek position 0.5 seconds represents the neutral (forward) eye position.
    - The implementation writes to the TimeSeek node's `seek_request` parameter
      rather than applying direct transform rotation.
7. Blinking is driven by a dedicated blink animation via `AnimationNodeOneShot` with:
   - Configurable minimum and maximum interval between blinks.
   - Random cadence within the configured range.
   - Configurable blink duration.
   - The implementation fires the OneShot node's `request` parameter rather than
     externally seeking the blink animation timeline.
8. Eye animation integrates into the AnimationTree as a partial blend, analogous to
   the hand pose partial blend in BODY-001. The eye blend runs in parallel with
   facial animations and does not override unrelated facial tracks.
9. The controller keeps horizontal and vertical look blend amounts enabled at runtime
   so inherited scene overrides cannot disable target-driven eye movement.
10. Imported character sources must have an `AnimationPlayer` after import; the import
    script creates an empty one when the source scene does not provide it.
11. Imported character sources generate or replace an `eyes` AnimationLibrary during
    import.
12. The generated `eyes` library must contain these animations:
    - `Eyes Blink`.
    - `Eyes Right Left`.
    - `Eyes Up Down`.
13. Generated blend-shape track paths must be discovered from the imported model topology
    relative to the model root and AnimationPlayer root.
14. If no recognised eye blend shapes exist, import creates an invisible placeholder mesh
    and no-op tracks so runtime validation can rely on the required eye animation contract.
15. Eye animation resources for imported characters must not depend on hard-coded
    reference-female mesh paths or a pre-authored reference-female `eyes.tres` asset.
16. Runtime character installation validates that the imported `eyes` library, required
    animations, and blend-shape track targets are present before enabling eye behaviour.
17. Player and NPC AnimationTree roots include the eye partial blend setup.

## In Scope

- `IEyes` component capability interface.
- `IEyesHolder` holder trait.
- `EyesBehaviour` Godot node facade.
- TimeSeek-driven eye movement (horizontal and vertical).
- TimeSeek-driven saccades anchored around the resolved look point.
- OneShot-driven randomised blinking with configurable cadence.
- AnimationTree partial blending for eyes.
- Import-time creation of a missing `AnimationPlayer`.
- Import-time generation or replacement of the `eyes` AnimationLibrary.
- Import-time generation of eye tracks from discovered eye blend shapes, or invisible
  placeholder/no-op tracks when no recognised eye blend shapes exist.
- Per-character AnimationTree integration.

## Out Of Scope

- Perception, sight AI, or independent gaze-selection logic.
- Visual landmark selection policy beyond a future hook owned by `EyesBehaviour`.
- Emotional-state policy that modifies saccade tuning.
- Eyebrow movement or expression changes.
- Lip-sync or mouth animation.
- Networked replication or multiplayer considerations.
- IK solver modifications for eye tracking.
- Eye collision with world geometry.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IEyes : IComponent` interface is defined with `LookTarget`, `SetLookTarget`, |
|    |                   | and `ClearLookTarget`. |
| 2  | Technical         | `IEyesHolder` defines `TryGetEyes` and `RequireEyes` methods. |
| 3  | Technical         | `EyesBehaviour` implements `IEyes` and delegates supplied look points to the |
|    |                   | controller without directly rotating eye transforms. |
| 4  | Technical         | `EyesBehaviour` exposes a protected look-point resolver that returns the |
|    |                   | assigned `LookTarget`, or a point 1 metre in front of the eyes as fallback. |
| 5  | User              | Setting a `LookTarget` causes the eyes to orient toward that target using |
|    |                   | TimeSeek-driven animation. |
| 6  | User              | Clearing the look target makes the eyes fall back to looking 1 metre forward. |
| 7  | User              | Bounded saccades move around the active gaze anchor without changing focus. |
| 8  | Technical         | Saccades poll the look-point resolver at the default 1-second interval and |
|    |                   | use default constants or exports for interval, speed, and amplitude. |
| 9  | Technical         | `EyesBehaviour` owns target-node resolution, fallback, future landmark hook, |
|    |                   | and saccade anchor/offset state. |
| 10 | Technical         | `EyesController` owns supplied-point to TimeSeek conversion, smoothing, blend |
|    |                   | enforcement, and blink timing; it does not resolve `Node3D` targets directly. |
| 11 | User              | Blinking occurs at randomised intervals within configured min/max range. |
| 12 | User              | Blink duration is configurable. |
| 13 | User              | Eye animations blend with facial animations without overriding unrelated |
|    |                   | facial tracks. |
| 14 | Technical         | Player and NPC AnimationTree roots include the eye partial blend setup. |
| 15 | Technical         | Import creates an empty `AnimationPlayer` when the imported character scene |
|    |                   | does not provide one. |
| 16 | Technical         | Imported characters provide an `eyes` AnimationLibrary with `Eyes Blink`, |
|    |                   | `Eyes Right Left`, and `Eyes Up Down`. |
| 17 | Technical         | Generated eye animation track paths are derived from discovered eye blend |
|    |                   | shapes relative to the imported model root and AnimationPlayer root. |
| 18 | Technical         | When no recognised eye blend shapes exist, import creates invisible |
|    |                   | placeholder/no-op tracks that satisfy runtime validation. |
| 19 | Technical         | Runtime installation rejects missing eye libraries, required animations, or |
|    |                   | invalid blend-shape track targets before enabling eye behaviour. |
| 20 | Technical         | Eye animation resources for imported characters do not depend on hard-coded |
|    |                   | reference-female mesh paths or hard-loading a reference-female `eyes.tres`. |
| 21 | Technical         | Implementation does not depend on perception, sight AI, collision, or network |
|    |                   | systems. |
| 22 | Technical         | Tests verify the eyes component is discoverable via `IEyesHolder`. |
| 23 | Technical         | Tests verify fallback target resolution and bounded saccade offsets around |
|    |                   | assigned and fallback gaze anchors. |
| 24 | Technical         | Tests verify that eye movement output writes only TimeSeek seek requests and |
|    |                   | never applies direct eye transform rotation. |
| 25 | Technical         | Tests verify that TimeSeek seek position 0.5 corresponds to neutral eye |
|    |                   | position (forward-facing). |
| 26 | Technical         | Tests verify that blink playback uses an `AnimationNodeOneShot` request, |
|    |                   | while horizontal and vertical look remain `AnimationNodeTimeSeek` nodes. |
| 27 | Technical         | Mirror-room tests verify that look blend overrides remain enabled at runtime. |
| 28 | User              | Visual verification confirms: (a) neutral eyes face forward at 0.5s seek, |
|    |                   | (b) directional look animates correctly for up/down/left/right, |
|    |                   | (c) saccades remain bounded around the anchor, and |
|    |                   | (d) blink animation opens and closes eyes. |

## References

- [BODY-001: Hands](../001-hands/index.md)
- [BODY-002: Character Physical Response System](../002-character-physical-response/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- `game/assets/characters/import/eye_animation_library_import.gd`
- `game/src/Body/Eyes/IEyes.cs`
- `game/src/Body/Eyes/IEyesHolder.cs`
- `game/src/Body/Eyes/EyesBehaviour.cs`
- `game/src/Body/Eyes/EyesController.cs`
- `game/src/Character/CharacterRuntimeSubsystemInstaller.cs`
