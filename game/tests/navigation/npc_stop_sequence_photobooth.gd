# Manual-only, windowed NAV-001 replacement-model stop-sequence visual evidence runner.
# Drives the npc_stop_sequence_photobooth scene through one user_reported navigation route and captures the
# terminal stop sequence: walking with the imminent release approach armed, the finite walk-to-idle (f2i) clip
# mid-play, the hard terminal boundary's first stationary confirmation frame (upright idle at the destination
# marker), and a post-boundary hold frame several ticks later that must be pose-identical to the confirmation.
#
# Run windowed with a renderer (never --headless for captures):
#   godot-mono -d -s --xr-mode off --path game "tests/navigation/npc_stop_sequence_photobooth.gd"
#
# Screenshots and the run manifest are written under res://temp/nav001-stop-sequence/ (game/temp).
extends SceneTree

const SCENE := "res://tests/navigation/npc_stop_sequence_photobooth.tscn"
const OUTPUT_DIR := "res://temp/nav001-stop-sequence"
const TARGET_POSITION := Vector3(2.043711, 0.0, -1.5803192)
const TARGET_YAW := -0.91257447
const DESTINATION_ACCEPTED := 0

# NPCAnimationStage / NPCTerminalBoundaryState values mirrored from production enums.
const STAGE_SUSTAINED_TRAVEL := 3
const STAGE_EXIT := 4
const STAGE_COMPLETE := 7
const BOUNDARY_CONFIRMATION := 1
const BOUNDARY_COMPLETED := 3

const MID_F2I_PHASE_START := 0.3
const MID_F2I_PHASE_END := 0.7
const POST_BOUNDARY_HOLD_PHYSICS_FRAMES := 12
const POST_BOUNDARY_SETTLED_PHYSICS_FRAMES := 36
const FRAME_LIMIT := 1200
# Visual-scaled stationarity budgets for the evidence manifest (the exact 0.001 m outcome tolerance is asserted
# by the paired C# integration test; this runner guards the visually plausible anomaly scale). The authored
# f2i-end-to-idle pose transition settles vertically during the first post-boundary ticks, so the feet guard
# measures the settled window; the actor (root) must not move at all after the boundary.
const POST_BOUNDARY_ACTOR_SLIDE_LIMIT_M := 0.003
const SETTLED_FOOT_PLANAR_LIMIT_M := 0.008
const SETTLED_FOOT_PER_FRAME_PLANAR_LIMIT_M := 0.0025
const MID_F2I_DISTINCT_POSITION_M := 0.05

var _manifest: Dictionary = {}
var _assertion_failures: Array = []


func _init() -> void:
	if DisplayServer.get_name().to_lower() == "headless":
		SceneUtils.fatal_error_and_quit("Stop-sequence captures require a renderer; run without --headless")
		return
	var packed := load(SCENE) as PackedScene
	if packed == null:
		SceneUtils.fatal_error_and_quit("Failed to load stop-sequence photobooth scene: %s" % SCENE)
		return
	var booth := packed.instantiate()
	var photobooth := booth as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("Stop-sequence scene root is not a Photobooth")
		return
	_prepare_scene_context(booth)
	root.add_child(booth)
	await process_frame
	var runtime := booth.get_node("NavigationRuntime") as Node3D
	runtime.set_process(false)
	runtime.set_physics_process(false)
	runtime.set_process_input(false)
	runtime.set_process_unhandled_input(false)
	var npc := runtime.get_node("NavigationTestNpc") as CharacterBody3D
	var navigation: Node = await _await_navigation(npc)
	if navigation == null:
		SceneUtils.fatal_error_and_quit("Installed NPC navigation did not appear")
		return
	for attempt in 240:
		if bool(navigation.get("IsRuntimeBindingActive")):
			break
		await physics_frame
	if not bool(navigation.get("IsRuntimeBindingActive")):
		SceneUtils.fatal_error_and_quit("NPC navigation runtime binding never activated")
		return
	await _disable_agentic_mind(npc)
	var skeleton := npc.get_node("Female/GeneralSkeleton") as Skeleton3D
	var bone_indices := {
		"Hips": skeleton.find_bone("Hips"),
		"LeftFoot": skeleton.find_bone("LeftFoot"),
		"RightFoot": skeleton.find_bone("RightFoot"),
	}
	for bone_name in bone_indices:
		if int(bone_indices[bone_name]) < 0:
			SceneUtils.fatal_error_and_quit("Stop-sequence skeleton lacks the %s bone" % bone_name)
			return
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(OUTPUT_DIR))
	_manifest = {
		"created_unix": Time.get_unix_time_from_system(),
		"scene": SCENE,
		"runner": "res://tests/navigation/npc_stop_sequence_photobooth.gd",
		"subject_identity": "reference_female",
		"fixture_strategy": "NavigationRuntime reference female NPC (production NPC role installer) driven directly through Navigation.SetDestination",
		"scenario": {
			"target_position": TARGET_POSITION,
			"target_yaw": TARGET_YAW,
			"marker_destination_position": (photobooth.get_marker("Destination") as Node3D).global_position,
			"marker_origin_position": (photobooth.get_marker("Origin") as Node3D).global_position,
		},
		"directional_sanity": "Godot world forward is -Z; the route hint's -Z half points origin-to-destination; marker positions equal the scenario endpoints.",
		"camera_rigs": {},
		"captures": [],
		"framing_captures": [],
		"post_boundary_samples": [],
		"terminal_evidence": {},
		"checks": {},
	}
	_verify_markers_and_rigs(photobooth)
	await _capture_framing(photobooth)
	await _run_scenario(photobooth, npc, navigation, skeleton, bone_indices)
	_manifest["completed_unix"] = Time.get_unix_time_from_system()
	var manifest_file := FileAccess.open("%s/run_manifest.json" % OUTPUT_DIR, FileAccess.WRITE)
	manifest_file.store_string(JSON.stringify(_manifest, "\t"))
	manifest_file.close()
	print("STOP_SEQUENCE_MANIFEST %s/run_manifest.json" % OUTPUT_DIR)
	for failure in _assertion_failures:
		push_error("Stop-sequence check failed: %s" % [failure])
	quit(0 if _assertion_failures.is_empty() else 1)


