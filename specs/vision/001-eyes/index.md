---
id: VISION-001
title: Eyes
legacy_id: BODY-004
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
- Adds subtle saccade motion around the current gaze anchor without owning semantic visual interpretation.
- Supports randomised blinking with configurable cadence.
- Integrates with AnimationTree partial blending analogous to hand pose setup.
- Periodically publishes a synchronous immutable survey of authored visual subjects visible to the character.

## User Requirements

1. Eye look direction must be controllable via a target node reference.
2. Eye movement must use AnimationTree TimeSeek parameters, not direct transform rotation.
3. Blinking must occur at random intervals with configurable timing parameters.
4. Eye animations must blend with existing facial animations without overriding them.
5. Left and right eyes must move together as a unit.
6. The eyes must make bounded saccade movements around the active gaze anchor.
7. Systems can discover authored visual cues that identify meaningful points on a character or other visual subject.
8. A visual cue can describe itself using the observing character's eyes and, when applicable, its containing visual
   subject, without requesting completed context.
9. Whole-character cues provide character-specific appearance descriptions while allowing shared character templates
   to use placeholder text.
10. A character can sense visible subjects without changing its current gaze target or saccade behaviour.
11. Only cues within the character's field of view, distance limit, and unobstructed line of sight are reported.
12. Invalid authored cue ownership fails clearly when its provider is published or explicitly refreshed.
13. NPC perception can inspect visible subjects periodically without creating routine visual memories or changing gaze
    and eye presentation.

## Technical Requirements

1. Define `IVision`, `IHasVision`, `VisualSurveyPercept`, and all visual-cue contracts directly in
   `AlleyCat.Vision`; it is the only vision contract namespace. `IVision : ISense` exposes:
   - `LookTarget: Node3D?` — optional target node the eyes are looking at.
   - `SetLookTarget(Node3D? target)` — sets the look target.
   - `ClearLookTarget()` — clears the look target.
2. Define `IHasVision : IComponentHolder` holder trait:
    - `TryGetVision(out IVision? vision)` — resolves the vision component.
    - `RequireVision()` — returns the vision component or throws if not found.
3. Implement `EyesBehaviour : Node, IVision` directly in `AlleyCat.Vision`:
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
    the hand pose partial blend in INTR-003. The eye blend runs in parallel with
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
18. Define the visual-cue contracts directly in `AlleyCat.Vision`:
    - `IProvidesVisualCues` exposes its authoritative, published, read-only `VisualCues` collection of owned
      `VisualCue` instances.
    - `IVisualSubject : IIdentifiable, IProvidesVisualCues` represents an identifiable subject that owns discoverable
      cues. Its `IIdentifiable` identity semantics are authoritatively defined by
      [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md). `IVisualObserver` must not
      exist.
19. `VisualCue` is an abstract `Node3D` base that supports Godot authoring and exports:
    - A non-empty `ID` that is ordinally unique within its `IProvidesVisualCues` provider.
    - A finite, non-negative relative `Prominence`, defaulting to `1`; `0` disables the cue and there is no fixed upper
      bound.
    - Cue-local `VisualBounds` used exclusively to determine representative visual-scan geometry.
20. `VisualCue` defines `Vector3 SampleGlobalPosition()` and
     `string Describe(ISceneContext scene, IHasVision observer)`.
21. `Describe` requires a non-null eyes holder. It must not call `GetContext`, compose completed context, or accept an
    `IContextual` or `IContextSource` input.
22. `Describe` builds its own local template root from the supplied scene and eyes holder. When present, it adds the
    nearest `IVisualSubject` ancestor as `subject`; a cue without subject ancestry is valid and omits `subject`.
23. `StaticVisualCue` is the concrete fixed-description implementation: its exported authored `Description` property
    is template-backed, and `Describe` compiles and renders it through the existing `ITemplate` system.
    `SampleGlobalPosition()` returns `GlobalPosition` regardless of the assigned `VisualBounds`.
24. On initialisation/publication and each explicit provider refresh, visual-cue providers validate non-empty IDs,
    finite non-negative prominence, ordinal ID uniqueness, and that each published cue has that provider as its nearest
    `IProvidesVisualCues` ancestor. Invalid ownership fails at that boundary; no standalone validation helper is
    prescribed.
25. A provider's published `VisualCues` collection is its authoritative owned list. Visual-cue topology is immutable
    after publication until that provider explicitly refreshes it. The nearest `IVisualSubject` ancestor is the
    scan-result subject.
26. The shared reference female and male character templates each author one whole-character cue with ID `body` at
    `Head/BodyVisualCue`, a sibling of the existing `Viewpoint`. Its generic template may contain placeholder
    description content.
27. Ally NPC, Ally player, and Vadim character assets override the `body` cue template with character-specific
    appearance descriptions.
28. `IVision` must not expose a public `Scan()` operation. As an `ISense`, it declares exactly
    `VisualSurveyPercept` and publishes percepts synchronously through `Perceived`.
29. `EyesBehaviour` owns its polling lifecycle. Its exported survey interval must be finite and positive; invalid
    authored or runtime values fail before activation. The final minimum and default remain tunable.
