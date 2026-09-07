class_name RuntimeCapability
extends RefCounted

const IDLE_CONTACT_FREE := "idle_contact_free"

const BOUNDARY_BY_CAPABILITY := {
	IDLE_CONTACT_FREE: "confined_manoeuvre",
	"cyclic_forward_primary": "cyclic_travel",
	"cyclic_backward_primary": "cyclic_travel",
	"cyclic_lateral_left_left_lane": "cyclic_travel",
	"cyclic_lateral_left_right_lane": "cyclic_travel",
	"cyclic_lateral_right_left_lane": "cyclic_travel",
	"cyclic_lateral_right_right_lane": "cyclic_travel",
	"cyclic_arc_left_left_lane": "cyclic_travel",
	"cyclic_arc_left_right_lane": "cyclic_travel",
	"cyclic_arc_right_left_lane": "cyclic_travel",
	"cyclic_arc_right_right_lane": "cyclic_travel",
	"confined_side_step_left": "confined_manoeuvre",
	"confined_side_step_right": "confined_manoeuvre",
	"confined_pivot_left": "confined_manoeuvre",
	"confined_pivot_right": "confined_manoeuvre",
	"entry_forward_left": "transition",
	"entry_forward_right": "transition",
	"exit_forward_left": "transition",
	"exit_forward_right": "transition",
	"entry_backward_left": "transition",
	"entry_backward_right": "transition",
	"exit_backward_left": "transition",
	"exit_backward_right": "transition",
}

const CYCLIC_TRAVEL_CAPABILITIES := {
	"cyclic_forward_primary": true,
	"cyclic_backward_primary": true,
	"cyclic_lateral_left_left_lane": true,
	"cyclic_lateral_left_right_lane": true,
	"cyclic_lateral_right_left_lane": true,
	"cyclic_lateral_right_right_lane": true,
	"cyclic_arc_left_left_lane": true,
	"cyclic_arc_left_right_lane": true,
	"cyclic_arc_right_left_lane": true,
	"cyclic_arc_right_right_lane": true,
}

const CONFINED_CAPABILITIES := {
	"confined_side_step_left": true,
	"confined_side_step_right": true,
	"confined_pivot_left": true,
	"confined_pivot_right": true,
}


static func validate_processed_action(entry: Dictionary) -> String:
	var capability := String(entry.get("runtime_capability", ""))
	if capability.is_empty():
		return "Processed action has no explicit runtime capability"
	if not BOUNDARY_BY_CAPABILITY.has(capability):
		return "Processed action has unknown runtime capability: %s" % capability
	var expected_loop := boundary_kind(capability) != "transition"
	if bool(entry.get("is_loop", false)) != expected_loop:
		return "Processed action capability/loop intent is incompatible: %s" % capability
	var output = entry.get("post_repair_event_evidence")
	if is_contact_free(capability):
		if entry.has("source_directional_event_evidence") or entry.has("event_derivation_evidence") or output != null or entry.has("loop_repair_evidence"):
			return "Processed contact-free capability has directional provenance: %s" % capability
	elif output is Dictionary:
		var events = (output as Dictionary).get("events", [])
		if not events is Array:
			return "Processed action capability has malformed directional phase events: %s" % capability
		var event_count := (events as Array).size()
		if is_confined(capability) and event_count != 0:
			return "Processed confined capability has directional phase events: %s" % capability
		if not is_confined(capability) and event_count == 0:
			return "Processed travel/transition capability has no directional phase events: %s" % capability
	return ""


static func boundary_kind(capability: String) -> String:
	return String(BOUNDARY_BY_CAPABILITY.get(capability, ""))


static func is_contact_free(capability: String) -> bool:
	return capability == IDLE_CONTACT_FREE


static func is_confined(capability: String) -> bool:
	return CONFINED_CAPABILITIES.has(capability)


static func requires_directional_event_provenance(capability: String) -> bool:
	return not is_contact_free(capability)


static func requires_loop_repair(capability: String) -> bool:
	return CYCLIC_TRAVEL_CAPABILITIES.has(capability)
