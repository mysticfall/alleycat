extends SceneTree

const TEST_SCENE_PATH := "res://tests/items/chain/physics_chain_visual_test.tscn"
const OUTPUT_ROOT := "ITEM-001/physics_chain"
const CHAIN_NODE_PATH := ^"Subject/Chain"
const START_ANCHOR_ATTACHMENT_PATH := ^"Subject/StartAnchor/AttachmentPoint"
const END_WEIGHT_PATH := ^"Subject/EndWeight"
const END_WEIGHT_ATTACHMENT_PATH := ^"Subject/EndWeight/AttachmentPoint"
const START_ATTACHMENT_POINT_PATH := ^"Subject/Chain/Links/Link01/StartAttachmentPoint"
const END_ATTACHMENT_POINT_PATH := ^"Subject/Chain/Links/Link10/EndAttachmentPoint"
const ATTACHMENT_TOLERANCE_METRES := 0.02
const INTER_LINK_ATTACHMENT_TOLERANCE_METRES := 0.03
const MINIMUM_WEIGHT_TRAVEL_METRES := 0.05
const LONG_RUN_SETTLE_SECONDS := 30.0
const STABILITY_SAMPLE_INTERVAL_SECONDS := 1.0
const REQUIRED_CAMERAS := [
	"FrontCamera",
	"RightCamera",
	"TopCamera",
]


func _init() -> void:
	await _run()


