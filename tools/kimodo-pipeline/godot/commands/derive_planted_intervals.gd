extends SceneTree

# Derives ANIM-002 per-foot planted intervals for every clip of one package's runtime AnimationLibrary, sampled on an
# imported GeneralSkeleton, and checks each clip against the planted-interval role policy. Writes the interval
# document only when no clip is refused. The report (`--report=`, retained in processed provenance as
# `per_foot_planted_interval_derivation_report_v1.json`) always lists every clip's segments, boundary causes, ground
# slides, fit and verification diagnostics, and refusals; it is deterministic (no paths, comparison, or timestamps)
# and binds the written interval document by `intervals_sha256` (empty when nothing was written).
#
# Usage (user arguments after `--`):
#   --package=<locomotion|idle> --variant=<variant> [--character=<character>]
#   [--index=<processed-index.json>] [--library=<runtime library .tres>] [--skeleton=<scene with one Skeleton3D>]
#   [--policy=<policy.json>] [--output=<intervals.json>] [--report=<report.json>]
#   [--compare=<reference intervals.json> [--compare-tolerance-s=<seconds, default 1e-6>]]

const DEFAULT_CHARACTER := "female"
const DEFAULT_VARIANT := "sad"
const DEFAULT_COMPARE_TOLERANCE_S := "0.000001"

var intervals: Script
var descriptor_helper: Script
var sampler: Script
var errors: Array[String] = []


func _init() -> void:
	intervals = _load_helper("planted_intervals.gd")
	descriptor_helper = _load_helper("runtime_package_descriptor.gd")
	sampler = _load_helper("animation_pose_sampler.gd")
	_run()
	quit(1 if not errors.is_empty() else 0)


