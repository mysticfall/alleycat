"""Read-only Blender source discovery for Kimodo ANIM-002 inputs."""
from __future__ import annotations

import re
from pathlib import Path
from typing import Any

import bpy

from .compatibility import ensure_registered
from .contracts import ROLE_REGISTRY, validate_variant


REFERENCE_ACTION_NAME = "pose_ref_idle"
SOURCE_ACTION_PREFIX = "kimodo_multi_"
_SOURCE_RIG_NAME = re.compile(r"kimodo[ _-]*source\Z", re.IGNORECASE)
_POSE_BONE_PATH = re.compile(r'^pose\.bones\["((?:[^"\\]|\\.)*)"\]')


class DiscoveryError(RuntimeError):
    """Raised when a supplied blend does not meet the generic source contract."""


def _source_rig() -> Any:
    matches = [
        obj
        for obj in bpy.data.objects
        if obj.type == "ARMATURE" and _SOURCE_RIG_NAME.fullmatch(obj.name)
    ]
    if not matches:
        raise DiscoveryError(
            "No Kimodo Source armature was found. Expected one ARMATURE named "
            "Kimodo Source (underscores, spaces, and hyphens are accepted)."
        )
    if len(matches) != 1:
        raise DiscoveryError("Expected exactly one Kimodo Source armature.")
    return matches[0]


def validate_action_for_rig(action: Any, rig: Any) -> None:
    """Ensure every pose-bone F-curve target exists on the supplied rig."""
    fcurves = _action_fcurves(action)
    if not fcurves:
        raise DiscoveryError(f"Action '{action.name}' has no F-curves.")
    rig_bones = {bone.name for bone in rig.pose.bones}
    for fcurve in fcurves:
        match = _POSE_BONE_PATH.match(fcurve.data_path)
        if match and bytes(match.group(1), "utf-8").decode("unicode_escape") not in rig_bones:
            raise DiscoveryError(
                f"Action '{action.name}' references a pose bone absent from "
                f"Kimodo Source: {match.group(1)!r}."
            )


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


def _segments() -> list[dict[str, Any]]:
    scene = bpy.context.scene
    settings = getattr(scene, "kimodo", None)
    segments = getattr(settings, "motion_segments", None) if settings else None
    if segments is None:
        raise DiscoveryError("The active scene has no Kimodo motion_segments collection.")
    observed = []
    for index, segment in enumerate(segments):
        start_frame = int(segment.start_frame)
        end_frame = int(segment.end_frame)
        if end_frame < start_frame:
            raise DiscoveryError(f"Kimodo segment {index} ends before it starts.")
        observed.append(
            {
                "index": index,
                "start_frame": start_frame,
                "end_frame": end_frame,
                "prompt": str(getattr(segment, "prompt", "")),
            }
        )
    declared_indices = {role.segment_index for role in ROLE_REGISTRY}
    missing = sorted(declared_indices - {segment["index"] for segment in observed})
    if missing:
        raise DiscoveryError(f"Kimodo scene is missing required segment indices: {missing}.")
    return observed


def discover(blend_path: str | Path, variant: str) -> dict[str, Any]:
    """Open a supplied blend without scripts and return dynamically observed inputs."""
    validate_variant(variant)
    source = Path(blend_path).expanduser().resolve()
    if not source.is_file():
        raise DiscoveryError(f"Source blend does not exist: {source}")
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(source), load_ui=False, use_scripts=False)
    rig = _source_rig()
    reference = bpy.data.actions.get(REFERENCE_ACTION_NAME)
    if reference is None:
        raise DiscoveryError(f"Required reference Action '{REFERENCE_ACTION_NAME}' was not found.")
    source_actions = sorted(
        (action for action in bpy.data.actions if action.name.startswith(SOURCE_ACTION_PREFIX)),
        key=lambda action: action.name,
    )
    if not source_actions:
        raise DiscoveryError(f"No source Actions matching '{SOURCE_ACTION_PREFIX}*' were found.")
    validate_action_for_rig(reference, rig)
    for action in source_actions:
        validate_action_for_rig(action, rig)
    return {
        "variant": variant,
        "source_blend": str(source),
        "source_rig": rig.name,
        "reference_action": reference.name,
        "source_actions": [action.name for action in source_actions],
        "segments": _segments(),
    }
