extends SceneTree

# Diagnostic probe for the static-skinned-mesh surface-override flush question.
#
# Question: on this engine (4.7.2), does installing a surface override material on a
# SKINNED mesh whose skeleton is fully static (editor-equivalent: no animation running)
# reach the renderer, and which flush candidates make it visible?
#
# Run WITHOUT --headless (needs a real renderer):
#   godot-mono -d -s --xr-mode off --path game "tests/markings/static_flush_probe.gd"

const CharacterScene := "res://assets/characters/templates/reference_female/reference_female_base.tscn"
const EnvironmentScene := "res://assets/testing/test_environment.tscn"
const OutputDir := "res://temp/markings"

# Chest strip facts discovered in v1 (same as prisoner_number_capture.gd).
const ChestCentreU := 0.3799
const ChestCentreV := 0.0806

const GarmentMeshPaths := [
	"Female/GeneralSkeleton/Female_wolgade_female_top_01",
	"Female/GeneralSkeleton/Female_cortu_jeans_shorts",
	"Female/GeneralSkeleton/Female_short01",
]

const AnimationFreezePaths := ["AnimationTree", "Female/GeneralSkeleton/DynamicPhysicalRig"]


func _init() -> void:
	var holder := Node3D.new()
	holder.name = "StaticFlushProbe"
	holder.add_child(load(EnvironmentScene).instantiate())
	root.add_child(holder)

	var character: CharacterBody3D = load(CharacterScene).instantiate()
	freeze_animation(character)
	holder.add_child(character)
	await process_frame
	await process_frame

	var body_mesh: MeshInstance3D = character.get_node("Female/GeneralSkeleton/Female_body")
	var skeleton: Skeleton3D = character.get_node("Female/GeneralSkeleton")
	print("body mesh=", body_mesh.get_path(), " skeleton bones=", skeleton.get_bone_count())
	print("skin reference=", body_mesh.get_skin_reference())

	# Hide garments so the chest band is unoccluded; they are separate meshes and do not
	# participate in the flush path under probe.
	for path in GarmentMeshPaths:
		var garment: MeshInstance3D = character.get_node_or_null(path)
		if garment != null:
			garment.visible = false

	var albedo_base: Texture2D = body_mesh.get_active_material(0).get("albedo_texture")
	print("albedo base=", albedo_base.resource_path)

	var aim := discover_chest_position(body_mesh)
	print("chest aim=", aim)

	var camera := Camera3D.new()
	camera.fov = 30.0
	camera.near = 0.01
	holder.add_child(camera)
	await process_frame
	camera.global_position = aim + Vector3(0.0, 0.02, -0.9)
	camera.look_at(aim, Vector3.UP)

	# Hold static long enough that any initial skeleton/skin upload settles.
	await wait_frames(20)
	var unmarked := capture()
	unmarked.save_png("%s/static_probe_unmarked.png" % OutputDir)
	print("captured unmarked baseline")

	# Production path: the template's authored SurfaceMarkings node bakes + installs.
	var markings: Node = character.get_node("SurfaceMarkings")
	markings.Overlay = build_big_ink_overlay(albedo_base)
	markings.Apply()
	var installed: Material = body_mesh.get_surface_override_material(0)
	print("installed override=", installed, " baked=", installed.get("albedo_texture") if installed else null)

	await wait_frames(20)
	var marked_noflush := capture()
	marked_noflush.save_png("%s/static_probe_marked_noflush.png" % OutputDir)
	print("RESULT no-flush ink score=", ink_score(unmarked, marked_noflush))

	# Flush candidate B: nudge every bone pose to itself (forces skeleton dirty + update).
	var nudged := await try_flush(
		"bone-nudge",
		func() -> void:
			for bone in skeleton.get_bone_count():
				skeleton.set_bone_pose(bone, skeleton.get_bone_pose(bone)),
		unmarked)
	if not nudged:
		# Flush candidate C: re-resolve the skin (instance_attach_skeleton re-submit).
		nudged = await try_flush(
			"skeleton-path-reset",
			func() -> void:
				body_mesh.skeleton_path = body_mesh.skeleton_path,
			unmarked)
	if not nudged:
		# Flush candidate D: mesh null/restore round-trip (full mesh-instance rebuild).
		nudged = await try_flush(
			"mesh-roundtrip",
			func() -> void:
				var mesh := body_mesh.mesh
				body_mesh.mesh = null
				body_mesh.mesh = mesh,
			unmarked)
	if not nudged:
		print("RESULT all flush candidates failed to make the override visible")
	quit(0)


func try_flush(candidate_name: String, flush: Callable, unmarked: Image) -> bool:
	flush.call()
	await wait_frames(20)
	var shot := capture()
	shot.save_png("%s/static_probe_flush_%s.png" % [OutputDir, candidate_name])
	var score := ink_score(unmarked, shot)
	print("RESULT flush=%s ink score=%d" % [candidate_name, score])
	return score > 50


func freeze_animation(character: Node) -> void:
	# Editor equivalence: the editor viewport never advances animation or physics rigs,
	# so the skeleton is fully static. Remove the drivers of bone motion.
	for path in AnimationFreezePaths:
		var node := character.get_node_or_null(path)
		if node != null:
			character.remove_child(node)


func build_big_ink_overlay(albedo_base: Texture2D) -> Resource:
	var image := Image.create_empty(32, 32, false, Image.FORMAT_RGBA8)
	image.fill(Color(0, 0, 0, 1))
	var marking := SurfaceMarking.new()
	marking.Position = Vector2(ChestCentreU, ChestCentreV)
	marking.Scale = Vector2(0.10, 0.045)
	marking.AlbedoTexture = ImageTexture.create_from_image(image)
	marking.Tint = Color(0.05, 0.05, 0.05)
	marking.Strength = 1.0

	var layer := MarkingLayer.new()
	layer.Name = "Probe"
	layer.Enabled = true
	layer.Markings = [marking]

	var overlay := MarkingOverlay.new()
	overlay.AlbedoBase = albedo_base
	overlay.Layers = [layer]
	return overlay


func discover_chest_position(body_mesh: MeshInstance3D) -> Vector3:
	var vertices: PackedVector3Array = body_mesh.mesh.surface_get_arrays(0)[Mesh.ARRAY_VERTEX]
	var sum := Vector3.ZERO
	var count := 0
	for vertex in vertices:
		if vertex.y < 1.35 or vertex.y > 1.40 or absf(vertex.x) > 0.13 or vertex.z > 0.09:
			continue
		sum += vertex
		count += 1
	return body_mesh.global_transform * (sum / maxf(count, 1.0))


func capture() -> Image:
	return root.get_texture().get_image()


func ink_score(unmarked: Image, marked: Image) -> int:
	var score := 0
	for y in range(0, marked.get_height(), 2):
		for x in range(0, marked.get_width(), 2):
			if unmarked.get_pixel(x, y).v - marked.get_pixel(x, y).v > 0.3:
				score += 1
	return score


func wait_frames(frame_count: int) -> void:
	for frame in frame_count:
		await process_frame
