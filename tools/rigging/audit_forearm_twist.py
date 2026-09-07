#!/usr/bin/env python3
"""Read-only Blender audit for the generated RIG-002 reference rigs.

Run through ``tools/rigging/run_forearm_twist_audit.py``.  The audit deliberately
uses source mesh data and explicit linear-blend skinning rather than Blender's
evaluated output.  This preserves source vertex identity and makes every
neutral-relative measurement reproducible in Godot.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from pathlib import Path
from typing import Iterable, Mapping

import bpy
from mathutils import Matrix, Vector

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from forearm_twist_bridge import (
    BEND_EVIDENCE_ASSERTIONS,
    BEND_EXTENSION_DEGREES,
    BEND_FLEXION_DEGREES,
    BEND_THRESHOLD_CONFIG,
    MINIMUM_TRIANGLE_AREA_METRES_SQUARED,
    ProvenanceError,
    RUNTIME_TWIST_WEIGHT,
    SCHEMA_VERSION as BRIDGE_SCHEMA_VERSION,
    absolute_pose_metrics,
    anatomical_hand_pose,
    anatomical_wrist_frame,
    assert_bend_sign_convention,
    blended_direction_transport,
    canonical_triangle_id,
    continuity_metrics,
    evaluate_bend_gates,
    helper_twist_pose,
    incremental_bend_metrics,
    m_inverse,
    m_mul,
    m_origin,
    m_transform_direction,
    m_transform_point,
    palm_forward_alignment,
    propagate_poses,
    q_from_matrix,
    retention,
    signed_foldover,
    signed_principal_twist,
    skin_point,
    subject_forward_frame,
    triangle_normal,
    v_add,
    v_dot,
    v_length,
    v_normalised,
    v_sub,
    validate_candidate_weight_evidence,
    validate_oriented_triangle_mapping,
)


SCHEMA_VERSION = BRIDGE_SCHEMA_VERSION
EPSILON = 1.0e-8
WEIGHT_EPSILON = 1.0e-6
RING_CENTRES = [round(-0.05 + index * 0.025, 6) for index in range(49)]
RING_HALF_WIDTH = 0.018
SCENARIOS = (
    ("neutral", 0.0, 0.0),
    ("hand_90_helper_45", 90.0, 45.0),
    ("hand_180_helper_45", 180.0, 45.0),
    ("hand_180_helper_90", 180.0, 90.0),
    ("hand_180_helper_180", 180.0, 180.0),
)
# Anatomical bend scenarios (RIG-002 TR8): pronation onto subject forward, then
# flexion/extension about longitudinal x palm-forward.  The palm-forward state
# is evaluated on its own as the incremental-family denominator.
ANATOMICAL_BEND_SCENARIOS = (
    ("palm_forward", 0.0),
    ("flexion_60", BEND_FLEXION_DEGREES),
    ("extension_60", BEND_EXTENSION_DEGREES),
)
# Distinct runtime TwistWeight executions; each is judged against its own
# independently derived expectation, never against the other execution.
ANATOMICAL_BEND_WEIGHTS = (0.0, RUNTIME_TWIST_WEIGHT)
SIDES = {
    "left": {
        "lower": "lowerarm_l",
        "helper": "LeftForearmTwist",
        "hand": "hand_l",
        "upper": "upperarm_l",
        "opposite_suffix": "_r",
        "side_sign": 1.0,
        "middle_distal": "middle_03_l",
        "thumb_proximal": "thumb_01_l",
        "little_distal": "pinky_03_l",
        "canonical": {
            "lowerarm_l": "LeftLowerArm",
            "LeftForearmTwist": "LeftForearmTwist",
            "hand_l": "LeftHand",
        },
    },
    "right": {
        "lower": "lowerarm_r",
        "helper": "RightForearmTwist",
        "hand": "hand_r",
        "upper": "upperarm_r",
        "opposite_suffix": "_l",
        "side_sign": -1.0,
        "middle_distal": "middle_03_r",
        "thumb_proximal": "thumb_01_r",
        "little_distal": "pinky_03_r",
        "canonical": {
            "lowerarm_r": "RightLowerArm",
            "RightForearmTwist": "RightForearmTwist",
            "hand_r": "RightHand",
        },
    },
}
# MPFB export convention: the derived subject forward must agree with the
# armature-space -Y axis, mirroring the runner's world -Z anchor.
SUBJECT_FORWARD_ARMATURE_ANCHOR = (0.0, -1.0, 0.0)


def _arguments() -> argparse.Namespace:
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--female", required=True, type=Path)
    parser.add_argument("--male", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    return parser.parse_args(arguments)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _vector(value: Vector) -> list[float]:
    return [float(component) for component in value]


def _matrix(value: Matrix) -> list[list[float]]:
    return [[float(component) for component in row] for row in value]


def _angular_difference_degrees(first: Matrix, second: Matrix) -> float:
    return math.degrees(first.to_quaternion().rotation_difference(second.to_quaternion()).angle)


def _require_single(objects: Iterable[bpy.types.Object], label: str) -> bpy.types.Object:
    values = list(objects)
    if len(values) != 1:
        raise AssertionError(f"Expected exactly one {label}; found {len(values)}")
    return values[0]


def _weights(mesh: bpy.types.Object, vertex_index: int) -> dict[str, float]:
    names = {group.index: group.name for group in mesh.vertex_groups}
    return {
        names[item.group]: float(item.weight)
        for item in mesh.data.vertices[vertex_index].groups
        if item.group in names and item.weight > WEIGHT_EPSILON
    }


def _is_descendant(armature: bpy.types.Object, candidate: str, ancestor: str) -> bool:
    bone = armature.data.bones.get(candidate)
    while bone is not None:
        if bone.name == ancestor:
            return True
        bone = bone.parent
    return False


def _family_weights(
    armature: bpy.types.Object, weights: dict[str, float], side: dict[str, object]
) -> dict[str, float]:
    hand = str(side["hand"])
    return {
        "lower_arm": weights.get(str(side["lower"]), 0.0),
        "helper": weights.get(str(side["helper"]), 0.0),
        "hand": weights.get(hand, 0.0),
        "fingers": sum(
            weight
            for name, weight in weights.items()
            if name != hand and _is_descendant(armature, name, hand)
        ),
    }


def _side_frame(armature: bpy.types.Object, side: dict[str, object]) -> tuple[Vector, Vector, Vector, Vector, float]:
    lower = armature.data.bones[str(side["lower"])]
    elbow = armature.matrix_world @ lower.head_local
    wrist = armature.matrix_world @ lower.tail_local
    axis_vector = wrist - elbow
    length = axis_vector.length
    if length <= EPSILON:
        raise AssertionError(f"Degenerate lower-arm axis for {lower.name}")
    axis = axis_vector / length
    reference = Vector((0.0, 0.0, 1.0))
    if abs(axis.dot(reference)) > 0.9:
        reference = Vector((0.0, 1.0, 0.0))
    first = axis.cross(reference).normalized()
    second = axis.cross(first).normalized()
    return elbow, axis, first, second, length


def _coordinates(point: Vector, frame: tuple[Vector, Vector, Vector, Vector, float]) -> tuple[float, float, float]:
    elbow, axis, first, second, length = frame
    relative = point - elbow
    return relative.dot(axis) / length, relative.dot(first), relative.dot(second)


def _convex_hull_area(points: list[tuple[float, float]]) -> float:
    unique = sorted(set(points))
    if len(unique) < 3:
        return 0.0

    def cross(origin: tuple[float, float], first: tuple[float, float], second: tuple[float, float]) -> float:
        return (first[0] - origin[0]) * (second[1] - origin[1]) - (first[1] - origin[1]) * (second[0] - origin[0])

    lower: list[tuple[float, float]] = []
    for point in unique:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], point) <= 0.0:
            lower.pop()
        lower.append(point)
    upper: list[tuple[float, float]] = []
    for point in reversed(unique):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], point) <= 0.0:
            upper.pop()
        upper.append(point)
    hull = lower[:-1] + upper[:-1]
    return abs(sum(
        hull[index][0] * hull[(index + 1) % len(hull)][1]
        - hull[(index + 1) % len(hull)][0] * hull[index][1]
        for index in range(len(hull))
    )) * 0.5


def _minimum_width(points: list[tuple[float, float]]) -> float:
    if len(points) < 4:
        return 0.0
    widths = []
    for index in range(72):
        angle = math.pi * index / 72.0
        direction = (math.cos(angle), math.sin(angle))
        projections = [point[0] * direction[0] + point[1] * direction[1] for point in points]
        widths.append(max(projections) - min(projections))
    return min(widths)


def _posed_bone_matrices(
    armature: bpy.types.Object, side: dict[str, object], hand_degrees: float, helper_degrees: float
) -> dict[str, Matrix]:
    explicit = {
        str(side["hand"]): math.radians(hand_degrees),
        str(side["helper"]): math.radians(helper_degrees),
    }
    result: dict[str, Matrix] = {}

    def resolve(bone: bpy.types.Bone) -> Matrix:
        if bone.name in result:
            return result[bone.name]
        rest = bone.matrix_local.copy()
        if bone.name in explicit:
            posed = rest @ Matrix.Rotation(explicit[bone.name], 4, "Y")
        elif bone.parent is not None:
            posed = resolve(bone.parent) @ bone.parent.matrix_local.inverted() @ rest
        else:
            posed = rest
        result[bone.name] = posed
        return posed

    for bone in armature.data.bones:
        resolve(bone)
    return result


def _tuple_matrix(value: Matrix) -> tuple[tuple[float, float, float, float], ...]:
    """Convert a mathutils matrix to the bridge's float64 row-major tuples."""

    return tuple(tuple(float(component) for component in row) for row in value)


