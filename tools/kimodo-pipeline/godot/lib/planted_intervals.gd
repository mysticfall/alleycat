class_name PlantedIntervals
extends RefCounted

# ANIM-002 per-foot planted intervals: role-aware derivation from a clip's ball joints, independent verification of
# every planted interval, and the published document contract shared by the derivation command, the packager, and the
# package validator.
#
# Planted means weight-bearing: the ball point (the rest-pose origin of the toe bone, sampled in skeleton space) is
# down and effectively stationary. The Root bone carries the clip's root motion, so skeleton space is the ground frame
# and planar ball speed is the foot's speed over the ground. A ball sliding along the ground faster than the policy
# tolerance is swing.
#
# Derivation, per foot:
#   1. Signals: ball height above that foot's own low-percentile baseline in the clip, and windowed planar speed.
#   2. Candidate classification with hysteresis: planted while both signals stay inside the enter/exit tolerances.
#   3. Role-structure fit: short planted gaps inside a motion episode join the swing; the role's expected number of
#      swing episodes is taken as the strongest episodes (planar travel plus weighted peak height); every remaining
#      episode is merged into its planted neighbours only if it is weak enough to absorb. The fitted state sequence
#      must match the role's expected sequence for the clip's loop intent, and the cross-foot checks must hold.
#   4. Boundary evidence: each surviving boundary records whether height or speed placed it, and the ground-level
#      slide at every speed-caused boundary.
#   5. Independent verification with displacement criteria the classifier does not use: the ball descends into
#      contact before each planted start, rises or leaves after each planted end, and drifts within the cap inside
#      each planted interval.
#
# The capability's lead foot is the foot its structure names. For transitions (entries and exits) it is the leading
# foot of the role suffix: the foot in swing at the clip's locomotion-side boundary (the end of an entry, the start of
# an exit), which is not the first foot to step in an entry.
#
# Looping clips are fitted circularly. A planted or swing interval that crosses the loop seam is one interval for the
# fit, verification, and report; the published segments split it at the clip boundary, so a looping clip's first and
# last segments of the same state are the two halves of one interval and neither is subject to a minimum length.
# Every looping state change, including one between the last and first samples, is placed half a sample before the
# sample that enters the new state, wrapped into the clip: a change at sample 0 is published at L - dt/2, and the
# seam split then lies inside that sample's interval.

const Sampler := preload("animation_pose_sampler.gd")

const SCHEMA_VERSION := 1
const CONTRACT := "anim002_per_foot_planted_intervals_v1"
const FILENAME := "per_foot_planted_intervals_v1.json"
const CATALOGUE_FIELD := "per_foot_planted_intervals"
const TOOL := "derive_planted_intervals"
const TOOL_VERSION := 3
const REPORT_SCHEMA_VERSION := 1
const REPORT_CONTRACT := "anim002_per_foot_planted_interval_derivation_report_v1"
const REPORT_FILENAME := "per_foot_planted_interval_derivation_report_v1.json"
const POLICY_SCHEMA_VERSION := 3
const POLICY_IDENTITY := "planted_interval_role_policy_v1"
const POLICY_FILENAME := "planted_interval_role_policy_v1.json"
const PLANTED := "planted"
const SWING := "swing"
const FEET := ["left", "right"]
const LOOPING := "looping"
const FINITE := "finite"
const LEAD := "lead"
const TRAIL := "trail"
const BOUNDARY_START := "start"
const BOUNDARY_END := "end"
const CAUSE_HEIGHT := "height"
const CAUSE_SPEED := "speed"
const DURATION_TOLERANCE := 0.000001
const TIME_DECIMALS := 0.000001

const DOCUMENT_KEYS := ["schema_version", "contract", "character", "package", "variant", "derivation", "clips"]
const DERIVATION_KEYS := ["tool", "tool_version", "policy", "ball_joints", "ball_point", "baseline", "sampling_rate_hz", "detection", "structure_fit", "verification", "skeleton", "runtime_library_sha256"]
const CLIP_KEYS := ["identity", "role", "capability", "looping", "duration_seconds", "feet"]
const FOOT_KEYS := ["baseline_ball_height_m", "segments"]
const SEGMENT_KEYS := ["state", "start_seconds", "end_seconds"]
const POLICY_KEYS := ["schema_version", "identity", "ball_joints", "sampling_rate_hz", "detection", "structure_fit", "verification", "structures", "capabilities"]
const DETECTION_KEYS := ["baseline_percentile", "height_enter_m", "height_exit_m", "planar_speed_enter_mps", "planar_speed_exit_mps", "speed_window_s"]
const STRUCTURE_FIT_KEYS := ["minimum_planted_gap_s", "episode_peak_height_weight", "absorb_maximum_travel_m", "absorb_maximum_peak_height_m", "minimum_step_score_m", "minimum_step_to_absorbed_score_ratio"]
const VERIFICATION_KEYS := ["maximum_planted_planar_drift_m", "arrival_window_s", "minimum_arrival_descent_m", "lift_window_s", "minimum_lift_rise_m", "minimum_lift_leave_m"]
const STRUCTURE_KEYS := ["sequences", "locomotion_boundary", "first_swing", "maximum_swing_overlap_s", "planted_phase_offset_range"]
const REPORT_KEYS := ["schema_version", "contract", "character", "package", "variant", "intervals_sha256", "policy", "runtime_library_sha256", "clip_count", "refused", "clips"]


