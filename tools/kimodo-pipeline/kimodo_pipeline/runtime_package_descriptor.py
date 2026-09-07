"""Durable, hash-bound Kimodo production runtime-package descriptor contract.

Every descriptor is character-owned and variant-scoped: it declares the
character, set, and variant identity it belongs to, and every bound artefact
lives inside that variant's processed metadata directory or runtime package
root (the retargeted authoring blend stays at the set level).
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
from typing import Any, Mapping


SCHEMA_VERSION = 2
DESCRIPTOR_RELATIVE_PATH = Path("provenance/runtime-package-descriptor-v1.json")
CONTACT_EVIDENCED_LOCOMOTION = "contact-evidenced-locomotion"
CONTACT_FREE_IDLE = "contact-free-idle"
DESCRIPTOR_IDENTITY_FIELDS = ("character", "name", "variant")


class PackageDescriptorError(ValueError):
    """A production package descriptor is missing, stale, or inconsistent."""


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def library_filename(name: str, variant: str) -> str:
    return f"{name}_{variant}.tres"


def catalogue_filename(name: str, variant: str) -> str:
    return f"{name}_{variant}_catalogue.json"


def blend_filename(name: str, variant: str) -> str:
    return f"{name}_{variant}.blend"


def reject_additional_variants(processed_set_root: Path, runtime_set_root: Path, variant: str) -> None:
    """Fail closed when another variant already owns this character/set pair.

    Multi-variant publication is not ratified; publication preparation and
    preflight must refuse before any production mutation when a sibling variant
    owns processed metadata or a runtime package.
    """
    if processed_set_root.is_dir():
        for child in sorted(processed_set_root.iterdir()):
            if child.name != variant and child.is_dir() and (child / "processed-index.json").is_file():
                raise PackageDescriptorError(
                    f"Publishing variant '{variant}' would alias variant '{child.name}' metadata in {processed_set_root}; "
                    "multi-variant publication is not ratified."
                )
    if runtime_set_root.is_dir():
        for child in sorted(runtime_set_root.iterdir()):
            if child.name != variant and child.is_dir() and any(child.glob("*_catalogue.json")):
                raise PackageDescriptorError(
                    f"Publishing variant '{variant}' would alias variant '{child.name}' runtime package in {runtime_set_root}; "
                    "multi-variant publication is not ratified."
                )


def build_descriptor(
    package: Mapping[str, Any],
    processed_root: Path,
    runtime_root: Path,
) -> dict[str, Any]:
    """Build a durable descriptor from a validated publication declaration and staged roots.

    ``processed_root`` is the staged or durable processed SET directory (it holds the
    set-level authoring blend and the variant metadata directory); ``runtime_root`` is
    the variant-scoped runtime package root.
    """
    mode = _publication_mode(package)
    name = _string(package, "name")
    variant = _string(package, "variant")
    index_name = Path(_string(package, "index")).name
    blend_name = blend_filename(name, variant)
    if mode == CONTACT_FREE_IDLE:
        return _build_contact_free_idle_descriptor(package, processed_root, runtime_root, name, variant, index_name, blend_name)

    contact = package.get("contact_evidence")
    if not isinstance(contact, Mapping):
        raise PackageDescriptorError("Production package requires contact_evidence.")
    runtime_path = _portable_relative(_string(contact, "runtime_path"))
    paths = {
        "processed_index": processed_root / variant / index_name,
        "retargeted_blend": processed_root / blend_name,
        "runtime_catalogue": runtime_root / catalogue_filename(name, variant),
        "runtime_library": runtime_root / library_filename(name, variant),
        "retained_contact_evidence": processed_root / variant / "provenance" / runtime_path,
        "runtime_contact_evidence": runtime_root / runtime_path,
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise PackageDescriptorError("Descriptor inputs are missing: " + ", ".join(missing))
    descriptor = {
        "schema_version": SCHEMA_VERSION,
        "character": _string(package, "character"),
        "name": name,
        "variant": variant,
        "index": _string(package, "published_index"),
        "blend": _published_blend_path(package),
        "root": _string(package, "root"),
        "contact_evidence_required": True,
        "contact_evidence": {
            "input": _string(contact, "retained_input"),
            "retained_input": _string(contact, "retained_input"),
            "runtime_path": runtime_path,
            "package_identity": _string(contact, "package_identity"),
            "generation": _string(contact, "generation"),
        },
        "stitch_edges": list(package.get("stitch_edges", [])),
        "binding": {key + "_sha256": sha256(path) for key, path in paths.items()},
    }
    validate_descriptor(descriptor, processed_root, runtime_root)
    return descriptor


def _build_contact_free_idle_descriptor(
    package: Mapping[str, Any],
    processed_root: Path,
    runtime_root: Path,
    name: str,
    variant: str,
    index_name: str,
    blend_name: str,
) -> dict[str, Any]:
    if name != "idle":
        raise PackageDescriptorError("The contact-free publication mode is reserved for the idle package.")
    if package.get("contact_evidence") is not None or package.get("contact_evidence_required") is True:
        raise PackageDescriptorError("A contact-free idle package must not declare or require contact evidence.")
    paths = {
        "processed_index": processed_root / variant / index_name,
        "retargeted_blend": processed_root / blend_name,
        "runtime_catalogue": runtime_root / catalogue_filename(name, variant),
        "runtime_library": runtime_root / library_filename(name, variant),
    }
    missing = [str(path) for path in paths.values() if not path.is_file()]
    if missing:
        raise PackageDescriptorError("Descriptor inputs are missing: " + ", ".join(missing))
    descriptor = {
        "schema_version": SCHEMA_VERSION,
        "character": _string(package, "character"),
        "name": name,
        "variant": variant,
        "index": _string(package, "published_index"),
        "blend": _published_blend_path(package),
        "root": _string(package, "root"),
        "contact_evidence_required": False,
        "contact_evidence": None,
        "stitch_edges": list(package.get("stitch_edges", [])),
        "binding": {key + "_sha256": sha256(path) for key, path in paths.items()},
    }
    validate_descriptor(descriptor, processed_root, runtime_root)
    return descriptor


def validate_descriptor(
    descriptor: Mapping[str, Any],
    processed_root: Path,
    runtime_root: Path,
) -> None:
    """Fail closed when identity, paths, or bound publication bytes disagree."""
    required = {
        "schema_version", *DESCRIPTOR_IDENTITY_FIELDS, "index", "blend", "root",
        "contact_evidence_required", "contact_evidence", "stitch_edges", "binding",
    }
    if set(descriptor) != required or descriptor.get("schema_version") != SCHEMA_VERSION:
        raise PackageDescriptorError(
            "Production package descriptor schema is invalid (expected character/set/variant identity schema "
            f"v{SCHEMA_VERSION})."
        )
    for field in DESCRIPTOR_IDENTITY_FIELDS:
        if not isinstance(descriptor.get(field), str) or not descriptor.get(field):
            raise PackageDescriptorError(f"Production descriptor has no {field} identity.")
    for key in ("index", "blend", "root"):
        _durable_resource_path(_string(descriptor, key), key)
    name = _string(descriptor, "name")
    variant = _string(descriptor, "variant")
    index = _string(descriptor, "index")
    root = _string(descriptor, "root")
    blend = _string(descriptor, "blend")
    root_parts = Path(root).parts
    if len(root_parts) < 3 or root_parts[-1] != variant or root_parts[-2] != name:
        raise PackageDescriptorError("Production descriptor root is not the runtime/<set>/<variant> package root.")
    index_parent = Path(index).parent
    if index_parent.name != variant or index_parent.parent.name in ("", ".", "/"):
        raise PackageDescriptorError("Production descriptor index is not variant-scoped under processed/<set>/<variant>.")
    if blend != str(Path(index).parent.parent / blend_filename(name, variant)).replace("res:/", "res://", 1):
        raise PackageDescriptorError("Production descriptor blend is not the set-level <set>_<variant>.blend carrier.")
    if descriptor.get("contact_evidence_required") is False:
        _validate_contact_free_idle_descriptor(descriptor, processed_root, runtime_root)
        return
    if descriptor.get("contact_evidence_required") is not True:
        raise PackageDescriptorError("Production descriptor must require contact evidence.")
    contact = descriptor.get("contact_evidence")
    if not isinstance(contact, Mapping) or set(contact) != {
        "input", "retained_input", "runtime_path", "package_identity", "generation",
    }:
        raise PackageDescriptorError("Production contact-evidence declaration is invalid.")
    input_path = _durable_resource_path(_string(contact, "input"), "contact input")
    if input_path != _durable_resource_path(_string(contact, "retained_input"), "retained input"):
        raise PackageDescriptorError("Production contact evidence must use its retained input.")
    runtime_path = _portable_relative(_string(contact, "runtime_path"))
    files = {
        "processed_index_sha256": processed_root / variant / Path(index).name,
        "retargeted_blend_sha256": processed_root / blend_filename(name, variant),
        "runtime_catalogue_sha256": runtime_root / catalogue_filename(name, variant),
        "runtime_library_sha256": runtime_root / library_filename(name, variant),
        "retained_contact_evidence_sha256": processed_root / variant / "provenance" / runtime_path,
        "runtime_contact_evidence_sha256": runtime_root / runtime_path,
    }
    binding = descriptor.get("binding")
    if not isinstance(binding, Mapping) or set(binding) != set(files):
        raise PackageDescriptorError("Production package hash binding is incomplete.")
    for key, path in files.items():
        if not path.is_file() or binding.get(key) != sha256(path):
            raise PackageDescriptorError(f"Production package hash mismatch: {key}.")
    retained = _json(files["retained_contact_evidence_sha256"])
    runtime = _json(files["runtime_contact_evidence_sha256"])
    catalogue = _json(files["runtime_catalogue_sha256"])
    identity = _string(contact, "package_identity")
    generation = _string(contact, "generation")
    if retained != runtime or retained.get("package_identity") != identity:
        raise PackageDescriptorError("Production contact-evidence identity or retained/runtime bytes disagree.")
    source = retained.get("source_derived")
    declaration = catalogue.get("contact_evidence")
    if not isinstance(source, Mapping) or source.get("generation") != generation:
        raise PackageDescriptorError("Production contact-evidence generation disagrees with its descriptor.")
    if not isinstance(declaration, Mapping) or declaration.get("package_identity") != identity or declaration.get("generation") != generation:
        raise PackageDescriptorError("Runtime catalogue contact declaration disagrees with its descriptor.")


def _validate_contact_free_idle_descriptor(
    descriptor: Mapping[str, Any],
    processed_root: Path,
    runtime_root: Path,
) -> None:
    if _string(descriptor, "name") != "idle" or descriptor.get("contact_evidence") is not None:
        raise PackageDescriptorError("A contact-free descriptor must be an idle package without contact evidence.")
    name = _string(descriptor, "name")
    variant = _string(descriptor, "variant")
    files = {
        "processed_index_sha256": processed_root / variant / Path(_string(descriptor, "index")).name,
        "retargeted_blend_sha256": processed_root / blend_filename(name, variant),
        "runtime_catalogue_sha256": runtime_root / catalogue_filename(name, variant),
        "runtime_library_sha256": runtime_root / library_filename(name, variant),
    }
    binding = descriptor.get("binding")
    if not isinstance(binding, Mapping) or set(binding) != set(files):
        raise PackageDescriptorError("Contact-free package hash binding is incomplete.")
    for key, path in files.items():
        if not path.is_file() or binding.get(key) != sha256(path):
            raise PackageDescriptorError(f"Contact-free package hash mismatch: {key}.")
    if _json(files["runtime_catalogue_sha256"]).get("contact_evidence") is not None:
        raise PackageDescriptorError("A contact-free idle catalogue must not declare contact evidence.")


def write_descriptor(descriptor: Mapping[str, Any], processed_root: Path, variant: str) -> Path:
    path = processed_root / variant / DESCRIPTOR_RELATIVE_PATH
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(descriptor, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return path


def _published_blend_path(package: Mapping[str, Any]) -> str:
    index = _string(package, "published_index")
    return str(Path(index).parent.parent / blend_filename(_string(package, "name"), _string(package, "variant"))).replace(
        "res:/", "res://", 1
    )


def _publication_mode(package: Mapping[str, Any]) -> str:
    mode = _string(package, "publication_mode")
    if mode == CONTACT_EVIDENCED_LOCOMOTION:
        if _string(package, "name") != "locomotion":
            raise PackageDescriptorError("The contact-evidenced publication mode is reserved for locomotion.")
        return mode
    if mode == CONTACT_FREE_IDLE:
        return mode
    raise PackageDescriptorError(f"Unsupported publication mode: {mode}.")


def _string(value: Mapping[str, Any], key: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result:
        raise PackageDescriptorError(f"Production descriptor field '{key}' is missing.")
    return result


def _portable_relative(value: str) -> str:
    path = Path(value)
    if path.is_absolute() or ".." in path.parts or value in {"", "."}:
        raise PackageDescriptorError("Runtime contact path must be package-relative.")
    return path.as_posix()


def _durable_resource_path(value: str, context: str) -> str:
    if not value.startswith("res://") or value.startswith(("res://tests/", "res://temp/")) or ".." in value.split("/"):
        raise PackageDescriptorError(f"Production {context} must be a durable resource path.")
    return value


def _json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise PackageDescriptorError(f"Bound JSON artefact is invalid: {path}.") from error
    if not isinstance(value, dict):
        raise PackageDescriptorError(f"Bound JSON artefact must be an object: {path}.")
    return value
