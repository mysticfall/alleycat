#!/usr/bin/env python3
"""Blender command wrapper for Kimodo locomotion contact-evidence generation."""
from __future__ import annotations

import json
from pathlib import Path
import sys


def _pipeline_root() -> Path:
    for candidate in Path(__file__).resolve().parents:
        if (candidate / "kimodo_pipeline").is_dir() and (candidate / "config").is_dir():
            return candidate
    raise RuntimeError("Kimodo pipeline package root could not be located.")


PIPELINE_ROOT = _pipeline_root()
if str(PIPELINE_ROOT) not in sys.path:
    sys.path.insert(0, str(PIPELINE_ROOT))

from kimodo_pipeline.contact_evidence import ContactEvidenceError, main


if __name__ == "__main__":
    try:
        main()
    except ContactEvidenceError as error:
        print("KIMODO_CONTACT_EVIDENCE_REFUSED=" + json.dumps({"reason": str(error)}, sort_keys=True))
        raise SystemExit(2)
