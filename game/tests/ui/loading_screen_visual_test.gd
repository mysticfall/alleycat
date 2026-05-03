extends SceneTree

const TEST_SCENE_PATH := "res://tests/ui/loading_screen_visual_test.tscn"
const OUTPUT_ROOT := "UI-002/loading_screen"
const WAITING_MESSAGE := "Stand up straight and recentre your headset to continue."
const REPRESENTATIVE_PROGRESS_VALUE := 0.42


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("UI-002 visual runner: failed to load scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var loading_screen: Control = SceneUtils.require_node(photobooth, ^"UILayer/LoadingScreen") as Control
	var loading_message: Label = SceneUtils.require_node(loading_screen, ^"CenterContent/LoadingMessage") as Label
	var progress_bar: ProgressBar = SceneUtils.require_node(loading_screen, ^"CenterContent/LoadingProgressBar") as ProgressBar

	if loading_screen == null or loading_message == null or progress_bar == null:
		SceneUtils.fatal_error_and_quit("UI-002 visual runner: required loading-screen nodes are missing")
		return

	progress_bar.value = REPRESENTATIVE_PROGRESS_VALUE
	await SceneUtils.wait_frames(self, 2)
	await SceneUtils.capture_screenshot(self, "%s/scenarios/loading_state.jpg" % OUTPUT_ROOT)

	loading_message.text = WAITING_MESSAGE
	progress_bar.hide()
	await SceneUtils.wait_frames(self, 2)
	await SceneUtils.capture_screenshot(self, "%s/scenarios/waiting_state.jpg" % OUTPUT_ROOT)

	quit(0)
