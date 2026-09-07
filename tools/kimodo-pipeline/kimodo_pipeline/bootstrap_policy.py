"""Pure classification policy and fingerprints for the bootstrap manifest.

Every function here operates on plain data (mappings, sequences, scalars) so
the extraction policy can be unit-tested without Blender.  The bpy-dependent
manifest builder extracts records from a live blend and feeds them to this
module; the fingerprints digest the same plain structures that the studied
``tools/kimodo-pipeline/kimodo_pipeline/blender_retargeting.py`` validation helpers capture.
"""
from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass
from typing import Any, Iterable, Mapping, Sequence

TARGET_ARMATURE_NAME = "Female"
SOURCE_ARMATURE_NAME = "Kimodo_Source"
IK_ARMATURE_NAME = "Kimodo_IK"
CHARACTER_MESH_PREFIX = "Female."
POSE_REFERENCE_PREFIX = "Kimodo_PoseRef_"
WAYPOINT_PREFIX = "Kimodo_Waypoint_"
RETARGET_BONE_COLLECTION = "Retarget Bones"
RET_HELPER_SUFFIX = "_RET"
LOOK_HELPER_SUFFIX = "_LOOK"
ROOT_BONE_NAME = "Root"

POSE_REFERENCE_ACTION_PREFIX = "pose_ref_"
POSE_REFERENCE_IDLE_ACTION_NAME = "pose_ref_idle"
VARIANT_SOURCE_ACTION_PREFIX = "kimodo_multi_"
VARIANT_MATERIALISED_ACTION_PREFIX = "loc_"

CLASSIFICATIONS = (
    # The "kimodo_source" bucket is intentionally never assigned by
    # ``classify_object``: the canonical clean rig is not a separate object in
    # the monolith but is derived at extraction time from the bound
    # ``Kimodo_Source`` via the bone-level complement in ``partition_clean_rig``.
    "kimodo_source",
    "locomotion_template",
    "character_retarget_setup",
    "variant_discard",
)

_VARIANT_MATERIALISED_ACTION = re.compile(
    rf"^{re.escape(VARIANT_MATERIALISED_ACTION_PREFIX)}[a-z][a-z0-9]*(?:_[a-z0-9]+)+\Z"
)


@dataclass(frozen=True)
class Classification:
    """The single artefact bucket proposed for one datablock, with rationale."""

    classification: str
    rationale: str
    confidence: str = "high"


def classify_object(record: Mapping[str, Any]) -> Classification:
    """Propose the extraction bucket for one plain object record."""
    name = str(record.get("name", ""))
    if name == TARGET_ARMATURE_NAME:
        return Classification(
            "character_retarget_setup",
            "Target armature carrying the working retarget binding onto Kimodo_Source.",
        )
    if name.startswith(CHARACTER_MESH_PREFIX):
        return Classification(
            "character_retarget_setup",
            "Character mesh parented to the target armature with its own material.",
        )
    if name == SOURCE_ARMATURE_NAME:
        return Classification(
            "character_retarget_setup",
            "Source armature is currently bound into Female via *_RET helper constraints; "
            "kept whole so the setup preserves the working binding.",
        )
    if name == IK_ARMATURE_NAME:
        return Classification(
            "locomotion_template",
            "Neutral IK control rig shared by every locomotion authoring workspace.",
        )
    if name.startswith(POSE_REFERENCE_PREFIX):
        return Classification(
            "locomotion_template",
            "Pose-reference armature structure for bootstrap-supplied reference poses.",
        )
    if name.startswith(WAYPOINT_PREFIX):
        return Classification(
            "locomotion_template",
            "Waypoint carrier for prompt-driven root motion constraints.",
        )
    return Classification(
        "variant_discard",
        "Object matches no bootstrap contract rule; defaulted to discard pending review.",
        confidence="low",
    )


def classify_material(record: Mapping[str, Any]) -> Classification:
    """Propose the extraction bucket for one plain material record."""
    name = str(record.get("name", ""))
    if name.startswith(CHARACTER_MESH_PREFIX):
        return Classification(
            "character_retarget_setup",
            "Material used only by the character's own meshes.",
        )
    return Classification(
        "variant_discard",
        "Material matches no character prefix; defaulted to discard pending review.",
        confidence="low",
    )


def classify_action(record: Mapping[str, Any]) -> Classification:
    """Propose the extraction bucket for one plain action record."""
    name = str(record.get("name", ""))
    if name.startswith(POSE_REFERENCE_ACTION_PREFIX):
        return Classification(
            "locomotion_template",
            "Pose-reference action structure retained; idle/turn content will be "
            "bootstrap-supplied.",
        )
    if name.startswith(VARIANT_SOURCE_ACTION_PREFIX):
        return Classification(
            "variant_discard",
            "Variant-generated multi-prompt source action; regenerated per variant.",
        )
    if _VARIANT_MATERIALISED_ACTION.fullmatch(name):
        return Classification(
            "variant_discard",
            "Materialised variant catalogue action; regenerated per variant.",
        )
    return Classification(
        "variant_discard",
        "Action matches no bootstrap contract rule; defaulted to discard pending review.",
        confidence="low",
    )


def partition_clean_rig(
    bone_names: Iterable[str], retarget_collection_bones: Iterable[str]
) -> dict[str, Any]:
    """Split a source armature's bones into retarget additions and clean rig.

    The clean ``kimodo_source`` rig is the complement of the bones the
    retarget binding added (the ``Retarget Bones`` collection members, which
    cover the ``*_RET`` helpers and their ``*_LOOK`` locked-track targets).
    """
    total = sorted(set(bone_names))
    additions = sorted(set(retarget_collection_bones))
    unknown = sorted(set(additions) - set(total))
    if unknown:
        raise ValueError(
            f"Retarget collection names bones absent from the armature: {unknown}"
        )
    clean = sorted(set(total) - set(additions))
    return {
        "total_bone_count": len(total),
        "retarget_addition_bones": additions,
        "retarget_addition_count": len(additions),
        "ret_helper_bones": [bone for bone in additions if bone.endswith(RET_HELPER_SUFFIX)],
        "look_target_bones": [bone for bone in additions if bone.endswith(LOOK_HELPER_SUFFIX)],
        "clean_bones": clean,
        "clean_bone_count": len(clean),
    }


def _normalise(value: Any) -> Any:
    if isinstance(value, float):
        return value + 0.0
    if isinstance(value, (list, tuple)):
        return [_normalise(item) for item in value]
    return value


def canonical_digest(entries: Sequence[Sequence[Any]]) -> str:
    """Digest ordered plain entries into a stable structural fingerprint."""
    payload = json.dumps(
        [_normalise(list(entry)) for entry in entries],
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=True,
        allow_nan=False,
    )
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def constraint_graph_fingerprint(edges: Sequence[Sequence[Any]]) -> str:
    """Fingerprint a retarget constraint graph as plain edge tuples.

    Each edge mirrors ``blender_retargeting.constraint_graph``:
    bone name, slot index, type, target name, subtarget, owner space, target
    space, mute flag, and influence rounded by the caller.
    """
    return canonical_digest(edges)


def helper_configuration_fingerprint(entries: Sequence[Sequence[Any]]) -> str:
    """Fingerprint retarget helper bones as plain configuration tuples.

    Each entry mirrors ``helper_configuration`` in the same studied module:
    bone name, parent name, rest (``matrix_local``) values, and pose-basis
    values, all rounded by the caller.
    """
    return canonical_digest(entries)
