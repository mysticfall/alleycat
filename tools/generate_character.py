#!/usr/bin/env python3
"""Generate a MakeHuman/MPFB character preset into a Blender file.

Run with Blender, for example:

    blender --background --python tools/generate_character.py -- \
        game/assets/characters/test/Female.character.json
"""

from __future__ import annotations

import sys
import re
import os
import math
import json
from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace
from typing import Any, cast

import bpy

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import generate_body_colliders
import forearm_twist_generator_run_ownership
import forearm_twist_weights
import update_character_import_retarget_metadata
from rigging.forearm_twist_bridge import (
    BEND_EVIDENCE_ASSERTIONS,
    BEND_EXTENSION_DEGREES,
    BEND_FLEXION_DEGREES,
    BEND_POPULATION_BIN_WIDTH,
    BEND_POPULATION_RADIUS_MAXIMUM,
    BEND_POPULATION_SOURCE_POOL_MINIMUM,
    BEND_POPULATION_T_MAX,
    BEND_POPULATION_T_MIN,
    BIN_WIDTH,
    GODOT_FROM_BLENDER,
    MINIMUM_RADIUS_METRES,
    POSITION_GRID_METRES,
    ProvenanceError,
    RUNTIME_TWIST_WEIGHT,
    SCHEMA_VERSION as FOREARM_TWIST_MANIFEST_SCHEMA_VERSION,
    BridgeVertex,
    anatomical_hand_pose,
    anatomical_wrist_frame,
    assert_bend_sign_convention,
    canonicalise_vertices,
    canonical_triangle_id,
    godot_position,
    helper_twist_pose,
    m_inverse,
    m_mul,
    m_transform_direction,
    m_transform_point,
    palm_forward_alignment,
    pin_bend_population,
    pin_bridge_population,
    projected_wrist_hinge_axis,
    propagate_poses,
    q_from_matrix,
    signed_principal_twist,
    skin_point,
    subject_forward_frame,
    triangle_area,
    v_dot,
    v_length,
    v_normalised,
    v_sub,
)


USAGE = (
    "Usage: blender --background --python tools/generate_character.py -- "
    "<character-config.json>"
)
WORLD_ENVIRONMENT_TEXTURE_NAME = "monochrome_studio_04_1k.exr"
RIGIFY_ANIMATION_OBJECT_SUFFIX = ".rigify"
RIGIFY_SOURCE_ACTION_SUFFIX = "-noexp"
RIGIFY_POLE_VECTOR_PARENT_BONES = (
    "upper_arm_parent.R",
    "upper_arm_parent.L",
    "thigh_parent.R",
    "thigh_parent.L",
)
GENERATED_RIG_RETARGET_PRESET_NAME = "MakeHuman__GameEngine.py"
RIGIFY_RETARGET_PRESET_NAME = "Rigify_Deform.py"
ACTION_CLEAN_THRESHOLD = 0.001
ACTION_VARIATION_THRESHOLD = 0.0001
BAKED_ACTION_COLLISION_SUFFIX = ".baked"
# Per-side forearm-twist chain (RIG-002 TR1): lower arm -> twist helper -> hand,
# plus the mirrored-contamination suffixes.  The generator emits exactly one
# helper bone per side and re-parents the matching hand onto it.
FOREARM_TWIST_WEIGHT_GROUPS = (
    ("lowerarm_l", "LeftForearmTwist", "hand_l", "_r", "_l"),
    ("lowerarm_r", "RightForearmTwist", "hand_r", "_l", "_r"),
)
# Subject-frame and finger-geometry bone names for the anatomical bend fixture
# (RIG-002 TR8); the MPFB export rig naming.
FOREARM_TWIST_SUBJECT_BONES = {
    "pelvis": "pelvis",
    "head": "head",
    "left_upper_arm": "upperarm_l",
    "right_upper_arm": "upperarm_r",
}
FOREARM_TWIST_FINGER_BONES = {
    "left": {
        "middle_distal": "middle_03_l",
        "thumb_proximal": "thumb_01_l",
        "little_distal": "pinky_03_l",
        "side_sign": 1.0,
    },
    "right": {
        "middle_distal": "middle_03_r",
        "thumb_proximal": "thumb_01_r",
        "little_distal": "pinky_03_r",
        "side_sign": -1.0,
    },
}


def _bridge_tuple_matrix(matrix) -> tuple[tuple[float, float, float, float], ...]:
    """Convert a mathutils matrix to the bridge's float64 row-major tuples."""

    return tuple(tuple(float(component) for component in row) for row in matrix)


def _required_manifest_bone(armature: bpy.types.Object, name: str):
    bone = armature.data.bones.get(name)
    if bone is None:
        raise ScriptError(f'Cannot capture forearm provenance: armature lacks bone "{name}".')
    return bone


def _manifest_source_influences(armature: bpy.types.Object, weights: dict[str, float]) -> list[tuple[str, float]]:
    """Deform-bone influences with audit-identical filtering."""

    return [
        (name, weight)
        for name, weight in weights.items()
        if (bone := armature.data.bones.get(name)) is not None and bone.use_deform
    ]


def reparent_hands_onto_twist_helpers(armature: bpy.types.Object) -> None:
    """Complete the per-side RIG-002 chain ``LowerArm -> ForearmTwist -> Hand``.

    The generic MPFB source rig already emits exactly one deform-only twist
    helper per side, directly parented to its lower arm, with the hand still
    parented to the lower arm.  This step re-parents each matching hand onto its
    twist helper while preserving every bone's world rest placement, so the
    exported chain is ``LowerArm -> ForearmTwist -> Hand`` with no second helper
    bone.  Because no bone is appended, the export's existing bone indices do
    not shift; the template bone-binding guard (RIG-002 TR12) still verifies the
    serialised ``bone_idx <-> bone_name`` agreement after regeneration.
    """

    bpy.context.view_layer.objects.active = armature
    if bpy.context.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        edit_bones = armature.data.edit_bones
        for _lower, twist, hand, _wrong, _correct in FOREARM_TWIST_WEIGHT_GROUPS:
            twist_bone = edit_bones.get(twist)
            hand_bone = edit_bones.get(hand)
            if twist_bone is None or hand_bone is None:
                raise ScriptError(
                    "Cannot complete the forearm-twist chain: armature lacks the RIG-002 bones "
                    f'"{twist}" and "{hand}".'
                )
            hand_bone.parent = twist_bone
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")

    for _lower, twist, hand, _wrong, _correct in FOREARM_TWIST_WEIGHT_GROUPS:
        helper = armature.data.bones.get(twist)
        hand_bone = armature.data.bones.get(hand)
        if helper is None or hand_bone is None:
            raise ScriptError(f'Twist-chain emission lost bone "{twist}" or "{hand}" while leaving edit mode.')
        if hand_bone.parent != helper:
            raise ScriptError(
                f'Twist-chain emission did not produce the chain "{helper.name}" -> "{hand_bone.name}".'
            )
        if not helper.use_deform:
            raise ScriptError(f'Twist helper "{helper.name}" is not deformation-only-enabled.')


def capture_forearm_twist_anatomical_bend(
    mesh: bpy.types.Object,
    armature: bpy.types.Object,
    source_weights: list[dict[str, float]],
    side_name: str,
    lower: str,
    helper: str,
    hand: str,
    required: bool,
) -> dict[str, object] | None:
    """Capture the twist-only anatomical bend population and per-pose deformation.

    Membership is frozen from neutral geometry and pre-redistribution source
    ownership; per-scenario deformed positions use real-hierarchy propagation
    and the same bridge pose construction the audit re-derives, so the audit can
    parity-check this record against its own execution (RIG-002 TR9-10).  The
    single twist helper is posed per scenario at the runtime weight times the
    extracted twist; the swing component of the hand delta is never applied
    anywhere.  The body mesh must carry a dense population; clothing without
    forearm surface coverage is skipped rather than failing generation.
    """

    lower_bone = _required_manifest_bone(armature, lower)
    hand_bone = _required_manifest_bone(armature, hand)
    helper_bone = _required_manifest_bone(armature, helper)
    fingers = FOREARM_TWIST_FINGER_BONES[side_name]

    subject = subject_forward_frame(
        _required_manifest_bone(armature, FOREARM_TWIST_SUBJECT_BONES["pelvis"]).head_local[:],
        _required_manifest_bone(armature, FOREARM_TWIST_SUBJECT_BONES["head"]).head_local[:],
        _required_manifest_bone(armature, FOREARM_TWIST_SUBJECT_BONES["left_upper_arm"]).head_local[:],
        _required_manifest_bone(armature, FOREARM_TWIST_SUBJECT_BONES["right_upper_arm"]).head_local[:],
    )
    lower_rest = _bridge_tuple_matrix(lower_bone.matrix_local)
    hand_rest = _bridge_tuple_matrix(hand_bone.matrix_local)
    helper_rest = _bridge_tuple_matrix(helper_bone.matrix_local)
    frame = anatomical_wrist_frame(
        lower_rest,
        hand_rest,
        subject["forward"],
        _required_manifest_bone(armature, fingers["middle_distal"]).head_local[:],
        _required_manifest_bone(armature, fingers["thumb_proximal"]).head_local[:],
        _required_manifest_bone(armature, fingers["little_distal"]).head_local[:],
        float(fingers["side_sign"]),
    )
    longitudinal = frame["longitudinal"]
    flex_axis = frame["flex_axis"]
    wrist = frame["wrist"]
    pronation = float(frame["pronation_radians"])

    # Declared frame: the lower-arm rest frame; the only place world and
    # armature quantities meet is the documented conversion below.
    lower_rest_inverse = m_inverse(lower_rest)
    world_to_declared = (armature.matrix_world @ lower_bone.matrix_local).inverted()
    world_to_armature = armature.matrix_world.inverted()

    def declared_position(world_position) -> tuple[float, float, float]:
        return tuple(world_to_declared @ world_position)

    longitudinal_declared = v_normalised(m_transform_direction(lower_rest_inverse, longitudinal))
    flex_axis_declared = v_normalised(m_transform_direction(lower_rest_inverse, flex_axis))
    wrist_declared = m_transform_point(lower_rest_inverse, wrist)

    # Population: neutral geometry plus pre-redistribution source ownership.
    elbow_world = armature.matrix_world @ lower_bone.head_local
    wrist_world = armature.matrix_world @ lower_bone.tail_local
    axis_vector = wrist_world - elbow_world
    axis_length = axis_vector.length
    axis_unit = axis_vector.normalized()
    population_vertices: list[BridgeVertex] = []
    member_declared: dict[int, tuple[float, float, float]] = {}
    for vertex, weights in zip(mesh.data.vertices, source_weights, strict=True):
        position = mesh.matrix_world @ vertex.co
        relative = position - elbow_world
        t = float(relative.dot(axis_unit)) / axis_length
        radius = float((relative - axis_unit * relative.dot(axis_unit)).length)
        lower_weight = weights.get(lower, 0.0)
        hand_weight = weights.get(hand, 0.0)
        candidate = BridgeVertex(
            mesh.name,
            0,
            side_name,
            vertex.index,
            tuple(position),
            t,
            radius,
            lower_weight,
            weights.get(helper, 0.0),
            hand_weight,
            tuple(sorted(weights.items())),
        )
        if not (
            BEND_POPULATION_T_MIN <= t < BEND_POPULATION_T_MAX
            and radius <= BEND_POPULATION_RADIUS_MAXIMUM
            and lower_weight + weights.get(helper, 0.0) + hand_weight >= BEND_POPULATION_SOURCE_POOL_MINIMUM
            and radius >= MINIMUM_RADIUS_METRES
        ):
            continue
        member_declared[vertex.index] = declared_position(position)
        population_vertices.append(candidate)
    try:
        bend_bins = pin_bend_population(population_vertices)
    except ProvenanceError:
        if required:
            raise
        # Clothing without dense forearm-surface coverage carries no bend
        # population; the bend gates are defined on the body mesh.
        return None
    canonical = canonicalise_vertices(
        [member for members in bend_bins.values() for member in members]
    )
    source_to_id = {
        member.source_index: identifier
        for identifier, members in canonical
        for member in members
    }
    if len(source_to_id) != len(member_declared):
        raise ScriptError("Anatomical bend population welding lost or duplicated a source vertex.")

    # Local neutral edge scale per member: mean incident authored-polygon edge
    # length in the declared frame, manifest-frozen and candidate-independent.
    incident: dict[int, list[float]] = {}
    for polygon in mesh.data.polygons:
        vertices = list(polygon.vertices)
        for index, first in enumerate(vertices):
            second = vertices[(index + 1) % len(vertices)]
            if first in member_declared and second in member_declared:
                length = v_length(v_sub(member_declared[first], member_declared[second]))
                incident.setdefault(first, []).append(length)
                incident.setdefault(second, []).append(length)

    def radial_distance_declared(identifier_position: tuple[float, float, float], axis: tuple[float, float, float]) -> float:
        relative = v_sub(identifier_position, wrist_declared)
        along = v_dot(relative, axis)
        return v_length(v_sub(relative, (axis[0] * along, axis[1] * along, axis[2] * along)))

    radius_longitudinal: dict[str, float] = {}
    radius_flex: dict[str, float] = {}
    vertex_rows: list[dict[str, object]] = []
    for identifier, members in canonical:
        source_index = members[0].source_index
        position = member_declared[source_index]
        radius_longitudinal[identifier] = radial_distance_declared(position, longitudinal_declared)
        radius_flex[identifier] = radial_distance_declared(position, flex_axis_declared)
        lengths = incident.get(source_index, [])
        vertex_rows.append(
            {
                "id": identifier,
                "members": [member.source_index for member in members],
                "position_declared": list(position),
                "position_godot": godot_position(members[0].position),
                "t": members[0].t,
                "bin": math.floor(members[0].t / BEND_POPULATION_BIN_WIDTH),
                "radius_longitudinal_metres": radius_longitudinal[identifier],
                "radius_flex_metres": radius_flex[identifier],
                "local_edge_metres": (sum(lengths) / len(lengths)) if lengths else 0.0,
                "source_ownership": dict(members[0].candidate),
            }
        )

    member_ids = set(source_to_id)
    mesh.data.calc_loop_triangles()
    triangle_rows: list[dict[str, object]] = []
    for triangle in mesh.data.loop_triangles:
        indices = list(triangle.vertices)
        if not all(index in member_ids for index in indices):
            continue
        identifiers = [source_to_id[index] for index in indices]
        area = triangle_area(*[member_declared[index] for index in indices])
        if area < 1.0e-10:
            continue
        triangle_rows.append(
            {"id": canonical_triangle_id(identifiers), "vertices": identifiers, "neutral_area_metres_squared": area}
        )
    triangle_rows.sort(key=lambda item: str(item["id"]))

    bones = [
        (bone.name, bone.parent.name if bone.parent else None, _bridge_tuple_matrix(bone.matrix_local))
        for bone in armature.data.bones
    ]
    rests = {name: rest for name, _parent, rest in bones}
    armature_positions = {
        index: tuple(world_to_armature @ (mesh.matrix_world @ mesh.data.vertices[index].co))
        for index in member_declared
    }

    scenarios: dict[str, object] = {}
    for name, bend_degrees in (
        ("palm_forward", 0.0),
        ("flexion_60", BEND_FLEXION_DEGREES),
        ("extension_60", BEND_EXTENSION_DEGREES),
    ):
        bend_radians = math.radians(bend_degrees)
        if abs(bend_radians) > 0.0:
            assert_bend_sign_convention(frame, bend_radians)
        posed_hand = anatomical_hand_pose(lower_rest, hand_rest, frame, bend_radians)
        hand_delta = m_mul(posed_hand, m_inverse(hand_rest))
        twist = signed_principal_twist(q_from_matrix(hand_delta), longitudinal)
        posed_helper = helper_twist_pose(helper_rest, longitudinal, twist, RUNTIME_TWIST_WEIGHT)
        posed_palm = v_normalised(m_transform_direction(posed_hand, (0.0, 0.0, 1.0)))
        # The palm normal may tilt along the forearm axis; the forward-facing
        # contract applies to its perpendicular component.
        palm_forward_dot = palm_forward_alignment(posed_palm, frame["palm_target"], longitudinal)
        palm_minimum = float(BEND_EVIDENCE_ASSERTIONS["palm_forward_minimum_dot"]["value"])
        twist_tolerance = float(BEND_EVIDENCE_ASSERTIONS["twist_recovery_tolerance_radians"]["value"])
        if palm_forward_dot < palm_minimum:
            raise ScriptError(
                f'Anatomical capture: pronation did not face the {side_name} palm forward (dot={palm_forward_dot:.9f}).'
            )
        if abs(twist - pronation) > twist_tolerance:
            raise ScriptError(
                f'Anatomical capture: {side_name} twist did not recover the pronation ({twist} vs {pronation}).'
            )
        posed = propagate_poses(bones, {hand: posed_hand, helper: posed_helper})
        deformed_declared: dict[str, list[float]] = {}
        for index in member_declared:
            influences = _manifest_source_influences(armature, source_weights[index])
            deformed_armature = skin_point(armature_positions[index], influences, posed, rests)
            deformed_declared[source_to_id[index]] = list(m_transform_point(lower_rest_inverse, deformed_armature))
        scenarios[name] = {
            "bend_degrees": bend_degrees,
            "helper_degrees": math.degrees(RUNTIME_TWIST_WEIGHT * twist),
            "pronation_degrees": math.degrees(pronation),
            "extracted_twist_degrees": math.degrees(twist),
            "palm_forward_dot": palm_forward_dot,
            "deformed_declared": deformed_declared,
        }

    return {
        "declared_frame": "lower_arm_rest",
        "conversions": {
            "positions": "declared = (armature.matrix_world @ lower_arm.matrix_local)^-1 @ world",
            "axes": "declared = lower_arm.matrix_local.to_3x3().inverted() @ armature-space axis",
            "godot": "position_godot = godot_position(world) per the manifest coordinate convention",
            "note": "All pose maths and metrics live in the declared lower-arm rest frame; the armature object transform participates only through the documented world conversion.",
        },
        "elbow_armature": list(lower_bone.head_local),
        "wrist_armature": list(hand_bone.head_local),
        "longitudinal_axis_armature": list(longitudinal),
        "flex_axis_armature": list(flex_axis),
        "longitudinal_axis_declared": list(longitudinal_declared),
        "flex_axis_declared": list(flex_axis_declared),
        "subject_forward_armature": list(subject["forward"]),
        "palm_rest_armature": list(frame["palm_rest"]),
        "palm_target_declared": list(frame["palm_target"]),
        "palm_thumb_alignment": float(frame["thumb_alignment"]),
        "palm_little_alignment": float(frame["little_alignment"]),
        "pronation_degrees": math.degrees(pronation),
        "population_bin_counts": {str(index): len(members) for index, members in sorted(bend_bins.items())},
        "population_vertex_count": len(vertex_rows),
        "population_triangle_count": len(triangle_rows),
        "vertices": vertex_rows,
        "triangles": triangle_rows,
        "scenarios": scenarios,
    }


