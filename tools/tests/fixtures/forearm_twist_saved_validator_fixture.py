"""Build disposable, physically saved Blender inputs for the ordinary validator."""

import sys
from pathlib import Path

import bpy

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools"))
import forearm_twist_generator_run_ownership as ownership


def make_mesh(name, armature, before, after, source_domain):
    data = bpy.data.meshes.new(name)
    data.from_pydata([(0.0, 0.0, 0.0)], [], [])
    mesh = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(mesh)
    modifier = mesh.modifiers.new("Skin", "ARMATURE")
    modifier.object = armature
    for group_name, weight in after.items():
        mesh.vertex_groups.new(name=group_name).add([0], weight, "REPLACE")
    return {
        "name": name,
        "vertex_count": 1,
        "axial_rows": [{"vertex": 0, "weights": before}],
        "authoring_eligible": {
            "LeftForearmTwist": [0] if before.get("LeftForearmTwist", 0.0) > 0 else [],
            "RightForearmTwist": [],
        },
        "source_domain": {"LeftForearmTwist": [0] if source_domain else [], "RightForearmTwist": []},
        "original_zero_rows": [],
        "original_positive_rows": [{"vertex": 0, "positives": list(before)}],
    }


def build(path, scenario):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    arm_data = bpy.data.armatures.new("Rig")
    arm = bpy.data.objects.new("Rig", arm_data)
    bpy.context.collection.objects.link(arm)
    bpy.context.view_layer.objects.active = arm
    arm.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for suffix, helper in (
        ("l", "LeftForearmTwist"),
        ("r", "RightForearmTwist"),
    ):
        lower = arm_data.edit_bones.new(f"lowerarm_{suffix}")
        lower.head, lower.tail = (0, 0, 0), (0, 1, 0)
        twist = arm_data.edit_bones.new(helper)
        twist.head, twist.tail, twist.parent = (0, 1, 0), (0, 2, 0), lower
        hand = arm_data.edit_bones.new(f"hand_{suffix}")
        hand.head, hand.tail, hand.parent = (0, 2, 0), (0, 3, 0), twist
    bpy.ops.object.mode_set(mode="OBJECT")

    supported = {"lowerarm_l": 0.4, "LeftForearmTwist": 0.2, "hand_l": 0.4}
    missing = {"lowerarm_l": 0.4, "hand_l": 0.6}
    protected = {"lowerarm_l": 0.4, "lowerarm_r": 0.1, "hand_l": 0.5}
    body = missing if scenario == "body_missing" else supported
    records = [make_mesh("Fixture.body", arm, body, body, True)]
    if scenario != "body_missing":
        garment = protected if scenario == "protected" else (
            {"hand_l": 1.0} if scenario == "unowned" else missing
        )
        records.append(make_mesh("Fixture.clothing", arm, garment, garment,
                                 scenario == "clothing_missing"))
    bpy.ops.wm.save_as_mainfile(filepath=str(path))
    ownership.write_evidence(ROOT, path, "alleycat_female", records)


if __name__ == "__main__":
    output = Path(sys.argv[sys.argv.index("--") + 1])
    for scenario in ("clothing_missing", "unowned", "protected", "body_missing"):
        build(output / f"{scenario}.blend", scenario)
