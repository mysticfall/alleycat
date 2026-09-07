#!/usr/bin/env python3
"""Validate ordinary MPFB-generated RIG-002 forearm-twist ownership in Blender.

This validator is read-only. It measures the generated blend directly and does
not use historical manifest or imported-vertex evidence as a delivery gate.
"""

from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import bpy

TOOLS_DIR = Path(__file__).resolve().parents[1]
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import forearm_twist_weights
import forearm_twist_generator_run_ownership


EPSILON = 1.0e-5
# Blender vertex-group weights are stored as float32; leave headroom for two
# independently rounded authored weights without masking material changes.
PHYSICAL_EPSILON = 1.0e-6
SIDES = (
    ("left", "lowerarm_l", "LeftForearmTwist", "hand_l"),
    ("right", "lowerarm_r", "RightForearmTwist", "hand_r"),
)


def parse_arguments() -> dict[str, Path]:
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    if len(arguments) % 2 != 0 or not arguments:
        raise ValueError("Usage: -- <variant> <ordinary-generator-output.blend> [...]")
    return {
        arguments[index]: Path(arguments[index + 1]).resolve()
        for index in range(0, len(arguments), 2)
    }


def vertex_weights(
    mesh: bpy.types.Object, vertex_index: int
) -> dict[str, float]:
    names_by_index = {group.index: group.name for group in mesh.vertex_groups}
    return {
        names_by_index[assignment.group]: float(assignment.weight)
        for assignment in mesh.data.vertices[vertex_index].groups
    }


def matrix_max_error(first, second) -> float:
    return max(
        abs(first[row][column] - second[row][column])
        for row in range(4)
        for column in range(4)
    )


def weighted_skinning_transform(
    armature: bpy.types.Object,
    weights: dict[str, float],
    deform_group_names: set[str],
):
    from mathutils import Matrix

    result = Matrix(((0.0, 0.0, 0.0, 0.0),) * 4)
    for name, weight in weights.items():
        if name in deform_group_names:
            bone = armature.data.bones.get(name)
            pose_bone = armature.pose.bones.get(name)
            if bone is None or pose_bone is None:
                raise AssertionError(f"{armature.name}: missing deform bone {name}")
            transform = (
                armature.matrix_world
                @ pose_bone.matrix
                @ bone.matrix_local.inverted()
                @ armature.matrix_world.inverted()
            )
            result += transform * weight
    return result


def assert_helper_topology(
    context: str,
    armature: bpy.types.Object,
    lower_arm: str,
    helper: str,
    hand: str,
) -> None:
    bones = armature.data.bones
    chain = tuple(bones.get(name) for name in (lower_arm, helper, hand))
    if any(bone is None for bone in chain):
        missing = [
            name
            for name, bone in zip((lower_arm, helper, hand), chain, strict=True)
            if bone is None
        ]
        raise AssertionError(f"{context}: missing RIG-002 chain bone(s): {', '.join(missing)}")
    lower_bone, helper_bone, hand_bone = chain
    if helper_bone.parent != lower_bone or hand_bone.parent != helper_bone:
        raise AssertionError(f"{context}: expected chain {lower_arm} -> {helper} -> {hand}")
    if not helper_bone.use_deform:
        raise AssertionError(f"{context}: RIG-002 helper must be deform-only")
    if list(helper_bone.children) != [hand_bone]:
        raise AssertionError(f"{context}: the twist helper must have exactly one child bone, the hand")


def evidence_meshes(evidence: dict[str, object], context: str) -> dict[str, dict[str, object]]:
    """Index complete contemporaneous mesh rows and reject duplicate evidence."""

    meshes = evidence.get("meshes")
    if not isinstance(meshes, list):
        raise AssertionError(f"{context}: missing mesh ownership rows")
    indexed: dict[str, dict[str, object]] = {}
    for mesh in meshes:
        if not isinstance(mesh, dict) or not isinstance(mesh.get("name"), str):
            raise AssertionError(f"{context}: malformed mesh ownership rows")
        name = mesh["name"]
        if name in indexed:
            raise AssertionError(f"{context}: duplicate mesh ownership rows for {name}")
        indexed[name] = mesh
    return indexed


