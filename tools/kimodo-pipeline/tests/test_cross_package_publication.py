"""Synthetic contracts for generic multi-package cross-root publication."""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import shutil
import tempfile
from typing import Iterator

import pytest

from kimodo_pipeline import publication_manifest
from kimodo_pipeline.cross_package_publication import (
    CrossPackagePublicationError,
    CrossRootPublicationTransaction,
    StagedPackage,
)
from kimodo_pipeline.runtime_package_descriptor import (
    CONTACT_EVIDENCED_LOCOMOTION,
    CONTACT_FREE_IDLE,
    build_descriptor,
    validate_descriptor,
)
from kimodo_pipeline.publication_manifest import (
    PublicationManifestError,
    STITCH_RELEASE_INTENT,
    load_stitch_release_intent,
    write_manifest,
)
from kimodo_pipeline.validated_publication import load_manifest


PACKAGE_NAMES = ("locomotion", "idle")


def tree_hashes(root: Path) -> dict[str, str]:
    return {
        str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest()
        for path in root.rglob("*")
        if path.is_file()
    }


def write_package(root: Path, marker: str) -> None:
    (root / "nested").mkdir(parents=True)
    (root / "nested" / "payload.bin").write_bytes(marker.encode("utf-8"))


def validate(processed: Path, runtime: Path) -> None:
    assert (processed / "nested" / "payload.bin").is_file()
    assert (runtime / "nested" / "payload.bin").is_file()


@pytest.fixture
def transaction_paths() -> Iterator[dict[str, Path]]:
    with tempfile.TemporaryDirectory(prefix="kimodo-cross-root-") as directory:
        base = Path(directory)
        paths = {"base": base, "legacy": base / "legacy-root"}
        write_package(paths["legacy"], "old legacy")
        for name in PACKAGE_NAMES:
            for kind, marker in (("processed", f"old {name} processed"), ("runtime", f"old {name} runtime")):
                root = base / "production" / name / kind
                stage = base / "staging" / name / kind
                write_package(root, marker)
                write_package(stage, f"new {name} {kind}")
                paths[f"{name}_{kind}_root"] = root
                paths[f"{name}_{kind}_stage"] = stage
        yield paths


def packages(paths: dict[str, Path], *, fail_idle_after_replacement: bool = False) -> tuple[StagedPackage, ...]:
    result = []
    for name in PACKAGE_NAMES:
        stage_processed = paths[f"{name}_processed_stage"]

        def package_validator(processed: Path, runtime: Path, *, package_name: str = name, stage: Path = stage_processed) -> None:
            validate(processed, runtime)
            if fail_idle_after_replacement and package_name == "idle" and processed != stage:
                raise ValueError("synthetic idle staged package validation failed")

        result.append(StagedPackage(
            name,
            paths[f"{name}_processed_stage"],
            paths[f"{name}_runtime_stage"],
            paths[f"{name}_processed_root"],
            paths[f"{name}_runtime_root"],
            package_validator,
        ))
    return tuple(result)


def transaction(
    paths: dict[str, Path],
    *,
    failure_point: str | None = None,
    fail_idle_after_replacement: bool = False,
) -> CrossRootPublicationTransaction:
    return CrossRootPublicationTransaction(
        packages(paths, fail_idle_after_replacement=fail_idle_after_replacement),
        legacy_roots=(paths["legacy"],),
        journal_directory=paths["base"] / "journals",
        failure_point=failure_point,
    )


