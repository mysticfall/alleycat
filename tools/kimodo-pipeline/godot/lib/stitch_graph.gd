class_name StitchGraph
extends RefCounted


static func resolve(clips: Array, declared_edges: Variant) -> Dictionary:
	if not declared_edges is Array:
		return _failure("Package stitch_edges must be an array")
	var by_role := {}
	var mappings_by_role := {}
	for value in clips:
		if not value is Dictionary:
			return _failure("Package clip declaration is invalid")
		var clip := value as Dictionary
		var role := String(clip.get("role", ""))
		if role.is_empty() or by_role.has(role):
			return _failure("Package clip roles must be non-empty and unique")
		var validation_error := _validate_clip_events(clip)
		if not validation_error.is_empty():
			return _failure(validation_error)
		by_role[role] = clip
		mappings_by_role[role] = []

	var seen_edges := {}
	for value in declared_edges:
		if not value is Dictionary:
			return _failure("Package stitch edge must be an object")
		var edge := value as Dictionary
		if not _has_exact_keys(edge, ["from_role", "to_role"]):
			return _failure("Package stitch edge must declare only from_role and to_role")
		var from_role := String(edge.get("from_role", ""))
		var to_role := String(edge.get("to_role", ""))
		var edge_key := "%s>%s" % [from_role, to_role]
		if from_role.is_empty() or to_role.is_empty() or from_role == to_role or seen_edges.has(edge_key):
			return _failure("Package stitch edges must be non-empty, unique, and connect distinct roles")
		seen_edges[edge_key] = true
		if not by_role.has(from_role) or not by_role.has(to_role):
			return _failure("Package stitch edge refers to an unknown role: %s" % edge_key)
		var source := by_role[from_role] as Dictionary
		var target := by_role[to_role] as Dictionary
		if String(source.get("boundary_kind", "")) == "confined_manoeuvre" or String(target.get("boundary_kind", "")) == "confined_manoeuvre":
			return _failure("Package stitch edge cannot connect a confined manoeuvre: %s" % edge_key)
		var compatible := _compatible_event_pairs(source, target)
		if compatible.size() != 1:
			return _failure("Package stitch edge must resolve to exactly one compatible declared event pair: %s" % edge_key)
		var pair := compatible[0] as Dictionary
		var mapping := {
			"from": {"role": from_role, "event": String((pair["from"] as Dictionary)["id"])},
			"to": {"role": to_role, "event": String((pair["to"] as Dictionary)["id"])},
		}
		(mappings_by_role[from_role] as Array).append(mapping)
		if String(source["boundary_kind"]) == "transition" or String(target["boundary_kind"]) == "transition":
			(mappings_by_role[to_role] as Array).append(mapping.duplicate(true))

	var declarations := {}
	for role in by_role:
		var boundary_kind := String((by_role[role] as Dictionary)["boundary_kind"])
		declarations[role] = {"applicability": "inapplicable"} if boundary_kind == "confined_manoeuvre" else {
			"applicability": "applicable",
			"mappings": mappings_by_role[role],
		}
	return {"ok": true, "declarations": declarations}


static func _validate_clip_events(clip: Dictionary) -> String:
	var boundary_kind := String(clip.get("boundary_kind", ""))
	if boundary_kind not in ["cyclic_travel", "confined_manoeuvre", "transition"]:
		return "Package clip boundary_kind is invalid"
	var phase_events = clip.get("phase_events")
	if not phase_events is Dictionary:
		return "Package clip phase_events declaration is invalid"
	var applicability := String((phase_events as Dictionary).get("applicability", ""))
	var events = (phase_events as Dictionary).get("events", [])
	if applicability == "inapplicable":
		return "" if boundary_kind == "confined_manoeuvre" and events is Array and events.is_empty() else "Only an event-free confined manoeuvre may declare phase events inapplicable"
	if applicability != "applicable" or not events is Array:
		return "Package clip phase event applicability is invalid"
	var seen := {}
	for value in events:
		if not value is Dictionary:
			return "Package phase event must be an object"
		var event := value as Dictionary
		var identifier := String(event.get("id", ""))
		var side := String(event.get("anatomical_side", ""))
		var kind := String(event.get("event_kind", ""))
		var expected_id := "%s_%s" % [kind.to_lower(), side.left(1).to_lower()]
		if kind not in ["Acquire", "Release"] or side not in ["Left", "Right"] or identifier != expected_id or seen.has(identifier):
			return "Package phase event identity, kind, and anatomy must agree uniquely"
		seen[identifier] = true
		var context = event.get("boundary_context")
		if boundary_kind == "transition" and context not in ["start", "end"]:
			return "Transition phase events must declare start or end ownership"
		if boundary_kind != "transition" and context != null:
			return "Non-transition phase events cannot declare finite boundary ownership"
	if boundary_kind == "confined_manoeuvre" and not events.is_empty():
		return "Confined manoeuvres cannot own stitchable phase events"
	return ""


static func _compatible_event_pairs(source: Dictionary, target: Dictionary) -> Array:
	var result: Array = []
	var source_events: Array = (source["phase_events"] as Dictionary).get("events", [])
	var target_events: Array = (target["phase_events"] as Dictionary).get("events", [])
	for source_value in source_events:
		var source_event := source_value as Dictionary
		if not _owns_edge_boundary(source, source_event, true):
			continue
		for target_value in target_events:
			var target_event := target_value as Dictionary
			if _owns_edge_boundary(target, target_event, false) \
					and source_event.get("event_kind") == target_event.get("event_kind") \
					and source_event.get("anatomical_side") == target_event.get("anatomical_side"):
				result.append({"from": source_event, "to": target_event})
	return result


static func _owns_edge_boundary(clip: Dictionary, event: Dictionary, source: bool) -> bool:
	if String(clip["boundary_kind"]) == "transition":
		return event.get("boundary_context") == ("end" if source else "start")
	return event.get("boundary_context") == null


static func _has_exact_keys(value: Dictionary, expected: Array[String]) -> bool:
	var actual: Array[String] = []
	for key in value.keys():
		actual.append(String(key))
	actual.sort()
	var sorted_expected := expected.duplicate()
	sorted_expected.sort()
	return actual == sorted_expected


static func _failure(message: String) -> Dictionary:
	return {"ok": false, "error": message}
