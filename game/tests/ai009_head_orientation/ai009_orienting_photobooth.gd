extends SceneTree

## AI-009 attention-driven head-orientation photobooth runner.
##
## Simulates the AI-007 gaze-selector role through the photobooth driver (direct IVision look-target
## assignment) and captures the AI-009 user-requirement scenarios at decisive timeline moments:
## glance phase (< centring delay 0.6 s), settled engagement (> 0.6 s + transit), envelope strain,
## and release-to-neutral. Every capture is preceded by non-visual probes and fail-fast assertions.

const TEST_SCENE_PATH := "res://tests/ai009_head_orientation/ai009_orienting_photobooth.tscn"
const PHOTOBOOTH_MIND_SCRIPT := "res://tests/ai009_head_orientation/Ai009OrientingPhotoboothMind.cs"
const OUTPUT_ROOT := "ai-009-attention-head-orientation"

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
	"ShortFarAnchor": {"yaw_deg": 85.0, "pitch_deg": 0.0, "distance": 1.15},
}

# AI-009 default timings: reaction 0.18 s, centring 0.6 s, eye seek smoothing 0.08 s, influence
# engage 4/s and release 3/s, head rates 143°/s horizontal and 103°/s vertical, smoothing 0.12 s.
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


func _init() -> void:
	await _run()


func _run() -> void:
	if DisplayServer.get_name() == "headless":
		SceneUtils.fatal_error_and_quit("AI-009 orienting photobooth must run with a renderer, never headless")
		return

	var photobooth: Photobooth = SceneUtils.instantiate_scene(TEST_SCENE_PATH) as Photobooth
	if photobooth == null:
		SceneUtils.fatal_error_and_quit("AI-009 photobooth failed to load %s" % TEST_SCENE_PATH)
		return

	# Swap the production agentic Minds for inert test Minds before tree entry so no AI provider work
	# runs; the template Mind children (AI-007 selector, AI-009 controller) are untouched.
	_install_test_minds(photobooth)
	root.add_child(photobooth)
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)

	var driver: Node = photobooth.get_node("Ai009OrientingPhotoboothDriver")
	driver.call("Activate")
	await SceneUtils.wait_seconds(self, 0.3)

	print("AI009_RUN_INFO fps=%f" % Engine.get_frames_per_second())
	_place_and_validate_anchors(photobooth)

	await _capture_framing(photobooth)
	await _run_scenarios(photobooth, driver)

	photobooth.queue_free()
	await SceneUtils.wait_frames(self, 2)
	print("AI009_ORIENTING_VISUAL_GATE_RUN_COMPLETE artefact_root=game/temp/%s" % OUTPUT_ROOT)
	quit(0)


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

			if local.z >= 0.0:
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
	_enable_only(photobooth, TALL_RIGS + SHORT_RIGS)
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)
	await photobooth.capture_screenshots("%s/framing/all_rigs" % OUTPUT_ROOT)


func _print_probes(driver: Node, character_id: String, label: String) -> void:
	print(
		"AI009_PROBE scenario=%s npc=%s head_to_anchor=%.2f yaw=%.2f pitch=%.2f influence=%.3f eye_h=%.3f eye_v=%.3f" % [
			label,
			character_id,
			float(driver.call("GetHeadToAnchorAngleDegrees", character_id)),
			float(driver.call("GetHeadYawDegrees", character_id)),
			float(driver.call("GetHeadPitchDegrees", character_id)),
			float(driver.call("GetInfluence", character_id)),
			float(driver.call("GetEyeSeekHorizontal", character_id)),
			float(driver.call("GetEyeSeekVertical", character_id)),
		]
	)


