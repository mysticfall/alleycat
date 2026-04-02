extends SceneTree

const SUBJECT_SCENE_PATH := "res://test/characters/characters/ik/neck_spine_ccdik_test.tscn"
const IK_SCENE_PATH := "res://assets/characters/ik/neck_spine_ccdik.tscn"

const BASE_TORSO_FOCUS_OFFSET := Vector3(0.0, 1.38, 0.02)
const DEFAULT_FOCUS_HEAD_WEIGHT := 0.58
const DEFAULT_ORTHOGRAPHIC_SIZE := 0.92
const AUTOFRAME_MIN_ORTHOGRAPHIC_SIZE := 0.84
const AUTOFRAME_MAX_ORTHOGRAPHIC_SIZE := 1.52
const AUTOFRAME_MAX_OCCUPANCY := 0.9
const AUTOFRAME_EDGE_MARGIN_NORMALISED := 0.025

const AC7_OCCUPANCY_MIN_STANDARD := 0.5
const AC7_OCCUPANCY_MIN_EXTREME := 0.4
const AC7_OCCUPANCY_MAX := 0.92

const ANGLE_CAPTURES := [
	{"slug": "front", "offset": Vector3(0.0, 0.08, 1.62), "focus_offset": Vector3(0.0, -0.02, -0.02)},
	{"slug": "left_profile", "offset": Vector3(-1.62, 0.08, 0.0), "focus_offset": Vector3(0.0, -0.01, 0.0)},
	{"slug": "right_profile", "offset": Vector3(1.62, 0.08, 0.0), "focus_offset": Vector3(0.0, -0.01, 0.0)},
]

const POSE_CAPTURES := [
	{"marker": "TargetForward", "slug": "forward", "focus_head_weight": 0.55, "orthographic_size": 0.9},
	{"marker": "TargetLeft", "slug": "left", "focus_head_weight": 0.55, "orthographic_size": 0.9},
	{"marker": "TargetRight", "slug": "right", "focus_head_weight": 0.55, "orthographic_size": 0.9},
	{"marker": "TargetUp", "slug": "up", "focus_head_weight": 0.6, "orthographic_size": 0.9, "focus_marker_offset": Vector3(0.0, -0.04, 0.0)},
	{"marker": "TargetDown", "slug": "down", "focus_head_weight": 0.52, "orthographic_size": 0.91, "focus_marker_offset": Vector3(0.0, 0.03, 0.0)},
	{
		"marker": "TargetStoopForward",
		"slug": "stoop-forward",
		"focus_head_weight": 0.46,
		"orthographic_size": 1.34,
		"target_occupancy": 0.58,
		"min_occupancy": AC7_OCCUPANCY_MIN_EXTREME,
		"focus_marker_offset": Vector3(0.0, 0.05, -0.03),
		"angle_focus_offsets": {
			"front": Vector3(0.0, -0.22, -0.06),
			"left_profile": Vector3(0.0, -0.2, -0.07),
			"right_profile": Vector3(0.0, -0.2, -0.07),
		},
	},
	{
		"marker": "TargetLeanBack",
		"slug": "lean-back",
		"focus_head_weight": 0.48,
		"orthographic_size": 1.28,
		"target_occupancy": 0.58,
		"min_occupancy": AC7_OCCUPANCY_MIN_EXTREME,
		"focus_marker_offset": Vector3(0.0, -0.05, 0.03),
		"angle_focus_offsets": {
			"front": Vector3(0.0, -0.18, 0.03),
			"left_profile": Vector3(0.0, -0.16, 0.03),
			"right_profile": Vector3(0.0, -0.16, 0.03),
		},
	},
]

var BONE_NAME_CANDIDATES := {
	"head": PackedStringArray(["head"]),
	"neck": PackedStringArray(["neck"]),
	"upper_spine": PackedStringArray(["spine2", "spine_02", "spine.002", "chest", "upperchest", "spine1", "spine_01", "spine.001", "spine"]),
	"hips": PackedStringArray(["hips", "pelvis", "hip"]),
}


class BonePointMap:
	var head_point: Vector3
	var neck_point: Vector3
	var upper_spine_point: Vector3
	var hips_point: Vector3
	var has_hips_point: bool = false


