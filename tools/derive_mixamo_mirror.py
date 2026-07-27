#!/usr/bin/env python3
"""Create an ANIM-001 post-retarget sagittal mirror and freshly sampled metrics.

This is intentionally a Blender worker: runtime code never mirrors pose, contacts, or Root motion.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
import retarget_mixamo_animation as retarget  # noqa: E402


def paired_name(name: str) -> str:
    if "Left" in name:
        return name.replace("Left", "Right")
    if "Right" in name:
        return name.replace("Right", "Left")
    if "_l" in name:
        return name.replace("_l", "_r")
    if "_r" in name:
        return name.replace("_r", "_l")
    return name


def mirror_curve_value(data_path: str, index: int, value: float) -> float:
    # Reflection S=diag(-1, 1, 1), applied to every sampled pose in armature world space.
    # Location X and quaternion X/Z change sign under S R S; Root yaw is therefore negated.
    if data_path.endswith("location") and index == 0:
        return -value
    if data_path.endswith("rotation_quaternion") and index in (1, 3):
        return -value
    if data_path.endswith("rotation_euler") and index in (1, 2):
        return -value
    return value


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--blend", required=True)
    parser.add_argument("--source-action", required=True)
    parser.add_argument("--derived-action", required=True)
    parser.add_argument("--source-motion-id", required=True)
    parser.add_argument("--metrics-output", required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])

    blend_path = Path(args.blend).resolve()
    source_hash = hashlib.sha256(blend_path.read_bytes()).hexdigest()
    recipe = {
        "version": 1,
        "type": "sagittal_world_matrix_reflection",
        "reflection": "S=diag(-1,1,1); M'=S*M*S",
        "paired_bones": "Left<->Right and _l<->_r",
        "root": "Root.location.x and yaw negated",
        "metrics": "freshly sampled from derived action; foot channels follow swapped pose",
    }
    recipe_hash = hashlib.sha256(json.dumps(recipe, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    bpy.ops.wm.open_mainfile(filepath=str(blend_path))
    source = bpy.data.actions.get(args.source_action)
    if source is None:
        raise RuntimeError(f"missing source action {args.source_action}")
    existing = bpy.data.actions.get(args.derived_action)
    if existing:
        bpy.data.actions.remove(existing, do_unlink=True)
    # Blender 5 stores action curves in layered channel bags. Copying preserves their
    # interpolation, handles, slot bindings and loop boundary exactly before reflection.
    derived = source.copy()
    derived.name = args.derived_action
    derived.use_fake_user = True
    channel_bag = derived.layers[0].strips[0].channelbags[0]
    for curve in channel_bag.fcurves:
        path = curve.data_path
        if 'pose.bones["' in path:
            start = path.index('pose.bones["') + len('pose.bones["')
            end = path.index('"]', start)
            path = path[:start] + paired_name(path[start:end]) + path[end:]
        curve.data_path = path
        for point in curve.keyframe_points:
            point.co.y = mirror_curve_value(curve.data_path, curve.array_index, point.co.y)
    derived.frame_range = source.frame_range
    armature = next((obj for obj in bpy.data.objects if obj.type == "ARMATURE"), None)
    if armature is None:
        raise RuntimeError("mirror source has no armature")
    armature.animation_data_create().action = derived
    # Reuse ANIM-001's metric sampler; this evaluates the derived action rather than copying source JSON.
    row = retarget.MotionManifestRow(args.source_motion_id, "Derived mirror", "Derived from vetted natural source", "Motion", "")
    root_metadata = retarget.RootMotionMetadata("reconstructed_root", True, diagnostics={"derived_mirror": recipe})
    canonical_action = args.derived_action.removesuffix("-loop")
    metrics = retarget.extract_motion_metrics(bpy.context.scene, armature, derived, row, root_metadata, canonical_action)
    metrics["derived_provenance"] = {
        "derived_identity": canonical_action,
        "source_motion_id": args.source_motion_id,
        # ``-loop`` is an internal Blender/import marker, never portable provenance.
        "source_action": args.source_action.removesuffix("-loop"),
        "derivation_type": "sagittal_world_matrix_reflection",
        "canonical_reflection_recipe": recipe,
        "source_artifact_sha256": source_hash,
        "recipe_sha256": recipe_hash,
    }
    Path(args.metrics_output).write_text(json.dumps(metrics, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    bpy.ops.wm.save_as_mainfile(filepath=str(blend_path), check_existing=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