def _mathutils_matrix(value: tuple[tuple[float, float, float, float], ...]) -> Matrix:
    return Matrix(value)


def _required_bone(armature: bpy.types.Object, name: str) -> bpy.types.Bone:
    bone = armature.data.bones.get(name)
    if bone is None:
        raise AssertionError(f"Armature is missing required bone '{name}'.")
    return bone


def _anatomical_bend_frame(armature: bpy.types.Object, side: dict[str, object]) -> dict[str, object]:
    """Derive the approved anatomical wrist-bend frame in armature space.

    All constructions mirror the approved Godot photobooth runner: palm normal
    from hand-rest local +Z cross-checked against finger geometry, subject
    forward derived from Hips->Head and the shoulder line, pronation derived as
    the signed nearest rotation onto subject forward — never a hard-coded angle.
    """

    lower = _required_bone(armature, str(side["lower"]))
    hand = _required_bone(armature, str(side["hand"]))
    frame = subject_forward_frame(
        _required_bone(armature, "pelvis").head_local[:],
        _required_bone(armature, "head").head_local[:],
        _required_bone(armature, "upperarm_l").head_local[:],
        _required_bone(armature, "upperarm_r").head_local[:],
    )
    forward_anchor_dot = v_dot(frame["forward"], SUBJECT_FORWARD_ARMATURE_ANCHOR)  # type: ignore[arg-type]
    minimum_dot = float(BEND_EVIDENCE_ASSERTIONS["subject_forward_anchor_minimum_dot"]["value"])
    if forward_anchor_dot < minimum_dot:
        raise AssertionError(
            f"Derived subject forward {frame['forward']} disagrees with the armature export "
            f"convention {SUBJECT_FORWARD_ARMATURE_ANCHOR} (dot={forward_anchor_dot:.9f})."
        )
    wrist = anatomical_wrist_frame(
        _tuple_matrix(lower.matrix_local),
        _tuple_matrix(hand.matrix_local),
        frame["forward"],  # type: ignore[arg-type]
        _required_bone(armature, str(side["middle_distal"])).head_local[:],
        _required_bone(armature, str(side["thumb_proximal"])).head_local[:],
        _required_bone(armature, str(side["little_distal"])).head_local[:],
        float(side["side_sign"]),
    )
    wrist["forward_anchor_dot"] = forward_anchor_dot
    return wrist


def _anatomical_bend_poses(
    armature: bpy.types.Object,
    side: dict[str, object],
    frame: dict[str, object],
    bend_degrees: float,
    helper_weight: float,
) -> tuple[dict[str, tuple[tuple[float, float, float, float], ...]], dict[str, object]]:
    """Build the anatomical bend pose with real-hierarchy finger propagation.

    The hand receives ``Swing(bend) x Twist(pronation)`` with an invariant wrist
    origin; the single twist helper receives only the weighted axial twist; the
    swing component of the hand delta is never applied anywhere; every
    descendant recomposes its unchanged local rest matrix under its posed
    parent.  The returned armature-space matrices are the bridge's float64
    tuples.
    """

    lower = _required_bone(armature, str(side["lower"]))
    hand = _required_bone(armature, str(side["hand"]))
    helper = _required_bone(armature, str(side["helper"]))
    lower_rest = _tuple_matrix(lower.matrix_local)
    hand_rest = _tuple_matrix(hand.matrix_local)
    helper_rest = _tuple_matrix(helper.matrix_local)

    bend_radians = math.radians(bend_degrees)
    if abs(bend_radians) > 0.0:
        assert_bend_sign_convention(frame, bend_radians)
    posed_hand = anatomical_hand_pose(lower_rest, hand_rest, frame, bend_radians)

    # Recover the commanded axial component from the composed pose itself.
    hand_delta = m_mul(posed_hand, m_inverse(hand_rest))
    hand_delta_quaternion = q_from_matrix(hand_delta)
    longitudinal = frame["longitudinal"]
    twist = signed_principal_twist(hand_delta_quaternion, longitudinal)  # type: ignore[arg-type]
    pronation = float(frame["pronation_radians"])  # type: ignore[arg-type]
    posed_helper = helper_twist_pose(helper_rest, longitudinal, twist, helper_weight)  # type: ignore[arg-type]

    posed_palm = v_normalised(m_transform_direction(posed_hand, (0.0, 0.0, 1.0)))
    palm_target = frame["palm_target"]
    # The palm normal may tilt along the forearm axis; the forward-facing
    # contract applies to its perpendicular component, exactly as the
    # pronation derivation does.
    palm_forward_dot = palm_forward_alignment(posed_palm, palm_target, longitudinal)  # type: ignore[arg-type]

    tolerance_radians = float(BEND_EVIDENCE_ASSERTIONS["twist_recovery_tolerance_radians"]["value"])
    palm_minimum = float(BEND_EVIDENCE_ASSERTIONS["palm_forward_minimum_dot"]["value"])
    origin_tolerance = float(BEND_EVIDENCE_ASSERTIONS["wrist_origin_invariance_metres"]["value"])
    helper_tolerance = float(BEND_EVIDENCE_ASSERTIONS["helper_response_tolerance_radians"]["value"])

    posed_origin = m_transform_point(posed_hand, (0.0, 0.0, 0.0))
    origin_error = v_length(v_sub(posed_origin, m_origin(hand_rest)))
    helper_delta = m_mul(posed_helper, m_inverse(helper_rest))
    helper_delta_angle = 2.0 * math.atan2(
        v_length(q_from_matrix(helper_delta)[:3]), q_from_matrix(helper_delta)[3]
    )
    expected_helper_angle = abs(helper_weight * twist)

    invariants = {
        "bend_degrees": bend_degrees,
        "helper_weight": helper_weight,
        "pronation_degrees": math.degrees(pronation),
        "extracted_twist_degrees": math.degrees(twist),
        "palm_forward_dot": palm_forward_dot,
        "wrist_origin_error_metres": origin_error,
        "helper_delta_degrees": math.degrees(helper_delta_angle),
        "expected_helper_delta_degrees": math.degrees(expected_helper_angle),
        "twist_recovery_error_radians": abs(twist - pronation),
        "helper_recovery_error_radians": abs(helper_delta_angle - expected_helper_angle),
    }
    if abs(twist - pronation) > tolerance_radians:
        raise AssertionError(f"Anatomical pose twist did not recover the pronation: {invariants}")
    if palm_forward_dot < palm_minimum:
        raise AssertionError(f"Anatomical pronation did not face the palm forward: {invariants}")
    if origin_error > origin_tolerance:
        raise AssertionError(f"Anatomical pose moved the wrist origin: {invariants}")
    if abs(helper_delta_angle - expected_helper_angle) > helper_tolerance:
        raise AssertionError(f"Helper rotation did not track weight x twist: {invariants}")

    bones = [
        (bone.name, bone.parent.name if bone.parent else None, _tuple_matrix(bone.matrix_local))
        for bone in armature.data.bones
    ]
    posed = propagate_poses(
        bones,
        {
            str(side["hand"]): posed_hand,
            str(side["helper"]): posed_helper,
        },
    )
    return posed, invariants