class CaptureMetrics:
	var pose: String
	var angle: String
	var screenshot_file_name: String
	var hips_visible: bool
	var occupancy_axis: float
	var occupancy_area: float
	var blank_space_ratio: float
	var edge_margin_min: float
	var critical_crop: bool
	var ac7_like_pass: bool
	var ac8_like_pass: bool
	var final_orthographic_size: float
	var centre_offset_norm: Vector2

	func to_dictionary() -> Dictionary:
		return {
			"pose": pose,
			"angle": angle,
			"screenshot": screenshot_file_name,
			"hips_visible": hips_visible,
			"occupancy_axis": snappedf(occupancy_axis, 0.0001),
			"occupancy_area": snappedf(occupancy_area, 0.0001),
			"blank_space_ratio": snappedf(blank_space_ratio, 0.0001),
			"edge_margin_min": snappedf(edge_margin_min, 0.0001),
			"critical_crop": critical_crop,
			"ac7_like_pass": ac7_like_pass,
			"ac8_like_pass": ac8_like_pass,
			"final_orthographic_size": snappedf(final_orthographic_size, 0.0001),
			"centre_offset_norm": {
				"x": snappedf(centre_offset_norm.x, 0.0001),
				"y": snappedf(centre_offset_norm.y, 0.0001),
			},
		}


func _init() -> void:
	await _run_probe()


func _run_probe() -> void:
	var photobooth: ProbeUtils.Photobooth = ProbeUtils.setup_photobooth(self)
	if photobooth == null:
		return

	var subject: Node3D = photobooth.load(SUBJECT_SCENE_PATH)
	if subject == null:
		return

	await ProbeUtils.wait_frames(self, 2)

	var target_poses: Node3D = ProbeUtils.require_node(subject, ^"TargetPoses") as Node3D
	if target_poses == null:
		quit(1)
		return

	var female: Node3D = ProbeUtils.require_node(subject, ^"Female") as Node3D
	if female == null:
		quit(1)
		return
	var skeleton: Skeleton3D = _find_first_skeleton(female)
	if skeleton == null:
		push_error("No Skeleton3D found under Female in neck-spine subject scene")
		quit(1)
		return

	var ik_target: Node3D = _attach_ik_target(female)
	if ik_target == null:
		quit(1)
		return

	photobooth.use_portrait()
	photobooth.set_orthographic(DEFAULT_ORTHOGRAPHIC_SIZE)
	var camera: Camera3D = photobooth.get_camera()
	if camera == null:
		push_error("Probe camera unavailable")
		quit(1)
		return

	await ProbeUtils.wait_frames(self, 3)
	await ProbeUtils.wait_seconds(self, 0.15)

	var capture_metrics_list: Array[CaptureMetrics] = []

	for pose_capture: Dictionary in POSE_CAPTURES:
		var marker_name: String = pose_capture["marker"]
		var pose_slug: String = String(pose_capture["slug"])
		var is_extreme_pose: bool = _is_extreme_pose_slug(pose_slug)
		var marker_node: Node3D = ProbeUtils.require_node(target_poses, NodePath(marker_name)) as Node3D
		if marker_node == null:
			quit(1)
			return

		ik_target.global_transform = marker_node.global_transform
		var orthographic_size: float = _read_pose_float(pose_capture, "orthographic_size", DEFAULT_ORTHOGRAPHIC_SIZE)
		photobooth.set_orthographic(orthographic_size)
		await ProbeUtils.wait_frames(self, 3)
		await ProbeUtils.wait_seconds(self, 0.1)

		for angle_capture: Dictionary in ANGLE_CAPTURES:
			var angle_slug: String = String(angle_capture["slug"])
			var framing_points: BonePointMap = _resolve_bone_point_map(subject, marker_node, skeleton)
			if not framing_points.has_hips_point:
				framing_points.hips_point = _estimate_hips_point_from_subject(subject)
				framing_points.has_hips_point = true

			var required_points: Array[Vector3] = _build_required_points(framing_points, is_extreme_pose)
			var objective_framing: Dictionary = await _apply_objective_framing(
				photobooth,
				subject,
				marker_node,
				pose_capture,
				angle_capture,
				required_points,
			)
			await ProbeUtils.wait_frames(self, 2)
			await ProbeUtils.wait_seconds(self, 0.05)

			var screenshot_file_name := "char001_neck_spine_%s_%s.jpg" % [pose_slug, angle_slug]
			await photobooth.capture(screenshot_file_name)

			var metrics: CaptureMetrics = _collect_capture_metrics(
				camera,
				required_points,
				framing_points,
				pose_slug,
				angle_slug,
				screenshot_file_name,
				is_extreme_pose,
			)
			metrics.final_orthographic_size = objective_framing["orthographic_size"] as float
			metrics.centre_offset_norm = objective_framing["centre_offset_norm"] as Vector2
			capture_metrics_list.append(metrics)

	_write_metrics_report(capture_metrics_list)
	_print_metrics_summary(capture_metrics_list)

	photobooth.dispose()
	quit(0)


