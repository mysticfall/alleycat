#!/usr/bin/env python3
"""Retarget one Mixamo FBX animation directly onto the reference female rig.

Run with Blender, for example:

    blender --background --python tools/retarget_mixamo_animation.py -- \
      --manifest game/assets/characters/reference/female/animations/source/mixamo/manifest.csv \
      --motion-id c9cef1a4-b96c-11e4-a802-0aaa78deedf9 \
      --mixamo-root ~/workspace/mixamo \
      --target-rig game/assets/characters/reference/female/reference_female.blend \
      --output game/assets/characters/reference/female/animations/source/mixamo/retargeted.blend
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import bpy
from mathutils import Euler, Matrix, Vector

SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))

from mixamo_root_policy import (
    ROOT_POLICY_MOVING,
    ROOT_POLICY_TURN_IN_PLACE,
    ROOT_SUBTYPE_CURVED_MOVING,
    RootReconstructionInput,
    classify_root_reconstruction,
    signed_turn_angle_from_metadata,
    straight_moving_diagnostics_from_metadata,
    straight_moving_plan_from_metadata,
)


GENERATED_RIG_RETARGET_PRESET_NAME = "MakeHuman__GameEngine.py"
MIXAMO_RETARGET_PRESET_NAME = "Mixamo"
MANIFEST_COLUMNS = ("motion_id", "name", "description", "type", "file")
NLA_REFERENCE_NAME_LIMIT = 63
NLA_REFERENCE_PREFIX = "LinkedActionRef"
RETARGET_COPY_LOCATION_PROPERTY = "loc_constraints"
METRICS_SCHEMA_VERSION = 2
METRICS_COORDINATE_SPACE = "root_relative_blender_z_up_y_forward"
METRICS_FUTURE_TRAJECTORY_SECONDS = (0.2, 0.5, 1.0)
METRICS_BONES = (
    "Root",
    "pelvis",
    "head",
    "foot_l",
    "foot_r",
    "ball_l",
    "ball_r",
    "hand_l",
    "hand_r",
)
METRICS_SAMPLE_FEATURE_BONES = (
    ("root", "Root"),
    ("hips", "pelvis"),
    ("left_foot", "foot_l"),
    ("right_foot", "foot_r"),
)
ROOT_MOTION_STATIC_PLANAR_EPSILON = 1e-4
ROOT_GROUND_EPSILON = 1e-5
ROOT_LOCAL_X_ROTATION_EPSILON = 1e-5
ROOT_REST_CANONICAL_EPSILON = 1e-5
MIXAMO_SANITISE_TRANSLATION_EPSILON = 1e-4
MIXAMO_SANITISE_ANGULAR_EPSILON = 1e-4
ROOT_VERTICAL_AXIS_MIN_CONFIDENCE = 0.75
ROOT_VERTICAL_AXIS_AMBIGUITY_EPSILON = 0.05
ROOT_LOCATION_DATA_PATH = 'pose.bones["Root"].location'


class ScriptError(Exception):
    """Raised for expected user-facing script failures."""


@dataclass(frozen=True)
class MotionManifestRow:
    motion_id: str
    name: str
    description: str
    motion_type: str
    file: str

    def fbx_path(self, mixamo_root: Path) -> Path:
        return mixamo_root / self.file


@dataclass(frozen=True)
class RetargetArgs:
    manifest: Path
    motion_id: str | None
    mixamo_root: Path
    target_rig: Path
    output: Path
    prepare_inspection: bool
    create_root_motion: bool
    skip_root_reconstruction: bool
    selection_category: str
    selection_motion_class: str
    selection_tags: tuple[str, ...]
    metrics_output: Path | None


@dataclass(frozen=True)
class RootMotionMetadata:
    source: str
    created: bool
    action_name: str | None = None
    diagnostics: dict[str, Any] | None = None


def blender_script_args(argv: list[str]) -> list[str]:
    """Return script arguments after Blender's optional `--` separator."""

    if "--" in argv:
        return argv[argv.index("--") + 1 :]

    return argv[1:]


def absolute_path(value: str) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = Path.cwd() / path
    return path.resolve()


def parse_args(argv: list[str]) -> RetargetArgs:
    parser = argparse.ArgumentParser(
        description="Retarget one Mixamo FBX from a manifest onto a target Blender armature."
    )
    parser.add_argument("--manifest", required=True, help="Mixamo manifest CSV path.")
    parser.add_argument("--motion-id", help="Manifest motion_id to retarget.")
    parser.add_argument(
        "--mixamo-root",
        required=True,
        help="Local root directory containing Mixamo FBX files. Not persisted.",
    )
    parser.add_argument("--target-rig", required=True, help="Target rig .blend path.")
    parser.add_argument("--output", required=True, help="Output .blend path to save.")
    parser.add_argument(
        "--metrics-output",
        help=(
            "Optional JSON sidecar path for prototype motion metrics extracted from the "
            "baked target action."
        ),
    )
    parser.add_argument(
        "--prepare-inspection",
        action="store_true",
        help=(
            "Prepare and save the Retarget setup for manual inspection without binding, "
            "baking, or removing imported Mixamo source objects/actions."
        ),
    )
    parser.add_argument(
        "--create-root-motion",
        action="store_true",
        help=(
            "Exceptional debug option: when reconstruction is skipped and the baked target Root "
            "bone is effectively static, synthesise planar root motion from baked pelvis "
            "displacement before metrics extraction and saving. Normal ANIM-002 outputs use "
            "Root reconstruction instead."
        ),
    )
    parser.add_argument(
        "--skip-root-reconstruction",
        action="store_true",
        help=(
            "Exceptional debug option: preserve raw baked Root tracks and skip Mixamo Root "
            "reconstruction/sanitisation. Normal ANIM-002 outputs must not use this."
        ),
    )
    parser.add_argument("--selection-category", default="", help="Curated selection category metadata.")
    parser.add_argument("--selection-motion-class", default="", help="Curated selection motion_class metadata.")
    parser.add_argument(
        "--selection-tags",
        default="",
        help="Semicolon-separated curated selection tags used for Root reconstruction policy selection.",
    )

    parsed = parser.parse_args(blender_script_args(argv))
    if parsed.prepare_inspection and parsed.create_root_motion:
        parser.error(
            "--create-root-motion requires normal retarget mode and cannot be combined with "
            "--prepare-inspection."
        )
    if parsed.create_root_motion and not parsed.skip_root_reconstruction:
        parser.error("--create-root-motion is only valid with --skip-root-reconstruction debug output.")

    selection_tags = tuple(tag.strip().lower() for tag in parsed.selection_tags.split(";") if tag.strip())

    return RetargetArgs(
        manifest=absolute_path(parsed.manifest),
        motion_id=parsed.motion_id.strip() if parsed.motion_id else None,
        mixamo_root=absolute_path(parsed.mixamo_root),
        target_rig=absolute_path(parsed.target_rig),
        output=absolute_path(parsed.output),
        prepare_inspection=parsed.prepare_inspection,
        create_root_motion=parsed.create_root_motion,
        skip_root_reconstruction=parsed.skip_root_reconstruction,
        selection_category=parsed.selection_category.strip().lower(),
        selection_motion_class=parsed.selection_motion_class.strip(),
        selection_tags=selection_tags,
        metrics_output=absolute_path(parsed.metrics_output) if parsed.metrics_output else None,
    )


def load_manifest(manifest_path: Path) -> list[MotionManifestRow]:
    if not manifest_path.is_file():
        raise ScriptError(f'Mixamo manifest was not found at "{manifest_path}".')

    try:
        with manifest_path.open("r", encoding="utf-8", newline="") as handle:
            reader = csv.DictReader(handle)
            fieldnames = tuple(reader.fieldnames or ())
            if fieldnames != MANIFEST_COLUMNS:
                expected = ",".join(MANIFEST_COLUMNS)
                actual = ",".join(fieldnames) or "none"
                raise ScriptError(
                    f'Mixamo manifest "{manifest_path}" must define columns {expected}; '
                    f"actual columns: {actual}."
                )

            rows = [
                MotionManifestRow(
                    motion_id=(row.get("motion_id") or "").strip(),
                    name=(row.get("name") or "").strip(),
                    description=(row.get("description") or "").strip(),
                    motion_type=(row.get("type") or "").strip(),
                    file=(row.get("file") or "").strip(),
                )
                for row in reader
            ]
    except ScriptError:
        raise
    except OSError as exc:
        raise ScriptError(f'Could not read Mixamo manifest "{manifest_path}": {exc}') from exc

    invalid_rows = [index + 2 for index, row in enumerate(rows) if not row.motion_id or not row.file]
    if invalid_rows:
        line_list = ", ".join(str(line) for line in invalid_rows[:10])
        raise ScriptError(
            f'Mixamo manifest "{manifest_path}" has rows with empty motion_id or file: '
            f"{line_list}."
        )
    if not rows:
        raise ScriptError(f'Mixamo manifest "{manifest_path}" contains no motion rows.')

    return rows


def select_manifest_row(rows: list[MotionManifestRow], args: RetargetArgs) -> MotionManifestRow:
    if args.motion_id:
        matches = [row for row in rows if row.motion_id == args.motion_id]
        if not matches:
            raise ScriptError(
                f'Mixamo manifest "{args.manifest}" does not contain motion_id '
                f'"{args.motion_id}".'
            )
        if len(matches) > 1:
            raise ScriptError(
                f'Mixamo manifest "{args.manifest}" contains duplicate motion_id '
                f'"{args.motion_id}".'
            )
        row = matches[0]
        if not row.fbx_path(args.mixamo_root).is_file():
            raise ScriptError(
                f'Requested Mixamo FBX for motion_id "{row.motion_id}" was not found at '
                f'"{row.fbx_path(args.mixamo_root)}".'
            )
        return row

    for row in rows:
        if row.fbx_path(args.mixamo_root).is_file():
            return row

    raise ScriptError(
        f'No manifest FBX files exist under mixamo root "{args.mixamo_root}" for '
        f'manifest "{args.manifest}".'
    )


def get_retarget_preset_apply_operator():
    try:
        return bpy.ops.object.expy_kit_armature_preset_apply
    except AttributeError as exc:
        raise ScriptError(
            "Retarget operator bpy.ops.object.expy_kit_armature_preset_apply is not available. "
            "Ensure the Retarget extension is installed and enabled."
        ) from exc


def get_retarget_constrain_to_armature_operator():
    try:
        return bpy.ops.armature.retarget_constrain_to_armature
    except AttributeError as exc:
        raise ScriptError(
            "Retarget operator bpy.ops.armature.retarget_constrain_to_armature is not "
            "available. Ensure the Retarget extension is installed and enabled."
        ) from exc


def ensure_retarget_copy_location_property_available(failure_context: str) -> None:
    constrain_to_armature = get_retarget_constrain_to_armature_operator()
    property_names = constrain_to_armature.get_rna_type().properties.keys()
    if RETARGET_COPY_LOCATION_PROPERTY not in property_names:
        raise ScriptError(
            f'{failure_context} failed because the Retarget constrain operator does not expose '
            f'the "{RETARGET_COPY_LOCATION_PROPERTY}" Copy Location property. Ensure the '
            "Retarget extension version matches the expected Bind to Active Armature panel."
        )


def prepare_retarget_copy_location_for_manual_bind(failure_context: str) -> None:
    """Prime the Retarget operator history so manual inspection opens with Copy Location on."""

    ensure_retarget_copy_location_property_available(failure_context)

    try:
        operator_properties = bpy.context.window_manager.operator_properties_last(
            "armature.retarget_constrain_to_armature"
        )
        setattr(operator_properties, RETARGET_COPY_LOCATION_PROPERTY, True)
    except Exception as exc:
        raise ScriptError(
            f"{failure_context} failed because Blender could not prepare the Retarget "
            f"Copy Location operator setting for manual inspection: {exc}"
        ) from exc


def resolve_retarget_preset_path(preset_name: str) -> Path:
    presets_dir = bpy.utils.user_resource(
        "SCRIPTS",
        path=os.path.join("presets", "retarget", "humanoid"),
        create=False,
    )
    if not presets_dir:
        raise ScriptError(
            "Retarget preset setup failed because Blender could not resolve the user resource "
            "directory for presets/retarget/humanoid."
        )

    candidate_names = [preset_name]
    if not preset_name.endswith(".py"):
        candidate_names.append(f"{preset_name}.py")

    for candidate_name in candidate_names:
        preset_path = Path(presets_dir) / candidate_name
        if preset_path.is_file():
            return preset_path

    candidate_list = ", ".join(str(Path(presets_dir) / name) for name in candidate_names)
    raise ScriptError(
        "Retarget preset setup failed because no preset file was found. Checked: "
        f"{candidate_list}. Ensure the Retarget extension presets are installed."
    )


