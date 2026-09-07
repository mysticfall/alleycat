#!/usr/bin/env python3
"""Bake materialised Kimodo actions to the constrained Female rig.

Called by the ``commands/retarget_animations.py`` Blender entry point.

    blender --background --factory-startup --python tools/kimodo-pipeline/commands/retarget_animations.py -- \
        --package locomotion --variant sad
    blender --background --factory-startup --python tools/kimodo-pipeline/commands/retarget_animations.py -- \
        --package idle --idle-source-action kimodo_m0a3dyf0

The locomotion package opens the character's authoring workspace (or a staged,
already-materialised copy of it) and publishes beneath the character asset
root's flattened ``animations/processed/locomotion`` directory.  The processor
never saves either source blend.  It writes a staged package, reopens it for
verification, then publishes its blend, index and metrics as a directory
transaction.  It intentionally does not invoke a retarget add-on or perform
Mixamo Root reconstruction.
"""
from __future__ import annotations

import argparse
from contextlib import contextmanager
import json
import math
import os
from pathlib import Path
import shutil
import sys
import uuid
import re
from typing import Any

import bpy
from mathutils import Vector

from .retargeting import (
    ExportMotion,
    idle_export,
    locomotion_exports,
    portable_source_name,
    published_output_root,
)
from .bootstrap_paths import BootstrapPathError, derive_artefact_paths, repository_root
from .phase_events import PhaseEventEvidenceError, validate_complete_event_provenance
from .loop_seam import validate_loop_repair_provenance
from .leg_silhouette import LegSilhouetteError, assess_precision_leg_silhouette
from .temp_paths import repository_temp_root
from .transport import (
    TransportContractError,
    make_coordinate_contract,
    make_transport_evidence,
    validate_transport_evidence,
)
from .workflow_binding import (
    WorkflowBindingError,
    sha256_file,
    validate_materialisation_report,
)


ROOT = repository_root()
SOURCE_ROOT = ROOT / "game/assets/characters/animations/source"
# The idle package still reads the legacy monolith-flow idle source; the
# locomotion package opens the character authoring workspace instead.
LEGACY_IDLE_SOURCE = SOURCE_ROOT / "idle/idle-standing.blend"
LEGACY_IDLE_SOURCE_ACTION = "kimodo_m0a3dyf0"
DEFAULT_CHARACTER_ASSET_ROOT = ROOT / "game/assets/characters/reference/female"
METRICS_SCHEMA_VERSION = 2
TRACKED_BONES = ("Root", "pelvis", "head", "foot_l", "foot_r", "hand_l", "hand_r")
PRECISION_LEG_CHAINS = (("thigh_l", "calf_l", "foot_l"), ("thigh_r", "calf_r", "foot_r"))
PRECISION_LEG_SILHOUETTE_TOLERANCE = 0.03
PRECISION_REVERSAL_TOLERANCE_SCALE = 5e-4
CYCLIC_TRAVEL_CAPABILITIES = frozenset({
    "cyclic_forward_primary", "cyclic_backward_primary",
    "cyclic_lateral_left_left_lane", "cyclic_lateral_left_right_lane",
    "cyclic_lateral_right_left_lane", "cyclic_lateral_right_right_lane",
    "cyclic_arc_left_left_lane", "cyclic_arc_left_right_lane",
    "cyclic_arc_right_left_lane", "cyclic_arc_right_right_lane",
})
# The established Female Root convention is local X lateral (and its canonical
# Blender-to-Godot transport maps X to -X).  Do not translate local Y/Z.
FEMALE_ROOT_LATERAL_LOCATION_AXIS = 0
EPSILON = 1e-4
OPENING_ROOT_ALIGNMENT_LIMIT_RADIANS = math.radians(0.1)
ROOT_TILT_LIMIT_RADIANS = math.radians(5.0)
ALLOW_DERIVED_TRANSPORT = False
OBSOLETE_LOOP_REPAIR_FIELDS = frozenset({
    "anim002_corrected_loop",
    "anim002_excluded_edge_frames",
    "anim002_oracle_exclusions",
    "anim002_safe_correction_window",
    "anim002_trailing_repair",
})


class ProcessorError(RuntimeError):
    """A clear expected processor failure."""


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", choices=("locomotion", "idle"), required=True)
    parser.add_argument("--variant", required=True, help="Lower-case snake_case content variant key.")
    parser.add_argument(
        "--character-root", type=Path, default=DEFAULT_CHARACTER_ASSET_ROOT,
        help="Character asset root driving workspace and published-path derivation.",
    )
    parser.add_argument(
        "--workspace", type=Path,
        help="Locomotion authoring workspace blend to open (or its staged materialised copy).",
    )
    parser.add_argument(
        "--idle-source-action", default=LEGACY_IDLE_SOURCE_ACTION,
        help="Idle source action name inside the idle source blend (an input, not a constant).",
    )
    parser.add_argument("--source", type=Path, help="Override source blend (useful only for disposable-copy tests).")
    parser.add_argument(
        "--output-root", type=Path, default=None,
        help="Published package root; defaults to the character's flattened animations/processed tree.",
    )
    parser.add_argument(
        "--provenance-path",
        help="Portable retained provenance path recorded in the processed index.",
    )
    parser.add_argument(
        "--materialisation-report", type=Path,
        help="Byte-binding report that must name the exact staged locomotion source.",
    )
    parser.add_argument("--roles", help="Comma-separated projected roles, for disposable focused tests.")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument(
        "--derive-missing-transport", action="store_true",
        help="Derive inclusive transport from Action ranges for accepted legacy disposable catalogues only.",
    )
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else sys.argv[1:]
    namespace = parser.parse_args(arguments)
    if namespace.workspace is not None and namespace.source is not None:
        parser.error("--workspace and --source are mutually exclusive source inputs.")
    if namespace.workspace is not None and namespace.package != "locomotion":
        parser.error("--workspace names a locomotion authoring artefact and cannot serve the idle package.")
    return namespace


def _workspace_provenance_path(source_path: Path, derived_workspace: Path) -> Path:
    """Return the portable, never-absolute provenance path for a workspace bake.

    A staged materialised copy beneath the repository temp boundary records the
    character's derived authoring-workspace contract path (its byte lineage),
    never the transient staging location; an in-repository workspace records its
    own repository-relative path; anything else falls back to the portable
    ``source/<package>/<name>`` shape.
    """
    if source_path == derived_workspace:
        return derived_workspace.relative_to(ROOT)
    try:
        source_path.relative_to(repository_temp_root())
    except ValueError:
        try:
            return source_path.relative_to(ROOT)
        except ValueError:
            return Path(f"source/locomotion/{portable_source_name(source_path.name)}")
    if source_path.name == derived_workspace.name:
        return derived_workspace.relative_to(ROOT)
    return Path(f"source/locomotion/{portable_source_name(source_path.name)}")


