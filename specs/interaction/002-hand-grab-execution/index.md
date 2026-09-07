---
id: INTR-002
title: Hand Grab Execution
---

# Hand Grab Execution

## Purpose

Define the contract for hand grab execution across two phases (approach
and commit), parenting, hand-mobility during hold, release with state
restoration, and per-animation grab-point transform authoring.

## Requirement

Implement hand grab mechanics that discover IGrabbable objects within
range, query grab points, move the hand to the grab point via IK, commit
the grab only after the hand settles, parent the object to the hand bone,
track hand motion while held, and cleanly release on demand.

## Goal

Provide a grab execution system that:

- Discovers IGrabbable nodes via configurable Godot group membership or Area3D overlap.
- Queries IGrabbable objects for suitable IGrabPoint candidates using hand transform.
- Moves the hand to the candidate's grab point via IK without teleporting the item.
- Commits the grab only after the hand reaches/settles at the target.
- Parents the grabbed object to a hand bone via BoneAttachment3D nodes.
- Applies hand pose animation from the grab point's candidate.
- Maintains hand mobility (IK follows the globally selected XR hand-pose source/default
  provider) while held.
- Releases held object and restores all involved subsystems to initial states.
- Supports separate authored position and rotation offsets per animation/grab point.
- Provides an authoring workflow to position an item on a character hand.

## User Requirements

1. On grab press, the item stays in place throughout the approach while the
   hand moves to the selected grab point; it receives no early magnetism or
   tracked-finger collision.
2. The grab commits only after the hand reaches/settles at the target.
3. While held, the hand remains mobile and follows the globally selected XR hand-pose
   source (or default provider motion).
4. The held item follows the hand during all movements.
5. Both left and right hand pose animations work while holding.
6. Grabbables and grab points support authored position and rotation offsets
   per animation or grab point so items sit appropriately in the hand.
7. There is an easy authoring workflow to determine or specify these offsets
   by manually positioning an item on a character hand.
8. Release cleanly drops the object and restores IK, hand pose, and
    parenting state.
9. Releasing a held Movable physical grabbable transfers an estimated
   release velocity so the object follows the throw trajectory instead
   of dropping from rest.
10. Releasing a held Movable physical grabbable from a stationary hand
    does not launch the object; near-zero velocity is transferred.
11. Releasing an Immovable grabbable does not apply throw impulse.
12. A slowly moving or rolling Movable grabbable remains catchable when it
     stays within the current hand's valid grab range during the approach,
     including a small pending-grab tolerance that does not affect stationary
     acquisition range.
13. Multiple hands operate independently without interference.
14. A physical RigidBody3D test ball remains discoverable and grabbable
    with physics suspended while held and restored on release.
15. While holding a Movable grabbable, the held item must not collide with
     the grabbing hand's colliders (fingers, hand, lower-arm proxy) while
     still following hand motion. Non-grabbing hand and world collisions
     remain unless explicitly configured otherwise.
16. While holding a Movable grabbable, the held item must retain effective
     world collision so it can hit and interact with other objects while
     following the hand. A held stick must be able to strike other objects.
17. In optical mode, a stable candidate-specific closure of the real hand
     initiates the same approach as the controller grab button; a stable
     aggregate opening cancels a pending approach or releases a held object.
18. While an optical-originated grab is pending, the hand visibly transitions
     to the selected candidate's authored reference pose while raw optical
     joints continue invisibly to detect opening and loss. Opening, loss, or
     mode switch cancels the approach. When raw optical tracking is unavailable
     as a loss cancellation begins, the visual blend freezes at its current
     assisted interpolation, resumes only when valid raw source samples return,
     and then targets current valid projected tracking. Once committed, the fixed
     authored AnimationTree pose owns that hand's fingers until release, with raw
     optical sampling continuing invisibly for release detection. Controller
     pending states receive no assistance.
19. Temporary tracking loss while an optical grab is held preserves the held
     object and its fixed pose; release recognition pauses, no synthetic
     release occurs, and after recovery a stable open releases.
20. An explicit committed mode switch may cancel a pending optical grab or
     release a held one; grab ownership is never transferred to the newly
     active input source.
21. On ordinary optical release, the authored pose blends back to the hand's
       current valid tracked pose; the opposite hand remains live tracked
       throughout.
22. Grabs resolve only to anatomically reachable hand poses. A player may be
     standing, seated, kneeling, or otherwise plausibly posed; standing is not
     a success precondition.
23. Grabbing no longer knocks the target item away: while a grab is pending, the approaching hand's rig and IK
    target can neither impact nor push the authorised candidate, so a deliberate grab converges without
    physically disturbing the item.
24. The protection is transient and complete: on abandonment or hand teardown the item immediately regains
    ordinary collision with the hand and its response to hand interaction, and on commit the held-grab behaviour
    takes over without a visible jump. Non-candidate items remain pushable by the open hand throughout.

## Technical Requirements

### Discovery

1. Hand component exposes configurable `DiscoveryRangeMetres` property (default 0.3m).
2. Discovery uses either Godot group membership (e.g., "grabbable") or Area3D overlap.
   - Group-based: queries all nodes in group, filters by distance to hand transform.
   - Area3D-based: configures Area3D with `CollisionShape3D` sphere of discovery range.
