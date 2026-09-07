"""Transactional, provenance-bearing repair of unsafe real Kimodo source cycles.

The tool never invents motion.  It derives a role-confined Action from a real
source window, keeps the middle samples immutable, and applies the same
hierarchy-space two-ended construction used by the materialiser to the two
boundary windows.  The production source is intentionally not a default.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import shutil
import sys
import tempfile
from typing import Any, Mapping, Sequence

import bpy
from mathutils import Matrix, Vector

from .compatibility import ensure_registered
from .arc_semantics import arc_body_support_evidence, arc_body_support_rank_key, arc_curve_evidence
from .discovery import validate_action_for_rig
from .loop_seam import (
    HIERARCHY_CADENCE_CONTRACT,
    REPAIR_BLEND_PROFILES,
    REPAIR_CONTRACT,
    adaptive_repair_specs,
    assess_foot_lift_apex_preservation,
    repair_naturalness_key,
    symmetric_window,
    validate_durable_repair_windows,
)
from .lateral_basis import LateralBasisError, anatomical_lateral_basis
from .contracts import ROLE_REGISTRY
from .materialise import (
    EULER_OUTPUT_MODE,
    ROOT as ROOT_PATH,
    _action_fcurves,
    _action_signature,
    _activate_action,
    _anatomical_character_scale,
    _candidate_quality_metrics,
    _capture_pivot_transform_oracle,
    _cycle_normalised_source_transform,
    _channels_from_armature_rows,
    _evaluated_body_relative,
    _foot_geometry,
    _align_about,
    _matrix_error,
    _motion_bone_names,
    _natural_recurrence_evidence,
    _repair_periodic_pose_rows,
    _reconstruct_root,
    _rig,
    _root_local_translation,
    role_evaluated_hierarchy_transform,
    _straight_source_projection,
)
from .discovery import _segments
from .selection import quality_evidence
from .selection import role_window
from .trajectory_normalisation import (
    TRAJECTORY_NORMALISER_VERSION,
    TrajectoryNormalisationError,
    normalise_xr_trajectory,
)
from .precision_step import (
    PRECISION_CALIBRATION_BASELINE_ACTION,
    PRECISION_FOOT_ORDER_ALGORITHM,
    PrecisionStepLimits,
    PrecisionStepError,
    assess_precision_foot_order,
    assess_precision_semantic_events,
    select_precision_candidate,
    precision_candidate_rank_key,
)
from .leg_silhouette import (
    PRECISION_LEG_SILHOUETTE_ALGORITHM,
    LegSilhouetteError,
    assess_precision_leg_silhouette,
)
from .pivot import (
    PivotHeadingProjection,
    pivot_three_cycle_evidence,
    project_pivot_heading,
    validate_pivot_repair_oracles,
)

ALGORITHM_VERSION = "real_source_semantic_repair_dispatch_v10"
LEGACY_ALGORITHM_VERSIONS = frozenset({
    "real_source_semantic_repair_dispatch_v8",
    "real_source_semantic_repair_dispatch_v9",
})
COMBINED_ALGORITHM_VERSION = "real_source_combined_semantic_repair_v1"
BOUNDARY_ALGORITHM_VERSION = "real_source_symmetric_hierarchy_boundary_repair_v1"
RESHAPE_ALGORITHM_VERSION = "root_relative_bilateral_two_bone_ik_lateral_reshape_v1"
ARC_ANATOMICAL_FIT_ALGORITHM_VERSION = "root_relative_arc_bilateral_two_bone_ik_anatomical_fit_v1"
PROVENANCE_PROPERTY = "anim002_source_content_repair"
ROLE_RESTRICTION_PROPERTY = "anim002_source_roles"

# These are source-content admission limits, not materialisation tolerances.
# They prevent a boundary repair from becoming wholesale motion authoring.
MAXIMUM_SOURCE_TRANSLATION_SCALE = 0.38
MAXIMUM_SOURCE_ROTATION_RADIANS = 0.55

# The selection gate is unchanged at 0.70.  This target provides only a 0.01
# rig-scale safety margin, avoiding arbitrary over-compression.
RESHAPE_TARGET_LATERAL_WIDTH = 0.69
RESHAPE_MINIMUM_SCALE = 0.60
RESHAPE_BLEND_FRAMES = 6
MAXIMUM_LIMB_LENGTH_ERROR = 1e-6
MINIMUM_KNEE_ORIENTATION_DOT = 0.90
MAXIMUM_CONTACT_HEIGHT_DELTA_SCALE = 1e-5
MAXIMUM_PLANTED_VELOCITY_INCREASE_SCALE = 0.02
MAXIMUM_TRAJECTORY_CONTACT_VELOCITY_DELTA_SCALE = 0.05

LEG_CHAINS = {
    "Left": ("LeftLeg", "LeftShin", "LeftFoot"),
    "Right": ("RightLeg", "RightShin", "RightFoot"),
}


class SourceRepairError(RuntimeError):
    """Raised before publication when a source repair is unsafe or incomplete."""


def _json_hash(value: Any) -> str:
    return hashlib.sha256(
        json.dumps(value, sort_keys=True, separators=(",", ":"), default=str).encode("utf-8")
    ).hexdigest()


def action_signature(action: Any) -> str:
    """Return a stable content signature independent of Blender datablock pointers."""
    return _json_hash(_action_signature(action))


def _exclude_unrelated_character_objects(rig: Any) -> list[tuple[Any, Any]]:
    """Temporarily unlink non-Kimodo character rigs and descendants from evaluation."""
    other_rigs = {
        obj for obj in bpy.context.scene.objects
        if obj.type == "ARMATURE" and obj != rig and not obj.name.startswith("Kimodo_PoseRef_")
    }
    unrelated = []
    for obj in bpy.context.scene.objects:
        ancestor = obj
        while ancestor.parent is not None:
            ancestor = ancestor.parent
        if obj in other_rigs or ancestor in other_rigs:
            unrelated.append(obj)
    links = [
        (obj, collection)
        for obj in unrelated
        for collection in tuple(obj.users_collection)
    ]
    for obj, collection in links:
        collection.objects.unlink(obj)
    bpy.context.view_layer.update()
    return links


def _restore_excluded_objects(links: Sequence[tuple[Any, Any]]) -> None:
    for obj, collection in links:
        if obj.name not in collection.objects:
            collection.objects.link(obj)
    bpy.context.view_layer.update()


def evaluated_action_signature(
    action: Any, start: int | None = None, end: int | None = None, digits: int = 6,
) -> str:
    """Hash integer-frame channel samples at representation-fidelity precision."""
    frame_start, frame_end = (int(round(value)) for value in action.frame_range)
    start = frame_start if start is None else start
    end = frame_end if end is None else end
    rows = []
    for curve in sorted(_action_fcurves(action), key=lambda item: (item.data_path, item.array_index)):
        rows.append((
            curve.data_path,
            curve.array_index,
            tuple(round(float(curve.evaluate(frame)), digits) for frame in range(start, end + 1)),
        ))
    return _json_hash(rows)


def source_content_signature(action: Any) -> str:
    """Hash source content without treating its mutable datablock name as semantics."""
    return _json_hash(_action_signature(action)[1:])


def source_window_content_signature(action: Any, start: int, end: int) -> str:
    """Hash only the local sampled window, independent of identity and frame origin."""
    rows = []
    for curve in sorted(_action_fcurves(action), key=lambda item: (item.data_path, item.array_index)):
        rows.append((
            curve.data_path,
            curve.array_index,
            tuple(float(curve.evaluate(frame)) for frame in range(start, end + 1)),
        ))
    return _json_hash(rows)


def _repair_family(roles: Sequence[str]) -> str:
    unique = set(roles)
    if unique and all(role.startswith("xr_") for role in unique):
        return "strafe"
    if unique and unique <= {"sr", "sl"}:
        return "precision_lateral"
    if unique and all(role.startswith(("ar_", "al_")) for role in unique):
        return "arc"
    if unique and unique <= {"pr45", "pl45"}:
        return "pivot"
    raise SourceRepairError(f"unsupported semantic source-repair roles: {sorted(unique)}")


def normaliser_algorithms_for_roles(roles: Sequence[str]) -> tuple[str, ...]:
    """Return the exact role-semantic normalisers which may be dispatched.

    This seam is deliberately independent of Action identity and source range.
    Lateral width is diagnostic for motion families; active normalisers preserve
    source anatomy and only repair the declared trajectory or loop boundary.
    """
    family = _repair_family(roles)
    algorithms = [BOUNDARY_ALGORITHM_VERSION]
    if family == "strafe":
        algorithms.append(TRAJECTORY_NORMALISER_VERSION)
    elif family == "pivot":
        algorithms.append("bounded_phase_preserving_monotonic_projection")
    return tuple(algorithms)


def rank_semantic_candidate_evidence(
    rows: Sequence[Mapping[str, Any]],
) -> list[Mapping[str, Any]]:
    """Rank admitted content without consulting locator/provenance fields."""
    return sorted(rows, key=lambda row: (
        float(row["repair_cost"]), str(row["source_window_content_signature"]),
    ))


def action_signatures(excluded: set[str] | None = None) -> dict[str, str]:
    excluded = excluded or set()
    return {
        action.name: action_signature(action)
        for action in sorted(bpy.data.actions, key=lambda item: item.name)
        if action.name not in excluded
    }


def _capture_pose_state(rig: Any) -> dict[str, tuple[Any, ...]]:
    """Capture serialised pose-channel defaults which sampling must preserve."""
    return {
        bone.name: (
            bone.rotation_mode,
            bone.location.copy(),
            bone.rotation_euler.copy(),
            bone.rotation_quaternion.copy(),
            tuple(bone.rotation_axis_angle),
            bone.scale.copy(),
        )
        for bone in rig.pose.bones
    }


def _restore_pose_state(rig: Any, state: Mapping[str, tuple[Any, ...]]) -> None:
    """Restore unkeyed pose defaults after evaluated sampling and construction."""
    for name, values in state.items():
        bone = rig.pose.bones[name]
        mode, location, euler, quaternion, axis_angle, scale = values
        bone.rotation_mode = mode
        bone.location = location
        bone.rotation_euler = euler
        bone.rotation_quaternion = quaternion
        bone.rotation_axis_angle = axis_angle
        bone.scale = scale


def source_repair_roles(action: Any) -> frozenset[str] | None:
    """Return a derived source Action's role allow-list, if it has one."""
    raw = action.get(ROLE_RESTRICTION_PROPERTY)
    if raw is None:
        return None
    try:
        roles = json.loads(raw)
    except (TypeError, json.JSONDecodeError) as error:
        raise SourceRepairError(f"{action.name} has invalid source-role metadata") from error
    if not isinstance(roles, list) or not roles or any(not isinstance(role, str) for role in roles):
        raise SourceRepairError(f"{action.name} has invalid source-role metadata")
    return frozenset(roles)


def action_is_candidate_for_role(action: Any, role_code: str) -> bool:
    roles = source_repair_roles(action)
    if roles is None:
        return True
    try:
        provenance = json.loads(action.get(PROVENANCE_PROPERTY, ""))
    except (TypeError, json.JSONDecodeError):
        return False
    if provenance.get("algorithm") not in {ALGORITHM_VERSION, *LEGACY_ALGORITHM_VERSIONS} or role_code not in roles:
        return False
    try:
        source_repair_window(action, role_code)
    except SourceRepairError:
        return False
    return True


def source_repair_window(action: Any, role_code: str) -> tuple[int, int] | None:
    """Return a provenance-evidenced repaired interval for its confined role."""
    roles = source_repair_roles(action)
    if roles is None or role_code not in roles:
        return None
    try:
        provenance = json.loads(action.get(PROVENANCE_PROPERTY, ""))
        interval = provenance["derived_range"]
    except (TypeError, KeyError, json.JSONDecodeError) as error:
        raise SourceRepairError(f"{action.name} has invalid source-repair window provenance") from error
    if (
        not isinstance(interval, list) or len(interval) != 2
        or any(not isinstance(value, int) or isinstance(value, bool) for value in interval)
        or interval[1] <= interval[0]
    ):
        raise SourceRepairError(f"{action.name} has invalid source-repair window provenance")
    opening = provenance.get("opening_repair_window")
    closing = provenance.get("closing_repair_window")
    transform_support = provenance.get("transform_support")
    sample_semantics = provenance.get("sample_semantics")
    if (
        provenance.get("selected_blend_profile") not in REPAIR_BLEND_PROFILES
        or not isinstance(opening, list) or len(opening) != 2
        or not isinstance(closing, list) or len(closing) != 2
        or provenance.get("opening_repair_width") != opening[1] - opening[0] + 1
        or provenance.get("closing_repair_width") != closing[1] - closing[0] + 1
        or not isinstance(provenance.get("repair_option_selection"), dict)
        or not isinstance(transform_support, dict)
        or transform_support.get("evaluated_space") != "cycle-normalised generated Root-relative hierarchy"
        or transform_support.get("repaired_domain") != "complete non-Root motion hierarchy"
        or not isinstance(sample_semantics, dict)
        or sample_semantics.get("conceptual_sample_count") != interval[1] - interval[0] + 1
        or sample_semantics.get("unique_runtime_sample_count") != interval[1] - interval[0]
        or sample_semantics.get("duplicate_endpoint") is not True
    ):
        raise SourceRepairError(f"{action.name} has stale or incomplete selected repair provenance")
    if role_code in {"sr", "sl"}:
        if (
            provenance.get("precision_derivation") != "temporal_reversal"
            or provenance.get("precision_native_source_role") != "sr"
            or provenance.get("precision_anatomy_mapping") != "same_anatomy_no_bone_swap"
        ):
            raise SourceRepairError(
                f"{action.name} is a stale precision repair derivative without temporal-reversal provenance"
            )
        foot_order = provenance.get("precision_foot_order")
        silhouette = provenance.get("precision_leg_silhouette")
        if (
            not isinstance(foot_order, dict)
            or foot_order.get("algorithm") != PRECISION_FOOT_ORDER_ALGORITHM
            or not isinstance(foot_order.get("pre_repair"), dict)
            or not isinstance(foot_order.get("post_repair"), dict)
        ):
            raise SourceRepairError(
                f"{action.name} is a stale precision repair derivative without current foot-order evidence"
            )
        if (
            not isinstance(silhouette, dict)
            or silhouette.get("algorithm") != PRECISION_LEG_SILHOUETTE_ALGORITHM
            or not isinstance(silhouette.get("pre_repair"), dict)
            or not isinstance(silhouette.get("post_repair"), dict)
            or silhouette["pre_repair"].get("armature_identity") != "Kimodo_Source:Kimodo_Source"
        ):
            raise SourceRepairError(
                f"{action.name} is a stale precision repair derivative without authoritative leg-silhouette evidence"
            )
        confinement = provenance.get("precision_confinement")
        if (
            not isinstance(confinement, dict)
            or confinement.get("policy") != "precision_lateral_complete_segment_confinement_v1"
            or confinement.get("accepted") is not True
        ):
            raise SourceRepairError(
                f"{action.name} is a stale precision repair derivative without accepted current confinement evidence"
            )
    if role_code.startswith(("ar_", "al_")):
        body_support = provenance.get("arc_body_support")
        if (
            not isinstance(body_support, dict)
            or body_support.get("coordinate_frame")
            != "cycle-normalised generated Root heading; bilateral foot midpoint support origin"
            or any(name not in body_support for name in (
                "mean_rearward_body_offset",
                "p90_rearward_body_offset",
                "mean_rearward_pelvis_offset",
                "mean_rearward_chest_offset",
            ))
        ):
            raise SourceRepairError(
                f"{action.name} is a stale arc repair derivative without generated-Root body-support evidence"
            )
        cadence = provenance.get("evaluated_hierarchy_cadence")
        if (
            not isinstance(cadence, dict)
            or cadence.get("contract") != HIERARCHY_CADENCE_CONTRACT
            or not isinstance(cadence.get("rank_key"), list)
        ):
            raise SourceRepairError(
                f"{action.name} is a stale arc repair derivative without evaluated hierarchy cadence evidence"
            )
    return interval[0], interval[1]


