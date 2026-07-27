extends SceneTree

# Configures contained-animation extraction without loading the source .blend.
# Run this before the explicit Godot import pass.

const INDEX_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/index.json"
const SIDECAR_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend.import"
const CLIP_DIR := "res://assets/characters/reference/female/animations/locomotion/clips"
const EXPECTED_SCHEMA_VERSION := 2
const EXPECTED_CLIP_COUNT := 9

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
	var subresources = config.get_value("params", "_subresources", {})
	if not (subresources is Dictionary):
		_fail("Import sidecar _subresources is not a Dictionary")
		_finish()
		return
	_ensure_retarget_node_settings(subresources)
	var preserved_settings := _snapshot_non_animation_settings(config)
	var preserved_subresources := _snapshot_non_animation_subresources(subresources)
	for key in subresources.keys().duplicate():
		if _is_animation_settings(subresources[key]):
			subresources.erase(key)
	var animations := {}
	for action in actions:
		var path := "%s/%s.res" % [CLIP_DIR, action]
		animations[action] = {
			"save_to_file/enabled": true,
			"save_to_file/fallback_path": path,
			"save_to_file/keep_custom_tracks": false,
			"save_to_file/path": path,
			"settings/loop_mode": Animation.LOOP_LINEAR if actions[action] else Animation.LOOP_NONE,
			"slices/amount": 0,
		}
	subresources["animations"] = animations

	config.set_value("params", "_subresources", subresources)
	var save_error := config.save(SIDECAR_PATH)
	if save_error != OK:
		_fail("Failed to save import sidecar: %s" % error_string(save_error))
		_finish()
		return

	_verify_saved(actions, preserved_subresources, preserved_settings)
	if not _has_errors():
		print("Configured %d standing locomotion animation extraction entries" % actions.size())
	_finish()

func _read_actions() -> Dictionary:
	var index := _load_json(INDEX_PATH)
	if int(index.get("schema_version", -1)) != EXPECTED_SCHEMA_VERSION:
		_fail("Processed index must use schema version %d" % EXPECTED_SCHEMA_VERSION)
		return {}
	var motions = index.get("motions", {})
	if not (motions is Dictionary):
		_fail("Processed index motions is not a Dictionary")
		return {}
	var actions := {}
	for motion_id in motions:
		var motion = motions[motion_id]
		if not (motion is Dictionary):
			continue
		if String(motion.get("status", "")) != "success" or String(motion.get("group", "")) != "locomotion_standing":
			continue
		var action := String(motion.get("action", ""))
		if action.is_empty():
			_fail("Successful standing motion %s has an empty action" % motion_id)
		elif actions.has(action):
			_fail("Duplicate standing action: %s" % action)
		else:
			actions[action] = _effective_loop_intent(motion)
	if actions.size() != EXPECTED_CLIP_COUNT:
		_fail("Expected %d successful standing actions, found %d" % [EXPECTED_CLIP_COUNT, actions.size()])
	return actions

func _verify_saved(expected_actions: Dictionary, expected_subresources: Dictionary, expected_preserved: Dictionary) -> void:
	var config := ConfigFile.new()
	var load_error := config.load(SIDECAR_PATH)
	if load_error != OK:
		_fail("Failed to reload configured import sidecar: %s" % error_string(load_error))
		return
	var subresources = config.get_value("params", "_subresources", {})
	if not (subresources is Dictionary):
		_fail("Reloaded _subresources is not a Dictionary")
		return
	if _snapshot_non_animation_subresources(subresources) != expected_subresources:
		_fail("Non-animation import subresources changed while configuring animations")
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
	var expected_action_names: Array[String] = []
	for action in expected_actions:
		expected_action_names.append(String(action))
	expected_action_names.sort()
	if actual_actions != expected_action_names:
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
		var expected_loop_mode := Animation.LOOP_LINEAR if expected_actions[action] else Animation.LOOP_NONE
		if int(settings.get("settings/loop_mode", -1)) != expected_loop_mode or int(settings.get("slices/amount", -1)) != 0:
			_fail("Animation loop policy or slice setting is invalid: %s" % action)

func _effective_loop_intent(motion: Dictionary) -> bool:
	var intent = motion.get("loop_intent", {})
	if not (intent is Dictionary) or not (intent as Dictionary).get("effective_loop_intent") is bool:
		_fail("Standing motion has no persisted ANIM-001 effective loop intent: %s" % motion.get("motion_id", ""))
		return false
	return bool((intent as Dictionary)["effective_loop_intent"])

func _is_animation_settings(value) -> bool:
	return value is Dictionary and (value as Dictionary).has("save_to_file/enabled")

func _snapshot_non_animation_subresources(subresources: Dictionary) -> Dictionary:
	var snapshot := subresources.duplicate(true)
	snapshot.erase("animations")
	for key in snapshot.keys():
		if _is_animation_settings(snapshot[key]):
			snapshot.erase(key)
	return snapshot

func _ensure_retarget_node_settings(subresources: Dictionary) -> void:
	var bone_map := load("res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres")
	if bone_map == null:
		_fail("Required MakeHuman bone map cannot be loaded")
		return
	var nodes: Dictionary = subresources.get("nodes", {})
	nodes["PATH:Female/Skeleton3D"] = {
		"retarget/bone_map": bone_map,
		"retarget/rest_fixer/fix_silhouette/enable": true,
		"retarget/rest_fixer/fix_silhouette/filter": [
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
			&"LeftLittleProximal", &"LeftLittleIntermediate", &"LeftLittleDistal", &"RightFoot", &"LeftFoot",
		],
	}
	subresources["nodes"] = nodes

func _snapshot_non_animation_settings(config: ConfigFile) -> Dictionary:
	var snapshot := {}
	for section in config.get_sections():
		var values := {}
		for key in config.get_section_keys(section):
			var value = config.get_value(section, key)
			if section == "params" and key == "_subresources" and value is Dictionary:
				value = _snapshot_non_animation_subresources(value)
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
