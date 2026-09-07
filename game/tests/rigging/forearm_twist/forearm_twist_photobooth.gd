# RIG-002 rest-pose wrist reproduction runner. Run windowed (never headless):
#
# xvfb-run -a godot-mono -d -s --xr-mode off --path game \
#   "tests/rigging/forearm_twist/forearm_twist_photobooth.gd" -- \
#   --run-id my-unique-run --run-mode create --weight 0 --scenario rest_hand_neutral
# xvfb-run -a godot-mono -d -s --xr-mode off --path game \
#   "tests/rigging/forearm_twist/forearm_twist_photobooth.gd" -- \
#   --run-id my-unique-run --run-mode join --weight 0.25 --scenario rest_hand_neutral
# Never delete or overwrite prior evidence.
# Capture weight zero first per scenario. --list-coverage checks the declaration
# without screenshots. --selected-weight defaults to 0.25.
#
# Scenarios, selected with --scenario (omitting it keeps the legacy wrist-roll behaviour):
# - rest_hand_roll_180: 180-degree rest-relative hand roll (top-down evidence only).
# - posed_arm_twist_trajectory: matched-weight, forward-posed upper/lower arm and
#   sampled neutral-to-180-to-neutral signed roll with hold and settling; top-down.
# - posed_arm_bend_trajectory: matched-weight, same upstream arm and top-down camera as the twist diagnostic;
#   neutral palm faces image-up, then five ordered static anatomical bend samples.
# - rest_hand_flexion_60 / rest_hand_extension_60: rest-relative palm-forward pronation about the
#   forearm longitudinal axis (hand origin fixed, nearest <=180-degree rotation bringing the verified
#   palm normal onto the subject's forward direction) followed by a +/-60-degree anatomical wrist bend
#   about the axis perpendicular to both the forearm and the palm-forward target, so palmar flexion
#   rotates the fingertips toward the palm face and dorsal extension away from it
#   (top-down plus supplemental palm-side profile evidence). The pronation is a real axial twist, so
#   each bend capture also enforces pixel evidence: non-blank frames, a required same-run zero
#   counterpart, and a non-trivial weight_000 vs weight_025 pixel diff.
extends SceneTree

const TEST_SCENE_PATH := "res://tests/rigging/forearm_twist/forearm_twist_photobooth.tscn"
const RUNS_DIR := "temp/RIG-002/runs"
const IDENTITY_FILE := "run_identity.txt"
const SCENARIO_REST_HAND_ROLL_180 := "rest_hand_roll_180"
const SCENARIO_REST_HAND_FLEXION_60 := "rest_hand_flexion_60"
const SCENARIO_REST_HAND_EXTENSION_60 := "rest_hand_extension_60"
const SCENARIO_POSED_ARM_TWIST_TRAJECTORY := "posed_arm_twist_trajectory"
const SCENARIO_POSED_ARM_BEND_TRAJECTORY := "posed_arm_bend_trajectory"
const SUPPORTED_SCENARIOS := [
	SCENARIO_REST_HAND_ROLL_180,
	SCENARIO_REST_HAND_FLEXION_60,
	SCENARIO_REST_HAND_EXTENSION_60,
	SCENARIO_POSED_ARM_TWIST_TRAJECTORY,
	SCENARIO_POSED_ARM_BEND_TRAJECTORY,
	"rest_hand_neutral",
	"rest_hand_roll_plus_45", "rest_hand_roll_minus_45",
	"rest_hand_roll_plus_90", "rest_hand_roll_minus_90",
	"rest_hand_roll_plus_135", "rest_hand_roll_minus_135",
	"rest_hand_roll_plus_180", "rest_hand_roll_minus_180",
	"rest_hand_roll_plus_90_flexion_60", "rest_hand_roll_minus_90_flexion_60",
	"rest_hand_roll_plus_90_extension_60", "rest_hand_roll_minus_90_extension_60",
]
const ROLL_RADIANS := PI
const BEND_ANGLE_DEGREES := 60.0
const CAMERA_IMAGE_SIZE := Vector2(768.0, 768.0)
const CAMERA_MINIMUM_ORTHOGONAL_SCALE := 0.34
const CAMERA_FRAMING_PADDING := 1.20
const FINGERTIP_EXTENSION_METRES := 0.055
const REST_TOLERANCE_METRES := 0.0001
const REST_TOLERANCE_RADIANS := 0.001
const HELPER_RESPONSE_TOLERANCE_RADIANS := 0.01
const AXIAL_ALIGNMENT_TOLERANCE := 0.999
const SUBJECT_FORWARD_MAXIMUM_UP_TILT := 0.15
const SUBJECT_FORWARD_WORLD_ANCHOR_MINIMUM_DOT := 0.99
const PALM_TARGET_MINIMUM_LENGTH := 0.00001
const PALM_GEOMETRY_MINIMUM_ALIGNMENT := 0.8
const PALM_FORWARD_MINIMUM_DOT := 0.999
const PRONATION_RECOVERY_TOLERANCE_RADIANS := 0.002
const BEND_COMPONENT_TOLERANCE_RADIANS := 0.002
const BEND_SIGN_CONVENTION_MINIMUM_ALIGNMENT_DELTA := 0.25
const TWIST_CANONICALISATION_TOLERANCE := 0.00001
const PIXEL_SAMPLE_STRIDE := 2
const PIXEL_DIFF_CHANNEL_SENSITIVITY := 10
# The helper twist only reshapes the forearm skin strip inside the wide wrist-centred frame, so real
# engagement lands around half a percent of sampled pixels; a pixel-identical render measures exactly
# zero. 0.1% separates the two by an order of magnitude.
const PIXEL_DIFF_MINIMUM_FRACTION := 0.001
const NON_BLANK_MINIMUM_CHANNEL_RANGE := 16
const COMPANION_WEIGHT_LABEL := "000"

var _photobooth: Photobooth
var _twist_weight := NAN
var _scenario_name := SCENARIO_REST_HAND_ROLL_180
var _selected_weight := 0.25
var _capture_camera: Camera3D
var _profile_camera: Camera3D
var _pixel_contrast_failures := 0
var _output_dir := ""
var _run_id := ""
var _weight_label := ""


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	if "--check-twist-guard-failure" in OS.get_cmdline_user_args():
		# Synthetic, isolated negative control: no scene, artefact or run identity is touched.
		var blank := Image.create(8, 8, false, Image.FORMAT_RGB8)
		if _assert_twist_diagnostic_nonblank(blank, "synthetic_blank"):
			SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: blank-image negative control was not rejected")
		return
	if "--check-output-labels" in OS.get_cmdline_user_args():
		for pair in [["0", "000"], ["0.0", "000"], ["0.25", "025"], ["0.2500", "025"], ["0.2501", "02501"], ["0.5", "050"], ["1", "1000"]]:
			if _weight_label_for(pair[0]) != pair[1]:
				SceneUtils.fatal_error_and_quit("RIG-002 runner: output label check failed: %s" % pair)
				return
		print("RIG002_OUTPUT_LABELS_PASS cases=7")
		quit(0)
		return
	if not _validate_coverage_declaration():
		return
	if "--list-coverage" in OS.get_cmdline_user_args():
		_selected_weight = _requested_selected_weight()
		if is_nan(_selected_weight):
			return
		_print_coverage()
		quit(0)
		return
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("RIG-002 runner must not run headless because screenshots would be invalid")
		return
	if _has_output_dir_argument(OS.get_cmdline_args()) or _has_output_dir_argument(OS.get_cmdline_user_args()):
		SceneUtils.fatal_error_and_quit("RIG-002 runner: omit --output-dir; capture is confined to game/temp")
		return
	_select_scenario()
	if _scenario_name not in SUPPORTED_SCENARIOS:
		return
	_twist_weight = _required_candidate_weight()
	if is_nan(_twist_weight):
		return
	_selected_weight = _requested_selected_weight()
	if is_nan(_selected_weight):
		return
	if _twist_weight not in [0.0, 0.25, _selected_weight]:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: weight must be zero, selected weight, or matched 0.25")
		return
	_weight_label = _weight_label_for(_weight_argument())
	if _weight_label.is_empty() or not _prepare_run():
		return
	if not _require_zero_companions():
		return
	if not _assert_capture_paths_available():
		return

	_photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: failed to instantiate photobooth")
		return
	root.add_child(_photobooth)
	await SceneUtils.wait_frames(self, 3)
	_validate_scene_setup()
	if _scenario_name == SCENARIO_POSED_ARM_TWIST_TRAJECTORY:
		await _run_posed_arm_twist_trajectory()
		return
	if _scenario_name == SCENARIO_POSED_ARM_BEND_TRAJECTORY:
		await _run_posed_arm_bend_trajectory()
		return

	for mesh_name: String in ["Female", "Male"]:
		_set_subject_visible(mesh_name)
		var character := _photobooth.get_node("Subject/%s" % mesh_name) as Node3D
		var skeleton := _skeleton(mesh_name)
		if character == null or skeleton == null:
			return
		await _ensure_hand_authority_stage(character, skeleton)
		_disable_all_ik_nodes(character, skeleton)
		_disable_pose_animation_nodes(character)
		_reset_to_actual_rest_pose(skeleton)
		_assert_rest_t_pose(mesh_name, skeleton)
		for side_name: String in ["Left", "Right"]:
			if _is_extended_scenario():
				_apply_extended_pose(skeleton, side_name)
			elif _is_bend_scenario():
				_apply_rest_relative_hand_bend(skeleton, side_name)
			else:
				_apply_rest_relative_hand_roll(skeleton, side_name)
			_pulse_hand_authority_stage(skeleton)
			if _is_extended_scenario():
				_assert_extended_pose(mesh_name, skeleton, side_name, false)
			elif _is_bend_scenario():
				_assert_no_ik_and_bend_pose(mesh_name, skeleton, side_name)
			else:
				_assert_no_ik_and_roll_pose(mesh_name, skeleton, side_name)
			await SceneUtils.wait_frames(self, 3)
			if _is_extended_scenario():
				_assert_extended_pose(mesh_name, skeleton, side_name, true)
			elif _is_bend_scenario():
				_assert_settled_rest_hand_bend(mesh_name, skeleton, side_name)
			else:
				_assert_settled_rest_hand_roll(mesh_name, skeleton, side_name)
			# Resolve the camera only after the final canonical pose has settled. This deliberately avoids a shared
			# world-space target: each sex/side frame comes from its current wrist, hand, fingers, and distal forearm.
			_focus_top_down_camera(skeleton, side_name)
			_assert_top_camera_framing(skeleton, side_name)
			_capture_camera.make_current()
			if root.get_camera_3d() != _capture_camera:
				SceneUtils.fatal_error_and_quit("RIG-002 runner: root viewport did not select the top-down capture camera")
				return
			await SceneUtils.capture_screenshot(self, _output_dir.trim_prefix("temp/").path_join(_capture_path(mesh_name, side_name, "top_down")))
			if _is_bend_scenario() or _is_extended_scenario():
				_assert_bend_pixel_evidence(mesh_name, side_name, "top_down")
			if _is_bend_scenario() or _is_combined_scenario():
				# Supplemental profile evidence; the baseline top-down capture above stays first and unchanged.
				_focus_profile_camera(skeleton, side_name)
				_assert_profile_camera_framing(skeleton, side_name)
				_profile_camera.make_current()
				if root.get_camera_3d() != _profile_camera:
					SceneUtils.fatal_error_and_quit("RIG-002 runner: root viewport did not select the profile capture camera")
					return
				await SceneUtils.capture_screenshot(self, _output_dir.trim_prefix("temp/").path_join(_capture_path(mesh_name, side_name, "profile")))
				_assert_bend_pixel_evidence(mesh_name, side_name, "profile")
			_reset_to_actual_rest_pose(skeleton)

	print("RIG002_%s_COMPLETE weight=%.6f artefact_dir=res://%s/%s" % [
		_scenario_name.to_upper(),
		_twist_weight,
		_output_dir,
		_scenario_name,
	])
	print("RIG002_PIXEL_CONTRAST_SUMMARY scenario=%s weight=%.2f failures=%d minimum_fraction=%.6f" % [
		_scenario_name, _twist_weight, _pixel_contrast_failures, PIXEL_DIFF_MINIMUM_FRACTION])
	quit(1 if _pixel_contrast_failures > 0 else 0)


func _validate_scene_setup() -> void:
	var top_camera := _photobooth.get_camera_rig("TopCamera") as CameraRig
	if top_camera == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing TopCamera")
		return
	top_camera.image_size = CAMERA_IMAGE_SIZE
	top_camera.viewport.own_world_3d = false
	top_camera.viewport.world_3d = _photobooth.get_world_3d()
	top_camera.camera.make_current()
	_capture_camera = Camera3D.new()
	_capture_camera.name = "RestHandRollTopCamera"
	_capture_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_capture_camera.size = CAMERA_MINIMUM_ORTHOGONAL_SCALE
	_capture_camera.near = 0.01
	_capture_camera.far = 10.0
	_photobooth.add_child(_capture_camera)
	# Supplemental bend-scenario profile camera. Constructed in code so the authored scene stays untouched; it is
	# oriented dynamically per subject/side in _focus_profile_camera.
	_profile_camera = Camera3D.new()
	_profile_camera.name = "RestHandBendProfileCamera"
	_profile_camera.projection = Camera3D.PROJECTION_ORTHOGONAL
	_profile_camera.size = CAMERA_MINIMUM_ORTHOGONAL_SCALE
	_profile_camera.near = 0.01
	_profile_camera.far = 10.0
	_photobooth.add_child(_profile_camera)
	for marker_name: String in ["ForearmStart", "Wrist", "Hand"]:
		var marker: Node3D = _photobooth.get_marker(marker_name)
		if marker == null:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: missing marker %s" % marker_name)
			return
		# Markers are intentionally hidden: a surface marker would invalidate wrist-skin evidence.
		marker.visible = false
	for camera_name: String in ["FrontCamera", "BackCamera", "RightCamera", "LeftCamera", "WristCloseCamera", "WristReverseCloseCamera", "WristProfileCamera"]:
		_photobooth.get_camera_rig(camera_name).visible = false


