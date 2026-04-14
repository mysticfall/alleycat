extends SceneTree

const TEST_SCENE_PATH := "res://tests/characters/ik/neck_spine_ccdik_test.tscn"
const IK_SCENE_PATH := "res://assets/characters/ik/neck_spine_ccdik.tscn"

const OUTPUT_ROOT := "char001/neck_spine_ccdik"

const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"LeftCamera",
	"BackCamera",
	"TopCamera",
]

const REQUIRED_POSES := [
	{"marker": "TargetForward", "slug": "forward"},
	{"marker": "TargetLeft", "slug": "left"},
	{"marker": "TargetRight", "slug": "right"},
	{"marker": "TargetStoopForward", "slug": "stoop-forward"},
	{"marker": "TargetLeanBack", "slug": "lean-back"},
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("IK-001 runner: failed to load test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var head_target: Node3D = SceneUtils.require_node(photobooth, ^"Markers/HeadTarget") as Node3D
	var target_poses: Node3D = SceneUtils.require_node(photobooth, ^"Markers/TargetPoses") as Node3D
	if head_target == null or target_poses == null:
		SceneUtils.fatal_error_and_quit("IK-001 runner: required marker nodes are missing in the test scene")
		return

	var pose_markers: Dictionary = _resolve_pose_markers(target_poses)
	if pose_markers.is_empty():
		SceneUtils.fatal_error_and_quit("IK-001 runner: failed to resolve required pose markers")
		return

	var skeleton: Skeleton3D = _find_first_skeleton(photobooth)
	if skeleton == null:
		SceneUtils.fatal_error_and_quit("IK-001 runner: failed to find Skeleton3D in the subject scene")
		return

	if not _bind_ik_target(skeleton, head_target):
		return

	if _has_user_arg("--validate-only"):
		print("IK-001 runner: validate-only mode completed successfully")
		quit(0)
		return

	_set_pose_marker_visibility(pose_markers, "")
	head_target.visible = false

	await _capture_framing_pass(photobooth, pose_markers, head_target)
	await _capture_pose_scenarios(photobooth, pose_markers, head_target)

	quit(0)


func _capture_framing_pass(photobooth: Photobooth, pose_markers: Dictionary, head_target: Node3D) -> void:
	for marker_value: Variant in pose_markers.values():
		var marker: DebugMarker = marker_value as DebugMarker
		if marker != null:
			marker.visible = true

	head_target.visible = true

	await SceneUtils.wait_frames(self, 2)

	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	_set_pose_marker_visibility(pose_markers, "")
	head_target.visible = false


func _capture_pose_scenarios(photobooth: Photobooth, pose_markers: Dictionary, head_target: Node3D) -> void:
	for pose_index: int in REQUIRED_POSES.size():
		var pose: Dictionary = REQUIRED_POSES[pose_index] as Dictionary
		var marker_name: String = pose["marker"] as String
		var pose_slug: String = pose["slug"] as String

		var marker: DebugMarker = pose_markers.get(marker_name) as DebugMarker
		if marker == null:
			SceneUtils.fatal_error_and_quit("IK-001 runner: required pose marker '%s' is missing" % marker_name)
			return

		_set_pose_marker_visibility(pose_markers, marker_name)
		head_target.global_transform = marker.global_transform

		await SceneUtils.wait_frames(self, 4)
		await SceneUtils.wait_seconds(self, 0.05)

		var file_name: String = "%s/poses/%02d_%s.jpg" % [OUTPUT_ROOT, pose_index + 1, pose_slug]
		await photobooth.capture_screenshots(file_name)

	_set_pose_marker_visibility(pose_markers, "")


func _resolve_pose_markers(target_poses: Node3D) -> Dictionary:
	var markers := {}
	for pose: Dictionary in REQUIRED_POSES:
		var marker_name: String = pose["marker"] as String
		var marker: DebugMarker = target_poses.get_node_or_null(NodePath(marker_name)) as DebugMarker
		if marker == null:
			SceneUtils.fatal_error_and_quit("IK-001 runner: required marker '%s' not found under TargetPoses" % marker_name)
			return {}

		markers[marker_name] = marker

	return markers


func _bind_ik_target(skeleton: Skeleton3D, head_target: Node3D) -> bool:
	var ik_node: CCDIK3D = skeleton.get_node_or_null(^"NeckSpineCCDIK3D") as CCDIK3D
	if ik_node == null:
		var ik_scene: PackedScene = SceneUtils.load_scene(IK_SCENE_PATH)
		if ik_scene == null:
			SceneUtils.fatal_error_and_quit("IK-001 runner: failed to load IK scene: %s" % IK_SCENE_PATH)
			return false

		ik_node = ik_scene.instantiate() as CCDIK3D
		if ik_node == null:
			SceneUtils.fatal_error_and_quit("IK-001 runner: IK root must be CCDIK3D in scene: %s" % IK_SCENE_PATH)
			return false

		skeleton.add_child(ik_node)

	var target_path: NodePath = ik_node.get_path_to(head_target)
	if target_path.is_empty():
		SceneUtils.fatal_error_and_quit("IK-001 runner: failed to resolve external target path for neck-spine IK")
		return false

	ik_node.set("settings/0/target_node", target_path)

	return true


func _set_pose_marker_visibility(pose_markers: Dictionary, active_marker_name: String) -> void:
	for marker_name: String in pose_markers.keys():
		var marker: DebugMarker = pose_markers[marker_name] as DebugMarker
		if marker != null:
			marker.visible = marker_name == active_marker_name


func _find_first_skeleton(root_node: Node) -> Skeleton3D:
	if root_node == null:
		return null

	var skeleton_nodes: Array[Node] = root_node.find_children("*", "Skeleton3D", true, false)
	if skeleton_nodes.is_empty():
		return null

	return skeleton_nodes[0] as Skeleton3D


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