def ensure_object_mode(active_armature: bpy.types.Object, failure_context: str) -> None:
    if active_armature.name not in bpy.context.view_layer.objects:
        raise ScriptError(
            f'{failure_context} failed because armature "{active_armature.name}" is not in '
            "the active view layer."
        )

    try:
        bpy.context.view_layer.objects.active = active_armature
        if bpy.context.mode != "OBJECT":
            result = bpy.ops.object.mode_set(mode="OBJECT")
            if "CANCELLED" in result and bpy.context.mode != "OBJECT":
                raise RuntimeError(f"mode_set returned {result}")
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because Blender could not switch armature '
            f'"{active_armature.name}" to Object mode: {exc}'
        ) from exc


def select_single_armature_for_retarget(target: bpy.types.Object, failure_context: str) -> None:
    if target.type != "ARMATURE":
        raise ScriptError(
            f'{failure_context} failed because object "{target.name}" is type '
            f'"{target.type}", not "ARMATURE".'
        )

    ensure_object_mode(target, failure_context)
    try:
        for obj in bpy.context.view_layer.objects:
            obj.select_set(False)
        target.select_set(True)
        bpy.context.view_layer.objects.active = target
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because Blender could not make armature '
            f'"{target.name}" the only selected object: {exc}'
        ) from exc


def validate_retarget_preset_application(target: bpy.types.Object, failure_context: str) -> None:
    settings = getattr(target.data, "retarget_retarget", None)
    if settings is None:
        raise ScriptError(
            f'{failure_context} failed because armature "{target.name}" does not expose '
            "data.retarget_retarget. Ensure the Retarget extension is installed and enabled."
        )

    has_settings = getattr(settings, "has_settings", None)
    if callable(has_settings) and not has_settings():
        raise ScriptError(
            f'{failure_context} failed because preset application left armature '
            f'"{target.name}" with an empty retarget_retarget settings tree.'
        )


def apply_retarget_preset(target: bpy.types.Object, preset_name: str, failure_context: str) -> None:
    preset_path = resolve_retarget_preset_path(preset_name)
    select_single_armature_for_retarget(target, failure_context)
    apply_preset = get_retarget_preset_apply_operator()

    try:
        result = apply_preset(filepath=str(preset_path), menu_idname="VIEW3D_MT_retarget_presets")
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because Blender could not apply retarget preset '
            f'"{preset_name}" from "{preset_path}" to armature "{target.name}": {exc}'
        ) from exc

    if "CANCELLED" in result:
        raise ScriptError(
            f'{failure_context} failed because retarget preset operator returned {result} for '
            f'preset "{preset_name}" on armature "{target.name}".'
        )

    validate_retarget_preset_application(target, failure_context)


def prime_retarget_constrain_operator_mode_state(failure_context: str) -> str | None:
    current_mode = bpy.context.mode
    if not isinstance(current_mode, str) or not current_mode:
        raise ScriptError(
            f"{failure_context} failed because Blender did not expose a valid current mode "
            "before invoking the Retarget constrain operator."
        )

    operator_type = None
    for module in list(sys.modules.values()):
        candidate = getattr(module, "ConstrainToArmature", None)
        if getattr(candidate, "bl_idname", None) == "armature.retarget_constrain_to_armature":
            operator_type = candidate
            break

    if operator_type is None:
        raise ScriptError(
            f"{failure_context} failed because the Retarget constrain operator class could not "
            "be located after the operator was registered. Ensure the Retarget extension is "
            "installed and enabled."
        )

    previous_mode = getattr(operator_type, "current_m", None)
    setattr(operator_type, "current_m", current_mode)
    return previous_mode


def restore_retarget_constrain_operator_mode_state(previous_mode: str | None) -> None:
    for module in list(sys.modules.values()):
        candidate = getattr(module, "ConstrainToArmature", None)
        if getattr(candidate, "bl_idname", None) == "armature.retarget_constrain_to_armature":
            setattr(candidate, "current_m", previous_mode)
            return


def select_armatures_for_retarget_bind(
    target_armature: bpy.types.Object,
    source_armature: bpy.types.Object,
) -> None:
    ensure_object_mode(source_armature, "Retarget bind")
    try:
        for obj in bpy.context.view_layer.objects:
            obj.select_set(False)
        target_armature.select_set(True)
        source_armature.select_set(True)
        bpy.context.view_layer.objects.active = source_armature
    except Exception as exc:
        raise ScriptError(
            "Retarget bind failed because Blender could not select exactly the target and "
            f'source armatures before binding: {exc}'
        ) from exc


def prepare_armatures_for_retarget_panel(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    source_armature: bpy.types.Object,
) -> None:
    """Leave the Retarget panel in the same source-active state used before binding."""

    failure_context = "Retarget inspection setup"
    validate_retarget_preset_application(target_armature, failure_context)
    validate_retarget_preset_application(source_armature, failure_context)
    prepare_retarget_copy_location_for_manual_bind(failure_context)

    if hasattr(scene, "retarget_bind_to"):
        try:
            scene.retarget_bind_to = source_armature
        except Exception as exc:
            raise ScriptError(
                f'{failure_context} failed because scene "{scene.name}" could not store '
                f'retarget_bind_to="{source_armature.name}": {exc}'
            ) from exc

        if getattr(scene, "retarget_bind_to", None) != source_armature:
            raise ScriptError(
                f'{failure_context} failed because scene "{scene.name}" did not retain '
                f'retarget_bind_to="{source_armature.name}".'
            )
    else:
        print(
            f'WARNING: {failure_context} could not set scene "{scene.name}" retarget_bind_to '
            "because the Retarget add-on did not expose that property.",
            file=sys.stderr,
        )

    select_armatures_for_retarget_bind(target_armature, source_armature)


def validate_retarget_bind_result(
    target_armature: bpy.types.Object,
    source_armature: bpy.types.Object,
) -> None:
    pose = target_armature.pose
    matching_constraints = []
    if pose is not None:
        for pose_bone in pose.bones:
            for constraint in pose_bone.constraints:
                if getattr(constraint, "target", None) == source_armature:
                    matching_constraints.append(f"{pose_bone.name}:{constraint.name}")

    if matching_constraints:
        return

    raise ScriptError(
        "Retarget bind failed because direct Mixamo preset retargeting did not add any target "
        f'pose constraints from target armature "{target_armature.name}" to source armature '
        f'"{source_armature.name}". This is a blocker; no workaround was attempted.'
    )


def bind_target_to_source(scene: bpy.types.Scene, target: bpy.types.Object, source: bpy.types.Object) -> None:
    failure_context = "Retarget bind"
    validate_retarget_preset_application(target, failure_context)
    validate_retarget_preset_application(source, failure_context)
    ensure_retarget_copy_location_property_available(failure_context)
    if not hasattr(scene, "retarget_bind_to"):
        raise ScriptError(
            f'{failure_context} failed because scene "{scene.name}" does not expose '
            "retarget_bind_to. Ensure the Retarget extension is installed and enabled."
        )

    try:
        scene.retarget_bind_to = source
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because scene "{scene.name}" could not store '
            f'retarget_bind_to="{source.name}": {exc}'
        ) from exc

    select_armatures_for_retarget_bind(target, source)
    constrain_to_armature = get_retarget_constrain_to_armature_operator()
    previous_mode_state = prime_retarget_constrain_operator_mode_state(failure_context)
    try:
        result = constrain_to_armature(
            "INVOKE_DEFAULT",
            force_dialog=False,
            src_preset="--Current--",
            trg_preset="--Current--",
            loc_constraints=True,
            play=False,
            action_range=False,
            custom_Frame=scene.frame_current,
        )
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because the Retarget constrain operator could not bind '
            f'target armature "{target.name}" to Mixamo source armature "{source.name}" '
            f"without the dialog: {exc}. This is a blocker; no workaround was attempted."
        ) from exc
    finally:
        restore_retarget_constrain_operator_mode_state(previous_mode_state)

    if "CANCELLED" in result:
        raise ScriptError(
            f'{failure_context} failed because the Retarget constrain operator returned {result} '
            f'for target armature "{target.name}" and Mixamo source armature '
            f'"{source.name}". This is a blocker; no workaround was attempted.'
        )

    validate_retarget_bind_result(target, source)


def identify_target_armature() -> bpy.types.Object:
    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    if len(armatures) == 1:
        return armatures[0]
    names = ", ".join(sorted(obj.name for obj in armatures)) or "none"
    if not armatures:
        raise ScriptError("Target rig blend contains no armature objects.")
    raise ScriptError(
        "Target rig blend must contain exactly one target armature for direct Mixamo "
        f"retargeting. Found {len(armatures)} armatures: {names}."
    )


def import_fbx(fbx_path: Path) -> tuple[set[bpy.types.Object], set[bpy.types.Action]]:
    if not fbx_path.is_file():
        raise ScriptError(f'Mixamo FBX was not found at "{fbx_path}".')

    objects_before = set(bpy.data.objects)
    actions_before = set(bpy.data.actions)

    try:
        if hasattr(bpy.ops, "import_scene") and hasattr(bpy.ops.import_scene, "fbx"):
            result = bpy.ops.import_scene.fbx(filepath=str(fbx_path), use_anim=True)
        elif hasattr(bpy.ops.wm, "fbx_import"):
            result = bpy.ops.wm.fbx_import(filepath=str(fbx_path), use_anim=True)
        else:
            raise ScriptError(
                "No FBX import operator is available. Expected bpy.ops.import_scene.fbx or "
                "bpy.ops.wm.fbx_import. Ensure Blender has FBX import support enabled."
            )
    except ScriptError:
        raise
    except Exception as exc:
        raise ScriptError(f'Blender could not import Mixamo FBX "{fbx_path}": {exc}') from exc

    if "CANCELLED" in result:
        raise ScriptError(f'FBX import operator returned {result} for "{fbx_path}".')

    return set(bpy.data.objects) - objects_before, set(bpy.data.actions) - actions_before


def identify_source_armature(imported_objects: set[bpy.types.Object]) -> bpy.types.Object:
    armatures = [obj for obj in imported_objects if obj.type == "ARMATURE"]
    if len(armatures) == 1:
        return armatures[0]
    names = ", ".join(sorted(obj.name for obj in armatures)) or "none"
    if not armatures:
        raise ScriptError("Imported Mixamo FBX did not create a source armature.")
    raise ScriptError(
        f"Imported Mixamo FBX created multiple source armatures; expected exactly one: {names}."
    )


def identify_source_action(
    source_armature: bpy.types.Object,
    imported_actions: set[bpy.types.Action],
) -> bpy.types.Action:
    action_candidates = {action for action in imported_actions if action.library is None}
    animation_data = getattr(source_armature, "animation_data", None)
    current_action = getattr(animation_data, "action", None)
    if current_action is not None and current_action.library is None:
        action_candidates.add(current_action)

    if len(action_candidates) == 1:
        return next(iter(action_candidates))

    names = ", ".join(sorted(action.name for action in action_candidates)) or "none"
    if not action_candidates:
        raise ScriptError(
            f'Imported Mixamo source armature "{source_armature.name}" has no local action.'
        )
    raise ScriptError(
        f'Imported Mixamo FBX has ambiguous source actions for armature '
        f'"{source_armature.name}": {names}.'
    )


def clear_target_pose_for_bind(target_armature: bpy.types.Object) -> None:
    """Put the target in rest-pose space before Retarget computes bind offsets."""

    failure_context = "Target generated-rig retarget bind pose preparation"
    ensure_object_mode(target_armature, failure_context)

    animation_data = getattr(target_armature, "animation_data", None)
    if animation_data is not None:
        animation_data.action = None

    pose = getattr(target_armature, "pose", None)
    if pose is None:
        raise ScriptError(
            f'{failure_context} failed because armature "{target_armature.name}" has no pose data.'
        )

    try:
        for pose_bone in pose.bones:
            pose_bone.matrix_basis.identity()
        bpy.context.view_layer.update()
    except Exception as exc:
        raise ScriptError(
            f'{failure_context} failed because Blender could not clear pose transforms on '
            f'armature "{target_armature.name}": {exc}'
        ) from exc


def fcurves_have_keyframes(fcurves) -> bool:
    if fcurves is None:
        return False
    return any(len(getattr(fcurve, "keyframe_points", ())) > 0 for fcurve in fcurves)


