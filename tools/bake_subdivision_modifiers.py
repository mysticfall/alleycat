#!/usr/bin/env python3
"""Bake SUBSURF subdivision into exported character meshes.

The character generator exports meshes whose SUBSURF modifiers hold the
intended subdivision on ``render_levels`` while ``levels`` stays at zero. The
Godot import sidecar exports raw mesh data (``blender/nodes/modifiers=0``), so
that subdivision would be silently lost. This module folds the subdivision
geometry into the export meshes themselves, before any downstream
topology-sensitive processing runs.

Blender cannot apply modifiers on meshes that carry shape keys, so keyed
meshes are baked by evaluation and delta reconstruction: with every other
modifier temporarily disabled, the dependency graph is evaluated once with all
shape key values at zero (giving the subdivided basis, which becomes the new
mesh data) and once per key with only that key at 1.0. The full per-vertex
delta of each key against the basis is stored with no significance threshold
and replayed as a reconstructed shape key on the subdivided mesh, preserving
key names and order exactly. Vertex-group weights, UV layers and materials
travel with the evaluated mesh data.

Only SUBSURF modifiers with ``render_levels > 0`` are consumed. ARMATURE and
MASK modifiers are always left live on the object with their prior visibility
restored.

The module imports no Blender code at import time; ``bpy`` is resolved inside
the functions so plain-Python unit tests can inject a fake module.
"""

from __future__ import annotations

from array import array
from typing import Callable, Iterable, Sequence


class BakeError(RuntimeError):
    """Raised when a subdivision bake cannot preserve a mesh's contract."""


def _bpy():
    """Return Blender's ``bpy`` module, imported lazily for fake-bpy tests."""

    import bpy

    return bpy


def compute_key_deltas(
    basis_coords: Sequence[float], key_coords: Sequence[float]
) -> list[float]:
    """Return full per-vertex deltas (key minus basis) with no cutoff."""

    if len(basis_coords) != len(key_coords):
        raise BakeError(
            f"Shape key evaluation produced {len(key_coords) // 3} vertices but "
            f"the subdivided basis has {len(basis_coords) // 3}; the subdivision "
            "topology changed between evaluations."
        )
    return [key - basis for basis, key in zip(basis_coords, key_coords)]


def reconstructed_key_coordinates(
    basis_coords: Sequence[float], deltas: Sequence[float]
) -> list[float]:
    """Return subdivided basis coordinates plus one key's stored deltas."""

    if len(basis_coords) != len(deltas):
        raise BakeError(
            f"Shape key reconstruction holds {len(deltas) // 3} delta vertices "
            f"but the subdivided basis has {len(basis_coords) // 3}."
        )
    return [basis + delta for basis, delta in zip(basis_coords, deltas)]


def _evaluated_mesh_duplicate(obj) -> object:
    """Return an owned copy of the object's fully evaluated mesh data.

    The evaluated mesh carries the subdivided geometry, UV layers, materials
    and vertex-group weights, and arrives keyless because the current shape key
    mix is baked into its coordinates.
    """

    bpy = _bpy()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    try:
        return mesh.copy()
    finally:
        evaluated.to_mesh_clear()


def _evaluated_vertex_coordinates(obj) -> list[float]:
    """Return the flat vertex coordinates of the object's evaluated mesh."""

    bpy = _bpy()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
    try:
        coords = [0.0] * (3 * len(mesh.vertices))
        mesh.vertices.foreach_get("co", coords)
        return coords
    finally:
        evaluated.to_mesh_clear()


def _consumed_subsurf_modifiers(obj) -> list:
    """Return the SUBSURF modifiers whose render subdivision must be baked."""

    return [
        modifier
        for modifier in obj.modifiers
        if modifier.type == "SUBSURF" and modifier.render_levels > 0
    ]


def _strip_shape_keys(mesh, mesh_name: str, log: Callable[[str], None]) -> None:
    """Drop an unexpected shape key container arriving on evaluated data."""

    bpy = _bpy()
    keys = mesh.shape_keys
    bpy.data.batch_remove((keys,))
    log(
        f'Discarded a stray shape key container on the evaluated mesh of "{mesh_name}".'
    )


def _reconstruct_shape_keys(
    obj,
    basis_coords: Sequence[float],
    key_snapshots: list[tuple[str, float, bool, float, float]],
    key_deltas: list[array],
    log: Callable[[str], None],
) -> None:
    """Rebuild every original shape key on the object's new subdivided mesh."""

    if len(key_deltas) != len(key_snapshots) - 1:
        raise BakeError(
            f'Captured {len(key_deltas)} shape key deltas for "{obj.name}" but its '
            f"original mesh held {len(key_snapshots) - 1} non-basis keys."
        )

    basis_name = key_snapshots[0][0]
    basis_block = obj.shape_key_add(name=basis_name, from_mix=False)
    basis_block.data.foreach_set("co", basis_coords)
    basis_block.slider_min = key_snapshots[0][3]
    basis_block.slider_max = key_snapshots[0][4]
    basis_block.value = key_snapshots[0][1]
    basis_block.mute = key_snapshots[0][2]

    for (name, value, mute, slider_min, slider_max), deltas in zip(
        key_snapshots[1:], key_deltas
    ):
        block = obj.shape_key_add(name=name, from_mix=False)
        block.relative_key = basis_block
        block.slider_min = slider_min
        block.slider_max = slider_max
        block.value = value
        block.mute = mute
        block.data.foreach_set("co", reconstructed_key_coordinates(basis_coords, deltas))

    reconstructed_names = [block.name for block in obj.data.shape_keys.key_blocks]
    expected_names = [snapshot[0] for snapshot in key_snapshots]
    if reconstructed_names != expected_names:
        raise BakeError(
            f'Reconstructed shape keys on "{obj.name}" as {reconstructed_names} but '
            f"the original mesh held {expected_names}."
        )


