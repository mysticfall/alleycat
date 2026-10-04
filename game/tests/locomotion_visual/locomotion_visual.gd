## CTRL-001 TR13 narrow visual acceptance runner: drives the production player composition through
## the real PlayerController XR input path (mock XR events) in the locomotion photobooth, capturing
## time-separated screenshots paired with per-physics-tick trajectory samples (actor transform,
## response model, evaluated graph state, foot-bone world positions).
##
## Usage:
##   godot-mono -d -s --xr-mode off --path game "tests/locomotion_visual/locomotion_visual.gd" -- \
##       --output-dir temp/locomotion-visual [--stage framing|bouts|all]
##
## Never run with --headless: screenshots need a real renderer.
extends SceneTree

const TEST_SCENE_PATH := "res://tests/locomotion_visual/locomotion_visual.tscn"
const ARTIFACT_ROOT := "ctrl001"
const INSTALL_TIMEOUT_SECONDS := 40.0
const SETTLE_TIMEOUT_TICKS := 240
const POST_RESET_TICKS := 20
const DIRECTION_EPSILON := 0.999
const SUSTAINED_SPEED_FLOOR := 0.3
const GLIDING_RATIO_FLAG := 0.8

var _driver: Node
var _photobooth: Photobooth
var _player: Node3D
var _output_dir := ""
var _stage := "all"


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("CTRL-001 locomotion visual runner must run with a renderer, never headless")
		return

	_read_arguments()

	var packed := load(TEST_SCENE_PATH) as PackedScene
	if packed == null:
		SceneUtils.fatal_error_and_quit("CTRL-001 failed to load %s" % TEST_SCENE_PATH)
		return

	_photobooth = packed.instantiate() as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("CTRL-001 photobooth scene root is not a Photobooth")
		return

	_driver = SceneUtils.require_node(_photobooth, ^"LocomotionVisualDriver")

	# The wiring game must claim the Game singleton before the player's PlayerController._Ready runs,
	# so the controller resolves this run's XRManager (mock runtime installed).
	if not bool(_driver.call("Activate")):
		SceneUtils.fatal_error_and_quit("CTRL-001 driver failed to install the wiring game")
		return
	await SceneUtils.wait_frames(self, 5)

	root.add_child(_photobooth)
	await SceneUtils.wait_frames(self, 5)

	if not await _await_player_install():
		return

	_player = SceneUtils.require_node(_photobooth, ^"Subject/Female") as Node3D
	if not bool(_driver.call("ConfigurePlayer", _player)):
		SceneUtils.fatal_error_and_quit("CTRL-001 driver failed to bind to the production player wiring")
		return

	_driver.call("EnsureControllerBound")
	if not bool(_driver.call("IsControllerBound")):
		SceneUtils.fatal_error_and_quit("CTRL-001 PlayerController never bound to the mock XR controllers")
		return

	_apply_booth_presentation_toggles()
	await _await_physics_ticks(20)
	if not _verify_directional_sanity():
		return

	if _stage == "framing" or _stage == "all":
		await _capture_framing()
		if _stage == "framing":
			_finish()

	if _stage == "bouts" or _stage == "all":
		await _run_bouts()

	_finish()


func _finish() -> void:
	_photobooth.queue_free()
	await SceneUtils.wait_frames(self, 2)
	print("CTRL001_LOCOMOTION_VISUAL_RUN_COMPLETE artefact_dir=%s" % _output_dir)
	quit(0)


func _read_arguments() -> void:
	var args := OS.get_cmdline_user_args()
	var output_dir := _find_argument_value(args, "--output-dir")
	_output_dir = output_dir if not output_dir.is_empty() else "temp"
	var stage := _find_argument_value(args, "--stage")
	if not stage.is_empty():
		_stage = stage
	print("CTRL001_RUN_ARGS stage=%s output_dir=%s" % [_stage, _output_dir])


func _find_argument_value(args: PackedStringArray, flag: String) -> String:
	for index in args.size():
		if args[index] == flag and index + 1 < args.size():
			return args[index + 1]
		if args[index].begins_with("%s=" % flag):
			return args[index].trim_prefix("%s=" % flag)
	return ""


