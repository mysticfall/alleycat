# XR-002 optical finger tracking photobooth runner.
#
# Captures close-up hand screenshots for the visual verification scenarios using the deterministic mock XR
# runtime through the OpticalHandTrackingScenarioDriver. Run windowed (never headless):
#
#   xvfb-run -a godot-mono -d -s --xr-mode off --path game "tests/xr/optical_finger_tracking_photobooth.gd"
extends SceneTree

const TEST_SCENE_PATH := "res://tests/xr/optical_finger_tracking_photobooth.tscn"
const DRIVER_SCRIPT_PATH := "res://src/XR/HandTracking/OpticalHandTrackingScenarioDriver.cs"
const OUTPUT_ROOT := "XR-002/optical_finger_tracking"

const REQUIRED_CAMERAS := ["LeftHandCamera", "RightHandCamera"]

# Tracked flexion scales: ~0 reads as an open hand, ~2.2 as a strong conspicuous curl.
const FLEX_OPEN := 0.15
const FLEX_MID := 1.0
const FLEX_CURL := 2.2
const FLEX_FREEZE_UPDATE := 2.4
const MINIMUM_CONTROL_FREEZE_DELTA_RADIANS := 0.3
const ROTATION_TOLERANCE_RADIANS := 0.001
const MINIMUM_CONTROL_FREEZE_CHILD_OFFSET_METRES := 0.005

# Deliberate spread and artificial source-roll contamination magnitudes (XR-002 visual scenarios).
const PROXIMAL_SPREAD_RADIANS := 0.42
const LONGITUDINAL_ROLL_RADIANS := 1.05
const MINIMUM_VISIBLE_SPREAD_RADIANS := 0.3

# World-space rest wrists (the character faces -Z; the hand cameras sit at -Z looking back toward +Z).
const RIGHT_WRIST_REST := Vector3(0.26, 1.02, -0.28)
const LEFT_WRIST_REST := Vector3(-0.26, 1.02, -0.28)

# Natural standing viewpoint for the mock head camera.
const HEAD_CAMERA_TRANSFORM := Transform3D(Basis.IDENTITY, Vector3(0, 1.62, 0))

# Wrist yaw offsets applied for the wrist-rotation scenario.
const RIGHT_WRIST_ROTATION_YAW := 1.3
const LEFT_WRIST_ROTATION_YAW := -1.3

const SETTLE_FRAMES_AFTER_INSTALL := 90
const SETTLE_FRAMES_PER_SCENARIO := 45

var _driver: Node
var _photobooth: Photobooth
var _player: Node
var _skeleton: Skeleton3D
var _right_ik_target: Node3D
var _captured: Dictionary = {}
var _capture_callable := Callable(self, "_on_skeleton_updated")


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit(
			"XR-002 runner must not run headless because screenshots would be invalid")
		return

	if not await _setup_fixture():
		return

	_validate_cameras_and_markers()

	if _has_user_arg("--validate-only"):
		print("XR-002 runner: validate-only mode completed successfully")
		quit(0)
		return

	await _capture_framing_pass()
	await _scenario_controller_authored_rest()
	await _scenario_optical_curl_override()
	await _scenario_independent_hands()
	await _scenario_deliberate_spread()
	await _scenario_combined_curl_and_spread()
	await _scenario_source_roll_contamination()
	await _scenario_thumb_opposition()
	await _scenario_per_joint_freeze()
	await _scenario_index_intermediate_controlled_comparison()
	await _scenario_wrist_rotation_active()
	await _scenario_optical_exit_restores_authored()

	_skeleton.skeleton_updated.disconnect(_capture_callable)

	print("XR002_OPTICAL_HAND_GATE_PASS artefact_dir=res://temp/%s" % OUTPUT_ROOT)
	quit(0)


