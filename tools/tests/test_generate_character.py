from __future__ import annotations

import importlib.util
import json
import sys
import types
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory


REPO_ROOT = Path(__file__).resolve().parents[2]
GENERATE_CHARACTER_PATH = REPO_ROOT / "tools" / "generate_character.py"


class FakeLibraryLoad:
    def __init__(self, object_names: list[str], action_names: list[str]) -> None:
        self.object_names = object_names
        self.action_names = action_names
        self.data_to = types.SimpleNamespace(actions=[])

    def __enter__(self):
        return (
            types.SimpleNamespace(objects=self.object_names, actions=self.action_names),
            self.data_to,
        )

    def __exit__(self, exc_type, exc_value, traceback) -> bool:
        return False


class FakeLibraries:
    def __init__(self, object_names: list[str], action_names: list[str]) -> None:
        self.object_names = object_names
        self.action_names = action_names
        self.load_calls: list[tuple[str, bool, bool]] = []
        self.load_contexts: list[FakeLibraryLoad] = []

    def load(self, path: str, *, link: bool, relative: bool) -> FakeLibraryLoad:
        self.load_calls.append((path, link, relative))
        context = FakeLibraryLoad(self.object_names, self.action_names)
        self.load_contexts.append(context)
        return context


class FakeCoordinate:
    def __init__(self, y: float) -> None:
        self.y = y


class FakeKeyframePoint:
    def __init__(self, frame: float, value: float) -> None:
        self.co = types.SimpleNamespace(x=frame, y=value)


class FakeFCurve:
    def __init__(self, values: list[float]) -> None:
        self.keyframe_points = [
            FakeKeyframePoint(float(index + 1), value) for index, value in enumerate(values)
        ]


class FakeAction(dict):
    def __init__(self, name: str, frame_range: tuple[float, float], values: list[float]) -> None:
        super().__init__()
        self.name = name
        self.frame_range = frame_range
        self.fcurves = [FakeFCurve(values)]
        self.library = None
        self.use_fake_user = True
        self.users = 1


class FakeActions:
    def __init__(self, actions: list[FakeAction]) -> None:
        self._actions = {action.name: action for action in actions}

    def get(self, name: str):
        return self._actions.get(name)

    def __iter__(self):
        return iter(self._actions.values())


