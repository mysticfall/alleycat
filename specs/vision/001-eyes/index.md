---
id: VISION-001
title: Eyes
legacy_id: BODY-004
---

# Eyes

## Requirement

The character body must expose eye movement and blinking capabilities that blend with existing
facial animations. The default gaze backend drives authored eye blend shapes through
AnimationTree parameters, matching the reference character, which uses eye blend shapes instead
of independent eye transform bones. Characters whose eye meshes lack gaze blend shapes may
instead author the optional transform-driven gaze backend, which rotates the eye nodes directly
while blinking continues through the blendshape path.

## Goal

Provide a reusable eye component system that:

- Exposes an optional `VisualCue` representing where the eyes are currently looking.
- Drives horizontal and vertical eye rotation through blend shape animation parameters by
  default, with an optional transform-driven backend for characters whose eye meshes lack gaze
  blend shapes.
- Adds subtle saccade motion around the current gaze anchor without owning semantic visual interpretation.
- Supports randomised blinking with configurable cadence in both backends.
- Integrates with AnimationTree partial blending analogous to hand pose setup.
- Periodically publishes a synchronous immutable survey of authored visual subjects visible to the character.
- Publishes immutable visual-family percepts when the assigned cue changes so Mind may inspect focused subjects.
- Keeps eye motion in step with head turns during transit, without changing gaze selection or the eye clamps.

## User Requirements

1. Eye look direction must be controllable via an authored visual-cue reference.
2. By default, eye movement must use AnimationTree TimeSeek parameters; characters whose eye
   meshes lack gaze blend shapes must instead use transform-driven eye-node rotation.
3. Blinking must occur at random intervals with configurable timing parameters.
4. Eye animations must blend with existing facial animations without overriding them.
5. Left and right eyes must move together as a unit.
6. The eyes must make bounded, finite saccade movements around the active gaze anchor and return to fixation.
7. The gaze backend is inferred from authoring rather than selected explicitly: leaving both
   eye-node references unassigned keeps the default blendshape backend, assigning both selects
   the transform backend, and assigning exactly one is invalid authoring that must fail clearly.
8. In the transform backend, both eyes look together through the same bounded, smoothed
   rotation, and each eye's authored neutral pose is preserved so gaze movement never
   accumulates drift.
9. Transform-backend characters keep the same configurable random blinking behaviour while
   their gaze moves.
10. Systems can discover authored visual cues that identify meaningful points on a character or other visual subject.
11. A visual cue can describe itself using the observing character's eyes and, when applicable, its containing visual
    subject, without drawing on character render context.
12. Whole-character cues provide character-specific appearance descriptions while allowing shared character templates
    to use placeholder text.
13. A character can sense visible subjects without changing its current gaze target or saccade behaviour.
14. Only cues within the character's field of view, distance limit, and unobstructed line of sight are reported.
15. Invalid authored cue ownership fails clearly when its provider is published or explicitly refreshed.
16. NPC perception can inspect visible subjects periodically without creating routine visual memories or changing gaze
    and eye presentation.
17. Effective focus changes can trigger a durable description of the newly focused visual subject; clearing focus or
    repeating the same cue creates no description by itself.
18. During fixation, both eyes continuously follow the shared gaze direction towards the target's current position
    as the target, head or character moves, without independent vergence. Beyond anatomical reach, the eyes aim in
    the constrained direction allowed by their rotation limits;
    unreachable target residual is not an eye defect. Head motion must not cause recurring catch-up lag.
19. Both eyes must acquire a genuinely new target within 0.4 s of actual assignment, then maintain fixation.
    This brief acquisition transition is separate from continuous fixation. Intentional saccades may briefly interrupt
    fixation; their random trajectory need not be exact.

## Technical Requirements

1. Define `IVision`, `IHasVision`, `IVisualPercept`, `VisualSurveyPercept`, `LookTargetChangedPercept`, and all
   visual-cue contracts directly in `AlleyCat.Vision`; it is the only vision contract namespace.
   `IVision : ISense<IVisualPercept>` preserves the non-generic `ISense` event bridge and exposes:
   - `LookTarget: VisualCue?` — optional authored cue the eyes are looking at.
   - `SetLookTarget(VisualCue? target)` — sets or clears the look target.
   - `ClearLookTarget()` — clears the look target.
2. Define `IHasVision : IComponentHolder` holder trait:
    - `TryGetVision(out IVision? vision)` — resolves the vision component.
    - `RequireVision()` — returns the vision component or throws if not found.
3. Implement `EyesBehaviour : Node, IVision` directly in `AlleyCat.Vision`:
   - Accepts an `AnimationTree` reference or inherits from parent.
     - Exposes `LookTarget` as the assigned visual-cue gaze anchor.
     - Provides a protected target-resolution method for the world-space look point.
      - Resolves the live assigned `LookTarget` position on every gaze evaluation when present.
     - Falls back to a point 1 metre directly in front of the eye origin, resolved from the current eye-origin
       transform rather than a cached world position.
      - Owns live anchor resolution and independently timed saccade offset state around the resolved look point.
     - Remains target-policy neutral: it must not select a target or own an attention-gaze hook. AI-007 is the
       separately composed post-attention consumer that may call `IVision.SetLookTarget` or `IVision.ClearLookTarget`.
