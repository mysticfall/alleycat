"""Narrow readability and immutability smoke tests for volatile core blends."""
from __future__ import annotations

import hashlib
from pathlib import Path

import pytest

from kimodo_pipeline.blender_cli import run_blender_python


ROOT = Path(__file__).resolve().parents[3]
CORE_ASSETS = (
    ROOT / "game/assets/characters/animations/source/locomotion/locomotion.blend",
    ROOT / "game/assets/characters/animations/source/locomotion/locomotion_template.blend",
    ROOT / "game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend",
)


@pytest.mark.parametrize("asset", CORE_ASSETS, ids=lambda path: path.name)
def test_core_asset_is_readable_without_mutation(asset: Path) -> None:
    """Do not derive authored actions, frames, poses, variants, or inventory here."""
    before = hashlib.sha256(asset.read_bytes()).hexdigest()
    result = run_blender_python([
        asset,
        "--python-expr",
        f"import bpy; assert bpy.data.filepath == {str(asset)!r}",
    ], ROOT)
    assert result.returncode == 0, result.stdout + result.stderr
    assert hashlib.sha256(asset.read_bytes()).hexdigest() == before
