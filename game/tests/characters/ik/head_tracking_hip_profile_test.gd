extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/head_tracking_hip_profile_test.tscn"
const OUTPUT_ROOT := "ik004/head_tracking_hip_profile"
const SCENARIOS_ROOT_PATH := ^"Markers/PoseStateMachine/Scenarios"
const HEAD_REST_MARKER_PATH := ^"Markers/PoseStateMachine/RestHeadTarget"
const LEFT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestLeft"
const RIGHT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestRight"
const SUBJECT_PATH := ^"Subject/Female"
const DRIVER_PATH := ^"PoseStateMachineDriver"
const HIP_MODIFIER_PATH := ^"Subject/Female/Female_export/GeneralSkeleton/HipReconciliationModifier"
const LEFT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/LeftFoot"
const RIGHT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/RightFoot"
const REQUIRED_SCENARIOS := [
	"Standing",
	"VerticalCrouchStrong",
	"StoopForward",
	"LeanBack",
]
const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"TopCamera",
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("IK-004 hip runner: failed to instantiate test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var subject: Node3D = SceneUtils.require_node(photobooth, SUBJECT_PATH) as Node3D
	var driver: Node = SceneUtils.require_node(photobooth, DRIVER_PATH)
	var hip_modifier: SkeletonModifier3D = SceneUtils.require_node(photobooth, HIP_MODIFIER_PATH) as SkeletonModifier3D
	var scenarios_root: Node3D = SceneUtils.require_node(photobooth, SCENARIOS_ROOT_PATH) as Node3D
	var rest_marker: Node3D = SceneUtils.require_node(photobooth, HEAD_REST_MARKER_PATH) as Node3D
	var left_hand_rest_marker: Node3D = SceneUtils.require_node(photobooth, LEFT_HAND_REST_MARKER_PATH) as Node3D
	var right_hand_rest_marker: Node3D = SceneUtils.require_node(photobooth, RIGHT_HAND_REST_MARKER_PATH) as Node3D
	var left_foot_target: Node3D = SceneUtils.require_node(photobooth, LEFT_FOOT_TARGET_PATH) as Node3D
	var right_foot_target: Node3D = SceneUtils.require_node(photobooth, RIGHT_FOOT_TARGET_PATH) as Node3D

	if (
		subject == null
		or driver == null
		or hip_modifier == null
		or scenarios_root == null
		or rest_marker == null
		or left_hand_rest_marker == null
		or right_hand_rest_marker == null
		or left_foot_target == null
		or right_foot_target == null
	):
		SceneUtils.fatal_error_and_quit("IK-004 hip runner: required scene nodes are missing")
		return

	hip_modifier.set("StateMachine", driver.call("GetDrivenStateMachine"))

	if _has_user_arg("--validate-only"):
		if not _validate_scene(subject, scenarios_root):
			SceneUtils.fatal_error_and_quit("IK-004 hip runner: validation failed for required scenarios")
			return

		print("IK-004 hip runner: validate-only mode completed successfully")
		quit(0)
		return

	await _capture_framing_pass(photobooth, scenarios_root, rest_marker)
	await _capture_scenarios(
		photobooth,
		driver,
		scenarios_root,
		rest_marker,
		left_hand_rest_marker,
		right_hand_rest_marker,
		left_foot_target,
		right_foot_target)

	quit(0)


func _capture_framing_pass(photobooth: Photobooth, scenarios_root: Node3D, rest_marker: Node3D) -> void:
	rest_marker.visible = true
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = true

	await SceneUtils.wait_frames(self, 2)

	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	rest_marker.visible = false
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = false


func _capture_scenarios(
	photobooth: Photobooth,
	driver: Node,
	scenarios_root: Node3D,
	head_rest_marker: Node3D,
	left_hand_rest_marker: Node3D,
	right_hand_rest_marker: Node3D,
	left_foot_target: Node3D,
	right_foot_target: Node3D
) -> void:
	for scenario_index: int in REQUIRED_SCENARIOS.size():
		var scenario_name: String = REQUIRED_SCENARIOS[scenario_index]
		var scenario_node: Node3D = scenarios_root.get_node_or_null(NodePath(scenario_name)) as Node3D
		if scenario_node == null:
			SceneUtils.fatal_error_and_quit("IK-004 hip runner: scenario marker '%s' is missing" % scenario_name)
			return

		scenario_node.visible = true
		driver.call(
			"TickPoseTargets",
			scenario_node.global_transform,
			left_hand_rest_marker.global_transform,
			right_hand_rest_marker.global_transform,
			left_foot_target.global_transform,
			right_foot_target.global_transform,
			head_rest_marker.global_transform,
			-1,
			-1.0)

		await SceneUtils.wait_frames(self, 6)
		await SceneUtils.wait_seconds(self, 0.05)

		var state_id: StringName = StringName(driver.call("GetCurrentStateId"))
		var file_name: String = "%s/poses/%02d_%s__%s.jpg" % [
			OUTPUT_ROOT,
			scenario_index + 1,
			_to_slug(scenario_name),
			_to_slug(state_id),
		]
		await photobooth.capture_screenshots(file_name)

		scenario_node.visible = false


func _validate_required_scenarios(scenarios_root: Node3D) -> bool:
	for scenario_name: String in REQUIRED_SCENARIOS:
		if scenarios_root.get_node_or_null(NodePath(scenario_name)) == null:
			return false

	var standing: Node3D = scenarios_root.get_node_or_null(^"Standing") as Node3D
	var stoop_forward: Node3D = scenarios_root.get_node_or_null(^"StoopForward") as Node3D
	var lean_back: Node3D = scenarios_root.get_node_or_null(^"LeanBack") as Node3D
	if standing == null or stoop_forward == null or lean_back == null:
		return false

	if stoop_forward.global_position.z >= standing.global_position.z:
		return false

	if lean_back.global_position.z <= standing.global_position.z:
		return false

	if stoop_forward.global_position.y >= standing.global_position.y:
		return false

	if lean_back.global_position.y <= stoop_forward.global_position.y:
		return false

	return true


func _validate_scene(subject: Node3D, scenarios_root: Node3D) -> bool:
	if subject.name != "Female":
		return false

	return _validate_required_scenarios(scenarios_root)


func _to_slug(value: Variant) -> String:
	return SceneUtils.to_safe_file_component(str(value).to_lower())


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	for arg: String in OS.get_cmdline_user_args():
		if arg == expected_arg:
			return true

	return false
