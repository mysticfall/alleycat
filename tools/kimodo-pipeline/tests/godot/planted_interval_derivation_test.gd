extends SceneTree

# Derivation checks for ANIM-002 per-foot planted intervals on synthetic clips with known contact and release times.
# Each foot's ball point (the toe-bone rest origin) follows a scripted ground-frame trajectory while the Root bone
# carries root motion, so a planted ball is still in the ground frame but moves in Root-local space.
#
# Finite clip (2.0 s, `entry_forward_left`): the right foot takes the first step and the left foot, the suffix's
# leading foot, is in swing at the locomotion-side end. The right foot rolls on its ball (heel lift 0.30-0.50 s),
# releases at 0.50 s, swings, heel-strikes at 1.20 s with the ball held high until 1.38 s, and makes ball contact at
# 1.40 s. The left foot is planted with a heel lift from 1.20 s and releases at 1.50 s. Variants replace one foot's
# motion to exercise each refusal cause and the ground-slide rule. Looping clip (1.6 s): the left foot is planted
# across the loop seam, releasing at 0.40 s and making contact at 1.20 s; the right foot repeats that trajectory a
# configurable phase later (half a cycle by default), and the left trajectory can be phase-shifted too. Finite
# side-step (1.6 s) and exit (1.2 s) clips script one step per foot.
#
# Every refusal check asserts that exactly one refusal fires and that it names the expected cause.
#
# Expected times come from the scripted trajectories, not from the derivation's own thresholds.

const POLICY := "planted_interval_role_policy_v1.json"
const BOUNDARY_TOLERANCE := 0.035
const KEY_RATE := 120.0
const GROUND := 0.02
const LIFT := 0.08
const TOE_OFFSET := Vector3(0.0, -0.08, 0.12)
const ROOT_SPEED := 0.5
const ENTRY := "entry_forward_left"

var passed := 0
var failed := 0
var intervals: Script
var sampler: Script
var policy: Dictionary


func _init() -> void:
	intervals = _load_helper("planted_intervals.gd")
	sampler = _load_helper("animation_pose_sampler.gd")
	var loaded: Dictionary = intervals.load_policy(_tool_root().path_join("config").path_join(POLICY))
	_expect(not loaded.has("error"), "production role policy loads: %s" % loaded.get("error", ""))
	if loaded.has("error"):
		_finish()
		return
	policy = loaded["policy"]
	var skeleton := _skeleton()
	var model: Dictionary = sampler.skeleton_model(skeleton)
	skeleton.free()
	_expect_finite_clip(model)
	_expect_float_and_sink_do_not_move_intervals(model)
	_expect_looping_clip(model)
	_expect_ground_slide_is_swing(model)
	_expect_weak_motion_is_absorbed(model)
	_expect_verification_refusals(model)
	_expect_structure_refusals(model)
	_expect_transition_lead_foot(model)
	_expect_confined_loop_intents(model)
	_expect_looping_cross_foot_refusals(model)
	_expect_looping_seam_boundary(model)
	_expect_policy_validation()
	_expect_document_validation(model)
	_expect_report(model)
	_finish()


func _finish() -> void:
	print("ANIM002_PLANTED_INTERVAL_DERIVATION_CHECKS=%s passed=%d failed=%d" % ["passed" if failed == 0 else "failed", passed, failed])
	quit(1 if failed > 0 else 0)


func _expect_finite_clip(model: Dictionary) -> void:
	var derived: Dictionary = intervals.derive_clip(_finite_animation({}), ENTRY, false, model, policy)
	_expect((derived["refusals"] as Array).is_empty(), "finite entry clip is not refused: %s" % str(derived["refusals"]))
	_expect_segments(derived, "right", [["planted", 0.0, 0.5], ["swing", 0.5, 1.4], ["planted", 1.4, 2.0]], "finite right foot")
	_expect_segments(derived, "left", [["planted", 0.0, 1.5], ["swing", 1.5, 2.0]], "finite left foot")
	var right := _boundaries(derived, "right")
	_expect(right.size() == 2 and String(right[1]["kind"]) == "contact" and String(right[1]["cause"]) == "height", "ball contact after a held heel strike is height-caused: %s" % str(right))
	for interval in (((derived["diagnostics"] as Dictionary)["right"] as Dictionary)["planted_intervals"] as Array):
		if absf(float(interval["start_seconds"]) - 1.4) <= BOUNDARY_TOLERANCE:
			_expect(float(interval["arrival_descent_m"]) >= 0.05, "the contact at 1.40 s verifies the scripted 0.06 m descent: %s" % str(interval))


func _expect_float_and_sink_do_not_move_intervals(model: Dictionary) -> void:
	var reference: Dictionary = intervals.derive_clip(_finite_animation({}), ENTRY, false, model, policy)
	var shifted: Dictionary = intervals.derive_clip(_finite_animation({"left_offset": 0.03, "right_offset": -0.02}), ENTRY, false, model, policy)
	_expect((shifted["refusals"] as Array).is_empty(), "floating/sinking clip is not refused: %s" % str(shifted["refusals"]))
	for foot in ["left", "right"]:
		_expect(((shifted["feet"] as Dictionary)[foot] as Dictionary)["segments"] == ((reference["feet"] as Dictionary)[foot] as Dictionary)["segments"], "constant %s float/sink leaves the intervals unchanged" % foot)
	var baseline := float(((shifted["feet"] as Dictionary)["left"] as Dictionary)["baseline_ball_height_m"])
	_expect(absf(baseline - (GROUND + 0.03)) <= 0.001, "floating left foot baseline follows its own low ball height: %f" % baseline)