30. `EyesBehaviour` performs at most one survey per frame. A delayed frame performs one survey without catch-up and
    starts the next interval from that survey.
31. `EyesBehaviour` directly queries `SceneTree.GetNodesInGroup("VisualSubjects")` for survey discovery. It owns the
    strict discovery, member-validation, and authoring-failure boundary: every member must implement `IVisualSubject`,
    and a non-`IVisualSubject` member is an authoring error that fails the survey immediately. SCN-001 defines only the
    `VisualSubjects` group-membership semantics.
32. A survey snapshots `VisualSubjects` membership synchronously before evaluating each subject's valid published cue
    collection. It excludes the observer's own subject and cues whose effective `Prominence` is `0`; it publishes no
    subject identity for a subject with no visible cues.
33. `EyesBehaviour` owns an `EyeOrigin` authoring reference for survey geometry. Its default field-of-view cone uses
    horizontal and vertical half angles of 60° and 45° respectively; these defaults remain export-overrideable.
34. Every `VisualCue` has cue-local `VisualBounds` authoring. `VisualBounds` subclasses support point, sphere, and
    oriented-box forms and alone determine the representative geometry used for survey distance, cone, and visibility
    evaluation; they do not alter a `StaticVisualCue` description or origin-based `SampleGlobalPosition()` contract.
35. Every `VisualCue` exposes `MaxVisibleDistance`. A value of `0` means unlimited distance; a positive value limits
    the cue to that world-space distance from `EyeOrigin`.
36. Survey visibility evaluation is separate from gaze sampling and selection. Acquisition must neither change
    `LookTarget` nor select a gaze anchor; `SampleGlobalPosition()` remains the representative cue-position API for
    consumers that require gaze or presentation sampling.
37. Visibility tests use representative sample points appropriate to the cue's `VisualBounds`, rather than treating a
    bounded cue as visible or hidden from one arbitrary point only. A cue is visible when at least one valid
    representative sample passes its distance, cone, and occlusion tests.
38. `VisionOccluder` identifies geometry that can block a visual survey. Each representative-sample visibility test
    casts a ray from `EyeOrigin` to the sample point and treats only a `VisionOccluder` hit before the endpoint as
    occlusion. Hits within a configurable endpoint tolerance do not occlude the cue.
39. Survey acquisition assumes valid published cue collections and selects visibility only; it must not reconcile
    ownership, filter nested-provider leaks, or refresh cue topology. Description and context composition remain
    separate operations.
40. Each survey publishes one `VisualSurveyPercept` containing only a producer-owned immutable ordered snapshot of
    canonical visible-subject `FullId` values. It contains no cues, descriptions, observations, or live subjects.
41. Routine surveys must not call `VisualCue.Describe`, produce visual observations, select gaze, change `LookTarget`,
    or otherwise alter saccade, blink, or eye-presentation state.

## In Scope

- `IVision` component capability interface.
- `IHasVision` holder trait.
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
- Visual subject, visual-cue provider, ownership, and visual-cue contracts under `AlleyCat.Vision`.
- Static cue origin sampling, fixed template-backed local descriptions, optional nearest-subject input, provider
  publication/refresh validation, and immutable published cue topology.
- Authored whole-character `body` cues and character-specific appearance overrides.
- Eyes-owned periodic `VisualSurveyPercept` publication for discovered non-self subjects with visible cues.
- `EyesBehaviour`-owned strict `VisualSubjects` querying, member validation, authoring failure, scan filtering,
  `EyeOrigin` cone evaluation, and `VisionOccluder` ray tests.
- Cue-local `VisualBounds`, per-cue distance limits, and representative visibility sampling.

## Out Of Scope

