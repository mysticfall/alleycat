"""Evaluate published disposable-copy Actions against the ANIM-002 curve contract."""
from __future__ import annotations

import sys
import json
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name, role_projection_policy
from kimodo_pipeline.discovery import _segments
from kimodo_pipeline.materialise import DEFAULT_TOLERANCES, ROOT as ROOT_PATH, _action_fcurves, _anatomical_character_scale, _capture_pivot_transform_oracle, _evaluated_world_samples, _loop_hierarchy_seam_metrics, _rig, _validate, _validate_loop_hierarchy_seam
from kimodo_pipeline.recurrence import recurrence_acceptance

ZERO, MINIMUM = 1e-4, 1e-4


def _curves(action: object) -> dict[tuple[str, int], object]:
    return {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}


def main() -> None:
    variant = sys.argv[sys.argv.index("--variant") + 1]
    pr45_source = sys.argv[sys.argv.index("--pr45-source") + 1]
    pl45_source = sys.argv[sys.argv.index("--pl45-source") + 1]
    ensure_registered()
    sources = {action.name for action in bpy.data.actions if action.name.startswith("kimodo_multi_")}
    rig, _, rig_basis = _rig()
    segment = {item["index"]: item for item in _segments()}[2]
    frames = list(range(segment["start_frame"], segment["end_frame"] + 1))
    reference_oracle = _capture_pivot_transform_oracle(rig, bpy.data.actions["pose_ref_idle"], [1])
    pivot_oracles = {
        "pr45": (_capture_pivot_transform_oracle(rig, bpy.data.actions[pr45_source], frames), reference_oracle),
        "pl45": (_capture_pivot_transform_oracle(rig, bpy.data.actions[pl45_source], frames), reference_oracle),
    }
    _validate(variant, sources, ROLE_REGISTRY, True, DEFAULT_TOLERANCES, pivot_oracles)
    world_up = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
    maxima = {"hips_horizontal_residual": 0.0, "hips_vertical_residual": 0.0, "foot_vertical_residual": 0.0}
    loop_seams = {}
    for role in ROLE_REGISTRY:
        action = bpy.data.actions[action_name(variant, role)]
        samples = _evaluated_world_samples(rig, action, int(action.frame_range[1]))
        root_start, hips_start, left_start, right_start = samples[0]
        relative_start = hips_start - root_start
        for root, hips, left, right in samples:
            residual = hips - root - relative_start
            vertical = residual.dot(world_up)
            maxima["hips_vertical_residual"] = max(maxima["hips_vertical_residual"], abs(vertical))
            maxima["hips_horizontal_residual"] = max(maxima["hips_horizontal_residual"], (residual - world_up * vertical).length)
            maxima["foot_vertical_residual"] = max(maxima["foot_vertical_residual"], abs((left - left_start).dot(world_up)), abs((right - right_start).dot(world_up)))
        if role.is_loop:
            metrics = _loop_hierarchy_seam_metrics(rig, action)
            loop_seams[role.code] = metrics
            if role_projection_policy(role).loop_closure == "generated_recurrence":
                recurrence = recurrence_acceptance(
                    metrics["maxima"], _anatomical_character_scale(rig), "generated recurrence",
                )
                assert recurrence["accepted"], (role.code, recurrence, metrics["landmarks"])
            _validate_loop_hierarchy_seam(rig, action, role)
            assert max(metrics["cycle_extension_fidelity"]["root_second_cycle_accumulation_error"].values()) <= DEFAULT_TOLERANCES["reconstruction_translation_tolerance"], (role.code, metrics)
    print("LOCOMOTION_CARRIAGE_METRICS=" + json.dumps(maxima, sort_keys=True))
    print("LOCOMOTION_EVALUATED_LOOP_SEAMS=" + json.dumps(loop_seams, sort_keys=True))


main()
