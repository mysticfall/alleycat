"""Regenerate test-only oracles from repository root (never from the C# runtime).

  blender --background --factory-startup --python-exit-code 1 --python tools/kimodo-pipeline/commands/regenerate_motion_frame_oracles.py -- blender
  <Kimodo Python> tools/kimodo-pipeline/commands/regenerate_motion_frame_oracles.py kimodo

Blender: evaluate the approved action/slot in the authored file, write raw pose
matrices as 12 little-endian float32 values (3 rows of 4) per joint and frame.
Kimodo: run the installed SOMA77 T-pose transforms on committed samples.
Neither output replaces a receipt for the unavailable historical inputs.
"""

import gzip
import hashlib
import json
from pathlib import Path
import struct
import sys


ROOT = Path(__file__).resolve().parents[3]
DIRECTORY = ROOT / "integration-tests/fixtures/motion-frame-oracles"
DATA = ROOT / "game/data/motionbricks"
SOURCE = DATA / "reviewed-feminine/landing-ablation-01.json"


def write_gzip(name, payload):
    (DIRECTORY / name).write_bytes(gzip.compress(payload, mtime=0))


def blender():
    import bpy

    bundle = json.loads(SOURCE.read_text())
    identity = bundle["descriptor"]["source"]
    path = ROOT / identity["path"]
    assert hashlib.sha256(path.read_bytes()).hexdigest() == identity["sha256"]
    bpy.ops.wm.open_mainfile(filepath=str(path))
    scene = bpy.context.scene
    rig = bpy.data.objects[identity["rig"]]
    action = bpy.data.actions[identity["action"]]
    assert list(action.frame_range) == identity["frames"]
    assert scene.render.fps / scene.render.fps_base == 30
    assert scene.unit_settings.scale_length == 1
    assert not rig.animation_data.nla_tracks
    rig.animation_data.action = action
    rig.animation_data.action_slot = next(slot for slot in action.slots if slot.identifier == identity["slot"])
    # Both complete role intervals plus the next Hips sample for finite-difference velocity.
    payload = bytearray()
    for frame in [*range(46, 108), *range(346, 408)]:
        scene.frame_set(frame)
        evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
        for name in bundle["names"]:
            matrix = evaluated.pose.bones[name].matrix
            payload.extend(struct.pack("<12f", *(matrix[r][c] for r in range(3) for c in range(4))))
    write_gzip("blender-matrices.f32.gz", payload)
    print("Blender", bpy.app.version_string, identity["action"], identity["sha256"])


def kimodo():
    from importlib.metadata import version

    import torch
    from scipy.spatial.transform import Rotation
    from kimodo.skeleton.definitions import SOMASkeleton77
    from kimodo.skeleton.transforms import from_standard_tpose, to_standard_tpose

    data = json.loads((DATA / "poses.json").read_text())
    source = json.loads(SOURCE.read_text())
    inputs = {
        "reviewed-forward": [sample["pose"] for sample in source["samples"][57:61]],
        "idle": data["clips"]["idle"]["frames"][:4],
        "rest": [data["rest"]],
    }
    skeleton = SOMASkeleton77()
    names = skeleton.bone_order_names
    indices = [data["names"].index(name) for name in names]
    fixtures = {}
    for label, poses in inputs.items():
        poses = [[pose[i] for i in indices] for pose in poses]
        rotations = Rotation.from_quat([p[3:] for pose in poses for p in pose]).as_matrix()
        canonical = torch.tensor(rotations, dtype=torch.float32).reshape(len(poses), len(names), 3, 3)
        # Exported offsets represent BONES from canonical via right multiplication; Kimodo's
        # from_standard_tpose transposes them before change_tpose's transpose contraction.
        model_local, model_world = from_standard_tpose(canonical, skeleton)
        round_trip, _ = to_standard_tpose(model_local, skeleton)
        fixtures[label] = {
            "input": poses,
            "model_local": model_local.tolist(),
            "model_world": model_world.tolist(),
            "canonical_local": round_trip.tolist(),
        }
    write_gzip("kimodo-rotations.json.gz", json.dumps({
        "kimodo_version": version("kimodo"), "names": names, "fixtures": fixtures,
    }, separators=(",", ":")).encode())
    print("Kimodo", version("kimodo"), "captured", list(fixtures))


if __name__ == "__main__":
    {"blender": blender, "kimodo": kimodo}[sys.argv[-1]]()
