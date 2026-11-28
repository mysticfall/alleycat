"""Blender side of the ANIM-003 native idle clip export.

Runs inside Blender (``commands/sample_native_clip.py``). Both authoring paths end the same way: a reference
character armature is evaluated frame by frame, exactly as the author previews it in Blender, and its armature-space
bone matrices are written as ``anim003_native_clip_samples_v1`` JSON. ``godot/commands/build_native_clip.gd`` turns
those samples into the native clip, so preview and export share one evaluation.

- ``kimodo``: a verified copy of the character's calibrated setup is opened, the animated source action is appended
  and bound to ``Kimodo_Source`` (slotted actions included), and the constraint-driven, meshed reference rig is
  sampled. The setup-parity gate (KIMODO-SET-002) runs before sampling.
- ``rig``: a copy of an authoring blend is opened, the action is bound to the control rig (for example Rigify), and
  the deform bones of the reference armature that follows the control rig are sampled. The control rig itself is not
  exported.

Protected inputs are hashed before and after and are never saved; only the samples JSON and an optional preview
copy (beneath ``game/temp``) are written.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import shutil
import sys
from typing import Any

import bpy

from .calibrated_setup import (
    SetupError,
    SOURCE_ARMATURE_NAME,
    bind_action_slot,
    capture_setup_parity_records,
    resolve_target_rig,
    validate_rigs,
    verify_setup_parity,
)
from .bootstrap_paths import derive_artefact_paths, repository_root
from .temp_paths import resolve_temp_root

SAMPLES_SCHEMA = "anim003_native_clip_samples_v1"
# Any sampled bone moving by more than this between frames counts as frame-varying motion.
MOTION_EPSILON = 1e-5


class SamplingError(RuntimeError):
    """A clear expected sampling failure."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _portable(path: Path) -> str:
    try:
        return path.resolve().relative_to(repository_root()).as_posix()
    except ValueError:
        return path.name


def _stage_copy(source: Path, staging: Path) -> Path:
    """Copy a protected blend into staging and prove the copy is byte-identical."""
    if not source.is_file():
        raise SamplingError(f"Required input is missing: {source}")
    staging.mkdir(parents=True, exist_ok=True)
    copy = staging / source.name
    shutil.copyfile(source, copy)
    if _sha256(copy) != _sha256(source):
        raise SamplingError(f"Staged copy of {source} is not byte-identical.")
    return copy


def _load_action(path: Path, name: str) -> bpy.types.Action:
    if bpy.data.actions.get(name) is not None:
        raise SamplingError(f'Action "{name}" already exists in the opened blend; refusing to append over it.')
    with bpy.data.libraries.load(str(path), link=False) as (available, wanted):
        if name not in available.actions:
            raise SamplingError(f'Action "{name}" is not in {path}; available: {sorted(available.actions)}.')
        wanted.actions = [name]
    action = wanted.actions[0]
    if action is None:
        raise SamplingError(f'Action "{name}" could not be appended from {path}.')
    return action


def _bind(owner: bpy.types.Object, action: bpy.types.Action) -> str | None:
    data = owner.animation_data_create()
    data.action = action
    bind_action_slot(data, action)
    slot = getattr(data, "action_slot", None)
    return None if slot is None else str(slot.identifier)


def _frame_range(action: bpy.types.Action, first: int | None, last: int | None) -> tuple[int, int]:
    start = math.floor(action.frame_range[0]) if first is None else first
    end = math.ceil(action.frame_range[1]) if last is None else last
    if end - start < 1:
        raise SamplingError(f'Frame range {start}..{end} of "{action.name}" needs at least two frames.')
    return start, end


def sample_armature(target: bpy.types.Object, bone_names: list[str], start: int, end: int) -> list[list[list[list[float]]]]:
    """Return evaluated armature-space bone matrices (row-major 4x4) for every frame in ``start..end``."""
    scene = bpy.context.scene
    frames = []
    for frame in range(start, end + 1):
        scene.frame_set(frame)
        evaluated = target.evaluated_get(bpy.context.evaluated_depsgraph_get())
        frames.append([[list(row) for row in evaluated.pose.bones[name].matrix] for name in bone_names])
    return frames