## Loads and validates the role policy. Returns {"policy", "sha256"} or {"error"}.
static func load_policy(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		return {"error": "Planted-interval role policy is missing: %s" % path}
	var value: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not value is Dictionary:
		return {"error": "Planted-interval role policy is not a JSON object: %s" % path}
	var policy := value as Dictionary
	var error := validate_policy(policy)
	if not error.is_empty():
		return {"error": "Planted-interval role policy is invalid: %s" % error}
	return {"policy": policy, "sha256": FileAccess.get_sha256(path)}


## Derives both feet's contiguous planted/swing segments for one clip by fitting the capability's role structure,
## then verifies every planted interval. Returns {"feet", "diagnostics", "refusals"}; a clip with refusals must not
## publish. Diagnostics carry, per foot, the fitted boundaries with their causes and slides, the merged episodes,
## and the verification measurements of every planted interval.
##
## Refusal order: a clip whose loop intent the structure does not accept is refused before fitting. Otherwise each
## foot's fit refusals come first. When the lead foot is fitted and the structure names a locomotion-side boundary,
## a lead foot that is not in swing at that boundary is one role refusal that replaces the per-foot sequence checks
## (the feet are then compared against the wrong roles). Verification refusals follow, and the cross-foot checks run
## only when nothing else refused.
static func derive_clip(animation: Animation, capability: String, looping: bool, model: Dictionary, policy: Dictionary) -> Dictionary:
	var joints := policy["ball_joints"] as Dictionary
	var refusals: Array[String] = []
	var entry: Variant = (policy["capabilities"] as Dictionary).get(capability)
	if not entry is Dictionary:
		refusals.append("capability %s has no role structure" % capability)
		return {"feet": {}, "diagnostics": {}, "refusals": refusals}
	var structure_name := String((entry as Dictionary)["structure"])
	var structure := (policy["structures"] as Dictionary)[structure_name] as Dictionary
	var lead := String((entry as Dictionary).get("lead_foot", "left"))
	var trail := _other_foot(lead)
	var intent := LOOPING if looping else FINITE
	var sequences: Variant = (structure["sequences"] as Dictionary).get(intent)
	if not sequences is Dictionary:
		refusals.append("role structure %s does not accept a %s clip" % [structure_name, intent])
		return {"feet": {}, "diagnostics": {}, "refusals": refusals}
	var length := animation.length
	var count := maxi(1, roundi(length * float(policy["sampling_rate_hz"])))
	var dt := length / float(count)
	var feet := {}
	var diagnostics := {"structure": structure_name, "loop_intent": intent, "lead_foot": lead}
	var runs_by_foot := {}
	var fitted_feet := {}
	var verification_refusals: Array[String] = []
	for foot in FEET:
		var joint := String(joints[foot])
		if not Sampler.has_joint(model, joint):
			refusals.append("skeleton has no %s ball joint %s" % [foot, joint])
			continue
		var expected := (sequences as Dictionary)[LEAD if foot == lead else TRAIL] as Array
		var signal_data := _foot_signal(animation, joint, looping, model, count, dt, policy["detection"])
		var candidate := _classify(signal_data, looping, policy["detection"])
		var fitted := _fit_structure(candidate, signal_data, looping, count, dt, expected, policy["structure_fit"])
		var runs := _runs(fitted["states"] as Array[bool], looping)
		for message in fitted["refusals"]:
			refusals.append("%s foot: %s" % [foot, message])
		if (fitted["refusals"] as Array).is_empty():
			fitted_feet[foot] = true
		var verification := _verify(runs, signal_data, looping, count, dt, policy["verification"])
		for message in verification["refusals"]:
			verification_refusals.append("%s foot: %s" % [foot, message])
		runs_by_foot[foot] = runs
		feet[foot] = {
			"baseline_ball_height_m": _round(float(signal_data["baseline"])),
			"segments": _segments(runs, looping, count, dt, length),
		}
		diagnostics[foot] = {
			"boundaries": _boundaries(runs, signal_data, looping, count, dt, policy["detection"]),
			"merged_planted_gaps": fitted["merged_planted_gaps"],
			"absorbed_episodes": fitted["absorbed_episodes"],
			"steps": fitted["steps"],
			"planted_intervals": verification["intervals"],
		}
	var boundary_error := ""
	if structure.has("locomotion_boundary") and fitted_feet.has(lead):
		boundary_error = _locomotion_boundary_error(structure_name, String(structure["locomotion_boundary"]), lead, runs_by_foot[lead] as Array)
	if not boundary_error.is_empty():
		refusals.append(boundary_error)
	else:
		for foot in FEET:
			if fitted_feet.has(foot):
				var sequence_error := _sequence_error(runs_by_foot[foot] as Array, looping, (sequences as Dictionary)[LEAD if foot == lead else TRAIL] as Array)
				if not sequence_error.is_empty():
					refusals.append("%s foot: %s" % [foot, sequence_error])
	refusals.append_array(verification_refusals)
	if refusals.is_empty():
		for message in _check_feet(structure, lead, trail, runs_by_foot, looping, count, dt):
			refusals.append(message)
	return {"feet": feet, "diagnostics": diagnostics, "refusals": refusals}


## Serialises a planted-interval or report document exactly as it is written to disk.
static func serialise(value: Dictionary) -> String:
	return JSON.stringify(value, "  ") + "\n"


## Builds the durable derivation report retained next to the interval provenance copy. It carries only derivation
## inputs and outputs (no stage paths, timestamps, or comparisons), so re-deriving the same library with the same
## policy and skeleton reproduces it byte for byte. `intervals_sha256` binds it to the serialised interval document
## it explains; the report is not part of any runtime package or descriptor binding.
static func report(identity: Dictionary, intervals_sha256: String, policy_sha256: String, runtime_library_sha256: String, clips: Array) -> Dictionary:
	var sorted_clips := clips.duplicate(true)
	sorted_clips.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return String(a["identity"]) < String(b["identity"]))
	var refused: Array = []
	for clip in sorted_clips:
		if not ((clip as Dictionary)["refusals"] as Array).is_empty():
			refused.append(String((clip as Dictionary)["identity"]))
	return {
		"schema_version": REPORT_SCHEMA_VERSION,
		"contract": REPORT_CONTRACT,
		"character": String(identity["character"]),
		"package": String(identity["package"]),
		"variant": String(identity["variant"]),
		"intervals_sha256": intervals_sha256,
		"policy": {"identity": POLICY_IDENTITY, "sha256": policy_sha256},
		"runtime_library_sha256": runtime_library_sha256,
		"clip_count": sorted_clips.size(),
		"refused": refused,
		"clips": sorted_clips,
	}


