extends SceneTree

# Surface Markings V2 visual evidence runner (fixture-only): a prisoner number "24601"
# composed from five separately-placed seven-segment digit images, baked into per-instance
# texture copies through the production SurfaceMarkings component on the reference female.
#
# Deterministic marked/unmarked pairs use an unskinned bind-pose clone of the body mesh
# (garments are separate meshes, so the clone shows the bare torso); a best-of-N pair covers
# the clothed production character.
#
# Run WITHOUT --headless (screenshot capture needs a real renderer):
#   godot-mono -d -s --xr-mode off --path game "tests/markings/prisoner_number_capture.gd"

const CharacterScene := "res://assets/characters/templates/reference_female/reference_female_base.tscn"
const EnvironmentScene := "res://assets/testing/test_environment.tscn"
const OutputDir := "res://temp/markings"
const PrisonerNumber := "24601"

# Chest strip facts discovered in v1 (tools/dump_uvs.gd filter: bind y 1.35..1.40,
# |x| <= 0.13, z <= 0.09, normal.z > 0.5, garment-exposed).
const ChestCentreU := 0.3799
const ChestCentreV := 0.0806
const DigitWidth := 0.012
const DigitHeight := 0.018
const DigitStep := 0.0165

# Mid-dark ink so the multiply blend keeps underlying skin shading visible through strokes.
const InkTint := Color(0.42, 0.38, 0.46)

const GarmentMeshPaths := [
	"Female/GeneralSkeleton/Female_wolgade_female_top_01",
	"Female/GeneralSkeleton/Female_cortu_jeans_shorts",
	"Female/GeneralSkeleton/Female_short01",
]

# Seven-segment digit definitions: a top, b top-right, c bottom-right, d bottom,
# e bottom-left, f top-left, g middle.
const _segments := {
	"0": "abcdef",
	"1": "bc",
	"2": "abged",
	"3": "abgcd",
	"4": "fgbc",
	"5": "afgcd",
	"6": "afgedc",
	"7": "abc",
	"8": "abcdefg",
	"9": "abcdfg",
}


func _init() -> void:
	var holder := Node3D.new()
	holder.name = "MarkingCapture"
	holder.add_child(load(EnvironmentScene).instantiate())
	var ground := MeshInstance3D.new()
	ground.mesh = PlaneMesh.new()
	ground.mesh.size = Vector2(20, 20)
	ground.material_override = StandardMaterial3D.new()
	ground.material_override.albedo_color = Color(0.32, 0.33, 0.35)
	root.add_child(holder)

	var character: CharacterBody3D = load(CharacterScene).instantiate()
	holder.add_child(character)
	await process_frame
	await process_frame

	# The production character idles, which also keeps surface-state uploads flushing: a fully
	# static skinned mesh would keep material changes from reaching the renderer (v1 finding).
	var body_mesh: MeshInstance3D = character.get_node("Female/GeneralSkeleton/Female_body")
	var albedo_base: Texture2D = body_mesh.get_active_material(0).get("albedo_texture")
	print("albedo base=", albedo_base.resource_path, " size=", albedo_base.get_size())

	var chest := discover_exposed_chest(body_mesh, collect_garment_triangles(character))
	print("chest exposed=%d/%d centre=(%.4f, %.4f)" % [
		chest.exposed_count, chest.candidate_count, chest.uv_centre.x, chest.uv_centre.y])

	var overlay := build_prisoner_overlay(albedo_base, chest)
	DirAccess.make_dir_recursive_absolute(OutputDir)

	# Production path: the template's authored SurfaceMarkings node bakes the overlay.
	var markings: Node = character.get_node("SurfaceMarkings")
	markings.Overlay = overlay
	markings.Apply()
	var applied_override: Material = body_mesh.get_surface_override_material(0)
	print("character override=", applied_override,
		" baked albedo=", applied_override.get("albedo_texture") if applied_override else null)

	# Deterministic evidence subject: an unskinned bind-pose clone of the same body mesh,
	# placed beside the character, with its own component instance baking the same overlay.
	# The clone renders bare (garments are separate meshes and are not cloned).
	var clone := MeshInstance3D.new()
	clone.name = "BodyClone"
	clone.mesh = body_mesh.mesh
	holder.add_child(clone)
	clone.global_transform = body_mesh.global_transform
	clone.global_position = body_mesh.global_position + Vector3(1.3, 0, 0)
	await process_frame

	var clone_markings := SurfaceMarkings.new()
	clone.add_child(clone_markings)
	clone_markings.TargetMesh = clone
	clone_markings.Overlay = overlay
	clone_markings.Apply()
	var clone_override: Material = clone.get_surface_override_material(0)
	print("clone override=", clone_override,
		" baked=", clone_override.get("albedo_texture") if clone_override else null)

	# View the clone's chest strip head-on; aim slightly below the band for the torso framing.
	var clone_aim: Vector3 = chest.world_position + Vector3(1.3, 0, 0) + chest.world_normal * 0.03
	print("clone chest aim=", clone_aim)

	var camera := Camera3D.new()
	camera.fov = 45.0
	camera.near = 0.01
	holder.add_child(camera)
	await process_frame

	# Conversational-distance framing pair on the clone (neck-to-abdomen torso view).
	position_camera(camera, clone_aim + Vector3(0, -0.12, 0), 1.45)
	await wait_render_frames(4)
	await capture_marking_pair(
		camera, clone, clone_override, "unmarked_framing.png", "prisoner_framing.png")

	# Close-up pair on the clone for the ink-blend evidence, then zoomed centre crops.
	position_camera(camera, clone_aim, 0.4)
	await wait_render_frames(4)
	await capture_marking_pair(
		camera, clone, clone_override, "unmarked_closeup.png", "prisoner_closeup.png")
	save_centre_zoom()

	# Production-character pair: the marked clothed reference character herself. The idle
	# sway means the strip reads best in some frames, so keep the pair with the most
	# ink-darkened pixels.
	var character_aim: Vector3 = chest.world_position + chest.world_normal * 0.05
	position_camera(camera, character_aim, 1.45)
	await wait_render_frames(4)
	await capture_best_marking_pair(
		camera, body_mesh, applied_override,
		"unmarked_character_framing.png", "marked_character_framing.png", 6)

	quit(0)