func _expect_looping_clip(model: Dictionary) -> void:
	var derived: Dictionary = intervals.derive_clip(_looping_animation({}), "cyclic_forward_primary", true, model, policy)
	_expect((derived["refusals"] as Array).is_empty(), "looping cyclic clip is not refused: %s" % str(derived["refusals"]))
	_expect_segments(derived, "left", [["planted", 0.0, 0.4], ["swing", 0.4, 1.2], ["planted", 1.2, 1.6]], "looping left foot")
	_expect_segments(derived, "right", [["swing", 0.0, 0.4], ["planted", 0.4, 1.2], ["swing", 1.2, 1.6]], "looping right foot")
	var drift := 0.0
	for foot in ["left", "right"]:
		var planted := ((derived["diagnostics"] as Dictionary)[foot] as Dictionary)["planted_intervals"] as Array
		_expect(planted.size() == 1, "the %s foot's seam-crossing interval is one planted interval: %s" % [foot, str(planted)])
		_expect(_boundaries(derived, foot).size() == 2, "the loop seam is not a %s-foot boundary: %s" % [foot, str(_boundaries(derived, foot))])
		for interval in planted:
			drift = maxf(drift, float((interval as Dictionary)["maximum_planar_drift_m"]))
	_expect(drift <= 0.002, "ground-frame planted balls are still despite root motion and the loop boundary: %f" % drift)


# The right ball comes down at 1.00 s but slides 0.08 m along the ground until 1.20 s: the slide is swing, and the
# speed-caused contact records the slide.
func _expect_ground_slide_is_swing(model: Dictionary) -> void:
	var derived: Dictionary = intervals.derive_clip(_finite_animation({"right": "short_slide"}), ENTRY, false, model, policy)
	_expect((derived["refusals"] as Array).is_empty(), "a clip with a short landing slide is not refused: %s" % str(derived["refusals"]))
	_expect_segments(derived, "right", [["planted", 0.0, 0.5], ["swing", 0.5, 1.2], ["planted", 1.2, 2.0]], "sliding right foot")
	var contact := _boundaries(derived, "right").filter(func(b: Dictionary) -> bool: return String(b["kind"]) == "contact")
	_expect(contact.size() == 1 and String(contact[0]["cause"]) == "speed", "the contact after a ground slide is speed-caused: %s" % str(contact))
	if contact.size() == 1 and contact[0].has("ground_slide_m"):
		_expect(absf(float(contact[0]["ground_slide_m"]) - 0.08) <= 0.02, "the report records the scripted 0.08 m ground slide: %s" % str(contact[0]))


# A 0.02 m nudge of the planted left ball is weak motion: it is merged into the planted interval and reported.
func _expect_weak_motion_is_absorbed(model: Dictionary) -> void:
	var derived: Dictionary = intervals.derive_clip(_finite_animation({"left": "nudge"}), ENTRY, false, model, policy)
	_expect((derived["refusals"] as Array).is_empty(), "a weak nudge inside a planted interval is not refused: %s" % str(derived["refusals"]))
	_expect_segments(derived, "left", [["planted", 0.0, 1.5], ["swing", 1.5, 2.0]], "nudged left foot")
	var absorbed := ((derived["diagnostics"] as Dictionary)["left"] as Dictionary)["absorbed_episodes"] as Array
	_expect(absorbed.size() == 1 and absf(float(absorbed[0]["start_seconds"]) - 0.8) <= 0.05, "the nudge is reported as an absorbed episode: %s" % str(absorbed))


func _expect_verification_refusals(model: Dictionary) -> void:
	# The planted left ball creeps 0.104 m at 0.08 m/s, below the speed tolerance, so only the drift cap sees it.
	_expect_single_refusal(intervals.derive_clip(_finite_animation({"left": "creep"}), ENTRY, false, model, policy), "left foot: planted interval at 0.000 s drifts", "a planted interval over the drift cap")
	# The right ball lands at 0.70 s and slides 0.35 m along the ground until 1.40 s: contact without any descent.
	_expect_single_refusal(intervals.derive_clip(_finite_animation({"right": "long_slide"}), ENTRY, false, model, policy), "has no descent into contact", "a contact without a descent", "right foot: planted interval at 1.4")
	# The left ball vibrates within 0.012 m of its release point for 0.30 s before it moves off: no lift.
	_expect_single_refusal(intervals.derive_clip(_finite_animation({"left": "jitter"}), ENTRY, false, model, policy), "left foot: planted interval at 0.000 s has no lift after its end", "a release without a rise or departure")
	# The planted left ball jerks 0.06 m in 0.05 s: too much motion to merge into the planted interval.
	_expect_single_refusal(intervals.derive_clip(_finite_animation({"left": "jerk"}), ENTRY, false, model, policy), "beyond the absorption limits", "a merged run beyond the absorption limits", "left foot: merged run at 0.7")