func _ensure_hand_authority_stage(character: Node3D, skeleton: Skeleton3D) -> void:
	# The IK-005 hand-authority adapter is no longer a scene node: CharacterIK inserts it under the
	# skeleton at runtime, immediately before the ForearmTwistModifier. The photobooth template ships
	# without a CharacterIK, so this runner installs a minimal one bound to the template's convention
	# targets (Viewpoint marker, IKTargets subtree, DynamicPhysicalRig) and lets it initialise. Without
	# the adapter the twist writer fails closed and rests its helpers, erasing the weight contrast this
	# runner must capture. This mirrors the sanctioned CharacterIK fixture used by the authority
	# integration tests; the node is disabled by _disable_all_ik_nodes right after installation.
	if skeleton.get_node_or_null("CharacterIKHandAuthorityStage") != null:
		return
	var mesh_name := character.name
	var viewpoint := character.get_node_or_null("%s/GeneralSkeleton/Head/Viewpoint" % mesh_name) as Marker3D
	var physical_rig := skeleton.get_node_or_null("DynamicPhysicalRig")
	var head_target := character.get_node_or_null("IKTargets/Head")
	var head_solve_target := character.get_node_or_null("IKTargets/HeadSolve")
	var right_hand_target := character.get_node_or_null("IKTargets/RightHand")
	var left_hand_target := character.get_node_or_null("IKTargets/LeftHand")
	if viewpoint == null or physical_rig == null or head_target == null or head_solve_target == null \
			or right_hand_target == null or left_hand_target == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s lacks the CharacterIK convention bindings (viewpoint=%s physical_rig=%s head=%s head_solve=%s right_hand=%s left_hand=%s)" % [
			mesh_name,
			viewpoint,
			physical_rig,
			head_target,
			head_solve_target,
			right_hand_target,
			left_hand_target,
		])
		return
	var character_ik := CharacterIK.new()
	character_ik.name = "CharacterIK"
	character_ik.set("Viewpoint", viewpoint)
	character_ik.set("HeadIKTarget", head_target)
	character_ik.set("HeadIKSolveTarget", head_solve_target)
	character_ik.set("RightHandIKTarget", right_hand_target)
	character_ik.set("LeftHandIKTarget", left_hand_target)
	character_ik.set("PhysicalRig", physical_rig)
	character.add_child(character_ik)
	for _frame_index in 12:
		await SceneUtils.wait_frames(self, 1)
		if skeleton.get_node_or_null("CharacterIKHandAuthorityStage") != null:
			print("RIG002_AUTHORITY_ADAPTER installed=characterik_runtime_hand_authority_stage skeleton=%s" % skeleton.get_path())
			return
	SceneUtils.fatal_error_and_quit("RIG-002 runner: installed CharacterIK under %s but the hand-authority stage never appeared under %s" % [
		character.get_path(),
		skeleton.get_path(),
	])


func _disable_all_ik_nodes(character: Node3D, skeleton: Skeleton3D) -> void:
	for node: Node in character.find_children("*", "", true, false):
		if _is_forearm_twist_chain_node(node) or not _is_ik_node(node):
			continue
		node.process_mode = Node.PROCESS_MODE_DISABLED
		if _has_property(node, "active"):
			node.set("active", false)
		if _has_property(node, "enabled"):
			node.set("enabled", false)
	for node: Node in character.find_children("*", "", true, false):
		if _is_forearm_twist_chain_node(node) or not _is_ik_node(node):
			continue
		if node.is_processing() or _is_active_or_enabled(node):
			SceneUtils.fatal_error_and_quit("RIG-002 runner: active IK node remained enabled: %s" % node.get_path())
			return
	var twist_modifier := skeleton.get_node("ForearmTwistModifier") as SkeletonModifier3D
	if twist_modifier == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing ForearmTwistModifier")
		return
	twist_modifier.active = true
	# The IK-005 hand-authority stage is part of the forearm-twist chain, not an IK solver: it samples the
	# canonical hand pose and hands the ForearmTwistModifier a fresh same-pass authority token. Without it
	# the writer correctly rests its helpers, which would erase the weight contrast this runner must
	# capture, so it stays active alongside the writer.
	var authority_stage := skeleton.get_node("CharacterIKHandAuthorityStage") as SkeletonModifier3D
	if authority_stage == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing CharacterIKHandAuthorityStage under %s" % skeleton.get_path())
		return
	authority_stage.active = true
	print("RIG002_NO_IK skeleton=%s non_twist_ik_nodes_disabled=true forearm_twist_active=%s hand_authority_stage_active=%s" % [
		skeleton.get_path(),
		twist_modifier.active,
		authority_stage.active,
	])


func _pulse_hand_authority_stage(skeleton: Skeleton3D) -> void:
	# Pulses the authority stage manually so the forced ForearmTwistModifier pass inside the pose
	# assertions consumes a fresh same-pass token carrying the just-applied canonical hand pose. This
	# mirrors the production ordering: authority submission immediately before the twist writer.
	var authority_stage := skeleton.get_node("CharacterIKHandAuthorityStage") as SkeletonModifier3D
	if authority_stage == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing CharacterIKHandAuthorityStage under %s" % skeleton.get_path())
		return
	authority_stage.call("_ProcessModificationWithDelta", 0.0)


func _disable_pose_animation_nodes(character: Node3D) -> void:
	for node: Node in character.find_children("*", "", true, false):
		if node is AnimationPlayer or node is AnimationTree:
			node.process_mode = Node.PROCESS_MODE_DISABLED
			if _has_property(node, "active"):
				node.set("active", false)


func _reset_to_actual_rest_pose(skeleton: Skeleton3D) -> void:
	for bone in skeleton.get_bone_count():
		skeleton.reset_bone_pose(bone)


func _assert_rest_t_pose(mesh_name: String, skeleton: Skeleton3D) -> void:
	for bone in skeleton.get_bone_count():
		var rest := skeleton.get_bone_global_rest(bone)
		var pose := skeleton.get_bone_global_pose(bone)
		if pose.origin.distance_to(rest.origin) > REST_TOLERANCE_METRES \
				or pose.basis.get_rotation_quaternion().angle_to(rest.basis.get_rotation_quaternion()) > REST_TOLERANCE_RADIANS:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: %s was not at skeleton rest pose before roll (bone %s pose=%s rest=%s)" % [
				mesh_name,
				skeleton.get_bone_name(bone),
				pose,
				rest,
			])
			return
	print("RIG002_REST_POSE scenario=%s mesh=%s body_transform=actual_skeleton_rest" % [_scenario_name, mesh_name])


func _apply_rest_relative_hand_roll(skeleton: Skeleton3D, side_name: String) -> void:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var lower_arm_rest := skeleton.get_bone_global_rest(lower_arm)
	var hand_rest := skeleton.get_bone_global_rest(hand)
	var longitudinal_axis := (hand_rest.origin - lower_arm_rest.origin).normalized()
	var lower_arm_local_axis := (lower_arm_rest.basis.inverse() * longitudinal_axis).normalized()
	var hand_rest_relative_to_lower_arm := lower_arm_rest.affine_inverse() * hand_rest
	var direction_sign := 1.0 if side_name == "Left" else -1.0
	var rolled_hand := lower_arm_rest * Transform3D(
		Basis(lower_arm_local_axis, direction_sign * ROLL_RADIANS) * hand_rest_relative_to_lower_arm.basis,
		hand_rest_relative_to_lower_arm.origin)
	skeleton.set_bone_global_pose(hand, rolled_hand)

	# This is the top-camera convention for the requested signed roll: positive is counter-clockwise and negative is
	# clockwise when the viewer follows the top camera's view direction. The actual roll axis remains the forearm's
	# rest-relative longitudinal axis; no helper, target, PlayerVRIK, or solver receives this transform.
	print("RIG002_ROLL_COMMAND scenario=%s side=%s coordinate=lower_arm_rest_relative axis=%s radians=%.9f top_camera_direction=%s" % [
		_scenario_name,
		side_name,
		lower_arm_local_axis,
		direction_sign * ROLL_RADIANS,
		"counter_clockwise" if direction_sign > 0.0 else "clockwise",
	])


func _assert_no_ik_and_roll_pose(mesh_name: String, skeleton: Skeleton3D, side_name: String) -> void:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var helper := _require_bone(skeleton, "%sForearmTwist" % side_name)
	var lower_before := skeleton.get_bone_global_pose(lower_arm).basis.get_rotation_quaternion()
	var hand_before := skeleton.get_bone_global_pose(hand)
	var helper_before := skeleton.get_bone_pose_rotation(helper)
	var lower_rest := skeleton.get_bone_global_rest(lower_arm)
	var hand_rest := skeleton.get_bone_global_rest(hand)
	var hand_relative_rest := lower_rest.affine_inverse() * hand_rest
	var hand_relative_pose := skeleton.get_bone_global_pose(lower_arm).affine_inverse() * skeleton.get_bone_global_pose(hand)
	var relative_rotation := hand_relative_rest.basis.get_rotation_quaternion().inverse() * hand_relative_pose.basis.get_rotation_quaternion()
	if absf(relative_rotation.get_angle() - ROLL_RADIANS) > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s hand did not receive an exact 180-degree rest-relative roll" % [mesh_name, side_name])
		return
	var modifier := skeleton.get_node("ForearmTwistModifier") as SkeletonModifier3D
	modifier.set("TwistWeight", _twist_weight)
	modifier.call("_ProcessModificationWithDelta", 0.0)
	var lower_error := lower_before.angle_to(skeleton.get_bone_global_pose(lower_arm).basis.get_rotation_quaternion())
	var hand_after := skeleton.get_bone_global_pose(hand)
	var hand_error := hand_before.basis.get_rotation_quaternion().angle_to(hand_after.basis.get_rotation_quaternion())
	var hand_origin_error := hand_before.origin.distance_to(hand_after.origin)
	if lower_error > REST_TOLERANCE_RADIANS or hand_error > REST_TOLERANCE_RADIANS \
			or hand_origin_error > REST_TOLERANCE_METRES:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: ForearmTwistModifier mutated canonical %s bones (lower_error=%.6f hand_error=%.6f hand_origin_error=%.9f)" % [side_name, lower_error, hand_error, hand_origin_error])
		return
	var helper_delta := helper_before.angle_to(skeleton.get_bone_pose_rotation(helper))
	if _twist_weight > 0.0 and helper_delta <= HELPER_RESPONSE_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s helper did not derive the canonical hand roll" % [mesh_name, side_name])
		return
	if is_zero_approx(_twist_weight) and helper_delta > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: weight_000 unexpectedly changed %s %s helper" % [mesh_name, side_name])
		return
	var world_axis := (hand_rest.origin - lower_rest.origin).normalized()
	var helper_rest := skeleton.get_bone_global_rest(helper)
	var helper_pose := skeleton.get_bone_global_pose(helper)
	var helper_global_delta := helper_pose.basis.get_rotation_quaternion() * helper_rest.basis.get_rotation_quaternion().inverse()
	var helper_axis := helper_global_delta.get_axis()
	var helper_angle := helper_global_delta.get_angle()
	var axial_alignment := absf(helper_axis.dot(world_axis)) if helper_angle > REST_TOLERANCE_RADIANS else 1.0
	if _twist_weight > 0.0 and axial_alignment < AXIAL_ALIGNMENT_TOLERANCE:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s helper rotation was not axial (alignment=%.6f)" % [mesh_name, side_name, axial_alignment])
		return
	print("RIG002_POSE_EVIDENCE scenario=%s mesh=%s side=%s rest=true ik_disabled=true roll_angle=%.9f top_direction=%s helper_delta=%.6f helper_global_axis=%s helper_global_angle=%.6f helper_axis_alignment=%.6f helper_rest_relative=%s hand_rest_relative=%s" % [
		_scenario_name,
		mesh_name,
		side_name,
		relative_rotation.get_angle(),
		"counter_clockwise" if side_name == "Left" else "clockwise",
		helper_delta,
		helper_axis,
		helper_angle,
		axial_alignment,
		helper_rest.affine_inverse() * helper_pose,
		hand_relative_rest.affine_inverse() * hand_relative_pose,
	])


func _assert_settled_rest_hand_roll(mesh_name: String, skeleton: Skeleton3D, side_name: String) -> void:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var lower_rest := skeleton.get_bone_global_rest(lower_arm)
	var lower_pose := skeleton.get_bone_global_pose(lower_arm)
	if lower_pose.origin.distance_to(lower_rest.origin) > REST_TOLERANCE_METRES \
				or lower_pose.basis.get_rotation_quaternion().angle_to(lower_rest.basis.get_rotation_quaternion()) > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: animation changed %s %s lower arm after the rest-hand roll" % [mesh_name, side_name])
		return
	var hand_rest_relative := lower_rest.affine_inverse() * skeleton.get_bone_global_rest(hand)
	var hand_pose_relative := lower_pose.affine_inverse() * skeleton.get_bone_global_pose(hand)
	var settled_roll := hand_rest_relative.basis.get_rotation_quaternion().inverse() * hand_pose_relative.basis.get_rotation_quaternion()
	if absf(settled_roll.get_angle() - ROLL_RADIANS) > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: animation changed %s %s hand after the 180-degree roll" % [mesh_name, side_name])


func _select_scenario() -> void:
	var arguments := OS.get_cmdline_user_args()
	for index in arguments.size():
		if arguments[index] == "--scenario" and index + 1 < arguments.size():
			_apply_scenario_selection(arguments[index + 1])
			return
		if arguments[index].begins_with("--scenario="):
			_apply_scenario_selection(arguments[index].trim_prefix("--scenario="))
			return


