class_name CoordinateContract
extends RefCounted

const VERSION := "anim002_female_root_rest_basis_v1"
const STRATEGY := "canonical_axis_conversion_with_strict_processed_root_rest"
const TOLERANCE := 0.00001
const CANONICAL_COLUMNS := [Vector3(-1.0, 0.0, 0.0), Vector3(0.0, 0.0, 1.0), Vector3(0.0, 1.0, 0.0)]

static func validate_evidence(evidence: Variant) -> String:
	if not evidence is Dictionary:
		return "coordinate contract evidence is absent or malformed"
	var value := evidence as Dictionary
	var fields := ["version", "strategy", "source_armature", "root_bone", "blender_rest", "canonical_blender_to_godot", "expected_godot_root_rest"]
	if not _has_exact_fields(value, fields):
		return "coordinate contract fields are malformed"
	if value["version"] != VERSION or value["strategy"] != STRATEGY:
		return "coordinate contract version or strategy is stale"
	if value["source_armature"] != "Female" or value["root_bone"] != "Root":
		return "coordinate contract names an unsupported rig"
	var blender = value["blender_rest"]
	var conversion = value["canonical_blender_to_godot"]
	var expected = value["expected_godot_root_rest"]
	if not blender is Dictionary or not _has_exact_fields(blender, ["coordinate_space", "basis_columns", "origin"]):
		return "processed Blender Root rest evidence is malformed"
	if blender["coordinate_space"] != "female_armature_local_z_up":
		return "processed Blender Root rest coordinate space is unsupported"
	if not conversion is Dictionary or not _has_exact_fields(conversion, ["vector_mapping", "basis_columns"]):
		return "canonical conversion evidence is malformed"
	if conversion["vector_mapping"] != "(-x,z,y)" or not _basis_columns_match(conversion["basis_columns"], CANONICAL_COLUMNS, 0.0):
		return "canonical Blender-to-Godot conversion is stale or malformed"
	if not expected is Dictionary or not _has_exact_fields(expected, ["coordinate_space", "basis_columns", "origin"]):
		return "expected Godot Root rest evidence is malformed"
	if expected["coordinate_space"] != "imported_skeleton_local_y_up":
		return "expected Godot Root rest coordinate space is unsupported"
	var blender_rest := _transform(blender)
	var expected_rest := _transform(expected)
	if not _transform_is_finite(blender_rest) or not _transform_is_finite(expected_rest):
		return "coordinate contract contains non-finite rest evidence"
	var conversion_basis := Basis(CANONICAL_COLUMNS[0], CANONICAL_COLUMNS[1], CANONICAL_COLUMNS[2])
	var mapped := Transform3D(conversion_basis * blender_rest.basis * conversion_basis.inverse(), conversion_basis * blender_rest.origin)
	if not _transform_near(mapped, expected_rest, TOLERANCE):
		return "expected Godot Root rest is not the mapped processed Blender rest"
	return ""

static func validate_skeleton(evidence: Variant, skeleton: Skeleton3D) -> String:
	var error := validate_evidence(evidence)
	if not error.is_empty():
		return error
	var value := evidence as Dictionary
	var root := skeleton.find_bone(String(value["root_bone"]))
	if root < 0 or skeleton.get_bone_parent(root) != -1:
		return "imported Female Skeleton3D lacks the required unparented Root"
	var expected := _transform(value["expected_godot_root_rest"])
	if not _transform_near(skeleton.get_bone_rest(root), expected, TOLERANCE):
		return "imported Female Root rest violates the processed Blender coordinate contract"
	return ""

static func map_blender_vector(evidence: Dictionary, value: Vector3) -> Vector3:
	# validate_evidence() proves these canonical columns before this mapper is used.
	var columns: Array = evidence["canonical_blender_to_godot"]["basis_columns"]
	return _basis(columns) * value

static func expected_godot_rest(evidence: Dictionary) -> Transform3D:
	return _transform(evidence["expected_godot_root_rest"])

static func _has_exact_fields(value: Dictionary, fields: Array) -> bool:
	if value.size() != fields.size(): return false
	for field in fields:
		if not value.has(field): return false
	return true

static func _transform(value: Dictionary) -> Transform3D:
	return Transform3D(_basis(value.get("basis_columns", [])), _vector(value.get("origin", [])))

static func _basis(value: Variant) -> Basis:
	if not value is Array or value.size() != 3: return Basis(Vector3(NAN, NAN, NAN), Vector3.ZERO, Vector3.ZERO)
	return Basis(_vector(value[0]), _vector(value[1]), _vector(value[2]))

static func _vector(value: Variant) -> Vector3:
	if not value is Array or value.size() != 3: return Vector3(NAN, NAN, NAN)
	return Vector3(float(value[0]), float(value[1]), float(value[2]))

static func _basis_columns_match(value: Variant, expected: Array, tolerance: float) -> bool:
	if not value is Array or value.size() != 3: return false
	for index in 3:
		if _vector(value[index]).distance_to(expected[index]) > tolerance: return false
	return true

static func _transform_is_finite(value: Transform3D) -> bool:
	for component in [value.origin.x, value.origin.y, value.origin.z, value.basis.x.x, value.basis.x.y, value.basis.x.z, value.basis.y.x, value.basis.y.y, value.basis.y.z, value.basis.z.x, value.basis.z.y, value.basis.z.z]:
		if not is_finite(component): return false
	return absf(value.basis.determinant()) > 0.000001

static func _transform_near(left: Transform3D, right: Transform3D, tolerance: float) -> bool:
	return left.origin.distance_to(right.origin) <= tolerance and left.basis.x.distance_to(right.basis.x) <= tolerance and left.basis.y.distance_to(right.basis.y) <= tolerance and left.basis.z.distance_to(right.basis.z) <= tolerance
