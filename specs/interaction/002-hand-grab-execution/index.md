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
12. Multiple hands operate independently without interference.
13. A physical RigidBody3D test ball remains discoverable and grabbable
    with physics suspended while held and restored on release.
14. While holding a Movable grabbable, the held item must not collide with
    the grabbing hand's colliders (fingers, hand, lower-arm proxy) while
    still following hand motion. Non-grabbing hand and world collisions
    remain unless explicitly configured otherwise.

## Technical Requirements

### Discovery

1. Hand component exposes configurable `DiscoveryRangeMetres` property (default 0.3m).
2. Discovery uses either Godot group membership (e.g., "grabbable") or Area3D overlap.
   - Group-based: queries all nodes in group, filters by distance to hand transform.
   - Area3D-based: configures Area3D with `CollisionShape3D` sphere of discovery range.
3. Discovery returns the closest IGrabbable within range that yields a valid grab point.

### Candidate Selection

4. Hand calls `IGrabbable.GetGrabPoint(Side, handTransform)` on discovered candidates.
5. Selection uses the closest candidate by `GrabPointCandidate.HandTarget.Origin` distance.
6. If multiple candidates are equally close, holder order (as defined in INTR-001) is tie-breaker.
7. Candidate selection is deterministic: same hand position yields same result.
8. Candidate includes the grab point's authored `GrabPointPositionOffsetFromHand` and
   `GrabPointRotationOffsetFromHand` (see section below).

### Two-Phase Grab: Approach and Commit

9. On candidate selection, hand records the selected candidate and enters `Approaching` state.
10. Hand forwards the candidate's `HandTarget` (including grab-point rotation) to
    `IKTargetStateProvider` for smooth interpolation.
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

### Grab Point Transform Offset

17. Each `GrabPointCandidate` carries authored position and rotation offsets:
    - `GrabPointPositionOffsetFromHand: Vector3` — position offset from hand attachment
      to grab point when held.
    - `GrabPointRotationOffsetFromHand: Vector3` — rotation offset (Euler radians) from hand
      attachment to grab point when held.
18. These offsets are stored in the candidate so they are immutable at query time.
19. On commit, the selected grab point's hand-relative transform is composed from the
     authored position and rotation offsets; object local transform may include the
     inverse of the grab point's object-local transform.
20. The separate position and rotation vectors enable copy/paste from Godot editor
     and other tools that work with Vector3 values.
21. `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` enable per-animation
    or per-grab-point correction so items sit naturally in the hand regardless
    of the animation's canonical pose.
22. The authoring workflow is:
     a. Position the character hand in the desired grab pose.
     b. Place the item in the hand so it looks correct.
     c. Read the item's local position and rotation (Euler) relative to the hand bone.
     d. Set these as `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`
        on the grab point or its associated animation resource.
23. The offsets are optional; when both are zero, the grab point's global transform
     is used directly as the held object's local transform.

### Hand Mobility During Hold

24. Grab execution behaves differently based on `IGrabbable.Mobility`:

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

25. For both mobility types, releasing clears the grab point and returns the IK
    target to default (e.g., controller position) via smooth interpolation.

### Parenting And Hand Bone

26. Parented object uses `BoneAttachment3D` nodes authored in `reference_female.tscn`.
27. The hand component manages which bone attachment to use based on hand side.
28. Object parenting preserves the grab point's relative transform at time of grab
    plus the authored position and rotation offsets.

### Hand Pose Integration

29. On commit, the grab point's `GrabPointCandidate.Animation` resource is passed
    to `HandPoseController.SetHandPose()` (internal, not via `IHand`).
30. `HandPoseController` validates the resource as Godot `Animation` before use.
31. Hand pose transition uses the existing smooth transition from BODY-001 (default 0.2s).
32. Both left and right hand pose animations are supported while holding.
33. On release, `HandPoseController.ClearHandPose()` restores upstream animation
    pass-through.

### Physics State Preservation

34. `IGrabbable.Grab` implementation may suspend physics (e.g., RigidBody3D mode, collision)
     on the item when committing the grab.
