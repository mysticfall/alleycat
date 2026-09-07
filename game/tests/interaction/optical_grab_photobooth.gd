# CTRL-002 optical grab interaction photobooth runner (natural-pose acceptance capture).
#
# Captures the natural, temporal, multi-view screenshot sequence of ball and stick interaction with
# anatomically plausible hands by driving real grabs through the production HandGrabInputCoordinator
# inside the reference female player rig, using the deterministic mock XR runtime through the
# OpticalGrabInteractionScenarioDriver. Run windowed (never headless):
#
#   xvfb-run -a godot-mono -d -s --xr-mode off --path game "tests/interaction/optical_grab_photobooth.gd" -- \
#     --output-dir "temp/CTRL-002/optical_grab_palm_20260907"
#
# Fixture-authoring notes (palm-side rework after the 2026-09-07 rejection):
# - The solved hand world orientation follows the injected wrist-sample basis (verified by
#   measurement), so natural palm orientation is an authored input: every right-hand ball/stick
#   injection carries a fixed basis whose local axes match the measured rig hand frame
#   (+X thumb, +Y fingers, +Z palm normal), with the fingers authored along the measured solved
#   forearm line (the production elbow pole rides out-and-back, so frontal waist targets always see
#   a descending-inward forearm) and the palm rolled to face the item.
# - The authored grab-point seat offsets are fixed content expressed in the wrist-sample frame
#   (ball +71 mm along the fingers / +49 mm along the palm; stick +67 mm along the stick axis /
#   +30 mm along the palm), so the items are PLACED at terminal-wrist + basis * offset: the ball
#   ends up cradled on the palm side and the stick contact sits palm-side along its axis.
# - The stick is authored ALONG the measured forearm direction so the hold's selected axis basis
#   keeps the fingers continuing the forearm (neutral wrist at the hold).
# - All pose measurements read through runtime BoneAttachment3D probes: direct
#   Skeleton3D.get_bone_global_pose reads resolve mid-pipeline (pre-modifier) data in this fixture.
# - The mock head camera is computed at runtime from the skeleton head-bone rest and the authored
#   Viewpoint marker so the default head target requests exactly the rest head pose (no crouch bias).
# - The StandingCrouching TimeSeek receives no seek requests in the fixture context (nothing drives
#   CharacterLocomotion here), so the crouch-seek clip would free-play to its full-crouch end pose.
#   The runner pins the authored standing seek once per physics tick, the same value the pose state
#   machine writes for a rest-height head target.
# - Item instances, markers, and camera rigs are re-authored at setup for the standing solve; the
#   shared scene file stays untouched so the C# suite's own authored placements are unaffected.
# - All wrist/closure inputs are independently authored frozen trajectories: no expected attachment,
#   realised error, provider output, or other runtime observation drives any input.
extends SceneTree

const TEST_SCENE_PATH := "res://tests/interaction/optical_grab_photobooth.tscn"
const DRIVER_SCRIPT_PATH := "res://src/Interaction/Hands/OpticalGrabInteractionScenarioDriver.cs"
const OUTPUT_ROOT := "CTRL-002/optical_grab_palm_20260907"

const GRAB_BALL_ANIMATION_PATH := "res://assets/characters/reference/female/animations/Grab-ball-40.tres"
const GRAB_PIPE_ANIMATION_PATH := "res://assets/characters/reference/female/animations/Grab-pipe-10.tres"

const REQUIRED_CAMERAS := [
	"RightHandCamera", "LeftHandCamera", "StickCamera", "InteractionOverviewCamera",
	"PendingCommitCamera", "ProfileCamera"
]
const REQUIRED_MARKERS := [
	"RightHandRest", "LeftHandRest", "BallItem", "BallContact", "StickItem", "StickContact",
]

const STANDING_SEEK_PARAMETER := "parameters/States/StandingCrouching/TimeSeek/seek_request"
const AUTHORED_STANDING_SEEK := 0.0

# Authored right-hand wrist-sample bases (columns = local X/Y/Z axes in world space). The rig's
# hand-attachment frame measured at identity basis maps +X to the thumb side, +Y along the fingers,
# and +Z onto the palm normal, with the fingers curling toward +Z; the basis below points the
# fingers along the measured solved forearm line (down-in-forward; the production elbow pole keeps
# the elbow out and back for frontal waist targets) and rolls the palm up-and-forward so it faces
# the item. The frozen basis never changes across a scenario's approach, commit, and hold.
const RIGHT_WRIST_SAMPLE_BASIS := Basis(
	Vector3(0.6605, -0.5983, -0.4536),
	Vector3(-0.7032, -0.2813, -0.653),
	Vector3(0.2631, 0.7503, -0.6065))
# Relaxed left-hand basis for the resting/moving left wrist: fingers down-and-forward with the
# palm rolled to face inward toward the thigh, matching the measured frame conventions above.
const LEFT_WRIST_SAMPLE_BASIS := Basis(
	Vector3(-0.1981, -0.2841, 0.9381),
	Vector3(-0.1204, -0.9428, -0.3109),
	Vector3(0.9728, -0.1745, 0.1526))
# The independent-hands scenario moves the left hand outward to a higher, more extended position
# where the forearm runs near-level; a separate basis keeps that pose's fingers continuing the
# forearm line instead of drooping straight down.
const LEFT_WRIST_MOVED_BASIS := Basis(
	Vector3(0.0854, -0.9496, 0.3076),
	Vector3(0.2036, -0.2851, -0.9366),
	Vector3(0.977, 0.143, 0.170))

# Ball workspace (right hand). The open hand rests directly palm-side of the ball along the palm
# normal (75 mm from the centre against the 120 mm spherical reach: a 45 mm acquisition margin);
# the terminal wrist completes the seat at the authored offset, giving a bent-elbow 0.56 extension
# hold at waist height with the ball cradled on the palm.
const RIGHT_WRIST_REST := Vector3(0.124, 1.02, -0.191)
const RIGHT_WRIST_HELD := Vector3(0.18, 1.06, -0.16)
const BALL_ITEM_POSITION := Vector3(0.144, 1.076, -0.237)

# Stick workspace (right hand). The stick runs along the measured forearm direction so the held
# selected-axis basis keeps the fingers continuing the forearm (11 degree wrist at the hold); the
# contact sits 130 mm from the stick centre along the axis and the open-hand waypoint rests 41 mm
# from the contact inside the 80 mm snap zone (39 mm margin).
const STICK_ITEM_POSITION := Vector3(0.082, 1.006, -0.335)
const STICK_ITEM_BASIS := Basis(
	Vector3(0.8101, 0.0, -0.5863),
	Vector3(-0.5493, -0.3495, -0.759),
	Vector3(-0.2049, 0.9369, -0.2832))
const STICK_CONTACT_POSITION := Vector3(0.153, 1.052, -0.236)
const RIGHT_WRIST_STICK := Vector3(0.185, 1.05, -0.17)
const RIGHT_WRIST_STICK_APPROACH := Vector3(0.15, 1.02, -0.21)

# Authored controller-mode wrist source whose calibrated controller sample resolves near the ball
# workspace after the mode-switch scenario (fixture-authored constant, verified at runtime against
# the PREP print: the XR-origin swing maps this source to ~ (0.19, 1.03, -0.19), ~80 mm from the
# ball centre).
const RIGHT_CONTROLLER_PENDING_SOURCE := Vector3(-0.19, 1.071, 0.147)

# The moved left wrist position for the independent-hands scenario.
const LEFT_WRIST_REST := Vector3(-0.2, 1.02, -0.16)
const LEFT_WRIST_MOVED := Vector3(-0.24, 1.06, -0.24)

# Authored small held-movement delta applied after the ball commit: the hand (and attached ball)
# visibly follow an independently written wrist displacement.
const HELD_WRIST_MOVEMENT_DELTA := Vector3(-0.03, 0.02, 0.02)
const HELD_MOVEMENT_DRIVE_FRAMES := 20

# Camera re-aiming: fixture camera rigs are re-aimed at the authored items for the standing
# framing. The palm-side poses cup the items up-and-forward, so the close-up rigs view from
# front-above where both the palm and the seated item are visible; the runtime-added ProfileCamera
# views along the character's right side so wrist angle and palm roll stay judgeable in profile.
const CAMERA_REAIM := {
	"RightHandCamera": {
		"eye": Vector3(0.42, 1.3, -0.85),
		"focus": Vector3(0.144, 1.076, -0.237),
	},
	"LeftHandCamera": {
		"eye": Vector3(-0.6, 1.15, -0.75),
		"focus": Vector3(-0.2, 1.02, -0.16),
	},
	"StickCamera": {
		"eye": Vector3(0.35, 1.25, -0.85),
		"focus": Vector3(0.153, 1.052, -0.236),
	},
	"PendingCommitCamera": {
		"eye": Vector3(0.42, 1.05, -0.8),
		"focus": Vector3(0.144, 1.076, -0.237),
	},
	"ProfileCamera": {
		"eye": Vector3(1.0, 1.1, -0.3),
		"focus": Vector3(0.15, 1.05, -0.2),
		"orthogonal_scale": 0.55,
	},
}

# Canonical destination finger bones (XRHandJoints.DestinationJoints order), cached per side at
# skeleton_updated because modifier writes are per-frame overlays.
const FINGER_JOINTS := [
	"ThumbMetacarpal", "ThumbProximal", "ThumbDistal",
	"IndexProximal", "IndexIntermediate", "IndexDistal",
	"MiddleProximal", "MiddleIntermediate", "MiddleDistal",
	"RingProximal", "RingIntermediate", "RingDistal",
	"LittleProximal", "LittleIntermediate", "LittleDistal"
]

# Stability 0.10 s and blend 0.20 s at 60 Hz physics: every wait is >= 2x the window plus settle margin.
const SETTLE_FRAMES_AFTER_INSTALL := 90
const VRIK_SETTLE_PHYSICS_TICKS := 45
const STABILITY_WAIT_PHYSICS_TICKS := 20
const BLEND_WAIT_PHYSICS_TICKS := 30
const APPROACH_BUDGET_PHYSICS_TICKS := 300
const APPROACH_BUDGET_PROCESS_FRAMES := 450

