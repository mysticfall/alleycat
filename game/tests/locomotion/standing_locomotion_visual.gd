extends SceneTree

const TEST_SCENE_PATH := "res://tests/locomotion/standing_locomotion_visual.tscn"
const MALE_SCENE_PATH := "res://assets/characters/templates/reference_male/reference_male_base.tscn"
const OUTPUT_ROOT := "CTRL-001/standing_locomotion"
const PIVOT_OUTPUT_ROOT := "ANIM-003/pivot_playback"
const ARC_LOOP_OUTPUT_ROOT := "ANIM-003/walk_arc_loop_seams"
const PIVOT_RELEASE_OUTPUT_ROOT := "NAV-001/production_pivot_release"
const MOVEMENT_PARAMETER := &"parameters/States/Walking/Locomotion/Movement/blend_position"
const TURN_PARAMETER := &"parameters/States/Walking/Locomotion/blend_position"
const PLAYBACK_PARAMETER := &"parameters/States/playback"
const LOOP_BOUNDARY_STEP_SECONDS := 1.0 / 120.0
const PIVOT_PRE_WRAP_FRAME_SECONDS := 1.0 / 60.0
const PIVOT_MINIMUM_SIGNED_YAW_RADIANS := 1.2
const FORWARD_MOVEMENT := Vector2(0.0, 1.0)

const SCENARIOS := [
	{"name": "idle", "movement": Vector2.ZERO, "turn": 0.0, "sample": 0.25},
	{"name": "forward", "movement": Vector2(0.0, 1.0), "turn": 0.0, "sample": 0.42},
	{"name": "backward_local_correction", "movement": Vector2(0.0, -1.0), "turn": 0.0, "sample": 0.42},
	{"name": "side_step_left", "movement": Vector2(-1.0, 0.0), "turn": 0.0, "sample": 0.48},
	{"name": "side_step_right", "movement": Vector2(1.0, 0.0), "turn": 0.0, "sample": 0.48},
	{"name": "walk_arc_left", "movement": Vector2(0.0, 1.0), "turn": -1.0, "sample": 0.42},
	{"name": "walk_arc_right", "movement": Vector2(0.0, 1.0), "turn": 1.0, "sample": 0.42},
	{"name": "turn_in_place_left_90", "movement": Vector2.ZERO, "turn": -1.0, "sample": 0.75},
	{"name": "turn_in_place_right_90", "movement": Vector2.ZERO, "turn": 1.0, "sample": 0.75},
]

func _init() -> void:
	await _run()

func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("CTRL-001 locomotion screenshots require a renderer")
		return
	var photobooth := SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("Failed to instantiate locomotion photobooth")
		return
	root.add_child(photobooth)
	var female := SceneUtils.require_node(photobooth, ^"Subject/Female") as Node3D
	var male_scene := SceneUtils.load_scene(MALE_SCENE_PATH)
	var male := male_scene.instantiate() as Node3D
	male.name = "Male"
	photobooth.get_node(^"Subject").add_child(male)
	await SceneUtils.wait_frames(self, 4)
	_validate_camera_orientation(photobooth)
	_prepare_subject(female)
	_prepare_subject(male)
	if OS.get_cmdline_user_args().has("--direct-root-track-evidence-only"):
		male.visible = false
		await _capture_direct_root_tracks(photobooth, female)
		_shutdown(photobooth)
		return
	if OS.get_cmdline_user_args().has("--root-turn-evidence-only"):
		male.visible = false
		await _capture_root_turn_progression(photobooth, female)
		_shutdown(photobooth)
		return
	if OS.get_cmdline_user_args().has("--production-pivot-release-evidence-only"):
		male.visible = false
		await _capture_production_pivot_release(photobooth, female)
		_shutdown(photobooth)
		return
	if OS.get_cmdline_user_args().has("--arc-loop-evidence-only"):
		male.visible = false
		await _capture_walk_arc_loop_boundaries(photobooth, female)
		print("ANIM003_WALK_ARC_LOOP_VISUAL_PASS artefact_root=%s" % ARC_LOOP_OUTPUT_ROOT)
		_shutdown(photobooth)
		return
	if OS.get_cmdline_user_args().has("--teardown-only"):
		print("CTRL001_STANDING_LOCOMOTION_TEARDOWN_READY")
		_shutdown(photobooth)
		return

	for subject_data in [{"name": "female", "node": female}, {"name": "male", "node": male}]:
		female.visible = subject_data.node == female
		male.visible = subject_data.node == male
		await SceneUtils.wait_frames(self, 2)
		await _capture_pair(photobooth, "framing/%s" % subject_data.name)
		var animation_tree := subject_data.node.get_node(^"AnimationTree") as AnimationTree
		for scenario in SCENARIOS:
			_apply_scenario(animation_tree, scenario)
			await SceneUtils.wait_frames(self, 2)
			await _capture_pair(photobooth, "scenarios/%s_%s" % [subject_data.name, scenario.name])
			if subject_data.name == "female" and String(scenario.name).begins_with("turn_in_place_"):
				await _capture_pivot_start_and_end(photobooth, female, scenario)
		if subject_data.name == "female" and OS.get_cmdline_user_args().has("--pivot-evidence-only"):
			print("ANIM003_PIVOT_PLAYBACK_VISUAL_PASS artefact_root=%s" % PIVOT_OUTPUT_ROOT)
			_shutdown(photobooth)
			return
		if subject_data.name == "female":
			await _capture_forward_loop_boundary(photobooth, female)

	print("CTRL001_STANDING_LOCOMOTION_VISUAL_PASS artefact_root=%s" % OUTPUT_ROOT)
	_shutdown(photobooth)

