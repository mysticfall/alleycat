extends SceneTree

## VISION-001 transform-driven eye-gaze visual verification runner.
## Loads res://tests/vision/eyes_transform_visual_test.tscn, verifies camera and
## marker framing, runs the Scene-Setup directional sanity gate (marker semantics vs
## EyesLookMath sign conventions), then captures each AC 38 scenario and asserts the
## non-visual state that must hold at capture time (transform mode: look blends zero,
## seek requests unchanged, measured gaze angles within the shared limits).
##
## Must run WITHOUT --headless so screenshots come from a real renderer.

const TEST_SCENE_PATH := "res://tests/vision/eyes_transform_visual_test.tscn"
const OUTPUT_SUBDIR := "eyes_transform"

const HORIZONTAL_BLEND_PARAM := "parameters/EyesHorizontalLookBlend/blend_amount"
const VERTICAL_BLEND_PARAM := "parameters/EyesVerticalLookBlend/blend_amount"
const HORIZONTAL_SEEK_PARAM := "parameters/EyesHorizontalLookSeek/seek_request"
const VERTICAL_SEEK_PARAM := "parameters/EyesVerticalLookSeek/seek_request"

const MAX_HORIZONTAL_ANGLE_RADIANS := deg_to_rad(35.0)
const MAX_VERTICAL_ANGLE_RADIANS := deg_to_rad(25.0)
const ANGLE_TOLERANCE_RADIANS := deg_to_rad(1.5)
const MARKER_LAYOUT_DISTANCE := 0.14

var photobooth: Photobooth
var rig: Node3D
var eyes: Node
var eye_origin: Node3D
var left_eye: Node3D
var right_eye: Node3D
var gaze_target: Node3D
var tree: AnimationTree
var face_mesh: MeshInstance3D
var camera_rig: CameraRig
var left_eye_authored_local: Transform3D
var right_eye_authored_local: Transform3D
var initial_horizontal_seek: float
var initial_vertical_seek: float


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner must not run headless because screenshots would be invalid")
		return

	photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("EYES_TRANSFORM runner: failed to load test scene: %s" % TEST_SCENE_PATH)
		return

	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, 2)
	_resolve_fixture_nodes()
	await _capture_framing()
	_validate_marker_directions()

	await _scenario_neutral()
	await _scenario_directional("look_left", "TargetLeft")
	await _scenario_directional("look_right", "TargetRight")
	await _scenario_directional("look_up", "TargetUp")
	await _scenario_directional("look_down", "TargetDown")
	await _scenario_directional("diagonal", "TargetDiagonal")
	await _scenario_beyond_limit()
	await _scenario_smoothing()
	await _scenario_return_to_neutral()
	await _scenario_blink_while_looking()

	print("VISION001_EYES_TRANSFORM_VISUAL_GATE_PASS artefact_subdir=%s" % OUTPUT_SUBDIR)
	quit(0)


func _resolve_fixture_nodes() -> void:
	rig = SceneUtils.require_node(photobooth, ^"Subject/EyesTransformRig") as Node3D
	eyes = SceneUtils.require_node(rig, ^"Eyes")
	eye_origin = SceneUtils.require_node(rig, ^"Head/EyeOrigin") as Node3D
	left_eye = SceneUtils.require_node(rig, ^"Head/LeftEyeSocket/LeftEye") as Node3D
	right_eye = SceneUtils.require_node(rig, ^"Head/RightEyeSocket/RightEye") as Node3D
	gaze_target = SceneUtils.require_node(rig, ^"GazeTarget") as Node3D
	tree = SceneUtils.require_node(rig, ^"AnimationTree") as AnimationTree
	face_mesh = SceneUtils.require_node(rig, ^"GeneralSkeleton/Female_body") as MeshInstance3D
	camera_rig = photobooth.get_camera_rig("CameraRig")

	if not (eyes.get("LeftEye") is Node3D and eyes.get("RightEye") is Node3D):
		SceneUtils.fatal_error_and_quit("EYES_TRANSFORM runner: both eye references must be assigned (transform mode)")
		return
	if eye_origin.global_transform.basis.get_rotation_quaternion() != Quaternion.IDENTITY:
		SceneUtils.fatal_error_and_quit("EYES_TRANSFORM runner: fixture EyeOrigin must have identity rotation for the neutral equivalence check")
		return

	left_eye_authored_local = left_eye.transform
	right_eye_authored_local = right_eye.transform
	initial_horizontal_seek = tree.get(HORIZONTAL_SEEK_PARAM)
	initial_vertical_seek = tree.get(VERTICAL_SEEK_PARAM)
	print("EYES_TRANSFORM_CAMERA rig=CameraRig projection=%s image_size=%s" % [
		camera_rig.camera.projection, camera_rig.image_size])