func _expect_structure_refusals(model: Dictionary) -> void:
	var finite := _finite_animation({})
	_expect_single_refusal(intervals.derive_clip(_finite_animation({"right": "static"}), ENTRY, false, model, policy), "right foot: role structure expects 1 swing episode(s); the signals show 0", "an entry whose stepping foot never steps")
	_expect_single_refusal(intervals.derive_clip(finite, "idle_contact_free", false, model, policy), "role structure idle does not accept a finite clip", "a finite clip as idle")
	_expect_single_refusal(intervals.derive_clip(finite, "cyclic_forward_primary", false, model, policy), "role structure cyclic_travel does not accept a finite clip", "a finite clip as cyclic travel")
	_expect_single_refusal(intervals.derive_clip(_looping_animation({}), ENTRY, true, model, policy), "role structure entry does not accept a looping clip", "a looping clip as an entry")
	_expect_single_refusal(intervals.derive_clip(finite, "undeclared_capability", false, model, policy), "capability undeclared_capability has no role structure", "an undeclared capability")
	var no_toes := model.duplicate(true)
	(no_toes["bones"] as Dictionary).erase("LeftToes")
	_expect_single_refusal(intervals.derive_clip(finite, ENTRY, false, no_toes, policy), "skeleton has no left ball joint LeftToes", "a skeleton without the ball joint")


# The role suffix names the leading foot: the foot in swing at the locomotion-side boundary. In the finite fixture the
# right foot steps first and the left foot is in swing at the end, so it is `entry_forward_left` and not
# `entry_forward_right`; the exit fixture has its left foot in swing at the start, so it is `exit_forward_left`.
func _expect_transition_lead_foot(model: Dictionary) -> void:
	var entry: Dictionary = intervals.derive_clip(_finite_animation({}), ENTRY, false, model, policy)
	_expect((entry["refusals"] as Array).is_empty() and String((entry["diagnostics"] as Dictionary)["lead_foot"]) == "left", "the entry whose left foot is in swing at the end leads with the left foot: %s" % str(entry["refusals"]))
	_expect_single_refusal(intervals.derive_clip(_finite_animation({}), "entry_forward_right", false, model, policy), "role structure entry expects the lead right foot in swing at the locomotion-side end boundary; it is planted", "an entry whose suffix foot is planted at the end boundary")
	var exit: Dictionary = intervals.derive_clip(_exit_animation(), "exit_forward_left", false, model, policy)
	_expect((exit["refusals"] as Array).is_empty(), "the exit whose left foot is in swing at the start is not refused: %s" % str(exit["refusals"]))
	_expect_segments(exit, "left", [["swing", 0.0, 0.3], ["planted", 0.3, 1.2]], "exit left foot")
	_expect_segments(exit, "right", [["planted", 0.0, 0.5], ["swing", 0.5, 0.9], ["planted", 0.9, 1.2]], "exit right foot")
	_expect_single_refusal(intervals.derive_clip(_exit_animation(), "exit_forward_right", false, model, policy), "role structure exit expects the lead right foot in swing at the locomotion-side start boundary; it is planted", "an exit whose suffix foot is planted at the start boundary")
	_expect_single_refusal(intervals.derive_clip(_finite_animation({}), "exit_forward_left", false, model, policy), "role structure exit expects the lead left foot in swing at the locomotion-side start boundary; it is planted", "an entry clip as an exit")
	# A trail foot that swings after the lead foot, although the lead foot is still in swing at the end, breaks the
	# entry's order: the non-lead foot steps first.
	_expect_single_refusal(intervals.derive_clip(_entry_out_of_order_animation(), ENTRY, false, model, policy), "role structure expects the right (trail) foot to swing first", "an entry whose lead foot swings before the trail foot")


# ANIM-002 TR4: confined manoeuvres may loop or not. Looping clips are fitted circularly, finite clips linearly.
func _expect_confined_loop_intents(model: Dictionary) -> void:
	var finite: Dictionary = intervals.derive_clip(_side_step_animation({}), "confined_side_step_left", false, model, policy)
	_expect((finite["refusals"] as Array).is_empty(), "a finite side-step is accepted: %s" % str(finite["refusals"]))
	_expect_segments(finite, "left", [["planted", 0.0, 0.3], ["swing", 0.3, 0.7], ["planted", 0.7, 1.6]], "finite side-step left foot")
	_expect_segments(finite, "right", [["planted", 0.0, 0.9], ["swing", 0.9, 1.3], ["planted", 1.3, 1.6]], "finite side-step right foot")
	_expect_single_refusal(intervals.derive_clip(_side_step_animation({}), "confined_side_step_right", false, model, policy), "role structure expects the right (lead) foot to swing first", "a finite side-step led by the wrong foot")
	var looping: Dictionary = intervals.derive_clip(_looping_animation({"contact": 0.8, "right_shift": 0.7}), "confined_side_step_left", true, model, policy)
	_expect((looping["refusals"] as Array).is_empty(), "a looping side-step is accepted: %s" % str(looping["refusals"]))
	_expect_segments(looping, "right", [["planted", 0.0, 1.1], ["swing", 1.1, 1.5], ["planted", 1.5, 1.6]], "looping side-step right foot")


