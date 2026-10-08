"""Transactional composition of a per-variant Kimodo authoring workspace.

The command stages a copy of the character retarget setup (the character must
be present throughout because Kimodo generation needs visual audit), appends
the character-neutral infrastructure from the neutral locomotion template,
regenerates the pose-reference actions from a validated idle input, re-binds
the scene links the extraction left dangling by design, and publishes the
workspace only after a reopen validation pass.  The three bootstrap inputs are
hash-verified against the Gate 1 baseline before and after every run so an
accidental re-extraction or in-place edit aborts the composition.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
from pathlib import Path
from typing import Any, Mapping

import bpy

from .bootstrap_manifest import (
    _action_fcurves,
    _constraint_graph_edges,
    _helper_configuration_entries,
)
from .bootstrap_paths import (
    BootstrapPathError,
    derive_artefact_paths,
    repository_root,
)
from .bootstrap_policy import (
    POSE_REFERENCE_ACTION_PREFIX,
    POSE_REFERENCE_IDLE_ACTION_NAME,
    SOURCE_ARMATURE_NAME,
    classify_action,
    classify_object,
    constraint_graph_fingerprint,
    helper_configuration_fingerprint,
)
from .character_baselines import resolve_approved_character
from .bootstrap_validation import (
    CHANNEL_DELTA_EPSILON,
    ROOT_TRANSLATION_TOLERANCE,
    WORKSPACE_DIRECTORY_NAME,
    WORKSPACE_REPORT_FILE_NAME,
    WORKSPACE_REPORT_SCHEMA,
    derive_rotation_conventions,
    idle_input_failures,
    rotation_action_comparison,
    workspace_failures,
    workspace_report_structure_failures,
)
from .compatibility import ensure_registered
from .temp_paths import resolve_temp_root

DEFAULT_CHARACTER_ROOT_RELATIVE = Path("game/assets/characters/reference/female")

# Gate 1 baseline approval is per character (KIMODO-CHAR-002): the approved
# bytes of the three bootstrap inputs live in ``character_baselines``, where
# onboarding a new character means adding that character's own baseline entry
# while every existing character's entry stays untouched.  Any drift (most
# plausibly an accidental re-run of extract_bootstrap.py) aborts workspace
# composition until the affected character's baseline is re-approved.


class WorkspaceError(RuntimeError):
    """Raised when the authoring-workspace composition contract is violated."""


def default_character_asset_root() -> Path:
    return repository_root() / DEFAULT_CHARACTER_ROOT_RELATIVE


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _open_mainfile(path: Path) -> None:
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(path), load_ui=False, use_scripts=False)


def _save_staged(path: Path) -> None:
    bpy.ops.file.make_paths_absolute()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(path), check_existing=False)


def _purge_orphans() -> int:
    return int(
        bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    )


def _curve_records(action: Any) -> list[dict[str, Any]]:
    """Plain curve records for one action, shared by rules, report, and evidence."""
    curves: list[dict[str, Any]] = []
    for fcurve in _action_fcurves(action):
        curves.append(
            {
                "data_path": str(fcurve.data_path),
                "array_index": int(fcurve.array_index),
                "keyframes": [
                    [float(key.co[0]), float(key.co[1]), str(key.interpolation)]
                    for key in fcurve.keyframe_points
                ],
            }
        )
    return curves


def _fingerprints() -> dict[str, Any]:
    from .blender_retargeting import ProcessorError, resolve_target_rig

    source = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
    try:
        target = resolve_target_rig(None, source)
    except ProcessorError as error:
        raise WorkspaceError(str(error)) from error
    target_edges = _constraint_graph_edges(target)
    helper_entries = _helper_configuration_entries(source) if source is not None else []
    return {
        # Keep the existing report column names; capture the resolved target,
        # not a character-name default. No report/schema migration is needed.
        "female_constraint_graph_sha256": constraint_graph_fingerprint(target_edges),
        "female_constraint_graph_edge_count": len(target_edges),
        "kimodo_source_constraint_graph_sha256": (
            constraint_graph_fingerprint(_constraint_graph_edges(source))
            if source is not None
            else None
        ),
        "kimodo_source_helper_configuration_sha256": (
            helper_configuration_fingerprint(helper_entries) if source is not None else None
        ),
        "kimodo_source_helper_bone_count": len(helper_entries),
    }


def _scene_settings() -> Any:
    settings = getattr(bpy.context.scene, "kimodo", None)
    if settings is None:
        raise WorkspaceError("The active scene has no Kimodo settings group.")
    return settings


def _motion_constraint_entries(settings: Any) -> list[dict[str, Any]]:
    entries: list[dict[str, Any]] = []
    try:
        constraints = settings["motion_constraints"]
        for index, entry in enumerate(constraints):
            marker = entry["marker_object"]
            entries.append(
                {
                    "index": index,
                    "constraint_type": int(entry["constraint_type"]),
                    "frame": int(entry["frame"]),
                    "label": str(entry["label"]),
                    "marker_object": marker.name if marker is not None else None,
                }
            )
    except KeyError:
        # A fixture or a newer Kimodo settings schema may have no optional
        # motion-constraint collection.  Empty remains a valid structural
        # contract; malformed present entries still fail below.
        return []
    except TypeError as error:
        raise WorkspaceError(
            f"A motion constraint entry lost required scalar state: {error}"
        ) from error
    return entries


def _segment_entries() -> list[dict[str, Any]]:
    segments = _scene_settings().motion_segments
    return [
        {
            "index": index,
            "start_frame": int(segment.start_frame),
            "end_frame": int(segment.end_frame),
            "prompt": str(getattr(segment, "prompt", "")),
        }
        for index, segment in enumerate(segments)
    ]


def _read_template(template_path: Path) -> dict[str, Any]:
    """Read every dynamic expectation and convention from the current template."""
    _open_mainfile(template_path)
    object_names: list[str] = []
    for obj in sorted(bpy.data.objects, key=lambda item: item.name):
        decision = classify_object({"name": obj.name, "type": obj.type})
        if decision.classification != "locomotion_template":
            raise WorkspaceError(
                f"Template object {obj.name!r} classifies as {decision.classification!r}; "
                "the workspace may only merge character-neutral template content."
            )
        object_names.append(obj.name)
    if not object_names:
        raise WorkspaceError("The template carries no locomotion-template objects to merge.")

    collections: dict[str, list[str]] = {}
    for collection in sorted(bpy.data.collections, key=lambda item: item.name):
        members = sorted(obj.name for obj in collection.objects if obj.name in set(object_names))
        if members:
            collections[collection.name] = members

    action_curves: dict[str, list[dict[str, Any]]] = {}
    for action in sorted(bpy.data.actions, key=lambda item: item.name):
        decision = classify_action({"name": action.name})
        if decision.classification != "locomotion_template":
            raise WorkspaceError(
                f"Template action {action.name!r} classifies as {decision.classification!r}; "
                "variant actions must never ride along with the template."
            )
        action_curves[action.name] = _curve_records(action)
    if POSE_REFERENCE_IDLE_ACTION_NAME not in action_curves:
        raise WorkspaceError(
            f"The template lacks its {POSE_REFERENCE_IDLE_ACTION_NAME!r} convention baseline."
        )
    turn_actions = {
        name: curves
        for name, curves in action_curves.items()
        if name.startswith(POSE_REFERENCE_ACTION_PREFIX) and name != POSE_REFERENCE_IDLE_ACTION_NAME
    }
    try:
        conventions = derive_rotation_conventions(
            action_curves[POSE_REFERENCE_IDLE_ACTION_NAME], turn_actions
        )
    except ValueError as error:
        raise WorkspaceError(f"Template rotation conventions are unusable: {error}") from error

    segments = _segment_entries()
    non_blank = [segment for segment in segments if segment["prompt"] != ""]
    if non_blank:
        raise WorkspaceError(
            "The template carries non-blank segment prompts; the workspace must start "
            f"blank (first offender: segment {non_blank[0]['index']})."
        )

    settings = _scene_settings()
    template_state = {
        "object_names": object_names,
        "collections": collections,
        "action_curves": action_curves,
        "conventions": conventions,
        "segments": segments,
        "motion_constraints": _motion_constraint_entries(settings),
        "frame_range": [int(bpy.context.scene.frame_start), int(bpy.context.scene.frame_end)],
    }
    for constraint in template_state["motion_constraints"]:
        if constraint["marker_object"] is None:
            raise WorkspaceError(
                f"Template motion constraint {constraint['index']} ({constraint['label']!r}) "
                "has no marker object to re-bind from."
            )
    return template_state


def _setup_records() -> dict[str, Any]:
    """Capture the staged setup bytes (pre-mutation) as the immutability baseline."""
    meshes: list[str] = []
    object_records: dict[str, dict[str, Any]] = {}
    for obj in sorted(bpy.data.objects, key=lambda item: item.name):
        record: dict[str, Any] = {
            "name": obj.name,
            "type": obj.type,
            "hide_viewport": bool(obj.hide_viewport),
            "hide_render": bool(obj.hide_render),
            "pose_constraint_count": (
                sum(len(bone.constraints) for bone in obj.pose.bones) if obj.pose else 0
            ),
        }
        if obj.type == "ARMATURE":
            record["bone_count"] = len(obj.data.bones)
            record["bone_collections"] = {
                collection.name: len(collection.bones) for collection in obj.data.collections
            }
        if obj.type == "MESH":
            meshes.append(obj.name)
        object_records[obj.name] = record
    source = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
    if source is None or source.type != "ARMATURE":
        raise WorkspaceError(f'The setup input lacks the "{SOURCE_ARMATURE_NAME}" armature.')
    def linked_setting(name: str) -> Any:
        try:
            return settings[name]
        except KeyError:
            return getattr(settings, name, None)

    settings = _scene_settings()
    setup_state = {
        "object_names": sorted(object_records),
        "mesh_names": sorted(meshes),
        "kimodo_source": {
            "bone_count": len(source.data.bones),
            "bone_collections": {
                collection.name: len(collection.bones) for collection in source.data.collections
            },
            "pose_constraint_count": sum(
                len(bone.constraints) for bone in source.pose.bones
            ),
        },
        "kimodo_bone_names": sorted(bone.name for bone in source.data.bones),
        "fingerprints": _fingerprints(),
        "scene_links": {
            "source_armature": (
                linked_setting("source_armature").name
                if linked_setting("source_armature") is not None
                else None
            ),
            "reuse_armature": (
                linked_setting("reuse_armature").name
                if linked_setting("reuse_armature") is not None
                else None
            ),
            "motion_constraints": _motion_constraint_entries(_scene_settings()),
        },
    }
    return setup_state


def _append_objects(source_path: Path, names: list[str], expected_total: int) -> list[str]:
    """Append the named objects (and their dependencies) from ``source_path``."""
    with bpy.data.libraries.load(str(source_path), link=False) as (_, destination):
        destination.objects = sorted(names)
    for obj in bpy.data.objects:
        if obj.name in set(names) and not obj.users_collection:
            bpy.context.scene.collection.objects.link(obj)
    appended = sorted(obj.name for obj in bpy.data.objects if obj.name in set(names))
    if appended != sorted(names):
        missing = sorted(set(names) - set(appended))
        raise WorkspaceError(f"Template objects failed to append: {missing}")
    if len(bpy.data.objects) != expected_total:
        raise WorkspaceError(
            f"The merge produced {len(bpy.data.objects)} objects instead of {expected_total}; "
            "an append created a renamed duplicate or dragged in extra content."
        )
    return appended


def _append_action(source_path: Path, action_name: str) -> Any:
    with bpy.data.libraries.load(str(source_path), link=False) as (source, destination):
        available = list(source.actions)
        if action_name not in available:
            raise WorkspaceError(
                f"The idle blend {source_path} carries no action {action_name!r}; "
                f"available: {sorted(available)[:8]}"
            )
        destination.actions = [action_name]
    appended = bpy.data.actions.get(action_name)
    if appended is None:
        raise WorkspaceError(f"The idle action {action_name!r} failed to append.")
    return appended


def _new_reference_action(name: str) -> tuple[Any, Any]:
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    slot = action.slots.new(id_type="OBJECT", name="Kimodo_PoseRef")
    bag = action.layers.new("Kimodo").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
    return action, bag


def _regenerate_turn_action(
    name: str,
    idle_curves: list[dict[str, Any]],
    conventions: Mapping[str, Any],
    turn: Mapping[str, Any],
) -> Any:
    """Build one turn action as the canonical idle plus the template's rotation delta."""
    action, bag = _new_reference_action(name)
    axis = (conventions["axis_data_path"], int(conventions["axis_array_index"]))
    idle_channels = {
        (curve["data_path"], int(curve["array_index"])) for curve in idle_curves
    }
    if axis not in idle_channels:
        raise WorkspaceError(
            f"The canonical idle carries no rotation channel {axis[0]}[{axis[1]}] required by "
            f"turn reference {name!r}; the template rotation convention does not fit the "
            "admitted idle input."
        )
    for curve in idle_curves:
        channel = (curve["data_path"], int(curve["array_index"]))
        frame, value, interpolation = curve["keyframes"][0]
        if channel == axis:
            value += float(turn["angle"])
        target = bag.fcurves.new(data_path=curve["data_path"], index=int(curve["array_index"]))
        target.keyframe_points.add(1)
        key = target.keyframe_points[0]
        key.co = (float(turn["frame"]), float(value))
        key.handle_left = (float(turn["frame"]), float(value))
        key.handle_right = (float(turn["frame"]), float(value))
        key.interpolation = interpolation
    return action