def action_has_keyed_fcurves(action: bpy.types.Action) -> bool:
    fcurves = getattr(action, "fcurves", None)
    if fcurves is not None and fcurves_have_keyframes(fcurves):
        return True

    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for channelbag in getattr(strip, "channelbags", ()):
                if fcurves_have_keyframes(getattr(channelbag, "fcurves", None)):
                    return True
    return False


def slugify(value: str) -> str:
    slug = re.sub(r"[^a-z0-9]+", "_", value.lower()).strip("_")
    return slug or "motion"


def baked_action_name(row: MotionManifestRow) -> str:
    return f'mixamo_{row.motion_id.replace("-", "_")}'


def select_target_for_bake(target_armature: bpy.types.Object) -> None:
    ensure_object_mode(target_armature, "Mixamo animation bake")
    try:
        for obj in bpy.context.view_layer.objects:
            obj.select_set(False)
        target_armature.select_set(True)
        bpy.context.view_layer.objects.active = target_armature
    except Exception as exc:
        raise ScriptError(
            f'Mixamo animation bake failed because Blender could not select only target '
            f'armature "{target_armature.name}": {exc}'
        ) from exc


def validate_baked_action(action: bpy.types.Action, source_action_name: str) -> None:
    if action.library is not None:
        raise ScriptError(f'Mixamo animation bake produced linked action "{action.name}".')
    if not action.use_fake_user:
        raise ScriptError(
            f'Mixamo animation bake produced action "{action.name}" without fake user.'
        )
    if not action_has_keyed_fcurves(action):
        raise ScriptError(
            f'Mixamo animation bake produced action "{action.name}" from source '
            f'"{source_action_name}" with no f-curves/keyframes.'
        )


def bake_mixamo_action(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    source_armature: bpy.types.Object,
    source_action: bpy.types.Action,
    row: MotionManifestRow,
) -> bpy.types.Action:
    target_animation_data = target_armature.animation_data_create()
    source_animation_data = source_armature.animation_data_create()
    if target_animation_data is None or source_animation_data is None:
        raise ScriptError("Mixamo animation bake failed because animation data could not be created.")

    target_name = baked_action_name(row)
    if bpy.data.actions.get(target_name) is not None:
        raise ScriptError(
            f'Mixamo animation bake failed because action "{target_name}" already exists.'
        )

    target_action = bpy.data.actions.new(target_name)
    target_action.use_fake_user = True
    target_animation_data.action = target_action
    source_animation_data.action = source_action

    frame_start = math.floor(source_action.frame_range[0])
    frame_end = math.ceil(source_action.frame_range[1])
    scene.frame_set(frame_start)
    select_target_for_bake(target_armature)

    try:
        result = bpy.ops.nla.bake(
            frame_start=frame_start,
            frame_end=frame_end,
            step=1,
            only_selected=False,
            visual_keying=True,
            clear_constraints=False,
            clear_parents=False,
            use_current_action=True,
            clean_curves=False,
            bake_types={"POSE"},
            channel_types={"LOCATION", "ROTATION", "SCALE"},
        )
    except Exception as exc:
        raise ScriptError(
            f'Mixamo animation bake failed because Blender could not bake source action '
            f'"{source_action.name}" to target action "{target_name}": {exc}'
        ) from exc

    if "CANCELLED" in result:
        raise ScriptError(
            f'Mixamo animation bake failed because bpy.ops.nla.bake returned {result} for '
            f'source action "{source_action.name}".'
        )

    if target_animation_data.action != target_action:
        actual_name = getattr(target_animation_data.action, "name", "none")
        raise ScriptError(
            f'Mixamo animation bake failed because Blender did not keep requested action '
            f'"{target_name}" current after baking; current action is "{actual_name}".'
        )

    validate_baked_action(target_action, source_action.name)
    return target_action


def nla_reference_name(action: bpy.types.Action) -> str:
    digest = hashlib.sha1(action.name.encode("utf-8")).hexdigest()[:12]
    slug = slugify(action.name)
    slug_length = max(1, NLA_REFERENCE_NAME_LIMIT - len(NLA_REFERENCE_PREFIX) - len(digest) - 2)
    return f"{NLA_REFERENCE_PREFIX}:{slug[:slug_length]}:{digest}"


def create_persistent_action_user(action: bpy.types.Action, armature: bpy.types.Object) -> None:
    try:
        animation_data = armature.animation_data_create()
        if animation_data is None:
            raise RuntimeError("animation_data_create() returned None")
        reference_name = nla_reference_name(action)
        for existing_track in list(animation_data.nla_tracks):
            if existing_track.name == reference_name:
                animation_data.nla_tracks.remove(existing_track)
        track = animation_data.nla_tracks.new()
        track.name = reference_name
        strip = track.strips.new(reference_name, int(action.frame_range[0]), action)
        strip.name = reference_name
        strip.mute = True
        track.mute = True
        track.lock = True
    except Exception as exc:
        raise ScriptError(
            f'Action persistence failed because Blender could not create a muted/locked NLA '
            f'reference for action "{action.name}" on armature "{armature.name}": {exc}'
        ) from exc

    validate_action_nla_reference(action, armature)


def validate_action_nla_reference(action: bpy.types.Action, armature: bpy.types.Object) -> None:
    animation_data = getattr(armature, "animation_data", None)
    if animation_data is None or action.users <= 0:
        raise ScriptError(
            f'Action persistence failed because action "{action.name}" has no real users on '
            f'armature "{armature.name}".'
        )

    reference_name = nla_reference_name(action)
    matching_tracks = [
        track
        for track in animation_data.nla_tracks
        if track.name == reference_name
        and track.mute
        and track.lock
        and any(
            strip.name == reference_name and strip.mute and strip.action == action
            for strip in track.strips
        )
    ]
    if not matching_tracks:
        raise ScriptError(
            f'Action persistence failed because action "{action.name}" lacks a deterministic '
            f'muted/locked NLA reference on armature "{armature.name}".'
        )


def pose_constraints_targeting(
    target_armature: bpy.types.Object,
    source_armature: bpy.types.Object,
) -> list[tuple[bpy.types.PoseBone, bpy.types.Constraint]]:
    pose = target_armature.pose
    if pose is None:
        return []
    return [
        (pose_bone, constraint)
        for pose_bone in pose.bones
        for constraint in pose_bone.constraints
        if getattr(constraint, "target", None) == source_armature
    ]


def cleanup_source_data(
    target_armature: bpy.types.Object,
    source_armature: bpy.types.Object,
    imported_objects: set[bpy.types.Object],
    source_action: bpy.types.Action,
) -> None:
    for pose_bone, constraint in pose_constraints_targeting(target_armature, source_armature):
        pose_bone.constraints.remove(constraint)

    source_animation_data = getattr(source_armature, "animation_data", None)
    if source_animation_data is not None:
        source_animation_data.action = None

    for obj in list(imported_objects):
        if obj.name in bpy.data.objects and bpy.data.objects[obj.name] == obj:
            bpy.data.objects.remove(obj, do_unlink=True)

    if source_action.name in bpy.data.actions and bpy.data.actions[source_action.name] == source_action:
        bpy.data.actions.remove(source_action, do_unlink=True)

    try:
        bpy.ops.outliner.orphans_purge(do_recursive=True)
    except Exception:
        # Orphan purging is best-effort; explicit temporary objects/actions were already removed.
        pass


def save_output(output_path: Path) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    try:
        result = bpy.ops.wm.save_as_mainfile(filepath=str(output_path), check_existing=False)
    except Exception as exc:
        raise ScriptError(f'Could not save output blend "{output_path}": {exc}') from exc
    if "CANCELLED" in result:
        raise ScriptError(f'Saving output blend "{output_path}" returned {result}.')


def run_blender_python_expr(
    blend_path: Path,
    code: str,
    failure_context: str,
) -> subprocess.CompletedProcess[str]:
    command = [bpy.app.binary_path, "--background", str(blend_path), "--python-expr", code]
    completed = subprocess.run(command, check=False, capture_output=True, text=True)
    if completed.returncode != 0:
        raise ScriptError(
            f"{failure_context} failed in a fresh Blender process:\n"
            f"stdout:\n{completed.stdout}\n"
            f"stderr:\n{completed.stderr}"
        )
    return completed


def postprocess_saved_root_motion_in_fresh_blender(
    output_path: Path,
    action_name: str,
) -> RootMotionMetadata:
    result_path = output_path.with_name(f"{output_path.stem}.root_motion_postprocess.json")
    if result_path.exists():
        result_path.unlink()
    code = "\n".join(
        (
            "import importlib.util, json, math, pathlib, sys, bpy",
            f"script_path = {str(Path(__file__).resolve())!r}",
            "spec = importlib.util.spec_from_file_location('retarget_mixamo_animation_postprocess', script_path)",
            "module = importlib.util.module_from_spec(spec)",
            "sys.modules['retarget_mixamo_animation_postprocess'] = module",
            "spec.loader.exec_module(module)",
            "target_armature = module.identify_target_armature()",
            f"original_action = bpy.data.actions.get({action_name!r})",
            "if original_action is None:",
            "    raise RuntimeError('saved action not found')",
            "animation_data = target_armature.animation_data_create()",
            "animation_data.action = original_action",
            "source = 'root'",
            "created = False",
            "action = original_action",
            "if module.root_location_fcurve_planar_displacement(original_action, target_armature) <= module.ROOT_MOTION_STATIC_PLANAR_EPSILON:",
            "    module.force_persistent_root_motion_fcurves(original_action, target_armature)",
            "    module.create_persistent_action_user(original_action, target_armature)",
            "    source = 'pelvis_planar'",
            "    created = True",
            "module.sanitise_mixamo_baked_root_action(bpy.context.scene, target_armature, original_action)",
            "module.create_persistent_action_user(original_action, target_armature)",
            f"module.save_output(pathlib.Path({str(output_path)!r}))",
            "path_distance = module.sample_root_planar_path_distance(bpy.context.scene, target_armature, action)",
            "displacement = module.root_location_fcurve_planar_displacement(action, target_armature)",
            f"pathlib.Path({str(result_path)!r}).write_text(json.dumps({{'action_name': action.name, 'root_source': source, 'root_created': created, 'root_fcurve_planar_displacement': displacement, 'root_planar_path_distance': path_distance}}, sort_keys=True), encoding='utf-8')",
        )
    )
    completed = run_blender_python_expr(output_path, code, "Root motion persistence post-process")
    if not result_path.is_file():
        raise ScriptError(
            f'Root motion persistence post-process did not create "{result_path}":\n'
            f"stdout:\n{completed.stdout}\n"
            f"stderr:\n{completed.stderr}"
        )
    try:
        with result_path.open("r", encoding="utf-8") as handle:
            result = json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise ScriptError(f'Could not read root motion post-process result "{result_path}": {exc}') from exc
    finally:
        try:
            result_path.unlink()
        except OSError:
            pass

    return RootMotionMetadata(
        source=str(result["root_source"]),
        created=bool(result["root_created"]),
        action_name=str(result["action_name"]),
    )


