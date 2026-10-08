"""Synthetic Blender coverage for the idle-source staging command (KIMODO-IDLE-002)."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile

import pytest

from synthetic_kimodo_blend import FIXTURE_VARIANT, SyntheticKimodoBlendBuilder
from kimodo_pipeline.bootstrap_paths import derive_idle_source_path
from kimodo_pipeline.bootstrap_policy import inert_staging_objects
from kimodo_pipeline.bootstrap_validation import idle_source_report_structure_failures
from test_blender_bootstrap_extraction_smoke import _fixture, _manifest
from test_blender_bootstrap_extraction_smoke import _arguments as extraction_arguments


ROOT = Path(__file__).resolve().parents[3]
ENTRYPOINT = ROOT / "tools/kimodo-pipeline/commands/idle_source_bootstrap.py"
DONOR_ACTION = "kimodo_multi_fixture_loop"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


@pytest.fixture(scope="module")
def fixture() -> dict[str, Path]:
    """One extraction fixture plus one synthetic donor blend for the module."""
    with tempfile.TemporaryDirectory(prefix="kimodo-idle-source-", dir=ROOT / "game/temp") as directory:
        work = Path(directory)
        built = _fixture(work)
        from kimodo_pipeline.blender_cli import run_blender_python
        from kimodo_pipeline.bootstrap_paths import derive_artefact_paths

        manifest = _manifest(built)
        extracted = run_blender_python(extraction_arguments(built, manifest), ROOT)
        if extracted.returncode != 0:
            raise RuntimeError(extracted.stdout + extracted.stderr)
        paths = derive_artefact_paths(built["character"], FIXTURE_VARIANT, built["repository"])
        built["setup"] = paths.character_setup
        built["template"] = paths.neutral_template
        built["canonical"] = paths.canonical_source
        donor = work / "inputs" / "donor_loops.blend"
        staged = SyntheticKimodoBlendBuilder().build(donor)
        if staged.returncode != 0:
            raise RuntimeError(staged.stdout + staged.stderr)
        built["donor"] = donor
        yield built


def _arguments(
    fixture: dict[str, Path], variant: str,
    donor: Path | None = None, action: str = DONOR_ACTION,
    **overrides: object,
) -> list[Path | str]:
    approved = overrides.get("approved", {
        "fixture_character": {
            "canonical_source": _sha256(fixture["canonical"]),
            "neutral_template": _sha256(fixture["template"]),
            "character_setup": _sha256(fixture["setup"]),
        }
    })
    return [
        "--python", ENTRYPOINT, "--", "--character-root", fixture["character"],
        "--variant", variant, "--donor-blend", donor if donor is not None else fixture["donor"],
        "--donor-action", action, "--temp-root", fixture["temp"],
        "--repository-root", fixture["repository"],
        "--approved-character-baselines", json.dumps(approved, sort_keys=True),
    ]


def _summary(result: object) -> dict:
    return json.loads(next(
        line.removeprefix("KIMODO_IDLE_SOURCE_JSON=")
        for line in result.stdout.splitlines()
        if line.startswith("KIMODO_IDLE_SOURCE_JSON=")
    ))


def _run(arguments: list[Path | str]) -> object:
    from kimodo_pipeline.blender_cli import run_blender_python

    return run_blender_python(arguments, ROOT)


def test_stages_a_verified_idle_source_without_touching_inputs(fixture: dict[str, Path]) -> None:
    inputs = {
        key: _sha256(fixture[key])
        for key in ("setup", "template", "donor", "canonical")
    }
    result = _run(_arguments(fixture, "ember"))
    assert result.returncode == 0, result.stdout + result.stderr
    summary = _summary(result)
    assert summary["validation_passed"] is True
    assert summary["inputs_unchanged"] is True
    assert summary["baseline_character"] == "fixture_character"
    destination = derive_idle_source_path(fixture["character"], "ember")
    assert Path(summary["idle_source"]["destination"]) == destination
    assert destination.is_file()
    assert _sha256(destination) == summary["idle_source"]["sha256"]
    report = json.loads(Path(summary["report_path"]).read_text(encoding="utf-8"))
    assert idle_source_report_structure_failures(report) == []
    assert report["baseline_character"] == "fixture_character"
    assert report["donor"]["action"] == DONOR_ACTION
    assert report["actions"] == [DONOR_ACTION]
    # Staging from the setup keeps inert rigs out structurally, and the
    # appended action is the only content difference.
    assert report["inert_rig_objects"] == []
    assert inert_staging_objects(report["object_names"]) == []
    assert all(entry["equal"] for entry in report["fingerprints"].values())
    # No input was written, including the donor blend.
    assert {key: _sha256(fixture[key]) for key in inputs} == inputs


def test_a_single_frame_donor_action_is_refused_as_a_pose_reference(fixture: dict[str, Path]) -> None:
    destination = derive_idle_source_path(fixture["character"], "calm")
    result = _run(_arguments(fixture, "calm", donor=fixture["template"], action="pose_ref_idle"))
    assert result.returncode == 2, result.stdout + result.stderr
    assert "must be an animated loop" in result.stderr
    assert "pose-reference bootstrap admission" in result.stderr
    assert not destination.exists()


def test_unapproved_setup_bytes_are_refused_before_staging(fixture: dict[str, Path]) -> None:
    destination = derive_idle_source_path(fixture["character"], "hush")
    unrecognised = {
        "other_character": {
            "canonical_source": _sha256(fixture["canonical"]),
            "neutral_template": _sha256(fixture["template"]),
            "character_setup": "0" * 64,
        }
    }
    result = _run(_arguments(fixture, "hush", approved=unrecognised))
    assert result.returncode == 2, result.stdout + result.stderr
    assert _sha256(fixture["setup"]) in result.stderr
    assert "other_character" in result.stderr
    assert not destination.exists()


def test_an_unknown_donor_action_is_refused_with_available_actions(fixture: dict[str, Path]) -> None:
    result = _run(_arguments(fixture, "still", action="fixture_missing_action"))
    assert result.returncode == 2, result.stdout + result.stderr
    assert "carries no action" in result.stderr
    assert DONOR_ACTION in result.stderr
    assert not derive_idle_source_path(fixture["character"], "still").exists()


def test_an_existing_idle_source_is_never_overwritten(fixture: dict[str, Path]) -> None:
    destination = derive_idle_source_path(fixture["character"], "mild")
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(b"fixture protected idle source sentinel")
    result = _run(_arguments(fixture, "mild"))
    assert result.returncode == 2, result.stdout + result.stderr
    assert "protected" in result.stderr
    assert destination.read_bytes() == b"fixture protected idle source sentinel"
    # The refused run leaves no staging clutter beside the destination.
    assert sorted(
        path.name for path in destination.parent.iterdir() if path.name.startswith(".")
    ) == []
    shutil.rmtree(destination.parent)


def test_a_failed_stage_leaves_no_partial_idle_source(fixture: dict[str, Path]) -> None:
    """A donor that disappears mid-flight cannot publish partial content.

    The admission path already ran Blender; here the missing donor file is
    refused before any staging begins, proving missing-input failures precede
    every write.
    """
    result = _run(_arguments(fixture, "late", donor=fixture["root"] / "absent.blend"))
    assert result.returncode == 2, result.stdout + result.stderr
    assert "donor_blend" in result.stderr
    assert not derive_idle_source_path(fixture["character"], "late").exists()
