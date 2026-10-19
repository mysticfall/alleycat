extends SceneTree

## AI-009 attention-driven head-orientation photobooth runner.
##
## Simulates the AI-007 gaze-selector role through the photobooth driver (direct IVision look-target
## assignment) and captures the AI-009 user-requirement scenarios at decisive timeline moments:
## glance phase (< centring delay 0.6 s), settled engagement (> 0.6 s + transit), envelope strain,
## and release-to-neutral. Every capture is preceded by non-visual probes and fail-fast assertions.
##
## Female yaw-contribution acceptance measures BOTH presented optical rays against immutable authored
## references. Existing male pitch regression probes are unchanged; other live-neutral yaw probes are diagnostic.

const TEST_SCENE_PATH := "res://tests/ai009_head_orientation/ai009_orienting_photobooth.tscn"
const PHOTOBOOTH_MIND_SCRIPT := "res://tests/ai009_head_orientation/Ai009OrientingPhotoboothMind.cs"
var OUTPUT_ROOT := "ai-009-attention-head-orientation"

const TALL := "TallNpc"
const SHORT := "ShortNpc"

const TALL_RIGS := ["TallSideProfile", "TallThreeQuarter", "TallFace"]
const SHORT_RIGS := ["ShortSideProfile", "ShortThreeQuarter", "ShortFace"]

# Anchor geometry relative to the neutral head frame: +yaw is the character's left, +pitch is up.
const ANCHOR_SPECS := {
	"TallStraightAnchor": {"yaw_deg": 0.0, "pitch_deg": 0.0, "distance": 1.4},
	"TallLowAnchor": {"yaw_deg": 0.0, "pitch_deg": -24.0, "distance": 1.15},
	"ShortHighAnchor": {"yaw_deg": 0.0, "pitch_deg": 24.0, "distance": 0.95},
	"ShortConversationAnchor": {"yaw_deg": 10.0, "pitch_deg": 0.0, "distance": 1.4},
	"ShortStraightAnchor": {"yaw_deg": 0.0, "pitch_deg": 0.0, "distance": 1.4},
	"ShortGlanceInConeAnchor": {"yaw_deg": 12.0, "pitch_deg": 0.0, "distance": 1.3},
	"ShortGlanceAnchor": {"yaw_deg": 30.0, "pitch_deg": 0.0, "distance": 1.25},
	# Strain geometry re-derivation (2026-10-13): authored relative to the startup head, the pivot-driven
	# neck-spine chain translates the engaged viewpoint toward the anchor AND the servo's neutral reference
	# absorbs part of the strain transit, so the aim-space demand shrinks well below the authored yaw (85°
	# at 1.15 m fully centred at aim 68.7°; even 85° at 5.0 m centred in the full scenario history). Measured
	# absorption across photobooth states: ~10-16°. The strain anchor is therefore authored beyond the
	# startup forward plane (105° left, 5 m) so the demand stays past the ±75° envelope even at the
	# absorption ceiling (evidence: diag/orienting_hold_fix/console.strain_history1.log and the photobooth
	# runs beside it). The in-front sanity gate below intentionally admits beyond-90° strain authoring.
	"ShortFarAnchor": {"yaw_deg": 105.0, "pitch_deg": 0.0, "distance": 5.0},
}

# AI-009 default timings: reaction 0.18 s, centring 0.6 s, eye seek smoothing 0.07 s, influence
# engage 4/s and release 3/s, head rates 143°/s horizontal and 103°/s vertical, smoothing 0.07 s.
const GLANCE_PHASE_WAIT_SECONDS := 0.22
const EYES_ONLY_GLANCE_WAIT_SECONDS := 0.25
const OUT_OF_CONE_GLANCE_WAIT_SECONDS := 0.22
const SETTLE_WAIT_SECONDS := 2.6
const STRAIN_SETTLE_WAIT_SECONDS := 3.2
const RECENTRE_WAIT_SECONDS := 2.4
const GLANCE_POST_WAIT_SECONDS := 1.4
const RELEASE_EARLY_WAIT_SECONDS := 0.1
const RELEASE_MID_WAIT_SECONDS := 0.25
const RELEASE_SETTLE_WAIT_SECONDS := 1.6
const NEUTRAL_SETTLE_WAIT_SECONDS := 1.6
const CAPTURE_SETTLE_FRAMES := 3

# Eye clamps (VISION-001): the denominators of the validated seek-to-angle mapping. A horizontal seek s renders
# (s − 0.5) × 2 × 35° of measured gaze yaw (positive to the character's left); a vertical seek s renders
# (0.5 − s) × 2 × 25° of measured gaze pitch (positive up). The eye assertions below spell their thresholds out
# in this form so each original seek-space threshold stays mechanically checkable.
const EYE_MAX_HORIZONTAL_ANGLE_DEGREES := 35.0
const EYE_MAX_VERTICAL_ANGLE_DEGREES := 25.0

# Only initial setup may be phase/settlement conditioned. Events inside a scenario are unconditional.
# Actual execution phase is logged, not inferred from a preceding phase wait.
const SWAY_CLIP_LENGTH_SECONDS := 7.966667
const SWAY_ANCHOR_PHASE_SECONDS := 0.48
const SWAY_PHASE_WINDOW_SECONDS := 0.10

# Settlement sampling under the production startup (see _wait_head_stable): the settle margin added
# to every _wait_head_stable caller timeout because the production-idle re-engagement excursion
# lengthens the settle by seconds. Chain-trace evidence: chain_trace_probe.gd under the
# eye_neutral_fix root. HEAD_SETTLE_CONSECUTIVE_POLLS applies to the no-anchor return-to-rest
# branch; the assigned-anchor branch uses the longer HEAD_SETTLE_CONFIRMATION_POLLS span.
const HEAD_SETTLE_CONSECUTIVE_POLLS := 3
const PRODUCTION_IDLE_SETTLE_MARGIN_SECONDS := 4.0