def validate_saved_root_motion_in_fresh_blender(
    output_path: Path,
    action_name: str,
    require_pelvis_compensation: bool,
) -> dict:
    result_path = output_path.with_name(f"{output_path.stem}.root_motion_validation.json")
    if result_path.exists():
        result_path.unlink()
    code = "\n".join(
        (
            "import importlib.util, json, math, pathlib, sys, bpy",
            f"script_path = {str(Path(__file__).resolve())!r}",
            "spec = importlib.util.spec_from_file_location('retarget_mixamo_animation_validate', script_path)",
            "module = importlib.util.module_from_spec(spec)",
            "sys.modules['retarget_mixamo_animation_validate'] = module",
            "spec.loader.exec_module(module)",
            "target_armature = module.identify_target_armature()",
            "action_names = sorted(action.name for action in bpy.data.actions)",
            f"action = bpy.data.actions.get({action_name!r})",
            "if action is None:",
            "    raise RuntimeError(f'expected final action not found; available actions: {action_names}')",
            "animation_data = target_armature.animation_data_create()",
            "animation_data.action = action",
            "module.create_persistent_action_user(action, target_armature)",
            "module.validate_action_nla_reference(action, target_armature)",
            "sanitised = module.validate_mixamo_sanitised_root_action(bpy.context.scene, target_armature, action, None, 'Root motion fresh validation')",
            "root_fcurves = module.read_location_fcurves(action, target_armature, 'pose.bones[\"Root\"].location')",
            "pelvis_fcurves = module.read_location_fcurves(action, target_armature, 'pose.bones[\"pelvis\"].location')",
            "root_curve_key_counts = [len(fcurve.keyframe_points) for fcurve in root_fcurves]",
            "root_curve_start_end = [[float(fcurve.keyframe_points[0].co.y), float(fcurve.keyframe_points[-1].co.y)] for fcurve in root_fcurves]",
            "root_key_non_static = any(math.hypot(float(root_fcurves[0].keyframe_points[i].co.y), float(root_fcurves[1].keyframe_points[i].co.y)) > module.ROOT_MOTION_STATIC_PLANAR_EPSILON for i in range(len(root_fcurves[0].keyframe_points)))",
            "pelvis_compensation_visible = (abs(float(pelvis_fcurves[0].keyframe_points[-1].co.y) - float(pelvis_fcurves[0].keyframe_points[0].co.y)) <= module.ROOT_MOTION_STATIC_PLANAR_EPSILON and abs(float(pelvis_fcurves[1].keyframe_points[-1].co.y) - float(pelvis_fcurves[1].keyframe_points[0].co.y)) <= module.ROOT_MOTION_STATIC_PLANAR_EPSILON)",
            "frame_start, frame_end = module.action_frame_range(action, 'Root motion fresh validation')",
            "if frame_start > 1 or frame_end < 45:",
            "    raise RuntimeError(f'action frame range {frame_start}-{frame_end} does not cover required frame 1 -> 45 validation')",
            "root = module.require_pose_bone(target_armature, 'Root', 'Root motion fresh validation')",
            "bpy.context.scene.frame_set(1)",
            "bpy.context.view_layer.update()",
            "root_frame_1 = module.pose_bone_world_matrix(target_armature, root).translation.copy()",
            "bpy.context.scene.frame_set(45)",
            "bpy.context.view_layer.update()",
            "root_frame_45 = module.pose_bone_world_matrix(target_armature, root).translation.copy()",
            "world_delta = root_frame_45 - root_frame_1",
            "world_delta_planar = module.horizontal_length(world_delta)",
            "path_distance = module.sample_root_planar_path_distance(bpy.context.scene, target_armature, action)",
            "displacement = module.root_location_fcurve_planar_displacement(action, target_armature)",
            "if animation_data.action != action:",
            "    raise RuntimeError('expected action is not assigned to target armature')",
            "if not root_key_non_static:",
            "    raise RuntimeError('persisted Root X/Y keyed curves are static')",
            "if world_delta_planar <= module.ROOT_MOTION_STATIC_PLANAR_EPSILON:",
            "    raise RuntimeError('evaluated Root world translation is static from frame 1 -> 45')",
            "if path_distance <= module.ROOT_MOTION_STATIC_PLANAR_EPSILON or displacement <= module.ROOT_MOTION_STATIC_PLANAR_EPSILON:",
            "    raise RuntimeError('persisted Root planar displacement/path is static')",
            f"if {require_pelvis_compensation!r} and not pelvis_compensation_visible:",
            "    raise RuntimeError('pelvis planar compensation is not visible in saved curves')",
            f"pathlib.Path({str(result_path)!r}).write_text(json.dumps({{'action_names': action_names, 'action_name': action.name, 'target_armature': target_armature.name, 'assigned_action_name': animation_data.action.name, 'root_curve_key_counts': root_curve_key_counts, 'root_curve_start_end': root_curve_start_end, 'root_rest_canonical': True, 'root_max_z_abs': sanitised['max_root_z_abs'], 'root_max_x_rotation_abs': sanitised['max_root_local_x_rotation_abs'], 'root_key_non_static': root_key_non_static, 'root_fcurve_planar_displacement': displacement, 'root_planar_path_distance': path_distance, 'root_world_frame_1': [root_frame_1.x, root_frame_1.y, root_frame_1.z], 'root_world_frame_45': [root_frame_45.x, root_frame_45.y, root_frame_45.z], 'root_world_frame_1_to_45_planar_delta': world_delta_planar, 'pelvis_compensation_required': {require_pelvis_compensation!r}, 'pelvis_compensation_visible': pelvis_compensation_visible}}, sort_keys=True), encoding='utf-8')",
        )
    )
    completed = run_blender_python_expr(output_path, code, "Root motion fresh validation")
    if not result_path.is_file():
        raise ScriptError(
            f'Root motion fresh validation did not create "{result_path}":\n'
            f"stdout:\n{completed.stdout}\n"
            f"stderr:\n{completed.stderr}"
        )
    try:
        with result_path.open("r", encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError) as exc:
        raise ScriptError(f'Could not read root motion fresh validation result "{result_path}": {exc}') from exc
    finally:
        try:
            result_path.unlink()
        except OSError:
            pass


def vector_to_json(vector: Vector) -> list[float]:
    values = [float(vector.x), float(vector.y), float(vector.z)]
    if not all(math.isfinite(value) for value in values):
        raise ScriptError(f"Motion metrics extraction produced a non-finite vector: {values}.")
    return values


def planar_vector_to_json(vector: Vector) -> list[float]:
    values = [float(vector.x), float(vector.y)]
    if not all(math.isfinite(value) for value in values):
        raise ScriptError(f"Motion metrics extraction produced a non-finite planar vector: {values}.")
    return values


def clamp01(value: float) -> float:
    return min(1.0, max(0.0, float(value)))


def wrap_angle_radians(value: float) -> float:
    wrapped = (float(value) + math.pi) % (math.tau) - math.pi
    if not math.isfinite(wrapped):
        raise ScriptError("Motion metrics extraction produced a non-finite wrapped angle.")
    return wrapped


def unwrap_angle_sequence(values: list[float]) -> list[float]:
    if not values:
        return []
    unwrapped = [float(values[0])]
    for value in values[1:]:
        unwrapped.append(unwrapped[-1] + wrap_angle_radians(float(value) - unwrapped[-1]))
    return unwrapped


def root_local_vector(vector: Vector, root_yaw_radians: float) -> Vector:
    return Matrix.Rotation(-float(root_yaw_radians), 4, "Z") @ vector


def root_relative_position(position: Vector, root_position: Vector, root_yaw_radians: float) -> Vector:
    return root_local_vector(position - root_position, root_yaw_radians)


def contact_confidence(
    height: float,
    speed: float,
    height_threshold: float,
    speed_threshold: float,
) -> float:
    height_margin = max(0.01, abs(height_threshold) * 0.02)
    speed_margin = max(0.01, speed_threshold)
    height_score = clamp01((height_threshold + height_margin - height) / height_margin)
    speed_score = clamp01((speed_threshold + speed_margin - speed) / speed_margin)
    return min(height_score, speed_score)


def derive_head_height_reference(target_armature: bpy.types.Object) -> dict:
    failure_context = "Head-height reference derivation"
    armature_data = getattr(target_armature, "data", None)
    bones = getattr(armature_data, "bones", None)
    if bones is None or "head" not in bones or "pelvis" not in bones:
        raise ScriptError(
            f'{failure_context} failed because armature "{target_armature.name}" lacks rest '
            'bones "head" and "pelvis" required for a stable derived reference.'
        )

    head_rest = (target_armature.matrix_world @ bones["head"].matrix_local).translation
    pelvis_rest = (target_armature.matrix_world @ bones["pelvis"].matrix_local).translation
    standing_height = float(head_rest.z)
    crouch_height = float(pelvis_rest.z + (standing_height - float(pelvis_rest.z)) * 0.65)
    if not all(math.isfinite(value) for value in (standing_height, crouch_height)) or standing_height <= crouch_height:
        crouch_height = standing_height * 0.65
    if standing_height <= crouch_height:
        raise ScriptError(
            f"{failure_context} failed because derived standing/crouch heights are invalid: "
            f"standing={standing_height}, crouch={crouch_height}."
        )
    return {
        "method": "target_rig_rest_estimated_crouch_from_pelvis_head_ratio",
        "source_bone": "head",
        "standing_height": standing_height,
        "crouch_height": crouch_height,
        "crouch_estimate_ratio_between_pelvis_and_head": 0.65,
        "notes": (
            "No authored standing/crouch calibration asset was available to the source pipeline; "
            "standing height uses the target rig rest head height and crouch height is a stable "
            "derived estimate between rest pelvis and head heights. Runtime tuning should replace "
            "this with authored rig calibration when available."
        ),
    }


def require_metrics_bones(target_armature: bpy.types.Object) -> dict[str, bpy.types.PoseBone]:
    pose = getattr(target_armature, "pose", None)
    if pose is None:
        raise ScriptError(
            f'Motion metrics extraction failed because armature "{target_armature.name}" '
            "has no pose data."
        )

    missing = [bone_name for bone_name in METRICS_BONES if bone_name not in pose.bones]
    if missing:
        missing_list = ", ".join(missing)
        available = ", ".join(sorted(pose.bones.keys())) or "none"
        raise ScriptError(
            f'Motion metrics extraction failed because armature "{target_armature.name}" '
            f"is missing required baked target bones: {missing_list}. Available pose bones: "
            f"{available}."
        )

    return {bone_name: pose.bones[bone_name] for bone_name in METRICS_BONES}


def pose_bone_world_matrix(
    armature: bpy.types.Object,
    pose_bone: bpy.types.PoseBone,
):
    return armature.matrix_world @ pose_bone.matrix


def estimate_root_yaw_radians(root_world_matrix) -> float:
    forward = root_world_matrix.to_quaternion() @ Vector((0.0, 1.0, 0.0))
    yaw = math.atan2(float(forward.x), float(forward.y))
    if not math.isfinite(yaw):
        raise ScriptError("Motion metrics extraction produced a non-finite root yaw estimate.")
    return yaw


def velocity_between_positions(
    positions: list[Vector],
    index: int,
    frame_start: int,
    frame_end: int,
    fps: float,
) -> Vector:
    if len(positions) <= 1:
        return Vector((0.0, 0.0, 0.0))
    if index == 0:
        delta_frames = 1
        delta = positions[1] - positions[0]
    elif index == len(positions) - 1:
        delta_frames = 1
        delta = positions[-1] - positions[-2]
    else:
        delta_frames = min(frame_end, frame_start + index + 1) - max(frame_start, frame_start + index - 1)
        delta = positions[index + 1] - positions[index - 1]

    delta_seconds = float(delta_frames) / fps if fps > 0.0 else 0.0
    if delta_seconds <= 0.0:
        return Vector((0.0, 0.0, 0.0))
    return delta / delta_seconds


def horizontal_length(vector: Vector) -> float:
    return math.sqrt(float(vector.x * vector.x + vector.y * vector.y))


def action_frame_range(action: bpy.types.Action, failure_context: str) -> tuple[int, int]:
    frame_start = math.floor(action.frame_range[0])
    frame_end = math.ceil(action.frame_range[1])
    if frame_end < frame_start:
        raise ScriptError(
            f'{failure_context} failed because action "{action.name}" has invalid frame range '
            f"{tuple(action.frame_range)}."
        )
    return frame_start, frame_end


def require_root_motion_bones(
    target_armature: bpy.types.Object,
    failure_context: str,
) -> tuple[bpy.types.PoseBone, bpy.types.PoseBone]:
    pose = getattr(target_armature, "pose", None)
    if pose is None:
        raise ScriptError(
            f'{failure_context} failed because armature "{target_armature.name}" has no pose data.'
        )

    missing = [bone_name for bone_name in ("Root", "pelvis") if bone_name not in pose.bones]
    if missing:
        missing_list = ", ".join(missing)
        available = ", ".join(sorted(pose.bones.keys())) or "none"
        raise ScriptError(
            f'{failure_context} failed because armature "{target_armature.name}" is missing '
            f"required bones: {missing_list}. Available pose bones: {available}."
        )

    root = pose.bones["Root"]
    pelvis = pose.bones["pelvis"]
    if root.parent is not None:
        raise ScriptError(
            f'{failure_context} failed because target bone "Root" is parented to '
            f'"{root.parent.name}". Root motion synthesis currently requires an unparented Root bone.'
        )
    if pelvis.parent != root:
        parent_name = pelvis.parent.name if pelvis.parent is not None else "none"
        raise ScriptError(
            f'{failure_context} failed because target bone "pelvis" is parented to '
            f'"{parent_name}". Root motion synthesis currently requires pelvis to be a direct '
            'child of Root.'
        )

    return root, pelvis


def require_pose_bone(
    target_armature: bpy.types.Object,
    bone_name: str,
    failure_context: str,
) -> bpy.types.PoseBone:
    pose = getattr(target_armature, "pose", None)
    if pose is None:
        raise ScriptError(
            f'{failure_context} failed because armature "{target_armature.name}" has no pose data.'
        )
    if bone_name not in pose.bones:
        available = ", ".join(sorted(pose.bones.keys())) or "none"
        raise ScriptError(
            f'{failure_context} failed because armature "{target_armature.name}" is missing '
            f'required bone "{bone_name}". Available pose bones: {available}.'
        )
    return pose.bones[bone_name]


