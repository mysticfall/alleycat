extends RefCounted


func external_identity() -> String:
	return get_script().resource_path
