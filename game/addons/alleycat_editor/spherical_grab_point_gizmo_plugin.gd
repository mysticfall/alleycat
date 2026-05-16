@tool
extends EditorNode3DGizmoPlugin

const SPHERICAL_GRAB_POINT_SCRIPT_PATH := "res://src/Interaction/SphericalGrabPoint.cs"
const CIRCLE_SEGMENTS := 32
const MINIMUM_DRAW_RADIUS := 0.001
const ORIGIN_MARKER_SIZE := 0.025
const PALM_HINT_LENGTH := 0.08
const HAND_AXIS_LENGTH := 0.06

var _selection: EditorSelection


func _init(selection: EditorSelection = null) -> void:
	_selection = selection
	create_material("origin", Color(1.0, 0.9, 0.2, 1.0), false, true)
	create_material("reach", Color(0.25, 0.75, 1.0, 0.72), false, true)
	create_material("palm_hint", Color(0.75, 0.35, 1.0, 1.0), false, true)
	create_material("offset", Color(1.0, 0.45, 0.1, 1.0), false, true)
	create_material("hand_x", Color(1.0, 0.15, 0.15, 1.0), false, true)
	create_material("hand_y", Color(0.15, 1.0, 0.15, 1.0), false, true)
	create_material("hand_z", Color(0.25, 0.45, 1.0, 1.0), false, true)


func _get_gizmo_name() -> String:
	return "SphericalGrabPoint"


func _has_gizmo(node: Node3D) -> bool:
	return _is_spherical_grab_point(node)


func _redraw(gizmo: EditorNode3DGizmo) -> void:
	gizmo.clear()

	var node := gizmo.get_node_3d()
	if node == null or not _is_selected(node):
		return

	var reach_distance_metres := maxf(_get_float_property(node, "ReachDistanceMetres", 0.0), 0.0)
	_draw_origin_marker(gizmo)
	if reach_distance_metres > 0.0:
		_draw_wire_sphere(gizmo, reach_distance_metres)
	_draw_hand_local_palm_direction_hint(gizmo, node)
	_draw_authored_hand_offset(gizmo, node)


func _is_spherical_grab_point(node: Node) -> bool:
	if node == null:
		return false
	if node.is_class("SphericalGrabPoint"):
		return true

	var script := node.get_script() as Script
	if script == null:
		return false
	if script.resource_path == SPHERICAL_GRAB_POINT_SCRIPT_PATH:
		return true
	if script.resource_path.ends_with("/SphericalGrabPoint.cs"):
		return true
	if script.has_method("get_global_name") and script.get_global_name() == "SphericalGrabPoint":
		return true

	return false


func _is_selected(node: Node) -> bool:
	if _selection == null:
		return false
	return _selection.get_selected_nodes().has(node)


func _get_float_property(node: Node, property_name: StringName, default_value: float) -> float:
	var value: Variant = node.get(property_name)
	if value == null:
		return default_value
	return float(value)


func _get_vector3_property(node: Node, property_name: StringName, default_value: Vector3) -> Vector3:
	var value: Variant = node.get(property_name)
	if value is Vector3:
		return value
	return default_value


func _draw_origin_marker(gizmo: EditorNode3DGizmo) -> void:
	var lines := PackedVector3Array()
	_append_line(lines, Vector3.LEFT * ORIGIN_MARKER_SIZE, Vector3.RIGHT * ORIGIN_MARKER_SIZE)
	_append_line(lines, Vector3.DOWN * ORIGIN_MARKER_SIZE, Vector3.UP * ORIGIN_MARKER_SIZE)
	_append_line(lines, Vector3.FORWARD * ORIGIN_MARKER_SIZE, Vector3.BACK * ORIGIN_MARKER_SIZE)
	gizmo.add_lines(lines, get_material("origin", gizmo), false)


func _draw_wire_sphere(gizmo: EditorNode3DGizmo, radius: float) -> void:
	var clamped_radius := maxf(radius, MINIMUM_DRAW_RADIUS)
	var lines := PackedVector3Array()
	_append_circle_xy(lines, clamped_radius)
	_append_circle_xz(lines, clamped_radius)
	_append_circle_yz(lines, clamped_radius)
	gizmo.add_lines(lines, get_material("reach", gizmo), false)


