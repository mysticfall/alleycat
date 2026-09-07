"""Generic validated publication command for declared Kimodo package stages."""
from __future__ import annotations

import argparse
from collections.abc import Mapping
from dataclasses import dataclass
import json
from pathlib import Path
import subprocess
from typing import Any

from .bootstrap_paths import repository_root
from .cross_package_publication import CrossRootPublicationTransaction, StagedPackage
from .runtime_package_descriptor import (
    CONTACT_EVIDENCED_LOCOMOTION,
    CONTACT_FREE_IDLE,
    PackageDescriptorError,
    build_descriptor,
    reject_additional_variants,
    validate_descriptor,
    write_descriptor,
)


ROOT = repository_root()
GODOT_RUNTIME_VALIDATOR = ROOT / "tools/kimodo-pipeline/godot/commands/validate_kimodo_runtime_package.gd"
MANIFEST_SCHEMA_VERSION = 1


@dataclass(frozen=True)
class PublicationPlan:
    """Declared source, stage, and target paths for one package in a publication."""

    name: str
    mode: str
    processed_stage: Path
    runtime_stage: Path
    processed_root: Path
    runtime_root: Path
    preflight_descriptor: Path
    final_descriptor: Path


def parse_arguments() -> argparse.Namespace:
    """Parse the generic validated-publication command arguments."""
    parser = argparse.ArgumentParser(description="Publish validated Kimodo package stages through one transaction.")
    parser.add_argument("--manifest", required=True, type=Path, help="Declared publication manifest JSON.")
    parser.add_argument("--journal-directory", required=True, type=Path)
    return parser.parse_args()


def descriptor_argument(descriptor: Path) -> str:
    """Return the Godot project-relative descriptor argument."""
    try:
        return f"--descriptor=res://{descriptor.resolve().relative_to(ROOT / 'game')}"
    except ValueError as error:
        raise ValueError(f"Descriptor must be inside the Godot project: {descriptor}") from error


def validate_with_godot(descriptor: Path) -> None:
    """Run the external runtime-package validator for one declared package stage."""
    result = subprocess.run(
        [
            "godot-mono", "--headless", "--xr-mode", "off", "--path", "game", "--script",
            str(GODOT_RUNTIME_VALIDATOR), "--", descriptor_argument(descriptor),
        ],
        cwd=ROOT,
        text=True,
        check=False,
        timeout=600,
    )
    if result.returncode != 0:
        raise RuntimeError(f"Godot runtime validation failed for {descriptor}")


def load_manifest(path: Path) -> tuple[tuple[PublicationPlan, ...], tuple[Path, ...]]:
    """Load a fully declared multi-package publication manifest without touching any roots."""
    document = _object(path)
    if set(document) != {"schema_version", "packages", "legacy_roots"} or document.get("schema_version") != MANIFEST_SCHEMA_VERSION:
        raise ValueError("Publication manifest schema is invalid.")
    packages = document.get("packages")
    legacy_roots = document.get("legacy_roots")
    if not isinstance(packages, list) or not isinstance(legacy_roots, list):
        raise ValueError("Publication manifest packages and legacy_roots must be arrays.")
    plans = tuple(_plan(item) for item in packages)
    if len(plans) < 2:
        raise ValueError("Publication manifest must declare both package plans.")
    if len({plan.name for plan in plans}) != len(plans):
        raise ValueError("Publication manifest package names must be unique.")
    if {plan.mode for plan in plans} != {CONTACT_EVIDENCED_LOCOMOTION, CONTACT_FREE_IDLE}:
        raise ValueError("Publication manifest must declare one locomotion and one idle package mode.")
    if not all(isinstance(root, str) and root for root in legacy_roots):
        raise ValueError("Publication manifest legacy roots must be non-empty paths.")
    return plans, tuple(Path(root).resolve() for root in legacy_roots)


