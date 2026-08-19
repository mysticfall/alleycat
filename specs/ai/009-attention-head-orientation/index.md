---
id: AI-009
title: Attention-Driven Head Orientation
---

# Attention-Driven Head Orientation

## Requirement

NPCs must present attention-driven gaze with head orientation rather than eyes alone: the head must follow the AI-007
gaze anchor according to how sustained the attention is, while quick glances remain eyes-only and gaze selection
semantics stay wholly with AI-007.

## Goal

Make sustained attention read as facing behaviour — an NPC turns its head towards a conversation partner and cranes
its neck for targets beyond comfortable eye range — without changing eye presentation, the AI-007 selector, or the
IK-001 solver, and without affecting the player's XR-owned head path.

## User Requirements

1. An NPC engaged with a sustained attention target — a conversation partner — turns its head to face that target,
   even when the target is well within eye rotation range. The NPC faces whoever it talks to; it does not fix its
   face and roll its eyes.
2. Tall or short NPCs looking at targets beyond comfortable eye range — for example a tall NPC looking down at the
   player — crane their neck naturally instead of saturating eye rotation alone.
3. Brief side glances within eye range stay eyes-only: the head holds towards the last sustained focus, or towards
   neutral when there is none. Brief glances beyond eye range move the head only as far as needed, preserving the
   glance's quick character, and the head eases back afterwards.
4. Head motion is smooth and visibly delayed relative to the eyes — eyes lead, the head follows — without snapping
   or oscillating when a target hovers near a threshold.
5. When gaze clears, the head returns to neutral; when a target lies beyond head range, the head strains towards it
   as far as it can while the eyes keep tracking, reading as trying to see.
6. Player characters remain unaffected; only NPC role templates that compose the controller gain attention-driven
   head orientation.

## Technical Requirements

### Ownership And Composition

1. `OrientingController` is a concrete `IKTargetIntentProvider` subclass living under `AlleyCat.Mind.Attention`
   alongside the AI-007 selector, named in anticipation of future body-orientation expansion. It is composed as a
   direct child of its owner `Mind` in NPC role templates, following the AI-007 placement precedent. It is not an
   `IComponent` and not a member of `Character.Components`; player and shared player-base templates do not compose
   it. The Mind-side namespace is deliberate: the controller is an attention consumer, Mind already references
   `AlleyCat.Vision` through the AI-007 selector, and Mind referencing `AlleyCat.IK` introduces no cycle.
2. The NPC installer wires the controller into `CharacterIK.HeadTargetIntentProvider`, the existing head provider
   slot that drives Neck-Spine CCDIK (IK-001) per the provider property mapping in the IK implementation notes. The
   controller returns an `IKTargetIntent` — an explicit world-space target transform plus desired influence — and a
   desired influence of 0 deactivates the neck-spine modifier group. The player's head slot remains XR-owned and
   unchanged.
3. The controller consumes only its owner character's `IVision`: it reads `LookTarget` — the assigned anchor's
   world position and how long that same anchor has been continuously assigned. It must never call `SetLookTarget`
   or `ClearLookTarget` and must not read AI-007 selector state. AI-007 remains the sole gaze selector; this
   controller owns only when and how far the head follows — biomechanics, not semantics.
4. The head tracks the assigned anchor's world position only, never saccade offsets.

### Engagement Policy

5. Engagement is hybrid, with two triggers:
   - Saturation rule (immediate, geometric): the desired direction to the anchor is evaluated per frame against the
     eye comfort cone — the angular range within which the eyes alone are expected to carry the gaze. When the
     direction exceeds the cone on either axis, the head takes the residual angle that brings the direction back
     inside it.
   - Sustained-attention centring (temporal): when the same anchor has been continuously assigned on
     `IVision.LookTarget` for at least the centring delay, the head centres on the anchor — full centring, even when
     the anchor is well within the eye rotation bound. The degree of head orientation encodes engagement, not
     geometry.