func _wait_until_centred(driver: Node, character_id: String, timeout_seconds: float) -> void:
	# Polls until the solved head centres the current gaze anchor. This is the scenario-independent oracle:
	# absolute world pitch is not, because neck craning also translates the solved viewpoint.
	var elapsed := 0.0
	while elapsed < timeout_seconds:
		if float(driver.call("GetHeadToAnchorAngleDegrees", character_id)) < 2.0:
			return
		await SceneUtils.wait_seconds(self, 0.1)
		elapsed += 0.1

	SceneUtils.fatal_error_and_quit(
		"AI-009 head of %s did not centre its gaze anchor within %.1fs (last angle: %.2f°)" % [
			character_id,
			timeout_seconds,
			float(driver.call("GetHeadToAnchorAngleDegrees", character_id)),
		])


func _read_eye_probe(driver: Node, character_id: String, method: String) -> float:
	# The eye-presentation seek occasionally reads the authored -1.0 sentinel when an animation track stomp
	# lands between frames; retry once so assertions see the controller-written value.
	var value := float(driver.call(method, character_id))
	if value <= -0.999:
		await SceneUtils.wait_frames(self, 3)
		value = float(driver.call(method, character_id))
	return value


func _assert(condition: bool, message: String) -> void:
	if not condition:
		SceneUtils.fatal_error_and_quit("AI-009 visual gate assertion failed: %s" % message)


