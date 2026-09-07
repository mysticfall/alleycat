"""Disposable full-transform and Root-anchor corruption regressions by role family."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.contracts import ROLE_REGISTRY, action_name, role_projection_policy
from kimodo_pipeline.materialise import (
    DEFAULT_TOLERANCES,
    MaterialisationError,
    _action_fcurves,
    _activate_action,
    _capture_pivot_transform_oracle,
    _rig,
    _validate_body_carriage,
    _validate_role_transform_oracle,
)


def rejects(callback: object, label: str) -> None:
    try:
        callback()
    except MaterialisationError:
        return
    raise AssertionError(f"{label} corruption passed immutable full-transform oracle")


def horizontal_separation(rig: object, action: object, frame: int) -> float:
    _activate_action(rig, action)
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()
    root = rig.pose.bones["Root"].matrix.translation
    hips = rig.pose.bones["Hips"].matrix.translation
    up = (rig.matrix_world.to_3x3() @ rig.data.bones["Root"].matrix_local.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    offset = rig.matrix_world.to_3x3() @ (hips - root)
    return (offset - up * offset.dot(up)).length


def mutate_horizontal_hips(rig: object, action: object, frame: int, amount: float) -> tuple[object, float, float]:
    baseline = horizontal_separation(rig, action, frame)
    for curve in _action_fcurves(action):
        if not curve.data_path.endswith('pose.bones["Hips"].location'):
            continue
        point = curve.keyframe_points[frame - 1]
        original = float(point.co.y)
        point.co.y = original + amount
        changed = horizontal_separation(rig, action, frame)
        if changed >= baseline + amount * .75:
            return point, original, changed
        point.co.y = original
    raise AssertionError(f"could not inject horizontal Root/Hips split into {action.name}")


def role_oracle(rig: object, reference: dict[str, object], source: object, mapping: list[dict[str, int]]) -> dict[str, object]:
    oracle = _capture_pivot_transform_oracle(rig, source, [row["source_frame"] for row in mapping])
    oracle["reference_oracle"] = reference
    return oracle


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--variant", required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    evidence = json.loads(options.evidence.read_text(encoding="utf-8"))
    rig, _, rig_basis = _rig()
    reference = _capture_pivot_transform_oracle(rig, bpy.data.actions["pose_ref_idle"], [1])
    roles = {role.code: role for role in ROLE_REGISTRY}
    metrics: dict[str, object] = {}

    # Every requested role is compared at every persisted mapped frame. This
    # covers reflection and reversal through the role declaration itself.
    for code, row in evidence["roles"].items():
        role = roles[code]
        output = bpy.data.actions[action_name(options.variant, role)]
        mapping = row["generated_output"]["frame_mapping"]
        oracle = role_oracle(rig, reference, bpy.data.actions[row["selected"]], mapping)
        seam_rows = row["generated_output"].get("loop_seam_construction", [])
        if seam_rows and "repaired_transform_oracle" in seam_rows[0].get("construction", {}):
            persisted = seam_rows[0]["construction"]["repaired_transform_oracle"]
            oracle["repaired_transform_rows"] = tuple(
                {name: Matrix(values) for name, values in sample.items()}
                for sample in persisted["rows"]
            )
            oracle["repaired_root_translation"] = tuple(
                tuple(float(value) for value in sample)
                for sample in persisted["root_translation"]
            )
        # The staged materialiser already validated the frozen pre-mutation
        # oracle at 5e-4. A separately recaptured source after reopen carries a
        # measured Blender dependency-evaluation residual, so this independent
        # corruption harness uses 1 cm while retaining the strict staged result.
        result = _validate_role_transform_oracle(rig, output, role, oracle, "mirror" in role.derivation, 1e-2)
        staged = row["generated_output"]["reconstruction_fidelity"]
        assert staged["maximum_matrix_error"] <= staged["tolerance"], (code, staged)
        metrics[code] = {"family": role_projection_policy(role).family, "mapped_frames": len(mapping), "staged_oracle": staged, "reopened_recapture": result}
        if code == "sl":
            # Complete-event reversal must carry durable repaired evidence; a
            # fresh process cannot recover it by reclassifying sl as a repair.
            missing = dict(oracle)
            missing.pop("repaired_transform_rows", None)
            rejects(
                lambda: _validate_role_transform_oracle(rig, output, role, missing, False, 1e-2),
                "missing temporal-reversal repaired transform oracle",
            )
            corrupt = dict(oracle)
            corrupt_rows = list(oracle["repaired_transform_rows"])
            corrupt_row = dict(corrupt_rows[0])
            corrupt_row["Hips"] = Matrix.Translation(Vector((1.0, 0.0, 0.0))) @ corrupt_row["Hips"]
            corrupt_rows[0] = corrupt_row
            corrupt["repaired_transform_rows"] = tuple(corrupt_rows)
            rejects(
                lambda: _validate_role_transform_oracle(rig, output, role, corrupt, False, 1e-2),
                "corrupt temporal-reversal repaired transform oracle",
            )
            metrics[code]["durable_temporal_reversal_oracle"] = {
                "missing_rejected": True,
                "corrupt_rejected": True,
            }

    # A plausible Root curve with a 1.5 m Hips split must fail even though the
    # Root component itself remains policy-correct.
    walk_role = roles["wf_r"]
    walk = bpy.data.actions[action_name(options.variant, walk_role)]
    walk_row = evidence["roles"]["wf_r"]
    walk_oracle = role_oracle(rig, reference, bpy.data.actions[walk_row["selected"]], walk_row["generated_output"]["frame_mapping"])
    frame = max(2, int(walk.frame_range[1]) // 2)
    point, original, injected = mutate_horizontal_hips(rig, walk, frame, 1.5)
    rejects(lambda: _validate_role_transform_oracle(rig, walk, walk_role, walk_oracle, False, 1e-2), "1.5 m Root/Hips split")
    point.co.y = original
    metrics["injected_walk_split"] = {"frame": frame, "horizontal_separation": injected, "rejected": True}

    # A 3 m pivot orbit is the same ownership error at a yawed Root and must be
    # rejected in the first unit; repeat-cycle correctness is independently
    # covered by the pivot repeat oracle regression.
    pivot_role = roles["pr45"]
    pivot = bpy.data.actions[action_name(options.variant, pivot_role)]
    pivot_row = evidence["roles"]["pr45"]
    pivot_oracle = role_oracle(rig, reference, bpy.data.actions[pivot_row["selected"]], pivot_row["generated_output"]["frame_mapping"])
    frame = max(2, int(pivot.frame_range[1]) // 2)
    point, original, injected = mutate_horizontal_hips(rig, pivot, frame, 3.0)
    rejects(lambda: _validate_role_transform_oracle(rig, pivot, pivot_role, pivot_oracle, False, 1e-2), "3 m pivot orbit")
    point.co.y = original
    metrics["injected_pivot_orbit"] = {"frame": frame, "horizontal_radius": injected, "rejected": True}

    # User-named transitions and both arc directions receive independent torso
    # rocking and inherited-foot-carriage corruptions.
    for code in ("f2i_r", "b2i_r", "ar_l", "al_l"):
        if code not in evidence["roles"]:
            continue
        role = roles[code]
        output = bpy.data.actions[action_name(options.variant, role)]
        row = evidence["roles"][code]
        oracle = role_oracle(rig, reference, bpy.data.actions[row["selected"]], row["generated_output"]["frame_mapping"])
        frame = max(2, int(output.frame_range[1]) // 2)
        chest = next(curve for curve in _action_fcurves(output) if curve.data_path.endswith('pose.bones["Chest"].rotation_euler') and curve.array_index == 0)
        chest_point = chest.keyframe_points[frame - 1]
        chest_original = float(chest_point.co.y)
        chest_point.co.y = chest_original + .5
        rejects(lambda: _validate_role_transform_oracle(rig, output, role, oracle, "mirror" in role.derivation, 1e-2), f"{code} torso rocking")
        chest_point.co.y = chest_original
        if role_projection_policy(role).family == "walk_transition":
            point, original, _ = mutate_horizontal_hips(rig, output, frame, 1.0)
            rejects(lambda: _validate_body_carriage(output, role, rig, rig_basis, int(output.frame_range[1]), DEFAULT_TOLERANCES), f"{code} foot carriage")
            point.co.y = original
        else:
            foot_curve = next(curve for curve in _action_fcurves(output) if curve.data_path.endswith('pose.bones["LeftFoot"].location') and curve.array_index == 0)
            foot_point = foot_curve.keyframe_points[frame - 1]
            foot_original = float(foot_point.co.y)
            foot_point.co.y = foot_original + 1.0
            rejects(lambda: _validate_role_transform_oracle(rig, output, role, oracle, "mirror" in role.derivation, 1e-2), f"{code} foot carriage")
            foot_point.co.y = foot_original
        metrics[f"{code}_corruptions"] = {"torso_rocking_rejected": True, "foot_carriage_rejected": True}

    print("KIMODO_ROLE_FAMILY_ORACLE_JSON=" + json.dumps(metrics, sort_keys=True))


main()
