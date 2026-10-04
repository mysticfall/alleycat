"""Focused synthetic coverage for the durable production descriptor contract."""
from __future__ import annotations

from copy import deepcopy
import hashlib
import json
from pathlib import Path
import shutil

import pytest


from kimodo_pipeline.runtime_package_descriptor import (
    CATALOGUE_PLANTED_INTERVAL_IDLE,
    PLANTED_INTERVAL_FILE_LOCOMOTION,
    PLANTED_INTERVALS_CATALOGUE_FIELD,
    PLANTED_INTERVALS_FILENAME,
    PackageDescriptorError,
    build_descriptor,
    canonical_runtime_library_bytes,
    reject_additional_variants,
    runtime_library_sha256,
    validate_descriptor,
)


FIXTURE_CHARACTER = "fixture_character"
FIXTURE_VARIANT = "ember"


_LIBRARY = b'[gd_resource type="AnimationLibrary" format=3]\n\n[sub_resource type="Animation" id="Animation_a"]\n'
_LIBRARY_WITH_UID = _LIBRARY.replace(b' format=3]', b' format=3 uid="uid://fixture"]', 1)


@pytest.mark.parametrize(
    ("case", "content", "expected"),
    [
        ("valid top-level UID ignored", _LIBRARY_WITH_UID, _LIBRARY),
        ("empty UID not ignored", _LIBRARY.replace(b' format=3]', b' format=3 uid="uid://"]', 1), None),
        ("missing quote not ignored", _LIBRARY.replace(b' format=3]', b' format=3 uid="uid://fixture', 1), None),
        ("UID-like body text not ignored", _LIBRARY + b'; uid="uid://fixture"\n', None),
        ("body mutation significant", _LIBRARY.replace(b"Animation_a", b"Animation_b"), None),
        ("whitespace mutation significant", _LIBRARY.replace(b"\n\n", b"\n \n", 1), None),
        (
            "CRLF versus LF significant",
            _LIBRARY_WITH_UID.replace(b"\n", b"\r\n"),
            _LIBRARY.replace(b"\n", b"\r\n"),
        ),
        ("trailing-byte mutation significant", _LIBRARY + b"\x00", None),
    ],
)
def test_runtime_library_hash_canonicalisation_matrix(
    tmp_path: Path,
    case: str,
    content: bytes,
    expected: bytes | None,
) -> None:
    expected_bytes = content if expected is None else expected
    path = tmp_path / f"{case.replace(' ', '_')}.tres"
    path.write_bytes(content)

    assert canonical_runtime_library_bytes(content) == expected_bytes
    assert runtime_library_sha256(path) == hashlib.sha256(expected_bytes).hexdigest()
    if case != "valid top-level UID ignored":
        assert runtime_library_sha256(path) != hashlib.sha256(_LIBRARY).hexdigest()


CANONICALISATION_VECTORS = Path(__file__).resolve().parent / "fixtures/runtime_library_canonicalisation_vectors.json"


def _canonicalisation_vectors() -> list[dict]:
    return json.loads(CANONICALISATION_VECTORS.read_text(encoding="utf-8"))["vectors"]


@pytest.mark.parametrize("vector", _canonicalisation_vectors(), ids=lambda vector: vector["name"])
def test_runtime_library_canonicalisation_matches_the_shared_parity_vectors(vector: dict) -> None:
    """Python, Godot, and C# canonicalise every shared vector to the same bytes."""
    content = bytes.fromhex(vector["content_hex"])
    expected = content if vector["canonical_hex"] is None else bytes.fromhex(vector["canonical_hex"])

    assert canonical_runtime_library_bytes(content) == expected


RETAINED_INTERVALS = f"res://assets/fixture/{FIXTURE_VARIANT}/provenance/{PLANTED_INTERVALS_FILENAME}"


def _intervals(runtime_library: Path) -> dict:
    return {
        "schema_version": 1,
        "contract": "anim002_per_foot_planted_intervals_v1",
        "derivation": {"runtime_library_sha256": runtime_library_sha256(runtime_library)},
        "clips": [],
    }


