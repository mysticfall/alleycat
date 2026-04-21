# Pose State Machine (IK-004)

This folder hosts the VRIK pose state machine framework delivered under
[IK-004](../../../../specs/characters/ik/004-vrik-pose-state-machine/index.md).

## Increment 1 Scope

Increment 1 shipped the framework types plus a minimal concrete `StandingPoseState` and a
standing-only hip reconciliation profile. The state machine and hip modifier were not yet
referenced by any scene.

## Increment 2 Scope

Increment 2 ships:

- `CrouchingPoseState` and `HeadOffsetPoseTransition` for the Standing <-> Crouching
  edge using normalised local head offset with configurable Threshold and Direction.
- `TimeSeekAnimationBinding` driving an `AnimationNodeTimeSeek` parameter on the scene's
  `AnimationTree`. All tree parameter paths and state names are exported so authoring changes
  do not require a code recompile.
- `HeadTrackingHipProfile` (1:1 head-tracking hip) shared by Standing and Crouching. The
  profile returns an absolute hip target in skeleton-local space computed from the head
  viewpoint offset and the rest hip pose. It deliberately does not read the currently animated
  hip because that value is being scrubbed by `TimeSeek` each tick; mixing the two causes a
  feedback loop and manifests as spine flicker during crouch descent.
- `PlayerVRIK` integration: the XR bridge builds a `PoseStateContext` each `_Process` tick
  (before the AnimationTree samples) and forwards it to `PoseStateMachine.Tick`.
- Authored scene resources under `assets/characters/ik/pose/` plus an `AnimationTree` whose root holds
  a single `StandingCrouching` state. The state wraps a blend tree of
  `TimeSeek -> AnimationNodeAnimation("female/Crouch-seek")` so `TimeSeek` runs continuously
  regardless of which pose state is active — the state-machine framework is retained for
  future pose categories (sitting, crawling, lying) that will introduce divergent
  AnimationTree states.

Hip reconciliation executes inside `HipReconciliationModifier` (`SkeletonModifier3D`) ordered
immediately after `VRIKBeginStage` and before the IK controllers, preserving AC-HR-07 ordering.
The modifier applies the pending hip target via `Skeleton3D.SetBonePosePosition`, replacing
(not adding to) the animated hip position.

## Resource Layout

Authored `.tres` files under `assets/characters/ik/pose/`:

- `head_tracking_hip_profile.tres`
- `standing_animation_binding.tres` / `crouching_animation_binding.tres`
- `standing_to_crouching_transition.tres` / `crouching_to_standing_transition.tres`
- `standing_pose_state.tres` / `crouching_pose_state.tres`

Authored `AnimationTree` tree-root and library:

- `game/assets/characters/reference/female/pose_state_machine_tree.tres`
- `game/assets/characters/reference/female/animation_library.tres`