func _setup_fixture() -> bool:
	# The service fixture must exist before the player rig enters the tree so runtime service resolution in the
	# finger modifier, VRIK providers, and hand behaviours sees the mock XR runtime.
	var driver_script: Script = load(DRIVER_SCRIPT_PATH) as Script
	if driver_script == null:
		SceneUtils.fatal_error_and_quit("XR-002 runner: failed to load the scenario driver script")
		return false

	_driver = driver_script.new()
	root.add_child(_driver)

	# The SceneTree script body starts before the root window finishes initialising, so wait for process frames
	# before touching the tree from C#.
	await SceneUtils.wait_frames(self, 2)

	if not bool(_driver.call("Setup")):
		SceneUtils.fatal_error_and_quit("XR-002 runner: driver setup failed: %s" % _driver.get("LastError"))
		return false

	await SceneUtils.wait_frames(self, 2)

	# Head camera and controller anchors give VRIK a stable standing pose with the hands resting where the
	# close-up cameras are framed.
	_driver.call("SetHeadCameraTransform", HEAD_CAMERA_TRANSFORM)
	_driver.call("SetControllerHandTransform", "Right", Transform3D(Basis.IDENTITY, RIGHT_WRIST_REST))
	_driver.call("SetControllerHandTransform", "Left", Transform3D(Basis.IDENTITY, LEFT_WRIST_REST))

	_photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("XR-002 runner: failed to instantiate the photobooth scene")
		return false

	root.add_child(_photobooth)
	await SceneUtils.wait_frames(self, 2)

	_player = SceneUtils.require_node(_photobooth, ^"Subject/Player")
	if not bool(_driver.call("InstallPlayerRig", _player)):
		SceneUtils.fatal_error_and_quit(
			"XR-002 runner: player rig installation failed: %s" % _driver.get("LastError"))
		return false

	# Activate the authored standing rest pose that stays selected across every scenario. The animation keeps
	# playing: freezing or zero-scaling playback stalls the VRIK actuation pipeline, while the idle animation's
	# arm drift is negligible once the hands settle at their targets.
	var animation_tree: AnimationTree = _player.get_node("AnimationTree") as AnimationTree
	animation_tree.active = true
	var playback: AnimationNodeStateMachinePlayback = animation_tree.get("parameters/States/playback")
	playback.start(&"StandingCrouching", true)

	# Bind the player VRIK after installation and animation activation: the installer defers a pose state
	# machine restart that invalidates earlier bindings (production binds through PlayerVRIKStartupBinder once
	# XRManager fires its initialised signal, which happens at the equivalent point in startup).
	if not bool(_driver.call("BindPlayerVRIK", _player)):
		SceneUtils.fatal_error_and_quit(
			"XR-002 runner: player VRIK binding failed: %s" % _driver.get("LastError"))
		return false

	var vrik: Node = _player.get_node("VRIK")
	_right_ik_target = (vrik.get("RightHandIKTarget") as Node3D)

	await SceneUtils.wait_frames(self, SETTLE_FRAMES_AFTER_INSTALL)

	_skeleton = _player.get_node("Female/GeneralSkeleton") as Skeleton3D
	_skeleton.skeleton_updated.connect(_capture_callable)

	print(
		"XR002_SETUP committed_mode=%s player_path=%s" % [
			String(_driver.call("GetCommittedMode")),
			_player.get_path()])

	return true


func _validate_cameras_and_markers() -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		if _photobooth.get_camera_rig(camera_name) == null:
			SceneUtils.fatal_error_and_quit("XR-002 runner: missing camera rig %s" % camera_name)
			return

	# Directional sanity: both rest markers must sit in front of the character (negative Z), matching the
	# close-up cameras placed on the -Z side looking back at the subject.
	for marker_name: String in ["RightHandRest", "LeftHandRest"]:
		var marker: Node3D = _photobooth.get_marker(marker_name) as Node3D
		if marker.global_position.z >= 0.0:
			SceneUtils.fatal_error_and_quit(
				"XR-002 runner: rest marker %s is not in front of the character (z >= 0)" % marker_name)
			return

	print(
		"XR002_MARKERS right_rest=%s left_rest=%s" % [
			_photobooth.get_marker("RightHandRest").global_position,
			_photobooth.get_marker("LeftHandRest").global_position])