def source_repair_arc_body_support(action: Any, role_code: str) -> dict[str, Any] | None:
    """Return durable generated-Root carriage evidence for a current arc derivative."""
    if not role_code.startswith(("ar_", "al_")) or source_repair_roles(action) is None:
        return None
    source_repair_window(action, role_code)
    return dict(json.loads(action[PROVENANCE_PROPERTY])["arc_body_support"])


def source_repair_precision_foot_order(action: Any, role_code: str) -> dict[str, Any] | None:
    """Return immutable original/repaired precision evidence for a current derivative."""
    if role_code not in {"sr", "sl"} or source_repair_roles(action) is None:
        return None
    source_repair_window(action, role_code)
    provenance = json.loads(action[PROVENANCE_PROPERTY])
    evidence = dict(provenance["precision_foot_order"])
    evidence["materialisation_role"] = role_code
    evidence["derivation"] = "native" if role_code == "sr" else "temporal_reversal"
    evidence["anatomy_mapping"] = "same LeftFoot and RightFoot anatomy; no bone swapping"
    return evidence


def source_repair_precision_leg_silhouette(action: Any, role_code: str) -> dict[str, Any] | None:
    """Return immutable original/repaired authoritative silhouette evidence."""
    if role_code not in {"sr", "sl"} or source_repair_roles(action) is None:
        return None
    source_repair_window(action, role_code)
    provenance = json.loads(action[PROVENANCE_PROPERTY])
    evidence = dict(provenance["precision_leg_silhouette"])
    evidence["materialisation_role"] = role_code
    evidence["derivation"] = "native" if role_code == "sr" else "temporal_reversal"
    return evidence


def source_repair_natural_recurrence(action: Any, role_code: str) -> dict[str, Any] | None:
    """Return pre-repair recurrence evidence for a declared recurrent derivative."""
    roles = source_repair_roles(action)
    if roles is None or role_code not in roles:
        return None
    try:
        evidence = json.loads(action[PROVENANCE_PROPERTY])["natural_recurrence"]
    except (TypeError, KeyError, json.JSONDecodeError) as error:
        raise SourceRepairError(f"{action.name} lacks natural-recurrence provenance") from error
    if not isinstance(evidence, dict) or evidence.get("contract") != "anatomical_scale_native_frame_pose_derivative_recurrence_v1":
        raise SourceRepairError(f"{action.name} has invalid natural-recurrence provenance")
    return evidence


def source_repair_pivot_recurrence_admission(
    action: Any, role_code: str,
) -> dict[str, Any] | None:
    """Admit only a fully evidenced repaired pivot despite raw non-recurrence."""
    roles = source_repair_roles(action)
    if role_code not in {"pr45", "pl45"} or roles is None or role_code not in roles:
        return None
    try:
        provenance = json.loads(action[PROVENANCE_PROPERTY])
        anatomy = provenance["anatomy_contact_preservation"]
        corruption_oracles = provenance["pivot_corruption_oracles"]
        heading = provenance["pivot_heading_projection"]
        root = provenance["pivot_root_translation"]
        composition = provenance["pivot_three_cycle_composition"]
        unit_yaw = heading["unit_yaw"]
        boundary_yaw = composition["boundary_yaw"]
    except (TypeError, KeyError, json.JSONDecodeError) as error:
        raise SourceRepairError(
            f"{action.name} lacks complete pivot recurrence-admission provenance"
        ) from error
    if (
        provenance.get("algorithm") not in {ALGORITHM_VERSION, *LEGACY_ALGORITHM_VERSIONS}
        or provenance.get("role_family") != "pivot"
        or provenance.get("semantic_source_role") != "pr45"
        or provenance.get("content_repair_algorithm_version") not in {ALGORITHM_VERSION, *LEGACY_ALGORITHM_VERSIONS}
    ):
        raise SourceRepairError(f"{action.name} has stale pivot repair provenance")
    try:
        validate_pivot_repair_oracles(corruption_oracles)
    except (KeyError, TypeError, ValueError) as error:
        raise SourceRepairError(
            f"{action.name} has invalid pivot anatomy/contact provenance: {error}"
        ) from error
    if (
        anatomy.get("accepted") is not True
        or anatomy.get("contact_topology_before") != anatomy.get("contact_topology_after")
    ):
        raise SourceRepairError(f"{action.name} lacks accepted pivot anatomy/contact preservation")
    budgets = heading.get("budgets")
    if not isinstance(budgets, dict) or not all(
        math.isfinite(float(heading[key])) and float(heading[key]) <= float(budgets[budget])
        for key, budget in (
            ("maximum_correction", "maximum_correction_magnitude"),
            ("correction_area", "maximum_correction_area"),
            ("corrected_duration", "maximum_corrected_duration"),
        )
    ):
        raise SourceRepairError(f"{action.name} exceeds bounded pivot heading provenance")
    if (
        not isinstance(unit_yaw, list) or len(unit_yaw) < 2
        or not math.isclose(float(unit_yaw[0]), 0.0, abs_tol=1e-9)
        or not math.isclose(float(unit_yaw[-1]), -math.pi / 4.0, abs_tol=1e-9)
        or float(heading.get("directional_span", 0.0))
        < float(budgets.get("minimum_signed_heading_range_rad", math.inf))
    ):
        raise SourceRepairError(f"{action.name} lacks a signed 45-degree pivot unit")
    expected_boundaries = [-(math.pi / 4.0) * index for index in range(4)]
    if (
        root.get("accepted") is not True
        or float(root.get("maximum_absolute_component", math.inf)) != 0.0
        or composition.get("cycles") != 3
        or composition.get("reset_detected") is not False
        or float(composition.get("root_translation_maximum", math.inf)) != 0.0
        or not isinstance(boundary_yaw, list) or len(boundary_yaw) != 4
        or any(
            not math.isclose(float(actual), expected, abs_tol=1e-9)
            for actual, expected in zip(boundary_yaw, expected_boundaries)
        )
    ):
        raise SourceRepairError(f"{action.name} lacks exact three-cycle pivot Root provenance")
    return {
        "accepted": True,
        "contract": "provenance_qualified_repaired_pivot_recurrence_admission_v1",
        "raw_natural_recurrence_is_diagnostic": True,
        "anatomy_contact_preservation": anatomy,
        "pivot_corruption_oracles": corruption_oracles,
        "pivot_heading_projection": heading,
        "pivot_root_translation": root,
        "pivot_three_cycle_composition": composition,
    }


def derived_action_name(source_action: Any, start: int, end: int, roles: Sequence[str]) -> str:
    """Name a repair from semantics and content provenance, never source identity."""
    role_key = "_".join(sorted(set(roles)))
    fingerprint = _json_hash({
        "algorithm": ALGORITHM_VERSION,
        "role_family": _repair_family(roles),
        "roles": sorted(set(roles)),
        "source_window_content": source_window_content_signature(source_action, start, end),
    })[:12]
    return f"kimodo_multi_repair_{role_key}_{fingerprint}"


def _set_curve_sample(curve: Any, frame: int, value: float) -> None:
    point = next(
        (item for item in curve.keyframe_points if math.isclose(float(item.co.x), frame, abs_tol=1e-6)),
        None,
    )
    if point is None:
        point = curve.keyframe_points.insert(frame, value)
    point.co.y = value
    point.interpolation = "LINEAR"
    curve.update()


def _channel_bag(action: Any, rig: Any) -> Any:
    slot = next((item for item in action.slots if item.target_id_type == rig.id_type), None)
    if slot is None:
        raise SourceRepairError(f"{action.name} has no compatible Action slot")
    return next(
        bag
        for layer in action.layers
        for strip in layer.strips
        for bag in strip.channelbags
        if bag.slot == slot
    )


def _sample_window(
    rig: Any, action: Any, frames: Sequence[int], role: Any, rig_basis: Any,
    pivot_projection: PivotHeadingProjection | None = None,
) -> tuple[list[Any], list[dict[str, Any]], Any, Any]:
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    names = _motion_bone_names(rig)
    projected_root, _, alignment, travel_evidence = _straight_source_projection(
        rig, role, action, list(frames), rig_basis,
    )
    mirror = "mirror" in role.derivation
    _, source_yaw = _evaluated_body_relative(
        rig, action, list(frames), mirror, rig_basis, role.segment_index in {2, 5},
    )
    generated_root, generated_yaw = _reconstruct_root(
        role, projected_root, source_yaw, pivot_projection,
    )
    try:
        _activate_action(rig, action)
        roots, armature_rows = [], []
        alignment_origin = None
        opening_root = None
        for index, frame in enumerate(frames):
            bpy.context.scene.frame_set(frame)
            root = rig.pose.bones["Root"].matrix.copy()
            _, root = role_evaluated_hierarchy_transform("Root", root, mirror)
            roots.append(root)
            if opening_root is None:
                opening_root = root.translation.copy()
            if alignment_origin is None:
                _, mirrored_hips = role_evaluated_hierarchy_transform(
                    "Hips", rig.pose.bones["Hips"].matrix, mirror,
                )
                alignment_origin = mirrored_hips.translation.copy()
            generated = (
                rig.data.bones["Root"].matrix_local
                @ Matrix.Translation(Vector((
                    generated_root[0][index], generated_root[1][index], generated_root[2][index],
                )))
                @ Matrix.Rotation(generated_yaw[index], 4, "Y")
            )
            row = {}
            for name in names:
                target_name, source_matrix = role_evaluated_hierarchy_transform(
                    name, rig.pose.bones[name].matrix, mirror,
                )
                row[target_name] = generated.inverted_safe() @ _cycle_normalised_source_transform(
                    source_matrix, alignment, alignment_origin, opening_root, rig_basis,
                )
            armature_rows.append(row)
        assert alignment_origin is not None
        return roots, armature_rows, alignment, (alignment_origin, generated_root, generated_yaw, travel_evidence)
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _smootherstep(value: float) -> float:
    value = max(0.0, min(1.0, value))
    return value * value * value * (value * (value * 6.0 - 15.0) + 10.0)


def _rotation_angle(first: Matrix, second: Matrix) -> float:
    return first.to_quaternion().rotation_difference(second.to_quaternion()).angle


def _descendants_in_rows(rig: Any, bone_name: str, names: set[str]) -> list[str]:
    return [
        name for name in names
        if name != bone_name and any(parent.name == bone_name for parent in rig.pose.bones[name].parent_recursive)
    ]


def _solve_two_bone_leg(
    row: Mapping[str, Matrix], side: str, target_ankle: Vector,
) -> tuple[dict[str, Matrix], dict[str, float]]:
    """Move an ankle through the real thigh/shin hierarchy without stretching."""
    upper_name, lower_name, foot_name = LEG_CHAINS[side]
    upper, lower, foot = row[upper_name], row[lower_name], row[foot_name]
    hip, knee, ankle = upper.translation.copy(), lower.translation.copy(), foot.translation.copy()
    upper_length, lower_length = (knee - hip).length, (ankle - knee).length
    target_vector = target_ankle - hip
    distance = target_vector.length
    if min(upper_length, lower_length, distance) <= 1e-8:
        raise SourceRepairError(f"{side} leg has degenerate evaluated geometry")
    if distance >= upper_length + lower_length - 1e-7 or distance <= abs(upper_length - lower_length) + 1e-7:
        raise SourceRepairError(f"{side} reshape target is outside the evaluated leg reach envelope")

    direction = target_vector.normalized()
    along = (upper_length * upper_length - lower_length * lower_length + distance * distance) / (2.0 * distance)
    height = math.sqrt(max(0.0, upper_length * upper_length - along * along))
    original_direction = (ankle - hip).normalized()
    original_along = (knee - hip).dot(original_direction)
    original_bend = (knee - hip) - original_direction * original_along
    bend = original_bend - direction * original_bend.dot(direction)
    if bend.length <= 1e-8:
        raise SourceRepairError(f"{side} knee bend plane is ambiguous")
    bend.normalize()
    new_knee = hip + direction * along + bend * height

    upper_rotation = (knee - hip).rotation_difference(new_knee - hip).to_matrix().to_4x4()
    lower_rotation = (ankle - knee).rotation_difference(target_ankle - new_knee).to_matrix().to_4x4()
    desired_upper = Matrix.Translation(hip) @ upper_rotation @ Matrix.Translation(-hip) @ upper
    desired_lower = Matrix.Translation(new_knee) @ lower_rotation @ Matrix.Translation(-knee) @ lower
    desired_foot = foot.copy()
    desired_foot.translation = target_ankle

    new_bend = (new_knee - hip) - direction * (new_knee - hip).dot(direction)
    orientation_dot = original_bend.normalized().dot(new_bend.normalized())
    upper_error = abs((new_knee - hip).length - upper_length)
    lower_error = abs((target_ankle - new_knee).length - lower_length)
    return {
        upper_name: desired_upper,
        lower_name: desired_lower,
        foot_name: desired_foot,
    }, {
        "limb_length_error": max(upper_error, lower_error),
        "knee_orientation_dot": orientation_dot,
    }