def capture_forearm_twist_neutral_manifest(
    exported_objects: set[bpy.types.Object],
    armature: bpy.types.Object,
) -> dict[str, object]:
    """Capture source ownership before helper redistribution changes it.

    The sidecar is intentionally assembled from the freshly exported MPFB mesh,
    not from a later checked-in generated file.  The latter has already had its
    candidate helper weights applied and is not valid provenance for a fixed
    bridge population.
    """

    from mathutils import Vector

    meshes: list[dict[str, object]] = []
    for mesh in sorted(exported_objects, key=lambda item: item.name):
        if mesh.type != "MESH" or not any(
            modifier.type == "ARMATURE" and modifier.object == armature for modifier in mesh.modifiers
        ):
            continue
        names = {group.index: group.name for group in mesh.vertex_groups}
        source_weights = [
            {
                names[item.group]: float(item.weight)
                for item in vertex.groups
                if item.group in names and item.weight > 0.0
            }
            for vertex in mesh.data.vertices
        ]
        sides: dict[str, object] = {}
        for side_name, (lower, helper, hand, _wrong, _correct) in zip(
            ("left", "right"), FOREARM_TWIST_WEIGHT_GROUPS, strict=True
        ):
            lower_bone = armature.data.bones.get(lower)
            hand_bone = armature.data.bones.get(hand)
            if lower_bone is None or hand_bone is None:
                continue
            elbow = armature.matrix_world @ lower_bone.head_local
            axis_vector = (armature.matrix_world @ lower_bone.tail_local) - elbow
            if axis_vector.length <= 1.0e-8:
                raise ScriptError(f'Cannot capture forearm provenance: bone "{lower}" has no length.')
            axis = axis_vector.normalized()
            lower_basis = armature.matrix_world.to_3x3() @ lower_bone.matrix_local.to_3x3()
            lower_basis_inverse = lower_basis.inverted()
            lower_axis_rest = lower_basis_inverse @ axis
            hand_z_rest = lower_basis_inverse @ (armature.matrix_world.to_3x3() @ hand_bone.matrix_local.to_3x3() @ Vector((0.0, 0.0, 1.0)))
            hinge_axis_rest = Vector(projected_wrist_hinge_axis(tuple(hand_z_rest), tuple(lower_axis_rest)))
            hinge_axis = lower_basis @ hinge_axis_rest
            hinge_pivot = armature.matrix_world @ hand_bone.head_local
            vertices = []
            for vertex, weights in zip(mesh.data.vertices, source_weights, strict=True):
                position = mesh.matrix_world @ vertex.co
                relative = position - elbow
                t = relative.dot(axis) / axis_vector.length
                hinge_relative = position - hinge_pivot
                radius = (hinge_relative - (hinge_axis * hinge_relative.dot(hinge_axis))).length
                vertices.append(
                    BridgeVertex(
                        mesh.name,
                        0,
                        side_name,
                        vertex.index,
                        tuple(position),
                        t,
                        radius,
                        weights.get(lower, 0.0),
                        weights.get(helper, 0.0),
                        weights.get(hand, 0.0),
                        tuple(sorted(weights.items())),
                    )
                )
            mixed_bins, bridge_vertices, single_anchor = pin_bridge_population(vertices)
            canonical = canonicalise_vertices(bridge_vertices)
            source_to_id = {
                member.source_index: identifier
                for identifier, members in canonical
                for member in members
            }
            mixed_indices = {member.source_index for members in mixed_bins.values() for member in members}
            mesh.data.calc_loop_triangles()
            triangles = []
            for triangle in mesh.data.loop_triangles:
                source_indices = list(triangle.vertices)
                if not all(index in source_to_id for index in source_indices) or not any(index in mixed_indices for index in source_indices):
                    continue
                points = [mesh.matrix_world @ mesh.data.vertices[index].co for index in source_indices]
                area = ((points[1] - points[0]).cross(points[2] - points[0])).length * 0.5
                if area < 1.0e-10:
                    continue
                ids = [source_to_id[index] for index in source_indices]
                triangles.append({"id": canonical_triangle_id(ids), "vertices": ids, "neutral_area_metres_squared": area})
            sides[side_name] = {
                "pivot_godot_metres": godot_position(hinge_pivot),
                "axis_godot": godot_position(hinge_axis),
                "lower_arm_longitudinal_axis_godot": godot_position(axis),
                "axis_lower_arm_rest": tuple(hinge_axis_rest),
                "mixed_bin_counts": {str(index): len(members) for index, members in sorted(mixed_bins.items())},
                "fixed_bridge_count": len(bridge_vertices),
                "single_anchor_count": len(single_anchor),
                "vertices": [
                    {
                        "id": identifier,
                        "members": [member.source_index for member in members],
                        "duplicate_count": len(members),
                        "position_godot_metres": godot_position(members[0].position),
                        "t": members[0].t,
                        "bin": math.floor(members[0].t / BIN_WIDTH),
                        "radius_metres": members[0].radius,
                        "baseline": {"lower_arm": members[0].lower, "helper": members[0].helper, "hand": members[0].hand},
                        "source_ownership": dict(members[0].candidate),
                        "classification": "mixed" if members[0].source_index in mixed_indices else "single_anchor",
                    }
                    for identifier, members in canonical
                ],
                "triangles": sorted(triangles, key=lambda item: str(item["id"])),
                **(
                    {}
                    if (anatomical_bend := capture_forearm_twist_anatomical_bend(
                        mesh,
                        armature,
                        source_weights,
                        side_name,
                        lower,
                        helper,
                        hand,
                        required=mesh.name.endswith(".body"),
                    ))
                    is None
                    else {"anatomical_bend": anatomical_bend}
                ),
            }
        if sides:
            meshes.append({"name": mesh.name, "surface": 0, "sides": sides})
    return {
        "schema_version": FOREARM_TWIST_MANIFEST_SCHEMA_VERSION,
        "producer": "tools/generate_character.py (pre-helper redistribution)",
        "coordinate_convention": {
            "positions": "Godot skeleton-local metres",
            "godot_from_blender": GODOT_FROM_BLENDER,
            "winding": "orientation-preserving; cyclic triangle rotations only",
            "units": "metres",
            "grid_origin_metres": [0.0, 0.0, 0.0],
            "position_quantisation_metres": POSITION_GRID_METRES,
            "anatomical_bend_positions": "declared lower-arm rest frame metres (see sides.<side>.anatomical_bend.conversions)",
        },
        "fixed_population": {
            "t": [0.85, 1.10],
            "source_lower_arm_plus_hand_minimum": 0.50,
            "radius_minimum_metres": 1.0e-5,
            "mixed": {"lower_arm_minimum": 0.05, "hand_minimum": 0.05, "bin_width": BIN_WIDTH, "minimum_reference_vertices": 10},
            "single_anchor": "every pinned window vertex whose pre-tuning lower+hand ownership is not a two-anchor blend row",
            "anatomical_bend": {
                "t": [BEND_POPULATION_T_MIN, BEND_POPULATION_T_MAX],
                "radius_maximum_metres": BEND_POPULATION_RADIUS_MAXIMUM,
                "source_forearm_chain_pool_minimum": BEND_POPULATION_SOURCE_POOL_MINIMUM,
                "minimum_reference_vertices_per_bin": 10,
                "rationale": "Widens the measured region to the surrounding wrist and distal-forearm transition so pinch relocation cannot evade the bend gates (RIG-002 TR9); the pool covers the lower-arm, helper, and hand chain.",
            },
        },
        "meshes": meshes,
    }


def capture_forearm_twist_candidate_weight_evidence(
    exported_objects: set[bpy.types.Object], neutral_manifest: dict[str, object]
) -> dict[str, object]:
    """Record redistributed candidate weights without mutating neutral ownership."""

    meshes = {mesh.name: mesh for mesh in exported_objects if mesh.type == "MESH"}
    records = []
    for manifest_mesh in neutral_manifest["meshes"]:
        mesh = meshes.get(manifest_mesh["name"])
        if mesh is None:
            raise ScriptError(f'Candidate evidence cannot find exported mesh "{manifest_mesh["name"]}".')
        names = {group.index: group.name for group in mesh.vertex_groups}
        sides = {}
        for side_name, manifest_side in manifest_mesh["sides"].items():
            rows = []
            for row in manifest_side["vertices"]:
                weights = []
                for index in row["members"]:
                    weights.append({names[item.group]: float(item.weight) for item in mesh.data.vertices[index].groups if item.group in names and item.weight > 0.0})
                if any(candidate != weights[0] for candidate in weights[1:]):
                    raise ScriptError(f'Candidate seam weights disagree for canonical vertex "{row["id"]}".')
                rows.append({"id": row["id"], "weights": weights[0]})
            sides[side_name] = {"vertices": rows}
        records.append({"name": manifest_mesh["name"], "sides": sides})
    return {
        "schema_version": FOREARM_TWIST_MANIFEST_SCHEMA_VERSION,
        "producer": "tools/generate_character.py (post-helper redistribution candidate weights)",
        "neutral_manifest_schema_version": FOREARM_TWIST_MANIFEST_SCHEMA_VERSION,
        "meshes": records,
    }


class ScriptError(Exception):
    """Raised for expected user-facing script failures."""


@dataclass(frozen=True)
class CharacterConfig:
    """Validated character generation configuration."""

    preset: str
    name: str
    output_file: str
    amimations: tuple[str, ...]
    config_path: Path
    game_dir: Path

    @property
    def output_file_path(self) -> Path:
        return self.game_dir / self.output_file

    @property
    def animation_source_paths(self) -> tuple[Path, ...]:
        return tuple(self.game_dir / source for source in self.amimations)


def blender_script_args(argv: list[str]) -> list[str]:
    """Return script arguments after Blender's optional `--` separator."""

    if "--" in argv:
        return argv[argv.index("--") + 1 :]

    return argv[1:]


def parse_args(argv: list[str]) -> Path:
    args = blender_script_args(argv)
    if len(args) != 1:
        raise ScriptError(f"Expected exactly one configuration path.\n{USAGE}")

    config_path = Path(args[0]).expanduser()
    if not config_path.is_absolute():
        config_path = Path.cwd() / config_path

    return config_path


def require_non_empty_string(data: dict[str, Any], key: str, config_path: Path) -> str:
    value = data.get(key)
    if not isinstance(value, str) or not value.strip():
        raise ScriptError(
            f'Character configuration "{config_path}" must define non-empty string "{key}".'
        )

    return value.strip()


def require_string_list(data: dict[str, Any], key: str, config_path: Path) -> tuple[str, ...]:
    value = data.get(key)
    if not isinstance(value, list):
        raise ScriptError(
            f'Character configuration "{config_path}" must define "{key}" as a list of '
            "non-empty strings."
        )

    strings: list[str] = []
    for index, item in enumerate(value):
        if not isinstance(item, str) or not item.strip():
            raise ScriptError(
                f'Character configuration "{config_path}" must define "{key}" as a list of '
                f"non-empty strings; item {index} is invalid."
            )
        strings.append(item.strip())

    return tuple(strings)


def validate_game_relative_path(value: str, key: str, config_path: Path) -> None:
    path = Path(value)
    if path.is_absolute() or any(part == ".." for part in path.parts):
        raise ScriptError(
            f'Character configuration "{config_path}" field "{key}" must be relative to '
            f'@game and must not be absolute or contain parent traversal: "{value}".'
        )


def resolve_game_directory_for_config(config_path: Path) -> Path:
    """Resolve @game for a character config without relying on the output path."""

    for ancestor in [config_path.parent, *config_path.parent.parents]:
        if ancestor.name == "game":
            return ancestor

    cwd_game_dir = Path.cwd() / "game"
    if cwd_game_dir.is_dir():
        return cwd_game_dir.resolve()

    raise ScriptError(
        f'Character configuration "{config_path}" could not resolve @game. Place the config '
        'under a "game" directory or run from the repository root containing "game".'
    )