# Held-pose comparisons: near-exact agreement with the authored reference, idle-animation drift absorbed.
const AUTHORED_MATCH_TOLERANCE_RADIANS := 0.03
# Fixed-pose stability across over-clench and tracking loss (animation active, so small drift is allowed).
const HELD_STABLE_TOLERANCE_RADIANS := 0.005
# Distinctness guards: poses that must look different.
const DISTINCT_POSE_RADIANS := 0.15
# The early pending checkpoint must visibly sit between the open tracked baseline and the committed authored grip.
const PENDING_PARTIAL_MIN_CUP_RADIANS := 0.08
const PENDING_PARTIAL_MIN_GAP_TO_HELD_RADIANS := 0.10
# Gradual-closure checkpoints (fractions of the calibrated open->closed band, still below the grab edge).
const CLOSURE_CAPTURE_FRACTIONS := [0.4, 0.7]
# Tracked-tracking agreement after release/independence (real profile projection, near-exact).
const TRACKED_MATCH_TOLERANCE_RADIANS := 0.03
# Stick offset guard: the held hand must sit closer to the contact point than to the stick centre.
const STICK_OFFSET_MIN_MARGIN_METRES := 0.05
const MOVABLE_ATTACHMENT_POSITION_TOLERANCE_METRES := 0.008
const MOVABLE_ATTACHMENT_ORIENTATION_TOLERANCE_DEGREES := 5.0
const STATIONARY_POSITION_TOLERANCE_METRES := 0.001
const ADJACENT_ITEM_DISCONTINUITY_TOLERANCE_METRES := 0.010
const ADJACENT_ITEM_DISCONTINUITY_TOLERANCE_DEGREES := 5.5
# The visual runner samples once per physics tick under software rendering. The natural-reach
# fixture's assisted approaches travel real distances (the provider responsiveness of 18 1/s over
# a >10 cm correction peaks above 1.4 m/s, i.e. >23 mm per tick) so the strict gate bounds anomalous
# snaps at 25 mm rather than the near-zero-correction C# fixture's 12 mm; item continuity stays 10 mm.
const ADJACENT_HAND_DISCONTINUITY_TOLERANCE_METRES := 0.025
const ADJACENT_HAND_DISCONTINUITY_TOLERANCE_DEGREES := 8.0
# The cylindrical assisted approach composes the authored grab-point rotation offset into the
# approach target, which rolls the target ~89 degrees about the palm normal relative to any
# non-identity query basis (the spherical grab point cancels the same offset analytically). The
# production actuator chases that authored roll smoothly at a bounded fraction per tick, so the
# stick continuity gate bounds the authored-roll chase instead of an anomalous snap; the strict
# positional gate and every ball-trace angular gate stay at the strict bounds.
const STICK_HAND_ANGULAR_TOLERANCE_DEGREES := 20.0
# Commit-boundary chase allowance (the reparent and first post-clear frames), mirroring the C#
# continuity unit's separate boundary bound; every other adjacent pair stays under the strict gates.
const COMMIT_BOUNDARY_HAND_TOLERANCE_METRES := 0.030
const COMMIT_BOUNDARY_ITEM_TOLERANCE_METRES := 0.030
const CLOSURE_RAMP_FRAMES := 10
const WRIST_TRAJECTORY_DELAY_FRAMES := 18
const WRIST_TRAJECTORY_FRAMES := 30
const INITIAL_HELD_TRACE_FRAMES := 6

# Item reachability and natural-pose qualification bounds (read-only measurements, asserted).
const BALL_REACH_METRES := 0.12
const STICK_SNAP_METRES := 0.08
const BALL_ACQUISITION_MARGIN_MIN_METRES := 0.03
const BALL_ACQUISITION_MARGIN_MAX_METRES := 0.09
const STICK_ACQUISITION_MARGIN_MIN_METRES := 0.03
# Body-lean allowance on the skeleton-measured rest arm envelope (mirrors the C# qualification).
const REACH_QUALIFICATION_BODY_LEAN_ALLOWANCE_METRES := 0.08
# Final-pose guards (all measured through the solved-pose attachment probes): the rejected captures
# had near-straight arms, ~100+ degree wrist bends, palms rolled away from the item, and the item
# seated on the dorsal side, so every hold must keep a clearly bent elbow, a near-neutral wrist,
# a palm that faces the item, and positive palm-side seating.
const ARM_EXTENSION_RATIO_MIN := 0.45
const ARM_EXTENSION_RATIO_MAX := 0.75
const WRIST_NEUTRAL_MAX_DEGREES := 30.0
const PALM_FACING_ITEM_MIN_DOT := 0.5
const PALM_SIDE_SEATING_MIN_METRES := 0.02
const ITEM_CONTACT_MAX_DISTANCE_METRES := 0.09
const ITEM_TO_ELBOW_MAX_COSINE := 0.35
# Crouch anomaly guards for the pinned standing pose (hip drops to ~0.31 when the crouch clip
# free-plays; the pinned standing hip sits ~0.83).
const STANDING_HIP_MIN_HEIGHT_METRES := 0.75
const HEAD_TARGET_REST_DESCENT_MAX_METRES := 0.03
const ROOT_DRIFT_MAX_METRES := 0.002

const OVER_CLENCH_SCALE := 1.8

# Placement-survey mode: samples the solved hand geometry at authored candidate wrist positions and
# prints measurements (palm normal candidates, hand axis, forearm axis, wrist flexion, arm extension).
# The accepted placements are frozen authored constants; nothing from this survey feeds back into any
# input at capture time.
const SAMPLE_PALM_ARG := "--sample-palm"
# Candidate right-wrist positions on the frontal lower-chest/waist workspace (character faces -Z).
const PALM_SAMPLE_RIGHT_CANDIDATES := [
	Vector3(0.10, 0.98, -0.30), Vector3(0.18, 0.98, -0.30), Vector3(0.26, 0.98, -0.30),
	Vector3(0.10, 1.04, -0.22), Vector3(0.18, 1.04, -0.22), Vector3(0.26, 1.04, -0.22),
	Vector3(0.10, 1.04, -0.30), Vector3(0.18, 1.04, -0.30), Vector3(0.26, 1.04, -0.30),
	Vector3(0.10, 1.04, -0.38), Vector3(0.18, 1.04, -0.38), Vector3(0.26, 1.04, -0.38),
	Vector3(0.10, 1.10, -0.30), Vector3(0.18, 1.10, -0.30), Vector3(0.26, 1.10, -0.30),
	Vector3(0.18, 1.16, -0.30), Vector3(0.26, 1.16, -0.30),
]
const PALM_SAMPLE_LEFT_CANDIDATES := [
	Vector3(-0.24, 1.02, -0.28), Vector3(-0.20, 1.06, -0.26), Vector3(-0.28, 0.98, -0.30),
]
const PALM_SAMPLE_SETTLE_TICKS := 55

var _driver: Node
var _photobooth: Photobooth
var _player: Node
var _skeleton: Skeleton3D
var _ball: RigidBody3D
var _stick: RigidBody3D
var _stick_authored_transform: Transform3D
var _captured: Dictionary = {}
var _capture_callable := Callable(self, "_on_skeleton_updated")
var _seek_pin_callable := Callable(self, "_pin_standing_seek")
var _animation_tree: AnimationTree

var _ball_closed_flex := 0.0
var _ball_open_flex := 0.0
var _stick_closed_flex := 0.0
var _stick_open_flex := 0.0
var _ball_authored_reference: Array = []
var _pipe_authored_reference: Array = []
var _ball_open_finger_reference: Dictionary = {}
var _active_temporal_trace: Array = []
var _active_temporal_item: Node3D
# Scenario path prefix: with an explicit --output-dir the paths are relative to it; without one they sit
# under the SceneUtils default temp root.
var _output_root := OUTPUT_ROOT
# Capture manifest rows: [image path, authored expected visible cue].
var _manifest_rows: Array = []


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit(
			"CTRL-002 runner must not run headless because screenshots would be invalid")
		return

	_output_root = _resolve_output_root()

	if not await _setup_fixture():
		return

	_validate_cameras_and_markers()

	if _has_user_arg(SAMPLE_PALM_ARG):
		await _run_palm_sampling()
		quit(0)
		return

	if _has_user_arg("--validate-only"):
		print("CTRL-002 runner: validate-only mode completed successfully")
		quit(0)
		return

	await _capture_framing_pass()
	await _scenario_open_hand_near_ball()
	await _scenario_ball_gradual_closure()
	await _scenario_ball_commit()
	await _scenario_ball_held_movement()
	await _scenario_ball_held_hidden_open()
	await _scenario_ball_release()
	await _scenario_stick_commit_offset()
	await _scenario_held_tracking_loss()
	await _scenario_mode_switch_release()
	await _scenario_controller_pending_without_assistance()
	await _scenario_independent_hands()

	_skeleton.skeleton_updated.disconnect(_capture_callable)
	physics_frame.disconnect(_seek_pin_callable)

	_write_manifest()

	print("CTRL002_OPTICAL_GRAB_GATE_PASS artefact_dir=res://temp/%s" % OUTPUT_ROOT)
	quit(0)


func _setup_fixture() -> bool:
	# The service fixture must exist before the player rig enters the tree so runtime service resolution in
	# the coordinator, finger modifier, VRIK providers, and hand behaviours sees the mock XR runtime.
	var driver_script: Script = load(DRIVER_SCRIPT_PATH) as Script
	if driver_script == null:
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to load the scenario driver script")
		return false

	_driver = driver_script.new()
	root.add_child(_driver)

	await SceneUtils.wait_frames(self, 2)

	if not bool(_driver.call("Setup")):
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: driver setup failed: %s" % _driver.get("LastError"))
		return false

	await SceneUtils.wait_frames(self, 2)

	_photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if _photobooth == null:
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to instantiate the photobooth scene")
		return false

	root.add_child(_photobooth)
	await SceneUtils.wait_frames(self, 2)

	_player = SceneUtils.require_node(_photobooth, ^"Subject/Player")
	if not bool(_driver.call("AttachPlayer", _player)):
		SceneUtils.fatal_error_and_quit(
			"CTRL-002 runner: player rig attachment failed: %s" % _driver.get("LastError"))
		return false

	# The mock head camera is computed from the skeleton head-bone rest and the authored Viewpoint
	# marker so the default head target equals the rest head target exactly:
	#   camera.global = (skeleton.global * headBoneRest * viewpointLocal) * viewpointLocal
	# This removes the head-descent crouch bias; the value is derived only from rest-pose asset
	# data, never from a solved or runtime state.
	_skeleton = _player.get_node("Female/GeneralSkeleton") as Skeleton3D
	var viewpoint: Node3D = _player.get_node("Female/GeneralSkeleton/Head/Viewpoint") as Node3D
	var head_bone_index := _skeleton.find_bone("Head")
	_require(head_bone_index >= 0, "the fixture skeleton must expose the Head bone")
	var head_rest := _skeleton.global_transform * _skeleton.get_bone_global_rest(head_bone_index)
	var head_target_rest := head_rest * viewpoint.transform
	var head_camera_transform := head_target_rest * viewpoint.transform
	_driver.call("SetHeadCameraTransform", head_camera_transform)
	print("CTRL002_HEAD_CAMERA head_rest_y=%.4f viewpoint_y=%.4f camera_y=%.4f" % [
		head_rest.origin.y, viewpoint.transform.origin.y, head_camera_transform.origin.y])

	# Controller anchors pin the hands at the fixture rest wrists.
	_driver.call("SetControllerHandTransform", "Right", _ball_wrist_sample(RIGHT_WRIST_REST))
	_driver.call("SetControllerHandTransform", "Left", _left_wrist_sample(LEFT_WRIST_REST))

	# Activate the authored standing rest pose that stays selected across every scenario (XR-002 precedent:
	# the animation keeps playing so the VRIK actuation pipeline stays live).
	_animation_tree = _player.get_node("AnimationTree") as AnimationTree
	_animation_tree.active = true
	var playback: AnimationNodeStateMachinePlayback = _animation_tree.get("parameters/States/playback")
	playback.start(&"StandingCrouching", true)

	if not bool(_driver.call("BindPlayerVRIK", _player)):
		SceneUtils.fatal_error_and_quit(
			"CTRL-002 runner: player VRIK binding failed: %s" % _driver.get("LastError"))
		return false

	# Pin the authored standing seek once per physics tick: nothing in the fixture context drives
	# CharacterLocomotion, so without the pin the crouch-seek clip free-plays to its full-crouch end
	# pose (the root cause of the crouch in every invalidated capture).
	physics_frame.connect(_seek_pin_callable)

	await SceneUtils.wait_frames(self, SETTLE_FRAMES_AFTER_INSTALL)

	_ball = _photobooth.get_node("Items/Ball") as RigidBody3D
	_stick = _photobooth.get_node("Items/Stick") as RigidBody3D

	_author_fixture_geometry()

	_skeleton.skeleton_updated.connect(_capture_callable)

	if not await _commit_optical_and_calibrate():
		return false

	print(
		"CTRL002_SETUP committed_mode=%s ball_flex=%.2f/%.2f stick_flex=%.2f/%.2f player_path=%s" % [
			String(_driver.call("GetCommittedMode")), _ball_closed_flex, _ball_open_flex,
			_stick_closed_flex, _stick_open_flex, _player.get_path()])

	return true


