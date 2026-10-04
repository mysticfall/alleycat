extends SceneTree

const ROOT_POSITION := ^"%GeneralSkeleton:Root"
const ROOT_ROTATION := ^"%GeneralSkeleton:Root"
const DURATION_TOLERANCE := 0.000001
const DEFAULT_CHARACTER := "female"
const DEFAULT_VARIANT := "sad"
var errors: Array[String] = []
var _coordinate_contract: Dictionary = {}
var transport: Script
var coordinate_contract: Script
var planted_intervals: Script
var descriptor_helper: Script
var stitch_graph_helper: Script
var runtime_capability: Script

func _init() -> void:
	transport = _load_helper("sample_transport.gd")
	coordinate_contract = _load_helper("coordinate_contract.gd")
	planted_intervals = _load_helper("planted_intervals.gd")
	descriptor_helper = _load_helper("runtime_package_descriptor.gd")
	stitch_graph_helper = _load_helper("stitch_graph.gd")
	runtime_capability = _load_helper("runtime_capability.gd")
	var descriptor := _descriptor()
	if not descriptor.is_empty(): _package(descriptor)
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
	var retained := index.get_base_dir().path_join("provenance").path_join(planted_intervals.FILENAME)
	var declaration := {"input": retained, "retained_input": retained, "storage": descriptor_helper.planted_interval_storage(name)}
	if declaration["storage"] == descriptor_helper.PACKAGE_FILE: declaration["runtime_path"] = planted_intervals.FILENAME
	return {
		"name": name, "character": character, "variant": variant, "index": index,
		"blend": set_directory.path_join("%s_%s.blend" % [name, variant]),
		"root": "res://assets/characters/reference/%s/animations/runtime/%s/%s" % [character, name, variant],
		"planted_intervals": declaration,
	}

func _package(package: Dictionary) -> void:
	var index := _json(String(package.get("index", "")))
	var actions: Array = index.get("actions", [])
	var name := String(package.get("name", index.get("package", "")))
	var variant := String(package.get("variant", index.get("variant", "")))
	if name.is_empty() or variant.is_empty() or String(package.get("character", "")).is_empty() or actions.is_empty():
		_fail("Package descriptor or processed index is incomplete: character, set, and variant identity are required")
		return
	package["name"] = name
	package["variant"] = variant
	var root := String(package.get("root", ""))
	if root.is_empty():
		_fail("Package descriptor has no runtime root")
		return
	if root.get_file() != variant or root.get_base_dir().get_file() != name:
		_fail("Runtime root must be the character-owned runtime/%s/%s package directory: %s" % [name, variant, root])
		return
	var intervals := {} if _library_only() else _planted_intervals(package, name)
	if intervals.has("error"):
		_fail(String(intervals["error"]))
		return
	_validate_coordinate_contract(index)
	_validate_import_configuration(package, actions)
	_reject_second_variant(package)
	if not errors.is_empty(): return
	var imported := _imported_library(String(package.get("blend", "")))
	if imported == null: return
	_coordinate_contract = index.get("coordinate_contract", {})
	var library := AnimationLibrary.new()
	var catalogue: Array = []
	for entry_value in actions:
		if not entry_value is Dictionary:
			_fail("Processed index contains an invalid action entry")
			continue
		var entry := entry_value as Dictionary
		if not _validate_event_provenance(entry):
			continue
		var action := String(entry.get("action", ""))
		var identity := String(entry.get("external_identity", ""))
		var source := _source_animation(imported, action)
		if action.is_empty() or identity.is_empty() or source == null:
			_fail("Imported source is missing declared action %s" % action)
			continue
		var metrics_path := String(package["index"]).get_base_dir().path_join(String(entry.get("metrics", "")))
		var metrics := _json(metrics_path)
		if String(metrics.get("action", "")) != action:
			_fail("Metrics action mismatch: %s" % identity)
			continue
		var runtime := _portable_copy(action, identity, source, bool(entry.get("is_loop", false)), entry.get("transport", {}))
		if runtime == null: continue
		if library.has_animation(identity):
			_fail("Processed index identity is duplicated: %s" % identity)
			continue
		if library.add_animation(identity, runtime) != OK:
			_fail("Could not add runtime animation: %s" % identity)
			continue
		if not _validate_frame_math(identity, runtime, entry.get("transport", {})):
			continue
		catalogue.append(_catalogue_entry(package, index, entry, runtime, metrics_path))
	if catalogue.size() != actions.size() or not errors.is_empty(): return
	var stitch_graph: Dictionary = stitch_graph_helper.resolve(catalogue, package.get("stitch_edges", []))
	if not bool(stitch_graph.get("ok", false)):
		_fail(String(stitch_graph.get("error", "Package stitch graph is invalid")))
		return
	var stitch_declarations := stitch_graph["declarations"] as Dictionary
	for entry in catalogue:
		(entry as Dictionary)["stitch_mappings"] = stitch_declarations[String((entry as Dictionary)["role"])]
	var document := {
		"schema_version": 4,
		"package": String(index.get("package", name)),
		"package_variant": String(index.get("variant", variant)),
		"animation_library": _library_filename(name, variant),
		"clip_count": catalogue.size(),
		"godot_response_coordinates": {"root": "+X right, +Y up, -Z forward", "actor_local_planar": "[right, forward] = [root.x, -root.z]", "yaw": "signed local-Y"},
		"clips": catalogue,
	}
	_stage_validate_publish(package, index, library, document, intervals)

