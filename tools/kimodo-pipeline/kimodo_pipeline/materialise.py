"""Staged Blender materialisation for the ANIM-002 Kimodo source contract."""
from __future__ import annotations

import json
import hashlib
import math
import os
from pathlib import Path
import shutil
import tempfile
from types import MappingProxyType
from typing import Any, Mapping, Sequence

import bpy
from mathutils import Matrix, Quaternion, Vector

from .compatibility import ensure_registered
from .anatomical_scale import (
    ANATOMICAL_SCALE_PROCEDURE,
    AnatomicalScaleError,
    anatomical_character_scale,
    is_anatomical_motion_bone,
)
from .arc_semantics import arc_body_support_rank_key, arc_curve_evidence, compose_local_arc_cycles
from .contracts import (
    ROLE_REGISTRY, action_name,
    pivot_root_yaw_sign, role_projection_policy, transition_boundary_phase, validate_variant,
)
from .pivot import PivotHeadingProjection, pivot_unit_yaw, project_pivot_heading
from .precision_step import (
    PRECISION_CALIBRATION_BASELINE_ACTION, PrecisionStepError, PrecisionStepLimits,
    assess_precision_foot_order, assess_precision_semantic_events,
    normalise_precision_step, precision_candidate_rank_key,
)
from .recurrence import RECURRENCE_CONTRACT, foot_recurrence_flags, recurrence_acceptance, recurrence_thresholds
from .loop_seam import (
    REPAIR_BLEND_PROFILES,
    REPAIR_CONTRACT,
    REPAIR_PROVENANCE_CONTRACT,
    adaptive_repair_specs,
    evaluated_hierarchy_cadence,
    assess_repair,
    correction_asymmetry_evidence,
    repair_naturalness_key,
    repair_profile_weight,
    symmetric_window,
    validate_durable_repair_windows,
    validate_loop_repair_provenance,
)
from .lateral_basis import LateralBasisError, anatomical_lateral_basis
from .phase_events import (
    DERIVATION_EVENT_CONTRACT,
    PHASE_EVENT_ALGORITHM,
    PHASE_EVENT_EVIDENCE_VERSION,
    POST_REPAIR_EVENT_CONTRACT,
    PHASE_EVENT_DETECTOR_STENCIL_SAMPLES,
    SEED_DISPLACEMENT_CONTRACT,
    SOURCE_DIRECTIONAL_EVENT_CONTRACT,
    PhaseEventEvidenceError,
    canonicalise_duplicate_endpoint_events,
    canonical_frame_delta,
    derive_output_event,
    derivation_operations,
    event_id,
    expected_phase_event_sides,
    transition_boundary_context,
    validate_phase_event_evidence,
    validate_event_derivation_evidence,
    validate_post_repair_event_evidence,
    validate_source_directional_event_evidence,
)
from .transport import TransportContractError, make_transport_evidence, validate_transport_evidence
from .workflow_binding import (
    MATERIALISATION_BINDING_CONTRACT,
    WorkflowBindingError,
    sha256_file,
    validate_action_provenance_binding,
    validate_repair_precursor,
)
from .discovery import (
    REFERENCE_ACTION_NAME,
    DiscoveryError,
    _action_fcurves,
    _segments,
    _source_rig,
    validate_action_for_rig,
)
from .selection import (
    RELEASE_FORWARD_CONFIDENCE_MINIMUM,
    RELEASE_FORWARD_MINIMUM,
    RELEASE_HYSTERESIS_FRAMES,
    RELEASE_SPEED_MARGIN,
    FootGeometry,
    FootSample,
    SelectionError,
    derived_anatomical_side,
    locomotion_direction,
    phase_semantics,
    directional_phase_event_observation,
    map_transition_event_to_output_anatomy,
    natural_cycle_candidate_rank_key,
    natural_cycle_quality_rank_key,
    quality_evidence,
    canonical_transition_events,
    release_event_evidence,
    release_events,
    recurrence_window_evidence,
    role_window,
    score_candidate,
    transition_evidence,
)
from .temp_paths import repository_temp_root, resolve_temp_root
from .travel_frame import (
    DEFAULT_TRAVEL_FRAME_LIMITS,
    TravelFrameError,
    alignment_angle,
    estimate_source_travel_frame,
    estimate_finite_transition_frame,
)

ROOT, HIPS = 'pose.bones["Root"]', 'pose.bones["Hips"]'
EULER_OUTPUT_MODE = "ZYX"
METADATA = ("anim002_root_translation_trajectory", "anim002_root_yaw_trajectory", "anim002_phase", "anim002_boundary", "anim002_left_contact", "anim002_right_contact", "anim002_left_contact_confidence", "anim002_right_contact_confidence", "anim002_endpoint_displacement", "anim002_endpoint_yaw", "anim002_source_window_complete", "anim002_source_directional_event_evidence", "anim002_event_derivation_evidence", "anim002_post_repair_event_evidence", "anim002_transport_contract")
DEFAULT_TOLERANCES = {
    "root_zero_tolerance": 1e-4,
    "root_direction_minimum": 1e-4,
    # Numerical representation fidelity is deliberately distinct from physical
    # recurrence. It checks cycle modifiers/Root accumulation, not anatomy.
    "reconstruction_translation_tolerance": 1e-5,
    "reconstruction_rotation_tolerance": 1e-5,
    "internal_discontinuity_tolerance": 10.0,
    # These are measured from evaluated armature-space/world-space geometry,
    # rather than Action channel values.  They deliberately leave room for
    # ordinary gait sway while rejecting a second accumulated carriage path.
    "body_horizontal_residual_maximum": 0.20,
    "body_longitudinal_residual_maximum": 0.15,
    "body_drift_recovery_maximum": 0.01,
    "body_centring_tolerance": 1e-4,
    "body_vertical_residual_maximum": 0.20,
    "foot_vertical_residual_maximum": 0.35,
    "body_ground_clearance_minimum": -0.25,
    # Matrix-space oracle tolerance.  This is intentionally far tighter than
    # gameplay-scale gait checks: reconstruction is a change of hierarchy
    # representation, not an opportunity to reshape evaluated motion.
    "transform_oracle_tolerance": 5e-4,
    "precision_crossover_tolerance_scale": 0.03,
}
FOOT_GEOMETRY_SEMANTICS = (
    "hips_relative_position",
    "root_rest_forward",
    "root_rest_lateral",
    "root_rest_up",
    "hips_travel_arc_tangent",
)


class MaterialisationError(RuntimeError):
    """A mandatory source, rig, or reopen contract was not met."""


def _requested_roles(roles: Sequence[str] | None) -> tuple[Any, ...]:
    """Resolve an explicit, non-empty, duplicate-free registry role selection."""
    if roles is None:
        return ROLE_REGISTRY
    if isinstance(roles, (str, bytes)) or not isinstance(roles, Sequence) or not roles:
        raise MaterialisationError("roles must be a non-empty sequence of exact registry role codes")
    registry = {role.code: role for role in ROLE_REGISTRY}
    selected = []
    seen = set()
    for code in roles:
        if not isinstance(code, str) or not code or code != code.strip() or "," in code:
            raise MaterialisationError("roles must contain non-empty exact registry role codes")
        if code in seen:
            raise MaterialisationError(f"roles contains duplicate registry role code: {code}")
        if code not in registry:
            raise MaterialisationError(f"roles contains unknown registry role code: {code}")
        seen.add(code)
        selected.append(registry[code])
    return tuple(selected)


def _property_value(value: Any) -> Any:
    """Turn Blender ID-property values into stable, comparable Python values."""
    if hasattr(value, "to_list"):
        return _property_value(value.to_list())
    if isinstance(value, (list, tuple)):
        return tuple(_property_value(item) for item in value)
    if isinstance(value, dict):
        return tuple(sorted((str(key), _property_value(item)) for key, item in value.items()))
    return value


def _action_signature(action: Any) -> tuple[Any, ...]:
    """Capture Action content which staged role updates are forbidden to alter."""
    curves = []
    for curve in sorted(_action_fcurves(action), key=lambda item: (item.data_path, item.array_index)):
        keys = tuple(
            (tuple(point.co), tuple(point.handle_left), tuple(point.handle_right), point.interpolation, point.handle_left_type, point.handle_right_type)
            for point in curve.keyframe_points
        )
        modifiers = tuple(_modifier_signature(modifier) for modifier in curve.modifiers)
        curves.append((curve.data_path, curve.array_index, curve.extrapolation, keys, modifiers))
    metadata = tuple(sorted((key, _property_value(action[key])) for key in action.keys()))
    slots = tuple((slot.identifier, slot.target_id_type) for slot in action.slots)
    return action.name, action.use_fake_user, tuple(action.frame_range), metadata, slots, tuple(curves)


def _source_window_content_signature(action: Any, start: int, end: int) -> str:
    """Return a name/range-position-independent deterministic window key."""
    rows = []
    for curve in sorted(_action_fcurves(action), key=lambda item: (item.data_path, item.array_index)):
        values = [round(float(curve.evaluate(frame)), 9) for frame in range(start, end + 1)]
        rows.append((curve.data_path, curve.array_index, values))
    payload = json.dumps(rows, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _matrix_signature(matrix: Any) -> tuple[tuple[float, ...], ...]:
    """Return a stable value key for a geometry-defining Blender matrix."""
    return tuple(tuple(float(matrix[row][column]) for column in range(len(matrix[row]))) for row in range(len(matrix)))


def _modifier_signature(modifier: Any) -> tuple[Any, ...]:
    """Capture writable F-curve modifier settings without assuming ID properties."""
    values = []
    for property_definition in modifier.bl_rna.properties:
        if property_definition.identifier == "rna_type" or property_definition.is_readonly:
            continue
        if property_definition.type in {"POINTER", "COLLECTION"}:
            continue
        values.append((property_definition.identifier, _property_value(getattr(modifier, property_definition.identifier))))
    return modifier.type, tuple(values)


def _preservation_snapshot(target_names: set[str]) -> dict[str, tuple[Any, ...]]:
    return {action.name: _action_signature(action) for action in bpy.data.actions if action.name not in target_names}


def _assert_preserved_actions(expected: dict[str, tuple[Any, ...]], target_names: set[str]) -> None:
    """Ensure a staged role update has not changed any non-target Action."""
    actual = _preservation_snapshot(target_names)
    if actual != expected:
        changed = sorted(name for name in expected.keys() & actual.keys() if expected[name] != actual[name])
        missing, added = sorted(expected.keys() - actual.keys()), sorted(actual.keys() - expected.keys())
        raise MaterialisationError(
            f"reopen validation did not preserve non-target Actions; changed: {changed}, missing: {missing}, added: {added}"
        )


def default_temp_root(blend: Path) -> Path:
    del blend
    return repository_temp_root()


def _curves(action: Any) -> dict[tuple[str, int], Any]:
    return {(curve.data_path, curve.array_index): curve for curve in _action_fcurves(action)}


def _activate_action(rig: Any, action: Any) -> None:
    """Assign the Action and its object slot; Action assignment alone is ambiguous."""
    animation = rig.animation_data_create()
    slot = next((item for item in action.slots if item.target_id_type == rig.id_type), None)
    if slot is None:
        raise MaterialisationError(f"{action.name} has no compatible Action slot for {rig.name}")
    animation.action = action
    animation.action_slot = slot


def _rig() -> tuple[Any, int, Any]:
    rig = _source_rig()
    missing = {"Root", "Hips", "LeftFoot", "RightFoot"} - {bone.name for bone in rig.pose.bones}
    if missing:
        raise MaterialisationError(f"Kimodo Source rig is missing required bones: {sorted(missing)}")
    root = rig.data.bones["Root"]
    if (root.tail_local - root.head_local).length <= 1e-6:
        raise MaterialisationError("Kimodo Source Root has no valid rest-axis basis")
    basis = root.matrix_local.to_3x3()
    if abs(basis.determinant()) <= 1e-6:
        raise MaterialisationError("Kimodo Source Root has a singular rest-axis basis")
    # Source Hips translation is expressed in the rig basis.  Keep this
    # conversion in one place so Root reconstruction works for any valid rest
    # orientation rather than relying on a baked local-X/local-Z convention.
    return rig, 1, basis


def _euler_target_bones(action: Any) -> set[str]:
    """Return bones addressed by Euler curves, rejecting mixed rotation paths.

    Blender evaluates a pose-bone's rotation channels according to its current
    ``rotation_mode``. An Euler F-curve on a quaternion-mode bone is accepted
    as data but does not drive the evaluated pose, which this pipeline must not
    publish. The source rig convention is ZYX, so no lossy quaternion-to-Euler
    conversion is attempted here.
    """
    euler_bones: set[str] = set()
    other_rotation_bones: set[str] = set()
    for curve in _action_fcurves(action):
        if not curve.data_path.startswith('pose.bones["'):
            continue
        bone_name = curve.data_path.split('"', 2)[1]
        if curve.data_path.endswith(".rotation_euler"):
            euler_bones.add(bone_name)
        elif curve.data_path.endswith((".rotation_quaternion", ".rotation_axis_angle")):
            other_rotation_bones.add(bone_name)
    mixed = euler_bones & other_rotation_bones
    if mixed:
        raise MaterialisationError(
            f"{action.name} mixes Euler and non-Euler rotation paths for {sorted(mixed)}"
        )
    return euler_bones


def _assert_euler_evaluation_convention(rig: Any, actions: Sequence[Any]) -> None:
    """Require the saved ZYX Euler convention before sampling or publication.

    This deliberately validates instead of implicitly changing a rig mode:
    changing source representation can alter unrelated Actions. Sampling
    helpers restore the active Action, Action slot, and scene frame after use.
    """
    if rig.pose.bones["Root"].rotation_mode != EULER_OUTPUT_MODE:
        raise MaterialisationError("Kimodo Source Root must use the ZYX Euler evaluation convention")
    for action in actions:
        for bone_name in _euler_target_bones(action):
            bone = rig.pose.bones.get(bone_name)
            if bone is None:
                continue  # validate_action_for_rig emits the actionable error.
            if bone.rotation_mode != EULER_OUTPUT_MODE:
                raise MaterialisationError(
                    f"{action.name} writes rotation_euler for {bone_name}, but the rig uses "
                    f"{bone.rotation_mode}; expected {EULER_OUTPUT_MODE}"
                )


def _root_local_translation(rig_basis: Any, translation: Vector) -> Vector:
    """Convert a horizontal rig-space Hips displacement into Root-local space."""
    return rig_basis.inverted_safe() @ translation


def _reconstruct_root(
    role: Any,
    root: list[list[float]],
    source_yaw: list[float],
    pivot_projection: PivotHeadingProjection | None = None,
) -> tuple[list[list[float]], list[float]]:
    """Project evaluated Hips motion through the closed role-family policy."""
    policy = role_projection_policy(role)
    result = [[0.0] * len(source_yaw) for _ in range(3)]
    for axis in policy.translation_axes:
        result[axis] = root[axis]
    if policy.family == "pivot":
        if pivot_projection is not None:
            if pivot_projection.role_code != role.code:
                raise MaterialisationError("pivot projection role does not match Root reconstruction role")
            return result, list(pivot_projection.unit_yaw)
        try:
            # Root local-Y and armature/global-Z have the same evaluated sign
            # on this rig. ``source_yaw`` is already reflected when required.
            yaw = pivot_unit_yaw(role.code, source_yaw)
        except ValueError as error:
            raise MaterialisationError(f"{role.code} cannot reconstruct source-paced pivot yaw: {error}") from error
    else:
        yaw = source_yaw if policy.promote_global_z_yaw else [0.0] * len(source_yaw)
    return result, yaw


def _heading_progress_from_armature_rows(rows: Sequence[Mapping[str, Matrix]]) -> list[float]:
    """Return continuous Hips armature/global-Z heading relative to the opening row."""
    if not rows:
        raise MaterialisationError("heading reconstruction requires evaluated hierarchy rows")
    opening = rows[0]["Hips"]
    wrapped = [_global_z_yaw(row["Hips"] @ opening.inverted_safe()) for row in rows]
    result = [wrapped[0]]
    for value in wrapped[1:]:
        previous = result[-1]
        while value - previous > math.pi:
            value -= 2.0 * math.pi
        while value - previous < -math.pi:
            value += 2.0 * math.pi
        result.append(value)
    return result


def _foot_geometry(rig: Any, action: Any, start: int, end: int, rig_basis: Any) -> dict[str, dict[int, FootSample]]:
    """Measure feet on the fixed Root-forward horizontal axis.

    Hips supplies only the translating origin.  Its animated orientation is
    deliberately not used: the Root rest basis defines the immutable forward
    axis for both position diagnosis and relative foot velocity.
    """
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        try:
            lateral_basis = anatomical_lateral_basis(rig, rig_basis)
        except LateralBasisError as error:
            raise MaterialisationError(str(error)) from error
        world_up = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
        forward = rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 0.0, 1.0))
        forward = (forward - world_up * forward.dot(world_up)).normalized()
        if forward.length <= 1e-6:
            raise MaterialisationError("Kimodo Source Root has no horizontal forward axis")
        lateral = (rig.matrix_world.to_3x3() @ lateral_basis.axis).normalized()
        positions = {"Left": {}, "Right": {}}
        lateral_positions = {"Left": {}, "Right": {}}
        relative_positions = {"Left": {}, "Right": {}}
        hips_positions = {}
        for frame in range(start, end + 1):
            bpy.context.scene.frame_set(frame)
            depsgraph = bpy.context.evaluated_depsgraph_get()
            evaluated = rig.evaluated_get(depsgraph)
            hips = rig.matrix_world @ evaluated.pose.bones["Hips"].matrix.translation
            hips_positions[frame] = hips
            for side in positions:
                foot = rig.matrix_world @ evaluated.pose.bones[f"{side}Foot"].matrix.translation
                relative = foot - hips
                positions[side][frame] = float(relative.dot(forward))
                lateral_positions[side][frame] = float(relative.dot(lateral))
                relative_positions[side][frame] = relative
        result: dict[str, dict[int, FootSample]] = {"Left": {}, "Right": {}}
        for side, values in positions.items():
            previous: float | None = None
            for frame in range(start, end + 1):
                position = values[frame]
                velocity = Vector() if previous is None else relative_positions[side][frame] - relative_positions[side][frame - 1]
                # Arc phases use the evaluated horizontal Hips travel tangent
                # at each frame.  This is the required curved-travel geometry,
                # rather than a fixed world/Root-forward proxy.
                hips_delta = Vector() if previous is None else hips_positions[frame] - hips_positions[frame - 1]
                tangent = hips_delta - world_up * hips_delta.dot(world_up)
                if tangent.length <= 1e-6:
                    tangent = forward.copy()
                else:
                    tangent.normalize()
                arc_position = float(relative_positions[side][frame].dot(tangent))
                arc_velocity = float(velocity.dot(tangent))
                # Store velocity in the immutable Root semantic frame. Its
                # final component is Root-forward, never arbitrary world-Z.
                result[side][frame] = FootSample(
                    position,
                    (
                        float(velocity.dot(lateral)),
                        float(velocity.dot(world_up)),
                        float(velocity.dot(forward)),
                    ),
                    lateral_positions[side][frame],
                    arc_position,
                    arc_velocity,
                )
                previous = position
        return result
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


class EvaluatedFootGeometryCache:
    """Own immutable pre-mutation source-foot samples for one materialisation selection pass.

    Entries are deliberately value-keyed by Action identity and content, frame
    interval, rig/world and Root-rest bases, and the declared measurement
    semantics. A changed Action or measurement context is therefore a miss,
    not a potentially stale reuse. ``close`` is called before output Actions
    are created so this cache can never sample or retain post-mutation state.
    """

    def __init__(self, rig: Any, rig_basis: Any) -> None:
        self._rig = rig
        self._rig_basis = rig_basis
        self._entries: dict[tuple[Any, ...], Mapping[str, Mapping[int, FootSample]]] = {}
        self.hits = 0
        self.misses = 0
        self._closed = False

    def get(self, action: Any, start: int, end: int) -> Mapping[str, Mapping[int, FootSample]]:
        if self._closed:
            raise MaterialisationError("evaluated foot geometry cache is closed")
        if start > end:
            raise MaterialisationError("evaluated foot geometry cache requires an inclusive ascending frame range")
        key = (
            self._rig.as_pointer(),
            action.as_pointer(),
            action.name,
            _action_signature(action),
            start,
            end,
            _matrix_signature(self._rig_basis),
            _matrix_signature(self._rig.matrix_world),
            FOOT_GEOMETRY_SEMANTICS,
        )
        cached = self._entries.get(key)
        if cached is not None:
            self.hits += 1
            return cached
        self.misses += 1
        sampled = _foot_geometry(self._rig, action, start, end, self._rig_basis)
        immutable = MappingProxyType({
            side: MappingProxyType(dict(samples))
            for side, samples in sampled.items()
        })
        self._entries[key] = immutable
        return immutable

    def close(self) -> None:
        """Discard source snapshots before materialisation begins mutation."""
        self._entries.clear()
        self._closed = True


def _precision_foot_order_evidence(
    rig: Any,
    rig_basis: Any,
    role_code: str,
    geometry: FootGeometry,
    start: int,
    end: int,
    stage: str,
    maximum_crossover_depth_scale: float = 0.03,
) -> dict[str, Any]:
    """Apply the precision-only gate to evaluated positive-left foot coordinates."""
    try:
        basis = anatomical_lateral_basis(rig, rig_basis)
        return assess_precision_foot_order(
            role_code,
            [
                (frame, geometry["Left"][frame].lateral_position, geometry["Right"][frame].lateral_position)
                for frame in range(start, end + 1)
            ],
            _anatomical_character_scale(rig),
            stage=stage,
            basis_evidence=basis.evidence,
            limits=PrecisionStepLimits(maximum_crossover_depth_scale=maximum_crossover_depth_scale),
        )
    except (LateralBasisError, PrecisionStepError) as error:
        raise SelectionError(f"{role_code} precision anatomical foot-order evidence is invalid: {error}") from error


def _precision_semantic_event_evidence(
    geometry: FootGeometry, start: int, end: int, rig_scale: float,
) -> dict[str, Any]:
    """Measure complete precision events from immutable evaluated Segment-1 data."""
    samples = [
        (
            frame,
            geometry["Left"][frame].velocity[0], geometry["Left"][frame].speed,
            geometry["Right"][frame].velocity[0], geometry["Right"][frame].speed,
        )
        for frame in range(start, end + 1)
    ]
    trajectory = [
        (geometry["Left"][frame].lateral_position + geometry["Right"][frame].lateral_position) * .5
        for frame in range(start, end + 1)
    ]
    try:
        return assess_precision_semantic_events(samples, trajectory, rig_scale)
    except PrecisionStepError as error:
        raise SelectionError(f"precision complete-event assessment is invalid: {error}") from error


def _precision_leg_silhouette_evidence(
    rig: Any, rig_basis: Any, role_code: str, action: Any, start: int, end: int, stage: str,
    tolerance_scale: float = 0.03,
) -> dict[str, Any]:
    """Use the shared evaluated-chain metric; feet remain supplemental only."""
    from .source_repair import SourceRepairError, precision_leg_silhouette_evidence

    try:
        evidence = precision_leg_silhouette_evidence(rig, action, start, end, rig_basis, stage, role_code)
    except SourceRepairError as error:
        raise SelectionError(f"{role_code} precision leg-silhouette evidence is invalid: {error}") from error
    # Materialisation owns the configured output tolerance, never an Action-specific exception.
    normalised_depth = float(evidence["maximum_scale_normalised_crossover_depth"])
    evidence["normalised_tolerance"] = tolerance_scale
    evidence["minimum_allowed_signed_separation"] = -tolerance_scale * float(evidence["anatomical_scale"])
    evidence["accepted"] = normalised_depth <= tolerance_scale
    evidence["rejection_reason"] = None if evidence["accepted"] else (
        f"maximum scale-normalised leg-silhouette crossover depth {normalised_depth:.9f} exceeds {tolerance_scale:.9f}"
    )
    return evidence


def _candidate_quality_metrics(
    rig: Any,
    action: Any,
    start: int,
    end: int,
    rig_basis: Any,
    geometry_cache: EvaluatedFootGeometryCache | None = None,
) -> dict[str, float]:
    """Return evaluated gait, limb-chain, contact, and carriage evidence."""
    geometry = geometry_cache.get(action, start, end) if geometry_cache is not None else _foot_geometry(rig, action, start, end, rig_basis)
    scale = _anatomical_character_scale(rig)
    animation, previous_action, previous_frame = rig.animation_data_create(), rig.animation_data.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        up = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
        forward = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 0.0, 1.0))).normalized()
        try:
            lateral = (rig.matrix_world.to_3x3() @ anatomical_lateral_basis(rig, rig_basis).axis).normalized()
        except LateralBasisError as error:
            raise MaterialisationError(str(error)) from error
        names = ("Hips", "Chest", "LeftLeg", "LeftShin", "LeftFoot", "RightLeg", "RightShin", "RightFoot")
        rows = []
        for frame in range(start, end + 1):
            bpy.context.scene.frame_set(frame)
            evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
            rows.append({name: rig.matrix_world @ evaluated.pose.bones[name].matrix for name in names})
        rest_points = {
            name: rig.matrix_world @ rig.data.bones[name].matrix_local.translation
            for name in names
        }
        rest_lengths = {
            (upper, lower): (rest_points[lower] - rest_points[upper]).length
            for upper, lower in (
                ("LeftLeg", "LeftShin"), ("LeftShin", "LeftFoot"),
                ("RightLeg", "RightShin"), ("RightShin", "RightFoot"),
            )
        }
        pelvis_width = (rest_points["LeftLeg"] - rest_points["RightLeg"]).length
        if min(*rest_lengths.values(), pelvis_width) <= 1e-8:
            raise MaterialisationError("candidate quality requires non-degenerate evaluated leg geometry")
        lateral_sign = 1.0 if (rest_points["LeftLeg"] - rest_points["RightLeg"]).dot(lateral) >= 0.0 else -1.0
        positions = [
            (row["Hips"].translation, row["LeftFoot"].translation, row["RightFoot"].translation)
            for row in rows
        ]
        separations = [abs((left - right).dot(lateral)) / scale for _, left, right in positions]
        ordered_widths = sorted(separations)
        percentile_index = min(len(ordered_widths) - 1, math.ceil(.90 * len(ordered_widths)) - 1)
        contact_widths = [
            width for width, (_, left, right) in zip(separations, positions)
            if min(left.dot(up), right.dot(up)) <= min(
                min(sample[1].dot(up), sample[2].dot(up)) for sample in positions
            ) + scale * .05
        ]
        reaches = [abs((left - right).dot(forward)) / scale for _, left, right in positions]
        support = [((hips - (left + right) * .5) - up * (hips - (left + right) * .5).dot(up)).length / scale for hips, left, right in positions]
        ground = [(max(left.dot(up), right.dot(up)) - min(left.dot(up), right.dot(up))) / scale for _, left, right in positions]
        speeds = [sample.speed / scale for side in geometry.values() for sample in side.values()]
        accelerations = [abs(b - a) for side in geometry.values() for a, b in zip([sample.speed / scale for sample in side.values()], [sample.speed / scale for sample in list(side.values())[1:]])]
        jerks = [abs(b - a) for a, b in zip(accelerations, accelerations[1:])]
        slips = [min(geometry["Left"][frame].speed, geometry["Right"][frame].speed) / scale for frame in range(start + 1, end + 1)]
        segment_scale_errors = []
        extension_ratios = []
        lateral_separations = []
        collapsed_frames = 0
        torso_skews = []
        torso_tilts = []
        knee_bends: dict[str, list[Vector]] = {"Left": [], "Right": []}
        for row in rows:
            per_level_separation = []
            for level in ("Shin", "Foot"):
                separation = lateral_sign * (
                    row[f"Left{level}"].translation - row[f"Right{level}"].translation
                ).dot(lateral) / pelvis_width
                lateral_separations.append(separation)
                per_level_separation.append(abs(separation))
            if max(per_level_separation) < .10:
                collapsed_frames += 1
            torso = row["Chest"].translation - row["Hips"].translation
            torso_vertical = torso.dot(up)
            torso_horizontal = torso - up * torso_vertical
            torso_skews.append(torso_horizontal.length / max(abs(torso_vertical), 1e-8))
            torso_tilts.append(math.acos(max(-1.0, min(1.0, torso.normalized().dot(up)))))
            hips_inverse = row["Hips"].to_3x3().inverted_safe()
            for side in ("Left", "Right"):
                upper_name, lower_name, foot_name = f"{side}Leg", f"{side}Shin", f"{side}Foot"
                hip, knee, ankle = (
                    row[upper_name].translation, row[lower_name].translation, row[foot_name].translation,
                )
                upper_length = (knee - hip).length
                lower_length = (ankle - knee).length
                expected_upper = rest_lengths[(upper_name, lower_name)]
                expected_lower = rest_lengths[(lower_name, foot_name)]
                segment_scale_errors.extend((
                    abs(upper_length / expected_upper - 1.0),
                    abs(lower_length / expected_lower - 1.0),
                ))
                extension_ratios.append((ankle - hip).length / (expected_upper + expected_lower))
                leg_axis = ankle - hip
                bend = knee - hip - leg_axis * ((knee - hip).dot(leg_axis) / max(leg_axis.length_squared, 1e-12))
                if bend.length / (expected_upper + expected_lower) >= .02:
                    knee_bends[side].append((hips_inverse @ bend).normalized())
        knee_orientation_dots = []
        for bends in knee_bends.values():
            if not bends:
                knee_orientation_dots.append(-1.0)
                continue
            dominant = Vector((0.0, 0.0, 0.0))
            for bend in bends:
                dominant += bend
            if dominant.length <= 1e-8:
                knee_orientation_dots.append(-1.0)
                continue
            dominant.normalize()
            knee_orientation_dots.append(min(bend.dot(dominant) for bend in bends))
        return {
            "maximum_lateral_width": max(separations, default=0.0),
            "median_lateral_width": ordered_widths[len(ordered_widths) // 2] if ordered_widths else 0.0,
            "p90_lateral_width": ordered_widths[percentile_index] if ordered_widths else 0.0,
            "endpoint_lateral_width": separations[-1] if separations else 0.0,
            "frames_above_lateral_width_soft_target": float(sum(width > .70 for width in separations)),
            "duration_fraction_above_lateral_width_soft_target": sum(width > .70 for width in separations) / max(1, len(separations)),
            "frames_above_lateral_width_ceiling": float(sum(width > 1.20 for width in separations)),
            "duration_fraction_above_lateral_width_ceiling": sum(width > 1.20 for width in separations) / max(1, len(separations)),
            "contact_conditioned_maximum_lateral_width": max(contact_widths, default=0.0),
            "maximum_fore_aft_reach": max(reaches, default=0.0),
            "maximum_leg_segment_scale_error": max(segment_scale_errors, default=0.0),
            "maximum_leg_extension_ratio": max(extension_ratios, default=0.0),
            "minimum_knee_orientation_dot": min(knee_orientation_dots, default=-1.0),
            "minimum_leg_lateral_separation_ratio": min(lateral_separations, default=0.0),
            "collapsed_leg_fraction": collapsed_frames / max(1, len(rows)),
            "maximum_torso_skew_ratio": max(torso_skews, default=0.0),
            "maximum_torso_tilt_rad": max(torso_tilts, default=0.0),
            "foot_speed": max(speeds, default=0.0),
            "foot_acceleration": max(accelerations, default=0.0), "foot_jerk": max(jerks, default=0.0),
            "ground_envelope": max(ground, default=0.0), "planted_foot_slip": max(slips, default=0.0),
            "body_support_offset": max(support, default=0.0),
        }
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)