func build_prisoner_overlay(albedo_base: Texture2D, chest: Dictionary) -> Resource:
	var markings: Array = []
	for digit_index in PrisonerNumber.length():
		var digit := PrisonerNumber[digit_index]
		var marking := SurfaceMarking.new()
		marking.Position = Vector2(
			chest.uv_centre.x + (digit_index - float(PrisonerNumber.length() - 1) * 0.5) * DigitStep,
			chest.uv_centre.y)
		marking.Scale = Vector2(DigitWidth, DigitHeight)
		marking.RotationDegrees = 0.0
		marking.AlbedoTexture = create_digit_texture(digit)
		marking.Tint = InkTint
		marking.Strength = 1.0
		markings.append(marking)

	var layer := MarkingLayer.new()
	layer.Name = "Prisoner Number"
	layer.Enabled = true
	layer.Markings = markings

	var overlay := MarkingOverlay.new()
	overlay.AlbedoBase = albedo_base
	overlay.Layers = [layer]
	return overlay


func create_digit_texture(digit: String) -> ImageTexture:
	# One seven-segment digit per small image: white upright strokes on transparent. The
	# per-marking placement composes these modular letter images into the full number.
	var width := 64
	var height := 96
	var stroke := 12
	var image := Image.create_empty(width, height, false, Image.FORMAT_RGBA8)
	image.fill(Color(1, 1, 1, 0))
	draw_seven_segment_digit(
		image, Vector2i(0, 0), width, height, stroke, str(_segments.get(digit, "abcdefg")))
	image.save_png("%s/digit_%s.png" % [OutputDir, digit])
	return ImageTexture.create_from_image(image)


func draw_seven_segment_digit(
		image: Image, origin: Vector2i, width: int, height: int, stroke: int, segments: String
) -> void:
	var right_x := origin.x + width - stroke
	var left_x := origin.x
	var top_y := origin.y
	var mid_y := origin.y + int(height * 0.5 - stroke * 0.5)
	var bottom_y := origin.y + height - stroke
	var fills := {
		"a": Rect2i(left_x, top_y, width, stroke),
		"b": Rect2i(right_x, top_y, stroke, int(height * 0.5)),
		"c": Rect2i(right_x, mid_y, stroke, int(height * 0.5)),
		"d": Rect2i(left_x, bottom_y, width, stroke),
		"e": Rect2i(left_x, mid_y, stroke, int(height * 0.5)),
		"f": Rect2i(left_x, top_y, stroke, int(height * 0.5)),
		"g": Rect2i(left_x, mid_y, width, stroke),
	}
	for segment in segments:
		image.fill_rect(fills[segment], Color(1, 1, 1, 1))


