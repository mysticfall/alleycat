---
name: godot-visual-verification
description: Use for tasks that require probe-driven visual verification and calibration in Godot.
---

# Godot Visual Verification

Use this skill when acceptance depends on visual correctness, not only code-level assertions.

## When To Load

Load this skill when a spec requires visual checks (for example IK alignment, marker placement, pose quality, UI layout,
or world-space readability).

## Development Workflow

1. Define the functional visual property under test.
2. Build a calibration probe that makes mismatches easy to see.
3. Run probe captures and inspect screenshots for functional correctness.
4. Iterate implementation from evidence until the gate passes, or escalate after three non-improving iterations.
5. Only then add/accept value-locking tests.
6. Run final verification captures.

## Visual Evidence Gate

### Minimum Evidence

- Calibration probe script path.
- Calibration screenshot artefact directory.
- Functional metric summary per capture (for example residual distance, visibility booleans).
- Final verification artefact directory.

### Gate Checks

1. The calibration probe ran before value-locking tests were accepted.
2. Screenshots were reviewed for the functional property (not framing-only quality).
3. Probe captures are diagnosable (isolated variable, required context visible, no obstructive overlap).
4. Screenshots and functional metrics do not conflict, or conflict was resolved.
5. Value-locking tests are derived from calibrated evidence.

### Outcome States

- `PASS`
- `FOLLOW-UP REQUIRED`
- `ESCALATE`

### Primary-Agent Mapping

- `PASS` → classify the delegated result as `accepted`.
- `FOLLOW-UP REQUIRED` → classify as `needs-follow-up` and redelegate with narrowed criteria.
- `ESCALATE` → classify as `escalated` and request user decision.

## Probe Guides

- [2D/UI guide](visual-probes-2d.md)
- [3D/photobooth guide](visual-probes-3d.md)

## Run Record Fields

- Spec path.
- Calibration probe path.
- Calibration artefact directory/directories.
- Final verification artefact directory.
- Functional checks reviewed.
- Functional metric summary.
- Gate outcome.
