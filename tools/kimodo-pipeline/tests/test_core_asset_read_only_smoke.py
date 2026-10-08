"""Persistent structural and read-only validation for the Kimodo source inputs.

Covers the three repository-fixed bootstrap inputs and the reference female's
character-authored idle source (KIMODO-SRC-006): protected inputs the pipeline
must never mutate.
"""
from __future__ import annotations

import hashlib
import json
import tempfile
from pathlib import Path
from typing import Iterator

import pytest

from kimodo_pipeline.bootstrap_paths import derive_artefact_paths, derive_idle_source_path
from kimodo_pipeline.blender_cli import run_blender_python


ROOT = Path(__file__).resolve().parents[3]
TOOL_ROOT = ROOT / "tools/kimodo-pipeline"
CHARACTER_ROOT = ROOT / "game/assets/characters/reference/female"
BOOTSTRAP_PATHS = derive_artefact_paths(CHARACTER_ROOT, "sad", ROOT)
CANONICAL_SOURCE = BOOTSTRAP_PATHS.canonical_source
NEUTRAL_TEMPLATE = BOOTSTRAP_PATHS.neutral_template
MONOLITH = ROOT / "game/assets/characters/animations/source/locomotion/locomotion.blend"
IDLE_SOURCE = derive_idle_source_path(CHARACTER_ROOT, "sad")
MANIFEST_ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/manifest_bootstrap.py"
LFS_POINTER_HEADER = b"version https://git-lfs.github.com/spec/v1"
SOURCE_ROLES = (
    "canonical clean rig",
    "neutral locomotion template",
    "bootstrap monolith",
    "idle source",
)


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _assert_materialised_source(path: Path, role: str) -> None:
    assert path.is_file(), f"Required Kimodo {role} source does not exist: {path}"
    with path.open("rb") as handle:
        prefix = handle.read(128)
    assert prefix, f"Required Kimodo {role} source is empty: {path}"
    assert not prefix.startswith(LFS_POINTER_HEADER), (
        f"Required Kimodo {role} source is an unmaterialised Git LFS pointer: {path}"
    )


def _run_blender_read_only(path: Path, role: str, expression: str) -> None:
    _assert_materialised_source(path, role)
    before = _sha256(path)
    python_expression = (
        f"import bpy, sys; sys.path.insert(0, {str(TOOL_ROOT)!r}); "
        f"{expression}"
    )
    result = run_blender_python([path, "--python-expr", python_expression], ROOT)
    assert result.returncode == 0, result.stdout + result.stderr
    assert _sha256(path) == before, f"Read-only {role} preflight mutated {path}"


@pytest.fixture(scope="module")
def bootstrap_manifest() -> Iterator[tuple[Path, dict[str, object]]]:
    """Characterise the real monolith once, serially, without publishing or retaining output."""
    _assert_materialised_source(MONOLITH, "bootstrap monolith")
    before = _sha256(MONOLITH)
    with tempfile.TemporaryDirectory(prefix="kimodo-core-source-", dir=ROOT / "game/temp") as directory:
        result = run_blender_python([
            "--python", MANIFEST_ENTRYPOINT, "--", "--blend", MONOLITH,
            "--character-root", CHARACTER_ROOT, "--variant", "sad",
            "--repository-root", ROOT, "--temp-root", directory,
        ], ROOT)
        assert result.returncode == 0, result.stdout + result.stderr
        summary = json.loads(next(
            line.removeprefix("KIMODO_BOOTSTRAP_MANIFEST_JSON=")
            for line in result.stdout.splitlines()
            if line.startswith("KIMODO_BOOTSTRAP_MANIFEST_JSON=")
        ))
        manifest_path = Path(summary["manifest_path"])
        assert manifest_path.is_file(), (
            f"Bootstrap preflight did not produce its temporary manifest: {manifest_path}"
        )
        assert _sha256(MONOLITH) == before, (
            f"Read-only bootstrap manifest preflight mutated {MONOLITH}"
        )
        yield manifest_path, summary

    assert _sha256(MONOLITH) == before, f"Bootstrap manifest cleanup mutated {MONOLITH}"


