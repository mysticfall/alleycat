"""Exercise the evaluated strafe carriage oracle against a real disposable output."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Callable

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.materialise import (
    DEFAULT_TOLERANCES,
    ROOT as ROOT_PATH,
    MaterialisationError,
    _curves,
    _rig,
    _strafe_foot_oracle,
    _validate,
    _validate_loop_hierarchy_seam,
)


def rejects(callback: Callable[[], object], label: str) -> None:
    try:
        callback()
    except MaterialisationError:
        return
    raise AssertionError(f"{label} corruption passed strafe validation")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    arguments = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else None)
    evidence = json.loads(arguments.evidence.read_text(encoding="utf-8"))
    rig, _, basis = _rig()
    roles = {role.code: role for role in ROLE_REGISTRY}
    sources = {action.name for action in bpy.data.actions if action.name.startswith("kimodo_multi_")}
    report = {}
    for code in ("xr_l", "xr_r", "xl_l", "xl_r"):
        role = roles[code]
        selected = evidence["roles"][code]["selected_source_range"]
        frames = list(range(selected[0], selected[1] + 1))
        if "reverse" in role.derivation:
            frames.reverse()
        output = bpy.data.actions[action_name(arguments.variant, role)]
        curves = _curves(output)
        root = [[float(curves[(f"{ROOT_PATH}.location", axis)].evaluate(frame)) for frame in range(1, len(frames) + 1)] for axis in range(3)]
        oracle = _strafe_foot_oracle(rig, bpy.data.actions[evidence["roles"][code]["selected"]], output, frames, root, basis, "mirror" in role.derivation)
        assert oracle["accepted"], f"{code} unexpectedly skates: {oracle}"
        assert any(oracle["feet"][side]["plant_proxy_intervals"] for side in ("Left", "Right")), code
        expected_sign = -1.0 if code.startswith("xr_") else 1.0
        root_lateral = root[0]
        root_forward = root[2]
        root_vertical = root[1]
        yaw = [float(curves[(f"{ROOT_PATH}.rotation_euler", 1)].evaluate(frame)) for frame in range(1, len(frames) + 1)]
        assert expected_sign * root_lateral[-1] > 0.1, (code, root_lateral[-1])
        assert max(abs(value) for value in root_forward) <= 1e-8, code
        assert max(abs(value) for value in root_vertical) <= 1e-8, code
        assert max(abs(value) for value in yaw) <= 1e-8, code

        validate = lambda: _validate(
            arguments.variant, sources, (role,), False, DEFAULT_TOLERANCES, {}, None,
        )
        lateral_curve = curves[(f"{ROOT_PATH}.location", 0)]
        forward_curve = curves[(f"{ROOT_PATH}.location", 2)]
        original_lateral = [float(point.co.y) for point in lateral_curve.keyframe_points]
        original_forward = [float(point.co.y) for point in forward_curve.keyframe_points]
        try:
            for lateral_point, forward_point, value in zip(
                lateral_curve.keyframe_points, forward_curve.keyframe_points, original_lateral,
            ):
                lateral_point.co.y = 0.0
                forward_point.co.y = abs(value)
            rejects(validate, f"{code} wrong Root axis")
        finally:
            for point, value in zip(lateral_curve.keyframe_points, original_lateral):
                point.co.y = value
            for point, value in zip(forward_curve.keyframe_points, original_forward):
                point.co.y = value
        try:
            for point, value in zip(lateral_curve.keyframe_points, original_lateral):
                point.co.y = -value
            rejects(validate, f"{code} wrong Root sign")
        finally:
            for point, value in zip(lateral_curve.keyframe_points, original_lateral):
                point.co.y = value

        seam_curve = curves[('pose.bones["LeftFoot"].location', 0)]
        seam_point = seam_curve.keyframe_points[1]
        seam_original = float(seam_point.co.y)
        seam_point.co.y = seam_original + .08
        try:
            rejects(lambda: _validate_loop_hierarchy_seam(rig, output), f"{code} one-sided seam")
        finally:
            seam_point.co.y = seam_original

        # Corrupt an actual foot transform in the all-edge source-oracle range.
        # Selection, duration, proxy, and threshold all come from the evaluated
        # fixture itself; no correction window or edge exclusion exists.
        injected_side, injected_interval = next(
            (side, interval)
            for side in ("Left", "Right")
            for interval in oracle["feet"][side]["plant_proxy_intervals"]
        )
        curve = curves[(f'pose.bones["{injected_side}Foot"].location', 0)]
        injected_frame = injected_interval["frames"][1]
        point = curve.keyframe_points[injected_frame - 1]
        original = float(point.co.y)
        point.co.y = original + oracle["unintended_skate_tolerance"] * 10.0
        injected = _strafe_foot_oracle(rig, bpy.data.actions[evidence["roles"][code]["selected"]], output, frames, root, basis, "mirror" in role.derivation)
        point.co.y = original
        assert not injected["accepted"], f"{code} did not detect injected foot skate"
        report[code] = {
            "root_lateral_endpoint": root_lateral[-1],
            "maximum_root_forward": max(abs(value) for value in root_forward),
            "maximum_root_vertical": max(abs(value) for value in root_vertical),
            "maximum_root_yaw": max(abs(value) for value in yaw),
            "source_oracle_compared_frames": oracle["source_oracle_compared_frames"],
            "wrong_axis_rejected": True,
            "wrong_sign_rejected": True,
            "one_sided_seam_rejected": True,
            "foot_skate_rejected": True,
        }
    print("KIMODO_STRAFE_PUBLICATION_ORACLE=" + json.dumps(report, sort_keys=True))


if __name__ == "__main__":
    main()