func _apply_scenario_selection(value: String) -> void:
	if value not in SUPPORTED_SCENARIOS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner only supports --scenario values: %s" % ", ".join(SUPPORTED_SCENARIOS))
		return
	_scenario_name = value


func _is_bend_scenario() -> bool:
	return _scenario_name == SCENARIO_REST_HAND_FLEXION_60 or _scenario_name == SCENARIO_REST_HAND_EXTENSION_60


func _is_combined_scenario() -> bool:
	return _scenario_name.begins_with("rest_hand_roll_") \
			and (_scenario_name.ends_with("_flexion_60") or _scenario_name.ends_with("_extension_60"))


func _is_extended_scenario() -> bool:
	return _scenario_name not in [SCENARIO_REST_HAND_ROLL_180, SCENARIO_REST_HAND_FLEXION_60, SCENARIO_REST_HAND_EXTENSION_60]


func _extended_angles(side_name: String) -> Vector2:
	if _scenario_name == "rest_hand_neutral":
		return Vector2.ZERO
	var label := _scenario_name.trim_prefix("rest_hand_roll_")
	var sign := 1.0 if label.begins_with("plus_") else -1.0
	var degrees := int(label.trim_prefix("plus_").trim_prefix("minus_").get_slice("_", 0))
	# Signed labels are top-camera directions, not mirrored skeleton-space signs.
	var mirror := 1.0 if side_name == "Left" else -1.0
	var bend := 60.0 if label.ends_with("_flexion_60") else (-60.0 if label.ends_with("_extension_60") else 0.0)
	return Vector2(deg_to_rad(sign * mirror * degrees), deg_to_rad(bend))


func _extended_frame(skeleton: Skeleton3D, side_name: String) -> Dictionary:
	if _is_combined_scenario():
		var frame := _bend_frame_from_rest(skeleton, side_name)
		if frame.is_empty():
			return frame
		var angles := _extended_angles(side_name)
		var palm_after_roll: Vector3 = Basis(frame["longitudinal"], angles.x) * frame["palm_rest"]
		var flexion_axis := (frame["longitudinal"] as Vector3).cross(palm_after_roll).normalized()
		frame["flexion_axis_world"] = flexion_axis
		frame["flexion_axis_lower"] = ((frame["lower_rest"] as Transform3D).basis.inverse() * flexion_axis).normalized()
		return frame
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var lower_rest := skeleton.get_bone_global_rest(lower_arm)
	var hand_rest := skeleton.get_bone_global_rest(hand)
	var axis := (hand_rest.origin - lower_rest.origin).normalized()
	return {
		"lower_arm": lower_arm, "hand": hand, "lower_rest": lower_rest,
		"hand_rest": hand_rest, "longitudinal": axis,
		"longitudinal_lower": (lower_rest.basis.inverse() * axis).normalized(),
	}


func _apply_extended_pose(skeleton: Skeleton3D, side_name: String) -> void:
	var frame := _extended_frame(skeleton, side_name)
	if frame.is_empty():
		return
	var angles := _extended_angles(side_name)
	var lower_rest: Transform3D = frame["lower_rest"]
	var hand_rest: Transform3D = frame["hand_rest"]
	var relative := lower_rest.affine_inverse() * hand_rest
	var twist := Basis(frame["longitudinal_lower"], angles.x)
	var swing := Basis(frame["flexion_axis_lower"], angles.y) if _is_combined_scenario() else Basis.IDENTITY
	# The roll is about the forearm rest axis, then bend about the independently derived
	# anatomical axis in the SAME lower-arm rest frame: D = S_bend * T_roll.
	skeleton.set_bone_global_pose(int(frame["hand"]), lower_rest * Transform3D(
		swing * twist * relative.basis, relative.origin))
	print("RIG002_EXTENDED_COMMAND scenario=%s side=%s roll_radians=%.9f bend_radians=%.9f composition=S_bend_times_T_roll" % [
		_scenario_name, side_name, angles.x, angles.y])


func _assert_extended_pose(mesh_name: String, skeleton: Skeleton3D, side_name: String, settled: bool) -> void:
	var frame := _extended_frame(skeleton, side_name)
	if frame.is_empty():
		return
	var angles := _extended_angles(side_name)
	var lower_rest: Transform3D = frame["lower_rest"]
	var hand_rest: Transform3D = frame["hand_rest"]
	var lower_arm := int(frame["lower_arm"])
	var hand := int(frame["hand"])
	var lower_pose := skeleton.get_bone_global_pose(lower_arm)
	var hand_pose := skeleton.get_bone_global_pose(hand)
	var axis: Vector3 = frame["longitudinal_lower"]
	var twist := Quaternion(axis, angles.x)
	var swing := Quaternion(frame["flexion_axis_lower"], angles.y) if _is_combined_scenario() else Quaternion.IDENTITY
	# Independent oracle against engine read-back: build the intended rotation from
	# rest geometry, not from the applied pose or the modifier's decomposition.
	var actual := (lower_pose.affine_inverse() * hand_pose).basis.get_rotation_quaternion() \
			* (lower_rest.affine_inverse() * hand_rest).basis.get_rotation_quaternion().inverse()
	var expected := swing * twist
	if lower_pose.origin.distance_to(lower_rest.origin) > REST_TOLERANCE_METRES \
			or lower_pose.basis.get_rotation_quaternion().angle_to(lower_rest.basis.get_rotation_quaternion()) > REST_TOLERANCE_RADIANS \
			or hand_pose.origin.distance_to(hand_rest.origin) > REST_TOLERANCE_METRES \
			or actual.angle_to(expected) > BEND_COMPONENT_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s %s hand/lower arm lost commanded pose or wrist origin (rotation_error=%.9f)" % [
			mesh_name, side_name, _scenario_name, actual.angle_to(expected)])
		return
	if _is_combined_scenario():
		var recovered := actual * twist.inverse()
		if recovered.angle_to(swing) > BEND_COMPONENT_TOLERANCE_RADIANS:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: combined pose did not retain S_bend x T_roll ordering")
			return
		# After roll, positive flexion still moves the fingertip towards its rotated
		# palm face (not towards a fixed world-facing palm target).
		var tip := _require_bone(skeleton, "%sMiddleDistal" % side_name)
		var tip_rest := (skeleton.get_bone_global_rest(tip).origin - hand_rest.origin).normalized()
		var palm_rolled := Basis(frame["longitudinal"], angles.x) * hand_rest.basis.z.normalized()
		var tip_rolled := Basis(frame["longitudinal"], angles.x) * tip_rest
		var tip_bent := Basis(frame["flexion_axis_world"], angles.y) * tip_rolled
		var change := tip_bent.dot(palm_rolled) - tip_rolled.dot(palm_rolled)
		if (angles.y > 0.0 and change < BEND_SIGN_CONVENTION_MINIMUM_ALIGNMENT_DELTA) \
				or (angles.y < 0.0 and change > -BEND_SIGN_CONVENTION_MINIMUM_ALIGNMENT_DELTA):
			SceneUtils.fatal_error_and_quit("RIG-002 runner: combined %s %s bend direction disagrees with rotated palm (delta=%.9f)" % [mesh_name, side_name, change])
			return
	if not settled:
		var helper := _require_bone(skeleton, "%sForearmTwist" % side_name)
		var modifier := skeleton.get_node("ForearmTwistModifier") as SkeletonModifier3D
		modifier.set("TwistWeight", _twist_weight)
		modifier.call("_ProcessModificationWithDelta", 0.0)
		var helper_delta := skeleton.get_bone_rest(helper).basis.get_rotation_quaternion().angle_to(skeleton.get_bone_pose_rotation(helper))
		if is_zero_approx(_twist_weight) and helper_delta > REST_TOLERANCE_RADIANS:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: zero-weight extended helper moved")
			return
		if _twist_weight > 0.0 and absf(angles.x) > REST_TOLERANCE_RADIANS \
				and helper_delta <= HELPER_RESPONSE_TOLERANCE_RADIANS:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: extended helper did not engage")
			return
		var helper_rest := skeleton.get_bone_global_rest(helper)
		var helper_pose := skeleton.get_bone_global_pose(helper)
		var helper_rotation := helper_pose.basis.get_rotation_quaternion() * helper_rest.basis.get_rotation_quaternion().inverse()
		if _twist_weight > 0.0 and helper_delta > HELPER_RESPONSE_TOLERANCE_RADIANS \
				and absf(helper_rotation.get_axis().dot(frame["longitudinal"])) < AXIAL_ALIGNMENT_TOLERANCE:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: extended twist helper lost axial alignment")
			return
		_assert_extended_pose(mesh_name, skeleton, side_name, true)
		return
	print("RIG002_EXTENDED_EVIDENCE scenario=%s mesh=%s side=%s settled=%s roll=%.9f bend=%.9f rotation_error=%.9f wrist_fixed=true" % [
		_scenario_name, mesh_name, side_name, settled, angles.x, angles.y, actual.angle_to(expected)])


func _validate_coverage_declaration() -> bool:
	var expected: Array[String] = [SCENARIO_REST_HAND_ROLL_180, SCENARIO_REST_HAND_FLEXION_60, SCENARIO_REST_HAND_EXTENSION_60, SCENARIO_POSED_ARM_TWIST_TRAJECTORY, SCENARIO_POSED_ARM_BEND_TRAJECTORY, "rest_hand_neutral"]
	for degrees in [45, 90, 135, 180]:
		for sign_name in ["plus", "minus"]:
			expected.append("rest_hand_roll_%s_%d" % [sign_name, degrees])
	for sign_name in ["plus", "minus"]:
		for bend_name in ["flexion", "extension"]:
			expected.append("rest_hand_roll_%s_90_%s_60" % [sign_name, bend_name])
	if SUPPORTED_SCENARIOS.size() != expected.size():
		SceneUtils.fatal_error_and_quit("RIG-002 runner: incomplete or duplicate scenario matrix")
		return false
	for scenario: String in expected:
		if SUPPORTED_SCENARIOS.count(scenario) != 1:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: missing or duplicate scenario %s" % scenario)
			return false
	return true


func _print_coverage() -> void:
	for scenario: String in SUPPORTED_SCENARIOS:
		if scenario == SCENARIO_POSED_ARM_TWIST_TRAJECTORY:
			print("RIG002_COVERAGE scenario=%s sexes=female,male sides=left,right weights=0,%.2f,0.25 views=top_down_00..08" % [scenario, _selected_weight])
			continue
		if scenario == SCENARIO_POSED_ARM_BEND_TRAJECTORY:
			print("RIG002_COVERAGE scenario=%s sexes=female,male sides=left,right weights=0,%.2f,0.25 views=top_down_00..04" % [scenario, _selected_weight])
			continue
		var views := "top_down,profile" if scenario.ends_with("_60") else "top_down"
		print("RIG002_COVERAGE scenario=%s sexes=female,male sides=left,right weights=0,%.2f,0.25 views=%s" % [scenario, _selected_weight, views])


func _assert_capture_paths_available() -> bool:
	for mesh_name in ["Female", "Male"]:
		for side_name in ["Left", "Right"]:
			for view_name in _capture_views():
				var path := _absolute_artefact_path(_capture_path(mesh_name, side_name, view_name))
				var folder := path.get_base_dir()
				if not _safe_capture_folder(folder):
					return false
				var directory := DirAccess.open(folder)
				if directory == null or directory.is_link(path.get_file()) or directory.file_exists(path.get_file()) or directory.dir_exists(path.get_file()):
					SceneUtils.fatal_error_and_quit("RIG-002 runner: existing or inaccessible capture %s" % path)
					return false
				# Reserve every target before rendering. Keep reservations after success or failure:
				# even an interrupted capture must not be silently retried over the same name.
				if directory.make_dir(path.get_file() + ".reserved") != OK:
					SceneUtils.fatal_error_and_quit("RIG-002 runner: capture target already reserved %s" % path)
					return false
	return true


func _capture_views() -> Array[String]:
	if _scenario_name == SCENARIO_POSED_ARM_BEND_TRAJECTORY:
		var views: Array[String] = []
		var phases := _bend_phases()
		for index in phases.size():
			views.append("top_down_%02d_%s" % [index, phases[index][0]])
		return views
	if _scenario_name == SCENARIO_POSED_ARM_TWIST_TRAJECTORY:
		var views: Array[String] = []
		var phases := _twist_phases()
		for index in phases.size():
			views.append("top_down_%02d_%s" % [index, phases[index][0]])
		return views
	return ["top_down", "profile"] if _is_bend_scenario() or _is_combined_scenario() else ["top_down"]


func _twist_phases() -> Array:
	# Complete visible interval: neutral, outward travel, extreme, hold, return and settling.
	# Repeated phases are distinct frames; each is held for three rendered frames.
	return [["neutral", 0.0], ["out_45", 45.0], ["out_90", 90.0],
		["out_135", 135.0], ["extreme_180", 180.0], ["hold_180", 180.0],
		["return_90", 90.0], ["return_neutral", 0.0], ["settled_neutral", 0.0]]


func _bend_phases() -> Array:
	# Static samples only: three rendered frames at each pose, no inferred travel between them.
	return [["neutral", 0.0], ["flex_45", 45.0], ["extend_45", -45.0],
		["extend_90", -90.0], ["flex_90", 90.0]]