def validate_skipped_mesh(variant: str, mesh: bpy.types.Object, record: dict[str, object]) -> int:
    context = f"{variant} {mesh.name}"
    groups = sorted((group.index, group.name) for group in mesh.vertex_groups)
    names = dict(groups)
    rows = {}
    for position, vertex in enumerate(mesh.data.vertices):
        if vertex.index != position:
            raise AssertionError(f"{context}: physical vertex ordering changed")
        row = {}
        for assignment in vertex.groups:
            name = names.get(assignment.group)
            if name is None or name in row:
                raise AssertionError(f"{context}: ambiguous physical group index")
            row[name] = float(assignment.weight)
        rows[position] = row
    try:
        return forearm_twist_generator_run_ownership.compare_skipped_mesh(
            record, mesh.name, groups, rows, PHYSICAL_EPSILON
        )
    except ValueError as exc:
        raise AssertionError(f"{context}: {exc}") from exc


def validate_mesh(
    variant: str,
    mesh: bpy.types.Object,
    armature: bpy.types.Object,
    deform_group_names: set[str],
    axial_mesh: dict[str, object],
) -> dict[str, object]:
    vertex_count = axial_mesh.get("vertex_count")
    axial_rows = axial_mesh.get("axial_rows")
    if vertex_count != len(mesh.data.vertices):
        raise AssertionError(f"{variant} {mesh.name}: authored axial vertex count does not match final mesh")
    final_physical_rows = {
        vertex.index: vertex_weights(mesh, vertex.index)
        for vertex in mesh.data.vertices
    }
    finger_groups = {}
    for _side_name, _lower, helper, hand in SIDES:
        hand_bone = armature.data.bones.get(hand)
        if hand_bone is None:
            raise AssertionError(f"{variant} {mesh.name}: missing hand bone {hand}")
        finger_groups[helper] = {
            bone.name for bone in armature.data.bones
            if bone.use_deform and bone != hand_bone
            and any(ancestor == hand_bone for ancestor in bone.parent_recursive)
        }
    try:
        physical_count, physical_error = forearm_twist_generator_run_ownership.compare_physical_rows(axial_rows, final_physical_rows, axial_mesh.get("authoring_eligible"), tuple(side[1:] for side in SIDES), PHYSICAL_EPSILON, f"{variant} {mesh.name}")
        zero_count = forearm_twist_generator_run_ownership.compare_zero_ledger(
            axial_mesh.get("original_zero_rows"), axial_rows,
            final_physical_rows, axial_mesh.get("authoring_eligible"),
            tuple(side[1:] for side in SIDES), f"{variant} {mesh.name}",
            axial_mesh.get("original_positive_rows"), finger_groups,
        )
    except ValueError as exc:
        raise AssertionError(str(exc)) from exc
    final_rows = {
        index: {name: weight for name, weight in row.items() if name in deform_group_names and weight > 0.0}
        for index, row in final_physical_rows.items()
    }
    measurements: dict[str, object] = {}
    for side_name, lower_arm, helper, hand in SIDES:
        context = f"{variant} {mesh.name} {side_name}"
        assert_helper_topology(context, armature, lower_arm, helper, hand)
        reference_rows = forearm_twist_generator_run_ownership.rows_by_vertex(
            axial_rows,
            context,
        )
        owns_twist_mass = any(
            weights.get(helper, 0.0) > 0.0
            for weights in reference_rows.values()
        )
        if owns_twist_mass and mesh.vertex_groups.get(helper) is None:
            raise AssertionError(f"{context}: twist-owned mesh rows are missing a helper vertex group")

        try:
            ownership_rows, maximum_ownership_error = (
                forearm_twist_generator_run_ownership.compare_axial_rows(
                    axial_rows,
                    final_rows,
                    deform_group_names,
                    lower_arm,
                    helper,
                    hand,
                    EPSILON,
                    context,
                )
            )
        except ValueError as exc:
            raise AssertionError(str(exc)) from exc
        maximum_deformation_error = 0.0
        for vertex_index, reference in reference_rows.items():
            candidate = final_rows[vertex_index]
            try:
                before, after = forearm_twist_weights.assert_axial_ownership_contract(
                    reference,
                    forearm_twist_generator_run_ownership.side_comparison_candidate(reference, candidate, helper),
                    deform_group_names,
                    lower_arm,
                    helper,
                    hand,
                    EPSILON,
                )
            except ValueError as exc:
                raise AssertionError(f"{context} vertex {vertex_index}: {exc}") from exc
            maximum_deformation_error = max(
                maximum_deformation_error,
                matrix_max_error(
                    weighted_skinning_transform(armature, before, deform_group_names),
                    weighted_skinning_transform(armature, after, deform_group_names),
                ),
            )
        if maximum_deformation_error > EPSILON:
            raise AssertionError(
                f"{context}: saved deformation differs from the authored axial reference ({maximum_deformation_error:.8f})"
            )
        # Physical conservation still uses authoring_eligible; applicability uses
        # the separately frozen pre-authoring source/geometric domain.
        eligible = set(axial_mesh["authoring_eligible"][helper])
        owned = [index for index in eligible
                 if reference_rows[index].get(helper, 0.0) > 0.0]
        domain = axial_mesh.get("source_domain")
        if not isinstance(domain, dict) or set(domain) != {side[2] for side in SIDES}:
            raise AssertionError(f"{context}: missing independent source domain")
        source_rows = domain[helper]
        if (not isinstance(source_rows, list) or any(type(index) is not int for index in source_rows)
                or len(set(source_rows)) != len(source_rows) or not set(source_rows) <= set(reference_rows)):
            raise AssertionError(f"{context}: invalid independent source domain")
        twist_weights = [final_physical_rows[index].get(helper, 0.0) for index in source_rows]
        measurements[side_name] = {
            "ownership_rows": ownership_rows,
            "ownership_max_error": maximum_ownership_error,
            "axial_deformation_max_error": maximum_deformation_error,
            "eligible_owned_rows": len(owned),
            "source_domain_rows": len(source_rows),
            "positive_twist_rows": sum(weight > 0.0 for weight in twist_weights),
            "positive_twist_mass": sum(twist_weights),
        }
        support = measurements[side_name]
        if source_rows and any(
            support[key] <= 0 for key in (
                "positive_twist_rows",
                "positive_twist_mass",
            )
        ):
            raise AssertionError(
                f"{context}: independent source domain has no positive physical twist support "
                f"(source rows={len(source_rows)}, twist rows={support['positive_twist_rows']}, "
                f"twist mass={support['positive_twist_mass']})"
            )
    measurements["physical_rows"] = {"count": physical_count, "max_error": physical_error,
                                      "retained_original_zero_memberships": zero_count}
    return measurements


