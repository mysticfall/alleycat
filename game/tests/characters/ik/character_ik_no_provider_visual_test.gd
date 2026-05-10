extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/character_ik_no_provider_visual_test.tscn"
const OUTPUT_ROOT := "IK-CharacterIK/no_provider_female"
const PLAYBACK_PARAMETER := &"parameters/States/playback"

const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"TopCamera",
]

const HEAD_MODIFIER_PATHS := [
	^"Subject/Female/Female_export/GeneralSkeleton/NeckSpineIK",
	^"Subject/Female/Female_export/GeneralSkeleton/HeadCopyRotation",
	^"Subject/Female/Female_export/GeneralSkeleton/NeckTwistDisperser",
]
const RIGHT_HAND_MODIFIER_PATHS := [
	^"Subject/Female/Female_export/GeneralSkeleton/RightArmIKController",
	^"Subject/Female/Female_export/GeneralSkeleton/RightArmTwoBoneIKController",
	^"Subject/Female/Female_export/GeneralSkeleton/RightHandCopyRotation",
]
const LEFT_HAND_MODIFIER_PATHS := [
	^"Subject/Female/Female_export/GeneralSkeleton/LeftArmIKController",
	^"Subject/Female/Female_export/GeneralSkeleton/LeftArmTwoBoneIKController",
	^"Subject/Female/Female_export/GeneralSkeleton/LeftHandCopyRotation",
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("CharacterIK no-provider visual runner: failed to instantiate test scene")
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 3)

	_validate_cameras(photobooth)

	var character_ik: Node = SceneUtils.require_node(photobooth, ^"Subject/Female/CharacterIK")
	var animation_tree: AnimationTree = SceneUtils.require_node(photobooth, ^"Subject/Female/AnimationTree") as AnimationTree
	var skeleton: Skeleton3D = SceneUtils.require_node(photobooth, ^"Subject/Female/Female_export/GeneralSkeleton") as Skeleton3D
	var head_target: CollisionObject3D = SceneUtils.require_node(photobooth, ^"Subject/Female/IKTargets/Head") as CollisionObject3D
	var right_hand_target: CollisionObject3D = SceneUtils.require_node(photobooth, ^"Subject/Female/IKTargets/RightHand") as CollisionObject3D
	var left_hand_target: CollisionObject3D = SceneUtils.require_node(photobooth, ^"Subject/Female/IKTargets/LeftHand") as CollisionObject3D

	if character_ik == null or animation_tree == null or skeleton == null or head_target == null or right_hand_target == null or left_hand_target == null:
		SceneUtils.fatal_error_and_quit("CharacterIK no-provider visual runner: required nodes are missing")
		return

	_start_idle(animation_tree)

	await _capture_framing_pass(photobooth)
	await SceneUtils.wait_frames(self, 8)

	var initial_metrics: Dictionary = _collect_pose_metrics(skeleton, head_target, right_hand_target, left_hand_target)
	_print_modifier_summary(photobooth, "after_initial_settle")
	_print_target_summary("after_initial_settle", head_target, right_hand_target, left_hand_target)
	_print_pose_metrics("initial", initial_metrics)
	await photobooth.capture_screenshots("%s/poses/01_initial_idle.jpg" % OUTPUT_ROOT)

	await SceneUtils.wait_seconds(self, 1.25)
	await SceneUtils.wait_frames(self, 8)

	var advanced_metrics: Dictionary = _collect_pose_metrics(skeleton, head_target, right_hand_target, left_hand_target)
	_print_modifier_summary(photobooth, "after_idle_advance")
	_print_target_summary("after_idle_advance", head_target, right_hand_target, left_hand_target)
	_print_pose_metrics("advanced", advanced_metrics)
	_print_pose_delta(initial_metrics, advanced_metrics)
	await photobooth.capture_screenshots("%s/poses/02_advanced_idle.jpg" % OUTPUT_ROOT)

	quit(0)


func _validate_cameras(photobooth: Photobooth) -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		if camera_rig == null:
			SceneUtils.fatal_error_and_quit("CharacterIK no-provider visual runner: missing camera %s" % camera_name)
			return


func _capture_framing_pass(photobooth: Photobooth) -> void:
	await SceneUtils.wait_frames(self, 2)
	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_character.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])


func _start_idle(animation_tree: AnimationTree) -> void:
	animation_tree.active = true
	var playback: AnimationNodeStateMachinePlayback = animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	if playback != null:
		playback.travel(&"Idle")