def import_generate_character_with_fake_bpy(object_names: list[str], action_names: list[str] | None = None):
    fake_libraries = FakeLibraries(object_names, action_names or [])
    fake_bpy = types.ModuleType("bpy")
    setattr(fake_bpy, "data", types.SimpleNamespace(libraries=fake_libraries, actions=FakeActions([])))
    setattr(fake_bpy, "types", types.SimpleNamespace(Action=FakeAction))
    fake_generate_body_colliders = types.ModuleType("generate_body_colliders")

    previous_bpy = sys.modules.get("bpy")
    previous_generate_body_colliders = sys.modules.get("generate_body_colliders")
    sys.modules["bpy"] = fake_bpy
    sys.modules["generate_body_colliders"] = fake_generate_body_colliders

    module_name = "generate_character_under_test"
    sys.modules.pop(module_name, None)
    spec = importlib.util.spec_from_file_location(module_name, GENERATE_CHARACTER_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load module spec for {GENERATE_CHARACTER_PATH}")

    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    try:
        spec.loader.exec_module(module)
    finally:
        if previous_bpy is None:
            sys.modules.pop("bpy", None)
        else:
            sys.modules["bpy"] = previous_bpy

        if previous_generate_body_colliders is None:
            sys.modules.pop("generate_body_colliders", None)
        else:
            sys.modules["generate_body_colliders"] = previous_generate_body_colliders

    return module, fake_libraries


def make_physical_twist_fixture(*, include_ghost=True):
    """Two independently stored meshes with live fake Blender group assignments."""
    class Vector:
        def __init__(self, x):
            self.x = x

        def __sub__(self, other):
            return Vector(self.x - other.x)

        def __mul__(self, scalar):
            return Vector(self.x * scalar)

        def dot(self, other):
            return self.x * other.x

        @property
        def length_squared(self):
            return self.x * self.x

        @property
        def length(self):
            return abs(self.x)

    class Identity:
        def __matmul__(self, point):
            return point

    bones = {}
    for side, origin in (("l", 0), ("r", 2)):
        lower = types.SimpleNamespace(name=f"lowerarm_{side}", use_deform=True,
            parent=None, parent_recursive=[], head_local=Vector(origin), tail_local=Vector(origin + 1))
        hand = types.SimpleNamespace(name=f"hand_{side}", use_deform=True,
            parent=lower, parent_recursive=[lower])
        finger = types.SimpleNamespace(name=f"finger_{side}", use_deform=True,
            parent=hand, parent_recursive=[hand, lower])
        helper = types.SimpleNamespace(name=f"{'Left' if side == 'l' else 'Right'}ForearmTwist",
            use_deform=True, parent=lower, parent_recursive=[lower])
        bones.update({bone.name: bone for bone in (lower, hand, finger, helper)})
    bone_collection = type("Bones", (dict,), {"__iter__": lambda self: iter(self.values())})(bones)
    armature = types.SimpleNamespace(data=types.SimpleNamespace(bones=bone_collection), matrix_world=Identity())
    rows = [
        ({"lowerarm_l": 0.8, "shirt": 0.2, "mask": 0.0}, 0.8),
        ({"lowerarm_r": 0.8, "shirt": 0.2, "mask": 0.0}, 2.8),
        ({"hand_r": 0.9, "shirt": 0.1, "finger_l": 0.0}, 2.85),
        ({"lowerarm_l": 0.4, "lowerarm_r": 0.6, "finger_l": 0.0}, 0.8),
        ({"lowerarm_l": 0.8, "shirt": 0.2, "mask": 0.0}, 1.8),
    ]
    if not include_ghost:
        rows[2][0].pop("finger_l")
    meshes = []
    for mesh_name in ("body", "coat"):
        memberships = [dict(row) for row, _ in rows]

        class Group:
            def __init__(self, name, index, membership_rows):
                self.name, self.index = name, index
                self.memberships = membership_rows

            def add(self, indices, weight, mode):
                assert mode == "REPLACE"
                for index in indices:
                    self.memberships[index][self.name] = weight

            def remove(self, indices):
                for index in indices:
                    del self.memberships[index][self.name]

        class Groups(dict):
            def __init__(self, membership_rows):
                super().__init__()
                self.memberships = membership_rows

            def __iter__(self):
                return iter(self.values())

            def new(self, *, name):
                self[name] = Group(name, len(self), self.memberships)
                return self[name]

        groups = Groups(memberships)
        for name in sorted({name for row, _ in rows for name in row}):
            groups.new(name=name)

        class Vertex:
            def __init__(self, index, position):
                self.index, self.co = index, Vector(position)
                self.memberships, self.group_collection = memberships, groups

            @property
            def groups(self):
                return [types.SimpleNamespace(group=self.group_collection[name].index, weight=weight)
                        for name, weight in self.memberships[self.index].items()]

        mesh = type("Mesh", (), {})()
        mesh.name, mesh.type, mesh.matrix_world = mesh_name, "MESH", Identity()
        mesh.vertex_groups = groups
        mesh.modifiers = [types.SimpleNamespace(type="ARMATURE", object=armature)]
        mesh.data = types.SimpleNamespace(vertices=[Vertex(index, position) for index, (_, position) in enumerate(rows)],
            polygons=[types.SimpleNamespace(vertices=(0, 1, 2, 3, 4))])
        mesh.as_pointer = lambda mesh=mesh: id(mesh)
        mesh.data.as_pointer = lambda data=mesh.data: id(data)
        mesh.physical_memberships = memberships
        meshes.append(mesh)
    return armature, meshes, [row for row, _ in rows]


class GenerateCharacterRigifyDetectionTests(unittest.TestCase):
    def test_excluded_export_mesh_carries_frozen_physical_rows_through_rename(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        _, meshes, original = make_physical_twist_fixture()
        module.bpy.data.objects = list(meshes)
        frozen = module.snapshot_export_physical_memberships(set(meshes))
        for mesh in meshes:
            mesh.name = f"reference_{mesh.name}"
        physical = module.verify_export_physical_memberships(set(meshes), frozen)
        records = module.capture_skipped_export_physical_meshes(
            set(meshes), {meshes[0].name}, physical, frozen
        )
        self.assertEqual([meshes[1].name], [record["name"] for record in records])
        self.assertEqual(original, [row["weights"] for row in records[0]["original_physical_rows"]])
        with self.assertRaisesRegex(module.ScriptError, "partition"):
            module.capture_skipped_export_physical_meshes(set(meshes), {"missing"}, physical, frozen)

    def test_export_physical_snapshot_survives_renaming_on_bilateral_meshes(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        _, meshes, original = make_physical_twist_fixture()
        module.bpy.data.objects = list(meshes)
        frozen = module.snapshot_export_physical_memberships(set(meshes))
        for mesh in meshes:
            mesh.name = f"reference_{mesh.name}"
        final = module.verify_export_physical_memberships(set(meshes), frozen)
        self.assertEqual({"reference_body", "reference_coat"}, set(final))
        for rows in final.values():
            self.assertEqual(original, rows)
            self.assertEqual(0.0, rows[2]["finger_l"])
            self.assertGreater(rows[3]["lowerarm_l"], 0.0)
            self.assertGreater(rows[3]["lowerarm_r"], 0.0)

    def test_export_physical_snapshot_rejects_mutation_before_preparation(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        _, meshes, _ = make_physical_twist_fixture()
        module.bpy.data.objects = list(meshes)
        frozen = module.snapshot_export_physical_memberships(set(meshes))
        meshes[0].physical_memberships[2].pop("finger_l")
        with self.assertRaisesRegex(module.ScriptError, "changed before authoring"):
            module.verify_export_physical_memberships(set(meshes), frozen)
        self.assertEqual(0.0, frozen[meshes[0].as_pointer()][2][2]["finger_l"])

    def test_export_physical_snapshot_rejects_group_and_mesh_identity_changes(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        _, meshes, _ = make_physical_twist_fixture()
        module.bpy.data.objects = list(meshes)
        frozen = module.snapshot_export_physical_memberships(set(meshes))
        meshes[0].vertex_groups["mask"].name = "renamed_mask"
        with self.assertRaisesRegex(module.ScriptError, "changed before authoring"):
            module.verify_export_physical_memberships(set(meshes), frozen)
        meshes[0].vertex_groups["mask"].name = "mask"
        with self.assertRaisesRegex(module.ScriptError, "mesh identity"):
            module.verify_export_physical_memberships({meshes[0]}, frozen)
        module.bpy.data.objects.remove(meshes[1])
        with self.assertRaisesRegex(module.ScriptError, "dropped export object"):
            module.verify_export_physical_memberships(set(meshes), frozen)
        module.bpy.data.objects.append(meshes[1])
        _, extra, _ = make_physical_twist_fixture()
        module.bpy.data.objects.append(extra[0])
        with self.assertRaisesRegex(module.ScriptError, "newly introduced mesh"):
            module.verify_export_physical_memberships(set(meshes), frozen, set())
        module.bpy.data.objects.remove(extra[0])
        meshes[0].data.vertices.pop()
        with self.assertRaisesRegex(module.ScriptError, "changed before authoring"):
            module.verify_export_physical_memberships(set(meshes), frozen)

    def test_export_physical_snapshot_rejects_ambiguous_group_indices(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        _, meshes, _ = make_physical_twist_fixture()
        meshes[0].vertex_groups["mask"].index = meshes[0].vertex_groups["shirt"].index
        with self.assertRaisesRegex(module.ScriptError, "ambiguous group index or name"):
            module.snapshot_export_physical_memberships(set(meshes))

    def test_actual_writer_authors_absent_helpers_on_both_sides_and_meshes(self) -> None:
        self.assert_physical_pipeline(include_ghost=False)

    def test_actual_writer_preserves_unattributed_opposite_hand_zero_finger(self) -> None:
        self.assert_physical_pipeline(include_ghost=True)

    def assert_physical_pipeline(self, *, include_ghost: bool) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        for reverse in (False, True):
            with self.subTest(reversed_sides=reverse):
                armature, meshes, original = make_physical_twist_fixture(include_ghost=include_ghost)
                self.assertTrue(all("LeftForearmTwist" not in row and "RightForearmTwist" not in row
                                    for row in original))
                if reverse:
                    module.FOREARM_TWIST_WEIGHT_GROUPS = tuple(reversed(module.FOREARM_TWIST_WEIGHT_GROUPS))
                physical = {}
                source = module.capture_forearm_twist_axial_input(set(meshes), armature, physical)
                eligible = {}
                axial = module.apply_forearm_twist_gradient_on_export(
                    set(meshes), armature, source, eligible, physical)
                for mesh in meshes:
                    self.assertEqual(original, physical[mesh.name])
                    output = mesh.physical_memberships
                    for index, side in ((0, "Left"), (1, "Right")):
                        helper = f"{side}ForearmTwist"
                        lower = "lowerarm_l" if side == "Left" else "lowerarm_r"
                        self.assertNotIn(helper, original[index])
                        self.assertGreater(output[index][helper], 0.0)
                        self.assertAlmostEqual(0.8, sum(output[index][name] for name in (lower, helper)))
                        self.assertAlmostEqual(0.8, axial[mesh.name][index].get(lower, 0.0)
                            + axial[mesh.name][index].get(helper, 0.0))
                        self.assertEqual(0.0, output[index]["mask"])
                        self.assertEqual(0.2, output[index]["shirt"])
                        self.assertEqual(set(original[index]) | {helper}, set(output[index]))
                        self.assertFalse(any(name.startswith("Right" if side == "Left" else "Left") for name in output[index]))
                    if include_ghost:
                        self.assertEqual(0.0, output[2]["finger_l"])
                    self.assertEqual(0.1, output[2]["shirt"])
                    self.assertEqual(set(original[2]) | {"RightForearmTwist"}, set(output[2]))
                    self.assertAlmostEqual(0.9, sum(output[2].get(name, 0.0) for name in
                        ("hand_r", "RightForearmTwist", "lowerarm_r")))
                    self.assertEqual(original[3], output[3], "protected bilateral row must be physically identical")
                    self.assertEqual(original[4], output[4], "out-of-domain row must be physically identical")
                    for index in range(len(original)):
                        self.assertFalse(any(name not in original[index] and weight == 0.0
                                             for name, weight in output[index].items()), "logical zero must not create membership")
                    self.assertEqual([0, 1, 2], sorted(set(eligible[mesh.name]["LeftForearmTwist"]
                        + eligible[mesh.name]["RightForearmTwist"])))

    def test_axial_generator_authors_zero_helper_on_each_mesh(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])

        class V:
            def __init__(self, x, y=0.0):
                self.x, self.y = x, y

            def __sub__(self, other):
                return V(self.x - other.x, self.y - other.y)

            def __mul__(self, scalar):
                return V(self.x * scalar, self.y * scalar)

            def dot(self, other):
                return self.x * other.x + self.y * other.y

            @property
            def length_squared(self):
                return self.dot(self)

            @property
            def length(self):
                return self.length_squared ** 0.5

        class Identity:
            def __matmul__(self, point):
                return point

        class Groups(dict):
            def __iter__(self):
                return iter(self.values())

            def new(self, *, name):
                group = types.SimpleNamespace(name=name, index=len(self))
                self[name] = group
                return group

        names = ("lowerarm_l", "hand_l", "lowerarm_r", "hand_r", "middle_01_l", "shirt")
        rows = [
            {"lowerarm_l": .8, "shirt": .2},
            {"hand_l": .8, "shirt": .2},
            {"hand_r": .8, "shirt": .2},
            {"lowerarm_l": .4, "lowerarm_r": .6},
            {"hand_l": .5, "middle_01_l": .5},
            {"lowerarm_l": .8, "shirt": .2},
        ]
        positions = [V(.8), V(.85), V(2.8), V(.8), V(.8), V(1.8)]
        bones = {name: types.SimpleNamespace(name=name, use_deform=True, parent=None)
                 for name in names[:4]}
        for side in ("l", "r"):
            bones[f"lowerarm_{side}"].head_local = V(0 if side == "l" else 2)
            bones[f"lowerarm_{side}"].tail_local = V(1 if side == "l" else 3)
            bones[f"hand_{side}"].parent = bones[f"lowerarm_{side}"]
        bones["middle_01_l"] = types.SimpleNamespace(name="middle_01_l", use_deform=True, parent=bones["hand_l"])
        for side in ("Left", "Right"):
            lower = bones[f"lowerarm_{side[0].lower()}"]
            helper = types.SimpleNamespace(name=f"{side}ForearmTwist", use_deform=True, parent=lower)
            bones[helper.name] = helper
        for bone in bones.values():
            ancestors = []
            parent = bone.parent
            while parent is not None:
                ancestors.append(parent)
                parent = parent.parent
            bone.parent_recursive = ancestors
        armature = types.SimpleNamespace(data=types.SimpleNamespace(bones=bones.values()), matrix_world=Identity())
        # Blender's bone collection supports keyed access as well as iteration.
        armature.data.bones = type("Bones", (dict,), {"__iter__": lambda self: iter(self.values())})(bones)
        snapshots = []
        class Mesh:
            pass

        for mesh_name in ("body", "coat"):
            groups = Groups({name: types.SimpleNamespace(index=i, name=name) for i, name in enumerate(names)})
            for helper in ("LeftForearmTwist", "RightForearmTwist"):
                groups.new(name=helper)
            vertices = [types.SimpleNamespace(index=i, co=pos, groups=[
                types.SimpleNamespace(group=groups[name].index, weight=weight)
                for name, weight in row.items()]) for i, (pos, row) in enumerate(zip(positions, rows, strict=True))]
            for index, helper in ((0, "LeftForearmTwist"), (2, "RightForearmTwist"),
                                  (3, "LeftForearmTwist"), (5, "LeftForearmTwist")):
                vertices[index].groups.append(types.SimpleNamespace(group=groups[helper].index, weight=0.0))
            mesh = Mesh()
            mesh.name, mesh.type, mesh.matrix_world, mesh.vertex_groups = mesh_name, "MESH", Identity(), groups
            mesh.modifiers = [types.SimpleNamespace(type="ARMATURE", object=armature)]
            mesh.data = types.SimpleNamespace(vertices=vertices, polygons=[types.SimpleNamespace(vertices=(0, 1, 3, 4)),
                    types.SimpleNamespace(vertices=(2, 5))])
            snapshots.append(mesh)
        physical = {}
        source = module.capture_forearm_twist_axial_input(set(snapshots), armature, physical)
        captured = {}
        module.write_forearm_twist_vertex_weights = lambda mesh, result, original: captured.update({mesh.name: result})
        authoring_eligible = {}
        source_domain = {}
        authored = module.apply_forearm_twist_gradient_on_export(set(snapshots), armature, source, authoring_eligible, physical, source_domain)
        for mesh in snapshots:
            self.assertEqual(0.0, physical[mesh.name][0]["LeftForearmTwist"])
            self.assertEqual(0.0, physical[mesh.name][3]["LeftForearmTwist"])
            self.assertEqual(0.0, physical[mesh.name][5]["LeftForearmTwist"])
            before, reference, final = source[mesh.name], authored[mesh.name], captured[mesh.name]
            self.assertEqual(rows, before)
            self.assertGreater(reference[0].get("LeftForearmTwist", 0), 0)
            self.assertGreater(final[0].get("LeftForearmTwist", 0), 0,
                "zero-helper source must gain authored helper ownership")
            self.assertGreater(final[2].get("RightForearmTwist", 0), 0)
            self.assertEqual(before[3:5], reference[3:5])
            self.assertEqual(before[3:5], final[3:5])
            self.assertEqual(before[5], final[5])
            for index in (0, 1, 2):
                side = "l" if index < 2 else "r"
                pool = (f"lowerarm_{side}", f"hand_{side}",
                        "LeftForearmTwist" if side == "l" else "RightForearmTwist")
                self.assertAlmostEqual(sum(before[index].get(name, 0) for name in pool),
                    sum(reference[index].get(name, 0) for name in pool))
                self.assertAlmostEqual(sum(reference[index].get(name, 0) for name in pool),
                    sum(final[index].get(name, 0) for name in pool))
            records = module.capture_forearm_twist_generator_run_ownership({mesh}, authored, authoring_eligible, physical, source_domain)
            self.assertEqual(source_domain[mesh.name], records[0]["source_domain"])
            self.assertEqual(reference, [row["weights"] for row in records[0]["axial_rows"]])
            self.assertEqual([0, 2, 3, 5], [row["vertex"] for row in records[0]["original_zero_rows"]])
            self.assertEqual(
                [{"vertex": index, "positives": sorted(name for name, weight in row.items() if weight > 0.0)}
                 for index, row in enumerate(physical[mesh.name]) if any(weight > 0.0 for weight in row.values())],
                records[0]["original_positive_rows"],
            )
            physical_count, _ = module.forearm_twist_generator_run_ownership.compare_physical_rows(
                records[0]["axial_rows"],
                {index: {name: weight for name, weight in row.items() if weight > 0.0}
                 for index, row in enumerate(final)}, records[0]["authoring_eligible"],
                tuple(group[:3] for group in module.FOREARM_TWIST_WEIGHT_GROUPS), 1e-6, mesh.name)
            self.assertEqual(len(rows), physical_count)
            for side, helper, hand in (("l", "LeftForearmTwist", "hand_l"),
                                       ("r", "RightForearmTwist", "hand_r")):
                count, error = module.forearm_twist_generator_run_ownership.compare_axial_rows(
                    records[0]["axial_rows"], dict(enumerate(final)), set(bones),
                    f"lowerarm_{side}", helper, hand, 1e-6, mesh.name)
                self.assertEqual(len(rows), count)
                self.assertLess(error, 1e-6)

        module.FOREARM_TWIST_WEIGHT_GROUPS = tuple(reversed(module.FOREARM_TWIST_WEIGHT_GROUPS))
        reversed_output = {}
        module.write_forearm_twist_vertex_weights = lambda mesh, result, original: reversed_output.update({mesh.name: result})
        reversed_reference = module.apply_forearm_twist_gradient_on_export(set(snapshots), armature, source, physical_memberships=physical)
        self.assertEqual(authored, reversed_reference)
        self.assertEqual(captured, reversed_output)
        module.write_forearm_twist_vertex_weights = lambda mesh, result, original: captured.update({mesh.name: result})
        broken = {name: [dict(row) for row in mesh_rows] for name, mesh_rows in source.items()}
        broken["body"].pop()
        with self.assertRaisesRegex(module.ScriptError, "vertex count changed"):
            module.apply_forearm_twist_gradient_on_export(set(snapshots), armature, broken, physical_memberships=physical)
        # Exported body vertex 750 has five positive deform owners. This synthetic
        # non-forearm row isolates the pre-export limit without needing MPFB startup.
        extra_names = ("clavicle_r", "head", "neck_01", "spine_03", "upperarm_r",
                       "extra_1", "extra_2", "extra_3", "extra_4")
        for name in extra_names:
            armature.data.bones[name] = types.SimpleNamespace(name=name, use_deform=True, parent=None,
                                                               parent_recursive=[])
        five = dict(zip(extra_names[:5], (.005, .4649, .4803, .0488, .001), strict=True))
        eight = {**five, "extra_1": 1e-8, "extra_2": .02, "extra_3": .03}
        for weights in (five, eight):
            with self.subTest(deform_count=len(weights)):
                candidate = {name: [dict(row) for row in mesh_rows] for name, mesh_rows in source.items()}
                candidate["body"][5] = {**weights, "shirt": .2}
                module.apply_forearm_twist_gradient_on_export(set(snapshots), armature, candidate, physical_memberships=physical)
                self.assertEqual(candidate["body"][5], captured["body"][5])
                self.assertEqual(len(weights), len({name for name in captured["body"][5] if name != "shirt"}))

        overflow = {name: [dict(row) for row in mesh_rows] for name, mesh_rows in source.items()}
        overflow["body"][5] = {**eight, "extra_4": 1e-9, "shirt": .2}
        with self.assertRaisesRegex(module.ScriptError, '"body" vertex 5 exceeds eight deform influences'):
            module.apply_forearm_twist_gradient_on_export(set(snapshots), armature, overflow, physical_memberships=physical)

    def test_axial_input_snapshot_keeps_source_bilateral_physical_memberships(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        names = ("lowerarm_l", "forearm_l", "hand_l", "middle_01_r", "lowerarm_r")
        weights = (0.25, 0.30, 0.10, 0.05, 0.30)
        class Mesh:
            pass

        mesh = Mesh()
        mesh.name = "body"
        mesh.type = "MESH"
        mesh.modifiers = [types.SimpleNamespace(type="ARMATURE")]
        mesh.vertex_groups = [types.SimpleNamespace(index=index, name=name) for index, name in enumerate(names)]
        mesh.data = types.SimpleNamespace(vertices=[types.SimpleNamespace(index=0, groups=[
            types.SimpleNamespace(group=index, weight=weight)
            for index, weight in enumerate(weights)
        ])])
        armature = object()
        mesh.modifiers[0].object = armature

        snapshot = module.capture_forearm_twist_axial_input({mesh}, armature)

        self.assertEqual(dict(zip(names, weights, strict=True)), snapshot["body"][0])
        self.assertAlmostEqual(1.0, sum(snapshot["body"][0].values()))

        # Physical zero ownership is independent of the positive axial reference.
        mesh.vertex_groups.append(types.SimpleNamespace(index=len(names), name="selection_mask"))
        mesh.data.vertices[0].groups.append(types.SimpleNamespace(group=len(names), weight=0.0))
        module.capture_forearm_twist_axial_input({mesh}, armature)
        physical = {}
        snapshot = module.capture_forearm_twist_axial_input({mesh}, armature, physical)
        self.assertEqual(dict(zip(names, weights, strict=True)), snapshot["body"][0])
        self.assertEqual({**snapshot["body"][0], "selection_mask": 0.0}, physical["body"][0])

    def test_pre_write_snapshot_preserves_original_zero_without_inventing_logical_zero(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        armature = object()

        for positive, zero_group in (
            ({"hand_r": 1.0}, "selection_mask"),
            ({"lowerarm_l": 0.4, "lowerarm_r": 0.6}, "selection_mask"),
            ({"lowerarm_l": 0.4, "lowerarm_r": 0.6}, "LeftForearmTwist"),
            ({"hand_r": 1.0}, "RightForearmTwist"),
            ({"lowerarm_l": 0.8, "shirt": 0.2}, "LeftForearmTwist"),
        ):
            with self.subTest(positive=positive, zero_group=zero_group):
                memberships = dict(positive, **{zero_group: 0.0})
                calls = []

                class Group:
                    def __init__(self, name, index):
                        self.name, self.index = name, index

                    def remove(self, indices):
                        calls.append(("remove", self.name, tuple(indices)))
                        for index in indices:
                            memberships.pop(self.name)

                    def add(self, indices, weight, mode):
                        calls.append(("add", self.name, tuple(indices)))
                        memberships[self.name] = weight

                class Groups(dict):
                    def __iter__(self):
                        return iter(self.values())

                    def new(self, *, name):
                        self[name] = Group(name, len(self))
                        return self[name]

                groups = Groups({name: Group(name, index) for index, name in enumerate(memberships)})

                class Vertex:
                    index = 7

                    @property
                    def groups(self):
                        return [types.SimpleNamespace(group=groups[name].index, weight=weight)
                                for name, weight in memberships.items()]

                class Mesh:
                    pass

                mesh = Mesh()
                mesh.name, mesh.type, mesh.vertex_groups = "ordinary", "MESH", groups
                mesh.modifiers = [types.SimpleNamespace(type="ARMATURE", object=armature)]
                mesh.data = types.SimpleNamespace(vertices=[Vertex()])
                before = dict(memberships)
                module.capture_forearm_twist_axial_input({mesh}, armature)
                physical = {}
                snapshot = module.capture_forearm_twist_axial_input({mesh}, armature, physical)
                self.assertEqual(before, physical[mesh.name][0])
                self.assertEqual(positive, snapshot[mesh.name][0])
                result = dict(positive, calculated_zero=0.0)
                if zero_group == "LeftForearmTwist" and "shirt" in positive:
                    result[zero_group] = 0.3
                    result["lowerarm_l"] = 0.5
                module.write_forearm_twist_vertex_weights(mesh, [result], physical[mesh.name])
                if zero_group == "LeftForearmTwist" and "shirt" in positive:
                    self.assertEqual(0.3, memberships[zero_group])
                    self.assertEqual(0.5, memberships["lowerarm_l"])
                    self.assertAlmostEqual(0.8, memberships["lowerarm_l"] + memberships[zero_group])
                    continue
                self.assertEqual(before, memberships)
                self.assertNotIn("calculated_zero", memberships)
                with self.assertRaisesRegex(module.ScriptError, "unexpected new physical keys"):
                    module.write_forearm_twist_vertex_weights(mesh, [{**positive, "unexpected": 0.1}], physical[mesh.name])

    def test_pre_write_snapshot_refuses_unknown_group_index(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        armature = object()
        mesh = type("Mesh", (), {})()
        mesh.name, mesh.type, mesh.modifiers = "body", "MESH", [types.SimpleNamespace(type="ARMATURE", object=armature)]
        mesh.vertex_groups = [types.SimpleNamespace(index=0, name="hand_l")]
        mesh.data = types.SimpleNamespace(vertices=[types.SimpleNamespace(index=0, groups=[types.SimpleNamespace(group=99, weight=0.0)])])
        with self.assertRaisesRegex(module.ScriptError, "unknown vertex group index 99"):
            module.capture_forearm_twist_axial_input({mesh}, armature)

    def test_unattributed_opposite_hand_zero_finger_remains_physical(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        bones = {name: types.SimpleNamespace(name=name, use_deform=True, parent_recursive=[])
                 for name in ("hand_l", "hand_r")}
        bones["finger_l"] = types.SimpleNamespace(name="finger_l", use_deform=True,
            parent_recursive=[bones["hand_l"]])
        collection = type("Bones", (dict,), {"__iter__": lambda self: iter(self.values())})(bones)
        armature = types.SimpleNamespace(data=types.SimpleNamespace(bones=collection))
        mesh = type("Mesh", (), {})()
        mesh.name, mesh.type, mesh.modifiers = "body", "MESH", [types.SimpleNamespace(type="ARMATURE", object=armature)]
        mesh.vertex_groups = [types.SimpleNamespace(index=0, name="hand_r"), types.SimpleNamespace(index=1, name="finger_l")]
        mesh.data = types.SimpleNamespace(vertices=[types.SimpleNamespace(index=0, groups=[
            types.SimpleNamespace(group=0, weight=1.0), types.SimpleNamespace(group=1, weight=0.0)])])
        physical = {}
        positive = module.capture_forearm_twist_axial_input({mesh}, armature, physical)
        self.assertEqual({"hand_r": 1.0}, positive["body"][0])
        self.assertEqual({"hand_r": 1.0, "finger_l": 0.0}, physical["body"][0])

    def test_pre_write_snapshot_accepts_positive_only_row(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        armature = object()
        class Mesh:
            pass

        mesh = Mesh()
        mesh.name, mesh.type = "ordinary", "MESH"
        mesh.modifiers = [types.SimpleNamespace(type="ARMATURE", object=armature)]
        mesh.vertex_groups = [types.SimpleNamespace(index=0, name="hand_r")]
        mesh.data = types.SimpleNamespace(vertices=[types.SimpleNamespace(index=0, groups=[
            types.SimpleNamespace(group=0, weight=1.0)])])
        self.assertEqual({"ordinary": [{"hand_r": 1.0}]},
            module.capture_forearm_twist_axial_input({mesh}, armature))

        mesh.data.vertices[0].groups.append(types.SimpleNamespace(group=99, weight=0.0))
        with self.assertRaisesRegex(module.ScriptError, 'ordinary.*vertex 0.*unknown vertex group index 99'):
            module.capture_forearm_twist_axial_input({mesh}, armature)

    def test_female_source_vertex_1945_has_only_right_hand_owners(self) -> None:
        path = REPO_ROOT / "tools/mpfb/data/rigs/weights.alleycat_female.json"
        groups = json.loads(path.read_text(encoding="utf-8"))["weights"]
        row = {name: weight for name, entries in groups.items() for index, weight in entries if index == 1945}

        self.assertNotIn("middle_01_l", row)
        self.assertAlmostEqual(0.0006000600405968726, row["middle_01_r"])
        self.assertAlmostEqual(0.961996, row["index_01_r"], places=5)
        self.assertAlmostEqual(0.029, row["hand_r"], places=3)
        self.assertAlmostEqual(1.0, sum(row.values()), places=4)
        self.assertTrue(all(not name.endswith("_l") or weight <= 0.0 for name, weight in row.items()))

    def test_weight_writer_keeps_original_zero_membership_without_removing_group(self) -> None:
        module, _ = import_generate_character_with_fake_bpy([])
        members = {"middle_01_l": {0: 0.0, 1: 0.4}, "index_01_r": {0: 0.95}, "hand_r": {0: 0.05}}
        calls = []

        class Group:
            def __init__(self, name, index):
                self.name, self.index = name, index

            def remove(self, indices):
                calls.append(("remove", self.name, tuple(indices)))
                for index in indices:
                    members[self.name].pop(index)

            def add(self, indices, weight, mode):
                calls.append(("add", self.name, tuple(indices), mode))
                for index in indices:
                    members[self.name][index] = weight

        class Groups(dict):
            def __iter__(self):
                return iter(self.values())

            def new(self, *, name):
                self[name] = Group(name, len(self))
                return self[name]

        groups = Groups({name: Group(name, index) for index, name in enumerate(members)})

        class Vertex:
            def __init__(self, index):
                self.index = index

            @property
            def groups(self):
                return [types.SimpleNamespace(group=group.index, weight=members[group.name][self.index])
                        for group in groups if self.index in members[group.name]]

        mesh = types.SimpleNamespace(name="body", vertex_groups=groups, data=types.SimpleNamespace(vertices=[Vertex(0), Vertex(1)]))
        physical = [{"middle_01_l": 0.0, "index_01_r": 0.95, "hand_r": 0.05}, {"middle_01_l": 0.4}]
        module.write_forearm_twist_vertex_weights(mesh, [{"index_01_r": 0.95, "hand_r": 0.05}, {"middle_01_l": 0.4}], physical)

        self.assertEqual(0.0, members["middle_01_l"][0])
        self.assertEqual(0.4, members["middle_01_l"][1])
        self.assertEqual(0.95, members["index_01_r"][0])
        self.assertEqual(0.05, members["hand_r"][0])
        self.assertEqual(3, len(groups))
        self.assertNotIn(("remove", "middle_01_l", (0,)), calls)
        self.assertIn(("add", "middle_01_l", (0,), "REPLACE"), calls)
        members["middle_01_l"].pop(0)
        with self.assertRaisesRegex(module.ScriptError, "membership changed since snapshot"):
            module.write_forearm_twist_vertex_weights(mesh, [{"index_01_r": 0.95, "hand_r": 0.05}, {"middle_01_l": 0.4}], physical)

    def test_forearm_twist_generation_uses_shape_audited_topology_profile(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        profile = module.forearm_twist_weights.SELECTED_PROFILE
        self.assertEqual("topology-linear-p10-d110-s24", profile.name)
        self.assertEqual(0.5, profile.helper_fraction)
        self.assertEqual(0.10, profile.proximal_t)
        self.assertEqual(1.10, profile.distal_t)
        self.assertGreater(profile.smoothing_passes, 0)

    def test_twist_groups_complete_the_single_helper_chain(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        for lower, twist, hand, _wrong, _correct in module.FOREARM_TWIST_WEIGHT_GROUPS:
            with self.subTest(twist=twist):
                self.assertTrue(twist.startswith(("Left", "Right")))
                self.assertIn("ForearmTwist", twist)
                self.assertEqual(("lowerarm_l", "LeftForearmTwist", "hand_l"), module.FOREARM_TWIST_WEIGHT_GROUPS[0][:3])

    def test_rigify_animation_object_name_uses_configured_character_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        self.assertEqual("Alex.rigify", module.rigify_animation_object_name("Alex"))
        self.assertEqual("Female.rigify", module.rigify_animation_object_name("Female"))

    def test_rigify_detection_uses_non_female_configured_name(self) -> None:
        module, fake_libraries = import_generate_character_with_fake_bpy(["Alex.rigify"])

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertTrue(module.animation_source_contains_rigify_object(source_path, "Alex"))

        self.assertEqual(1, len(fake_libraries.load_calls))
        self.assertTrue(all(call[1:] == (False, False) for call in fake_libraries.load_calls))

    def test_rigify_detection_accepts_unique_reference_rig_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(["Female.rigify"])

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertTrue(module.animation_source_contains_rigify_object(source_path, "Alex"))

    def test_rigify_detection_rejects_ambiguous_reference_rig_names(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(
            ["Female.rigify", "Other.rigify"]
        )

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            with self.assertRaisesRegex(module.ScriptError, "multiple Rigify armature candidates"):
                module.animation_source_contains_rigify_object(source_path, "Alex")

    def test_rigify_detection_prefers_configured_name_when_multiple_rigs_exist(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy(
            ["Alex.rigify", "Female.rigify"]
        )

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations.blend"
            source_path.touch()

            self.assertTrue(module.animation_source_contains_rigify_object(source_path, "Alex"))


class GenerateCharacterRigifyActionTests(unittest.TestCase):
    def test_baked_action_name_strips_legacy_noexp_suffix(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        self.assertEqual("Idle-loop", module.baked_action_name(FakeAction("Idle-loop-noexp", (1, 92), [0, 1])))

    def test_baked_action_name_accepts_current_suffixless_source_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])

        self.assertEqual("Idle-loop", module.baked_action_name(FakeAction("Idle-loop", (1, 92), [0, 1])))

    def test_transient_baked_action_name_uses_collision_suffix_for_source_name(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        source_action = FakeAction("Idle-loop", (1, 92), [0, 1])
        module.bpy.data.actions = FakeActions([source_action])

        self.assertEqual(
            "Idle-loop.baked",
            module.transient_baked_action_name("Idle-loop", source_action),
        )

    def test_static_action_validation_fails_clear_diagnostic(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        static_action = FakeAction("Static", (1, 30), [0, 0, 0])

        with self.assertRaisesRegex(module.ScriptError, "static/rest-pose-only"):
            module.validate_action_multiframe_non_static(static_action, "Rigify", "source action")

    def test_single_frame_action_validation_fails_clear_diagnostic(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        single_frame_action = FakeAction("Single", (1, 1), [0, 1])

        with self.assertRaisesRegex(module.ScriptError, "not multi-frame"):
            module.validate_action_multiframe_non_static(single_frame_action, "Rigify", "source action")

    def test_rigify_action_acquisition_reuses_existing_nla_dependency_actions(self) -> None:
        module, fake_libraries = import_generate_character_with_fake_bpy(
            [],
            ["Idle-loop", "Walk-loop"],
        )
        idle_action = FakeAction("Idle-loop", (1, 92), [0, 1])
        walk_action = FakeAction("Walk-loop", (1, 72), [0, 2])
        module.bpy.data.actions = FakeActions([idle_action, walk_action])
        persisted_actions: list[FakeAction] = []

        def fake_create_persistent_action_users(actions, animation_owner, failure_context, action_label):
            persisted_actions.extend(actions)

        setattr(module, "create_persistent_action_users", fake_create_persistent_action_users)

        with TemporaryDirectory() as temp_dir:
            source_path = Path(temp_dir) / "animations_rigify.blend"
            source_path.touch()

            acquired_actions = module.append_rigify_animation_actions(
                source_path,
                types.SimpleNamespace(type="ARMATURE", name="Female.rigify"),
            )

        self.assertEqual([idle_action, walk_action], acquired_actions)
        self.assertEqual([idle_action, walk_action], persisted_actions)
        self.assertEqual([], fake_libraries.load_contexts[-1].data_to.actions)
        self.assertEqual(["Idle-loop", "Walk-loop"], [action.name for action in acquired_actions])
        self.assertTrue(all(not action.name.endswith(".001") for action in acquired_actions))

    def test_rigify_source_cleanup_validation_catches_suffixless_original_collision(self) -> None:
        module, _fake_libraries = import_generate_character_with_fake_bpy([])
        suffixed_source_action = FakeAction("Idle-loop.001", (1, 92), [0, 1])
        lingering_original_action = FakeAction("Idle-loop", (1, 92), [0, 1])
        baked_action = FakeAction("Idle-loop.baked", (1, 92), [0, 1])
        module.bpy.data.actions = FakeActions([lingering_original_action, baked_action])

        with self.assertRaisesRegex(module.ScriptError, "Idle-loop"):
            module.validate_removed_rigify_source_actions(
                [suffixed_source_action.name],
                [baked_action],
            )


if __name__ == "__main__":
    unittest.main()