func _library_filename(name: String, variant: String) -> String:
	return "%s_%s.tres" % [name, variant]

func _catalogue_entry(package: Dictionary, index: Dictionary, entry: Dictionary, animation: Animation, metrics_path: String) -> Dictionary:
	var identity := String(entry["external_identity"])
	var looping := bool(entry.get("is_loop", false))
	var role := String(entry.get("runtime_role", entry.get("role", "")))
	var capability := String(entry.get("runtime_capability", ""))
	if role.is_empty() or capability.is_empty():
		_fail("Processed action has no explicit runtime role/capability: %s" % entry.get("action", ""))
	var events := _phase_events(entry, animation, capability)
	var published_index := String(package.get("published_index", package["index"]))
	var published_metrics := published_index.get_base_dir().path_join(String(entry.get("metrics", "")))
	return {
		"identity": identity, "role": role,
		"capability": capability,
		"boundary_kind": runtime_capability.boundary_kind(capability), "loop_intent": looping, "transport": entry.get("transport", {}),
		"diagnostic_provenance_non_runtime_authoritative": {
			"root_source": String(_json(metrics_path).get("root_source", "")), "processed_index": published_index,
			"metrics": published_metrics, "source_action": entry.get("source_action", ""),
		},
		"duration_seconds": animation.length,
		"endpoint_root_displacement_godot": _root_array(_vector(animation, animation.length)),
		"endpoint_local_y_yaw": _yaw(animation, animation.length),
		"response": _response(animation), "phase_events": events,
	}

func _phase_events(entry: Dictionary, animation: Animation, capability: String) -> Dictionary:
	if runtime_capability.is_idle(capability): return {"applicability": "inapplicable"}
	var evidence = entry.get("post_repair_event_evidence", {})
	if not evidence is Dictionary or String((evidence as Dictionary).get("contract", "")) != "anim002_post_repair_directional_phase_events_v2": return {"applicability": "inapplicable"}
	var timeline: Dictionary = (evidence as Dictionary).get("timeline", {})
	var fps := float(timeline.get("fps", 0.0))
	if fps <= 0.0: return {"applicability": "inapplicable"}
	var events: Array = []
	for item in (evidence as Dictionary).get("events", []):
		if item is Dictionary:
			var event := (item as Dictionary).duplicate(true)
			event["time_seconds"] = (float(event.get("output_frame", 0.0)) - float(timeline.get("frame_start", 0.0))) / fps
			events.append(event)
	return {"applicability": "applicable", "contract": evidence.get("contract"), "evaluation_stage": evidence.get("evaluation_stage"), "sample_semantics": evidence.get("sample_semantics"), "timeline": timeline, "events": events, "duration_seconds": animation.length}


