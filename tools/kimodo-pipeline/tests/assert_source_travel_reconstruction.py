"""Numerical source-travel reconstruction regressions on a disposable blend."""
from __future__ import annotations

import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Matrix

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY
from kimodo_pipeline.materialise import (
    ROOT as ROOT_PATH,
    MaterialisationError,
    SelectionError,
    _activate_action,
    _align_about,
    _curve,
    _curves,
    _evaluated_hips_root_motion,
    _rig,
    _new_action,
    _source_loop_candidate_eligible,
    _straight_source_projection,
)


def close(actual: float, expected: float, tolerance: float, label: str) -> None:
    assert abs(actual - expected) <= tolerance, (label, actual, expected, tolerance)


def main() -> None:
    rig, _, basis = _rig()
    roles = {role.code: role for role in ROLE_REGISTRY}
    walk_source = bpy.data.actions["kimodo_multi_388sjrhc"]
    report: dict[str, object] = {}

    repeat_action, repeat_bag = _new_action("__source_travel_repeat_contract", rig)
    _curve(repeat_bag, f"{ROOT_PATH}.location", 2, [0.0, .2, .55, 1.0], True, True)
    _curve(repeat_bag, 'pose.bones["Hips"].location', 0, [0.0, .1, -.1, 0.0], True, False)
    repeat_curves = _curves(repeat_action)
    root_repeat = repeat_curves[(f"{ROOT_PATH}.location", 2)]
    hips_repeat = repeat_curves[('pose.bones["Hips"].location', 0)]
    assert root_repeat.modifiers[0].mode_after == "REPEAT_OFFSET"
    assert hips_repeat.modifiers[0].mode_after == "REPEAT"
    close(float(root_repeat.evaluate(7)), 2.0, 1e-9, "second-cycle Root accumulation")
    close(float(hips_repeat.evaluate(7)), 0.0, 1e-9, "second-cycle non-Root repetition")
    report["repeat_modes"] = {"root_frame_7": 2.0, "hips_frame_7": 0.0}
    bpy.data.actions.remove(repeat_action, do_unlink=True)

    root_curve = _curves(walk_source)[(f"{ROOT_PATH}.location", 0)]
    root_point = min(root_curve.keyframe_points, key=lambda point: abs(float(point.co.x) - 61.0))
    original_root_value = float(root_point.co.y)
    root_point.co.y = original_root_value + .25
    try:
        _evaluated_hips_root_motion(rig, walk_source, list(range(29, 62)), basis, "native")
    except MaterialisationError as error:
        assert "animated source Root carriage" in str(error), error
        report["animated_source_root"] = {"rejected": True, "reason": str(error)}
    else:
        raise AssertionError("animated source Root carriage was double-ownable")
    finally:
        root_point.co.y = original_root_value

    for code in ("wf_r", "wf_l", "wb_r", "wb_l"):
        role = roles[code]
        frames = list(range(29, 62))
        if "reverse" in role.derivation:
            frames.reverse()
        raw = _evaluated_hips_root_motion(rig, walk_source, frames, basis, role.derivation)
        projected, aligned, alignment, evidence = _straight_source_projection(
            rig, role, walk_source, frames, basis,
        )
        target_axis = 2
        expected_sign = -1.0 if code.startswith("wb") else 1.0
        close(projected[target_axis][-1], expected_sign * 0.928596497, 2e-7, f"{code} Root endpoint")
        close(aligned[0][-1], 0.0, 2e-7, f"{code} aligned lateral endpoint")
        close(float(evidence["maximum_progress_copy_error"]), 0.0, 1e-12, f"{code} progress")
        # A second Hips subtraction is the proven corruption: it is wrong by
        # the complete role-signed Root carriage, not a seam-scale residual.
        once = math.hypot(aligned[0][-1] - projected[0][-1], aligned[2][-1] - projected[2][-1])
        twice = math.hypot(aligned[0][-1] - 2.0 * projected[0][-1], aligned[2][-1] - 2.0 * projected[2][-1])
        assert once < 2e-7, (code, once)
        close(twice, 0.928596497, 2e-7, f"{code} double subtraction")
        # The Root trace is copied source progress, including the nonlinear
        # timing near the endpoint; there is no trailing opposite correction.
        signed = [expected_sign * value for value in projected[target_axis]]
        assert all(following >= preceding - 1e-9 for preceding, following in zip(signed, signed[1:])), code
        linear = [signed[-1] * index / (len(signed) - 1) for index in range(len(signed))]
        nonlinear = max(abs(actual - expected) for actual, expected in zip(signed, linear))
        assert nonlinear > .016, (code, nonlinear)

        _activate_action(rig, walk_source)
        bpy.context.scene.frame_set(frames[0])
        first = rig.pose.bones["LeftFoot"].matrix.copy()
        bpy.context.scene.frame_set(frames[len(frames) // 2])
        second = rig.pose.bones["RightFoot"].matrix.copy()
        origin = rig.pose.bones["Hips"].matrix.translation.copy()
        before = first.inverted_safe() @ second
        after = _align_about(first, alignment, origin).inverted_safe() @ _align_about(second, alignment, origin)
        matrix_error = max(abs(before[row][column] - after[row][column]) for row in range(4) for column in range(4))
        assert matrix_error < 2e-7, (code, matrix_error)

        accepted, repair = _source_loop_candidate_eligible(
            rig, role, walk_source, 29, 61, role.derivation, basis,
        )
        assert accepted, (code, repair)
        assert repair["role"] == code
        assert not repair["failures"], code
        assert repair["both_windows_modified"], code
        assert repair["maximum_immutable_interior_change"] == 0.0, code
        assert not repair["foot_risk"]["reversal"], (code, repair["foot_risk"])
        assert not repair["foot_risk"]["spike"], (code, repair["foot_risk"])
        report[code] = {
            "raw_net_lateral_forward": [raw[0][-1], raw[2][-1]],
            "root_net_lateral_forward": [projected[0][-1], projected[2][-1]],
            "alignment_degrees": evidence["constant_alignment_degrees"],
            "maximum_linear_envelope_deviation": nonlinear,
            "constant_frame_matrix_error": matrix_error,
            "one_time_residual": once,
            "double_subtraction_error": twice,
            "repair_cost": repair["repair_cost"],
            "repair_metrics": repair["metrics"],
            "repair_windows": repair["window_ranges_zero_based"],
            "foot_risk": repair["foot_risk"],
        }

    xr_l = roles["xr_l"]
    xr_source = bpy.data.actions["kimodo_multi_r_2e0lwf"]
    projected, aligned, _, evidence = _straight_source_projection(
        rig, xr_l, xr_source, list(range(261, 314)), basis,
    )
    close(projected[0][-1], -0.7973846, 5e-6, "xr_l Root endpoint")
    close(aligned[2][-1], 0.0, 2e-7, "xr_l aligned forward endpoint")
    report["xr_l"] = {
        "root_net_lateral_forward": [projected[0][-1], projected[2][-1]],
        "alignment_degrees": evidence["constant_alignment_degrees"],
        "estimator": evidence,
    }

    try:
        _straight_source_projection(
            rig, roles["xr_r"], walk_source, list(range(267, 304)), basis,
        )
    except SelectionError as error:
        report["xr_r"] = {"rejected": True, "reason": str(error)}
    else:
        raise AssertionError("unstable xr_r source window unexpectedly received a straight frame")

    projected, aligned, alignment, transition_evidence = _straight_source_projection(
        rig, roles["i2f_r"], walk_source, list(range(1, 30)), basis,
    )
    assert transition_evidence["procedure"] == "finite_transition_endpoint_pca_source_progress_v1"
    assert not transition_evidence["sustained_loop_subwindow_estimator_applicable"]
    assert transition_evidence["finite_endpoint_pacing_preserved"]
    assert transition_evidence["finite_context"]["idle_endpoint"] == "start"
    assert projected[2] == transition_evidence["progress"]
    assert projected[2] != [projected[2][-1] * index / (len(projected[2]) - 1) for index in range(len(projected[2]))]
    assert aligned[0] == [0.0] * len(aligned[0]) or transition_evidence["body_rigid_alignment_applied"] is False
    assert alignment == Matrix.Identity(4)
    report["i2f_r"] = transition_evidence

    # Confined and turning families retain their distinct policies even when an
    # endpoint direction happens to exist.
    for code, source_name, frames in (
        ("sr", "kimodo_multi_388sjrhc", list(range(121, 161))),
        ("pr45", "kimodo_multi_388sjrhc", list(range(161, 211))),
        ("ar_r", "kimodo_multi_388sjrhc", list(range(415, 459))),
    ):
        source = bpy.data.actions[source_name]
        projected, aligned, _, evidence = _straight_source_projection(rig, roles[code], source, frames, basis)
        assert projected == aligned, code
        if code == "sr":
            assert evidence["role_family"] == "precision_lateral"
            assert evidence["correction_function"] == "exact Root-rest lateral projection; no lateral pacing deformation"
            report[code] = {"straight_estimator_applied": False, "policy": evidence["role_family"]}
        else:
            assert evidence["applicability"] == "not_straight_directional", code
            report[code] = {"straight_estimator_applied": False, "policy": evidence["applicability"]}

    print("KIMODO_SOURCE_TRAVEL_RECONSTRUCTION=" + json.dumps(report, sort_keys=True))


main()
