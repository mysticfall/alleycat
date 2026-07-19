extends SceneTree

# Validates source metadata, configured import metadata, extracted clips, and
# package correspondence. Missing post-import outputs are intentional failures.

const INDEX_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/index.json"
const SELECTION_PATH := "res://assets/characters/reference/female/animations/source/mixamo/selection.csv"
const SOURCE_MANIFEST_PATH := "res://assets/characters/reference/female/animations/source/mixamo/manifest.csv"
const SIDECAR_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend.import"
const CLIP_DIR := "res://assets/characters/reference/female/animations/locomotion/clips"
const LIBRARY_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres"
const CATALOGUE_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json"
const EXPECTED_COUNT := 46
const EXPECTED_SAMPLES := 2612
const EXPECTED_DURATION := 106.66666668653485
const DURATION_TOLERANCE := 0.00001
const REQUIRED_BONES: Array[String] = ["Root", "Hips", "LeftFoot", "RightFoot", "LeftToes", "RightToes"]
const SILHOUETTE_FILTER: Array[StringName] = [
	&"Head", &"Neck", &"UpperChest", &"Chest", &"Spine", &"Hips",
	&"RightThumbMetacarpal", &"RightThumbProximal", &"RightThumbDistal",
	&"RightIndexProximal", &"RightIndexIntermediate", &"RightIndexDistal",
	&"RightMiddleProximal", &"RightMiddleIntermediate", &"RightMiddleDistal",
	&"RightRingProximal", &"RightRingIntermediate", &"RightRingDistal",
	&"RightLittleProximal", &"RightLittleIntermediate", &"RightLittleDistal",
	&"LeftThumbMetacarpal", &"LeftThumbProximal", &"LeftThumbDistal",
	&"LeftIndexProximal", &"LeftIndexIntermediate", &"LeftIndexDistal",
	&"LeftMiddleProximal", &"LeftMiddleIntermediate", &"LeftMiddleDistal",
	&"LeftRingProximal", &"LeftRingIntermediate", &"LeftRingDistal",
	&"LeftLittleProximal", &"LeftLittleIntermediate", &"LeftLittleDistal",
	&"RightFoot", &"LeftFoot",
]
const LATERAL_IDS: Array[String] = [
	"c9c9d9d6-b96c-11e4-a802-0aaa78deedf9",
	"c9c9db9e-b96c-11e4-a802-0aaa78deedf9",
	"c9c9829c-b96c-11e4-a802-0aaa78deedf9",
	"c9c985b7-b96c-11e4-a802-0aaa78deedf9",
	"c9c7ff20-b96c-11e4-a802-0aaa78deedf9",
]

var _errors: Array[String] = []

func _init() -> void:
	var index := _load_json(INDEX_PATH)
	var expected := _validate_index_and_metrics(index)
	_validate_selection(expected)
	_validate_source_manifest(expected)
	_validate_sidecar(expected)
	_validate_clips(expected)
	_validate_package(expected)
	if FileAccess.file_exists(CATALOGUE_PATH) and ResourceLoader.exists(LIBRARY_PATH, "AnimationLibrary"):
		_validate_imported_resources(expected)
	_validate_portability(expected)
	if _errors.is_empty():
		print("Validated complete %d-clip standing locomotion catalogue" % EXPECTED_COUNT)
	_finish()

