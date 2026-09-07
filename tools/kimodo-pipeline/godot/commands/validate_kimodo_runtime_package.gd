extends SceneTree

const ROOT_POSITION := ^"%GeneralSkeleton:Root"
const ROOT_ROTATION := ^"%GeneralSkeleton:Root"
const DEFAULT_SKELETON_PATH := "PATH:Female/Skeleton3D"
const DEFAULT_BONE_MAP := "res://assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres"
const DEFAULT_CHARACTER := "female"
const DEFAULT_VARIANT := "sad"
const DURATION_TOLERANCE := 0.000001
var errors: Array[String] = []
var _coordinate_contract: Dictionary = {}
var transport: Script
var coordinate_contract: Script
var contact_evidence: Script
var runtime_package_descriptor: Script
var runtime_capability: Script

func _init() -> void:
	transport = _load_helper("sample_transport.gd")
	coordinate_contract = _load_helper("coordinate_contract.gd")
	contact_evidence = _load_helper("locomotion_contact_evidence.gd")
	runtime_package_descriptor = _load_helper("runtime_package_descriptor.gd")
	runtime_capability = _load_helper("runtime_capability.gd")
	call_deferred("_run")

func _run() -> void:
	var descriptor := _descriptor()
	if not descriptor.is_empty(): _validate_package(descriptor)
	if errors.is_empty(): print("Validated ANIM-002 Kimodo runtime package(s): %s/%s" % [descriptor.get("name", ""), descriptor.get("variant", "")])
	quit(1 if not errors.is_empty() else 0)

func _descriptor() -> Dictionary:
	var path := _argument("--descriptor=")
	if not path.is_empty(): return _json(path)
	var name := _argument("--package=")
	if name.is_empty(): name = "locomotion"
	var variant := _argument("--variant=")
	if variant.is_empty(): variant = DEFAULT_VARIANT
	var character := _argument("--character=")
	if character.is_empty(): character = DEFAULT_CHARACTER
	var set_directory := "res://assets/characters/reference/%s/animations/processed/%s" % [character, name]
	var index := set_directory.path_join(variant).path_join("processed-index.json")
	var durable_descriptor := index.get_base_dir().path_join("provenance/runtime-package-descriptor-v1.json")
	if FileAccess.file_exists(durable_descriptor):
		var descriptor := _json(durable_descriptor)
		var descriptor_error: String = runtime_package_descriptor.validate(descriptor)
		if not descriptor_error.is_empty():
			_expect(false, descriptor_error)
			return {}
		return descriptor
	if name == "locomotion":
		_expect(false, "Production package descriptor is missing: %s" % durable_descriptor)
		return {}
	return {
		"name": name, "character": character, "variant": variant, "index": index,
		"blend": set_directory.path_join("%s_%s.blend" % [name, variant]),
		"root": "res://assets/characters/reference/%s/animations/runtime/%s/%s" % [character, name, variant],
		"contact_evidence_required": name == "locomotion",
	}