def action_has_keys(action: bpy.types.Action) -> bool:
    if any(curve.keyframe_points for curve in getattr(action, "fcurves", ())):
        return True
    return any(
        curve.keyframe_points
        for layer in getattr(action, "layers", ())
        for strip in getattr(layer, "strips", ())
        for bag in getattr(strip, "channelbags", ())
        for curve in getattr(bag, "fcurves", ())
    )


def action_fcurves(action: bpy.types.Action) -> list[Any]:
    curves = list(getattr(action, "fcurves", ()))
    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for bag in getattr(strip, "channelbags", ()):
                curves.extend(getattr(bag, "fcurves", ()))
    return curves


def action_curve_container(action: bpy.types.Action) -> Any:
    """Return the legacy fcurve collection or Blender 5 layered channel bag."""
    legacy = getattr(action, "fcurves", None)
    if legacy is not None:
        return legacy
    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for bag in getattr(strip, "channelbags", ()):
                return bag.fcurves
    raise ProcessorError(f'Action "{action.name}" has no editable fcurve container.')


def select_target(target: bpy.types.Object) -> None:
    if bpy.context.object is not None and bpy.context.object.mode != "OBJECT":
        bpy.ops.object.mode_set(mode="OBJECT")
    for item in bpy.context.view_layer.objects:
        item.select_set(False)
    target.select_set(True)
    bpy.context.view_layer.objects.active = target


def select_deformation_bones(target: bpy.types.Object) -> None:
    select_target(target)
    bpy.ops.object.mode_set(mode="POSE")
    for bone in target.pose.bones:
        bone.select = bone.bone.use_deform


def source_constraints(target: bpy.types.Object, source: bpy.types.Object) -> list[Any]:
    return [constraint for bone in target.pose.bones for constraint in bone.constraints if constraint.target == source]


def validate_rigs(target: bpy.types.Object | None, source: bpy.types.Object | None) -> None:
    if target is None or target.type != "ARMATURE":
        raise ProcessorError('Expected target armature named "Female".')
    if source is None or source.type != "ARMATURE":
        raise ProcessorError('Expected source armature named "Kimodo_Source".')
    if target.pose.bones.get("Root") is None:
        raise ProcessorError('Female has no required pose bone "Root".')
    constraints = source_constraints(target, source)
    if not constraints:
        raise ProcessorError("Female has no visual constraints targeting Kimodo_Source.")
    root_constraints = [item for item in target.pose.bones["Root"].constraints if item.target == source]
    types = {item.type for item in root_constraints}
    if not {"COPY_LOCATION", "COPY_ROTATION"}.issubset(types):
        raise ProcessorError("Female:Root must have Kimodo_Source COPY_LOCATION and COPY_ROTATION constraints.")


def constraint_graph(target: bpy.types.Object) -> tuple[tuple[Any, ...], ...]:
    """Capture the authored retarget edges without evaluating helper transforms directly."""
    return tuple(
        (
            bone.name, index, constraint.type,
            constraint.target.name if constraint.target is not None else None,
            constraint.subtarget, constraint.owner_space, constraint.target_space,
            bool(constraint.mute), round(float(constraint.influence), 7),
        )
        for bone in target.pose.bones
        for index, constraint in enumerate(bone.constraints)
    )


def validate_constraint_graph(target: bpy.types.Object, authored: tuple[tuple[Any, ...], ...]) -> None:
    if constraint_graph(target) != authored:
        raise ProcessorError("Female authored retarget constraint graph changed during evaluation.")


def helper_configuration(source: bpy.types.Object) -> tuple[tuple[Any, ...], ...]:
    return tuple(
        (
            bone.name, bone.parent.name if bone.parent is not None else None,
            tuple(round(float(value), 7) for row in bone.matrix_local for value in row),
            tuple(round(float(value), 7) for row in source.pose.bones[bone.name].matrix_basis for value in row),
        )
        for bone in source.data.bones
        if bone.name.endswith("_RET")
    )


def validate_helper_configuration(source: bpy.types.Object, authored: tuple[tuple[Any, ...], ...]) -> None:
    if helper_configuration(source) != authored:
        raise ProcessorError("Kimodo authored retarget helper configuration changed during evaluation.")


def bake_action(scene: bpy.types.Scene, target: bpy.types.Object, source: bpy.types.Object, motion: ExportMotion, source_action: bpy.types.Action) -> bpy.types.Action:
    if bpy.data.actions.get(motion.internal_action) is not None:
        raise ProcessorError(f'Output action "{motion.internal_action}" already exists in the source file.')
    target_data = target.animation_data_create()
    source_data = source.animation_data_create()
    if target_data is None or source_data is None:
        raise ProcessorError("Could not create armature animation data.")
    baked = bpy.data.actions.new(motion.internal_action)
    baked.use_fake_user = True
    target_data.action = baked
    source_data.action = source_action
    start, end = math.floor(source_action.frame_range[0]), math.ceil(source_action.frame_range[1])
    scene.frame_set(start)
    select_deformation_bones(target)
    result = bpy.ops.nla.bake(
        frame_start=start, frame_end=end, step=1, only_selected=True, visual_keying=True,
        clear_constraints=False, clear_parents=False, use_current_action=True, clean_curves=False,
        bake_types={"POSE"}, channel_types={"LOCATION", "ROTATION", "SCALE"},
    )
    if "CANCELLED" in result or target_data.action != baked or not baked.use_fake_user or not action_has_keys(baked):
        raise ProcessorError(f'Visual constraint bake failed for "{motion.source_action}".')
    return baked


def _reverse_curve_keys(curve: Any, start: float, end: float, offset: float = 0.0) -> None:
    """Reverse one baked curve without asking Blender to re-sample its rotations.

    Visual baking generates Bezier handles and can choose different Euler branches
    for equivalent source poses.  Keeping the baked destination curves and
    reflecting their time coordinates is therefore the only representation that
    remains an exact temporal pair through the importer.
    """
    points = curve.keyframe_points
    snapshot = [
        {
            "co": tuple(point.co), "left": tuple(point.handle_left), "right": tuple(point.handle_right),
            "left_type": point.handle_left_type, "right_type": point.handle_right_type,
            "interpolation": point.interpolation, "easing": point.easing,
            "amplitude": point.amplitude, "back": point.back, "period": point.period,
        }
        for point in points
    ]
    for index, point in enumerate(points):
        source = snapshot[-index - 1]
        point.co = (start + end - source["co"][0], source["co"][1] + offset)
        point.handle_left = (start + end - source["right"][0], source["right"][1] + offset)
        point.handle_right = (start + end - source["left"][0], source["left"][1] + offset)
        point.handle_left_type = source["right_type"]
        point.handle_right_type = source["left_type"]
        # A key owns its outgoing segment, which becomes the incoming segment
        # of the reversed key. The final key has no outgoing segment.
        if index + 1 < len(points):
            point.interpolation = snapshot[-index - 2]["interpolation"]
        point.easing = source["easing"]
        point.amplitude = source["amplitude"]
        point.back = source["back"]
        point.period = source["period"]
    curve.update()