# Rate-still threshold per 0.1 s settle poll (3.5°/s): the production-idle re-engagement excursion
# slews the solved head at 10–60°/s while the idle sway that rides the settled head moves at
# ≲1.2°/s, so a centred-but-slewing head is mid-excursion, not settled. Chain-trace evidence
# (warmup_reengage2, console.chain_trace4.log): the excursion's return transit passes back through
# the anchor direction at high rate.
const HEAD_SETTLE_RATE_DEGREES_PER_POLL := 0.35

# Confirmation span for assigned-anchor settlement: the re-engagement excursion fires UP TO ~0.4 s
# AFTER the assignment, from a centred and rate-still head (chain-trace warmup_reengage2: centred
# and still for 0.38 s, then a 57°/s away-slew to head-to-anchor 44° before the seconds-long
# return). A centring check that passes inside that window false-settles and samples later glance
# assertions mid-excursion (final_run2–4 all sampled the in-cone glance at yaw ≈ +26°). Requiring
# the centred, rate-still state to hold for this many consecutive 0.1 s polls spans the observed
# onset with margin.
const HEAD_SETTLE_CONFIRMATION_POLLS := 10

# Consecutive settled polls required during initial setup (see _prepare_assignment). The
# assignment gate re-verifies settlement at the sway-phase window itself, so a shorter confirmation
# than HEAD_SETTLE_CONFIRMATION_POLLS suffices here.
const ASSIGNMENT_SETTLED_POLLS := 5
const ASSIGNMENT_SETUP_TIMEOUT_SECONDS := 24.0 # Three idle loops; fails closed, never retries forever.
var _last_assignment_usec := {}
var _trace_driver: Node
var _trace_label := "startup"
var _last_presented_usec := 0
var _no_captures := false

# Pass `-- --continue-on-failure` to record assertion failures and keep running, so one invocation yields the
# full per-scenario evidence table; the run still exits non-zero when any failure was recorded. The default
# stays strict: the first failed assertion terminates the run.
var _continue_on_failure := false
var _failure_summaries: Array[String] = []

# Rest pitch per NPC, captured at the scenario-00 neutral baseline; the no-anchor branch of the
# assignment settlement gate compares against it.
var _rest_pitch_by_npc := {}


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("AI-009 orienting photobooth must run with a renderer, never headless")
		return

	_continue_on_failure = OS.get_cmdline_user_args().has("--continue-on-failure")
	_no_captures = OS.get_cmdline_user_args().has("--no-captures")
	for arg: String in OS.get_cmdline_user_args():
		if arg.begins_with("--output-root="):
			OUTPUT_ROOT = arg.trim_prefix("--output-root=")

	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("AI-009 photobooth failed to load %s" % TEST_SCENE_PATH)
		return

	# Swap the production agentic Minds for inert test Minds before tree entry so no AI provider work
	# runs; the template Mind children (AI-007 selector, AI-009 controller) are untouched.
	_install_test_minds(photobooth)
	root.add_child(photobooth)

	# Mirror the production NPC startup an installer-composed character performs (AutoInstallOnReady ->
	# deferred install -> RefreshComponents -> NPCNavigation.ActivateRuntimeBinding -> manual AnimationTree
	# processing with the NPC idle programme started), instead of the unassigned StandingCrouching advance
	# the unactivated role template drifts into (the B0/B1 cold-start contamination). The trees are held
	# inert until the binding takes over — production wins this race by deferred timing in a live tree;
	# this runner's pre-loop context cannot — so the StandingCrouching clip stays frozen at zero exactly
	# like the production startup. The state is verified after the activation settle, before any anchor
	# placement or scenario.
	var driver: Node = photobooth.get_node("Ai009OrientingPhotoboothDriver")
	driver.call("HoldAnimationTreesInert")
	await SceneUtils.wait_frames(self, 1)
	if driver.call("ActivateProductionStartup") != true:
		SceneUtils.fatal_error_and_quit(
			"AI-009 photobooth failed to activate the production startup binding; scenarios must not run")
		return
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)

	driver.call("Activate")
	_trace_driver = driver
	RenderingServer.frame_post_draw.connect(_trace_frame)
	await SceneUtils.wait_seconds(self, 0.3)
	if driver.call("VerifyProductionIdleStartup") != true:
		SceneUtils.fatal_error_and_quit(
			"AI-009 photobooth did not hold the production idle startup state; scenarios must not run")
		return

	print(
		"AI009_RUN_INFO fps=%f continue_on_failure=%s" % [
			Engine.get_frames_per_second(),
			str(_continue_on_failure),
		]
	)
	_place_and_validate_anchors(photobooth)

	await _capture_framing(photobooth)
	await _run_scenarios(photobooth, driver)

	RenderingServer.frame_post_draw.disconnect(_trace_frame)
	_trace_driver = null
	photobooth.queue_free()
	await SceneUtils.wait_frames(self, 2)
	for failure: String in _failure_summaries:
		print("AI009_GATE_FAILURE_SUMMARY %s" % failure)
	print(
		"AI009_ORIENTING_VISUAL_GATE_RUN_COMPLETE artefact_root=game/temp/%s failures=%d" % [
			OUTPUT_ROOT,
			_failure_summaries.size(),
		]
	)
	quit(0 if _failure_summaries.is_empty() else 1)


