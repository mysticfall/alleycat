#!/usr/bin/env python3
"""Blender entry point for the read-only forearm-twist audit."""

from __future__ import annotations

import sys
from pathlib import Path


def character_generator_root() -> Path:
    """Locate the tool root from this command without relying on a fixed depth."""

    for candidate in Path(__file__).resolve().parents:
        if (candidate / "character_generator").is_dir():
            return candidate

    raise RuntimeError("Could not locate the character-generator import package.")


sys.path.insert(0, str(character_generator_root()))

from character_generator.rigging.audit_forearm_twist import main


if __name__ == "__main__":
    raise SystemExit(main())
