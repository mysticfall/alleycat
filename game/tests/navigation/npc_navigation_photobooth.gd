extends SceneTree

# Manual-only, windowed navigation evidence tool; automated tests use synthetic fixtures instead.
const SCENE := "res://tests/navigation/npc_navigation_photobooth.tscn"
const CATALOGUE := "res://assets/characters/reference/female/animations/runtime/locomotion/sad/locomotion_sad_catalogue.json"
const OUTPUT_ROOT := "res://temp/NAV-001"
const FEMALE_BASELINE := "res://assets/characters/reference/female/reference_female.blend"
const FEMALE_TEMPLATE := "res://assets/characters/templates/reference_female/reference_female_npc.tscn"
const MALE_BASELINE := "res://assets/characters/reference/male/reference_male.blend"
const MALE_TEMPLATE := "res://assets/characters/templates/reference_male/reference_male_npc.tscn"
const MALE_PRODUCTION_NPC := "res://assets/characters/reference/vadim_npc.tscn"
const FEMALE_GRAPH := "res://assets/characters/templates/animation/animation_tree_root_npc.tres"
const MALE_GRAPH := "res://assets/characters/templates/animation/animation_tree_root_reference_male_npc.tres"
const STAGE_IDLE := 0
const STAGE_ENTRY := 1
const STAGE_SHORT_BOUT := 2
const STAGE_SUSTAINED_TRAVEL := 3
const STAGE_EXIT := 4
const STAGE_SETTLEMENT := 5
const STAGE_ENDPOINT_HOLD := 6
const STAGE_COMPLETE := 7
const STAGE_NAMES := {
	STAGE_IDLE: "Idle",
	STAGE_ENTRY: "Entry",
	STAGE_SHORT_BOUT: "ShortBout",
	STAGE_SUSTAINED_TRAVEL: "SustainedTravel",
	STAGE_EXIT: "Exit",
	STAGE_SETTLEMENT: "Settlement",
	STAGE_ENDPOINT_HOLD: "EndpointHold",
	STAGE_COMPLETE: "Complete",
}
const CAPTURE_STAGES := {
	STAGE_ENTRY: "entry",
	STAGE_SUSTAINED_TRAVEL: "travel",
	STAGE_EXIT: "exit",
	STAGE_SETTLEMENT: "settlement",
	STAGE_COMPLETE: "complete",
}
const CAPTURE_STAGE_FRAMES := [3, 9, 17, 25]
const VIEWS := ["three_quarter", "side", "lower_body", "tracking"]
const REVERSAL_SPEED_THRESHOLD := 0.06
const REVERSAL_SUSTAINED_FRAMES := 4
const REVERSAL_TRACE_RADIUS := 8
const RELEASE_PHASE_ERROR_TOLERANCE := 0.002
const SUPPORT_PHASE_START := 0.06
const SUPPORT_PHASE_END := 0.12
const SUPPORT_HEIGHT_TOLERANCE_M := 0.015
const SUPPORT_VERTICAL_SPEED_TOLERANCE_MPS := 0.10
const VISIBLE_PLANTED_SLIP_PER_FRAME_M := 0.005
const VISIBLE_PLANTED_SLIP_PER_CONTACT_INTERVAL_M := 0.015
const TERMINAL_OVERSHOOT_MATERIAL_LIMIT_M := 0.015
const SUPPORT_CAPTURE_START := 62
const SUPPORT_CAPTURE_END := 66
const CANDIDATE_VIEWS := ["candidate_side", "candidate_top_route", "candidate_lower_body"]
const CONTACT_LOCK_CAPTURE_FLAG := "--contact-lock-f2i-l-diagnostic-only"
const CONTACT_LOCK_LATE_SUPPORT_PHASE := 0.35
const CONTACT_LOCK_RAW_ANCHOR_SEPARATION_M := 0.025
const CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M := 0.015
const SCENARIOS := {
	"behind": [Vector3(-0.6888549, 0.0, 2.2608037), 2.8458345],
	"short_diagonal": [Vector3(-0.6938858, 0.0, -0.8398442), 0.6905182],
	"shallow_right": [Vector3(0.7323381, 0.0, -3.1248817), -0.23020257],
	"exact_reported": [Vector3(-2.1925995, 0.0, -2.9508696), 0.6390286],
	"user_reported": [Vector3(2.043711, 0.0, -1.5803192), -0.91257447],
	"continuity_reported": [Vector3(1.0720425, 0.0, -1.7897065), -0.53968734],
}

var _run_id := ""
var _run_dir := ""
var _manifest: Dictionary = {}
var _clips: Dictionary = {}
var _subject_identity := "reference_female"
var _template_evidence := {}


func _init() -> void:
	if "--support-contact-mutation-check-only" in OS.get_cmdline_user_args():
		var mutation_guards := _support_contact_mutation_guards()
		var failed_guards: Array[String] = []
		for guard_name in mutation_guards:
			if not bool((mutation_guards[guard_name] as Dictionary)["failed_as_expected"]):
				failed_guards.append(guard_name)
		print("SUPPORT_CONTACT_MUTATION_CHECK passed=%s failed_guards=%s" % [
			failed_guards.is_empty(),
			",".join(failed_guards),
		])
		quit(0 if failed_guards.is_empty() else 1)
		return
	_run_id = Time.get_datetime_string_from_system(true).replace("-", "").replace(":", "").replace("T", "-")
	_run_dir = "%s/%s" % [OUTPUT_ROOT, _run_id]
	_clips = _load_clips()
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(_run_dir))
	var packed := load(SCENE) as PackedScene
	if packed == null:
		quit(1)
		return
	var booth := packed.instantiate()
	var male_exact := "--male-exact-only" in OS.get_cmdline_user_args()
	if male_exact:
		_subject_identity = "reference_male"
		var selectable_runtime := booth.get_node("NavigationRuntime") as Node3D
		selectable_runtime.set("CharacterVariant", 1)
		selectable_runtime.call("ApplyCharacterVariantSelection")
		_template_evidence = {
			"fixture_strategy": "navigation_test explicit ReferenceMale variant using the production Vadim NPC scene, reference male template and production role installer",
			"production_scene_path": MALE_PRODUCTION_NPC,
			"production_scene_uid": _resource_uid(MALE_PRODUCTION_NPC),
			"baseline_path": MALE_BASELINE,
			"baseline_uid": _resource_uid(MALE_BASELINE),
			"template_path": MALE_TEMPLATE,
			"template_uid": _resource_uid(MALE_TEMPLATE),
		}
	else:
		_template_evidence = {
			"fixture_strategy": "navigation_test authored ReferenceFemale default using the production reference female NPC template and role installer",
			"baseline_path": FEMALE_BASELINE,
			"baseline_uid": _resource_uid(FEMALE_BASELINE),
			"template_path": FEMALE_TEMPLATE,
			"template_uid": _resource_uid(FEMALE_TEMPLATE),
		}
	# The NPC role installer builds its scene context from the Actors/Player groups. navigation_test.tscn already
	# ships NavigationTestNpc in the Actors group and NavigationTestPlayer as the single Player-group character;
	# adding another player fixture would violate the exactly-one-player scene authoring contract.
	var context_npc := booth.get_node("NavigationRuntime/NavigationTestNpc") as CharacterBody3D
	context_npc.add_to_group(&"Actors")
	var context_player := booth.get_node("NavigationRuntime/NavigationTestPlayer") as CharacterBody3D
	if not context_npc.is_in_group(&"Actors") or not context_player.is_in_group(&"Player"):
		push_error("Navigation photobooth could not prepare the NPC and player scene-context groups before role installation.")
		quit(1)
		return
	print("NAVIGATION_PHOTOBOOTH_CONTEXT_PREPARED npc=%s player=%s" % [context_npc.name, context_player.name])
	root.add_child(booth)
	await process_frame
	var actors := get_nodes_in_group(&"Actors")
	if context_npc not in actors or context_player not in actors:
		push_error("Navigation photobooth did not initialise the NPC and player Actors scene context before capture.")
		quit(1)
		return
	print("NAVIGATION_PHOTOBOOTH_CONTEXT_INITIALISED npc=%s player=%s" % [context_npc.get_path(), context_player.get_path()])
	var runtime := booth.get_node("NavigationRuntime") as Node3D
	runtime.set_process(false)
	runtime.set_physics_process(false)
	runtime.set_process_input(false)
	runtime.set_process_unhandled_input(false)
	var camera := runtime.get_node("Camera3D") as Camera3D
	camera.current = true
	camera.fov = 44.0
	var npc := runtime.get_node("NavigationTestNpc") as CharacterBody3D
	var navigation := npc.get_node_or_null("Navigation")
	for attempt in 120:
		if navigation != null:
			break
		await physics_frame
		navigation = npc.get_node_or_null("Navigation")
	if navigation == null:
		push_error("Installed NPC navigation did not appear")
		quit(1)
		return
	if not is_equal_approx(navigation.get("ObjectiveReversalMinimumSpeed"), REVERSAL_SPEED_THRESHOLD) \
	or navigation.get("ObjectiveReversalRequiredConsecutiveFrames") != REVERSAL_SUSTAINED_FRAMES:
		push_error("Photobooth reversal trace thresholds differ from NPCNavigation's tested detector")
		quit(1)
		return
	var skeleton_name := "Male" if male_exact else "Female"
	var skeleton := npc.get_node("%s/GeneralSkeleton" % skeleton_name) as Skeleton3D
	var animation_tree := npc.get_node("AnimationTree") as AnimationTree
	var animation_player := npc.get_node("AnimationPlayer") as AnimationPlayer
	for attempt in 120:
		if bool(navigation.get("IsRuntimeBindingActive")):
			break
		await physics_frame
	var graph_path := animation_tree.tree_root.resource_path
	var graph_uid_value := ResourceLoader.get_resource_uid(graph_path)
	var source_graph_path := MALE_GRAPH if male_exact else FEMALE_GRAPH
	_template_evidence["animation_graph_source_path"] = source_graph_path
	_template_evidence["animation_graph_source_uid"] = _resource_uid(source_graph_path)
	_template_evidence["runtime_graph_resource_path"] = graph_path
	_template_evidence["runtime_graph_uid"] = ResourceUID.id_to_text(graph_uid_value) if graph_uid_value >= 0 else "runtime-local-duplicate"
	_template_evidence["runtime_graph_is_local_duplicate"] = graph_path.is_empty()
	_template_evidence["animation_tree_active"] = animation_tree.active
	_template_evidence["animation_tree_process_mode"] = animation_tree.process_mode
	_template_evidence["animation_tree_callback_mode"] = animation_tree.callback_mode_process
	_template_evidence["animation_tree_root_node"] = str(animation_tree.root_node)
	_template_evidence["animation_player_root_node"] = str(animation_player.root_node)
	_template_evidence["animation_libraries"] = animation_player.get_animation_library_list()
	_template_evidence["root_motion_track"] = str(animation_tree.root_motion_track)
	_template_evidence["output_chain_connected"] = _output_chain_connected(animation_tree)
	_template_evidence["idle_track_bindings"] = _track_binding_summary(animation_player, skeleton, &"idle/idle_sad")
	_template_evidence["entry_track_bindings"] = _track_binding_summary(animation_player, skeleton, &"locomotion/loc_sad_i2f_l")
	_template_evidence["sustained_track_bindings"] = _track_binding_summary(animation_player, skeleton, &"locomotion/loc_sad_wf")
	_template_evidence["navigation_runtime_binding_active"] = navigation.get("IsRuntimeBindingActive")
	_template_evidence["navigation_actor"] = str(navigation.get("Actor"))
	_template_evidence["navigation_animation_tree"] = str(navigation.get("AnimationTree"))
	_template_evidence["navigation_root_motion_reference"] = str(navigation.get("RootMotionReference"))
	_template_evidence["installer_present"] = npc.get_node_or_null("NPCCharacterInstaller") != null
	var bone_indices := {
		"Root": skeleton.find_bone("Root"),
		"Hips": skeleton.find_bone("Hips"),
		"LeftFoot": skeleton.find_bone("LeftFoot"),
		"RightFoot": skeleton.find_bone("RightFoot"),
		"LeftUpperArm": skeleton.find_bone("LeftUpperArm"),
		"RightUpperArm": skeleton.find_bone("RightUpperArm"),
	}
	_manifest = {
		"run_id": _run_id,
		"created_unix": Time.get_unix_time_from_system(),
		"capture_provenance": _capture_provenance(),
		"scene": SCENE,
		"runner": "res://tests/navigation/npc_navigation_photobooth.gd",
		"subject_identity": _subject_identity,
		"selected_character_variant": "ReferenceMale" if male_exact else "ReferenceFemale",
		"template_evidence": _template_evidence,
		"identity_forward_world": Vector3.FORWARD,
		"directional_sanity": "Identity actor forward is world -Z; each destination marker equals the scenario world endpoint; route arrow -Z points origin-to-destination.",
		"views": VIEWS,
		"captures": [],
		"absent_captures": [],
		"physical_metrics": [],
		"reversal_threshold": {
			"acceptance_frame": "stable route tangent; positive displacement advances along the accepted terminal route",
			"minimum_realised_longitudinal_speed_mps": REVERSAL_SPEED_THRESHOLD,
			"required_consecutive_frames": REVERSAL_SUSTAINED_FRAMES,
			"trace_radius_frames": REVERSAL_TRACE_RADIUS,
			"rationale": "0.06 m/s equals 1 mm per 60 Hz frame, above prior 0.02 m/s sign noise yet below 8% of ANIM-002's 0.73-1.05 m/s forward Root response; four frames (67 ms) rejects one-frame stage seams without hiding a visible correction pulse.",
		},
		"reversal_candidates": [],
		"assertion_failures": [],
		"release_timing_contract": {
			"maximum_phase_error": RELEASE_PHASE_ERROR_TOLERANCE,
			"maximum_time_error_seconds": 1.0 / float(Engine.physics_ticks_per_second),
			"rationale": "The replacement model latches LastTerminalCommitPhase from the sustained cycle phase observed on the crossing tick, so the latched commit phase may differ from the authored target release phase by the crossing evaluation's sub-tick quantisation (observed ~1e-4). The bound stays two orders of magnitude below one physics interval of cycle phase (~0.017) and still rejects a wrong-occurrence commit (order ~0.5); elapsed-time prediction may differ by at most one physics evaluation interval because the gate observes AnimationTree phase after discrete advancement.",
		},
		"support_contact_contract": {
			"phase_start": SUPPORT_PHASE_START,
			"phase_end": SUPPORT_PHASE_END,
			"maximum_height_m": SUPPORT_HEIGHT_TOLERANCE_M,
			"maximum_vertical_speed_mps": SUPPORT_VERTICAL_SPEED_TOLERANCE_MPS,
			"visible_slip_per_frame_m": VISIBLE_PLANTED_SLIP_PER_FRAME_M,
			"rationale": "Stage 1 retains Navigation.PhaseLane and LastEvaluatedNormalisedPhase only as diagnostics. This runner has no ANIM-002 authored-contact evidence or immutable IK-003 post-composition snapshot, so it cannot select a support foot, qualify contact, or pass a support assessment.",
			"assessment_status": "unavailable_stage1",
			"required_authoritative_source": "A future ANIM-002 authored-contact publication and/or immutable IK-003 post-composition snapshot.",
			"evidence_channels": {
				"navigation_phase_diagnostics": "Navigation.PhaseLane and LastEvaluatedNormalisedPhase are diagnostic-only and are never contact evidence or permission.",
				"pre_ik_pose_channel": "unavailable: this runner does not expose a pre-IK pose tap.",
				"post_ik_pose_channel": "unavailable: this runner does not expose a post-IK composition tap or make a Stage 2 foot-lock claim.",
				"skeleton_world_pose_observation": "Observed Skeleton3D world pose after animation evaluation; its IK composition status is intentionally unspecified.",
			},
			"mutation_guards": _support_contact_mutation_guards(),
		},
		"terminal_overshoot_contract": {
			"material_limit_m": TERMINAL_OVERSHOOT_MATERIAL_LIMIT_M,
			"rationale": "The existing 15 mm declared-contact interval budget is the smallest existing spatial continuity limit that captures a material terminal passage without inventing a fixture-specific destination tolerance.",
		},
		"raw_sign_change_candidates": [],
		"candidate_replay": {
			"scenario": "behind",
			"accepted_support_frame_start": SUPPORT_CAPTURE_START,
			"accepted_support_frame_end": SUPPORT_CAPTURE_END,
			"views": CANDIDATE_VIEWS,
			"frames": [],
		},
	}
	for guard_name in ((_manifest["support_contact_contract"] as Dictionary)["mutation_guards"] as Dictionary):
		var guard: Dictionary = ((_manifest["support_contact_contract"] as Dictionary)["mutation_guards"] as Dictionary)[guard_name]
		if not bool(guard["failed_as_expected"]):
			(_manifest["assertion_failures"] as Array).append({"contract": "support_contact_mutation_guard", "guard": guard_name, "result": guard})
	var trace := FileAccess.open("%s/runtime_metrics.jsonl" % _run_dir, FileAccess.WRITE)
	var scenario_names: Array = (
		["user_reported"] if CONTACT_LOCK_CAPTURE_FLAG in OS.get_cmdline_user_args()
		else ["continuity_reported"] if "--continuity-reported-only" in OS.get_cmdline_user_args()
		else ["user_reported"] if "--user-reported-only" in OS.get_cmdline_user_args()
		else ["exact_reported"] if "--exact-only" in OS.get_cmdline_user_args() or male_exact
		else ["behind"] if "--behind-only" in OS.get_cmdline_user_args()
		else SCENARIOS.keys()
	)
	for scenario_name in scenario_names:
		await _run_scenario(
			scenario_name,
			SCENARIOS[scenario_name],
			runtime,
			camera,
			npc,
			navigation,
			skeleton,
			bone_indices,
			animation_tree,
			animation_player,
			trace)
	trace.close()
	_finalise_capture_provenance()
	_manifest["completed_unix"] = Time.get_unix_time_from_system()
	_manifest["metrics_sha256"] = FileAccess.get_sha256("%s/runtime_metrics.jsonl" % _run_dir)
	var manifest_path := "%s/run_manifest.json" % _run_dir
	var manifest_file := FileAccess.open(manifest_path, FileAccess.WRITE)
	manifest_file.store_string(JSON.stringify(_manifest, "\t"))
	manifest_file.close()
	var current_run := FileAccess.open("%s/current_run.txt" % OUTPUT_ROOT, FileAccess.WRITE)
	current_run.store_string(_run_id)
	current_run.close()
	quit(0 if (_manifest["assertion_failures"] as Array).is_empty() else 1)


