extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/leg_feet_ik_test.tscn"

const OUTPUT_ROOT := "char003/leg_feet_ik"

const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"BottomCamera",
]

const REQUIRED_POSES := [
	{"left": "LeftNeutral", "right": "RightNeutral", "hips": "HipsNeutral", "slug": "neutral"},
	{"left": "LeftForward", "right": "RightForward", "hips": "HipsNeutral", "slug": "forward"},
	{"left": "LeftOutward", "right": "RightOutward", "hips": "HipsNeutral", "slug": "outward"},
	{"left": "LeftInward", "right": "RightInward", "hips": "HipsNeutral", "slug": "inward"},
	{"left": "LeftNeutral", "right": "RightNeutral", "hips": "HipsCrouch", "slug": "hips-crouch"},
	{"left": "LeftRaised", "right": "RightNeutral", "hips": "HipsLegUpLeft", "slug": "left-leg-up"},
	{"left": "LeftForward", "right": "RightInward", "hips": "HipsAsym", "slug": "asym-lower-body"},
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("IK-003 runner: failed to load test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var foot_target_poses: Node3D = SceneUtils.require_node(photobooth, ^"Markers/FootTargetPoses") as Node3D
	var hips_override_poses: Node3D = SceneUtils.require_node(photobooth, ^"Markers/HipsOverridePoses") as Node3D
	if foot_target_poses == null or hips_override_poses == null:
		SceneUtils.fatal_error_and_quit("IK-003 runner: required pose marker roots are missing")
		return

	var left_foot_target: Marker3D = SceneUtils.require_node(photobooth, ^"Markers/LeftFootTarget") as Marker3D
	var right_foot_target: Marker3D = SceneUtils.require_node(photobooth, ^"Markers/RightFootTarget") as Marker3D
	var left_pole_target: DebugMarker = SceneUtils.require_node(photobooth, ^"Markers/LeftKneePoleTarget") as DebugMarker
	var right_pole_target: DebugMarker = SceneUtils.require_node(photobooth, ^"Markers/RightKneePoleTarget") as DebugMarker
	var hips_harness: BoneAttachment3D = SceneUtils.require_node(
		photobooth,
		^"Subject/Female/Female_export/GeneralSkeleton/HipsOverrideHarness") as BoneAttachment3D

	if left_foot_target == null or right_foot_target == null or hips_harness == null:
		SceneUtils.fatal_error_and_quit("IK-003 runner: required target/harness nodes are missing")
		return

	var foot_pose_markers: Dictionary = _resolve_foot_pose_markers(foot_target_poses)
	if foot_pose_markers.is_empty():
		SceneUtils.fatal_error_and_quit("IK-003 runner: failed to resolve required foot pose markers")
		return

	var hips_pose_markers: Dictionary = _resolve_hips_pose_markers(hips_override_poses)
	if hips_pose_markers.is_empty():
		SceneUtils.fatal_error_and_quit("IK-003 runner: failed to resolve required hips pose markers")
		return

	if _has_user_arg("--validate-only"):
		_validate_pose_scenarios(
			foot_pose_markers,
			hips_pose_markers,
			left_foot_target,
			right_foot_target,
			hips_harness)
		print("IK-003 runner: validate-only mode completed successfully")
		quit(0)
		return

	_set_marker_visibility(foot_pose_markers, "", "")
	_set_single_marker_visibility(hips_pose_markers, "")

	await _capture_framing_pass(
		photobooth,
		foot_pose_markers,
		hips_pose_markers,
		left_foot_target,
		right_foot_target,
		left_pole_target,
		right_pole_target)
	await _capture_pose_scenarios(
		photobooth,
		foot_pose_markers,
		hips_pose_markers,
		left_foot_target,
		right_foot_target,
		hips_harness)

	quit(0)


func _capture_framing_pass(
	photobooth: Photobooth,
	foot_pose_markers: Dictionary,
	hips_pose_markers: Dictionary,
	left_foot_target: Marker3D,
	right_foot_target: Marker3D,
	left_pole_target: DebugMarker,
	right_pole_target: DebugMarker,
) -> void:
	for marker_value: Variant in foot_pose_markers.values():
		var marker: DebugMarker = marker_value as DebugMarker
		if marker != null:
			marker.visible = true

	for marker_value: Variant in hips_pose_markers.values():
		var marker: DebugMarker = marker_value as DebugMarker
		if marker != null:
			marker.visible = true

	await SceneUtils.wait_frames(self, 2)

	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	_set_marker_visibility(foot_pose_markers, "", "")
	_set_single_marker_visibility(hips_pose_markers, "")


func _capture_pose_scenarios(
	photobooth: Photobooth,
	foot_pose_markers: Dictionary,
	hips_pose_markers: Dictionary,
	left_foot_target: Marker3D,
	right_foot_target: Marker3D,
	hips_harness: BoneAttachment3D,
) -> void:
	for pose_index: int in REQUIRED_POSES.size():
		var pose: Dictionary = REQUIRED_POSES[pose_index] as Dictionary
		var left_marker_name: String = pose["left"] as String
		var right_marker_name: String = pose["right"] as String
		var hips_marker_name: String = pose["hips"] as String
		var pose_slug: String = pose["slug"] as String

		var left_marker: DebugMarker = foot_pose_markers.get(left_marker_name) as DebugMarker
		var right_marker: DebugMarker = foot_pose_markers.get(right_marker_name) as DebugMarker
		var hips_marker: DebugMarker = hips_pose_markers.get(hips_marker_name) as DebugMarker

		if left_marker == null or right_marker == null or hips_marker == null:
			SceneUtils.fatal_error_and_quit(
				"IK-003 runner: required markers '%s' / '%s' / '%s' are missing"
				% [left_marker_name, right_marker_name, hips_marker_name])
			return

		_set_marker_visibility(foot_pose_markers, left_marker_name, right_marker_name)
		_set_single_marker_visibility(hips_pose_markers, hips_marker_name)

		left_foot_target.global_transform = left_marker.global_transform
		right_foot_target.global_transform = right_marker.global_transform
		hips_harness.global_transform = hips_marker.global_transform

		await SceneUtils.wait_frames(self, 4)

		var file_name: String = "%s/poses/%02d_%s.jpg" % [OUTPUT_ROOT, pose_index + 1, pose_slug]
		await photobooth.capture_screenshots(file_name)

	_set_marker_visibility(foot_pose_markers, "", "")
	_set_single_marker_visibility(hips_pose_markers, "")


func _validate_pose_scenarios(
	foot_pose_markers: Dictionary,
	hips_pose_markers: Dictionary,
	left_foot_target: Marker3D,
	right_foot_target: Marker3D,
	hips_harness: BoneAttachment3D,
) -> void:
	for pose: Dictionary in REQUIRED_POSES:
		var left_marker_name: String = pose["left"] as String
		var right_marker_name: String = pose["right"] as String
		var hips_marker_name: String = pose["hips"] as String

		var left_marker: DebugMarker = foot_pose_markers.get(left_marker_name) as DebugMarker
		var right_marker: DebugMarker = foot_pose_markers.get(right_marker_name) as DebugMarker
		var hips_marker: DebugMarker = hips_pose_markers.get(hips_marker_name) as DebugMarker

		if left_marker == null or right_marker == null or hips_marker == null:
			SceneUtils.fatal_error_and_quit(
				"IK-003 runner: required validation markers '%s' / '%s' / '%s' are missing"
				% [left_marker_name, right_marker_name, hips_marker_name])
			return

		left_foot_target.global_transform = left_marker.global_transform
		right_foot_target.global_transform = right_marker.global_transform
		hips_harness.global_transform = hips_marker.global_transform


func _resolve_foot_pose_markers(foot_target_poses: Node3D) -> Dictionary:
	var markers := {}
	for pose: Dictionary in REQUIRED_POSES:
		var left_name: String = pose["left"] as String
		var right_name: String = pose["right"] as String

		if not markers.has(left_name):
			var left_marker: DebugMarker = foot_target_poses.get_node_or_null(NodePath(left_name)) as DebugMarker
			if left_marker == null:
				SceneUtils.fatal_error_and_quit("IK-003 runner: required marker '%s' not found" % left_name)
				return {}
			markers[left_name] = left_marker

		if not markers.has(right_name):
			var right_marker: DebugMarker = foot_target_poses.get_node_or_null(NodePath(right_name)) as DebugMarker
			if right_marker == null:
				SceneUtils.fatal_error_and_quit("IK-003 runner: required marker '%s' not found" % right_name)
				return {}
			markers[right_name] = right_marker

	return markers


func _resolve_hips_pose_markers(hips_override_poses: Node3D) -> Dictionary:
	var markers := {}
	for pose: Dictionary in REQUIRED_POSES:
		var hips_name: String = pose["hips"] as String
		if markers.has(hips_name):
			continue

		var hips_marker: DebugMarker = hips_override_poses.get_node_or_null(NodePath(hips_name)) as DebugMarker
		if hips_marker == null:
			SceneUtils.fatal_error_and_quit("IK-003 runner: required hips marker '%s' not found" % hips_name)
			return {}

		markers[hips_name] = hips_marker

	return markers


func _set_marker_visibility(
	pose_markers: Dictionary,
	active_left_name: String,
	active_right_name: String,
) -> void:
	for marker_name: String in pose_markers.keys():
		var marker: DebugMarker = pose_markers[marker_name] as DebugMarker
		if marker != null:
			marker.visible = marker_name == active_left_name or marker_name == active_right_name


func _set_single_marker_visibility(pose_markers: Dictionary, active_marker_name: String) -> void:
	for marker_name: String in pose_markers.keys():
		var marker: DebugMarker = pose_markers[marker_name] as DebugMarker
		if marker != null:
			marker.visible = marker_name == active_marker_name


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
