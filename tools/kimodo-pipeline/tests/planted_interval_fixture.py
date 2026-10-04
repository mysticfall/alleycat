"""Synthetic, structurally valid planted-interval documents for package-mechanics fixtures.

Package mechanics tests (layout, transactions, catalogue schema) need a planted-interval document bound to the
staged runtime library and the repository role policy. These documents declare both feet planted throughout
every clip; they exercise the publication contract, not interval derivation, which the Godot derivation test
covers with scripted foot motion.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import subprocess
from typing import Any

from kimodo_pipeline.bootstrap_paths import repository_root
from kimodo_pipeline.runtime_package_descriptor import PLANTED_INTERVALS_FILENAME, runtime_library_sha256


ROOT = repository_root()
GAME = ROOT / "game"
POLICY = ROOT / "tools/kimodo-pipeline/config/planted_interval_role_policy_v1.json"
PACKAGE = ROOT / "tools/kimodo-pipeline/godot/commands/package_kimodo_runtime.gd"


def stage_runtime_library(descriptor: Path, stage: Path) -> Path:
    """Stage only the runtime library a packaging descriptor would publish, and return its path."""
    result = subprocess.run(
        [
            "godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(PACKAGE),
            "--", f"--descriptor=res://{descriptor.relative_to(GAME)}",
            f"--stage-root=res://{stage.relative_to(GAME)}", "--stage-library-only",
        ],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "ANIM002_RUNTIME_LIBRARY_STAGED=" in result.stdout, result.stdout
    libraries = list(stage.glob("*.tres"))
    assert len(libraries) == 1, libraries
    return libraries[0]


def planted_intervals(
    *,
    character: str,
    package: str,
    variant: str,
    index: dict[str, Any],
    library: Path,
) -> dict[str, Any]:
    """Return a both-feet-planted document covering every processed-index action."""
    policy = json.loads(POLICY.read_text(encoding="utf-8"))
    clips = []
    for entry in index["actions"]:
        # Six decimals keep the document stable under Godot's JSON round trip and inside the 1e-6 s duration match.
        duration = round(float(entry["transport"]["duration_seconds"]), 6)
        foot = {
            "baseline_ball_height_m": 0.0,
            "segments": [{"state": "planted", "start_seconds": 0.0, "end_seconds": duration}],
        }
        clips.append({
            "identity": entry["external_identity"],
            "role": entry.get("runtime_role", entry["role"]),
            "capability": entry["runtime_capability"],
            "looping": bool(entry["is_loop"]),
            "duration_seconds": duration,
            "feet": {"left": foot, "right": dict(foot)},
        })
    return {
        "schema_version": 1,
        "contract": "anim002_per_foot_planted_intervals_v1",
        "character": character,
        "package": package,
        "variant": variant,
        "derivation": {
            "tool": "derive_planted_intervals",
            "tool_version": 3,
            "policy": {"identity": policy["identity"], "sha256": hashlib.sha256(POLICY.read_bytes()).hexdigest()},
            "ball_joints": policy["ball_joints"],
            "ball_point": "synthetic_fixture",
            "baseline": {"method": "synthetic_fixture"},
            "sampling_rate_hz": policy["sampling_rate_hz"],
            "detection": policy["detection"],
            "structure_fit": policy["structure_fit"],
            "verification": policy["verification"],
            "skeleton": {"scene": "synthetic_fixture"},
            "runtime_library_sha256": runtime_library_sha256(library),
        },
        "clips": sorted(clips, key=lambda clip: clip["identity"]),
    }


def write_planted_intervals(directory: Path, document: dict[str, Any]) -> Path:
    """Write a planted-interval document as ``per_foot_planted_intervals_v1.json`` under ``directory``."""
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / PLANTED_INTERVALS_FILENAME
    path.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return path
