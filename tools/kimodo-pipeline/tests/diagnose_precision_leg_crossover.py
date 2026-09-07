"""Read-only ANIM-002 precision-step anatomy diagnostic.

Run this through Blender against a source-to-Female materialisation file. It does
not alter the opened blend; its only output is the explicitly requested report.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.leg_silhouette import assess_precision_leg_silhouette


CHAINS = (
    ("Kimodo", ("LeftLeg", "LeftShin", "LeftFoot"), ("RightLeg", "RightShin", "RightFoot")),
    ("Female", ("thigh_l", "calf_l", "foot_l"), ("thigh_r", "calf_r", "foot_r")),
)


def _chain_for(rig: bpy.types.Object) -> tuple[tuple[str, ...], tuple[str, ...]] | None:
    for _, left, right in CHAINS:
        if all(rig.pose.bones.get(name) is not None for name in (*left, *right, "Root")):
            return left, right
    return None


def _axis(rig: bpy.types.Object, left_name: str, right_name: str):
    root = rig.pose.bones["Root"].bone.matrix_local
    up = root.to_3x3().col[2].normalized()
    left_rest = rig.pose.bones[left_name].bone.matrix_local.translation
    right_rest = rig.pose.bones[right_name].bone.matrix_local.translation
    lateral = left_rest - right_rest
    lateral -= up * lateral.dot(up)
    return lateral.normalized(), up


def _at_height(first, second, height: float):
    difference = second[1] - first[1]
    if abs(difference) <= 1e-9:
        return min(first[0], second[0])
    ratio = (height - first[1]) / difference
    return first[0] + (second[0] - first[0]) * ratio


def _sample(rig: bpy.types.Object, action: bpy.types.Action, left_names: tuple[str, ...], right_names: tuple[str, ...]) -> dict:
    data = rig.animation_data_create()
    assert data is not None
    slot = next((item for item in action.slots if item.target_id_type == rig.id_type), None)
    if slot is None:
        raise RuntimeError(f"{action.name} has no Action slot compatible with {rig.name}")
    data.action = action
    data.action_slot = slot
    lateral, up = _axis(rig, left_names[0], right_names[0])
    start, end = (int(value) for value in action.frame_range)
    rows = []
    for frame in range(start, end + 1):
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        root_inverse = rig.pose.bones["Root"].matrix.inverted_safe()
        positions = {
            name: root_inverse @ rig.pose.bones[name].matrix.translation
            for name in (*left_names, *right_names)
        }
        left = [(float(positions[name].dot(lateral)), float(positions[name].dot(up))) for name in left_names]
        right = [(float(positions[name].dot(lateral)), float(positions[name].dot(up))) for name in right_names]
        point_order = [left[index][0] - right[index][0] for index in range(3)]
        rows.append({
            "frame": frame,
            "joint_signed_separations": dict(zip(("hip", "knee", "ankle"), point_order)),
            "minimum_joint_signed_separation": min(point_order),
            "left_chain": left,
            "right_chain": right,
        })
    minimum_joint = min(rows, key=lambda row: (row["minimum_joint_signed_separation"], row["frame"]))
    scale = max(
        *(rig.pose.bones[name].bone.length for name in (*left_names[:2], *right_names[:2])),
    )
    silhouette = assess_precision_leg_silhouette(
        "sr",
        [(row["frame"], row.pop("left_chain"), row.pop("right_chain")) for row in rows],
        scale,
        stage="female_bake" if rig.name == "Female" else "immutable_pre_repair",
        armature_identity=rig.name,
        basis_evidence={"basis": "Root-relative rest-derived positive-left lateral and Root-rest up"},
    )
    return {
        "frames": rows,
        "minimum_joint": minimum_joint,
        "leg_silhouette": silhouette,
        "accepted_non_crossed": silhouette["accepted"],
        "basis": "Root-relative rest-derived positive-left lateral and Root-rest up",
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", required=True)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:])
    report = {"contract": "precision_step_anatomy_diagnostic_v1", "stages": {}}
    actions = [action for action in bpy.data.actions if action.name in {"loc_sad_sr_loop", "loc_sad_sl_loop", "loc_sad_sr-loop", "loc_sad_sl-loop"}]
    for rig in (item for item in bpy.data.objects if item.type == "ARMATURE"):
        chain = _chain_for(rig)
        if chain is None:
            continue
        for action in actions:
            if not all(rig.pose.bones.get(name) is not None for name in (*chain[0], *chain[1])):
                continue
            key = f"{rig.name}/{action.name}"
            try:
                report["stages"][key] = _sample(rig, action, *chain)
            except (KeyError, RuntimeError) as error:
                report["stages"][key] = {"error": str(error)}
    Path(options.report).write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
    print("ANIM002_PRECISION_ANATOMY_DIAGNOSTIC=" + json.dumps(report, sort_keys=True))


main()