func _author_fixture_geometry() -> void:
	# Fixture-only instance authoring for the standing solve: the shared scene and its C# authored
	# placements stay untouched; this run re-places its own item instances, diagnostic markers, and
	# camera-rig positions, and adds the side-profile rig plus the solved-pose joint probes.
	_ball.global_transform = Transform3D(Basis.IDENTITY, BALL_ITEM_POSITION)
	_ball.force_update_transform()

	_stick_authored_transform = Transform3D(STICK_ITEM_BASIS, STICK_ITEM_POSITION)
	# The stick is parked away from candidate discovery during the ball scenarios and placed at its
	# authored transform only for its own scenario (mirroring the C# competitor handling).
	_stick.global_position = Vector3(10.0, 10.0, 10.0)
	_stick.force_update_transform()
	_stick.visible = false

	_move_marker("RightHandRest", Transform3D(Basis.IDENTITY, RIGHT_WRIST_REST))
	_move_marker("LeftHandRest", Transform3D(Basis.IDENTITY, LEFT_WRIST_REST))
	_move_marker("BallItem", Transform3D(Basis.IDENTITY, BALL_ITEM_POSITION))
	_move_marker("BallContact", Transform3D(Basis.IDENTITY, BALL_ITEM_POSITION))
	_move_marker("StickItem", _stick_authored_transform)
	_move_marker("StickContact", Transform3D(Basis.IDENTITY, STICK_CONTACT_POSITION))

	# Solved-pose readouts: direct bone-pose reads resolve mid-pipeline data, so every measurement
	# goes through BoneAttachment3D probes on the measured joints.
	_ensure_joint_probes()

	# Runtime-added side-profile rig: looks at the hand workspace from the character's right side so
	# the wrist angle and palm roll stay judgeable in profile (the shared scene stays untouched).
	if not _photobooth.camera_rigs.has("ProfileCamera"):
		_require(_photobooth.add_camera_rig("ProfileCamera") != null,
			"the fixture must be able to add the profile camera rig")
		_photobooth.get_camera_rig("ProfileCamera").image_size = Vector2(640, 640)
	for camera_name: String in CAMERA_REAIM:
		var rig: CameraRig = _photobooth.get_camera_rig(camera_name)
		_require(rig != null, "the fixture must expose the %s rig for re-aiming" % camera_name)
		rig.global_position = CAMERA_REAIM[camera_name]["eye"]
		rig.look_at(CAMERA_REAIM[camera_name]["focus"], Vector3.UP)
		if CAMERA_REAIM[camera_name].has("orthogonal_scale"):
			rig.orthogonal_scale = CAMERA_REAIM[camera_name]["orthogonal_scale"]


func _ball_wrist_sample(wrist_position: Vector3) -> Transform3D:
	return Transform3D(RIGHT_WRIST_SAMPLE_BASIS, wrist_position)


func _stick_wrist_sample(wrist_position: Vector3) -> Transform3D:
	return Transform3D(RIGHT_WRIST_SAMPLE_BASIS, wrist_position)


func _left_wrist_sample(wrist_position: Vector3) -> Transform3D:
	return Transform3D(LEFT_WRIST_SAMPLE_BASIS, wrist_position)


func _left_moved_wrist_sample(wrist_position: Vector3) -> Transform3D:
	return Transform3D(LEFT_WRIST_MOVED_BASIS, wrist_position)


func _move_marker(marker_name: String, world_transform: Transform3D) -> void:
	var marker: Node3D = _photobooth.get_marker(marker_name) as Node3D
	_require(marker != null, "the fixture must expose the %s marker" % marker_name)
	marker.global_transform = world_transform


func _pin_standing_seek() -> void:
	if _animation_tree != null and is_instance_valid(_animation_tree):
		_animation_tree.set(STANDING_SEEK_PARAMETER, AUTHORED_STANDING_SEEK)


func _commit_optical_and_calibrate() -> bool:
	# Commit optical mode through bilateral optical agreement and place both hands at their rest wrists with
	# an open tracked shape so the modifier's optical session stages the shared projection binding.
	if not bool(_driver.call("CommitOpticalMode")):
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to commit the optical mode")
		return false

	_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(RIGHT_WRIST_REST))
	_driver.call("SetExactOpticalWristWorldSample", "Left", _left_wrist_sample(LEFT_WRIST_REST))
	_driver.call("InjectTrackedHandPose", "Right", 0.15, "")
	_driver.call("InjectTrackedHandPose", "Left", 0.15, "")

	await _wait_physics(VRIK_SETTLE_PHYSICS_TICKS)

	_require(
		String(_driver.call("GetCommittedMode")) == "Optical",
		"the committed hand-pose mode must be Optical before the scenarios")

	var ball_calibration: Array = _driver.call("CalibrateGrabFlex", "Right", GRAB_BALL_ANIMATION_PATH)
	if ball_calibration.is_empty():
		SceneUtils.fatal_error_and_quit(
			"CTRL-002 runner: ball calibration failed: %s" % _driver.get("LastError"))
		return false

	var stick_calibration: Array = _driver.call("CalibrateGrabFlex", "Right", GRAB_PIPE_ANIMATION_PATH)
	if stick_calibration.is_empty():
		SceneUtils.fatal_error_and_quit(
			"CTRL-002 runner: stick calibration failed: %s" % _driver.get("LastError"))
		return false

	_ball_closed_flex = ball_calibration[0]
	_ball_open_flex = ball_calibration[1]
	_stick_closed_flex = stick_calibration[0]
	_stick_open_flex = stick_calibration[1]

	_ball_authored_reference = _driver.call(
		"SampleAuthoredGrabReference", "Right", GRAB_BALL_ANIMATION_PATH)
	_pipe_authored_reference = _driver.call(
		"SampleAuthoredGrabReference", "Right", GRAB_PIPE_ANIMATION_PATH)
	_require(not _ball_authored_reference.is_empty() and not _pipe_authored_reference.is_empty(),
		"authored grab reference sampling must succeed for both candidate animations")

	# Calibration leaves high-flex samples injected; restore the open rest shape before the scenarios.
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	_driver.call("InjectTrackedHandPose", "Left", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS)
	await _assert_natural_rest_diagnostics()
	await _assert_reach_qualification()

	return true


func _validate_cameras_and_markers() -> void:
	for camera_name: String in REQUIRED_CAMERAS:
		if _photobooth.get_camera_rig(camera_name) == null:
			SceneUtils.fatal_error_and_quit("CTRL-002 runner: missing camera rig %s" % camera_name)
			return

	# Directional sanity (pre-capture): the character faces -Z, so every rest, item, and contact marker must
	# sit in front of the character, matching the close-up cameras placed on the -Z side.
	for marker_name: String in REQUIRED_MARKERS:
		var marker: Node3D = _photobooth.get_marker(marker_name) as Node3D
		if marker.global_position.z >= 0.0:
			SceneUtils.fatal_error_and_quit(
				"CTRL-002 runner: marker %s is not in front of the character (z >= 0)" % marker_name)
			return

	# The stick contact must be a clearly non-centre point along the stick axis.
	var stick_item: Vector3 = _photobooth.get_marker("StickItem").global_position
	var stick_contact: Vector3 = _photobooth.get_marker("StickContact").global_position
	_require(
		stick_contact.distance_to(stick_item) >= 0.12,
		"the stick contact marker must sit a non-centre distance along the stick axis")

	# The ball must sit comfortably inside its grab reach of the right-hand rest marker, not at the
	# acquisition boundary.
	var right_rest: Vector3 = _photobooth.get_marker("RightHandRest").global_position
	var ball_item: Vector3 = _photobooth.get_marker("BallItem").global_position
	var ball_reach_margin := BALL_REACH_METRES - right_rest.distance_to(ball_item)
	_require(
		ball_reach_margin >= BALL_ACQUISITION_MARGIN_MIN_METRES
			and ball_reach_margin <= BALL_ACQUISITION_MARGIN_MAX_METRES,
		"the ball must sit comfortably inside the spherical grab reach of the right-hand rest marker "
			+ "(margin %.3f m)" % ball_reach_margin)

	# The open-hand stick waypoint must sit inside the cylindrical snap zone of the authored contact.
	var stick_margin := STICK_SNAP_METRES - RIGHT_WRIST_STICK_APPROACH.distance_to(STICK_CONTACT_POSITION)
	_require(
		stick_margin >= STICK_ACQUISITION_MARGIN_MIN_METRES,
		"the stick open-hand waypoint must sit comfortably inside the snap zone (margin %.3f m)" % stick_margin)

	print("CTRL002_MARKERS right_rest=%s left_rest=%s ball=%s stick=%s stick_contact=%s ball_margin=%.3f stick_margin=%.3f" % [
		right_rest, _photobooth.get_marker("LeftHandRest").global_position, ball_item, stick_item,
		stick_contact, ball_reach_margin, stick_margin])


func _assert_reach_qualification() -> void:
	# Qualify the authored fixture destinations against the skeleton-measured arm envelope before
	# any grab input is injected (mirrors the C# reach qualification; read-only rest measurements).
	# A destination is reachable when the ball/contact sits a hand-length inside the bare envelope
	# from the settled shoulder, leaving body lean unstrained.
	var shoulder := _joint_world_position("RightUpperArm")
	var upper_arm := _bone_rest_length("RightUpperArm", "RightLowerArm")
	var forearm := _bone_rest_length("RightLowerArm", "RightHand")
	var envelope := upper_arm + forearm
	_require(envelope > 0.35 and envelope < 0.6,
		"the measured rest arm envelope must be plausible (measured %.3f m)" % envelope)

	var ball_distance := shoulder.distance_to(BALL_ITEM_POSITION)
	_require(
		ball_distance + 0.10 <= envelope + REACH_QUALIFICATION_BODY_LEAN_ALLOWANCE_METRES,
		"the authored ball must sit a hand-length inside the usable arm envelope "
			+ "(%.3f m vs envelope %.3f m)" % [ball_distance, envelope])

	var stick_distance := shoulder.distance_to(STICK_CONTACT_POSITION)
	_require(
		stick_distance + 0.10 <= envelope + REACH_QUALIFICATION_BODY_LEAN_ALLOWANCE_METRES,
		"the authored stick contact must sit a hand-length inside the usable arm envelope "
			+ "(%.3f m vs envelope %.3f m)" % [stick_distance, envelope])

	print("CTRL002_REACH_QUALIFICATION shoulder=%s envelope=%.4f ball_dist=%.4f stick_contact_dist=%.4f" % [
		shoulder, envelope, ball_distance, stick_distance])


func _capture_framing_pass() -> void:
	for marker_name: String in REQUIRED_MARKERS:
		_photobooth.get_marker(marker_name).visible = true

	await SceneUtils.wait_frames(self, 2)
	for camera_name: String in REQUIRED_CAMERAS:
		var rig: CameraRig = _photobooth.get_camera_rig(camera_name)
		await rig.capture_screenshot("%s/framing/%s_markers.jpg" % [_output_root, _to_slug(camera_name)])

	for marker_name: String in REQUIRED_MARKERS:
		_photobooth.get_marker(marker_name).visible = false


