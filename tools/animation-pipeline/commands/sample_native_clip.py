#!/usr/bin/env python3
"""Blender entry point sampling a reference armature for an ANIM-003 native idle clip.

Normally run by ``export_idle_clip.py``; see the tool README.
"""
from __future__ import annotations

from pathlib import Path
import sys


def _pipeline_root() -> Path:
    for candidate in Path(__file__).resolve().parents:
        if (candidate / "kimodo_pipeline" / "__init__.py").is_file():
            return candidate
    raise RuntimeError("Kimodo pipeline package root could not be located.")


PIPELINE_ROOT = _pipeline_root()
if str(PIPELINE_ROOT) not in sys.path:
    sys.path.insert(0, str(PIPELINE_ROOT))

from kimodo_pipeline.native_clip_sampling import main


if __name__ == "__main__":
    raise SystemExit(main())