def _declared_frame_matrix(armature: bpy.types.Object, side: dict[str, object]) -> Matrix:
    """The declared measurement frame: the lower-arm rest frame in armature space."""

    lower = _required_bone(armature, str(side["lower"]))
    return lower.matrix_local.copy()


def _armature_positions(
    mesh: bpy.types.Object, armature: bpy.types.Object, indices: list[int]
) -> dict[int, tuple[float, float, float]]:
    """Source vertex positions in armature space for the given indices."""

    world_to_armature = armature.matrix_world.inverted()
    return {
        index: tuple(world_to_armature @ (mesh.matrix_world @ mesh.data.vertices[index].co))
        for index in indices
    }


def _declared_positions(
    mesh: bpy.types.Object, armature: bpy.types.Object, frame: Matrix, indices: list[int]
) -> dict[int, tuple[float, float, float]]:
    world_to_declared = (armature.matrix_world @ frame).inverted()
    return {
        index: tuple(world_to_declared @ (mesh.matrix_world @ mesh.data.vertices[index].co))
        for index in indices
    }


def _skin_vertices(
    mesh: bpy.types.Object,
    armature: bpy.types.Object,
    source_weights: list[dict[str, float]],
    poses: dict[str, Matrix],
) -> list[Vector]:
    armature_world = armature.matrix_world
    world_to_armature = armature_world.inverted()
    output: list[Vector] = []
    for vertex, weights in zip(mesh.data.vertices, source_weights, strict=True):
        source_world = mesh.matrix_world @ vertex.co
        source_armature = world_to_armature @ source_world
        deformed = Vector((0.0, 0.0, 0.0))
        influences = [
            (name, weight)
            for name, weight in weights.items()
            if (bone := armature.data.bones.get(name)) is not None and bone.use_deform
        ]
        total = sum(weight for _name, weight in influences)
        for name, weight in influences:
            bone = armature.data.bones[name]
            weight /= total
            deformed += (armature_world @ poses[name] @ bone.matrix_local.inverted() @ source_armature) * weight
        if total <= WEIGHT_EPSILON:
            deformed = source_world
        output.append(deformed)
    return output


def _ring_measurements(
    neutral: list[Vector], deformed: list[Vector], selected: list[int], frame: tuple[Vector, Vector, Vector, Vector, float]
) -> list[dict[str, float | int]]:
    neutral_coordinates = {index: _coordinates(neutral[index], frame) for index in selected}
    deformed_coordinates = {index: _coordinates(deformed[index], frame) for index in selected}
    rings = []
    for centre in RING_CENTRES:
        indices = [index for index in selected if abs(neutral_coordinates[index][0] - centre) <= RING_HALF_WIDTH]
        neutral_points = [(neutral_coordinates[index][1], neutral_coordinates[index][2]) for index in indices]
        deformed_points = [(deformed_coordinates[index][1], deformed_coordinates[index][2]) for index in indices]
        if len(indices) < 8:
            continue
        # Remove each ring's best-fit axial phase before comparing its centre. Width and area are rotation
        # invariant, while this co-moving alignment prevents a harmless rigid roll being reported as drift.
        phase = math.atan2(
            sum(point[0] * reference[1] - point[1] * reference[0] for point, reference in zip(deformed_points, neutral_points, strict=True)),
            sum(point[0] * reference[0] + point[1] * reference[1] for point, reference in zip(deformed_points, neutral_points, strict=True)),
        )
        cosine = math.cos(phase)
        sine = math.sin(phase)
        co_moving_points = [
            (point[0] * cosine - point[1] * sine, point[0] * sine + point[1] * cosine)
            for point in deformed_points
        ]
        neutral_width = _minimum_width(neutral_points)
        neutral_area = _convex_hull_area(neutral_points)
        width = _minimum_width(co_moving_points)
        area = _convex_hull_area(co_moving_points)
        neutral_centre = Vector((
            sum(point[0] for point in neutral_points) / len(neutral_points),
            sum(point[1] for point in neutral_points) / len(neutral_points),
        ))
        centre_point = Vector((
            sum(point[0] for point in co_moving_points) / len(co_moving_points),
            sum(point[1] for point in co_moving_points) / len(co_moving_points),
        ))
        rings.append({
            "t": centre,
            "sample_count": len(indices),
            "neutral_width": neutral_width,
            "width": width,
            "width_ratio": width / neutral_width if neutral_width > EPSILON else 0.0,
            "neutral_area": neutral_area,
            "area": area,
            "area_ratio": area / neutral_area if neutral_area > EPSILON else 0.0,
            "centreline_drift": (centre_point - neutral_centre).length,
            "co_moving_rotation_degrees": math.degrees(phase),
        })
    return rings


def _triangle_indicators(
    mesh: bpy.types.Object,
    neutral: list[Vector],
    deformed: list[Vector],
    selected: set[int],
) -> dict[str, float | int]:
    mesh.data.calc_loop_triangles()
    minimum_ratio = float("inf")
    degenerate = 0
    foldover = 0
    count = 0
    for triangle in mesh.data.loop_triangles:
        indices = tuple(triangle.vertices)
        if not all(index in selected for index in indices):
            continue
        neutral_cross = (neutral[indices[1]] - neutral[indices[0]]).cross(neutral[indices[2]] - neutral[indices[0]])
        deformed_cross = (deformed[indices[1]] - deformed[indices[0]]).cross(deformed[indices[2]] - deformed[indices[0]])
        neutral_area = neutral_cross.length * 0.5
        deformed_area = deformed_cross.length * 0.5
        if neutral_area <= EPSILON:
            continue
        count += 1
        ratio = deformed_area / neutral_area
        minimum_ratio = min(minimum_ratio, ratio)
        if ratio < 1.0e-3:
            degenerate += 1
        # With an articulated axial roll there is no single world-space normal against which a sign is meaningful.
        # A near-zero local area is therefore the stable foldover candidate indicator; rigidly rotated triangles do
        # not produce false positives.
        if ratio < 0.05:
            foldover += 1
    return {
        "triangle_count": count,
        "minimum_area_ratio": minimum_ratio if count else 0.0,
        "degenerate_count": degenerate,
        "foldover_candidate_count": foldover,
    }