func _await_player_install() -> bool:
	var deadline := Time.get_ticks_msec() + int(INSTALL_TIMEOUT_SECONDS * 1000.0)
	while Time.get_ticks_msec() < deadline:
		# The player rig installer binds the locomotion permission sources as its final wiring step.
		if _photobooth.has_node("Subject/Female/Locomotion") \
				and _photobooth.has_node("Subject/Female/PlayerController") \
				and not (_photobooth.get_node("Subject/Female/Locomotion").get("PermissionSourceNodes") as Array).is_empty():
			print("CTRL001_PLAYER_INSTALLED locomotion=%s" % [
				_photobooth.get_node("Subject/Female/Locomotion").get_path()])
			return true
		await SceneUtils.wait_frames(self, 5)

	SceneUtils.fatal_error_and_quit(
		"CTRL-001 production player installer chain did not produce the player wiring within "
		+ "%s s" % INSTALL_TIMEOUT_SECONDS)
	return false


## MANDATORY pre-capture directional sanity check: marker world directions must match their labels,
## camera view axes must match the booth convention, and the actor's rest frame must agree with the
## CTRL-001/TR12 actor frame (forward -Z, right +X). Any contradiction is a setup error.
func _verify_directional_sanity() -> bool:
	var failures := PackedStringArray()
	var origin := Vector3.ZERO

	@warning_ignore("shadowed_global_identifier")
	var markers := {
		"DirN": Vector3(0.0, 0.0, -1.0),
		"DirS": Vector3(0.0, 0.0, 1.0),
		"DirE": Vector3(1.0, 0.0, 0.0),
		"DirW": Vector3(-1.0, 0.0, 0.0),
	}
	for marker_name: String in markers:
		var marker := _photobooth.get_marker(marker_name)
		var actual := (marker.global_position - origin).normalized()
		if actual.dot(markers[marker_name]) < DIRECTION_EPSILON:
			failures.append("marker %s at %s contradicts its world-direction label" % [marker_name, marker.global_position])
		if absf(marker.global_basis.get_euler().y) > 0.02:
			failures.append("marker %s is yawed; lane markers must stay world-aligned" % marker_name)

	var actor_forward := -_player.global_basis.z.normalized()
	var actor_right := _player.global_basis.x.normalized()
	if actor_forward.dot(Vector3(0.0, 0.0, -1.0)) < DIRECTION_EPSILON:
		failures.append("actor rest forward %s is not world -Z (TR12 actor frame)" % actor_forward)
	if actor_right.dot(Vector3(1.0, 0.0, 0.0)) < DIRECTION_EPSILON:
		failures.append("actor rest right %s is not world +X (TR12 actor frame)" % actor_right)

	var rig := SceneUtils.require_node(_player, ^"Female") as Node3D
	var rig_forward := -rig.global_basis.z.normalized()
	print("CTRL001_RIG_BASIS_PROBE rig_forward=%s (production visual rig is 180-degree yawed: expect ~+Z)" % rig_forward)

	var camera_views := {
		"FrontCamera": Vector3(0.0, 0.0, 1.0),
		"BackCamera": Vector3(0.0, 0.0, -1.0),
		"LeftCamera": Vector3(1.0, 0.0, 0.0),
		"RightCamera": Vector3(-1.0, 0.0, 0.0),
		"TopCamera": Vector3(0.0, -1.0, 0.0),
	}
	for camera_name: String in camera_views:
		var rig_cam := _photobooth.get_camera_rig(camera_name)
		var view := -rig_cam.global_basis.z.normalized()
		if view.dot(camera_views[camera_name]) < DIRECTION_EPSILON:
			failures.append("camera %s views %s, expected %s" % [camera_name, view, camera_views[camera_name]])

	var top := _photobooth.get_camera_rig("TopCamera")
	if top.global_basis.y.normalized().dot(Vector3(0.0, 0.0, -1.0)) < DIRECTION_EPSILON:
		failures.append("TopCamera screen-up is not -Z (N); plan-view orientation convention broken")

	if failures.size() > 0:
		SceneUtils.fatal_error_and_quit(
			"CTRL001 DIRECTIONAL SETUP ERROR - refusing to capture with a broken setup:\n  - "
			+ "\n  - ".join(failures))
		return false

	print("CTRL001_DIRECTIONAL_SANITY_OK markers=N/S/E/W aligned, actor forward=-Z right=+X, cameras verified")
	return true


## Booth-only presentation wiring: the photobooth has no XR hand/head tracking, so the unbound player
## VRIK would pin the arms and head to their authored static IK targets (a shoulder-height T hold).
## Production degrades the same way when hand providers report zero influence - PlayerVRIK deactivates
## the head/hand modifier groups - so this reproduces that documented degradation for the gait captures.
## Locomotion behaviour and scheduling are untouched.
func _apply_booth_presentation_toggles() -> void:
	var vrik := _player.get_node_or_null("VRIK") as Node3D
	if vrik == null:
		SceneUtils.fatal_error_and_quit("CTRL-001 expected the installed player VRIK node")
		return

	vrik.process_mode = Node.PROCESS_MODE_DISABLED
	for group_property in ["HeadModifierGroup", "RightHandModifierGroup", "LeftHandModifierGroup"]:
		for modifier_value in vrik.get(group_property):
			var modifier := modifier_value as SkeletonModifier3D
			if modifier != null:
				modifier.active = false
				modifier.influence = 0.0
	print("CTRL001_BOOTH_PRESENTATION_TOGGLES vrik=disabled head/hand modifier groups deactivated (no XR tracking in booth)")

	# The fixed close-up gait camera cannot follow travel; hide it so bout captures stay meaningful.
	_photobooth.get_camera_rig("GaitCamera").visible = false


