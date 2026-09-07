"""One-time transactional extraction of the three bootstrap input blends.

The command stages the canonical clean rig, the neutral locomotion template,
and the character retarget setup beneath the repository temp boundary, saves
each staged blend, reopens it, and validates it against the Phase 1 manifest
expectations through the pure rules in ``bootstrap_validation``.  Only when
all three staged blends pass — and the monolith is proven byte-identical — are
the artefacts published atomically to their destination paths.  The monolithic
authoring blend itself is opened read-only-style (no UI, no scripts) and never
saved, so it remains byte-identical.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
from pathlib import Path
from typing import Any, Mapping

import bpy

from .bootstrap_manifest import _constraint_graph_edges, _helper_configuration_entries
from .bootstrap_paths import BootstrapPathError, derive_artefact_paths, repository_root
from .bootstrap_policy import (
    CLASSIFICATIONS,
    RETARGET_BONE_COLLECTION,
    RET_HELPER_SUFFIX,
    SOURCE_ARMATURE_NAME,
    classify_action,
    classify_object,
    constraint_graph_fingerprint,
    helper_configuration_fingerprint,
    partition_clean_rig,
)
from .bootstrap_validation import (
    ARTEFACT_KEYS,
    EXTRACTION_DIRECTORY_NAME,
    REPORT_FILE_NAME,
    REPORT_SCHEMA,
    blank_segment_prompts,
    canonical_source_failures,
    character_setup_failures,
    expectations_from_manifest,
    neutral_template_failures,
    report_structure_failures,
)
from .compatibility import ensure_registered
from .temp_paths import resolve_temp_root

MONOLITH_RELATIVE = Path("game/assets/characters/animations/source/locomotion/locomotion.blend")
DEFAULT_CHARACTER_ROOT_RELATIVE = Path("game/assets/characters/reference/female")
DEFAULT_MANIFEST_RELATIVE = (
    Path("game/temp/kimodo-bootstrap-manifest/locomotion_authoring_manifest.json")
)
_VALIDATORS = {
    "canonical_source": canonical_source_failures,
    "neutral_template": neutral_template_failures,
    "character_setup": character_setup_failures,
}


class ExtractionError(RuntimeError):
    """Raised when the bootstrap extraction contract is violated."""


def default_blend_path() -> Path:
    return repository_root() / MONOLITH_RELATIVE


def default_character_asset_root() -> Path:
    return repository_root() / DEFAULT_CHARACTER_ROOT_RELATIVE


def default_manifest_path() -> Path:
    return repository_root() / DEFAULT_MANIFEST_RELATIVE


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _open_monolith(source: Path) -> None:
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False, use_scripts=False)


def _save_staged(path: Path) -> None:
    # Absolutise external paths first: save_as_mainfile remaps relative paths
    # against the staging directory, which would break them once the staged
    # blend is published at a different depth (the world HDRI is the known
    # relative case).  Absolute paths survive the relocation unchanged.
    bpy.ops.file.make_paths_absolute()
    # Keep the staging boundary free of ".blend1" backups, like materialise.
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(path), check_existing=False)


def _purge_orphans() -> int:
    return int(
        bpy.data.orphans_purge(do_local_ids=True, do_linked_ids=True, do_recursive=True)
    )


def _object_decision(obj: Any) -> str:
    decision = classify_object({"name": obj.name, "type": obj.type})
    if decision.classification not in CLASSIFICATIONS:
        raise ExtractionError(f"Unclassifiable object {obj.name!r}.")
    return decision.classification


def _remove_objects_except(keep_names: set[str]) -> list[str]:
    removed: list[str] = []
    for obj in list(bpy.data.objects):
        if obj.name not in keep_names:
            removed.append(obj.name)
            bpy.data.objects.remove(obj, do_unlink=True)
    return sorted(removed)


def _segment_records() -> list[dict[str, Any]]:
    settings = getattr(bpy.context.scene, "kimodo", None)
    segments = getattr(settings, "motion_segments", None) if settings else None
    if segments is None:
        raise ExtractionError("The active scene has no Kimodo motion_segments collection.")
    return [
        {
            "index": index,
            "start_frame": int(segment.start_frame),
            "end_frame": int(segment.end_frame),
            "prompt": str(getattr(segment, "prompt", "")),
        }
        for index, segment in enumerate(segments)
    ]


def _clear_segments() -> None:
    settings = getattr(bpy.context.scene, "kimodo", None)
    segments = getattr(settings, "motion_segments", None) if settings else None
    if segments is None:
        raise ExtractionError("The active scene has no Kimodo motion_segments collection.")
    segments.clear()


def _doomed_references(
    keep_objects: set[str], keep_actions: set[str], keep_materials: set[str]
) -> list[dict[str, Any]]:
    """Record surviving-state references to datablocks the artefact is about to drop.

    Blender nulls a pointer the moment its target datablock is removed, so this
    capture runs BEFORE any removal.  The recorded references are dangling by
    design: they only re-resolve once Phase 3 composes the artefacts into a
    workspace carrying the missing datablocks.
    """
    from bpy.types import ID

    keep_by_type = {
        "Object": keep_objects,
        "Action": keep_actions,
        "Material": keep_materials,
    }

    def doomed(field: str, value: Any) -> None:
        identifier = value.bl_rna.identifier
        keep = keep_by_type.get(identifier)
        if keep is None or value.name not in keep:
            hits.append(
                {
                    "field": field,
                    "missing_target": value.name,
                    "id_type": identifier,
                    "disposition": (
                        "Blender nulls this link at extraction; re-binds on Phase 3 "
                        "workspace composition"
                    ),
                }
            )

    hits: list[dict[str, Any]] = []

    def visit(value: Any, path: str) -> None:
        if isinstance(value, ID):
            doomed(path, value)
            return
        if isinstance(value, (list, tuple)):
            # IDProperty collections surface as plain lists on access.
            for index, inner in enumerate(value):
                visit(inner, f"{path}[{index}]")
            return
        if hasattr(value, "keys") and not isinstance(value, (str, bytes)):
            named = False
            for key in list(value.keys()):
                try:
                    inner = value[key]
                except KeyError:
                    continue
                named = True
                visit(inner, f"{path}.{key}")
            if not named:
                # IDProperty collections expose their entries through iteration
                # rather than named keys.
                for index, inner in enumerate(value):
                    visit(inner, f"{path}[{index}]")

    settings = getattr(bpy.context.scene, "kimodo", None)
    if settings is not None:
        visit(settings, "scene.kimodo")
    for obj in bpy.data.objects:
        if obj.name not in keep_objects:
            continue
        if obj.parent is not None:
            doomed(f"objects[{obj.name!r}].parent", obj.parent)
        for constraint in obj.constraints:
            target = getattr(constraint, "target", None)
            if target is not None:
                doomed(f"objects[{obj.name!r}].constraints[{constraint.name!r}].target", target)
        for modifier in obj.modifiers:
            for attribute in ("object", "target"):
                target = getattr(modifier, attribute, None)
                if target is not None:
                    doomed(
                        f"objects[{obj.name!r}].modifiers[{modifier.name!r}].{attribute}", target
                    )
        for bone in obj.pose.bones if obj.pose else ():
            for constraint in bone.constraints:
                target = getattr(constraint, "target", None)
                if target is not None:
                    doomed(
                        f"objects[{obj.name!r}].pose.bones[{bone.name!r}]"
                        f".constraints[{constraint.name!r}].target",
                        target,
                    )
    return hits


def _stage_canonical_source(source: Path, staged: Path, expectations: Mapping[str, Any]) -> dict[str, Any]:
    _open_monolith(source)
    rig = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
    if rig is None or rig.type != "ARMATURE":
        raise ExtractionError(f'Expected a source armature named "{SOURCE_ARMATURE_NAME}".')
    dangling = _doomed_references({SOURCE_ARMATURE_NAME}, set(), set())
    _remove_objects_except({SOURCE_ARMATURE_NAME})
    if rig.animation_data is not None:
        rig.animation_data_clear()

    retarget_collection = next(
        (c for c in rig.data.collections if c.name == RETARGET_BONE_COLLECTION), None
    )
    if retarget_collection is None:
        raise ExtractionError(
            f'Kimodo_Source has no "{RETARGET_BONE_COLLECTION}" bone collection to partition against.'
        )
    partition = partition_clean_rig(
        (bone.name for bone in rig.data.bones),
        (bone.name for bone in retarget_collection.bones),
    )
    if partition["total_bone_count"] != expectations["total_bone_count"]:
        raise ExtractionError(
            f"Monolith bone count {partition['total_bone_count']} drifted from the "
            f"characterised {expectations['total_bone_count']}."
        )
    if partition["retarget_addition_bones"] != sorted(expectations["retarget_addition_bones"]):
        raise ExtractionError("Monolith retarget additions drifted from the characterised set.")
    clean_parented_to_addition = [
        bone.name
        for bone in rig.data.bones
        if bone.name in set(partition["clean_bones"])
        and bone.parent is not None
        and bone.parent.name in set(partition["retarget_addition_bones"])
    ]
    if clean_parented_to_addition:
        raise ExtractionError(
            "Clean bones are parented to retarget additions; bone deletion would rewrite "
            f"clean-rig parenting: {sorted(clean_parented_to_addition)}"
        )

    # Bone surgery is the only structural deviation from object-level routing.
    try:
        bpy.context.view_layer.objects.active = rig
        rig.select_set(True)
        bpy.ops.object.mode_set(mode="EDIT")
        edit_bones = rig.data.edit_bones
        for name in partition["retarget_addition_bones"]:
            edit_bones.remove(edit_bones[name])
        bpy.ops.object.mode_set(mode="OBJECT")
    except (RuntimeError, KeyError) as error:
        raise ExtractionError(f"Clean-rig bone surgery failed: {error}") from error
    for collection in [
        c for c in rig.data.collections if c.name == RETARGET_BONE_COLLECTION
    ]:
        rig.data.collections.remove(collection)

    _clear_segments()
    for action in list(bpy.data.actions):
        bpy.data.actions.remove(action, do_unlink=True)
    purged = _purge_orphans()

    remaining_bones = sorted(bone.name for bone in rig.data.bones)
    if remaining_bones != sorted(expectations["clean_bones"]):
        raise ExtractionError("Bone surgery did not produce the characterised clean bone set.")
    if sum(len(bone.constraints) for bone in rig.pose.bones) != 0:
        raise ExtractionError("Bone-level constraints survived the clean-rig surgery.")
    if bpy.data.actions or bpy.data.materials or bpy.data.meshes:
        raise ExtractionError("Clean-rig purge left actions, materials, or meshes behind.")

    _save_staged(staged)
    return {
        "removed_bone_count": partition["retarget_addition_count"],
        "clean_bone_count": partition["clean_bone_count"],
        "purged_datablocks": purged,
        "dangling_by_design": dangling,
    }


def _stage_neutral_template(
    source: Path, staged: Path, expectations: Mapping[str, Any]
) -> dict[str, Any]:
    _open_monolith(source)
    keep = {
        obj.name
        for obj in bpy.data.objects
        if _object_decision(obj) == "locomotion_template"
    }
    expected_keep = set(expectations["template_object_names"])
    if keep != expected_keep:
        raise ExtractionError(
            f"Template routing mismatch: extra={sorted(keep - expected_keep)}, "
            f"missing={sorted(expected_keep - keep)}"
        )
    keep_actions = set(expectations["template_action_frame_ranges"])
    dangling = _doomed_references(keep, keep_actions, set())
    _remove_objects_except(keep)

    for action in list(bpy.data.actions):
        decision = classify_action({"name": action.name})
        if decision.classification != "locomotion_template" or action.name not in keep_actions:
            bpy.data.actions.remove(action, do_unlink=True)
    for action in bpy.data.actions:
        if action.name not in keep_actions:
            raise ExtractionError(f"Unexpected template action survived: {action.name!r}")

    records = _segment_records()
    expected_segments = expectations["segments"]
    if len(records) != len(expected_segments):
        raise ExtractionError(
            f"Expected {len(expected_segments)} segments; the monolith carries {len(records)}."
        )
    blanked, provenance = blank_segment_prompts(records)
    segments = bpy.context.scene.kimodo.motion_segments
    for index, segment in enumerate(segments):
        segment.prompt = blanked[index]["prompt"]

    for action in bpy.data.actions:
        action.use_fake_user = True

    purged = _purge_orphans()
    if sorted(obj.name for obj in bpy.data.objects) != sorted(expected_keep):
        raise ExtractionError("Template object set changed during purge.")
    if sorted(a.name for a in bpy.data.actions) != sorted(keep_actions):
        raise ExtractionError("Template action set changed during purge.")
    if bpy.data.materials or bpy.data.meshes:
        raise ExtractionError("Template purge left materials or meshes behind.")

    ik = bpy.data.objects.get("Kimodo_IK")
    composition: dict[str, Any] = {
        "ik_mirror_note": None,
    }
    if ik is not None:
        collected = {bone.name for c in ik.data.collections for bone in c.bones}
        mirror = {bone.name for bone in ik.data.bones} - collected
        composition["ik_mirror_note"] = {
            "ik_bone_count": len(ik.data.bones),
            "mirrors_clean_rig_bone_names": sorted(mirror) == sorted(expectations["clean_bones"]),
        }
    _save_staged(staged)
    return {
        "blanked_prompts": provenance,
        "dangling_by_design": dangling,
        "composition_expectations": composition,
        "purged_datablocks": purged,
    }


def _stage_character_setup(
    source: Path, staged: Path, expectations: Mapping[str, Any]
) -> dict[str, Any]:
    _open_monolith(source)
    keep = {
        obj.name
        for obj in bpy.data.objects
        if _object_decision(obj) == "character_retarget_setup"
    }
    expected_keep = set(expectations["setup_object_names"])
    if keep != expected_keep:
        raise ExtractionError(
            f"Setup routing mismatch: extra={sorted(keep - expected_keep)}, "
            f"missing={sorted(expected_keep - keep)}"
        )
    dangling = _doomed_references(
        keep, set(), set(expectations["setup_material_names"])
    )
    _remove_objects_except(keep)

    for obj in bpy.data.objects:
        if obj.animation_data is not None:
            # Helper calibration lives in rest matrices, so clearing the
            # animation assignment never alters matrix_local values.
            obj.animation_data_clear()
    for action in list(bpy.data.actions):
        bpy.data.actions.remove(action, do_unlink=True)
    _clear_segments()

    purged = _purge_orphans()
    if sorted(obj.name for obj in bpy.data.objects) != sorted(expected_keep):
        raise ExtractionError("Setup object set changed during purge.")
    if bpy.data.actions:
        raise ExtractionError("Setup purge left actions behind.")

    _save_staged(staged)
    return {
        "dangling_by_design": dangling,
        "purged_datablocks": purged,
    }


def _image_records() -> list[dict[str, Any]]:
    """Record each image's stored path and whether it resolves from this file."""
    records: list[dict[str, Any]] = []
    for image in sorted(bpy.data.images, key=lambda i: i.name):
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