func _capture_framing_pass() -> void:
	_photobooth.get_marker("RightHandRest").visible = true
	_photobooth.get_marker("LeftHandRest").visible = true

	await SceneUtils.wait_frames(self, 2)
	for camera_name: String in REQUIRED_CAMERAS:
		var rig: CameraRig = _photobooth.get_camera_rig(camera_name)
		await rig.capture_screenshot("%s/framing/%s_markers.jpg" % [OUTPUT_ROOT, _to_slug(camera_name)])

	_photobooth.get_marker("RightHandRest").visible = false
	_photobooth.get_marker("LeftHandRest").visible = false


func _scenario_controller_authored_rest() -> void:
	# Controller mode with valid optical curl samples available: the authored open rest pose must stay
	# authoritative because the committed mode gates all modifier writes (XR-002 TR17-TR18).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_CURL, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_CURL, "")

	await _settle("controller_authored_rest")
	await _capture("01_controller_authored_rest")


func _scenario_optical_curl_override() -> void:
	# Both controllers down and both hands tracked optically: the mode commits Optical (XR-002 TR3) and the
	# tracked curl visibly wins over the still-selected authored pose (XR-002 TR17).
	_driver.call("SetControllersTracked", false)
	_driver.call("SetOpticalHandsTracked", true)
	_driver.call("SetOpticalWristWorldSample", "Right", Transform3D(Basis.IDENTITY, RIGHT_WRIST_REST))
	_driver.call("SetOpticalWristWorldSample", "Left", Transform3D(Basis.IDENTITY, LEFT_WRIST_REST))
	_driver.call("EvaluateHandTrackingMode")

	await _settle("optical_curl_override")
	await _capture("02_optical_curl_override")


func _scenario_independent_hands() -> void:
	# Asymmetric tracked shapes: left hand curled while the right hand stays open (XR-002 TR22 independence).
	_driver.call("InjectTrackedHandPose", "Left", FLEX_CURL, "")
	_driver.call("InjectTrackedHandPose", "Right", FLEX_OPEN, "")

	await _settle("independent_hands")
	await _capture("03_independent_hands")


func _scenario_deliberate_spread() -> void:
	# Straight fingers with a bilateral deliberate spread: each proximal receives a signed swing about the palm
	# axis while the intermediate/distal joints hold their natural neutral (XR-002 TR22, H3).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_OPEN, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_OPEN, "")
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)
	var neutral_proximal: Quaternion = _rotation("RightIndexProximal")

	_driver.call("InjectTrackedHandPoseWithSpreadAndRoll", "Right", FLEX_OPEN, -PROXIMAL_SPREAD_RADIANS, 0.0, "")
	_driver.call("InjectTrackedHandPoseWithSpreadAndRoll", "Left", FLEX_OPEN, PROXIMAL_SPREAD_RADIANS, 0.0, "")

	await _settle("deliberate_spread")
	var spread_proximal: Quaternion = _rotation("RightIndexProximal")
	if neutral_proximal.angle_to(spread_proximal) <= MINIMUM_VISIBLE_SPREAD_RADIANS:
		SceneUtils.fatal_error_and_quit(
			"XR-002 runner: deliberate spread did not visibly tilt the right index proximal")
		return
	await _capture("03a_deliberate_spread")


func _scenario_combined_curl_and_spread() -> void:
	# Partial curl combined with a deliberate spread: bend planes stay coherent while the spread is retained
	# (XR-002 TR21-TR22, H4).
	_driver.call("InjectTrackedHandPoseWithSpreadAndRoll", "Right", FLEX_MID, -PROXIMAL_SPREAD_RADIANS * 0.8, 0.0, "")
	_driver.call("InjectTrackedHandPoseWithSpreadAndRoll", "Left", FLEX_MID, PROXIMAL_SPREAD_RADIANS * 0.8, 0.0, "")

	await _settle("combined_curl_spread")
	await _capture("03b_combined_curl_spread")