def descriptor_mismatch_transaction(paths: dict[str, Path]) -> CrossRootPublicationTransaction:
    declared_packages = list(packages(paths))
    variant = "ember"
    processed = paths["locomotion_processed_stage"]
    runtime = paths["locomotion_runtime_stage"]
    variant_stage = processed / variant
    (variant_stage / "provenance").mkdir(parents=True)
    (variant_stage / "processed-index.json").write_text("{}\n", encoding="utf-8")
    (processed / f"locomotion_{variant}.blend").write_bytes(b"synthetic locomotion blend")
    evidence = {
        "package_identity": "synthetic-locomotion",
        "source_derived": {"generation": "synthetic-generation"},
    }
    evidence_text = json.dumps(evidence) + "\n"
    (variant_stage / "provenance" / "contacts.json").write_text(evidence_text, encoding="utf-8")
    (runtime / "contacts.json").write_text(evidence_text, encoding="utf-8")
    (runtime / f"locomotion_{variant}.tres").write_text("synthetic library\n", encoding="utf-8")
    (runtime / f"locomotion_{variant}_catalogue.json").write_text(json.dumps({
        "contact_evidence": {
            "package_identity": "synthetic-locomotion",
            "generation": "synthetic-generation",
        },
    }), encoding="utf-8")
    descriptor = build_descriptor({
        "publication_mode": CONTACT_EVIDENCED_LOCOMOTION,
        "character": "fixture_character",
        "name": "locomotion",
        "variant": variant,
        "index": "res://staging/processed-index.json",
        "published_index": f"res://assets/fixture/{variant}/processed-index.json",
        "blend": "res://staging/opaque.blend",
        "root": f"res://assets/fixture/runtime/locomotion/{variant}",
        "contact_evidence": {
            "retained_input": "res://assets/fixture/provenance/contacts.json",
            "runtime_path": "contacts.json",
            "package_identity": "synthetic-locomotion",
            "generation": "synthetic-generation",
        },
        "stitch_edges": [],
    }, processed, runtime)
    (runtime / f"locomotion_{variant}.tres").write_text("corrupted after binding\n", encoding="utf-8")

    def reject_locomotion_descriptor(processed: Path, runtime: Path) -> None:
        validate(processed, runtime)
        validate_descriptor(descriptor, processed, runtime)

    declared_packages[0] = StagedPackage(
        "locomotion",
        paths["locomotion_processed_stage"],
        paths["locomotion_runtime_stage"],
        paths["locomotion_processed_root"],
        paths["locomotion_runtime_root"],
        reject_locomotion_descriptor,
    )
    return CrossRootPublicationTransaction(
        declared_packages,
        legacy_roots=(paths["legacy"],),
        journal_directory=paths["base"] / "journals",
    )


def remnants(paths: dict[str, Path]) -> list[Path]:
    return (
        list(paths["base"].rglob(".kimodo-publication-*.journal.json"))
        + list(paths["base"].rglob(".*.backup-*"))
        + list(paths["base"].rglob(".*.recovery-*"))
        + [path for path in paths["base"].glob("staging/*/*") if path.exists()]
    )


def root_snapshots(paths: dict[str, Path]) -> dict[str, dict[str, str]]:
    names = ["legacy"] + [f"{package}_{kind}_root" for package in PACKAGE_NAMES for kind in ("processed", "runtime")]
    return {name: tree_hashes(paths[name]) for name in names}


def test_success_commits_locomotion_and_idle_and_removes_legacy_without_remnants(transaction_paths: dict[str, Path]) -> None:
    transaction(transaction_paths).publish()

    for name in PACKAGE_NAMES:
        for kind in ("processed", "runtime"):
            root = transaction_paths[f"{name}_{kind}_root"]
            assert tree_hashes(root)["nested/payload.bin"] == hashlib.sha256(f"new {name} {kind}".encode()).hexdigest()
            assert not transaction_paths[f"{name}_{kind}_stage"].exists()
    assert not transaction_paths["legacy"].exists()
    assert not remnants(transaction_paths)


def test_one_package_post_replacement_validation_failure_restores_all_target_and_legacy_roots(
    transaction_paths: dict[str, Path],
) -> None:
    before = root_snapshots(transaction_paths)

    with pytest.raises(CrossPackagePublicationError, match="all roots were restored"):
        transaction(transaction_paths, fail_idle_after_replacement=True).publish()

    for name, snapshot in before.items():
        assert tree_hashes(transaction_paths[name]) == snapshot
    assert not remnants(transaction_paths)


def test_locomotion_descriptor_mismatch_refuses_publication_without_mutating_any_root(
    transaction_paths: dict[str, Path],
) -> None:
    before = root_snapshots(transaction_paths)

    with pytest.raises(CrossPackagePublicationError, match="all roots were restored"):
        descriptor_mismatch_transaction(transaction_paths).publish()

    for name, snapshot in before.items():
        assert tree_hashes(transaction_paths[name]) == snapshot
    assert not remnants(transaction_paths)


