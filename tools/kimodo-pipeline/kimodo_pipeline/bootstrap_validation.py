"""Pure validation rules and report checks for the bootstrap extraction.

Every function here operates on plain data (mappings, sequences, scalars) so
the extraction contract can be unit-tested without Blender.  The bpy-dependent
extraction command extracts plain inventories from reopened staged blends and
feeds them to these rules; expectations are derived from the Phase 1 manifest
so the extraction validates against the characterised monolith rather than a
second, drifting copy of its structure.

The workspace rules below follow the same discipline for the Phase 3 authoring
workspace: the bpy-dependent composition command derives every expectation
dynamically from the current setup and template artefacts in the same run, so
manual clean-ups between runs change expectations rather than failing them.
"""
from __future__ import annotations

import math
from typing import Any, Iterable, Mapping, Sequence

from .bootstrap_policy import (
    RETARGET_BONE_COLLECTION,
    ROOT_BONE_NAME,
    SOURCE_ARMATURE_NAME,
    VARIANT_MATERIALISED_ACTION_PREFIX,
    VARIANT_SOURCE_ACTION_PREFIX,
)

ARTEFACT_KEYS = ("canonical_source", "neutral_template", "character_setup")
REPORT_SCHEMA = "kimodo-bootstrap-extraction/1"
REPORT_FILE_NAME = "bootstrap_extraction_report.json"
EXTRACTION_DIRECTORY_NAME = "kimodo-bootstrap-extraction"

WORKSPACE_REPORT_SCHEMA = "kimodo-bootstrap-workspace/1"
WORKSPACE_DIRECTORY_NAME = "kimodo-bootstrap-workspace"
WORKSPACE_REPORT_FILE_NAME = "bootstrap_workspace_report.json"

FRAME_EPSILON = 1e-6
ROOT_TRANSLATION_TOLERANCE = 1e-6
# The template's turn actions were baked through float32 quaternion/euler
# conversions, so the unrotated axes still carry ~2.4e-07 of quantisation
# noise.  A channel only counts as the rotation axis once it moves beyond
# that noise floor.
CHANNEL_DELTA_EPSILON = 1e-6


def expectations_from_manifest(manifest: Mapping[str, Any]) -> dict[str, Any]:
    """Derive the plain extraction expectations from a Phase 1 manifest."""
    try:
        object_classes = manifest["classification_summary"]["objects"]
        template_objects = sorted(
            name for name, bucket in object_classes.items() if bucket == "locomotion_template"
        )
        setup_objects = sorted(
            name for name, bucket in object_classes.items() if bucket == "character_retarget_setup"
        )
        template_actions = {
            record["name"]: list(record["frame_range"])
            for record in manifest["actions"]
            if record["classification"] == "locomotion_template"
        }
        setup_materials = sorted(
            record["name"] for record in manifest["materials"]
            if record["classification"] == "character_retarget_setup"
        )
        segments = [
            {
                "index": record["index"],
                "start_frame": record["start_frame"],
                "end_frame": record["end_frame"],
            }
            for record in manifest["scene"]["segments"]
        ]
        clean_rig = manifest["clean_rig_subset"]
        fingerprints = manifest["fingerprints"]
        expectations = {
            "template_object_names": template_objects,
            "setup_object_names": setup_objects,
            "template_action_frame_ranges": template_actions,
            "setup_material_names": setup_materials,
            "segments": segments,
            "clean_bones": sorted(clean_rig["clean_bones"]),
            "clean_bone_count": clean_rig["clean_bone_count"],
            "retarget_addition_bones": sorted(clean_rig["retarget_addition_bones"]),
            "retarget_addition_count": clean_rig["retarget_addition_count"],
            "total_bone_count": clean_rig["total_bone_count"],
            "source_blend_sha256": manifest["source_blend_sha256"],
            "female_constraint_graph_sha256": fingerprints["female_constraint_graph_sha256"],
            "female_constraint_graph_edge_count": fingerprints["female_constraint_graph_edge_count"],
            "kimodo_source_constraint_graph_sha256": fingerprints[
                "kimodo_source_constraint_graph_sha256"
            ],
            "kimodo_source_helper_configuration_sha256": fingerprints[
                "kimodo_source_helper_configuration_sha256"
            ],
            "kimodo_source_helper_bone_count": fingerprints["kimodo_source_helper_bone_count"],
        }
    except (KeyError, TypeError) as error:
        raise ValueError(f"Phase 1 manifest is missing extraction expectations: {error}") from error
    if not template_objects or not setup_objects or not template_actions:
        raise ValueError("Phase 1 manifest classifications are empty; cannot derive expectations.")
    return expectations


