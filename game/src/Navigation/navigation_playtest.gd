extends Node3D
## Desktop playtest controls; navigation and generated movement remain owned by the NPC.

var pressed := false
var destination := Vector3.ZERO
var vias: Array[Vector3] = []
var _via_markers: Array[Node3D] = []
var _placements: Array[Dictionary] = []
var _orbiting := false
var _panning := false
var _has_pointer := false
var _pointer := Vector2.ZERO
var _origin := Vector3.ZERO
var _distance := 4.0
var _yaw := 0.0
var _pitch := 0.0
var _facing_probe := Vector3.ZERO
var _box: VBoxContainer
var _interactive_controls: Array[Control] = []
var _preview_material: Material
var _pressed_material: StandardMaterial3D
var _ray := PhysicsRayQueryParameters3D.new()
var recorder
var _recording_status: Label
var _status_elapsed := 0.0
@onready var camera: Camera3D = $Camera3D
@onready var marker: Node3D = $DestinationMarker
@onready var npc = $NavigationTestNpc

func _init() -> void:
	# Bind the observer before the normal NPC's deferred navigation/worker startup.
	recorder = load("res://src/Navigation/Recording/NavigationPlaytestRecorder.cs").new()
	recorder.name = "SessionRecorder"
	add_child(recorder)

func _ready() -> void:
	_origin = npc.global_position
	var offset := camera.global_position - _origin
	_distance = clampf(offset.length(), 2.0, 10.0)
	_yaw = atan2(offset.x, offset.z)
	_pitch = clampf(asin(offset.normalized().y), 0.15, 1.35)
	_update_camera()
	camera.make_current()
	_ray.collision_mask = 1
	_preview_material = $DestinationMarker/MarkerDisc.material_override
	_pressed_material = _preview_material.duplicate()
	_pressed_material.albedo_color = Color(1, 0.65, 0.08, 0.82)
	var layer := CanvasLayer.new()
	add_child(layer)
	_box = VBoxContainer.new()
	_box.position = Vector2(12, 12)
	_box.size.x = 340
	_box.mouse_filter = Control.MOUSE_FILTER_IGNORE
	layer.add_child(_box)
	var label := Label.new()
	label.mouse_filter = Control.MOUSE_FILTER_IGNORE
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.clip_text = true
	label.custom_minimum_size.y = 100
	label.max_lines_visible = 5
	label.add_theme_font_size_override("font_size", 14)
	label.text = "Left-drag: destination / facing · Shift-click: via · Esc: cancel\nRight-drag: orbit · Middle-drag: pan · Wheel: zoom\nSpeed intent"
	_box.add_child(label)
	var speed := HSlider.new()
	speed.name = "Speed"
	speed.min_value = 0.1
	speed.max_value = 1.0
	speed.step = 0.05
	speed.value = 1.0
	speed.custom_minimum_size.x = 240
	speed.size_flags_horizontal = Control.SIZE_SHRINK_BEGIN
	_box.add_child(speed)
	_interactive_controls.append(speed)
	speed.value_changed.connect(func(value):
		recorder.Control("speed", value)
		if npc.get("Navigation") != null: npc.get("Navigation").set("NormalisedSpeed", value))
	var actions := HBoxContainer.new()
	actions.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_box.add_child(actions)
	var bookmark := Button.new()
	bookmark.name = "MarkIssue"
	bookmark.text = "Mark issue"
	bookmark.pressed.connect(func(): recorder.MarkIssue())
	actions.add_child(bookmark)
	_interactive_controls.append(bookmark)
	var save := Button.new()
	save.name = "SaveFeedback"
	save.text = "Save feedback"
	save.pressed.connect(func(): recorder.SaveFeedback())
	actions.add_child(save)
	_interactive_controls.append(save)
	_recording_status = Label.new()
	_recording_status.name = "RecordingStatus"
	_recording_status.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	_recording_status.mouse_filter = Control.MOUSE_FILTER_IGNORE
	_recording_status.clip_text = true
	_recording_status.max_lines_visible = 6
	_recording_status.custom_minimum_size.y = 132
	_recording_status.add_theme_font_size_override("font_size", 14)
	_box.add_child(_recording_status)
	_recording_status.text = recorder.StatusText
	_record_view()

func _process(delta: float) -> void:
	_status_elapsed += delta
	if _status_elapsed >= 0.25 and _recording_status != null:
		_status_elapsed = 0.0
		_recording_status.text = recorder.StatusText

func _record_view() -> void:
	recorder.ViewState(_origin, _distance, _yaw, _pitch, _orbiting, _panning)

func ground_at(screen: Vector2) -> Variant:
	# Call only during physics processing: the space may be locked during input callbacks.
	_ray.from = camera.project_ray_origin(screen)
	_ray.to = _ray.from + camera.project_ray_normal(screen) * 1000.0
	var hit := get_world_3d().direct_space_state.intersect_ray(_ray)
	return hit.get("position")

func _over_ui(screen: Vector2) -> bool:
	for control in _interactive_controls:
		if control.is_visible_in_tree() and control.get_global_rect().has_point(screen):
			return true
	return false

