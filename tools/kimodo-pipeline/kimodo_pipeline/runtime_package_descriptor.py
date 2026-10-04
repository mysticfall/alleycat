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
import re
from typing import Any, Mapping


SCHEMA_VERSION = 3
DESCRIPTOR_RELATIVE_PATH = Path("provenance/runtime-package-descriptor-v1.json")
PLANTED_INTERVAL_FILE_LOCOMOTION = "planted-interval-file-locomotion"
CATALOGUE_PLANTED_INTERVAL_IDLE = "catalogue-planted-interval-idle"
DESCRIPTOR_IDENTITY_FIELDS = ("character", "name", "variant")
PLANTED_INTERVALS_FILENAME = "per_foot_planted_intervals_v1.json"
PLANTED_INTERVALS_SCHEMA_VERSION = 1
PLANTED_INTERVALS_CATALOGUE_FIELD = "per_foot_planted_intervals"
PACKAGE_FILE = "package_file"
CATALOGUE_FIELD = "catalogue_field"
PLANTED_INTERVAL_STORAGE = {"locomotion": PACKAGE_FILE, "idle": CATALOGUE_FIELD}
PUBLICATION_MODES = {"locomotion": PLANTED_INTERVAL_FILE_LOCOMOTION, "idle": CATALOGUE_PLANTED_INTERVAL_IDLE}


class PackageDescriptorError(ValueError):
    """A production package descriptor is missing, stale, or inconsistent."""


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


_RESOURCE_HEADER = re.compile(
    br'^\[gd_resource(?: [A-Za-z_][A-Za-z0-9_]*=(?:"[^"\r\n]*"|[^ "\[\]\r\n=]+))*\]$'
)
_RESOURCE_UID = re.compile(br' uid="uid://[^"\r\n]+"')


def canonical_runtime_library_bytes(content: bytes) -> bytes:
    """Remove only a valid, non-empty UID attribute from a valid top-level resource header.

    The header (the first line, without its line ending) must be strict UTF-8 without NUL bytes; any other header
    leaves the content unchanged. The Godot and C# implementations apply the same rule, checked against the shared
    ``tests/fixtures/runtime_library_canonicalisation_vectors.json`` vectors.
    """
    line_end = content.find(b"\n")
    header_end = len(content) if line_end < 0 else line_end
    syntax_end = header_end - 1 if header_end > 0 and content[header_end - 1] == ord("\r") else header_end
    header = content[:syntax_end]
    try:
        header.decode("utf-8", errors="strict")
    except UnicodeDecodeError:
        return content
    if b"\x00" in header or _RESOURCE_HEADER.fullmatch(header) is None:
        return content
    matches = list(_RESOURCE_UID.finditer(header))
    if len(matches) != 1:
        return content
    uid = matches[0]
    return content[:uid.start()] + content[uid.end():]


def runtime_library_sha256(path: Path) -> str:
    """Hash ResourceSaver bytes, excluding only the path-owned top-level UID annotation."""
    return hashlib.sha256(canonical_runtime_library_bytes(path.read_bytes())).hexdigest()


def _binding_sha256(key: str, path: Path) -> str:
    return runtime_library_sha256(path) if key == "runtime_library_sha256" else sha256(path)


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


def planted_interval_declaration(name: str, retained_input: str) -> dict[str, str]:
    """Return the durable planted-interval declaration owned by one package."""
    storage = PLANTED_INTERVAL_STORAGE.get(name)
    if storage is None:
        raise PackageDescriptorError(f"Package '{name}' has no planted-interval storage contract.")
    declaration = {"retained_input": retained_input, "storage": storage}
    if storage == PACKAGE_FILE:
        declaration["runtime_path"] = PLANTED_INTERVALS_FILENAME
    return declaration


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
    _publication_mode(package)
    name = _string(package, "name")
    variant = _string(package, "variant")
    declared = package.get("planted_intervals")
    if not isinstance(declared, Mapping):
        raise PackageDescriptorError("Production package requires a planted-interval declaration.")
    intervals = planted_interval_declaration(name, _string(declared, "retained_input"))
    paths = _bound_paths(name, variant, Path(_string(package, "index")).name, processed_root, runtime_root, intervals)
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
        "planted_intervals": intervals,
        "stitch_edges": list(package.get("stitch_edges", [])),
        "binding": {key: _binding_sha256(key, path) for key, path in paths.items()},
    }
    validate_descriptor(descriptor, processed_root, runtime_root)
    return descriptor