def _fixed_bridge_metrics(
    mesh: bpy.types.Object,
    armature: bpy.types.Object,
    source_weights: list[dict[str, float]],
    neutral: list[Vector],
    deformed: list[Vector],
    poses: dict[str, Matrix],
    manifest_side: dict[str, object],
    pivot: Vector,
    hinge_axis: Vector,
) -> dict[str, object]:
    """Measure the manifest-pinned bridge, never a candidate-selected subset."""

    id_to_index: dict[str, int] = {}
    source_to_id: dict[int, str] = {}
    mixed_ids: set[str] = set()
    radii: dict[str, float] = {}
    for row in manifest_side["vertices"]:
        identifier = str(row["id"])
        members = [int(index) for index in row["members"]]
        if not members:
            raise ProvenanceError(f"Manifest vertex {identifier} has no source membership.")
        id_to_index[identifier] = members[0]
        radii[identifier] = float(row["radius_metres"])
        if row["classification"] == "mixed":
            mixed_ids.add(identifier)
        for index in members:
            if index in source_to_id:
                raise ProvenanceError(f"Manifest source vertex {index} belongs to multiple canonical IDs.")
            source_to_id[index] = identifier
    expected_bins = {int(bin_index): int(count) for bin_index, count in manifest_side["mixed_bin_counts"].items()}
    actual_bins: dict[int, int] = {}
    for identifier in mixed_ids:
        # The manifest keeps baseline membership immutable; source candidates do
        # not get to erase an expected bin by changing helper distribution.
        bin_index = int(next(row["bin"] for row in manifest_side["vertices"] if str(row["id"]) == identifier))
        actual_bins[bin_index] = actual_bins.get(bin_index, 0) + 1
    if actual_bins != expected_bins or any(count < 10 for count in expected_bins.values()):
        raise ProvenanceError(
            f"Fixed mixed bridge bin coverage mismatch: expected {expected_bins}, actual {actual_bins}."
        )
    expected_triangle_rows = [[str(identifier) for identifier in row["vertices"]] for row in manifest_side["triangles"]]
    current_triangle_rows = []
    mesh.data.calc_loop_triangles()
    for triangle in mesh.data.loop_triangles:
        indices = list(triangle.vertices)
        if not all(index in source_to_id for index in indices):
            continue
        ids = [source_to_id[index] for index in indices]
        if not any(identifier in mixed_ids for identifier in ids):
            continue
        neutral_area = ((neutral[indices[1]] - neutral[indices[0]]).cross(neutral[indices[2]] - neutral[indices[0]])).length * 0.5
        if neutral_area >= MINIMUM_TRIANGLE_AREA_METRES_SQUARED:
            current_triangle_rows.append(ids)
    validate_oriented_triangle_mapping(expected_triangle_rows, current_triangle_rows)
    retentions = [retention(deformed[index], pivot, hinge_axis, radii[identifier]) for identifier, index in id_to_index.items()]
    area_ratios: list[float] = []
    foldovers = 0
    for row in manifest_side["triangles"]:
        identifiers = [str(identifier) for identifier in row["vertices"]]
        indices = [id_to_index[identifier] for identifier in identifiers]
        neutral_cross = (neutral[indices[1]] - neutral[indices[0]]).cross(neutral[indices[2]] - neutral[indices[0]])
        deformed_cross = (deformed[indices[1]] - deformed[indices[0]]).cross(deformed[indices[2]] - deformed[indices[0]])
        neutral_area = neutral_cross.length * 0.5
        area_ratios.append((deformed_cross.length * 0.5) / neutral_area)
        transported = Vector((0.0, 0.0, 0.0))
        for index in indices:
            weighted = Matrix(((0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 0.0)))
            total = 0.0
            for name, weight in source_weights[index].items():
                bone = armature.data.bones.get(name)
                if bone is not None and bone.use_deform:
                    weighted += (poses[name] @ bone.matrix_local.inverted()).to_3x3() * weight
                    total += weight
            if total > WEIGHT_EPSILON:
                transported += armature.matrix_world.to_3x3() @ (weighted @ neutral_cross.normalized())
        if signed_foldover(tuple(deformed_cross), tuple(transported)):
            foldovers += 1
    return {
        "fixed_vertex_count": len(id_to_index),
        "fixed_triangle_count": len(expected_triangle_rows),
        "minimum_hinge_retention": min(retentions),
        "minimum_area_ratio": min(area_ratios),
        "area_ratio_below_0_05_count": sum(ratio < 0.05 for ratio in area_ratios),
        "signed_foldover_count": foldovers,
    }


def _validate_candidate_weights(
    source_weights: list[dict[str, float]], manifest_side: dict[str, object], candidate_side: dict[str, object]
) -> None:
    expected = {str(row["id"]): dict(row["weights"]) for row in candidate_side["vertices"]}
    validate_candidate_weight_evidence(
        (str(row["id"]) for row in manifest_side["vertices"]), expected
    )
    for row in manifest_side["vertices"]:
        identifier = str(row["id"])
        if identifier not in expected:
            raise ProvenanceError(f"Missing candidate-weight evidence for canonical vertex {identifier}.")
        for index in row["members"]:
            actual = source_weights[int(index)]
            names = set(actual) | set(expected[identifier])
            if any(abs(actual.get(name, 0.0) - expected[identifier].get(name, 0.0)) > WEIGHT_EPSILON for name in names):
                raise ProvenanceError(f"Candidate weights differ for canonical vertex {identifier}.")


def _population_skin_influences(
    armature: bpy.types.Object, weights: Mapping[str, float]
) -> list[tuple[str, float]]:
    influences = [
        (name, weight)
        for name, weight in weights.items()
        if (bone := armature.data.bones.get(name)) is not None and bone.use_deform
    ]
    return influences


def _declared_pose_transports(
    armature: bpy.types.Object,
    frame_inverse: tuple[tuple[float, float, float, float], ...],
    frame: tuple[tuple[float, float, float, float], ...],
    posed: Mapping[str, tuple[tuple[float, float, float, float], ...]],
) -> dict[str, tuple[tuple[float, float, float, float], ...]]:
    """Conjugate each bone's rest-relative pose into the declared frame.

    A point or direction measured in the declared lower-arm rest frame is
    transformed by ``F^-1 @ (pose @ rest^-1) @ F`` with ``F`` the armature-space
    lower-arm rest matrix; this documented conversion is the only place world
    and armature quantities meet.
    """

    transports = {}
    for name, pose in posed.items():
        bone = armature.data.bones.get(name)
        if bone is None:
            continue
        rest_relative = m_mul(pose, m_inverse(_tuple_matrix(bone.matrix_local)))
        transports[name] = m_mul(frame_inverse, m_mul(rest_relative, frame))
    return transports