func _capture_framing() -> void:
	await camera_rig.capture_screenshot("%s/framing/face.jpg" % OUTPUT_SUBDIR)
	for marker_name: String in [
		"TargetNeutral", "TargetLeft", "TargetRight", "TargetUp",
		"TargetDown", "TargetDiagonal", "TargetBeyondLimit",
	]:
		var marker: DebugMarker = photobooth.get_marker(marker_name)
		var authored_position: Vector3 = marker.global_position
		marker.global_position = (
			eye_origin.global_position
			+ (authored_position - eye_origin.global_position).normalized() * MARKER_LAYOUT_DISTANCE)
		marker.visible = true
		await camera_rig.capture_screenshot(
			"%s/framing/marker_%s.jpg" % [OUTPUT_SUBDIR, marker_name.to_snake_case()])
		marker.visible = false
		marker.global_position = authored_position


## Scene-Setup Gate: every directional marker must resolve, through first-principles
## eye-origin-frame maths matching EyesLookMath sign conventions, to the direction its
## name claims. Contradiction stops the run before any scenario capture.
func _validate_marker_directions() -> void:
	for entry: Dictionary in [
		{"marker": "TargetNeutral", "check": "neutral"},
		{"marker": "TargetLeft", "check": "horizontal_negative"},
		{"marker": "TargetRight", "check": "horizontal_positive"},
		{"marker": "TargetUp", "check": "vertical_positive"},
		{"marker": "TargetDown", "check": "vertical_negative"},
		{"marker": "TargetDiagonal", "check": "diagonal_left_up"},
		{"marker": "TargetBeyondLimit", "check": "beyond_limits"},
	]:
		var marker: DebugMarker = photobooth.get_marker(entry["marker"])
		var clamped := _resolve_clamped_angles(marker.global_position)
		var raw := _resolve_raw_angles(marker.global_position)
		var check: String = entry["check"]
		var valid := true
		match check:
			"neutral":
				valid = absf(clamped.x) < deg_to_rad(0.5) and absf(clamped.y) < deg_to_rad(0.5)
			"horizontal_negative":
				valid = clamped.x < -deg_to_rad(20.0) and absf(clamped.y) < deg_to_rad(1.0)
			"horizontal_positive":
				valid = clamped.x > deg_to_rad(20.0) and absf(clamped.y) < deg_to_rad(1.0)
			"vertical_positive":
				valid = clamped.y > deg_to_rad(15.0) and absf(clamped.x) < deg_to_rad(1.0)
			"vertical_negative":
				valid = clamped.y < -deg_to_rad(15.0) and absf(clamped.x) < deg_to_rad(1.0)
			"diagonal_left_up":
				valid = clamped.x < -deg_to_rad(15.0) and clamped.y > deg_to_rad(10.0)
			"beyond_limits":
				valid = raw.x > deg_to_rad(60.0) and raw.y > deg_to_rad(30.0) \
					and absf(clamped.x - MAX_HORIZONTAL_ANGLE_RADIANS) < deg_to_rad(0.1) \
					and absf(clamped.y - MAX_VERTICAL_ANGLE_RADIANS) < deg_to_rad(0.1)
		if not valid:
			SceneUtils.fatal_error_and_quit(
				"EYES_TRANSFORM runner: marker %s semantics contradict EyesLookMath (check=%s clamped=(%.2f, %.2f) deg raw=(%.2f, %.2f) deg)" % [
					entry["marker"], check,
					rad_to_deg(clamped.x), rad_to_deg(clamped.y),
					rad_to_deg(raw.x), rad_to_deg(raw.y)])
			return
		print("EYES_TRANSFORM_MARKER name=%s clamped=(%.2f, %.2f) raw=(%.2f, %.2f)" % [
			entry["marker"],
			rad_to_deg(clamped.x), rad_to_deg(clamped.y),
			rad_to_deg(raw.x), rad_to_deg(raw.y)])