## Builds the publishable document from derived clips (each {"identity", "role", "capability", "looping",
## "duration_seconds", "feet"}).
static func document(identity: Dictionary, clips: Array, policy: Dictionary, policy_sha256: String, skeleton: Dictionary, runtime_library_sha256: String) -> Dictionary:
	var sorted_clips := clips.duplicate(true)
	sorted_clips.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return String(a["identity"]) < String(b["identity"]))
	return {
		"schema_version": SCHEMA_VERSION,
		"contract": CONTRACT,
		"character": String(identity["character"]),
		"package": String(identity["package"]),
		"variant": String(identity["variant"]),
		"derivation": {
			"tool": TOOL,
			"tool_version": TOOL_VERSION,
			"policy": {"identity": String(policy["identity"]), "sha256": policy_sha256},
			"ball_joints": (policy["ball_joints"] as Dictionary).duplicate(true),
			"ball_point": "toe_bone_rest_pose_origin_in_skeleton_space",
			"baseline": {"method": "per_clip_per_foot_low_percentile_ball_height", "percentile": float((policy["detection"] as Dictionary)["baseline_percentile"])},
			"sampling_rate_hz": float(policy["sampling_rate_hz"]),
			"detection": (policy["detection"] as Dictionary).duplicate(true),
			"structure_fit": (policy["structure_fit"] as Dictionary).duplicate(true),
			"verification": (policy["verification"] as Dictionary).duplicate(true),
			"skeleton": skeleton.duplicate(true),
			"runtime_library_sha256": runtime_library_sha256,
		},
		"clips": sorted_clips,
	}


## Validates a published document against its package. `expected` carries "character", "package", "variant",
## "clips" (catalogue-shaped entries with identity/role/capability/loop_intent/duration_seconds),
## "runtime_library_sha256", and "policy_sha256". Returns an empty string when valid.
static func validate_document(value: Variant, expected: Dictionary) -> String:
	if not value is Dictionary:
		return "Planted-interval data is not a JSON object"
	var document_value := value as Dictionary
	if int(document_value.get("schema_version", 0)) != SCHEMA_VERSION:
		return "Planted-interval data schema version is unknown or mismatched: %s" % str(document_value.get("schema_version", null))
	if not _has_exact_keys(document_value, DOCUMENT_KEYS) or String(document_value.get("contract", "")) != CONTRACT:
		return "Planted-interval data has an unsupported root schema"
	for field in ["character", "package", "variant"]:
		if String(document_value.get(field, "")) != String(expected.get(field, "")):
			return "Planted-interval data %s identity differs from the package" % field
	var derivation_error := _validate_derivation(document_value.get("derivation"), expected)
	if not derivation_error.is_empty():
		return derivation_error
	var clips: Variant = document_value.get("clips")
	if not clips is Array:
		return "Planted-interval data clips are invalid"
	var expected_clips := {}
	for clip_value in expected.get("clips", []):
		var clip := clip_value as Dictionary
		expected_clips[String(clip.get("identity", ""))] = clip
	var seen := {}
	for clip_value in clips:
		if not clip_value is Dictionary or not _has_exact_keys(clip_value as Dictionary, CLIP_KEYS):
			return "Planted-interval clip entry has an unsupported schema"
		var clip := clip_value as Dictionary
		var identity := String(clip.get("identity", ""))
		if seen.has(identity) or not expected_clips.has(identity):
			return "Planted-interval clip is duplicated or absent from the package: %s" % identity
		seen[identity] = true
		var catalogue_clip := expected_clips[identity] as Dictionary
		if String(clip.get("role", "")) != String(catalogue_clip.get("role", "")) \
				or String(clip.get("capability", "")) != String(catalogue_clip.get("capability", "")) \
				or not clip.get("looping") is bool or bool(clip["looping"]) != bool(catalogue_clip.get("loop_intent", false)):
			return "Planted-interval clip role, capability, or loop intent differs from the package: %s" % identity
		var duration := float(clip.get("duration_seconds", NAN))
		if not _finite(duration) or absf(duration - float(catalogue_clip.get("duration_seconds", NAN))) > DURATION_TOLERANCE:
			return "Planted-interval clip duration differs from the package: %s" % identity
		var feet_value: Variant = clip.get("feet")
		if not feet_value is Dictionary or not _has_exact_keys(feet_value as Dictionary, FEET):
			return "Planted-interval clip must cover exactly both feet: %s" % identity
		for foot in FEET:
			var foot_error := _validate_foot((feet_value as Dictionary)[foot], duration)
			if not foot_error.is_empty():
				return "Planted-interval %s foot of %s is invalid: %s" % [foot, identity, foot_error]
	if seen.size() != expected_clips.size():
		return "Planted-interval data does not cover every package clip"
	return ""


## Expected-clip entries for `validate_document`, taken from a runtime catalogue document.
static func catalogue_expectations(catalogue: Dictionary) -> Array:
	var result: Array = []
	for value in catalogue.get("clips", []):
		if value is Dictionary:
			result.append(value)
	return result


