## Enables visual scene testing by capturing screenshots from pre-configured [CameraRig] setups.
class_name Photobooth
extends Node3D

const CAMERA_RIG_SCENE_PATH := "res://assets/testing/photobooth/camera_rig.tscn"
const MARKER_SCENE_PATH := "res://assets/markers/debug_marker.tscn"

## Camera rigs indexed by rig node name for lookup and batch capture operations.
var camera_rigs: Dictionary[StringName, CameraRig] = {}
## Debug markers indexed by marker node name for lookup.
var markers: Dictionary[StringName, DebugMarker] = {}

## Instantiates a [CameraRig], adds it under `Cameras`, and returns it.
func add_camera_rig(camera_name: String) -> CameraRig:
	var rig_node: Node = SceneUtils.instantiate_scene(CAMERA_RIG_SCENE_PATH)
	var rig: CameraRig = rig_node as CameraRig

	if rig == null:
		SceneUtils.fatal_error_and_quit("Photobooth: failed to instantiate a camera rig.")
		return null

	if not camera_name.strip_edges().is_empty():
		rig.name = camera_name

	$Cameras.add_child(rig)

	rig.owner = self

	_register_camera_rig(rig)

	return rig

## Instantiates a debug marker, adds it under `Markers`, and returns it.
func add_marker(marker_name: String, label_text: String = "") -> DebugMarker:
	var marker: DebugMarker = SceneUtils.instantiate_scene(MARKER_SCENE_PATH) as DebugMarker

	if marker == null:
		SceneUtils.fatal_error_and_quit("Photobooth: failed to instantiate a debug marker.")
		return null

	if not marker_name.strip_edges().is_empty():
		marker.name = marker_name

	marker.label_text = label_text

	$Markers.add_child(marker)

	marker.owner = self

	_register_marker(marker)

	return marker

## Removes the marker registered under [param marker_name] and frees it.
func remove_marker(marker_name: String) -> void:
	var marker: DebugMarker = get_marker(marker_name)
	markers.erase(marker_name)
	marker.queue_free()

## Returns the marker registered under [param marker_name], or quits if it is missing.
func get_marker(marker_name: String) -> DebugMarker:
	var marker: DebugMarker = markers[marker_name]

	if marker != null:
		return marker

	SceneUtils.fatal_error_and_quit("Photobooth: marker '%s' not found" % marker_name)
	return null

## Returns the camera rig registered under [param camera_name], or quits if it is missing.
func get_camera_rig(camera_name: String) -> CameraRig:
	var rig: CameraRig = camera_rigs[camera_name]

	if rig != null:
		return rig

	SceneUtils.fatal_error_and_quit("Photobooth: camera rig '%s' not found" % camera_name)
	return null

## Removes the camera rig registered under [param camera_name] and frees it.
func remove_camera(camera_name: String) -> void:
	var rig: CameraRig = get_camera_rig(camera_name)
	camera_rigs.erase(camera_name)
	rig.queue_free()

## Captures screenshots from all visible camera rigs and returns output paths keyed by camera name.
func capture_screenshots(file_name: String) -> Dictionary:
	if camera_rigs.is_empty():
		SceneUtils.fatal_error_and_quit("Photobooth: no camera rigs available while capturing screenshots")
		return {}

	var output_paths_by_camera_name := {}
	for key: StringName in camera_rigs:
		var rig: CameraRig = camera_rigs[key]

		if (!rig.visible):
			continue

		var per_camera_file_name: String = _append_camera_name_to_file_name(file_name, rig.name)
		var output_path: String = await rig.capture_screenshot(per_camera_file_name)

		output_paths_by_camera_name[rig.camera.name] = output_path

	return output_paths_by_camera_name

func _ready() -> void:
	var camera_nodes: Array[Node] = $Cameras.find_children("*", "CameraRig", true, false)

	for node: Node in camera_nodes:
		var rig: CameraRig = node as CameraRig

		if rig == null:
			continue

		_register_camera_rig(rig)

	var marker_nodes: Array[Node] = $Markers.find_children("*", "DebugMarker", true, false)

	for node: Node in marker_nodes:
		var marker: DebugMarker = node as DebugMarker

		if marker == null:
			continue

		_register_marker(marker)

func _register_camera_rig(rig: CameraRig) -> void:
	if rig == null:
		SceneUtils.fatal_error_and_quit("Photobooth: cannot register a null CameraRig")
		return

	var existing_rig: CameraRig = null
	if camera_rigs.has(rig.name):
		existing_rig = camera_rigs[rig.name]
	if existing_rig != null and existing_rig != rig:
		SceneUtils.fatal_error_and_quit("Photobooth: duplicate camera rig name '%s'" % rig.name)
		return

	camera_rigs[rig.name] = rig

func _register_marker(marker: DebugMarker) -> void:
	if marker == null:
		SceneUtils.fatal_error_and_quit("Photobooth: cannot register a null marker")
		return

	var existing_marker: DebugMarker = null
	if markers.has(marker.name):
		existing_marker = markers[marker.name]
	if existing_marker != null and existing_marker != marker:
		SceneUtils.fatal_error_and_quit("Photobooth: duplicate marker name '%s'" % marker.name)
		return

	markers[marker.name] = marker

func _append_camera_name_to_file_name(file_name: String, camera_name: String) -> String:
	var file_name_without_directory: String = file_name.get_file()
	var output_directory: String = file_name.get_base_dir()
	var extension: String = file_name_without_directory.get_extension()

	var base_name: String = file_name_without_directory
	if not extension.is_empty():
		base_name = file_name_without_directory.trim_suffix(".%s" % extension)
	else:
		extension = "jpg"

	var camera_name_for_file: String = SceneUtils.to_safe_file_component(camera_name)
	if camera_name_for_file.is_empty():
		camera_name_for_file = "camera"

	var output_file_name: String = "%s_%s.%s" % [base_name, camera_name_for_file, extension]
	if output_directory.is_empty():
		return output_file_name

	return output_directory.path_join(output_file_name)
