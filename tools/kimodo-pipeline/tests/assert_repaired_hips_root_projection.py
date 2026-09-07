"""Reopen and prove WF/WB Stage-2 Root ownership on the actual hierarchy."""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys
from typing import Any, Callable

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY
from kimodo_pipeline.materialise import (
    ROOT as ROOT_PATH,
    MaterialisationError,
    _activate_action,
    _curves,
    _rig,
    _validate_role_transform_oracle,
    _walk_root_from_repaired_hips,
)


def close(actual: float, expected: float, tolerance: float, label: str) -> None:
    assert abs(actual - expected) <= tolerance, (label, actual, expected, tolerance)


def repaired_oracle(construction: dict[str, object]) -> dict[str, object]:
    persisted = construction["repaired_transform_oracle"]
    assert isinstance(persisted, dict)
    rows = persisted["rows"]
    assert isinstance(rows, list)
    return {
        "samples": tuple({"source_frame": frame} for frame in range(1, len(rows) + 1)),
        "repaired_root_translation": tuple(
            tuple(float(value) for value in row)
            for row in persisted["root_translation"]
        ),
        "repaired_transform_rows": tuple(
            {name: Matrix(values) for name, values in row.items()}
            for row in rows
        ),
    }


def assert_corruption_rejected(callback: Callable[[], object], label: str) -> None:
    try:
        callback()
    except MaterialisationError:
        return
    raise AssertionError(f"{label} passed the repaired full-transform oracle")


