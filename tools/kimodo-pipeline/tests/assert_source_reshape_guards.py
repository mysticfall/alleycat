"""Corruption probes for the real-source interior reshape admission guards."""
from __future__ import annotations

import copy
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from kimodo_pipeline.source_repair import SourceRepairError, _validate_reshape_evidence


baseline = {
    "lateral_scale": 0.67,
    "after_width": 0.69,
    "maximum_limb_length_error": 0.0,
    "minimum_knee_orientation_dot": 0.99,
    "maximum_contact_height_delta": 0.0,
    "contact_height_delta_limit": 1e-5,
    "maximum_planted_velocity_increase": 0.0,
    "planted_velocity_increase_limit": 1e-2,
    "maximum_bilateral_scale_difference": 0.0,
    "lateral_axis_alignment": 1.0,
    "double_application_detected": False,
}

corruptions = {
    "excessive_compression": ("lateral_scale", 0.59),
    "leg_stretch": ("maximum_limb_length_error", 1e-3),
    "knee_inversion": ("minimum_knee_orientation_dot", -0.5),
    "contact_height_drift": ("maximum_contact_height_delta", 1e-3),
    "planted_velocity_spike": ("maximum_planted_velocity_increase", 0.1),
    "wrong_lateral_axis_or_sign": ("lateral_axis_alignment", -1.0),
    "asymmetry": ("maximum_bilateral_scale_difference", 0.01),
    "double_application": ("double_application_detected", True),
}

_validate_reshape_evidence(baseline)
accepted = []
for name, (field, value) in corruptions.items():
    evidence = copy.deepcopy(baseline)
    evidence[field] = value
    try:
        _validate_reshape_evidence(evidence)
    except SourceRepairError:
        accepted.append(name)
    else:
        raise AssertionError(f"reshape corruption was not rejected: {name}")

print("KIMODO_SOURCE_RESHAPE_GUARDS=" + json.dumps(sorted(accepted)))