func _run_posed_arm_twist_trajectory() -> void:
	for mesh_name: String in ["Female", "Male"]:
		_set_subject_visible(mesh_name)
		var character := _photobooth.get_node("Subject/%s" % mesh_name) as Node3D
		var skeleton := _skeleton(mesh_name)
		await _ensure_hand_authority_stage(character, skeleton)
		_disable_all_ik_nodes(character, skeleton)
		_disable_pose_animation_nodes(character)
		for side_name: String in ["Left", "Right"]:
			_reset_to_actual_rest_pose(skeleton)
			_assert_rest_t_pose(mesh_name, skeleton)
			var upper := _require_bone(skeleton, "%sUpperArm" % side_name)
			var lower := _require_bone(skeleton, "%sLowerArm" % side_name)
			var hand := _require_bone(skeleton, "%sHand" % side_name)
			var helper := _require_bone(skeleton, "%sForearmTwist" % side_name)
			var upper_rest := skeleton.get_bone_global_rest(upper)
			var lower_rest := skeleton.get_bone_global_rest(lower)
			var hand_rest := skeleton.get_bone_global_rest(hand)
			var forward := _subject_forward(skeleton)
			var up := (skeleton.get_bone_global_rest(_require_bone(skeleton, "Head")).origin - skeleton.get_bone_global_rest(_require_bone(skeleton, "Hips")).origin).normalized()
			# Bend the elbow in the horizontal plane, bringing the forearm partly forward while
			# retaining a transverse component for the unchanged top-down silhouette view.
			var lateral := (hand_rest.origin - upper_rest.origin).normalized()
			var upper_rotation := Basis(up, atan2(up.dot(lateral.cross(forward)), lateral.dot(forward)) * 0.28)
			var lower_rotation := Basis(up, atan2(up.dot(lateral.cross(forward)), lateral.dot(forward)) * 0.35)
			var upper_pose := Transform3D(upper_rotation * upper_rest.basis, upper_rest.origin)
			var lower_pose := upper_pose * (upper_rest.affine_inverse() * lower_rest) * Transform3D(lower_rotation, Vector3.ZERO)
			var hand_neutral := lower_pose * (lower_rest.affine_inverse() * hand_rest)
			var axis := (hand_neutral.origin - lower_pose.origin).normalized()
			var local_axis := (lower_pose.basis.inverse() * axis).normalized()
			if axis.dot(forward) < 0.2 or absf(axis.dot(lateral)) < 0.2:
				SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: posed arm does not expose a forward and lateral forearm")
				return
			# Hand +Z is the palm normal: the rest-geometry oracle checks thumb/middle/little
			# chirality. Conjugate that normal through the posed arm before choosing the
			# anatomical sign. Both normal and target must be projected off the posed
			# forearm axis; an axial component cannot be changed by wrist roll.
			var rest_frame := _bend_frame_from_rest(skeleton, side_name)
			if rest_frame.is_empty():
				return
			var palm_neutral := (hand_neutral.basis * (hand_rest.basis.inverse() * (rest_frame["palm_rest"] as Vector3))).normalized()
			var palm_flat := palm_neutral - axis * palm_neutral.dot(axis)
			var forward_flat := forward - axis * forward.dot(axis)
			if palm_flat.length() < PALM_TARGET_MINIMUM_LENGTH or forward_flat.length() < PALM_TARGET_MINIMUM_LENGTH:
				SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: posed palm/forward plane is degenerate")
				return
			palm_flat = palm_flat.normalized()
			forward_flat = forward_flat.normalized()
			var cross_dot := axis.dot(palm_flat.cross(forward_flat))
			var neutral_dot := palm_flat.dot(forward_flat)
			var anatomical_sign := -1.0 if side_name == "Left" else 1.0
			# The upstream yaw already turns this palm partly forward; it need not
			# begin perpendicular to forward. The signed cross product is the
			# independent handedness cue, even with that nonzero neutral alignment.
			if cross_dot * anatomical_sign < 0.5 or neutral_dot > 0.75:
				SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: %s palm-to-forward direction disagrees with handedness (axis_cross_dot=%.6f neutral_dot=%.6f)" % [side_name, cross_dot, neutral_dot])
				return
			print("RIG002_TWIST_DIAGNOSTIC_POSE mesh=%s side=%s upper_yaw_fraction=0.28 elbow_yaw_fraction=0.35 forearm_axis=%s forward_dot=%.4f lateral_dot=%.4f" % [mesh_name, side_name, axis, axis.dot(forward), axis.dot(lateral)])
			for index in _twist_phases().size():
				var phase: Array = _twist_phases()[index]
				var radians := deg_to_rad(float(phase[1]) * anatomical_sign)
				skeleton.set_bone_global_pose(upper, upper_pose)
				skeleton.set_bone_global_pose(lower, lower_pose)
				skeleton.set_bone_global_pose(hand, lower_pose * Transform3D(
					Basis(local_axis, radians) * (lower_rest.affine_inverse() * hand_rest).basis,
					(lower_rest.affine_inverse() * hand_rest).origin))
				_pulse_hand_authority_stage(skeleton)
				var modifier := skeleton.get_node("ForearmTwistModifier") as SkeletonModifier3D
				modifier.set("TwistWeight", _twist_weight)
				modifier.call("_ProcessModificationWithDelta", 0.0)
				await SceneUtils.wait_frames(self, 3)
				var observed_hand := skeleton.get_bone_global_pose(hand)
				var expected_hand := lower_pose * Transform3D(
					Basis(local_axis, radians) * (lower_rest.affine_inverse() * hand_rest).basis,
					(lower_rest.affine_inverse() * hand_rest).origin)
				# Observe the modifier result before the next phase authors any pose. Compare full
				# skeleton-local transforms, not quaternion-only read-backs that miss scale or
				# translation drift on either deform-only helper.
				if not _twist_diagnostic_transform_matches(skeleton.get_bone_global_pose(upper), upper_pose) \
						or not _twist_diagnostic_transform_matches(skeleton.get_bone_global_pose(lower), lower_pose) \
						or not _twist_diagnostic_transform_matches(observed_hand, expected_hand) \
						or (is_zero_approx(_twist_weight) and not _twist_diagnostic_helpers_rest(skeleton, helper)):
					SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: full upstream/hand/helper transform failed mesh=%s side=%s phase=%s upper=%s lower=%s hand=%s helper_local=%s" % [mesh_name, side_name, phase[0], skeleton.get_bone_global_pose(upper), skeleton.get_bone_global_pose(lower), observed_hand, skeleton.get_bone_pose(helper)])
					return
				if not _twist_diagnostic_weighted_helpers_valid(skeleton, helper, absf(radians) > REST_TOLERANCE_RADIANS, mesh_name, side_name, str(phase[0])):
					return
				if index <= 2:
					var posed_palm := observed_hand.basis.z.normalized()
					var posed_flat := posed_palm - axis * posed_palm.dot(axis)
					if posed_flat.length() < PALM_TARGET_MINIMUM_LENGTH:
						SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: palm collapsed into forearm axis")
						return
					var alignment := posed_flat.normalized().dot(forward_flat)
					# Both intermediate poses must face substantially more forward than
					# neutral. 45 may align more closely than 90 when the posed upstream
					# arm has already rotated the neutral palm towards forward.
					if (index == 0 and absf(alignment - neutral_dot) > 0.01) \
							or (index == 1 and alignment < neutral_dot + 0.25) \
							or (index == 2 and alignment < neutral_dot + 0.20):
						SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: palm rotated away from subject forward mesh=%s side=%s phase=%s neutral_dot=%.6f observed_dot=%.6f" % [mesh_name, side_name, phase[0], neutral_dot, alignment])
						return
					print("RIG002_TWIST_DIRECTION mesh=%s side=%s phase=%s radians=%.6f axis_cross_dot=%.6f palm_forward_dot=%.6f" % [mesh_name, side_name, phase[0], radians, cross_dot, alignment])
				_focus_top_down_camera(skeleton, side_name)
				_assert_top_camera_framing(skeleton, side_name)
				_capture_camera.make_current()
				if root.get_camera_3d() != _capture_camera:
					SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: capture camera not selected")
					return
				var view := "top_down_%02d_%s" % [index, phase[0]]
				await SceneUtils.capture_screenshot(self, _output_dir.trim_prefix("temp/").path_join(_capture_path(mesh_name, side_name, view)))
				if not _assert_twist_diagnostic_pixel_evidence(mesh_name, side_name, view, absf(radians) > REST_TOLERANCE_RADIANS):
					return
				print("RIG002_TWIST_DIAGNOSTIC_FRAME mesh=%s side=%s phase=%s degrees=%.1f hand_origin=%s weight=%.6f" % [mesh_name, side_name, phase[0], rad_to_deg(radians), observed_hand.origin, _twist_weight])
	print("RIG002_TWIST_DIAGNOSTIC_COMPLETE weight=%.6f artefact_dir=res://%s/%s" % [_twist_weight, _output_dir, _scenario_name])
	print("RIG002_PIXEL_CONTRAST_SUMMARY scenario=%s weight=%.2f failures=%d minimum_fraction=%.6f" % [
		_scenario_name, _twist_weight, _pixel_contrast_failures, PIXEL_DIFF_MINIMUM_FRACTION])
	quit(1 if _pixel_contrast_failures > 0 else 0)


