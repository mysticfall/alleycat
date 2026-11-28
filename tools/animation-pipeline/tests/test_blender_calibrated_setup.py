"""Synthetic Blender coverage for the calibrated-setup gate (KIMODO-SET-002, KIMODO-CHAR-001).

Idle-source bootstrap, workspace composition and native idle clip export rely on
``calibrated_setup`` to resolve the character target rig generically and to refuse
calibration drift or unapproved setup bytes before writing any output.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

import pytest

from approved_setup_fixture import ApprovedSetupBuilder
from kimodo_pipeline.blender_cli import run_blender_python
from kimodo_pipeline.bootstrap_paths import derive_artefact_paths
from synthetic_kimodo_blend import FIXTURE_VARIANT

ROOT = Path(__file__).resolve().parents[3]
GATE = ROOT / "tools/animation-pipeline/tests/assert_setup_gate.py"
SCRATCH = ROOT / "game/temp/kimodo-calibrated-setup"


def _build(directory: Path, target_rig_name: str = "Female") -> Path:
    source = directory / "source.blend"
    built = ApprovedSetupBuilder().build_bootstrap_monolith(source, target_rig_name=target_rig_name)
    assert built.returncode == 0, built.stdout + built.stderr
    return source


@pytest.fixture(scope="module")
def approved_fixture():
    SCRATCH.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="approved-", dir=SCRATCH) as directory:
        _build(Path(directory))
        yield Path(directory)


@pytest.fixture
def fixture_copy(approved_fixture):
    with tempfile.TemporaryDirectory(prefix="case-", dir=SCRATCH) as directory:
        copy = Path(directory) / "fixture"
        shutil.copytree(approved_fixture, copy)
        yield copy


def _gate(character_root: Path, *target_rig: str) -> subprocess.CompletedProcess[str]:
    return run_blender_python([
        "--python", GATE, "--", character_root, character_root / "source.blend",
        character_root / "approved-baseline.json", *target_rig,
    ], ROOT)


def _gate_result(result: subprocess.CompletedProcess[str]) -> dict:
    assert result.returncode == 0, result.stdout + result.stderr
    line = next(line for line in result.stdout.splitlines() if line.startswith("SETUP_GATE_JSON="))
    return json.loads(line.removeprefix("SETUP_GATE_JSON="))


def test_approved_source_passes_and_binds_the_setup(fixture_copy: Path) -> None:
    setup = derive_artefact_paths(fixture_copy, FIXTURE_VARIANT, ROOT).character_setup
    result = _gate_result(_gate(fixture_copy))
    assert result["baseline_character"] == "fixture_character"
    assert result["setup_sha256"] == hashlib.sha256(setup.read_bytes()).hexdigest()
    assert result["setup_parity"]["target_rig"] == "Female"


def test_target_rig_is_resolved_generically_from_the_opened_content() -> None:
    SCRATCH.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="renamed-", dir=SCRATCH) as directory:
        root = Path(directory)
        _build(root, target_rig_name="Reference_Male")
        assert _gate_result(_gate(root))["setup_parity"]["target_rig"] == "Reference_Male"


@pytest.mark.parametrize("bogus,expected", [
    ("No_Such_Rig", 'names no armature named "No_Such_Rig"'),
    ("Kimodo_Source", "must name the character target rig"),
])
def test_explicit_target_rig_is_validated_against_the_opened_content(
    fixture_copy: Path, bogus: str, expected: str
) -> None:
    result = _gate(fixture_copy, bogus)
    assert result.returncode != 0
    assert expected in result.stderr


@pytest.mark.parametrize("mutation,diagnostic", [
    ("bpy.data.objects['Female'].pose.bones['Root'].constraints[0].mute=True", "mute"),
    ("bpy.context.view_layer.objects.active=bpy.data.objects['Kimodo_Source']; "
     "bpy.ops.object.mode_set(mode='EDIT'); "
     "bpy.data.objects['Kimodo_Source'].data.edit_bones['Root_RET'].roll=0.4; "
     "bpy.ops.object.mode_set(mode='OBJECT')", "rest"),
    ("bpy.data.objects['Kimodo_Source'].pose.bones['Root_RET'].keyframe_insert(data_path='location',frame=1)",
     "animated or driven"),
])
def test_calibration_drift_is_refused(fixture_copy: Path, mutation: str, diagnostic: str) -> None:
    source = fixture_copy / "source.blend"
    mutated = subprocess.run([
        "blender", "--background", "--factory-startup", str(source), "--python-expr",
        "import bpy; " + mutation + "; bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
    ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=120)
    assert mutated.returncode == 0, mutated.stdout + mutated.stderr
    result = _gate(fixture_copy)
    assert result.returncode != 0
    assert "Setup-parity gate refused the bake" in result.stderr
    assert diagnostic in result.stderr


def test_unapproved_setup_bytes_are_refused_with_their_digest(fixture_copy: Path) -> None:
    setup = derive_artefact_paths(fixture_copy, FIXTURE_VARIANT, ROOT).character_setup
    setup.write_bytes(setup.read_bytes() + b"unapproved setup bytes")
    result = _gate(fixture_copy)
    assert result.returncode != 0
    assert hashlib.sha256(setup.read_bytes()).hexdigest() in result.stderr
