extends SceneTree

const PHOTOBOOTH_SCENE_PATH := "res://tests/ik/contact_lock_native_pipeline_photobooth.tscn"
const STAGE_PREFIX := "ALLEYCAT_BONE_TRANSCRIPT_STAGE:"
const MAXIMUM_PHYSICS_FRAMES := 16


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	print(STAGE_PREFIX + "photobooth-load-start")
	var photobooth_scene := load(PHOTOBOOTH_SCENE_PATH) as PackedScene
	if photobooth_scene == null:
		_fail("Failed to load native pipeline photobooth scene.")
		return
	print(STAGE_PREFIX + "photobooth-load-complete")

	var photobooth := photobooth_scene.instantiate()
	root.add_child(photobooth)
	print(STAGE_PREFIX + "photobooth-attached")

	for frame in MAXIMUM_PHYSICS_FRAMES:
		await physics_frame
		var controller := photobooth.get_node_or_null("NativePipelineController")
		if controller != null and controller.get("IsConfigured"):
			print(STAGE_PREFIX + "roles-configured")
			photobooth.queue_free()
			await process_frame
			print(STAGE_PREFIX + "complete")
			quit(0)
			return

	photobooth.queue_free()
	_fail("The native pipeline photobooth did not configure both minimal roles on physics frames.")


func _fail(message: String) -> void:
	push_error(message)
	quit(1)
