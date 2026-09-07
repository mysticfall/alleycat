extends SceneTree

var failures: Array[String] = []
var stitch_graph: Script


func _init() -> void:
	stitch_graph = _load_helper("stitch_graph.gd")
	_test_native_mirror_reverse_and_combined_derivations()
	_test_missing_ambiguous_and_dangling_refusal()
	for failure in failures:
		push_error(failure)
	quit(1 if not failures.is_empty() else 0)


func _test_native_mirror_reverse_and_combined_derivations() -> void:
	var clips := [
		_clip("target_combined", "cyclic_travel", [_event("acquire_r", "Right", "Acquire", null)]),
		_clip("source_native", "transition", [_event("release_l", "Left", "Release", "end")]),
		_clip("target_mirror", "cyclic_travel", [_event("release_r", "Right", "Release", null)]),
		_clip("source_reverse", "transition", [_event("acquire_l", "Left", "Acquire", "end")]),
		_clip("target_native", "cyclic_travel", [_event("release_l", "Left", "Release", null)]),
		_clip("source_combined", "transition", [_event("acquire_r", "Right", "Acquire", "end")]),
		_clip("target_reverse", "cyclic_travel", [_event("acquire_l", "Left", "Acquire", null)]),
		_clip("source_mirror", "transition", [_event("release_r", "Right", "Release", "end")]),
		_clip("reverse_exit_target", "transition", [_event("acquire_l", "Left", "Acquire", "start")]),
		_clip("reverse_exit_source", "cyclic_travel", [_event("acquire_l", "Left", "Acquire", null)]),
		{"role": "unrelated_confined", "boundary_kind": "confined_manoeuvre", "phase_events": {"applicability": "inapplicable"}},
	]
	var edges := [
		{"from_role": "source_combined", "to_role": "target_combined"},
		{"from_role": "source_native", "to_role": "target_native"},
		{"from_role": "source_reverse", "to_role": "target_reverse"},
		{"from_role": "source_mirror", "to_role": "target_mirror"},
		{"from_role": "reverse_exit_source", "to_role": "reverse_exit_target"},
	]
	var result: Dictionary = stitch_graph.resolve(clips, edges)
	_expect(bool(result.get("ok", false)), "Declared native/mirror/reverse/combined stitch graph was refused")
	if not bool(result.get("ok", false)):
		return
	var declarations := result["declarations"] as Dictionary
	_expect(_mapping_event(declarations, "source_native") == "release_l", "Native event was not preserved")
	_expect(_mapping_event(declarations, "source_mirror") == "release_r", "Mirrored anatomy was not consumed from its declaration")
	_expect(_mapping_event(declarations, "source_reverse") == "acquire_l", "Reversed Release-to-Acquire semantics were not consumed")
	_expect(_mapping_event(declarations, "source_combined") == "acquire_r", "Combined reverse/mirror semantics were not consumed")
	_expect(_mapping_event(declarations, "reverse_exit_source") == "acquire_l", "Reversed start-boundary ownership was not consumed")
	_expect((declarations["source_native"] as Dictionary)["mappings"].size() == 1, "Arbitrary inventory introduced undeclared edges")
	_expect((declarations["unrelated_confined"] as Dictionary)["applicability"] == "inapplicable", "Unrelated confined role was not preserved")


func _test_missing_ambiguous_and_dangling_refusal() -> void:
	var source := _clip("one", "transition", [_event("release_l", "Left", "Release", "end")])
	var incompatible := _clip("two", "cyclic_travel", [_event("acquire_l", "Left", "Acquire", null)])
	_expect(not bool(stitch_graph.resolve([source, incompatible], [{"from_role": "one", "to_role": "two"}]).get("ok", false)), "Missing compatible event pair was accepted")

	var ambiguous := _clip("two", "cyclic_travel", [
		_event("release_l", "Left", "Release", null),
		_event("release_l", "Left", "Release", null),
	])
	_expect(not bool(stitch_graph.resolve([source, ambiguous], [{"from_role": "one", "to_role": "two"}]).get("ok", false)), "Ambiguous duplicate event ownership was accepted")
	_expect(not bool(stitch_graph.resolve([source], [{"from_role": "one", "to_role": "missing"}]).get("ok", false)), "Dangling mapping role was accepted")


func _clip(role: String, boundary_kind: String, events: Array) -> Dictionary:
	return {"role": role, "boundary_kind": boundary_kind, "phase_events": {"applicability": "applicable", "events": events}}


func _event(identifier: String, side: String, kind: String, boundary_context: Variant) -> Dictionary:
	return {"id": identifier, "anatomical_side": side, "event_kind": kind, "boundary_context": boundary_context}


func _mapping_event(declarations: Dictionary, role: String) -> String:
	var mappings: Array = (declarations[role] as Dictionary)["mappings"]
	return String(((mappings[0] as Dictionary)["from"] as Dictionary)["event"])


func _expect(condition: bool, message: String) -> void:
	if not condition:
		failures.append(message)

func _load_helper(filename: String) -> Script:
	var test_path: String = get_script().resource_path
	var helper_path: String = test_path.get_base_dir().get_base_dir().get_base_dir().path_join("godot/lib").path_join(filename).simplify_path()
	return load(helper_path) as Script