func _capture_framing() -> void:
	for camera_name: String in ["FrontCamera", "BackCamera", "LeftCamera", "RightCamera", "TopCamera", "GaitCamera"]:
		var rig := _photobooth.get_camera_rig(camera_name)
		await rig.capture_screenshot("%s/framing/%s_framing.jpg" % [ARTIFACT_ROOT, camera_name])

	await _photobooth.capture_screenshots("%s/framing/all_cameras_actor_at_rest" % ARTIFACT_ROOT)
	print("CTRL001_FRAMING_CAPTURED")


func _await_physics_ticks(ticks: int) -> void:
	for _tick in ticks:
		await physics_frame


func _await_settled() -> bool:
	for _tick in SETTLE_TIMEOUT_TICKS:
		if bool(_driver.call("IsResponseSettled")):
			return true
		await physics_frame
	return bool(_driver.call("IsResponseSettled"))


## Bout specifications. Movement input convention: (0, +1) forward toward actor facing (-Z at rest),
## (0, -1) backward, (+1, 0) actor right (+X), (-1, 0) actor left. Rotation (+1, 0) turns actor right.
func _bout_specs() -> Array[Dictionary]:
	return [
		{
			"name": "forward_hold",
			"input": Vector2(0.0, 1.0),
			"phases": [
				{"mv": Vector2(0.0, 1.0), "rot": Vector2.ZERO, "ticks": 110, "cps": [16, 40, 64, 88]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 55], "release": true},
			],
		},
		{
			"name": "backward_hold",
			"input": Vector2(0.0, -1.0),
			"phases": [
				{"mv": Vector2(0.0, -1.0), "rot": Vector2.ZERO, "ticks": 110, "cps": [16, 40, 64, 88]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 55], "release": true},
			],
		},
		{
			"name": "left_hold",
			"input": Vector2(-1.0, 0.0),
			"phases": [
				{"mv": Vector2(-1.0, 0.0), "rot": Vector2.ZERO, "ticks": 110, "cps": [16, 40, 64, 88]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 55], "release": true},
			],
		},
		{
			"name": "right_hold",
			"input": Vector2(1.0, 0.0),
			"phases": [
				{"mv": Vector2(1.0, 0.0), "rot": Vector2.ZERO, "ticks": 110, "cps": [16, 40, 64, 88]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 55], "release": true},
			],
		},
		{
			"name": "low_speed_nudge",
			"input": Vector2(0.0, 0.25),
			"phases": [
				{"mv": Vector2(0.0, 0.25), "rot": Vector2.ZERO, "ticks": 150, "cps": [30, 70, 110, 140]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 45], "release": true},
			],
		},
		{
			"name": "turn_while_walking",
			"input": Vector2(0.0, 1.0),
			"phases": [
				{"mv": Vector2(0.0, 1.0), "rot": Vector2(1.0, 0.0), "ticks": 110, "cps": [16, 40, 64, 88]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 55], "release": true},
			],
		},
		{
			"name": "reversal",
			"input": Vector2(0.0, -1.0),
			"phases": [
				{"mv": Vector2(0.0, 1.0), "rot": Vector2.ZERO, "ticks": 70, "cps": [40]},
				{"mv": Vector2(0.0, -1.0), "rot": Vector2.ZERO, "ticks": 110, "cps": [10, 34, 58, 90]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 90, "cps": [16, 55], "release": true},
			],
		},
		{
			"name": "stationary_turn",
			"input": Vector2.ZERO,
			"phases": [
				{"mv": Vector2.ZERO, "rot": Vector2(1.0, 0.0), "ticks": 120, "cps": [20, 55, 90, 115]},
				{"mv": Vector2.ZERO, "rot": Vector2.ZERO, "ticks": 60, "cps": [20], "release": true},
			],
		},
	]