- Attention, Mind interpretation, or independent gaze-selection logic.
- Visual landmark selection policy beyond a future hook owned by `EyesBehaviour`.
- Automatic visual-cue selection or gaze movement towards cues.
- Emotional-state policy that modifies saccade tuning.
- Eyebrow movement or expression changes.
- Lip-sync or mouth animation.
- Networked replication or multiplayer considerations.
- IK solver modifications for eye tracking.
- Physical eye collision or physics-response behaviour; scan-only `VisionOccluder` ray tests remain required.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IVision`, `IHasVision`, `VisualSurveyPercept`, and visual-cue contracts live |
|    |                   | directly in `AlleyCat.Vision`; `IVision : ISense` defines `LookTarget`, |
|    |                   | `SetLookTarget`, and `ClearLookTarget`. |
| 2  | Technical         | `IHasVision` defines `TryGetVision` and `RequireVision` methods. |
| 3  | Technical         | `EyesBehaviour` implements `IVision` and delegates supplied look points to the |
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
| 21 | Technical         | Implementation does not depend on Mind, physical eye-collision response, or |
|    |                   | network systems; required survey-only occlusion rays are permitted. |
| 22 | Technical         | Tests verify the vision component is discoverable via `IHasVision`. |
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
| 29 | User              | Systems can discover an authored whole-character `body` cue and obtain an observer-relative |
|    |                   | appearance description for Ally NPC, Ally player, and Vadim. |
| 30 | User              | Shared female and male templates provide a usable `body` cue even when its |
|    |                   | description is placeholder text. |
| 31 | Technical         | `IProvidesVisualCues` and `IVisualSubject` have the authoritative ownership, |
|    |                   | `IIdentifiable`, and read-only collection contracts specified in Technical |
|    |                   | Requirement 18; `IVisualObserver` does not exist. |
| 32 | Technical         | `VisualCue` and `StaticVisualCue` expose the authoring, sampling, prominence, |
|    |                   | and required-observer description contracts specified in Technical |
|    |                   | Requirements 19–24, including `Describe(ISceneContext scene, IHasVision observer)`, |
|    |                   | the exported `StaticVisualCue.Description` property, and origin sampling independent of |
|    |                   | bounds. |
| 33 | Technical         | Description rendering builds a local root without `GetContext`, uses the supplied |
|    |                   | `IHasVision`, and supplies the nearest `IVisualSubject` ancestor only when present. |
|    |                   | Missing subject ancestry remains valid. |
| 34 | Technical         | Provider publication or explicit refresh rejects empty cue IDs, non-finite |
|    |                   | or negative prominence, ordinally duplicate IDs, and cues not owned by that |
|    |                   | nearest provider; it accepts disabled prominence `0` and finite values above |
|    |                   | `1`. Published cue topology remains immutable until explicit refresh. |
| 35 | Technical         | Shared female and male templates each author exactly one `body` `StaticVisualCue` |
|    |                   | at `Head/BodyVisualCue`, a sibling of the existing `Viewpoint`; Ally NPC, Ally player, |
|    |                   | and Vadim supply character-specific template overrides. |
| 36 | Technical         | Automated tests verify cue discovery, sampling, description context rendering, |
|    |                   | authoring, overrides, and validation without requiring screenshot or |
|    |                   | visual-rendering acceptance. |
| 37 | User              | A character can sense currently visible authored subjects without changing its |
|    |                   | look target, blink cadence, or saccade anchor. |
| 38 | User              | Cues outside the field of view or positive distance limit, behind an occluder, |
|    |                   | on the observing subject, or disabled by zero prominence are not reported. |
| 39 | Technical         | `IVision : ISense` declares only `VisualSurveyPercept`, publishes synchronously, |
|    |                   | and exposes no public `Scan()` operation. |
| 40 | Technical         | `EyesBehaviour` owns a finite positive interval, performs |
|    |                   | at most one survey per frame, performs no delayed-frame catch-up, and rejects |
|    |                   | invalid authored or runtime cadence before activation. |
| 41 | Technical         | Survey evaluation excludes the observer's own subject and zero-prominence cues. |
| 42 | Technical         | `EyesBehaviour` evaluates from `EyeOrigin` using export-overrideable 60° |
|    |                   | horizontal and 45° vertical half-angle defaults. |
| 43 | Technical         | Cues support cue-local point, sphere, and oriented-box `VisualBounds`, plus |
|    |                   | `MaxVisibleDistance`, where `0` means unlimited distance. |
| 44 | Technical         | Visibility sampling is separate from gaze sampling and uses representative |
|    |                   | bounds samples; a passing sample must satisfy distance, cone, and ray tests. |
| 45 | Technical         | Ray tests recognise only pre-endpoint `VisionOccluder` hits as occlusion and |
|    |                   | honour a configurable endpoint tolerance. |
| 46 | Technical         | Tests verify provider-side nearest-provider ownership validation and clear |
|    |                   | publication or refresh failure for invalid cue ownership; surveys consume only |
|    |                   | valid published collections and perform visibility selection without ownership |
|    |                   | reconciliation or nested-leak filtering. |
| 47 | Technical         | Tests verify visibility selection, cue description, and CTX-001 completed-context |
|    |                   | aggregation remain separate operations. |
| 48 | User              | Periodic NPC visual perception can notice all visible subjects without creating routine |
|    |                   | visual history or changing gaze and eye presentation. |
| 49 | Technical         | Each survey emits one producer-owned immutable ordered snapshot containing only |
|    |                   | canonical visible-subject `FullId` values, never descriptions or observations. |
| 50 | Technical         | Tests verify surveys never invoke `VisualCue.Describe`, select gaze, change |
|    |                   | `LookTarget`, or alter saccades, blink cadence, or other eye presentation. |

## References

- [INTR-003: Hands](../../interaction/003-hands/index.md)
- [CHAR-003: Character Physical Response](../../character/003-physical-response/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [CTX-001: Contextual Information API](../../context/001-contextual-information-api/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- `game/assets/characters/import/eye_animation_library_import.gd`
- `game/src/Vision/IVision.cs`
- `game/src/Vision/IHasVision.cs`
- `game/src/Vision/VisualSurveyPercept.cs`
- `game/src/Vision/EyesBehaviour.cs`
- `game/src/Vision/EyesController.cs`
- `game/src/Character/CharacterRuntimeSubsystemInstaller.cs`
