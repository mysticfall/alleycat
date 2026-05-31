#!/usr/bin/env python3
"""Generate a MakeHuman/MPFB body collider rig from a source Blender file.

This is a Blender Python script. Run it with Blender, for example:

    blender --background --python tools/generate_body_colliders.py -- \
        --source game/assets/characters/reference/female/reference_female.blend \
        --output /tmp/reference_female_colliders.generated.blend

The generator intentionally uses only the exported body mesh under the source
armature. Clothing, hair, teeth, eyelashes, and other child meshes are ignored.

Generation rules encoded here:

* Duplicate the source armature as `Ragdoll` and keep all source bones.
* Generate one collider mesh for each major body bone used by the hand-authored
  reference collider rig.
* Split `Female.body_export` by deformation weights, not by object names. The
  default rule selects a face when its average weight for the collider's owning
  group is at least the configured threshold. This naturally keeps the small
  weighted overlap already present in the MPFB skinning data.
* Optional per-collider face-adjacency expansion can be enabled through
  `overlap_rings`, but the reference profile currently relies on skin-weight
  overlap only.
* Fill open boundary holes after each split so every collider is a closed mesh.
* Add, but do not apply, a Blender Decimate modifier. Extracted meshes use
  ratios matching the current hand-authored `colliders.blend` reference: most
  pieces use 0.10, upper legs use 0.08, and small breast/clavicle pieces use
  0.30. Primitive colliders use ratio 1.0 because they are already low-poly.
* Place each collider object's origin at the matching bone origin, with mesh
  vertices authored relative to that origin, then parent it to the bone.
"""

from __future__ import annotations

import argparse
from collections import defaultdict
from dataclasses import dataclass
from math import cos, pi, sin
from pathlib import Path
from typing import Iterable

import bpy
import bmesh
from mathutils import Vector
from mathutils.bvhtree import BVHTree


@dataclass(frozen=True)
class ColliderSpec:
    name: str
    bone: str
    groups: tuple[str, ...]
    decimate_ratio: float
    min_weight: float = 0.05
    overlap_rings: int = 0
    local_min: tuple[float | None, float | None, float | None] | None = None
    local_max: tuple[float | None, float | None, float | None] | None = None
    primitive: str | None = None
    primitive_segments: int = 12
    primitive_rings: int = 5
    profile_radius_quantile: float = 1.0
    profile_axis_trim: tuple[float, float] = (0.0, 0.0)
    profile_v_padding: tuple[float, float] = (0.0, 0.0)
    profile_v_offset: tuple[float, float] = (0.0, 0.0)
    symmetric_x: bool = False
    bounds_padding: tuple[float, float, float] = (0.0, 0.0, 0.0)
    mesh_padding: tuple[float, float, float] = (0.0, 0.0, 0.0)
    align_primitive_to_bone: bool = False


COLLIDERS: tuple[ColliderSpec, ...] = (
    ColliderSpec(
        "Pelvis-convcol",
        "pelvis",
        ("pelvis",),
        1.0,
        min_weight=0.20,
        primitive="shrinkwrap_ellipsoid",
        primitive_segments=18,
        primitive_rings=9,
        overlap_rings=1,
        symmetric_x=True,
        bounds_padding=(0.0, 0.01, 0.015),
    ),
    ColliderSpec(
        "Spine01-convcol",
        "spine_01",
        ("spine_01",),
        1.0,
        min_weight=0.35,
        primitive="shrinkwrap_ellipsoid",
        primitive_segments=16,
        primitive_rings=8,
        overlap_rings=1,
        symmetric_x=True,
        bounds_padding=(0.0, 0.02, 0.020),
    ),
    ColliderSpec(
        "Spine02-convcol",
        "spine_02",
        ("spine_02",),
        1.0,
        min_weight=0.15,
        primitive="shrinkwrap_ellipsoid",
        primitive_segments=18,
        primitive_rings=9,
        overlap_rings=2,
        symmetric_x=True,
        bounds_padding=(0.0, 0.025, 0.025),
    ),
    ColliderSpec(
        "Spine03-convcol",
        "spine_03",
        ("spine_03",),
        1.0,
        min_weight=0.45,
        primitive="shrinkwrap_ellipsoid",
        primitive_segments=20,
        primitive_rings=10,
        overlap_rings=2,
        symmetric_x=True,
        bounds_padding=(0.0, 0.025, 0.025),
    ),
    ColliderSpec("Breast_L-convcol", "breast_l", ("breast_l",), 1.0, min_weight=0.12, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=6, bounds_padding=(0.004, 0.004, 0.004)),
    ColliderSpec("Breast_R-convcol", "breast_r", ("breast_r",), 1.0, min_weight=0.12, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=6, bounds_padding=(0.004, 0.004, 0.004)),
    ColliderSpec("Clavicles_L-convcol", "clavicle_l", ("clavicle_l",), 1.0, min_weight=0.20, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=6, bounds_padding=(0.006, 0.006, 0.006)),
    ColliderSpec("Clavicle_R-convcol", "clavicle_r", ("clavicle_r",), 1.0, min_weight=0.20, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=6, bounds_padding=(0.006, 0.006, 0.006)),
    ColliderSpec(
        "Neck01-convcol",
        "neck_01",
        ("neck_01",),
        1.0,
        min_weight=0.10,
        primitive="shrinkwrap_profiled_tube",
        primitive_segments=12,
        primitive_rings=5,
        profile_radius_quantile=0.82,
        profile_axis_trim=(0.018, 0.0),
        profile_v_padding=(0.010, 0.006),
        profile_v_offset=(0.0, -0.018),
        symmetric_x=True,
        bounds_padding=(0.0, 0.004, 0.006),
        align_primitive_to_bone=True,
    ),
    ColliderSpec(
        "Head01-convcol",
        "head",
        ("head", "ears", "scalp", "lips"),
        1.0,
        local_min=(None, None, -0.0772),
        primitive="shrinkwrap_profiled_ellipsoid",
        primitive_segments=28,
        primitive_rings=16,
        symmetric_x=True,
        bounds_padding=(0.0, 0.01, 0.015),
    ),
    ColliderSpec(
        "UpperArm_L-convcol",
        "upperarm_l",
        ("upperarm_l",),
        1.0,
        min_weight=0.20,
        primitive="shrinkwrap_profiled_ellipsoid",
        primitive_segments=16,
        primitive_rings=10,
        profile_radius_quantile=0.80,
        bounds_padding=(0.003, 0.003, 0.006),
        align_primitive_to_bone=True,
    ),
    ColliderSpec(
        "UpperArm_R-convcol",
        "upperarm_r",
        ("upperarm_r",),
        1.0,
        min_weight=0.20,
        primitive="shrinkwrap_profiled_ellipsoid",
        primitive_segments=16,
        primitive_rings=10,
        profile_radius_quantile=0.80,
        bounds_padding=(0.003, 0.003, 0.006),
        align_primitive_to_bone=True,
    ),
    ColliderSpec(
        "LowerArm_L-convcol",
        "lowerarm_l",
        ("lowerarm_l",),
        1.0,
        min_weight=0.12,
        primitive="shrinkwrap_ellipsoid",
        primitive_segments=14,
        primitive_rings=8,
        overlap_rings=1,
        bounds_padding=(0.006, 0.006, 0.012),
        align_primitive_to_bone=True,
    ),
    ColliderSpec(
        "LowerArm_R-convcol",
        "lowerarm_r",
        ("lowerarm_r",),
        1.0,
        min_weight=0.12,
        primitive="shrinkwrap_ellipsoid",
        primitive_segments=14,
        primitive_rings=8,
        overlap_rings=1,
        bounds_padding=(0.006, 0.006, 0.012),
        align_primitive_to_bone=True,
    ),
    ColliderSpec("Hand_L-convcol", "hand_l", ("hand_l",), 1.0, min_weight=0.10, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=6, bounds_padding=(0.004, 0.004, 0.006)),
    ColliderSpec("Hand_R-convcol", "hand_r", ("hand_r",), 1.0, min_weight=0.10, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=6, bounds_padding=(0.004, 0.004, 0.006)),
    ColliderSpec("UpperLeg_L-convcol", "thigh_l", ("thigh_l",), 1.0, min_weight=0.25, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=16, primitive_rings=10, bounds_padding=(0.006, 0.006, 0.03)),
    ColliderSpec("UpperLeg_R-convcol", "thigh_r", ("thigh_r",), 1.0, min_weight=0.25, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=16, primitive_rings=10, bounds_padding=(0.006, 0.006, 0.03)),
    ColliderSpec("LowerLeg_L-convcol", "calf_l", ("calf_l",), 1.0, min_weight=0.40, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=14, primitive_rings=10, bounds_padding=(0.005, 0.005, 0.040)),
    ColliderSpec("LowerLeg_R-convcol", "calf_r", ("calf_r",), 1.0, min_weight=0.40, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=14, primitive_rings=10, bounds_padding=(0.005, 0.005, 0.040)),
    ColliderSpec("Foot_L-convcol", "foot_l", ("foot_l",), 1.0, min_weight=0.45, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=7, bounds_padding=(0.004, 0.008, 0.030)),
    ColliderSpec("Foot_R-convcol", "foot_r", ("foot_r",), 1.0, min_weight=0.40, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=7, bounds_padding=(0.004, 0.008, 0.030)),
    ColliderSpec("Ball_L-convcol", "ball_l", ("ball_l",), 1.0, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=5),
    ColliderSpec("Ball_R-convcol", "ball_r", ("ball_r",), 1.0, overlap_rings=1, primitive="shrinkwrap_ellipsoid", primitive_segments=12, primitive_rings=5),
)


