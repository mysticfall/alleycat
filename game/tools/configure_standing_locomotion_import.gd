extends SceneTree

# Configures contained-animation extraction without loading the source .blend.
# Run this before the explicit Godot import pass.

const INDEX_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/index.json"
const SIDECAR_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend.import"
const CLIP_DIR := "res://assets/characters/reference/female/animations/locomotion/clips"
const EXPECTED_SCHEMA_VERSION := 2
const EXPECTED_CLIP_COUNT := 46

var _errors: Array[String] = []

func _init() -> void:
	var actions := _read_actions()
	if _has_errors():
		_finish()
		return

	var config := ConfigFile.new()
	var load_error := config.load(SIDECAR_PATH)
	if load_error != OK:
		_fail("Failed to load import sidecar: %s" % error_string(load_error))
		_finish()
		return
	var preserved_settings := _snapshot_non_animation_settings(config)

	var subresources = config.get_value("params", "_subresources", {})
	if not (subresources is Dictionary):
		_fail("Import sidecar _subresources is not a Dictionary")
		_finish()
		return
	var original_nodes: Dictionary = (subresources as Dictionary).get("nodes", {}).duplicate(true)
	if original_nodes.is_empty():
		_fail("Import sidecar has no node import settings to preserve")
		_finish()
		return

	var animations := {}
	for action in actions:
		var path := "%s/%s.res" % [CLIP_DIR, action]
		animations[action] = {
			"save_to_file/enabled": true,
			"save_to_file/fallback_path": path,
			"save_to_file/keep_custom_tracks": false,
			"save_to_file/path": path,
			"settings/loop_mode": 0,
			"slices/amount": 0,
		}

	# Replace any prior action dictionary with the minimal pre-import configuration.
	# Godot may later expand it with valid canonical opaque slice defaults.
	subresources["animations"] = animations
	config.set_value("params", "_subresources", subresources)
	var save_error := config.save(SIDECAR_PATH)
	if save_error != OK:
		_fail("Failed to save import sidecar: %s" % error_string(save_error))
		_finish()
		return

	_verify_saved(actions, original_nodes, preserved_settings)
	if not _has_errors():
		print("Configured %d standing locomotion animation extraction entries" % actions.size())
	_finish()

func _read_actions() -> Array[String]:
	var index := _load_json(INDEX_PATH)
	if int(index.get("schema_version", -1)) != EXPECTED_SCHEMA_VERSION:
		_fail("Processed index must use schema version %d" % EXPECTED_SCHEMA_VERSION)
		return []
	var motions = index.get("motions", {})
	if not (motions is Dictionary):
		_fail("Processed index motions is not a Dictionary")
		return []
	var unique := {}
	for motion_id in motions:
		var motion = motions[motion_id]
		if not (motion is Dictionary):
			continue
		if String(motion.get("status", "")) != "success" or String(motion.get("group", "")) != "locomotion_standing":
			continue
		var action := String(motion.get("action", ""))
		if action.is_empty():
			_fail("Successful standing motion %s has an empty action" % motion_id)
		elif unique.has(action):
			_fail("Duplicate standing action: %s" % action)
		else:
			unique[action] = true
	var actions: Array[String] = []
	for action in unique.keys():
		actions.append(String(action))
	actions.sort()
	if actions.size() != EXPECTED_CLIP_COUNT:
		_fail("Expected %d successful standing actions, found %d" % [EXPECTED_CLIP_COUNT, actions.size()])
	return actions

func _verify_saved(expected_actions: Array[String], expected_nodes: Dictionary, expected_preserved: Dictionary) -> void:
	var config := ConfigFile.new()
	var load_error := config.load(SIDECAR_PATH)
	if load_error != OK:
		_fail("Failed to reload configured import sidecar: %s" % error_string(load_error))
		return
	var subresources = config.get_value("params", "_subresources", {})
	if not (subresources is Dictionary):
		_fail("Reloaded _subresources is not a Dictionary")
		return
	if subresources.get("nodes", {}) != expected_nodes:
		_fail("Node import settings changed while configuring animations")
	if _snapshot_non_animation_settings(config) != expected_preserved:
		_fail("Non-animation import settings changed while configuring animations")
	var animations = subresources.get("animations", {})
	if not (animations is Dictionary):
		_fail("Reloaded animations is not a Dictionary")
		return
	var actual_actions: Array[String] = []
	for action in animations.keys():
		actual_actions.append(String(action))
	actual_actions.sort()
	if actual_actions != expected_actions:
		_fail("Reloaded animation action set does not match the processed index")
	for action in expected_actions:
		var settings = animations.get(action, {})
		var expected_path := "%s/%s.res" % [CLIP_DIR, action]
		if not (settings is Dictionary):
			_fail("Animation settings are not a Dictionary: %s" % action)
			continue
		if settings.size() != 6:
			_fail("Pre-import animation %s has non-minimal or unsupported settings" % action)
		if settings.get("save_to_file/enabled") != true:
			_fail("Animation extraction is not enabled: %s" % action)
		if String(settings.get("save_to_file/path", "")) != expected_path:
			_fail("Animation has an invalid save path: %s" % action)
		if String(settings.get("save_to_file/fallback_path", "")) != expected_path:
			_fail("Animation has an invalid fallback path: %s" % action)
		if String(settings.get("save_to_file/path", "")).begins_with("uid://"):
			_fail("Animation save path uses a UID: %s" % action)
		if settings.get("save_to_file/keep_custom_tracks") != false:
			_fail("Animation keeps custom tracks: %s" % action)
		if int(settings.get("settings/loop_mode", -1)) != 0 or int(settings.get("slices/amount", -1)) != 0:
			_fail("Animation has invalid loop or slice settings: %s" % action)

func _snapshot_non_animation_settings(config: ConfigFile) -> Dictionary:
	var snapshot := {}
	for section in config.get_sections():
		var values := {}
		for key in config.get_section_keys(section):
			var value = config.get_value(section, key)
			if section == "params" and key == "_subresources" and value is Dictionary:
				value = value.duplicate(true)
				value.erase("animations")
			values[key] = value
		snapshot[section] = values
	return snapshot

func _load_json(path: String) -> Dictionary:
	if not FileAccess.file_exists(path):
		_fail("Missing JSON file: %s" % path)
		return {}
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	if not (parsed is Dictionary):
		_fail("Invalid JSON object: %s" % path)
		return {}
	return parsed

func _fail(message: String) -> void:
	push_error(message)
	_errors.append(message)

func _has_errors() -> bool:
	return not _errors.is_empty()

func _finish() -> void:
	for message in _errors:
		printerr(message)
	quit(1 if _has_errors() else 0)
