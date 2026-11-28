"""Export source-owned model frames using the installed Kimodo package; no evaluation clips."""
from pathlib import Path
from kimodo.skeleton.definitions import SOMASkeleton77
from kimodo_pipeline.motionbricks_publication import publish_motionbricks

out = Path(__file__).resolve().parents[2] / "game/assets/animations/motionbricks"
skeleton = SOMASkeleton77()
publish_motionbricks(out / "model-frames.json", dict(
    names=skeleton.bone_order_names, offsets=skeleton.global_rot_offsets.tolist()), "model-frames")