# Local-space envelopes measured from the hand-authored collider rig. They keep
# broad skin-weight selections from bleeding into adjacent body regions while
# still deriving the actual surface from the source body mesh.
REFERENCE_LOCAL_BOUNDS: dict[str, tuple[tuple[float, float, float], tuple[float, float, float]]] = {
    "Pelvis-convcol": ((-0.1748, -0.1208, -0.1067), (0.1748, 0.1136, 0.1317)),
    "Spine01-convcol": ((-0.1412, -0.1667, -0.0013), (0.1412, 0.0252, 0.0965)),
    "Spine02-convcol": ((-0.1296, -0.1451, -0.0088), (0.1296, 0.0285, 0.1340)),
    "Spine03-convcol": ((-0.1452, -0.1336, -0.0096), (0.1452, 0.0744, 0.3163)),
    "Breast_L-convcol": ((0.0148, -0.1549, -0.1107), (0.1360, -0.0419, 0.0160)),
    "Breast_R-convcol": ((-0.1360, -0.1549, -0.1107), (-0.0148, -0.0419, 0.0160)),
    "Clavicles_L-convcol": ((0.0071, -0.0635, -0.0617), (0.2041, 0.0800, 0.0735)),
    "Clavicle_R-convcol": ((-0.2041, -0.0635, -0.0617), (-0.0071, 0.0800, 0.0735)),
    "Neck01-convcol": ((-0.0820, -0.0818, -0.0357), (0.0820, 0.0306, 0.0719)),
    "Head01-convcol": ((-0.0836, -0.1081, -0.0772), (0.0836, 0.0826, 0.1362)),
    "UpperArm_L-convcol": ((-0.0816, -0.0774, -0.2121), (0.1965, 0.0718, 0.0679)),
    "UpperArm_R-convcol": ((-0.1965, -0.0774, -0.2121), (0.0816, 0.0718, 0.0679)),
    "LowerArm_L-convcol": ((-0.0378, -0.1594, -0.1378), (0.1289, 0.0362, 0.0238)),
    "LowerArm_R-convcol": ((-0.1289, -0.1594, -0.1378), (0.0378, 0.0362, 0.0238)),
    "Hand_L-convcol": ((-0.0274, -0.0918, -0.0752), (0.0524, 0.0151, 0.0127)),
    "Hand_R-convcol": ((-0.0477, -0.0806, -0.0674), (0.0321, 0.0264, 0.0204)),
    "UpperLeg_L-convcol": ((-0.0846, -0.1185, -0.4127), (0.0898, 0.1027, -0.0095)),
    "UpperLeg_R-convcol": ((-0.0897, -0.1181, -0.4106), (0.0847, 0.1032, -0.0074)),
    "LowerLeg_L-convcol": ((-0.0476, -0.0535, -0.3515), (0.0752, 0.0853, 0.0180)),
    "LowerLeg_R-convcol": ((-0.0752, -0.0535, -0.3515), (0.0476, 0.0853, 0.0180)),
    "Foot_L-convcol": ((-0.0364, -0.1266, -0.0681), (0.0530, 0.0411, 0.0332)),
    "Foot_R-convcol": ((-0.0530, -0.1266, -0.0681), (0.0364, 0.0411, 0.0332)),
    "Ball_L-convcol": ((-0.0430, -0.0649, -0.0102), (0.0435, 0.0067, 0.0181)),
    "Ball_R-convcol": ((-0.0435, -0.0649, -0.0102), (0.0430, 0.0067, 0.0181)),
}


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, help="Source .blend file containing the exported character")
    parser.add_argument("--output", required=True, help="Generated collider .blend output path")
    parser.add_argument("--armature", default="Female_export", help="Source armature object name")
    parser.add_argument("--body", default="Female.body_export", help="Source body mesh object name")
    parser.add_argument("--apply-decimate", action="store_true", help="Apply decimate modifiers instead of leaving them live")
    args = parser.parse_args(_script_args())
    args.source = str(Path(args.source).resolve())
    args.output = str(Path(args.output).resolve())
    return args