func _install_test_minds(photobooth: Photobooth) -> void:
	var mind_script: Script = load(PHOTOBOOTH_MIND_SCRIPT) as Script
	for npc_name: String in [TALL, SHORT]:
		var mind: Node = photobooth.get_node("Subject/%s/Mind" % npc_name)
		mind.set_script(mind_script)


func _place_and_validate_anchors(photobooth: Photobooth) -> void:
	for npc_name: String in [TALL, SHORT]:
		var body_name := "Male" if npc_name == TALL else "Female"
		var viewpoint: Node3D = photobooth.get_node(
			"Subject/%s/%s/GeneralSkeleton/Head/Viewpoint" % [npc_name, body_name])
		for anchor_name: String in ANCHOR_SPECS:
			if not anchor_name.begins_with("Tall") and npc_name == TALL:
				continue
			if anchor_name.begins_with("Tall") and npc_name == SHORT:
				continue

			var spec: Dictionary = ANCHOR_SPECS[anchor_name]
			var yaw := deg_to_rad(float(spec.yaw_deg))
			var pitch := deg_to_rad(float(spec.pitch_deg))
			var local_direction := Vector3(
				-sin(yaw) * cos(pitch),
				sin(pitch),
				-cos(yaw) * cos(pitch))
			var anchor: Node3D = photobooth.get_node("Subject/%s" % anchor_name)
			anchor.global_position = viewpoint.global_position + (viewpoint.global_basis * local_direction) * float(spec.distance)

	# Directional sanity probes in the neutral viewpoint frame: left anchors must sit at negative local
	# X, every anchor must sit in front (negative local Z), and measured angles must match authoring.
	for npc_name: String in [TALL, SHORT]:
		var body_name := "Male" if npc_name == TALL else "Female"
		var viewpoint: Node3D = photobooth.get_node(
			"Subject/%s/%s/GeneralSkeleton/Head/Viewpoint" % [npc_name, body_name])
		var neutral_euler := viewpoint.global_basis.get_euler()
		print(
			"AI009_NEUTRAL_PROBE npc=%s viewpoint_height=%.3f yaw_deg=%.2f pitch_deg=%.2f" % [
				npc_name,
				viewpoint.global_position.y,
				rad_to_deg(neutral_euler.y),
				rad_to_deg(neutral_euler.x),
			]
		)

		for anchor_name: String in ANCHOR_SPECS:
			var is_tall_anchor := anchor_name.begins_with("Tall")
			if is_tall_anchor != (npc_name == TALL):
				continue

			var spec: Dictionary = ANCHOR_SPECS[anchor_name]
			var anchor: Node3D = photobooth.get_node("Subject/%s" % anchor_name)
			var local := viewpoint.global_transform.affine_inverse() * anchor.global_position
			var measured_yaw := rad_to_deg(atan2(-local.x, -local.z))
			var measured_pitch := rad_to_deg(atan(local.y / sqrt(local.x * local.x + local.z * local.z)))
			print(
				"AI009_ANCHOR_PROBE npc=%s anchor=%s local=(%.2f,%.2f,%.2f) yaw_deg=%.2f pitch_deg=%.2f" % [
					npc_name,
					anchor_name,
					local.x,
					local.y,
					local.z,
					measured_yaw,
					measured_pitch,
				]
			)

			# Directional sanity: in-cone/frontal authoring must sit in front of the startup head (negative
			# local Z). Strain anchors deliberately authored beyond ±90° yaw sit behind the startup forward
			# plane by design — a target the head cannot reach by turning — and are validated by the
			# measured-authoring check below instead.
			if float(spec.yaw_deg) <= 90.0 and local.z >= 0.0:
				SceneUtils.fatal_error_and_quit(
					"AI-009 anchor %s is not in front of %s (local z=%.2f); directional setup is invalid" % [
						anchor_name,
						npc_name,
						local.z,
					])
				return
			if float(spec.yaw_deg) > 0.0 and local.x >= 0.0:
				SceneUtils.fatal_error_and_quit(
					"AI-009 left anchor %s is not on %s's left (local x=%.2f); directional setup is invalid" % [
						anchor_name,
						npc_name,
						local.x,
					])
				return
			if absf(measured_yaw - float(spec.yaw_deg)) > 1.5 or absf(measured_pitch - float(spec.pitch_deg)) > 1.5:
				SceneUtils.fatal_error_and_quit(
					"AI-009 anchor %s measured (yaw %.2f°, pitch %.2f°) diverges from authored (yaw %.2f°, pitch %.2f°)" % [
						anchor_name,
						measured_yaw,
						measured_pitch,
						float(spec.yaw_deg),
						float(spec.pitch_deg),
					])
				return


func _enable_only(photobooth: Photobooth, rig_names: Array) -> void:
	for key: StringName in photobooth.camera_rigs:
		var rig: CameraRig = photobooth.camera_rigs[key]
		rig.visible = rig_names.has(String(rig.name))


func _capture_framing(photobooth: Photobooth) -> void:
	if _no_captures:
		return
	_enable_only(photobooth, TALL_RIGS + SHORT_RIGS)
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)
	await photobooth.capture_screenshots("%s/framing/all_rigs" % OUTPUT_ROOT)