func _run() -> void:
	var package := _argument("--package=", "locomotion")
	var variant := _argument("--variant=", DEFAULT_VARIANT)
	var character := _argument("--character=", DEFAULT_CHARACTER)
	var processed_set := "res://assets/characters/reference/%s/animations/processed/%s" % [character, package]
	var runtime_root := "res://assets/characters/reference/%s/animations/runtime/%s/%s" % [character, package, variant]
	var index_path := _argument("--index=", processed_set.path_join(variant).path_join("processed-index.json"))
	var library_path := _argument("--library=", runtime_root.path_join("%s_%s.tres" % [package, variant]))
	var skeleton_path := _argument("--skeleton=", processed_set.path_join("%s_%s.blend" % [package, variant]))
	var policy_path := _argument("--policy=", _tool_root().path_join("config").path_join(intervals.POLICY_FILENAME))
	var loaded: Dictionary = intervals.load_policy(policy_path)
	if loaded.has("error"):
		_fail(String(loaded["error"]))
		return
	var policy := loaded["policy"] as Dictionary
	var index := _json(index_path)
	var library := ResourceLoader.load(library_path, "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary
	if index.is_empty() or library == null:
		_fail("Processed index or runtime library is unavailable: %s, %s" % [index_path, library_path])
		return
	if String(index.get("package", "")) != package:
		_fail("Processed index package differs from the requested package: %s" % index_path)
		return
	var skeleton := _skeleton(skeleton_path)
	if skeleton.is_empty():
		return
	var clips: Array = []
	var report_clips: Array = []
	var refused: Array = []
	var identities := {}
	for value in index.get("actions", []):
		if not value is Dictionary:
			_fail("Processed index contains an invalid action entry")
			return
		var entry := value as Dictionary
		var identity := String(entry.get("external_identity", ""))
		var role := String(entry.get("runtime_role", entry.get("role", "")))
		var capability := String(entry.get("runtime_capability", ""))
		var looping := bool(entry.get("is_loop", false))
		if identity.is_empty() or role.is_empty() or capability.is_empty() or identities.has(identity) or not library.has_animation(identity):
			_fail("Processed action is missing, duplicated, or absent from the runtime library: %s" % identity)
			return
		identities[identity] = true
		var animation := library.get_animation(identity)
		var derived: Dictionary = intervals.derive_clip(animation, capability, looping, skeleton["model"], policy)
		var clip := {
			"identity": identity, "role": role, "capability": capability, "looping": looping,
			"duration_seconds": animation.length, "feet": derived["feet"],
		}
		clips.append(clip)
		var refusals := derived["refusals"] as Array
		report_clips.append({
			"identity": identity, "role": role, "capability": capability, "looping": looping,
			"duration_seconds": animation.length, "feet": derived["feet"], "diagnostics": derived["diagnostics"],
			"refusals": refusals,
		})
		if not refusals.is_empty():
			refused.append({"identity": identity, "role": role, "refusals": refusals})
	if identities.size() != library.get_animation_list().size():
		_fail("Runtime library carries animations the processed index does not declare: %s" % library_path)
		return
	var document: Dictionary = intervals.document(
		{"character": character, "package": package, "variant": variant}, clips, policy, String(loaded["sha256"]),
		{"scene": skeleton_path, "skeleton": skeleton["name"], "motion_scale": skeleton["motion_scale"]},
		descriptor_helper.runtime_library_sha256(library_path))
	var comparison := _compare(document)
	for item in report_clips:
		print("ANIM002_PLANTED_CLIP=" + JSON.stringify({
			"identity": item["identity"], "role": item["role"], "refused": not (item["refusals"] as Array).is_empty(),
			"left": _summary(item, "left"), "right": _summary(item, "right"),
			"left_boundaries": _boundary_summary(item["diagnostics"], "left"), "right_boundaries": _boundary_summary(item["diagnostics"], "right"),
		}))
	for item in refused:
		_fail("Refused clip %s (%s): %s" % [item["identity"], item["role"], "; ".join(item["refusals"])])
	if not comparison.is_empty():
		print("ANIM002_PLANTED_COMPARISON=" + JSON.stringify(comparison))
		if not bool(comparison["within_tolerance"]):
			_fail("Derived intervals differ from the reference beyond %s s" % str(comparison["tolerance_seconds"]))
	var serialised: String = intervals.serialise(document)
	var report_path := _argument("--report=", "")
	if not report_path.is_empty():
		var intervals_sha256 := serialised.sha256_text() if errors.is_empty() else ""
		_write_text(report_path, intervals.serialise(intervals.report(
			{"character": character, "package": package, "variant": variant}, intervals_sha256, String(loaded["sha256"]),
			String((document["derivation"] as Dictionary)["runtime_library_sha256"]), report_clips)))
	if not errors.is_empty():
		print("ANIM002_PLANTED_INTERVALS=refused clips=%d refused=%d" % [clips.size(), refused.size()])
		return
	var output := _argument("--output=", "")
	if not output.is_empty():
		_write_text(output, serialised)
	print("ANIM002_PLANTED_INTERVALS=derived clips=%d refused=0" % clips.size())


func _compare(document: Dictionary) -> Dictionary:
	var reference_path := _argument("--compare=", "")
	if reference_path.is_empty():
		return {}
	var tolerance := float(_argument("--compare-tolerance-s=", DEFAULT_COMPARE_TOLERANCE_S))
	var reference := _json(reference_path)
	var by_identity := {}
	for value in reference.get("clips", []):
		by_identity[String((value as Dictionary).get("identity", ""))] = value
	var topology_mismatches: Array = []
	var maximum := 0.0
	var worst := {}
	for clip_value in document["clips"]:
		var clip := clip_value as Dictionary
		var other: Variant = by_identity.get(String(clip["identity"]))
		var looping := bool(clip["looping"])
		var length := float(clip["duration_seconds"])
		for foot in intervals.FEET:
			var own := _transitions(((clip["feet"] as Dictionary)[foot] as Dictionary)["segments"], looping)
			var theirs: Array = [] if not other is Dictionary else _transitions((((other as Dictionary)["feet"] as Dictionary)[foot] as Dictionary)["segments"], looping)
			if own.size() != theirs.size():
				topology_mismatches.append({"identity": clip["identity"], "foot": foot})
				continue
			# Looping transitions are compared circularly from the best-aligned rotation, so a boundary that moves
			# across the loop seam is a small shift rather than a topology change.
			var best := INF
			var best_rotation := -1
			for rotation in (maxi(1, own.size()) if looping else 1):
				var difference := 0.0
				var aligned := true
				for position in own.size():
					var a := own[position] as Array
					var b := theirs[(position + rotation) % theirs.size()] as Array
					if String(a[0]) != String(b[0]):
						aligned = false
						break
					var delta := absf(float(a[1]) - float(b[1]))
					difference = maxf(difference, minf(delta, length - delta) if looping else delta)
				if aligned and difference < best:
					best = difference
					best_rotation = rotation
			if best_rotation < 0:
				topology_mismatches.append({"identity": clip["identity"], "foot": foot})
				continue
			if best > maximum:
				maximum = best
				worst = {"identity": clip["identity"], "foot": foot, "derived": own, "reference": theirs}
	return {
		"reference": reference_path, "tolerance_seconds": tolerance, "topology_mismatches": topology_mismatches,
		"maximum_boundary_difference_seconds": maximum, "worst": worst,
		"within_tolerance": topology_mismatches.is_empty() and maximum <= tolerance and by_identity.size() == (document["clips"] as Array).size(),
	}


# State changes of one foot as [entered state, time]. A looping clip's seam split (first and last segments of the
# same state) is one interval, not a transition.
func _transitions(segments: Array, looping: bool) -> Array:
	var result: Array = []
	for position in range(1, segments.size()):
		var segment := segments[position] as Dictionary
		result.append([String(segment["state"]), float(segment["start_seconds"])])
	if looping and segments.size() > 1 and String((segments[0] as Dictionary)["state"]) != String((segments[-1] as Dictionary)["state"]):
		result.append([String((segments[0] as Dictionary)["state"]), 0.0])
	return result


func _summary(clip: Dictionary, foot: String) -> Array:
	var result: Array = []
	if not (clip["feet"] as Dictionary).has(foot):
		return result
	for segment in ((clip["feet"] as Dictionary)[foot] as Dictionary)["segments"]:
		result.append("%s[%.3f,%.3f]" % [String(segment["state"]).substr(0, 1).to_upper(), float(segment["start_seconds"]), float(segment["end_seconds"])])
	return result


func _boundary_summary(diagnostics: Dictionary, foot: String) -> Array:
	var result: Array = []
	if not diagnostics.has(foot):
		return result
	for boundary in (diagnostics[foot] as Dictionary)["boundaries"]:
		var text := "%s@%.3f:%s" % [boundary["kind"], float(boundary["time_seconds"]), boundary["cause"]]
		if (boundary as Dictionary).has("ground_slide_m"):
			text += "(%.3fm)" % float(boundary["ground_slide_m"])
		result.append(text)
	return result


func _skeleton(path: String) -> Dictionary:
	var scene := ResourceLoader.load(path, "PackedScene") as PackedScene
	if scene == null:
		_fail("Skeleton scene is unavailable: %s" % path)
		return {}
	var root := scene.instantiate()
	var skeletons := root.find_children("*", "Skeleton3D", true, false)
	if skeletons.size() != 1:
		root.free()
		_fail("Skeleton scene must contain exactly one Skeleton3D: %s" % path)
		return {}
	var skeleton := skeletons[0] as Skeleton3D
	var result := {"model": sampler.skeleton_model(skeleton), "name": String(skeleton.name), "motion_scale": skeleton.motion_scale}
	root.free()
	return result


func _tool_root() -> String:
	return String(get_script().resource_path).get_base_dir().get_base_dir().get_base_dir()


func _load_helper(filename: String) -> Script:
	var command_path: String = get_script().resource_path
	return load(command_path.get_base_dir().get_base_dir().path_join("lib").path_join(filename).simplify_path()) as Script


func _argument(prefix: String, fallback: String) -> String:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(prefix):
			return argument.trim_prefix(prefix)
	return fallback


func _json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path):
		_fail("JSON file is missing: %s" % path)
		return {}
	var value: Variant = JSON.parse_string(FileAccess.get_file_as_string(path))
	if value is Dictionary:
		return value
	_fail("JSON object is invalid: %s" % path)
	return {}


func _write_text(path: String, text: String) -> void:
	var absolute := ProjectSettings.globalize_path(path)
	DirAccess.make_dir_recursive_absolute(absolute.get_base_dir())
	var file := FileAccess.open(absolute, FileAccess.WRITE)
	if file == null:
		_fail("Could not write JSON: %s" % path)
		return
	file.store_string(text)


func _fail(message: String) -> void:
	push_error(message)
	errors.append(message)
