extends SceneTree

const TEST_SCENE_PATH := "res://tests/ik/vrik_lateral_crouch_regression_test.tscn"
const OUTPUT_ROOT := "IK-004/vrik_lateral_crouch_regression"
const DRIVER_PATH := ^"PoseStateMachineDriver"
const HIP_MODIFIER_PATH := ^"Subject/Female/Female_export/GeneralSkeleton/HipReconciliationModifier"
const SKELETON_PATH := ^"Subject/Female/Female_export/GeneralSkeleton"
const SCENARIOS_ROOT_PATH := ^"Markers/PoseStateMachine/Scenarios"
const HEAD_REST_MARKER_PATH := ^"Markers/PoseStateMachine/RestHeadTarget"
const LEFT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestLeft"
const RIGHT_HAND_REST_MARKER_PATH := ^"Markers/PoseStateMachine/HandTargetRestRight"
const LEFT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/LeftFoot"
const RIGHT_FOOT_TARGET_PATH := ^"Subject/Female/IKTargets/RightFoot"
const HEAD_IK_TARGET_PATH := ^"Subject/Female/IKTargets/Head"

const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"TopCamera",
]

const LATERAL_HEAD_OFFSET_METRES := 0.35
const CROUCH_HEAD_DROP_METRES := 0.48
const MINIMUM_LATERAL_HIP_RESPONSE_METRES := 0.025
const MAXIMUM_FOOT_LIFT_METRES := 0.005
const MINIMUM_CROUCH_HIP_DROP_METRES := 0.10
const MINIMUM_HIP_ABOVE_FEET_METRES := 0.25


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner must not run headless because screenshots would be invalid")
		return

	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: failed to instantiate test scene")
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var driver: Node = SceneUtils.require_node(photobooth, DRIVER_PATH)
	var hip_modifier: SkeletonModifier3D = SceneUtils.require_node(photobooth, HIP_MODIFIER_PATH) as SkeletonModifier3D
	var skeleton: Skeleton3D = SceneUtils.require_node(photobooth, SKELETON_PATH) as Skeleton3D
	var scenarios_root: Node3D = SceneUtils.require_node(photobooth, SCENARIOS_ROOT_PATH) as Node3D
	var head_rest_marker: Node3D = SceneUtils.require_node(photobooth, HEAD_REST_MARKER_PATH) as Node3D
	var left_hand_rest_marker: Node3D = SceneUtils.require_node(photobooth, LEFT_HAND_REST_MARKER_PATH) as Node3D
	var right_hand_rest_marker: Node3D = SceneUtils.require_node(photobooth, RIGHT_HAND_REST_MARKER_PATH) as Node3D
	var left_foot_target: Node3D = SceneUtils.require_node(photobooth, LEFT_FOOT_TARGET_PATH) as Node3D
	var right_foot_target: Node3D = SceneUtils.require_node(photobooth, RIGHT_FOOT_TARGET_PATH) as Node3D
	var head_ik_target: Node3D = SceneUtils.require_node(photobooth, HEAD_IK_TARGET_PATH) as Node3D

	if (
		driver == null
		or hip_modifier == null
		or skeleton == null
		or scenarios_root == null
		or head_rest_marker == null
		or left_hand_rest_marker == null
		or right_hand_rest_marker == null
		or left_foot_target == null
		or right_foot_target == null
		or head_ik_target == null
	):
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: required nodes are missing")
		return

	hip_modifier.set("StateMachine", driver.call("GetDrivenStateMachine"))
	_validate_camera_rigs(photobooth)
	_validate_marker_orientation(scenarios_root)

	await _capture_framing_pass(photobooth, scenarios_root, head_rest_marker)

	var standing_marker: Node3D = _require_scenario_marker(scenarios_root, "Standing")
	var standing := await _apply_and_measure(
		"standing_baseline",
		driver,
		skeleton,
		standing_marker.global_transform,
		head_rest_marker,
		left_hand_rest_marker,
		right_hand_rest_marker,
		left_foot_target,
		right_foot_target,
		head_ik_target)
	await photobooth.capture_screenshots("%s/poses/01_standing_baseline.jpg" % OUTPUT_ROOT)

	var right_head := standing_marker.global_transform
	right_head.origin.x += LATERAL_HEAD_OFFSET_METRES
	var lateral_right := await _apply_and_measure(
		"head_lateral_right",
		driver,
		skeleton,
		right_head,
		head_rest_marker,
		left_hand_rest_marker,
		right_hand_rest_marker,
		left_foot_target,
		right_foot_target,
		head_ik_target)
	await photobooth.capture_screenshots("%s/poses/02_head_lateral_right.jpg" % OUTPUT_ROOT)

	var left_head := standing_marker.global_transform
	left_head.origin.x -= LATERAL_HEAD_OFFSET_METRES
	var lateral_left := await _apply_and_measure(
		"head_lateral_left",
		driver,
		skeleton,
		left_head,
		head_rest_marker,
		left_hand_rest_marker,
		right_hand_rest_marker,
		left_foot_target,
		right_foot_target,
		head_ik_target)
	await photobooth.capture_screenshots("%s/poses/03_head_lateral_left.jpg" % OUTPUT_ROOT)

	var crouch_head := standing_marker.global_transform
	crouch_head.origin.y -= CROUCH_HEAD_DROP_METRES
	var crouch := await _apply_and_measure(
		"vertical_crouch",
		driver,
		skeleton,
		crouch_head,
		head_rest_marker,
		left_hand_rest_marker,
		right_hand_rest_marker,
		left_foot_target,
		right_foot_target,
		head_ik_target)
	await photobooth.capture_screenshots("%s/poses/04_vertical_crouch.jpg" % OUTPUT_ROOT)

	if _assert_gate_metrics(standing, lateral_right, lateral_left, crouch):
		print("IK004_VRIK_GATE_PASS artefact_dir=res://temp/%s" % OUTPUT_ROOT)
	quit(0)