def _plan(value: object) -> PublicationPlan:
    if not isinstance(value, Mapping):
        raise ValueError("Publication package plan must be an object.")
    required = {
        "name", "mode", "processed_stage", "runtime_stage", "processed_root", "runtime_root",
        "preflight_descriptor", "final_descriptor",
    }
    if set(value) != required:
        raise ValueError("Publication package plan schema is invalid.")
    fields: dict[str, str] = {}
    for key in required:
        item = value.get(key)
        if not isinstance(item, str) or not item:
            raise ValueError("Publication package plan fields must be non-empty strings.")
        fields[key] = item
    if len(fields) != len(required):
        raise ValueError("Publication package plan fields must be non-empty strings.")
    return PublicationPlan(
        name=fields["name"],
        mode=fields["mode"],
        processed_stage=Path(fields["processed_stage"]).resolve(),
        runtime_stage=Path(fields["runtime_stage"]).resolve(),
        processed_root=Path(fields["processed_root"]).resolve(),
        runtime_root=Path(fields["runtime_root"]).resolve(),
        preflight_descriptor=Path(fields["preflight_descriptor"]).resolve(),
        final_descriptor=Path(fields["final_descriptor"]).resolve(),
    )


def _object(path: Path) -> Mapping[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise ValueError(f"Publication manifest is unreadable: {path}") from error
    if not isinstance(value, Mapping):
        raise ValueError("Publication manifest must be an object.")
    return value


def _declared_package(path: Path, plan: PublicationPlan) -> Mapping[str, Any]:
    declaration = _object(path)
    if declaration.get("name") != plan.name or declaration.get("publication_mode") != plan.mode:
        raise PackageDescriptorError(f"Declared package mode or name does not match publication plan: {path}")
    return declaration


def _staged_package(plan: PublicationPlan) -> StagedPackage:
    preflight = _declared_package(plan.preflight_descriptor, plan)
    final = _declared_package(plan.final_descriptor, plan)
    variant = final.get("variant")
    if not isinstance(variant, str) or not variant:
        raise PackageDescriptorError(f"Declared package has no variant identity: {plan.final_descriptor}")
    # Multi-variant publication is not ratified: refuse before constructing the
    # transaction when the production set already owns a different variant.
    reject_additional_variants(plan.processed_root, plan.runtime_root.parent, variant)
    durable_descriptor = build_descriptor(final, plan.processed_stage, plan.runtime_stage)
    write_descriptor(durable_descriptor, plan.processed_stage, variant)

    def validate(processed: Path, runtime: Path) -> None:
        validate_descriptor(durable_descriptor, processed, runtime)
        validate_with_godot(plan.preflight_descriptor if processed == plan.processed_stage else plan.final_descriptor)

    # Loading both declarations before replacement refuses a mismatched staged
    # package declaration even when its durable hashes would otherwise match.
    _validate_declaration_mode(preflight)
    _validate_declaration_mode(final)
    return StagedPackage(
        plan.name,
        plan.processed_stage,
        plan.runtime_stage,
        plan.processed_root,
        plan.runtime_root,
        validate,
    )


def _validate_declaration_mode(declaration: Mapping[str, Any]) -> None:
    mode = declaration.get("publication_mode")
    if mode == CONTACT_EVIDENCED_LOCOMOTION:
        return
    if mode == CONTACT_FREE_IDLE:
        if declaration.get("contact_evidence") is not None or declaration.get("contact_evidence_required") is True:
            raise PackageDescriptorError("A contact-free idle declaration must not declare or require contact evidence.")
        return
    raise PackageDescriptorError("Publication declaration has an unsupported mode.")


def main() -> None:
    """Publish declared locomotion and idle stages only after independent validation."""
    arguments = parse_arguments()
    plans, legacy_roots = load_manifest(arguments.manifest)
    packages = tuple(_staged_package(plan) for plan in plans)
    CrossRootPublicationTransaction(
        packages,
        legacy_roots=legacy_roots,
        journal_directory=arguments.journal_directory,
    ).publish()
    print("KIMODO_VALIDATED_PUBLICATION=success")
