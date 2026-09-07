"""Seed doctored workspace-bootstrap fixture inputs for exit-2 contract tests."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import bpy

from kimodo_pipeline.bootstrap_manifest import _action_fcurves
from kimodo_pipeline.bootstrap_validation import derive_rotation_conventions
from kimodo_pipeline.bootstrap_workspace import _curve_records
from kimodo_pipeline.compatibility import ensure_registered


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, help="Template copy to doctor.")
    parser.add_argument("--output", required=True, help="Doctored blend to write.")
    parser.add_argument(
        "--mode",
        required=True,
        choices=(
            "multi-frame-idle",
            "prompted-template",
            "hidden-mesh-setup",
            "idle-missing-axis",
        ),
    )
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(arguments)


def main() -> int:
    arguments = _arguments()
    ensure_registered()
    bpy.ops.wm.open_mainfile(
        filepath=arguments.input, load_ui=False, use_scripts=False
    )
    if arguments.mode == "multi-frame-idle":
        action = bpy.data.actions.get("pose_ref_idle")
        if action is None:
            print("pose_ref_idle is missing from the seed input", file=sys.stderr)
            return 1
        fcurve = _action_fcurves(action)[0]
        fcurve.keyframe_points.add(1)
        key = fcurve.keyframe_points[-1]
        key.co = (5.0, fcurve.keyframe_points[0].co[1] + 0.125)
    elif arguments.mode == "idle-missing-axis":
        idle = bpy.data.actions.get("pose_ref_idle")
        turns = sorted(
            (action for action in bpy.data.actions if action.name.startswith("pose_ref_turn_")),
            key=lambda action: action.name,
        )
        if idle is None or not turns:
            print("the seed input lacks pose_ref_idle or turn references", file=sys.stderr)
            return 1
        conventions = derive_rotation_conventions(
            _curve_records(idle),
            {action.name: _curve_records(action) for action in turns},
        )
        axis = (conventions["axis_data_path"], int(conventions["axis_array_index"]))
        target = [
            fcurve
            for fcurve in _action_fcurves(idle)
            if (fcurve.data_path, int(fcurve.array_index)) == axis
        ]
        if not target:
            print(f"no {axis} curves exist to remove", file=sys.stderr)
            return 1
        # The legacy fcurves view (when present) proxies the same curves as the
        # layered channel bags but without stable object identity, so removal
        # matches on the channel value rather than the fcurve object.
        legacy = getattr(idle, "fcurves", None)
        containers = (
            [legacy]
            if legacy is not None and len(legacy) > 0
            else [
                bag.fcurves
                for layer in getattr(idle, "layers", ())
                for strip in getattr(layer, "strips", ())
                for bag in getattr(strip, "channelbags", ())
            ]
        )
        removed = 0
        for container in containers:
            for fcurve in list(container):
                if (fcurve.data_path, int(fcurve.array_index)) == axis:
                    container.remove(fcurve)
                    removed += 1
        if removed != len(target):
            print(
                f"expected to remove {len(target)} {axis} curves; removed {removed}",
                file=sys.stderr,
            )
            return 1
    elif arguments.mode == "hidden-mesh-setup":
        meshes = sorted(
            (obj for obj in bpy.data.objects if obj.type == "MESH"),
            key=lambda obj: obj.name,
        )
        if not meshes:
            print("the seed input carries no mesh objects", file=sys.stderr)
            return 1
        meshes[0].hide_viewport = True
    else:
        segments = bpy.context.scene.kimodo.motion_segments
        if len(segments) == 0:
            print("the seed input carries no segments", file=sys.stderr)
            return 1
        segments[0].prompt = "a woman walks"
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=arguments.output, check_existing=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