func _print_probes(driver: Node, character_id: String, label: String) -> void:
	# roll and anchor_yaw are diagnostic-only attribution fields: the YXZ yaw euler of the solved
	# viewpoint shifts with residual head roll, so a centred head can read a non-zero yaw euler
	# (head_to_anchor is the roll-free oracle); anchor_yaw decomposes the residual into the head
	# frame's horizontal axis.
	print(
		"AI009_PROBE scenario=%s npc=%s head_to_anchor=%.2f yaw=%.2f pitch=%.2f roll=%.2f anchor_yaw=%.2f influence=%.3f eye_yaw=%.2f eye_pitch=%.2f" % [
			label,
			character_id,
			float(driver.call("GetHeadToAnchorAngleDegrees", character_id)),
			float(driver.call("GetHeadYawDegrees", character_id)),
			float(driver.call("GetHeadPitchDegrees", character_id)),
			float(driver.call("GetHeadRollDegrees", character_id)),
			float(driver.call("GetAnchorYawInHeadDegrees", character_id)),
			float(driver.call("GetInfluence", character_id)),
			float(driver.call("GetEyeGazeYawDegrees", character_id)),
			float(driver.call("GetEyeGazePitchDegrees", character_id)),
		]
	)


func _wait_until_centred(driver: Node, character_id: String, timeout_seconds: float) -> void:
	# Polls until the solved head centres the current gaze anchor. This is the scenario-independent oracle:
	# absolute world pitch is not, because neck craning also translates the solved viewpoint.
	var start_usec := Time.get_ticks_usec()
	var elapsed := 0.0
	while elapsed < timeout_seconds:
		if float(driver.call("GetHeadToAnchorAngleDegrees", character_id)) < 2.0:
			return
		await SceneUtils.wait_seconds(self, 0.1)
		elapsed = (Time.get_ticks_usec() - start_usec) / 1000000.0

	_fail_or_record(
		"AI-009 head of %s did not centre its gaze anchor within %.1fs (last angle: %.2f°)" % [
			character_id,
			timeout_seconds,
			float(driver.call("GetHeadToAnchorAngleDegrees", character_id)),
		])


func _fail_or_record(message: String) -> void:
	# Strict default: the first failed assertion terminates the run. With --continue-on-failure the failure is
	# recorded and the run continues for per-scenario evidence; the exit code still reflects any failure.
	var failure := "AI-009 visual gate assertion failed: %s" % message
	if not _continue_on_failure:
		SceneUtils.fatal_error_and_quit(failure)
		return

	_failure_summaries.append(failure)
	print("AI009_GATE_ASSERTION_FAILED count=%d %s" % [_failure_summaries.size(), failure])


func _assert(condition: bool, message: String) -> void:
	print("AI009_ASSERT result=%s %s" % ["PASS" if condition else "FAIL", message])
	if condition:
		return
	_fail_or_record(message)


func _wait_head_stable(
		driver: Node,
		character_id: String,
		expected_yaw_deg: float,
		expected_pitch_deg: float,
		timeout_seconds: float) -> void:
	# Polls until the solved head settles and STAYS settled for the required consecutive polls.
	# Under the production startup the first engagement after a release can slew the head on a
	# seconds-long re-engagement excursion before it converges (the orienting neutral-reference
	# re-anchoring absorbs chain lag while the solved head lags the commanded intent); a poll that
	# lands inside that excursion samples the later glance assertions mid-transit. The
	# production-idle settle margin extends every caller timeout because the excursion adds seconds
	# to the settle.
	#
	# Settled-state oracle: while a gaze anchor is assigned, settlement means the head is CENTRED on
	# the anchor (head-to-anchor < 2°, the same roll-free scenario-independent oracle
	# _wait_until_centred uses) AND rate-still (see HEAD_SETTLE_RATE_DEGREES_PER_POLL), held for
	# HEAD_SETTLE_CONFIRMATION_POLLS consecutive polls so the excursion's delayed onset (up to
	# ~0.4 s after assignment, from an already-centred still head) cannot false-settle the wait. The
	# pivot-driven neck-spine chain converges on a path-dependent world pose: the head translates as
	# it turns, so a head centred to 0.01° legitimately reads a world yaw euler of +3° or more
	# (measured), and demanding the fresh-startup euler (0, baseline) ± 1.5° never converges on that
	# state. Without an anchor the head returns to its rest pose, so the raw euler expectation
	# (yaw ≈ 0, pitch ≈ baseline) ± 1.5° held for HEAD_SETTLE_CONSECUTIVE_POLLS polls applies.
	var start_usec := Time.get_ticks_usec()
	var elapsed := 0.0
	var consecutive := 0
	var previous_yaw := 0.0
	var previous_pitch := 0.0
	var has_previous_sample := false
	var deadline_seconds := timeout_seconds + PRODUCTION_IDLE_SETTLE_MARGIN_SECONDS
	while elapsed < deadline_seconds:
		var settled: bool
		var required_consecutive := HEAD_SETTLE_CONSECUTIVE_POLLS
		var anchor_assigned := bool(driver.call("HasAssignedLookTarget", character_id))
		if anchor_assigned:
			required_consecutive = HEAD_SETTLE_CONFIRMATION_POLLS
			var yaw := float(driver.call("GetHeadYawDegrees", character_id))
			var pitch := float(driver.call("GetHeadPitchDegrees", character_id))
			var centred := float(driver.call("GetHeadToAnchorAngleDegrees", character_id)) < 2.0
			# The rate-still guard rejects a centred-but-slewing head: the re-engagement excursion
			# returns through the anchor direction at 10–60°/s, and centring alone would pass
			# mid-transit.
			var rate_still := not has_previous_sample or (
				absf(yaw - previous_yaw) < HEAD_SETTLE_RATE_DEGREES_PER_POLL
					and absf(pitch - previous_pitch) < HEAD_SETTLE_RATE_DEGREES_PER_POLL)
			previous_yaw = yaw
			previous_pitch = pitch
			has_previous_sample = true
			settled = centred and rate_still
		else:
			var yaw := float(driver.call("GetHeadYawDegrees", character_id))
			var pitch := float(driver.call("GetHeadPitchDegrees", character_id))
			settled = absf(yaw - expected_yaw_deg) < 1.5 and absf(pitch - expected_pitch_deg) < 1.5
		if settled:
			consecutive += 1
			if consecutive >= required_consecutive:
				return
		else:
			consecutive = 0
		await SceneUtils.wait_seconds(self, 0.1)
		elapsed = (Time.get_ticks_usec() - start_usec) / 1000000.0

	var has_anchor := bool(driver.call("HasAssignedLookTarget", character_id))
	if has_anchor:
		_fail_or_record(
			"AI-009 head of %s did not stabilise centred on its gaze anchor within %.1fs (last head-to-anchor: %.2f°)" % [
				character_id,
				deadline_seconds,
				float(driver.call("GetHeadToAnchorAngleDegrees", character_id)),
			])
	else:
		_fail_or_record(
			"AI-009 head of %s did not stabilise at (yaw %.2f°, pitch %.2f°) within %.1fs (last: %.2f°, %.2f°)" % [
				character_id,
				expected_yaw_deg,
				expected_pitch_deg,
				deadline_seconds,
				float(driver.call("GetHeadYawDegrees", character_id)),
				float(driver.call("GetHeadPitchDegrees", character_id)),
			])