func _shutdown(photobooth: Photobooth) -> void:
	photobooth.free()
	print("CTRL001_STANDING_LOCOMOTION_TEARDOWN_COMPLETE")
	quit(0)

func _prepare_subject(subject: Node3D) -> void:
	var locomotion := subject.get_node_or_null(^"Locomotion")
	if locomotion != null:
		locomotion.set_physics_process(false)
	var animation_tree := subject.get_node(^"AnimationTree") as AnimationTree
	animation_tree.callback_mode_process = AnimationMixer.ANIMATION_CALLBACK_MODE_PROCESS_MANUAL
	# Visual samples advance the graph explicitly.  Leaving the tree in idle processing makes
	# screenshot frame waits alter the sampled loop phase.
	animation_tree.set_process(false)
	animation_tree.set_physics_process(false)
	animation_tree.active = true
	for modifier in subject.find_children("*", "SkeletonModifier3D", true, false):
		modifier.active = false

func _apply_scenario(animation_tree: AnimationTree, scenario: Dictionary) -> void:
	var playback := animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	playback.start(&"Walking", true)
	animation_tree.set(MOVEMENT_PARAMETER, scenario.get("pre_movement", scenario.movement))
	var pre_movement: Vector2 = scenario.get("pre_movement", scenario.movement)
	animation_tree.set(TURN_PARAMETER, Vector2(float(scenario.get("pre_turn", scenario.turn)), minf(pre_movement.length(), 1.0)))
	animation_tree.advance(float(scenario.get("pre_sample", 0.0)))
	var movement: Vector2 = scenario.movement
	animation_tree.set(MOVEMENT_PARAMETER, movement)
	animation_tree.set(TURN_PARAMETER, Vector2(float(scenario.turn), minf(movement.length(), 1.0)))
	animation_tree.advance(float(scenario.sample))

func _capture_pair(photobooth: Photobooth, slug: String) -> void:
	await photobooth.get_camera_rig("FrontCamera").capture_screenshot("%s/%s_front.jpg" % [OUTPUT_ROOT, slug])
	if slug.begins_with("framing/") or slug.contains("forward") or slug.contains("walk_arc") or slug.contains("turn_in_place"):
		await photobooth.get_camera_rig("RightCamera").capture_screenshot("%s/%s_right.jpg" % [OUTPUT_ROOT, slug])

func _capture_pivot_start_and_end(photobooth: Photobooth, female: Node3D, scenario: Dictionary) -> void:
	var animation_tree := female.get_node(^"AnimationTree") as AnimationTree
	var skeleton := female.get_node(^"Female/GeneralSkeleton") as Skeleton3D
	var root_bone := skeleton.find_bone(&"Root")
	var left_foot := skeleton.find_bone(&"LeftFoot")
	var right_foot := skeleton.find_bone(&"RightFoot")
	if root_bone < 0 or left_foot < 0 or right_foot < 0:
		SceneUtils.fatal_error_and_quit("Pivot photobooth is missing Root or foot bones")
		return
	var expected_role := &"TurnInPlaceLeft90" if String(scenario.name).contains("left") else &"TurnInPlaceRight90"
	# Imported Root Euler-Y retains the canonical Blender sign: the left physical pivot
	# is positive here and the right physical pivot is negative. CharacterLocomotion
	# converts this representation to actor yaw; this visual probe samples pre-conversion.
	var expected_sign := 1.0 if String(scenario.name).contains("left") else -1.0
	var clip := _get_pivot_animation_name(animation_tree, expected_role)
	var clip_length := _get_animation_length(animation_tree, clip)
	var pre_wrap_time := clip_length - PIVOT_PRE_WRAP_FRAME_SECONDS
	if pre_wrap_time <= 0.0:
		SceneUtils.fatal_error_and_quit("Pivot clip %s is too short for a pre-wrap graph sample" % clip)
		return
	var original_transform := female.global_transform
	female.global_transform = original_transform
	_start_graph_pivot_sample(animation_tree, scenario, 0.0)
	await SceneUtils.wait_frames(self, 2)
	var start := _pivot_measurement(animation_tree, skeleton, root_bone, left_foot, right_foot)
	await photobooth.get_camera_rig("FrontCamera").capture_screenshot("%s/%s_start_front.jpg" % [PIVOT_OUTPUT_ROOT, scenario.name])
	await photobooth.get_camera_rig("RightCamera").capture_screenshot("%s/%s_start_right.jpg" % [PIVOT_OUTPUT_ROOT, scenario.name])
	# Pivot clips currently loop.  The exact loop endpoint aliases the start pose, so this
	# graph-playback check samples one rendered frame before wrapping instead.
	female.global_transform = original_transform
	_start_graph_pivot_sample(animation_tree, scenario, pre_wrap_time)
	_consume_sampled_root_motion(female, pre_wrap_time)
	await SceneUtils.wait_frames(self, 2)
	var finish := _pivot_measurement(animation_tree, skeleton, root_bone, left_foot, right_foot)
	var planar_displacement: Vector2 = Vector2(finish.root_motion_position.x - start.root_motion_position.x, finish.root_motion_position.z - start.root_motion_position.z)
	var yaw_delta := wrapf(finish.yaw - start.yaw, -PI, PI)
	var playback := animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	var graph_time := playback.get_current_play_position()
	if planar_displacement.length() > 0.02 or yaw_delta * expected_sign < PIVOT_MINIMUM_SIGNED_YAW_RADIANS or not is_equal_approx(graph_time, pre_wrap_time):
		SceneUtils.fatal_error_and_quit("Pivot graph diagnostic failed for %s role=%s clip=%s: planar=%s yaw=%.6f graph_time=%.6f pre_wrap=%.6f" % [scenario.name, expected_role, clip, planar_displacement, yaw_delta, graph_time, pre_wrap_time])
		return
	await photobooth.get_camera_rig("FrontCamera").capture_screenshot("%s/%s_end_front.jpg" % [PIVOT_OUTPUT_ROOT, scenario.name])
	await photobooth.get_camera_rig("RightCamera").capture_screenshot("%s/%s_end_right.jpg" % [PIVOT_OUTPUT_ROOT, scenario.name])
	var record := {"scenario": scenario.name, "role": expected_role, "clip": clip, "clip_length_seconds": clip_length, "pre_wrap_time_seconds": pre_wrap_time, "graph_playback_time_seconds": graph_time, "start": start, "pre_wrap": finish, "planar_root_displacement": planar_displacement, "root_yaw_delta_radians": yaw_delta}
	var diagnostics := FileAccess.open("res://temp/%s/%s_diagnostics.json" % [PIVOT_OUTPUT_ROOT, scenario.name], FileAccess.WRITE)
	if diagnostics == null:
		SceneUtils.fatal_error_and_quit("Could not write pivot diagnostic record")
		return
	diagnostics.store_string(JSON.stringify(record, "  ") + "\n")
	print("ANIM003_PIVOT_DIAGNOSTIC %s" % JSON.stringify(record))
	female.global_transform = original_transform


