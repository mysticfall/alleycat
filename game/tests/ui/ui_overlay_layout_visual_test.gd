extends SceneTree

const TEST_SCENE_PATH := "res://tests/ui/ui_overlay_layout_visual_test.tscn"
const OUTPUT_ROOT := "UI-003/ui_overlay_layout"


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("UI-003 visual runner: failed to load scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	await _capture_camera_marker_framing(photobooth)

	var overlay: Node = SceneUtils.require_node(photobooth, ^"UIOverlay")
	if overlay == null:
		SceneUtils.fatal_error_and_quit("UI-003 visual runner: UIOverlay node is missing")
		return

	if _has_user_arg("--validate-only"):
		print("UI-003 visual runner: validate-only mode completed")
		quit(0)
		return

	overlay.call("TryClearNotifications")
	overlay.call("TrySetDebugMessage", "Debug Widget: Bottom-Centred")
	await SceneUtils.wait_frames(self, 2)
	await SceneUtils.capture_screenshot(self, "%s/scenarios/debug_bottom_centered.jpg" % OUTPUT_ROOT)

	overlay.call("TryPostNotification", "Oldest message", 30.0)
	await SceneUtils.wait_frames(self, 1)
	overlay.call("TryPostNotification", "Middle message", 30.0)
	await SceneUtils.wait_frames(self, 1)
	overlay.call("TryPostNotification", "Newest message", 30.0)
	await SceneUtils.wait_frames(self, 2)

	await SceneUtils.capture_screenshot(self, "%s/scenarios/notification_stack_top_left.jpg" % OUTPUT_ROOT)

	quit(0)


func _capture_camera_marker_framing(photobooth: Photobooth) -> void:
	var marker: DebugMarker = SceneUtils.require_node(photobooth, ^"Markers/OriginMarker") as DebugMarker
	var camera_rig: CameraRig = photobooth.get_camera_rig("OverlayCamera")
	if marker == null or camera_rig == null:
		SceneUtils.fatal_error_and_quit("UI-003 visual runner: required framing nodes are missing")
		return

	marker.visible = true
	await SceneUtils.wait_frames(self, 2)
	await camera_rig.capture_screenshot("%s/framing/overlay_camera_marker.jpg" % OUTPUT_ROOT)
	marker.visible = false


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	for arg: String in user_args:
		if arg == expected_arg:
			return true

	return false