func _scenario_source_roll_contamination() -> void:
	# Artificial source roll about the tracked longitudinal direction: every destination output must be identical
	# to the roll-free pose — the non-thumb swing/hinge mappings discard it and the Stage 1 thumb discards the
	# metacarpal's axial opposition roll exactly the same way (XR-002 TR21-TR22, TR24, TR26, A3, H6).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_CURL, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_CURL, "")
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)
	var roll_free_proximal: Quaternion = _rotation("RightIndexProximal")
	var roll_free_intermediate: Quaternion = _rotation("RightIndexIntermediate")
	var roll_free_thumb_metacarpal: Quaternion = _rotation("RightThumbMetacarpal")

	_driver.call(
		"InjectTrackedHandPoseWithSpreadAndRoll",
		"Right",
		FLEX_CURL,
		0.0,
		LONGITUDINAL_ROLL_RADIANS,
		"")
	_driver.call(
		"InjectTrackedHandPoseWithSpreadAndRoll",
		"Left",
		FLEX_CURL,
		0.0,
		LONGITUDINAL_ROLL_RADIANS,
		"")

	await _settle("source_roll_contamination")
	_require_rotation_held(
		roll_free_proximal,
		_rotation("RightIndexProximal"),
		"contaminated roll reached the right index proximal output")
	_require_rotation_held(
		roll_free_intermediate,
		_rotation("RightIndexIntermediate"),
		"contaminated roll reached the right index intermediate output")
	_require_rotation_held(
		roll_free_thumb_metacarpal,
		_rotation("RightThumbMetacarpal"),
		"contaminated axial roll reached the right thumb metacarpal output")
	await _capture("03c_source_roll_contamination")


func _scenario_thumb_opposition() -> void:
	# Relaxed→opposition→relaxed thumb sweep: a strong tracked metacarpal direction change sweeps the right
	# thumb palmward towards the little-finger base through the Stage 1 constrained model while the left thumb
	# stays relaxed at its authored neutral (XR-002 TR24, TR26, H8).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_OPEN, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_OPEN, "")
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)
	var relaxed_thumb_metacarpal: Quaternion = _rotation("RightThumbMetacarpal")

	_driver.call(
		"InjectTrackedHandPoseWithJointFlex",
		"Right",
		FLEX_OPEN,
		"ThumbMetacarpal",
		FLEX_CURL,
		"")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_OPEN, "")

	await _settle("thumb_opposition")
	_require_rotation_difference(
		relaxed_thumb_metacarpal,
		_rotation("RightThumbMetacarpal"),
		"tracked opposition did not visibly sweep the right thumb metacarpal")
	await _capture("03d_thumb_opposition")

	# Releasing the opposition returns the thumb to its authored neutral (H8 relaxed→opposition→relaxed).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_OPEN, "")
	await _settle("thumb_opposition_released")
	_require_rotation_held(
		relaxed_thumb_metacarpal,
		_rotation("RightThumbMetacarpal"),
		"released opposition did not return the right thumb metacarpal to its relaxed pose")
	await _capture("03e_thumb_opposition_released")


func _scenario_per_joint_freeze() -> void:
	# Seed a shared tracked pose, then invalidate the right index intermediate joint while every sibling
	# receives a stronger curl: the frozen finger segment stays put while its neighbours move (XR-002 TR22).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_MID, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_MID, "")
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)

	_driver.call("ClearJointSample", "Right", "IndexIntermediate")
	_driver.call("InjectTrackedHandPose", "Right", FLEX_FREEZE_UPDATE, "IndexIntermediate")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_FREEZE_UPDATE, "")

	await _settle("per_joint_freeze")
	await _capture("04_per_joint_freeze")


