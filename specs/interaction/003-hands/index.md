---
id: INTR-003
title: Hands
legacy_id: BODY-001
---

# Hands

## Purpose

Define contracts for hand components that manage hand-pose animation blending,
two-phase grab execution (approach then commit), and held-object lifecycle,
exposing hand-side control to external consumers via the CORE-003
Component/Trait System.

## Requirement

Implement hand components that provide per-hand pose control, enable characters
to retain full-body posture while applying filtered finger-bone pose overrides
with smooth transitions, and manage grab execution across approach and commit
phases with hand mobility during hold.

## Goal

Provide a reusable hand component system that:

- Supports independent left/right hand control via `LimbSide`.
- Blends hand poses through filtered AnimationNodeBlend2 nodes without affecting
  the hand bone or arm bones.
- Transitions poses smoothly with configurable duration.
- Exposes a CORE-003 compatible component interface and holder trait.
- Integrates with the existing VRIK/IK pipeline without circular dependencies.
- Discovers and grabs IGrabbable objects within configured range.
- Moves the hand to the grab point via IK without moving the item.
- Commits the grab only after the hand settles at the target.
- Maintains hand mobility while holding (grab override released; hand follows the globally
  selected XR hand-pose source, held item follows via hand-bone parenting).
- Manages held object lifecycle and release restoration.
- Supports separate authored `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` per grab point
  or animation.

## User Requirements

1. Hand poses must not override upstream hand animations unless explicitly specified.
2. When a hand pose is set, the hand must blend between the pose and upstream output
   using a specified weight, defaulting to full application.
3. Hand pose changes must transition smoothly rather than jumping instantly.
4. Each hand must be controllable independently.
5. Hand poses must only affect finger bones, leaving the hand bone and upstream
   arm/body animations unaltered. Wrist and hand bone tracks are intentionally
   filtered out; only finger bones are affected.
6. Hands must discover IGrabbable objects within a configured interaction range.
7. On grab press, the item stays in place while the hand moves to the selected
   grab point via IK.
8. The grab commits only after the hand reaches and settles at the target.
9. While holding, the hand remains mobile and follows the globally selected XR hand-pose
   source (or default provider motion); the held item follows the hand.
10. Both left and right hand pose animations must work while holding and visibly
    and effectively affect their own finger bones, not the opposite hand.
11. Grab points support separate authored `GrabPointPositionOffsetFromHand`
      and `GrabPointRotationOffsetFromHand` to correct how items sit
      in the hand.
12. There must be an easy authoring workflow to determine or specify these
     offsets by manually positioning an item on a character hand.
13. Debug diagnostic output is available when enabled, posting concise
     notifications via the global UI to reveal grab state, candidate
     selection, and animation status.
14. Release must drop the held object and restore all involved subsystems to
     initial states.
15. A physical RigidBody3D test ball must remain discoverable and grabbable
    with physics suspended while held and restored on release.
16. While optical mode is active, an optical-originated pending hand visibly
       transitions to its candidate's authored reference pose while raw joints
       remain invisible and detect opening or loss. Stable opening or an explicit
       mode switch cancels assistance and blends to current valid live tracking.
        Tracking loss cancels pending assistance;
        visual interpolation freezes at its current assisted interpolation;
        it resumes only when valid raw source samples return, then targets current valid projected tracking.
        A committed
       hand shows the fixed authored grab pose while the opposite hand keeps
        following real tracked fingers; ordinary optical release blends back to
        live tracking with nothing left stuck.
17. Unrelated characters or stale hand instances cannot change the player's
    finger-pose presentation. Regrabs and pose replacement retain the intended
    authored pose until its own handoff is ready.

## Technical Requirements

1. Use the shared `AlleyCat.Rigging.LimbSide` enum from `game/src/Rigging/LimbSide.cs`
   (`Left = 0`, `Right = 1`).