## Validates a role policy document. Returns an empty string when valid.
static func validate_policy(policy: Dictionary) -> String:
	if int(policy.get("schema_version", 0)) != POLICY_SCHEMA_VERSION or String(policy.get("identity", "")) != POLICY_IDENTITY:
		return "unknown schema version or identity"
	if not _has_exact_keys(policy, POLICY_KEYS):
		return "policy must declare exactly: %s" % ", ".join(POLICY_KEYS)
	var joints: Variant = policy.get("ball_joints")
	if not joints is Dictionary or not _has_exact_keys(joints as Dictionary, FEET):
		return "ball_joints must name both feet"
	if float(policy.get("sampling_rate_hz", 0.0)) <= 0.0:
		return "sampling_rate_hz must be positive"
	for section in [["detection", DETECTION_KEYS], ["structure_fit", STRUCTURE_FIT_KEYS], ["verification", VERIFICATION_KEYS]]:
		var values: Variant = policy.get(section[0])
		if not values is Dictionary or not _has_exact_keys(values as Dictionary, section[1]):
			return "%s must declare exactly: %s" % [section[0], ", ".join(section[1])]
		for key in section[1]:
			var number: Variant = (values as Dictionary)[key]
			if not (number is float or number is int) or not _finite(float(number)) or float(number) <= 0.0:
				return "%s.%s must be a positive number" % [section[0], key]
	var detection := policy["detection"] as Dictionary
	if float(detection["height_exit_m"]) < float(detection["height_enter_m"]) or float(detection["planar_speed_exit_mps"]) < float(detection["planar_speed_enter_mps"]) or float(detection["baseline_percentile"]) >= 1.0:
		return "detection hysteresis is inverted"
	var structures: Variant = policy.get("structures")
	if not structures is Dictionary or (structures as Dictionary).is_empty():
		return "structures are missing"
	for name in structures:
		var error := _validate_structure((structures as Dictionary)[name])
		if not error.is_empty():
			return "structure %s: %s" % [name, error]
	var capabilities: Variant = policy.get("capabilities")
	if not capabilities is Dictionary or (capabilities as Dictionary).is_empty():
		return "capabilities are missing"
	for capability in capabilities:
		var entry: Variant = (capabilities as Dictionary)[capability]
		if not entry is Dictionary or not (entry as Dictionary).has("structure") or not (structures as Dictionary).has(String((entry as Dictionary)["structure"])):
			return "capability %s names an unknown structure" % capability
		for key in entry:
			if key not in ["structure", "lead_foot"]:
				return "capability %s declares unsupported field %s" % [capability, key]
		var structure := (structures as Dictionary)[String((entry as Dictionary)["structure"])] as Dictionary
		if (entry as Dictionary).has("lead_foot") and String((entry as Dictionary)["lead_foot"]) not in FEET:
			return "capability %s names an unknown lead foot" % capability
		if (structure.has("locomotion_boundary") or structure.has("first_swing")) and not (entry as Dictionary).has("lead_foot"):
			return "capability %s must name the lead foot its structure orders" % capability
	return ""


static func _validate_structure(value: Variant) -> String:
	if not value is Dictionary:
		return "must be an object"
	var structure := value as Dictionary
	for key in structure:
		if key not in STRUCTURE_KEYS:
			return "unsupported field %s" % key
	var sequences: Variant = structure.get("sequences")
	if not sequences is Dictionary or (sequences as Dictionary).is_empty():
		return "sequences must map at least one loop intent (looping, finite) to lead and trail sequences"
	for intent in sequences:
		if String(intent) not in [LOOPING, FINITE]:
			return "sequences declare unknown loop intent %s" % intent
		var pair: Variant = (sequences as Dictionary)[intent]
		if not pair is Dictionary or not _has_exact_keys(pair as Dictionary, [LEAD, TRAIL]):
			return "sequences.%s must declare exactly lead and trail" % intent
		for role in [LEAD, TRAIL]:
			var sequence: Variant = (pair as Dictionary)[role]
			if not sequence is Array or (sequence as Array).is_empty():
				return "sequences.%s.%s must be a non-empty array" % [intent, role]
			var previous := ""
			for state in sequence:
				if String(state) not in [PLANTED, SWING] or String(state) == previous:
					return "sequences.%s.%s must alternate planted and swing" % [intent, role]
				previous = String(state)
			if String(intent) == LOOPING and (sequence as Array).size() > 1 and String((sequence as Array)[0]) == String((sequence as Array)[-1]):
				return "sequences.looping.%s must be circular: its first and last states must differ" % role
	if structure.has("locomotion_boundary"):
		var boundary := String(structure["locomotion_boundary"])
		if boundary not in [BOUNDARY_START, BOUNDARY_END]:
			return "locomotion_boundary must be start or end"
		if (sequences as Dictionary).has(LOOPING):
			return "locomotion_boundary applies only to finite transitions"
		var lead_sequence := ((sequences as Dictionary)[FINITE] as Dictionary)[LEAD] as Array
		if String(lead_sequence[0 if boundary == BOUNDARY_START else -1]) != SWING:
			return "the lead sequence must be in swing at the %s locomotion boundary" % boundary
	if structure.has("first_swing") and String(structure["first_swing"]) not in [LEAD, TRAIL]:
		return "first_swing must be lead or trail"
	if structure.has("maximum_swing_overlap_s") and (not _finite(float(structure["maximum_swing_overlap_s"])) or float(structure["maximum_swing_overlap_s"]) < 0.0):
		return "maximum_swing_overlap_s must be non-negative"
	if structure.has("planted_phase_offset_range"):
		var bounds: Variant = structure["planted_phase_offset_range"]
		if not bounds is Array or (bounds as Array).size() != 2 or float((bounds as Array)[0]) < 0.0 or float((bounds as Array)[0]) > float((bounds as Array)[1]) or float((bounds as Array)[1]) > 0.5:
			return "planted_phase_offset_range must be [low, high] within [0, 0.5] (a circular cycle fraction)"
	return ""


static func _validate_derivation(value: Variant, expected: Dictionary) -> String:
	if not value is Dictionary or not _has_exact_keys(value as Dictionary, DERIVATION_KEYS):
		return "Planted-interval derivation provenance has an unsupported schema"
	var derivation := value as Dictionary
	if String(derivation.get("tool", "")) != TOOL or int(derivation.get("tool_version", 0)) != TOOL_VERSION:
		return "Planted-interval derivation tool or version is unknown or mismatched"
	var policy: Variant = derivation.get("policy")
	if not policy is Dictionary or String((policy as Dictionary).get("identity", "")) != POLICY_IDENTITY \
			or String((policy as Dictionary).get("sha256", "")) != String(expected.get("policy_sha256", "")):
		return "Planted-interval derivation policy is stale or mismatched"
	if String(derivation.get("runtime_library_sha256", "")) != String(expected.get("runtime_library_sha256", "")):
		return "Planted-interval data was derived from a different runtime library"
	var joints: Variant = derivation.get("ball_joints")
	if not joints is Dictionary or not _has_exact_keys(joints as Dictionary, FEET):
		return "Planted-interval derivation ball joints are invalid"
	for section in [["detection", DETECTION_KEYS], ["structure_fit", STRUCTURE_FIT_KEYS], ["verification", VERIFICATION_KEYS]]:
		var values: Variant = derivation.get(section[0])
		if not values is Dictionary or not _has_exact_keys(values as Dictionary, section[1]):
			return "Planted-interval derivation tolerances are invalid"
	return ""


