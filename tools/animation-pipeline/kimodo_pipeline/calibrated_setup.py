"""Calibrated Kimodo setup rig resolution and setup-parity gate (KIMODO-SET-002).

Idle-source bootstrap, workspace composition and native idle clip export open a
verified copy of a character's calibrated setup. These helpers resolve its
character target rig from the opened content, check the source/target binding,
bind slotted actions for evaluation, and refuse calibration drift against the
approved setup baseline before any output is written.
"""
from __future__ import annotations

import hashlib
from pathlib import Path
from typing import Any

import bpy

from .bootstrap_manifest import _constraint_graph_edges, _helper_configuration_entries
from .bootstrap_paths import BootstrapArtefactPaths
from .bootstrap_policy import SOURCE_ARMATURE_NAME
from .character_baselines import CharacterBaselineError, resolve_approved_character
from .setup_parity import parity_fingerprint_records, setup_parity_failures

__all__ = [
    "SOURCE_ARMATURE_NAME",
    "SetupError",
    "bind_action_slot",
    "capture_setup_parity_records",
    "resolve_target_rig",
    "validate_rigs",
    "verify_setup_parity",
]


class SetupError(RuntimeError):
    """A clear expected calibrated-setup failure."""


def _sha256_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def bind_action_slot(animation_data: Any, action: bpy.types.Action) -> None:
    """Bind a slotted action for evaluation (Blender 4.4+ slot requirement).

    Assigning ``animation_data.action`` binds a slot automatically only when the
    slot identifier matches the object. Idle donor actions carry history-named
    slots from their authoring armature (for example ``OBKimodo`` on a rig since
    renamed to ``Kimodo_Source``); such an assignment leaves ``action_slot``
    null and the action then evaluates no channels at all, so a bake of it
    records one frozen pose at every frame. Legacy-layout actions carry no
    slots and need no binding. A multi-slot action is refused rather than
    guessed.
    """
    slots = list(getattr(action, "slots", ()))
    if not slots or getattr(animation_data, "action_slot", None) is not None:
        return
    if len(slots) != 1:
        raise SetupError(
            f'Action "{action.name}" carries {len(slots)} slots; '
            "refusing to guess which slot drives the rig."
        )
    animation_data.action_slot = slots[0]


def source_constraints(target: bpy.types.Object, source: bpy.types.Object) -> list[Any]:
    return [constraint for bone in target.pose.bones for constraint in bone.constraints if constraint.target == source]


def _carries_root_retarget_binding(candidate: bpy.types.Object, source: bpy.types.Object) -> bool:
    """Whether ``candidate``'s Root bone is bound onto the source armature."""
    root = candidate.pose.bones.get("Root") if candidate.pose else None
    if root is None:
        return False
    types = {item.type for item in root.constraints if item.target == source}
    return {"COPY_LOCATION", "COPY_ROTATION"}.issubset(types)


def resolve_target_rig(
    explicit_name: str | None, source: bpy.types.Object | None
) -> bpy.types.Object:
    """Resolve the character target rig from the opened content (KIMODO-CHAR-001).

    Every calibrated retarget setup binds its character rig onto
    ``Kimodo_Source`` through Root COPY_LOCATION and COPY_ROTATION constraints,
    so that binding is the structural definition of the target rig and no
    character name is assumed.  ``explicit_name`` (``--target-rig``) selects
    among several bound armatures; it must never name the source rig itself.
    """
    if explicit_name is not None:
        candidate = bpy.data.objects.get(explicit_name)
        if candidate is None or candidate.type != "ARMATURE":
            raise SetupError(f'--target-rig names no armature named "{explicit_name}".')
        if candidate == source:
            raise SetupError(
                "--target-rig must name the character target rig, not the "
                f'"{SOURCE_ARMATURE_NAME}" source armature.'
            )
        return candidate
    if source is None or source.type != "ARMATURE":
        raise SetupError(
            f'The opened blend lacks the "{SOURCE_ARMATURE_NAME}" source armature '
            "required to resolve the character target rig."
        )
    candidates = sorted(
        (
            obj
            for obj in bpy.data.objects
            if obj.type == "ARMATURE"
            and obj != source
            and _carries_root_retarget_binding(obj, source)
        ),
        key=lambda obj: obj.name,
    )
    if len(candidates) == 1:
        return candidates[0]
    present = sorted(obj.name for obj in bpy.data.objects if obj.type == "ARMATURE")
    if not candidates:
        raise SetupError(
            "No character target armature carries the Root COPY_LOCATION and "
            f'COPY_ROTATION binding onto "{SOURCE_ARMATURE_NAME}"; armatures present: '
            f"{present}."
        )
    raise SetupError(
        "Multiple armatures carry the Root retarget binding onto "
        f'"{SOURCE_ARMATURE_NAME}" ({[obj.name for obj in candidates]}); name one '
        "explicitly with --target-rig."
    )