def load_character_config(config_path: Path) -> CharacterConfig:
    try:
        raw_text = config_path.read_text(encoding="utf-8")
    except OSError as exc:
        raise ScriptError(f'Could not read character configuration "{config_path}": {exc}') from exc

    try:
        data = json.loads(raw_text)
    except json.JSONDecodeError as exc:
        raise ScriptError(
            f'Character configuration "{config_path}" is not valid JSON: {exc.msg} '
            f"at line {exc.lineno}, column {exc.colno}."
        ) from exc

    if not isinstance(data, dict):
        raise ScriptError(f'Character configuration "{config_path}" must contain a JSON object.')

    allowed_fields = {"preset", "name", "outputFile", "amimations"}
    unknown_fields = sorted(str(key) for key in data if key not in allowed_fields)
    if unknown_fields:
        field_list = ", ".join(unknown_fields)
        raise ScriptError(
            f'Character configuration "{config_path}" contains unsupported field(s): '
            f"{field_list}. Expected only: amimations, name, outputFile, preset."
        )

    preset = require_non_empty_string(data, "preset", config_path)
    name = require_non_empty_string(data, "name", config_path)
    output_file = require_non_empty_string(data, "outputFile", config_path)
    amimations = require_string_list(data, "amimations", config_path)

    validate_game_relative_path(output_file, "outputFile", config_path)
    if Path(output_file).suffix != ".blend":
        raise ScriptError(
            f'Character configuration "{config_path}" field "outputFile" must include the '
            f'generated Blender file name with a .blend suffix: "{output_file}".'
        )
    for source in amimations:
        validate_game_relative_path(source, "amimations", config_path)

    return CharacterConfig(
        preset=preset,
        name=name,
        output_file=output_file,
        amimations=amimations,
        config_path=config_path,
        game_dir=resolve_game_directory_for_config(config_path),
    )


def get_scene() -> bpy.types.Scene:
    scene = bpy.data.scenes.get("Scene")
    if scene is None:
        raise ScriptError('Required Blender scene "Scene" was not found.')

    return scene


def get_mpfb_generate_operator():
    try:
        return bpy.ops.mpfb.human_from_presets
    except AttributeError as exc:
        raise ScriptError(
            "MPFB operator bpy.ops.mpfb.human_from_presets is not available. "
            "Ensure the MPFB add-on is installed and enabled."
        ) from exc


def get_mpfb_export_operator():
    try:
        return bpy.ops.mpfb.export_copy
    except AttributeError as exc:
        raise ScriptError(
            "MPFB operator bpy.ops.mpfb.export_copy is not available. "
            "Ensure the MPFB add-on is installed and enabled."
        ) from exc


def snapshot_export_physical_memberships(
    exported_objects: set[bpy.types.Object],
) -> dict[int, tuple[int, tuple[tuple[int, str], ...], list[dict[str, float]]]]:
    """Freeze every export mesh's physical rows before any generator cleanup."""

    snapshots = {}
    for mesh in exported_objects:
        if mesh.type != "MESH":
            continue
        pointer = mesh.as_pointer()
        if not pointer or pointer in snapshots:
            raise ScriptError(f'Cannot snapshot export mesh "{mesh.name}": duplicate or invalid object identity.')
        groups = [(group.index, group.name) for group in mesh.vertex_groups]
        if len({index for index, _ in groups}) != len(groups) or len({name for _, name in groups}) != len(groups):
            raise ScriptError(f'Cannot snapshot export mesh "{mesh.name}": ambiguous group index or name.')
        names = dict(groups)
        rows = []
        for position, vertex in enumerate(mesh.data.vertices):
            if vertex.index != position:
                raise ScriptError(f'Cannot snapshot export mesh "{mesh.name}": vertex ordering changed.')
            row = {}
            for assignment in vertex.groups:
                name = names.get(assignment.group)
                if name is None or name in row or not 0.0 <= assignment.weight <= 1.0:
                    raise ScriptError(f'Cannot snapshot export mesh "{mesh.name}" vertex {position}: ambiguous physical group.')
                row[name] = float(assignment.weight)
            rows.append(row)
        data_pointer = mesh.data.as_pointer()
        if not data_pointer:
            raise ScriptError(f'Cannot snapshot export mesh "{mesh.name}": invalid mesh data identity.')
        snapshots[pointer] = (data_pointer, tuple(sorted(groups)), rows)
    if not snapshots:
        raise ScriptError("MPFB export_copy created no export meshes for physical snapshot.")
    return snapshots


def verify_export_physical_memberships(
    exported_objects: set[bpy.types.Object],
    snapshots: dict[int, tuple[int, tuple[tuple[int, str], ...], list[dict[str, float]]]],
    objects_before_export_pointers: set[int] | None = None,
) -> dict[str, list[dict[str, float]]]:
    """Bind the frozen export rows to final names only after verifying the entire interval."""

    live_pointers = {obj.as_pointer() for obj in bpy.data.objects}
    if any(pointer not in live_pointers for pointer in snapshots):
        raise ScriptError("Export physical snapshot: dropped export object before authoring.")
    if objects_before_export_pointers is not None:
        unexpected = {obj.as_pointer() for obj in bpy.data.objects if obj.type == "MESH"} - objects_before_export_pointers - snapshots.keys()
        if unexpected:
            raise ScriptError("Export physical snapshot: newly introduced mesh before authoring.")
    current = snapshot_export_physical_memberships(exported_objects)
    if current.keys() != snapshots.keys():
        raise ScriptError("Export physical snapshot: missing, duplicate, newly introduced or dropped mesh identity.")
    by_name = {}
    for mesh in exported_objects:
        if mesh.type != "MESH":
            continue
        pointer = mesh.as_pointer()
        if current[pointer] != snapshots[pointer]:
            raise ScriptError(f'Export physical snapshot changed before authoring on "{mesh.name}" (data, groups, vertices or memberships).')
        if mesh.name in by_name:
            raise ScriptError(f'Export physical snapshot: duplicate final mesh name "{mesh.name}".')
        by_name[mesh.name] = snapshots[pointer][2]
    return by_name


def capture_forearm_twist_axial_input(
    exported_objects: set[bpy.types.Object],
    armature: bpy.types.Object,
    physical_memberships: dict[str, list[dict[str, float]]] | None = None,
) -> dict[str, list[dict[str, float]]]:
    """Capture complete physical rows separately from positive axial input."""

    prepared: dict[str, list[dict[str, float]]] = {}
    for mesh in exported_objects:
        if mesh.type != "MESH" or not any(
            modifier.type == "ARMATURE" and modifier.object == armature
            for modifier in mesh.modifiers
        ):
            continue
        group_names = {group.index: group.name for group in mesh.vertex_groups}
        physical_rows = []
        for vertex in mesh.data.vertices:
            row = {}
            for assignment in vertex.groups:
                name = group_names.get(assignment.group)
                if name is None:
                    raise ScriptError(
                        f'Cannot snapshot physical row on "{mesh.name}" vertex {vertex.index}: '
                        f'unknown vertex group index {assignment.group}.'
                    )
                if assignment.weight < 0.0:
                    raise ScriptError(
                        f'Cannot snapshot physical row on "{mesh.name}" vertex {vertex.index} '
                        f'group {name!r}: negative membership.'
                    )
                if name in row:
                    raise ScriptError(f'Cannot snapshot duplicate group {name!r} on "{mesh.name}" vertex {vertex.index}.')
                row[name] = float(assignment.weight)
            physical_rows.append(row)
        # No exporter-ghost attribution is available for these rows. Preserve
        # every zero, including opposite-hand finger memberships, by default.
        prepared[mesh.name] = [{name: weight for name, weight in row.items() if weight > 0.0}
                               for row in physical_rows]
        if physical_memberships is not None:
            physical_memberships[mesh.name] = physical_rows
    return prepared


def capture_forearm_twist_generator_run_ownership(
    exported_objects: set[bpy.types.Object],
    axial_reference: dict[str, list[dict[str, float]]],
    authoring_eligible: dict[str, dict[str, list[int]]],
    physical_memberships: dict[str, list[dict[str, float]]],
    source_domain: dict[str, dict[str, list[int]]],
) -> list[dict[str, object]]:
    """Capture the immutable axial reference and original export-local physical key presence."""

    records: list[dict[str, object]] = []
    for mesh in sorted(exported_objects, key=lambda item: item.name):
        if mesh.type != "MESH" or mesh.name not in axial_reference:
            continue
        rows = axial_reference[mesh.name]
        if len(rows) != len(mesh.data.vertices):
            raise ScriptError(f'Cannot capture generator-run ownership: mesh "{mesh.name}" vertex count changed.')
        if mesh.name not in authoring_eligible:
            raise ScriptError(f'Cannot capture generator-run ownership: missing authoring eligibility for "{mesh.name}".')
        if mesh.name not in source_domain:
            raise ScriptError(f'Cannot capture generator-run ownership: missing source domain for "{mesh.name}".')
        original = physical_memberships.get(mesh.name)
        if original is None or len(original) != len(rows):
            raise ScriptError(f'Cannot capture generator-run ownership: missing physical snapshot for "{mesh.name}".')
        records.append(
            {
                "name": mesh.name,
                "vertex_count": len(rows),
                "authoring_eligible": authoring_eligible[mesh.name],
                "source_domain": source_domain[mesh.name],
                "original_zero_rows": [
                    {"vertex": index, "zeros": sorted(name for name, weight in weights.items() if weight == 0.0)}
                    for index, weights in enumerate(original)
                    if any(weight == 0.0 for weight in weights.values())
                ],
                "original_positive_rows": [
                    {"vertex": index, "positives": sorted(name for name, weight in weights.items() if weight > 0.0)}
                    for index, weights in enumerate(original)
                    if any(weight > 0.0 for weight in weights.values())
                ],
                "axial_rows": [
                    {"vertex": index, "weights": weights}
                    for index, weights in enumerate(rows)
                ],
            }
        )
    return records


def capture_skipped_export_physical_meshes(
    exported_objects: set[bpy.types.Object],
    axial_mesh_names: set[str],
    physical_memberships: dict[str, list[dict[str, float]]],
    export_snapshot: dict[int, tuple[int, tuple[tuple[int, str], ...], list[dict[str, float]]]],
) -> list[dict[str, object]]:
    """Keep the untouched export-stage rows for every non-axial export mesh."""

    records = []
    exported_names = {mesh.name for mesh in exported_objects if mesh.type == "MESH"}
    if not axial_mesh_names <= exported_names or set(physical_memberships) != exported_names:
        raise ScriptError("Export physical census does not partition every export mesh.")
    for mesh in sorted(exported_objects, key=lambda item: item.name):
        if mesh.type != "MESH" or mesh.name in axial_mesh_names:
            continue
        original = physical_memberships[mesh.name]
        snapshot = export_snapshot.get(mesh.as_pointer())
        if snapshot is None or len(original) != len(mesh.data.vertices):
            raise ScriptError(f'Export physical census lost mesh identity or rows on "{mesh.name}".')
        groups = tuple(sorted((group.index, group.name) for group in mesh.vertex_groups))
        if mesh.data.as_pointer() != snapshot[0] or groups != snapshot[1]:
            raise ScriptError(f'Export physical census changed mesh data or groups on "{mesh.name}".')
        records.append({
            "name": mesh.name,
            "vertex_count": len(original),
            "group_indices": [[index, name] for index, name in snapshot[1]],
            "original_physical_rows": [
                {"vertex": index, "weights": row} for index, row in enumerate(original)
            ],
        })
    return records


def apply_forearm_twist_gradient_on_export(
    exported_objects: set[bpy.types.Object],
    armature: bpy.types.Object,
    axial_input: dict[str, list[dict[str, float]]],
    authoring_eligible: dict[str, dict[str, list[int]]] | None = None,
    physical_memberships: dict[str, list[dict[str, float]]] | None = None,
    source_domain: dict[str, dict[str, list[int]]] | None = None,
) -> dict[str, list[dict[str, float]]]:
    """Author axial ownership and freeze its immutable physical reference."""

    references: dict[str, list[dict[str, float]]] = {}
    for mesh in exported_objects:
        if mesh.type != "MESH" or not any(
            modifier.type == "ARMATURE" and modifier.object == armature
            for modifier in mesh.modifiers
        ):
            continue
        polygons = [tuple(polygon.vertices) for polygon in mesh.data.polygons]
        if mesh.name not in axial_input:
            raise ScriptError(f'Cannot apply forearm twist: missing axial input ownership for "{mesh.name}".')
        source_weights = [dict(weights) for weights in axial_input[mesh.name]]
        if len(source_weights) != len(mesh.data.vertices):
            raise ScriptError(f'Cannot apply forearm twist: mesh "{mesh.name}" vertex count changed.')
        deform_bones = {bone.name for bone in armature.data.bones if bone.use_deform}
        sides = []
        projections = {}
        for lower_arm, helper, hand, _wrong_suffix, _correct_suffix in FOREARM_TWIST_WEIGHT_GROUPS:
            if lower_arm not in deform_bones or helper not in deform_bones or hand not in deform_bones:
                raise ScriptError(f'Cannot author forearm twist: missing deform chain for "{lower_arm}".')

            hand_bone = armature.data.bones[hand]
            finger_groups = frozenset(
                bone.name for bone in armature.data.bones
                if bone.use_deform and bone != hand_bone and
                any(ancestor == hand_bone for ancestor in bone.parent_recursive)
            )
            sides.append(forearm_twist_weights.AxialSide(lower_arm, helper, hand, finger_groups))

            lower_arm_bone = armature.data.bones[lower_arm]
            elbow = armature.matrix_world @ lower_arm_bone.head_local
            axis = (armature.matrix_world @ lower_arm_bone.tail_local) - elbow
            axis_length_squared = axis.length_squared
            if axis_length_squared <= 1e-8:
                raise ScriptError(f'Cannot apply forearm twist: bone "{lower_arm}" has no length.')

            projected = []
            for vertex in mesh.data.vertices:
                relative = (mesh.matrix_world @ vertex.co) - elbow
                along_lower_arm = relative.dot(axis) / axis_length_squared
                radial_offset = relative - axis * along_lower_arm
                projected.append((along_lower_arm, radial_offset.length))
            projections[lower_arm[-2:]] = projected

        vertices = [forearm_twist_weights.ForearmVertex(0.0, 0.0, weights)
                    for weights in source_weights]
        try:
            authored = forearm_twist_weights.author_axial_weights(vertices, polygons, sides, projections)
        except (ValueError, IndexError) as exc:
            raise ScriptError(f'Cannot author forearm twist on "{mesh.name}": {exc}') from exc
        final = [dict(row) for row in authored.reference]
        # Provisional pre-export ceiling for Godot's configured All Influences skin import;
        # passing this check does not prove exported/imported channel retention.
        for index, row in enumerate(final):
            if sum(weight > 0.0 and name in deform_bones for name, weight in row.items()) > 8:
                raise ScriptError(f'Cannot author forearm twist: "{mesh.name}" vertex {index} exceeds eight deform influences.')
        references[mesh.name] = [dict(row) for row in authored.reference]
        if source_domain is not None:
            source_domain[mesh.name] = {
                side.helper: [index for index, allowed in enumerate(authored.source_domain[position]) if allowed]
                for position, side in enumerate(sides)
            }
        if authoring_eligible is not None:
            authoring_eligible[mesh.name] = {
                side.helper: [index for index, beta in enumerate(authored.beta[position])
                              if beta > 0.0 and authored.logical_twist[position][index] > 0.0]
                for position, side in enumerate(sides)
            }
        if physical_memberships is None or mesh.name not in physical_memberships:
            raise ScriptError(f'Cannot write forearm twist: missing physical snapshot for "{mesh.name}".')
        write_forearm_twist_vertex_weights(mesh, final, physical_memberships[mesh.name])
    return references


