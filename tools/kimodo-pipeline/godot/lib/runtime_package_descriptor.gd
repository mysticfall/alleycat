class_name RuntimePackageDescriptor
extends RefCounted

const SCHEMA_VERSION := 3
const Intervals := preload("planted_intervals.gd")
const COMMON_BINDING_KEYS := [
	"processed_index_sha256", "retargeted_blend_sha256", "runtime_catalogue_sha256",
	"runtime_library_sha256", "retained_planted_intervals_sha256",
]
const PACKAGE_FILE := "package_file"
const CATALOGUE_FIELD := "catalogue_field"


## Planted-interval storage owned by each package: locomotion publishes a package file, idle embeds the intervals
## in its catalogue's `per_foot_planted_intervals` field.
static func planted_interval_storage(name: String) -> String:
	if name == "locomotion":
		return PACKAGE_FILE
	if name == "idle":
		return CATALOGUE_FIELD
	return ""


static func validate(descriptor: Dictionary) -> String:
	if int(descriptor.get("schema_version", 0)) != SCHEMA_VERSION or not _has_exact_keys(descriptor, [
		"schema_version", "character", "name", "variant", "index", "blend", "root",
		"planted_intervals", "stitch_edges", "binding",
	]):
		return "Production package descriptor schema is invalid (expected character/set/variant identity schema v%d)" % SCHEMA_VERSION
	var character := String(descriptor.get("character", ""))
	var name := String(descriptor.get("name", ""))
	var variant := String(descriptor.get("variant", ""))
	var index := String(descriptor.get("index", ""))
	var blend := String(descriptor.get("blend", ""))
	var root := String(descriptor.get("root", ""))
	for declaration in [[index, "index"], [blend, "blend"], [root, "root"]]:
		if not _durable_resource_path(String(declaration[0])):
			return "Production package descriptor %s path is not durable" % declaration[1]
	if character.is_empty() or name.is_empty() or variant.is_empty():
		return "Production package descriptor has no character/set/variant identity"
	var layout_error := _validate_package_layout(name, variant, index, root, blend)
	if not layout_error.is_empty():
		return layout_error
	var storage := planted_interval_storage(name)
	var intervals = descriptor.get("planted_intervals")
	var interval_keys := ["retained_input", "storage", "runtime_path"] if storage == PACKAGE_FILE else ["retained_input", "storage"]
	if storage.is_empty() or not intervals is Dictionary or not _has_exact_keys(intervals as Dictionary, interval_keys) \
			or String((intervals as Dictionary).get("storage", "")) != storage:
		return "Production package planted-interval declaration is invalid"
	var retained := String((intervals as Dictionary).get("retained_input", ""))
	if not _durable_resource_path(retained) or retained != index.get_base_dir().path_join("provenance").path_join(Intervals.FILENAME):
		return "Production package planted intervals are not retained in the variant provenance directory"
	var paths := {
		"processed_index_sha256": index,
		"retargeted_blend_sha256": blend,
		"runtime_catalogue_sha256": root.path_join("%s_%s_catalogue.json" % [name, variant]),
		"runtime_library_sha256": root.path_join("%s_%s.tres" % [name, variant]),
		"retained_planted_intervals_sha256": retained,
	}
	if storage == PACKAGE_FILE:
		var runtime_path := String((intervals as Dictionary).get("runtime_path", ""))
		if runtime_path != Intervals.FILENAME:
			return "Production package planted-interval runtime path is invalid"
		paths["runtime_planted_intervals_sha256"] = root.path_join(runtime_path)
	var binding = descriptor.get("binding")
	if not binding is Dictionary or not _has_exact_keys(binding as Dictionary, paths.keys()):
		return "Production package descriptor hash binding is incomplete"
	for key in paths:
		var path := String(paths[key])
		if not FileAccess.file_exists(path):
			return "Production package descriptor hash mismatch: %s" % key
		var actual_hash := runtime_library_sha256(path) if key == "runtime_library_sha256" else FileAccess.get_sha256(path)
		if String((binding as Dictionary).get(key, "")) != actual_hash:
			return "Production package descriptor hash mismatch: %s" % key
	var retained_intervals := _json(retained)
	var catalogue := _json(String(paths["runtime_catalogue_sha256"]))
	if int(retained_intervals.get("schema_version", 0)) != Intervals.SCHEMA_VERSION:
		return "Production planted intervals declare an unknown or mismatched schema version"
	var derivation = retained_intervals.get("derivation")
	if not derivation is Dictionary or String((derivation as Dictionary).get("runtime_library_sha256", "")) != String((binding as Dictionary)["runtime_library_sha256"]):
		return "Production planted intervals were derived from a different runtime library"
	if storage == PACKAGE_FILE:
		if catalogue.has(Intervals.CATALOGUE_FIELD):
			return "A locomotion catalogue must not embed planted intervals"
		if FileAccess.get_file_as_string(retained) != FileAccess.get_file_as_string(String(paths["runtime_planted_intervals_sha256"])):
			return "Production retained/runtime planted intervals disagree"
	elif catalogue.get(Intervals.CATALOGUE_FIELD) != retained_intervals:
		return "Production idle catalogue planted intervals disagree with the retained intervals"
	return ""