func _run_posed_arm_bend_trajectory() -> void:
	for mesh_name: String in ["Female", "Male"]:
		_set_subject_visible(mesh_name)
		var character := _photobooth.get_node("Subject/%s" % mesh_name) as Node3D
		var skeleton := _skeleton(mesh_name)
		await _ensure_hand_authority_stage(character, skeleton)
		_disable_all_ik_nodes(character, skeleton)
		_disable_pose_animation_nodes(character)
		for side_name: String in ["Left", "Right"]:
			_reset_to_actual_rest_pose(skeleton)
			_assert_rest_t_pose(mesh_name, skeleton)
			var upper := _require_bone(skeleton, "%sUpperArm" % side_name)
			var lower := _require_bone(skeleton, "%sLowerArm" % side_name)
			var hand := _require_bone(skeleton, "%sHand" % side_name)
			var middle := _require_bone(skeleton, "%sMiddleDistal" % side_name)
			var helper := _require_bone(skeleton, "%sForearmTwist" % side_name)
			var opposite := "Right" if side_name == "Left" else "Left"
			var opposite_bones := [
				_require_bone(skeleton, "%sUpperArm" % opposite),
				_require_bone(skeleton, "%sLowerArm" % opposite),
				_require_bone(skeleton, "%sHand" % opposite)]
			var opposite_helpers := [
				_require_bone(skeleton, "%sForearmTwist" % opposite)]
			var upper_rest := skeleton.get_bone_global_rest(upper)
			var lower_rest := skeleton.get_bone_global_rest(lower)
			var hand_rest := skeleton.get_bone_global_rest(hand)
			var forward := _subject_forward(skeleton)
			var up := (skeleton.get_bone_global_rest(_require_bone(skeleton, "Head")).origin - skeleton.get_bone_global_rest(_require_bone(skeleton, "Hips")).origin).normalized()
			var lateral := (hand_rest.origin - upper_rest.origin).normalized()
			# Same upstream geometry and yaw fractions as the confirmed twist control.
			var yaw := atan2(up.dot(lateral.cross(forward)), lateral.dot(forward))
			var upper_pose := Transform3D(Basis(up, yaw * 0.28) * upper_rest.basis, upper_rest.origin)
			var lower_pose := upper_pose * (upper_rest.affine_inverse() * lower_rest) * Transform3D(Basis(up, yaw * 0.35), Vector3.ZERO)
			var hand_relative := lower_rest.affine_inverse() * hand_rest
			var hand_neutral := lower_pose * hand_relative
			var axis := (hand_neutral.origin - lower_pose.origin).normalized()
			if axis.dot(forward) < 0.2 or absf(axis.dot(lateral)) < 0.2:
				SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: upstream posed forearm direction failed")
				return
			var frame := _bend_frame_from_rest(skeleton, side_name)
			if frame.is_empty():
				return
			# Probe the fixed twist camera before any capture. A palm normal is transverse
			# to the forearm: exact image-up is impossible when the arm points partly up-image.
			skeleton.set_bone_global_pose(upper, upper_pose)
			skeleton.set_bone_global_pose(lower, lower_pose)
			skeleton.set_bone_global_pose(hand, hand_neutral)
			_focus_top_down_camera(skeleton, side_name)
			_assert_top_camera_framing(skeleton, side_name)
			var camera_up_world := _capture_camera.global_basis.y.normalized()
			var camera_up := (skeleton.global_basis.inverse() * camera_up_world).normalized()
			var maximum_up_alignment := (camera_up - axis * camera_up.dot(axis)).length()
			var forward_up_dot := (skeleton.global_basis * forward).normalized().dot(camera_up_world)
			if maximum_up_alignment < 0.65 or forward_up_dot < 0.99:
				SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: twist camera/posed forearm cannot frame an image-up palm mesh=%s side=%s maximum_up=%.6f forward_up=%.6f" % [mesh_name, side_name, maximum_up_alignment, forward_up_dot])
				return
			# Geometry-checked imported hand-rest +Z, conjugated through the posed arm.
			var palm_neutral := (hand_neutral.basis * (hand_rest.basis.inverse() * (frame["palm_rest"] as Vector3))).normalized()
			var palm_flat := palm_neutral - axis * palm_neutral.dot(axis)
			var forward_flat := camera_up - axis * camera_up.dot(axis)
			if palm_flat.length() < PALM_TARGET_MINIMUM_LENGTH or forward_flat.length() < PALM_TARGET_MINIMUM_LENGTH:
				SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: posed palm/forward plane is degenerate")
				return
			palm_flat = palm_flat.normalized()
			forward_flat = forward_flat.normalized()
			var pronation := atan2(axis.dot(palm_flat.cross(forward_flat)), palm_flat.dot(forward_flat))
			var bend_axis := axis.cross(forward_flat).normalized()
			var local_axis := (lower_pose.basis.inverse() * axis).normalized()
			var local_bend_axis := (lower_pose.basis.inverse() * bend_axis).normalized()
			var tip_local := hand_rest.basis.inverse() * (skeleton.get_bone_global_rest(middle).origin - hand_rest.origin)
			var palm_forward_pose := lower_pose * Transform3D(Basis(local_axis, pronation) * hand_relative.basis, hand_relative.origin)
			var palm_observed := palm_forward_pose.basis.z.normalized()
			var tip_before := (palm_forward_pose.basis * tip_local).normalized()
			if palm_observed.dot(forward_flat) < PALM_FORWARD_MINIMUM_DOT or absf(tip_before.dot(forward_flat)) > 0.25:
				SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: neutral palm or middle finger disagrees with anatomical forward (palm=%.6f tip=%.6f)" % [palm_observed.dot(forward_flat), tip_before.dot(forward_flat)])
				return
			print("RIG002_BEND_POSE mesh=%s side=%s axis=%s forward_dot=%.4f lateral_dot=%.4f camera_up=%s forward_up_dot=%.6f maximum_up_alignment=%.6f neutral_up_dot=%.6f pronation=%.6f" % [mesh_name, side_name, axis, axis.dot(forward), axis.dot(lateral), camera_up_world, forward_up_dot, maximum_up_alignment, palm_observed.dot(camera_up), pronation])
			var phases := _bend_phases()
			for index in phases.size():
				var phase: Array = phases[index]
				var radians := deg_to_rad(float(phase[1]))
				var expected_hand := lower_pose * Transform3D(Basis(local_bend_axis, radians) * Basis(local_axis, pronation) * hand_relative.basis, hand_relative.origin)
				skeleton.set_bone_global_pose(upper, upper_pose)
				skeleton.set_bone_global_pose(lower, lower_pose)
				skeleton.set_bone_global_pose(hand, expected_hand)
				_pulse_hand_authority_stage(skeleton)
				var modifier := skeleton.get_node("ForearmTwistModifier") as SkeletonModifier3D
				modifier.set("TwistWeight", _twist_weight)
				modifier.call("_ProcessModificationWithDelta", 0.0)
				await SceneUtils.wait_frames(self, 3)
				var observed_hand := skeleton.get_bone_global_pose(hand)
				var tip_after := (observed_hand.basis * tip_local).normalized()
				# Independent anatomical oracle: positive moves the middle finger towards the
				# verified palm face; negative moves it away, relative to the neutral posed wrist.
				var anatomical_delta := tip_after.dot(forward_flat) - tip_before.dot(forward_flat)
				if not _twist_diagnostic_transform_matches(skeleton.get_bone_global_pose(upper), upper_pose) \
						or not _twist_diagnostic_transform_matches(skeleton.get_bone_global_pose(lower), lower_pose) \
						or not _twist_diagnostic_transform_matches(observed_hand, expected_hand) \
						or observed_hand.origin.distance_to(hand_neutral.origin) > REST_TOLERANCE_METRES \
						or (is_zero_approx(_twist_weight) and not _twist_diagnostic_helpers_rest(skeleton, helper)) \
						or (radians > 0.0 and anatomical_delta < 0.25) \
						or (radians < 0.0 and anatomical_delta > -0.25) \
						or (is_zero_approx(radians) and absf(anatomical_delta) > 0.01):
					SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: pose/authority/anatomical sign failed mesh=%s side=%s phase=%s tip_delta=%.6f hand=%s" % [mesh_name, side_name, phase[0], anatomical_delta, observed_hand])
					return
				if not _twist_diagnostic_weighted_helpers_valid(skeleton, helper, absf(pronation) > REST_TOLERANCE_RADIANS, mesh_name, side_name, str(phase[0])):
					return
				for opposite_bone: int in opposite_bones:
					if not _twist_diagnostic_transform_matches(skeleton.get_bone_global_pose(opposite_bone), skeleton.get_bone_global_rest(opposite_bone)):
						SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: opposite side moved mesh=%s side=%s phase=%s" % [mesh_name, side_name, phase[0]])
						return
				for opposite_helper: int in opposite_helpers:
					if not _twist_diagnostic_transform_matches(skeleton.get_bone_pose(opposite_helper), skeleton.get_bone_rest(opposite_helper)):
						SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: opposite helper moved mesh=%s side=%s phase=%s" % [mesh_name, side_name, phase[0]])
						return
				_focus_top_down_camera(skeleton, side_name)
				_assert_top_camera_framing(skeleton, side_name)
				_capture_camera.make_current()
				if root.get_camera_3d() != _capture_camera:
					SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: capture camera not selected")
					return
				# Read the rendered camera's actual screen-up and observed hand normal, not
				# the authored wrist rotation. At ±90 the bent palm cannot still face up.
				var observed_palm_world := (skeleton.global_basis * observed_hand.basis.z).normalized()
				var actual_up_dot := observed_palm_world.dot(_capture_camera.global_basis.y.normalized())
				# The fixed bend axis is not necessarily perpendicular to image-up: its
				# cross term contributes to the projected normal as the wrist bends.
				# Use measured neutral geometry rather than duplicating the authored basis.
				var expected_up_dot := palm_observed.dot(camera_up) * cos(radians) \
						+ bend_axis.cross(palm_observed).dot(camera_up) * sin(radians) \
						+ bend_axis.dot(palm_observed) * bend_axis.dot(camera_up) * (1.0 - cos(radians))
				if absf(actual_up_dot - expected_up_dot) > 0.02:
					SceneUtils.fatal_error_and_quit("RIG-002 bend diagnostic: post-write palm/camera-up mismatch mesh=%s side=%s phase=%s actual=%.6f expected=%.6f" % [mesh_name, side_name, phase[0], actual_up_dot, expected_up_dot])
					return
				var view := "top_down_%02d_%s" % [index, phase[0]]
				await SceneUtils.capture_screenshot(self, _output_dir.trim_prefix("temp/").path_join(_capture_path(mesh_name, side_name, view)))
				if not _assert_twist_diagnostic_pixel_evidence(mesh_name, side_name, view, absf(pronation) > REST_TOLERANCE_RADIANS):
					return
				print("RIG002_BEND_FRAME mesh=%s side=%s phase=%s view=top_down degrees=%.1f palm_camera_up_dot=%.6f maximum_up_alignment=%.6f tip_forward_delta=%.6f wrist=%s weight=%.6f" % [mesh_name, side_name, phase[0], float(phase[1]), actual_up_dot, maximum_up_alignment, anatomical_delta, observed_hand.origin, _twist_weight])
	print("RIG002_BEND_DIAGNOSTIC_COMPLETE weight=%.6f samples_per_side=5 minimum_settle_frames_per_side=15 artefact_dir=res://%s/%s" % [_twist_weight, _output_dir, _scenario_name])
	quit(0)


func _twist_diagnostic_transform_matches(actual: Transform3D, expected: Transform3D) -> bool:
	# The existing 1e-4 m positional and 1e-3 rad rotation tolerances also bound
	# each basis column, including scale/shear (which angle_to would not inspect).
	return actual.origin.distance_to(expected.origin) <= REST_TOLERANCE_METRES \
			and actual.basis.x.distance_to(expected.basis.x) <= REST_TOLERANCE_RADIANS \
			and actual.basis.y.distance_to(expected.basis.y) <= REST_TOLERANCE_RADIANS \
			and actual.basis.z.distance_to(expected.basis.z) <= REST_TOLERANCE_RADIANS


func _twist_diagnostic_helpers_rest(skeleton: Skeleton3D, helper: int) -> bool:
	return _twist_diagnostic_transform_matches(skeleton.get_bone_pose(helper), skeleton.get_bone_rest(helper))


func _twist_diagnostic_weighted_helpers_valid(skeleton: Skeleton3D, helper: int, axial_commanded: bool, mesh_name: String, side_name: String, phase: String) -> bool:
	if is_zero_approx(_twist_weight):
		return true
	var helper_pose := skeleton.get_bone_pose(helper)
	var helper_finite := helper_pose.is_finite()
	var helper_delta := NAN
	if helper_finite:
		helper_delta = skeleton.get_bone_rest(helper).basis.get_rotation_quaternion().angle_to(helper_pose.basis.get_rotation_quaternion())
	var engagement_failed := axial_commanded and (is_nan(helper_delta) or helper_delta <= HELPER_RESPONSE_TOLERANCE_RADIANS)
	var neutral_rest_failed := not axial_commanded and not _twist_diagnostic_helpers_rest(skeleton, helper)
	var helper_global := skeleton.get_bone_global_pose(helper)
	var helper_global_failed := not helper_global.is_finite()
	if not helper_finite or engagement_failed or neutral_rest_failed or helper_global_failed:
		SceneUtils.fatal_error_and_quit("RIG-002 diagnostic: helper authority/engagement failed mesh=%s side=%s phase=%s helper_finite=%s engagement_failed=%s neutral_rest_failed=%s helper_global_failed=%s helper_delta=%.6f helper_local=%s helper_global=%s" % [mesh_name, side_name, phase, helper_finite, engagement_failed, neutral_rest_failed, helper_global_failed, helper_delta, helper_pose, helper_global])
		return false
	print("RIG002_DIAGNOSTIC_HELPERS mesh=%s side=%s phase=%s helper_delta=%.6f axial_commanded=%s helper_global_failed=%s" % [mesh_name, side_name, phase, helper_delta, axial_commanded, helper_global_failed])
	return true


func _assert_twist_diagnostic_pixel_evidence(mesh_name: String, side_name: String, view_name: String, axial_commanded: bool) -> bool:
	var image := _load_artefact_image(_capture_path(mesh_name, side_name, view_name))
	if image == null:
		return false
	if not _assert_twist_diagnostic_nonblank(image, "%s %s %s" % [mesh_name, side_name, view_name]):
		return false
	if _twist_weight <= 0.0 or not axial_commanded:
		return true
	# Same-run zero is required before capture. A new run must acquire both weights
	# from the same imported assets; historical references are not interchangeable.
	var zero := _load_artefact_image(_capture_path_for_weight(mesh_name, side_name, view_name, COMPANION_WEIGHT_LABEL))
	if zero == null:
		return false
	if image.get_size() != zero.get_size():
		SceneUtils.fatal_error_and_quit("RIG-002 diagnostic: weighted/zero image dimensions differ mesh=%s side=%s view=%s" % [mesh_name, side_name, view_name])
		return false
	var fraction := _sampled_differing_pixel_fraction(image, zero)
	if fraction < PIXEL_DIFF_MINIMUM_FRACTION:
		if _scenario_name == SCENARIO_POSED_ARM_TWIST_TRAJECTORY:
			_pixel_contrast_failures += 1
			push_error("RIG002_TWIST_CONTRAST_FAILURE mesh=%s side=%s phase=%s fraction=%.6f minimum=%.6f" % [
				mesh_name, side_name, view_name, fraction, PIXEL_DIFF_MINIMUM_FRACTION])
			return true
		SceneUtils.fatal_error_and_quit("RIG-002 diagnostic: pixel contrast failed mesh=%s side=%s view=%s fraction=%.6f minimum=%.6f" % [mesh_name, side_name, view_name, fraction, PIXEL_DIFF_MINIMUM_FRACTION])
		return false
	print("RIG002_DIAGNOSTIC_PIXEL_DIFF mesh=%s side=%s view=%s fraction=%.6f minimum=%.6f" % [mesh_name, side_name, view_name, fraction, PIXEL_DIFF_MINIMUM_FRACTION])
	return true


func _assert_twist_diagnostic_nonblank(image: Image, label: String) -> bool:
	var bounds := _sampled_channel_bounds(image)
	var channel_range: int = int(bounds["maximum"]) - int(bounds["minimum"])
	if channel_range <= NON_BLANK_MINIMUM_CHANNEL_RANGE:
		SceneUtils.fatal_error_and_quit("RIG-002 twist diagnostic: %s capture is blank (sampled channel range=%d minimum=%d)" % [label, channel_range, NON_BLANK_MINIMUM_CHANNEL_RANGE])
		return false
	print("RIG002_PIXEL_EVIDENCE scenario=%s capture=%s non_blank=true sampled_channel_range=%d stride=%d" % [_scenario_name, label, channel_range, PIXEL_SAMPLE_STRIDE])
	return true


func _require_zero_companions() -> bool:
	if _twist_weight <= 0.0:
		return true
	for mesh_name in ["Female", "Male"]:
		for side_name in ["Left", "Right"]:
			for view_name in _capture_views():
				var path := _absolute_artefact_path(_capture_path_for_weight(mesh_name, side_name, view_name, COMPANION_WEIGHT_LABEL))
				if not _safe_capture_folder(path.get_base_dir()):
					return false
				var directory := DirAccess.open(path.get_base_dir())
				if directory == null or directory.is_link(path.get_file()) or not directory.file_exists(path.get_file()):
					SceneUtils.fatal_error_and_quit("RIG-002 runner: zero counterpart must precede weighted capture: %s" % path)
					return false
				var image := Image.load_from_file(path)
				if image == null or image.is_empty():
					SceneUtils.fatal_error_and_quit("RIG-002 runner: unreadable or blank zero counterpart: %s" % path)
					return false
				var bounds := _sampled_channel_bounds(image)
				if int(bounds["maximum"]) - int(bounds["minimum"]) <= NON_BLANK_MINIMUM_CHANNEL_RANGE:
					SceneUtils.fatal_error_and_quit("RIG-002 runner: unreadable or blank zero counterpart: %s" % path)
					return false
	return true


func _safe_capture_folder(folder: String) -> bool:
	var current := ProjectSettings.globalize_path("res://").path_join(_output_dir)
	for component in folder.trim_prefix(current + "/").split("/"):
		var directory := DirAccess.open(current)
		if directory == null or directory.is_link(component):
			SceneUtils.fatal_error_and_quit("RIG-002 runner: unsafe capture folder %s" % folder)
			return false
		current = current.path_join(component)
		if not directory.dir_exists(component) and directory.make_dir(component) != OK:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: cannot create capture folder %s" % current)
			return false
	return true


func _option(name: String) -> String:
	var arguments := OS.get_cmdline_user_args()
	for index in arguments.size():
		if arguments[index] == name and index + 1 < arguments.size():
			return arguments[index + 1]
		if arguments[index].begins_with(name + "="):
			return arguments[index].trim_prefix(name + "=")
	return ""


