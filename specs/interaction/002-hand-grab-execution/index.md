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
- Maintains hand mobility (IK follows controller/default provider) while held.
- Releases held object and restores all involved subsystems to initial states.
- Supports separate authored position and rotation offsets per animation/grab point.
- Provides an authoring workflow to position an item on a character hand.

## User Requirements

1. On grab press, the item stays in place while the hand moves to the
   selected grab point.
2. The grab commits only after the hand reaches/settles at the target.
3. While held, the hand remains mobile and follows normal controller or
   default provider motion.
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

## Technical Requirements

### Discovery

1. Hand component exposes configurable `DiscoveryRangeMetres` property (default 0.3m).
2. Discovery uses either Godot group membership (e.g., "grabbable") or Area3D overlap.
   - Group-based: queries all nodes in group, filters by distance to hand transform.
   - Area3D-based: configures Area3D with `CollisionShape3D` sphere of discovery range.
3. Discovery returns the closest IGrabbable within range that yields a valid grab point.

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
12. Hand monitors IK settling using an implementation-defined threshold (e.g., position
    within 2mm and angular delta below 5° for 2 consecutive frames).
13. Once settled, hand enters `Grabbing` and calls `IGrabbable.Grab(candidate)` to commit.
14. On commit success, hand parents the object to the appropriate BoneAttachment3D node.
15. The selected grab point's hand-relative transform equals the transform composed from
     `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`; object local may include the inverse
     of the grab point's object-local transform.
16. If the hand loses the candidate (e.g., object removed) before settling, the grab
    is abandoned and the hand returns to idle.
17. On commit, the hand may refresh the selected grab point candidate using the
     current hand transform, provided the refreshed candidate comes from the same
     grab-point source as the pending selection. This allows slowly moving Movable
     grabbables to be caught without accepting a different grab source.
18. The commit-time refresh may apply a separate configurable acquisition tolerance
    for `Movable` grabbables only. This tolerance does not change `ReachDistanceMetres`
    on grab points and must not loosen initial stationary acquisition.

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
    - After parenting, clear the hand grab target provider override so normal or
      controller tracking resumes.
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
    target to default (e.g., controller position) via smooth interpolation.

### Parenting And Hand Bone

28. Parented objects use character hand `BoneAttachment3D` nodes supplied by the character's visible runtime module
    templates.
29. The hand component manages which bone attachment to use based on hand side.
30. Object parenting preserves the grab point's relative transform at time of grab
    plus the authored position and rotation offsets.

### Hand Pose Integration

31. On commit, the grab point's `GrabPointCandidate.Animation` resource is passed
    to `HandPoseController.SetHandPose()` (internal, not via `IHand`).
32. `HandPoseController` validates the resource as Godot `Animation` before use.
33. Hand pose transition uses the existing smooth transition from BODY-001 (default 0.2s).
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

## Out Of Scope

- Multi-hand grab coordination (two hands on same object).
- Physics constraints or forces while held (suspension is allowed; active forces are out).
- Animation content creation.
- Network replication.
- Inventory integration.
- Procedural grab point generation.
- IK solver modifications.
- Automatic offset computation (authoring only).

## Acceptance Criteria

| ID | Requirement Layer | Criterion |
|----|-------------------|-----------|
| 1  | User              | On grab press, the item stays in place while the hand moves |
|    |                   | to the selected grab point. |
| 2  | User              | Grab commits only after the hand reaches and settles at target. |
| 3  | User              | While holding a `Movable` grabbable, the hand remains mobile and |
|    |                   | follows normal controller or default provider motion. |
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
| 15 | Technical         | Hand enters `Approaching` state, moves to target via IK, then |
|    |                   | commits on settling (not on button press). |
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
| 40 | Technical         | Commit-time candidate refresh accepts a moved pending grabbable only |
|    |                   | when the current candidate is still produced by the same grab-point source. |
| 41 | Technical         | Pending-grab refresh may use a separate `Movable`-only acquisition |
|    |                   | tolerance without changing grab-point `ReachDistanceMetres` or initial |
|    |                   | stationary acquisition. |

## References

- [Project Specifications Index](../../index.md)
- [INTR-001: Grabbable Interface](../001-grabbable/index.md)
- [INTR-001-A: Spherical Grab Point](../001-grabbable/spherical-grab-point.md)
- [BODY-001: Hands](../../body/001-hands/index.md)
- [IK-002: Arm And Shoulder IK System](../../ik/002-arm-shoulder-ik/index.md)
- [IK Implementation Notes](../../ik/implementation-notes.md)
- [CORE-005: Scene Installer System](../../core/005-scene-installer-system/index.md)
- `game/src/Interaction/` (implementation namespace)
