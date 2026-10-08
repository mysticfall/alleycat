"""Pure path-contract coverage for the bootstrap artefact derivation."""
from __future__ import annotations

from pathlib import Path
from typing import Any
import unittest

from kimodo_pipeline.bootstrap_paths import (
    CANONICAL_SOURCE_RELATIVE,
    CHARACTER_SETUP_RELATIVE,
    IDLE_SOURCE_DIRECTORY_RELATIVE,
    NEUTRAL_TEMPLATE_RELATIVE,
    BootstrapPathError,
    derive_artefact_paths,
    derive_idle_source_path,
    repository_root,
)

REPOSITORY_ROOT = Path("/tmp/kimodo-fixture-repository")
CHARACTER_ROOT = Path("/tmp/kimodo-fixture-character")
VARIANT = "ember"


class BootstrapPathTests(unittest.TestCase):
    def test_derives_all_five_artefacts_from_character_root_and_variant(self) -> None:
        paths = derive_artefact_paths(CHARACTER_ROOT, VARIANT, REPOSITORY_ROOT)
        self.assertEqual(
            paths.canonical_source,
            REPOSITORY_ROOT / CANONICAL_SOURCE_RELATIVE,
        )
        self.assertEqual(
            paths.neutral_template,
            REPOSITORY_ROOT / NEUTRAL_TEMPLATE_RELATIVE,
        )
        self.assertEqual(
            paths.character_setup,
            CHARACTER_ROOT / CHARACTER_SETUP_RELATIVE,
        )
        self.assertEqual(
            paths.authoring_workspace,
            CHARACTER_ROOT / "animations/source/locomotion" / f"locomotion_{VARIANT}_authoring.blend",
        )
        self.assertEqual(
            paths.processed_package,
            CHARACTER_ROOT / "animations/processed/locomotion" / f"locomotion_{VARIANT}.blend",
        )

    def test_repository_fixed_artefacts_are_independent_of_character_and_variant(self) -> None:
        first = derive_artefact_paths(CHARACTER_ROOT, VARIANT, REPOSITORY_ROOT)
        second = derive_artefact_paths(Path("/tmp/kimodo-other-character"), "calm", REPOSITORY_ROOT)
        self.assertEqual(first.canonical_source, second.canonical_source)
        self.assertEqual(first.neutral_template, second.neutral_template)
        self.assertNotEqual(first.authoring_workspace, second.authoring_workspace)
        self.assertNotEqual(first.processed_package, second.processed_package)
        self.assertNotEqual(first.character_setup, second.character_setup)

    def test_derivation_is_deterministic_and_expands_users(self) -> None:
        self.assertEqual(
            derive_artefact_paths(CHARACTER_ROOT, VARIANT, REPOSITORY_ROOT),
            derive_artefact_paths(str(CHARACTER_ROOT) + "/", VARIANT, REPOSITORY_ROOT),
        )

    def test_invalid_variants_raise_contract_errors(self) -> None:
        invalid_variants: tuple[Any, ...] = ("Ember", "1x", "-a", "ember-x", "", "ember upper", None)
        for variant in invalid_variants:
            with self.subTest(variant=variant), self.assertRaises(BootstrapPathError):
                derive_artefact_paths(CHARACTER_ROOT, variant, REPOSITORY_ROOT)

    def test_invalid_character_roots_raise_contract_errors(self) -> None:
        invalid_roots: tuple[Any, ...] = ("", "   ", "/", Path("/"))
        for root in invalid_roots:
            with self.subTest(root=root), self.assertRaises(BootstrapPathError):
                derive_artefact_paths(root, VARIANT, REPOSITORY_ROOT)

    def test_unsupported_root_types_raise_contract_errors(self) -> None:
        unsupported_root: Any = 42
        with self.assertRaises(BootstrapPathError):
            derive_artefact_paths(unsupported_root, VARIANT, REPOSITORY_ROOT)

    def test_derives_the_character_idle_source_per_variant(self) -> None:
        self.assertEqual(
            derive_idle_source_path(CHARACTER_ROOT, VARIANT),
            CHARACTER_ROOT / IDLE_SOURCE_DIRECTORY_RELATIVE / f"{VARIANT}.blend",
        )
        self.assertEqual(
            derive_idle_source_path(CHARACTER_ROOT, VARIANT),
            CHARACTER_ROOT / "animations/source/idle" / f"{VARIANT}.blend",
        )
        self.assertNotEqual(
            derive_idle_source_path(CHARACTER_ROOT, "calm"),
            derive_idle_source_path(Path("/tmp/kimodo-other-character"), VARIANT),
        )

    def test_invalid_idle_source_variants_and_roots_raise_contract_errors(self) -> None:
        for variant in ("Ember", "1x", "", None):
            with self.subTest(variant=variant), self.assertRaises(BootstrapPathError):
                derive_idle_source_path(CHARACTER_ROOT, variant)
        for root in ("", "   ", "/", Path("/")):
            with self.subTest(root=root), self.assertRaises(BootstrapPathError):
                derive_idle_source_path(root, VARIANT)


if __name__ == "__main__":
    unittest.main()
