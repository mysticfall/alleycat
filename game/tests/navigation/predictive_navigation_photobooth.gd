extends Node3D

const OUTPUT_ROOT := "NAV-001/predictive_navigation"
const TRAIL_INTERVAL := 3
const CAMERA_SIZE := 10.0
const CAMERA_TARGET := Vector3(0.0, 0.0, -1.25)
const TERMINAL_CAMERA_DISTANCE := 2.65
const TERMINAL_CAMERA_SIDE_OFFSET := 0.85
const TERMINAL_CAMERA_HEIGHT := 1.45
const TERMINAL_CAMERA_FOV := 38.0
const TERMINAL_CORRECTION_MIN_YAW_DEGREES := 10.0
# NAV-001 requires smooth, bounded release but does not prescribe a 60-tick release deadline.
# This is a safety cap before the separate 60-frame terminal-stability observation window.
const MAX_NEUTRALISATION_OBSERVATION_FRAMES := 120
const ROUTE_COLOR := Color(1.0, 0.82, 0.12)
const REPLACEMENT_ROUTE_COLOR := Color(0.25, 1.0, 0.35)
const OLD_ROUTE_COLOR := Color(0.55, 0.55, 0.55)
const TRAIL_COLOR := Color(0.05, 0.75, 1.0)
const ANTICIPATION_COLOR := Color(1.0, 0.35, 0.05)
const DEVIATION_COLOR := Color(1.0, 0.1, 0.75)
const ENDPOINT_COLOR := Color(0.65, 0.25, 1.0)
const SHARP_TURN_DIAGNOSTIC_PATH := "res://temp/NAV-001/sharp_turn_diagnostic.csv"
const EXACT_SHARP_TURN_DIAGNOSTIC_PATH := "res://temp/NAV-001/exact_sharp_turn_diagnostic.jsonl"
const EXACT_DESTINATION_POSITION := Vector3(-0.23867352, 0.0, -2.7152236)
const EXACT_ROUTE_YAW := 0.08767663
const EXACT_DESTINATION_FORWARD := Vector3(-0.08756434, 0.0, -0.99615884)
const TURN_IN_PLACE_RIGHT_ANIMATION_PATH := "res://assets/characters/reference/female/animations/locomotion/clips/mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9.res"
const MOVEMENT_BLEND_PARAMETER := &"parameters/States/Walking/Locomotion/Movement/blend_position"
const TURN_BLEND_PARAMETER := &"parameters/States/Walking/Locomotion/blend_position"
const STATE_PLAYBACK_PARAMETER := &"parameters/States/playback"
const REQUIRED_IMAGES := [
	"framing/route_fixture.jpg",
	"corner_anticipation.jpg",
	"corner_progression.jpg",
	"deviation_injection.jpg",
	"deviation_recovery.jpg",
	"replacement_before.jpg",
	"replacement_transition.jpg",
	"replacement_progression.jpg",
	"terminal_stable.jpg",
	"short_endpoint_correction.jpg",
	"short_terminal_stable.jpg",
]

var _actor: Node3D
var _navigation: Node
var _runtime: Node3D
var _trail: PackedVector3Array = []
var _previous_movement := Vector2.ZERO
var _previous_turn := 0.0
var _maximum_control_delta := 0.0
var _reversal_count := 0
var _reference_visuals: Node3D
var _annotation_visuals: Node3D
var _telemetry_label: Label
var _animation_tree: AnimationTree


func _ready() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("Predictive-navigation screenshots require a renderer")
		return
	_clear_stale_output()
	_runtime = get_node(^"NavigationRuntime") as Node3D
	_runtime.set_process(false)
	_runtime.set_physics_process(false)
	_actor = _runtime.get_node(^"NavigationTestNpc") as Node3D
	_navigation = _actor.get_node(^"Navigation")
	var runtime_camera := _runtime.get_node(^"Camera3D") as Camera3D
	runtime_camera.current = false
	var camera := get_node(^"VerificationCamera") as Camera3D
	camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	camera.size = CAMERA_SIZE
	camera.global_position = CAMERA_TARGET + Vector3.UP * 12.0
	camera.look_at(CAMERA_TARGET, Vector3.FORWARD)
	camera.make_current()
	var camera_forward := -camera.global_basis.z.normalized()
	var screen_right := camera.global_basis.x.normalized()
	var screen_up := camera.global_basis.y.normalized()
	if camera.projection != Camera3D.PROJECTION_ORTHOGONAL \
			or camera.global_position.y < 10.0 \
			or camera_forward.dot(Vector3.DOWN) < 0.999 \
			or screen_right.dot(Vector3.RIGHT) < 0.999 \
			or screen_up.dot(Vector3.FORWARD) < 0.999:
		SceneUtils.fatal_error_and_quit("Verification camera framing is not a downward route overview")
		return
	_create_visual_layers()
	_extend_rear_navigation_corridor()
	_draw_axis_and_legend()
	_create_telemetry_overlay()
	print("PREDICTIVE_NAV_CAMERA position=%s forward=%s screen_right=%s screen_up=%s projection=orthogonal size=%.2f semantic_forward=-Z" % [camera.global_position, camera_forward, screen_right, screen_up, camera.size])
	await SceneUtils.wait_frames(get_tree(), 20)
	_animation_tree = _actor.get_node(^"AnimationTree") as AnimationTree
	if _animation_tree == null:
		SceneUtils.fatal_error_and_quit("Sharp-turn diagnostics require the installed character AnimationTree")
		return
	if "--exact-sharp-turn-diagnostic" in OS.get_cmdline_user_args():
		await _run_exact_sharp_turn_diagnostic("--capture-abrupt" in OS.get_cmdline_user_args())
		get_tree().quit(0)
		return
	if "--sharp-turn-diagnostic" in OS.get_cmdline_user_args():
		await _run_sharp_turn_diagnostic()
		get_tree().quit(0)
		return
	camera.make_current()
	if get_viewport().get_camera_3d() != camera:
		SceneUtils.fatal_error_and_quit("VerificationCamera lost current-camera ownership before capture")
		return
	if "--short-only" in OS.get_cmdline_user_args():
		await _run_short_terminal_facing()
		get_tree().quit(0)
		return
	if "--rear-correction-visual" in OS.get_cmdline_user_args():
		await _run_rear_correction_visuals()
		get_tree().quit(0)
		return
	await _run_corner_and_deviation()
	await _run_straight()
	await _run_destination_replacement()
	await _run_short_terminal_facing()
	_verify_required_images()
	get_tree().quit(0)

func _extend_rear_navigation_corridor() -> void:
	# This photobooth-only extension keeps the authored navigation-test obstacle geometry unchanged
	# while making the contract's 10 m rear target reachable for visual verification.
	var region := _runtime.get_node(^"NavigationRegion3D") as NavigationRegion3D
	var source := region.navigation_mesh
	if source == null:
		SceneUtils.fatal_error_and_quit("Rear-correction fixture requires NavigationRegion3D mesh")
		return
	var mesh := source.duplicate(true) as NavigationMesh
	var vertices := mesh.vertices
	var base := vertices.size()
	vertices.append_array(PackedVector3Array([
		Vector3(5.8, 0.0, 10.2), Vector3(-5.8, 0.0, 10.2),
	]))
	mesh.vertices = vertices
	# Reuse the authored z=5.8 edge (indices 7 and 63) so the new polygon is connected.
	mesh.add_polygon(PackedInt32Array([7, 63, base, base + 1]))
	region.navigation_mesh = mesh
	NavigationServer3D.region_set_navigation_mesh(region.get_rid(), mesh)
	print("REAR_CORRECTION_NAV_MESH corridor=+Z5.8_to_10.2")

func _run_rear_correction_visuals() -> void:
	var scenarios := [
		{"name": "rear_1m_backward", "destination": Vector3(0.0, 0.0, 1.0), "expect_back": true},
		{"name": "rear_side_1m_diagonal", "destination": Vector3(0.5, 0.0, 0.85), "expect_back": true},
		{"name": "rear_1_25m_release", "destination": Vector3(0.0, 0.0, 1.25), "expect_back": false},
	]
	for scenario: Dictionary in scenarios:
		await _reset_scenario(Transform3D.IDENTITY)
		var destination := Transform3D.IDENTITY
		destination.origin = scenario.destination
		_require_accepted(destination, String(scenario.name))
		var movement := Vector2.ZERO
		var turn := 0.0
		for frame in range(20):
			await get_tree().physics_frame
			movement = _navigation.get("LastPlannedMovement")
			turn = float(_navigation.get("LastPlannedTurn"))
			if (bool(scenario.expect_back) and movement.y < -0.01) or (not bool(scenario.expect_back) and absf(turn) > 0.01):
				break
		if bool(scenario.expect_back) and movement.y >= -0.01:
			SceneUtils.fatal_error_and_quit("%s did not publish backward correction movement=%s turn=%.3f remaining=%.3f" % [scenario.name, movement, turn, float(_navigation.get("LastRemainingDistance"))])
			return
		if not bool(scenario.expect_back) and movement.y < -0.01:
			SceneUtils.fatal_error_and_quit("%s retained short backward correction" % scenario.name)
			return
		await _capture("rear_correction/%s" % scenario.name, "%s  movement=(%.2f, %.2f) turn=%.2f" % [scenario.name, movement.x, movement.y, turn])
	await _reset_scenario(Transform3D.IDENTITY)
	var distant := Transform3D.IDENTITY
	distant.origin = Vector3(0.0, 0.0, 10.0)
	_require_accepted(distant, "rear_10m")
	await get_tree().physics_frame
	var initial_movement: Vector2 = _navigation.get("LastPlannedMovement")
	var initial_turn := float(_navigation.get("LastPlannedTurn"))
	if initial_movement.length() > 0.01 or absf(initial_turn) < 0.01:
		SceneUtils.fatal_error_and_quit("rear_10m did not begin with stationary pivot intent")
		return
	await _capture("rear_correction/rear_10m_pivot", "rear_10m pivot  movement=%s turn=%.2f" % [initial_movement, initial_turn])
	for frame in range(360):
		await get_tree().physics_frame
		_sample(frame, "rear_10m")
		var movement: Vector2 = _navigation.get("LastPlannedMovement")
		if movement.y > 0.15:
			await _capture("rear_correction/rear_10m_forward", "rear_10m forward after pivot  movement=%s" % movement)
			print("REAR_CORRECTION_VISUAL_PASS")
			return
	SceneUtils.fatal_error_and_quit("rear_10m did not transition from pivot to forward travel")