35. `Release()` restores the object's prior physics state.
36. For a RigidBody3D test ball, this means:
     - On commit: switch to StaticBody3D or disable physics integration.
     - On release: restore to RigidBody3D and re-enable physics.
37. The mechanism for suspension/restoration is implementation-defined; the contract
     is that the object behaves physically when not held and becomes kinematic while held.

### Collision Exception Handling For Held Movables

38. While holding a Movable grabbable, collision must be temporarily disabled between
     the held body and the grabbing hand's collision proxies to prevent erratic motion.
39. Collision exceptions are added only between the held movable body and:
     - The hand target (IK target proxy collider).
     - The finger colliders of the grabbing hand.
     - The hand collider of the grabbing hand.
     - The lower-arm proxy collider of the grabbing hand.
40. The held object preserves its RigidBody3D identity; do not replace the node type
     at runtime. Instead, emulate animatable held behaviour by:
     - Suspending physics integration (freezing linear/angular velocity).
     - Parenting to the hand bone as currently designed.
     - Using the collision exception mechanism to prevent self-collision.
41. Non-grabbing hand collisions and world collisions remain active unless explicitly
     configured otherwise by the grabbable or grab point.
42. On release, all collision exceptions are removed and the object's physics state
     is restored.

### Throw Momentum On Release

43. While holding a Movable grabbable that is a RigidBody3D, the system
    estimates a release velocity from the recent motion of the hand or
    held-object transform. Estimation may use delta-position over delta-time
    from recent frames, with a smoothing window; stationary hold yields
    near-zero estimated velocity.
44. On `Release()`, for a Movable physical grabbable:
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
45. Existing collision exceptions are removed on release (as per item 42 above).

### Release

46. `Release()` must restore all involved subsystems:
    - Unparent grabbed object from BoneAttachment3D.
    - Clear hand pose via `HandPoseController.ClearHandPose()`.
    - Restore IK target to default via `IKTargetStateProvider`.
    - Remove collision exceptions added during hold.
    - For a Movable physical grabbable, transfer estimated release velocity
      to the RigidBody3D so it follows the throw trajectory.
47. Release is idempotent: calling on already-empty hand is a no-op.

### Testing Asset

47. Define a test ball asset:
    - RigidBody3D with sphere mesh, radius 4cm (0.04m).
    - `SphericalGrabPoint` component at centre.
    - Authored in `test_ball_grabbable.tscn` for photobooth verification.
48. The scene must remain discoverable and grabbable; physics is suspended on grab and
    restored on release.

## In Scope

- IGrabbable discovery via group membership or Area3D.
- Candidate selection via IGrabPoint queries.
- Two-phase grab (approach, then commit).
- Hand-mobility during hold (divergent: `Movable` releases override for hand freedom;
  `Immovable` keeps override active to constrain hand to grab point).
- Authored `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand` per grab point.
- Authoring workflow for offsets by manual positioning on character hand.
- IK integration via IKTargetStateProvider.
- Object parenting via BoneAttachment3D.
- Hand pose from grab point animation (left and right).
- Physics state suspension on grab and restoration on release.
- Release with subsystem state restoration.
- Throw momentum: velocity estimation from hand/attachment motion and transfer
  to Movable physical grabbables on release.