def _sample_armature_rows(
    rig: Any, action: Any, frames: Sequence[int], names: Sequence[str],
) -> tuple[list[Matrix], list[dict[str, Matrix]]]:
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        roots, rows = [], []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            bpy.context.view_layer.update()
            roots.append(rig.pose.bones["Root"].matrix.copy())
            rows.append({name: rig.pose.bones[name].matrix.copy() for name in names})
        return roots, rows
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _trajectory_normalise_strafe(
    rig: Any,
    action: Any,
    frames: Sequence[int],
    role_code: str,
    character_scale: float,
    rig_basis: Matrix,
) -> dict[str, Any]:
    """Rigidly translate the complete non-Root hierarchy in a candidate-local frame."""
    if action.get("anim002_trajectory_normalisation"):
        raise SourceRepairError("double application of trajectory normalisation is prohibited")
    names = _motion_bone_names(rig)
    roots, original_rows = _sample_armature_rows(rig, action, frames, names)
    hips_origin = _root_local_translation(rig_basis, original_rows[0]["Hips"].translation)
    samples = []
    for row in original_rows:
        hips = _root_local_translation(rig_basis, row["Hips"].translation) - hips_origin
        samples.append((float(hips.x), float(hips.z)))
    try:
        decision = normalise_xr_trajectory(role_code, samples, character_scale)
    except TrajectoryNormalisationError as error:
        raise SourceRepairError(f"{role_code} trajectory normalisation rejected candidate: {error}") from error

    corrections = [
        rig_basis @ Vector((after[0] - before[0], 0.0, after[1] - before[1]))
        for before, after in zip(samples, decision.samples)
    ]
    desired_rows = [
        {name: Matrix.Translation(correction) @ row[name] for name in names}
        for row, correction in zip(original_rows, corrections)
    ]
    channels = _channels_from_armature_rows(rig, roots, desired_rows)
    curves = {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}
    bag = _channel_bag(action, rig)
    hips_path = 'pose.bones["Hips"].location'
    for key, values in channels.items():
        if key[0] != hips_path:
            continue
        curve = curves.get(key)
        if curve is None:
            curve = bag.fcurves.new(data_path=key[0], index=key[1])
            curves[key] = curve
        for index, frame in enumerate(frames):
            _set_curve_sample(curve, frame, values[index])

    _, actual_rows = _sample_armature_rows(rig, action, frames, names)
    maximum_relative_pose_error = 0.0
    maximum_orientation_error = 0.0
    maximum_height_error = 0.0
    maximum_contact_velocity_delta = 0.0
    up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    contact_samples = 0
    contact_topology_before: dict[str, list[int]] = {}
    contact_topology_after: dict[str, list[int]] = {}
    for before, after in zip(original_rows, actual_rows):
        for name in names:
            expected = Matrix.Translation(after["Hips"].translation - before["Hips"].translation) @ before[name]
            maximum_relative_pose_error = max(maximum_relative_pose_error, _matrix_error(expected, after[name]))
            maximum_orientation_error = max(maximum_orientation_error, _rotation_angle(before[name], after[name]))
        for foot_name in ("LeftFoot", "RightFoot"):
            before_height = (before[foot_name].translation - before["Hips"].translation).dot(up)
            after_height = (after[foot_name].translation - after["Hips"].translation).dot(up)
            maximum_height_error = max(maximum_height_error, abs(after_height - before_height))
    for foot_name in ("LeftFoot", "RightFoot"):
        heights = [(row[foot_name].translation - row["Hips"].translation).dot(up) for row in original_rows]
        ground = min(heights)
        contacts = [index for index, height in enumerate(heights) if height <= ground + character_scale * 0.05]
        contact_topology_before[foot_name] = contacts
        actual_heights = [(row[foot_name].translation - row["Hips"].translation).dot(up) for row in actual_rows]
        actual_ground = min(actual_heights)
        contact_topology_after[foot_name] = [
            index for index, height in enumerate(actual_heights)
            if height <= actual_ground + character_scale * 0.05
        ]
        for index in contacts:
            if index == 0:
                continue
            contact_samples += 1
            before_velocity = original_rows[index][foot_name].translation - original_rows[index - 1][foot_name].translation
            after_velocity = actual_rows[index][foot_name].translation - actual_rows[index - 1][foot_name].translation
            maximum_contact_velocity_delta = max(
                maximum_contact_velocity_delta, (after_velocity - before_velocity).length,
            )
    if maximum_relative_pose_error > 2e-5 or maximum_orientation_error > 2e-5:
        raise SourceRepairError("trajectory normalisation changed a non-Root relative pose")
    if maximum_height_error > character_scale * 1e-5:
        raise SourceRepairError("trajectory normalisation changed relative foot height")
    if contact_topology_before != contact_topology_after:
        raise SourceRepairError("trajectory normalisation changed planted-foot height topology")
    if maximum_contact_velocity_delta > character_scale * MAXIMUM_TRAJECTORY_CONTACT_VELOCITY_DELTA_SCALE:
        raise SourceRepairError("trajectory normalisation exceeds the planted-foot velocity-effect bound")

    retention = decision.evidence["orthogonal_retention_factor"]
    if not isinstance(retention, (int, float)):
        raise SourceRepairError("trajectory normaliser returned invalid retention evidence")
    evidence = {
        **decision.evidence,
        "applied": float(retention) < 1.0 - 1e-12,
        "source_interval": [frames[0], frames[-1]],
        "affected_hierarchy": "Hips and every animated non-Root descendant by one per-frame horizontal rigid translation",
        "authored_channels": [hips_path],
        "maximum_non_root_relative_pose_error": maximum_relative_pose_error,
        "maximum_non_root_orientation_error_rad": maximum_orientation_error,
        "maximum_relative_foot_height_error": maximum_height_error,
        "maximum_contact_velocity_delta": maximum_contact_velocity_delta,
        "contact_velocity_delta_limit": character_scale * MAXIMUM_TRAJECTORY_CONTACT_VELOCITY_DELTA_SCALE,
        "planted_contact_samples": contact_samples,
        "contact_height_topology_before": contact_topology_before,
        "contact_height_topology_after": contact_topology_after,
        "limb_lengths_changed": False,
        "knee_orientation_changed": False,
        "hips_or_torso_orientation_changed": False,
        "normaliser_algorithm_version": TRAJECTORY_NORMALISER_VERSION,
    }
    action["anim002_trajectory_normalisation"] = json.dumps(evidence, sort_keys=True)
    return evidence


def _validate_reshape_evidence(evidence: Mapping[str, Any]) -> None:
    failures = {}
    checks = {
        "lateral_scale": (float(evidence["lateral_scale"]), RESHAPE_MINIMUM_SCALE, 1.0),
        "after_width": (float(evidence["after_width"]), 0.0, RESHAPE_TARGET_LATERAL_WIDTH + 3e-6),
        "limb_length_error": (float(evidence["maximum_limb_length_error"]), 0.0, MAXIMUM_LIMB_LENGTH_ERROR),
        "knee_orientation": (float(evidence["minimum_knee_orientation_dot"]), MINIMUM_KNEE_ORIENTATION_DOT, 1.0 + 1e-6),
        "contact_height_delta": (float(evidence["maximum_contact_height_delta"]), 0.0, float(evidence["contact_height_delta_limit"])),
        "planted_velocity_increase": (float(evidence["maximum_planted_velocity_increase"]), 0.0, float(evidence["planted_velocity_increase_limit"])),
        "symmetry": (float(evidence["maximum_bilateral_scale_difference"]), 0.0, 1e-12),
        "axis_alignment": (float(evidence["lateral_axis_alignment"]), 1.0 - 1e-8, 1.0 + 1e-8),
    }
    for name, (value, minimum, maximum) in checks.items():
        if not minimum <= value <= maximum:
            failures[name] = {"value": value, "minimum": minimum, "maximum": maximum}
    if evidence.get("double_application_detected"):
        failures["double_application"] = True
    if failures:
        raise SourceRepairError(f"interior reshape violates anatomical/contact guards: {failures}")


def _precision_anatomy_contact_evidence(
    before: Sequence[Mapping[str, Matrix]],
    after: Sequence[Mapping[str, Matrix]],
    character_scale: float,
    rig_basis: Matrix,
    immutable: range,
    repair_evidence: Mapping[str, Any],
    maximum_foot_height_scale: float = 0.05,
    height_delta_evidence_only: bool = False,
) -> dict[str, Any]:
    """Measure preservation of Segment-1 anatomy, lift order, and contact topology."""
    up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    maximum_height_delta = maximum_limb_delta = maximum_planted_velocity_delta = 0.0
    minimum_knee_dot = 1.0
    maximum_torso_rotation = 0.0
    topology_before: dict[str, list[int]] = {}
    topology_after: dict[str, list[int]] = {}
    lift_apex_before: dict[str, int] = {}
    lift_apex_after: dict[str, int] = {}
    lift_apex_preservation: dict[str, dict[str, object]] = {}
    for name in ("Hips", "Chest"):
        maximum_torso_rotation = max(
            maximum_torso_rotation,
            max(_rotation_angle(first[name], second[name]) for first, second in zip(before, after)),
        )
    for side, (upper_name, lower_name, foot_name) in LEG_CHAINS.items():
        before_heights = [(row[foot_name].translation - row["Hips"].translation).dot(up) for row in before]
        after_heights = [(row[foot_name].translation - row["Hips"].translation).dot(up) for row in after]
        maximum_height_delta = max(
            maximum_height_delta,
            max(abs(first - second) for first, second in zip(before_heights, after_heights)),
        )
        before_ground, after_ground = min(before_heights), min(after_heights)
        topology_before[foot_name] = [
            index for index, value in enumerate(before_heights)
            if value <= before_ground + character_scale * 0.05
        ]
        topology_after[foot_name] = [
            index for index, value in enumerate(after_heights)
            if value <= after_ground + character_scale * 0.05
        ]
        apex = assess_foot_lift_apex_preservation(
            before_heights,
            after_heights,
            character_scale,
            character_scale * maximum_foot_height_scale,
            repair_evidence,
        )
        apex_before, apex_after = apex["source_apex_sample"], apex["repaired_apex_sample"]
        if not isinstance(apex_before, int) or not isinstance(apex_after, int):
            raise SourceRepairError("foot-lift apex evidence has invalid evaluated sample indices")
        lift_apex_before[foot_name] = apex_before
        lift_apex_after[foot_name] = apex_after
        lift_apex_preservation[foot_name] = apex
        for first, second in zip(before, after):
            for parent_name, child_name in ((upper_name, lower_name), (lower_name, foot_name)):
                source_length = (first[child_name].translation - first[parent_name].translation).length
                repaired_length = (second[child_name].translation - second[parent_name].translation).length
                maximum_limb_delta = max(maximum_limb_delta, abs(repaired_length - source_length))
            source_axis = first[foot_name].translation - first[upper_name].translation
            repaired_axis = second[foot_name].translation - second[upper_name].translation
            source_bend = first[lower_name].translation - first[upper_name].translation
            source_bend -= source_axis * (source_bend.dot(source_axis) / max(source_axis.length_squared, 1e-12))
            repaired_bend = second[lower_name].translation - second[upper_name].translation
            repaired_bend -= repaired_axis * (repaired_bend.dot(repaired_axis) / max(repaired_axis.length_squared, 1e-12))
            if source_bend.length > 1e-8 and repaired_bend.length > 1e-8:
                minimum_knee_dot = min(minimum_knee_dot, source_bend.normalized().dot(repaired_bend.normalized()))
        for index in topology_before[foot_name]:
            if index == 0:
                continue
            source_velocity = before[index][foot_name].translation - before[index - 1][foot_name].translation
            repaired_velocity = after[index][foot_name].translation - after[index - 1][foot_name].translation
            maximum_planted_velocity_delta = max(
                maximum_planted_velocity_delta, (repaired_velocity - source_velocity).length,
            )
    immutable_change = max(
        (_matrix_error(before[index][name], after[index][name]) for index in immutable for name in before[index]),
        default=0.0,
    )
    topology_difference = {
        foot: len(set(topology_before[foot]) ^ set(topology_after[foot]))
        for foot in topology_before
    }
    failures = {}
    limits = {
        "maximum_relative_foot_height_delta": character_scale * maximum_foot_height_scale,
        "maximum_limb_length_delta": character_scale * 0.05,
        "minimum_knee_orientation_dot": 0.80,
        "maximum_planted_velocity_delta": character_scale * 0.08,
        "maximum_contact_topology_difference_samples": 6,
    }
    checks = {
        "maximum_relative_foot_height_delta": maximum_height_delta,
        "maximum_limb_length_delta": maximum_limb_delta,
        "maximum_planted_velocity_delta": maximum_planted_velocity_delta,
        "maximum_contact_topology_difference_samples": max(topology_difference.values()),
    }
    for name, value in checks.items():
        if name == "maximum_relative_foot_height_delta" and height_delta_evidence_only:
            continue
        if value > limits[name]:
            failures[name] = {"value": value, "threshold": limits[name]}
    if minimum_knee_dot < limits["minimum_knee_orientation_dot"]:
        failures["minimum_knee_orientation_dot"] = {
            "value": minimum_knee_dot, "threshold": limits["minimum_knee_orientation_dot"],
        }
    if immutable_change > 1e-12:
        failures["immutable_interior_leakage"] = immutable_change
    rejected_apices = {
        foot: evidence for foot, evidence in lift_apex_preservation.items()
        if not evidence["accepted"]
    }
    if rejected_apices:
        failures["foot_lift_apex_preservation"] = rejected_apices
    evidence = {
        "maximum_relative_foot_height_delta": maximum_height_delta,
        "maximum_limb_length_delta": maximum_limb_delta,
        "minimum_knee_orientation_dot": minimum_knee_dot,
        "maximum_hips_chest_rotation_correction_rad": maximum_torso_rotation,
        "maximum_planted_velocity_delta": maximum_planted_velocity_delta,
        "contact_topology_before": topology_before,
        "contact_topology_after": topology_after,
        "contact_topology_symmetric_difference_samples": topology_difference,
        "foot_lift_apex_before": lift_apex_before,
        "foot_lift_apex_after": lift_apex_after,
        "foot_lift_apex_preservation": lift_apex_preservation,
        "maximum_immutable_interior_change": immutable_change,
        "relative_foot_height_delta_admission": (
            "diagnostic_only; repaired arc uses topology, planted stability, swing apex, and direct limb evidence"
            if height_delta_evidence_only else "blocking"
        ),
        "limits": limits,
        "failures": failures,
        "accepted": not failures,
    }
    if failures:
        raise SourceRepairError(f"precision-step anatomy/contact preservation rejected: {failures}")
    return evidence


