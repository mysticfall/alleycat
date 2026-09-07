class_name LocomotionContactEvidence
extends RefCounted

const CONTACT_SCHEMA_VERSION := 1
const REQUIRED_SAMPLE_FIELDS := [
	"schema_version", "role", "foot", "lifecycle", "timing", "validity", "confidence",
	"provenance", "clip", "variant", "reference", "identity", "generation",
]


static func describe(package: Dictionary, index: Dictionary, runtime_root: String) -> Dictionary:
	var declaration = package.get("contact_evidence")
	if declaration == null:
		if bool(package.get("contact_evidence_required", false)):
			return {"error": "Package descriptor requires an explicit contact-evidence declaration"}
		return {"declared": false}
	if not declaration is Dictionary:
		return {"error": "Contact-evidence declaration must be an object"}
	var value := declaration as Dictionary
	var input := String(value.get("input", ""))
	var retained_input := String(value.get("retained_input", input))
	var runtime_path := String(value.get("runtime_path", ""))
	var package_identity := String(value.get("package_identity", ""))
	var generation := String(value.get("generation", ""))
	if input.is_empty() or retained_input.is_empty() or runtime_path.is_empty() or package_identity.is_empty() or generation.is_empty():
		return {"error": "Contact-evidence declaration requires input, runtime_path, package_identity, and generation"}
	if value.has("retained_input") and (retained_input.begins_with("res://temp/") or retained_input.begins_with("res://tests/")):
		return {"error": "Contact-evidence retained_input must not name a disposable test or temp path"}
	if runtime_path.is_absolute_path() or runtime_path.contains(".."):
		return {"error": "Contact-evidence runtime_path must be package-relative"}
	if not FileAccess.file_exists(input):
		return {"error": "Contact-evidence input is missing: %s" % input}
	var source_text := FileAccess.get_file_as_string(input)
	var evidence = JSON.parse_string(source_text)
	if not evidence is Dictionary:
		return {"error": "Contact-evidence input is not a JSON object: %s" % input}
	var error := _validate(evidence as Dictionary, index, runtime_path, package_identity, generation)
	if not error.is_empty():
		return {"error": error}
	return {
		"declared": true,
		"input": input,
		"retained_input": retained_input,
		"runtime_path": runtime_path,
		"runtime_file": runtime_root.path_join(runtime_path),
		"catalogue_path": runtime_root.path_join(runtime_path),
		"package_identity": package_identity,
		"generation": generation,
		"source_text": source_text,
		"evidence": evidence,
		"identities": _identities(evidence as Dictionary),
	}


static func copy_to_stage(info: Dictionary, stage: String) -> String:
	if not bool(info.get("declared", false)):
		return ""
	var destination := stage.path_join(String(info["runtime_path"]))
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(destination.get_base_dir()))
	var file := FileAccess.open(destination, FileAccess.WRITE)
	if file == null:
		return "Could not stage contact evidence: %s" % destination
	file.store_string(String(info["source_text"]))
	return ""


static func validate_published(info: Dictionary, index: Dictionary, runtime_root: String, catalogue: Dictionary) -> String:
	if not bool(info.get("declared", false)):
		return ""
	var published_path := String(info["runtime_file"])
	var catalogue_path := String(info.get("catalogue_path", published_path))
	if not FileAccess.file_exists(published_path):
		return "Published contact evidence is missing: %s" % published_path
	var published_text := FileAccess.get_file_as_string(published_path)
	var published = JSON.parse_string(published_text)
	if not published is Dictionary:
		return "Published contact evidence is not a JSON object: %s" % published_path
	if published_text != String(info["source_text"]):
		return "Published contact evidence differs from the declared upstream artefact"
	var evidence := published as Dictionary
	var error := _validate(
		evidence, index, String(info["runtime_path"]),
		String(info["package_identity"]), String(info["generation"]),
	)
	if not error.is_empty():
		return error
	var catalogue_contact = catalogue.get("contact_evidence")
	if not catalogue_contact is Dictionary:
		return "Runtime catalogue is missing declared contact-evidence provenance"
	var declared := catalogue_contact as Dictionary
	if int(declared.get("schema_version", 0)) != CONTACT_SCHEMA_VERSION \
		or String(declared.get("path", "")) != catalogue_path \
		or String(declared.get("package_identity", "")) != String(info["package_identity"]) \
		or String(declared.get("generation", "")) != String(info["generation"]):
		return "Runtime catalogue contact-evidence declaration differs from the package descriptor"
	var provenance: Variant = declared.get("diagnostic_provenance_non_runtime_authoritative")
	if not provenance is Dictionary \
		or String((provenance as Dictionary).get("processed_index", "")) != String(info.get("index_path", "")) \
		or String((provenance as Dictionary).get("input", "")) != String(info["retained_input"]):
		return "Runtime catalogue contact-evidence provenance differs from the package descriptor"
	var declared_identities := _string_array(declared.get("identities", []))
	if declared_identities != _identities(evidence):
		return "Runtime catalogue contact-evidence identity set differs from the evidence"
	var clips: Variant = catalogue.get("clips", [])
	if not clips is Array:
		return "Runtime catalogue clips are invalid for contact-evidence validation"
	var catalogue_clips := {}
	for value in clips:
		if value is Dictionary:
			var clip := value as Dictionary
			var identity := String(clip.get("identity", ""))
			if identity.is_empty() or catalogue_clips.has(identity):
				return "Runtime catalogue clip identities are not bijective for contact evidence"
			catalogue_clips[identity] = clip
		else:
			return "Runtime catalogue clips are invalid for contact-evidence validation"
	for sample_value in evidence.get("samples", []):
		var sample := sample_value as Dictionary
		var identity := String(sample["identity"])
		var catalogue_clip: Variant = catalogue_clips.get(identity)
		if not catalogue_clip is Dictionary \
				or String((catalogue_clip as Dictionary).get("identity", "")) != String(sample["clip"]):
			return "Contact-evidence identity does not map bijectively to the runtime catalogue: %s" % identity
	return ""


