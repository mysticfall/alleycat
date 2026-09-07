"""Render fixed-world, evaluated Segment-2 pivot evidence without saving.

The lower three-quarter captures deliberately identify each evaluated foot by
bone name, endpoints, and its complete first-unit trajectory.  They are visual
evidence, not an alternative materialisation path.
"""
from __future__ import annotations

import hashlib
import json
import math
import sys
from pathlib import Path
from typing import Any

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))

from kimodo_pipeline.discovery import _source_rig
from kimodo_pipeline.materialise import _activate_action

OUTPUT = ROOT / "game/temp/kimodo-pivot-repair-visual"
CREATED_PREFIX = "Kimodo Pivot Evidence "
FOOT_BONES = ("LeftFoot", "RightFoot")
OUTPUT_SAMPLES = [1, 25, *range(43, 51), 51, 75, 99]


def look_at(item: Any, target: Vector) -> None:
    item.rotation_euler = (target - item.location).to_track_quat("-Z", "Y").to_euler()


def material(name: str, colour: tuple[float, float, float, float]) -> Any:
    result = bpy.data.materials.new(f"{CREATED_PREFIX}{name}")
    result.diffuse_color = colour
    result.use_nodes = True
    principled = result.node_tree.nodes["Principled BSDF"]
    principled.inputs["Base Color"].default_value = colour
    principled.inputs["Emission Color"].default_value = colour
    principled.inputs["Emission Strength"].default_value = 0.35
    return result


