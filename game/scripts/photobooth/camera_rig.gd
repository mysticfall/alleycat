## Provides a configurable capture camera for visual scene testing in the photobooth workflow.
@tool
class_name CameraRig
extends Node3D

const DEFAULT_IMAGE_SIZE := Vector2i(512, 512)

@onready var camera: Camera3D = get_node("%Camera")
@onready var viewport: SubViewport = get_node("%Viewport")

## Camera projection mode used by the rig.
@export var projection: Camera3D.ProjectionType = Camera3D.PROJECTION_ORTHOGONAL:
	set(value):
		projection = value
		notify_property_list_changed()
		_apply_exported_properties()

## Output image size used by the viewport.
@export var image_size: Vector2 = DEFAULT_IMAGE_SIZE:
	set(value):
		image_size = value
		_apply_exported_properties()

## Orthographic camera size, shown only when the projection is orthographic.
@export_range(0.1, 20.0) var orthogonal_scale: float = 2.0:
	set(value):
		orthogonal_scale = value
		_apply_exported_properties()

## Perspective camera field of view, shown only when the projection is perspective.
@export_range(45.0, 90.0) var fov: float = 45.0:
	set(value):
		fov = value
		_apply_exported_properties()

## Capture a screenshot to a JPG file and print the generated absolute path.
func capture_screenshot(file_name: String) -> String:
	viewport.render_target_update_mode = SubViewport.UPDATE_ONCE

	await SceneUtils.wait_frames(get_tree(), 1)

	var path: String = await SceneUtils.capture_viewport_screenshot(get_tree(), viewport, file_name)

	viewport.render_target_update_mode = SubViewport.UPDATE_DISABLED

	return path

func _ready() -> void:
	_apply_exported_properties()

func _validate_property(property: Dictionary) -> void:
	if property.name == "orthogonal_scale":
		property.usage = PROPERTY_USAGE_DEFAULT if projection == Camera3D.PROJECTION_ORTHOGONAL else PROPERTY_USAGE_NO_EDITOR
	elif property.name == "fov":
		property.usage = PROPERTY_USAGE_DEFAULT if projection == Camera3D.PROJECTION_PERSPECTIVE else PROPERTY_USAGE_NO_EDITOR

func _apply_exported_properties() -> void:
	if camera != null:
		camera.projection = projection

		if projection == Camera3D.PROJECTION_ORTHOGONAL:
			camera.size = orthogonal_scale
		elif projection == Camera3D.PROJECTION_PERSPECTIVE:
			camera.fov = fov

	if viewport != null:
		viewport.size = Vector2i(image_size)