func _validate_event_provenance(entry: Dictionary) -> bool:
	var capability_error: String = runtime_capability.validate_processed_action(entry)
	if not capability_error.is_empty():
		_fail("%s: %s" % [capability_error, entry.get("action", "")]); return false
	var capability := String(entry["runtime_capability"])
	if not runtime_capability.requires_directional_event_provenance(capability): return true
	var source = entry.get("source_directional_event_evidence")
	var derivation = entry.get("event_derivation_evidence")
	var output = entry.get("post_repair_event_evidence")
	if not source is Dictionary or String((source as Dictionary).get("contract", "")) != "anim002_source_directional_phase_events_v1":
		_fail("Processed action has stale source directional event evidence: %s" % entry.get("action", "")); return false
	if not derivation is Dictionary or String((derivation as Dictionary).get("contract", "")) != "anim002_event_derivation_v1":
		_fail("Processed action has stale event derivation evidence: %s" % entry.get("action", "")); return false
	if not output is Dictionary or String((output as Dictionary).get("contract", "")) != "anim002_post_repair_directional_phase_events_v2":
		_fail("Processed action has stale post-repair physical event evidence: %s" % entry.get("action", "")); return false
	if String((output as Dictionary).get("evaluation_stage", "")) != "final_repaired_evaluated_hierarchy" or not (output as Dictionary).get("refusals", []).is_empty():
		_fail("Processed action has refused or pre-repair physical event evidence: %s" % entry.get("action", "")); return false
	if runtime_capability.requires_loop_repair(capability):
		var repair = entry.get("loop_repair_evidence")
		if not repair is Dictionary or String((repair as Dictionary).get("contract", "")) != "anim002_selected_loop_repair_provenance_v2" or String((repair as Dictionary).get("blend_profile", "")).is_empty():
			_fail("Processed action has stale selected loop-repair provenance: %s" % entry.get("action", "")); return false
	return true

func _response(animation: Animation) -> Dictionary:
	var count := 3
	var samples: Array = []
	for i in count:
		var time := animation.length * float(i) / float(count - 1)
		samples.append({"time_seconds": time, "cumulative_local_planar": _planar(_vector(animation, time)), "yaw": _yaw(animation, time), "planar_velocity": _planar(_velocity(animation, time)), "yaw_rate": _yaw_rate(animation, time)})
	return {"sample_count": count, "samples": samples}

func _portable_copy(action: String, identity: String, source: Animation, looping: bool, evidence: Variant) -> Animation:
	if not evidence is Dictionary: _fail("Missing transport evidence: %s" % action); return null
	var transport_evidence: Dictionary = transport.from_json(evidence as Dictionary, looping)
	var transport_error: String = transport.validate(transport_evidence, looping)
	if not transport_error.is_empty(): _fail("Invalid transport evidence for %s: %s" % [action, transport_error]); return null
	var output := Animation.new()
	output.length = source.length; output.loop_mode = source.loop_mode; output.step = source.step; output.resource_name = identity
	for track in source.get_track_count():
		var path := String(source.track_get_path(track)); var colon := path.rfind(":")
		if colon <= 0: _fail("Nonportable track without skeleton path: %s" % path); return null
		source.copy_track(track, output)
		output.track_set_path(output.get_track_count() - 1, NodePath("%GeneralSkeleton" + path.substr(colon)))
	var normalise_error: String = transport.normalise_imported(output, transport_evidence, looping)
	if not normalise_error.is_empty(): _fail("Transport normalisation failed for %s: %s" % [action, normalise_error]); return null
	if output.find_track(ROOT_POSITION, Animation.TYPE_POSITION_3D) < 0 or output.find_track(ROOT_ROTATION, Animation.TYPE_ROTATION_3D) < 0:
		_fail("Runtime animation has no Root position/rotation tracks: %s" % action)
		return null
	return output