def _bake_mesh_object(obj, log: Callable[[str], None]) -> bool:
    """Bake the render subdivision of one mesh object in place.

    Returns ``True`` when the object was baked, ``False`` when it carries no
    SUBSURF modifier with render subdivision and was left untouched.
    """

    bpy = _bpy()
    consumed = _consumed_subsurf_modifiers(obj)
    if not consumed:
        return False

    if obj.mode != "OBJECT":
        raise BakeError(
            f'Cannot bake subdivision on "{obj.name}" while it is in {obj.mode} mode.'
        )

    old_mesh = obj.data
    shape_keys = old_mesh.shape_keys
    if shape_keys is not None and not shape_keys.use_relative:
        raise BakeError(
            f'Cannot bake subdivision on "{obj.name}": its absolute shape keys '
            "cannot be reconstructed as relative keys."
        )
    key_blocks = list(shape_keys.key_blocks) if shape_keys is not None else []
    key_snapshots = [
        (block.name, block.value, block.mute, block.slider_min, block.slider_max)
        for block in key_blocks
    ]
    old_active_index = obj.active_shape_key_index if key_blocks else -1
    old_vertex_count = len(old_mesh.vertices)

    # The consumed modifiers are removed at the end of the bake, so their
    # visibility states need no restoration; forcing them on ensures the
    # dependency graph applies them during evaluation.
    for modifier in consumed:
        modifier.levels = modifier.render_levels
        modifier.show_viewport = True
        modifier.show_render = True

    consumed_names = {modifier.name for modifier in consumed}
    preserved = [
        (modifier, modifier.show_viewport, modifier.show_render)
        for modifier in obj.modifiers
        if modifier.name not in consumed_names
    ]
    for modifier, _viewport, _render in preserved:
        modifier.show_viewport = False
        modifier.show_render = False

    for block in key_blocks:
        block.value = 0.0
        block.mute = False

    try:
        log(
            f'Baking subdivision on "{obj.name}": {old_vertex_count} vertices, '
            f'{len(key_blocks)} shape keys; consuming '
            f'{", ".join(modifier.name for modifier in consumed)}.'
        )

        new_mesh = _evaluated_mesh_duplicate(obj)
        basis_coords = [0.0] * (3 * len(new_mesh.vertices))
        new_mesh.vertices.foreach_get("co", basis_coords)

        if len(new_mesh.polygons) and len(new_mesh.vertices) <= old_vertex_count:
            raise BakeError(
                f'Subdivision evaluation on "{obj.name}" produced '
                f'{len(new_mesh.vertices)} vertices from {old_vertex_count}; the '
                "modifiers were not applied, so the mesh would bake unsubdivided."
            )

        if new_mesh.shape_keys is not None:
            _strip_shape_keys(new_mesh, obj.name, log)

        key_deltas: list[array] = []
        for block in key_blocks[1:]:
            for other in key_blocks:
                other.value = 1.0 if other.name == block.name else 0.0
            key_coords = _evaluated_vertex_coordinates(obj)
            key_deltas.append(
                array("f", compute_key_deltas(basis_coords, key_coords))
            )
    finally:
        for (_name, value, mute, _slider_min, _slider_max), block in zip(
            key_snapshots, key_blocks
        ):
            block.value = value
            block.mute = mute
        for modifier, viewport, render in preserved:
            modifier.show_viewport = viewport
            modifier.show_render = render

    old_mesh_name = old_mesh.name
    obj.data = new_mesh
    for modifier in consumed:
        obj.modifiers.remove(modifier)
    if old_mesh.users == 0:
        bpy.data.meshes.remove(old_mesh)
        new_mesh.name = old_mesh_name
    else:
        log(
            f'Kept the unsubdivided mesh data "{old_mesh_name}" of "{obj.name}" '
            f"because it still has {old_mesh.users} other users."
        )

    if key_blocks:
        _reconstruct_shape_keys(obj, basis_coords, key_snapshots, key_deltas, log)
        if 0 <= old_active_index < len(obj.data.shape_keys.key_blocks):
            obj.active_shape_key_index = old_active_index

    left_types = ", ".join(modifier.type for modifier in obj.modifiers) or "none"
    log(
        f'Baked subdivision on "{obj.name}": {old_vertex_count} -> '
        f'{len(new_mesh.vertices)} vertices; shape keys {len(key_blocks)} -> '
        f'{len(key_blocks)}; modifiers left: {left_types}.'
    )
    return True


def bake_subdivision_modifiers(
    objects: Iterable[object],
    log: Callable[[str], None] = print,
) -> list[str]:
    """Bake SUBSURF render subdivision into every given mesh object.

    Each mesh carrying a SUBSURF modifier with ``render_levels > 0`` has that
    subdivision folded into its mesh data, with shape keys, vertex groups, UV
    layers and materials preserved and every non-SUBSURF modifier left live.
    Meshes without render subdivision, and non-mesh objects, are skipped.

    Returns the names of the objects whose meshes were baked.
    """

    baked: list[str] = []
    for obj in list(objects):
        if getattr(obj, "type", None) != "MESH":
            continue
        if _bake_mesh_object(obj, log):
            baked.append(obj.name)
        else:
            log(
                f'Skipped "{obj.name}": no SUBSURF modifier with render subdivision; '
                "mesh left unchanged."
            )
    return baked
