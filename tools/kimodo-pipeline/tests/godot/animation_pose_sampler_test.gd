extends SceneTree

# Checks AnimationPoseSampler against poses the engine itself evaluates: an AnimationPlayer drives a real
# Skeleton3D, and Skeleton3D.get_bone_global_pose() is the oracle. The fixture deliberately leaves LeftFoot
# without a position track and LeftToes without any track, which previously sampled as zero/identity. A second pass
# uses a fresh rig with a non-unit Skeleton3D.motion_scale, which the engine applies to every bone position track.

const TIME := 0.25
const TOLERANCE := 0.00001

var passed := 0
var failed := 0
var sampler: Script


func _init() -> void:
	sampler = _load_helper("animation_pose_sampler.gd")
	var rig := Node3D.new()
	rig.name = "Rig"
	get_root().add_child(rig)
	var skeleton := _skeleton(rig)
	var model: Dictionary = sampler.skeleton_model(skeleton)
	var animation := _animation()
	_expect_rest_fallback(animation, skeleton, model)
	_expect_tracked_channels_sample_tracks(animation, model)
	_expect_engine_global_poses(rig, skeleton, animation, model)
	rig.free()
	var scaled_rig := Node3D.new()
	scaled_rig.name = "ScaledRig"
	get_root().add_child(scaled_rig)
	var scaled_skeleton := _skeleton(scaled_rig)
	scaled_skeleton.motion_scale = 0.8
	var scaled_model: Dictionary = sampler.skeleton_model(scaled_skeleton)
	_expect_vector(sampler.joint_position(animation, "Hips", TIME, scaled_model), Vector3(0.0, 0.95, 0.0).lerp(Vector3(0.02, 1.02, -0.03), TIME) * 0.8, "tracked Hips position follows the skeleton motion scale")
	_expect_engine_global_poses(scaled_rig, scaled_skeleton, animation, scaled_model)
	scaled_rig.free()
	print("ANIM002_POSE_SAMPLER_CHECKS=%s passed=%d failed=%d" % ["passed" if failed == 0 else "failed", passed, failed])
	quit(1 if failed > 0 else 0)


func _skeleton(rig: Node3D) -> Skeleton3D:
	var skeleton := Skeleton3D.new()
	skeleton.name = "GeneralSkeleton"
	rig.add_child(skeleton)
	skeleton.owner = rig
	skeleton.unique_name_in_owner = true
	var rests := [
		["Root", -1, Transform3D(Basis.IDENTITY, Vector3.ZERO)],
		["Hips", 0, Transform3D(Basis(Vector3.UP, 0.2), Vector3(0.0, 1.0, 0.0))],
		["LeftFoot", 1, Transform3D(Basis(Vector3.RIGHT, 0.4), Vector3(0.1, -0.9, 0.05))],
		["LeftToes", 2, Transform3D(Basis(Vector3.UP, 0.3).scaled_local(Vector3(1.1, 0.9, 1.2)), Vector3(0.0, -0.05, 0.12))],
	]
	for rest in rests:
		var bone := skeleton.add_bone(String(rest[0]))
		if int(rest[1]) >= 0: skeleton.set_bone_parent(bone, int(rest[1]))
		skeleton.set_bone_rest(bone, rest[2] as Transform3D)
	skeleton.reset_bone_poses()
	return skeleton


func _animation() -> Animation:
	var animation := Animation.new()
	animation.length = 1.0
	_add_position(animation, "Root", Vector3.ZERO, Vector3(0.0, 0.0, -0.8))
	_add_rotation(animation, "Root", Quaternion.IDENTITY, Quaternion(Vector3.UP, 0.5))
	_add_position(animation, "Hips", Vector3(0.0, 0.95, 0.0), Vector3(0.02, 1.02, -0.03))
	_add_rotation(animation, "Hips", Quaternion(Vector3.FORWARD, 0.1), Quaternion(Vector3.FORWARD, -0.1))
	_add_rotation(animation, "LeftFoot", Quaternion(Vector3.RIGHT, 0.2), Quaternion(Vector3.RIGHT, 0.9))
	return animation


func _add_position(animation: Animation, joint: String, start: Vector3, end: Vector3) -> void:
	var track := animation.add_track(Animation.TYPE_POSITION_3D)
	animation.track_set_path(track, NodePath("%%GeneralSkeleton:%s" % joint))
	animation.position_track_insert_key(track, 0.0, start)
	animation.position_track_insert_key(track, 1.0, end)