func _validate_frame_math(identity: String, animation: Animation, evidence: Variant) -> bool:
	# The processed index transport (frames at the declared fps) is the single
	# source of truth; the embedded animation must already conform to it exactly.
	if not evidence is Dictionary:
		_fail("Missing transport frame math: %s" % identity)
		return false
	var transport_evidence: Dictionary = transport.from_json(evidence as Dictionary, animation.loop_mode == Animation.LOOP_LINEAR)
	var transport_error: String = transport.validate(transport_evidence, animation.loop_mode == Animation.LOOP_LINEAR)
	if not transport_error.is_empty():
		_fail("Invalid transport frame math for %s: %s" % [identity, transport_error])
		return false
	var expected := float(transport_evidence["duration_seconds"])
	if absf(animation.length - expected) > DURATION_TOLERANCE:
		_fail("Embedded animation duration disagrees with the processed-index frame math by more than %s s: %s" % [str(DURATION_TOLERANCE), identity])
		return false
	return true

func _validate_coordinate_contract(index: Dictionary) -> void:
	if int(index.get("schema_version", 0)) != 2 or int(index.get("metrics_schema_version", 0)) != 2:
		_fail("Processed index schema is stale")
		return
	var error: String = coordinate_contract.validate_evidence(index.get("coordinate_contract"))
	if not error.is_empty(): _fail("Invalid coordinate contract: %s" % error)

func _validate_import_configuration(package: Dictionary, actions: Array) -> void:
	var config := ConfigFile.new(); var sidecar := String(package.get("blend", "")) + ".import"
	if config.load(sidecar) != OK: _fail("Could not load import sidecar: %s" % sidecar); return
	var subresources: Dictionary = config.get_value("params", "_subresources", {})
	var nodes: Dictionary = subresources.get("nodes", {})
	var skeleton_path := String(package.get("skeleton_import_path", "PATH:Female/Skeleton3D"))
	var settings = nodes.get(skeleton_path)
	if not settings is Dictionary or (settings as Dictionary).get("retarget/rest_fixer/normalize_position_tracks", false) != true:
		_fail("Imported sidecar has disabled position-track normalisation: %s" % sidecar)
	for key in config.get_section_keys("params"):
		if String(key).ends_with("save_to_file/enabled") and config.get_value("params", key) == true:
			_fail("Import configuration externalises animations globally; embedded-only packages require save_to_file disabled: %s" % sidecar)
	var animations: Dictionary = subresources.get("animations", {})
	if animations.size() != actions.size(): _fail("Imported sidecar action set is invalid: %s" % sidecar)
	for action in animations.keys():
		var block = animations[action]
		if not block is Dictionary:
			_fail("Imported sidecar has a malformed action block: %s" % action)
			continue
		for setting in (block as Dictionary).keys():
			if String(setting).ends_with("save_to_file/enabled") and (block as Dictionary)[setting] == true:
				_fail("Import configuration externalises animation %s; embedded-only packages require save_to_file disabled: %s" % [action, sidecar])

func _reject_second_variant(package: Dictionary) -> void:
	# Multi-variant publication is not ratified: refuse before staging when the
	# character/set pair already owns a different variant's artefacts.
	var variant := String(package["variant"])
	var runtime_set := String(package["root"]).get_base_dir()
	var runtime_directory := DirAccess.open(runtime_set)
	if runtime_directory != null:
		for child in runtime_directory.get_directories():
			if child != variant and _contains_file(runtime_set.path_join(child), "_catalogue.json"):
				_fail("Publishing variant '%s' would alias variant '%s'; multi-variant publication is not ratified: %s" % [variant, child, runtime_set])
	var processed_set := String(package["index"]).get_base_dir().get_base_dir()
	var processed_directory := DirAccess.open(processed_set)
	if processed_directory != null:
		for child in processed_directory.get_directories():
			if child != variant and FileAccess.file_exists(processed_set.path_join(child).path_join("processed-index.json")):
				_fail("Publishing variant '%s' would alias variant '%s' metadata; multi-variant publication is not ratified: %s" % [variant, child, processed_set])

func _contains_file(directory: String, suffix: String) -> bool:
	var handle := DirAccess.open(directory)
	if handle == null: return false
	for file in handle.get_files():
		if file.ends_with(suffix): return true
	return false