def _require_motion(frames: list[Any], action: str) -> None:
    """Refuse a frozen sample set: keys spanning a duration do not prove retained motion (KIMODO-SET-004)."""
    first = frames[0]
    for frame in frames[1:]:
        for bone, matrix in enumerate(frame):
            if any(abs(a - b) > MOTION_EPSILON for row_a, row_b in zip(matrix, first[bone]) for a, b in zip(row_a, row_b)):
                return
    raise SamplingError(
        f'Action "{action}" evaluates to one frozen pose on the sampled rig; '
        "check that it is bound to the right armature and slot."
    )


def _document(target: bpy.types.Object, bone_names: list[str], frames: list[Any], source: dict[str, Any]) -> dict[str, Any]:
    if any(abs(value - (1.0 if row == column else 0.0)) > 1e-6
           for row, values in enumerate(target.matrix_world) for column, value in enumerate(values)):
        raise SamplingError(f'Reference armature "{target.name}" must have an identity object transform.')
    render = bpy.context.scene.render
    bones = [target.data.bones[name] for name in bone_names]
    return {
        "schema": SAMPLES_SCHEMA,
        "fps": render.fps / render.fps_base,
        "target": target.name,
        "bones": [
            {
                "name": bone.name,
                "parent": bone.parent.name if bone.parent is not None else None,
                "rest": [list(row) for row in bone.matrix_local],
            }
            for bone in bones
        ],
        "frames": frames,
        "source": source,
    }


def _write_preview(path: Path | None, action: bpy.types.Action, start: int, end: int) -> None:
    """Save the prepared copy (action bound, scene range set) for review in the Blender UI."""
    if path is None:
        return
    resolved = path.resolve()
    resolve_temp_root(resolved.parent)
    scene = bpy.context.scene
    scene.frame_start, scene.frame_end = start, end
    action.use_fake_user = True
    resolved.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(resolved), copy=True, compress=False)


def _write(path: Path, document: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, separators=(",", ":")), encoding="utf-8")


def _protected_hashes(paths: list[Path]) -> dict[Path, str]:
    return {path: _sha256(path) for path in paths}


def _verify_unchanged(hashes: dict[Path, str]) -> None:
    for path, digest in hashes.items():
        if _sha256(path) != digest:
            raise SamplingError(f"Protected input changed during sampling: {path}")


def run_kimodo(args: argparse.Namespace) -> dict[str, Any]:
    character_root = args.character_root.resolve()
    artefacts = derive_artefact_paths(character_root, args.name)
    setup = (args.setup or artefacts.character_setup).resolve()
    source = args.source.resolve()
    staging = resolve_temp_root(args.staging)
    hashes = _protected_hashes([setup, source])
    copy = _stage_copy(setup, staging / "setup")
    baselines = json.loads(args.approved_character_baselines) if args.approved_character_baselines else None
    if args.setup is not None:
        artefacts = type(artefacts)(**{**artefacts.__dict__, "character_setup": setup})
    try:
        parity = verify_setup_parity(artefacts, copy, args.target_rig, baselines, gate_name="Idle clip setup-parity gate")
    except SetupError as error:
        raise SamplingError(str(error)) from error
    bpy.ops.wm.open_mainfile(filepath=str(copy), load_ui=False, use_scripts=False)
    action = _load_action(source, args.action)
    rig = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
    try:
        target = resolve_target_rig(args.target_rig, rig)
        validate_rigs(target, rig)
        # Refuse an appended action that animates the fixed calibration helpers.
        capture_setup_parity_records(target.name)
    except SetupError as error:
        raise SamplingError(str(error)) from error
    slot = _bind(rig, action)
    start, end = _frame_range(action, args.frame_start, args.frame_end)
    bone_names = [bone.name for bone in target.data.bones if bone.use_deform]
    frames = sample_armature(target, bone_names, start, end)
    _require_motion(frames, action.name)
    _write_preview(args.preview_blend, action, start, end)
    _verify_unchanged(hashes)
    return _document(target, bone_names, frames, {
        "path": "kimodo",
        "source_blend": _portable(source), "source_sha256": hashes[source],
        "setup_blend": _portable(setup), "setup_sha256": hashes[setup],
        "baseline_character": parity["baseline_character"],
        "action": action.name, "slot": slot, "driver": rig.name,
        "frame_start": start, "frame_end": end,
    })