func _resource_uid(path: String) -> String:
	var value := ResourceLoader.get_resource_uid(path)
	return ResourceUID.id_to_text(value) if value >= 0 else "unresolved"


func _track_binding_summary(player: AnimationPlayer, skeleton: Skeleton3D, animation_name: StringName) -> Dictionary:
	var animation := player.get_animation(animation_name)
	var bone_tracks := 0
	var resolved_bone_tracks := 0
	var missing_bones: Array[String] = []
	for track in animation.get_track_count():
		var path := animation.track_get_path(track)
		if path.get_subname_count() == 0:
			continue
		if not String(path).begins_with("%GeneralSkeleton:"):
			continue
		var bone_name := String(path.get_subname(0))
		bone_tracks += 1
		if skeleton.find_bone(bone_name) < 0:
			if bone_name not in missing_bones:
				missing_bones.append(bone_name)
			continue
		resolved_bone_tracks += 1
	return {
		"animation": String(animation_name),
		"track_count": animation.get_track_count(),
		"bone_track_count": bone_tracks,
		"resolved_bone_track_count": resolved_bone_tracks,
		"missing_bones": missing_bones,
	}


func _output_chain_connected(tree: AnimationTree) -> bool:
	var connections: Array = tree.tree_root.get("node_connections")
	for index in range(0, connections.size(), 3):
		if String(connections[index]) == "LeftHandBlend" and int(connections[index + 1]) == 0 and String(connections[index + 2]) == "States":
			return true
	return false