func _start_graph_pivot_sample(animation_tree: AnimationTree, scenario: Dictionary, sample_time: float) -> void:
	var playback := animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	playback.stop()
	playback.start(&"Walking", true)
	var movement: Vector2 = scenario.movement
	animation_tree.set(MOVEMENT_PARAMETER, movement)
	animation_tree.set(TURN_PARAMETER, Vector2(float(scenario.turn), minf(movement.length(), 1.0)))
	animation_tree.advance(0.0)
	animation_tree.advance(sample_time)


func _consume_sampled_root_motion(female: Node3D, delta_seconds: float) -> void:
	var locomotion := female.get_node_or_null(^"Locomotion")
	if locomotion == null:
		SceneUtils.fatal_error_and_quit("Pivot graph sample is missing CharacterLocomotion")
		return
	locomotion.call(&"Move", Vector2.ZERO)
	locomotion.call(&"_PhysicsProcess", delta_seconds)


func _get_pivot_animation_name(animation_tree: AnimationTree, role: StringName) -> StringName:
	var root_tree := animation_tree.tree_root as AnimationNodeBlendTree
	var states := root_tree.get_node(&"States") as AnimationNodeStateMachine
	var walking := states.get_node(&"Walking") as AnimationNodeBlendTree
	var locomotion := walking.get_node(&"Locomotion") as AnimationNodeBlendSpace2D
	var point := locomotion.find_blend_point_by_name(role)
	if point >= 0:
		var node := locomotion.get_blend_point_node(point) as AnimationNodeAnimation
		if node != null:
			return node.animation
	SceneUtils.fatal_error_and_quit("Walking locomotion blend space does not define pivot role %s" % role)
	return &""


func _get_animation_length(animation_tree: AnimationTree, animation_name: StringName) -> float:
	var player := animation_tree.get_node(animation_tree.anim_player) as AnimationPlayer
	var animation := player.get_animation(animation_name)
	if animation == null:
		SceneUtils.fatal_error_and_quit("AnimationTree graph animation does not resolve: %s" % animation_name)
		return 0.0
	return animation.length

func _pivot_measurement(animation_tree: AnimationTree, skeleton: Skeleton3D, root_bone: int, left_foot: int, right_foot: int) -> Dictionary:
	var root_pose := skeleton.get_bone_global_pose(root_bone)
	# The graph extracts %GeneralSkeleton:Root as root motion, so it deliberately does not
	# remain in the skeleton's evaluated pose.  Accumulators are the graph's real sampled
	# Root transform; the skeleton pose is retained only as evidence of that extraction.
	var root_motion_rotation := animation_tree.get_root_motion_rotation_accumulator()
	return {"root_motion_position": animation_tree.get_root_motion_position_accumulator(), "yaw": root_motion_rotation.get_euler().y, "extracted_skeleton_root": root_pose.origin, "left_foot": skeleton.get_bone_global_pose(left_foot).origin, "right_foot": skeleton.get_bone_global_pose(right_foot).origin}