2. Define `IHand : IComponent` capability interface in `AlleyCat.Interaction.Hands`:
   - `Side: LimbSide` — identifies the hand side.
   - `CurrentGrabbed: IGrabbable?` — currently held object, null if none.
   - `Grab(): IGrabbable?` — attempts to grab a discoverable object; returns
     grabbed object or null if no valid grabbable in range.
     Note: this initiates the approach phase; commit is deferred until IK settles.
   - `Release(): void` — releases the currently held object, restoring involved
     subsystems including physics state.
   Pose control is internal to `HandPoseController`; `IHand` is the public hand
   interaction contract, not a generic pose-control interface.
3. Define `IHasHands : IComponentHolder` holder trait in `AlleyCat.Interaction.Hands`:
   - `TryGetHand(LimbSide side, out IHand? hand)` — resolves exactly one hand
     component for the requested side; returns `false` if zero or more than one
     hand component exists.
   - `RequireHand(LimbSide side)` — returns the hand component or throws if not
     found or multiple exist.
4. Implement `HandPoseBehaviour : Node, IHand` in `AlleyCat.Interaction.Hands`:
   - Accepts an `AnimationTree` reference or inherits from parent.
   - Exposes per-side pose properties that delegate to an internal
     `HandPoseController`.
   - Manages a `HandPoseController` instance that:
     - References per-hand filtered `AnimationNodeBlend2` nodes in the
       AnimationTree.
     - Uses track filters matching only finger bones for the target hand
       (e.g., `*LeftIndexProximal`, `*RightThumbMetacarpal`), excluding the
       hand bone.
     - Exposes `LeftHandPose`, `RightHandPose` (typed `Animation`, not
       arbitrary `Resource`), `LeftHandPoseWeight`, `RightHandPoseWeight`,
       `CurrentLeftHandPose`, `CurrentRightHandPose`, and `TransitionDuration`.
     - Validates pose resources as Godot `Animation` before use.
     - Implements `SetHandPose(LimbSide side, Animation? pose, float? weight,
       bool immediate)` and `ClearHandPose(LimbSide side, bool immediate)`.
     - On grab commit, reads the grab point's `GrabPointCandidate.Animation`
       resource and applies it via `SetHandPose`.
     - On release, clears the hand pose via `ClearHandPose`.
5. The hand blend nodes must use their `blend_amount` parameter as effective
   hand-pose application: requested weight multiplied by current smooth
   transition progress.
6. When no hand pose is set, the blend node must pass through upstream output
   unchanged.
7. Default transition duration is 0.2 seconds; the implementation interpolates
   `blend_amount` smoothly, not instantly switching.
8. The root AnimationTree must be a functional `AnimationNodeBlendTree` containing
   a filtered per-hand blend chain sequenced after the upstream state-machine output.
9. The implementation must be compatible with the existing VRIK/IK pipeline (IK-004)
   without introducing state coupling or circular dependencies.
10. Grab execution must implement two phases:
     a. `Approaching`: IK target moves to grab point; item stays still.
     b. `Grabbing/Committed`: IK has settled; hand parents item to bone and
        applies grab-pose animation from the candidate's `Animation` resource
        via `HandPoseController.SetHandPose`. The resource is validated as
        Godot `Animation` before application.
11. While holding, the grab override is released and normal tracking from the globally
      selected XR hand-pose source resumes; the hand follows that source and the parented
      held item follows the hand.
12. `CurrentGrabbed` reflects the held object or null when empty.
13. Expose a `DebugGrabOutput: bool` exported property to enable diagnostic
     notifications; when enabled, the hand posts concise state updates through
     `NotificationUIExtensions.PostNotification`. Diagnostics reveal:
     - Selected or rejected grab candidate, with reason when available.
     - Grabbable mobility (Movable/Immovable).
     - Pending or Committed grab state.
     - Distance to commit target during approach.
     - Provider override state (active/cleared).
     - Animation resource name and registration status.
     - Release and clear events.
14. `Release()` restores physics state for physical objects (e.g., RigidBody3D).
15. Hand pose track filters intentionally exclude wrist and hand bones; only
    finger bones are affected by pose animations. This is by design, not an
    oversight.
16. While holding a Movable grabbable, collision exceptions are added between
     the held body and the grabbing hand's target, finger colliders, hand
     collider, and lower-arm proxy collider to prevent erratic motion.
