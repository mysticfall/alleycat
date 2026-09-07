"""Synthetic Blender discovery smoke coverage."""
from __future__ import annotations

import json
from pathlib import Path
import tempfile
import unittest

from synthetic_kimodo_blend import FIXTURE_VARIANT, MANIFEST, RIG_NAME, SyntheticKimodoBlendBuilder


ROOT = Path(__file__).resolve().parents[3]
ENTRYPOINT = ROOT / "tools/kimodo-pipeline/kimodo_pipeline/discover_source.py"


class BlenderDiscoverySmokeTests(unittest.TestCase):
    def test_discovers_the_declared_fixture_contract_without_writing(self) -> None:
        with tempfile.TemporaryDirectory(prefix="kimodo-discovery-") as directory:
            blend = Path(directory) / "fixture.blend"
            built = SyntheticKimodoBlendBuilder().build(blend)
            self.assertEqual(built.returncode, 0, built.stdout + built.stderr)
            before = blend.read_bytes()
            from kimodo_pipeline.blender_cli import run_blender_python

            result = run_blender_python([
                "--python", ENTRYPOINT, "--", "--blend", blend, "--variant", FIXTURE_VARIANT,
            ], ROOT)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            report = json.loads(next(
                line.removeprefix("KIMODO_DISCOVERY_JSON=")
                for line in result.stdout.splitlines()
                if line.startswith("KIMODO_DISCOVERY_JSON=")
            ))
            self.assertEqual(report["variant"], FIXTURE_VARIANT)
            self.assertEqual(report["source_rig"], RIG_NAME)
            self.assertEqual(set(report["source_actions"]), {item.name for item in MANIFEST.actions})
            self.assertEqual([item["index"] for item in report["segments"]], list(range(len(MANIFEST.segments))))
            self.assertEqual(blend.read_bytes(), before)