def mutate_point(curve: Any, frame: int, value: float, callback: Callable[[], object], label: str) -> None:
    point = curve.keyframe_points[frame - 1]
    original = float(point.co.y)
    point.co.y = value
    try:
        assert_corruption_rejected(callback, label)
    finally:
        point.co.y = original


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    arguments = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    evidence = json.loads(arguments.evidence.read_text(encoding="utf-8"))
    rig, _, basis = _rig()
    roles = {role.code: role for role in ROLE_REGISTRY}
    report: dict[str, object] = {}
    duration = 32
    for code in ("wf_r", "wf_l", "wb_r", "wb_l"):
        action = bpy.data.actions[f"loc_{arguments.variant}_{code}_loop"]
        curves = _curves(action)
        construction = evidence["roles"][code]["generated_output"]["loop_seam_construction"][0]["construction"]
        transform_oracle = repaired_oracle(construction)
        transform_result = _validate_role_transform_oracle(
            rig, action, roles[code], transform_oracle, code.endswith("_l"), 5e-4,
        )
        assert transform_result["compared_source_frames"] == duration + 1, (code, transform_result)
        assert transform_result["hips_translation_samples"] == duration + 1, (code, transform_result)
        corruption_tolerance = max(
            float(transform_result["maximum_matrix_error"]),
            float(transform_result["maximum_translation_error"]),
            float(transform_result["maximum_rotation_error_rad"]),
        ) * 2.0 + 1e-9
        projection = construction["root_projection"]
        assert projection["stage_1_evidence_consumed"]
        assert projection["source_estimator_superseded"]
        assert projection["hips_carriage_subtractions"] == 1
        assert construction["root_projection_stage"] == "complete_repaired_hips_line_projection"
        scalars = projection["projection_scalar"]
        sample_count = len(scalars)
        assert sample_count == duration + 1
        sign = -1.0 if code.startswith("wb_") else 1.0
        root_forward = curves[(f"{ROOT_PATH}.location", 2)]
        root_lateral = curves[(f"{ROOT_PATH}.location", 0)]
        root_vertical = curves[(f"{ROOT_PATH}.location", 1)]
        for frame, scalar in enumerate(scalars, 1):
            close(float(root_forward.evaluate(frame)), sign * scalar, 2e-6, f"{code} repaired projection frame {frame}")
            close(float(root_lateral.evaluate(frame)), 0.0, 1e-12, f"{code} lateral frame {frame}")
            close(float(root_vertical.evaluate(frame)), 0.0, 1e-12, f"{code} vertical frame {frame}")
        for axis in range(3):
            rotation = curves[(f"{ROOT_PATH}.rotation_euler", axis)]
            assert max(abs(float(rotation.evaluate(frame))) for frame in range(1, sample_count + 1)) <= 1e-12
            assert rotation.modifiers[0].mode_after == "REPEAT"
        assert root_forward.modifiers[0].mode_after == "REPEAT_OFFSET"
        hips_curves = [curve for (path, _), curve in curves.items() if path.startswith('pose.bones["Hips"]')]
        assert hips_curves and all(curve.modifiers[0].mode_after == "REPEAT" for curve in hips_curves)

        repaired = [Vector(row) for row in construction["repaired_evaluated_hips_origins_armature"]]
        local = [basis.inverted_safe() @ point for point in repaired]
        start = Vector((local[0].x, 0.0, local[0].z))
        line = Vector((local[-1].x, 0.0, local[-1].z)) - start
        direction = line.normalized()
        independent = [(Vector((point.x, 0.0, point.z)) - start).dot(direction) for point in local]
        maximum_projection_error = max(abs(actual - expected) for actual, expected in zip(scalars, independent))
        assert maximum_projection_error <= 2e-7
        linear = [scalars[-1] * index / duration for index in range(sample_count)]
        nonlinear = max(abs(actual - expected) for actual, expected in zip(scalars, linear))
        assert nonlinear > 1e-3, (code, nonlinear)

        # Independent corruption oracles. The old source-axis carriage and a
        # pre-repair projection are sampled from the selected raw source, while
        # PCA deliberately chooses a different line from the mandated repaired
        # endpoints. None may reproduce the repaired endpoint-line pacing.
        selection = evidence["roles"][code]
        source = bpy.data.actions[selection["selected"]]
        first, last = selection["selected_source_range"]
        source_frames = list(range(first, last + 1))
        if code.startswith("wb_"):
            source_frames.reverse()
        _activate_action(rig, source)
        raw_local = []
        for frame in source_frames:
            bpy.context.scene.frame_set(frame)
            point = basis.inverted_safe() @ rig.pose.bones["Hips"].matrix.translation
            if code.endswith("_l"):
                point.x = -point.x
            raw_local.append(point)
        raw_start = Vector((raw_local[0].x, 0.0, raw_local[0].z))
        raw_line = Vector((raw_local[-1].x, 0.0, raw_local[-1].z)) - raw_start
        raw_direction = raw_line.normalized()
        pre_repair = [(Vector((point.x, 0.0, point.z)) - raw_start).dot(raw_direction) for point in raw_local]
        pre_repair_error = max(abs(actual - corrupt) for actual, corrupt in zip(scalars, pre_repair))
        assert pre_repair_error > 1e-8, (code, pre_repair_error)
        fixed_source_axis = [(-1.0 if code.startswith("wb_") else 1.0) * (point.z - raw_local[0].z) for point in raw_local]
        fixed_axis_error = max(abs(actual - corrupt) for actual, corrupt in zip(scalars, fixed_source_axis))
        assert fixed_axis_error > 1e-8, (code, fixed_axis_error)
        offsets = [(point.x - local[0].x, point.z - local[0].z) for point in local]
        mean_x = sum(point[0] for point in offsets) / len(offsets)
        mean_z = sum(point[1] for point in offsets) / len(offsets)
        xx = sum((point[0] - mean_x) ** 2 for point in offsets) / len(offsets)
        zz = sum((point[1] - mean_z) ** 2 for point in offsets) / len(offsets)
        xz = sum((point[0] - mean_x) * (point[1] - mean_z) for point in offsets) / len(offsets)
        major = (xx + zz + math.sqrt((xx - zz) ** 2 + 4.0 * xz * xz)) * .5
        pca = Vector((xz, 0.0, major - xx)) if abs(xz) + abs(major - xx) > 1e-12 else Vector((1.0, 0.0, 0.0))
        pca.normalize()
        if pca.dot(direction) < 0.0:
            pca.negate()
        pca_progress = [Vector((point[0], 0.0, point[1])).dot(pca) for point in offsets]
        pca_error = max(abs(actual - corrupt) for actual, corrupt in zip(scalars, pca_progress))
        assert pca_error > 1e-7, (code, pca_error)
        wrong_basis = [point.z - repaired[0].z for point in repaired]
        wrong_basis_error = max(abs(actual - corrupt) for actual, corrupt in zip(scalars, wrong_basis))
        assert wrong_basis_error > .5, (code, wrong_basis_error)

        validate_transform = lambda: _validate_role_transform_oracle(
            rig, action, roles[code], transform_oracle, code.endswith("_l"), corruption_tolerance,
        )
        hips_lateral = curves[(f'pose.bones["Hips"].location', 0)]
        hips_forward = curves[(f'pose.bones["Hips"].location', 2)]
        interior_frame = 16
        corruption_results: dict[str, bool] = {}

        mutate_point(
            hips_lateral, interior_frame,
            -float(hips_lateral.evaluate(interior_frame)) + .25,
            validate_transform, f"{code} wrong mirror basis",
        )
        corruption_results["wrong_mirror_basis"] = True
        carriage = abs(float(root_forward.evaluate(interior_frame)))
        assert carriage > .05, (code, carriage)
        original_forward = float(hips_forward.evaluate(interior_frame))
        mutate_point(
            hips_forward, interior_frame, original_forward - carriage,
            validate_transform, f"{code} second carriage subtraction",
        )
        corruption_results["second_carriage_subtraction"] = True
        mutate_point(
            hips_forward, interior_frame, original_forward + carriage,
            validate_transform, f"{code} omitted carriage subtraction",
        )
        corruption_results["omitted_carriage_subtraction"] = True

        original_root_values = [float(point.co.y) for point in root_forward.keyframe_points]
        try:
            amplification = max(1.0, 4.0 * corruption_tolerance / pre_repair_error)
            for point, repaired_scalar, corrupt in zip(root_forward.keyframe_points, scalars, pre_repair):
                # Preserve the measured pre-repair residual's shape while
                # ensuring this corruption sits outside the oracle's measured
                # numerical floor on nearly coincident real-source windows.
                point.co.y = sign * (repaired_scalar + amplification * (corrupt - repaired_scalar))
            assert_corruption_rejected(validate_transform, f"{code} pre-repair projection")
        finally:
            for point, original_value in zip(root_forward.keyframe_points, original_root_values):
                point.co.y = original_value
        corruption_results["pre_repair_projection"] = True

        opening_original = [float(point.co.y) for point in hips_forward.keyframe_points[:6]]
        try:
            for index, point in enumerate(hips_forward.keyframe_points[:6]):
                point.co.y = opening_original[index] + .08 * (1.0 - index / 6.0)
            assert_corruption_rejected(validate_transform, f"{code} one-sided seam repair")
        finally:
            for point, original_value in zip(hips_forward.keyframe_points[:6], opening_original):
                point.co.y = original_value
        corruption_results["one_sided_seam_repair"] = True
        mutate_point(
            hips_forward, interior_frame, original_forward + .08,
            validate_transform, f"{code} immutable interior modification",
        )
        corruption_results["interior_modification"] = True

        angle = float(projection["constant_alignment_radians"])
        alignment = rig.data.bones["Root"].matrix_local @ Matrix.Rotation(-angle, 4, "Y") @ rig.data.bones["Root"].matrix_local.inverted_safe()
        origin = repaired[0]
        _activate_action(rig, action)
        pose_errors = []
        double_subtraction_errors = []
        trace = []
        for cycle in range(3):
            for base_frame in (1, 2, sample_count - 1, sample_count):
                frame = base_frame + cycle * duration
                bpy.context.scene.frame_set(frame)
                expected = origin + alignment.to_3x3() @ (repaired[base_frame - 1] - origin)
                expected += basis @ Vector((0.0, 0.0, sign * scalars[-1] * cycle))
                actual = rig.pose.bones["Hips"].matrix.translation
                pose_errors.append((actual - expected).length)
                root_offset = rig.pose.bones["Root"].matrix.translation - rig.pose.bones["Root"].bone.matrix_local.translation
                double_subtraction_errors.append((actual - root_offset - expected).length)
                trace.append({
                    "frame": frame,
                    "cycle": cycle + 1,
                    "base_frame": base_frame,
                    "root_forward": float(root_forward.evaluate(frame)),
                    "projection_scalar": scalars[base_frame - 1],
                    "hips_world_error": (actual - expected).length,
                })
        maximum_pose_error = max(pose_errors)
        assert maximum_pose_error <= 2e-5, (code, maximum_pose_error)
        # At a travelled sample, a second subtraction must remove the complete
        # accumulated Root carriage and therefore fail the world-pose oracle.
        assert max(double_subtraction_errors) > .5, (code, double_subtraction_errors)
        mirror_peer = code[:-1] + ("l" if code.endswith("r") else "r")
        report[code] = {
            "mirror_peer": mirror_peer,
            "endpoint": sign * scalars[-1],
            "maximum_projection_error": maximum_projection_error,
            "maximum_linear_envelope_deviation": nonlinear,
            "maximum_world_pose_error": maximum_pose_error,
            "pre_repair_projection_failure": pre_repair_error,
            "fixed_source_axis_failure": fixed_axis_error,
            "pca_direction_failure": pca_error,
            "wrong_basis_failure": wrong_basis_error,
            "minimum_double_subtraction_failure": min(value for value in double_subtraction_errors if value > 1e-4),
            "full_transform_oracle": transform_result,
            "actual_hierarchy_corruptions_rejected": corruption_results,
            "trace": trace,
        }
    for first, second in (("wf_r", "wf_l"), ("wb_r", "wb_l")):
        close(report[first]["endpoint"], report[second]["endpoint"], 2e-7, f"{first}/{second} mirror endpoint")  # type: ignore[index]
    close(report["wf_r"]["endpoint"], -report["wb_r"]["endpoint"], 2e-7, "WF/WB reversal")  # type: ignore[index,operator]
    try:
        _walk_root_from_repaired_hips(rig, roles["wf_r"], {}, basis)
    except MaterialisationError as error:
        assert "Stage 2 requires" in str(error)
    else:
        raise AssertionError("Stage 2 accepted missing repaired-Hips evidence")
    _activate_action(rig, bpy.data.actions[f"loc_{arguments.variant}_wf_r_loop"])
    names = tuple(bone.name for bone in rig.pose.bones if bone.name != "Root")
    hierarchy_rows = []
    for frame in range(1, duration + 2):
        bpy.context.scene.frame_set(frame)
        hierarchy_rows.append({name: rig.pose.bones[name].matrix.copy() for name in names})
    degenerate_rows = [{name: matrix.copy() for name, matrix in row.items()} for row in hierarchy_rows]
    degenerate_rows[-1]["Hips"].translation = degenerate_rows[0]["Hips"].translation
    for label, rows, expected_message in (
        ("degenerate", degenerate_rows, "degenerate"),
        ("wrong_direction", list(reversed(hierarchy_rows)), "contradicts declared signed direction"),
    ):
        try:
            _walk_root_from_repaired_hips(rig, roles["wf_r"], {
                "accepted": True,
                "root_projection_stage": "pending_family_specific_repaired_hips_projection",
                "_frozen_repaired_armature_rows": rows,
            }, basis)
        except MaterialisationError as error:
            assert expected_message in str(error), (label, error)
        else:
            raise AssertionError(f"Stage 2 accepted {label} repaired-Hips line")
    print("KIMODO_REPAIRED_HIPS_ROOT_PROJECTION=" + json.dumps(report, sort_keys=True))


main()