func _validate_package(package: Dictionary) -> void:
	var name := String(package.get("name", "")); var index := _json(String(package.get("index", "")))
	var variant := String(package.get("variant", index.get("variant", "")))
	_expect(not name.is_empty(), "Package descriptor has no name")
	_expect(not variant.is_empty() and not String(package.get("character", "")).is_empty(), "Package descriptor has no character/set/variant identity")
	var mode_error := _validate_declared_mode(package)
	_expect(mode_error.is_empty(), mode_error)
	if not mode_error.is_empty(): return
	_expect(int(index.get("schema_version", 0)) == 2 and int(index.get("metrics_schema_version", 0)) == 2, "Processed index schema is stale: %s" % name)
	var contract_error: String = coordinate_contract.validate_evidence(index.get("coordinate_contract"))
	_expect(contract_error.is_empty(), "Coordinate contract is invalid: %s" % contract_error)
	if not contract_error.is_empty(): return
	_coordinate_contract = index["coordinate_contract"]
	var expected := _expected_actions(index, String(package["index"]))
	var runtime_root := String(package.get("root", ""))
	_expect(runtime_root.get_file() == variant and runtime_root.get_base_dir().get_file() == name, "Runtime root must be the character-owned runtime/%s/%s package directory: %s" % [name, variant, runtime_root])
	var published_root := String(package.get("published_root", runtime_root))
	var contact_package := package.duplicate(true)
	contact_package["root"] = published_root
	var contacts: Dictionary = contact_evidence.describe(contact_package, index, published_root)
	contacts["index_path"] = String(package.get("published_index", package["index"]))
	contacts["runtime_file"] = runtime_root.path_join(String(contacts.get("runtime_path", "")))
	_expect(not contacts.has("error"), String(contacts.get("error", "")))
	if contacts.has("error"): return
	_validate_import_configuration(package, expected)
	var imported := _imported_library_and_skeleton(String(package.get("blend", "")))
	var library_filename := "%s_%s.tres" % [name, variant]
	var library_path := runtime_root.path_join(library_filename)
	var runtime := ResourceLoader.load(library_path, "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary
	_expect(runtime != null, "Runtime animation library failed to load: %s" % library_path)
	if runtime == null: return
	_validate_library_bijection(name, expected, runtime)
	_validate_source_correspondence(name, expected, imported.get("library"), runtime)
	_reject_clip_directory(package)
	_validate_catalogue(package, index, expected, runtime, library_filename, published_root)
	if String(package.get("publication_mode", "")) == "contact-free-idle":
		_expect(not bool(contacts.get("declared", false)), "A contact-free idle package must not declare contact evidence")
		var contact_free_catalogue := _json(runtime_root.path_join("%s_%s_catalogue.json" % [name, variant]))
		_expect(not contact_free_catalogue.has("contact_evidence"), "A contact-free idle catalogue must not declare contact evidence")
	if bool(contacts.get("declared", false)):
		var catalogue_path := runtime_root.path_join("%s_%s_catalogue.json" % [name, variant])
		var catalogue := _json(catalogue_path)
		contacts["index_path"] = String(package.get("published_index", package["index"]))
		var contact_error: String = contact_evidence.validate_published(contacts, index, published_root, catalogue)
		_expect(contact_error.is_empty(), contact_error)
	var skeleton = imported.get("skeleton") as Skeleton3D
	if skeleton != null:
		var skeleton_error: String = coordinate_contract.validate_skeleton(_coordinate_contract, skeleton)
		_expect(skeleton_error.is_empty(), "Imported skeleton violates coordinate contract: %s" % skeleton_error)
	var root = imported.get("root") as Node
	if root != null: root.free()

func _validate_declared_mode(package: Dictionary) -> String:
	var mode := String(package.get("publication_mode", ""))
	if mode.is_empty(): return ""
	var name := String(package.get("name", ""))
	if mode == "contact-evidenced-locomotion":
		if name != "locomotion" or not bool(package.get("contact_evidence_required", false)) or not package.get("contact_evidence") is Dictionary:
			return "A contact-evidenced locomotion package requires declared contact evidence"
		return ""
	if mode == "contact-free-idle":
		if name != "idle" or bool(package.get("contact_evidence_required", false)) or package.has("contact_evidence"):
			return "A contact-free idle package must not declare or require contact evidence"
		return ""
	return "Package descriptor has an unsupported publication mode"

func _expected_actions(index: Dictionary, index_path: String) -> Dictionary:
	var expected := {}
	var actions: Array = index.get("actions", [])
	_expect(not actions.is_empty(), "Processed index has no actions")
	for value in actions:
		if not value is Dictionary:
			_expect(false, "Processed index contains an invalid action entry")
			continue
		var entry := value as Dictionary; var identity := String(entry.get("external_identity", "")); var action := String(entry.get("action", "")); var metrics_path := index_path.get_base_dir().path_join(String(entry.get("metrics", "")))
		_expect(not identity.is_empty() and not action.is_empty() and not expected.has(identity), "Processed index action identity is invalid: %s" % identity)
		if identity.is_empty() or action.is_empty() or expected.has(identity): continue
		var capability_error: String = runtime_capability.validate_processed_action(entry)
		_expect(capability_error.is_empty(), "%s: %s" % [capability_error, identity])
		if not capability_error.is_empty(): continue
		_validate_processed_provenance(entry, identity)
		var transport_evidence = entry.get("transport", {})
		if transport_evidence is Dictionary: transport_evidence = transport.from_json(transport_evidence, bool(entry.get("is_loop", false)))
		_expect(transport_evidence is Dictionary and transport.validate(transport_evidence, bool(entry.get("is_loop", false))).is_empty(), "Processed transport is invalid: %s" % identity)
		var metrics := _json(metrics_path)
		_expect(String(metrics.get("action", "")) == action, "Metrics action mismatch: %s" % identity)
		_expect(int(metrics.get("schema_version", 0)) == 2 and not String(metrics.get("root_source", "")).is_empty(), "Metrics provenance is invalid: %s" % identity)
		expected[identity] = entry
	return expected


func _validate_processed_provenance(entry: Dictionary, identity: String) -> void:
	var capability := String(entry["runtime_capability"])
	if not runtime_capability.requires_directional_event_provenance(capability): return
	var source = entry.get("source_directional_event_evidence")
	var derivation = entry.get("event_derivation_evidence")
	var output = entry.get("post_repair_event_evidence")
	_expect(source is Dictionary and String((source as Dictionary).get("contract", "")) == "anim002_source_directional_phase_events_v1", "Source directional event provenance is stale: %s" % identity)
	_expect(derivation is Dictionary and String((derivation as Dictionary).get("contract", "")) == "anim002_event_derivation_v1", "Event derivation provenance is stale: %s" % identity)
	_expect(output is Dictionary and String((output as Dictionary).get("contract", "")) == "anim002_post_repair_directional_phase_events_v2" and String((output as Dictionary).get("evaluation_stage", "")) == "final_repaired_evaluated_hierarchy" and (output as Dictionary).get("refusals", []).is_empty(), "Post-repair directional phase event provenance is stale or refused: %s" % identity)
	if runtime_capability.requires_loop_repair(capability):
		var repair = entry.get("loop_repair_evidence")
		_expect(repair is Dictionary and String((repair as Dictionary).get("contract", "")) == "anim002_selected_loop_repair_provenance_v2" and not String((repair as Dictionary).get("blend_profile", "")).is_empty(), "Selected loop-repair provenance is stale: %s" % identity)

func _validate_import_configuration(package: Dictionary, expected: Dictionary) -> void:
	var sidecar := String(package.get("blend", "")) + ".import"; var config := ConfigFile.new()
	if config.load(sidecar) != OK: _expect(false, "Could not load import sidecar: %s" % sidecar); return
	var subresources: Dictionary = config.get_value("params", "_subresources", {}); var nodes: Dictionary = subresources.get("nodes", {}); var settings = nodes.get(String(package.get("skeleton_import_path", DEFAULT_SKELETON_PATH)))
	_expect(settings is Dictionary, "Import sidecar lacks declared skeleton settings: %s" % sidecar)
	if settings is Dictionary:
		var bone_map := (settings as Dictionary).get("retarget/bone_map") as BoneMap
		_expect(bone_map != null and bone_map.resource_path == String(package.get("bone_map", DEFAULT_BONE_MAP)), "Import sidecar retarget binding is invalid: %s" % sidecar)
		_expect((settings as Dictionary).get("retarget/rest_fixer/fix_silhouette/enable") == true, "Import sidecar silhouette fixer is disabled: %s" % sidecar)
		_expect((settings as Dictionary).get("retarget/rest_fixer/normalize_position_tracks", false) == true, "Import sidecar has disabled position-track normalisation: %s" % sidecar)
	for key in config.get_section_keys("params"):
		if String(key).ends_with("save_to_file/enabled") and config.get_value("params", key) == true:
			_expect(false, "Import configuration externalises animations globally: %s" % sidecar)
	var animations: Dictionary = subresources.get("animations", {})
	_expect(animations.size() == expected.size(), "Import sidecar action set is invalid: %s" % sidecar)
	for action in animations.keys():
		var block = animations[action]
		if block is Dictionary:
			for setting in (block as Dictionary).keys():
				if String(setting).ends_with("save_to_file/enabled") and (block as Dictionary)[setting] == true:
					_expect(false, "Import configuration externalises animation %s: %s" % [action, sidecar])
	for identity in expected:
		var entry: Dictionary = expected[identity]; var settings_for_action = animations.get(String(entry["action"]))
		_expect(settings_for_action is Dictionary and (settings_for_action as Dictionary).get("save_to_file/enabled") == false, "Import sidecar does not retain embedded action: %s" % identity)
		if settings_for_action is Dictionary:
			_expect(int((settings_for_action as Dictionary).get("settings/loop_mode", -1)) == (Animation.LOOP_LINEAR if bool(entry.get("is_loop", false)) else Animation.LOOP_NONE), "Import sidecar loop intent differs: %s" % identity)

func _imported_library_and_skeleton(path: String) -> Dictionary:
	var scene := ResourceLoader.load(path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	_expect(scene != null, "Processed blend failed to load: %s" % path)
	if scene == null: return {}
	var root := scene.instantiate(); get_root().add_child(root)
	var players := root.find_children("*", "AnimationPlayer", true, false); var skeletons := root.find_children("*", "Skeleton3D", true, false)
	_expect(players.size() == 1, "Processed blend must expose one AnimationPlayer")
	_expect(skeletons.size() == 1, "Processed blend must expose one Skeleton3D")
	if players.size() != 1: return {"root": root}
	var player := players[0] as AnimationPlayer; var names := player.get_animation_library_list()
	_expect(names.size() == 1, "Processed blend must expose one AnimationLibrary")
	return {"root": root, "library": player.get_animation_library(names[0]) if names.size() == 1 else null, "skeleton": skeletons[0] if skeletons.size() == 1 else null}

func _validate_library_bijection(name: String, expected: Dictionary, library: AnimationLibrary) -> void:
	var names := library.get_animation_list(); _expect(names.size() == expected.size(), "%s runtime library identity count is invalid" % name)
	for identity in expected:
		_expect(library.has_animation(identity), "%s runtime library is missing identity: %s" % [name, identity])
		if library.has_animation(identity): _validate_animation(identity, library.get_animation(identity), bool((expected[identity] as Dictionary).get("is_loop", false)))
	for identity in names: _expect(expected.has(String(identity)), "%s runtime library has unexpected identity: %s" % [name, identity])

func _validate_source_correspondence(name: String, expected: Dictionary, imported: AnimationLibrary, runtime: AnimationLibrary) -> void:
	_expect(imported != null, "%s imported source library is unavailable" % name)
	if imported == null: return
	for identity in expected:
		var entry: Dictionary = expected[identity]; var source := _source_animation(imported, String(entry["action"])); var packaged := runtime.get_animation(identity)
		_expect(source != null and packaged != null, "Imported/runtime correspondence is missing: %s" % identity)
		if source == null or packaged == null: continue
		var normalised := source.duplicate(true) as Animation; var transport_evidence: Dictionary = transport.from_json(entry.get("transport", {}), bool(entry.get("is_loop", false))); var error: String = transport.normalise_imported(normalised, transport_evidence, bool(entry.get("is_loop", false)))
		_expect(error.is_empty(), "Imported source transport cannot be normalised: %s" % identity)
		if error.is_empty(): _expect(_animations_correspond(normalised, packaged), "Imported source/runtime correspondence differs: %s" % identity)

func _animations_correspond(source: Animation, runtime: Animation) -> bool:
	if not is_equal_approx(source.length, runtime.length) or source.loop_mode != runtime.loop_mode or not is_equal_approx(source.step, runtime.step) or source.get_track_count() != runtime.get_track_count(): return false
	var runtime_tracks := {}
	for track in runtime.get_track_count(): runtime_tracks["%s|%s" % [runtime.track_get_path(track), runtime.track_get_type(track)]] = track
	for track in source.get_track_count():
		var path := String(source.track_get_path(track)); var colon := path.rfind(":"); if colon <= 0: return false
		var key := "%s|%s" % [NodePath("%GeneralSkeleton" + path.substr(colon)), source.track_get_type(track)]
		if not runtime_tracks.has(key): return false
		var paired: int = runtime_tracks[key]
		if source.track_is_enabled(track) != runtime.track_is_enabled(paired) or source.track_get_interpolation_type(track) != runtime.track_get_interpolation_type(paired) or source.track_get_interpolation_loop_wrap(track) != runtime.track_get_interpolation_loop_wrap(paired) or source.track_get_key_count(track) != runtime.track_get_key_count(paired): return false
		for point in source.track_get_key_count(track):
			if not is_equal_approx(source.track_get_key_time(track, point), runtime.track_get_key_time(paired, point)) or not is_equal_approx(source.track_get_key_transition(track, point), runtime.track_get_key_transition(paired, point)) or source.track_get_key_value(track, point) != runtime.track_get_key_value(paired, point): return false
	return true

func _reject_clip_directory(package: Dictionary) -> void:
	var root := String(package["root"])
	_expect(DirAccess.open(root.path_join("clips")) == null, "Runtime package must not contain a standalone clips directory: %s" % root)
	var directory := DirAccess.open(root)
	if directory == null: return
	for file in directory.get_files():
		_expect(not file.ends_with(".res"), "Runtime package must not contain standalone clip resources: %s" % file)

func _validate_catalogue(package: Dictionary, index: Dictionary, expected: Dictionary, library: AnimationLibrary, library_filename: String, published_root: String) -> void:
	var name := String(package["name"]); var variant := String(package.get("variant", index.get("variant", "")))
	var path := String(package["root"]).path_join("%s_%s_catalogue.json" % [name, variant]); var catalogue := _json(path)
	_expect(int(catalogue.get("schema_version", 0)) == 3, "Runtime catalogue schema is unsupported; embedded-library packages require schema v3 (a stale clip-path catalogue must be regenerated): %s" % path)
	_expect(String(catalogue.get("package", "")) == String(index.get("package", name)), "Runtime catalogue package differs from index")
	_expect(String(catalogue.get("package_variant", "")) == String(index.get("variant", "")), "Runtime catalogue variant differs from index")
	_expect(String(catalogue.get("animation_library", "")) == library_filename, "Runtime catalogue library link is invalid: %s" % catalogue.get("animation_library", ""))
	_expect(not library_filename.is_absolute_path() and not library_filename.contains("/"), "Runtime catalogue library link must be the co-located package library filename: %s" % library_filename)
	_expect(library_filename == "%s_%s.tres" % [name, variant], "Runtime package library filename must follow <set>_<variant>.tres: %s" % library_filename)
	var clips: Array = catalogue.get("clips", []); _expect(int(catalogue.get("clip_count", -1)) == expected.size() and clips.size() == expected.size(), "Runtime catalogue clip count is invalid")
	var seen := {}
	var seen_capabilities := {}
	for value in clips:
		if not value is Dictionary: _expect(false, "Runtime catalogue contains an invalid clip"); continue
		var clip := value as Dictionary; var identity := String(clip.get("identity", "")); _expect(expected.has(identity) and not seen.has(identity), "Runtime catalogue identity is invalid: %s" % identity)
		if not expected.has(identity) or seen.has(identity): continue
		seen[identity] = true; var entry: Dictionary = expected[identity]; var animation := library.get_animation(identity)
		var capability := String(clip.get("capability", ""))
		_expect(capability == String(entry.get("runtime_capability", "")) and not seen_capabilities.has(capability), "Runtime catalogue capability is missing, duplicated, or differs from the processed action: %s" % capability)
		seen_capabilities[capability] = true
		_expect(not clip.has("clip"), "Runtime catalogue carries a standalone-clip path field; runtime resolution is (library, identity): %s" % identity)
		_expect(String(clip.get("role", "")) == String(entry.get("runtime_role", entry.get("role", ""))) and clip.get("loop_intent") == bool(entry.get("is_loop", false)), "Runtime catalogue declared identity differs: %s" % identity)
		_expect(String(clip.get("boundary_kind", "")) == runtime_capability.boundary_kind(capability), "Runtime catalogue boundary kind is incompatible with capability: %s" % identity)
		_validate_provenance(clip, entry, String(package.get("published_index", package["index"])), String(package["index"]))
		_validate_runtime_metadata(clip, animation, bool(entry.get("is_loop", false)), identity)
		_validate_events_and_mappings(clip, expected, capability)

func _validate_provenance(clip: Dictionary, entry: Dictionary, published_index_path: String, source_index_path: String) -> void:
	_expect(not clip.has("processed_index") and not clip.has("metrics"), "Diagnostic provenance is misplaced")
	var provenance = clip.get("diagnostic_provenance_non_runtime_authoritative")
	_expect(provenance is Dictionary and (provenance as Dictionary).size() == 4, "Diagnostic provenance is missing or malformed")
	if provenance is Dictionary:
		var value := provenance as Dictionary; var published_metrics := published_index_path.get_base_dir().path_join(String(entry.get("metrics", ""))); var source_metrics := source_index_path.get_base_dir().path_join(String(entry.get("metrics", ""))); var metrics := _json(source_metrics)
		_expect(String(value.get("processed_index", "")) == published_index_path and String(value.get("metrics", "")) == published_metrics and String(value.get("source_action", "")) == String(entry.get("source_action", "")) and String(value.get("root_source", "")) == String(metrics.get("root_source", "")), "Diagnostic provenance differs from declared index")

func _validate_runtime_metadata(clip: Dictionary, animation: Animation, looping: bool, identity: String) -> void:
	_expect(animation != null and _finite(float(clip.get("duration_seconds", NAN))) and absf(float(clip.get("duration_seconds", NAN)) - animation.length) <= DURATION_TOLERANCE, "Runtime catalogue duration is invalid or differs from the embedded animation beyond %s s: %s" % [str(DURATION_TOLERANCE), identity])
	var transport_evidence = clip.get("transport", {}); if transport_evidence is Dictionary: transport_evidence = transport.from_json(transport_evidence, looping)
	_expect(transport_evidence is Dictionary and transport.validate(transport_evidence, looping).is_empty(), "Runtime catalogue transport is invalid: %s" % identity)
	if transport_evidence is Dictionary and animation != null:
		_expect(absf(float(transport_evidence.get("duration_seconds", NAN)) - animation.length) <= DURATION_TOLERANCE, "Runtime catalogue frame math disagrees with the embedded animation beyond %s s: %s" % [str(DURATION_TOLERANCE), identity])
	var response: Dictionary = clip.get("response", {}) if clip.get("response", {}) is Dictionary else {}
	var samples: Array = response.get("samples", [])
	_expect(samples.size() >= 2 and int(response.get("sample_count", -1)) == samples.size(), "Runtime response samples are invalid")
	if animation != null and not samples.is_empty():
		var last: Variant = samples[-1]; _expect(last is Dictionary and _finite_sample(last as Dictionary), "Runtime response contains non-finite sample")
		if last is Dictionary:
			var sample: Dictionary = last as Dictionary; var planar: Variant = sample.get("cumulative_local_planar", []); _expect(planar is Array and (planar as Array).size() == 2 and is_equal_approx(float(planar[0]), _runtime_planar(animation, animation.length)[0]) and is_equal_approx(float(planar[1]), _runtime_planar(animation, animation.length)[1]) and is_equal_approx(float(sample.get("yaw", NAN)), _runtime_yaw(animation, animation.length)), "Runtime response differs from animation tracks")

func _validate_events_and_mappings(clip: Dictionary, expected: Dictionary, capability: String) -> void:
	var events = clip.get("phase_events", {}); var mappings = clip.get("stitch_mappings", {}); var ids := {}
	var expected_applicability := "inapplicable" if runtime_capability.is_contact_free(capability) else "applicable"
	_expect(events is Dictionary and String((events as Dictionary).get("applicability", "")) == expected_applicability, "Event applicability is incompatible with capability: %s" % capability)
	if events is Dictionary and String((events as Dictionary).get("applicability", "")) == "applicable":
		for item in (events as Dictionary).get("events", []):
			if item is Dictionary:
				var id := String((item as Dictionary).get("id", "")); _expect(not id.is_empty() and not ids.has(id) and _finite(float((item as Dictionary).get("time_seconds", NAN))), "Declared event is invalid"); ids[id] = true
	else: _expect(events is Dictionary and String((events as Dictionary).get("applicability", "")) == "inapplicable", "Event applicability is invalid")
	if mappings is Dictionary and String((mappings as Dictionary).get("applicability", "")) == "applicable":
		for item in (mappings as Dictionary).get("mappings", []):
			if not item is Dictionary: _expect(false, "Declared mapping is invalid"); continue
			var from: Dictionary = (item as Dictionary).get("from", {}); var to: Dictionary = (item as Dictionary).get("to", {}); _expect(_has_declared_endpoint(expected, from) and _has_declared_endpoint(expected, to), "Declared mapping identity is dangling")
	else: _expect(mappings is Dictionary and String((mappings as Dictionary).get("applicability", "")) == "inapplicable", "Mapping applicability is invalid")

func _has_declared_endpoint(expected: Dictionary, endpoint: Dictionary) -> bool:
	var identity := String(endpoint.get("identity", ""))
	if not identity.is_empty(): return expected.has(identity)
	var role := String(endpoint.get("role", ""))
	if role.is_empty(): return false
	for candidate in expected:
		if String((expected[candidate] as Dictionary).get("role", "")) == role: return true
	return false

func _validate_animation(identity: String, animation: Animation, looping: bool) -> void:
	_expect(animation.resource_name == identity and animation.loop_mode == (Animation.LOOP_LINEAR if looping else Animation.LOOP_NONE), "Runtime animation identity or loop intent is invalid: %s" % identity)
	var position := animation.find_track(ROOT_POSITION, Animation.TYPE_POSITION_3D); var rotation := animation.find_track(ROOT_ROTATION, Animation.TYPE_ROTATION_3D)
	_expect(position >= 0 and rotation >= 0 and animation.track_get_key_count(position) > 0 and animation.track_get_key_count(rotation) > 0, "Runtime animation Root tracks are invalid: %s" % identity)
	if looping and position >= 0 and rotation >= 0: _expect(animation.track_get_interpolation_loop_wrap(position) and animation.track_get_interpolation_loop_wrap(rotation), "Looping Root tracks do not wrap: %s" % identity)

func _source_animation(library: AnimationLibrary, action: String) -> Animation:
	for candidate in [action, action.trim_suffix("-loop"), action.trim_suffix("_loop")]: if library.has_animation(candidate): return library.get_animation(candidate)
	return null
func _runtime_yaw(animation: Animation, time: float) -> float:
	var track := animation.find_track(ROOT_ROTATION, Animation.TYPE_ROTATION_3D); return (animation.rotation_track_interpolate(track, 0.0).inverse() * animation.rotation_track_interpolate(track, time)).get_euler().y
func _runtime_planar(animation: Animation, time: float) -> Array:
	var track := animation.find_track(ROOT_POSITION, Animation.TYPE_POSITION_3D); var value := animation.position_track_interpolate(track, time) - animation.position_track_interpolate(track, 0.0); return [-value.x, value.z]
func _finite_sample(sample: Dictionary) -> bool:
	for field in ["time_seconds", "yaw", "yaw_rate"]: if not _finite(float(sample.get(field, NAN))): return false
	for field in ["cumulative_local_planar", "planar_velocity"]:
		var pair = sample.get(field, []); if not pair is Array or (pair as Array).size() != 2 or not _finite(float(pair[0])) or not _finite(float(pair[1])): return false
	return true
func _finite(value: float) -> bool: return not is_nan(value) and not is_inf(value)
func _argument(prefix: String) -> String:
	for argument in OS.get_cmdline_user_args(): if argument.begins_with(prefix): return argument.trim_prefix(prefix)
	return ""
func _load_helper(filename: String) -> Script:
	var command_path: String = get_script().resource_path
	var helper_path: String = command_path.get_base_dir().get_base_dir().path_join("lib").path_join(filename).simplify_path()
	return load(helper_path) as Script
func _json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path): _expect(false, "JSON file is missing: %s" % path); return {}
	var value = JSON.parse_string(FileAccess.get_file_as_string(path)); if value is Dictionary: return value
	_expect(false, "JSON object is invalid: %s" % path); return {}
func _expect(condition: bool, message: String) -> void:
	if not condition: push_error(message); errors.append(message)