def run_rig(args: argparse.Namespace) -> dict[str, Any]:
    blend = args.blend.resolve()
    action_blend = (args.action_blend or args.blend).resolve()
    staging = resolve_temp_root(args.staging)
    hashes = _protected_hashes(sorted({blend, action_blend}))
    copy = _stage_copy(blend, staging / "authoring")
    bpy.ops.wm.open_mainfile(filepath=str(copy), load_ui=False, use_scripts=False)
    control = bpy.data.objects.get(args.control_rig)
    target = bpy.data.objects.get(args.target)
    if control is None or control.type != "ARMATURE":
        raise SamplingError(f'--control-rig names no armature "{args.control_rig}".')
    if target is None or target.type != "ARMATURE":
        raise SamplingError(f'--target names no armature "{args.target}".')
    if target == control:
        raise SamplingError("--target must be the reference armature, not the control rig.")
    if not any(constraint.target == control for bone in target.pose.bones for constraint in bone.constraints):
        raise SamplingError(
            f'Reference armature "{target.name}" has no bone constraint following "{control.name}"; '
            "the reference skeleton must follow the control rig's deform bones."
        )
    if args.action_blend is None:
        action = bpy.data.actions.get(args.action)
        if action is None:
            raise SamplingError(f'Action "{args.action}" is not in {blend}; available: {sorted(a.name for a in bpy.data.actions)}.')
    else:
        action = _load_action(action_blend, args.action)
    if target.animation_data is not None and target.animation_data.action is not None:
        raise SamplingError(f'Reference armature "{target.name}" carries its own action; it must only follow the control rig.')
    slot = _bind(control, action)
    start, end = _frame_range(action, args.frame_start, args.frame_end)
    bone_names = [bone.name for bone in target.data.bones if bone.use_deform]
    frames = sample_armature(target, bone_names, start, end)
    _require_motion(frames, action.name)
    _write_preview(args.preview_blend, action, start, end)
    _verify_unchanged(hashes)
    return _document(target, bone_names, frames, {
        "path": "rig",
        "source_blend": _portable(action_blend), "source_sha256": hashes[action_blend],
        "authoring_blend": _portable(blend), "authoring_sha256": hashes[blend],
        "action": action.name, "slot": slot, "driver": control.name,
        "frame_start": start, "frame_end": end,
    })


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="path", required=True)
    for name in ("kimodo", "rig"):
        command = commands.add_parser(name)
        command.add_argument("--action", required=True)
        command.add_argument("--samples", type=Path, required=True)
        command.add_argument("--staging", type=Path, required=True)
        command.add_argument("--name", required=True)
        command.add_argument("--frame-start", type=int)
        command.add_argument("--frame-end", type=int)
        command.add_argument("--preview-blend", type=Path)
    kimodo = commands.choices["kimodo"]
    kimodo.add_argument("--character-root", type=Path, required=True)
    kimodo.add_argument("--source", type=Path, required=True)
    kimodo.add_argument("--setup", type=Path)
    kimodo.add_argument("--target-rig")
    kimodo.add_argument("--approved-character-baselines")
    rig = commands.choices["rig"]
    rig.add_argument("--blend", type=Path, required=True)
    rig.add_argument("--action-blend", type=Path)
    rig.add_argument("--control-rig", required=True)
    rig.add_argument("--target", required=True)
    return parser.parse_args(argv)


def main() -> int:
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    args = parse_args(argv)
    try:
        document = run_kimodo(args) if args.path == "kimodo" else run_rig(args)
        _write(args.samples, document)
    except (SamplingError, ValueError) as error:
        print(f"NATIVE_CLIP_SAMPLING_ERROR: {error}", file=sys.stderr)
        return 1
    print("NATIVE_CLIP_SAMPLES_JSON=" + json.dumps({
        "samples": str(args.samples), "frames": len(document["frames"]), "bones": len(document["bones"]),
        "target": document["target"], "action": document["source"]["action"],
    }))
    return 0