static func _validate_foot(value: Variant, duration: float) -> String:
	if not value is Dictionary or not _has_exact_keys(value as Dictionary, FOOT_KEYS):
		return "unsupported schema"
	var foot := value as Dictionary
	if not _finite(float(foot.get("baseline_ball_height_m", NAN))):
		return "baseline is not finite"
	var segments: Variant = foot.get("segments")
	if not segments is Array or (segments as Array).is_empty():
		return "segments are missing"
	var cursor := 0.0
	var previous_state := ""
	for segment_value in segments:
		if not segment_value is Dictionary or not _has_exact_keys(segment_value as Dictionary, SEGMENT_KEYS):
			return "segment has an unsupported schema"
		var segment := segment_value as Dictionary
		var state := String(segment.get("state", ""))
		var start := float(segment.get("start_seconds", NAN))
		var end := float(segment.get("end_seconds", NAN))
		if state not in [PLANTED, SWING] or state == previous_state:
			return "segment states must alternate between planted and swing"
		if not _finite(start) or not _finite(end) or absf(start - cursor) > DURATION_TOLERANCE or end <= start:
			return "segments must be contiguous and non-empty"
		cursor = end
		previous_state = state
	if absf(cursor - duration) > DURATION_TOLERANCE:
		return "segments must cover the whole clip"
	return ""


# Samples the ball's skeleton-space position, height above the foot's clip baseline, and windowed planar speed.
# Looping clips extend positions past the seam through the Root cycle transform, so speeds and displacements are
# continuous across the loop boundary.
static func _foot_signal(animation: Animation, joint: String, looping: bool, model: Dictionary, count: int, dt: float, detection: Dictionary) -> Dictionary:
	var sample_count := count if looping else count + 1
	var positions: Array[Vector3] = []
	for index in count + 1:
		var time := minf(animation.length, float(index) * dt)
		positions.append((Sampler.joint_global(animation, joint, time, model, {}) as Transform3D).origin)
	var cycle := Transform3D.IDENTITY
	if looping:
		var root_joint := _root_joint(model, joint)
		cycle = (Sampler.joint_global(animation, root_joint, animation.length, model, {}) as Transform3D) \
			* (Sampler.joint_global(animation, root_joint, 0.0, model, {}) as Transform3D).affine_inverse()
	var heights: Array[float] = []
	for index in sample_count:
		heights.append(positions[index].y)
	var sorted_heights := heights.duplicate()
	sorted_heights.sort()
	var baseline := float(sorted_heights[floori(float(detection["baseline_percentile"]) * float(sample_count - 1))])
	var window := maxi(1, roundi(float(detection["speed_window_s"]) / dt))
	var speeds: Array[float] = []
	for index in sample_count:
		var low := index - window
		var high := index + window
		if not looping:
			low = maxi(0, low)
			high = mini(count, high)
		var a := _position_at(positions, low, count, looping, cycle)
		var b := _position_at(positions, high, count, looping, cycle)
		speeds.append(Vector2(b.x - a.x, b.z - a.z).length() / (float(high - low) * dt))
	var relative: Array[float] = []
	for index in sample_count:
		relative.append(heights[index] - baseline)
	return {"positions": positions, "cycle": cycle, "heights": relative, "speeds": speeds, "baseline": baseline, "count": sample_count}


static func _position_at(positions: Array[Vector3], index: int, count: int, looping: bool, cycle: Transform3D) -> Vector3:
	if not looping:
		return positions[clampi(index, 0, count)]
	var wrapped := posmod(index, count)
	var laps := (index - wrapped) / count
	var result := positions[wrapped]
	var step := cycle if laps > 0 else cycle.affine_inverse()
	for _lap in absi(laps):
		result = step * result
	return result


static func _root_joint(model: Dictionary, joint: String) -> String:
	var bones := model.get("bones", {}) as Dictionary
	var current := joint
	while bones.has(current) and not String((bones[current] as Dictionary).get("parent", "")).is_empty():
		current = String((bones[current] as Dictionary)["parent"])
	return current


# Candidate hysteresis classification: enter planted inside the enter tolerances, stay planted inside the exit
# tolerances. A finite clip's first sample has no history and is judged against the exit tolerances; a looping clip is
# classified twice so its first sample inherits the state at the seam.
static func _classify(signal_data: Dictionary, looping: bool, detection: Dictionary) -> Array[bool]:
	var heights := signal_data["heights"] as Array[float]
	var speeds := signal_data["speeds"] as Array[float]
	var initial := heights[0] <= float(detection["height_exit_m"]) and speeds[0] <= float(detection["planar_speed_exit_mps"])
	var states := _hysteresis(heights, speeds, initial, detection)
	if looping:
		states = _hysteresis(heights, speeds, states[states.size() - 1], detection)
	return states


static func _hysteresis(heights: Array[float], speeds: Array[float], initial: bool, detection: Dictionary) -> Array[bool]:
	var states: Array[bool] = []
	var planted := initial
	for index in heights.size():
		if planted:
			planted = heights[index] <= float(detection["height_exit_m"]) and speeds[index] <= float(detection["planar_speed_exit_mps"])
		else:
			planted = heights[index] <= float(detection["height_enter_m"]) and speeds[index] <= float(detection["planar_speed_enter_mps"])
		states.append(planted)
	return states