func _add_rotation(animation: Animation, joint: String, start: Quaternion, end: Quaternion) -> void:
	var track := animation.add_track(Animation.TYPE_ROTATION_3D)
	animation.track_set_path(track, NodePath("%%GeneralSkeleton:%s" % joint))
	animation.rotation_track_insert_key(track, 0.0, start)
	animation.rotation_track_insert_key(track, 1.0, end)


func _expect_rest_fallback(animation: Animation, skeleton: Skeleton3D, model: Dictionary) -> void:
	var toes := skeleton.find_bone("LeftToes")
	var toes_rest := skeleton.get_bone_rest(toes)
	_expect_vector(sampler.joint_position(animation, "LeftToes", TIME, model), toes_rest.origin, "untracked LeftToes position uses rest origin")
	_expect_quaternion(sampler.joint_rotation(animation, "LeftToes", TIME, model), toes_rest.basis.get_rotation_quaternion(), "untracked LeftToes rotation uses rest rotation")
	_expect_vector(sampler.joint_scale(animation, "LeftToes", TIME, model), toes_rest.basis.get_scale(), "untracked LeftToes scale uses rest scale")
	_expect(not (sampler.joint_position(animation, "LeftToes", TIME, model) as Vector3).is_zero_approx(), "untracked LeftToes position is not zeroed")
	var foot_rest := skeleton.get_bone_rest(skeleton.find_bone("LeftFoot"))
	_expect_vector(sampler.joint_position(animation, "LeftFoot", TIME, model), foot_rest.origin, "LeftFoot without a position track uses rest origin")


func _expect_tracked_channels_sample_tracks(animation: Animation, model: Dictionary) -> void:
	_expect_vector(sampler.joint_position(animation, "Hips", TIME, model), Vector3(0.0, 0.95, 0.0).lerp(Vector3(0.02, 1.02, -0.03), TIME), "tracked Hips position interpolates its track")
	_expect_quaternion(sampler.joint_rotation(animation, "LeftFoot", TIME, model), Quaternion(Vector3.RIGHT, 0.2).slerp(Quaternion(Vector3.RIGHT, 0.9), TIME), "tracked LeftFoot rotation interpolates its track")


func _expect_engine_global_poses(rig: Node3D, skeleton: Skeleton3D, animation: Animation, model: Dictionary) -> void:
	var library := AnimationLibrary.new()
	library.add_animation("sample", animation)
	var player := AnimationPlayer.new()
	rig.add_child(player)
	player.root_node = NodePath("..")
	player.callback_mode_process = AnimationMixer.ANIMATION_CALLBACK_MODE_PROCESS_MANUAL
	player.add_animation_library("fixture", library)
	player.play("fixture/sample")
	player.seek(TIME, true)
	player.advance(0.0)
	var cache := {}
	for joint in ["Root", "Hips", "LeftFoot", "LeftToes"]:
		var expected := skeleton.get_bone_global_pose(skeleton.find_bone(joint))
		var actual: Transform3D = sampler.joint_global(animation, joint, TIME, model, cache)
		_expect_vector(actual.origin, expected.origin, "%s global origin matches the engine skeleton" % joint)
		_expect(_basis_close(actual.basis, expected.basis), "%s global basis matches the engine skeleton: %s vs %s" % [joint, actual.basis, expected.basis])
	var uncached: Transform3D = sampler.joint_global(animation, "LeftToes", TIME, model)
	_expect_vector(uncached.origin, (cache["LeftToes"] as Transform3D).origin, "uncached global pose matches the cached pose")


func _basis_close(actual: Basis, expected: Basis) -> bool:
	return (actual.x - expected.x).length() <= TOLERANCE and (actual.y - expected.y).length() <= TOLERANCE and (actual.z - expected.z).length() <= TOLERANCE


func _expect_vector(actual: Vector3, expected: Vector3, message: String) -> void:
	_expect((actual - expected).length() <= TOLERANCE, "%s: %s vs %s" % [message, actual, expected])


func _expect_quaternion(actual: Quaternion, expected: Quaternion, message: String) -> void:
	_expect(absf(actual.dot(expected)) >= 1.0 - TOLERANCE, "%s: %s vs %s" % [message, actual, expected])


func _expect(condition: bool, message: String) -> void:
	if not condition:
		push_error(message)
		failed += 1
		return
	passed += 1


func _load_helper(filename: String) -> Script:
	var test_path: String = get_script().resource_path
	var helper_path: String = test_path.get_base_dir().get_base_dir().get_base_dir().path_join("godot/lib").path_join(filename).simplify_path()
	return load(helper_path) as Script