func collect_garment_triangles(character: Node) -> Array:
	var triangles: Array = []
	for path in GarmentMeshPaths:
		var garment: MeshInstance3D = character.get_node_or_null(path)
		if garment == null or garment.mesh == null:
			continue
		var vertices: PackedVector3Array = garment.mesh.surface_get_arrays(0)[Mesh.ARRAY_VERTEX]
		for triangle_index in vertices.size() / 3:
			triangles.append([
				garment.global_transform * vertices[triangle_index * 3],
				garment.global_transform * vertices[triangle_index * 3 + 1],
				garment.global_transform * vertices[triangle_index * 3 + 2],
			])
	return triangles


func discover_exposed_chest(body_mesh: MeshInstance3D, garment_triangles: Array) -> Dictionary:
	var arrays := body_mesh.mesh.surface_get_arrays(0)
	var vertices: PackedVector3Array = arrays[Mesh.ARRAY_VERTEX]
	var normals: PackedVector3Array = arrays[Mesh.ARRAY_NORMAL]
	var uvs: PackedVector2Array = arrays[Mesh.ARRAY_TEX_UV]

	var exposed_uvs: Array[Vector2] = []
	var exposed_positions: Array[Vector3] = []
	var normal_sum := Vector3.ZERO
	var candidate_count := 0
	var body_to_global := body_mesh.global_transform
	for index in vertices.size():
		var vertex := vertices[index]
		var normal := normals[index]
		# Upper-sternum band: above the top garment's neckline and below the throat, so only
		# visibly exposed chest skin on the torso UV island remains.
		if vertex.y < 1.35 or vertex.y > 1.4 or absf(vertex.x) > 0.13 or vertex.z > 0.09 or normal.z <= 0.5:
			continue
		candidate_count += 1
		if is_garment_covered(body_to_global * vertex, body_to_global.basis * normal, garment_triangles):
			continue
		exposed_uvs.append(uvs[index])
		exposed_positions.append(vertex)
		normal_sum += normal

	var us := exposed_uvs.map(func(uv: Vector2): return uv.x)
	var vs := exposed_uvs.map(func(uv: Vector2): return uv.y)
	us.sort()
	vs.sort()
	var uv_min := Vector2(us[int(us.size() * 0.1)], vs[int(vs.size() * 0.1)])
	var uv_max := Vector2(us[int(us.size() * 0.9)], vs[int(vs.size() * 0.9)])
	var position_sum := Vector3.ZERO
	for position in exposed_positions:
		position_sum += position
	var world_normal := body_mesh.global_transform.basis * normal_sum.normalized()
	return {
		"candidate_count": candidate_count,
		"exposed_count": exposed_uvs.size(),
		"uv_min": uv_min,
		"uv_max": uv_max,
		"uv_centre": (uv_min + uv_max) * 0.5,
		"world_position": body_mesh.global_transform * (position_sum / exposed_positions.size()),
		"world_normal": world_normal.normalized(),
	}


func is_garment_covered(surface_point: Vector3, surface_normal: Vector3, garment_triangles: Array) -> bool:
	var segment_end := surface_point + surface_normal * 0.2
	for triangle in garment_triangles:
		if segment_hits_triangle(surface_point, segment_end, triangle[0], triangle[1], triangle[2]):
			return true
	return false


func segment_hits_triangle(
		segment_start: Vector3,
		segment_end: Vector3,
		triangle_a: Vector3,
		triangle_b: Vector3,
		triangle_c: Vector3
) -> bool:
	var edge1 := triangle_b - triangle_a
	var edge2 := triangle_c - triangle_a
	var direction := segment_end - segment_start
	var cross := direction.cross(edge2)
	var determinant := edge1.dot(cross)
	if absf(determinant) < 1e-9:
		return false
	var inverse := 1.0 / determinant
	var difference := segment_start - triangle_a
	var barycentric_u := difference.dot(cross) * inverse
	if barycentric_u < 0.0 or barycentric_u > 1.0:
		return false
	var barycentric_q := difference.cross(edge1)
	var barycentric_v := direction.dot(barycentric_q) * inverse
	if barycentric_v < 0.0 or barycentric_u + barycentric_v > 1.0:
		return false
	var distance := edge2.dot(barycentric_q) * inverse
	return distance >= 0.0 and distance <= 1.0