def _package(name: str, mode: str) -> dict:
    return {
        "publication_mode": mode,
        "character": FIXTURE_CHARACTER,
        "name": name,
        "variant": FIXTURE_VARIANT,
        "index": "res://staging/processed-index.json",
        "published_index": f"res://assets/fixture/{FIXTURE_VARIANT}/processed-index.json",
        "blend": "res://staging/opaque.blend",
        "root": f"res://assets/fixture/runtime/{name}/{FIXTURE_VARIANT}",
        "planted_intervals": {"input": "res://staging/intervals.json", "retained_input": RETAINED_INTERVALS},
        "stitch_edges": [],
    }


def _stage(tmp_path: Path, name: str) -> tuple[Path, Path, dict]:
    processed = tmp_path / "processed"
    runtime = tmp_path / "runtime"
    variant_dir = processed / FIXTURE_VARIANT
    (variant_dir / "provenance").mkdir(parents=True)
    runtime.mkdir()
    (variant_dir / "processed-index.json").write_text("{}\n", encoding="utf-8")
    (processed / f"{name}_{FIXTURE_VARIANT}.blend").write_bytes(b"synthetic blend")
    library = runtime / f"{name}_{FIXTURE_VARIANT}.tres"
    library.write_bytes(_LIBRARY_WITH_UID)
    intervals = _intervals(library)
    (variant_dir / "provenance" / PLANTED_INTERVALS_FILENAME).write_text(json.dumps(intervals) + "\n", encoding="utf-8")
    return processed, runtime, intervals


def fixture_package(tmp_path: Path) -> tuple[dict, Path, Path]:
    processed, runtime, intervals = _stage(tmp_path, "locomotion")
    (runtime / PLANTED_INTERVALS_FILENAME).write_text(json.dumps(intervals) + "\n", encoding="utf-8")
    (runtime / f"locomotion_{FIXTURE_VARIANT}_catalogue.json").write_text("{}\n", encoding="utf-8")
    package = _package("locomotion", PLANTED_INTERVAL_FILE_LOCOMOTION)
    return build_descriptor(package, processed, runtime), processed, runtime


def idle_fixture_package(tmp_path: Path) -> tuple[dict, Path, Path]:
    processed, runtime, intervals = _stage(tmp_path, "idle")
    (runtime / f"idle_{FIXTURE_VARIANT}_catalogue.json").write_text(
        json.dumps({PLANTED_INTERVALS_CATALOGUE_FIELD: intervals}) + "\n", encoding="utf-8",
    )
    package = _package("idle", CATALOGUE_PLANTED_INTERVAL_IDLE)
    return build_descriptor(package, processed, runtime), processed, runtime


def test_bound_production_descriptor_accepts_matching_publication(tmp_path: Path) -> None:
    descriptor, processed, runtime = fixture_package(tmp_path)
    validate_descriptor(descriptor, processed, runtime)
    assert descriptor["character"] == FIXTURE_CHARACTER
    assert descriptor["name"] == "locomotion"
    assert descriptor["variant"] == FIXTURE_VARIANT
    assert descriptor["planted_intervals"] == {
        "retained_input": RETAINED_INTERVALS, "storage": "package_file", "runtime_path": PLANTED_INTERVALS_FILENAME,
    }
    assert set(descriptor["binding"]) == {
        "processed_index_sha256", "retargeted_blend_sha256", "runtime_catalogue_sha256",
        "runtime_library_sha256", "retained_planted_intervals_sha256", "runtime_planted_intervals_sha256",
    }


def test_idle_descriptor_binds_catalogue_embedded_planted_intervals(tmp_path: Path) -> None:
    descriptor, processed, runtime = idle_fixture_package(tmp_path)

    validate_descriptor(descriptor, processed, runtime)
    assert descriptor["planted_intervals"] == {"retained_input": RETAINED_INTERVALS, "storage": "catalogue_field"}
    assert "runtime_planted_intervals_sha256" not in descriptor["binding"]