def write_forearm_twist_vertex_weights(
    mesh: bpy.types.Object, rows: list[dict[str, float]], physical_rows: list[dict[str, float]]
) -> None:
    """Write positive ownership and retain every original physical zero key."""

    if len(rows) != len(mesh.data.vertices) or len(physical_rows) != len(rows):
        raise ScriptError(f'Cannot write forearm twist on "{mesh.name}": vertex count changed.')
    required_groups = sorted({name for weights in rows for name, weight in weights.items() if weight > 0.0})
    existing_names = {group.index: group.name for group in mesh.vertex_groups}
    # Validate all rows before mutating any group: unknown indices, missing
    # source keys and unapproved new physical zeros cannot be silently repaired.
    for vertex, weights, physical in zip(mesh.data.vertices, rows, physical_rows, strict=True):
        current = {}
        for assignment in vertex.groups:
            name = existing_names.get(assignment.group)
            if name is None:
                raise ScriptError(f'Cannot write physical row on "{mesh.name}" vertex {vertex.index}: unknown vertex group index {assignment.group}.')
            current[name] = assignment.weight
        if set(current) != set(physical):
            raise ScriptError(f'Cannot write physical row on "{mesh.name}" vertex {vertex.index}: membership changed since snapshot.')
        if any(weight < 0.0 for weight in weights.values()):
            raise ScriptError(f'Cannot write physical row on "{mesh.name}" vertex {vertex.index}: negative weight.')
        new_positive = {name for name, weight in weights.items() if weight > 0.0 and name not in physical}
        helper_names = {side[1] for side in FOREARM_TWIST_WEIGHT_GROUPS}
        if new_positive - helper_names:
            raise ScriptError(f'Cannot write physical row on "{mesh.name}" vertex {vertex.index}: unexpected new physical keys {sorted(new_positive - helper_names)}.')
    groups = {
        name: mesh.vertex_groups.get(name) or mesh.vertex_groups.new(name=name)
        for name in required_groups
    }
    for vertex, weights, physical in zip(mesh.data.vertices, rows, physical_rows, strict=True):
        for assignment in tuple(vertex.groups):
            name = existing_names.get(assignment.group)
            if weights.get(name, 0.0) <= 0.0 and name not in physical:
                mesh.vertex_groups[name].remove([vertex.index])
            elif name in physical and weights.get(name, 0.0) <= 0.0:
                mesh.vertex_groups[name].add([vertex.index], 0.0, "REPLACE")
        for name, weight in weights.items():
            if weight > 0.0:
                groups[name].add([vertex.index], weight, "REPLACE")


def get_orphans_purge_operator():
    try:
        return bpy.ops.outliner.orphans_purge
    except AttributeError as exc:
        raise ScriptError(
            "Blender operator bpy.ops.outliner.orphans_purge is not available."
        ) from exc


def get_file_pack_all_operator():
    try:
        return bpy.ops.file.pack_all
    except AttributeError as exc:
        raise ScriptError("Blender operator bpy.ops.file.pack_all is not available.") from exc


def get_file_unpack_all_operator():
    try:
        return bpy.ops.file.unpack_all
    except AttributeError as exc:
        raise ScriptError("Blender operator bpy.ops.file.unpack_all is not available.") from exc


def get_retarget_preset_apply_operator():
    try:
        return bpy.ops.object.expy_kit_armature_preset_apply
    except AttributeError as exc:
        raise ScriptError(
            "Retarget operator bpy.ops.object.expy_kit_armature_preset_apply is not available. "
            "Ensure the Retarget extension is installed and enabled."
        ) from exc


def get_retarget_constrain_to_armature_operator():
    try:
        return bpy.ops.armature.retarget_constrain_to_armature
    except AttributeError as exc:
        raise ScriptError(
            "Retarget operator bpy.ops.armature.retarget_constrain_to_armature is not "
            "available. Ensure the Retarget extension is installed and enabled."
        ) from exc


def get_action_clean_operator():
    try:
        return bpy.ops.action.clean
    except AttributeError as exc:
        raise ScriptError("Blender operator bpy.ops.action.clean is not available.") from exc


def prime_retarget_constrain_operator_mode_state(failure_context: str) -> str | None:
    """Seed Retarget's non-RNA mode cache for background operator execution.

    The Retarget extension keeps ``ConstrainToArmature.current_m`` as a plain class
    attribute rather than an operator property. In background Blender,
    ``INVOKE_DEFAULT`` can reach ``execute`` with that value still set to ``None``, and
    the extension later passes it to ``bpy.ops.object.mode_set``. Setting the class
    fallback here preserves the direct operator path while giving the extension a
    valid mode to restore to.
    """

    current_mode = bpy.context.mode
    if not isinstance(current_mode, str) or not current_mode:
        raise ScriptError(
            f"{failure_context} failed because Blender did not expose a valid current mode "
            "before invoking the Retarget constrain operator."
        )

    operator_type = None
    for module in list(sys.modules.values()):
        candidate = getattr(module, "ConstrainToArmature", None)
        if getattr(candidate, "bl_idname", None) == "armature.retarget_constrain_to_armature":
            operator_type = candidate
            break

    if operator_type is None:
        raise ScriptError(
            f"{failure_context} failed because the Retarget constrain operator class could not "
            "be located after the operator was registered. Ensure the Retarget extension is "
            "installed and enabled."
        )

    previous_mode = getattr(operator_type, "current_m", None)
    setattr(operator_type, "current_m", current_mode)
    return previous_mode


def restore_retarget_constrain_operator_mode_state(previous_mode: str | None) -> None:
    """Restore Retarget's class-level mode fallback after a direct bind attempt."""

    for module in list(sys.modules.values()):
        candidate = getattr(module, "ConstrainToArmature", None)
        if getattr(candidate, "bl_idname", None) == "armature.retarget_constrain_to_armature":
            setattr(candidate, "current_m", previous_mode)
            return


def require_scene_properties(scene: bpy.types.Scene, property_names: list[str], purpose: str) -> None:
    missing = [property_name for property_name in property_names if not hasattr(scene, property_name)]
    if missing:
        missing_list = ", ".join(missing)
        raise ScriptError(
            f'Scene "Scene" does not expose MPFB properties required for {purpose}: '
            f"{missing_list}. Ensure the MPFB add-on is installed and enabled."
        )


def clear_scene_objects(scene: bpy.types.Scene) -> None:
    for obj in list(scene.objects):
        bpy.data.objects.remove(obj, do_unlink=True)


def select_generated_armature(scene: bpy.types.Scene, preset: str) -> bpy.types.Object:
    armature = bpy.data.objects.get(preset)
    if armature is None:
        raise ScriptError(f'Expected generated armature named "{preset}" was not found.')

    if armature.type != "ARMATURE":
        raise ScriptError(
            f'Generated object named "{preset}" exists but is type "{armature.type}", not "ARMATURE".'
        )

    if scene.objects.get(armature.name) != armature:
        raise ScriptError(f'Generated armature named "{preset}" is not linked to scene "Scene".')

    for obj in scene.objects:
        obj.select_set(False)

    armature.select_set(True)
    bpy.context.view_layer.objects.active = armature

    return armature


def collect_object_hierarchy(root: bpy.types.Object) -> set[bpy.types.Object]:
    """Return the root object and every descendant object in its hierarchy."""

    hierarchy = {root}
    pending = list(root.children)
    while pending:
        obj = pending.pop()
        if obj in hierarchy:
            continue

        hierarchy.add(obj)
        pending.extend(obj.children)

    return hierarchy


def find_generated_body_mesh(armature: bpy.types.Object, character_name: str) -> bpy.types.Object:
    """Return the generated body mesh under the character armature.

    MPFB export normalisation renames the generated character hierarchy to the configured
    character prefix, so the preferred body mesh object/data name is `<character>.body`.
    Fall back to any descendant mesh whose object or data name ends with `.body`, but fail
    clearly when the source body is absent or ambiguous.
    """

    expected_name = f"{character_name}.body"
    mesh_descendants = [obj for obj in collect_object_hierarchy(armature) if obj != armature and obj.type == "MESH"]

    exact_matches = [
        obj
        for obj in mesh_descendants
        if obj.name == expected_name or getattr(obj.data, "name", None) == expected_name
    ]
    if len(exact_matches) == 1:
        return exact_matches[0]
    if len(exact_matches) > 1:
        names = ", ".join(sorted(obj.name for obj in exact_matches))
        raise ScriptError(
            f'Collider generation found multiple exact body mesh matches for "{expected_name}" '
            f'under armature "{armature.name}": {names}.'
        )

    suffix_matches = [
        obj
        for obj in mesh_descendants
        if obj.name.endswith(".body") or str(getattr(obj.data, "name", "")).endswith(".body")
    ]
    if len(suffix_matches) == 1:
        return suffix_matches[0]

    if not suffix_matches:
        descendant_names = ", ".join(sorted(obj.name for obj in mesh_descendants)) or "none"
        raise ScriptError(
            f'Collider generation could not find body mesh "{expected_name}" or a descendant '
            f'mesh ending with ".body" under armature "{armature.name}". Mesh descendants: '
            f"{descendant_names}."
        )

    names = ", ".join(sorted(obj.name for obj in suffix_matches))
    raise ScriptError(
        f'Collider generation found multiple body mesh candidates ending with ".body" under '
        f'armature "{armature.name}" and none was exactly "{expected_name}": {names}.'
    )


def detect_character_collider_profile(armature: bpy.types.Object) -> str:
    """Return the collider body profile detected from generated armature bones."""

    bones = armature.data.bones
    has_breast_bone = bones.get("breast_l") is not None or bones.get("breast_r") is not None
    return "female" if has_breast_bone else "male"


def generate_character_colliders(
    armature: bpy.types.Object,
    body: bpy.types.Object,
    output_path: Path,
) -> Path:
    """Generate and save the collider blend beside the generated character blend."""

    collider_output_path = output_path.with_name(f"{output_path.stem}.colliders.blend")
    args = SimpleNamespace(
        source=str(output_path),
        output=str(collider_output_path),
        armature=armature.name,
        body=body.name,
        apply_decimate=False,
    )
    generate_body_colliders.generate(cast(Any, args))
    return collider_output_path


def require_scene_object(name: str, expected_type: str, scene: bpy.types.Scene) -> bpy.types.Object:
    """Return a named object from the active scene after reopening a saved blend."""

    obj = bpy.data.objects.get(name)
    if obj is None:
        raise ScriptError(f'Required object "{name}" was not found after reopening generated blend.')
    if obj.type != expected_type:
        raise ScriptError(
            f'Required object "{name}" is type "{obj.type}" after reopening generated blend, '
            f'expected "{expected_type}".'
        )
    if scene.objects.get(obj.name) != obj:
        raise ScriptError(f'Required object "{name}" is not linked to scene "{scene.name}".')

    return obj


def reopen_generated_character_for_animation(
    output_path: Path,
    armature_name: str,
    character_name: str,
) -> tuple[bpy.types.Scene, bpy.types.Object, bpy.types.Object]:
    """Reopen the pre-animation character blend and reacquire animation targets."""

    bpy.ops.wm.open_mainfile(filepath=str(output_path))
    scene = get_scene()
    armature = require_scene_object(armature_name, "ARMATURE", scene)
    body = find_generated_body_mesh(armature, character_name)
    return scene, armature, body


def delete_objects(objects: set[bpy.types.Object]) -> None:
    for obj in list(objects):
        if obj.name in bpy.data.objects and bpy.data.objects[obj.name] == obj:
            bpy.data.objects.remove(obj, do_unlink=True)


def purge_orphans() -> None:
    orphans_purge = get_orphans_purge_operator()
    result = orphans_purge()
    if "CANCELLED" in result:
        raise ScriptError(f"Blender failed to purge orphaned data-blocks; operator returned {result}.")


def normalised_export_name(name: str) -> str:
    """Remove MPFB export-copy and Blender duplicate suffixes from the end of a name."""

    normalised = name
    while True:
        without_duplicate_suffix = re.sub(r"\.\d{3,}$", "", normalised)
        if without_duplicate_suffix != normalised:
            normalised = without_duplicate_suffix
            continue

        without_export_suffix = re.sub(r"_export_copy$", "", normalised)
        if without_export_suffix != normalised:
            normalised = without_export_suffix
            continue

        return normalised or name


def normalise_exported_object_names(objects: set[bpy.types.Object]) -> None:
    for obj in sorted(objects, key=lambda item: item.name):
        obj.name = normalised_export_name(obj.name)

        data = getattr(obj, "data", None)
        if data is not None:
            data.name = normalised_export_name(data.name)


def prefixed_character_name(name: str, old_prefix: str, new_prefix: str) -> str:
    if name == old_prefix:
        return new_prefix
    if name.startswith(old_prefix):
        return f"{new_prefix}{name[len(old_prefix):]}"
    return name


def existing_datablock_named_like(datablock: object, name: str) -> object | None:
    for collection_name in (
        "armatures",
        "meshes",
        "curves",
        "cameras",
        "lights",
        "materials",
        "images",
    ):
        collection = getattr(bpy.data, collection_name, None)
        get_item = getattr(collection, "get", None)
        if callable(get_item):
            existing = get_item(name)
            if existing is not None and type(existing) is type(datablock):
                return existing

    return None


def rename_datablock_or_fail(datablock: object, new_name: str, label: str) -> None:
    current_name = getattr(datablock, "name", None)
    if not isinstance(current_name, str) or current_name == new_name:
        return

    existing = existing_datablock_named_like(datablock, new_name)
    if existing is not None and existing != datablock:
        raise ScriptError(
            f'Character prefix rename failed because {label} "{current_name}" would collide '
            f'with existing {label} "{new_name}".'
        )

    setattr(datablock, "name", new_name)
    actual_name = getattr(datablock, "name", None)
    if actual_name != new_name:
        raise ScriptError(
            f'Character prefix rename failed because Blender renamed {label} "{current_name}" '
            f'to "{actual_name}" instead of requested name "{new_name}".'
        )


def rename_exported_character_prefix(
    objects: set[bpy.types.Object], makehuman_preset: str, character_name: str
) -> None:
    """Rename exported preset-prefixed objects/data to the configured character name."""

    if makehuman_preset == character_name:
        return

    for obj in sorted(objects, key=lambda item: item.name):
        new_object_name = prefixed_character_name(obj.name, makehuman_preset, character_name)
        rename_datablock_or_fail(obj, new_object_name, "object")

        data = getattr(obj, "data", None)
        data_name = getattr(data, "name", None)
        if data is not None and isinstance(data_name, str):
            new_data_name = prefixed_character_name(data_name, makehuman_preset, character_name)
            rename_datablock_or_fail(data, new_data_name, "object data")


def select_exported_animation_owner(
    scene: bpy.types.Scene, exported_objects: set[bpy.types.Object], preset: str
) -> bpy.types.Object:
    """Return the single exported armature that should own persistent action references."""

    scene_exported_armatures = [
        obj
        for obj in exported_objects
        if obj.type == "ARMATURE" and scene.objects.get(obj.name) == obj
    ]
    preset_armature = bpy.data.objects.get(preset)

    if preset_armature is not None:
        if preset_armature.type != "ARMATURE":
            raise ScriptError(
                f'Normalised object named "{preset}" exists but is type '
                f'"{preset_armature.type}", not "ARMATURE".'
            )

        if scene.objects.get(preset_armature.name) != preset_armature:
            raise ScriptError(f'Normalised armature named "{preset}" is not linked to scene "Scene".')

        if preset_armature not in scene_exported_armatures:
            raise ScriptError(
                f'Normalised armature named "{preset}" was not part of the exported object set.'
            )

        return preset_armature

    if len(scene_exported_armatures) != 1:
        armature_names = ", ".join(sorted(obj.name for obj in scene_exported_armatures)) or "none"
        raise ScriptError(
            "Animation action linking failed because exactly one exported armature is required "
            f"to store linked action references, but found {len(scene_exported_armatures)}: "
            f"{armature_names}."
        )

    return scene_exported_armatures[0]


def externalise_textures() -> None:
    """Pack and locally unpack external files so saved blends reference local textures."""

    pack_all = get_file_pack_all_operator()
    result = pack_all()
    if "CANCELLED" in result:
        raise ScriptError(f"Blender failed to pack external files; operator returned {result}.")

    unpack_all = get_file_unpack_all_operator()
    result = unpack_all(method="USE_LOCAL")
    if "CANCELLED" in result:
        raise ScriptError(
            f"Blender failed to locally unpack external files; operator returned {result}."
        )


