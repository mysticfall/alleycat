extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/pose_state_machine_test.tscn"
const OUTPUT_ROOT := "IK-004/pose_state_machine"
const SCENARIOS_ROOT_PATH := ^"Markers/PoseStateMachine/Scenarios"
const HEAD_REST_MARKER_PATH := ^"Markers/PoseStateMachine/RestHeadTarget"
const LEFT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestLeft"
const RIGHT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestRight"
const LEFT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/LeftFoot"
const RIGHT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/RightFoot"

const HEAD_MARKER_NAME := "Head"
const LEFT_HAND_MARKER_NAME := "LeftHand"
const RIGHT_HAND_MARKER_NAME := "RightHand"
const LEFT_FOOT_MARKER_NAME := "LeftFoot"
const RIGHT_FOOT_MARKER_NAME := "RightFoot"

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
		SceneUtils.fatal_error_and_quit("IK-004 runner: failed to instantiate test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var driver: Node = SceneUtils.require_node(photobooth, ^"PoseStateMachineDriver")
	var scenarios_root: Node3D = SceneUtils.require_node(photobooth, SCENARIOS_ROOT_PATH) as Node3D
	var rest_marker: Node3D = SceneUtils.require_node(photobooth, HEAD_REST_MARKER_PATH) as Node3D
	var left_hand_rest_marker: Node3D = SceneUtils.require_node(photobooth, LEFT_HAND_REST_MARKER_PATH) as Node3D
	var right_hand_rest_marker: Node3D = SceneUtils.require_node(photobooth, RIGHT_HAND_REST_MARKER_PATH) as Node3D
	var left_foot_target: Node3D = SceneUtils.require_node(photobooth, LEFT_FOOT_TARGET_PATH) as Node3D
	var right_foot_target: Node3D = SceneUtils.require_node(photobooth, RIGHT_FOOT_TARGET_PATH) as Node3D

	if (
		driver == null
		or scenarios_root == null
		or rest_marker == null
		or left_hand_rest_marker == null
		or right_hand_rest_marker == null
		or left_foot_target == null
		or right_foot_target == null
	):
		SceneUtils.fatal_error_and_quit("IK-004 runner: required driver/marker nodes are missing")
		return

	if _has_user_arg("--validate-only"):
		if not _validate_required_scenarios(scenarios_root):
			SceneUtils.fatal_error_and_quit("IK-004 runner: validation failed for required scenarios")
			return

		print("IK-004 runner: validate-only mode completed successfully")
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
	var selected_scenarios: PackedStringArray = _resolve_selected_scenarios(scenarios_root)
	if selected_scenarios.is_empty():
		SceneUtils.fatal_error_and_quit("IK-004 runner: no scenarios selected")
		return

	for scenario_index: int in selected_scenarios.size():
		var scenario_name: String = selected_scenarios[scenario_index]
		var scenario_node: Node3D = scenarios_root.get_node_or_null(NodePath(scenario_name)) as Node3D
		if scenario_node == null:
			SceneUtils.fatal_error_and_quit("IK-004 runner: scenario marker '%s' is missing" % scenario_name)
			return

		scenario_node.visible = true

		var pose_targets: Dictionary = _resolve_scenario_pose_targets(
			scenario_node,
			head_rest_marker,
			left_hand_rest_marker,
			right_hand_rest_marker,
			left_foot_target,
			right_foot_target)
		driver.call(
			"TickPoseTargets",
			pose_targets["head"],
			pose_targets["left_hand"],
			pose_targets["right_hand"],
			pose_targets["left_foot"],
			pose_targets["right_foot"],
			pose_targets["head_rest"],
			-1,
			-1.0)

		await SceneUtils.wait_frames(self, 6)
		await SceneUtils.wait_seconds(self, 0.05)

		var state_id: StringName = StringName(driver.call("GetCurrentStateId"))
		var file_name: String = "%s/poses/%02d_%s__%s.jpg" % [
			OUTPUT_ROOT,
			scenario_index + 1,
			_to_slug(scenario_name),
			_to_slug(state_id)
		]
		await photobooth.capture_screenshots(file_name)

		scenario_node.visible = false


func _resolve_selected_scenarios(scenarios_root: Node3D) -> PackedStringArray:
	var all_scenarios: PackedStringArray = _extract_scenario_names(scenarios_root)

	if all_scenarios.is_empty():
		return PackedStringArray()

	var requested: String = _get_user_arg_value("--scenarios")
	if requested.is_empty():
		return all_scenarios

	var requested_scenarios: PackedStringArray = requested.split(",", false)
	var selected := PackedStringArray()
	for scenario_name_raw: String in requested_scenarios:
		var scenario_name: String = scenario_name_raw.strip_edges()
		if scenario_name.is_empty():
			continue

		if not all_scenarios.has(scenario_name):
			SceneUtils.fatal_error_and_quit("IK-004 runner: requested scenario '%s' is not defined" % scenario_name)
			return PackedStringArray()

		selected.append(scenario_name)

	return selected


func _validate_required_scenarios(scenarios_root: Node3D) -> bool:
	var scenario_names: PackedStringArray = _extract_scenario_names(scenarios_root)
	return scenario_names.has("Standing") and scenario_names.has("CrouchMidway") and scenario_names.has("CrouchFull")


func _extract_scenario_names(scenarios_root: Node3D) -> PackedStringArray:
	var names := PackedStringArray()
	for child: Node in scenarios_root.get_children():
		if child is Node3D:
			names.append(str(child.name))

	return names


func _resolve_scenario_pose_targets(
	scenario_node: Node3D,
	head_rest_marker: Node3D,
	left_hand_rest_marker: Node3D,
	right_hand_rest_marker: Node3D,
	left_foot_target: Node3D,
	right_foot_target: Node3D
) -> Dictionary:
	var head_marker: Node3D = scenario_node.get_node_or_null(NodePath(HEAD_MARKER_NAME)) as Node3D
	var left_hand_marker: Node3D = scenario_node.get_node_or_null(NodePath(LEFT_HAND_MARKER_NAME)) as Node3D
	var right_hand_marker: Node3D = scenario_node.get_node_or_null(NodePath(RIGHT_HAND_MARKER_NAME)) as Node3D
	var left_foot_marker: Node3D = scenario_node.get_node_or_null(NodePath(LEFT_FOOT_MARKER_NAME)) as Node3D
	var right_foot_marker: Node3D = scenario_node.get_node_or_null(NodePath(RIGHT_FOOT_MARKER_NAME)) as Node3D

	return {
		"head": head_marker.global_transform if head_marker != null else scenario_node.global_transform,
		"left_hand": left_hand_marker.global_transform if left_hand_marker != null else left_hand_rest_marker.global_transform,
		"right_hand": right_hand_marker.global_transform if right_hand_marker != null else right_hand_rest_marker.global_transform,
		"left_foot": left_foot_marker.global_transform if left_foot_marker != null else left_foot_target.global_transform,
		"right_foot": right_foot_marker.global_transform if right_foot_marker != null else right_foot_target.global_transform,
		"head_rest": head_rest_marker.global_transform,
	}


func _to_slug(value: Variant) -> String:
	return SceneUtils.to_safe_file_component(str(value).to_lower())


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	for arg: String in OS.get_cmdline_user_args():
		if arg == expected_arg:
			return true

	return false


func _get_user_arg_value(prefix: String) -> String:
	if prefix.is_empty():
		return ""

	for arg: String in OS.get_cmdline_user_args():
		if arg.begins_with("%s=" % prefix):
			return arg.trim_prefix("%s=" % prefix)

	return ""
