#!/usr/bin/env python3
"""Install the locked Blender validation dependencies outside source control."""
from __future__ import annotations

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


TOOL_ROOT = Path(__file__).resolve().parent
REQUIREMENTS = TOOL_ROOT / "requirements-blender.txt"
DEFAULT_TARGET = TOOL_ROOT / ".blender-python-deps"
DEPENDENCY_DIRECTORY_ENVIRONMENT_VARIABLE = "KIMODO_BLENDER_PYTHON_DEPS"


def dependency_directory() -> Path:
    """Return the configured local package directory without resolving a missing path."""
    configured = os.environ.get(DEPENDENCY_DIRECTORY_ENVIRONMENT_VARIABLE)
    return Path(configured).expanduser() if configured else DEFAULT_TARGET


def main() -> None:
    """Rebuild the local, hash-locked dependency directory atomically."""
    target = dependency_directory()
    target.parent.mkdir(parents=True, exist_ok=True)

    with tempfile.TemporaryDirectory(prefix=".blender-python-deps-", dir=target.parent) as temporary_directory:
        staging = Path(temporary_directory) / "site-packages"
        command = [
            sys.executable,
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--no-compile",
            "--require-hashes",
            "--target",
            str(staging),
            "--requirement",
            str(REQUIREMENTS),
        ]
        subprocess.run(command, check=True)

        if target.exists():
            shutil.rmtree(target)
        os.replace(staging, target)

    print(f"Installed Blender validation dependencies in {target}")


if __name__ == "__main__":
    main()