func _apply_objective_framing(
		photobooth: ProbeUtils.Photobooth,
		subject: Node3D,
		marker_node: Node3D,
		pose_capture: Dictionary,
		angle_capture: Dictionary,
		required_points: Array[Vector3]
	) -> Dictionary:
	var camera: Camera3D = photobooth.get_camera()
	var angle_offset: Vector3 = angle_capture["offset"] as Vector3
	var fallback_focus: Vector3 = _resolve_focus_point(subject, marker_node, pose_capture, angle_capture)
	var focus_point: Vector3 = _average_points(required_points, fallback_focus)

	photobooth.position_camera(focus_point + angle_offset, focus_point)
	var centre_offset_norm: Vector2 = _recenter_camera_from_projected_bounds(photobooth, required_points, angle_offset)

	var target_occupancy: float = _read_pose_float(
		pose_capture,
		"target_occupancy",
		0.62 if _is_extreme_pose_slug(String(pose_capture["slug"])) else 0.66
	)
	var min_occupancy: float = _read_pose_float(
		pose_capture,
		"min_occupancy",
		AC7_OCCUPANCY_MIN_EXTREME if _is_extreme_pose_slug(String(pose_capture["slug"])) else AC7_OCCUPANCY_MIN_STANDARD
	)
	var objective_size: float = _calculate_objective_orthographic_size(
		camera,
		required_points,
		target_occupancy,
		min_occupancy,
		AUTOFRAME_MAX_OCCUPANCY,
	)
	photobooth.set_orthographic(objective_size)

	await ProbeUtils.wait_frames(self, 1)
	await ProbeUtils.wait_seconds(self, 0.02)
	centre_offset_norm = _recenter_camera_from_projected_bounds(photobooth, required_points, angle_offset)

	return {
		"orthographic_size": objective_size,
		"centre_offset_norm": centre_offset_norm,
	}


func _collect_capture_metrics(
		camera: Camera3D,
		required_points: Array[Vector3],
		framing_points: BonePointMap,
		pose_slug: String,
		angle_slug: String,
		screenshot_file_name: String,
		is_extreme_pose: bool
	) -> CaptureMetrics:
	var viewport_size: Vector2 = _resolve_viewport_size(camera)
	var projected_metrics: Dictionary = _calculate_projected_metrics(camera, required_points, viewport_size)

	var hips_visible: bool = false
	if framing_points.has_hips_point:
		hips_visible = _is_world_point_visible(camera, framing_points.hips_point, viewport_size)

	var metrics := CaptureMetrics.new()
	metrics.pose = pose_slug
	metrics.angle = angle_slug
	metrics.screenshot_file_name = screenshot_file_name
	metrics.hips_visible = hips_visible
	metrics.occupancy_axis = projected_metrics["occupancy_axis"] as float
	metrics.occupancy_area = projected_metrics["occupancy_area"] as float
	metrics.blank_space_ratio = projected_metrics["blank_space_ratio"] as float
	metrics.edge_margin_min = projected_metrics["edge_margin_min"] as float
	metrics.critical_crop = projected_metrics["critical_crop"] as bool

	var occupancy_min: float = AC7_OCCUPANCY_MIN_EXTREME if is_extreme_pose else AC7_OCCUPANCY_MIN_STANDARD
	metrics.ac7_like_pass = (
		not metrics.critical_crop
		and metrics.edge_margin_min >= AUTOFRAME_EDGE_MARGIN_NORMALISED
		and metrics.occupancy_axis >= occupancy_min
		and metrics.occupancy_axis <= AC7_OCCUPANCY_MAX
	)
	metrics.ac8_like_pass = (not is_extreme_pose) or metrics.hips_visible

	return metrics