# Fits the role's expected swing count to the candidate states. Returns {"states", "refusals", "steps",
# "merged_planted_gaps", "absorbed_episodes"}.
static func _fit_structure(candidate: Array[bool], signal_data: Dictionary, looping: bool, count: int, dt: float, expected: Array, fit: Dictionary) -> Dictionary:
	var states := candidate.duplicate()
	var samples := states.size()
	var refusals: Array[String] = []
	var merged_gaps: Array = []
	var minimum_gap := maxi(1, roundi(float(fit["minimum_planted_gap_s"]) / dt))
	for run_value in _runs(states, looping):
		var run := run_value as Dictionary
		var start := int(run["start"])
		var length := int(run["length"])
		var interior := looping or (start > 0 and start + length < samples)
		if bool(run["planted"]) and length < minimum_gap and length < samples and interior:
			for step in length:
				states[(start + step) % samples] = false
			merged_gaps.append({"start_seconds": _round(float(start) * dt), "duration_seconds": _round(float(length) * dt)})
	var episodes: Array = []
	for run_value in _runs(states, looping):
		var run := run_value as Dictionary
		if not bool(run["planted"]):
			episodes.append(_episode(run, signal_data, looping, count, dt, fit))
	episodes.sort_custom(func(a: Dictionary, b: Dictionary) -> bool: return float(a["score_m"]) > float(b["score_m"]))
	var wanted := expected.count(SWING)
	var steps := episodes.slice(0, wanted)
	var extra := episodes.slice(wanted)
	var absorbed: Array = []
	if episodes.size() < wanted:
		refusals.append("role structure expects %d swing episode(s); the signals show %d" % [wanted, episodes.size()])
	for episode_value in extra:
		var episode := episode_value as Dictionary
		absorbed.append(_public_episode(episode))
		if float(episode["travel_m"]) > float(fit["absorb_maximum_travel_m"]) or float(episode["peak_height_m"]) > float(fit["absorb_maximum_peak_height_m"]):
			refusals.append("merged run at %.3f s travels %.3f m and rises %.3f m, beyond the absorption limits" % [float(episode["start"]) * dt, float(episode["travel_m"]), float(episode["peak_height_m"])])
		for step in int(episode["length"]):
			states[(int(episode["start"]) + step) % samples] = true
	if not steps.is_empty():
		var weakest := steps[-1] as Dictionary
		if float(weakest["score_m"]) < float(fit["minimum_step_score_m"]):
			refusals.append("role structure step at %.3f s scores %.3f m, below the minimum step score" % [float(weakest["start"]) * dt, float(weakest["score_m"])])
		if not extra.is_empty():
			var strongest := extra[0] as Dictionary
			var ratio := float(weakest["score_m"]) / maxf(float(strongest["score_m"]), 0.000001)
			if ratio < float(fit["minimum_step_to_absorbed_score_ratio"]):
				refusals.append("role structure cannot separate its steps from extra motion: weakest step %.3f m versus extra episode %.3f m at %.3f s" % [float(weakest["score_m"]), float(strongest["score_m"]), float(strongest["start"]) * dt])
	var public_steps: Array = []
	for step_value in steps:
		public_steps.append(_public_episode(step_value))
	return {"states": states, "refusals": refusals, "steps": public_steps, "merged_planted_gaps": merged_gaps, "absorbed_episodes": absorbed}


static func _episode(run: Dictionary, signal_data: Dictionary, looping: bool, count: int, dt: float, fit: Dictionary) -> Dictionary:
	var positions := signal_data["positions"] as Array[Vector3]
	var heights := signal_data["heights"] as Array[float]
	var cycle := signal_data["cycle"] as Transform3D
	var samples := int(signal_data["count"])
	var start := int(run["start"])
	var length := int(run["length"])
	var travel := 0.0
	var peak := 0.0
	var previous := _position_at(positions, start - 1, count, looping, cycle)
	for step in length:
		var point := _position_at(positions, start + step, count, looping, cycle)
		travel += Vector2(point.x - previous.x, point.z - previous.z).length()
		previous = point
		peak = maxf(peak, heights[(start + step) % samples])
	if not looping and start + length < samples:
		var landing := _position_at(positions, start + length, count, looping, cycle)
		travel += Vector2(landing.x - previous.x, landing.z - previous.z).length()
	return {
		"start": start, "length": length, "travel_m": travel, "peak_height_m": peak,
		"score_m": travel + float(fit["episode_peak_height_weight"]) * peak,
		"start_seconds": _round(float(start) * dt), "duration_seconds": _round(float(length) * dt),
	}


static func _public_episode(episode: Dictionary) -> Dictionary:
	return {
		"start_seconds": episode["start_seconds"], "duration_seconds": episode["duration_seconds"],
		"travel_m": _round(float(episode["travel_m"])), "peak_height_m": _round(float(episode["peak_height_m"])),
		"score_m": _round(float(episode["score_m"])),
	}


static func _sequence_error(runs: Array, looping: bool, expected: Array) -> String:
	var actual: Array = []
	for run in runs:
		actual.append(PLANTED if bool((run as Dictionary)["planted"]) else SWING)
	if looping:
		if actual.count(SWING) != expected.count(SWING) or actual.count(PLANTED) != expected.count(PLANTED):
			return "role structure expects the circular sequence %s; the fit gives %s" % [str(expected), str(actual)]
		return ""
	if actual != expected:
		return "role structure expects the sequence %s; the fit gives %s" % [str(expected), str(actual)]
	return ""


# Runs are {"planted", "start", "length"} in sample indices. Looping runs are circular: the first run starts at a
# state change, so a run may wrap past the last sample.
static func _runs(states: Array[bool], looping: bool) -> Array:
	var count := states.size()
	var offset := 0
	if looping:
		offset = -1
		for index in count:
			if states[index] != states[posmod(index - 1, count)]:
				offset = index
				break
		if offset < 0:
			return [{"planted": states[0], "start": 0, "length": count}]
	var runs: Array = []
	for step in count:
		var index := (offset + step) % count if looping else step
		if not runs.is_empty() and bool((runs[-1] as Dictionary)["planted"]) == states[index]:
			(runs[-1] as Dictionary)["length"] = int((runs[-1] as Dictionary)["length"]) + 1
		else:
			runs.append({"planted": states[index], "start": index, "length": 1})
	return runs


