"""Seed disposable blends with unmistakably stale generated target Actions."""
from __future__ import annotations

from pathlib import Path
import sys

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.compatibility import ensure_registered
from kimodo_pipeline.materialise import ROOT as ROOT_PATH, _rig


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    blend, names = Path(arguments[0]).resolve(), arguments[1:]
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(blend), load_ui=False, use_scripts=False)
    rig, _, _ = _rig()
    for name in names:
        existing = bpy.data.actions.get(name)
        if existing is not None:
            bpy.data.actions.remove(existing, do_unlink=True)
        action = bpy.data.actions.new(name)
        action.use_fake_user = True
        action["anim002_stale_fixture"] = True
        action["anim002_root_translation_trajectory"] = "stale_fixture"
        action["anim002_stale_payload"] = [99.0, -42.0, 7.0]
        slot = action.slots.new(rig.id_type, rig.name)
        bag = action.layers.new("STALE").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
        curve = bag.fcurves.new(data_path=f"{ROOT_PATH}.location", index=0)
        curve.keyframe_points.add(2)
        for index, value in enumerate((99.0, 101.0)):
            curve.keyframe_points[index].co = index + 1, value
            curve.keyframe_points[index].interpolation = "CONSTANT"
        curve.modifiers.new("NOISE").strength = 12.0
    bpy.ops.wm.save_as_mainfile(filepath=str(blend), check_existing=False)


main()