17. Collision exceptions are removed and physics state is restored on release.
18. Hand component exposes `HeldCollisionTarget: CollisionObject3D` property for
    collision proxy attachment; detailed collision proxy implementation is
    specified in INTR-002 (Hand Grab Execution).
19. Per-hand finger-pose arbitration is published by the concrete hand instance and
      a distinct publication generation, never by side alone. In `Optical` mode,
      live optical presentation wins only for a hand with no current optical-originated
      pending or committed publication. An optical-originated pending hand visibly
      transitions to its candidate's authored reference while raw joints remain the
      invisible opening/loss source. On commit, the candidate's authored hand-pose
      publication wins and the optical modifier performs zero writes to that hand's
      finger bones. Clearing, release, replacement, or teardown may revoke only its
      own publisher/generation, so stale or unrelated same-side cleanup is harmless.
      Controller pending states never publish optical assistance and the opposite hand
      remains independent.
20. `HandPoseBehaviour` exposes a narrow current-best-candidate observation seam and
    per-hand pending/held input-provenance state for the CTRL-002 grab input
    coordinator, without exposing pose-control APIs through `IHand`: `IHand`'s public
    surface stays `Side`, `CurrentGrabbed`, `Grab()`, and `Release()`, and pose control
    remains internal to `HandPoseController`.
21. Release from optical input performs the same `Release()` restoration as controller
     release; on ordinary optical release the authored pose blends to the hand's current
     valid optical pose rather than snapping to a stale capture.
22. The selected candidate's validated `Animation` is one instance-exact reference
    identity shared by recognition, pending assistance, and hand-pose playback.
    Valid pathless resources are supported; resource names, paths, and side alone are
    not identity keys. Binding and derived-profile caches include this identity, the
    bound hand/skeleton identity, and the relevant generation/configuration.
23. The legitimate hand-pose owner publishes readiness for its own reference and
    generation only when it has the same instance-exact reference at the intended
    effective weight and evaluated pose state. Optical writes relinquish only after
    that readiness, including active-pose replacement, regrab, default and non-default
    transitions. The optical modifier and reference sampler must not mutate authored
    `Animation` resources; `HandPoseController` may update only its designated
    AnimationTree hand-pose slot and blend parameters to realise the selected pose.

> **Optical Hand-Pose Arbitration:** while the optical hand-pose mode is active
> ([XR-002: Optical Hand Tracking](../../xr/002-optical-hand-tracking/index.md)),
> optical tracking overrides the authored finger poses above for presentation on any
> hand that is neither committed nor in an optical-originated pending grab. An
> optical-originated pending hand
> visibly transitions to its candidate's authored reference pose, while raw optical
> sampling remains invisible and solely detects opening or loss. Stable opening or an
> explicit mode switch cancels assistance and blends to current valid live tracking.
> Tracking loss cancels pending assistance;
> visual interpolation freezes at its current assisted interpolation;
> it resumes only when valid raw source samples return, then targets current valid projected tracking.
> While a grab is committed, the candidate's fixed
> authored AnimationTree pose owns that hand's finger presentation and the optical
> modifier performs zero writes to that hand's finger bones (per-hand arbitration seam,
> Requirement 19). The current publisher's instance-exact reference and generation must
> report the readiness in Requirement 23 before optical writes relinquish. Controller
> pending states never receive assistance. The opposite hand stays live optical throughout.
> The optical override resumes on that hand when the grab releases, blending from the
> candidate animation to the hand's current valid tracked pose (Requirement 21); authored
> finger poses regain authority when optical mode exits; outside optical mode this
> specification remains authoritative unchanged.

## In Scope

