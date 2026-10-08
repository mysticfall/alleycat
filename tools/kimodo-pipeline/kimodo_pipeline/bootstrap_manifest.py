"""Read-only Blender inspection building the locomotion authoring manifest.

The command opens the monolithic authoring blend without scripts or UI,
characterises every object, action, material, and semantic segment, proposes a
bootstrap classification for each, emits structural fingerprints of the
retarget binding, and writes the manifest JSON into the repository-owned temp
boundary.  The opened file is never saved and remains byte-identical.
"""
from __future__ import annotations

import hashlib
import re
from pathlib import Path
from typing import Any, Mapping, Sequence

import bpy

from .bootstrap_paths import BootstrapPathError, derive_artefact_paths
from .bootstrap_policy import (
    CLASSIFICATIONS,
    RETARGET_BONE_COLLECTION,
    RET_HELPER_SUFFIX,
    classify_action,
    classify_material,
    classify_object,
    constraint_graph_fingerprint,
    helper_configuration_fingerprint,
    partition_clean_rig,
)
from .compatibility import ensure_registered
from .contracts import validate_variant
from .temp_paths import resolve_temp_root

MANIFEST_SCHEMA = "kimodo-bootstrap-manifest/1"
MANIFEST_DIRECTORY_NAME = "kimodo-bootstrap-manifest"
MANIFEST_FILE_NAME = "locomotion_authoring_manifest.json"
_PROPERTY_SNAPSHOT_LIMIT = 160
_POSE_BONE_PATH = re.compile(r'^pose\.bones\["((?:[^"\\]|\\.)*)"\]')


class ManifestError(RuntimeError):
    """Raised when the authoring blend cannot be characterised for bootstrap."""


def _action_fcurves(action: Any) -> tuple[Any, ...]:
    """Read F-curves from either legacy or layered Blender Actions."""
    legacy_fcurves = getattr(action, "fcurves", None)
    if legacy_fcurves is not None:
        return tuple(legacy_fcurves)
    return tuple(
        fcurve
        for layer in action.layers
        for strip in layer.strips
        for channelbag in strip.channelbags
        for fcurve in channelbag.fcurves
    )


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _property_snapshot(value: Any) -> str:
    text = repr(value)
    return text if len(text) <= _PROPERTY_SNAPSHOT_LIMIT else text[:_PROPERTY_SNAPSHOT_LIMIT] + "..."


def _custom_properties(item: Any) -> dict[str, str]:
    return {
        key: _property_snapshot(item[key])
        for key in item.keys()
        if not key.startswith("_")
    }


def _constraint_record(bone_name: str, slot: int, constraint: Any) -> dict[str, Any]:
    record: dict[str, Any] = {
        "bone": bone_name,
        "slot": slot,
        "type": constraint.type,
        "name": constraint.name,
        "target": constraint.target.name if getattr(constraint, "target", None) is not None else None,
        "subtarget": getattr(constraint, "subtarget", None),
        "influence": round(float(constraint.influence), 7),
        "mute": bool(constraint.mute),
    }
    for attribute in ("owner_space", "target_space", "track_axis", "mix_mode"):
        if hasattr(constraint, attribute):
            record[attribute] = str(getattr(constraint, attribute))
    return record


def _constraint_graph_edges(obj: Any) -> list[list[Any]]:
    """Plain edge rows mirroring the studied retarget constraint_graph helper."""
    edges: list[list[Any]] = []
    for bone in obj.pose.bones:
        for slot, constraint in enumerate(bone.constraints):
            edges.append([
                bone.name,
                slot,
                constraint.type,
                constraint.target.name if constraint.target is not None else None,
                getattr(constraint, "subtarget", None),
                str(constraint.owner_space) if hasattr(constraint, "owner_space") else None,
                str(constraint.target_space) if hasattr(constraint, "target_space") else None,
                bool(constraint.mute),
                round(float(constraint.influence), 7),
            ])
    return edges