func _collect_pose_metrics(
	skeleton: Skeleton3D,
	head_target: Node3D,
	right_hand_target: Node3D,
	left_hand_target: Node3D
) -> Dictionary:
	var head_index: int = skeleton.find_bone("Head")
	var right_hand_index: int = skeleton.find_bone("RightHand")
	var left_hand_index: int = skeleton.find_bone("LeftHand")
	if head_index < 0 or right_hand_index < 0 or left_hand_index < 0:
		SceneUtils.fatal_error_and_quit("CharacterIK no-provider visual runner: required bones not found")
		return {}

	var head_pose: Transform3D = skeleton.global_transform * skeleton.get_bone_global_pose(head_index)
	var right_hand_pose: Transform3D = skeleton.global_transform * skeleton.get_bone_global_pose(right_hand_index)
	var left_hand_pose: Transform3D = skeleton.global_transform * skeleton.get_bone_global_pose(left_hand_index)

	return {
		"head": head_pose,
		"right_hand": right_hand_pose,
		"left_hand": left_hand_pose,
		"head_target_distance": head_pose.origin.distance_to(head_target.global_position),
		"right_hand_target_distance": right_hand_pose.origin.distance_to(right_hand_target.global_position),
		"left_hand_target_distance": left_hand_pose.origin.distance_to(left_hand_target.global_position),
	}


func _print_modifier_summary(photobooth: Photobooth, label: String) -> void:
	_print_modifier_group(photobooth, label, "head", HEAD_MODIFIER_PATHS)
	_print_modifier_group(photobooth, label, "right_hand", RIGHT_HAND_MODIFIER_PATHS)
	_print_modifier_group(photobooth, label, "left_hand", LEFT_HAND_MODIFIER_PATHS)


func _print_modifier_group(photobooth: Photobooth, label: String, group_name: String, paths: Array) -> void:
	for path_value: NodePath in paths:
		var modifier: SkeletonModifier3D = SceneUtils.require_node(photobooth, path_value) as SkeletonModifier3D
		if modifier == null:
			SceneUtils.fatal_error_and_quit("CharacterIK no-provider visual runner: modifier not found: %s" % path_value)
			return

		print(
			"NO_PROVIDER_MODIFIER %s group=%s node=%s active=%s influence=%.3f" % [
				label,
				group_name,
				modifier.name,
				str(modifier.active),
				modifier.influence,
			]
		)


func _print_target_summary(
	label: String,
	head_target: CollisionObject3D,
	right_hand_target: CollisionObject3D,
	left_hand_target: CollisionObject3D
) -> void:
	_print_target_state(label, "head", head_target)
	_print_target_state(label, "right_hand", right_hand_target)
	_print_target_state(label, "left_hand", left_hand_target)


func _print_target_state(label: String, target_name: String, target: CollisionObject3D) -> void:
	var process_disabled := target.process_mode == Node.PROCESS_MODE_DISABLED
	var collision_disabled := target.collision_layer == 0 and target.collision_mask == 0
	var shapes_disabled := _are_collision_shapes_disabled(target)
	print(
		"NO_PROVIDER_TARGET %s target=%s process_disabled=%s collision_disabled=%s shapes_disabled=%s" % [
			label,
			target_name,
			str(process_disabled),
			str(collision_disabled),
			str(shapes_disabled),
		]
	)
	if not process_disabled or not collision_disabled or not shapes_disabled:
		SceneUtils.fatal_error_and_quit(
			"CharacterIK no-provider visual runner: %s target was not disabled after no-provider settle" % target_name
		)


func _are_collision_shapes_disabled(root_node: Node) -> bool:
	var shapes := root_node.find_children("*", "CollisionShape3D", true, false)
	for shape: CollisionShape3D in shapes:
		if not shape.disabled:
			return false
	return true


func _print_pose_metrics(label: String, metrics: Dictionary) -> void:
	print(
		"NO_PROVIDER_POSE %s head_target_distance=%.4f right_hand_target_distance=%.4f left_hand_target_distance=%.4f" % [
			label,
			metrics["head_target_distance"],
			metrics["right_hand_target_distance"],
			metrics["left_hand_target_distance"],
		]
	)


func _print_pose_delta(initial_metrics: Dictionary, advanced_metrics: Dictionary) -> void:
	for key: String in ["head", "right_hand", "left_hand"]:
		var initial_transform: Transform3D = initial_metrics[key]
		var advanced_transform: Transform3D = advanced_metrics[key]
		print(
			"NO_PROVIDER_POSE_DELTA bone=%s origin_delta=%.4f basis_delta=%.4f" % [
				key,
				initial_transform.origin.distance_to(advanced_transform.origin),
				_initial_basis_delta(initial_transform.basis, advanced_transform.basis),
			]
		)


func _initial_basis_delta(initial_basis: Basis, advanced_basis: Basis) -> float:
	return (
		initial_basis.x.distance_to(advanced_basis.x)
		+ initial_basis.y.distance_to(advanced_basis.y)
		+ initial_basis.z.distance_to(advanced_basis.z)
	)


func _to_slug(value: String) -> String:
	return SceneUtils.to_safe_file_component(value).to_snake_case()