func _run_scenario(
	scenario_name: String,
	data: Array,
	runtime: Node3D,
	camera: Camera3D,
	npc: CharacterBody3D,
	navigation,
	skeleton: Skeleton3D,
	bone_indices: Dictionary,
	animation_tree: AnimationTree,
	animation_player: AnimationPlayer,
	trace: FileAccess
) -> void:
	var frame_limit := 140 if "--pose-proof-only" in OS.get_cmdline_user_args() else 1500
	var contact_lock_capture_enabled := CONTACT_LOCK_CAPTURE_FLAG in OS.get_cmdline_user_args()
	var contact_lock_diagnostics := runtime.get_node_or_null("IK003NavigationPlaytestDiagnostics")
	var contact_lock_captures := {}
	if contact_lock_capture_enabled and contact_lock_diagnostics == null:
		push_error("The contact-lock diagnostic capture requires IK003NavigationPlaytestDiagnostics on the established navigation test scene.")
		(_manifest["assertion_failures"] as Array).append({
			"scenario": scenario_name,
			"contract": "contact_lock_visual_diagnostics",
			"reason": "IK003NavigationPlaytestDiagnostics is absent.",
		})
	elif contact_lock_capture_enabled:
		contact_lock_diagnostics.SetVisualScenario(scenario_name)
		contact_lock_diagnostics.SetVisualMarkerRendering(true)
	npc.global_transform = Transform3D.IDENTITY
	var destination_position: Vector3 = data[0]
	var destination := Transform3D(Basis(Vector3.UP, data[1]), destination_position)
	_configure_route_markers(runtime, destination_position)
	var request_result = navigation.SetDestination(destination)
	var min_y: float = npc.global_position.y
	var max_y: float = npc.global_position.y
	var raw_sign_change_count := 0
	var previous_raw_sign := 0.0
	var dense_frames: Array[Dictionary] = []
	var candidate_records: Array[Dictionary] = []
	var raw_candidate_records: Array[Dictionary] = []
	var completion_frame := -1
	var stability_frames := 0
	var baseline_steps: int = navigation.get("PhysicalStepCount")
	var travel_phases: Array[float] = []
	var travel_root_frames := 0
	var sustained_synthetic_planar := 0.0
	var exit_root_planar := Vector2.ZERO
	var exit_crossfade_root_planar := Vector2.ZERO
	var exit_remaining_root_planar := Vector2.ZERO
	var exit_synthetic_planar := Vector2.ZERO
	var exit_realised_planar := Vector2.ZERO
	var terminal_correction_distance := 0.0
	var terminal_correction_vector := Vector2.ZERO
	var terminal_correction_frames := 0
	var terminal_correction_by_stage := {}
	var terminal_overshoot_records: Array[Dictionary] = []
	var terminal_overshoot_active := false
	var terminal_overshoot_first_frame := -1
	var terminal_overshoot_first_stage := ""
	var terminal_overshoot_maximum_m := 0.0
	var terminal_overshoot_backtrack_frames := 0
	var terminal_overshoot_before_exit := false
	var terminal_overshoot_during_crossfade := false
	var terminal_overshoot_during_exit := false
	var terminal_overshoot_during_settlement := false
	var stage_root_distance := {}
	var stage_synthetic_distance := {}
	var release_wait_frames := 0
	var release_wait_distance := 0.0
	var release_wait_vector := Vector2.ZERO
	var post_root_frames := 0
	var post_root_distance := 0.0
	var root_zero_synthetic_frames := 0
	var root_zero_synthetic_distance := 0.0
	var exit_root_observed := false
	var exit_commit_diagnostics := {}
	var travel_start_frame := -1
	var travel_end_frame := -1
	var last_stage := -1
	var stage_frames: Dictionary = {}
	var stage_root_duration_distance := {}
	var stage_frame_counts := {}
	var captured_sequences: Dictionary = {}
	var pending_capture: Dictionary = {}
	var previous_transform := npc.global_transform
	var previous_position_error := npc.global_position.distance_to(destination.origin)
	var previous_bones := _bone_world_positions(skeleton, bone_indices)
	var previous_bone_rotations := _bone_world_rotations(skeleton, bone_indices)
	var previous_bone_deltas := {"Root": Vector3.ZERO, "Hips": Vector3.ZERO, "LeftFoot": Vector3.ZERO, "RightFoot": Vector3.ZERO}
	var previous_bone_rotation_deltas := {"Root": 0.0, "Hips": 0.0, "LeftFoot": 0.0, "RightFoot": 0.0}
	var previous_root_delta := Vector3.ZERO
	var maximum_transition_foot_jump := 0.0
	var maximum_transition_hips_jump := 0.0
	var maximum_root_translation_residual := 0.0
	var maximum_hips_translation_residual := 0.0
	var maximum_foot_translation_residual := 0.0
	var maximum_root_rotation_residual := 0.0
	var maximum_hips_rotation_residual := 0.0
	var maximum_foot_rotation_residual := 0.0
	var support_samples: Array[Dictionary] = []
	var support_contact_episodes: Array[Dictionary] = []
	var support_evidence_failures: Array[Dictionary] = []
	var active_support_episode: Dictionary = {}
	var minimum_foot_y := {"LeftFoot": INF, "RightFoot": INF}
	var normal_gait_frame_jumps: Array[float] = []
	var transition_jumps: Dictionary = {}
	var idle_planar_translation := 0.0
	var ordering_mismatch_frames := 0
	var moving_yaw_assist_radians := 0.0
	var moving_yaw_assist_frames := 0
	var peak_reserved_planar := 0.0
	var peak_reserved_yaw := 0.0
	var facade_premature_completion_frames := 0
	var yaw_target_crossing_count := 0
	var maximum_zero_root_yaw_assist := 0.0
	var ledger_reset_frames: Array[int] = []
	var previous_ledger_realised_planar := 0.0
	var left_upper_arm_baseline := skeleton.get_bone_pose(bone_indices.LeftUpperArm)
	var right_upper_arm_baseline := skeleton.get_bone_pose(bone_indices.RightUpperArm)
	var maximum_left_upper_arm_rotation := 0.0
	var maximum_right_upper_arm_rotation := 0.0
	var dense_capture_label := ""
	var dense_capture_remaining := 0
	var dense_capture_sequence := 0
	var mid_support_captured := false
	var previous_actor_velocity := Vector2.ZERO
	var previous_actor_acceleration := Vector2.ZERO
	var previous_root_velocity := Vector2.ZERO
	var previous_yaw_rate := 0.0
	var previous_blend_point := Vector2.ZERO
	var previous_triangle := -1
	var previous_release_armed := false
	var approach_activation_frame := -1
	var approach_activation_blend_point := Vector2.ZERO
	var approach_activation_blend_valid := false
	var approach_activation_count := 0
	var selection_time_to_release_seconds := NAN
	var previous_phase := 0.0
	var triangle_capture_count := 0
	var release_capture_count := 0
	var pre_entry_boundary_captured := false
	for frame in range(1, frame_limit + 1):
		var previous_step: int = navigation.get("PhysicalStepCount")
		await physics_frame
		# Post-boundary confirmation frames perform no movement, so PhysicalStepCount freezes; the wait releases
		# on the completed programme stage or on the durable outcome clearing the destination (which resets the
		# programme stage to Idle).
		while navigation.get("PhysicalStepCount") == previous_step \
		and navigation.get("ProgrammeStage") != STAGE_COMPLETE \
		and bool(navigation.get("HasDestination")):
			await physics_frame
		if contact_lock_capture_enabled and contact_lock_diagnostics != null:
			var visual_snapshot := contact_lock_diagnostics.GetVisualSnapshot() as Dictionary
			if _is_contact_lock_capture_snapshot(visual_snapshot, "acquire_support") and not contact_lock_captures.has("acquire_support"):
				await _capture_contact_lock_snapshot("acquire_support", frame, camera, npc, skeleton, contact_lock_diagnostics, visual_snapshot)
				contact_lock_captures["acquire_support"] = visual_snapshot.duplicate(true)
			elif _is_contact_lock_capture_snapshot(visual_snapshot, "late_support") and not contact_lock_captures.has("late_support"):
				await _capture_contact_lock_snapshot("late_support", frame, camera, npc, skeleton, contact_lock_diagnostics, visual_snapshot)
				contact_lock_captures["late_support"] = visual_snapshot.duplicate(true)
			elif _is_contact_lock_capture_snapshot(visual_snapshot, "release_fallback") and not contact_lock_captures.has("release_fallback"):
				await _capture_contact_lock_snapshot("release_fallback", frame, camera, npc, skeleton, contact_lock_diagnostics, visual_snapshot)
				contact_lock_captures["release_fallback"] = visual_snapshot.duplicate(true)
		if not pending_capture.is_empty():
			var actual_stage: int = navigation.get("ProgrammeStage")
			var pending_stage: String = pending_capture["stage"]
			if CAPTURE_STAGES.get(actual_stage, "") == pending_stage:
				_save_capture(
					scenario_name,
					pending_stage,
					pending_capture["sequence"],
					pending_capture["view"],
					pending_capture["physics_frame"],
					camera,
					npc,
					navigation,
					skeleton,
					bone_indices)
			else:
				(_manifest["absent_captures"] as Array).append({
					"run_id": _run_id,
					"scenario": scenario_name,
					"stage": pending_stage,
					"view": pending_capture["view"],
					"sequence": pending_capture["sequence"],
					"reason": "Programme stage changed before render-complete capture.",
				})
			pending_capture = {}
		# The facade contract counts only pre-completion frames: after the durable outcome commits the
		# destination clears and the programme stage resets to Idle, which is legitimate completion rather than a
		# premature facade finish.
		var stage: int = navigation.get("ProgrammeStage")
		if not bool(navigation.get("IsNavigationRunning")) \
		and stage != STAGE_COMPLETE \
		and bool(navigation.get("HasDestination")):
			facade_premature_completion_frames += 1
		var current_bones := _bone_world_positions(skeleton, bone_indices)
		var current_bone_rotations := _bone_world_rotations(skeleton, bone_indices)
		var current_root_delta: Vector3 = navigation.get("LastAuthoredRootWorldDelta")
		var previous_stage := last_stage
		if previous_stage >= 0 and stage != previous_stage:
			maximum_transition_hips_jump = maxf(maximum_transition_hips_jump, current_bones.Hips.distance_to(previous_bones.Hips))
			maximum_transition_foot_jump = maxf(maximum_transition_foot_jump, current_bones.LeftFoot.distance_to(previous_bones.LeftFoot))
			maximum_transition_foot_jump = maxf(maximum_transition_foot_jump, current_bones.RightFoot.distance_to(previous_bones.RightFoot))
			transition_jumps["%d_to_%d" % [previous_stage, stage]] = {
				"hips_m": current_bones.Hips.distance_to(previous_bones.Hips),
				"left_foot_m": current_bones.LeftFoot.distance_to(previous_bones.LeftFoot),
				"right_foot_m": current_bones.RightFoot.distance_to(previous_bones.RightFoot),
				"root_delta_change_m": current_root_delta.distance_to(previous_root_delta),
				"ledger_realised_planar": navigation.get("LastRealisedPlanarCorrection"),
				"ledger_remaining_planar": navigation.get("RemainingPlanarCorrection"),
				"ledger_reserved_planar": navigation.get("ReservedTerminalPlanar"),
				"ledger_capacity_planar": navigation.get("MaximumPlanarResidualPerSegment"),
			}
		if previous_stage == STAGE_SUSTAINED_TRAVEL and stage == STAGE_EXIT:
			# The removed forecast/arm members are replaced by the replacement model's live commit diagnostics,
			# runner-observed approach activation evidence, and the selected imminent-candidate projection.
			var commit_phase := float(navigation.get("LastTerminalCommitPhase"))
			var target_release_phase := float(navigation.get("LastTargetReleasePhase"))
			var commit_release_phase_error := absf(wrapf(commit_phase - target_release_phase, -0.5, 0.5))
			var tick_seconds := 1.0 / float(Engine.physics_ticks_per_second)
			var actual_release_wait_seconds := (frame - approach_activation_frame) * tick_seconds if approach_activation_frame >= 0 else INF
			var release_wait_time_error := (
				absf(actual_release_wait_seconds - selection_time_to_release_seconds)
				if approach_activation_frame >= 0 and is_finite(selection_time_to_release_seconds)
				else INF
			)
			exit_commit_diagnostics = {
				"model": "replacement_terminal_model",
				"commit_phase": commit_phase,
				"commit_previous_phase": navigation.get("LastTerminalCommitPreviousPhase"),
				"commit_expected_phase_advance": navigation.get("LastTerminalCommitExpectedAdvance"),
				"commit_playback_rate": navigation.get("LastTerminalCommitPlaybackRate"),
				"commit_ordinary_playback_rate": navigation.get("LastTerminalCommitOrdinaryPlaybackRate"),
				"target_release_phase": target_release_phase,
				"release_phase_error": commit_release_phase_error,
				"selection_time_to_release_seconds": selection_time_to_release_seconds,
				"actual_release_wait_seconds": actual_release_wait_seconds,
				"release_wait_time_error_seconds": release_wait_time_error,
				"approach_activation_frame": approach_activation_frame,
				"approach_activation_blend_point": str(approach_activation_blend_point) if approach_activation_blend_valid else null,
				"approach_activation_count": approach_activation_count,
				"selected_release_lane": navigation.get("LastSelectedReleaseLane"),
				"selected_release_event": navigation.get("LastSelectedReleaseEvent"),
				"root_accumulator": navigation.get("LastTerminalCommitRootAccumulator"),
				"selected_exit_role": navigation.get("LastSelectedExitRole"),
				"time_to_selected_release_seconds": navigation.get("LastTimeToSelectedReleaseSeconds"),
				"terminal_signed_remaining_m": navigation.get("LastTerminalSignedRemainingDistance"),
				"terminal_lateral_error_m": navigation.get("LastTerminalLateralError"),
				"terminal_root_tangent_m": navigation.get("LastTerminalRootTangentDisplacement"),
				"terminal_realised_tangent_m": navigation.get("LastTerminalRealisedTangentDisplacement"),
				"terminal_residual_tangent_m": navigation.get("LastTerminalResidualTangentDisplacement"),
				"terminal_tangent": navigation.get("LastTerminalTangent"),
				"finite_transition_source_wall_ratio": navigation.get("FiniteTransitionSourceWallRatio"),
				"reserved_planar_m": navigation.get("ReservedTerminalPlanar"),
				"reserved_yaw_rad": navigation.get("ReservedTerminalYaw"),
				"selected_candidate": _selected_candidate_summary(navigation),
			}
			if scenario_name in ["exact_reported", "user_reported", "continuity_reported"] and previous_stage == STAGE_ENTRY and stage == STAGE_SUSTAINED_TRAVEL:
				dense_capture_label = "entry_to_sustained"
				dense_capture_remaining = 8
				dense_capture_sequence = 0
			elif scenario_name in ["exact_reported", "user_reported", "continuity_reported"] and previous_stage == STAGE_SUSTAINED_TRAVEL and stage == STAGE_EXIT:
				dense_capture_label = "sustained_to_exit"
				dense_capture_remaining = 8
				dense_capture_sequence = 0
			elif scenario_name in ["exact_reported", "user_reported", "continuity_reported"] and previous_stage == STAGE_EXIT and stage == STAGE_SETTLEMENT:
				dense_capture_label = "exit_to_settlement"
				dense_capture_remaining = 8
				dense_capture_sequence = 0
			elif scenario_name in ["exact_reported", "user_reported", "continuity_reported"] and previous_stage == STAGE_SETTLEMENT and stage == STAGE_COMPLETE:
				dense_capture_label = "settlement_to_complete"
				dense_capture_remaining = 16
				dense_capture_sequence = 0
		last_stage = stage
		# The replacement model exposes the imminent release window as IsTerminalReleaseApproachActive; the
		# activation edge is the evidence replacement for the removed arm-time members (arm phase, arm blend
		# point, target/actual release wait times).
		var approach_active := bool(navigation.get("IsTerminalReleaseApproachActive"))
		if approach_active and not previous_release_armed:
			approach_activation_frame = frame
			approach_activation_blend_point = navigation.get("LastBlendPoint")
			approach_activation_blend_valid = true
			selection_time_to_release_seconds = float(navigation.get("LastTimeToSelectedReleaseSeconds"))
			approach_activation_count += 1
		var left_delta: Vector3 = current_bones.LeftFoot - previous_bones.LeftFoot
		var right_delta: Vector3 = current_bones.RightFoot - previous_bones.RightFoot
		minimum_foot_y.LeftFoot = minf(minimum_foot_y.LeftFoot, current_bones.LeftFoot.y)
		minimum_foot_y.RightFoot = minf(minimum_foot_y.RightFoot, current_bones.RightFoot.y)
		if stage == STAGE_SUSTAINED_TRAVEL and previous_stage == STAGE_SUSTAINED_TRAVEL:
			normal_gait_frame_jumps.append(maxf(left_delta.length(), right_delta.length()))
		var raw_shared_phase = navigation.get("LastEvaluatedNormalisedPhase")
		var shared_phase := float(raw_shared_phase) if raw_shared_phase is float or raw_shared_phase is int else NAN
		var seam_distance := minf(shared_phase, 1.0 - shared_phase) if is_finite(shared_phase) else NAN
		var phase_lane = navigation.get("PhaseLane")
		var phase_lane_is_well_formed := phase_lane is int and int(phase_lane) in [0, 1]
		var phase_diagnostic_interval := (
			stage == STAGE_SUSTAINED_TRAVEL
			and str(navigation.get("LastFadingFromState")).is_empty()
			and is_finite(shared_phase)
			and shared_phase >= SUPPORT_PHASE_START
			and shared_phase <= SUPPORT_PHASE_END
		)
		# ANIM-002 permission and an IK-003 snapshot are not exposed to this Stage 1 runner.
		# Navigation phase values remain trace diagnostics and must not select or qualify support.
		var contact_evidence_available := false
		var declared_position := Vector3(NAN, NAN, NAN)
		var previous_declared_position := Vector3(NAN, NAN, NAN)
		var declared_delta := declared_position - previous_declared_position
		var current_lower_foot_y := minf(current_bones.LeftFoot.y, current_bones.RightFoot.y)
		var declared_height := declared_position.y - current_lower_foot_y
		var declared_vertical_speed := absf(declared_delta.y) * Engine.physics_ticks_per_second
		var declared_support_motion := Vector2(declared_delta.x, declared_delta.z).length()
		if stage == STAGE_SUSTAINED_TRAVEL and str(navigation.get("LastFadingFromState")).is_empty():
			support_evidence_failures.append({
				"frame": frame,
				"reason": "authoritative_contact_evidence_unavailable_stage1",
				"raw_phase_type": typeof(raw_shared_phase),
				"phase_lane": phase_lane,
				"phase_lane_is_well_formed": phase_lane_is_well_formed,
				"phase_is_diagnostic_only": true,
			})
		var declared_contact_qualified := false
		if phase_diagnostic_interval:
			var support_sample := _evaluate_support_sample({
				"frame": frame,
				"declared_support_foot": "",
				"observed_foot": "",
				"phase_lane": phase_lane,
				"phase_lane_valid": false,
				"phase": shared_phase,
				"contact_evidence_available": contact_evidence_available,
				"contact_evidence_source": "unavailable_stage1: Navigation.PhaseLane and LastEvaluatedNormalisedPhase are diagnostics, not ANIM-002 or IK-003 contact evidence.",
				"contact_confidence": 0.0,
				"contact_evidence_frame": -1,
				"previous_world": previous_declared_position,
				"current_world": declared_position,
				"height_above_lower_foot_m": declared_height,
				"vertical_speed_mps": declared_vertical_speed,
				"anchor_world": Vector3(NAN, NAN, NAN),
				"anchor_frame": -1,
				"anchor_last_verified_frame": -1,
			})
		# Retain every diagnostic-window sample, including unavailable or rejected evidence.
			support_samples.append(support_sample)
		elif not active_support_episode.is_empty():
			_finalize_support_episode(active_support_episode, support_contact_episodes)
			active_support_episode = {}
		if str(navigation.get("LastEvaluatedState")) == "NPCIdle":
			var idle_delta: Vector3 = navigation.get("LastRealisedDisplacement")
			idle_planar_translation += Vector2(idle_delta.x, idle_delta.z).length()
		if navigation.get("LastPublishedFrameGeneration") != navigation.get("LastConsumedFrameGeneration"):
			ordering_mismatch_frames += 1
		var moving_yaw_assist: float = absf(float(navigation.get("LastYawResidual"))) / Engine.physics_ticks_per_second
		var realised_for_yaw: Vector3 = navigation.get("LastRealisedDisplacement")
		if stage in [STAGE_ENTRY, STAGE_SUSTAINED_TRAVEL, STAGE_EXIT] and Vector2(realised_for_yaw.x, realised_for_yaw.z).length_squared() > 0.0000000001 and moving_yaw_assist > 0.0:
			moving_yaw_assist_radians += moving_yaw_assist
			moving_yaw_assist_frames += 1
		peak_reserved_planar = maxf(peak_reserved_planar, float(navigation.get("ReservedTerminalPlanar")))
		peak_reserved_yaw = maxf(peak_reserved_yaw, float(navigation.get("ReservedTerminalYaw")))
		var current_ledger_realised_planar := float(navigation.get("LastRealisedPlanarCorrection"))
		if current_ledger_realised_planar + 0.0000001 < previous_ledger_realised_planar:
			ledger_reset_frames.append(frame)
		previous_ledger_realised_planar = current_ledger_realised_planar
		maximum_left_upper_arm_rotation = maxf(
			maximum_left_upper_arm_rotation,
			left_upper_arm_baseline.basis.get_rotation_quaternion().angle_to(
				skeleton.get_bone_pose(bone_indices.LeftUpperArm).basis.get_rotation_quaternion()))
		maximum_right_upper_arm_rotation = maxf(
			maximum_right_upper_arm_rotation,
			right_upper_arm_baseline.basis.get_rotation_quaternion().angle_to(
				skeleton.get_bone_pose(bone_indices.RightUpperArm).basis.get_rotation_quaternion()))
		var stage_label: String = CAPTURE_STAGES.get(stage, "")
		if not stage_label.is_empty():
			stage_frames[stage_label] = int(stage_frames.get(stage_label, 0)) + 1
			var in_stage_frame: int = stage_frames[stage_label]
			if in_stage_frame in CAPTURE_STAGE_FRAMES:
				var sequence_index := CAPTURE_STAGE_FRAMES.find(in_stage_frame)
				_configure_camera(VIEWS[sequence_index], camera, npc, destination_position)
				pending_capture = {
					"stage": stage_label,
					"sequence": sequence_index,
					"view": VIEWS[sequence_index],
					"physics_frame": frame,
				}
				captured_sequences[stage_label] = int(captured_sequences.get(stage_label, 0)) + 1
			if stage == STAGE_SETTLEMENT and in_stage_frame <= 120:
				await _capture_dense_views(scenario_name, "settlement_all", in_stage_frame - 1, frame, camera, npc, navigation, skeleton, bone_indices)
			if stage == STAGE_COMPLETE and in_stage_frame <= 16:
				await _capture_dense_views(scenario_name, "idle_initial", in_stage_frame - 1, frame, camera, npc, navigation, skeleton, bone_indices)
		if stage == STAGE_SUSTAINED_TRAVEL:
			if travel_start_frame < 0:
				travel_start_frame = frame
			travel_end_frame = frame
			travel_phases.append(navigation.get("LastEvaluatedNormalisedPhase"))
			var root_delta: Vector3 = navigation.get("LastEvaluatedRootDelta")
			if root_delta.is_finite() and not root_delta.is_zero_approx():
				travel_root_frames += 1
			var sustained_synthetic: Vector3 = navigation.get("LastRequestedSyntheticWorldDelta")
			sustained_synthetic_planar += Vector2(sustained_synthetic.x, sustained_synthetic.z).length()
		elif stage == STAGE_EXIT:
			var exit_root: Vector3 = navigation.get("LastAuthoredRootWorldDelta")
			var exit_synthetic: Vector3 = navigation.get("LastRequestedSyntheticWorldDelta")
			exit_root_planar += Vector2(exit_root.x, exit_root.z)
			if str(navigation.get("LastFadingFromState")) == "NPCTravel":
				exit_crossfade_root_planar += Vector2(exit_root.x, exit_root.z)
			else:
				exit_remaining_root_planar += Vector2(exit_root.x, exit_root.z)
			exit_synthetic_planar += Vector2(exit_synthetic.x, exit_synthetic.z)
			var exit_realised: Vector3 = navigation.get("LastRealisedDisplacement")
			exit_realised_planar += Vector2(exit_realised.x, exit_realised.z)
			trace.store_line(JSON.stringify({
				"kind": "exit_crossfade_frame",
				"scenario": scenario_name,
				"frame": frame,
				"fading_from_state": str(navigation.get("LastFadingFromState")),
				"state": str(navigation.get("LastEvaluatedState")),
				"play_position": navigation.get("LastEvaluatedPlayPosition"),
				"authored_root_world_delta": exit_root,
				"realised_world_delta": exit_realised,
			}))
		var terminal_synthetic: Vector3 = navigation.get("LastRequestedSyntheticWorldDelta")
		var terminal_synthetic_distance := Vector2(terminal_synthetic.x, terminal_synthetic.z).length()
		var frame_root: Vector3 = navigation.get("LastAuthoredRootWorldDelta")
		var frame_root_distance := Vector2(frame_root.x, frame_root.z).length()
		if stage in [STAGE_ENTRY, STAGE_SUSTAINED_TRAVEL] and frame_root_distance <= 0.0000001:
			maximum_zero_root_yaw_assist = maxf(maximum_zero_root_yaw_assist, absf(float(navigation.get("LastYawResidual"))))
		if scenario_name == "user_reported" and stage in [STAGE_ENTRY, STAGE_SUSTAINED_TRAVEL] and float(navigation.get("LastRemainingDistance")) > 0.65:
			var current_yaw := atan2(npc.global_basis.z.x, npc.global_basis.z.z)
			if current_yaw < float(data[1]) - 0.0001:
				yaw_target_crossing_count += 1
		var frame_realised: Vector3 = navigation.get("LastRealisedDisplacement")
		var frame_realised_distance := Vector2(frame_realised.x, frame_realised.z).length()
		stage_root_distance[str(stage)] = float(stage_root_distance.get(str(stage), 0.0)) + frame_root_distance
		stage_synthetic_distance[str(stage)] = float(stage_synthetic_distance.get(str(stage), 0.0)) + terminal_synthetic_distance
		if stage == STAGE_SUSTAINED_TRAVEL and bool(navigation.get("IsTerminalReleaseApproachActive")):
			release_wait_frames += 1
			release_wait_distance += frame_realised_distance
			release_wait_vector += Vector2(frame_realised.x, frame_realised.z)
		if stage == STAGE_EXIT and frame_root_distance > 0.0000001:
			exit_root_observed = true
		if exit_root_observed and stage in [STAGE_EXIT, STAGE_SETTLEMENT, STAGE_COMPLETE] and frame_root_distance <= 0.0000001:
			post_root_frames += 1
			post_root_distance += frame_realised_distance
		if frame_root_distance <= 0.0000001 and terminal_synthetic_distance > 0.0000001:
			root_zero_synthetic_frames += 1
			root_zero_synthetic_distance += terminal_synthetic_distance
		if terminal_synthetic_distance > 0.0:
			terminal_correction_distance += terminal_synthetic_distance
			terminal_correction_vector += Vector2(terminal_synthetic.x, terminal_synthetic.z)
			terminal_correction_frames += 1
			terminal_correction_by_stage[str(stage)] = float(terminal_correction_by_stage.get(str(stage), 0.0)) + terminal_synthetic_distance
		min_y = minf(min_y, npc.global_position.y)
		max_y = maxf(max_y, npc.global_position.y)
		var realised_longitudinal: float = navigation.get("LastRealisedLongitudinal")
		var realised_longitudinal_speed := realised_longitudinal * Engine.physics_ticks_per_second
		var raw_sign := signf(realised_longitudinal) if absf(realised_longitudinal) > 0.000001 else 0.0
		if raw_sign != 0 and previous_raw_sign != 0 and raw_sign != previous_raw_sign:
			raw_sign_change_count += 1
			raw_candidate_records.append({
				"scenario": scenario_name,
				"candidate_frame": frame,
				"from_sign": previous_raw_sign,
				"to_sign": raw_sign,
			})
		if raw_sign != 0:
			previous_raw_sign = raw_sign
		var frame_record := _reversal_frame_record(
			scenario_name,
			frame,
			destination,
			npc,
			navigation,
			realised_longitudinal_speed,
			previous_transform,
			previous_position_error)
		var frame_delta := 1.0 / float(Engine.physics_ticks_per_second)
		if stage in [STAGE_ENTRY, STAGE_SUSTAINED_TRAVEL, STAGE_EXIT, STAGE_SHORT_BOUT]:
			stage_frame_counts[str(stage)] = int(stage_frame_counts.get(str(stage), 0)) + 1
			stage_root_duration_distance[str(stage)] = float(stage_root_duration_distance.get(str(stage), 0.0)) + frame_root_distance
		var actor_velocity := Vector2(frame_realised.x, frame_realised.z) / frame_delta
		var actor_acceleration := (actor_velocity - previous_actor_velocity) / frame_delta if frame > 1 else Vector2.ZERO
		var actor_jerk := (actor_acceleration - previous_actor_acceleration) / frame_delta if frame > 2 else Vector2.ZERO
		var root_world_delta: Vector3 = navigation.get("LastAuthoredRootWorldDelta")
		var root_velocity := Vector2(root_world_delta.x, root_world_delta.z) / frame_delta
		var root_acceleration := (root_velocity - previous_root_velocity) / frame_delta if frame > 1 else Vector2.ZERO
		var actor_yaw_delta := float(frame_record["actor_yaw_delta"])
		var yaw_rate := actor_yaw_delta / frame_delta
		var yaw_acceleration := (yaw_rate - previous_yaw_rate) / frame_delta if frame > 1 else 0.0
		var route_tangent3: Vector3 = navigation.get("LastRouteTangent")
		var route_tangent := Vector2(route_tangent3.x, route_tangent3.z)
		if route_tangent.length_squared() <= 0.0000001:
			route_tangent = Vector2(0.0, 1.0)
		else:
			route_tangent = route_tangent.normalized()
		var route_normal := Vector2(-route_tangent.y, route_tangent.x)
		var actor_tangential_acceleration := actor_acceleration.dot(route_tangent)
		var actor_normal_acceleration := actor_acceleration.dot(route_normal)
		var actor_tangential_jerk := actor_jerk.dot(route_tangent)
		var actor_normal_jerk := actor_jerk.dot(route_normal)
		var root_tangential_acceleration := root_acceleration.dot(route_tangent)
		var root_normal_acceleration := root_acceleration.dot(route_normal)
		var actor_speed_discontinuity := absf(actor_velocity.length() - previous_actor_velocity.length()) if frame > 1 else 0.0
		var root_speed_discontinuity := absf(root_velocity.length() - previous_root_velocity.length()) if frame > 1 else 0.0
		var yaw_rate_discontinuity := absf(yaw_rate - previous_yaw_rate) if frame > 1 else 0.0
		var terminal_remaining_signed: float = navigation.get("LastTerminalSignedRemainingDistance")
		var terminal_lateral_error: float = navigation.get("LastTerminalLateralError")
		var root_terminal_tangent_m: float = navigation.get("LastTerminalRootTangentDisplacement")
		var realised_terminal_tangent_m: float = navigation.get("LastTerminalRealisedTangentDisplacement")
		var residual_terminal_tangent_m: float = navigation.get("LastTerminalResidualTangentDisplacement")
		var overshot_terminal: bool = navigation.get("LastTerminalOvershot")
		if overshot_terminal:
			terminal_overshoot_maximum_m = maxf(terminal_overshoot_maximum_m, absf(terminal_remaining_signed))
			if not terminal_overshoot_active:
				terminal_overshoot_active = true
				terminal_overshoot_first_frame = frame
				terminal_overshoot_first_stage = _stage_name(stage)
				terminal_overshoot_before_exit = stage == STAGE_SUSTAINED_TRAVEL
				terminal_overshoot_during_crossfade = not str(navigation.get("LastFadingFromState")).is_empty()
				terminal_overshoot_during_exit = stage == STAGE_EXIT
				terminal_overshoot_during_settlement = stage == STAGE_SETTLEMENT
			if realised_terminal_tangent_m < -0.000001:
				terminal_overshoot_backtrack_frames += 1
			terminal_overshoot_records.append({
				"frame": frame,
				"stage": _stage_name(stage),
				"remaining_signed_m": terminal_remaining_signed,
				"lateral_error_m": terminal_lateral_error,
				"root_tangent_m": root_terminal_tangent_m,
				"realised_tangent_m": realised_terminal_tangent_m,
				"residual_tangent_m": residual_terminal_tangent_m,
				"phase": navigation.get("LastEvaluatedNormalisedPhase"),
				"blend_point": navigation.get("LastBlendPoint"),
				"playback_rate": navigation.get("LastSmoothedPlaybackRate"),
				"fading_from_state": navigation.get("LastFadingFromState"),
				"exit_release_armed": navigation.get("IsTerminalReleaseApproachActive"),
				"candidate_overshoot_rejection": navigation.get("LastCandidateRejectionSummary"),
				"collision_detected": navigation.get("LastMovementCollided"),
				"ledger_reserved_planar_m": navigation.get("ReservedTerminalPlanar"),
				"ledger_realised_planar_m": navigation.get("LastRealisedPlanarCorrection"),
				"ledger_remaining_planar_m": navigation.get("RemainingPlanarCorrection"),
			})
		var blend_point: Vector2 = navigation.get("LastBlendPoint")
		var triangle := _active_blend_triangle(animation_tree, blend_point)
		var phase_advance := _phase_advance(previous_phase, float(navigation.get("LastEvaluatedNormalisedPhase")))
		var hips_delta: Vector3 = current_bones.Hips - previous_bones.Hips
		var root_bone_delta: Vector3 = current_bones.Root - previous_bones.Root
		var root_translation_residual: float = (root_bone_delta - (previous_bone_deltas.Root as Vector3)).length()
		var hips_translation_residual: float = (hips_delta - (previous_bone_deltas.Hips as Vector3)).length()
		var left_foot_translation_residual: float = (left_delta - (previous_bone_deltas.LeftFoot as Vector3)).length()
		var right_foot_translation_residual: float = (right_delta - (previous_bone_deltas.RightFoot as Vector3)).length()
		var root_rotation_delta: float = (current_bone_rotations.Root as Quaternion).angle_to(previous_bone_rotations.Root)
		var hips_rotation_delta: float = (current_bone_rotations.Hips as Quaternion).angle_to(previous_bone_rotations.Hips)
		var left_foot_rotation_delta: float = (current_bone_rotations.LeftFoot as Quaternion).angle_to(previous_bone_rotations.LeftFoot)
		var right_foot_rotation_delta: float = (current_bone_rotations.RightFoot as Quaternion).angle_to(previous_bone_rotations.RightFoot)
		var root_rotation_residual := absf(root_rotation_delta - float(previous_bone_rotation_deltas.Root)) if frame > 2 else 0.0
		var hips_rotation_residual := absf(hips_rotation_delta - float(previous_bone_rotation_deltas.Hips)) if frame > 2 else 0.0
		var left_foot_rotation_residual := absf(left_foot_rotation_delta - float(previous_bone_rotation_deltas.LeftFoot)) if frame > 2 else 0.0
		var right_foot_rotation_residual := absf(right_foot_rotation_delta - float(previous_bone_rotation_deltas.RightFoot)) if frame > 2 else 0.0
		if previous_stage >= 0 and previous_stage != stage:
			maximum_root_translation_residual = maxf(maximum_root_translation_residual, root_translation_residual)
			maximum_hips_translation_residual = maxf(maximum_hips_translation_residual, hips_translation_residual)
			maximum_foot_translation_residual = maxf(maximum_foot_translation_residual, maxf(left_foot_translation_residual, right_foot_translation_residual))
			maximum_root_rotation_residual = maxf(maximum_root_rotation_residual, root_rotation_residual)
			maximum_hips_rotation_residual = maxf(maximum_hips_rotation_residual, hips_rotation_residual)
			maximum_foot_rotation_residual = maxf(maximum_foot_rotation_residual, maxf(left_foot_rotation_residual, right_foot_rotation_residual))
		var signed_yaw_error := wrapf(data[1] - atan2(npc.global_basis.z.x, npc.global_basis.z.z), -PI, PI)
		var playback := animation_tree.get("parameters/States/playback") as AnimationNodeStateMachinePlayback
		var fading_from_state := str(navigation.get("LastFadingFromState"))
		var boundary := ""
		if previous_stage >= 0 and previous_stage != stage:
			boundary = "%s_to_%s" % [_stage_name(previous_stage), _stage_name(stage)]
		frame_record.merge({
			"boundary": boundary,
			"previous_animation_state": fading_from_state,
			"animation_play_position": navigation.get("LastEvaluatedPlayPosition"),
			"crossfade_active": not fading_from_state.is_empty(),
			"crossfade_progress": clampf(playback.get_current_play_position() / 0.12, 0.0, 1.0) if not fading_from_state.is_empty() else 1.0,
			"effective_playback_rate": animation_tree.get("parameters/States/NPCTravel/Rate/scale"),
		"finite_source_wall_ratio": navigation.get("FiniteTransitionSourceWallRatio") if stage in [STAGE_ENTRY, STAGE_EXIT, STAGE_SHORT_BOUT] else null,
			"actor_planar_speed_mps": actor_velocity.length(),
			"actor_planar_velocity": str(actor_velocity),
			"actor_planar_acceleration_mps2": actor_acceleration.length(),
			"actor_planar_jerk_mps3": actor_jerk.length(),
			"actor_speed_discontinuity_mps": actor_speed_discontinuity,
			"actor_tangential_acceleration_mps2": actor_tangential_acceleration,
			"actor_normal_acceleration_mps2": actor_normal_acceleration,
			"actor_tangential_jerk_mps3": actor_tangential_jerk,
			"actor_normal_jerk_mps3": actor_normal_jerk,
			"root_planar_speed_mps": root_velocity.length(),
			"root_planar_acceleration_mps2": root_acceleration.length(),
			"root_speed_discontinuity_mps": root_speed_discontinuity,
			"root_tangential_acceleration_mps2": root_tangential_acceleration,
			"root_normal_acceleration_mps2": root_normal_acceleration,
			"yaw_rate_radps": yaw_rate,
			"yaw_rate_discontinuity_radps": yaw_rate_discontinuity,
			"yaw_acceleration_radps2": yaw_acceleration,
			"route_tangent": str(route_tangent),
			"terminal_remaining_signed_m": terminal_remaining_signed,
			"terminal_lateral_error_m": terminal_lateral_error,
			"root_terminal_tangent_m": root_terminal_tangent_m,
			"realised_terminal_tangent_m": realised_terminal_tangent_m,
			"residual_terminal_tangent_m": residual_terminal_tangent_m,
			"terminal_overshoot": overshot_terminal,
			"terminal_tangent": navigation.get("LastTerminalTangent"),
			"terminal_approach_blend_point": (
				navigation.get("LastBlendPoint")
				if bool(navigation.get("IsTerminalReleaseApproachActive"))
				else null
			),
			"signed_yaw_error_rad": signed_yaw_error,
			"blend_point": str(blend_point),
			"blend_delta": str(blend_point - previous_blend_point),
			"blend_speed_units_per_second": blend_point.distance_to(previous_blend_point) / frame_delta,
			"blend_triangle": triangle,
			"previous_blend_triangle": previous_triangle,
			"blend_triangle_changed": previous_triangle >= 0 and triangle != previous_triangle,
			"phase_advance": phase_advance,
			"root_bone_world_jump_m": root_bone_delta.length(),
			"hips_world_jump_m": hips_delta.length(),
			"left_foot_world_jump_m": left_delta.length(),
			"right_foot_world_jump_m": right_delta.length(),
			"root_translation_residual_m": root_translation_residual,
			"hips_translation_residual_m": hips_translation_residual,
			"left_foot_translation_residual_m": left_foot_translation_residual,
			"right_foot_translation_residual_m": right_foot_translation_residual,
			"root_rotation_residual_rad": root_rotation_residual,
			"hips_rotation_residual_rad": hips_rotation_residual,
			"left_foot_rotation_residual_rad": left_foot_rotation_residual,
			"right_foot_rotation_residual_rad": right_foot_rotation_residual,
			"terminal_release_approach_active": navigation.get("IsTerminalReleaseApproachActive"),
			"release_approach_changed": bool(navigation.get("IsTerminalReleaseApproachActive")) != previous_release_armed,
			"release_wait_seconds": navigation.get("LastTimeToSelectedReleaseSeconds"),
			"release_target_seconds": selection_time_to_release_seconds,
			"reserved_planar_m": navigation.get("ReservedTerminalPlanar"),
			"reserved_yaw_rad": navigation.get("ReservedTerminalYaw"),
			"remaining_planar_budget_m": navigation.get("RemainingPlanarCorrection"),
			"requested_local_velocity": str(blend_point),
			"evaluated_root_world_velocity": str(root_velocity),
			"yaw_assistance_rate_radps": navigation.get("LastYawResidual"),
			"facade_running": navigation.get("IsNavigationRunning"),
			"contact_qualified": declared_contact_qualified,
			"support_foot": "unavailable_stage1",
			"low_net_motion_gait": stage == STAGE_SUSTAINED_TRAVEL and actor_velocity.length() < 0.2 and phase_advance > 0.001 and phase_advance < 0.2,
		}, true)
		if stage == STAGE_SUSTAINED_TRAVEL:
			frame_record["terminal_release_approach_active"] = navigation.get("IsTerminalReleaseApproachActive")
			frame_record["time_to_selected_release_seconds"] = navigation.get("LastTimeToSelectedReleaseSeconds")
			frame_record["terminal_signed_remaining_m"] = navigation.get("LastTerminalSignedRemainingDistance")
			frame_record["selected_candidate_endpoint_residual_world"] = str(
				_selected_candidate_summary(navigation).get("predicted_endpoint_residual_world", ""))
			frame_record["weighted_samples"] = _weighted_sample_diagnostics(animation_tree, navigation)
			frame_record["blend_point"] = navigation.get("LastBlendPoint")
			frame_record["evaluated_support_foot"] = "unavailable_stage1"
			frame_record["support_evidence"] = {
				"authoritative_contact_evidence_available": false,
				"support_assessment_status": "unavailable_stage1",
				"support_assessment_can_pass": false,
				"required_authoritative_source": "A future ANIM-002 authored-contact publication and/or immutable IK-003 post-composition snapshot.",
				"declared_support_foot": "",
				"support_selection": "unavailable: Stage 1 cannot select a support foot. Navigation phase fields are diagnostics only.",
				"phase_diagnostic_interval": phase_diagnostic_interval,
				"phase_lane_diagnostic": phase_lane,
				"phase_lane_is_well_formed": phase_lane_is_well_formed,
				"phase_diagnostic": shared_phase,
				"declared_contact_qualified": declared_contact_qualified,
				"contact_evidence_channel": "unavailable_stage1",
				"post_ik_composition_channel": "unavailable_stage1",
				"declared_support_planar_motion_m": declared_support_motion,
				"left_height_from_observed_ground": current_bones.LeftFoot.y - float(minimum_foot_y.LeftFoot),
				"right_height_from_observed_ground": current_bones.RightFoot.y - float(minimum_foot_y.RightFoot),
				"left_height_above_lower_foot": current_bones.LeftFoot.y - current_lower_foot_y,
				"right_height_above_lower_foot": current_bones.RightFoot.y - current_lower_foot_y,
				"left_vertical_speed": absf(left_delta.y) * Engine.physics_ticks_per_second,
				"right_vertical_speed": absf(right_delta.y) * Engine.physics_ticks_per_second,
				"left_world_position": current_bones.LeftFoot,
				"right_world_position": current_bones.RightFoot,
				"left_world_displacement": left_delta,
				"right_world_displacement": right_delta,
				"seam_distance": seam_distance,
				"support_phase_start": SUPPORT_PHASE_START,
				"support_phase_end": SUPPORT_PHASE_END,
			}
			frame_record["actor_world_delta"] = npc.global_position - previous_transform.origin
			frame_record["authored_root_world_delta"] = navigation.get("LastAuthoredRootWorldDelta")
			frame_record["requested_synthetic_world_delta"] = navigation.get("LastRequestedSyntheticWorldDelta")
			frame_record["yaw_assist_rate"] = navigation.get("LastYawResidual")
			frame_record["fading_from_state"] = str(navigation.get("LastFadingFromState"))
			frame_record["kind"] = "sustained_weighted_frame"
			trace.store_line(JSON.stringify(frame_record))
			if scenario_name in ["exact_reported", "user_reported", "continuity_reported"] and not mid_support_captured and int(stage_frames.get("travel", 0)) >= 180 and contact_evidence_available and declared_contact_qualified:
				dense_capture_label = "mid_support"
				dense_capture_remaining = 6
				dense_capture_sequence = 0
				mid_support_captured = true
		if scenario_name == "continuity_reported" and bool(frame_record["blend_triangle_changed"]) and triangle_capture_count < 4:
			dense_capture_label = "triangle_change_%03d" % frame
			dense_capture_remaining = 4
			dense_capture_sequence = 0
			triangle_capture_count += 1
		if scenario_name == "continuity_reported" and bool(frame_record["release_approach_changed"]) and release_capture_count < 3:
			dense_capture_label = "release_approach_%03d" % frame
			dense_capture_remaining = 5
			dense_capture_sequence = 0
			release_capture_count += 1
		if scenario_name == "continuity_reported" and stage == STAGE_ENTRY and float(navigation.get("LastEvaluatedNormalisedPhase")) >= 0.94 and not pre_entry_boundary_captured:
			dense_capture_label = "pre_entry_to_sustained"
			dense_capture_remaining = 8
			dense_capture_sequence = 0
			pre_entry_boundary_captured = true
		if scenario_name in ["exact_reported", "user_reported", "continuity_reported"] and dense_capture_remaining > 0:
			await _capture_dense_views(scenario_name, dense_capture_label, dense_capture_sequence, frame, camera, npc, navigation, skeleton, bone_indices)
			dense_capture_sequence += 1
			dense_capture_remaining -= 1
		dense_frames.append(frame_record)
		if scenario_name == "behind" and frame >= SUPPORT_CAPTURE_START and frame <= SUPPORT_CAPTURE_END and frame_record.has("support_evidence"):
			await _capture_candidate_frame(frame, runtime, camera, npc, navigation, frame_record)
		previous_transform = npc.global_transform
		previous_bones = current_bones
		previous_bone_rotations = current_bone_rotations
		previous_bone_deltas = {"Root": root_bone_delta, "Hips": hips_delta, "LeftFoot": left_delta, "RightFoot": right_delta}
		previous_bone_rotation_deltas = {
			"Root": root_rotation_delta,
			"Hips": hips_rotation_delta,
			"LeftFoot": left_foot_rotation_delta,
			"RightFoot": right_foot_rotation_delta,
		}
		previous_root_delta = current_root_delta
		previous_position_error = npc.global_position.distance_to(destination.origin)
		if navigation.get("LastObjectiveReversalConfirmed"):
			candidate_records.append({
				"scenario": scenario_name,
				"candidate_frame": frame - REVERSAL_SUSTAINED_FRAMES + 1,
				"confirmed_frame": frame,
				"from_sign": -int(signf(realised_longitudinal_speed)),
				"to_sign": int(signf(realised_longitudinal_speed)),
			})
		if scenario_name in ["user_reported", "continuity_reported"]:
			var diagnostic_record := frame_record.duplicate(true)
			diagnostic_record.merge(_diagnostic_pose_record(
				frame,
				previous_stage,
				stage,
				npc,
				navigation,
				animation_tree,
				skeleton,
				bone_indices), true)
			trace.store_line(JSON.stringify(diagnostic_record))
		previous_actor_velocity = actor_velocity
		previous_actor_acceleration = actor_acceleration
		previous_root_velocity = root_velocity
		previous_yaw_rate = yaw_rate
		previous_blend_point = blend_point
		previous_triangle = triangle
		previous_release_armed = bool(navigation.get("IsTerminalReleaseApproachActive"))
		previous_phase = float(navigation.get("LastEvaluatedNormalisedPhase"))
		# The replaced model's completion is durable: after the terminal outcome commits, the destination clears
		# and the programme stage resets to Idle, so stability is counted from the completed stage or the cleared
		# destination rather than from a sustained Complete stage that no longer exists.
		var outcome_committed := not bool(navigation.get("HasDestination"))
		if stage == STAGE_COMPLETE or outcome_committed:
			stability_frames += 1
			if completion_frame < 0:
				completion_frame = frame
			if stability_frames >= CAPTURE_STAGE_FRAMES[-1] + 2:
				break
		else:
			stability_frames = 0
	if not active_support_episode.is_empty():
		_finalize_support_episode(active_support_episode, support_contact_episodes)
	if contact_lock_capture_enabled:
		_manifest["contact_lock_f2i_l_capture"] = {
			"scenario": scenario_name,
			"route": "user_reported",
			"direction_convention": "Godot world forward is -Z. The lower-body camera is placed on avatar-left (-global Basis.X) and looks at the semantic LeftFoot; avatar forward is -global Basis.Z.",
			"required_snapshots": ["acquire_support", "late_support", "release_fallback"],
			"captured_snapshots": contact_lock_captures,
			"raw_anchor_separation_threshold_m": CONTACT_LOCK_RAW_ANCHOR_SEPARATION_M,
			"native_anchor_tolerance_m": CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M,
		}
		for required_snapshot in ["acquire_support", "late_support", "release_fallback"]:
			if not contact_lock_captures.has(required_snapshot):
				(_manifest["assertion_failures"] as Array).append({
					"scenario": scenario_name,
					"contract": "contact_lock_visual_diagnostics",
					"reason": "Required f2i_l diagnostic capture was not observed.",
					"snapshot": required_snapshot,
				})
	for expected_stage in ["entry", "travel", "exit", "settlement", "complete"]:
		var count := int(captured_sequences.get(expected_stage, 0))
		if count < CAPTURE_STAGE_FRAMES.size():
			(_manifest["absent_captures"] as Array).append({
				"run_id": _run_id,
				"scenario": scenario_name,
				"stage": expected_stage,
				"captured_sequences": count,
				"required_sequences": CAPTURE_STAGE_FRAMES.size(),
				"observed_stage_frames": int(stage_frames.get(expected_stage, 0)),
				"reason": "Stage did not remain active for all render-complete capture offsets; no prior-run image was reused.",
			})
	for candidate in candidate_records:
		var centre: int = candidate["candidate_frame"]
		var window: Array[Dictionary] = []
		for record in dense_frames:
			var recorded_frame: int = record["frame"]
			if recorded_frame >= centre - REVERSAL_TRACE_RADIUS and recorded_frame <= centre + REVERSAL_TRACE_RADIUS:
				window.append(record)
		candidate["dense_window"] = window
		(_manifest["reversal_candidates"] as Array).append(candidate)
	for candidate in raw_candidate_records:
		var centre: int = candidate["candidate_frame"]
		var window: Array[Dictionary] = []
		var maximum_opposite_speed := 0.0
		var stages: Dictionary = {}
		for record in dense_frames:
			var recorded_frame: int = record["frame"]
			if recorded_frame >= centre - REVERSAL_TRACE_RADIUS and recorded_frame <= centre + REVERSAL_TRACE_RADIUS:
				window.append(record)
				stages[record["programme_stage"]] = true
				if signf(record["realised_longitudinal_speed"]) == candidate["to_sign"]:
					maximum_opposite_speed = maxf(maximum_opposite_speed, absf(record["realised_longitudinal_speed"]))
		candidate["dense_window"] = window
		candidate["maximum_new_sign_speed"] = maximum_opposite_speed
		candidate["classification"] = "stage boundary" if stages.size() > 1 else (
			"numerical noise" if maximum_opposite_speed < REVERSAL_SPEED_THRESHOLD else "transient synthetic/root cancellation")
		(_manifest["raw_sign_change_candidates"] as Array).append(candidate)
	var planar_error := Vector2(npc.global_position.x - destination_position.x, npc.global_position.z - destination_position.z).length()
	var actor_yaw := atan2(npc.global_basis.z.x, npc.global_basis.z.z)
	# The removed forecast model has no successor members; the durable boundary measurement, confirmation
	# counter, and committed outcome records are its replacement evidence.
	var terminal_evidence := _terminal_evidence(navigation)
	var terminal_boundary_measurement: Dictionary = terminal_evidence.get("terminal_boundary_measurement", {})
	var terminal_outcomes: Array = terminal_evidence.get("terminal_outcomes", [])
	var release_phase_error := float(exit_commit_diagnostics.get("release_phase_error", INF))
	var release_time_error := float(exit_commit_diagnostics.get("release_wait_time_error_seconds", INF))
	var release_time_tolerance := 1.1 / float(Engine.physics_ticks_per_second)
	var scenario_support_assessment := _evaluate_support_scenario(
		support_samples,
		support_contact_episodes,
		support_evidence_failures)
	var support_assessment: Dictionary = scenario_support_assessment["support_assessment"]
	support_assessment["status"] = "unavailable_stage1"
	support_assessment["authoritative_contact_evidence_available"] = false
	support_assessment["required_authoritative_source"] = "A future ANIM-002 authored-contact publication and/or immutable IK-003 post-composition snapshot."
	support_assessment["evidence_pass"] = false
	support_assessment["passed"] = false
	scenario_support_assessment["passed"] = false
	(scenario_support_assessment["failure_reasons"] as Array).append("authoritative_contact_evidence_unavailable_stage1")
	var visible_support_slip_speed: float = support_assessment["visible_support_slip_speed_mps"]
	var maximum_support_slip_speed: float = support_assessment["maximum_speed_mps"]
	var maximum_support_interval_slip: float = support_assessment["maximum_interval_displacement_m"]
	var support_slip_pass: bool = scenario_support_assessment["passed"]
	var release_timing_pass := (
		release_phase_error <= RELEASE_PHASE_ERROR_TOLERANCE
		and release_time_error <= release_time_tolerance
	)
	if not release_timing_pass:
		(_manifest["assertion_failures"] as Array).append({
			"scenario": scenario_name,
			"contract": "selected_release_timing",
			"phase_error": release_phase_error,
			"maximum_phase_error": RELEASE_PHASE_ERROR_TOLERANCE,
			"time_error_seconds": release_time_error,
			"maximum_time_error_seconds": release_time_tolerance,
		})
	if not support_slip_pass:
		(_manifest["assertion_failures"] as Array).append({
			"scenario": scenario_name,
			"contract": "visible_planted_foot_slip",
			"expected_support_frames": support_assessment["expected_support_frames"],
			"accepted_support_frames": support_assessment["accepted_support_frames"],
			"rejected_support_frames": support_assessment["rejected_support_frames"],
			"rejection_reasons": support_assessment["rejection_reasons"],
			"out_of_interval_evidence_failures": support_assessment["out_of_interval_evidence_failures"],
			"empty_expected_intervals": support_assessment["empty_expected_intervals"],
			"maximum_speed_mps": maximum_support_slip_speed,
			"maximum_speed_mps_allowed": visible_support_slip_speed,
			"maximum_contact_interval_m": maximum_support_interval_slip,
			"maximum_contact_interval_m_allowed": VISIBLE_PLANTED_SLIP_PER_CONTACT_INTERVAL_M,
		})
	if scenario_name == "user_reported":
		var focused_contracts := {
			# The replacement model's exact-landing contract is the durable completed outcome record with
			# non-negative margins; the legacy forecast response errors have no successor members.
			"exact_completion": _has_single_completed_outcome(terminal_outcomes) and stability_frames >= 4,
			"bounded_release_wait": float(exit_commit_diagnostics.get("actual_release_wait_seconds", INF)) <= 1.1,
			"single_release_approach": approach_activation_count == 1,
			"terminal_budget_feasible": terminal_correction_distance <= float(navigation.get("MaximumPlanarResidualPerSegment")),
			"yaw_no_target_crossing": yaw_target_crossing_count == 0 and maximum_zero_root_yaw_assist <= 0.000001,
			"facade_programme_ordering": facade_premature_completion_frames == 0,
		}
		for contract in focused_contracts:
			if not bool(focused_contracts[contract]):
				(_manifest["assertion_failures"] as Array).append({"scenario": scenario_name, "contract": contract})
	var stage_root_synthetic_ratio := {}
	for stage_key in stage_root_distance:
		var root_distance := float(stage_root_distance[stage_key])
		var synthetic_distance := float(stage_synthetic_distance.get(stage_key, 0.0))
		stage_root_synthetic_ratio[stage_key] = synthetic_distance / root_distance if root_distance > 0.0000001 else 0.0
	var terminal_overshoot := {
		"observed": terminal_overshoot_active,
		"first_frame": terminal_overshoot_first_frame,
		"first_stage": terminal_overshoot_first_stage,
		"maximum_signed_m": -terminal_overshoot_maximum_m if terminal_overshoot_active else 0.0,
		"maximum_m": terminal_overshoot_maximum_m,
		"backtrack_frames": terminal_overshoot_backtrack_frames,
		"before_exit": terminal_overshoot_before_exit,
		"during_crossfade": terminal_overshoot_during_crossfade,
		"during_exit": terminal_overshoot_during_exit,
		"during_settlement": terminal_overshoot_during_settlement,
		"records": terminal_overshoot_records,
	}
	if terminal_overshoot_maximum_m > TERMINAL_OVERSHOOT_MATERIAL_LIMIT_M:
		(_manifest["assertion_failures"] as Array).append({
			"scenario": scenario_name,
			"contract": "terminal_route_overshoot",
			"maximum_m": terminal_overshoot_maximum_m,
			"limit_m": TERMINAL_OVERSHOOT_MATERIAL_LIMIT_M,
			"first_frame": terminal_overshoot_first_frame,
			"first_stage": terminal_overshoot_first_stage,
			"post_overshoot_backtrack_frames": terminal_overshoot_backtrack_frames,
		})
	var continuity_analysis := _analyse_continuity(dense_frames)
	var continuity_gate := {
		"status": "not_evaluated_stage1",
		"reason": "Continuity maxima are diagnostic-only in this photobooth. No empty or deferred limit set is reported as a passing gate.",
		"ungated_metrics": (continuity_analysis.maxima as Dictionary).keys(),
	}
	var cadence_evidence := {}
	for stage_key in stage_frame_counts:
		var frames := int(stage_frame_counts[stage_key])
		var root_distance := float(stage_root_duration_distance.get(stage_key, 0.0))
		var seconds := frames / float(Engine.physics_ticks_per_second)
		cadence_evidence[stage_key] = {
			"frames": frames,
			"wall_seconds": seconds,
			"root_distance_m": root_distance,
			"average_root_speed_mps": root_distance / maxf(seconds, 0.000001),
		}
	var metrics := {
		"kind": "scenario_metrics",
		"run_id": _run_id,
		"scenario": scenario_name,
		"request_result": request_result,
		"position_error": planar_error,
		"position_error_world": Vector3(destination_position.x - npc.global_position.x, 0.0, destination_position.z - npc.global_position.z),
		"yaw_error": absf(wrapf(data[1] - actor_yaw, -PI, PI)),
		"completion_frame": completion_frame,
		"stability_frames": stability_frames,
		"required_stable_frames": 4,
		"completed_with_required_stability": completion_frame >= 0 and stability_frames >= 4,
		"release_timing_within_tolerance": release_timing_pass,
		"support_slip_within_visible_frame_budget": support_slip_pass,
		"visible_support_slip_speed_budget_mps": visible_support_slip_speed,
		"visible_support_slip_contact_interval_budget_m": VISIBLE_PLANTED_SLIP_PER_CONTACT_INTERVAL_M,
		"maximum_support_slip_contact_interval_m": maximum_support_interval_slip,
		"entry_count": navigation.get("EntryCount"),
		"exit_count": navigation.get("ExitCount"),
		"idle_crossings": navigation.get("IdleCrossingCount"),
		"entry_restarts": maxi(int(navigation.get("EntryCount")) - 1, 0),
		"internal_idle_count": navigation.get("IdleCrossingCount"),
		"domain_handoff_count": navigation.get("DomainHandoffCount"),
		"forward_back_reversals": navigation.get("ObjectiveReversalCount"),
		"raw_actor_local_longitudinal_sign_changes": raw_sign_change_count,
		"raw_longitudinal_sign_changes": raw_sign_change_count,
		"y_variation": max_y - min_y,
		"timeout_frames": frame_limit,
		"probe_scope": "pose_proof" if frame_limit < 600 else "strict_completion",
		"physical_steps": int(navigation.get("PhysicalStepCount")) - baseline_steps,
		"travel_frames": maxi(travel_end_frame - travel_start_frame + 1, 0),
		"travel_phase_span": _circular_phase_span(travel_phases),
		"travel_root_frames": travel_root_frames,
		"sustained_synthetic_planar": sustained_synthetic_planar,
		"exit_root_planar": exit_root_planar,
		"exit_crossfade_root_planar": exit_crossfade_root_planar,
		"exit_remaining_root_planar": exit_remaining_root_planar,
		"exit_synthetic_planar": exit_synthetic_planar,
		"exit_collision_realised_planar": exit_realised_planar,
		"exit_commit": exit_commit_diagnostics,
		"terminal_boundary_measurement": terminal_boundary_measurement,
		"terminal_boundary_measurement_available": bool(terminal_evidence.get("terminal_boundary_measurement_available", false)),
		"terminal_boundary_state": int(terminal_evidence.get("terminal_boundary_state", 0)),
		"terminal_confirmation_frame_count": int(terminal_evidence.get("terminal_confirmation_frame_count", 0)),
		"terminal_outcomes": terminal_outcomes,
		"terminal_correction_distance": terminal_correction_distance,
		"terminal_correction_vector": terminal_correction_vector,
		"terminal_correction_frames": terminal_correction_frames,
		"terminal_correction_seconds": terminal_correction_frames / float(Engine.physics_ticks_per_second),
		"terminal_correction_by_stage": terminal_correction_by_stage,
		"stage_root_distance": stage_root_distance,
		"stage_synthetic_distance": stage_synthetic_distance,
		"stage_root_synthetic_ratio": stage_root_synthetic_ratio,
		"terminal_overshoot": terminal_overshoot,
		"cadence_evidence": cadence_evidence,
		"release_wait_frames": release_wait_frames,
		"cumulative_release_wait_seconds": release_wait_frames / float(Engine.physics_ticks_per_second),
		"release_rearm_count": maxi(approach_activation_count - 1, 0),
		"approach_activation_count": approach_activation_count,
		"candidate_rejection_summary": navigation.get("LastCandidateRejectionSummary"),
		"release_wait_distance": release_wait_distance,
		"release_wait_vector": release_wait_vector,
		"release_wait_actual_seconds": exit_commit_diagnostics.get("actual_release_wait_seconds", INF),
		"release_wait_selection_seconds": exit_commit_diagnostics.get("selection_time_to_release_seconds", NAN),
		"post_root_frames": post_root_frames,
		"post_root_distance": post_root_distance,
		"root_zero_synthetic_frames": root_zero_synthetic_frames,
		"root_zero_synthetic_distance": root_zero_synthetic_distance,
		"moving_yaw_assist_radians": moving_yaw_assist_radians,
		"moving_yaw_assist_frames": moving_yaw_assist_frames,
		"maximum_zero_root_yaw_assist_rate": maximum_zero_root_yaw_assist,
		"yaw_target_crossing_count": yaw_target_crossing_count,
		"facade_premature_completion_frames": facade_premature_completion_frames,
		"ledger_realised_planar": navigation.get("LastRealisedPlanarCorrection"),
		"ledger_capacity_planar": navigation.get("MaximumPlanarResidualPerSegment"),
		"ledger_remaining_planar": navigation.get("RemainingPlanarCorrection"),
		"ledger_reserved_planar": navigation.get("ReservedTerminalPlanar"),
		"ledger_peak_reserved_planar": peak_reserved_planar,
		"ledger_realised_yaw": navigation.get("LastRealisedYawCorrection"),
		"ledger_capacity_yaw": navigation.get("MaximumYawResidualPerSegment"),
		"ledger_remaining_yaw": navigation.get("RemainingYawCorrection"),
		"ledger_reserved_yaw": navigation.get("ReservedTerminalYaw"),
		"ledger_peak_reserved_yaw": peak_reserved_yaw,
		"ledger_reset_frames": ledger_reset_frames,
		"ledger_reset_detected": not ledger_reset_frames.is_empty(),
		"latest_selected_exit_role": navigation.get("LastSelectedExitRole"),
		"latest_terminal_evidence": terminal_evidence,
		"maximum_transition_foot_jump": maximum_transition_foot_jump,
		"maximum_transition_hips_jump": maximum_transition_hips_jump,
		"maximum_root_translation_residual_m": maximum_root_translation_residual,
		"maximum_hips_translation_residual_m": maximum_hips_translation_residual,
		"maximum_foot_translation_residual_m": maximum_foot_translation_residual,
		"maximum_root_rotation_residual_rad": maximum_root_rotation_residual,
		"maximum_hips_rotation_residual_rad": maximum_hips_rotation_residual,
		"maximum_foot_rotation_residual_rad": maximum_foot_rotation_residual,
		"support_evidence_channels": {
			"navigation_phase_diagnostics": "Navigation.PhaseLane and LastEvaluatedNormalisedPhase are diagnostic-only; neither can make this assessment pass.",
			"pre_ik_pose_channel": "unavailable",
			"post_ik_pose_channel": "unavailable_stage1_no_composition_claim",
			"skeleton_world_pose_observation": "observed_ik_composition_unspecified",
		},
		"support_assessment_status": "unavailable_stage1: cannot pass without future ANIM-002 authored-contact evidence and/or an immutable IK-003 post-composition snapshot.",
		"support_slip_sample_count": support_samples.size(),
		"support_samples": support_samples,
		"support_contact_episodes": support_contact_episodes,
		"support_assessment": support_assessment,
		"mean_planted_foot_slip_mps": support_assessment["mean_speed_mps"],
		"p95_planted_foot_slip_mps": support_assessment["p95_speed_mps"],
		"maximum_planted_foot_slip_mps": maximum_support_slip_speed,
		"accumulated_planted_foot_slip": support_assessment["accumulated_displacement_m"],
		"normal_gait_frame_jump_median": _percentile(normal_gait_frame_jumps, 0.5),
		"normal_gait_frame_jump_p95": _percentile(normal_gait_frame_jumps, 0.95),
		"transition_jumps": transition_jumps,
		"idle_planar_translation": idle_planar_translation,
		"ordering_mismatch_frames": ordering_mismatch_frames,
		"last_stage": last_stage,
		"animation_tree_active": animation_tree.active,
		"animation_libraries": animation_player.get_animation_library_list(),
		"subject_identity": _subject_identity,
		"animation_graph_path": _template_evidence.get("animation_graph_source_path", ""),
		"animation_graph_uid": _template_evidence.get("animation_graph_source_uid", ""),
		"selected_character_variant": _manifest.get("selected_character_variant", ""),
		"continuity_analysis": continuity_analysis,
		"continuity_gate": continuity_gate,
	}
	if scenario_name in ["exact_reported", "user_reported"]:
		_template_evidence["pose_deformation"] = {
			"left_upper_arm_max_rotation_rad": maximum_left_upper_arm_rotation,
			"right_upper_arm_max_rotation_rad": maximum_right_upper_arm_rotation,
			"left_upper_arm_deformed": maximum_left_upper_arm_rotation > 0.01,
			"right_upper_arm_deformed": maximum_right_upper_arm_rotation > 0.01,
		}
	trace.store_line(JSON.stringify(metrics))
	(_manifest["physical_metrics"] as Array).append(metrics)