func _scenario_index_intermediate_controlled_comparison() -> void:
	# Controlled comparison for reviewer inspection: both captures retain the left hand and every non-index-
	# intermediate right joint at the same mid-flex static reference. The control accepts a conspicuous local
	# curl only on the right index intermediate; the freeze case differs only by invalidating that source joint
	# after reseeding its cached mid-flex rotation. Its required child, IndexDistal, must retain its own cache too.
	_driver.call("InjectTrackedHandPose", "Right", FLEX_MID, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_MID, "")
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)
	var cached_intermediate: Quaternion = _rotation("RightIndexIntermediate")
	var cached_distal: Quaternion = _rotation("RightIndexDistal")

	_driver.call(
		"InjectTrackedHandPoseWithJointFlex",
		"Right",
		FLEX_MID,
		"IndexIntermediate",
		FLEX_FREEZE_UPDATE,
		"")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_MID, "")
	await _settle("index_intermediate_control")
	var control_intermediate: Quaternion = _rotation("RightIndexIntermediate")
	var control_distal_global: Transform3D = _captured.get("RightIndexDistalGlobal", Transform3D.IDENTITY)
	_require_rotation_difference(
		cached_intermediate,
		control_intermediate,
		"control right index intermediate did not receive its conspicuous update")
	await _capture("07_index_intermediate_control")

	# Re-establish the exact cached reference that the invalid source joint must retain. The visual input is
	# then identical to the control except for the missing intermediate sample.
	_driver.call("InjectTrackedHandPose", "Right", FLEX_MID, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_MID, "")
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)
	cached_intermediate = _rotation("RightIndexIntermediate")
	cached_distal = _rotation("RightIndexDistal")

	_driver.call("ClearJointSample", "Right", "IndexIntermediate")
	_driver.call(
		"InjectTrackedHandPoseWithJointFlex",
		"Right",
		FLEX_MID,
		"IndexIntermediate",
		FLEX_FREEZE_UPDATE,
		"IndexIntermediate")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_MID, "")
	await _settle("index_intermediate_freeze")
	var frozen_intermediate: Quaternion = _rotation("RightIndexIntermediate")
	var frozen_distal: Quaternion = _rotation("RightIndexDistal")
	var frozen_distal_global: Transform3D = _captured.get("RightIndexDistalGlobal", Transform3D.IDENTITY)
	_require_rotation_held(
		cached_intermediate,
		frozen_intermediate,
		"frozen right index intermediate did not retain its cached rotation")
	_require_rotation_held(
		cached_distal,
		frozen_distal,
		"frozen right index distal did not retain its cached rotation")
	_require_rotation_difference(
		control_intermediate,
		frozen_intermediate,
		"control and freeze right index intermediate rotations are visually too similar")
	_require_child_offset(
		control_distal_global.origin,
		frozen_distal_global.origin,
		"control and freeze index-distal child positions are visually too similar")
	await _capture("08_index_intermediate_freeze")


func _scenario_wrist_rotation_active() -> void:
	# Restore a strong tracked curl, then rotate both VRIK wrist targets while tracking stays active: the wrist
	# orientation changes while the tracked finger pose persists (XR-002 TR7, TR13).
	_driver.call("InjectTrackedHandPose", "Right", FLEX_CURL, "")
	_driver.call("InjectTrackedHandPose", "Left", FLEX_CURL, "")

	var right_rotated := Transform3D(Basis.IDENTITY.rotated(Vector3.UP, RIGHT_WRIST_ROTATION_YAW), RIGHT_WRIST_REST)
	var left_rotated := Transform3D(Basis.IDENTITY.rotated(Vector3.UP, LEFT_WRIST_ROTATION_YAW), LEFT_WRIST_REST)
	_driver.call("SetOpticalWristWorldSample", "Right", right_rotated)
	_driver.call("SetOpticalWristWorldSample", "Left", left_rotated)

	await _settle("wrist_rotation_active")
	await _capture("05_wrist_rotation_active")