func _resolve_bone_point_map(subject: Node3D, marker_node: Node3D, skeleton: Skeleton3D) -> BonePointMap:
	var points := BonePointMap.new()

	var head_point: Vector3 = _resolve_bone_world_point(skeleton, "head")
	var neck_point: Vector3 = _resolve_bone_world_point(skeleton, "neck")
	var upper_spine_point: Vector3 = _resolve_bone_world_point(skeleton, "upper_spine")
	var hips_point: Vector3 = _resolve_bone_world_point(skeleton, "hips")

	var fallback_head: Vector3 = marker_node.global_position
	var fallback_neck: Vector3 = subject.global_position + Vector3(0.0, 1.48, 0.08)
	var fallback_upper_spine: Vector3 = subject.global_position + Vector3(0.0, 1.32, 0.06)
	var fallback_hips: Vector3 = subject.global_position + Vector3(0.0, 0.96, 0.02)

	points.head_point = head_point if head_point != Vector3.INF else fallback_head
	points.neck_point = neck_point if neck_point != Vector3.INF else fallback_neck
	points.upper_spine_point = upper_spine_point if upper_spine_point != Vector3.INF else fallback_upper_spine
	if hips_point != Vector3.INF:
		points.hips_point = hips_point
		points.has_hips_point = true
	else:
		points.hips_point = fallback_hips
		points.has_hips_point = true

	return points


func _resolve_bone_world_point(skeleton: Skeleton3D, point_key: String) -> Vector3:
	var candidates: PackedStringArray = BONE_NAME_CANDIDATES.get(point_key, PackedStringArray()) as PackedStringArray
	if candidates.is_empty():
		return Vector3.INF

	var bone_index: int = _find_bone_index_from_candidates(skeleton, candidates)
	if bone_index < 0:
		return Vector3.INF

	var bone_pose: Transform3D = skeleton.get_bone_global_pose(bone_index)
	return skeleton.global_transform * bone_pose.origin


func _find_bone_index_from_candidates(skeleton: Skeleton3D, candidates: PackedStringArray) -> int:
	for candidate: String in candidates:
		var exact_index: int = skeleton.find_bone(candidate)
		if exact_index >= 0:
			return exact_index

	for candidate: String in candidates:
		var candidate_lower: String = candidate.to_lower()
		for bone_index in skeleton.get_bone_count():
			var bone_name_lower: String = skeleton.get_bone_name(bone_index).to_lower()
			if bone_name_lower == candidate_lower or bone_name_lower.contains(candidate_lower):
				return bone_index

	return -1


func _build_required_points(points: BonePointMap, include_hips: bool) -> Array[Vector3]:
	var required_points: Array[Vector3] = [
		points.head_point,
		points.neck_point,
		points.upper_spine_point,
	]
	if include_hips and points.has_hips_point:
		required_points.append(points.hips_point)

	return required_points


func _estimate_hips_point_from_subject(subject: Node3D) -> Vector3:
	var subject_bounds: AABB = _resolve_subject_world_aabb(subject)
	if subject_bounds.has_surface():
		var hips_y: float = subject_bounds.position.y + (subject_bounds.size.y * 0.47)
		return Vector3(subject_bounds.get_center().x, hips_y, subject_bounds.get_center().z)

	return subject.global_position + Vector3(0.0, 0.96, 0.02)


func _resolve_subject_world_aabb(subject_root: Node3D) -> AABB:
	var has_bounds: bool = false
	var merged: AABB = AABB()
	var stack: Array[Node] = [subject_root]

	while not stack.is_empty():
		var current: Node = stack.pop_back()
		var visual: VisualInstance3D = current as VisualInstance3D
		if visual != null:
			var local_bounds: AABB = visual.get_aabb()
			if local_bounds.has_surface():
				var world_bounds: AABB = visual.global_transform * local_bounds
				if has_bounds:
					merged = merged.merge(world_bounds)
				else:
					merged = world_bounds
					has_bounds = true

		for child: Node in current.get_children():
			stack.append(child)

	return merged if has_bounds else AABB()