func _scenario_open_hand_near_ball() -> void:
	# Optical mode with an open tracked hand near the ball: recognition stays idle — no pending grab, no
	# approach override (CTRL-002 TR10; XR-002 TR47). The authored basis presents the open palm toward
	# the ball with a near-neutral wrist and a bent elbow.
	_set_stick_visible(false)
	_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(RIGHT_WRIST_REST))
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + VRIK_SETTLE_PHYSICS_TICKS)
	_log_hand_state("open_hand_near_ball")

	_require(
		_driver.call("GetHandLifecycle", "Right") == "None",
		"an open tracked hand near the ball must stay idle (no pending grab)")
	_require(
		not bool(_driver.call("IsGrabOverrideActive", "Right")),
		"no approach override may activate while the hand stays open")
	_require(
		String(_driver.call("GetCurrentCandidateAnimationName", "Right")) == "Grab-ball-40",
		"the ball must be the observed best candidate for the right hand at rest")
	_assert_palm_alignment("ball_open", BALL_ITEM_POSITION)

	await _capture("01_open_hand_near_ball",
		"open hand rests palm-side of the ball at waist height; palm faces the ball, elbow bent, no crouch")
	await _capture_camera("01_open_hand_near_ball", "ProfileCamera",
		"profile view: wrist near-neutral (hand continues the forearm), palm rolled toward the ball")
	_ball_open_finger_reference = _snapshot_finger_rotations("Right")


func _scenario_ball_gradual_closure() -> void:
	# Gradual closure: authored fractions of the calibrated open->closed band, each still below the
	# grab edge, so the sequence shows the hand visibly closing in stages before any grab begins.
	for fraction_index in CLOSURE_CAPTURE_FRACTIONS.size():
		var fraction: float = CLOSURE_CAPTURE_FRACTIONS[fraction_index]
		var flex := lerpf(_ball_open_flex, _ball_closed_flex, fraction)
		_driver.call("InjectTrackedHandPose", "Right", flex, "")
		await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS)
		_require(
			_driver.call("GetHandLifecycle", "Right") == "None",
			"a partial closure below the calibrated grab edge must not begin a grab")
		var label := "01b_ball_closure_partial_%d" % [int(round(fraction * 100))]
		await _capture(label,
			"fingers visibly more cupped than the open pose (fraction %.0f%% of the closure band)" % (fraction * 100))

	# Restore the open rest shape before the commit scenario.
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS)


func _scenario_ball_commit() -> void:
	# A closure ramp past the grab threshold, held for the stability interval, begins the grab with optical
	# provenance; the VRIK approach settles it into a held grab whose finger pose is authored by the grab
	# animation once the commit blend window completes (INTR-002 R59-60; XR-002 TR31, TR46-TR49).
	var drive: Dictionary = await _drive_authored_input_until_pending(
		RIGHT_WRIST_REST, RIGHT_WRIST_HELD, _ball_open_flex, _ball_closed_flex, _ball)
	var pending_reached: bool = bool(drive.get("pending", false))
	_require(pending_reached, "closing near the ball must begin a pending grab")
	var pending_ball_transform := _ball.global_transform
	var pending_ball_parent := _ball.get_parent()
	# Deliberately capture at the first assisted physics checkpoint, before moving the live mock wrist to the
	# attachment target used for the subsequent commit. This preserves visible ball/hand separation in the matched
	# overview while showing a real, partial PendingAssistance blend rather than an almost-committed grip.
	await _wait_physics(1)
	_require(
		_driver.call("GetHandLifecycle", "Right") == "Pending",
		"the ball must remain pending at the early assisted checkpoint")
	_require(
		_driver.call("GetHandGrabInputSource", "Right") == "Optical",
		"the early pending ball must retain Optical provenance")
	_require(
		_driver.call("GetGrabPoseBlendPhase", "Right") == "PendingAssistance",
		"the pending optical hand must be visually assisted toward the candidate reference")
	_require(
		not bool(_driver.call("IsOpticalGrabHeld", "Right")),
		"pending assistance must not claim committed authored-pose ownership")
	_require(pending_ball_parent == _photobooth.get_node("Items"), "the pending ball must remain unparented")
	_require(_ball.get_parent() == pending_ball_parent,
		"the early pending ball must remain with its original parent (direct attachment has not committed)")
	_require(_ball.global_position.distance_to(pending_ball_transform.origin) <= STATIONARY_POSITION_TOLERANCE_METRES,
		"the ball must be stationary while the optical hand approaches")
	_require(
		_mean_angle_against_snapshot("Right", _ball_open_finger_reference) >= PENDING_PARTIAL_MIN_CUP_RADIANS,
		"the early pending fingers must be materially more cupped than the open baseline")
	_require(
		_mean_finger_angle("Right", _ball_authored_reference) >= PENDING_PARTIAL_MIN_GAP_TO_HELD_RADIANS,
		"the early pending fingers must remain materially less closed than the committed ball grip")
	_log_hand_state("ball_pending_assistance")
	_assert_palm_alignment("ball_pending", BALL_ITEM_POSITION)
	# Multi-view pending evidence: the shared close framing plus the interaction overview and profile.
	await _capture_camera("02a_ball_pending_assistance_early_partial", "PendingCommitCamera",
		"hand visibly between open and committed grip; ball separate, stationary, unattached")
	await _capture_camera("02a_ball_pending_assistance_early_partial", "InteractionOverviewCamera",
		"standing body reaches toward the ball; visible hand/ball separation; no crouch")
	await _capture_camera("02a_ball_pending_assistance_early_partial", "RightHandCamera",
		"fingers partially cupped toward the authored grip, palm toward the ball")
	await _capture_camera("02a_ball_pending_assistance_early_partial", "ProfileCamera",
		"profile view: approaching hand keeps a near-neutral wrist with the palm toward the ball")

	var trace: Array = drive["trace"]
	var held_reached := await _continue_authored_input_until_held(
		RIGHT_WRIST_REST, RIGHT_WRIST_HELD, _ball_closed_flex, _ball,
		int(drive["input_frame"]), trace)
	_require(held_reached, "the pending ball grab must settle into a held grab")
	_assert_commit_trace_continuity(trace, "ball")

	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS)

	_require(_driver.call("GetHandLifecycle", "Right") == "Held", "the ball grab must be held")
	_require(
		_driver.call("GetHandGrabInputSource", "Right") == "Optical",
		"the held ball grab must carry optical provenance")
	_require(
		String(_driver.call("GetHeldGrabbableName", "Right")) == "Ball",
		"the held item must be the ball")
	_require(
		String(_driver.call("GetActiveGrabAnimationName", "Right")) == "Grab-ball-40",
		"the active grab animation must be the authored ball grab")
	_require(
		_driver.call("GetGrabPoseBlendPhase", "Right") == "HeldSuppressed",
		"the commit blend window must have completed into held suppression")
	_require(
		bool(_driver.call("IsOpticalGrabHeld", "Right")),
		"arbitration must report the right-hand optical grab as held")
	_require(
		_max_finger_angle("Right", _ball_authored_reference) <= AUTHORED_MATCH_TOLERANCE_RADIANS,
		"the held finger pose must match the authored ball grab reference")
	_require(_ball.get_parent() == _player.get_node("Female/GeneralSkeleton/RightHand"),
		"the direct attachment gate must parent the committed ball to the real right-hand attachment")
	var post_gate_ball_transform := _ball.global_transform
	await _wait_physics(3)
	_require(_ball.global_position.distance_to(post_gate_ball_transform.origin) <= STATIONARY_POSITION_TOLERANCE_METRES,
		"the ball must not late-snap after the direct attachment gate has committed")

	_assert_final_pose_telemetry("ball", BALL_ITEM_POSITION, true)

	await _capture_camera("02_ball_commit", "RightHandCamera",
		"closed authored ball grip: ball cradled on the palm side, fingers curled over it")
	await _capture_camera("02_ball_commit", "InteractionOverviewCamera",
		"standing character holding the ball at waist height with a bent elbow; no crouch")
	await _capture_camera("02_ball_commit", "PendingCommitCamera",
		"ball in hand close framing, fingers on the ball surface")
	await _capture_camera("02_ball_commit", "ProfileCamera",
		"profile view: held hand continues the forearm line, ball seated on the palm side")


func _scenario_ball_held_movement() -> void:
	# Held ball with a small authored wrist movement: the attached ball visibly follows the hand
	# (CTRL-002 UR4 continuation). The displacement is an independently authored input.
	_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(RIGHT_WRIST_HELD))
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS)
	var ball_before := _ball.global_position
	var held_start := RIGHT_WRIST_HELD
	var held_end := RIGHT_WRIST_HELD + HELD_WRIST_MOVEMENT_DELTA
	for frame_index in HELD_MOVEMENT_DRIVE_FRAMES:
		var alpha := float(frame_index + 1) / HELD_MOVEMENT_DRIVE_FRAMES
		_driver.call("SetExactOpticalWristWorldSample", "Right",
			_ball_wrist_sample(held_start.lerp(held_end, alpha)))
		_driver.call("InjectTrackedHandPose", "Right", _ball_closed_flex, "")
		await physics_frame
		await process_frame

	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS)
	_require(
		_driver.call("GetHandLifecycle", "Right") == "Held",
		"the authored held movement must keep the ball grab held")
	var ball_follow := _ball.global_position.distance_to(ball_before)
	_require(
		ball_follow >= 0.4 * HELD_WRIST_MOVEMENT_DELTA.length(),
		"the held ball must visibly follow the authored wrist movement (followed %.3f m of the %.3f m input)" % [
			ball_follow, HELD_WRIST_MOVEMENT_DELTA.length()])
	print("CTRL002_HELD_MOVEMENT wrist_delta=%.3f ball_follow=%.3f" % [
		HELD_WRIST_MOVEMENT_DELTA.length(), ball_follow])

	await _capture("02d_ball_held_small_wrist_movement",
		"ball still palm-centred in the grip after the small hand movement; arm pose shifted visibly")

	# Restore the terminal wrist and settle before the next scenario.
	_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(RIGHT_WRIST_HELD))
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS)


func _scenario_ball_held_hidden_open() -> void:
	# While held, inject a drastically different tracked pose (a further clench beyond the reference
	# articulation): no release triggers, and the bones stay fixed at the authored pose — the visible cue is
	# the unchanging grip (CTRL-002 UR4; XR-002 TR48, TR31).
	var held_pose: Dictionary = _snapshot_finger_rotations("Right")

	_driver.call("InjectTrackedHandPose", "Right", _ball_closed_flex * OVER_CLENCH_SCALE, "")
	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS * 2)

	_require(
		_driver.call("GetHandLifecycle", "Right") == "Held",
		"an over-clench beyond the reference articulation must not release the held ball")
	_require(
		String(_driver.call("GetHeldGrabbableName", "Right")) == "Ball",
		"the ball must still be held after the over-clench")
	_require(
		_max_angle_against_snapshot("Right", held_pose) <= HELD_STABLE_TOLERANCE_RADIANS,
		"the held finger pose must stay fixed through the over-clench")
	var over_clench_projection: Array = _driver.call("ProjectTrackedFingerRotations", "Right")
	_require(
		_max_finger_angle("Right", over_clench_projection) >= DISTINCT_POSE_RADIANS,
		"anomaly guard: the held bones must NOT match the projected over-clench pose")

	await _capture("03_ball_held_hidden_open",
		"grip unchanged through the hidden over-clench; ball still cradled palm-side")