func _capture_scenario(
		photobooth: Photobooth,
		driver: Node,
		character_id: String,
		rigs: Array,
		scenario_name: String,
		label: String) -> void:
	print("AI009_CAPTURE_REQUEST label=%s time_usec=%d" % [label, Time.get_ticks_usec()])
	if _no_captures:
		_print_probes(driver, character_id, label)
		return
	_enable_only(photobooth, rigs)
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)
	_print_probes(driver, character_id, label)
	await photobooth.capture_screenshots("%s/scenarios/%s" % [OUTPUT_ROOT, scenario_name])


func _assign(driver: Node, photobooth: Photobooth, character_id: String, anchor_name: String) -> void:
	driver.call("AssignLookTarget", character_id, photobooth.get_node("Subject/%s" % anchor_name))
	_last_assignment_usec[character_id] = Time.get_ticks_usec()
	_log_event(driver, character_id, "assign", anchor_name)


func _setup_assign(driver: Node, photobooth: Photobooth, character_id: String, anchor_name: String) -> bool:
	if not await _prepare_assignment(driver, character_id):
		return false
	_assign(driver, photobooth, character_id, anchor_name)
	return true


func _log_event(driver: Node, character_id: String, kind: String, anchor: String = "") -> void:
	_trace_label = "%s:%s:%s" % [character_id, kind, anchor]
	print("AI009_EVENT kind=%s npc=%s anchor=%s time_usec=%d phase=%.6f" % [
		kind, character_id, anchor, Time.get_ticks_usec(),
		float(driver.call("GetIdleClipPositionSeconds", character_id))])


func _trace_frame() -> void:
	_last_presented_usec = Time.get_ticks_usec()
	for character_id: String in [TALL, SHORT]:
		print("AI009_FRAME time_usec=%d frame=%d label=%s npc=%s phase=%.6f residual=%.6f yaw=%.6f pitch=%.6f roll=%.6f influence=%.6f eye_yaw=%.6f eye_pitch=%.6f" % [
			Time.get_ticks_usec(), Engine.get_process_frames(), _trace_label, character_id,
			float(_trace_driver.call("GetIdleClipPositionSeconds", character_id)),
			float(_trace_driver.call("GetHeadToAnchorAngleDegrees", character_id)),
			float(_trace_driver.call("GetHeadYawDegrees", character_id)),
			float(_trace_driver.call("GetHeadPitchDegrees", character_id)),
			float(_trace_driver.call("GetHeadRollDegrees", character_id)),
			float(_trace_driver.call("GetInfluence", character_id)),
			float(_trace_driver.call("GetEyeGazeYawDegrees", character_id)),
			float(_trace_driver.call("GetEyeGazePitchDegrees", character_id))])


func _wait_brief_deadline(character_id: String, duration: float) -> void:
	var start: int = _last_assignment_usec[character_id]
	# First completed render at/after the prescribed deadline; never wait for head success.
	while Time.get_ticks_usec() - start < duration * 1000000.0:
		await RenderingServer.frame_post_draw
	print("AI009_BRIEF_DEADLINE npc=%s prescribed=%.6f actual=%.6f" % [
		character_id, duration, (Time.get_ticks_usec() - start) / 1000000.0])
	_assert(Time.get_ticks_usec() - start <= 600000,
		"brief event must execute within the approved 0.6-second window")


func _wait_return_window(character_id: String, duration: float) -> void:
	var start: int = _last_assignment_usec[character_id]
	# Require a recorded presentation through the full assignment-relative window, not an accumulated
	# delta wait which can finish early. Capture I/O and head success never set this deadline.
	while _last_presented_usec - start < duration * 1000000.0:
		await RenderingServer.frame_post_draw
	print("AI009_RETURN_WINDOW npc=%s assignment_usec=%d last_observation_usec=%d end_usec=%d prescribed=%.6f" % [
		character_id, start, _last_presented_usec, Time.get_ticks_usec(), duration])
	_assert(_last_presented_usec - start >= duration * 1000000.0,
		"original straight-anchor return must retain a presented observation through the full 1.4 seconds")