4. Saccades are presentation movement around the active gaze anchor:
    - Resolve the protected target-resolution method on every gaze evaluation, independently of random saccade
      timing; with no live target, resolve the fallback from the current head-relative eye origin.
    - Apply bounded, finite offsets around the live anchor and return to fixation, without independent gaze selection.
    - Disable saccades explicitly in precision fixation tests. Test their bounds, finiteness, return to fixation and
      independence from target refresh separately; no exact random trajectory or new biological model is required.
    - Use constants or exported defaults for interval, speed, and amplitude tuning.
    - Keep tuning override-friendly so later emotional state can alter speed and amplitude.
5. Implement `EyesController` as the low-level AnimationTree output owner:
   - Accepts supplied world-space look points from `EyesBehaviour`.
   - In blendshape mode, converts supplied world-space look points to horizontal and vertical
     TimeSeek values.
   - In transform mode, converts the shared gaze angle into clamped, smoothed eye-node rotation
     (Technical Requirements 45–49).
   - Owns look smoothing, look blend enforcement or suppression depending on the active backend,
     and blink timing.
   - Does not resolve look-target `Node3D` nodes directly; transform-mode eye nodes are supplied
     as authored references.
6. In the default blendshape backend, eye movement uses `AnimationNodeTimeSeek` for both
   horizontal and vertical look animations:
     - Each look animation is normalised to 1 second duration.
     - Seek position 0.5 seconds represents the neutral (forward) eye position.
     - The implementation writes to the TimeSeek node's `seek_request` parameter rather than
       applying direct transform rotation.
7. Blinking is driven by a dedicated blink animation via `AnimationNodeOneShot` with:
    - Configurable minimum and maximum interval between blinks.
    - Random cadence within the configured range.
    - Configurable blink duration.
    - The implementation fires the OneShot node's `request` parameter rather than
      externally seeking the blink animation timeline.
8. Eye animation integrates into the AnimationTree as a partial blend, analogous to
    the hand pose partial blend in INTR-003. The eye blend runs in parallel with
   facial animations and does not override unrelated facial tracks.
9. In blendshape mode, the controller keeps horizontal and vertical look blend amounts enabled
   at runtime so inherited scene overrides cannot disable target-driven eye movement. In
   transform mode, the controller instead holds both look blends at `0`, as defined by Technical
   Requirement 49.
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
     - `IVisualSubject : IIdentifiable, IProvidesVisualCues, ISpatial` represents an identifiable subject that owns
       discoverable cues. Its `IIdentifiable` identity semantics are authoritatively defined by
       [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md); its `ISpatial` base is defined
       by Technical Requirement 44. `IVisualObserver` must not exist.
19. `VisualCue` is an abstract `Node3D` base that supports Godot authoring and exports:
     - A non-empty `ID` that is ordinally unique within its `IProvidesVisualCues` provider.
     - A finite, non-negative relative `Prominence`, defaulting to `1`; `0` disables the cue and there is no fixed upper
       bound.
     - Cue-local `VisualBounds` used exclusively to determine representative visual-scan geometry.
20. `VisualCue` defines `Vector3 SampleGlobalPosition()` and the asynchronous description
     operation `ValueTask<string> Describe(ISceneContext scene, IHasVision observer)`.
21. `Describe` requires a non-null eyes holder. It must not compose character render context or accept render-context
     inputs; eyes and visual inspection never feed AI-003 render-context composition.
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
28. `IVision` must not expose a public `Scan()` operation. Its exact concrete `PerceptTypes` are
     `VisualSurveyPercept` and `LookTargetChangedPercept`; both implement `IVisualPercept`, and publication uses the
     synchronous non-generic `ISense.Perceived` bridge.
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
42. An effective cue-identity change publishes exactly one immutable
     `LookTargetChangedPercept(previous, current)`. Assigning the current cue again publishes none. Clearing an active
     cue publishes `current -> null`, while clearing an already null target publishes none.
43. EyesBehaviour owns applied target state and transition sensing but no target-selection policy. Focused inspection
     is owned by AI-006 perception and must validate cue lifetime and subject association before awaiting `Describe`.
44. `AlleyCat.Core.ISpatial` is the minimal read-only spatial trait consumed through `IVisualSubject` inheritance:
     - It exposes exactly one member: `Transform3D GlobalTransform { get; }`, the provider's current world-space
       transform.
     - It lives in `AlleyCat.Core`, so Vision may depend on it under the AI-006 dependency direction without adding a
       Sense or Mind dependency.
     - It is contract-only: Vision owns no spatial sampling, polling, or interpretation policy around it; perceiver-side
       geometry is owned by AI-006 perception faculties.