func _recenter_camera_from_projected_bounds(
		photobooth: ProbeUtils.Photobooth,
		world_points: Array[Vector3],
		angle_offset: Vector3
	) -> Vector2:
	var camera: Camera3D = photobooth.get_camera()
	if camera == null:
		return Vector2.ZERO

	var viewport_size: Vector2 = _resolve_viewport_size(camera)
	var projected_bounds: Rect2 = _projected_bounds_rect(camera, world_points)
	if projected_bounds.size == Vector2.ZERO:
		return Vector2.ZERO

	var viewport_centre: Vector2 = viewport_size * 0.5
	var bounds_centre: Vector2 = projected_bounds.get_center()
	var centre_delta_px: Vector2 = bounds_centre - viewport_centre

	var visible_size: Vector2 = _resolve_camera_visible_size(camera, viewport_size)
	if is_zero_approx(visible_size.x) or is_zero_approx(visible_size.y):
		return Vector2.ZERO

	var world_delta_x: float = (centre_delta_px.x / viewport_size.x) * visible_size.x
	var world_delta_y: float = -(centre_delta_px.y / viewport_size.y) * visible_size.y
	var basis: Basis = camera.global_transform.basis
	var world_offset: Vector3 = (basis.x * world_delta_x) + (basis.y * world_delta_y)

	var current_focus: Vector3 = camera.global_position - angle_offset
	var next_focus: Vector3 = current_focus + world_offset
	photobooth.position_camera(next_focus + angle_offset, next_focus)

	return Vector2(
		centre_delta_px.x / maxf(viewport_size.x, 1.0),
		centre_delta_px.y / maxf(viewport_size.y, 1.0)
	)


func _calculate_objective_orthographic_size(
		camera: Camera3D,
		world_points: Array[Vector3],
		target_occupancy: float,
		min_occupancy: float,
		max_occupancy: float
	) -> float:
	var viewport_size: Vector2 = _resolve_viewport_size(camera)
	var span: Vector2 = _camera_local_span(camera, world_points)
	if span == Vector2.ZERO:
		return clampf(camera.size, AUTOFRAME_MIN_ORTHOGRAPHIC_SIZE, AUTOFRAME_MAX_ORTHOGRAPHIC_SIZE)

	var clamped_target_occupancy: float = clampf(target_occupancy, 0.3, 0.85)
	var clamped_min_occupancy: float = clampf(min_occupancy, 0.25, clamped_target_occupancy)
	var clamped_max_occupancy: float = clampf(max_occupancy, clamped_target_occupancy, 0.95)

	var target_size: float = _orthographic_size_for_occupancy(camera, viewport_size, span, clamped_target_occupancy)
	var min_size: float = _orthographic_size_for_occupancy(camera, viewport_size, span, clamped_max_occupancy)
	var max_size: float = _orthographic_size_for_occupancy(camera, viewport_size, span, clamped_min_occupancy)

	return clampf(target_size, maxf(min_size, AUTOFRAME_MIN_ORTHOGRAPHIC_SIZE), minf(max_size, AUTOFRAME_MAX_ORTHOGRAPHIC_SIZE))


func _orthographic_size_for_occupancy(
		camera: Camera3D,
		viewport_size: Vector2,
		span: Vector2,
		occupancy: float
	) -> float:
	var safe_occupancy: float = maxf(occupancy, 0.001)
	var aspect: float = maxf(viewport_size.x / maxf(viewport_size.y, 1.0), 0.001)

	if camera.keep_aspect == Camera3D.KEEP_WIDTH:
		var width_size: float = span.x / safe_occupancy
		var height_size: float = (span.y * aspect) / safe_occupancy
		return maxf(width_size, height_size)

	var height_size_keep_height: float = span.y / safe_occupancy
	var width_size_keep_height: float = span.x / (safe_occupancy * aspect)
	return maxf(height_size_keep_height, width_size_keep_height)


func _camera_local_span(camera: Camera3D, world_points: Array[Vector3]) -> Vector2:
	if world_points.is_empty():
		return Vector2.ZERO

	var min_x: float = INF
	var max_x: float = -INF
	var min_y: float = INF
	var max_y: float = -INF

	for point: Vector3 in world_points:
		var local_point: Vector3 = camera.to_local(point)
		min_x = minf(min_x, local_point.x)
		max_x = maxf(max_x, local_point.x)
		min_y = minf(min_y, local_point.y)
		max_y = maxf(max_y, local_point.y)

	if not is_finite(min_x) or not is_finite(max_x) or not is_finite(min_y) or not is_finite(max_y):
		return Vector2.ZERO

	return Vector2(maxf(max_x - min_x, 0.0), maxf(max_y - min_y, 0.0))