def _canonicalise_idle(action: Any, idle_frame: float) -> dict[str, Any]:
    """Rename the appended idle input and rebase its keys onto the template frame."""
    previous_name = action.name
    rebase_delta = None
    if action.name != POSE_REFERENCE_IDLE_ACTION_NAME:
        if bpy.data.actions.get(POSE_REFERENCE_IDLE_ACTION_NAME) is not None:
            raise WorkspaceError(
                f"Cannot canonicalise the idle input onto the existing "
                f"{POSE_REFERENCE_IDLE_ACTION_NAME!r}."
            )
        action.name = POSE_REFERENCE_IDLE_ACTION_NAME
    if not any(slot.target_id_type == "OBJECT" for slot in action.slots):
        raise WorkspaceError(
            "The idle input carries no OBJECT action slot; it cannot be assigned to the "
            "pose-reference armatures."
        )
    frames = {
        float(key.co[0])
        for fcurve in _action_fcurves(action)
        for key in fcurve.keyframe_points
    }
    # Guard locally rather than trusting the upstream admission check: a
    # multi-frame idle cannot be canonicalised onto one reference frame.
    if len(frames) > 1:
        raise WorkspaceError(
            f"The idle input {action.name!r} carries {len(frames)} distinct key frames; "
            "the canonical pose reference must occupy exactly one frame."
        )
    if frames and abs(next(iter(frames)) - float(idle_frame)) > 1e-9:
        rebase_delta = float(idle_frame) - next(iter(frames))
        for fcurve in _action_fcurves(action):
            for key in fcurve.keyframe_points:
                value = float(key.co[1])
                key.co = (float(idle_frame), value)
                key.handle_left = (float(idle_frame), value)
                key.handle_right = (float(idle_frame), value)
    action.use_fake_user = True
    return {
        "appended_name": previous_name,
        "canonical_name": action.name,
        "rebase_delta": rebase_delta,
    }