func _scenario_ball_release() -> void:
	# A stable aggregate opening for the stability window releases through the ordinary restoration; the
	# blend-out completes and the hand resumes tracking the open pose (INTR-002 R59, R61).
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + BLEND_WAIT_PHYSICS_TICKS)

	_require(
		_driver.call("GetHandLifecycle", "Right") == "None",
		"a stable open must release the held ball")
	_require(
		String(_driver.call("GetHeldGrabbableName", "Right")) == "",
		"no item may remain held after the release")
	_require(
		_driver.call("GetGrabPoseBlendPhase", "Right") == "Tracking",
		"the release blend-out must have completed back into tracking")
	_require(
		not bool(_driver.call("IsOpticalGrabHeld", "Right")),
		"arbitration must withdraw the held state after the release")

	var open_projection: Array = _driver.call("ProjectTrackedFingerRotations", "Right")
	_require(
		_max_finger_angle("Right", open_projection) <= TRACKED_MATCH_TOLERANCE_RADIANS,
		"after the blend-out the hand must track the projected open pose")

	await _capture("04_ball_release",
		"hand open again, ball left in place (no longer attached)")


func _scenario_stick_commit_offset() -> void:
	# Move the right hand near a clearly non-centre point along the stick and close: the same generic
	# recognition path commits the authored pipe grab, producing a visibly different grip than the ball
	# (CTRL-002 TR22; XR-002 TR50).
	_stick.global_transform = _stick_authored_transform
	_stick.force_update_transform()
	_set_stick_visible(true)
	# Fixture-only competitor control for the matched comparison: the ball is removed from candidate
	# discovery for the stick scenario (restored afterwards), mirroring the C# stick fixtures.
	_ball.global_position = Vector3(10.0, 10.0, 10.0)
	_ball.force_update_transform()
	_driver.call("SetExactOpticalWristWorldSample", "Right", _stick_wrist_sample(RIGHT_WRIST_STICK_APPROACH))
	_driver.call("InjectTrackedHandPose", "Right", _stick_open_flex, "")
	await _wait_physics(12)
	_driver.call("SetExactOpticalWristWorldSample", "Right", _stick_wrist_sample(RIGHT_WRIST_STICK_APPROACH))
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + VRIK_SETTLE_PHYSICS_TICKS)

	_require(
		String(_driver.call("GetCurrentCandidateAnimationName", "Right")) == "Grab-pipe-10",
		"the stick must be the observed best candidate at the offset contact position")
	_assert_palm_alignment("stick_open", STICK_CONTACT_POSITION)

	var drive: Dictionary = await _drive_authored_input_until_pending(
		RIGHT_WRIST_STICK_APPROACH, RIGHT_WRIST_STICK, _stick_open_flex, _stick_closed_flex, _stick)
	var pending_reached: bool = bool(drive.get("pending", false))
	_require(pending_reached, "closing near the stick must begin a pending grab")
	await _wait_physics(1)
	_require(
		_driver.call("GetHandLifecycle", "Right") == "Pending",
		"the stick must remain pending at the early assisted checkpoint")
	_require(
		_driver.call("GetGrabPoseBlendPhase", "Right") == "PendingAssistance",
		"the pending optical hand must be assisted toward the pipe reference")
	_assert_palm_alignment("stick_pending", STICK_CONTACT_POSITION)
	await _capture_camera("05a_stick_pending_assistance_early_partial", "StickCamera",
		"hand partially closed around the off-centre stick contact; stick stationary")
	await _capture_camera("05a_stick_pending_assistance_early_partial", "InteractionOverviewCamera",
		"standing reach to the off-centre stick grip point; no crouch")
	await _capture_camera("05a_stick_pending_assistance_early_partial", "ProfileCamera",
		"profile view: stick lies along the forearm line; wrist stays near-neutral through the approach")
	# Cancel nothing: continue the same authored input into the commit.
	var trace: Array = drive["trace"]
	var held_reached := await _continue_authored_input_until_held(
		RIGHT_WRIST_STICK_APPROACH, RIGHT_WRIST_STICK, _stick_closed_flex, _stick,
		int(drive["input_frame"]), trace)
	_require(held_reached, "the pending stick grab must settle into a held grab")
	_assert_commit_trace_continuity_with_hand_angle(trace, "stick", STICK_HAND_ANGULAR_TOLERANCE_DEGREES)

	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS)

	_require(
		String(_driver.call("GetHeldGrabbableName", "Right")) == "Stick",
		"the held item must be the stick")
	_require(
		String(_driver.call("GetActiveGrabAnimationName", "Right")) == "Grab-pipe-10",
		"the active grab animation must be the authored pipe grab")
	_require(
		_driver.call("GetGrabPoseBlendPhase", "Right") == "HeldSuppressed",
		"the stick commit blend window must have completed")
	_require(
		_max_finger_angle("Right", _pipe_authored_reference) <= AUTHORED_MATCH_TOLERANCE_RADIANS,
		"the held finger pose must match the authored pipe grab reference")
	_require(
		_max_angle_between_references(_pipe_authored_reference, _ball_authored_reference)
			>= DISTINCT_POSE_RADIANS,
		"anomaly guard: the pipe grip must differ materially from the ball grip")

	# Offset guard: the solved hand must grip at the contact point, not the stick centre.
	var hand_position: Vector3 = _driver.call("GetHandAttachmentWorldPosition", "Right")
	var stick_centre: Vector3 = _photobooth.get_marker("StickItem").global_position
	var stick_contact: Vector3 = _photobooth.get_marker("StickContact").global_position
	_require(
		hand_position.distance_to(stick_contact) + STICK_OFFSET_MIN_MARGIN_METRES
			<= hand_position.distance_to(stick_centre),
		"the held hand must sit closer to the offset contact point than to the stick centre")

	_assert_final_pose_telemetry("stick", STICK_CONTACT_POSITION, false)

	await _capture_camera("05_stick_commit_offset", "StickCamera",
		"pipe grip closed around the stick at the off-centre contact; stick passing through the fist")
	await _capture_camera("05_stick_commit_offset", "InteractionOverviewCamera",
		"standing character holding the stick off-centre; natural arm posture")
	await _capture_camera("05_stick_commit_offset", "RightHandCamera",
		"wrapped pipe grip distinct from the ball grip")
	await _capture_camera("05_stick_commit_offset", "ProfileCamera",
		"profile view: held stick continues the forearm line with a near-neutral wrist")

	# Release the stick, park it away from discovery, and restore the ball for the tracking-loss
	# scenario.
	_driver.call("InjectTrackedHandPose", "Right", _stick_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + BLEND_WAIT_PHYSICS_TICKS)
	_require(
		_driver.call("GetHandLifecycle", "Right") == "None",
		"a stable open must release the held stick")
	_set_stick_visible(false)
	_stick.global_position = Vector3(10.0, 10.0, 10.0)
	_stick.force_update_transform()
	_ball.global_transform = Transform3D(Basis.IDENTITY, BALL_ITEM_POSITION)
	_ball.force_update_transform()


func _scenario_held_tracking_loss() -> void:
	# Re-grab the ball, then invalidate the right hand's optical observations entirely: the grab is
	# preserved — still held, pose fixed at the authored reference, arbitration unchanged (INTR-002 R59;
	# XR-002 TR51).
	_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(RIGHT_WRIST_REST))
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + VRIK_SETTLE_PHYSICS_TICKS)
	_require(
		String(_driver.call("GetCurrentCandidateAnimationName", "Right")) == "Grab-ball-40",
		"the ball must be the best candidate again after returning to rest")

	var drive: Dictionary = await _drive_authored_input_until_pending(
		RIGHT_WRIST_REST, RIGHT_WRIST_HELD, _ball_open_flex, _ball_closed_flex, _ball)
	var trace: Array = drive["trace"]
	var held_reached := false
	if bool(drive.get("pending", false)):
		held_reached = await _continue_authored_input_until_held(
			RIGHT_WRIST_REST, RIGHT_WRIST_HELD, _ball_closed_flex, _ball,
			int(drive["input_frame"]), trace)
	_require(held_reached, "the re-grab must settle into a held ball grab")
	_assert_commit_trace_continuity(trace, "tracking-loss setup ball")
	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS)

	var held_pose: Dictionary = _snapshot_finger_rotations("Right")
	_require(
		_max_finger_angle("Right", _ball_authored_reference) <= AUTHORED_MATCH_TOLERANCE_RADIANS,
		"the re-grab held pose must match the authored ball reference before the loss")

	_driver.call("ClearHandJointSamples", "Right")
	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS * 2)

	_require(
		_driver.call("GetHandLifecycle", "Right") == "Held",
		"tracking loss while held must preserve the grab")
	_require(
		String(_driver.call("GetHeldGrabbableName", "Right")) == "Ball",
		"the ball must still be held through the tracking loss")
	_require(
		bool(_driver.call("IsOpticalGrabHeld", "Right")),
		"arbitration must keep the held state through the tracking loss")
	_require(
		_max_angle_against_snapshot("Right", held_pose) <= HELD_STABLE_TOLERANCE_RADIANS,
		"the held finger pose must stay fixed through the tracking loss")

	await _capture("06_held_tracking_loss",
		"grip and held ball unchanged through the tracking loss")


func _scenario_mode_switch_release() -> void:
	# While held, commit both hands to Controller: the explicit mode transition releases the optical grab —
	# item dropped in place, arbitration withdrawn, hand returns to its authored pose (INTR-002 R58-59).
	if not bool(_driver.call("CommitControllerMode")):
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to commit the controller mode")
		return

	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS)

	_require(
		_driver.call("GetHandLifecycle", "Right") == "None",
		"the explicit mode switch must release the held ball")
	_require(
		String(_driver.call("GetHeldGrabbableName", "Right")) == "",
		"no item may remain held after the mode switch")
	_require(
		_driver.call("GetHandGrabInputSource", "Right") == "None",
		"the grab provenance must clear with the mode-switch release")
	_require(
		not bool(_driver.call("IsOpticalGrabHeld", "Right")),
		"arbitration must withdraw the held state on the mode switch")
	_require(
		String(_driver.call("GetCommittedMode")) == "Controller",
		"the committed mode must be Controller after the switch")

	await _capture("07_mode_switch_release",
		"ball dropped in place; hand released to the open controller pose")


