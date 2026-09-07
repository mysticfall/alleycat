extends SceneTree

const HelperFilename := "external_helper.gd"
const ProbeIdPrefix := "--probe-id="
const ProjectResourcePath := "res://default_bus_layout.tres"
const SuccessMarker := "KIMODO_EXTERNAL_GDSCRIPT_SEAM=ok"


func _init() -> void:
	quit(_run())


func _run() -> int:
	var probe_id := _required_probe_id(OS.get_cmdline_user_args())
	if probe_id.is_empty():
		return 2

	var runner_path: String = get_script().resource_path
	if not runner_path.is_absolute_path():
		printerr("External GDScript seam failed: runner path is not absolute: %s" % runner_path)
		return 3

	var helper_path: String = runner_path.get_base_dir().path_join(HelperFilename).simplify_path()
	var helper_script: Script = load(helper_path) as Script
	if helper_script == null:
		printerr("External GDScript seam failed: could not load external helper: %s" % helper_path)
		return 4

	var helper: Object = helper_script.new()
	if helper == null or not helper.has_method("external_identity"):
		printerr("External GDScript seam failed: external helper has an invalid interface: %s" % helper_path)
		return 5
	var helper_identity: String = str(helper.call("external_identity"))
	if helper_identity != helper_path:
		printerr("External GDScript seam failed: helper resolved outside its script-relative path: %s" % helper_path)
		return 6

	var project_resource: Resource = ResourceLoader.load(ProjectResourcePath)
	if project_resource == null:
		printerr("External GDScript seam failed: could not read project resource: %s" % ProjectResourcePath)
		return 7
	if project_resource.resource_path != ProjectResourcePath:
		printerr("External GDScript seam failed: project resource did not resolve through res://: %s" % project_resource.resource_path)
		return 8

	print("%s probe_id=%s helper=%s resource=%s" % [SuccessMarker, probe_id, helper_path, project_resource.resource_path])
	return 0


func _required_probe_id(arguments: PackedStringArray) -> String:
	var probe_id := ""
	for argument in arguments:
		if argument.begins_with(ProbeIdPrefix):
			if not probe_id.is_empty():
				printerr("External GDScript seam failed: --probe-id may be supplied once.")
				return ""
			probe_id = argument.trim_prefix(ProbeIdPrefix)

	if probe_id.is_empty():
		printerr("External GDScript seam failed: required user argument --probe-id=<value> is missing.")
	return probe_id