func _calculate_projected_metrics(camera: Camera3D, world_points: Array[Vector3], viewport_size: Vector2) -> Dictionary:
	if world_points.is_empty() or viewport_size.x <= 0.0 or viewport_size.y <= 0.0:
		return {
			"occupancy_axis": 0.0,
			"occupancy_area": 0.0,
			"blank_space_ratio": 1.0,
			"edge_margin_min": 0.0,
			"critical_crop": true,
		}

	var min_x: float = INF
	var max_x: float = -INF
	var min_y: float = INF
	var max_y: float = -INF
	var critical_crop: bool = false

	for world_point: Vector3 in world_points:
		if camera.is_position_behind(world_point):
			critical_crop = true
			continue

		var screen_point: Vector2 = camera.unproject_position(world_point)
		min_x = minf(min_x, screen_point.x)
		max_x = maxf(max_x, screen_point.x)
		min_y = minf(min_y, screen_point.y)
		max_y = maxf(max_y, screen_point.y)

		if screen_point.x < 0.0 or screen_point.x > viewport_size.x or screen_point.y < 0.0 or screen_point.y > viewport_size.y:
			critical_crop = true

	if not is_finite(min_x) or not is_finite(max_x) or not is_finite(min_y) or not is_finite(max_y):
		return {
			"occupancy_axis": 0.0,
			"occupancy_area": 0.0,
			"blank_space_ratio": 1.0,
			"edge_margin_min": 0.0,
			"critical_crop": true,
		}

	var width_norm: float = clampf((max_x - min_x) / viewport_size.x, 0.0, 1.0)
	var height_norm: float = clampf((max_y - min_y) / viewport_size.y, 0.0, 1.0)
	var occupancy_axis: float = maxf(width_norm, height_norm)
	var occupancy_area: float = width_norm * height_norm
	var blank_space_ratio: float = 1.0 - occupancy_axis

	var left_margin: float = min_x / viewport_size.x
	var right_margin: float = (viewport_size.x - max_x) / viewport_size.x
	var top_margin: float = min_y / viewport_size.y
	var bottom_margin: float = (viewport_size.y - max_y) / viewport_size.y
	var edge_margin_min: float = minf(minf(left_margin, right_margin), minf(top_margin, bottom_margin))

	return {
		"occupancy_axis": occupancy_axis,
		"occupancy_area": occupancy_area,
		"blank_space_ratio": blank_space_ratio,
		"edge_margin_min": edge_margin_min,
		"critical_crop": critical_crop,
	}


func _projected_bounds_rect(camera: Camera3D, world_points: Array[Vector3]) -> Rect2:
	if world_points.is_empty():
		return Rect2()

	var min_x: float = INF
	var max_x: float = -INF
	var min_y: float = INF
	var max_y: float = -INF

	for world_point: Vector3 in world_points:
		if camera.is_position_behind(world_point):
			continue
		var screen_point: Vector2 = camera.unproject_position(world_point)
		min_x = minf(min_x, screen_point.x)
		max_x = maxf(max_x, screen_point.x)
		min_y = minf(min_y, screen_point.y)
		max_y = maxf(max_y, screen_point.y)

	if not is_finite(min_x) or not is_finite(max_x) or not is_finite(min_y) or not is_finite(max_y):
		return Rect2()

	return Rect2(Vector2(min_x, min_y), Vector2(max_x - min_x, max_y - min_y))


func _is_world_point_visible(camera: Camera3D, world_point: Vector3, viewport_size: Vector2) -> bool:
	if camera.is_position_behind(world_point):
		return false

	var screen_point: Vector2 = camera.unproject_position(world_point)
	return (
		screen_point.x >= 0.0
		and screen_point.x <= viewport_size.x
		and screen_point.y >= 0.0
		and screen_point.y <= viewport_size.y
	)