func _validate_index_and_metrics(index: Dictionary) -> Dictionary:
	if int(index.get("schema_version", -1)) != 2 or int(index.get("metrics_schema_version", -1)) != 2:
		_fail("Processed index and metrics schema versions must both be 2")
	var expected := {}
	var motion_ids := {}
	var class_counts := {"StandingIdle": 0, "StandingLocomotion": 0, "TurnInPlace": 0}
	var total_samples := 0
	var motions = index.get("motions", {})
	if not (motions is Dictionary):
		_fail("Processed motions is not a Dictionary")
		return expected
	for key in motions:
		var motion = motions[key]
		if not (motion is Dictionary) or String(motion.get("status", "")) != "success":
			continue
		if String(motion.get("group", "")) != "locomotion_standing" or String(motion.get("category", "")) != "locomotion":
			_fail("Successful motion is outside the standing locomotion group: %s" % key)
			continue
		var action := String(motion.get("action", ""))
		var motion_id := String(motion.get("motion_id", key))
		if action.is_empty() or motion_id.is_empty() or expected.has(action) or motion_ids.has(motion_id):
			_fail("Empty or duplicate action/motion ID: %s / %s" % [action, motion_id])
			continue
		expected[action] = motion
		motion_ids[motion_id] = true
		var motion_class := String(motion.get("motion_class", ""))
		if not class_counts.has(motion_class):
			_fail("Unsupported motion class: %s" % motion_class)
		else:
			class_counts[motion_class] += 1
		var metrics_path := _to_res_path(String(motion.get("metrics", "")))
		var metrics := _load_json(metrics_path)
		if int(metrics.get("schema_version", -1)) != 2 or String(metrics.get("action", "")) != action:
			_fail("Metrics schema/action mismatch: %s" % metrics_path)
		if String(metrics.get("root_source", "")) != "reconstructed_root" or not bool(metrics.get("root_created", false)):
			_fail("Metrics root invariant failed: %s" % metrics_path)
		total_samples += int(metrics.get("sample_count", 0))
		var source_manifest: Dictionary = motion.get("manifest", {})
		_validate_portable_path(String(source_manifest.get("file", "")), "source-manifest file for %s" % action, false)
	if expected.size() != EXPECTED_COUNT:
		_fail("Expected %d processed motions, found %d" % [EXPECTED_COUNT, expected.size()])
	if class_counts != {"StandingIdle": 4, "StandingLocomotion": 36, "TurnInPlace": 6}:
		_fail("Motion class split is not 4/36/6: %s" % class_counts)
	if total_samples != EXPECTED_SAMPLES:
		_fail("Expected %d metric samples, found %d" % [EXPECTED_SAMPLES, total_samples])
	for motion_id in LATERAL_IDS:
		if not motion_ids.has(motion_id):
			_fail("Missing required lateral motion: %s" % motion_id)
	return expected

func _validate_selection(expected: Dictionary) -> void:
	if not FileAccess.file_exists(SELECTION_PATH):
		_fail("Missing selection CSV")
		return
	var lines := FileAccess.get_file_as_string(SELECTION_PATH).split("\n", false)
	var selected := {}
	var class_counts := {"StandingIdle": 0, "StandingLocomotion": 0, "TurnInPlace": 0}
	var motions_by_id := {}
	for motion in expected.values():
		motions_by_id[String(motion.get("motion_id", ""))] = motion
	for line_index in range(1, lines.size()):
		var columns := _parse_csv_line(String(lines[line_index]))
		if columns.size() != 9:
			_fail("Selection row %d must contain exactly 9 CSV fields, found %d" % [line_index + 1, columns.size()])
			continue
		var motion_id := columns[0]
		var enabled = _parse_strict_csv_bool(columns[1], "enabled", motion_id)
		if enabled == null or not enabled:
			continue
		if motion_id.is_empty() or selected.has(motion_id):
			_fail("Selection has an empty or duplicate enabled motion ID: %s" % motion_id)
			continue
		selected[motion_id] = true
		if not motions_by_id.has(motion_id):
			_fail("Enabled selection motion is missing from processed index: %s" % motion_id)
			continue
		var motion: Dictionary = motions_by_id[motion_id]
		var action := String(motion.get("action", ""))
		var expected_action := "mixamo_" + motion_id.replace("-", "_")
		if action != expected_action:
			_fail("Selection/index action association mismatch for %s: expected %s, found %s" % [motion_id, expected_action, action])
		var create_root_motion = _parse_strict_csv_bool(columns[7], "create_root_motion", motion_id)
		if create_root_motion == null:
			continue
		var tags: Array[String] = []
		for tag in columns[4].split(";", false):
			tags.append(String(tag))

		_validate_provenance_field("index", motion_id, action, motion, "motion_id", motion_id)
		_validate_provenance_field("index", motion_id, action, motion, "category", columns[2])
		_validate_provenance_field("index", motion_id, action, motion, "motion_class", columns[3])
		_validate_provenance_field("index", motion_id, action, motion, "tags", tags)
		_validate_provenance_field("index", motion_id, action, motion, "gender", columns[5])
		_validate_provenance_field("index", motion_id, action, motion, "group", columns[6])
		_validate_provenance_field("index", motion_id, action, motion, "create_root_motion", create_root_motion)
		_validate_provenance_field("index", motion_id, action, motion, "selection_notes", columns[8])

		var metrics_path := _to_res_path(String(motion.get("metrics", "")))
		var metrics := _load_json(metrics_path)
		if String(metrics.get("action", "")) != action:
			_fail("Metrics action association mismatch for %s (%s): found %s" % [motion_id, action, metrics.get("action")])
		var metrics_selection = metrics.get("selection", {})
		if not (metrics_selection is Dictionary):
			_fail("Metrics selection provenance is missing for %s (%s)" % [motion_id, action])
		else:
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "motion_id", motion_id)
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "category", columns[2])
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "motion_class", columns[3])
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "tags", tags)
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "gender", columns[5])
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "group", columns[6])
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "create_root_motion", create_root_motion)
			_validate_provenance_field("metrics", motion_id, action, metrics_selection, "notes", columns[8])
		if class_counts.has(columns[3]):
			class_counts[columns[3]] += 1
		else:
			_fail("Selection has unsupported motion class for %s (%s): %s" % [motion_id, action, columns[3]])
	if selected.size() != EXPECTED_COUNT or class_counts != {"StandingIdle": 4, "StandingLocomotion": 36, "TurnInPlace": 6}:
		_fail("Selection count or 4/36/6 split is invalid")
	var expected_ids := {}
	for motion in expected.values():
		expected_ids[String(motion.get("motion_id", ""))] = true
	if selected != expected_ids:
		_fail("Selection and processed-index motion ID sets differ")

