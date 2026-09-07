"""Deterministic, content-independent Blender fixture for pipeline tests.

The declaration below is the oracle.  It deliberately does not open, copy, or
inspect an authored Kimodo asset.
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
import json
from pathlib import Path
import subprocess
import sys
from typing import Any


def _pipeline_root() -> Path:
    for candidate in Path(__file__).resolve().parents:
        if (candidate / "kimodo_pipeline").is_dir() and (candidate / "config").is_dir():
            return candidate
    raise RuntimeError("Kimodo pipeline package root could not be located.")


PIPELINE_ROOT = _pipeline_root()
if str(PIPELINE_ROOT) not in sys.path:
    sys.path.insert(0, str(PIPELINE_ROOT))

from kimodo_pipeline.bootstrap_paths import repository_root


ROOT = repository_root()
FIXTURE_SCHEMA = "synthetic-kimodo-blend/1"
RIG_NAME = "Kimodo_Source"
REFERENCE_ACTION = "pose_ref_idle"
FIXTURE_VARIANT = "ember"


@dataclass(frozen=True)
class SyntheticActionContract:
    """The complete declared behaviour of one disposable source Action."""

    name: str
    kind: str
    frame_window: tuple[int, int]
    root_track: tuple[tuple[float, float, float], tuple[float, float, float]]
    heading_radians: tuple[float, float]
    contacts: tuple[str, ...]
    expected_selection: tuple[str, ...]
    intended_rejection: str | None = None


@dataclass(frozen=True)
class SyntheticKimodoFixtureManifest:
    """Content oracle persisted into every generated fixture."""

    schema: str
    rig_name: str
    bones: tuple[str, ...]
    segments: tuple[tuple[str, int, int], ...]
    actions: tuple[SyntheticActionContract, ...]

    def to_json(self) -> str:
        return json.dumps(asdict(self), sort_keys=True, separators=(",", ":"))


MANIFEST = SyntheticKimodoFixtureManifest(
    schema=FIXTURE_SCHEMA,
    rig_name=RIG_NAME,
    bones=(
        "Root", "Hips", "Chest", "Head", "LeftLeg", "LeftShin", "LeftFoot",
        "RightLeg", "RightShin", "RightFoot",
    ),
    segments=(
        ("loop", 1, 33),
        ("reversal_contact", 40, 72),
        ("pivot", 80, 112),
        ("finite_transition", 120, 152),
        ("lateral", 160, 192),
        ("arc", 200, 232),
    ),
    actions=(
        SyntheticActionContract(
            "kimodo_multi_fixture_loop", "loop", (1, 33), ((0, 0, 0), (0, 0, 2)),
            (0, 0), ("left_planted", "right_release"), ("wf_r", "wb_l"),
        ),
        SyntheticActionContract(
            "kimodo_multi_fixture_transition", "finite_transition", (120, 152),
            ((0, 0, 0), (0, 0, 1)), (0, 0), ("right_release",), ("i2f_r", "f2i_r"),
        ),
        SyntheticActionContract(
            "kimodo_multi_fixture_pivot", "pivot", (80, 112), ((0, 0, 0), (0, 0, 0)),
            (0, -0.7853981633974483), ("left_planted", "right_planted"), ("pr45", "pl45"),
        ),
        SyntheticActionContract(
            "kimodo_multi_fixture_lateral", "lateral", (160, 192), ((0, 0, 0), (1, 0, 0)),
            (0, 0), ("right_release",), ("xr_r", "xl_l"),
        ),
        SyntheticActionContract(
            "kimodo_multi_fixture_arc", "arc", (200, 232), ((0, 0, 0), (1, 0, 1)),
            (0, -0.4), ("left_release", "right_release"), ("ar_r", "al_l"),
        ),
        SyntheticActionContract(
            "kimodo_multi_fixture_reversal", "reversal_contact", (40, 72),
            ((0, 0, 0), (-1, 0, 0)), (0, 0), ("left_release", "right_contact"), ("sr", "sl"),
        ),
        SyntheticActionContract(
            "kimodo_multi_fixture_rejected_missing_chest", "intentional_failure", (1, 33),
            ((0, 0, 0), (0, 0, 0)), (0, 0), (), (), "missing_required_chest_track",
        ),
    ),
)


class SyntheticKimodoBlendBuilder:
    """Build the declared fixture at an explicit disposable path."""

    manifest = MANIFEST

    def build(self, output: Path) -> subprocess.CompletedProcess[str]:
        output = output.resolve()
        if output.suffix != ".blend":
            raise ValueError("Synthetic Kimodo fixture output must end in .blend")
        output.parent.mkdir(parents=True, exist_ok=True)
        from kimodo_pipeline.blender_cli import run_blender_python

        return run_blender_python(["--python", Path(__file__), "--", output], ROOT)

    def build_bootstrap_monolith(self, output: Path) -> subprocess.CompletedProcess[str]:
        """Build a complete disposable bootstrap input from this manifest.

        This deliberately models only the structural contracts consumed by the
        bootstrap stages.  It never copies, opens, or characterises an authored
        blend, and its variant-shaped names are fixture declarations.
        """
        output = output.resolve()
        if output.suffix != ".blend":
            raise ValueError("Synthetic Kimodo fixture output must end in .blend")
        output.parent.mkdir(parents=True, exist_ok=True)
        from kimodo_pipeline.blender_cli import run_blender_python

        return run_blender_python(
            ["--python", Path(__file__), "--", output, "--bootstrap-monolith"], ROOT
        )

    def runtime_descriptor(self, processed_set: Path, runtime_root: Path, *, package_name: str) -> dict[str, Any]:
        """Return a declared, disposable Godot package descriptor.

        The caller owns both paths.  This helper deliberately derives its only
        expectations from the fixture-produced index rather than any authored
        asset or production package inventory.  ``processed_set`` is the staged
        processed SET directory (set-level blend plus the variant metadata
        directory); ``runtime_root`` is the variant-scoped runtime package root
        ending in ``runtime/<set>/<variant>``.
        """
        variants = sorted(
            child.name for child in processed_set.iterdir() if (child / "processed-index.json").is_file()
        )
        if len(variants) != 1:
            raise ValueError("Synthetic processed set must declare exactly one variant metadata directory")
        variant = variants[0]
        index = processed_set / variant / "processed-index.json"
        blends = sorted(processed_set.glob("*.blend"))
        if len(blends) != 1:
            raise ValueError("Synthetic processed set must contain exactly one blend")
        if runtime_root.parent.name != package_name or runtime_root.name != variant:
            raise ValueError("Synthetic runtime root must end in runtime/<set>/<variant>")
        return {
            "name": package_name,
            "character": "fixture_character",
            "variant": variant,
            "index": str(index),
            "blend": str(blends[0]),
            "root": str(runtime_root),
            "fixture_schema": FIXTURE_SCHEMA,
            "fixture_variant": FIXTURE_VARIANT,
        }


def _create_rig(name: str = RIG_NAME) -> Any:
    import bpy

    armature = bpy.data.armatures.new(name)
    rig = bpy.data.objects.new(name, armature)
    bpy.context.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    bones: dict[str, Any] = {}

    def bone(name: str, head: tuple[float, float, float], tail: tuple[float, float, float], parent: str | None = None) -> None:
        created = armature.edit_bones.new(name)
        created.head, created.tail = head, tail
        created.use_deform = name != "Root"
        if parent is not None:
            created.parent = bones[parent]
        bones[name] = created

    bone("Root", (0, 0, 0), (0, 1, 0))
    bone("Hips", (0, 1, 0), (0, 2, 0), "Root")
    bone("Chest", (0, 2, 0), (0, 3, 0), "Hips")
    bone("Head", (0, 3, 0), (0, 3.6, 0), "Chest")
    for side, x in (("Left", .35), ("Right", -.35)):
        bone(f"{side}Leg", (x, 1.8, 0), (x, .9, 0), "Hips")
        bone(f"{side}Shin", (x, .9, 0), (x, .15, .08), f"{side}Leg")
        bone(f"{side}Foot", (x, .15, .08), (x, .15, .52), f"{side}Shin")
    bpy.ops.object.mode_set(mode="POSE")
    for pose_bone in rig.pose.bones:
        pose_bone.rotation_mode = "ZYX"
    bpy.ops.object.mode_set(mode="OBJECT")
    return rig


def _reference_action(rig: Any, name: str, root_yaw: float = 0.0) -> None:
    """Create a one-frame declared pose reference usable by workspace tests."""
    import bpy

    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    slot = action.slots.new(rig.id_type, rig.name)
    bag = action.layers.new("SYNTHETIC_REFERENCE").strips.new(
        type="KEYFRAME"
    ).channelbag(slot, ensure=True)
    for bone in ("Hips", "Root"):
        _curve(bag, f'pose.bones["{bone}"].location', 0, [1], [0.0])
    _curve(bag, 'pose.bones["Root"].rotation_euler', 1, [1], [root_yaw])


def _bootstrap_source_rig() -> Any:
    """Create the source rig plus one declared retarget helper bone."""
    import bpy

    rig = _create_rig()
    bpy.context.view_layer.objects.active = rig
    rig.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    helper = rig.data.edit_bones.new("Root_RET")
    helper.head, helper.tail = (0, 0, 0), (0, 0.5, 0)
    helper.parent = rig.data.edit_bones["Root"]
    bpy.ops.object.mode_set(mode="OBJECT")
    collection = rig.data.collections.new("Retarget Bones")
    collection.assign(rig.data.bones["Root_RET"])
    return rig


def _fixture_mesh() -> None:
    """Create one visible fixture-owned character mesh and material."""
    import bpy

    mesh = bpy.data.meshes.new("Female.fixture_mesh")
    mesh.from_pydata([(0, 0, 0), (0.1, 0, 0), (0, 0.1, 0)], [], [(0, 1, 2)])
    material = bpy.data.materials.new("Female.fixture_mesh")
    mesh.materials.append(material)
    character = bpy.data.objects.new("Female.fixture_mesh", mesh)
    bpy.context.collection.objects.link(character)


def _bootstrap_monolith(output: Path) -> None:
    """Write a fully synthetic source for manifest/extraction/workspace tests."""
    import bpy

    bpy.ops.wm.read_factory_settings(use_empty=True)
    sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))
    from kimodo_pipeline.compatibility import ensure_registered

    ensure_registered()
    source = _bootstrap_source_rig()
    target = _create_rig("Female")
    target.data.bones["Root"].use_deform = True
    for kind in ("COPY_LOCATION", "COPY_ROTATION"):
        constraint = target.pose.bones["Root"].constraints.new(kind)
        constraint.target = source
        constraint.subtarget = "Root"
        constraint.owner_space = "WORLD"
        constraint.target_space = "WORLD"
    _fixture_mesh()
    _create_rig("Kimodo_IK")
    _create_rig("Kimodo_PoseRef_fixture")
    waypoint = bpy.data.objects.new("Kimodo_Waypoint_fixture", None)
    bpy.context.collection.objects.link(waypoint)
    _reference_action(source, REFERENCE_ACTION)
    _reference_action(source, "pose_ref_turn_fixture", -0.7853981633974483)
    contract = SyntheticActionContract(
        f"loc_{FIXTURE_VARIANT}_wf_r_loop",
        "retarget_source",
        (1, 33),
        ((0, 0, 0), (0, 0, 1)),
        (0, 0),
        ("left_release", "right_release"),
        ("wf",),
    )
    _action(source, contract)
    retarget_source = bpy.data.actions[contract.name]
    source_events = [
        {"id": "release_l", "anatomical_side": "Left", "event_kind": "Release", "source_frame": 1, "boundary_context": None, "selector_confidence": 1.0, "selector_evidence": {"fixture": True}},
        {"id": "release_r", "anatomical_side": "Right", "event_kind": "Release", "source_frame": 17, "boundary_context": None, "selector_confidence": 1.0, "selector_evidence": {"fixture": True}},
    ]
    derived_events = [
        {"source_event_id": row["id"], "id": row["id"], "anatomical_side": row["anatomical_side"], "event_kind": "Release", "output_frame": row["source_frame"], "boundary_context": None, "canonicalised_from_duplicate_endpoint": None}
        for row in source_events
    ]
    repair_support = {1 + (offset % 32) for offset in (*range(0, 6), *range(27, 33))}
    repair_support_dilated = sorted({
        1 + ((frame - 1 + delta) % 32)
        for frame in repair_support for delta in range(-4, 5)
    })
    def seed_displacement(row):
        seed = row["output_frame"]
        stencil = sorted({1 + ((seed - 1 + delta) % 32) for delta in range(-4, 5)})
        intersection = sorted(set(stencil) & set(repair_support_dilated))
        return {
            "contract": "anim002_post_repair_phase_seed_displacement_v1",
            "derived_output_frame": seed, "post_repair_output_frame": seed,
            "canonical_delta_frames": 0,
            "source_detector_stencil_frames": stencil,
            "post_repair_detector_stencil_frames": stencil,
            "repair_support_dilated_frames": repair_support_dilated,
            "source_repair_intersection_frames": intersection,
            "post_repair_repair_intersection_frames": intersection,
            "displacement_authorised": bool(intersection),
        }
    retarget_source["anim002_source_directional_event_evidence"] = json.dumps({
        "contract": "anim002_source_directional_phase_events_v1", "source_interval": [1, 33], "events": source_events,
    }, sort_keys=True)
    retarget_source["anim002_event_derivation_evidence"] = json.dumps({
        "contract": "anim002_event_derivation_v1", "derivation": "native", "operations": [],
        "frame_mapping": {"source_start": 1, "source_end": 33, "output_start": 1, "output_end": 33},
        "events": derived_events,
    }, sort_keys=True)
    retarget_source["anim002_post_repair_event_evidence"] = json.dumps({
        "contract": "anim002_post_repair_directional_phase_events_v2", "evaluation_stage": "final_repaired_evaluated_hierarchy",
        "timeline": {"frame_start": 1, "frame_end": 33, "fps": 24.0},
        "sample_semantics": {"is_loop": True, "conceptual_sample_count": 33, "unique_runtime_sample_count": 32, "duplicate_endpoint": True},
        "events": [{**row, "derivation_event_id": row["id"], "derived_output_frame": row["output_frame"], "repair_frame_delta": 0, "seed_displacement_provenance": seed_displacement(row), "accepted": True, "confidence": 1.0, "measurement": {"fixture": True}} for row in derived_events],
        "refusals": [],
    }, sort_keys=True)
    retarget_source["anim002_loop_repair_evidence"] = json.dumps({
        "contract": "anim002_selected_loop_repair_provenance_v2",
        "algorithm": "symmetric_two_ended_hierarchy_pose_repair_v1", "blend_profile": "quadratic",
        "opening_support": {"window": [0, 5], "width": 6},
        "immutable_interior": [6, 26], "closing_support": {"window": [27, 32], "width": 6},
        "repair_cost": 0.0,
        "selection": {"contract": "bounded_duration_scaled_repaired_output_naturalness_v1", "selected_rank_key": [0.0]},
        "transform_derivation": {
            "source_range": [1, 33],
            "frame_mapping": [{"source_frame": frame, "output_frame": frame} for frame in range(1, 34)],
            "role_derivation": "native", "temporal_order": "preserved", "anatomical_mapping": "preserved",
            "evaluated_space": "generated Root-relative evaluated hierarchy", "repaired_domain": "complete non-Root motion hierarchy",
            "root_handling": "immutable generated Root carriage",
        },
        "sample_semantics": {"conceptual_sample_count": 33, "unique_runtime_sample_count": 32, "duplicate_endpoint": True, "conceptual_boundary_sample": 32, "runtime_interval": [0, 31]},
    }, sort_keys=True)
    retarget_source["anim002_transport_contract"] = json.dumps({
        "contract": "anim002_sample_transport_v1", "boundary": "loop", "fps": 24.0,
        "frame_start": 1, "frame_end": 33, "sample_count": 33,
        "duration_frames": 32, "duration_seconds": 32 / 24,
        "period_frames": 32, "runtime_sample_count": 32, "conceptual_boundary_frame": 33,
    }, sort_keys=True)
    for name, start, end in MANIFEST.segments:
        segment = bpy.context.scene.kimodo.motion_segments.add()
        segment.prompt, segment.start_frame, segment.end_frame = name, start, end
    bpy.context.scene.kimodo.source_armature = source
    bpy.context.scene.kimodo.reuse_armature = source
    bpy.context.scene.frame_start = MANIFEST.segments[0][1]
    bpy.context.scene.frame_end = MANIFEST.segments[-1][2]
    bpy.context.scene["synthetic_kimodo_manifest"] = MANIFEST.to_json()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output), check_existing=False)


def _curve(bag: Any, path: str, index: int, frames: list[int], values: list[float]) -> None:
    curve = bag.fcurves.new(data_path=path, index=index)
    curve.keyframe_points.add(len(frames))
    for point, frame, value in zip(curve.keyframe_points, frames, values):
        point.co = frame, value
        point.interpolation = "LINEAR"


def _action(rig: Any, contract: SyntheticActionContract | None) -> None:
    import bpy

    name = REFERENCE_ACTION if contract is None else contract.name
    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    slot = action.slots.new(rig.id_type, rig.name)
    bag = action.layers.new("SYNTHETIC").strips.new(type="KEYFRAME").channelbag(slot, ensure=True)
    start, end = (1, 232) if contract is None else contract.frame_window
    frames = list(range(start, end + 1))
    progress = [index / max(1, len(frames) - 1) for index in range(len(frames))]
    root_start, root_end = ((0, 0, 0), (0, 0, 0)) if contract is None else contract.root_track
    heading_start, heading_end = (0, 0) if contract is None else contract.heading_radians
    omitted = set() if contract is None or contract.intended_rejection is None else {"Chest"}
    for bone in MANIFEST.bones:
        if bone == "Root":
            continue
        if bone in omitted:
            continue
        location = [[0.0] * len(frames) for _ in range(3)]
        for axis in range(3):
            _curve(bag, f'pose.bones["{bone}"].location', axis, frames, location[axis])
            rotation = heading_start + (heading_end - heading_start) * progress[-1] if bone == "Hips" and axis == 1 else 0.0
            _curve(bag, f'pose.bones["{bone}"].rotation_euler', axis, frames, [rotation * value for value in progress])
    for axis in range(3):
        _curve(
            bag, 'pose.bones["Root"].location', axis, frames,
            [root_start[axis] + (root_end[axis] - root_start[axis]) * value for value in progress],
        )
        _curve(
            bag, 'pose.bones["Root"].rotation_euler', axis, frames,
            [heading_start + (heading_end - heading_start) * value for value in progress]
            if axis == 1 else [0.0] * len(frames),
        )
    if contract is not None:
        action["synthetic_kimodo_contract"] = json.dumps(asdict(contract), sort_keys=True)


def build_blend(output: Path) -> None:
    """Run inside Blender and write only ``output``."""
    import bpy

    sys.path.insert(0, str(ROOT / "tools/kimodo-pipeline"))
    from kimodo_pipeline.compatibility import ensure_registered

    bpy.ops.wm.read_factory_settings(use_empty=True)
    ensure_registered()
    rig = _create_rig()
    _action(rig, None)
    for contract in MANIFEST.actions:
        _action(rig, contract)
    segments = bpy.context.scene.kimodo.motion_segments
    for name, start, end in MANIFEST.segments:
        segment = segments.add()
        segment.prompt, segment.start_frame, segment.end_frame = name, start, end
    bpy.context.scene["synthetic_kimodo_manifest"] = MANIFEST.to_json()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=str(output), check_existing=False)


if __name__ == "__main__":
    arguments = sys.argv[sys.argv.index("--") + 1 :]
    destination = Path(arguments[0])
    if "--bootstrap-monolith" in arguments:
        _bootstrap_monolith(destination)
    else:
        build_blend(destination)