static func catalogue_declaration(info: Dictionary, index_path: String) -> Dictionary:
	if not bool(info.get("declared", false)):
		return {}
	var output := {
		"schema_version": CONTACT_SCHEMA_VERSION,
		"path": String(info["catalogue_path"]),
		"package_identity": String(info["package_identity"]),
		"generation": String(info["generation"]),
		"identities": info["identities"],
		"diagnostic_provenance_non_runtime_authoritative": {
			"processed_index": index_path,
			"input": String(info["retained_input"]),
		},
	}
	return output


static func _validate(evidence: Dictionary, index: Dictionary, runtime_path: String, package_identity: String, generation: String) -> String:
	if not evidence.has("schema_version") or not evidence.has("package_identity") or not evidence.has("samples"):
		return "Contact evidence has an unsupported root schema"
	if evidence.keys().size() == 4:
		var source_derived: Variant = evidence.get("source_derived")
		if not source_derived is Dictionary or not _validate_source_derived(source_derived as Dictionary, generation).is_empty():
			return "Contact evidence source-derived provenance is malformed"
	elif evidence.keys().size() != 3:
		return "Contact evidence has an unsupported root schema"
	if int(evidence.get("schema_version", 0)) != CONTACT_SCHEMA_VERSION:
		return "Contact evidence schema version is invalid"
	if String(evidence.get("package_identity", "")) != package_identity:
		return "Contact evidence package identity is stale or mismatched"
	var samples: Variant = evidence.get("samples", [])
	if not samples is Array or (samples as Array).is_empty():
		return "Contact evidence samples are missing or empty"
	var actions := {}
	for entry_value in index.get("actions", []):
		if entry_value is Dictionary:
			var entry := entry_value as Dictionary
			var identity := String(entry.get("external_identity", ""))
			if not identity.is_empty() and not actions.has(identity):
				actions[identity] = entry
	if actions.is_empty():
		return "Processed index has no actions for contact-evidence validation"
	var unique_samples := {}
	for sample_value in samples:
		if not sample_value is Dictionary:
			return "Contact evidence contains a malformed sample"
		var sample := sample_value as Dictionary
		var sample_error := _validate_sample(sample, actions, generation, String(index.get("variant", "")))
		if not sample_error.is_empty():
			return sample_error
		var key := "%s|%s|%s|%s" % [sample["identity"], sample["foot"], sample["lifecycle"], JSON.stringify(sample["timing"])]
		if unique_samples.has(key):
			return "Contact evidence contains duplicate lifecycle declarations"
		unique_samples[key] = true
	return ""


