"""Blender read-only reviewed SOMA77 action ingestion; see README Reviewed Source Ingestion."""
import argparse
import json
import math
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from kimodo_pipeline.reviewed_source import (COORDINATES, assemble, publish, require, sha,
                                            validate_descriptor, validate_review)


def main():
    import bpy
    from mathutils import Matrix, Vector

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--descriptor', type=Path, required=True)
    parser.add_argument('--review', type=Path, required=True)
    parser.add_argument('--calibration', type=Path, required=True)
    parser.add_argument('--destination', type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index('--') + 1:])
    d, review = [json.loads(p.read_text()) for p in (args.descriptor, args.review)]
    validate_descriptor(d)
    validate_review(d, review, sha(args.descriptor))
    s = d['source']
    source_path = Path(s['path']).resolve()
    protected = [source_path, args.descriptor, args.review, args.calibration, *review['evidence_sha256']]
    before = {str(p): sha(p) for p in protected}
    require(sha(source_path) == s['sha256'], 'Source bytes differ from reviewed identity')
    calibration = json.loads(args.calibration.read_text())
    bpy.ops.wm.open_mainfile(filepath=str(source_path))
    scene = bpy.context.scene
    require(scene.render.fps / scene.render.fps_base == 30 and scene.unit_settings.scale_length == 1,
            'Expected 30 Hz metre source; no implicit retiming or unit conversion')
    rig = bpy.data.objects.get(s['rig'])
    require(rig is not None and rig.type == 'ARMATURE' and rig.matrix_world == Matrix.Identity(4),
            'Supported identity-object source armature required')
    action = bpy.data.actions.get(s['action'])
    require(action is not None and list(action.frame_range) == s['frames'], 'Action/frame identity mismatch')
    require(not rig.animation_data or not rig.animation_data.nla_tracks, 'NLA composition is not supported')
    animation = rig.animation_data_create()
    animation.action = action
    slots = list(action.slots)
    if slots:
        slot = next((slot for slot in slots if slot.identifier == s['slot']), None)
        require(slot is not None and slot.target_id_type == 'OBJECT', 'Explicit action slot missing')
        animation.action_slot = slot
    else:
        require(s['slot'] is None, 'Legacy action must declare null slot')
    names = [b.name for b in rig.data.bones if not b.name.endswith(('_RET', '_LOOK')) and b.name != 'Root']
    parents = [names.index(rig.data.bones[n].parent.name) if rig.data.bones[n].parent.name in names else -1 for n in names]
    require(names == calibration['names'] and parents == calibration['parents'], 'Source/calibration skeleton mismatch')
    rests = [rig.data.bones[n].matrix_local.copy() for n in names]
    require(max(abs(rests[i][r][c] - calibration['blenderRest'][i][r][c])
                for i in range(len(names)) for r in range(4) for c in range(4)) < 1e-5,
            'Source rests differ from supported calibrated SOMA skeleton')
    axis = Matrix(((1, 0, 0), (0, 0, 1), (0, -1, 0)))
    samples = []
    previous_yaw = None
    for frame in range(s['frames'][0], s['frames'][1] + 1):
        scene.frame_set(frame)
        bpy.context.view_layer.update()
        evaluated = rig.evaluated_get(bpy.context.evaluated_depsgraph_get())
        world = [(axis @ evaluated.pose.bones[n].matrix.translation,
                  axis @ evaluated.pose.bones[n].matrix.to_3x3() @ rests[i].to_3x3().inverted() @ axis.inverted())
                 for i, n in enumerate(names)]
        local = []
        for i, (position, rotation) in enumerate(world):
            require(abs(rotation.determinant() - 1) < 1e-5
                    and max(abs(v) for row in (rotation.transposed() @ rotation - Matrix.Identity(3)) for v in row) < 1e-5,
                    'Non-rigid source transforms cannot be sanitised without changing motion')
            if parents[i] >= 0:
                pp, pr = world[parents[i]]
                position, rotation = pr.inverted() @ (position - pp), pr.inverted() @ rotation
            q = rotation.to_quaternion().normalized()
            local.append(list(position) + [q.x, q.y, q.z, q.w])
        forward = world[0][1] @ Vector((0, 0, 1))
        require(math.hypot(forward.x, forward.z) > 1e-6, 'Hips heading undefined')
        yaw = math.atan2(forward.x, forward.z)
        if previous_yaw is not None:
            yaw = previous_yaw + (yaw - previous_yaw + math.pi) % (2 * math.pi) - math.pi
        previous_yaw = yaw
        samples.append({'frame': frame, 'time_seconds': (frame - s['frames'][0]) / 30, 'pose': local,
                        'root_m': local[0][:3], 'heading_radians': yaw,
                        'velocity_to_next_mps': None, 'angular_velocity_to_next_rps': None})
    for a, z in zip(samples, samples[1:]):
        a['velocity_to_next_mps'] = [(z['root_m'][j] - a['root_m'][j]) * 30 for j in range(3)]
        a['angular_velocity_to_next_rps'] = (z['heading_radians'] - a['heading_radians']) * 30
    provenance = {'blender': bpy.app.version_string, 'exporter_sha256': sha(__file__),
                  'contract_sha256': sha(Path(__file__).parent / 'kimodo_pipeline/reviewed_source.py'),
                  'calibration_sha256': sha(args.calibration), 'review_sha256': sha(args.review),
                  'technical_transforms': ['Blender_XYZ_to_XZ_minusY', 'rest_axes_to_canonical_identity',
                                           'world_to_parent_local', 'unit_quaternion_representation'],
                  'coordinates': COORDINATES, 'generation': d.get('generation', 'not_supplied')}
    bundle = assemble(d, args.descriptor.read_text(), review, samples, names, parents, provenance)
    require(all(sha(p) == h for p, h in before.items()), 'Protected input changed during export')
    publish(args.destination, bundle, protected)
    print('REVIEWED_SOURCE_EXPORTED', len(samples), 'samples;', len(names), 'joints;', sha(args.destination))


if __name__ == '__main__':
    main()