func _resolve_camera_visible_size(camera: Camera3D, viewport_size: Vector2) -> Vector2:
	var aspect: float = maxf(viewport_size.x / maxf(viewport_size.y, 1.0), 0.001)
	if camera.keep_aspect == Camera3D.KEEP_WIDTH:
		return Vector2(camera.size, camera.size / aspect)

	return Vector2(camera.size * aspect, camera.size)


func _resolve_viewport_size(camera: Camera3D) -> Vector2:
	var viewport: Viewport = camera.get_viewport()
	if viewport == null:
		return Vector2(540.0, 960.0)

	var rect: Rect2 = viewport.get_visible_rect()
	if rect.size.x <= 0.0 or rect.size.y <= 0.0:
		return Vector2(540.0, 960.0)

	return rect.size


func _average_points(points: Array[Vector3], fallback: Vector3) -> Vector3:
	if points.is_empty():
		return fallback

	var accum: Vector3 = Vector3.ZERO
	for point: Vector3 in points:
		accum += point

	return accum / float(points.size())


func _write_metrics_report(metrics_list: Array[CaptureMetrics]) -> void:
	var output_dir: String = _resolve_output_dir_arg()
	if output_dir.is_empty():
		return

	var metrics_json_path: String = output_dir.path_join("framing_metrics.json")
	var metrics_csv_path: String = output_dir.path_join("framing_metrics.csv")
	DirAccess.make_dir_recursive_absolute(ProjectSettings.globalize_path(output_dir))

	var report_rows: Array[Dictionary] = []
	for metrics: CaptureMetrics in metrics_list:
		report_rows.append(metrics.to_dictionary())

	var summary: Dictionary = _summarise_metrics(metrics_list)
	var report: Dictionary = {
		"summary": summary,
		"captures": report_rows,
	}

	var json_file: FileAccess = FileAccess.open(metrics_json_path, FileAccess.WRITE)
	if json_file != null:
		json_file.store_string(JSON.stringify(report, "\t", false))
		json_file.close()

	var csv_lines := PackedStringArray([
		"pose,angle,screenshot,hips_visible,occupancy_axis,occupancy_area,blank_space_ratio,edge_margin_min,critical_crop,ac7_like_pass,ac8_like_pass,final_orthographic_size,centre_offset_x,centre_offset_y"
	])
	for metrics: CaptureMetrics in metrics_list:
		csv_lines.append(
			"%s,%s,%s,%s,%.4f,%.4f,%.4f,%.4f,%s,%s,%s,%.4f,%.4f,%.4f"
			% [
				metrics.pose,
				metrics.angle,
				metrics.screenshot_file_name,
				str(metrics.hips_visible),
				metrics.occupancy_axis,
				metrics.occupancy_area,
				metrics.blank_space_ratio,
				metrics.edge_margin_min,
				str(metrics.critical_crop),
				str(metrics.ac7_like_pass),
				str(metrics.ac8_like_pass),
				metrics.final_orthographic_size,
				metrics.centre_offset_norm.x,
				metrics.centre_offset_norm.y,
			]
		)

	var csv_file: FileAccess = FileAccess.open(metrics_csv_path, FileAccess.WRITE)
	if csv_file != null:
		csv_file.store_string("\n".join(csv_lines))
		csv_file.close()

	print("Saved framing metrics JSON: %s" % ProjectSettings.globalize_path(metrics_json_path))
	print("Saved framing metrics CSV: %s" % ProjectSettings.globalize_path(metrics_csv_path))


func _resolve_output_dir_arg() -> String:
	var user_args: PackedStringArray = OS.get_cmdline_user_args()
	var from_user_args: String = _find_output_dir_arg(user_args)
	if not from_user_args.is_empty():
		return from_user_args

	var all_args: PackedStringArray = OS.get_cmdline_args()
	var from_all_args: String = _find_output_dir_arg(all_args)
	if not from_all_args.is_empty():
		return from_all_args

	return ""


func _find_output_dir_arg(args: PackedStringArray) -> String:
	for index in args.size():
		var argument: String = args[index]
		if argument == "--output-dir" and index + 1 < args.size():
			return args[index + 1]
		if argument.begins_with("--output-dir="):
			return argument.trim_prefix("--output-dir=")

	return ""