func _assert_eye_contribution(driver: Node, character_id: String, axis: int, comparison: String,
		threshold: float, label: String) -> void:
	var pair: Dictionary = driver.call("GetAuthoredEyeContributions", character_id)
	for eye_name: String in ["LeftEye", "RightEye"]:
		var contribution: Vector2 = pair[eye_name]
		var value: float = contribution[axis]
		var passed := value > threshold if comparison == ">" else (
			value < threshold if comparison == "<" else absf(value) < threshold)
		print("AI009_EYE_ACCEPTANCE npc=%s eye=%s time_usec=%d frame=%d label=%s yaw=%.9f pitch=%.9f comparison=%s threshold=%.9f pass=%s" % [
			character_id, eye_name, Time.get_ticks_usec(), Engine.get_process_frames(), label,
			contribution.x, contribution.y, comparison, threshold, str(passed)])
		_assert(passed, "%s: %s authored optical contribution %.6f°, required %s %.6f°" % [
			label, eye_name, value, comparison, threshold])


func _head_settled_at_current_anchor(driver: Node, character_id: String) -> bool:
	# Single-poll settled-state predicate: centred on the assigned anchor (head-to-anchor < 2°,
	# the roll-free oracle) or, without an anchor, within 1.5° of the rest euler.
	if bool(driver.call("HasAssignedLookTarget", character_id)):
		return float(driver.call("GetHeadToAnchorAngleDegrees", character_id)) < 2.0
	var rest_pitch := float(_rest_pitch_by_npc.get(character_id, 0.0))
	return absf(float(driver.call("GetHeadYawDegrees", character_id))) < 1.5 \
		and absf(float(driver.call("GetHeadPitchDegrees", character_id)) - rest_pitch) < 1.5


func _prepare_assignment(driver: Node, character_id: String,
		timeout_seconds: float = ASSIGNMENT_SETUP_TIMEOUT_SECONDS) -> bool:
	_trace_label = "%s:setup" % character_id
	var start := Time.get_ticks_usec()
	var consecutive := 0
	var settlement_confirmed := false
	var previous_yaw := 0.0
	var previous_pitch := 0.0
	var has_previous_sample := false
	while Time.get_ticks_usec() - start < timeout_seconds * 1000000.0:
		var centred := _head_settled_at_current_anchor(driver, character_id)
		var yaw := float(driver.call("GetHeadYawDegrees", character_id))
		var pitch := float(driver.call("GetHeadPitchDegrees", character_id))
		var rate_still := not has_previous_sample or (
			absf(yaw - previous_yaw) < HEAD_SETTLE_RATE_DEGREES_PER_POLL
				and absf(pitch - previous_pitch) < HEAD_SETTLE_RATE_DEGREES_PER_POLL)
		previous_yaw = yaw
		previous_pitch = pitch
		has_previous_sample = true
		if centred and rate_still:
			consecutive += 1
		else:
			consecutive = 0
		settlement_confirmed = settlement_confirmed or consecutive >= ASSIGNMENT_SETTLED_POLLS
		var phase := float(driver.call("GetIdleClipPositionSeconds", character_id))
		if settlement_confirmed and fposmod(
				phase - SWAY_ANCHOR_PHASE_SECONDS, SWAY_CLIP_LENGTH_SECONDS) <= SWAY_PHASE_WINDOW_SECONDS:
			# Recheck at the event, using the preceding poll for rate, not a hidden future wait.
			if centred and rate_still:
				print("AI009_SETUP_READY npc=%s time_usec=%d phase=%.6f" % [character_id, Time.get_ticks_usec(), phase])
				return true
			settlement_confirmed = false
		await SceneUtils.wait_seconds(self, 0.1)
	_fail_or_record("assignment setup timed out for %s after %.2fs; scenarios aborted" % [character_id, timeout_seconds])
	return false


func _clear(driver: Node, character_id: String) -> void:
	driver.call("ClearLookTarget", character_id)
	_log_event(driver, character_id, "clear")