def sample_root_planar_path_distance(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
) -> float:
    animation_data = target_armature.animation_data_create()
    if animation_data is None:
        raise ScriptError("Root motion inspection failed because animation data could not be created.")
    animation_data.action = action

    root = require_pose_bone(target_armature, "Root", "Root motion inspection")
    frame_start, frame_end = action_frame_range(action, "Root motion inspection")
    previous_position: Vector | None = None
    planar_path_distance = 0.0
    for frame in range(frame_start, frame_end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        position = pose_bone_world_matrix(target_armature, root).translation.copy()
        if previous_position is not None:
            planar_path_distance += horizontal_length(position - previous_position)
        previous_position = position

    return planar_path_distance


def action_fcurve_collection_for_id(
    action: bpy.types.Action,
    animated_id: bpy.types.ID,
):
    animation_data = getattr(animated_id, "animation_data", None)
    action_slot = getattr(animation_data, "action_slot", None)
    action_slot_handle = getattr(action_slot, "handle", None)
    if action_slot_handle is None:
        action_slot_handle = getattr(animation_data, "action_slot_handle", None)

    fallback_channelbag_fcurves = None

    for layer in getattr(action, "layers", ()):
        for strip in getattr(layer, "strips", ()):
            for channelbag in getattr(strip, "channelbags", ()):
                channelbag_fcurves = getattr(channelbag, "fcurves", None)
                if channelbag_fcurves is None:
                    continue
                if fallback_channelbag_fcurves is None:
                    fallback_channelbag_fcurves = channelbag_fcurves
                channelbag_slot = getattr(channelbag, "slot", None)
                channelbag_slot_handle = getattr(channelbag_slot, "handle", None)
                if action_slot_handle is None or channelbag_slot_handle == action_slot_handle:
                    return channelbag_fcurves

    if fallback_channelbag_fcurves is not None:
        return fallback_channelbag_fcurves

    legacy_fcurves = getattr(action, "fcurves", None)
    if legacy_fcurves is not None:
        return legacy_fcurves

    raise ScriptError(
        f'Action channel lookup failed because action "{action.name}" has no editable F-Curve '
        f'collection for animated ID "{animated_id.name}".'
    )


def write_action_location_fcurves(
    action: bpy.types.Action,
    animated_id: bpy.types.ID,
    data_path: str,
    frames: list[int],
    values: list[tuple[float, float, float]],
) -> None:
    if len(frames) != len(values):
        raise ScriptError(
            f'Action channel write failed for "{data_path}" because {len(frames)} frames were '
            f'provided for {len(values)} values.'
        )

    fcurves = action_fcurve_collection_for_id(action, animated_id)
    for index in range(3):
        fcurve = fcurves.find(data_path, index=index)
        if fcurve is None:
            fcurve = fcurves.new(data_path, index=index)
        fcurve.keyframe_points.clear()
        for frame, value in zip(frames, values):
            fcurve.keyframe_points.insert(
                float(frame),
                float(value[index]),
                options={"FAST"},
                keyframe_type="KEYFRAME",
            )
        for keyframe_point in fcurve.keyframe_points:
            keyframe_point.interpolation = "LINEAR"
        fcurve.keyframe_points.sort()
        fcurve.keyframe_points.deduplicate()
        fcurve.keyframe_points.handles_recalc()
        fcurve.update()


def read_location_fcurves(
    action: bpy.types.Action,
    animated_id: bpy.types.ID,
    data_path: str,
) -> list[bpy.types.FCurve]:
    fcurves = action_fcurve_collection_for_id(action, animated_id)
    location_fcurves = [fcurves.find(data_path, index=index) for index in range(3)]
    if any(fcurve is None for fcurve in location_fcurves):
        raise ScriptError(
            f'Action channel read failed because action "{action.name}" does not contain all '
            f'location F-Curves for "{data_path}".'
        )
    return location_fcurves


def resolve_root_vertical_location_axis(
    target_armature: bpy.types.Object,
    failure_context: str,
) -> tuple[int, float]:
    """Resolve which Root local location component maps most strongly to world up."""

    root_bone = target_armature.data.bones.get("Root") if target_armature.data else None
    if root_bone is None:
        raise ScriptError(f'{failure_context} failed because target armature has no "Root" bone.')

    world_up = Vector((0.0, 0.0, 1.0))
    armature_world_basis = target_armature.matrix_world.to_3x3()
    root_rest_basis = root_bone.matrix_local.to_3x3()
    scores: list[tuple[int, float]] = []
    for axis_index in range(3):
        axis = armature_world_basis @ root_rest_basis.col[axis_index]
        if axis.length <= 0.0:
            raise ScriptError(
                f'{failure_context} failed because Root rest basis axis {axis_index} has zero length.'
            )
        scores.append((axis_index, abs(axis.normalized().dot(world_up))))

    scores.sort(key=lambda item: item[1], reverse=True)
    best_axis, best_score = scores[0]
    second_score = scores[1][1]
    if (
        best_score < ROOT_VERTICAL_AXIS_MIN_CONFIDENCE
        or best_score - second_score < ROOT_VERTICAL_AXIS_AMBIGUITY_EPSILON
    ):
        formatted_scores = ", ".join(f"axis[{axis}]={score:.6f}" for axis, score in scores)
        raise ScriptError(
            f"{failure_context} failed because Root vertical location axis is ambiguous or low "
            f"confidence relative to world up: {formatted_scores}."
        )

    return best_axis, best_score


def matrix_angular_difference_radians(left: Matrix, right: Matrix) -> float:
    delta = left.to_quaternion().rotation_difference(right.to_quaternion()).angle
    if not math.isfinite(delta):
        raise ScriptError("Matrix validation produced a non-finite angular difference.")
    return abs(float(delta))


def root_direct_child_pose_bones(
    target_armature: bpy.types.Object,
    failure_context: str,
) -> list[bpy.types.PoseBone]:
    root = require_pose_bone(target_armature, "Root", failure_context)
    children = list(root.children)
    if not children:
        raise ScriptError(f'{failure_context} failed because target Root has no direct child bones.')
    if "pelvis" not in {child.name for child in children}:
        names = ", ".join(child.name for child in children)
        raise ScriptError(f'{failure_context} failed because pelvis is not a direct Root child: {names}.')
    return children


def validate_mixamo_root_rest_canonical(
    target_armature: bpy.types.Object,
    failure_context: str,
) -> None:
    root_bone = target_armature.data.bones.get("Root") if target_armature.data else None
    if root_bone is None:
        raise ScriptError(f'{failure_context} failed because target armature has no "Root" bone.')
    if root_bone.parent is not None:
        raise ScriptError(f'{failure_context} failed because Root rest bone is parented.')
    if root_bone.head_local.length > ROOT_REST_CANONICAL_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because Root rest head is {tuple(root_bone.head_local)}, not origin.'
        )
    direction = root_bone.tail_local - root_bone.head_local
    if direction.length <= ROOT_REST_CANONICAL_EPSILON:
        raise ScriptError(f'{failure_context} failed because Root rest bone has zero length.')
    if (direction.normalized() - Vector((0.0, 1.0, 0.0))).length > ROOT_REST_CANONICAL_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because Root rest tail does not point along canonical +Y: '
            f"{tuple(root_bone.tail_local)}."
        )
    basis = root_bone.matrix_local.to_3x3()
    expected_axes = (Vector((1.0, 0.0, 0.0)), Vector((0.0, 1.0, 0.0)), Vector((0.0, 0.0, 1.0)))
    for axis_index, expected_axis in enumerate(expected_axes):
        axis = basis.col[axis_index]
        if axis.length <= ROOT_REST_CANONICAL_EPSILON:
            raise ScriptError(f'{failure_context} failed because Root rest basis axis {axis_index} is zero.')
        if (axis.normalized() - expected_axis).length > ROOT_REST_CANONICAL_EPSILON:
            raise ScriptError(
                f'{failure_context} failed because Root rest basis axis {axis_index} is '
                f"{tuple(axis.normalized())}, not {tuple(expected_axis)}."
            )


def validate_mixamo_sanitised_root_action(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
    reference_matrices: dict[int, dict[str, Matrix]] | None,
    failure_context: str,
) -> dict:
    animation_data = target_armature.animation_data_create()
    if animation_data is None:
        raise ScriptError(f"{failure_context} failed because animation data could not be created.")
    animation_data.action = action
    validate_mixamo_root_rest_canonical(target_armature, failure_context)

    root = require_pose_bone(target_armature, "Root", failure_context)
    frame_start, frame_end = action_frame_range(action, failure_context)
    max_root_z = 0.0
    max_root_x_rotation = 0.0
    max_translation_drift = 0.0
    max_angular_drift = 0.0
    for frame in range(frame_start, frame_end + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        root_matrix = root.matrix.copy()
        max_root_z = max(max_root_z, abs(float(root_matrix.translation.z)))
        root_x = abs(float(root_matrix.to_euler("XYZ").x))
        max_root_x_rotation = max(max_root_x_rotation, min(root_x, abs(math.tau - root_x)))

        if reference_matrices is not None:
            for bone_name, before_matrix in reference_matrices[frame].items():
                after_matrix = target_armature.pose.bones[bone_name].matrix.copy()
                max_translation_drift = max(
                    max_translation_drift,
                    float((after_matrix.translation - before_matrix.translation).length),
                )
                max_angular_drift = max(
                    max_angular_drift,
                    matrix_angular_difference_radians(after_matrix, before_matrix),
                )

    if max_root_z > ROOT_GROUND_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because sanitised Root vertical drift is {max_root_z:.9g}.'
        )
    if max_root_x_rotation > ROOT_LOCAL_X_ROTATION_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because sanitised Root local X rotation is '
            f"{max_root_x_rotation:.9g} radians."
        )
    if max_translation_drift > MIXAMO_SANITISE_TRANSLATION_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because non-Root translation drift is {max_translation_drift:.9g}.'
        )
    if max_angular_drift > MIXAMO_SANITISE_ANGULAR_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because non-Root angular drift is {max_angular_drift:.9g} radians.'
        )
    return {
        "frame_start": frame_start,
        "frame_end": frame_end,
        "max_root_z_abs": max_root_z,
        "max_root_local_x_rotation_abs": max_root_x_rotation,
        "max_non_root_translation_drift": max_translation_drift,
        "max_non_root_angular_drift": max_angular_drift,
    }


def canonicalise_mixamo_root_edit_bone(target_armature: bpy.types.Object) -> None:
    failure_context = "Mixamo Root sanitisation"
    ensure_object_mode(target_armature, failure_context)
    root_data_bone = target_armature.data.bones.get("Root") if target_armature.data else None
    if root_data_bone is None:
        raise ScriptError(f'{failure_context} failed because target armature has no "Root" bone.')
    child_names = [child.name for child in root_data_bone.children]
    if not child_names or "pelvis" not in child_names:
        raise ScriptError(
            f'{failure_context} failed because Root direct children do not include pelvis: {child_names}.'
        )
    if any(target_armature.data.bones[name].use_connect for name in child_names):
        raise ScriptError(
            f'{failure_context} failed because at least one Root child is connected; refusing to move Root rest only.'
        )
    root_length = max(float(root_data_bone.length), 0.05)

    try:
        bpy.context.view_layer.objects.active = target_armature
        bpy.ops.object.mode_set(mode="EDIT")
        root_edit_bone = target_armature.data.edit_bones["Root"]
        root_edit_bone.parent = None
        root_edit_bone.use_connect = False
        root_edit_bone.head = (0.0, 0.0, 0.0)
        root_edit_bone.tail = (0.0, root_length, 0.0)
        root_edit_bone.roll = 0.0
        for child_name in child_names:
            child_edit_bone = target_armature.data.edit_bones[child_name]
            child_edit_bone.use_connect = False
        bpy.ops.object.mode_set(mode="OBJECT")
        bpy.context.view_layer.update()
    except Exception as exc:
        try:
            bpy.ops.object.mode_set(mode="OBJECT")
        except Exception:
            pass
        raise ScriptError(f"{failure_context} failed while editing Root rest bone: {exc}") from exc


def sanitised_root_matrix_from_sample(sample: Matrix) -> Matrix:
    location, rotation, scale = sample.decompose()
    euler = rotation.to_euler("XYZ")
    sanitised_rotation = Euler((0.0, float(euler.y), float(euler.z)), "XYZ").to_quaternion()
    sanitised_location = Vector((float(location.x), float(location.y), 0.0))
    return Matrix.LocRotScale(sanitised_location, sanitised_rotation, scale)