def resolve_retarget_preset_path(preset_name: str) -> Path:
    """Return the active user-resource path for a retarget humanoid preset file."""

    presets_dir = bpy.utils.user_resource(
        "SCRIPTS",
        path=os.path.join("presets", "retarget", "humanoid"),
        create=False,
    )
    if not presets_dir:
        raise ScriptError(
            "Retarget preset setup failed because Blender could not resolve the user resource "
            "directory for presets/retarget/humanoid."
        )

    preset_path = Path(presets_dir) / preset_name
    if not preset_path.is_file():
        raise ScriptError(
            "Retarget preset setup failed because the preset file was not found at "
            f'"{preset_path}". Ensure the Retarget extension presets are installed for this '
            "Blender runtime."
        )

    return preset_path


def select_single_armature_for_retarget(target: bpy.types.Object, failure_context: str) -> None:
    """Select exactly one armature object and make it active for retarget preset application."""

    if target.type != "ARMATURE":
        raise ScriptError(
            f'{failure_context} failed because target object "{target.name}" is type '
            f'"{target.type}", not "ARMATURE".'
        )

    if target.name not in bpy.context.view_layer.objects:
        raise ScriptError(
            f'{failure_context} failed because target armature "{target.name}" is not in the '
            "active view layer."
        )

    try:
        bpy.context.view_layer.objects.active = target
        if bpy.context.mode != "OBJECT":
            result = bpy.ops.object.mode_set(mode="OBJECT")
            if "CANCELLED" in result and bpy.context.mode != "OBJECT":
                raise RuntimeError(f"mode_set returned {result}")
    except Exception as exc:
        if bpy.context.mode != "OBJECT":
            raise ScriptError(
                f'{failure_context} failed because Blender could not switch armature '
                f'"{target.name}" to Object mode before applying the preset: {exc}'
            ) from exc

    try:
        for obj in bpy.context.view_layer.objects:
            obj.select_set(False)

        target.select_set(True)
        bpy.context.view_layer.objects.active = target
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because Blender could not make armature "{target.name}" '
            f"the only selected object: {exc}"
        ) from exc

    selected_armatures = [obj for obj in bpy.context.selected_objects if obj.type == "ARMATURE"]
    if selected_armatures != [target] or bpy.context.view_layer.objects.active != target:
        selected_names = ", ".join(obj.name for obj in selected_armatures) or "none"
        raise ScriptError(
            f'{failure_context} failed because exactly one target armature must be selected. '
            f'Expected "{target.name}" only; selected armatures: {selected_names}.'
        )


def validate_retarget_preset_application(target: bpy.types.Object, failure_context: str) -> None:
    """Validate that the retarget extension property tree exists and is populated."""

    if target.type != "ARMATURE":
        raise ScriptError(
            f'{failure_context} failed because target object "{target.name}" is type '
            f'"{target.type}", not "ARMATURE" after preset application.'
        )

    settings = getattr(target.data, "retarget_retarget", None)
    if settings is None:
        raise ScriptError(
            f'{failure_context} failed because armature "{target.name}" does not expose '
            "data.retarget_retarget. Ensure the Retarget extension is installed and enabled."
        )

    has_settings = getattr(settings, "has_settings", None)
    if callable(has_settings) and not has_settings():
        raise ScriptError(
            f'{failure_context} failed because preset application left armature '
            f'"{target.name}" with an empty retarget_retarget settings tree.'
        )


def apply_retarget_preset(target: bpy.types.Object, preset_name: str, failure_context: str) -> None:
    """Apply a retarget extension preset to one selected armature."""

    preset_path = resolve_retarget_preset_path(preset_name)
    select_single_armature_for_retarget(target, failure_context)
    apply_preset = get_retarget_preset_apply_operator()

    try:
        result = apply_preset(
            filepath=str(preset_path),
            menu_idname="VIEW3D_MT_retarget_presets",
        )
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because Blender could not apply retarget preset '
            f'"{preset_name}" from "{preset_path}" to armature "{target.name}": {exc}'
        ) from exc

    if "CANCELLED" in result:
        raise ScriptError(
            f'{failure_context} failed because retarget preset operator returned {result} for '
            f'preset "{preset_name}" on armature "{target.name}".'
        )

    validate_retarget_preset_application(target, failure_context)


def ensure_object_mode_for_retarget_bind(active_armature: bpy.types.Object) -> None:
    """Ensure Blender is in Object mode with the requested active armature."""

    if active_armature.name not in bpy.context.view_layer.objects:
        raise ScriptError(
            f'Retarget bind failed because active armature "{active_armature.name}" is not in '
            "the active view layer."
        )

    try:
        bpy.context.view_layer.objects.active = active_armature
        if bpy.context.mode != "OBJECT":
            result = bpy.ops.object.mode_set(mode="OBJECT")
            if "CANCELLED" in result and bpy.context.mode != "OBJECT":
                raise RuntimeError(f"mode_set returned {result}")
    except Exception as exc:
        raise ScriptError(
            "Retarget bind failed because Blender could not switch to Object mode before "
            f'binding with active armature "{active_armature.name}": {exc}'
        ) from exc

    if bpy.context.mode != "OBJECT":
        raise ScriptError(
            "Retarget bind failed because Blender is not in Object mode after preparation; "
            f'current mode is "{bpy.context.mode}".'
        )


def select_armatures_for_retarget_bind(
    generated_armature: bpy.types.Object,
    rigify_armature: bpy.types.Object,
) -> None:
    """Mirror the panel bind selection with generated source selected and Rigify active."""

    ensure_object_mode_for_retarget_bind(rigify_armature)

    try:
        for obj in bpy.context.view_layer.objects:
            obj.select_set(False)

        generated_armature.select_set(True)
        rigify_armature.select_set(True)
        bpy.context.view_layer.objects.active = rigify_armature
    except Exception as exc:
        raise ScriptError(
            "Retarget bind failed because Blender could not select exactly the generated and "
            f'Rigify armatures before binding: {exc}'
        ) from exc

    selected_armatures = [obj for obj in bpy.context.selected_objects if obj.type == "ARMATURE"]
    selected_set = set(selected_armatures)
    expected_set = {generated_armature, rigify_armature}
    if selected_set != expected_set or bpy.context.view_layer.objects.active != rigify_armature:
        selected_names = ", ".join(sorted(obj.name for obj in selected_armatures)) or "none"
        raise ScriptError(
            "Retarget bind failed because exactly the generated armature and Rigify armature "
            "must be selected, with Rigify active. "
            f'Expected selected: "{generated_armature.name}", "{rigify_armature.name}"; '
            f'active: "{rigify_armature.name}". Selected armatures: {selected_names}; '
            f'active object: "{getattr(bpy.context.view_layer.objects.active, "name", "none")}".'
        )


def has_retarget_bones_collection(armature: bpy.types.Object) -> bool:
    """Return whether the armature data has a Retarget Bones collection."""

    collections = getattr(armature.data, "collections", None)
    if collections is None:
        return False

    try:
        return collections.get("Retarget Bones") is not None
    except AttributeError:
        return any(getattr(collection, "name", None) == "Retarget Bones" for collection in collections)


def validate_retarget_bind_result(
    generated_armature: bpy.types.Object,
    rigify_armature: bpy.types.Object,
) -> None:
    """Validate that binding added structural Retarget constraints/bones."""

    matching_constraints: list[str] = []
    pose = generated_armature.pose
    if pose is not None:
        for pose_bone in pose.bones:
            for constraint in pose_bone.constraints:
                if (
                    getattr(constraint, "target", None) == rigify_armature
                    and isinstance(getattr(constraint, "subtarget", None), str)
                    and constraint.subtarget.endswith("_RET")
                ):
                    matching_constraints.append(f"{pose_bone.name}:{constraint.name}")

    rigify_has_retarget_collection = has_retarget_bones_collection(rigify_armature)
    if matching_constraints:
        return

    collection_hint = (
        f' Rigify armature "{rigify_armature.name}" has a "Retarget Bones" collection.'
        if rigify_has_retarget_collection
        else f' Rigify armature "{rigify_armature.name}" does not have a "Retarget Bones" collection.'
    )
    raise ScriptError(
        "Retarget bind failed because the generated armature did not receive any pose bone "
        f'constraints targeting Rigify armature "{rigify_armature.name}" with _RET subtargets.'
        f"{collection_hint}"
    )


def bind_generated_armature_to_rigify(
    scene: bpy.types.Scene,
    generated_armature: bpy.types.Object,
    rigify_armature: bpy.types.Object,
) -> None:
    """Bind the generated MPFB armature to the appended Rigify armature via Retarget."""

    failure_context = "Retarget bind"
    if generated_armature.type != "ARMATURE":
        raise ScriptError(
            f'{failure_context} failed because generated object "{generated_armature.name}" is '
            f'type "{generated_armature.type}", not "ARMATURE".'
        )
    if rigify_armature.type != "ARMATURE":
        raise ScriptError(
            f'{failure_context} failed because Rigify object "{rigify_armature.name}" is type '
            f'"{rigify_armature.type}", not "ARMATURE".'
        )

    validate_retarget_preset_application(generated_armature, failure_context)
    validate_retarget_preset_application(rigify_armature, failure_context)
    if not hasattr(scene, "retarget_bind_to"):
        raise ScriptError(
            f'{failure_context} failed because scene "Scene" does not expose retarget_bind_to. '
            "Ensure the Retarget extension is installed and enabled."
        )

    try:
        scene.retarget_bind_to = rigify_armature
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because scene "Scene" could not store '
            f'retarget_bind_to="{rigify_armature.name}": {exc}'
        ) from exc

    if getattr(scene, "retarget_bind_to", None) != rigify_armature:
        raise ScriptError(
            f'{failure_context} failed because scene "Scene" did not retain '
            f'retarget_bind_to="{rigify_armature.name}".'
        )

    select_armatures_for_retarget_bind(generated_armature, rigify_armature)
    constrain_to_armature = get_retarget_constrain_to_armature_operator()
    previous_mode_state = prime_retarget_constrain_operator_mode_state(failure_context)

    try:
        result = constrain_to_armature(
            "INVOKE_DEFAULT",
            force_dialog=False,
            src_preset="--Current--",
            trg_preset="--Current--",
            play=False,
            action_range=False,
            custom_Frame=scene.frame_current,
        )
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because the Retarget constrain operator could not bind '
            f'generated armature "{generated_armature.name}" to Rigify armature '
            f'"{rigify_armature.name}" without the dialog: {exc}'
        ) from exc
    finally:
        restore_retarget_constrain_operator_mode_state(previous_mode_state)

    if "CANCELLED" in result:
        raise ScriptError(
            f'{failure_context} failed because the Retarget constrain operator returned {result} '
            f'for generated armature "{generated_armature.name}" and Rigify armature '
            f'"{rigify_armature.name}".'
        )

    validate_retarget_bind_result(generated_armature, rigify_armature)


def blender_relative_path(path: Path, blend_path: Path) -> str:
    """Return a Blender-relative file path from the target .blend location."""

    relative_path = Path(os.path.relpath(path, blend_path.parent))
    return f"//{relative_path.as_posix()}"


def closest_game_directory(path: Path) -> Path:
    """Return the nearest enclosing directory named "game" for a target path."""

    directory = path if path.is_dir() else path.parent
    for ancestor in [directory, *directory.parents]:
        if ancestor.name == "game":
            return ancestor

    raise ScriptError(
        "World environment setup failed because the target directory is not under a "
        f'"game" directory: "{directory}". Character targets must be inside a game directory.'
    )


def setup_world_environment(scene: bpy.types.Scene, output_path: Path) -> None:
    """Configure the scene world to use the project HDRI as an environment texture."""

    game_dir = closest_game_directory(output_path.parent)
    hdri_path = game_dir / "assets" / "hdri" / WORLD_ENVIRONMENT_TEXTURE_NAME
    if not hdri_path.is_file():
        raise ScriptError(
            "World environment setup failed because the HDRI file was not found at "
            f'"{hdri_path}".'
        )

    image_relative_path = blender_relative_path(hdri_path, output_path)

    try:
        image = bpy.data.images.load(str(hdri_path), check_existing=True)
    except RuntimeError as exc:
        raise ScriptError(
            "World environment setup failed because Blender could not load HDRI "
            f'"{hdri_path}": {exc}'
        ) from exc

    image.filepath = image_relative_path

    world = scene.world
    if world is None:
        world = bpy.data.worlds.new("World")
        scene.world = world

    try:
        world.use_nodes = True
        node_tree = world.node_tree
        if node_tree is None:
            raise RuntimeError("world node tree was not created")

        node_tree.nodes.clear()

        environment = node_tree.nodes.new(type="ShaderNodeTexEnvironment")
        environment.name = "HDRI Environment Texture"
        environment.label = "HDRI Environment Texture"
        environment.image = image

        background = node_tree.nodes.new(type="ShaderNodeBackground")
        output = node_tree.nodes.new(type="ShaderNodeOutputWorld")

        node_tree.links.new(environment.outputs["Color"], background.inputs["Color"])
        node_tree.links.new(background.outputs["Background"], output.inputs["Surface"])
    except Exception as exc:
        raise ScriptError(f"World environment node setup failed: {exc}") from exc


def action_name(item: object) -> str:
    """Return a stable Blender action identifier for library loading and messages."""

    name = getattr(item, "name", None)
    return name if isinstance(name, str) else str(item)


def rigify_animation_object_name(character_name: str) -> str:
    """Return the configured character's expected Rigify source object name."""

    return f"{character_name}{RIGIFY_ANIMATION_OBJECT_SUFFIX}"


def select_rigify_animation_object_name(object_names: list[str], character_name: str) -> str | None:
    """Return the Rigify source object name from a source blend object-name list."""

    expected_object_name = rigify_animation_object_name(character_name)
    if expected_object_name in object_names:
        return expected_object_name

    rigify_object_names = sorted(
        name for name in object_names if name.endswith(RIGIFY_ANIMATION_OBJECT_SUFFIX)
    )
    if not rigify_object_names:
        return None
    if len(rigify_object_names) > 1:
        object_list = ", ".join(rigify_object_names)
        raise ScriptError(
            "Rigify animation source detection failed because the source Blender file contains "
            "multiple Rigify armature candidates and none match the configured character name "
            f'"{expected_object_name}": {object_list}.'
        )

    return rigify_object_names[0]


def nla_reference_name(action: bpy.types.Action) -> str:
    """Return a deterministic NLA track and strip name for a persisted action reference."""

    return f"LinkedActionReference::{action.name}"


def create_persistent_action_users(
    actions: list[bpy.types.Action],
    animation_owner: bpy.types.Object,
    failure_context: str = "Animation action persistence",
    action_label: str = "actions",
) -> None:
    """Create muted NLA strips so actions have real saved users."""

    if animation_owner.type != "ARMATURE":
        raise ScriptError(
            f"{failure_context} failed because the action reference owner must be an "
            f'armature, but "{animation_owner.name}" is type "{animation_owner.type}".'
        )

    try:
        animation_data = animation_owner.animation_data_create()
        if animation_data is None:
            raise RuntimeError("animation_data_create() returned None")

        for action in sorted(actions, key=lambda item: item.name):
            reference_name = nla_reference_name(action)

            for existing_track in list(animation_data.nla_tracks):
                if existing_track.name == reference_name:
                    animation_data.nla_tracks.remove(existing_track)

            track = animation_data.nla_tracks.new()
            track.name = reference_name

            strip_start = int(action.frame_range[0])
            strip = track.strips.new(reference_name, strip_start, action)
            strip.name = reference_name
            strip.mute = True
            track.mute = True
            track.lock = True
    except Exception as exc:
        raise ScriptError(
            f"{failure_context} failed because Blender could not create persistent NLA "
            f'action references on armature "{animation_owner.name}": {exc}'
        ) from exc

    unreferenced_actions = [action.name for action in actions if action.users <= 0]
    if unreferenced_actions:
        action_list = ", ".join(unreferenced_actions)
        raise ScriptError(
            f"{failure_context} failed because {action_label} still have no real users "
            f"after NLA reference creation: {action_list}."
        )

    validate_action_nla_references(actions, animation_owner, failure_context, action_label)


