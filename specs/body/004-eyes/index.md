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
- Supports randomised blinking with configurable cadence.
- Integrates with AnimationTree partial blending analogous to hand pose setup.

## User Requirements

1. Eye look direction must be controllable via a target node reference.
2. Eye movement must use AnimationTree TimeSeek parameters, not direct transform rotation.
3. Blinking must occur at random intervals with configurable timing parameters.
4. Eye animations must blend with existing facial animations without overriding them.
5. Left and right eyes must move together as a unit.

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
   - Exposes `LookTarget` property that drives horizontal and vertical look animations.
   - Maps look direction to TimeSeek seek times over the 0.0 to 1.0 range.
4. Eye movement uses `AnimationNodeTimeSeek` for both horizontal and vertical look
   animations:
   - Each look animation is normalised to 1 second duration.
   - Seek position 0.5 seconds represents the neutral (forward) eye position.
   - The implementation writes to the TimeSeek node's `seek_request` parameter
     rather than applying direct transform rotation.
5. Blinking is driven by a dedicated blink animation via `AnimationNodeOneShot` with:
   - Configurable minimum and maximum interval between blinks.
   - Random cadence within the configured range.
   - Configurable blink duration.
   - The implementation fires the OneShot node's `request` parameter rather than
     externally seeking the blink animation timeline.
6. Eye animation integrates into the AnimationTree as a partial blend, analogous to
    the hand pose partial blend in BODY-001. The eye blend runs in parallel with
    facial animations and does not override unrelated facial tracks.
7. The controller keeps horizontal and vertical look blend amounts enabled at runtime
    so inherited scene overrides cannot disable target-driven eye movement.
8. Imported character sources generate an `eyes` AnimationLibrary during import.
9. The generated `eyes` library must contain these animations:
   - `Eyes Blink`.
   - `Eyes Right Left`.
   - `Eyes Up Down`.
10. Generated blend-shape track paths must be discovered from the imported model topology
    relative to the model root and AnimationPlayer root.
11. Eye animation resources for imported characters must not depend on hard-coded
    reference-female mesh paths or a pre-authored reference-female `eyes.tres` asset.
12. Runtime character installation validates that the imported `eyes` library, required
    animations, and blend-shape track targets are present before enabling eye behaviour.
13. Player and NPC AnimationTree roots include the eye partial blend setup.

## In Scope

- `IEyes` component capability interface.
- `IEyesHolder` holder trait.
- `EyesBehaviour` Godot node facade.
- TimeSeek-driven eye movement (horizontal and vertical).
- OneShot-driven randomised blinking with configurable cadence.
- AnimationTree partial blending for eyes.
- Import-time generation of the `eyes` AnimationLibrary from discovered eye blend
  shapes.
- Per-character AnimationTree integration.

## Out Of Scope

- Perception, sight AI, or gaze behaviour logic.
- Eyebrow movement or expression changes.
- Lip-sync or mouth animation.
- Networked replication or multiplayer considerations.
- IK solver modifications for eye tracking.
- Procedural eye target calculation or focus logic.
- Eye collision with world geometry.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IEyes : IComponent` interface is defined with `LookTarget`, `SetLookTarget`, |
|    |                   | and `ClearLookTarget`. |
| 2  | Technical         | `IEyesHolder` defines `TryGetEyes` and `RequireEyes` methods. |
| 3  | Technical         | `EyesBehaviour` implements `IEyes` and drives eye movement through |
|    |                   | AnimationNodeTimeSeek parameters, not transform rotation. |
| 4  | User              | Setting a `LookTarget` causes the eyes to orient toward that target using |
|    |                   | TimeSeek-driven animation. |
| 5  | User              | Clearing the look target stops the directed eye movement. |
| 6  | User              | Blinking occurs at randomised intervals within configured min/max range. |
| 7  | User              | Blink duration is configurable. |
| 8  | User              | Eye animations blend with facial animations without overriding unrelated |
|    |                   | facial tracks. |
| 9  | Technical         | Player and NPC AnimationTree roots include the eye partial blend setup. |
| 10 | Technical         | Imported characters provide an `eyes` AnimationLibrary with `Eyes Blink`, |
|    |                   | `Eyes Right Left`, and `Eyes Up Down`. |
| 11 | Technical         | Generated eye animation track paths are derived from discovered eye blend |
|    |                   | shapes relative to the imported model root and AnimationPlayer root. |
| 12 | Technical         | Runtime installation rejects missing eye libraries, required animations, or |
|    |                   | invalid blend-shape track targets before enabling eye behaviour. |
| 13 | Technical         | Eye animation resources for imported characters do not depend on hard-coded |
|    |                   | reference-female mesh paths or hard-loading a reference-female `eyes.tres`. |
| 14 | Technical         | Implementation does not depend on perception or sight AI systems. |
| 15 | Technical         | Tests verify the eyes component is discoverable via `IEyesHolder`. |
| 16 | Technical         | Tests verify that TimeSeek seek position 0.5 corresponds to neutral eye |
|    |                   | position (forward-facing). |
| 17 | Technical         | Tests verify that blink playback uses an `AnimationNodeOneShot` request, |
|    |                   | while horizontal and vertical look remain `AnimationNodeTimeSeek` nodes. |
| 18 | Technical         | Mirror-room tests verify that look blend overrides remain enabled at runtime. |
| 19 | User              | Visual verification confirms: (a) neutral eyes face forward at 0.5s seek, |
|    |                   | (b) directional look animates correctly for up/down/left/right, |
|    |                   | (c) blink animation opens and closes eyes. |

## References

- [BODY-001: Hands](../001-hands/index.md)
- [BODY-002: Character Physical Response System](../002-character-physical-response/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- `game/assets/characters/import/eye_animation_library_import.gd`
- `game/src/Body/Eyes/IEyes.cs`
- `game/src/Body/Eyes/IEyesHolder.cs`
- `game/src/Body/Eyes/EyesBehaviour.cs`
- `game/src/Character/Runtime/CharacterRuntimeSubsystemInstaller.cs`
