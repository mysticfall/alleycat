extends SceneTree

const TEST_SCENE_PATH := "res://tests/speech/blendshape_playback_test.tscn"
const OUTPUT_ROOT := "speech/wav2arkit_prototype"


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("Speech runner: failed to load scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)

	var blendshape_player: Node = SceneUtils.require_node(photobooth, ^"BlendShapePlayer")
	if blendshape_player == null:
		SceneUtils.fatal_error_and_quit("Speech runner: BlendShapePlayer node is missing")
		return

	if not await _run_preflight_assertions(blendshape_player):
		return

	if _has_user_arg("--validate-only"):
		print("Speech runner: validate-only mode completed successfully")
		quit(0)
		return

	await photobooth.capture_screenshots("%s/framing/initial.jpg" % OUTPUT_ROOT)

	await SceneUtils.wait_seconds(self, 0.5)
	await photobooth.capture_screenshots("%s/playback/frame_01.jpg" % OUTPUT_ROOT)

	await SceneUtils.wait_seconds(self, 0.7)
	await photobooth.capture_screenshots("%s/playback/frame_02.jpg" % OUTPUT_ROOT)

	await SceneUtils.wait_seconds(self, 0.8)
	await photobooth.capture_screenshots("%s/playback/frame_03.jpg" % OUTPUT_ROOT)

	quit(0)


func _run_preflight_assertions(blendshape_player: Node) -> bool:
	var is_initialised: bool = bool(blendshape_player.get("IsInitialised"))
	var initialisation_error: String = String(blendshape_player.get("InitialisationError"))
	if not is_initialised:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: BlendShapePlayer failed to initialise. Error: %s" % initialisation_error)
		return false

	var frame_count: int = int(blendshape_player.get("FrameCount"))
	if frame_count <= 0:
		SceneUtils.fatal_error_and_quit("Speech runner: FrameCount must be > 0, got %d" % frame_count)
		return false

	var channel_count: int = int(blendshape_player.get("BlendshapeChannelCount"))
	if channel_count != 52:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: BlendshapeChannelCount must be 52, got %d" % channel_count)
		return false

	var mapped_mesh_count: int = int(blendshape_player.get("MappedMeshCount"))
	if mapped_mesh_count <= 0:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: expected mapped meshes > 0, got %d" % mapped_mesh_count)
		return false

	var mapped_channel_count: int = int(blendshape_player.get("MappedChannelCount"))
	if mapped_channel_count <= 0:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: expected mapped channels > 0, got %d" % mapped_channel_count)
		return false

	var initial_change_count: int = int(blendshape_player.get("WeightChangeEventCount"))
	var initial_applied_frames: int = int(blendshape_player.get("AppliedFrameCount"))
	var initial_audio_playing: bool = bool(blendshape_player.get("IsAudioPlaying"))
	var initial_playback_error: String = String(blendshape_player.get("PlaybackError"))
	if not initial_playback_error.is_empty():
		SceneUtils.fatal_error_and_quit(
			"Speech runner: playback error before sampling window: %s" % initial_playback_error)
		return false
	if not initial_audio_playing:
		SceneUtils.fatal_error_and_quit("Speech runner: audio is not playing at preflight start")
		return false

	await SceneUtils.wait_seconds(self, 0.35)
	var mid_change_count: int = int(blendshape_player.get("WeightChangeEventCount"))
	var mid_applied_frames: int = int(blendshape_player.get("AppliedFrameCount"))
	var mid_audio_playing: bool = bool(blendshape_player.get("IsAudioPlaying"))
	var mid_playback_error: String = String(blendshape_player.get("PlaybackError"))
	var mid_initialised: bool = bool(blendshape_player.get("IsInitialised"))

	await SceneUtils.wait_seconds(self, 0.35)
	var end_change_count: int = int(blendshape_player.get("WeightChangeEventCount"))
	var end_applied_frames: int = int(blendshape_player.get("AppliedFrameCount"))
	var end_audio_playing: bool = bool(blendshape_player.get("IsAudioPlaying"))
	var end_playback_error: String = String(blendshape_player.get("PlaybackError"))
	var end_initialised: bool = bool(blendshape_player.get("IsInitialised"))

	if not mid_initialised or not end_initialised:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: BlendShapePlayer left initialised state during sync check. mid=%s end=%s"
			% [mid_initialised, end_initialised])
		return false

	if not mid_playback_error.is_empty() or not end_playback_error.is_empty():
		SceneUtils.fatal_error_and_quit(
			"Speech runner: playback error detected during sync check. mid='%s' end='%s'"
			% [mid_playback_error, end_playback_error])
		return false

	if not mid_audio_playing or not end_audio_playing:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: audio playback not active during sampled window. mid=%s end=%s"
			% [mid_audio_playing, end_audio_playing])
		return false

	if end_applied_frames <= initial_applied_frames or mid_applied_frames <= initial_applied_frames:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: playback did not advance. AppliedFrameCount start=%d mid=%d end=%d"
			% [initial_applied_frames, mid_applied_frames, end_applied_frames])
		return false

	if end_change_count <= initial_change_count:
		SceneUtils.fatal_error_and_quit(
			"Speech runner: no observable blendshape changes detected. WeightChangeEventCount start=%d end=%d"
			% [initial_change_count, end_change_count])
		return false

	return true


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	for arg: String in user_args:
		if arg == expected_arg:
			return true

	return false