static func runtime_library_sha256(path: String) -> String:
	var context := HashingContext.new()
	if context.start(HashingContext.HASH_SHA256) != OK:
		return ""
	if context.update(canonical_runtime_library_bytes(FileAccess.get_file_as_bytes(path))) != OK:
		return ""
	return context.finish().hex_encode()


# Removes only a valid, non-empty UID attribute from a valid top-level resource header. The header (the first line,
# without its line ending) must be strict UTF-8 without NUL bytes; any other header leaves the content unchanged. The
# Python and C# implementations apply the same rule, checked against the shared
# `tests/fixtures/runtime_library_canonicalisation_vectors.json` vectors.
static func canonical_runtime_library_bytes(content: PackedByteArray) -> PackedByteArray:
	var header_end := content.size()
	for index in content.size():
		if content[index] == 10:
			header_end = index
			break
	var syntax_end := header_end - 1 if header_end > 0 and content[header_end - 1] == 13 else header_end
	var header_bytes := content.slice(0, syntax_end)
	if header_bytes.has(0):
		return content
	var header := header_bytes.get_string_from_utf8()
	if header.to_utf8_buffer() != header_bytes:
		return content
	var header_pattern := RegEx.new()
	if header_pattern.compile('^\\[gd_resource(?: [A-Za-z_][A-Za-z0-9_]*=(?:"[^"\\r\\n]*"|[^ "\\[\\]\\r\\n=]+))*\\]$') != OK \
			or header_pattern.search(header) == null:
		return content
	var uid_pattern := RegEx.new()
	if uid_pattern.compile(' uid="uid://[^"\\r\\n]+"') != OK:
		return content
	var matches := uid_pattern.search_all(header)
	if matches.size() != 1:
		return content
	var uid := matches[0] as RegExMatch
	var annotation_start := header.substr(0, uid.get_start()).to_utf8_buffer().size()
	var annotation_end := header.substr(0, uid.get_end()).to_utf8_buffer().size()
	var canonical := content.slice(0, annotation_start)
	canonical.append_array(content.slice(annotation_end))
	return canonical


static func _validate_package_layout(name: String, variant: String, index: String, root: String, blend: String) -> String:
	if root.get_file() != variant or root.get_base_dir().get_file() != name:
		return "Production package descriptor root is not the character-owned runtime/<set>/<variant> package root"
	var set_directory := index.get_base_dir().get_base_dir()
	if index.get_base_dir().get_file() != variant:
		return "Production package descriptor index is not variant-scoped under processed/<set>/<variant>"
	if blend != set_directory.path_join("%s_%s.blend" % [name, variant]):
		return "Production package descriptor blend is not the set-level <set>_<variant>.blend authoring carrier"
	return ""


static func _durable_resource_path(path: String) -> bool:
	return path.begins_with("res://") and not path.begins_with("res://tests/") and not path.begins_with("res://temp/") and not path.contains("..")


static func _has_exact_keys(value: Dictionary, expected: Array) -> bool:
	var actual: Array[String] = []
	for key in value.keys(): actual.append(String(key))
	actual.sort()
	var sorted_expected: Array = expected.duplicate()
	sorted_expected.sort()
	return actual == sorted_expected


static func _json(path: String) -> Dictionary:
	if not FileAccess.file_exists(path): return {}
	var value = JSON.parse_string(FileAccess.get_file_as_string(path))
	return value as Dictionary if value is Dictionary else {}