def derive_baked_temporal_reversal(
    sr: bpy.types.Action, motion: ExportMotion,
) -> bpy.types.Action:
    """Create ``sl`` by reversing the already baked Female ``sr`` Action.

    Every curve is directly reversed, preserving its baked representation. Root
    local X is Female's established lateral channel, so it alone
    receives ``-sr[end]``: ``sl[i] = sr[N - 1 - i] - sr[N - 1]``.  This makes
    the derived action begin at the lateral origin without changing its pose.
    """
    if motion.runtime_capability != "confined_side_step_left":
        raise ProcessorError("Only the precision sl role may be derived from a baked temporal reversal.")
    start, end = (float(value) for value in sr.frame_range)
    derived = sr.copy()
    derived.name = motion.internal_action
    derived.use_fake_user = True
    root_lateral = next((
        curve for curve in action_fcurves(sr)
        if curve.data_path == 'pose.bones["Root"].location'
        and curve.array_index == FEMALE_ROOT_LATERAL_LOCATION_AXIS
    ), None)
    if root_lateral is None or not root_lateral.keyframe_points:
        raise ProcessorError("Baked precision sr requires a non-empty Female Root local lateral location curve.")
    root_offsets = [0.0, 0.0, 0.0]
    root_offsets[FEMALE_ROOT_LATERAL_LOCATION_AXIS] = -float(root_lateral.evaluate(end))
    for curve in action_fcurves(derived):
        offset = root_offsets[curve.array_index] if curve.data_path == 'pose.bones["Root"].location' else 0.0
        _reverse_curve_keys(curve, start, end, offset)
    derived["anim002_precision_temporal_reversal_provenance"] = json.dumps({
        "version": 4,
        "derivation": "baked_female_fcurve_temporal_reversal",
        "source_action": sr.name,
        "curve_representation": "preserved_baked_fcurves",
        "root_reverse_offset": root_offsets,
        "root_reverse_offset_space": "female_pose_root_location",
    }, sort_keys=True, separators=(",", ":"))
    validate_baked_temporal_reversal_fcurves(sr, derived)
    return derived


def validate_baked_temporal_reversal_fcurves(sr: bpy.types.Action, sl: bpy.types.Action, tolerance: float = 1e-7) -> None:
    """Prove ``sl`` is direct ``sr`` reversal except final-relative Root lateral."""
    start, end = (float(value) for value in sr.frame_range)
    sr_curves = {(curve.data_path, curve.array_index): curve for curve in action_fcurves(sr)}
    sl_curves = {(curve.data_path, curve.array_index): curve for curve in action_fcurves(sl)}
    if sl_curves.keys() != sr_curves.keys():
        raise ProcessorError("Baked precision temporal reversal has different destination curve channels.")
    for key, sr_curve in sr_curves.items():
        sl_curve = sl_curves[key]
        if sr_curve.extrapolation != sl_curve.extrapolation or len(sr_curve.keyframe_points) != len(sl_curve.keyframe_points):
            raise ProcessorError(f"Baked precision temporal reversal changed curve shape: {key[0]}[{key[1]}].")
        for index, point in enumerate(sr_curve.keyframe_points):
            reversed_point = sl_curve.keyframe_points[-index - 1]
            reverse_time = start + end - float(point.co.x)
            offset = -float(sr_curve.evaluate(end)) if key == (
                'pose.bones["Root"].location', FEMALE_ROOT_LATERAL_LOCATION_AXIS,
            ) else 0.0
            if abs(float(sl_curve.evaluate(reverse_time)) - (float(point.co.y) + offset)) > tolerance:
                raise ProcessorError(f"Baked precision temporal reversal curve mismatch: {key[0]}[{key[1]}].")
            if (
                abs(float(reversed_point.co.x) - reverse_time) > tolerance
                or abs(float(reversed_point.co.y) - (float(point.co.y) + offset)) > tolerance
                or reversed_point.handle_left_type != point.handle_right_type
                or reversed_point.handle_right_type != point.handle_left_type
            ):
                raise ProcessorError(f"Baked precision temporal reversal changed key representation: {key[0]}[{key[1]}].")
            if index > 0 and reversed_point.interpolation != sr_curve.keyframe_points[index - 1].interpolation:
                raise ProcessorError(f"Baked precision temporal reversal changed interpolation: {key[0]}[{key[1]}].")
    root_key = ('pose.bones["Root"].location', FEMALE_ROOT_LATERAL_LOCATION_AXIS)
    if root_key not in sr_curves:
        raise ProcessorError("Baked precision temporal reversal lacks the Female Root local lateral location curve.")
    sr_lateral, sl_lateral = sr_curves[root_key], sl_curves[root_key]
    if abs(float(sl_lateral.evaluate(start))) > tolerance:
        raise ProcessorError("Baked precision temporal reversal Root lateral does not begin at the origin.")
    if abs(float(sr_lateral.evaluate(start))) <= tolerance and abs(
        float(sl_lateral.evaluate(end)) + float(sr_lateral.evaluate(end))
    ) > tolerance:
        raise ProcessorError("Baked precision temporal reversal Root lateral final endpoint is not -sr final.")


def nla_reference_name(action: bpy.types.Action) -> str:
    return f"KimodoBakedRef:{action.name}"[:63]


def persist_action(action: bpy.types.Action, target: bpy.types.Object) -> None:
    data = target.animation_data_create()
    assert data is not None
    name = nla_reference_name(action)
    track = data.nla_tracks.new()
    track.name = name
    strip = track.strips.new(name, int(action.frame_range[0]), action)
    strip.name = name
    strip.mute = True
    track.mute = True
    track.lock = True


def root_paths(action: bpy.types.Action) -> tuple[set[str], set[str]]:
    curves = action_fcurves(action)
    paths: set[str] = {str(curve.data_path) for curve in curves if 'pose.bones["Root"]' in curve.data_path}
    locations = {path for path in paths if path.endswith(".location")}
    rotations = {path for path in paths if ".rotation_" in path}
    return locations, rotations


def action_bone_names(action: bpy.types.Action) -> set[str]:
    curves = action_fcurves(action)
    return {
        match.group(1)
        for curve in curves
        if (match := re.search(r'pose\.bones\["([^"]+)"\]', str(curve.data_path)))
    }


def enforce_root_translation_semantics(action: bpy.types.Action, source: bpy.types.Action) -> None:
    if source.get("anim002_root_translation_trajectory") != "zero_translation":
        return
    curves = [
        curve for curve in action_fcurves(action)
        if curve.data_path == 'pose.bones["Root"].location'
    ]
    if {curve.array_index for curve in curves} != {0, 1, 2}:
        raise ProcessorError(f'Baked action "{action.name}" cannot enforce zero_translation without complete Root location channels.')
    for curve in curves:
        points = curve.keyframe_points
        if not points:
            raise ProcessorError(f'Baked action "{action.name}" has an empty Root location channel.')
        opening = float(points[0].co.y)
        for point in points:
            point.co.y = opening
            point.handle_left.y = opening
            point.handle_right.y = opening
        curve.update()