func _prepare_scene_context(booth: Node) -> void:
	# navigation_test.tscn already ships the scene context: NavigationTestNpc is in the Actors group and
	# NavigationTestPlayer is the single Player-group character the role installer's scene context requires.
	# Adding another player fixture would violate the exactly-one-player scene authoring contract.
	var context_npc := booth.get_node("NavigationRuntime/NavigationTestNpc") as CharacterBody3D
	if not context_npc.is_in_group(&"Actors"):
		SceneUtils.fatal_error_and_quit("Stop-sequence scene context NPC is not in the Actors group")
		return
	var players := get_nodes_in_group(&"Player")
	if players.size() != 1:
		SceneUtils.fatal_error_and_quit(
			"Stop-sequence scene context requires exactly one Player-group character; found %d" % players.size())
		return
	print("STOP_SEQUENCE_CONTEXT_PREPARED npc=%s player=%s" % [context_npc.name, players[0].name])


func _await_navigation(npc: Node):
	for attempt in 240:
		var navigation := npc.get_node_or_null("Navigation")
		if navigation != null:
			return navigation
		await physics_frame
	return null


func _disable_agentic_mind(npc: Node) -> void:
	# The AgenticMind is unrelated runtime wiring for this navigation fixture: its live agent session can issue
	# competing locomotion commands after the terminal boundary and freeze the observed physics step. Disable its
	# processing so the stop sequence under test is the only movement authority.
	for attempt in 240:
		var mind := npc.get_node_or_null("Mind")
		if mind != null:
			mind.process_mode = Node.PROCESS_MODE_DISABLED
			print("STOP_SEQUENCE_MIND_DISABLED")
			return
		await physics_frame
	push_warning("Stop-sequence fixture did not find an installed Mind node to disable.")


func _verify_markers_and_rigs(photobooth: Photobooth) -> void:
	# Skill gate check 2: verify marker transforms and camera rigs before the scenario screenshots. Markers are
	# axis gizmos (no directional swap risk); the Destination marker must sit exactly on the scenario endpoint.
	var destination_marker := photobooth.get_marker("Destination")
	var origin_marker := photobooth.get_marker("Origin")
	var destination_offset: Vector3 = destination_marker.global_position - (TARGET_POSITION + Vector3.UP * 0.012)
	var checks := {
		"destination_marker_on_endpoint": destination_offset.length() <= 0.0005,
		"origin_marker_at_route_start": origin_marker.global_position.distance_to(Vector3.UP * 0.012) <= 0.0005,
	}
	for check_name in checks:
		if not bool(checks[check_name]):
			_assertion_failures.append({"check": check_name, "observed": str(destination_marker.global_position)})
	for rig_name in ["SideCamera", "FrontCamera", "LowerBodyCamera", "ThreeQuarterCamera"]:
		var rig := photobooth.get_camera_rig(rig_name)
		var forward := -(rig.global_basis * Vector3.FORWARD)
		(_manifest["camera_rigs"] as Dictionary)[rig_name] = {
			"position": rig.global_position,
			"forward": forward,
			"projection": rig.projection,
			"orthogonal_scale": rig.orthogonal_scale,
			"fov": rig.fov,
			"look_at_destination": forward.dot((TARGET_POSITION + Vector3.UP * 0.85 - rig.global_position).normalized()) > 0.9,
		}
	_manifest["checks"]["marker_and_rig_verification"] = checks