func _validate_camera_rigs(photobooth: Photobooth) -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		if camera_rig == null:
			SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: missing camera %s" % camera_name)
			return


func _validate_marker_orientation(scenarios_root: Node3D) -> void:
	var standing_marker := _require_scenario_marker(scenarios_root, "Standing")
	var vertical_crouch := _require_scenario_marker(scenarios_root, "VerticalCrouchStrong")
	if vertical_crouch.global_position.y >= standing_marker.global_position.y:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: crouch marker is not below standing marker")
		return
	print(
		"IK004_VRIK_MARKERS standing_y=%.4f crouch_y=%.4f lateral_right_x=%.4f lateral_left_x=%.4f" % [
			standing_marker.global_position.y,
			vertical_crouch.global_position.y,
			standing_marker.global_position.x + LATERAL_HEAD_OFFSET_METRES,
			standing_marker.global_position.x - LATERAL_HEAD_OFFSET_METRES,
		]
	)


func _capture_framing_pass(photobooth: Photobooth, scenarios_root: Node3D, head_rest_marker: Node3D) -> void:
	head_rest_marker.visible = true
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = true

	await SceneUtils.wait_frames(self, 2)
	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	head_rest_marker.visible = false
	for child: Node in scenarios_root.get_children():
		var marker: Node3D = child as Node3D
		if marker != null:
			marker.visible = false


