"""Unit tests for the subdivision baker in tools/bake_subdivision_modifiers.py.

The baker runs against a fake ``bpy`` that simulates the parts of Blender's
dependency graph it touches: evaluated meshes produced by a fixed affine
subdivision transform, relative shape key mixing, modifier visibility,
data-block user accounting and ID removal. No Blender installation is needed,
and the module under test resolves ``bpy`` only at call time.
"""

from __future__ import annotations

import importlib.util
import sys
import types
import unittest
from contextlib import contextmanager
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
BAKE_MODULE_PATH = REPO_ROOT / "tools" / "bake_subdivision_modifiers.py"
MODULE_NAME = "bake_subdivision_modifiers_under_test"

# One fake subdivision level: every source vertex keeps its position and gains
# a second vertex offset by a constant. The transform is affine, so the key
# deltas reconstructed by the baker must reproduce the transform of the mixed
# mesh exactly, and all values are dyadic so float arithmetic stays exact.
SUBDIVISION_OFFSET = (0.5, 0.25, -0.125)

DEFAULT_BASIS = [
    (0.0, 0.0, 0.0),
    (1.0, 0.5, -0.25),
    (-2.0, 1.0, 0.75),
    (0.25, -1.5, 2.0),
]
DEFAULT_KEY_DELTAS = {
    "KeyA": [(0.25, 0.0, -0.125), (-0.5, 0.75, 0.0), (1.0, -0.25, 0.5), (0.0, 1.0, -2.0)],
    "KeyB": [(0.0, 0.0, 2.0), (0.125, 0.125, 0.125), (-1.0, 0.5, 0.25), (0.75, 0.0, 0.0)],
}


