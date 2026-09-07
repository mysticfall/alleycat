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

from synthetic_kimodo_blend import FIXTURE_SCHEMA, FIXTURE_VARIANT, SyntheticKimodoBlendBuilder


GODOT_COMMANDS = ROOT / "tools/kimodo-pipeline/godot/commands"
GODOT_TESTS = ROOT / "tools/kimodo-pipeline/tests/godot"
CONFIGURE = GODOT_COMMANDS / "configure_kimodo_import.gd"
PACKAGE = GODOT_COMMANDS / "package_kimodo_runtime.gd"
VALIDATE = GODOT_COMMANDS / "validate_kimodo_runtime_package.gd"
STITCH_GRAPH_TEST = GODOT_TESTS / "stitch_graph_test.gd"
SAMPLE_TRANSPORT_TEST = GODOT_TESTS / "sample_transport_test.gd"
RETARGET = TOOLS / "kimodo-pipeline/commands/retarget_animations.py"
CONTACT_FILENAME = "authored_per_foot_" + "contacts_v1.json"
LIBRARY_FILENAME = f"idle_{FIXTURE_VARIANT}.tres"
CATALOGUE_FILENAME = f"idle_{FIXTURE_VARIANT}_catalogue.json"
DURATION_TOLERANCE = 1e-6


def run_godot(script: Path, descriptor: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            "godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script", str(script),
            "--", f"--descriptor=res://{descriptor.relative_to(GAME)}", *extra,
        ],
        cwd=ROOT, text=True, capture_output=True, check=False, timeout=180,
    )


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
        entry = index["actions"][0]
        package_identity = "synthetic-contact-package-v1"
        generation = "synthetic-contact-generation-v1"
        contact_source = fixture / "declared_contacts.json"
        contact_source.write_text(json.dumps({
            "schema_version": 1,
            "package_identity": package_identity,
            "source_derived": {
                "schema_version": 1,
                "method": "synthetic_declared_source_geometry_v1",
                "generation": generation,
                "source_package": {"package": "synthetic", "variant": FIXTURE_VARIANT},
                "paths_relative_to_processed_package": {"processed_index": "processed-index.json", "retargeted_blend": "declared.blend"},
                "input_digests": {"processed_index_sha256": "fixture", "retargeted_blend_sha256": "fixture"},
                "mapping": {"identity": "synthetic", "feet": {"left": "declared", "right": "declared"}},
                "sample_rate_hz": 30.0,
                "configuration": {"declared": True},
                "clips": [{"identity": entry["external_identity"], "evidence_windows": {"left": [], "right": []}}],
                "uncertainty_reasons": [],
            },
            "samples": [{
                "schema_version": 1,
                "role": entry["role"],
                "foot": "left",
                "lifecycle": "support",
                "timing": [0.0, 1.0],
                "validity": "valid",
                "confidence": 1.0,
                "provenance": "synthetic fixture upstream declaration",
                "clip": entry["external_identity"],
                "variant": index["variant"],
                "reference": "synthetic declared fixture reference",
                "identity": entry["external_identity"],
                "generation": generation,
            }],
        }, indent=2) + "\n", encoding="utf-8")
        descriptor["contact_evidence"] = {
            "input": str(contact_source),
            "runtime_path": CONTACT_FILENAME,
            "package_identity": package_identity,
            "generation": generation,
        }
        descriptor_path.write_text(json.dumps({
            **{key: "res://" + str(Path(value).relative_to(GAME)) if key in {"index", "blend", "root"} else value for key, value in descriptor.items() if key != "contact_evidence"},
            "contact_evidence": {
                **descriptor["contact_evidence"],
                "input": "res://" + str(contact_source.relative_to(GAME)),
            },
        }), encoding="utf-8")
        first_import = import_project()
        assert first_import.returncode == 0, first_import.stdout + first_import.stderr
        configured = run_godot(CONFIGURE, descriptor_path)
        assert configured.returncode == 0, configured.stdout + configured.stderr
        reimported = import_project()
        assert reimported.returncode == 0, reimported.stdout + reimported.stderr
        yield {"fixture": fixture, "input": input_set, "descriptor": descriptor_path, "runtime": runtime_root, "index": index_path, "contacts": contact_source}


def seed_output(path: Path) -> None:
    (path / "clips").mkdir(parents=True)
    (path / "sentinel.bin").write_bytes(b"synthetic previous package")
    (path / "clips" / "obsolete.res").write_bytes(b"synthetic stale clip")
    (path / CONTACT_FILENAME).write_text("synthetic stale contacts\n", encoding="utf-8")
    (path / f"{LIBRARY_FILENAME}.uid").write_text("uid://syntheticfixture\n", encoding="utf-8")


