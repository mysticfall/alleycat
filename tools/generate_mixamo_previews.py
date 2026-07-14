#!/usr/bin/env python3
"""Generate vertically-stacked PNG previews for downloaded Mixamo FBX clips."""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path


class ScriptError(Exception):
    """Raised for expected user-facing failures."""


@dataclass(frozen=True)
class Args:
    source_dir: Path
    files: tuple[Path, ...]
    force: bool
    width: int
    height: int
    separator_pixels: int
    sample_count: int
    background_colour: str


@dataclass(frozen=True)
class WorkItem:
    source: Path
    output: Path


DEFAULT_BACKGROUND_COLOUR = "4a85d9"
HEX_COLOUR_PATTERN = re.compile(r"^#?[0-9A-Fa-f]{6}$")


BLENDER_WORKER = r'''
from __future__ import annotations

import argparse
import math
import os
import pathlib
import sys
import tempfile

import bpy
from mathutils import Vector


SAMPLE_TIMES = (0.0, 1.0 / 3.0, 2.0 / 3.0, 1.0)
MAX_CAMERA_BOUND_FRAMES = 300


def parse_args():
    parser = argparse.ArgumentParser(description="Render one Mixamo FBX preview.")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--width", type=int, required=True)
    parser.add_argument("--height", type=int, required=True)
    parser.add_argument("--separator-pixels", type=int, required=True)
    parser.add_argument("--sample-count", type=int, required=True)
    parser.add_argument("--background-colour", required=True)
    return parser.parse_args(blender_script_args(sys.argv))


def blender_script_args(argv):
    if "--" in argv:
        return argv[argv.index("--") + 1 :]
    return argv[1:]


def choose_render_engine(scene):
    enum_items = scene.render.bl_rna.properties["engine"].enum_items
    available = {item.identifier for item in enum_items}
    for engine in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE", "EEVEE", "BLENDER_WORKBENCH"):
        if engine in available:
            scene.render.engine = engine
            return


def hex_colour_to_rgb(value: str):
    stripped = value.lstrip("#")
    return tuple(int(stripped[index : index + 2], 16) / 255.0 for index in (0, 2, 4))


def configure_scene(width: int, height: int, sample_count: int, background_colour):
    scene = bpy.context.scene
    choose_render_engine(scene)
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    if hasattr(scene.render, "use_motion_blur"):
        scene.render.use_motion_blur = False
    # Prefer rendering the configured colour directly, then still composite any
    # transparent pixels over it during the final merge for engine differences.
    scene.render.film_transparent = True
    scene.view_settings.view_transform = "Standard"
    try:
        scene.view_settings.look = "None"
    except TypeError:
        pass
    scene.view_settings.exposure = 0.0
    scene.view_settings.gamma = 1.0
    if scene.world is None:
        scene.world = bpy.data.worlds.new("World")
    scene.world.color = background_colour
    shading = getattr(scene.display, "shading", None)
    if shading is not None:
        if hasattr(shading, "background_type"):
            try:
                shading.background_type = "VIEWPORT"
            except TypeError:
                pass
        if hasattr(shading, "background_color"):
            shading.background_color = background_colour

    eevee = getattr(scene, "eevee", None)
    if eevee is not None:
        if hasattr(eevee, "use_motion_blur"):
            eevee.use_motion_blur = False
        for attr in ("taa_render_samples", "taa_samples"):
            if hasattr(eevee, attr):
                setattr(eevee, attr, max(1, sample_count))

    light_data = bpy.data.lights.new("Preview Key Light", type="AREA")
    light_data.energy = 650.0
    light_data.size = 5.0
    light = bpy.data.objects.new("Preview Key Light", light_data)
    bpy.context.collection.objects.link(light)
    light.location = (0.0, -4.0, 5.0)
    light.rotation_euler = (math.radians(60.0), 0.0, 0.0)

    camera_data = bpy.data.cameras.new("Preview Camera")
    camera_data.type = "ORTHO"
    camera = bpy.data.objects.new("Preview Camera", camera_data)
    bpy.context.collection.objects.link(camera)
    scene.camera = camera
    return scene, camera


def import_fbx(path: pathlib.Path):
    bpy.ops.import_scene.fbx(filepath=str(path), use_anim=True, axis_forward="-Z", axis_up="Y")


def apply_preview_material():
    material = bpy.data.materials.new("Opaque Neutral Grey Preview")
    material.diffuse_color = (0.52, 0.52, 0.52, 1.0)
    material.use_nodes = True
    material.blend_method = "OPAQUE"
    if hasattr(material, "show_transparent_back"):
        material.show_transparent_back = False

    principled = material.node_tree.nodes.get("Principled BSDF")
    if principled is not None:
        values = {
            "Base Color": (0.52, 0.52, 0.52, 1.0),
            "Alpha": 1.0,
            "Roughness": 0.85,
            "Metallic": 0.0,
        }
        for input_name, value in values.items():
            socket = principled.inputs.get(input_name)
            if socket is not None:
                socket.default_value = value

    for obj in bpy.context.scene.objects:
        if obj.type != "MESH" or obj.data is None:
            continue
        obj.active_material = material
        obj.color = (0.52, 0.52, 0.52, 1.0)
        if hasattr(obj, "show_transparent"):
            obj.show_transparent = False
        obj.data.materials.clear()
        obj.data.materials.append(material)
        for polygon in obj.data.polygons:
            polygon.material_index = 0


def animation_frame_range(scene):
    action_ranges = [action.frame_range for action in bpy.data.actions if hasattr(action, "frame_range")]
    if action_ranges:
        start = min(frame_range[0] for frame_range in action_ranges)
        end = max(frame_range[1] for frame_range in action_ranges)
    else:
        start = float(scene.frame_start)
        end = float(scene.frame_end)
    if end < start:
        start, end = end, start
    start_frame = int(math.floor(start))
    end_frame = int(math.ceil(end))
    if end_frame < start_frame:
        end_frame = start_frame
    return start_frame, end_frame


def preview_frame_numbers(start: int, end: int):
    if math.isclose(start, end):
        return [int(round(start))] * len(SAMPLE_TIMES)
    return [int(round(start + (end - start) * time)) for time in SAMPLE_TIMES]


def bounds_frame_numbers(start: int, end: int):
    frame_count = end - start + 1
    if frame_count <= MAX_CAMERA_BOUND_FRAMES:
        return list(range(start, end + 1))

    # Very long clips are sampled densely but bounded to keep the batch tool responsive.
    # Start/end are always included and the remaining frames are evenly distributed
    # across the full animation range, so camera framing is not tied to preview panels.
    return sorted(
        {
            int(round(start + (end - start) * index / (MAX_CAMERA_BOUND_FRAMES - 1)))
            for index in range(MAX_CAMERA_BOUND_FRAMES)
        }
    )


def object_bounds(scene):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    mins = Vector((math.inf, math.inf, math.inf))
    maxs = Vector((-math.inf, -math.inf, -math.inf))
    found = False
    for obj in scene.objects:
        if obj.type in {"CAMERA", "LIGHT"} or obj.hide_get():
            continue
        if not hasattr(obj, "bound_box") or not obj.bound_box:
            continue
        evaluated = obj.evaluated_get(depsgraph)
        matrix = evaluated.matrix_world
        for corner in evaluated.bound_box:
            point = matrix @ Vector(corner)
            mins.x = min(mins.x, point.x)
            mins.y = min(mins.y, point.y)
            mins.z = min(mins.z, point.z)
            maxs.x = max(maxs.x, point.x)
            maxs.y = max(maxs.y, point.y)
            maxs.z = max(maxs.z, point.z)
            found = True
    if not found:
        raise RuntimeError("no renderable object bounds were found after FBX import")
    return mins, maxs


def motion_bounds(scene, frames):
    mins = Vector((math.inf, math.inf, math.inf))
    maxs = Vector((-math.inf, -math.inf, -math.inf))
    for frame in frames:
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        frame_min, frame_max = object_bounds(scene)
        mins.x = min(mins.x, frame_min.x)
        mins.y = min(mins.y, frame_min.y)
        mins.z = min(mins.z, frame_min.z)
        maxs.x = max(maxs.x, frame_max.x)
        maxs.y = max(maxs.y, frame_max.y)
        maxs.z = max(maxs.z, frame_max.z)
    return mins, maxs


def frame_camera(camera, bounds_min, bounds_max, width: int, height: int):
    centre = (bounds_min + bounds_max) * 0.5
    span = bounds_max - bounds_min
    horizontal = max(span.x, 0.01)
    vertical = max(span.z, 0.01)
    depth = max(span.y, 0.01)
    aspect = max(width, 1) / max(height, 1)
    camera.data.ortho_scale = max(vertical, horizontal / aspect) * 1.18
    distance = max(depth * 2.0, camera.data.ortho_scale * 2.0, 4.0)
    camera.location = (centre.x, bounds_min.y - distance, centre.z)
    camera.rotation_euler = (math.radians(90.0), 0.0, 0.0)
    camera.data.clip_start = 0.01
    camera.data.clip_end = distance + depth + 100.0


def render_samples(scene, frames, temp_dir: pathlib.Path):
    paths = []
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    for index, frame in enumerate(frames):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        path = temp_dir / f"sample_{index}.png"
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)
        paths.append(path)
    return paths


def merge_vertical(image_paths, output: pathlib.Path, separator_pixels: int, background_colour):
    loaded = [bpy.data.images.load(str(path), check_existing=False) for path in image_paths]
    try:
        width = loaded[0].size[0]
        height = loaded[0].size[1]
        if any(image.size[0] != width or image.size[1] != height for image in loaded):
            raise RuntimeError("rendered sample sizes did not match")
        separator = max(0, separator_pixels)
        final_height = height * len(loaded) + separator * (len(loaded) - 1)
        final_image = bpy.data.images.new("Mixamo Preview", width=width, height=final_height, alpha=True)
        pixels = [0.0, 0.0, 0.0, 1.0] * (width * final_height)

        # Blender image pixels are bottom-up. Place the first sample at the top.
        for image_index, image in enumerate(loaded):
            source_pixels = list(image.pixels[:])
            top_y = final_height - image_index * (height + separator) - height
            for row in range(height):
                source_offset = row * width * 4
                destination_offset = (top_y + row) * width * 4
                for column in range(width):
                    source_pixel = source_offset + column * 4
                    destination_pixel = destination_offset + column * 4
                    alpha = source_pixels[source_pixel + 3]
                    source_colour = (
                        source_pixels[source_pixel],
                        source_pixels[source_pixel + 1],
                        source_pixels[source_pixel + 2],
                    )
                    inverse_alpha = 1.0 - alpha
                    pixels[destination_pixel] = source_colour[0] * alpha + background_colour[0] * inverse_alpha
                    pixels[destination_pixel + 1] = source_colour[1] * alpha + background_colour[1] * inverse_alpha
                    pixels[destination_pixel + 2] = source_colour[2] * alpha + background_colour[2] * inverse_alpha
                    pixels[destination_pixel + 3] = 1.0

        final_image.pixels.foreach_set(pixels)
        final_image.filepath_raw = str(output)
        final_image.file_format = "PNG"
        final_image.save()
    finally:
        for image in loaded:
            bpy.data.images.remove(image)


def main():
    args = parse_args()
    source = pathlib.Path(args.input)
    output = pathlib.Path(args.output)
    background_colour = hex_colour_to_rgb(args.background_colour)
    output.parent.mkdir(parents=True, exist_ok=True)
    temp_output = output.with_name(f".{output.stem}.tmp.png")
    if temp_output.exists():
        temp_output.unlink()

    bpy.ops.wm.read_factory_settings(use_empty=True)
    import_fbx(source)
    apply_preview_material()
    scene, camera = configure_scene(args.width, args.height, args.sample_count, background_colour)
    start_frame, end_frame = animation_frame_range(scene)
    preview_frames = preview_frame_numbers(start_frame, end_frame)
    bounds_frames = bounds_frame_numbers(start_frame, end_frame)
    bounds_min, bounds_max = motion_bounds(scene, bounds_frames)
    frame_camera(camera, bounds_min, bounds_max, args.width, args.height)

    with tempfile.TemporaryDirectory(prefix="mixamo_preview_render_") as temp_name:
        samples = render_samples(scene, preview_frames, pathlib.Path(temp_name))
        merge_vertical(samples, temp_output, args.separator_pixels, background_colour)
    os.replace(temp_output, output)


if __name__ == "__main__":
    main()
'''