# Looping cross-foot checks: overlap and lead order for confined manoeuvres, and the circular planted phase offset
# for cyclic travel (a right foot 0.9 cycle behind is 0.1 cycle ahead, and 0.65 behind is 0.35 ahead).
func _expect_looping_cross_foot_refusals(model: Dictionary) -> void:
	# Left swing over [0.4, 0.8] s against right swing over [0.6, 1.0] s: they overlap about 0.2 s.
	var overlap: Dictionary = intervals.derive_clip(_looping_animation({"contact": 0.8, "right_shift": 0.2}), "confined_side_step_left", true, model, policy)
	_expect_single_refusal(overlap, "role structure expects the feet to swing one at a time; the swings overlap 0.", "overlapping swings in a looping manoeuvre")
	if (overlap["refusals"] as Array).size() == 1:
		var seconds := float(String(overlap["refusals"][0]).get_slice("overlap ", 1).trim_suffix(" s"))
		_expect(absf(seconds - 0.2) <= BOUNDARY_TOLERANCE * 2.0, "the reported overlap matches the scripted 0.2 s: %s" % str(overlap["refusals"]))
	# Left swing centred at 0.6 s of the loop, right swing at 1.4 s: the left foot swings first, not the right.
	var order: Dictionary = intervals.derive_clip(_looping_animation({"contact": 0.8}), "confined_side_step_right", true, model, policy)
	_expect_single_refusal(order, "role structure expects the right (lead) foot to swing first", "a looping manoeuvre led by the wrong foot")
	for shift in [0.16, 1.44]:
		var close: Dictionary = intervals.derive_clip(_looping_animation({"right_shift": shift}), "cyclic_forward_primary", true, model, policy)
		_expect_single_refusal(close, "role structure expects alternating feet; the planted phase offset is ", "planted phases %.2f s apart" % shift)
		if (close["refusals"] as Array).size() == 1:
			var offset := float(String(close["refusals"][0]).get_slice("offset is ", 1).get_slice(" ", 0))
			_expect(absf(offset - 0.1) <= 0.02, "planted phases %.2f s apart are a circular 0.1 cycle offset: %s" % [shift, str(close["refusals"])])
	for shift in [0.56, 1.04]:
		var apart: Dictionary = intervals.derive_clip(_looping_animation({"right_shift": shift}), "cyclic_forward_primary", true, model, policy)
		_expect((apart["refusals"] as Array).is_empty(), "planted phases %.2f s apart are a circular 0.35 cycle offset: %s" % [shift, str(apart["refusals"])])


# A looping state change at sample 0 is placed half a sample before it, at L - dt/2, like every other boundary; the
# seam split then puts the half-sample tail of the new state at the end of the clip.
func _expect_looping_seam_boundary(model: Dictionary) -> void:
	var length := 1.6
	var dt := length / roundi(length * float(policy["sampling_rate_hz"]))
	var reference: Dictionary = intervals.derive_clip(_looping_animation({}), "cyclic_forward_primary", true, model, policy)
	var contact := -1.0
	for boundary in _boundaries(reference, "left"):
		if String(boundary["kind"]) == "contact":
			contact = float(boundary["time_seconds"])
	var contact_sample := roundi(contact / dt + 0.5)
	# Shifting the left trajectory by whole samples moves its contact to sample 0 without changing any sampled signal.
	var shift := length - float(contact_sample) * dt
	var shifted: Dictionary = intervals.derive_clip(_looping_animation({"left_shift": shift, "right_shift": fposmod(0.8 + shift, length)}), "cyclic_forward_primary", true, model, policy)
	_expect((shifted["refusals"] as Array).is_empty(), "a looping clip with a contact at sample 0 is not refused: %s" % str(shifted["refusals"]))
	var seam := length - 0.5 * dt
	var segments := ((shifted["feet"] as Dictionary)["left"] as Dictionary)["segments"] as Array
	_expect(segments.size() == 3 and String(segments[0]["state"]) == "planted" and String(segments[-1]["state"]) == "planted" and absf(float(segments[-1]["start_seconds"]) - seam) <= 0.000001 and float(segments[0]["start_seconds"]) == 0.0, "the seam contact is published at L - dt/2 and split at the clip boundary: %s" % str(segments))
	var contacts := _boundaries(shifted, "left").filter(func(b: Dictionary) -> bool: return String(b["kind"]) == "contact")
	_expect(contacts.size() == 1 and absf(float(contacts[0]["time_seconds"]) - seam) <= 0.000001, "the seam contact is reported at L - dt/2: %s" % str(contacts))
	var planted := ((shifted["diagnostics"] as Dictionary)["left"] as Dictionary)["planted_intervals"] as Array
	_expect(planted.size() == 1 and absf(float(planted[0]["start_seconds"]) - seam) <= 0.000001, "the seam planted interval starts at L - dt/2: %s" % str(planted))


