"""Synthetic, normal-path ANIM-002 import, publication, and validation coverage."""
from __future__ import annotations

from contextlib import contextmanager
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from typing import Any, Callable, Iterator

import pytest


from kimodo_pipeline.bootstrap_paths import repository_root


ROOT = repository_root()
GAME = ROOT / "game"
TOOLS = ROOT / "tools"
sys.path.insert(0, str(TOOLS / "kimodo-pipeline/tests"))

from planted_interval_fixture import planted_intervals, stage_runtime_library, write_planted_intervals
from synthetic_kimodo_blend import FIXTURE_SCHEMA, FIXTURE_VARIANT, SyntheticKimodoBlendBuilder


GODOT_COMMANDS = ROOT / "tools/kimodo-pipeline/godot/commands"
GODOT_TESTS = ROOT / "tools/kimodo-pipeline/tests/godot"
CONFIGURE = GODOT_COMMANDS / "configure_kimodo_import.gd"
PACKAGE = GODOT_COMMANDS / "package_kimodo_runtime.gd"
VALIDATE = GODOT_COMMANDS / "validate_kimodo_runtime_package.gd"
STITCH_GRAPH_TEST = GODOT_TESTS / "stitch_graph_test.gd"
SAMPLE_TRANSPORT_TEST = GODOT_TESTS / "sample_transport_test.gd"
ANIMATION_POSE_SAMPLER_TEST = GODOT_TESTS / "animation_pose_sampler_test.gd"
RUNTIME_PACKAGE_DESCRIPTOR_TEST = GODOT_TESTS / "runtime_package_descriptor_test.gd"
PLANTED_INTERVAL_DERIVATION_TEST = GODOT_TESTS / "planted_interval_derivation_test.gd"
DERIVE = GODOT_COMMANDS / "derive_planted_intervals.gd"
RETARGET = TOOLS / "kimodo-pipeline/commands/retarget_animations.py"
INTERVALS_FILENAME = "per_foot_planted_intervals_v1.json"
INTERVALS_FIELD = "per_foot_planted_intervals"
REPORT_FILENAME = "per_foot_planted_interval_derivation_report_v1.json"
LIBRARY_FILENAME = f"idle_{FIXTURE_VARIANT}.tres"
CATALOGUE_FILENAME = f"idle_{FIXTURE_VARIANT}_catalogue.json"
DURATION_TOLERANCE = 1e-6


def run_godot(script: Path, descriptor: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    if script == PACKAGE and LIBRARY_SOURCE is not None and not any(item.startswith("--runtime-library-source=") for item in extra):
        # Reuse the library the fixture intervals were bound to; a regenerated library gets new subresource IDs.
        extra = (*extra, f"--runtime-library-source=res://{LIBRARY_SOURCE.relative_to(GAME)}")
    return subprocess.run(
        [
            "godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(script),
            "--", f"--descriptor=res://{descriptor.relative_to(GAME)}", *extra,
        ],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )


LIBRARY_SOURCE: Path | None = None


def import_project() -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--editor", "--quit"],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )


@pytest.mark.parametrize("script", [STITCH_GRAPH_TEST, SAMPLE_TRANSPORT_TEST])
def test_external_godot_helper_scripts_pass(script: Path) -> None:
    result = subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(script)],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )
    assert result.returncode == 0, result.stdout + result.stderr


def test_runtime_package_descriptor_uid_canonicalisation_matrix() -> None:
    result = subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(RUNTIME_PACKAGE_DESCRIPTOR_TEST)],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert "ANIM-002 runtime descriptor UID tests passed: 42" in result.stdout


def test_planted_interval_derivation_fits_role_structure_and_refuses_each_failed_check() -> None:
    result = subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(PLANTED_INTERVAL_DERIVATION_TEST)],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert "ANIM002_PLANTED_INTERVAL_DERIVATION_CHECKS=passed passed=179 failed=0" in result.stdout


def test_published_planted_intervals_rederive_from_the_published_packages() -> None:
    """Real selected-content check: re-deriving each package reproduces its published intervals exactly, and its
    retained derivation report byte for byte, bound to the retained interval bytes."""
    animations = GAME / "assets/characters/reference/female/animations"
    runtime = animations / "runtime"
    published = {
        "locomotion": json.loads((runtime / "locomotion/sad" / INTERVALS_FILENAME).read_text(encoding="utf-8")),
        "idle": json.loads((runtime / "idle/sad/idle_sad_catalogue.json").read_text(encoding="utf-8"))[INTERVALS_FIELD],
    }
    for name, document in published.items():
        provenance = animations / "processed" / name / document["variant"] / "provenance"
        retained_report = (provenance / REPORT_FILENAME).read_bytes()
        assert json.loads(retained_report)["intervals_sha256"] == hashlib.sha256((provenance / INTERVALS_FILENAME).read_bytes()).hexdigest()
        with tempfile.TemporaryDirectory(prefix="kimodo-rederive-", dir=GAME / "temp") as directory:
            output = Path(directory) / INTERVALS_FILENAME
            report = Path(directory) / REPORT_FILENAME
            result = subprocess.run(
                [
                    "godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(DERIVE), "--",
                    f"--package={name}", f"--variant={document['variant']}", f"--character={document['character']}",
                    f"--output=res://{output.relative_to(GAME)}", f"--report=res://{report.relative_to(GAME)}",
                ],
                cwd=ROOT, text=True, capture_output=True, check=False, timeout=300,
            )
            assert result.returncode == 0, result.stdout + result.stderr
            assert "refused=0" in result.stdout
            assert json.loads(output.read_text(encoding="utf-8")) == document
            assert output.read_bytes() == (provenance / INTERVALS_FILENAME).read_bytes()
            assert report.read_bytes() == retained_report


