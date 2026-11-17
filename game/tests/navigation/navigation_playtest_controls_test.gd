extends SceneTree
## Focused synthetic-input regression through the real scene's viewport/GUI boundary.
## Run with navigation_test.tscn as the scene argument, --xr-mode off and -- --no-ai.

var scene: Node3D
var failures: Array[String] = []
var checks := 0
var _finished := false

func _initialize() -> void:
	call_deferred("run")

func check(condition: bool, message: String) -> void:
	checks += 1
	if not condition:
		failures.append(message)
		push_error(message)

func settle() -> void:
	await physics_frame
	await physics_frame
	await process_frame

func motion(position: Vector2, relative := Vector2.ZERO) -> void:
	var event := InputEventMouseMotion.new()
	event.position = position
	event.relative = relative
	root.push_input(event, true)

func button(position: Vector2, index: int, down: bool, shift := false) -> void:
	var event := InputEventMouseButton.new()
	event.position = position
	event.button_index = index
	event.pressed = down
	event.shift_pressed = shift
	root.push_input(event, true)

func escape() -> void:
	var event := InputEventKey.new()
	event.keycode = KEY_ESCAPE
	event.pressed = true
	root.push_input(event, true)

func run() -> void:
	# Bound setup failures without turning a fixed settling delay into a gameplay oracle.
	create_timer(30.0).timeout.connect(func():
		if not _finished:
			push_error("Desktop-controls regression exceeded its 30-second setup/run budget")
			root.get_node("Global").RequestExit(1))
	scene = current_scene
	while not scene.is_processing_unhandled_input(): await settle()
	await settle()
	var nav = scene.npc.get("Navigation")
	var camera: Camera3D = scene.camera
	var marker: Node3D = scene.marker
	var a := camera.unproject_position(Vector3(-1, 0, -2))
	var b := camera.unproject_position(Vector3(-2, 0, -2))
	motion(a)
	await settle()
	check(marker.visible and marker.global_position.distance_to(Vector3(-1, 0, -2)) < 0.001, "Hover projects onto real ground before click")
	motion(b)
	await settle()
	check(marker.global_position.distance_to(Vector3(-2, 0, -2)) < 0.001, "Hover tracks pointer")
	var original := camera.global_transform
	button(a, MOUSE_BUTTON_RIGHT, true)
	motion(a + Vector2(20, 10), Vector2(20, 10))
	button(a, MOUSE_BUTTON_RIGHT, false)
	check(not camera.global_transform.is_equal_approx(original), "Right drag changes view")
	var released := camera.global_transform
	motion(a + Vector2(30, 10), Vector2(10, 0))
	check(camera.global_transform.is_equal_approx(released), "Orbit release stops gesture")
	button(a, MOUSE_BUTTON_MIDDLE, true)
	motion(a + Vector2(10, 5), Vector2(10, 5))
	button(a, MOUSE_BUTTON_MIDDLE, false)
	check(not camera.global_position.is_equal_approx(released.origin), "Middle drag pans")
	var distance: float = scene._distance
	button(a, MOUSE_BUTTON_WHEEL_UP, true)
	check(scene._distance < distance, "Wheel up zooms in")
	button(a, MOUSE_BUTTON_WHEEL_DOWN, true)
	check(is_equal_approx(scene._distance, distance), "Wheel down restores distance")
	check(not nav.get("HasDestination"), "Camera controls do not accept navigation")
	a = camera.unproject_position(Vector3(-1, 0, -2))
	b = camera.unproject_position(Vector3(-2, 0, -2))
	button(a, MOUSE_BUTTON_LEFT, true)
	motion(b)
	await settle()
	check(scene.pressed and marker.global_position.distance_to(Vector3(-1, 0, -2)) < 0.001, "Press locks position through drag")
	check((-marker.global_basis.z).distance_to(Vector3.LEFT) < 0.001, "Drag previews independent world-left facing")
	check(marker.get_node("MarkerDisc").material_override.albedo_color == Color(1, 0.65, 0.08, 0.82), "Pressed marker is amber")
	check(not nav.get("HasDestination"), "Press and drag do not submit")
	var ui: Vector2 = scene._box.get_node("Speed").get_global_rect().get_center()
	button(ui, MOUSE_BUTTON_LEFT, false)
	await settle()
	check(not scene.pressed and not nav.get("HasDestination"), "Release over UI cancels without submitting")
	button(ui, MOUSE_BUTTON_LEFT, true)
	button(a, MOUSE_BUTTON_LEFT, false)
	await settle()
	check(not scene.pressed and not nav.get("HasDestination"), "UI press then world release cannot start placement")
	button(a, MOUSE_BUTTON_RIGHT, true)
	button(ui, MOUSE_BUTTON_RIGHT, false)
	released = camera.global_transform
	motion(b, Vector2(30, 20))
	check(camera.global_transform.is_equal_approx(released), "Orbit release over UI stops gesture")
	button(a, MOUSE_BUTTON_MIDDLE, true)
	button(ui, MOUSE_BUTTON_MIDDLE, false)
	released = camera.global_transform
	motion(b, Vector2(30, 20))
	check(camera.global_transform.is_equal_approx(released), "Pan release over UI stops gesture")
	button(a, MOUSE_BUTTON_LEFT, true, true)
	button(a, MOUSE_BUTTON_LEFT, false, true)
	button(b, MOUSE_BUTTON_LEFT, true, true)
	button(b, MOUSE_BUTTON_LEFT, false, true)
	await settle()
	check(scene.vias.size() == 2 and scene._via_markers.size() == 2, "Two visible ordered waypoint nodes")
	check(scene.vias[0].distance_to(Vector3(-1, 0, -2)) < 0.001 and scene.vias[1].distance_to(Vector3(-2, 0, -2)) < 0.001, "Waypoint order preserves clicks")
	check(scene._via_markers[0].visible and scene._via_markers[1].visible and not nav.get("HasDestination"), "Waypoints visible without destination submission")
	escape()
	await settle()
	check(scene.vias.is_empty() and scene._via_markers.is_empty(), "Escape clears waypoint queue and visuals")
	button(a, MOUSE_BUTTON_LEFT, true)
	await settle()
	escape()
	button(b, MOUSE_BUTTON_LEFT, false)
	await settle()
	check(not scene.pressed and not nav.get("HasDestination"), "Escape cancels placement even before later release")
	motion(Vector2(root.size.x - 1, 1))
	await settle()
	check(not marker.visible, "Invalid physics hit hides preview")
	# Valid route submission remains on production navigation; this is not a fake request counter.
	var via_screen := camera.unproject_position(Vector3(-0.5, 0, -1))
	button(via_screen, MOUSE_BUTTON_LEFT, true, true)
	button(via_screen, MOUSE_BUTTON_LEFT, false, true)
	await settle()
	check(scene.vias.size() == 1, "Waypoint remains queued until submission")
	button(a, MOUSE_BUTTON_LEFT, true)
	motion(b)
	button(b, MOUSE_BUTTON_LEFT, false)
	await settle()
	check(nav.get("HasDestination"), "Valid release accepted by real navigator")
	var destination: Transform3D = nav.get("Destination")
	check(destination.origin.distance_to(Vector3(-1, 0, -2)) < 0.001, "Submitted destination keeps locked point")
	check((-destination.basis.z).distance_to(Vector3.LEFT) < 0.001, "Submitted facing matches live preview")
	check(not marker.visible, "Running navigation hides free preview")
	check(scene.vias.is_empty() and scene._via_markers.is_empty(), "Submission consumes waypoint queue and visuals")
	_finished = true
	print("PLAYTEST_CONTROLS_RESULT ", JSON.stringify({"checks": checks, "failures": failures}))
	root.get_node("Global").RequestExit(0 if failures.is_empty() else 1)