func _expect_policy_validation() -> void:
	var mutations := [
		["unknown field", func(p: Dictionary) -> void: p["legacy"] = true, "policy must declare exactly"],
		["missing tolerance", func(p: Dictionary) -> void: (p["verification"] as Dictionary).erase("minimum_lift_rise_m"), "verification must declare exactly"],
		["non-positive tolerance", func(p: Dictionary) -> void: (p["structure_fit"] as Dictionary)["absorb_maximum_travel_m"] = 0.0, "must be a positive number"],
		["unknown structure", func(p: Dictionary) -> void: (p["capabilities"] as Dictionary)["confined_pivot_left"] = {"structure": "hop"}, "names an unknown structure"],
		["unordered lead", func(p: Dictionary) -> void: (p["capabilities"] as Dictionary)["entry_forward_left"] = {"structure": "entry"}, "must name the lead foot"],
		["retired structure field", func(p: Dictionary) -> void: ((p["structures"] as Dictionary)["exit"] as Dictionary)["lead_swings_first"] = true, "unsupported field lead_swings_first"],
		["non-alternating sequence", func(p: Dictionary) -> void: ((((p["structures"] as Dictionary)["exit"] as Dictionary)["sequences"] as Dictionary)["finite"] as Dictionary)["lead"] = ["swing", "swing"], "must alternate"],
		["non-circular loop", func(p: Dictionary) -> void: ((((p["structures"] as Dictionary)["cyclic_travel"] as Dictionary)["sequences"] as Dictionary)["looping"] as Dictionary)["lead"] = ["planted", "swing", "planted"], "must be circular"],
		["unknown loop intent", func(p: Dictionary) -> void: (((p["structures"] as Dictionary)["idle"] as Dictionary)["sequences"] as Dictionary)["sometimes"] = {"lead": ["planted"], "trail": ["planted"]}, "unknown loop intent sometimes"],
		["lead planted at the boundary", func(p: Dictionary) -> void: ((((p["structures"] as Dictionary)["entry"] as Dictionary)["sequences"] as Dictionary)["finite"] as Dictionary)["lead"] = ["planted", "swing", "planted"], "must be in swing at the end locomotion boundary"],
		["unknown first swing", func(p: Dictionary) -> void: ((p["structures"] as Dictionary)["entry"] as Dictionary)["first_swing"] = "both", "first_swing must be lead or trail"],
		["non-circular phase range", func(p: Dictionary) -> void: ((p["structures"] as Dictionary)["cyclic_travel"] as Dictionary)["planted_phase_offset_range"] = [0.3, 0.7], "within [0, 0.5]"],
	]
	for mutation in mutations:
		var copy := policy.duplicate(true)
		(mutation[1] as Callable).call(copy)
		var error: String = intervals.validate_policy(copy)
		_expect(error.contains(String(mutation[2])), "policy %s is rejected: %s" % [mutation[0], error])


func _expect_document_validation(model: Dictionary) -> void:
	var derived: Dictionary = intervals.derive_clip(_finite_animation({}), ENTRY, false, model, policy)
	var clip := {"identity": "fixture_entry", "role": "fixture_role", "capability": ENTRY, "looping": false, "duration_seconds": 2.0, "feet": derived["feet"]}
	var document: Dictionary = intervals.document({"character": "fixture", "package": "locomotion", "variant": "fixture"}, [clip], policy, "policy-digest", {"scene": "fixture"}, "library-digest")
	var expected := {
		"character": "fixture", "package": "locomotion", "variant": "fixture", "policy_sha256": "policy-digest", "runtime_library_sha256": "library-digest",
		"clips": [{"identity": "fixture_entry", "role": "fixture_role", "capability": ENTRY, "loop_intent": false, "duration_seconds": 2.0}],
	}
	var round_trip: Variant = JSON.parse_string(JSON.stringify(document, "  "))
	_expect(intervals.validate_document(round_trip, expected).is_empty(), "a derived document validates after a JSON round trip: %s" % intervals.validate_document(round_trip, expected))
	var mutations := [
		["schema_version", func(d: Dictionary) -> void: d["schema_version"] = 2, "schema version is unknown or mismatched"],
		["library", func(d: Dictionary) -> void: (d["derivation"] as Dictionary)["runtime_library_sha256"] = "other", "different runtime library"],
		["policy", func(d: Dictionary) -> void: ((d["derivation"] as Dictionary)["policy"] as Dictionary)["sha256"] = "other", "policy is stale or mismatched"],
		["tool version", func(d: Dictionary) -> void: (d["derivation"] as Dictionary)["tool_version"] = 1, "tool or version is unknown"],
		["tolerances", func(d: Dictionary) -> void: ((d["derivation"] as Dictionary)["verification"] as Dictionary).erase("lift_window_s"), "tolerances are invalid"],
		["missing foot", func(d: Dictionary) -> void: (((d["clips"] as Array)[0] as Dictionary)["feet"] as Dictionary).erase("right"), "must cover exactly both feet"],
		["gap", func(d: Dictionary) -> void: ((((((d["clips"] as Array)[0] as Dictionary)["feet"] as Dictionary)["left"] as Dictionary)["segments"] as Array)[1] as Dictionary)["start_seconds"] = 1.6, "contiguous"],
		["short coverage", func(d: Dictionary) -> void: ((((((d["clips"] as Array)[0] as Dictionary)["feet"] as Dictionary)["left"] as Dictionary)["segments"] as Array)[1] as Dictionary)["end_seconds"] = 1.9, "cover the whole clip"],
		["unknown clip", func(d: Dictionary) -> void: ((d["clips"] as Array)[0] as Dictionary)["identity"] = "other", "absent from the package"],
		["capability", func(d: Dictionary) -> void: ((d["clips"] as Array)[0] as Dictionary)["capability"] = "exit_forward_left", "role, capability, or loop intent differs"],
	]
	for mutation in mutations:
		var copy := (round_trip as Dictionary).duplicate(true)
		(mutation[1] as Callable).call(copy)
		var error: String = intervals.validate_document(copy, expected)
		_expect(error.contains(String(mutation[2])), "%s mutation is rejected: %s" % [mutation[0], error])
	var uncovered := expected.duplicate(true)
	(uncovered["clips"] as Array).append({"identity": "fixture_other", "role": "other", "capability": "exit_forward_left", "loop_intent": false, "duration_seconds": 1.0})
	_expect(String(intervals.validate_document(round_trip, uncovered)).contains("does not cover every package clip"), "a package clip without intervals is rejected")


