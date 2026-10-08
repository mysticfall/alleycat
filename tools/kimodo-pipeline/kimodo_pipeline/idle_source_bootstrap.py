"""Blender CLI entry point staging a per-character animated idle source.

The command (KIMODO-IDLE-002) stages a byte-verified copy of the character's
calibrated retarget setup — Gate 1 baseline-checked through
``character_baselines`` — appends ONLY the donor's animated Kimodo-space idle
action, reopens the staged blend to prove its rig content still matches the
approved setup calibration and that no inert staging rigs rode along, and
publishes it transactionally to ``derive_idle_source_path``:

    blender --background --factory-startup \
        --python tools/kimodo-pipeline/commands/idle_source_bootstrap.py -- \
        --character-root game/assets/characters/reference/female --variant sad \
        --donor-blend <donor.blend> --donor-action <action>

The published idle source is a protected character-authored input: the command
refuses to overwrite an existing one and never writes to any bootstrap input or
the donor blend.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import sys
from pathlib import Path
from typing import Any, Mapping

import bpy

from .blender_retargeting import (
    SOURCE_ARMATURE_NAME,
    ProcessorError,
    capture_setup_parity_records,
)
from .bootstrap_manifest import _action_fcurves
from .bootstrap_paths import (
    BootstrapPathError,
    derive_artefact_paths,
    derive_idle_source_path,
)
from .bootstrap_policy import inert_staging_objects
from .bootstrap_validation import (
    IDLE_SOURCE_DIRECTORY_NAME,
    IDLE_SOURCE_REPORT_FILE_NAME,
    IDLE_SOURCE_REPORT_SCHEMA,
    animated_idle_input_failures,
    idle_source_report_structure_failures,
)
from .character_baselines import resolve_approved_character
from .compatibility import ensure_registered
from .setup_parity import parity_fingerprint_records, setup_parity_failures
from .temp_paths import resolve_temp_root


class IdleSourceError(RuntimeError):
    """Raised when the idle-source staging contract is violated."""


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _open_mainfile(path: Path) -> None:
    ensure_registered()
    bpy.ops.wm.open_mainfile(filepath=str(path), load_ui=False, use_scripts=False)


def _save_staged(path: Path) -> None:
    bpy.ops.file.make_paths_absolute()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(path), check_existing=False)


def _curve_records(action: Any) -> list[dict[str, Any]]:
    """Plain curve records for one action, shared by admission and the report."""
    return [
        {
            "data_path": str(fcurve.data_path),
            "array_index": int(fcurve.array_index),
            "keyframes": [
                [float(key.co[0]), float(key.co[1]), str(key.interpolation)]
                for key in fcurve.keyframe_points
            ],
        }
        for fcurve in _action_fcurves(action)
    ]


def _append_action(source_path: Path, action_name: str) -> Any:
    """Append exactly one donor action, refusing anything that rides along."""
    with bpy.data.libraries.load(str(source_path), link=False) as (source, destination):
        available = list(source.actions)
        if action_name not in available:
            raise IdleSourceError(
                f"The donor blend {source_path} carries no action {action_name!r}; "
                f"available: {sorted(available)[:8]}"
            )
        destination.actions = [action_name]
    appended = bpy.data.actions.get(action_name)
    if appended is None:
        raise IdleSourceError(f"The donor action {action_name!r} failed to append.")
    return appended


def _assert_no_inert_rigs(staged_objects: list[str], phase: str) -> None:
    inert = inert_staging_objects(staged_objects)
    if inert:
        raise IdleSourceError(
            f"{phase}: the staged idle source carries inert staging rigs {inert}; "
            "an idle source is a copy of the calibrated setup only (KIMODO-IDLE-002)"
        )


def _verify_baseline(
    hashes: Mapping[str, str], phase: str, expected: Mapping[str, str]
) -> None:
    for key in ("canonical_source", "neutral_template", "character_setup"):
        if hashes[key] != expected[key]:
            raise IdleSourceError(
                f"{phase}: bootstrap input {key} digest {hashes[key]} does not match the "
                f"approved character baseline {expected[key]}; refusing to stage over "
                "unapproved bytes (re-extraction or an in-place edit drifted the artefact)."
            )


def stage_idle_source(
    variant: str,
    character_asset_root: str | Path,
    donor_blend: str | Path,
    donor_action: str,
    setup_path: str | Path | None = None,
    temp_root: str | Path | None = None,
    repository_root_path: str | Path | None = None,
    approved_character_baselines: Mapping[str, Mapping[str, str]] | None = None,
) -> dict[str, Any]:
    """Stage, verify, and transactionally publish one per-character idle source."""
    try:
        paths = derive_artefact_paths(
            character_asset_root,
            variant,
            Path(repository_root_path).expanduser().resolve()
            if repository_root_path is not None
            else None,
        )
    except (BootstrapPathError, ValueError) as error:
        raise IdleSourceError(str(error)) from error

    setup_input = (
        Path(setup_path).expanduser().resolve()
        if setup_path is not None
        else paths.character_setup
    )
    donor_input = Path(donor_blend).expanduser().resolve()
    inputs = {
        "canonical_source": paths.canonical_source,
        "neutral_template": paths.neutral_template,
        "character_setup": setup_input,
        "donor_blend": donor_input,
    }
    for label, candidate in inputs.items():
        if not candidate.is_file():
            raise IdleSourceError(f"The {label} input does not exist: {candidate}")

    # The Gate 1 baseline binds the DERIVED artefact paths, exactly as the
    # workspace bootstrap does; a caller-supplied --setup override is still
    # hashed before and after the run so the report attests unchanged bytes.
    baseline_files = {
        "canonical_source": paths.canonical_source,
        "neutral_template": paths.neutral_template,
        "character_setup": paths.character_setup,
    }
    baseline_before = {key: _sha256(path) for key, path in baseline_files.items()}
    try:
        approved_character, expected_baseline = resolve_approved_character(
            baseline_before, approved_character_baselines
        )
    except ValueError as error:
        raise IdleSourceError(str(error)) from error
    hashes_before = {key: _sha256(path) for key, path in inputs.items()}

    destination = derive_idle_source_path(paths.character_asset_root, paths.variant)
    if destination.exists():
        raise IdleSourceError(
            f"The idle source already exists at {destination}; it is a protected "
            "character-authored input, so staging never overwrites one — delete it "
            "deliberately before re-staging."
        )

    staging_root = resolve_temp_root(temp_root) / IDLE_SOURCE_DIRECTORY_NAME
    staged_directory = staging_root / "staged"
    staged_directory.mkdir(parents=True, exist_ok=True)
    staged = staged_directory / f"{paths.variant}.blend"
    shutil.copy2(setup_input, staged)
    if _sha256(staged) != baseline_before["character_setup"]:
        raise IdleSourceError("The staged idle-source copy does not match the setup input bytes.")
    _open_mainfile(staged)

    try:
        setup_records = capture_setup_parity_records(None)
    except ProcessorError as error:
        raise IdleSourceError(str(error)) from error
    setup_objects = sorted(obj.name for obj in bpy.data.objects)
    _assert_no_inert_rigs(setup_objects, "The character setup input")
    if bpy.data.actions:
        raise IdleSourceError(
            "The character setup input carries actions "
            f"{sorted(action.name for action in bpy.data.actions)}; a staged idle "
            "source appends its donor action onto an action-free setup."
        )
    source_rig = bpy.data.objects[SOURCE_ARMATURE_NAME]
    rig_bone_names = sorted(bone.name for bone in source_rig.data.bones)

    appended = _append_action(donor_input, donor_action)
    appended.use_fake_user = True
    curves = _curve_records(appended)
    admission = {
        "action_name": appended.name,
        "curves": curves,
        "rig_bone_names": rig_bone_names,
    }
    admission_failures = animated_idle_input_failures(admission)
    if admission_failures:
        raise IdleSourceError(
            f"The donor action {donor_action!r} from {donor_input} failed animated "
            "idle admission: " + "; ".join(admission_failures)
        )
    frame_start = int(math.floor(float(appended.frame_range[0])))
    frame_end = int(math.ceil(float(appended.frame_range[1])))
    bpy.context.scene.frame_start = frame_start
    bpy.context.scene.frame_end = frame_end
    curve_count = len(_action_fcurves(appended))
    # The staged save and reopen below invalidate live datablock references;
    # keep the plain name for every later comparison.
    appended_action_name = appended.name

    _save_staged(staged)

    _open_mainfile(staged)
    try:
        reopened_records = capture_setup_parity_records(None)
    except ProcessorError as error:
        raise IdleSourceError(str(error)) from error
    parity_failures = setup_parity_failures(setup_records, reopened_records)
    if parity_failures:
        raise IdleSourceError(
            "The staged idle source drifted from the approved setup calibration: "
            + "; ".join(parity_failures)
        )
    reopened_objects = sorted(obj.name for obj in bpy.data.objects)
    _assert_no_inert_rigs(reopened_objects, "The staged idle source")
    if reopened_objects != setup_objects:
        unexpected = sorted(set(reopened_objects) - set(setup_objects))
        missing = sorted(set(setup_objects) - set(reopened_objects))
        raise IdleSourceError(
            f"The staged idle source object set drifted: unexpected={unexpected}, "
            f"missing={missing}; only the donor action may be appended."
        )
    reopened_actions = sorted(action.name for action in bpy.data.actions)
    if reopened_actions != [appended_action_name]:
        raise IdleSourceError(
            f"The staged idle source must carry exactly the donor action "
            f"{appended_action_name!r}; found {reopened_actions}."
        )
    reopened = bpy.data.actions[appended_action_name]
    reopened_range = (
        int(math.floor(float(reopened.frame_range[0]))),
        int(math.ceil(float(reopened.frame_range[1]))),
    )
    if (
        not reopened.use_fake_user
        or len(_action_fcurves(reopened)) != curve_count
        or reopened_range != (frame_start, frame_end)
    ):
        raise IdleSourceError(
            "The appended idle action did not survive staging intact "
            f"(fake_user={reopened.use_fake_user}, curves="
            f"{len(_action_fcurves(reopened))}/{curve_count}, "
            f"frame_range={reopened_range}/({frame_start}, {frame_end}))."
        )

    hashes_after = {key: _sha256(path) for key, path in inputs.items()}
    baseline_after = {key: _sha256(path) for key, path in baseline_files.items()}
    _verify_baseline(baseline_after, "post-run check", expected_baseline)
    for key in inputs:
        if hashes_after[key] != hashes_before[key]:
            raise IdleSourceError(
                f"The staging run modified the {key} input {inputs[key]}; inputs are "
                "read-only."
            )

    setup_fingerprints = parity_fingerprint_records(setup_records)
    staged_fingerprints = parity_fingerprint_records(reopened_records)
    destination.parent.mkdir(parents=True, exist_ok=True)
    idle_source_sha = _sha256(staged)
    report = {
        "schema": IDLE_SOURCE_REPORT_SCHEMA,
        "generated_by": "kimodo_pipeline/idle_source_bootstrap.py",
        "blender_version": ".".join(str(component) for component in bpy.app.version),
        "variant": paths.variant,
        "character_asset_root": str(paths.character_asset_root),
        "baseline_character": approved_character,
        "inputs": {
            key: {
                "path": str(path),
                "sha256_before": hashes_before[key],
                "sha256_after": hashes_after[key],
                "unchanged": hashes_before[key] == hashes_after[key],
            }
            for key, path in inputs.items()
        },
        "baseline": {
            key: {
                "path": str(path),
                "sha256": baseline_after[key],
                "matches_gate1": baseline_after[key] == expected_baseline[key],
            }
            for key, path in baseline_files.items()
        },
        "donor": {
            "blend": str(donor_input),
            "action": appended_action_name,
            "frame_range": [frame_start, frame_end],
            "curve_count": curve_count,
        },
        "idle_source": {
            "destination": str(destination),
            "staged": str(staged),
            "published": True,
            "replaced_existing": False,
            "sha256": idle_source_sha,
        },
        "admission": {"passed": True, "failures": []},
        "fingerprints": {
            key: {
                "setup": setup_fingerprints[key],
                "idle_source": staged_fingerprints[key],
                "equal": setup_fingerprints[key] == staged_fingerprints[key],
            }
            for key in sorted(setup_fingerprints)
        },
        "inert_rig_objects": [],
        "object_names": reopened_objects,
        "actions": reopened_actions,
        "validation": {"passed": True, "failures": []},
    }
    structural_failures = idle_source_report_structure_failures(report)
    if structural_failures:
        raise IdleSourceError(
            f"Idle-source report failed its own structure contract: {structural_failures}"
        )
    report_path = staging_root / IDLE_SOURCE_REPORT_FILE_NAME
    report_path.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")

    publish = destination.with_name(f".{destination.name}.kimodo-publish")
    try:
        shutil.copy2(staged, publish)
        os.replace(publish, destination)
    except OSError:
        publish.unlink(missing_ok=True)
        raise

    return {
        "report_path": str(report_path),
        "idle_source": {
            "destination": str(destination),
            "sha256": idle_source_sha,
            "replaced_existing": False,
        },
        "variant": paths.variant,
        "baseline_character": approved_character,
        "validation_passed": True,
        "inputs_unchanged": True,
    }


def _arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--character-root", type=Path, required=True,
        help="Character asset root owning the setup input and the idle-source destination.",
    )
    parser.add_argument(
        "--variant", required=True, help="Lower-case snake_case variant key for path derivation."
    )
    parser.add_argument(
        "--donor-blend", type=Path, required=True,
        help="Blend carrying the animated Kimodo-space idle action (read-only; never modified).",
    )
    parser.add_argument(
        "--donor-action", required=True,
        help="Name of the animated Kimodo-space idle action inside --donor-blend.",
    )
    parser.add_argument(
        "--setup", default=None,
        help="Character retarget setup .blend; defaults to the derived character-setup artefact.",
    )
    parser.add_argument(
        "--temp-root",
        help="Optional staging/report root beneath the repository game/temp directory.",
    )
    parser.add_argument(
        "--repository-root",
        help="Optional root for repository-fixed bootstrap inputs in disposable tests.",
    )
    parser.add_argument(
        "--approved-character-baselines",
        help=(
            "Optional JSON per-character baseline mapping for a disposable fixture, "
            'as in \'{"fixture_character": {"canonical_source": "<sha256>", ...}}\'.'
        ),
    )
    arguments = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else []
    return parser.parse_args(arguments)


def main() -> int:
    arguments = _arguments()
    try:
        summary = stage_idle_source(
            variant=arguments.variant,
            character_asset_root=arguments.character_root,
            donor_blend=arguments.donor_blend,
            donor_action=arguments.donor_action,
            setup_path=arguments.setup,
            temp_root=arguments.temp_root,
            repository_root_path=arguments.repository_root,
            approved_character_baselines=(
                json.loads(arguments.approved_character_baselines)
                if arguments.approved_character_baselines is not None
                else None
            ),
        )
    except (IdleSourceError, ProcessorError, ValueError, OSError) as error:
        print(f"Kimodo idle-source bootstrap failed: {error}", file=sys.stderr)
        return 2
    print("KIMODO_IDLE_SOURCE_JSON=" + json.dumps(summary, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