# Converts runs into contiguous, whole-clip segments in source seconds. A state change between samples i-1 and i is
# placed half a sample before sample i. A finite clip's first run starts at 0 and its last run ends at the clip
# length. A looping clip's boundary indices wrap into [1, count], so a change at sample 0 lies at L - dt/2, and a run
# whose wrapped start is not before its wrapped end is split at the clip boundary.
static func _segments(runs: Array, looping: bool, count: int, dt: float, length: float) -> Array:
	if runs.size() == 1:
		return [{"state": PLANTED if bool((runs[0] as Dictionary)["planted"]) else SWING, "start_seconds": 0.0, "end_seconds": length}]
	var pieces: Array = []
	for run_value in runs:
		var run := run_value as Dictionary
		var planted := bool(run["planted"])
		var start := int(run["start"])
		var end := start + int(run["length"])
		if looping:
			var first := posmod(start - 1, count) + 1
			var last := posmod(end - 1, count) + 1
			if first < last:
				pieces.append([planted, _boundary_time(first, dt), _boundary_time(last, dt)])
			else:
				pieces.append([planted, _boundary_time(first, dt), length])
				pieces.append([planted, 0.0, _boundary_time(last, dt)])
		else:
			pieces.append([planted, 0.0 if start == 0 else _boundary_time(start, dt), length if end >= count + 1 else _boundary_time(end, dt)])
	pieces.sort_custom(func(a: Array, b: Array) -> bool: return float(a[1]) < float(b[1]))
	var segments: Array = []
	for item in pieces:
		var state := PLANTED if bool(item[0]) else SWING
		if not segments.is_empty() and String((segments[-1] as Dictionary)["state"]) == state:
			(segments[-1] as Dictionary)["end_seconds"] = item[2]
		else:
			segments.append({"state": state, "start_seconds": item[1], "end_seconds": item[2]})
	return segments


static func _boundary_time(index: int, dt: float) -> float:
	return _round((float(index) - 0.5) * dt)


# Source time of the state change entering sample `index`: half a sample before it, wrapped into the clip for looping
# clips (so a change at sample 0 lies at L - dt/2) and clamped to 0 at a finite clip's start.
static func _change_time(index: int, looping: bool, count: int, dt: float) -> float:
	if looping:
		return _boundary_time(posmod(index - 1, count) + 1, dt)
	return 0.0 if index == 0 else _boundary_time(index, dt)


# Records each fitted boundary (a run start that is a real state change, so not a finite clip's start) with the
# signal that placed it. A lift is caused by height when the ball is above the exit height on its first swing sample,
# otherwise by speed; a contact is caused by height when the ball was above the enter height on its last swing sample,
# otherwise by speed. Speed-caused boundaries carry the ground-level slide: the planar path the ball travels at floor
# height between the boundary and its rising above (lift) or since its coming down to (contact) the height tolerance.
static func _boundaries(runs: Array, signal_data: Dictionary, looping: bool, count: int, dt: float, detection: Dictionary) -> Array:
	var heights := signal_data["heights"] as Array[float]
	var positions := signal_data["positions"] as Array[Vector3]
	var cycle := signal_data["cycle"] as Transform3D
	var samples := int(signal_data["count"])
	var result: Array = []
	if runs.size() < 2:
		return result
	for position in runs.size():
		var run := runs[position] as Dictionary
		var start := int(run["start"])
		if not looping and start == 0:
			continue
		var planted := bool(run["planted"])
		var swing := run if not planted else runs[posmod(position - 1, runs.size())] as Dictionary
		var swing_start := int(swing["start"])
		var swing_end := swing_start + int(swing["length"])
		var cause := CAUSE_SPEED
		var slide := 0.0
		var slide_samples := 0
		if planted:
			if heights[posmod(start - 1, samples)] > float(detection["height_enter_m"]):
				cause = CAUSE_HEIGHT
			else:
				var index := start - 1
				if looping and index < swing_start:
					index += samples
				while index >= swing_start and heights[posmod(index, samples)] <= float(detection["height_enter_m"]):
					var a := _position_at(positions, index, count, looping, cycle)
					var b := _position_at(positions, index + 1, count, looping, cycle)
					slide += Vector2(b.x - a.x, b.z - a.z).length()
					slide_samples += 1
					index -= 1
		else:
			if heights[start % samples] > float(detection["height_exit_m"]):
				cause = CAUSE_HEIGHT
			else:
				var index := start
				while index < swing_end and heights[index % samples] <= float(detection["height_exit_m"]):
					var a := _position_at(positions, index - 1, count, looping, cycle)
					var b := _position_at(positions, index, count, looping, cycle)
					slide += Vector2(b.x - a.x, b.z - a.z).length()
					slide_samples += 1
					index += 1
		var entry := {"time_seconds": _change_time(start, looping, count, dt), "kind": "contact" if planted else "lift", "cause": cause}
		if cause == CAUSE_SPEED:
			entry["ground_slide_m"] = _round(slide)
			entry["ground_slide_seconds"] = _round(float(slide_samples) * dt)
		result.append(entry)
	return result