func _imported_library(path: String) -> AnimationLibrary:
	var scene := ResourceLoader.load(path, "PackedScene", ResourceLoader.CACHE_MODE_REPLACE) as PackedScene
	if scene == null: _fail("Processed blend was not imported: %s" % path); return null
	var root := scene.instantiate(); var players := root.find_children("*", "AnimationPlayer", true, false)
	if players.size() != 1: _fail("Imported blend must have exactly one AnimationPlayer: %s" % path); root.free(); return null
	var player := players[0] as AnimationPlayer; var names := player.get_animation_library_list()
	if names.size() != 1: _fail("Imported blend must have one animation library: %s" % path); root.free(); return null
	var skeletons := root.find_children("*", "Skeleton3D", true, false)
	if skeletons.size() != 1: _fail("Imported blend must have exactly one Skeleton3D: %s" % path); root.free(); return null
	var library := player.get_animation_library(names[0]); root.free(); return library

func _source_animation(library: AnimationLibrary, action: String) -> Animation:
	for candidate in [action, action.trim_suffix("-loop"), action.trim_suffix("_loop")]:
		if library.has_animation(candidate): return library.get_animation(candidate)
	return null

func _stage_validate_publish(package: Dictionary, index: Dictionary, library: AnimationLibrary, document: Dictionary, intervals: Dictionary) -> void:
	var root := String(package["root"])
	var stage := _stage_root(root, String(package["name"]), String(package["variant"]))
	if stage.is_empty(): return
	var library_filename := _library_filename(String(package["name"]), String(package["variant"]))
	_remove_tree(stage)
	if DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(stage)) != OK: _fail("Could not create package staging directory"); return
	var staged_library_path := stage.path_join(library_filename)
	var library_source := _argument("--runtime-library-source=")
	if "--preserve-runtime-library" in OS.get_cmdline_user_args() and library_source.is_empty():
		library_source = root.path_join(library_filename)
	if not library_source.is_empty():
		# Byte-preserving reuse keeps planted intervals derived from this library bound to it.
		if library_source.get_file() != library_filename or not FileAccess.file_exists(library_source) \
			or DirAccess.copy_absolute(
				ProjectSettings.globalize_path(library_source),
				ProjectSettings.globalize_path(staged_library_path)) != OK:
			_fail("Could not preserve the animation library: %s" % library_source); _remove_tree(stage); return
	elif ResourceSaver.save(library, staged_library_path) != OK:
		_fail("Could not stage animation library"); _remove_tree(stage); return
	var library_uid := root.path_join("%s.uid" % library_filename)
	if FileAccess.file_exists(library_uid):
		var staged_uid := stage.path_join("%s.uid" % library_filename)
		if DirAccess.copy_absolute(ProjectSettings.globalize_path(library_uid), ProjectSettings.globalize_path(staged_uid)) != OK:
			_fail("Could not preserve animation-library UID"); _remove_tree(stage); return
	if _library_only():
		print("ANIM002_RUNTIME_LIBRARY_STAGED=" + JSON.stringify({"package": package["name"], "variant": package["variant"], "library": staged_library_path}))
		return
	var interval_error := _validate_planted_intervals(package, intervals["document"], document, staged_library_path)
	if not interval_error.is_empty(): _fail(interval_error); _remove_tree(stage); return
	if String(intervals["storage"]) == descriptor_helper.CATALOGUE_FIELD:
		document[planted_intervals.CATALOGUE_FIELD] = intervals["document"]
	if not _write_json(stage.path_join("%s_%s_catalogue.json" % [package["name"], package["variant"]]), document): _remove_tree(stage); return
	if String(intervals["storage"]) == descriptor_helper.PACKAGE_FILE:
		var interval_file := FileAccess.open(stage.path_join(String(intervals["runtime_path"])), FileAccess.WRITE)
		if interval_file == null: _fail("Could not stage planted intervals"); _remove_tree(stage); return
		interval_file.store_string(String(intervals["text"]))
		interval_file.close()
	if "--inject-staged-validation-failure" in OS.get_cmdline_user_args():
		var corrupt := FileAccess.open(stage.path_join(library_filename), FileAccess.WRITE)
		if corrupt != null: corrupt.store_string("corrupt staged fixture")
	if not _validate_stage(package, index, stage, library_filename, document, intervals): _remove_tree(stage); return
	print("ANIM002_RUNTIME_STAGE_PREPARED=" + JSON.stringify({"package": package["name"], "variant": package["variant"], "stage": stage}))