func _run_sharp_turn_diagnostic() -> void:
	var absolute_path := ProjectSettings.globalize_path(SHARP_TURN_DIAGNOSTIC_PATH)
	DirAccess.make_dir_recursive_absolute(absolute_path.get_base_dir())
	var output := FileAccess.open(SHARP_TURN_DIAGNOSTIC_PATH, FileAccess.WRITE)
	if output == null:
		SceneUtils.fatal_error_and_quit("Unable to open sharp-turn diagnostic telemetry output")
		return
	output.store_csv_line(PackedStringArray([
		"scenario", "frame", "position_x", "position_y", "position_z", "actor_yaw",
		"actor_yaw_delta", "root_yaw_delta", "accumulator_yaw", "accumulator_yaw_delta",
		"state", "play_position", "play_length", "loop_boundary", "branch", "movement_x",
		"movement_y", "turn_x", "turn_y", "destination_generation", "route_revision",
		"planner_movement_x", "planner_movement_y", "planner_turn", "position_complete",
		"facing_complete"
	]))
	var scenarios := [
		{"name": "behind_left_2m", "destination": Vector3(-0.2, 0.0, 2.0)},
		{"name": "behind_centre_2_5m", "destination": Vector3(0.0, 0.0, 2.5)},
		{"name": "behind_right_3m", "destination": Vector3(0.2, 0.0, 2.99)},
	]
	for scenario: Dictionary in scenarios:
		var scenario_name: String = scenario["name"]
		var scenario_destination: Vector3 = scenario["destination"]
		await _run_sharp_turn_case(output, scenario_name, scenario_destination)
	output.close()
	print("SHARP_TURN_DIAGNOSTIC_PASS scenarios=%d telemetry=%s" % [scenarios.size(), SHARP_TURN_DIAGNOSTIC_PATH])


func _run_exact_sharp_turn_diagnostic(capture_abrupt: bool) -> void:
	await _reset_scenario(Transform3D.IDENTITY)
	var visual_rig := _actor.get_node(^"Female") as Node3D
	var skeleton := _actor.get_node(^"Female/GeneralSkeleton") as Skeleton3D
	if visual_rig == null or skeleton == null:
		SceneUtils.fatal_error_and_quit("Exact sharp-turn diagnostics require Female/GeneralSkeleton production wiring")
		return
	var root_bone_index := skeleton.find_bone("Root")
	var hips_bone_index := skeleton.find_bone("Hips")
	if root_bone_index < 0 or hips_bone_index < 0:
		SceneUtils.fatal_error_and_quit("Exact sharp-turn diagnostics require Root and Hips bones")
		return
	var playback := _animation_tree.get(STATE_PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	if playback == null:
		SceneUtils.fatal_error_and_quit("Exact sharp-turn diagnostics require locomotion state playback")
		return
	var absolute_path := ProjectSettings.globalize_path(EXACT_SHARP_TURN_DIAGNOSTIC_PATH)
	DirAccess.make_dir_recursive_absolute(absolute_path.get_base_dir())
	var output := FileAccess.open(EXACT_SHARP_TURN_DIAGNOSTIC_PATH, FileAccess.WRITE)
	if output == null:
		SceneUtils.fatal_error_and_quit("Unable to open exact sharp-turn diagnostic telemetry output")
		return
	var destination_basis := Basis.looking_at(EXACT_DESTINATION_FORWARD.normalized(), Vector3.UP)
	var destination := Transform3D(destination_basis, EXACT_DESTINATION_POSITION)
	var actor_yaw := _horizontal_yaw(-_actor.global_basis.z)
	var route_yaw := _horizontal_yaw(destination.origin - _actor.global_position)
	if _actor.global_position.length() > 0.001 or absf(actor_yaw) > 0.001 or absf(route_yaw - EXACT_ROUTE_YAW) > 0.0001:
		SceneUtils.fatal_error_and_quit("Exact forward-left fixture must start at origin/yaw zero with the reported route yaw")
		return
	_require_accepted(destination, "exact_user_request")
	await _prepare_route_visual(destination, ROUTE_COLOR, "REPORTED FORWARD-LEFT ROUTE", false)
	var requested_distance := destination.origin.length()
	if requested_distance <= 1.25:
		SceneUtils.fatal_error_and_quit("Exact reported forward-left destination must remain a production navigation route")
		return
	_print_turn_animation_track_diagnostic()
	if capture_abrupt:
		var camera := get_node(^"VerificationCamera") as Camera3D
		camera.projection = Camera3D.PROJECTION_PERSPECTIVE
		camera.global_position = Vector3(3.2, 2.6, -3.2)
		camera.look_at(Vector3(0.0, 0.9, 0.5), Vector3.UP)
		camera.make_current()
		_remove_directory_contents(ProjectSettings.globalize_path("res://temp/NAV-001/exact_sharp_turn_after"))
		print("EXACT_SHARP_TURN_CAPTURE_CAMERA position=%s forward=%s target=%s" % [camera.global_position, -camera.global_basis.z, Vector3(0.0, 0.9, 0.5)])
	var context := {
		"transforms": {},
		"root_rotation_accumulator": _animation_tree.get_root_motion_rotation_accumulator(),
		"root_position_accumulator": _animation_tree.get_root_motion_position_accumulator(),
		"play_position": playback.get_current_play_position(),
		"planner_turn_sign": 0,
	}
	var previous_render_image: Image = null
	var abrupt_frames := 0
	var capture_count := 0
	var maximum_visual_angle_delta := 0.0
	var maximum_visual_position_delta := 0.0
	var maximum_actor_angle_delta := 0.0
	var sampled_physics_frames := 0
	var forward_left_seen := false
	var previous_movement := Vector2.ZERO
	var movement_reversals := 0
	var previous_distance := requested_distance
	var maximum_distance_increase := 0.0
	print("EXACT_FORWARD_LEFT_SETUP destination_position=%s route_yaw=%.8f actor_position=%s actor_yaw=%.8f destination_forward=%s rig_local_yaw=%.8f skeleton=%s root_bone=Root[%d] root_parent=%d hips_bone=Hips[%d] hips_parent=%s[%d]" % [destination.origin, route_yaw, _actor.global_position, actor_yaw, -destination.basis.z, _horizontal_yaw(-visual_rig.basis.z), skeleton.get_path(), root_bone_index, skeleton.get_bone_parent(root_bone_index), hips_bone_index, skeleton.get_bone_name(skeleton.get_bone_parent(hips_bone_index)), skeleton.get_bone_parent(hips_bone_index)])
	for frame in range(720):
		await get_tree().physics_frame
		_sample(frame, "exact_user_request")
		sampled_physics_frames = frame + 1
		var physics_sample := _sample_exact_sharp_turn(output, frame, "physics", visual_rig, skeleton, root_bone_index, hips_bone_index, playback, context)
		if frame == 30:
			await _capture("exact_user_request/forward_left", "REPORTED FORWARD-LEFT ROUTE\nrequested %.3f m | route yaw %+.6f | movement %s\nActor advances forward-left along the yellow route; no rear branch" % [requested_distance, route_yaw, _navigation.get("LastPlannedMovement")])
		var planned_movement: Vector2 = _navigation.get("LastPlannedMovement")
		if planned_movement.y < -0.01:
			SceneUtils.fatal_error_and_quit("Exact forward-left route entered the prohibited rear locomotion branch")
			return
		if planned_movement.x < -0.01 and planned_movement.y > 0.01:
			forward_left_seen = true
		if planned_movement.length() > 0.03 and previous_movement.length() > 0.03 and planned_movement.dot(previous_movement) < -0.02:
			movement_reversals += 1
		previous_movement = planned_movement if planned_movement.length() > 0.03 else previous_movement
		var current_distance := _actor.global_position.distance_to(EXACT_DESTINATION_POSITION)
		maximum_distance_increase = maxf(maximum_distance_increase, current_distance - previous_distance)
		previous_distance = current_distance
		await get_tree().process_frame
		var render_sample := _sample_exact_sharp_turn(output, frame, "render", visual_rig, skeleton, root_bone_index, hips_bone_index, playback, context)
		var abrupt := bool(physics_sample.abrupt_visual) or bool(render_sample.abrupt_visual)
		if abrupt:
			abrupt_frames += 1
		maximum_visual_angle_delta = maxf(maximum_visual_angle_delta, maxf(float(physics_sample.maximum_visual_angle_delta), float(render_sample.maximum_visual_angle_delta)))
		maximum_visual_position_delta = maxf(maximum_visual_position_delta, maxf(float(physics_sample.maximum_visual_position_delta), float(render_sample.maximum_visual_position_delta)))
		maximum_actor_angle_delta = maxf(maximum_actor_angle_delta, maxf(float(physics_sample.actor_global_angular_delta), float(render_sample.actor_global_angular_delta)))
		if capture_abrupt:
			await RenderingServer.frame_post_draw
			var current_render_image := get_viewport().get_texture().get_image()
			var capture_event := abrupt or bool(render_sample.loop_boundary) or bool(render_sample.branch_transition)
			if capture_event and capture_count < 6:
				capture_count += 1
				var capture_root := "res://temp/NAV-001/exact_sharp_turn_after"
				DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(capture_root))
				var event_slug := "loop" if bool(render_sample.loop_boundary) else "branch"
				if previous_render_image != null:
					previous_render_image.save_jpg("%s/frame_%04d_%s_before.jpg" % [capture_root, frame, event_slug], 0.95)
				current_render_image.save_jpg("%s/frame_%04d_%s_after.jpg" % [capture_root, frame, event_slug], 0.95)
			previous_render_image = current_render_image
		if not bool(_navigation.get("IsNavigationRunning")):
			break
	output.close()
	var final_distance := _actor.global_position.distance_to(EXACT_DESTINATION_POSITION)
	var final_yaw_error := _planar_angle_between(-_actor.global_basis.z, EXACT_DESTINATION_FORWARD)
	var completed := not bool(_navigation.get("IsNavigationRunning"))
	print("EXACT_FORWARD_LEFT_SUMMARY frames=%d abrupt_frames=%d max_visual_quaternion_delta=%.8f max_visual_position_delta=%.8f max_actor_quaternion_delta=%.8f max_distance_increase=%.8f movement_reversals=%d forward_left_seen=%s final_actor_position=%s final_distance=%.8f final_yaw_error=%.8f completed=%s progress=%.6f telemetry=%s" % [sampled_physics_frames, abrupt_frames, maximum_visual_angle_delta, maximum_visual_position_delta, maximum_actor_angle_delta, maximum_distance_increase, movement_reversals, str(forward_left_seen), _actor.global_position, final_distance, final_yaw_error, str(completed), float(_navigation.get("LastRouteProgress")), EXACT_SHARP_TURN_DIAGNOSTIC_PATH])
	# A looping stationary pivot restarts its authored pose phase, so Hips can legitimately advance more than a
	# walking frame at its loop boundary. Root-owned actor motion remains the continuity authority.
	if abrupt_frames != 0 or maximum_actor_angle_delta > 0.05:
		SceneUtils.fatal_error_and_quit("Exact sharp-turn regression exceeded bounded per-frame rotation")
		return
	if not forward_left_seen or movement_reversals != 0 or maximum_distance_increase > 0.015:
		SceneUtils.fatal_error_and_quit("Exact forward-left regression did not retain a monotonic non-reversing forward-left correction")
		return
	if not completed or final_distance > 0.08 or final_yaw_error > deg_to_rad(3.0) or float(_navigation.get("LastRouteProgress")) < 1.0:
		SceneUtils.fatal_error_and_quit("Exact forward-left regression did not converge through Root-owned motion")
		return
	await _capture("exact_user_request/completed", "REPORTED FORWARD-LEFT ROUTE — COMPLETED\ndistance %.4f m | yaw %.4f rad | route progress %.3f m\nActor reached the destination through forward-left root motion without reversal" % [final_distance, final_yaw_error, float(_navigation.get("LastRouteProgress"))])
	if "--exact-only" not in OS.get_cmdline_user_args():
		await _run_near_pi_regressions(skeleton, hips_bone_index)