func _save_capture(
	scenario_name: String,
	stage_label: String,
	sequence_index: int,
	view: String,
	physics_frame_index: int,
	camera: Camera3D,
	npc: CharacterBody3D,
	navigation,
	skeleton: Skeleton3D,
	bone_indices: Dictionary
) -> void:
	RenderingServer.force_draw(true, 0.0)
	await RenderingServer.frame_post_draw
	var actual_stage: int = navigation.get("ProgrammeStage")
	if CAPTURE_STAGES.get(actual_stage, "") != stage_label:
		(_manifest["absent_captures"] as Array).append({
			"run_id": _run_id,
			"scenario": scenario_name,
			"stage": stage_label,
			"view": view,
			"sequence": sequence_index,
			"reason": "Programme stage changed before render-complete capture.",
		})
		return
	var filename := "%s_%s_%s_%02d.png" % [scenario_name, stage_label, view, sequence_index]
	var path := "%s/%s" % [_run_dir, filename]
	var image := camera.get_viewport().get_texture().get_image()
	if image.save_png(path) != OK:
		push_error("Failed to save screenshot: %s" % path)
		return
	(_manifest["captures"] as Array).append({
		"run_id": _run_id,
		"scenario": scenario_name,
		"physics_frame": physics_frame_index,
		"programme_stage": actual_stage,
		"stage": stage_label,
		"view": view,
		"sequence": sequence_index,
		"file": filename,
		"modified_unix": FileAccess.get_modified_time(path),
		"sha256": FileAccess.get_sha256(path),
		"state": str(navigation.get("LastEvaluatedState")),
		"play_position": navigation.get("LastEvaluatedPlayPosition"),
		"phase": navigation.get("LastEvaluatedNormalisedPhase"),
		"blend_point": str(navigation.get("LastBlendPoint")),
		"authored_root_world_delta": str(navigation.get("LastAuthoredRootWorldDelta")),
		"requested_synthetic_world_delta": str(navigation.get("LastRequestedSyntheticWorldDelta")),
		"realised_world_delta": str(navigation.get("LastRealisedDisplacement")),
		"animation_graph_uid": _template_evidence.get("animation_graph_source_uid", ""),
		"actor_transform": str(npc.global_transform),
		"camera_transform": str(camera.global_transform),
		"bones": _capture_bones(skeleton, bone_indices),
	})


