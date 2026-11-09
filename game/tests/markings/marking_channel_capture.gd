extends SceneTree

# Surface Markings V2 channel-convention evidence runner (fixture-only). A plane shaded by
# the HumanShaders skin ShaderMaterial carries two markings through the production
# SurfaceMarkings component: a roughness marking (dark green scalar -> smooth, glossy band)
# and a normal marking (tilted tangent axes -> differently-lit band), while albedo stays
# untouched. The lit captures prove the roughness `.g` and normal `rg`+AO-`.b` conventions
# visibly.
#
# Run WITHOUT --headless (screenshot capture needs a real renderer):
#   godot-mono -d -s --xr-mode off --path game "tests/markings/marking_channel_capture.gd"

const EnvironmentScene := "res://assets/testing/test_environment.tscn"
const SkinShaderPath := "res://addons/HumanShaders/skin_shader.gdshader"
const OutputDir := "res://temp/markings"
const SurfaceSize := 256


func _init() -> void:
	var holder := Node3D.new()
	holder.name = "ChannelCapture"
	holder.add_child(load(EnvironmentScene).instantiate())
	root.add_child(holder)

	# Studio HDRI light plus a strong directional key light so gloss differences read clearly.
	var key_light := DirectionalLight3D.new()
	key_light.rotation_degrees = Vector3(-48, -28, 0)
	key_light.light_energy = 3.2
	holder.add_child(key_light)

	# Synthetic channel bases the skin shader material references.
	var albedo_base := solid_texture(Color(0.72, 0.52, 0.42))
	var normal_base := solid_texture(Color(0.5, 0.5, 1.0))
	var roughness_base := solid_texture(Color(0.0, 0.55, 0.0))

	var material := ShaderMaterial.new()
	material.shader = load(SkinShaderPath)
	material.set_shader_parameter("texture_albedo", albedo_base)
	material.set_shader_parameter("texture_normal", normal_base)
	material.set_shader_parameter("texture_roughness", roughness_base)
	material.set_shader_parameter("ambient_occlusion_texture", solid_texture(Color(1, 1, 1)))
	material.set_shader_parameter("roughness", 1.0)
	material.set_shader_parameter("specular", 1.0)

	var surface := MeshInstance3D.new()
	surface.name = "SkinPlane"
	var plane := PlaneMesh.new()
	plane.size = Vector2(1.2, 1.2)
	surface.mesh = plane
	surface.material_override = material
	surface.position = Vector3(0, 1.0, 0)
	surface.rotation_degrees = Vector3(90, 0, 0)
	holder.add_child(surface)
	await process_frame

	# Layer one: a near-mirror gloss band on the low-u half (roughness green darkened by
	# multiply). Layer two: a constant tangent tilt band on the high-u half (normal rg
	# whiteout; the AO packed in blue is preserved by the compositor).
	var gloss_marking := SurfaceMarking.new()
	gloss_marking.Position = Vector2(0.28, 0.5)
	gloss_marking.Scale = Vector2(0.4, 0.9)
	gloss_marking.RoughnessTexture = solid_texture(Color(1.0, 0.02, 1.0))
	gloss_marking.RoughnessBlend = 0  # Multiply.

	var relief_marking := SurfaceMarking.new()
	relief_marking.Position = Vector2(0.72, 0.5)
	relief_marking.Scale = Vector2(0.4, 0.9)
	relief_marking.NormalTexture = solid_texture(Color(0.9, 0.36, 0.2))

	var gloss_layer := MarkingLayer.new()
	gloss_layer.Name = "Sheen"
	gloss_layer.Markings = [gloss_marking]
	var relief_layer := MarkingLayer.new()
	relief_layer.Name = "Relief"
	relief_layer.Markings = [relief_marking]

	var overlay := MarkingOverlay.new()
	overlay.NormalBase = normal_base
	overlay.RoughnessBase = roughness_base
	overlay.Layers = [gloss_layer, relief_layer]

	var markings := SurfaceMarkings.new()
	surface.add_child(markings)
	markings.TargetMesh = surface
	markings.Overlay = overlay
	markings.Apply()
	var baked: Material = surface.material_override
	print("baked material=", baked,
		" roughness slot=", baked.get_shader_parameter("texture_roughness") if baked else null,
		" normal slot=", baked.get_shader_parameter("texture_normal") if baked else null)

	var camera := Camera3D.new()
	camera.fov = 40.0
	camera.near = 0.01
	holder.add_child(camera)
	await process_frame

	DirAccess.make_dir_recursive_absolute(OutputDir)

	camera.global_position = Vector3(0.0, 1.0, 1.1)
	camera.look_at(Vector3(0, 1.0, 0), Vector3.UP)
	await wait_render_frames(4)
	await capture_marking_pair(
		camera, surface, material, baked, "channel_unmarked.png", "channel_marked.png")

	save_band_crops()
	quit(0)


func solid_texture(colour: Color) -> ImageTexture:
	var image := Image.create_empty(SurfaceSize, SurfaceSize, false, Image.FORMAT_RGBA8)
	image.fill(colour)
	return ImageTexture.create_from_image(image)


func capture_marking_pair(
		camera: Camera3D,
		target: MeshInstance3D,
		authored: Material,
		baked: Material,
		unmarked_name: String,
		marked_name: String
) -> void:
	# Swap the whole-instance override between the authored base material and the installed
	# baked duplicate; the only difference between the shots is the baked marking channels.
	target.material_override = authored
	await wait_render_frames(2)
	save_capture(unmarked_name)
	target.material_override = baked
	await wait_render_frames(2)
	save_capture(marked_name)


func save_band_crops() -> void:
	var marked := Image.load_from_file("%s/channel_marked.png" % OutputDir)
	var plain := Image.load_from_file("%s/channel_unmarked.png" % OutputDir)
	if marked == null or plain == null:
		print("band crops skipped (image reload failed)")
		return
	var half := 200
	for entry in [["left", marked.get_width() / 4], ["right", marked.get_width() * 3 / 4]]:
		var crop := Rect2i(entry[1] - half / 2, marked.get_height() / 2 - half, half, half * 2)
		var marked_crop := marked.get_region(crop)
		marked_crop.resize(crop.size.x * 2, crop.size.y * 2, Image.INTERPOLATE_NEAREST)
		marked_crop.save_png("%s/channel_%s_marked.png" % [OutputDir, entry[0]])
		var plain_crop := plain.get_region(crop)
		plain_crop.resize(crop.size.x * 2, crop.size.y * 2, Image.INTERPOLATE_NEAREST)
		plain_crop.save_png("%s/channel_%s_unmarked.png" % [OutputDir, entry[0]])
	print("band crops saved")


func wait_render_frames(frame_count: int) -> void:
	for frame in frame_count:
		await process_frame


func save_capture(file_name: String) -> void:
	var image := root.get_texture().get_image()
	var path := "%s/%s" % [OutputDir, file_name]
	var error := image.save_png(path)
	print("saved %s -> %s (%s)" % [path, error_string(error), Vector2i(image.get_width(), image.get_height())])