func _parse_strict_csv_bool(value: String, field: String, motion_id: String):
	if value == "true":
		return true
	if value == "false":
		return false
	_fail("Selection %s must be exactly true or false for %s, found '%s'" % [field, motion_id, value])
	return null

func _validate_provenance_field(layer: String, motion_id: String, action: String, value: Dictionary, field: String, expected_value) -> void:
	if not value.has(field):
		_fail("Missing %s provenance field '%s' for %s (%s)" % [layer, field, motion_id, action])
		return
	var actual_value = value[field]
	if field == "tags":
		actual_value = _as_string_array(actual_value)
	if actual_value != expected_value:
		_fail("Mismatched %s provenance field '%s' for %s (%s): expected %s, found %s" % [layer, field, motion_id, action, expected_value, actual_value])

func _as_string_array(value) -> Array[String]:
	var result: Array[String] = []
	if not (value is Array):
		return result
	for item in value:
		result.append(String(item))
	return result

func _validate_source_manifest(expected: Dictionary) -> void:
	if not FileAccess.file_exists(SOURCE_MANIFEST_PATH):
		_fail("Missing source manifest CSV")
		return
	var rows := {}
	var lines := FileAccess.get_file_as_string(SOURCE_MANIFEST_PATH).split("\n", false)
	for line_index in range(1, lines.size()):
		var columns := _parse_csv_line(String(lines[line_index]))
		if columns.size() < 5:
			continue
		if columns[0].is_empty() or rows.has(columns[0]):
			_fail("Source manifest has an empty or duplicate motion ID")
			continue
		rows[columns[0]] = columns
	for motion in expected.values():
		var motion_id := String(motion.get("motion_id", ""))
		if not rows.has(motion_id):
			_fail("Processed motion is missing from source manifest: %s" % motion_id)
			continue
		var columns: Array[String] = rows[motion_id]
		var source: Dictionary = motion.get("manifest", {})
		if String(source.get("name", "")) != columns[1] or String(source.get("description", "")) != columns[2] or String(source.get("type", "")) != columns[3] or String(source.get("file", "")) != columns[4]:
			_fail("Processed source provenance differs from source manifest: %s" % motion_id)