def _evaluate_anatomical_bend(
    mesh: bpy.types.Object,
    armature: bpy.types.Object,
    side: dict[str, object],
    source_weights: list[dict[str, float]],
    manifest_side: dict[str, object],
) -> dict[str, object]:
    """Evaluate both bend metric families over the frozen population (RIG-002 TR9-10).

    Family A measures per-pose absolute shape quality; family B measures bend
    damage relative to the palm-forward state at the same weight configuration.
    Every execution is an independent pose construction; gates are decided at
    the shipped runtime twist weight.
    """

    bend_manifest = manifest_side.get("anatomical_bend")
    if not isinstance(bend_manifest, dict):
        raise ProvenanceError(
            "Manifest side is missing the schema-3 anatomical bend population; "
            "regenerate the pre-helper manifest from MPFB."
        )

    frame = _anatomical_bend_frame(armature, side)
    lower_rest = _tuple_matrix(_required_bone(armature, str(side["lower"])).matrix_local)
    lower_rest_inverse = m_inverse(lower_rest)
    wrist = frame["wrist"]
    longitudinal = frame["longitudinal"]
    flex_axis = frame["flex_axis"]

    # Axes and pivot expressed in the declared lower-arm rest frame.
    longitudinal_declared = v_normalised(m_transform_direction(lower_rest_inverse, longitudinal))  # type: ignore[arg-type]
    flex_axis_declared = v_normalised(m_transform_direction(lower_rest_inverse, flex_axis))  # type: ignore[arg-type]
    wrist_declared = m_transform_point(lower_rest_inverse, wrist)  # type: ignore[arg-type]

    # Frame parity against the generator-recorded construction.
    parity_radians = float(BEND_EVIDENCE_ASSERTIONS["manifest_parity_radians"]["value"])
    parity_axis = float(BEND_EVIDENCE_ASSERTIONS["manifest_parity_axis_maximum_component_delta"]["value"])
    derived_pronation_degrees = math.degrees(float(frame["pronation_radians"]))  # type: ignore[arg-type]
    if abs(derived_pronation_degrees - float(bend_manifest["pronation_degrees"])) > math.degrees(parity_radians):
        raise ProvenanceError(
            f"Derived pronation {derived_pronation_degrees:.6f} degrees differs from the "
            f"manifest-recorded {bend_manifest['pronation_degrees']} degrees."
        )
    for name, derived in (
        ("longitudinal_axis_declared", longitudinal_declared),
        ("flex_axis_declared", flex_axis_declared),
    ):
        recorded = bend_manifest[name]
        if max(abs(derived[index] - float(recorded[index])) for index in range(3)) > parity_axis:
            raise ProvenanceError(f"Derived {name} {derived} differs from the manifest-recorded {recorded}.")

    # Frozen population from the manifest.
    rows = bend_manifest["vertices"]
    id_to_members = {str(row["id"]): [int(index) for index in row["members"]] for row in rows}
    member_to_id = {index: identifier for identifier, members in id_to_members.items() for index in members}
    identifiers = list(id_to_members)
    neutral_declared = {str(row["id"]): tuple(float(value) for value in row["position_declared"]) for row in rows}
    radius_longitudinal = {str(row["id"]): float(row["radius_longitudinal_metres"]) for row in rows}
    radius_flex = {str(row["id"]): float(row["radius_flex_metres"]) for row in rows}
    local_scales = {str(row["id"]): float(row["local_edge_metres"]) for row in rows}
    source_ownership = {str(row["id"]): {str(name): float(weight) for name, weight in row["source_ownership"].items()} for row in rows}
    triangle_rows = [[str(identifier) for identifier in row["vertices"]] for row in bend_manifest["triangles"]]
    neutral_areas = {str(row["id"]): float(row["neutral_area_metres_squared"]) for row in bend_manifest["triangles"]}
    bin_counts = {int(index): int(count) for index, count in bend_manifest["population_bin_counts"].items()}
    if sum(bin_counts.values()) != len(identifiers):
        raise ProvenanceError("Bend population bin counts do not cover every population vertex.")
    actual_bins: dict[int, int] = {}
    for row in rows:
        actual_bins[int(row["bin"])] = actual_bins.get(int(row["bin"]), 0) + 1
    if actual_bins != bin_counts:
        raise ProvenanceError(f"Bend population bins changed: expected {bin_counts}, actual {actual_bins}.")

    # Current-mesh neutral parity and oriented triangle coverage.
    member_indices = sorted(member_to_id)
    if any(index >= len(mesh.data.vertices) for index in member_indices):
        raise ProvenanceError("Bend population refers to source vertices absent from the current mesh.")
    current_neutral = _declared_positions(mesh, armature, _mathutils_matrix(lower_rest), member_indices)
    parity_metres = float(BEND_EVIDENCE_ASSERTIONS["manifest_parity_position_metres"]["value"])
    for index in member_indices:
        identifier = member_to_id[index]
        if v_length(v_sub(current_neutral[index], neutral_declared[identifier])) > parity_metres:
            raise ProvenanceError(
                f"Current mesh neutral position for {identifier} drifted from the manifest by more than {parity_metres} m."
            )
    mesh.data.calc_loop_triangles()
    current_triangle_rows = []
    for triangle in mesh.data.loop_triangles:
        indices = list(triangle.vertices)
        if not all(index in member_to_id for index in indices):
            continue
        current_triangle_rows.append([member_to_id[index] for index in indices])
    validate_oriented_triangle_mapping(triangle_rows, current_triangle_rows)

    armature_positions = _armature_positions(mesh, armature, member_indices)
    weights_executions: dict[str, object] = {}
    scenario_states_by_weight: dict[str, dict[str, dict[str, tuple[float, float, float]]]] = {}
    gates: dict[str, object] | None = None
    parity: dict[str, object] | None = None
    recorded_scenarios = bend_manifest["scenarios"]

    for helper_weight in ANATOMICAL_BEND_WEIGHTS:
        scenario_states: dict[str, dict[str, tuple[float, float, float]]] = {}
        scenario_record: dict[str, object] = {}
        scenario_poses: dict[str, dict[str, tuple[tuple[float, float, float, float], ...]]] = {}
        for name, bend_degrees in ANATOMICAL_BEND_SCENARIOS:
            posed, invariants = _anatomical_bend_poses(armature, side, frame, bend_degrees, helper_weight)
            scenario_poses[name] = posed
            transports = _declared_pose_transports(armature, lower_rest_inverse, lower_rest, posed)
            deformed_armature = {}
            for index in member_indices:
                influences = _population_skin_influences(armature, source_weights[index])
                deformed_armature[index] = skin_point(
                    armature_positions[index],
                    influences,
                    posed,
                    {bone.name: _tuple_matrix(bone.matrix_local) for bone in armature.data.bones},
                )
            deformed = {
                member_to_id[index]: m_transform_point(lower_rest_inverse, position)
                for index, position in deformed_armature.items()
            }
            scenario_states[name] = deformed

            axis = longitudinal_declared if bend_degrees == 0.0 else flex_axis_declared
            reference_radii = radius_longitudinal if bend_degrees == 0.0 else radius_flex
            transported_normals = {}
            for identifiers_triangle in triangle_rows:
                triangle_id = canonical_triangle_id(list(identifiers_triangle))
                neutral_normal = triangle_normal(
                    *[neutral_declared[identifier] for identifier in identifiers_triangle]
                )
                transported = (0.0, 0.0, 0.0)
                for identifier in identifiers_triangle:
                    index = id_to_members[identifier][0]
                    influences = _population_skin_influences(armature, source_weights[index])
                    total = sum(weight for _name, weight in influences)
                    if total <= WEIGHT_EPSILON:
                        continue
                    transported = v_add(
                        transported,
                        blended_direction_transport(
                            neutral_normal,
                            [
                                (weight / total, transports[name])
                                for name, weight in influences
                            ],
                        ),
                    )
                transported_normals[triangle_id] = transported
            absolute = absolute_pose_metrics(
                deformed,
                triangle_rows,
                neutral_areas,
                reference_radii,
                wrist_declared,
                axis,
                transported_normals,
            )
            continuity = continuity_metrics(neutral_declared, deformed, triangle_rows)
            scenario_record[name] = {
                "invariants": invariants,
                "absolute": absolute,
                "continuity": continuity,
            }

        incremental = {
            bend_name: incremental_bend_metrics(
                scenario_states["palm_forward"],
                scenario_states[bend_name],
                triangle_rows,
                local_scales,
                wrist_declared,
                flex_axis_declared,
            )
            for bend_name in ("flexion_60", "extension_60")
        }

        # Generator parity: the runtime-weight execution re-skinned with the
        # manifest's source ownership must reproduce the recorded deformation.
        weight_key = f"{helper_weight:.3f}"
        weights_executions[weight_key] = {"scenarios": scenario_record, "incremental": incremental}
        scenario_states_by_weight[weight_key] = scenario_states
        if abs(helper_weight - RUNTIME_TWIST_WEIGHT) < 1.0e-9:
            parity_errors = {}
            for name, _bend in ANATOMICAL_BEND_SCENARIOS:
                posed = scenario_poses[name]
                recorded = recorded_scenarios[name]["deformed_declared"]
                worst = 0.0
                for index in member_indices:
                    identifier = member_to_id[index]
                    deformed_armature_position = skin_point(
                        armature_positions[index],
                        _population_skin_influences(armature, source_ownership[identifier]),
                        posed,
                        {bone.name: _tuple_matrix(bone.matrix_local) for bone in armature.data.bones},
                    )
                    deformed_declared_position = m_transform_point(lower_rest_inverse, deformed_armature_position)
                    recorded_position = tuple(float(value) for value in recorded[identifier])
                    worst = max(worst, v_length(v_sub(deformed_declared_position, recorded_position)))
                parity_errors[name] = worst
            if any(error > parity_metres for error in parity_errors.values()):
                raise ProvenanceError(
                    f"Audit source-ownership deformation diverged from the manifest record: {parity_errors}."
                )
            parity = {
                "position_tolerance_metres": parity_metres,
                "worst_position_error_metres": parity_errors,
            }
            gates = evaluate_bend_gates(
                {name: record["absolute"] for name, record in scenario_record.items()},
                incremental,
                {name: record["continuity"] for name, record in scenario_record.items()},
            )

    # Weight-engagement evidence: a non-zero position delta between the two
    # weight executions proves the helper engages; it is never a quality claim.
    engagement = {
        name: max(
            v_length(v_sub(scenario_states_by_weight[f"{RUNTIME_TWIST_WEIGHT:.3f}"][name][identifier], scenario_states_by_weight["0.000"][name][identifier]))
            for identifier in identifiers
        )
        for name, _bend in ANATOMICAL_BEND_SCENARIOS
    }

    return {
        "declared_frame": "lower_arm_rest",
        "frame": {
            "elbow_armature": _vector(Vector(frame["elbow"])),  # type: ignore[arg-type]
            "wrist_armature": _vector(Vector(frame["wrist"])),  # type: ignore[arg-type]
            "longitudinal_axis_declared": list(longitudinal_declared),
            "flex_axis_declared": list(flex_axis_declared),
            "subject_forward_armature": _vector(Vector(frame["forward"])),  # type: ignore[arg-type]
            "forward_anchor_dot": float(frame["forward_anchor_dot"]),  # type: ignore[arg-type]
            "palm_thumb_alignment": float(frame["thumb_alignment"]),  # type: ignore[arg-type]
            "palm_little_alignment": float(frame["little_alignment"]),  # type: ignore[arg-type]
            "pronation_degrees": math.degrees(float(frame["pronation_radians"])),  # type: ignore[arg-type]
            "conversions": {
                "positions": "declared = (armature.matrix_world @ lower_arm.matrix_local)^-1 @ world",
                "axes": "declared = lower_arm.matrix_local.to_3x3().inverted() @ armature-space axis",
                "note": "The declared frame is the lower-arm rest frame; world conversions happen only at skinning input.",
            },
        },
        "population": {
            "vertex_count": len(identifiers),
            "triangle_count": len(triangle_rows),
            "bin_counts": {str(index): count for index, count in sorted(bin_counts.items())},
        },
        "weights": weights_executions,
        "runtime_weight_gates": gates,
        "manifest_parity": parity,
        "runtime_weight_engagement_position_delta_metres": engagement,
        "thresholds": BEND_THRESHOLD_CONFIG,
    }


