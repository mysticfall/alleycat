@tool
extends EditorPlugin

const CylindricalGrabPointGizmoPlugin := preload("res://addons/alleycat_editor/cylindrical_grab_point_gizmo_plugin.gd")
const SphericalGrabPointGizmoPlugin := preload("res://addons/alleycat_editor/spherical_grab_point_gizmo_plugin.gd")

var _cylindrical_grab_point_gizmo_plugin: EditorNode3DGizmoPlugin
var _spherical_grab_point_gizmo_plugin: EditorNode3DGizmoPlugin
var _last_selected_nodes: Array[Node] = []


func _enter_tree() -> void:
	var selection := get_editor_interface().get_selection()
	_last_selected_nodes = selection.get_selected_nodes()
	_cylindrical_grab_point_gizmo_plugin = CylindricalGrabPointGizmoPlugin.new(selection)
	_spherical_grab_point_gizmo_plugin = SphericalGrabPointGizmoPlugin.new(selection)
	add_node_3d_gizmo_plugin(_cylindrical_grab_point_gizmo_plugin)
	add_node_3d_gizmo_plugin(_spherical_grab_point_gizmo_plugin)
	selection.selection_changed.connect(_on_selection_changed)


func _exit_tree() -> void:
	var selection := get_editor_interface().get_selection()
	if selection.selection_changed.is_connected(_on_selection_changed):
		selection.selection_changed.disconnect(_on_selection_changed)

	if _cylindrical_grab_point_gizmo_plugin != null:
		remove_node_3d_gizmo_plugin(_cylindrical_grab_point_gizmo_plugin)
		_cylindrical_grab_point_gizmo_plugin = null

	if _spherical_grab_point_gizmo_plugin != null:
		remove_node_3d_gizmo_plugin(_spherical_grab_point_gizmo_plugin)
		_spherical_grab_point_gizmo_plugin = null


func _on_selection_changed() -> void:
	var selection := get_editor_interface().get_selection()
	var selected_nodes := selection.get_selected_nodes()
	var nodes_to_update: Array[Node] = []
	nodes_to_update.append_array(_last_selected_nodes)
	nodes_to_update.append_array(selected_nodes)
	_last_selected_nodes = selected_nodes

	for node in nodes_to_update:
		if node is Node3D and is_instance_valid(node):
			(node as Node3D).update_gizmos()