func _capture_bones(skeleton: Skeleton3D, bone_indices: Dictionary) -> Dictionary:
	var result := {}
	for bone_name in bone_indices:
		var index: int = bone_indices[bone_name]
		result[bone_name] = str(skeleton.get_bone_global_pose(index)) if index >= 0 else "absent"
	return result


func _diagnostic_pose_record(
	frame: int,
	previous_stage: int,
	stage: int,
	npc: CharacterBody3D,
	navigation,
	animation_tree: AnimationTree,
	skeleton: Skeleton3D,
	bone_indices: Dictionary
) -> Dictionary:
	var root_accumulator := animation_tree.get_root_motion_position_accumulator()
	var root_rotation_accumulator := animation_tree.get_root_motion_rotation_accumulator()
	var root_reference := navigation.get("RootMotionReference") as Node3D
	var root_accumulator_world := root_reference.global_basis.orthonormalized() * root_accumulator
	var boundary := ""
	if previous_stage >= 0 and previous_stage != stage:
		boundary = "%s_to_%s" % [_stage_name(previous_stage), _stage_name(stage)]
	return {
		"kind": "npc_diagnostic_frame",
		"physics_frame": navigation.get("PhysicalStepCount"),
		"elapsed_physics_seconds": float(navigation.get("PhysicalStepCount")) / float(Engine.physics_ticks_per_second),
		"programme_stage": stage,
		"programme_stage_name": _stage_name(stage),
		"previous_programme_stage": previous_stage,
		"previous_programme_stage_name": _stage_name(previous_stage),
		"boundary": boundary,
		"is_navigation_running": navigation.get("IsNavigationRunning"),
		"actor_world_transform": str(npc.global_transform),
		"actor_world_position": str(npc.global_position),
		"actor_world_yaw": atan2(npc.global_basis.z.x, npc.global_basis.z.z),
		"animation_state": str(navigation.get("LastEvaluatedState")),
		"animation_fading_from_state": str(navigation.get("LastFadingFromState")),
		"animation_play_position": navigation.get("LastEvaluatedPlayPosition"),
		"animation_normalised_phase": navigation.get("LastEvaluatedNormalisedPhase"),
		"root_skeleton_local_delta": str(navigation.get("LastEvaluatedRootDelta")),
		"root_world_delta": str(navigation.get("LastAuthoredRootWorldDelta")),
		"root_rotation_delta": str(navigation.get("LastEvaluatedRootRotation")),
		"root_position_accumulator_skeleton_local": str(root_accumulator),
		"root_position_accumulator_world_oriented": str(root_accumulator_world),
		"root_rotation_accumulator_skeleton_local": str(root_rotation_accumulator),
		"bones": _diagnostic_bones(skeleton, bone_indices),
		"runner_frame": frame,
	}