func _validate_sidecar(expected: Dictionary) -> void:
	var config := ConfigFile.new()
	var error := config.load(SIDECAR_PATH)
	if error != OK:
		_fail("Failed to parse standing import sidecar: %s" % error_string(error))
		return
	if config.get_value("params", "animation/import", false) != true or int(config.get_value("params", "animation/fps", 0)) != 30:
		_fail("Standing import must enable animation at 30 fps")
	var subresources = config.get_value("params", "_subresources", {})
	if not (subresources is Dictionary):
		_fail("Standing import _subresources is not a Dictionary")
		return
	var nodes: Dictionary = subresources.get("nodes", {})
	var skeleton: Dictionary = nodes.get("PATH:Female/Skeleton3D", {})
	var bone_map = skeleton.get("retarget/bone_map")
	if bone_map == null or String(bone_map.resource_path) != "res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres":
		_fail("Standing import does not use the required MakeHuman bone map")
	elif ResourceUID.id_to_text(ResourceLoader.get_resource_uid(String(bone_map.resource_path))) != "uid://db42k2j8v05ku":
		_fail("Standing import MakeHuman bone map UID is invalid")
	if skeleton.get("retarget/rest_fixer/fix_silhouette/enable") != true:
		_fail("Standing import silhouette fixer is not enabled")
	var filter: Array = skeleton.get("retarget/rest_fixer/fix_silhouette/filter", [])
	if filter != SILHOUETTE_FILTER:
		_fail("Standing import silhouette filter is incomplete")
	var animations = subresources.get("animations", {})
	if not (animations is Dictionary) or animations.size() != EXPECTED_COUNT:
		_fail("Standing import must contain exactly %d animations" % EXPECTED_COUNT)
		return
	if _string_key_set(animations) != _string_key_set(expected):
		_fail("Standing import action set differs from processed index")
	var canonical_uids := {}
	for action in animations:
		var settings: Dictionary = animations[action]
		var expected_path := "%s/%s.res" % [CLIP_DIR, action]
		if settings.get("save_to_file/enabled") != true:
			_fail("Import extraction is disabled: %s" % action)
		var fallback_path := String(settings.get("save_to_file/fallback_path", ""))
		if fallback_path != expected_path:
			_fail("Import fallback path is empty or mismatched: %s" % action)
		var primary_path := String(settings.get("save_to_file/path", ""))
		if primary_path == expected_path:
			pass
		elif primary_path.begins_with("uid://"):
			var resolved_path := ResourceUID.ensure_path(primary_path)
			if resolved_path.is_empty() or resolved_path != expected_path:
				_fail("Import UID does not resolve to the expected clip: %s -> %s" % [primary_path, resolved_path])
			if canonical_uids.has(primary_path):
				_fail("Duplicate canonical import UID: %s" % primary_path)
			canonical_uids[primary_path] = action
		else:
			_fail("Import primary path is empty or unsupported: %s" % action)
		if settings.get("save_to_file/keep_custom_tracks") != false or int(settings.get("settings/loop_mode", -1)) != 0:
			_fail("Import track or loop settings are invalid: %s" % action)
		for key in settings:
			var setting_name := String(key)
			if setting_name.begins_with("slice_") and setting_name.ends_with("/save_to_file/enabled") and settings[key] == true:
				_fail("Enabled import slice changes extraction semantics: %s (%s)" % [action, setting_name])