def polyline(name: str, points: list[Vector], width: float, colour: Any) -> Any:
    curve = bpy.data.curves.new(f"{CREATED_PREFIX}{name}", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = width
    spline = curve.splines.new("POLY")
    spline.points.add(len(points) - 1)
    for point, position in zip(spline.points, points):
        point.co = (*position, 1.0)
    curve.materials.append(colour)
    object_ = bpy.data.objects.new(f"{CREATED_PREFIX}{name}", curve)
    bpy.context.scene.collection.objects.link(object_)
    return object_


def set_polyline(object_: Any, points: list[Vector]) -> None:
    for point, position in zip(object_.data.splines[0].points, points):
        point.co = (*position, 1.0)


def marker(name: str, position: Vector, radius: float, colour: Any) -> Any:
    bpy.ops.mesh.primitive_uv_sphere_add(segments=16, ring_count=8, radius=radius, location=position)
    object_ = bpy.context.object
    object_.name = f"{CREATED_PREFIX}{name}"
    object_.data.materials.append(colour)
    return object_


def motion_arrow(name: str, start: Vector, end: Vector, colour: Any) -> None:
    """Draw an evaluated start-to-end motion vector with an arrowhead."""
    direction = end - start
    if direction.length <= 0.0001:
        raise RuntimeError(f"{name} has no visible evaluated motion vector")
    polyline(f"{name} Shaft", [start, end], 0.006, colour)
    bpy.ops.mesh.primitive_cone_add(vertices=12, radius1=0.026, depth=0.075, location=end)
    arrowhead = bpy.context.object
    arrowhead.name = f"{CREATED_PREFIX}{name} Arrowhead"
    arrowhead.data.materials.append(colour)
    arrowhead.rotation_mode = "QUATERNION"
    arrowhead.rotation_quaternion = direction.to_track_quat("Z", "Y")


def text_label(name: str, body: str, colour: Any) -> Any:
    data = bpy.data.curves.new(f"{CREATED_PREFIX}{name}", "FONT")
    data.body = body
    data.align_x = "LEFT"
    data.align_y = "CENTER"
    data.size = 0.08
    data.extrude = 0.002
    data.materials.append(colour)
    object_ = bpy.data.objects.new(f"{CREATED_PREFIX}{name}", data)
    bpy.context.scene.collection.objects.link(object_)
    return object_


def set_label(label: Any, leader: Any, point: Vector, offset: Vector, camera: Any) -> None:
    """Attach a readable label and leader to the evaluated point for this frame."""
    label.location = point + offset
    # The text front is local -Z in Blender's offline renderer, so point that
    # front directly at the camera rather than reusing the camera transform.
    label.rotation_mode = "XYZ"
    look_at(label, camera.location)
    label.rotation_euler.rotate_axis("Y", math.pi)
    set_polyline(leader, [point, label.location])


def cleanup() -> None:
    for object_ in list(bpy.data.objects):
        if object_.name.startswith(CREATED_PREFIX):
            bpy.data.objects.remove(object_, do_unlink=True)
    for material_ in list(bpy.data.materials):
        if material_.name.startswith(CREATED_PREFIX):
            bpy.data.materials.remove(material_)


def pose_point(rig: Any, name: str) -> Vector:
    evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
    return evaluated.matrix_world @ evaluated.pose.bones[name].matrix.translation


def image_hash(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def evaluate_points(rig: Any, frames: list[int]) -> dict[str, list[Vector]]:
    result = {bone: [] for bone in FOOT_BONES}
    for frame in frames:
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        for bone in FOOT_BONES:
            result[bone].append(pose_point(rig, bone).copy())
    return result


def evaluate_root_relative_swings(rig: Any, frames: list[int]) -> dict[str, float]:
    """Measure each foot's evaluated root-relative path length over the unit."""
    positions = {bone: [] for bone in FOOT_BONES}
    for frame in frames:
        bpy.context.scene.frame_set(frame)
        bpy.context.view_layer.update()
        evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
        root_inverse = evaluated.pose.bones["Root"].matrix.inverted_safe()
        for bone in FOOT_BONES:
            positions[bone].append(root_inverse @ evaluated.pose.bones[bone].matrix.translation)
    return {
        bone: round(sum((end - start).length for start, end in zip(samples, samples[1:])), 6)
        for bone, samples in positions.items()
    }


def yaw_degrees(rig: Any) -> float:
    # Root local X projected into fixed world XY provides the visible yaw.
    evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
    forward = evaluated.pose.bones["Root"].matrix.to_3x3() @ Vector((1.0, 0.0, 0.0))
    return math.degrees(math.atan2(forward.y, forward.x))


def render(scene: Any, camera: Any, path: Path) -> str:
    scene.camera = camera
    scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    return image_hash(path)


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for item in OUTPUT.glob("*.png"):
        item.unlink()
    cleanup()
    rig = _source_rig()
    missing_bones = [bone for bone in (*FOOT_BONES, "Root", "Hips", "Chest", "Head") if bone not in rig.pose.bones]
    if missing_bones:
        raise RuntimeError(f"evaluated rig is missing evidence bones: {missing_bones}")

    source = bpy.data.actions["kimodo_multi_388sjrhc"]
    _activate_action(rig, source)
    bpy.context.scene.frame_set(161)
    bpy.context.view_layer.update()
    root_origin = pose_point(rig, "Root")
    full_body_target = root_origin + Vector((0.0, 0.0, 1.1))
    lower_body_target = root_origin + Vector((0.0, 0.0, 0.52))

    cyan = material("Torso", (0.0, 0.8, 1.0, 1.0))
    yellow = material("LeftFoot", (1.0, 0.75, 0.0, 1.0))
    magenta = material("RightFoot", (1.0, 0.05, 0.8, 1.0))
    red = material("World X", (1.0, 0.05, 0.05, 1.0))
    green = material("World Y", (0.05, 1.0, 0.05, 1.0))
    blue = material("World Z", (0.05, 0.2, 1.0, 1.0))
    foot_materials = {"LeftFoot": yellow, "RightFoot": magenta}
    torso = polyline("Torso Axis", [pose_point(rig, "Hips"), pose_point(rig, "Chest"), pose_point(rig, "Head")], 0.012, cyan)
    legs = {
        bone: polyline(f"{bone} Current", [pose_point(rig, "Hips"), pose_point(rig, bone)], 0.012, foot_materials[bone])
        for bone in FOOT_BONES
    }
    polyline("World X", [root_origin, root_origin + Vector((0.45, 0.0, 0.0))], 0.009, red)
    polyline("World Y", [root_origin, root_origin + Vector((0.0, 0.45, 0.0))], 0.009, green)
    polyline("World Z", [root_origin, root_origin + Vector((0.0, 0.0, 0.45))], 0.009, blue)

    cameras: dict[str, Any] = {}
    camera_specs = {
        "front": (full_body_target, Vector((0.0, -4.2, 0.0)), 3.2),
        "side": (full_body_target, Vector((4.2, 0.0, 0.0)), 3.2),
        # The diagonal is level and fixed in world space, while exposing both
        # feet. It is intentionally not character-centred.
        "lower_3q": (lower_body_target, Vector((3.8, -5.2, 0.0)), 2.15),
    }
    for name, (target, offset, ortho_scale) in camera_specs.items():
        data = bpy.data.cameras.new(f"{CREATED_PREFIX}{name}")
        data.type = "ORTHO"
        data.ortho_scale = ortho_scale
        camera = bpy.data.objects.new(f"{CREATED_PREFIX}{name}", data)
        bpy.context.scene.collection.objects.link(camera)
        camera.location = target + offset
        look_at(camera, target)
        cameras[name] = camera

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x = scene.render.resolution_y = 720
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"

    report: dict[str, Any] = {
        "fixed_world_target": list(root_origin),
        "camera_level": "all cameras target a point at their own world Z; world Z remains screen vertical",
        "camera_specs": {name: {"target": list(target), "offset": list(offset), "ortho_scale": scale} for name, (target, offset, scale) in camera_specs.items()},
        "marker_identity_validation": {
            "evaluated_rig": rig.name,
            "bone_names": list(FOOT_BONES),
            "point_source": "evaluated_rig.matrix_world @ evaluated_rig.pose.bones[bone].matrix.translation after scene.frame_set and view_layer.update",
            "trajectory": "solid line joins every evaluated frame from unit start to the first-unit signed 45-degree endpoint",
            "endpoint_markers": "start/end spheres are placed at actual evaluated LeftFoot/RightFoot positions",
            "current_labels": "billboard labels include bone name and current frame; their leaders are reattached to each actual evaluated foot before every lower_3q render, so F1/F50 labels identify the endpoint spheres",
            "root_relative_swing_measure": "sum of consecutive evaluated root-relative foot displacements across the complete first unit",
        },
        "samples": {},
    }

    captures = {
        "loc_sad_pr45_loop": {"frames": OUTPUT_SAMPLES, "trajectory": list(range(1, 51)), "full_body": True},
        "loc_sad_pl45_loop": {"frames": OUTPUT_SAMPLES, "trajectory": list(range(1, 51)), "full_body": True},
    }
    for action_name, capture in captures.items():
        _activate_action(rig, bpy.data.actions[action_name])
        trajectory = evaluate_points(rig, capture["trajectory"])
        paths = {bone: polyline(f"{action_name} {bone} Trajectory", points, 0.008, foot_materials[bone]) for bone, points in trajectory.items()}
        del paths  # Objects remain linked; retain no accidental action-state dependency.
        for bone in FOOT_BONES:
            start, end = trajectory[bone][0], trajectory[bone][-1]
            marker(f"{action_name} {bone} Start", start, 0.026, foot_materials[bone])
            marker(f"{action_name} {bone} End", end, 0.032, foot_materials[bone])
            motion_arrow(f"{action_name} {bone} Motion F{capture['trajectory'][0]}-F{capture['trajectory'][-1]}", start, end, foot_materials[bone])
        labels = {bone: text_label(f"{action_name} {bone} Current Label", f"{bone} (bone)", foot_materials[bone]) for bone in FOOT_BONES}
        labels["RightFoot"].data.align_x = "RIGHT"
        leaders = {bone: polyline(f"{action_name} {bone} Label Leader", [Vector(), Vector()], 0.005, foot_materials[bone]) for bone in FOOT_BONES}
        label_offsets = {"LeftFoot": Vector((0.11, 0.05, 0.14)), "RightFoot": Vector((-0.22, 0.05, 0.22))}
        action_rows = []
        full_body_frames = {1, 25, 51, 75, 99}
        for frame in capture["frames"]:
            scene.frame_set(frame)
            bpy.context.view_layer.update()
            set_polyline(torso, [pose_point(rig, "Hips"), pose_point(rig, "Chest"), pose_point(rig, "Head")])
            for bone in FOOT_BONES:
                set_polyline(legs[bone], [pose_point(rig, "Hips"), pose_point(rig, bone)])
            for bone in FOOT_BONES:
                # The label is attached to the actual evaluated bone position;
                # at F1/F50 it names the corresponding endpoint sphere.
                labels[bone].data.body = f"{bone} bone F{frame}"
                set_label(labels[bone], leaders[bone], pose_point(rig, bone), label_offsets[bone] * 1.45, cameras["lower_3q"])
            row: dict[str, Any] = {"frame": frame, "root_yaw_degrees": round(yaw_degrees(rig), 3), "views": {}}
            path = OUTPUT / f"{action_name}_lower_3q_frame_{frame:03d}.png"
            row["views"]["lower_3q"] = {"image": path.name, "sha256": render(scene, cameras["lower_3q"], path)}
            if capture["full_body"] and frame in full_body_frames:
                for view in ("front", "side"):
                    path = OUTPUT / f"{action_name}_{view}_frame_{frame:03d}.png"
                    row["views"][view] = {"image": path.name, "sha256": render(scene, cameras[view], path)}
            action_rows.append(row)
        lead_distances = evaluate_root_relative_swings(rig, capture["trajectory"])
        report["samples"][action_name] = {
            "trajectory_frames": capture["trajectory"],
            "evaluated_root_relative_swing_world_units": lead_distances,
            "rows": action_rows,
        }
        # Lead dominance is captured as evidence for manual review. This render
        # harness must not turn a source-relative anatomy convention into a
        # false visual-render failure.
        # Keep each clip's overlay self-contained; otherwise prior clips' text
        # and trajectories remain visible and make later evidence ambiguous.
        action_prefix = f"{CREATED_PREFIX}{action_name} "
        for object_ in bpy.data.objects:
            if object_.name.startswith(action_prefix):
                object_.hide_render = True
    (OUTPUT / "capture_report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


main()