func _diagnostic_bones(skeleton: Skeleton3D, bone_indices: Dictionary) -> Dictionary:
	var result := {}
	for bone_name in ["Root", "Hips", "LeftFoot", "RightFoot"]:
		var index: int = bone_indices[bone_name]
		if index < 0:
			result[bone_name] = {"present": false}
			continue
		var skeleton_pose := skeleton.get_bone_global_pose(index)
		var world_pose := Transform3D(
			skeleton.global_basis * skeleton_pose.basis,
			skeleton.to_global(skeleton_pose.origin))
		result[bone_name] = {
			"present": true,
			"skeleton_transform": str(skeleton_pose),
			"skeleton_position": str(skeleton_pose.origin),
			"skeleton_rotation": str(skeleton_pose.basis.get_rotation_quaternion()),
			"world_transform": str(world_pose),
			"world_position": str(world_pose.origin),
			"world_rotation": str(world_pose.basis.orthonormalized().get_rotation_quaternion()),
		}
	return result


func _stage_name(stage: int) -> String:
	return STAGE_NAMES.get(stage, "Unknown(%d)" % stage)


func _terminal_evidence(navigation) -> Dictionary:
	# NPCNavigation.GetTerminalEvidenceSnapshot exposes the replacement terminal model's structured evidence
	# (selected occurrence, boundary measurement, confirmation counter, durable outcome records, candidate
	# decision) in a GDScript-readable form.
	var evidence: Variant = navigation.call("GetTerminalEvidenceSnapshot")
	return evidence if evidence is Dictionary else {}


func _selected_candidate_summary(navigation) -> Dictionary:
	var decision: Dictionary = _terminal_evidence(navigation).get("terminal_candidate_decision", {})
	if not bool(decision.get("scheduler_commit", false)) and not bool(decision.get("terminal_release_selected", false)):
		return {}
	var selected_index := int(decision.get("selected_candidate_index", -1))
	for candidate_key in ["same_lane_candidate", "opposite_lane_candidate"]:
		var candidate: Dictionary = decision.get(candidate_key, {})
		if bool(candidate.get("is_evaluated", false)) and int(candidate.get("candidate_index", -2)) == selected_index:
			return candidate
	for candidate_key in ["same_lane_candidate", "opposite_lane_candidate"]:
		var fallback: Dictionary = decision.get(candidate_key, {})
		if bool(fallback.get("is_evaluated", false)):
			return fallback
	return {}


func _has_single_completed_outcome(outcomes: Array) -> bool:
	if outcomes.size() != 1:
		return false
	var outcome: Dictionary = outcomes[0]
	return str(outcome.get("outcome", "")) == "Completed" \
		and str(outcome.get("reason", "")) == "DestinationReached" \
		and float(outcome.get("planar_margin_m", -1.0)) >= 0.0 \
		and float(outcome.get("yaw_margin_rad", -1.0)) >= 0.0


func _capture_dense_views(
	scenario_name: String,
	label: String,
	sequence: int,
	physics_frame_index: int,
	camera: Camera3D,
	npc: CharacterBody3D,
	navigation,
	skeleton: Skeleton3D,
	bone_indices: Dictionary
) -> void:
	paused = true
	for view: String in ["candidate_side", "candidate_lower_body", "candidate_top_route"]:
		_configure_candidate_camera(view, camera, npc, navigation.get("LastRouteTangent"))
		await process_frame
		RenderingServer.force_draw(true, 0.0)
		await RenderingServer.frame_post_draw
		var filename := "%s_%s_%02d_%s.png" % [scenario_name, label, sequence, view]
		var path := "%s/%s" % [_run_dir, filename]
		if camera.get_viewport().get_texture().get_image().save_png(path) == OK:
			(_manifest["captures"] as Array).append({
				"run_id": _run_id,
				"scenario": scenario_name,
				"stage": label,
				"sequence": sequence,
				"view": view,
				"physics_frame": physics_frame_index,
				"file": filename,
				"state": str(navigation.get("LastEvaluatedState")),
				"fading_from_state": str(navigation.get("LastFadingFromState")),
				"phase": navigation.get("LastEvaluatedNormalisedPhase"),
				"blend_point": str(navigation.get("LastBlendPoint")),
				"authored_root_world_delta": str(navigation.get("LastAuthoredRootWorldDelta")),
				"requested_synthetic_world_delta": str(navigation.get("LastRequestedSyntheticWorldDelta")),
				"realised_world_delta": str(navigation.get("LastRealisedDisplacement")),
				"bones": _capture_bones(skeleton, bone_indices),
				"animation_graph_uid": _template_evidence.get("animation_graph_source_uid", ""),
			})
	paused = false


func _bone_world_positions(skeleton: Skeleton3D, bone_indices: Dictionary) -> Dictionary:
	var result := {}
	for bone_name in bone_indices:
		var index: int = bone_indices[bone_name]
		result[bone_name] = skeleton.to_global(skeleton.get_bone_global_pose(index).origin)
	return result


func _bone_world_rotations(skeleton: Skeleton3D, bone_indices: Dictionary) -> Dictionary:
	var result := {}
	for bone_name in bone_indices:
		var index: int = bone_indices[bone_name]
		result[bone_name] = (skeleton.global_transform * skeleton.get_bone_global_pose(index)).basis.get_rotation_quaternion()
	return result


func _finalize_support_episode(episode: Dictionary, episodes: Array[Dictionary]) -> void:
	var start_world: Vector3 = episode["start_world"]
	var end_world: Vector3 = episode["end_world"]
	episode["net_world_displacement_m"] = Vector2(end_world.x - start_world.x, end_world.z - start_world.z).length()
	episodes.append(episode.duplicate(true))


func _new_support_episode(frame: int, foot: String, anchor_world: Vector3) -> Dictionary:
	return {
		"foot": foot,
		"start_frame": frame,
		"end_frame": frame,
		"start_world": anchor_world,
		"end_world": anchor_world,
		"anchor_world": anchor_world,
		"anchor_frame": frame - 1,
		"last_sample_frame": frame - 1,
		"expected_frames": 0,
		"accepted_frames": 0,
		"rejected_frames": 0,
		"accumulated_displacement_m": 0.0,
		"maximum_speed_mps": 0.0,
		"rejection_reasons": {},
	}


func _update_support_episode(episode: Dictionary, sample: Dictionary) -> void:
	episode["end_frame"] = sample["frame"]
	episode["expected_frames"] = int(episode["expected_frames"]) + 1
	episode["last_sample_frame"] = sample["frame"]
	if bool(sample["accepted"]):
		episode["end_world"] = sample["current_world"]
		episode["accepted_frames"] = int(episode["accepted_frames"]) + 1
		episode["accumulated_displacement_m"] = float(episode["accumulated_displacement_m"]) + float(sample["planar_displacement_m"])
		episode["maximum_speed_mps"] = maxf(float(episode["maximum_speed_mps"]), float(sample["planar_speed_mps"]))
	else:
		episode["rejected_frames"] = int(episode["rejected_frames"]) + 1
		for reason in sample["rejection_reasons"]:
			var reasons: Dictionary = episode["rejection_reasons"]
			reasons[reason] = int(reasons.get(reason, 0)) + 1


func _evaluate_support_sample(input: Dictionary) -> Dictionary:
	var frame := int(input.get("frame", -1))
	var authoritative_evidence: Variant = input.get("authoritative_contact_evidence", null)
	var authoritative_evidence_supplied := authoritative_evidence is Dictionary \
		and not (authoritative_evidence as Dictionary).is_empty()
	var current_world := input.get("current_world", Vector3(NAN, NAN, NAN)) as Vector3
	var previous_world := input.get("previous_world", Vector3(NAN, NAN, NAN)) as Vector3
	var anchor_world := input.get("anchor_world", Vector3(NAN, NAN, NAN)) as Vector3
	var reasons: Array[String] = []
	if not authoritative_evidence_supplied:
		reasons.append("authoritative_contact_evidence_unavailable_stage1")
	var planar_displacement := Vector2(current_world.x - previous_world.x, current_world.z - previous_world.z).length()
	var planar_speed := planar_displacement * Engine.physics_ticks_per_second
	var anchor_displacement := Vector2(current_world.x - anchor_world.x, current_world.z - anchor_world.z).length()
	var finite_measurement := is_finite(planar_displacement) and is_finite(planar_speed) and is_finite(anchor_displacement)
	return {
		"frame": frame,
		"declared_support_foot": input.get("declared_support_foot", ""),
		"observed_foot": input.get("observed_foot", ""),
		"phase_lane": input.get("phase_lane", null),
		"phase": input.get("phase", NAN),
		"contact_evidence_available": authoritative_evidence_supplied,
		"contact_evidence_source": input.get("contact_evidence_source", "unavailable_stage1"),
		"authoritative_contact_evidence": authoritative_evidence,
		"anchor_frame": input.get("anchor_frame", -1),
		"anchor_last_verified_frame": input.get("anchor_last_verified_frame", -1),
		"current_world": current_world,
		"previous_world": previous_world,
		"anchor_world": anchor_world,
		"height_above_lower_foot_m": input.get("height_above_lower_foot_m", NAN),
		"vertical_speed_mps": input.get("vertical_speed_mps", NAN),
		"planar_displacement_m": planar_displacement,
		"planar_speed_mps": planar_speed,
		"anchor_displacement_m": anchor_displacement,
		"accepted": reasons.is_empty(),
		"verdict": "accepted" if reasons.is_empty() else "rejected",
		"rejection_reasons": reasons,
		"within_frame_slip_budget": finite_measurement and planar_displacement <= VISIBLE_PLANTED_SLIP_PER_FRAME_M,
	}


func _evaluate_support_collection(samples: Array[Dictionary], episodes: Array[Dictionary]) -> Dictionary:
	var accepted_speeds: Array[float] = []
	var accumulated_displacement := 0.0
	var rejection_reasons := {}
	for sample in samples:
		if bool(sample["accepted"]):
			accepted_speeds.append(float(sample["planar_speed_mps"]))
			accumulated_displacement += float(sample["planar_displacement_m"])
		else:
			for reason in sample["rejection_reasons"]:
				rejection_reasons[reason] = int(rejection_reasons.get(reason, 0)) + 1
	var empty_expected_intervals: Array[Dictionary] = []
	var maximum_interval_displacement := 0.0
	for episode in episodes:
		maximum_interval_displacement = maxf(maximum_interval_displacement, float(episode.get("accumulated_displacement_m", 0.0)))
		if int(episode.get("expected_frames", 0)) > 0 and int(episode.get("accepted_frames", 0)) == 0:
			empty_expected_intervals.append(episode.duplicate(true))
	var expected_support_frames := samples.size()
	var accepted_support_frames := accepted_speeds.size()
	var visible_support_slip_speed := VISIBLE_PLANTED_SLIP_PER_FRAME_M * Engine.physics_ticks_per_second
	var maximum_speed := _percentile(accepted_speeds, 1.0)
	var coverage_pass := expected_support_frames > 0 and accepted_support_frames > 0 and empty_expected_intervals.is_empty()
	var evidence_pass := rejection_reasons.is_empty()
	# Local slip budgets remain trace diagnostics until an authoritative evidence object is available.
	var threshold_evaluated := false
	var threshold_pass := false
	return {
		"expected_support_frames": expected_support_frames,
		"accepted_support_frames": accepted_support_frames,
		"rejected_support_frames": expected_support_frames - accepted_support_frames,
		"rejection_reasons": rejection_reasons,
		"empty_expected_intervals": empty_expected_intervals,
		"coverage_pass": coverage_pass,
		"evidence_pass": evidence_pass,
		"threshold_evaluated": threshold_evaluated,
		"threshold_pass": threshold_pass,
		"passed": coverage_pass and evidence_pass,
		"visible_support_slip_speed_mps": visible_support_slip_speed,
		"maximum_speed_mps": maximum_speed,
		"maximum_interval_displacement_m": maximum_interval_displacement,
		"mean_speed_mps": _mean(accepted_speeds),
		"p95_speed_mps": _percentile(accepted_speeds, 0.95),
		"accumulated_displacement_m": accumulated_displacement,
	}


func _evaluate_support_scenario(
	samples: Array[Dictionary],
	episodes: Array[Dictionary],
	out_of_interval_evidence_failures: Array[Dictionary] = []
) -> Dictionary:
	var support_assessment := _evaluate_support_collection(samples, episodes)
	support_assessment["out_of_interval_evidence_failures"] = out_of_interval_evidence_failures
	if not out_of_interval_evidence_failures.is_empty():
		support_assessment["evidence_pass"] = false
		support_assessment["passed"] = false
	var failure_reasons: Array[String] = []
	if not bool(support_assessment["coverage_pass"]):
		failure_reasons.append("insufficient_support_coverage")
	if not bool(support_assessment["evidence_pass"]):
		for rejection_reason in (support_assessment["rejection_reasons"] as Dictionary):
			failure_reasons.append("rejected_support_evidence:%s" % rejection_reason)
		if not out_of_interval_evidence_failures.is_empty():
			failure_reasons.append("out_of_interval_support_evidence")
	if bool(support_assessment["threshold_evaluated"]) and not bool(support_assessment["threshold_pass"]):
		failure_reasons.append("visible_support_slip_threshold")
	return {
		"passed": bool(support_assessment["passed"]),
		"failure_reasons": failure_reasons,
		"support_assessment": support_assessment,
	}


func _support_contact_mutation_guards() -> Dictionary:
	var baseline := {
		"frame": 10,
		"declared_support_foot": "LeftFoot",
		"observed_foot": "LeftFoot",
		"phase_lane_valid": true,
		"phase": (SUPPORT_PHASE_START + SUPPORT_PHASE_END) * 0.5,
		"contact_evidence_available": true,
		"contact_confidence": 1.0,
		"contact_evidence_frame": 10,
		"previous_world": Vector3.ZERO,
		"current_world": Vector3.ZERO,
		"height_above_lower_foot_m": 0.0,
		"vertical_speed_mps": 0.0,
		"anchor_world": Vector3.ZERO,
		"anchor_frame": 9,
		"anchor_last_verified_frame": 9,
	}
	var translated := baseline.duplicate(true)
	translated["current_world"] = Vector3(VISIBLE_PLANTED_SLIP_PER_FRAME_M * 2.0, 0.0, 0.0)
	var geometry := baseline.duplicate(true)
	geometry["height_above_lower_foot_m"] = SUPPORT_HEIGHT_TOLERANCE_M * 2.0
	geometry["vertical_speed_mps"] = SUPPORT_VERTICAL_SPEED_TOLERANCE_MPS * 2.0
	var phase := baseline.duplicate(true)
	phase["phase"] = SUPPORT_PHASE_END + 0.25
	phase["phase_lane_valid"] = false
	phase["declared_support_foot"] = "RightFoot"
	phase["observed_foot"] = "RightFoot"
	return {
		"phase_lane_and_window_cannot_authorise_support": _evaluate_support_mutation_guard(baseline),
		"geometry_cannot_authorise_support": _evaluate_support_mutation_guard(geometry),
		"local_slip_limit_cannot_authorise_support": _evaluate_support_mutation_guard(translated),
		"phase_mutation_cannot_change_authority": _evaluate_support_mutation_guard(phase),
		"empty_interval": _evaluate_empty_support_interval_mutation_guard(),
	}


func _evaluate_support_mutation_guard(
	input: Dictionary
) -> Dictionary:
	var sample := _evaluate_support_sample(input)
	var episode := {
		"expected_frames": 1,
		"accepted_frames": 1 if bool(sample["accepted"]) else 0,
		"accumulated_displacement_m": float(sample["planar_displacement_m"]) if bool(sample["accepted"]) else 0.0,
	}
	var scenario_assessment := _evaluate_support_scenario([sample], [episode])
	var final_failure_reasons: Array = scenario_assessment["failure_reasons"]
	var sample_rejection_reasons: Array = sample["rejection_reasons"]
	const EXPECTED_SAMPLE_REJECTION := "authoritative_contact_evidence_unavailable_stage1"
	const EXPECTED_FINAL_FAILURE := "rejected_support_evidence:authoritative_contact_evidence_unavailable_stage1"
	return {
		"failed_as_expected": not bool(scenario_assessment["passed"])
			and EXPECTED_FINAL_FAILURE in final_failure_reasons
			and EXPECTED_SAMPLE_REJECTION in sample_rejection_reasons,
		"expected_sample_rejection": EXPECTED_SAMPLE_REJECTION,
		"expected_final_failure": EXPECTED_FINAL_FAILURE,
		"sample": sample,
		"final_result": scenario_assessment,
	}


func _evaluate_empty_support_interval_mutation_guard() -> Dictionary:
	var scenario_assessment := _evaluate_support_scenario(
		[], [{"expected_frames": 1, "accepted_frames": 0, "accumulated_displacement_m": 0.0}])
	var final_failure_reasons: Array = scenario_assessment["failure_reasons"]
	return {
		"failed_as_expected": not bool(scenario_assessment["passed"])
			and "insufficient_support_coverage" in final_failure_reasons,
		"expected_final_failure": "insufficient_support_coverage",
		"final_result": scenario_assessment,
	}


