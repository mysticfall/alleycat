extends SceneTree

const TEST_SCENE_PATH := "res://tests/attention_gaze_target_selection/attention_gaze_target_selection_photobooth.tscn"
const OUTPUT_ROOT := "ai-007-attention-gaze-target-selection/retry"
const LEFT_SCENARIO := "01_left_attended_target"
const RIGHT_SCENARIO := "02_right_attended_target"
const BLINK_SCENARIO := "03_right_attended_target_blink"
const HORIZONTAL_SEEK_PARAM := "parameters/EyesHorizontalLookSeek/seek_request"
const VERTICAL_SEEK_PARAM := "parameters/EyesVerticalLookSeek/seek_request"
const BLINK_REQUEST_PARAM := "parameters/EyesBlinkOneShot/request"
const ASSIGNMENT_SETTLE_FRAMES := 30
const CAPTURE_SETTLE_FRAMES := 4
const BLINK_CAPTURE_DELAY_SECONDS := 0.15
const BLINK_EYE_CLOSURE_THRESHOLD := 0.6
const OPEN_EYE_THRESHOLD := 0.1
const BLINK_REQUEST_ABORT := 2


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("AI-007 attention gaze photobooth must run with a renderer, never headless")
		return

	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("AI-007 retry photobooth failed to load %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)
	_validate_isolated_scene(photobooth)

	var driver: Node = SceneUtils.require_node(photobooth, ^"AttentionGazeTargetSelectionPhotoboothDriver")
	var observer_viewpoint: Node3D = SceneUtils.require_node(photobooth, ^"Subject/Observer/Female/GeneralSkeleton/Head/Viewpoint") as Node3D
	var left_cue: Node3D = SceneUtils.require_node(photobooth, ^"Subject/LeftTarget/Cue") as Node3D
	var right_cue: Node3D = SceneUtils.require_node(photobooth, ^"Subject/RightTarget/Cue") as Node3D
	if driver == null or observer_viewpoint == null or left_cue == null or right_cue == null:
		SceneUtils.fatal_error_and_quit("AI-007 retry photobooth required runtime nodes are missing")
		return

	driver.call("Activate")
	var camera: Camera3D = _create_face_camera(photobooth, observer_viewpoint)
	await _settle_renderer_and_eye_state()
	_validate_camera_and_target_layout(camera, observer_viewpoint, left_cue, right_cue)
	camera.fov = 75.0
	_set_target_marker_visibility(photobooth, true)
	await _settle_renderer_and_eye_state()
	await SceneUtils.capture_screenshot(self, "%s/framing/face_camera_and_target_cues.jpg" % OUTPUT_ROOT)
	camera.fov = 32.0
	_set_target_marker_visibility(photobooth, false)
	await _settle_renderer_and_eye_state()

	await _capture_assigned_scenario(driver, left_cue, LEFT_SCENARIO, "SetLeftAttendedScenario")
	await _capture_assigned_scenario(driver, right_cue, RIGHT_SCENARIO, "SetRightAttendedScenario")

	driver.call("TriggerAssignedTargetBlink")
	await SceneUtils.wait_seconds(self, BLINK_CAPTURE_DELAY_SECONDS)
	_assert_assigned_target(driver, right_cue, BLINK_SCENARIO)
	_assert_blink_state(photobooth, true)
	await SceneUtils.capture_screenshot(self, "%s/scenarios/%s.jpg" % [OUTPUT_ROOT, BLINK_SCENARIO])

	camera.queue_free()
	photobooth.queue_free()
	await SceneUtils.wait_frames(self, 2)
	print("AI007_ATTENTION_GAZE_RETRY_VISUAL_GATE_PASS artefact_root=game/temp/%s" % OUTPUT_ROOT)
	quit(0)


func _validate_isolated_scene(photobooth: Photobooth) -> void:
	var actor_paths := PackedStringArray()
	for actor: Node in get_nodes_in_group("Actors"):
		actor_paths.append(str(actor.get_path()))
	if actor_paths.size() != 1 or actor_paths[0] != str(photobooth.get_node(^"Subject/Observer").get_path()):
		SceneUtils.fatal_error_and_quit("AI-007 retry fixture requires exactly one observing NPC, found: %s" % "; ".join(actor_paths))
		return

	var cue_subject_paths := PackedStringArray()
	for cue_subject: Node in get_nodes_in_group("AI007CueSubjects"):
		cue_subject_paths.append(str(cue_subject.get_path()))
	if cue_subject_paths.size() != 2:
		SceneUtils.fatal_error_and_quit("AI-007 retry fixture requires exactly two target cues, found: %s" % "; ".join(cue_subject_paths))
		return
	print("AI007_RETRY_SCENE_TREE observer=%s target_cues=%s" % [actor_paths[0], "; ".join(cue_subject_paths)])


func _create_face_camera(parent: Node, viewpoint: Node3D) -> Camera3D:
	var camera := Camera3D.new()
	camera.name = "AI007AttentionGazeRetryFaceCamera"
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = 32.0
	camera.near = 0.01
	camera.far = 10.0
	parent.add_child(camera)
	camera.global_position = viewpoint.global_transform * Vector3(0.0, 0.0, -0.85)
	camera.look_at(viewpoint.global_position + Vector3(0.0, -0.025, 0.0), Vector3.UP)
	camera.make_current()
	return camera


func _settle_renderer_and_eye_state() -> void:
	await SceneUtils.wait_frames(self, ASSIGNMENT_SETTLE_FRAMES)
	await RenderingServer.frame_post_draw


func _validate_camera_and_target_layout(camera: Camera3D, viewpoint: Node3D, left_cue: Node3D, right_cue: Node3D) -> void:
	if root.get_camera_3d() != camera or not camera.is_current():
		SceneUtils.fatal_error_and_quit("AI-007 retry capture camera is not current in the root viewport")
		return

	var camera_local: Vector3 = viewpoint.global_transform.affine_inverse() * camera.global_position
	var left_local: Vector3 = viewpoint.global_transform.affine_inverse() * left_cue.global_position
	var right_local: Vector3 = viewpoint.global_transform.affine_inverse() * right_cue.global_position
	print(
		"AI007_RETRY_DIRECTION_PROBE camera_local=(%.3f,%.3f,%.3f) left_local=(%.3f,%.3f,%.3f) right_local=(%.3f,%.3f,%.3f)" % [
			camera_local.x,
			camera_local.y,
			camera_local.z,
			left_local.x,
			left_local.y,
			left_local.z,
			right_local.x,
			right_local.y,
			right_local.z,
		]
	)
	if camera_local.z >= 0.0:
		SceneUtils.fatal_error_and_quit("AI-007 retry face camera is not in front of the observer face")
		return
	if left_local.z >= 0.0 or right_local.z >= 0.0 or left_local.x >= 0.0 or right_local.x <= 0.0:
		SceneUtils.fatal_error_and_quit("AI-007 retry directional target semantics are invalid")
		return
	if camera.is_position_behind(left_cue.global_position) or camera.is_position_behind(right_cue.global_position):
		SceneUtils.fatal_error_and_quit("AI-007 retry framing does not contain both target cues")
		return
	print(
		"AI007_RETRY_CAMERA_MARKER_CHECK root_camera=%s camera_local=(%.3f,%.3f,%.3f) left_local=(%.3f,%.3f,%.3f) right_local=(%.3f,%.3f,%.3f)" % [
			root.get_camera_3d().get_path(),
			camera_local.x,
			camera_local.y,
			camera_local.z,
			left_local.x,
			left_local.y,
			left_local.z,
			right_local.x,
			right_local.y,
			right_local.z,
		]
	)


func _capture_assigned_scenario(driver: Node, expected_cue: Node3D, scenario_name: String, driver_method: String) -> void:
	_abort_blink(driver.get_parent() as Photobooth)
	driver.call(driver_method)
	await _settle_renderer_and_eye_state()
	_assert_assigned_target(driver, expected_cue, scenario_name)
	_assert_blink_state(driver.get_parent() as Photobooth, false)
	var horizontal_seek: float = float(driver.call("GetEyeAnimationParameter", HORIZONTAL_SEEK_PARAM))
	var vertical_seek: float = float(driver.call("GetEyeAnimationParameter", VERTICAL_SEEK_PARAM))
	if scenario_name == LEFT_SCENARIO and horizontal_seek <= 0.5:
		SceneUtils.fatal_error_and_quit("AI-007 retry left-attended gaze did not visibly move to the observer-left eye pose")
		return
	if scenario_name == RIGHT_SCENARIO and horizontal_seek >= 0.5:
		SceneUtils.fatal_error_and_quit("AI-007 retry right-attended gaze did not visibly move to the observer-right eye pose")
		return
	print(
		"AI007_RETRY_SCENARIO scenario=%s assigned=%s horizontal_seek=%.4f vertical_seek=%.4f" % [
			scenario_name,
			expected_cue.get_path(),
			horizontal_seek,
			vertical_seek,
		]
	)
	await SceneUtils.capture_screenshot(self, "%s/scenarios/%s.jpg" % [OUTPUT_ROOT, scenario_name])


func _set_target_marker_visibility(photobooth: Photobooth, is_visible: bool) -> void:
	var left_marker: MeshInstance3D = SceneUtils.require_node(photobooth, ^"Subject/LeftTarget/LeftTarget Attended Cue") as MeshInstance3D
	var right_marker: MeshInstance3D = SceneUtils.require_node(photobooth, ^"Subject/RightTarget/RightTarget Attended Cue") as MeshInstance3D
	if left_marker == null or right_marker == null:
		return
	left_marker.visible = is_visible
	right_marker.visible = is_visible


func _abort_blink(photobooth: Photobooth) -> void:
	var animation_tree: AnimationTree = SceneUtils.require_node(photobooth, ^"Subject/Observer/AnimationTree") as AnimationTree
	if animation_tree != null:
		animation_tree.set(BLINK_REQUEST_PARAM, BLINK_REQUEST_ABORT)


func _assert_assigned_target(driver: Node, expected_cue: Node3D, scenario_name: String) -> void:
	if not bool(driver.call("HasAssignedLookTarget")):
		SceneUtils.fatal_error_and_quit("AI-007 retry scenario %s did not retain an explicit EyesBehaviour target" % scenario_name)
		return
	var assigned_path: String = str(driver.call("GetAssignedLookTargetPath"))
	if assigned_path != str(expected_cue.get_path()):
		SceneUtils.fatal_error_and_quit("AI-007 retry scenario %s assigned %s, expected selector cue %s" % [scenario_name, assigned_path, expected_cue.get_path()])


func _assert_blink_state(photobooth: Node, expected_closed: bool) -> void:
	var face_mesh: MeshInstance3D = SceneUtils.require_node(photobooth, ^"Subject/Observer/Female/GeneralSkeleton/Female_body") as MeshInstance3D
	var eyelash_mesh: MeshInstance3D = SceneUtils.require_node(photobooth, ^"Subject/Observer/Female/GeneralSkeleton/Female_eyelashes01") as MeshInstance3D
	if face_mesh == null or eyelash_mesh == null:
		return
	var face_closed := _blend_shape_value(face_mesh, "eyeBlinkLeft")
	var eyelash_closed := _blend_shape_value(eyelash_mesh, "eyeBlinkLeft")
	var closure: float = maxf(face_closed, eyelash_closed)
	if expected_closed and closure < BLINK_EYE_CLOSURE_THRESHOLD:
		SceneUtils.fatal_error_and_quit("AI-007 retry blink capture did not reach visible eye closure; closure=%.3f" % closure)
		return
	if not expected_closed and closure > OPEN_EYE_THRESHOLD:
		SceneUtils.fatal_error_and_quit("AI-007 retry open-eye capture still has blink closure; closure=%.3f" % closure)
		return
	print("AI007_RETRY_BLINK_CHECK closed=%s closure=%.3f request=%s" % [expected_closed, closure, str(_eye_blink_request(photobooth))])


func _blend_shape_value(mesh: MeshInstance3D, blend_shape_name: StringName) -> float:
	var blend_shape_index: int = mesh.find_blend_shape_by_name(blend_shape_name)
	return 0.0 if blend_shape_index < 0 else mesh.get_blend_shape_value(blend_shape_index)


func _eye_blink_request(photobooth: Node) -> Variant:
	var animation_tree: AnimationTree = SceneUtils.require_node(photobooth, ^"Subject/Observer/AnimationTree") as AnimationTree
	return null if animation_tree == null else animation_tree.get(BLINK_REQUEST_PARAM)