def enforce_baked_root_translation_semantics(
    action: bpy.types.Action, source: bpy.types.Action, motion: ExportMotion,
) -> None:
    """Apply source-owned Root policies without replacing a derived ``sl`` trace.

    ``sl`` owns no independently baked Root trajectory: it is the direct curve
    reversal of the already normalised baked ``sr`` destination Action.  Applying
    the original source ``sl`` policy afterwards would replace that derived Root
    trace and break its reverse-offset correspondence.
    """
    if motion.runtime_capability == "confined_side_step_left":
        if action.get("anim002_precision_temporal_reversal_provenance") is None:
            raise ProcessorError("Derived precision sl is missing baked temporal-reversal provenance.")
        return
    enforce_root_translation_semantics(action, source)


@contextmanager
def baked_action_evaluation(target: bpy.types.Object) -> Any:
    constraints = [constraint for bone in target.pose.bones for constraint in bone.constraints]
    mute_states = [constraint.mute for constraint in constraints]
    try:
        for constraint in constraints:
            constraint.mute = True
        yield
    finally:
        for constraint, mute in zip(constraints, mute_states, strict=True):
            constraint.mute = mute


def _vector(value: Any) -> list[float]:
    return [round(float(item), 7) for item in value]


def _transform(matrix: Any) -> dict[str, Any]:
    return {
        "coordinate_space": "female_armature_local_z_up",
        "basis_columns": [
            [round(float(matrix[row][column]), 7) for row in range(3)]
            for column in range(3)
        ],
        "origin": _vector(matrix.translation),
    }


def _yaw(matrix: Any) -> float:
    # This is an evaluated target skeleton world-Z yaw measurement, not a source
    # reconstruction convention and not a claim about any Mixamo axis.
    return math.atan2(float(matrix[1][0]), float(matrix[0][0]))


