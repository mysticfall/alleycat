#!/usr/bin/env python3
"""Launch the background-only RIG-002 axial declared candidate-set audit.

The audit is read-only: it measures the declared twist-only axial candidate
study set (see /tmp/opencode/rig-002-twist-only-axial-candidates.md) and records
study metadata alongside per-candidate metrics in the candidate evidence.
"""

from __future__ import annotations

import argparse
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
        default=REPO_ROOT / "game/temp/RIG-002/twist-only/candidate-evidence.json",
    )
    values = parser.parse_args()
    if not values.blender:
        parser.error("Blender was not found; pass --blender or add it to PATH")
    command = [
        values.blender,
        "--background",
        "--factory-startup",
        "--python-exit-code",
        "1",
        "--python",
        str(Path(__file__).with_name("optimise_forearm_twist.py")),
        "--",
        "--female",
        str(REPO_ROOT / "game/assets/characters/reference/female/reference_female.blend"),
        "--male",
        str(REPO_ROOT / "game/assets/characters/reference/male/reference_male.blend"),
        "--output",
        str(values.output),
    ]
    raise SystemExit(subprocess.run(command, cwd=REPO_ROOT, check=False).returncode)


if __name__ == "__main__":
    main()
