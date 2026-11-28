"""Orchestrates the ANIM-003 native idle clip export (plain Python, outside Blender).

The export runs in three stages beneath a disposable ``game/temp`` staging directory:

1. Blender samples the reference armature (``commands/sample_native_clip.py``), exactly as the author previews it.
2. Godot builds and reloads the staged native clip on the imported reference skeleton
   (``godot/commands/build_native_clip.gd``), checks SOMA-mapped bindings on the requested skeletons, and reports
   preview parity.
3. Only then is the published clip replaced atomically. An existing clip keeps its resource UID, so packs and scenes
   that reference it stay valid. Any failure leaves the published clip untouched.

Tools produce clips only: nothing here reads or writes ``pack.json``.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import uuid
from typing import Any, Sequence

from .bootstrap_paths import repository_root
from .temp_paths import resolve_temp_root

ROOT = repository_root()
GAME = ROOT / "game"
TOOL_ROOT = Path(__file__).resolve().parents[1]
SAMPLER = TOOL_ROOT / "commands" / "sample_native_clip.py"
BUILDER = TOOL_ROOT / "godot" / "commands" / "build_native_clip.gd"
DEFAULT_TEMP = GAME / "temp" / "anim003-idle-clip"
HEADER = re.compile(r'^\[gd_resource type="Animation"(?P<attributes>[^\]]*)\]')
UID = re.compile(r' uid="uid://[a-z0-9]+"')
CLIP_NAME = re.compile(r"^[a-z][a-z0-9_]*$")


class ExportError(RuntimeError):
    """A clear expected export failure."""


def resource_path(path: Path) -> str:
    """Return the ``res://`` path of a file inside the Godot project."""
    resolved = path.resolve()
    try:
        return "res://" + resolved.relative_to(GAME.resolve()).as_posix()
    except ValueError as error:
        raise ExportError(f"{path} is not inside the Godot project at {GAME}.") from error


def validate_output(output: Path, protected: Sequence[Path]) -> Path:
    resolved = output.resolve()
    if resolved.suffix != ".tres":
        raise ExportError(f"--output must name a text resource (.tres): {output}")
    resource_path(resolved)
    if "animations/source" in resolved.as_posix() or any(resolved == item.resolve() for item in protected):
        raise ExportError(f"--output must not name a protected authoring input: {output}")
    return resolved


def with_uid_of(staged_text: str, destination: Path) -> str:
    """Give staged clip text the destination's existing resource UID, so references to it stay valid."""
    first, separator, rest = staged_text.partition("\n")
    header = HEADER.match(first)
    if header is None:
        raise ExportError("The staged clip is not a text Animation resource.")
    if not destination.is_file():
        return staged_text
    existing = HEADER.match(destination.read_text(encoding="utf-8").partition("\n")[0])
    if existing is None:
        raise ExportError(f"The published file {destination} is not a text Animation resource; refusing to replace it.")
    uid = UID.search(existing.group("attributes"))
    attributes = UID.sub("", header.group("attributes"))
    if uid is not None:
        attributes += uid.group(0)
    return f'[gd_resource type="Animation"{attributes}]' + separator + rest


def publish(staged: Path, destination: Path) -> None:
    """Atomically replace ``destination`` with the staged clip, preserving its UID."""
    text = with_uid_of(staged.read_text(encoding="utf-8"), destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(f".{destination.name}.{uuid.uuid4().hex}.tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as handle:
            handle.write(text)
            handle.flush()
            os.fsync(handle.fileno())
        if temporary.read_text(encoding="utf-8") != text:
            raise ExportError(f"Staged publication of {destination} did not read back identically.")
        os.replace(temporary, destination)
    finally:
        temporary.unlink(missing_ok=True)


def _marker(output: str, prefix: str) -> dict[str, Any] | None:
    for line in output.splitlines():
        if line.startswith(prefix):
            return json.loads(line[len(prefix):])
    return None


def _errors(output: str, prefix: str) -> list[str]:
    return [line[len(prefix):].strip() for line in output.splitlines() if line.startswith(prefix)]


def _run(command: list[str], log: Path) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, check=False)
    log.write_text(f"$ {' '.join(command)}\n--- stdout\n{result.stdout}\n--- stderr\n{result.stderr}", encoding="utf-8")
    return result


def parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="export_idle_clip.py",
        description="Export an ANIM-003 native idle clip from a Kimodo idle source or a control-rig action.",
    )
    paths = parser.add_subparsers(dest="path", required=True)
    kimodo = paths.add_parser("kimodo", help="Kimodo_Source action previewed through the calibrated setup.")
    kimodo.add_argument("--character-root", type=Path, required=True,
                        help="Character asset root; its calibrated setup and approved baseline are used.")
    kimodo.add_argument("--source", type=Path, required=True, help="Animated idle source blend.")
    kimodo.add_argument("--setup", type=Path, help="Setup override for disposable tests.")
    kimodo.add_argument("--target-rig", help="Reference rig name when the setup binds more than one.")
    kimodo.add_argument("--approved-character-baselines", help="Baseline JSON override for disposable tests.")
    rig = paths.add_parser("rig", help="Control-rig (for example Rigify) action over the reference skeleton.")
    rig.add_argument("--blend", type=Path, required=True,
                     help="Authoring blend holding the control rig and the reference armature that follows it.")
    rig.add_argument("--action-blend", type=Path, help="Blend to append the action from (default: --blend).")
    rig.add_argument("--control-rig", required=True, help="Armature object the action animates.")
    rig.add_argument("--target", required=True, help="Reference armature object whose deform bones are exported.")
    for command in (kimodo, rig):
        command.add_argument("--action", required=True, help="Action to export.")
        command.add_argument("--output", type=Path, required=True, help="Published clip path (.tres) inside game/.")
        command.add_argument("--skeleton-scene", required=True,
                             help="res:// imported character scene providing the reference skeleton.")
        command.add_argument("--check-skeleton-scene", action="append", default=[],
                             help="Additional res:// character scene whose skeleton must bind the clip; repeatable.")
        command.add_argument("--bone-map", help="res:// BoneMap used by the character importer.")
        command.add_argument("--name", help="Animation resource name (default: output file stem).")
        command.add_argument("--no-loop", action="store_true", help="Export a non-looping clip.")
        command.add_argument("--frame-start", type=int)
        command.add_argument("--frame-end", type=int)
        command.add_argument("--compare", help="res:// clip to compare against diagnostically.")
        command.add_argument("--preview-blend", type=Path, help="Save the prepared preview copy here (game/temp).")
        command.add_argument("--temp-root", type=Path, default=DEFAULT_TEMP)
        command.add_argument("--stage-only", action="store_true", help="Build and validate without publishing.")
    return parser.parse_args(argv)