func _capture_root_turn_progression(photobooth: Photobooth, female: Node3D) -> void:
	var animation_tree := female.get_node(^"AnimationTree") as AnimationTree
	var skeleton := female.get_node(^"Female/GeneralSkeleton") as Skeleton3D
	var root_bone := skeleton.find_bone(&"Root")
	var left_clavicle := skeleton.find_bone(&"LeftShoulder")
	var right_clavicle := skeleton.find_bone(&"RightShoulder")
	if root_bone < 0 or left_clavicle < 0 or right_clavicle < 0:
		SceneUtils.fatal_error_and_quit("Root-turn photobooth is missing Root or bilateral shoulder landmarks")
		return
	var front := photobooth.get_camera_rig("FrontCamera")
	front.orthogonal_scale = 3.0
	var records: Array[Dictionary] = []
	for scenario in [
		{"name": "pivot_right", "movement": Vector2.ZERO, "turn": 1.0},
		{"name": "pivot_left", "movement": Vector2.ZERO, "turn": -1.0},
	]:
		var samples: Array[Dictionary] = []
		for sample in [{"name": "start", "time": 0.0}, {"name": "mid", "time": 0.5}, {"name": "end", "time": 1.0}]:
			var applied: Dictionary = scenario.duplicate()
			applied.sample = sample.time
			_apply_scenario(animation_tree, applied)
			await SceneUtils.wait_frames(self, 2)
			var measurement := _root_turn_measurement(animation_tree, skeleton, root_bone, left_clavicle, right_clavicle)
			measurement.sample = sample.name
			samples.append(measurement)
			await front.capture_screenshot("ANIM-003/root_turn_progression/%s_%s_front.png" % [scenario.name, sample.name])
		var root_yaw_delta := wrapf(float(samples[2].root_yaw) - float(samples[0].root_yaw), -PI, PI)
		var body_yaw_delta := wrapf(float(samples[2].body_yaw) - float(samples[0].body_yaw), -PI, PI)
		var planar := Vector2(float(samples[2].root_position.x) - float(samples[0].root_position.x), float(samples[2].root_position.z) - float(samples[0].root_position.z))
		var matching_signed_progression := root_yaw_delta * body_yaw_delta > 0.0
		if absf(root_yaw_delta) <= 0.4 or absf(body_yaw_delta) <= 0.4 or not matching_signed_progression or planar.length() > 0.02:
			print("ANIM003_ROOT_TURN_INVALID %s" % JSON.stringify({"root_yaw": root_yaw_delta, "body_yaw": body_yaw_delta, "planar": planar, "samples": samples}))
			SceneUtils.fatal_error_and_quit("Root/body pivot evidence is not a non-zero stationary matching signed turn for %s" % scenario.name)
			return
		records.append({"scenario": scenario.name, "root_track_path": "%GeneralSkeleton:Root", "samples": samples, "root_yaw_delta_radians": root_yaw_delta, "visible_body_turn_delta_radians": body_yaw_delta, "planar_root_delta": planar, "matching_signed_turn_progression": matching_signed_progression, "stationary_pivot": true})
	if float(records[0].root_yaw_delta_radians) * float(records[1].root_yaw_delta_radians) >= 0.0 or float(records[0].visible_body_turn_delta_radians) * float(records[1].visible_body_turn_delta_radians) >= 0.0:
		SceneUtils.fatal_error_and_quit("Pivot evidence did not retain opposing Root and body yaw signs")
		return
	var output := FileAccess.open("res://temp/ANIM-003/root_turn_progression/run_metadata.json", FileAccess.WRITE)
	output.store_string(JSON.stringify({"measurement": "body forward = UP cross (right shoulder - left shoulder); deltas are relative to each track start heading", "records": records}, "  ") + "\n")
	print("ANIM003_ROOT_TURN_PROGRESSION %s" % JSON.stringify(records))


func _root_turn_measurement(animation_tree: AnimationTree, skeleton: Skeleton3D, root_bone: int, left_shoulder: int, right_shoulder: int) -> Dictionary:
	var root := skeleton.get_bone_global_pose(root_bone)
	var lateral := skeleton.get_bone_global_pose(right_shoulder).origin - skeleton.get_bone_global_pose(left_shoulder).origin
	var body_forward := Vector3.UP.cross(lateral).normalized()
	# The graph extracts exactly %GeneralSkeleton:Root, therefore the accumulator is the
	# evaluated Root track while the skeleton Root pose remains deliberately neutral. Compose
	# that extracted track with the shoulder landmarks to recover the physical body heading.
	var root_rotation := animation_tree.get_root_motion_rotation_accumulator()
	var physical_body_forward := (Basis(root_rotation) * body_forward).normalized()
	return {"root_position": animation_tree.get_root_motion_position_accumulator(), "root_yaw": root_rotation.get_euler().y, "body_yaw": atan2(physical_body_forward.x, physical_body_forward.z), "extracted_skeleton_root_position": root.origin}