func _active_blend_triangle(tree: AnimationTree, point: Vector2) -> int:
	var root_tree := tree.tree_root as AnimationNodeBlendTree
	var states := root_tree.get_node(&"States") as AnimationNodeStateMachine
	var travel := states.get_node(&"NPCTravel") as AnimationNodeBlendTree
	var space := travel.get_node(&"Sustained") as AnimationNodeBlendSpace2D
	for triangle_index in space.get_triangle_count():
		var a := space.get_triangle_point(triangle_index, 0)
		var b := space.get_triangle_point(triangle_index, 1)
		var c := space.get_triangle_point(triangle_index, 2)
		var bary := _barycentric(
			point,
			space.get_blend_point_position(a),
			space.get_blend_point_position(b),
			space.get_blend_point_position(c))
		if bary.x >= -0.0001 and bary.y >= -0.0001 and bary.z >= -0.0001:
			return triangle_index
	return -1


func _phase_advance(previous: float, current: float) -> float:
	return current - previous if current >= previous else 1.0 - previous + current


func _terminal_route_projection(destination: Vector3, position: Vector3, tangent: Vector2) -> Dictionary:
	var safe_tangent := tangent.normalized() if tangent.length_squared() > 0.0000001 else Vector2(0.0, -1.0)
	var remaining := Vector2(destination.x - position.x, destination.z - position.z)
	return {
		"remaining_signed_m": remaining.dot(safe_tangent),
		"lateral_error_m": absf(remaining.dot(Vector2(-safe_tangent.y, safe_tangent.x))),
	}


func _analyse_continuity(frames: Array[Dictionary]) -> Dictionary:
	var maxima := {
		"actor_speed": _empty_peak(),
		"actor_speed_discontinuity": _empty_peak(),
		"actor_acceleration": _empty_peak(),
		"actor_tangential_acceleration": _empty_peak(),
		"actor_normal_acceleration": _empty_peak(),
		"actor_jerk": _empty_peak(),
		"actor_tangential_jerk": _empty_peak(),
		"actor_normal_jerk": _empty_peak(),
		"root_speed": _empty_peak(),
		"root_speed_discontinuity": _empty_peak(),
		"root_acceleration": _empty_peak(),
		"root_tangential_acceleration": _empty_peak(),
		"root_normal_acceleration": _empty_peak(),
		"yaw_rate": _empty_peak(),
		"yaw_rate_discontinuity": _empty_peak(),
		"yaw_acceleration": _empty_peak(),
		"blend_speed": _empty_peak(),
		"root_jump": _empty_peak(),
		"hips_jump": _empty_peak(),
		"left_foot_jump": _empty_peak(),
		"right_foot_jump": _empty_peak(),
	}
	var events: Array[Dictionary] = []
	var low_motion_intervals: Array[Dictionary] = []
	var active_low := {}
	var previous_state := ""
	var previous_yaw_error_sign := 0.0
	var yaw_target_crossing_frames: Array[int] = []
	for record in frames:
		_update_peak(maxima.actor_speed, record, "actor_planar_speed_mps")
		_update_peak(maxima.actor_speed_discontinuity, record, "actor_speed_discontinuity_mps")
		_update_peak(maxima.actor_acceleration, record, "actor_planar_acceleration_mps2")
		_update_peak(maxima.actor_tangential_acceleration, record, "actor_tangential_acceleration_mps2", true)
		_update_peak(maxima.actor_normal_acceleration, record, "actor_normal_acceleration_mps2", true)
		_update_peak(maxima.actor_jerk, record, "actor_planar_jerk_mps3")
		_update_peak(maxima.actor_tangential_jerk, record, "actor_tangential_jerk_mps3", true)
		_update_peak(maxima.actor_normal_jerk, record, "actor_normal_jerk_mps3", true)
		_update_peak(maxima.root_speed, record, "root_planar_speed_mps")
		_update_peak(maxima.root_speed_discontinuity, record, "root_speed_discontinuity_mps")
		_update_peak(maxima.root_acceleration, record, "root_planar_acceleration_mps2")
		_update_peak(maxima.root_tangential_acceleration, record, "root_tangential_acceleration_mps2", true)
		_update_peak(maxima.root_normal_acceleration, record, "root_normal_acceleration_mps2", true)
		_update_peak(maxima.yaw_rate, record, "yaw_rate_radps", true)
		_update_peak(maxima.yaw_rate_discontinuity, record, "yaw_rate_discontinuity_radps")
		_update_peak(maxima.yaw_acceleration, record, "yaw_acceleration_radps2", true)
		_update_peak(maxima.blend_speed, record, "blend_speed_units_per_second")
		_update_peak(maxima.root_jump, record, "root_bone_world_jump_m")
		_update_peak(maxima.hips_jump, record, "hips_world_jump_m")
		_update_peak(maxima.left_foot_jump, record, "left_foot_world_jump_m")
		_update_peak(maxima.right_foot_jump, record, "right_foot_world_jump_m")
		var event_types: Array[String] = []
		if not str(record.get("boundary", "")).is_empty():
			event_types.append("programme_boundary")
		if bool(record.get("blend_triangle_changed", false)):
			event_types.append("blend_triangle_change")
		if bool(record.get("release_approach_changed", false)):
			event_types.append("release_approach_change")
		var state := str(record.get("state", ""))
		if not previous_state.is_empty() and state != previous_state:
			event_types.append("animation_state_change")
		if not event_types.is_empty():
			events.append(_continuity_event(record, event_types))
		previous_state = state
		var yaw_error := float(record.get("signed_yaw_error_rad", 0.0))
		var yaw_error_sign := signf(yaw_error) if absf(yaw_error) > 0.0001 else 0.0
		if yaw_error_sign != 0.0 and previous_yaw_error_sign != 0.0 and yaw_error_sign != previous_yaw_error_sign:
			yaw_target_crossing_frames.append(int(record.frame))
		if yaw_error_sign != 0.0:
			previous_yaw_error_sign = yaw_error_sign
		if bool(record.get("low_net_motion_gait", false)):
			if active_low.is_empty():
				active_low = {
					"start_frame": record.frame,
					"end_frame": record.frame,
					"frames": 0,
					"net_motion_m": 0.0,
					"phase_advance": 0.0,
					"maximum_foot_jump_m": 0.0,
				}
			active_low.end_frame = record.frame
			active_low.frames = int(active_low.frames) + 1
			active_low.net_motion_m = float(active_low.net_motion_m) + float(record.actor_planar_speed_mps) / float(Engine.physics_ticks_per_second)
			active_low.phase_advance = float(active_low.phase_advance) + float(record.phase_advance)
			active_low.maximum_foot_jump_m = maxf(
				float(active_low.maximum_foot_jump_m),
				maxf(float(record.left_foot_world_jump_m), float(record.right_foot_world_jump_m)))
		elif not active_low.is_empty():
			low_motion_intervals.append(active_low.duplicate(true))
			active_low = {}
	if not active_low.is_empty():
		low_motion_intervals.append(active_low.duplicate(true))
	return {
		"maxima": maxima,
		"directional_decomposition": {
			"actor_tangential_acceleration_peak": maxima.actor_tangential_acceleration,
			"actor_normal_acceleration_peak": maxima.actor_normal_acceleration,
			"actor_tangential_jerk_peak": maxima.actor_tangential_jerk,
			"actor_normal_jerk_peak": maxima.actor_normal_jerk,
			"root_tangential_acceleration_peak": maxima.root_tangential_acceleration,
			"root_normal_acceleration_peak": maxima.root_normal_acceleration,
			"yaw_target_crossing_frames": yaw_target_crossing_frames,
			"low_net_motion_gait_intervals": low_motion_intervals,
		},
		"event_timeline": events,
		"low_net_motion_gait_intervals": low_motion_intervals,
		"event_count": events.size(),
		"triangle_change_count": _count_events(events, "blend_triangle_change"),
		"release_arm_change_count": _count_events(events, "release_approach_change"),
		"programme_boundary_count": _count_events(events, "programme_boundary"),
		"animation_state_change_count": _count_events(events, "animation_state_change"),
		"yaw_target_crossing_frames": yaw_target_crossing_frames,
	}


func _empty_peak() -> Dictionary:
	return {"magnitude": 0.0, "signed_value": 0.0, "frame": -1, "stage": "", "state": "", "boundary": ""}


func _update_peak(peak: Dictionary, record: Dictionary, key: String, absolute := false) -> void:
	var signed_value := float(record.get(key, 0.0))
	var magnitude := absf(signed_value) if absolute else signed_value
	if magnitude <= float(peak.magnitude):
		return
	peak.magnitude = magnitude
	peak.signed_value = signed_value
	peak.frame = record.frame
	peak.stage = _stage_name(int(record.programme_stage))
	peak.state = str(record.state)
	peak.boundary = str(record.get("boundary", ""))


func _continuity_event(record: Dictionary, types: Array[String]) -> Dictionary:
	return {
		"frame": record.frame,
		"types": types,
		"stage": _stage_name(int(record.programme_stage)),
		"boundary": record.get("boundary", ""),
		"state": record.state,
		"previous_state": record.get("previous_animation_state", ""),
		"fading_from_state": record.get("fading_from_state", ""),
		"phase": record.phase,
		"blend_point": record.get("blend_point", ""),
		"blend_triangle": record.get("blend_triangle", -1),
		"previous_blend_triangle": record.get("previous_blend_triangle", -1),
		"actor_speed_mps": record.get("actor_planar_speed_mps", 0.0),
		"actor_acceleration_mps2": record.get("actor_planar_acceleration_mps2", 0.0),
		"actor_jerk_mps3": record.get("actor_planar_jerk_mps3", 0.0),
		"yaw_rate_radps": record.get("yaw_rate_radps", 0.0),
		"yaw_acceleration_radps2": record.get("yaw_acceleration_radps2", 0.0),
		"root_jump_m": record.get("root_bone_world_jump_m", 0.0),
		"hips_jump_m": record.get("hips_world_jump_m", 0.0),
		"maximum_foot_jump_m": maxf(float(record.get("left_foot_world_jump_m", 0.0)), float(record.get("right_foot_world_jump_m", 0.0))),
		"terminal_release_approach_active": record.get("terminal_release_approach_active", false),
		"reserved_planar_m": record.get("reserved_planar_m", 0.0),
		"position_error_m": record.get("distance_error", 0.0),
		"signed_yaw_error_rad": record.get("signed_yaw_error_rad", 0.0),
	}


func _count_events(events: Array[Dictionary], event_type: String) -> int:
	var count := 0
	for event in events:
		if event_type in event.types:
			count += 1
	return count


func _weighted_sample_diagnostics(tree: AnimationTree, navigation) -> Array[Dictionary]:
	var root_tree := tree.tree_root as AnimationNodeBlendTree
	var states := root_tree.get_node(&"States") as AnimationNodeStateMachine
	var travel := states.get_node(&"NPCTravel") as AnimationNodeBlendTree
	var space := travel.get_node(&"Sustained") as AnimationNodeBlendSpace2D
	var point: Vector2 = navigation.get("LastBlendPoint")
	var weights := _blend_weights(space, point)
	var shared_phase: float = navigation.get("LastEvaluatedNormalisedPhase")
	var lane_suffix := "r" if navigation.get("PhaseLane") == 1 else "l"
	var result: Array[Dictionary] = []
	for index in weights.size():
		if weights[index] <= 0.0001:
			continue
		var point_name := String(space.get_blend_point_name(index))
		var role := point_name if point_name in ["wf", "wb"] else "%s_%s" % [point_name, lane_suffix]
		var clip: Dictionary = _clips[role]
		var event_id := "release_%s" % lane_suffix
		var event_time := _event_time(clip, event_id)
		var duration := float(clip.duration_seconds)
		var source_rate := float(navigation.get("LastSmoothedPlaybackRate"))
		var local_time := fposmod(event_time + shared_phase * duration, duration)
		var support_local_start := fposmod(event_time + SUPPORT_PHASE_START * duration, duration)
		var support_local_end := fposmod(event_time + SUPPORT_PHASE_END * duration, duration)
		result.append({
			"role": role,
			"weight": weights[index],
			"shared_phase": shared_phase,
			"local_source_time": local_time,
			"source_clip_duration_seconds": duration,
			"effective_playback_rate": source_rate,
			"source_wall_time_ratio": source_rate,
			"predicted_source_root_speed_mps": _source_root_speed(clip, local_time, source_rate),
			"declared_event": event_id,
			"declared_event_time": event_time,
			"support_local_start_time": support_local_start,
			"support_local_end_time": support_local_end,
			"declared_support_foot": "RightFoot" if lane_suffix == "l" else "LeftFoot",
			"seam_source_time_error": absf(local_time - event_time) if shared_phase <= 0.0001 else -1.0,
			"seam_support_foot": "RightFoot" if lane_suffix == "l" else "LeftFoot",
		})
	return result


func _source_root_speed(clip: Dictionary, source_time: float, playback_rate: float) -> float:
	var duration := float(clip.duration_seconds)
	if duration <= 0.000001:
		return 0.0
	var samples: Array = (clip.response as Dictionary).samples
	if samples.is_empty():
		return 0.0
	var best: Dictionary = samples[0]
	var best_error := INF
	for sample in samples:
		var error := absf(float(sample.time_seconds) - source_time)
		if error < best_error:
			best = sample
			best_error = error
	var velocity: Array = best.planar_velocity
	return Vector2(float(velocity[0]), float(velocity[1])).length() * playback_rate


func _blend_weights(space: AnimationNodeBlendSpace2D, point: Vector2) -> Array[float]:
	var weights: Array[float] = []
	weights.resize(space.get_blend_point_count())
	weights.fill(0.0)
	for triangle_index in space.get_triangle_count():
		var ia := space.get_triangle_point(triangle_index, 0)
		var ib := space.get_triangle_point(triangle_index, 1)
		var ic := space.get_triangle_point(triangle_index, 2)
		var bary := _barycentric(point, space.get_blend_point_position(ia), space.get_blend_point_position(ib), space.get_blend_point_position(ic))
		if bary.x >= -0.0001 and bary.y >= -0.0001 and bary.z >= -0.0001:
			weights[ia] = maxf(bary.x, 0.0)
			weights[ib] = maxf(bary.y, 0.0)
			weights[ic] = maxf(bary.z, 0.0)
			return weights
	var closest_distance := INF
	var closest_triangle := Vector3i.ZERO
	var closest_bary := Vector3(1.0, 0.0, 0.0)
	for triangle_index in space.get_triangle_count():
		var ia := space.get_triangle_point(triangle_index, 0)
		var ib := space.get_triangle_point(triangle_index, 1)
		var ic := space.get_triangle_point(triangle_index, 2)
		var a := space.get_blend_point_position(ia)
		var b := space.get_blend_point_position(ib)
		var c := space.get_blend_point_position(ic)
		for edge: Array in [[a, b], [b, c], [c, a]]:
			var projected := Geometry2D.get_closest_point_to_segment(point, edge[0], edge[1])
			var distance := point.distance_squared_to(projected)
			if distance < closest_distance:
				closest_distance = distance
				closest_triangle = Vector3i(ia, ib, ic)
				closest_bary = _barycentric(projected, a, b, c)
	weights[closest_triangle.x] = maxf(closest_bary.x, 0.0)
	weights[closest_triangle.y] = maxf(closest_bary.y, 0.0)
	weights[closest_triangle.z] = maxf(closest_bary.z, 0.0)
	return weights


func _barycentric(point: Vector2, a: Vector2, b: Vector2, c: Vector2) -> Vector3:
	var denominator := (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y)
	if is_zero_approx(denominator):
		return Vector3(-1.0, -1.0, -1.0)
	var wa := ((b.y - c.y) * (point.x - c.x) + (c.x - b.x) * (point.y - c.y)) / denominator
	var wb := ((c.y - a.y) * (point.x - c.x) + (a.x - c.x) * (point.y - c.y)) / denominator
	return Vector3(wa, wb, 1.0 - wa - wb)


func _event_time(clip: Dictionary, event_id: String) -> float:
	for event: Dictionary in clip.phase_events.events:
		if event.id == event_id:
			return float(event.time_seconds)
	return NAN


func _load_clips() -> Dictionary:
	var file := FileAccess.open(CATALOGUE, FileAccess.READ)
	var document: Dictionary = JSON.parse_string(file.get_as_text())
	var clips := {}
	for clip: Dictionary in document.clips:
		clips[clip.role] = clip
	return clips


func _mean(values: Array[float]) -> float:
	if values.is_empty():
		return 0.0
	var total := 0.0
	for value in values:
		total += value
	return total / values.size()


func _percentile(values: Array[float], percentile: float) -> float:
	if values.is_empty():
		return 0.0
	var sorted := values.duplicate()
	sorted.sort()
	var index := clampi(ceili(percentile * sorted.size()) - 1, 0, sorted.size() - 1)
	return sorted[index]


func _reversal_frame_record(
	scenario_name: String,
	frame: int,
	destination: Transform3D,
	npc: CharacterBody3D,
	navigation,
	realised_longitudinal_speed: float,
	previous_transform: Transform3D,
	previous_position_error: float
) -> Dictionary:
	var actor_yaw := atan2(npc.global_basis.z.x, npc.global_basis.z.z)
	var previous_yaw := atan2(previous_transform.basis.z.x, previous_transform.basis.z.z)
	var world_displacement: Vector3 = navigation.get("LastRealisedDisplacement")
	var actor_local_displacement := previous_transform.basis.orthonormalized().inverse() * world_displacement
	var destination_local_displacement := destination.basis.orthonormalized().inverse() * world_displacement
	var route_tangent: Vector3 = navigation.get("LastRouteTangent")
	var current_position_error := npc.global_position.distance_to(destination.origin)
	return {
		"scenario": scenario_name,
		"frame": frame,
		"programme_stage": navigation.get("ProgrammeStage"),
		"state": str(navigation.get("LastEvaluatedState")),
		"phase": navigation.get("LastEvaluatedNormalisedPhase"),
		"actor_transform": str(npc.global_transform),
		"route_tangent": str(route_tangent),
		"actor_world_displacement": str(world_displacement),
		"actor_local_displacement": str(actor_local_displacement),
		"destination_frame_displacement": str(destination_local_displacement),
		"destination_frame_longitudinal_displacement": -destination_local_displacement.z,
		"route_longitudinal_displacement": world_displacement.dot(route_tangent),
		"destination_progress_displacement": previous_position_error - current_position_error,
		"actor_yaw_delta": wrapf(actor_yaw - previous_yaw, -PI, PI),
		"authored_root_world_delta": str(navigation.get("LastAuthoredRootWorldDelta")),
		"requested_synthetic_world_delta": str(navigation.get("LastRequestedSyntheticWorldDelta")),
		"authored_root_longitudinal_delta": navigation.get("LastAuthoredRootLongitudinal"),
		"requested_synthetic_longitudinal_delta": navigation.get("LastRequestedSyntheticLongitudinal"),
		"realised_longitudinal_delta": navigation.get("LastRealisedLongitudinal"),
		"realised_longitudinal_speed": realised_longitudinal_speed,
		"total_speed": npc.velocity.length(),
		"distance_error": current_position_error,
		"yaw_error": absf(wrapf(destination.basis.get_euler().y - actor_yaw, -PI, PI)),
		"terminal_committed": navigation.get("TerminalCommitted"),
		"budget_scope": navigation.get("CorrectionScope"),
	}


