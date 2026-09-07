---
id: CTRL-002
title: Hand Grab Input
parent: CTRL
---

# Hand Grab Input

## Purpose

Define the contract for translating recognised grab and release intent into hand grab actions for the two grab
input sources that coexist with the global hand-pose mode
([XR-002: Optical Hand Tracking](../../xr/002-optical-hand-tracking/index.md)): XR controller grab buttons, whose
physical edges are honoured in every committed mode, and optical hand closure/opening, recognised only while the
committed mode is `Optical`. Both sources drive the same two-phase grab (approach then commit) defined by
[INTR-002: Hand Grab Execution](../../interaction/002-hand-grab-execution/index.md).

## Requirement

Implement a grab input layer that calls `IHand.Grab()` when grab intent is recognised and `IHand.Release()` when
release intent is recognised, supporting independent left/right hand control. Physical XR controller grab button
edges are honoured in every committed mode: a press initiates the approach phase with `Controller` provenance and
commit occurs automatically once IK settles. While the committed mode is `Optical`, a second intent source is
continuous candidate-aware gesture recognition: the hand's current eligible grab candidate is evaluated against
its animation-derived power-grip reference, so closing the real hand near an eligible grab point grabs and a
stable aggregate opening cancels a pending approach or releases a held object.

## Goal

Provide a hand grab input system that:

- Maps XR controller grab trigger or button presses to hand grab actions in every committed mode.
- Recognises candidate-aware, animation-derived optical power-grip closure and opening in `Optical` mode.
- Honours physical controller grab/release edges regardless of the committed mode, with per-hand input
  provenance so one hand may hold an optical grab while the other grabs via the controller.
- Supports independent left/right hand control.
- Integrates with PlayerController or a dedicated Control namespace coordinator.
- Maintains loose coupling between input handling and interaction logic.
- Handles the two-phase nature of grab (recognised grab intent initiates the approach; commit is deferred until
  IK settles).

## User Requirements

1. Pressing the grab button on an XR controller initiates the approach phase, moving the hand to the selected
   grab point while the item stays still, regardless of the committed hand-pose mode.
2. Releasing the grab button triggers release of whatever that hand holds, regardless of the committed mode or
   which input source originated the grab; pressing the button while the hand already holds is a no-op.
3. In `Optical` mode, closing the real hand near an eligible grab point initiates the same approach and visibly
    transitions that pending hand to the candidate's authored reference pose; raw opening before commit cancels it
    and blends to current valid live tracking. If raw optical tracking is unavailable when a loss cancellation begins,
    the visual blend freezes at its current assisted interpolation, resumes only when valid raw source samples return,
    and then targets current valid projected tracking.
4. In `Optical` mode, a stable opening of the whole hand releases a held object; over-clenching or a single
   unrelated finger extending does not.
5. Left and right hands operate independently, including one hand holding an optical grab while the other grabs
   or holds via the controller.
6. Commit occurs automatically once IK settles; no second press or repeated gesture is required.
7. Grab and release input is suppressed while the game menu is open, in both modes.
8. Controller features other than grabbing (locomotion, menus, transcription) work identically regardless of the
   committed mode.
9. When play resumes, an open controller grip cancels a pending grab or releases a held item once. A continuously
    held grip preserves the existing grab, while a press and release wholly during the pause creates no grab.
10. Controller grab input is never silently disabled by the committed mode: on hardware whose mode arbiter
    commits `Optical` even while controllers are held, the grab buttons still initiate and release grabs.

## Technical Requirements

### Controller Input (Mode-Independent)

1. Define grab input handling in the `Control` namespace (for example a mode-aware `HandGrabInputHandler` or
   grab input coordinator).
2. The handler references left and right XR controller nodes from the XR rig.
3. On grab button press (for example grip button or trigger), in any committed mode, initiate a grab on the
   corresponding hand with `Controller` provenance.
   - This initiates the approach phase; the hand moves to the grab point via IK.
   - The item does not move during approach.
   - Commit is deferred until IK settles; the input handler takes no further action.
   - A press on a hand that is already pending or held is a lifecycle no-op: the active grab and its provenance
     are unchanged.
4. On grab button release, in any committed mode, call `IHand.Release()` on the corresponding hand.
   - The release ends whatever that hand holds, regardless of which input source originated the grab.
   - If the hand has not yet committed (for example still approaching), the grab is abandoned and the hand
     returns to idle.
   - If already holding, release proceeds normally; if the hand holds nothing, the release is a no-op.
