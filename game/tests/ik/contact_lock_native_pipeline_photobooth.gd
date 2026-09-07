extends SceneTree

const TEST_SCENE := "res://tests/ik/contact_lock_native_pipeline_photobooth.tscn"
const OUTPUT_ROOT_PREFIX := "IK-003/gate_b_contact_refusal_native_fallback"
const REQUIRED_CAMERAS := ["FrontCamera", "RightCamera", "LeftCamera", "BackCamera", "BottomCamera"]

var _photobooth: Photobooth
var _controller: Node
var _output_root := ""


func _init() -> void:
	await _run()


func _run() -> void:
	_output_root = "%s/%s" % [OUTPUT_ROOT_PREFIX, _timestamped_run_directory()]
	_photobooth = SceneUtils.instantiate_scene(TEST_SCENE) as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("Gate B fixture-only native pipeline photobooth could not be instantiated")
		return
	root.add_child(_photobooth)
	for _physics_frame in 32:
		await physics_frame
		_controller = _photobooth.get_node_or_null(^"NativePipelineController")
		if _controller != null and bool(_controller.get("IsConfigured")):
			break

	if _controller == null or not bool(_controller.get("IsConfigured")):
		SceneUtils.fatal_error_and_quit("Gate B fixture-only native pipeline controller did not configure")
		return

	# Capture the marker while the live Gate B Player-left pole is configured. The marker is
	# deliberately removed before the baseline and scenario captures so their evidence
	# remains uncluttered.
	_controller.call("ApplyGateBScenarioA")
	if not await _await_fresh_native_evidence("directional sanity Gate B player-left pole"):
		return
	if not _validate_directional_marker_transform():
		SceneUtils.fatal_error_and_quit("Gate B directional marker does not resolve the identity fixture's rig +Z avatar-forward convention before capture")
		return
	await _capture_directional_sanity()
	_photobooth.remove_marker("PoleForwardSanity")

	_controller.call("ApplyAllFallback")
	if not await _await_fresh_native_evidence("baseline all-fallback"):
		return
	await _capture_framing_pass()
	await _capture_baseline_scenario()

	_controller.call("ApplyGateBScenarioA")
	if not await _await_fresh_native_evidence("Gate B scenario A player-left/NPC-right"):
		return
	_controller.call("AssertGateBScenarioA")
	await _capture_gate_b_scenario("scenario_a_player_left_npc_right")
	await _capture_focused_role_foot("scenario_a", "Player", "Left", "fallback_masking")
	await _capture_focused_role_foot("scenario_a", "NPC", "Right", "missing")

	_controller.call("ApplyGateBScenarioB")
	if not await _await_fresh_native_evidence("Gate B scenario B player-right/NPC-left"):
		return
	_controller.call("AssertGateBScenarioB")
	await _capture_gate_b_scenario("scenario_b_player_right_npc_left")
	await _capture_focused_role_foot("scenario_b", "Player", "Right", "stale")
	await _capture_focused_role_foot("scenario_b", "NPC", "Left", "contradictory")
	_write_provenance_manifest()
	print("Gate B fixture-only native pipeline captures exported to res://temp/%s" % _output_root)
	quit(0)


func _await_fresh_native_evidence(scenario_name: String) -> bool:
	for _physics_frame in 16:
		await physics_frame
		if bool(_controller.call("HasFreshNativeEvidence")):
			return true

	SceneUtils.fatal_error_and_quit("%s did not receive fresh native TwoBoneIK3D ModificationProcessed evidence on physics frames" % scenario_name)
	return false


func _timestamped_run_directory() -> String:
	var timestamp := Time.get_datetime_dict_from_system()
	return "%04d-%02d-%02d/%02d-%02d-%02d-fixture-only" % [
		int(timestamp.year), int(timestamp.month), int(timestamp.day),
		int(timestamp.hour), int(timestamp.minute), int(timestamp.second),
	]


func _validate_directional_marker_transform() -> bool:
	var avatar_forward: Vector3 = _controller.call("GetAvatarForwardWorld", "Player")
	var marker := _photobooth.add_marker("PoleForwardSanity", "FIXTURE-ONLY POLE FORWARD\nPLAYER LEFT POLE\nRIG +Z → WORLD +Z")
	marker.global_position = _controller.call("GetPoleWorldPosition", "Player", "Left") + Vector3.UP * 0.10
	# DebugMarker's blue conical +Z axis is the visible directional arrow.  Aim its +Z axis at
	# the live semantic forward vector so the arrow and the label describe the same transform.
	marker.look_at(marker.global_position - avatar_forward, Vector3.UP)
	var actual_forward := marker.global_transform.basis.z.normalized()
	var valid := actual_forward.dot(avatar_forward) > 0.99 and avatar_forward.dot(Vector3.BACK) > 0.99
	return valid


func _capture_directional_sanity() -> void:
	# The right camera views world +Z laterally, making the marker's forward direction readable
	# beside the Player left pole, native solved-leg sticks, knee, target, and end-bone evidence.
	var camera: CameraRig = _photobooth.get_camera_rig("RightCamera")
	await camera.capture_screenshot("%s/directional_sanity/pole_forward_player_left_right_camera.jpg" % _output_root)


func _capture_framing_pass() -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
		await camera.capture_screenshot("%s/framing/%s.jpg" % [_output_root, camera_name.to_snake_case()])


func _capture_gate_b_scenario(scenario_name: String) -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
		await camera.capture_screenshot("%s/scenarios/%s_%s.jpg" % [_output_root, scenario_name, camera_name.to_snake_case()])


