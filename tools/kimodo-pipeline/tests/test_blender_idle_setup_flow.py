"""Synthetic Blender coverage for the idle bake through the setup-parity gate."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile

import pytest

from kimodo_pipeline.bootstrap_paths import derive_idle_source_path
from kimodo_pipeline.character_baselines import APPROVED_CHARACTER_BASELINES
from test_blender_idle_source_bootstrap import DONOR_ACTION, _arguments as staging_arguments


ROOT = Path(__file__).resolve().parents[3]
ENTRY = ROOT / "tools/kimodo-pipeline/commands/retarget_animations.py"


def _sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


@pytest.fixture(scope="module")
def fixture() -> dict[str, Path]:
    """One extraction fixture with a staged idle source ready for baking."""
    with tempfile.TemporaryDirectory(prefix="kimodo-idle-flow-", dir=ROOT / "game/temp") as directory:
        work = Path(directory)
        built = _extraction_fixture(work)
        from kimodo_pipeline.blender_cli import run_blender_python

        staged = run_blender_python(staging_arguments(built, "ember"), ROOT)
        if staged.returncode != 0:
            raise RuntimeError(staged.stdout + staged.stderr)
        built["idle_source"] = derive_idle_source_path(built["character"], "ember")
        # The bake derives repository-fixed inputs from the real repository
        # root, so the disposable baseline pairs the approved repository
        # digests with this fixture's setup bytes.
        built["bake_baselines"] = json.dumps({
            "fixture_character": {
                "canonical_source": APPROVED_CHARACTER_BASELINES["female"]["canonical_source"],
                "neutral_template": APPROVED_CHARACTER_BASELINES["female"]["neutral_template"],
                "character_setup": _sha256(built["setup"]),
            }
        }, sort_keys=True)
        yield built


def _extraction_fixture(work: Path) -> dict[str, Path]:
    from kimodo_pipeline.blender_cli import run_blender_python
    from kimodo_pipeline.bootstrap_paths import derive_artefact_paths
    from synthetic_kimodo_blend import FIXTURE_VARIANT, SyntheticKimodoBlendBuilder
    from test_blender_bootstrap_extraction_smoke import _fixture, _manifest
    from test_blender_bootstrap_extraction_smoke import _arguments as extraction_arguments

    built = _fixture(work)
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
    return built


def _bake(
    fixture: dict[str, Path], output: Path, *extra: str, character_root: Path | None = None,
) -> object:
    from kimodo_pipeline.blender_cli import run_blender_python

    return run_blender_python([
        "--python", ENTRY, "--",
        "--package", "idle", "--variant", "ember",
        "--character-root", character_root if character_root is not None else fixture["character"],
        "--idle-source-action", DONOR_ACTION,
        "--output-root", str(output),
        "--approved-character-baselines", fixture["bake_baselines"],
        *extra,
    ], ROOT)


def test_idle_bake_publishes_through_the_parity_gate_with_idle_source_provenance(
    fixture: dict[str, Path],
) -> None:
    output = fixture["root"] / "bake-output"
    result = _bake(fixture, output)
    assert result.returncode == 0, result.stdout + result.stderr
    index_path = output / "idle" / "ember" / "processed-index.json"
    index = json.loads(index_path.read_text(encoding="utf-8"))
    relative_idle_source = fixture["idle_source"].relative_to(ROOT).as_posix()
    # Provenance names the per-character idle source path and action.
    assert index["source_path"] == relative_idle_source
    binding = index["idle_source_binding"]
    assert binding["idle_source_path"] == relative_idle_source
    assert binding["idle_source_action"] == DONOR_ACTION
    assert binding["idle_source_sha256"] == _sha256(fixture["idle_source"])
    assert binding["setup_baseline_character"] == "fixture_character"
    assert binding["setup_sha256"] == _sha256(fixture["setup"])
    fingerprints = binding["setup_parity_fingerprints"]
    assert fingerprints["target_rig"] == "Female"
    for field in (
        "target_constraint_graph_sha256",
        "source_constraint_graph_sha256",
        "source_helper_configuration_sha256",
    ):
        assert len(fingerprints[field]) == 64
    assert index["target_skeleton"] == "Female"
    assert index["action_count"] == 1
    assert index["actions"][0]["role"] == "idle"
    assert (output / "idle" / "idle_ember.blend").is_file()
    # The idle source itself is never written by the bake.
    assert _sha256(fixture["idle_source"]) == binding["idle_source_sha256"]


def test_the_baked_idle_clip_retains_the_source_motion(fixture: dict[str, Path]) -> None:
    """Regression (static-bake defect): the idle source action must drive the rig.

    The real idle donor action carries a history-named slot ("OBKimodo" — its
    authoring armature was renamed to Kimodo_Source afterwards). Blender 5.x
    does not auto-bind such a slot when the bake assigns
    ``animation_data.action``, the action animates nothing, every frame
    evaluates the same frozen pose, and the bake emits a single-pose clip with a
    full-length transport. The bake must bind the action slot itself, and the
    baked output must carry the donor action's frame-varying motion.
    """
    from kimodo_pipeline.blender_cli import run_blender_python

    # Shape the staged idle source to the real donor's condition: a slot
    # identifier that no longer matches the source armature's name.
    stale_slot = (
        "import bpy; "
        "action = bpy.data.actions[{action!r}]; "
        "action.slots[0].identifier = 'OBLegacyAuthoringRig'; "
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath, check_existing=False)"
    ).format(action=DONOR_ACTION)
    reshaped = run_blender_python([fixture["idle_source"], "--python-expr", stale_slot], ROOT)
    assert reshaped.returncode == 0, reshaped.stdout + reshaped.stderr

    output = fixture["root"] / "animated-bake"
    result = _bake(fixture, output)
    assert result.returncode == 0, result.stdout + result.stderr
    probe = (
        "import bpy, json; "
        "bpy.ops.wm.open_mainfile(filepath={blend!r}); "
        "action = bpy.data.actions[{action!r}]; "
        "curves = ( "
        "    list(action.fcurves) if getattr(action, 'fcurves', None) else "
        "    [c for layer in action.layers for strip in layer.strips "
        "     for bag in strip.channelbags for c in bag.fcurves] "
        "); "
        "varying = [ "
        "    {{'keys': len(c.keyframe_points), 'extent': "
        "      max(p.co.y for p in c.keyframe_points) "
        "        - min(p.co.y for p in c.keyframe_points)}} "
        "    for c in curves if c.keyframe_points and "
        "      max(p.co.y for p in c.keyframe_points) "
        "        - min(p.co.y for p in c.keyframe_points) > 1e-4]; "
        "print('BOUNDED_MOTION=' + json.dumps({{ "
        "    'curve_count': len(curves), 'varying': varying }}))"
    ).format(blend=str(output / "idle" / "idle_ember.blend"), action="idle_ember_loop")
    probed = run_blender_python([output / "idle" / "idle_ember.blend", "--python-expr", probe], ROOT)
    assert probed.returncode == 0, probed.stdout + probed.stderr
    motion = json.loads(next(
        line for line in probed.stdout.splitlines() if line.startswith("BOUNDED_MOTION=")
    ).partition("=")[2])
    assert motion["curve_count"] > 2, motion
    # A frozen bake keys one constant value per curve: every curve varies only
    # across bones, never across frames (varying == []). The synthetic donor
    # ramps a single heading channel through the retarget constraints, so an
    # animated bake shows at least that one curve varying across the window.
    assert motion["varying"], motion
    assert all(entry["keys"] >= 10 for entry in motion["varying"]), motion


def test_an_un_calibrated_idle_source_is_refused_before_publication(
    fixture: dict[str, Path],
) -> None:
    """The diagnosed defect class: helper roll reset and Locked-Track un-muting."""
    character_root = fixture["root"] / "uncalibrated-character"
    shutil.copytree(fixture["character"], character_root)
    idle_source = derive_idle_source_path(character_root, "ember")
    mutation = (
        "import bpy; src=bpy.data.objects['Kimodo_Source']; "
        "bpy.context.view_layer.objects.active=src; src.select_set(True); "
        "bpy.ops.object.mode_set(mode='EDIT'); "
        "src.data.edit_bones['Root_RET'].roll=0.6; "
        "bpy.ops.object.mode_set(mode='OBJECT'); "
        "tgt=bpy.data.objects['Female']; "
        "next(c for c in tgt.pose.bones['Root'].constraints "
        "if c.type=='COPY_LOCATION').mute=True; "
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"
    )
    from kimodo_pipeline.blender_cli import run_blender_python

    mutated = run_blender_python([idle_source, "--python-expr", mutation], ROOT)
    assert mutated.returncode == 0, mutated.stdout + mutated.stderr
    output = fixture["root"] / "uncalibrated-output"
    result = _bake(fixture, output, character_root=character_root)
    assert result.returncode != 0
    assert "Idle setup-parity gate refused the bake" in result.stderr
    assert "Root_RET" in result.stderr
    assert "rest basis/roll state" in result.stderr
    assert "COPY_LOCATION" in result.stderr
    assert "mute state differs" in result.stderr
    assert "calibration drift" in result.stderr
    assert not output.exists()


def test_a_drifted_character_setup_is_refused_by_the_baseline_digest(
    fixture: dict[str, Path],
) -> None:
    from kimodo_pipeline.bootstrap_paths import derive_artefact_paths

    character_root = fixture["root"] / "drifted-character"
    shutil.copytree(fixture["character"], character_root)
    drifted_setup = derive_artefact_paths(character_root, "ember").character_setup
    mutation = (
        "import bpy; bpy.context.scene.frame_start=42; "
        "bpy.ops.wm.save_as_mainfile(filepath=bpy.data.filepath,check_existing=False)"
    )
    from kimodo_pipeline.blender_cli import run_blender_python

    drifted = run_blender_python([drifted_setup, "--python-expr", mutation], ROOT)
    assert drifted.returncode == 0, drifted.stdout + drifted.stderr
    output = fixture["root"] / "drifted-output"
    result = _bake(fixture, output, character_root=character_root)
    assert result.returncode != 0
    assert "Idle setup-parity gate" in result.stderr
    assert "match no approved character baseline" in result.stderr
    assert "fixture_character" in result.stderr
    assert not output.exists()


def test_the_idle_action_is_an_explicit_input_of_the_default_flow() -> None:
    """No silent default action name: the per-character flow refuses to guess."""
    from kimodo_pipeline.blender_cli import run_blender_python

    result = run_blender_python([
        "--python", ENTRY, "--", "--package", "idle", "--variant", "ember",
        "--character-root", ROOT / "game/assets/characters/reference/female",
    ], ROOT)
    assert result.returncode != 0
    assert "--idle-source-action is required" in result.stderr


def test_a_missing_per_character_idle_source_fails_with_its_derived_path(
    fixture: dict[str, Path],
) -> None:
    from kimodo_pipeline.blender_cli import run_blender_python

    absent = fixture["root"] / "empty-character"
    absent.mkdir()
    result = run_blender_python([
        "--python", ENTRY, "--", "--package", "idle", "--variant", "absent",
        "--character-root", absent, "--idle-source-action", DONOR_ACTION,
        "--output-root", str(fixture["root"] / "absent-output"),
        "--approved-character-baselines", fixture["bake_baselines"],
    ], ROOT)
    assert result.returncode != 0
    assert "Source blend does not exist" in result.stderr
    assert "animations/source/idle/absent.blend" in result.stderr


def test_the_idle_package_refuses_explicit_source_overrides(fixture: dict[str, Path]) -> None:
    """Only the character's per-character idle source serves the idle package."""
    from kimodo_pipeline.blender_cli import run_blender_python

    refused = run_blender_python([
        "--python", ENTRY, "--", "--package", "idle", "--variant", "ember",
        "--character-root", fixture["character"],
        "--source", fixture["donor"], "--idle-source-action", DONOR_ACTION,
        "--output-root", str(fixture["root"] / "override-output"),
    ], ROOT)
    assert refused.returncode != 0
    # The refusal is an input-contract error naming the per-character flow.
    assert "--source cannot serve the idle package" in refused.stderr
    assert "animations/source/idle/<variant>.blend" in refused.stderr
    assert not (fixture["root"] / "override-output").exists()
