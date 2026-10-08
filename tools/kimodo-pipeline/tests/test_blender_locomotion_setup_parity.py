"""Real producer-bound calibration refusal, independent of carrier integrity."""
from pathlib import Path
import hashlib
import json
import shutil
import tempfile

import pytest

from kimodo_pipeline.blender_cli import run_blender_python
from kimodo_pipeline.bootstrap_paths import derive_artefact_paths, derive_idle_source_path
from kimodo_pipeline.workflow_binding import validate_materialisation_report

ROOT = Path(__file__).resolve().parents[3]
PIPELINE = ROOT / "tools/kimodo-pipeline"
REAL_CHARACTER = ROOT / "game/assets/characters/reference/female"
# Selected input identity, not an authored motion/pose oracle.
VARIANT = derive_idle_source_path(REAL_CHARACTER, "sad").stem


def run(work: Path, label: str, arguments: list):
    result = run_blender_python(arguments, ROOT)
    (work / (label + ".log")).write_text(result.stdout + "\nSTDERR\n" + result.stderr)
    return result


@pytest.fixture(scope="module")
def real_carriers():
    evidence = ROOT / "game/temp/wp6-b02-fix"
    evidence.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="maintained-real-", dir=evidence))
    real = derive_artefact_paths(REAL_CHARACTER, VARIANT, ROOT)
    character = work / "character"
    setup = derive_artefact_paths(character, VARIANT, ROOT).character_setup
    setup.parent.mkdir(parents=True)
    shutil.copy2(real.character_setup, setup)
    approval = {"female": {
        key: hashlib.sha256(getattr(real, key).read_bytes()).hexdigest()
        for key in ("canonical_source", "neutral_template", "character_setup")
    }}
    (work / "approval.json").write_text(json.dumps(approval, indent=2))
    cases = {}
    for case, helpers in (
        ("control", ()),
        ("forearms", ("lowerarm_l_RET", "lowerarm_r_RET")),
        ("thighs", ("thigh_l_RET", "thigh_r_RET")),
    ):
        directory = work / case
        directory.mkdir()
        source = directory / "producer-input.blend"
        if helpers:
            result = run(work, case + "-mutation", [real.authoring_workspace, "--python-expr",
                "import bpy; from mathutils import Matrix; "
                + " ".join(f"bpy.data.objects['Kimodo_Source'].pose.bones[{name!r}].matrix_basis=Matrix.Identity(4);" for name in helpers)
                + f" bpy.ops.wm.save_as_mainfile(filepath={str(source)!r},check_existing=False)"])
            assert result.returncode == 0, result.stdout + result.stderr
        else:
            shutil.copy2(real.authoring_workspace, source)
        precursor = directory / "precursor.blend"
        repair = directory / "repair.json"
        result = run(work, case + "-repair", ["--python", PIPELINE / "kimodo_pipeline/repair_locomotion_source.py", "--",
            "--input", source, "--output", precursor, "--report", repair, "--role", "pr45"])
        assert result.returncode == 0, result.stdout + result.stderr
        carrier, report = directory / "materialised.blend", directory / "materialisation.json"
        # Only relocate disposable evidence; all producer admissions remain active.
        producer = directory / "materialise.py"
        producer.write_text(
            "import sys\nfrom pathlib import Path\n"
            f"sys.path.insert(0,{str(PIPELINE)!r})\n"
            "from kimodo_pipeline import materialise\n"
            f"materialise.repository_temp_root=lambda:Path({str(directory)!r})\n"
            "from kimodo_pipeline.materialise_locomotion import main\nraise SystemExit(main())\n"
        )
        result = run(work, case + "-materialise", ["--python", producer, "--", "--blend", precursor,
            "--variant", VARIANT, "--roles", "pr45", "--output", carrier,
            "--source-repair-report", repair, "--report", report, "--temp-root", directory / "temp"])
        assert result.returncode == 0, result.stdout + result.stderr
        validate_materialisation_report(report)
        cases[case] = (carrier, report)
    return work, character, approval, cases


def consume(fixture, case: str):
    work, character, approval, cases = fixture
    carrier, report = cases[case]
    output = work / (case + "-output")
    result = run(work, case + "-consumer", ["--python", PIPELINE / "tests/observe_retarget_bake.py", "--",
        "--package", "locomotion", "--source", carrier, "--character-root", character,
        "--output-root", output, "--variant", VARIANT, "--roles", "pr45", "--root-lateral-axis", "0",
        "--materialisation-report", report, "--approved-character-baselines", json.dumps(approval)])
    return result, output


def test_real_calibrated_report_bound_carrier_bakes(real_carriers):
    result, output = consume(real_carriers, "control")
    assert result.returncode == 0, result.stdout + result.stderr
    assert result.stdout.count("KIMODO_TEST_BAKE_ENTRY") == 1
    assert (output / "locomotion" / f"locomotion_{VARIANT}.blend").is_file()