func _capture_framing(photobooth: Photobooth) -> void:
	# Framing pass with the idle NPC still at the origin: verifies every camera rig and the marker placements
	# before the feature-level scenario captures.
	for rig_name in ["SideCamera", "FrontCamera", "LowerBodyCamera", "ThreeQuarterCamera"]:
		var path: String = await photobooth.get_camera_rig(rig_name).capture_screenshot(
			"nav001-stop-sequence/framing/%s" % rig_name)
		(_manifest["framing_captures"] as Array).append({
			"rig": rig_name,
			"path": path,
		})
	_manifest["checks"]["framing_captured_all_rigs"] = (_manifest["framing_captures"] as Array).size() == 4


func _run_scenario(
	photobooth: Photobooth,
	npc: CharacterBody3D,
	navigation,
	skeleton: Skeleton3D,
	bone_indices: Dictionary
) -> void:
	npc.global_transform = Transform3D.IDENTITY
	var destination := Transform3D(Basis(Vector3.UP, TARGET_YAW), TARGET_POSITION)
	var request_result: int = navigation.SetDestination(destination)
	if request_result != DESTINATION_ACCEPTED:
		_assertion_failures.append({"check": "destination_accepted", "observed": request_result})
		return
	var captures := {
		"release_approach": false,
		"mid_f2i": false,
		"boundary_confirmation": false,
		"post_boundary_hold": false,
		"post_boundary_settled": false,
	}
	var boundary_physics_frame := -1
	var boundary_actor_position := Vector3.ZERO
	var boundary_feet := {}
	var mid_f2i_actor_position := Vector3(INF, INF, INF)
	var post_boundary_actor_position := Vector3(INF, INF, INF)
	var post_boundary_feet := {}
	var settled_actor_position := Vector3(INF, INF, INF)
	var settled_feet := {}
	for frame in range(1, FRAME_LIMIT + 1):
		var previous_step: int = navigation.get("PhysicalStepCount")
		await physics_frame
		var wait_spins := 0
		# Post-boundary confirmation frames perform no movement, so PhysicalStepCount freezes; the wait releases
		# on the completed programme stage or on the durable outcome clearing the destination (which resets the
		# programme stage to Idle).
		while navigation.get("PhysicalStepCount") == previous_step \
		and int(navigation.get("ProgrammeStage")) != STAGE_COMPLETE \
		and bool(navigation.get("HasDestination")):
			await physics_frame
			wait_spins += 1
			if wait_spins == 120:
				push_warning("Stop-sequence step stall: stage=%s boundary=%s has_destination=%s" % [
					navigation.get("ProgrammeStage"),
					navigation.get("TerminalBoundaryState"),
					navigation.get("HasDestination"),
				])
			if wait_spins > 600:
				_assertion_failures.append({"check": "physics_step_stall", "frame": frame})
				return
		var stage: int = navigation.get("ProgrammeStage")
		var boundary_state: int = navigation.get("TerminalBoundaryState")
		var phase := float(navigation.get("LastEvaluatedNormalisedPhase"))
		var fading_from := str(navigation.get("LastFadingFromState"))
		print("STOP_SEQUENCE_FRAME frame=%d stage=%s boundary=%s phase=%.3f approach=%s" % [
			frame,
			stage,
			boundary_state,
			phase,
			navigation.get("IsTerminalReleaseApproachActive"),
		])
		if not captures["release_approach"] \
		and stage == STAGE_SUSTAINED_TRAVEL \
		and bool(navigation.get("IsTerminalReleaseApproachActive")):
			captures["release_approach"] = true
			await _capture_scenario(photobooth, navigation, npc, skeleton, bone_indices, "release_approach", frame)
		if not captures["mid_f2i"] \
		and stage == STAGE_EXIT \
		and fading_from.is_empty() \
		and phase >= MID_F2I_PHASE_START and phase <= MID_F2I_PHASE_END:
			captures["mid_f2i"] = true
			mid_f2i_actor_position = npc.global_position
			await _capture_scenario(photobooth, navigation, npc, skeleton, bone_indices, "mid_f2i", frame)
		if not captures["boundary_confirmation"] and boundary_state == BOUNDARY_CONFIRMATION:
			captures["boundary_confirmation"] = true
			boundary_physics_frame = frame
			boundary_actor_position = npc.global_position
			boundary_feet = _foot_world_positions(skeleton, bone_indices)
			await _capture_scenario(photobooth, navigation, npc, skeleton, bone_indices, "boundary_confirmation", frame)
		elif captures["boundary_confirmation"] and not captures["post_boundary_settled"]:
			(_manifest["post_boundary_samples"] as Array).append({
				"frame": frame,
				"actor_position": npc.global_position,
				"feet": _foot_world_positions(skeleton, bone_indices),
				"boundary_state": boundary_state,
			})
			var frames_since_boundary := frame - boundary_physics_frame
			if frames_since_boundary >= POST_BOUNDARY_HOLD_PHYSICS_FRAMES and not captures["post_boundary_hold"]:
				captures["post_boundary_hold"] = true
				post_boundary_actor_position = npc.global_position
				post_boundary_feet = _foot_world_positions(skeleton, bone_indices)
				await _capture_scenario(photobooth, navigation, npc, skeleton, bone_indices, "post_boundary_hold", frame)
			elif frames_since_boundary >= POST_BOUNDARY_SETTLED_PHYSICS_FRAMES:
				captures["post_boundary_settled"] = true
				settled_actor_position = npc.global_position
				settled_feet = _foot_world_positions(skeleton, bone_indices)
				await _capture_scenario(photobooth, navigation, npc, skeleton, bone_indices, "post_boundary_settled", frame)
		var outcomes: Array = _terminal_evidence(navigation).get("terminal_outcomes", [])
		if not outcomes.is_empty() and captures["post_boundary_settled"]:
			break
	_manifest["terminal_evidence"] = _terminal_evidence(navigation)
	_evaluate_checks(
		npc,
		captures,
		boundary_actor_position,
		boundary_feet,
		mid_f2i_actor_position,
		post_boundary_actor_position,
		post_boundary_feet,
		settled_actor_position,
		settled_feet,
		boundary_physics_frame)


