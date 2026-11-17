extends SceneTree
## Render-only evidence runner. The C# probe scripts explicit intent on the normal existing NPC.

const BASE := "res://temp/motionbricks/source-pipeline-05/reviewed-playback-01/"
var scene: Node3D
var actor: Node3D
var probe
var views: Array[SubViewport] = []
var cameras: Array[Camera3D] = []
var records: Array = []
var next_capture := 0.0
var busy := false
var ending := false
var mode := "framing"
var output := ""
var complete_forward := false
var begun := false

func _initialize() -> void:
	mode = "run" if "--observe" in OS.get_cmdline_user_args() else "framing"
	complete_forward = "--complete-forward" in OS.get_cmdline_user_args()
	output = ("res://temp/motionbricks/source-pipeline-05/complete-forward-01/" if complete_forward else BASE) + mode
	assert(not DirAccess.dir_exists_absolute(output), "Preserve existing capture outputs")
	DirAccess.make_dir_recursive_absolute(output)
	if complete_forward:
		probe = load("res://tests/motionbricks/ReviewedPlaybackProbe.cs").new()
		probe.set("CompleteForward", true)
		root.add_child(probe)
	call_deferred("start")

func start() -> void:
	await process_frame
	scene = current_scene
	actor = scene.get_node("NavigationTestNpc")
	for i in range(2):
		var viewport := SubViewport.new()
		viewport.size = Vector2i(480, 480)
		viewport.world_3d = scene.get_world_3d()
		viewport.render_target_update_mode = SubViewport.UPDATE_ALWAYS
		root.add_child(viewport)
		var camera := Camera3D.new()
		camera.fov = 38
		viewport.add_child(camera)
		camera.make_current()
		views.append(viewport)
		cameras.append(camera)
	add_marker("Forward +Z Source / -Z World", Vector3(0, 0.01, -0.75), Color.GREEN)
	add_marker("Left +X Source / -X World", Vector3(-0.75, 0.01, 0), Color.BLUE)
	frame_cameras()
	await physics_frame
	await physics_frame
	if complete_forward:
		assert(probe.get("CapturedNavigationNodes") == 1, "Native observer must bind before normal worker startup")
	await RenderingServer.frame_post_draw
	if mode == "framing":
		for i in range(2):
			views[i].get_texture().get_image().save_png(output + ("/front.png" if i == 0 else "/oblique.png"))
		FileAccess.open(output + "/setup.json", FileAccess.WRITE).store_string(JSON.stringify({
			"actor": str(actor.global_transform), "forward_marker_world": [0, 0.01, -0.75],
			"left_marker_world": [-0.75, 0.01, 0], "cameras": cameras.map(func(c): return str(c.global_transform))}))
		root.get_node("Global").RequestExit(0)
		return
	if probe == null:
		probe = load("res://tests/motionbricks/ReviewedPlaybackProbe.cs").new()
		scene.add_child(probe)
	probe.Begin(actor)
	begun = true

func add_marker(text: String, position: Vector3, colour: Color) -> void:
	var marker := MeshInstance3D.new()
	var mesh := SphereMesh.new()
	mesh.radius = 0.025
	mesh.height = 0.05
	marker.mesh = mesh
	var material := StandardMaterial3D.new()
	material.albedo_color = colour
	marker.material_override = material
	scene.add_child(marker)
	marker.global_position = actor.global_position + position
	var label := Label3D.new()
	label.text = text
	label.font_size = 24
	label.pixel_size = 0.002
	label.billboard = BaseMaterial3D.BILLBOARD_ENABLED
	marker.add_child(label)
	label.position.y = 0.07

func frame_cameras() -> void:
	for i in range(cameras.size()):
		cameras[i].global_position = actor.global_position + (Vector3(0, 1.45, -3.6) if i == 0 else Vector3(-2.1, 1.6, -3.1))
		cameras[i].look_at(actor.global_position + Vector3(0, 0.85, 0))

func _process(_delta: float) -> bool:
	if not begun or probe == null or ending:
		return false
	frame_cameras()
	if not busy and probe.get("Time") >= next_capture:
		next_capture = probe.get("Time") + 0.1
		capture()
	if probe.get("Finished"):
		ending = true
		finish()
	return false

func capture() -> void:
	busy = true
	await RenderingServer.frame_post_draw
	var index := records.size()
	for i in range(2):
		views[i].get_texture().get_image().save_png(output + ("/front_" if i == 0 else "/oblique_") + "%04d.png" % index)
	records.append({"index": index, "time": probe.get("Time"), "state": probe.get("State")})
	busy = false

func finish() -> void:
	await RenderingServer.frame_post_draw
	probe.Save(ProjectSettings.globalize_path(output + "/trace.json"))
	FileAccess.open(output + "/captures.json", FileAccess.WRITE).store_string(JSON.stringify(records))
	FileAccess.open(output + "/result.json", FileAccess.WRITE).store_string(JSON.stringify({
		"passed": probe.get("Passed"), "failure": probe.get("Failure"), "time": probe.get("Time"),
		"settled": probe.get("Settled"), "frames_per_view": records.size()}))
	root.get_node("Global").RequestExit(0 if probe.get("Passed") else 1)