func _run_scenarios(photobooth: Photobooth, driver: Node) -> void:
	# 00 — Neutral baselines for both NPCs (no gaze assignment). The baseline pitch captures the rig rest pose
	# (≈2.7° up) so later return-to-neutral assertions compare against the actual rest, not absolute zero.
	_assert(not bool(driver.call("HasAssignedLookTarget", TALL)), "tall NPC should start with no gaze assignment")
	_assert(absf(float(driver.call("GetHeadYawDegrees", TALL))) < 3.0, "tall NPC should start near neutral yaw")
	await _capture_scenario(photobooth, driver, TALL, TALL_RIGS, "00_tall_neutral", "00_tall_neutral")
	var tall_baseline_pitch := float(driver.call("GetHeadPitchDegrees", TALL))
	_rest_pitch_by_npc[TALL] = tall_baseline_pitch

	_assert(absf(float(driver.call("GetHeadYawDegrees", SHORT))) < 3.0, "short NPC should start near neutral yaw")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "00_short_neutral", "00_short_neutral")
	var short_baseline_pitch := float(driver.call("GetHeadPitchDegrees", SHORT))
	_rest_pitch_by_npc[SHORT] = short_baseline_pitch

	# 01 — UR 1 headline: tall NPC looking down at a shorter character's head height (24° down, beyond the 15°
	# down comfort cone). Warm up engaged on the straight anchor first (the production AI-007 flow always has the
	# NPC engaged before glances), then assign the low anchor: the glance phase holds the head while the eyes
	# carry the downward angle; the settled phase cranes the neck down with the eyes near-centred.
	if not await _setup_assign(driver, photobooth, TALL, "TallStraightAnchor"):
		return
	await _wait_head_stable(driver, TALL, 0.0, tall_baseline_pitch, 5.0)
	# Re-capture the eye-gaze neutral settled on the centred straight anchor: under the production startup the
	# Activate-time bootstrap captures the eye pose mid-animation-decay, which would offset every later eye
	# reading. The driver's own contract requires a known-neutral moment, and this warm-up is one.
	driver.call("CaptureEyeGazeNeutral", TALL)

	if not await _setup_assign(driver, photobooth, TALL, "TallLowAnchor"):
		return
	await SceneUtils.wait_seconds(self, GLANCE_PHASE_WAIT_SECONDS)
	var down_glance_eye_pitch := float(driver.call("GetEyeGazePitchDegrees", TALL))
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", TALL)) > 15.0,
		"tall down glance phase should hold the head near the previous sustained aim (head-to-anchor well above 0)")
	_assert(
		down_glance_eye_pitch < (0.5 - 0.6) * 2.0 * EYE_MAX_VERTICAL_ANGLE_DEGREES,
		"tall down glance phase should show eyes carrying the downward angle (measured eye pitch %.2f°, required below %.2f°)" % [
			down_glance_eye_pitch,
			(0.5 - 0.6) * 2.0 * EYE_MAX_VERTICAL_ANGLE_DEGREES,
		])
	await _capture_scenario(photobooth, driver, TALL, TALL_RIGS, "01_tall_down_glance_phase", "01_tall_down_glance_phase")

	await SceneUtils.wait_seconds(self, SETTLE_WAIT_SECONDS - GLANCE_PHASE_WAIT_SECONDS)
	var down_settled_eye_pitch := float(driver.call("GetEyeGazePitchDegrees", TALL))
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", TALL)) < 3.0,
		"tall down settled shot should centre the head on the low anchor")
	_assert(
		float(driver.call("GetHeadPitchDegrees", TALL)) < tall_baseline_pitch - 18.0,
		"tall down settled shot should pitch the head down beyond the eye-only range (neck craning)")
	_assert(
		absf(down_settled_eye_pitch) < 0.18 * 2.0 * EYE_MAX_VERTICAL_ANGLE_DEGREES,
		"tall down settled shot should leave the eyes near-centred in their sockets (measured eye pitch %.2f°, required within %.2f°)" % [
			down_settled_eye_pitch,
			0.18 * 2.0 * EYE_MAX_VERTICAL_ANGLE_DEGREES,
		])
	await _capture_scenario(photobooth, driver, TALL, TALL_RIGS, "01_tall_down_settled", "01_tall_down_settled")
	await _clear(driver, TALL)
	await _wait_head_stable(driver, TALL, 0.0, tall_baseline_pitch, 4.0)

	# 02 — UR 1 mirror: short NPC looking up at a taller target (24° up, beyond the 10° up cone).
	if not await _setup_assign(driver, photobooth, SHORT, "ShortHighAnchor"):
		return
	await _wait_until_centred(driver, SHORT, 6.0)
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", SHORT)) < 3.0,
		"short up settled shot should centre the head on the high anchor")
	_assert(
		float(driver.call("GetHeadPitchDegrees", SHORT)) > short_baseline_pitch + 18.0,
		"short up settled shot should pitch the head up beyond the eye-only range (neck extension)")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "02_short_up_settled", "02_short_up_settled")
	await _clear(driver, SHORT)
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	# 03 — UR 2 conversation signature: sustained same-height anchor 10° left (inside the comfort cone and the
	# eye clamps). Glance phase holds the head on the warm-up (straight) aim while the eyes carry the anchor;
	# settled turns the head to face it even though the eyes could have carried it.
	if not await _setup_assign(driver, photobooth, SHORT, "ShortStraightAnchor"):
		return
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 5.0)
	# Sustained-pose reference for the glance head-hold oracle: an engaged head centred on its anchor
	# legitimately reads a world yaw euler of +3-4° (the pivot chain translates the viewpoint toward the
	# anchor; documented with _wait_head_stable), so the glance-phase hold is asserted against the measured
	# sustained pose, not absolute zero. Same 3° tolerance as the original absolute form.
	var sustained_yaw_conversation := float(driver.call("GetHeadYawDegrees", SHORT))
	# Same known-neutral eye-gaze reference re-capture as the tall warm-up above (see scenario 01).
	driver.call("CaptureEyeGazeNeutral", SHORT)

	if not await _setup_assign(driver, photobooth, SHORT, "ShortConversationAnchor"):
		return
	await SceneUtils.wait_seconds(self, GLANCE_PHASE_WAIT_SECONDS)
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT)) - sustained_yaw_conversation) < 3.0,
		"conversation glance phase should keep the head on the last sustained aim while the target is in-cone")
	_assert_eye_contribution(driver, SHORT, 0, ">", (0.55 - 0.5) * 2.0 * EYE_MAX_HORIZONTAL_ANGLE_DEGREES,
		"conversation glance phase should show the eyes on the anchor")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "03_conversation_glance_phase", "03_conversation_glance_phase")

	await SceneUtils.wait_seconds(self, SETTLE_WAIT_SECONDS - GLANCE_PHASE_WAIT_SECONDS)
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", SHORT)) < 2.5,
		"conversation settled shot should fully centre the head on the conversation partner")
	_assert_eye_contribution(driver, SHORT, 0, "abs<", 0.12 * 2.0 * EYE_MAX_HORIZONTAL_ANGLE_DEGREES,
		"conversation settled shot should leave the eyes near eye-neutral once the head faces the partner")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "03_conversation_settled", "03_conversation_settled")
	await _clear(driver, SHORT)
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	# 04 — UR 3 brief side glances: an in-cone glance stays purely eyes-only (head holds exactly on the sustained
	# straight anchor), an out-of-cone glance moves the head only the residual distance, and the head eases back
	# after each glance ends.
	if not await _setup_assign(driver, photobooth, SHORT, "ShortStraightAnchor"):
		return
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 5.0)
	# Sustained-pose reference for the glance head-hold oracle, as in scenario 03: the centred engaged head
	# legitimately reads +3-4° world yaw from the pivot-chain viewpoint translation, so the eyes-only hold is
	# asserted against the measured sustained pose. Same 3° tolerance as the original absolute form.
	var sustained_yaw_glance := float(driver.call("GetHeadYawDegrees", SHORT))

	if not await _setup_assign(driver, photobooth, SHORT, "ShortGlanceInConeAnchor"):
		return
	await _wait_brief_deadline(SHORT, EYES_ONLY_GLANCE_WAIT_SECONDS)
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT)) - sustained_yaw_glance) < 3.0,
		"in-cone brief glance must keep the head exactly on the sustained anchor (eyes-only glance)")
	_assert_eye_contribution(driver, SHORT, 0, ">", (0.58 - 0.5) * 2.0 * EYE_MAX_HORIZONTAL_ANGLE_DEGREES,
		"in-cone brief glance should show the eyes on the glance target")
	# Return before any screenshot I/O or capture-settle frames can extend the stimulus.
	_assign(driver, photobooth, SHORT, "ShortStraightAnchor")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "04_glance_in_cone_return", "04_glance_in_cone_return")
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	if not await _setup_assign(driver, photobooth, SHORT, "ShortGlanceAnchor"):
		return
	await _wait_brief_deadline(SHORT, OUT_OF_CONE_GLANCE_WAIT_SECONDS)
	var out_of_cone_yaw := absf(float(driver.call("GetHeadYawDegrees", SHORT)))
	_assert(
		out_of_cone_yaw > 1.0 and out_of_cone_yaw < 20.0,
		"out-of-cone brief glance should move the head only part of the residual (found %.2f° yaw)" % out_of_cone_yaw)
	_assert_eye_contribution(driver, SHORT, 0, ">", (0.7 - 0.5) * 2.0 * EYE_MAX_HORIZONTAL_ANGLE_DEGREES,
		"out-of-cone brief glance should show the eyes still carrying most of the glance angle")
	_assign(driver, photobooth, SHORT, "ShortStraightAnchor")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "04_glance_out_of_cone_return", "04_glance_out_of_cone_return")
	await _wait_return_window(SHORT, GLANCE_POST_WAIT_SECONDS)
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT)) - sustained_yaw_glance) < 3.5,
		"post-glance head should ease back onto the sustained anchor")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "04_glance_post_return", "04_glance_post_return")
	await _clear(driver, SHORT)
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	# 05 — UR 5 strain: sustained anchor 105° left at 5.0 m — beyond the ±75° envelope by a margin that
	# survives the pivot-chain viewpoint translation and the strain-transit absorption (see the
	# ANCHOR_SPECS geometry note; measured aim clamps at the 75° envelope edge). The head cranes toward
	# the envelope edge but cannot fully centre, and the eyes keep tracking past the strained head — the
	# strain-premise assertion mirrors the scenario-04 residual style.
	if not await _setup_assign(driver, photobooth, SHORT, "ShortFarAnchor"):
		return
	await SceneUtils.wait_seconds(self, STRAIN_SETTLE_WAIT_SECONDS)
	var strain_yaw := absf(float(driver.call("GetHeadYawDegrees", SHORT)))
	var strain_head_to_anchor := float(driver.call("GetHeadToAnchorAngleDegrees", SHORT))
	_assert(
		strain_yaw > 50.0,
		"beyond-envelope settled shot should crane the head far toward the anchor (found %.2f° yaw)" % strain_yaw)
	_assert(
		strain_head_to_anchor > 5.0,
		"beyond-envelope settled shot must leave the head unable to fully centre the anchor (measured head-to-anchor %.2f°, required above 5.00°)" % strain_head_to_anchor)
	_assert_eye_contribution(driver, SHORT, 0, ">", (0.55 - 0.5) * 2.0 * EYE_MAX_HORIZONTAL_ANGLE_DEGREES,
		"beyond-envelope settled shot should keep the eyes tracking past the strained head")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "05_beyond_envelope_settled", "05_beyond_envelope_settled")

	# 06 — UR 5 release: clearing the gaze eases the head back to neutral. Consecutive captures around the
	# release document the return motion and the settled neutral.
	await _clear(driver, SHORT)
	await SceneUtils.wait_seconds(self, RELEASE_EARLY_WAIT_SECONDS)
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "06_release_early", "06_release_early")

	await SceneUtils.wait_seconds(self, RELEASE_MID_WAIT_SECONDS - RELEASE_EARLY_WAIT_SECONDS)
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "06_release_mid", "06_release_mid")

	await SceneUtils.wait_seconds(self, RELEASE_SETTLE_WAIT_SECONDS - RELEASE_MID_WAIT_SECONDS)
	_assert(
		not bool(driver.call("HasAssignedLookTarget", SHORT)),
		"release should clear the explicit gaze assignment")
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT))) < 2.5,
		"settled release should return the head to neutral yaw")
	_assert(
		absf(float(driver.call("GetHeadPitchDegrees", SHORT)) - short_baseline_pitch) < 2.5,
		"settled release should return the head to its rest pitch")
	_assert(
		float(driver.call("GetInfluence", SHORT)) < 0.05,
		"settled release should ramp the orienting influence back to zero")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "06_release_settled", "06_release_settled")