def _helper_configuration_entries(obj: Any, *, include_fixed_pose: bool = False) -> list[list[Any]]:
    """Capture rest rows, optionally including verified unanimated pose calibration.

    Extraction manifests retain their rest-only structural contract. Setup parity
    requests fixed pose rows after excluding animation of helpers; matrix_basis
    is before constraints/parenting, so animated donor ancestors cannot pollute it.
    """
    entries: list[list[Any]] = []
    for bone in sorted(obj.data.bones, key=lambda item: item.name):
        if not bone.name.endswith(RET_HELPER_SUFFIX):
            continue
        entries.append([
            bone.name,
            bone.parent.name if bone.parent is not None else None,
            [round(float(value), 7) for row in bone.matrix_local for value in row],
        ])
        if include_fixed_pose:
            entries[-1].append([
                round(float(value), 7)
                for row in obj.pose.bones[bone.name].matrix_basis for value in row
            ])
    return entries


def _object_record(obj: Any) -> dict[str, Any]:
    record: dict[str, Any] = {
        "name": obj.name,
        "type": obj.type,
        "parent": obj.parent.name if obj.parent is not None else None,
        "collections": sorted(collection.name for collection in obj.users_collection),
        "data_name": obj.data.name if obj.data is not None else None,
        "visibility": {
            "hide_viewport": bool(obj.hide_viewport),
            "hide_render": bool(obj.hide_render),
            "hide_select": bool(obj.hide_select),
        },
        "custom_properties": _custom_properties(obj),
    }
    if obj.type == "MESH":
        record["materials"] = sorted(
            slot.material.name for slot in obj.material_slots if slot.material is not None
        )
    if obj.type == "ARMATURE":
        armature = obj.data
        if armature is None:
            raise ManifestError(f'Armature object "{obj.name}" has no armature data.')
        bone_collections: dict[str, Any] = {}
        collected = set()
        for collection in armature.collections:
            members = sorted(bone.name for bone in collection.bones)
            collected.update(members)
            bone_collections[collection.name] = {"bone_count": len(members), "bones": members}
        bone_names = {bone.name for bone in armature.bones}
        record["bone_count"] = len(bone_names)
        record["bone_collections"] = bone_collections
        record["uncollected_bone_count"] = len(bone_names - collected)
        record["ret_helper_bone_count"] = sum(
            1 for bone in armature.bones if bone.name.endswith(RET_HELPER_SUFFIX)
        )
        record["pose_constraints"] = [
            _constraint_record(bone.name, slot, constraint)
            for bone in obj.pose.bones
            for slot, constraint in enumerate(bone.constraints)
        ]
    animation_data = obj.animation_data
    if animation_data is not None:
        record["animation"] = {
            "action": animation_data.action.name if animation_data.action else None,
            "nla_strips": [
                {"track": track.name, "strip": strip.name, "action": strip.action.name if strip.action else None}
                for track in animation_data.nla_tracks
                for strip in track.strips
            ],
        }
    decision = classify_object(record)
    record["classification"] = decision.classification
    record["classification_rationale"] = decision.rationale
    record["classification_confidence"] = decision.confidence
    return record


def _action_record(action: Any, users: Mapping[Any, Sequence[Any]]) -> dict[str, Any]:
    pose_bones: set[str] = set()
    non_pose_paths: set[str] = set()
    fcurves = _action_fcurves(action)
    for fcurve in fcurves:
        match = _POSE_BONE_PATH.match(fcurve.data_path)
        if match:
            pose_bones.add(bytes(match.group(1), "utf-8").decode("unicode_escape"))
        else:
            non_pose_paths.add(fcurve.data_path)
    record: dict[str, Any] = {
        "name": action.name,
        "frame_range": [round(float(action.frame_range[0]), 3), round(float(action.frame_range[1]), 3)],
        "curve_count": len(fcurves),
        "pose_bone_target_count": len(pose_bones),
        "distinct_pose_bone_targets": sorted(pose_bones),
        "non_pose_paths": sorted(non_pose_paths),
        "user_count": int(action.users),
        "fake_user": bool(action.use_fake_user),
        "referencing_ids": sorted(identifier.name for identifier in users.get(action, ())),
        "custom_properties": _custom_properties(action),
    }
    decision = classify_action(record)
    record["classification"] = decision.classification
    record["classification_rationale"] = decision.rationale
    record["classification_confidence"] = decision.confidence
    return record


