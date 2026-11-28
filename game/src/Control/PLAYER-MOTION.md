# Player Motion

Normal player installation uses the shared feminine source set at
`res://assets/animations/locomotion/feminine/set.json`. No locomotion activation flags are required.
See [Source Data](../../assets/animations/locomotion/feminine/README.md) and
[Shared Motion Runtime](../Motion/README.md).

## Controls and Ownership

Left primary selects forward/backward, non-crossing lateral or crossing travel. Right primary steers while
travelling and selects stationary left/right turning while stationary. Release retires owned work immediately;
an idle connection may settle the body. Held travel retains its stable input frame, continuous source phase,
60°/s full walking steering and .75 backward requested-speed ratio.

Stationary turns map the two primary foot lifts across a cycle of up to 120°, using the same measured source
angles as NPC terminal facing. Stick magnitude sets the angle when the cycle begins; changing strength without
changing direction leaves the current cycle intact. Source frames and articulation retain their timing.
Generated clip duration is model-selected, so the displayed cycle need not last exactly two seconds.

Only generated root translation goes through `MoveAndCollide`. During stationary turns the actor follows the
mapped source yaw between the previous and current lift, sampled at the played fraction of the returned clip.
The generated hips retain their world orientation relative to this moving actor frame. Walking facing still
comes from the generated hips.
Post-`SkeletonUpdated` observations supply history. Existing live XR, head/hand/gaze/finger/VRIK ownership is
unchanged. Game owns and drains workers; cancellation invalidates requests, not an in-progress native call.

## Independent Diagnostics

`PLAYER_MOTION_TARGET=1` shows pending/active generation guides and the shorter prediction marker.
`PLAYER_MOTION_TRACE=<label>` records input and pose evidence to a unique task-local session.
`PLAYER_ANIMATION_BODY=1` is a separate body diagnostic, not normal tracked gameplay and not suitable for
normal hand/grab acceptance. `MOTIONBRICKS_MODEL` remains an optional model-path override.

## Remaining Limits

The preferred source's stepping/foot-sliding improvement is accepted; its initial twitch is explicitly deferred.
Raw repetitions are not certified seamless loops. Generated feet, headset comfort and interaction quality
still need human review; source adoption does not certify naturalness or eliminate inference holds.
Known native shutdown and physical-input/rendered-origin risks remain open, not fixed by this migration.

Manual headset verification remains required: start with diagnostics off, check tracking/hands/grabbing, travel,
steer, release, turn both ways and change intent. Stop if the view or hands behave unexpectedly. No head collider
or new wall-safety mechanism is provided.