def _read_inventory(kind: str) -> dict[str, Any]:
    objects: list[dict[str, Any]] = []
    for obj in sorted(bpy.data.objects, key=lambda o: o.name):
        record: dict[str, Any] = {
            "name": obj.name,
            "type": obj.type,
            "pose_constraint_count": sum(len(bone.constraints) for bone in obj.pose.bones)
            if obj.pose
            else 0,
        }
        if obj.type == "ARMATURE":
            record["bone_count"] = len(obj.data.bones)
            record["bone_collections"] = {
                collection.name: len(collection.bones) for collection in obj.data.collections
            }
            record["ret_helper_bone_count"] = sum(
                1 for bone in obj.data.bones if bone.name.endswith(RET_HELPER_SUFFIX)
            )
        objects.append(record)
    inventory: dict[str, Any] = {
        "datablock_counts": {
            key: len(getattr(bpy.data, key))
            for key in ("objects", "meshes", "armatures", "actions", "materials", "images")
        },
        "objects": objects,
        "actions": [
            {
                "name": action.name,
                "frame_range": [
                    round(float(action.frame_range[0]), 3),
                    round(float(action.frame_range[1]), 3),
                ],
                "users": int(action.users),
                "fake_user": bool(action.use_fake_user),
            }
            for action in sorted(bpy.data.actions, key=lambda a: a.name)
        ],
        "materials": sorted(material.name for material in bpy.data.materials),
        "images": _image_records(),
        "segments": _segment_records(),
        "scene": {
            "unit_system": str(bpy.context.scene.unit_settings.system),
            "unit_scale": round(float(bpy.context.scene.unit_settings.scale_length), 9),
            "render_engine": str(bpy.context.scene.render.engine),
            "frame_range": [
                int(bpy.context.scene.frame_start),
                int(bpy.context.scene.frame_end),
            ],
        },
    }
    if kind == "canonical_source":
        rig = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
        inventory["bone_names"] = (
            sorted(bone.name for bone in rig.data.bones) if rig is not None else []
        )
    if kind == "character_setup":
        female = bpy.data.objects.get("Female")
        rig = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
        inventory["fingerprints"] = {
            "female_constraint_graph_sha256": (
                constraint_graph_fingerprint(_constraint_graph_edges(female))
                if female is not None
                else None
            ),
            "female_constraint_graph_edge_count": (
                len(_constraint_graph_edges(female)) if female is not None else 0
            ),
            "kimodo_source_constraint_graph_sha256": (
                constraint_graph_fingerprint(_constraint_graph_edges(rig))
                if rig is not None
                else None
            ),
            "kimodo_source_helper_configuration_sha256": (
                helper_configuration_fingerprint(_helper_configuration_entries(rig))
                if rig is not None
                else None
            ),
        }
    return inventory


