extends Node

const ACTIVATION_PROBE_DESTINATION := Vector3(2.043711, 0.0, -1.5803192)

@export var controller: Node
@export var navigation_region: NavigationRegion3D
@export var actor: Node3D


func _ready() -> void:
	controller.set_process(false)
	controller.set_physics_process(false)
	controller.set_process_unhandled_input(false)


func _physics_process(_delta: float) -> void:
	var map := navigation_region.get_navigation_map()
	if not map.is_valid():
		return

	var path := NavigationServer3D.map_get_path(
		map,
		actor.global_position,
		ACTIVATION_PROBE_DESTINATION,
		true)
	if path.size() < 2:
		return

	controller.set_process(true)
	controller.set_physics_process(true)
	controller.set_process_unhandled_input(true)
	set_physics_process(false)