def measure_variant(variant: str, output_path: Path) -> dict[str, object]:
    bpy.ops.wm.open_mainfile(filepath=str(output_path))
    armatures = [obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE"]
    if len(armatures) != 1:
        raise AssertionError(f"{variant}: expected one armature, found {len(armatures)}")
    armature = armatures[0]
    deform_group_names = {bone.name for bone in armature.data.bones if bone.use_deform}
    meshes = [
        obj
        for obj in bpy.context.scene.objects
        if obj.type == "MESH"
        and any(
            modifier.type == "ARMATURE" and modifier.object == armature
            for modifier in obj.modifiers
        )
    ]
    body_meshes = [mesh for mesh in meshes if mesh.name.endswith(".body")]
    if len(body_meshes) != 1:
        raise AssertionError(f"{variant}: expected one body mesh, found {len(body_meshes)}")
    body = body_meshes[0]
    try:
        ownership_evidence = forearm_twist_generator_run_ownership.load_evidence(
            TOOLS_DIR.parent,
            output_path,
        )
    except ValueError as exc:
        raise AssertionError(f"{variant}: {exc}") from exc
    axial_meshes = evidence_meshes(ownership_evidence, variant)
    skipped_meshes = evidence_meshes({"meshes": ownership_evidence.get("skipped_meshes")}, variant)
    generated_meshes = {mesh.name: mesh for mesh in meshes}
    all_meshes = [obj for obj in bpy.context.scene.objects if obj.type == "MESH"]
    all_by_name = {mesh.name: mesh for mesh in all_meshes}
    if (len(all_by_name) != len(all_meshes) or set(axial_meshes) & set(skipped_meshes)
            or set(axial_meshes) | set(skipped_meshes) != set(all_by_name)):
        raise AssertionError(f"{variant}: generator-run physical mesh census does not match final blend")
    if set(axial_meshes) != set(generated_meshes):
        raise AssertionError(f"{variant}: generator-run ownership mesh set does not match final blend")
    return {
        "body": validate_mesh(
            variant,
            body,
            armature,
            deform_group_names,
            axial_meshes[body.name],
        ),
        "clothing": {
            mesh.name: validate_mesh(
                variant,
                mesh,
                armature,
                deform_group_names,
                axial_meshes[mesh.name],
            )
            for mesh in meshes
            if mesh != body
        },
        "skipped": {name: validate_skipped_mesh(variant, all_by_name[name], record)
                    for name, record in skipped_meshes.items()},
    }


def main() -> None:
    measurements = {
        variant: measure_variant(variant, output_path)
        for variant, output_path in parse_arguments().items()
    }
    print(json.dumps(measurements, sort_keys=True))


if __name__ == "__main__":
    main()
