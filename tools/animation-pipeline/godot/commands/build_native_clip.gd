extends SceneTree

# Builds an ANIM-003 native idle clip (a Godot `Animation` resource on the reference skeleton) from Blender-evaluated
# armature samples. Invoked by `commands/export_idle_clip.py`; it writes only the requested staged output and report.
#
# Usage (user arguments after `--`):
#   --samples=<samples.json> --skeleton-scene=<res:// imported character scene> --output=<staged clip .tres>
#   --name=<animation resource name> [--bone-map=<res:// BoneMap>] [--loop=<true|false, default true>]
#   [--report=<report.json>] [--compare=<res:// reference clip> --compare-fps=<rate, default 30>]
#   [--check-skeleton-scene=<res:// scene>]...  (reloads the written clip and checks SOMA-mapped tracks resolve)

const DEFAULT_BONE_MAP := "res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres"
const SOMA_MAPPED_BONES := [
	"Hips", "Spine", "Chest", "UpperChest", "Neck", "Head", "Jaw", "LeftEye", "RightEye",
	"LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
	"LeftThumbMetacarpal", "LeftThumbProximal", "LeftThumbDistal",
	"LeftIndexProximal", "LeftIndexIntermediate", "LeftIndexDistal",
	"LeftMiddleProximal", "LeftMiddleIntermediate", "LeftMiddleDistal",
	"LeftRingProximal", "LeftRingIntermediate", "LeftRingDistal",
	"LeftLittleProximal", "LeftLittleIntermediate", "LeftLittleDistal",
	"RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
	"RightThumbMetacarpal", "RightThumbProximal", "RightThumbDistal",
	"RightIndexProximal", "RightIndexIntermediate", "RightIndexDistal",
	"RightMiddleProximal", "RightMiddleIntermediate", "RightMiddleDistal",
	"RightRingProximal", "RightRingIntermediate", "RightRingDistal",
	"RightLittleProximal", "RightLittleIntermediate", "RightLittleDistal",
	"LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
	"RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
]

var builder: Script
var comparison: Script
var errors: Array[String] = []


func _init() -> void:
	builder = _load_helper("native_clip_builder.gd")
	comparison = _load_helper("native_clip_comparison.gd")
	_run()
	for error in errors:
		printerr("NATIVE_CLIP_ERROR: %s" % error)
	quit(1 if not errors.is_empty() else 0)


func _run() -> void:
	var samples_path := _argument("--samples=", "")
	var scene_path := _argument("--skeleton-scene=", "")
	var output := _argument("--output=", "")
	var clip_name := _argument("--name=", "")
	if samples_path.is_empty() or scene_path.is_empty() or output.is_empty() or clip_name.is_empty():
		_fail("--samples=, --skeleton-scene=, --output= and --name= are required.")
		return
	if not output.ends_with(".tres"):
		_fail("--output= must name a text resource (.tres): %s" % output)
		return
	var loaded: Dictionary = builder.load_samples(samples_path)
	if loaded.has("error"):
		_fail(String(loaded["error"]))
		return
	var skeleton_result: Dictionary = builder.load_skeleton(scene_path)
	if skeleton_result.has("error"):
		_fail(String(skeleton_result["error"]))
		return
	var skeleton := skeleton_result["skeleton"] as Dictionary
	var bone_map_path := _argument("--bone-map=", DEFAULT_BONE_MAP)
	var bone_map := ResourceLoader.load(bone_map_path) as BoneMap
	if bone_map == null:
		_fail("Bone map is unavailable: %s" % bone_map_path)
		return
	var built: Dictionary = builder.build(loaded["samples"], skeleton, bone_map, clip_name, _argument("--loop=", "true") == "true")
	if built.has("error"):
		_fail(String(built["error"]))
		return
	var report := built["report"] as Dictionary
	report["skeleton_scene"] = scene_path
	report["source"] = (loaded["samples"] as Dictionary).get("source", {})
	var save_error := ResourceSaver.save(built["animation"] as Animation, output)
	if save_error != OK:
		_fail("Could not save the staged clip %s: %s" % [output, error_string(save_error)])
		return
	var reloaded := ResourceLoader.load(output, "Animation", ResourceLoader.CACHE_MODE_IGNORE) as Animation
	if reloaded == null:
		_fail("The staged clip does not reload as an Animation: %s" % output)
		return
	report["preview_parity"] = comparison.compare_to_samples(reloaded, loaded["samples"], skeleton, built["sources"], float(built["scale"]))
	var checks := {}
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--check-skeleton-scene="):
			var check_path := argument.trim_prefix("--check-skeleton-scene=")
			checks[check_path] = _compatibility(reloaded, check_path)
	report["compatibility"] = checks
	var compare_path := _argument("--compare=", "")
	if not compare_path.is_empty():
		var reference := ResourceLoader.load(compare_path, "Animation", ResourceLoader.CACHE_MODE_IGNORE) as Animation
		if reference == null:
			_fail("Comparison clip is unavailable: %s" % compare_path)
		else:
			report["comparison"] = comparison.compare(reloaded, reference, skeleton, float(_argument("--compare-fps=", "30")))
			report["comparison"]["reference"] = compare_path
			report["comparison"]["reference_preview_parity"] = comparison.compare_to_samples(reference, loaded["samples"], skeleton, built["sources"], float(built["scale"]))
	var report_path := _argument("--report=", "")
	if not report_path.is_empty():
		var file := FileAccess.open(report_path, FileAccess.WRITE)
		if file == null:
			_fail("Could not write the report: %s" % report_path)
			return
		file.store_string(JSON.stringify(report, "  ", true))
		file.close()
	print("NATIVE_CLIP_JSON=%s" % JSON.stringify({
		"output": output, "tracked_bones": (report["tracked_bones"] as Array).size(),
		"position_tracks": report["position_tracks"], "length_seconds": report["length_seconds"],
		"compatibility": checks,
	}))


# Every SOMA-mapped bone track must bind to the scene's skeleton by name and resolve to one of its bones.
func _compatibility(animation: Animation, scene_path: String) -> Dictionary:
	var loaded: Dictionary = builder.load_skeleton(scene_path)
	if loaded.has("error"):
		_fail(String(loaded["error"]))
		return {"compatible": false}
	var skeleton := loaded["skeleton"] as Dictionary
	var names: Array[String] = skeleton["names"]
	var unresolved: Array[String] = []
	var optional_absent: Array[String] = []
	for track in animation.get_track_count():
		var path := animation.track_get_path(track)
		var bone := String(path.get_concatenated_subnames())
		var node := String(path.get_concatenated_names()).trim_prefix("%")
		var resolves := node == String(skeleton["name"]) and names.has(bone)
		if resolves:
			continue
		if SOMA_MAPPED_BONES.has(bone):
			if not unresolved.has(bone):
				unresolved.append(bone)
		elif not optional_absent.has(bone):
			optional_absent.append(bone)
	if not unresolved.is_empty():
		_fail("Clip has SOMA-mapped tracks that do not resolve on %s: %s" % [scene_path, ", ".join(unresolved)])
	return {"compatible": unresolved.is_empty(), "unresolved_mapped": unresolved, "absent_optional": optional_absent}


func _load_helper(filename: String) -> Script:
	var command_path: String = get_script().resource_path
	return load(command_path.get_base_dir().get_base_dir().path_join("lib").path_join(filename).simplify_path()) as Script


func _argument(prefix: String, fallback: String) -> String:
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with(prefix):
			return argument.trim_prefix(prefix)
	return fallback


func _fail(message: String) -> void:
	errors.append(message)