static func _validate_source_derived(value: Dictionary, generation: String) -> String:
	var version := int(value.get("schema_version", 0))
	var required := ["schema_version", "method", "generation", "source_package", "paths_relative_to_processed_package", "input_digests", "mapping", "sample_rate_hz", "configuration", "clips", "uncertainty_reasons"]
	if version == 2:
		required = ["schema_version", "method", "generation", "publication_eligible", "required_refusals", "source_package", "paths_relative_to_processed_package", "input_digests", "phase_policy", "bone_mapping", "sampling", "configuration", "clips", "role_contact_matrix"]
	if version == 3:
		required = ["schema_version", "method", "generation", "publication_eligible", "required_refusals", "source_package", "paths_relative_to_processed_package", "input_digests", "phase_policy", "bone_mapping", "sampling", "configuration", "configuration_digest", "confidence_weights", "clips", "role_contact_matrix"]
	if version == 4:
		required = ["schema_version", "method", "generation", "publication_eligible", "required_refusals", "source_package", "paths_relative_to_processed_package", "input_digests", "phase_policy", "bone_mapping", "input_contracts", "cyclic_boundary_policy", "sampling", "configuration", "configuration_digest", "confidence_weights", "clips", "role_contact_matrix"]
	if version == 5:
		required = ["schema_version", "method", "generation", "publication_eligible", "required_refusals", "source_package", "paths_relative_to_processed_package", "input_digests", "phase_policy", "bone_mapping", "input_contracts", "phase_seed_policy", "sampling", "configuration", "configuration_digest", "confidence_weights", "clips", "role_contact_matrix"]
	if value.keys().size() != required.size(): return "unsupported fields"
	for field in required:
		if not value.has(field): return "missing %s" % field
	if version not in [1, 2, 3, 5] or String(value["method"]).is_empty() or String(value["generation"]) != generation: return "invalid method or generation"
	if version == 1 and (not _is_number(value["sample_rate_hz"]) or not _finite(float(value["sample_rate_hz"])) or float(value["sample_rate_hz"]) <= 0.0): return "invalid sample rate"
	if version == 2 and (String(value["method"]) != "phase_guided_bone_contact_refinement_v1" or String(value["sampling"]) != "native_fps"): return "invalid phase-guided bone method"
	if version == 3 and (String(value["method"]) != "phase_guided_relative_motion_segmentation_v1" or String(value["sampling"]) != "native_fps"): return "invalid phase-guided relative-motion method"
	if version == 4 and (String(value["method"]) != "phase_guided_relative_motion_segmentation_v1" or String(value["sampling"]) != "native_fps"): return "invalid cyclic phase-guided relative-motion method"
	if version == 5 and (String(value["method"]) != "phase_seeded_relative_motion_segmentation_v2" or String(value["sampling"]) != "native_fps"): return "invalid phase-seeded relative-motion method"
	var dictionary_fields := ["source_package", "paths_relative_to_processed_package", "input_digests", "configuration"]
	if version == 1: dictionary_fields.append("mapping")
	else: dictionary_fields.append_array(["phase_policy", "bone_mapping"])
	if version in [3, 4, 5]: dictionary_fields.append("confidence_weights")
	if version == 4: dictionary_fields.append("cyclic_boundary_policy")
	if version == 5: dictionary_fields.append("phase_seed_policy")
	for field in dictionary_fields:
		if not value[field] is Dictionary or (value[field] as Dictionary).is_empty(): return "invalid %s" % field
	var paths := value["paths_relative_to_processed_package"] as Dictionary
	for path_field in ["processed_index", "retargeted_blend"]:
		var path := String(paths.get(path_field, ""))
		if path.is_empty() or path.is_absolute_path() or path.contains("..") or path.begins_with("res://tests/") or path.begins_with("res://temp/"): return "invalid processed-relative path"
	if not value["clips"] is Array or (value["clips"] as Array).is_empty(): return "invalid evidence windows"
	if version == 1 and not value["uncertainty_reasons"] is Array: return "invalid uncertainty reasons"
	if version in [2, 3, 4, 5]:
		if not value["required_refusals"] is Array or not value["role_contact_matrix"] is Array: return "invalid phase-contact coverage"
		if not bool(value["publication_eligible"]) or not (value["required_refusals"] as Array).is_empty(): return "contact evidence contains required refusals"
		for declaration_field in ["phase_policy", "bone_mapping"]:
			var declaration := value[declaration_field] as Dictionary
			if String(declaration.get("identity", "")).is_empty() or String(declaration.get("digest", "")).is_empty(): return "invalid %s provenance" % declaration_field
	if version in [3, 4, 5]:
		if String(value["configuration_digest"]).is_empty(): return "invalid configuration provenance"
		var confidence_total := 0.0
		for component in ["semantic_event_anchor_agreement", "within_state_cohesion", "between_state_separation", "transition_persistence_sharpness", "contextual_opposite_consistency", "ambiguity_margin"]:
			if not (value["confidence_weights"] as Dictionary).has(component) or not _is_number((value["confidence_weights"] as Dictionary)[component]): return "invalid confidence weights"
			confidence_total += float((value["confidence_weights"] as Dictionary)[component])
		if not is_equal_approx(confidence_total, 1.0): return "invalid confidence weight total"
	if version == 4:
		return "stale post-repair physical-event producer contract"
	if version == 5:
		if not value["input_contracts"] is Array or (value["input_contracts"] as Array) != ["anim002_source_directional_phase_events_v1", "anim002_event_derivation_v1", "anim002_post_repair_directional_phase_events_v2", "anim002_selected_loop_repair_provenance_v2"]: return "invalid input contracts"
		var phase_seed_policy := value["phase_seed_policy"] as Dictionary
		if int(phase_seed_policy.get("detector_influence_samples", 0)) != 4 \
			or String(phase_seed_policy.get("two_release_sector", "")) != "cyclic_voronoi_midpoints" \
			or String(phase_seed_policy.get("one_release_sector", "")) != "half_cycle_around_repeated_seed" \
			or bool(phase_seed_policy.get("absolute_world_slide_affects_eligibility", true)):
			return "invalid phase-seed policy"
	return ""


