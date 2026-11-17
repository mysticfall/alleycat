"""Read the calibrated setup and authored idle; never save or rebake source assets.

Run from repository root with Blender --background --python this/file.py.
Moving and turn sources use the separate maintained reviewed-source ingestion.
"""
import hashlib
import re
import sys
from pathlib import Path

import bpy
from mathutils import Matrix

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "game/data/motionbricks"
sys.path.insert(0, str(Path(__file__).parent))
from kimodo_pipeline.blender_retargeting import bind_action_slot
from kimodo_pipeline.motionbricks_publication import publish_motionbricks

SETUP = "game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend"
MAP = "game/assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres"
CLIPS = [
    ("idle", "game/assets/characters/reference/female/animations/source/idle/sad.blend", "kimodo_m0a3dyf0"),
]
hashes = {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in [SETUP, MAP, *[c[1] for c in CLIPS]]}
bpy.ops.wm.open_mainfile(filepath=str(ROOT / SETUP))
source, target = bpy.data.objects["Kimodo_Source"], bpy.data.objects["Female"]
names = [b.name for b in source.data.bones if not b.name.endswith(("_RET", "_LOOK")) and b.name != "Root"]
A = Matrix(((1, 0, 0), (0, 0, 1), (0, -1, 0)))
rests = {b.name: b.matrix_local.copy() for b in source.data.bones}
parents = [names.index(source.data.bones[n].parent.name) if source.data.bones[n].parent.name in names else -1 for n in names]


def matrix(m):
    return [list(row) for row in m]


def pose(p, q):
    return list(p) + [q.x, q.y, q.z, q.w]


rest = []
for i, n in enumerate(names):
    p = rests[n].translation.copy()
    if parents[i] >= 0:
        p -= rests[names[parents[i]]].translation
    rest.append(pose(A @ p, Matrix.Identity(3).to_quaternion()))
bone_map = dict(re.findall(r'bone_map/(\w+) = &"([^"]+)"', (ROOT / MAP).read_text()))
reverse = {v: k for k, v in bone_map.items()}
mapping = []
for b in target.data.bones:
    helper = source.pose.bones.get(b.name + "_RET")
    if helper is None or b.name == "Root":
        continue
    track = next((c for c in helper.constraints if c.type == "LOCKED_TRACK" and not c.mute), None)
    mapping.append(dict(target=reverse[b.name], source=names.index(helper.parent.name),
                        helper=matrix(helper.bone.matrix_local @ helper.matrix_basis),
                        targetRest=matrix(b.matrix_local), track=track.subtarget if track else "", length=helper.length))
looks = {b.name: dict(source=names.index(b.parent.name),
                     offset=matrix(rests[b.parent.name].inverted() @ b.bone.matrix_local @ b.matrix_basis), length=b.length)
         for b in source.pose.bones if b.name.endswith("_LOOK")}
data = dict(names=names, parents=parents, rest=rest, blenderRest=[matrix(rests[n]) for n in names],
            mapping=mapping, looks=looks, clips={}, sources=hashes)
for label, path, action_name in CLIPS:
    with bpy.data.libraries.load(str(ROOT / path), link=False) as (_, dst):
        dst.actions = [action_name]
    action = dst.actions[0]
    source.animation_data_create().action = action
    bind_action_slot(source.animation_data, action)
    frames = []
    for frame in range(int(action.frame_range[0]), int(action.frame_range[1]) + 1):
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        evaluated = source.evaluated_get(bpy.context.evaluated_depsgraph_get())
        world = [(A @ evaluated.pose.bones[n].matrix.translation,
                  A @ evaluated.pose.bones[n].matrix.to_3x3() @ rests[n].to_3x3().inverted() @ A.inverted()) for n in names]
        local = []
        for i, (p, r) in enumerate(world):
            if parents[i] >= 0:
                pp, pr = world[parents[i]]
                p, r = pr.inverted() @ (p - pp), pr.inverted() @ r
            local.append(pose(p, r.to_quaternion().normalized()))
        frames.append(local)
    assert len(frames) >= 4
    data["clips"][label] = dict(action=action_name, source=path, frames=frames, fps=30)
assert all(hashlib.sha256((ROOT / p).read_bytes()).hexdigest() == h for p, h in hashes.items())
publish_motionbricks(OUT / "poses.json", data, "poses")
print("Exported unchanged authored sources", {k: len(v["frames"]) for k, v in data["clips"].items()})