func _scenario_independent_hands() -> void:
	# Re-enter optical mode and hold the ball with the right hand while the left hand tracks an open pose
	# that moves to a new wrist position: the right hand stays fixed at the authored pose while the left
	# keeps following its live tracked data (CTRL-002 UR5; XR-002 TR22).
	if not bool(_driver.call("CommitOpticalMode")):
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to re-commit the optical mode")
		return

	_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(RIGHT_WRIST_REST))
	_driver.call("SetExactOpticalWristWorldSample", "Left", _left_wrist_sample(LEFT_WRIST_REST))
	_driver.call("InjectTrackedHandPose", "Left", _ball_open_flex, "")
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + VRIK_SETTLE_PHYSICS_TICKS)
	var drive: Dictionary = await _drive_authored_input_until_pending(
		RIGHT_WRIST_REST, RIGHT_WRIST_HELD, _ball_open_flex, _ball_closed_flex, _ball)
	var trace: Array = drive["trace"]
	var held_reached := false
	if bool(drive.get("pending", false)):
		held_reached = await _continue_authored_input_until_held(
			RIGHT_WRIST_REST, RIGHT_WRIST_HELD, _ball_closed_flex, _ball,
			int(drive["input_frame"]), trace)
	_require(held_reached, "the right hand must hold the ball again")
	_assert_commit_trace_continuity(trace, "independent-hands ball")
	await _wait_physics(BLEND_WAIT_PHYSICS_TICKS)

	_require(
		_max_finger_angle("Right", _ball_authored_reference) <= AUTHORED_MATCH_TOLERANCE_RADIANS,
		"the right hand must stay at the authored ball pose while the left hand moves")

	# Move the left hand: new wrist position, same open tracked shape.
	_driver.call("SetExactOpticalWristWorldSample", "Left", _left_moved_wrist_sample(LEFT_WRIST_MOVED))
	_driver.call("InjectTrackedHandPose", "Left", _ball_open_flex, "")
	await _wait_physics(STABILITY_WAIT_PHYSICS_TICKS + VRIK_SETTLE_PHYSICS_TICKS)

	_require(
		_driver.call("GetHandLifecycle", "Right") == "Held",
		"the right hand must keep holding while the left hand tracks")
	_require(
		_max_finger_angle("Right", _ball_authored_reference) <= AUTHORED_MATCH_TOLERANCE_RADIANS,
		"the right-hand held pose must be untouched by left-hand motion")
	_require(
		_driver.call("GetHandLifecycle", "Left") == "None",
		"the left hand stays idle (no left-hand candidate in reach)")
	var left_projection: Array = _driver.call("ProjectTrackedFingerRotations", "Left")
	_require(
		_max_finger_angle("Left", left_projection) <= TRACKED_MATCH_TOLERANCE_RADIANS,
		"the left hand must keep tracking its live projected pose")
	var left_geometry: Dictionary = _measure_hand_geometry("Left")
	_require(
		left_geometry["wrist_flex_degrees"] <= WRIST_NEUTRAL_MAX_DEGREES,
		"the moved left wrist must stay near-neutral (measured %.1f degrees)" % left_geometry["wrist_flex_degrees"])
	print("CTRL002_LEFT_MOVED wrist=%s wrist_deg=%.1f extension=%.3f" % [
		left_geometry["wrist"], left_geometry["wrist_flex_degrees"], left_geometry["extension"]])

	# The released stick is fixture-hidden so the overview proves the right ball grip and live open left hand.
	_set_stick_visible(false)
	await _capture("08_independent_hands",
		"right hand holds the ball while the open left hand visibly moved to its new position")


func _scenario_controller_pending_without_assistance() -> void:
	# Controller provenance uses the same overview framing as the optical pending image. Reset only fixture state,
	# then begin the installed hand lifecycle as Controller; no optical arbiter or modifier assistance may appear.
	_set_stick_visible(false)
	# Remove the released stick from fixture discovery as well as rendering so this matched comparison is unambiguously
	# controller-pending Ball rather than an incidental nearer pipe candidate.
	_stick.global_position = Vector3(10.0, 10.0, 10.0)
	_stick.force_update_transform()
	_ball.global_transform = _photobooth.get_marker("BallItem").global_transform
	_ball.force_update_transform()
	_driver.call("SetControllerHandTransform", "Right", Transform3D(Basis.IDENTITY, RIGHT_CONTROLLER_PENDING_SOURCE))
	# Re-pin the authored controller wrist every physics tick against the XR-origin swing while the
	# body settles (the same per-frame re-pinning the C# controller fixtures use), so the canonical
	# controller sample resolves the authored source stably.
	for _tick in VRIK_SETTLE_PHYSICS_TICKS:
		_driver.call("SetControllerHandTransform", "Right", _ball_wrist_sample(RIGHT_CONTROLLER_PENDING_SOURCE))
		await physics_frame
	var original_parent := _ball.get_parent()
	var stationary_position := _ball.global_position
	print("CTRL002_CONTROLLER_PENDING_PREP target=%s attachment=%s ball=%s candidate=%s" % [
		_driver.call("GetHandTargetWorldPosition", "Right"),
		_driver.call("GetHandAttachmentWorldPosition", "Right"),
		_ball.global_position,
		String(_driver.call("GetCurrentCandidateAnimationName", "Right"))])
	_require(bool(_driver.call("BeginControllerGrab", "Right")),
		"the installed right hand must begin a controller-provenance pending ball grab")
	_require(_driver.call("GetHandLifecycle", "Right") == "Pending",
		"the controller ball grab must be pending before its direct attachment settles")
	_require(_driver.call("GetHandGrabInputSource", "Right") == "Controller",
		"the controller pending grab must retain Controller provenance")
	_require(_driver.call("GetGrabPoseBlendPhase", "Right") == "Tracking",
		"controller pending must not activate optical pending assistance")
	_require(not bool(_driver.call("IsOpticalGrabHeld", "Right")),
		"controller pending must not publish optical held ownership")
	_require(bool(_driver.call("IsGrabOverrideActive", "Right")),
		"controller pending must use the real VRIK provider approach")
	_require(_ball.get_parent() == original_parent,
		"controller pending must leave the ball unparented")
	_require(_ball.global_position.distance_to(stationary_position) <= STATIONARY_POSITION_TOLERANCE_METRES,
		"controller pending must leave the ball stationary")
	await _capture_camera("07a_controller_pending_no_assistance", "InteractionOverviewCamera",
		"controller-pending comparison: ball stationary, hand approaching, no optical assistance blend")
	_driver.call("InjectTrackedHandPose", "Right", _ball_open_flex, "")
	# Controller input has no optical recogniser opening edge, so explicitly cancel this fixture-only pending state.
	_player.get_node("Hands/RightHand").call("CancelPendingGrab")
	await _wait_physics(2)
	_require(_driver.call("GetHandLifecycle", "Right") == "None", "the controller pending comparison must clean up")


func _drive_authored_input_until_pending(
		wrist_start: Vector3,
		wrist_end: Vector3,
		open_flex: float,
		closed_flex: float,
		item: Node3D,
		wrist_basis: Basis = RIGHT_WRIST_SAMPLE_BASIS) -> Dictionary:
	var trace: Array = []
	_start_temporal_trace(trace, item)
	for input_frame in APPROACH_BUDGET_PHYSICS_TICKS:
		var closure_alpha := clampf(float(input_frame + 1) / CLOSURE_RAMP_FRAMES, 0.0, 1.0)
		var wrist_alpha := clampf(
			float(input_frame + 1 - WRIST_TRAJECTORY_DELAY_FRAMES) / WRIST_TRAJECTORY_FRAMES,
			0.0, 1.0)
		var wrist := wrist_start.lerp(wrist_end, wrist_alpha)
		var flex := lerpf(open_flex, closed_flex, closure_alpha)
		_driver.call("SetExactOpticalWristWorldSample", "Right", Transform3D(wrist_basis, wrist))
		_driver.call("InjectTrackedHandPose", "Right", flex, "")
		await physics_frame
		await process_frame
		if _driver.call("GetHandLifecycle", "Right") == "Pending":
			return {"pending": true, "input_frame": input_frame + 1, "trace": trace}

	_stop_temporal_trace()
	return {"pending": false, "input_frame": APPROACH_BUDGET_PHYSICS_TICKS, "trace": trace}


func _continue_authored_input_until_held(
		wrist_start: Vector3,
		wrist_end: Vector3,
		closed_flex: float,
		item: Node3D,
		input_frame: int,
		trace: Array,
		wrist_basis: Basis = RIGHT_WRIST_SAMPLE_BASIS) -> bool:
	# Drive the frozen authored input until the grab is held AND the wrist trajectory has fully
	# completed: returning earlier would leave the wrist intent still ramping, and every post-commit
	# assertion (and capture) would observe the hand — and the attached item — mid-motion.
	var ramp_end_frame := WRIST_TRAJECTORY_DELAY_FRAMES + WRIST_TRAJECTORY_FRAMES
	var held_frames := 0
	var held_stable := false
	for offset in APPROACH_BUDGET_PROCESS_FRAMES:
		var authored_frame := input_frame + offset
		var wrist_alpha := clampf(
			float(authored_frame + 1 - WRIST_TRAJECTORY_DELAY_FRAMES) / WRIST_TRAJECTORY_FRAMES,
			0.0, 1.0)
		var wrist := wrist_start.lerp(wrist_end, wrist_alpha)
		_driver.call("SetExactOpticalWristWorldSample", "Right", Transform3D(wrist_basis, wrist))
		_driver.call("InjectTrackedHandPose", "Right", closed_flex, "")
		await physics_frame
		await process_frame
		if _driver.call("GetHandLifecycle", "Right") == "Held":
			held_frames += 1
			# The continuity trace covers the pending->commit transition plus the first held
			# samples (matching the C# unit's evidence window); after that the trace stops while
			# the input keeps driving, so post-commit observations happen after the wrist ramp has
			# completed rather than capturing the hand's smooth settling as fake discontinuities.
			if held_frames == INITIAL_HELD_TRACE_FRAMES and _active_temporal_item != null:
				_stop_temporal_trace()
			if held_frames >= INITIAL_HELD_TRACE_FRAMES and authored_frame + 1 >= ramp_end_frame:
				held_stable = true
		else:
			held_frames = 0
		if held_stable:
			break

	_stop_temporal_trace()
	if not held_stable:
		var hand := _player.get_node("Hands/RightHand")
		print("CTRL002_COMMIT_TIMEOUT lifecycle=%s abandonment=%s target=%s attachment=%s" % [
			String(_driver.call("GetHandLifecycle", "Right")),
			str(hand.get("LastPendingGrabAbandonmentReason")),
			_driver.call("GetHandTargetWorldPosition", "Right"),
			_driver.call("GetHandAttachmentWorldPosition", "Right")])
	return held_stable


func _start_temporal_trace(trace: Array, item: Node3D) -> void:
	_active_temporal_trace = trace
	_active_temporal_item = item
	if not process_frame.is_connected(_append_temporal_trace):
		process_frame.connect(_append_temporal_trace)


func _stop_temporal_trace() -> void:
	if process_frame.is_connected(_append_temporal_trace):
		process_frame.disconnect(_append_temporal_trace)
	_active_temporal_item = null


func _append_temporal_trace() -> void:
	if _active_temporal_item == null:
		return
	var sample: Dictionary = _driver.call("CaptureTemporalTraceSample", "Right", _active_temporal_item)
	_require(not sample.is_empty(), "the temporal trace sample must be available")
	_active_temporal_trace.append(sample)
	print("CTRL002_TEMPORAL_TRACE %s" % sample)


func _assert_commit_trace_continuity(trace: Array, label: String) -> void:
	_assert_commit_trace_continuity_with_hand_angle(
		trace, label, ADJACENT_HAND_DISCONTINUITY_TOLERANCE_DEGREES)