# PalmLocalDirection is authored in the query hand's local frame at runtime. This selected-only cue is a hand-local
# preview/hint at the marker origin; the exact palm-facing check still depends on each runtime hand transform.
func _draw_hand_local_palm_direction_hint(gizmo: EditorNode3DGizmo, node: Node) -> void:
	var palm_local_direction := _get_vector3_property(node, "PalmLocalDirection", Vector3.ZERO)
	if palm_local_direction.length_squared() <= 0.000001:
		return

	var direction := palm_local_direction.normalized()
	var end := direction * PALM_HINT_LENGTH
	var lines := PackedVector3Array()
	_append_line(lines, Vector3.ZERO, end)
	_append_arrowhead(lines, Vector3.ZERO, end)
	gizmo.add_lines(lines, get_material("palm_hint", gizmo), false)


func _draw_authored_hand_offset(gizmo: EditorNode3DGizmo, node: Node) -> void:
	var grab_point_position_offset_from_hand := _get_vector3_property(node, "GrabPointPositionOffsetFromHand", Vector3.ZERO)
	var grab_point_rotation_offset_from_hand := _get_vector3_property(node, "GrabPointRotationOffsetFromHand", Vector3.ZERO)
	var grab_from_hand := Transform3D(Basis.from_euler(grab_point_rotation_offset_from_hand), grab_point_position_offset_from_hand)
	var hand_from_grab := grab_from_hand.affine_inverse()
	var hand_origin := hand_from_grab.origin

	var offset_lines := PackedVector3Array()
	_append_line(offset_lines, Vector3.ZERO, hand_origin)
	_append_arrowhead(offset_lines, Vector3.ZERO, hand_origin)
	gizmo.add_lines(offset_lines, get_material("offset", gizmo), false)

	_draw_hand_axis(gizmo, hand_origin, hand_from_grab.basis.x.normalized(), "hand_x")
	_draw_hand_axis(gizmo, hand_origin, hand_from_grab.basis.y.normalized(), "hand_y")
	_draw_hand_axis(gizmo, hand_origin, hand_from_grab.basis.z.normalized(), "hand_z")


func _draw_hand_axis(gizmo: EditorNode3DGizmo, origin: Vector3, axis: Vector3, material_name: StringName) -> void:
	var lines := PackedVector3Array()
	_append_line(lines, origin, origin + axis * HAND_AXIS_LENGTH)
	gizmo.add_lines(lines, get_material(material_name, gizmo), false)


func _append_circle_xy(lines: PackedVector3Array, radius: float) -> void:
	for index in CIRCLE_SEGMENTS:
		var current_angle := TAU * float(index) / float(CIRCLE_SEGMENTS)
		var next_angle := TAU * float(index + 1) / float(CIRCLE_SEGMENTS)
		_append_line(
			lines,
			Vector3(cos(current_angle) * radius, sin(current_angle) * radius, 0.0),
			Vector3(cos(next_angle) * radius, sin(next_angle) * radius, 0.0))


func _append_circle_xz(lines: PackedVector3Array, radius: float) -> void:
	for index in CIRCLE_SEGMENTS:
		var current_angle := TAU * float(index) / float(CIRCLE_SEGMENTS)
		var next_angle := TAU * float(index + 1) / float(CIRCLE_SEGMENTS)
		_append_line(
			lines,
			Vector3(cos(current_angle) * radius, 0.0, sin(current_angle) * radius),
			Vector3(cos(next_angle) * radius, 0.0, sin(next_angle) * radius))


func _append_circle_yz(lines: PackedVector3Array, radius: float) -> void:
	for index in CIRCLE_SEGMENTS:
		var current_angle := TAU * float(index) / float(CIRCLE_SEGMENTS)
		var next_angle := TAU * float(index + 1) / float(CIRCLE_SEGMENTS)
		_append_line(
			lines,
			Vector3(0.0, cos(current_angle) * radius, sin(current_angle) * radius),
			Vector3(0.0, cos(next_angle) * radius, sin(next_angle) * radius))


func _append_arrowhead(lines: PackedVector3Array, start: Vector3, end: Vector3) -> void:
	var direction := end - start
	if direction.length_squared() <= 0.000001:
		return

	var unit_direction := direction.normalized()
	var reference := Vector3.UP if absf(unit_direction.dot(Vector3.UP)) < 0.95 else Vector3.RIGHT
	var side_a := unit_direction.cross(reference).normalized()
	var side_b := unit_direction.cross(side_a).normalized()
	var size := maxf(minf(direction.length() * 0.2, 0.04), 0.015)
	_append_line(lines, end, end - unit_direction * size + side_a * size * 0.45)
	_append_line(lines, end, end - unit_direction * size - side_a * size * 0.45)
	_append_line(lines, end, end - unit_direction * size + side_b * size * 0.45)
	_append_line(lines, end, end - unit_direction * size - side_b * size * 0.45)


func _append_line(lines: PackedVector3Array, from: Vector3, to: Vector3) -> void:
	lines.push_back(from)
	lines.push_back(to)