def _evaluated_hips_root_motion(rig: Any, action: Any, frames: list[int], rig_basis: Any, derivation: str) -> list[list[float]]:
    """Capture canonical Hips carriage and reject pre-owned source Root carriage."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        positions, roots = [], []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            positions.append(_root_local_translation(rig_basis, rig.pose.bones["Hips"].matrix.translation))
            roots.append(_root_local_translation(rig_basis, rig.pose.bones["Root"].matrix.translation))
        root_origin = roots[0]
        root_carriage = [root - root_origin for root in roots]
        maximum_source_root_carriage = max(vector.length for vector in root_carriage)
        if maximum_source_root_carriage > 1e-5:
            raise MaterialisationError(
                f"{action.name} source window {min(frames)}:{max(frames)} has animated source Root carriage "
                f"{maximum_source_root_carriage:.9f} m; Root ownership would be ambiguous"
            )
        origin = positions[0]
        values = [[position[index] - origin[index] for position in positions] for index in range(3)]
        if "mirror" in derivation:
            values[0] = [-value for value in values[0]]
        return values
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)


def _declared_straight_direction(role_code: str) -> tuple[float, float]:
    """Return declared Root-local (lateral, forward) travel direction."""
    if role_code.startswith(("wf", "i2f", "f2i")):
        return 0.0, 1.0
    if role_code.startswith(("wb", "i2b", "b2i")):
        return 0.0, -1.0
    if role_code.startswith(("xr", "sr")):
        return -1.0, 0.0
    if role_code.startswith(("xl", "sl")):
        return 1.0, 0.0
    raise MaterialisationError(f"{role_code} has no declared straight translation direction")


def _constant_alignment_delta(rig: Any, angle: float) -> Matrix:
    rest = rig.data.bones["Root"].matrix_local
    # ``angle`` is reported in the conventional (lateral, forward) plane.
    # Blender's positive local-Y matrix uses the opposite planar sign.
    return rest @ Matrix.Rotation(-angle, 4, "Y") @ rest.inverted_safe()


def _align_about(matrix: Matrix, alignment: Matrix, origin: Vector) -> Matrix:
    return Matrix.Translation(origin) @ alignment @ Matrix.Translation(-origin) @ matrix


def _cycle_normalised_source_transform(
    matrix: Matrix, alignment: Matrix, opening_hips: Vector, opening_root: Vector, rig_basis: Matrix,
) -> Matrix:
    """Reanchor source ground position before moving/yawing Root normalisation.

    A source sequence's arbitrary armature-space offset is evidence, not a
    lever arm around generated Root yaw. Retain Hips height while centring the
    opening character horizontally on generated Root.
    """
    up = (rig_basis.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
    offset = opening_hips - opening_root
    horizontal = offset - up * offset.dot(up)
    aligned = _align_about(matrix, alignment, opening_hips)
    return Matrix.Translation(-horizontal) @ aligned


def _straight_source_projection(
    rig: Any, role: Any, action: Any, frames: list[int], rig_basis: Any,
) -> tuple[list[list[float]], list[list[float]], Matrix, dict[str, Any]]:
    """Map immutable source-paced straight carriage once into role direction."""
    source = _evaluated_hips_root_motion(rig, action, frames, rig_basis, role.derivation)
    policy = role_projection_policy(role)
    if policy.family == "precision_lateral":
        _, source_heading = _evaluated_body_relative(
            rig, action, frames, "mirror" in role.derivation, rig_basis, True,
        )
        try:
            normalised = normalise_precision_step(
                role.code,
                tuple(zip(source[0], source[2])),
                source_heading,
                _anatomical_character_scale(rig),
            )
        except PrecisionStepError as error:
            raise SelectionError(
                f"{role.code} source {action.name} complete Segment-1 cycle is unsafe: {error}"
            ) from error
        projected = [list(axis) for axis in source]
        projected[0] = list(normalised.lateral_samples)
        return projected, source, Matrix.Identity(4), normalised.evidence
    if not policy.straight_source_travel:
        return source, source, Matrix.Identity(4), {"applicability": "not_straight_directional"}
    scale = _anatomical_character_scale(rig)
    if _uses_repaired_walk_root(role):
        # Stage 1 needs a provisional carriage split in order to repair the
        # periodic body pose. It must not run the former PCA estimator: Stage 2
        # replaces this provisional split from repaired evaluated Hips evidence.
        endpoint = source[0][-1], source[2][-1]
        endpoint_length = math.hypot(*endpoint)
        if endpoint_length < scale * .05:
            raise SelectionError(
                f"{role.code} source {action.name} window {min(frames)}:{max(frames)} "
                "has degenerate provisional endpoint travel"
            )
        direction = endpoint[0] / endpoint_length, endpoint[1] / endpoint_length
        progress = tuple(
            lateral * direction[0] + forward * direction[1]
            for lateral, forward in zip(source[0], source[2])
        )
        frame_evidence: dict[str, Any] = {
            "procedure": "provisional_endpoint_line_for_stage_1_only",
            "direction_lateral_forward": list(direction),
            "progress": list(progress),
            "net_travel_rig_scale": endpoint_length / scale,
            "superseded_by": "repaired_hips_ground_line_root_projection_v1",
        }
    else:
        try:
            estimator = (
                estimate_finite_transition_frame
                if policy.family == "walk_transition"
                else estimate_source_travel_frame
            )
            frame = estimator(tuple(zip(source[0], source[2])), scale, DEFAULT_TRAVEL_FRAME_LIMITS)
        except TravelFrameError as error:
            raise SelectionError(
                f"{role.code} source {action.name} window {min(frames)}:{max(frames)} "
                f"has no stable straight source locomotion frame: {error}"
            ) from error
        direction, progress, frame_evidence = frame.direction, frame.progress, frame.evidence()
    target = _declared_straight_direction(role.code)
    angle = alignment_angle(direction, target)
    cosine, sine = math.cos(angle), math.sin(angle)
    aligned = [[0.0] * len(frames) for _ in range(3)]
    aligned[1] = source[1].copy()
    for index, (lateral, forward) in enumerate(zip(source[0], source[2])):
        aligned[0][index] = cosine * lateral - sine * forward
        aligned[2][index] = sine * lateral + cosine * forward
    projected = [[0.0] * len(frames) for _ in range(3)]
    for index, scalar in enumerate(progress):
        projected[0][index] = target[0] * scalar
        projected[2][index] = target[1] * scalar
    evidence = frame_evidence
    if policy.family == "walk_transition":
        speeds = [0.0] + [
            math.hypot(source[0][index] - source[0][index - 1], source[2][index] - source[2][index - 1])
            for index in range(1, len(frames))
        ]
        peak_speed = max(speeds)
        active_threshold = max(scale * 1e-4, peak_speed * .20)
        active_indices = [index for index, speed in enumerate(speeds) if speed >= active_threshold]
        if not active_indices:
            raise SelectionError(f"{role.code} source {action.name} has no finite active-travel interval")
        idle_at_start = role.code.startswith("i2")
        idle_indices = range(0, min(3, len(frames))) if idle_at_start else range(max(0, len(frames) - 3), len(frames))
        idle_speed_maximum = max(speeds[index] for index in idle_indices)
        idle_limit = max(scale * .01, peak_speed * .45)
        if idle_speed_maximum > idle_limit:
            endpoint = "start" if idle_at_start else "end"
            raise SelectionError(
                f"{role.code} source {action.name} has no near-idle {endpoint} endpoint: "
                f"speed {idle_speed_maximum:.9f} exceeds {idle_limit:.9f}"
            )
        source_heading = _evaluated_body_relative(
            rig, action, frames, "mirror" in role.derivation, rig_basis, True,
        )[1]
        heading_span = max(source_heading) - min(source_heading)
        if heading_span > math.radians(30.0):
            raise SelectionError(
                f"{role.code} source {action.name} has unstable finite facing span "
                f"{math.degrees(heading_span):.6f} degrees"
            )
        evidence["procedure"] = "finite_transition_endpoint_pca_source_progress_v1"
        evidence["sustained_loop_subwindow_estimator_applicable"] = False
        evidence["finite_endpoint_pacing_preserved"] = True
        evidence["finite_context"] = {
            "idle_endpoint": "start" if idle_at_start else "end",
            "idle_endpoint_output_frame": 1 if idle_at_start else len(frames),
            "idle_hysteresis_output_frames": [index + 1 for index in idle_indices],
            "idle_speed_maximum": idle_speed_maximum,
            "idle_speed_limit": idle_limit,
            "peak_horizontal_speed": peak_speed,
            "active_speed_threshold": active_threshold,
            "active_interval_output_frames": [active_indices[0] + 1, active_indices[-1] + 1],
            "source_progress": list(progress),
            "source_progress_endpoint": progress[-1],
            "source_progress_linearised": False,
            "body_heading_samples_rad": source_heading,
            "body_heading_span_rad": heading_span,
            "facing_span_limit_rad": math.radians(30.0),
            "contact_hysteresis": {
                "algorithm": "directional_release_velocity_hysteresis",
                "frames": RELEASE_HYSTERESIS_FRAMES,
                "selected_crossing_is_role_qualified": True,
            },
        }
    evidence.update({
        "applicability": "straight_directional",
        "character_scale": scale,
        "character_scale_procedure": ANATOMICAL_SCALE_PROCEDURE,
        "declared_direction_lateral_forward": list(target),
        "constant_alignment_radians": angle,
        "constant_alignment_degrees": math.degrees(angle),
        "maximum_progress_copy_error": max(
            abs(projected[0][index] * target[0] + projected[2][index] * target[1] - scalar)
            for index, scalar in enumerate(progress)
        ),
        "limits": vars(DEFAULT_TRAVEL_FRAME_LIMITS),
    })
    if policy.family == "walk_transition":
        # Finite entry/exit body facing and endpoint poses are authoritative.
        # Root receives the declared signed scalar carriage, while Hips
        # subtraction consumes the unrotated source carriage exactly once.
        # A rigid body alignment is optional under the contract and is not
        # justified merely by a small source travel-frame offset.
        evidence["body_rigid_alignment_applied"] = False
        evidence["body_rigid_alignment_reason"] = "preserve finite source endpoint facing and pose"
        return projected, source, Matrix.Identity(4), evidence
    return projected, aligned, _constant_alignment_delta(rig, angle), evidence


def _mirror_path(path: str) -> str:
    for left, right in (("Left", "Right"), ("Right", "Left"), ("_l", "_r"), ("_r", "_l")):
        if left in path:
            return path.replace(left, right)
    return path


def role_evaluated_hierarchy_transform(name: str, matrix: Matrix, mirror: bool) -> tuple[str, Matrix]:
    """Express one evaluated hierarchy transform in its output-role frame.

    Spatial derivations reflect both the armature transform and bilateral bone
    identity.  Applying only one leaves the generated Root and the evaluated
    body in incompatible frames during seam, contact, and anatomy assessment.
    """
    if not mirror:
        return name, matrix.copy()
    reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    return _mirror_path(name), reflection @ matrix @ reflection


def _value(path: str, index: int, value: float, derivation: str) -> float:
    if "mirror" in derivation and ((path.endswith(".location") and index == 0) or (path.endswith(".rotation_euler") and index in (1, 2))):
        return -value
    return value


def _curve(bag: Any, path: str, index: int, values: list[float], cyclic: bool, accumulated_root: bool = False) -> None:
    """Create a curve with an explicit repeat policy.

    Root channels represent a per-cycle delta, so their next cycle must retain
    that delta.  Pose channels repeat their pose rather than accumulating it.
    """
    curve = bag.fcurves.new(data_path=path, index=index)
    curve.keyframe_points.add(len(values))
    for frame, value in enumerate(values, 1):
        curve.keyframe_points[frame - 1].co = frame, value
        curve.keyframe_points[frame - 1].interpolation = "LINEAR"
    if cyclic:
        modifier = curve.modifiers.new("CYCLES")
        modifier.mode_before = "REPEAT_OFFSET" if accumulated_root else "REPEAT"
        modifier.mode_after = "REPEAT_OFFSET" if accumulated_root else "REPEAT"


def _new_action(name: str, rig: Any) -> tuple[Any, Any]:
    action = bpy.data.actions.new(name)
    # Output catalogue Actions are intentionally unassigned in the source blend.
    action.use_fake_user = True
    slot = action.slots.new(rig.id_type, rig.name)
    bag = action.layers.new("ANIM-002").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
    return action, bag


def _trajectory(code: str) -> tuple[str, str]:
    if code in {"pl45", "pr45"}: return "zero_translation", "turning"
    if code.startswith(("al", "ar")): return "curved", "turning"
    if code in {"sl", "sr"} or code.startswith(("xl_", "xr_")): return "lateral", "zero_yaw"
    return "longitudinal", "zero_yaw"


def _semantic_yaw(transform: Any, rig_basis: Any) -> float:
    """Extract the Root-up twist from a transform with quaternion/matrix math."""
    # Pose-bone matrices already use the Root pose basis.  The rig rest basis
    # is used for translation diagnostics, not reapplied to a pose transform.
    # Retaining this parameter keeps the transform-space API explicit.
    del rig_basis
    semantic_rotation = transform.to_3x3()
    quaternion = semantic_rotation.to_quaternion().normalized()
    # Project the quaternion vector onto Root-up.  This is a signed twist,
    # unlike subtracting a potentially wrapped Euler channel.
    twist = Quaternion((quaternion.w, 0.0, quaternion.y, 0.0))
    if twist.magnitude <= 1e-8:
        return 0.0
    twist.normalize()
    return 2.0 * math.atan2(twist.y, twist.w)


def _global_z_yaw(transform: Any) -> float:
    """Extract a signed twist around immutable armature/global Z-up."""
    quaternion = transform.to_3x3().to_quaternion().normalized()
    twist = Quaternion((quaternion.w, 0.0, 0.0, quaternion.z))
    if twist.magnitude <= 1e-8:
        return 0.0
    twist.normalize()
    return 2.0 * math.atan2(twist.z, twist.w)


def _root_yaw_matrix(yaw: float, rig_basis: Any) -> Any:
    """Return a promotion correction about immutable armature/global Z-up."""
    del rig_basis
    return Matrix.Rotation(yaw, 4, "Z")


def _root_local_y_delta(rig: Any, theta: float) -> Matrix:
    """Express Root-local-Y rotation in armature transform space."""
    rest = rig.data.bones["Root"].matrix_local
    return rest @ Matrix.Rotation(theta, 4, "Y") @ rest.inverted_safe()


def _armature_global_z_delta(rig: Any, yaw: float) -> Matrix:
    """Express a world-global-Z heading delta in armature transform space."""
    world = rig.matrix_world
    return world.inverted_safe() @ Matrix.Rotation(yaw, 4, "Z") @ world


def _evaluated_body_relative(rig: Any, action: Any, frames: list[int], mirror: bool, rig_basis: Any, global_heading: bool = False) -> tuple[list[Any], list[float]]:
    """Snapshot immutable evaluated source Hips residuals and global-Z yaw."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    try:
        _activate_action(rig, action)
        source_residuals, source_hips_global = [], []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            residual = rig.pose.bones["Root"].matrix.inverted_safe() @ rig.pose.bones["Hips"].matrix
            source_residuals.append(residual)
            # Heading promotion is measured from the evaluated Hips full
            # transform in armature/global Z-up, never its local Euler axes or
            # a Root-relative residual.
            source_hips_global.append(rig.matrix_world @ rig.pose.bones["Hips"].matrix)
        initial = source_residuals[0]
        initial_global = source_hips_global[0]
        result, yaws = [], []
        for residual, hips_global in zip(source_residuals, source_hips_global):
            # Left delta expresses the authored heading change in armature
            # space. Do not apply an inverse inside the opening Hips frame.
            yaw = (
                _global_z_yaw(hips_global @ initial_global.inverted_safe())
                if global_heading else
                _global_z_yaw(residual @ initial.inverted_safe())
            )
            if mirror:
                residual = reflection @ residual @ reflection
                yaw = -yaw
            result.append(residual)
            yaws.append(yaw)
        return result, yaws
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)


def _hips_pose_channels(rig: Any, action: Any, frames: list[int], residuals: list[Any], reference_residual: Any, source_carriage: list[list[float]], root_carriage: list[list[float]], rig_basis: Any) -> dict[tuple[str, int], list[float]]:
    """Materialise Root-relative Hips translation as valid pose-local channels.

    The source Hips translation contains the source carriage.  That carriage is
    promoted to Root, so it must be subtracted before solving Hips' local pose.
    This uses the actual parent pose and rest transforms, rather than assigning
    an armature-space matrix and reading whichever raw channels Blender happens
    to expose.
    """
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    hips = rig.pose.bones["Hips"]
    parent = hips.parent
    if parent is None:
        raise MaterialisationError("Kimodo Source Hips must have a pose parent")
    values = {(f"{HIPS}.location", index): [] for index in range(3)}
    values.update({(f"{HIPS}.rotation_euler", index): [] for index in range(3)})
    try:
        _activate_action(rig, action)
        bpy.context.view_layer.update()
        for frame, residual in zip(frames, residuals):
            bpy.context.scene.frame_set(frame)
            desired = rig.pose.bones["Root"].matrix @ residual
            # In rig space, remove precisely the part of the source Hips path
            # carried by generated Root.  The remainder is only gait-scale
            # local residual (including vertical bob), anchored to reference.
            source_offset = rig_basis @ Vector((source_carriage[0][frame - 1], source_carriage[1][frame - 1], source_carriage[2][frame - 1]))
            root_offset = rig_basis @ Vector((root_carriage[0][frame - 1], root_carriage[1][frame - 1], root_carriage[2][frame - 1]))
            desired.translation = (rig.pose.bones["Root"].matrix @ reference_residual).translation + source_offset - root_offset
            basis = _basis_from_full_transform(rig, "Hips", parent.matrix, desired)
            location = basis.translation
            # The convention was checked before sampling; never serialise an
            # Euler path through a quaternion representation.
            rotation = basis.to_euler(EULER_OUTPUT_MODE)
            for index in range(3):
                values[(f"{HIPS}.location", index)].append(float(location[index]))
                values[(f"{HIPS}.rotation_euler", index)].append(float(rotation[index]))
        return values
    finally:
        bpy.context.scene.frame_set(previous_frame)
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _matrix_error(actual: Any, expected: Any) -> float:
    return max(
        abs(actual[row][column] - expected[row][column])
        for row in range(4)
        for column in range(4)
    )


def _frozen_matrix(matrix: Any) -> tuple[tuple[float, ...], ...]:
    """Copy an evaluated matrix into immutable, Blender-independent evidence."""
    return tuple(tuple(float(matrix[row][column]) for column in range(4)) for row in range(4))


def _thaw_matrix(values: tuple[tuple[float, ...], ...]) -> Matrix:
    return Matrix(values)


def _motion_bone_names(rig: Any) -> tuple[str, ...]:
    """Return the visible motion hierarchy consumed by downstream retargeting.

    The source-to-Female processor defines ``*_RET`` and ``*_LOOK`` bones as
    authored helpers around the deforming motion hierarchy. ``use_deform``
    cannot express every such distinction on the Kimodo rig, so the shared
    semantic naming classifier is also required.
    """
    names = tuple(sorted(
        bone.name
        for bone in rig.data.bones
        if is_anatomical_motion_bone(bone.name, bone.use_deform)
    ))
    required = {"Hips", "Chest", "Head", "LeftFoot", "RightFoot"}
    if not required <= set(names):
        raise MaterialisationError(f"Kimodo motion hierarchy lacks required bones: {sorted(required - set(names))}")
    return names


def _anatomical_character_scale(rig: Any) -> float:
    """Measure one authoritative scale from the retargeted motion hierarchy."""
    try:
        return anatomical_character_scale(
            (
                bone.name,
                (bone.tail_local - bone.head_local).length,
                bone.use_deform,
            )
            for bone in rig.data.bones
        )
    except AnatomicalScaleError as error:
        raise MaterialisationError(f"Kimodo Source {error}") from error


def _basis_from_full_transform(rig: Any, bone_name: str, parent_transform: Matrix, transform: Matrix) -> Matrix:
    """Express frozen evaluated full transforms in Blender's pose-local basis."""
    bone = rig.pose.bones[bone_name]
    parent = bone.parent
    if parent is None:
        raise MaterialisationError(f"pivot oracle target {bone_name} has no pose parent")
    return bone.bone.convert_local_to_pose(
        transform,
        bone.bone.matrix_local,
        parent_matrix=parent_transform,
        parent_matrix_local=parent.bone.matrix_local,
        invert=True,
    )


def _capture_pivot_transform_oracle(rig: Any, action: Any, frames: list[int]) -> dict[str, Any]:
    """Capture original-mode evaluated Segment-2 transforms before any mutation.

    This is deliberately full transform evidence rather than the Hips residual
    used by the writer.  Validation must not be able to turn writer state into
    its own expectation, or reread a subsequently corrupted source Action.
    """
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    animated_bones = {
        path.split('"', 2)[1]
        for path, _ in _curves(action)
        if path.startswith('pose.bones["') and not path.startswith(ROOT)
    }
    required = {"Hips", "Chest", "Head", "LeftFoot", "RightFoot"}
    if not required <= animated_bones:
        raise MaterialisationError(
            f"{action.name} does not expose required pivot oracle targets: {sorted(required - animated_bones)}"
        )
    # Capture the entire non-Root hierarchy so expected full transforms can be
    # reconstructed from immutable parent-relative source transforms. The five
    # anatomy landmarks below are the mandatory assertion targets.
    source_bones = set(_motion_bone_names(rig))
    try:
        _activate_action(rig, action)
        samples = []
        initial_hips_global = None
        initial_hips_residual = None
        for output_frame, source_frame in enumerate(frames, 1):
            bpy.context.scene.frame_set(source_frame)
            root = rig.pose.bones["Root"].matrix.copy()
            hips = rig.pose.bones["Hips"].matrix.copy()
            hips_global = rig.matrix_world @ hips
            if initial_hips_global is None:
                initial_hips_global = hips_global.copy()
            hips_residual = root.inverted_safe() @ hips
            if initial_hips_residual is None:
                initial_hips_residual = hips_residual.copy()
            assert initial_hips_global is not None
            assert initial_hips_residual is not None
            samples.append({
                "source_frame": source_frame,
                "output_frame": output_frame,
                "root": _frozen_matrix(root),
                "bones": {name: _frozen_matrix(rig.pose.bones[name].matrix) for name in sorted(source_bones)},
                # This is captured with the source hierarchy, before any
                # generated Action exists.  It supplies only the declared
                # source pacing for Root's bounded pivot unit.
                "global_z_heading": float(_global_z_yaw(hips_global @ initial_hips_global.inverted_safe())),
                "root_relative_global_z_heading": float(
                    _global_z_yaw(hips_residual @ initial_hips_residual.inverted_safe())
                ),
                "rotation_modes": {name: rig.pose.bones[name].rotation_mode for name in sorted(source_bones | {"Root"})},
            })
        return {
            "action_name": action.name,
            "action_signature": _action_signature(action),
            "source_bones": tuple(sorted(source_bones)),
            "parents": {name: rig.pose.bones[name].parent.name if rig.pose.bones[name].parent else "Root" for name in source_bones},
            "samples": tuple(samples),
        }
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _assert_pivot_oracle_source_intact(rig: Any, action: Any, oracle: dict[str, Any]) -> None:
    """Reject a source or representation changed after its oracle was captured."""
    if action.name != oracle["action_name"] or _action_signature(action) != oracle["action_signature"]:
        raise MaterialisationError("pivot source Action changed after immutable transform oracle capture")
    modes = oracle["samples"][0]["rotation_modes"]
    if any(rig.pose.bones[name].rotation_mode != mode for name, mode in modes.items()):
        raise MaterialisationError("pivot source rotation mode changed after immutable transform oracle capture")


