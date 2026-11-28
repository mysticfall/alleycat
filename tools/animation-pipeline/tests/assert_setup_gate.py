"""Run the calibrated-setup gate on a synthetic fixture inside Blender.

Usage: blender --python assert_setup_gate.py -- <character_root> <source.blend> <baselines.json> [target_rig]

Prints ``SETUP_GATE_JSON=`` with the gate result; a refusal propagates as an
unhandled ``SetupError`` so Blender exits non-zero with its diagnostic.
"""
from __future__ import annotations

import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[3]
sys.path[:0] = [str(ROOT / "tools/animation-pipeline"), str(Path(__file__).resolve().parent)]

from kimodo_pipeline.bootstrap_paths import derive_artefact_paths  # noqa: E402
from kimodo_pipeline.calibrated_setup import verify_setup_parity  # noqa: E402
from synthetic_kimodo_blend import FIXTURE_VARIANT  # noqa: E402


def main() -> None:
    arguments = sys.argv[sys.argv.index("--") + 1:]
    character_root, source, baselines = (Path(value) for value in arguments[:3])
    target_rig = arguments[3] if len(arguments) > 3 else None
    result = verify_setup_parity(
        derive_artefact_paths(character_root, FIXTURE_VARIANT, ROOT),
        source,
        target_rig,
        json.loads(baselines.read_text(encoding="utf-8")),
    )
    print("SETUP_GATE_JSON=" + json.dumps(result, sort_keys=True))


main()
