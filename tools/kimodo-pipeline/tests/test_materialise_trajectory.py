"""Trajectory fixture assertions derive exclusively from the synthetic contract."""
from __future__ import annotations

import json
from pathlib import Path
import tempfile

from kimodo_pipeline.blender_cli import run_blender_python

from synthetic_kimodo_blend import MANIFEST, SyntheticKimodoBlendBuilder


ROOT = Path(__file__).resolve().parents[3]


def test_synthetic_trajectory_tracks_match_the_declared_contract() -> None:
    with tempfile.TemporaryDirectory(prefix="kimodo-trajectory-") as directory:
        blend = Path(directory) / "fixture.blend"
        built = SyntheticKimodoBlendBuilder().build(blend)
        assert built.returncode == 0, built.stdout + built.stderr
        expression = (
            "import bpy,json; "
            "print('KIMODO_SYNTHETIC_TRAJECTORY='+bpy.context.scene['synthetic_kimodo_manifest'])"
        )
        inspected = run_blender_python([blend, "--python-expr", expression], ROOT)
        assert inspected.returncode == 0, inspected.stdout + inspected.stderr
        observed = json.loads(next(
            row.removeprefix("KIMODO_SYNTHETIC_TRAJECTORY=")
            for row in inspected.stdout.splitlines()
            if row.startswith("KIMODO_SYNTHETIC_TRAJECTORY=")
        ))
        assert observed == json.loads(MANIFEST.to_json())


def test_synthetic_fixture_declares_forward_lateral_arc_and_reversal_root_tracks() -> None:
    by_kind = {action.kind: action for action in MANIFEST.actions}
    assert by_kind["loop"].root_track[1][2] > 0
    assert by_kind["lateral"].root_track[1][0] > 0
    assert by_kind["arc"].root_track[1][0] > 0 and by_kind["arc"].root_track[1][2] > 0
    assert by_kind["reversal_contact"].root_track[1][0] < 0
