@tool
extends EditorPlugin

## Spatial Minimap editor tooling (SCN-002 TR-17). The bottom-panel preview opens on the whole authored coverage
## of the edited scene, supports drag-to-pan and zoom in/out layered on the automatic scale, refreshes through the
## production MiniMapRenderer via the MiniMapPreviewDriver bridge, and never presents a stale image after a failed
## refresh. All rendering goes through the same composition the runtime uses; only the viewport driver inputs differ.
##
## Evidence mode: setting the MINIMAP_EDITOR_EVIDENCE environment variable to an output tag makes the plugin capture
## editor-window screenshots (open/pan/zoom-in/zoom-out) under res://temp/<tag>/ and quit, for review evidence.

const FootprintGizmo = preload("res://addons/spatial_minimap/footprint_gizmo.gd")
const PreviewDriver = preload("res://src/Scene/MiniMap/MiniMapPreviewDriver.cs")

const STALE_REFRESH_SECONDS := 0.3
const ZOOM_FACTOR := 2.0
const MINIMUM_WORLD_SIZE := 0.05
const MAXIMUM_WORLD_SIZE := 200000.0
const EVIDENCE_ENVIRONMENT := "MINIMAP_EDITOR_EVIDENCE"
const EVIDENCE_SCENE := "res://tests/spatial_minimap/demo.tscn"

var _panel: VBoxContainer
var _image: TextureRect
var _status: Label
var _diagnostics: Label
var _width: SpinBox
var _height: SpinBox
var _gizmo: EditorNode3DGizmoPlugin
var _driver: RefCounted
var _scene_root: Node
var _viewport := Rect2()
var _scale := 0.0
var _refresh_timer: Timer
var _dragging := false


func _enter_tree() -> void:
	_driver = PreviewDriver.new()
	_build_panel()
	_refresh_timer = Timer.new()
	_refresh_timer.wait_time = STALE_REFRESH_SECONDS
	_refresh_timer.one_shot = true
	_refresh_timer.timeout.connect(_refresh)
	add_child(_refresh_timer)
	_gizmo = FootprintGizmo.new()
	add_node_3d_gizmo_plugin(_gizmo)
	scene_changed.connect(_on_scene_changed)
	scene_closed.connect(_on_scene_closed)
	EditorInterface.get_inspector().property_edited.connect(_on_property_edited)
	get_undo_redo().history_changed.connect(_mark_stale)
	get_undo_redo().version_changed.connect(_mark_stale)
	_on_scene_changed(EditorInterface.get_edited_scene_root())
	var evidence_tag := OS.get_environment(EVIDENCE_ENVIRONMENT)
	if evidence_tag != "":
		_run_evidence_sequence(evidence_tag)


func _exit_tree() -> void:
	EditorInterface.get_inspector().property_edited.disconnect(_on_property_edited)
	get_undo_redo().history_changed.disconnect(_mark_stale)
	get_undo_redo().version_changed.disconnect(_mark_stale)
	scene_changed.disconnect(_on_scene_changed)
	scene_closed.disconnect(_on_scene_closed)
	remove_node_3d_gizmo_plugin(_gizmo)
	_release_preview_resources()
	remove_control_from_bottom_panel(_panel)
	_panel.queue_free()
	_driver = null
	_scene_root = null