def _script_args() -> list[str]:
    import sys

    if "--" not in sys.argv:
        return []
    return sys.argv[sys.argv.index("--") + 1 :]


def require_object(name: str, expected_type: str) -> bpy.types.Object:
    obj = bpy.data.objects.get(name)
    if obj is None:
        raise RuntimeError(f"Missing object: {name}")
    if obj.type != expected_type:
        raise RuntimeError(f"Object {name} is {obj.type}, expected {expected_type}")
    return obj


def reset_armature_pose_for_collider_generation(armature: bpy.types.Object) -> None:
    """Clear action state and pose offsets so colliders use rest/default bone origins."""

    armature.animation_data_clear()

    if armature.pose is not None:
        for pose_bone in armature.pose.bones:
            pose_bone.location = (0.0, 0.0, 0.0)
            pose_bone.rotation_euler = (0.0, 0.0, 0.0)
            pose_bone.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)
            pose_bone.rotation_axis_angle = (0.0, 0.0, 1.0, 0.0)
            pose_bone.scale = (1.0, 1.0, 1.0)
            pose_bone.matrix_basis.identity()

    armature.data.pose_position = "POSE"
    bpy.context.view_layer.update()


def reset_body_animation_for_collider_generation(body: bpy.types.Object) -> None:
    """Clear mesh animation state before reading collider source vertices."""

    body.animation_data_clear()
    if body.data is not None:
        body.data.animation_data_clear()
        if body.data.shape_keys is not None:
            body.data.shape_keys.animation_data_clear()


def duplicate_armature(source: bpy.types.Object) -> bpy.types.Object:
    armature = source.copy()
    armature.data = source.data.copy()
    armature.name = "Ragdoll"
    armature.data.name = "Ragdoll"
    reset_armature_pose_for_collider_generation(armature)
    return armature


def vertex_group_weights(body: bpy.types.Object) -> tuple[list[dict[str, float]], dict[str, int]]:
    group_names = {group.index: group.name for group in body.vertex_groups}
    group_indices = {group.name: group.index for group in body.vertex_groups}
    weights: list[dict[str, float]] = []

    for vertex in body.data.vertices:
        row: dict[str, float] = {}
        for group in vertex.groups:
            name = group_names.get(group.group)
            if name:
                row[name] = group.weight
        weights.append(row)

    return weights, group_indices


def score_vertex(weights: dict[str, float], groups: Iterable[str]) -> float:
    return sum(weights.get(group, 0.0) for group in groups)


def build_face_neighbours(mesh: bpy.types.Mesh) -> list[set[int]]:
    edge_to_faces: dict[tuple[int, int], list[int]] = defaultdict(list)
    for polygon in mesh.polygons:
        vertices = list(polygon.vertices)
        for index, vertex in enumerate(vertices):
            other = vertices[(index + 1) % len(vertices)]
            edge_to_faces[tuple(sorted((vertex, other)))].append(polygon.index)

    neighbours = [set() for _ in mesh.polygons]
    for faces in edge_to_faces.values():
        if len(faces) < 2:
            continue
        for face in faces:
            neighbours[face].update(other for other in faces if other != face)
    return neighbours


def select_faces(
    mesh: bpy.types.Mesh,
    spec: ColliderSpec,
    weights: list[dict[str, float]],
    neighbours: list[set[int]],
    local_vertices: list[tuple[float, float, float]],
) -> set[int]:
    selected: set[int] = set()

    for polygon in mesh.polygons:
        vertex_scores = [score_vertex(weights[index], spec.groups) for index in polygon.vertices]
        if vertex_scores and sum(vertex_scores) / len(vertex_scores) >= spec.min_weight and within_local_bounds(polygon, spec, local_vertices):
            selected.add(polygon.index)

    for _ in range(spec.overlap_rings):
        expanded = set(selected)
        for face_index in selected:
            expanded.update(neighbours[face_index])
        selected = expanded

    return selected


def within_local_bounds(
    polygon: bpy.types.MeshPolygon,
    spec: ColliderSpec,
    local_vertices: list[tuple[float, float, float]],
) -> bool:
    reference_bounds = padded_reference_bounds(spec)
    local_min = spec.local_min if spec.local_min is not None else reference_bounds[0] if reference_bounds else None
    local_max = spec.local_max if spec.local_max is not None else reference_bounds[1] if reference_bounds else None

    if local_min is None and local_max is None:
        return True

    centroid = [0.0, 0.0, 0.0]
    for vertex_index in polygon.vertices:
        vertex = local_vertices[vertex_index]
        centroid[0] += vertex[0]
        centroid[1] += vertex[1]
        centroid[2] += vertex[2]
    centroid = [value / len(polygon.vertices) for value in centroid]

    if local_min is not None:
        for axis, minimum in enumerate(local_min):
            if minimum is not None and centroid[axis] < minimum:
                return False
    if local_max is not None:
        for axis, maximum in enumerate(local_max):
            if maximum is not None and centroid[axis] > maximum:
                return False
    return True


def padded_reference_bounds(
    spec: ColliderSpec,
) -> tuple[tuple[float, float, float], tuple[float, float, float]] | None:
    bounds = REFERENCE_LOCAL_BOUNDS.get(spec.name)
    if bounds is None:
        return None

    minimum, maximum = bounds
    padding = spec.bounds_padding
    return (
        (minimum[0] - padding[0], minimum[1] - padding[1], minimum[2] - padding[2]),
        (maximum[0] + padding[0], maximum[1] + padding[1], maximum[2] + padding[2]),
    )