- Shared `AlleyCat.Rigging.LimbSide` enum from `game/src/Rigging/LimbSide.cs`.
- `IHand` component capability interface including grab/release methods.
- `IHasHands` holder trait with `TryGetHand` and `RequireHand` methods.
- `HandPoseBehaviour` Godot node facade exposing the hand-pose API.
- Per-hand filtered `AnimationNodeBlend2` nodes in the animation tree.
- Smooth transition for hand pose changes using configurable duration.
- Independent per-hand control (left/right).
- Track filters matching only finger bones, excluding the hand bone.
- Compatibility with VRIK/IK pipeline.
- Two-phase grab execution (approach then commit) with hand mobility during hold.
- Separate `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
  support from grab point candidates.
- Release execution with subsystem state restoration including physics.
- Collision exception handling for held Movable grabbables.
- `DebugGrabOutput` property for diagnostic notifications.
- Per-hand finger-pose arbitration between optical presentation and committed grab
  animations, with the narrow candidate-observation and provenance seam for the
  CTRL-002 input coordinator.
- Instance-exact authored-reference identity, publisher hand-instance/generation
  authority, and readiness-based optical handoff as defined with XR-002.

## Out Of Scope

- Animation content creation or pose animation assets.
- Eye blinking, breathing, or facial animation pipelines.
- Networked replication or multiplayer considerations.
- IK solver modifications.
- Explicit blend shape or morph target support.
- Automatic pose detection or procedural pose generation.
- Multi-hand grab coordination.
- Inventory integration.
- Automatic offset computation (authoring workflow only).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | Technical         | `LimbSide` enum is defined in `AlleyCat.Rigging` with `Left = 0`, |
|    |                   | `Right = 1`; Body and IK namespaces do not define replacement enums. |
| 2  | Technical         | `IHand : IComponent` interface defines `Side`, `CurrentGrabbed`, |
|    |                   | `Grab()`, and `Release()`; it does not expose `Pose`, |
|    |                   | `PoseWeight`, `CurrentPose`, `SetPose`, or `ClearPose`. |
| 3  | Technical         | `IHasHands : IComponentHolder` defines `TryGetHand` and |
|    |                   | `RequireHand` methods. |
| 4  | Technical         | `HandPoseBehaviour` implements `IHand`; pose control is internal |
|    |                   | via `HandPoseController` with per-side pose properties. |
| 5  | Technical         | `HandPoseController` uses `Animation`, not arbitrary `Resource`, |
|    |                   | for pose resources and validates the type before use. |
| 6  | Technical         | Track filters on blend nodes match only finger bones, |
|    |                   | excluding the hand bone. |
| 7  | Technical         | When no hand pose is set, blend nodes pass through upstream |
|    |                   | output unchanged. |
| 8  | User              | Clearing a hand pose leaves upstream hand animations unaltered. |
| 9  | User              | Setting a hand pose with weight 1.0 fully applies the pose to |
|    |                   | finger bones. |
| 10 | User              | Hand pose changes transition smoothly over the configured |
|    |                   | duration, not instantly. |
| 11 | User              | Left and right hands are independently controllable. |
| 12 | Technical         | Implementation is compatible with VRIK/IK pipeline without |
|    |                   | circular dependencies. |
| 13 | User              | Hand discovers IGrabbable objects within configured |
|    |                   | interaction range. |
| 14 | User              | On grab press, the item stays in place while the hand moves |
|    |                   | to the grab point via IK. |
| 15 | User              | Grab commits only after the hand settles at the target. |
| 16 | User              | While holding, the hand remains mobile and follows the globally |
|    |                   | selected XR hand-pose source; held item follows the hand. |
| 17 | User              | Both left and right hand pose animations work while holding. |
| 18 | User              | Release drops held object and restores all involved |
|    |                   | subsystems to initial states. |
| 19 | User              | Physical RigidBody3D test ball is discoverable and grabbable |
|    |                   | with physics suspended while held and restored on release. |
| 20 | Technical         | `Grab()` initiates approach phase; commit is deferred until IK |
|    |                   | settles. |
| 21 | Technical         | While holding, grab override is released and normal tracking from |
|    |                   | the globally selected XR hand-pose source resumes; hand follows that |
|    |                   | source and held item follows the hand via parenting. |
| 22 | Technical         | `CurrentGrabbed` reflects the held object or null when empty. |
| 23 | Technical         | `Release()` restores physics state for physical objects. |
| 24 | User              | Debug output when enabled reveals grab candidate, state, provider |
|    |                   | override, and animation status through notifications. |
| 25 | Technical         | `DebugGrabOutput` property enables diagnostic notifications via |
|    |                   | `NotificationUIExtensions`. |
| 26 | User              | Both left and right hand pose animations visibly and effectively |
|    |                   | affect their own finger bones and not the opposite hand. |
| 27 | Technical         | Hand pose track filters intentionally exclude wrist and hand bones; |
|    |                   | only finger bones are affected by pose animations. |
| 28 | User              | While holding a Movable grabbable, the held item does not collide |
|    |                   | with the grabbing hand's colliders while still following hand motion. |
| 29 | Technical         | Collision exceptions are added between held movable body and same-side |
|    |                   | hand target, finger colliders, hand collider, and lower-arm proxy; |
|    |                   | exceptions are removed on release. |
| 30 | Technical         | Hand exposes `HeldCollisionTarget: CollisionObject3D` property for |
|    |                   | collision proxy attachment; collision proxy implementation is |
|    |                   | covered in INTR-002. |
| 31 | User              | While optical mode is active, an optical pending hand visibly transitions |
|    |                   | to its candidate reference pose. Stable opening or an explicit mode switch |
|    |                   | cancels assistance and blends to current valid live tracking. |
|    |                   | Tracking loss cancels pending assistance; |
|    |                   | visual interpolation freezes at its current assisted interpolation; |
|    |                   | it resumes only when valid raw source samples return, |
|    |                   | then targets current valid projected tracking. |
|    |                   | A committed hand shows the fixed authored grab pose; the |
|    |                   | opposite hand stays live tracked; ordinary optical release blends back. |
| 32 | Technical         | Per-hand arbitration: live optical presentation wins only for hands with |
|    |                   | neither a committed nor optical-originated pending grab. Pending assistance |
|    |                   | uses raw joints only for opening/loss recognition. A committed hand is authored |
|    |                   | AnimationTree-owned and receives zero optical modifier writes. |
| 33 | Technical         | Controller-originated pending states never publish optical pending |
|    |                   | assistance. |
| 34 | Technical         | The candidate-observation and pending/held provenance seam is exposed |
|    |                   | through `HandPoseBehaviour` without adding pose APIs to `IHand`. |
| 35 | Technical         | Optical release performs the same `Release()` restoration and blends |
|    |                   | the authored pose to the current valid optical pose; the opposite |
|    |                   | hand remains live. |
| 36 | User              | An unrelated character or stale same-side hand cleanup cannot disturb the |
|    |                   | player's current finger-pose presentation. |
| 37 | Technical         | Recognition, assistance, and playback use one validated instance-exact |
|    |                   | `Animation` reference. Pathless valid resources work; names, paths, and side |
|    |                   | do not substitute for identity, including in binding and profile caches. |
| 38 | Technical         | A publisher is identified by hand instance and generation. Only it can revoke |
|    |                   | its publication; stale cleanup is safe. |
| 39 | Technical         | Optical writes stop only after the legitimate hand-pose owner reports the same |
|    |                   | reference at intended effective weight and evaluated state for its generation, |
|    |                   | including replacement, regrab, and non-default transitions. |

## References

- [Project Specifications Index](../../index.md)
- [CORE-003: Component/Trait System](../../core/003-component-system/index.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
- [IK-004: VRIK Pose State Machine And Hip Reconciliation](../../ik/004-vrik-pose-state-machine/index.md)
- [INTR-001: Grabbable Interface](../../interaction/001-grabbable/index.md)
- [INTR-001-A: Spherical Grab Point](../../interaction/001-grabbable/spherical-grab-point.md)
- [INTR-002: Hand Grab Execution](../../interaction/002-hand-grab-execution/index.md)
- [CTRL-002: Hand Grab Input](../../ctrl/002-hand-grab-input/index.md)
- [XR-002: Optical Hand Tracking](../../xr/002-optical-hand-tracking/index.md)
- [Character Skeleton Profile](../../character/001-character-skeleton/index.md)
- `game/src/Rigging/LimbSide.cs`
- `game/src/Interaction/Hands/IHand.cs`
- `game/src/Interaction/Hands/IHasHands.cs`
- `game/src/Interaction/Hands/HandPoseBehaviour.cs`
- `game/src/Interaction/Hands/HandPoseController.cs`