def test_animation_pose_sampler_matches_engine_skeleton_and_falls_back_to_rest_pose() -> None:
    result = subprocess.run(
        ["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(ANIMATION_POSE_SAMPLER_TEST)],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert "ANIM002_POSE_SAMPLER_CHECKS=passed passed=26 failed=0" in result.stdout


FILE_INTERVAL_CATALOGUE_KEYS = {
    "schema_version", "package", "package_variant", "animation_library", "clip_count", "godot_response_coordinates", "clips",
}
EMBEDDED_INTERVAL_CATALOGUE_KEYS = FILE_INTERVAL_CATALOGUE_KEYS | {INTERVALS_FIELD}


def assert_catalogue_rejects_unsupported_fields(catalogue_path: Path, descriptor: Path, expected_keys: set[str]) -> None:
    """The published catalogue carries exactly the supported fields, and validation refuses any extra field."""
    catalogue = json.loads(catalogue_path.read_text(encoding="utf-8"))
    assert set(catalogue) == expected_keys
    original = catalogue_path.read_bytes()
    try:
        catalogue["undeclared_contract"] = {"schema_version": 1}
        catalogue_path.write_text(json.dumps(catalogue), encoding="utf-8")
        rejected = run_godot(VALIDATE, descriptor)
        assert rejected.returncode != 0
        assert "Runtime catalogue declares an unsupported field: undeclared_contract" in (rejected.stdout + rejected.stderr)
    finally:
        catalogue_path.write_bytes(original)


def tree_hashes(path: Path) -> dict[str, str]:
    return {
        str(item.relative_to(path)): hashlib.sha256(item.read_bytes()).hexdigest()
        for item in path.rglob("*") if item.is_file()
    }


@contextmanager
def runtime_stage_root(package_name: str, variant: str = FIXTURE_VARIANT) -> Iterator[Path]:
    """Provide one disposable, packager-safe runtime stage for a package invocation."""
    parent = Path(tempfile.mkdtemp(prefix="kimodo-runtime-stage-", dir=GAME / "temp"))
    try:
        yield parent / "runtime" / package_name / variant
    finally:
        shutil.rmtree(parent, ignore_errors=True)


def stage_root_argument(stage: Path) -> str:
    return "--stage-root=res://" + str(stage.relative_to(GAME))


def publish_disposable_fixture_stage(stage: Path, runtime_root: Path) -> None:
    """Move a prepared stage only into this test module's disposable fixture root."""
    stage.relative_to(GAME / "temp")
    runtime_root.relative_to(GAME / "assets/testing")
    shutil.rmtree(runtime_root, ignore_errors=True)
    runtime_root.parent.mkdir(parents=True, exist_ok=True)
    shutil.move(str(stage), str(runtime_root))


def write_mutator(path: Path, body: str) -> None:
    path.write_text("extends SceneTree\nfunc _init() -> void:\n" + body + "\n\tquit()\n", encoding="utf-8")


@pytest.fixture(scope="module")
def synthetic_package() -> Iterator[dict[str, Any]]:
    with tempfile.TemporaryDirectory(prefix="kimodo-publication-", dir=GAME / "assets/testing") as directory:
        fixture = Path(directory)
        source = fixture / "source.blend"
        builder = SyntheticKimodoBlendBuilder()
        built = builder.build_bootstrap_monolith(source)
        assert built.returncode == 0, built.stdout + built.stderr
        processed_root = fixture / "processed"
        processed = subprocess.run(
            [
                "blender", "--background", "--factory-startup", "--python", str(RETARGET), "--",
                "--package", "locomotion", "--source", str(source), "--output-root", str(processed_root),
                "--variant", FIXTURE_VARIANT, "--roles", "wf",
            ],
            cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
        )
        assert processed.returncode == 0, processed.stdout + processed.stderr
        input_set = processed_root / "locomotion"
        index_path = input_set / FIXTURE_VARIANT / "processed-index.json"
        index = json.loads(index_path.read_text(encoding="utf-8"))
        index["package"] = "idle"
        index_path.write_text(json.dumps(index), encoding="utf-8")
        descriptor = builder.runtime_descriptor(input_set, fixture / "runtime" / "idle" / FIXTURE_VARIANT, package_name="idle")
        descriptor_path = fixture / "descriptor.json"
        runtime_root = Path(descriptor["root"])
        descriptor_path.write_text(json.dumps({
            key: "res://" + str(Path(value).relative_to(GAME)) if key in {"index", "blend", "root"} else value
            for key, value in descriptor.items()
        }), encoding="utf-8")
        first_import = import_project()
        assert first_import.returncode == 0, first_import.stdout + first_import.stderr
        configured = run_godot(CONFIGURE, descriptor_path)
        assert configured.returncode == 0, configured.stdout + configured.stderr
        reimported = import_project()
        assert reimported.returncode == 0, reimported.stdout + reimported.stderr
        global LIBRARY_SOURCE
        with runtime_stage_root("idle") as library_stage:
            staged_library = stage_runtime_library(descriptor_path, library_stage)
            LIBRARY_SOURCE = fixture / "library_source" / LIBRARY_FILENAME
            LIBRARY_SOURCE.parent.mkdir()
            shutil.copy2(staged_library, LIBRARY_SOURCE)
        intervals = write_planted_intervals(input_set / FIXTURE_VARIANT / "provenance", planted_intervals(
            character=descriptor["character"], package="idle", variant=FIXTURE_VARIANT, index=index, library=LIBRARY_SOURCE,
        ))
        declared = json.loads(descriptor_path.read_text(encoding="utf-8"))
        declared["planted_intervals"] = {
            "input": "res://" + str(intervals.relative_to(GAME)),
            "retained_input": "res://" + str(intervals.relative_to(GAME)),
            "storage": "catalogue_field",
        }
        descriptor_path.write_text(json.dumps(declared), encoding="utf-8")
        try:
            yield {"fixture": fixture, "input": input_set, "descriptor": descriptor_path, "runtime": runtime_root, "index": index_path, "intervals": intervals}
        finally:
            LIBRARY_SOURCE = None
            shutil.rmtree(fixture, ignore_errors=True)


def test_synthetic_package_fixture_cleanup_removes_its_publication_directory() -> None:
    fixture = Path(tempfile.mkdtemp(prefix="kimodo-publication-cleanup-", dir=GAME / "assets/testing"))
    (fixture / "expected-residue").mkdir()

    shutil.rmtree(fixture, ignore_errors=True)

    assert not fixture.exists()


def seed_output(path: Path) -> None:
    (path / "clips").mkdir(parents=True)
    (path / "sentinel.bin").write_bytes(b"synthetic previous package")
    (path / "clips" / "obsolete.res").write_bytes(b"synthetic stale clip")
    (path / INTERVALS_FILENAME).write_text("synthetic stale intervals\n", encoding="utf-8")
    (path / f"{LIBRARY_FILENAME}.uid").write_text("uid://syntheticfixture\n", encoding="utf-8")


def test_packager_refuses_stale_or_mismatched_planted_intervals_before_staging(
    synthetic_package: dict[str, Any],
) -> None:
    intervals = synthetic_package["intervals"]
    output = synthetic_package["runtime"]
    original = intervals.read_bytes()
    mutations = [
        (lambda d: d.__setitem__("schema_version", 2), "schema version is unknown or mismatched"),
        (lambda d: d["derivation"].__setitem__("tool_version", 99), "tool or version is unknown or mismatched"),
        (lambda d: d["derivation"]["policy"].__setitem__("sha256", "0" * 64), "policy is stale or mismatched"),
        (lambda d: d["derivation"].__setitem__("runtime_library_sha256", "0" * 64), "derived from a different runtime library"),
        (lambda d: d["clips"][0]["feet"].pop("right"), "must cover exactly both feet"),
        (lambda d: d["clips"][0]["feet"]["left"]["segments"][0].__setitem__("end_seconds", 0.01), "cover the whole clip"),
        (lambda d: d["clips"][0].__setitem__("capability", "exit_forward_left"), "role, capability, or loop intent differs"),
        (lambda d: d.__setitem__("clips", []), "does not cover every package clip"),
    ]
    try:
        for mutate, diagnostic in mutations:
            document = json.loads(original)
            mutate(document)
            intervals.write_text(json.dumps(document), encoding="utf-8")
            shutil.rmtree(output, ignore_errors=True)
            seed_output(output)
            before = tree_hashes(output)
            with runtime_stage_root("idle") as stage:
                rejected = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
                assert rejected.returncode != 0
                assert diagnostic in rejected.stdout + rejected.stderr
                assert not stage.exists()
            assert tree_hashes(output) == before
        intervals.unlink()
        with runtime_stage_root("idle") as stage:
            missing = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert missing.returncode != 0
            assert "Planted-interval input is missing" in missing.stdout + missing.stderr
    finally:
        intervals.write_bytes(original)


def test_actual_sidecar_configuration_reimport_and_generic_idle_catalogue(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
        assert result.returncode == 0, result.stdout + result.stderr
        assert not output.exists()
        publish_disposable_fixture_stage(stage, output)
    validated = run_godot(VALIDATE, synthetic_package["descriptor"])
    assert validated.returncode == 0, validated.stdout + validated.stderr
    document = json.loads((synthetic_package["runtime"] / CATALOGUE_FILENAME).read_text(encoding="utf-8"))
    index = json.loads(synthetic_package["index"].read_text(encoding="utf-8"))
    assert document["schema_version"] == 4
    assert document["package"] == index["package"]
    assert document["package_variant"] == index["variant"]
    assert document["animation_library"] == LIBRARY_FILENAME
    assert document["clip_count"] == len(index["actions"])
    assert all("clip" not in clip for clip in document["clips"])
    assert all("processed_index" not in clip and "metrics" not in clip for clip in document["clips"])
    assert all(set(clip["diagnostic_provenance_non_runtime_authoritative"]) == {"root_source", "processed_index", "metrics", "source_action"} for clip in document["clips"])
    for clip in document["clips"]:
        expected_applicability = "inapplicable" if clip["boundary_kind"] == "confined_manoeuvre" else "applicable"
        assert clip["stitch_mappings"]["applicability"] == expected_applicability
    assert "Validated ANIM-002" in validated.stdout
    assert not (synthetic_package["runtime"] / INTERVALS_FILENAME).exists()
    assert document[INTERVALS_FIELD] == json.loads(synthetic_package["intervals"].read_text(encoding="utf-8"))
    assert_catalogue_rejects_unsupported_fields(
        synthetic_package["runtime"] / CATALOGUE_FILENAME, synthetic_package["descriptor"], EMBEDDED_INTERVAL_CATALOGUE_KEYS,
    )


def test_locomotion_package_publishes_its_planted_intervals_as_a_package_file(synthetic_package: dict[str, Any]) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    runtime_root = synthetic_package["fixture"] / "runtime" / "locomotion" / FIXTURE_VARIANT
    document = json.loads(synthetic_package["intervals"].read_text(encoding="utf-8"))
    document["package"] = "locomotion"
    locomotion_intervals = write_planted_intervals(synthetic_package["fixture"] / "locomotion_intervals", document)
    descriptor["name"] = "locomotion"
    descriptor["root"] = "res://" + str(runtime_root.relative_to(GAME))
    descriptor["publication_mode"] = "planted-interval-file-locomotion"
    descriptor["planted_intervals"] = {
        "input": "res://" + str(locomotion_intervals.relative_to(GAME)),
        "retained_input": "res://" + str(locomotion_intervals.relative_to(GAME)),
        "storage": "package_file",
        "runtime_path": INTERVALS_FILENAME,
    }
    locomotion_descriptor = synthetic_package["fixture"] / "locomotion_descriptor.json"
    locomotion_descriptor.write_text(json.dumps(descriptor), encoding="utf-8")
    library_source = synthetic_package["fixture"] / "locomotion_library_source" / f"locomotion_{FIXTURE_VARIANT}.tres"
    library_source.parent.mkdir(exist_ok=True)
    shutil.copy2(LIBRARY_SOURCE, library_source)

    shutil.rmtree(runtime_root, ignore_errors=True)
    with runtime_stage_root("locomotion") as stage:
        packaged = run_godot(
            PACKAGE, locomotion_descriptor, stage_root_argument(stage),
            f"--runtime-library-source=res://{library_source.relative_to(GAME)}",
        )
        assert packaged.returncode == 0, packaged.stdout + packaged.stderr
        assert not runtime_root.exists()
        publish_disposable_fixture_stage(stage, runtime_root)
    validated = run_godot(VALIDATE, locomotion_descriptor)
    assert validated.returncode == 0, validated.stdout + validated.stderr
    assert (runtime_root / INTERVALS_FILENAME).read_bytes() == locomotion_intervals.read_bytes()
    assert_catalogue_rejects_unsupported_fields(
        runtime_root / f"locomotion_{FIXTURE_VARIANT}_catalogue.json", locomotion_descriptor, FILE_INTERVAL_CATALOGUE_KEYS,
    )
    runtime_intervals = runtime_root / INTERVALS_FILENAME
    original = runtime_intervals.read_bytes()
    try:
        runtime_intervals.write_bytes(original + b" ")
        diverged = run_godot(VALIDATE, locomotion_descriptor)
        assert diverged.returncode != 0
        assert "Published planted intervals differ from the declared input" in diverged.stdout + diverged.stderr
    finally:
        runtime_intervals.write_bytes(original)


@pytest.mark.parametrize("mutation, message", [
    (lambda descriptor: descriptor["planted_intervals"].__setitem__("storage", "package_file"), "storage package_file is invalid for the idle package"),
    (lambda descriptor: descriptor.pop("planted_intervals"), "requires an explicit planted-interval declaration"),
    (lambda descriptor: descriptor["planted_intervals"].__setitem__("retained_input", "res://temp/intervals/" + INTERVALS_FILENAME), "durable retained_input"),
])
def test_idle_package_refuses_an_invalid_planted_interval_declaration(
    synthetic_package: dict[str, Any], mutation: Callable[[dict[str, Any]], None], message: str,
) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    mutation(descriptor)
    rejected_descriptor = synthetic_package["fixture"] / "invalid_idle_interval_descriptor.json"
    rejected_descriptor.write_text(json.dumps(descriptor), encoding="utf-8")

    with runtime_stage_root("idle") as stage:
        rejected = run_godot(PACKAGE, rejected_descriptor, stage_root_argument(stage))

    assert rejected.returncode != 0
    assert message in rejected.stdout + rejected.stderr


@pytest.mark.parametrize("mode", ["planted-interval-file-locomotion", "unsupported-idle-mode"])
def test_validator_refuses_a_publication_mode_reserved_for_another_package(synthetic_package: dict[str, Any], mode: str) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    descriptor["publication_mode"] = mode
    rejected_descriptor = synthetic_package["fixture"] / "invalid_mode_descriptor.json"
    rejected_descriptor.write_text(json.dumps(descriptor), encoding="utf-8")

    rejected = run_godot(VALIDATE, rejected_descriptor)

    assert rejected.returncode != 0
    assert "publication mode" in (rejected.stdout + rejected.stderr).lower()


@pytest.mark.parametrize("mutation, message", [
    (lambda document: document.__setitem__("schema_version", 2), "Runtime catalogue schema is unsupported"),
    (lambda document: document.__setitem__("schema_version", 9), "Runtime catalogue schema is unsupported"),
    (lambda document: document.__setitem__("animation_library", "res://elsewhere/idle_library.tres"), "Runtime catalogue library link is invalid"),
    (lambda document: document["clips"][0].__setitem__("clip", "res://assets/testing/clips/idle.res"), "standalone-clip path field"),
    (lambda document: document["clips"][0]["diagnostic_provenance_non_runtime_authoritative"].pop("metrics"), "Diagnostic provenance is missing or malformed"),
    (lambda document: document["clips"][0].__setitem__("metrics", "misplaced"), "Diagnostic provenance is misplaced"),
    (lambda document: document["clips"][0].__setitem__("identity", "incorrect"), "Runtime catalogue identity is invalid"),
    (lambda document: document["clips"][0].pop("capability"), "Runtime catalogue capability is missing, duplicated, or differs from the processed action"),
])
def test_generic_catalogue_rejects_schema_provenance_and_identity_misplacement(synthetic_package: dict[str, Any], mutation: Callable[[dict[str, Any]], None], message: str) -> None:
    catalogue = synthetic_package["runtime"] / CATALOGUE_FILENAME
    original = catalogue.read_bytes()
    try:
        document = json.loads(original)
        mutation(document)
        catalogue.write_text(json.dumps(document), encoding="utf-8")
        result = run_godot(VALIDATE, synthetic_package["descriptor"])
        assert result.returncode != 0
        assert message in result.stdout + result.stderr
    finally:
        catalogue.write_bytes(original)


@pytest.mark.parametrize("kind", ["missing", "stale", "malformed"])
def test_coordinate_preflight_rejection_preserves_previous_output(synthetic_package: dict[str, Any], kind: str) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    original = synthetic_package["index"].read_bytes()
    try:
        document = json.loads(original)
        if kind == "missing":
            document.pop("coordinate_contract")
        elif kind == "stale":
            document["coordinate_contract"]["version"] = "obsolete"
        else:
            document["coordinate_contract"]["blender_rest"]["basis_columns"] = [[1.0]]
        synthetic_package["index"].write_text(json.dumps(document), encoding="utf-8")
        before = tree_hashes(output)
        with runtime_stage_root("idle") as stage:
            result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert result.returncode != 0
            assert tree_hashes(output) == before
    finally:
        synthetic_package["index"].write_bytes(original)


def test_staged_validation_failure_preserves_previous_output_and_cleans_transaction(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    before = tree_hashes(output)
    with runtime_stage_root("idle") as stage:
        result = run_godot(
            PACKAGE,
            synthetic_package["descriptor"],
            stage_root_argument(stage),
            "--inject-staged-validation-failure",
        )
        assert result.returncode != 0
        assert tree_hashes(output) == before
    assert not list(output.parent.glob(f".{output.name}.staging-*"))
    assert not list(output.parent.glob(f".{output.name}.backup-*"))


def test_packager_requires_a_safe_explicit_stage_and_preserves_the_production_root(
    synthetic_package: dict[str, Any],
) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    before = tree_hashes(output)

    for argument, message in (
        (None, "Runtime staging root must be explicitly supplied"),
        ("--stage-root=", "Runtime staging root must be explicitly supplied"),
        ("--stage-root=res://temp/../assets/testing/unsafe/runtime/idle/" + FIXTURE_VARIANT, "Runtime staging root must resolve beneath res://temp"),
        ("--stage-root=res://temp/kimodo-unsafe/idle", "Runtime staging root must end in runtime/idle/" + FIXTURE_VARIANT),
        ("--stage-root=res://temp/kimodo-unsafe/runtime/idle", "Runtime staging root must end in runtime/idle/" + FIXTURE_VARIANT),
    ):
        result = (
            run_godot(PACKAGE, synthetic_package["descriptor"])
            if argument is None
            else run_godot(PACKAGE, synthetic_package["descriptor"], argument)
        )
        assert result.returncode != 0
        assert message in result.stdout + result.stderr
        assert tree_hashes(output) == before

    stage_parent = Path(tempfile.mkdtemp(prefix="kimodo-explicit-stage-", dir=GAME / "temp"))
    stage = stage_parent / "runtime" / "idle" / FIXTURE_VARIANT
    try:
        prepared = run_godot(
            PACKAGE,
            synthetic_package["descriptor"],
            "--stage-root=res://" + str(stage.relative_to(GAME)),
        )
        assert prepared.returncode == 0, prepared.stdout + prepared.stderr
        assert "ANIM002_RUNTIME_STAGE_PREPARED=" in prepared.stdout
        assert (stage / LIBRARY_FILENAME).is_file()
        assert (stage / CATALOGUE_FILENAME).is_file()
        assert not (stage / "clips").is_dir()
        assert tree_hashes(output) == before
    finally:
        shutil.rmtree(stage_parent, ignore_errors=True)


def test_disabled_normalisation_rejects_before_publication(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    blend = next(synthetic_package["input"].glob("*.blend"))
    sidecar = Path(str(blend) + ".import")
    mutator = synthetic_package["fixture"] / "disable_normalisation.gd"
    write_mutator(mutator, "\tvar c := ConfigFile.new(); c.load(\"res://%s\"); var s: Dictionary = c.get_value(\"params\", \"_subresources\", {}); var n: Dictionary = s.get(\"nodes\", {}); var v: Dictionary = n.get(\"PATH:Female/Skeleton3D\", {}); v[\"retarget/rest_fixer/normalize_position_tracks\"] = false; n[\"PATH:Female/Skeleton3D\"] = v; s[\"nodes\"] = n; c.set_value(\"params\", \"_subresources\", s); c.save(\"res://%s\")" % (sidecar.relative_to(GAME), sidecar.relative_to(GAME)))
    mutation = subprocess.run(["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "-s", "res://" + str(mutator.relative_to(GAME))], cwd=ROOT, text=True, capture_output=True, check=False, timeout=180)
    assert mutation.returncode == 0, mutation.stdout + mutation.stderr
    before = tree_hashes(output)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
        assert result.returncode != 0
        assert "disabled position-track normalisation" in result.stdout + result.stderr
        assert tree_hashes(output) == before
    configured = run_godot(CONFIGURE, synthetic_package["descriptor"])
    assert configured.returncode == 0, configured.stdout + configured.stderr
    assert import_project().returncode == 0


@pytest.mark.parametrize("externalisation", ["per_action", "slice", "global"])
def test_externalising_import_configuration_is_rejected_before_staging(
    synthetic_package: dict[str, Any], externalisation: str,
) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    index = json.loads(synthetic_package["index"].read_text(encoding="utf-8"))
    action = index["actions"][0]["action"]
    blend = next(synthetic_package["input"].glob("*.blend"))
    sidecar = Path(str(blend) + ".import")
    mutator = synthetic_package["fixture"] / f"externalise_{externalisation}.gd"
    if externalisation == "per_action":
        statement = 'var a: Dictionary = s.get("animations", {}); var b: Dictionary = a.get("%s", {}); b["save_to_file/enabled"] = true; b["save_to_file/fallback_path"] = "res://temp/externalised.res"; a["%s"] = b; s["animations"] = a' % (action, action)
    elif externalisation == "slice":
        statement = 'var a: Dictionary = s.get("animations", {}); var b: Dictionary = a.get("%s", {}); b["slice_1/save_to_file/enabled"] = true; a["%s"] = b; s["animations"] = a' % (action, action)
    else:
        statement = 'c.set_value("params", "animation/save_to_file/enabled", true)'
    write_mutator(mutator, '\tvar c := ConfigFile.new(); c.load("res://%s"); var s: Dictionary = c.get_value("params", "_subresources", {}); %s; c.set_value("params", "_subresources", s); c.save("res://%s")' % (sidecar.relative_to(GAME), statement, sidecar.relative_to(GAME)))
    mutation = subprocess.run(["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "-s", "res://" + str(mutator.relative_to(GAME))], cwd=ROOT, text=True, capture_output=True, check=False, timeout=180)
    assert mutation.returncode == 0, mutation.stdout + mutation.stderr
    before = tree_hashes(output)
    try:
        with runtime_stage_root("idle") as stage:
            rejected = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert rejected.returncode != 0
            assert "embedded-only packages require save_to_file disabled" in rejected.stdout + rejected.stderr
            assert tree_hashes(output) == before
        rejected_validation = run_godot(VALIDATE, synthetic_package["descriptor"])
        assert rejected_validation.returncode != 0
        assert "externalises animation" in rejected_validation.stdout + rejected_validation.stderr
    finally:
        configured = run_godot(CONFIGURE, synthetic_package["descriptor"])
        assert configured.returncode == 0, configured.stdout + configured.stderr
        assert import_project().returncode == 0


def test_configure_strips_stale_externalisation_blocks_from_the_import_sidecar(synthetic_package: dict[str, Any]) -> None:
    blend = next(synthetic_package["input"].glob("*.blend"))
    sidecar = Path(str(blend) + ".import")
    index = json.loads(synthetic_package["index"].read_text(encoding="utf-8"))
    action = index["actions"][0]["action"]
    mutator = synthetic_package["fixture"] / "seed_externalisation.gd"
    write_mutator(mutator, '\tvar c := ConfigFile.new(); c.load("res://%s"); var s: Dictionary = c.get_value("params", "_subresources", {}); var a: Dictionary = s.get("animations", {}); var b: Dictionary = a.get("%s", {}); b["save_to_file/enabled"] = true; b["save_to_file/path"] = "uid://stalefixture"; b["save_to_file/fallback_path"] = "res://assets/legacy/clips/stale.res"; b["slice_1/save_to_file/enabled"] = true; b["slice_1/save_to_file/fallback_path"] = "res://assets/legacy/clips/slice.res"; a["%s"] = b; s["animations"] = a; c.set_value("params", "_subresources", s); c.save("res://%s")' % (sidecar.relative_to(GAME), action, action, sidecar.relative_to(GAME)))
    mutation = subprocess.run(["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "-s", "res://" + str(mutator.relative_to(GAME))], cwd=ROOT, text=True, capture_output=True, check=False, timeout=180)
    assert mutation.returncode == 0, mutation.stdout + mutation.stderr
    seeded = sidecar.read_text(encoding="utf-8")
    assert '"save_to_file/enabled": true' in seeded

    configured = run_godot(CONFIGURE, synthetic_package["descriptor"])

    assert configured.returncode == 0, configured.stdout + configured.stderr
    text = sidecar.read_text(encoding="utf-8")
    assert '"save_to_file/enabled": true' not in text
    assert "res://assets/legacy/clips/stale.res" not in text
    assert "res://assets/legacy/clips/slice.res" not in text
    assert import_project().returncode == 0


def test_second_variant_publication_is_refused_before_staging(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    runtime_set = output.parent
    sibling = runtime_set / "zephyr"
    processed_set = synthetic_package["input"]
    processed_sibling = processed_set / "zephyr"
    sibling.mkdir(parents=True)
    (sibling / "zephyr_catalogue.json").write_text("{}", encoding="utf-8")
    processed_sibling.mkdir()
    (processed_sibling / "processed-index.json").write_text("{}", encoding="utf-8")
    before = tree_hashes(output)
    try:
        with runtime_stage_root("idle") as stage:
            result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert result.returncode != 0
            combined = result.stdout + result.stderr
            assert "multi-variant publication is not ratified" in combined
            assert "zephyr" in combined
            assert tree_hashes(output) == before
            assert not stage.exists()
    finally:
        shutil.rmtree(sibling, ignore_errors=True)
        shutil.rmtree(processed_sibling, ignore_errors=True)


def test_successful_replacement_removes_stale_files_and_transaction_remnants(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    before = tree_hashes(output)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
        assert result.returncode == 0, result.stdout + result.stderr
        assert tree_hashes(output) == before
        publish_disposable_fixture_stage(stage, output)
    index = json.loads(synthetic_package["index"].read_text(encoding="utf-8"))
    assert not (output / "sentinel.bin").exists()
    assert not (output / "clips").exists()
    assert (output / f"{LIBRARY_FILENAME}.uid").read_text(encoding="utf-8") == "uid://syntheticfixture\n"
    assert not (output / INTERVALS_FILENAME).exists()
    assert json.loads((output / CATALOGUE_FILENAME).read_text(encoding="utf-8"))[INTERVALS_FIELD] == json.loads(
        synthetic_package["intervals"].read_text(encoding="utf-8"))
    assert not list(output.rglob("*.res"))
    assert not list(output.parent.glob(f".{output.name}.staging-*"))
    assert not list(output.parent.glob(f".{output.name}.backup-*"))


def test_embedded_only_stage_conforms_to_processed_index_frame_math(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
        assert result.returncode == 0, result.stdout + result.stderr
        publish_disposable_fixture_stage(stage, output)
    index = json.loads(synthetic_package["index"].read_text(encoding="utf-8"))
    document = json.loads((output / CATALOGUE_FILENAME).read_text(encoding="utf-8"))
    expected_files = {LIBRARY_FILENAME, CATALOGUE_FILENAME}
    actual_files = {item.name for item in output.iterdir() if item.is_file()} - {f"{LIBRARY_FILENAME}.uid"}
    assert actual_files == expected_files
    assert not (output / "clips").exists()
    frame_math = {entry["external_identity"]: entry["transport"]["duration_seconds"] for entry in index["actions"]}
    assert {clip["identity"] for clip in document["clips"]} == set(frame_math)
    for clip in document["clips"]:
        assert abs(clip["duration_seconds"] - frame_math[clip["identity"]]) <= DURATION_TOLERANCE
        assert abs(clip["transport"]["duration_seconds"] - frame_math[clip["identity"]]) <= DURATION_TOLERANCE
    # Binary-safety guard: no staged artefact may carry UTF-8 replacement bytes.
    for item in output.rglob("*"):
        if item.is_file():
            assert b"\xef\xbf\xbd" not in item.read_bytes(), item


def test_cold_import_project_loads_the_package_without_uid_registration(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
        assert result.returncode == 0, result.stdout + result.stderr
        publish_disposable_fixture_stage(stage, output)

    scratch = Path(tempfile.mkdtemp(prefix="kimodo-cold-import-"))
    try:
        (scratch / "project.godot").write_text(
            'config_version=5\n\n[application]\n\nconfig/name="KimodoColdImportProbe"\n',
            encoding="utf-8",
        )
        package_root = scratch / "runtime" / "idle" / FIXTURE_VARIANT
        shutil.copytree(output, package_root, ignore=shutil.ignore_patterns("*.uid"))
        assert not list(scratch.rglob("*.uid"))
        imported = subprocess.run(
            ["godot-mono", "--headless", "--xr-mode", "off", "--path", str(scratch), "--import"],
            cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
        )
        assert imported.returncode == 0, imported.stdout + imported.stderr
        loader = scratch / "probe_loader.gd"
        loader.write_text(
            "extends SceneTree\n"
            "func _init() -> void:\n"
            '\tvar library := ResourceLoader.load("res://runtime/idle/' + FIXTURE_VARIANT + "/" + LIBRARY_FILENAME + '", "AnimationLibrary", ResourceLoader.CACHE_MODE_REPLACE) as AnimationLibrary\n'
            "\tif library == null:\n"
            '\t\tprint("COLD_IMPORT_FAILED=library")\n'
            "\t\tquit(1)\n"
            "\t\treturn\n"
            "\tvar report := {}\n"
            "\tfor identity in library.get_animation_list():\n"
            '\t\treport[String(identity)] = library.get_animation(String(identity)).length\n'
            '\tprint("COLD_IMPORT_LIBRARY=" + JSON.stringify(report))\n'
            "\tquit(0)\n",
            encoding="utf-8",
        )
        loaded = subprocess.run(
            ["godot-mono", "--headless", "--xr-mode", "off", "--path", str(scratch), "--script", str(loader)],
            cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
        )
        assert loaded.returncode == 0, loaded.stdout + loaded.stderr
        report_line = next(line for line in loaded.stdout.splitlines() if line.startswith("COLD_IMPORT_LIBRARY="))
        report = json.loads(report_line.removeprefix("COLD_IMPORT_LIBRARY="))
        index = json.loads(synthetic_package["index"].read_text(encoding="utf-8"))
        expected = {entry["external_identity"]: entry["transport"]["duration_seconds"] for entry in index["actions"]}
        assert report.keys() == expected.keys()
        for identity, duration in expected.items():
            assert abs(report[identity] - duration) <= DURATION_TOLERANCE
    finally:
        shutil.rmtree(scratch, ignore_errors=True)


def test_synthetic_imported_source_runtime_mismatch_is_rejected(synthetic_package: dict[str, Any]) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
        assert result.returncode == 0, result.stdout + result.stderr
        assert not output.exists()
        publish_disposable_fixture_stage(stage, output)
    library = synthetic_package["runtime"] / LIBRARY_FILENAME
    mutator = synthetic_package["fixture"] / "corrupt_runtime.gd"
    write_mutator(mutator, "\tvar library := ResourceLoader.load(\"res://%s\", \"AnimationLibrary\") as AnimationLibrary; var identity := library.get_animation_list()[0]; var animation := library.get_animation(identity); var track := animation.find_track(NodePath(\"%%GeneralSkeleton:Root\"), Animation.TYPE_POSITION_3D); animation.track_set_key_value(track, 0, Vector3(99.0, 0.0, 0.0)); ResourceSaver.save(library, \"res://%s\")" % (library.relative_to(GAME), library.relative_to(GAME)))
    mutation = subprocess.run(["godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "-s", "res://" + str(mutator.relative_to(GAME))], cwd=ROOT, text=True, capture_output=True, check=False, timeout=180)
    assert mutation.returncode == 0, mutation.stdout + mutation.stderr
    rejected = run_godot(VALIDATE, synthetic_package["descriptor"])
    assert rejected.returncode != 0
    assert "correspondence differs" in rejected.stdout + rejected.stderr


def test_fixture_is_declared_and_content_independent(synthetic_package: dict[str, Any]) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    assert descriptor["fixture_schema"] == FIXTURE_SCHEMA
    assert descriptor["fixture_variant"] == FIXTURE_VARIANT
