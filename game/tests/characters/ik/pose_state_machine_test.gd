extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/pose_state_machine_test.tscn"
const OUTPUT_ROOT := "IK-004/pose_state_machine"
const SCENARIOS_ROOT_PATH := ^"Markers/PoseStateMachine/Scenarios"
const HEAD_REST_MARKER_PATH := ^"Markers/PoseStateMachine/RestHeadTarget"
const LEFT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestLeft"
const RIGHT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestRight"
const ANIMATION_TREE_PATH := ^"Subject/Female/AnimationTree"
const SKELETON_PATH := ^"Subject/Female/Female_export/GeneralSkeleton"
const LEFT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/LeftFoot"
const RIGHT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/RightFoot"
const PLAYBACK_PARAMETER := &"parameters/playback"

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
const REQUIRED_SCENARIO_MARKERS := [
	"Standing",
	"CrouchMidway",
	"CrouchFull",
	"CrouchMidwayForward",
	"KneelForward",
]
const KNEEL_ARM_HEAD_Z := 0.32
const KNEEL_RETREAT_HEAD_Z := 0.26
const DEFAULT_STEP_WAIT_FRAMES := 6
const DEFAULT_STEP_WAIT_SECONDS := 0.05
const DEFAULT_CAPTURE_GATE_TIMEOUT_SECONDS := 2.0
const DEFAULT_CAPTURE_GATE_SETTLE_FRAMES := 1


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
	var animation_tree: AnimationTree = SceneUtils.require_node(photobooth, ANIMATION_TREE_PATH) as AnimationTree
	var skeleton: Skeleton3D = SceneUtils.require_node(photobooth, SKELETON_PATH) as Skeleton3D
	var left_foot_target: Node3D = SceneUtils.require_node(photobooth, LEFT_FOOT_TARGET_PATH) as Node3D
	var right_foot_target: Node3D = SceneUtils.require_node(photobooth, RIGHT_FOOT_TARGET_PATH) as Node3D

	if (
		driver == null
		or scenarios_root == null
		or rest_marker == null
		or left_hand_rest_marker == null
		or right_hand_rest_marker == null
		or animation_tree == null
		or skeleton == null
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
		animation_tree,
		skeleton,
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
	animation_tree: AnimationTree,
	skeleton: Skeleton3D,
	scenarios_root: Node3D,
	head_rest_marker: Node3D,
	left_hand_rest_marker: Node3D,
	right_hand_rest_marker: Node3D,
	left_foot_target: Node3D,
	right_foot_target: Node3D
) -> void:
	var visual_scenarios: Array[Dictionary] = _build_visual_scenarios()
	var selected_scenarios: Array[Dictionary] = _resolve_selected_scenarios(visual_scenarios)
	if selected_scenarios.is_empty():
		SceneUtils.fatal_error_and_quit("IK-004 runner: no scenarios selected")
		return

	for scenario_index: int in selected_scenarios.size():
		var visual_scenario: Dictionary = selected_scenarios[scenario_index]
		await _apply_visual_scenario(
			driver,
			animation_tree,
			skeleton,
			scenarios_root,
			visual_scenario,
			head_rest_marker,
			left_hand_rest_marker,
			right_hand_rest_marker,
			left_foot_target,
			right_foot_target)

		var state_id: StringName = StringName(driver.call("GetCurrentStateId"))
		var file_name: String = "%s/poses/%02d_%s__%s.jpg" % [
			OUTPUT_ROOT,
			scenario_index + 1,
			_to_slug(visual_scenario["name"]),
			_to_slug(state_id)
		]
		await photobooth.capture_screenshots(file_name)


