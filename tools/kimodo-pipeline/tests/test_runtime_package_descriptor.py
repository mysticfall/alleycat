"""Focused synthetic coverage for the durable production descriptor contract."""
from __future__ import annotations

from copy import deepcopy
import json
from pathlib import Path
import shutil

import pytest


from kimodo_pipeline.runtime_package_descriptor import (
    CONTACT_EVIDENCED_LOCOMOTION,
    CONTACT_FREE_IDLE,
    PackageDescriptorError,
    build_descriptor,
    reject_additional_variants,
    validate_descriptor,
)


FIXTURE_CHARACTER = "fixture_character"
FIXTURE_VARIANT = "ember"


def fixture_package(tmp_path: Path) -> tuple[dict, Path, Path]:
    processed = tmp_path / "processed"
    runtime = tmp_path / "runtime"
    variant_dir = processed / FIXTURE_VARIANT
    (variant_dir / "provenance").mkdir(parents=True)
    runtime.mkdir()
    (variant_dir / "processed-index.json").write_text("{}\n", encoding="utf-8")
    (processed / f"locomotion_{FIXTURE_VARIANT}.blend").write_bytes(b"synthetic blend")
    evidence = {
        "package_identity": "fixture-package",
        "source_derived": {"generation": "fixture-generation"},
    }
    evidence_text = json.dumps(evidence) + "\n"
    (variant_dir / "provenance/authored.json").write_text(evidence_text, encoding="utf-8")
    (runtime / "authored.json").write_text(evidence_text, encoding="utf-8")
    (runtime / f"locomotion_{FIXTURE_VARIANT}.tres").write_text("fixture library\n", encoding="utf-8")
    (runtime / f"locomotion_{FIXTURE_VARIANT}_catalogue.json").write_text(json.dumps({
        "contact_evidence": {
            "package_identity": "fixture-package",
            "generation": "fixture-generation",
        },
    }) + "\n", encoding="utf-8")
    package = {
        "publication_mode": CONTACT_EVIDENCED_LOCOMOTION,
        "character": FIXTURE_CHARACTER,
        "name": "locomotion",
        "variant": FIXTURE_VARIANT,
        "index": "res://staging/processed-index.json",
        "published_index": f"res://assets/fixture/{FIXTURE_VARIANT}/processed-index.json",
        "blend": "res://staging/opaque.blend",
        "root": "res://assets/fixture/runtime/locomotion/ember",
        "contact_evidence": {
            "retained_input": "res://assets/fixture/provenance/authored.json",
            "runtime_path": "authored.json",
            "package_identity": "fixture-package",
            "generation": "fixture-generation",
        },
        "stitch_edges": [],
    }
    return build_descriptor(package, processed, runtime), processed, runtime


def idle_fixture_package(tmp_path: Path) -> tuple[dict, Path, Path]:
    processed = tmp_path / "processed"
    runtime = tmp_path / "runtime"
    variant_dir = processed / FIXTURE_VARIANT
    variant_dir.mkdir(parents=True)
    runtime.mkdir()
    (variant_dir / "processed-index.json").write_text("{}\n", encoding="utf-8")
    (processed / f"idle_{FIXTURE_VARIANT}.blend").write_bytes(b"synthetic idle blend")
    (runtime / f"idle_{FIXTURE_VARIANT}.tres").write_text("synthetic library\n", encoding="utf-8")
    (runtime / f"idle_{FIXTURE_VARIANT}_catalogue.json").write_text("{}\n", encoding="utf-8")
    package = {
        "publication_mode": CONTACT_FREE_IDLE,
        "character": FIXTURE_CHARACTER,
        "name": "idle",
        "variant": FIXTURE_VARIANT,
        "index": "res://staging/processed-index.json",
        "published_index": f"res://assets/fixture/{FIXTURE_VARIANT}/processed-index.json",
        "blend": "res://staging/opaque.blend",
        "root": "res://assets/fixture/runtime/idle/ember",
        "stitch_edges": [],
    }
    return build_descriptor(package, processed, runtime), processed, runtime