func _stage_root(root: String, package_name: String, variant: String) -> String:
	var requested := ""
	var supplied := 0
	for argument in OS.get_cmdline_user_args():
		if argument.begins_with("--stage-root="):
			requested = argument.trim_prefix("--stage-root=")
			supplied += 1
	if supplied != 1 or requested.is_empty():
		_fail("Runtime staging root must be explicitly supplied with --stage-root")
		return ""
	var stage := requested.simplify_path().trim_suffix("/")
	if not stage.begins_with("res://temp/"):
		_fail("Runtime staging root must resolve beneath res://temp")
		return ""
	if stage.get_file() != variant or stage.get_base_dir().get_file() != package_name or stage.get_base_dir().get_base_dir().get_file() != "runtime":
		_fail("Runtime staging root must end in runtime/%s/%s" % [package_name, variant])
		return ""
	if stage == root:
		_fail("Runtime staging root must differ from the production root")
		return ""
	if _has_symbolic_link_component(stage):
		_fail("Runtime staging root must not traverse symbolic links")
		return ""
	return stage

func _has_symbolic_link_component(path: String) -> bool:
	var project_directory := DirAccess.open(ProjectSettings.globalize_path("res://"))
	if project_directory == null: return true
	var current := "res://"
	for component in path.trim_prefix("res://").split("/", false):
		current = current.path_join(component)
		if project_directory.is_link(ProjectSettings.globalize_path(current)): return true
	return false