def _image_records() -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    for image in sorted(bpy.data.images, key=lambda item: item.name):
        filepath = str(image.filepath)
        applicable = bool(filepath) and image.packed_file is None
        records.append(
            {
                "name": image.name,
                "filepath": filepath,
                "file_exists": (
                    os.path.exists(bpy.path.abspath(filepath)) if applicable else None
                ),
            }
        )
    return records


def _workspace_inventory() -> dict[str, Any]:
    """Plain reopen inventory fed to the pure workspace rules and the report."""
    objects: list[dict[str, Any]] = []
    for obj in sorted(bpy.data.objects, key=lambda item: item.name):
        record: dict[str, Any] = {
            "name": obj.name,
            "type": obj.type,
            "hide_viewport": bool(obj.hide_viewport),
            "hide_render": bool(obj.hide_render),
            "pose_constraint_count": (
                sum(len(bone.constraints) for bone in obj.pose.bones) if obj.pose else 0
            ),
        }
        if obj.type == "ARMATURE":
            record["bone_count"] = len(obj.data.bones)
            record["bone_collections"] = {
                collection.name: len(collection.bones) for collection in obj.data.collections
            }
        objects.append(record)
    actions: list[dict[str, Any]] = []
    for action in sorted(bpy.data.actions, key=lambda item: item.name):
        curves = _curve_records(action)
        frames = sorted({keyframe[0] for curve in curves for keyframe in curve["keyframes"]})
        actions.append(
            {
                "name": action.name,
                "frame_range": [
                    round(float(action.frame_range[0]), 3),
                    round(float(action.frame_range[1]), 3),
                ],
                "curve_count": len(curves),
                "key_frames": frames,
                "users": int(action.users),
                "fake_user": bool(action.use_fake_user),
            }
        )
    settings = _scene_settings()
    try:
        source_armature = settings["source_armature"]
    except KeyError:
        source_armature = getattr(settings, "source_armature", None)
    try:
        reuse_armature = settings["reuse_armature"]
    except KeyError:
        reuse_armature = getattr(settings, "reuse_armature", None)
    return {
        "datablock_counts": {
            key: len(getattr(bpy.data, key))
            for key in ("objects", "meshes", "armatures", "actions", "materials", "images")
        },
        "objects": objects,
        "actions": actions,
        "materials": sorted(material.name for material in bpy.data.materials),
        "images": _image_records(),
        "segments": _segment_entries(),
        "scene": {
            "frame_range": [
                int(bpy.context.scene.frame_start),
                int(bpy.context.scene.frame_end),
            ],
        },
        "scene_links": {
            "source_armature": (
                source_armature.name if source_armature is not None else None
            ),
            "reuse_armature": reuse_armature.name if reuse_armature is not None else None,
            "motion_constraints": _motion_constraint_entries(settings),
        },
        "fingerprints": _fingerprints(),
    }


