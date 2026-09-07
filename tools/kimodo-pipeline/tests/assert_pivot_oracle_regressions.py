"""Concrete corruption regressions for the immutable Segment-2 transform oracle."""
from __future__ import annotations

import sys
import json
import math
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name
from kimodo_pipeline.materialise import (
    MaterialisationError,
    _action_fcurves,
    _activate_action,
    _assert_pivot_oracle_source_intact,
    _capture_pivot_transform_oracle,
    _global_z_yaw,
    _rig,
    _validate_role_transform_oracle,
    _validate_pivot_non_root_targets,
    _validate_pivot_kinematics,
)


def inject_opposite_body_direction(rig, action, sign):
    _activate_action(rig, action)
    bpy.context.scene.frame_set(1)
    opening = rig.pose.bones["Hips"].matrix.copy()
    for curve in _action_fcurves(action):
        if not curve.data_path.endswith('pose.bones["Hips"].rotation_euler'):
            continue
        point = curve.keyframe_points[-1]
        original = float(point.co.y)
        for delta in (math.pi / 2.0, -math.pi / 2.0, math.pi, -math.pi):
            point.co.y = original + delta
            bpy.context.scene.frame_set(int(action.frame_range[1]))
            heading = _global_z_yaw(rig.pose.bones["Hips"].matrix @ opening.inverted_safe())
            if heading * sign < -0.1:
                return point, original, heading
        point.co.y = original
    raise AssertionError(f"could not inject opposite body direction into {action.name}")


def rejects(callback) -> None:
    try:
        callback()
    except (MaterialisationError, ValueError):
        return
    raise AssertionError("corruption was accepted by immutable pivot-oracle validation")