def test_bound_production_descriptor_accepts_matching_publication(tmp_path: Path) -> None:
    descriptor, processed, runtime = fixture_package(tmp_path)
    validate_descriptor(descriptor, processed, runtime)
    assert descriptor["character"] == FIXTURE_CHARACTER
    assert descriptor["name"] == "locomotion"
    assert descriptor["variant"] == FIXTURE_VARIANT
    assert set(descriptor["binding"]) == {
        "processed_index_sha256", "retargeted_blend_sha256", "runtime_catalogue_sha256",
        "runtime_library_sha256", "retained_contact_evidence_sha256", "runtime_contact_evidence_sha256",
    }


def test_contact_free_idle_descriptor_accepts_matching_publication_without_contact_files(tmp_path: Path) -> None:
    descriptor, processed, runtime = idle_fixture_package(tmp_path)

    validate_descriptor(descriptor, processed, runtime)
    assert descriptor["contact_evidence_required"] is False
    assert descriptor["contact_evidence"] is None


@pytest.mark.parametrize("mutation", [
    lambda value: value.__setitem__("contact_evidence", {}),
    lambda value: value.__setitem__("contact_evidence_required", True),
    lambda value: value.__setitem__("variant", ""),
])
def test_contact_free_idle_descriptor_refuses_contact_declaration_or_requirement(tmp_path: Path, mutation) -> None:
    _descriptor, processed, runtime = idle_fixture_package(tmp_path)
    package = {
        "publication_mode": CONTACT_FREE_IDLE,
        "character": FIXTURE_CHARACTER,
        "name": "idle",
        "variant": FIXTURE_VARIANT,
        "index": "res://staging/processed-index.json",
        "published_index": f"res://assets/fixture/{FIXTURE_VARIANT}/processed-index.json",
        "blend": "res://staging/opaque.blend",
        "root": "res://assets/fixture/runtime/idle/ember",
        "stitch_edges": [],
    }
    mutation(package)

    with pytest.raises(PackageDescriptorError, match="contact-free idle|variant"):
        build_descriptor(package, processed, runtime)


@pytest.mark.parametrize("mutation", [
    lambda value: value.clear(),
    lambda value: value.__setitem__("schema_version", 1),
    lambda value: value.pop("character"),
    lambda value: value.pop("variant"),
    lambda value: value.__setitem__("root", "res://assets/fixture/runtime/locomotion/other"),
    lambda value: value["contact_evidence"].__setitem__("input", "res://assets/other/evidence.json"),
    lambda value: value["contact_evidence"].__setitem__("package_identity", "wrong-package"),
    lambda value: value["contact_evidence"].__setitem__("generation", "wrong-generation"),
    lambda value: value["binding"].__setitem__("runtime_catalogue_sha256", "0" * 64),
])
def test_bound_production_descriptor_rejects_missing_path_identity_generation_and_hash_mismatch(
    tmp_path: Path,
    mutation,
) -> None:
    descriptor, processed, runtime = fixture_package(tmp_path)
    malformed = deepcopy(descriptor)
    mutation(malformed)
    with pytest.raises(PackageDescriptorError):
        validate_descriptor(malformed, processed, runtime)


def test_second_variant_for_the_same_character_and_set_is_refused(tmp_path: Path) -> None:
    processed = tmp_path / "processed"
    runtime = tmp_path / "runtime"
    (processed / FIXTURE_VARIANT).mkdir(parents=True)
    (processed / FIXTURE_VARIANT / "processed-index.json").write_text("{}\n", encoding="utf-8")
    (runtime / FIXTURE_VARIANT).mkdir(parents=True)
    (runtime / FIXTURE_VARIANT / f"idle_{FIXTURE_VARIANT}_catalogue.json").write_text("{}\n", encoding="utf-8")

    sibling_processed = processed / "zephyr"
    sibling_processed.mkdir()
    (sibling_processed / "processed-index.json").write_text("{}\n", encoding="utf-8")
    with pytest.raises(PackageDescriptorError, match="zephyr.*multi-variant publication is not ratified"):
        reject_additional_variants(processed, runtime, FIXTURE_VARIANT)
    shutil.rmtree(sibling_processed)

    sibling_runtime = runtime / "quartz"
    sibling_runtime.mkdir()
    (sibling_runtime / "idle_quartz_catalogue.json").write_text("{}\n", encoding="utf-8")
    with pytest.raises(PackageDescriptorError, match="quartz.*multi-variant publication is not ratified"):
        reject_additional_variants(processed, runtime, FIXTURE_VARIANT)
    shutil.rmtree(sibling_runtime)

    reject_additional_variants(processed, runtime, FIXTURE_VARIANT)