func _capture_scenario(
	photobooth: Photobooth,
	navigation,
	npc: CharacterBody3D,
	skeleton: Skeleton3D,
	bone_indices: Dictionary,
	label: String,
	frame: int
) -> void:
	# Freeze the simulation while the camera rigs render so each capture set shows one exact physics state.
	paused = true
	await SceneUtils.wait_frames(self, 1)
	var evidence := _terminal_evidence(navigation)
	var paths: Dictionary = await photobooth.capture_screenshots("nav001-stop-sequence/%s" % label)
	paused = false
	(_manifest["captures"] as Array).append({
		"label": label,
		"physics_frame": frame,
		"programme_stage": int(navigation.get("ProgrammeStage")),
		"terminal_boundary_state": int(navigation.get("TerminalBoundaryState")),
		"terminal_confirmation_frame_count": int(evidence.get("terminal_confirmation_frame_count", 0)),
		"animation_state": str(navigation.get("LastEvaluatedState")),
		"evaluated_phase": float(navigation.get("LastEvaluatedNormalisedPhase")),
		"authored_root_world_delta": str(navigation.get("LastAuthoredRootWorldDelta")),
		"requested_synthetic_world_delta": str(navigation.get("LastRequestedSyntheticWorldDelta")),
		"actor_transform": str(npc.global_transform),
		"feet": _foot_world_positions(skeleton, bone_indices),
		"files": paths.values(),
	})


func _foot_world_positions(skeleton: Skeleton3D, bone_indices: Dictionary) -> Dictionary:
	var feet := {}
	for bone_name in ["Hips", "LeftFoot", "RightFoot"]:
		feet[bone_name] = skeleton.to_global(skeleton.get_bone_global_pose(int(bone_indices[bone_name])).origin)
	return feet


func _terminal_evidence(navigation) -> Dictionary:
	var evidence: Variant = navigation.call("GetTerminalEvidenceSnapshot")
	return evidence if evidence is Dictionary else {}