func _build_visual_scenarios() -> Array[Dictionary]:
	return [
		{
			"name": "Standing_Reference",
			"steps": [
				{"marker": "Standing"},
			],
		},
		{
			"name": "Crouch_Midway_Reference",
			"steps": [
				{"marker": "CrouchMidway"},
			],
		},
		{
			"name": "Crouch_Full_Reference",
			"steps": [
				{"marker": "CrouchFull"},
			],
		},
		{
			"name": "AllFours_HipEntry_PreEntry_Standing",
			"steps": [
				{"marker": "CrouchFull", "head_normalized_local_y": 0.20, "head_normalized_forward_offset": 0.65},
			],
			"capture_gate": {
				"state_id": "Standing",
				"playback_node": "StandingCrouching",
				"settle_frames": 3,
				"extra_frames": 2,
				"extra_seconds": 0.05,
			},
		},
		{
			"name": "AllFours_HipEntry_Transitioning_OnEntry",
			"steps": [
				{"marker": "CrouchFull", "head_normalized_local_y": 0.20, "head_normalized_forward_offset": 0.76},
			],
			"capture_gate": {
				"state_id": "AllFours",
				"playback_node": "AllFoursTransitioning",
				"settle_frames": 3,
				"extra_frames": 2,
				"extra_seconds": 0.05,
			},
		},
		{
			"name": "Standing_MidCrouch_Forward_Clamp",
			"steps": [
				{"marker": "CrouchMidwayForward"},
			],
			"capture_gate": {
				"state_id": "Standing",
				"playback_node": "StandingCrouching",
				"settle_frames": 3,
				"extra_frames": 2,
				"extra_seconds": 0.05,
			},
		},
		{
			"name": "Kneel_Settled_After_Armed_Retreat",
			"steps": [
				{"marker": "CrouchMidwayForward"},
				{"marker": "KneelForward", "head_z": KNEEL_ARM_HEAD_Z},
				{"marker": "KneelForward", "head_z": KNEEL_RETREAT_HEAD_Z},
			],
			"capture_gate": {
				"state_id": "Kneeling",
				"playback_node": "Kneeling",
				"timeout_seconds": 6.5,
				"extra_frames": 8,
				"extra_seconds": 0.12,
			},
		},
		{
			"name": "Kneel_Exit_Settled_On_Standing_Crouch_Continuum",
			"steps": [
				{"marker": "KneelForward", "head_z": KNEEL_ARM_HEAD_Z},
				{"marker": "KneelForward", "head_z": KNEEL_RETREAT_HEAD_Z},
				{"marker": "Standing"},
				{"marker": "KneelForward", "head_z": KNEEL_ARM_HEAD_Z},
				{"marker": "KneelForward", "head_z": KNEEL_RETREAT_HEAD_Z},
			],
			"capture_gate": {
				"state_id": "Standing",
				"playback_node": "StandingCrouching",
				"timeout_seconds": 6.5,
				"settle_frames": 6,
				"extra_frames": 2,
				"extra_seconds": 0.05,
			},
		},
	]


func _resolve_selected_scenarios(visual_scenarios: Array[Dictionary]) -> Array[Dictionary]:
	var all_scenarios: PackedStringArray = _extract_visual_scenario_names(visual_scenarios)

	if all_scenarios.is_empty():
		return []

	var requested: String = _get_user_arg_value("--scenarios")
	if requested.is_empty():
		return visual_scenarios

	var requested_scenarios: PackedStringArray = requested.split(",", false)
	var selected: Array[Dictionary] = []
	for scenario_name_raw: String in requested_scenarios:
		var scenario_name: String = scenario_name_raw.strip_edges()
		if scenario_name.is_empty():
			continue

		if not all_scenarios.has(scenario_name):
			SceneUtils.fatal_error_and_quit("IK-004 runner: requested scenario '%s' is not defined" % scenario_name)
			return []

		selected.append(_find_visual_scenario(visual_scenarios, scenario_name))

	return selected


func _validate_required_scenarios(scenarios_root: Node3D) -> bool:
	var scenario_names: PackedStringArray = _extract_scenario_names(scenarios_root)
	for required_scenario_marker: String in REQUIRED_SCENARIO_MARKERS:
		if not scenario_names.has(required_scenario_marker):
			return false

	return true


func _extract_scenario_names(scenarios_root: Node3D) -> PackedStringArray:
	var names := PackedStringArray()
	for child: Node in scenarios_root.get_children():
		if child is Node3D:
			names.append(str(child.name))

	return names


func _extract_visual_scenario_names(visual_scenarios: Array[Dictionary]) -> PackedStringArray:
	var names := PackedStringArray()
	for visual_scenario: Dictionary in visual_scenarios:
		names.append(str(visual_scenario["name"]))

	return names


func _find_visual_scenario(visual_scenarios: Array[Dictionary], scenario_name: String) -> Dictionary:
	for visual_scenario: Dictionary in visual_scenarios:
		if str(visual_scenario["name"]) == scenario_name:
			return visual_scenario

	return {}


func _apply_visual_scenario(
	driver: Node,
	animation_tree: AnimationTree,
	skeleton: Skeleton3D,
	scenarios_root: Node3D,
	visual_scenario: Dictionary,
	head_rest_marker: Node3D,
	left_hand_rest_marker: Node3D,
	right_hand_rest_marker: Node3D,
	left_foot_target: Node3D,
	right_foot_target: Node3D
) -> void:
	var steps: Array = visual_scenario.get("steps", [])
	for step_index: int in steps.size():
		var step: Dictionary = steps[step_index]
		var show_marker: bool = step_index == steps.size() - 1
		_apply_pose_step(
			driver,
			skeleton,
			scenarios_root,
			step,
			show_marker,
			head_rest_marker,
			left_hand_rest_marker,
			right_hand_rest_marker,
			left_foot_target,
			right_foot_target)

		await SceneUtils.wait_frames(self, DEFAULT_STEP_WAIT_FRAMES)
		await SceneUtils.wait_seconds(self, DEFAULT_STEP_WAIT_SECONDS)

	await _await_capture_gate(driver, animation_tree, visual_scenario.get("capture_gate", {}))

	_hide_all_scenario_markers(scenarios_root)