def _segments() -> list[dict[str, Any]]:
    settings = getattr(bpy.context.scene, "kimodo", None)
    segments = getattr(settings, "motion_segments", None) if settings else None
    if segments is None:
        raise ManifestError("The active scene has no Kimodo motion_segments collection.")
    return [
        {
            "index": index,
            "start_frame": int(segment.start_frame),
            "end_frame": int(segment.end_frame),
            "prompt": str(getattr(segment, "prompt", "")),
        }
        for index, segment in enumerate(segments)
    ]


def _counts_by_classification(records: Sequence[Mapping[str, Any]]) -> dict[str, int]:
    counts = {classification: 0 for classification in CLASSIFICATIONS}
    for record in records:
        counts[record["classification"]] += 1
    return counts


def build_manifest(
    blend_path: str | Path,
    character_asset_root: str | Path | None = None,
    variant: str | None = None,
    temp_root: str | Path | None = None,
    repository_root_path: str | Path | None = None,
) -> dict[str, Any]:
    """Characterise the authoring blend read-only and write the manifest JSON."""
    import json

    source = Path(blend_path).expanduser().resolve()
    if not source.is_file():
        raise ManifestError(f"Authoring blend does not exist: {source}")
    projected = None
    if character_asset_root is not None or variant is not None:
        if character_asset_root is None or variant is None:
            raise ManifestError(
                "--character-root and --variant must be supplied together for path projection."
            )
        try:
            paths = derive_artefact_paths(
                character_asset_root,
                variant,
                Path(repository_root_path).expanduser().resolve()
                if repository_root_path is not None
                else None,
            )
        except (BootstrapPathError, ValueError) as error:
            raise ManifestError(str(error)) from error
        projected = {
            "canonical_source": str(paths.canonical_source),
            "neutral_template": str(paths.neutral_template),
            "character_setup": str(paths.character_setup),
            "authoring_workspace": str(paths.authoring_workspace),
            "processed_package": str(paths.processed_package),
            "variant": paths.variant,
            "character_asset_root": str(paths.character_asset_root),
        }
    source_sha256 = _sha256(source)
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False, use_scripts=False)

    female = bpy.data.objects.get("Female")
    kimodo_source = bpy.data.objects.get("Kimodo_Source")
    if female is None or female.type != "ARMATURE":
        raise ManifestError('Expected a target armature named "Female".')
    if kimodo_source is None or kimodo_source.type != "ARMATURE":
        raise ManifestError('Expected a source armature named "Kimodo_Source".')

    object_records = [_object_record(obj) for obj in sorted(bpy.data.objects, key=lambda o: o.name)]
    action_users = bpy.data.user_map(subset=list(bpy.data.actions))
    action_records = [
        _action_record(action, action_users) for action in sorted(bpy.data.actions, key=lambda a: a.name)
    ]
    material_records = []
    for material in sorted(bpy.data.materials, key=lambda m: m.name):
        record: dict[str, Any] = {
            "name": material.name,
            "user_count": int(material.users),
            "custom_properties": _custom_properties(material),
        }
        decision = classify_material(record)
        record["classification"] = decision.classification
        record["classification_rationale"] = decision.rationale
        record["classification_confidence"] = decision.confidence
        material_records.append(record)

    retarget_collection = next(
        (collection for collection in kimodo_source.data.collections if collection.name == RETARGET_BONE_COLLECTION),
        None,
    )
    if retarget_collection is None:
        raise ManifestError(
            f'Kimodo_Source has no "{RETARGET_BONE_COLLECTION}" bone collection to partition the clean rig.'
        )
    clean_rig = partition_clean_rig(
        (bone.name for bone in kimodo_source.data.bones),
        (bone.name for bone in retarget_collection.bones),
    )

    female_edges = _constraint_graph_edges(female)
    source_edges = _constraint_graph_edges(kimodo_source)
    helper_entries = _helper_configuration_entries(kimodo_source)
    fingerprints = {
        "female_constraint_graph_sha256": constraint_graph_fingerprint(female_edges),
        "female_constraint_graph_edge_count": len(female_edges),
        "kimodo_source_constraint_graph_sha256": constraint_graph_fingerprint(source_edges),
        "kimodo_source_constraint_graph_edge_count": len(source_edges),
        "kimodo_source_helper_configuration_sha256": helper_configuration_fingerprint(helper_entries),
        "kimodo_source_helper_bone_count": len(helper_entries),
        "retarget_bone_collection_members_sha256": constraint_graph_fingerprint(
            [sorted(bone.name for bone in retarget_collection.bones)]
        ),
    }

    scene_segments = _segments()
    manifest = {
        "schema": MANIFEST_SCHEMA,
        "generated_by": "kimodo_pipeline/manifest_bootstrap.py",
        "blender_version": ".".join(str(component) for component in bpy.app.version),
        "source_blend": str(source),
        "source_blend_sha256": source_sha256,
        "read_only_guarantee": "opened with load_ui=False and use_scripts=False; never saved",
        "projected_paths": projected,
        "scene": {
            "segments": scene_segments,
            "custom_properties": _custom_properties(bpy.context.scene),
        },
        "collections": [
            {
                "name": collection.name,
                "objects": sorted(obj.name for obj in collection.objects),
            }
            for collection in sorted(bpy.data.collections, key=lambda c: c.name)
        ],
        "objects": object_records,
        "actions": action_records,
        "materials": material_records,
        "clean_rig_subset": {
            "armature": kimodo_source.name,
            **clean_rig,
        },
        "fingerprints": fingerprints,
        "classification_summary": {
            "objects_by_classification": _counts_by_classification(object_records),
            "actions_by_classification": _counts_by_classification(action_records),
            "materials_by_classification": _counts_by_classification(material_records),
            "objects": {record["name"]: record["classification"] for record in object_records},
            "actions": {record["name"]: record["classification"] for record in action_records},
        },
        "notes": [
            "Kimodo_Source stays whole in character_retarget_setup; the clean kimodo_source rig "
            "is the recorded complement of its Retarget Bones collection members.",
            "The helper-configuration fingerprint digests rest matrices only, deviating from the "
            "studied bake-time helper_configuration so it stays variant-action independent.",
            "Kimodo_Source muted thigh_*_RET locked-track constraints differ from the unmuted "
            "pose-reference copies; the muted state is captured in the fingerprints.",
            "The waypoint object Kimodo_Waypoint_walk_forawrd carries a source typo retained "
            "verbatim for byte-identical round-tripping.",
        ],
    }

    directory = resolve_temp_root(temp_root) / MANIFEST_DIRECTORY_NAME
    directory.mkdir(parents=True, exist_ok=True)
    manifest_path = directory / MANIFEST_FILE_NAME
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True), encoding="utf-8")

    return {
        "manifest_path": str(manifest_path),
        "source_blend": str(source),
        "source_blend_sha256": source_sha256,
        "counts": {
            "objects": len(object_records),
            "actions": len(action_records),
            "materials": len(material_records),
            "segments": len(scene_segments),
            "objects_by_classification": manifest["classification_summary"]["objects_by_classification"],
            "actions_by_classification": manifest["classification_summary"]["actions_by_classification"],
        },
    }
