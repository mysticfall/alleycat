class_name RuntimePackageDescriptor
extends RefCounted

const SCHEMA_VERSION := 2
const CONTACT_EVIDENCED_BINDING_KEYS := [
	"processed_index_sha256", "retargeted_blend_sha256", "runtime_catalogue_sha256",
	"runtime_library_sha256", "retained_contact_evidence_sha256", "runtime_contact_evidence_sha256",
]


static func validate(descriptor: Dictionary) -> String:
	if int(descriptor.get("schema_version", 0)) != SCHEMA_VERSION or not _has_exact_keys(descriptor, [
		"schema_version", "character", "name", "variant", "index", "blend", "root",
		"contact_evidence_required", "contact_evidence", "stitch_edges", "binding",
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
	if not bool(descriptor.get("contact_evidence_required", false)):
		return _validate_contact_free_idle(descriptor, name, variant, index, blend, root)
	var contact = descriptor.get("contact_evidence")
	if not contact is Dictionary or not _has_exact_keys(contact as Dictionary, [
		"input", "retained_input", "runtime_path", "package_identity", "generation",
	]):
		return "Production package contact-evidence declaration is invalid"
	var contact_value := contact as Dictionary
	var retained := String(contact_value.get("retained_input", ""))
	var input := String(contact_value.get("input", ""))
	var runtime_path := String(contact_value.get("runtime_path", ""))
	if input != retained or not _durable_resource_path(retained) or runtime_path.is_empty() or runtime_path.is_absolute_path() or runtime_path.contains(".."):
		return "Production package contact-evidence path is invalid or not retained"
	var paths := {
		"processed_index_sha256": index,
		"retargeted_blend_sha256": blend,
		"runtime_catalogue_sha256": root.path_join("%s_%s_catalogue.json" % [name, variant]),
		"runtime_library_sha256": root.path_join("%s_%s.tres" % [name, variant]),
		"retained_contact_evidence_sha256": retained,
		"runtime_contact_evidence_sha256": root.path_join(runtime_path),
	}
	var binding = descriptor.get("binding")
	if not binding is Dictionary or not _has_exact_keys(binding as Dictionary, CONTACT_EVIDENCED_BINDING_KEYS):
		return "Production package descriptor hash binding is incomplete"
	for key in CONTACT_EVIDENCED_BINDING_KEYS:
		var path := String(paths[key])
		if not FileAccess.file_exists(path) or String((binding as Dictionary).get(key, "")) != FileAccess.get_sha256(path):
			return "Production package descriptor hash mismatch: %s" % key
	var retained_evidence := _json(retained)
	var runtime_evidence := _json(String(paths["runtime_contact_evidence_sha256"]))
	var catalogue := _json(String(paths["runtime_catalogue_sha256"]))
	if retained_evidence.is_empty() or retained_evidence != runtime_evidence:
		return "Production retained/runtime contact evidence disagrees"
	var identity := String(contact_value.get("package_identity", ""))
	var generation := String(contact_value.get("generation", ""))
	if String(retained_evidence.get("package_identity", "")) != identity:
		return "Production contact-evidence identity mismatch"
	var source = retained_evidence.get("source_derived")
	if not source is Dictionary or String((source as Dictionary).get("generation", "")) != generation:
		return "Production contact-evidence generation mismatch"
	var catalogue_contact = catalogue.get("contact_evidence")
	if not catalogue_contact is Dictionary \
			or String((catalogue_contact as Dictionary).get("package_identity", "")) != identity \
			or String((catalogue_contact as Dictionary).get("generation", "")) != generation:
		return "Production catalogue contact identity or generation mismatch"
	return ""


static func _validate_contact_free_idle(descriptor: Dictionary, name: String, variant: String, index: String, blend: String, root: String) -> String:
	if name != "idle" or descriptor.get("contact_evidence") != null:
		return "A contact-free descriptor must be an idle package without contact evidence"
	var paths := {
		"processed_index_sha256": index,
		"retargeted_blend_sha256": blend,
		"runtime_catalogue_sha256": root.path_join("%s_%s_catalogue.json" % [name, variant]),
		"runtime_library_sha256": root.path_join("%s_%s.tres" % [name, variant]),
	}
	var binding = descriptor.get("binding")
	if not binding is Dictionary or not _has_exact_keys(binding as Dictionary, paths.keys()):
		return "Contact-free package descriptor hash binding is incomplete"
	for key in paths:
		var path := String(paths[key])
		if not FileAccess.file_exists(path) or String((binding as Dictionary).get(key, "")) != FileAccess.get_sha256(path):
			return "Contact-free package descriptor hash mismatch: %s" % key
	var catalogue := _json(String(paths["runtime_catalogue_sha256"]))
	if catalogue.has("contact_evidence"):
		return "A contact-free idle catalogue must not declare contact evidence"
	return ""


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