def validate_rigs(target: bpy.types.Object | None, source: bpy.types.Object | None) -> None:
    if target is None or target.type != "ARMATURE":
        raise SetupError("The resolved character target rig is not an armature.")
    if source is None or source.type != "ARMATURE":
        raise SetupError(f'Expected source armature named "{SOURCE_ARMATURE_NAME}".')
    if target.pose.bones.get("Root") is None:
        raise SetupError(f'{target.name} has no required pose bone "Root".')
    constraints = source_constraints(target, source)
    if not constraints:
        raise SetupError(
            f"{target.name} has no visual constraints targeting {SOURCE_ARMATURE_NAME}."
        )
    root_constraints = [item for item in target.pose.bones["Root"].constraints if item.target == source]
    types = {item.type for item in root_constraints}
    if not {"COPY_LOCATION", "COPY_ROTATION"}.issubset(types):
        raise SetupError(
            f"{target.name}:Root must have {SOURCE_ARMATURE_NAME} COPY_LOCATION and "
            "COPY_ROTATION constraints."
        )


def capture_setup_parity_records(explicit_target_rig: str | None) -> dict[str, Any]:
    """Capture the calibration-relevant rig records of the currently opened blend.

    The rows mirror the plain manifest captures: the target and source
    constraint graphs (whose edges carry constraint mute state and influence)
    plus the ``_RET`` helper rest and fixed pose matrix_basis entries.
    The setup-parity gate compares two of these
    records, so the calibration state it protects is exactly the state named
    by KIMODO-SET-003.
    """
    source = bpy.data.objects.get(SOURCE_ARMATURE_NAME)
    if source is None or source.type != "ARMATURE":
        raise SetupError(
            f'The opened blend lacks the "{SOURCE_ARMATURE_NAME}" source armature '
            "required by the setup-parity gate."
        )
    target = resolve_target_rig(explicit_target_rig, source)
    # No evaluation-frame choice can recover authored calibration from an
    # animated helper. Refuse ambiguous inputs instead of sampling an action or
    # clearing animation (which leaves the last evaluated channel values behind).
    # Check all resident actions, including the unbound donor staged for baking,
    # and drivers. NLA actions are resident too. This is deliberately fail-closed.
    helper_paths = tuple(
        bone.path_from_id() for bone in source.pose.bones
        if bone.name.endswith("_RET")
    )
    curves = []
    for action in bpy.data.actions:
        if action.is_action_legacy:
            curves.extend(action.fcurves)
        else:
            for layer in action.layers:
                for strip in layer.strips:
                    for bag in strip.channelbags:
                        curves.extend(bag.fcurves)
    if source.animation_data is not None:
        curves.extend(source.animation_data.drivers)
    for curve in curves:
        if curve.data_path.startswith(helper_paths):
            raise SetupError(
                "Setup-parity gate refused the bake: fixed helper calibration "
                f"is animated or driven at {curve.data_path!r}; cannot establish "
                "action-independent pose matrix_basis calibration."
            )
    return {
        "target_rig": target.name,
        "target_constraint_edges": _constraint_graph_edges(target),
        "source_constraint_edges": _constraint_graph_edges(source),
        "helper_entries": _helper_configuration_entries(source, include_fixed_pose=True),
    }


def _capture_parity_from_blend(path: Path, explicit_target_rig: str | None) -> dict[str, Any]:
    bpy.ops.wm.open_mainfile(filepath=str(path), load_ui=False, use_scripts=False)
    return capture_setup_parity_records(explicit_target_rig)


def verify_setup_parity(
    artefacts: BootstrapArtefactPaths,
    source_path: Path,
    explicit_target_rig: str | None,
    approved_character_baselines: Any = None,
    *,
    gate_name: str = "Setup-parity gate",
) -> dict[str, Any]:
    """Bind any bake to the character's approved setup baseline (KIMODO-SET-002).

    The gate refuses before any bake or publication output.  First the three
    bootstrap inputs must hash to one approved character baseline (byte-digest
    binding, exactly as workspace composition requires).  Then the selected
    source's setup-derived rig content must fingerprint-match the approved
    setup's calibration — constraint mute state plus helper rest and fixed pose
    matrix_basis — so an un-calibrated staged copy (for example helpers reset to
    identity or Locked Tracks un-muted) is refused as calibration drift with a
    diagnostic naming the drifted state, not as opaque byte drift.
    """
    baseline_files = {
        "canonical_source": artefacts.canonical_source,
        "neutral_template": artefacts.neutral_template,
        "character_setup": artefacts.character_setup,
    }
    for key, path in baseline_files.items():
        if not path.is_file():
            raise SetupError(
                f'{gate_name} requires the approved bootstrap input '
                f'"{key}": {path}'
            )
    digests = {key: _sha256_file(path) for key, path in baseline_files.items()}
    try:
        character, _expected = resolve_approved_character(digests, approved_character_baselines)
    except CharacterBaselineError as error:
        raise SetupError(f"{gate_name}: {error}") from error
    try:
        approved_records = _capture_parity_from_blend(
            baseline_files["character_setup"], explicit_target_rig
        )
        actual_records = _capture_parity_from_blend(source_path, explicit_target_rig)
    except SetupError as error:
        raise SetupError(f"{gate_name} refused the bake: {error}") from error
    failures = setup_parity_failures(approved_records, actual_records)
    if failures:
        raise SetupError(
            f"{gate_name} refused the bake: the source's rig content "
            "does not match the approved calibrated setup (calibration drift): "
            + "; ".join(failures)
        )
    return {
        "baseline_character": character,
        "setup_sha256": digests["character_setup"],
        "source_sha256": _sha256_file(source_path),
        "setup_parity": parity_fingerprint_records(approved_records),
    }