func _validate_clips(expected: Dictionary) -> void:
	var expected_metrics := {}
	for motion in expected.values():
		expected_metrics[_to_res_path(String(motion.get("metrics", ""))).get_file()] = true
	var metrics_directory := DirAccess.open("res://assets/characters/reference/female/animations/processed/mixamo/metrics")
	var actual_metrics := {}
	if metrics_directory == null:
		_fail("Missing standing metrics directory")
	else:
		for file_name in metrics_directory.get_files():
			if file_name.ends_with(".metrics.json"):
				actual_metrics[file_name] = true
	if actual_metrics != expected_metrics:
		_fail("Metrics file set differs from processed-index metrics set")
	var directory := DirAccess.open(CLIP_DIR)
	if directory == null:
		_fail("Missing extracted clips directory")
		return
	var files: Array[String] = []
	for file_name in directory.get_files():
		if file_name.ends_with(".res"):
			files.append(file_name)
	files.sort()
	if files.size() != EXPECTED_COUNT:
		_fail("Expected %d extracted clips, found %d" % [EXPECTED_COUNT, files.size()])
	var total_duration := 0.0
	for action in expected:
		var path := "%s/%s.res" % [CLIP_DIR, action]
		var animation := ResourceLoader.load(path, "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
		if animation == null:
			_fail("Failed to load extracted Animation: %s" % path)
			continue
		if animation.resource_name != action:
			_fail("Extracted clip resource_name mismatch: %s" % action)
		total_duration += animation.length
		_validate_required_tracks(action, animation)
	if abs(total_duration - EXPECTED_DURATION) > DURATION_TOLERANCE:
		_fail("Imported clip duration total %.12f differs from %.12f" % [total_duration, EXPECTED_DURATION])

func _validate_package(expected: Dictionary) -> void:
	if not FileAccess.file_exists(CATALOGUE_PATH) or not ResourceLoader.exists(LIBRARY_PATH, "AnimationLibrary"):
		_fail("Post-import catalogue outputs are missing; run generation in the controlled import slice")
		return
	var catalogue := _load_json(CATALOGUE_PATH)
	if int(catalogue.get("catalogue_schema_version", -1)) != 1 or int(catalogue.get("source_index_schema_version", -1)) != 2 or int(catalogue.get("metrics_schema_version", -1)) != 2:
		_fail("Catalogue schema fields are invalid")
	if String(catalogue.get("source_index", "")) != INDEX_PATH or String(catalogue.get("animation_library", "")) != LIBRARY_PATH:
		_fail("Catalogue source or library path is invalid")
	var clips: Array = catalogue.get("clips", [])
	if int(catalogue.get("clip_count", -1)) != EXPECTED_COUNT or clips.size() != EXPECTED_COUNT:
		_fail("Catalogue clip count is invalid")
	var catalogue_keys := {}
	for clip in clips:
		var key := String(clip.get("key", ""))
		if key.is_empty() or catalogue_keys.has(key):
			_fail("Catalogue has an empty or duplicate key: %s" % key)
		catalogue_keys[key] = true
		if String(clip.get("action", "")) != key or String(clip.get("animation_resource", "")) != "%s/%s.res" % [CLIP_DIR, key]:
			_fail("Catalogue action/resource mismatch: %s" % key)
		if expected.has(key):
			var motion: Dictionary = expected[key]
			for field in ["motion_id", "group", "motion_class", "category"]:
				if String(clip.get(field, "")) != String(motion.get(field, "")):
					_fail("Catalogue %s mismatch: %s" % [field, key])
			if String(clip.get("group_blend", "")) != _to_res_path(String(motion.get("group_blend", ""))) or String(clip.get("metrics", "")) != _to_res_path(String(motion.get("metrics", ""))) or clip.get("tags", []) != motion.get("tags", []):
				_fail("Catalogue provenance mismatch: %s" % key)
		for required in ["length", "track_count", "fps", "frame_range", "sample_count", "root_source", "root_created"]:
			if not clip.has(required):
				_fail("Catalogue is missing %s: %s" % [required, key])
		var source_manifest: Dictionary = clip.get("source_manifest", {})
		if String(source_manifest.get("file", "")).is_empty():
			_fail("Catalogue source-manifest file is missing: %s" % key)
		for forbidden in ["timestamp", "generated_at", "target_scene", "target_animation_player_root", "target_skeleton_track_path"]:
			if catalogue.has(forbidden) or clip.has(forbidden):
				_fail("Catalogue contains forbidden field: %s" % forbidden)
	if catalogue_keys != _string_key_set(expected):
		_fail("Catalogue and processed-index action sets differ")
	var library := ResourceLoader.load(LIBRARY_PATH, "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary
	if library == null or library.resource_name != "standing_locomotion_library":
		_fail("Catalogue AnimationLibrary is missing or misnamed")
		return
	var library_keys := {}
	for key in library.get_animation_list():
		library_keys[String(key)] = true
	if library_keys != catalogue_keys:
		_fail("Library and catalogue key sets differ")
	for action in library_keys:
		var packaged := library.get_animation(action)
		var extracted := ResourceLoader.load("%s/%s.res" % [CLIP_DIR, action], "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
		_compare_animations(action, extracted, packaged)

func _validate_imported_resources(expected: Dictionary) -> void:
	var blend_paths := {}
	for motion in expected.values():
		blend_paths[_to_res_path(String(motion.get("group_blend", "")))] = true
	if blend_paths.size() != 1:
		_fail("Processed motions do not reference exactly one standing group .blend")
		return
	var blend_path := String(blend_paths.keys()[0])
	var packed := ResourceLoader.load(blend_path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if packed == null:
		_fail("Failed to load explicitly imported standing scene: %s" % blend_path)
		return
	var scene_root := packed.instantiate()
	var players := scene_root.find_children("*", "AnimationPlayer", true, false)
	var skeletons := scene_root.find_children("*", "Skeleton3D", true, false)
	if players.size() != 1 or skeletons.size() != 1:
		_fail("Imported standing scene must contain one AnimationPlayer and one Skeleton3D")
		scene_root.free()
		return
	var player := players[0] as AnimationPlayer
	var library_names := player.get_animation_library_list()
	if library_names.size() != 1:
		_fail("Imported standing scene must contain one AnimationLibrary")
		scene_root.free()
		return
	var imported_library := player.get_animation_library(library_names[0])
	var imported_keys := {}
	for key in imported_library.get_animation_list():
		if key == &"RESET":
			continue
		imported_keys[String(key)] = true
	if imported_keys != _string_key_set(expected):
		_fail("Imported and processed-index action sets differ")
	var total_duration := 0.0
	for action in expected:
		var imported := imported_library.get_animation(action)
		var extracted := ResourceLoader.load("%s/%s.res" % [CLIP_DIR, action], "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
		if imported == null:
			_fail("Imported scene is missing action: %s" % action)
			continue
		total_duration += imported.length
		_validate_required_tracks(action, imported)
		_compare_imported_to_extracted(action, imported, extracted)
	if abs(total_duration - EXPECTED_DURATION) > DURATION_TOLERANCE:
		_fail("Imported duration total %.12f differs from %.12f" % [total_duration, EXPECTED_DURATION])
	scene_root.free()

func _compare_imported_to_extracted(action: String, imported: Animation, extracted: Animation) -> void:
	if extracted == null:
		_fail("Missing extracted clip for imported pass-through check: %s" % action)
		return
	if imported.length != extracted.length or imported.loop_mode != extracted.loop_mode or imported.step != extracted.step or imported.get_track_count() != extracted.get_track_count():
		_fail("Imported animation header changed during extraction: %s" % action)
		return
	for track in imported.get_track_count():
		var imported_path := String(imported.track_get_path(track))
		var extracted_path := String(extracted.track_get_path(track))
		var separator := imported_path.rfind(":")
		if separator > 0 and not ["%GeneralSkeleton", "GeneralSkeleton", "Female/GeneralSkeleton"].has(imported_path.substr(0, separator)):
			_fail("Imported track uses an unsupported skeleton prefix: %s track %d" % [action, track])
		if extracted_path != _normalise_imported_path(imported_path):
			_fail("Track path changed beyond accepted prefix normalisation: %s track %d" % [action, track])
		if imported.track_get_type(track) != extracted.track_get_type(track) or imported.track_is_enabled(track) != extracted.track_is_enabled(track) or imported.track_get_interpolation_type(track) != extracted.track_get_interpolation_type(track) or imported.track_get_interpolation_loop_wrap(track) != extracted.track_get_interpolation_loop_wrap(track):
			_fail("Imported track metadata changed during extraction: %s track %d" % [action, track])
		var count := imported.track_get_key_count(track)
		if count != extracted.track_get_key_count(track):
			_fail("Imported track key count changed during extraction: %s track %d" % [action, track])
			continue
		for key in count:
			if imported.track_get_key_time(track, key) != extracted.track_get_key_time(track, key) or imported.track_get_key_transition(track, key) != extracted.track_get_key_transition(track, key) or imported.track_get_key_value(track, key) != extracted.track_get_key_value(track, key):
				_fail("Imported track key changed during extraction: %s track %d key %d" % [action, track, key])

func _normalise_imported_path(path: String) -> String:
	var separator := path.rfind(":")
	if separator <= 0:
		return path
	var prefix := path.substr(0, separator)
	if ["%GeneralSkeleton", "GeneralSkeleton", "Female/GeneralSkeleton"].has(prefix):
		return "GeneralSkeleton" + path.substr(separator)
	return path

func _validate_required_tracks(action: String, animation: Animation) -> void:
	var bones := {}
	for track in animation.get_track_count():
		var path := String(animation.track_get_path(track))
		var separator := path.rfind(":")
		if separator > 0:
			bones[path.substr(separator + 1)] = true
	for bone in REQUIRED_BONES:
		if not bones.has(bone):
			_fail("%s is missing required skeleton track: %s" % [action, bone])

func _compare_animations(action: String, source: Animation, packaged: Animation) -> void:
	if source == null or packaged == null:
		_fail("Missing animation layer for pass-through check: %s" % action)
		return
	if source.length != packaged.length or source.loop_mode != packaged.loop_mode or source.step != packaged.step or source.get_track_count() != packaged.get_track_count():
		_fail("Animation header changed in package: %s" % action)
		return
	for track in source.get_track_count():
		if source.track_get_type(track) != packaged.track_get_type(track) or source.track_is_enabled(track) != packaged.track_is_enabled(track) or source.track_get_interpolation_type(track) != packaged.track_get_interpolation_type(track) or source.track_get_interpolation_loop_wrap(track) != packaged.track_get_interpolation_loop_wrap(track):
			_fail("Track metadata changed in package: %s track %d" % [action, track])
		var count := source.track_get_key_count(track)
		if count != packaged.track_get_key_count(track):
			_fail("Track key count changed in package: %s track %d" % [action, track])
			continue
		for key in count:
			if source.track_get_key_time(track, key) != packaged.track_get_key_time(track, key) or source.track_get_key_transition(track, key) != packaged.track_get_key_transition(track, key) or source.track_get_key_value(track, key) != packaged.track_get_key_value(track, key):
				_fail("Track key changed in package: %s track %d key %d" % [action, track, key])

func _validate_portability(expected: Dictionary) -> void:
	var paths: Array[String] = [INDEX_PATH, SELECTION_PATH, SOURCE_MANIFEST_PATH, SIDECAR_PATH, CATALOGUE_PATH]
	for motion in expected.values():
		paths.append(_to_res_path(String(motion.get("metrics", ""))))
	for path in paths:
		if not FileAccess.file_exists(path):
			continue
		var text := FileAccess.get_file_as_string(path)
		if text.contains("locomotion_" + "crouch") or text.contains("mixamo_" + "locomotion_library") or text.contains("mixamo_" + "locomotion_manifest"):
			_fail("Crouch or non-catalogue package reference leaked into %s" % path)
		for marker in ["/" + "home/", "C:" + "\\", "/" + "tmp/", "target_" + "scene"]:
			if text.contains(marker):
				_fail("Machine, temporary, or consumer-specific content leaked into %s: %s" % [path, marker])

func _validate_portable_path(path: String, label: String, require_res := true) -> void:
	if path.is_empty() or path.begins_with("uid://") or path.begins_with("/") or path.contains(":\\"):
		_fail("Invalid portable path for %s: %s" % [label, path])
	elif require_res and not path.begins_with("res://"):
		_fail("Expected res:// path for %s: %s" % [label, path])

func _parse_csv_line(line: String) -> Array[String]:
	var fields: Array[String] = []
	var current := ""
	var quoted := false
	var index := 0
	while index < line.length():
		var character := line[index]
		if character == '"':
			if quoted and index + 1 < line.length() and line[index + 1] == '"':
				current += '"'
				index += 1
			else:
				quoted = not quoted
		elif character == "," and not quoted:
			fields.append(current)
			current = ""
		else:
			current += character
		index += 1
	fields.append(current)
	return fields

func _string_key_set(value: Dictionary) -> Dictionary:
	var result := {}
	for key in value:
		result[String(key)] = true
	return result

func _load_json(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		_fail("Missing JSON file: %s" % path)
		return {}
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not (parsed is Dictionary):
		_fail("Invalid JSON object: %s" % path)
		return {}
	return parsed

func _to_res_path(path: String) -> String:
	return "res://" + path.substr(5) if path.begins_with("game/") else path

func _fail(message: String) -> void:
	push_error(message)
	_errors.append(message)

func _finish() -> void:
	for message in _errors:
		printerr(message)
	quit(1 if not _errors.is_empty() else 0)