func _expect_report(model: Dictionary) -> void:
	var clips: Array = []
	for pair in [["fixture_entry", ENTRY, _finite_animation({}), false], ["fixture_refused", "entry_forward_right", _finite_animation({}), false]]:
		var derived: Dictionary = intervals.derive_clip(pair[2], pair[1], pair[3], model, policy)
		clips.append({"identity": pair[0], "role": "fixture_role", "capability": pair[1], "looping": pair[3], "duration_seconds": 2.0, "feet": derived["feet"], "diagnostics": derived["diagnostics"], "refusals": derived["refusals"]})
	var identity := {"character": "fixture", "package": "locomotion", "variant": "fixture"}
	var first: Dictionary = intervals.report(identity, "intervals-digest", "policy-digest", "library-digest", clips)
	var reversed := clips.duplicate(true)
	reversed.reverse()
	var second: Dictionary = intervals.report(identity, "intervals-digest", "policy-digest", "library-digest", reversed)
	_expect(intervals.serialise(first) == intervals.serialise(second), "the derivation report serialises identically regardless of clip order")
	_expect(first.keys() == intervals.REPORT_KEYS and first["refused"] == ["fixture_refused"], "the derivation report carries exactly its declared fields and the refused clips: %s" % str(first.keys()))
	var boundaries := ((((first["clips"] as Array)[0] as Dictionary)["diagnostics"] as Dictionary)["right"] as Dictionary)["boundaries"] as Array
	_expect(boundaries.size() == 2 and String(boundaries[1]["cause"]) == "height", "the report keeps each boundary's cause: %s" % str(boundaries))


# Finite entry. Options: "left_offset"/"right_offset" shift a foot's height; "right" is "step" (release 0.5, heel
# strike 1.2, contact 1.4), "static", "short_slide" (down at 1.0, slides 0.08 m to 1.2), or "long_slide" (down at
# 0.7, slides 0.35 m to 1.4); "left" is "roll" (heel lift from 1.2, release 1.5), "creep", "nudge", "jerk", or
# "jitter" (vibrates 1.5-1.8 before moving off).
func _finite_animation(options: Dictionary) -> Animation:
	var length := 2.0
	var right_offset := float(options.get("right_offset", 0.0))
	var left_offset := float(options.get("left_offset", 0.0))
	var right_mode := String(options.get("right", "step"))
	var left_mode := String(options.get("left", "roll"))
	var right_start := Vector3(-0.1, 0.0, 0.0)
	var right_end := Vector3(-0.1, 0.0, -0.7)
	var left_start := Vector3(0.1, 0.0, -0.1)
	var right := func(t: float) -> Array:
		var ground := GROUND + right_offset
		match right_mode:
			"static":
				return [right_start + Vector3(0.0, ground, 0.0), 0.0]
			"short_slide":
				if t <= 0.5:
					return [right_start + Vector3(0.0, ground, 0.0), 0.0]
				if t < 1.0:
					var height := LIFT * clampf((t - 0.5) / 0.03, 0.0, 1.0) * clampf((1.0 - t) / 0.03, 0.0, 1.0)
					return [right_start + Vector3(0.0, ground + height, -0.5 * (t - 0.5) / 0.5), 0.0]
				return [right_start + Vector3(0.0, ground, -0.5 - 0.4 * (minf(t, 1.2) - 1.0)), 0.0]
			"long_slide":
				if t <= 0.5:
					return [right_start + Vector3(0.0, ground, 0.0), 0.0]
				if t < 0.7:
					var height := LIFT * clampf((t - 0.5) / 0.03, 0.0, 1.0) * clampf((0.7 - t) / 0.03, 0.0, 1.0)
					return [right_start + Vector3(0.0, ground + height, -0.2 * (t - 0.5) / 0.2), 0.0]
				return [right_start + Vector3(0.0, ground, -0.2 - 0.5 * (minf(t, 1.4) - 0.7)), 0.0]
		if t <= 0.5:
			return [right_start + Vector3(0.0, ground, 0.0), -deg_to_rad(30.0) * clampf((t - 0.3) / 0.2, 0.0, 1.0)]
		if t < 1.2:
			var progress := (t - 0.5) / 0.7
			return [right_start.lerp(right_end, progress) + Vector3(0.0, ground + LIFT * clampf((t - 0.5) / 0.03, 0.0, 1.0), 0.0), 0.0]
		if t < 1.38:
			return [right_end + Vector3(0.0, ground + 0.06, 0.0), deg_to_rad(25.0)]
		var drop := clampf((t - 1.38) / 0.02, 0.0, 1.0)
		return [right_end + Vector3(0.0, ground + 0.06 * (1.0 - drop), 0.0), deg_to_rad(25.0) * (1.0 - drop)]
	var left := func(t: float) -> Array:
		var ground := GROUND + left_offset
		var planted := left_start
		match left_mode:
			"creep":
				planted += Vector3(0.0, 0.0, -0.08 * (clampf(t, 0.2, 1.5) - 0.2))
			"nudge":
				planted += Vector3(0.0, 0.0, -0.02 * _smoothstep((t - 0.8) / 0.04))
			"jerk":
				planted += Vector3(0.0, 0.0, -0.06 * _smoothstep((t - 0.8) / 0.05))
		if left_mode == "jitter":
			if t <= 1.5:
				return [planted + Vector3(0.0, ground, 0.0), 0.0]
			if t < 1.8:
				return [planted + Vector3(0.012 * sin(TAU * 10.0 * (t - 1.5)), ground, 0.0), 0.0]
			return [planted + Vector3(0.0, 0.0, -1.0 * (t - 1.8)) + Vector3(0.0, ground + LIFT * clampf((t - 1.8) / 0.03, 0.0, 1.0), 0.0), 0.0]
		if t <= 1.5:
			return [planted + Vector3(0.0, ground, 0.0), -deg_to_rad(30.0) * clampf((t - 1.2) / 0.3, 0.0, 1.0)]
		return [planted + Vector3(0.0, 0.0, -1.0 * (t - 1.5)) + Vector3(0.0, ground + LIFT * clampf((t - 1.5) / 0.03, 0.0, 1.0), 0.0), 0.0]
	return _animation(length, false, left, right)