def _adaptive_arc_repair(
    rig: Any, rows: Sequence[Mapping[str, Matrix]], character_scale: float, rig_basis: Matrix,
) -> tuple[list[dict[str, Matrix]], dict[str, Any], dict[str, Any]]:
    """Select the least intrusive arc seam repair that passes direct physical checks."""
    accepted = []
    rejected = []
    for width, profile in adaptive_repair_specs(len(rows)):
        repaired, assessment = _repair_periodic_pose_rows(
            rows, character_scale, window_frames=width, blend_profile=profile,
            direct_safety_contract=True, rig=rig,
        )
        try:
            anatomy_contact = _precision_anatomy_contact_evidence(
                rows, repaired, character_scale, rig_basis,
                range(width, len(rows) - width), assessment,
                maximum_foot_height_scale=0.11,
                height_delta_evidence_only=True,
            )
        except SourceRepairError as error:
            rejected.append({
                "window_frames": width,
                "blend_profile": profile,
                "reason": str(error),
            })
            continue
        accepted.append((repair_naturalness_key(assessment), repaired, assessment, anatomy_contact))
    if not accepted:
        raise SourceRepairError(f"no adaptive arc repair passed direct anatomy/contact evidence: {rejected}")
    _, repaired, assessment, anatomy_contact = min(accepted, key=lambda item: item[0])
    assessment["repair_option_selection"] = {
        "contract": "bounded_adaptive_arc_direct_physical_naturalness_v1",
        "selected_rank_key": list(repair_naturalness_key(assessment)),
        "rejected_options": rejected,
    }
    return repaired, assessment, anatomy_contact


def arc_body_support_for_action(
    rig: Any, action: Any, start: int, end: int, role: Any, rig_basis: Matrix,
) -> dict[str, Any]:
    """Measure repaired arc carriage in the same generated-Root frame used for output."""
    scale = _anatomical_character_scale(rig)
    frames = list(range(start, end + 1))
    _, original_rows, _, _ = _sample_window(rig, action, frames, role, rig_basis)
    repaired_rows, _, _ = _adaptive_arc_repair(rig, original_rows, scale, rig_basis)
    return arc_body_support_evidence(
        [
            {
                "hips": (float(row["Hips"].translation.x), float(row["Hips"].translation.z)),
                "chest": (float(row["Chest"].translation.x), float(row["Chest"].translation.z)),
                "left_foot": (float(row["LeftFoot"].translation.x), float(row["LeftFoot"].translation.z)),
                "right_foot": (float(row["RightFoot"].translation.x), float(row["RightFoot"].translation.z)),
            }
            for row in repaired_rows
        ],
        [(0.0, 1.0)] * len(repaired_rows),
        scale,
    )


def _precision_foot_order_from_rows(
    rig: Any,
    rows: Sequence[Mapping[str, Matrix]],
    frames: Sequence[int],
    character_scale: float,
    rig_basis: Matrix,
    stage: str,
    role_code: str = "sr",
) -> dict[str, Any]:
    """Assess source or repaired hierarchy rows in the shared positive-left basis."""
    try:
        basis = anatomical_lateral_basis(rig, rig_basis)
        return assess_precision_foot_order(
            role_code,
            [
                (
                    int(frame),
                    float(row["LeftFoot"].translation.dot(basis.axis)),
                    float(row["RightFoot"].translation.dot(basis.axis)),
                )
                for frame, row in zip(frames, rows)
            ],
            character_scale,
            stage=stage,
            basis_evidence=basis.evidence,
        )
    except (LateralBasisError, PrecisionStepError) as error:
        raise SourceRepairError(f"precision anatomical foot-order evidence is invalid: {error}") from error


def _precision_leg_silhouette_from_rows(
    rig: Any,
    rows: Sequence[Mapping[str, Matrix]],
    frames: Sequence[int],
    character_scale: float,
    rig_basis: Matrix,
    stage: str,
    role_code: str = "sr",
) -> dict[str, Any]:
    """Assess evaluated hip-to-foot chains in the authoritative source rig."""
    try:
        basis = anatomical_lateral_basis(rig, rig_basis)
        up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
        return assess_precision_leg_silhouette(
            role_code,
            [
                (
                    int(frame),
                    [(float(row[name].translation.dot(basis.axis)), float(row[name].translation.dot(up))) for name in ("LeftLeg", "LeftShin", "LeftFoot")],
                    [(float(row[name].translation.dot(basis.axis)), float(row[name].translation.dot(up))) for name in ("RightLeg", "RightShin", "RightFoot")],
                )
                for frame, row in zip(frames, rows)
            ],
            character_scale,
            stage=stage,
            armature_identity=f"{rig.name}:Kimodo_Source",
            basis_evidence=basis.evidence,
        )
    except (LateralBasisError, LegSilhouetteError) as error:
        raise SourceRepairError(f"precision leg-silhouette evidence is invalid: {error}") from error


def precision_leg_silhouette_evidence(
    rig: Any, action: Any, start: int, end: int, rig_basis: Matrix, stage: str, role_code: str = "sr",
) -> dict[str, Any]:
    """Evaluate an Action's authoritative source chain without proxy armatures."""
    frames = list(range(start, end + 1))
    _, rows = _sample_armature_rows(rig, action, frames, _motion_bone_names(rig))
    return _precision_leg_silhouette_from_rows(
        rig, rows, frames, _anatomical_character_scale(rig), rig_basis, stage, role_code,
    )


def _pivot_heading_projection(
    rig: Any, action: Any, frames: Sequence[int], role_code: str = "pr45",
) -> tuple[PivotHeadingProjection, dict[str, Any]]:
    """Capture the sole authorised immutable armature/global-Z pivot trace."""
    oracle = _capture_pivot_transform_oracle(rig, action, list(frames))
    headings = [float(sample["global_z_heading"]) for sample in oracle["samples"]]
    projection = project_pivot_heading(role_code, headings)
    return projection, {
        "source_reference": "immutable pre-repair evaluated Hips armature/global-Z heading relative to the first candidate sample",
        "comparison_frame": "cycle-normalised generated Root local frame; Root rest local +Y maps to armature/global +Z",
        "source_action_content_signature": source_content_signature(action),
        "source_frames": [int(frames[0]), int(frames[-1])],
        "samples": len(frames),
        **projection.evidence(),
    }


def _pivot_contact_evidence(
    before: Sequence[Mapping[str, Matrix]],
    after: Sequence[Mapping[str, Matrix]],
    character_scale: float,
    rig_basis: Matrix,
    immutable: range,
    repair_evidence: Mapping[str, Any],
) -> dict[str, Any]:
    """Measure pivot contact and anatomy in generated Root space."""
    anatomy = _precision_anatomy_contact_evidence(
        before, after, character_scale, rig_basis, immutable, repair_evidence,
        maximum_foot_height_scale=0.05,
    )
    up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    feet: dict[str, Any] = {}
    pivot_candidates: list[tuple[float, str]] = []
    for foot in ("LeftFoot", "RightFoot"):
        heights = [(row[foot].translation - row["Hips"].translation).dot(up) for row in before]
        ground = min(heights)
        contacts = [index for index, height in enumerate(heights) if height <= ground + character_scale * 0.05]
        before_positions = [row[foot].translation for row in before]
        after_positions = [row[foot].translation for row in after]
        contact_drift = max(
            ((after_positions[index] - before_positions[index]).length for index in contacts),
            default=0.0,
        )
        contact_velocity = sum(
            (before_positions[index] - before_positions[index - 1]).length
            for index in contacts if index > 0
        )
        pivot_candidates.append((contact_velocity, foot))
        feet[foot] = {
            "contact_samples": contacts,
            "source_contact_path_length": contact_velocity,
            "maximum_repair_contact_drift": contact_drift,
            "minimum_relative_height": ground,
        }
    pivot_candidates.sort(key=lambda item: (item[0], item[1]))
    pivot_foot = pivot_candidates[0][1]
    centre_before = before[0][pivot_foot].translation
    centre_after = after[0][pivot_foot].translation
    evidence = {
        **anatomy,
        "coordinate_frame": "cycle-normalised generated Root",
        "pivot_foot": pivot_foot,
        "pivot_centre_before": list(centre_before),
        "pivot_centre_after": list(centre_after),
        "pivot_centre_displacement": (centre_after - centre_before).length,
        "feet": feet,
        "declared_leading_foot": "Right",
        "mirror_derivation": "pl45 reflects the accepted pr45 hierarchy and swaps Left/Right anatomy",
    }
    return evidence


def _zero_root_translation_evidence(action: Any, frames: Sequence[int]) -> dict[str, Any]:
    curves = {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}
    samples = []
    for frame in frames:
        samples.append([
            float(curves[(f"{ROOT_PATH}.location", axis)].evaluate(frame))
            if (f"{ROOT_PATH}.location", axis) in curves else 0.0
            for axis in range(3)
        ])
    maximum = max((abs(value) for sample in samples for value in sample), default=0.0)
    if maximum != 0.0:
        raise SourceRepairError(
            f"pivot source Root translation is not exactly zero (maximum {maximum:.9g})"
        )
    return {
        "policy": "exact zero source Root translation; translation is never repaired, promoted, or fitted",
        "maximum_absolute_component": maximum,
        "sample_count": len(samples),
        "accepted": True,
    }


def _reshape_bilateral_interior(
    rig: Any,
    action: Any,
    source_frames: Sequence[int],
    character_scale: float,
    rig_basis: Matrix,
    algorithm: str,
    semantic_contract: str,
) -> dict[str, Any]:
    """Apply one explicitly contracted bilateral anatomical fit."""
    if action.get("anim002_interior_reshape"):
        raise SourceRepairError("double application of interior reshape is prohibited")
    start, end = source_frames[0], source_frames[-1]
    envelope_frames = list(range(start - RESHAPE_BLEND_FRAMES, end + RESHAPE_BLEND_FRAMES + 1))
    names = _motion_bone_names(rig)
    roots, original_rows = _sample_armature_rows(rig, action, envelope_frames, names)
    try:
        lateral = anatomical_lateral_basis(rig, rig_basis).axis
    except LateralBasisError as error:
        raise SourceRepairError(str(error)) from error
    semantic_up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    semantic_forward = (rig_basis.to_3x3() @ Vector((0.0, 0.0, 1.0))).normalized()
    declared_lateral = semantic_up.cross(semantic_forward).normalized()
    source_indices = [envelope_frames.index(frame) for frame in source_frames]
    widths = [
        abs((original_rows[index]["LeftFoot"].translation - original_rows[index]["RightFoot"].translation).dot(lateral))
        / character_scale
        for index in source_indices
    ]
    before_width = max(widths)
    if before_width <= RESHAPE_TARGET_LATERAL_WIDTH:
        raise SourceRepairError("interior reshape requested for a candidate already within the geometry budget")
    lateral_scale = RESHAPE_TARGET_LATERAL_WIDTH / before_width
    if lateral_scale < RESHAPE_MINIMUM_SCALE:
        raise SourceRepairError(
            f"required lateral scale {lateral_scale:.6f} exceeds bounded compression limit {RESHAPE_MINIMUM_SCALE:.6f}"
        )

    desired_rows = [{name: matrix.copy() for name, matrix in row.items()} for row in original_rows]
    per_bone_position = {name: 0.0 for chain in LEG_CHAINS.values() for name in chain}
    per_bone_angle = dict(per_bone_position)
    maximum_limb_error = 0.0
    minimum_knee_dot = 1.0
    affected = set(per_bone_position)
    for side, (_, _, foot_name) in LEG_CHAINS.items():
        affected.update(_descendants_in_rows(rig, foot_name, set(names)))
    weights = []
    for index, frame in enumerate(envelope_frames):
        if frame < start:
            weight = _smootherstep((frame - (start - RESHAPE_BLEND_FRAMES)) / RESHAPE_BLEND_FRAMES)
        elif frame > end:
            weight = _smootherstep(((end + RESHAPE_BLEND_FRAMES) - frame) / RESHAPE_BLEND_FRAMES)
        else:
            weight = 1.0
        weights.append(weight)
        effective_scale = 1.0 - weight * (1.0 - lateral_scale)
        row, desired = original_rows[index], desired_rows[index]
        centre = row["Hips"].translation
        for side, (_, _, foot_name) in LEG_CHAINS.items():
            ankle = row[foot_name].translation
            lateral_offset = (ankle - centre).dot(lateral)
            target = ankle + lateral * (lateral_offset * (effective_scale - 1.0))
            solved, checks = _solve_two_bone_leg(row, side, target)
            desired.update(solved)
            delta = target - ankle
            for descendant in _descendants_in_rows(rig, foot_name, set(names)):
                desired[descendant] = Matrix.Translation(delta) @ row[descendant]
            maximum_limb_error = max(maximum_limb_error, checks["limb_length_error"])
            minimum_knee_dot = min(minimum_knee_dot, checks["knee_orientation_dot"])
        for name in affected:
            per_bone_position[name] = max(
                per_bone_position.get(name, 0.0),
                (desired[name].translation - row[name].translation).length,
            )
            per_bone_angle[name] = max(
                per_bone_angle.get(name, 0.0), _rotation_angle(row[name], desired[name]),
            )

    channels = _channels_from_armature_rows(rig, roots, desired_rows)
    curves = {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}
    bag = _channel_bag(action, rig)
    for key, values in channels.items():
        if not any(key[0].startswith(f'pose.bones["{name}"]') for name in affected):
            continue
        curve = curves.get(key)
        if curve is None:
            curve = bag.fcurves.new(data_path=key[0], index=key[1])
            curves[key] = curve
        for index, frame in enumerate(envelope_frames):
            _set_curve_sample(curve, frame, values[index])

    _, actual_rows = _sample_armature_rows(rig, action, envelope_frames, names)
    actual_widths = [
        abs((actual_rows[index]["LeftFoot"].translation - actual_rows[index]["RightFoot"].translation).dot(lateral))
        / character_scale
        for index in source_indices
    ]
    up = semantic_up
    maximum_height_delta = 0.0
    maximum_velocity_delta = 0.0
    maximum_velocity_increase = 0.0
    contact_samples = 0
    for side, (_, _, foot_name) in LEG_CHAINS.items():
        source_original = [original_rows[index][foot_name].translation - original_rows[index]["Hips"].translation for index in source_indices]
        source_actual = [actual_rows[index][foot_name].translation - actual_rows[index]["Hips"].translation for index in source_indices]
        heights = [point.dot(up) for point in source_original]
        ground = min(heights)
        for index, (before, after) in enumerate(zip(source_original, source_actual)):
            maximum_height_delta = max(maximum_height_delta, abs((after - before).dot(up)))
            if index == 0 or heights[index] > ground + character_scale * 0.05:
                continue
            contact_samples += 1
            before_speed = (before - source_original[index - 1]).length
            after_speed = (after - source_actual[index - 1]).length
            maximum_velocity_delta = max(maximum_velocity_delta, abs(after_speed - before_speed))
            maximum_velocity_increase = max(maximum_velocity_increase, after_speed - before_speed)

    evidence = {
        "algorithm": algorithm,
        "semantic_contract": semantic_contract,
        "anatomical_centre": "evaluated Hips origin / Root-rest sagittal plane",
        "coordinate_definition": "absolute LeftFoot-RightFoot separation projected on Root-rest lateral, normalised by anatomical character scale",
        "affected_bones": sorted(affected),
        "source_interval": [start, end],
        "repair_envelope": [envelope_frames[0], envelope_frames[-1]],
        "blend_frames_each_side": RESHAPE_BLEND_FRAMES,
        "blend_function": "quintic smootherstep with zero boundary velocity and acceleration",
        "lateral_axis": list(lateral),
        "lateral_axis_alignment": lateral.dot(declared_lateral),
        "lateral_offset": 0.0,
        "lateral_scale": lateral_scale,
        "target_width": RESHAPE_TARGET_LATERAL_WIDTH,
        "before_width": before_width,
        "after_width": max(actual_widths),
        "maximum_bilateral_scale_difference": 0.0,
        "maximum_per_bone_positional_correction": per_bone_position,
        "maximum_per_bone_angular_correction_rad": per_bone_angle,
        "maximum_positional_correction": max(per_bone_position.values()),
        "maximum_angular_correction_rad": max(per_bone_angle.values()),
        "maximum_limb_length_error": maximum_limb_error,
        "minimum_knee_orientation_dot": minimum_knee_dot,
        "maximum_contact_height_delta": maximum_height_delta,
        "contact_height_delta_limit": character_scale * MAXIMUM_CONTACT_HEIGHT_DELTA_SCALE,
        "planted_contact_samples": contact_samples,
        "maximum_planted_velocity_delta": maximum_velocity_delta,
        "maximum_planted_velocity_increase": maximum_velocity_increase,
        "planted_velocity_increase_limit": character_scale * MAXIMUM_PLANTED_VELOCITY_INCREASE_SCALE,
        "double_application_detected": False,
        "source_frame_count": len(source_frames),
        "source_timing_changed": False,
        "hips_or_torso_changed": False,
        "foot_height_changed": maximum_height_delta > character_scale * MAXIMUM_CONTACT_HEIGHT_DELTA_SCALE,
        "weight_samples": weights,
    }
    _validate_reshape_evidence(evidence)
    action["anim002_interior_reshape"] = json.dumps(evidence, sort_keys=True)
    return evidence