func _await_capture_gate(driver: Node, animation_tree: AnimationTree, capture_gate: Dictionary) -> void:
	if capture_gate.is_empty():
		return

	var expected_state_id: StringName = StringName(capture_gate.get("state_id", ""))
	var expected_playback_node: StringName = StringName(capture_gate.get("playback_node", ""))
	var timeout_seconds: float = float(capture_gate.get("timeout_seconds", DEFAULT_CAPTURE_GATE_TIMEOUT_SECONDS))
	var settle_frames: int = max(int(capture_gate.get("settle_frames", DEFAULT_CAPTURE_GATE_SETTLE_FRAMES)), 1)
	var deadline_msec: int = Time.get_ticks_msec() + int(ceil(timeout_seconds * 1000.0))
	var consecutive_matches: int = 0

	while true:
		var current_state_id: StringName = StringName(driver.call("GetCurrentStateId"))
		var current_playback_node: StringName = _get_current_playback_node(animation_tree)
		var state_matches: bool = expected_state_id.is_empty() or current_state_id == expected_state_id
		var playback_matches: bool = expected_playback_node.is_empty() or current_playback_node == expected_playback_node
		if state_matches and playback_matches:
			consecutive_matches += 1
			if consecutive_matches >= settle_frames:
				break
		else:
			consecutive_matches = 0

		if Time.get_ticks_msec() >= deadline_msec:
			SceneUtils.fatal_error_and_quit(
				"IK-004 runner: capture gate timed out waiting for state '%s' and playback '%s' (last observed state '%s', playback '%s')"
				% [expected_state_id, expected_playback_node, current_state_id, current_playback_node])
			return

		await SceneUtils.wait_frames(self, 1)

	var extra_frames: int = int(capture_gate.get("extra_frames", 0))
	if extra_frames > 0:
		await SceneUtils.wait_frames(self, extra_frames)

	var extra_seconds: float = float(capture_gate.get("extra_seconds", 0.0))
	if extra_seconds > 0.0:
		await SceneUtils.wait_seconds(self, extra_seconds)


func _get_current_playback_node(animation_tree: AnimationTree) -> StringName:
	var playback: AnimationNodeStateMachinePlayback = animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	if playback == null:
		SceneUtils.fatal_error_and_quit(
			"IK-004 runner: AnimationTree playback object is missing at '%s'" % PLAYBACK_PARAMETER)
		return StringName()

	return playback.get_current_node()


func _apply_pose_step(
	driver: Node,
	skeleton: Skeleton3D,
	scenarios_root: Node3D,
	step: Dictionary,
	show_marker: bool,
	head_rest_marker: Node3D,
	left_hand_rest_marker: Node3D,
	right_hand_rest_marker: Node3D,
	left_foot_target: Node3D,
	right_foot_target: Node3D
) -> void:
	_hide_all_scenario_markers(scenarios_root)

	var scenario_name: String = str(step.get("marker", ""))
	var scenario_node: Node3D = scenarios_root.get_node_or_null(NodePath(scenario_name)) as Node3D
	if scenario_node == null:
		SceneUtils.fatal_error_and_quit("IK-004 runner: scenario marker '%s' is missing" % scenario_name)
		return

	scenario_node.visible = show_marker

	var pose_targets: Dictionary = _resolve_scenario_pose_targets(
		scenario_node,
		head_rest_marker,
		left_hand_rest_marker,
		right_hand_rest_marker,
		left_foot_target,
		right_foot_target)
	if step.has("head_z"):
		pose_targets["head"] = _override_transform_z(pose_targets["head"], float(step["head_z"]))
	if step.has("head_normalized_local_y") and step.has("head_normalized_forward_offset"):
		pose_targets["head"] = _create_skeleton_local_head_transform(
			skeleton,
			head_rest_marker,
			float(step["head_normalized_local_y"]),
			float(step["head_normalized_forward_offset"]))

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


func _hide_all_scenario_markers(scenarios_root: Node3D) -> void:
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = false


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


func _override_transform_z(transform: Transform3D, z: float) -> Transform3D:
	var origin: Vector3 = transform.origin
	origin.z = z
	return Transform3D(transform.basis, origin)


func _create_skeleton_local_head_transform(
	skeleton: Skeleton3D,
	head_rest_marker: Node3D,
	normalized_local_y: float,
	normalized_forward_offset: float
) -> Transform3D:
	var rest_head_local: Vector3 = (skeleton.global_transform.affine_inverse() * head_rest_marker.global_transform).origin
	var rest_head_height: float = absf(rest_head_local.y)
	if rest_head_height <= 0.0001:
		rest_head_height = 1.0
	var skeleton_local_head_position := Vector3(
		0.0,
		normalized_local_y * rest_head_height,
		normalized_forward_offset * rest_head_height)
	return Transform3D(
		head_rest_marker.global_transform.basis,
		skeleton.global_transform * skeleton_local_head_position)


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
