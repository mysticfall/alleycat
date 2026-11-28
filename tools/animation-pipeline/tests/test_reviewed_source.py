"""Approved integrity/publication contracts; synthetic motion, not artistic acceptance evidence."""
import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from kimodo_pipeline.reviewed_source import (assemble, publish, sha, validate_bundle,
                                            validate_descriptor, validate_review)


class ReviewedSourceTests(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[3] / 'game/temp/motionbricks/reviewed-source-tests'
        root.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=root)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        evidence = [self.root / 'source-evidence', self.root / 'preview-evidence']
        for p in evidence:
            p.write_text('synthetic fixture, not human reviewed motion')
        source = {'path': 'synthetic.blend', 'sha256': 'a' * 64, 'action': 'synthetic',
                  'slot': 'OBfixture', 'rig': 'Kimodo_Source', 'frames': [1, 12], 'fps': 30}
        role = {'role': 'forward', 'usable': [2, 10], 'representative': [3, 9],
                'loopability': 'non_cyclic', 'travel_direction': [0, 0, 1],
                'phase': {'intended': 'fixture', 'observed_support': 'unknown', 'transfer_compatibility': 'unverified'}}
        self.d = {'schema': 'anim002_reviewed_descriptor_v1', 'source': source,
                  'variants': [{'id': 'fixture', 'posture': 'fixture',
                                'stages': [{'id': 'one', 'coverage': 'partial', 'roles': [role]}]}]}
        self.descriptor_text = json.dumps(self.d)
        self.descriptor_hash = hashlib.sha256(self.descriptor_text.encode()).hexdigest()
        self.review = {'schema': 'anim002_reviewed_approval_v1', 'decision': 'accepted',
                       'reviewer': 'synthetic-test-only', 'approval_origin': 'synthetic contract fixture',
                       'descriptor_sha256': self.descriptor_hash, 'source': copy.deepcopy(source),
                       'accepted_ranges': {'fixture/one/forward': [2, 10]}, 'full_window': [1, 12],
                       'preview_rig': 'Female', 'calibration_preserved': True,
                       'evidence_sha256': {str(p): sha(p) for p in evidence}}
        samples = []
        for i in range(12):
            pose = [[0, 0, 0, 0, 0, 0, 1] for _ in range(77)]
            pose[0][:3] = [0, 1, i * 0.01]
            samples.append({'frame': i + 1, 'time_seconds': i / 30, 'pose': pose,
                            'root_m': pose[0][:3], 'heading_radians': 0.,
                            'velocity_to_next_mps': None, 'angular_velocity_to_next_rps': None})
        for a, z in zip(samples, samples[1:]):
            a['velocity_to_next_mps'] = [(z['root_m'][j] - a['root_m'][j]) * 30 for j in range(3)]
            a['angular_velocity_to_next_rps'] = 0.
        names = ['Hips', 'LeftFoot', 'RightFoot'] + ['joint' + str(i) for i in range(74)]
        self.bundle = assemble(self.d, self.descriptor_text, self.review, samples, names, [-1] + [0] * 76, {})

    def test_accepts_generic_partial_content_without_generation_receipt(self):
        validate_bundle(self.bundle)
        self.assertEqual(len(self.bundle['samples']), 12)
        self.assertNotIn('generation', self.bundle['provenance'])
        self.assertAlmostEqual(self.bundle['descriptor']['variants'][0]['stages'][0]['roles'][0]
                               ['physical_reference']['planar_net_speed_mps'], .3)

    def test_unreviewed_changed_source_or_ranges_cannot_be_admitted(self):
        for key, value in [('decision', 'rejected'), ('descriptor_sha256', 'c' * 64),
                           ('accepted_ranges', {'fixture/one/forward': [3, 10]})]:
            review = copy.deepcopy(self.review)
            review[key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                validate_review(self.d, review, self.descriptor_hash)
        changed = copy.deepcopy(self.d)
        changed['source']['sha256'] = 'c' * 64
        with self.assertRaisesRegex(ValueError, 'identity mismatch'):
            validate_review(changed, self.review, self.descriptor_hash)

    def test_bad_format_rate_range_and_false_completeness_refused(self):
        for mutate in [lambda d: d.update(schema='unknown'), lambda d: d['source'].update(fps=24),
                       lambda d: d['variants'][0]['stages'][0]['roles'][0].update(usable=[10, 13]),
                       lambda d: d['variants'][0]['stages'][0].update(coverage='complete')]:
            d = copy.deepcopy(self.d)
            mutate(d)
            with self.assertRaises(ValueError):
                validate_descriptor(d)
        b = copy.deepcopy(self.bundle)
        b['samples'][4]['pose'][0][0] = float('nan')
        with self.assertRaises(ValueError):
            validate_bundle(b)

    def test_publication_failure_preserves_prior_bytes_and_removes_staging(self):
        dest = self.root / 'content.json'
        publish(dest, self.bundle)
        before = dest.read_bytes()
        with patch('kimodo_pipeline.reviewed_source.os.replace', side_effect=OSError('simulated replace failure')):
            with self.assertRaises(OSError):
                publish(dest, self.bundle)
        self.assertEqual(dest.read_bytes(), before)
        invalid = copy.deepcopy(self.bundle)
        invalid['review']['decision'] = 'rejected'
        with self.assertRaises(ValueError):
            publish(dest, invalid)
        self.assertEqual(dest.read_bytes(), before)
        self.assertEqual(list(self.root.glob('.*.tmp')), [])
        with self.assertRaisesRegex(ValueError, 'source/evidence'):
            publish(dest, self.bundle, [dest])


if __name__ == '__main__':
    unittest.main()
