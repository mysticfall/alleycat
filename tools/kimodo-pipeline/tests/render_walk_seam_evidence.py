"""Render source/output walk comparison frames for manual seam review only."""
from __future__ import annotations
import json
from pathlib import Path
import sys
import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))
from kimodo_pipeline.discovery import _source_rig
from kimodo_pipeline.materialise import _activate_action

OUT = ROOT / "game/temp/kimodo-walk-seam-visual"
SOURCE = "kimodo_multi_388sjrhc"
CASES = {"wf_r": "loc_sad_wf_r_loop", "wb_r": "loc_sad_wb_r_loop"}

def look_at(camera, point): camera.rotation_euler = (point - camera.location).to_track_quat("-Z", "Y").to_euler()
def main():
    OUT.mkdir(parents=True, exist_ok=True)
    rig, scene = _source_rig(), bpy.context.scene
    data = bpy.data.cameras.new("Kimodo Walk Evidence Camera"); data.type = "ORTHO"; data.ortho_scale = 4.0
    camera = bpy.data.objects.new("Kimodo Walk Evidence Camera", data); scene.collection.objects.link(camera); scene.camera = camera
    scene.render.engine = "BLENDER_WORKBENCH"; scene.display.shading.light = "STUDIO"; scene.render.resolution_x = scene.render.resolution_y = 720; scene.render.image_settings.file_format = "PNG"
    report = {"renderer": "BLENDER_WORKBENCH background render", "source": SOURCE, "camera": "fixed world three-quarter orthographic; world Z screen vertical", "cases": {}}
    for code, output in CASES.items():
        rows=[]
        for label, action_name, frames in (("source", SOURCE, (29,45,61)), ("output", output, (1,17,33))):
            _activate_action(rig, bpy.data.actions[action_name])
            for frame in frames:
                scene.frame_set(frame); bpy.context.view_layer.update()
                hips = rig.matrix_world @ rig.pose.bones["Hips"].matrix.translation
                camera.location = hips + Vector((3.8,-5.0,1.8)); look_at(camera, hips + Vector((0,0,.2)))
                path = OUT / f"{code}_{label}_f{frame:03d}.png"; scene.render.filepath=str(path); bpy.ops.render.render(write_still=True)
                rows.append({"kind":label,"action":action_name,"frame":frame,"image":path.name})
        report["cases"][code] = {"source_mapping": [[29,1],[45,17],[61,33]], "rows":rows}
    (OUT / "index.json").write_text(json.dumps(report,indent=2),encoding="utf-8")
main()
