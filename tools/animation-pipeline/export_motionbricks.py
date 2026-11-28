"""Read the calibrated setup's canonical SOMA skeleton; never save or rebake source assets.

Run from repository root with Blender --background --python this/file.py.
Moving and turn sources use the separate maintained reviewed-source ingestion; catalogue idles are native clips.
"""
import hashlib
import sys
from pathlib import Path

import bpy
from mathutils import Matrix

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "game/data/motionbricks"
sys.path.insert(0, str(Path(__file__).parent))
from kimodo_pipeline.motionbricks_publication import publish_motionbricks

SETUP = "game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend"
MAP = "game/assets/characters/reference/skeleton_profiles/bone_map_makehuman.tres"
hashes = {p: hashlib.sha256((ROOT / p).read_bytes()).hexdigest() for p in [SETUP, MAP]}
bpy.ops.wm.open_mainfile(filepath=str(ROOT / SETUP))
source = bpy.data.objects["Kimodo_Source"]
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
data = dict(names=names, parents=parents, rest=rest, blenderRest=[matrix(rests[n]) for n in names], sources=hashes)
assert all(hashlib.sha256((ROOT / p).read_bytes()).hexdigest() == h for p, h in hashes.items())
publish_motionbricks(OUT / "poses.json", data, "poses")
print("Exported unchanged authored sources", len(names), "joints")