def yaw_matrix(location: Vector, yaw_radians: float, scale: Vector | None = None) -> Matrix:
    if scale is None:
        scale = Vector((1.0, 1.0, 1.0))
    return Matrix.LocRotScale(
        Vector((float(location.x), float(location.y), 0.0)),
        Euler((0.0, 0.0, float(yaw_radians)), "XYZ").to_quaternion(),
        scale,
    )


def sample_viewpoint_proxy_path(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    frames: list[int],
) -> list[Vector]:
    head = require_pose_bone(target_armature, "head", "Mixamo Root reconstruction viewpoint sampling")
    positions: list[Vector] = []
    for frame in frames:
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        position = pose_bone_world_matrix(target_armature, head).translation.copy()
        positions.append(Vector((float(position.x), float(position.y), 0.0)))
    return positions


def planar_path_distance(positions: list[Vector]) -> float:
    return sum(horizontal_length(current - previous) for previous, current in zip(positions, positions[1:]))


def cumulative_planar_progress(positions: list[Vector]) -> list[float]:
    progress = [0.0]
    for previous, current in zip(positions, positions[1:]):
        progress.append(progress[-1] + horizontal_length(current - previous))
    return progress


def planar_delta_xy_json(positions: list[Vector]) -> list[float] | None:
    if not positions:
        return None
    delta = positions[-1] - positions[0]
    return [float(delta.x), float(delta.y)]


def clean_yaw_sequence(yaws: list[float], max_delta_radians: float = math.radians(12.0)) -> list[float]:
    if not yaws:
        return []
    unwrapped = unwrap_angle_sequence(yaws)
    cleaned = [unwrapped[0]]
    for index, yaw in enumerate(unwrapped[1:], start=1):
        previous = cleaned[-1]
        delta = max(-max_delta_radians, min(max_delta_radians, float(yaw - previous)))
        cleaned.append(previous + delta)
    if len(cleaned) < 3:
        return cleaned
    smoothed = cleaned[:]
    for index in range(1, len(cleaned) - 1):
        smoothed[index] = (cleaned[index - 1] + cleaned[index] * 2.0 + cleaned[index + 1]) / 4.0
    return smoothed


def reconstruct_root_matrices(
    frames: list[int],
    sampled_root_matrices: dict[int, Matrix],
    viewpoint_positions: list[Vector],
    metadata: RootReconstructionInput,
) -> tuple[dict[int, Matrix], dict[str, Any]]:
    classification = classify_root_reconstruction(metadata)
    root_positions = [sampled_root_matrices[frame].translation.copy() for frame in frames]
    root_planar_positions = [Vector((float(position.x), float(position.y), 0.0)) for position in root_positions]
    root_yaws = [estimate_root_yaw_radians(sampled_root_matrices[frame]) for frame in frames]
    cleaned_yaws = clean_yaw_sequence(root_yaws)
    before_yaw_delta = unwrap_angle_sequence(root_yaws)[-1] - unwrap_angle_sequence(root_yaws)[0] if len(root_yaws) > 1 else 0.0
    reconstructed_positions: list[Vector] = []
    reconstructed_yaws: list[float] = []
    suppressed_lateral_deviation = 0.0
    suspicious = []
    straight_direction_source = None
    straight_progress_source = None

    if classification.policy == ROOT_POLICY_MOVING and classification.subtype == ROOT_SUBTYPE_CURVED_MOVING:
        start = viewpoint_positions[0]
        reconstructed_positions = [position - start for position in viewpoint_positions]
        reconstructed_yaws = cleaned_yaws
    elif classification.policy == ROOT_POLICY_MOVING:
        plan = straight_moving_plan_from_metadata(metadata.tags)
        straight_direction_source = plan.direction_source
        # plan.direction is an avatar-semantic metadata decision already resolved
        # by mixamo_root_policy into this Blender action/root frame: planar X/Y,
        # Z-up, Root rest tail +Y.  It is deliberately not a Godot world vector;
        # reference-rig avatar forward maps to Blender -Y here.
        direction = Vector((float(plan.direction[0]), float(plan.direction[1]), 0.0))
        root_delta = root_planar_positions[-1] - root_planar_positions[0]
        viewpoint_delta = viewpoint_positions[-1] - viewpoint_positions[0]
        disagreement = straight_moving_diagnostics_from_metadata(
            metadata.tags,
            (float(root_delta.x), float(root_delta.y)),
            (float(viewpoint_delta.x), float(viewpoint_delta.y)),
            static_epsilon=ROOT_MOTION_STATIC_PLANAR_EPSILON,
        )
        suspicious.extend(disagreement.flags)
        viewpoint_progress = cumulative_planar_progress(viewpoint_positions)
        root_progress = cumulative_planar_progress(root_planar_positions)
        if viewpoint_progress[-1] > ROOT_MOTION_STATIC_PLANAR_EPSILON:
            progress_values = viewpoint_progress
            straight_progress_source = "viewpoint_path_distance"
        else:
            progress_values = root_progress
            straight_progress_source = "raw_root_path_distance"
        viewpoint_start = viewpoint_positions[0]
        for position, progress in zip(viewpoint_positions, progress_values):
            delta = position - viewpoint_start
            projected = direction * float(progress)
            expected_projection = direction * float(delta.dot(direction))
            suppressed_lateral_deviation = max(
                suppressed_lateral_deviation,
                horizontal_length(delta - expected_projection),
            )
            reconstructed_positions.append(projected)
        yaw = wrap_angle_radians(plan.yaw_radians)
        reconstructed_yaws = [yaw for _ in frames]
    elif classification.policy == ROOT_POLICY_TURN_IN_PLACE:
        reconstructed_positions = [Vector((0.0, 0.0, 0.0)) for _ in frames]
        metadata_turn = signed_turn_angle_from_metadata(metadata.tags)
        if metadata_turn is not None and len(frames) > 1:
            reconstructed_yaws = [metadata_turn * (index / float(len(frames) - 1)) for index in range(len(frames))]
        elif metadata_turn is not None:
            reconstructed_yaws = [metadata_turn]
        else:
            reconstructed_yaws = cleaned_yaws
            suspicious.append("turn_in_place_missing_angle_metadata")
    else:
        reconstructed_positions = [Vector((0.0, 0.0, 0.0)) for _ in frames]
        initial_yaw = cleaned_yaws[0] if cleaned_yaws else 0.0
        reconstructed_yaws = [initial_yaw for _ in frames]

    reconstructed: dict[int, Matrix] = {}
    for frame, position, yaw in zip(frames, reconstructed_positions, reconstructed_yaws):
        source_matrix = sampled_root_matrices[frame]
        _, _, scale = source_matrix.decompose()
        reconstructed[frame] = yaw_matrix(position, yaw, scale)

    after_yaw_delta = reconstructed_yaws[-1] - reconstructed_yaws[0] if len(reconstructed_yaws) > 1 else 0.0
    diagnostics = {
        "root_reconstruction_policy": classification.policy,
        "root_reconstruction_subtype": classification.subtype,
        "root_path_distance_before": planar_path_distance(root_planar_positions),
        "root_path_distance_after": planar_path_distance(reconstructed_positions),
        "viewpoint_proxy": "target_head_projected_to_ground",
        "viewpoint_path_distance": planar_path_distance(viewpoint_positions),
        "straight_suppressed_lateral_deviation": suppressed_lateral_deviation,
        "straight_direction_source": straight_direction_source,
        "straight_reconstructed_delta_blender_root_xy": planar_delta_xy_json(reconstructed_positions),
        "raw_root_delta_blender_root_xy": planar_delta_xy_json(root_planar_positions),
        "viewpoint_delta_blender_root_xy": planar_delta_xy_json(viewpoint_positions),
        "straight_progress_source": straight_progress_source,
        "root_yaw_delta_before": before_yaw_delta,
        "root_yaw_delta_after": after_yaw_delta,
        "suspicious_metadata_root_disagreement_flags": suspicious,
    }
    return reconstructed, diagnostics


def sanitise_mixamo_baked_root_action(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
    reconstruction_input: RootReconstructionInput | None = None,
) -> dict:
    """Canonicalise Mixamo Root rest/animation and compensate children.

    This is intentionally Mixamo-specific post-bake cleanup. Mixamo downloads can leave the
    target Root with a contaminated rest orientation and noisy vertical/X-tilt tracks. Before
    touching the Root rest/pose, sample every non-Root pose matrix in armature space, then key
    direct Root children (including pelvis) back to those matrices after the Root is made
    canonical. Descendants keep their original local animation because their direct Root child
    parent has been matrix-compensated.
    """

    failure_context = "Mixamo Root sanitisation"
    animation_data = target_armature.animation_data_create()
    if animation_data is None:
        raise ScriptError(f"{failure_context} failed because animation data could not be created.")
    animation_data.action = action
    ensure_object_mode(target_armature, failure_context)
    root = require_pose_bone(target_armature, "Root", failure_context)
    direct_children = root_direct_child_pose_bones(target_armature, failure_context)
    frame_start, frame_end = action_frame_range(action, failure_context)
    frames = list(range(frame_start, frame_end + 1))

    reference_matrices: dict[int, dict[str, Matrix]] = {}
    sampled_root_matrices: dict[int, Matrix] = {}
    viewpoint_positions = sample_viewpoint_proxy_path(scene, target_armature, frames)
    non_root_names = [pose_bone.name for pose_bone in target_armature.pose.bones if pose_bone.name != "Root"]
    for frame in frames:
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        sampled_root_matrices[frame] = root.matrix.copy()
        reference_matrices[frame] = {
            bone_name: target_armature.pose.bones[bone_name].matrix.copy() for bone_name in non_root_names
        }

    reconstruction_diagnostics: dict[str, Any] = {
        "root_reconstruction_policy": "SanitiseOnly",
        "root_reconstruction_subtype": None,
        "root_path_distance_before": planar_path_distance(
            [Vector((float(sampled_root_matrices[frame].translation.x), float(sampled_root_matrices[frame].translation.y), 0.0)) for frame in frames]
        ),
        "root_path_distance_after": None,
        "viewpoint_proxy": "target_head_projected_to_ground",
        "viewpoint_path_distance": planar_path_distance(viewpoint_positions),
        "straight_suppressed_lateral_deviation": 0.0,
        "straight_direction_source": None,
        "straight_progress_source": None,
        "root_yaw_delta_before": 0.0,
        "root_yaw_delta_after": 0.0,
        "suspicious_metadata_root_disagreement_flags": [],
    }
    root_matrices_to_write = sampled_root_matrices
    if reconstruction_input is not None:
        root_matrices_to_write, reconstruction_diagnostics = reconstruct_root_matrices(
            frames,
            sampled_root_matrices,
            viewpoint_positions,
            reconstruction_input,
        )

    canonicalise_mixamo_root_edit_bone(target_armature)
    root = require_pose_bone(target_armature, "Root", failure_context)
    direct_children = root_direct_child_pose_bones(target_armature, failure_context)
    for pose_bone in [root, *direct_children]:
        pose_bone.rotation_mode = "XYZ"

    for frame in frames:
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        root.matrix = sanitised_root_matrix_from_sample(root_matrices_to_write[frame])
        bpy.context.view_layer.update()
        for child in direct_children:
            child.matrix = reference_matrices[frame][child.name]
        bpy.context.view_layer.update()
        for pose_bone in [root, *direct_children]:
            pose_bone.keyframe_insert(data_path="location", frame=frame)
            pose_bone.keyframe_insert(data_path="rotation_euler", frame=frame)
            pose_bone.keyframe_insert(data_path="scale", frame=frame)

    for fcurve in action_fcurve_collection_for_id(action, target_armature):
        for keyframe_point in fcurve.keyframe_points:
            keyframe_point.interpolation = "LINEAR"
        fcurve.update()

    validation = validate_mixamo_sanitised_root_action(
        scene,
        target_armature,
        action,
        reference_matrices,
        failure_context,
    )
    validation.update(reconstruction_diagnostics)
    if validation.get("root_path_distance_after") is None:
        validation["root_path_distance_after"] = sample_root_planar_path_distance(scene, target_armature, action)
    return validation


