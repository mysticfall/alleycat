@tool
extends EditorNode3DGizmoPlugin

const CYLINDRICAL_GRAB_POINT_SCRIPT_PATH := "res://src/Interaction/CylindricalGrabPoint.cs"
const CIRCLE_SEGMENTS := 32
const LONGITUDINAL_SEGMENTS := 8
const MINIMUM_DRAW_RADIUS := 0.001
const HAND_AXIS_LENGTH := 0.06

var _selection: EditorSelection


func _init(selection: EditorSelection = null) -> void:
	_selection = selection
	create_material("axis", Color(0.1, 0.95, 0.25, 1.0), false, true)
	create_material("positive_y", Color(1.0, 0.78, 0.15, 1.0), false, true)
	create_material("reach", Color(0.25, 0.75, 1.0, 0.72), false, true)
	create_material("snap", Color(0.95, 0.35, 1.0, 0.78), false, true)
	create_material("offset", Color(1.0, 0.45, 0.1, 1.0), false, true)
	create_material("hand_x", Color(1.0, 0.15, 0.15, 1.0), false, true)
	create_material("hand_y", Color(0.15, 1.0, 0.15, 1.0), false, true)
	create_material("hand_z", Color(0.25, 0.45, 1.0, 1.0), false, true)


func _get_gizmo_name() -> String:
	return "CylindricalGrabPoint"


func _has_gizmo(node: Node3D) -> bool:
	return _is_cylindrical_grab_point(node)


func _redraw(gizmo: EditorNode3DGizmo) -> void:
	gizmo.clear()

	var node := gizmo.get_node_3d()
	if node == null or not _is_selected(node):
		return

	var length_metres := maxf(_get_float_property(node, "LengthMetres", 0.0), 0.0)
	var reach_distance_metres := maxf(_get_float_property(node, "ReachDistanceMetres", 0.0), 0.0)
	var snap_distance_metres := maxf(_get_float_property(node, "SnapDistanceMetres", 0.0), 0.0)
	var half_length := length_metres * 0.5
	var y_min := -half_length
	var y_max := half_length

	_draw_axis(gizmo, y_min, y_max, reach_distance_metres)
	if reach_distance_metres > 0.0 and length_metres > 0.0:
		_draw_wire_tube(gizmo, y_min, y_max, reach_distance_metres, "reach", true)
	if snap_distance_metres > 0.0 and length_metres > 0.0:
		_draw_wire_tube(gizmo, y_min, y_max, snap_distance_metres, "snap", false)
	_draw_authored_hand_offset(gizmo, node)


func _is_cylindrical_grab_point(node: Node) -> bool:
	if node == null:
		return false
	if node.is_class("CylindricalGrabPoint"):
		return true

	var script := node.get_script() as Script
	if script == null:
		return false
	if script.resource_path == CYLINDRICAL_GRAB_POINT_SCRIPT_PATH:
		return true
	if script.resource_path.ends_with("/CylindricalGrabPoint.cs"):
		return true
	if script.has_method("get_global_name") and script.get_global_name() == "CylindricalGrabPoint":
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


func _draw_axis(gizmo: EditorNode3DGizmo, y_min: float, y_max: float, scale_hint: float) -> void:
	var lines := PackedVector3Array()
	var start := Vector3(0.0, y_min, 0.0)
	var end := Vector3(0.0, y_max, 0.0)
	_append_line(lines, start, end)
	gizmo.add_lines(lines, get_material("axis", gizmo), false)

	var marker_size := maxf(scale_hint * 0.35, 0.025)
	var marker_lines := PackedVector3Array()
	_append_line(marker_lines, end, end + Vector3(marker_size * 0.45, -marker_size, 0.0))
	_append_line(marker_lines, end, end + Vector3(-marker_size * 0.45, -marker_size, 0.0))
	_append_line(marker_lines, end, end + Vector3(0.0, -marker_size, marker_size * 0.45))
	_append_line(marker_lines, end, end + Vector3(0.0, -marker_size, -marker_size * 0.45))
	gizmo.add_lines(marker_lines, get_material("positive_y", gizmo), false)


func _draw_wire_tube(
	gizmo: EditorNode3DGizmo,
	y_min: float,
	y_max: float,
	radius: float,
	material_name: StringName,
	include_capsule_hints: bool
) -> void:
	var clamped_radius := maxf(radius, MINIMUM_DRAW_RADIUS)
	var lines := PackedVector3Array()

	_append_ring(lines, y_min, clamped_radius)
	_append_ring(lines, y_max, clamped_radius)
	_append_ring(lines, (y_min + y_max) * 0.5, clamped_radius)

	for index in LONGITUDINAL_SEGMENTS:
		var angle := TAU * float(index) / float(LONGITUDINAL_SEGMENTS)
		var x := cos(angle) * clamped_radius
		var z := sin(angle) * clamped_radius
		_append_line(lines, Vector3(x, y_min, z), Vector3(x, y_max, z))

	if include_capsule_hints:
		_append_capsule_arc(lines, y_min, clamped_radius, true, true)
		_append_capsule_arc(lines, y_min, clamped_radius, true, false)
		_append_capsule_arc(lines, y_max, clamped_radius, false, true)
		_append_capsule_arc(lines, y_max, clamped_radius, false, false)

	gizmo.add_lines(lines, get_material(material_name, gizmo), false)


func _append_ring(lines: PackedVector3Array, y: float, radius: float) -> void:
	for index in CIRCLE_SEGMENTS:
		var current_angle := TAU * float(index) / float(CIRCLE_SEGMENTS)
		var next_angle := TAU * float(index + 1) / float(CIRCLE_SEGMENTS)
		_append_line(
			lines,
			Vector3(cos(current_angle) * radius, y, sin(current_angle) * radius),
			Vector3(cos(next_angle) * radius, y, sin(next_angle) * radius))


func _append_capsule_arc(lines: PackedVector3Array, end_y: float, radius: float, is_negative_end: bool, x_plane: bool) -> void:
	var direction := -1.0 if is_negative_end else 1.0
	for index in CIRCLE_SEGMENTS / 2:
		var current_angle := PI * float(index) / float(CIRCLE_SEGMENTS / 2)
		var next_angle := PI * float(index + 1) / float(CIRCLE_SEGMENTS / 2)
		var current_perpendicular := cos(current_angle) * radius
		var next_perpendicular := cos(next_angle) * radius
		var current_y := end_y + sin(current_angle) * radius * direction
		var next_y := end_y + sin(next_angle) * radius * direction

		if x_plane:
			_append_line(lines, Vector3(current_perpendicular, current_y, 0.0), Vector3(next_perpendicular, next_y, 0.0))
		else:
			_append_line(lines, Vector3(0.0, current_y, current_perpendicular), Vector3(0.0, next_y, next_perpendicular))


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