45. Transform-backend authoring uses two separate exported `Node3D` references on `EyesBehaviour`:
      left eye and right eye. The active gaze backend is inferred from these assignments, never
      selected by an enum or flag:
      - Neither reference assigned → blendshape backend (default).
      - Both references assigned → transform backend.
      - Exactly one reference assigned → invalid authoring that fails clearly at activation time
        and is rejected by the runtime character installer validation before eye behaviour is
        enabled.
46. Gaze output ownership is exclusive while the character runtime is active: the blendshape
      backend is the sole writer of look blends and TimeSeek seek requests, and the transform
      backend is the sole writer of eye-node rotation. Authored `AnimationPlayer` transform
      tracks on transform-mode eyes would conflict with this ownership and must not be authored
      for them.
47. The transform backend captures each eye's authored neutral local transform — position,
       orientation, and scale — when transform mode activates. Every frame's rotation is rebuilt
       from that neutral transform, so consecutive gaze updates never accumulate drift. Arbitrary
       authored local offsets and orientations are supported, and the two eyes do not need to
       share the same immediate parent. Positive non-uniform scale on `EyeOrigin`, eye parents,
       and eyes is supported; negative scale and skew are unsupported transform configurations
       for transform-mode eye authoring.
48. The transform backend shares the blendshape backend's `EyeOrigin`-frame yaw/pitch angle
      calculation, limits, smoothing, fallback gaze, behind-target handling, and saccade
      behaviour; it introduces no new target-selection rules. Direction conversion is
      rotation-only with `-Z` forward and `+Y` up. The same clamped, smoothed yaw/pitch delta
      is transferred into each eye's current parent frame and applied identically to both eyes,
       giving coordinated movement with no independent vergence or convergence. The normative geometry is defined in
       [Shared-Direction Geometry](#shared-direction-geometry); bilateral precision does not add per-eye targeting.
49. The transform backend suppresses only the horizontal and vertical blendshape gaze look
      blends by writing `0`; blink cadence, duration, time scale, and `AnimationNodeOneShot`
      requests continue unchanged. The AnimationTree, `EyeOrigin`, eye animation library, and
      blink tracks therefore remain required in both backends, because blinking depends on
      them.
50. Eye–head turn synchronisation applies to both transform and blendshape backends: gaze composition must consume
    the same solved head pose that the renderer presents, never a stale pose from an earlier frame. Verify actual
    presented eye output, not only a transform callback or a TimeSeek write after animation has already consumed it.
    Retain ordinary post-solve transform composition. Before changing blendshape scheduling, investigate native
    animation→solve→display timing in a minimal representative fixture and independently review a bounded proposal
    with engine evidence. Wider animation ownership or architecture changes require approval; transform-only success
    cannot close this requirement. This gate does not prescribe an unproved blendshape scheduling implementation.
    Advance temporal state once only for the active binding. Recomposition-only zero-delta callbacks must not advance
    smoothing, blink/saccade clocks or random state. Deferred execution validates active lifetime, required Godot object
    validity and binding generation; teardown/rebind invalidates pending work so old bindings receive no writes.
51. With saccades disabled, each presented eye's direct angular alignment error against its independently derived
    constrained direction must be at most 0.5° during fixation. Derive that direction from the current live target,
    current solved head/eye-origin geometry and immutable authored optical references, with the existing 35° horizontal
    / 25° vertical clamps. Use [Shared-Direction Geometry](#shared-direction-geometry), including coupled axes;
    do not use the production gaze helper to compute the expected answer. Measure error against the constrained
    direction, not unreachable target residual. This is the fixation acceptance metric.
    A genuinely new target's acquisition onset must occur within 2 presented frames of assignment, with overshoot
    at most 4°. Keep eye smoothing 0.08 for acquisition, separate from current-head compensation; ongoing target,
    head or character movement and repeated same-target assignment must not restart acquisition or excuse fixation lag.
    Gaze selection and yaw-dispersion breadth remain unchanged. Head centring within 0.8 s remains owned by AI-009;
    it is not an eye-acquisition completion deadline. These bounds and complete observation windows require explicit
    user approval supported by measurements to change. Acquisition completion must satisfy the decision below.
52. Acceptance measures both eyes simultaneously from immutable authored neutral references, independent of controller
    outputs. Verify optical-forward coordinate conventions; do not assume node-forward is optical-forward. Live neutral
    recapture is diagnostic only and cannot zero acceptance error. Setup timeout fails closed. Retain a pre-assignment
    reference, exact assignment timestamps, [presentation ordering](#assignment-and-presentation-ordering) and complete
    post-draw baseline,
    acquisition, transit, centring and settled windows in both isolated and migrated transit suites. Response and
    acquisition-completion and centring deadlines start at actual assignment, not a later sample. These frozen
    deadlines are expressed in engine time at the authored target cadence — equivalently, presented frames at the
    authored fps — measured from actual assignment, never wall-clock elapsed time. That clock basis is a measurement
    convention, not product scheduling grace: the product must still meet each deadline at its operating cadence, and
    slow test-environment rendering (for example software rendering under a virtual framebuffer) affects the
    frame-to-wall-clock mapping only, never the bound. Observe acquisition from assignment; never wait for
    software-declared success to begin recording or discard a failed interval.
    Cover fixed and moving targets,
    head compensation, mixed yaw/pitch and out-of-range directions in both backends. Validate direct-error sensitivity
    with independent geometry, stale frames, drift across the 0.5° boundary, acquisition-onset/completion/overshoot
    boundaries and settled recurrence. Deliberately offset only one eye beyond tolerance and prove bilateral detection.
    Replay the assignment/presentation boundaries below; no eligible presentation may be skipped or pre-assignment
    presentation counted towards onset.

### Shared-Direction Geometry

This is the normative independent calculation for TR48/51–52 in both backends. All rotations below are proper,
rotation-only bases; products apply rightmost first. Positions retain world-space distance, but scale must not distort
direction angles. `O` is the current solved shared `EyeOrigin` position and `R` its rotation into world space.
`T` is the live target position. Neither physical eye's position replaces `O`: near-target vergence is not required.

1. Compute `(x, y, z) = inverse(R) * (T - O)`, in the shared frame with `-Z` forward, `+X` right and `+Y` up.
   For a nonzero direction, yaw `h = atan2(x, -z)` is positive right; pitch
   `v = atan2(y, sqrt(x² + z²))` is positive up. Extract both from the original direction before clamping either.
2. Clamp independently: `hc = clamp(h, -35°, 35°)` and `vc = clamp(v, -25°, 25°)`.
   Reconstruct `d = (sin(hc) cos(vc), sin(vc), -cos(hc) cos(vc))`.
   Equivalently, `D = Ry(-hc) Rx(vc)` and `d = D * (0, 0, -1)`: pitch first, then yaw, not reversed.
   `R*d` is the shared constrained request, not a separate ray from each physical eye to `T`.
3. Behind targets retain their full signed yaw before clamping; do not reflect them into the forward hemisphere.
   On the exact rear axis (`x = 0`, `z > 0`), choose `h = +180°`, regardless of signed zero.
   On the vertical axis (`x = z = 0`, `y != 0`), choose `h = 0` and `v = sign(y)*90°`.
   When `T = O`, no direction exists: use neutral `h = v = 0`. Non-finite inputs invalidate acceptance, not pass it.
4. For each eye `i`, let `N_i` be its immutable authored neutral rotation in its parent frame, `P_i` the current solved
   parent-to-world rotation and `f_i` its immutable unit optical-forward vector in authored eye-local coordinates.
   The neutral optical direction is `n_i = P_i*N_i*f_i`; the independently expected presented direction is
   `e_i = R*D*inverse(R)*n_i`. Thus both eyes receive the same shared request through their authored optical mappings.
   Where neutral optical axes align with shared forward, both `e_i` equal `R*d`. Arbitrary node-axis orientation does
   not imply a different optical forward. Preserve authored neutral offsets; do not add per-eye correction frames or
   recapture neutral from live output to force agreement. This mapping is not physical-origin vergence.
5. Measure the actual presented optical direction `a_i` separately for each eye, then compare
   `acos(clamp(dot(normalise(a_i), normalise(e_i)), -1, 1))` against 0.5°.
   Transform output uses the presented eye rotation applied to `f_i`; blendshape output requires equivalent optical
   references on the presented deformed eye geometry. Desired angles or seek writes alone are not observations.
   Authored references must be established independently of live gaze and production conversion helpers.

#### Independently Calculable Examples

These examples use `O = (0, 0, 0)`, `R = identity` and neutral optical axes aligned with `-Z`; lengths are metres.
Values follow directly from the trigonometry above, not from production helpers.

- **Mixed Axes:** `T = (1, 1, -sqrt(3))` gives `h = 30°` and `v = atan(1/2) = 26.565051°`.
  After clamping, `(hc, vc) = (30°, 25°)` and both expected directions are approximately
  `(0.453154, 0.422618, -0.784886)`. Reversing the rotation order does not produce this vector.
- **Near Target:** `T = (0.03, 0, -0.1)` gives `h = atan(0.3) = 16.699244°`, `v = 0°` and
  `d = (0.287348, 0, -0.957826)`. Even with physical eye positions at `(-0.032, 0, 0)` and `(0.032, 0, 0)`,
  both expected directions remain `d`; computing two eye-to-target rays would wrongly introduce vergence.
- **Authored Axis:** With `P_i = identity`, `N_i = Ry(90°)` and `f_i = (1, 0, 0)`, `n_i = (0, 0, -1)`.
  The expected optical direction is still `d`, although the eye node's local `-Z` is not its optical forward.
- **Rear Axis:** `T = (0, 0, 1)` gives `(hc, vc) = (35°, 0°)` and
  `d = (0.573576, 0, -0.819152)`, not neutral forward. Pure up clamps to `(0, sin(25°), -cos(25°))`.

### Assignment and Presentation Ordering

Record the actual assignment event `A`, each gaze evaluation `G`, each presentation's rendered-scene-state sampling
event `S`, and its post-draw observation `B`. A presentation is eligible for acquisition onset counting exactly when
its `S` follows `A`. Frame 1 is the first such presentation; count every eligible presentation thereafter, even if it
contains stale gaze. A post-draw callback observes an already sampled presentation; it does not redefine `S` or `A`.
Use monotonic event/sequence ordering to disambiguate equal timestamps, retaining the presentation identity through
observation. An assignment after `S` cannot make that presentation eligible merely because `B` occurs later.

Boundary replays must demonstrate all three orders, including equal timestamps resolved by sequence:

- `A → G → S → B`: this presentation is frame 1.
- `G → A → S → B`: this presentation is also frame 1; stale gaze still counts against the ≤2-frame onset bound.
- `G → S → A → B`: this presentation is pre-assignment and does not count; frame 1 is the next eligible presentation.

Observe the pre-assignment reference and all eligible presentations without skipping failures. The 0.4 s completion
deadline remains relative to actual `A`, never frame 1, `G` or `B`, with no cadence grace. Establish installed-engine
sampling/observation ordering with evidence; if it cannot be observed reliably, report that bounded evidence gap
rather than claim conformance or silently substitute callback timing.

### Acquisition Completion Decision

With saccades disabled for precision acceptance, both eyes must enter at most 0.5° angular alignment error against
their independently derived anatomically constrained directions within 0.4 s of actual target assignment, then
maintain fixation. Acquisition onset within 2 presented frames is distinct from completion. Measured cadence does not
extend the completion deadline; target, head or character movement must not restart it. This behavioural acceptance
requirement does not establish measured feasibility: report conflicts rather than relax the bound or tune smoothing.

## In Scope

- `IVision` component capability interface.
- Visual-family typing, exact concrete percept metadata, and look-target transition publication.
- `IHasVision` holder trait.
- `EyesBehaviour` Godot node facade.
- Default blendshape/TimeSeek-driven eye movement (horizontal and vertical).
- Saccades anchored around the resolved look point, expressed through the active gaze backend.
- OneShot-driven randomised blinking with configurable cadence.
- AnimationTree partial blending for eyes.
- Optional transform-driven gaze backend for characters whose eye meshes lack gaze blend
  shapes: paired eye-node authoring, assignment-based backend inference, activation and
  installer validation of the pair, neutral-transform capture with drift-free rebuild,
  coordinated clamped rotation in each eye's parent frame, look-blend suppression with
  continued blinking, and exclusive rotation ownership.
- Backwards compatibility: existing characters and scenes exercise the default blendshape
  backend without authoring changes.
- Import-time creation of a missing `AnimationPlayer`.
- Import-time generation or replacement of the `eyes` AnimationLibrary.
- Import-time generation of eye tracks from discovered eye blend shapes, or invisible
  placeholder/no-op tracks when no recognised eye blend shapes exist.
- Per-character AnimationTree integration.
- Visual subject, visual-cue provider, ownership, and visual-cue contracts under `AlleyCat.Vision`.
- The `AlleyCat.Core.ISpatial` read-only global-transform trait inherited by `IVisualSubject`.
- Static cue origin sampling, fixed template-backed local descriptions, optional nearest-subject input, provider
  publication/refresh validation, and immutable published cue topology.
- Authored whole-character `body` cues and character-specific appearance overrides.
- Eyes-owned periodic `VisualSurveyPercept` publication for discovered non-self subjects with visible cues.
- `EyesBehaviour`-owned strict `VisualSubjects` querying, member validation, authoring failure, scan filtering,
  `EyeOrigin` cone evaluation, and `VisionOccluder` ray tests.
- Cue-local `VisualBounds`, per-cue distance limits, and representative visibility sampling.
- Awaitable cue description for transition-driven focused inspection under AI-006.
- Continuous live-target fixation and separate bounded saccades in both backends: frame-consistent solved-head
  composition, lifetime-safe scheduling and independent bilateral, complete-window measurement.

## Out Of Scope

- Attention, Mind interpretation, or gaze-selection policy. AI-007 alone is the separately composed post-attention
  consumer that may assign look targets; Vision remains policy-neutral and has no attention-gaze hook.
- Automatic visual-cue selection or gaze movement towards cues.
- Pose-change detection or continuous reinspection while the same cue remains assigned; periodic re-examination of a
  focused subject is owned by AI-006 polling faculties, not Vision.
- Emotional-state policy that modifies saccade tuning.
- Independent per-eye vergence or convergence movement; both backends move the eyes as one
  coordinated unit.
- Per-eye forward-axis correction frames; the transform backend uses one shared rotation-only
  direction conversion.
- Eyebrow movement or expression changes.
- Lip-sync or mouth animation.
- Networked replication or multiplayer considerations.
- IK solver modifications for eye tracking.
- Physical eye collision or physics-response behaviour; scan-only `VisionOccluder` ray tests remain required.
- Head-turn transit dynamics themselves — rate limits, reaction delay, and transit stalls — which remain owned by
  [AI-009: Attention-Driven Head Orientation](../../ai/009-attention-head-orientation/index.md).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|----------|
| 1  | Technical         | `IVision`, `IHasVision`, `IVisualPercept`, both concrete visual percepts, and |
|    |                   | visual-cue contracts live directly in `AlleyCat.Vision`; |
|    |                   | `IVision : ISense<IVisualPercept>` preserves the non-generic bridge and defines |
|    |                   | `VisualCue?` `LookTarget`, `SetLookTarget`, and `ClearLookTarget`. |
| 2  | Technical         | `IHasVision` defines `TryGetVision` and `RequireVision` methods. |
| 3  | Technical         | `EyesBehaviour` implements `IVision` and delegates supplied look points to the |
|    |                   | controller; in blendshape mode it never applies direct eye transform rotation. |
| 4  | Technical         | `EyesBehaviour` exposes a protected look-point resolver that returns the |
|    |                   | assigned `LookTarget`, or a point 1 metre in front of the eyes as fallback. |
| 5  | User              | Setting a visual-cue `LookTarget` causes the eyes to orient toward that cue using |
|    |                   | the default TimeSeek-driven blendshape animation. |
| 6  | User              | Clearing the look target makes the eyes fall back to looking 1 metre forward. |
| 7  | User              | Bounded, finite saccades move around the live anchor and return to fixation without |
|    |                   | changing focus; test separately from precision fixation, without an exact trajectory. |
| 8  | Technical         | Every gaze evaluation resolves the live assigned target or current-head-relative fallback, |
|    |                   | independently of random saccade timing. Saccade interval, speed and amplitude use default |
|    |                   | constants or exports; saccade timing never throttles moving-target tracking. |
| 9  | Technical         | `EyesBehaviour` owns target-node resolution, fallback, and saccade |
|    |                   | anchor/offset state, but no gaze-selection policy or attention-gaze hook. |
| 10 | Technical         | `EyesController` owns supplied-point conversion (TimeSeek values in blendshape |
|    |                   | mode, clamped smoothed eye-node rotation in transform mode), smoothing, blend |
|    |                   | enforcement or suppression, and blink timing; it does not resolve look-target |
|    |                   | `Node3D` nodes directly. |
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
| 24 | Technical         | In blendshape mode, tests verify that eye movement output writes only TimeSeek |
|    |                   | seek requests and never applies direct eye transform rotation. |
| 25 | Technical         | Tests verify that TimeSeek seek position 0.5 corresponds to neutral eye |
|    |                   | position (forward-facing). |
| 26 | Technical         | Tests verify that blink playback uses an `AnimationNodeOneShot` request, |
|    |                   | while horizontal and vertical look remain `AnimationNodeTimeSeek` nodes in |
|    |                   | blendshape mode. |
| 27 | Technical         | Mirror-room tests verify that blendshape-mode look blend overrides remain |
|    |                   | enabled at runtime. |
| 28 | User              | Default blendshape-mode visual verification confirms: (a) neutral eyes face |
|    |                   | forward at 0.5s seek, (b) directional look animates correctly for |
|    |                   | up/down/left/right, (c) saccades remain bounded around the anchor, and |
|    |                   | (d) blink animation opens and closes eyes. |
| 29 | User              | Leaving both eye-node references unassigned keeps the default blendshape |
|    |                   | backend; assigning both selects the transform backend; assigning exactly one |
|    |                   | fails clearly rather than silently picking a backend. |
| 30 | Technical         | Automated tests verify backend inference from the paired assignments (no enum |
|    |                   | or flag selects the backend) and clear activation-time plus runtime-installer |
|    |                   | rejection of partial pairs before eye behaviour is enabled. |
| 31 | User              | In transform mode, both eyes look together through the same bounded, smoothed |
|    |                   | rotation, and each eye's authored neutral pose is preserved without drift |
|    |                   | accumulating across gaze changes. |
| 32 | Technical         | Automated transform-mode tests verify both eyes receive the identical clamped, |
|    |                   | smoothed yaw/pitch delta under the shared look limits, that the neutral local |
|    |                   | transform (position, orientation, scale) is captured at activation, and that |
|    |                   | per-frame rotation rebuilds from that neutral without accumulated drift. |
| 33 | Technical         | Automated transform-mode tests verify gaze deltas are computed in the shared |
|    |                   | `EyeOrigin` frame using rotation-only conversion with `-Z` forward and `+Y` up, |
|    |                   | then transferred into each eye's current parent frame, supporting eyes with |
|    |                   | different parents and positive non-uniform scale on `EyeOrigin`, eye parents, |
|    |                   | and eyes. |
| 34 | Technical         | Automated transform-mode tests verify horizontal and vertical look blends are |
|    |                   | driven to `0` and held there while blink `AnimationNodeOneShot` requests, |
|    |                   | cadence, duration, and time scale continue unchanged, and that the |
|    |                   | AnimationTree, `EyeOrigin`, `eyes` library, and blink tracks remain required. |
| 35 | Technical         | Tests verify gaze output ownership is exclusive while the character runtime is |
|    |                   | active: the blendshape backend solely writes look blends and seek requests, the |
|    |                   | transform backend solely writes eye-node rotation, and transform-mode eyes |
|    |                   | carry no authored `AnimationPlayer` transform tracks. |
| 36 | User              | Transform-mode characters blink at the configured random cadence while looking |
|    |                   | around, matching blendshape-mode blink behaviour. |
| 37 | Technical         | Existing character scenes require no authoring changes and continue exercising |
|    |                   | the default blendshape backend. |
| 38 | User              | A dedicated transform-eye photobooth fixture under `game/temp/eyes-transform/` |
|    |                   | provides visual verification that: (a) neutral eyes rest at their authored |
|    |                   | neutral pose, (b) directional look rotates both eyes up/down/left/right, |
|    |                   | (c) extreme targets clamp to the shared limits, (d) target changes smooth |
|    |                   | rather than snap, and (e) the character blinks while looking around. |
| 39 | User              | Systems can discover an authored whole-character `body` cue and obtain an observer-relative |
|    |                   | appearance description for Ally NPC, Ally player, and Vadim. |
| 40 | User              | Shared female and male templates provide a usable `body` cue even when its |
|    |                   | description is placeholder text. |
| 41 | Technical         | `IProvidesVisualCues` and `IVisualSubject` have the authoritative ownership, |
|    |                   | `IIdentifiable`, read-only collection, and `ISpatial` inheritance contracts |
|    |                   | specified in Technical Requirement 18; `IVisualObserver` does not exist. |
| 42 | Technical         | `VisualCue` and `StaticVisualCue` expose the authoring, sampling, prominence, |
|    |                   | and required-observer description contracts specified in Technical |
|    |                   | Requirements 19–24, including asynchronous |
|    |                   | `Describe(ISceneContext scene, IHasVision observer)`, |
|    |                   | the exported `StaticVisualCue.Description` property, and origin sampling independent of |
|    |                   | bounds. |
| 43 | Technical         | Description rendering builds a local root without composed render context, uses the |
|    |                   | supplied `IHasVision`, and supplies the nearest `IVisualSubject` ancestor only when |
|    |                   | present. Missing subject ancestry remains valid. |
| 44 | Technical         | Provider publication or explicit refresh rejects empty cue IDs, non-finite |
|    |                   | or negative prominence, ordinally duplicate IDs, and cues not owned by that |
|    |                   | nearest provider; it accepts disabled prominence `0` and finite values above |
|    |                   | `1`. Published cue topology remains immutable until explicit refresh. |
| 45 | Technical         | Shared female and male templates each author exactly one `body` `StaticVisualCue` |
|    |                   | at `Head/BodyVisualCue`, a sibling of the existing `Viewpoint`; Ally NPC, Ally player, |
|    |                   | and Vadim supply character-specific template overrides. |
| 46 | Technical         | Automated tests verify cue discovery, sampling, description context rendering, |
|    |                   | authoring, overrides, and validation without requiring screenshot or |
|    |                   | visual-rendering acceptance. |
| 47 | User              | A character can sense currently visible authored subjects without changing its |
|    |                   | look target, blink cadence, or saccade anchor. |
| 48 | User              | Cues outside the field of view or positive distance limit, behind an occluder, |
|    |                   | on the observing subject, or disabled by zero prominence are not reported. |
| 49 | Technical         | `IVision : ISense<IVisualPercept>` declares exact concrete metadata for only |
|    |                   | `VisualSurveyPercept` and `LookTargetChangedPercept`, publishes through the |
|    |                   | synchronous non-generic bridge, and exposes no public `Scan()` operation. |
| 50 | Technical         | `EyesBehaviour` owns a finite positive interval, performs |
|    |                   | at most one survey per frame, performs no delayed-frame catch-up, and rejects |
|    |                   | invalid authored or runtime cadence before activation. |
| 51 | Technical         | Survey evaluation excludes the observer's own subject and zero-prominence cues. |
| 52 | Technical         | `EyesBehaviour` evaluates from `EyeOrigin` using export-overrideable 60° |
|    |                   | horizontal and 45° vertical half-angle defaults. |
| 53 | Technical         | Cues support cue-local point, sphere, and oriented-box `VisualBounds`, plus |
|    |                   | `MaxVisibleDistance`, where `0` means unlimited distance. |
| 54 | Technical         | Visibility sampling is separate from gaze sampling and uses representative |
|    |                   | bounds samples; a passing sample must satisfy distance, cone, and ray tests. |
| 55 | Technical         | Ray tests recognise only pre-endpoint `VisionOccluder` hits as occlusion and |
|    |                   | honour a configurable endpoint tolerance. |
| 56 | Technical         | Tests verify provider-side nearest-provider ownership validation and clear |
|    |                   | publication or refresh failure for invalid cue ownership; surveys consume only |
|    |                   | valid published collections and perform visibility selection without ownership |
|    |                   | reconciliation or nested-leak filtering. |
| 57 | Technical         | Tests verify visibility selection, cue description, and AI-003 render-context |
|    |                   | assembly remain separate operations. |
| 58 | User              | Periodic NPC visual perception can notice all visible subjects without creating routine |
|    |                   | visual history or changing gaze and eye presentation. |
| 59 | Technical         | Each survey emits one producer-owned immutable ordered snapshot containing only |
|    |                   | canonical visible-subject `FullId` values, never descriptions or observations. |
| 60 | Technical         | Tests verify surveys never invoke `VisualCue.Describe`, select gaze, change |
|    |                   | `LookTarget`, or alter saccades, blink cadence, or other eye presentation. |
| 61 | Technical         | Tests verify EyesBehaviour remains policy-neutral and has no attention-gaze |
|    |                   | hook; AI-007 is the separately composed post-attention look-target consumer. |
| 62 | User              | An effective NPC focus change can lead to a durable description of the newly |
|    |                   | focused subject, while same-cue assignment and clearing create none by themselves. |
| 63 | Technical         | Transition tests verify one immutable previous/current percept per effective |
|    |                   | cue-identity change, no percept for same-cue assignment, `current -> null` on |
|    |                   | active clear, and no percept for an already clear target. |
| 64 | Technical         | Tests verify transition publication reports applied state without selecting gaze, |
|    |                   | and focused inspection validates cue lifetime and subject association before |
|    |                   | awaiting `Describe`. |
| 65 | Technical         | Tests verify `IVisualSubject` inherits `AlleyCat.Core.ISpatial`, that `ISpatial` |
|    |                   | exposes exactly the read-only `Transform3D GlobalTransform` member, and that Vision |
|    |                   | owns no spatial sampling or interpretation policy around it. |
| 66 | User              | During a 40° head turn to a new sustained target (reference female), both eyes maintain |
|    |                   | fixation within 0.5° of their independently derived constrained directions under TR51, |
|    |                   | with saccades disabled and acquisition observed separately. The head centres within 0.8 s |
|    |                   | (head-transit timing owned by AI-009). Target and head motion do not restart acquisition. |
| 67 | Technical         | Tests verify the eye-gaze composition consumes the same-frame solved head pose presented |
|    |                   | to the renderer in both backends, new-target acquisition onset stays within 2 presented |
|    |                   | frames of actual assignment, with overshoot ≤4°. With saccades disabled, both eyes |
|    |                   | enter ≤0.5° alignment with their independently derived constrained directions within 0.4 s |
|    |                   | of actual assignment and maintain fixation thereafter. Acquisition smoothing remains 0.08; |
|    |                   | measured cadence does not extend the completion deadline. |
| 68 | Technical         | Tests verify the synchronisation requirement changes neither the eye clamps (35° |
|    |                   | horizontal / 25° vertical) nor gaze selection or blink behaviour. Separate saccade tests |
|    |                   | verify TR4 without relaxing precision fixation. Fixed/moving targets, head compensation, |
|    |                   | mixed axes and out-of-range directions satisfy TR51–52. |
| 69 | Technical         | Schedule→remove/free/rebind coverage verifies pending-work invalidation, no old-binding |
|    |                   | writes, exactly-once temporal advancement and zero-delta recomposition without random-state |
|    |                   | consumption. Establish a discriminating reproduction or state the runtime evidence gap |
|    |                   | before deferred-path repair; source findings alone do not prove a runtime failure. (TR-50) |
| 70 | Technical         | Oracles pass TR52 adversarial replays and single-eye fault detection, using both eyes' |
|    |                   | authored references, assignment-time deadlines and complete post-draw windows. |
|    |                   | Independently reproduce Shared-Direction Geometry examples and Assignment and Presentation |
|    |                   | Ordering boundary replays; skip no eligible presentation and count no pre-assignment one. |

Criteria 66–70 retain the original 40° reassignment and 135-frame 90 fps series with measured actual cadence, including
all baseline, acquisition, transit, centring and settled observations. Neither suite may shorten its window
or omit settled recurrence. The
[AI-009 evidence contract](../../ai/009-attention-head-orientation/index.md#complete-window-evidence-contract)
is a normative dependency for fixed glance events, immutable candidate records, at least three final stability runs
and visual review. Backend timing must be verified against presented eye output; passing only transform mode does not
establish blendshape conformance. Current-fixture acceptance is conditional, not historical regression resolution or
universal animated-idle proof.

## References

- [INTR-003: Hands](../../interaction/003-hands/index.md)
- [CHAR-003: Character Physical Response](../../character/003-physical-response/index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CORE-009: Identifiable Identity](../../core/009-identifiable-identity/index.md)
- [AI-003: Prompt API](../../ai/003-prompt-api/index.md)
- [TMPL-001: Templating System](../../templating/001-templating-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../../ai/007-attention-gaze-target-selection/index.md)
- [AI-009: Attention-Driven Head Orientation](../../ai/009-attention-head-orientation/index.md)
- [Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- `game/assets/characters/import/eye_animation_library_import.gd`
- `game/src/Vision/IVision.cs`
- `game/src/Vision/IHasVision.cs`
- `game/src/Vision/VisualSurveyPercept.cs`
- `game/src/Vision/EyesBehaviour.cs`
- `game/src/Vision/EyesController.cs`
- `game/src/Character/CharacterRuntimeSubsystemInstaller.cs`