func _run_bouts() -> void:
	var run_report := {
		"scene": TEST_SCENE_PATH,
		"stage": "bouts",
		"captured_at": Time.get_datetime_string_from_system(),
		"conventions": {
			"movement_input": "(0,+1)=actor forward (-Z at rest heading 0), (0,-1)=backward, (+1,0)=right (+X), (-1,0)=left",
			"actor_frame": "forward -Z, right +X (CTRL-001 TR12)",
			"markers": "N=-Z, S=+Z, E=+X, W=-X",
		},
		"bouts": [],
	}

	for spec in _bout_specs():
		var report := await _run_bout(spec)
		run_report["bouts"].append(report)

	_write_json(run_report, "%s/trajectory_summary.json" % ARTIFACT_ROOT)
	print("CTRL001_TRAJECTORY_JSON_WRITTEN %s/trajectory_summary.json" % ARTIFACT_ROOT)


func _run_bout(spec: Dictionary) -> Dictionary:
	if not await _await_settled():
		print("CTRL001_BOUT_SETTLE_TIMEOUT bout=%s continuing after timeout" % spec.name)
	_driver.call("ResetActor")
	await _await_physics_ticks(POST_RESET_TICKS)

	var bout_start: Dictionary = _driver.call("CaptureSample")
	var heading0: float = bout_start.yaw
	var expected_dir := _semantic_input_to_world(heading0, spec.input).normalized()

	_driver.call("StartCollecting")
	var checkpoints := []
	var checkpoint_index := 0

	for phase: Dictionary in spec.phases:
		_driver.call("PushMovement", phase.mv)
		_driver.call("PushRotation", phase.rot)
		var captured := 0
		var tick := 0
		while tick < phase.ticks:
			await physics_frame
			tick += 1
			if captured < phase.cps.size() and tick >= phase.cps[captured]:
				checkpoint_index += 1
				var index_before: int = _driver.call("GetCollectedCount")
				var file_prefix := "%s/%s/cp%02d" % [ARTIFACT_ROOT, spec.name, checkpoint_index]
				await _photobooth.capture_screenshots(file_prefix)
				var index_after: int = _driver.call("GetCollectedCount")
				checkpoints.append({
					"checkpoint": checkpoint_index,
					"phase_ticks": tick,
					"released": bool(phase.get("release", false)),
					"movement": [phase.mv.x, phase.mv.y],
					"rotation": [phase.rot.x, phase.rot.y],
					"file_prefix": file_prefix,
					"sample_index_at_capture_start": index_before,
					"sample_index_after_capture": index_after,
					"closest_sample": _driver.call("CaptureSample"),
				})
				captured += 1

	_driver.call("ReleaseSticks")
	await _await_settled()
	var samples: Array = _driver.call("StopCollecting")

	var report := {
		"name": spec.name,
		"input": [spec.input.x, spec.input.y],
		"heading_at_start": heading0,
		"expected_world_direction": [expected_dir.x, expected_dir.y, expected_dir.z],
		"bout_start_position": bout_start.pos,
		"checkpoints": checkpoints,
		"samples": samples,
		"metrics": _compute_metrics(samples, expected_dir),
	}

	var metrics: Dictionary = report.metrics
	print((
		"CTRL001_BOUT_SUMMARY bout=%s net_dot=%.3f net_len=%.2fm travel_ticks=%d mean_rate=%.2f "
		+ "supported_foot_ratio_mean=%.3f p90=%.3f pose_progress=%.2fs settled_end=%s") % [
			spec.name,
			metrics.net_direction_dot,
			metrics.net_displacement_length,
			metrics.travel_sample_count,
			metrics.mean_travel_rate,
			metrics.supported_foot_ratio_mean,
			metrics.supported_foot_ratio_p90,
			metrics.pose_progress_seconds,
			metrics.ends_settled,
		])
	return report


## Expected world direction for a semantic movement input under the actor's heading, derived by
## trigonometry exactly like the movement-frame integration tests (never from runtime bases).
func _semantic_input_to_world(heading_radians: float, input: Vector2) -> Vector3:
	return Vector3(
		(input.x * cos(heading_radians)) - (input.y * sin(heading_radians)),
		0.0,
		(-input.x * sin(heading_radians)) - (input.y * cos(heading_radians)))


