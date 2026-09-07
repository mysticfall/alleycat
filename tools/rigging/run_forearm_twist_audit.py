#!/usr/bin/env python3
"""Launch the RIG-002 Blender audit against both checked-in generated assets."""

from __future__ import annotations

import argparse
import shutil
import subprocess
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = REPO_ROOT / "game" / "temp" / "RIG-002" / "phase-1" / "blender-evidence.json"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--blender", default=shutil.which("blender"))
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    arguments = parser.parse_args()
    if not arguments.blender:
        parser.error("Blender was not found; pass --blender or add it to PATH")

    command = [
        arguments.blender,
        "--background",
        "--factory-startup",
        "--python-exit-code",
        "1",
        "--python",
        str(Path(__file__).with_name("audit_forearm_twist.py")),
        "--",
        "--female",
        str(REPO_ROOT / "game/assets/characters/reference/female/reference_female.blend"),
        "--male",
        str(REPO_ROOT / "game/assets/characters/reference/male/reference_male.blend"),
        "--output",
        str(arguments.output),
    ]
    completed = subprocess.run(command, cwd=REPO_ROOT, check=False)
    if completed.returncode != 0:
        raise SystemExit(completed.returncode)


if __name__ == "__main__":
    main()