6. Glance versus sustained is classified only from continuous same-anchor assignment duration measured on
   `LookTarget`; assignment of a different anchor or a clear resets the timing. There is no conversation or speech
   detection: conversation partners produce sustained centring because they are sustained primary targets under
   AI-007 dwell semantics.
7. Glance head-hold: during a brief in-cone glance the head holds towards the last sustained anchor, or neutral when
   none exists, leaving the glance eyes-only. During a brief out-of-cone glance the head moves only the residual
   needed to re-enter the comfort cone. On glance end the head eases back towards the sustained anchor or neutral.
8. Sustained head aim brings the anchor onto the eye-neutral axis. A small residual eccentricity is tunable, with
   full centring as the default.

### Thresholds And Orientation Envelope

9. Comfort-cone and envelope thresholds are per-axis tunable with asymmetric vertical defaults mirroring physiology:
   comfort cone of approximately ±15° horizontal and 10° up / 15° down; orientation envelope of approximately ±75°
   horizontal and 40° up / 55° down. The eye hard clamps (35° horizontal / 25° vertical) remain owned by
   `EyesController` under VISION-001; this spec does not change them.
10. A target beyond the envelope strains best-effort to the envelope edge while the eyes keep tracking, reading as
    trying to see. IK-001 joint constraints remain the final authority on the achievable pose, and the envelope is
    the declared seam for a future body-turning AI-family sibling using `INavigation`.

### Motion Feel, Release, And Timing

11. The head must not snap like eyes. Engagement applies a reaction delay (approximately 0.15–0.2 s), rate-limited
    smoothed rotation, and an influence ramp; the head must remain materially slower than eye seek so the eyes lead
    and the head follows. Release applies hysteresis on angle and engagement so a target hovering near a threshold
    does not flap the head.
12. When `LookTarget` is cleared, the head eases to neutral and influence goes to 0. IVision's existing fallback gaze
    (1 metre ahead) stays eyes-only.
13. The centring delay (default approximately 0.6 s) must exceed AI-007's configured secondary dwell so brief
    secondary glances never trigger centring. This relationship is a tuning constraint, not an enforced contract:
    both values remain independently tunable, and the defaults must keep a safe margin.

### Reference Frame And Determinism

14. Angle computation uses the current solved head and eye-line orientation each frame, making the controller a
    converging closed-loop servo: as the head turns, the residual shrinks. Eyes update every frame, so eye offsets
    shrink automatically as the neck rotates, without changes to `EyesBehaviour`.
15. Evaluation is delta-driven and deterministic. The pure engagement and aim logic — per-axis threshold evaluation,
    glance and sustained classification, hysteresis state machine, head-hold, and rate-limited smoothing — must be
    extractable as a Godot-free seam injectable into unit tests, following the `EyesLookMath` precedent in
    `game/src/Vision/`.

### Purity, Lifecycle, And Validation

16. No changes to `EyesBehaviour`, `EyesController`, saccades, blinking, the AI-007 selector, or the IK-001 solver.
17. The controller resolves its owner character's `IVision` only after the owner Character's component projection
    commits, following the AI-007 Mind lifecycle pattern; projection changes must not leave stale Vision bindings.
    Teardown stops providing intent — influence 0 — so CharacterIK falls back to its existing safe idle.
18. Exported settings must validate before activation: finite angles and durations; per-axis envelope angles must
    exceed the corresponding comfort-cone angles; reaction delay, centring delay, and rate limits must be finite and
    positive.

## In Scope

- A direct Mind-child `OrientingController : IKTargetIntentProvider` under `AlleyCat.Mind.Attention`, composed in NPC
  role templates and wired by the NPC installer into `CharacterIK.HeadTargetIntentProvider`.
- Hybrid engagement policy: immediate saturation residual, sustained-attention centring from continuous same-anchor
  `LookTarget` assignment timing, glance classification, and glance head-hold semantics.
- Per-axis asymmetric comfort cone and orientation envelope with physiological defaults, best-effort edge strain,
  and release to neutral.
- Reaction delay, rate-limited smoothed rotation, influence ramp, release hysteresis, and the centring-delay versus
  AI-007 secondary-dwell tuning constraint.