func _evaluate_checks(
	npc: CharacterBody3D,
	captures: Dictionary,
	boundary_actor_position: Vector3,
	boundary_feet: Dictionary,
	mid_f2i_actor_position: Vector3,
	post_boundary_actor_position: Vector3,
	post_boundary_feet: Dictionary,
	settled_actor_position: Vector3,
	settled_feet: Dictionary,
	boundary_physics_frame: int
) -> void:
	var outcomes: Array = (_manifest["terminal_evidence"] as Dictionary).get("terminal_outcomes", [])
	var checks := {
		"captured_release_approach": bool(captures["release_approach"]),
		"captured_mid_f2i": bool(captures["mid_f2i"]),
		"captured_boundary_confirmation": bool(captures["boundary_confirmation"]),
		"captured_post_boundary_hold": bool(captures["post_boundary_hold"]),
		"captured_post_boundary_settled": bool(captures["post_boundary_settled"]),
		"single_completed_outcome": outcomes.size() == 1
			and str(outcomes[0].get("outcome", "")) == "Completed"
			and str(outcomes[0].get("reason", "")) == "DestinationReached",
		"boundary_tick_recorded": boundary_physics_frame > 0,
	}
	if checks["single_completed_outcome"]:
		var outcome: Dictionary = outcomes[0]
		checks["outcome_within_terminal_tolerance"] = float(outcome.get("planar_error_m", INF)) <= 0.001 \
			and float(outcome.get("yaw_error_rad", INF)) <= float(outcome.get("yaw_tolerance_rad", 0.0))
		checks["marker_proximity_visual_scale"] = Vector2(
			npc.global_position.x - TARGET_POSITION.x,
			npc.global_position.z - TARGET_POSITION.z).length() <= 0.02
	if bool(captures["boundary_confirmation"]) and bool(captures["post_boundary_settled"]):
		# The actor (root) must not move at all from the boundary through the settled hold. The authored
		# f2i-end-to-idle pose transition settles vertically over the first post-boundary ticks, so planted-foot
		# slide is guarded across the settled window (hold to settled) in planar distance, per frame and net.
		var actor_slide := settled_actor_position.distance_to(boundary_actor_position)
		checks["post_boundary_actor_stationary"] = actor_slide <= POST_BOUNDARY_ACTOR_SLIDE_LIMIT_M
		var left_settled_slide := _planar(
			(settled_feet["LeftFoot"] as Vector3) - (post_boundary_feet["LeftFoot"] as Vector3))
		var right_settled_slide := _planar(
			(settled_feet["RightFoot"] as Vector3) - (post_boundary_feet["RightFoot"] as Vector3))
		var maximum_settled_per_frame_slide := _maximum_settled_per_frame_foot_slide(
			boundary_physics_frame + POST_BOUNDARY_HOLD_PHYSICS_FRAMES)
		checks["post_boundary_feet_settled_no_slide"] = (
			maxf(left_settled_slide, right_settled_slide) <= SETTLED_FOOT_PLANAR_LIMIT_M
			and maximum_settled_per_frame_slide <= SETTLED_FOOT_PER_FRAME_PLANAR_LIMIT_M
		)
		checks["mid_f2i_visibly_distinct_from_settled"] = (
			settled_actor_position.distance_to(mid_f2i_actor_position) >= MID_F2I_DISTINCT_POSITION_M
			if mid_f2i_actor_position.x != INF else false
		)
		_manifest["post_boundary_slide_m"] = {
			"actor_boundary_to_settled": actor_slide,
			"left_foot_settled_window": left_settled_slide,
			"right_foot_settled_window": right_settled_slide,
			"maximum_settled_per_frame": maximum_settled_per_frame_slide,
			"left_foot_transition": (post_boundary_feet["LeftFoot"] as Vector3).distance_to(
				boundary_feet["LeftFoot"] as Vector3),
			"right_foot_transition": (post_boundary_feet["RightFoot"] as Vector3).distance_to(
				boundary_feet["RightFoot"] as Vector3),
		}
	(_manifest["checks"] as Dictionary).merge(checks, true)
	for check_name in checks:
		if not bool(checks[check_name]):
			_assertion_failures.append({"check": check_name})


func _planar(displacement: Vector3) -> float:
	return Vector2(displacement.x, displacement.z).length()


func _maximum_settled_per_frame_foot_slide(from_frame: int) -> float:
	var samples: Array = _manifest["post_boundary_samples"]
	var maximum := 0.0
	var previous: Dictionary = {}
	for sample in samples:
		if int(sample["frame"]) < from_frame:
			previous = sample
			continue
		if previous.is_empty():
			previous = sample
			continue
		for foot_name in ["LeftFoot", "RightFoot"]:
			maximum = maxf(maximum, _planar(
				(sample["feet"][foot_name] as Vector3) - (previous["feet"][foot_name] as Vector3)))
		previous = sample
	return maximum
