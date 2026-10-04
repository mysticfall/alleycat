class_name AnimationPoseSampler
extends RefCounted

# Forward-kinematics sampling of skeleton-bone animation tracks without a live Skeleton3D.
# Every bone-local channel (position, rotation, scale) comes from its animation track when one exists and
# otherwise from that bone's rest pose, matching what a Skeleton3D evaluates for an untracked channel. Tracked
# positions are multiplied by the skeleton's motion scale, as AnimationMixer does for bone position tracks.

const DEFAULT_SKELETON_PATH := "%GeneralSkeleton"


## Captures the skeleton's motion scale and each bone's parent name and rest transform (keyed by bone name).
static func skeleton_model(skeleton: Skeleton3D) -> Dictionary:
	var bones := {}
	for bone in skeleton.get_bone_count():
		var parent := skeleton.get_bone_parent(bone)
		bones[skeleton.get_bone_name(bone)] = {
			"parent": skeleton.get_bone_name(parent) if parent >= 0 else "",
			"rest": skeleton.get_bone_rest(bone),
		}
	return {"motion_scale": skeleton.motion_scale, "bones": bones}


## Whether the modelled skeleton has a bone named `joint`.
static func has_joint(model: Dictionary, joint: String) -> bool:
	return (model.get("bones", {}) as Dictionary).has(joint)


## Bone-local position at `time`, falling back to the rest origin when the animation has no position track.
## Tracked positions are scaled by the skeleton's motion scale.
static func joint_position(animation: Animation, joint: String, time: float, model: Dictionary, skeleton_path: String = DEFAULT_SKELETON_PATH) -> Vector3:
	var track := animation.find_track(_track_path(skeleton_path, joint), Animation.TYPE_POSITION_3D)
	if track >= 0:
		return animation.position_track_interpolate(track, time) * float(model.get("motion_scale", 1.0))
	return _rest(model, joint).origin


## Bone-local rotation at `time`, falling back to the rest rotation when the animation has no rotation track.
static func joint_rotation(animation: Animation, joint: String, time: float, model: Dictionary, skeleton_path: String = DEFAULT_SKELETON_PATH) -> Quaternion:
	var track := animation.find_track(_track_path(skeleton_path, joint), Animation.TYPE_ROTATION_3D)
	if track >= 0:
		return animation.rotation_track_interpolate(track, time).normalized()
	return _rest(model, joint).basis.get_rotation_quaternion()


## Bone-local scale at `time`, falling back to the rest scale when the animation has no scale track.
static func joint_scale(animation: Animation, joint: String, time: float, model: Dictionary, skeleton_path: String = DEFAULT_SKELETON_PATH) -> Vector3:
	var track := animation.find_track(_track_path(skeleton_path, joint), Animation.TYPE_SCALE_3D)
	if track >= 0:
		return animation.scale_track_interpolate(track, time)
	return _rest(model, joint).basis.get_scale()


## Bone-local pose at `time`, composed from the per-channel samples above.
static func joint_local(animation: Animation, joint: String, time: float, model: Dictionary, skeleton_path: String = DEFAULT_SKELETON_PATH) -> Transform3D:
	var basis := Basis(joint_rotation(animation, joint, time, model, skeleton_path)).scaled_local(joint_scale(animation, joint, time, model, skeleton_path))
	return Transform3D(basis, joint_position(animation, joint, time, model, skeleton_path))


## Skeleton-space pose at `time` (the Skeleton3D global bone pose). `cache` memoises ancestors for one `time` only.
static func joint_global(animation: Animation, joint: String, time: float, model: Dictionary, cache: Dictionary = {}, skeleton_path: String = DEFAULT_SKELETON_PATH) -> Transform3D:
	if cache.has(joint):
		return cache[joint]
	var local := joint_local(animation, joint, time, model, skeleton_path)
	var parent := String((_bones(model).get(joint, {}) as Dictionary).get("parent", ""))
	var result := local if parent.is_empty() else joint_global(animation, parent, time, model, cache, skeleton_path) * local
	cache[joint] = result
	return result


static func _bones(model: Dictionary) -> Dictionary:
	return model.get("bones", {}) as Dictionary


static func _rest(model: Dictionary, joint: String) -> Transform3D:
	var entry: Variant = _bones(model).get(joint)
	if entry is Dictionary and (entry as Dictionary).get("rest") is Transform3D:
		return (entry as Dictionary)["rest"]
	return Transform3D.IDENTITY


static func _track_path(skeleton_path: String, joint: String) -> NodePath:
	return NodePath("%s:%s" % [skeleton_path, joint])
