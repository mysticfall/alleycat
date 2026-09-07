"""Structural transport fidelity between package-owned stitch intent and published catalogues.

The stitch release intent is the sole authored source of role-to-role stitch
edges; the packager must transport exactly that edge set into the published
catalogue's per-clip stitch declarations, and the durable descriptor must carry
the same set. These are structural provenance checks over production inputs —
they assert nothing about motion behaviour and invent no role identifiers.
"""
from __future__ import annotations

import json
from pathlib import Path

from kimodo_pipeline.publication_manifest import load_stitch_release_intent
from kimodo_pipeline.runtime_package_descriptor import catalogue_filename


ROOT = Path(__file__).resolve().parents[3]
ANIMATIONS = ROOT / "game/assets/characters/reference/female/animations"


def _single_variant(processed_set: Path) -> str:
    variants = sorted(
        child.name for child in processed_set.iterdir() if (child / "processed-index.json").is_file()
    )
    assert len(variants) == 1, f"Expected exactly one processed variant directory: {processed_set}"
    return variants[0]


def _declared_catalogue_edges(catalogue_path: Path) -> set[tuple[str, str]]:
    document = json.loads(catalogue_path.read_text(encoding="utf-8"))
    declared: set[tuple[str, str]] = set()
    for clip in document["clips"]:
        for mapping in clip.get("stitch_mappings", {}).get("mappings", []):
            declared.add((mapping["from"]["role"], mapping["to"]["role"]))
    return declared


def test_locomotion_intent_descriptor_and_published_catalogue_transport_the_same_edges() -> None:
    processed_set = ANIMATIONS / "processed/locomotion"
    variant = _single_variant(processed_set)
    intent = load_stitch_release_intent(processed_set / variant)
    edges = {(edge["from_role"], edge["to_role"]) for edge in intent}
    assert edges, "The locomotion package must declare at least one stitch edge."

    descriptor = json.loads(
        (processed_set / variant / "provenance" / "runtime-package-descriptor-v1.json").read_text(encoding="utf-8")
    )
    assert descriptor["stitch_edges"] == intent

    catalogue = ANIMATIONS / "runtime/locomotion" / variant / catalogue_filename("locomotion", variant)
    assert _declared_catalogue_edges(catalogue) == edges


def test_idle_intent_stays_empty_and_transports_no_catalogue_edges() -> None:
    processed_set = ANIMATIONS / "processed/idle"
    variant = _single_variant(processed_set)
    assert load_stitch_release_intent(processed_set / variant) == []

    catalogue = ANIMATIONS / "runtime/idle" / variant / catalogue_filename("idle", variant)
    assert _declared_catalogue_edges(catalogue) == set()
