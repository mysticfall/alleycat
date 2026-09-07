"""Capability-driven coverage shared by the three production Godot package tools."""
from __future__ import annotations

from contextlib import contextmanager
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
from typing import Any, Callable, Iterator

import pytest


from kimodo_pipeline.bootstrap_paths import repository_root


ROOT = repository_root()
GAME = ROOT / "game"
PROCESSED = GAME / "assets/characters/reference/female/animations/processed/locomotion"
VARIANT_SOURCE = PROCESSED / "sad"
GODOT_COMMANDS = ROOT / "tools/kimodo-pipeline/godot/commands"
CONFIGURE = GODOT_COMMANDS / "configure_kimodo_import.gd"
PACKAGE = GODOT_COMMANDS / "package_kimodo_runtime.gd"
VALIDATE = GODOT_COMMANDS / "validate_kimodo_runtime_package.gd"
FIXTURE_VARIANT = "sample"


def run_godot(script: Path, descriptor: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(script),
            "--", f"--descriptor=res://{descriptor.relative_to(GAME)}", *extra,
        ],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )


@contextmanager
def runtime_stage_root() -> Iterator[Path]:
    with tempfile.TemporaryDirectory(prefix="kimodo-capability-stage-", dir=GAME / "temp") as directory:
        yield Path(directory) / "runtime" / "capability_fixture" / FIXTURE_VARIANT


def import_project() -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--editor", "--quit"],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )


@pytest.fixture(scope="module")
def capability_package() -> Iterator[dict[str, Path]]:
    with tempfile.TemporaryDirectory(prefix="kimodo-capability-tools-", dir=GAME / "assets/testing") as directory:
        fixture = Path(directory)
        processed = fixture / "processed"
        variant_dir = processed / FIXTURE_VARIANT
        runtime = fixture / "runtime" / "capability_fixture" / FIXTURE_VARIANT
        shutil.copytree(VARIANT_SOURCE / "metrics", variant_dir / "metrics")
        shutil.copy2(PROCESSED / "locomotion_sad.blend", processed / f"capability_fixture_{FIXTURE_VARIANT}.blend")
        shutil.copy2(PROCESSED / "locomotion_sad.blend.import", processed / f"capability_fixture_{FIXTURE_VARIANT}.blend.import")

        index = json.loads((VARIANT_SOURCE / "processed-index.json").read_text(encoding="utf-8"))
        for position, entry in enumerate(index["actions"]):
            entry["runtime_role"] = f"slot_{position:02d}"
            entry["external_identity"] = f"ember_token_{position:02d}"
            if entry["runtime_capability"] == "cyclic_backward_primary":
                entry["runtime_capability"] = "idle_contact_free"
                for field in (
                    "source_directional_event_evidence", "event_derivation_evidence",
                    "post_repair_event_evidence", "loop_repair_evidence",
                ):
                    entry.pop(field, None)
        index["actions"].reverse()
        index_path = variant_dir / "processed-index.json"
        index_path.parent.mkdir(parents=True, exist_ok=True)
        index_path.write_text(json.dumps(index), encoding="utf-8")
        descriptor = fixture / "descriptor.json"
        descriptor.write_text(json.dumps({
            "name": "capability_fixture",
            "character": "fixture_character",
            "variant": FIXTURE_VARIANT,
            "index": f"res://{index_path.relative_to(GAME)}",
            "published_index": f"res://{index_path.relative_to(GAME)}",
            "blend": f"res://{(processed / f'capability_fixture_{FIXTURE_VARIANT}.blend').relative_to(GAME)}",
            "root": f"res://{runtime.relative_to(GAME)}",
            "contact_evidence_required": False,
        }), encoding="utf-8")

        configured = run_godot(CONFIGURE, descriptor)
        assert configured.returncode == 0, configured.stdout + configured.stderr
        imported = import_project()
        assert imported.returncode == 0, imported.stdout + imported.stderr
        with runtime_stage_root() as stage:
            packaged = run_godot(
                PACKAGE,
                descriptor,
                "--stage-root=res://" + str(stage.relative_to(GAME)),
            )
            assert packaged.returncode == 0, packaged.stdout + packaged.stderr
            shutil.move(stage, runtime)
        validated = run_godot(VALIDATE, descriptor)
        assert validated.returncode == 0, validated.stdout + validated.stderr
        yield {"fixture": fixture, "index": index_path, "descriptor": descriptor, "runtime": runtime}


def test_all_three_tools_accept_renamed_reordered_declared_capabilities(
    capability_package: dict[str, Path],
) -> None:
    index = json.loads(capability_package["index"].read_text(encoding="utf-8"))
    catalogue = json.loads(
        (capability_package["runtime"] / f"capability_fixture_{FIXTURE_VARIANT}_catalogue.json").read_text(encoding="utf-8"),
    )
    assert [entry["runtime_role"] for entry in index["actions"]] == [
        f"slot_{position:02d}" for position in reversed(range(22))
    ]
    clips = {clip["capability"]: clip for clip in catalogue["clips"]}
    assert clips["idle_contact_free"]["phase_events"]["applicability"] == "inapplicable"
    assert clips["idle_contact_free"]["boundary_kind"] == "confined_manoeuvre"
    assert clips["confined_side_step_left"]["boundary_kind"] == "confined_manoeuvre"
    assert clips["confined_pivot_right"]["boundary_kind"] == "confined_manoeuvre"
    assert clips["cyclic_forward_primary"]["boundary_kind"] == "cyclic_travel"
    assert clips["entry_forward_left"]["boundary_kind"] == "transition"
    assert clips["exit_backward_right"]["boundary_kind"] == "transition"
    assert all(clip["role"].startswith("slot_") for clip in catalogue["clips"])
    assert all(clip["identity"].startswith("ember_token_") for clip in catalogue["clips"])
    assert catalogue["schema_version"] == 3
    assert catalogue["animation_library"] == f"capability_fixture_{FIXTURE_VARIANT}.tres"


@pytest.mark.parametrize("tool", [CONFIGURE, PACKAGE, VALIDATE])
@pytest.mark.parametrize("mutation", [
    lambda entry: entry.pop("runtime_capability"),
    lambda entry: entry.__setitem__("runtime_capability", "unknown_opaque_capability"),
    lambda entry: entry.__setitem__("runtime_capability", "idle_contact_free"),
])
def test_all_three_tools_refuse_missing_unknown_or_incompatible_capabilities(
    capability_package: dict[str, Path],
    tool: Path,
    mutation: Callable[[dict[str, Any]], object],
) -> None:
    index_path = capability_package["index"]
    original = index_path.read_bytes()
    try:
        index = json.loads(original)
        target = next(entry for entry in index["actions"] if entry["runtime_capability"] == "cyclic_forward_primary")
        mutation(target)
        index_path.write_text(json.dumps(index), encoding="utf-8")
        if tool == PACKAGE:
            with runtime_stage_root() as stage:
                result = run_godot(
                    tool,
                    capability_package["descriptor"],
                    "--stage-root=res://" + str(stage.relative_to(GAME)),
                )
        else:
            result = run_godot(tool, capability_package["descriptor"])
        assert result.returncode != 0
        assert "capability" in (result.stdout + result.stderr).lower()
    finally:
        index_path.write_bytes(original)