func _scenario_optical_exit_restores_authored() -> void:
	# Pick both controllers up again: the mode commits Controller (XR-002 TR3) and, with no pose clearing, the
	# still-selected authored rest pose becomes visible again (XR-002 TR18, TR24).
	_driver.call("SetControllersTracked", true)
	_driver.call("SetOpticalHandsTracked", false)
	_driver.call("EvaluateHandTrackingMode")

	await _settle("optical_exit_restores_authored")
	await _capture("06_optical_exit_restores_authored")


func _settle(label: String) -> void:
	await SceneUtils.wait_frames(self, SETTLE_FRAMES_PER_SCENARIO)
	var proximal: Quaternion = _captured.get("RightIndexProximal", Quaternion.IDENTITY)
	var hand: Transform3D = _captured.get("RightHandGlobal", Transform3D.IDENTITY)
	print(
		"XR002_SCENARIO label=%s committed_mode=%s r_prox_w=%.4f r_hand=%s r_ik_target=%s" % [
			label, String(_driver.call("GetCommittedMode")), proximal.w, hand.origin,
			_right_ik_target.global_position if _right_ik_target != null else Vector3.ZERO])


func _on_skeleton_updated() -> void:
	if _skeleton == null:
		return
	var proximal_index: int = _skeleton.find_bone("RightIndexProximal")
	var intermediate_index: int = _skeleton.find_bone("RightIndexIntermediate")
	var distal_index: int = _skeleton.find_bone("RightIndexDistal")
	var thumb_metacarpal_index: int = _skeleton.find_bone("RightThumbMetacarpal")
	var hand_index: int = _skeleton.find_bone("RightHand")
	_captured["RightIndexProximal"] = _skeleton.get_bone_pose_rotation(proximal_index)
	_captured["RightIndexIntermediate"] = _skeleton.get_bone_pose_rotation(intermediate_index)
	_captured["RightIndexDistal"] = _skeleton.get_bone_pose_rotation(distal_index)
	_captured["RightThumbMetacarpal"] = _skeleton.get_bone_pose_rotation(thumb_metacarpal_index)
	_captured["RightIndexDistalGlobal"] = _skeleton.get_bone_global_pose(distal_index)
	_captured["RightHandGlobal"] = _skeleton.get_bone_global_pose(hand_index)


func _capture(scenario_slug: String) -> void:
	await _photobooth.capture_screenshots("%s/scenarios/%s.jpg" % [OUTPUT_ROOT, scenario_slug])


func _rotation(bone_name: String) -> Quaternion:
	return _captured.get(bone_name, Quaternion.IDENTITY)


func _require_rotation_held(expected: Quaternion, actual: Quaternion, message: String) -> void:
	if expected.angle_to(actual) > ROTATION_TOLERANCE_RADIANS:
		SceneUtils.fatal_error_and_quit("XR-002 runner: %s" % message)


func _require_rotation_difference(reference: Quaternion, actual: Quaternion, message: String) -> void:
	if reference.angle_to(actual) <= MINIMUM_CONTROL_FREEZE_DELTA_RADIANS:
		SceneUtils.fatal_error_and_quit("XR-002 runner: %s" % message)


func _require_child_offset(reference: Vector3, actual: Vector3, message: String) -> void:
	if reference.distance_to(actual) <= MINIMUM_CONTROL_FREEZE_CHILD_OFFSET_METRES:
		SceneUtils.fatal_error_and_quit("XR-002 runner: %s" % message)


func _to_slug(value: String) -> String:
	return SceneUtils.to_safe_file_component(value.to_lower())


func _has_user_arg(expected_arg: String) -> bool:
	for arg: String in OS.get_cmdline_user_args():
		if arg == expected_arg:
			return true

	return false