# Looping cycle: 0.8 m root advance per 1.6 s; each foot travels one cycle advance (0.8 m) during its 0.8 s swing.
# The base trajectory is planted at z = -0.4 over [1.2, 1.6] and, one cycle earlier, at z = +0.4 over [0.0, 0.4].
# Options: "contact" (seconds, default 1.2) ends the base swing earlier, planting at z = -0.4 from then on;
# "left_shift" and "right_shift" (seconds, right default 0.8) delay each foot's copy of the base trajectory; a
# delayed sample before the shift reads the previous cycle, one cycle advance further back.
func _looping_animation(options: Dictionary) -> Animation:
	var length := 1.6
	var advance := ROOT_SPEED * length
	var contact := float(options.get("contact", 1.2))
	var base := func(u: float) -> Vector3:
		if u <= 0.4:
			return Vector3(0.0, GROUND, -0.4 + advance)
		if u < contact:
			var progress := (u - 0.4) / (contact - 0.4)
			return Vector3(0.0, GROUND + LIFT * _ramp(u, 0.4, contact), -0.4 + advance * (1.0 - progress))
		return Vector3(0.0, GROUND, -0.4)
	var shifted := func(t: float, shift: float, x: float) -> Array:
		var u := t - shift
		var z := 0.0
		if u < 0.0:
			u += length
			z = advance
		return [(base.call(u) as Vector3) + Vector3(x, 0.0, z), 0.0]
	var left_shift := float(options.get("left_shift", 0.0))
	var right_shift := float(options.get("right_shift", 0.8))
	return _animation(length, true, func(t: float) -> Array: return shifted.call(t, left_shift, 0.1), func(t: float) -> Array: return shifted.call(t, right_shift, -0.1))


# Finite side-step: the left foot steps 0.3 m sideways over [0.3, 0.7] s and the right foot follows over [0.9, 1.3] s.
# Options override "length" and each foot's step window ("left_start", "left_end", "right_start", "right_end").
func _side_step_animation(options: Dictionary) -> Animation:
	var length := float(options.get("length", 1.6))
	var left_window := [float(options.get("left_start", 0.3)), float(options.get("left_end", 0.7))]
	var right_window := [float(options.get("right_start", 0.9)), float(options.get("right_end", 1.3))]
	var left := func(t: float) -> Array: return _step(t, left_window[0], left_window[1], Vector3(0.1, 0.0, 0.0), Vector3(0.4, 0.0, 0.0))
	var right := func(t: float) -> Array: return _step(t, right_window[0], right_window[1], Vector3(-0.1, 0.0, 0.0), Vector3(0.2, 0.0, 0.0))
	return _animation(length, false, left, right)


# Finite entry (2.0 s) whose left (lead) foot starts its swing at 0.50 s, before the right (trail) foot's step over
# [0.8, 1.2] s, and is still in swing at the end.
func _entry_out_of_order_animation() -> Animation:
	var left := func(t: float) -> Array: return _step(t, 0.5, 2.5, Vector3(0.1, 0.0, 0.0), Vector3(0.1, 0.0, -1.0))
	var right := func(t: float) -> Array: return _step(t, 0.8, 1.2, Vector3(-0.1, 0.0, 0.0), Vector3(-0.1, 0.0, -0.6))
	return _animation(2.0, false, left, right)


# Finite exit (1.2 s): the left foot is in swing at the start and lands at 0.30 s; the right foot steps over
# [0.5, 0.9] s.
func _exit_animation() -> Animation:
	var left := func(t: float) -> Array: return _step(t, -0.3, 0.3, Vector3(0.1, 0.0, 0.5), Vector3(0.1, 0.0, -0.1))
	var right := func(t: float) -> Array: return _step(t, 0.5, 0.9, Vector3(-0.1, 0.0, 0.0), Vector3(-0.1, 0.0, -0.6))
	return _animation(1.2, false, left, right)


# One ground-frame step: planted at `from` until `start`, swinging (lifted by LIFT with 0.03 s ramps) to `to`, planted
# at `to` from `end`.
func _step(t: float, start: float, end: float, from: Vector3, to: Vector3) -> Array:
	var ground := Vector3(0.0, GROUND, 0.0)
	if t <= start:
		return [from + ground, 0.0]
	if t >= end:
		return [to + ground, 0.0]
	return [from.lerp(to, (t - start) / (end - start)) + ground + Vector3(0.0, LIFT * _ramp(t, start, end), 0.0), 0.0]