def _bootstrap_validator_expression(manifest_path: Path, kind: str, baseline_key: str) -> str:
    validator = f"{kind}_failures"
    return (
        "import json; from pathlib import Path; "
        "from kimodo_pipeline.compatibility import ensure_registered; "
        "from kimodo_pipeline.bootstrap_extraction import _read_inventory; "
        f"from kimodo_pipeline.bootstrap_validation import expectations_from_manifest, {validator}; "
        "from kimodo_pipeline.character_baselines import APPROVED_CHARACTER_BASELINES; "
        "from kimodo_pipeline.bootstrap_workspace import _sha256; "
        f"manifest=json.loads(Path({str(manifest_path)!r}).read_text(encoding='utf-8')); "
        "expectations=expectations_from_manifest(manifest); ensure_registered(); "
        f"failures={validator}(_read_inventory({kind!r}), expectations); "
        "assert not failures, '; '.join(failures); "
        "assert _sha256(Path(bpy.data.filepath)) == "
        f"APPROVED_CHARACTER_BASELINES['female'][{baseline_key!r}]"
    )


def test_canonical_clean_source_passes_extraction_structure_and_approved_baseline(
    bootstrap_manifest: tuple[Path, dict[str, object]],
) -> None:
    manifest_path, _ = bootstrap_manifest
    _run_blender_read_only(
        CANONICAL_SOURCE,
        "canonical clean rig",
        _bootstrap_validator_expression(manifest_path, "canonical_source", "canonical_source"),
    )


def test_neutral_template_passes_workspace_structure_and_approved_baseline() -> None:
    expression = (
        "from pathlib import Path; "
        "from kimodo_pipeline.character_baselines import APPROVED_CHARACTER_BASELINES; "
        "from kimodo_pipeline.bootstrap_workspace import _read_template, _sha256; "
        "template_path=Path(bpy.data.filepath).resolve(); _read_template(template_path); "
        "assert _sha256(template_path) == "
        "APPROVED_CHARACTER_BASELINES['female']['neutral_template']"
    )
    _run_blender_read_only(
        NEUTRAL_TEMPLATE,
        "neutral locomotion template",
        expression,
    )


def test_bootstrap_monolith_passes_read_only_manifest_preflight(
    bootstrap_manifest: tuple[Path, dict[str, object]],
) -> None:
    _, summary = bootstrap_manifest
    assert Path(str(summary["source_blend"])).resolve() == MONOLITH.resolve()
    assert summary["source_blend_sha256"] == _sha256(MONOLITH)
    expression = (
        "from pathlib import Path; "
        "from kimodo_pipeline.bootstrap_extraction import default_blend_path; "
        "assert Path(bpy.data.filepath).resolve() == default_blend_path().resolve()"
    )
    _run_blender_read_only(MONOLITH, "bootstrap monolith", expression)


def test_character_idle_source_is_a_single_animated_action_on_the_calibrated_rig() -> None:
    expression = (
        "import bpy; "
        "from kimodo_pipeline.bootstrap_policy import inert_staging_objects; "
        "names = sorted(action.name for action in bpy.data.actions); "
        "assert len(names) == 1, f'the idle source must carry exactly one idle action: {names}'; "
        "span = bpy.data.actions[names[0]].frame_range; "
        "assert float(span[1]) - float(span[0]) >= 1.0, f'the idle action is not animated: {span}'; "
        "objects = sorted(obj.name for obj in bpy.data.objects); "
        "assert 'Kimodo_Source' in objects, 'the idle source lost the source rig'; "
        "assert not inert_staging_objects(objects), list(inert_staging_objects(objects))"
    )
    _run_blender_read_only(IDLE_SOURCE, "idle source", expression)


@pytest.mark.parametrize("role", SOURCE_ROLES)
def test_missing_required_source_reports_its_role_and_path(tmp_path: Path, role: str) -> None:
    missing = tmp_path / f"missing-{role.replace(' ', '-')}.blend"
    with pytest.raises(AssertionError, match=rf"{role} source does not exist: {missing}"):
        _assert_materialised_source(missing, role)


def test_unmaterialised_lfs_pointer_is_not_accepted_as_a_source(tmp_path: Path) -> None:
    pointer = tmp_path / "pointer.blend"
    pointer.write_bytes(LFS_POINTER_HEADER + b"\noid sha256:fixture\nsize 1\n")
    with pytest.raises(AssertionError, match="unmaterialised Git LFS pointer"):
        _assert_materialised_source(pointer, "fixture")
