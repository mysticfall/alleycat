#!/usr/bin/env python3
"""Batch-process curated Mixamo animations into grouped Blender action libraries."""

from __future__ import annotations

import argparse
import csv
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_MANIFEST = ROOT / "game/assets/characters/reference/female/animations/source/mixamo/manifest.csv"
DEFAULT_SELECTION = ROOT / "game/assets/characters/reference/female/animations/source/mixamo/selection.csv"
DEFAULT_TARGET_RIG = ROOT / "game/assets/characters/reference/female/reference_female.blend"
DEFAULT_OUTPUT_DIR = ROOT / "game/assets/characters/reference/female/animations/processed/mixamo"
RETARGET_SCRIPT = ROOT / "tools/retarget_mixamo_animation.py"
MANIFEST_COLUMNS = ("motion_id", "name", "description", "type", "file")
SELECTION_COLUMNS = (
    "motion_id",
    "enabled",
    "category",
    "motion_class",
    "tags",
    "gender",
    "group",
    "create_root_motion",
    "notes",
)
# Version 6 forces reprocessing after the retargeter made reconstructed/sanitised Mixamo Root
# tracks the normal source path and records root_source=reconstructed_root diagnostics.
PROCESSOR_VERSION = 6
INDEX_SCHEMA_VERSION = 2
METRICS_SCHEMA_VERSION = 2
ROOT_SOURCE_STATIC = "root_static"
ROOT_SOURCE_RECONSTRUCTED = "reconstructed_root"
ROOT_SOURCE_RAW = "raw_root"


class ScriptError(Exception):
    """Raised for expected user-facing batch failures."""


@dataclass(frozen=True)
class ManifestRow:
    motion_id: str
    name: str
    description: str
    motion_type: str
    file: str

    @property
    def fbx_basename(self) -> str:
        return Path(self.file).name


@dataclass(frozen=True)
class SelectionRow:
    motion_id: str
    enabled: bool
    category: str
    motion_class: str
    tags: list[str]
    gender: str
    group: str
    create_root_motion: bool
    notes: str


@dataclass(frozen=True)
class WorkItem:
    manifest: ManifestRow
    selection: SelectionRow
    fbx_path: Path

    @property
    def action_name(self) -> str:
        return f'mixamo_{self.manifest.motion_id.replace("-", "_")}'


@dataclass(frozen=True)
class Args:
    source_dir: Path
    manifest: Path
    selection: Path
    target_rig: Path
    output_dir: Path
    motion_id: str | None
    group: str | None
    force: bool
    dry_run: bool
    skip_root_reconstruction: bool