def absolute_path(value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = Path.cwd() / path
    return path.resolve()


def parse_positive_int(value: str) -> int:
    parsed = int(value)
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be greater than zero")
    return parsed


def parse_non_negative_int(value: str) -> int:
    parsed = int(value)
    if parsed < 0:
        raise argparse.ArgumentTypeError("must be zero or greater")
    return parsed


def parse_hex_colour(value: str) -> str:
    stripped = value.strip()
    if HEX_COLOUR_PATTERN.fullmatch(stripped) is None:
        raise argparse.ArgumentTypeError("must be a 6-digit hex colour, with or without leading #")
    return stripped.lstrip("#").lower()


def parse_args(argv: list[str]) -> Args:
    parser = argparse.ArgumentParser(
        description="Generate vertical four-frame PNG previews beside downloaded Mixamo FBX files."
    )
    parser.add_argument("--source-dir", required=True, help="Directory containing downloaded Mixamo FBX files.")
    parser.add_argument(
        "--file",
        action="append",
        default=[],
        help="Specific FBX basename or path to preview; repeat to select multiple files.",
    )
    parser.add_argument("--force", action="store_true", help="Regenerate previews that already exist.")
    parser.add_argument("--width", type=parse_positive_int, default=512, help="Per-frame render width in pixels.")
    parser.add_argument("--height", type=parse_positive_int, default=512, help="Per-frame render height in pixels.")
    parser.add_argument(
        "--separator-pixels",
        type=parse_non_negative_int,
        default=8,
        help="Black horizontal separator height between stacked frames.",
    )
    parser.add_argument(
        "--sample-count",
        type=parse_positive_int,
        default=16,
        help="Fast EEVEE render sample count when supported by this Blender version.",
    )
    parser.add_argument(
        "--background-colour",
        "--background-color",
        type=parse_hex_colour,
        default=DEFAULT_BACKGROUND_COLOUR,
        help=f"Panel background hex colour, default: {DEFAULT_BACKGROUND_COLOUR}.",
    )
    parsed = parser.parse_args(argv)
    source_dir = absolute_path(parsed.source_dir)
    files = tuple(resolve_selected_file(source_dir, value) for value in parsed.file)
    return Args(
        source_dir=source_dir,
        files=files,
        force=parsed.force,
        width=parsed.width,
        height=parsed.height,
        separator_pixels=parsed.separator_pixels,
        sample_count=parsed.sample_count,
        background_colour=parsed.background_colour,
    )


def resolve_selected_file(source_dir: Path, value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = source_dir / path
    return path.resolve()


def blender_command() -> str:
    configured = os.environ.get("BLENDER")
    if configured:
        return configured
    resolved = shutil.which("blender") or shutil.which("blender-mono")
    if resolved is None:
        raise ScriptError('Could not locate Blender. Set BLENDER or ensure "blender"/"blender-mono" is on PATH.')
    return resolved


def discover_work_items(args: Args) -> list[WorkItem]:
    if not args.source_dir.is_dir():
        raise ScriptError(f'Source directory was not found at "{args.source_dir}".')

    if args.files:
        sources = list(args.files)
        invalid = [path for path in sources if path.suffix.lower() != ".fbx"]
        if invalid:
            raise ScriptError("Selected files must have a .fbx extension: " + ", ".join(str(path) for path in invalid))
        missing = [path for path in sources if not path.is_file()]
        if missing:
            raise ScriptError("Selected FBX files were not found: " + ", ".join(str(path) for path in missing))
    else:
        sources = sorted(path for path in args.source_dir.glob("*.fbx") if path.is_file())
        if not sources:
            raise ScriptError(f'No FBX files were found in "{args.source_dir}".')

    return [WorkItem(source=source, output=source.with_suffix(".png")) for source in sources]


def write_worker_script(directory: Path) -> Path:
    path = directory / "generate_mixamo_preview_worker.py"
    path.write_text(BLENDER_WORKER, encoding="utf-8")
    return path


def run_blender_worker(blender: str, worker: Path, item: WorkItem, args: Args) -> None:
    command = [
        blender,
        "--background",
        "--python",
        str(worker),
        "--",
        "--input",
        str(item.source),
        "--output",
        str(item.output),
        "--width",
        str(args.width),
        "--height",
        str(args.height),
        "--separator-pixels",
        str(args.separator_pixels),
        "--sample-count",
        str(args.sample_count),
        "--background-colour",
        args.background_colour,
    ]
    completed = subprocess.run(command, check=False, capture_output=True, text=True)
    worker_traceback = re.search(r'File ".*generate_mixamo_preview_worker\.py"', completed.stderr) is not None
    if completed.returncode != 0 or worker_traceback:
        if completed.stdout:
            print(completed.stdout, file=sys.stderr)
        if completed.stderr:
            print(completed.stderr, file=sys.stderr)
        raise ScriptError(f'Preview render failed for "{item.source}" with exit code {completed.returncode}.')
    if not item.output.is_file():
        raise ScriptError(f'Preview render did not write expected output "{item.output}".')


def generate_previews(args: Args, items: list[WorkItem]) -> int:
    blender = blender_command()
    failures = 0
    generated = 0
    skipped = 0
    with tempfile.TemporaryDirectory(prefix="mixamo_preview_worker_") as temp_name:
        worker = write_worker_script(Path(temp_name))
        for index, item in enumerate(items, start=1):
            if item.output.exists() and not args.force:
                print(f'[{index}/{len(items)}] SKIP existing: {item.output}')
                skipped += 1
                continue
            print(f'[{index}/{len(items)}] Rendering preview: {item.source.name} -> {item.output.name}')
            try:
                run_blender_worker(blender, worker, item, args)
                generated += 1
                print(f'[{index}/{len(items)}] OK: {item.output}')
            except ScriptError as error:
                failures += 1
                print(f"ERROR: {error}", file=sys.stderr)
    print(f"Mixamo preview generation complete: generated={generated}, skipped={skipped}, failed={failures}")
    return 1 if failures else 0


def run(args: Args) -> int:
    items = discover_work_items(args)
    return generate_previews(args, items)


def main() -> int:
    try:
        return run(parse_args(sys.argv[1:]))
    except ScriptError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