def test_old_root_removal_failure_restores_all_target_and_legacy_roots(transaction_paths: dict[str, Path]) -> None:
    before = root_snapshots(transaction_paths)

    with pytest.raises(CrossPackagePublicationError, match="all roots were restored"):
        transaction(transaction_paths, failure_point="after_old_root_removal").publish()

    for name, snapshot in before.items():
        assert tree_hashes(transaction_paths[name]) == snapshot
    assert not remnants(transaction_paths)


def test_publication_manifest_requires_the_two_declared_package_modes(tmp_path: Path) -> None:
    def plan(name: str, mode: str) -> dict[str, str]:
        return {
            "name": name,
            "mode": mode,
            "processed_stage": str(tmp_path / f"{name}-processed-stage"),
            "runtime_stage": str(tmp_path / f"{name}-runtime-stage"),
            "processed_root": str(tmp_path / f"{name}-processed-root"),
            "runtime_root": str(tmp_path / f"{name}-runtime-root"),
            "preflight_descriptor": str(tmp_path / f"{name}-preflight.json"),
            "final_descriptor": str(tmp_path / f"{name}-final.json"),
        }

    manifest = tmp_path / "publication-manifest.json"
    manifest.write_text(json.dumps({
        "schema_version": 1,
        "packages": [
            plan("locomotion", CONTACT_EVIDENCED_LOCOMOTION),
            plan("idle", CONTACT_FREE_IDLE),
        ],
        "legacy_roots": [str(tmp_path / "legacy")],
    }), encoding="utf-8")

    plans, legacy_roots = load_manifest(manifest)

    assert {(plan.name, plan.mode) for plan in plans} == {
        ("locomotion", CONTACT_EVIDENCED_LOCOMOTION),
        ("idle", CONTACT_FREE_IDLE),
    }
    assert legacy_roots == ((tmp_path / "legacy").resolve(),)


def test_package_owned_stitch_release_intent_is_loaded_without_role_semantics(tmp_path: Path) -> None:
    path = tmp_path / STITCH_RELEASE_INTENT
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({
        "schema_version": 1,
        "stitch_edges": [
            {"from_role": "quartz_cycle", "to_role": "ember_exit"},
            {"from_role": "quartz_cycle", "to_role": "cobalt_exit"},
        ],
    }), encoding="utf-8")

    assert load_stitch_release_intent(tmp_path) == [
        {"from_role": "quartz_cycle", "to_role": "ember_exit"},
        {"from_role": "quartz_cycle", "to_role": "cobalt_exit"},
    ]


def test_contact_free_idle_stitch_release_intent_remains_empty(tmp_path: Path) -> None:
    path = tmp_path / STITCH_RELEASE_INTENT
    path.parent.mkdir(parents=True)
    path.write_text(json.dumps({"schema_version": 1, "stitch_edges": []}), encoding="utf-8")

    assert load_stitch_release_intent(tmp_path) == []