func _has_output_dir_argument(arguments: PackedStringArray) -> bool:
	for argument in arguments:
		if argument == "--output-dir" or argument.begins_with("--output-dir="):
			return true
	return false


func _safe_runs_root(create: bool) -> String:
	var current := ProjectSettings.globalize_path("res://").trim_suffix("/")
	for component in ["temp", "RIG-002", "runs"]:
		var directory := DirAccess.open(current)
		if directory == null or directory.is_link(component):
			SceneUtils.fatal_error_and_quit("RIG-002 runner: linked or inaccessible runs ancestor: %s/%s" % [current, component])
			return ""
		if not directory.dir_exists(component):
			if not create or directory.make_dir(component) != OK:
				SceneUtils.fatal_error_and_quit("RIG-002 runner: missing or inaccessible runs ancestor: %s/%s" % [current, component])
				return ""
		current = current.path_join(component)
	return current


func _prepare_run() -> bool:
	_run_id = _option("--run-id")
	var mode := _option("--run-mode")
	if _run_id.is_empty() or _run_id.contains("..") or not _run_id.replace("-", "_").is_valid_ascii_identifier() or mode not in ["create", "join"]:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: supply a safe single-component --run-id and explicit --run-mode create|join")
		return false
	var runs_path := _safe_runs_root(mode == "create")
	if runs_path.is_empty():
		return false
	var parent := DirAccess.open(runs_path)
	if parent == null or parent.is_link(_run_id):
		SceneUtils.fatal_error_and_quit("RIG-002 runner: absent or unsafe runs directory")
		return false
	var run_path := runs_path.path_join(_run_id)
	var identity_path := run_path.path_join(IDENTITY_FILE)
	if mode == "create":
		if parent.make_dir(_run_id) != OK:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: run already exists or cannot be created: %s" % _run_id)
			return false
		var identity := FileAccess.open(identity_path, FileAccess.WRITE)
		if identity == null:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: cannot record run identity")
			return false
		identity.store_string("RIG-002 forearm twist run v1\n" + _run_id + "\n")
		identity.flush()
	else:
		var joined_directory := DirAccess.open(run_path)
		if joined_directory == null or joined_directory.is_link(IDENTITY_FILE) or not joined_directory.file_exists(IDENTITY_FILE) or FileAccess.get_file_as_string(identity_path) != "RIG-002 forearm twist run v1\n" + _run_id + "\n":
			SceneUtils.fatal_error_and_quit("RIG-002 runner: missing or mismatched run identity: %s" % _run_id)
			return false
	_output_dir = RUNS_DIR.path_join(_run_id).path_join("forearm_twist")
	var run_directory := DirAccess.open(run_path)
	if run_directory == null or run_directory.is_link("forearm_twist"):
		SceneUtils.fatal_error_and_quit("RIG-002 runner: unsafe capture subtree")
		return false
	if not run_directory.dir_exists("forearm_twist") and run_directory.make_dir("forearm_twist") != OK:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: cannot initialise capture subtree")
		return false
	return true


func _weight_label_for(value: String) -> String:
	# Do not round a float into a filename: canonical decimal text keeps distinct inputs distinct.
	var parts := value.split(".")
	if parts.size() > 2 or parts[0] not in ["0", "1"] or (parts.size() == 2 and (parts[1].is_empty() or parts[1].length() > 6 or not parts[1].is_valid_int() or parts[1].begins_with("-") or parts[1].begins_with("+"))):
		SceneUtils.fatal_error_and_quit("RIG-002 runner: ambiguous or unsupported decimal weight label: %s" % value)
		return ""
	var fraction := parts[1].rstrip("0") if parts.size() == 2 else ""
	if parts[0] == "1" and not fraction.is_empty():
		SceneUtils.fatal_error_and_quit("RIG-002 runner: weight exceeds one: %s" % value)
		return ""
	if parts[0] == "1":
		return "1000"
	return "0" + fraction.rpad(2, "0") if not fraction.is_empty() else "000"


func _weight_argument() -> String:
	return _option("--weight")


func _requested_selected_weight() -> float:
	var arguments := OS.get_cmdline_user_args()
	for index in arguments.size():
		if arguments[index] == "--selected-weight" and index + 1 < arguments.size():
			return _validate_candidate_weight(arguments[index + 1])
		if arguments[index].begins_with("--selected-weight="):
			return _validate_candidate_weight(arguments[index].trim_prefix("--selected-weight="))
	return 0.25


func _bend_radians_for_scenario() -> float:
	# The flexion axis mirrors per side, so the same signs apply to both sides: positive bends the
	# fingertips toward the palm face (palmar flexion) and negative bends them toward the dorsum
	# (dorsal extension) on Left and Right alike (verified at runtime by _assert_bend_sign_convention).
	if _scenario_name == SCENARIO_REST_HAND_FLEXION_60:
		return deg_to_rad(BEND_ANGLE_DEGREES)
	return deg_to_rad(-BEND_ANGLE_DEGREES)


func _subject_forward(skeleton: Skeleton3D) -> Vector3:
	# The subject's forward direction, derived from the actual rest skeleton geometry (the Hips->Head up
	# axis crossed with the LeftUpperArm->RightUpperArm shoulder line) instead of an assumed world axis.
	# The authored photobooth places FrontCamera on the world -Z side looking back at the subject, and the
	# derived forward conjugated by the skeleton node basis must agree with that scene convention.
	var hips := _require_bone(skeleton, "Hips")
	var head := _require_bone(skeleton, "Head")
	var left_upper_arm := _require_bone(skeleton, "LeftUpperArm")
	var right_upper_arm := _require_bone(skeleton, "RightUpperArm")
	var up := (skeleton.get_bone_global_rest(head).origin - skeleton.get_bone_global_rest(hips).origin).normalized()
	var right := (skeleton.get_bone_global_rest(right_upper_arm).origin - skeleton.get_bone_global_rest(left_upper_arm).origin).normalized()
	var forward := up.cross(right).normalized()
	if absf(forward.dot(up)) > SUBJECT_FORWARD_MAXIMUM_UP_TILT:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: derived subject forward is tilted into the rest up axis (forward=%s up=%s dot=%.9f)" % [
			forward,
			up,
			forward.dot(up),
		])
		return Vector3.ZERO
	var world_forward := (skeleton.global_basis * forward).normalized()
	if world_forward.dot(Vector3.FORWARD) < SUBJECT_FORWARD_WORLD_ANCHOR_MINIMUM_DOT:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: derived subject forward %s disagrees with the scene forward convention (world %s, dot=%.9f)" % [
			world_forward,
			Vector3.FORWARD,
			world_forward.dot(Vector3.FORWARD),
		])
		return Vector3.ZERO
	return forward


func _bend_frame_from_rest(skeleton: Skeleton3D, side_name: String) -> Dictionary:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var middle_distal := _require_bone(skeleton, "%sMiddleDistal" % side_name)
	var thumb_proximal := _require_bone(skeleton, "%sThumbProximal" % side_name)
	var little_distal := _require_bone(skeleton, "%sLittleDistal" % side_name)
	var lower_rest := skeleton.get_bone_global_rest(lower_arm)
	var hand_rest := skeleton.get_bone_global_rest(hand)
	var longitudinal_world := (hand_rest.origin - lower_rest.origin).normalized()
	var forward_world := _subject_forward(skeleton)
	if forward_world == Vector3.ZERO:
		return {}
	var palm_target := forward_world - longitudinal_world * forward_world.dot(longitudinal_world)
	if palm_target.length() < PALM_TARGET_MINIMUM_LENGTH:
		SceneUtils.fatal_error_and_quit(
			"RIG-002 runner: %s forearm longitudinal axis is parallel to the subject forward direction, so the palm-forward target is undefined (length=%.9f)" % [
				side_name,
				palm_target.length(),
			])
		return {}
	palm_target = palm_target.normalized()
	# The palm normal is the hand-rest +Z axis. This is verified against independent rest geometry rather
	# than assumed: the cross products of the wrist->middle-finger direction with the wrist->thumb and
	# wrist->little-finger directions identify the same palm-facing half-space on each mirrored side.
	# (The previous session projected hand-rest +X as the palm facing; this check rejects that axis.)
	var palm_rest := hand_rest.basis.z.normalized()
	var fingers := (skeleton.get_bone_global_rest(middle_distal).origin - hand_rest.origin).normalized()
	var thumb := skeleton.get_bone_global_rest(thumb_proximal).origin - hand_rest.origin
	thumb = (thumb - longitudinal_world * thumb.dot(longitudinal_world)).normalized()
	var little := skeleton.get_bone_global_rest(little_distal).origin - hand_rest.origin
	little = (little - longitudinal_world * little.dot(longitudinal_world)).normalized()
	var side_sign := 1.0 if side_name == "Left" else -1.0
	var palm_from_thumb := (fingers.cross(thumb).normalized()) * side_sign
	var palm_from_little := (little.cross(fingers).normalized()) * side_sign
	var palm_thumb_alignment := palm_from_thumb.dot(palm_rest)
	var palm_little_alignment := palm_from_little.dot(palm_rest)
	if palm_thumb_alignment < PALM_GEOMETRY_MINIMUM_ALIGNMENT or palm_little_alignment < PALM_GEOMETRY_MINIMUM_ALIGNMENT:
		SceneUtils.fatal_error_and_quit(
			"RIG-002 runner: %s hand-rest +Z is not the palm normal according to rest finger geometry (thumb_alignment=%.9f little_alignment=%.9f)" % [
				side_name,
				palm_thumb_alignment,
				palm_little_alignment,
			])
		return {}
	var palm_perpendicular := palm_rest - longitudinal_world * palm_rest.dot(longitudinal_world)
	if palm_perpendicular.length() < PALM_TARGET_MINIMUM_LENGTH:
		SceneUtils.fatal_error_and_quit(
			"RIG-002 runner: %s palm normal is parallel to the forearm longitudinal axis, so the pronation angle is undefined (length=%.9f)" % [
				side_name,
				palm_perpendicular.length(),
			])
		return {}
	palm_perpendicular = palm_perpendicular.normalized()
	# Signed pronation about the forearm longitudinal axis (right-hand rule) that rotates the palm normal
	# onto the forward target; atan2 yields the nearest rotation with magnitude at most PI.
	var pronation := atan2(
		longitudinal_world.dot(palm_perpendicular.cross(palm_target)),
		palm_perpendicular.dot(palm_target))
	# Anatomical flexion axis: perpendicular to both the forearm and the palm-forward target, so a positive
	# bend rotates the fingertips toward the palm face on both mirrored sides.
	var flexion_axis_world := longitudinal_world.cross(palm_target).normalized()
	var longitudinal_lower := (lower_rest.basis.inverse() * longitudinal_world).normalized()
	var flexion_axis_lower := (lower_rest.basis.inverse() * flexion_axis_world).normalized()
	return {
		"lower_arm": lower_arm,
		"hand": hand,
		"lower_rest": lower_rest,
		"hand_rest": hand_rest,
		"longitudinal": longitudinal_world,
		"longitudinal_lower": longitudinal_lower,
		"flexion_axis_world": flexion_axis_world,
		"flexion_axis_lower": flexion_axis_lower,
		"palm_target": palm_target,
		"palm_rest": palm_rest,
		"pronation": pronation,
		"palm_thumb_alignment": palm_thumb_alignment,
		"palm_little_alignment": palm_little_alignment,
		"forward": forward_world,
	}


func _apply_rest_relative_hand_bend(skeleton: Skeleton3D, side_name: String) -> void:
	var flexion_radians := _bend_radians_for_scenario()
	var frame := _bend_frame_from_rest(skeleton, side_name)
	if frame.is_empty():
		return
	var lower_rest: Transform3D = frame["lower_rest"]
	var hand_rest: Transform3D = frame["hand_rest"]
	var longitudinal_lower: Vector3 = frame["longitudinal_lower"]
	var flexion_axis_lower: Vector3 = frame["flexion_axis_lower"]
	var palm_target: Vector3 = frame["palm_target"]
	var pronation: float = frame["pronation"]
	var hand_relative_rest := lower_rest.affine_inverse() * hand_rest
	var pronation_basis := Basis(longitudinal_lower, pronation)
	var flexion_basis := Basis(flexion_axis_lower, flexion_radians)
	# Stage 1, palm-forward pre-rotation: rest-relative axial pronation with the hand origin fixed. The
	# pose is set through the engine and read back so the palm-forward gate observes the actual pose.
	skeleton.set_bone_global_pose(int(frame["hand"]), lower_rest * Transform3D(
		pronation_basis * hand_relative_rest.basis,
		hand_relative_rest.origin))
	var palm_after_pronation := skeleton.get_bone_global_pose(int(frame["hand"])).basis.z.normalized()
	var palm_forward_dot := palm_after_pronation.dot(palm_target)
	if palm_forward_dot < PALM_FORWARD_MINIMUM_DOT:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s pronation did not bring the %s palm normal to face the subject forward direction (dot=%.9f)" % [
			_scenario_name,
			side_name,
			palm_forward_dot,
		])
		return
	# Stage 2, anatomical bend: flexion composed after the pronation, still in the lower-arm rest frame,
	# with the hand origin still fixed.
	skeleton.set_bone_global_pose(int(frame["hand"]), lower_rest * Transform3D(
		flexion_basis * pronation_basis * hand_relative_rest.basis,
		hand_relative_rest.origin))
	var hand_pose := skeleton.get_bone_global_pose(int(frame["hand"]))
	if hand_pose.origin.distance_to(hand_rest.origin) > REST_TOLERANCE_METRES:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s bend moved the %s wrist origin beyond the rest tolerance (%.9f m)" % [
			_scenario_name,
			side_name,
			hand_pose.origin.distance_to(hand_rest.origin),
		])
		return
	var palm_bend_angle := acos(clampf(hand_pose.basis.z.normalized().dot(palm_target), -1.0, 1.0))
	if absf(palm_bend_angle - absf(flexion_radians)) > BEND_COMPONENT_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s final palm normal is not rotated by the commanded bend from the forward target (angle=%.9f expected=%.9f)" % [
			_scenario_name,
			side_name,
			palm_bend_angle,
			absf(flexion_radians),
		])
		return
	_assert_bend_sign_convention(skeleton, side_name, hand_rest, frame, flexion_radians)
	print("RIG002_BEND_FRAME scenario=%s side=%s forward=%s palm_axis=hand_rest_positive_z palm_target=%s palm_thumb_alignment=%.9f palm_little_alignment=%.9f pronation_radians=%.9f flexion_axis=%s" % [
		_scenario_name,
		side_name,
		frame["forward"],
		palm_target,
		frame["palm_thumb_alignment"],
		frame["palm_little_alignment"],
		pronation,
		frame["flexion_axis_world"],
	])
	print("RIG002_PALM_FORWARD scenario=%s side=%s palm_dot=%.9f minimum_dot=%.9f" % [
		_scenario_name,
		side_name,
		palm_forward_dot,
		PALM_FORWARD_MINIMUM_DOT,
	])
	print("RIG002_BEND_COMMAND scenario=%s side=%s coordinate=lower_arm_rest_relative pronation_axis=%s pronation_radians=%.9f flexion_axis=%s flexion_radians=%.9f same_sign_both_sides=true" % [
		_scenario_name,
		side_name,
		longitudinal_lower,
		pronation,
		flexion_axis_lower,
		flexion_radians,
	])