def main() -> None:
    variant = sys.argv[sys.argv.index("--variant") + 1]
    source_name = sys.argv[sys.argv.index("--source") + 1]
    ensure_registered()
    rig, _, _ = _rig()
    source = bpy.data.actions[source_name]
    frames = list(range(161, 211))
    oracle = _capture_pivot_transform_oracle(rig, source, frames)
    reference = _capture_pivot_transform_oracle(rig, bpy.data.actions["pose_ref_idle"], [1])
    oracle["reference_oracle"] = reference
    pr_role = next(role for role in ROLE_REGISTRY if role.code == "pr45")
    pr = bpy.data.actions[action_name(variant, pr_role)]
    pl_role = next(role for role in ROLE_REGISTRY if role.code == "pl45")
    pl = bpy.data.actions[action_name(variant, pl_role)]

    # Prove the actual, uncorrupted repeated hierarchy against the immutable
    # source snapshot before exercising the rejection cases below.
    _validate_pivot_non_root_targets(rig, pr, oracle, reference, False, "pr45")
    kinematics = _validate_pivot_kinematics(rig, pr, oracle, False, "pr45", 1e-2)
    _validate_pivot_non_root_targets(rig, pl, oracle, reference, True, "pl45")
    pl_kinematics = _validate_pivot_kinematics(rig, pl, oracle, True, "pl45", 1e-2)
    print("KIMODO_PIVOT_REPEAT_ORACLE_JSON=" + json.dumps({
        "action": pr.name,
        "first_repeat_samples": [51, 75, 99],
        "targets": ["Hips", "Chest", "Head", "LeftFoot", "RightFoot"],
        "checks": ["evaluated_transform", "body_heading", "root_relative_trajectory"],
        "kinematics": kinematics,
        "reflected_kinematics": pl_kinematics,
    }, sort_keys=True))

    # A post-capture expected-value mutation must fail, proving expected data
    # cannot be silently replaced by writer residuals or generated output.
    bad_oracle = dict(oracle)
    bad_samples = list(oracle["samples"])
    bad_sample = dict(bad_samples[1])
    bad_bones = dict(bad_sample["bones"])
    head = [list(row) for row in bad_bones["Head"]]
    head[0][3] += 1.0
    bad_bones["Head"] = tuple(tuple(row) for row in head)
    bad_sample["bones"] = bad_bones
    bad_samples[1] = bad_sample
    bad_oracle["samples"] = tuple(bad_samples)
    rejects(lambda: _validate_pivot_non_root_targets(rig, pr, bad_oracle, reference, False, "pr45"))

    head_bone = rig.pose.bones["Head"]
    old_mode = head_bone.rotation_mode
    head_bone.rotation_mode = "QUATERNION"
    rejects(lambda: _assert_pivot_oracle_source_intact(rig, source, oracle))
    head_bone.rotation_mode = old_mode
    curve = next(curve for curve in _action_fcurves(source) if curve.keyframe_points)
    old_value = float(curve.keyframe_points[0].co.y)
    curve.keyframe_points[0].co.y = old_value + 1.0
    rejects(lambda: _assert_pivot_oracle_source_intact(rig, source, oracle))
    curve.keyframe_points[0].co.y = old_value

    # A pr45 output is not its own reflected ground truth.
    rejects(lambda: _validate_pivot_non_root_targets(rig, pr, oracle, reference, True, "pr45"))

    # Root keys remain plausible, but this Hips-only split mutation makes the
    # stepping body diverge from its immutable transferred full transform.
    hips_yaw = next(
        curve for curve in _action_fcurves(pr)
        if curve.data_path.endswith('pose.bones["Hips"].rotation_euler') and curve.array_index == 1
    )
    point = hips_yaw.keyframe_points[len(hips_yaw.keyframe_points) // 2]
    original = float(point.co.y)
    point.co.y = original + .25
    role = next(role for role in ROLE_REGISTRY if role.code == "pr45")
    rejects(lambda: _validate_role_transform_oracle(rig, pr, role, oracle, False, 5e-4))
    point.co.y = original

    # A wide orbital split repeats with ordinary non-Root cycle semantics and
    # must fail both first-unit and accumulated-repeat hierarchy checks.
    hips_location = next(
        curve for curve in _action_fcurves(pr)
        if curve.data_path.endswith('pose.bones["Hips"].location') and curve.array_index == 0
    )
    orbit_point = hips_location.keyframe_points[len(hips_location.keyframe_points) // 2]
    orbit_original = float(orbit_point.co.y)
    orbit_point.co.y = orbit_original + 3.0
    rejects(lambda: _validate_pivot_non_root_targets(rig, pr, oracle, reference, False, "pr45"))
    orbit_point.co.y = orbit_original

    # Deliberately decouple one foot from the coherent Hips/leg chain while
    # leaving Root and body trajectories untouched.
    foot_location = next(
        curve for curve in _action_fcurves(pr)
        if curve.data_path.endswith('pose.bones["RightFoot"].location') and curve.array_index == 0
    )
    foot_point = foot_location.keyframe_points[len(foot_location.keyframe_points) // 2]
    foot_original = float(foot_point.co.y)
    foot_point.co.y = foot_original + .25
    rejects(lambda: _validate_pivot_kinematics(rig, pr, oracle, False, "pr45", 1e-2))
    foot_point.co.y = foot_original

    # Fixed-sign corruptions cover both semantic directions. Root curves remain
    # untouched and valid while body, orbit/split, or one foot is corrupted.
    for code, action, role, mirror, sign in (
        ("pr45", pr, pr_role, False, -1.0),
        ("pl45", pl, pl_role, True, 1.0),
    ):
        point, original, heading = inject_opposite_body_direction(rig, action, sign)
        assert heading * sign < -0.1
        rejects(lambda action=action, role=role, mirror=mirror, code=code: _validate_pivot_kinematics(rig, action, oracle, mirror, code, 1e-2))
        point.co.y = original
        for amount in (1.5, 3.0):
            curve = next(curve for curve in _action_fcurves(action) if curve.data_path.endswith('pose.bones["Hips"].location') and curve.array_index == 0)
            split = curve.keyframe_points[len(curve.keyframe_points) // 2]
            split_original = float(split.co.y)
            split.co.y = split_original + amount
            rejects(lambda action=action, role=role, mirror=mirror, code=code: _validate_pivot_kinematics(rig, action, oracle, mirror, code, 1e-2))
            split.co.y = split_original
        curve = next(curve for curve in _action_fcurves(action) if curve.data_path.endswith('pose.bones["RightFoot"].location') and curve.array_index == 0)
        foot = curve.keyframe_points[len(curve.keyframe_points) // 2]
        foot_original = float(foot.co.y)
        foot.co.y = foot_original + .25
        rejects(lambda action=action, role=role, mirror=mirror, code=code: _validate_pivot_kinematics(rig, action, oracle, mirror, code, 1e-2))
        foot.co.y = foot_original

    # Root must repeat with its accumulated offset.  Changing only its repeat
    # mode leaves the first unit intact, but the immutable second-cycle
    # hierarchy expectation must reject ordinary repetition.
    root_yaw = next(
        curve for curve in _action_fcurves(pr)
        if curve.data_path.endswith('pose.bones["Root"].rotation_euler') and curve.array_index == 1
    )
    modifier = next(item for item in root_yaw.modifiers if item.type == "CYCLES")
    old_mode = modifier.mode_after
    modifier.mode_after = "REPEAT"
    rejects(lambda: _validate_pivot_non_root_targets(rig, pr, oracle, reference, False, "pr45"))
    modifier.mode_after = old_mode


main()
