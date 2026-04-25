extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/arm_shoulder_ik_test.tscn"

const OUTPUT_ROOT := "IK-002/arm_shoulder_ik"

const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"TopCamera",
]

const REQUIRED_POSES := [
	{"left": "LeftLowered", "right": "RightLowered", "slug": "lowered"},
	{"left": "LeftForward", "right": "RightForward", "slug": "forward"},
	{"left": "LeftOverhead", "right": "RightOverhead", "slug": "overhead"},
	{"left": "LeftSide", "right": "RightSide", "slug": "side"},
	{"left": "LeftBehindHead", "right": "RightBehindHead", "slug": "behind-head"},
	{"left": "LeftChest", "right": "RightChest", "slug": "chest"},
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("IK-002 runner: failed to load test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var hand_target_poses: Node3D = SceneUtils.require_node(photobooth, ^"Markers/HandTargetPoses") as Node3D
	if hand_target_poses == null:
		SceneUtils.fatal_error_and_quit("IK-002 runner: required HandTargetPoses node is missing")
		return

	var left_hand_target: Marker3D = SceneUtils.require_node(photobooth, ^"Markers/LeftHandTarget") as Marker3D
	var right_hand_target: Marker3D = SceneUtils.require_node(photobooth, ^"Markers/RightHandTarget") as Marker3D
	if left_hand_target == null or right_hand_target == null:
		SceneUtils.fatal_error_and_quit("IK-002 runner: hand target markers are missing from the test scene")
		return

	var pose_markers: Dictionary = _resolve_pose_markers(hand_target_poses)
	if pose_markers.is_empty():
		SceneUtils.fatal_error_and_quit("IK-002 runner: failed to resolve required pose markers")
		return

	if _has_user_arg("--validate-only"):
		print("IK-002 runner: validate-only mode completed successfully")
		quit(0)
		return

	_set_pose_marker_visibility(pose_markers, "", "")
	left_hand_target.visible = false
	right_hand_target.visible = false

	await _capture_framing_pass(photobooth, pose_markers, left_hand_target, right_hand_target)
	await _capture_pose_scenarios(photobooth, pose_markers, left_hand_target, right_hand_target)

	quit(0)


func _capture_framing_pass(
	photobooth: Photobooth,
	pose_markers: Dictionary,
	left_hand_target: Marker3D,
	right_hand_target: Marker3D,
) -> void:
	for marker_value: Variant in pose_markers.values():
		var marker: DebugMarker = marker_value as DebugMarker
		if marker != null:
			marker.visible = true

	left_hand_target.visible = true
	right_hand_target.visible = true

	await SceneUtils.wait_frames(self, 2)

	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	_set_pose_marker_visibility(pose_markers, "", "")


func _capture_pose_scenarios(
	photobooth: Photobooth,
	pose_markers: Dictionary,
	left_hand_target: Marker3D,
	right_hand_target: Marker3D,
) -> void:
	for pose_index: int in REQUIRED_POSES.size():
		var pose: Dictionary = REQUIRED_POSES[pose_index] as Dictionary
		var left_marker_name: String = pose["left"] as String
		var right_marker_name: String = pose["right"] as String
		var pose_slug: String = pose["slug"] as String

		var left_marker: DebugMarker = pose_markers.get(left_marker_name) as DebugMarker
		var right_marker: DebugMarker = pose_markers.get(right_marker_name) as DebugMarker

		if left_marker == null or right_marker == null:
			SceneUtils.fatal_error_and_quit(
				"IK-002 runner: required pose markers '%s' / '%s' are missing" % [left_marker_name, right_marker_name])
			return

		_set_pose_marker_visibility(pose_markers, left_marker_name, right_marker_name)

		left_hand_target.global_transform = left_marker.global_transform
		right_hand_target.global_transform = right_marker.global_transform

		await SceneUtils.wait_frames(self, 4)

		var file_name: String = "%s/poses/%02d_%s.jpg" % [OUTPUT_ROOT, pose_index + 1, pose_slug]
		await photobooth.capture_screenshots(file_name)

	_set_pose_marker_visibility(pose_markers, "", "")


func _resolve_pose_markers(hand_target_poses: Node3D) -> Dictionary:
	var markers := {}
	for pose: Dictionary in REQUIRED_POSES:
		var left_name: String = pose["left"] as String
		var right_name: String = pose["right"] as String

		var left_marker: DebugMarker = hand_target_poses.get_node_or_null(NodePath(left_name)) as DebugMarker
		if left_marker == null:
			SceneUtils.fatal_error_and_quit(
				"IK-002 runner: required marker '%s' not found under HandTargetPoses" % left_name)
			return {}

		var right_marker: DebugMarker = hand_target_poses.get_node_or_null(NodePath(right_name)) as DebugMarker
		if right_marker == null:
			SceneUtils.fatal_error_and_quit(
				"IK-002 runner: required marker '%s' not found under HandTargetPoses" % right_name)
			return {}

		markers[left_name] = left_marker
		markers[right_name] = right_marker

	return markers


func _set_pose_marker_visibility(
	pose_markers: Dictionary,
	active_left_name: String,
	active_right_name: String,
) -> void:
	for marker_name: String in pose_markers.keys():
		var marker: DebugMarker = pose_markers[marker_name] as DebugMarker
		if marker != null:
			marker.visible = marker_name == active_left_name or marker_name == active_right_name


func _to_slug(value: String) -> String:
	return SceneUtils.to_safe_file_component(value.to_lower())


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	for arg: String in user_args:
		if arg == expected_arg:
			return true

	return false
