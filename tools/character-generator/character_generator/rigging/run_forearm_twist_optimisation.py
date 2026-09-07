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


def repository_root() -> Path:
    """Locate the repository root without assuming an implementation-file depth."""

    for candidate in Path(__file__).resolve().parents:
        if (candidate / ".git").exists():
            return candidate

    raise RuntimeError("Could not locate the repository root from the character-generator tool.")


REPO_ROOT = repository_root()


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
        str(REPO_ROOT / "tools/character-generator/commands/optimise_forearm_twist.py"),
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
