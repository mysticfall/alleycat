#!/usr/bin/env python3
"""Validate generated body/clothing weights and retain the measured source-formula evidence."""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--blender", default=shutil.which("blender"))
    parser.add_argument(
        "--output",
        type=Path,
        default=REPO_ROOT / "game/temp/RIG-002/phase-2/generated-weight-validation.json",
    )
    values = parser.parse_args()
    if not values.blender:
        parser.error("Blender was not found; pass --blender or add it to PATH")
    completed = subprocess.run(
        [
            values.blender,
            "--background",
            "--python-exit-code",
            "1",
            "--python",
            str(REPO_ROOT / "tools/mpfb/validate_forearm_twist_weights.py"),
            "--",
            "female",
            str(REPO_ROOT / "game/assets/characters/reference/female/reference_female.blend"),
            "male",
            str(REPO_ROOT / "game/assets/characters/reference/male/reference_male.blend"),
        ],
        cwd=REPO_ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        print(completed.stdout, end="")
        print(completed.stderr, end="")
        raise SystemExit(completed.returncode)
    measurement_line = next(
        (line for line in reversed(completed.stdout.splitlines()) if line.startswith("{")),
        None,
    )
    if measurement_line is None:
        raise SystemExit("Generated-weight validator did not emit JSON measurements")
    measurements = json.loads(measurement_line)
    evidence = {
        "schema_version": 1,
        "producer": "tools/rigging/run_generated_forearm_weight_validation.py",
        "status": "passed",
        "assets": measurements,
    }
    values.output.parent.mkdir(parents=True, exist_ok=True)
    values.output.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(json.dumps({"evidence": str(values.output), "status": "passed"}, sort_keys=True))


if __name__ == "__main__":
    main()
