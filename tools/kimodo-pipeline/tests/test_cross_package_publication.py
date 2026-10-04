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
    CATALOGUE_PLANTED_INTERVAL_IDLE,
    PLANTED_INTERVAL_FILE_LOCOMOTION,
    PLANTED_INTERVALS_FILENAME,
    build_descriptor,
    runtime_library_sha256,
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
    (runtime / f"locomotion_{variant}.tres").write_text("synthetic library\n", encoding="utf-8")
    intervals_text = json.dumps({
        "schema_version": 1,
        "derivation": {"runtime_library_sha256": runtime_library_sha256(runtime / f"locomotion_{variant}.tres")},
    }) + "\n"
    (variant_stage / "provenance" / PLANTED_INTERVALS_FILENAME).write_text(intervals_text, encoding="utf-8")
    (runtime / PLANTED_INTERVALS_FILENAME).write_text(intervals_text, encoding="utf-8")
    (runtime / f"locomotion_{variant}_catalogue.json").write_text("{}", encoding="utf-8")
    descriptor = build_descriptor({
        "publication_mode": PLANTED_INTERVAL_FILE_LOCOMOTION,
        "character": "fixture_character",
        "name": "locomotion",
        "variant": variant,
        "index": "res://staging/processed-index.json",
        "published_index": f"res://assets/fixture/{variant}/processed-index.json",
        "blend": "res://staging/opaque.blend",
        "root": f"res://assets/fixture/runtime/locomotion/{variant}",
        "planted_intervals": {
            "retained_input": f"res://assets/fixture/{variant}/provenance/{PLANTED_INTERVALS_FILENAME}",
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


@pytest.mark.parametrize("failure_point", [
    "preflight",
    "after_target_0_publication",
    "after_target_1_publication",
    "after_target_2_publication",
    "after_target_3_publication",
    "after_target_4_publication",
    "before_old_root_removal",
    "after_old_root_removal",
])
def test_every_injected_failure_point_restores_all_target_and_legacy_roots(
    transaction_paths: dict[str, Path], failure_point: str,
) -> None:
    before = root_snapshots(transaction_paths)

    with pytest.raises(CrossPackagePublicationError, match="all roots were restored"):
        transaction(transaction_paths, failure_point=failure_point).publish()

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
            plan("locomotion", PLANTED_INTERVAL_FILE_LOCOMOTION),
            plan("idle", CATALOGUE_PLANTED_INTERVAL_IDLE),
        ],
        "legacy_roots": [str(tmp_path / "legacy")],
    }), encoding="utf-8")

    plans, legacy_roots = load_manifest(manifest)

    assert {(plan.name, plan.mode) for plan in plans} == {
        ("locomotion", PLANTED_INTERVAL_FILE_LOCOMOTION),
        ("idle", CATALOGUE_PLANTED_INTERVAL_IDLE),
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


def test_idle_stitch_release_intent_remains_empty(tmp_path: Path) -> None:
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
        (variant_stage / "provenance" / PLANTED_INTERVALS_FILENAME).write_text("{}\n", encoding="utf-8")
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
        retained = f"res://assets/characters/fixture_character/animations/processed/{plan['name']}/{variant}/provenance/{PLANTED_INTERVALS_FILENAME}"
        staged = f"res://temp/stage/processed/{plan['name']}/{variant}/provenance/{PLANTED_INTERVALS_FILENAME}"
        final = json.loads((stage / f"{plan['name']}-final-descriptor.json").read_text(encoding="utf-8"))
        assert final["planted_intervals"]["input"] == final["planted_intervals"]["retained_input"] == retained
        assert descriptor["planted_intervals"]["input"] == staged
        assert descriptor["planted_intervals"]["storage"] == ("package_file" if plan["name"] == "locomotion" else "catalogue_field")


def test_manifest_refuses_a_stage_without_derived_planted_intervals(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    game = tmp_path / "game"
    stage = game / "temp" / "stage"
    character_root = game / "assets" / "characters" / "fixture_character" / "animations"
    for name in PACKAGE_NAMES:
        variant_stage = stage / "processed" / name / "ember"
        (variant_stage / "provenance").mkdir(parents=True)
        (stage / "runtime" / name / "ember").mkdir(parents=True)
        (stage / "processed" / name / f"{name}_ember.blend").write_bytes(b"fixture")
        (variant_stage / "processed-index.json").write_text("{}\n", encoding="utf-8")
        (variant_stage / STITCH_RELEASE_INTENT).write_text(json.dumps({"schema_version": 1, "stitch_edges": []}), encoding="utf-8")
    monkeypatch.setattr(publication_manifest, "GAME", game)

    with pytest.raises(PublicationManifestError, match="no derived planted intervals"):
        write_manifest(stage, character_root / "processed", character_root / "runtime", stage / "manifest.json", [game / "legacy"])


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
        (variant_stage / "provenance" / PLANTED_INTERVALS_FILENAME).write_text("{}\n", encoding="utf-8")
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