def phase_seeded_source_derived(generation: str, legacy: dict[str, Any]) -> dict[str, Any]:
    return {
        "schema_version": 5,
        "method": "phase_seeded_relative_motion_segmentation_v2",
        "generation": generation,
        "publication_eligible": True,
        "required_refusals": [],
        "source_package": legacy["source_package"],
        "paths_relative_to_processed_package": legacy["paths_relative_to_processed_package"],
        "input_digests": legacy["input_digests"],
        "phase_policy": {"identity": "fixture-policy", "digest": "fixture"},
        "bone_mapping": {"identity": "fixture-mapping", "digest": "fixture"},
        "input_contracts": [
            "anim002_source_directional_phase_events_v1",
            "anim002_event_derivation_v1",
            "anim002_post_repair_directional_phase_events_v2",
            "anim002_selected_loop_repair_provenance_v2",
        ],
        "phase_seed_policy": {
            "detector_influence_samples": 4,
            "two_release_sector": "cyclic_voronoi_midpoints",
            "one_release_sector": "half_cycle_around_repeated_seed",
            "absolute_world_slide_affects_eligibility": False,
        },
        "sampling": "native_fps",
        "configuration": {"minimum_confidence": 0.8},
        "configuration_digest": "fixture",
        "confidence_weights": {
            "semantic_event_anchor_agreement": 0.22,
            "within_state_cohesion": 0.18,
            "between_state_separation": 0.22,
            "transition_persistence_sharpness": 0.16,
            "contextual_opposite_consistency": 0.12,
            "ambiguity_margin": 0.10,
        },
        "clips": legacy["clips"],
        "role_contact_matrix": [],
    }


def test_godot_accepts_phase_seeded_schema_v5_and_rejects_stale_v4_contract(
    synthetic_package: dict[str, Any],
) -> None:
    contacts = synthetic_package["contacts"]
    output = synthetic_package["runtime"]
    original = contacts.read_bytes()
    try:
        document = json.loads(original)
        document["source_derived"] = phase_seeded_source_derived(
            document["source_derived"]["generation"], document["source_derived"],
        )
        contacts.write_text(json.dumps(document), encoding="utf-8")
        shutil.rmtree(output, ignore_errors=True)
        with runtime_stage_root("idle") as stage:
            accepted = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert accepted.returncode == 0, accepted.stdout + accepted.stderr
            assert not output.exists()

        stale = document["source_derived"]
        stale["schema_version"] = 4
        stale["method"] = "phase_guided_relative_motion_segmentation_v1"
        stale["input_contracts"][2] = "anim002_post_repair_physical_events_v1"
        stale["cyclic_boundary_policy"] = stale.pop("phase_seed_policy")
        contacts.write_text(json.dumps(document), encoding="utf-8")
        shutil.rmtree(output, ignore_errors=True)
        with runtime_stage_root("idle") as stage:
            rejected = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert rejected.returncode != 0
            assert "source-derived provenance is malformed" in rejected.stdout + rejected.stderr
    finally:
        contacts.write_bytes(original)


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
    assert document["schema_version"] == 3
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
    assert (synthetic_package["runtime"] / CONTACT_FILENAME).read_bytes() == synthetic_package["contacts"].read_bytes()
    assert document["contact_evidence"]["identities"] == [index["actions"][0]["external_identity"]]


def test_contact_evidenced_locomotion_mode_packages_and_validates_a_synthetic_stage(synthetic_package: dict[str, Any]) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    runtime_root = synthetic_package["fixture"] / "runtime" / "locomotion" / FIXTURE_VARIANT
    contacts = json.loads(synthetic_package["contacts"].read_text(encoding="utf-8"))
    assert all(sample["clip"] == sample["identity"] for sample in contacts["samples"])
    contact_source = synthetic_package["fixture"] / "locomotion_contacts.json"
    contact_source.write_text(json.dumps(contacts), encoding="utf-8")
    descriptor["name"] = "locomotion"
    descriptor["root"] = "res://" + str(runtime_root.relative_to(GAME))
    descriptor["publication_mode"] = "contact-evidenced-locomotion"
    descriptor["contact_evidence_required"] = True
    descriptor["contact_evidence"]["input"] = "res://" + str(contact_source.relative_to(GAME))
    locomotion_descriptor = synthetic_package["fixture"] / "locomotion_descriptor.json"
    locomotion_descriptor.write_text(json.dumps(descriptor), encoding="utf-8")

    shutil.rmtree(runtime_root, ignore_errors=True)
    with runtime_stage_root("locomotion") as stage:
        packaged = run_godot(PACKAGE, locomotion_descriptor, stage_root_argument(stage))
        assert packaged.returncode == 0, packaged.stdout + packaged.stderr
        assert not runtime_root.exists()
        publish_disposable_fixture_stage(stage, runtime_root)
    validated = run_godot(VALIDATE, locomotion_descriptor)
    assert validated.returncode == 0, validated.stdout + validated.stderr


