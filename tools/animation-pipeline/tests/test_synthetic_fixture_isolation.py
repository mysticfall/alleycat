"""Exhaustively guard automated ANIM-002 tests from volatile authored content."""
from __future__ import annotations

import ast
from dataclasses import dataclass
from pathlib import Path

from kimodo_pipeline.bootstrap_paths import repository_root


ROOT = repository_root()
SCANNED_ROOTS = (
    ROOT / "tests/src",
    ROOT / "integration-tests/src",
    ROOT / "tools",
)
FORBIDDEN_AUTHORED_EXPECTATIONS = (
    "locomotion_feminine_landing_ablation_01",
    "loc_sad_",
    "locomotion.blend",
    "locomotion_template.blend",
    "kimodo_retarget_setup.blend",
)
ALLOWED_PYTHON_OCCURRENCES = {
    (
        "tools/animation-pipeline/export_motionbricks.py",
        "<module>",
        "game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend",
    ): "structural/calibration input path",
    (
        "tools/animation-pipeline/kimodo_pipeline/bootstrap_extraction.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    ): "structural/path derivation",
    (
        "tools/animation-pipeline/kimodo_pipeline/bootstrap_paths.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion_template.blend",
    ): "structural/path derivation",
    (
        "tools/animation-pipeline/kimodo_pipeline/bootstrap_paths.py",
        "<module>",
        "animations/source/kimodo_retarget_setup.blend",
    ): "structural/path derivation",
    (
        "tools/animation-pipeline/tests/test_blender_idle_real_asset_smoke.py",
        "<module>",
        "sad",
    ): "production idle-source variant identity (input staging, not an oracle)",
    (
        "tools/animation-pipeline/tests/test_native_idle_clip.py",
        "<module>",
        "animations/source/kimodo_retarget_setup.blend",
    ): "explicit real-asset idle-clip smoke setup input (byte-unchanged check, not an oracle)",
    (
        "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    ): "structural/immutability smoke",
    (
        "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion_template.blend",
    ): "structural/immutability smoke",
    # The two exact 'sad' allowance tuples below are the selected-variant identity of the read-only
    # source-contract smoke, not motion-behaviour oracles (KIMODO-TAC-001, KIMODO-SRC-005): the
    # module-level literals derive the repository-fixed canonical-source / neutral-template paths and
    # the character's idle-source path, and the bootstrap_manifest fixture passes the variant to a
    # preflight that asserts the monolith's SHA-256 is identical before and after. Only these (path, symbol, exact literal) tuples are exempt; every other
    # literal or symbol of that file stays unclassified.
    (
        "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "sad",
    ): "structural/path derivation",
    (
        "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "bootstrap_manifest",
        "sad",
    ): "structural/immutability bootstrap",
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
    if relative == "tools/animation-pipeline/tests/test_synthetic_fixture_isolation.py":
        return "static guard policy data"
    if relative == "integration-tests/src/Motion/MotionBricksDeliveryIntegrationTests.cs" and symbol == "<text>":
        # These exact statements remove obsolete provenance from a startup fixture and assert its absence.
        # Do not exempt the file: any additional authored-content oracle must still fail the guard.
        remaining = literal
        for statement in (
            '_ = poses["sources"]!.AsObject().Remove("game/assets/characters/animations/source/locomotion/locomotion.blend");',
            'Assert.False(data.Manifest.GetProperty("sources").TryGetProperty(\n'
            '            "game/assets/characters/animations/source/locomotion/locomotion.blend", out _));',
        ):
            remaining = remaining.replace(statement, "")
        if remaining != literal and not _contains_forbidden_authored_content(remaining):
            return "structural/obsolete provenance absence"
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


def test_production_authored_numeric_behaviour_oracle_is_rejected() -> None:
    """An authored source reference plus a numeric motion oracle remains prohibited."""
    source = '''
def test_production_stride_distance():
    blend = "game/assets/characters/animations/source/locomotion/locomotion_feminine_landing_ablation_01.blend"
    expected_root_distance = 0.928596497
    assert measured_root_distance(blend) == expected_root_distance
'''
    occurrences = _python_occurrences_from_source(ROOT / "tests/src/ProductionMotionOracle.py", source)

    assert len(occurrences) == 1
    assert occurrences[0].symbol == "test_production_stride_distance"
    assert occurrences[0].category == "unclassified"


def test_precise_synthetic_and_structural_classifications_are_accepted() -> None:
    """Permit only the named synthetic declarations and structural contracts."""
    real_asset_smoke = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_native_idle_clip.py",
        "<module>",
        "animations/source/kimodo_retarget_setup.blend",
    )
    real_asset_near_miss = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_native_idle_clip.py",
        "KimodoPathTests.test_sad_reexports_matching_the_blender_preview_without_touching_sources",
        "animations/source/kimodo_retarget_setup.blend",
    )
    path_derivation = _category_for(
        ROOT / "tools/animation-pipeline/kimodo_pipeline/bootstrap_extraction.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    )
    structural = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    )
    near_miss = _category_for(
        ROOT / "tools/animation-pipeline/kimodo_pipeline/bootstrap_extraction.py",
        "assert_behaviour",
        "game/assets/characters/animations/source/locomotion/locomotion.blend",
    )
    synthetic_assertion = _category_for(
        ROOT / "integration-tests/src/Motion/MotionBricksDeliveryIntegrationTests.cs",
        "<text>",
        '[Fact] public void ProductionOracle() { Assert.Equal(0.9, productionMotion); }',
    )

    assert real_asset_smoke == "explicit real-asset idle-clip smoke setup input (byte-unchanged check, not an oracle)"
    assert real_asset_near_miss == "unclassified"
    assert path_derivation == "structural/path derivation"
    assert structural == "structural/immutability smoke"
    assert near_miss == "unclassified"
    assert synthetic_assertion == "unclassified"


def test_delivery_absence_contract_does_not_allow_neighbouring_content_oracles() -> None:
    """SA-B01 absence checks are structural; a new authored motion oracle in the same file is not."""
    path = ROOT / "integration-tests/src/Motion/MotionBricksDeliveryIntegrationTests.cs"
    source = path.read_text(encoding="utf-8")
    assert _category_for(path, "<text>", source) == "structural/obsolete provenance absence"
    assert _category_for(path, "<text>", source + '\nAssert.Equal("loc_sad_fixture", actual);') == "unclassified"
    exporter = ROOT / "tools/animation-pipeline/export_motionbricks.py"
    calibration = "game/assets/characters/reference/female/animations/source/kimodo_retarget_setup.blend"
    assert _category_for(exporter, "<module>", calibration) == "structural/calibration input path"
    assert _category_for(exporter, "test_motion", calibration) == "unclassified"


def test_core_asset_smoke_variant_identity_is_structural_not_behavioural() -> None:
    """The smoke's two exact 'sad' variant-identity literals classify as structural checks."""
    module_variant = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "sad",
    )
    bootstrap_variant = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "bootstrap_manifest",
        "sad",
    )

    assert module_variant == "structural/path derivation"
    assert bootstrap_variant == "structural/immutability bootstrap"


def test_core_asset_smoke_variant_exemption_is_not_a_wildcard() -> None:
    """Unlisted literals and unlisted neighbouring symbols of the smoke file stay unclassified."""
    unlisted_literal = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "<module>",
        "loc_sad_",
    )
    unlisted_symbol = _category_for(
        ROOT / "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        "test_some_behavioural_oracle",
        "sad",
    )

    assert unlisted_literal == "unclassified"
    assert unlisted_symbol == "unclassified"


def test_variant_literal_matching_is_exact_not_substring() -> None:
    """Only the exact value 'sad' is flagged; longer strings merely containing 'sad' are not."""
    source = 'SADNESS = "sadness"\nEMBEDDED = "prefix sad suffix"\nEXACT = "sad"\n'
    occurrences = _python_occurrences_from_source(
        ROOT / "tools/animation-pipeline/tests/test_core_asset_read_only_smoke.py",
        source,
    )

    assert len(occurrences) == 1
    assert occurrences[0].symbol == "<module>"
    assert occurrences[0].category == "structural/path derivation"