func _assert_bend_sign_convention(
		skeleton: Skeleton3D,
		side_name: String,
		hand_rest: Transform3D,
		frame: Dictionary,
		flexion_radians: float) -> void:
	var middle_distal := _require_bone(skeleton, "%sMiddleDistal" % side_name)
	var longitudinal_world: Vector3 = frame["longitudinal"]
	var flexion_axis_world: Vector3 = frame["flexion_axis_world"]
	var palm_target: Vector3 = frame["palm_target"]
	var pronation: float = frame["pronation"]
	var tip_rest := (skeleton.get_bone_global_rest(middle_distal).origin - hand_rest.origin).normalized()
	var tip_after_pronation := (Basis(longitudinal_world, pronation) * tip_rest).normalized()
	var tip_after_bend := (Basis(flexion_axis_world, flexion_radians) * tip_after_pronation).normalized()
	var palm_alignment_delta := tip_after_bend.dot(palm_target) - tip_after_pronation.dot(palm_target)
	if flexion_radians > 0.0 and palm_alignment_delta < BEND_SIGN_CONVENTION_MINIMUM_ALIGNMENT_DELTA:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s flexion did not rotate the wrist-to-middle-finger direction toward the palm-facing forward target (delta=%.9f); the derived frame violates the anatomical contract" % [
			_scenario_name,
			side_name,
			palm_alignment_delta,
		])
		return
	if flexion_radians < 0.0 and palm_alignment_delta > -BEND_SIGN_CONVENTION_MINIMUM_ALIGNMENT_DELTA:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s extension did not rotate the wrist-to-middle-finger direction away from the palm-facing forward target (delta=%.9f); the derived frame violates the anatomical contract" % [
			_scenario_name,
			side_name,
			palm_alignment_delta,
		])
		return
	print("RIG002_BEND_SIGN_CONVENTION scenario=%s side=%s palm_target_alignment_before=%.9f palm_target_alignment_after=%.9f delta=%.9f flexion_toward_palm=%s" % [
		_scenario_name,
		side_name,
		tip_after_pronation.dot(palm_target),
		tip_after_bend.dot(palm_target),
		palm_alignment_delta,
		"true" if flexion_radians > 0.0 else "false",
	])


func _assert_no_ik_and_bend_pose(mesh_name: String, skeleton: Skeleton3D, side_name: String) -> void:
	var flexion_radians := _bend_radians_for_scenario()
	var frame := _bend_frame_from_rest(skeleton, side_name)
	if frame.is_empty():
		return
	var lower_arm := int(frame["lower_arm"])
	var hand := int(frame["hand"])
	var helper := _require_bone(skeleton, "%sForearmTwist" % side_name)
	var pronation: float = frame["pronation"]
	var longitudinal_lower: Vector3 = frame["longitudinal_lower"]
	var lower_before := skeleton.get_bone_global_pose(lower_arm).basis.get_rotation_quaternion()
	var hand_before := skeleton.get_bone_global_pose(hand)
	var helper_rest_rotation := skeleton.get_bone_rest(helper).basis.get_rotation_quaternion()
	var lower_rest: Transform3D = frame["lower_rest"]
	var hand_relative_rest := lower_rest.affine_inverse() * skeleton.get_bone_global_rest(hand)
	var hand_relative_pose := skeleton.get_bone_global_pose(lower_arm).affine_inverse() * skeleton.get_bone_global_pose(hand)
	# Un-conjugated hand delta in the lower-arm rest frame (pose * rest^-1), matching the decomposition
	# space the ForearmTwistModifier itself uses. The read-back quaternion may sit on either double-cover
	# branch, so canonicalise it before extracting the principal twist.
	var hand_delta := _canonicalise_double_cover(
		(hand_relative_pose.basis.get_rotation_quaternion()
			* hand_relative_rest.basis.get_rotation_quaternion().inverse()).normalized())
	var extracted_twist := _signed_principal_twist(hand_delta, longitudinal_lower)
	if absf(extracted_twist - pronation) > PRONATION_RECOVERY_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s principal twist did not recover the commanded pronation (extracted=%.9f commanded=%.9f)" % [
			mesh_name,
			side_name,
			extracted_twist,
			pronation,
		])
		return
	var modifier := skeleton.get_node("ForearmTwistModifier") as SkeletonModifier3D
	modifier.set("TwistWeight", _twist_weight)
	modifier.call("_ProcessModificationWithDelta", 0.0)
	var lower_error := lower_before.angle_to(skeleton.get_bone_global_pose(lower_arm).basis.get_rotation_quaternion())
	var hand_after := skeleton.get_bone_global_pose(hand)
	var hand_error := hand_before.basis.get_rotation_quaternion().angle_to(hand_after.basis.get_rotation_quaternion())
	var hand_origin_error := hand_before.origin.distance_to(hand_after.origin)
	if lower_error > REST_TOLERANCE_RADIANS or hand_error > REST_TOLERANCE_RADIANS \
			or hand_origin_error > REST_TOLERANCE_METRES:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: ForearmTwistModifier mutated canonical %s bones under bend (lower_error=%.6f hand_error=%.6f hand_origin_error=%.9f)" % [side_name, lower_error, hand_error, hand_origin_error])
		return
	# The pronation stage is a real axial twist, so the helper must respond with weight x pronation instead
	# of staying invariant.
	var helper_delta := helper_rest_rotation.angle_to(skeleton.get_bone_pose_rotation(helper))
	var expected_helper := absf(_twist_weight * pronation)
	if _twist_weight > 0.0 and absf(helper_delta - expected_helper) > HELPER_RESPONSE_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s helper rotation did not track weight x pronation (delta=%.9f expected=%.9f)" % [
			mesh_name,
			side_name,
			helper_delta,
			expected_helper,
		])
		return
	if is_zero_approx(_twist_weight) and helper_delta > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: weight_000 unexpectedly changed %s %s helper (delta=%.9f)" % [
			mesh_name,
			side_name,
			helper_delta,
		])
		return
	var world_axis: Vector3 = frame["longitudinal"]
	var helper_rest := skeleton.get_bone_global_rest(helper)
	var helper_pose := skeleton.get_bone_global_pose(helper)
	var helper_global_delta := helper_pose.basis.get_rotation_quaternion() * helper_rest.basis.get_rotation_quaternion().inverse()
	var helper_axis := helper_global_delta.get_axis()
	var helper_angle := helper_global_delta.get_angle()
	var axial_alignment := absf(helper_axis.dot(world_axis)) if helper_angle > REST_TOLERANCE_RADIANS else 1.0
	if _twist_weight > 0.0 and axial_alignment < AXIAL_ALIGNMENT_TOLERANCE:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s helper rotation was not axial (alignment=%.6f)" % [mesh_name, side_name, axial_alignment])
		return
	print("RIG002_BEND_EVIDENCE scenario=%s mesh=%s side=%s rest=true ik_disabled=true pronation=%.9f extracted_twist=%.9f flexion=%.9f weight=%.2f helper_delta=%.9f expected_helper=%.9f helper_global_axis=%s helper_global_angle=%.9f helper_axis_alignment=%.9f" % [
		_scenario_name,
		mesh_name,
		side_name,
		pronation,
		extracted_twist,
		flexion_radians,
		_twist_weight,
		helper_delta,
		expected_helper,
		helper_axis,
		helper_angle,
		axial_alignment,
	])


func _assert_settled_rest_hand_bend(mesh_name: String, skeleton: Skeleton3D, side_name: String) -> void:
	var flexion_radians := _bend_radians_for_scenario()
	var frame := _bend_frame_from_rest(skeleton, side_name)
	if frame.is_empty():
		return
	var lower_arm := int(frame["lower_arm"])
	var hand := int(frame["hand"])
	var helper := _require_bone(skeleton, "%sForearmTwist" % side_name)
	var pronation: float = frame["pronation"]
	var longitudinal_world: Vector3 = frame["longitudinal"]
	var palm_target: Vector3 = frame["palm_target"]
	var lower_rest: Transform3D = frame["lower_rest"]
	var hand_rest: Transform3D = frame["hand_rest"]
	var lower_pose := skeleton.get_bone_global_pose(lower_arm)
	if lower_pose.origin.distance_to(lower_rest.origin) > REST_TOLERANCE_METRES \
			or lower_pose.basis.get_rotation_quaternion().angle_to(lower_rest.basis.get_rotation_quaternion()) > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: animation changed %s %s lower arm after the rest-hand bend" % [mesh_name, side_name])
		return
	var hand_pose := skeleton.get_bone_global_pose(hand)
	if hand_pose.origin.distance_to(hand_rest.origin) > REST_TOLERANCE_METRES:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: animation moved %s %s hand origin after the %.0f-degree bend (drift=%.9f m)" % [
			mesh_name,
			side_name,
			absf(rad_to_deg(flexion_radians)),
			hand_pose.origin.distance_to(hand_rest.origin),
		])
		return
	# The lower arm is still at its rest pose, so the skeleton-global hand delta conjugates the
	# lower-arm-rest-frame delta; the twist about the matching world longitudinal axis is identical. The
	# canonicalisation keeps the extraction off the negative double-cover branch.
	var settled_world_delta := _canonicalise_double_cover(
		(hand_pose.basis.get_rotation_quaternion() * hand_rest.basis.get_rotation_quaternion().inverse()).normalized())
	var settled_twist := _signed_principal_twist(settled_world_delta, longitudinal_world)
	if absf(settled_twist - pronation) > PRONATION_RECOVERY_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: settled %s %s twist drifted from the commanded pronation (extracted=%.9f commanded=%.9f)" % [
			mesh_name,
			side_name,
			settled_twist,
			pronation,
		])
		return
	var settled_palm_angle := acos(clampf(hand_pose.basis.z.normalized().dot(palm_target), -1.0, 1.0))
	if absf(settled_palm_angle - absf(flexion_radians)) > BEND_COMPONENT_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: settled %s %s palm normal is not rotated by the commanded bend from the forward target (angle=%.9f expected=%.9f)" % [
			mesh_name,
			side_name,
			settled_palm_angle,
			absf(flexion_radians),
		])
		return
	var helper_delta := skeleton.get_bone_rest(helper).basis.get_rotation_quaternion().angle_to(skeleton.get_bone_pose_rotation(helper))
	var expected_helper := absf(_twist_weight * pronation)
	if _twist_weight > 0.0 and absf(helper_delta - expected_helper) > HELPER_RESPONSE_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: settled %s %s helper rotation did not track weight x pronation (delta=%.9f expected=%.9f)" % [
			mesh_name,
			side_name,
			helper_delta,
			expected_helper,
		])
		return
	if is_zero_approx(_twist_weight) and helper_delta > REST_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: settled weight_000 unexpectedly changed %s %s helper (delta=%.9f)" % [
			mesh_name,
			side_name,
			helper_delta,
		])
		return
	print("RIG002_BEND_SETTLED scenario=%s mesh=%s side=%s lower_arm_at_rest=true pronation=%.9f settled_twist=%.9f flexion=%.9f settled_palm_angle=%.9f helper_delta=%.9f expected_helper=%.9f" % [
		_scenario_name,
		mesh_name,
		side_name,
		pronation,
		settled_twist,
		flexion_radians,
		settled_palm_angle,
		helper_delta,
		expected_helper,
	])




func _assert_bend_pixel_evidence(mesh_name: String, side_name: String, view_name: String) -> void:
	# Pixel-level oracle for a bend capture is non-blank only: bend-role captures verify helper
	# axial-only behaviour and hand authority, not cosmetic bend quality or cross-weight contrast
	# (RIG-002 TR13). The axial contrast guard lives in the twist diagnostic scenarios.
	var relative_path := _capture_path(mesh_name, side_name, view_name)
	var image := _load_artefact_image(relative_path)
	if image == null:
		return
	var channel_bounds := _sampled_channel_bounds(image)
	var channel_range: int = int(channel_bounds["maximum"]) - int(channel_bounds["minimum"])
	if channel_range <= NON_BLANK_MINIMUM_CHANNEL_RANGE:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: %s %s %s %s capture is blank (sampled channel range=%d minimum=%d)" % [
			_scenario_name,
			mesh_name,
			side_name,
			view_name,
			channel_range,
			NON_BLANK_MINIMUM_CHANNEL_RANGE,
		])
		return
	print("RIG002_PIXEL_EVIDENCE scenario=%s mesh=%s side=%s view=%s non_blank=true sampled_channel_range=%d stride=%d" % [
		_scenario_name,
		mesh_name,
		side_name,
		view_name,
		channel_range,
		PIXEL_SAMPLE_STRIDE,
	])