def validate_descriptor(
    descriptor: Mapping[str, Any],
    processed_root: Path,
    runtime_root: Path,
) -> None:
    """Fail closed when identity, paths, planted intervals, or bound publication bytes disagree."""
    required = {
        "schema_version", *DESCRIPTOR_IDENTITY_FIELDS, "index", "blend", "root",
        "planted_intervals", "stitch_edges", "binding",
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
    intervals = descriptor.get("planted_intervals")
    expected_retained = f"{index.rsplit('/', 1)[0]}/provenance/{PLANTED_INTERVALS_FILENAME}"
    if name not in PLANTED_INTERVAL_STORAGE or not isinstance(intervals, Mapping) \
            or dict(intervals) != planted_interval_declaration(name, expected_retained):
        raise PackageDescriptorError("Production planted-interval declaration is invalid.")
    files = _bound_paths(name, variant, Path(index).name, processed_root, runtime_root, intervals)
    binding = descriptor.get("binding")
    if not isinstance(binding, Mapping) or set(binding) != set(files):
        raise PackageDescriptorError("Production package hash binding is incomplete.")
    for key, path in files.items():
        if not path.is_file() or binding.get(key) != _binding_sha256(key, path):
            raise PackageDescriptorError(f"Production package hash mismatch: {key}.")
    retained = _json(files["retained_planted_intervals_sha256"])
    catalogue = _json(files["runtime_catalogue_sha256"])
    if retained.get("schema_version") != PLANTED_INTERVALS_SCHEMA_VERSION:
        raise PackageDescriptorError("Production planted intervals declare an unknown or mismatched schema version.")
    derivation = retained.get("derivation")
    if not isinstance(derivation, Mapping) or derivation.get("runtime_library_sha256") != binding["runtime_library_sha256"]:
        raise PackageDescriptorError("Production planted intervals were derived from a different runtime library.")
    if intervals["storage"] == PACKAGE_FILE:
        if PLANTED_INTERVALS_CATALOGUE_FIELD in catalogue:
            raise PackageDescriptorError("A locomotion catalogue must not embed planted intervals.")
        if files["retained_planted_intervals_sha256"].read_bytes() != files["runtime_planted_intervals_sha256"].read_bytes():
            raise PackageDescriptorError("Production retained/runtime planted intervals disagree.")
    elif catalogue.get(PLANTED_INTERVALS_CATALOGUE_FIELD) != retained:
        raise PackageDescriptorError("Production idle catalogue planted intervals disagree with the retained intervals.")


def _bound_paths(
    name: str,
    variant: str,
    index_name: str,
    processed_root: Path,
    runtime_root: Path,
    intervals: Mapping[str, Any],
) -> dict[str, Path]:
    paths = {
        "processed_index_sha256": processed_root / variant / index_name,
        "retargeted_blend_sha256": processed_root / blend_filename(name, variant),
        "runtime_catalogue_sha256": runtime_root / catalogue_filename(name, variant),
        "runtime_library_sha256": runtime_root / library_filename(name, variant),
        "retained_planted_intervals_sha256": processed_root / variant / "provenance" / PLANTED_INTERVALS_FILENAME,
    }
    if intervals.get("storage") == PACKAGE_FILE:
        paths["runtime_planted_intervals_sha256"] = runtime_root / PLANTED_INTERVALS_FILENAME
    return paths


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
    if mode not in PUBLICATION_MODES.values():
        raise PackageDescriptorError(f"Unsupported publication mode: {mode}.")
    if PUBLICATION_MODES.get(_string(package, "name")) != mode:
        raise PackageDescriptorError(f"Publication mode {mode} is reserved for another package.")
    return mode


def _string(value: Mapping[str, Any], key: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result:
        raise PackageDescriptorError(f"Production descriptor field '{key}' is missing.")
    return result


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