def mesh_from_faces(
    source: bpy.types.Object,
    face_indices: set[int],
    mesh_name: str,
    origin: bpy.types.Vector,
) -> bpy.types.Mesh:
    source_mesh = source.data
    vertex_map: dict[int, int] = {}
    vertices: list[tuple[float, float, float]] = []
    faces: list[list[int]] = []

    world_from_source = source.matrix_world
    for polygon in source_mesh.polygons:
        if polygon.index not in face_indices:
            continue
        face: list[int] = []
        for old_index in polygon.vertices:
            if old_index not in vertex_map:
                vertex_map[old_index] = len(vertices)
                vertices.append(tuple((world_from_source @ source_mesh.vertices[old_index].co) - origin))
            face.append(vertex_map[old_index])
        faces.append(face)

    mesh = bpy.data.meshes.new(mesh_name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    fill_boundary_holes(mesh)
    return mesh


def ellipsoid_mesh(
    mesh_name: str,
    bounds: tuple[tuple[float, float, float], tuple[float, float, float]],
    segments: int = 12,
    rings: int = 5,
) -> bpy.types.Mesh:
    minimum, maximum = bounds
    centre = Vector(((minimum[0] + maximum[0]) * 0.5, (minimum[1] + maximum[1]) * 0.5, (minimum[2] + maximum[2]) * 0.5))
    axis = Vector((0.0, 0.0, 1.0))
    u_axis = Vector((1.0, 0.0, 0.0))
    v_axis = Vector((0.0, 1.0, 0.0))
    radius = tuple((maximum[index] - minimum[index]) * 0.5 for index in range(3))
    return ellipsoid_mesh_from_basis(mesh_name, centre, axis, u_axis, v_axis, radius[2], radius[0], radius[1], segments, rings)


def bone_aligned_ellipsoid_mesh(
    mesh_name: str,
    bounds: tuple[tuple[float, float, float], tuple[float, float, float]],
    axis: Vector,
    segments: int = 12,
    rings: int = 5,
) -> bpy.types.Mesh:
    minimum, maximum = bounds
    centre = Vector(((minimum[0] + maximum[0]) * 0.5, (minimum[1] + maximum[1]) * 0.5, (minimum[2] + maximum[2]) * 0.5))
    axis = axis.normalized()
    helper = Vector((0.0, 0.0, 1.0)) if abs(axis.z) < 0.9 else Vector((0.0, 1.0, 0.0))
    u_axis = helper.cross(axis).normalized()
    v_axis = axis.cross(u_axis).normalized()

    corners = [
        Vector((x, y, z))
        for x in (minimum[0], maximum[0])
        for y in (minimum[1], maximum[1])
        for z in (minimum[2], maximum[2])
    ]
    offsets = [corner - centre for corner in corners]
    axis_radius = max(abs(offset.dot(axis)) for offset in offsets)
    u_radius = max(abs(offset.dot(u_axis)) for offset in offsets)
    v_radius = max(abs(offset.dot(v_axis)) for offset in offsets)
    return ellipsoid_mesh_from_basis(mesh_name, centre, axis, u_axis, v_axis, axis_radius, u_radius, v_radius, segments, rings)


def target_aligned_ellipsoid_mesh(
    mesh_name: str,
    target: bpy.types.Mesh,
    axis: Vector,
    segments: int = 12,
    rings: int = 5,
) -> bpy.types.Mesh:
    axis = axis.normalized()
    helper = Vector((0.0, 0.0, 1.0)) if abs(axis.z) < 0.9 else Vector((0.0, 1.0, 0.0))
    u_axis = helper.cross(axis).normalized()
    v_axis = axis.cross(u_axis).normalized()
    centre = sum((vertex.co for vertex in target.vertices), Vector()) / len(target.vertices)
    offsets = [vertex.co - centre for vertex in target.vertices]
    axis_radius = max(abs(offset.dot(axis)) for offset in offsets)
    u_radius = max(abs(offset.dot(u_axis)) for offset in offsets)
    v_radius = max(abs(offset.dot(v_axis)) for offset in offsets)
    return ellipsoid_mesh_from_basis(mesh_name, centre, axis, u_axis, v_axis, axis_radius, u_radius, v_radius, segments, rings)


def target_profiled_ellipsoid_mesh(
    mesh_name: str,
    target: bpy.types.Mesh,
    axis: Vector,
    segments: int = 12,
    rings: int = 5,
    radius_quantile: float = 1.0,
) -> bpy.types.Mesh:
    axis = axis.normalized()
    helper = Vector((0.0, 0.0, 1.0)) if abs(axis.z) < 0.9 else Vector((0.0, 1.0, 0.0))
    u_axis = helper.cross(axis).normalized()
    v_axis = axis.cross(u_axis).normalized()
    centre = Vector((0.0, 0.0, 0.0))
    for vertex in target.vertices:
        centre += vertex.co
    centre /= len(target.vertices)

    samples: list[tuple[float, float, float]] = []
    for vertex in target.vertices:
        offset = vertex.co - centre
        samples.append((offset.dot(axis), abs(offset.dot(u_axis)), abs(offset.dot(v_axis))))

    min_axis = min(sample[0] for sample in samples)
    max_axis = max(sample[0] for sample in samples)
    axis_radius = (max_axis - min_axis) * 0.5
    axis_centre = (min_axis + max_axis) * 0.5
    centre += axis * axis_centre
    global_u_radius = quantile([sample[1] for sample in samples], radius_quantile)
    global_v_radius = quantile([sample[2] for sample in samples], radius_quantile)
    band_width = (max_axis - min_axis) / max(rings, 1)

    vertices: list[tuple[float, float, float]] = [tuple(centre + axis * axis_radius)]
    faces: list[list[int]] = []

    for ring in range(1, rings + 1):
        phi = pi * ring / (rings + 1)
        axis_offset = cos(phi) * axis_radius
        axis_position = axis_centre + axis_offset
        band = [sample for sample in samples if abs(sample[0] - axis_position) <= band_width]
        if not band:
            band = samples
        u_radius = quantile([sample[1] for sample in band], radius_quantile)
        v_radius = quantile([sample[2] for sample in band], radius_quantile)

        # Keep a minimum cross-section so sparse end slices do not collapse.
        u_radius = max(u_radius, global_u_radius * 0.35)
        v_radius = max(v_radius, global_v_radius * 0.35)
        for segment in range(segments):
            theta = 2.0 * pi * segment / segments
            radial_offset = u_axis * (cos(theta) * u_radius) + v_axis * (sin(theta) * v_radius)
            vertices.append(tuple(centre + axis * axis_offset + radial_offset))
    vertices.append(tuple(centre - axis * axis_radius))

    bottom_index = len(vertices) - 1
    for segment in range(segments):
        next_segment = (segment + 1) % segments
        faces.append([0, 1 + next_segment, 1 + segment])

    for ring in range(rings - 1):
        row = 1 + ring * segments
        next_row = row + segments
        for segment in range(segments):
            next_segment = (segment + 1) % segments
            faces.append([row + segment, row + next_segment, next_row + next_segment, next_row + segment])

    last_row = 1 + (rings - 1) * segments
    for segment in range(segments):
        next_segment = (segment + 1) % segments
        faces.append([last_row + segment, last_row + next_segment, bottom_index])

    mesh = bpy.data.meshes.new(mesh_name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    return mesh


def target_profiled_tube_mesh(
    mesh_name: str,
    target: bpy.types.Mesh,
    axis: Vector,
    segments: int = 12,
    rings: int = 5,
    radius_quantile: float = 1.0,
    axis_trim: tuple[float, float] = (0.0, 0.0),
    v_padding: tuple[float, float] = (0.0, 0.0),
    v_offset: tuple[float, float] = (0.0, 0.0),
) -> bpy.types.Mesh:
    axis = axis.normalized()
    helper = Vector((0.0, 0.0, 1.0)) if abs(axis.z) < 0.9 else Vector((0.0, 1.0, 0.0))
    u_axis = helper.cross(axis).normalized()
    v_axis = axis.cross(u_axis).normalized()
    centre = sum((vertex.co for vertex in target.vertices), Vector()) / len(target.vertices)

    samples: list[tuple[float, float, float]] = []
    for vertex in target.vertices:
        offset = vertex.co - centre
        samples.append((offset.dot(axis), abs(offset.dot(u_axis)), abs(offset.dot(v_axis))))

    min_axis = min(sample[0] for sample in samples) + axis_trim[1]
    max_axis = max(sample[0] for sample in samples) - axis_trim[0]
    if min_axis >= max_axis:
        min_axis = min(sample[0] for sample in samples)
        max_axis = max(sample[0] for sample in samples)
    global_u_radius = quantile([sample[1] for sample in samples], radius_quantile)
    global_v_radius = quantile([sample[2] for sample in samples], radius_quantile)
    band_width = (max_axis - min_axis) / max(rings - 1, 1)

    vertices: list[tuple[float, float, float]] = []
    faces: list[list[int]] = []
    for ring in range(rings):
        t = ring / max(rings - 1, 1)
        axis_position = max_axis + (min_axis - max_axis) * t
        ring_centre_offset = v_axis * (v_offset[0] + (v_offset[1] - v_offset[0]) * t)
        band = [sample for sample in samples if abs(sample[0] - axis_position) <= band_width]
        if not band:
            band = samples
        u_radius = max(quantile([sample[1] for sample in band], radius_quantile), global_u_radius * 0.35)
        v_radius = max(quantile([sample[2] for sample in band], radius_quantile), global_v_radius * 0.35)
        for segment in range(segments):
            theta = 2.0 * pi * segment / segments
            v_sign = sin(theta)
            padded_v_radius = v_radius + (v_padding[1] if v_sign >= 0.0 else v_padding[0])
            radial_offset = u_axis * (cos(theta) * u_radius) + v_axis * (v_sign * padded_v_radius)
            vertices.append(tuple(centre + axis * axis_position + ring_centre_offset + radial_offset))

    for ring in range(rings - 1):
        row = ring * segments
        next_row = row + segments
        for segment in range(segments):
            next_segment = (segment + 1) % segments
            faces.append([row + segment, row + next_segment, next_row + next_segment, next_row + segment])

    top_centre = len(vertices)
    vertices.append(tuple(centre + axis * max_axis + v_axis * v_offset[0]))
    bottom_centre = len(vertices)
    vertices.append(tuple(centre + axis * min_axis + v_axis * v_offset[1]))
    last_row = (rings - 1) * segments
    for segment in range(segments):
        next_segment = (segment + 1) % segments
        faces.append([top_centre, next_segment, segment])
        faces.append([bottom_centre, last_row + segment, last_row + next_segment])

    mesh = bpy.data.meshes.new(mesh_name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    return mesh


def quantile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    if fraction >= 1.0:
        return max(values)
    if fraction <= 0.0:
        return min(values)
    ordered = sorted(values)
    index = round((len(ordered) - 1) * fraction)
    return ordered[index]


def target_ellipsoid_mesh(
    mesh_name: str,
    target: bpy.types.Mesh,
    segments: int = 12,
    rings: int = 5,
) -> bpy.types.Mesh:
    minimum = Vector((min(vertex.co.x for vertex in target.vertices), min(vertex.co.y for vertex in target.vertices), min(vertex.co.z for vertex in target.vertices)))
    maximum = Vector((max(vertex.co.x for vertex in target.vertices), max(vertex.co.y for vertex in target.vertices), max(vertex.co.z for vertex in target.vertices)))
    centre = (minimum + maximum) * 0.5
    radii = (maximum - minimum) * 0.5
    return ellipsoid_mesh_from_basis(
        mesh_name,
        centre,
        Vector((0.0, 0.0, 1.0)),
        Vector((1.0, 0.0, 0.0)),
        Vector((0.0, 1.0, 0.0)),
        radii.z,
        radii.x,
        radii.y,
        segments,
        rings,
    )


def ellipsoid_mesh_from_basis(
    mesh_name: str,
    centre: Vector,
    axis: Vector,
    u_axis: Vector,
    v_axis: Vector,
    axis_radius: float,
    u_radius: float,
    v_radius: float,
    segments: int,
    rings: int,
) -> bpy.types.Mesh:
    vertices: list[tuple[float, float, float]] = []
    faces: list[list[int]] = []

    vertices.append(tuple(centre + axis * axis_radius))
    for ring in range(1, rings + 1):
        phi = pi * ring / (rings + 1)
        axis_offset = axis * (cos(phi) * axis_radius)
        xy_scale = sin(phi)
        for segment in range(segments):
            theta = 2.0 * pi * segment / segments
            radial_offset = u_axis * (cos(theta) * u_radius * xy_scale) + v_axis * (sin(theta) * v_radius * xy_scale)
            vertices.append(tuple(centre + axis_offset + radial_offset))
    vertices.append(tuple(centre - axis * axis_radius))

    bottom_index = len(vertices) - 1
    for segment in range(segments):
        next_segment = (segment + 1) % segments
        faces.append([0, 1 + next_segment, 1 + segment])

    for ring in range(rings - 1):
        row = 1 + ring * segments
        next_row = row + segments
        for segment in range(segments):
            next_segment = (segment + 1) % segments
            faces.append([row + segment, row + next_segment, next_row + next_segment, next_row + segment])

    last_row = 1 + (rings - 1) * segments
    for segment in range(segments):
        next_segment = (segment + 1) % segments
        faces.append([last_row + segment, last_row + next_segment, bottom_index])

    mesh = bpy.data.meshes.new(mesh_name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    return mesh


def shrinkwrap_mesh_to_mesh(mesh: bpy.types.Mesh, target: bpy.types.Mesh) -> None:
    target_vertices = [vertex.co.copy() for vertex in target.vertices]
    target_polygons = [list(polygon.vertices) for polygon in target.polygons]
    tree = BVHTree.FromPolygons(target_vertices, target_polygons, epsilon=0.0)

    for vertex in mesh.vertices:
        position, _, _, _ = tree.find_nearest(vertex.co)
        if position is not None:
            vertex.co = position
    mesh.update(calc_edges=True)


def pad_mesh_axes(mesh: bpy.types.Mesh, padding: tuple[float, float, float]) -> None:
    if padding == (0.0, 0.0, 0.0):
        return

    centre = Vector((0.0, 0.0, 0.0))
    for vertex in mesh.vertices:
        centre += vertex.co
    centre /= len(mesh.vertices)
    for vertex in mesh.vertices:
        if padding[0] != 0.0:
            offset = vertex.co.x - centre.x
            if offset > 0.0:
                vertex.co.x += padding[0]
            elif offset < 0.0:
                vertex.co.x -= padding[0]
        if padding[1] != 0.0:
            offset = vertex.co.y - centre.y
            if offset > 0.0:
                vertex.co.y += padding[1]
            elif offset < 0.0:
                vertex.co.y -= padding[1]
        if padding[2] != 0.0:
            offset = vertex.co.z - centre.z
            if offset > 0.0:
                vertex.co.z += padding[2]
            elif offset < 0.0:
                vertex.co.z -= padding[2]
    mesh.update(calc_edges=True)


def symmetrise_mesh_x(mesh: bpy.types.Mesh, tolerance: float = 0.004) -> None:
    processed: set[int] = set()
    tolerance_squared = tolerance * tolerance

    for index, vertex in enumerate(mesh.vertices):
        if index in processed:
            continue

        if abs(vertex.co.x) <= tolerance:
            vertex.co.x = 0.0
            processed.add(index)
            continue

        target = (-vertex.co.x, vertex.co.y, vertex.co.z)
        best_index = -1
        best_distance = tolerance_squared
        for other_index, other in enumerate(mesh.vertices):
            if other_index == index or other_index in processed:
                continue
            distance = (other.co.x - target[0]) ** 2 + (other.co.y - target[1]) ** 2 + (other.co.z - target[2]) ** 2
            if distance <= best_distance:
                best_distance = distance
                best_index = other_index

        if best_index < 0:
            continue

        other = mesh.vertices[best_index]
        average_x = (abs(vertex.co.x) + abs(other.co.x)) * 0.5
        average_y = (vertex.co.y + other.co.y) * 0.5
        average_z = (vertex.co.z + other.co.z) * 0.5
        sign = 1.0 if vertex.co.x >= 0.0 else -1.0
        vertex.co.x = sign * average_x
        other.co.x = -sign * average_x
        vertex.co.y = other.co.y = average_y
        vertex.co.z = other.co.z = average_z
        processed.add(index)
        processed.add(best_index)

    mesh.update(calc_edges=True)


def symmetrise_ellipsoid_mesh_x(mesh: bpy.types.Mesh, segments: int, rings: int) -> None:
    mesh.vertices[0].co.x = 0.0
    mesh.vertices[len(mesh.vertices) - 1].co.x = 0.0

    half_segments = segments // 2
    for ring in range(rings):
        row = 1 + ring * segments
        processed: set[int] = set()

        for segment in range(segments):
            if segment in processed:
                continue

            opposite_segment = (half_segments - segment) % segments
            if opposite_segment == segment:
                mesh.vertices[row + segment].co.x = 0.0
                processed.add(segment)
                continue

            vertex = mesh.vertices[row + segment]
            opposite = mesh.vertices[row + opposite_segment]
            average_x = (abs(vertex.co.x) + abs(opposite.co.x)) * 0.5
            average_y = (vertex.co.y + opposite.co.y) * 0.5
            average_z = (vertex.co.z + opposite.co.z) * 0.5
            sign = 1.0 if cos(2.0 * pi * segment / segments) >= 0.0 else -1.0
            vertex.co.x = sign * average_x
            opposite.co.x = -sign * average_x
            vertex.co.y = opposite.co.y = average_y
            vertex.co.z = opposite.co.z = average_z
            processed.add(segment)
            processed.add(opposite_segment)

    mesh.update(calc_edges=True)


def symmetrise_profiled_tube_mesh_x(mesh: bpy.types.Mesh, segments: int, rings: int) -> None:
    half_segments = segments // 2
    for ring in range(rings):
        row = ring * segments
        processed: set[int] = set()
        for segment in range(segments):
            if segment in processed:
                continue

            opposite_segment = (half_segments - segment) % segments
            if opposite_segment == segment:
                mesh.vertices[row + segment].co.x = 0.0
                processed.add(segment)
                continue

            vertex = mesh.vertices[row + segment]
            opposite = mesh.vertices[row + opposite_segment]
            average_x = (abs(vertex.co.x) + abs(opposite.co.x)) * 0.5
            average_y = (vertex.co.y + opposite.co.y) * 0.5
            average_z = (vertex.co.z + opposite.co.z) * 0.5
            sign = 1.0 if cos(2.0 * pi * segment / segments) >= 0.0 else -1.0
            vertex.co.x = sign * average_x
            opposite.co.x = -sign * average_x
            vertex.co.y = opposite.co.y = average_y
            vertex.co.z = opposite.co.z = average_z
            processed.add(segment)
            processed.add(opposite_segment)

    mesh.vertices[len(mesh.vertices) - 2].co.x = 0.0
    mesh.vertices[len(mesh.vertices) - 1].co.x = 0.0
    mesh.update(calc_edges=True)


def convex_hull_mesh(source_mesh: bpy.types.Mesh, mesh_name: str) -> bpy.types.Mesh:
    bm = bmesh.new()
    bm.from_mesh(source_mesh)
    geom = list(bm.verts) + list(bm.edges) + list(bm.faces)
    result = bmesh.ops.convex_hull(bm, input=geom, use_existing_faces=False)
    delete_geom = []
    for key in ("geom_interior", "geom_unused", "geom_holes"):
        delete_geom.extend(result.get(key, []))
    if delete_geom:
        bmesh.ops.delete(bm, geom=list(set(delete_geom)), context="VERTS")
    bm.normal_update()

    mesh = bpy.data.meshes.new(mesh_name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.update(calc_edges=True)
    return mesh


def mirrored_mesh_x(source: bpy.types.Mesh, mesh_name: str) -> bpy.types.Mesh:
    vertices = [(-vertex.co.x, vertex.co.y, vertex.co.z) for vertex in source.vertices]
    faces = [list(reversed(polygon.vertices)) for polygon in source.polygons]
    mesh = bpy.data.meshes.new(mesh_name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update(calc_edges=True)
    return mesh


def mirror_left_limbs_from_right(generated_meshes: list[tuple[ColliderSpec, bpy.types.Mesh]]) -> None:
    source_by_name = {spec.name: mesh for spec, mesh in generated_meshes}
    source_names = {
        "UpperArm_L-convcol": "UpperArm_R-convcol",
        "LowerArm_L-convcol": "LowerArm_R-convcol",
        "Hand_L-convcol": "Hand_R-convcol",
        "UpperLeg_L-convcol": "UpperLeg_R-convcol",
        "LowerLeg_L-convcol": "LowerLeg_R-convcol",
        "Foot_L-convcol": "Foot_R-convcol",
        "Ball_L-convcol": "Ball_R-convcol",
    }

    for index, (spec, mesh) in enumerate(generated_meshes):
        source_name = source_names.get(spec.name)
        if source_name is None:
            continue
        source_mesh = source_by_name.get(source_name)
        if source_mesh is None:
            continue
        replacement = mirrored_mesh_x(source_mesh, spec.name)
        bpy.data.meshes.remove(mesh)
        generated_meshes[index] = (spec, replacement)


def fill_boundary_holes(mesh: bpy.types.Mesh) -> None:
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.edges.ensure_lookup_table()
    boundary_edges = [edge for edge in bm.edges if edge.is_boundary]
    if boundary_edges:
        bmesh.ops.holes_fill(bm, edges=boundary_edges, sides=0)
    bm.normal_update()
    bm.to_mesh(mesh)
    bm.free()
    mesh.update(calc_edges=True)


def parent_to_bone(obj: bpy.types.Object, armature: bpy.types.Object, bone_name: str) -> None:
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        raise RuntimeError(f"Armature {armature.name} has no bone named {bone_name}")

    obj.parent = armature
    obj.parent_type = "BONE"
    obj.parent_bone = bone_name
    obj.location = armature.matrix_world @ bone.head_local
    obj.rotation_euler = (0.0, 0.0, 0.0)
    obj.scale = (1.0, 1.0, 1.0)
    obj.matrix_parent_inverse = (armature.matrix_world @ bone.matrix_local).inverted() @ obj.matrix_world

    # Bone parenting composes through the bone transform after the parent
    # inverse. Re-anchor the evaluated object origin to the rest-pose bone head
    # so Blender solves the final inverse exactly like the hand-authored rig.
    matrix = obj.matrix_world.copy()
    matrix.translation = armature.matrix_world @ bone.head_local
    obj.matrix_world = matrix


def bone_origin(armature: bpy.types.Object, bone_name: str) -> bpy.types.Vector:
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        raise RuntimeError(f"Armature {armature.name} has no bone named {bone_name}")
    return armature.matrix_world @ bone.head_local


def bone_axis(armature: bpy.types.Object, bone_name: str) -> Vector:
    bone = armature.data.bones.get(bone_name)
    if bone is None:
        raise RuntimeError(f"Armature {armature.name} has no bone named {bone_name}")
    return ((armature.matrix_world @ bone.tail_local) - (armature.matrix_world @ bone.head_local)).normalized()


def add_decimate(obj: bpy.types.Object, ratio: float, apply: bool) -> None:
    modifier = obj.modifiers.new("Decimate", "DECIMATE")
    modifier.decimate_type = "COLLAPSE"
    modifier.ratio = ratio
    if apply:
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        obj.select_set(False)


def smooth_faces(mesh: bpy.types.Mesh) -> None:
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    mesh.update()


def make_mesh_normals_consistent(obj: bpy.types.Object) -> None:
    """Recalculate mesh normals outward with Blender's mesh operator."""

    if obj.type != "MESH":
        raise TypeError(f"Object {obj.name} is not a mesh")

    view_layer = bpy.context.view_layer
    previous_active = view_layer.objects.active
    previous_selection = list(bpy.context.selected_objects)

    try:
        if previous_active is not None and previous_active.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")

        bpy.ops.object.select_all(action="DESELECT")
        view_layer.objects.active = obj
        obj.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="SELECT")
        bpy.ops.mesh.normals_make_consistent(inside=False)
        bpy.ops.object.mode_set(mode="OBJECT")
    finally:
        if view_layer.objects.active is not None and view_layer.objects.active.mode != "OBJECT":
            bpy.ops.object.mode_set(mode="OBJECT")
        bpy.ops.object.select_all(action="DESELECT")
        view_layer_object_names = {layer_obj.name for layer_obj in view_layer.objects}
        for selected in previous_selection:
            if selected.name in view_layer_object_names:
                selected.select_set(True)
        if previous_active is not None and previous_active.name in view_layer_object_names:
            view_layer.objects.active = previous_active


def clear_scene() -> None:
    for obj in list(bpy.context.scene.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def purge_orphan_datablocks() -> None:
    """Remove unused datablocks so source-only data is not saved to the collider file."""

    try:
        bpy.ops.outliner.orphans_purge(
            do_local_ids=True,
            do_linked_ids=True,
            do_recursive=True,
        )
    except RuntimeError:
        # Some Blender builds require an Outliner context for this operator. The explicit
        # action/library stripping below is the required cleanup path; orphan purging is
        # only a best-effort sweep for unrelated unused source data.
        pass


def remove_datablock(collection: object, datablock: bpy.types.ID) -> None:
    """Remove a Blender ID datablock from its collection, unlinking users when supported."""

    remove = getattr(collection, "remove")
    try:
        remove(datablock, do_unlink=True)
    except TypeError:
        remove(datablock)


def strip_actions_and_linked_libraries() -> None:
    """Strip animation actions and linked source libraries from generated collider output."""

    for obj in bpy.data.objects:
        obj.animation_data_clear()

    for action in list(bpy.data.actions):
        action.user_clear()
        remove_datablock(bpy.data.actions, action)

    linked_datablocks: list[tuple[object, bpy.types.ID]] = []
    for collection_name in dir(bpy.data):
        if collection_name.startswith("_") or collection_name == "libraries":
            continue

        collection = getattr(bpy.data, collection_name)
        if not hasattr(collection, "remove"):
            continue

        try:
            datablocks = list(collection)
        except TypeError:
            continue

        linked_datablocks.extend(
            (collection, datablock)
            for datablock in datablocks
            if getattr(datablock, "library", None) is not None
        )

    for collection, datablock in linked_datablocks:
        try:
            datablock.user_clear()
        except RuntimeError:
            pass
        remove_datablock(collection, datablock)

    purge_orphan_datablocks()

    for library in list(bpy.data.libraries):
        try:
            bpy.data.libraries.remove(library)
        except RuntimeError:
            pass


def generate(args: argparse.Namespace) -> None:
    bpy.ops.wm.open_mainfile(filepath=args.source)
    source_armature = require_object(args.armature, "ARMATURE")
    body = require_object(args.body, "MESH")
    reset_armature_pose_for_collider_generation(source_armature)
    reset_body_animation_for_collider_generation(body)
    bpy.context.scene.frame_set(bpy.context.scene.frame_start)
    reset_armature_pose_for_collider_generation(source_armature)

    weights, group_indices = vertex_group_weights(body)
    missing_groups = sorted({group for spec in COLLIDERS for group in spec.groups if group not in group_indices})
    if missing_groups:
        print(f"Warning: missing vertex groups ignored: {', '.join(missing_groups)}")

    neighbours = build_face_neighbours(body.data)
    armature = duplicate_armature(source_armature)

    generated_meshes: list[tuple[ColliderSpec, bpy.types.Mesh]] = []
    for spec in COLLIDERS:
        if source_armature.data.bones.get(spec.bone) is None:
            print(f"Warning: skipping {spec.name}; armature has no bone named {spec.bone}")
            continue
        if not any(group in group_indices for group in spec.groups):
            print(f"Warning: skipping {spec.name}; body has none of its vertex groups")
            continue

        origin = bone_origin(source_armature, spec.bone)
        local_vertices = [tuple((body.matrix_world @ vertex.co) - origin) for vertex in body.data.vertices]
        if spec.primitive == "ellipsoid":
            bounds = padded_reference_bounds(spec)
            if bounds is None:
                raise RuntimeError(f"Missing local bounds for primitive collider {spec.name}")
            if spec.align_primitive_to_bone:
                mesh = bone_aligned_ellipsoid_mesh(
                    spec.name,
                    bounds,
                    bone_axis(source_armature, spec.bone),
                    spec.primitive_segments,
                    spec.primitive_rings,
                )
            else:
                mesh = ellipsoid_mesh(spec.name, bounds, spec.primitive_segments, spec.primitive_rings)
            if spec.symmetric_x:
                symmetrise_ellipsoid_mesh_x(mesh, spec.primitive_segments, spec.primitive_rings)
            generated_meshes.append((spec, mesh))
            continue

        face_indices = select_faces(body.data, spec, weights, neighbours, local_vertices)
        if not face_indices:
            print(f"Warning: no faces selected for {spec.name}")
            continue
        mesh = mesh_from_faces(body, face_indices, spec.name, origin)
        if spec.primitive in {"shrinkwrap_ellipsoid", "shrinkwrap_profiled_ellipsoid", "shrinkwrap_profiled_tube"}:
            bounds = padded_reference_bounds(spec)
            if bounds is None:
                raise RuntimeError(f"Missing local bounds for primitive collider {spec.name}")
            target_mesh = mesh
            if spec.primitive == "shrinkwrap_profiled_ellipsoid":
                profile_axis = bone_axis(source_armature, spec.bone) if spec.align_primitive_to_bone else Vector((0.0, 0.0, 1.0))
                mesh = target_profiled_ellipsoid_mesh(
                    spec.name,
                    target_mesh,
                    profile_axis,
                    spec.primitive_segments,
                    spec.primitive_rings,
                    spec.profile_radius_quantile,
                )
                shrinkwrap_mesh_to_mesh(mesh, target_mesh)
            elif spec.primitive == "shrinkwrap_profiled_tube":
                profile_axis = bone_axis(source_armature, spec.bone) if spec.align_primitive_to_bone else Vector((0.0, 0.0, 1.0))
                profile_v_offset = spec.profile_v_offset
                if spec.name == "Neck01-convcol" and "breast_l" not in group_indices and "breast_r" not in group_indices:
                    profile_v_offset = (profile_v_offset[0] + 0.018, profile_v_offset[1] + 0.018)
                mesh = target_profiled_tube_mesh(
                    spec.name,
                    target_mesh,
                    profile_axis,
                    spec.primitive_segments,
                    spec.primitive_rings,
                    spec.profile_radius_quantile,
                    spec.profile_axis_trim,
                    spec.profile_v_padding,
                    profile_v_offset,
                )
            elif spec.align_primitive_to_bone:
                mesh = target_aligned_ellipsoid_mesh(
                    spec.name,
                    target_mesh,
                    bone_axis(source_armature, spec.bone),
                    spec.primitive_segments,
                    spec.primitive_rings,
                )
                shrinkwrap_mesh_to_mesh(mesh, target_mesh)
            else:
                mesh = target_ellipsoid_mesh(spec.name, target_mesh, spec.primitive_segments, spec.primitive_rings)
                shrinkwrap_mesh_to_mesh(mesh, target_mesh)
            bpy.data.meshes.remove(target_mesh)
            pad_mesh_axes(mesh, spec.mesh_padding)
            if spec.symmetric_x and spec.primitive == "shrinkwrap_profiled_tube":
                symmetrise_profiled_tube_mesh_x(mesh, spec.primitive_segments, spec.primitive_rings)
            elif spec.symmetric_x:
                symmetrise_ellipsoid_mesh_x(mesh, spec.primitive_segments, spec.primitive_rings)
        if spec.primitive == "convex_hull":
            source_mesh = mesh
            mesh = convex_hull_mesh(source_mesh, spec.name)
            bpy.data.meshes.remove(source_mesh)
        if spec.symmetric_x and spec.primitive not in {"shrinkwrap_ellipsoid", "shrinkwrap_profiled_ellipsoid", "shrinkwrap_profiled_tube"}:
            symmetrise_mesh_x(mesh)
        generated_meshes.append((spec, mesh))

    mirror_left_limbs_from_right(generated_meshes)

    clear_scene()
    bpy.context.collection.objects.link(armature)

    generated: list[str] = []
    for spec, mesh in generated_meshes:
        smooth_faces(mesh)
        obj = bpy.data.objects.new(spec.name, mesh)
        bpy.context.collection.objects.link(obj)
        make_mesh_normals_consistent(obj)
        parent_to_bone(obj, armature, spec.bone)
        add_decimate(obj, spec.decimate_ratio, args.apply_decimate)
        generated.append(f"{spec.name}: {len(mesh.vertices)} verts, {len(mesh.polygons)} polys, decimate {spec.decimate_ratio}")

    strip_actions_and_linked_libraries()

    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=args.output)
    print("Generated collider rig:")
    for line in generated:
        print(f"  {line}")


def main() -> None:
    generate(parse_args())


if __name__ == "__main__":
    main()