func _ramp(t: float, start: float, end: float) -> float:
	return minf(clampf((t - start) / 0.03, 0.0, 1.0), clampf((end - t) / 0.03, 0.0, 1.0))


func _smoothstep(x: float) -> float:
	var clamped := clampf(x, 0.0, 1.0)
	return clamped * clamped * (3.0 - 2.0 * clamped)


# Keys Root motion plus each foot bone so that its toe-bone rest origin (the ball) follows the scripted ground-frame
# trajectory with the scripted pitch about the lateral axis.
func _animation(length: float, looping: bool, left: Callable, right: Callable) -> Animation:
	var animation := Animation.new()
	animation.length = length
	animation.loop_mode = Animation.LOOP_LINEAR if looping else Animation.LOOP_NONE
	var root_position := _track(animation, "Root", Animation.TYPE_POSITION_3D)
	var root_rotation := _track(animation, "Root", Animation.TYPE_ROTATION_3D)
	var tracks := {}
	for foot in ["LeftFoot", "RightFoot"]:
		tracks[foot] = [_track(animation, foot, Animation.TYPE_POSITION_3D), _track(animation, foot, Animation.TYPE_ROTATION_3D)]
	var keys := roundi(length * KEY_RATE)
	for key in keys + 1:
		var t := length * float(key) / float(keys)
		var root := Transform3D(Basis.IDENTITY, Vector3(0.0, 0.0, -ROOT_SPEED * t))
		animation.position_track_insert_key(root_position, t, root.origin)
		animation.rotation_track_insert_key(root_rotation, t, Quaternion.IDENTITY)
		var hips := root * Transform3D(Basis.IDENTITY, Vector3(0.0, 1.0, 0.0))
		for foot in ["LeftFoot", "RightFoot"]:
			var state: Array = (left if foot == "LeftFoot" else right).call(t)
			var basis := Basis(Vector3.RIGHT, float(state[1]))
			var global := Transform3D(basis, (state[0] as Vector3) - basis * TOE_OFFSET)
			var local := hips.affine_inverse() * global
			animation.position_track_insert_key(tracks[foot][0], t, local.origin)
			animation.rotation_track_insert_key(tracks[foot][1], t, local.basis.get_rotation_quaternion())
	return animation


func _track(animation: Animation, bone: String, type: Animation.TrackType) -> int:
	var track := animation.add_track(type)
	animation.track_set_path(track, NodePath("%%GeneralSkeleton:%s" % bone))
	return track


func _skeleton() -> Skeleton3D:
	var skeleton := Skeleton3D.new()
	skeleton.name = "GeneralSkeleton"
	var rests := [
		["Root", -1, Vector3.ZERO],
		["Hips", 0, Vector3(0.0, 1.0, 0.0)],
		["LeftFoot", 1, Vector3(0.1, -0.9, 0.0)],
		["LeftToes", 2, TOE_OFFSET],
		["RightFoot", 1, Vector3(-0.1, -0.9, 0.0)],
		["RightToes", 4, TOE_OFFSET],
	]
	for rest in rests:
		var bone := skeleton.add_bone(String(rest[0]))
		if int(rest[1]) >= 0:
			skeleton.set_bone_parent(bone, int(rest[1]))
		skeleton.set_bone_rest(bone, Transform3D(Basis.IDENTITY, rest[2] as Vector3))
	return skeleton


func _boundaries(derived: Dictionary, foot: String) -> Array:
	var diagnostics := derived["diagnostics"] as Dictionary
	return [] if not diagnostics.has(foot) else ((diagnostics[foot] as Dictionary)["boundaries"] as Array)


func _expect_segments(derived: Dictionary, foot: String, expected: Array, label: String) -> void:
	var segments := ((derived["feet"] as Dictionary)[foot] as Dictionary)["segments"] as Array
	_expect(segments.size() == expected.size(), "%s has %d segments: %s" % [label, expected.size(), str(segments)])
	if segments.size() != expected.size():
		return
	for index in expected.size():
		var actual := segments[index] as Dictionary
		var wanted := expected[index] as Array
		_expect(String(actual["state"]) == String(wanted[0]), "%s segment %d is %s" % [label, index, wanted[0]])
		_expect(absf(float(actual["start_seconds"]) - float(wanted[1])) <= BOUNDARY_TOLERANCE, "%s segment %d starts at %.3f s: %s" % [label, index, float(wanted[1]), actual])
		_expect(absf(float(actual["end_seconds"]) - float(wanted[2])) <= BOUNDARY_TOLERANCE, "%s segment %d ends at %.3f s: %s" % [label, index, float(wanted[2]), actual])


# Exactly one refusal fires and it contains every expected fragment of its cause.
func _expect_single_refusal(derived: Dictionary, fragment: String, label: String, detail: String = "") -> void:
	var refusals := derived["refusals"] as Array
	var matches := refusals.size() == 1 and String(refusals[0]).contains(fragment) and (detail.is_empty() or String(refusals[0]).contains(detail))
	_expect(matches, "%s is refused once for its cause (%s): %s" % [label, fragment, str(refusals)])


func _expect(condition: bool, message: String) -> void:
	if not condition:
		push_error(message)
		failed += 1
		return
	passed += 1


func _tool_root() -> String:
	return String(get_script().resource_path).get_base_dir().get_base_dir().get_base_dir()


func _load_helper(filename: String) -> Script:
	return load(_tool_root().path_join("godot/lib").path_join(filename).simplify_path()) as Script