def sample_metrics(scene: bpy.types.Scene, target: bpy.types.Object, action: bpy.types.Action, motion: ExportMotion) -> dict[str, Any]:
    data = target.animation_data_create()
    assert data is not None
    data.action = action
    start, end = (int(math.floor(value)) for value in action.frame_range)
    fps = scene.render.fps / scene.render.fps_base
    root = target.pose.bones["Root"]
    target_up = target.matrix_world.to_3x3() @ root.bone.matrix_local.to_3x3().col[2]
    target_up.normalize()
    tracked = [name for name in TRACKED_BONES if target.pose.bones.get(name) is not None]
    samples: list[dict[str, Any]] = []
    previous_position: Any = None
    previous_yaw: float | None = None
    first_position: Any = None
    for frame in range(start, end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        matrix = target.matrix_world @ root.matrix
        armature_matrix = root.matrix.copy()
        position = matrix.translation.copy()
        yaw = _yaw(matrix)
        root_up = matrix.to_3x3().col[2].normalized()
        tilt = math.acos(max(-1.0, min(1.0, float(root_up.dot(target_up)))))
        if first_position is None:
            first_position = position.copy()
        velocity = (position - previous_position) * fps if previous_position is not None else position * 0.0
        yaw_velocity = ((yaw - previous_yaw + math.pi) % (2.0 * math.pi) - math.pi) * fps if previous_yaw is not None else 0.0
        samples.append({
            "frame": frame, "seconds": round((frame - start) / fps, 7),
            "root_position": _vector(position - first_position), "root_velocity": _vector(velocity),
            "root_yaw_radians": round(yaw, 7), "root_yaw_velocity_radians": round(yaw_velocity, 7),
            "baked_female_root_transform": _transform(armature_matrix),
            "root_tilt_radians": round(tilt, 7),
            "tracked_bones_root_relative": {
                name: _vector((target.matrix_world @ target.pose.bones[name].matrix).translation - position)
                for name in tracked
            },
        })
        previous_position, previous_yaw = position, yaw
    return {
        "schema_version": METRICS_SCHEMA_VERSION, "action": action.name,
        "external_identity": motion.external_identity, "target_skeleton": target.name,
        "coordinate_space": "evaluated_female_world_z_up", "frame_start": start, "frame_end": end,
        "frame_count": end - start + 1, "duration_seconds": round((end - start) / fps, 7),
        "fps": fps, "tracked_bones": tracked, "root_source": "evaluated_kimodo_to_female_constraint_bake",
        "root_transform_evidence": "baked_female_armature_local_transform_v1",
        "samples": samples,
    }


def precision_leg_silhouette_sample(
    scene: bpy.types.Scene, target: bpy.types.Object, action: bpy.types.Action, role: str,
) -> tuple[dict[str, Any], list[float], list[dict[str, Vector]], list[tuple[float, float, float]]]:
    """Measure the baked Female's evaluated hip, knee, ankle, and foot-tip chains."""
    left_names, right_names = PRECISION_LEG_CHAINS
    if target.pose.bones.get("Root") is None or any(
        target.pose.bones.get(name) is None for name in (*left_names, *right_names)
    ):
        raise ProcessorError("Female precision leg-silhouette gate requires Root, thigh, calf, and foot chains.")
    data = target.animation_data_create()
    assert data is not None
    data.action = action
    root_rest = target.data.bones["Root"].matrix_local
    up = root_rest.to_3x3().col[2].normalized()
    left_rest = target.data.bones[left_names[0]].matrix_local.translation
    right_rest = target.data.bones[right_names[0]].matrix_local.translation
    lateral = left_rest - right_rest
    lateral -= up * lateral.dot(up)
    if lateral.length <= EPSILON:
        raise ProcessorError("Female precision leg-silhouette gate cannot derive Root-rest lateral basis.")
    lateral.normalize()
    scale = max(target.data.bones[name].length for name in (*left_names, *right_names))
    start, end = (int(math.floor(value)) for value in action.frame_range)
    samples: list[tuple[int, list[tuple[float, float]], list[tuple[float, float]]]] = []
    trace: list[float] = []
    chains: list[dict[str, Vector]] = []
    roots: list[tuple[float, float, float]] = []
    for frame in range(start, end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        def points(names: tuple[str, str, str]) -> list[tuple[float, float]]:
            matrices = [target.pose.bones[name].matrix for name in names]
            positions = [matrix.translation for matrix in matrices]
            positions.append(matrices[-1] @ Vector((0.0, target.data.bones[names[-1]].length, 0.0)))
            return [(float(point.dot(lateral)), float(point.dot(up))) for point in positions]
        left, right = points(left_names), points(right_names)
        samples.append((frame, left, right))
        root_matrix = target.pose.bones["Root"].matrix
        root_inverse = root_matrix.inverted()
        chains.append({
            name: root_inverse @ target.pose.bones[name].matrix.translation
            for name in (*left_names, *right_names)
        } | {
            f"{left_names[-1]}_tail": root_inverse @ (target.pose.bones[left_names[-1]].matrix @ Vector((0.0, target.data.bones[left_names[-1]].length, 0.0))),
            f"{right_names[-1]}_tail": root_inverse @ (target.pose.bones[right_names[-1]].matrix @ Vector((0.0, target.data.bones[right_names[-1]].length, 0.0))),
        })
        root_position = root_matrix.translation
        roots.append((float(root_position.x), float(root_position.y), float(root_position.z)))
    try:
        evidence = assess_precision_leg_silhouette(
            role, samples, scale, stage="female_bake", armature_identity=f"{target.name}:evaluated_baked_female",
            basis_evidence={
                "basis": "Female Root-rest positive-left lateral and Root-rest +Z up",
                "left_chain": [*left_names, "foot_l_tail"], "right_chain": [*right_names, "foot_r_tail"],
                "coordinate_space": "evaluated Female armature local",
            }, tolerance_scale=PRECISION_LEG_SILHOUETTE_TOLERANCE,
        )
    except LegSilhouetteError as error:
        raise ProcessorError(f"Female precision leg-silhouette evidence is invalid: {error}") from error
    for frame, left, right in samples:
        trace.append(float(assess_precision_leg_silhouette(
            role, [(frame, left, right)], scale, stage="female_bake", armature_identity=target.name,
            basis_evidence={}, tolerance_scale=PRECISION_LEG_SILHOUETTE_TOLERANCE,
        )["minimum_signed_separation"]))
    return evidence, trace, chains, roots


def precision_shared_idle_error(
    sr_chains: list[dict[str, Vector]], sl_chains: list[dict[str, Vector]], sr_frames: tuple[int, int], sl_frames: tuple[int, int],
) -> tuple[float, dict[str, Any]]:
    """Compare paired endpoints in each evaluated Root's no-transport anatomy space."""
    maximum = (-1.0, {})
    for sr_index, sl_index in ((0, -1), (-1, 0)):
        for bone, sr_point in sr_chains[sr_index].items():
            sl_point = sl_chains[sl_index][bone]
            for axis, coordinate in enumerate(("x", "y", "z")):
                error = abs(float(sr_point[axis] - sl_point[axis]))
                if error > maximum[0]:
                    maximum = (error, {
                        "coordinate_space": "evaluated_female_root_relative_no_transport",
                        "bone": bone,
                        "coordinate": coordinate,
                        "sr_frame": sr_frames[sr_index],
                        "sl_frame": sl_frames[sl_index],
                        "sr_value": float(sr_point[axis]),
                        "sl_value": float(sl_point[axis]),
                    })
    return maximum


def validate_precision_leg_silhouette_pair(
    scene: bpy.types.Scene, target: bpy.types.Object, actions: dict[str, bpy.types.Action], metrics: dict[str, dict[str, Any]],
) -> None:
    """Reject a baked pair unless its evaluated same-anatomy reversal remains intact."""
    if set(actions).isdisjoint({"sr", "sl"}):
        return
    if set(actions).intersection({"sr", "sl"}) != {"sr", "sl"}:
        raise ProcessorError("Female precision leg-silhouette gate requires sr and sl together for same-anatomy reversal.")
    sampled = {role: precision_leg_silhouette_sample(scene, target, actions[role], role) for role in ("sr", "sl")}
    for role, (evidence, _, _, _) in sampled.items():
        if not evidence["accepted"]:
            raise ProcessorError(f'Baked Female precision-step {role} leg silhouette rejected: {evidence["rejection_reason"]}')
        metrics[role]["precision_leg_silhouette"] = evidence
    sr_trace, sl_trace = sampled["sr"][1], sampled["sl"][1]
    if len(sr_trace) != len(sl_trace):
        raise ProcessorError("Baked Female precision-step pair has unequal temporal sample counts.")
    scale = max(float(sampled["sr"][0]["anatomical_scale"]), float(sampled["sl"][0]["anatomical_scale"]))
    reversal_error = max(abs(a - b) for a, b in zip(sr_trace, reversed(sl_trace), strict=True))
    sr_chains, sl_chains = sampled["sr"][2], sampled["sl"][2]
    idle_error, idle_extremum = precision_shared_idle_error(
        sr_chains, sl_chains,
        (int(actions["sr"].frame_range[0]), int(actions["sr"].frame_range[1])),
        (int(actions["sl"].frame_range[0]), int(actions["sl"].frame_range[1])),
    )
    sr_roots, sl_roots = sampled["sr"][3], sampled["sl"][3]
    root_reverse_error = max(
        abs((sr_roots[index][axis] - sr_roots[0][axis]) - (sl_roots[-index - 1][axis] - sl_roots[-1][axis]))
        for index in range(len(sr_roots)) for axis in range(3)
    )
    limit = scale * PRECISION_REVERSAL_TOLERANCE_SCALE
    if reversal_error > limit or idle_error > limit or root_reverse_error > limit:
        raise ProcessorError(
            "Baked Female precision-step same-anatomy temporal reversal rejected: "
            f"silhouette={reversal_error:.9f}, shared_idle={idle_error:.9f}, root_reverse_offset={root_reverse_error:.9f}, limit={limit:.9f}; "
            f"shared_idle_extremum={json.dumps(idle_extremum, sort_keys=True)}"
        )
    pair = {"same_anatomy_temporal_reversal_maximum_silhouette_error": reversal_error,
            "shared_idle_endpoint_maximum_error": idle_error, "root_reverse_offset_maximum_error": root_reverse_error,
            "shared_idle_endpoint_extremum": idle_extremum, "tolerance": limit, "accepted": True}
    for role in ("sr", "sl"):
        metrics[role]["precision_leg_silhouette_pair"] = pair


def event_provenance(source: bpy.types.Action, motion: ExportMotion) -> dict[str, Any] | None:
    """Decode complete source-to-derivation-to-final-output event provenance."""
    properties = {
        "source_directional_event_evidence": "anim002_source_directional_event_evidence",
        "event_derivation_evidence": "anim002_event_derivation_evidence",
        "post_repair_event_evidence": "anim002_post_repair_event_evidence",
    }
    if motion.runtime_capability == "idle_contact_free":
        if any(source.get(name) is not None for name in properties.values()):
            raise ProcessorError("Idle must not claim ANIM-002 event provenance.")
        return None
    try:
        decoded = {}
        for field, property_name in properties.items():
            encoded = source.get(property_name)
            if not isinstance(encoded, str):
                raise TypeError(f"missing {property_name}")
            decoded[field] = json.loads(encoded)
        start, end = (int(value) for value in source.frame_range)
        return validate_complete_event_provenance(
            motion.role,
            decoded["source_directional_event_evidence"],
            decoded["event_derivation_evidence"],
            decoded["post_repair_event_evidence"],
            start, end,
        )
    except (json.JSONDecodeError, TypeError, PhaseEventEvidenceError) as error:
        raise ProcessorError(f'Action "{motion.source_action}" has stale or incomplete event provenance: {error}') from error


def loop_repair_evidence(source: bpy.types.Action, motion: ExportMotion) -> dict[str, Any] | None:
    """Decode the materialiser's durable symmetric seam-repair provenance."""
    encoded = source.get("anim002_loop_repair_evidence")
    if encoded is None:
        if motion.runtime_capability in CYCLIC_TRAVEL_CAPABILITIES:
            raise ProcessorError(f'Action "{motion.source_action}" lacks selected loop-repair provenance.')
        return None
    if not isinstance(encoded, str):
        raise ProcessorError(f'Action "{motion.source_action}" has non-portable loop-repair evidence.')
    try:
        evidence = json.loads(encoded)
    except json.JSONDecodeError as error:
        raise ProcessorError(f'Action "{motion.source_action}" has invalid loop-repair evidence: {error}') from error
    sample_count = int(math.floor(source.frame_range[1])) - int(math.floor(source.frame_range[0])) + 1
    try:
        return validate_loop_repair_provenance(evidence, conceptual_sample_count=sample_count)
    except ValueError as error:
        raise ProcessorError(f'Action "{motion.source_action}" has stale or incomplete loop-repair evidence: {error}') from error


def reject_obsolete_loop_repair_metadata(source: bpy.types.Action, motion: ExportMotion) -> None:
    """Reject hidden or one-sided repair claims superseded by symmetric evidence."""
    obsolete = sorted(OBSOLETE_LOOP_REPAIR_FIELDS.intersection(source.keys()))
    if obsolete:
        fields = ", ".join(obsolete)
        raise ProcessorError(
            f'Action "{motion.source_action}" contains obsolete loop-repair metadata: {fields}.'
        )


def transport_evidence(source: bpy.types.Action, motion: ExportMotion, fps: float) -> dict[str, Any]:
    """Decode the materialiser-owned inclusive-sample timeline contract."""
    encoded = source.get("anim002_transport_contract")
    start, end = (int(value) for value in source.frame_range)
    if encoded is None and (motion.runtime_capability == "idle_contact_free" or ALLOW_DERIVED_TRANSPORT):
        return make_transport_evidence(
            frame_start=start, frame_end=end, fps=fps, is_loop=motion.is_loop,
        )
    if not isinstance(encoded, str):
        raise ProcessorError(f'Action "{motion.source_action}" lacks durable transport evidence.')
    try:
        evidence = json.loads(encoded)
        return validate_transport_evidence(
            evidence, frame_start=start, frame_end=end, fps=fps, is_loop=motion.is_loop,
        )
    except (json.JSONDecodeError, TypeError, TransportContractError) as error:
        raise ProcessorError(f'Action "{motion.source_action}" has invalid transport evidence: {error}') from error


def action_metadata(source: bpy.types.Action, motion: ExportMotion, metrics: dict[str, Any]) -> dict[str, Any]:
    fields = (
        "anim002_root_translation_trajectory", "anim002_root_yaw_trajectory", "anim002_phase",
        "anim002_boundary", "anim002_left_contact", "anim002_right_contact",
        "anim002_left_contact_confidence", "anim002_right_contact_confidence",
        "anim002_source_window_complete",
        "anim002_endpoint_displacement", "anim002_endpoint_yaw",
    )
    def json_value(value: Any) -> Any:
        if isinstance(value, (str, int, float, bool)) or value is None:
            return value
        if hasattr(value, "keys"):
            return {str(key): json_value(value[key]) for key in value.keys()}
        try:
            return [json_value(item) for item in value]
        except TypeError:
            raise ProcessorError("ANIM-002 source metadata contains a non-portable value.")

    retained = {field.removeprefix("anim002_"): json_value(source.get(field)) for field in fields if field in source}
    result = {
        "role": motion.role, "runtime_capability": motion.runtime_capability,
        "action": motion.internal_action, "external_identity": motion.external_identity,
        "source_action": motion.source_action, "source_loop_marker": motion.is_loop,
        "internal_loop_marker": motion.is_loop, "is_loop": motion.is_loop,
        "source_metadata": retained, "contacts": {
            "left": retained.get("left_contact", "undetermined"), "right": retained.get("right_contact", "undetermined"),
            "left_confidence": retained.get("left_contact_confidence", 0.0), "right_confidence": retained.get("right_contact_confidence", 0.0),
        }, "metrics": f"metrics/{motion.external_identity}.metrics.json",
        "provenance": {"retarget": "evaluated_visual_constraint_bake", "root_source": metrics["root_source"],
                        "selected_side": motion.selected_side, "selection_rationale": motion.selection_rationale},
        "transport": transport_evidence(source, motion, metrics["fps"]),
    }
    events = event_provenance(source, motion)
    if events is not None:
        result.update(events)
    repair = loop_repair_evidence(source, motion)
    if repair is not None:
        result["loop_repair_evidence"] = repair
    return result


def validate_baked(action: bpy.types.Action, motion: ExportMotion, source: bpy.types.Action, target: bpy.types.Object, scene: bpy.types.Scene) -> None:
    locations, rotations = root_paths(action)
    if len(locations) != 1 or len(rotations) != 1:
        raise ProcessorError(f'Baked action "{action.name}" must have exactly one Root location and rotation data path.')
    if not action.use_fake_user or not action_has_keys(action):
        raise ProcessorError(f'Baked action "{action.name}" is not persistent or has no keys.')
    deform_bones = {bone.name for bone in target.data.bones if bone.use_deform}
    baked_bones = action_bone_names(action)
    if not baked_bones or not baked_bones.issubset(deform_bones):
        raise ProcessorError(f'Baked action "{action.name}" contains non-deformation or helper tracks.')
    metrics = sample_metrics(scene, target, action, motion)
    positions = [sample["root_position"] for sample in metrics["samples"]]
    endpoint = positions[-1]
    translation_semantics = source.get("anim002_root_translation_trajectory")
    yaw_semantics = source.get("anim002_root_yaw_trajectory")
    scene.frame_set(math.floor(action.frame_range[0]))
    bpy.context.view_layer.update()
    opening_basis = target.pose.bones["Root"].matrix.to_3x3()
    rest_basis = target.data.bones["Root"].matrix_local.to_3x3()
    opening_angle = rest_basis.to_quaternion().rotation_difference(opening_basis.to_quaternion()).angle
    if opening_angle > OPENING_ROOT_ALIGNMENT_LIMIT_RADIANS:
        raise ProcessorError(f'Baked action "{action.name}" opening Female Root is not rest-aligned.')
    if motion.runtime_capability != "idle_contact_free" and max(sample["root_tilt_radians"] for sample in metrics["samples"]) > ROOT_TILT_LIMIT_RADIANS:
        raise ProcessorError(f'Baked action "{action.name}" exceeds the Root pitch/roll bound.')
    if translation_semantics == "zero_translation" and math.sqrt(sum(value * value for value in endpoint)) > EPSILON:
        raise ProcessorError(f'Baked action "{action.name}" violates zero_translation Root semantics.')
    if yaw_semantics == "zero_yaw" and abs(metrics["samples"][-1]["root_yaw_radians"] - metrics["samples"][0]["root_yaw_radians"]) > EPSILON:
        raise ProcessorError(f'Baked action "{action.name}" violates zero_yaw Root semantics.')


def remove_source_dependencies(target: bpy.types.Object, source: bpy.types.Object, baked: set[bpy.types.Action]) -> None:
    for bone in target.pose.bones:
        for constraint in list(bone.constraints):
            if constraint.target == source:
                bone.constraints.remove(constraint)
    for obj in list(bpy.data.objects):
        if obj != target and (obj.type == "ARMATURE" or obj.name.startswith("Kimodo_")):
            bpy.data.objects.remove(obj, do_unlink=True)
    for action in list(bpy.data.actions):
        if action not in baked:
            bpy.data.actions.remove(action, do_unlink=True)


def remove_non_animation_data(target: bpy.types.Object) -> None:
    """Reduce the published blend to the baked Female rig and its actions.

    Published files are animation carriers, not character scenes.  Removing the
    source meshes before saving also keeps Blender's scene importer from
    selecting a mesh/skeleton import path that differs from the intended Female
    animation-only path.
    """
    for obj in list(bpy.data.objects):
        if obj != target:
            bpy.data.objects.remove(obj, do_unlink=True)
    for mesh in list(bpy.data.meshes):
        bpy.data.meshes.remove(mesh, do_unlink=True)
    for material in list(bpy.data.materials):
        bpy.data.materials.remove(material, do_unlink=True)
    for image in list(bpy.data.images):
        bpy.data.images.remove(image, do_unlink=True)
    # Removal can expose nested unused data (for example node groups retained
    # through a material). Purge until Blender reports no further orphans.
    while bpy.ops.outliner.orphans_purge(do_recursive=True) != {"CANCELLED"}:
        pass


def validate_reopened(path: Path, expected: tuple[ExportMotion, ...]) -> None:
    bpy.ops.wm.open_mainfile(filepath=str(path))
    target, source = bpy.data.objects.get("Female"), bpy.data.objects.get("Kimodo_Source")
    if target is None or source is not None or target.type != "ARMATURE" or len(bpy.data.objects) != 1:
        raise ProcessorError("Reopened package does not contain only the required Female rig.")
    if bpy.data.meshes or bpy.data.materials or bpy.data.images:
        raise ProcessorError("Reopened package retains mesh, material, or image datablocks.")
    if len(bpy.data.armatures) != 1:
        raise ProcessorError("Reopened package retains a source or helper armature datablock.")
    if any(constraint.target and constraint.target.name.startswith("Kimodo_") for bone in target.pose.bones for constraint in bone.constraints):
        raise ProcessorError("Reopened package retains a Kimodo constraint dependency.")
    for motion in expected:
        action = bpy.data.actions.get(motion.internal_action)
        if action is None:
            raise ProcessorError(f'Reopened package is missing "{motion.internal_action}".')
        locations, rotations = root_paths(action)
        if len(locations) != 1 or len(rotations) != 1:
            raise ProcessorError(f'Reopened action "{action.name}" has invalid Root tracks.')
        deform_bones = {bone.name for bone in target.data.bones if bone.use_deform}
        if not action_bone_names(action).issubset(deform_bones):
            raise ProcessorError(f'Reopened action "{action.name}" retains helper tracks.')
        if not any(track.mute and track.lock and any(strip.action == action and strip.mute for strip in track.strips) for track in target.animation_data.nla_tracks):
            raise ProcessorError(f'Reopened action "{action.name}" lacks a muted locked NLA reference.')
        if bpy.context.scene.frame_start > math.floor(action.frame_range[0]) or bpy.context.scene.frame_end < math.ceil(action.frame_range[1]):
            raise ProcessorError(f'Reopened scene range truncates action "{action.name}" for downstream import.')
        if motion.runtime_capability != "idle_contact_free":
            # The baked Action and index are independent durable carriers. A
            # later package repair must not silently retain only one of them.
            event_provenance(action, motion)
        loop_repair_evidence(action, motion)
        transport_evidence(action, motion, bpy.context.scene.render.fps / bpy.context.scene.render.fps_base)
    precision_actions = {
        motion.role: bpy.data.actions[motion.internal_action]
        for motion in expected
        if motion.runtime_capability in {"confined_side_step_right", "confined_side_step_left"}
    }
    if precision_actions:
        validate_precision_leg_silhouette_pair(bpy.context.scene, target, precision_actions, {role: {} for role in precision_actions})


def publish(stage: Path, output: Path) -> None:
    backup = output.with_name(f".{output.name}.previous-{uuid.uuid4().hex}")
    try:
        if output.exists():
            os.replace(output, backup)
        os.replace(stage, output)
    except Exception:
        if backup.exists() and not output.exists():
            os.replace(backup, output)
        raise
    finally:
        if backup.exists():
            shutil.rmtree(backup)


def preserve_unmanaged_package_files(stage: Path, output: Path) -> None:
    """Carry Godot sidecars and other non-processor files through replacement."""
    if not output.is_dir():
        return
    for existing in output.rglob("*"):
        relative = existing.relative_to(output)
        destination = stage / relative
        if destination.exists() or existing.is_dir():
            continue
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(existing, destination)


def main() -> None:
    global ALLOW_DERIVED_TRANSPORT
    args = parse_args()
    ALLOW_DERIVED_TRANSPORT = args.derive_missing_transport
    try:
        artefacts = derive_artefact_paths(args.character_root, args.variant)
    except (BootstrapPathError, ValueError) as error:
        raise ProcessorError(f"Invalid character-root/variant derivation: {error}") from error
    variant = artefacts.variant
    if args.package == "locomotion":
        source_path = (args.workspace or args.source or artefacts.authoring_workspace).resolve()
    else:
        source_path = (args.source or LEGACY_IDLE_SOURCE).resolve()
    if not source_path.is_file():
        raise ProcessorError(f'Source blend does not exist: {source_path}')
    source_carrier_binding = None
    if args.materialisation_report is not None:
        if args.package != "locomotion":
            raise ProcessorError("--materialisation-report is valid only for locomotion.")
        try:
            materialisation_binding = validate_materialisation_report(args.materialisation_report)
        except WorkflowBindingError as error:
            raise ProcessorError(f"Invalid materialisation carrier binding: {error}") from error
        if Path(materialisation_binding["final"]["path"]).expanduser().resolve() != source_path:
            raise ProcessorError("Materialisation report does not name the selected locomotion source.")
        source_carrier_binding = {
            "contract": materialisation_binding["contract"],
            "source_sha256": materialisation_binding["final"]["sha256"],
            "materialisation_report_sha256": sha256_file(args.materialisation_report.resolve()),
            "action_provenance_sha256": materialisation_binding["action_provenance_sha256"],
        }
    motions = locomotion_exports(variant) if args.package == "locomotion" else (idle_export(variant, args.idle_source_action),)
    if args.roles:
        allowed = {item.strip() for item in args.roles.split(",")}
        motions = tuple(item for item in motions if item.role in allowed)
        if not motions or allowed != {item.role for item in motions}:
            raise ProcessorError("--roles must name one or more published projected roles.")
    if args.dry_run:
        print(json.dumps({"package": args.package, "variant": variant, "motions": [item.__dict__ for item in motions]}, sort_keys=True))
        return
    if args.output_root is not None:
        output = args.output_root.resolve() / args.package
    elif args.package == "locomotion":
        # The bootstrap artefact contract owns the flattened processed directory.
        output = artefacts.processed_package.parent
    else:
        output = ROOT / published_output_root(artefacts.character_asset_root.relative_to(ROOT), "idle")
    provenance_path = (
        Path(args.provenance_path)
        if args.provenance_path is not None
        else (
            _workspace_provenance_path(source_path, artefacts.authoring_workspace)
            if args.package == "locomotion" and args.workspace is not None
            else Path(f"source/{args.package}/{portable_source_name(source_path.name)}")
        )
    )
    if provenance_path.is_absolute() or ".." in provenance_path.parts:
        raise ProcessorError("--provenance-path must be a portable package-relative path.")
    stage = output.with_name(f".{output.name}.staging-{uuid.uuid4().hex}")
    try:
        stage.mkdir(parents=True)
        bpy.ops.wm.open_mainfile(filepath=str(source_path))
        scene, target, source = bpy.context.scene, bpy.data.objects.get("Female"), bpy.data.objects.get("Kimodo_Source")
        validate_rigs(target, source)
        assert target is not None and source is not None
        coordinate_contract = make_coordinate_contract(target)
        authored_constraints = constraint_graph(target)
        authored_helpers = helper_configuration(source)
        source_actions: dict[str, bpy.types.Action] = {}
        for motion in motions:
            source_action = bpy.data.actions.get(motion.source_action)
            if source_action is None:
                raise ProcessorError(f'Required materialised action "{motion.source_action}" was not found.')
            reject_obsolete_loop_repair_metadata(source_action, motion)
            event_provenance(source_action, motion)
            source_actions[motion.source_action] = source_action
        # Transitions have no _loop suffix, so their source and baked action
        # identities coincide. Rename only the in-memory source datablock; the
        # immutable input file is never saved and the old action is discarded.
        for motion in motions:
            if motion.source_action == motion.internal_action:
                source_actions[motion.source_action].name = f"__KimodoSource__{motion.source_action}"
        baked: set[bpy.types.Action] = set()
        entries: list[dict[str, Any]] = []
        precision_actions: dict[str, bpy.types.Action] = {}
        precision_metrics: dict[str, dict[str, Any]] = {}
        for motion in motions:
            source_action = source_actions[motion.source_action]
            if motion.runtime_capability == "confined_side_step_left":
                sr = precision_actions.get("sr")
                if sr is None:
                    raise ProcessorError("Precision sl must be derived after baking its native sr counterpart.")
                output_action = derive_baked_temporal_reversal(sr, motion)
            else:
                output_action = bake_action(scene, target, source, motion, source_action)
            validate_constraint_graph(target, authored_constraints)
            validate_helper_configuration(source, authored_helpers)
            enforce_baked_root_translation_semantics(output_action, source_action, motion)
            for property_name in (
                "anim002_source_directional_event_evidence",
                "anim002_event_derivation_evidence",
                "anim002_post_repair_event_evidence",
            ):
                encoded_event_evidence = source_action.get(property_name)
                if encoded_event_evidence is not None:
                    output_action[property_name] = encoded_event_evidence
            encoded_loop_repair = source_action.get("anim002_loop_repair_evidence")
            if encoded_loop_repair is not None:
                loop_repair_evidence(source_action, motion)
                output_action["anim002_loop_repair_evidence"] = encoded_loop_repair
            transport = transport_evidence(source_action, motion, scene.render.fps / scene.render.fps_base)
            output_action["anim002_transport_contract"] = json.dumps(
                transport, sort_keys=True, separators=(",", ":"),
            )
            with baked_action_evaluation(target):
                validate_baked(output_action, motion, source_action, target, scene)
                metrics = sample_metrics(scene, target, output_action, motion)
            if motion.runtime_capability in {"confined_side_step_right", "confined_side_step_left"}:
                precision_actions[motion.role] = output_action
                precision_metrics[motion.role] = metrics
            validate_constraint_graph(target, authored_constraints)
            persist_action(output_action, target)
            entries.append(action_metadata(source_action, motion, metrics))
            baked.add(output_action)
        with baked_action_evaluation(target):
            validate_precision_leg_silhouette_pair(scene, target, precision_actions, precision_metrics)
        # The processed package is variant-scoped: the authoring blend stays at the
        # set level while the index, metrics, and later provenance live under the
        # variant metadata directory.
        variant_stage = stage / variant
        (variant_stage / "metrics").mkdir(parents=True, exist_ok=True)
        for motion, entry in zip(motions, entries, strict=True):
            metrics_path = variant_stage / "metrics" / f"{motion.external_identity}.metrics.json"
            metrics = precision_metrics.get(motion.role)
            if metrics is None:
                # Non-precision entries were already serialised into their index row.
                action = bpy.data.actions[motion.internal_action]
                with baked_action_evaluation(target):
                    metrics = sample_metrics(scene, target, action, motion)
            metrics_path.write_text(json.dumps(metrics, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        validate_constraint_graph(target, authored_constraints)
        validate_helper_configuration(source, authored_helpers)
        remove_source_dependencies(target, source, baked)
        remove_non_animation_data(target)
        # Godot's Blender importer clamps every imported Action to the scene frame
        # range.  Preserve the complete union so long actions (notably p45 and
        # strafe cycles) are not silently truncated to whichever action baked last.
        scene.frame_start = min(math.floor(action.frame_range[0]) for action in baked)
        scene.frame_end = max(math.ceil(action.frame_range[1]) for action in baked)
        blend_name = f"{args.package}_{variant}.blend"
        bpy.ops.wm.save_as_mainfile(filepath=str(stage / blend_name), check_existing=False)
        validate_reopened(stage / blend_name, motions)
        index = {"schema_version": 2, "metrics_schema_version": METRICS_SCHEMA_VERSION, "package": args.package, "variant": variant, "source_blend": portable_source_name(source_path.name),
                  "source_path": provenance_path.as_posix(), "target_skeleton": "Female",
                  "coordinate_contract": coordinate_contract, "action_count": len(entries), "actions": entries}
        if source_carrier_binding is not None:
            index["source_carrier_binding"] = source_carrier_binding
        (variant_stage / "processed-index.json").write_text(json.dumps(index, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        preserve_unmanaged_package_files(stage, output)
        publish(stage, output)
        try:
            display_output = str(output.relative_to(ROOT))
        except ValueError:
            display_output = f"<external-output>/{output.name}"
        print("KIMODO_RETARGET_JSON=" + json.dumps({"package": args.package, "variant": variant, "output": display_output, "actions": [item.internal_action for item in motions]}, sort_keys=True))
    except Exception:
        if stage.exists():
            shutil.rmtree(stage)
        raise

