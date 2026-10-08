"""Real-asset smoke for the idle-via-setup flow (structural, temp-output only).

Everything this module opens is read-only: the repository female setup, the
repository-fixed bootstrap inputs, and the repository female idle source (the
published migration artefact) are hashed before and after every run, staged
copies and bakes write only beneath ``game/temp``, and no repository character
tree gains or loses an idle source.  The donor action name is discovered from
the real idle source — an input, not a code constant.  The four-helper refusal
below reproduces the diagnosed pose-only drift — ``thigh_l/_r_RET`` and
``lowerarm_l/_r_RET`` fixed pose matrices reset — against the real calibrated
setup, separately from rest and Locked Track mute regressions.
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
STAGING_ENTRYPOINT = ROOT / "tools/kimodo-pipeline/commands/idle_source_bootstrap.py"
BAKE_ENTRYPOINT = ROOT / "tools/kimodo-pipeline/commands/retarget_animations.py"
REAL_CHARACTER_ROOT = ROOT / "game/assets/characters/reference/female"
# The real character's selected idle variant is an asset identity, not a
# literal: derive it from the published idle-source path.
REAL_VARIANT = derive_idle_source_path(REAL_CHARACTER_ROOT, "sad").stem
REAL_IDLE_SOURCE = derive_idle_source_path(REAL_CHARACTER_ROOT, REAL_VARIANT)
VARIANT = "ember"
UNCALIBRATED_HELPERS = ("thigh_l_RET", "thigh_r_RET", "lowerarm_l_RET", "lowerarm_r_RET")


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


def test_the_real_calibrated_idle_source_bakes_through_the_parity_gate(
    staged: dict[str, Path], donor_action: str,
) -> None:
    output = staged["work"] / "bake-output"
    result = _run([
        "--python", BAKE_ENTRYPOINT, "--",
        "--package", "idle", "--variant", VARIANT,
        "--character-root", staged["character"],
        "--idle-source-action", donor_action,
        "--output-root", str(output),
    ])
    assert result.returncode == 0, result.stdout + result.stderr
    index = json.loads(
        (output / "idle" / VARIANT / "processed-index.json").read_text(encoding="utf-8")
    )
    relative_idle_source = staged["idle_source"].relative_to(ROOT).as_posix()
    assert index["source_path"] == relative_idle_source
    binding = index["idle_source_binding"]
    assert binding["idle_source_path"] == relative_idle_source
    assert binding["idle_source_action"] == donor_action
    assert binding["setup_baseline_character"] == "female"
    assert binding["idle_source_sha256"] == _sha256(staged["idle_source"])
    assert binding["setup_sha256"] == _sha256(staged["real_setup"])
    fingerprints = binding["setup_parity_fingerprints"]
    assert fingerprints["target_rig"] == "Female"
    assert fingerprints["source_helper_bone_count"] == 53
    assert fingerprints["helper_capture_contract"] == "rest-and-unanimated-pose-basis-v2"
    staging_report = json.loads(Path(staged["staging_summary"]["report_path"]).read_text())
    assert fingerprints["source_helper_configuration_sha256"] == staging_report[
        "fingerprints"
    ]["source_helper_configuration_sha256"]["setup"]
    assert (output / "idle" / f"idle_{VARIANT}.blend").is_file()
    assert index["target_skeleton"] == "Female"
    probe = _run([output / "idle" / f"idle_{VARIANT}.blend", "--python-expr",
        "import bpy,json; a=next(iter(bpy.data.actions)); "
        "curves=[c for l in a.layers for s in l.strips for b in s.channelbags for c in b.fcurves]; "
        "varying=[c for c in curves if len(c.keyframe_points)==240 and "
        "max(p.co.y for p in c.keyframe_points)-min(p.co.y for p in c.keyframe_points)>1e-4]; "
        "print('REAL_VARYING=' + str(len(varying)))"])
    assert probe.returncode == 0, probe.stdout + probe.stderr
    assert int(next(line.partition("=")[2] for line in probe.stdout.splitlines()
                    if line.startswith("REAL_VARYING="))) > 0


def test_the_repository_female_character_bakes_its_own_idle_source(
    work: str, donor_action: str,
) -> None:
    """The published female idle source bakes through the production entry."""
    output = Path(work) / "real-character-output"
    idle_source_before = _sha256(REAL_IDLE_SOURCE)
    result = _run([
        "--python", BAKE_ENTRYPOINT, "--",
        "--package", "idle", "--variant", REAL_VARIANT,
        "--character-root", REAL_CHARACTER_ROOT,
        "--idle-source-action", donor_action,
        "--output-root", str(output),
    ])
    assert result.returncode == 0, result.stdout + result.stderr
    assert _sha256(REAL_IDLE_SOURCE) == idle_source_before
    index = json.loads(
        (output / "idle" / REAL_VARIANT / "processed-index.json").read_text(encoding="utf-8")
    )
    relative_idle_source = REAL_IDLE_SOURCE.relative_to(ROOT).as_posix()
    assert index["source_path"] == relative_idle_source
    binding = index["idle_source_binding"]
    assert binding["idle_source_path"] == relative_idle_source
    assert binding["idle_source_action"] == donor_action
    assert binding["setup_baseline_character"] == "female"
    assert binding["idle_source_sha256"] == idle_source_before
    assert (output / "idle" / f"idle_{REAL_VARIANT}.blend").is_file()
    assert index["target_skeleton"] == "Female"


@pytest.mark.parametrize("helpers_to_reset", [UNCALIBRATED_HELPERS, UNCALIBRATED_HELPERS[:2], UNCALIBRATED_HELPERS[2:]])
def test_an_idle_source_with_helper_pose_reset_is_refused(
    staged: dict[str, Path], donor_action: str,
    helpers_to_reset: tuple[str, ...],
) -> None:
    """The diagnosed legacy defect class must be refused and named."""
    label = helpers_to_reset[0] + str(len(helpers_to_reset))
    character = staged["work"] / ("uncalibrated-character-" + label)
    shutil.copytree(staged["character"], character)
    idle_source = derive_idle_source_path(character, VARIANT)
    helpers = ", ".join(f"'{name}'" for name in helpers_to_reset)
    mutation = (
        "import bpy\nfrom mathutils import Matrix\n"
        "src = bpy.data.objects['Kimodo_Source']\n"
        f"for name in ({helpers}):\n"
        "    src.pose.bones[name].matrix_basis = Matrix.Identity(4)\n"
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath, check_existing=False)"
    )
    mutated = _run([idle_source, "--python-expr", mutation])
    assert mutated.returncode == 0, mutated.stdout + mutated.stderr
    output = staged["work"] / ("uncalibrated-output-" + label)
    result = _run([
        "--python", BAKE_ENTRYPOINT, "--",
        "--package", "idle", "--variant", VARIANT,
        "--character-root", character,
        "--idle-source-action", donor_action,
        "--output-root", str(output),
    ])
    assert result.returncode != 0
    assert "Idle setup-parity gate refused the bake" in result.stderr
    for helper in helpers_to_reset:
        assert helper in result.stderr
    assert "fixed pose matrix_basis" in result.stderr
    assert "calibration drift" in result.stderr
    assert not output.exists()


def test_a_drifted_real_setup_copy_is_refused_by_the_baseline_digest(
    staged: dict[str, Path], donor_action: str,
) -> None:
    character = staged["work"] / "drifted-character"
    shutil.copytree(staged["character"], character)
    drifted_setup = derive_artefact_paths(character, VARIANT, ROOT).character_setup
    mutation = (
        "import bpy\n"
        "bpy.context.scene.frame_start = 77\n"
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath, check_existing=False)"
    )
    drifted = _run([drifted_setup, "--python-expr", mutation])
    assert drifted.returncode == 0, drifted.stdout + drifted.stderr
    output = staged["work"] / "drifted-output"
    result = _run([
        "--python", BAKE_ENTRYPOINT, "--",
        "--package", "idle", "--variant", VARIANT,
        "--character-root", character,
        "--idle-source-action", donor_action,
        "--output-root", str(output),
    ])
    assert result.returncode != 0
    assert "Idle setup-parity gate" in result.stderr
    assert "match no approved character baseline" in result.stderr
    assert "female" in result.stderr
    assert not output.exists()


@pytest.mark.parametrize("channel", ["rest", "mute", "animated", "driver"])
def test_other_real_calibration_drift_is_refused_before_output(
    staged: dict[str, Path], donor_action: str, channel: str,
) -> None:
    character = staged["work"] / ("drift-" + channel)
    shutil.copytree(staged["character"], character)
    idle_source = derive_idle_source_path(character, VARIANT)
    mutations = {
        "rest": "bpy.context.view_layer.objects.active=src\nsrc.select_set(True)\n"
                "bpy.ops.object.mode_set(mode='EDIT')\n"
                "src.data.edit_bones['thigh_l_RET'].roll += 0.2\n"
                "bpy.ops.object.mode_set(mode='OBJECT')\n",
        "mute": "next(c for c in src.pose.bones['thigh_l_RET'].constraints "
                "if c.type=='LOCKED_TRACK').mute=False\n",
        "animated": "src.animation_data_create().action=next(iter(bpy.data.actions))\n"
                    "src.animation_data.action_slot=src.animation_data.action.slots[0]\n"
                    "src.pose.bones['thigh_l_RET'].keyframe_insert(data_path='rotation_euler',frame=1)\n",
        "driver": "src.pose.bones['thigh_l_RET'].driver_add('rotation_euler',0)\n",
    }
    mutation = "import bpy\nsrc=bpy.data.objects['Kimodo_Source']\n" + mutations[channel] + (
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"
    )
    mutated = _run([idle_source, "--python-expr", mutation])
    assert mutated.returncode == 0, mutated.stdout + mutated.stderr
    output = staged["work"] / ("refused-output-" + channel)
    result = _run(["--python", BAKE_ENTRYPOINT, "--", "--package", "idle",
                   "--variant", VARIANT, "--character-root", character,
                   "--idle-source-action", donor_action, "--output-root", output])
    assert result.returncode != 0, result.stdout + result.stderr
    assert "Idle setup-parity gate refused the bake" in result.stderr
    assert "thigh_l_RET" in result.stderr
    expected = {"rest": "rest basis/roll state", "mute": "mute state differs",
                "animated": "is animated or driven", "driver": "is animated or driven"}
    assert expected[channel] in result.stderr
    assert not output.exists()


def test_fixed_calibration_is_independent_of_animated_donor_evaluation(
    staged: dict[str, Path], donor_action: str,
) -> None:
    expression = (
        "import bpy,sys\n"
        f"sys.path.insert(0,{str(ROOT / 'tools/kimodo-pipeline')!r})\n"
        "from kimodo_pipeline.blender_retargeting import capture_setup_parity_records,bind_action_slot\n"
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