static func _validate_sample(sample: Dictionary, actions: Dictionary, generation: String, variant: String) -> String:
	if sample.keys().size() != REQUIRED_SAMPLE_FIELDS.size():
		return "Contact evidence sample has unsupported fields"
	for field in REQUIRED_SAMPLE_FIELDS:
		if not sample.has(field):
			return "Contact evidence sample is missing %s" % field
	if int(sample.get("schema_version", 0)) != CONTACT_SCHEMA_VERSION:
		return "Contact evidence sample schema version is invalid"
	var identity := String(sample["identity"])
	var entry: Variant = actions.get(identity)
	if not entry is Dictionary:
		return "Contact evidence identity is absent from the processed index: %s" % identity
	if String(sample["role"]) != String((entry as Dictionary).get("role", "")):
		return "Contact evidence role differs from the processed index: %s" % identity
	if String(sample["variant"]) != variant:
		return "Contact evidence variant differs from the processed index: %s" % identity
	if String(sample["generation"]) != generation:
		return "Contact evidence generation is stale or mismatched: %s" % identity
	if String(sample["foot"]) not in ["left", "right"] or String(sample["lifecycle"]) not in ["acquire", "support", "release"]:
		return "Contact evidence foot or lifecycle is invalid: %s" % identity
	if String(sample["validity"]) != "valid":
		return "Contact evidence is stale, invalid, inferred, or inapplicable: %s" % identity
	for field in ["role", "provenance", "clip", "variant", "reference", "identity", "generation"]:
		if String(sample[field]).is_empty():
			return "Contact evidence sample has an empty %s" % field
	var confidence: Variant = sample.get("confidence")
	if not _is_number(confidence) or not _finite(float(confidence)) or float(confidence) <= 0.0:
		return "Contact evidence confidence is invalid: %s" % identity
	var timing: Variant = sample.get("timing")
	if not timing is Array or (timing as Array).size() != (2 if String(sample["lifecycle"]) == "support" else 1):
		return "Contact evidence lifecycle timing is invalid: %s" % identity
	var previous := -1.0
	for item in timing:
		if not _is_number(item) or not _finite(float(item)) or float(item) < 0.0 or float(item) > 1.0 or float(item) < previous:
			return "Contact evidence timing is invalid: %s" % identity
		previous = float(item)
	if String(sample["lifecycle"]) == "support" and float((timing as Array)[0]) >= float((timing as Array)[1]):
		return "Contact evidence support timing is not half-open: %s" % identity
	if String(sample["clip"]) != identity:
		return "Contact evidence clip is not the embedded library animation identity: %s" % identity
	return ""


static func _identities(evidence: Dictionary) -> Array:
	var identities: Array[String] = []
	for sample_value in evidence.get("samples", []):
		if sample_value is Dictionary:
			var identity := String((sample_value as Dictionary).get("identity", ""))
			if not identity.is_empty() and identity not in identities:
				identities.append(identity)
	identities.sort()
	return identities


static func _string_array(value: Variant) -> Array:
	if not value is Array:
		return []
	var output: Array[String] = []
	for item in value:
		var text := String(item)
		if text.is_empty() or text in output:
			return []
		output.append(text)
	output.sort()
	return output


static func _is_number(value: Variant) -> bool:
	return typeof(value) == TYPE_INT or typeof(value) == TYPE_FLOAT


static func _finite(value: float) -> bool:
	return not is_nan(value) and not is_inf(value)