5. Existing controller grab button and analogue-trigger edge semantics are unchanged; this specification
   introduces no new controller thresholds or hysteresis.

### Mode Independence And Provenance

6. Physical controller grab and release edges are mode-independent: they are always honoured with `Controller`
   provenance, regardless of the committed global hand-pose mode. Optical grab recognition acts only while the
   committed mode is `Optical` (mode contract in XR-002). Grab ownership is never implicitly transferred between
   sources on a mode change.
7. Mode gating applies only to optical grab recognition. Unrelated controller consumers (locomotion via CTRL-001,
   the game menu, transcription input) are unaffected by the committed mode.
8. The committed mode-change notification from the XR-001 runtime boundary is the signal used to cancel pending
   and release held grabs originated by the previously active source, per the provenance contract in
   Requirement 17; continuity across an explicit mode switch is not required. Controller edge detection resets
   its per-side press state on the same notification, so no stale edge state leaks across the transition.

Mode independence for controller edges is deliberate. The XR-002 mode arbiter commits `Optical` whenever optical
tracking sees the hands — including while controllers are held — and retains `Optical` through tracking loss or
ambiguity, so gating controller grab edges on `Controller` mode permanently silences controller grab input on
such hardware.

### Optical-Mode Input

9. In `Optical` mode the coordinator continuously evaluates the hand's current best eligible grab candidate —
   obtained through the existing generic discovery/selection path (INTR-001/INTR-002) via the candidate
   observation seam in INTR-003 — against that candidate's animation-derived power-grip reference.
10. Recognition is candidate-aware: with no eligible candidate there is no grab recognition regardless of hand
    closure; there is no generic "fist anywhere" grab.
11. Stability accumulates only while the same candidate stays selected; a candidate change resets the stability
    accumulator without locking a speculative candidate.
12. Grab and release thresholds derive from the candidate's mandatory `Animation` articulation as a weighted
    aggregate power grip (profile contract in INTR-001; derivation contract in XR-002). Defaults are `0.75` entry,
    `0.55` release, and a `0.10 s` stability interval; all remain implementation-tunable. Recognition distinguishes
    directional opening from mere pose difference so over-clenching never causes release, and modest
    individual-finger variation is tolerated.
13. Recognition consumes the current candidate's raw optical source joints in parent-local/anatomical
    (destination-local) space through the shared calibrated anatomical projection defined by XR-002. Raw OpenXR
    source rotations must never be compared directly with destination animation keys, and neither rendered nor
    assisted presentation may be read for recognition.
14. On a stable grab-threshold crossing, call `IHand.Grab()` and begin optical-only pending contact assistance
     towards that candidate's authored reference pose. On raw stable aggregate opening, recognition dependency loss,
     or an explicit
     mode switch while pending, cancel through the existing abandonment path. If raw optical tracking is unavailable
     when loss cancellation begins, freeze the visual blend at its current assisted interpolation; resume it only when
     valid raw source samples return, then target current valid projected tracking. On a stable aggregate opening while
     held, call `IHand.Release()`.
15. Recognition dependency loss while a grab is pending cancels the pending grab; while held it preserves the grab
     and pauses release recognition without synthesising a release. It invalidates recognition measurements and resets
     stability; after recovery, fresh candidate recognition and ordinary stable-open recognition may release (lifecycle
     state table in INTR-002). Candidate content validation belongs to the shared discovery/selection boundary. An
     animation that cannot supply its side-specific reference is unavailable to both controller and optical paths and
     reports `NoCandidate`; the coordinator reports `ProfileUnavailable` only when a validated reference cannot
     produce the selected strategy's profile.

### Shared Input Handling

16. Grab input handling must:
    - Identify which hand side triggered the input (left/right controller or left/right optical hand).
    - Resolve the correct `IHand` component via `IHasHands.TryGetHand()`.
    - Forward grab/release calls to the resolved hand component.
17. The coordinator tracks input provenance per hand — idle, pending, or held, and the source mode that
    originated the grab — so exceptional committed mode changes cancel or release grabs deterministically and
    never transfer ownership implicitly.
18. Game-menu pause suppresses controller and optical grab/release edges consistently with existing controller
    grab suppression.
19. While paused, controller delivery must issue no `IHand.Grab()` or `IHand.Release()` action. It must still retain
    the latest *valid* physical digital or analogue grip state for each side without treating absent, invalid, or
    unobserved state as open.