func _run_near_pi_regressions(skeleton: Skeleton3D, hips_bone_index: int) -> void:
	var scenarios := [
		{"name": "near_pi_left_epsilon", "position": Vector3(-0.002, 0.0, EXACT_DESTINATION_POSITION.z), "forward": Vector3(-0.001, 0.0, 0.9999995)},
		{"name": "near_pi_right_epsilon", "position": Vector3(0.002, 0.0, EXACT_DESTINATION_POSITION.z), "forward": Vector3(0.001, 0.0, 0.9999995)},
	]
	for scenario: Dictionary in scenarios:
		await _reset_scenario(Transform3D.IDENTITY)
		var destination_position: Vector3 = scenario.position
		var destination_forward: Vector3 = scenario.forward
		var destination := Transform3D(Basis.looking_at(destination_forward.normalized(), Vector3.UP), destination_position)
		_require_accepted(destination, scenario.name)
		await _prepare_route_visual(destination, ROUTE_COLOR, "NEAR-π REAR ROUTE — %s" % String(scenario.name).to_upper(), false)
		var previous_hips := skeleton.get_bone_pose(hips_bone_index).basis.get_rotation_quaternion().normalized()
		var previous_actor := _actor.global_transform
		var previous_turn_sign := 0
		var turn_reversals := 0
		var maximum_hips_delta := 0.0
		var maximum_actor_angle_delta := 0.0
		var maximum_actor_position_delta := 0.0
		var sampled_frames := 0
		for frame in range(1200):
			await get_tree().physics_frame
			await get_tree().process_frame
			sampled_frames = frame + 1
			if frame == 30:
				await _capture("near_pi/%s_pivot" % scenario.name, "NEAR-π REAR — %s\nFinite pivot starts on the signed side of the yellow route" % scenario.name)
			var hips := skeleton.get_bone_pose(hips_bone_index).basis.get_rotation_quaternion().normalized()
			maximum_hips_delta = maxf(maximum_hips_delta, previous_hips.angle_to(hips))
			var actor_transform := _actor.global_transform
			maximum_actor_angle_delta = maxf(maximum_actor_angle_delta, previous_actor.basis.get_rotation_quaternion().angle_to(actor_transform.basis.get_rotation_quaternion()))
			maximum_actor_position_delta = maxf(maximum_actor_position_delta, previous_actor.origin.distance_to(actor_transform.origin))
			var planner_turn := float(_navigation.get("LastPlannedTurn"))
			var turn_sign := int(signf(planner_turn)) if absf(planner_turn) > 0.03 else 0
			if turn_sign != 0 and previous_turn_sign != 0 and turn_sign != previous_turn_sign:
				turn_reversals += 1
			if turn_sign != 0:
				previous_turn_sign = turn_sign
			previous_hips = hips
			previous_actor = actor_transform
			if not bool(_navigation.get("IsNavigationRunning")):
				break
		var final_distance := _actor.global_position.distance_to(destination_position)
		var final_yaw_error := _planar_angle_between(-_actor.global_basis.z, destination_forward)
		var completed := not bool(_navigation.get("IsNavigationRunning"))
		print("NEAR_PI_REGRESSION scenario=%s frames=%d max_hips_delta=%.8f max_actor_angle_delta=%.8f max_actor_position_delta=%.8f turn_reversals=%d final_distance=%.8f final_yaw_error=%.8f completed=%s progress=%.6f planner_movement=%s planner_turn=%.6f position_complete=%s facing_complete=%s" % [scenario.name, sampled_frames, maximum_hips_delta, maximum_actor_angle_delta, maximum_actor_position_delta, turn_reversals, final_distance, final_yaw_error, str(completed), float(_navigation.get("LastRouteProgress")), _navigation.get("LastPlannedMovement"), float(_navigation.get("LastPlannedTurn")), str(_navigation.get("LastPositionComplete")), str(_navigation.get("LastFacingComplete"))])
		if maximum_actor_angle_delta > 0.05 or maximum_actor_position_delta > 0.05 or turn_reversals > 1:
			SceneUtils.fatal_error_and_quit("Near-pi sharp-turn regression exceeded continuity bounds: %s" % scenario.name)
			return
		if not completed or final_distance > 0.08 or final_yaw_error > deg_to_rad(3.0) or float(_navigation.get("LastRouteProgress")) < 1.0:
			SceneUtils.fatal_error_and_quit("Near-pi sharp-turn regression did not converge: %s" % scenario.name)
			return
		await _capture("near_pi/%s_completed" % scenario.name, "NEAR-π REAR — %s COMPLETED\ndistance %.4f m | yaw %.4f rad | progress %.3f m\nActor reached the signed rear destination through finite pivots and route-directed travel" % [scenario.name, final_distance, final_yaw_error, float(_navigation.get("LastRouteProgress"))])


func _planar_angle_between(left: Vector3, right: Vector3) -> float:
	var left_planar := Vector2(left.x, left.z).normalized()
	var right_planar := Vector2(right.x, right.z).normalized()
	return acos(clampf(left_planar.dot(right_planar), -1.0, 1.0))