- Deterministic delta-driven evaluation with a Godot-free pure-logic seam, settings validation, and
  component-projection lifecycle integration with teardown to IK safe idle.

## Out Of Scope

- Body or locomotion turning via `INavigation` for behind-the-character targets; the orientation envelope is the
  declared seam for this future AI-family sibling.
- Conversation, speech, or any semantic target classification; sustained-ness is inferred from gaze assignment
  timing only.
- Changes to AI-007 selection policy, `EyesBehaviour`, `EyesController`, saccades, blinking, or Vision fallback.
- Changes to the IK-001 solver or the player's XR-owned head path.
- Final tuning values; thresholds, delays, and rate limits remain tunable, with physiological defaults only.

## Acceptance Criteria

### User Requirements

1. Visual verification and integration coverage show a tall NPC looking down at a shorter character — the player —
   craning its neck naturally, with the eyes no longer carrying the downward angle alone; the short-NPC looking-up
   case behaves symmetrically.
2. Coverage shows a same-height sustained target well within eye rotation bounds receiving full head centring after
   the centring delay: the NPC faces its conversation partner rather than fixing its face and rolling its eyes.
3. Coverage shows brief in-cone side glances remaining eyes-only while the head holds towards the last sustained
   anchor or neutral, brief out-of-cone glances moving the head only by the residual, and the head easing back after
   the glance ends.
4. Coverage shows head engagement visibly delayed and smoother than eye movement, hysteresis preventing flapping for
   a target hovering near a threshold, and release to neutral when gaze clears.
5. Coverage shows a beyond-envelope target producing best-effort head strain to the envelope edge with the eyes
   still tracking.
6. Coverage confirms player templates remain free of the controller and the player's XR head path is unaffected.

### Technical Requirements

7. Contract and composition tests verify the `IKTargetIntentProvider` subclass under `AlleyCat.Mind.Attention`,
   direct Mind-child placement, no `IComponent` or `Character.Components` membership, NPC-only template composition,
   and installer wiring into `CharacterIK.HeadTargetIntentProvider`.
8. State-machine tests verify saturation engagement, sustained centring within the comfort cone, glance
   classification from continuous assignment timing with reset on anchor change or clear, in-cone head-hold, minimal
   residual for brief out-of-cone glances, glance-end return, release hysteresis, envelope edge clamping, and
   clear-to-neutral with zero influence.
9. Timing tests verify that the default centring delay exceeds the configured AI-007 secondary dwell and pin the
   centring-delay boundary behaviour.
10. Servo tests verify the closed-loop reference frame: the residual shrinks as the head turns, without drift or
    overshoot.
11. Purity tests verify no changes to `EyesBehaviour`, `EyesController`, saccades, blinking, the AI-007 selector, or
    the IK-001 solver; that the controller never calls `SetLookTarget` or `ClearLookTarget`; and that the head
    tracks the anchor world position, never saccade offsets.
12. Determinism and lifecycle tests verify delta-driven evaluation through the Godot-free seam, component-projection
    before Vision resolution, projection-safe rebinding, teardown to safe idle with influence 0, and settings
    validation including envelope-exceeds-comfort-cone per axis.

## References

- [AI-001: Mind Component](../001-mind/index.md)
- [AI-007: Attention-Driven Gaze Target Selection](../007-attention-gaze-target-selection/index.md)
- [VISION-001: Eyes](../../vision/001-eyes/index.md)
- [IK-001: Reusable Neck-Spine CCDIK Setup](../../ik/001-neck-spine-ik/index.md)
- [IK Implementation Notes — IKTargetIntentProvider Contract](../../ik/implementation-notes.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [NAV-001: NPC Navigation](../../nav/001-npc-navigation/index.md)
- `game/src/IK/IKTargetIntentProvider.cs`
- `game/src/IK/CharacterIK.cs`
- `game/src/Vision/EyesLookMath.cs`
- `game/src/Vision/EyesController.cs`