func _input(event: InputEvent) -> void:
	# Releases must be seen even when the GUI consumes them. Never start a world gesture here.
	if event is InputEventKey and event.pressed and event.keycode == KEY_ESCAPE:
		_cancel_gestures()
		_clear_vias()
		var nav = npc.get("Navigation")
		recorder.Control("cancel", 0.0)
		if nav != null: nav.ClearDestination()
		get_viewport().set_input_as_handled()
	if event is InputEventMouse:
		_pointer = event.position
		_has_pointer = true
	if event is InputEventMouseButton and not event.pressed:
		match event.button_index:
			MOUSE_BUTTON_RIGHT: _orbiting = false
			MOUSE_BUTTON_MIDDLE: _panning = false
			MOUSE_BUTTON_LEFT:
				if _over_ui(event.position):
					pressed = false
					_placements.clear()
				else:
					_placements.append({"kind": "release", "screen": event.position})
	if event is InputEventMouseMotion:
		if _orbiting:
			_yaw -= event.relative.x * 0.006
			_pitch = clampf(_pitch + event.relative.y * 0.006, 0.15, 1.35)
		if _panning:
			var right := camera.global_basis.x
			var forward := -camera.global_basis.z
			right.y = 0
			forward.y = 0
			_origin += (-right.normalized() * event.relative.x + forward.normalized() * event.relative.y) * 0.01 * _distance
		if _orbiting or _panning: _update_camera()
	if event is InputEventMouse: _record_view()

func _unhandled_input(event: InputEvent) -> void:
	if not event is InputEventMouseButton or not event.pressed or _over_ui(event.position): return
	match event.button_index:
		MOUSE_BUTTON_RIGHT: _orbiting = true
		MOUSE_BUTTON_MIDDLE: _panning = true
		MOUSE_BUTTON_WHEEL_UP:
			_distance = clampf(_distance - 0.75, 2.0, 10.0)
			_update_camera()
		MOUSE_BUTTON_WHEEL_DOWN:
			_distance = clampf(_distance + 0.75, 2.0, 10.0)
			_update_camera()
		MOUSE_BUTTON_LEFT:
			_placements.append({"kind": "via" if event.shift_pressed else "press", "screen": event.position})
	_record_view()

func _physics_process(_delta: float) -> void:
	var nav = npc.get("Navigation")
	for placement in _placements:
		var point = ground_at(placement.screen)
		match placement.kind:
			"via":
				if point != null:
					vias.append(point)
					var via := marker.duplicate() as Node3D
					add_child(via)
					via.global_transform = Transform3D(_facing(point, point), point)
					via.get_node("MarkerDisc").material_override = _preview_material
					via.show()
					_via_markers.append(via)
			"press":
				pressed = point != null
				if pressed:
					destination = point
					_facing_probe = point
			"release":
				if pressed:
					if point != null: _facing_probe = point
					if nav != null:
						_submit_destination(Transform3D(_facing(destination, _facing_probe), destination), PackedVector3Array(vias))
					_clear_vias()
				pressed = false
	_placements.clear()
	var hit = ground_at(_pointer) if _has_pointer and not _over_ui(_pointer) else null
	if pressed and hit != null: _facing_probe = hit
	marker.visible = (pressed or hit != null) and not (nav != null and nav.get("IsNavigationRunning"))
	if pressed:
		marker.global_transform = Transform3D(_facing(destination, _facing_probe), destination)
	elif hit != null:
		marker.global_transform = Transform3D(_facing(hit, hit), hit)
	$DestinationMarker/MarkerDisc.material_override = _pressed_material if pressed else _preview_material

func _submit_destination(target: Transform3D, ordered_vias: PackedVector3Array) -> int:
	var nav = npc.get("Navigation")
	recorder.BeforeCommand(target, ordered_vias)
	var result: int = nav.SelectDestination(target, ordered_vias)
	recorder.AfterCommand(result)
	return result

func _facing(position: Vector3, probe: Vector3) -> Basis:
	var direction := probe - position
	direction.y = 0
	if direction.length_squared() <= 0.01:
		direction = position - npc.global_position
		direction.y = 0
	if direction.length_squared() <= 0.01:
		direction = -npc.global_basis.z
		direction.y = 0
	return Basis.looking_at(direction.normalized()) if direction.length_squared() > 0.000001 else Basis.IDENTITY

func _update_camera() -> void:
	var horizontal := _distance * cos(_pitch)
	camera.global_position = _origin + Vector3(horizontal * sin(_yaw), _distance * sin(_pitch), horizontal * cos(_yaw))
	camera.look_at(_origin)

func _clear_vias() -> void:
	vias.clear()
	for via in _via_markers: via.queue_free()
	_via_markers.clear()

func _cancel_gestures() -> void:
	pressed = false
	_orbiting = false
	_panning = false
	_placements.clear()

func _notification(what: int) -> void:
	if what == NOTIFICATION_APPLICATION_FOCUS_OUT:
		_cancel_gestures()
		_has_pointer = false