func _build_panel() -> void:
	_panel = VBoxContainer.new()
	_panel.custom_minimum_size.y = 420
	var controls := HBoxContainer.new()
	_panel.add_child(controls)
	var refresh := Button.new()
	refresh.text = "Refresh"
	refresh.pressed.connect(_refresh)
	controls.add_child(refresh)
	var zoom_in := Button.new()
	zoom_in.text = "Zoom In"
	zoom_in.pressed.connect(func() -> void: _zoom_by(1.0 / ZOOM_FACTOR))
	controls.add_child(zoom_in)
	var zoom_out := Button.new()
	zoom_out.text = "Zoom Out"
	zoom_out.pressed.connect(func() -> void: _zoom_by(ZOOM_FACTOR))
	controls.add_child(zoom_out)
	var coverage := Button.new()
	coverage.text = "Whole Coverage"
	coverage.pressed.connect(_render_coverage)
	controls.add_child(coverage)
	_width = _number(controls, 1024, 16, 16384, "Output width in pixels")
	_height = _number(controls, 700, 16, 16384, "Output height in pixels")
	_status = Label.new()
	_status.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_status.clip_text = true
	controls.add_child(_status)
	_diagnostics = Label.new()
	_diagnostics.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_diagnostics.max_lines_visible = 2
	_panel.add_child(_diagnostics)
	_image = TextureRect.new()
	_image.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_image.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	_image.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_image.mouse_filter = Control.MOUSE_FILTER_STOP
	_image.gui_input.connect(_on_image_gui_input)
	_panel.add_child(_image)
	add_control_to_bottom_panel(_panel, "Spatial Minimap")


func _number(parent: Node, value: float, minimum: float, maximum: float, hint: String) -> SpinBox:
	var control := SpinBox.new()
	control.min_value = minimum
	control.max_value = maximum
	control.step = 1
	control.value = value
	control.custom_minimum_size.x = 86
	control.tooltip_text = hint
	control.value_changed.connect(func(_value: float) -> void: _mark_stale())
	parent.add_child(control)
	return control


func _current_scene_root() -> Node:
	var root := EditorInterface.get_edited_scene_root()
	if root == null or not is_instance_valid(root) or root.is_queued_for_deletion():
		return null
	return root


func _on_scene_changed(root: Node) -> void:
	_release_preview_resources()
	_scene_root = root
	_viewport = Rect2()
	_scale = 0.0
	if root == null:
		_set_status("No scene open")
		return
	_render_coverage()


func _on_scene_closed(_path: String) -> void:
	_on_scene_changed(EditorInterface.get_edited_scene_root())


func _on_property_edited(_property: StringName) -> void:
	_mark_stale()
	for node in EditorInterface.get_selection().get_selected_nodes():
		if node is Node3D:
			node.update_gizmos()


func _mark_stale() -> void:
	if _panel == null:
		return
	_set_status("STALE - edited state - refreshing...")
	_refresh_timer.start()


func _release_preview_resources() -> void:
	if _image != null:
		_image.texture = null
	if _diagnostics != null:
		_diagnostics.text = ""
	_viewport = Rect2()
	_scale = 0.0


func _set_status(message: String) -> void:
	if _status != null:
		_status.text = message


func _refresh() -> void:
	if _viewport.size.x > 0.0 and _viewport.size.y > 0.0:
		_render_viewport()
	else:
		_render_coverage()


func _render_coverage() -> void:
	var root := _current_scene_root()
	if root == null:
		_release_preview_resources()
		_set_status("No scene open")
		return
	_image.texture = null
	var result: Dictionary = _driver.call("RenderCoverage", root, int(_width.value), int(_height.value))
	_apply_result(result)


func _render_viewport() -> void:
	var root := _current_scene_root()
	if root == null or _viewport.size.x <= 0.0 or _viewport.size.y <= 0.0:
		_release_preview_resources()
		_set_status("No viewport - open Whole Coverage first")
		return
	_image.texture = null
	var result: Dictionary = _driver.call("RenderViewport", root, _viewport, int(_width.value), int(_height.value))
	_apply_result(result)


func _apply_result(result: Dictionary) -> void:
	_diagnostics.text = str(result.get("diagnostics", ""))
	_diagnostics.tooltip_text = _diagnostics.text
	if str(result.get("status", "")) != "Ready" or not result.has("texture"):
		# A failed refresh never leaves a stale image behind (TR-17).
		_image.texture = null
		_viewport = Rect2()
		_scale = 0.0
		_set_status("FAILED - " + str(result.get("diagnostics", "render failed")))
		return
	_image.texture = result["texture"]
	_viewport = result["viewport"]
	_scale = float(result["scale"])
	var step := str(result["grid_step"])
	_set_status("Ready - grid %s m - scale %.1f px/m" % [step, _scale])