def absolute_path(value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = Path.cwd() / path
    return path.resolve()


def relative_to_root(path: Path) -> str:
    try:
        return path.resolve().relative_to(ROOT).as_posix()
    except ValueError:
        return path.as_posix()


def parse_bool(value: str, field: str, line_number: int) -> bool:
    normalised = value.strip().lower()
    if normalised in {"1", "true", "yes", "y", "on"}:
        return True
    if normalised in {"0", "false", "no", "n", "off", ""}:
        return False
    raise ScriptError(f'Invalid boolean value for {field} on selection line {line_number}: "{value}".')


def parse_args(argv: list[str]) -> Args:
    parser = argparse.ArgumentParser(
        description="Process curated Mixamo animation selections into grouped .blend action libraries."
    )
    parser.add_argument("--source-dir", required=True, help="Directory containing downloaded Mixamo FBX files.")
    parser.add_argument("--manifest", default=str(DEFAULT_MANIFEST), help="Raw Mixamo manifest CSV path.")
    parser.add_argument("--selection", default=str(DEFAULT_SELECTION), help="Curated Mixamo selection CSV path.")
    parser.add_argument("--target-rig", default=str(DEFAULT_TARGET_RIG), help="Reference target rig .blend path.")
    parser.add_argument("--output-dir", default=str(DEFAULT_OUTPUT_DIR), help="Processed output directory.")
    parser.add_argument("--motion-id", help="Process only one selected motion_id.")
    parser.add_argument("--group", help="Process only one selected group.")
    parser.add_argument("--force", action="store_true", help="Reprocess entries even when outputs look current.")
    parser.add_argument("--dry-run", action="store_true", help="Validate inputs and print planned work without writing outputs.")
    parser.add_argument(
        "--skip-root-reconstruction",
        action="store_true",
        help="Exceptional debug option passed through to the single-clip worker; normal outputs reconstruct Root.",
    )
    parsed = parser.parse_args(argv)
    return Args(
        source_dir=absolute_path(parsed.source_dir),
        manifest=absolute_path(parsed.manifest),
        selection=absolute_path(parsed.selection),
        target_rig=absolute_path(parsed.target_rig),
        output_dir=absolute_path(parsed.output_dir),
        motion_id=parsed.motion_id.strip() if parsed.motion_id else None,
        group=parsed.group.strip() if parsed.group else None,
        force=parsed.force,
        dry_run=parsed.dry_run,
        skip_root_reconstruction=parsed.skip_root_reconstruction,
    )


def load_manifest(path: Path) -> dict[str, ManifestRow]:
    if not path.is_file():
        raise ScriptError(f'Mixamo manifest was not found at "{path}".')
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        fieldnames = tuple(reader.fieldnames or ())
        if fieldnames != MANIFEST_COLUMNS:
            raise ScriptError(
                f'Mixamo manifest "{path}" must define columns {",".join(MANIFEST_COLUMNS)}; '
                f'actual columns: {",".join(fieldnames) or "none"}.'
            )
        rows: dict[str, ManifestRow] = {}
        duplicates: list[str] = []
        for line_number, row in enumerate(reader, start=2):
            motion_id = (row.get("motion_id") or "").strip()
            file_value = (row.get("file") or "").strip()
            if not motion_id or not file_value:
                raise ScriptError(f'Manifest line {line_number} has empty motion_id or file.')
            if motion_id in rows:
                duplicates.append(motion_id)
            rows[motion_id] = ManifestRow(
                motion_id=motion_id,
                name=(row.get("name") or "").strip(),
                description=(row.get("description") or "").strip(),
                motion_type=(row.get("type") or "").strip(),
                file=file_value,
            )
    if duplicates:
        raise ScriptError(f'Mixamo manifest contains duplicate motion_id values: {", ".join(sorted(set(duplicates)))}.')
    if not rows:
        raise ScriptError(f'Mixamo manifest "{path}" contains no rows.')
    return rows


def load_selection(path: Path) -> list[SelectionRow]:
    if not path.is_file():
        raise ScriptError(f'Mixamo selection CSV was not found at "{path}".')
    with path.open("r", encoding="utf-8", newline="") as handle:
        reader = csv.DictReader(handle)
        fieldnames = tuple(reader.fieldnames or ())
        if fieldnames != SELECTION_COLUMNS:
            raise ScriptError(
                f'Mixamo selection "{path}" must define columns {",".join(SELECTION_COLUMNS)}; '
                f'actual columns: {",".join(fieldnames) or "none"}.'
            )
        rows: list[SelectionRow] = []
        for line_number, row in enumerate(reader, start=2):
            motion_id = (row.get("motion_id") or "").strip()
            if not motion_id:
                raise ScriptError(f'Selection line {line_number} has empty motion_id.')
            tags = [tag.strip() for tag in (row.get("tags") or "").split(";") if tag.strip()]
            rows.append(
                SelectionRow(
                    motion_id=motion_id,
                    enabled=parse_bool(row.get("enabled") or "", "enabled", line_number),
                    category=(row.get("category") or "").strip(),
                    motion_class=(row.get("motion_class") or "").strip(),
                    tags=tags,
                    gender=(row.get("gender") or "").strip(),
                    group=(row.get("group") or "").strip(),
                    create_root_motion=parse_bool(
                        row.get("create_root_motion") or "", "create_root_motion", line_number
                    ),
                    notes=(row.get("notes") or "").strip(),
                )
            )
    return rows


def build_work_items(args: Args, manifest: dict[str, ManifestRow], selection: list[SelectionRow]) -> list[WorkItem]:
    if not args.source_dir.is_dir():
        raise ScriptError(f'Source directory was not found at "{args.source_dir}".')
    if not args.target_rig.is_file():
        raise ScriptError(f'Target rig blend was not found at "{args.target_rig}".')
    if not RETARGET_SCRIPT.is_file():
        raise ScriptError(f'Single-clip worker was not found at "{RETARGET_SCRIPT}".')

    work_items: list[WorkItem] = []
    missing_manifest: list[str] = []
    missing_group: list[str] = []
    missing_fbx: list[str] = []
    for selected in selection:
        if not selected.enabled:
            continue
        if args.motion_id is not None and selected.motion_id != args.motion_id:
            continue
        if args.group is not None and selected.group != args.group:
            continue

        row = manifest.get(selected.motion_id)
        if row is None:
            missing_manifest.append(selected.motion_id)
            continue
        if not selected.group:
            missing_group.append(selected.motion_id)
        fbx_path = args.source_dir / row.fbx_basename
        if not fbx_path.is_file():
            missing_fbx.append(f'{selected.motion_id} ({row.fbx_basename})')
        work_items.append(WorkItem(manifest=row, selection=selected, fbx_path=fbx_path))

    if missing_manifest:
        raise ScriptError(f'Selection references motion_id values missing from manifest: {", ".join(missing_manifest)}.')
    if missing_group:
        raise ScriptError(f'Enabled selection rows must have group values: {", ".join(missing_group)}.')
    if missing_fbx:
        raise ScriptError(
            'Enabled selection rows reference FBX files missing under source directory by basename: '
            + ", ".join(missing_fbx)
        )
    return work_items


def load_index(index_path: Path) -> dict[str, Any]:
    if not index_path.is_file():
        return {"schema_version": INDEX_SCHEMA_VERSION, "processor_version": PROCESSOR_VERSION, "motions": {}}
    try:
        with index_path.open("r", encoding="utf-8") as handle:
            data = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise ScriptError(f'Could not read existing dataset index "{index_path}": {exc}') from exc
    if not isinstance(data, dict):
        raise ScriptError(f'Existing dataset index "{index_path}" is not a JSON object.')
    data.setdefault("schema_version", INDEX_SCHEMA_VERSION)
    data.setdefault("processor_version", PROCESSOR_VERSION)
    data.setdefault("motions", {})
    return data


def write_json_atomic(path: Path, data: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(data, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
        Path(tmp_name).replace(path)
    except Exception:
        try:
            Path(tmp_name).unlink()
        except OSError:
            pass
        raise


def action_metrics_path(output_dir: Path, action_name: str) -> Path:
    return output_dir / "metrics" / f"{action_name}.metrics.json"


def group_blend_path(output_dir: Path, group: str) -> Path:
    safe_group = re.sub(r"[^A-Za-z0-9_.-]+", "_", group).strip("._")
    if not safe_group:
        raise ScriptError("Selection group resolves to an empty output file name.")
    return output_dir / f"{safe_group}.blend"


def index_entry_current(index: dict[str, Any], item: WorkItem, output_dir: Path) -> bool:
    entry = index.get("motions", {}).get(item.manifest.motion_id)
    if not isinstance(entry, dict):
        return False
    metrics_path = action_metrics_path(output_dir, item.action_name)
    blend_path = group_blend_path(output_dir, item.selection.group)
    if not metrics_path.is_file() or not blend_path.is_file():
        return False
    try:
        with metrics_path.open("r", encoding="utf-8") as handle:
            metrics = json.load(handle)
    except (OSError, json.JSONDecodeError):
        return False
    if metrics.get("schema_version") != METRICS_SCHEMA_VERSION:
        return False
    selection = metrics.get("selection")
    if not isinstance(selection, dict) or selection.get("tags") != item.selection.tags:
        return False
    expected = {
        "action": item.action_name,
        "group": item.selection.group,
        "group_blend": relative_to_root(blend_path),
        "metrics": relative_to_root(metrics_path),
        "category": item.selection.category,
        "motion_class": item.selection.motion_class,
        "tags": item.selection.tags,
        "gender": item.selection.gender,
        "create_root_motion": item.selection.create_root_motion,
        "processor_version": PROCESSOR_VERSION,
        "status": "success",
    }
    return all(entry.get(key) == value for key, value in expected.items())


def blender_command() -> str:
    configured = os.environ.get("BLENDER")
    if configured:
        return configured
    resolved = shutil.which("blender") or shutil.which("blender-mono")
    if resolved is None:
        raise ScriptError('Could not locate Blender. Set BLENDER or ensure "blender" is on PATH.')
    return resolved


def run_subprocess(command: list[str], description: str) -> None:
    print(" ".join(command))
    completed = subprocess.run(command, check=False, text=True)
    if completed.returncode != 0:
        raise ScriptError(f"{description} failed with exit code {completed.returncode}.")


def write_single_row_manifest(path: Path, row: ManifestRow) -> None:
    with path.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=MANIFEST_COLUMNS)
        writer.writeheader()
        writer.writerow(
            {
                "motion_id": row.motion_id,
                "name": row.name,
                "description": row.description,
                "type": row.motion_type,
                "file": row.fbx_basename,
            }
        )


def retarget_single_clip(args: Args, item: WorkItem, temp_dir: Path, blender: str) -> tuple[Path, Path]:
    temp_manifest = temp_dir / f"{item.manifest.motion_id}.manifest.csv"
    temp_blend = temp_dir / f"{item.action_name}.blend"
    temp_metrics = temp_dir / f"{item.action_name}.metrics.json"
    write_single_row_manifest(temp_manifest, item.manifest)
    command = [
        blender,
        "--background",
        "--python",
        str(RETARGET_SCRIPT),
        "--",
        "--manifest",
        str(temp_manifest),
        "--motion-id",
        item.manifest.motion_id,
        "--mixamo-root",
        str(args.source_dir),
        "--target-rig",
        str(args.target_rig),
        "--output",
        str(temp_blend),
        "--metrics-output",
        str(temp_metrics),
        "--selection-category",
        item.selection.category,
        "--selection-motion-class",
        item.selection.motion_class,
        "--selection-tags",
        ";".join(item.selection.tags),
    ]
    if args.skip_root_reconstruction:
        command.append("--skip-root-reconstruction")
    if item.selection.create_root_motion:
        command.append("--create-root-motion")
    run_subprocess(command, f'Retargeting Mixamo motion "{item.manifest.motion_id}"')
    return temp_blend, temp_metrics


def append_action_to_group(temp_blend: Path, group_blend: Path, action_name: str, blender: str) -> None:
    group_blend.parent.mkdir(parents=True, exist_ok=True)
    final_path = group_blend
    temp_output = group_blend.with_name(f".{group_blend.stem}.tmp.blend")
    script = "\n".join(
        (
            "import hashlib, pathlib, re, bpy",
            f"group_path = pathlib.Path({str(group_blend)!r})",
            f"temp_output = pathlib.Path({str(temp_output)!r})",
            f"source_blend = pathlib.Path({str(temp_blend)!r})",
            f"action_name = {action_name!r}",
            "NLA_REFERENCE_NAME_LIMIT = 63",
            "NLA_REFERENCE_PREFIX = 'LinkedActionRef'",
            "def slugify(value):",
            "    slug = re.sub(r'[^a-z0-9]+', '_', value.lower()).strip('_')",
            "    return slug or 'motion'",
            "def nla_reference_name(name):",
            "    digest = hashlib.sha1(name.encode('utf-8')).hexdigest()[:12]",
            "    slug = slugify(name)",
            "    slug_length = max(1, NLA_REFERENCE_NAME_LIMIT - len(NLA_REFERENCE_PREFIX) - len(digest) - 2)",
            "    return f'{NLA_REFERENCE_PREFIX}:{slug[:slug_length]}:{digest}'",
            "def remove_meshes_and_non_armature_objects():",
            "    for obj in list(bpy.data.objects):",
            "        if obj.type != 'ARMATURE':",
            "            bpy.data.objects.remove(obj, do_unlink=True)",
            "    for mesh in list(bpy.data.meshes):",
            "        bpy.data.meshes.remove(mesh, do_unlink=True)",
            "def shared_armature_or_none():",
            "    armatures = [obj for obj in bpy.data.objects if obj.type == 'ARMATURE']",
            "    if not armatures:",
            "        return None",
            "    armatures.sort(key=lambda obj: obj.name)",
            "    keeper = armatures[0]",
            "    for extra in armatures[1:]:",
            "        bpy.data.objects.remove(extra, do_unlink=True)",
            "    return keeper",
            "def ensure_shared_armature():",
            "    remove_meshes_and_non_armature_objects()",
            "    armature = shared_armature_or_none()",
            "    if armature is not None:",
            "        return armature",
            "    with bpy.data.libraries.load(str(source_blend), link=False) as (data_from, data_to):",
            "        data_to.objects = list(data_from.objects)",
            "    for obj in data_to.objects:",
            "        if obj is not None and obj.name not in bpy.context.scene.collection.objects:",
            "            bpy.context.scene.collection.objects.link(obj)",
            "    remove_meshes_and_non_armature_objects()",
            "    armature = shared_armature_or_none()",
            "    if armature is None:",
            "        raise RuntimeError(f'no target armature found in {source_blend}')",
            "    return armature",
            "def remove_existing_action_reference(armature, name):",
            "    animation_data = getattr(armature, 'animation_data', None)",
            "    if animation_data is None:",
            "        return",
            "    reference_name = nla_reference_name(name)",
            "    if getattr(animation_data, 'action', None) is not None and animation_data.action.name == name:",
            "        animation_data.action = None",
            "    for track in list(animation_data.nla_tracks):",
            "        if track.name == reference_name or any(getattr(strip.action, 'name', None) == name for strip in track.strips):",
            "            animation_data.nla_tracks.remove(track)",
            "def append_or_replace_action(name):",
            "    existing = bpy.data.actions.get(name)",
            "    if existing is not None:",
            "        bpy.data.actions.remove(existing, do_unlink=True)",
            "    with bpy.data.libraries.load(str(source_blend), link=False) as (data_from, data_to):",
            "        if name not in data_from.actions:",
            "            raise RuntimeError(f'action {name} not found in {source_blend}; available={list(data_from.actions)}')",
            "        data_to.actions = [name]",
            "    action = bpy.data.actions.get(name)",
            "    if action is None:",
            "        raise RuntimeError(f'appended action {name} not found')",
            "    action.use_fake_user = True",
            "    return action",
            "def create_persistent_action_user(action, armature):",
            "    animation_data = armature.animation_data_create()",
            "    reference_name = nla_reference_name(action.name)",
            "    for track in list(animation_data.nla_tracks):",
            "        if track.name == reference_name or any(strip.action == action for strip in track.strips):",
            "            animation_data.nla_tracks.remove(track)",
            "    track = animation_data.nla_tracks.new()",
            "    track.name = reference_name",
            "    strip = track.strips.new(reference_name, int(action.frame_range[0]), action)",
            "    strip.name = reference_name",
            "    strip.mute = True",
            "    track.mute = True",
            "    track.lock = True",
            "    animation_data.action = action",
            "def prune_non_processed_actions(armature):",
            "    animation_data = armature.animation_data_create()",
            "    for track in list(animation_data.nla_tracks):",
            "        if any(strip.action is not None and not strip.action.name.startswith('mixamo_') for strip in track.strips):",
            "            animation_data.nla_tracks.remove(track)",
            "    if getattr(animation_data, 'action', None) is not None and not animation_data.action.name.startswith('mixamo_'):",
            "        animation_data.action = None",
            "    for candidate in list(bpy.data.actions):",
            "        if not candidate.name.startswith('mixamo_'):",
            "            bpy.data.actions.remove(candidate, do_unlink=True)",
            "def prune_duplicate_processed_actions(armature):",
            "    duplicate_pattern = re.compile(r'^mixamo_.+\\.\\d{3}$')",
            "    duplicate_names = {action.name for action in bpy.data.actions if duplicate_pattern.match(action.name)}",
            "    if not duplicate_names:",
            "        return",
            "    animation_data = armature.animation_data_create()",
            "    for track in list(animation_data.nla_tracks):",
            "        if any(strip.action is not None and strip.action.name in duplicate_names for strip in track.strips):",
            "            animation_data.nla_tracks.remove(track)",
            "    if getattr(animation_data, 'action', None) is not None and animation_data.action.name in duplicate_names:",
            "        animation_data.action = None",
            "    for candidate in list(bpy.data.actions):",
            "        if candidate.name in duplicate_names:",
            "            bpy.data.actions.remove(candidate, do_unlink=True)",
            "if group_path.is_file():",
            "    bpy.ops.wm.open_mainfile(filepath=str(group_path))",
            "else:",
            "    bpy.ops.wm.read_factory_settings(use_empty=True)",
            "armature = ensure_shared_armature()",
            "prune_non_processed_actions(armature)",
            "prune_duplicate_processed_actions(armature)",
            "remove_existing_action_reference(armature, action_name)",
            "action = append_or_replace_action(action_name)",
            "create_persistent_action_user(action, armature)",
            "for other_action in bpy.data.actions:",
            "    other_action.use_fake_user = True",
            "remove_meshes_and_non_armature_objects()",
            "armatures = [obj for obj in bpy.data.objects if obj.type == 'ARMATURE']",
            "if len(armatures) != 1:",
            "    raise RuntimeError(f'expected exactly one shared armature, found {[obj.name for obj in armatures]}')",
            "try:",
            "    bpy.ops.outliner.orphans_purge(do_recursive=True)",
            "except Exception:",
            "    pass",
            "bpy.ops.wm.save_as_mainfile(filepath=str(temp_output), check_existing=False)",
        )
    )
    run_subprocess(
        [blender, "--background", "--python-expr", script],
        f'Appending action "{action_name}" to group blend "{group_blend}"',
    )
    temp_output.replace(final_path)


def validate_metrics(metrics_path: Path, item: WorkItem, allow_raw_root: bool = False) -> None:
    with metrics_path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)
    if data.get("action") != item.action_name:
        raise ScriptError(f'Metrics "{metrics_path}" action does not match {item.action_name}.')
    if data.get("schema_version") != METRICS_SCHEMA_VERSION:
        raise ScriptError(
            f'Metrics "{metrics_path}" schema_version={data.get("schema_version")} does not match '
            f"required {METRICS_SCHEMA_VERSION}."
        )
    for required_key in (
        "tracked_bones",
        "coordinate_space",
        "head_height_norm",
        "foot_contact",
        "feature_schema",
        "selection",
    ):
        if required_key not in data:
            raise ScriptError(f'Metrics "{metrics_path}" is missing required field "{required_key}".')
    samples = data.get("samples")
    if not isinstance(samples, list) or not samples:
        raise ScriptError(f'Metrics "{metrics_path}" contains no metric samples.')
    sample = samples[0]
    for required_sample_key in (
        "root_relative_joint_positions",
        "sample_motion_features",
        "root_local_planar_velocity",
        "root_yaw_delta_radians",
        "root_angular_velocity_radians_per_second",
        "future_trajectory",
        "foot_contact",
    ):
        if required_sample_key not in sample:
            raise ScriptError(
                f'Metrics "{metrics_path}" first sample is missing required field '
                f'"{required_sample_key}".'
            )
    if (
        not item.selection.create_root_motion
        and data.get("root_created")
        and data.get("root_source") != ROOT_SOURCE_RECONSTRUCTED
    ):
        raise ScriptError(
            f'Metrics "{metrics_path}" unexpectedly created root motion for '
            f'non-synthetic selection {item.manifest.motion_id}.'
        )
    root_source = data.get("root_source")
    allowed_root_sources = {ROOT_SOURCE_RECONSTRUCTED}
    if allow_raw_root:
        allowed_root_sources.add(ROOT_SOURCE_RAW)
        allowed_root_sources.add("pelvis_planar")
        allowed_root_sources.add("root")
    if root_source not in allowed_root_sources:
        raise ScriptError(
            f'Metrics "{metrics_path}" report root_source={root_source}; expected one of '
            f'{sorted(allowed_root_sources)} for selection {item.manifest.motion_id}.'
        )
    if item.selection.category == "locomotion" and root_source == ROOT_SOURCE_STATIC:
        raise ScriptError(
            f'Metrics "{metrics_path}" report static Root motion for locomotion selection '
            f'{item.manifest.motion_id} with create_root_motion=false. Verify the downloaded '
            "Mixamo FBX uses the extended root-bone rig and that the retarget presets map "
            "that source root onto the target Root bone; synthetic root motion is opt-in only."
        )
    serialised = json.dumps(data)
    if str(item.fbx_path) in serialised or str(item.fbx_path.parent) in serialised:
        raise ScriptError(f'Metrics "{metrics_path}" contains an absolute source path.')


def enrich_metrics_with_selection(metrics_path: Path, item: WorkItem) -> None:
    with metrics_path.open("r", encoding="utf-8") as handle:
        data = json.load(handle)
    data["selection"] = {
        "motion_id": item.selection.motion_id,
        "category": item.selection.category,
        "motion_class": item.selection.motion_class,
        "tags": item.selection.tags,
        "gender": item.selection.gender,
        "group": item.selection.group,
        "create_root_motion": item.selection.create_root_motion,
        "notes": item.selection.notes,
    }
    data.setdefault("clip", {})["selection"] = {
        "category": item.selection.category,
        "motion_class": item.selection.motion_class,
        "tags": item.selection.tags,
        "group": item.selection.group,
    }
    write_json_atomic(metrics_path, data)


def make_index_entry(item: WorkItem, output_dir: Path) -> dict[str, Any]:
    metrics_path = action_metrics_path(output_dir, item.action_name)
    blend_path = group_blend_path(output_dir, item.selection.group)
    return {
        "motion_id": item.manifest.motion_id,
        "action": item.action_name,
        "group": item.selection.group,
        "group_blend": relative_to_root(blend_path),
        "metrics": relative_to_root(metrics_path),
        "category": item.selection.category,
        "motion_class": item.selection.motion_class,
        "tags": item.selection.tags,
        "gender": item.selection.gender,
        "create_root_motion": item.selection.create_root_motion,
        "selection_notes": item.selection.notes,
        "manifest": {
            "name": item.manifest.name,
            "description": item.manifest.description,
            "type": item.manifest.motion_type,
            "file": item.manifest.file,
        },
        "source_fbx": item.manifest.fbx_basename,
        "status": "success",
        "processor_version": PROCESSOR_VERSION,
        "processed_at": datetime.now(timezone.utc).isoformat(),
    }


def reset_full_selection_outputs(args: Args, items: list[WorkItem], index: dict[str, Any]) -> None:
    if not args.force or args.motion_id is not None or args.group is not None:
        return

    selected_motion_ids = {item.manifest.motion_id for item in items}
    selected_metric_names = {f"{item.action_name}.metrics.json" for item in items}
    motions = index.setdefault("motions", {})
    for motion_id in list(motions):
        if motion_id not in selected_motion_ids:
            del motions[motion_id]

    metrics_dir = args.output_dir / "metrics"
    if metrics_dir.is_dir():
        for path in metrics_dir.glob("*.metrics.json"):
            if path.name not in selected_metric_names:
                path.unlink()

    for group in sorted({item.selection.group for item in items}):
        path = group_blend_path(args.output_dir, group)
        if path.exists():
            path.unlink()


def process_items(args: Args, items: list[WorkItem]) -> None:
    index_path = args.output_dir / "index.json"
    index = load_index(index_path)
    index["schema_version"] = INDEX_SCHEMA_VERSION
    index["processor_version"] = PROCESSOR_VERSION
    index["updated_at"] = datetime.now(timezone.utc).isoformat()
    index["metrics_schema_version"] = METRICS_SCHEMA_VERSION
    index["selection"] = {
        "path": relative_to_root(args.selection),
        "enabled_count": len(items),
        "groups": sorted({item.selection.group for item in items}),
        "categories": sorted({item.selection.category for item in items}),
    }
    index.setdefault("motions", {})
    reset_full_selection_outputs(args, items, index)

    skipped = 0
    processed = 0
    blender = blender_command()
    for item in items:
        if not args.force and index_entry_current(index, item, args.output_dir):
            print(f'SKIP current: {item.manifest.motion_id} -> {item.action_name}')
            skipped += 1
            continue
        with tempfile.TemporaryDirectory(prefix="mixamo_batch_") as temp_name:
            temp_dir = Path(temp_name)
            temp_blend, temp_metrics = retarget_single_clip(args, item, temp_dir, blender)
            final_metrics = action_metrics_path(args.output_dir, item.action_name)
            final_metrics.parent.mkdir(parents=True, exist_ok=True)
            metrics_tmp = final_metrics.with_suffix(final_metrics.suffix + ".tmp")
            shutil.copy2(temp_metrics, metrics_tmp)
            enrich_metrics_with_selection(metrics_tmp, item)
            validate_metrics(metrics_tmp, item, args.skip_root_reconstruction)
            append_action_to_group(temp_blend, group_blend_path(args.output_dir, item.selection.group), item.action_name, blender)
            metrics_tmp.replace(final_metrics)
            validate_metrics(final_metrics, item, args.skip_root_reconstruction)
            index["motions"][item.manifest.motion_id] = make_index_entry(item, args.output_dir)
            write_json_atomic(index_path, index)
            processed += 1
            print(f'OK processed: {item.manifest.motion_id} -> {item.action_name}')
    write_json_atomic(index_path, index)
    print(f'Mixamo batch complete: processed={processed}, skipped={skipped}, total={len(items)}')


def dry_run(args: Args, items: list[WorkItem]) -> None:
    print(f"Validated manifest: {args.manifest}")
    print(f"Validated selection: {args.selection}")
    print(f"Source directory: {args.source_dir}")
    print(f"Output directory: {args.output_dir}")
    print(f"Planned enabled items: {len(items)}")
    for item in items:
        print(
            f'DRY-RUN {item.manifest.motion_id} "{item.manifest.name}" -> '
            f'action={item.action_name}, group={item.selection.group}, '
            f'root_motion={item.selection.create_root_motion}, fbx={item.manifest.fbx_basename}'
        )


def run(args: Args) -> None:
    manifest = load_manifest(args.manifest)
    selection = load_selection(args.selection)
    items = build_work_items(args, manifest, selection)
    if args.dry_run:
        dry_run(args, items)
        return
    process_items(args, items)


def main() -> int:
    try:
        run(parse_args(sys.argv[1:]))
        return 0
    except ScriptError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