3. Discovery returns the closest IGrabbable within range that yields a valid grab point.
   A holder occupied by another hand is unavailable before ranking, so it cannot
   obscure an available candidate. This is candidate eligibility, not a reservation;
   INTR-001 retains the atomic commit-time ownership guard.

### Candidate Selection

4. Hand calls `IGrabbable.GetGrabPoint(Side, handTransform)` on discovered candidates.
5. Selection uses the closest candidate by `GrabPointCandidate.AcquisitionDistance`.
   `AcquisitionDistance` is the distance from the querying hand origin to the accepted
   acquisition reference's selected point, as produced by the grab point implementation.
   This metric, not `HandTarget.Origin`, must be used for ranking. `HandTarget` is for
   the target hand pose/IK settling only and may differ from the acquisition point when
   authored offsets exist.
6. If multiple candidates are equally close, holder order (as defined in INTR-001) is tie-breaker.
7. Candidate selection is deterministic: same hand position yields same result.
8. Candidate includes the grab point's authored `GrabPointPositionOffsetFromHand` and
   `GrabPointRotationOffsetFromHand` (see section below).

### Two-Phase Grab: Approach and Commit

9. On candidate selection, hand records the selected candidate and enters `Approaching` state.
10. Hand forwards the candidate's `HandTarget` (including grab-point rotation) to
    `IKTargetIntentProvider` for smooth interpolation.
11. The item does not move during approach; only the hand IK target moves.
    No approach-time parenting, physics/collision proxy, magnetism, or
    tracked-finger collision may move or contact the item.
12. For `Immovable` candidates only, hand monitors IK settling using an
    implementation-defined threshold (e.g., position within 2mm and angular delta below
    5° for 2 consecutive frames). `Immovable` candidates retain these existing settling
    semantics.
13. Once the `Immovable` settling requirement is satisfied, hand enters `Grabbing` and
    calls `IGrabbable.Grab(candidate)` to commit. `Movable` commit eligibility is defined
    solely by the direct attachment-transform requirement in Requirement 18.
14. On commit success, hand parents the object to the appropriate BoneAttachment3D node.
15. The selected grab point's hand-relative transform equals the transform composed from
     `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`; object local may include the inverse
     of the grab point's object-local transform.
16. If the hand loses the candidate (e.g., object removed) before settling, the grab
    is abandoned and the hand returns to idle.
17. On commit, the hand may refresh the selected grab point candidate using the
     current hand transform, provided the refreshed candidate comes from the same
     grab-point source as the pending selection. This allows slowly moving Movable
     grabbables to be caught without accepting a different grab source. If such a
     refresh materially changes the expected position or orientation target, the
     Movable candidate's consecutive-frame settle count resets.
18. The commit-time refresh may apply a separate configurable acquisition tolerance
       for `Movable` grabbables only. This tolerance does not change `ReachDistanceMetres`
       on grab points and must not loosen initial stationary acquisition. The sole final
       settlement and commit-eligibility gate for a `Movable` commit requires the actual
       `HandBoneAttachment`
       (`BoneAttachment3D`) transform to match
       `candidate.GrabPointTransform × inverse(candidate.GrabPointOffsetFromHand)` within
       production defaults of 8 mm (0.008 m) position and 5° orientation for two consecutive
       process frames. It compares the direct attachment, not a target body alone; therefore,
       the first qualifying frame cannot commit. Values may remain exported and tunable, but
       these defaults are required for production. No generic or IK-target settling gate
       applies to `Movable` commits. This Movable-only gate does not change `Immovable`
        commit semantics.

#### Recovery, Continuity, And Settlement Boundaries

- A pending grab keeps independent values for source intent, retained contact
  identity, commanded approach target, physical realised target, and solved
  `HandBoneAttachment` attachment. They must not be substituted for one another.
  The canonical per-tick sample epoch and producer/consumer ordering are defined
  by [IK-005: Target Pipeline](../../ik/005-target-pipeline/index.md).
- Candidate eligibility and optical recognition consume independent source intent,
  never assisted presentation, a provider output, a realised target, an expected
  attachment, or solver residual. Assistance may move the commanded approach but
  cannot fabricate eligibility or trigger retries.
- The direct attachment gate remains 8 mm, 5°, and two process frames. Solver
  residual is not authored-frame calibration. When a target is unreachable or
  obstructed, bounded non-convergence preserves pending/held safety, records the
  blocking reason, and permits withdrawal or release without chasing unbounded
  commands or relaxing the gate.
- Temporal acceptance measures the final Pending sample, reparent/alignment,
  override clearance, initial held motion, and presentation handoff directly at
  their boundaries. End-to-end inputs are independently authored and never taken
  from expected output, solver state, provider output, or attachment state.

### Grab Point Transform Offset

19. Each `GrabPointCandidate` carries authored position and rotation offsets:
    - `GrabPointPositionOffsetFromHand: Vector3` — position offset from hand attachment
      to the selected/contact grab point when held. For dynamic grab points this is
      not an offset to the item root/centre unless the selected/contact point is the
      root/centre; root-authored measurements must include the root-to-selected
      transform before being used as this property.
    - `GrabPointRotationOffsetFromHand: Vector3` — rotation offset (Euler radians) from hand
      attachment to grab point when held.
20. These offsets are stored in the candidate so they are immutable at query time.
21. On commit, the selected grab point's hand-relative transform is composed from the
     authored position and rotation offsets; object local transform may include the
     inverse of the grab point's object-local transform.