# Verifies each planted interval with displacement criteria that the classifier does not use. Arrival: within the
# arrival window before the start, the ball was at least the minimum descent above its height at the start. Lift:
# within the lift window after the end, the ball rises by the minimum rise or moves by the minimum leave distance
# from where it was released. Drift: the ball stays within the cap of its position at the start. Finite clip edges
# have no arrival or lift to verify; a foot planted throughout has neither.
static func _verify(runs: Array, signal_data: Dictionary, looping: bool, count: int, dt: float, verification: Dictionary) -> Dictionary:
	var positions := signal_data["positions"] as Array[Vector3]
	var cycle := signal_data["cycle"] as Transform3D
	var samples := int(signal_data["count"])
	var arrival_window := maxi(1, roundi(float(verification["arrival_window_s"]) / dt))
	var lift_window := maxi(1, roundi(float(verification["lift_window_s"]) / dt))
	var refusals: Array[String] = []
	var intervals: Array = []
	for run_value in runs:
		var run := run_value as Dictionary
		if not bool(run["planted"]):
			continue
		var start := int(run["start"])
		var length := int(run["length"])
		var last := start + length - 1
		var anchor := _position_at(positions, start, count, looping, cycle)
		var drift := 0.0
		for step in length:
			var point := _position_at(positions, start + step, count, looping, cycle)
			drift = maxf(drift, Vector2(point.x - anchor.x, point.z - anchor.z).length())
		var throughout := length == samples
		var check_arrival := not throughout and (looping or start > 0)
		var check_lift := not throughout and (looping or start + length < samples)
		var descent := 0.0
		if check_arrival:
			for step in range(1, arrival_window + 1):
				if not looping and start - step < 0:
					break
				descent = maxf(descent, _position_at(positions, start - step, count, looping, cycle).y - anchor.y)
		var release := _position_at(positions, last, count, looping, cycle)
		var rise := 0.0
		var leave := 0.0
		if check_lift:
			for step in range(1, lift_window + 1):
				if not looping and last + step > count:
					break
				var point := _position_at(positions, last + step, count, looping, cycle)
				rise = maxf(rise, point.y - release.y)
				leave = maxf(leave, Vector2(point.x - release.x, point.z - release.z).length())
		var start_time := 0.0 if throughout else _change_time(start, looping, count, dt)
		if drift > float(verification["maximum_planted_planar_drift_m"]):
			refusals.append("planted interval at %.3f s drifts %.3f m over the drift cap" % [start_time, drift])
		if check_arrival and descent < float(verification["minimum_arrival_descent_m"]):
			refusals.append("planted interval at %.3f s has no descent into contact (%.3f m)" % [start_time, descent])
		if check_lift and rise < float(verification["minimum_lift_rise_m"]) and leave < float(verification["minimum_lift_leave_m"]):
			refusals.append("planted interval at %.3f s has no lift after its end (rise %.3f m, leave %.3f m)" % [start_time, rise, leave])
		var interval := {"start_seconds": _round(start_time), "duration_seconds": _round(float(length) * dt), "maximum_planar_drift_m": _round(drift)}
		if check_arrival:
			interval["arrival_descent_m"] = _round(descent)
		if check_lift:
			interval["lift_rise_m"] = _round(rise)
			interval["lift_leave_m"] = _round(leave)
		intervals.append(interval)
	return {"refusals": refusals, "intervals": intervals}


# Cross-foot structure: the foot the structure names (`first_swing`: the lead or the trail foot) starts its first
# swing first; confined manoeuvres keep their swings apart within the overlap allowance; cyclic gaits keep their
# planted intervals within the phase-offset range, measured as a circular cycle fraction in [0, 0.5].
static func _check_feet(structure: Dictionary, lead: String, trail: String, runs_by_foot: Dictionary, looping: bool, count: int, dt: float) -> Array[String]:
	var refusals: Array[String] = []
	var lead_swings := (runs_by_foot[lead] as Array).filter(func(run: Dictionary) -> bool: return not bool(run["planted"]))
	var trail_swings := (runs_by_foot[trail] as Array).filter(func(run: Dictionary) -> bool: return not bool(run["planted"]))
	if structure.has("first_swing") and not lead_swings.is_empty() and not trail_swings.is_empty():
		var lead_first := String(structure["first_swing"]) == LEAD
		var first_key := _first_swing_key(lead_swings if lead_first else trail_swings, looping, count)
		var second_key := _first_swing_key(trail_swings if lead_first else lead_swings, looping, count)
		if first_key >= second_key:
			refusals.append("role structure expects the %s (%s) foot to swing first" % [lead if lead_first else trail, String(structure["first_swing"])])
	if structure.has("maximum_swing_overlap_s") and not lead_swings.is_empty() and not trail_swings.is_empty():
		var overlap := 0
		for a in lead_swings:
			for b in trail_swings:
				overlap += _overlap(a, b, count, looping)
		if float(overlap) * dt > float(structure["maximum_swing_overlap_s"]) + DURATION_TOLERANCE:
			refusals.append("role structure expects the feet to swing one at a time; the swings overlap %.3f s" % (float(overlap) * dt))
	if structure.has("planted_phase_offset_range"):
		var lead_planted := (runs_by_foot[lead] as Array).filter(func(run: Dictionary) -> bool: return bool(run["planted"]))
		var trail_planted := (runs_by_foot[trail] as Array).filter(func(run: Dictionary) -> bool: return bool(run["planted"]))
		if lead_planted.size() == 1 and trail_planted.size() == 1:
			var difference := absf(_circular_centre(lead_planted[0], count) - _circular_centre(trail_planted[0], count)) / float(count)
			var offset := minf(difference, 1.0 - difference)
			var bounds := structure["planted_phase_offset_range"] as Array
			if offset < float(bounds[0]) or offset > float(bounds[1]):
				refusals.append("role structure expects alternating feet; the planted phase offset is %.3f of the cycle" % offset)
	return refusals


# A transition's lead foot is in swing at its locomotion-side boundary: the last run of an entry, the first of an exit.
static func _locomotion_boundary_error(structure_name: String, boundary: String, lead: String, runs: Array) -> String:
	if runs.is_empty():
		return ""
	var run := (runs[0] if boundary == BOUNDARY_START else runs[-1]) as Dictionary
	if not bool(run["planted"]):
		return ""
	return "role structure %s expects the lead %s foot in swing at the locomotion-side %s boundary; it is planted" % [structure_name, lead, boundary]


static func _other_foot(foot: String) -> String:
	return "right" if foot == "left" else "left"


# Finite clips order swings by start sample. Looping clips order them by circular centre within the clip, so a swing
# that is already in progress at the seam counts from where most of it lies.
static func _first_swing_key(swings: Array, looping: bool, count: int) -> float:
	var best := INF
	for run in swings:
		best = minf(best, _circular_centre(run, count) if looping else float((run as Dictionary)["start"]))
	return best


static func _circular_centre(run: Dictionary, count: int) -> float:
	return fposmod(float(run["start"]) + float(run["length"]) * 0.5, float(count))


static func _overlap(a: Dictionary, b: Dictionary, count: int, looping: bool) -> int:
	var modulus := count if looping else 1 << 30
	var total := 0
	for step in int(a["length"]):
		var index := (int(a["start"]) + step) % modulus
		if posmod(index - int(b["start"]), modulus) < int(b["length"]):
			total += 1
	return total


static func _round(value: float) -> float:
	return snappedf(value, TIME_DECIMALS)


static func _has_exact_keys(value: Dictionary, expected: Array) -> bool:
	if value.size() != expected.size():
		return false
	for key in expected:
		if not value.has(key):
			return false
	return true


static func _finite(value: float) -> bool:
	return not is_nan(value) and not is_inf(value)
