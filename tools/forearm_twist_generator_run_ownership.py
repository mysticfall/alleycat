"""Generator-run evidence for direct RIG-002 skinning ownership validation."""

from __future__ import annotations

import hashlib
import json
import math
from collections.abc import Mapping, Sequence
from pathlib import Path

try:
    from .forearm_twist_weights import assert_axial_ownership_contract, is_source_bilateral
except ImportError:
    from forearm_twist_weights import assert_axial_ownership_contract, is_source_bilateral


SCHEMA_VERSION = 7
SIDECAR_SUFFIX = ".forearm_twist_generator_run_ownership.json"
PRODUCER = "tools/generate_character.py (generator-run authored axial reference)"


def sidecar_path(output_path: Path) -> Path:
    """Return the required generator-run ownership-validation sidecar path."""

    return output_path.with_suffix(SIDECAR_SUFFIX)


def sha256_file(path: Path) -> str:
    """Return a complete file digest without loading generated blends into memory."""

    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def source_asset_path(repository_root: Path, preset: str) -> Path:
    """Resolve the checked-in MPFB preset asset associated with a generator run."""

    if not preset or any(part in {"", ".", ".."} for part in Path(preset).parts):
        raise ValueError(f"Invalid MPFB preset identifier {preset!r}")
    path = repository_root / "tools" / "mpfb" / "config" / f"human.{preset}.json"
    if not path.is_file():
        raise ValueError(f"Missing checked-in MPFB source asset for preset {preset!r}: {path}")
    return path