func _on_image_gui_input(event: InputEvent) -> void:
	if event is InputEventMouseButton and event.button_index == MOUSE_BUTTON_LEFT:
		if event.pressed:
			_dragging = true
		elif _dragging:
			_dragging = false
			_refresh()
	elif event is InputEventMouseMotion and _dragging and _scale > 0.0:
		_apply_pan(event.relative)
		_set_status("Panning...")
		_refresh_timer.start()


func _apply_pan(pixel_delta: Vector2) -> void:
	if _scale <= 0.0:
		return
	var world_delta := Vector2(-pixel_delta.x, -pixel_delta.y) / _scale
	_viewport = Rect2(_viewport.position + world_delta, _viewport.size)


func _zoom_by(size_factor: float) -> void:
	if _viewport.size.x <= 0.0 or _viewport.size.y <= 0.0:
		return
	var centre := _viewport.get_center()
	var size := _viewport.size * size_factor
	size.x = clampf(size.x, MINIMUM_WORLD_SIZE, MAXIMUM_WORLD_SIZE)
	size.y = clampf(size.y, MINIMUM_WORLD_SIZE, MAXIMUM_WORLD_SIZE)
	_viewport = Rect2(centre - size / 2.0, size)
	_refresh()


func _run_evidence_sequence(output_tag: String) -> void:
	print("MINIMAP_EDITOR_EVIDENCE_STARTED settling for editor startup")
	for _frame in 90:
		await get_tree().process_frame
	print("MINIMAP_EDITOR_EVIDENCE_OPENING %s" % EVIDENCE_SCENE)
	EditorInterface.open_scene_from_path(EVIDENCE_SCENE)
	for _frame in 30:
		await get_tree().process_frame
	var output_dir := "res://temp/%s" % output_tag
	DirAccess.make_dir_recursive_absolute(output_dir)
	print("MINIMAP_EDITOR_EVIDENCE_PANEL opening panel")
	make_bottom_panel_item_visible(_panel)
	_render_coverage()
	await _settle()
	print("MINIMAP_EDITOR_EVIDENCE_STAGE open")
	await _capture_editor("%s/editor_open.png" % output_dir)
	var opened := _viewport
	_apply_pan(Vector2(140, 80))
	_refresh()
	await _settle()
	print("MINIMAP_EDITOR_EVIDENCE_STAGE pan")
	await _capture_editor("%s/editor_pan.png" % output_dir)
	_viewport = opened
	_zoom_by(0.25)
	_viewport = Rect2(Vector2(-1, -1) - _viewport.size / 2.0, _viewport.size)
	_refresh()
	await _settle()
	print("MINIMAP_EDITOR_EVIDENCE_STAGE zoom_in")
	await _capture_editor("%s/editor_zoom_in.png" % output_dir)
	_viewport = opened
	_zoom_by(ZOOM_FACTOR)
	await _settle()
	print("MINIMAP_EDITOR_EVIDENCE_STAGE zoom_out")
	await _capture_editor("%s/editor_zoom_out.png" % output_dir)
	print("MINIMAP_EDITOR_EVIDENCE_COMPLETE %s" % output_dir)
	get_tree().quit()


func _settle() -> void:
	for _frame in 25:
		await get_tree().process_frame


func _capture_editor(path: String) -> void:
	print("MINIMAP_EDITOR_EVIDENCE_CAPTURING %s" % path)
	var image := get_viewport().get_texture().get_image()
	var error := image.save_png(path)
	if error != OK:
		push_error("Minimap editor evidence capture failed (%s): %s" % [error, path])
	else:
		print("MINIMAP_EDITOR_EVIDENCE_CAPTURED %s" % path)