def load_bake_module(fake_bpy=None):
    """Load a fresh baker instance, optionally with a stub bpy installed."""

    previous_bpy = sys.modules.get("bpy")
    if fake_bpy is not None:
        sys.modules["bpy"] = fake_bpy
    sys.modules.pop(MODULE_NAME, None)
    spec = importlib.util.spec_from_file_location(MODULE_NAME, BAKE_MODULE_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load module spec for {BAKE_MODULE_PATH}")
    module = importlib.util.module_from_spec(spec)
    sys.modules[MODULE_NAME] = module
    try:
        spec.loader.exec_module(module)
    finally:
        if fake_bpy is not None:
            if previous_bpy is None:
                sys.modules.pop("bpy", None)
            else:
                sys.modules["bpy"] = previous_bpy
    return module


# Loaded once with no bpy present anywhere: the baker must import cleanly
# without Blender because it defers every bpy lookup to call time.
BAKE = load_bake_module()


@contextmanager
def installed_fake_bpy(fake_bpy):
    """Install a stub bpy for the duration of a baker invocation."""

    previous_bpy = sys.modules.get("bpy")
    sys.modules["bpy"] = fake_bpy
    try:
        yield fake_bpy
    finally:
        if previous_bpy is None:
            sys.modules.pop("bpy", None)
        else:
            sys.modules["bpy"] = previous_bpy


def flattened(coords):
    """Flatten an iterable of xyz triples into a component list."""

    return [component for triple in coords for component in triple]


def subdivided(coords):
    """Apply the fake subdivision transform to a flat coordinate list."""

    offset = SUBDIVISION_OFFSET
    extra = []
    for position in range(0, len(coords), 3):
        extra.extend(
            (
                coords[position] + offset[0],
                coords[position + 1] + offset[1],
                coords[position + 2] + offset[2],
            )
        )
    return list(coords) + extra


class FakeVertexGroupAssignment:
    def __init__(self, group: int, weight: float) -> None:
        self.group = group
        self.weight = weight


class FakeVertexGroup:
    def __init__(self, name: str, index: int) -> None:
        self.name = name
        self.index = index


class FakeVertex:
    def __init__(self, co, groups=()) -> None:
        self.co = tuple(float(component) for component in co)
        self.groups = list(groups)
        self.index = 0


class FakeVertices:
    def __init__(self, vertices) -> None:
        self._vertices = list(vertices)
        for position, vertex in enumerate(self._vertices):
            vertex.index = position

    def __len__(self) -> int:
        return len(self._vertices)

    def __iter__(self):
        return iter(self._vertices)

    def __getitem__(self, index):
        return self._vertices[index]

    def foreach_get(self, attr, target) -> None:
        if attr != "co":
            raise AssertionError(f"Unexpected foreach_get attribute {attr!r}")
        for position, vertex in enumerate(self._vertices):
            target[position * 3 : position * 3 + 3] = vertex.co


class FakeKeyBlockData:
    """Backing store for one key block's flat coordinates."""

    def __init__(self, coords) -> None:
        self.coords = list(coords)

    def foreach_set(self, attr, source) -> None:
        if attr != "co":
            raise AssertionError(f"Unexpected foreach_set attribute {attr!r}")
        if len(source) != len(self.coords):
            raise AssertionError(
                f"foreach_set length {len(source)} does not match block size {len(self.coords)}"
            )
        self.coords = list(source)


class FakeKeyBlock:
    """A relative shape key block carrying its own evaluation displacement."""

    def __init__(
        self,
        name,
        coords,
        evaluation_delta=None,
        value=0.0,
        mute=False,
        slider_min=0.0,
        slider_max=1.0,
    ) -> None:
        self.name = name
        self.slider_min = slider_min
        self.slider_max = slider_max
        self._value = 0.0
        self.value = value
        self.mute = mute
        self.relative_key = None
        self.data = FakeKeyBlockData(coords)
        self.evaluation_delta = flattened(
            evaluation_delta or [(0.0, 0.0, 0.0)] * (len(coords) // 3)
        )

    @property
    def value(self) -> float:
        return self._value

    @value.setter
    def value(self, new_value: float) -> None:
        # Stricter than Blender's RNA range, but it catches assignment ordering
        # where a restored value would be clamped by stale slider bounds.
        self._value = min(max(new_value, self.slider_min), self.slider_max)


class FakeShapeKeys:
    def __init__(self, blocks, use_relative=True, owner_mesh=None) -> None:
        self.key_blocks = list(blocks)
        self.use_relative = use_relative
        self.owner_mesh = owner_mesh


class FakePolygon:
    pass


class FakeMesh:
    def __init__(
        self,
        name,
        vertices,
        polygons=None,
        shape_keys=None,
        uv_layers=None,
        materials=None,
        vertex_groups=(),
    ) -> None:
        self.name = name
        self.vertices = FakeVertices(vertices)
        self.polygons = list(polygons) if polygons is not None else [FakePolygon()]
        self.shape_keys = shape_keys
        self.uv_layers = list(uv_layers) if uv_layers is not None else [object()]
        self.materials = list(materials) if materials is not None else [object()]
        self.vertex_groups = list(vertex_groups)
        self.users = 0

    def copy(self):
        duplicate = FakeMesh(
            f"{self.name}.001",
            [
                FakeVertex(
                    vertex.co,
                    [
                        FakeVertexGroupAssignment(assignment.group, assignment.weight)
                        for assignment in vertex.groups
                    ],
                )
                for vertex in self.vertices
            ],
            polygons=list(self.polygons),
            uv_layers=list(self.uv_layers),
            materials=list(self.materials),
            vertex_groups=[
                FakeVertexGroup(group.name, group.index) for group in self.vertex_groups
            ],
        )
        stray = getattr(self, "stray_shape_keys", None)
        if stray is not None:
            stray.owner_mesh = duplicate
            duplicate.shape_keys = stray
        return duplicate


class FakeModifiers:
    def __init__(self, modifiers=()) -> None:
        self._modifiers = list(modifiers)

    def __iter__(self):
        return iter(self._modifiers)

    def __len__(self) -> int:
        return len(self._modifiers)

    def __getitem__(self, index):
        return self._modifiers[index]

    def remove(self, modifier) -> None:
        self._modifiers.remove(modifier)


class FakeModifier:
    """A modifier; only SUBSURF exposes levels, mirroring Blender's RNA."""

    def __init__(
        self, type_, name, show_viewport=True, show_render=True, levels=0, render_levels=0
    ) -> None:
        self.type = type_
        self.name = name
        self.show_viewport = show_viewport
        self.show_render = show_render
        if type_ == "SUBSURF":
            self.levels = levels
            self.render_levels = render_levels


class FakeEvaluatedObject:
    def __init__(self, engine, obj) -> None:
        self._engine = engine
        self._obj = obj

    def to_mesh(self, preserve_all_data_layers=False, depsgraph=None):
        self._engine.to_mesh_calls += 1
        self._engine.to_mesh_flags.append(preserve_all_data_layers)
        return self._engine.evaluate(self._obj, preserve_all_data_layers)

    def to_mesh_clear(self):
        self._engine.to_mesh_clear_calls += 1


class FakeEngine:
    """Dependency-graph stand-in recording every evaluation and ID removal."""

    def __init__(self) -> None:
        self.evaluations = []
        self.to_mesh_calls = 0
        self.to_mesh_clear_calls = 0
        self.to_mesh_flags = []
        self.removed_meshes = []
        self.batch_removed = []
        self.subdiv_factor = 2
        self.mismatch_at_evaluation = None
        self.attach_stray_shape_keys = False

    def make_bpy_module(self):
        fake_bpy = types.ModuleType("bpy")
        fake_bpy.context = types.SimpleNamespace(evaluated_depsgraph_get=lambda: object())
        fake_bpy.data = types.SimpleNamespace(
            meshes=types.SimpleNamespace(remove=self.removed_meshes.append),
            batch_remove=self._batch_remove,
        )
        return fake_bpy

    def _batch_remove(self, ids) -> None:
        for identifier in ids:
            owner = getattr(identifier, "owner_mesh", None)
            if owner is not None:
                owner.shape_keys = None
            self.batch_removed.append(identifier)

    def evaluate(self, obj, preserve_all_data_layers):
        source = obj.data
        subsurf_active = any(
            modifier.type == "SUBSURF" and modifier.show_viewport and modifier.levels > 0
            for modifier in obj.modifiers
        )
        modifier_states = [
            (
                modifier.name,
                modifier.type,
                modifier.show_viewport,
                modifier.show_render,
                getattr(modifier, "levels", None),
            )
            for modifier in obj.modifiers
        ]
        coords = []
        for vertex in source.vertices:
            coords.extend(vertex.co)
        key_values = {}
        if source.shape_keys is not None:
            key_values = {block.name: block.value for block in source.shape_keys.key_blocks}
            for block in source.shape_keys.key_blocks[1:]:
                if block.mute or block.value == 0.0:
                    continue
                coords = [
                    coordinate + block.value * delta
                    for coordinate, delta in zip(coords, block.evaluation_delta)
                ]
        self.evaluations.append(
            types.SimpleNamespace(
                modifier_states=modifier_states,
                key_values=key_values,
                subsurf_active=subsurf_active,
            )
        )

        if preserve_all_data_layers:
            assignments = [
                [
                    FakeVertexGroupAssignment(assignment.group, assignment.weight)
                    for assignment in vertex.groups
                ]
                for vertex in source.vertices
            ]
        else:
            assignments = [[] for _ in source.vertices]
        if subsurf_active and self.subdiv_factor > 1:
            coords = subdivided(coords)
            # Subdivision keeps the source vertices first and appends new
            # geometry, mirroring the vertex ordering verified against Blender.
            assignments = assignments + [[] for _ in range(len(coords) // 3 - len(assignments))]
        if self.mismatch_at_evaluation == len(self.evaluations):
            coords = coords + [0.0, 0.0, 0.0]
            assignments = assignments + [[]]

        vertices = [
            FakeVertex(
                (coords[position * 3], coords[position * 3 + 1], coords[position * 3 + 2]),
                vertex_assignments,
            )
            for position, vertex_assignments in enumerate(assignments)
        ]
        evaluated = FakeMesh(
            source.name,
            vertices,
            polygons=[FakePolygon()],
            uv_layers=list(source.uv_layers),
            materials=list(source.materials),
            vertex_groups=(
                [FakeVertexGroup(group.name, group.index) for group in source.vertex_groups]
                if preserve_all_data_layers
                else []
            ),
        )
        if self.attach_stray_shape_keys:
            evaluated.stray_shape_keys = FakeShapeKeys(
                [FakeKeyBlock("Stray", [0.0] * len(coords))], owner_mesh=evaluated
            )
        return evaluated


class FakeObject:
    def __init__(self, name, mesh, engine, type_="MESH", mode="OBJECT") -> None:
        self.name = name
        self.type = type_
        self.mode = mode
        self._engine = engine
        self._data = None
        self.active_shape_key_index = 0
        self.modifiers = FakeModifiers()
        self.data = mesh

    @property
    def data(self):
        return self._data

    @data.setter
    def data(self, mesh) -> None:
        if mesh is self._data:
            return
        if self._data is not None:
            self._data.users -= 1
        mesh.users += 1
        self._data = mesh

    @property
    def vertex_groups(self):
        return self._data.vertex_groups

    def evaluated_get(self, depsgraph):
        return FakeEvaluatedObject(self._engine, self)

    def shape_key_add(self, name, from_mix=False):
        mesh = self._data
        if mesh.shape_keys is None:
            mesh.shape_keys = FakeShapeKeys([])
        coords = []
        for vertex in mesh.vertices:
            coords.extend(vertex.co)
        block = FakeKeyBlock(name, coords)
        mesh.shape_keys.key_blocks.append(block)
        return block


MODIFIER_DISPLAY_NAMES = {"ARMATURE": "Armature", "SUBSURF": "Subdivision", "MASK": "Mask"}


def make_mesh_object(
    engine,
    *,
    name="Female.body",
    mesh_name="base",
    basis=None,
    key_deltas=None,
    key_values=None,
    key_mutes=None,
    slider_bounds=None,
    use_relative=True,
    mode="OBJECT",
    type_="MESH",
    stack=("ARMATURE", "SUBSURF", "MASK", "MASK"),
    subsurf_levels=(0, 1),
    modifier_visibility=None,
    group_names=(),
    weights_by_vertex=None,
):
    """Build one mesh object with keys, modifier stack and group weights."""

    basis = basis if basis is not None else DEFAULT_BASIS
    key_deltas = key_deltas if key_deltas is not None else DEFAULT_KEY_DELTAS
    weights_by_vertex = weights_by_vertex if weights_by_vertex is not None else [{} for _ in basis]
    groups = [FakeVertexGroup(group_name, index) for index, group_name in enumerate(group_names)]
    vertices = []
    for index, co in enumerate(basis):
        assignments = [
            FakeVertexGroupAssignment(group_names.index(group_name), weight)
            for group_name, weight in weights_by_vertex[index].items()
        ]
        vertices.append(FakeVertex(co, assignments))
    mesh = FakeMesh(mesh_name, vertices, vertex_groups=groups)

    blocks = [FakeKeyBlock("Basis", flattened(basis), value=1.0)]
    for key_name, deltas in key_deltas.items():
        bounds = (slider_bounds or {}).get(key_name, (0.0, 1.0))
        blocks.append(
            FakeKeyBlock(
                key_name,
                flattened(basis),
                evaluation_delta=deltas,
                value=(key_values or {}).get(key_name, 0.0),
                mute=(key_mutes or {}).get(key_name, False),
                slider_min=bounds[0],
                slider_max=bounds[1],
            )
        )
    if blocks:
        mesh.shape_keys = FakeShapeKeys(blocks, use_relative=use_relative)

    counters: dict[str, int] = {}
    modifiers = []
    for modifier_type in stack:
        counters[modifier_type] = counters.get(modifier_type, 0) + 1
        occurrence = counters[modifier_type]
        display_name = MODIFIER_DISPLAY_NAMES.get(modifier_type, modifier_type)
        modifier_name = (
            display_name if occurrence == 1 else f"{display_name}.{occurrence - 1:03d}"
        )
        viewport, render = (modifier_visibility or {}).get(modifier_type, (True, True))
        if modifier_type == "SUBSURF":
            modifiers.append(
                FakeModifier(
                    "SUBSURF",
                    modifier_name,
                    show_viewport=viewport,
                    show_render=render,
                    levels=subsurf_levels[0],
                    render_levels=subsurf_levels[1],
                )
            )
        else:
            modifiers.append(
                FakeModifier(modifier_type, modifier_name, show_viewport=viewport, show_render=render)
            )

    obj = FakeObject(name, mesh, engine, type_=type_, mode=mode)
    obj.modifiers = FakeModifiers(modifiers)
    obj.active_shape_key_index = 1 if key_deltas else 0
    return obj


def run_bake(objects, engine):
    """Invoke the real baker with the engine's fake bpy installed."""

    messages: list[str] = []
    with installed_fake_bpy(engine.make_bpy_module()):
        baked = BAKE.bake_subdivision_modifiers(objects, log=messages.append)
    return baked, messages


class ModuleImportDisciplineTests(unittest.TestCase):
    def test_module_imports_with_no_bpy_present(self) -> None:
        previous = sys.modules.pop("bpy", None)
        try:
            module = load_bake_module()
        finally:
            if previous is not None:
                sys.modules["bpy"] = previous
        self.assertFalse(hasattr(module, "bpy"))
        self.assertTrue(callable(module.bake_subdivision_modifiers))
        self.assertTrue(issubclass(module.BakeError, RuntimeError))

    def test_module_runs_against_a_stubbed_bpy(self) -> None:
        engine = FakeEngine()
        load_bake_module(fake_bpy=engine.make_bpy_module())
        baked, _messages = run_bake((), engine)
        self.assertEqual([], baked)


class PureDeltaHelperTests(unittest.TestCase):
    def test_compute_key_deltas_subtracts_basis_per_component(self) -> None:
        basis = [1.0, 2.0, 3.0, -1.0, 0.0, 0.5]
        key = [1.25, 1.5, 3.5, -1.0, -0.25, 0.5]
        self.assertEqual(
            [0.25, -0.5, 0.5, 0.0, -0.25, 0.0], BAKE.compute_key_deltas(basis, key)
        )

    def test_compute_key_deltas_round_trips_through_reconstruction(self) -> None:
        basis = [0.0, 0.25, -0.5, 1.0]
        key = [0.75, 0.25, -1.0, 1.25]
        deltas = BAKE.compute_key_deltas(basis, key)
        self.assertEqual(key, BAKE.reconstructed_key_coordinates(basis, deltas))

    def test_reconstructed_key_coordinates_zero_deltas_return_basis(self) -> None:
        basis = [3.0, -2.0, 0.125]
        self.assertEqual(
            basis, BAKE.reconstructed_key_coordinates(basis, [0.0, 0.0, 0.0])
        )

    def test_compute_key_deltas_length_mismatch_raises(self) -> None:
        with self.assertRaisesRegex(BAKE.BakeError, "topology changed between evaluations"):
            BAKE.compute_key_deltas([0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 0.0])

    def test_reconstructed_key_coordinates_length_mismatch_raises(self) -> None:
        with self.assertRaisesRegex(BAKE.BakeError, "delta vertices"):
            BAKE.reconstructed_key_coordinates([0.0, 0.0, 0.0], [0.0])


class ModifierSelectionTests(unittest.TestCase):
    def test_only_render_subsurf_consumed_and_armature_mask_left(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine)
        armature = next(m for m in obj.modifiers if m.type == "ARMATURE")
        masks = [m for m in obj.modifiers if m.type == "MASK"]
        baked, _messages = run_bake([obj], engine)

        self.assertEqual(["Female.body"], baked)
        self.assertEqual(["ARMATURE", "MASK", "MASK"], [m.type for m in obj.modifiers])
        self.assertIn(armature, list(obj.modifiers))
        self.assertIn(masks[0], list(obj.modifiers))
        self.assertIn(masks[1], list(obj.modifiers))

    def test_non_subsurf_visibility_restored_after_bake(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(
            engine,
            modifier_visibility={"ARMATURE": (True, True), "MASK": (False, True)},
        )
        masks = [m for m in obj.modifiers if m.type == "MASK"]
        run_bake([obj], engine)
        armature = next(m for m in obj.modifiers if m.type == "ARMATURE")
        self.assertEqual((True, True), (armature.show_viewport, armature.show_render))
        for mask in masks:
            self.assertEqual((False, True), (mask.show_viewport, mask.show_render))

    def test_levels_set_to_render_levels_during_evaluation(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, subsurf_levels=(0, 3))
        run_bake([obj], engine)
        self.assertTrue(engine.evaluations)
        for evaluation in engine.evaluations:
            subsurf = next(state for state in evaluation.modifier_states if state[1] == "SUBSURF")
            self.assertEqual(("Subdivision", "SUBSURF", True, True, 3), subsurf)

    def test_non_subsurf_modifiers_disabled_during_every_evaluation(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine)
        run_bake([obj], engine)
        self.assertEqual(3, len(engine.evaluations))
        for evaluation in engine.evaluations:
            for name, type_, viewport, render, _levels in evaluation.modifier_states:
                if type_ == "SUBSURF":
                    continue
                self.assertFalse(viewport, f"{name} viewport-enabled during evaluation")
                self.assertFalse(render, f"{name} render-enabled during evaluation")

    def test_object_without_subsurf_is_skipped_untouched(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, stack=("ARMATURE",), key_deltas={})
        mesh = obj.data
        baked, messages = run_bake([obj], engine)
        self.assertEqual([], baked)
        self.assertIs(mesh, obj.data)
        self.assertEqual(["ARMATURE"], [m.type for m in obj.modifiers])
        self.assertEqual([], engine.evaluations)
        self.assertTrue(
            any("no SUBSURF modifier with render subdivision" in message for message in messages)
        )

    def test_zero_render_levels_subsurf_is_skipped(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, subsurf_levels=(0, 0), key_deltas={})
        mesh = obj.data
        baked, messages = run_bake([obj], engine)
        self.assertEqual([], baked)
        self.assertIs(mesh, obj.data)
        self.assertEqual(
            ["ARMATURE", "SUBSURF", "MASK", "MASK"], [m.type for m in obj.modifiers]
        )
        self.assertEqual([], engine.evaluations)
        self.assertTrue(
            any("no SUBSURF modifier with render subdivision" in message for message in messages)
        )

    def test_non_mesh_objects_are_ignored_without_skip_logging(self) -> None:
        engine = FakeEngine()
        mesh_object = make_mesh_object(engine, key_deltas={})
        armature_object = make_mesh_object(engine, name="Rig", type_="ARMATURE", key_deltas={})
        baked, messages = run_bake([armature_object, mesh_object], engine)
        self.assertEqual(["Female.body"], baked)
        self.assertFalse(any("Rig" in message for message in messages))

    def test_only_positive_render_subsurf_consumed_among_multiple(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(
            engine,
            stack=("SUBSURF", "ARMATURE", "SUBSURF"),
            subsurf_levels=(0, 2),
            key_deltas={},
        )
        zero_render = [m for m in obj.modifiers if m.type == "SUBSURF"][1]
        zero_render.render_levels = 0
        baked, _messages = run_bake([obj], engine)
        self.assertEqual(["Female.body"], baked)
        self.assertEqual(["ARMATURE", "SUBSURF"], [m.type for m in obj.modifiers])
        self.assertIn(zero_render, list(obj.modifiers))
        self.assertEqual((True, True), (zero_render.show_viewport, zero_render.show_render))


class ShapeKeyReconstructionTests(unittest.TestCase):
    def test_keyed_mesh_reconstruction_end_to_end(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, key_deltas=DEFAULT_KEY_DELTAS, key_values={"KeyA": 0.25})
        obj.active_shape_key_index = 2
        old_mesh = obj.data
        old_basis = old_mesh.shape_keys.key_blocks[0]

        baked, _messages = run_bake([obj], engine)

        self.assertEqual(["Female.body"], baked)
        self.assertIsNot(old_mesh, obj.data)
        self.assertEqual("base", obj.data.name)
        self.assertEqual([old_mesh], engine.removed_meshes)
        self.assertEqual(1.0, old_basis.value)

        basis_flat = flattened(DEFAULT_BASIS)
        expected_basis = subdivided(basis_flat)
        new_coords = [0.0] * (3 * len(obj.data.vertices))
        obj.data.vertices.foreach_get("co", new_coords)
        self.assertEqual(expected_basis, new_coords)

        blocks = obj.data.shape_keys.key_blocks
        self.assertEqual(["Basis", "KeyA", "KeyB"], [block.name for block in blocks])
        self.assertEqual(expected_basis, blocks[0].data.coords)
        expected_key_a = subdivided(
            [c + d for c, d in zip(basis_flat, flattened(DEFAULT_KEY_DELTAS["KeyA"]))]
        )
        expected_key_b = subdivided(
            [c + d for c, d in zip(basis_flat, flattened(DEFAULT_KEY_DELTAS["KeyB"]))]
        )
        self.assertEqual(expected_key_a, blocks[1].data.coords)
        self.assertEqual(expected_key_b, blocks[2].data.coords)
        self.assertIs(blocks[0], blocks[1].relative_key)
        self.assertIs(blocks[0], blocks[2].relative_key)

        self.assertEqual(1.0, blocks[0].value)
        self.assertEqual(0.25, blocks[1].value)
        self.assertEqual(0.0, blocks[2].value)
        self.assertEqual(2, obj.active_shape_key_index)

        # One basis evaluation plus one per non-basis key, all preserving data
        # layers, and every evaluated mesh released again.
        self.assertEqual(3, len(engine.evaluations))
        self.assertTrue(all(flag is True for flag in engine.to_mesh_flags))
        self.assertEqual(engine.to_mesh_calls, engine.to_mesh_clear_calls)
        self.assertEqual(
            {"Basis": 0.0, "KeyA": 0.0, "KeyB": 0.0}, engine.evaluations[0].key_values
        )
        self.assertEqual(
            {"Basis": 0.0, "KeyA": 1.0, "KeyB": 0.0}, engine.evaluations[1].key_values
        )
        self.assertEqual(
            {"Basis": 0.0, "KeyA": 0.0, "KeyB": 1.0}, engine.evaluations[2].key_values
        )

    def test_keyed_mesh_slider_bounds_and_mutes_restored(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(
            engine,
            key_deltas={"Wide": [(0.5, 0.5, 0.5)] * 4, "Quiet": [(0.0, 0.0, 0.0)] * 4},
            key_values={"Wide": 1.5, "Quiet": 0.0},
            key_mutes={"Quiet": True},
            slider_bounds={"Wide": (-0.5, 2.0)},
        )
        run_bake([obj], engine)
        blocks = {block.name: block for block in obj.data.shape_keys.key_blocks}
        self.assertEqual((-0.5, 2.0), (blocks["Wide"].slider_min, blocks["Wide"].slider_max))
        self.assertEqual(1.5, blocks["Wide"].value)
        self.assertTrue(blocks["Quiet"].mute)
        self.assertFalse(blocks["Wide"].mute)

    def test_basis_only_mesh_takes_keyed_route(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, key_deltas={})
        baked, _messages = run_bake([obj], engine)
        self.assertEqual(["Female.body"], baked)
        self.assertEqual(1, len(engine.evaluations))
        self.assertEqual(["Basis"], [block.name for block in obj.data.shape_keys.key_blocks])
        self.assertEqual(
            subdivided(flattened(DEFAULT_BASIS)),
            obj.data.shape_keys.key_blocks[0].data.coords,
        )

    def test_keyless_mesh_takes_direct_route(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, key_deltas={})
        obj.data.shape_keys = None
        baked, _messages = run_bake([obj], engine)
        self.assertEqual(["Female.body"], baked)
        self.assertEqual(1, len(engine.evaluations))
        self.assertIsNone(obj.data.shape_keys)

    def test_evaluation_mutes_are_cleared_for_key_capture(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(
            engine,
            key_deltas={"KeyA": DEFAULT_KEY_DELTAS["KeyA"]},
            key_mutes={"KeyA": True},
        )
        run_bake([obj], engine)
        # A muted key left muted would evaluate as the basis and reconstruct as
        # an all-zero delta; the cleared mute must let the displacement through.
        expected = subdivided(
            [
                c + d
                for c, d in zip(flattened(DEFAULT_BASIS), flattened(DEFAULT_KEY_DELTAS["KeyA"]))
            ]
        )
        self.assertEqual(expected, obj.data.shape_keys.key_blocks[1].data.coords)


class GuardBehaviourTests(unittest.TestCase):
    def test_vertex_count_mismatch_raises_and_restores_object_state(self) -> None:
        engine = FakeEngine()
        engine.mismatch_at_evaluation = 2
        obj = make_mesh_object(engine, key_deltas=DEFAULT_KEY_DELTAS)
        obj.data.shape_keys.key_blocks[1].value = 0.75
        old_mesh = obj.data
        with installed_fake_bpy(engine.make_bpy_module()):
            with self.assertRaisesRegex(BAKE.BakeError, "topology changed between evaluations"):
                BAKE.bake_subdivision_modifiers([obj])

        self.assertIs(old_mesh, obj.data)
        self.assertEqual(
            ["ARMATURE", "SUBSURF", "MASK", "MASK"], [m.type for m in obj.modifiers]
        )
        self.assertTrue(all(m.show_viewport for m in obj.modifiers))
        self.assertEqual(0.75, obj.data.shape_keys.key_blocks[1].value)
        self.assertEqual([], engine.removed_meshes)

    def test_unsubdivided_evaluation_raises_and_restores_object_state(self) -> None:
        engine = FakeEngine()
        engine.subdiv_factor = 1
        obj = make_mesh_object(engine, key_deltas={})
        old_mesh = obj.data
        with installed_fake_bpy(engine.make_bpy_module()):
            with self.assertRaisesRegex(BAKE.BakeError, "were not applied"):
                BAKE.bake_subdivision_modifiers([obj])
        self.assertIs(old_mesh, obj.data)
        self.assertIn("SUBSURF", [m.type for m in obj.modifiers])
        self.assertTrue(all(m.show_viewport for m in obj.modifiers))
        self.assertEqual(1, len(engine.evaluations))

    def test_absolute_shape_keys_are_rejected_before_any_evaluation(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(
            engine, key_deltas={"KeyA": DEFAULT_KEY_DELTAS["KeyA"]}, use_relative=False
        )
        with installed_fake_bpy(engine.make_bpy_module()):
            with self.assertRaisesRegex(BAKE.BakeError, "absolute shape keys"):
                BAKE.bake_subdivision_modifiers([obj])
        self.assertEqual([], engine.evaluations)
        self.assertEqual(
            ["ARMATURE", "SUBSURF", "MASK", "MASK"], [m.type for m in obj.modifiers]
        )

    def test_edit_mode_object_is_rejected(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(engine, key_deltas={}, mode="EDIT")
        with installed_fake_bpy(engine.make_bpy_module()):
            with self.assertRaisesRegex(BAKE.BakeError, "EDIT"):
                BAKE.bake_subdivision_modifiers([obj])
        self.assertEqual([], engine.evaluations)


class DataSurvivalTests(unittest.TestCase):
    def test_vertex_groups_and_layers_survive_keyed_bake(self) -> None:
        engine = FakeEngine()
        obj = make_mesh_object(
            engine,
            key_deltas={"KeyA": DEFAULT_KEY_DELTAS["KeyA"]},
            group_names=("head", "body", "thigh_r"),
            weights_by_vertex=[
                {"head": 1.0, "body": 1.0},
                {"head": 1.0},
                {"body": 0.25, "thigh_r": 0.75},
                {},
            ],
        )
        run_bake([obj], engine)
        self.assertEqual(
            ["head", "body", "thigh_r"], [group.name for group in obj.data.vertex_groups]
        )
        # Original vertices keep their indices and exact weights; appended
        # subdivision vertices carry no assignments of their own.
        self.assertEqual(
            [(0, 1.0), (1, 1.0)],
            [(a.group, a.weight) for a in obj.data.vertices[0].groups],
        )
        self.assertEqual([(0, 1.0)], [(a.group, a.weight) for a in obj.data.vertices[1].groups])
        self.assertEqual(
            [(1, 0.25), (2, 0.75)],
            [(a.group, a.weight) for a in obj.data.vertices[2].groups],
        )
        self.assertEqual([], list(obj.data.vertices[3].groups))
        self.assertEqual([], list(obj.data.vertices[7].groups))
        self.assertEqual(1, len(obj.data.uv_layers))
        self.assertEqual(1, len(obj.data.materials))

    def test_multi_user_mesh_data_is_kept(self) -> None:
        engine = FakeEngine()
        shared_obj = make_mesh_object(engine, key_deltas={})
        old_mesh = shared_obj.data
        other = FakeObject("Female.clothes", old_mesh, engine)
        self.assertEqual(2, old_mesh.users)
        baked, messages = run_bake([shared_obj], engine)
        self.assertEqual(["Female.body"], baked)
        self.assertEqual([], engine.removed_meshes)
        self.assertIs(old_mesh, other.data)
        self.assertIsNot(old_mesh, shared_obj.data)
        self.assertEqual("base.001", shared_obj.data.name)
        self.assertTrue(
            any("Kept the unsubdivided mesh data" in message for message in messages)
        )

    def test_stray_shape_keys_on_evaluated_data_are_discarded(self) -> None:
        engine = FakeEngine()
        engine.attach_stray_shape_keys = True
        obj = make_mesh_object(engine, key_deltas={"KeyA": DEFAULT_KEY_DELTAS["KeyA"]})
        baked, messages = run_bake([obj], engine)
        self.assertEqual(["Female.body"], baked)
        self.assertEqual(1, len(engine.batch_removed))
        self.assertEqual("Stray", engine.batch_removed[0].key_blocks[0].name)
        self.assertTrue(any("stray shape key container" in message for message in messages))
        self.assertEqual(
            ["Basis", "KeyA"], [block.name for block in obj.data.shape_keys.key_blocks]
        )


if __name__ == "__main__":
    unittest.main()
