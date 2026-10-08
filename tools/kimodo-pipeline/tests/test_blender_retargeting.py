"""Synthetic Blender contract coverage for Kimodo constraint baking."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import pytest


from kimodo_pipeline.bootstrap_paths import derive_artefact_paths, repository_root


ROOT = repository_root()
TOOLS = ROOT / "tools"
sys.path.extend((str(TOOLS / "kimodo-pipeline"), str(TOOLS / "kimodo-pipeline/tests")))
from synthetic_kimodo_blend import FIXTURE_VARIANT
from approved_retarget_fixture import ApprovedRetargetBuilder as SyntheticKimodoBlendBuilder
from kimodo_pipeline.workflow_binding import (
    MATERIALISATION_BINDING_CONTRACT,
    canonical_json_sha256,
)


ENTRY = TOOLS / "kimodo-pipeline/commands/retarget_animations.py"


def run_processor(
    source: Path, output: Path, character_root: Path, *extra: str
) -> subprocess.CompletedProcess[str]:
    approval = character_root / "approved-baseline.json"
    baseline_args = ["--approved-character-baselines", approval.read_text()] if approval.is_file() else []
    return subprocess.run([
        "blender", "--background", "--factory-startup", "--python", str(ENTRY), "--",
        "--package", "locomotion", "--source", str(source), "--output-root", str(output),
        "--character-root", str(character_root),
        "--variant", FIXTURE_VARIANT, "--roles", "wf", *baseline_args, *extra,
    ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=180)


@pytest.mark.parametrize("mutation,diagnostic", [
    ("bpy.data.objects['Female'].pose.bones['Root'].constraints[0].mute=True", "mute"),
    ("bpy.context.view_layer.objects.active=bpy.data.objects['Kimodo_Source']; "
     "bpy.ops.object.mode_set(mode='EDIT'); "
     "bpy.data.objects['Kimodo_Source'].data.edit_bones['Root_RET'].roll=0.4; "
     "bpy.ops.object.mode_set(mode='OBJECT')", "rest"),
    ("bpy.data.objects['Kimodo_Source'].pose.bones['Root_RET'].keyframe_insert(data_path='location',frame=1)", "animated or driven"),
])
def test_locomotion_refuses_other_approved_calibration_drift(mutation, diagnostic):
    evidence = ROOT / "game/temp/wp6-b02-fix"
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="calibration-", dir=evidence) as directory:
        fixture = Path(directory)
        source = fixture / "source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        mutated = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr",
            "import bpy; " + mutation + "; bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=120)
        assert mutated.returncode == 0, mutated.stdout + mutated.stderr
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode != 0
        assert diagnostic in result.stderr
        assert not (fixture / "output").exists()


def test_locomotion_refuses_unapproved_setup_bytes_without_staging():
    evidence = ROOT / "game/temp/wp6-b02-fix"
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="setup-digest-", dir=evidence) as directory:
        fixture = Path(directory)
        source = fixture / "source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        setup = derive_artefact_paths(fixture, FIXTURE_VARIANT, ROOT).character_setup
        setup.write_bytes(setup.read_bytes() + b"unapproved setup bytes")
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode != 0
        assert hashlib.sha256(setup.read_bytes()).hexdigest() in result.stderr
        assert not (fixture / "output").exists()


def test_synthetic_bake_preserves_source_and_publishes_portable_package() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        before = hashlib.sha256(source.read_bytes()).hexdigest()
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode == 0, result.stdout + result.stderr
        assert hashlib.sha256(source.read_bytes()).hexdigest() == before
        package = fixture / "output/locomotion"
        index_path = package / FIXTURE_VARIANT / "processed-index.json"
        index = json.loads(index_path.read_text(encoding="utf-8"))
        assert index["variant"] == FIXTURE_VARIANT
        assert index["action_count"] == 1
        # The target skeleton and coordinate contract are recorded from the
        # resolved rig, not from any assumed character name.
        assert index["target_skeleton"] == "Female"
        assert index["coordinate_contract"]["source_armature"] == "Female"
        assert index["coordinate_contract"]["root_bone"] == "Root"
        assert index["setup_binding"]["baseline_character"] == "fixture_character"
        assert index["setup_binding"]["setup_parity"]["helper_capture_contract"] == "rest-and-unanimated-pose-basis-v2"
        assert index["setup_binding"]["source_sha256"] == before
        entry = index["actions"][0]
        assert entry["role"] == "wf"
        assert entry["source_action"].startswith(f"loc_{FIXTURE_VARIANT}_")
        assert entry["source_directional_event_evidence"]["contract"] == "anim002_source_directional_phase_events_v1"
        assert entry["event_derivation_evidence"]["contract"] == "anim002_event_derivation_v1"
        assert entry["post_repair_event_evidence"]["contract"] == "anim002_post_repair_directional_phase_events_v2"
        assert entry["post_repair_event_evidence"]["evaluation_stage"] == "final_repaired_evaluated_hierarchy"
        assert entry["loop_repair_evidence"]["blend_profile"] == "quadratic"
        assert entry["loop_repair_evidence"]["sample_semantics"]["unique_runtime_sample_count"] == 32
        assert not Path(index["source_path"]).is_absolute()
        assert (index_path.parent / Path(entry["metrics"])).is_file()
        assert (package / f"locomotion_{FIXTURE_VARIANT}.blend").is_file()


def test_target_rig_is_resolved_generically_from_workspace_content() -> None:
    """A differently named character rig bakes through the same processor.

    The target rig is resolved from the opened content (the armature whose Root
    binds onto Kimodo_Source), so no character name is required anywhere, and
    the processed index records the actual rig's name.
    """
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-generic-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        target_rig = "Reference_Male"
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source, target_rig_name=target_rig)
        assert built.returncode == 0, built.stdout + built.stderr
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode == 0, result.stdout + result.stderr
        index = json.loads(
            (fixture / "output/locomotion" / FIXTURE_VARIANT / "processed-index.json").read_text(
                encoding="utf-8"
            )
        )
        assert index["target_skeleton"] == target_rig
        assert index["setup_binding"]["setup_parity"]["target_rig"] == target_rig
        assert index["coordinate_contract"]["source_armature"] == target_rig
        metrics = json.loads(
            next(
                (fixture / "output/locomotion" / FIXTURE_VARIANT / "metrics").glob("*.metrics.json")
            ).read_text(encoding="utf-8")
        )
        assert metrics["target_skeleton"] == target_rig
        assert (fixture / "output/locomotion" / f"locomotion_{FIXTURE_VARIANT}.blend").is_file()


def test_character_root_is_required_and_cannot_default_to_a_character() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        result = subprocess.run([
            "blender", "--background", "--factory-startup", "--python", str(ENTRY), "--",
            "--package", "locomotion", "--variant", FIXTURE_VARIANT,
            "--source", str(fixture / "unused.blend"),
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=120)
        assert result.returncode != 0
        assert "--character-root" in result.stderr


def test_explicit_target_rig_name_is_validated_against_the_opened_content() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        for bogus, expected in (
            ("No_Such_Rig", 'names no armature named "No_Such_Rig"'),
            ("Kimodo_Source", "must name the character target rig"),
        ):
            result = run_processor(
                source, fixture / "output", fixture, "--target-rig", bogus,
            )
            assert result.returncode != 0, bogus
            assert expected in result.stderr
        assert not (fixture / "output/locomotion").exists()


def test_precision_reversal_roles_require_the_per_character_lateral_axis() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        result = subprocess.run([
            "blender", "--background", "--factory-startup", "--python", str(ENTRY), "--",
            "--package", "locomotion", "--source", str(source), "--output-root", str(fixture / "output"),
            "--character-root", str(fixture), "--variant", FIXTURE_VARIANT, "--roles", "sr,sl",
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=120)
        assert result.returncode != 0
        assert "--root-lateral-axis" in result.stderr
        assert not (fixture / "output/locomotion").exists()


def test_obsolete_metadata_is_rejected_without_replacing_a_fixture_package() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        action = f"loc_{FIXTURE_VARIANT}_wf_r_loop"
        mutation = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr",
            "import bpy; a=bpy.data.actions[" + repr(action) + "]; "
            "a['anim002_trailing_repair']=[1,2]; "
            "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=60)
        assert mutation.returncode == 0, mutation.stdout + mutation.stderr
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode != 0
        assert "obsolete loop-repair metadata" in result.stderr
        assert sentinel.read_bytes() == b"fixture previous package"


def test_missing_synthetic_source_preserves_a_fixture_package() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        result = run_processor(fixture / "missing.blend", fixture / "output", fixture)
        assert result.returncode != 0
        assert sentinel.read_bytes() == b"fixture previous package"


def test_retarget_binds_the_exact_materialised_carrier_and_rejects_later_mutation() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-binding-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        precursor = fixture / "immutable-precursor.blend"
        precursor.write_bytes(b"declared repair precursor")
        repair_report = fixture / "repair.json"
        repair_report.write_text(json.dumps({
            "output": str(precursor.resolve()),
            "after_sha256": hashlib.sha256(precursor.read_bytes()).hexdigest(),
        }), encoding="utf-8")
        source = fixture / "materialised.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        evidence = fixture / "selection.json"
        evidence.write_text("{}", encoding="utf-8")
        report = fixture / "materialisation.json"
        report.write_text(json.dumps({
            "contract": MATERIALISATION_BINDING_CONTRACT,
            "precursor": {"path": str(precursor), "sha256": hashlib.sha256(precursor.read_bytes()).hexdigest()},
            "final": {"path": str(source), "sha256": hashlib.sha256(source.read_bytes()).hexdigest()},
            "source_repair_report": {"path": str(repair_report), "sha256": hashlib.sha256(repair_report.read_bytes()).hexdigest()},
            "selection_evidence": {"path": str(evidence), "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest()},
            "action_provenance_sha256": canonical_json_sha256({"wf": "declared synthetic provenance"}),
            "roles": ["wf_r"],
        }), encoding="utf-8")

        accepted = run_processor(
            source, fixture / "accepted", fixture, "--materialisation-report", str(report),
        )
        assert accepted.returncode == 0, accepted.stdout + accepted.stderr
        index = json.loads((fixture / "accepted/locomotion" / FIXTURE_VARIANT / "processed-index.json").read_text(encoding="utf-8"))
        assert index["source_carrier_binding"]["source_sha256"] == hashlib.sha256(source.read_bytes()).hexdigest()
        assert index["source_carrier_binding"]["action_provenance_sha256"] == canonical_json_sha256(
            {"wf": "declared synthetic provenance"}
        )

        source.write_bytes(source.read_bytes() + b"later mutation")
        rejected = run_processor(
            source, fixture / "rejected", fixture, "--materialisation-report", str(report),
        )
        assert rejected.returncode != 0
        assert "report-bound final bytes changed" in rejected.stderr
        assert not (fixture / "rejected/locomotion").exists()


def test_incomplete_event_provenance_is_rejected_before_fixture_publication() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        action = f"loc_{FIXTURE_VARIANT}_wf_r_loop"
        mutation = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr",
            "import bpy; a=bpy.data.actions[" + repr(action) + "]; "
            "del a['anim002_event_derivation_evidence']; "
            "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)",
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=60)
        assert mutation.returncode == 0, mutation.stdout + mutation.stderr
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode != 0
        assert "stale or incomplete event provenance" in result.stderr
        assert sentinel.read_bytes() == b"fixture previous package"


def test_stale_post_repair_physical_contract_is_rejected_before_fixture_publication() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-retarget-", dir=ROOT / "game/temp") as directory:
        fixture = Path(directory)
        source = fixture / "fixture_source.blend"
        built = SyntheticKimodoBlendBuilder().build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        package = fixture / "output/locomotion"
        package.mkdir(parents=True)
        sentinel = package / "sentinel.bin"
        sentinel.write_bytes(b"fixture previous package")
        action = f"loc_{FIXTURE_VARIANT}_wf_r_loop"
        expression = (
            "import bpy,json; a=bpy.data.actions[" + repr(action) + "]; "
            "e=json.loads(a['anim002_post_repair_event_evidence']); "
            "e['contract']='anim002_post_repair_physical_events_v1'; "
            "a['anim002_post_repair_event_evidence']=json.dumps(e,sort_keys=True); "
            "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"
        )
        mutation = subprocess.run([
            "blender", "--background", "--factory-startup", str(source), "--python-expr", expression,
        ], cwd=ROOT, text=True, capture_output=True, check=False, timeout=60)
        assert mutation.returncode == 0, mutation.stdout + mutation.stderr
        result = run_processor(source, fixture / "output", fixture)
        assert result.returncode != 0
        assert "stale or incomplete event provenance" in result.stderr
        assert sentinel.read_bytes() == b"fixture previous package"
