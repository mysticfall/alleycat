"""Render evaluated starts and motion traces for the four sad strafe loops."""
from __future__ import annotations

import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))
from kimodo_pipeline.discovery import _source_rig
from kimodo_pipeline.materialise import _activate_action

OUTPUT = ROOT / "game/temp/kimodo-strafe-phase-visual"
PREFIX = "Kimodo Strafe Evidence "
ROLES = ("xr_l", "xr_r", "xl_l", "xl_r")


def look_at(item: object, target: Vector) -> None:
    item.rotation_euler = (target - item.location).to_track_quat("-Z", "Y").to_euler()


def material(name: str, colour: tuple[float, float, float, float]) -> object:
    value = bpy.data.materials.new(PREFIX + name)
    value.diffuse_color = colour
    return value


def line(name: str, points: list[Vector], colour: object, width: float = .012) -> object:
    data = bpy.data.curves.new(PREFIX + name, "CURVE"); data.dimensions = "3D"; data.bevel_depth = width
    spline = data.splines.new("POLY"); spline.points.add(len(points) - 1)
    for point, value in zip(spline.points, points): point.co = (*value, 1.0)
    data.materials.append(colour)
    result = bpy.data.objects.new(PREFIX + name, data); bpy.context.scene.collection.objects.link(result)
    return result


def label(name: str, text: str, point: Vector, camera: object, colour: object) -> None:
    data = bpy.data.curves.new(PREFIX + name, "FONT"); data.body = text; data.size = .12; data.align_x = "CENTER"; data.materials.append(colour)
    result = bpy.data.objects.new(PREFIX + name, data); bpy.context.scene.collection.objects.link(result)
    result.location = point; look_at(result, camera.location); result.rotation_euler.rotate_axis("Y", 3.14159265)


def point(rig: object, bone: str) -> Vector:
    evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
    return evaluated.matrix_world @ evaluated.pose.bones[bone].matrix.translation


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    rig = _source_rig(); scene = bpy.context.scene
    camera_data = bpy.data.cameras.new(PREFIX + "Camera"); camera_data.type = "ORTHO"; camera_data.ortho_scale = 3.0
    camera = bpy.data.objects.new(PREFIX + "Camera", camera_data); bpy.context.scene.collection.objects.link(camera)
    camera.location = Vector((3.8, -5.0, 2.4)); scene.camera = camera
    scene.render.engine = "BLENDER_WORKBENCH"; scene.display.shading.light = "STUDIO"; scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x = scene.render.resolution_y = 720; scene.render.resolution_percentage = 100; scene.render.image_settings.file_format = "PNG"
    colours = {"LeftFoot": material("Left", (1, .72, 0, 1)), "RightFoot": material("Right", (1, .05, .75, 1)), "Body": material("Body", (0, .8, 1, 1))}
    report: dict[str, object] = {"roles": {}, "cue": "yellow=LeftFoot, magenta=RightFoot; traces run from frame 1 to the recurrence boundary"}
    for role in ROLES:
        action = bpy.data.actions[f"loc_sad_{role}_loop"]; _activate_action(rig, action)
        end = int(action.frame_range[1]); frames = list(range(1, end + 1))
        traces = {bone: [] for bone in ("LeftFoot", "RightFoot")}
        for frame in frames:
            scene.frame_set(frame); bpy.context.view_layer.update()
            for bone in traces: traces[bone].append(point(rig, bone).copy())
        scene.frame_set(1); bpy.context.view_layer.update(); hips = point(rig, "Hips")
        target = hips + Vector((0, 0, -.3)); look_at(camera, target)
        line(role + " body", [hips, point(rig, "Chest"), point(rig, "Head")], colours["Body"])
        for bone, samples in traces.items(): line(role + bone, samples, colours[bone], .009)
        label(role, f"{role}  F1→F{end}", target + Vector((0, 0, .65)), camera, colours["Body"])
        path = OUTPUT / f"sad_{role}.png"; scene.render.filepath = str(path); bpy.ops.render.render(write_still=True)
        report["roles"][role] = {"image": str(path), "end_frame": end, "left_start": list(traces["LeftFoot"][0]), "right_start": list(traces["RightFoot"][0])}
        for object_ in list(bpy.data.objects):
            if object_.name.startswith(PREFIX) and object_ != camera: bpy.data.objects.remove(object_, do_unlink=True)
    (OUTPUT / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__": main()