func _run() -> void:
	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("ITEM-001 runner: failed to load test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 10)

	var end_weight: RigidBody3D = SceneUtils.require_node(photobooth, ^"Subject/EndWeight") as RigidBody3D
	if end_weight == null:
		SceneUtils.fatal_error_and_quit("ITEM-001 runner: EndWeight rigid body is missing")
		return

	if _has_user_arg("--validate-only"):
		end_weight.apply_central_impulse(Vector3(0.55, 0.0, -0.25))
		await _run_long_run_stability_verification(photobooth)
		print("ITEM-001 runner: validate-only mode completed")
		quit(0)
		return

	await _capture_framing_pass(photobooth)
	end_weight.apply_central_impulse(Vector3(0.55, 0.0, -0.25))

	await photobooth.capture_screenshots("%s/scenarios/01_rest.jpg" % OUTPUT_ROOT)
	await SceneUtils.wait_frames(self, 24)
	await photobooth.capture_screenshots("%s/scenarios/02_swing.jpg" % OUTPUT_ROOT)

	await SceneUtils.wait_frames(self, 48)
	await photobooth.capture_screenshots("%s/scenarios/03_settle.jpg" % OUTPUT_ROOT)

	await _run_long_run_stability_verification(photobooth)
	await photobooth.capture_screenshots("%s/scenarios/04_long_run_30s.jpg" % OUTPUT_ROOT)

	quit(0)


func _run_long_run_stability_verification(photobooth: Photobooth) -> void:
	var chain: Node3D = SceneUtils.require_node(photobooth, CHAIN_NODE_PATH) as Node3D
	var start_anchor_attachment: Marker3D = SceneUtils.require_node(photobooth, START_ANCHOR_ATTACHMENT_PATH) as Marker3D
	var end_weight: RigidBody3D = SceneUtils.require_node(photobooth, END_WEIGHT_PATH) as RigidBody3D
	var end_weight_attachment: Marker3D = SceneUtils.require_node(photobooth, END_WEIGHT_ATTACHMENT_PATH) as Marker3D
	var chain_start_attachment: Marker3D = SceneUtils.require_node(photobooth, START_ATTACHMENT_POINT_PATH) as Marker3D
	var chain_end_attachment: Marker3D = SceneUtils.require_node(photobooth, END_ATTACHMENT_POINT_PATH) as Marker3D
	var start_weight_position: Vector3 = end_weight.global_position
	var longest_observed_weight_travel: float = 0.0
	var worst_observed_start_attachment_gap: float = 0.0
	var worst_observed_end_attachment_gap: float = 0.0
	var worst_observed_inter_link_gap: float = 0.0
	var sample_count: int = int(round(LONG_RUN_SETTLE_SECONDS / STABILITY_SAMPLE_INTERVAL_SECONDS))

	for sample_index: int in range(sample_count):
		await SceneUtils.wait_seconds(self, STABILITY_SAMPLE_INTERVAL_SECONDS)

		var elapsed_seconds: float = float(sample_index + 1) * STABILITY_SAMPLE_INTERVAL_SECONDS
		var start_attachment_gap: float = start_anchor_attachment.global_position.distance_to(chain_start_attachment.global_position)
		var end_attachment_gap: float = end_weight_attachment.global_position.distance_to(chain_end_attachment.global_position)

		worst_observed_start_attachment_gap = maxf(worst_observed_start_attachment_gap, start_attachment_gap)
		worst_observed_end_attachment_gap = maxf(worst_observed_end_attachment_gap, end_attachment_gap)
		longest_observed_weight_travel = maxf(longest_observed_weight_travel, end_weight.global_position.distance_to(start_weight_position))

		if start_attachment_gap > ATTACHMENT_TOLERANCE_METRES:
			SceneUtils.fatal_error_and_quit(
				"ITEM-001 long-run verification failed: start attachment gap %.4f m exceeded tolerance %.4f m at %.1f s"
				% [start_attachment_gap, ATTACHMENT_TOLERANCE_METRES, elapsed_seconds])
			return

		if end_attachment_gap > ATTACHMENT_TOLERANCE_METRES:
			SceneUtils.fatal_error_and_quit(
				"ITEM-001 long-run verification failed: end attachment gap %.4f m exceeded tolerance %.4f m at %.1f s"
				% [end_attachment_gap, ATTACHMENT_TOLERANCE_METRES, elapsed_seconds])
			return

		for link_index: int in range(9):
			var back_attachment: Vector3 = chain.call("GetLinkBackAttachmentGlobalPosition", link_index)
			var next_front_attachment: Vector3 = chain.call("GetLinkFrontAttachmentGlobalPosition", link_index + 1)
			var attachment_gap: float = back_attachment.distance_to(next_front_attachment)
			worst_observed_inter_link_gap = maxf(worst_observed_inter_link_gap, attachment_gap)

			if attachment_gap > INTER_LINK_ATTACHMENT_TOLERANCE_METRES:
				SceneUtils.fatal_error_and_quit(
					"ITEM-001 long-run verification failed: inter-link gap %.4f m exceeded tolerance %.4f m at %.1f s between links %d/%d"
					% [attachment_gap, INTER_LINK_ATTACHMENT_TOLERANCE_METRES, elapsed_seconds, link_index + 1, link_index + 2])
				return

	if longest_observed_weight_travel < MINIMUM_WEIGHT_TRAVEL_METRES:
		SceneUtils.fatal_error_and_quit(
			"ITEM-001 long-run verification failed: maximum end-weight travel %.4f m stayed below minimum %.4f m"
			% [longest_observed_weight_travel, MINIMUM_WEIGHT_TRAVEL_METRES])
		return

	print(
		"ITEM-001 long-run metrics: duration=%ds, max_weight_travel=%.4f m, max_start_gap=%.4f m, max_end_gap=%.4f m, max_inter_link_gap=%.4f m"
		% [int(LONG_RUN_SETTLE_SECONDS), longest_observed_weight_travel, worst_observed_start_attachment_gap, worst_observed_end_attachment_gap, worst_observed_inter_link_gap])


func _capture_framing_pass(photobooth: Photobooth) -> void:
	var marker_names := [
		"ChainFocusMarker",
		"StartAnchorMarker",
		"EndWeightMarker",
	]

	for marker_name: String in marker_names:
		var marker: DebugMarker = photobooth.get_marker(marker_name)
		marker.visible = true

	await SceneUtils.wait_frames(self, 2)

	for camera_name: String in REQUIRED_CAMERAS:
		var camera_rig: CameraRig = photobooth.get_camera_rig(camera_name)
		await camera_rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	for marker_name: String in marker_names:
		var marker: DebugMarker = photobooth.get_marker(marker_name)
		marker.visible = false


func _to_slug(value: String) -> String:
	return value.to_lower().replace("camera", "").trim_suffix("_").replace(" ", "_")


func _has_user_arg(expected_arg: String) -> bool:
	if expected_arg.is_empty():
		return false

	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	for arg: String in user_args:
		if arg == expected_arg:
			return true

	return false