func _assert_commit_trace_continuity_with_hand_angle(
		trace: Array,
		label: String,
		hand_angle_tolerance_degrees: float) -> void:
	var held_index := -1
	for index in trace.size():
		if String(trace[index]["lifecycle"]) == "Held":
			held_index = index
			break

	_require(held_index >= 3, "%s trace must retain at least three Pending samples before Held" % label)
	_require(trace.size() - held_index >= INITIAL_HELD_TRACE_FRAMES,
		"%s trace must retain the initial Held frames" % label)
	for pending_index in range(held_index - 3, held_index):
		_require(String(trace[pending_index]["lifecycle"]) == "Pending",
			"%s trace must retain adjacent Pending samples through alignment" % label)
	_require(bool(trace[held_index - 1]["override_active"]),
		"%s final Pending sample must retain the provider override" % label)
	_require(not bool(trace[held_index]["override_active"]),
		"%s first Held sample must observe override clear" % label)
	_require(String(trace[held_index - 1]["item_parent"]) != String(trace[held_index]["item_parent"]),
		"%s first Held sample must observe the commit reparent" % label)

	for index in range(1, trace.size()):
		var previous: Dictionary = trace[index - 1]
		var current: Dictionary = trace[index]
		var previous_item: Transform3D = previous["item_transform"]
		var current_item: Transform3D = current["item_transform"]
		var previous_hand: Transform3D = previous["actual_attachment"]
		var current_hand: Transform3D = current["actual_attachment"]
		var item_distance := previous_item.origin.distance_to(current_item.origin)
		var item_angle := _transform_angle_degrees(previous_item, current_item)
		var hand_distance := previous_hand.origin.distance_to(current_hand.origin)
		var hand_angle := _transform_angle_degrees(previous_hand, current_hand)
		# The commit boundary (the reparent and the first post-clear chase frames) legitimately moves
		# the solved hand and the re-parented item faster: the override releases and the assisted
		# hand converges onto the authored wrist over the wrist-to-contact offset, exactly the phase
		# the C# continuity unit also bounds separately. Every other adjacent pair keeps the strict
		# discontinuity gate.
		var at_commit_boundary: bool = index >= held_index and index <= held_index + 2
		var hand_position_tolerance := ADJACENT_HAND_DISCONTINUITY_TOLERANCE_METRES if not at_commit_boundary \
			else COMMIT_BOUNDARY_HAND_TOLERANCE_METRES
		var item_position_tolerance := ADJACENT_ITEM_DISCONTINUITY_TOLERANCE_METRES if not at_commit_boundary \
			else COMMIT_BOUNDARY_ITEM_TOLERANCE_METRES
		_require(item_distance <= item_position_tolerance,
			"%s item discontinuity at samples %d->%d: %.5f m" % [label, index - 1, index, item_distance])
		_require(item_angle <= ADJACENT_ITEM_DISCONTINUITY_TOLERANCE_DEGREES,
			"%s item angular discontinuity at samples %d->%d: %.3f deg" % [label, index - 1, index, item_angle])
		_require(hand_distance <= hand_position_tolerance,
			"%s hand discontinuity at samples %d->%d: %.5f m" % [label, index - 1, index, hand_distance])
		_require(hand_angle <= hand_angle_tolerance_degrees,
			"%s hand angular discontinuity at samples %d->%d: %.3f deg" % [label, index - 1, index, hand_angle])


func _wait_physics(ticks: int) -> void:
	for _tick in ticks:
		await physics_frame


func _run_palm_sampling() -> void:
	# Placement survey: park both items far from discovery, then measure the solved hand geometry at
	# each authored candidate wrist position with a mid-closure finger shape (the finger curl gives an
	# independent palm-side disambiguation signal). Observations only — no input is derived from them.
	_set_stick_visible(false)
	_stick.global_position = Vector3(10.0, 10.0, 10.0)
	_stick.force_update_transform()
	_ball.global_position = Vector3(10.0, 10.0, 10.0)
	_ball.force_update_transform()

	var mid_flex := lerpf(_ball_open_flex, _ball_closed_flex, 0.5)
	_driver.call("SetExactOpticalWristWorldSample", "Left", _left_wrist_sample(LEFT_WRIST_REST))
	_driver.call("InjectTrackedHandPose", "Left", mid_flex, "")
	_ensure_joint_probes()
	await SceneUtils.wait_frames(self, 2)

	for sample_index in PALM_SAMPLE_RIGHT_CANDIDATES.size():
		var candidate: Vector3 = PALM_SAMPLE_RIGHT_CANDIDATES[sample_index]
		_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(candidate))
		_driver.call("InjectTrackedHandPose", "Right", mid_flex, "")
		for _tick in PALM_SAMPLE_SETTLE_TICKS:
			_driver.call("SetExactOpticalWristWorldSample", "Right", _ball_wrist_sample(candidate))
			await physics_frame
		await process_frame
		await process_frame
		_print_palm_sample("Right", candidate, sample_index)

	for sample_index in PALM_SAMPLE_LEFT_CANDIDATES.size():
		var candidate: Vector3 = PALM_SAMPLE_LEFT_CANDIDATES[sample_index]
		_driver.call("SetExactOpticalWristWorldSample", "Left", _left_wrist_sample(candidate))
		_driver.call("InjectTrackedHandPose", "Left", mid_flex, "")
		for _tick in PALM_SAMPLE_SETTLE_TICKS:
			_driver.call("SetExactOpticalWristWorldSample", "Left", _left_wrist_sample(candidate))
			await physics_frame
		await process_frame
		await process_frame
		_print_palm_sample("Left", candidate, 100 + sample_index)


func _measure_hand_geometry(side: String) -> Dictionary:
	# Read-only solved-pose geometry measured through BoneAttachment3D probes: direct
	# get_bone_global_pose reads resolve mid-pipeline (pre-modifier) data in this fixture, so every
	# observation must go through attachments (the same readout the production hand uses).
	# The confirmed rig hand frame maps the attachment local +X to the thumb side, +Y along the
	# fingers, and +Z onto the palm normal (the fingers curl toward local +Z), so the ANATOMICAL palm
	# normal used by the guards below is the solved attachment basis Z column.
	var wrist := _joint_world_position(side + "Hand")
	var elbow := _joint_world_position(side + "LowerArm")
	var shoulder := _joint_world_position(side + "UpperArm")
	var middle_proximal := _joint_world_position(side + "MiddleProximal")

	var hand_axis := (middle_proximal - wrist).normalized()
	var attachment: Transform3D = _driver.call("GetHandAttachmentWorldTransform", side)
	var palm_normal := attachment.basis.z.normalized()

	return {
		"wrist": wrist,
		"elbow": elbow,
		"shoulder": shoulder,
		"hand_axis": hand_axis,
		"palm_normal": palm_normal,
		"fingers_axis": attachment.basis.y.normalized(),
		"extension": shoulder.distance_to(wrist)
			/ (_bone_rest_length(side + "UpperArm", side + "LowerArm")
				+ _bone_rest_length(side + "LowerArm", side + "Hand")),
		"wrist_flex_degrees": rad_to_deg((wrist - elbow).normalized().angle_to(hand_axis)),
	}


# Joints measured through runtime BoneAttachment3D probes (solved-pose readouts).
const PROBE_JOINTS := [
	"UpperArm", "LowerArm", "Hand",
	"IndexProximal", "MiddleProximal", "MiddleIntermediate", "MiddleDistal",
	"LittleProximal", "ThumbProximal", "ThumbMetacarpal",
]
var _joint_probes: Dictionary = {}


func _ensure_joint_probes() -> void:
	if not _joint_probes.is_empty():
		return
	for side: String in ["Right", "Left"]:
		for joint: String in PROBE_JOINTS:
			var bone_name := side + joint
			var probe := BoneAttachment3D.new()
			probe.name = "Probe_%s" % bone_name
			probe.bone_name = bone_name
			_skeleton.add_child(probe)
			_joint_probes[bone_name] = probe
	# The hips carry no side prefix.
	var hip_probe := BoneAttachment3D.new()
	hip_probe.name = "Probe_Hips"
	hip_probe.bone_name = "Hips"
	_skeleton.add_child(hip_probe)
	_joint_probes["Hips"] = hip_probe


func _joint_world_position(bone_name: String) -> Vector3:
	var probe: BoneAttachment3D = _joint_probes.get(bone_name)
	_require(probe != null, "the fixture must expose the %s joint probe" % bone_name)
	return probe.global_position


func _print_palm_sample(side: String, injected: Vector3, sample_index: int) -> void:
	var geometry: Dictionary = _measure_hand_geometry(side)
	print(
		"CTRL002_PALM_SAMPLE idx=%d side=%s injected=%s solved_wrist=%s ext=%.3f wrist_deg=%.1f palm=%s fingers=%s" % [
			sample_index, side, injected, geometry["wrist"], geometry["extension"],
			geometry["wrist_flex_degrees"], geometry["palm_normal"], geometry["fingers_axis"]])


func _assert_natural_rest_diagnostics() -> void:
	# Natural-pose diagnostics at standing rest (read-only measurements, printed and asserted). The
	# pose state actually chosen is reported rather than mandated; the guards catch the two rejected
	# anomalies directly: the free-played crouch (hip collapse) and any head-target descent bias.
	# Arm and hip measurements read the solved pose through the joint probes.
	var head_target: Node3D = _player.get_node("IKTargets/Head") as Node3D
	var head: Node3D = _player.get_node("Female/GeneralSkeleton/Head") as Node3D
	var viewpoint: Node3D = _player.get_node("Female/GeneralSkeleton/Head/Viewpoint") as Node3D
	var head_bone_index := _skeleton.find_bone("Head")
	var head_rest := _skeleton.global_transform * _skeleton.get_bone_global_rest(head_bone_index)
	var head_target_rest := head_rest * viewpoint.transform
	var descent := head_target_rest.origin.y - head_target.global_position.y
	var root_before: Vector3 = _player.global_position
	await process_frame
	await process_frame
	var root_drift := root_before.distance_to(_player.global_position)
	var head_residual := head_target.global_position.distance_to(head.global_position)
	var rest_geometry: Dictionary = _measure_hand_geometry("Right")
	var hip := _joint_world_position("Hips")
	var extension: float = rest_geometry["extension"]
	var wrist_degrees: float = rest_geometry["wrist_flex_degrees"]

	_require(descent <= HEAD_TARGET_REST_DESCENT_MAX_METRES,
		"the head target must request the rest head rather than a descent (descent %.4f m)" % descent)
	_require(hip.y >= STANDING_HIP_MIN_HEIGHT_METRES,
		"the hips must stay in the standing band rather than the free-played crouch (hip y %.3f m)" % hip.y)
	_require(root_drift <= ROOT_DRIFT_MAX_METRES,
		"the player root must remain stable at standing rest")
	_require(head_residual <= 0.20,
		"the head target must remain near the upright rest head")
	_require(
		extension >= ARM_EXTENSION_RATIO_MIN and extension <= ARM_EXTENSION_RATIO_MAX,
		"the open right arm must keep a clearly bent elbow within [%.2f, %.2f] (measured %.3f)" % [
			ARM_EXTENSION_RATIO_MIN, ARM_EXTENSION_RATIO_MAX, extension])
	_require(wrist_degrees <= WRIST_NEUTRAL_MAX_DEGREES,
		"the open right wrist must stay near-neutral (measured %.1f degrees)" % wrist_degrees)

	print("CTRL002_NATURAL_REST descent=%.4f hip_y=%.4f head_y=%.4f head_residual=%.4f root_drift=%.4f r_extension=%.3f r_wrist_deg=%.1f r_palm=%s" % [
		descent, hip.y, head.global_position.y, head_residual, root_drift, extension,
		wrist_degrees, rest_geometry["palm_normal"]])


func _assert_palm_alignment(label: String, item_position: Vector3) -> void:
	# Approach/open-pose palm guard (read-only measurement, printed and asserted): the solved
	# anatomical palm normal (the hand attachment basis Z column, confirmed against the finger
	# knuckles and the curl direction) must materially face the item.
	var geometry: Dictionary = _measure_hand_geometry("Right")
	var palm_dot: float = geometry["palm_normal"].dot(
		(item_position - geometry["wrist"]).normalized())
	_require(
		palm_dot >= PALM_FACING_ITEM_MIN_DOT,
		"%s solved palm must face the item (dot %.2f < %.2f)" % [
			label, palm_dot, PALM_FACING_ITEM_MIN_DOT])
	print("CTRL002_PALM_ALIGNMENT label=%s palm=%s dot=%.2f wrist_deg=%.1f" % [
		label, geometry["palm_normal"], palm_dot, geometry["wrist_flex_degrees"]])