@pytest.mark.parametrize(("name", "mode"), [
    ("idle", PLANTED_INTERVAL_FILE_LOCOMOTION),
    ("locomotion", CATALOGUE_PLANTED_INTERVAL_IDLE),
    ("locomotion", "contact-evidenced-locomotion"),
])
def test_publication_modes_are_reserved_per_package(tmp_path: Path, name: str, mode: str) -> None:
    processed, runtime, _intervals_document = _stage(tmp_path, name)

    with pytest.raises(PackageDescriptorError, match="publication mode|reserved"):
        build_descriptor(_package(name, mode), processed, runtime)


def test_descriptor_refuses_intervals_derived_from_another_library(tmp_path: Path) -> None:
    descriptor, processed, runtime = idle_fixture_package(tmp_path)
    stale = {**_intervals(runtime / f"idle_{FIXTURE_VARIANT}.tres"), "derivation": {"runtime_library_sha256": "0" * 64}}
    retained = processed / FIXTURE_VARIANT / "provenance" / PLANTED_INTERVALS_FILENAME
    retained.write_text(json.dumps(stale) + "\n", encoding="utf-8")
    (runtime / f"idle_{FIXTURE_VARIANT}_catalogue.json").write_text(
        json.dumps({PLANTED_INTERVALS_CATALOGUE_FIELD: stale}) + "\n", encoding="utf-8",
    )
    rebound = deepcopy(descriptor)
    rebound["binding"]["retained_planted_intervals_sha256"] = hashlib.sha256(retained.read_bytes()).hexdigest()
    rebound["binding"]["runtime_catalogue_sha256"] = hashlib.sha256(
        (runtime / f"idle_{FIXTURE_VARIANT}_catalogue.json").read_bytes()).hexdigest()

    with pytest.raises(PackageDescriptorError, match="different runtime library"):
        validate_descriptor(rebound, processed, runtime)


@pytest.mark.parametrize("mutation", [
    lambda value: value.clear(),
    lambda value: value.__setitem__("schema_version", 2),
    lambda value: value.pop("character"),
    lambda value: value.pop("variant"),
    lambda value: value.__setitem__("root", "res://assets/fixture/runtime/locomotion/other"),
    lambda value: value["planted_intervals"].__setitem__("retained_input", "res://assets/other/intervals.json"),
    lambda value: value["planted_intervals"].__setitem__("storage", "catalogue_field"),
    lambda value: value["planted_intervals"].__setitem__("runtime_path", "legacy.json"),
    lambda value: value["binding"].pop("runtime_planted_intervals_sha256"),
    lambda value: value["binding"].__setitem__("runtime_catalogue_sha256", "0" * 64),
    lambda value: value["binding"].__setitem__("runtime_library_sha256", "0" * 64),
    lambda value: value["binding"].__setitem__("retained_planted_intervals_sha256", "0" * 64),
])
def test_bound_production_descriptor_rejects_missing_path_identity_intervals_and_hash_mismatch(
    tmp_path: Path,
    mutation,
) -> None:
    descriptor, processed, runtime = fixture_package(tmp_path)
    malformed = deepcopy(descriptor)
    mutation(malformed)
    with pytest.raises(PackageDescriptorError):
        validate_descriptor(malformed, processed, runtime)


def test_locomotion_descriptor_refuses_divergent_runtime_intervals(tmp_path: Path) -> None:
    descriptor, processed, runtime = fixture_package(tmp_path)
    runtime_intervals = runtime / PLANTED_INTERVALS_FILENAME
    runtime_intervals.write_text(runtime_intervals.read_text(encoding="utf-8") + " ", encoding="utf-8")
    rebound = deepcopy(descriptor)
    rebound["binding"]["runtime_planted_intervals_sha256"] = hashlib.sha256(runtime_intervals.read_bytes()).hexdigest()

    with pytest.raises(PackageDescriptorError, match="retained/runtime planted intervals disagree"):
        validate_descriptor(rebound, processed, runtime)


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
