extends SceneTree
## Bakes the agent-navigation arena's navigation mesh from its own geometry and saves it back to
## `arena_navmesh.tres`. Run after changing `arena.tscn`:
##   godot-mono --headless --path game --xr-mode off -s res://tests/agent_navigation/bake_navmesh.gd

const ARENA_SCENE := "res://tests/agent_navigation/arena.tscn"
const NAVMESH_PATH := "res://tests/agent_navigation/arena_navmesh.tres"
## World height of the arena floor's top face. The arena is flat by construction, so every walkable polygon lies on
## it; Recast's voxel height quantisation otherwise lifts the baked surface (by 0.4 m here), which NavigationAgent3D's
## 3D waypoint and arrival distances cannot absorb.
const FLOOR_TOP_Y := 0.0


func _initialize() -> void:
	var arena := (load(ARENA_SCENE) as PackedScene).instantiate() as NavigationRegion3D
	root.add_child.call_deferred(arena)
	await process_frame
	arena.bake_navigation_mesh(false)
	var navmesh := arena.navigation_mesh
	var vertices := navmesh.vertices
	for index in vertices.size():
		vertices[index].y = FLOOR_TOP_Y
	navmesh.vertices = vertices
	var error := ResourceSaver.save(navmesh, NAVMESH_PATH)
	print("Baked %d polygons (%d vertices); save result %d." % [
		navmesh.get_polygon_count(), navmesh.vertices.size(), error])
	quit(0 if error == OK and navmesh.get_polygon_count() > 0 else 1)