def blank_segment_prompts(
    segments: Sequence[Mapping[str, Any]],
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    """Return prompt-blanked segment records plus a provenance record of originals."""
    blanked: list[dict[str, Any]] = []
    provenance: list[dict[str, Any]] = []
    for segment in segments:
        blanked.append({**segment, "prompt": ""})
        provenance.append(
            {
                "index": segment.get("index"),
                "start_frame": segment.get("start_frame"),
                "end_frame": segment.get("end_frame"),
                "original_prompt": str(segment.get("prompt", "")),
            }
        )
    return blanked, provenance


def _object_records(inventory: Mapping[str, Any]) -> list[Mapping[str, Any]]:
    return list(inventory.get("objects", ()))


def _action_records(inventory: Mapping[str, Any]) -> list[Mapping[str, Any]]:
    return list(inventory.get("actions", ()))


def _datablock_count(inventory: Mapping[str, Any], key: str) -> int:
    return int(inventory.get("datablock_counts", {}).get(key, -1))


def _constraint_total(inventory: Mapping[str, Any]) -> int:
    return sum(int(record.get("pose_constraint_count", 0)) for record in _object_records(inventory))


def broken_image_path_failures(inventory: Mapping[str, Any]) -> list[str]:
    """Flag file-backed images whose stored path is relative or does not resolve.

    Published artefacts must carry absolute external paths: a stored ``//``
    path resolves against whichever directory the blend happens to sit in, so
    staging-to-publishing relocation silently breaks it.  Generated buffers
    (for example ``Render Result``) and packed images carry a ``None``
    existence flag and are not path-checked.
    """
    failures: list[str] = []
    for image in inventory.get("images", ()):
        if not isinstance(image, Mapping):
            continue
        filepath = str(image.get("filepath", ""))
        if not filepath or image.get("file_exists") is None:
            continue
        if filepath.startswith("//"):
            failures.append(
                f"image {image.get('name')!r} path must be absolute, not blend-relative: "
                f"{filepath!r}"
            )
        elif image.get("file_exists") is not True:
            failures.append(
                f"image {image.get('name')!r} path does not resolve from the blend: {filepath!r}"
            )
    return failures


def canonical_source_failures(
    inventory: Mapping[str, Any], expectations: Mapping[str, Any]
) -> list[str]:
    """Validate a reopened canonical clean-rig inventory against expectations."""
    failures: list[str] = []
    records = _object_records(inventory)
    if len(records) != 1 or records[0]["name"] != SOURCE_ARMATURE_NAME:
        failures.append(
            f"expected exactly one object named {SOURCE_ARMATURE_NAME}; "
            f"found {sorted(record['name'] for record in records)}"
        )
        return failures
    record = records[0]
    if record.get("type") != "ARMATURE":
        failures.append(f"{SOURCE_ARMATURE_NAME} must be an armature; found {record.get('type')}")
    bone_names = sorted(inventory.get("bone_names", ()))
    expected_bones = sorted(expectations["clean_bones"])
    if bone_names != expected_bones:
        extra = sorted(set(bone_names) - set(expected_bones))
        missing = sorted(set(expected_bones) - set(bone_names))
        failures.append(
            f"clean bone set mismatch: {len(bone_names)} bones (expected {len(expected_bones)}); "
            f"unexpected={extra[:8]}, missing={missing[:8]}"
        )
    collections = dict(record.get("bone_collections", {}))
    if RETARGET_BONE_COLLECTION in collections:
        failures.append(f"bone collection {RETARGET_BONE_COLLECTION!r} must be absent")
    if _constraint_total(inventory) != 0:
        failures.append("bone-level constraints must be entirely absent")
    if _datablock_count(inventory, "meshes") != 0 or any(
        item.get("type") == "MESH" for item in records
    ):
        failures.append("no meshes may remain in the canonical clean rig")
    if _datablock_count(inventory, "actions") != 0 or _action_records(inventory):
        failures.append("no actions may remain in the canonical clean rig")
    if _datablock_count(inventory, "materials") != 0 or inventory.get("materials"):
        failures.append("no materials may remain in the canonical clean rig")
    if inventory.get("segments"):
        failures.append("no semantic segments may remain in the canonical clean rig")
    failures.extend(broken_image_path_failures(inventory))
    return failures


def neutral_template_failures(
    inventory: Mapping[str, Any], expectations: Mapping[str, Any]
) -> list[str]:
    """Validate a reopened neutral-template inventory against expectations."""
    failures: list[str] = []
    records = _object_records(inventory)
    names = sorted(record["name"] for record in records)
    expected_names = sorted(expectations["template_object_names"])
    if names != expected_names:
        extra = sorted(set(names) - set(expected_names))
        missing = sorted(set(expected_names) - set(names))
        failures.append(
            f"template object set mismatch: {len(names)} objects (expected {len(expected_names)}); "
            f"unexpected={extra[:8]}, missing={missing[:8]}"
        )
    if any(record.get("type") == "MESH" for record in records):
        failures.append("no mesh objects may remain in the neutral template")
    if _datablock_count(inventory, "meshes") != 0:
        failures.append("no mesh datablocks may remain in the neutral template")
    if _datablock_count(inventory, "materials") != 0 or inventory.get("materials"):
        failures.append("no materials may remain in the neutral template")
    if SOURCE_ARMATURE_NAME in names:
        failures.append(f"{SOURCE_ARMATURE_NAME} must be absent from the neutral template")
    actions = {record["name"]: record for record in _action_records(inventory)}
    expected_actions = expectations["template_action_frame_ranges"]
    if sorted(actions) != sorted(expected_actions):
        failures.append(
            f"template actions mismatch: found {sorted(actions)}, expected {sorted(expected_actions)}"
        )
    for name, expected_range in sorted(expected_actions.items()):
        record = actions.get(name)
        if record is None:
            continue
        if int(record.get("users", 0)) < 1:
            failures.append(f"template action {name!r} lost all users")
        if list(record.get("frame_range", [])) != list(expected_range):
            failures.append(
                f"template action {name!r} frame placement {record.get('frame_range')} "
                f"drifted from {expected_range}"
            )
    segments = list(inventory.get("segments", ()))
    expected_segments = expectations["segments"]
    if len(segments) != len(expected_segments):
        failures.append(
            f"expected {len(expected_segments)} segments; found {len(segments)}"
        )
    for expected, actual in zip(expected_segments, segments):
        if (
            actual.get("start_frame") != expected["start_frame"]
            or actual.get("end_frame") != expected["end_frame"]
        ):
            failures.append(
                f"segment {expected['index']} frame range "
                f"[{actual.get('start_frame')}, {actual.get('end_frame')}] drifted from "
                f"[{expected['start_frame']}, {expected['end_frame']}]"
            )
        if str(actual.get("prompt", "")) != "":
            failures.append(f"segment {expected['index']} prompt must be blank")
    failures.extend(broken_image_path_failures(inventory))
    return failures


def character_setup_failures(
    inventory: Mapping[str, Any], expectations: Mapping[str, Any]
) -> list[str]:
    """Validate a reopened character-setup inventory against expectations."""
    failures: list[str] = []
    records = _object_records(inventory)
    names = sorted(record["name"] for record in records)
    expected_names = sorted(expectations["setup_object_names"])
    if names != expected_names:
        extra = sorted(set(names) - set(expected_names))
        missing = sorted(set(expected_names) - set(names))
        failures.append(
            f"setup object set mismatch: {len(names)} objects (expected {len(expected_names)}); "
            f"unexpected={extra[:8]}, missing={missing[:8]}"
        )
    template_leak = sorted(set(names) & set(expectations["template_object_names"]))
    if template_leak:
        failures.append(f"template objects leaked into the setup: {template_leak}")
    source = next((record for record in records if record["name"] == SOURCE_ARMATURE_NAME), None)
    if source is None:
        failures.append(f"{SOURCE_ARMATURE_NAME} must be present in the setup")
    else:
        if source.get("bone_count") != expectations["total_bone_count"]:
            failures.append(
                f"{SOURCE_ARMATURE_NAME} bone count {source.get('bone_count')} drifted from "
                f"{expectations['total_bone_count']}"
            )
        collections = dict(source.get("bone_collections", {}))
        if RETARGET_BONE_COLLECTION not in collections:
            failures.append(f"bone collection {RETARGET_BONE_COLLECTION!r} must be present")
        elif collections[RETARGET_BONE_COLLECTION] != expectations["retarget_addition_count"]:
            failures.append(
                f"{RETARGET_BONE_COLLECTION} membership "
                f"{collections[RETARGET_BONE_COLLECTION]} drifted from "
                f"{expectations['retarget_addition_count']}"
            )
        if source.get("ret_helper_bone_count") != expectations["kimodo_source_helper_bone_count"]:
            failures.append(
                f"helper bone count {source.get('ret_helper_bone_count')} drifted from "
                f"{expectations['kimodo_source_helper_bone_count']}"
            )
    fingerprints = dict(inventory.get("fingerprints", {}))
    expected_fingerprints = {
        "female_constraint_graph_sha256": expectations["female_constraint_graph_sha256"],
        "kimodo_source_constraint_graph_sha256": expectations[
            "kimodo_source_constraint_graph_sha256"
        ],
        "kimodo_source_helper_configuration_sha256": expectations[
            "kimodo_source_helper_configuration_sha256"
        ],
    }
    for key, expected in expected_fingerprints.items():
        if fingerprints.get(key) != expected:
            failures.append(
                f"{key} {fingerprints.get(key)} drifted from the Phase 1 fingerprint {expected}"
            )
    if fingerprints.get("female_constraint_graph_edge_count") != expectations[
        "female_constraint_graph_edge_count"
    ]:
        failures.append(
            f"Female constraint edge count {fingerprints.get('female_constraint_graph_edge_count')} "
            f"drifted from {expectations['female_constraint_graph_edge_count']}"
        )
    if _datablock_count(inventory, "actions") != 0 or _action_records(inventory):
        failures.append("no actions of any kind may remain in the character setup")
    if inventory.get("segments"):
        failures.append("no semantic segments may remain in the character setup")
    materials = sorted(inventory.get("materials", ()))
    if materials != sorted(expectations["setup_material_names"]):
        failures.append(
            f"setup materials mismatch: found {materials}, "
            f"expected {sorted(expectations['setup_material_names'])}"
        )
    failures.extend(broken_image_path_failures(inventory))
    return failures


def report_structure_failures(report: Mapping[str, Any]) -> list[str]:
    """Check the extraction report carries the contract structure."""
    failures: list[str] = []
    for key in (
        "schema",
        "generated_by",
        "blender_version",
        "source_blend",
        "source_blend_sha256_before",
        "source_blend_sha256_after",
        "manifest_path",
        "variant",
        "character_asset_root",
        "artefacts",
        "template_blank_prompts",
        "dangling_by_design",
        "monolith_unchanged",
    ):
        if key not in report:
            failures.append(f"report is missing the {key!r} field")
    if report.get("schema") != REPORT_SCHEMA:
        failures.append(f"report schema {report.get('schema')!r} is not {REPORT_SCHEMA!r}")
    artefacts = report.get("artefacts")
    if not isinstance(artefacts, Mapping) or sorted(artefacts) != sorted(ARTEFACT_KEYS):
        failures.append(f"report artefacts must cover exactly {sorted(ARTEFACT_KEYS)}")
        artefacts = None
    if artefacts is not None:
        for key in ARTEFACT_KEYS:
            entry = artefacts[key]
            for field in (
                "destination",
                "staged",
                "published",
                "replaced_existing",
                "sha256",
                "validation",
                "inventory",
            ):
                if field not in entry:
                    failures.append(f"artefact {key!r} is missing the {field!r} field")
            validation = entry.get("validation")
            if isinstance(validation, Mapping):
                if sorted(validation) != ["failures", "passed"]:
                    failures.append(f"artefact {key!r} validation must carry failures and passed")
                elif validation["passed"] != (not validation["failures"]):
                    failures.append(f"artefact {key!r} validation flags disagree with its failures")
            if not entry.get("sha256"):
                failures.append(f"artefact {key!r} has no output digest")
    hashes_match = (
        report.get("source_blend_sha256_before") == report.get("source_blend_sha256_after")
    )
    if not hashes_match:
        failures.append("the monolith digest changed during extraction")
    if report.get("monolith_unchanged") is not hashes_match:
        failures.append("the report does not attest an unchanged monolith")
    return failures


# ---------------------------------------------------------------------------
# Phase 3: authoring-workspace composition rules (pure, dynamically derived).
# ---------------------------------------------------------------------------


def _curve_channel_map(
    curves: Iterable[Mapping[str, Any]],
) -> dict[tuple[str, int], list[tuple[float, float]]]:
    """Collapse plain curve records into ``{(data_path, array_index): keys}``."""
    channel_map: dict[tuple[str, int], list[tuple[float, float]]] = {}
    for curve in curves:
        key = (str(curve["data_path"]), int(curve["array_index"]))
        channel_map.setdefault(key, [])
        for keyframe in curve.get("keyframes", ()):
            channel_map[key].append((float(keyframe[0]), float(keyframe[1])))
    return channel_map


def _distinct_frames(
    channel_map: Mapping[tuple[str, int], Sequence[tuple[float, float]]]
) -> list[float]:
    frames = sorted({frame for keys in channel_map.values() for frame, _ in keys})
    collapsed: list[float] = []
    for frame in frames:
        if not collapsed or abs(frame - collapsed[-1]) > FRAME_EPSILON:
            collapsed.append(frame)
    return collapsed


def idle_input_failures(
    idle: Mapping[str, Any], expectations: Mapping[str, Any]
) -> list[str]:
    """Validate a plain idle-input record against the Kimodo single-pose policy.

    ``idle`` carries the plain curve records of the candidate action plus the
    bone names of the workspace rig.  The rules are deliberately independent of
    Blender so the idle admission policy is unit-testable: all authored keys
    must share one source frame, every channel must address a pose bone of the
    workspace rig, values must be finite, and the Root bone may not carry a
    translation (a reference pose is authored in place).
    """
    failures: list[str] = []
    name = str(idle.get("action_name", "<unnamed>"))
    curves = list(idle.get("curves", ()))
    if not curves:
        return [f"idle action {name!r} carries no curves at all"]
    channel_map = _curve_channel_map(curves)
    frames = _distinct_frames(channel_map)
    if len(frames) != 1:
        failures.append(
            f"idle action {name!r} must author every key within ONE source frame; "
            f"found {len(frames)} distinct frames starting {frames[:4]}"
        )
    rig_bones = set(idle.get("rig_bone_names", ()))
    for (data_path, _), keys in sorted(channel_map.items()):
        if not data_path.startswith("pose.bones["):
            failures.append(
                f"idle action {name!r} carries a non-pose channel: {data_path!r}"
            )
            continue
        bone = data_path.split('"')[1] if '"' in data_path else ""
        if bone not in rig_bones:
            failures.append(
                f"idle action {name!r} channels bone {bone!r} absent from the workspace rig"
            )
        for frame, value in keys:
            if not (math.isfinite(frame) and math.isfinite(value)):
                failures.append(
                    f"idle action {name!r} carries a non-finite sample on {data_path!r}"
                )
                break
    tolerance = float(expectations.get("root_translation_tolerance", ROOT_TRANSLATION_TOLERANCE))
    root_location = f'pose.bones["{ROOT_BONE_NAME}"].location'
    for (data_path, _), keys in sorted(channel_map.items()):
        if data_path != root_location:
            continue
        for frame, value in keys:
            if abs(value) > tolerance:
                failures.append(
                    f"idle action {name!r} carries an unexpected Root translation "
                    f"{value!r} at frame {frame!r}; reference poses are authored in place"
                )
    return failures


def derive_rotation_conventions(
    idle_curves: Iterable[Mapping[str, Any]],
    turn_curves_by_name: Mapping[str, Iterable[Mapping[str, Any]]],
) -> dict[str, Any]:
    """Derive the template's rotation axis, angles, and frame placements.

    The template's reference actions encode one proven convention: each turn
    action equals the idle pose except on exactly one shared channel, keyed on
    its own single placement frame.  This reads that convention from plain
    curve records so the workspace regeneration honours whatever the current
    template proves rather than a hard-coded axis or angle.
    """
    if not turn_curves_by_name:
        raise ValueError("the template carries no pose-reference turn actions to derive from")
    idle_map = _curve_channel_map(idle_curves)
    if not idle_map:
        raise ValueError("the template idle action carries no curves")
    idle_frames = _distinct_frames(idle_map)
    if len(idle_frames) != 1:
        raise ValueError(
            f"the template idle action must occupy one frame; found {len(idle_frames)}"
        )
    for key, keys in sorted(idle_map.items()):
        if len(keys) != 1:
            raise ValueError(f"template idle channel {key} carries {len(keys)} keys; expected one")
    axis: tuple[str, int] | None = None
    turns: dict[str, dict[str, Any]] = {}
    for name in sorted(turn_curves_by_name):
        turn_map = _curve_channel_map(turn_curves_by_name[name])
        if set(turn_map) != set(idle_map):
            idle_only = sorted(set(idle_map) - set(turn_map))
            turn_only = sorted(set(turn_map) - set(idle_map))
            raise ValueError(
                f"template turn action {name!r} channel coverage differs from the idle "
                f"(idle-only {idle_only[:3]}, turn-only {turn_only[:3]})"
            )
        turn_frames = _distinct_frames(turn_map)
        if len(turn_frames) != 1:
            raise ValueError(
                f"template turn action {name!r} must occupy one frame; found {len(turn_frames)}"
            )
        differences = []
        for key in sorted(idle_map):
            idle_value = idle_map[key][0][1]
            turn_value = turn_map[key][0][1]
            if abs(turn_value - idle_value) > CHANNEL_DELTA_EPSILON:
                differences.append(key)
        if len(differences) != 1:
            raise ValueError(
                f"template turn action {name!r} must differ from the idle on exactly one "
                f"channel; found {len(differences)} ({differences[:4]})"
            )
        channel = differences[0]
        if axis is None:
            axis = channel
        elif axis != channel:
            raise ValueError(
                f"template turn actions rotate different channels: {axis} vs {channel} ({name!r})"
            )
        turns[name] = {
            "frame": turn_frames[0],
            "angle": turn_map[channel][0][1] - idle_map[channel][0][1],
        }
    assert axis is not None
    return {
        "axis_data_path": axis[0],
        "axis_array_index": axis[1],
        "idle_value": idle_map[axis][0][1],
        "idle_frame": idle_frames[0],
        "turns": turns,
        "curve_count": len(idle_map),
    }


def rotation_action_comparison(
    regenerated_curves: Iterable[Mapping[str, Any]],
    template_curves: Iterable[Mapping[str, Any]],
) -> dict[str, Any]:
    """Informational comparison of one regenerated action against the template original."""
    regenerated = _curve_channel_map(regenerated_curves)
    template = _curve_channel_map(template_curves)
    differing: list[str] = []
    max_abs_delta = 0.0
    frame_gap: list[float] = []
    for key in sorted(set(regenerated) | set(template)):
        if key not in regenerated or key not in template:
            differing.append(f"{key[0]}[{key[1]}] (coverage)")
            continue
        delta = abs(regenerated[key][0][1] - template[key][0][1])
        max_abs_delta = max(max_abs_delta, delta)
        frame_gap.append(abs(regenerated[key][0][0] - template[key][0][0]))
        if delta > CHANNEL_DELTA_EPSILON:
            differing.append(f"{key[0]}[{key[1]}]")
    return {
        "regenerated_curve_count": len(regenerated),
        "template_curve_count": len(template),
        "differing_channel_count": len(differing),
        "differing_channels": differing[:8],
        "max_abs_value_delta": round(max_abs_delta, 9),
        "max_abs_frame_delta": round(max(frame_gap), 9) if frame_gap else None,
    }


def workspace_failures(
    inventory: Mapping[str, Any], expectations: Mapping[str, Any]
) -> list[str]:
    """Validate a reopened authoring-workspace inventory against same-run expectations."""
    failures: list[str] = []
    records = _object_records(inventory)
    names = sorted(record["name"] for record in records)
    expected_names = sorted(
        set(expectations["setup_object_names"]) | set(expectations["template_object_names"])
    )
    if names != expected_names:
        extra = sorted(set(names) - set(expected_names))
        missing = sorted(set(expected_names) - set(names))
        failures.append(
            f"workspace object set mismatch: {len(names)} objects (expected {len(expected_names)}); "
            f"unexpected={extra[:8]}, missing={missing[:8]}"
        )
    sources = [record for record in records if record["name"] == SOURCE_ARMATURE_NAME]
    if len(sources) != 1:
        failures.append(
            f"exactly one {SOURCE_ARMATURE_NAME} object may exist; found {len(sources)}"
        )
    else:
        expected_source = expectations["kimodo_source"]
        if sources[0].get("type") != "ARMATURE":
            failures.append(f"{SOURCE_ARMATURE_NAME} must be an armature")
        if sources[0].get("bone_count") != expected_source["bone_count"]:
            failures.append(
                f"{SOURCE_ARMATURE_NAME} bone count {sources[0].get('bone_count')} drifted from "
                f"the setup input {expected_source['bone_count']}"
            )
        workspace_collections = dict(sources[0].get("bone_collections", {}))
        if workspace_collections != dict(expected_source["bone_collections"]):
            failures.append(
                f"{SOURCE_ARMATURE_NAME} bone collections "
                f"{sources[0].get('bone_collections')} drifted from the setup input "
                f"{expected_source['bone_collections']}"
            )
        if sources[0].get("pose_constraint_count") != expected_source["pose_constraint_count"]:
            failures.append(
                f"{SOURCE_ARMATURE_NAME} pose constraint count "
                f"{sources[0].get('pose_constraint_count')} drifted from the setup input "
                f"{expected_source['pose_constraint_count']}"
            )
    for mesh_name in sorted(expectations["setup_mesh_names"]):
        record = next((item for item in records if item["name"] == mesh_name), None)
        if record is None:
            failures.append(f"character mesh {mesh_name!r} is missing from the workspace")
        elif record.get("hide_viewport") or record.get("hide_render"):
            failures.append(
                f"character mesh {mesh_name!r} must stay visible "
                f"(hide_viewport={record.get('hide_viewport')}, hide_render={record.get('hide_render')})"
            )
    meshes = sorted(record["name"] for record in records if record.get("type") == "MESH")
    if meshes != sorted(expectations["setup_mesh_names"]):
        failures.append(
            f"workspace meshes {meshes} must equal the setup meshes; the template contributes none"
        )
    actions = {record["name"]: record for record in _action_records(inventory)}
    expected_actions = dict(expectations["action_placements"])
    if sorted(actions) != sorted(expected_actions):
        forbidden = sorted(
            name
            for name in actions
            if name.startswith((VARIANT_SOURCE_ACTION_PREFIX, VARIANT_MATERIALISED_ACTION_PREFIX))
        )
        failures.append(
            f"workspace actions must be exactly {sorted(expected_actions)}; found {sorted(actions)}"
            + (f" (variant leftovers: {forbidden})" if forbidden else "")
        )
    for name, frame in sorted(expected_actions.items()):
        record = actions.get(name)
        if record is None:
            continue
        key_frames = [float(value) for value in record.get("key_frames", ())]
        if key_frames != [float(frame)]:
            failures.append(
                f"reference action {name!r} key placement {key_frames} does not match the "
                f"template placement [{frame}]"
            )
        if record.get("curve_count") != expectations["reference_curve_count"]:
            failures.append(
                f"reference action {name!r} carries {record.get('curve_count')} curves; "
                f"the canonical idle carries {expectations['reference_curve_count']}"
            )
        if not record.get("fake_user"):
            failures.append(f"reference action {name!r} lost its fake user")
    segments = list(inventory.get("segments", ()))
    expected_segments = expectations["segments"]
    if len(segments) != len(expected_segments):
        failures.append(
            f"expected {len(expected_segments)} segments; found {len(segments)}"
        )
    for expected, actual in zip(expected_segments, segments):
        if (
            actual.get("start_frame") != expected["start_frame"]
            or actual.get("end_frame") != expected["end_frame"]
        ):
            failures.append(
                f"segment {expected['index']} frame range "
                f"[{actual.get('start_frame')}, {actual.get('end_frame')}] drifted from "
                f"[{expected['start_frame']}, {expected['end_frame']}]"
            )
        if str(actual.get("prompt", "")) != "":
            failures.append(f"segment {expected['index']} prompt must be blank")
    scene = dict(inventory.get("scene", {}))
    if scene.get("frame_range") != list(expectations["frame_range"]):
        failures.append(
            f"scene frame range {scene.get('frame_range')} must encompass the segment extents "
            f"{expectations['frame_range']}"
        )
    links = dict(inventory.get("scene_links", {}))
    for field in ("source_armature", "reuse_armature"):
        if links.get(field) != SOURCE_ARMATURE_NAME:
            failures.append(
                f"scene.kimodo.{field} must point at the workspace {SOURCE_ARMATURE_NAME}; "
                f"found {links.get(field)!r}"
            )
    constraints = list(links.get("motion_constraints", ()))
    expected_constraints = expectations["motion_constraints"]
    if len(constraints) != len(expected_constraints):
        failures.append(
            f"scene.kimodo.motion_constraints must re-bind {len(expected_constraints)} markers; "
            f"found {len(constraints)}"
        )
    else:
        for expected, actual in zip(expected_constraints, constraints):
            marker = actual.get("marker_object")
            if marker is None:
                failures.append(
                    f"motion constraint {expected['index']} ({expected.get('label')!r}) marker_object "
                    "is dangling"
                )
            elif marker not in names:
                failures.append(
                    f"motion constraint {expected['index']} marker {marker!r} is not a workspace object"
                )
            elif marker != expected["marker_object"]:
                failures.append(
                    f"motion constraint {expected['index']} marker re-bound to {marker!r} instead of "
                    f"the template's {expected['marker_object']!r}"
                )
    fingerprints = dict(inventory.get("fingerprints", {}))
    for key, expected in sorted(expectations["fingerprints"].items()):
        if fingerprints.get(key) != expected:
            failures.append(
                f"{key} {fingerprints.get(key)} drifted from the setup input {expected}"
            )
    failures.extend(broken_image_path_failures(inventory))
    return failures


def workspace_report_structure_failures(report: Mapping[str, Any]) -> list[str]:
    """Check the workspace report carries the Phase 3 contract structure."""
    failures: list[str] = []
    for key in (
        "schema",
        "generated_by",
        "blender_version",
        "variant",
        "character_asset_root",
        "inputs",
        "baseline",
        "workspace",
        "composition",
        "validation",
        "inventory",
    ):
        if key not in report:
            failures.append(f"workspace report is missing the {key!r} field")
    if report.get("schema") != WORKSPACE_REPORT_SCHEMA:
        failures.append(
            f"workspace report schema {report.get('schema')!r} is not {WORKSPACE_REPORT_SCHEMA!r}"
        )
    baseline = report.get("baseline")
    if not isinstance(baseline, Mapping) or sorted(baseline) != sorted(
        ("canonical_source", "neutral_template", "character_setup")
    ):
        failures.append("workspace report baseline must cover the three Gate 1 artefacts")
    elif not all(entry.get("matches_gate1") for entry in baseline.values()):
        failures.append("workspace report baseline does not attest the Gate 1 digests")
    inputs = report.get("inputs")
    if not isinstance(inputs, Mapping) or sorted(inputs) != sorted(
        ("canonical_source", "neutral_template", "character_setup", "idle_blend")
    ):
        failures.append(
            "workspace report inputs must cover the three artefacts plus the idle blend"
        )
        inputs = None
    if inputs is not None:
        for key, entry in inputs.items():
            if not isinstance(entry, Mapping):
                failures.append(f"workspace report input {key!r} must be a record")
                continue
            if entry.get("sha256_before") != entry.get("sha256_after") or not entry.get("unchanged"):
                failures.append(f"workspace report input {key!r} does not attest unchanged bytes")
    workspace = report.get("workspace")
    if isinstance(workspace, Mapping):
        for field in ("destination", "staged", "published", "replaced_existing", "sha256"):
            if field not in workspace:
                failures.append(f"workspace report workspace record lacks {field!r}")
        if workspace.get("published") is not True:
            failures.append("workspace report must attest a published workspace")
        if not workspace.get("sha256"):
            failures.append("workspace report has no workspace digest")
    else:
        failures.append("workspace report lacks the workspace record")
    validation = report.get("validation")
    if not isinstance(validation, Mapping) or sorted(validation) != ["failures", "passed"]:
        failures.append("workspace report validation must carry failures and passed")
    elif validation["passed"] != (not validation["failures"]):
        failures.append("workspace report validation flags disagree with its failures")
    elif validation["passed"] is not True:
        failures.append("workspace report must attest a passed validation")
    composition = report.get("composition")
    if isinstance(composition, Mapping):
        for field in (
            "merged_objects",
            "appended_collections",
            "idle",
            "rotation_generation",
            "segments",
            "motion_constraints_rebind",
            "scene_links",
            "frame_range",
            "fingerprints",
        ):
            if field not in composition:
                failures.append(f"workspace report composition lacks {field!r}")
    else:
        failures.append("workspace report lacks the composition record")
    return failures