func _wait_head_stable(
		driver: Node,
		character_id: String,
		expected_yaw_deg: float,
		expected_pitch_deg: float,
		timeout_seconds: float) -> void:
	# Polls until the solved head settles within 1.5° of the expected orientation. Cold-start engagement of the
	# IK/physical stack shows a short blend transient; waiting it out keeps glance-phase captures clean.
	var elapsed := 0.0
	while elapsed < timeout_seconds:
		var yaw := float(driver.call("GetHeadYawDegrees", character_id))
		var pitch := float(driver.call("GetHeadPitchDegrees", character_id))
		if absf(yaw - expected_yaw_deg) < 1.5 and absf(pitch - expected_pitch_deg) < 1.5:
			return
		await SceneUtils.wait_seconds(self, 0.1)
		elapsed += 0.1

	SceneUtils.fatal_error_and_quit(
		"AI-009 head of %s did not stabilise at (yaw %.2f°, pitch %.2f°) within %.1fs (last: %.2f°, %.2f°)" % [
			character_id,
			expected_yaw_deg,
			expected_pitch_deg,
			timeout_seconds,
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
	_enable_only(photobooth, rigs)
	await SceneUtils.wait_frames(self, CAPTURE_SETTLE_FRAMES)
	_print_probes(driver, character_id, label)
	await photobooth.capture_screenshots("%s/scenarios/%s" % [OUTPUT_ROOT, scenario_name])


func _assign(driver: Node, photobooth: Photobooth, character_id: String, anchor_name: String) -> void:
	driver.call("AssignLookTarget", character_id, photobooth.get_node("Subject/%s" % anchor_name))


func _clear(driver: Node, character_id: String) -> void:
	driver.call("ClearLookTarget", character_id)


func _run_scenarios(photobooth: Photobooth, driver: Node) -> void:
	# 00 — Neutral baselines for both NPCs (no gaze assignment). The baseline pitch captures the rig rest pose
	# (≈2.7° up) so later return-to-neutral assertions compare against the actual rest, not absolute zero.
	_assert(not bool(driver.call("HasAssignedLookTarget", TALL)), "tall NPC should start with no gaze assignment")
	_assert(absf(float(driver.call("GetHeadYawDegrees", TALL))) < 3.0, "tall NPC should start near neutral yaw")
	await _capture_scenario(photobooth, driver, TALL, TALL_RIGS, "00_tall_neutral", "00_tall_neutral")
	var tall_baseline_pitch := float(driver.call("GetHeadPitchDegrees", TALL))

	_assert(absf(float(driver.call("GetHeadYawDegrees", SHORT))) < 3.0, "short NPC should start near neutral yaw")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "00_short_neutral", "00_short_neutral")
	var short_baseline_pitch := float(driver.call("GetHeadPitchDegrees", SHORT))

	# 01 — UR 1 headline: tall NPC looking down at a shorter character's head height (24° down, beyond the 15°
	# down comfort cone). Warm up engaged on the straight anchor first (the production AI-007 flow always has the
	# NPC engaged before glances), then assign the low anchor: the glance phase holds the head while the eyes
	# carry the downward angle; the settled phase cranes the neck down with the eyes near-centred.
	_assign(driver, photobooth, TALL, "TallStraightAnchor")
	await _wait_head_stable(driver, TALL, 0.0, tall_baseline_pitch, 5.0)

	_assign(driver, photobooth, TALL, "TallLowAnchor")
	await SceneUtils.wait_seconds(self, GLANCE_PHASE_WAIT_SECONDS)
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", TALL)) > 15.0,
		"tall down glance phase should hold the head near the previous sustained aim (head-to-anchor well above 0)")
	_assert(
		await _read_eye_probe(driver, TALL, "GetEyeSeekVertical") > 0.6,
		"tall down glance phase should show eyes carrying the downward angle")
	await _capture_scenario(photobooth, driver, TALL, TALL_RIGS, "01_tall_down_glance_phase", "01_tall_down_glance_phase")

	await SceneUtils.wait_seconds(self, SETTLE_WAIT_SECONDS - GLANCE_PHASE_WAIT_SECONDS)
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", TALL)) < 3.0,
		"tall down settled shot should centre the head on the low anchor")
	_assert(
		float(driver.call("GetHeadPitchDegrees", TALL)) < tall_baseline_pitch - 18.0,
		"tall down settled shot should pitch the head down beyond the eye-only range (neck craning)")
	_assert(
		absf(await _read_eye_probe(driver, TALL, "GetEyeSeekVertical") - 0.5) < 0.18,
		"tall down settled shot should leave the eyes near-centred in their sockets")
	await _capture_scenario(photobooth, driver, TALL, TALL_RIGS, "01_tall_down_settled", "01_tall_down_settled")
	_clear(driver, TALL)
	await _wait_head_stable(driver, TALL, 0.0, tall_baseline_pitch, 4.0)

	# 02 — UR 1 mirror: short NPC looking up at a taller target (24° up, beyond the 10° up cone).
	_assign(driver, photobooth, SHORT, "ShortHighAnchor")
	await _wait_until_centred(driver, SHORT, 6.0)
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", SHORT)) < 3.0,
		"short up settled shot should centre the head on the high anchor")
	_assert(
		float(driver.call("GetHeadPitchDegrees", SHORT)) > short_baseline_pitch + 18.0,
		"short up settled shot should pitch the head up beyond the eye-only range (neck extension)")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "02_short_up_settled", "02_short_up_settled")
	_clear(driver, SHORT)
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	# 03 — UR 2 conversation signature: sustained same-height anchor 10° left (inside the comfort cone and the
	# eye clamps). Glance phase holds the head on the warm-up (straight) aim while the eyes carry the anchor;
	# settled turns the head to face it even though the eyes could have carried it.
	_assign(driver, photobooth, SHORT, "ShortStraightAnchor")
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 5.0)

	_assign(driver, photobooth, SHORT, "ShortConversationAnchor")
	await SceneUtils.wait_seconds(self, GLANCE_PHASE_WAIT_SECONDS)
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT))) < 3.0,
		"conversation glance phase should keep the head on the last sustained aim while the target is in-cone")
	_assert(
		await _read_eye_probe(driver, SHORT, "GetEyeSeekHorizontal") > 0.55,
		"conversation glance phase should show the eyes on the anchor")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "03_conversation_glance_phase", "03_conversation_glance_phase")

	await SceneUtils.wait_seconds(self, SETTLE_WAIT_SECONDS - GLANCE_PHASE_WAIT_SECONDS)
	_assert(
		float(driver.call("GetHeadToAnchorAngleDegrees", SHORT)) < 2.5,
		"conversation settled shot should fully centre the head on the conversation partner")
	_assert(
		absf(await _read_eye_probe(driver, SHORT, "GetEyeSeekHorizontal") - 0.5) < 0.12,
		"conversation settled shot should leave the eyes near eye-neutral once the head faces the partner")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "03_conversation_settled", "03_conversation_settled")
	_clear(driver, SHORT)
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	# 04 — UR 3 brief side glances: an in-cone glance stays purely eyes-only (head holds exactly on the sustained
	# straight anchor), an out-of-cone glance moves the head only the residual distance, and the head eases back
	# after each glance ends.
	_assign(driver, photobooth, SHORT, "ShortStraightAnchor")
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 5.0)

	_assign(driver, photobooth, SHORT, "ShortGlanceInConeAnchor")
	await SceneUtils.wait_seconds(self, EYES_ONLY_GLANCE_WAIT_SECONDS)
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT))) < 3.0,
		"in-cone brief glance must keep the head exactly on the sustained anchor (eyes-only glance)")
	_assert(
		await _read_eye_probe(driver, SHORT, "GetEyeSeekHorizontal") > 0.58,
		"in-cone brief glance should show the eyes on the glance target")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "04_glance_in_cone_mid", "04_glance_in_cone_mid")

	_assign(driver, photobooth, SHORT, "ShortStraightAnchor")
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	_assign(driver, photobooth, SHORT, "ShortGlanceAnchor")
	await SceneUtils.wait_seconds(self, OUT_OF_CONE_GLANCE_WAIT_SECONDS)
	var out_of_cone_yaw := absf(float(driver.call("GetHeadYawDegrees", SHORT)))
	_assert(
		out_of_cone_yaw > 1.0 and out_of_cone_yaw < 20.0,
		"out-of-cone brief glance should move the head only part of the residual (found %.2f° yaw)" % out_of_cone_yaw)
	_assert(
		await _read_eye_probe(driver, SHORT, "GetEyeSeekHorizontal") > 0.7,
		"out-of-cone brief glance should show the eyes still carrying most of the glance angle")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "04_glance_out_of_cone_mid", "04_glance_out_of_cone_mid")

	_assign(driver, photobooth, SHORT, "ShortStraightAnchor")
	await SceneUtils.wait_seconds(self, GLANCE_POST_WAIT_SECONDS)
	_assert(
		absf(float(driver.call("GetHeadYawDegrees", SHORT))) < 3.5,
		"post-glance head should ease back onto the sustained anchor")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "04_glance_post_return", "04_glance_post_return")
	_clear(driver, SHORT)
	await _wait_head_stable(driver, SHORT, 0.0, short_baseline_pitch, 4.0)

	# 05 — UR 5 strain: sustained anchor 85° left (beyond the ±75° envelope). The head cranes toward the
	# envelope edge while the eyes keep tracking past it.
	_assign(driver, photobooth, SHORT, "ShortFarAnchor")
	await SceneUtils.wait_seconds(self, STRAIN_SETTLE_WAIT_SECONDS)
	var strain_yaw := absf(float(driver.call("GetHeadYawDegrees", SHORT)))
	_assert(
		strain_yaw > 50.0,
		"beyond-envelope settled shot should crane the head far toward the anchor (found %.2f° yaw)" % strain_yaw)
	_assert(
		await _read_eye_probe(driver, SHORT, "GetEyeSeekHorizontal") > 0.55,
		"beyond-envelope settled shot should keep the eyes tracking past the strained head")
	await _capture_scenario(photobooth, driver, SHORT, SHORT_RIGS, "05_beyond_envelope_settled", "05_beyond_envelope_settled")

	# 06 — UR 5 release: clearing the gaze eases the head back to neutral. Consecutive captures around the
	# release document the return motion and the settled neutral.
	_clear(driver, SHORT)
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