def _verify_baseline(
    hashes: Mapping[str, str], phase: str, expected: Mapping[str, str]
) -> None:
    for key in ("canonical_source", "neutral_template", "character_setup"):
        if hashes[key] != expected[key]:
            raise WorkspaceError(
                f"{phase}: bootstrap input {key} digest {hashes[key]} does not match the "
                f"approved character baseline {expected[key]}; refusing to compose over "
                "unapproved bytes (re-extraction or an in-place edit drifted the artefact)."
            )


def bootstrap_workspace(
    variant: str | None = None,
    character_asset_root: str | Path | None = None,
    setup_path: str | Path | None = None,
    template_path: str | Path | None = None,
    idle_blend: str | Path | None = None,
    idle_action: str | None = None,
    temp_root: str | Path | None = None,
    publish_root: str | Path | None = None,
    repository_root_path: str | Path | None = None,
    approved_character_baselines: Mapping[str, Mapping[str, str]] | None = None,
) -> dict[str, Any]:
    """Stage, compose, validate, and publish one authoring workspace transactionally."""
    if variant is None:
        raise WorkspaceError("--variant is required for workspace path derivation.")
    if idle_blend is None or idle_action is None:
        raise WorkspaceError("--idle-blend and --idle-action are required.")
    try:
        paths = derive_artefact_paths(
            character_asset_root if character_asset_root is not None
            else default_character_asset_root(),
            variant,
            Path(repository_root_path).expanduser().resolve()
            if repository_root_path is not None
            else None,
        )
    except (BootstrapPathError, ValueError) as error:
        raise WorkspaceError(str(error)) from error

    setup_input = (
        Path(setup_path).expanduser().resolve()
        if setup_path is not None
        else paths.character_setup
    )
    template_input = (
        Path(template_path).expanduser().resolve()
        if template_path is not None
        else paths.neutral_template
    )
    idle_blend_input = Path(idle_blend).expanduser().resolve()
    for label, candidate in (
        ("character setup", setup_input),
        ("neutral template", template_input),
        ("idle blend", idle_blend_input),
    ):
        if not candidate.is_file():
            raise WorkspaceError(f"The {label} input does not exist: {candidate}")

    inputs = {
        "canonical_source": paths.canonical_source,
        "neutral_template": template_input,
        "character_setup": setup_input,
        "idle_blend": idle_blend_input,
    }
    for label, candidate in inputs.items():
        if not candidate.is_file():
            raise WorkspaceError(f"The {label} input does not exist: {candidate}")

    # Gate 1 approves the bytes actually consumed. Override immutability is not
    # approval, and identical approved bytes at another path remain admissible.
    baseline_files = {
        "canonical_source": paths.canonical_source,
        "neutral_template": template_input,
        "character_setup": setup_input,
    }
    derived_files = (paths.canonical_source, paths.neutral_template, paths.character_setup)
    derived_before = {path: _sha256(path) for path in derived_files if path.is_file()}
    baseline_before = {key: _sha256(path) for key, path in baseline_files.items()}
    try:
        approved_character, expected_baseline = resolve_approved_character(
            baseline_before, approved_character_baselines
        )
    except ValueError as error:
        raise WorkspaceError(str(error)) from error
    hashes_before = {key: _sha256(path) for key, path in inputs.items()}

    template_state = _read_template(template_input)
    conventions = template_state["conventions"]

    from .blender_retargeting import ProcessorError, capture_setup_parity_records
    from .setup_parity import setup_parity_failures

    # Establish valid, action-independent approved calibration before staging.
    # These captures contain plain values only and survive the subsequent reload.
    _open_mainfile(setup_input)
    setup_state = _setup_records()
    try:
        approved_setup_records = capture_setup_parity_records(None)
    except ProcessorError as error:
        raise WorkspaceError(f"Workspace setup-parity gate: {error}") from error

    staging_root = resolve_temp_root(temp_root) / WORKSPACE_DIRECTORY_NAME
    staged_directory = staging_root / "staged"
    staged_directory.mkdir(parents=True, exist_ok=True)
    staged = staged_directory / "workspace.blend"
    shutil.copy2(setup_input, staged)
    if _sha256(staged) != hashes_before["character_setup"]:
        raise WorkspaceError("The staged workspace copy does not match the setup input bytes.")
    _open_mainfile(staged)

    overlap = sorted(
        set(setup_state["object_names"]) & set(template_state["object_names"])
    )
    if overlap:
        raise WorkspaceError(
            f"Setup and template object sets overlap; the merge would duplicate: {overlap}"
        )
    setup_constraints = setup_state["scene_links"]["motion_constraints"]
    template_constraints = template_state["motion_constraints"]
    if len(setup_constraints) != len(template_constraints):
        raise WorkspaceError(
            f"The setup carries {len(setup_constraints)} motion constraints but the template "
            f"records {len(template_constraints)}; the dangling re-bind contract is unusable."
        )
    for setup_entry, template_entry in zip(setup_constraints, template_constraints):
        drift = [
            field
            for field in ("constraint_type", "frame", "label")
            if setup_entry[field] != template_entry[field]
        ]
        if drift:
            raise WorkspaceError(
                f"Motion constraint {setup_entry['index']} scalar state {drift} drifted between "
                "the setup and the template; the marker re-bind cannot proceed."
            )

    merged_objects = _append_objects(
        template_input,
        template_state["object_names"],
        expected_total=len(setup_state["object_names"]) + len(template_state["object_names"]),
    )
    if sum(1 for obj in bpy.data.objects if obj.name == SOURCE_ARMATURE_NAME) != 1:
        raise WorkspaceError(
            f"Exactly one {SOURCE_ARMATURE_NAME} may survive the merge; "
            f"found {sum(1 for obj in bpy.data.objects if obj.name == SOURCE_ARMATURE_NAME)}."
        )

    collections_created: list[str] = []
    appended_by_collection: dict[str, list[str]] = {}
    scene = bpy.context.scene
    for collection_name in sorted(template_state["collections"]):
        if bpy.data.collections.get(collection_name) is not None:
            raise WorkspaceError(
                f"The setup already carries a collection named {collection_name!r}; "
                "the template collection structure cannot be merged deterministically."
            )
        collection = bpy.data.collections.new(collection_name)
        scene.collection.children.link(collection)
        collections_created.append(collection_name)
    for collection_name in sorted(template_state["collections"]):
        collection = bpy.data.collections[collection_name]
        for object_name in sorted(template_state["collections"][collection_name]):
            obj = bpy.data.objects[object_name]
            for existing in list(obj.users_collection):
                existing.objects.unlink(obj)
            collection.objects.link(obj)
            appended_by_collection.setdefault(collection_name, []).append(object_name)

    poseref_bindings: dict[str, str] = {}
    for object_name in sorted(merged_objects):
        obj = bpy.data.objects[object_name]
        if obj.animation_data is not None and obj.animation_data.action is not None:
            poseref_bindings[object_name] = obj.animation_data.action.name
    template_action_copies = sorted(action.name for action in bpy.data.actions)
    for action in list(bpy.data.actions):
        bpy.data.actions.remove(action, do_unlink=True)

    idle_action_block = _append_action(idle_blend_input, idle_action)
    idle_curves = _curve_records(idle_action_block)
    idle_record = {
        "action_name": idle_action_block.name,
        "curves": idle_curves,
        "rig_bone_names": setup_state["kimodo_bone_names"],
    }
    idle_failures = idle_input_failures(
        idle_record, {"root_translation_tolerance": ROOT_TRANSLATION_TOLERANCE}
    )
    if idle_failures:
        raise WorkspaceError(
            f"The idle input {idle_action!r} from {idle_blend_input} failed admission: "
            + "; ".join(idle_failures)
        )
    canonicalisation = _canonicalise_idle(idle_action_block, conventions["idle_frame"])
    canonical_curves = _curve_records(idle_action_block)

    regenerated: dict[str, Any] = {POSE_REFERENCE_IDLE_ACTION_NAME: idle_action_block}
    for turn_name in sorted(conventions["turns"]):
        regenerated[turn_name] = _regenerate_turn_action(
            turn_name, canonical_curves, conventions, conventions["turns"][turn_name]
        )
    for object_name, action_name in sorted(poseref_bindings.items()):
        obj = bpy.data.objects[object_name]
        action = regenerated.get(action_name)
        if action is None:
            raise WorkspaceError(
                f"Merged object {object_name!r} expects reference action {action_name!r}, "
                "which the regeneration did not produce."
            )
        animation = obj.animation_data_create()
        animation.action = action
        animation.action_slot = next(
            slot for slot in action.slots if slot.target_id_type == "OBJECT"
        )

    settings = _scene_settings()
    segments = settings.motion_segments
    segments.clear()
    for segment in template_state["segments"]:
        entry = segments.add()
        entry.start_frame = segment["start_frame"]
        entry.end_frame = segment["end_frame"]
        entry.prompt = ""
    live_segments = _segment_entries()
    if any(segment["prompt"] != "" for segment in live_segments):
        raise WorkspaceError("A non-empty segment prompt reached the workspace; aborting.")

    rebind_records: list[dict[str, Any]] = []
    try:
        constraints = settings["motion_constraints"]
    except KeyError:
        constraints = []
    for index, entry in enumerate(constraints):
        marker_name = template_constraints[index]["marker_object"]
        target = bpy.data.objects.get(marker_name)
        if target is None:
            raise WorkspaceError(
                f"Marker object {marker_name!r} for motion constraint {index} is absent "
                "from the merged workspace."
            )
        entry["marker_object"] = target
        rebind_records.append(
            {
                "index": index,
                "label": template_constraints[index]["label"],
                "marker_object": marker_name,
            }
        )
    workspace_source = bpy.data.objects[SOURCE_ARMATURE_NAME]
    settings.source_armature = workspace_source
    settings.reuse_armature = workspace_source
    settings["source_armature"] = workspace_source
    settings["reuse_armature"] = workspace_source

    segment_starts = [segment["start_frame"] for segment in template_state["segments"]]
    segment_ends = [segment["end_frame"] for segment in template_state["segments"]]
    frame_range = [min(segment_starts), max(segment_ends)]
    previous_frame_range = [int(scene.frame_start), int(scene.frame_end)]
    scene.frame_start = frame_range[0]
    scene.frame_end = frame_range[1]

    object_names_before_purge = sorted(obj.name for obj in bpy.data.objects)
    action_names_before_purge = sorted(action.name for action in bpy.data.actions)
    purged = _purge_orphans()
    # Blender may remove truly unreferenced helper objects while purging a
    # minimal synthetic fixture.  The final inventory comparison below remains
    # the authoritative object-set validation after reload.
    if sorted(action.name for action in bpy.data.actions) != action_names_before_purge:
        raise WorkspaceError("The orphan purge changed the workspace action set.")

    regenerated_curves = {
        name: _curve_records(action) for name, action in sorted(regenerated.items())
    }

    # Hard rotation-convention check: re-derive the convention the regenerated
    # references actually prove and require it to equal the template's.  A
    # mismatch (wrong axis channel, wrong placement frame, wrong angle, or
    # channel-coverage drift against the template) is a composition failure,
    # not an informational report field.
    try:
        regenerated_conventions = derive_rotation_conventions(
            canonical_curves,
            {
                name: regenerated_curves[name]
                for name in sorted(conventions["turns"])
            },
        )
    except ValueError as error:
        raise WorkspaceError(
            f"The regenerated pose references violate the rotation convention: {error}"
        ) from error
    if (
        regenerated_conventions["axis_data_path"] != conventions["axis_data_path"]
        or int(regenerated_conventions["axis_array_index"]) != int(conventions["axis_array_index"])
        or int(regenerated_conventions["curve_count"]) != int(conventions["curve_count"])
    ):
        raise WorkspaceError(
            "The regenerated pose references prove a different rotation axis or channel "
            f"coverage than the template convention ({regenerated_conventions['axis_data_path']}"
            f"[{regenerated_conventions['axis_array_index']}] with "
            f"{regenerated_conventions['curve_count']} curves)."
        )
    for name in sorted(conventions["turns"]):
        expected_turn = conventions["turns"][name]
        actual_turn = regenerated_conventions["turns"].get(name)
        if (
            actual_turn is None
            or float(actual_turn["frame"]) != float(expected_turn["frame"])
            or abs(float(actual_turn["angle"]) - float(expected_turn["angle"])) > CHANNEL_DELTA_EPSILON
        ):
            raise WorkspaceError(
                f"Regenerated turn reference {name!r} does not reproduce the template turn "
                f"(frame {actual_turn['frame'] if actual_turn else None}, angle "
                f"{actual_turn['angle'] if actual_turn else None}); expected frame "
                f"{expected_turn['frame']} and angle {expected_turn['angle']}."
            )
    comparison = {
        name: rotation_action_comparison(
            regenerated_curves[name],
            template_state["action_curves"].get(name, []),
        )
        for name in sorted(regenerated_curves)
    }
    for name, entry in sorted(comparison.items()):
        if entry["regenerated_curve_count"] != entry["template_curve_count"]:
            raise WorkspaceError(
                f"Regenerated reference {name!r} carries {entry['regenerated_curve_count']} "
                f"curves against the template's {entry['template_curve_count']}; the idle "
                "input does not cover the template's reference channels."
            )

    _save_staged(staged)

    _open_mainfile(staged)
    try:
        calibration_failures = setup_parity_failures(
            approved_setup_records, capture_setup_parity_records(None)
        )
    except ProcessorError as error:
        raise WorkspaceError(f"Workspace setup-parity gate: {error}") from error
    if calibration_failures:
        raise WorkspaceError(
            "Workspace setup-parity gate refused composition: calibration drift: "
            + "; ".join(calibration_failures)
        )
    inventory = _workspace_inventory()
    expectations = {
        "setup_object_names": setup_state["object_names"],
        "template_object_names": template_state["object_names"],
        "setup_mesh_names": setup_state["mesh_names"],
        "kimodo_source": setup_state["kimodo_source"],
        "fingerprints": setup_state["fingerprints"],
        "segments": template_state["segments"],
        "action_placements": {
            POSE_REFERENCE_IDLE_ACTION_NAME: conventions["idle_frame"],
            **{
                name: turn["frame"] for name, turn in conventions["turns"].items()
            },
        },
        "reference_curve_count": conventions["curve_count"],
        "frame_range": frame_range,
        "motion_constraints": template_constraints,
    }
    validation_failures = workspace_failures(inventory, expectations)
    if validation_failures:
        raise WorkspaceError(
            "The staged workspace failed reopen validation; nothing was published: "
            + json.dumps(validation_failures, indent=2)
        )

    hashes_after = {key: _sha256(path) for key, path in inputs.items()}
    baseline_after = {key: _sha256(path) for key, path in baseline_files.items()}
    _verify_baseline(baseline_after, "post-run check", expected_baseline)
    if any(not path.is_file() or _sha256(path) != digest for path, digest in derived_before.items()):
        raise WorkspaceError("A derived bootstrap input changed during composition.")

    destination = paths.authoring_workspace
    if publish_root is not None:
        root = Path(publish_root).expanduser().resolve()
        root.mkdir(parents=True, exist_ok=True)
        try:
            destination = root / destination.relative_to(repository_root())
        except ValueError as error:
            raise WorkspaceError(
                f"Cannot rebase the workspace destination {destination} beneath {root}: "
                "it is not beneath the repository root."
            ) from error
    replaced_existing = destination.exists()
    destination.parent.mkdir(parents=True, exist_ok=True)
    # The report is built, structurally validated, and written BEFORE the
    # workspace is published: a report failure cannot leave a published
    # workspace without a report.  The recorded digest is taken from the
    # staged bytes, which publication replaces byte-for-byte.
    workspace_sha = _sha256(staged)

    report = {
        "schema": WORKSPACE_REPORT_SCHEMA,
        "generated_by": "kimodo_pipeline/workspace_bootstrap.py",
        "blender_version": ".".join(str(component) for component in bpy.app.version),
        "variant": paths.variant,
        "character_asset_root": str(paths.character_asset_root),
        "baseline_character": approved_character,
        "publish_root": str(publish_root) if publish_root is not None else None,
        "inputs": {
            key: {
                "path": str(path),
                "sha256_before": hashes_before[key],
                "sha256_after": hashes_after[key],
                "unchanged": hashes_before[key] == hashes_after[key],
            }
            for key, path in inputs.items()
        },
        "baseline": {
            key: {
                "path": str(path),
                "sha256": baseline_after[key],
                "matches_gate1": baseline_after[key] == expected_baseline[key],
            }
            for key, path in baseline_files.items()
        },
        "workspace": {
            "destination": str(destination),
            "staged": str(staged),
            "published": True,
            "replaced_existing": replaced_existing,
            "sha256": workspace_sha,
        },
        "composition": {
            "merged_objects": merged_objects,
            "appended_collections": appended_by_collection,
            "setup_objects": setup_state["object_names"],
            "template_objects": template_state["object_names"],
            "removed_template_action_copies": template_action_copies,
            "poseref_action_bindings": poseref_bindings,
            "idle": {
                "source_blend": str(idle_blend_input),
                "source_action": str(idle_action),
                "validation": {"passed": True, "failures": []},
                "curve_count": len(canonical_curves),
                "canonical_frame": conventions["idle_frame"],
                "canonicalisation": canonicalisation,
            },
            "rotation_generation": {
                "axis_data_path": conventions["axis_data_path"],
                "axis_array_index": conventions["axis_array_index"],
                "idle_value": conventions["idle_value"],
                "turns": conventions["turns"],
                "regenerated_actions": sorted(regenerated),
                "comparison_vs_template": comparison,
            },
            "segments": template_state["segments"],
            "prompt_enforcement": {"passed": True},
            "motion_constraints_rebind": rebind_records,
            "scene_links": {
                "source_armature": SOURCE_ARMATURE_NAME,
                "reuse_armature": SOURCE_ARMATURE_NAME,
            },
            "frame_range": {
                "chosen": frame_range,
                "previous_scene_range": previous_frame_range,
                "segment_extents": [min(segment_starts), max(segment_ends)],
            },
            "fingerprints": {
                key: {
                    "setup": setup_state["fingerprints"][key],
                    "workspace": inventory["fingerprints"][key],
                    "equal": setup_state["fingerprints"][key] == inventory["fingerprints"][key],
                }
                for key in sorted(setup_state["fingerprints"])
            },
            "purged_datablocks": purged,
        },
        "validation": {"passed": True, "failures": []},
        "inventory": inventory,
    }
    structural_failures = workspace_report_structure_failures(report)
    if structural_failures:
        raise WorkspaceError(
            f"Workspace report failed its own structure contract: {structural_failures}"
        )
    report_path = staging_root / WORKSPACE_REPORT_FILE_NAME
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")

    publish = destination.with_name(f".{destination.name}.kimodo-publish")
    try:
        shutil.copy2(staged, publish)
        os.replace(publish, destination)
    except OSError:
        publish.unlink(missing_ok=True)
        raise

    return {
        "report_path": str(report_path),
        "workspace": {
            "destination": str(destination),
            "sha256": workspace_sha,
            "replaced_existing": replaced_existing,
        },
        "variant": paths.variant,
        "baseline_character": approved_character,
        "validation_passed": True,
        "inputs_unchanged": True,
    }
