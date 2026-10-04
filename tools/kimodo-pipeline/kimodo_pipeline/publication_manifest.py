"""Generate declared two-package publication manifests from prepared stages.

A prepared processed stage is the staged processed SET directory: the set-level
authoring blend plus exactly one variant metadata directory holding the
processed index, metrics, and provenance. A prepared runtime stage is the
variant-scoped ``runtime/<set>/<variant>`` package root.
"""
from __future__ import annotations

from collections.abc import Iterable
import json
from pathlib import Path
from typing import Any

from .bootstrap_paths import repository_root
from .runtime_package_descriptor import (
    PLANTED_INTERVALS_FILENAME,
    PUBLICATION_MODES,
    PackageDescriptorError,
    blend_filename,
    planted_interval_declaration,
    reject_additional_variants,
)


ROOT = repository_root()
GAME = ROOT / "game"
STITCH_RELEASE_INTENT = Path("provenance/stitch-release-intent-v1.json")
PLANTED_INTERVALS = Path("provenance") / PLANTED_INTERVALS_FILENAME


class PublicationManifestError(ValueError):
    """A prepared stage cannot supply a complete, declared publication plan."""


def write_manifest(
    stage_root: Path,
    processed_root: Path,
    runtime_root: Path,
    output: Path,
    legacy_roots: Iterable[Path],
) -> Path:
    """Write stage/final descriptors and a manifest without changing production roots."""
    stage_root = stage_root.resolve()
    processed_root = processed_root.resolve()
    runtime_root = runtime_root.resolve()
    plans = [
        _plan(name, PUBLICATION_MODES[name], stage_root, processed_root, runtime_root)
        for name in ("locomotion", "idle")
    ]
    document = {
        "schema_version": 1,
        "packages": plans,
        "legacy_roots": [str(path.resolve()) for path in legacy_roots],
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return output


def _plan(
    name: str,
    mode: str,
    stage_root: Path,
    processed_root: Path,
    runtime_root: Path,
) -> dict[str, str]:
    processed_stage = stage_root / "processed" / name
    variant = _single_variant(processed_stage)
    runtime_stage = stage_root / "runtime" / name / variant
    if not processed_stage.is_dir() or not runtime_stage.is_dir():
        raise PublicationManifestError(f"Prepared {name} stage is incomplete.")
    blend = _single_blend(processed_stage, name, variant)
    stitch_edges = load_stitch_release_intent(processed_stage / variant)
    staged_intervals = processed_stage / variant / PLANTED_INTERVALS
    if not staged_intervals.is_file():
        raise PublicationManifestError(f"Prepared {name} stage has no derived planted intervals: {staged_intervals}")
    final_processed = processed_root / name
    final_runtime = runtime_root / name / variant
    # Multi-variant publication is not ratified: refuse manifest preparation when
    # the production set already owns a different variant.
    try:
        reject_additional_variants(final_processed, runtime_root / name, variant)
    except PackageDescriptorError as error:
        raise PublicationManifestError(str(error)) from error
    final = _declaration(name, mode, variant, final_processed, final_runtime, blend.name, stitch_edges)
    packaging = _declaration(name, mode, variant, final_processed, final_runtime, blend.name, stitch_edges)
    packaging["index"] = _resource(processed_stage / variant / "processed-index.json")
    packaging["blend"] = _resource(blend)
    packaging["planted_intervals"]["input"] = _resource(staged_intervals)
    preflight = _declaration(name, mode, variant, final_processed, final_runtime, blend.name, stitch_edges)
    preflight["index"] = _resource(processed_stage / variant / "processed-index.json")
    preflight["blend"] = _resource(blend)
    preflight["root"] = _resource(runtime_stage)
    preflight["published_root"] = _resource(final_runtime)
    preflight["planted_intervals"]["input"] = _resource(staged_intervals)
    preflight_path = stage_root / f"{name}-preflight-descriptor.json"
    final_path = stage_root / f"{name}-final-descriptor.json"
    packaging_path = stage_root / f"{name}-packaging-descriptor.json"
    preflight_path.write_text(json.dumps(preflight, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    final_path.write_text(json.dumps(final, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    packaging_path.write_text(json.dumps(packaging, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return {
        "name": name,
        "mode": mode,
        "processed_stage": str(processed_stage),
        "runtime_stage": str(runtime_stage),
        "processed_root": str(final_processed),
        "runtime_root": str(final_runtime),
        "preflight_descriptor": str(preflight_path),
        "final_descriptor": str(final_path),
    }


def _single_variant(processed_stage: Path) -> str:
    variants = sorted(child.name for child in processed_stage.iterdir() if (child / "processed-index.json").is_file()) if processed_stage.is_dir() else []
    if len(variants) != 1:
        raise PublicationManifestError(
            f"Prepared processed stage must declare exactly one variant metadata directory: {processed_stage}"
        )
    return variants[0]


def _declaration(
    name: str,
    mode: str,
    variant: str,
    processed_root: Path,
    runtime_root: Path,
    blend_name: str,
    stitch_edges: list[dict[str, str]],
) -> dict[str, Any]:
    declaration: dict[str, Any] = {
        "name": name,
        "character": _character_identity(processed_root),
        "variant": variant,
        "publication_mode": mode,
        "index": _resource(processed_root / variant / "processed-index.json"),
        "published_index": _resource(processed_root / variant / "processed-index.json"),
        "blend": _resource(processed_root / blend_name),
        "root": _resource(runtime_root),
        "stitch_edges": stitch_edges,
    }
    retained = _resource(processed_root / variant / PLANTED_INTERVALS)
    declaration["planted_intervals"] = {"input": retained, **planted_interval_declaration(name, retained)}
    return declaration


def _character_identity(processed_root: Path) -> str:
    """Derive the character identity from the processed tree ownership.

    The character is the path segment that owns the ``animations`` tree;
    preceding segments (for example the ``reference`` grouping) are not part of
    the character identity.
    """
    relative = processed_root.relative_to(GAME)
    parts = relative.parts
    if len(parts) >= 4 and parts[0] == "assets" and parts[1] == "characters" and "animations" in parts[2:-1]:
        return parts[parts.index("animations") - 1]
    raise PublicationManifestError(
        f"Processed root must live under game/assets/characters/<character>/animations/processed: {processed_root}"
    )


def load_stitch_release_intent(processed_variant_stage: Path) -> list[dict[str, str]]:
    """Load package-owned stitch intent without assigning semantics to opaque role identifiers."""
    path = processed_variant_stage / STITCH_RELEASE_INTENT
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PublicationManifestError(f"Stitch release intent is missing or unreadable: {path}") from error
    if not isinstance(document, dict) or set(document) != {"schema_version", "stitch_edges"}:
        raise PublicationManifestError("Stitch release intent schema is invalid.")
    if document.get("schema_version") != 1 or not isinstance(document.get("stitch_edges"), list):
        raise PublicationManifestError("Stitch release intent schema is invalid.")

    result: list[dict[str, str]] = []
    seen: set[tuple[str, str]] = set()
    for value in document["stitch_edges"]:
        if not isinstance(value, dict) or set(value) != {"from_role", "to_role"}:
            raise PublicationManifestError("Stitch release intent edge schema is invalid.")
        source = value.get("from_role")
        destination = value.get("to_role")
        if (
            not isinstance(source, str)
            or not source
            or not isinstance(destination, str)
            or not destination
            or source == destination
            or (source, destination) in seen
        ):
            raise PublicationManifestError("Stitch release intent edges must be unique and connect distinct roles.")
        seen.add((source, destination))
        result.append({"from_role": source, "to_role": destination})
    return result


def _single_blend(processed_stage: Path, name: str, variant: str) -> Path:
    expected = processed_stage / blend_filename(name, variant)
    if not expected.is_file():
        raise PublicationManifestError(
            f"Prepared package must carry its set-level authoring blend: {expected}"
        )
    return expected


def _resource(path: Path) -> str:
    try:
        return f"res://{path.resolve().relative_to(GAME).as_posix()}"
    except ValueError as error:
        raise PublicationManifestError(f"Publication artefact must be inside game: {path}") from error