22. The separate position and rotation vectors enable copy/paste from Godot editor
     and other tools that work with Vector3 values.
23. `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` enable per-animation
    or per-grab-point correction so items sit naturally in the hand regardless
    of the animation's canonical pose.
24. The authoring workflow is:
     a. Position the character hand in the desired grab pose.
     b. Place the item in the hand so it looks correct.
     c. Read the selected/contact grab point's local position and rotation (Euler)
        relative to the hand bone. If the tool reports the item root/centre instead,
        compose the root-to-selected/contact transform first.
     d. Set these as `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
        on the grab point or its associated animation resource.
25. The offsets are optional; when both are zero, the grab point's global transform
     is used directly as the held object's local transform.

### Hand Mobility During Hold

26. Grab execution behaves differently based on `IGrabbable.Mobility`:

    **Movable grabbables** (e.g. ball, prop):
    - On commit, hand parents the object to the hand bone (BoneAttachment3D).
    - After parenting, clear the hand grab target provider override so normal tracking from
      the globally selected XR hand-pose source resumes.
    - The hand becomes mobile; the parented object follows the hand bone.
    - The hand pose from the grab point's animation is maintained.

    **Immovable grabbables** (e.g. fixed prop, lever):
    - On commit, keep the hand grab target provider override active throughout the hold.
    - The hand stays constrained to the grab point transform; it cannot move freely.
    - The object is not parented to the hand bone — it remains fixed in place.
    - Hand pose from the grab point's animation is maintained.
    - If the immovable object must be parented for hierarchy reasons, it uses a
      fixed-offset attachment that does not follow hand motion; the IK target
      remains locked to the grab point.

27. For both mobility types, releasing clears the grab point and returns the IK
    target to default (the globally selected XR hand-pose source, for example controller
    position) via smooth interpolation.

### Parenting And Hand Bone

28. Parented objects use character hand `BoneAttachment3D` nodes supplied by the character's visible runtime module
    templates.
29. The hand component manages which bone attachment to use based on hand side.
30. Object parenting preserves the grab point's relative transform at time of grab
    plus the authored position and rotation offsets.

### Hand Pose Integration

31. On commit, the grab point's `GrabPointCandidate.Animation` resource is passed
     to `HandPoseController.SetHandPose()` (internal, not via `IHand`).
    The hand-pose controller preserves that same validated resource instance as
    the identity used by optical recognition and assistance (INTR-003/XR-002).
32. `HandPoseController` validates the resource as Godot `Animation` before use.
33. Hand pose transition uses the existing smooth transition from INTR-003 (default 0.2s).
34. Both left and right hand pose animations are supported while holding.
35. On release, `HandPoseController.ClearHandPose()` restores upstream animation
    pass-through.

### Physics State Preservation

36. `IGrabbable.Grab` implementation may suspend physics (e.g., RigidBody3D mode, collision)
     on the item when committing the grab.
37. `Release()` restores the object's prior physics state.
38. For a RigidBody3D test ball, this means:
     - On commit: switch to StaticBody3D or disable physics integration.
     - On release: restore to RigidBody3D and re-enable physics.
39. The mechanism for suspension/restoration is implementation-defined; the contract
     is that the object behaves physically when not held and becomes kinematic while held.

### Collision Exception Handling For Held Movables

40. While holding a Movable grabbable, collision must be temporarily disabled between
     the held body and the grabbing hand's collision proxies to prevent erratic motion.
41. Collision exceptions are added only between the held movable body and:
     - The hand target (IK target proxy collider).
     - The finger colliders of the grabbing hand.
     - The hand collider of the grabbing hand.
     - The lower-arm proxy collider of the grabbing hand.
42. The held object preserves its RigidBody3D identity; do not replace the node type
     at runtime. Instead, emulate animatable held behaviour by:
     - Suspending physics integration (freezing linear/angular velocity).
     - Parenting to the hand bone as currently designed.
     - Using the collision exception mechanism to prevent self-collision.
43. Non-grabbing hand collisions and world collisions remain active unless explicitly
     configured otherwise by the grabbable or grab point.
44. On release, all collision exceptions are removed and the object's physics state
     is restored.

### Held Collision Proxies

45. Hand component exposes a configurable `HeldCollisionTarget: CollisionObject3D`
     property that specifies which collision object receives runtime shape
     owners while holding a Movable grabbable. This is typically the hand target
     (which must be a CollisionObject3D, such as an AnimatableBody3D) or a
     dedicated proxy CollisionObject3D.
46. On commit for a Movable grabbable, after parenting the object to hand bone:
     a. Create runtime shape owners on `HeldCollisionTarget` for each enabled
        held item `CollisionShape3D`.
     b. Add the original `Shape3D` resources to those runtime owners without
        duplicating the resources or creating proxy `CollisionShape3D` nodes.
     c. Capture each runtime shape owner's hand-target-local transform relative
        to `HeldCollisionTarget` at commit, matching a manually authored
        `CollisionShape3D` child under that target with the same local transform.
     d. Disable the original item's enabled collision shapes while runtime owners
        exist. Shapes that were already disabled are not proxied.
     e. Preserve same-side collision exceptions between the held body and the
        grabbing hand (as per items 40-43 above).
     f. Toggle original shape disabled state synchronously during grab/release
        processing; record the prior disabled state for accurate restoration.
47. On release, for a Movable grabbable:
     a. Remove all runtime shape owners created on `HeldCollisionTarget`.
     b. Restore the original item's collision shapes to their prior disabled
        state (using recorded state from step 44f).
     c. Remove collision exceptions added during hold (as per item 44 above).
     d. Restore physics state (as per existing throw momentum section).
48. Runtime shape owners must preserve scene hierarchy and avoid per-grab shape
     resource duplication while enabling clean restoration on release.
49. The collision proxy implementation must ensure the held item retains effective
     world collision while held, enabling the held item to hit and interact with
     other objects in the game world.
50. Runtime shape owner transforms are captured as hand-target-local transforms
     at commit and remain fixed in `HeldCollisionTarget` local space for the
     duration of the hold. Movement of `HeldCollisionTarget` carries the proxy
     shapes. The proxy system must not continuously chase the disabled source
     `CollisionShape3D` global transforms each frame.

### Throw Momentum On Release

51. While holding a Movable grabbable that is a RigidBody3D, the system
     estimates a release velocity from the recent motion of the hand or
     held-object transform. Estimation may use delta-position over delta-time
     from recent frames, with a smoothing window; stationary hold yields
     near-zero estimated velocity.
52. On `Release()`, for a Movable physical grabbable:
     - Restore physics state (unfreeze mode, re-enable collision).
     - Transfer the estimated release velocity so the object continues along
       the throw trajectory rather than dropping from rest.
     - The velocity/impulse transfer mechanism is implementation-defined:
       direct `LinearVelocity` assignment or impulse/force application are both
       acceptable, as long as intuitive throw behaviour results for the player.
       Mass and impulse semantics are implementation-defined; the contract is
       that the object follows the release trajectory with appropriate speed.
     - Clamp or tune the transferred velocity to avoid extreme impulses.
     - Immovable grabbables do not receive throw impulse.
53. Existing collision exceptions are removed on release (as per item 47c above).

### Release

54. `Release()` must restore all involved subsystems:
     - Unparent grabbed object from BoneAttachment3D.
     - Clear hand pose via `HandPoseController.ClearHandPose()`.
     - Restore IK target to default via `IKTargetIntentProvider`.
     - Remove runtime shape owners from `HeldCollisionTarget`.
     - Restore original item collision shapes to prior disabled state.
     - Remove collision exceptions added during hold.
     - For a Movable physical grabbable, transfer estimated release velocity
       to the RigidBody3D so it follows the throw trajectory.
55. Release is idempotent: calling on already-empty hand is a no-op.

### Testing Asset

56. Define a test ball asset:
    - RigidBody3D with sphere mesh, radius 4cm (0.04m).
    - `SphericalGrabPoint` component at centre.
    - Authored in `test_ball.tscn` for photobooth verification.
57. The scene must remain discoverable and grabbable; physics is suspended on grab and
    restored on release.

### Optical Grab Lifecycle (Optical-Mode Input)

58. Input provenance: the hand tracks whether its active grab (pending or held)
     originated from controller or optical input, so committed mode transitions
     affect optical-originated grabs deterministically and never transfer grab
     ownership to the newly active input source.
59. Normative per-hand lifecycle state table for optical-mode input. Controller-mode
     input keeps the Requirements 9–18 semantics unchanged; recognition contracts
     (candidate awareness, stability, thresholds, frame space, pause suppression)
     are normative in CTRL-002 and XR-002:

     | State | Observation | Result |
     |-------|-------------|--------|
     | Idle | No eligible candidate | Do not recognise a grab, regardless of hand closure. |
     | Idle | The same eligible candidate stays selected and its closure holds at | Call `IHand.Grab()`; the ordinary |
     |      | or beyond its animation-derived power-grip threshold for the | approach (Requirements 9–18) |
     |      | stability interval | begins. |
     | Idle | The best candidate changes | Reset the stability accumulator; do not lock a speculative candidate. |
     | Pending | Valid raw articulation remains closed | Continue the existing approach with same-source refresh |
     |         | | (Requirement 17) and Movable tolerance/attachment settling |
     |         | | (Requirement 18). The candidate remains the raw recognition |
     |         | | reference while its authored reference pose visibly assists this |
     |         | | optical-originated pending hand. |
     | Pending | Stable aggregate opening | Cancel through the existing abandonment path (Requirement 16) and |
     |         | | blend assistance to current valid live tracking. |
      | Pending | Recognition dependency loss | Cancel through normal hand cleanup; invalidate |
      |         | | measurements and stability; then freeze the visual blend at its |
      |         | | current assisted interpolation. Resume it only when valid raw source |
      |         | | samples return, then target current valid projected tracking. Fresh |
      |         | | candidate recognition is required after recovery. |
     | Pending | Explicit committed mode switch | Cancel and blend assistance to current valid live tracking; do not |
     |         | | transfer ownership to the newly active source. |
     | Held | Valid articulation remains closed, including over-clenching and modest | Preserve the item and the fixed |
     |      | single-finger variation | authored pose. |
     | Held | Stable aggregate opening | Release through the ordinary `Release()` restoration (Requirement 54). |
      | Held | Recognition dependency loss | Preserve the item and fixed authored pose; invalidate |
      |      | | measurements and stability, emit no synthetic release, and pause |
      |      | | release recognition until fresh recovery recognition. |
     | Held | Tracking recovers, then a stable aggregate open holds for the | Release through the ordinary |
     |      | ordinary stability interval | `Release()` restoration. |
      | Pending or Held | Explicit committed global mode switch | Apply the source-provenance |
      |                 | | exit policy without creating a per-hand mixed source mode or transferring |
      |                 | | grab or presentation ownership. |

60. While held from optical input, the candidate's fixed authored AnimationTree
     pose owns that hand's finger presentation after the current handoff (per-hand
     authority in XR-002 and INTR-003). The optical modifier performs zero writes
     to that hand's finger bones. Gesture-reference sampling of the candidate animation
     is immutable — no registration, resource mutation, or live-pose reads — while the
     designated `HandPoseController` slot may apply that same reference. Raw optical
     joints continue to be sampled invisibly as the release detector.
61. Release via optical input performs the same restoration as explicit
     `Release()` (Requirement 54); additionally, the authored pose blends back to
     the hand's current valid optical pose rather than snapping to a stale capture.
     The opposite hand remains live optical throughout, and game-menu pause
     suppresses optical grab/release edges consistently with controller grab
     suppression.
62. Pending contact assistance applies only to optical-originated pending states.
     Controller pending states retain their existing presentation and never receive
      authored reference-pose assistance.
63. Presentation authority is published by the concrete hand instance and its
     publication generation, not by side alone. Cleanup can revoke only the
     publication it owns; stale or unrelated same-side teardown is harmless.
     Reference identity, readiness, and source-exit authority are normative in
     INTR-003 and XR-002.

### Pending-Candidate Approach Protection

64. While a `Movable` grab is Pending and the authorised candidate is a live physics body, the hand applies the
     same mutual collision exception set as the held contract (Requirements 40–41) between the pending
     candidate's body and the same-side hand target, finger colliders, hand collider, and lower-arm proxy
     collider. The authorised approach may not displace the item it is acquiring.
65. The pending candidate is suppressed from the explicit hand interaction channel (impact and sustained push)
     for the duration of the pending attempt. Suppression is held per body across concurrent attempts: it lasts
     while any hand has a pending approach against that body, so simultaneous attempts cannot push the item.
66. Protection applies when the pending grab begins and follows the live candidate through same-source refresh:
     when the protected body changes, the previous body's protection fully reverts and the new body's applies.
67. Protection fully reverts on abandonment, release, and hand teardown. On commit it reverts synchronously as
     the held-movable exceptions (Requirements 40–43) take over the identical body pairs, with no gap or
     duplicated application.
68. Candidates without a live physics body (non-physics candidates, or bodies with suspended physics) skip
     protection without error. World-versus-item and hand-versus-world collision guarantees are unchanged, and
     open-hand pushing of non-candidate items is unchanged.

### Pending-Grab Abandonment Reasons

69. A pending `Movable` grab that cannot settle is abandoned through the ordinary abandonment path
     (Requirement 16) with an observable reason, without relaxing the 8 mm/5°/two-process-frame commit gate of
     Requirement 18:
     - Non-convergence: the direct-attachment residual stops shrinking while outside the commit gate for a
       bounded interval of physics ticks (`MovableAttachmentNonConvergencePhysicsFrames`, default 90), indicating
       an unreachable or collision-limited destination.
     - Moving candidate: the candidate `RigidBody3D`'s linear velocity stays above
       `PendingMovableGrabMovingCandidateSpeedMetresPerSecond` (default 0.05 m/s) for
       `PendingMovableGrabMovingCandidatePhysicsFrames` (default 15, ≈ 0.25 s at 60 Hz) consecutive physics
       ticks; any below-threshold sample restarts the interval. The approach is chasing a live destination
       rather than converging onto a settled one.
70. The most recent abandonment reason is observable per hand through the grab lifecycle seam
     (`IHandGrabLifecycle.LastPendingGrabAbandonmentReason`), so input layers and diagnostics can distinguish the
     reasons without observing solver internals; the optical input coordinator surfaces a MovingCandidate
     abandonment through its neutral evaluation trace.
71. Both intervals are exported and implementation-tunable. The abandonment semantics change neither the commit
     gate, item offsets, calibration, nor recognition thresholds, and they do not alter `Immovable` settling
     semantics.

## In Scope

- IGrabbable discovery via group membership or Area3D.
- Candidate selection via IGrabPoint queries.
- Two-phase grab (approach, then commit).
- Hand-mobility during hold (divergent: `Movable` releases override for hand freedom;
  `Immovable` keeps override active to constrain hand to grab point).
- Authored `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` per grab point.
- Authoring workflow for offsets by manual positioning on character hand.
- IK integration via IKTargetIntentProvider.
- Object parenting via BoneAttachment3D.
- Hand pose from grab point animation (left and right).
- Physics state suspension on grab and restoration on release.
- Held collision proxy system: `HeldCollisionTarget` property, runtime shape owners
  using original `Shape3D` resources, original shape disable with prior-state tracking,
  and owner cleanup on release.
- Same-side collision exception handling for held Movables.
- Release with subsystem state restoration.
- Throw momentum: velocity estimation from hand/attachment motion and transfer
  to Movable physical grabbables on release.
- Test ball asset (RigidBody3D + SphericalGrabPoint) in `test_ball.tscn`.
- Commit-time candidate refresh for pending grabs whose selected grab-point source remains valid.
- Separate pending-grab acquisition tolerance for `Movable` grabbables without changing
  grab-point `ReachDistanceMetres` or initial stationary acquisition.
- Optical grab lifecycle: per-hand input provenance, the normative Idle/Pending/Held
  state table (Requirement 59), optical-only pending contact assistance, tracking-loss
  and mode-switch policies, fixed-pose authority with hidden release detection, and
  authored-to-current-optical blend on ordinary release.
- Pending-candidate approach protection: the authorised `Movable` candidate receives the held-contract collision
  exception set and explicit-interaction suppression while Pending, follows candidate switches, and fully reverts
  on abandonment, commit, release, and hand teardown.
- Observable pending-grab abandonment reasons — bounded non-convergence and persistently moving candidate — with
  exported tunable intervals.

## Out Of Scope

- Multi-hand grab coordination (two hands on same object).
- Physics constraints or forces while held (suspension is allowed; active forces are out).
- Animation content creation.
- Network replication.
- Inventory integration.
- Procedural grab point generation.
- Unrelated IK solver replacement or global VRIK correction. The required
  target-pipeline epoch, residual, and bounded non-convergence contracts remain
  in scope through IK-005; this specification owns the final `Movable`
  attachment-settlement gate's numerical defaults.
- Automatic offset computation (authoring only).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | User              | On grab press, the item stays in place while the hand moves |
|    |                   | to the selected grab point, with no early magnetism or tracked-finger collision. |
| 2  | User              | Grab commits only after the hand reaches and settles at target. |
| 3  | User              | While holding a `Movable` grabbable, the hand remains mobile and |
|    |                   | follows the globally selected XR hand-pose source (or default |
|    |                   | provider motion). |
| 4  | User              | While holding a `Movable` grabbable, the held item follows the |
|    |                   | hand through all movements. |
| 5  | User              | While holding an `Immovable` grabbable, the hand stays constrained |
|    |                   | to the grab point and cannot move freely. |
| 6  | User              | Both left and right hand pose animations work while holding. |
| 7  | User              | Grab points support separate authored position and |
|    |                   | rotation offsets per animation or grab point. |
| 8  | User              | Authoring workflow exists to determine offset by manually |
|    |                   | positioning an item on a character hand. |
| 9  | User              | Release restores IK, hand pose, and parenting to initial states. |
| 10 | User              | A slowly moving or rolling Movable grabbable can still be caught when |
|    |                   | it remains in valid range of the current hand during approach. |
| 11 | User              | RigidBody3D test ball is discoverable and grabbable with |
|    |                   | physics suspended while held and restored on release. |
| 12 | Technical         | Discovery uses either Godot group or Area3D with configurable range. |
| 13 | Technical         | Candidate selection is deterministic using closest acquisition |
|    |                   | distance rule; `HandTarget.Origin` must not be used for ranking. |
| 14 | Technical         | Candidate carries authored `GrabPointPositionOffsetFromHand` |
|    |                   | and `GrabPointRotationOffsetFromHand` (zero if absent). |
| 15 | Technical         | For `Immovable` grabbables, hand enters `Approaching`, moves to |
|    |                   | target via IK, then commits on implementation-defined IK settling |
|    |                   | (not on button press). |
| 16 | Technical         | For `Movable` grabbables: on commit, after parenting object to hand |
|    |                   | bone, clear the hand grab target provider override so hand can move |
|    |                   | freely while the object follows. |
| 17 | Technical         | For `Immovable` grabbables: on commit, keep hand grab target provider |
|    |                   | override active throughout the hold; hand stays constrained to grab |
|    |                   | point. |
| 18 | Technical         | On commit, grab-point hand-relative transform is composed |
|    |                   | from `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`; |
|    |                   | object local may include inverse of grab-point object-local transform. |
| 19 | Technical         | Physics is suspended on grab and restored on release for |
|    |                   | physical objects such as RigidBody3D. |
| 20 | Technical         | `Release()` restores all subsystems and is idempotent. |
| 21 | Technical         | Test ball exists as RigidBody3D with 4cm radius and centre |
|    |                   | SphericalGrabPoint in `test_ball.tscn`. |
| 22 | Technical         | Authoring workflow uses manual positioning and reads relative |
|    |                   | position and rotation (Euler) from hand bone. |
| 23 | Technical         | Grab-point animation resource is validated as Godot `Animation` |
|    |                   | before being passed to `HandPoseController.SetHandPose()`. |
| 24 | User              | While holding a Movable grabbable, the held item does not collide |
|    |                   | with the grabbing hand's colliders (fingers, hand, lower-arm) |
|    |                   | while still following hand motion; non-grabbing hand/world |
|    |                   | collisions remain unless configured otherwise. |
| 25 | Technical         | Collision exceptions are added between held movable body and |
|    |                   | same-side hand target, finger colliders, hand collider, and |
|    |                   | lower-arm proxy collider of the grabbing hand. |
| 26 | Technical         | Held object preserves RigidBody3D identity; physics is suspended |
|    |                   | while held and exceptions are removed on release. |
| 27 | User              | Releasing a held Movable physical grabbable transfers an |
|    |                   | estimated release velocity so the object follows the throw |
|    |                   | trajectory instead of dropping from rest. |
| 28 | User              | Releasing a held Movable physical grabbable from a stationary |
|    |                   | hand does not launch the object. |
| 29 | User              | Releasing an Immovable grabbable does not apply throw impulse. |
| 30 | Technical         | Release velocity is estimated from recent hand/attachment or |
|    |                   | held-object transform motion; a smoothing window or low-pass |
|    |                   | filter may be used; near-zero velocity for stationary hold. |
| 31 | Technical         | Velocity/impulse transfer is implementation-defined; mass and |
|    |                   | impulse semantics are not specified, but intuitive throw |
|    |                   | trajectory and testable behaviour are required. |
| 32 | Technical         | Implemented via validation hooks or integration tests covering |
|    |                   | non-zero release velocity (e.g. moving hand releases ball and |
|    |                   | ball continues in throw direction) and stationary release |
|    |                   | (e.g. stationary release does not launch ball). |
| 33 | User              | While holding a Movable grabbable, the held item retains |
|    |                   | effective world collision and can hit/interact with other |
|    |                   | objects while following the hand. |
| 34 | Technical         | Hand exposes configurable `HeldCollisionTarget: CollisionObject3D` |
|    |                   | property specifying where runtime shape owners attach while holding. |
| 35 | Technical         | On commit for Movable grabbable, create runtime shape owners on |
|    |                   | `HeldCollisionTarget` that reuse original `Shape3D` resources; no |
|    |                   | proxy `CollisionShape3D` nodes or duplicated shape resources are created. |
| 36 | Technical         | Toggle original shape disabled state synchronously during grab/release |
|    |                   | record prior disabled state for accurate restoration. |
| 37 | Technical         | On release, remove runtime shape owners from `HeldCollisionTarget` and |
|    |                   | restore original item shapes to recorded prior disabled state. |
| 38 | Technical         | Same-side collision exceptions are preserved during shape-owner-based |
|    |                   | held collision; exceptions are removed on release. |
| 39 | Technical         | Runtime shape owner transforms are captured once as hand-target-local |
|    |                   | transforms at commit, then remain fixed while |
|    |                   | `HeldCollisionTarget` movement carries them; disabled original shapes |
|    |                   | are not proxied. |
| 40 | Technical         | A valid same-source pending `Movable` candidate may refresh at commit |
|    |                   | and use the tolerance in Criterion 41; a different source never replaces it. |
|    |                   | If the selected source or candidate becomes invalid before settlement, normal |
|    |                   | cleanup abandons the pending grab, releases/returns the provider override, |
|    |                   | performs no parenting or physics transition, and leaves the hand unheld. |
| 41 | Technical         | Pending-grab refresh may use a separate `Movable`-only acquisition |
|    |                   | tolerance without changing grab-point `ReachDistanceMetres` or initial |
|    |                   | stationary acquisition. |
| 42 | Technical         | The sole final settlement and commit-eligibility gate for a `Movable` |
|    |                   | commit compares the actual `HandBoneAttachment` (`BoneAttachment3D`) with |
|    |                   | `candidate.GrabPointTransform × inverse(candidate.GrabPointOffsetFromHand)`. |
|    |                   | Direct-attachment position and orientation must be within the required 8 mm |
|    |                   | (0.008 m) and 5° production defaults, respectively, for two consecutive process |
|    |                   | frames; a target-body-only comparison cannot commit. No generic or IK-target |
|    |                   | settling gate applies to `Movable` commits. Values may remain exported and tunable. |
|    |                   | A same-source refresh that materially changes the target resets the count, without |
|    |                   | changing `Immovable` semantics. |
| 43 | User              | In optical mode, a stable candidate-specific closure starts the grab and |
|    |                   | a stable aggregate opening cancels a pending approach or releases a held |
|    |                   | object. |
| 44 | User              | An optical pending grab visibly transitions to its candidate reference pose; |
|    |                   | raw opening or mode switch cancels it and blends to current valid live tracking. |
|    |                   | Raw-loss cancellation freezes its current assisted interpolation until valid raw |
|    |                   | source samples return, then targets current valid projected tracking. Controller |
|    |                   | pending states receive no assistance. |
| 45 | User              | While held from optical input, the hand shows the fixed authored grab |
|    |                   | pose while the opposite hand stays live tracked. |
| 46 | User              | Temporary tracking loss while held preserves the item and its fixed |
|    |                   | pose with no synthetic release; a recovered stable open releases. |
| 47 | User              | Explicit committed mode switches may cancel pending or release held |
|    |                   | optical grabs without transferring ownership. |
| 48 | Technical         | The Requirement 59 state table is verified end to end: idle no-candidate |
|    |                   | non-recognition, stability reset on candidate change, optical pending |
|    |                   | assistance with raw detection, cancel-and-blend on opening/mode switch, and |
|    |                   | loss cancellation both mid-partial assistance and after pending assistance has |
|    |                   | completed: freeze at the current assisted interpolation, resume only on valid raw |
|    |                   | samples, then target current valid projected tracking. Held preserve on |
|    |                   | over-clench and modest single-finger variation, held release on stable open, |
|    |                   | and held-loss preserve-then-recover behaviour are also verified. |
| 49 | Technical         | Per-hand input provenance makes mode transitions deterministic for |
|    |                   | optical-originated grabs; ownership is never implicitly transferred. |
| 50 | Technical         | The candidate animation is sampled immutably while held; after the handoff |
|    |                   | the authored AnimationTree pose is the sole presentation owner and the optical |
|    |                   | modifier performs zero writes. Optical release performs the same `Release()` |
|    |                   | restoration and blends to current valid optical pose; the opposite hand remains live. |
| 51 | Technical         | The real reference-player/MockXR RigidBody3D test-ball path commits only after |
|    |                   | two consecutive direct-attachment samples are within 8 mm (0.008 m) and 5°. |
| 52 | Technical         | The first direct-attachment sample within 8 mm (0.008 m) and 5° is insufficient |
|    |                   | to commit a `Movable` grab. |
| 53 | Technical         | A `Movable` candidate with direct-attachment position residual above 8 mm |
|    |                   | (0.008 m), or angular residual above 5°, does not commit. |
| 54 | Technical         | A non-centre cylindrical stick remains pending and unparented while its direct |
|    |                   | attachment position residual exceeds 8 mm (0.008 m), even when orientation is |
|    |                   | within 5°; its provider override remains active and the object stays stationary. |
| 55 | Technical         | Regression coverage confirms this gate change preserves existing Movable and |
|    |                   | Immovable behaviour; controller and optical input/provenance; optical pending |
|    |                   | assistance and held authority; VRIK, actuator, and calibration contracts; stationary |
|    |                   | approach with no magnetism or tracked-finger collision; and same-source refresh |
|    |                   | and candidate-loss cancellation. |
| 56 | User              | Nominal success destinations are anatomically reachable in a plausible pose; |
|    |                   | standing is not required. |
| 57 | Technical         | Occupied holders are excluded before candidate ranking, while commit remains |
|    |                   | atomic against races and no reservation or two-hand expansion is added. |
| 58 | Technical         | Each end-to-end continuity measurement samples the direct temporal boundary and |
|    |                   | uses independently authored input, never expected output, provider output, solved |
|    |                   | attachment, or solver residual as input. |
| 59 | Technical         | Source intent, retained contact, commanded approach, realised target, and solved |
|    |                   | attachment remain distinct at the IK-005 epoch. Assistance cannot create eligibility; |
|    |                   | residual is not calibration; unreachable or obstructed targets follow the bounded |
|    |                   | non-convergence policy without relaxing the 8 mm/5°/two-frame gate. |
| 60 | Technical         | Recognition dependency loss cancels optical Pending, preserves Held, invalidates |
|    |                   | measurements and stability, and requires fresh recognition after recovery. |
| 61 | Technical         | Presentation cleanup is scoped to the publisher hand instance and generation; |
|    |                   | stale or unrelated same-side cleanup cannot revoke the current authority. |
| 62 | User              | While a grab is pending on a free `Movable` physics body, the authorised |
|    |                   | candidate is not physically disturbed: its speed stays within a bounded limit |
|    |                   | (default 0.05 m/s), it stays within a bounded displacement of rest (default |
|    |                   | 0.05 m), and the approach converges without knocking the item away. |
| 63 | User              | Open-hand pushing of non-candidate items is unchanged; only the authorised |
|    |                   | pending candidate is protected. |
| 64 | Technical         | Pending protection mirrors the held exception set (Requirements 40–41) between |
|    |                   | the candidate body and the same-side hand target, finger, hand, and lower-arm |
|    |                   | proxies; it applies at pending begin, moves with candidate switches, and fully |
|    |                   | reverts on abandonment, release, and hand teardown. |
| 65 | Technical         | Commit transitions synchronously from pending protection into the held |
|    |                   | exceptions over identical body pairs — no gap, no duplicate — and completes |
|    |                   | within bounded ticks with at most 10 mm of commit-boundary item discontinuity. |
| 66 | Technical         | The pending candidate is suppressed from the explicit hand interaction channel |
|    |                   | per body across concurrent attempts; non-physics candidates skip protection, and |
|    |                   | world-versus-item and hand-versus-world collision guarantees are unchanged. |
| 67 | Technical         | A candidate whose `RigidBody3D` linear velocity stays above the exported |
|    |                   | threshold (default 0.05 m/s) for the exported consecutive physics-tick interval |
|    |                   | (default 15) — restarted by any below-threshold sample — abandons Pending with |
|    |                   | the distinct MovingCandidate reason. |
| 68 | Technical         | Abandonment reasons are observable through the per-hand lifecycle seam, and a |
|    |                   | MovingCandidate abandonment surfaces through the optical coordinator's neutral |
|    |                   | evaluation trace; the 8 mm/5°/two-process-frame commit gate, item offsets, |
|    |                   | calibration, and recognition thresholds are unchanged. |

## References

- [Project Specifications Index](../../index.md)
- [INTR-001: Grabbable Interface](../001-grabbable/index.md)
- [INTR-001-A: Spherical Grab Point](../001-grabbable/spherical-grab-point.md)
- [INTR-003: Hands](../003-hands/index.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
- [IK Implementation Notes](../../ik/implementation-notes.md)
- [XR-002: Optical Hand Tracking](../../xr/002-optical-hand-tracking/index.md)
- [CTRL-002: Hand Grab Input](../../ctrl/002-hand-grab-input/index.md)
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
- `game/src/Interaction/` (implementation namespace)