func _summarise_metrics(metrics_list: Array[CaptureMetrics]) -> Dictionary:
	var ac7_like_pass_count: int = 0
	var ac8_like_pass_count: int = 0
	var hips_visible_count: int = 0
	var extreme_total: int = 0

	for metrics: CaptureMetrics in metrics_list:
		if metrics.ac7_like_pass:
			ac7_like_pass_count += 1
		if metrics.ac8_like_pass:
			ac8_like_pass_count += 1
		if _is_extreme_pose_slug(metrics.pose):
			extreme_total += 1
			if metrics.hips_visible:
				hips_visible_count += 1

	return {
		"captures_total": metrics_list.size(),
		"ac7_like_pass": ac7_like_pass_count,
		"ac7_like_fail": metrics_list.size() - ac7_like_pass_count,
		"ac8_like_pass": ac8_like_pass_count,
		"ac8_like_fail": metrics_list.size() - ac8_like_pass_count,
		"extreme_hips_visible": hips_visible_count,
		"extreme_total": extreme_total,
	}


func _print_metrics_summary(metrics_list: Array[CaptureMetrics]) -> void:
	var summary: Dictionary = _summarise_metrics(metrics_list)
	print("Framing metrics summary: %s" % JSON.stringify(summary))


func _is_extreme_pose_slug(pose_slug: String) -> bool:
	return pose_slug == "stoop-forward" or pose_slug == "lean-back"


func _resolve_focus_point(subject: Node3D, marker_node: Node3D, pose_capture: Dictionary, angle_capture: Dictionary) -> Vector3:
	var torso_focus: Vector3 = subject.global_position + BASE_TORSO_FOCUS_OFFSET
	var marker_focus_offset: Vector3 = _read_pose_vector3(pose_capture, "focus_marker_offset", Vector3.ZERO)
	var head_focus: Vector3 = marker_node.global_position + marker_focus_offset
	var focus_head_weight: float = _read_pose_float(pose_capture, "focus_head_weight", DEFAULT_FOCUS_HEAD_WEIGHT)
	var base_focus: Vector3 = torso_focus.lerp(head_focus, focus_head_weight)

	var angle_focus_offset: Vector3 = _read_angle_focus_offset(pose_capture, String(angle_capture["slug"]))
	var per_angle_base_offset: Vector3 = _read_pose_vector3(angle_capture, "focus_offset", Vector3.ZERO)
	return base_focus + per_angle_base_offset + angle_focus_offset


func _read_angle_focus_offset(pose_capture: Dictionary, angle_slug: String) -> Vector3:
	var angle_offsets: Variant = pose_capture.get("angle_focus_offsets", {})
	if angle_offsets is Dictionary:
		var offsets_dict := angle_offsets as Dictionary
		var value: Variant = offsets_dict.get(angle_slug, Vector3.ZERO)
		if value is Vector3:
			return value

	return Vector3.ZERO


func _read_pose_vector3(config: Dictionary, key: String, fallback: Vector3) -> Vector3:
	var value: Variant = config.get(key, fallback)
	if value is Vector3:
		return value

	return fallback


func _read_pose_float(config: Dictionary, key: String, fallback: float) -> float:
	var value: Variant = config.get(key, fallback)
	if value is float:
		return value
	if value is int:
		return float(value)

	return fallback


func _attach_ik_target(female: Node3D) -> Node3D:
	var skeleton: Skeleton3D = _find_first_skeleton(female)
	if skeleton == null:
		push_error("No Skeleton3D found under Female in neck-spine subject scene")
		return null

	var ik_scene: PackedScene = ProbeUtils.load_scene(IK_SCENE_PATH)
	if ik_scene == null:
		return null

	var ik_instance: CCDIK3D = ik_scene.instantiate() as CCDIK3D
	if ik_instance == null:
		push_error("IK scene root is not CCDIK3D: %s" % IK_SCENE_PATH)
		return null

	skeleton.add_child(ik_instance)
	var ik_target: Node3D = ik_instance.get_node_or_null(^"HeadTarget") as Node3D
	if ik_target == null:
		push_error("IK scene is missing required node: HeadTarget")
		return null

	return ik_target


func _find_first_skeleton(node: Node) -> Skeleton3D:
	if node is Skeleton3D:
		return node as Skeleton3D

	for child: Node in node.get_children():
		var found: Skeleton3D = _find_first_skeleton(child)
		if found != null:
			return found

	return null