@pytest.mark.parametrize("case,names", [
    ("forearms", ("lowerarm_l_RET", "lowerarm_r_RET")),
    ("thighs", ("thigh_l_RET", "thigh_r_RET")),
])
def test_real_preproducer_pose_drift_refuses_before_bake(real_carriers, case, names):
    result, output = consume(real_carriers, case)
    assert result.returncode != 0, result.stdout + result.stderr
    assert "KIMODO_TEST_BAKE_ENTRY" not in result.stdout
    assert not output.exists()
    for name in names:
        assert name in result.stderr
    assert "fixed pose matrix_basis" in result.stderr


def test_stale_real_report_still_refuses_before_bake(real_carriers):
    work, character, approval, cases = real_carriers
    carrier, report = cases["control"]
    before = carrier.read_bytes()
    try:
        carrier.write_bytes(before + b"post-report mutation")
        result, output = consume((work, character, approval, {"stale": (carrier, report)}), "stale")
        assert result.returncode != 0
        assert "report-bound final bytes changed" in result.stderr
        assert "KIMODO_TEST_BAKE_ENTRY" not in result.stdout
        assert not output.exists()
    finally:
        carrier.write_bytes(before)


def test_approved_renamed_real_target_composes_materialises_and_bakes(real_carriers):
    work = real_carriers[0]
    directory = work / "renamed"
    directory.mkdir()
    character = directory / "character"
    paths = derive_artefact_paths(character, VARIANT, ROOT)
    real = derive_artefact_paths(REAL_CHARACTER, VARIANT, ROOT)
    paths.character_setup.parent.mkdir(parents=True)
    shutil.copy2(real.character_setup, paths.character_setup)
    result = run(work, "renamed-setup", [paths.character_setup, "--python-expr",
        "import bpy; bpy.data.objects['Female'].name='Reference_Fixture'; "
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"])
    assert result.returncode == 0, result.stdout + result.stderr
    approval = {"renamed_fixture": {
        key: hashlib.sha256(getattr(paths, key).read_bytes()).hexdigest()
        for key in ("canonical_source", "neutral_template", "character_setup")
    }}
    (directory / "approval.json").write_text(json.dumps(approval, indent=2))
    result = run(work, "renamed-compose", ["--python", PIPELINE / "kimodo_pipeline/workspace_bootstrap.py", "--",
        "--variant", VARIANT, "--character-root", character, "--setup", paths.character_setup,
        "--template", paths.neutral_template, "--idle-blend", paths.neutral_template, "--idle-action", "pose_ref_idle",
        "--temp-root", directory / "compose-temp", "--approved-character-baselines", json.dumps(approval)])
    assert result.returncode == 0, result.stdout + result.stderr
    summary = json.loads(next(line.split("=", 1)[1] for line in result.stdout.splitlines()
                              if line.startswith("KIMODO_BOOTSTRAP_WORKSPACE_JSON=")))
    report = json.loads(Path(summary["report_path"]).read_text())
    assert report["inventory"]["fingerprints"]["female_constraint_graph_edge_count"] > 0
    source = directory / "producer-input.blend"
    append = directory / "append.py"
    append.write_text("import bpy\n"
        f"with bpy.data.libraries.load({str(real.authoring_workspace)!r},link=False) as (src,dst):\n"
        "    dst.actions=[name for name in src.actions if name.startswith('kimodo_multi_')]\n"
        "for action in dst.actions: action.use_fake_user=True\n"
        f"bpy.ops.wm.save_as_mainfile(filepath={str(source)!r},check_existing=False)\n")
    result = run(work, "renamed-append", [paths.authoring_workspace, "--python", append])
    assert result.returncode == 0, result.stdout + result.stderr
    precursor, repair = directory / "precursor.blend", directory / "repair.json"
    result = run(work, "renamed-repair", ["--python", PIPELINE / "kimodo_pipeline/repair_locomotion_source.py", "--",
        "--input", source, "--output", precursor, "--report", repair, "--role", "pr45"])
    assert result.returncode == 0, result.stdout + result.stderr
    carrier, binding = directory / "materialised.blend", directory / "materialisation.json"
    producer = directory / "materialise.py"
    producer.write_text("import sys\nfrom pathlib import Path\n"
        f"sys.path.insert(0,{str(PIPELINE)!r})\nfrom kimodo_pipeline import materialise\n"
        f"materialise.repository_temp_root=lambda:Path({str(directory)!r})\n"
        "from kimodo_pipeline.materialise_locomotion import main\nraise SystemExit(main())\n")
    result = run(work, "renamed-materialise", ["--python", producer, "--", "--blend", precursor,
        "--variant", VARIANT, "--roles", "pr45", "--output", carrier,
        "--source-repair-report", repair, "--report", binding, "--temp-root", directory / "temp"])
    assert result.returncode == 0, result.stdout + result.stderr
    validate_materialisation_report(binding)
    result, output = consume((work, character, approval, {"renamed": (carrier, binding)}), "renamed")
    assert result.returncode == 0, result.stdout + result.stderr
    assert result.stdout.count("KIMODO_TEST_BAKE_ENTRY") == 1
    index = json.loads((output / "locomotion" / VARIANT / "processed-index.json").read_text())
    assert index["target_skeleton"] == "Reference_Fixture"
    assert index["setup_binding"]["baseline_character"] == "renamed_fixture"
    assert index["setup_binding"]["setup_parity"]["target_rig"] == "Reference_Fixture"