func _capture_direct_root_tracks(photobooth: Photobooth, female: Node3D) -> void:
	var player := female.get_node(^"AnimationPlayer") as AnimationPlayer
	var skeleton := female.get_node(^"Female/GeneralSkeleton") as Skeleton3D
	var root_bone := skeleton.find_bone(&"Root")
	var left_shoulder := skeleton.find_bone(&"LeftShoulder")
	var right_shoulder := skeleton.find_bone(&"RightShoulder")
	var camera := photobooth.get_camera_rig("FrontCamera")
	# Fixed, wider world framing covers the arc's recorded +Z travel; the subject is never re-centred.
	camera.global_position = Vector3(camera.global_position.x, camera.global_position.y, -4.0)
	camera.orthogonal_scale = 5.0
	var records: Array[Dictionary] = []
	for data in [{"name": "pivot_right", "key": "mixamo_c9cef01d_b96c_11e4_a802_0aaa78deedf9"}, {"name": "pivot_left", "key": "mixamo_c9ceef5f_b96c_11e4_a802_0aaa78deedf9"}]:
		var animation_name := &"locomotion/" + StringName(data.key)
		var animation := player.get_animation(animation_name)
		var position_track := animation.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_POSITION_3D)
		var rotation_track := animation.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_ROTATION_3D)
		if position_track < 0 or rotation_track < 0:
			SceneUtils.fatal_error_and_quit("Missing Root tracks for %s" % animation_name)
			return
		var samples: Array[Dictionary] = []
		# AnimationPlayer wraps an exact loop endpoint to zero. Sample one rendered 60 Hz frame
		# before it so the final key is observed without manufacturing a terminal value.
		for fraction in [0.0, 0.5, 1.0 - (1.0 / 60.0) / animation.length]:
			player.play(animation_name)
			player.seek(animation.length * fraction, true)
			await SceneUtils.wait_frames(self, 2)
			var sample := _direct_root_turn_measurement(skeleton, root_bone, left_shoulder, right_shoulder)
			sample.fraction = fraction
			samples.append(sample)
			await camera.capture_screenshot("ANIM-003/direct_root_tracks/%s_%.1f_front.png" % [data.name, fraction])
		var root_delta := wrapf(float(samples[2].root_yaw) - float(samples[0].root_yaw), -PI, PI)
		var body_delta := wrapf(float(samples[2].body_yaw) - float(samples[0].body_yaw), -PI, PI)
		var planar := Vector2(float(samples[2].root_position.x) - float(samples[0].root_position.x), float(samples[2].root_position.z) - float(samples[0].root_position.z))
		var matching_signed_progression := root_delta * body_delta > 0.0
		if absf(root_delta) <= 1.0 or absf(body_delta) <= 1.0 or not matching_signed_progression or planar.length() > 0.02:
			SceneUtils.fatal_error_and_quit("Direct Root track evidence is not a non-zero stationary matching signed pivot for %s" % data.name)
			return
		records.append({"clip": data.key, "root_track_path": "%GeneralSkeleton:Root", "position_track": position_track, "rotation_track": rotation_track, "root_position_start": animation.position_track_interpolate(position_track, 0.0), "root_position_end": animation.position_track_interpolate(position_track, animation.length), "pre_wrap_time_seconds": animation.length - 1.0 / 60.0, "root_yaw_delta_radians": root_delta, "visible_body_turn_delta_radians": body_delta, "planar_delta": planar, "matching_signed_turn_progression": matching_signed_progression, "stationary_pivot": true, "samples": samples})
	if float(records[0].root_yaw_delta_radians) * float(records[1].root_yaw_delta_radians) >= 0.0 or float(records[0].visible_body_turn_delta_radians) * float(records[1].visible_body_turn_delta_radians) >= 0.0:
		SceneUtils.fatal_error_and_quit("Direct Root track evidence did not retain opposing pivot signs")
		return
	var file := FileAccess.open("res://temp/ANIM-003/direct_root_tracks/run_metadata.json", FileAccess.WRITE)
	file.store_string(JSON.stringify({"records": records}, "  ") + "\n")

func _direct_root_turn_measurement(skeleton: Skeleton3D, root_bone: int, left_shoulder: int, right_shoulder: int) -> Dictionary:
	var root := skeleton.get_bone_global_pose(root_bone)
	var lateral := skeleton.get_bone_global_pose(right_shoulder).origin - skeleton.get_bone_global_pose(left_shoulder).origin
	var body_forward := Vector3.UP.cross(lateral).normalized()
	var root_forward := root.basis.z.normalized()
	return {"root_position": root.origin, "root_yaw": atan2(root_forward.x, root_forward.z), "body_yaw": atan2(body_forward.x, body_forward.z)}