func _scenario_neutral() -> void:
	eyes.call("ClearLookTarget")
	await SceneUtils.wait_seconds(self, 0.6)
	_assert_transform_mode("neutral")
	if left_eye.transform != left_eye_authored_local or right_eye.transform != right_eye_authored_local:
		SceneUtils.fatal_error_and_quit("EYES_TRANSFORM runner: neutral scenario must rest at the authored neutral pose")
		return
	await photobooth.capture_screenshots("%s/scenarios/01_neutral.jpg" % OUTPUT_SUBDIR)


func _scenario_directional(scenario_name: String, marker_name: String) -> void:
	var marker: DebugMarker = photobooth.get_marker(marker_name)
	gaze_target.global_position = marker.global_position
	eyes.call("ClearLookTarget")
	eyes.call("SetLookTarget", gaze_target)
	await SceneUtils.wait_seconds(self, 0.7)
	_assert_transform_mode(scenario_name)
	_assert_gaze_matches_target(scenario_name, marker.global_position)
	await photobooth.capture_screenshots(
		"%s/scenarios/%s.jpg" % [OUTPUT_SUBDIR, _scenario_file_name(scenario_name)])


func _scenario_beyond_limit() -> void:
	var marker: DebugMarker = photobooth.get_marker("TargetBeyondLimit")
	gaze_target.global_position = marker.global_position
	eyes.call("ClearLookTarget")
	eyes.call("SetLookTarget", gaze_target)
	await SceneUtils.wait_seconds(self, 0.7)
	_assert_transform_mode("beyond_limit_clamped")

	var measured := _measure_gaze_angles()
	var raw := _resolve_raw_angles(marker.global_position)
	if absf(absf(measured.x) - MAX_HORIZONTAL_ANGLE_RADIANS) > deg_to_rad(1.5) \
			or absf(absf(measured.y) - MAX_VERTICAL_ANGLE_RADIANS) > deg_to_rad(1.5):
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: beyond-limit gaze must clamp to the shared limits, measured=(%.2f, %.2f) deg raw=(%.2f, %.2f) deg" % [
				rad_to_deg(measured.x), rad_to_deg(measured.y), rad_to_deg(raw.x), rad_to_deg(raw.y)])
		return
	print("EYES_TRANSFORM_CLAMP raw=(%.2f, %.2f) clamped_measured=(%.2f, %.2f)" % [
		rad_to_deg(raw.x), rad_to_deg(raw.y), rad_to_deg(measured.x), rad_to_deg(measured.y)])
	await photobooth.capture_screenshots("%s/scenarios/07_beyond_limit_clamped.jpg" % OUTPUT_SUBDIR)


func _scenario_smoothing() -> void:
	# Slow smoothing keeps every capture mid-transition even with the extra elapsed
	# time screenshot captures add between the timed waits.
	eyes.set("LookSmoothingTime", 1.2)
	var left_marker: DebugMarker = photobooth.get_marker("TargetLeft")
	var right_marker: DebugMarker = photobooth.get_marker("TargetRight")
	gaze_target.global_position = left_marker.global_position
	eyes.call("SetLookTarget", gaze_target)
	await SceneUtils.wait_seconds(self, 2.0)

	gaze_target.global_position = right_marker.global_position
	# Mid-transition windows are intentionally wide (frame-timing varies under software
	# rendering); the decisive properties are: never snapped to either extreme and a
	# monotonic progression across the three captures.
	var progress_angles: Array[float] = []
	await SceneUtils.wait_seconds(self, 0.10)
	_assert_transform_mode("smoothing_progress_a")
	progress_angles.append(_assert_gaze_between_extremes("smoothing_progress_a"))
	await photobooth.capture_screenshots("%s/scenarios/08_smoothing_progress_a.jpg" % OUTPUT_SUBDIR)

	await SceneUtils.wait_seconds(self, 0.12)
	_assert_transform_mode("smoothing_progress_b")
	progress_angles.append(_assert_gaze_between_extremes("smoothing_progress_b"))
	await photobooth.capture_screenshots("%s/scenarios/08_smoothing_progress_b.jpg" % OUTPUT_SUBDIR)

	await SceneUtils.wait_seconds(self, 0.12)
	_assert_transform_mode("smoothing_progress_c")
	progress_angles.append(_assert_gaze_between_extremes("smoothing_progress_c"))
	await photobooth.capture_screenshots("%s/scenarios/08_smoothing_progress_c.jpg" % OUTPUT_SUBDIR)

	if not (progress_angles[0] < progress_angles[1] and progress_angles[1] < progress_angles[2]):
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: smoothing captures must progress monotonically left-to-right, measured %s" % [
				progress_angles.map(func(angle: float) -> String: return "%.2f" % rad_to_deg(angle))])
		return
	print("EYES_TRANSFORM_SMOOTHING progress_deg=%s" % [
		progress_angles.map(func(angle: float) -> String: return "%.2f" % rad_to_deg(angle))])

	await SceneUtils.wait_seconds(self, 2.0)