func capture_marking_pair(
		camera: Camera3D,
		target_mesh: MeshInstance3D,
		applied_override: Material,
		unmarked_name: String,
		marked_name: String
) -> void:
	target_mesh.set_surface_override_material(0, null)
	await wait_render_frames(2)
	save_capture(unmarked_name)
	target_mesh.set_surface_override_material(0, applied_override)
	await wait_render_frames(2)
	save_capture(marked_name)


func capture_best_marking_pair(
		camera: Camera3D,
		target_mesh: MeshInstance3D,
		applied_override: Material,
		unmarked_name: String,
		marked_name: String,
		attempts: int
) -> void:
	# Captures marked/unmarked pairs at the fixed aim and keeps the pair with the most
	# ink-darkened pixels: the production character's idle sway garment-occludes the strip in
	# some frames, and full-strength multiply ink darkens skin far more than pose sway shifts.
	var best_plain: Image = null
	var best_marked: Image = null
	var best_score := -1
	for attempt in attempts:
		target_mesh.set_surface_override_material(0, null)
		await wait_render_frames(2)
		var plain_shot := root.get_texture().get_image()
		target_mesh.set_surface_override_material(0, applied_override)
		await wait_render_frames(2)
		var marked_shot := root.get_texture().get_image()
		var score := 0
		for y in range(0, marked_shot.get_height(), 2):
			for x in range(0, marked_shot.get_width(), 2):
				if plain_shot.get_pixel(x, y).v - marked_shot.get_pixel(x, y).v > 0.3:
					score += 1
		print("pair attempt=%d ink score=%d" % [attempt, score])
		if score > best_score:
			best_score = score
			best_plain = plain_shot
			best_marked = marked_shot
	print("best pair ink score=", best_score)
	best_plain.save_png("%s/%s" % [OutputDir, unmarked_name])
	best_marked.save_png("%s/%s" % [OutputDir, marked_name])


func position_camera(camera: Camera3D, aim: Vector3, distance: float) -> void:
	# View the aim point head-on along the chest normal, slightly off-axis so the surface
	# shading reads as lit skin rather than a flat-on plane.
	var offset := Vector3(0.0, 0.03, -distance).rotated(Vector3.UP, deg_to_rad(15.0))
	camera.global_position = aim + offset
	camera.look_at(aim, Vector3.UP)


func wait_render_frames(frame_count: int) -> void:
	for frame in frame_count:
		await process_frame


func save_capture(file_name: String) -> void:
	var image := root.get_texture().get_image()
	var path := "%s/%s" % [OutputDir, file_name]
	var error := image.save_png(path)
	print("saved %s -> %s (%s)" % [path, error_string(error), Vector2i(image.get_width(), image.get_height())])


func save_centre_zoom() -> void:
	# The digits sit at the swept aim, i.e. the centre of the close-up pair. Save 3x zoomed
	# centre crops so the strokes are large enough to inspect directly.
	var marked := Image.load_from_file("%s/prisoner_closeup.png" % OutputDir)
	var plain := Image.load_from_file("%s/unmarked_closeup.png" % OutputDir)
	if marked == null or plain == null:
		print("centre zoom skipped (image reload failed)")
		return
	var half := 150
	var crop := Rect2i(marked.get_width() / 2 - half, marked.get_height() / 2 - half, half * 2, half * 2)
	var marked_crop := marked.get_region(crop)
	marked_crop.resize(crop.size.x * 3, crop.size.y * 3, Image.INTERPOLATE_NEAREST)
	marked_crop.save_png("%s/prisoner_closeup_zoom.png" % OutputDir)
	var plain_crop := plain.get_region(crop)
	plain_crop.resize(crop.size.x * 3, crop.size.y * 3, Image.INTERPOLATE_NEAREST)
	plain_crop.save_png("%s/unmarked_closeup_zoom.png" % OutputDir)
	print("centre zoom crops saved")
