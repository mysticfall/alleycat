extends SceneTree

const CONTROL_SCRIPT_PATH := "res://tests/speech/lipsync_test_control.gd"


class LipSyncPlayProbe extends Node:
	var play_call_count: int = 0
	var last_speech_clip: AudioStreamWAV = null

	func Play(speech: AudioStreamWAV) -> void:
		play_call_count += 1
		last_speech_clip = speech


func _init() -> void:
	await _run()


func _run() -> void:
	var control_script: GDScript = load(CONTROL_SCRIPT_PATH) as GDScript
	if control_script == null:
		SceneUtils.fatal_error_and_quit("LipSync manual button validation: failed to load %s" % CONTROL_SCRIPT_PATH)
		return

	var root_node := Node.new()
	root_node.name = "LipSyncManualButtonValidationRoot"

	var play_probe := LipSyncPlayProbe.new()
	play_probe.name = "LipSyncPlayer"

	var test_control := CanvasLayer.new()
	test_control.name = "LipSyncTestControl"
	test_control.set_script(control_script)

	var margin_container := MarginContainer.new()
	margin_container.name = "MarginContainer"

	var play_button := Button.new()
	play_button.name = "PlayButton"

	margin_container.add_child(play_button)
	test_control.add_child(margin_container)
	root_node.add_child(play_probe)
	root_node.add_child(test_control)
	root.add_child(root_node)
	await SceneUtils.wait_frames(self, 5)

	if play_button.text != "Play / Restart Speech":
		SceneUtils.fatal_error_and_quit(
			"LipSync manual button validation: expected button text 'Play / Restart Speech', got '%s'" % play_button.text)
		return

	play_button.emit_signal("pressed")
	play_button.emit_signal("pressed")
	await SceneUtils.wait_frames(self, 2)

	var manual_play_request_count: int = test_control.get_manual_play_request_count()
	if manual_play_request_count != 2:
		SceneUtils.fatal_error_and_quit(
			"LipSync manual button validation: expected 2 play requests, got %d" % manual_play_request_count)
		return

	if not test_control.has_requested_speech_clip():
		SceneUtils.fatal_error_and_quit("LipSync manual button validation: control did not retain the requested speech clip")
		return

	if play_probe.play_call_count != 2:
		SceneUtils.fatal_error_and_quit(
			"LipSync manual button validation: expected probe Play() to be called twice, got %d" % play_probe.play_call_count)
		return

	if play_probe.last_speech_clip == null:
		SceneUtils.fatal_error_and_quit("LipSync manual button validation: probe did not receive the speech clip")
		return

	print("LipSync manual button validation passed: play button routed two Play() requests with a speech clip.")
	quit(0)
