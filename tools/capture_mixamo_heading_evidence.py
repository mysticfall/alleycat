#!/usr/bin/env python3
"""Render reproducible top-down evaluated Root and torso heading evidence.

Example:
  blender --factory-startup --background --python tools/capture_mixamo_heading_evidence.py -- \
    --processed game/assets/characters/reference/female/animations/processed/mixamo/locomotion_standing.blend \
    --source-dir ~/workspace/mixamo/download --output-dir game/temp/ANIM-001/pivot_heading

The render deliberately uses evaluated armature arrows rather than a mesh: processed group blends retain
the reusable armature/actions but intentionally remove meshes. Blue is evaluated physical Root +Y/tail,
orange is the processed bilateral-clavicle torso heading, and green is the source bilateral-shoulder
torso heading.  Raw Root channels are recorded only as representation data, never used as visual proof.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

REPRESENTATIVES = {
    "pivot_left": "c9ceef5f-b96c-11e4-a802-0aaa78deedf9",
    "pivot_right": "c9cef01d-b96c-11e4-a802-0aaa78deedf9",
    "arc_left": "c9ccf8d5-b96c-11e4-a802-0aaa78deedf9",
}


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--processed", required=True)
    parser.add_argument("--source-dir", required=True)
    parser.add_argument("--output-dir", required=True)
    values = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(values)


def heading_from_forward(vector: Vector) -> float:
    return math.atan2(float(vector.x), float(vector.y))


def torso_heading(obj: bpy.types.Object, names: tuple[tuple[str, ...], tuple[str, ...]]) -> float:
    left = bone(obj, names[0]).matrix.translation
    right = bone(obj, names[1]).matrix.translation
    return heading_from_forward(Vector((0.0, 0.0, 1.0)).cross(right - left))


def evaluated_root_heading(root) -> float:
    return heading_from_forward(root.matrix.to_3x3() @ Vector((0.0, 1.0, 0.0)))


def wrapped_delta(start: float, end: float) -> float:
    return (end - start + math.pi) % math.tau - math.pi


def armature() -> bpy.types.Object:
    return next(obj for obj in bpy.context.scene.objects if obj.type == "ARMATURE")


def bone(obj: bpy.types.Object, names: tuple[str, ...]):
    for name in names:
        candidate = obj.pose.bones.get(name)
        if candidate is not None:
            return candidate
    raise RuntimeError(f"missing expected bone; tried {names}")


def clear_scene() -> None:
    # Do not call read_factory_settings while a windowed capture script is running:
    # Blender 5's FBX importer then loses its active-object context.  Explicitly
    # removing scene objects leaves a valid window/view layer for the next import.
    for obj in list(bpy.context.scene.objects):
        bpy.data.objects.remove(obj, do_unlink=True)
    bpy.ops.object.empty_add(type="PLAIN_AXES")


def arrow(name: str, origin: Vector, angle: float, colour: tuple[float, float, float, float]) -> None:
    direction = Vector((math.sin(angle), math.cos(angle), 0.0))
    bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=0.035, depth=1.05, location=origin + direction * 0.525)
    shaft = bpy.context.view_layer.objects.active
    shaft.name = name
    shaft.rotation_mode = "QUATERNION"
    shaft.rotation_quaternion = Vector((0.0, 0.0, 1.0)).rotation_difference(direction)
    material = bpy.data.materials.new(name)
    material.diffuse_color = colour
    shaft.data.materials.append(material)
    bpy.ops.mesh.primitive_cone_add(vertices=16, radius1=0.13, radius2=0.0, depth=0.30, location=origin + direction * 1.18)
    tip = bpy.context.view_layer.objects.active
    tip.rotation_mode = "QUATERNION"
    tip.rotation_quaternion = Vector((0.0, 0.0, 1.0)).rotation_difference(direction)
    tip.data.materials.append(material)


def render(path: Path, source_heading: float, processed_heading: float, root_heading: float) -> None:
    clear_scene()
    arrow("SourceTorsoHeading_Green", Vector((-0.55, 0.0, 0.0)), source_heading, (0.02, 0.85, 0.20, 1.0))
    arrow("ProcessedTorsoHeading_Orange", Vector((0.0, 0.0, 0.0)), processed_heading, (1.0, 0.25, 0.02, 1.0))
    arrow("EvaluatedRootForward_Blue", Vector((0.55, 0.0, 0.0)), root_heading, (0.02, 0.55, 1.0, 1.0))
    bpy.ops.object.camera_add(location=(0.0, 0.0, 6.0))
    camera = bpy.context.view_layer.objects.active
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = 3.2
    # Default rotation points the camera's local -Z straight down from +Z.
    bpy.context.scene.camera = camera
    bpy.context.scene.render.engine = "BLENDER_WORKBENCH"
    bpy.context.scene.display.shading.light = "STUDIO"
    bpy.context.scene.display.shading.background_type = "WORLD"
    bpy.context.scene.display.shading.background_color = (0.08, 0.08, 0.08)
    bpy.context.scene.render.resolution_x = 512
    bpy.context.scene.render.resolution_y = 512
    bpy.context.scene.render.resolution_percentage = 100
    bpy.context.scene.render.filepath = str(path)
    bpy.ops.wm.save_as_mainfile(filepath=str(path.with_suffix(".blend")), check_existing=False)
    bpy.ops.render.render(write_still=True)


def source_measurement(path: Path) -> tuple[float, float]:
    clear_scene()
    bpy.ops.import_scene.fbx(filepath=str(path), use_anim=True)
    obj = armature()
    action = obj.animation_data.action
    start, end = map(round, action.frame_range)
    bpy.context.scene.frame_set(start)
    start_heading = torso_heading(obj, (("LeftShoulder", "mixamorig:LeftShoulder"), ("RightShoulder", "mixamorig:RightShoulder")))
    bpy.context.scene.frame_set(end)
    end_heading = torso_heading(obj, (("LeftShoulder", "mixamorig:LeftShoulder"), ("RightShoulder", "mixamorig:RightShoulder")))
    return start_heading, end_heading


def processed_measurement(path: Path, motion_id: str) -> tuple[float, float, float, float, float, float]:
    bpy.ops.wm.open_mainfile(filepath=str(path))
    obj = armature()
    base = f"mixamo_{motion_id.replace('-', '_')}"
    action = bpy.data.actions.get(base) or bpy.data.actions.get(f"{base}-loop")
    if action is None:
        raise RuntimeError(f"missing processed action {base}")
    obj.animation_data_create().action = action
    start, end = map(round, action.frame_range)
    root = bone(obj, ("Root",))
    bpy.context.scene.frame_set(start)
    body_start = torso_heading(obj, (("clavicle_l", "upperarm_l"), ("clavicle_r", "upperarm_r")))
    root_start = evaluated_root_heading(root)
    root_euler_start = float(root.matrix.to_euler("XYZ").z)
    bpy.context.scene.frame_set(end)
    return body_start, torso_heading(obj, (("clavicle_l", "upperarm_l"), ("clavicle_r", "upperarm_r"))), root_start, evaluated_root_heading(root), root_euler_start, float(root.matrix.to_euler("XYZ").z)


def main() -> int:
    args = arguments()
    processed = Path(args.processed).expanduser().resolve()
    source_dir = Path(args.source_dir).expanduser().resolve()
    output = Path(args.output_dir).expanduser().resolve()
    output.mkdir(parents=True, exist_ok=True)
    proof = {}
    for label, motion_id in REPRESENTATIVES.items():
        source_start, source_end = source_measurement(source_dir / f"{motion_id}.fbx")
        body_start, body_end, root_start, root_end, root_euler_start, root_euler_end = processed_measurement(processed, motion_id)
        render(output / f"{label}_start.png", source_start, body_start, root_start)
        render(output / f"{label}_end.png", source_end, body_end, root_end)
        source_delta = wrapped_delta(source_start, source_end)
        processed_root_delta = wrapped_delta(root_start, root_end)
        proof[label] = {
            "motion_id": motion_id,
            "source_body_heading_delta_radians": source_delta,
            "processed_body_heading_delta_radians": wrapped_delta(body_start, body_end),
            "processed_evaluated_root_physical_heading_delta_radians": processed_root_delta,
            "processed_root_euler_z_start_end_radians": [root_euler_start, root_euler_end],
            "direction_agrees": source_delta * processed_root_delta > 0.0,
        }
    (output / "heading_proof.json").write_text(json.dumps(proof, indent=2) + "\n", encoding="utf-8")
    if not all(value["direction_agrees"] for value in proof.values()):
        raise RuntimeError(f"source/processed direction disagreement: {proof}")
    print(f"ANIM001_HEADING_EVIDENCE {output / 'heading_proof.json'}")
    bpy.ops.wm.quit_blender()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