func _assert_final_pose_telemetry(
		item_name: String,
		contact_point: Vector3,
		require_facing_dot: bool) -> void:
	# Final-pose numeric assertions (read-only measurements through the solved-pose probes, printed
	# and asserted): bent-elbow arm extension, near-neutral wrist angle, palm facing the item,
	# positive palm-side seating of the held item, contact distance, and the item-to-elbow anomaly
	# guard (the rejected evidence had the ball between palm and elbow). The facing-direction dot
	# applies to the ball (whose seat offset is palm-dominated); the cylindrical seat runs mostly
	# ALONG the stick axis by authored content, so the stick is guarded by its open/approach facing
	# dot plus the palm-side seating here instead.
	var geometry: Dictionary = _measure_hand_geometry("Right")
	var attachment: Vector3 = geometry["wrist"]
	var elbow: Vector3 = geometry["elbow"]
	var extension_ratio: float = geometry["extension"]
	var contact_distance := attachment.distance_to(contact_point)
	var item_offset := contact_point - attachment
	var item_direction := item_offset.normalized()
	var elbow_cosine := (elbow - attachment).normalized().dot(item_direction)
	var palm_dot: float = geometry["palm_normal"].dot(item_direction)
	var palm_side_seating: float = geometry["palm_normal"].dot(item_offset)

	_require(
		extension_ratio >= ARM_EXTENSION_RATIO_MIN and extension_ratio <= ARM_EXTENSION_RATIO_MAX,
		"%s held pose arm extension ratio %.3f must keep a bent elbow within [%.2f, %.2f]" % [
			item_name, extension_ratio, ARM_EXTENSION_RATIO_MIN, ARM_EXTENSION_RATIO_MAX])
	_require(
		geometry["wrist_flex_degrees"] <= WRIST_NEUTRAL_MAX_DEGREES,
		"%s held wrist must stay near-neutral (%.1f degrees > %.1f)" % [
			item_name, geometry["wrist_flex_degrees"], WRIST_NEUTRAL_MAX_DEGREES])
	if require_facing_dot:
		_require(
			palm_dot >= PALM_FACING_ITEM_MIN_DOT,
			"%s held palm must face the item (dot %.2f < %.2f)" % [
				item_name, palm_dot, PALM_FACING_ITEM_MIN_DOT])
	_require(
		palm_side_seating >= PALM_SIDE_SEATING_MIN_METRES,
		"%s must seat on the palm side, not the back of the hand (palm projection %.3f m < %.3f m)" % [
			item_name, palm_side_seating, PALM_SIDE_SEATING_MIN_METRES])
	_require(
		contact_distance <= ITEM_CONTACT_MAX_DISTANCE_METRES,
		"%s held attachment must sit in contact with the item (distance %.3f m)" % [
			item_name, contact_distance])
	_require(
		elbow_cosine <= ITEM_TO_ELBOW_MAX_COSINE,
		"%s must sit beyond the palm, not between palm and elbow (elbow cosine %.2f)" % [
			item_name, elbow_cosine])

	print("CTRL002_FINAL_POSE item=%s attachment=%s extension_ratio=%.3f wrist_deg=%.1f palm_dot=%.2f palm_seating=%.3f contact_distance=%.4f elbow_cosine=%.2f palm=%s fingers=%s" % [
		item_name, attachment, extension_ratio, geometry["wrist_flex_degrees"], palm_dot,
		palm_side_seating, contact_distance, elbow_cosine, geometry["palm_normal"],
		geometry["fingers_axis"]])


func _bone_rest_length(bone_a: String, bone_b: String) -> float:
	var index_a := _skeleton.find_bone(bone_a)
	var index_b := _skeleton.find_bone(bone_b)
	_require(index_a >= 0 and index_b >= 0,
		"the fixture skeleton must expose %s and %s for reach qualification" % [bone_a, bone_b])
	var origin_a: Vector3 = _skeleton.get_bone_global_rest(index_a).origin
	var origin_b: Vector3 = _skeleton.get_bone_global_rest(index_b).origin
	return origin_a.distance_to(origin_b)


func _on_skeleton_updated() -> void:
	if _skeleton == null:
		return

	for prefix: String in ["Right", "Left"]:
		for joint_name: String in FINGER_JOINTS:
			var bone_name := prefix + joint_name
			var bone_index: int = _skeleton.find_bone(bone_name)
			if bone_index >= 0:
				_captured[bone_name] = _skeleton.get_bone_pose_rotation(bone_index)


func _snapshot_finger_rotations(side: String) -> Dictionary:
	var snapshot: Dictionary = {}
	for joint_name: String in FINGER_JOINTS:
		snapshot[side + joint_name] = _captured.get(side + joint_name, Quaternion.IDENTITY)

	return snapshot


func _max_finger_angle(side: String, expected_rotations: Array) -> float:
	var max_angle := 0.0
	for finger_index: int in expected_rotations.size():
		var bone_name: String = side + FINGER_JOINTS[finger_index]
		var actual: Quaternion = _captured.get(bone_name, Quaternion.IDENTITY)
		max_angle = maxf(max_angle, absf(actual.angle_to(expected_rotations[finger_index])))

	return max_angle


func _max_angle_against_snapshot(side: String, snapshot: Dictionary) -> float:
	var max_angle := 0.0
	for joint_name: String in FINGER_JOINTS:
		var bone_name: String = side + joint_name
		var actual: Quaternion = _captured.get(bone_name, Quaternion.IDENTITY)
		var expected: Quaternion = snapshot.get(bone_name, Quaternion.IDENTITY)
		max_angle = maxf(max_angle, absf(actual.angle_to(expected)))

	return max_angle


func _mean_angle_against_snapshot(side: String, snapshot: Dictionary) -> float:
	var total_angle := 0.0
	for joint_name: String in FINGER_JOINTS:
		var bone_name: String = side + joint_name
		var actual: Quaternion = _captured.get(bone_name, Quaternion.IDENTITY)
		var expected: Quaternion = snapshot.get(bone_name, Quaternion.IDENTITY)
		total_angle += absf(actual.angle_to(expected))

	return total_angle / FINGER_JOINTS.size()


func _mean_finger_angle(side: String, expected_rotations: Array) -> float:
	var total_angle := 0.0
	for finger_index: int in expected_rotations.size():
		var bone_name: String = side + FINGER_JOINTS[finger_index]
		var actual: Quaternion = _captured.get(bone_name, Quaternion.IDENTITY)
		total_angle += absf(actual.angle_to(expected_rotations[finger_index]))

	return total_angle / expected_rotations.size()


func _max_angle_between_references(first: Array, second: Array) -> float:
	var max_angle := 0.0
	for finger_index: int in first.size():
		max_angle = maxf(max_angle, absf(first[finger_index].angle_to(second[finger_index])))

	return max_angle


func _capture(scenario_slug: String, expected_cue: String) -> void:
	await _photobooth.capture_screenshots("%s/scenarios/%s.jpg" % [_output_root, scenario_slug])
	for camera_name: String in REQUIRED_CAMERAS:
		_manifest_rows.append(["scenarios/%s_%s.jpg" % [scenario_slug, camera_name], expected_cue])


func _capture_camera(scenario_slug: String, camera_name: String, expected_cue: String) -> void:
	var rig: CameraRig = _photobooth.get_camera_rig(camera_name)
	await rig.capture_screenshot("%s/scenarios/%s_%s.jpg" % [
		_output_root, scenario_slug, camera_name])
	_manifest_rows.append(["scenarios/%s_%s.jpg" % [scenario_slug, camera_name], expected_cue])


func _write_manifest() -> void:
	# Capture manifest: per-image expected visible cue (authored before inspection); the observed
	# column is filled only after independent inspection of the rendered artefacts. The base
	# directory mirrors the SceneUtils --output-dir resolution the captures themselves use.
	var base_dir := "temp"
	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	for arg_index in user_args.size():
		var arg: String = user_args[arg_index]
		if arg == "--output-dir" and arg_index + 1 < user_args.size():
			base_dir = user_args[arg_index + 1]
		elif arg.begins_with("--output-dir="):
			base_dir = arg.trim_prefix("--output-dir=")

	var path := base_dir.path_join("%s/manifest.md" % _output_root)
	var directory := path.get_base_dir()
	if DirAccess.make_dir_recursive_absolute(directory) != OK:
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to create the manifest directory %s" % directory)
		return

	var content := "# CTRL-002 Optical Grab Palm-Side Capture Manifest\n\n"
	content += "Capture date: 2026-09-07. Runner: tests/interaction/optical_grab_photobooth.gd.\n\n"
	content += "Expected cues are authored inputs; Observed and Pass columns are filled after "
	content += "independent visual inspection of the rendered artefacts.\n\n"
	content += "| Image | Expected Visible Cue | Observed | Pass/Fail |\n"
	content += "| ----- | -------------------- | -------- | --------- |\n"
	for row: Array in _manifest_rows:
		content += "| `%s` | %s | | |\n" % [row[0], row[1]]

	var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null:
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: failed to write the capture manifest at %s" % path)
		return
	file.store_string(content)
	file.close()
	print("CTRL002_MANIFEST rows=%d path=%s" % [_manifest_rows.size(), path])


func _set_stick_visible(is_visible: bool) -> void:
	# Fixture-only presentation control: never changes the test-stick asset or its grab-point semantics.
	_stick.visible = is_visible


func _transform_angle_degrees(expected: Transform3D, actual: Transform3D) -> float:
	var expected_rotation := Quaternion(expected.basis.orthonormalized())
	var actual_rotation := Quaternion(actual.basis.orthonormalized())
	return rad_to_deg(2.0 * acos(clampf(absf(expected_rotation.dot(actual_rotation)), 0.0, 1.0)))


func _resolve_output_root() -> String:
	# An explicit --output-dir already names the CTRL-002 artefact directory, so scenario paths must not
	# repeat it; without one the default temp root needs the feature-relative prefix.
	for arg: String in OS.get_cmdline_user_args():
		if arg.begins_with("--output-dir"):
			return ""

	return OUTPUT_ROOT


func _require(condition: bool, message: String) -> void:
	if not condition:
		SceneUtils.fatal_error_and_quit("CTRL-002 runner: %s" % message)


func _log_hand_state(label: String) -> void:
	print(
		"CTRL002_SCENARIO label=%s committed_mode=%s r_target=%s r_attach=%s r_candidate=%s " % [
			label, String(_driver.call("GetCommittedMode")),
			_driver.call("GetHandTargetWorldPosition", "Right"),
			_driver.call("GetHandAttachmentWorldPosition", "Right"),
			String(_driver.call("GetCurrentCandidateAnimationName", "Right"))]
		+ "r_lifecycle=%s r_blend=%s l_lifecycle=%s" % [
			String(_driver.call("GetHandLifecycle", "Right")),
			String(_driver.call("GetGrabPoseBlendPhase", "Right")),
			String(_driver.call("GetHandLifecycle", "Left"))])


func _to_slug(value: String) -> String:
	return SceneUtils.to_safe_file_component(value.to_lower())


func _has_user_arg(expected_arg: String) -> bool:
	for arg: String in OS.get_cmdline_user_args():
		if arg == expected_arg:
			return true

	return false
