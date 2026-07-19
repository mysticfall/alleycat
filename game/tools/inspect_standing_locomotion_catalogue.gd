extends SceneTree

# Read-only inspection of imported, extracted, and packaged catalogue layers.
# This script requires the explicit import and package generation steps.

const INDEX_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/index.json"
const CLIP_DIR := "res://assets/characters/reference/female/animations/locomotion/clips"
const LIBRARY_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres"

var _errors: Array[String] = []
var _scene_root: Node

func _init() -> void:
	var index := _load_json(INDEX_PATH)
	var motions: Dictionary = index.get("motions", {})
	var actions: Array[String] = []
	var blend_path := ""
	for motion in motions.values():
		if String(motion.get("status", "")) == "success" and String(motion.get("group", "")) == "locomotion_standing":
			actions.append(String(motion.get("action", "")))
			blend_path = _to_res_path(String(motion.get("group_blend", "")))
	actions.sort()
	var imported := _load_imported_library(blend_path)
	var packaged := ResourceLoader.load(LIBRARY_PATH, "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary
	if packaged == null:
		_fail("Missing catalogue library: %s" % LIBRARY_PATH)
		_finish()
		return
	print("Standing Locomotion Catalogue Inspection")
	var imported_names := imported.get_animation_list() if imported != null else []
	var imported_extras: Array[StringName] = []
	for imported_name in imported_names:
		if not actions.has(String(imported_name)):
			imported_extras.append(imported_name)
	print("Imported library entries=%d selected_actions=%d extras=%s" % [imported_names.size(), actions.size(), imported_extras])
	for action in actions:
		var source := imported.get_animation(action) if imported != null else null
		var clip := ResourceLoader.load("%s/%s.res" % [CLIP_DIR, action], "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
		var library_clip := packaged.get_animation(action)
		if source == null or clip == null or library_clip == null:
			_fail("Missing imported, extracted, or packaged layer for %s" % action)
			continue
		var prefixes := {}
		for track in source.get_track_count():
			var path := String(source.track_get_path(track))
			var separator := path.rfind(":")
			if separator > 0:
				prefixes[path.substr(0, separator)] = true
		print("%s length=%.9f tracks=%d source_prefixes=%s" % [action, clip.length, clip.get_track_count(), prefixes.keys()])
	_finish()

func _load_imported_library(path: String) -> AnimationLibrary:
	var packed := ResourceLoader.load(path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if packed == null:
		_fail("Failed to load imported standing scene: %s" % path)
		return null
	_scene_root = packed.instantiate()
	var players := _scene_root.find_children("*", "AnimationPlayer", true, false)
	if players.size() != 1:
		_fail("Imported scene does not contain exactly one AnimationPlayer")
		return null
	var player := players[0] as AnimationPlayer
	var libraries := player.get_animation_library_list()
	if libraries.size() != 1:
		_fail("Imported scene does not contain exactly one AnimationLibrary")
		return null
	return player.get_animation_library(libraries[0])

func _load_json(path: String) -> Dictionary:
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
	if is_instance_valid(_scene_root):
		_scene_root.free()
	quit(1 if not _errors.is_empty() else 0)