def force_persistent_root_motion_fcurves(
    action: bpy.types.Action,
    target_armature: bpy.types.Object,
) -> None:
    root_fcurves = read_location_fcurves(action, target_armature, ROOT_LOCATION_DATA_PATH)
    pelvis_fcurves = read_location_fcurves(action, target_armature, 'pose.bones["pelvis"].location')
    root_vertical_axis, _ = resolve_root_vertical_location_axis(target_armature, "Root motion synthesis")
    if not root_fcurves[root_vertical_axis].keyframe_points:
        raise ScriptError(
            f'Root motion synthesis failed because action "{action.name}" has no Root vertical '
            f"location keys on axis {root_vertical_axis}."
        )
    root_vertical_baseline = float(root_fcurves[root_vertical_axis].keyframe_points[0].co.y)
    key_count = len(pelvis_fcurves[0].keyframe_points)
    if key_count == 0 or any(len(fcurve.keyframe_points) != key_count for fcurve in pelvis_fcurves):
        raise ScriptError(
            f'Root motion synthesis failed because pelvis location F-Curves on action '
            f'"{action.name}" do not have consistent keyed samples.'
        )

    first_pelvis_x = float(pelvis_fcurves[0].keyframe_points[0].co.y)
    first_pelvis_y = float(pelvis_fcurves[1].keyframe_points[0].co.y)
    root_values: list[tuple[float, float, float]] = []
    pelvis_values: list[tuple[float, float, float]] = []
    for index in range(key_count):
        pelvis_x = float(pelvis_fcurves[0].keyframe_points[index].co.y)
        pelvis_y = float(pelvis_fcurves[1].keyframe_points[index].co.y)
        pelvis_z = float(pelvis_fcurves[2].keyframe_points[index].co.y)
        root_x = pelvis_x - first_pelvis_x
        root_y = pelvis_y - first_pelvis_y
        root_value = [root_x, root_y, 0.0]
        root_value[root_vertical_axis] = root_vertical_baseline
        root_values.append((root_value[0], root_value[1], root_value[2]))
        pelvis_values.append((first_pelvis_x, first_pelvis_y, pelvis_z))

    if not any(horizontal_length(Vector((value[0], value[1], 0.0))) > ROOT_MOTION_STATIC_PLANAR_EPSILON for value in root_values):
        raise ScriptError(
            f'Root motion synthesis failed because action "{action.name}" pelvis location '
            "F-Curves contain no planar displacement to transfer to Root."
        )

    frames = [int(round(keyframe.co.x)) for keyframe in pelvis_fcurves[0].keyframe_points]
    write_action_location_fcurves(
        action,
        target_armature,
        'pose.bones["pelvis"].location',
        frames,
        pelvis_values,
    )
    write_action_location_fcurves(
        action,
        target_armature,
        ROOT_LOCATION_DATA_PATH,
        frames,
        root_values,
    )
    bpy.context.view_layer.update()


def root_location_fcurve_planar_displacement(
    action: bpy.types.Action,
    target_armature: bpy.types.Object,
) -> float:
    root_fcurves = read_location_fcurves(action, target_armature, ROOT_LOCATION_DATA_PATH)
    if any(len(fcurve.keyframe_points) == 0 for fcurve in root_fcurves[:2]):
        return 0.0

    x_start = float(root_fcurves[0].keyframe_points[0].co.y)
    x_end = float(root_fcurves[0].keyframe_points[-1].co.y)
    y_start = float(root_fcurves[1].keyframe_points[0].co.y)
    y_end = float(root_fcurves[1].keyframe_points[-1].co.y)
    return math.hypot(x_end - x_start, y_end - y_start)


def root_motion_metadata_for_baked_action(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
    create_root_motion: bool,
) -> RootMotionMetadata:
    root_planar_path_distance = sample_root_planar_path_distance(scene, target_armature, action)
    if root_planar_path_distance > ROOT_MOTION_STATIC_PLANAR_EPSILON:
        return RootMotionMetadata(source="root", created=False)
    if not create_root_motion:
        return RootMotionMetadata(source="root_static", created=False)

    synthesise_root_motion_from_pelvis_planar_displacement(scene, target_armature, action)
    return RootMotionMetadata(
        source="pelvis_planar",
        created=True,
    )


def synthesise_root_motion_from_pelvis_planar_displacement(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
) -> None:
    failure_context = "Root motion synthesis"
    animation_data = target_armature.animation_data_create()
    if animation_data is None:
        raise ScriptError(f"{failure_context} failed because animation data could not be created.")
    animation_data.action = action

    require_root_motion_bones(target_armature, failure_context)
    frame_start, _ = action_frame_range(action, failure_context)
    force_persistent_root_motion_fcurves(action, target_armature)
    animation_data.action = action
    scene.frame_set(frame_start)
    bpy.context.view_layer.update()

    root_planar_path_distance = sample_root_planar_path_distance(scene, target_armature, action)
    if root_planar_path_distance <= ROOT_MOTION_STATIC_PLANAR_EPSILON:
        raise ScriptError(
            f'{failure_context} failed because synthesising action "{action.name}" left Root '
            "effectively static."
        )