def _scenario_summary(rings: list[dict[str, float | int]], triangles: dict[str, float | int]) -> dict[str, object]:
    analysis_rings = [ring for ring in rings if 0.25 <= float(ring["t"]) <= 1.05]
    wrist_rings = [ring for ring in rings if 0.80 <= float(ring["t"]) <= 1.05]
    if not analysis_rings or not wrist_rings:
        raise AssertionError("Dense ring sampling did not resolve the forearm and wrist")

    def minimum(key: str, values: list[dict[str, float | int]]) -> dict[str, float]:
        ring = min(values, key=lambda item: float(item[key]))
        return {"value": float(ring[key]), "t": float(ring["t"])}

    discontinuity = {"value": 1.0, "t": 0.0}
    for first, second in zip(analysis_rings, analysis_rings[1:]):
        first_ratio = float(first["width_ratio"])
        second_ratio = float(second["width_ratio"])
        ratio = min(first_ratio, second_ratio) / max(first_ratio, second_ratio) if max(first_ratio, second_ratio) > EPSILON else 0.0
        if ratio < discontinuity["value"]:
            discontinuity = {"value": ratio, "t": float(second["t"])}
    return {
        "measured": {
            "minimum_width_ratio": minimum("width_ratio", analysis_rings),
            "minimum_wrist_width_ratio": minimum("width_ratio", wrist_rings),
            "minimum_cross_section_area_ratio": minimum("area_ratio", analysis_rings),
            "minimum_wrist_area_ratio": minimum("area_ratio", wrist_rings),
            "minimum_adjacent_ring_continuity": discontinuity,
            "maximum_centreline_drift_metres": max(float(ring["centreline_drift"]) for ring in analysis_rings),
            **triangles,
        },
        "rings": rings,
    }


WEIGHT_FAMILIES = ("lower_arm", "helper", "hand", "fingers")