func _scenario_return_to_neutral() -> void:
	# Converged at look_right by the end of the smoothing scenario; return to neutral
	# with a longer smoothing time so the capture lands clearly mid-return even with
	# frame-timing slop under software rendering.
	eyes.set("LookSmoothingTime", 0.8)
	var neutral_marker: DebugMarker = photobooth.get_marker("TargetNeutral")
	gaze_target.global_position = neutral_marker.global_position
	await SceneUtils.wait_seconds(self, 0.18)
	_assert_transform_mode("return_to_neutral")
	var horizontal := _measure_gaze_angles().x
	if horizontal < deg_to_rad(2.0) or horizontal > deg_to_rad(23.0):
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: return_to_neutral must capture mid-return (between the right extreme and the centre), measured %.2f deg" % rad_to_deg(horizontal))
		return
	await photobooth.capture_screenshots("%s/scenarios/09_return_to_neutral.jpg" % OUTPUT_SUBDIR)

	await SceneUtils.wait_seconds(self, 1.0)
	if left_eye.transform != left_eye_authored_local or right_eye.transform != right_eye_authored_local:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: return-to-neutral must settle at the exact authored neutral pose")
		return
	await photobooth.capture_screenshots(
		"%s/scenarios/09b_return_to_neutral_settled.jpg" % OUTPUT_SUBDIR)
	eyes.set("LookSmoothingTime", 0.12)


func _scenario_blink_while_looking() -> void:
	var marker: DebugMarker = photobooth.get_marker("TargetDiagonal")
	gaze_target.global_position = marker.global_position
	eyes.call("SetLookTarget", gaze_target)
	await SceneUtils.wait_seconds(self, 0.8)
	_assert_transform_mode("blink_while_looking")

	eyes.call("TriggerBlink")
	await SceneUtils.wait_seconds(self, 0.16)
	# The active tree's one-shot consumes the Fire request on the following frame, so the
	# durable evidence is the one-shot being active plus the mesh blend value mid-close.
	var blink_active: bool = tree.get("parameters/EyesBlinkOneShot/active")
	if not blink_active:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: forced blink must drive the one-shot node (active=false)")
		return
	_assert_transform_mode("blink_while_looking")
	_assert_gaze_matches_target("blink_while_looking", marker.global_position)
	var blink_value := _read_blink_blend_value()
	if blink_value < 0.35 or blink_value > 0.95:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: blink capture must be mid-close, blend value %.3f outside [0.35, 0.95]" % blink_value)
		return
	print("EYES_TRANSFORM_BLINK mid_close_value=%.3f gaze=(%.2f, %.2f)" % [
		blink_value, rad_to_deg(_measure_gaze_angles().x), rad_to_deg(_measure_gaze_angles().y)])
	await photobooth.capture_screenshots("%s/scenarios/10_blink_while_looking.jpg" % OUTPUT_SUBDIR)
	await SceneUtils.wait_seconds(self, 0.6)


func _scenario_file_name(scenario_name: String) -> String:
	match scenario_name:
		"look_left":
			return "02_look_left"
		"look_right":
			return "03_look_right"
		"look_up":
			return "04_look_up"
		"look_down":
			return "05_look_down"
		"diagonal":
			return "06_diagonal"
		_:
			SceneUtils.fatal_error_and_quit("EYES_TRANSFORM runner: unmapped scenario %s" % scenario_name)
			return scenario_name


func _assert_transform_mode(scenario_name: String) -> void:
	var horizontal_blend: float = tree.get(HORIZONTAL_BLEND_PARAM)
	var vertical_blend: float = tree.get(VERTICAL_BLEND_PARAM)
	if absf(horizontal_blend) > 0.00001 or absf(vertical_blend) > 0.00001:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: %s must keep look blends at 0 in transform mode (h=%.5f v=%.5f)" % [
				scenario_name, horizontal_blend, vertical_blend])
		return
	var horizontal_seek: float = tree.get(HORIZONTAL_SEEK_PARAM)
	var vertical_seek: float = tree.get(VERTICAL_SEEK_PARAM)
	if absf(horizontal_seek - initial_horizontal_seek) > 0.0000001 \
			or absf(vertical_seek - initial_vertical_seek) > 0.0000001:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: %s must not emit look seek requests in transform mode" % scenario_name)
		return