func _sample_exact_sharp_turn(
		output: FileAccess,
		frame: int,
		stage: String,
		visual_rig: Node3D,
		skeleton: Skeleton3D,
		root_bone_index: int,
		hips_bone_index: int,
		playback: AnimationNodeStateMachinePlayback,
		context: Dictionary
	) -> Dictionary:
	var record := {"frame": frame, "stage": stage}
	var previous_transforms: Dictionary = context.transforms
	_append_transform_sample(record, "actor_global", _actor.global_transform, previous_transforms)
	_append_transform_sample(record, "actor_local", _actor.transform, previous_transforms)
	_append_transform_sample(record, "rig_global", visual_rig.global_transform, previous_transforms)
	_append_transform_sample(record, "rig_local", visual_rig.transform, previous_transforms)
	_append_transform_sample(record, "skeleton_global", skeleton.global_transform, previous_transforms)
	_append_transform_sample(record, "skeleton_local", skeleton.transform, previous_transforms)
	var root_local_pose := skeleton.get_bone_pose(root_bone_index)
	var root_skeleton_pose := skeleton.get_bone_global_pose(root_bone_index)
	var hips_local_pose := skeleton.get_bone_pose(hips_bone_index)
	var hips_skeleton_pose := skeleton.get_bone_global_pose(hips_bone_index)
	_append_transform_sample(record, "root_bone_local_pose", root_local_pose, previous_transforms)
	_append_transform_sample(record, "root_bone_skeleton_pose", root_skeleton_pose, previous_transforms)
	_append_transform_sample(record, "root_bone_world_pose", skeleton.global_transform * root_skeleton_pose, previous_transforms)
	_append_transform_sample(record, "hips_bone_local_pose", hips_local_pose, previous_transforms)
	_append_transform_sample(record, "hips_bone_skeleton_pose", hips_skeleton_pose, previous_transforms)
	_append_transform_sample(record, "hips_bone_world_pose", skeleton.global_transform * hips_skeleton_pose, previous_transforms)
	var root_position_delta := _animation_tree.get_root_motion_position()
	var root_rotation_delta := _animation_tree.get_root_motion_rotation()
	var root_position_accumulator := _animation_tree.get_root_motion_position_accumulator()
	var root_rotation_accumulator := _animation_tree.get_root_motion_rotation_accumulator()
	record.root_motion_position_delta = _vector_array(root_position_delta)
	record.root_motion_rotation_delta = _quaternion_array(root_rotation_delta)
	record.root_motion_rotation_delta_angle = root_rotation_delta.get_angle()
	record.root_motion_rotation_delta_yaw = root_rotation_delta.get_euler().y
	record.root_motion_position_accumulator = _vector_array(root_position_accumulator)
	record.root_motion_rotation_accumulator = _quaternion_array(root_rotation_accumulator)
	record.root_motion_position_accumulator_delta = root_position_accumulator.distance_to(context.root_position_accumulator)
	record.root_motion_rotation_accumulator_delta = context.root_rotation_accumulator.angle_to(root_rotation_accumulator)
	context.root_position_accumulator = root_position_accumulator
	context.root_rotation_accumulator = root_rotation_accumulator
	var movement_blend := _animation_tree.get(MOVEMENT_BLEND_PARAMETER) as Vector2
	var turn_blend := _animation_tree.get(TURN_BLEND_PARAMETER) as Vector2
	record.movement_blend = [movement_blend.x, movement_blend.y]
	record.turn_blend = [turn_blend.x, turn_blend.y]
	record.branch = _classify_locomotion_branch(turn_blend)
	var previous_branch := String(context.get("branch", ""))
	record.branch_transition = not previous_branch.is_empty() and previous_branch != record.branch
	context.branch = record.branch
	record.animation_state = str(playback.get_current_node())
	record.animation_play_position = playback.get_current_play_position()
	record.animation_play_length = playback.get_current_length()
	record.loop_boundary = record.animation_state == "Walking" and float(record.animation_play_position) + 0.25 < float(context.play_position)
	context.play_position = record.animation_play_position
	var planner_movement: Vector2 = _navigation.get("LastPlannedMovement")
	var planner_turn := float(_navigation.get("LastPlannedTurn"))
	var planner_turn_sign := int(signf(planner_turn)) if absf(planner_turn) > 0.03 else 0
	record.planner_movement = [planner_movement.x, planner_movement.y]
	record.planner_turn = planner_turn
	record.planner_turn_reversal = planner_turn_sign != 0 and int(context.planner_turn_sign) != 0 and planner_turn_sign != int(context.planner_turn_sign)
	if planner_turn_sign != 0:
		context.planner_turn_sign = planner_turn_sign
	record.destination_generation = int(_navigation.get("LastDestinationRequestGeneration"))
	record.route_revision = int(_navigation.get("LastRouteRevision"))
	record.route_progress = float(_navigation.get("LastRouteProgress"))
	record.cross_track_error = float(_navigation.get("LastCrossTrackError"))
	record.remaining_distance = float(_navigation.get("LastRemainingDistance"))
	record.path_index = int(_navigation.get("CurrentPathIndex"))
	var current_path: PackedVector3Array = _navigation.get("CurrentPath")
	record.path_point_count = current_path.size()
	if not current_path.is_empty():
		var sampled_path_index := clampi(int(record.path_index), 0, current_path.size() - 1)
		var next_path_point := current_path[sampled_path_index]
		record.next_path_point = _vector_array(next_path_point)
		record.actor_to_next_path_point = _vector_array(next_path_point - _actor.global_position)
	var visual_angle_keys := [
		"rig_global_angular_delta", "skeleton_global_angular_delta", "root_bone_world_pose_angular_delta",
		"hips_bone_world_pose_angular_delta", "root_bone_local_pose_angular_delta", "hips_bone_local_pose_angular_delta",
	]
	var visual_position_keys := [
		"rig_global_position_delta", "skeleton_global_position_delta", "root_bone_world_pose_position_delta",
		"hips_bone_world_pose_position_delta", "root_bone_local_pose_position_delta", "hips_bone_local_pose_position_delta",
	]
	var maximum_visual_angle_delta := 0.0
	var maximum_visual_position_delta := 0.0
	for key: String in visual_angle_keys:
		maximum_visual_angle_delta = maxf(maximum_visual_angle_delta, float(record[key]))
	for key: String in visual_position_keys:
		maximum_visual_position_delta = maxf(maximum_visual_position_delta, float(record[key]))
	record.maximum_visual_angle_delta = maximum_visual_angle_delta
	record.maximum_visual_position_delta = maximum_visual_position_delta
	record.abrupt_visual = maximum_visual_angle_delta > deg_to_rad(45.0) or maximum_visual_position_delta > 0.25
	output.store_line(JSON.stringify(record))
	if bool(record.abrupt_visual) or bool(record.loop_boundary):
		print("EXACT_SHARP_TURN_EVENT frame=%d stage=%s abrupt=%s loop=%s branch=%s phase=%.5f/%.5f actor_angle=%.5f rig_angle=%.5f skeleton_angle=%.5f root_local_angle=%.5f hips_local_angle=%.5f root_world_angle=%.5f hips_world_angle=%.5f root_delta_angle=%.5f accumulator_delta=%.5f planner_turn=%.5f generation=%d revision=%d path_index=%d" % [frame, stage, str(record.abrupt_visual), str(record.loop_boundary), record.branch, record.animation_play_position, record.animation_play_length, record.actor_global_angular_delta, record.rig_global_angular_delta, record.skeleton_global_angular_delta, record.root_bone_local_pose_angular_delta, record.hips_bone_local_pose_angular_delta, record.root_bone_world_pose_angular_delta, record.hips_bone_world_pose_angular_delta, record.root_motion_rotation_delta_angle, record.root_motion_rotation_accumulator_delta, record.planner_turn, record.destination_generation, record.route_revision, record.path_index])
	return record


func _append_transform_sample(record: Dictionary, prefix: String, sample_transform: Transform3D, previous_transforms: Dictionary) -> void:
	var sample_rotation := sample_transform.basis.get_rotation_quaternion().normalized()
	var yaw := _horizontal_yaw(-sample_transform.basis.z)
	var previous: Transform3D = previous_transforms.get(prefix, sample_transform)
	var previous_rotation := previous.basis.get_rotation_quaternion().normalized()
	record["%s_position" % prefix] = _vector_array(sample_transform.origin)
	record["%s_rotation" % prefix] = _quaternion_array(sample_rotation)
	record["%s_yaw" % prefix] = yaw
	record["%s_position_delta" % prefix] = sample_transform.origin.distance_to(previous.origin)
	record["%s_angular_delta" % prefix] = previous_rotation.angle_to(sample_rotation)
	record["%s_yaw_delta" % prefix] = wrapf(yaw - _horizontal_yaw(-previous.basis.z), -PI, PI)
	previous_transforms[prefix] = sample_transform


func _print_turn_animation_track_diagnostic() -> void:
	var animation := ResourceLoader.load(TURN_IN_PLACE_RIGHT_ANIMATION_PATH) as Animation
	if animation == null:
		SceneUtils.fatal_error_and_quit("Unable to load turn-in-place-right animation for track diagnostics")
		return
	for track_index in range(animation.get_track_count()):
		var track_path := str(animation.track_get_path(track_index))
		if not track_path.contains(":Root") and not track_path.contains(":Hips"):
			continue
		var key_count := animation.track_get_key_count(track_index)
		var first_time := animation.track_get_key_time(track_index, 0) if key_count > 0 else -1.0
		var last_time := animation.track_get_key_time(track_index, key_count - 1) if key_count > 0 else -1.0
		var first_value: Variant = animation.track_get_key_value(track_index, 0) if key_count > 0 else null
		var last_value: Variant = animation.track_get_key_value(track_index, key_count - 1) if key_count > 0 else null
		var angular_span := -1.0
		if first_value is Quaternion and last_value is Quaternion:
			angular_span = (first_value as Quaternion).angle_to(last_value as Quaternion)
		print("EXACT_SHARP_TURN_TRACK resource=%s track=%d path=%s type=%d keys=%d first_time=%.6f last_time=%.6f angular_span=%.6f first=%s last=%s" % [TURN_IN_PLACE_RIGHT_ANIMATION_PATH, track_index, track_path, animation.track_get_type(track_index), key_count, first_time, last_time, angular_span, first_value, last_value])


func _vector_array(value: Vector3) -> Array:
	return [value.x, value.y, value.z]


func _quaternion_array(value: Quaternion) -> Array:
	return [value.x, value.y, value.z, value.w]


