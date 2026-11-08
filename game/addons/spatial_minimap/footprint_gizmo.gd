@tool
extends EditorNode3DGizmoPlugin

## Authoring-time 3D footprint gizmos for every minimap primitive (SCN-002 TR-17). Outlines come from the
## production GetFootprintOutline method, so gizmos always reflect the authored primitive the renderer captures.


func _init() -> void:
	create_material("footprint", Color(0.1, 0.8, 0.8))


func _get_gizmo_name() -> String:
	return "Spatial Minimap Footprint"


func _has_gizmo(node: Node3D) -> bool:
	return node.has_method("GetFootprintOutline")


func _redraw(gizmo: EditorNode3DGizmo) -> void:
	gizmo.clear()
	var node := gizmo.get_node_3d()
	var points: PackedVector3Array = node.call("GetFootprintOutline")
	if points.is_empty():
		return
	var lines := PackedVector3Array()
	for index in points.size():
		lines.append(node.to_local(points[index]))
		lines.append(node.to_local(points[(index + 1) % points.size()]))
	gizmo.add_lines(lines, get_material("footprint", gizmo))