20. On the first resume reconciliation for each side, a known-open physical grip cancels an existing pending grab or
    releases an existing held grab exactly once; a known-held grip preserves either state. Reconciliation must not
    replay a paused press as a new grab, so a complete press/release cycle during pause has no grab effect.
21. Controller pause reconciliation is local to existing controller-originated lifecycle state and does not alter the
    global `Controller`/`Optical` mode policy or create per-hand mixed tracking modes.
22. Input handling does not implement grab mechanics: it translates recognised intent and mode/loss transitions
    into `IHand.Grab()`/`IHand.Release()` calls and does not monitor IK settling or duplicate candidate
    discovery, commit, parenting, or release restoration (INTR-001/INTR-002 own those mechanics).
23. If no hand component exists for the input side, the input is ignored (no error).
24. If `Grab()` returns null (no valid grabbable), no visual or haptic feedback is required for this spec
    (future expansion may add feedback).
25. Recognition must not branch on concrete grab-point classes (spherical, cylindrical, or future types). A
    candidate-keyed strategy resolver used by production recognition must accept a stub future precision profile
    without structural change; only the animation-derived power-grip strategy exists in this increment.
26. Pending contact assistance applies only to optical-originated pending states. Controller pending states retain
    their existing presentation and never receive authored reference-pose assistance.

## In Scope

- XR controller grab button press/release mapping to `IHand.Grab()`/`Release()` in every committed mode.
- Candidate-aware optical power-grip closure/opening recognition mapping to `IHand.Grab()`/`Release()` in
  `Optical` mode, including optical-only pending contact assistance.
- Mode-independent controller grab edges with `Controller` provenance and `Optical`-mode gating of optical
  recognition, with per-hand provenance supporting mixed-source holds across hands.
- Independent left/right hand control, including provenance tracking per hand.
- Integration with PlayerController or a Control namespace coordinator.
- Loose coupling between input and interaction systems.
- Pause suppression of grab edges in both modes.
- Valid-state controller resume reconciliation for existing pending and held grabs.
- Handling of two-phase grab (recognised grab intent initiates the approach; commit is automatic).

## Out Of Scope

- Haptic feedback on grab attempt or success.
- Visual feedback (for example highlighting valid grabbables).
- Grab input for non-XR input sources (keyboard, gamepad).
- Precision/pinch grip recognition with related animations, assets, and tests; the strategy seam must remain
  extensible for them.
- Manual gesture-profile authoring or per-grab-point pose/threshold overrides.
- New grab animation content; the optical profile derives from each grab point's existing mandatory `Animation`.
- Multi-hand grabbing (two hands on one object); "two-finger grip" in this feature family means digits of one
  hand.
- Network replication of input events.
- IK settling detection (handled by the hand component or IK system).
- Grab mechanics themselves: discovery, candidate selection, approach, commit, parenting, and release
  restoration (INTR-001/INTR-002).

## Open Design Items