func _capture_production_pivot_release(photobooth: Photobooth, female: Node3D) -> void:
	var animation_tree := female.get_node(^"AnimationTree") as AnimationTree
	var locomotion := female.get_node(^"Locomotion")
	var playback := animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	var camera := photobooth.get_camera_rig("FrontCamera")
	camera.orthogonal_scale = 3.0
	var label := Label3D.new()
	label.name = "PivotReleaseEvidenceLabel"
	label.position = Vector3(-1.35, 2.35, 0.0)
	label.font_size = 52
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	label.modulate = Color(1.0, 0.85, 0.2)
	photobooth.add_child(label)
	locomotion.call(&"Move", Vector2.ZERO)
	locomotion.call(&"Rotate", Vector2.RIGHT)
	playback.start(&"Walking", true)
	animation_tree.advance(0.0)
	var last_time := playback.get_current_play_position()
	var accumulated_root_yaw := 0.0
	var accumulated_actor_yaw := 0.0
	var wrapped := false
	for tick in 960:
		var actor_yaw_before := female.global_basis.get_euler().y
		animation_tree.advance(LOOP_BOUNDARY_STEP_SECONDS)
		var root_yaw := animation_tree.get_root_motion_rotation().get_euler().y
		locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
		var actor_yaw_after := female.global_basis.get_euler().y
		accumulated_root_yaw += root_yaw
		accumulated_actor_yaw += wrapf(actor_yaw_after - actor_yaw_before, -PI, PI)
		var current_time := playback.get_current_play_position()
		if tick == 30:
			label.text = "PIVOT: held right turn"
			await SceneUtils.wait_frames(self, 2)
			await camera.capture_screenshot("%s/pivot_front.png" % PIVOT_RELEASE_OUTPUT_ROOT)
		if current_time < last_time:
			wrapped = true
			label.text = "WRAP: continuous Root yaw"
			await SceneUtils.wait_frames(self, 2)
			await camera.capture_screenshot("%s/wrap_front.png" % PIVOT_RELEASE_OUTPUT_ROOT)
			break
		last_time = current_time
	if not wrapped or absf(accumulated_root_yaw) <= 1.0 or absf(accumulated_actor_yaw) <= 1.0 or not is_equal_approx(accumulated_actor_yaw, accumulated_root_yaw):
		SceneUtils.fatal_error_and_quit("Production pivot actor yaw did not directly match one continuous signed Root yaw through a loop wrap")
		return
	locomotion.call(&"Rotate", Vector2.ZERO)
	locomotion.call(&"Move", FORWARD_MOVEMENT)
	# The graph is held explicitly for the visual hand-off checkpoint: this makes the
	# immediate pivot-to-forward branch visible without sampling an unrelated idle blend.
	playback.start(&"Walking", false)
	locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
	animation_tree.advance(LOOP_BOUNDARY_STEP_SECONDS)
	locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
	if playback.get_current_node() != &"Walking":
		SceneUtils.fatal_error_and_quit("Production pivot release crossed an Idle interlude; state=%s" % playback.get_current_node())
		return
	var movement_blend: Vector2 = animation_tree.get(MOVEMENT_PARAMETER)
	var turn_blend: Vector2 = animation_tree.get(TURN_PARAMETER)
	if movement_blend.y <= 0.9 or absf(turn_blend.x) > 0.01:
		SceneUtils.fatal_error_and_quit("Production pivot release did not enter the direct forward movement branch")
		return
	label.text = "FORWARD: direct movement branch"
	await SceneUtils.wait_frames(self, 2)
	await camera.capture_screenshot("%s/forward_front.png" % PIVOT_RELEASE_OUTPUT_ROOT)
	var file := FileAccess.open("res://temp/%s/run_metadata.json" % PIVOT_RELEASE_OUTPUT_ROOT, FileAccess.WRITE)
	file.store_string(JSON.stringify({"root_track_path": "%GeneralSkeleton:Root", "pivot_loop_wrapped": wrapped, "accumulated_root_yaw_radians": accumulated_root_yaw, "accumulated_actor_yaw_radians": accumulated_actor_yaw, "actor_yaw_matches_direct_signed_root_yaw": is_equal_approx(accumulated_actor_yaw, accumulated_root_yaw), "release_state": String(playback.get_current_node()), "movement_blend": movement_blend, "turn_blend": turn_blend, "images": ["pivot_front.png", "wrap_front.png", "forward_front.png"]}, "  ") + "\n")
	print("NAV001_PRODUCTION_PIVOT_RELEASE_PASS root_yaw=%.6f actor_yaw=%.6f" % [accumulated_root_yaw, accumulated_actor_yaw])

func _capture_forward_loop_boundary(photobooth: Photobooth, female: Node3D) -> void:
	var animation_tree := female.get_node(^"AnimationTree") as AnimationTree
	var locomotion := female.get_node(^"Locomotion")
	var skeleton := female.get_node(^"Female/GeneralSkeleton") as Skeleton3D
	var world_origin_reference := SceneUtils.require_node(photobooth, ^"WorldOriginReference") as Node3D
	var right_camera := photobooth.get_camera_rig("RightCamera")
	var fixed_right_camera_transform := right_camera.global_transform
	var root_bone := skeleton.find_bone(&"Root")
	if root_bone < 0:
		SceneUtils.fatal_error_and_quit("Forward locomotion skeleton does not define a Root bone")
		return
	# This advances the production AnimationTree followed by CharacterLocomotion's normal root-motion consumer.
	# The tree owns looping through the imported clip's loop mode; this fixture never starts or advances
	# AnimationPlayer directly.
	female.global_position = Vector3.ZERO
	animation_tree.active = true
	locomotion.call(&"Move", FORWARD_MOVEMENT)
	var playback := animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	playback.start(&"Walking", true)
	animation_tree.advance(0.0)
	for tick in 480:
		animation_tree.advance(LOOP_BOUNDARY_STEP_SECONDS)
		var root_delta := animation_tree.get_root_motion_position()
		locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
		var current_time := playback.get_current_play_position()
		if current_time + LOOP_BOUNDARY_STEP_SECONDS >= playback.get_current_length():
			var before_time := current_time
			var before_world_position := female.global_position
			var before_delta := root_delta
			await _capture_loop_boundary_frame(photobooth, skeleton, root_bone, world_origin_reference, fixed_right_camera_transform, "female_before_runtime_boundary_right")
			animation_tree.advance(LOOP_BOUNDARY_STEP_SECONDS)
			var after_delta := animation_tree.get_root_motion_position()
			locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
			var after_time := playback.get_current_play_position()
			var boundary_world_delta := female.global_position - before_world_position
			var root_motion_reference := female.get_node(^"Female") as Node3D
			var forward_axis: Vector3 = root_motion_reference.global_basis.z.normalized()
			if after_time >= before_time or boundary_world_delta.dot(forward_axis) <= 0.0:
				SceneUtils.fatal_error_and_quit("Runtime locomotion did not move forward through the AnimationTree loop boundary")
				return
			if boundary_world_delta.length() > maxf(before_delta.length(), after_delta.length()) * 3.0:
				SceneUtils.fatal_error_and_quit("Runtime locomotion produced a reset-sized displacement at the AnimationTree loop boundary")
				return
			print("CTRL001_RUNTIME_LOOP_BOUNDARY before_time=%.9f after_time=%.9f before_delta=%s after_delta=%s actor_boundary_delta=%s" % [before_time, after_time, before_delta, after_delta, boundary_world_delta])
			await _capture_loop_boundary_frame(photobooth, skeleton, root_bone, world_origin_reference, fixed_right_camera_transform, "female_after_runtime_boundary_right")
			return
	SceneUtils.fatal_error_and_quit("AnimationTree Walking state did not approach a cyclic runtime wrap")


