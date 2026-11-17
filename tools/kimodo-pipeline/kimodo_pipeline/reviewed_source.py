"""Reviewed-action contracts. No generation receipt, motion repair or aesthetic admission tests."""
import copy
import hashlib
import json
import math
import os
from pathlib import Path
import tempfile

ROLES = ('forward', 'backward', 'left_noncross', 'right_noncross',
         'left_cross', 'right_cross', 'left_turn', 'right_turn')
COORDINATES = {'units': 'metres', 'up': '+Y', 'forward': '+Z', 'anatomical_left': '+X',
               'root': 'evaluated_Hips_not_ground_Root', 'pose': 'canonical_identity_rest_parent_local',
               'heading': 'atan2_forward_x_forward_z_radians', 'foot': 'joint_not_sole'}


def sha(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def require(condition, message):
    if not condition:
        raise ValueError(message)


def number(value):
    return type(value) in (int, float) and math.isfinite(value)


def vector(value, size):
    return isinstance(value, list) and len(value) == size and all(number(v) for v in value)


def blocks(descriptor):
    for variant in descriptor['variants']:
        for stage in variant['stages']:
            for role in stage['roles']:
                yield variant['id'] + '/' + stage['id'] + '/' + role['role'], role


def interval(value, full, minimum=4):
    return (isinstance(value, list) and len(value) == 2 and all(type(v) is int for v in value)
            and full[0] <= value[0] <= value[1] <= full[1] and value[1] - value[0] + 1 >= minimum)


def validate_descriptor(d):
    require(d.get('schema') == 'anim002_reviewed_descriptor_v1', 'Unsupported descriptor schema')
    s = d['source']
    require(all(isinstance(s.get(k), str) and s[k] for k in ('path', 'sha256', 'action', 'rig'))
            and isinstance(s.get('slot'), (str, type(None))), 'Explicit source/action/slot identity required')
    require(s['fps'] == 30 and interval(s['frames'], s['frames']), 'Only ordered 30 Hz sources supported')
    variants = d['variants']
    require(isinstance(variants, list) and variants, 'At least one variant required')
    ids = set()
    for v in variants:
        require(v['id'] and '/' not in v['id'] and v['id'] not in ids and v['posture'], 'Unique variant and posture required')
        ids.add(v['id'])
        require(v['stages'], 'At least one speed stage required')
        stages = set()
        for stage in v['stages']:
            require(stage['id'] and '/' not in stage['id'] and stage['id'] not in stages, 'Unique stage required')
            stages.add(stage['id'])
            roles = [r['role'] for r in stage['roles']]
            require(roles and len(set(roles)) == len(roles) and set(roles) <= set(ROLES), 'Invalid role coverage')
            require(stage['coverage'] in ('partial', 'complete'), 'Explicit coverage required')
            require(stage['coverage'] != 'complete' or set(roles) == set(ROLES), 'Complete stage requires all eight roles')
            for r in stage['roles']:
                require(interval(r['usable'], s['frames']) and interval(r['representative'], r['usable'], 2),
                        'Invalid usable/representative range')
                require(r['loopability'] == 'non_cyclic', 'This reader does not admit unverified cycles')
                require(vector(r['travel_direction'], 3), 'Travel direction required separately from facing')
                require(r['phase']['observed_support'] in ('unknown', 'supported', 'mismatched')
                        and r['phase']['transfer_compatibility'] == 'unverified'
                        and 'intended' in r['phase'], 'Explicit intended/observed phase required')
            ordered = sorted(r['usable'] for r in stage['roles'])
            require(all(a[1] < b[0] for a, b in zip(ordered, ordered[1:])), 'Role ranges overlap')


def validate_review(d, review, descriptor_hash):
    require(review.get('schema') == 'anim002_reviewed_approval_v1' and review.get('decision') == 'accepted'
            and bool(review.get('reviewer')) and bool(review.get('approval_origin')), 'Explicit human approval required')
    require(review.get('descriptor_sha256') == descriptor_hash and review.get('source') == d['source'],
            'Human approval source/descriptor identity mismatch')
    require(review.get('accepted_ranges') == {k: r['usable'] for k, r in blocks(d)}
            and review.get('full_window') == d['source']['frames'], 'Human approval ranges/window mismatch')
    require(review.get('preview_rig') == 'Female' and review.get('calibration_preserved') is True,
            'Calibrated Female preview review required')
    evidence = review.get('evidence_sha256', {})
    require(len(evidence) >= 2 and all(Path(p).is_file() and sha(p) == h for p, h in evidence.items()),
            'Source and preview review evidence identity mismatch')


def physical(samples, first_frame, span):
    a, z = (i - first_frame for i in span)
    dt = (z - a) / 30
    velocity = [(samples[z]['root_m'][i] - samples[a]['root_m'][i]) / dt for i in range(3)]
    distance = sum(math.hypot(y['root_m'][0] - x['root_m'][0], y['root_m'][2] - x['root_m'][2])
                   for x, y in zip(samples[a:z], samples[a + 1:z + 1]))
    return {'interval': span, 'velocity_mps': velocity, 'planar_net_speed_mps': math.hypot(velocity[0], velocity[2]),
            'planar_path_speed_mps': distance / dt,
            'angular_velocity_rps': (samples[z]['heading_radians'] - samples[a]['heading_radians']) / dt}


def assemble(d, descriptor_text, review, samples, names, parents, provenance):
    descriptor_hash = hashlib.sha256(descriptor_text.encode('utf-8')).hexdigest()
    require(json.loads(descriptor_text) == d, 'Descriptor text/content mismatch')
    validate_descriptor(d)
    validate_review(d, review, descriptor_hash)
    bundle = {'schema': 'anim002_reviewed_source_v1', 'descriptor': copy.deepcopy(d),
              'descriptor_sha256': descriptor_hash, 'descriptor_text': descriptor_text,
              'review': review, 'coordinates': COORDINATES,
              'names': names, 'parents': parents, 'samples': samples, 'provenance': provenance,
              'disposition': 'HUMAN_ACCEPTED_SOURCE_ONLY'}
    for _, r in blocks(bundle['descriptor']):
        r['physical_reference'] = physical(samples, d['source']['frames'][0], r['representative'])
    validate_bundle(bundle)
    return bundle


def validate_bundle(b):
    require(b.get('schema') == 'anim002_reviewed_source_v1' and b.get('coordinates') == COORDINATES
            and b.get('disposition') == 'HUMAN_ACCEPTED_SOURCE_ONLY', 'Unsupported/unreviewed source bundle')
    d = b['descriptor']
    validate_descriptor(d)
    authored = copy.deepcopy(d)
    for _, r in blocks(authored):
        r.pop('physical_reference', None)
    require(hashlib.sha256(b['descriptor_text'].encode('utf-8')).hexdigest() == b['descriptor_sha256']
            and json.loads(b['descriptor_text']) == authored, 'Descriptor hash/content mismatch')
    validate_review(d, b['review'], b['descriptor_sha256'])
    require(b['review']['decision'] == 'accepted' and b['review']['source'] == d['source']
            and b['review']['descriptor_sha256'] == b['descriptor_sha256']
            and b['review']['accepted_ranges'] == {k: r['usable'] for k, r in blocks(d)}, 'Review binding mismatch')
    names, parents = b['names'], b['parents']
    require(len(names) == 77 and len(set(names)) == 77 and names[0] == 'Hips'
            and all(n in names for n in ('LeftFoot', 'RightFoot')) and len(parents) == len(names)
            and parents[0] == -1 and all(type(p) is int and 0 <= p < i for i, p in enumerate(parents[1:], 1)),
            'Supported ordered SOMA77 skeleton required')
    first, last = d['source']['frames']
    samples = b['samples']
    require(len(samples) == last - first + 1, 'Full source chronology required')
    for i, s in enumerate(samples):
        require(s['frame'] == first + i and s['time_seconds'] == i / 30, 'Source ordering/timing changed')
        require(len(s['pose']) == len(names) and all(vector(p, 7) and abs(sum(x*x for x in p[3:]) - 1) < 1e-5
                for p in s['pose']), 'Invalid/non-finite canonical pose')
        require(vector(s['root_m'], 3) and s['root_m'] == s['pose'][0][:3] and number(s['heading_radians']),
                'Root/heading mismatch')
        if i + 1 < len(samples):
            expected = [(samples[i+1]['root_m'][j] - s['root_m'][j]) * 30 for j in range(3)]
            require(s['velocity_to_next_mps'] == expected
                    and s['angular_velocity_to_next_rps'] == (samples[i+1]['heading_radians'] - s['heading_radians']) * 30,
                    'Local derivative mismatch')
        else:
            require(s['velocity_to_next_mps'] is None and s['angular_velocity_to_next_rps'] is None,
                    'No future derivative at final sample')
    for _, r in blocks(d):
        require(r['physical_reference'] == physical(samples, first, r['representative']), 'Physical reference mismatch')
    json.dumps(b, allow_nan=False)


def publish(destination, bundle, protected=()):
    """Single self-contained content/review/provenance destination, replaced only after readback validation."""
    validate_bundle(bundle)
    destination = Path(destination).resolve()
    require(destination.suffix == '.json', 'Published content destination must be JSON')
    require(destination not in {Path(p).resolve() for p in protected}, 'Cannot publish over source/evidence')
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(prefix='.' + destination.name, suffix='.tmp', dir=destination.parent)
    staged = Path(name)
    try:
        with os.fdopen(fd, 'w', encoding='utf-8') as stream:
            json.dump(bundle, stream, separators=(',', ':'), allow_nan=False)
            stream.flush()
            os.fsync(stream.fileno())
        loaded = json.loads(staged.read_text())
        validate_bundle(loaded)
        require(loaded == bundle, 'Staged bytes differ')
        os.replace(staged, destination)
    finally:
        staged.unlink(missing_ok=True)