def export(args: argparse.Namespace) -> dict[str, Any]:
    name = args.name or args.output.stem
    if not CLIP_NAME.match(name):
        raise ExportError(f"Clip name '{name}' must be a lowercase identifier.")
    protected = [args.source, args.setup] if args.path == "kimodo" else [args.blend, args.action_blend]
    protected = [item for item in protected if item is not None]
    output = validate_output(args.output, protected)
    staging = resolve_temp_root(args.temp_root) / uuid.uuid4().hex[:12]
    staging.mkdir(parents=True)
    samples = staging / "samples.json"
    staged_clip = staging / f"{name}.tres"
    report_path = staging / "report.json"
    blender = [
        os.environ.get("BLENDER", "blender"), "--background", "--factory-startup", "--python-exit-code", "1",
        "--python", str(SAMPLER), "--", args.path,
        "--action", args.action, "--samples", str(samples), "--staging", str(staging / "inputs"), "--name", name,
    ]
    if args.frame_start is not None:
        blender += ["--frame-start", str(args.frame_start)]
    if args.frame_end is not None:
        blender += ["--frame-end", str(args.frame_end)]
    if args.preview_blend is not None:
        blender += ["--preview-blend", str(args.preview_blend.resolve())]
    if args.path == "kimodo":
        blender += ["--character-root", str(args.character_root.resolve()), "--source", str(args.source.resolve())]
        if args.setup is not None:
            blender += ["--setup", str(args.setup.resolve())]
        if args.target_rig is not None:
            blender += ["--target-rig", args.target_rig]
        if args.approved_character_baselines is not None:
            blender += ["--approved-character-baselines", args.approved_character_baselines]
    else:
        blender += ["--blend", str(args.blend.resolve()), "--control-rig", args.control_rig, "--target", args.target]
        if args.action_blend is not None:
            blender += ["--action-blend", str(args.action_blend.resolve())]
    try:
        sampled = _run(blender, staging / "blender.log")
        if sampled.returncode != 0 or not samples.is_file():
            reasons = _errors(sampled.stderr + sampled.stdout, "NATIVE_CLIP_SAMPLING_ERROR:")
            raise ExportError("Blender sampling failed: " + ("; ".join(reasons) or f"see {staging / 'blender.log'}"))
    finally:
        shutil.rmtree(staging / "inputs", ignore_errors=True)
    godot = [
        os.environ.get("GODOT_PATH", "godot-mono"), "--headless", "--xr-mode", "off", "--path", str(GAME),
        "--script", str(BUILDER), "--",
        f"--samples={samples}", f"--skeleton-scene={args.skeleton_scene}", f"--output={resource_path(staged_clip)}",
        f"--name={name}", f"--loop={'false' if args.no_loop else 'true'}", f"--report={report_path}",
    ]
    for scene in [args.skeleton_scene, *args.check_skeleton_scene]:
        godot.append(f"--check-skeleton-scene={scene}")
    if args.bone_map is not None:
        godot.append(f"--bone-map={args.bone_map}")
    if args.compare is not None:
        godot.append(f"--compare={args.compare}")
    built = _run(godot, staging / "godot.log")
    if built.returncode != 0 or _marker(built.stdout, "NATIVE_CLIP_JSON=") is None or not staged_clip.is_file():
        reasons = _errors(built.stderr + built.stdout, "NATIVE_CLIP_ERROR:")
        raise ExportError("Godot clip build failed: " + ("; ".join(reasons) or f"see {staging / 'godot.log'}"))
    report = json.loads(report_path.read_text(encoding="utf-8"))
    if not args.stage_only:
        publish(staged_clip, output)
    summary = {
        "output": str(output.relative_to(ROOT)) if output.is_relative_to(ROOT) else str(output),
        "published": not args.stage_only,
        "staging": str(staging.relative_to(ROOT)),
        "frames": report["frames"],
        "length_seconds": report["length_seconds"],
        "tracked_bones": len(report["tracked_bones"]),
        "position_tracks": report["position_tracks"],
        "preview_parity_worst_joint_m": report["preview_parity"]["worst_joint_position"]["m"],
        "compatibility": {scene: result["compatible"] for scene, result in report["compatibility"].items()},
    }
    return summary


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    try:
        summary = export(args)
    except (ExportError, ValueError) as error:
        print(f"IDLE_CLIP_ERROR: {error}", file=sys.stderr)
        return 1
    print("IDLE_CLIP_JSON=" + json.dumps(summary, sort_keys=True))
    return 0