func _run_sharp_turn_case(output: FileAccess, scenario: String, destination_position: Vector3) -> void:
	await _reset_scenario(Transform3D.IDENTITY)
	var destination_direction := destination_position.normalized()
	var destination := Transform3D(Basis.looking_at(destination_direction, Vector3.UP), destination_position)
	_require_accepted(destination, scenario)
	var playback := _animation_tree.get(STATE_PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	if playback == null:
		SceneUtils.fatal_error_and_quit("Sharp-turn diagnostics require locomotion state playback")
		return
	var previous_position := _actor.global_position
	var previous_actor_yaw := _horizontal_yaw(-_actor.global_basis.z)
	var previous_accumulator := _animation_tree.get_root_motion_rotation_accumulator()
	var previous_play_position := playback.get_current_play_position()
	var previous_branch := ""
	var previous_turn_sign := 0
	var previous_revision := int(_navigation.get("LastRouteRevision"))
	var root_spikes := 0
	var actor_spikes := 0
	var loop_boundaries := 0
	var branch_transitions := 0
	var turn_reversals := 0
	var route_revisions := 0
	var maximum_actor_yaw_delta := 0.0
	var maximum_root_yaw_delta := 0.0
	var maximum_position_delta := 0.0
	var sampled_frames := 0
	for frame in range(480):
		await get_tree().physics_frame
		sampled_frames = frame + 1
		var actor_position := _actor.global_position
		var actor_yaw := _horizontal_yaw(-_actor.global_basis.z)
		var actor_yaw_delta := wrapf(actor_yaw - previous_actor_yaw, -PI, PI)
		var position_delta := actor_position.distance_to(previous_position)
		var root_yaw_delta := _animation_tree.get_root_motion_rotation().get_euler().y
		var accumulator := _animation_tree.get_root_motion_rotation_accumulator()
		var accumulator_delta := (previous_accumulator.inverse() * accumulator).get_euler().y
		var movement_blend := _animation_tree.get(MOVEMENT_BLEND_PARAMETER) as Vector2
		var turn_blend := _animation_tree.get(TURN_BLEND_PARAMETER) as Vector2
		var branch := _classify_locomotion_branch(turn_blend)
		var state := str(playback.get_current_node())
		var play_position := playback.get_current_play_position()
		var play_length := playback.get_current_length()
		var loop_boundary := state == "Walking" and play_position + 0.25 < previous_play_position
		var planner_movement: Vector2 = _navigation.get("LastPlannedMovement")
		var planner_turn := float(_navigation.get("LastPlannedTurn"))
		var turn_sign := int(signf(planner_turn)) if absf(planner_turn) > 0.03 else 0
		var generation := int(_navigation.get("LastDestinationRequestGeneration"))
		var revision := int(_navigation.get("LastRouteRevision"))
		var root_spike := absf(root_yaw_delta) > deg_to_rad(30.0) or absf(accumulator_delta) > deg_to_rad(30.0)
		var actor_spike := absf(actor_yaw_delta) > deg_to_rad(30.0) or position_delta > 0.35
		if root_spike:
			root_spikes += 1
		if actor_spike:
			actor_spikes += 1
		if loop_boundary:
			loop_boundaries += 1
		if not previous_branch.is_empty() and branch != previous_branch:
			branch_transitions += 1
		if turn_sign != 0 and previous_turn_sign != 0 and turn_sign != previous_turn_sign:
			turn_reversals += 1
		if revision != previous_revision:
			route_revisions += 1
		maximum_actor_yaw_delta = maxf(maximum_actor_yaw_delta, absf(actor_yaw_delta))
		maximum_root_yaw_delta = maxf(maximum_root_yaw_delta, maxf(absf(root_yaw_delta), absf(accumulator_delta)))
		maximum_position_delta = maxf(maximum_position_delta, position_delta)
		output.store_csv_line(PackedStringArray([
			scenario, str(frame), str(actor_position.x), str(actor_position.y), str(actor_position.z),
			str(actor_yaw), str(actor_yaw_delta), str(root_yaw_delta), str(_horizontal_yaw(-(Basis(accumulator).z))),
			str(accumulator_delta), state, str(play_position), str(play_length), str(loop_boundary), branch,
			str(movement_blend.x), str(movement_blend.y), str(turn_blend.x), str(turn_blend.y),
			str(generation), str(revision), str(planner_movement.x), str(planner_movement.y),
			str(planner_turn), str(_navigation.get("LastPositionComplete")), str(_navigation.get("LastFacingComplete"))
		]))
		if root_spike or actor_spike or loop_boundary or branch != previous_branch or revision != previous_revision:
			print("SHARP_TURN_EVENT scenario=%s frame=%d actor_pos=%s position_delta=%.5f actor_yaw_delta=%.5f root_yaw_delta=%.5f accumulator_delta=%.5f state=%s phase=%.5f/%.5f loop=%s branch=%s movement=%s turn_blend=%s planner_movement=%s planner_turn=%.5f generation=%d revision=%d" % [scenario, frame, actor_position, position_delta, actor_yaw_delta, root_yaw_delta, accumulator_delta, state, play_position, play_length, str(loop_boundary), branch, movement_blend, turn_blend, planner_movement, planner_turn, generation, revision])
		previous_position = actor_position
		previous_actor_yaw = actor_yaw
		previous_accumulator = accumulator
		previous_play_position = play_position
		previous_branch = branch
		if turn_sign != 0:
			previous_turn_sign = turn_sign
		previous_revision = revision
		if not bool(_navigation.get("IsNavigationRunning")):
			break
	print("SHARP_TURN_SUMMARY scenario=%s destination=%s distance=%.3f frames=%d max_actor_yaw_delta=%.5f max_root_yaw_delta=%.5f max_position_delta=%.5f root_spikes=%d actor_spikes=%d loop_boundaries=%d branch_transitions=%d turn_reversals=%d route_revision_changes=%d final_position=%s final_forward=%s" % [scenario, destination_position, destination_position.length(), sampled_frames, maximum_actor_yaw_delta, maximum_root_yaw_delta, maximum_position_delta, root_spikes, actor_spikes, loop_boundaries, branch_transitions, turn_reversals, route_revisions, _actor.global_position, -_actor.global_basis.z])


func _classify_locomotion_branch(turn_blend: Vector2) -> String:
	if turn_blend.y > 0.15:
		return "movement" if absf(turn_blend.x) <= 0.15 else "moving_turn"
	if absf(turn_blend.x) > 0.15:
		return "turn_in_place_left" if turn_blend.x < 0.0 else "turn_in_place_right"
	return "idle"


func _horizontal_yaw(direction: Vector3) -> float:
	direction.y = 0.0
	if direction.length_squared() <= 0.000001:
		return 0.0
	direction = direction.normalized()
	return atan2(-direction.x, -direction.z)


func _run_corner_and_deviation() -> void:
	await _reset_scenario(Transform3D.IDENTITY)
	var destination := Transform3D(Basis(Vector3.UP, -PI * 0.5), Vector3(-4.0, 0.0, 2.0))
	_require_accepted(destination, "corner")
	var route_info: Dictionary = await _prepare_route_visual(destination, ROUTE_COLOR, "ACCEPTED 90° / S ROUTE", true)
	if route_info.corner_angle_degrees < 70.0 or route_info.corner_angle_degrees > 110.0:
		SceneUtils.fatal_error_and_quit("Primary route corner is not a readable 90-degree fixture: %.2f°" % route_info.corner_angle_degrees)
		return
	await _capture(
		"framing/route_fixture",
		"FIXTURE / CAMERA SANITY\nORTHOGRAPHIC TOP: screen right = +X, screen up = -Z semantic forward\nYellow = accepted route | cyan = actual root-motion trail | orange = anticipation region\nPrimary corner %.1f° | endpoint region radius 0.65 m" % route_info.corner_angle_degrees
	)
	var anticipation_frame := -1
	var deviation_frame := -1
	var progression_captured := false
	var endpoint_entry_error := -1.0
	var previous_endpoint_error := -1.0
	var maximum_endpoint_error_increase := 0.0
	var endpoint_error_violations := 0
	var endpoint_reversals := 0
	var previous_endpoint_movement := Vector2.ZERO
	var route_corner_reversals := -1
	for frame in range(1080):
		await get_tree().physics_frame
		_sample(frame, "corner")
		if route_corner_reversals < 0:
			var sampled_path: PackedVector3Array = _navigation.get("CurrentPath")
			if sampled_path.size() >= 3:
				route_corner_reversals = _count_corner_reversals(sampled_path)
				print("PREDICTIVE_NAV_ROUTE_SHAPE scenario=corner points=%d signed_corner_reversals=%d" % [sampled_path.size(), route_corner_reversals])
		var endpoint_error := _actor.global_position.distance_to(destination.origin)
		if endpoint_entry_error < 0.0 and endpoint_error <= 0.65 and float(_navigation.get("LastRemainingDistance")) <= 0.65:
			endpoint_entry_error = endpoint_error
			previous_endpoint_error = endpoint_error
		if endpoint_entry_error >= 0.0:
			var error_increase := endpoint_error - previous_endpoint_error
			maximum_endpoint_error_increase = maxf(maximum_endpoint_error_increase, error_increase)
			if error_increase > 0.015:
				endpoint_error_violations += 1
			var endpoint_movement: Vector2 = _navigation.get("LastPlannedMovement")
			if endpoint_movement.dot(previous_endpoint_movement) < -0.02:
				endpoint_reversals += 1
			if endpoint_movement.length() > 0.03:
				previous_endpoint_movement = endpoint_movement
			previous_endpoint_error = endpoint_error
		if anticipation_frame < 0 and absf(float(_navigation.get("LastPlannedTurn"))) > 0.08 and float(_navigation.get("LastRouteProgress")) > 0.4 and float(_navigation.get("LastRemainingDistance")) > 1.0:
			anticipation_frame = frame
			await _capture(
				"corner_anticipation",
				"TURN ANTICIPATION — BEFORE PRIMARY CORNER\nprogress %.3f m | corner %.1f° | turn %+.3f | max Δcontrol %.4f\nActor/trail remain before orange corner marker" % [float(_navigation.get("LastRouteProgress")), route_info.corner_angle_degrees, float(_navigation.get("LastPlannedTurn")), _maximum_control_delta]
			)
		if deviation_frame < 0 and float(_navigation.get("LastRouteProgress")) > 1.0:
			var deviation_start := _actor.global_position
			_actor.global_position += Vector3(0.12, 0.0, 0.0)
			_draw_deviation_marker(deviation_start, _actor.global_position)
			deviation_frame = frame
			print("PREDICTIVE_NAV_DEVIATION injected=0.12 frame=%d" % frame)
			await _capture(
				"deviation_injection",
				"DEVIATION INJECTION\nMagenta arrow = +X direction at ×6 display scale; actual displacement = 0.120 m\ncross-track %.4f m | Δcontrol %.4f | reversals %d" % [float(_navigation.get("LastCrossTrackError")), _maximum_control_delta, _reversal_count]
			)
		if deviation_frame >= 0 and frame == deviation_frame + 90:
			await _capture(
				"deviation_recovery",
				"GRADUAL DEVIATION RECOVERY — 90 PHYSICS FRAMES\ncross-track %.4f m | max Δcontrol %.4f | reversals %d\nCyan trail should converge continuously towards yellow route" % [float(_navigation.get("LastCrossTrackError")), _maximum_control_delta, _reversal_count]
			)
		if not progression_captured and float(_navigation.get("LastRouteProgress")) > 4.0:
			progression_captured = true
			await _capture(
				"corner_progression",
				"CONTINUOUS MULTI-CORNER PROGRESSION\nprogress %.3f m | cross-track %.4f m | max Δcontrol %.4f | reversals %d\nNo disconnected trail segment or opposite correction pulse" % [float(_navigation.get("LastRouteProgress")), float(_navigation.get("LastCrossTrackError")), _maximum_control_delta, _reversal_count]
			)
		if not bool(_navigation.get("IsNavigationRunning")):
			break
	if anticipation_frame < 0:
		SceneUtils.fatal_error_and_quit("No bounded turn anticipation was observed before the corner completed")
		return
	if bool(_navigation.get("IsNavigationRunning")):
		SceneUtils.fatal_error_and_quit("Corner route did not reach a controlled completed stop within the bounded run")
		return
	var stability := await _assert_stable_completion(destination, "corner")
	if endpoint_entry_error < 0.0 or endpoint_entry_error > 0.65 or endpoint_error_violations > 0 or maximum_endpoint_error_increase > 0.015 or endpoint_reversals > 1 or _maximum_control_delta > 0.06:
		SceneUtils.fatal_error_and_quit("Corner endpoint correction violated convergence bounds")
		return
	await _capture(
		"terminal_stable",
		"TERMINAL STOP / FACING — 60 STABLE PHYSICS FRAMES\ncompleted %s | distance %.4f m | yaw %.4f rad | drift %.5f m\nmax Δcontrol %.4f | endpoint reversals %d" % [str(stability.completed), stability.distance, stability.yaw, stability.drift, _maximum_control_delta, endpoint_reversals]
	)
	print("PREDICTIVE_NAV_CONVERGENCE scenario=corner endpoint_entry_error=%.4f max_error_increase=%.4f error_violations=%d max_control_delta=%.4f endpoint_reversals=%d final_distance=%.4f final_yaw=%.4f neutralisation_frames=%d stability_drift=%.5f stability_frames=60 completed=%s" % [endpoint_entry_error, maximum_endpoint_error_increase, endpoint_error_violations, _maximum_control_delta, endpoint_reversals, stability.distance, stability.yaw, stability.neutralisation_frames, stability.drift, str(stability.completed)])
	print("PREDICTIVE_NAV_SCENARIO corner anticipation_frame=%d progress=%.3f cross_track=%.3f remaining=%.3f max_control_delta=%.3f reversals=%d finished=%s" % [anticipation_frame, float(_navigation.get("LastRouteProgress")), float(_navigation.get("LastCrossTrackError")), float(_navigation.get("LastRemainingDistance")), _maximum_control_delta, _reversal_count, str(not bool(_navigation.get("IsNavigationRunning")))])


func _run_straight() -> void:
	await _reset_scenario(Transform3D.IDENTITY)
	var destination := Transform3D(Basis.IDENTITY, Vector3(0.0, 0.0, -2.0))
	_require_accepted(destination, "straight")
	var maximum_lateral_error := 0.0
	for frame in range(480):
		await get_tree().physics_frame
		_sample(frame, "straight")
		maximum_lateral_error = maxf(maximum_lateral_error, absf(_actor.global_position.x))
		if not bool(_navigation.get("IsNavigationRunning")):
			break
	if bool(_navigation.get("IsNavigationRunning")):
		SceneUtils.fatal_error_and_quit("Straight root-motion route did not complete")
		return
	var stability := await _assert_stable_completion(destination, "straight")
	if maximum_lateral_error > 0.08 or _maximum_control_delta > 0.06:
		SceneUtils.fatal_error_and_quit("Straight route exceeded lateral or command-continuity bounds")
		return
	print("PREDICTIVE_NAV_CONVERGENCE scenario=straight max_lateral_error=%.4f max_control_delta=%.4f final_distance=%.4f final_yaw=%.4f neutralisation_frames=%d stability_drift=%.5f stability_frames=60 completed=%s" % [maximum_lateral_error, _maximum_control_delta, stability.distance, stability.yaw, stability.neutralisation_frames, stability.drift, str(stability.completed)])


func _run_destination_replacement() -> void:
	await _reset_scenario(Transform3D.IDENTITY)
	var initial_destination := Transform3D(Basis.IDENTITY, Vector3(0.0, 0.0, -4.5))
	_require_accepted(initial_destination, "replacement_initial")
	await _prepare_route_visual(initial_destination, ROUTE_COLOR, "ORIGINAL ACCEPTED ROUTE", false)
	for frame in range(90):
		await get_tree().physics_frame
		_sample(frame, "replacement_initial")
	await _capture(
		"replacement_before",
		"DESTINATION REPLACEMENT — BEFORE REQUEST\nYellow = original accepted route | cyan = root-motion trail\nmovement %s | turn %+.3f" % [_navigation.get("LastPlannedMovement"), float(_navigation.get("LastPlannedTurn"))]
	)
	var before_movement: Vector2 = _navigation.get("LastPlannedMovement")
	var before_turn := float(_navigation.get("LastPlannedTurn"))
	var replacement_point := _actor.global_position
	var original_path: PackedVector3Array = _navigation.get("CurrentPath")
	var replacement_destination := Transform3D(Basis(Vector3.UP, -PI * 0.5), Vector3(4.0, 0.0, -1.0))
	_require_accepted(replacement_destination, "replacement_new")
	await get_tree().physics_frame
	_clear_reference_visuals()
	_draw_route_geometry(original_path, initial_destination, OLD_ROUTE_COLOR, "OLD ROUTE", false)
	var replacement_path: PackedVector3Array = _navigation.get("CurrentPath")
	_draw_route_geometry(replacement_path, replacement_destination, REPLACEMENT_ROUTE_COLOR, "REPLACEMENT ROUTE", false)
	_draw_point_marker(_annotation_visuals, replacement_point, DEVIATION_COLOR, "DESTINATION REPLACED HERE")
	var after_movement: Vector2 = _navigation.get("LastPlannedMovement")
	var after_turn := float(_navigation.get("LastPlannedTurn"))
	var replacement_delta := maxf(before_movement.distance_to(after_movement), absf(before_turn - after_turn))
	print("PREDICTIVE_NAV_REPLACEMENT before_move=%s before_turn=%.3f after_move=%s after_turn=%.3f delta=%.3f" % [before_movement, before_turn, after_movement, after_turn, replacement_delta])
	if replacement_delta > 0.2:
		SceneUtils.fatal_error_and_quit("Destination replacement jumped controls beyond the bounded transition")
		return
	await _capture(
		"replacement_transition",
		"DESTINATION REPLACEMENT — FIRST PHYSICS TICK\nGrey = old route | green = accepted replacement | magenta = replacement point\nΔcontrol %.4f (limit 0.20) | before %s / %+.3f | after %s / %+.3f" % [replacement_delta, before_movement, before_turn, after_movement, after_turn]
	)
	for frame in range(180):
		await get_tree().physics_frame
		_sample(frame, "replacement_new")
	await _capture(
		"replacement_progression",
		"DESTINATION REPLACEMENT — CONTINUOUS PROGRESSION\nCyan trail remains connected through magenta replacement point\nmax Δcontrol %.4f | reversals %d" % [_maximum_control_delta, _reversal_count]
	)


func _run_short_terminal_facing() -> void:
	await _reset_scenario(Transform3D.IDENTITY)
	var destination := Transform3D(Basis(Vector3.UP, -PI * 0.5), Vector3(0.35, 0.0, -0.15))
	_require_accepted(destination, "short_terminal")
	await _prepare_route_visual(destination, ROUTE_COLOR, "SHORT ENDPOINT ROUTE", false)
	var turn_reversals := 0
	var previous_sign := 0
	var correction_captured := false
	for frame in range(480):
		await get_tree().physics_frame
		_sample(frame, "short_terminal")
		var turn := float(_navigation.get("LastPlannedTurn"))
		var turn_sign: int = int(signf(turn)) if absf(turn) > 0.03 else 0
		if turn_sign != 0 and previous_sign != 0 and turn_sign != previous_sign:
			turn_reversals += 1
		if turn_sign != 0:
			previous_sign = turn_sign
		var endpoint_distance := _actor.global_position.distance_to(destination.origin)
		var endpoint_movement: Vector2 = _navigation.get("LastPlannedMovement")
		if not correction_captured and endpoint_distance < 0.65 and absf(endpoint_movement.x) > 0.1:
			var correction_yaw := _destination_yaw_error(destination)
			if correction_yaw < deg_to_rad(TERMINAL_CORRECTION_MIN_YAW_DEGREES):
				continue
			correction_captured = true
			_configure_terminal_facing_camera(destination, "correction")
			await _capture(
				"short_endpoint_correction",
				"TERMINAL CORRECTION — CLOSE THREE-QUARTER VIEW\ndistance %.4f m | yaw outstanding %.1f° | side %+.3f | turn %+.3f\nBody heading visibly differs from the purple requested-facing arrow" % [endpoint_distance, rad_to_deg(correction_yaw), endpoint_movement.x, turn]
			)
		if not bool(_navigation.get("IsNavigationRunning")):
			break
	print("PREDICTIVE_NAV_TERMINAL finished=%s turn_reversals=%d movement=%s turn=%.3f" % [str(not bool(_navigation.get("IsNavigationRunning"))), turn_reversals, _navigation.get("LastPlannedMovement"), float(_navigation.get("LastPlannedTurn"))])
	if not correction_captured:
		SceneUtils.fatal_error_and_quit("Short endpoint correction checkpoint was not captured")
	elif turn_reversals > 1:
		SceneUtils.fatal_error_and_quit("Terminal facing oscillated across semantic turn directions")
	elif bool(_navigation.get("IsNavigationRunning")):
		SceneUtils.fatal_error_and_quit("Short terminal correction did not complete within the bounded run")
	else:
		var stability := await _assert_stable_completion(destination, "short_terminal")
		_configure_terminal_facing_camera(destination, "stable")
		await _capture(
			"short_terminal_stable",
			"ARRIVED + FACING — CLOSE THREE-QUARTER VIEW\n60 stable physics frames | completed %s | distance %.4f m | yaw %.2f° | drift %.5f m\nBody heading aligns with the purple requested-facing arrow; controls are neutral" % [str(stability.completed), stability.distance, rad_to_deg(stability.yaw), stability.drift]
		)
		print("PREDICTIVE_NAV_CONVERGENCE scenario=short_terminal max_control_delta=%.4f turn_reversals=%d final_distance=%.4f final_yaw=%.4f neutralisation_frames=%d stability_drift=%.5f stability_frames=60 completed=%s" % [_maximum_control_delta, turn_reversals, stability.distance, stability.yaw, stability.neutralisation_frames, stability.drift, str(stability.completed)])


func _reset_scenario(actor_transform: Transform3D) -> void:
	_navigation.call("ClearDestination")
	for _frame in range(60):
		var settling_movement: Vector2 = _navigation.get("LastPlannedMovement")
		var settling_turn := absf(float(_navigation.get("LastPlannedTurn")))
		if settling_movement.length() <= 0.001 and settling_turn <= 0.001:
			break
		await get_tree().physics_frame
	_navigation.set_physics_process(false)
	_actor.global_transform = actor_transform
	_actor.set("velocity", Vector3.ZERO)
	_navigation.set_physics_process(true)
	_trail = PackedVector3Array([_actor.global_position])
	_previous_movement = Vector2.ZERO
	_previous_turn = 0.0
	_maximum_control_delta = 0.0
	_reversal_count = 0
	_clear_trail_visual()
	_clear_reference_visuals()
	_telemetry_label.text = ""


func _require_accepted(destination: Transform3D, scenario: String) -> void:
	var result := int(_navigation.call("SetDestination", destination))
	if result != 0:
		SceneUtils.fatal_error_and_quit("Scenario %s destination was not accepted: %d" % [scenario, result])


func _assert_stable_completion(destination: Transform3D, scenario: String) -> Dictionary:
	var neutralisation_frames := 0
	var neutralised := false
	while not neutralised and neutralisation_frames < MAX_NEUTRALISATION_OBSERVATION_FRAMES:
		var settling_movement: Vector2 = _navigation.get("LastPlannedMovement")
		var settling_turn := absf(float(_navigation.get("LastPlannedTurn")))
		if settling_movement.length() <= 0.01 and settling_turn <= 0.01:
			neutralised = true
			break
		await get_tree().physics_frame
		# Observe after LocomotiveNavigation's physics callback has published this tick's command.
		await get_tree().process_frame
		neutralisation_frames += 1
	# `physics_frame` resumes before physics callbacks, so release is observed through a
	# bounded safety window before the distinct 60-frame stability assertion begins.
	if not neutralised:
		var final_settling_movement: Vector2 = _navigation.get("LastPlannedMovement")
		var final_settling_turn := absf(float(_navigation.get("LastPlannedTurn")))
		neutralised = final_settling_movement.length() <= 0.01 and final_settling_turn <= 0.01
	var stable_start := _actor.global_transform
	var maximum_stability_drift := 0.0
	var stability_command_violation := false
	for _frame in range(60):
		await get_tree().physics_frame
		await get_tree().process_frame
		maximum_stability_drift = maxf(maximum_stability_drift, _actor.global_position.distance_to(stable_start.origin))
		var stable_movement: Vector2 = _navigation.get("LastPlannedMovement")
		var stable_turn := absf(float(_navigation.get("LastPlannedTurn")))
		stability_command_violation = stability_command_violation or stable_movement.length() > 0.01 or stable_turn > 0.01
	var destination_forward := (destination.basis * Vector3.FORWARD).normalized()
	var actor_forward := (-_actor.global_basis.z).normalized()
	var yaw_error := acos(clampf(Vector2(destination_forward.x, destination_forward.z).normalized().dot(Vector2(actor_forward.x, actor_forward.z).normalized()), -1.0, 1.0))
	var distance := _actor.global_position.distance_to(destination.origin)
	var drift := maximum_stability_drift
	var movement: Vector2 = _navigation.get("LastPlannedMovement")
	var turn := absf(float(_navigation.get("LastPlannedTurn")))
	var completed := not bool(_navigation.get("IsNavigationRunning"))
	print("PREDICTIVE_NAV_STABILITY scenario=%s neutralisation_frames=%d neutralised=%s completed=%s stability_command_violation=%s distance=%.6f yaw=%.6f drift=%.6f movement=%.6f turn=%.6f" % [scenario, neutralisation_frames, str(neutralised), str(completed), str(stability_command_violation), distance, yaw_error, drift, movement.length(), turn])
	if not neutralised or not completed or stability_command_violation or distance > 0.05 or yaw_error > deg_to_rad(3.0) or drift > 0.005 or movement.length() > 0.01 or turn > 0.01:
		SceneUtils.fatal_error_and_quit("Scenario %s failed completion stability bounds" % scenario)
	return {"distance": distance, "yaw": yaw_error, "drift": drift, "completed": completed, "neutralisation_frames": neutralisation_frames}


func _destination_yaw_error(destination: Transform3D) -> float:
	var destination_forward := (destination.basis * Vector3.FORWARD).normalized()
	var actor_forward := (-_actor.global_basis.z).normalized()
	return acos(clampf(Vector2(destination_forward.x, destination_forward.z).normalized().dot(Vector2(actor_forward.x, actor_forward.z).normalized()), -1.0, 1.0))


func _configure_terminal_facing_camera(destination: Transform3D, checkpoint: String) -> void:
	var camera := get_node(^"VerificationCamera") as Camera3D
	var requested_forward := (destination.basis * Vector3.FORWARD).normalized()
	var requested_right := requested_forward.cross(Vector3.UP).normalized()
	var target := _actor.global_position + Vector3.UP * 0.95
	camera.projection = Camera3D.PROJECTION_PERSPECTIVE
	camera.fov = TERMINAL_CAMERA_FOV
	camera.global_position = target + requested_forward * TERMINAL_CAMERA_DISTANCE + requested_right * TERMINAL_CAMERA_SIDE_OFFSET + Vector3.UP * (TERMINAL_CAMERA_HEIGHT - 0.95)
	camera.look_at(target, Vector3.UP)
	camera.make_current()
	var camera_to_target := (target - camera.global_position).normalized()
	var expected_camera_to_target := (-requested_forward * TERMINAL_CAMERA_DISTANCE - requested_right * TERMINAL_CAMERA_SIDE_OFFSET + Vector3.UP * (0.95 - TERMINAL_CAMERA_HEIGHT)).normalized()
	if get_viewport().get_camera_3d() != camera or camera_to_target.dot(expected_camera_to_target) < 0.999:
		SceneUtils.fatal_error_and_quit("Terminal-facing camera did not retain the required close three-quarter framing")
		return
	print("PREDICTIVE_NAV_TERMINAL_CAMERA checkpoint=%s position=%s target=%s requested_forward=%s camera_to_target=%s" % [checkpoint, camera.global_position, target, requested_forward, camera_to_target])


func _sample(frame: int, scenario: String) -> void:
	var movement: Vector2 = _navigation.get("LastPlannedMovement")
	var turn := float(_navigation.get("LastPlannedTurn"))
	_maximum_control_delta = maxf(_maximum_control_delta, maxf(movement.distance_to(_previous_movement), absf(turn - _previous_turn)))
	if turn * _previous_turn < -0.0025:
		_reversal_count += 1
	_previous_movement = movement
	_previous_turn = turn
	if frame % TRAIL_INTERVAL == 0:
		_trail.append(_actor.global_position)
	if frame % 30 == 0:
		var forward := -_actor.global_basis.z.normalized()
		var route_direction := (_navigation.get("CurrentPath") as PackedVector3Array)
		print("PREDICTIVE_NAV_TELEMETRY scenario=%s frame=%d position=%s forward=%s progress=%.3f cross_track=%.3f planner_remaining=%.3f nav_remaining=%.3f path_index=%d position_complete=%s facing_complete=%s movement=%s turn=%.3f control_delta=%.3f reversals=%d path_points=%d" % [scenario, frame, _actor.global_position, forward, float(_navigation.get("LastRouteProgress")), float(_navigation.get("LastCrossTrackError")), float(_navigation.get("LastRemainingDistance")), float(_navigation.get("LastNavigationRemainingDistance")), int(_navigation.get("CurrentPathIndex")), str(_navigation.get("LastPositionComplete")), str(_navigation.get("LastFacingComplete")), movement, turn, _maximum_control_delta, _reversal_count, route_direction.size()])


func _count_corner_reversals(path: PackedVector3Array) -> int:
	var previous_sign := 0
	var reversals := 0
	for index in range(1, path.size() - 1):
		var incoming := Vector2(path[index].x - path[index - 1].x, path[index].z - path[index - 1].z).normalized()
		var outgoing := Vector2(path[index + 1].x - path[index].x, path[index + 1].z - path[index].z).normalized()
		var cross := incoming.x * outgoing.y - incoming.y * outgoing.x
		var corner_sign := int(signf(cross)) if absf(cross) > 0.01 else 0
		if corner_sign != 0 and previous_sign != 0 and corner_sign != previous_sign:
			reversals += 1
		if corner_sign != 0:
			previous_sign = corner_sign
	return reversals


func _prepare_route_visual(
		destination: Transform3D,
		color: Color,
		label: String,
		draw_anticipation: bool
	) -> Dictionary:
	var path: PackedVector3Array = []
	for _frame in range(20):
		path = _navigation.get("CurrentPath") as PackedVector3Array
		if path.size() >= 2:
			break
		await get_tree().physics_frame
	if path.size() < 2:
		SceneUtils.fatal_error_and_quit("Accepted route geometry was unavailable for visual annotation")
		return {}
	_clear_reference_visuals()
	return _draw_route_geometry(path, destination, color, label, draw_anticipation)


func _draw_route_geometry(
		path: PackedVector3Array,
		destination: Transform3D,
		color: Color,
		label: String,
		draw_anticipation: bool
	) -> Dictionary:
	for index in range(path.size() - 1):
		_add_line_prism(_reference_visuals, path[index] + Vector3.UP * 0.08, path[index + 1] + Vector3.UP * 0.08, color, 0.055)
	for index in range(path.size()):
		_draw_point_marker(_reference_visuals, path[index], color, "W%d" % index)
	_add_label(_reference_visuals, label, path[0] + Vector3(0.0, 0.18, 0.28), color, 26)
	_draw_endpoint_region(destination.origin, 0.65)
	var destination_forward := (destination.basis * Vector3.FORWARD).normalized()
	_add_arrow(_reference_visuals, destination.origin + Vector3.UP * 0.11, destination_forward * 0.75, ENDPOINT_COLOR, 0.065)
	_add_label(_reference_visuals, "DESTINATION FACING", destination.origin + Vector3(0.0, 0.18, 0.42), ENDPOINT_COLOR, 24)

	var largest_angle := 0.0
	var primary_corner := Vector3.ZERO
	var anticipation_start := Vector3.ZERO
	for index in range(1, path.size() - 1):
		var incoming_3d := path[index] - path[index - 1]
		var outgoing_3d := path[index + 1] - path[index]
		var incoming := Vector2(incoming_3d.x, incoming_3d.z)
		var outgoing := Vector2(outgoing_3d.x, outgoing_3d.z)
		if incoming.length_squared() < 0.0001 or outgoing.length_squared() < 0.0001:
			continue
		incoming = incoming.normalized()
		outgoing = outgoing.normalized()
		var angle := rad_to_deg(acos(clampf(incoming.dot(outgoing), -1.0, 1.0)))
		if angle > largest_angle:
			largest_angle = angle
			primary_corner = path[index]
			var incoming_direction := Vector3(incoming.x, 0.0, incoming.y)
			var lead := minf(0.8 * clampf(angle / 90.0, 0.2, 1.5), incoming_3d.length())
			anticipation_start = primary_corner - incoming_direction * lead
	if draw_anticipation and largest_angle > 0.0:
		_add_line_prism(_reference_visuals, anticipation_start + Vector3.UP * 0.12, primary_corner + Vector3.UP * 0.12, ANTICIPATION_COLOR, 0.09)
		_draw_point_marker(_reference_visuals, anticipation_start, ANTICIPATION_COLOR, "ANTICIPATION START")
		_draw_point_marker(_reference_visuals, primary_corner, ANTICIPATION_COLOR, "PRIMARY CORNER %.1f°" % largest_angle)
	return {
		"path": path,
		"corner_angle_degrees": largest_angle,
		"corner": primary_corner,
		"anticipation_start": anticipation_start,
	}


func _draw_endpoint_region(origin: Vector3, radius: float) -> void:
	var previous := origin + Vector3(radius, 0.0, 0.0)
	for index in range(1, 49):
		var angle := TAU * float(index) / 48.0
		var current := origin + Vector3(cos(angle) * radius, 0.0, sin(angle) * radius)
		_add_line_prism(_reference_visuals, previous + Vector3.UP * 0.06, current + Vector3.UP * 0.06, ENDPOINT_COLOR, 0.035)
		previous = current
	_add_label(_reference_visuals, "ENDPOINT CORRECTION ≤ 0.65 m", origin + Vector3(0.0, 0.16, -0.82), ENDPOINT_COLOR, 22)


func _draw_deviation_marker(start: Vector3, finish: Vector3) -> void:
	var actual_displacement := finish - start
	var display_origin := start + Vector3(0.0, 0.18, 0.38)
	_add_arrow(_annotation_visuals, display_origin, actual_displacement * 6.0, DEVIATION_COLOR, 0.075)
	_add_label(
		_annotation_visuals,
		"+X DEVIATION ×6 DISPLAY (ACTUAL 0.12 m)",
		display_origin + Vector3(0.36, 0.05, 0.18),
		DEVIATION_COLOR,
		22
	)
	_draw_point_marker(_annotation_visuals, finish, DEVIATION_COLOR, "ACTUAL OFFSET END")


func _capture(slug: String, telemetry: String) -> void:
	_telemetry_label.text = telemetry
	_draw_trail()
	await SceneUtils.capture_screenshot(get_tree(), "%s/%s.jpg" % [OUTPUT_ROOT, slug])
	_previous_movement = _navigation.get("LastPlannedMovement") as Vector2
	_previous_turn = float(_navigation.get("LastPlannedTurn"))
	var image_path := "res://temp/%s/%s.jpg" % [OUTPUT_ROOT, slug]
	if not FileAccess.file_exists(image_path):
		SceneUtils.fatal_error_and_quit("Required predictive-navigation capture is missing: %s" % image_path)


func _clear_trail_visual() -> void:
	var old := get_node_or_null(^"RuntimeTrail")
	if old != null:
		old.free()


func _draw_trail() -> void:
	_clear_trail_visual()
	if _trail.size() < 2:
		return
	var trail := Node3D.new()
	trail.name = "RuntimeTrail"
	add_child(trail)
	for index in range(_trail.size() - 1):
		_add_line_prism(
			trail,
			_trail[index] + Vector3.UP * 0.2,
			_trail[index + 1] + Vector3.UP * 0.2,
			TRAIL_COLOR,
			0.04
		)


func _create_visual_layers() -> void:
	_reference_visuals = Node3D.new()
	_reference_visuals.name = "ReferenceRouteVisuals"
	add_child(_reference_visuals)
	_annotation_visuals = Node3D.new()
	_annotation_visuals.name = "ScenarioAnnotations"
	add_child(_annotation_visuals)


func _clear_reference_visuals() -> void:
	for child in _reference_visuals.get_children():
		child.free()
	for child in _annotation_visuals.get_children():
		child.free()


func _draw_axis_and_legend() -> void:
	var axes := Node3D.new()
	axes.name = "ScreenAxisLegend"
	add_child(axes)
	var origin := Vector3(3.65, 0.12, 2.55)
	_add_arrow(axes, origin, Vector3.RIGHT * 0.7, Color(1.0, 0.25, 0.2), 0.055)
	_add_arrow(axes, origin, Vector3.LEFT * 0.7, Color(0.75, 0.2, 0.15), 0.055)
	_add_arrow(axes, origin, Vector3.FORWARD * 0.7, Color(0.2, 1.0, 0.3), 0.055)
	_add_arrow(axes, origin, Vector3.BACK * 0.7, Color(1.0, 0.2, 0.8), 0.055)
	_add_label(axes, "+X", origin + Vector3(0.9, 0.08, 0.0), Color.WHITE, 22, 0.005)
	_add_label(axes, "-X", origin + Vector3(-0.9, 0.08, 0.0), Color.WHITE, 22, 0.005)
	_add_label(axes, "↑ -Z / FORWARD", origin + Vector3(0.0, 0.08, -0.95), Color(0.2, 1.0, 0.3), 20, 0.005)
	_add_label(axes, "↓ +Z", origin + Vector3(0.0, 0.08, 0.95), Color(1.0, 0.2, 0.8), 20, 0.005)
	_add_label(axes, "LEGEND: YELLOW route | CYAN actual | ORANGE anticipation | MAGENTA event | PURPLE endpoint", Vector3(0.0, 0.2, 3.35), Color.WHITE, 18, 0.0035)


func _create_telemetry_overlay() -> void:
	var canvas := CanvasLayer.new()
	canvas.name = "TelemetryOverlay"
	add_child(canvas)
	var panel := ColorRect.new()
	panel.position = Vector2(14.0, 14.0)
	panel.size = Vector2(760.0, 112.0)
	panel.color = Color(0.015, 0.02, 0.03, 0.84)
	canvas.add_child(panel)
	_telemetry_label = Label.new()
	_telemetry_label.position = Vector2(26.0, 20.0)
	_telemetry_label.size = Vector2(730.0, 100.0)
	_telemetry_label.add_theme_font_size_override(&"font_size", 19)
	_telemetry_label.add_theme_color_override(&"font_color", Color.WHITE)
	canvas.add_child(_telemetry_label)
	var legend_panel := ColorRect.new()
	legend_panel.position = Vector2(14.0, 600.0)
	legend_panel.size = Vector2(1120.0, 34.0)
	legend_panel.color = Color(0.015, 0.02, 0.03, 0.9)
	canvas.add_child(legend_panel)
	var legend_label := Label.new()
	legend_label.position = Vector2(24.0, 605.0)
	legend_label.text = "SCREEN: ← −X | +X → | ↑ −Z / FORWARD | +Z ↓    LEGEND: YELLOW route | CYAN actual | ORANGE anticipation | MAGENTA event | PURPLE endpoint"
	legend_label.add_theme_font_size_override(&"font_size", 15)
	legend_label.add_theme_color_override(&"font_color", Color.WHITE)
	canvas.add_child(legend_label)


func _draw_point_marker(parent: Node3D, marker_position: Vector3, color: Color, text: String) -> void:
	var mesh := CylinderMesh.new()
	mesh.top_radius = 0.085
	mesh.bottom_radius = 0.085
	mesh.height = 0.045
	mesh.radial_segments = 20
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = color
	mesh.material = material
	var marker := MeshInstance3D.new()
	marker.mesh = mesh
	marker.position = marker_position + Vector3.UP * 0.09
	parent.add_child(marker)
	_add_label(parent, text, marker_position + Vector3(0.16, 0.17, 0.12), color, 19)


func _add_arrow(parent: Node3D, origin: Vector3, direction: Vector3, color: Color, width: float) -> void:
	var finish := origin + direction
	_add_line_prism(parent, origin, finish, color, width)
	var planar := Vector3(direction.x, 0.0, direction.z).normalized()
	if planar.is_zero_approx():
		return
	var side := Vector3(-planar.z, 0.0, planar.x)
	_add_line_prism(parent, finish, finish - planar * 0.2 + side * 0.12, color, width)
	_add_line_prism(parent, finish, finish - planar * 0.2 - side * 0.12, color, width)


func _add_line_prism(parent: Node3D, start: Vector3, finish: Vector3, color: Color, width: float) -> void:
	var length := start.distance_to(finish)
	if length <= 0.0001:
		return
	var mesh := BoxMesh.new()
	mesh.size = Vector3(width, width, length)
	var material := StandardMaterial3D.new()
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	material.albedo_color = color
	mesh.material = material
	var instance := MeshInstance3D.new()
	instance.mesh = mesh
	var midpoint := (start + finish) * 0.5
	instance.position = midpoint
	instance.look_at_from_position(midpoint, finish, Vector3.UP)
	parent.add_child(instance)


func _add_label(
		parent: Node3D,
		text: String,
		label_position: Vector3,
		color: Color,
		font_size: int,
		pixel_size := 0.006
	) -> void:
	var label := Label3D.new()
	label.text = text
	label.position = label_position
	label.font_size = font_size
	label.pixel_size = pixel_size
	label.modulate = color
	label.outline_size = 8
	label.no_depth_test = true
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	parent.add_child(label)


func _clear_stale_output() -> void:
	var absolute := ProjectSettings.globalize_path("res://temp/%s" % OUTPUT_ROOT)
	_remove_directory_contents(absolute)


func _remove_directory_contents(absolute_path: String) -> void:
	var directory := DirAccess.open(absolute_path)
	if directory == null:
		return
	directory.list_dir_begin()
	var entry := directory.get_next()
	while not entry.is_empty():
		if entry != "." and entry != "..":
			var child := absolute_path.path_join(entry)
			if directory.current_is_dir():
				_remove_directory_contents(child)
				DirAccess.remove_absolute(child)
			else:
				DirAccess.remove_absolute(child)
		entry = directory.get_next()
	directory.list_dir_end()


func _verify_required_images() -> void:
	for relative_path in REQUIRED_IMAGES:
		var resource_path := "res://temp/%s/%s" % [OUTPUT_ROOT, relative_path]
		if not FileAccess.file_exists(resource_path):
			SceneUtils.fatal_error_and_quit("Required final image was not freshly generated: %s" % resource_path)
			return
	print("PREDICTIVE_NAV_VISUAL_CAPTURE_SET_PASS images=%d output=res://temp/%s" % [REQUIRED_IMAGES.size(), OUTPUT_ROOT])
