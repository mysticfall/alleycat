"""Exhaustively guard automated ANIM-002 tests from volatile authored content."""
from __future__ import annotations

import ast
from dataclasses import dataclass
from pathlib import Path

from kimodo_pipeline.bootstrap_paths import repository_root


TEST_ROOT = Path(__file__).resolve().parent
ROOT = repository_root()
SCANNED_ROOTS = (
    ROOT / "tests/src",
    ROOT / "integration-tests/src",
    ROOT / "tools",
)
FORBIDDEN_AUTHORED_EXPECTATIONS = (
    "locomotion_catalogue.json",
    "authored_per_foot_contacts_v1.json",
    "loc_sad_",
    "locomotion.blend",
    "locomotion_template.blend",
    "kimodo_retarget_setup.blend",
)
VISUAL_EVIDENCE_SOURCES = {
    "diagnose_precision_leg_crossover.py",
    "render_pivot_evidence.py",
    "render_strafe_evidence.py",
    "render_walk_seam_evidence.py",
}
EXTERNAL_RUNTIME_COMMANDS = {
    "tools/kimodo-pipeline/godot/commands/configure_kimodo_import.gd",
    "tools/kimodo-pipeline/godot/commands/package_kimodo_runtime.gd",
    "tools/kimodo-pipeline/godot/commands/validate_kimodo_runtime_package.gd",
}
ALLOWED_PYTHON_OCCURRENCES = {
    (
        "tools/kimodo-pipeline/kimodo_pipeline/bootstrap_extraction.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    ): "structural/path derivation",
    (
        "tools/kimodo-pipeline/kimodo_pipeline/bootstrap_paths.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion_template.blend",
    ): "structural/path derivation",
    (
        "tools/kimodo-pipeline/kimodo_pipeline/bootstrap_paths.py",
        "<module>",
        "animations/source/kimodo_retarget_setup.blend",
    ): "structural/path derivation",
    (
        "tools/kimodo-pipeline/kimodo_pipeline/contact_evidence.py",
        "<module>",
        "provenance/authored_per_foot_contacts_v1.json",
    ): "generator output contract",
    (
        "tools/kimodo-pipeline/kimodo_pipeline/contact_evidence.py",
        "build_descriptor",
        "authored_per_foot_contacts_v1.json",
    ): "generator descriptor contract",
    (
        "tools/kimodo-pipeline/tests/test_cross_package_publication.py",
        "test_contact_provenance_survives_in_the_published_processed_package",
        "authored_per_foot_contacts_v1.json",
    ): "synthetic publication/provenance fixture",
    (
        "tools/kimodo-pipeline/tests/test_cross_package_publication.py",
        "descriptor_mismatch_transaction",
        "locomotion_catalogue.json",
    ): "synthetic publication descriptor fixture",
    (
        "tools/kimodo-pipeline/tests/test_runtime_tool_capability_semantics.py",
        "<module>",
        "sad",
    ): "production tool fixture source variant (input staging, not an oracle)",
    (
        "tools/kimodo-pipeline/kimodo_pipeline/publication_manifest.py",
        "_plan",
        "authored_per_foot_contacts_v1.json",
    ): "publication manifest contract",
    (
        "tools/kimodo-pipeline/tests/test_runtime_package_descriptor.py",
        "fixture_package",
        "locomotion_catalogue.json",
    ): "synthetic package descriptor fixture",
    (
        "tools/kimodo-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    ): "structural/immutability smoke",
    (
        "tools/kimodo-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion_template.blend",
    ): "structural/immutability smoke",
    (
        "tools/kimodo-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend",
    ): "structural/immutability smoke",
}


@dataclass(frozen=True)
class IsolationOccurrence:
    """One volatile-content literal and its permitted isolation category."""

    path: Path
    symbol: str
    category: str


def _source_files() -> tuple[Path, ...]:
    return tuple(
        sorted(
            (
                path
                for root in SCANNED_ROOTS
                for path in (root.rglob("*") if root.is_dir() else (root,))
                if path.suffix in {".py", ".cs", ".gd", ".tscn"}
            ),
            key=lambda path: str(path.relative_to(ROOT)),
        )
    )


def _python_occurrences(path: Path) -> tuple[IsolationOccurrence, ...]:
    return _python_occurrences_from_source(path, path.read_text(encoding="utf-8"))


def _python_occurrences_from_source(path: Path, source: str) -> tuple[IsolationOccurrence, ...]:
    tree = ast.parse(source, filename=str(path))
    occurrences: list[IsolationOccurrence] = []

    def visit(node: ast.AST, symbol: str = "<module>") -> None:
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
            symbol = node.name if symbol == "<module>" else f"{symbol}.{node.name}"
        if isinstance(node, ast.Constant) and isinstance(node.value, str):
            if _contains_forbidden_authored_content(node.value):
                occurrences.append(IsolationOccurrence(path, symbol, _category_for(path, symbol, node.value)))
        for child in ast.iter_child_nodes(node):
            visit(child, symbol)

    visit(tree)
    return tuple(occurrences)


def _text_occurrences(path: Path) -> tuple[IsolationOccurrence, ...]:
    source = path.read_text(encoding="utf-8")
    if not _contains_forbidden_authored_content(source):
        return ()
    return (IsolationOccurrence(path, "<text>", _category_for(path, "<text>", source)),)


def _contains_forbidden_authored_content(value: str) -> bool:
    return any(expectation in value for expectation in FORBIDDEN_AUTHORED_EXPECTATIONS) or value == "sad"


def _category_for(path: Path, symbol: str, literal: str) -> str:
    relative = path.relative_to(ROOT).as_posix()
    if relative == "tools/kimodo-pipeline/tests/test_synthetic_fixture_isolation.py":
        return "static guard policy data"
    if relative == "integration-tests/src/AnimationCatalogue/RuntimeFixtureIsolationIntegrationTests.cs":
        return "static guard data"
    if relative == "integration-tests/src/Support/SyntheticLocomotionRuntimeFixture.cs":
        return (
            "unclassified"
            if "[Fact]" in literal or "Assert." in literal
            else "synthetic runtime fixture declaration"
        )
    if relative in EXTERNAL_RUNTIME_COMMANDS:
        return "production runtime command default path"
    if path.name in VISUAL_EVIDENCE_SOURCES:
        return "non-automated visual evidence (excluded from automated-test roots)"
    return ALLOWED_PYTHON_OCCURRENCES.get((relative, symbol, literal), "unclassified")


def isolation_occurrences() -> tuple[IsolationOccurrence, ...]:
    """Return all scanned literals with file, enclosing symbol, and policy category."""
    occurrences: list[IsolationOccurrence] = []
    for path in _source_files():
        occurrences.extend(_python_occurrences(path) if path.suffix == ".py" else _text_occurrences(path))
    return tuple(occurrences)


def _format_occurrences(occurrences: tuple[IsolationOccurrence, ...]) -> str:
    return "\n".join(
        f"{occurrence.path.relative_to(ROOT)}::{occurrence.symbol} [{occurrence.category}]"
        for occurrence in occurrences
    )


def test_automated_behavioural_tests_have_no_volatile_authored_content_oracle() -> None:
    """Require synthetic fixtures unless a check is explicitly structural or a static guard."""
    occurrences = isolation_occurrences()
    unclassified = tuple(occurrence for occurrence in occurrences if occurrence.category == "unclassified")
    assert not unclassified, _format_occurrences(unclassified)


def test_visual_evidence_scripts_are_not_automated_test_helpers() -> None:
    """Keep excluded visual evidence out of the automated isolation boundary."""
    visual_sources = tuple(sorted({
        occurrence.path.name
        for occurrence in isolation_occurrences()
        if occurrence.category == "non-automated visual evidence (excluded from automated-test roots)"
    }))
    assert visual_sources == (
        "diagnose_precision_leg_crossover.py",
        "render_pivot_evidence.py",
        "render_strafe_evidence.py",
        "render_walk_seam_evidence.py",
    )
    for source in (
        *TEST_ROOT.glob("test_*.py"),
    ):
        if source == Path(__file__):
            continue
        source_text = source.read_text(encoding="utf-8")
        assert not [
            visual_source.removesuffix(".py")
            for visual_source in visual_sources
            if visual_source.removesuffix(".py") in source_text
        ], source


def test_production_authored_numeric_behaviour_oracle_is_rejected() -> None:
    """A production package reference plus a numeric motion oracle remains prohibited."""
    source = '''
def test_production_stride_distance():
    catalogue = "game/assets/characters/example/runtime/locomotion/locomotion_catalogue.json"
    expected_root_distance = 0.928596497
    assert measured_root_distance(catalogue) == expected_root_distance
'''
    occurrences = _python_occurrences_from_source(ROOT / "tests/src/ProductionMotionOracle.py", source)

    assert len(occurrences) == 1
    assert occurrences[0].symbol == "test_production_stride_distance"
    assert occurrences[0].category == "unclassified"


def test_precise_synthetic_and_structural_classifications_are_accepted() -> None:
    """Permit only the named synthetic declarations and structural contracts."""
    synthetic = _category_for(
        ROOT / "integration-tests/src/Support/SyntheticLocomotionRuntimeFixture.cs",
        "<text>",
        "synthetic authored_per_foot_contacts_v1.json declaration",
    )
    generator = _category_for(
        ROOT / "tools/kimodo-pipeline/kimodo_pipeline/contact_evidence.py",
        "build_descriptor",
        "authored_per_foot_contacts_v1.json",
    )
    structural = _category_for(
        ROOT / "tools/kimodo-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    )
    near_miss = _category_for(
        ROOT / "tools/kimodo-pipeline/kimodo_pipeline/contact_evidence.py",
        "assert_behaviour",
        "authored_per_foot_contacts_v1.json",
    )
    synthetic_assertion = _category_for(
        ROOT / "integration-tests/src/Support/SyntheticLocomotionRuntimeFixture.cs",
        "<text>",
        '[Fact] public void ProductionOracle() { Assert.Equal(0.9, productionMotion); }',
    )

    assert synthetic == "synthetic runtime fixture declaration"
    assert generator == "generator descriptor contract"
    assert structural == "structural/immutability smoke"
    assert near_miss == "unclassified"
    assert synthetic_assertion == "unclassified"