func _capture_walk_arc_loop_boundaries(photobooth: Photobooth, female: Node3D) -> void:
	var animation_tree := female.get_node(^"AnimationTree") as AnimationTree
	var locomotion := female.get_node(^"Locomotion")
	var skeleton := female.get_node(^"Female/GeneralSkeleton") as Skeleton3D
	var front_camera := photobooth.get_camera_rig("FrontCamera")
	var root_bone := skeleton.find_bone(&"Root")
	if front_camera == null or root_bone < 0:
		SceneUtils.fatal_error_and_quit("Walk-arc seam fixture is missing its fixed front camera or Root bone")
		return
	# A loop-boundary pair may be sampled more than a metre along the authored arc.
	# Keep one fixed camera behind the full trajectory rather than letting the actor pass
	# through the near front camera and produce an empty evidence frame.
	front_camera.global_position = Vector3(front_camera.global_position.x, front_camera.global_position.y, -4.0)
	front_camera.orthogonal_scale = 5.0
	var fixed_camera_transform := front_camera.global_transform
	await SceneUtils.wait_frames(self, 2)
	await front_camera.capture_screenshot("%s/camera_framing_front.png" % ARC_LOOP_OUTPUT_ROOT)
	var run_record := {
		"spec_path": "res://../specs/animation/003-standing-locomotion-catalogue/index.md",
		"test_scene": TEST_SCENE_PATH,
		"test_runner": "res://tests/locomotion/standing_locomotion_visual.gd",
		"camera": {
			"name": "FrontCamera",
			"projection": "orthogonal",
			"orthogonal_scale": front_camera.orthogonal_scale,
			"image_dimensions": [int(front_camera.image_size.x), int(front_camera.image_size.y)],
			"global_position": _vector_json(front_camera.global_position),
			"global_transform": _transform_json(fixed_camera_transform),
		},
		"comparison_policy": "The camera remains fixed. The actor is reset to world origin only between left and right scenarios; within each before/after pair normal root-motion progression is retained without re-centering.",
		"expected_visual_cues": "The full body and both feet remain in the same orthographic framing. Across each wrap, the pose, planted-foot contact, and body silhouette should continue without a reset-sized pop or slide.",
		"clips": [],
	}
	for scenario in [{"role": "WalkArcLeft", "turn": -1.0}, {"role": "WalkArcRight", "turn": 1.0}]:
		var record := await _capture_walk_arc_loop_boundary(
			animation_tree,
			locomotion,
			skeleton,
			root_bone,
			female,
			front_camera,
			fixed_camera_transform,
			scenario
		)
		if record.is_empty():
			return
		run_record.clips.append(record)
	var output := FileAccess.open("res://temp/%s/run_metadata.json" % ARC_LOOP_OUTPUT_ROOT, FileAccess.WRITE)
	if output == null:
		SceneUtils.fatal_error_and_quit("Could not write walk-arc seam capture metadata")
		return
	output.store_string(JSON.stringify(run_record, "  ") + "\n")
	print("ANIM003_WALK_ARC_LOOP_METADATA %s" % JSON.stringify(run_record))


func _capture_walk_arc_loop_boundary(
	animation_tree: AnimationTree,
	locomotion: Node,
	skeleton: Skeleton3D,
	root_bone: int,
	female: Node3D,
	front_camera: CameraRig,
	fixed_camera_transform: Transform3D,
	scenario: Dictionary) -> Dictionary:
	var role := String(scenario.role)
	var turn := float(scenario.turn)
	var slug := "walk_arc_left" if turn < 0.0 else "walk_arc_right"
	if not front_camera.global_transform.is_equal_approx(fixed_camera_transform):
		SceneUtils.fatal_error_and_quit("Front camera moved before %s seam comparison" % role)
		return {}
	female.global_position = Vector3.ZERO
	animation_tree.active = true
	locomotion.call(&"Move", FORWARD_MOVEMENT)
	var playback := animation_tree.get(PLAYBACK_PARAMETER) as AnimationNodeStateMachinePlayback
	playback.start(&"Walking", true)
	animation_tree.set(MOVEMENT_PARAMETER, FORWARD_MOVEMENT)
	animation_tree.set(TURN_PARAMETER, Vector2(turn, 1.0))
	animation_tree.advance(0.0)
	var clip := _get_walk_arc_animation_name(animation_tree, turn)
	for tick in 960:
		animation_tree.advance(LOOP_BOUNDARY_STEP_SECONDS)
		var before_delta := animation_tree.get_root_motion_position()
		locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
		var before_time := playback.get_current_play_position()
		var clip_length := playback.get_current_length()
		if before_time + LOOP_BOUNDARY_STEP_SECONDS < clip_length:
			continue
		var before_actor_position := female.global_position
		var before_root_position := skeleton.to_global(skeleton.get_bone_global_pose(root_bone).origin)
		await SceneUtils.wait_frames(self, 2)
		await front_camera.capture_screenshot("%s/%s_before_wrap_front.png" % [ARC_LOOP_OUTPUT_ROOT, slug])
		animation_tree.advance(LOOP_BOUNDARY_STEP_SECONDS)
		var after_delta := animation_tree.get_root_motion_position()
		locomotion.call(&"_PhysicsProcess", LOOP_BOUNDARY_STEP_SECONDS)
		var after_time := playback.get_current_play_position()
		var after_actor_position := female.global_position
		var after_root_position := skeleton.to_global(skeleton.get_bone_global_pose(root_bone).origin)
		var actor_boundary_delta := after_actor_position - before_actor_position
		if after_time >= before_time or actor_boundary_delta.length() > maxf(before_delta.length(), after_delta.length()) * 3.0:
			SceneUtils.fatal_error_and_quit("Walk-arc runtime loop did not wrap continuously for %s" % role)
			return {}
		if not front_camera.global_transform.is_equal_approx(fixed_camera_transform):
			SceneUtils.fatal_error_and_quit("Front camera moved during %s seam comparison" % role)
			return {}
		await SceneUtils.wait_frames(self, 2)
		await front_camera.capture_screenshot("%s/%s_after_wrap_front.png" % [ARC_LOOP_OUTPUT_ROOT, slug])
		return {
			"role": role,
			"clip": String(clip),
			"sample_times_seconds": {"before_wrap": before_time, "after_wrap": after_time, "clip_length": clip_length},
			"world_actor_position": {"before_wrap": _vector_json(before_actor_position), "after_wrap": _vector_json(after_actor_position)},
			"world_root_position": {"before_wrap": _vector_json(before_root_position), "after_wrap": _vector_json(after_root_position)},
			"world_actor_displacement_across_wrap": _vector_json(actor_boundary_delta),
			"root_motion_delta": {"before_wrap": _vector_json(before_delta), "after_wrap": _vector_json(after_delta)},
			"images": {"before_wrap": "%s_before_wrap_front.png" % slug, "after_wrap": "%s_after_wrap_front.png" % slug},
		}
	SceneUtils.fatal_error_and_quit("AnimationTree Walking state did not approach a %s cyclic wrap" % role)
	return {}


