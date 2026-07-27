extends SceneTree

const INDEX_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/index.json"
const SIDECAR_PATH := "res://assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend.import"
const CLIP_DIR := "res://assets/characters/reference/female/animations/locomotion/clips"
const LIBRARY_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_library.tres"
const CATALOGUE_PATH := "res://assets/characters/reference/female/animations/locomotion/standing_locomotion_catalogue.json"
const REQUIRED_ROLES := ["Idle", "ForwardWalk", "BackwardWalk", "WalkArcLeft", "WalkArcRight", "SideStepLeft", "SideStepRight", "TurnInPlaceLeft90", "TurnInPlaceRight90"]
# Imported Root Euler-Y retains the canonical Blender representation: its sign is the
# inverse of the clockwise-positive physical/visible Root-tail heading. Runtime actor
# yaw conversion remains unchanged and consumes this channel representation.
const PIVOT_IDS := {"c9ceef5f-b96c-11e4-a802-0aaa78deedf9": 1.0, "c9cef01d-b96c-11e4-a802-0aaa78deedf9": -1.0}

var _errors: Array[String] = []

func _init() -> void:
	var index := _json(INDEX_PATH)
	var motions: Dictionary = index.get("motions", {})
	var expected := {}
	for motion_id in motions:
		var motion = motions[motion_id]
		if motion is Dictionary and motion.get("status") == "success" and motion.get("group") == "locomotion_standing":
			expected[String(motion.get("action", ""))] = motion
	_expect(expected.size() == 9, "Processed index must contain exactly nine standing actions")
	_expect(not motions.has("c9c94322-b96c-11e4-a802-0aaa78deedf9"), "Rejected left-45 action remains in processed index")
	var sidecar := ConfigFile.new()
	_expect(sidecar.load(SIDECAR_PATH) == OK, "Import sidecar is unreadable")
	var subresources: Dictionary = sidecar.get_value("params", "_subresources", {})
	var animations: Dictionary = subresources.get("animations", {})
	_expect(animations.size() == 9, "Import sidecar must contain exactly nine extraction entries")
	for action in expected:
		_expect(animations.has(action), "Missing sidecar action %s" % action)
		var settings: Dictionary = animations.get(action, {})
		_expect(String(settings.get("save_to_file/fallback_path", "")) == "%s/%s.res" % [CLIP_DIR, action], "Fallback path mismatch for %s" % action)
		var motion: Dictionary = expected[action]
		var intent = motion.get("loop_intent", {})
		_expect(intent is Dictionary and (intent as Dictionary).get("effective_loop_intent") is bool, "Missing persisted loop intent for %s" % action)
		var expected_loop := Animation.LOOP_LINEAR if intent is Dictionary and bool((intent as Dictionary).get("effective_loop_intent", false)) else Animation.LOOP_NONE
		_expect(int(settings.get("settings/loop_mode", -1)) == expected_loop, "Loop policy mismatch for %s" % action)
	var library := ResourceLoader.load(LIBRARY_PATH, "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary
	_expect(library != null and library.get_animation_list().size() == 9, "Library must load with exactly nine keys")
	var catalogue := _json(CATALOGUE_PATH)
	_expect(int(catalogue.get("clip_count", -1)) == 9 and int(catalogue.get("catalogue_schema_version", -1)) == 2, "Catalogue count or schema is invalid")
	var roles := {}
	for clip in catalogue.get("clips", []):
		roles[String(clip.get("role", ""))] = true
	for role in REQUIRED_ROLES:
		_expect(roles.has(role), "Catalogue is missing role %s" % role)
	_expect(roles.keys().size() == 9, "Catalogue role set is not the exact required nine")
	for motion_id in PIVOT_IDS:
		var action := "mixamo_" + String(motion_id).replace("-", "_")
		var clip := ResourceLoader.load("%s/%s.res" % [CLIP_DIR, action], "Animation", ResourceLoader.CACHE_MODE_REPLACE) as Animation
		var motion: Dictionary = expected.get(action, {})
		var pivot_intent = motion.get("loop_intent", {})
		var pivot_loop := pivot_intent is Dictionary and bool((pivot_intent as Dictionary).get("effective_loop_intent", false))
		_expect(clip != null and clip.loop_mode == (Animation.LOOP_LINEAR if pivot_loop else Animation.LOOP_NONE), "Pivot loop mode must match persisted seam intent: %s" % action)
		if clip != null:
			var position_track := clip.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_POSITION_3D)
			var rotation_track := clip.find_track(^"%GeneralSkeleton:Root", Animation.TYPE_ROTATION_3D)
			var translation := clip.position_track_interpolate(position_track, clip.length) - clip.position_track_interpolate(position_track, 0.0)
			var yaw := (clip.rotation_track_interpolate(rotation_track, 0.0).inverse() * clip.rotation_track_interpolate(rotation_track, clip.length)).get_euler().y
			print("PIVOT_ROOT action=%s translation=%s yaw=%.9f" % [action, translation, yaw])
			# Source-derived turns are intentionally not snapped to metadata's exact 90°.  0.12 rad
			# admits the observed authored cadence while still rejecting a 45° or reversed pivot.
			_expect(translation.length() < 0.02 and absf(absf(yaw) - PI / 2.0) < 0.12 and signf(yaw) == PIVOT_IDS[motion_id], "Pivot root measurement is invalid for %s" % action)
	if _errors.is_empty():
		print("Validated ANIM-003 nine-key library and persisted pose-seam loop intent")
	quit(1 if not _errors.is_empty() else 0)

func _json(path: String) -> Dictionary:
	var parsed = JSON.parse_string(FileAccess.get_file_as_string(path))
	return parsed if parsed is Dictionary else {}

func _expect(condition: bool, message: String) -> void:
	if not condition:
		push_error(message)
		_errors.append(message)