def _influence_audit(
    mesh: bpy.types.Object,
    armature: bpy.types.Object,
    side_name: str,
    side: dict[str, object],
    source_weights: list[dict[str, float]],
    neutral: list[Vector],
    frame: tuple[Vector, Vector, Vector, Vector, float],
) -> dict[str, object]:
    deform_names = {bone.name for bone in armature.data.bones if bone.use_deform}
    unresolved = sorted({name for weights in source_weights for name in weights if name not in deform_names})
    sums = [sum(weight for name, weight in weights.items() if name in deform_names) for weights in source_weights]
    unnormalised = [index for index, total in enumerate(sums) if abs(total - 1.0) > 2.0e-4]
    influence_counts = [sum(1 for name in weights if name in deform_names) for weights in source_weights]
    rows: list[dict[str, object]] = []
    sectors: list[dict[str, object]] = []
    samples: list[tuple[int, float, float, dict[str, float], dict[str, float]]] = []
    wrong_side = []
    nonlocal_helper = []
    opposite_suffix = str(side["opposite_suffix"])
    own_side_suffix = "_l" if side_name == "left" else "_r"
    for index, (weights, point) in enumerate(zip(source_weights, neutral, strict=True)):
        t, first, second = _coordinates(point, frame)
        radius = math.hypot(first, second)
        families = _family_weights(armature, weights, side)
        if radius <= 0.18 and -0.10 <= t <= 1.20:
            angle = math.atan2(second, first)
            samples.append((index, t, angle, weights, families))
        if radius <= 0.25 and -0.10 <= t <= 2.50:
            for name, weight in weights.items():
                if weight <= WEIGHT_EPSILON:
                    continue
                if name.endswith(opposite_suffix) and not name.endswith(own_side_suffix):
                    wrong_side.append((index, name, weight, t))
        helper_weight = weights.get(str(side["helper"]), 0.0)
        if helper_weight > WEIGHT_EPSILON and (t < -0.05 or t > 1.12 or radius > 0.18):
            nonlocal_helper.append((index, helper_weight, t, radius))

    for row_index in range(48):
        start = -0.10 + row_index * (1.30 / 48.0)
        end = start + (1.30 / 48.0)
        row_samples = [sample for sample in samples if start <= sample[1] < end]
        if not row_samples:
            continue
        row = {
            "t": (start + end) * 0.5,
            "count": len(row_samples),
            **{
                family: sum(sample[4][family] for sample in row_samples) / len(row_samples)
                for family in WEIGHT_FAMILIES
            },
        }
        rows.append(row)
        for sector in range(16):
            sector_start = -math.pi + sector * (2.0 * math.pi / 16.0)
            sector_end = sector_start + (2.0 * math.pi / 16.0)
            sector_samples = [sample for sample in row_samples if sector_start <= sample[2] < sector_end]
            if sector_samples:
                sectors.append({
                    "row": len(rows) - 1,
                    "sector": sector,
                    "count": len(sector_samples),
                    **{
                        family: sum(sample[4][family] for sample in sector_samples) / len(sector_samples)
                        for family in WEIGHT_FAMILIES
                    },
                })

    row_transitions = []
    for first, second in zip(rows, rows[1:]):
        for family in WEIGHT_FAMILIES:
            row_transitions.append({
                "family": family,
                "from_t": first["t"],
                "to_t": second["t"],
                "delta_t": float(second["t"]) - float(first["t"]),
                "absolute_delta": abs(float(second[family]) - float(first[family])),
            })
    edges = set()
    for polygon in mesh.data.polygons:
        vertices = list(polygon.vertices)
        for index, first in enumerate(vertices):
            second = vertices[(index + 1) % len(vertices)]
            edges.add(tuple(sorted((first, second))))
    edge_transitions = []
    for first, second in edges:
        first_families = _family_weights(armature, source_weights[first], side)
        second_families = _family_weights(armature, source_weights[second], side)
        for family in WEIGHT_FAMILIES:
            edge_transitions.append({
                "family": family,
                "vertices": [first, second],
                "absolute_delta": abs(first_families[family] - second_families[family]),
            })
    return {
        "deform_weight_sum": {
            "minimum": min(sums),
            "maximum": max(sums),
            "unnormalised_count": len(unnormalised),
            "worst_vertex": max(range(len(sums)), key=lambda index: abs(sums[index] - 1.0)),
            "maximum_influence_count": max(influence_counts),
            "vertices_above_four_influences": sum(count > 4 for count in influence_counts),
            "vertices_above_eight_influences": sum(count > 8 for count in influence_counts),
            "anatomical_minimum": min(
                sum(weight for name, weight in sample[3].items() if name in deform_names)
                for sample in samples
            ),
            "anatomical_maximum": max(
                sum(weight for name, weight in sample[3].items() if name in deform_names)
                for sample in samples
            ),
        },
        "unresolved_groups": unresolved,
        "wrong_side_contamination": {
            "count": len(wrong_side),
            "maximum_weight": max((item[2] for item in wrong_side), default=0.0),
            "examples": [
                {"vertex": item[0], "group": item[1], "weight": item[2], "t": item[3]}
                for item in sorted(wrong_side, key=lambda item: item[2], reverse=True)[:8]
            ],
        },
        "nonlocal_helper_contamination": {
            "count": len(nonlocal_helper),
            "maximum_weight": max((item[1] for item in nonlocal_helper), default=0.0),
            "examples": [
                {"vertex": item[0], "weight": item[1], "t": item[2], "radius": item[3]}
                for item in sorted(nonlocal_helper, key=lambda item: item[1], reverse=True)[:8]
            ],
        },
        "longitudinal_rows": rows,
        "circumferential_sectors": sectors,
        "worst_row_transition": max(row_transitions, key=lambda item: item["absolute_delta"]),
        "worst_edge_transition": max(edge_transitions, key=lambda item: item["absolute_delta"]),
    }


def _bone_audit(armature: bpy.types.Object, side: dict[str, object]) -> dict[str, object]:
    lower = armature.data.bones[str(side["lower"])]
    helper = armature.data.bones[str(side["helper"])]
    hand = armature.data.bones[str(side["hand"])]
    return {
        "names": dict(side["canonical"]),
        "lower_parent": lower.parent.name if lower.parent else None,
        "helper_parent": helper.parent.name if helper.parent else None,
        "hand_parent": hand.parent.name if hand.parent else None,
        "deform": {"lower": lower.use_deform, "helper": helper.use_deform, "hand": hand.use_deform},
        "lower": {"head": _vector(lower.head_local), "tail": _vector(lower.tail_local), "matrix": _matrix(lower.matrix_local)},
        "helper": {"head": _vector(helper.head_local), "tail": _vector(helper.tail_local), "matrix": _matrix(helper.matrix_local)},
        "hand": {"head": _vector(hand.head_local), "tail": _vector(hand.tail_local), "matrix": _matrix(hand.matrix_local)},
        "alignment": {
            "head_distance": (helper.head_local - lower.head_local).length,
            "tail_distance": (helper.tail_local - lower.tail_local).length,
            "axis_angle_degrees": math.degrees((helper.tail_local - helper.head_local).angle(lower.tail_local - lower.head_local)),
            "roll_frame_angle_degrees": _angular_difference_degrees(helper.matrix_local, lower.matrix_local),
            "hand_pivot_to_lower_tail_distance": (hand.head_local - lower.tail_local).length,
        },
    }


def _mesh_audit(
    mesh: bpy.types.Object, armature: bpy.types.Object, neutral_manifest: dict[str, object], candidate_evidence: dict[str, object]
) -> dict[str, object]:
    source_weights = [_weights(mesh, vertex.index) for vertex in mesh.data.vertices]
    neutral = [mesh.matrix_world @ vertex.co for vertex in mesh.data.vertices]
    deform_names = {bone.name for bone in armature.data.bones if bone.use_deform}
    topology_payload = json.dumps(
        {
            "vertices": [[round(component, 7) for component in vertex.co] for vertex in mesh.data.vertices],
            "polygons": [list(polygon.vertices) for polygon in mesh.data.polygons],
        },
        separators=(",", ":"),
    ).encode("utf-8")
    result: dict[str, object] = {
        "name": mesh.name,
        "vertex_count": len(mesh.data.vertices),
        "polygon_count": len(mesh.data.polygons),
        "topology_sha256": hashlib.sha256(topology_payload).hexdigest(),
        "object_transform": _matrix(mesh.matrix_world),
        "vertex_group_count": len(mesh.vertex_groups),
        "unresolved_vertex_groups": sorted(
            group.name for group in mesh.vertex_groups if group.name not in deform_names
        ),
        "modifiers": [
            {
                "name": modifier.name,
                "type": modifier.type,
                "object": modifier.object.name if modifier.type == "ARMATURE" and modifier.object else None,
                "use_vertex_groups": modifier.use_vertex_groups if modifier.type == "ARMATURE" else None,
                "use_deform_preserve_volume": modifier.use_deform_preserve_volume if modifier.type == "ARMATURE" else None,
            }
            for modifier in mesh.modifiers
        ],
        "sides": {},
    }
    manifest_mesh = next((item for item in neutral_manifest["meshes"] if item["name"] == mesh.name), None)
    if manifest_mesh is None:
        raise ProvenanceError(f"No pre-helper neutral manifest mesh entry for {mesh.name}.")
    candidate_mesh = next((item for item in candidate_evidence["meshes"] if item["name"] == mesh.name), None)
    if candidate_mesh is None:
        raise ProvenanceError(f"No candidate-weight evidence mesh entry for {mesh.name}.")
    for side_name, side in SIDES.items():
        frame = _side_frame(armature, side)
        selected = [
            index
            for index, point in enumerate(neutral)
            if -0.10 <= _coordinates(point, frame)[0] <= 1.20
            and math.hypot(*_coordinates(point, frame)[1:]) <= 0.18
        ]
        scenarios: dict[str, object] = {}
        for name, hand_degrees, helper_degrees in SCENARIOS:
            poses = _posed_bone_matrices(armature, side, hand_degrees, helper_degrees)
            deformed = _skin_vertices(mesh, armature, source_weights, poses)
            rings = _ring_measurements(neutral, deformed, selected, frame)
            triangles = _triangle_indicators(mesh, neutral, deformed, set(selected))
            scenarios[name] = {
                "hand_degrees": hand_degrees,
                "helper_degrees": helper_degrees,
                **_scenario_summary(rings, triangles),
            }
        manifest_side = manifest_mesh["sides"].get(side_name)
        if manifest_side is None:
            raise ProvenanceError(f"No pre-helper neutral manifest side entry for {mesh.name}/{side_name}.")
        _validate_candidate_weights(source_weights, manifest_side, candidate_mesh["sides"][side_name])
        neutral_reconstruction = _skin_vertices(
            mesh, armature, source_weights, {bone.name: bone.matrix_local for bone in armature.data.bones}
        )
        # TR11 neutral-rest bridge gates: the 0.90 hinge retention, the 0.05
        # area floor, and zero signed foldovers apply to the neutral-rest
        # population only; bend poses use the declared two-family thresholds.
        lower_bone = armature.data.bones[str(side["lower"])]
        hinge_axis_world = (
            armature.matrix_world.to_3x3()
            @ lower_bone.matrix_local.to_3x3()
            @ Vector(manifest_side["axis_lower_arm_rest"])
        ).normalized()
        neutral_bridge = _fixed_bridge_metrics(
            mesh,
            armature,
            source_weights,
            neutral,
            neutral_reconstruction,
            {bone.name: bone.matrix_local for bone in armature.data.bones},
            manifest_side,
            armature.matrix_world @ armature.data.bones[str(side["hand"])].head_local,
            hinge_axis_world,
        )
        scenarios["neutral"]["bridge"] = {
            **neutral_bridge,
            "hinge_axis_frame": "world; manifest lower-arm-rest wrist hinge axis conjugated by armature.matrix_world and the lower-arm rest basis",
            "gates_passed": (
                float(neutral_bridge["minimum_hinge_retention"]) >= 0.90
                and int(neutral_bridge["area_ratio_below_0_05_count"]) == 0
                and int(neutral_bridge["signed_foldover_count"]) == 0
            ),
        }
        # Anatomical bend evaluation: both metric families over the frozen
        # population, at distinct runtime twist weights, gated at the shipped
        # runtime weight.
        anatomical_bend = _evaluate_anatomical_bend(
            mesh, armature, side, source_weights, manifest_side
        )
        result["sides"][side_name] = {
            "frame": {
                "elbow": _vector(frame[0]),
                "axis": _vector(frame[1]),
                "first": _vector(frame[2]),
                "second": _vector(frame[3]),
                "length": frame[4],
            },
            "neutral_reconstruction_max_error_metres": max(
                (first - second).length for first, second in zip(neutral, neutral_reconstruction, strict=True)
            ),
            "weights": _influence_audit(mesh, armature, side_name, side, source_weights, neutral, frame),
            "scenarios": scenarios,
            "anatomical_bend": anatomical_bend,
        }
    return result