func _get_walk_arc_animation_name(animation_tree: AnimationTree, turn: float) -> StringName:
	var root_tree := animation_tree.tree_root as AnimationNodeBlendTree
	var states := root_tree.get_node(&"States") as AnimationNodeStateMachine
	var walking := states.get_node(&"Walking") as AnimationNodeBlendTree
	var locomotion := walking.get_node(&"Locomotion") as AnimationNodeBlendSpace2D
	var point := locomotion.find_blend_point_by_name(&"WalkArcLeft" if turn < 0.0 else &"WalkArcRight")
	if point >= 0:
		var node := locomotion.get_blend_point_node(point) as AnimationNodeAnimation
		if node != null:
			return node.animation
	SceneUtils.fatal_error_and_quit("Walking locomotion blend space does not define the requested walk arc")
	return &""


func _vector_json(value: Vector3) -> Array[float]:
	return [value.x, value.y, value.z]


func _transform_json(value: Transform3D) -> Dictionary:
	return {"basis_x": _vector_json(value.basis.x), "basis_y": _vector_json(value.basis.y), "basis_z": _vector_json(value.basis.z), "origin": _vector_json(value.origin)}

func _capture_loop_boundary_frame(
	photobooth: Photobooth,
	skeleton: Skeleton3D,
	root_bone: int,
	world_origin_reference: Node3D,
	fixed_right_camera_transform: Transform3D,
	filename: String) -> void:
	var right_camera := photobooth.get_camera_rig("RightCamera")
	if not right_camera.global_transform.is_equal_approx(fixed_right_camera_transform):
		SceneUtils.fatal_error_and_quit("Right camera moved during the loop-boundary comparison")
		return
	var root_position := skeleton.to_global(skeleton.get_bone_global_pose(root_bone).origin)
	var marker_relative_root_position := root_position - world_origin_reference.global_position
	print("CTRL001_LOOP_BOUNDARY_FRAME frame=%s root_marker_relative=%s right_camera=%s" % [filename, marker_relative_root_position, right_camera.global_position])
	await SceneUtils.wait_frames(self, 2)
	await right_camera.capture_screenshot("%s/loop_boundary/%s.jpg" % [OUTPUT_ROOT, filename])

func _get_forward_animation_name(animation_tree: AnimationTree) -> StringName:
	var root_tree := animation_tree.tree_root as AnimationNodeBlendTree
	var states := root_tree.get_node(&"States") as AnimationNodeStateMachine
	var walking := states.get_node(&"Walking") as AnimationNodeBlendTree
	var locomotion := walking.get_node(&"Locomotion") as AnimationNodeBlendSpace2D
	for locomotion_point in locomotion.get_blend_point_count():
		if locomotion.get_blend_point_position(locomotion_point).is_equal_approx(FORWARD_MOVEMENT):
			var movement := locomotion.get_blend_point_node(locomotion_point) as AnimationNodeBlendSpace2D
			for movement_point in movement.get_blend_point_count():
				if movement.get_blend_point_position(movement_point).is_equal_approx(FORWARD_MOVEMENT):
					var forward_node := movement.get_blend_point_node(movement_point) as AnimationNodeAnimation
					return forward_node.animation
	SceneUtils.fatal_error_and_quit("Walking locomotion blend space does not define a forward clip")
	return &""

func _validate_camera_orientation(photobooth: Photobooth) -> void:
	var front := photobooth.get_camera_rig("FrontCamera")
	var right := photobooth.get_camera_rig("RightCamera")
	if front == null or right == null or front.global_position.z >= 0.0 or right.global_position.x <= 0.0:
		SceneUtils.fatal_error_and_quit("Locomotion camera labels do not match the scene coordinate frame")
		return
	front.orthogonal_scale = 3.0
	right.orthogonal_scale = 3.0
	print("CTRL001_CAMERA_SANITY front_z=%.3f right_x=%.3f avatar_forward_skeleton=+Z" % [front.global_position.z, right.global_position.x])