def extract_motion_metrics(
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
    row: MotionManifestRow,
    root_motion_metadata: RootMotionMetadata,
) -> dict:
    animation_data = target_armature.animation_data_create()
    if animation_data is None:
        raise ScriptError("Motion metrics extraction failed because animation data could not be created.")
    animation_data.action = action
    if root_motion_metadata.source == "reconstructed_root":
        validate_mixamo_sanitised_root_action(
            scene,
            target_armature,
            action,
            None,
            "Motion metrics extraction",
        )

    frame_start, frame_end = action_frame_range(action, "Motion metrics extraction")
    frames = list(range(frame_start, frame_end + 1))
    fps = float(scene.render.fps) / float(scene.render.fps_base or 1.0)
    if fps <= 0.0 or not math.isfinite(fps):
        raise ScriptError(f"Motion metrics extraction failed because scene FPS is invalid: {fps}.")

    bones = require_metrics_bones(target_armature)
    head_height_reference = derive_head_height_reference(target_armature)
    raw_samples = []
    root_positions: list[Vector] = []
    root_yaws: list[float] = []
    foot_positions: dict[str, list[Vector]] = {name: [] for name in ("foot_l", "foot_r", "ball_l", "ball_r")}

    for frame in frames:
        scene.frame_set(frame)
        bpy.context.view_layer.update()

        matrices = {name: pose_bone_world_matrix(target_armature, bone) for name, bone in bones.items()}
        positions = {name: matrices[name].translation.copy() for name in METRICS_BONES}
        root_yaw_radians = estimate_root_yaw_radians(matrices["Root"])
        root_positions.append(positions["Root"])
        root_yaws.append(root_yaw_radians)
        for foot_bone_name in foot_positions:
            foot_positions[foot_bone_name].append(positions[foot_bone_name])

        raw_samples.append(
            {
                "frame": frame,
                "time_seconds": float(frame - frame_start) / fps,
                "root_position": positions["Root"],
                "root_yaw_radians": root_yaw_radians,
                "pelvis_position": positions["pelvis"],
                "head_position": positions["head"],
                "tracked_joint_positions": positions,
                "foot_positions": {
                    "foot_l": positions["foot_l"],
                    "foot_r": positions["foot_r"],
                    "ball_l": positions["ball_l"],
                    "ball_r": positions["ball_r"],
                },
                "hand_positions": {
                    "hand_l": positions["hand_l"],
                    "hand_r": positions["hand_r"],
                },
            }
        )

    head_heights = [sample["head_position"].z for sample in raw_samples]
    head_height_min = float(min(head_heights))
    head_height_max = float(max(head_heights))
    head_height_range = head_height_max - head_height_min

    velocities = [velocity_between_positions(root_positions, i, frame_start, frame_end, fps) for i in range(len(frames))]
    unwrapped_root_yaws = unwrap_angle_sequence(root_yaws)
    root_yaw_deltas: list[float] = []
    root_angular_velocities: list[float] = []
    for index, yaw in enumerate(unwrapped_root_yaws):
        if index == 0:
            delta = 0.0
        else:
            delta = float(yaw - unwrapped_root_yaws[index - 1])
        root_yaw_deltas.append(delta)
        root_angular_velocities.append(delta * fps)
    foot_speeds = {
        name: [horizontal_length(velocity_between_positions(values, i, frame_start, frame_end, fps)) for i in range(len(frames))]
        for name, values in foot_positions.items()
    }
    foot_height_thresholds = {}
    foot_speed_thresholds = {}
    for side, foot_name, ball_name in (("left", "foot_l", "ball_l"), ("right", "foot_r", "ball_r")):
        heights = [position.z for position in foot_positions[foot_name] + foot_positions[ball_name]]
        min_height = float(min(heights))
        height_range = float(max(heights) - min_height)
        foot_height_thresholds[side] = min_height + max(0.03, height_range * 0.08)
        combined_speeds = foot_speeds[foot_name] + foot_speeds[ball_name]
        sorted_speeds = sorted(combined_speeds)
        lower_quartile = sorted_speeds[max(0, len(sorted_speeds) // 4 - 1)] if sorted_speeds else 0.0
        foot_speed_thresholds[side] = max(0.05, float(lower_quartile) * 1.5)

    root_path_distance = 0.0
    root_planar_path_distance = 0.0
    for previous, current in zip(root_positions, root_positions[1:]):
        delta = current - previous
        root_path_distance += float(delta.length)
        root_planar_path_distance += horizontal_length(delta)
    duration_seconds = float(frames[-1] - frames[0]) / fps if len(frames) > 1 else 0.0
    root_displacement = root_positions[-1] - root_positions[0]

    samples = []
    for index, sample in enumerate(raw_samples):
        clip_normalised_head_height = 0.0
        if head_height_range > 1e-8:
            clip_normalised_head_height = float((sample["head_position"].z - head_height_min) / head_height_range)

        reference_height_range = head_height_reference["standing_height"] - head_height_reference["crouch_height"]
        reference_normalised_head_height = float(
            (sample["head_position"].z - head_height_reference["crouch_height"]) / reference_height_range
        )

        left_height = min(sample["foot_positions"]["foot_l"].z, sample["foot_positions"]["ball_l"].z)
        right_height = min(sample["foot_positions"]["foot_r"].z, sample["foot_positions"]["ball_r"].z)
        left_speed = min(foot_speeds["foot_l"][index], foot_speeds["ball_l"][index])
        right_speed = min(foot_speeds["foot_r"][index], foot_speeds["ball_r"][index])
        left_low = left_height <= foot_height_thresholds["left"]
        right_low = right_height <= foot_height_thresholds["right"]
        left_slow = left_speed <= foot_speed_thresholds["left"]
        right_slow = right_speed <= foot_speed_thresholds["right"]
        left_contact = bool(left_low and left_slow)
        right_contact = bool(right_low and right_slow)
        left_confidence = contact_confidence(left_height, left_speed, foot_height_thresholds["left"], foot_speed_thresholds["left"])
        right_confidence = contact_confidence(right_height, right_speed, foot_height_thresholds["right"], foot_speed_thresholds["right"])

        root_yaw = sample["root_yaw_radians"]
        root_local_velocity = root_local_vector(velocities[index], root_yaw)
        root_local_planar_velocity = Vector((root_local_velocity.x, root_local_velocity.y, 0.0))
        root_relative_joint_positions = {
            name: root_relative_position(position, sample["root_position"], root_yaw)
            for name, position in sample["tracked_joint_positions"].items()
        }
        sample_motion_feature_joints = {}
        previous_index = max(0, index - 1)
        previous_sample = raw_samples[previous_index]
        previous_root_yaw = previous_sample["root_yaw_radians"]
        previous_root_relative_joint_positions = {
            name: root_relative_position(position, previous_sample["root_position"], previous_root_yaw)
            for name, position in previous_sample["tracked_joint_positions"].items()
        }
        for feature_name, bone_name in METRICS_SAMPLE_FEATURE_BONES:
            position = root_relative_joint_positions[bone_name]
            previous_position = previous_root_relative_joint_positions[bone_name]
            delta = position - previous_position
            sample_motion_feature_joints[feature_name] = {
                "source_bone": bone_name,
                "root_relative_position": vector_to_json(position),
                "root_relative_position_delta": vector_to_json(delta),
                "root_relative_velocity": vector_to_json(delta * fps),
            }
        future_trajectory = []
        for offset_seconds in METRICS_FUTURE_TRAJECTORY_SECONDS:
            future_index = min(len(raw_samples) - 1, index + max(1, int(round(offset_seconds * fps))))
            future_sample = raw_samples[future_index]
            displacement = future_sample["root_position"] - sample["root_position"]
            local_displacement = root_local_vector(displacement, root_yaw)
            future_trajectory.append(
                {
                    "offset_seconds": float(offset_seconds),
                    "offset_frames": int(frames[future_index] - sample["frame"]),
                    "root_local_planar_displacement": planar_vector_to_json(local_displacement),
                    "root_yaw_delta_radians": wrap_angle_radians(
                        unwrapped_root_yaws[future_index] - unwrapped_root_yaws[index]
                    ),
                }
            )

        samples.append(
            {
                "frame": sample["frame"],
                "time_seconds": sample["time_seconds"],
                "root_position": vector_to_json(sample["root_position"]),
                "root_yaw_radians": sample["root_yaw_radians"],
                "pelvis_position": vector_to_json(sample["pelvis_position"]),
                "head_position": vector_to_json(sample["head_position"]),
                "head_height_norm": reference_normalised_head_height,
                "clip_head_height_norm": clip_normalised_head_height,
                "root_relative_joint_positions": {name: vector_to_json(position) for name, position in root_relative_joint_positions.items()},
                "sample_motion_features": {
                    "schema_version": 1,
                    "coordinate_space": METRICS_COORDINATE_SPACE,
                    "delta_window_seconds": 1.0 / fps,
                    "joints": sample_motion_feature_joints,
                },
                "foot_positions": {
                    name: vector_to_json(position)
                    for name, position in sample["foot_positions"].items()
                },
                "hand_positions": {
                    name: vector_to_json(position)
                    for name, position in sample["hand_positions"].items()
                },
                "root_velocity": vector_to_json(velocities[index]),
                "root_local_planar_velocity": planar_vector_to_json(root_local_planar_velocity),
                "root_yaw_delta_radians": root_yaw_deltas[index],
                "root_angular_velocity_radians_per_second": root_angular_velocities[index],
                "future_trajectory": future_trajectory,
                "foot_contact_l": left_contact,
                "foot_contact_r": right_contact,
                "foot_contact": {
                    "left": {
                        "contact": left_contact,
                        "confidence": left_confidence,
                        "phase": "stance" if left_contact else "swing",
                    },
                    "right": {
                        "contact": right_contact,
                        "confidence": right_confidence,
                        "phase": "stance" if right_contact else "swing",
                    },
                },
            }
        )

    metrics = {
        "schema_version": METRICS_SCHEMA_VERSION,
        "manifest": {
            "motion_id": row.motion_id,
            "name": row.name,
            "description": row.description,
            "type": row.motion_type,
        },
        "action": action.name,
        "root_source": root_motion_metadata.source,
        "root_created": root_motion_metadata.created,
        "frame_range": {"start": frame_start, "end": frame_end},
        "fps": fps,
        "sample_count": len(samples),
        "tracked_bones": list(METRICS_BONES),
        "bone_names": list(METRICS_BONES),
        "coordinate_space": METRICS_COORDINATE_SPACE,
        "head_height_norm": {
            **head_height_reference,
        },
        "clip_head_height_norm": {
            "method": "clip_minmax",
            "source_bone": "head",
            "min_height": head_height_min,
            "max_height": head_height_max,
        },
        "foot_contact": {
            "method": "low_slow",
            "confidence_method": "low_slow_threshold_distance_confidence",
            "limitations": (
                "Contact and phase are inferred from per-clip low/slow foot and ball thresholds; "
                "they are suitable for first-pass filtering but are not authored contact events."
            ),
            "height_threshold": foot_height_thresholds,
            "speed_threshold": foot_speed_thresholds,
        },
        "feature_schema": {
            "future_trajectory_offsets_seconds": list(METRICS_FUTURE_TRAJECTORY_SECONDS),
            "root_relative_joint_positions": "Root-local yaw-only frame in Blender Z-up coordinates.",
            "sample_motion_features": {
                "schema_version": 1,
                "coordinate_space": METRICS_COORDINATE_SPACE,
                "joints": [feature_name for feature_name, _bone_name in METRICS_SAMPLE_FEATURE_BONES],
                "fields": ["root_relative_position", "root_relative_position_delta", "root_relative_velocity"],
                "delta_window_seconds": "Previous generated sample at the clip FPS; first sample uses zero delta.",
            },
            "root_local_planar_velocity": "Root-local X/Y velocity in Blender units per second.",
            "root_angular_velocity": "Yaw delta per frame multiplied by scene FPS.",
        },
        "clip": {
            "duration_seconds": duration_seconds,
            "root_displacement": vector_to_json(root_displacement),
            "root_planar_displacement": horizontal_length(root_displacement),
            "root_path_distance": root_path_distance,
            "root_planar_path_distance": root_planar_path_distance,
            "avg_root_speed": root_path_distance / duration_seconds if duration_seconds > 0.0 else 0.0,
            "avg_root_planar_speed": root_planar_path_distance / duration_seconds if duration_seconds > 0.0 else 0.0,
        },
        "samples": samples,
    }
    if root_motion_metadata.diagnostics is not None:
        metrics["root_reconstruction"] = root_motion_metadata.diagnostics
    return metrics


def write_motion_metrics(
    metrics_output: Path,
    scene: bpy.types.Scene,
    target_armature: bpy.types.Object,
    action: bpy.types.Action,
    row: MotionManifestRow,
    root_motion_metadata: RootMotionMetadata,
) -> None:
    metrics = extract_motion_metrics(scene, target_armature, action, row, root_motion_metadata)
    metrics_output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with metrics_output.open("w", encoding="utf-8") as handle:
            json.dump(metrics, handle, ensure_ascii=False, indent=2, sort_keys=True)
            handle.write("\n")
    except OSError as exc:
        raise ScriptError(f'Could not write motion metrics JSON "{metrics_output}": {exc}') from exc


def run(args: RetargetArgs) -> bpy.types.Action:
    rows = load_manifest(args.manifest)
    row = select_manifest_row(rows, args)
    fbx_path = row.fbx_path(args.mixamo_root)

    if not args.target_rig.is_file():
        raise ScriptError(f'Target rig blend was not found at "{args.target_rig}".')

    bpy.ops.wm.open_mainfile(filepath=str(args.target_rig))
    scene = bpy.context.scene
    target_armature = identify_target_armature()
    clear_target_pose_for_bind(target_armature)

    imported_objects, imported_actions = import_fbx(fbx_path)
    source_armature = identify_source_armature(imported_objects)
    source_action = identify_source_action(source_armature, imported_actions)

    apply_retarget_preset(
        target_armature,
        GENERATED_RIG_RETARGET_PRESET_NAME,
        "Target generated-rig retarget preset application",
    )
    apply_retarget_preset(
        source_armature,
        MIXAMO_RETARGET_PRESET_NAME,
        "Mixamo source retarget preset application",
    )

    if args.prepare_inspection:
        if args.metrics_output is not None:
            raise ScriptError(
                "Motion metrics extraction requires a baked target action and cannot be combined "
                "with --prepare-inspection. Run without --prepare-inspection to write metrics."
            )
        prepare_armatures_for_retarget_panel(scene, target_armature, source_armature)
        save_output(args.output)

        print(
            "Prepared Mixamo retarget inspection blend successfully: "
            f'motion_id={row.motion_id}, name="{row.name}", '
            f'fbx="{fbx_path}", target="{target_armature.name}", '
            f'source="{source_armature.name}", source_action="{source_action.name}", '
            f'output="{args.output}"'
        )
        return source_action

    bind_target_to_source(scene, target_armature, source_armature)

    baked_action = bake_mixamo_action(scene, target_armature, source_armature, source_action, row)
    target_armature_name = target_armature.name
    baked_action_name_value = baked_action.name
    source_action_name = source_action.name
    validate_baked_action(baked_action, source_action_name)

    cleanup_source_data(target_armature, source_armature, imported_objects, source_action)
    if bpy.data.actions.get(baked_action.name) != baked_action:
        raise ScriptError(f'Baked action "{baked_action.name}" was removed during cleanup.')
    validate_baked_action(baked_action, source_action_name)

    root_motion_metadata: RootMotionMetadata | None = None
    reconstruction_input = RootReconstructionInput(
        category=args.selection_category,
        motion_class=args.selection_motion_class,
        tags=args.selection_tags,
    )
    if args.create_root_motion:
        root_motion_metadata = root_motion_metadata_for_baked_action(
            scene,
            target_armature,
            baked_action,
            True,
        )
    if args.skip_root_reconstruction:
        sanitise_validation = {
            "max_root_z_abs": 0.0,
            "max_root_local_x_rotation_abs": 0.0,
            "max_non_root_translation_drift": 0.0,
            "max_non_root_angular_drift": 0.0,
            "root_reconstruction_policy": "Skipped",
            "root_reconstruction_subtype": None,
            "suspicious_metadata_root_disagreement_flags": [],
        }
        if root_motion_metadata is None:
            root_motion_metadata = RootMotionMetadata(source="raw_root", created=False)
    else:
        sanitise_validation = sanitise_mixamo_baked_root_action(
            scene,
            target_armature,
            baked_action,
            reconstruction_input,
        )
        root_motion_metadata = RootMotionMetadata(
            source="reconstructed_root",
            created=True,
            diagnostics=sanitise_validation,
        )
    create_persistent_action_user(baked_action, target_armature)
    save_output(args.output)

    final_action_name = baked_action_name_value
    if args.create_root_motion and not args.skip_root_reconstruction:
        if root_motion_metadata is None:
            raise ScriptError("Root motion metadata was not prepared before fresh validation.")
        validation = validate_saved_root_motion_in_fresh_blender(
            args.output,
            final_action_name,
            root_motion_metadata.created,
        )
        print(
            "Fresh root motion persistence validation succeeded: "
            f'action="{validation["action_name"]}", '
            f'assigned_action="{validation["assigned_action_name"]}", '
            f'target="{validation["target_armature"]}", '
            f'available_actions={validation["action_names"]}, '
            f'root_curve_key_counts={validation["root_curve_key_counts"]}, '
            f'root_curve_start_end={validation["root_curve_start_end"]}, '
            f'root_rest_canonical={validation["root_rest_canonical"]}, '
            f'root_max_z_abs={validation["root_max_z_abs"]:.9f}, '
            f'root_max_x_rotation_abs={validation["root_max_x_rotation_abs"]:.9f}, '
            f'root_fcurve_planar_displacement={validation["root_fcurve_planar_displacement"]:.6f}, '
            f'root_planar_path_distance={validation["root_planar_path_distance"]:.6f}, '
            f'root_world_frame_1_to_45_planar_delta='
            f'{validation["root_world_frame_1_to_45_planar_delta"]:.6f}, '
            f'pelvis_compensation_visible={validation["pelvis_compensation_visible"]}'
        )

    if args.metrics_output is not None:
        try:
            bpy.ops.wm.open_mainfile(filepath=str(args.output))
        except Exception as exc:
            raise ScriptError(
                f'Motion metrics extraction could not reopen final blend "{args.output}": {exc}'
            ) from exc
        scene = bpy.context.scene
        target_armature = identify_target_armature()
        final_action = bpy.data.actions.get(final_action_name)
        if final_action is None:
            raise ScriptError(
                f'Motion metrics extraction failed because final action "{final_action_name}" '
                f'was not found after reopening "{args.output}".'
            )
        target_armature.animation_data_create().action = final_action
        if not args.skip_root_reconstruction:
            validate_mixamo_sanitised_root_action(
                scene,
                target_armature,
                final_action,
                None,
                "Final saved action metrics preparation",
            )
        if root_motion_metadata is None:
            if args.skip_root_reconstruction:
                root_motion_metadata = RootMotionMetadata(source="raw_root", created=False)
            else:
                root_motion_metadata = RootMotionMetadata(
                    source="reconstructed_root",
                    created=True,
                    diagnostics=sanitise_validation,
                )
        else:
            root_motion_metadata = RootMotionMetadata(
                source=root_motion_metadata.source,
                created=root_motion_metadata.created,
                action_name=final_action_name,
                diagnostics=root_motion_metadata.diagnostics,
            )
        write_motion_metrics(
            args.metrics_output,
            scene,
            target_armature,
            final_action,
            row,
            root_motion_metadata,
        )

    print(
        "Retargeted Mixamo animation successfully: "
        f'motion_id={row.motion_id}, name="{row.name}", '
        f'fbx="{fbx_path}", target="{target_armature_name}", '
        f'action="{final_action_name}", output="{args.output}", '
        f'root_max_z_abs={sanitise_validation["max_root_z_abs"]:.9f}, '
        f'root_max_x_rotation_abs={sanitise_validation["max_root_local_x_rotation_abs"]:.9f}, '
        f'non_root_translation_drift={sanitise_validation["max_non_root_translation_drift"]:.9f}, '
        f'non_root_angular_drift={sanitise_validation["max_non_root_angular_drift"]:.9f}'
    )
    return bpy.data.actions.get(final_action_name) or baked_action


def main() -> int:
    try:
        args = parse_args(sys.argv)
        run(args)
        return 0
    except ScriptError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