def _validate_pivot_non_root_targets(
    rig: Any,
    output: Any,
    oracle: dict[str, Any],
    reference_oracle: dict[str, Any],
    mirror: bool,
    role_code: str,
) -> None:
    """Compare repeated evaluated output with frozen hierarchy expectations.

    This intentionally validates ``PoseBone.matrix``, not ``matrix_basis``.
    A basis copied from the source is only correct below the source Root; it is
    wrong as soon as the generated Root has a non-zero pivot heading.
    """
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    source_bones = set(oracle["source_bones"])
    target_bones = {_mirror_path(name) if mirror else name for name in source_bones}
    required = {"Hips", "Chest", "Head", "LeftFoot", "RightFoot"}
    if not required <= target_bones:
        raise MaterialisationError(
            f"{oracle['action_name']} does not expose required pivot oracle targets: {sorted(required - target_bones)}"
        )
    try:
        reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
        source_samples = oracle["samples"]
        if role_code not in {"pr45", "pl45"}:
            raise MaterialisationError(f"unsupported pivot oracle role: {role_code}")
        sign = pivot_root_yaw_sign(role_code)
        # This expected unit is reconstructed exclusively from immutable source
        # headings, never from generated Root keys or F-curves.
        unit_yaw = pivot_unit_yaw(
            role_code,
            [(-1.0 if mirror else 1.0) * float(sample["global_z_heading"]) for sample in source_samples],
        )
        root_rest = rig.data.bones["Root"].matrix_local.copy()
        endpoint = sign * math.pi / 4.0
        reference_hips = _thaw_matrix(reference_oracle["samples"][0]["bones"]["Hips"])
        source_initial_hips = _thaw_matrix(source_samples[0]["bones"]["Hips"])
        if mirror:
            reference_hips = reflection @ reference_hips @ reflection
            source_initial_hips = reflection @ source_initial_hips @ reflection
        anchor = Matrix.Translation(reference_hips.translation - source_initial_hips.translation)
        # Validate every unique repeat sample. In particular these include
        # F51, F75, and F99 for a 50-frame unit. This detects ordinary Root
        # repetition, double body heading, copied local bases below a yawed
        # Root, and a held/snap body after the first cycle.
        for cycle in (0, 1):
            # Blender's keyed endpoint is the cycle boundary. The unique
            # repeated samples begin at source sample two: F51, F75, and F99
            # for this 50-frame action. The boundary itself is covered by the
            # independent Root and first-unit seam checks.
            for sample in source_samples if cycle == 0 else source_samples[1:]:
                output_frame = sample["output_frame"] + cycle * (len(source_samples) - 1)
                _activate_action(rig, output)
                bpy.context.scene.frame_set(output_frame)
                # The declared Root transfer is applied to every source
                # descendant.  This is deliberately not a source-heading
                # inverse: inverse-heading would make a plausible Root curve
                # while holding the body in place and sliding the turn.
                if not math.isfinite(unit_yaw[sample["output_frame"] - 1]):
                    raise MaterialisationError("pivot oracle contains a non-finite declared unit yaw")
                total_theta = cycle * endpoint + unit_yaw[sample["output_frame"] - 1]
                declared_output_root = root_rest @ Matrix.Rotation(total_theta, 4, "Y")
                cycle_delta = _root_local_y_delta(rig, cycle * endpoint)
                for source_name in required:
                    target_name = _mirror_path(source_name) if mirror else source_name
                    expected = _thaw_matrix(sample["bones"][source_name])
                    if mirror:
                        expected = reflection @ expected @ reflection
                    expected = cycle_delta @ anchor @ expected
                    actual = rig.pose.bones[target_name].matrix
                    error = _matrix_error(actual, expected)
                    if error > 5e-2:
                        raise MaterialisationError(
                            f"{output.name} does not preserve immutable source transform for {target_name} at frame {output_frame} ({error:.6f})"
                        )
                    # Explicit root-relative trajectory evidence makes an
                    # erroneous Root/body counter-composition diagnosable.
                    expected_relative = declared_output_root.inverted_safe() @ expected
                    actual_relative = rig.pose.bones["Root"].matrix.inverted_safe() @ actual
                    if _matrix_error(actual_relative, expected_relative) > 5e-2:
                        raise MaterialisationError(
                            f"{output.name} does not preserve immutable Root-relative trajectory for {target_name} at frame {output_frame}"
                        )
                    if source_name in {"Hips", "Chest", "Head"}:
                        opening = _thaw_matrix(source_samples[0]["bones"][source_name])
                        if mirror:
                            opening = reflection @ opening @ reflection
                        opening = cycle_delta @ anchor @ opening
                        expected_heading = _global_z_yaw(expected @ opening.inverted_safe())
                        actual_heading = _global_z_yaw(actual @ opening.inverted_safe())
                        if abs(actual_heading - expected_heading) > 5e-2:
                            raise MaterialisationError(
                                f"{output.name} does not preserve source-paced semantic body turn for {target_name} at frame {output_frame}"
                            )
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _validate_role_transform_oracle(
    rig: Any, output: Any, role: Any, oracle: dict[str, Any], mirror: bool, tolerance: float,
) -> dict[str, Any]:
    """Assert the declared Root transfer against immutable full transforms.

    This is the shared materialisation oracle.  It compares evaluated matrices
    for the anatomy landmarks rather than Hips-relative channels, so both a
    counter-yawed pivot and a planted-foot walk slide are observable.
    """
    if _uses_bounded_loop_repair(role):
        expected_rows = oracle.get("repaired_transform_rows")
        if not isinstance(expected_rows, tuple) or len(expected_rows) != len(oracle["samples"]):
            raise MaterialisationError(f"{output.name} lacks its immutable repaired full-transform oracle")
        animation = rig.animation_data_create()
        previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
        maximum = maximum_translation = maximum_rotation = 0.0
        maximum_separation_error = maximum_horizontal_separation = 0.0
        maximum_expected_horizontal_separation = 0.0
        hips_translation_samples = 0
        try:
            _activate_action(rig, output)
            expected_root_translation = oracle.get("repaired_root_translation")
            if not isinstance(expected_root_translation, tuple) or len(expected_root_translation) != len(expected_rows):
                raise MaterialisationError(f"{output.name} lacks its immutable repaired Root trajectory oracle")
            output_curves = _curves(output)
            maximum_root_projection_error = max(
                abs(
                    float(output_curves[(f"{ROOT}.location", axis)].evaluate(frame))
                    - float(expected_root_translation[frame - 1][axis])
                )
                for frame in range(1, len(expected_rows) + 1)
                for axis in range(3)
            )
            root_projection_tolerance = max(1e-8, min(tolerance, 1e-6))
            if maximum_root_projection_error > root_projection_tolerance:
                raise MaterialisationError(
                    f"{output.name} violates immutable repaired Root trajectory oracle "
                    f"({maximum_root_projection_error:.9f})"
                )
            up_world = (
                rig.matrix_world.to_3x3()
                @ rig.data.bones["Root"].matrix_local.to_3x3()
                @ Vector((0.0, 1.0, 0.0))
            ).normalized()
            for frame, expected_row in enumerate(expected_rows, 1):
                bpy.context.scene.frame_set(frame)
                output_root = rig.pose.bones["Root"].matrix.copy()
                expected_hips = expected_row["Hips"]
                for name, expected in expected_row.items():
                    actual = rig.pose.bones[name].matrix
                    matrix_error = _matrix_error(actual, expected)
                    translation_error = (actual.translation - expected.translation).length
                    rotation_error = _shortest_rotation_difference(
                        actual.to_quaternion(), expected.to_quaternion(),
                    ).angle
                    maximum = max(maximum, matrix_error)
                    maximum_translation = max(maximum_translation, translation_error)
                    maximum_rotation = max(maximum_rotation, rotation_error)
                    if name == "Hips":
                        hips_translation_samples += 1
                    if max(matrix_error, translation_error, rotation_error) > tolerance:
                        raise MaterialisationError(
                            f"{output.name} violates immutable repaired full-transform oracle for {name} "
                            f"at output frame {frame} (matrix={matrix_error:.8f}, "
                            f"translation={translation_error:.8f}, rotation={rotation_error:.8f})"
                        )
                actual_offset = rig.matrix_world.to_3x3() @ (
                    rig.pose.bones["Hips"].matrix.translation - output_root.translation
                )
                expected_offset = rig.matrix_world.to_3x3() @ (
                    expected_hips.translation - output_root.translation
                )
                actual_horizontal = actual_offset - up_world * actual_offset.dot(up_world)
                expected_horizontal = expected_offset - up_world * expected_offset.dot(up_world)
                maximum_horizontal_separation = max(maximum_horizontal_separation, actual_horizontal.length)
                maximum_expected_horizontal_separation = max(
                    maximum_expected_horizontal_separation, expected_horizontal.length,
                )
                maximum_separation_error = max(
                    maximum_separation_error, (actual_horizontal - expected_horizontal).length,
                )
            if hips_translation_samples != len(expected_rows):
                raise MaterialisationError(
                    f"{output.name} repaired oracle did not measure Hips translation at every frame"
                )
            return {
                "maximum_matrix_error": maximum,
                "maximum_translation_error": maximum_translation,
                "maximum_rotation_error_rad": maximum_rotation,
                "maximum_root_hips_horizontal_separation": maximum_horizontal_separation,
                "maximum_expected_root_hips_horizontal_separation": maximum_expected_horizontal_separation,
                "maximum_root_hips_horizontal_separation_error": maximum_separation_error,
                "maximum_root_projection_error": maximum_root_projection_error,
                "root_projection_tolerance": root_projection_tolerance,
                "tolerance": tolerance,
                "compared_source_frames": len(expected_rows),
                "hips_translation_samples": hips_translation_samples,
                "replacement_oracle": "frozen_repaired_periodic_full_transform_target",
                "pivot_endpoint_recurrence_mapping": None,
            }
        finally:
            if previous_action is not None:
                _activate_action(rig, previous_action)
            else:
                animation.action = None
            bpy.context.scene.frame_set(previous_frame)
            bpy.context.view_layer.update()
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    required = ("Hips", "Chest", "Head", "LeftFoot", "RightFoot")
    maximum = 0.0
    maximum_translation = 0.0
    maximum_rotation = 0.0
    maximum_separation_error = 0.0
    maximum_horizontal_separation = 0.0
    maximum_expected_horizontal_separation = 0.0
    try:
        _activate_action(rig, output)
        reference_oracle = oracle["reference_oracle"]
        reference_hips = _thaw_matrix(reference_oracle["samples"][0]["bones"]["Hips"])
        source_initial_hips = _thaw_matrix(oracle["samples"][0]["bones"]["Hips"])
        if mirror:
            reference_hips = reflection @ reference_hips @ reflection
            source_initial_hips = reflection @ source_initial_hips @ reflection
        anchor = Matrix.Translation(reference_hips.translation - source_initial_hips.translation)
        full_transform_oracle = role_projection_policy(role).reconstruct_full_hierarchy or (
            not mirror and role_projection_policy(role).family == "walk"
        )
        alignment = _thaw_matrix(oracle.get("constant_alignment", _frozen_matrix(Matrix.Identity(4))))
        alignment_origin = source_initial_hips.translation.copy()
        compared_frames = 0
        for sample in oracle["samples"]:
            compared_frames += 1
            bpy.context.scene.frame_set(sample["output_frame"])
            output_root = rig.pose.bones["Root"].matrix.copy()
            source_root = _thaw_matrix(sample["root"])
            if mirror:
                source_root = reflection @ source_root @ reflection
            # Pivot yaw extraction is a hierarchy reparameterisation. The
            # re-anchored immutable source pose remains the final first-cycle
            # target; Root ownership is handled solely by the parent/rest local
            # solve. A time-varying left correction would move source-planted
            # feet even when all instantaneous relative matrices still agree.
            transfer = Matrix.Identity(4)
            expected_hips = None
            expected_bones: dict[str, Matrix] = {}
            actual_bones: dict[str, Matrix] = {}
            for source_name in required:
                target_name = _mirror_path(source_name) if mirror else source_name
                expected = _thaw_matrix(sample["bones"][source_name])
                if mirror:
                    expected = reflection @ expected @ reflection
                expected = _align_about(expected, alignment, alignment_origin)
                expected = transfer @ anchor @ expected
                actual = rig.pose.bones[target_name].matrix
                expected_bones[source_name] = expected
                actual_bones[source_name] = actual.copy()
                if full_transform_oracle:
                    error = _matrix_error(actual, expected)
                    translation_error = (actual.translation - expected.translation).length
                    rotation_error = _shortest_rotation_difference(actual.to_quaternion(), expected.to_quaternion()).angle
                elif source_name == "Hips":
                    source_root = _thaw_matrix(sample["root"])
                    if mirror:
                        source_root = reflection @ source_root @ reflection
                    expected_relative = source_root.inverted_safe() @ expected
                    if role_projection_policy(role).promote_global_z_yaw:
                        promoted_yaw = (-1.0 if mirror else 1.0) * float(sample["root_relative_global_z_heading"])
                        expected_relative = _root_yaw_matrix(-promoted_yaw, rig_basis=None) @ expected_relative
                    actual_relative = output_root.inverted_safe() @ actual
                    hips_rotation_error = _shortest_rotation_difference(actual_relative.to_quaternion(), expected_relative.to_quaternion()).angle
                    error = 0.0
                    translation_error = 0.0
                    rotation_error = hips_rotation_error
                else:
                    expected_relative = expected_bones["Hips"].inverted_safe() @ expected
                    actual_relative = actual_bones["Hips"].inverted_safe() @ actual
                    error = _matrix_error(actual_relative, expected_relative)
                    translation_error = (actual_relative.translation - expected_relative.translation).length
                    rotation_error = _shortest_rotation_difference(actual_relative.to_quaternion(), expected_relative.to_quaternion()).angle
                maximum = max(maximum, error)
                maximum_translation = max(maximum_translation, translation_error)
                maximum_rotation = max(maximum_rotation, rotation_error)
                if source_name == "Hips":
                    expected_hips = expected
                relative_oracle = not full_transform_oracle
                effective_error = rotation_error if relative_oracle and source_name == "Hips" else error
                effective_tolerance = 2e-2 if relative_oracle else tolerance
                if effective_error > effective_tolerance:
                        raise MaterialisationError(
                            f"{output.name} violates immutable full-transform oracle for {target_name} "
                            f"at source/output {sample['source_frame']}/{sample['output_frame']} "
                            f"(matrix={error:.8f}, rotation={rotation_error:.8f})"
                        )
            assert expected_hips is not None
            world_up = (rig.matrix_world.to_3x3() @ rig.data.bones["Root"].matrix_local.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
            actual_offset = rig.matrix_world.to_3x3() @ (rig.pose.bones["Hips"].matrix.translation - output_root.translation)
            expected_offset = rig.matrix_world.to_3x3() @ (expected_hips.translation - output_root.translation)
            actual_horizontal = actual_offset - world_up * actual_offset.dot(world_up)
            expected_horizontal = expected_offset - world_up * expected_offset.dot(world_up)
            maximum_horizontal_separation = max(maximum_horizontal_separation, actual_horizontal.length)
            maximum_expected_horizontal_separation = max(maximum_expected_horizontal_separation, expected_horizontal.length)
            maximum_separation_error = max(maximum_separation_error, (actual_horizontal - expected_horizontal).length)
        return {
            "maximum_matrix_error": maximum,
            "maximum_translation_error": maximum_translation,
            "maximum_rotation_error_rad": maximum_rotation,
            "maximum_root_hips_horizontal_separation": maximum_horizontal_separation,
            "maximum_expected_root_hips_horizontal_separation": maximum_expected_horizontal_separation,
            "maximum_root_hips_horizontal_separation_error": maximum_separation_error,
            "tolerance": 2e-2 if not full_transform_oracle else tolerance,
            "compared_source_frames": compared_frames,
            "pivot_endpoint_recurrence_mapping": None,
        }
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _validate_pivot_kinematics(
    rig: Any, output: Any, oracle: dict[str, Any], mirror: bool, role_code: str, tolerance: float = 5e-4,
) -> dict[str, Any]:
    """Validate pivot stepping in body, chain, plant, and repeat spaces.

    Unlike the former writer-derived matrix target, this oracle starts from the
    re-anchored immutable source pose itself. Root yaw is therefore tested as a
    hierarchy ownership change, while source world foot carriage remains the
    independent kinematic truth.
    """
    reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    reference = oracle["reference_oracle"]["samples"][0]
    source_samples = oracle["samples"]
    reference_hips = _thaw_matrix(reference["bones"]["Hips"])
    source_initial_hips = _thaw_matrix(source_samples[0]["bones"]["Hips"])
    if mirror:
        reference_hips = reflection @ reference_hips @ reflection
        source_initial_hips = reflection @ source_initial_hips @ reflection
    anchor = Matrix.Translation(reference_hips.translation - source_initial_hips.translation)
    chains = (
        ("LeftLeg", "Hips"), ("LeftShin", "LeftLeg"), ("LeftFoot", "LeftShin"),
        ("RightLeg", "Hips"), ("RightShin", "RightLeg"), ("RightFoot", "RightShin"),
    )
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    expected_rows: list[dict[str, Matrix]] = []
    actual_rows: list[dict[str, Matrix]] = []
    maximum_body_foot = maximum_hips_foot = maximum_chain = 0.0
    maximum_separation = maximum_expected_separation = 0.0
    maximum_body_root_heading_difference = 0.0
    try:
        for sample in source_samples:
            expected: dict[str, Matrix] = {}
            for target_name in ("Root", "Hips", "Chest", "Head", "LeftLeg", "LeftShin", "LeftFoot", "RightLeg", "RightShin", "RightFoot"):
                source_name = _mirror_path(target_name) if mirror else target_name
                source_matrix = _thaw_matrix(sample["root"] if source_name == "Root" else sample["bones"][source_name])
                expected[target_name] = anchor @ (reflection @ source_matrix @ reflection if mirror else source_matrix)
            expected_rows.append(expected)
        _activate_action(rig, output)
        for frame in range(1, len(source_samples) + 1):
            bpy.context.scene.frame_set(frame)
            actual_rows.append({name: rig.pose.bones[name].matrix.copy() for name in expected_rows[frame - 1]})
        initial_root, initial_hips = actual_rows[0]["Root"], actual_rows[0]["Hips"]
        up = (rig.matrix_world.to_3x3() @ rig.data.bones["Root"].matrix_local.to_3x3() @ Vector((0.0, 1.0, 0.0))).normalized()
        for expected, actual in zip(expected_rows, actual_rows):
            expected_heading = _global_z_yaw(expected["Hips"])
            actual_heading = _global_z_yaw(actual["Hips"])
            expected_heading_inverse = Matrix.Rotation(-expected_heading, 4, "Z")
            actual_heading_inverse = Matrix.Rotation(-actual_heading, 4, "Z")
            for foot in ("LeftFoot", "RightFoot"):
                expected_body = expected_heading_inverse @ (expected[foot].translation - expected["Hips"].translation)
                actual_body = actual_heading_inverse @ (actual[foot].translation - actual["Hips"].translation)
                maximum_body_foot = max(maximum_body_foot, (actual_body - expected_body).length)
                maximum_hips_foot = max(maximum_hips_foot, _matrix_error(actual["Hips"].inverted_safe() @ actual[foot], expected["Hips"].inverted_safe() @ expected[foot]))
            for child, parent in chains:
                maximum_chain = max(maximum_chain, _matrix_error(actual[parent].inverted_safe() @ actual[child], expected[parent].inverted_safe() @ expected[child]))
            root_heading = _semantic_yaw(initial_root.inverted_safe() @ actual["Root"], None)
            body_heading = _global_z_yaw(actual["Hips"] @ initial_hips.inverted_safe())
            maximum_body_root_heading_difference = max(maximum_body_root_heading_difference, abs(body_heading - root_heading))
            offset = rig.matrix_world.to_3x3() @ (actual["Hips"].translation - actual["Root"].translation)
            expected_offset = rig.matrix_world.to_3x3() @ (expected["Hips"].translation - actual["Root"].translation)
            maximum_separation = max(maximum_separation, (offset - up * offset.dot(up)).length)
            maximum_expected_separation = max(maximum_expected_separation, (expected_offset - up * expected_offset.dot(up)).length)

        feet: dict[str, Any] = {}
        for foot in ("LeftFoot", "RightFoot"):
            expected_velocity = [b[foot].translation - a[foot].translation for a, b in zip(expected_rows, expected_rows[1:])]
            actual_velocity = [b[foot].translation - a[foot].translation for a, b in zip(actual_rows, actual_rows[1:])]
            source_plants = [index + 2 for index, velocity in enumerate(expected_velocity) if velocity.length <= .01]
            velocity_error = max((actual - expected).length for actual, expected in zip(actual_velocity, expected_velocity))
            height_error = max(abs((actual[foot].translation - expected[foot].translation).dot(up)) for actual, expected in zip(actual_rows, expected_rows))
            feet[foot] = {"source_plant_frames": source_plants, "maximum_velocity_error": velocity_error, "maximum_lift_height_error": height_error}
            if velocity_error > tolerance or height_error > tolerance:
                raise MaterialisationError(f"{output.name} does not preserve source {foot} plant/lift timing")
        if max(maximum_body_foot, maximum_hips_foot, maximum_chain) > tolerance:
            raise MaterialisationError(f"{output.name} does not preserve source pivot leg/body kinematics")
        if maximum_separation > maximum_expected_separation + tolerance:
            raise MaterialisationError(f"{output.name} introduces pivot Root/Hips orbit")
        if maximum_body_root_heading_difference > 2e-2:
            raise MaterialisationError(f"{output.name} Root and body semantic turns disagree")

        repeat_rows = []
        endpoint = pivot_root_yaw_sign(role_code) * math.pi / 4.0
        for frame in (51, 75, 99):
            source_index = (frame - 1) % (len(source_samples) - 1)
            cycle = (frame - 1) // (len(source_samples) - 1)
            if source_index == 0 and frame > 1:
                source_index = len(source_samples) - 1
                cycle -= 1
            bpy.context.scene.frame_set(frame)
            cycle_delta = _root_local_y_delta(rig, cycle * endpoint)
            if source_index == len(source_samples) - 1:
                expected = _root_local_y_delta(rig, (cycle + 1) * endpoint) @ expected_rows[0]["Hips"]
            else:
                expected = cycle_delta @ expected_rows[source_index]["Hips"]
            actual = rig.pose.bones["Hips"].matrix
            error = _matrix_error(actual, expected)
            if error > 5e-2:
                raise MaterialisationError(f"{output.name} repeat pivot kinematics diverge at frame {frame}")
            repeat_rows.append({"frame": frame, "matrix_error": error})
        return {
            "contract": "reanchored_source_pivot_kinematics",
            "mapped_frames": len(source_samples),
            "maximum_body_heading_frame_foot_error": maximum_body_foot,
            "maximum_hips_relative_foot_error": maximum_hips_foot,
            "maximum_leg_chain_error": maximum_chain,
            "maximum_root_hips_horizontal_separation": maximum_separation,
            "maximum_expected_root_hips_horizontal_separation": maximum_expected_separation,
            "maximum_body_root_heading_difference_rad": maximum_body_root_heading_difference,
            "feet": feet,
            "repeat_samples": repeat_rows,
            "tolerance": tolerance,
        }
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _full_transform_channels(
    rig: Any,
    role: Any,
    oracle: dict[str, Any],
    mirror: bool,
    root: list[list[float]],
    yaw: list[float],
) -> dict[tuple[str, int], list[float]]:
    """Solve the non-Root hierarchy from immutable evaluated full transforms.

    First align the immutable source opening Hips transform to the reference
    Hips transform. This removes Kimodo's arbitrary armature-origin offset
    without changing any frame-to-frame source motion. Translation ownership
    then changes only the Root/Hips split. Pivots additionally replace source
    heading with the declared Root heading about that co-located anchor.
    """
    reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    values: dict[tuple[str, int], list[float]] = {}
    source_names = set(oracle["source_bones"])
    reference_oracle = oracle["reference_oracle"]
    reference_hips = _thaw_matrix(reference_oracle["samples"][0]["bones"]["Hips"])
    source_initial_hips = _thaw_matrix(oracle["samples"][0]["bones"]["Hips"])
    if mirror:
        reference_hips = reflection @ reference_hips @ reflection
        source_initial_hips = reflection @ source_initial_hips @ reflection
    # Re-anchor only the arbitrary opening carriage origin. Rotating by the
    # opening Hips basis would rotate source translation away from the Root
    # policy projection and recreate family-dependent Root/body separation.
    anchor = Matrix.Translation(reference_hips.translation - source_initial_hips.translation)
    ordered_sources = sorted(
        source_names,
        key=lambda name: sum(1 for parent in _ancestor_names(rig.pose.bones[name]) if parent in source_names),
    )
    for source_name in ordered_sources:
        target_name = _mirror_path(source_name) if mirror else source_name
        target = rig.pose.bones[target_name]
        location_key = (f'pose.bones["{target_name}"].location', 0)
        rotation_property = ".rotation_quaternion" if target.rotation_mode == "QUATERNION" else ".rotation_euler"
        rotation_count = 4 if target.rotation_mode == "QUATERNION" else 3
        rotation_key = (f'pose.bones["{target_name}"]{rotation_property}', 0)
        scale_key = (f'pose.bones["{target_name}"].scale', 0)
        for index in range(3):
            values[(location_key[0], index)] = []
            values[(scale_key[0], index)] = []
        for index in range(rotation_count):
            values[(rotation_key[0], index)] = []
        previous_rotation = None
        for sample_index, (sample, root_yaw) in enumerate(zip(oracle["samples"], yaw)):
            desired = _thaw_matrix(sample["bones"][source_name])
            source_root = _thaw_matrix(sample["root"])
            if mirror:
                desired = reflection @ desired @ reflection
            if mirror:
                source_root = reflection @ source_root @ reflection
            root_location = Vector((root[0][sample_index], root[1][sample_index], root[2][sample_index]))
            generated_root = (
                rig.data.bones["Root"].matrix_local
                @ Matrix.Translation(root_location)
                @ Matrix.Rotation(root_yaw, 4, "Y")
            )
            desired = anchor @ desired
            parent_name = oracle["parents"][source_name]
            if parent_name == "Root":
                parent_transform = generated_root
            else:
                parent_source = _mirror_path(parent_name) if mirror else parent_name
                parent_desired = _thaw_matrix(sample["bones"][parent_name])
                if mirror:
                    parent_desired = reflection @ parent_desired @ reflection
                parent_desired = anchor @ parent_desired
                parent_transform = parent_desired
                if parent_source != target.parent.name:
                    raise MaterialisationError(f"pivot mirror hierarchy mismatch: {target_name} parent is not {parent_source}")
            basis = _basis_from_full_transform(rig, target_name, parent_transform, desired)
            location, rotation, scale = basis.decompose()
            rotation_values = rotation if target.rotation_mode == "QUATERNION" else rotation.to_euler(EULER_OUTPUT_MODE, previous_rotation)
            if target.rotation_mode != "QUATERNION":
                previous_rotation = rotation_values.copy()
            for index in range(3):
                values[(location_key[0], index)].append(float(location[index]))
                values[(scale_key[0], index)].append(float(scale[index]))
            for index in range(rotation_count):
                values[(rotation_key[0], index)].append(float(rotation_values[index]))
    return values


def _repair_generated_non_root_channels(
    rig: Any, action: Any, sample_count: int,
    *, window_frames: int | None = None, blend_profile: str = "quadratic",
) -> tuple[dict[tuple[str, int], list[float]], dict[str, Any]]:
    """Repair evaluated periodic pose, then solve it through real parent/rest transforms."""
    frames = list(range(1, sample_count + 1))
    relative_rows = _loop_relative_samples(rig, action, frames)
    _, legacy_full_transform = _repair_periodic_pose_rows(
        relative_rows, _anatomical_character_scale(rig),
    )
    repaired_rows, evidence = _repair_periodic_pose_rows(
        relative_rows, _anatomical_character_scale(rig),
        window_frames=window_frames, blend_profile=blend_profile,
        direct_safety_contract=True, rig=rig,
    )
    evidence["legacy_full_transform_diagnostic"] = {
        "admission": "diagnostic_only",
        "window_frames": legacy_full_transform["windows"]["beginning_frames"],
        "blend_profile": legacy_full_transform["blend_profile"],
        "metrics": legacy_full_transform["metrics"],
        "failures": legacy_full_transform["failures"],
        "repair_cost": legacy_full_transform["repair_cost"],
    }
    if not evidence["accepted"]:
        worst = sorted(
            (
                (
                    max(
                        (relative_rows[-1][name].translation - relative_rows[0][name].translation).length,
                        _shortest_rotation_difference(
                            relative_rows[0][name].to_quaternion(), relative_rows[-1][name].to_quaternion(),
                        ).angle,
                    ),
                    name,
                )
                for name in relative_rows[0]
            ),
            reverse=True,
        )[:5]
        raise MaterialisationError(
            f"{action.name} required loop repair is unsafe: {evidence['failures']}; worst_endpoint_bones={worst}"
        )
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        roots = []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            roots.append(rig.pose.bones["Root"].matrix.copy())
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
    desired_rows = [
        {name: root @ relative for name, relative in row.items()}
        for root, row in zip(roots, repaired_rows)
    ]
    names = set(desired_rows[0])
    ordered = sorted(names, key=lambda name: sum(1 for parent in _ancestor_names(rig.pose.bones[name]) if parent in names))
    values: dict[tuple[str, int], list[float]] = {}
    for name in ordered:
        target = rig.pose.bones[name]
        location_path = f'pose.bones["{name}"].location'
        rotation_path = f'pose.bones["{name}"].rotation_quaternion' if target.rotation_mode == "QUATERNION" else f'pose.bones["{name}"].rotation_euler'
        scale_path = f'pose.bones["{name}"].scale'
        rotation_count = 4 if target.rotation_mode == "QUATERNION" else 3
        for index in range(3):
            values[(location_path, index)] = []
            values[(scale_path, index)] = []
        for index in range(rotation_count):
            values[(rotation_path, index)] = []
        previous_rotation = None
        for frame_index, desired in enumerate(desired_rows):
            parent = target.parent
            if parent is None:
                raise MaterialisationError(f"periodic non-Root motion bone {name} has no parent")
            parent_transform = roots[frame_index] if parent.name == "Root" else desired[parent.name]
            basis = _basis_from_full_transform(rig, name, parent_transform, desired[name])
            location, rotation, scale = basis.decompose()
            rotation_values = rotation if target.rotation_mode == "QUATERNION" else rotation.to_euler(EULER_OUTPUT_MODE, previous_rotation)
            if target.rotation_mode != "QUATERNION":
                previous_rotation = rotation_values.copy()
            for index in range(3):
                values[(location_path, index)].append(float(location[index]))
                values[(scale_path, index)].append(float(scale[index]))
            for index in range(rotation_count):
                values[(rotation_path, index)].append(float(rotation_values[index]))
    evidence["repaired_evaluated_hips_origins_armature"] = [list(row["Hips"].translation) for row in desired_rows]
    evidence["source_carriage_separated"] = True
    evidence["root_projection_stage"] = "pending_family_specific_repaired_hips_projection"
    evidence["_frozen_repaired_relative_rows"] = repaired_rows
    evidence["_frozen_repaired_armature_rows"] = desired_rows
    return values, evidence


def _channels_from_armature_rows(
    rig: Any, roots: Sequence[Matrix], desired_rows: Sequence[Mapping[str, Matrix]],
) -> dict[tuple[str, int], list[float]]:
    """Solve complete non-Root armature transforms through the real hierarchy."""
    names = set(desired_rows[0])
    ordered = sorted(names, key=lambda name: sum(1 for parent in _ancestor_names(rig.pose.bones[name]) if parent in names))
    values: dict[tuple[str, int], list[float]] = {}
    for name in ordered:
        target = rig.pose.bones[name]
        location_path = f'pose.bones["{name}"].location'
        rotation_path = f'pose.bones["{name}"].rotation_quaternion' if target.rotation_mode == "QUATERNION" else f'pose.bones["{name}"].rotation_euler'
        scale_path = f'pose.bones["{name}"].scale'
        rotation_count = 4 if target.rotation_mode == "QUATERNION" else 3
        for index in range(3):
            values[(location_path, index)] = []
            values[(scale_path, index)] = []
        for index in range(rotation_count):
            values[(rotation_path, index)] = []
        previous_rotation = None
        for frame_index, desired in enumerate(desired_rows):
            parent = target.parent
            if parent is None:
                raise MaterialisationError(f"periodic non-Root motion bone {name} has no parent")
            parent_transform = roots[frame_index] if parent.name == "Root" else desired_rows[frame_index][parent.name]
            basis = _basis_from_full_transform(rig, name, parent_transform, desired[name])
            location, rotation, scale = basis.decompose()
            rotation_values = rotation if target.rotation_mode == "QUATERNION" else rotation.to_euler(EULER_OUTPUT_MODE, previous_rotation)
            if target.rotation_mode != "QUATERNION":
                previous_rotation = rotation_values.copy()
            for index in range(3):
                values[(location_path, index)].append(float(location[index]))
                values[(scale_path, index)].append(float(scale[index]))
            for index in range(rotation_count):
                values[(rotation_path, index)].append(float(rotation_values[index]))
    return values


def _walk_root_from_repaired_hips(
    rig: Any, role: Any, repair_evidence: Mapping[str, Any], rig_basis: Matrix,
) -> tuple[list[list[float]], dict[tuple[str, int], list[float]], dict[str, Any]]:
    """Author WF/WB Root from the repaired evaluated Hips ground-line projection."""
    if repair_evidence.get("root_projection_stage") != "pending_family_specific_repaired_hips_projection":
        raise MaterialisationError(f"{role.code} Stage 2 requires pending Stage 1 repaired-Hips evidence")
    if not repair_evidence.get("accepted") or "_frozen_repaired_armature_rows" not in repair_evidence:
        raise MaterialisationError(f"{role.code} Stage 2 requires accepted Stage 1 repaired-Hips transforms")
    rows = repair_evidence["_frozen_repaired_armature_rows"]
    if not isinstance(rows, list) or len(rows) < 2:
        raise MaterialisationError(f"{role.code} Stage 2 repaired-Hips trace is incomplete")
    hips_local = [_root_local_translation(rig_basis, row["Hips"].translation) for row in rows]
    start = Vector((hips_local[0].x, 0.0, hips_local[0].z))
    end = Vector((hips_local[-1].x, 0.0, hips_local[-1].z))
    line = end - start
    character_scale = _anatomical_character_scale(rig)
    minimum_length = character_scale * .05
    if line.length < minimum_length:
        raise MaterialisationError(
            f"{role.code} repaired Hips ground line is degenerate: {line.length:.9f} m < {minimum_length:.9f} m"
        )
    direction = line.normalized()
    target_lateral, target_forward = _declared_straight_direction(role.code)
    target = Vector((target_lateral, 0.0, target_forward))
    signed_direction = line.dot(target)
    if signed_direction <= DEFAULT_TOLERANCES["root_direction_minimum"]:
        raise MaterialisationError(
            f"{role.code} repaired Hips ground line contradicts declared signed direction ({signed_direction:.9f})"
        )
    progress = [(Vector((point.x, 0.0, point.z)) - start).dot(direction) for point in hips_local]
    root = [[0.0] * len(rows) for _ in range(3)]
    root[0] = [target_lateral * value for value in progress]
    root[2] = [target_forward * value for value in progress]
    angle = alignment_angle((direction.x, direction.z), (target_lateral, target_forward))
    alignment = _constant_alignment_delta(rig, angle)
    origin = rows[0]["Hips"].translation.copy()
    aligned_rows = [
        {name: _align_about(matrix, alignment, origin) for name, matrix in row.items()}
        for row in rows
    ]
    roots = [
        rig.data.bones["Root"].matrix_local @ Matrix.Translation(Vector((root[0][index], 0.0, root[2][index])))
        for index in range(len(rows))
    ]
    channels = _channels_from_armature_rows(rig, roots, aligned_rows)
    linear = [progress[-1] * index / (len(progress) - 1) for index in range(len(progress))]
    evidence = {
        "contract": "repaired_hips_ground_line_root_projection_v1",
        "ordering": "stage_1_repaired_hips_then_stage_2_ground_line_projection",
        "stage_1_evidence_consumed": True,
        "source_estimator_superseded": True,
        "ground_line_start_root_local": list(start),
        "ground_line_end_root_local": list(end),
        "ground_line_length": line.length,
        "minimum_ground_line_length": minimum_length,
        "declared_direction_lateral_forward": [target_lateral, target_forward],
        "signed_direction_dot": signed_direction,
        "projection_scalar": progress,
        "constant_alignment_radians": angle,
        "maximum_linear_envelope_deviation": max(abs(actual - expected) for actual, expected in zip(progress, linear)),
        "root_lateral_maximum": max(abs(value) for value in root[0]),
        "root_vertical_maximum": max(abs(value) for value in root[1]),
        "root_forward_endpoint": root[2][-1],
        "hips_carriage_subtractions": 1,
        "_frozen_aligned_armature_rows": aligned_rows,
    }
    return root, channels, evidence


def _arc_root_from_repaired_hips(
    rig: Any, role: Any, repair_evidence: Mapping[str, Any], rig_basis: Matrix,
) -> tuple[list[list[float]], list[float], dict[tuple[str, int], list[float]], dict[str, Any]]:
    """Split repaired source-paced curved Hips carriage into Root exactly once."""
    rows = repair_evidence.get("_frozen_repaired_armature_rows")
    if not repair_evidence.get("accepted") or not isinstance(rows, list) or len(rows) < 3:
        raise MaterialisationError(f"{role.code} arc reconstruction requires accepted repaired hierarchy rows")
    hips_local = [_root_local_translation(rig_basis, row["Hips"].translation) for row in rows]
    origin = hips_local[0]
    root = [[0.0] * len(rows) for _ in range(3)]
    root[0] = [point.x - origin.x for point in hips_local]
    root[2] = [point.z - origin.z for point in hips_local]
    yaw = _heading_progress_from_armature_rows(rows)
    scale = _anatomical_character_scale(rig)
    points = [Vector((root[0][index], 0.0, root[2][index])) for index in range(len(rows))]
    chord = points[-1] - points[0]
    polyline = sum((points[index] - points[index - 1]).length for index in range(1, len(points)))
    curve = arc_curve_evidence(
        role.code,
        [(point.x, point.z) for point in points],
        yaw,
        scale,
    )
    if "yaw_sign_or_span" in curve["failures"]:
        raise MaterialisationError(f"{role.code} repaired arc has wrong or insignificant yaw ({curve['yaw_span_rad']:.9f})")
    if "lateral_curvature_handedness" in curve["failures"]:
        raise MaterialisationError(
            f"{role.code} repaired arc has wrong or insignificant role-relative lateral curvature "
            f"({curve['role_relative_lateral_excursion']:.9f})"
        )
    if "forward_progress" in curve["failures"]:
        raise MaterialisationError(f"{role.code} repaired arc lacks material forward progress ({curve['forward_progress']:.9f})")
    if "straight_or_near_straight" in curve["failures"]:
        raise MaterialisationError(f"{role.code} repaired arc was straightened")
    if "unstable_curve_step" in curve["failures"]:
        raise MaterialisationError(f"{role.code} repaired arc has an unstable path step")
    roots = [
        rig.data.bones["Root"].matrix_local
        @ Matrix.Translation(Vector((root[0][index], 0.0, root[2][index])))
        @ Matrix.Rotation(yaw[index], 4, "Y")
        for index in range(len(rows))
    ]
    channels = _channels_from_armature_rows(rig, roots, rows)
    linear = [chord * index / (len(rows) - 1) for index in range(len(rows))]
    evidence = {
        "contract": "repaired_hips_source_paced_arc_root_projection_v1",
        "ordering": "periodic_non_root_repair_then_curved_hips_translation_and_global_z_heading_promotion",
        "source_formula_substitution": False,
        "translation_samples": [[root[0][index], root[2][index]] for index in range(len(rows))],
        "heading_samples_rad": yaw,
        "endpoint_yaw_rad": curve["yaw_span_rad"],
        "expected_handedness_sign": curve["expected_handedness_sign"],
        "maximum_lateral_excursion": curve["lateral_excursion"],
        "role_relative_lateral_excursion": curve["role_relative_lateral_excursion"],
        "forward_progress": curve["forward_progress"],
        "polyline_length": polyline,
        "chord_length": chord.length,
        "curvature_excess": polyline - chord.length,
        "maximum_linear_envelope_deviation": max((points[index] - linear[index]).length for index in range(len(rows))),
        "hips_carriage_subtractions": 1,
        "vertical_root_maximum": 0.0,
        "_frozen_repaired_armature_rows": rows,
    }
    return root, yaw, channels, evidence


def _ancestor_names(bone: Any) -> list[str]:
    """Return a pose bone's ancestor names, nearest first."""
    names = []
    parent = bone.parent
    while parent is not None:
        names.append(parent.name)
        parent = parent.parent
    return names


def _loop_relative_samples(rig: Any, action: Any, frames: Sequence[int]) -> list[dict[str, Matrix]]:
    """Sample every non-Root bone in cycle-normalised generated Root space."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    names = _motion_bone_names(rig)
    try:
        _activate_action(rig, action)
        rows = []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            root_inverse = rig.pose.bones["Root"].matrix.inverted_safe()
            rows.append({name: root_inverse @ rig.pose.bones[name].matrix for name in names})
        return rows
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _loop_armature_samples(rig: Any, action: Any, frames: Sequence[int]) -> list[dict[str, Matrix]]:
    """Sample every non-Root motion bone in armature space."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    names = _motion_bone_names(rig)
    try:
        _activate_action(rig, action)
        rows = []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            rows.append({name: rig.pose.bones[name].matrix.copy() for name in names})
        return rows
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _shortest_rotation_difference(first: Quaternion, second: Quaternion) -> Quaternion:
    """Return the sign-normalised shortest quaternion delta."""
    difference = first.rotation_difference(second)
    if difference.w < 0.0:
        difference.negate()
    return difference


def _root_space_rotation_delta(first: Quaternion, second: Quaternion) -> Quaternion:
    """Return the shortest angular step expressed in cycle-normalised Root space."""
    difference = second @ first.conjugated()
    if difference.w < 0.0:
        difference.negate()
    return difference


def _natural_recurrence_evidence(
    samples: Sequence[Mapping[str, Matrix]], scale: float,
) -> dict[str, Any]:
    """Measure declared source recurrence before any boundary repair."""
    thresholds = recurrence_thresholds(scale)
    maxima = {key: 0.0 for key in thresholds}
    maximum_bones = {key: "" for key in thresholds}
    foot_seams: dict[str, dict[str, float | bool]] = {}
    landmarks: dict[str, dict[str, float]] = {}
    for name in samples[0]:
        values = [sample[name] for sample in samples]
        locations = [value.translation for value in values]
        rotations = [value.to_quaternion() for value in values]
        outgoing_linear, incoming_linear = locations[1] - locations[0], locations[-1] - locations[-2]
        outgoing_angular = _root_space_rotation_delta(rotations[0], rotations[1])
        incoming_angular = _root_space_rotation_delta(rotations[-2], rotations[-1])
        metrics = {
            "pose_translation": (locations[-1] - locations[0]).length,
            "pose_rotation_rad": _shortest_rotation_difference(rotations[0], rotations[-1]).angle,
            "linear_velocity": (incoming_linear - outgoing_linear).length,
            "angular_velocity_rad": _shortest_rotation_difference(outgoing_angular, incoming_angular).angle,
            "linear_acceleration": ((locations[-1] - 2.0 * locations[-2] + locations[-3]) - (locations[2] - 2.0 * locations[1] + locations[0])).length,
            "angular_acceleration_rad": _shortest_rotation_difference(
                _root_space_rotation_delta(_root_space_rotation_delta(rotations[-3], rotations[-2]), incoming_angular),
                _root_space_rotation_delta(outgoing_angular, _root_space_rotation_delta(rotations[1], rotations[2])),
            ).angle,
        }
        for key, value in metrics.items():
            if value > maxima[key]:
                maxima[key], maximum_bones[key] = value, name
        if name in {"Hips", "Chest", "Head", "LeftFoot", "RightFoot"}:
            landmarks[name] = metrics
        if name in {"LeftFoot", "RightFoot"}:
            reversal, spike = foot_recurrence_flags(
                incoming_linear.length, outgoing_linear.length,
                incoming_linear.dot(outgoing_linear), metrics["linear_velocity"], scale,
            )
            foot_seams[name] = {
                "reversal": reversal, "spike": spike,
                "incoming_speed": incoming_linear.length, "outgoing_speed": outgoing_linear.length,
            }
    decision = recurrence_acceptance(maxima, scale, "source recurrence")
    failures = {
        key: {**failure, "bone": maximum_bones[key]}
        for key, failure in decision["failures"].items()
    }
    foot_failures = {name: row for name, row in foot_seams.items() if row["reversal"] or row["spike"]}
    return {
        "accepted": not failures and not foot_failures,
        "diagnostic": "source recurrence",
        "contract": RECURRENCE_CONTRACT,
        "sampling_interval_frames": decision["sampling_interval_frames"],
        "units": decision["units"],
        "character_scale_metres": decision["character_scale_metres"],
        "thresholds": decision["thresholds"],
        "maxima": maxima,
        "maximum_bones": maximum_bones,
        "landmarks": landmarks,
        "feet": foot_seams,
        "failures": failures,
        "foot_failures": foot_failures,
    }


def _uses_bounded_loop_repair(role: Any) -> bool:
    """Apply symmetric non-Root seam repair to declared periodic loop families."""
    policy = role_projection_policy(role)
    return role.is_loop and policy.family in {"walk", "precision_lateral", "strafe", "arc"}


def _uses_repaired_walk_root(role: Any) -> bool:
    """Return whether Stage 2 owns this role's straight longitudinal Root."""
    return role.is_loop and role_projection_policy(role).family == "walk" and role.code.startswith(("wf_", "wb_"))


def _matrix_components(matrix: Matrix) -> tuple[Vector, Quaternion, Vector]:
    location, rotation, scale = matrix.decompose()
    rotation.normalize()
    return location, rotation, scale


def _apply_reference_matrix_correction(
    current: Matrix, reference: Matrix, target: Matrix, weight: float,
) -> Matrix:
    """Apply a decaying reference correction without pulling toward its pose."""
    location, rotation, scale = _matrix_components(current)
    reference_location, reference_rotation, reference_scale = _matrix_components(reference)
    target_location, target_rotation, target_scale = _matrix_components(target)
    rotation_delta = target_rotation @ reference_rotation.conjugated()
    correction_rotation = Quaternion().slerp(rotation_delta, weight)
    return Matrix.LocRotScale(
        location + (target_location - reference_location) * weight,
        correction_rotation @ rotation,
        scale + (target_scale - reference_scale) * weight,
    )


def _rotation_vector(rotation: Quaternion) -> Vector:
    """Return a shortest-path axis-angle vector in cycle-normalised Root space."""
    value = rotation.copy()
    value.normalize()
    if value.w < 0.0:
        value.negate()
    angle = float(value.angle)
    return Vector((0.0, 0.0, 0.0)) if angle <= 1e-12 else value.axis * angle


def _maximum_attribution(records: Sequence[Mapping[str, Any]]) -> dict[str, Any]:
    """Return one deterministic maximum while retaining its physical attribution."""
    if not records:
        return {"value": 0.0, "bone": None, "sample_index": None}
    return dict(max(records, key=lambda row: (float(row["value"]), str(row.get("bone", "")))))


def _attributed_series_evidence(
    per_bone: Mapping[str, Sequence[Mapping[str, Any]]], sample_count: int,
) -> dict[str, Any]:
    flattened = [row for name in sorted(per_bone) for row in per_bone[name]]
    per_frame = []
    for sample_index in range(sample_count):
        records = [row for row in flattened if row.get("sample_index") == sample_index]
        if records:
            per_frame.append(_maximum_attribution(records))
    return {
        "maximum": _maximum_attribution(flattened),
        "per_bone_maxima": {
            name: _maximum_attribution(per_bone[name]) for name in sorted(per_bone)
        },
        "per_frame_maxima": per_frame,
    }


def _hierarchy_basis_rows(
    rig: Any, rows: Sequence[Mapping[str, Matrix]],
) -> list[dict[str, Matrix]]:
    names = set(rows[0])
    ordered = sorted(names, key=lambda name: sum(1 for parent in _ancestor_names(rig.pose.bones[name]) if parent in names))
    result = []
    for row in rows:
        basis_row = {}
        for name in ordered:
            parent = rig.pose.bones[name].parent
            if parent is None:
                raise ValueError(f"periodic hierarchy bone {name} has no parent")
            parent_transform = Matrix.Identity(4) if parent.name == "Root" else row[parent.name]
            basis_row[name] = _basis_from_full_transform(rig, name, parent_transform, row[name])
        result.append(basis_row)
    return result


def _hierarchy_full_rows(
    rig: Any, basis_rows: Sequence[Mapping[str, Matrix]],
) -> list[dict[str, Matrix]]:
    names = set(basis_rows[0])
    ordered = sorted(names, key=lambda name: sum(1 for parent in _ancestor_names(rig.pose.bones[name]) if parent in names))
    result = []
    for basis_row in basis_rows:
        full_row = {}
        for name in ordered:
            bone = rig.pose.bones[name]
            parent = bone.parent
            if parent is None:
                raise ValueError(f"periodic hierarchy bone {name} has no parent")
            parent_transform = Matrix.Identity(4) if parent.name == "Root" else full_row[parent.name]
            full_row[name] = bone.bone.convert_local_to_pose(
                basis_row[name], bone.bone.matrix_local,
                parent_matrix=parent_transform,
                parent_matrix_local=parent.bone.matrix_local,
                invert=False,
            )
        result.append(full_row)
    return result


def _repair_periodic_pose_rows(
    rows: Sequence[Mapping[str, Matrix]], character_scale: float,
    *, window_frames: int | None = None, blend_profile: str = "quadratic",
    direct_safety_contract: bool = False, rig: Any | None = None,
) -> tuple[list[dict[str, Matrix]], dict[str, Any]]:
    """Copy the opening pose to the endpoint and smooth both boundary windows."""
    if window_frames is None:
        beginning, ending = symmetric_window(len(rows))
    else:
        beginning = ending = int(window_frames)
        if beginning < 2 or len(rows) < 2 * beginning + 3:
            raise ValueError("symmetric repair windows overlap the immutable interior")
    if blend_profile not in REPAIR_BLEND_PROFILES:
        raise ValueError(f"unknown loop repair blend profile: {blend_profile}")
    if direct_safety_contract:
        if rig is None:
            raise ValueError("direct loop-repair safety requires the evaluated rig hierarchy")
        basis_rows = _hierarchy_basis_rows(rig, rows)
        repaired_basis, evidence = _repair_periodic_pose_rows(
            basis_rows, character_scale,
            window_frames=beginning, blend_profile=blend_profile,
        )
        repaired_full = _hierarchy_full_rows(rig, repaired_basis)
        for index in range(beginning, len(rows) - ending):
            repaired_full[index] = {name: matrix.copy() for name, matrix in rows[index].items()}
        evidence["repair_coordinate_space"] = "pose_local_basis_with_forward_kinematic_reconstruction"
        evidence["hierarchy_reconstruction"] = {
            "contract": "real_parent_rest_forward_kinematic_reconstruction_v1",
            "maximum_immutable_interior_matrix_error": max(
                (
                    _matrix_error(repaired_full[index][name], rows[index][name])
                    for index in range(beginning, len(rows) - ending)
                    for name in repaired_full[index]
                ),
                default=0.0,
            ),
            "post_repair_endpoint_matrix_error": max(
                _matrix_error(repaired_full[0][name], repaired_full[-1][name])
                for name in repaired_full[0]
            ),
        }
        evidence["reconstructed_hierarchy_recurrence"] = _natural_recurrence_evidence(
            repaired_full, character_scale,
        )
        cadence_names = ("Hips", "LeftShin", "RightShin", "LeftFoot", "RightFoot")
        cadence_rows = [
            repaired_full[index]
            for index in (*range(len(repaired_full) - 4, len(repaired_full) - 1), 0, 1, 2, 3)
        ]
        evidence["evaluated_hierarchy_cadence"] = evaluated_hierarchy_cadence(
            [
                {name: tuple(row[name].translation) for name in cadence_names}
                for row in cadence_rows
            ],
            character_scale,
            30.0,
        )
        _apply_direct_repair_safety(rows, repaired_full, character_scale, evidence)
        return repaired_full, evidence
    original = [{name: matrix.copy() for name, matrix in row.items()} for row in rows]
    repaired = [{name: matrix.copy() for name, matrix in row.items()} for row in rows]
    maximum_translation_correction = maximum_rotation_correction = 0.0
    maximum_translation_curvature = maximum_rotation_curvature = 0.0
    raw_endpoint_translation = raw_endpoint_rotation = 0.0
    raw_linear_mismatch = raw_angular_mismatch = 0.0
    legacy_attribution: dict[str, list[dict[str, Any]]] = {
        key: [] for key in (
            "endpoint_translation", "endpoint_rotation_rad", "linear_derivative_mismatch",
            "angular_derivative_mismatch_rad", "correction_curvature",
            "angular_correction_curvature_rad",
        )
    }
    series_by_metric: dict[str, dict[str, list[dict[str, Any]]]] = {
        key: {} for key in (
            "translation_correction", "rotation_correction_rad",
            "translation_correction_curvature",
            "axis_angular_correction_derivative_rad",
            "axis_angular_correction_curvature_rad",
        )
    }
    for name in sorted(original[0]):
        locations = [_matrix_components(row[name])[0] for row in original]
        rotations = [_matrix_components(row[name])[1] for row in original]
        scales = [_matrix_components(row[name])[2] for row in original]
        outgoing = locations[1] - locations[0]
        incoming = locations[-1] - locations[-2]
        # The endpoint displacement contributes equally to both windows, making
        # the symmetric two-ended topology an explicit construction invariant.
        common_velocity = (outgoing + incoming) * .5 + (locations[-1] - locations[0]) / (2.0 * beginning)
        outgoing_rotation = _root_space_rotation_delta(rotations[0], rotations[1])
        incoming_rotation = _root_space_rotation_delta(rotations[-2], rotations[-1])
        common_rotation = outgoing_rotation.slerp(incoming_rotation, .5)
        opening = original[0][name]
        opening_location, opening_rotation, opening_scale = _matrix_components(opening)
        common_scale_delta = ((scales[1] - scales[0]) + (scales[-1] - scales[-2])) * .5
        common_acceleration = (
            (locations[2] - 2.0 * locations[1] + locations[0])
            + (locations[-1] - 2.0 * locations[-2] + locations[-3])
        ) * .5
        opening_one_rotation = common_rotation @ opening_rotation
        closing_one_rotation = common_rotation.conjugated() @ opening_rotation
        targets = {
            0: opening,
            1: Matrix.LocRotScale(opening_location + common_velocity, opening_one_rotation, opening_scale + common_scale_delta),
            2: Matrix.LocRotScale(opening_location + 2.0 * common_velocity + common_acceleration, common_rotation @ opening_one_rotation, opening_scale + 2.0 * common_scale_delta),
            len(rows) - 3: Matrix.LocRotScale(opening_location - 2.0 * common_velocity + common_acceleration, common_rotation.conjugated() @ closing_one_rotation, opening_scale - 2.0 * common_scale_delta),
            len(rows) - 2: Matrix.LocRotScale(opening_location - common_velocity, closing_one_rotation, opening_scale - common_scale_delta),
            len(rows) - 1: opening,
        }
        for index, target in targets.items():
            repaired[index][name] = target.copy()
        for distance in range(3, beginning):
            weight = repair_profile_weight(distance, beginning, blend_profile)
            repaired[distance][name] = _apply_reference_matrix_correction(
                original[distance][name], original[2][name], targets[2], weight,
            )
        for distance in range(3, ending):
            weight = repair_profile_weight(distance, ending, blend_profile)
            index = len(rows) - 1 - distance
            repaired[index][name] = _apply_reference_matrix_correction(
                original[index][name], original[len(rows) - 3][name], targets[len(rows) - 3], weight,
            )
        endpoint_translation_vector = locations[-1] - locations[0]
        endpoint_rotation_delta = _shortest_rotation_difference(rotations[0], rotations[-1])
        endpoint_rotation_vector = _rotation_vector(endpoint_rotation_delta)
        linear_mismatch_vector = incoming - outgoing
        angular_mismatch_delta = _shortest_rotation_difference(outgoing_rotation, incoming_rotation)
        angular_mismatch_vector = _rotation_vector(angular_mismatch_delta)
        boundary_records = {
            "endpoint_translation": {
                "value": endpoint_translation_vector.length, "bone": name,
                "sample_indices": [0, len(rows) - 1], "vector": list(endpoint_translation_vector),
            },
            "endpoint_rotation_rad": {
                "value": endpoint_rotation_delta.angle, "bone": name,
                "sample_indices": [0, len(rows) - 1], "rotation_vector": list(endpoint_rotation_vector),
            },
            "linear_derivative_mismatch": {
                "value": linear_mismatch_vector.length, "bone": name,
                "sample_indices": [0, 1, len(rows) - 2, len(rows) - 1],
                "vector": list(linear_mismatch_vector),
            },
            "angular_derivative_mismatch_rad": {
                "value": angular_mismatch_delta.angle, "bone": name,
                "sample_indices": [0, 1, len(rows) - 2, len(rows) - 1],
                "rotation_vector": list(angular_mismatch_vector),
            },
        }
        for key, record in boundary_records.items():
            legacy_attribution[key].append(record)
        raw_endpoint_translation = max(raw_endpoint_translation, endpoint_translation_vector.length)
        raw_endpoint_rotation = max(raw_endpoint_rotation, endpoint_rotation_delta.angle)
        raw_linear_mismatch = max(raw_linear_mismatch, linear_mismatch_vector.length)
        raw_angular_mismatch = max(raw_angular_mismatch, angular_mismatch_delta.angle)
        correction_locations = []
        correction_angles = []
        correction_rotation_vectors = []
        translation_rows = []
        rotation_rows = []
        for sample_index, (before, after) in enumerate(zip(original, repaired)):
            translation = after[name].translation - before[name].translation
            rotation_delta = _shortest_rotation_difference(
                before[name].to_quaternion(), after[name].to_quaternion(),
            )
            rotation_vector = _rotation_vector(_root_space_rotation_delta(
                before[name].to_quaternion(), after[name].to_quaternion(),
            ))
            correction_locations.append(translation)
            correction_rotation_vectors.append(rotation_vector)
            correction_angles.append(rotation_delta.angle)
            translation_rows.append({
                "value": translation.length, "bone": name, "sample_index": sample_index,
                "vector": list(translation),
            })
            rotation_rows.append({
                "value": rotation_delta.angle, "bone": name, "sample_index": sample_index,
                "rotation_vector": list(rotation_vector),
            })
            maximum_translation_correction = max(maximum_translation_correction, correction_locations[-1].length)
            maximum_rotation_correction = max(maximum_rotation_correction, correction_angles[-1])
        translation_curvature_rows = []
        scalar_rotation_curvature_rows = []
        axis_angular_derivative_rows = []
        axis_angular_curvature_rows = []
        for sample_index in range(1, len(rows)):
            derivative = correction_rotation_vectors[sample_index] - correction_rotation_vectors[sample_index - 1]
            axis_angular_derivative_rows.append({
                "value": derivative.length, "bone": name, "sample_index": sample_index,
                "sample_indices": [sample_index - 1, sample_index], "rotation_vector": list(derivative),
            })
        for sample_index in range(1, len(rows) - 1):
            translation_curvature = (
                correction_locations[sample_index + 1]
                - 2.0 * correction_locations[sample_index]
                + correction_locations[sample_index - 1]
            )
            scalar_rotation_curvature = abs(
                correction_angles[sample_index + 1]
                - 2.0 * correction_angles[sample_index]
                + correction_angles[sample_index - 1]
            )
            axis_angular_curvature = (
                correction_rotation_vectors[sample_index + 1]
                - 2.0 * correction_rotation_vectors[sample_index]
                + correction_rotation_vectors[sample_index - 1]
            )
            translation_curvature_rows.append({
                "value": translation_curvature.length, "bone": name, "sample_index": sample_index,
                "sample_indices": [sample_index - 1, sample_index, sample_index + 1],
                "vector": list(translation_curvature),
            })
            scalar_rotation_curvature_rows.append({
                "value": scalar_rotation_curvature, "bone": name, "sample_index": sample_index,
                "sample_indices": [sample_index - 1, sample_index, sample_index + 1],
            })
            axis_angular_curvature_rows.append({
                "value": axis_angular_curvature.length, "bone": name, "sample_index": sample_index,
                "sample_indices": [sample_index - 1, sample_index, sample_index + 1],
                "rotation_vector": list(axis_angular_curvature),
            })
        maximum_translation_curvature = max(
            maximum_translation_curvature,
            float(_maximum_attribution(translation_curvature_rows)["value"]),
        )
        maximum_rotation_curvature = max(
            maximum_rotation_curvature,
            float(_maximum_attribution(scalar_rotation_curvature_rows)["value"]),
        )
        legacy_attribution["correction_curvature"].extend(translation_curvature_rows)
        legacy_attribution["angular_correction_curvature_rad"].extend(scalar_rotation_curvature_rows)
        series_by_metric["translation_correction"][name] = translation_rows
        series_by_metric["rotation_correction_rad"][name] = rotation_rows
        series_by_metric["translation_correction_curvature"][name] = translation_curvature_rows
        series_by_metric["axis_angular_correction_derivative_rad"][name] = axis_angular_derivative_rows
        series_by_metric["axis_angular_correction_curvature_rad"][name] = axis_angular_curvature_rows
    immutable_start, immutable_end = beginning, len(rows) - ending - 1
    interior_error = max(
        (_matrix_error(original[index][name], repaired[index][name]) for index in range(immutable_start, immutable_end + 1) for name in original[index]),
        default=0.0,
    )
    beginning_change = max(
        (_matrix_error(original[index][name], repaired[index][name]) for index in range(beginning) for name in original[index]), default=0.0,
    )
    ending_change = max(
        (_matrix_error(original[index][name], repaired[index][name]) for index in range(len(rows) - ending, len(rows)) for name in original[index]), default=0.0,
    )
    foot_reversal = foot_spike = False
    for foot in ("LeftFoot", "RightFoot"):
        incoming = repaired[-1][foot].translation - repaired[-2][foot].translation
        outgoing = repaired[1][foot].translation - repaired[0][foot].translation
        reversal, spike = foot_recurrence_flags(
            incoming.length, outgoing.length, incoming.dot(outgoing), (incoming - outgoing).length, character_scale,
        )
        foot_reversal = foot_reversal or reversal
        foot_spike = foot_spike or spike
    metrics = {
        "endpoint_translation": raw_endpoint_translation,
        "endpoint_rotation_rad": raw_endpoint_rotation,
        "linear_derivative_mismatch": raw_linear_mismatch,
        "angular_derivative_mismatch_rad": raw_angular_mismatch,
        "correction_curvature": maximum_translation_curvature,
        "angular_correction_curvature_rad": maximum_rotation_curvature,
    }
    assessment: dict[str, Any] = assess_repair(
        metrics, character_scale, len(rows), foot_reversal=foot_reversal, foot_spike=foot_spike,
        window_frames=beginning,
    )
    attributed_series = {
        key: _attributed_series_evidence(value, len(rows))
        for key, value in series_by_metric.items()
    }
    opening_range, closing_range = range(beginning), range(len(rows) - ending, len(rows))
    asymmetry = {}
    for key, per_bone in series_by_metric.items():
        all_rows = [row for name in sorted(per_bone) for row in per_bone[name]]
        opening = _maximum_attribution([
            row for row in all_rows if row.get("sample_index") in opening_range
        ])
        closing = _maximum_attribution([
            row for row in all_rows if row.get("sample_index") in closing_range
        ])
        asymmetry[key] = correction_asymmetry_evidence(
            float(opening["value"]), float(closing["value"]), opening, closing,
        )
    endpoint_error = max(_matrix_error(repaired[0][name], repaired[-1][name]) for name in repaired[0])
    linear_velocity_error = max(
        ((repaired[1][name].translation - repaired[0][name].translation) - (repaired[-1][name].translation - repaired[-2][name].translation)).length
        for name in repaired[0]
    )
    angular_velocity_error = max(
        _shortest_rotation_difference(
            _root_space_rotation_delta(repaired[0][name].to_quaternion(), repaired[1][name].to_quaternion()),
            _root_space_rotation_delta(repaired[-2][name].to_quaternion(), repaired[-1][name].to_quaternion()),
        ).angle for name in repaired[0]
    )
    assessment.update({
        "diagnostic_evidence_version": 1,
        "blend_profile": blend_profile,
        "maximum_translation_correction": maximum_translation_correction,
        "maximum_rotation_correction_rad": maximum_rotation_correction,
        "window_ranges_zero_based": {"beginning": [0, beginning - 1], "ending": [len(rows) - ending, len(rows) - 1]},
        "both_windows_modified": beginning_change > 1e-12 and ending_change > 1e-12,
        "beginning_window_maximum_change": beginning_change,
        "ending_window_maximum_change": ending_change,
        "maximum_immutable_interior_change": interior_error,
        "post_repair_endpoint_matrix_error": endpoint_error,
        "post_repair_linear_velocity_error": linear_velocity_error,
        "post_repair_angular_velocity_error_rad": angular_velocity_error,
        "legacy_metric_attribution": {
            key: _maximum_attribution(value) for key, value in legacy_attribution.items()
        },
        "legacy_metric_extrema": {
            key: {
                "maximum": _maximum_attribution(value),
                "per_bone_maxima": {
                    name: _maximum_attribution([row for row in value if row["bone"] == name])
                    for name in sorted({str(row["bone"]) for row in value})
                },
            }
            for key, value in legacy_attribution.items()
        },
        "correction_extrema": attributed_series,
        "axis_angular_correction": {
            "representation": "shortest_root_space_axis_angle_rotation_vector_radians",
            "derivative": attributed_series["axis_angular_correction_derivative_rad"],
            "curvature": attributed_series["axis_angular_correction_curvature_rad"],
        },
        "opening_closing_correction_asymmetry": asymmetry,
        "naturalness_ranking_signals": {
            "evidence_only": True,
            "changes_admission_or_selection": False,
            "lower_is_more_symmetric": {
                key: row["normalised_imbalance"] for key, row in asymmetry.items()
            },
        },
    })
    if not assessment["both_windows_modified"]:
        assessment["accepted"] = False
        assessment["failures"]["two_ended_topology"] = {"value": 0.0, "threshold": 1.0}
    if interior_error > 1e-12:
        assessment["accepted"] = False
        assessment["failures"]["immutable_interior"] = {"value": interior_error, "threshold": 1e-12}
    assessment["windows"] = {"beginning_frames": beginning, "ending_frames": ending}
    return repaired, assessment


def _diagnose_periodic_pose_repair_options(
    rows_by_source_range: Mapping[tuple[int, int], Sequence[Mapping[str, Matrix]]],
    character_scale: float,
    window_frames: Sequence[int],
    blend_profiles: Sequence[str] = REPAIR_BLEND_PROFILES,
) -> dict[str, Any]:
    """Compare physical repair evidence without selecting, baking, or publishing."""
    comparisons = []
    for source_range, rows in sorted(rows_by_source_range.items()):
        for width in sorted(set(window_frames)):
            for profile in sorted(set(blend_profiles)):
                try:
                    _, evidence = _repair_periodic_pose_rows(
                        rows, character_scale, window_frames=width, blend_profile=profile,
                    )
                except ValueError as error:
                    comparisons.append({
                        "source_range": list(source_range), "window_frames": width,
                        "blend_profile": profile, "evaluated": False, "reason": str(error),
                    })
                    continue
                comparisons.append({
                    "source_range": list(source_range), "window_frames": width,
                    "blend_profile": profile, "evaluated": True,
                    "legacy_metrics": evidence["metrics"],
                    "repair_cost_for_comparison_only": evidence["repair_cost"],
                    "opening_closing_correction_asymmetry": evidence["opening_closing_correction_asymmetry"],
                    "correction_maxima": {
                        key: value["maximum"] for key, value in evidence["correction_extrema"].items()
                    },
                    "axis_angular_correction": evidence["axis_angular_correction"],
                })
    return {
        "contract": "read_only_hierarchy_repair_profile_comparison_v1",
        "read_only": True,
        "changes_admission_or_selection": False,
        "comparisons": comparisons,
    }


def _contact_interval_count(indices: Sequence[int]) -> int:
    return sum(index == 0 or indices[index - 1] != value - 1 for index, value in enumerate(indices))


def _apply_direct_repair_safety(
    before: Sequence[Mapping[str, Matrix]],
    after: Sequence[Mapping[str, Matrix]],
    character_scale: float,
    evidence: dict[str, Any],
) -> None:
    """Replace raw endpoint vetoes with applied-correction and anatomy checks."""
    failures = evidence["failures"]
    raw_endpoint_exceedances = {
        name: failures.pop(name)
        for name in tuple(failures)
        if name in {"endpoint_translation", "endpoint_rotation_rad"}
    }
    translation_limit = character_scale * .20
    rotation_limit = .30
    translation = float(evidence["maximum_translation_correction"])
    rotation = float(evidence["maximum_rotation_correction_rad"])
    correction_components = {
        "translation": translation / translation_limit,
        "rotation": rotation / rotation_limit,
    }
    if translation > translation_limit:
        failures["actual_translation_correction"] = {"value": translation, "threshold": translation_limit}
    if rotation > rotation_limit:
        failures["actual_rotation_correction_rad"] = {"value": rotation, "threshold": rotation_limit}
    for metric in ("correction_curvature", "angular_correction_curvature_rad"):
        failures.pop(metric, None)
        value = float(evidence["metrics"][metric])
        threshold = float(evidence["thresholds"][metric])
        if value > threshold:
            failures[metric] = {"value": value, "threshold": threshold}
    closure_limits = {
        "post_repair_endpoint_matrix_error": 1e-6,
        "post_repair_linear_velocity_error": 1e-6,
        "post_repair_angular_velocity_error_rad": 1e-6,
    }
    for metric, threshold in closure_limits.items():
        if float(evidence[metric]) > threshold:
            failures[metric] = {"value": evidence[metric], "threshold": threshold}

    maximum_limb_delta = 0.0
    minimum_knee_dot = 1.0
    topology = {}
    maximum_contact_height_delta = 0.0
    for side in ("Left", "Right"):
        upper, lower, foot = f"{side}Leg", f"{side}Shin", f"{side}Foot"
        for source, repaired in zip(before, after):
            for parent, child in ((upper, lower), (lower, foot)):
                source_length = (source[child].translation - source[parent].translation).length
                repaired_length = (repaired[child].translation - repaired[parent].translation).length
                maximum_limb_delta = max(maximum_limb_delta, abs(repaired_length - source_length))
            source_axis = source[foot].translation - source[upper].translation
            repaired_axis = repaired[foot].translation - repaired[upper].translation
            source_bend = source[lower].translation - source[upper].translation
            source_bend -= source_axis * (source_bend.dot(source_axis) / max(source_axis.length_squared, 1e-12))
            repaired_bend = repaired[lower].translation - repaired[upper].translation
            repaired_bend -= repaired_axis * (repaired_bend.dot(repaired_axis) / max(repaired_axis.length_squared, 1e-12))
            if source_bend.length > 1e-8 and repaired_bend.length > 1e-8:
                minimum_knee_dot = min(minimum_knee_dot, source_bend.normalized().dot(repaired_bend.normalized()))
        source_heights = [row[foot].translation.y - row["Hips"].translation.y for row in before]
        repaired_heights = [row[foot].translation.y - row["Hips"].translation.y for row in after]
        source_ground, repaired_ground = min(source_heights), min(repaired_heights)
        source_contacts = [
            index for index, value in enumerate(source_heights)
            if value <= source_ground + character_scale * .05
        ]
        repaired_contacts = [
            index for index, value in enumerate(repaired_heights)
            if value <= repaired_ground + character_scale * .05
        ]
        topology[foot] = {
            "source_interval_count": _contact_interval_count(source_contacts),
            "repaired_interval_count": _contact_interval_count(repaired_contacts),
            "source_samples": source_contacts,
            "repaired_samples": repaired_contacts,
            "symmetric_difference_samples": len(set(source_contacts) ^ set(repaired_contacts)),
        }
        for index in source_contacts:
            maximum_contact_height_delta = max(
                maximum_contact_height_delta,
                abs(repaired_heights[index] - source_heights[index]),
            )
    limb_limit = character_scale * .02
    contact_height_limit = character_scale * .05
    if maximum_limb_delta > limb_limit:
        failures["limb_segment_preservation"] = {"value": maximum_limb_delta, "threshold": limb_limit}
    if minimum_knee_dot < .50:
        failures["knee_orientation_preservation"] = {"value": minimum_knee_dot, "threshold": .50}
    changed_topology = {
        foot: row for foot, row in topology.items()
        if (
            row["repaired_interval_count"] > row["source_interval_count"]
            or row["symmetric_difference_samples"] > 6
        )
    }
    if changed_topology:
        failures["foot_contact_topology"] = changed_topology
    if maximum_contact_height_delta > contact_height_limit:
        failures["foot_contact_height"] = {
            "value": maximum_contact_height_delta, "threshold": contact_height_limit,
        }
    evidence["raw_endpoint_mismatch"] = {
        "admission": "diagnostic_only",
        "exceedances": raw_endpoint_exceedances,
        "metrics": {
            name: evidence["metrics"][name]
            for name in ("endpoint_translation", "endpoint_rotation_rad")
        },
    }
    evidence["actual_correction_safety"] = {
        "accepted": not any(name.startswith("actual_") for name in failures),
        "translation_limit": translation_limit,
        "rotation_limit_rad": rotation_limit,
        "normalised_components": correction_components,
    }
    evidence["repair_anatomy_contact"] = {
        "maximum_limb_segment_delta": maximum_limb_delta,
        "limb_segment_delta_limit": limb_limit,
        "minimum_knee_orientation_dot": minimum_knee_dot,
        "minimum_knee_orientation_dot_limit": .50,
        "maximum_contact_height_delta": maximum_contact_height_delta,
        "contact_height_delta_limit": contact_height_limit,
        "contact_topology": topology,
        "maximum_contact_topology_difference_samples": 6,
        "topology_contract": "bounded_contact_sample_change_without_added_intervals_v1",
        "accepted": not any(name in failures for name in (
            "limb_segment_preservation", "knee_orientation_preservation",
            "foot_contact_topology", "foot_contact_height",
        )),
    }
    evidence["accepted"] = not failures
    evidence["repair_cost"] = float(repair_naturalness_key(evidence)[0])


def _loop_hierarchy_seam_metrics(rig: Any, action: Any) -> dict[str, Any]:
    """Measure generated physical recurrence and numerical cycle fidelity."""
    end = int(action.frame_range[1])
    if end < 3:
        raise MaterialisationError(f"{action.name} has fewer than three loop samples")
    period = end - 1
    rows = _loop_relative_samples(
        rig, action, (1, 2, 3, end - 2, end - 1, end, 1 + period, 2 + period, end + period),
    )
    maxima = {
        "pose_translation": 0.0,
        "pose_rotation_rad": 0.0,
        "linear_velocity": 0.0,
        "angular_velocity_rad": 0.0,
        "linear_acceleration": 0.0,
        "angular_acceleration_rad": 0.0,
    }
    maximum_bones = {key: "" for key in maxima}
    landmarks: dict[str, dict[str, float]] = {}
    repeat_translation_maximum = 0.0
    repeat_rotation_maximum = 0.0
    foot_boundaries: dict[str, dict[str, float]] = {}
    for name in rows[0]:
        first, second, third, antepenultimate, penultimate, last, repeat_first, repeat_second, repeat_last = (
            row[name] for row in rows
        )
        endpoint_translation = (last.translation - first.translation).length
        endpoint_rotation = _shortest_rotation_difference(first.to_quaternion(), last.to_quaternion()).angle
        outgoing_linear = second.translation - first.translation
        incoming_linear = last.translation - penultimate.translation
        linear_velocity = (incoming_linear - outgoing_linear).length
        outgoing_angular = _root_space_rotation_delta(first.to_quaternion(), second.to_quaternion())
        incoming_angular = _root_space_rotation_delta(penultimate.to_quaternion(), last.to_quaternion())
        angular_velocity = _shortest_rotation_difference(outgoing_angular, incoming_angular).angle
        linear_acceleration = (
            (last.translation - 2.0 * penultimate.translation + antepenultimate.translation)
            - (third.translation - 2.0 * second.translation + first.translation)
        ).length
        angular_acceleration = _shortest_rotation_difference(
            _root_space_rotation_delta(
                _root_space_rotation_delta(antepenultimate.to_quaternion(), penultimate.to_quaternion()),
                incoming_angular,
            ),
            _root_space_rotation_delta(
                outgoing_angular,
                _root_space_rotation_delta(second.to_quaternion(), third.to_quaternion()),
            ),
        ).angle
        repeat_translation = max(
            (repeat_first.translation - last.translation).length,
            (repeat_second.translation - second.translation).length,
            (repeat_last.translation - last.translation).length,
        )
        repeat_rotation = max(
            _shortest_rotation_difference(repeat_first.to_quaternion(), last.to_quaternion()).angle,
            _shortest_rotation_difference(repeat_second.to_quaternion(), second.to_quaternion()).angle,
            _shortest_rotation_difference(repeat_last.to_quaternion(), last.to_quaternion()).angle,
        )
        repeat_translation_maximum = max(repeat_translation_maximum, repeat_translation)
        repeat_rotation_maximum = max(repeat_rotation_maximum, repeat_rotation)
        values = {
            "pose_translation": endpoint_translation,
            "pose_rotation_rad": endpoint_rotation,
            "linear_velocity": linear_velocity,
            "angular_velocity_rad": angular_velocity,
            "linear_acceleration": linear_acceleration,
            "angular_acceleration_rad": angular_acceleration,
        }
        for key, value in values.items():
            if value > maxima[key]:
                maxima[key] = value
                maximum_bones[key] = name
        if name in {"Hips", "Chest", "Head", "LeftFoot", "RightFoot"}:
            landmarks[name] = values
        if name in {"LeftFoot", "RightFoot"}:
            foot_boundaries[name] = {
                "incoming_speed": incoming_linear.length,
                "outgoing_speed": outgoing_linear.length,
                "velocity_dot": incoming_linear.dot(outgoing_linear),
                "linear_velocity_delta": linear_velocity,
            }

    curves = _curves(action)
    root_accumulation = {"translation": 0.0, "yaw": 0.0}
    for axis in range(3):
        curve = curves[(f"{ROOT}.location", axis)]
        start, endpoint = float(curve.evaluate(1)), float(curve.evaluate(end))
        expected = start + 2.0 * (endpoint - start)
        root_accumulation["translation"] = max(
            root_accumulation["translation"], abs(float(curve.evaluate(end + period)) - expected)
        )
    yaw_curve = curves[(f"{ROOT}.rotation_euler", 1)]
    yaw_start, yaw_endpoint = float(yaw_curve.evaluate(1)), float(yaw_curve.evaluate(end))
    yaw_expected = yaw_start + 2.0 * (yaw_endpoint - yaw_start)
    root_accumulation["yaw"] = abs(float(yaw_curve.evaluate(end + period)) - yaw_expected)
    cycle_delta = Vector((
        float(curves[(f"{ROOT}.location", 0)].evaluate(end) - curves[(f"{ROOT}.location", 0)].evaluate(1)),
        float(curves[(f"{ROOT}.location", 2)].evaluate(end) - curves[(f"{ROOT}.location", 2)].evaluate(1)),
    ))
    yaw_delta = yaw_endpoint - yaw_start
    semantic_endpoints = [
        list(point) for point in compose_local_arc_cycles((cycle_delta.x, cycle_delta.y), yaw_delta)
    ]
    same_chord_endpoints = [list(cycle_delta * cycle) for cycle in (1.0, 2.0, 3.0)]
    composition = {
        "contract": "local_cycle_se2_translation_plus_accumulated_yaw_v1",
        "root_channel_mode": "REPEAT_OFFSET unit delta; runtime composes each local cycle under accumulated heading",
        "cycle_translation_local": list(cycle_delta),
        "cycle_yaw_rad": yaw_delta,
        "semantic_cycle_endpoints": semantic_endpoints,
        "same_chord_cycle_endpoints": same_chord_endpoints,
        "same_chord_third_cycle_error": (Vector(semantic_endpoints[-1]) - Vector(same_chord_endpoints[-1])).length,
        "third_cycle_yaw_rad": 3.0 * yaw_delta,
        "wrong_reset_yaw_error_rad": abs(3.0 * yaw_delta - yaw_delta),
    }
    return {
        "maxima": maxima,
        "maximum_bones": maximum_bones,
        "landmarks": landmarks,
        "foot_boundaries": foot_boundaries,
        "cycle_extension_fidelity": {
            "second_cycle_pose_translation": repeat_translation_maximum,
            "second_cycle_pose_rotation_rad": repeat_rotation_maximum,
            "root_second_cycle_accumulation_error": root_accumulation,
            "root_three_cycle_composition": composition,
        },
    }


def _validate_loop_hierarchy_seam(
    rig: Any, action: Any, role: Any | None = None,
) -> dict[str, Any]:
    """Validate the declared loop-closure family and cycle extension fidelity."""
    metrics = _loop_hierarchy_seam_metrics(rig, action)
    scale = _anatomical_character_scale(rig)
    closure = "generated_recurrence" if role is None else role_projection_policy(role).loop_closure
    if closure == "complete_event_repeat_offset":
        fidelity_failures = _cycle_extension_fidelity_failures(metrics["cycle_extension_fidelity"], scale)
        decision = {
            "accepted": not fidelity_failures,
            "contract": "precision_complete_event_repeat_offset_closure_v1",
            "diagnostic": "generated precision complete-event periodic closure",
            "gait_recurrence_not_applicable": True,
            "cycle_extension_fidelity_failures": fidelity_failures,
        }
        metrics["precision_complete_event_closure"] = decision
        if fidelity_failures:
            raise MaterialisationError(
                f"{action.name} precision complete-event closure failed: {fidelity_failures}"
            )
        return metrics
    if closure != "generated_recurrence":
        raise MaterialisationError(f"{action.name} has unknown loop closure policy: {closure}")
    world_decision = recurrence_acceptance(metrics["maxima"], scale, "generated recurrence")
    if role is not None and _uses_bounded_loop_repair(role):
        end = int(action.frame_range[1])
        relative_rows = _loop_relative_samples(rig, action, range(1, end + 1))
        local_rows = _hierarchy_basis_rows(rig, relative_rows)
        local_decision = _natural_recurrence_evidence(local_rows, scale)
        decision = {
            **local_decision,
            "contract": "pose_local_c1_with_articulated_world_contact_recurrence_v1",
            "coordinate_space": "pose-local basis through real parent/rest hierarchy",
            "articulated_world_derivative_diagnostic": world_decision,
        }
    else:
        decision = world_decision
    foot_failures = {}
    for name, boundary in metrics["foot_boundaries"].items():
        reversal, spike = foot_recurrence_flags(
            boundary["incoming_speed"], boundary["outgoing_speed"], boundary["velocity_dot"],
            boundary["linear_velocity_delta"], scale,
        )
        boundary.update({"reversal": reversal, "spike": spike})
        if reversal or spike:
            foot_failures[name] = boundary
    decision["foot_failures"] = foot_failures
    decision["accepted"] = bool(decision["accepted"]) and not foot_failures
    metrics["generated_recurrence"] = decision
    if not decision["accepted"]:
        failures = decision["failures"]
        bones = {key: metrics["maximum_bones"].get(key) for key in failures}
        raise MaterialisationError(
            f"{action.name} generated recurrence failed {RECURRENCE_CONTRACT}: "
            f"metric_failures={failures}; foot_failures={foot_failures}; "
            f"bones={bones}; landmarks={metrics['landmarks']}"
        )
    fidelity_failures = _cycle_extension_fidelity_failures(metrics["cycle_extension_fidelity"], scale)
    if fidelity_failures:
        raise MaterialisationError(
            f"{action.name} cycle extension fidelity failed: {fidelity_failures}"
        )
    return metrics


def _cycle_extension_fidelity_failures(fidelity: Mapping[str, Any], scale: float) -> dict[str, Any]:
    """Reject broken REPEAT/REPEAT_OFFSET extension for every loop family."""
    failures: dict[str, Any] = {}
    if fidelity["second_cycle_pose_translation"] > DEFAULT_TOLERANCES["reconstruction_translation_tolerance"]:
        failures["second_cycle_pose_translation"] = fidelity["second_cycle_pose_translation"]
    if fidelity["second_cycle_pose_rotation_rad"] > DEFAULT_TOLERANCES["reconstruction_rotation_tolerance"]:
        failures["second_cycle_pose_rotation_rad"] = fidelity["second_cycle_pose_rotation_rad"]
    accumulation = fidelity["root_second_cycle_accumulation_error"]
    for key, value in accumulation.items():
        tolerance = DEFAULT_TOLERANCES[
            "reconstruction_rotation_tolerance" if key == "yaw" else "reconstruction_translation_tolerance"
        ]
        if value > tolerance:
            failures[f"root_{key}_accumulation"] = value
    composition = fidelity["root_three_cycle_composition"]
    cycle_translation = Vector(composition["cycle_translation_local"])
    if abs(float(composition["cycle_yaw_rad"])) > 0.10 and cycle_translation.length > scale * .05:
        if float(composition["same_chord_third_cycle_error"]) <= scale * .01:
            failures["arc_same_chord_composition_not_rejected"] = composition
        if float(composition["wrong_reset_yaw_error_rad"]) <= 0.10:
            failures["arc_reset_yaw_composition_not_rejected"] = composition
    return failures


def _make(role: Any, source: Any, reference: Any, start: int, end: int, rig: Any, up: int, rig_basis: Any, variant: str, root: list[list[float]], source_carriage: list[list[float]], source_residuals: list[Any], source_yaw: list[float], reference_residual: Any, complete_source_window: bool, seam_evidence: list[dict[str, Any]], pivot_oracle: dict[str, Any] | None = None, pivot_projection: PivotHeadingProjection | None = None, name: str | None = None, repair_window_frames: int | None = None, repair_blend_profile: str = "quadratic") -> Any:
    """Materialise one role, constructing bounded non-confined periodic pose when declared."""
    action, bag = _new_action(name or action_name(variant, role), rig)
    source_curves, frames = _curves(source), list(range(start, end + 1))
    if _reverses_time(role.derivation): frames.reverse()
    if any((f"{HIPS}.location", index) not in source_curves for index in range(3)):
        raise MaterialisationError(f"{source.name} lacks a required Hips location channel")
    values_by_curve: dict[tuple[str, int], list[float]] = {}
    for (path, index), source_curve in sorted(source_curves.items()):
        if path.startswith(ROOT): continue
        values = [_value(path, index, float(source_curve.evaluate(frame)), role.derivation) for frame in frames]
        values_by_curve[(_mirror_path(path) if "mirror" in role.derivation else path, index)] = values
    root, yaw = _reconstruct_root(role, root, source_yaw, pivot_projection)
    for index, values in enumerate(root): values_by_curve[(f"{ROOT}.location", index)] = values
    # Author all Root Euler axes explicitly. This keeps the evaluated ZYX path
    # deterministic and makes an injected roll/pitch channel observable to
    # staged validation rather than relying on an absent-property default.
    for index in range(3):
        values_by_curve[(f"{ROOT}.rotation_euler", index)] = [0.0] * len(frames)
    values_by_curve[(f"{ROOT}.rotation_euler", up)] = yaw
    policy = role_projection_policy(role)
    if policy.reconstruct_full_hierarchy:
        if pivot_oracle is None:
            raise MaterialisationError(f"{role.code} requires an immutable full-hierarchy reconstruction oracle")
        values_by_curve.update(
            _full_transform_channels(
                rig, role, pivot_oracle, "mirror" in role.derivation, root, yaw,
            )
        )

    # First evaluate the generated Root and all non-Hips source pose channels.
    # Then express the source Hips gait residual in that Root frame and convert
    # the resulting armature-space target through the real parent/rest chain.
    # This is intentionally transform-space work: raw Hips channel indices do
    # not share a coordinate system with an evaluated Root trajectory.
    for (path, index), values in sorted(values_by_curve.items()):
        _curve(bag, path, index, values, False)
    # Only a heading actually promoted to Root may be removed from Hips. Walks
    # and transitions retain their source-backed body orientation unchanged.
    if not policy.reconstruct_full_hierarchy:
        promoted_yaw = yaw if policy.promote_global_z_yaw else [0.0] * len(yaw)
        residuals = [
            # ``residual`` is already Root-pose-relative. Remove promoted
            # heading on Root local Y; a global-Z matrix here is the wrong
            # comparison frame for a non-identity Root rest basis.
            Matrix.Rotation(-yaw_value, 4, "Y")
            @ residual
            for residual, yaw_value in zip(source_residuals, promoted_yaw)
        ]
        values_by_curve.update(_hips_pose_channels(rig, action, list(range(1, len(frames) + 1)), residuals, reference_residual, source_carriage, root, rig_basis))

    if _uses_bounded_loop_repair(role):
        # Evaluate the complete pre-repair hierarchy first. Root carriage stays
        # immutable and separate; only its periodic non-Root relative pose is
        # repaired and solved back through the actual rest/parent chain.
        for curve in list(_action_fcurves(action)):
            bag.fcurves.remove(curve)
        for (path, index), values in sorted(values_by_curve.items()):
            _curve(bag, path, index, values, False)
        repaired_channels, repair_evidence = _repair_generated_non_root_channels(
            rig, action, len(frames),
            window_frames=repair_window_frames,
            blend_profile=repair_blend_profile,
        )
        values_by_curve.update(repaired_channels)
        if _uses_repaired_walk_root(role):
            root, stage2_channels, stage2_evidence = _walk_root_from_repaired_hips(
                rig, role, repair_evidence, rig_basis,
            )
            for index, values in enumerate(root):
                values_by_curve[(f"{ROOT}.location", index)] = values
            values_by_curve.update(stage2_channels)
            repair_evidence["root_projection_stage"] = "complete_repaired_hips_line_projection"
            repair_evidence["root_projection"] = stage2_evidence
        elif policy.family == "arc":
            root, yaw, arc_channels, arc_evidence = _arc_root_from_repaired_hips(
                rig, role, repair_evidence, rig_basis,
            )
            for index, values in enumerate(root):
                values_by_curve[(f"{ROOT}.location", index)] = values
            values_by_curve[(f"{ROOT}.rotation_euler", up)] = yaw
            values_by_curve.update(arc_channels)
            repair_evidence["root_projection_stage"] = "complete_repaired_hips_arc_projection"
            repair_evidence["root_projection"] = arc_evidence
        seam_evidence.append({"construction": repair_evidence})

    for curve in list(_action_fcurves(action)):
        bag.fcurves.remove(curve)
    for (path, index), values in sorted(values_by_curve.items()):
        smoothed = values
        accumulated_root = (
            path == f"{ROOT}.location"
            or (
                path == f"{ROOT}.rotation_euler"
                and index == 1
                and any(abs(value) > 1e-8 for value in smoothed)
            )
        )
        _curve(bag, path, index, smoothed, role.is_loop, accumulated_root)
    if _uses_bounded_loop_repair(role):
        construction = seam_evidence[-1]["construction"]
        window_ranges = construction["window_ranges_zero_based"]
        try:
            validate_durable_repair_windows(
                window_ranges["beginning"], construction["immutable_interior"], window_ranges["ending"],
                sample_count=len(frames), opening_width=int(window_ranges["beginning"][1]) + 1,
                closing_width=len(frames) - int(window_ranges["ending"][0]),
            )
        except ValueError as error:
            raise MaterialisationError(
                f"{action.name} has invalid durable loop-repair provenance: {error}"
            ) from error
        sample_count = len(frames)
        selection = construction.get("repair_option_selection", {})
        durable_repair = {
            "contract": REPAIR_PROVENANCE_CONTRACT,
            "algorithm": construction["contract"],
            "blend_profile": construction["blend_profile"],
            "opening_support": {
                "window": window_ranges["beginning"],
                "width": int(window_ranges["beginning"][1]) + 1,
            },
            "closing_support": {
                "window": window_ranges["ending"],
                "width": sample_count - int(window_ranges["ending"][0]),
            },
            "immutable_interior": construction["immutable_interior"],
            "repair_cost": construction["repair_cost"],
            "selection": {
                "contract": selection.get("contract", "bounded_duration_scaled_repaired_output_naturalness_v1"),
                "selected_rank_key": selection.get(
                    "selected_rank_key", list(repair_naturalness_key(construction)),
                ),
            },
            "transform_derivation": {
                "source_range": [start, end],
                "frame_mapping": _frame_mapping(start, end, role.derivation),
                "role_derivation": role.derivation,
                "temporal_order": "reversed" if _reverses_time(role.derivation) else "preserved",
                "anatomical_mapping": "bilateral_mirror" if "mirror" in role.derivation else "preserved",
                "evaluated_space": "generated Root-relative evaluated hierarchy",
                "repaired_domain": "complete non-Root motion hierarchy",
                "root_handling": construction.get("root_projection_stage", "immutable generated Root carriage"),
            },
            "sample_semantics": {
                "conceptual_sample_count": sample_count,
                "unique_runtime_sample_count": sample_count - 1,
                "duplicate_endpoint": True,
                "conceptual_boundary_sample": sample_count - 1,
                "runtime_interval": [0, sample_count - 2],
            },
        }
        try:
            validate_loop_repair_provenance(durable_repair, conceptual_sample_count=sample_count)
        except ValueError as error:
            raise MaterialisationError(
                f"{action.name} has invalid durable selected loop-repair provenance: {error}"
            ) from error
        action["anim002_loop_repair_evidence"] = json.dumps(
            durable_repair, sort_keys=True, separators=(",", ":"),
        )
        frozen_rows = construction.pop("_frozen_repaired_relative_rows")
        frozen_armature_rows = construction.pop("_frozen_repaired_armature_rows")
        actual_rows = _loop_relative_samples(rig, action, range(1, len(frames) + 1))
        if _uses_repaired_walk_root(role):
            stage2_expected = construction["root_projection"].pop("_frozen_aligned_armature_rows")
            repaired_transform_rows = stage2_expected
            actual_armature_rows = _loop_armature_samples(rig, action, range(1, len(frames) + 1))
            reconstruction_error = max(
                _matrix_error(actual[name], expected[name])
                for actual, expected in zip(actual_armature_rows, stage2_expected)
                for name in expected
            )
        elif policy.family == "arc":
            repaired_transform_rows = construction["root_projection"].pop("_frozen_repaired_armature_rows")
            actual_armature_rows = _loop_armature_samples(rig, action, range(1, len(frames) + 1))
            reconstruction_error = max(
                _matrix_error(actual[name], expected[name])
                for actual, expected in zip(actual_armature_rows, repaired_transform_rows)
                for name in expected
            )
        else:
            repaired_transform_rows = frozen_armature_rows
            reconstruction_error = max(
                _matrix_error(actual[name], expected[name])
                for actual, expected in zip(actual_rows, frozen_rows)
                for name in expected
            )
        if pivot_oracle is None:
            raise MaterialisationError(f"{role.code} lacks its immutable transform oracle")
        required_oracle_names = ("Hips", "Chest", "Head", "LeftFoot", "RightFoot")
        pivot_oracle["repaired_transform_rows"] = tuple(
            {name: row[name].copy() for name in required_oracle_names}
            for row in repaired_transform_rows
        )
        # Freeze Blender's authored single-precision representation, not the
        # pre-write Python doubles. This remains an immutable pre-publication
        # oracle while avoiding false failures for legitimate sub-1e-7 source
        # projections after save/reopen.
        authored_curves = _curves(action)
        pivot_oracle["repaired_root_translation"] = tuple(
            tuple(float(authored_curves[(f"{ROOT}.location", axis)].evaluate(frame + 1)) for axis in range(3))
            for frame in range(len(frames))
        )
        construction["repaired_transform_oracle"] = _serialise_repaired_transform_oracle(
            repaired_transform_rows,
            tuple(
                tuple(float(root[axis][frame]) for axis in range(3))
                for frame in range(len(frames))
            ),
            "post_derivation_repair_alignment_full_transform_v1",
        )
        construction["maximum_hierarchy_reconstruction_error"] = reconstruction_error
        construction["hierarchy_reconstruction_tolerance"] = DEFAULT_TOLERANCES["transform_oracle_tolerance"]
        if reconstruction_error > DEFAULT_TOLERANCES["transform_oracle_tolerance"]:
            raise MaterialisationError(
                f"{action.name} cannot reconstruct repaired hierarchy transforms ({reconstruction_error:.9f})"
            )
    if role.is_loop:
        post_metrics = _validate_loop_hierarchy_seam(rig, action, role)
        if "generated_recurrence" in post_metrics:
            seam_evidence.append({
                "generated_recurrence": post_metrics["generated_recurrence"],
                "cycle_extension_fidelity": post_metrics["cycle_extension_fidelity"],
            })
        else:
            seam_evidence.append({
                "loop_closure": post_metrics["precision_complete_event_closure"],
                "cycle_extension_fidelity": post_metrics["cycle_extension_fidelity"],
            })
    translation, yaw_kind = _trajectory(role.code)
    action["anim002_root_translation_trajectory"], action["anim002_root_yaw_trajectory"] = translation, yaw_kind
    action["anim002_phase"] = role.code.rsplit("_", 1)[-1] if "_" in role.code else "natural"
    action["anim002_boundary"] = "loop" if role.is_loop else "transition"
    action["anim002_left_contact"], action["anim002_right_contact"] = "undetermined", "undetermined"
    action["anim002_left_contact_confidence"], action["anim002_right_contact_confidence"] = 0.0, 0.0
    action["anim002_source_window_complete"] = complete_source_window
    action["anim002_endpoint_displacement"], action["anim002_endpoint_yaw"] = [values[-1] for values in root], yaw[-1]
    frame_start, frame_end = (int(value) for value in action.frame_range)
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    action["anim002_transport_contract"] = json.dumps(
        make_transport_evidence(
            frame_start=frame_start, frame_end=frame_end, fps=fps, is_loop=role.is_loop,
        ),
        sort_keys=True, separators=(",", ":"),
    )
    return action


def _evaluated_world_samples(rig: Any, action: Any, end: int) -> list[tuple[Vector, Vector, Vector, Vector]]:
    """Sample evaluated Root, Hips, and feet in world space using its Action slot."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        samples = []
        for frame in range(1, end + 1):
            bpy.context.scene.frame_set(frame)
            evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
            root = evaluated.matrix_world @ evaluated.pose.bones["Root"].matrix.translation
            hips = evaluated.matrix_world @ evaluated.pose.bones["Hips"].matrix.translation
            left_foot = evaluated.matrix_world @ evaluated.pose.bones["LeftFoot"].matrix.translation
            right_foot = evaluated.matrix_world @ evaluated.pose.bones["RightFoot"].matrix.translation
            samples.append((root, hips, left_foot, right_foot))
        return samples
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)


def _strafe_foot_oracle(rig: Any, source: Any, generated: Any, frames: list[int], root: list[list[float]], rig_basis: Any, mirror: bool) -> dict[str, Any]:
    """Compare source-relative plant proxies with generated evaluated foot carriage.

    This intentionally does not assert physical contact.  A plant interval is a
    source-relative low-velocity, low-height *proxy*; generated horizontal
    motion is compared against the source carriage plus evaluated Root travel.
    Thus a strafe's intended lateral Root displacement is not reported as skate.
    """
    end = int(generated.frame_range[1])
    if end < 2 or len(frames) != end or len(root[0]) != end:
        raise MaterialisationError(f"{generated.name} cannot build a strafe foot oracle for mismatched samples")
    animation, previous_action, previous_frame = rig.animation_data_create(), rig.animation_data.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, source)
        source_feet: dict[str, list[Vector]] = {"Left": [], "Right": []}
        source_hips: list[Vector] = []
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
            def source_point(bone: str) -> Vector:
                point = evaluated.pose.bones[bone].matrix.translation.copy()
                if mirror:
                    point.x = -point.x
                return evaluated.matrix_world @ point
            source_feet["Left"].append(source_point("RightFoot" if mirror else "LeftFoot"))
            source_feet["Right"].append(source_point("LeftFoot" if mirror else "RightFoot"))
            source_hips.append(source_point("Hips"))
        generated_samples = _evaluated_world_samples(rig, generated, end)
        world_up = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
        scale = _anatomical_character_scale(rig)
        if world_up.length <= 1e-6:
            raise MaterialisationError(f"{generated.name} cannot establish strafe oracle geometry")
        # The source Hips trace is the evaluated locomotion reference.  It is
        # not an assertion that contact was solved, nor a reimplementation of
        # Root reconstruction through raw F-curves.
        source_root = [point - source_hips[0] for point in source_hips]
        proxy_speed_limit, proxy_height_limit = .08 * scale, .10 * scale
        tolerance = .12 * scale
        result: dict[str, Any] = {"contract": "evaluated_world_space_source_relative_plant_proxy", "contact_evaluated": False, "scale": scale, "proxy": {"horizontal_speed_maximum": proxy_speed_limit, "height_above_lowest_foot_maximum": proxy_height_limit}, "unintended_skate_tolerance": tolerance, "feet": {}}
        preserved_end = end
        result["source_oracle_compared_frames"] = [1, end]
        generated_curves = _curves(generated)
        root_error = max(
            abs(float(generated_curves[(f"{ROOT}.location", axis)].evaluate(frame)) - (root[axis][frame - 1] if axis == 0 else 0.0))
            for axis in range(3) for frame in range(1, end + 1)
        )
        result["root_projection_curve_error"] = root_error
        for side, foot_index in (("Left", 2), ("Right", 3)):
            candidates: list[int] = []
            for index in range(1, end):
                if index + 1 > preserved_end:
                    continue
                source_delta = source_feet[side][index] - source_feet[side][index - 1] - (source_root[index] - source_root[index - 1])
                horizontal = source_delta - world_up * source_delta.dot(world_up)
                lowest = min(source_feet["Left"][index].dot(world_up), source_feet["Right"][index].dot(world_up))
                if horizontal.length <= proxy_speed_limit and source_feet[side][index].dot(world_up) - lowest <= proxy_height_limit:
                    candidates.append(index)
            intervals: list[list[int]] = []
            for index in candidates:
                if not intervals or index != intervals[-1][-1] + 1:
                    intervals.append([index])
                else:
                    intervals[-1].append(index)
            rows = []
            for interval in intervals:
                if len(interval) < 2:
                    continue
                first, last = interval[0] - 1, interval[-1]
                source_carriage = source_feet[side][last] - source_feet[side][first] - (source_root[last] - source_root[first])
                generated_foot = generated_samples[last][foot_index] - generated_samples[first][foot_index]
                generated_carriage = generated_foot - (generated_samples[last][1] - generated_samples[first][1])
                source_horizontal = source_carriage - world_up * source_carriage.dot(world_up)
                generated_horizontal = generated_carriage - world_up * generated_carriage.dot(world_up)
                excess = generated_horizontal - source_horizontal
                rows.append({"frames": [first + 1, last + 1], "source_carriage_horizontal": list(source_horizontal), "generated_carriage_horizontal": list(generated_horizontal), "unintended_skate_horizontal": list(excess), "unintended_skate": excess.length, "intended_root_carriage": (generated_samples[last][0] - generated_samples[first][0]).length})
            result["feet"][side] = {"plant_proxy_intervals": rows, "maximum_unintended_skate": max((row["unintended_skate"] for row in rows), default=0.0), "accepted": all(row["unintended_skate"] <= tolerance for row in rows)}
        result["accepted"] = root_error <= 1e-5 and all(row["accepted"] for row in result["feet"].values())
        return result
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)


def _validate_body_carriage(action: Any, role: Any, rig: Any, rig_basis: Any, end: int, tolerances: dict[str, float]) -> None:
    """Reject double-applied Root travel using evaluated character geometry."""
    samples = _evaluated_world_samples(rig, action, end)
    root_start, hips_start, left_start, right_start = samples[0]
    relative_start = hips_start - root_start
    world_up = (rig.matrix_world.to_3x3() @ rig_basis @ Vector((0.0, 1.0, 0.0))).normalized()
    if world_up.length <= 1e-6:
        raise MaterialisationError(f"{action.name} has no evaluable vertical basis")
    horizontal_residuals = []
    vertical_residuals = []
    foot_verticals = {"LeftFoot": [], "RightFoot": []}
    for root, hips, left_foot, right_foot in samples:
        relative = hips - root
        residual = relative - relative_start
        vertical = residual.dot(world_up)
        horizontal = residual - world_up * vertical
        horizontal_residuals.append(horizontal)
        vertical_residuals.append(vertical)
        if relative.dot(world_up) < tolerances["body_ground_clearance_minimum"]:
            raise MaterialisationError(f"{action.name} has unreasonable evaluated Hips ground clearance")
        foot_verticals["LeftFoot"].append((left_foot - left_start).dot(world_up))
        foot_verticals["RightFoot"].append((right_foot - right_start).dot(world_up))
    maximum = max(vector.length for vector in horizontal_residuals)
    if max(abs(value) for value in vertical_residuals) > tolerances["body_vertical_residual_maximum"]:
        raise MaterialisationError(f"{action.name} has unreasonable evaluated vertical Hips motion")
    if role_projection_policy(role).family in {"walk", "walk_transition"} and maximum > tolerances["body_horizontal_residual_maximum"]:
        raise MaterialisationError(f"{action.name} has excessive evaluated Hips-relative-to-Root horizontal residual: {maximum:.3f} m")
    for foot, values in foot_verticals.items():
        if max(abs(value) for value in values) > tolerances["foot_vertical_residual_maximum"]:
            raise MaterialisationError(f"{action.name} has unreasonable evaluated {foot} vertical motion")

    translation, _ = _trajectory(role.code)
    if translation == "zero_translation":
        return
    expected, _ = _validation_expectation(role.code, rig_basis)
    world_direction = (rig.matrix_world.to_3x3() @ expected).normalized()
    if world_direction.length <= 1e-6:
        raise MaterialisationError(f"{action.name} has no evaluable Root travel direction")
    longitudinal = [residual.dot(world_direction) for residual in horizontal_residuals]
    if role.code.startswith(("wf", "wb", "i2f", "f2i", "i2b", "b2i")) and max(abs(value) for value in longitudinal) > tolerances["body_longitudinal_residual_maximum"]:
        raise MaterialisationError(f"{action.name} has excessive evaluated Hips-relative-to-Root longitudinal residual")
    # Hips world travel must be Root travel plus bounded gait residual, not a
    # second accumulated travel curve.  A large late return to Root is the
    # characteristic endpoint-masked double-travel failure.
    furthest = max(longitudinal, key=abs)
    if role.code.startswith(("wf", "wb", "i2f", "f2i", "i2b", "b2i")) and abs(furthest) > tolerances["body_drift_recovery_maximum"] and abs(longitudinal[-1]) < abs(furthest) - tolerances["body_drift_recovery_maximum"]:
        raise MaterialisationError(f"{action.name} has a late recovery of accumulated Hips-vs-Root drift")


def _validate_frame_one_centring(action: Any, reference: Any, rig: Any, mirror: bool, tolerance: float) -> None:
    """Ensure generated frame one is centred as the reference body, in geometry."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    reflection = Matrix.Diagonal((-1.0, 1.0, 1.0, 1.0))
    try:
        _activate_action(rig, reference)
        bpy.context.scene.frame_set(1)
        reference_relative = rig.pose.bones["Root"].matrix.inverted_safe() @ rig.pose.bones["Hips"].matrix
        if mirror:
            reference_relative = reflection @ reference_relative @ reflection
        _activate_action(rig, action)
        bpy.context.scene.frame_set(1)
        expected = rig.matrix_world @ (rig.pose.bones["Root"].matrix @ reference_relative).translation
        actual = rig.matrix_world @ rig.pose.bones["Hips"].matrix.translation
        difference = (actual - expected).length
        if difference > tolerance:
            raise MaterialisationError(f"{action.name} Hips frame one is not centred at the reference pose ({difference:.4f} m)")
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)