_STAGERS = {
    "canonical_source": _stage_canonical_source,
    "neutral_template": _stage_neutral_template,
    "character_setup": _stage_character_setup,
}


REPOSITORY_FIXED_KEYS = ("canonical_source", "neutral_template")


def _rebased_destinations(
    destinations: Mapping[str, Path], publish_root: str | Path | None
) -> dict[str, Path]:
    """Rebase only the repository-fixed artefacts beneath an optional publish root.

    The character setup destination always follows the caller-supplied
    character asset root, so disposable test publication only remaps the two
    repository-owned artefact paths.
    """
    if publish_root is None:
        return dict(destinations)
    root = Path(publish_root).expanduser().resolve()
    root.mkdir(parents=True, exist_ok=True)
    rebased: dict[str, Path] = {}
    for key, destination in destinations.items():
        if key not in REPOSITORY_FIXED_KEYS:
            rebased[key] = destination
            continue
        try:
            relative = destination.relative_to(repository_root())
        except ValueError as error:
            raise ExtractionError(
                f"Cannot rebase artefact {key} destination {destination} beneath {root}: "
                "it is not beneath the repository root."
            ) from error
        rebased[key] = root / relative
    return rebased


def extract_bootstrap(
    blend_path: str | Path | None = None,
    character_asset_root: str | Path | None = None,
    variant: str | None = None,
    temp_root: str | Path | None = None,
    manifest_path: str | Path | None = None,
    publish_root: str | Path | None = None,
    repository_root_path: str | Path | None = None,
) -> dict[str, Any]:
    """Stage, validate, and publish the three bootstrap artefacts transactionally."""
    if variant is None:
        raise ExtractionError("--variant is required for bootstrap artefact path derivation.")
    source = (
        Path(blend_path).expanduser().resolve()
        if blend_path is not None
        else default_blend_path()
    )
    if not source.is_file():
        raise ExtractionError(f"Monolithic authoring blend does not exist: {source}")
    manifest_file = (
        Path(manifest_path).expanduser().resolve()
        if manifest_path is not None
        else default_manifest_path()
    )
    if not manifest_file.is_file():
        raise ExtractionError(f"Phase 1 manifest does not exist: {manifest_file}")
    manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
    expectations = expectations_from_manifest(manifest)

    try:
        paths = derive_artefact_paths(
            character_asset_root if character_asset_root is not None else default_character_asset_root(),
            variant,
            Path(repository_root_path).expanduser().resolve()
            if repository_root_path is not None
            else None,
        )
    except (BootstrapPathError, ValueError) as error:
        raise ExtractionError(str(error)) from error

    destinations = _rebased_destinations(
        {
            "canonical_source": paths.canonical_source,
            "neutral_template": paths.neutral_template,
            "character_setup": paths.character_setup,
        },
        publish_root,
    )

    hash_before = _sha256(source)
    if hash_before != expectations["source_blend_sha256"]:
        raise ExtractionError(
            f"Monolith digest {hash_before} does not match the characterised "
            f"{expectations['source_blend_sha256']}; refusing to extract an uncharacterised blend."
        )

    staging_root = resolve_temp_root(temp_root) / EXTRACTION_DIRECTORY_NAME
    staged_directory = staging_root / "staged"
    staged_directory.mkdir(parents=True, exist_ok=True)
    destinations_before = {key: destination.exists() for key, destination in destinations.items()}

    staged_notes: dict[str, Any] = {}
    staged_paths: dict[str, Path] = {}
    for key in ARTEFACT_KEYS:
        staged = staged_directory / f"{key}.blend"
        staged_notes[key] = _STAGERS[key](source, staged, expectations)
        staged_paths[key] = staged

    artefacts: dict[str, Any] = {}
    for key in ARTEFACT_KEYS:
        ensure_registered()
        bpy.ops.wm.open_mainfile(
            filepath=str(staged_paths[key]), load_ui=False, use_scripts=False
        )
        inventory = _read_inventory(key)
        failures = _VALIDATORS[key](inventory, expectations)
        artefacts[key] = {
            "destination": str(destinations[key]),
            "staged": str(staged_paths[key]),
            "replaced_existing": destinations_before[key],
            "validation": {"passed": not failures, "failures": failures},
            "inventory": inventory,
        }

    validation_failures = {
        key: artefacts[key]["validation"]["failures"] for key in ARTEFACT_KEYS
    }
    if any(validation_failures.values()):
        raise ExtractionError(
            "Staged artefacts failed structural validation; nothing was published: "
            + json.dumps(validation_failures, indent=2)
        )

    hash_after = _sha256(source)
    if hash_after != hash_before:
        raise ExtractionError(
            f"The monolith changed during extraction ({hash_before} -> {hash_after})."
        )

    # Group-atomic publish: stage every copy beside its destination first, then
    # replace them in one tight loop so the mixed-state window is minimal and a
    # failed copy leaves no partial replacement or stray publish copies.
    publish_copies: dict[str, Path] = {}
    try:
        for key in ARTEFACT_KEYS:
            destination = destinations[key]
            destination.parent.mkdir(parents=True, exist_ok=True)
            publish = destination.with_name(f".{destination.name}.kimodo-publish")
            shutil.copy2(staged_paths[key], publish)
            publish_copies[key] = publish
        for key in ARTEFACT_KEYS:
            os.replace(publish_copies[key], destinations[key])
            artefacts[key]["published"] = True
            artefacts[key]["sha256"] = _sha256(destinations[key])
    except OSError:
        for publish in publish_copies.values():
            publish.unlink(missing_ok=True)
        raise

    artefacts["neutral_template"]["blanked_prompts"] = staged_notes["neutral_template"][
        "blanked_prompts"
    ]
    report = {
        "schema": REPORT_SCHEMA,
        "generated_by": "kimodo_pipeline/extract_bootstrap.py",
        "blender_version": ".".join(str(component) for component in bpy.app.version),
        "source_blend": str(source),
        "source_blend_sha256_before": hash_before,
        "source_blend_sha256_after": hash_after,
        "manifest_path": str(manifest_file),
        "manifest_source_blend_sha256": expectations["source_blend_sha256"],
        "variant": paths.variant,
        "character_asset_root": str(paths.character_asset_root),
        "publish_root": str(publish_root) if publish_root is not None else None,
        "artefacts": artefacts,
        "template_blank_prompts": staged_notes["neutral_template"]["blanked_prompts"],
        "dangling_by_design": {
            key: staged_notes[key]["dangling_by_design"] for key in ARTEFACT_KEYS
        },
        "composition_expectations": staged_notes["neutral_template"].get(
            "composition_expectations"
        ),
        "staging_notes": {
            key: {
                field: value
                for field, value in notes.items()
                if field not in ("blanked_prompts", "dangling_by_design")
            }
            for key, notes in staged_notes.items()
        },
        "monolith_unchanged": True,
    }
    structural_failures = report_structure_failures(report)
    if structural_failures:
        raise ExtractionError(
            f"Extraction report failed its own structure contract: {structural_failures}"
        )
    report_path = staging_root / REPORT_FILE_NAME
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")

    return {
        "report_path": str(report_path),
        "source_blend": str(source),
        "source_blend_sha256": hash_after,
        "monolith_unchanged": True,
        "artefacts": {
            key: {
                "destination": artefacts[key]["destination"],
                "sha256": artefacts[key]["sha256"],
                "replaced_existing": artefacts[key]["replaced_existing"],
                "validation_passed": artefacts[key]["validation"]["passed"],
            }
            for key in ARTEFACT_KEYS
        },
    }
