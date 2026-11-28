"""Real-asset smoke for idle-source staging on the calibrated setup (structural, temp-output only).

Everything this module opens is read-only: the repository female setup, the
repository-fixed bootstrap inputs, and the repository female idle source are
hashed before and after every run, staged copies write only beneath
``game/temp``, and no repository character tree gains or loses an idle source.
The donor action name is discovered from the real idle source — an input, not a
code constant.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile

import pytest

from kimodo_pipeline.bootstrap_paths import derive_artefact_paths, derive_idle_source_path


ROOT = Path(__file__).resolve().parents[3]
STAGING_ENTRYPOINT = ROOT / "tools/animation-pipeline/commands/idle_source_bootstrap.py"
REAL_CHARACTER_ROOT = ROOT / "game/assets/animations/source/reference/female"
# The real character's selected idle variant is an asset identity, not a
# literal: derive it from the published idle-source path.
REAL_VARIANT = derive_idle_source_path(REAL_CHARACTER_ROOT, "sad").stem
REAL_IDLE_SOURCE = derive_idle_source_path(REAL_CHARACTER_ROOT, REAL_VARIANT)
VARIANT = "ember"


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _run(arguments: list[Path | str]) -> object:
    from kimodo_pipeline.blender_cli import run_blender_python

    return run_blender_python(arguments, ROOT)


@pytest.fixture(scope="module")
def work() -> str:
    evidence = ROOT / "game/temp/b01-helper-pose"
    evidence.mkdir(parents=True, exist_ok=True)
    return tempfile.mkdtemp(prefix="real-", dir=evidence)


@pytest.fixture(scope="module")
def donor_action() -> str:
    """Discover the real idle source's single action name read-only."""
    result = _run([
        REAL_IDLE_SOURCE,
        "--python-expr",
        "import bpy; print('KIMODO_ACTIONS=' + '|'.join(sorted(a.name for a in bpy.data.actions)))",
    ])
    assert result.returncode == 0, result.stdout + result.stderr
    names = next(
        line.removeprefix("KIMODO_ACTIONS=")
        for line in result.stdout.splitlines()
        if line.startswith("KIMODO_ACTIONS=")
    ).split("|")
    assert len(names) == 1, f"The real idle source must carry exactly one action: {names}"
    return names[0]


@pytest.fixture(scope="module")
def staged(work: str, donor_action: str) -> dict[str, Path]:
    """Stage a real-setup idle source into a disposable character root."""
    work_path = Path(work)
    character = work_path / "character"
    real_setup = derive_artefact_paths(REAL_CHARACTER_ROOT, VARIANT, ROOT).character_setup
    fixture_setup = derive_artefact_paths(character, VARIANT, ROOT).character_setup
    fixture_setup.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(real_setup, fixture_setup)
    result = _run([
        "--python", STAGING_ENTRYPOINT, "--",
        "--character-root", character, "--variant", VARIANT,
        "--donor-blend", REAL_IDLE_SOURCE, "--donor-action", donor_action,
        "--temp-root", work_path / "temp",
    ])
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    yield {
        "work": work_path,
        "character": character,
        "real_setup": real_setup,
        "idle_source": derive_idle_source_path(character, VARIANT),
        "staging_summary": _summary(result),
    }


def _summary(result: object) -> dict:
    return json.loads(next(
        line.removeprefix("KIMODO_IDLE_SOURCE_JSON=")
        for line in result.stdout.splitlines()
        if line.startswith("KIMODO_IDLE_SOURCE_JSON=")
    ))


def test_the_real_setup_and_idle_source_stage_into_a_temp_idle_source(
    staged: dict[str, Path], donor_action: str,
) -> None:
    summary = staged["staging_summary"]
    # The real approved baselines resolve the real setup bytes: no fixture
    # override was supplied to the staging run.
    assert summary["baseline_character"] == "female"
    assert summary["validation_passed"] is True
    assert summary["inputs_unchanged"] is True
    assert summary["idle_source"]["destination"] == str(staged["idle_source"])
    assert _sha256(staged["idle_source"]) == summary["idle_source"]["sha256"]
    report = json.loads(Path(summary["report_path"]).read_text(encoding="utf-8"))
    assert report["baseline_character"] == "female"
    assert report["donor"]["action"] == donor_action
    assert report["donor"]["frame_range"] == [1, 240]
    assert report["inert_rig_objects"] == []
    assert report["actions"] == [donor_action]
    assert all(entry["equal"] for entry in report["fingerprints"].values())
    assert len(report["fingerprints"]["source_helper_configuration_sha256"]["setup"]) == 64
    # The repository setup and idle source stay untouched (the report attests
    # it and the bytes still hash to the approved female baseline).
    from kimodo_pipeline.character_baselines import APPROVED_CHARACTER_BASELINES

    assert _sha256(staged["real_setup"]) == APPROVED_CHARACTER_BASELINES["female"]["character_setup"]
    assert _sha256(REAL_IDLE_SOURCE) == report["inputs"]["donor_blend"]["sha256_before"]
    assert _sha256(REAL_IDLE_SOURCE) == report["inputs"]["donor_blend"]["sha256_after"]


def test_fixed_calibration_is_independent_of_animated_donor_evaluation(
    staged: dict[str, Path], donor_action: str,
) -> None:
    expression = (
        "import bpy,sys\n"
        f"sys.path.insert(0,{str(ROOT / 'tools/animation-pipeline')!r})\n"
        "from kimodo_pipeline.calibrated_setup import capture_setup_parity_records,bind_action_slot\n"
        "src=bpy.data.objects['Kimodo_Source']\n"
        f"action=bpy.data.actions[{donor_action!r}]\n"
        "src.animation_data_create().action=action\nbind_action_slot(src.animation_data,action)\n"
        "baseline=capture_setup_parity_records(None)\n"
        "initial=tuple(tuple(v for row in b.matrix_basis for v in row) for b in src.pose.bones)\n"
        "moving=False\n"
        "for frame in range(1,241):\n"
        "    bpy.context.scene.frame_set(frame)\n"
        "    assert capture_setup_parity_records(None)==baseline, frame\n"
        "    moving |= initial != tuple(tuple(v for row in b.matrix_basis for v in row) for b in src.pose.bones)\n"
        "assert moving, 'donor did not animate'\n"
        "print('FIXED_CALIBRATION_FRAMES=240')"
    )
    result = _run([staged["idle_source"], "--python-expr", expression])
    assert result.returncode == 0, result.stdout + result.stderr
    assert "FIXED_CALIBRATION_FRAMES=240" in result.stdout