def build_evidence(
    repository_root: Path,
    output_path: Path,
    preset: str,
    meshes: Sequence[Mapping[str, object]],
    skipped_meshes: Sequence[Mapping[str, object]] = (),
) -> dict[str, object]:
    """Bind export-local axial and original physical-key rows to output and source."""

    source_path = source_asset_path(repository_root, preset)
    try:
        source_relative_path = source_path.relative_to(repository_root).as_posix()
    except ValueError as exc:
        raise ValueError(f"MPFB source asset is outside the repository: {source_path}") from exc
    payload = json.dumps({"meshes": meshes, "skipped_meshes": skipped_meshes}, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return {
        "schema_version": SCHEMA_VERSION,
        "producer": PRODUCER,
        "generation": {
            "id": hashlib.sha256(payload).hexdigest(),
            "output_file_name": output_path.name,
            "output_sha256": sha256_file(output_path),
        },
        "source_asset": {
            "preset": preset,
            "path": source_relative_path,
            "sha256": sha256_file(source_path),
        },
        "meshes": list(meshes),
        "skipped_meshes": list(skipped_meshes),
    }


def write_evidence(
    repository_root: Path,
    output_path: Path,
    preset: str,
    meshes: Sequence[Mapping[str, object]],
    skipped_meshes: Sequence[Mapping[str, object]] = (),
) -> Path:
    """Persist finalised generator-run ownership evidence after the final blend save."""

    evidence_path = sidecar_path(output_path)
    evidence = build_evidence(repository_root, output_path, preset, meshes, skipped_meshes)
    evidence_path.write_text(json.dumps(evidence, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return evidence_path


def load_evidence(repository_root: Path, output_path: Path) -> dict[str, object]:
    """Load only evidence linked to the exact output and installed source asset."""

    evidence_path = sidecar_path(output_path)
    if not evidence_path.is_file():
        raise ValueError(f"Missing generator-run ownership validation sidecar: {evidence_path}")
    try:
        with evidence_path.open(encoding="utf-8") as source:
            evidence = json.load(source)
    except (OSError, json.JSONDecodeError) as exc:
        raise ValueError(f"Invalid generator-run ownership validation sidecar: {evidence_path}") from exc
    if not isinstance(evidence, dict):
        raise ValueError(f"Invalid generator-run ownership validation sidecar: {evidence_path}")
    if evidence.get("schema_version") != SCHEMA_VERSION or evidence.get("producer") != PRODUCER:
        raise ValueError(f"Unsupported generator-run ownership validation sidecar: {evidence_path}")

    generation = evidence.get("generation")
    source_asset = evidence.get("source_asset")
    if not isinstance(generation, dict) or not isinstance(source_asset, dict):
        raise ValueError(f"Invalid generator-run ownership validation linkage: {evidence_path}")
    if generation.get("output_file_name") != output_path.name:
        raise ValueError(f"Generator-run ownership validation output name mismatch: {evidence_path}")
    if generation.get("output_sha256") != sha256_file(output_path):
        raise ValueError(f"Stale generator-run ownership validation sidecar: {evidence_path}")
    source_relative_path = source_asset.get("path")
    source_digest = source_asset.get("sha256")
    preset = source_asset.get("preset")
    if (
        not isinstance(source_relative_path, str)
        or not isinstance(source_digest, str)
        or not isinstance(preset, str)
    ):
        raise ValueError(f"Invalid generator-run ownership validation source linkage: {evidence_path}")
    source_path = (repository_root / source_relative_path).resolve()
    try:
        expected_source_path = source_asset_path(repository_root, preset).resolve()
    except ValueError as exc:
        raise ValueError(f"Invalid generator-run ownership validation source asset: {evidence_path}") from exc
    if source_path != expected_source_path:
        raise ValueError(f"Generator-run ownership validation source asset mismatch: {evidence_path}")
    if not source_path.is_relative_to(repository_root.resolve()) or not source_path.is_file():
        raise ValueError(f"Missing generator-run ownership validation source asset: {evidence_path}")
    if source_digest != sha256_file(source_path):
        raise ValueError(f"Stale generator-run ownership validation source asset: {evidence_path}")
    if not isinstance(evidence.get("meshes"), list) or any(
        not isinstance(mesh, dict) or not isinstance(mesh.get("original_zero_rows"), list)
        or not isinstance(mesh.get("original_positive_rows"), list)
        or not isinstance(mesh.get("source_domain"), dict)
        for mesh in evidence["meshes"]
    ):
        raise ValueError(f"Invalid generator-run ownership validation rows: {evidence_path}")
    if not isinstance(evidence.get("skipped_meshes"), list) or any(
        not isinstance(mesh, dict) for mesh in evidence["skipped_meshes"]
    ):
        raise ValueError(f"Invalid generator-run skipped mesh rows: {evidence_path}")
    payload = json.dumps({"meshes": evidence["meshes"], "skipped_meshes": evidence["skipped_meshes"]},
                         sort_keys=True, separators=(",", ":")).encode("utf-8")
    if generation.get("id") != hashlib.sha256(payload).hexdigest():
        raise ValueError(f"Generator-run ownership validation generation mismatch: {evidence_path}")
    return evidence


def compare_skipped_mesh(
    record: Mapping[str, object], name: str, groups: Sequence[tuple[int, str]],
    final_rows: Mapping[int, Mapping[str, float]], tolerance: float,
) -> int:
    """Require complete unchanged physical membership on a non-axial export mesh."""

    context = f"{name}: skipped mesh physical membership"
    if record.get("name") != name or type(record.get("vertex_count")) is not int:
        raise ValueError(f"{context}: invalid mesh identity or vertex count")
    group_indices = record.get("group_indices")
    if (not isinstance(group_indices, list) or not all(
        isinstance(group, list) and len(group) == 2 and type(group[0]) is int
        and group[0] >= 0 and isinstance(group[1], str) and group[1]
        for group in group_indices
    ) or len({group[0] for group in group_indices}) != len(group_indices)
            or len({group[1] for group in group_indices}) != len(group_indices)
            or group_indices != [[index, group_name] for index, group_name in groups]
            or len({index for index, _ in groups}) != len(groups)
            or len({group_name for _, group_name in groups}) != len(groups)):
        raise ValueError(f"{context}: ambiguous or changed group index mapping")
    expected = rows_by_vertex(record.get("original_physical_rows"), context)
    count = record["vertex_count"]
    if set(expected) != set(range(count)) or set(final_rows) != set(range(count)):
        raise ValueError(f"{context}: physical vertex count or ordering changed")
    known = {group_name for _, group_name in groups}
    for index in range(count):
        before, after = expected[index], final_rows[index]
        if set(before) != set(after) or not set(after) <= known:
            raise ValueError(f"{context} vertex {index}: physical membership changed")
        for group_name, weight in before.items():
            saved = after[group_name]
            if (not math.isfinite(weight) or not math.isfinite(saved)
                    or not 0.0 <= weight <= 1.0 or not 0.0 <= saved <= 1.0
                    or (weight == 0.0) != (saved == 0.0)
                    or abs(weight - saved) > tolerance):
                raise ValueError(f"{context} vertex {index}: physical membership changed for {group_name}")
    return count


def rows_by_vertex(rows: object, context: str) -> dict[int, dict[str, float]]:
    """Validate and index one mesh's complete authored axial ownership rows."""

    if not isinstance(rows, list):
        raise ValueError(f"{context}: missing authored axial ownership rows")
    indexed: dict[int, dict[str, float]] = {}
    for row in rows:
        if not isinstance(row, dict) or not isinstance(row.get("vertex"), int):
            raise ValueError(f"{context}: malformed authored axial ownership row")
        vertex = row["vertex"]
        weights = row.get("weights")
        if vertex < 0 or not isinstance(weights, dict) or any(
            not isinstance(name, str) or not isinstance(weight, (int, float))
            for name, weight in weights.items()
        ):
            raise ValueError(f"{context}: malformed authored axial ownership row")
        if vertex in indexed:
            raise ValueError(f"{context}: duplicate authored axial vertex {vertex}")
        indexed[vertex] = {name: float(weight) for name, weight in weights.items()}
    return indexed


def side_comparison_candidate(
    reference: Mapping[str, float], candidate: Mapping[str, float], helper: str
) -> dict[str, float]:
    """Hold the independently checked opposite side fixed for a per-side comparison."""

    opposite = {
        "LeftForearmTwist": "RightForearmTwist",
        "RightForearmTwist": "LeftForearmTwist",
    }.get(helper)
    result = dict(candidate)
    if opposite is None:
        return result
    if opposite in reference:
        result[opposite] = reference[opposite]
    else:
        result.pop(opposite, None)
    return result


def compare_physical_rows(
    axial_rows: object,
    final_rows: Mapping[int, Mapping[str, float]],
    authoring_eligible: object,
    sides: Sequence[tuple[str, str, str]],
    tolerance: float,
    context: str,
) -> tuple[int, float]:
    """Check positive physical masses; zero keys are checked by the separate ledger.

    Blender stores vertex weights as float32, so positive numeric comparison
    permits rounding but never a missing/added positive non-authoring membership.
    """

    reference_rows = rows_by_vertex(axial_rows, context)
    if set(reference_rows) != set(final_rows):
        raise ValueError(f"{context}: authored axial and final vertex sets differ")
    if not isinstance(authoring_eligible, dict) or set(authoring_eligible) != {side[1] for side in sides}:
        raise ValueError(f"{context}: missing physical authoring eligibility")
    eligible: dict[str, set[int]] = {}
    for helper, vertices in authoring_eligible.items():
        if (not isinstance(vertices, list) or any(type(index) is not int for index in vertices)
                or len(set(vertices)) != len(vertices) or not set(vertices) <= set(reference_rows)):
            raise ValueError(f"{context}: invalid physical authoring eligibility for {helper}")
        eligible[helper] = set(vertices)
    maximum_error = 0.0
    for vertex, authored_reference in reference_rows.items():
        candidate = final_rows[vertex]
        if any(not math.isfinite(weight) or weight < 0.0 for weight in candidate.values()):
            raise ValueError(f"{context} vertex {vertex}: invalid physical final membership")
        if any(not math.isfinite(weight) or weight < 0.0 for weight in authored_reference.values()):
            raise ValueError(f"{context} vertex {vertex}: invalid physical reference membership")
        # An authored logical zero is not proof of a physical zero membership.
        # The independent export-local ledger checks original physical zero keys.
        reference = {name: weight for name, weight in authored_reference.items() if weight > 0.0}
        if is_source_bilateral(reference) and len(reference) != len(authored_reference):
            raise ValueError(f"{context} vertex {vertex}: protected physical zero membership")
        permitted: set[str] = set()
        for _lower, helper, _hand in sides:
            if vertex in eligible[helper]:
                if is_source_bilateral(reference) or reference.get(helper, 0.0) <= 0.0:
                    raise ValueError(f"{context} vertex {vertex}: invalid physical authoring eligibility")
                permitted.add(helper)
                error = abs(reference.get(helper, 0.0) - candidate.get(helper, 0.0))
                maximum_error = max(maximum_error, error)
                if error > tolerance:
                    raise ValueError(f"{context} vertex {vertex}: physical helper mass changed")
        before = set(reference) - permitted
        after = {name for name, weight in candidate.items() if weight > 0.0} - permitted
        if before != after:
            raise ValueError(f"{context} vertex {vertex}: physical group membership changed")
        for name in before:
            error = abs(reference[name] - candidate[name])
            maximum_error = max(maximum_error, error)
            if error > tolerance:
                raise ValueError(f"{context} vertex {vertex}: physical influence {name!r} changed")
    return len(reference_rows), maximum_error


def compare_zero_ledger(
    original_zero_rows: object,
    axial_rows: object,
    final_rows: Mapping[int, Mapping[str, float]],
    authoring_eligible: object,
    sides: Sequence[tuple[str, str, str]],
    context: str,
    original_positive_rows: object,
    finger_groups: Mapping[str, set[str]] | None = None,
) -> int:
    """Compare export-local physical keys, independently of positive axial mass.

    Sparse ledger rows contain original zero and positive keys separately.
    Both ledgers are required; older zero-only sidecars fail closed.
    Promotion additionally requires original positive same-side lower/hand
    ownership without positive opposite or finger ownership. Finger identities
    come from the saved armature, not suffix inference. Geometry/topology and
    beta eligibility still rely on the generator's authored eligibility list.
    No finger-ghost deletion is attributed here.
    Float32 zero is exactly 0.0 (including negative zero); no epsilon may
    reclassify a positive assignment as a retained zero membership.
    """

    axial = rows_by_vertex(axial_rows, context)
    if (not isinstance(original_zero_rows, list) or not isinstance(original_positive_rows, list)
            or not isinstance(authoring_eligible, dict)):
        raise ValueError(f"{context}: missing physical key ledger or eligibility")
    if set(authoring_eligible) != {side[1] for side in sides}:
        raise ValueError(f"{context}: invalid physical zero eligibility")
    eligible = {}
    for helper, indices in authoring_eligible.items():
        if (not isinstance(indices, list) or any(type(index) is not int for index in indices)
                or len(indices) != len(set(indices)) or not set(indices) <= set(axial)):
            raise ValueError(f"{context}: invalid physical zero eligibility")
        eligible[helper] = set(indices)
    zeros: dict[int, set[str]] = {}
    for row in original_zero_rows:
        if not isinstance(row, dict) or set(row) != {"vertex", "zeros"}:
            raise ValueError(f"{context}: malformed physical zero ledger row")
        index, names = row["vertex"], row["zeros"]
        if (type(index) is not int or index not in axial or index in zeros
                or not isinstance(names, list) or not names
                or any(not isinstance(name, str) or not name for name in names)
                or len(names) != len(set(names))):
            raise ValueError(f"{context}: malformed physical zero ledger row")
        zeros[index] = set(names)
    positives: dict[int, set[str]] = {}
    for row in original_positive_rows:
        if not isinstance(row, dict) or set(row) != {"vertex", "positives"}:
            raise ValueError(f"{context}: malformed physical positive ledger row")
        index, names = row["vertex"], row["positives"]
        if (type(index) is not int or index not in axial or index in positives
                or not isinstance(names, list) or not names
                or any(not isinstance(name, str) or not name for name in names)
                or len(names) != len(set(names)) or set(names) & zeros.get(index, set())):
            raise ValueError(f"{context}: malformed physical positive ledger row")
        positives[index] = set(names)
    if set(final_rows) != set(axial):
        raise ValueError(f"{context}: physical zero vertex set differs")
    side_by_helper = {helper: (lower, hand) for lower, helper, hand in sides}
    valid_fingers = (finger_groups is not None and set(finger_groups) == set(side_by_helper)
                     and all(isinstance(names, (set, frozenset))
                             and all(isinstance(name, str) for name in names)
                             for names in finger_groups.values()))
    all_fingers = set().union(*finger_groups.values()) if valid_fingers else set()
    count = 0
    for index, candidate in final_rows.items():
        original = zeros.get(index, set())
        for name in original:
            weight = candidate.get(name)
            if weight == 0.0:
                count += 1
                continue
            # The axial row and eligibility are authored evidence, not a proof
            # of which physical groups owned the export-stage vertex.
            lower, hand = side_by_helper.get(name, (None, None))
            positive = positives.get(index, set())
            opposite = {group for other_lower, other_helper, other_hand in sides
                        if other_helper != name
                        for group in (other_lower, other_helper, other_hand)}
            wrong_suffix = "_r" if lower is not None and lower.endswith("_l") else "_l"
            original_side = (valid_fingers and bool(positive & {lower, hand})
                             and not positive & all_fingers
                             and not positive & opposite
                             and not any(group.endswith(wrong_suffix) for group in positive))
            promotable = (name in eligible and index in eligible[name]
                          and original_side
                          and not is_source_bilateral(axial[index])
                          and axial[index].get(name, 0.0) > 0.0
                          and weight is not None and math.isfinite(weight) and weight > 0.0)
            if not promotable:
                raise ValueError(f"{context} vertex {index}: lost physical zero {name!r}")
        # An original positive can reach physical zero only as its authored axial
        # mass legitimately concentrates onto the helper within the eligible pool.
        retained = set()
        for lower, helper, hand in sides:
            if index not in eligible[helper] or is_source_bilateral(axial[index]):
                continue
            for name in positives.get(index, set()) & {lower, helper, hand}:
                if name == helper or axial[index].get(name, 0.0) == 0.0:
                    retained.add(name)
        invented = {name for name, weight in candidate.items() if weight == 0.0} - original - retained
        if invented:
            raise ValueError(f"{context} vertex {index}: invented physical zero {sorted(invented)}")
    return count


def compare_axial_rows(
    axial_rows: object,
    final_rows: Mapping[int, Mapping[str, float]],
    deform_group_names: set[str],
    lower_arm: str,
    helper: str,
    hand: str,
    tolerance: float,
    context: str,
) -> tuple[int, float]:
    """Compare the immutable authored axial reference with final mesh ownership."""

    reference_rows = rows_by_vertex(axial_rows, context)
    if set(reference_rows) != set(final_rows):
        raise ValueError(f"{context}: authored axial and final vertex sets differ")
    maximum_error = 0.0
    for vertex, reference in reference_rows.items():
        try:
            before, after = assert_axial_ownership_contract(
                reference,
                side_comparison_candidate(reference, final_rows[vertex], helper),
                deform_group_names,
                lower_arm,
                helper,
                hand,
                tolerance,
            )
        except ValueError as exc:
            raise ValueError(f"{context} vertex {vertex}: {exc}") from exc
        maximum_error = max(maximum_error, abs(before.get(helper, 0.0) - after.get(helper, 0.0)))
    return len(reference_rows), maximum_error