func _assert_gaze_matches_target(scenario_name: String, target_world_position: Vector3) -> void:
	var expected := _resolve_clamped_angles(target_world_position)
	var measured := _measure_gaze_angles()
	if absf(measured.x - expected.x) > ANGLE_TOLERANCE_RADIANS \
			or absf(measured.y - expected.y) > ANGLE_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: %s gaze (%.2f, %.2f) deg does not match the expected clamped angles (%.2f, %.2f) deg" % [
				scenario_name,
				rad_to_deg(measured.x), rad_to_deg(measured.y),
				rad_to_deg(expected.x), rad_to_deg(expected.y)])
		return


func _assert_gaze_between_extremes(scenario_name: String) -> float:
	var horizontal := _measure_gaze_angles().x
	if absf(horizontal) >= deg_to_rad(22.0):
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: %s must capture the gaze mid-transition, not snapped to an extreme, measured %.2f deg" % [
				scenario_name, rad_to_deg(horizontal)])
		return horizontal
	return horizontal


## First-principles clamped gaze angles for a world position, matching the
## EyesLookMath conventions (-Z forward, +X right, +Y up, rotation-only conversion).
func _resolve_clamped_angles(target_world_position: Vector3) -> Vector2:
	var raw := _resolve_raw_angles(target_world_position)
	return Vector2(
		clampf(raw.x, -MAX_HORIZONTAL_ANGLE_RADIANS, MAX_HORIZONTAL_ANGLE_RADIANS),
		clampf(raw.y, -MAX_VERTICAL_ANGLE_RADIANS, MAX_VERTICAL_ANGLE_RADIANS))


func _resolve_raw_angles(target_world_position: Vector3) -> Vector2:
	var origin_basis := eye_origin.global_transform.basis
	var right := (origin_basis * Vector3.RIGHT).normalized()
	var up := (origin_basis * Vector3.UP).normalized()
	var back := (origin_basis * Vector3.BACK).normalized()
	var direction := (target_world_position - eye_origin.global_position).normalized()
	var horizontal_component: float = direction.dot(right)
	var vertical_component: float = direction.dot(up)
	var forward_component: float = -direction.dot(back)
	return Vector2(
		atan2(horizontal_component, forward_component),
		atan2(vertical_component, sqrt(
			(horizontal_component * horizontal_component) + (forward_component * forward_component))))


## Measures the coordinated world gaze rotation applied to both eyes relative to their
## authored neutrals, expressed as eye-origin-frame yaw/pitch angles.
func _measure_gaze_angles() -> Vector2:
	var left_delta := _resolve_world_gaze_delta(left_eye, left_eye_authored_local)
	var right_delta := _resolve_world_gaze_delta(right_eye, right_eye_authored_local)
	if left_delta.angle_to(right_delta) > 0.005:
		SceneUtils.fatal_error_and_quit(
			"EYES_TRANSFORM runner: eyes must rotate as a coordinated pair (divergence %.4f deg)" % [
				rad_to_deg(left_delta.angle_to(right_delta))])
		return Vector2.ZERO
	var origin_rotation := eye_origin.global_transform.basis.get_rotation_quaternion()
	var origin_frame_delta := origin_rotation.inverse() * left_delta * origin_rotation
	var gaze_direction: Vector3 = origin_frame_delta * Vector3(0.0, 0.0, -1.0)
	return Vector2(
		atan2(gaze_direction.x, -gaze_direction.z),
		atan2(gaze_direction.y, sqrt(
			(gaze_direction.x * gaze_direction.x) + (gaze_direction.z * gaze_direction.z))))


func _resolve_world_gaze_delta(eye: Node3D, authored_local: Transform3D) -> Quaternion:
	var parent_rotation: Quaternion = (
		eye.get_parent() as Node3D).global_transform.basis.get_rotation_quaternion()
	var local_delta: Quaternion = (
		eye.transform.basis * authored_local.basis.inverse()).get_rotation_quaternion()
	return parent_rotation * local_delta * parent_rotation.inverse()


func _read_blink_blend_value() -> float:
	var mesh := face_mesh.mesh as ArrayMesh
	for index: int in mesh.get_blend_shape_count():
		if mesh.get_blend_shape_name(index) == "eyeBlinkLeft":
			return face_mesh.get_blend_shape_value(index)
	return -1.0