def validate_action_nla_references(
    actions: list[bpy.types.Action],
    animation_owner: bpy.types.Object,
    failure_context: str,
    action_label: str,
) -> None:
    """Validate that actions have deterministic muted/locked NLA strip users."""

    animation_data = getattr(animation_owner, "animation_data", None)
    if animation_data is None:
        action_list = ", ".join(sorted(action.name for action in actions)) or "none"
        raise ScriptError(
            f"{failure_context} failed because armature \"{animation_owner.name}\" has no "
            f"animation data to persist {action_label}: {action_list}."
        )

    missing_references: list[str] = []
    for action in actions:
        if (
            action.name not in bpy.data.actions
            or bpy.data.actions[action.name] != action
            or action.users <= 0
        ):
            missing_references.append(action.name)
            continue

        reference_name = nla_reference_name(action)
        matching_tracks = [
            track
            for track in animation_data.nla_tracks
            if track.name == reference_name
            and track.mute
            and track.lock
            and any(
                strip.name == reference_name and strip.mute and strip.action == action
                for strip in track.strips
            )
        ]
        if not matching_tracks:
            missing_references.append(action.name)

    if missing_references:
        action_list = ", ".join(sorted(missing_references))
        raise ScriptError(
            f"{failure_context} failed because {action_label} lack deterministic muted/locked "
            f"NLA references on armature \"{animation_owner.name}\": {action_list}."
        )


def animation_source_contains_rigify_object(source_blend_path: Path, character_name: str) -> bool:
    """Inspect an animation source without loading data to detect Rigify retarget input."""

    if not source_blend_path.is_file():
        raise ScriptError(
            "Animation source detection failed because the source Blender file was not found at "
            f'"{source_blend_path}".'
        )

    try:
        with bpy.data.libraries.load(
            str(source_blend_path), link=False, relative=False
        ) as (data_from, _data_to):
            object_names = [action_name(obj) for obj in data_from.objects]
            return select_rigify_animation_object_name(object_names, character_name) is not None
    except Exception as exc:
        raise ScriptError(
            "Animation source detection failed because Blender could not inspect source "
            f'"{source_blend_path}": {exc}'
        ) from exc


def append_rigify_animation_object(
    scene: bpy.types.Scene, source_blend_path: Path, character_name: str
) -> tuple[bpy.types.Object, set[bpy.types.Object]]:
    """Append the source Rigify object locally and return every new appended object."""

    if not source_blend_path.is_file():
        raise ScriptError(
            "Rigify animation source setup failed because the source Blender file was not found at "
            f'"{source_blend_path}".'
        )

    expected_object_name = rigify_animation_object_name(character_name)

    objects_before_append = set(bpy.data.objects)
    appended_objects: list[bpy.types.Object] = []

    try:
        with bpy.data.libraries.load(
            str(source_blend_path), link=False, relative=False
        ) as (data_from, data_to):
            object_names = [action_name(obj) for obj in data_from.objects]
            rigify_object_name = select_rigify_animation_object_name(object_names, character_name)
            if rigify_object_name is None:
                raise ScriptError(
                    "Rigify animation source setup failed because the source Blender file does "
                    f'not contain object "{expected_object_name}" or any unique '
                    f'"*{RIGIFY_ANIMATION_OBJECT_SUFFIX}" object: '
                    f'"{source_blend_path}".'
                )

            data_to.objects = [rigify_object_name]

        appended_objects = [
            cast(bpy.types.Object, obj) for obj in data_to.objects if obj is not None
        ]
    except ScriptError:
        raise
    except Exception as exc:
        raise ScriptError(
            "Rigify animation source setup failed because Blender could not append object "
            f'"{expected_object_name}" from "{source_blend_path}": {exc}'
        ) from exc

    new_objects = set(bpy.data.objects) - objects_before_append
    if len(appended_objects) != 1 or appended_objects[0] not in new_objects:
        object_list = ", ".join(obj.name for obj in appended_objects) or "none"
        raise ScriptError(
            "Rigify animation source setup failed because Blender did not return exactly one "
            f'appended object for "{expected_object_name}" from "{source_blend_path}". '
            f"Returned: {object_list}."
        )

    appended_object = appended_objects[0]
    if appended_object.library is not None or appended_object.override_library is not None:
        raise ScriptError(
            "Rigify animation source setup failed because the appended object is not local: "
            f'"{appended_object.name}" from "{source_blend_path}".'
        )

    try:
        if scene.objects.get(appended_object.name) != appended_object:
            scene.collection.objects.link(appended_object)

        for obj in scene.objects:
            obj.select_set(False)

        appended_object.select_set(True)
        bpy.context.view_layer.objects.active = appended_object
    except Exception as exc:
        raise ScriptError(
            "Rigify animation source setup failed because Blender could not link and select "
            f'appended local object "{expected_object_name}" from "{source_blend_path}": '
            f"{exc}"
        ) from exc

    if appended_object.type != "ARMATURE":
        raise ScriptError(
            "Rigify animation source setup failed because the appended animation owner must be "
            f'an armature, but "{appended_object.name}" is type "{appended_object.type}".'
        )

    return appended_object, new_objects


def append_animation_actions(
    animation_owner: bpy.types.Object,
    source_blend_path: Path,
    failure_context: str = "Animation action append",
) -> list[bpy.types.Action]:
    """Append every action from a reference animations blend into the generated blend."""

    if not source_blend_path.is_file():
        raise ScriptError(
            f"{failure_context} failed because the source Blender file was not found at "
            f'"{source_blend_path}".'
        )

    requested_action_names: list[str] = []
    appended_actions: list[bpy.types.Action] = []

    try:
        with bpy.data.libraries.load(
            str(source_blend_path), link=False, relative=False
        ) as (data_from, data_to):
            action_names = [action_name(action) for action in data_from.actions]
            if not action_names:
                raise ScriptError(
                    f"{failure_context} failed because the source Blender file contains "
                    f'no actions: "{source_blend_path}".'
                )

            requested_action_names = list(action_names)
            data_to.actions = list(action_names)

        appended_actions = [
            cast(bpy.types.Action, action) for action in data_to.actions if action is not None
        ]
    except ScriptError:
        raise
    except Exception as exc:
        raise ScriptError(
            f"{failure_context} failed because Blender could not append actions from "
            f'"{source_blend_path}": {exc}'
        ) from exc

    if not appended_actions:
        raise ScriptError(
            f"{failure_context} failed because no local actions were appended from "
            f'"{source_blend_path}".'
        )

    if len(appended_actions) != len(requested_action_names):
        appended_list = ", ".join(action.name for action in appended_actions) or "none"
        requested_list = ", ".join(requested_action_names) or "none"
        raise ScriptError(
            f"{failure_context} failed because only a partial set of actions was appended "
            f'from "{source_blend_path}". Requested: {requested_list}. Appended: '
            f"{appended_list}."
        )

    linked_actions = [action.name for action in appended_actions if action.library is not None]
    if linked_actions:
        action_list = ", ".join(linked_actions)
        raise ScriptError(
            f"{failure_context} failed because appended actions were not local: {action_list}."
        )

    for action in appended_actions:
        action.use_fake_user = True

    non_persistable_actions = [action.name for action in appended_actions if not action.use_fake_user]
    if non_persistable_actions:
        action_list = ", ".join(non_persistable_actions)
        raise ScriptError(
            f"{failure_context} failed because appended actions could not be marked for "
            f"persistence before saving: {action_list}."
        )

    create_persistent_action_users(
        appended_actions,
        animation_owner,
        failure_context,
        "appended MPFB actions",
    )

    return appended_actions


def append_rigify_animation_actions(
    source_blend_path: Path,
    animation_owner: bpy.types.Object,
    failure_context: str = "Rigify animation action append",
) -> list[bpy.types.Action]:
    """Acquire every Rigify action locally and persist it on the Rigify armature.

    Appending the Rigify armature also appends the NLA-referenced source actions from
    the source file. Reuse those exact local actions when present so a subsequent
    action acquisition pass does not create Blender duplicate names such as
    ``Idle-loop.001``.
    """

    if not source_blend_path.is_file():
        raise ScriptError(
            f"{failure_context} failed because the source Blender file was not found at "
            f'"{source_blend_path}".'
        )

    requested_action_names: list[str] = []
    acquired_actions: list[bpy.types.Action] = []
    appended_actions: list[bpy.types.Action] = []

    try:
        with bpy.data.libraries.load(
            str(source_blend_path), link=False, relative=False
        ) as (data_from, data_to):
            action_names = [action_name(action) for action in data_from.actions]
            if not action_names:
                raise ScriptError(
                    f"{failure_context} failed because the source Blender file contains "
                    f'no Rigify actions to append: "{source_blend_path}".'
                )

            requested_action_names = list(action_names)
            missing_action_names = [
                name for name in requested_action_names if bpy.data.actions.get(name) is None
            ]
            data_to.actions = list(missing_action_names)

        appended_actions = [
            cast(bpy.types.Action, action) for action in data_to.actions if action is not None
        ]
    except ScriptError:
        raise
    except Exception as exc:
        raise ScriptError(
            f"{failure_context} failed because Blender could not append Rigify actions from "
            f'"{source_blend_path}": {exc}'
        ) from exc

    actions_by_name = {action.name: action for action in appended_actions}
    for action_name_to_acquire in requested_action_names:
        action = bpy.data.actions.get(action_name_to_acquire)
        if action is None:
            action = actions_by_name.get(action_name_to_acquire)
        if action is None:
            continue
        acquired_actions.append(cast(bpy.types.Action, action))

    if not acquired_actions:
        raise ScriptError(
            f"{failure_context} failed because no local Rigify actions were acquired from "
            f'"{source_blend_path}".'
        )

    if len(acquired_actions) != len(requested_action_names):
        acquired_list = ", ".join(action.name for action in acquired_actions) or "none"
        requested_list = ", ".join(requested_action_names) or "none"
        raise ScriptError(
            f"{failure_context} failed because only a partial set of Rigify actions was acquired "
            f'from "{source_blend_path}". Requested: {requested_list}. Acquired: '
            f"{acquired_list}."
        )

    renamed_actions = [
        f"{requested}:{actual.name}"
        for requested, actual in zip(requested_action_names, acquired_actions, strict=True)
        if actual.name != requested
    ]
    if renamed_actions:
        action_list = ", ".join(renamed_actions)
        raise ScriptError(
            f"{failure_context} failed because acquired Rigify actions must keep source names; "
            f"found renamed actions: {action_list}."
        )

    linked_actions = [action.name for action in acquired_actions if action.library is not None]
    if linked_actions:
        action_list = ", ".join(linked_actions)
        raise ScriptError(
            f"{failure_context} failed because acquired Rigify actions were not local: "
            f"{action_list}."
        )

    for action in acquired_actions:
        action.use_fake_user = True
        validate_action_multiframe_non_static(
            action,
            failure_context,
            "Rigify source action",
        )

    non_persistable_actions = [action.name for action in acquired_actions if not action.use_fake_user]
    if non_persistable_actions:
        action_list = ", ".join(non_persistable_actions)
        raise ScriptError(
            f"{failure_context} failed because acquired Rigify actions could not be marked for "
            f"persistence before saving: {action_list}."
        )

    create_persistent_action_users(
        acquired_actions,
        animation_owner,
        failure_context,
        "acquired Rigify actions",
    )

    return acquired_actions


def baked_action_name(source_action: bpy.types.Action) -> str:
    """Return the generated-rig action name for a Rigify source action."""

    target_name = source_action.name
    if source_action.name.endswith(RIGIFY_SOURCE_ACTION_SUFFIX):
        target_name = source_action.name[: -len(RIGIFY_SOURCE_ACTION_SUFFIX)]

    if not target_name:
        raise ScriptError(
            "Rigify animation bake failed because source action "
            f'"{source_action.name}" would produce an empty baked action name.'
        )

    return target_name


def transient_baked_action_name(target_name: str, source_action: bpy.types.Action) -> str:
    """Return a bake action name that cannot collide with an appended source action."""

    existing_action = bpy.data.actions.get(target_name)
    if existing_action is None:
        return target_name
    if existing_action != source_action:
        raise ScriptError(
            "Rigify animation bake failed because an action named "
            f'"{target_name}" already exists before baking source action '
            f'"{source_action.name}".'
        )

    base_name = f"{target_name}{BAKED_ACTION_COLLISION_SUFFIX}"
    candidate_name = base_name
    suffix = 1
    while bpy.data.actions.get(candidate_name) is not None:
        candidate_name = f"{base_name}.{suffix:03d}"
        suffix += 1

    return candidate_name


def select_generated_armature_for_bake(generated_armature: bpy.types.Object) -> None:
    """Select only the generated armature and make it active in Object mode for baking."""

    if generated_armature.type != "ARMATURE":
        raise ScriptError(
            "Rigify animation bake failed because generated object "
            f'"{generated_armature.name}" is type "{generated_armature.type}", not "ARMATURE".'
        )

    if generated_armature.name not in bpy.context.view_layer.objects:
        raise ScriptError(
            "Rigify animation bake failed because generated armature "
            f'"{generated_armature.name}" is not in the active view layer.'
        )

    try:
        bpy.context.view_layer.objects.active = generated_armature
        if bpy.context.mode != "OBJECT":
            result = bpy.ops.object.mode_set(mode="OBJECT")
            if "CANCELLED" in result and bpy.context.mode != "OBJECT":
                raise RuntimeError(f"mode_set returned {result}")

        for obj in bpy.context.view_layer.objects:
            obj.select_set(False)

        generated_armature.select_set(True)
        bpy.context.view_layer.objects.active = generated_armature
    except Exception as exc:
        raise ScriptError(
            "Rigify animation bake failed because Blender could not select only generated "
            f'armature "{generated_armature.name}" in Object mode: {exc}'
        ) from exc

    selected_objects = list(bpy.context.selected_objects)
    if (
        selected_objects != [generated_armature]
        or bpy.context.view_layer.objects.active != generated_armature
    ):
        selected_names = ", ".join(obj.name for obj in selected_objects) or "none"
        raise ScriptError(
            "Rigify animation bake failed because exactly the generated armature must be "
            f'selected for baking. Selected objects: {selected_names}.'
        )


def validate_armature_for_bake(armature: bpy.types.Object, label: str) -> None:
    if armature.type != "ARMATURE":
        raise ScriptError(
            f'Rigify animation bake failed because {label} object "{armature.name}" is type '
            f'"{armature.type}", not "ARMATURE".'
        )


def enable_rigify_pole_vectors(rigify_armature: bpy.types.Object) -> None:
    """Enable pole-vector controls required by the appended Rigify animation rig."""

    validate_armature_for_bake(rigify_armature, "Rigify")
    pose = rigify_armature.pose
    if pose is None:
        raise ScriptError(
            "Rigify animation bake failed because Rigify armature "
            f'"{rigify_armature.name}" has no pose data for pole-vector setup.'
        )

    for bone_name in RIGIFY_POLE_VECTOR_PARENT_BONES:
        pose_bone = pose.bones.get(bone_name)
        if pose_bone is None:
            raise ScriptError(
                "Rigify animation bake failed because Rigify armature "
                f'"{rigify_armature.name}" is missing required pole-vector pose bone '
                f'"{bone_name}".'
            )

        if "pole_vector" not in pose_bone:
            raise ScriptError(
                "Rigify animation bake pole-vector setup failed because Rigify armature "
                f'"{rigify_armature.name}" pose bone "{bone_name}" does not expose the '
                'required "pole_vector" custom property.'
            )

        pose_bone["pole_vector"] = True
        if pose_bone.get("pole_vector") is not True:
            actual_value = pose_bone.get("pole_vector")
            raise ScriptError(
                "Rigify animation bake failed because pole-vector custom property "
                f'"{bone_name}.pole_vector" on Rigify armature "{rigify_armature.name}" '
                f'was not set to True; actual value is {actual_value!r}.'
            )