def _validate_pivot_root_transform(action: Any, rig: Any, end: int, sign: float, tolerance: float) -> None:
    """Prove bounded, monotonic Root-local-Y units and evaluated pure RotY."""
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        bpy.context.scene.frame_set(1)
        rest = rig.pose.bones["Root"].matrix.copy()
        curves = _curves(action)
        yaw = curves[(f"{ROOT}.rotation_euler", 1)]
        endpoint = sign * math.pi / 4.0
        # Inspect authored keys as well as evaluated samples. Expected theta is
        # derived from the published unit contract, not from the generated yaw
        # curve, so a self-consistent multi-turn curve cannot pass.
        raw = [float(point.co.y) for point in yaw.keyframe_points]
        if len(raw) != end or any(value * sign < -tolerance or value * sign > math.pi / 4.0 + tolerance for value in raw):
            raise MaterialisationError(f"{action.name} has out-of-bounds raw Root local-Y pivot keys")
        if any((following - preceding) * sign < -tolerance for preceding, following in zip(raw, raw[1:])):
            raise MaterialisationError(f"{action.name} has non-monotonic raw Root local-Y pivot keys")
        for frame in range(1, end + 1):
            value = float(yaw.evaluate(frame))
            if value * sign < -tolerance or value * sign > math.pi / 4.0 + tolerance or abs(value - raw[frame - 1]) > tolerance:
                raise MaterialisationError(f"{action.name} violates its bounded monotonic first Root local-Y unit at frame {frame}")
        for frame in range(end, 1 + 2 * (end - 1) + 1):
            value = float(yaw.evaluate(frame))
            source_index = (frame - 1) % (end - 1)
            cycle = (frame - 1) // (end - 1)
            expected_theta = cycle * endpoint + (0.0 if source_index == 0 else raw[source_index])
            if value * sign < math.pi / 4.0 - tolerance or value * sign > math.pi / 2.0 + tolerance or abs(value - expected_theta) > tolerance:
                raise MaterialisationError(f"{action.name} violates its bounded accumulated second Root local-Y unit at frame {frame}")
        for index in (0, 2):
            curve = curves.get((f"{ROOT}.rotation_euler", index))
            if curve is None:
                raise MaterialisationError(f"{action.name} has forbidden Root pivot swing")
            assert curve is not None
            if any(abs(float(curve.evaluate(frame))) > tolerance for frame in range(1, end + 1)):
                raise MaterialisationError(f"{action.name} has forbidden Root pivot swing")
        for index in range(3):
            curve = curves.get((f"{ROOT}.location", index))
            if curve is None:
                raise MaterialisationError(f"{action.name} has forbidden Root pivot translation")
            assert curve is not None
            if any(abs(float(curve.evaluate(frame))) > tolerance for frame in range(1, end + 1)):
                raise MaterialisationError(f"{action.name} has forbidden Root pivot translation")
        for frame in (1, max(2, end // 2), end, 1 + (end - 1) + max(1, (end - 1) // 2), 1 + 2 * (end - 1)):
            bpy.context.scene.frame_set(frame)
            actual = rest.inverted_safe() @ rig.pose.bones["Root"].matrix
            expected = Matrix.Rotation(float(yaw.evaluate(frame)), 4, "Y")
            error = max(
                abs(actual[row][column] - expected[row][column])
                for row in range(4)
                for column in range(4)
            )
            if error > tolerance:
                raise MaterialisationError(
                    f"{action.name} violates evaluated Root rest * RotY at frame {frame} ({error:.6f})"
                )
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()


def _validation_expectation(code: str, rig_basis: Any) -> tuple[Vector, int | None]:
    """Independently state role direction in evaluated rig space for validation."""
    # The diagnostic/reference convention declares Root local +Z as forward;
    # transform it through the actual rest basis before evaluating a result.
    forward, right = rig_basis @ Vector((0.0, 0.0, 1.0)), rig_basis @ Vector((-1.0, 0.0, 0.0))
    if code.startswith(("wf", "i2f", "f2i")): return forward, 0
    if code.startswith(("wb", "i2b", "b2i")): return -forward, 0
    if code.startswith(("xr", "sr")): return right, 0
    if code.startswith(("xl", "sl")): return -right, 0
    if code.startswith("ar"): return (forward + right).normalized(), None
    if code.startswith("al"): return (forward - right).normalized(), None
    if code in {"pr45", "pl45"}: return Vector(), int(pivot_root_yaw_sign(code))
    raise MaterialisationError(f"unrecognised ANIM-002 Root trajectory role: {code}")


def _validate_walk_phase(action: Any, role_code: str, rig: Any, rig_basis: Any) -> None:
    """Validate one whole output cycle bounded by the named release recurrence."""
    code = role_code
    if code not in {"wf_r", "wf_l", "wb_r", "wb_l"}:
        return
    lead = "Right" if code.endswith("_r") else "Left"
    geometry = _foot_geometry(rig, action, 1, min(3, int(action.frame_range[1])), rig_basis)
    other = "Left" if lead == "Right" else "Right"
    if int(action.frame_range[1]) < 2 or abs(geometry[lead][2].speed) < abs(geometry[other][2].speed) + 1e-4:
        raise MaterialisationError(f"{action.name} suffix is not supported by evaluated {lead.lower()} release velocity")
    end = int(action.frame_range[1])
    complete_geometry = _foot_geometry(rig, action, 1, end, rig_basis)
    if abs(complete_geometry[lead][end].speed) < abs(complete_geometry[other][end].speed) + 1e-4:
        raise MaterialisationError(f"{action.name} does not retain the evaluated {lead.lower()} release at its recurrence boundary")


def _validate_transition_phase(action: Any, role_code: str, rig: Any, rig_basis: Any) -> None:
    """Verify one output phase, including boundary samples lacking clip context.

    Source selection already evaluates the predecessor/successor around an edge
    crossing.  The generated clip has no key outside its contractual interval,
    so its edge release is re-established from the retained evaluated velocity;
    all interior crossings are counted from generated geometry.
    """
    expectation = transition_boundary_phase(role_code)
    if expectation is None:
        return
    edge, lead = expectation
    end = int(action.frame_range[1])
    geometry = _foot_geometry(rig, action, 1, end, rig_basis)
    # The source window includes exactly one candidate at the locomotion-facing
    # edge.  The output geometry must retain that candidate; no metadata can
    # substitute for this velocity-hysteresis assertion.
    segment = {"start_frame": 1, "end_frame": end}
    events = [(side, frame) for side in ("Left", "Right") for frame in release_events(segment, geometry, side, role_code)]
    # Frame one has no predecessor in a sliced Action. A start-boundary
    # crossing may consequently be unobservable in the output interval; its
    # source proof is retained by selection. Generated output independently
    # proves the same direction-consistent release at its edge and permits at
    # most the one first-observable frame-two crossing.
    boundary_frame = end if edge == "end" else min(2, end)
    other = "Left" if lead == "Right" else "Right"
    sample = geometry[lead][boundary_frame]
    other_sample = geometry[other][boundary_frame]
    axis, direction = phase_semantics(role_code)
    axis_velocity = sample.velocity[0] if axis == "lateral" else sample.root_forward_velocity
    axis_confidence = abs(axis_velocity) / sample.speed if sample.speed > 1e-9 else 0.0
    has_boundary_release = (
        direction * axis_velocity >= RELEASE_FORWARD_MINIMUM
        and axis_confidence >= RELEASE_FORWARD_CONFIDENCE_MINIMUM
        and sample.speed >= other_sample.speed + RELEASE_SPEED_MARGIN
    )
    if not has_boundary_release:
        raise MaterialisationError(
            f"{action.name} suffix is not supported by an evaluated {lead.lower()} release at its {edge} boundary"
        )
    counted = set(events)
    selected_observable = (lead, end) if edge == "end" else (lead, 2)
    # Other-foot gait evidence is valid in a finite acceleration/deceleration
    # envelope. It must not displace or duplicate the selected boundary event.
    same_side = {event for event in counted if event[0] == lead}
    if same_side and selected_observable not in same_side:
        raise MaterialisationError(
            f"{action.name} generated crossings displace its selected {lead.lower()} boundary phase: {sorted(counted)}"
        )


def _source_walk_role(role: Any) -> str:
    """Select a source boundary that becomes the requested output phase."""
    # Segment 1 has exactly one selected native source.  ``sl`` is written by
    # reversing that complete sequence in time, never by selecting or mirroring
    # a left-anatomy source.
    if role.code in {"sr", "sl"}:
        return "sr"
    if role.code.startswith("xl") and "mirror" in role.derivation:
        return role.code.replace("xl", "xr", 1)
    if role.code.startswith("al_") and "mirror" in role.derivation:
        return f"ar_{'r' if role.code.endswith('_l') else 'l'}"
    if role.code.startswith(("wf", "wb", "i2f", "f2i", "i2b", "b2i")) and "mirror" in role.derivation:
        code = f"{role.code.rsplit('_', 1)[0]}_r"
    else:
        code = role.code
    # A reversed clip exchanges source start/end.  Choose its source window so
    # the published idle/locomotion boundary keeps the role's stated phase.
    if "reverse" in role.derivation:
        form, separator, side = code.rpartition("_")
        if separator and form == "wb":
            return f"wf_{side}"
        if separator and form in {"i2b", "b2i"}:
            return f"{'f2i' if form == 'i2b' else 'i2f'}_{side}"
    return code


def _reverses_time(derivation: str) -> bool:
    """Return whether a derivation reverses sample order without implying a mirror."""
    return "reverse" in derivation or derivation == "temporal_reversal"


def _frame_mapping(start: int, end: int, derivation: str) -> list[dict[str, int]]:
    """Persist the complete source-to-generated frame mapping for a slice."""
    source_frames = list(range(start, end + 1))
    if _reverses_time(derivation):
        source_frames.reverse()
    return [{"source_frame": frame, "output_frame": index + 1} for index, frame in enumerate(source_frames)]


def _source_loop_candidate_eligible(
    rig: Any, role: Any, action: Any, start: int, end: int, derivation: str, rig_basis: Any,
    *, repair_window_frames: int | None = None, repair_blend_profile: str = "quadratic",
    include_legacy_full_transform: bool = False,
) -> tuple[bool, dict[str, Any]]:
    """Assess post-role loop construction cost or an explicitly excluded policy."""
    frames = list(range(start, end + 1))
    if _reverses_time(derivation):
        frames.reverse()
    if len(frames) < 5:
        return False, {"accepted": False, "reason": "source loop has fewer than five samples"}
    animation = rig.animation_data_create()
    previous_action, previous_frame = animation.action, bpy.context.scene.frame_current
    try:
        _activate_action(rig, action)
        try:
            scale = _anatomical_character_scale(rig)
        except MaterialisationError as error:
            return False, {"accepted": False, "reason": f"source cycle has no usable anatomical character scale: {error}"}
        try:
            source_root, _, constant_alignment, travel_frame_evidence = _straight_source_projection(
                rig, role, action, frames, rig_basis,
            )
        except (SelectionError, MaterialisationError) as error:
            return False, {
                "accepted": False,
                "contract": "post_role_projection_pose_derivative_recurrence",
                "role": role.code,
                "source_range": [start, end],
                "reason": str(error),
            }
        _, source_yaw = _evaluated_body_relative(
            rig, action, frames, "mirror" in derivation, rig_basis,
            role_projection_policy(role).family in {"pivot", "arc"},
        )
        pivot_projection = None
        if role_projection_policy(role).family == "pivot":
            pivot_samples = _capture_pivot_transform_oracle(rig, action, frames)["samples"]
            try:
                pivot_projection = project_pivot_heading(
                    role.code,
                    [
                        (-1.0 if "mirror" in derivation else 1.0) * float(sample["global_z_heading"])
                        for sample in pivot_samples
                    ],
                )
            except ValueError as error:
                return False, {
                    "accepted": False,
                    "contract": "natural_generated_root_relative_pose_velocity_acceleration_recurrence",
                    "source_range": [start, end],
                    "reason": f"source-faithful pivot Root projection failed: {error}",
                }
        generated_root, generated_yaw = _reconstruct_root(role, source_root, source_yaw, pivot_projection)
        mirror = "mirror" in derivation
        samples: list[dict[str, Matrix]] = []
        names = _motion_bone_names(rig)
        alignment_origin: Vector | None = None
        opening_root: Vector | None = None
        for frame in frames:
            bpy.context.scene.frame_set(frame)
            if opening_root is None:
                _, source_root = role_evaluated_hierarchy_transform(
                    "Root", rig.pose.bones["Root"].matrix, mirror,
                )
                opening_root = source_root.translation.copy()
            index = len(samples)
            generated = (
                rig.data.bones["Root"].matrix_local
                @ Matrix.Translation(Vector((generated_root[0][index], generated_root[1][index], generated_root[2][index])))
                @ Matrix.Rotation(generated_yaw[index], 4, "Y")
            )
            row = {}
            for name in names:
                target_name, value = role_evaluated_hierarchy_transform(
                    name, rig.pose.bones[name].matrix, mirror,
                )
                if alignment_origin is None and name == "Hips":
                    alignment_origin = value.translation.copy()
                if alignment_origin is None:
                    # Hips is guaranteed to be in the motion hierarchy, but its
                    # lexical position is not an ordering contract.
                    _, hips_value = role_evaluated_hierarchy_transform(
                        "Hips", rig.pose.bones["Hips"].matrix, mirror,
                    )
                    alignment_origin = hips_value.translation.copy()
                value = _cycle_normalised_source_transform(
                    value, constant_alignment, alignment_origin, opening_root, rig_basis,
                )
                row[target_name] = generated.inverted_safe() @ value
            samples.append(row)
        if _uses_bounded_loop_repair(role):
            legacy_full_transform = None
            if include_legacy_full_transform:
                _, legacy_full_transform = _repair_periodic_pose_rows(samples, scale)
            _, repair = _repair_periodic_pose_rows(
                samples, scale,
                window_frames=repair_window_frames,
                blend_profile=repair_blend_profile,
                direct_safety_contract=True, rig=rig,
            )
            if legacy_full_transform is not None:
                repair["legacy_full_transform_diagnostic"] = {
                    "admission": "diagnostic_only",
                    "window_frames": legacy_full_transform["windows"]["beginning_frames"],
                    "blend_profile": legacy_full_transform["blend_profile"],
                    "metrics": legacy_full_transform["metrics"],
                    "failures": legacy_full_transform["failures"],
                    "repair_cost": legacy_full_transform["repair_cost"],
                }
            evidence = {
                **repair,
                "diagnostic": "required bounded post-role-derivation seam repair",
                "source_range": [start, end],
                "scale": scale,
                "character_scale_metres": scale,
                "scale_procedure": ANATOMICAL_SCALE_PROCEDURE,
                "role": role.code,
                "source_travel_frame": travel_frame_evidence,
                "raw_source_recurrence_required": False,
            }
            if not evidence["accepted"]:
                evidence["reason"] = "required symmetric loop repair exceeds physical or topology budgets"
                return False, evidence
            return True, evidence
        if role_projection_policy(role).loop_closure == "complete_event_repeat_offset":
            return True, {
                "accepted": True,
                "contract": "precision_complete_event_repeat_offset_closure_v1",
                "diagnostic": "complete precision event; gait recurrence is not an eligibility criterion",
                "role": role.code,
                "source_range": [start, end],
                "scale": scale,
                "scale_procedure": ANATOMICAL_SCALE_PROCEDURE,
                "source_travel_frame": travel_frame_evidence,
                "observed_gait_recurrence": _natural_recurrence_evidence(samples, scale),
            }
        evidence = {
            **_natural_recurrence_evidence(samples, scale),
            "source_range": [start, end], "scale": scale,
            "scale_procedure": ANATOMICAL_SCALE_PROCEDURE,
            "role": role.code, "source_travel_frame": travel_frame_evidence,
        }
        if not evidence["accepted"]:
            evidence["reason"] = "generated-Root-relative recurrence failed"
            return False, evidence
    finally:
        if previous_action is not None:
            _activate_action(rig, previous_action)
        else:
            animation.action = None
        bpy.context.scene.frame_set(previous_frame)
        bpy.context.view_layer.update()
    return True, evidence


def _select_adaptive_loop_repair(
    rig: Any, role: Any, action: Any, start: int, end: int, rig_basis: Any,
) -> tuple[bool, dict[str, Any]]:
    options = []
    for width, profile in adaptive_repair_specs(end - start + 1):
        accepted, evidence = _source_loop_candidate_eligible(
            rig, role, action, start, end, role.derivation, rig_basis,
            repair_window_frames=width,
            repair_blend_profile=profile,
        )
        options.append((accepted, evidence))
    if not options:
        return False, {"accepted": False, "reason": "source loop has no non-overlapping bounded repair support"}
    eligible = [evidence for accepted, evidence in options if accepted]
    selected = min(eligible, key=repair_naturalness_key) if eligible else min(
        (evidence for _, evidence in options),
        key=lambda evidence: (len(evidence.get("failures", {})), repair_naturalness_key(evidence)),
    )
    selected_width = int(selected["window_ranges_zero_based"]["beginning"][1]) + 1
    _, selected = _source_loop_candidate_eligible(
        rig, role, action, start, end, role.derivation, rig_basis,
        repair_window_frames=selected_width,
        repair_blend_profile=str(selected["blend_profile"]),
        include_legacy_full_transform=True,
    )
    selected["repair_option_selection"] = {
        "contract": "bounded_duration_scaled_repaired_output_naturalness_v1",
        "selected_rank_key": list(repair_naturalness_key(selected)),
        "options": [
            {
                "window_range_zero_based": evidence["window_ranges_zero_based"],
                "blend_profile": evidence["blend_profile"],
                "accepted": accepted,
                "failures": evidence["failures"],
                "naturalness_rank_key": list(repair_naturalness_key(evidence)),
            }
            for accepted, evidence in options
        ],
    }
    selected["naturalness_ranking_signals"]["evidence_only"] = False
    selected["naturalness_ranking_signals"]["changes_admission_or_selection"] = True
    if not eligible:
        selected["reason"] = "no adaptive repair option passed direct correction, anatomy, contact, and topology safety"
    return bool(eligible), selected


def _candidate_phase_evidence(
    segment: dict[str, int], role_code: str, geometry: FootGeometry | None,
    derivation: str = "native", output_role_code: str | None = None,
) -> dict[str, Any]:
    """Persist all positional phase proposals, not just accepted releases."""
    if geometry is None:
        return {
            "event_rows": [], "eligible_crossing_count": 0,
            "proposed_windows": [], "recurrence_windows": [],
        }
    event_rows = [
        row
        for side in ("Left", "Right")
        for row in release_event_evidence(segment, geometry, side, role_code)
    ]
    boundary = transition_boundary_phase(role_code)
    windows = []
    if boundary is not None:
        edge, lead = boundary
        output_boundary = transition_boundary_phase(output_role_code or role_code)
        assert output_boundary is not None
        required_output_lead = output_boundary[1]
        for event in (row for row in event_rows if row["foot_side"] == lead):
            start, end = (int(segment["start_frame"]), event["source_frame"]) if edge == "end" else (event["source_frame"], int(segment["end_frame"]))
            eligible = [row for row in event_rows if row["accepted"] and start <= row["source_frame"] <= end]
            mapped_event = map_transition_event_to_output_anatomy(event, derivation)
            mapped_output_lead = mapped_event["foot_side"] if mapped_event is not None else None
            phase_accepted = event["accepted"] and any(
                row["foot_side"] == event["foot_side"] and row["source_frame"] == event["source_frame"]
                for row in eligible
            )
            anatomy_accepted = mapped_output_lead == required_output_lead
            accepted = phase_accepted and anatomy_accepted
            windows.append({
                "source_range": [start, end],
                "boundary_edge": edge,
                "lead_foot_side": lead,
                "trigger_event": event,
                "mapped_output_event": mapped_event,
                "eligible_event_rows": eligible,
                "eligible_crossing_count": len(eligible),
                "accepted": accepted,
                "reason": "accepted role-qualified finite boundary window" if accepted else (
                    event["reason"] if not event["accepted"] else (
                        f"mapped output anatomy is {mapped_output_lead}, not required {required_output_lead}"
                        if not anatomy_accepted else "retained interval does not contain the selected crossing"
                    )
                ),
            })
    return {
        "axis": phase_semantics(role_code)[0],
        "event_rows": event_rows,
        "eligible_crossing_count": sum(row["accepted"] for row in event_rows),
        "proposed_windows": windows,
        "recurrence_windows": (
            recurrence_window_evidence(segment, role_code, geometry)
            if role_code.startswith(("wf_", "wb_", "xr_", "xl_", "ar_", "al_"))
            else []
        ),
    }


def _precision_temporal_reversal_evidence(rig: Any, sr: Any, sl: Any) -> dict[str, Any]:
    """Prove ``sl`` reverses the native ``sr`` trace without changing anatomy."""
    sample_count = int(sr.frame_range[1])
    if sample_count != int(sl.frame_range[1]):
        raise MaterialisationError("precision temporal reversal requires matching inclusive sample counts")
    names = ("Hips", "Chest", "LeftLeg", "LeftShin", "LeftFoot", "RightLeg", "RightShin", "RightFoot")

    def sample(action: Any) -> tuple[list[list[float]], list[dict[str, Matrix]]]:
        _activate_action(rig, action)
        curves = _curves(action)
        root = [[float(curves[(f"{ROOT}.location", axis)].evaluate(frame)) for frame in range(1, sample_count + 1)] for axis in range(3)]
        rows = []
        for frame in range(1, sample_count + 1):
            bpy.context.scene.frame_set(frame)
            root_inverse = rig.pose.bones["Root"].matrix.inverted_safe()
            rows.append({name: root_inverse @ rig.pose.bones[name].matrix.copy() for name in names})
        return root, rows

    sr_root, sr_rows = sample(sr)
    sl_root, sl_rows = sample(sl)
    transform_error = max(_matrix_error(sl_rows[i][name], sr_rows[-i - 1][name]) for i in range(sample_count) for name in names)
    root_error = max(abs(sl_root[axis][i] - (sr_root[axis][-i - 1] - sr_root[axis][-1])) for axis in range(3) for i in range(sample_count))
    idle_error = max(_matrix_error(sr_rows[0][name], sr_rows[-1][name]) for name in names)
    tolerance = DEFAULT_TOLERANCES["transform_oracle_tolerance"]
    result = {
        "derivation": "temporal_reversal", "native_source_role": "sr", "native_source_action": sr.name,
        "derived_action": sl.name,
        "phase_mapping": "sl[frame] = sr[period - frame] in same-anatomy Root-relative coordinates",
        "shared_idle_boundary": "sr[1] == sr[N] == sl[1] == sl[N] in Root-relative anatomy",
        "sample_count": sample_count, "transform_trace_maximum_error": transform_error,
        "root_lateral_trace_maximum_error": root_error, "shared_idle_boundary_maximum_error": idle_error,
        "tolerance": tolerance,
        "accepted": transform_error <= tolerance and root_error <= tolerance and idle_error <= tolerance,
    }
    if not result["accepted"]:
        raise MaterialisationError(f"precision temporal-reversal trace failed: {result}")
    return result


def _serialise_repaired_transform_oracle(
    rows: Sequence[dict[str, Matrix]], root_translation: Sequence[Sequence[float]],
    contract: str, provenance: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Persist the immutable full-transform target used after reopening."""
    names = ("Hips", "Chest", "Head", "LeftFoot", "RightFoot")
    if len(rows) != len(root_translation) or any(any(name not in row for name in names) for row in rows):
        raise MaterialisationError("repaired transform oracle has incomplete frame or landmark evidence")
    return {
        "contract": contract,
        "frames": len(rows),
        "bones": list(names),
        "hips_translation_samples": len(rows),
        "root_translation": [[float(value) for value in sample] for sample in root_translation],
        "rows": [{name: _frozen_matrix(row[name]) for name in names} for row in rows],
        **({"provenance": provenance} if provenance is not None else {}),
    }


def _precision_temporal_reversal_oracle(rig: Any, sr: Any, sl: Any) -> dict[str, Any]:
    """Freeze the reversed repaired ``sr`` hierarchy and its reverse-offset Root."""
    end = int(sr.frame_range[1])
    if end != int(sl.frame_range[1]):
        raise MaterialisationError("precision temporal reversal requires matching inclusive sample counts")
    names = ("Hips", "Chest", "Head", "LeftFoot", "RightFoot")
    repaired_sr_relative_rows = list(reversed(_loop_relative_samples(rig, sr, range(1, end + 1))))
    sl_curves = _curves(sl)
    root_translation = tuple(
        tuple(float(sl_curves[(f"{ROOT}.location", axis)].evaluate(frame)) for axis in range(3))
        for frame in range(1, end + 1)
    )
    root_rest = rig.data.bones["Root"].matrix_local
    repaired_transform_rows = tuple(
        {
            name: root_rest @ Matrix.Translation(Vector(root_translation[index])) @ source_row[name]
            for name in names
        }
        for index, source_row in enumerate(repaired_sr_relative_rows)
    )
    return {
        "repaired_transform_rows": repaired_transform_rows,
        "repaired_root_translation": root_translation,
        "evidence": _serialise_repaired_transform_oracle(
            repaired_transform_rows,
            root_translation,
            "precision_temporal_reversal_repaired_full_transform_v1",
            {
                "derivation": "temporal_reversal",
                "native_source_role": "sr",
                "native_source_action": sr.name,
                "derived_action": sl.name,
                "root_trajectory": "sl[frame] = sr[period - frame] - sr[period]",
                "spatial_mirror": False,
            },
        ),
    }


def _derive_precision_temporal_reversal(rig: Any, sr: Any, sl: Any) -> None:
    """Write sl from repaired sr so symmetric seam repair cannot diverge by direction."""
    end = int(sr.frame_range[1])
    if end != int(sl.frame_range[1]):
        raise MaterialisationError("precision temporal reversal requires matching inclusive sample counts")
    sr_curves = _curves(sr)
    root = [
        [
            float(sr_curves[(f"{ROOT}.location", axis)].evaluate(end - frame + 1))
            - float(sr_curves[(f"{ROOT}.location", axis)].evaluate(end))
            for frame in range(1, end + 1)
        ]
        for axis in range(3)
    ]
    relative_rows = list(reversed(_loop_relative_samples(rig, sr, range(1, end + 1))))
    root_rest = rig.data.bones["Root"].matrix_local
    roots = [root_rest @ Matrix.Translation(Vector((root[0][index], root[1][index], root[2][index]))) for index in range(end)]
    rows = [{name: roots[index] @ matrix for name, matrix in row.items()} for index, row in enumerate(relative_rows)]
    values = _channels_from_armature_rows(rig, roots, rows)
    for axis in range(3):
        values[(f"{ROOT}.location", axis)] = root[axis]
        values[(f"{ROOT}.rotation_euler", axis)] = [0.0] * end
    bag = sl.layers[0].strips[0].channelbag(sl.slots[0], ensure=True)
    for curve in list(_action_fcurves(sl)):
        bag.fcurves.remove(curve)
    for (path, index), samples in sorted(values.items()):
        _curve(bag, path, index, samples, True, path == f"{ROOT}.location")


def _generated_output_evidence(role: Any, chosen: Any, source_segment: dict[str, int], source_geometry: FootGeometry | None, rig: Any, rig_basis: Any, variant: str, precision_crossover_tolerance_scale: float = 0.03) -> dict[str, Any]:
    """Record generated boundary and interval proof independently of selection."""
    output = bpy.data.actions[action_name(variant, role)]
    end = int(output.frame_range[1])
    evidence: dict[str, Any] = {"frame_mapping": _frame_mapping(chosen.start, chosen.end, role.derivation)}
    if role_projection_policy(role).family == "precision_lateral":
        geometry = _foot_geometry(rig, output, 1, end, rig_basis)
        foot_order = _precision_foot_order_evidence(
            rig, rig_basis, role.code, geometry, 1, end, "repaired_output",
            precision_crossover_tolerance_scale,
        )
        responsible_output = int(foot_order["responsible_sample"])
        foot_order["responsible_output_frame"] = responsible_output
        foot_order["responsible_source_frame"] = evidence["frame_mapping"][responsible_output - 1]["source_frame"]
        foot_order["admission"] = "diagnostic_only"
        evidence["precision_foot_order"] = foot_order
        silhouette = _precision_leg_silhouette_evidence(
            rig, rig_basis, role.code, output, 1, end, "repaired_output", precision_crossover_tolerance_scale,
        )
        silhouette["responsible_output_frame"] = silhouette["worst_frame"]
        silhouette["responsible_source_frame"] = evidence["frame_mapping"][int(silhouette["worst_frame"]) - 1]["source_frame"]
        silhouette["admission"] = "diagnostic_only"
        evidence["precision_leg_silhouette"] = silhouette
    phase_code = role.code
    boundary = transition_boundary_phase(phase_code)
    if boundary is None:
        return evidence
    edge, lead = boundary
    geometry = _foot_geometry(rig, output, 1, end, rig_basis)
    rows = [row for side in ("Left", "Right") for row in release_event_evidence({"start_frame": 1, "end_frame": end}, geometry, side, phase_code)]
    locomotion_frame = end if edge == "end" else 1
    idle_frame = 1 if edge == "end" else end
    mapping = evidence["frame_mapping"]
    source_locomotion = mapping[locomotion_frame - 1]["source_frame"]
    source_idle = mapping[idle_frame - 1]["source_frame"]
    source_phase_code = _source_walk_role(role)
    source_boundary = transition_boundary_phase(source_phase_code)
    assert source_boundary is not None
    source_lead = source_boundary[1]
    source_event = next((row for row in _candidate_phase_evidence(source_segment, source_phase_code, source_geometry)["event_rows"] if row["foot_side"] == source_lead and row["source_frame"] == source_locomotion), None)
    # The source event is measured before derivation.  Its context is consumed
    # against generated geometry, so express anatomy in output space first.
    # This is essential for every mirrored transition, while reverse only
    # changes the mapped frame order.
    canonical_source_event = map_transition_event_to_output_anatomy(source_event, role.derivation)
    same_phase_sources = set(range(source_locomotion - 1, source_locomotion + RELEASE_HYSTERESIS_FRAMES))
    same_phase_output_frames = [
        item["output_frame"] for item in mapping if item["source_frame"] in same_phase_sources
    ]
    edge_context = {
        "output_frame": locomotion_frame,
        "predecessor_source_frame": source_locomotion - 1,
        "successor_source_frame": source_locomotion + 1,
        "same_phase_output_frames": same_phase_output_frames,
        "source_event": canonical_source_event,
    }
    canonical_events = canonical_transition_events(
        {"start_frame": 1, "end_frame": end}, geometry, phase_code, edge_context
    )
    selected_lead = lead
    selected_events = [
        event for event in canonical_events
        if event["kind"] == "edge" and event["foot_side"] == selected_lead
    ]
    if len(selected_events) != 1:
        raise MaterialisationError(
            f"{output.name} must contain exactly one selected canonical {selected_lead.lower()} boundary phase; "
            f"found {canonical_events}"
        )
    evidence.update({
        "locomotion_facing_boundary": {
            "edge": edge,
            "release_foot_side": lead,
            "source_release_foot_side": source_lead,
            "output_frame": locomotion_frame,
            "source_frame": source_locomotion,
            "predecessor_source_frame": source_locomotion - 1,
            "successor_source_frame": source_locomotion + 1,
            "source_event": source_event,
            "mapped_output_event": canonical_source_event,
        },
        "bilateral_interval_crossings": {
            "crossing_count": len(rows), "event_rows": rows,
            "canonical_events": canonical_events,
            "canonical_release_count": len(canonical_events),
            "selected_boundary_release_count": len(selected_events),
            "passes_exactly_one_selected_boundary_release": True,
            "surrounding_gait_crossings_retained": len(canonical_events) - len(selected_events),
        },
        "idle_facing_endpoint_retention": {
            "retained": True,
            "output_frame": idle_frame,
            "source_frame": source_idle,
            "source_range": [chosen.start, chosen.end],
            "reason": "complete persisted frame mapping retains the semantic idle-facing endpoint",
        },
    })
    return evidence


def _durable_phase_event_evidence(
    role: Any, chosen: Any, selected_phase: Mapping[str, Any],
    generated_evidence: Mapping[str, Any], action: Any,
) -> dict[str, Any]:
    """Compact the accepted selector result without re-deriving it from metrics."""
    expected_sides = expected_phase_event_sides(role.code)
    if expected_sides is None:
        return {
            "version": PHASE_EVENT_EVIDENCE_VERSION,
            "applicability": "inapplicable",
            "events": [],
        }
    frame_start, frame_end = (int(value) for value in action.frame_range)
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    rows: list[dict[str, Any]] = []
    boundary = transition_boundary_context(role.code)
    if boundary is not None:
        locomotion = generated_evidence.get("locomotion_facing_boundary")
        if not isinstance(locomotion, Mapping):
            raise MaterialisationError(f"{action.name} lacks accepted transition phase evidence")
        source_event = locomotion.get("source_event")
        if not isinstance(source_event, Mapping) or not source_event.get("accepted"):
            raise MaterialisationError(f"{action.name} lacks an accepted selector transition event")
        sample = source_event.get("sample")
        if not isinstance(sample, Mapping):
            raise MaterialisationError(f"{action.name} accepted transition event lacks selector measurements")
        side = str(locomotion.get("release_foot_side"))
        rows.append({
            "id": event_id(side),
            "anatomical_side": side,
            "output_frame": int(locomotion["output_frame"]),
            "source_frame": int(locomotion["source_frame"]),
            "boundary_context": boundary,
            "selector_confidence": float(sample["directional_release_confidence"]),
        })
    else:
        mapping = {
            int(item["source_frame"]): int(item["output_frame"])
            for item in generated_evidence.get("frame_mapping", [])
        }
        accepted = [
            row for row in selected_phase.get("event_rows", [])
            if isinstance(row, Mapping) and row.get("accepted")
            and chosen.start <= int(row.get("source_frame", -1)) <= chosen.end
        ]
        for side in expected_sides:
            matching = []
            for source_event in accepted:
                source_side = source_event.get("foot_side")
                source_frame = source_event.get("source_frame")
                if not isinstance(source_side, str) or not isinstance(source_frame, int) or source_frame not in mapping:
                    continue
                if derived_anatomical_side(source_side, role.derivation) == side:
                    matching.append((mapping[source_frame], source_event))
            # The selected cycle repeats its named event at both endpoints.
            # Frame one is the canonical cyclic identity; the endpoint is the
            # next recurrence, not a second event. The opposite release remains
            # independently selected at its observed, potentially asymmetric frame.
            canonical = next((item for item in matching if item[0] == frame_start), None)
            if canonical is None:
                interior = [item for item in matching if item[0] != frame_end]
                if len(interior) != 1:
                    raise MaterialisationError(
                        f"{action.name} has ambiguous accepted {side.lower()} phase events: "
                        f"{[item[0] for item in matching]}"
                    )
                canonical = interior[0]
            output_frame, source_event = canonical
            sample = source_event.get("sample")
            if not isinstance(sample, Mapping):
                raise MaterialisationError(f"{action.name} accepted loop event lacks selector measurements")
            rows.append({
                "id": event_id(side),
                "anatomical_side": side,
                "output_frame": output_frame,
                "source_frame": int(source_event["source_frame"]),
                "boundary_context": None,
                "selector_confidence": float(sample["directional_release_confidence"]),
            })
    result = {
        "version": PHASE_EVENT_EVIDENCE_VERSION,
        "applicability": "applicable",
        "algorithm": PHASE_EVENT_ALGORITHM,
        "timeline": {"frame_start": frame_start, "frame_end": frame_end, "fps": float(fps)},
        "events": sorted(rows, key=lambda row: row["id"]),
    }
    try:
        validate_phase_event_evidence(role.code, result, frame_start, frame_end)
    except PhaseEventEvidenceError as error:
        raise MaterialisationError(f"{action.name} cannot publish phase-event evidence: {error}") from error
    return result


def _durable_event_provenance(
    role: Any, chosen: Any, selected_phase: Mapping[str, Any],
    generated_evidence: Mapping[str, Any], action: Any, rig: Any, rig_basis: Any,
) -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
    """Publish immutable source, explicit derivation, and final physical evidence."""
    frame_start, frame_end = (int(value) for value in action.frame_range)
    fps = bpy.context.scene.render.fps / bpy.context.scene.render.fps_base
    expected_sides = expected_phase_event_sides(role.code)
    source_interval = [chosen.start, chosen.end]
    if expected_sides is None:
        source = {
            "contract": SOURCE_DIRECTIONAL_EVENT_CONTRACT,
            "source_interval": source_interval,
            "events": [],
        }
    else:
        legacy = _durable_phase_event_evidence(role, chosen, selected_phase, generated_evidence, action)
        source_rows = []
        source_candidates = [
            row for row in selected_phase.get("event_rows", [])
            if isinstance(row, Mapping) and row.get("accepted")
        ]
        transition = generated_evidence.get("locomotion_facing_boundary")
        for output_row in legacy["events"]:
            source_frame = int(output_row["source_frame"])
            selected = next((
                row for row in source_candidates
                if row.get("source_frame") == source_frame
                and derived_anatomical_side(str(row.get("foot_side")), role.derivation)
                == output_row["anatomical_side"]
            ), None)
            if selected is None and isinstance(transition, Mapping):
                candidate = transition.get("source_event")
                if isinstance(candidate, Mapping) and candidate.get("source_frame") == source_frame:
                    selected = candidate
            if not isinstance(selected, Mapping):
                raise MaterialisationError(
                    f"{action.name} cannot bind output event to immutable source directional evidence"
                )
            source_side = str(selected["foot_side"])
            sample = selected.get("sample")
            if not isinstance(sample, Mapping):
                raise MaterialisationError(f"{action.name} source directional event lacks selector measurements")
            source_rows.append({
                "id": event_id(source_side, "Release"),
                "anatomical_side": source_side,
                "event_kind": "Release",
                "source_frame": source_frame,
                "boundary_context": transition_boundary_context(_source_walk_role(role)),
                "selector_confidence": float(sample["directional_release_confidence"]),
                "selector_evidence": dict(selected),
            })
        source = {
            "contract": SOURCE_DIRECTIONAL_EVENT_CONTRACT,
            "source_interval": source_interval,
            "events": sorted(source_rows, key=lambda row: row["id"]),
        }
    try:
        validate_source_directional_event_evidence(source)
    except PhaseEventEvidenceError as error:
        raise MaterialisationError(f"{action.name} cannot publish source directional events: {error}") from error

    derived_rows = [
        derive_output_event(
            row, derivation=role.derivation,
            source_start=chosen.start, source_end=chosen.end, output_start=frame_start,
        )
        for row in source["events"]
    ]
    derived_rows = canonicalise_duplicate_endpoint_events(
        derived_rows, frame_start=frame_start, frame_end=frame_end,
        duplicate_endpoint=bool(role.is_loop),
    )
    derivation = {
        "contract": DERIVATION_EVENT_CONTRACT,
        "derivation": role.derivation,
        "operations": list(derivation_operations(role.derivation)),
        "frame_mapping": {
            "source_start": chosen.start, "source_end": chosen.end,
            "output_start": frame_start, "output_end": frame_end,
        },
        "events": sorted(derived_rows, key=lambda row: row["id"]),
    }
    try:
        validate_event_derivation_evidence(derivation)
    except PhaseEventEvidenceError as error:
        raise MaterialisationError(f"{action.name} cannot publish event derivation: {error}") from error

    physical_rows, refusals = [], []
    if expected_sides is not None:
        geometry = _foot_geometry(rig, action, frame_start, frame_end, rig_basis)
        for row in derivation["events"]:
            expected_frame = int(row["output_frame"])
            repair_support_dilated: set[int] = set()
            detector_stencil: set[int] = {expected_frame}
            if role.is_loop:
                period = frame_end - frame_start
                repair_raw = action.get("anim002_loop_repair_evidence")
                if not isinstance(repair_raw, str):
                    raise MaterialisationError(f"{action.name} lacks loop repair evidence for phase-seed validation")
                repair = json.loads(repair_raw)
                repair_support = {
                    frame_start + (offset % period)
                    for field in ("opening_support", "closing_support")
                    for offset in range(int(repair[field]["window"][0]), int(repair[field]["window"][1]) + 1)
                }
                repair_support_dilated = {
                    frame_start + ((frame - frame_start + delta) % period)
                    for frame in repair_support
                    for delta in range(-PHASE_EVENT_DETECTOR_STENCIL_SAMPLES, PHASE_EVENT_DETECTOR_STENCIL_SAMPLES + 1)
                }
                detector_stencil = {
                    frame_start + ((expected_frame - frame_start + delta) % period)
                    for delta in range(-PHASE_EVENT_DETECTOR_STENCIL_SAMPLES, PHASE_EVENT_DETECTOR_STENCIL_SAMPLES + 1)
                }
            source_repair_intersection = detector_stencil & repair_support_dilated
            def stencil(frame: int) -> set[int]:
                if not role.is_loop:
                    return {frame}
                return {
                    frame_start + ((frame - frame_start + delta) % (frame_end - frame_start))
                    for delta in range(-PHASE_EVENT_DETECTOR_STENCIL_SAMPLES, PHASE_EVENT_DETECTOR_STENCIL_SAMPLES + 1)
                }
            candidate_frames = tuple(
                frame for frame in range(frame_start, frame_end)
                if source_repair_intersection and stencil(frame) & repair_support_dilated
            ) if role.is_loop else (expected_frame,)
            if not candidate_frames:
                candidate_frames = (expected_frame,)
            observations = [(
                frame,
                directional_phase_event_observation(
                    {"start_frame": frame_start, "end_frame": frame_end}, geometry,
                    row["anatomical_side"], role.code, row["event_kind"], frame,
                    duplicate_endpoint=bool(role.is_loop),
                    boundary_context=row["boundary_context"],
                ),
            ) for frame in candidate_frames]
            accepted = [(frame, item) for frame, item in observations if item["accepted"]]
            if role.is_loop:
                period = frame_end - frame_start
                distance = lambda frame: min(abs(frame - expected_frame), period - abs(frame - expected_frame))
            else:
                distance = lambda frame: abs(frame - expected_frame)
            actual_frame, observation = min(
                accepted or observations,
                key=lambda item: (distance(item[0]), item[0]),
            )
            post_detector_stencil = stencil(actual_frame)
            post_repair_intersection = post_detector_stencil & repair_support_dilated
            displacement_authorised = bool(source_repair_intersection and post_repair_intersection)
            physical = {
                **row, **observation,
                "derivation_event_id": row["id"],
                "derived_output_frame": expected_frame,
                "output_frame": actual_frame,
                "repair_frame_delta": canonical_frame_delta(
                    actual_frame, expected_frame,
                    frame_start=frame_start, frame_end=frame_end,
                    duplicate_endpoint=bool(role.is_loop),
                ),
                "seed_displacement_provenance": {
                    "contract": SEED_DISPLACEMENT_CONTRACT,
                    "derived_output_frame": expected_frame,
                    "post_repair_output_frame": actual_frame,
                    "canonical_delta_frames": canonical_frame_delta(
                        actual_frame, expected_frame,
                        frame_start=frame_start, frame_end=frame_end,
                        duplicate_endpoint=bool(role.is_loop),
                    ),
                    "source_detector_stencil_frames": sorted(detector_stencil),
                    "post_repair_detector_stencil_frames": sorted(post_detector_stencil),
                    "repair_support_dilated_frames": sorted(repair_support_dilated),
                    "source_repair_intersection_frames": sorted(source_repair_intersection),
                    "post_repair_repair_intersection_frames": sorted(post_repair_intersection),
                    "displacement_authorised": displacement_authorised,
                },
            }
            physical.pop("source_event_id", None)
            if physical["accepted"]:
                physical_rows.append(physical)
            else:
                refusals.append(physical)
    post_repair = {
        "contract": POST_REPAIR_EVENT_CONTRACT,
        "evaluation_stage": "final_repaired_evaluated_hierarchy",
        "timeline": {"frame_start": frame_start, "frame_end": frame_end, "fps": float(fps)},
        "sample_semantics": {
            "is_loop": bool(role.is_loop),
            "conceptual_sample_count": frame_end - frame_start + 1,
            "unique_runtime_sample_count": frame_end - frame_start + (0 if role.is_loop else 1),
            "duplicate_endpoint": bool(role.is_loop),
        },
        "events": sorted(physical_rows, key=lambda row: row["id"]),
        "refusals": sorted(refusals, key=lambda row: row["id"]),
    }
    try:
        validate_post_repair_event_evidence(role.code, post_repair, frame_start, frame_end)
    except PhaseEventEvidenceError as error:
        raise MaterialisationError(
            f"{action.name} refuses post-repair directional phase event publication: {error}; "
            f"diagnostics={post_repair['refusals']}"
        ) from error
    return source, derivation, post_repair


def _validate(variant: str, sources: set[str], roles: Sequence[Any], full_catalogue: bool, tolerances: dict[str, float], pivot_oracles: dict[str, tuple[dict[str, Any], dict[str, Any]]], transform_oracles: dict[str, dict[str, Any]] | None = None) -> dict[str, Any]:
    rig, _, rig_basis = _rig()
    reference = bpy.data.actions[REFERENCE_ACTION_NAME]
    names, expected_actions = {action.name for action in bpy.data.actions}, [action_name(variant, role) for role in roles]
    expected_names = set(expected_actions)
    if len(expected_actions) != len(expected_names) or not expected_names <= names:
        raise MaterialisationError(
            f"reopen validation did not generate the requested registry Actions; missing: {sorted(expected_names - names)}"
        )
    if full_catalogue:
        namespace = {name for name in names if name.startswith(f"loc_{variant}_")}
        if namespace != expected_names:
            raise MaterialisationError(
                f"reopen validation generated namespace must be exactly the 24 registry Actions; "
                f"unexpected: {sorted(namespace - expected_names)}, missing: {sorted(expected_names - namespace)}"
            )
    if not sources <= names or "pose_ref_idle" not in names: raise MaterialisationError("reopen validation did not preserve sources/reference")
    for role in roles:
        action, curves = bpy.data.actions[action_name(variant, role)], _curves(bpy.data.actions[action_name(variant, role)])
        if any(key not in action for key in METADATA): raise MaterialisationError(f"{action.name} lacks required metadata")
        has_loop_repair_provenance = "anim002_loop_repair_evidence" in action
        if has_loop_repair_provenance != _uses_bounded_loop_repair(role):
            raise MaterialisationError(f"{action.name} loop-repair provenance applicability is invalid")
        translation, yaw_kind = _trajectory(role.code)
        if action["anim002_root_translation_trajectory"] != translation or action["anim002_root_yaw_trajectory"] != yaw_kind:
            raise MaterialisationError(f"{action.name} trajectory metadata does not match its role")
        phase = role.code.rsplit("_", 1)[-1] if "_" in role.code else "natural"
        if action["anim002_phase"] != phase or action["anim002_boundary"] != ("loop" if role.is_loop else "transition"):
            raise MaterialisationError(f"{action.name} phase or boundary metadata does not match its role")
        if (action["anim002_left_contact"], action["anim002_right_contact"], action["anim002_left_contact_confidence"], action["anim002_right_contact_confidence"]) != ("undetermined", "undetermined", 0.0, 0.0):
            raise MaterialisationError(f"{action.name} contact metadata is inconsistent with the generated contract")
        try:
            source_events = json.loads(action["anim002_source_directional_event_evidence"])
            derivation_events = json.loads(action["anim002_event_derivation_evidence"])
            post_repair_events = json.loads(action["anim002_post_repair_event_evidence"])
            validate_source_directional_event_evidence(source_events)
            validate_event_derivation_evidence(derivation_events)
            validate_post_repair_event_evidence(
                role.code, post_repair_events,
                int(action.frame_range[0]), int(action.frame_range[1]),
            )
            if "anim002_loop_repair_evidence" in action:
                validate_loop_repair_provenance(
                    json.loads(action["anim002_loop_repair_evidence"]),
                    conceptual_sample_count=int(action.frame_range[1]) - int(action.frame_range[0]) + 1,
                )
        except (json.JSONDecodeError, TypeError, ValueError, PhaseEventEvidenceError) as error:
            raise MaterialisationError(f"{action.name} has invalid durable event/repair provenance: {error}") from error
        try:
            transport_evidence = json.loads(action["anim002_transport_contract"])
            validate_transport_evidence(
                transport_evidence,
                frame_start=int(action.frame_range[0]), frame_end=int(action.frame_range[1]),
                fps=bpy.context.scene.render.fps / bpy.context.scene.render.fps_base,
                is_loop=role.is_loop,
            )
        except (json.JSONDecodeError, TypeError, TransportContractError) as error:
            raise MaterialisationError(f"{action.name} has invalid durable transport evidence: {error}") from error
        roots = [curves.get((f"{ROOT}.location", index)) for index in range(3)]
        yaw = curves.get((f"{ROOT}.rotation_euler", 1))
        if any(curve is None or abs(curve.evaluate(1)) > 1e-5 for curve in roots) or yaw is None or abs(yaw.evaluate(1)) > 1e-5: raise MaterialisationError(f"{action.name} Root frame one is not origin/rest")
        # A source-window frame is an immutable motion sample, not necessarily
        # the reference idle frame.  Its source-equivalent full transform is
        # asserted below; reference-pose centring would counter-translate it.
        root_curves = [curve for curve in roots if curve is not None]
        assert len(root_curves) == 3
        root_x, root_y, root_z = root_curves
        assert yaw is not None
        end, root_end, yaw_end = action.frame_range[1], [curve.evaluate(action.frame_range[1]) for curve in root_curves], yaw.evaluate(action.frame_range[1])
        endpoint = action["anim002_endpoint_displacement"]
        if len(endpoint) != 3 or any(not math.isclose(actual, expected, abs_tol=1e-5) for actual, expected in zip(endpoint, root_end)) or not math.isclose(action["anim002_endpoint_yaw"], yaw_end, abs_tol=1e-5):
            raise MaterialisationError(f"{action.name} endpoint metadata does not match its evaluated Root curves")
        root_zero, direction_minimum = tolerances["root_zero_tolerance"], tolerances["root_direction_minimum"]
        expected, expected_yaw = _validation_expectation(role.code, rig_basis)
        root_samples = [rig_basis @ Vector((root_x.evaluate(frame), root_y.evaluate(frame), root_z.evaluate(frame))) for frame in range(1, int(end) + 1)]
        if expected.length > 0:
            endpoint_vector = root_samples[-1] - root_samples[0]
            if endpoint_vector.dot(expected) < direction_minimum:
                raise MaterialisationError(f"{action.name} has invalid evaluated Root direction")
            lateral = endpoint_vector - expected * endpoint_vector.dot(expected)
            if not role.code.startswith(("ar", "al")) and lateral.length > root_zero:
                raise MaterialisationError(f"{action.name} has forbidden evaluated Root lateral motion")
        yaw_values = [float(yaw.evaluate(frame)) for frame in range(1, int(end) + 1)]
        if expected_yaw == 0 and any(abs(value) > root_zero for value in yaw_values):
            raise MaterialisationError(f"{action.name} has forbidden Root yaw motion")
        if expected_yaw is not None and expected_yaw and yaw_values[-1] * expected_yaw < direction_minimum:
            raise MaterialisationError(f"{action.name} has invalid evaluated Root yaw direction")
        vertical = roots[1]
        assert vertical is not None
        if any(abs(float(vertical.evaluate(frame))) > root_zero for frame in range(1, int(end) + 1)):
            raise MaterialisationError(f"{action.name} has forbidden Root vertical motion")
        if role.code in {"pr45", "pl45"}:
            expected_endpoint = pivot_root_yaw_sign(role.code) * math.pi / 4.0
            if not math.isclose(yaw_values[-1], expected_endpoint, abs_tol=root_zero):
                raise MaterialisationError(f"{action.name} does not reach its signed 45-degree pivot unit")
            assert expected_yaw is not None
            _validate_pivot_root_transform(action, rig, int(end), expected_yaw, root_zero)
            oracle, reference_oracle = pivot_oracles[role.code]
            _validate_pivot_non_root_targets(
                rig, action, oracle, reference_oracle, "mirror" in role.derivation, role.code
            )
            if transform_oracles is not None:
                _validate_pivot_kinematics(rig, action, transform_oracles[role.code], "mirror" in role.derivation, role.code)
        if transform_oracles is not None:
            _validate_role_transform_oracle(
                rig, action, role, transform_oracles[role.code], "mirror" in role.derivation,
                tolerances["transform_oracle_tolerance"],
            )
        if role_projection_policy(role).family != "pivot":
            _validate_body_carriage(action, role, rig, rig_basis, int(end), tolerances)
        _validate_transition_phase(action, role.code, rig, rig_basis)
        if role.is_loop:
            _validate_walk_phase(action, role.code, rig, rig_basis)
            _validate_loop_hierarchy_seam(rig, action, role)
            for curve in _action_fcurves(action):
                if curve.data_path.startswith(ROOT):
                    continue
                values = [float(curve.evaluate(frame)) for frame in range(1, int(end) + 1)]
                deltas = [abs(second - first) for first, second in zip(values, values[1:])]
                if len(deltas) > 1 and max(deltas) > tolerances["internal_discontinuity_tolerance"]:
                    raise MaterialisationError(f"{action.name} has an internal adjacent-frame discontinuity")
        if any(any(modifier.type == "CYCLES" for modifier in curve.modifiers) != role.is_loop for curve in _action_fcurves(action)):
            raise MaterialisationError(f"{action.name} loop metadata and curve policy are inconsistent")
        if role.is_loop:
            for curve in _action_fcurves(action):
                modifier = next(modifier for modifier in curve.modifiers if modifier.type == "CYCLES")
                expected_mode = (
                    "REPEAT_OFFSET"
                    if curve.data_path == f"{ROOT}.location"
                    or (curve.data_path == f"{ROOT}.rotation_euler" and curve.array_index == 1
                    and any(abs(float(curve.evaluate(frame))) > 1e-8 for frame in range(1, int(end) + 1)))
                    else "REPEAT"
                )
                if modifier.mode_before != expected_mode or modifier.mode_after != expected_mode:
                    raise MaterialisationError(f"{action.name} has an invalid {expected_mode} cycle mode")
        else:
            try:
                transition_provenance = json.loads(action["anim002_transition_evidence"])
            except (KeyError, TypeError, json.JSONDecodeError) as error:
                raise MaterialisationError(f"{action.name} lacks durable finite-transition provenance") from error
            if (
                transition_provenance.get("contract") != "generic_candidate_local_finite_walk_transition_v1"
                or transition_provenance.get("finite_output_duration_frames") != int(end)
                or transition_provenance.get("hips_carriage_subtractions") != 1
                or transition_provenance.get("cycle_modifiers_permitted") is not False
            ):
                raise MaterialisationError(f"{action.name} has invalid durable finite-transition provenance")
    return {
        "action_count": len(expected_actions),
        "actions": expected_actions,
        "undetermined_checks": [
            "direct automated validation covers role semantics, anatomy, contact safety, path, heading, "
            "hierarchy closure, and native cadence; it does not prove subjective player-perceived naturalness",
        ],
    }


def materialise(
    blend_path: str | Path,
    variant: str,
    temp_root: str | Path | None = None,
    tuning: dict[str, float] | None = None,
    roles: Sequence[str] | None = None,
    *,
    output_path: str | Path | None = None,
    source_repair_report_path: str | Path | None = None,
    materialisation_report_path: str | Path | None = None,
) -> dict[str, Any]:
    variant, precursor = validate_variant(variant), Path(blend_path).expanduser().resolve()
    binding_arguments = (output_path, source_repair_report_path, materialisation_report_path)
    if any(value is not None for value in binding_arguments) and not all(value is not None for value in binding_arguments):
        raise MaterialisationError(
            "--output, --source-repair-report and --report must be supplied together"
        )
    source = precursor
    repair_report: Path | None = None
    materialisation_report: Path | None = None
    precursor_hash: str | None = None
    if output_path is not None:
        assert source_repair_report_path is not None
        assert materialisation_report_path is not None
        source = Path(output_path).expanduser().resolve()
        repair_report = Path(source_repair_report_path).expanduser().resolve()
        materialisation_report = Path(materialisation_report_path).expanduser().resolve()
        if len({precursor, source, repair_report, materialisation_report}) != 4:
            raise MaterialisationError(
                "repair precursor, materialised output, repair report and materialisation report paths must be distinct"
            )
        try:
            validate_repair_precursor(repair_report, precursor)
        except WorkflowBindingError as error:
            raise MaterialisationError(str(error)) from error
        precursor_hash = sha256_file(precursor)
        source.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.NamedTemporaryFile(prefix=f".{source.name}.", dir=source.parent, delete=False) as temporary:
            copy_path = Path(temporary.name)
        try:
            shutil.copy2(precursor, copy_path)
            os.replace(copy_path, source)
        finally:
            copy_path.unlink(missing_ok=True)
    full_catalogue = roles is None
    requested_roles = _requested_roles(roles)
    if not source.is_file(): raise MaterialisationError(f"source blend does not exist: {source}")
    tuning = tuning or {}
    allowed_tuning = set(DEFAULT_TOLERANCES)
    if set(tuning) - allowed_tuning:
        raise MaterialisationError(f"unsupported scoring tuning keys: {sorted(set(tuning) - allowed_tuning)}")
    tolerances = DEFAULT_TOLERANCES | {key: value for key, value in tuning.items() if key in DEFAULT_TOLERANCES}
    if any(
        not isinstance(value, (int, float))
        or (value < 0 and key != "body_ground_clearance_minimum")
        for key, value in tolerances.items()
    ):
        raise MaterialisationError("Root and seam tolerances must be non-negative numbers")
    try:
        temp = resolve_temp_root(temp_root if temp_root is not None else default_temp_root(source))
    except ValueError as error:
        raise MaterialisationError(str(error)) from error
    temp.mkdir(parents=True, exist_ok=True)
    run, staged = Path(tempfile.mkdtemp(prefix="kimodo-materialise-", dir=temp)), None
    # Evidence must outlive a caller's disposable staging root.  Persist it in
    # the repository-owned temp boundary, never beside the production blend.
    evidence_directory = repository_temp_root() / "kimodo-selection-evidence"
    evidence_directory.mkdir(parents=True, exist_ok=True)
    evidence_path = evidence_directory / f"{run.name}.json"
    try:
        staged = run / source.name; shutil.copy2(source, staged); ensure_registered()
        bpy.ops.wm.open_mainfile(filepath=str(staged), load_ui=False, use_scripts=False)
        rig, up, rig_basis = _rig(); segments = {item["index"]: item for item in _segments()}
        reference = bpy.data.actions.get(REFERENCE_ACTION_NAME)
        if reference is None:
            raise MaterialisationError(f"required reference Action '{REFERENCE_ACTION_NAME}' was not found")
        # Role-confined repaired sources remain real-source derivations, but
        # must never compete for unrelated families merely because they retain
        # their original Action's complete timeline.
        from .source_repair import (
            SourceRepairError,
            _exclude_unrelated_character_objects,
            _restore_excluded_objects,
            action_is_candidate_for_role,
            source_repair_natural_recurrence,
            source_repair_arc_body_support,
            arc_body_support_for_action,
            source_repair_pivot_recurrence_admission,
            source_repair_precision_foot_order,
            source_repair_precision_leg_silhouette,
            source_repair_window,
        )
        excluded_character_links = _exclude_unrelated_character_objects(rig)
        all_sources = sorted((action for action in bpy.data.actions if action.name.startswith("kimodo_multi_")), key=lambda action: action.name)
        sources = all_sources
        if not sources: raise MaterialisationError("no Kimodo source Actions are available")
        validate_action_for_rig(reference, rig)
        for action in sources:
            validate_action_for_rig(action, rig)
        _assert_euler_evaluation_convention(rig, [reference, *sources])
        source_names = {action.name for action in sources}
        target_names = {action_name(variant, role) for role in requested_roles}
        removal_names = {action.name for action in bpy.data.actions if action.name.startswith(f"loc_{variant}_")} if full_catalogue else target_names
        preservation_exclusions = removal_names | target_names
        preserved_actions = _preservation_snapshot(preservation_exclusions)
        evidence: dict[str, Any] = {"roles": {}}
        selections = []
        source_geometry_cache = EvaluatedFootGeometryCache(rig, rig_basis)
        # Selection is read-only and completes before output Action evaluation
        # starts, so generated pose evaluation can never influence a later
        # source-candidate measurement.
        for role in requested_roles:
            role_sources = [
                action for action in sources
                if action_is_candidate_for_role(action, _source_walk_role(role))
            ]
            candidates = []
            rejected = []
            evaluated_candidates = []
            quality_metrics: dict[str, dict[str, float]] = {}
            quality_keys: dict[tuple[str, int, int], str] = {}
            segment = segments[role.segment_index]
            candidate_sources = []
            for action in role_sources:
                geometry = source_geometry_cache.get(action, segment["start_frame"], segment["end_frame"]) if role.segment_index in (0, 1, 4, 5) else None
                phase_evidence = _candidate_phase_evidence(
                    segment, _source_walk_role(role), geometry if role.segment_index in (0, 4, 5) else None,
                    role.derivation, role.code,
                )
                repaired_window = source_repair_window(action, _source_walk_role(role))
                if repaired_window is not None:
                    windows: list[tuple[int, int] | None] = [repaired_window]
                elif _uses_bounded_loop_repair(role):
                    windows = [
                        tuple(row["source_range"])
                        for row in phase_evidence["recurrence_windows"]
                        if row["phase_admissible"]
                    ]
                    if not windows:
                        windows = [None]
                else:
                    windows = [None]
                candidate_sources.extend(
                    (action, geometry, phase_evidence, window)
                    for window in windows
                )
            for action, geometry, phase_evidence, explicit_window in candidate_sources:
                recurrence: dict[str, Any] | None = None
                travel_projection: dict[str, Any] | None = None
                foot_order: dict[str, Any] | None = None
                leg_silhouette: dict[str, Any] | None = None
                precision_events: dict[str, Any] | None = None
                candidate_window: list[int] | None = None
                quality_key: str | None = None
                try:
                    start, end = explicit_window or role_window(
                        segment, _source_walk_role(role), _action_fcurves(action), geometry,
                        role.derivation, role.code,
                    )
                    candidate_window = [start, end]
                    content_signature = _source_window_content_signature(action, start, end)
                    quality_key = hashlib.sha256(
                        f"{content_signature}:{start}:{end}".encode("ascii")
                    ).hexdigest()
                    quality_keys[(action.name, start, end)] = quality_key
                    quality_metrics[quality_key] = _candidate_quality_metrics(
                        rig, action, start, end, rig_basis, source_geometry_cache,
                    )
                    if role_projection_policy(role).family == "precision_lateral":
                        assert geometry is not None
                        derivative_foot_order = source_repair_precision_foot_order(action, role.code)
                        if derivative_foot_order is not None:
                            immutable_evidence = derivative_foot_order.get("pre_repair")
                            if not isinstance(immutable_evidence, dict):
                                raise SelectionError("precision repair derivative lacks immutable pre-repair evidence")
                            foot_order = dict(immutable_evidence)
                            configured_threshold = tolerances["precision_crossover_tolerance_scale"]
                            normalised_depth = float(foot_order["maximum_scale_normalised_crossover_depth"])
                            foot_order["normalised_tolerance"] = configured_threshold
                            foot_order["minimum_allowed_signed_separation"] = -configured_threshold * float(foot_order["anatomical_scale"])
                            foot_order["accepted"] = normalised_depth <= configured_threshold
                            foot_order["rejection_reason"] = None if foot_order["accepted"] else (
                                f"maximum scale-normalised crossover depth {normalised_depth:.9f} "
                                f"exceeds {configured_threshold:.9f}"
                            )
                            foot_order["derivative_evidence"] = derivative_foot_order
                        else:
                            foot_order = _precision_foot_order_evidence(
                                rig, rig_basis, role.code, geometry, start, end, "immutable_pre_repair",
                                tolerances["precision_crossover_tolerance_scale"],
                            )
                        foot_order["admission"] = "diagnostic_only"
                        derivative_silhouette = source_repair_precision_leg_silhouette(action, role.code)
                        if derivative_silhouette is not None:
                            leg_silhouette = dict(derivative_silhouette["pre_repair"])
                        else:
                            leg_silhouette = _precision_leg_silhouette_evidence(
                                rig, rig_basis, role.code, action, start, end, "immutable_pre_repair",
                                tolerances["precision_crossover_tolerance_scale"],
                            )
                        leg_silhouette["admission"] = "diagnostic_only"
                        precision_events = _precision_semantic_event_evidence(
                            geometry, int(segment["start_frame"]), int(segment["end_frame"]),
                            _anatomical_character_scale(rig),
                        )
                        if not precision_events["accepted"]:
                            raise SelectionError(str(precision_events["rejection_reason"]))
                    candidate_frames = list(range(start, end + 1))
                    if _reverses_time(role.derivation):
                        candidate_frames.reverse()
                    _, _, _, travel_projection = _straight_source_projection(
                        rig, role, action, candidate_frames, rig_basis,
                    )
                    if role.is_loop:
                        seam_eligible, recurrence = (
                            _select_adaptive_loop_repair(rig, role, action, start, end, rig_basis)
                            if _uses_bounded_loop_repair(role)
                            else _source_loop_candidate_eligible(
                                rig, role, action, start, end, role.derivation, rig_basis,
                            )
                        )
                        if not seam_eligible:
                            raise SelectionError(str(recurrence["reason"]))
                        if role.requires_natural_recurrence:
                            provenance_recurrence = source_repair_natural_recurrence(
                                action, _source_walk_role(role),
                            )
                            if provenance_recurrence is not None:
                                recurrence["natural_source_recurrence"] = provenance_recurrence
                                if not provenance_recurrence.get("accepted"):
                                    pivot_admission = source_repair_pivot_recurrence_admission(
                                        action, _source_walk_role(role),
                                    )
                                    if pivot_admission is None:
                                        raise SelectionError("declared natural recurrence failed")
                                    recurrence["repaired_pivot_recurrence_admission"] = pivot_admission
                    if role_projection_policy(role).family == "arc":
                        assert recurrence is not None
                        recurrence["arc_body_support"] = arc_body_support_for_action(
                            rig, action, start, end, role, rig_basis,
                        )
                    repair_cost = (
                        float(recurrence["repair_cost"])
                        if recurrence is not None and recurrence.get("contract") == REPAIR_CONTRACT
                        else 0.0
                    )
                    candidate = score_candidate(
                        action.name, _action_fcurves(action), start, end, repair_cost,
                        recurrence if repair_cost > 0.0 else None, content_signature,
                    )
                    candidates.append(candidate)
                    evaluated_candidates.append({
                        "action": action.name,
                        "quality_identity": quality_key,
                        "source_range": [start, end],
                        "frame_mapping": _frame_mapping(start, end, role.derivation),
                        "selection": {
                            "accepted": True,
                            "reason": "role window derived from evidenced source phase",
                            "loop_construction": recurrence,
                            "source_travel_frame": travel_projection,
                            "selected_source_window": [start, end],
                            "source_window_content_signature": content_signature,
                            "precision_foot_order": foot_order,
                            "precision_leg_silhouette": leg_silhouette,
                            "precision_semantic_events": precision_events,
                            "recurrence": [
                                row for row in phase_evidence["event_rows"]
                                if row["accepted"] and row["source_frame"] in {start, end}
                            ],
                        },
                        "ranking_evidence": candidate.evidence,
                        "phase": phase_evidence,
                    })
                except (SelectionError, MaterialisationError, SourceRepairError) as error:
                    rejected.append({"action": action.name, "reason": str(error), "phase": phase_evidence, "recurrence": recurrence, "precision_foot_order": foot_order, "precision_leg_silhouette": leg_silhouette, "precision_semantic_events": precision_events})
                    evaluated_candidates.append({
                        "action": action.name,
                        "quality_identity": quality_key,
                        "source_range": candidate_window,
                        "frame_mapping": (
                            _frame_mapping(candidate_window[0], candidate_window[1], role.derivation)
                            if candidate_window is not None else []
                        ),
                        "selection": {"accepted": False, "reason": str(error), "loop_construction": recurrence, "source_travel_frame": travel_projection, "precision_foot_order": foot_order, "precision_leg_silhouette": leg_silhouette, "precision_semantic_events": precision_events},
                        "phase": phase_evidence,
                    })
            family = role_projection_policy(role).family
            quality = quality_evidence(quality_metrics, family)
            for candidate_row in evaluated_candidates:
                row_quality = quality.get(candidate_row["quality_identity"])
                if row_quality is not None:
                    candidate_row["quality"] = row_quality
                    candidate_row["quality"]["accepted"] = row_quality["accepted"]
            def quality_for(item: Any) -> dict[str, Any]:
                return quality[quality_keys[(item.action_name, item.start, item.end)]]
            if candidates:
                rejected.extend(
                    {"action": candidate.action_name, "source_range": [candidate.start, candidate.end],
                     "reason": "; ".join(quality_for(candidate)["reasons"]),
                     "quality": quality_for(candidate)}
                    for candidate in candidates if not quality_for(candidate)["accepted"]
                )
                candidates = [candidate for candidate in candidates if quality_for(candidate)["accepted"]]
                if family == "precision_lateral":
                    baseline = next((item for item in candidates if item.action_name == PRECISION_CALIBRATION_BASELINE_ACTION), None)
                    measured_slide: float | None = None
                    margin: float | None = None
                    calibrated_maximum: float | None = None
                    # Synthetic/general fixtures without the configured one-time
                    # calibration source retain normal semantic selection; a
                    # real calibrated set always records and applies its budget.
                    if baseline is not None:
                        measured_slide = float(quality_for(baseline)["metrics"]["planted_foot_slip"])
                        margin = PrecisionStepLimits().calibration_sliding_margin_scale
                        calibrated_maximum = measured_slide + margin
                    for candidate in tuple(candidates):
                        row = next(row for row in evaluated_candidates if row["action"] == candidate.action_name and row["source_range"] == [candidate.start, candidate.end])
                        calibration = {
                            "policy": "one_time_precision_sliding_calibration_v1",
                            "is_calibration_baseline": candidate is baseline,
                            "measured_normalised_planted_foot_slide": float(quality_for(candidate)["metrics"]["planted_foot_slip"]),
                            "calibration_available": baseline is not None,
                            "calibration_baseline_measured_slide": measured_slide if baseline is not None else None,
                            "fixed_margin": margin if baseline is not None else None,
                            "precision_sliding_maximum": calibrated_maximum if baseline is not None else None,
                        }
                        row["precision_sliding_calibration"] = calibration
                        if baseline is not None and candidate is not baseline and calibrated_maximum is not None and calibration["measured_normalised_planted_foot_slide"] > calibrated_maximum:
                            candidates.remove(candidate)
                            reason = "normal precision candidate planted-foot slide exceeds calibrated precision sliding maximum"
                            calibration["accepted"] = False
                            calibration["rejection_reason"] = reason
                            rejected.append({"action": candidate.action_name, "reason": reason, "precision_sliding_calibration": calibration})
                        else:
                            calibration["accepted"] = True
                            calibration["rejection_reason"] = None
            if not candidates:
                evidence["roles"][role.code] = {"selected": None, "candidates": evaluated_candidates, "rejected": rejected}
                evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True), encoding="utf-8")
                reasons = "; ".join(
                    str(row["reason"]) for row in rejected if row.get("reason")
                )
                diagnostic = f"; rejection reasons: {reasons}" if reasons else ""
                raise SelectionError(f"{role.code} has no valid source candidates{diagnostic}; evidence: {evidence_path}")
            family = role_projection_policy(role).family
            def precision_key(item: Any) -> tuple[Any, ...]:
                candidate_metrics = quality_for(item)["metrics"]
                confinement = item.evidence["evaluated_role_cost"].get("confinement", {})
                return precision_candidate_rank_key({
                    "repair_cost": item.score,
                    "source_window_content_signature": item.content_signature,
                    "width_evidence": {
                        "maximum": candidate_metrics["maximum_lateral_width"],
                        "median": candidate_metrics["median_lateral_width"],
                    },
                    "confinement": confinement,
                    "quality_metrics": candidate_metrics,
                })
            def naturalness_key(item: Any) -> tuple[Any, ...]:
                repair = item.evidence["evaluated_role_cost"]
                repaired_output = (
                    repair_naturalness_key(repair)
                    if repair.get("contract") == REPAIR_CONTRACT else ()
                )
                metrics = quality_for(item)["metrics"]
                durable_arc_carriage = (
                    source_repair_arc_body_support(
                        bpy.data.actions[item.action_name], _source_walk_role(role),
                    )
                    if family == "arc" else None
                )
                arc_carriage = (
                    (0, *arc_body_support_rank_key(durable_arc_carriage))
                    if durable_arc_carriage is not None else
                    (1, *arc_body_support_rank_key(repair["arc_body_support"]))
                    if family == "arc" else ()
                )
                if family == "arc":
                    return (
                        *arc_carriage,
                        *natural_cycle_quality_rank_key(metrics),
                        *repaired_output,
                        item.content_signature,
                        item.start,
                        item.end,
                    )
                return natural_cycle_candidate_rank_key(
                    metrics, repaired_output, item.content_signature, item.start, item.end,
                )
            if family == "precision_lateral":
                ranking = sorted(candidates, key=precision_key)
                selected_sr = next((item for selected_role, item in selections if selected_role.code == "sr"), None)
                if role.code == "sl" and selected_sr is not None:
                    chosen = next((item for item in ranking if (
                        item.action_name, item.start, item.end
                    ) == (
                        selected_sr.action_name, selected_sr.start, selected_sr.end
                    )), None)
                    if chosen is None:
                        raise SelectionError(
                            "precision_lateral temporal reversal has no eligible copy of the selected native sr sequence"
                        )
                    ranking_reason = "selected native sr sequence, then temporal reversal"
                else:
                    chosen = ranking[0]
                    ranking_reason = (
                        "repair cost, lower lateral width, confinement, support/slip/derivatives, "
                        "then source-window content signature"
                    )
            else:
                ranking = sorted(candidates, key=naturalness_key)
                chosen = ranking[0]
                ranking_reason = (
                    "hard phase/anatomy/contact eligibility, natural cycle/body quality, arc sustained carriage, repaired-output "
                    "smoothness/magnitude/balance, then source-window content identity"
                )
            for rank, candidate in enumerate(ranking, start=1):
                row = next(row for row in evaluated_candidates if row["action"] == candidate.action_name and row["source_range"] == [candidate.start, candidate.end])
                row["deterministic_ranking"] = {
                    "rank": rank,
                    "sort_key": list(precision_key(candidate)) if family == "precision_lateral" else list(naturalness_key(candidate)),
                    "reason": ranking_reason,
                }
            evidence["roles"][role.code] = {
                "selected": chosen.action_name,
                "selected_source_range": [chosen.start, chosen.end],
                "root_projection": {
                    "family": role_projection_policy(role).family,
                    "source_segment": role_projection_policy(role).segment_index,
                    "retained_translation_axes": list(role_projection_policy(role).translation_axes),
                    "retains_global_z_yaw": role_projection_policy(role).promote_global_z_yaw,
                    "suppressed": list(role_projection_policy(role).suppress),
                },
                "selection_precedence": (
                    ["evidenced phase and idle-facing window eligibility"]
                    + (["precision anatomical foot-order eligibility"] if role_projection_policy(role).family == "precision_lateral" else [])
                    + [
                        "evaluated rig-normalised quality eligibility",
                        "precision lexicographic repair/width/confinement/support ranking" if family == "precision_lateral" else "natural cycle/body quality ranking",
                        "repaired-output smoothness, correction magnitude, and opening/closing balance",
                        "deterministic source-window content identity tie-break",
                    ]
                ),
                "deterministic_ranking_reason": ranking_reason,
                "candidates": evaluated_candidates,
                "rejected": rejected,
            }
            selections.append((role, chosen))
        precision = {role.code: chosen for role, chosen in selections if role.code in {"sr", "sl"}}
        if set(precision) == {"sr", "sl"} and (
            precision["sr"].action_name != precision["sl"].action_name
            or (precision["sr"].start, precision["sr"].end) != (precision["sl"].start, precision["sl"].end)
        ):
            raise SelectionError(
                "precision_lateral must derive sl from the exact selected native sr sequence; "
                "independent candidate selection diverged"
            )
        # Retain only the selected pre-mutation source measurements needed for
        # output evidence, then retire the cache before generated Actions exist.
        selected_source_geometry = {
            role.code: source_geometry_cache.get(
                bpy.data.actions[chosen.action_name],
                segments[role.segment_index]["start_frame"],
                segments[role.segment_index]["end_frame"],
            )
            if role.segment_index in (0, 4, 5) else None
            for role, chosen in selections
        }
        source_geometry_cache.close()
        prepared = []
        pivot_oracles: dict[str, tuple[dict[str, Any], dict[str, Any]]] = {}
        transform_oracles: dict[str, dict[str, Any]] = {}
        reference_transform_oracle = _capture_pivot_transform_oracle(rig, reference, [1])
        for role, chosen in selections:
            frames = list(range(chosen.start, chosen.end + 1))
            if _reverses_time(role.derivation):
                frames.reverse()
            source_action = bpy.data.actions[chosen.action_name]
            transform_oracles[role.code] = _capture_pivot_transform_oracle(rig, source_action, frames)
            transform_oracles[role.code]["reference_oracle"] = reference_transform_oracle
            # This capture is intentionally before removing old outputs or
            # creating a single generated Action.  It is the only source-pose
            # expectation permitted during pivot validation.
            if role.code in {"pr45", "pl45"}:
                source_oracle = transform_oracles[role.code]
                reference_oracle = reference_transform_oracle
                pivot_oracles[role.code] = (source_oracle, reference_oracle)
                # This is the only heading input accepted by the projection:
                # frozen pre-mutation evaluated Hips armature/global-Z samples.
                projection = project_pivot_heading(
                    role.code,
                    [
                        (-1.0 if "mirror" in role.derivation else 1.0) * float(sample["global_z_heading"])
                        for sample in source_oracle["samples"]
                    ],
                )
                evidence["roles"][role.code]["root_projection"]["pivot_heading_projection"] = projection.evidence()
            else:
                projection = None
            source_residuals, source_yaw = _evaluated_body_relative(
                rig, source_action, frames, "mirror" in role.derivation, rig_basis,
                role_projection_policy(role).family in {"pivot", "arc"},
            )
            projected_root, aligned_source_carriage, constant_alignment, travel_projection = _straight_source_projection(
                rig, role, source_action, frames, rig_basis,
            )
            if role_projection_policy(role).straight_source_travel:
                alignment_origin = source_residuals[0].translation.copy()
                source_residuals = [
                    _align_about(residual, constant_alignment, alignment_origin)
                    for residual in source_residuals
                ]
            transform_oracles[role.code]["constant_alignment"] = _frozen_matrix(constant_alignment)
            evidence["roles"][role.code]["root_projection"]["source_travel_frame"] = travel_projection
            prepared.append((
                role,
                chosen,
                projected_root,
                aligned_source_carriage,
                source_residuals,
                source_yaw,
                _evaluated_body_relative(rig, reference, [1], "mirror" in role.derivation, rig_basis)[0][0],
                chosen.start == segments[role.segment_index]["start_frame"] and chosen.end == segments[role.segment_index]["end_frame"],
                projection,
            ))
        for role, chosen, *_ in prepared:
            _assert_pivot_oracle_source_intact(rig, bpy.data.actions[chosen.action_name], transform_oracles[role.code])
        for action in [item for item in bpy.data.actions if item.name in removal_names]:
            bpy.data.actions.remove(action, do_unlink=True)
        seam_evidence: dict[str, list[dict[str, Any]]] = {}
        for role, chosen, root, source_carriage, source_residuals, source_yaw, reference_residual, complete_source_window, projection in prepared:
            role_seam_evidence: list[dict[str, Any]] = []
            oracle = transform_oracles[role.code]
            repair = chosen.evidence["evaluated_role_cost"]
            repair_window = repair.get("window_ranges_zero_based", {}).get("beginning")
            _make(
                role, bpy.data.actions[chosen.action_name], reference, chosen.start, chosen.end,
                rig, up, rig_basis, variant, root, source_carriage, source_residuals, source_yaw,
                reference_residual, complete_source_window, role_seam_evidence, oracle, projection,
                repair_window_frames=(
                    int(repair_window[1]) - int(repair_window[0]) + 1
                    if repair.get("contract") == REPAIR_CONTRACT else None
                ),
                repair_blend_profile=(
                    str(repair["blend_profile"])
                    if repair.get("contract") == REPAIR_CONTRACT else "quadratic"
                ),
            )
            seam_evidence[role.code] = role_seam_evidence
        selected_roles = {role.code: (role, chosen) for role, chosen in selections}
        if {"sr", "sl"} <= set(selected_roles):
            sl_role = selected_roles["sl"][0]
            sl_action = bpy.data.actions[action_name(variant, sl_role)]
            sr_action = bpy.data.actions[action_name(variant, selected_roles["sr"][0])]
            _derive_precision_temporal_reversal(
                rig,
                sr_action,
                sl_action,
            )
            temporal_oracle = _precision_temporal_reversal_oracle(rig, sr_action, sl_action)
            transform_oracles["sl"].update({
                "repaired_transform_rows": temporal_oracle["repaired_transform_rows"],
                "repaired_root_translation": temporal_oracle["repaired_root_translation"],
            })
            seam_evidence["sl"] = [{
                "construction": {
                    "repaired_transform_oracle": temporal_oracle["evidence"],
                },
                "loop_closure": _validate_loop_hierarchy_seam(
                    rig, sl_action, sl_role,
                )["precision_complete_event_closure"],
                "cycle_extension_fidelity": _loop_hierarchy_seam_metrics(
                    rig, sl_action,
                )["cycle_extension_fidelity"],
            }]
        for role, chosen in selections:
            source_segment = segments[role.segment_index]
            source_geometry = selected_source_geometry[role.code]
            evidence["roles"][role.code]["generated_output"] = _generated_output_evidence(
                role, chosen, source_segment, source_geometry, rig, rig_basis, variant,
                tolerances["precision_crossover_tolerance_scale"],
            )
            selected_candidate = next(
                row for row in evidence["roles"][role.code]["candidates"]
                if row["action"] == chosen.action_name and row["source_range"] == [chosen.start, chosen.end]
            )
            if not role.is_loop:
                transition_provenance = {
                    "contract": "generic_candidate_local_finite_walk_transition_v1",
                    "source_window_content_signature": selected_candidate["selection"]["source_window_content_signature"],
                    "derivation": role.derivation,
                    "finite_output_duration_frames": int(bpy.data.actions[action_name(variant, role)].frame_range[1]),
                    "finite_context": evidence["roles"][role.code]["root_projection"]["source_travel_frame"]["finite_context"],
                    "selected_crossing": evidence["roles"][role.code]["generated_output"]["locomotion_facing_boundary"],
                    "crossing_topology": evidence["roles"][role.code]["generated_output"]["bilateral_interval_crossings"],
                    "root_policy": evidence["roles"][role.code]["root_projection"],
                    "hips_carriage_subtractions": 1,
                    "cycle_modifiers_permitted": False,
                }
                bpy.data.actions[action_name(variant, role)]["anim002_transition_evidence"] = json.dumps(
                    transition_provenance, sort_keys=True, separators=(",", ":"),
                )
            output_action = bpy.data.actions[action_name(variant, role)]
            source_events, event_derivation, post_repair_events = _durable_event_provenance(
                role, chosen, selected_candidate["phase"], evidence["roles"][role.code]["generated_output"],
                output_action, rig, rig_basis,
            )
            for property_name, value in (
                ("anim002_source_directional_event_evidence", source_events),
                ("anim002_event_derivation_evidence", event_derivation),
                ("anim002_post_repair_event_evidence", post_repair_events),
            ):
                output_action[property_name] = json.dumps(value, sort_keys=True, separators=(",", ":"))
            evidence["roles"][role.code]["generated_output"].update({
                "source_directional_event_evidence": source_events,
                "event_derivation_evidence": event_derivation,
                "post_repair_event_evidence": post_repair_events,
            })
            evidence["roles"][role.code]["generated_output"]["reconstruction_fidelity"] = _validate_role_transform_oracle(
                rig, bpy.data.actions[action_name(variant, role)], role, transform_oracles[role.code],
                "mirror" in role.derivation, tolerances["transform_oracle_tolerance"],
            )
            if role.code in {"pr45", "pl45"}:
                evidence["roles"][role.code]["generated_output"]["pivot_kinematic_oracle"] = _validate_pivot_kinematics(
                    rig, bpy.data.actions[action_name(variant, role)], transform_oracles[role.code],
                    "mirror" in role.derivation, role.code,
                )
            evidence["roles"][role.code]["generated_output"]["loop_seam_construction"] = seam_evidence[role.code]
        if {"sr", "sl"} <= set(selected_roles):
            trace = _precision_temporal_reversal_evidence(
                rig,
                bpy.data.actions[action_name(variant, selected_roles["sr"][0])],
                bpy.data.actions[action_name(variant, selected_roles["sl"][0])],
            )
            evidence["roles"]["sr"]["generated_output"]["temporal_reversal"] = trace
            evidence["roles"]["sl"]["generated_output"]["temporal_reversal"] = trace
            for code in ("sr", "sl"):
                bpy.data.actions[action_name(variant, selected_roles[code][0])]["anim002_precision_derivation"] = json.dumps(
                    {**trace, "output_role": code}, sort_keys=True, separators=(",", ":"),
                )
        evidence["scoring_tuning"] = tuning
        evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True), encoding="utf-8")
        _restore_excluded_objects(excluded_character_links)
        bpy.context.preferences.filepaths.save_version = 0; bpy.ops.wm.save_as_mainfile(filepath=str(staged), check_existing=False)
        bpy.ops.wm.open_mainfile(filepath=str(staged), load_ui=False, use_scripts=False)
        reopened_rig = _rig()[0]
        _exclude_unrelated_character_objects(reopened_rig)
        _assert_euler_evaluation_convention(
            reopened_rig, [bpy.data.actions[action_name(variant, role)] for role in requested_roles]
        )
        validation = _validate(variant, source_names, requested_roles, full_catalogue, tolerances, pivot_oracles, transform_oracles)
        _assert_preserved_actions(preserved_actions, preservation_exclusions)
        # The staging boundary may be on another filesystem (as it is for a
        # caller's disposable source copy).  Put a verified publish copy beside
        # the target so the visible replacement itself remains atomic.
        publish = source.with_name(f".{source.name}.kimodo-publish")
        shutil.copy2(staged, publish)
        os.replace(publish, source)
        result = {
            "status": "published",
            "temporary_run": str(run),
            "evidence": str(evidence_path),
            "roles": [role.code for role in requested_roles],
            "selections": {role.code: {"source": chosen.action_name, "range": [chosen.start, chosen.end]} for role, chosen in selections},
            "quality_rejections": [
                {"role": role_code, **rejected}
                for role_code, role_evidence in evidence["roles"].items()
                for rejected in role_evidence["rejected"]
                if "quality" in rejected
            ],
            **validation,
        }
        if materialisation_report is not None and repair_report is not None and precursor_hash is not None:
            if sha256_file(precursor) != precursor_hash:
                raise MaterialisationError("immutable repair precursor changed during materialisation")
            evidence_provenance = {
                role.code: {
                    key: evidence["roles"][role.code]["generated_output"][key]
                    for key in (
                        "source_directional_event_evidence",
                        "event_derivation_evidence",
                        "post_repair_event_evidence",
                    )
                }
                for role in requested_roles
            }
            action_provenance = {
                role.code: {
                    key: json.loads(bpy.data.actions[action_name(variant, role)][property_name])
                    for key, property_name in (
                        ("source_directional_event_evidence", "anim002_source_directional_event_evidence"),
                        ("event_derivation_evidence", "anim002_event_derivation_evidence"),
                        ("post_repair_event_evidence", "anim002_post_repair_event_evidence"),
                    )
                }
                for role in requested_roles
            }
            try:
                action_digest = validate_action_provenance_binding(evidence_provenance, action_provenance)
            except WorkflowBindingError as error:
                raise MaterialisationError(str(error)) from error
            binding = {
                "contract": MATERIALISATION_BINDING_CONTRACT,
                "precursor": {"path": str(precursor), "sha256": precursor_hash},
                "final": {"path": str(source), "sha256": sha256_file(source)},
                "source_repair_report": {"path": str(repair_report), "sha256": sha256_file(repair_report)},
                "selection_evidence": {"path": str(evidence_path), "sha256": sha256_file(evidence_path)},
                "action_provenance_sha256": action_digest,
                "roles": [role.code for role in requested_roles],
            }
            materialisation_report.parent.mkdir(parents=True, exist_ok=True)
            with tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", prefix=f".{materialisation_report.name}.",
                dir=materialisation_report.parent, delete=False,
            ) as temporary:
                json.dump(binding, temporary, indent=2, sort_keys=True)
                temporary.write("\n")
                temporary_path = Path(temporary.name)
            os.replace(temporary_path, materialisation_report)
            result["binding_report"] = str(materialisation_report)
            result["precursor_sha256"] = precursor_hash
            result["final_sha256"] = binding["final"]["sha256"]
            result["action_provenance_sha256"] = action_digest
        return result
    except (DiscoveryError, SelectionError, OSError, RuntimeError) as error:
        (run / "failure.json").write_text(json.dumps({"error": str(error)}, indent=2), encoding="utf-8")
        raise MaterialisationError(f"materialisation failed; original preserved; diagnostics: {run}: {error}") from error