func _validate_stage(package: Dictionary, index: Dictionary, stage: String, library_filename: String, document: Dictionary, intervals: Dictionary) -> bool:
	var before := errors.size()
	if DirAccess.dir_exists_absolute(ProjectSettings.globalize_path(stage.path_join("clips"))):
		_fail("Staged package must not contain a standalone clips directory")
	var staged_directory := DirAccess.open(stage)
	if staged_directory != null:
		for file in staged_directory.get_files():
			if file.ends_with(".res"):
				_fail("Staged package must not contain standalone clip resources: %s" % file)
	var library := ResourceLoader.load(stage.path_join(library_filename), "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary
	if library == null:
		_fail("Staged animation library failed strict reload")
	else:
		var actions: Array = index.get("actions", [])
		var frame_math: Dictionary = _index_frame_math(actions)
		var identities := {}
		for value in document.get("clips", []):
			if value is Dictionary: identities[String((value as Dictionary).get("identity", ""))] = value
		if library.get_animation_list().size() != identities.size():
			_fail("Staged library identity count is invalid")
		for identity in library.get_animation_list():
			var animation := library.get_animation(String(identity))
			var entry: Variant = identities.get(String(identity))
			if not entry is Dictionary:
				_fail("Staged library carries an undeclared identity: %s" % identity)
				continue
			var clip := entry as Dictionary
			if absf(float(clip.get("duration_seconds", NAN)) - animation.length) > DURATION_TOLERANCE:
				_fail("Staged catalogue duration disagrees with the embedded animation: %s" % identity)
			var declared: Variant = frame_math.get(String(identity))
			if declared is float and absf(animation.length - float(declared)) > DURATION_TOLERANCE:
				_fail("Staged library duration disagrees with the processed-index frame math: %s" % identity)
			if animation.loop_mode != (Animation.LOOP_LINEAR if bool(clip.get("loop_intent", false)) else Animation.LOOP_NONE):
				_fail("Staged library loop intent disagrees with the catalogue: %s" % identity)
	var catalogue := _json(stage.path_join("%s_%s_catalogue.json" % [package["name"], package["variant"]]))
	if catalogue != JSON.parse_string(JSON.stringify(document)): _fail("Staged catalogue differs after reload")
	if int(catalogue.get("schema_version", 0)) != 4: _fail("Staged catalogue schema is invalid")
	if String(catalogue.get("animation_library", "")) != library_filename: _fail("Staged catalogue library link is invalid")
	for value in catalogue.get("clips", []):
		if value is Dictionary and (value as Dictionary).has("clip"):
			_fail("Staged catalogue carries a standalone-clip path field: %s" % (value as Dictionary).get("identity", ""))
	var staged_intervals: Variant = catalogue.get(planted_intervals.CATALOGUE_FIELD)
	if String(intervals["storage"]) == descriptor_helper.CATALOGUE_FIELD and staged_intervals != intervals["document"]:
		_fail("Staged idle catalogue planted intervals differ from the declared input")
	if String(intervals["storage"]) == descriptor_helper.PACKAGE_FILE:
		var staged_path := stage.path_join(String(intervals["runtime_path"]))
		if catalogue.has(planted_intervals.CATALOGUE_FIELD): _fail("Staged locomotion catalogue must not embed planted intervals")
		if not FileAccess.file_exists(staged_path) or FileAccess.get_file_as_string(staged_path) != String(intervals["text"]):
			_fail("Staged planted intervals differ from the declared input")
		staged_intervals = JSON.parse_string(FileAccess.get_file_as_string(staged_path)) if FileAccess.file_exists(staged_path) else null
	var staged_error := _validate_planted_intervals(package, staged_intervals, catalogue, stage.path_join(library_filename))
	if not staged_error.is_empty(): _fail("Staged " + staged_error.substr(0, 1).to_lower() + staged_error.substr(1))
	return errors.size() == before

func _library_only() -> bool:
	return "--stage-library-only" in OS.get_cmdline_user_args()

func _planted_intervals(package: Dictionary, name: String) -> Dictionary:
	var declaration: Variant = package.get("planted_intervals")
	var storage: String = descriptor_helper.planted_interval_storage(name)
	if not declaration is Dictionary:
		return {"error": "Package descriptor requires an explicit planted-interval declaration"}
	var value := declaration as Dictionary
	var input := String(value.get("input", ""))
	var retained := String(value.get("retained_input", ""))
	var declared_storage := String(value.get("storage", ""))
	if declared_storage not in [descriptor_helper.PACKAGE_FILE, descriptor_helper.CATALOGUE_FIELD] or (not storage.is_empty() and declared_storage != storage):
		return {"error": "Planted-interval storage %s is invalid for the %s package" % [declared_storage, name]}
	storage = declared_storage
	if input.is_empty() or retained.is_empty() or retained.begins_with("res://temp/") or retained.begins_with("res://tests/"):
		return {"error": "Planted-interval declaration requires an input and a durable retained_input"}
	if input.get_file() != planted_intervals.FILENAME or retained.get_file() != planted_intervals.FILENAME:
		return {"error": "Planted-interval input must be %s" % planted_intervals.FILENAME}
	if storage == descriptor_helper.PACKAGE_FILE and String(value.get("runtime_path", "")) != planted_intervals.FILENAME:
		return {"error": "Planted-interval runtime_path must be %s" % planted_intervals.FILENAME}
	if not FileAccess.file_exists(input):
		return {"error": "Planted-interval input is missing: %s" % input}
	var text := FileAccess.get_file_as_string(input)
	var parsed: Variant = JSON.parse_string(text)
	if not parsed is Dictionary:
		return {"error": "Planted-interval input is not a JSON object: %s" % input}
	if storage == descriptor_helper.CATALOGUE_FIELD and JSON.parse_string(JSON.stringify(parsed)) != parsed:
		# The catalogue re-serialises the embedded intervals, so they must survive a Godot JSON round trip unchanged.
		return {"error": "Planted-interval input does not survive a Godot JSON round trip; re-derive it: %s" % input}
	return {"input": input, "retained_input": retained, "storage": storage, "runtime_path": String(value.get("runtime_path", "")), "text": text, "document": parsed}

func _validate_planted_intervals(package: Dictionary, value: Variant, catalogue: Dictionary, library_path: String) -> String:
	var policy: Dictionary = planted_intervals.load_policy(_tool_root().path_join("config").path_join(planted_intervals.POLICY_FILENAME))
	if policy.has("error"): return String(policy["error"])
	return planted_intervals.validate_document(value, {
		"character": String(package["character"]), "package": String(package["name"]), "variant": String(package["variant"]),
		"clips": planted_intervals.catalogue_expectations(catalogue), "policy_sha256": String(policy["sha256"]),
		"runtime_library_sha256": descriptor_helper.runtime_library_sha256(library_path),
	})

func _tool_root() -> String:
	return String(get_script().resource_path).get_base_dir().get_base_dir().get_base_dir()

func _index_frame_math(actions: Array) -> Dictionary:
	var durations := {}
	for value in actions:
		if not value is Dictionary: continue
		var entry := value as Dictionary
		var identity := String(entry.get("external_identity", ""))
		var evidence = entry.get("transport", {})
		if identity.is_empty() or not evidence is Dictionary: continue
		durations[identity] = float((evidence as Dictionary).get("duration_seconds", NAN))
	return durations

func _vector(animation: Animation, time: float) -> Vector3:
	var track := animation.find_track(ROOT_POSITION, Animation.TYPE_POSITION_3D); return animation.position_track_interpolate(track, time) - animation.position_track_interpolate(track, 0.0)
func _root_array(value: Vector3) -> Array: return [-value.x, value.y, -value.z]
func _planar(value: Vector3) -> Array: return [-value.x, value.z]
func _yaw(animation: Animation, time: float) -> float:
	var track := animation.find_track(ROOT_ROTATION, Animation.TYPE_ROTATION_3D); return (animation.rotation_track_interpolate(track, 0.0).inverse() * animation.rotation_track_interpolate(track, time)).get_euler().y
func _velocity(animation: Animation, time: float) -> Vector3:
	var delta := minf(1.0 / 60.0, animation.length / 16.0); var left := maxf(0.0, time - delta); var right := minf(animation.length, time + delta); return (_vector(animation, right) - _vector(animation, left)) / maxf(right - left, 0.0001)
func _yaw_rate(animation: Animation, time: float) -> float:
	var delta := minf(1.0 / 60.0, animation.length / 16.0); var left := maxf(0.0, time - delta); var right := minf(animation.length, time + delta); return (_yaw(animation, right) - _yaw(animation, left)) / maxf(right - left, 0.0001)
func _argument(prefix: String) -> String:
	for argument in OS.get_cmdline_user_args(): if argument.begins_with(prefix): return argument.trim_prefix(prefix)
	return ""
func _load_helper(filename: String) -> Script:
	var command_path: String = get_script().resource_path
	var helper_path: String = command_path.get_base_dir().get_base_dir().path_join("lib").path_join(filename).simplify_path()
	return load(helper_path) as Script
func _json(path: String) -> Dictionary:
	if path.is_empty() or not FileAccess.file_exists(path): _fail("JSON file is missing: %s" % path); return {}
	var value = JSON.parse_string(FileAccess.get_file_as_string(path)); if value is Dictionary: return value
	_fail("JSON object is invalid: %s" % path); return {}
func _write_json(path: String, value: Dictionary) -> bool:
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(path.get_base_dir())); var file := FileAccess.open(path, FileAccess.WRITE)
	if file == null: _fail("Could not write JSON: %s" % path); return false
	file.store_string(JSON.stringify(value, "  ") + "\n"); return true
func _remove_tree(path: String) -> void:
	var absolute := ProjectSettings.globalize_path(path); if not DirAccess.dir_exists_absolute(absolute): return
	var directory := DirAccess.open(absolute); if directory == null: return
	for file in directory.get_files(): directory.remove(file)
	for child in directory.get_directories(): _remove_tree(path.path_join(child))
	DirAccess.remove_absolute(absolute)
func _fail(message: String) -> void:
	push_error(message); errors.append(message)