def fcurves_have_keyframes(fcurves) -> bool:
    if fcurves is None:
        return False

    for fcurve in fcurves:
        keyframe_points = getattr(fcurve, "keyframe_points", None)
        if keyframe_points is not None and len(keyframe_points) > 0:
            return True

    return False


def action_has_keyed_fcurves(action: bpy.types.Action) -> bool:
    fcurves = getattr(action, "fcurves", None)
    if fcurves is not None:
        return fcurves_have_keyframes(fcurves)

    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for channelbag in getattr(strip, "channelbags", ()):
                if fcurves_have_keyframes(getattr(channelbag, "fcurves", None)):
                    return True

    return False


def action_frame_span(action: bpy.types.Action) -> float:
    frame_range = getattr(action, "frame_range", (0.0, 0.0))
    return float(frame_range[1]) - float(frame_range[0])


def action_has_varying_fcurve(action: bpy.types.Action) -> bool:
    for fcurve in iter_action_fcurves(action):
        keyframe_points = getattr(fcurve, "keyframe_points", None)
        if keyframe_points is None or len(keyframe_points) < 2:
            continue

        values = [float(point.co.y) for point in keyframe_points]
        if max(values) - min(values) > ACTION_VARIATION_THRESHOLD:
            return True

    return False


def validate_action_multiframe_non_static(
    action: bpy.types.Action,
    failure_context: str,
    action_label: str,
) -> None:
    """Reject actions that would bake/import as single-frame rest-pose clips."""

    if action_frame_span(action) < 1.0:
        raise ScriptError(
            f'{failure_context} failed because {action_label} "{action.name}" is not '
            f'multi-frame; frame range is {tuple(action.frame_range)}.'
        )
    if not action_has_keyed_fcurves(action):
        raise ScriptError(
            f'{failure_context} failed because {action_label} "{action.name}" has no '
            "f-curves/keyframes."
        )
    if not action_has_varying_fcurve(action):
        raise ScriptError(
            f'{failure_context} failed because {action_label} "{action.name}" has no '
            "varying keyframes and appears static/rest-pose-only."
        )


def iter_action_fcurves(action: bpy.types.Action):
    """Yield f-curves from legacy and Blender 5.1 layered action storage."""

    fcurves = getattr(action, "fcurves", None)
    if fcurves is not None:
        for fcurve in fcurves:
            yield fcurve

    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for channelbag in getattr(strip, "channelbags", ()):
                for fcurve in getattr(channelbag, "fcurves", ()):
                    yield fcurve


def action_clean_region(area):
    for region in getattr(area, "regions", ()):
        if getattr(region, "type", None) == "WINDOW":
            return region

    return None


def action_clean_override(
    generated_armature: bpy.types.Object,
    window=None,
    screen=None,
    area=None,
) -> dict:
    override = {
        "scene": bpy.context.scene,
        "view_layer": bpy.context.view_layer,
        "active_object": generated_armature,
        "object": generated_armature,
        "selected_objects": [generated_armature],
        "selected_editable_objects": [generated_armature],
    }

    if window is not None:
        override["window"] = window
    if screen is not None:
        override["screen"] = screen
    if area is not None:
        override["area"] = area
        region = action_clean_region(area)
        if region is not None:
            override["region"] = region
        spaces = getattr(area, "spaces", None)
        active_space = getattr(spaces, "active", None) if spaces is not None else None
        if active_space is not None:
            override["space_data"] = active_space

    return override


def action_clean_area_candidates():
    """Yield UI areas that can host Action Clean, preferring existing Dope Sheet editors."""

    seen_area_ids: set[int] = set()
    windows = list(getattr(bpy.context.window_manager, "windows", ()))
    for window in windows:
        screen = getattr(window, "screen", None)
        if screen is None:
            continue
        for area in getattr(screen, "areas", ()):
            if id(area) in seen_area_ids:
                continue
            seen_area_ids.add(id(area))
            if getattr(area, "type", None) == "DOPESHEET_EDITOR":
                yield window, screen, area

    for window in windows:
        screen = getattr(window, "screen", None)
        if screen is None:
            continue
        for area in getattr(screen, "areas", ()):
            if id(area) in seen_area_ids:
                continue
            seen_area_ids.add(id(area))
            yield window, screen, area

    screen = getattr(bpy.context, "screen", None)
    if screen is not None:
        for area in getattr(screen, "areas", ()):
            if id(area) in seen_area_ids:
                continue
            seen_area_ids.add(id(area))
            yield getattr(bpy.context, "window", None), screen, area


def prepare_action_clean_area(area) -> tuple[str | None, str | None]:
    """Temporarily switch an area to a Dope Sheet/Action context when supported."""

    previous_ui_type = getattr(area, "ui_type", None)
    spaces = getattr(area, "spaces", None)
    active_space = getattr(spaces, "active", None) if spaces is not None else None
    previous_mode = getattr(active_space, "mode", None) if active_space is not None else None

    if getattr(area, "type", None) != "DOPESHEET_EDITOR":
        try:
            area.ui_type = "DOPESHEET"
        except Exception:
            try:
                area.ui_type = "ACTION"
            except Exception:
                return previous_ui_type, previous_mode

    spaces = getattr(area, "spaces", None)
    active_space = getattr(spaces, "active", None) if spaces is not None else None
    if active_space is not None and hasattr(active_space, "mode"):
        try:
            active_space.mode = "ACTION"
        except Exception:
            pass

    return previous_ui_type, previous_mode


def restore_action_clean_area(area, previous_ui_type: str | None, previous_mode: str | None) -> None:
    spaces = getattr(area, "spaces", None)
    active_space = getattr(spaces, "active", None) if spaces is not None else None
    if active_space is not None and previous_mode is not None and hasattr(active_space, "mode"):
        try:
            active_space.mode = previous_mode
        except Exception:
            pass

    if previous_ui_type is not None:
        try:
            area.ui_type = previous_ui_type
        except Exception:
            pass


def call_action_clean_operator(action_clean, generated_armature: bpy.types.Object) -> bool:
    """Run bpy.ops.action.clean when a pollable UI context is available."""

    for window, screen, area in action_clean_area_candidates():
        previous_ui_type, previous_mode = prepare_action_clean_area(area)
        try:
            override = action_clean_override(generated_armature, window, screen, area)
            with bpy.context.temp_override(**override):
                if action_clean.poll():
                    result = action_clean(channels=False)
                    if "CANCELLED" in result:
                        raise RuntimeError(f"bpy.ops.action.clean returned {result}")
                    return True
        finally:
            restore_action_clean_area(area, previous_ui_type, previous_mode)

    with bpy.context.temp_override(**action_clean_override(generated_armature)):
        if action_clean.poll():
            result = action_clean(channels=False)
            if "CANCELLED" in result:
                raise RuntimeError(f"bpy.ops.action.clean returned {result}")
            return True

    return False


def fallback_clean_baked_action_fcurves(target_action: bpy.types.Action) -> None:
    """Background-mode equivalent for bpy.ops.action.clean(channels=False).

    Blender background sessions can expose no screen area, which prevents the Action
    Clean operator from polling even when the generated armature and baked action are
    current. This fallback applies the same contained channel cleanup intent directly
    to the current action's f-curves while preserving channels, matching
    ``channels=False``. It supports both legacy action f-curves and Blender 5.1
    layered action channel bags.
    """

    for fcurve in iter_action_fcurves(target_action):
        keyframe_points = getattr(fcurve, "keyframe_points", None)
        if keyframe_points is None or len(keyframe_points) < 3:
            continue

        indices_to_remove: list[int] = []
        for index in range(1, len(keyframe_points) - 1):
            previous_point = keyframe_points[index - 1]
            current_point = keyframe_points[index]
            next_point = keyframe_points[index + 1]
            current_value = current_point.co.y
            if (
                math.isclose(
                    previous_point.co.y,
                    current_value,
                    rel_tol=0.0,
                    abs_tol=ACTION_CLEAN_THRESHOLD,
                )
                and math.isclose(
                    next_point.co.y,
                    current_value,
                    rel_tol=0.0,
                    abs_tol=ACTION_CLEAN_THRESHOLD,
                )
            ):
                indices_to_remove.append(index)

        for index in reversed(indices_to_remove):
            keyframe_points.remove(keyframe_points[index], fast=True)

        if indices_to_remove:
            fcurve.update()


def validate_baked_action(action: bpy.types.Action, source_action: bpy.types.Action) -> None:
    if action.library is not None:
        raise ScriptError(
            "Rigify animation bake failed because baked action "
            f'"{action.name}" is linked, not local.'
        )
    if not action.use_fake_user:
        raise ScriptError(
            "Rigify animation bake failed because baked action "
            f'"{action.name}" was not marked with fake user.'
        )
    if action.name.endswith(RIGIFY_SOURCE_ACTION_SUFFIX):
        raise ScriptError(
            "Rigify animation bake failed because baked action retained the source suffix: "
            f'"{action.name}".'
        )
    validate_action_multiframe_non_static(
        action,
        "Rigify animation bake",
        f'baked action from source "{source_action.name}"',
    )


def clean_baked_action(
    generated_armature: bpy.types.Object,
    generated_animation_data: bpy.types.AnimData,
    target_action: bpy.types.Action,
    source_action: bpy.types.Action,
) -> None:
    """Clean redundant keyframes from the current generated baked action."""

    generated_animation_data.action = target_action
    select_generated_armature_for_bake(generated_armature)

    if generated_animation_data.action != target_action:
        actual_name = getattr(generated_animation_data.action, "name", "none")
        raise ScriptError(
            "Rigify animation bake failed because generated action "
            f'"{target_action.name}" from source "{source_action.name}" was not current '
            f'before cleaning; current action is "{actual_name}".'
        )

    action_clean = get_action_clean_operator()
    try:
        cleaned_with_operator = call_action_clean_operator(action_clean, generated_armature)
        if not cleaned_with_operator:
            fallback_clean_baked_action_fcurves(target_action)
    except Exception as exc:
        raise ScriptError(
            "Rigify animation bake failed because Blender could not clean baked action "
            f'"{target_action.name}" from source "{source_action.name}": {exc}'
        ) from exc

    if generated_animation_data.action != target_action:
        actual_name = getattr(generated_animation_data.action, "name", "none")
        raise ScriptError(
            "Rigify animation bake failed because cleaning did not keep generated action "
            f'"{target_action.name}" from source "{source_action.name}" current; '
            f'current action is "{actual_name}".'
        )


def validate_baked_actions_persistable(
    actions: list[bpy.types.Action], failure_context: str = "Rigify animation bake"
) -> None:
    """Validate that baked local actions are present, fake-user marked, and keyed."""

    invalid_actions: list[str] = []
    for action in actions:
        current_action = bpy.data.actions.get(action.name)
        if (
            current_action is None
            or current_action != action
            or current_action.library is not None
            or not current_action.use_fake_user
            or current_action.name.endswith(RIGIFY_SOURCE_ACTION_SUFFIX)
            or action_frame_span(current_action) < 1.0
            or not action_has_keyed_fcurves(current_action)
            or not action_has_varying_fcurve(current_action)
        ):
            invalid_actions.append(action.name)

    if invalid_actions:
        action_list = ", ".join(sorted(invalid_actions))
        raise ScriptError(
            f"{failure_context} failed because baked actions are not present as multi-frame, "
            f"non-static, keyed local fake-user actions: {action_list}."
        )


def finalise_baked_action_names(
    actions: list[bpy.types.Action], failure_context: str = "Rigify animation bake"
) -> None:
    """Rename transient baked actions to their final exported action names."""

    for action in actions:
        target_name = action.get("alleycat_baked_action_name")
        if not isinstance(target_name, str) or not target_name:
            continue
        if action.name == target_name:
            del action["alleycat_baked_action_name"]
            continue

        existing_action = bpy.data.actions.get(target_name)
        if existing_action is not None and existing_action != action:
            raise ScriptError(
                f'{failure_context} failed because baked action "{action.name}" cannot be '
                f'renamed to "{target_name}" while another action with that name remains.'
            )

        previous_name = action.name
        action.name = target_name
        if action.name != target_name:
            raise ScriptError(
                f'{failure_context} failed because Blender renamed baked action '
                f'"{previous_name}" to "{action.name}" instead of final name "{target_name}".'
            )
        del action["alleycat_baked_action_name"]


def validate_appended_actions_persistable(
    actions: list[bpy.types.Action], failure_context: str = "MPFB animation action append"
) -> None:
    """Validate that appended animation actions remain local and fake-user marked."""

    invalid_actions: list[str] = []
    for action in actions:
        current_action = bpy.data.actions.get(action.name)
        if (
            current_action is None
            or current_action != action
            or current_action.library is not None
            or not current_action.use_fake_user
        ):
            invalid_actions.append(action.name)

    if invalid_actions:
        action_list = ", ".join(sorted(invalid_actions))
        raise ScriptError(
            f"{failure_context} failed because appended actions are not present as local "
            f"fake-user actions: {action_list}."
        )


def pose_constraints_targeting(
    generated_armature: bpy.types.Object, target_armature: bpy.types.Object
) -> list[tuple[bpy.types.PoseBone, bpy.types.Constraint]]:
    pose = generated_armature.pose
    if pose is None:
        return []

    matches: list[tuple[bpy.types.PoseBone, bpy.types.Constraint]] = []
    for pose_bone in pose.bones:
        for constraint in pose_bone.constraints:
            if getattr(constraint, "target", None) == target_armature:
                matches.append((pose_bone, constraint))

    return matches


def remove_generated_retarget_constraints(
    generated_armature: bpy.types.Object, rigify_armature: bpy.types.Object
) -> None:
    """Remove generated pose constraints that target the temporary Rigify armature."""

    for pose_bone, constraint in pose_constraints_targeting(generated_armature, rigify_armature):
        pose_bone.constraints.remove(constraint)