func _capture_focused_role_foot(scenario_name: String, role: String, foot: String, evidence_name: String) -> void:
	# The broad five-camera set proves overall context. These front/right close-ups deliberately hide all other
	# role-foot bundles, so the marker, refusal panel, fallback target, and native-solved end cannot overlap.
	_controller.call("FocusRoleFootEvidence", role, foot)
	await process_frame
	var centre: Vector3 = _controller.call("GetRoleFootEvidenceCentre", role, foot)
	# Keep the named front view recognisably frontal while giving the hollow marker a slight diagonal view; the right
	# close-up remains the primary aperture/split-pair inspection angle. The wider scale reserves a separate panel lane.
	await _capture_focused_camera(
		"FrontCamera", centre + Vector3(0.32, 0.24, -1.02), centre + Vector3.UP * 0.20,
		"%s/focused/%s_%s_%s_%s_front_camera.jpg" % [_output_root, scenario_name, role.to_snake_case(), foot.to_snake_case(), evidence_name])
	await _capture_focused_camera(
		"RightCamera", centre + Vector3(1.05, 0.24, 0.10), centre + Vector3.UP * 0.20,
		"%s/focused/%s_%s_%s_%s_right_camera.jpg" % [_output_root, scenario_name, role.to_snake_case(), foot.to_snake_case(), evidence_name])
	_controller.call("ShowAllEvidence")


func _capture_focused_camera(camera_name: String, position: Vector3, target: Vector3, output_path: String) -> void:
	var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
	var original_transform := camera.global_transform
	var original_scale := camera.orthogonal_scale
	camera.global_position = position
	camera.look_at(target, Vector3.UP)
	camera.orthogonal_scale = 0.82
	await camera.capture_screenshot(output_path)
	camera.global_transform = original_transform
	camera.orthogonal_scale = original_scale


func _capture_baseline_scenario() -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		var camera: CameraRig = _photobooth.get_camera_rig(camera_name)
		await camera.capture_screenshot("%s/scenarios/baseline_all_fallback_%s.jpg" % [_output_root, camera_name.to_snake_case()])


func _write_provenance_manifest() -> void:
	var manifest := {
		"gate": "IK-003 Gate B visual evidence (fixture/test-only; not a production release decision)",
		"scene": TEST_SCENE,
		"runner": "res://tests/ik/contact_lock_native_pipeline_photobooth.gd",
		"base": "res://assets/testing/photobooth/templates/lower_body_5_cams.tscn",
		"fixtureTopology": "res://tests/ik/contact_lock_leg_solve_fixture.tscn",
		"pipeline": "CharacterIK priority provider stage -> native Skeleton3D TwoBoneIK3D",
		"scenario": "Paired refused-contact cases. Scenario A samples Player-left fallback-masking and NPC-right missing evidence; scenario B samples Player-right stale and NPC-left contradictory evidence. Every displayed case retains an ordinary fallback and fresh native solve while refusing contact publication.",
		"footTargetToleranceMetres": 0.006,
		"directionalSanity": "The identity minimal fixture maps rig avatar +Z to world +Z (Godot world-back). Every role-foot bundle includes a cyan shaft-and-arrowhead from its live pole along that vector; the retained standalone marker and C# dot-product guard validate the same convention before capture.",
		"captures": [
			"directional_sanity/pole_forward_player_left_right_camera.jpg",
			"framing/front_camera.jpg", "framing/right_camera.jpg", "framing/left_camera.jpg", "framing/back_camera.jpg", "framing/bottom_camera.jpg",
			"scenarios/baseline_all_fallback_front_camera.jpg",
			"scenarios/baseline_all_fallback_right_camera.jpg",
			"scenarios/baseline_all_fallback_left_camera.jpg",
			"scenarios/baseline_all_fallback_back_camera.jpg",
			"scenarios/baseline_all_fallback_bottom_camera.jpg",
			"scenarios/scenario_a_player_left_npc_right_front_camera.jpg",
			"scenarios/scenario_a_player_left_npc_right_right_camera.jpg",
			"scenarios/scenario_a_player_left_npc_right_left_camera.jpg",
			"scenarios/scenario_a_player_left_npc_right_back_camera.jpg",
			"scenarios/scenario_a_player_left_npc_right_bottom_camera.jpg",
			"scenarios/scenario_b_player_right_npc_left_front_camera.jpg",
			"scenarios/scenario_b_player_right_npc_left_right_camera.jpg",
			"scenarios/scenario_b_player_right_npc_left_left_camera.jpg",
			"scenarios/scenario_b_player_right_npc_left_back_camera.jpg",
			"scenarios/scenario_b_player_right_npc_left_bottom_camera.jpg",
			"focused/scenario_a_player_left_fallback_masking_front_camera.jpg",
			"focused/scenario_a_player_left_fallback_masking_right_camera.jpg",
			"focused/scenario_a_npc_right_missing_front_camera.jpg",
			"focused/scenario_a_npc_right_missing_right_camera.jpg",
			"focused/scenario_b_player_right_stale_front_camera.jpg",
			"focused/scenario_b_player_right_stale_right_camera.jpg",
			"focused/scenario_b_npc_left_contradictory_front_camera.jpg",
			"focused/scenario_b_npc_left_contradictory_right_camera.jpg",
		],
	}
	var output_path := "res://temp/%s/provenance_manifest.json" % _output_root
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path("res://temp/%s" % _output_root))
	var file := FileAccess.open(output_path, FileAccess.WRITE)
	if file == null:
		SceneUtils.fatal_error_and_quit("Could not write Gate A provenance manifest")
		return
	file.store_string(JSON.stringify(manifest, "\t") + "\n")
	file.close()