func _compute_metrics(samples: Array, expected_dir: Vector3) -> Dictionary:
	var metrics := {
		"net_direction_dot": 0.0,
		"net_displacement_length": 0.0,
		"travel_sample_count": 0,
		"mean_travel_rate": 0.0,
		"supported_foot_ratio_mean": 0.0,
		"supported_foot_ratio_p90": 0.0,
		"supported_foot_ratio_max": 0.0,
		"pose_progress_seconds": 0.0,
		"post_settle_drift": 0.0,
		"ends_settled": false,
		"suspect_gliding": false,
		"heading_delta_radians": 0.0,
	}

	if samples.is_empty():
		return metrics

	var first: Dictionary = samples[0]
	var last_input_index := samples.size() - 1
	for index in range(samples.size() - 1, -1, -1):
		var movement: Array = samples[index].get("resp_v", [0.0, 0.0])
		if Vector2(movement[0], movement[1]).length() > 0.01:
			last_input_index = index
			break

	var last_input: Dictionary = samples[last_input_index]
	var net := Vector3(last_input.pos[0], 0.0, last_input.pos[2]) - Vector3(first.pos[0], 0.0, first.pos[2])
	metrics.net_displacement_length = net.length()
	if net.length() > 0.0001:
		metrics.net_direction_dot = net.normalized().dot(expected_dir)
	var accumulated_yaw := 0.0
	for index in range(1, samples.size()):
		accumulated_yaw += _angle_delta(samples[index - 1].yaw, samples[index].yaw)
	metrics.heading_delta_radians = accumulated_yaw

	# Sustained-travel window: evaluated travel state with realised body speed.
	var ratios := []
	var rates := []
	var min_play := 1.0e9
	var max_play := -1.0e9
	for index in range(1, samples.size()):
		var sample: Dictionary = samples[index]
		if String(sample.node) != "PlayerTravel" or float(sample.speed) < SUSTAINED_SPEED_FLOOR:
			continue
		rates.append(float(sample.rate))
		min_play = minf(min_play, float(sample.play_t))
		max_play = maxf(max_play, float(sample.play_t))

		var previous: Dictionary = samples[index - 1]
		var body_step := Vector3(
			sample.pos[0] - previous.pos[0], 0.0, sample.pos[2] - previous.pos[2]).length()
		var left_step := Vector3(
			sample.foot_l[0] - previous.foot_l[0],
			sample.foot_l[1] - previous.foot_l[1],
			sample.foot_l[2] - previous.foot_l[2]).length()
		var right_step := Vector3(
			sample.foot_r[0] - previous.foot_r[0],
			sample.foot_r[1] - previous.foot_r[1],
			sample.foot_r[2] - previous.foot_r[2]).length()
		if body_step > 0.001:
			ratios.append(minf(left_step, right_step) / body_step)

	metrics.travel_sample_count = rates.size()
	if not rates.is_empty():
		metrics.mean_travel_rate = rates.reduce(func(sum, value): return sum + value, 0.0) / rates.size()
	if not ratios.is_empty():
		ratios.sort()
		metrics.supported_foot_ratio_mean = ratios.reduce(func(sum, value): return sum + value, 0.0) / ratios.size()
		metrics.supported_foot_ratio_p90 = ratios[int(0.9 * (ratios.size() - 1))]
		metrics.supported_foot_ratio_max = ratios[ratios.size() - 1]
		metrics.suspect_gliding = metrics.supported_foot_ratio_mean > GLIDING_RATIO_FLAG
	if max_play > min_play:
		metrics.pose_progress_seconds = max_play - min_play

	# Motion after settling: find the last sample still responding to input, then measure displacement
	# from the first settled sample after it to the final sample (creep after the stop would show here).
	metrics.ends_settled = bool(samples[samples.size() - 1].settled)
	var last_active_index := -1
	for index in samples.size():
		if not bool(samples[index].settled):
			last_active_index = index
	if last_active_index >= 0 and last_active_index + 1 < samples.size():
		var settled_sample: Dictionary = samples[last_active_index + 1]
		var final_sample: Dictionary = samples[samples.size() - 1]
		metrics.post_settle_drift = Vector3(
			final_sample.pos[0] - settled_sample.pos[0], 0.0,
			final_sample.pos[2] - settled_sample.pos[2]).length()

	return metrics


func _angle_delta(from_radians: float, to_radians: float) -> float:
	return wrapf(to_radians - from_radians, -PI, PI)





func _write_json(data: Dictionary, relative_path: String) -> void:
	var absolute_dir := (_output_dir if _output_dir.is_absolute_path() or _output_dir.begins_with("res://")
		else ProjectSettings.globalize_path("res://").path_join(_output_dir)).simplify_path()
	DirAccess.make_dir_recursive_absolute(absolute_dir.path_join(relative_path.get_base_dir()))
	var file := FileAccess.open(absolute_dir.path_join(relative_path), FileAccess.WRITE)
	if file == null:
		SceneUtils.fatal_error_and_quit("CTRL-001 failed to open trajectory JSON for writing: %s" % relative_path)
		return
	file.store_string(JSON.stringify(data, "\t"))
	file.close()