def _reshape_strafe_interior(
    rig: Any, action: Any, source_frames: Sequence[int], character_scale: float, rig_basis: Matrix,
) -> dict[str, Any]:
    return _reshape_bilateral_interior(
        rig, action, source_frames, character_scale, rig_basis,
        RESHAPE_ALGORITHM_VERSION,
        "xr_* bilateral width normalisation after candidate-local orthogonal trajectory contraction",
    )


def _fit_arc_interior(
    rig: Any, action: Any, source_frames: Sequence[int], character_scale: float, rig_basis: Matrix,
) -> dict[str, Any]:
    return _reshape_bilateral_interior(
        rig, action, source_frames, character_scale, rig_basis,
        ARC_ANATOMICAL_FIT_ALGORITHM_VERSION,
        "arc-only bounded anatomical admission fit preserving curved Hips translation and source heading",
    )


def derive_repaired_action(
    rig: Any,
    source_action: Any,
    start: int,
    end: int,
    derived_name: str,
    roles: Sequence[str],
    selection_evidence: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    """Create one admitted derived Action, or return the existing identical result."""
    if end <= start:
        raise SourceRepairError("source repair requires an ascending source range")
    family = _repair_family(roles)
    normaliser_algorithms = normaliser_algorithms_for_roles(roles)
    semantic_source_role = (
        "sr" if family == "precision_lateral"
        else "pr45" if family == "pivot"
        else next((role for role in roles if role.startswith("ar_")), roles[0])
    )
    window_signature = source_window_content_signature(source_action, start, end)
    existing = bpy.data.actions.get(derived_name)
    expected_identity = {
        "algorithm": ALGORITHM_VERSION,
        "derived_action": derived_name,
        "derived_range": [start, end],
        "roles": sorted(set(roles)),
        "role_family": family,
        "semantic_source_role": semantic_source_role,
        "source_window_content_signature": window_signature,
    }
    if existing is not None:
        try:
            provenance = json.loads(existing.get(PROVENANCE_PROPERTY, ""))
        except json.JSONDecodeError as error:
            raise SourceRepairError(f"existing {derived_name} has invalid provenance") from error
        if all(provenance.get(key) == value for key, value in expected_identity.items()):
            return {**provenance, "idempotent": True}
        raise SourceRepairError(f"existing {derived_name} does not match the requested deterministic repair")

    frames = list(range(start, end + 1))
    role = next((item for item in ROLE_REGISTRY if item.code == semantic_source_role), None)
    if role is None:
        raise SourceRepairError("source repair requires a declared semantic source role")
    _, _, rig_basis = _rig()
    derived = source_action.copy()
    derived.name = derived_name
    derived.use_fake_user = True
    scale = _anatomical_character_scale(rig)
    if family == "strafe":
        trajectory_normalisation = _trajectory_normalise_strafe(
            rig, derived, frames, semantic_source_role, scale, rig_basis,
        )
    else:
        trajectory_normalisation = {
            "applied": False,
            "reason": (
                "arc retains candidate-local source-paced curved translation and heading without trajectory fitting"
                if family == "arc" else
                "pivot Root translation is exactly zero and yaw uses only the immutable bounded heading projection"
                if family == "pivot" else
                "precision Root confinement is evaluated by the materialiser from the complete source step"
            ),
            "source_interval": [start, end],
        }
    if family == "pivot":
        root_translation_evidence = _zero_root_translation_evidence(derived, frames)
        pivot_projection, pivot_heading_evidence = _pivot_heading_projection(
            rig, derived, frames, "pr45",
        )
    else:
        root_translation_evidence = {"applicability": "not_pivot"}
        pivot_projection, pivot_heading_evidence = None, {"applicability": "not_pivot"}
    roots, original_rows, alignment, projection_context = _sample_window(
        rig, derived, frames, role, rig_basis, pivot_projection,
    )
    natural_recurrence = _natural_recurrence_evidence(original_rows, scale)
    precision_foot_order = None
    precision_leg_silhouette = None
    pre_repair_foot_order = None
    pre_repair_leg_silhouette = None
    if family == "precision_lateral":
        pre_repair_leg_silhouette = _precision_leg_silhouette_from_rows(
            rig, original_rows, frames, scale, rig_basis, "immutable_pre_repair",
        )
        pre_repair_leg_silhouette["admission"] = "diagnostic_only"
        pre_repair_foot_order = _precision_foot_order_from_rows(
            rig, original_rows, frames, scale, rig_basis, "immutable_pre_repair",
        )
        pre_repair_foot_order["admission"] = "diagnostic_only"
    if family == "arc":
        repaired_rows, assessment, adaptive_arc_anatomy = _adaptive_arc_repair(
            rig, original_rows, scale, rig_basis,
        )
    else:
        repaired_rows, assessment = _repair_periodic_pose_rows(
            original_rows, scale,
        )
        adaptive_arc_anatomy = None
    if family == "precision_lateral":
        assert pre_repair_foot_order is not None
        assert pre_repair_leg_silhouette is not None
        post_repair_leg_silhouette = _precision_leg_silhouette_from_rows(
            rig, repaired_rows, frames, scale, rig_basis, "repaired_output",
        )
        post_repair_leg_silhouette["admission"] = "diagnostic_only"
        post_repair_foot_order = _precision_foot_order_from_rows(
            rig, repaired_rows, frames, scale, rig_basis, "repaired_output",
        )
        post_repair_foot_order["admission"] = "diagnostic_only"
        precision_foot_order = {
            "algorithm": PRECISION_FOOT_ORDER_ALGORITHM,
            "pre_repair": pre_repair_foot_order,
            "post_repair": post_repair_foot_order,
        }
        precision_leg_silhouette = {
            "algorithm": PRECISION_LEG_SILHOUETTE_ALGORITHM,
            "pre_repair": pre_repair_leg_silhouette,
            "post_repair": post_repair_leg_silhouette,
        }
    translation_limit = MAXIMUM_SOURCE_TRANSLATION_SCALE * scale
    source_failures = {}
    if assessment["maximum_translation_correction"] > translation_limit:
        source_failures["maximum_translation_correction"] = {
            "value": assessment["maximum_translation_correction"], "threshold": translation_limit,
        }
    if assessment["maximum_rotation_correction_rad"] > MAXIMUM_SOURCE_ROTATION_RADIANS:
        source_failures["maximum_rotation_correction_rad"] = {
            "value": assessment["maximum_rotation_correction_rad"],
            "threshold": MAXIMUM_SOURCE_ROTATION_RADIANS,
        }
    if source_failures:
        raise SourceRepairError(f"source repair exceeds content-safety limits: {source_failures}")

    opening = int(assessment["windows"]["beginning_frames"])
    closing = int(assessment["windows"]["ending_frames"])
    interior = range(opening, len(frames) - closing)
    interior_error = max(
        (_matrix_error(original_rows[index][name], repaired_rows[index][name])
         for index in interior for name in original_rows[index]),
        default=0.0,
    )
    if interior_error > 1e-12 or not assessment["both_windows_modified"]:
        raise SourceRepairError("source repair violated its symmetric immutable-interior contract")
    anatomy_contact = (
        _pivot_contact_evidence(
            original_rows, repaired_rows, scale, rig_basis, interior, assessment,
        )
        if family == "pivot"
        else
        adaptive_arc_anatomy
        if family == "arc" else
        _precision_anatomy_contact_evidence(
            original_rows, repaired_rows, scale, rig_basis, interior, assessment,
            maximum_foot_height_scale=0.05,
        )
        if family in {"precision_lateral", "arc"}
        else {"applicability": "not_required_for_family"}
    )
    assert anatomy_contact is not None
    if family == "pivot":
        assert pivot_projection is not None
        limits_value = anatomy_contact["limits"]
        if not isinstance(limits_value, Mapping):
            raise SourceRepairError("pivot anatomy evidence has invalid limits")
        limits: Mapping[str, Any] = limits_value
        topology_before = anatomy_contact["contact_topology_before"]
        topology_after = anatomy_contact["contact_topology_after"]
        pivot_corruption_oracles = {
            "comparison_frame": "cycle-normalised generated Root",
            "both_repair_windows_used": assessment["both_windows_modified"],
            "maximum_immutable_interior_change": interior_error,
            "root_translation_maximum": root_translation_evidence["maximum_absolute_component"],
            "double_application_detected": bool(source_action.get(PROVENANCE_PROPERTY)),
            "contact_topology_preserved": topology_before == topology_after,
            "maximum_contact_velocity_delta": anatomy_contact["maximum_planted_velocity_delta"],
            "contact_velocity_limit": limits["maximum_planted_velocity_delta"],
            "maximum_limb_length_delta": anatomy_contact["maximum_limb_length_delta"],
            "limb_length_limit": limits["maximum_limb_length_delta"],
            "minimum_knee_orientation_dot": anatomy_contact["minimum_knee_orientation_dot"],
            "knee_orientation_limit": limits["minimum_knee_orientation_dot"],
        }
        validate_pivot_repair_oracles(pivot_corruption_oracles)
        three_cycle = pivot_three_cycle_evidence(
            "pr45", pivot_projection.unit_yaw,
            [[0.0, 0.0, 0.0] for _ in frames],
        )
    else:
        pivot_corruption_oracles = {"applicability": "not_pivot"}
        three_cycle = {"applicability": "not_pivot"}

    alignment_origin, generated_root, generated_yaw, travel_evidence = projection_context
    inverse_alignment = alignment.inverted_safe()
    semantic_up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    opening_offset = alignment_origin - roots[0].translation
    horizontal_reanchor = opening_offset - semantic_up * opening_offset.dot(semantic_up)
    desired_armature_rows = []
    for index, row in enumerate(repaired_rows):
        generated = (
            rig.data.bones["Root"].matrix_local
            @ Matrix.Translation(Vector((
                generated_root[0][index], generated_root[1][index], generated_root[2][index],
            )))
            @ Matrix.Rotation(generated_yaw[index], 4, "Y")
        )
        desired_armature_rows.append({
            name: _align_about(
                Matrix.Translation(horizontal_reanchor) @ generated @ relative,
                inverse_alignment,
                alignment_origin,
            )
            for name, relative in row.items()
        })
    channels = _channels_from_armature_rows(rig, roots, desired_armature_rows)
    curves = {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(derived)}
    bag = _channel_bag(derived, rig)
    mutable_indices = list(range(opening)) + list(range(len(frames) - closing, len(frames)))
    for key, values in channels.items():
        curve = curves.get(key)
        if curve is None:
            if not key[0].endswith((".scale", ".location")):
                raise SourceRepairError(f"{source_action.name} lacks required hierarchy channel {key}")
            curve = bag.fcurves.new(data_path=key[0], index=key[1])
            default = 1.0 if key[0].endswith(".scale") else 0.0
            _set_curve_sample(curve, start - 1, default)
            _set_curve_sample(curve, end + 1, default)
            curves[key] = curve
        for index in mutable_indices:
            _set_curve_sample(curve, frames[index], values[index])

    if family == "precision_lateral":
        _, authored_rows = _sample_armature_rows(rig, derived, frames, _motion_bone_names(rig))
        authored_leg_silhouette = _precision_leg_silhouette_from_rows(
            rig, authored_rows, frames, scale, rig_basis, "repaired_output",
        )
        authored_leg_silhouette["admission"] = "diagnostic_only"
        authored_foot_order = _precision_foot_order_from_rows(
            rig, authored_rows, frames, scale, rig_basis, "repaired_output",
        )
        authored_foot_order["admission"] = "diagnostic_only"
        assert precision_foot_order is not None
        precision_foot_order["post_repair"] = authored_foot_order
        assert precision_leg_silhouette is not None
        precision_leg_silhouette["post_repair"] = authored_leg_silhouette

    reshape = {
        "applied": False,
        "reason": (
            "lateral width and bilateral separation are diagnostic; direct role anatomy, contact, and path evidence governs admission"
            if family in {"strafe", "arc"} else
            "pivot source interior is immutable; only symmetric opening/closing hierarchy repair is authorised"
            if family == "pivot" else
            "precision-step source interior is immutable; no bilateral width reshape is applicable"
        ),
        "source_interval": [start, end],
        "affected_bones": [],
    }
    arc_body_support = (
        arc_body_support_evidence(
            [
                {
                    "hips": (float(row["Hips"].translation.x), float(row["Hips"].translation.z)),
                    "chest": (float(row["Chest"].translation.x), float(row["Chest"].translation.z)),
                    "left_foot": (float(row["LeftFoot"].translation.x), float(row["LeftFoot"].translation.z)),
                    "right_foot": (float(row["RightFoot"].translation.x), float(row["RightFoot"].translation.z)),
                }
                for row in repaired_rows
            ],
            [(0.0, 1.0)] * len(repaired_rows),
            scale,
        )
        if family == "arc" else {"applicability": "not_arc"}
    )

    provenance = {
        **expected_identity,
        "original_source_range": [start, end],
        "contract": REPAIR_CONTRACT,
        "boundary_repair_algorithm": BOUNDARY_ALGORITHM_VERSION,
        "selected_blend_profile": assessment["blend_profile"],
        "opening_repair_window": [frames[0], frames[opening - 1]],
        "opening_repair_width": opening,
        "closing_repair_window": [frames[-closing], frames[-1]],
        "closing_repair_width": closing,
        "immutable_interior": [frames[opening], frames[-closing - 1]],
        "repair_option_selection": assessment.get("repair_option_selection", {
            "contract": "fixed_symmetric_two_ended_source_repair_v1",
            "selected_rank_key": [float(assessment["repair_cost"])],
        }),
        "transform_support": {
            "source_range": [start, end],
            "temporal_order": "preserved",
            "anatomical_mapping": "preserved source anatomy",
            "evaluated_space": "cycle-normalised generated Root-relative hierarchy",
            "repaired_domain": "complete non-Root motion hierarchy",
            "coordinate_derivation": travel_evidence,
            "root_handling": "generated Root projection retained while boundary hierarchy is solved through parent/rest transforms",
        },
        "sample_semantics": {
            "conceptual_sample_count": len(frames),
            "unique_runtime_sample_count": len(frames) - 1,
            "duplicate_endpoint": True,
            "conceptual_boundary_frame": frames[-1],
            "runtime_frame_interval": [frames[0], frames[-2]],
        },
        "maximum_immutable_interior_change": interior_error,
        "maximum_translation_correction": assessment["maximum_translation_correction"],
        "maximum_rotation_correction_rad": assessment["maximum_rotation_correction_rad"],
        "source_content_limits": {
            "maximum_translation": translation_limit,
            "maximum_rotation_radians": MAXIMUM_SOURCE_ROTATION_RADIANS,
        },
        "pre_repair_materialisation_metrics": assessment["metrics"],
        "pre_repair_failed_materialisation_budgets": assessment["failures"],
        "natural_recurrence": natural_recurrence,
        "both_repair_windows_used": assessment["both_windows_modified"],
        "phase_contact_evidence": {
            "role": semantic_source_role,
            "lead_foot": "Right" if family in {"precision_lateral", "pivot"} else ("Left" if semantic_source_role.endswith("_l") else "Right"),
            "source_release_frames": [] if family == "precision_lateral" else [start, end],
            "basis": (
                "complete natural-right Segment-1 source step; confined roles have no durable phase event"
                if family == "precision_lateral"
                else "complete semantic pivot unit with accepted heading direction and evaluated pivot-foot contact"
                if family == "pivot"
                else "two accepted same-foot real-source release events"
            ),
            "derivation": (
                "temporal_reversal" if family == "precision_lateral"
                else "pl45 reflects pr45 and swaps left/right anatomy" if family == "pivot"
                else "role-declared"
            ),
        },
        "pivot_heading_projection": pivot_heading_evidence,
        "pivot_root_translation": root_translation_evidence,
        "pivot_corruption_oracles": pivot_corruption_oracles,
        "pivot_three_cycle_composition": three_cycle,
        "anatomy_contact_preservation": anatomy_contact,
        "evaluated_hierarchy_cadence": assessment.get(
            "evaluated_hierarchy_cadence", {"applicability": "not_direct_hierarchy_repair"},
        ),
        "arc_body_support": arc_body_support,
        "precision_foot_order": precision_foot_order or {"applicability": "not_precision_lateral"},
        "precision_leg_silhouette": precision_leg_silhouette or {"applicability": "not_precision_lateral"},
        "precision_derivation": "temporal_reversal" if family == "precision_lateral" else "not_precision_lateral",
        "precision_native_source_role": "sr" if family == "precision_lateral" else None,
        "precision_anatomy_mapping": "same_anatomy_no_bone_swap" if family == "precision_lateral" else None,
        "precision_confinement": (
            travel_evidence.get("confinement", {"applicability": "not_precision_lateral"})
            if family == "precision_lateral" else {"applicability": "not_precision_lateral"}
        ),
        "precision_source_selection": (
            dict(selection_evidence) if family == "precision_lateral" and selection_evidence is not None
            else {"applicability": "not_precision_lateral"}
        ),
        "source_travel_frame": travel_evidence,
        "trajectory_normalisation": trajectory_normalisation,
        "interior_reshape": reshape,
        "normaliser_dispatch": {
            "role_family": family,
            "algorithms": list(normaliser_algorithms),
        },
        "content_repair_algorithm_version": ALGORITHM_VERSION,
        "idempotent": False,
    }
    try:
        validate_durable_repair_windows(
            provenance["opening_repair_window"], provenance["immutable_interior"],
            provenance["closing_repair_window"], sample_count=len(frames),
            opening_width=opening, closing_width=closing, first_frame=frames[0],
        )
    except ValueError as error:
        raise SourceRepairError(f"source repair produced invalid durable window provenance: {error}") from error
    derived[PROVENANCE_PROPERTY] = json.dumps(provenance, sort_keys=True)
    derived[ROLE_RESTRICTION_PROPERTY] = json.dumps(sorted(set(roles)))
    validate_action_for_rig(derived, rig)
    return provenance


def _select_precision_step_candidate(
    rig: Any, actions: Sequence[Any], start: int, end: int, rig_basis: Matrix,
) -> tuple[Any, dict[str, Any]]:
    """Select the lowest-cost eligible complete Segment-1 real candidate."""
    role = next(item for item in ROLE_REGISTRY if item.code == "sr")
    frames = list(range(start, end + 1))
    scale = _anatomical_character_scale(rig)
    rows: list[dict[str, Any]] = []
    quality_metrics: dict[str, dict[str, float]] = {}
    candidates: list[tuple[float, str, Any, dict[str, Any]]] = []
    for action in actions:
        if action.get(PROVENANCE_PROPERTY):
            continue
        window_signature = source_window_content_signature(action, start, end)
        row: dict[str, Any] = {
            "source_action": action.name,
            "original_source_range": [start, end],
            "local_window": [0, end - start],
            "source_window_content_signature": window_signature,
            "phase_compatible": True,
            "phase_basis": "complete Segment-1 natural-right step",
        }
        try:
            _, original_rows, _, projection_context = _sample_window(
                rig, action, frames, role, rig_basis,
            )
            pre_repair_foot_order = _precision_foot_order_from_rows(
                rig, original_rows, frames, scale, rig_basis, "immutable_pre_repair",
            )
            pre_repair_foot_order["admission"] = "diagnostic_only"
            row["precision_foot_order_pre_repair"] = pre_repair_foot_order
            pre_repair_leg_silhouette = _precision_leg_silhouette_from_rows(
                rig, original_rows, frames, scale, rig_basis, "immutable_pre_repair",
            )
            pre_repair_leg_silhouette["admission"] = "diagnostic_only"
            row["precision_leg_silhouette_pre_repair"] = pre_repair_leg_silhouette
            geometry = _foot_geometry(rig, action, start, end, rig_basis)
            event_samples = [
                (
                    frame,
                    geometry["Left"][frame].velocity[0], geometry["Left"][frame].speed,
                    geometry["Right"][frame].velocity[0], geometry["Right"][frame].speed,
                )
                for frame in frames
            ]
            lateral_trajectory = [
                (geometry["Left"][frame].lateral_position + geometry["Right"][frame].lateral_position) * .5
                for frame in frames
            ]
            try:
                precision_events = assess_precision_semantic_events(
                    event_samples, lateral_trajectory, scale,
                )
            except PrecisionStepError as error:
                raise SourceRepairError(f"precision complete-event assessment is invalid: {error}") from error
            row["precision_semantic_events"] = precision_events
            if not precision_events["accepted"]:
                raise SourceRepairError(str(precision_events["rejection_reason"]))
            repaired_rows, assessment = _repair_periodic_pose_rows(original_rows, scale)
            post_repair_foot_order = _precision_foot_order_from_rows(
                rig, repaired_rows, frames, scale, rig_basis, "repaired_output",
            )
            post_repair_foot_order["admission"] = "diagnostic_only"
            row["precision_foot_order_post_repair"] = post_repair_foot_order
            post_repair_leg_silhouette = _precision_leg_silhouette_from_rows(
                rig, repaired_rows, frames, scale, rig_basis, "repaired_output",
            )
            post_repair_leg_silhouette["admission"] = "diagnostic_only"
            row["precision_leg_silhouette_post_repair"] = post_repair_leg_silhouette
            opening, closing = symmetric_window(len(frames))
            anatomy_contact = _precision_anatomy_contact_evidence(
                original_rows, repaired_rows, scale, rig_basis,
                range(opening, len(frames) - closing), assessment,
                maximum_foot_height_scale=0.05,
            )
            translation_limit = MAXIMUM_SOURCE_TRANSLATION_SCALE * scale
            failures = dict(assessment["failures"])
            if assessment["maximum_translation_correction"] > translation_limit:
                failures["source_translation_correction"] = {
                    "value": assessment["maximum_translation_correction"], "threshold": translation_limit,
                }
            if assessment["maximum_rotation_correction_rad"] > MAXIMUM_SOURCE_ROTATION_RADIANS:
                failures["source_rotation_correction"] = {
                    "value": assessment["maximum_rotation_correction_rad"],
                    "threshold": MAXIMUM_SOURCE_ROTATION_RADIANS,
                }
            row["repair"] = {
                **assessment,
                "opening_window_local": [0, assessment["windows"]["beginning_frames"] - 1],
                "closing_window_local": [
                    len(frames) - assessment["windows"]["ending_frames"], len(frames) - 1,
                ],
                "immutable_interior_local": assessment["immutable_interior"],
                "source_admission_failures": failures,
            }
            row["root_confinement"] = projection_context[3]
            quality_metrics[window_signature] = _candidate_quality_metrics(
                rig, action, start, end, rig_basis,
            )
            if failures:
                raise SourceRepairError(f"bounded precision-step repair rejected: {failures}")
            candidates.append((float(assessment["repair_cost"]), window_signature, action, row))
            row["accepted_before_peer_quality"] = True
        except (SourceRepairError, TrajectoryNormalisationError, ValueError, RuntimeError) as error:
            row["accepted_before_peer_quality"] = False
            row["rejection_reason"] = str(error)
        rows.append(row)

    quality = quality_evidence(quality_metrics, "precision_lateral") if quality_metrics else {}
    eligible = []
    for cost, signature, action, row in candidates:
        row["quality"] = quality[signature]
        if quality[signature]["accepted"]:
            eligible.append((cost, signature, action, row))
        else:
            row["rejection_reason"] = "; ".join(quality[signature]["reasons"])
    # The configured name participates only in this one-time measured budget.
    # It never participates in normal admission or ranking, which remain based
    # solely on evaluated candidate evidence.
    baseline = next(
        (item for item in eligible if item[2].name == PRECISION_CALIBRATION_BASELINE_ACTION), None,
    )
    measured_slide: float | None = None
    margin: float | None = None
    calibrated_maximum: float | None = None
    if baseline is not None:
        measured_slide = float(quality[baseline[1]]["metrics"]["planted_foot_slip"])
        margin = PrecisionStepLimits().calibration_sliding_margin_scale
        calibrated_maximum = measured_slide + margin
    calibrated_eligible = []
    for candidate in eligible:
        cost, signature, action, row = candidate
        measured = float(quality[signature]["metrics"]["planted_foot_slip"])
        calibration = {
            "policy": "one_time_precision_sliding_calibration_v1",
            "is_calibration_baseline": candidate is baseline,
            "measured_normalised_planted_foot_slide": measured,
            "calibration_available": baseline is not None,
            "calibration_baseline_measured_slide": measured_slide,
            "fixed_margin": margin,
            "precision_sliding_maximum": calibrated_maximum,
        }
        row["precision_sliding_calibration"] = calibration
        if candidate is not baseline and calibrated_maximum is not None and measured > calibrated_maximum:
            reason = "normal precision candidate planted-foot slide exceeds calibrated precision sliding maximum"
            calibration["accepted"] = False
            calibration["rejection_reason"] = reason
            row["rejection_reason"] = reason
            row["accepted_before_peer_quality"] = False
            continue
        calibration["accepted"] = True
        calibration["rejection_reason"] = None
        calibrated_eligible.append(candidate)
    eligible = calibrated_eligible
    if not eligible:
        raise SourceRepairError(f"no safely repairable precision-step source candidate: {rows}")
    selected_decision = select_precision_candidate([
        {
            "phase_compatible": True,
            "foot_order_accepted": True,
            "quality_accepted": True,
            "repair_accepted": True,
            "repair_cost": cost,
            "source_window_content_signature": signature,
            "width_evidence": {
                "maximum": quality[signature]["metrics"]["maximum_lateral_width"],
                "median": quality[signature]["metrics"]["median_lateral_width"],
            },
            "confinement": row["root_confinement"].get("confinement", {}),
            "quality_metrics": quality[signature]["metrics"],
        }
        for cost, signature, _, row in eligible
    ])
    selected = next(
        item for item in eligible
        if item[1] == selected_decision["source_window_content_signature"]
        and item[0] == selected_decision["repair_cost"]
    )
    eligible.sort(key=lambda item: precision_candidate_rank_key({
        "repair_cost": item[0],
        "source_window_content_signature": item[1],
        "width_evidence": {
            "maximum": item[3]["quality"]["metrics"]["maximum_lateral_width"],
            "median": item[3]["quality"]["metrics"]["median_lateral_width"],
        },
        "confinement": item[3]["root_confinement"].get("confinement", {}),
        "quality_metrics": item[3]["quality"]["metrics"],
    }))
    for rank, (cost, signature, _, row) in enumerate(eligible, start=1):
        row["rank"] = rank
        row["rank_key"] = list(precision_candidate_rank_key({
            "repair_cost": cost,
            "source_window_content_signature": signature,
            "width_evidence": {
                "maximum": row["quality"]["metrics"]["maximum_lateral_width"],
                "median": row["quality"]["metrics"]["median_lateral_width"],
            },
            "confinement": row["root_confinement"].get("confinement", {}),
            "quality_metrics": row["quality"]["metrics"],
        }))
        row["selected"] = row is selected[3]
    return selected[2], {
        "dispatcher": "semantic_role_family_evaluated_candidate_dispatch_v1",
        "role_family": "precision_lateral",
        "semantic_source_role": "sr",
        "source_segment_index": 1,
        "source_window_definition": "complete discovered semantic Segment-1 bounds",
        "selection_precedence": [
            "complete-step phase compatibility",
            "immutable pre-repair signed anatomical foot-order eligibility",
            "immutable and repaired authoritative evaluated leg-silhouette eligibility",
            "complete-window exactly-one evaluated precision-event eligibility",
            "generic evaluated rig-scale-normalised quality with precision_lateral 1.20 width ceiling",
            "configured one-time calibrated planted-foot sliding maximum",
            "repair cost, lower width, confinement, support/slip/derivatives",
            "source-window content signature tie-break",
        ],
        "selected_source_window_content_signature": selected[1],
        "selected_repair_cost": selected[0],
        "candidates": rows,
    }


def _select_strafe_candidate(
    rig: Any, actions: Sequence[Any], semantic_role: str,
    segment: dict[str, int], rig_basis: Matrix,
) -> tuple[Any, tuple[int, int], dict[str, Any]]:
    """Select Segment-4 content from semantic phase and evaluated evidence."""
    role = next(item for item in ROLE_REGISTRY if item.code == semantic_role)
    scale = _anatomical_character_scale(rig)
    rows: list[dict[str, Any]] = []
    admitted: list[tuple[float, str, Any, tuple[int, int], dict[str, Any]]] = []
    quality_metrics: dict[str, dict[str, float]] = {}
    for action in actions:
        row: dict[str, Any] = {
            "source_action": action.name,
            "semantic_role": semantic_role,
        }
        try:
            geometry = _foot_geometry(
                rig, action, int(segment["start_frame"]), int(segment["end_frame"]), rig_basis,
            )
            start, end = role_window(segment, semantic_role, (), geometry)
            frames = list(range(start, end + 1))
            signature = source_window_content_signature(action, start, end)
            # Strafe source admission owns a bounded pre-materialisation
            # trajectory contraction. Assess the same candidate view that
            # derive_repaired_action will publish, rather than applying the
            # unchanged 15-degree materialisation gate to the raw source first.
            # The copy is never published and derived/provenance Actions remain
            # excluded from the candidate pool.
            candidate_view = action.copy()
            try:
                trajectory = _trajectory_normalise_strafe(
                    rig, candidate_view, frames, semantic_role, scale, rig_basis,
                )
                _, original_rows, _, _ = _sample_window(
                    rig, candidate_view, frames, role, rig_basis,
                )
                _, assessment = _repair_periodic_pose_rows(original_rows, scale)
                candidate_quality = _candidate_quality_metrics(
                    rig, candidate_view, start, end, rig_basis,
                )
            finally:
                animation = rig.animation_data_create()
                if animation.action == candidate_view:
                    animation.action = None
                bpy.data.actions.remove(candidate_view)
            # Materialiser seam-budget failures are the reason this dedicated
            # source-content repair exists. Admission here uses the source-
            # authoring correction limits below, then ranks the bounded cost.
            failures: dict[str, Any] = {}
            translation_limit = MAXIMUM_SOURCE_TRANSLATION_SCALE * scale
            if assessment["maximum_translation_correction"] > translation_limit:
                failures["source_translation_correction"] = {
                    "value": assessment["maximum_translation_correction"],
                    "threshold": translation_limit,
                }
            if assessment["maximum_rotation_correction_rad"] > MAXIMUM_SOURCE_ROTATION_RADIANS:
                failures["source_rotation_correction"] = {
                    "value": assessment["maximum_rotation_correction_rad"],
                    "threshold": MAXIMUM_SOURCE_ROTATION_RADIANS,
                }
            row.update({
                "source_range": [start, end],
                "local_window": [0, end - start],
                "source_window_content_signature": signature,
                "phase_compatible": True,
                "phase_basis": "named-foot lateral crossing and same-foot recurrence",
                "repair": assessment,
                "trajectory_normalisation": trajectory,
                "source_admission_failures": failures,
            })
            quality_metrics[signature] = candidate_quality
            if not failures:
                admitted.append((float(assessment["repair_cost"]), signature, action, (start, end), row))
        except (SourceRepairError, ValueError, RuntimeError) as error:
            row["phase_compatible"] = False
            row["rejection_reason"] = str(error)
        rows.append(row)

    quality = quality_evidence(quality_metrics, "strafe") if quality_metrics else {}
    eligible = []
    for candidate in admitted:
        row = candidate[4]
        row["quality"] = quality[candidate[1]]
        reasons = quality[candidate[1]]["reasons"]
        width = float(quality[candidate[1]]["metrics"]["maximum_lateral_width"])
        bounded_width_normalisation = (
            reasons == ["maximum_lateral_width exceeds severe geometry limit"]
            and RESHAPE_TARGET_LATERAL_WIDTH / width >= RESHAPE_MINIMUM_SCALE
        )
        row["quality"]["bounded_bilateral_width_normalisation_eligible"] = bounded_width_normalisation
        if quality[candidate[1]]["accepted"] or bounded_width_normalisation:
            eligible.append(candidate)
        else:
            row["rejection_reason"] = "; ".join(reasons)
    if not eligible:
        raise SourceRepairError(f"no safely repairable semantic strafe source candidate: {rows}")

    # Action name, collection order, and absolute range are intentionally absent.
    ranked_evidence = rank_semantic_candidate_evidence([
        {"repair_cost": item[0], "source_window_content_signature": item[1]}
        for item in eligible
    ])
    selected_key = ranked_evidence[0]
    selected = next(
        item for item in eligible
        if item[0] == selected_key["repair_cost"]
        and item[1] == selected_key["source_window_content_signature"]
    )
    eligible.sort(key=lambda item: (item[0], item[1]))
    for rank, candidate in enumerate(eligible, start=1):
        candidate[4]["rank"] = rank
        candidate[4]["rank_key"] = [candidate[0], candidate[1]]
        candidate[4]["selected"] = candidate[0] == selected[0] and candidate[1] == selected[1]
    return selected[2], selected[3], {
        "dispatcher": "semantic_strafe_phase_quality_bounded_cost_dispatch_v1",
        "role_family": "strafe",
        "semantic_source_role": semantic_role,
        "source_segment_index": 4,
        "source_window_definition": "evaluated named-foot lateral recurrence",
        "selection_precedence": [
            "named-foot phase recurrence eligibility",
            "generic evaluated rig-scale-normalised quality or bounded bilateral width normalisation",
            "bounded symmetric repair cost",
            "source-window content signature tie-break",
        ],
        "excluded_from_dispatch_and_ranking": [
            "Action identity", "Action name", "source-list order", "absolute source range",
        ],
        "selected_source_window_content_signature": selected[1],
        "selected_repair_cost": selected[0],
        "candidates": rows,
    }


def _select_pivot_candidate(
    rig: Any, actions: Sequence[Any], segment: Mapping[str, int], rig_basis: Matrix,
) -> tuple[Any, tuple[int, int], dict[str, Any]]:
    """Select the lowest-cost semantic right-pivot candidate from evaluated evidence."""
    role = next(item for item in ROLE_REGISTRY if item.code == "pr45")
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    frames = list(range(start, end + 1))
    scale = _anatomical_character_scale(rig)
    rows: list[dict[str, Any]] = []
    eligible: list[tuple[tuple[float, float, float, str], Any, dict[str, Any]]] = []
    for action in actions:
        row: dict[str, Any] = {
            "source_action": action.name,
            "source_range": [start, end],
            "local_window": [0, end - start],
        }
        try:
            signature = source_window_content_signature(action, start, end)
            row["source_window_content_signature"] = signature
            root_translation = _zero_root_translation_evidence(action, frames)
            projection, heading = _pivot_heading_projection(rig, action, frames, "pr45")
            _, original_rows, _, projection_context = _sample_window(
                rig, action, frames, role, rig_basis, projection,
            )
            repaired_rows, assessment = _repair_periodic_pose_rows(original_rows, scale)
            opening, closing = symmetric_window(len(frames))
            immutable = range(opening, len(frames) - closing)
            contact = _pivot_contact_evidence(
                original_rows, repaired_rows, scale, rig_basis, immutable, assessment,
            )
            translation_limit = MAXIMUM_SOURCE_TRANSLATION_SCALE * scale
            failures = dict(assessment["failures"])
            if assessment["maximum_translation_correction"] > translation_limit:
                failures["source_translation_correction"] = {
                    "value": assessment["maximum_translation_correction"],
                    "threshold": translation_limit,
                }
            if assessment["maximum_rotation_correction_rad"] > MAXIMUM_SOURCE_ROTATION_RADIANS:
                failures["source_rotation_correction"] = {
                    "value": assessment["maximum_rotation_correction_rad"],
                    "threshold": MAXIMUM_SOURCE_ROTATION_RADIANS,
                }
            row.update({
                "phase_compatible": True,
                "phase_basis": "complete discovered semantic pivot unit; direction from immutable Hips global-Z heading and contact from evaluated feet",
                "heading": heading,
                "root_translation": root_translation,
                "pivot_contact": contact,
                "generated_root_frame": {
                    "translation": projection_context[1],
                    "yaw": projection_context[2],
                    "reference": "Root rest transform with source opening horizontally reanchored; non-Root rows are generated-Root-relative",
                },
                "repair": assessment,
                "source_admission_failures": failures,
            })
            if failures:
                raise SourceRepairError(f"bounded pivot repair rejected: {failures}")
            rank_key = (
                float(assessment["repair_cost"]),
                float(projection.maximum_correction),
                float(contact["pivot_centre_displacement"]),
                signature,
            )
            eligible.append((rank_key, action, row))
        except (SourceRepairError, ValueError, RuntimeError, KeyError) as error:
            row["phase_compatible"] = False
            row["rejection_reason"] = str(error)
        rows.append(row)
    if not eligible:
        raise SourceRepairError(f"no safely repairable semantic pivot source candidate: {rows}")
    eligible.sort(key=lambda item: item[0])
    selected = eligible[0]
    for rank, (rank_key, _, row) in enumerate(eligible, start=1):
        row["rank"] = rank
        row["rank_key"] = list(rank_key)
        row["selected"] = row is selected[2]
    return selected[1], (start, end), {
        "dispatcher": "semantic_pivot_heading_contact_bounded_cost_dispatch_v1",
        "role_family": "pivot",
        "semantic_source_role": "pr45",
        "source_segment_index": int(segment["index"]),
        "source_window_definition": "complete discovered semantic pivot segment; absolute frame numbers are evidence only",
        "selection_precedence": [
            "correct-sign meaningful immutable Hips global-Z heading within isotonic correction budgets",
            "evaluated pivot-foot contact and complete-unit phase compatibility",
            "bounded symmetric generated-Root-space repair cost",
            "heading correction, pivot-centre displacement, then source-window content signature",
        ],
        "selected_source_window_content_signature": selected[2]["source_window_content_signature"],
        "selected_repair_cost": selected[0][0],
        "selected_action_name_is_evidence_only": selected[1].name,
        "candidates": rows,
    }


def _semantic_source_candidates(
    rig: Any, segment: Mapping[str, int], excluded: Sequence[Any] = (),
) -> list[Any]:
    """Discover source-sequence candidates by compatible content and coverage."""
    start, end = int(segment["start_frame"]), int(segment["end_frame"])
    excluded_ids = {item.as_pointer() for item in excluded}
    candidates = []
    for action in bpy.data.actions:
        if action.as_pointer() in excluded_ids or action.get(PROVENANCE_PROPERTY):
            continue
        frame_start, frame_end = (float(value) for value in action.frame_range)
        if frame_start > start or frame_end < end:
            continue
        paths = {curve.data_path for curve in _action_fcurves(action)}
        if not all(any(path.startswith(f'pose.bones["{name}"]') for path in paths) for name in ("Hips", "Chest", "LeftFoot", "RightFoot")):
            continue
        try:
            validate_action_for_rig(action, rig)
        except Exception:
            continue
        candidates.append(action)
    return sorted(candidates, key=source_content_signature)


def _arc_curve_evidence(
    role_code: str, generated_root: Sequence[Sequence[float]], generated_yaw: Sequence[float], scale: float,
) -> dict[str, Any]:
    """Measure candidate-local curved translation/heading consistency without fitting a formula."""
    evidence = arc_curve_evidence(
        role_code,
        [(float(generated_root[0][index]), float(generated_root[2][index])) for index in range(len(generated_yaw))],
        [float(value) for value in generated_yaw],
        scale,
    )
    return {
        **evidence,
        "coordinate_frame": "cycle-normalised generated Root with promoted source curve and armature/global-Z heading",
    }


def _select_arc_candidate(
    rig: Any, actions: Sequence[Any], semantic_role: str, segment: dict[str, int], rig_basis: Matrix,
) -> tuple[Any, tuple[int, int], dict[str, Any]]:
    """Select an evidenced same-foot arc recurrence by bounded repair cost and content tie-break."""
    role = next(item for item in ROLE_REGISTRY if item.code == semantic_role)
    scale = _anatomical_character_scale(rig)
    rows: list[dict[str, Any]] = []
    admitted: list[tuple[tuple[Any, ...], str, Any, tuple[int, int], dict[str, Any]]] = []
    quality_metrics: dict[str, dict[str, float]] = {}
    for action in actions:
        if action.get(PROVENANCE_PROPERTY):
            continue
        row: dict[str, Any] = {"source_action": action.name, "semantic_role": semantic_role}
        try:
            geometry = _foot_geometry(
                rig, action, int(segment["start_frame"]), int(segment["end_frame"]), rig_basis,
            )
            start, end = role_window(segment, semantic_role, (), geometry)
            frames = list(range(start, end + 1))
            signature = source_window_content_signature(action, start, end)
            _, original_rows, _, projection = _sample_window(rig, action, frames, role, rig_basis)
            repaired_rows, assessment, anatomy_contact = _adaptive_arc_repair(
                rig, original_rows, scale, rig_basis,
            )
            body_support = arc_body_support_evidence(
                [
                    {
                        "hips": (float(row["Hips"].translation.x), float(row["Hips"].translation.z)),
                        "chest": (float(row["Chest"].translation.x), float(row["Chest"].translation.z)),
                        "left_foot": (float(row["LeftFoot"].translation.x), float(row["LeftFoot"].translation.z)),
                        "right_foot": (float(row["RightFoot"].translation.x), float(row["RightFoot"].translation.z)),
                    }
                    for row in repaired_rows
                ],
                [(0.0, 1.0)] * len(repaired_rows),
                scale,
            )
            generated_root, generated_yaw = projection[1], projection[2]
            curve = _arc_curve_evidence(semantic_role, generated_root, generated_yaw, scale)
            translation_limit = MAXIMUM_SOURCE_TRANSLATION_SCALE * scale
            source_failures = dict(curve["failures"])
            if assessment["maximum_translation_correction"] > translation_limit:
                source_failures["source_translation_correction"] = {
                    "value": assessment["maximum_translation_correction"], "threshold": translation_limit,
                }
            if assessment["maximum_rotation_correction_rad"] > MAXIMUM_SOURCE_ROTATION_RADIANS:
                source_failures["source_rotation_correction"] = {
                    "value": assessment["maximum_rotation_correction_rad"],
                    "threshold": MAXIMUM_SOURCE_ROTATION_RADIANS,
                }
            row.update({
                "source_range": [start, end],
                "local_window": [0, end - start],
                "source_window_content_signature": signature,
                "phase_compatible": True,
                "phase_basis": "named-foot evaluated arc-tangent crossing and same-foot recurrence",
                "curve_heading": curve,
                "repair": assessment,
                "anatomy_contact": anatomy_contact,
                "body_support": body_support,
                "source_admission_failures": source_failures,
            })
            quality_metrics[signature] = _candidate_quality_metrics(rig, action, start, end, rig_basis)
            if not source_failures:
                rank_key = (
                    *arc_body_support_rank_key(body_support),
                    float(assessment["repair_cost"]),
                    signature,
                )
                admitted.append((rank_key, signature, action, (start, end), row))
        except (SourceRepairError, ValueError, RuntimeError) as error:
            row["rejection_reason"] = str(error)
        rows.append(row)
    quality = quality_evidence(quality_metrics, "arc") if quality_metrics else {}
    eligible = []
    for candidate in admitted:
        row = candidate[4]
        row["quality"] = quality[candidate[1]]
        reasons = quality[candidate[1]]["reasons"]
        width = float(quality[candidate[1]]["metrics"]["maximum_lateral_width"])
        bounded_arc_anatomical_fit = (
            reasons == ["maximum_lateral_width exceeds severe geometry limit"]
            and RESHAPE_TARGET_LATERAL_WIDTH / width >= RESHAPE_MINIMUM_SCALE
        )
        row["quality"]["bounded_arc_anatomical_fit"] = {
            "eligible": bounded_arc_anatomical_fit,
            "algorithm": ARC_ANATOMICAL_FIT_ALGORITHM_VERSION,
            "preserves": ["curved Hips translation", "source heading", "source timing"],
        }
        if quality[candidate[1]]["accepted"] or bounded_arc_anatomical_fit:
            eligible.append(candidate)
        else:
            row["rejection_reason"] = "; ".join(reasons)
    if not eligible:
        raise SourceRepairError(f"no safely repairable semantic arc source candidate: {rows}")
    eligible.sort(key=lambda item: item[0])
    selected = eligible[0]
    for rank, candidate in enumerate(eligible, start=1):
        candidate[4]["rank"] = rank
        candidate[4]["rank_key"] = list(candidate[0])
        candidate[4]["selected"] = candidate is selected
    return selected[2], selected[3], {
        "dispatcher": "semantic_arc_phase_contact_curve_body_support_dispatch_v2",
        "role_family": "arc",
        "semantic_source_role": semantic_role,
        "source_segment_index": 5,
        "source_window_definition": "evaluated named-foot arc-tangent phase recurrence",
        "selection_precedence": [
            "phase/contact recurrence and curve/yaw semantic eligibility",
            "generic evaluated rig-scale-normalised quality",
            "sustained pelvis/chest rearward carriage from bilateral support in generated-Root heading",
            "bounded symmetric repair cost",
            "source-window content signature tie-break",
        ],
        "selected_source_window_content_signature": selected[1],
        "selected_body_support": selected[4]["body_support"],
        "selected_repair_cost": selected[4]["repair"]["repair_cost"],
        "candidates": rows,
    }


def repair_blend(
    input_path: Path,
    output_path: Path,
    report_path: Path,
    *,
    source_action_name: str | None,
    source_range: tuple[int, int] | None,
    roles: Sequence[str],
    derived_name: str | None = None,
) -> dict[str, Any]:
    """Repair through a staged blend and atomically publish only after reopen checks."""
    source = input_path.resolve()
    output = output_path.resolve()
    if not source.is_file():
        raise SourceRepairError(f"input blend does not exist: {source}")
    output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    before_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    with tempfile.TemporaryDirectory(prefix="kimodo-source-repair-", dir=output.parent) as directory:
        staged = Path(directory) / output.name
        shutil.copy2(source, staged)
        ensure_registered()
        bpy.ops.wm.open_mainfile(filepath=str(staged), load_ui=False, use_scripts=False)
        rig, _, _ = _rig()
        excluded_links = _exclude_unrelated_character_objects(rig)
        pose_state = _capture_pose_state(rig)
        dispatch_evidence = None
        family = _repair_family(roles)
        source_action: Any | None = None
        semantically_selected_action = None
        semantically_selected_range = None
        if family == "strafe":
            segment = next((item for item in _segments() if item["index"] == 4), None)
            if segment is None:
                raise SourceRepairError("semantic Segment 4 was not discovered")
            semantic_role = next((role for role in roles if role.startswith("xr_")), None)
            if semantic_role is None:
                raise SourceRepairError("strafe repair requires one native right-strafe phase role")
            reference = bpy.data.actions.get("pose_ref_idle")
            candidates = _semantic_source_candidates(
                rig, segment, (reference,) if reference is not None else (),
            )
            semantically_selected_action, semantically_selected_range, dispatch_evidence = _select_strafe_candidate(
                rig, candidates, semantic_role, segment, _rig()[2],
            )
        elif family == "precision_lateral":
            segment = next((item for item in _segments() if item["index"] == 1), None)
            if segment is None:
                raise SourceRepairError("semantic Segment 1 was not discovered")
            source_range = int(segment["start_frame"]), int(segment["end_frame"])
            reference = bpy.data.actions.get("pose_ref_idle")
            candidates = _semantic_source_candidates(
                rig, segment, (reference,) if reference is not None else (),
            )
            semantically_selected_action, dispatch_evidence = _select_precision_step_candidate(
                rig, candidates,
                source_range[0], source_range[1], _rig()[2],
            )
        elif family == "arc":
            segment = next((item for item in _segments() if item["index"] == 5), None)
            if segment is None:
                raise SourceRepairError("semantic Segment 5 was not discovered")
            semantic_role = next((role for role in roles if role.startswith("ar_")), None)
            if semantic_role is None:
                raise SourceRepairError("arc repair requires one native right-arc phase role")
            reference = bpy.data.actions.get("pose_ref_idle")
            candidates = _semantic_source_candidates(
                rig, segment, (reference,) if reference is not None else (),
            )
            semantically_selected_action, semantically_selected_range, dispatch_evidence = _select_arc_candidate(
                rig, candidates, semantic_role, segment, _rig()[2],
            )
        elif family == "pivot":
            segment = next((item for item in _segments() if item["index"] == 2), None)
            if segment is None:
                raise SourceRepairError("semantic pivot segment was not discovered")
            reference = bpy.data.actions.get("pose_ref_idle")
            candidates = _semantic_source_candidates(
                rig, segment, (reference,) if reference is not None else (),
            )
            semantically_selected_action, semantically_selected_range, dispatch_evidence = _select_pivot_candidate(
                rig, candidates, segment, _rig()[2],
            )
        if semantically_selected_action is not None:
            selected_range = semantically_selected_range or source_range
            assert selected_range is not None
            selected_signature = source_window_content_signature(
                semantically_selected_action, selected_range[0], selected_range[1],
            )
            # Optional Action/range arguments are locators only.  They are
            # consulted after semantic selection and must resolve equivalent
            # selected content; they cannot override the decision.
            source_action = (
                bpy.data.actions.get(source_action_name)
                if source_action_name is not None else semantically_selected_action
            )
            if source_action is None:
                raise SourceRepairError(f"source locator Action was not found: {source_action_name}")
            source_range = source_range or selected_range
            locator_signature = source_window_content_signature(
                source_action, source_range[0], source_range[1],
            )
            if locator_signature != selected_signature:
                raise SourceRepairError(
                    "source Action/range locator does not resolve the semantically selected content"
                )
            if dispatch_evidence is not None:
                dispatch_evidence["selected_content_locator"] = {
                    "source_action_name_evidence_only": source_action.name,
                    "absolute_source_range_evidence_only": list(source_range),
                    "source_window_content_signature": locator_signature,
                }
        if source_action is None:
            raise SourceRepairError("semantic source selection did not resolve repair content")
        assert source_range is not None
        source_action_name_evidence = source_action.name
        resolved_derived_name = derived_name or derived_action_name(
            source_action, source_range[0], source_range[1], roles,
        )
        before_signatures = action_signatures({resolved_derived_name})
        try:
            provenance = derive_repaired_action(
                rig, source_action, source_range[0], source_range[1], resolved_derived_name, roles,
                dispatch_evidence if family == "precision_lateral" else None,
            )
        except Exception:
            failed = bpy.data.actions.get(resolved_derived_name)
            if failed is not None:
                bpy.data.actions.remove(failed)
            raise
        expected_unrelated = before_signatures
        if not provenance["idempotent"]:
            _restore_pose_state(rig, pose_state)
            _restore_excluded_objects(excluded_links)
            bpy.ops.wm.save_as_mainfile(filepath=str(staged), check_existing=False)
        else:
            # An idempotent in-place run still republishes the byte-identical input.
            shutil.copy2(source, staged)

        bpy.ops.wm.open_mainfile(filepath=str(staged), load_ui=False, use_scripts=False)
        reopened = bpy.data.actions.get(resolved_derived_name)
        if reopened is None:
            raise SourceRepairError("reopen validation did not retain the derived source Action")
        if action_signatures({resolved_derived_name}) != expected_unrelated:
            raise SourceRepairError("unrelated Actions changed during source repair")
        durable = json.loads(reopened[PROVENANCE_PROPERTY])
        if durable["immutable_interior"] != provenance["immutable_interior"]:
            raise SourceRepairError("reopen validation changed source-repair provenance")
        after_hash = hashlib.sha256(staged.read_bytes()).hexdigest()
        report = {
            "algorithm": ALGORITHM_VERSION,
            "input": str(source), "output": str(output),
            "before_sha256": before_hash, "after_sha256": after_hash,
            "derived_action_signature": action_signature(reopened),
            "derived_curve_signature": _json_hash(_action_signature(reopened)[5]),
            "derived_evaluated_signature": evaluated_action_signature(
                reopened, durable["derived_range"][0], durable["derived_range"][1],
            ),
            "unrelated_action_signatures_before": expected_unrelated,
            "unrelated_action_signatures_after": action_signatures({resolved_derived_name}),
            "repair": {**durable, "idempotent": provenance["idempotent"]},
            "dispatcher_evidence": dispatch_evidence,
            "source_action_name_evidence_only": source_action_name_evidence,
        }
        temporary_report = Path(directory) / "source-repair-report.json"
        temporary_report.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
        os.replace(staged, output)
        os.replace(temporary_report, report_path)
        return report


def _expanded_roles(role_code: str) -> tuple[str, ...]:
    if role_code in {"sr", "sl"}:
        return "sr", "sl"
    if role_code in {"pr45", "pl45"}:
        return "pr45", "pl45"
    if role_code in {"ar_l", "al_r"}:
        return "ar_l", "al_r"
    if role_code in {"ar_r", "al_l"}:
        return "ar_r", "al_l"
    return (role_code,)


def repair_blend_combined(
    input_path: Path,
    output_path: Path,
    report_path: Path,
    role_codes: Sequence[str],
) -> dict[str, Any]:
    """Apply multiple semantic repairs behind one atomic publication boundary."""
    if not role_codes or len(set(role_codes)) != len(role_codes):
        raise SourceRepairError("combined source repair roles must be non-empty and duplicate-free")
    supported = {
        role.code for role in ROLE_REGISTRY
        if role.code.startswith(("xr_", "ar_", "al_"))
        or role.code in {"sr", "sl", "pr45", "pl45"}
    }
    unknown = sorted(set(role_codes) - supported)
    if unknown:
        raise SourceRepairError(f"combined source repair has unsupported roles: {unknown}")
    repair_targets = [frozenset(_expanded_roles(code)) for code in role_codes]
    if len(set(repair_targets)) != len(repair_targets):
        raise SourceRepairError("combined source repair contains equivalent duplicate role targets")

    source = input_path.resolve()
    output = output_path.resolve()
    if not source.is_file():
        raise SourceRepairError(f"input blend does not exist: {source}")
    output.parent.mkdir(parents=True, exist_ok=True)
    report_path.parent.mkdir(parents=True, exist_ok=True)
    before_hash = hashlib.sha256(source.read_bytes()).hexdigest()
    with tempfile.TemporaryDirectory(prefix="kimodo-source-repair-combined-", dir=output.parent) as directory:
        root = Path(directory)
        staged = root / output.name
        shutil.copy2(source, staged)
        reports = []
        for index, role_code in enumerate(role_codes):
            stage_report = root / f"{index:02d}-{role_code}.json"
            reports.append(repair_blend(
                staged, staged, stage_report,
                source_action_name=None,
                source_range=None,
                roles=_expanded_roles(role_code),
            ))
        after_hash = hashlib.sha256(staged.read_bytes()).hexdigest()
        report = {
            "algorithm": COMBINED_ALGORITHM_VERSION,
            "input": str(source),
            "output": str(output),
            "before_sha256": before_hash,
            "after_sha256": after_hash,
            "role_order_evidence_only": list(role_codes),
            "repairs": reports,
        }
        temporary_report = root / "combined-source-repair-report.json"
        temporary_report.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
        os.replace(staged, output)
        os.replace(temporary_report, report_path)
        return report


def main() -> int:
    parser = argparse.ArgumentParser(description="Derive an admitted real Kimodo source repair.")
    parser.add_argument("--input", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument(
        "--role",
        choices=sorted(
            role.code for role in ROLE_REGISTRY
            if role.code.startswith(("xr_", "ar_", "al_")) or role.code in {"sr", "sl", "pr45", "pl45"}
        ),
    )
    parser.add_argument(
        "--roles",
        help="Comma-separated semantic repair roles to apply behind one atomic publication boundary.",
    )
    parser.add_argument("--source-action")
    parser.add_argument("--source-start", type=int)
    parser.add_argument("--source-end", type=int)
    options = parser.parse_args(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
    try:
        if (options.role is None) == (options.roles is None):
            raise SourceRepairError("supply exactly one of --role or --roles")
        if (options.source_start is None) != (options.source_end is None):
            raise SourceRepairError("source start and end must be supplied together")
        if options.roles is not None:
            if options.source_action is not None or options.source_start is not None:
                raise SourceRepairError("source locators are only valid for a single semantic repair")
            report = repair_blend_combined(
                Path(options.input), Path(options.output), Path(options.report),
                options.roles.split(","),
            )
        else:
            roles = _expanded_roles(options.role)
            report = repair_blend(
                Path(options.input), Path(options.output), Path(options.report),
                source_action_name=options.source_action,
                source_range=(options.source_start, options.source_end) if options.source_start is not None else None,
                roles=roles,
            )
    except (SourceRepairError, OSError, ValueError) as error:
        print(f"Kimodo source repair failed: {error}", file=sys.stderr)
        return 2
    print("KIMODO_SOURCE_REPAIR_JSON=" + json.dumps(report, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