def test_manifest_transports_each_package_owned_stitch_release_intent(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    game = tmp_path / "game"
    stage = game / "temp" / "stage"
    character_root = game / "assets" / "characters" / "fixture_character" / "animations"
    processed_root = character_root / "processed"
    runtime_root = character_root / "runtime"
    variant = "ember"
    expected = {
        "locomotion": [{"from_role": "quartz_cycle", "to_role": "ember_exit"}],
        "idle": [],
    }
    for name in PACKAGE_NAMES:
        processed = stage / "processed" / name
        variant_stage = processed / variant
        runtime = stage / "runtime" / name / variant
        (variant_stage / "provenance").mkdir(parents=True)
        runtime.mkdir(parents=True)
        (processed / f"{name}_{variant}.blend").write_bytes(b"fixture")
        (variant_stage / "processed-index.json").write_text("{}\n", encoding="utf-8")
        (variant_stage / STITCH_RELEASE_INTENT).write_text(json.dumps({
            "schema_version": 1,
            "stitch_edges": expected[name],
        }), encoding="utf-8")
    (stage / "processed" / "locomotion" / variant / "provenance" / "authored_per_foot_contacts_descriptor_v1.json").write_text(
        json.dumps({"contact_evidence": {
            "runtime_path": "contacts.json",
            "package_identity": "fixture-package",
            "generation": "fixture-generation",
        }}),
        encoding="utf-8",
    )
    monkeypatch.setattr(publication_manifest, "GAME", game)

    manifest = write_manifest(stage, processed_root, runtime_root, stage / "manifest.json", [game / "legacy"])

    document = json.loads(manifest.read_text(encoding="utf-8"))
    for plan in document["packages"]:
        descriptor = json.loads((stage / f"{plan['name']}-preflight-descriptor.json").read_text(encoding="utf-8"))
        assert descriptor["character"] == "fixture_character"
        assert descriptor["variant"] == variant
        assert descriptor["stitch_edges"] == expected[plan["name"]]
        assert Path(plan["runtime_stage"]) == stage / "runtime" / plan["name"] / variant
        assert Path(plan["runtime_root"]) == runtime_root / plan["name"] / variant


def test_manifest_rejects_a_second_variant_for_the_same_character_and_set(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    game = tmp_path / "game"
    stage = game / "temp" / "stage"
    character_root = game / "assets" / "characters" / "fixture_character" / "animations"
    processed_root = character_root / "processed"
    runtime_root = character_root / "runtime"
    variant = "ember"
    for name in PACKAGE_NAMES:
        processed = stage / "processed" / name
        variant_stage = processed / variant
        runtime = stage / "runtime" / name / variant
        (variant_stage / "provenance").mkdir(parents=True)
        runtime.mkdir(parents=True)
        (processed / f"{name}_{variant}.blend").write_bytes(b"fixture")
        (variant_stage / "processed-index.json").write_text("{}\n", encoding="utf-8")
        (variant_stage / STITCH_RELEASE_INTENT).write_text(json.dumps({
            "schema_version": 1,
            "stitch_edges": [],
        }), encoding="utf-8")
    (stage / "processed" / "locomotion" / variant / "provenance" / "authored_per_foot_contacts_descriptor_v1.json").write_text(
        json.dumps({"contact_evidence": {
            "runtime_path": "contacts.json",
            "package_identity": "fixture-package",
            "generation": "fixture-generation",
        }}),
        encoding="utf-8",
    )
    monkeypatch.setattr(publication_manifest, "GAME", game)

    delivered_variant = processed_root / "locomotion" / "zephyr"
    delivered_variant.mkdir(parents=True)
    (delivered_variant / "processed-index.json").write_text("{}\n", encoding="utf-8")
    try:
        with pytest.raises(PublicationManifestError, match="zephyr.*multi-variant publication is not ratified"):
            write_manifest(stage, processed_root, runtime_root, stage / "manifest.json", [game / "legacy"])
    finally:
        shutil.rmtree(delivered_variant)


@pytest.mark.parametrize("document", [
    None,
    [],
    {"schema_version": 2, "stitch_edges": []},
    {"schema_version": 1},
    {"schema_version": 1, "stitch_edges": [], "roles": []},
    {"schema_version": 1, "stitch_edges": {}},
    {"schema_version": 1, "stitch_edges": [{"from_role": "cycle"}]},
    {"schema_version": 1, "stitch_edges": [{"from_role": "cycle", "to_role": "cycle"}]},
    {"schema_version": 1, "stitch_edges": [
        {"from_role": "cycle", "to_role": "exit"},
        {"from_role": "cycle", "to_role": "exit"},
    ]},
])
def test_malformed_stitch_release_intent_is_rejected(tmp_path: Path, document: object) -> None:
    path = tmp_path / STITCH_RELEASE_INTENT
    path.parent.mkdir(parents=True)
    path.write_text("not json" if document is None else json.dumps(document), encoding="utf-8")

    with pytest.raises(PublicationManifestError, match="Stitch release intent"):
        load_stitch_release_intent(tmp_path)


def test_missing_stitch_release_intent_is_rejected(tmp_path: Path) -> None:
    with pytest.raises(PublicationManifestError, match="missing or unreadable"):
        load_stitch_release_intent(tmp_path)