func _apply_and_measure(
	label: String,
	driver: Node,
	skeleton: Skeleton3D,
	head_target_transform: Transform3D,
	head_rest_marker: Node3D,
	left_hand_rest_marker: Node3D,
	right_hand_rest_marker: Node3D,
	left_foot_target: Node3D,
	right_foot_target: Node3D,
	head_ik_target: Node3D
) -> Dictionary:
	var left_foot_input := left_foot_target.global_transform
	var right_foot_input := right_foot_target.global_transform
	head_ik_target.global_transform = head_target_transform
	driver.call(
		"TickPoseTargets",
		head_target_transform,
		left_hand_rest_marker.global_transform,
		right_hand_rest_marker.global_transform,
		left_foot_target.global_transform,
		right_foot_target.global_transform,
		head_rest_marker.global_transform,
		-1,
		-1.0)
	left_foot_target.global_transform = left_foot_input
	right_foot_target.global_transform = right_foot_input

	await SceneUtils.wait_frames(self, 10)
	await SceneUtils.wait_seconds(self, 0.05)
	left_foot_target.global_transform = left_foot_input
	right_foot_target.global_transform = right_foot_input

	var hips_index := skeleton.find_bone("Hips")
	if hips_index < 0:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: Hips bone not found")
		return {}

	if not bool(driver.call("HasLatestHipLocalPosition")):
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: pose state machine did not emit a hip target")
		return {}

	var hip_local_position: Vector3 = driver.call("GetLatestHipLocalPosition")
	var hips_world := skeleton.global_transform * Transform3D(Basis.IDENTITY, hip_local_position)
	var left_foot_y := left_foot_target.global_position.y
	var right_foot_y := right_foot_target.global_position.y
	var state_id := StringName(driver.call("GetCurrentStateId"))
	var metrics := {
		"label": label,
		"state_id": state_id,
		"head_x": head_target_transform.origin.x,
		"head_y": head_target_transform.origin.y,
		"hip_local_x": hip_local_position.x,
		"hip_local_y": hip_local_position.y,
		"hip_x": hips_world.origin.x,
		"hip_y": hips_world.origin.y,
		"left_foot_y": left_foot_y,
		"right_foot_y": right_foot_y,
	}
	print(
		"IK004_VRIK_METRIC label=%s state=%s head_x=%.4f head_y=%.4f hip_local_x=%.4f hip_local_y=%.4f hip_x=%.4f hip_y=%.4f left_foot_y=%.4f right_foot_y=%.4f" % [
			label,
			str(state_id),
			metrics["head_x"],
			metrics["head_y"],
			metrics["hip_local_x"],
			metrics["hip_local_y"],
			metrics["hip_x"],
			metrics["hip_y"],
			metrics["left_foot_y"],
			metrics["right_foot_y"],
		]
	)
	return metrics


func _assert_gate_metrics(standing: Dictionary, lateral_right: Dictionary, lateral_left: Dictionary, crouch: Dictionary) -> bool:
	var right_delta: float = lateral_right["hip_x"] - standing["hip_x"]
	var left_delta: float = lateral_left["hip_x"] - standing["hip_x"]
	var crouch_hip_drop: float = standing["hip_local_y"] - crouch["hip_local_y"]
	var left_foot_lift: float = crouch["left_foot_y"] - standing["left_foot_y"]
	var right_foot_lift: float = crouch["right_foot_y"] - standing["right_foot_y"]
	var lowest_foot_y: float = min(crouch["left_foot_y"], crouch["right_foot_y"])

	print(
		"IK004_VRIK_ASSERT right_hip_delta=%.4f left_hip_delta=%.4f crouch_hip_drop=%.4f left_foot_lift=%.4f right_foot_lift=%.4f hip_above_feet=%.4f" % [
			right_delta,
			left_delta,
			crouch_hip_drop,
			left_foot_lift,
			right_foot_lift,
			crouch["hip_y"] - lowest_foot_y,
		]
	)

	if right_delta <= MINIMUM_LATERAL_HIP_RESPONSE_METRES:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: rightward head offset did not produce positive hip response")
		return false
	if left_delta >= -MINIMUM_LATERAL_HIP_RESPONSE_METRES:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: leftward head offset did not produce negative hip response")
		return false
	if left_foot_lift > MAXIMUM_FOOT_LIFT_METRES or right_foot_lift > MAXIMUM_FOOT_LIFT_METRES:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: crouch unexpectedly lifted one or more foot targets")
		return false
	if crouch_hip_drop < MINIMUM_CROUCH_HIP_DROP_METRES:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: crouch did not lower the hip from standing baseline")
		return false
	if crouch["hip_y"] - lowest_foot_y < MINIMUM_HIP_ABOVE_FEET_METRES:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: hip collapsed too close to the feet")
		return false

	return true


func _require_scenario_marker(scenarios_root: Node3D, marker_name: String) -> Node3D:
	var marker: Node3D = scenarios_root.get_node_or_null(NodePath(marker_name)) as Node3D
	if marker == null:
		SceneUtils.fatal_error_and_quit("IK-004 VRIK regression runner: missing scenario marker %s" % marker_name)
	return marker


func _to_slug(value: Variant) -> String:
	return SceneUtils.to_safe_file_component(str(value).to_lower())