func _capture_candidate_frame(
	frame: int,
	runtime: Node3D,
	camera: Camera3D,
	npc: CharacterBody3D,
	navigation,
	frame_record: Dictionary
) -> void:
	paused = true
	_update_actor_forward_marker(runtime, npc)
	if frame == SUPPORT_CAPTURE_START:
		var evidence: Dictionary = frame_record["support_evidence"]
		if bool(evidence.get("authoritative_contact_evidence_available", false)):
			var support_foot := str(evidence.get("declared_support_foot", ""))
			var support_position_key := "left_world_position" if support_foot == "LeftFoot" else "right_world_position" if support_foot == "RightFoot" else ""
			if support_position_key.is_empty():
				push_error("Authoritative contact evidence did not identify a support foot.")
			else:
				_update_support_anchor(runtime, evidence[support_position_key])
		else:
			frame_record["support_anchor_status"] = "unavailable_stage1: no ANIM-002 or IK-003 authoritative support foot to anchor."
	var files: Array[String] = []
	for view in CANDIDATE_VIEWS:
		_configure_candidate_camera(view, camera, npc, navigation.get("LastRouteTangent"))
		await process_frame
		RenderingServer.force_draw(true, 0.0)
		var filename := "behind_candidate_%03d_%s.png" % [frame, view]
		var path := "%s/%s" % [_run_dir, filename]
		if camera.get_viewport().get_texture().get_image().save_png(path) != OK:
			push_error("Failed to save candidate screenshot: %s" % path)
		else:
			files.append(filename)
			(_manifest["captures"] as Array).append({
				"run_id": _run_id,
				"scenario": "behind",
				"physics_frame": frame,
				"stage": "candidate_replay",
				"view": view,
				"file": filename,
				"modified_unix": FileAccess.get_modified_time(path),
				"sha256": FileAccess.get_sha256(path),
			})
	frame_record["capture_files"] = files
	(_manifest["candidate_replay"]["frames"] as Array).append(frame_record.duplicate(true))
	paused = false


func _is_contact_lock_capture_snapshot(snapshot: Dictionary, checkpoint: String) -> bool:
	if not bool(snapshot.get("available", false)) or str(snapshot.get("role", "")) != "f2i_l" \
		or not bool(snapshot.get("native_observed", false)):
		return false
	var phase := float(snapshot.get("phase", -1.0))
	var lifecycle := str(snapshot.get("lifecycle", ""))
	var raw := snapshot.get("raw_source", Vector3.ZERO) as Vector3
	var anchor := snapshot.get("anchor", Vector3.ZERO) as Vector3
	var native_target := snapshot.get("native_target", Vector3.ZERO) as Vector3
	var native_end_foot := snapshot.get("native_end_foot", Vector3.ZERO) as Vector3
	match checkpoint:
		"acquire_support":
			return bool(snapshot.get("has_anchor", false)) \
				and lifecycle in ["Acquire", "Support"] \
				and phase >= 0.0 and phase <= 0.05 \
				and native_target.distance_to(anchor) <= CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M \
				and native_end_foot.distance_to(anchor) <= CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M
		"late_support":
			return bool(snapshot.get("has_anchor", false)) \
				and lifecycle == "Support" \
				and phase >= CONTACT_LOCK_LATE_SUPPORT_PHASE and phase < 0.5 \
				and raw.distance_to(anchor) >= CONTACT_LOCK_RAW_ANCHOR_SEPARATION_M \
				and native_target.distance_to(anchor) <= CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M \
				and native_end_foot.distance_to(anchor) <= CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M
		"release_fallback":
			return not bool(snapshot.get("has_anchor", true)) \
				and lifecycle == "Release" \
				and is_equal_approx(phase, 0.5) \
				and str(snapshot.get("selected_resolution", "")) == "fallback" \
				and str(snapshot.get("provider_selection", "")) == "animation_synchronised_fallback" \
				and native_target.distance_to(raw) <= CONTACT_LOCK_NATIVE_ANCHOR_TOLERANCE_M
	return false


func _capture_contact_lock_snapshot(
	checkpoint: String,
	frame: int,
	camera: Camera3D,
	npc: CharacterBody3D,
	skeleton: Skeleton3D,
	contact_lock_diagnostics: Node,
	snapshot: Dictionary
) -> void:
	paused = true
	var left_foot_index := skeleton.find_bone("LeftFoot")
	var hips_index := skeleton.find_bone("Hips")
	if left_foot_index < 0 or hips_index < 0:
		push_error("Contact-lock diagnostic capture requires LeftFoot and Hips bones.")
		paused = false
		return
	var left_foot_world := skeleton.to_global(skeleton.get_bone_global_pose(left_foot_index).origin)
	var hips_world := skeleton.to_global(skeleton.get_bone_global_pose(hips_index).origin)
	_configure_contact_lock_lower_body_camera(camera, npc, left_foot_world)
	await process_frame
	if not _assert_contact_lock_visual_layout(checkpoint, camera, left_foot_world, contact_lock_diagnostics, snapshot):
		paused = false
		return
	RenderingServer.force_draw(true, 0.0)
	await RenderingServer.frame_post_draw
	var filename := "user_reported_f2i_l_%s_lower_body.png" % checkpoint
	var path := "%s/%s" % [_run_dir, filename]
	if camera.get_viewport().get_texture().get_image().save_png(path) != OK:
		push_error("Failed to save contact-lock diagnostic screenshot: %s" % path)
	else:
		(_manifest["captures"] as Array).append({
			"run_id": _run_id,
			"scenario": "user_reported",
			"stage": "f2i_l_%s" % checkpoint,
			"view": "contact_lock_lower_body",
			"physics_frame": frame,
			"file": filename,
			"sha256": FileAccess.get_sha256(path),
			"trace": snapshot.duplicate(true),
			"left_foot_lateral_offset_from_hips_m": (left_foot_world - hips_world).dot(npc.global_basis.x.normalized()),
			"camera_avatar_left_dot_m": (camera.global_position - hips_world).dot(-npc.global_basis.x.normalized()),
		})
	paused = false


func _assert_contact_lock_visual_layout(
	checkpoint: String,
	camera: Camera3D,
	left_foot_world: Vector3,
	diagnostics: Node,
	snapshot: Dictionary
) -> bool:
	var layout := diagnostics.GetVisualLayout() as Dictionary
	var raw_anchor_clearance := float(layout.get("raw_anchor_clearance_m", 0.0))
	var anchor_target_clearance := float(layout.get("anchor_native_target_clearance_m", 0.0))
	var target_end_clearance := float(layout.get("native_target_end_foot_clearance_m", 0.0))
	var marker_foot_clearance := float(layout.get("minimum_marker_foot_clearance_m", 0.0))
	var panel_separation := float(layout.get("panel_separation_px", 0.0))
	if raw_anchor_clearance < 0.08 or anchor_target_clearance < 0.08 or target_end_clearance < 0.06 \
		or marker_foot_clearance < 0.04 or panel_separation < 10.0:
		return _report_contact_lock_layout_failure(checkpoint, "3D marker or screen-panel clearance fell below the focused diagnostic layout contract")

	var lifecycle_panel: Rect2 = layout.get("lifecycle_panel_rect", Rect2())
	var marker_legend_panel: Rect2 = layout.get("marker_legend_panel_rect", Rect2())
	var foot_safe_zone := Rect2(camera.unproject_position(left_foot_world) - Vector2(48.0, 48.0), Vector2(96.0, 96.0))
	if lifecycle_panel.intersects(marker_legend_panel) or lifecycle_panel.intersects(foot_safe_zone) \
		or marker_legend_panel.intersects(foot_safe_zone):
		return _report_contact_lock_layout_failure(checkpoint, "screen-space lifecycle/legend panels overlap each other or the focused foot safe zone")

	var marker_points: Array[Vector2] = [
		camera.unproject_position((snapshot.raw_source as Vector3) + Vector3.UP * float(layout["raw_marker_elevation_m"])),
		camera.unproject_position((snapshot.anchor as Vector3) + Vector3.UP * float(layout["anchor_marker_elevation_m"])),
		camera.unproject_position((snapshot.native_target as Vector3) + Vector3.UP * float(layout["native_target_marker_elevation_m"])),
		camera.unproject_position((snapshot.native_end_foot as Vector3) + Vector3.UP * float(layout["native_end_foot_marker_elevation_m"])),
	]
	for point in marker_points:
		if lifecycle_panel.grow(12.0).has_point(point) or marker_legend_panel.grow(12.0).has_point(point):
			return _report_contact_lock_layout_failure(checkpoint, "a geometric diagnostic marker entered a screen-space panel exclusion zone")
	for first in marker_points.size():
		for second in range(first + 1, marker_points.size()):
			if marker_points[first].distance_to(marker_points[second]) < 24.0:
				return _report_contact_lock_layout_failure(checkpoint, "projected geometric diagnostic markers are too close to distinguish")

	var foot_lock_status := str(snapshot.get("foot_lock_status", ""))
	var correction_status := str(snapshot.get("root_correction_status", ""))
	if checkpoint == "release_fallback":
		if foot_lock_status != "cleared; fallback" or not correction_status.begins_with("none"):
			return _report_contact_lock_layout_failure(checkpoint, "release must state a cleared foot lock and no root correction")
	elif foot_lock_status != "active" or not (correction_status.begins_with("none") or correction_status == "authorised"):
		return _report_contact_lock_layout_failure(checkpoint, "active foot lock and root-correction status must remain independently stated")
	return true


func _report_contact_lock_layout_failure(checkpoint: String, reason: String) -> bool:
	push_error("f2i_l %s diagnostic layout failure: %s" % [checkpoint, reason])
	(_manifest["assertion_failures"] as Array).append({
		"scenario": "user_reported",
		"contract": "contact_lock_visual_layout",
		"checkpoint": checkpoint,
		"reason": reason,
	})
	return false


func _finalise_capture_provenance() -> void:
	var output_files: Array[Dictionary] = []
	for capture: Dictionary in _manifest["captures"]:
		var filename := str(capture.get("file", ""))
		if filename.is_empty():
			continue
		var path := "%s/%s" % [_run_dir, filename]
		output_files.append({
			"path": path,
			"global_path": ProjectSettings.globalize_path(path),
			"sha256": str(capture.get("sha256", FileAccess.get_sha256(path))),
		})
	var capture_provenance: Dictionary = _manifest.get("capture_provenance", {})
	capture_provenance["completed_utc"] = Time.get_datetime_string_from_system(true) + "Z"
	capture_provenance["output_files"] = output_files
	_manifest["capture_provenance"] = capture_provenance


func _capture_provenance() -> Dictionary:
	var reported_invocation := _user_argument_value("--capture-invocation=")
	var focused_command := _user_argument_value("--focused-csharp-command=")
	return {
		"run_id": _run_id,
		"started_utc": Time.get_datetime_string_from_system(true) + "Z",
		"windowed": DisplayServer.get_name().to_lower() != "headless",
		"display_server": DisplayServer.get_name(),
		"xr_mode": "off",
		"reported_outer_invocation": reported_invocation if not reported_invocation.is_empty() else "not_provided",
		"runner_engine_arguments": OS.get_cmdline_args(),
		"runner_user_arguments": OS.get_cmdline_user_args(),
		"focused_csharp_integration": {
			"test_method": "AlleyCat.IntegrationTests.IK.ContactLockCoordinatorIntegrationTests.F2ILDiagnosticTrace_AcquireLateSupportAndReleaseExposeTheExpectedVisualChannels",
			"command": focused_command if not focused_command.is_empty() else "not_provided",
			"result": "not_run",
			"result_artifact": "%s/focused_integration_result.json" % _run_dir,
		},
	}


func _user_argument_value(prefix: String) -> String:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(prefix):
			return argument.trim_prefix(prefix)
	return ""


func _configure_contact_lock_lower_body_camera(camera: Camera3D, npc: Node3D, left_foot_world: Vector3) -> void:
	var avatar_forward := -npc.global_basis.z.normalized()
	var avatar_left := -npc.global_basis.x.normalized()
	camera.global_position = left_foot_world + avatar_left * 1.55 - avatar_forward * 1.15 + Vector3.UP * 0.72
	camera.look_at(left_foot_world + Vector3.UP * 0.19, Vector3.UP)


func _configure_candidate_camera(view: String, camera: Camera3D, npc: Node3D, route_tangent: Vector3) -> void:
	var route_direction := route_tangent.normalized()
	if route_direction.is_zero_approx():
		route_direction = Vector3.FORWARD
	var route_side := Vector3.UP.cross(route_direction).normalized()
	var target := npc.global_position + Vector3.UP * 0.8
	match view:
		"candidate_side":
			camera.global_position = target + route_side * 3.2 + Vector3.UP * 0.35
			camera.look_at(target, Vector3.UP)
		"candidate_top_route":
			camera.global_position = target + route_side * 1.2 - route_direction * 0.8 + Vector3.UP * 2.4
			camera.look_at(npc.global_position + Vector3.UP * 0.18, Vector3.UP)
		"candidate_lower_body":
			camera.global_position = npc.global_position + route_side * 2.1 - route_direction * 1.4 + Vector3.UP * 0.75
			camera.look_at(npc.global_position + Vector3.UP * 0.65, Vector3.UP)


func _update_actor_forward_marker(runtime: Node3D, npc: Node3D) -> void:
	var markers := runtime.get_node("NAV001EvidenceMarkers") as Node3D
	var marker := markers.get_node_or_null("ActorForward") as MeshInstance3D
	if marker == null:
		_add_marker(markers, "ActorForward", Color(0.0, 1.0, 1.0), false)
		marker = markers.get_node("ActorForward") as MeshInstance3D
	var actor_forward := npc.global_basis.orthonormalized() * Vector3.FORWARD
	marker.global_position = npc.global_position + actor_forward * 0.4 + Vector3.UP * 0.12
	marker.look_at(marker.global_position + actor_forward, Vector3.UP)
	(marker.mesh as BoxMesh).size = Vector3(0.06, 0.06, 0.8)


func _update_support_anchor(runtime: Node3D, world_position: Vector3) -> void:
	var markers := runtime.get_node("NAV001EvidenceMarkers") as Node3D
	var marker := markers.get_node_or_null("SupportAnchor") as MeshInstance3D
	if marker == null:
		_add_marker(markers, "SupportAnchor", Color(1.0, 0.0, 1.0), true)
		marker = markers.get_node("SupportAnchor") as MeshInstance3D
	var mesh := marker.mesh as SphereMesh
	mesh.radius = 0.035
	mesh.height = 0.07
	marker.global_position = world_position + Vector3.UP * 0.01


func _configure_camera(view: String, camera: Camera3D, npc: Node3D, destination: Vector3) -> void:
	var route_direction := Vector3(destination.x, 0.0, destination.z).normalized()
	if route_direction.is_zero_approx():
		route_direction = Vector3.FORWARD
	var midpoint := destination * 0.5
	match view:
		"three_quarter":
			camera.global_position = midpoint + Vector3(4.2, 2.8, 4.2)
			camera.look_at(midpoint + Vector3.UP * 0.8, Vector3.UP)
		"side":
			camera.global_position = midpoint + Vector3(4.8, 1.8, 0.0)
			camera.look_at(midpoint + Vector3.UP * 0.75, Vector3.UP)
		"lower_body":
			camera.global_position = npc.global_position + Vector3(2.3, 0.75, 2.3)
			camera.look_at(npc.global_position + Vector3.UP * 0.65, Vector3.UP)
		"tracking":
			var side := route_direction.cross(Vector3.UP).normalized()
			var target := npc.global_position + Vector3.UP * 0.9
			camera.global_position = target - route_direction * 3.4 + side * 2.1 + Vector3.UP * 0.55
			camera.look_at(target, Vector3.UP)


func _configure_route_markers(runtime: Node3D, destination: Vector3) -> void:
	var markers := runtime.get_node_or_null("NAV001EvidenceMarkers") as Node3D
	if markers == null:
		markers = Node3D.new()
		markers.name = "NAV001EvidenceMarkers"
		runtime.add_child(markers)
		_add_marker(markers, "Origin", Color(0.1, 0.9, 0.2), true)
		_add_marker(markers, "Destination", Color(0.1, 0.35, 1.0), true)
		_add_marker(markers, "Direction", Color(1.0, 0.85, 0.05), false)
	markers.get_node("Origin").global_position = Vector3.ZERO + Vector3.UP * 0.08
	markers.get_node("Destination").global_position = destination + Vector3.UP * 0.08
	var direction_marker := markers.get_node("Direction") as MeshInstance3D
	var length := destination.length()
	direction_marker.global_position = destination * 0.5 + Vector3.UP * 0.05
	direction_marker.look_at(destination, Vector3.UP)
	(direction_marker.mesh as BoxMesh).size = Vector3(0.08, 0.04, maxf(length, 0.1))


func _add_marker(parent: Node3D, marker_name: String, colour: Color, sphere: bool) -> void:
	var marker := MeshInstance3D.new()
	marker.name = marker_name
	var material := StandardMaterial3D.new()
	material.albedo_color = colour
	material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	if sphere:
		var mesh := SphereMesh.new()
		mesh.radius = 0.1
		mesh.height = 0.2
		marker.mesh = mesh
	else:
		marker.mesh = BoxMesh.new()
	marker.material_override = material
	parent.add_child(marker)


func _circular_phase_span(phases: Array[float]) -> float:
	if phases.size() < 2:
		return 0.0
	var unwrapped: float = phases[0]
	var minimum := unwrapped
	var maximum := unwrapped
	var previous: float = phases[0]
	for index in range(1, phases.size()):
		var difference := wrapf(phases[index] - previous, -0.5, 0.5)
		unwrapped += difference
		minimum = minf(minimum, unwrapped)
		maximum = maxf(maximum, unwrapped)
		previous = phases[index]
	return maximum - minimum