- Test ball asset (RigidBody3D + SphericalGrabPoint) in `test_ball_grabbable.tscn`.

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
| 4a | User              | While holding an `Immovable` grabbable, the hand stays constrained |
|    |                   | to the grab point and cannot move freely. |
| 5  | User              | Both left and right hand pose animations work while holding. |
| 6  | User              | Grab points support separate authored position and |
|    |                   | rotation offsets per animation or grab point. |
| 7  | User              | Authoring workflow exists to determine offset by manually |
|    |                   | positioning an item on a character hand. |
| 8  | User              | Release restores IK, hand pose, and parenting to initial states. |
| 9  | User              | RigidBody3D test ball is discoverable and grabbable with |
|    |                   | physics suspended while held and restored on release. |
| 10 | Technical         | Discovery uses either Godot group or Area3D with configurable range. |
| 11 | Technical         | Candidate selection is deterministic using closest-distance rule. |
| 12 | Technical         | Candidate carries authored `GrabPointPositionOffsetFromHand` |
|    |                   | and `GrabPointRotationOffsetFromHand` (zero if absent). |
| 13 | Technical         | Hand enters `Approaching` state, moves to target via IK, then |
|    |                   | commits on settling (not on button press). |
| 14 | Technical         | For `Movable` grabbables: on commit, after parenting object to hand |
|    |                   | bone, clear the hand grab target provider override so hand can move |
|    |                   | freely while the object follows. |
| 15 | Technical         | For `Immovable` grabbables: on commit, keep hand grab target provider |
|    |                   | override active throughout the hold; hand stays constrained to grab |
|    |                   | point. |
| 16 | Technical         | On commit, grab-point hand-relative transform is composed |
|    |                   | from `GrabPointPositionOffsetFromHand` and `GrabPointRotationOffsetFromHand`; |
|    |                   | object local may include inverse of grab-point object-local transform. |
| 17 | Technical         | Physics is suspended on grab and restored on release for |
|    |                   | physical objects such as RigidBody3D. |
| 18 | Technical         | `Release()` restores all subsystems and is idempotent. |
| 19 | Technical         | Test ball exists as RigidBody3D with 4cm radius and centre |
|    |                   | SphericalGrabPoint in `test_ball_grabbable.tscn`. |
| 20 | Technical         | Authoring workflow uses manual positioning and reads relative |
|    |                   | position and rotation (Euler) from hand bone. |
| 21 | Technical         | Grab-point animation resource is validated as Godot `Animation` |
|    |                   | before being passed to `HandPoseController.SetHandPose()`. |
| 22 | User              | While holding a Movable grabbable, the held item does not collide |
|    |                   | with the grabbing hand's colliders (fingers, hand, lower-arm) |
|    |                   | while still following hand motion; non-grabbing hand/world |
|    |                   | collisions remain unless configured otherwise. |
| 23 | Technical         | Collision exceptions are added between held movable body and |
|    |                   | same-side hand target, finger colliders, hand collider, and |
|    |                   | lower-arm proxy collider of the grabbing hand. |
| 24 | Technical         | Held object preserves RigidBody3D identity; physics is suspended |
|    |                   | while held and exceptions are removed on release. |
| 25 | User              | Releasing a held Movable physical grabbable transfers an |
|    |                   | estimated release velocity so the object follows the throw |
|    |                   | trajectory instead of dropping from rest. |
| 26 | User              | Releasing a held Movable physical grabbable from a stationary |
|    |                   | hand does not launch the object. |
| 27 | User              | Releasing an Immovable grabbable does not apply throw impulse. |
| 28 | Technical         | Release velocity is estimated from recent hand/attachment or |
|    |                   | held-object transform motion; a smoothing window or low-pass |
|    |                   | filter may be used; near-zero velocity for stationary hold. |
| 29 | Technical         | Velocity/impulse transfer is implementation-defined; mass and |
|    |                   | impulse semantics are not specified, but intuitive throw |
|    |                   | trajectory and testable behaviour are required. |
| 30 | Technical         | Implemented via validation hooks or integration tests covering |
|    |                   | non-zero release velocity (e.g. moving hand releases ball and |
|    |                   | ball continues in throw direction) and stationary release |
|    |                   | (e.g. stationary release does not launch ball). |

## References

- [Project Specifications Index](../../index.md)
- [INTR-001: Grabbable Interface](../001-grabbable/index.md)
- [INTR-001-A: Spherical Grab Point](../001-grabbable/spherical-grab-point.md)
- [BODY-001: Hands](../../body/001-hands/index.md)
- [IK-002: Arm And Shoulder IK System](../../characters/ik/002-arm-shoulder-ik/index.md)
- [IK Implementation Notes](../../characters/ik/implementation-notes.md)
- `game/assets/characters/reference/female/reference_female.tscn`
- `game/src/Interaction/` (implementation namespace)