def test_idle_descriptor_does_not_require_or_mutate_contacts_when_undeclared(synthetic_package: dict[str, Any]) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    descriptor.pop("contact_evidence")
    descriptor["publication_mode"] = "contact-free-idle"
    alternate_descriptor = synthetic_package["fixture"] / "no_contacts_descriptor.json"
    alternate_descriptor.write_text(json.dumps(descriptor), encoding="utf-8")
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    before = tree_hashes(output)
    with runtime_stage_root("idle") as stage:
        result = run_godot(PACKAGE, alternate_descriptor, stage_root_argument(stage))
        assert result.returncode == 0, result.stdout + result.stderr
        assert tree_hashes(output) == before
        publish_disposable_fixture_stage(stage, output)
    validated = run_godot(VALIDATE, alternate_descriptor)
    assert validated.returncode == 0, validated.stdout + validated.stderr
    assert not (output / CONTACT_FILENAME).exists()


@pytest.mark.parametrize("mutation", [
    lambda descriptor: descriptor.__setitem__("contact_evidence_required", True),
    lambda descriptor: descriptor.__setitem__("contact_evidence", {"input": "declared.json"}),
])
def test_contact_free_idle_mode_refuses_declared_or_required_contact_evidence(
    synthetic_package: dict[str, Any], mutation: Callable[[dict[str, Any]], None],
) -> None:
    descriptor = json.loads(synthetic_package["descriptor"].read_text(encoding="utf-8"))
    descriptor.pop("contact_evidence")
    descriptor["publication_mode"] = "contact-free-idle"
    mutation(descriptor)
    rejected_descriptor = synthetic_package["fixture"] / "invalid_contact_free_idle_descriptor.json"
    rejected_descriptor.write_text(json.dumps(descriptor), encoding="utf-8")

    rejected = run_godot(VALIDATE, rejected_descriptor)

    assert rejected.returncode != 0
    assert "contact-free idle package must not declare or require contact evidence" in rejected.stdout + rejected.stderr


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


@pytest.mark.parametrize("kind, message", [
    ("mismatch", "Contact evidence identity is absent"),
    ("malformed", "Contact evidence sample has unsupported fields"),
    ("stale", "Contact evidence is stale"),
    ("missing", "Contact-evidence input is missing"),
    ("identity_clip", "Contact evidence clip is not the embedded library animation identity"),
])
def test_contact_preflight_rejection_preserves_previous_output(synthetic_package: dict[str, Any], kind: str, message: str) -> None:
    output = synthetic_package["runtime"]
    shutil.rmtree(output, ignore_errors=True)
    seed_output(output)
    original = synthetic_package["contacts"].read_bytes()
    try:
        if kind == "missing":
            synthetic_package["contacts"].unlink()
        else:
            document = json.loads(original)
            if kind == "mismatch":
                document["samples"][0]["identity"] = "unmapped_fixture_identity"
            elif kind == "malformed":
                document["samples"][0].pop("provenance")
            elif kind == "identity_clip":
                document["samples"][0]["clip"] = "res://assets/testing/legacy/clips/idle.res"
            else:
                document["samples"][0]["validity"] = "stale"
            synthetic_package["contacts"].write_text(json.dumps(document), encoding="utf-8")
        before = tree_hashes(output)
        with runtime_stage_root("idle") as stage:
            result = run_godot(PACKAGE, synthetic_package["descriptor"], stage_root_argument(stage))
            assert result.returncode != 0
            assert message in result.stdout + result.stderr
            assert tree_hashes(output) == before
    finally:
        synthetic_package["contacts"].write_bytes(original)


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
    assert (output / CONTACT_FILENAME).read_bytes() == synthetic_package["contacts"].read_bytes()
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
    expected_files = {LIBRARY_FILENAME, CATALOGUE_FILENAME, CONTACT_FILENAME}
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