def bake_rigify_actions_to_generated_rig(
    scene: bpy.types.Scene,
    generated_armature: bpy.types.Object,
    rigify_armature: bpy.types.Object,
    source_actions: list[bpy.types.Action],
) -> list[bpy.types.Action]:
    """Bake appended Rigify source actions to local generated-rig actions."""

    validate_armature_for_bake(generated_armature, "generated")
    validate_armature_for_bake(rigify_armature, "Rigify")

    if not source_actions:
        raise ScriptError("Rigify animation bake failed because there are no source actions to bake.")

    baked_actions: list[bpy.types.Action] = []
    generated_animation_data = generated_armature.animation_data_create()
    rigify_animation_data = rigify_armature.animation_data_create()
    if generated_animation_data is None or rigify_animation_data is None:
        raise ScriptError(
            "Rigify animation bake failed because Blender could not create animation data on "
            f'generated armature "{generated_armature.name}" or Rigify armature '
            f'"{rigify_armature.name}".'
        )

    enable_rigify_pole_vectors(rigify_armature)

    for source_action in sorted(source_actions, key=lambda action: action.name):
        if source_action.library is not None:
            raise ScriptError(
                "Rigify animation bake failed because source action "
                f'"{source_action.name}" is linked, not local.'
            )
        validate_action_multiframe_non_static(
            source_action,
            "Rigify animation bake",
            "Rigify source action",
        )

        target_name = baked_action_name(source_action)
        transient_target_name = transient_baked_action_name(target_name, source_action)

        target_action = bpy.data.actions.new(transient_target_name)
        target_action.use_fake_user = True
        target_action["alleycat_baked_action_name"] = target_name
        generated_animation_data.action = target_action
        rigify_animation_data.action = source_action

        frame_start = math.floor(source_action.frame_range[0])
        frame_end = math.ceil(source_action.frame_range[1])
        scene.frame_set(frame_start)
        select_generated_armature_for_bake(generated_armature)

        try:
            result = bpy.ops.nla.bake(
                frame_start=frame_start,
                frame_end=frame_end,
                step=1,
                only_selected=False,
                visual_keying=True,
                clear_constraints=False,
                clear_parents=False,
                use_current_action=True,
                clean_curves=False,
                bake_types={"POSE"},
                channel_types={"LOCATION", "ROTATION", "SCALE"},
            )
        except Exception as exc:
            raise ScriptError(
                "Rigify animation bake failed because Blender could not bake source action "
                f'"{source_action.name}" to generated action "{transient_target_name}": {exc}'
            ) from exc

        if "CANCELLED" in result:
            raise ScriptError(
                "Rigify animation bake failed because bpy.ops.nla.bake returned "
                f'{result} for source action "{source_action.name}".'
            )

        baked_action = generated_animation_data.action
        if baked_action != target_action:
            actual_name = getattr(baked_action, "name", "none")
            raise ScriptError(
                "Rigify animation bake failed because Blender did not keep the requested current "
                f'action "{transient_target_name}" after baking source action "{source_action.name}"; '
                f'current action is "{actual_name}".'
            )

        clean_baked_action(generated_armature, generated_animation_data, target_action, source_action)
        validate_baked_action(target_action, source_action)
        baked_actions.append(target_action)

    return baked_actions


def cleanup_rigify_animation_source(
    rigify_armature: bpy.types.Object,
    appended_objects: set[bpy.types.Object],
    source_actions: list[bpy.types.Action],
) -> None:
    """Remove temporary Rigify source objects/actions and purge orphaned data-blocks."""

    animation_data = getattr(rigify_armature, "animation_data", None)
    if animation_data is not None:
        animation_data.action = None

    delete_objects(appended_objects)

    for action in list(source_actions):
        if action.name in bpy.data.actions and bpy.data.actions[action.name] == action:
            bpy.data.actions.remove(action, do_unlink=True)

    purge_orphans()


def validate_rigify_cleanup(
    generated_armature: bpy.types.Object,
    rigify_armature: bpy.types.Object,
    rigify_armature_name: str,
    appended_object_names: set[str],
    source_action_ids: set[int],
    appended_mpfb_actions: list[bpy.types.Action],
    baked_actions: list[bpy.types.Action],
) -> None:
    remaining_objects = [
        name for name in appended_object_names if bpy.data.objects.get(name) is not None
    ]
    if remaining_objects:
        object_list = ", ".join(sorted(remaining_objects))
        raise ScriptError(
            "Rigify animation source cleanup failed because tracked Rigify objects remain: "
            f"{object_list}."
        )

    remaining_source_actions = [
        action.name
        for action in bpy.data.actions
        if id(action) in source_action_ids
    ]
    if remaining_source_actions:
        action_list = ", ".join(sorted(remaining_source_actions))
        raise ScriptError(
            "Rigify animation source cleanup failed because tracked source actions remain: "
            f"{action_list}."
        )

    remaining_noexp_actions = [
        action.name
        for action in bpy.data.actions
        if action.library is None and action.name.endswith(RIGIFY_SOURCE_ACTION_SUFFIX)
    ]
    if remaining_noexp_actions:
        action_list = ", ".join(sorted(remaining_noexp_actions))
        raise ScriptError(
            "Rigify animation source cleanup failed because local source-suffixed actions remain: "
            f"{action_list}."
        )

    if (
        generated_armature.name not in bpy.data.objects
        or bpy.data.objects[generated_armature.name] != generated_armature
    ):
        raise ScriptError(
            "Rigify animation source cleanup failed because generated armature "
            f'"{generated_armature.name}" was removed.'
        )

    validate_baked_actions_persistable(baked_actions, "Rigify animation source cleanup")
    validate_appended_actions_persistable(appended_mpfb_actions, "Rigify animation source cleanup")
    validate_action_nla_references(
        appended_mpfb_actions,
        generated_armature,
        "Rigify animation source cleanup",
        "appended MPFB actions",
    )
    validate_action_nla_references(
        baked_actions,
        generated_armature,
        "Rigify animation source cleanup",
        "baked Rigify actions",
    )

    remaining_constraints = pose_constraints_targeting(generated_armature, rigify_armature)
    if remaining_constraints:
        constraint_list = ", ".join(
            sorted(
                f"{pose_bone.name}:{constraint.name}"
                for pose_bone, constraint in remaining_constraints
            )
        )
        raise ScriptError(
            "Rigify animation source cleanup failed because generated pose constraints still "
            f'target removed Rigify armature "{rigify_armature_name}": {constraint_list}.'
        )


def validate_removed_rigify_source_actions(
    source_action_names: list[str],
    ignored_actions: list[bpy.types.Action],
    failure_context: str = "Rigify animation source cleanup",
) -> None:
    """Validate that no Rigify source action datablocks remain before final renames."""

    ignored_action_ids = {id(action) for action in ignored_actions}
    source_name_bases = {normalised_export_name(action_name) for action_name in source_action_names}
    remaining_actions = [
        action.name
        for action in bpy.data.actions
        if id(action) not in ignored_action_ids and normalised_export_name(action.name) in source_name_bases
    ]
    if remaining_actions:
        action_list = ", ".join(sorted(remaining_actions))
        raise ScriptError(
            f"{failure_context} failed because Rigify source actions remain before baked "
            f"action finalisation: {action_list}."
        )


def retarget_animation_source(
    scene: bpy.types.Scene,
    output_path: Path,
    exported_animation_owner: bpy.types.Object,
    source_blend_path: Path,
    mpfb_appended_actions: list[bpy.types.Action],
    character_name: str,
) -> list[bpy.types.Action]:
    """Append, retarget, bake, clean up, and validate one Rigify animation source."""

    rigify_animation_owner, rigify_appended_objects = append_rigify_animation_object(
        scene, source_blend_path, character_name
    )
    rigify_source_actions = append_rigify_animation_actions(
        source_blend_path,
        rigify_animation_owner,
        "Rigify animation action append",
    )
    rigify_source_action_names = [action.name for action in rigify_source_actions]
    rigify_source_action_ids = {id(action) for action in rigify_source_actions}
    apply_retarget_preset(
        exported_animation_owner,
        GENERATED_RIG_RETARGET_PRESET_NAME,
        "Generated rig retarget preset application",
    )
    apply_retarget_preset(
        rigify_animation_owner,
        RIGIFY_RETARGET_PRESET_NAME,
        "Rigify retarget preset application",
    )
    bind_generated_armature_to_rigify(scene, exported_animation_owner, rigify_animation_owner)
    baked_rigify_actions = bake_rigify_actions_to_generated_rig(
        scene,
        exported_animation_owner,
        rigify_animation_owner,
        rigify_source_actions,
    )
    rigify_animation_owner_name = rigify_animation_owner.name
    rigify_appended_object_names = {obj.name for obj in rigify_appended_objects}
    remove_generated_retarget_constraints(exported_animation_owner, rigify_animation_owner)
    cleanup_rigify_animation_source(
        rigify_animation_owner,
        rigify_appended_objects,
        rigify_source_actions,
    )
    validate_removed_rigify_source_actions(
        rigify_source_action_names,
        [*mpfb_appended_actions, *baked_rigify_actions],
    )
    finalise_baked_action_names(baked_rigify_actions)
    create_persistent_action_users(
        mpfb_appended_actions,
        exported_animation_owner,
        "MPFB animation action append",
        "appended MPFB actions",
    )
    validate_appended_actions_persistable(mpfb_appended_actions)
    create_persistent_action_users(
        baked_rigify_actions,
        exported_animation_owner,
        "Rigify animation bake",
        "baked Rigify actions",
    )
    validate_baked_actions_persistable(baked_rigify_actions)
    validate_rigify_cleanup(
        exported_animation_owner,
        rigify_animation_owner,
        rigify_animation_owner_name,
        rigify_appended_object_names,
        rigify_source_action_ids,
        mpfb_appended_actions,
        baked_rigify_actions,
    )

    return baked_rigify_actions


def generate_character(config: CharacterConfig) -> Path:
    preset = config.preset
    character_name = config.name
    output_path = config.output_file_path
    scene = get_scene()

    require_scene_properties(scene, ["MPFB_FPR_available_presets"], "preset generation")
    clear_scene_objects(scene)

    scene.MPFB_FPR_available_presets = preset

    create_human = get_mpfb_generate_operator()
    result = create_human()
    if "CANCELLED" in result:
        raise ScriptError(
            f'MPFB failed to create a human from preset "{preset}"; operator returned {result}.'
        )

    source_armature = select_generated_armature(scene, preset)
    source_hierarchy = collect_object_hierarchy(source_armature)
    objects_before_export = set(bpy.data.objects)
    objects_before_export_pointers = {obj.as_pointer() for obj in objects_before_export}

    export_property_names = [
        "MPFB_EXPO_bake_shapekeys",
        "MPFB_EXPO_delete_helpers",
        "MPFB_EXPO_faceunits_arkit",
        "MPFB_EXPO_collection",
        "MPFB_EXPO_interpolate",
        "MPFB_FAOP_visemes02",
    ]
    require_scene_properties(scene, export_property_names, "export_copy")
    scene.MPFB_EXPO_bake_shapekeys = True
    scene.MPFB_EXPO_delete_helpers = True
    scene.MPFB_EXPO_faceunits_arkit = True
    scene.MPFB_EXPO_collection = False
    scene.MPFB_EXPO_interpolate = True
    scene.MPFB_FAOP_visemes02 = True

    export_copy = get_mpfb_export_operator()
    result = export_copy()
    if "CANCELLED" in result:
        raise ScriptError(
            f'MPFB failed to export a copy for preset "{preset}"; operator returned {result}.'
        )

    exported_objects = set(bpy.data.objects) - objects_before_export - source_hierarchy
    if not exported_objects:
        raise ScriptError(f'MPFB export_copy did not create exported objects for preset "{preset}".')
    export_physical_snapshot = snapshot_export_physical_memberships(exported_objects)

    delete_objects(source_hierarchy)
    purge_orphans()
    normalise_exported_object_names(exported_objects)
    rename_exported_character_prefix(exported_objects, preset, character_name)
    exported_animation_owner = select_exported_animation_owner(
        scene, exported_objects, character_name
    )
    reparent_hands_onto_twist_helpers(exported_animation_owner)
    collider_body = find_generated_body_mesh(exported_animation_owner, character_name)
    neutral_manifest = capture_forearm_twist_neutral_manifest(
        exported_objects,
        exported_animation_owner,
    )
    axial_input = capture_forearm_twist_axial_input(
        exported_objects,
        exported_animation_owner,
    )
    physical_memberships = verify_export_physical_memberships(
        exported_objects, export_physical_snapshot, objects_before_export_pointers
    )
    authoring_eligible: dict[str, dict[str, list[int]]] = {}
    source_domain: dict[str, dict[str, list[int]]] = {}
    axial_reference = apply_forearm_twist_gradient_on_export(
        exported_objects,
        exported_animation_owner,
        axial_input,
        authoring_eligible,
        physical_memberships,
        source_domain,
    )
    generator_run_ownership = capture_forearm_twist_generator_run_ownership(
        exported_objects,
        axial_reference,
        authoring_eligible,
        physical_memberships,
        source_domain,
    )
    skipped_export_meshes = capture_skipped_export_physical_meshes(
        exported_objects, set(axial_reference), physical_memberships, export_physical_snapshot
    )
    candidate_weight_evidence = capture_forearm_twist_candidate_weight_evidence(
        exported_objects,
        neutral_manifest,
    )
    exported_animation_owner_name = exported_animation_owner.name
    collider_profile = detect_character_collider_profile(exported_animation_owner)
    print(
        f'Collider source profile detected as {collider_profile}; armature "{exported_animation_owner.name}", '
        f'body mesh "{collider_body.name}".'
    )

    output_path.parent.mkdir(parents=True, exist_ok=True)
    manifest_path = output_path.with_suffix(".forearm_twist_manifest.json")
    manifest_path.write_text(json.dumps(neutral_manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    candidate_weights_path = output_path.with_suffix(".forearm_twist_candidate_weights.json")
    candidate_weights_path.write_text(json.dumps(candidate_weight_evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(f"Captured pre-helper forearm-twist neutral manifest: {manifest_path}")

    # Blender resolves USE_LOCAL unpack paths relative to the current .blend file.
    # Save to the final target first so local texture paths land under the output directory.
    bpy.ops.wm.save_as_mainfile(filepath=str(output_path), check_existing=False)
    externalise_textures()

    # Configure the world only after texture externalisation so pack_all() does not
    # create a local copy of the shared project HDRI beside generated textures.
    setup_world_environment(scene, output_path)

    # Persist the rest/default generated character and build colliders from that
    # pre-animation source. Collider generation opens and strips the scene in the
    # current Blender process, so reopen the character blend afterwards before
    # appending direct actions or baking Rigify sources into the final output.
    bpy.ops.wm.save_as_mainfile(filepath=str(output_path), check_existing=False)
    collider_output_path = generate_character_colliders(
        exported_animation_owner,
        collider_body,
        output_path,
    )
    print(f"Generated character colliders: {collider_output_path}")
    scene, exported_animation_owner, collider_body = reopen_generated_character_for_animation(
        output_path,
        exported_animation_owner_name,
        character_name,
    )

    mpfb_appended_actions: list[bpy.types.Action] = []
    baked_rigify_actions: list[bpy.types.Action] = []

    for source_blend_path in config.animation_source_paths:
        if animation_source_contains_rigify_object(source_blend_path, character_name):
            baked_rigify_actions.extend(
                retarget_animation_source(
                    scene,
                    output_path,
                    exported_animation_owner,
                    source_blend_path,
                    mpfb_appended_actions,
                    character_name,
                )
            )
            if baked_rigify_actions:
                validate_baked_actions_persistable(baked_rigify_actions)
                validate_action_nla_references(
                    baked_rigify_actions,
                    exported_animation_owner,
                    "Rigify animation bake",
                    "baked Rigify actions",
                )
        else:
            mpfb_appended_actions.extend(
                append_animation_actions(
                    exported_animation_owner,
                    source_blend_path,
                    "MPFB animation action append",
                )
            )
            if mpfb_appended_actions:
                validate_appended_actions_persistable(mpfb_appended_actions)
                validate_action_nla_references(
                    mpfb_appended_actions,
                    exported_animation_owner,
                    "MPFB animation action append",
                    "appended MPFB actions",
                )

    bpy.ops.wm.save_as_mainfile(filepath=str(output_path), check_existing=False)
    generator_run_ownership_path = forearm_twist_generator_run_ownership.write_evidence(
        TOOLS_DIR.parent,
        output_path,
        preset,
        generator_run_ownership,
        skipped_export_meshes,
    )
    print(f"Captured generator-run authored axial reference validation: {generator_run_ownership_path}")
    for message in update_character_import_retarget_metadata.update_existing_character_sidecars(
        output_path,
        character_name,
    ):
        print(message)

    return output_path


def main(argv: list[str]) -> int:
    try:
        config_path = parse_args(argv)
        config = load_character_config(config_path)
        output_path = generate_character(config)
    except ScriptError as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 2

    print(f"Generated character: {output_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