- Exclusive grab-input mode switching. The two grab input sources are conceptually exclusive — one active input
  mode per player — and reliable exclusive switching remains the desired end state. The always-honoured
  controller edges above are the accepted stopgap, because the XR-002 mode arbiter can commit `Optical` on
  hardware where optical tracking sees the hands even while controllers are held. A future increment may
  introduce hysteresis-based exclusivity: for example, committing `Controller` only after sustained optical
  tracking loss combined with controller presence or activity, and returning to `Optical` after sustained valid
  optical tracking with no controller activity. Such tuning is hardware-dependent and requires its own approved
  specification change before the always-honoured contract above may be tightened.

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | User              | Pressing the grab button initiates the approach phase for the corresponding |
|    |                   | hand, in every committed mode. |
| 2  | User              | Releasing the grab button triggers release of whatever that hand holds — |
|    |                   | regardless of the grab's originating source — or abandons the approach if not |
|    |                   | yet committed; a press while already holding is a no-op. |
| 3  | User              | In `Optical` mode, closing the hand near an eligible grab point |
|    |                   | initiates the same approach phase and visibly transitions the pending hand |
|    |                   | to the candidate reference pose; raw opening cancels and blends it back. |
| 4  | User              | In `Optical` mode, stable aggregate opening releases; over-clenching or |
|    |                   | a single unrelated finger extending does not. |
| 5  | User              | Left and right hands operate independently, including mixed-source holds |
|    |                   | (one hand optical, the other controller). |
| 6  | User              | Commit occurs automatically once IK settles; no second press or |
|    |                   | repeated gesture is required. |
| 7  | User              | Grab and release edges are suppressed while the game menu is open in |
|    |                   | both modes. |
| 8  | User              | Non-grab controller features (locomotion, menus, transcription) are |
|    |                   | unaffected by the committed mode. |
| 9  | Technical         | Handler uses XR controller input to detect press/release in every committed |
|    |                   | mode with unchanged button and analogue edge semantics. |
| 10 | Technical         | Optical recognition is candidate-aware: no eligible candidate yields |
|    |                   | no grab recognition regardless of closure. |
| 11 | Technical         | Candidate changes reset stability accumulation; no speculative |
|    |                   | candidate is locked. |
| 12 | Technical         | Optical thresholds derive from the candidate's mandatory `Animation`; |
|    |                   | defaults are `0.75` entry, `0.55` release, and `0.10 s` stability, while |
|    |                   | remaining tunable. Recognition uses candidate-specific raw joints through the |
|    |                   | shared calibrated anatomical projection, never raw OpenXR-versus-animation-key |
|    |                   | comparison or rendered/assisted presentation. |
| 13 | Technical         | Grab calls `IHand.Grab()` on the correct hand side; release calls |
|    |                   | `IHand.Release()`; pending cancellation uses the existing |
|    |                   | abandonment path. |
| 14 | Technical         | Hand resolution uses `IHasHands.TryGetHand()` for the input side. |
| 15 | Technical         | Optical grab edges are inactive outside `Optical` mode; controller grab |
|    |                   | edges are honoured in every committed mode with `Controller` provenance; |
|    |                   | grab ownership is never implicitly transferred on a mode change. |
| 16 | Technical         | The committed mode-change notification cancels pending and releases |
|    |                   | held grabs of the previously active source per the tracked per-hand |
|    |                   | provenance. |
| 17 | Technical         | The input layer maintains loose coupling: no duplicated discovery, |
|    |                   | commit, parenting, or release mechanics, and no IK-settling |
|    |                   | monitoring. |
| 18 | Technical         | Temporary tracking loss while held pauses release recognition without |
|    |                   | a synthetic release; a recovered stable open releases. |
| 19 | Technical         | Recognition has no concrete grab-point class branches; the candidate-keyed |
|    |                   | production strategy resolver accepts a stub future precision profile without |
|    |                   | structural change. |
| 20 | Technical         | Optical-originated pending states visibly assist towards the selected |
|    |                   | candidate reference pose while raw joints detect opening/loss. Cancellation |
|    |                   | from opening or mode switch blends to current valid live tracking. Loss |
|    |                   | cancellation, both mid-partial assistance and after pending assistance has completed, |
|    |                   | freezes the current assisted interpolation until valid raw source samples return, then |
|    |                   | targets current valid projected tracking; controller pending states never assist. |
| 21 | User              | Resume with a known-open controller grip cancels Pending or releases Held |
|    |                   | once, while a known-held grip preserves it and a complete paused input cycle creates |
|    |                   | no grab. |
| 22 | Technical         | Pause suppresses all controller actions while retaining only valid physical grip |
|    |                   | state. Missing state is not treated as open; resume reconciles existing lifecycle |
|    |                   | state once without replaying paused edges or changing global source mode. |
| 23 | Technical         | Recognition dependency loss cancels Pending, preserves Held, invalidates |
|    |                   | measurements and stability, and requires fresh recognition after recovery. |
| 24 | User              | Controller grab input works while the committed mode is `Optical`; the grab |
|    |                   | buttons are never silently disabled by the committed mode. |
| 25 | Technical         | A controller press during a pending or held grab leaves the active grab and |
|    |                   | its provenance unchanged; a controller release edge ends a hold of any |
|    |                   | provenance; controller edge-detection state resets on committed mode changes. |

## References

- [Project Specifications Index](../../index.md)
- [CTRL: Player Character Control System](../index.md)
- [CTRL-001: Locomotion](../001-locomotion/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [XR-002: Optical Hand Tracking](../../xr/002-optical-hand-tracking/index.md)
- [INTR-001: Grabbable Interface](../../interaction/001-grabbable/index.md)
- [INTR-002: Hand Grab Execution](../../interaction/002-hand-grab-execution/index.md)
- [INTR-003: Hands](../../interaction/003-hands/index.md)
- `game/src/Control/` (implementation namespace)