func _load_artefact_image(relative_path: String) -> Image:
	var absolute_path := _absolute_artefact_path(relative_path)
	if not FileAccess.file_exists(absolute_path):
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing expected capture artefact %s" % absolute_path)
		return null
	var image := Image.load_from_file(absolute_path)
	if image == null or image.is_empty():
		SceneUtils.fatal_error_and_quit("RIG-002 runner: failed to load capture artefact %s" % absolute_path)
		return null
	return image


func _absolute_artefact_path(relative_path: String) -> String:
	return ProjectSettings.globalize_path("res://").path_join(_output_dir).path_join(relative_path)


func _sampled_channel_bounds(image: Image) -> Dictionary:
	var data := image.get_data()
	var width := image.get_width()
	var height := image.get_height()
	@warning_ignore("integer_division")
	var bytes_per_pixel := data.size() / maxi(1, width * height)
	var minimum := 255
	var maximum := 0
	for y: int in range(0, height, PIXEL_SAMPLE_STRIDE):
		for x: int in range(0, width, PIXEL_SAMPLE_STRIDE):
			var base := (y * width + x) * bytes_per_pixel
			for channel in bytes_per_pixel:
				var value := data[base + channel]
				minimum = mini(minimum, value)
				maximum = maxi(maximum, value)
	return {"minimum": minimum, "maximum": maximum}


func _sampled_differing_pixel_fraction(image: Image, companion: Image) -> float:
	var data := image.get_data()
	var companion_data := companion.get_data()
	var width := image.get_width()
	var height := image.get_height()
	@warning_ignore("integer_division")
	var bytes_per_pixel := data.size() / maxi(1, width * height)
	var sampled := 0
	var differing := 0
	for y: int in range(0, height, PIXEL_SAMPLE_STRIDE):
		for x: int in range(0, width, PIXEL_SAMPLE_STRIDE):
			var base := (y * width + x) * bytes_per_pixel
			var largest_channel_delta := 0
			for channel in bytes_per_pixel:
				largest_channel_delta = maxi(largest_channel_delta, absi(data[base + channel] - companion_data[base + channel]))
			sampled += 1
			if largest_channel_delta >= PIXEL_DIFF_CHANNEL_SENSITIVITY:
				differing += 1
	return float(differing) / float(maxi(1, sampled))


func _signed_principal_twist(rotation: Quaternion, axis: Vector3) -> float:
	# Mirrors AlleyCat.Rigging.ForearmTwistMath.TryExtractSignedPrincipalTwist: the deterministic signed
	# principal twist around `axis`, canonicalised over the quaternion double cover.
	var normalised_rotation := _canonicalise_double_cover(rotation.normalized())
	var normalised_axis := axis.normalized()
	var signed_sin_half_angle := Vector3(normalised_rotation.x, normalised_rotation.y, normalised_rotation.z).dot(normalised_axis)
	var twist := Quaternion(
		normalised_axis.x * signed_sin_half_angle,
		normalised_axis.y * signed_sin_half_angle,
		normalised_axis.z * signed_sin_half_angle,
		normalised_rotation.w).normalized()
	twist = _canonicalise_double_cover(twist)
	var twist_vector_length := Vector3(twist.x, twist.y, twist.z).length()
	var angle := 2.0 * atan2(twist_vector_length, twist.w)
	if signed_sin_half_angle < 0.0:
		angle = -angle
	return angle


func _canonicalise_double_cover(rotation: Quaternion) -> Quaternion:
	if rotation.w < -TWIST_CANONICALISATION_TOLERANCE:
		return Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w)
	if absf(rotation.w) > TWIST_CANONICALISATION_TOLERANCE:
		return rotation
	var x := absf(rotation.x)
	var y := absf(rotation.y)
	var z := absf(rotation.z)
	var selected := rotation.x if x >= y and x >= z else (rotation.y if y >= z else rotation.z)
	return rotation if selected >= 0.0 else Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w)


func _focus_top_down_camera(skeleton: Skeleton3D, side_name: String) -> void:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var lower_position := _bone_world_position(skeleton, lower_arm)
	var wrist_position := _bone_world_position(skeleton, hand)
	var framing_points := _rest_hand_roll_framing_points(skeleton, side_name, lower_position, wrist_position)
	var largest_wrist_plane_distance := 0.0
	for point: Vector3 in framing_points:
		var wrist_plane_offset := point - wrist_position
		wrist_plane_offset.y = 0.0
		largest_wrist_plane_distance = maxf(largest_wrist_plane_distance, wrist_plane_offset.length())
	var orthogonal_scale := maxf(
		CAMERA_MINIMUM_ORTHOGONAL_SCALE,
		largest_wrist_plane_distance * 2.0 * CAMERA_FRAMING_PADDING)
	_capture_camera.global_position = wrist_position + Vector3.UP * 0.85
	_capture_camera.look_at(wrist_position, Vector3.FORWARD)
	_capture_camera.size = orthogonal_scale
	_capture_camera.make_current()
	print("RIG002_CAMERA scenario=%s side=%s convention=view_from_above target=actual_wrist=%s distal_forearm_and_palm_extent=%.4f orthogonal_scale=%.4f image_size=%s" % [
		_scenario_name,
		side_name,
		wrist_position,
		largest_wrist_plane_distance,
		_capture_camera.size,
		CAMERA_IMAGE_SIZE,
	])


func _assert_top_camera_framing(skeleton: Skeleton3D, side_name: String) -> void:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var lower_position := _bone_world_position(skeleton, lower_arm)
	var wrist_position := _bone_world_position(skeleton, hand)
	var wrist_camera_local := _capture_camera.to_local(wrist_position)
	if maxf(absf(wrist_camera_local.x), absf(wrist_camera_local.y)) > 0.001:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: top camera did not centre the actual %s wrist crease (%s)" % [side_name, wrist_camera_local])
		return
	var safe_half_frame := _capture_camera.size * 0.5 * 0.93
	for point: Vector3 in _rest_hand_roll_framing_points(skeleton, side_name, lower_position, wrist_position):
		var camera_local := _capture_camera.to_local(point)
		if absf(camera_local.x) > safe_half_frame or absf(camera_local.y) > safe_half_frame:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: top camera did not contain the complete %s palm/distal forearm (%s)" % [side_name, camera_local])
			return
	print("RIG002_FRAMING scenario=%s side=%s camera_setup=actual_wrist_centred complete_palm_and_distal_third=within_frustum image_review_required=true" % [
		_scenario_name,
		side_name,
	])


func _rest_hand_roll_framing_points(
		skeleton: Skeleton3D,
		side_name: String,
		lower_position: Vector3,
		wrist_position: Vector3) -> Array[Vector3]:
	var points: Array[Vector3] = [lower_position.lerp(wrist_position, 0.66), wrist_position]
	for digit_name: String in ["Thumb", "Index", "Middle", "Ring", "Little"]:
		var distal_position := _bone_world_position(skeleton, _require_bone(skeleton, "%s%sDistal" % [side_name, digit_name]))
		var fingertip_direction := (distal_position - wrist_position).normalized()
		points.append(distal_position + (fingertip_direction * FINGERTIP_EXTENSION_METRES))
	return points


func _focus_profile_camera(skeleton: Skeleton3D, side_name: String) -> void:
	var frame := _bend_frame_from_rest(skeleton, side_name)
	if frame.is_empty():
		return
	# With the palm pre-rotated to face forward, the anatomical bend lives in the plane spanned by the
	# forearm longitudinal axis and the subject's forward direction; the unchanged top-down baseline
	# already frames that bend face-on. This supplemental view is horizontal, from the palm-facing side
	# (the forward target direction conjugated into world space), so it frames the wrist crease and the
	# palm-forward orientation from the front while the top-down baseline shows the bend arc.
	var palm_target: Vector3 = frame["palm_target"]
	var camera_direction := (skeleton.global_basis * palm_target).normalized()
	var lower_position := _bone_world_position(skeleton, int(frame["lower_arm"]))
	var wrist_position := _bone_world_position(skeleton, int(frame["hand"]))
	var framing_points := _rest_hand_roll_framing_points(skeleton, side_name, lower_position, wrist_position)
	var largest_image_plane_distance := 0.0
	for point: Vector3 in framing_points:
		var image_plane_offset := point - wrist_position
		image_plane_offset -= camera_direction * image_plane_offset.dot(camera_direction)
		largest_image_plane_distance = maxf(largest_image_plane_distance, image_plane_offset.length())
	var orthogonal_scale := maxf(
		CAMERA_MINIMUM_ORTHOGONAL_SCALE,
		largest_image_plane_distance * 2.0 * CAMERA_FRAMING_PADDING)
	_profile_camera.global_position = wrist_position + camera_direction * 0.85
	_profile_camera.look_at(wrist_position, Vector3.UP)
	_profile_camera.size = orthogonal_scale
	_profile_camera.make_current()
	print("RIG002_CAMERA scenario=%s side=%s convention=profile_palm_facing_side_horizontal target=actual_wrist=%s distal_forearm_and_palm_extent=%.4f orthogonal_scale=%.4f image_size=%s" % [
		_scenario_name,
		side_name,
		wrist_position,
		largest_image_plane_distance,
		_profile_camera.size,
		CAMERA_IMAGE_SIZE,
	])


func _assert_profile_camera_framing(skeleton: Skeleton3D, side_name: String) -> void:
	var lower_arm := _require_bone(skeleton, "%sLowerArm" % side_name)
	var hand := _require_bone(skeleton, "%sHand" % side_name)
	var lower_position := _bone_world_position(skeleton, lower_arm)
	var wrist_position := _bone_world_position(skeleton, hand)
	var wrist_camera_local := _profile_camera.to_local(wrist_position)
	if maxf(absf(wrist_camera_local.x), absf(wrist_camera_local.y)) > 0.001:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: profile camera did not centre the actual %s wrist crease (%s)" % [side_name, wrist_camera_local])
		return
	var safe_half_frame := _profile_camera.size * 0.5 * 0.93
	for point: Vector3 in _rest_hand_roll_framing_points(skeleton, side_name, lower_position, wrist_position):
		var camera_local := _profile_camera.to_local(point)
		if absf(camera_local.x) > safe_half_frame or absf(camera_local.y) > safe_half_frame:
			SceneUtils.fatal_error_and_quit("RIG-002 runner: profile camera did not contain the complete %s palm/distal forearm (%s)" % [side_name, camera_local])
			return
	print("RIG002_FRAMING scenario=%s side=%s camera_setup=profile_wrist_centred complete_palm_and_distal_third=within_frustum image_review_required=true" % [
		_scenario_name,
		side_name,
	])


func _capture_path(mesh_name: String, side_name: String, view_name: String = "top_down") -> String:
	return _capture_path_for_weight(mesh_name, side_name, view_name, _weight_label)


func _capture_path_for_weight(mesh_name: String, side_name: String, view_name: String, weight_label: String) -> String:
	return "%s/%s/%s/%s_%s.jpg" % [
		_scenario_name,
		mesh_name.to_lower(),
		side_name.to_lower(),
		view_name,
		weight_label,
	]


func _skeleton(mesh_name: String) -> Skeleton3D:
	var skeleton: Skeleton3D = _photobooth.get_node("Subject/%s/%s/GeneralSkeleton" % [mesh_name, mesh_name]) as Skeleton3D
	if skeleton == null:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing %s skeleton" % mesh_name)
	return skeleton


func _set_subject_visible(active_mesh_name: String) -> void:
	for mesh_name: String in ["Female", "Male"]:
		_photobooth.get_node("Subject/%s" % mesh_name).visible = mesh_name == active_mesh_name


func _require_bone(skeleton: Skeleton3D, bone_name: String) -> int:
	var bone := skeleton.find_bone(bone_name)
	if bone < 0:
		SceneUtils.fatal_error_and_quit("RIG-002 runner: missing bone %s" % bone_name)
	return bone


func _bone_world_position(skeleton: Skeleton3D, bone: int) -> Vector3:
	return skeleton.to_global(skeleton.get_bone_global_pose(bone).origin)


func _is_forearm_twist_chain_node(node: Node) -> bool:
	# The ForearmTwistModifier and its IK-005 hand-authority stage form the deform-only forearm-twist
	# chain this runner exercises; every other IK-ish node must stay disabled.
	return node.name == "ForearmTwistModifier" or node.name == "CharacterIKHandAuthorityStage"


func _is_ik_node(node: Node) -> bool:
	return node is SkeletonModifier3D or node.name.to_lower().contains("ik")


func _is_active_or_enabled(node: Node) -> bool:
	return (_has_property(node, "active") and bool(node.get("active"))) \
		or (_has_property(node, "enabled") and bool(node.get("enabled")))


func _has_property(node: Node, property_name: String) -> bool:
	for property: Dictionary in node.get_property_list():
		if property.name == property_name:
			return true
	return false


func _required_candidate_weight() -> float:
	for index in OS.get_cmdline_user_args().size():
		var arguments := OS.get_cmdline_user_args()
		if arguments[index] == "--weight" and index + 1 < arguments.size():
			return _validate_candidate_weight(arguments[index + 1])
		if arguments[index].begins_with("--weight="):
			return _validate_candidate_weight(arguments[index].trim_prefix("--weight="))
	SceneUtils.fatal_error_and_quit("RIG-002 runner requires an explicit --weight candidate (0.0, 0.25, 0.5, 0.75, or 1.0)")
	return NAN


func _validate_candidate_weight(value: String) -> float:
	var candidate := value.to_float()
	if _weight_label_for(value).is_empty():
		return NAN
	return candidate