def _audit_asset(label: str, source_path: Path) -> dict[str, object]:
    source_path = source_path.resolve()
    before_hash = _sha256(source_path)
    manifest_path = source_path.with_suffix(".forearm_twist_manifest.json")
    candidate_path = source_path.with_suffix(".forearm_twist_candidate_weights.json")
    if not manifest_path.is_file():
        raise ProvenanceError(
            f"Missing pre-helper neutral manifest '{manifest_path}'. Regenerate from MPFB; do not infer provenance "
            "from this already redistributed mesh."
        )
    neutral_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if neutral_manifest.get("schema_version") != SCHEMA_VERSION:
        raise ProvenanceError(f"Unsupported neutral manifest schema at '{manifest_path}'.")
    if not candidate_path.is_file():
        raise ProvenanceError(f"Missing candidate-weight evidence '{candidate_path}'.")
    candidate_evidence = json.loads(candidate_path.read_text(encoding="utf-8"))
    bpy.ops.wm.open_mainfile(filepath=str(source_path), load_ui=False)
    armature = _require_single(
        (obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"), f"{label} armature"
    )
    skinned_meshes = [
        obj
        for obj in bpy.context.scene.objects
        if obj.type == "MESH"
        and any(modifier.type == "ARMATURE" and modifier.object == armature for modifier in obj.modifiers)
    ]
    body = _require_single((mesh for mesh in skinned_meshes if mesh.name.endswith(".body")), f"{label} body mesh")
    bones = {
        bone.name: {
            "parent": bone.parent.name if bone.parent else None,
            "use_deform": bone.use_deform,
        }
        for bone in armature.data.bones
    }
    result = {
        "source_path": source_path.as_posix(),
        "source_sha256": before_hash,
        "armature": {
            "name": armature.name,
            "object_transform": _matrix(armature.matrix_world),
            "bone_count": len(armature.data.bones),
            "bones": bones,
            "sides": {name: _bone_audit(armature, side) for name, side in SIDES.items()},
        },
        "neutral_manifest_path": manifest_path.as_posix(),
        "candidate_weight_evidence_path": candidate_path.as_posix(),
        "neutral_manifest": neutral_manifest,
        "body": _mesh_audit(body, armature, neutral_manifest, candidate_evidence),
        "other_skinned_meshes": [mesh.name for mesh in skinned_meshes if mesh != body],
    }
    after_hash = _sha256(source_path)
    if after_hash != before_hash:
        raise AssertionError(f"Read-only audit changed {source_path}")
    return result


def _not_measurable(output_path: Path, reason: str, required_inputs: list[Path]) -> int:
    """Emit deterministic unavailable evidence instead of a traceback-only failure."""

    result = {
        "schema_version": SCHEMA_VERSION,
        "producer": "tools/rigging/audit_forearm_twist.py",
        "status": "not_measurable",
        "reason": reason,
        "missing_paths": [path.resolve().as_posix() for path in sorted(required_inputs)],
        "required_inputs": [path.resolve().as_posix() for path in sorted(required_inputs)],
        "evidence_path": output_path.resolve().as_posix(),
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps(result, sort_keys=True))
    return 2


def main() -> int:
    arguments = _arguments()
    output_path = arguments.output.resolve()
    required_manifests = [arguments.female.with_suffix(".forearm_twist_manifest.json"), arguments.male.with_suffix(".forearm_twist_manifest.json")]
    missing = [path for path in required_manifests if not path.is_file()]
    if missing:
        return _not_measurable(output_path, "missing_pre_helper_neutral_manifest", missing)
    candidate_evidence = [arguments.female.with_suffix(".forearm_twist_candidate_weights.json"), arguments.male.with_suffix(".forearm_twist_candidate_weights.json")]
    missing = [path for path in candidate_evidence if not path.is_file()]
    if missing:
        return _not_measurable(output_path, "missing_candidate_weight_evidence", missing)
    result = {
        "schema_version": SCHEMA_VERSION,
        "producer": "tools/rigging/audit_forearm_twist.py",
        "blender_version": bpy.app.version_string,
        "units": {"length": "metres", "angle": "degrees", "ratios": "neutral_relative"},
        "thresholds": {
            "weight_sum_absolute_tolerance": 0.0002,
            "neutral_reconstruction_metres": 0.00001,
            "neutral_rest_bridge": {
                "minimum_hinge_retention": 0.90,
                "minimum_area_ratio": 0.05,
                "signed_foldovers": 0,
                "rationale": "RIG-002 TR11: these apply to the neutral-rest population only and are deliberately not reused as bend gates.",
            },
            "anatomical_bend": BEND_THRESHOLD_CONFIG,
            "anatomical_bend_evidence_assertions": BEND_EVIDENCE_ASSERTIONS,
            "note": "Thresholds are validation contracts; scenario values under assets are measurements.",
        },
        "assets": {
            "female": _audit_asset("female", arguments.female),
            "male": _audit_asset("male", arguments.male),
        },
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(result, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"evidence": output_path.as_posix(), "schema_version": SCHEMA_VERSION}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
