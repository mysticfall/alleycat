---
name: godot-visual-verification
description: Use for visual correctness verification in Godot, including static appearance and temporal motion claims.
---

# Godot Visual Verification

Use this skill whenever acceptance depends on how something looks or behaves on-screen.

## When To Load

Load this skill whenever acceptance depends on visible appearance or on-screen behaviour, including character pose/IK,
item placement, animation readability, motion timing, settling, or camera framing.

## Available Photobooth Scenes

The project provides these reusable photobooth base scenes:

- `@game/assets/testing/photobooth/photobooth.tscn`: generic base for new reusable setups.
- `@game/assets/testing/photobooth/templates/full_body_5_cams.tscn`: five-camera full-body character checks; use by
  default for character IK and pose work.
- `@game/assets/testing/photobooth/templates/upper_body_5_cams.tscn`: five-camera arm, hand, and torso checks.
- `@game/assets/testing/photobooth/templates/lower_body_5_cams.tscn`: five-camera leg, foot, and gait checks.
- `@game/assets/testing/photobooth/templates/face_cam.tscn`: single-camera facial expression and eye-tracking checks.

Photobooths are optional evidence tools. Prefer the actual scene or harness when it reproduces the report more
representatively. When a photobooth is appropriate, use the most specific scene that matches the verification need.

## Development Workflow

1. **Define the Visible Claim and, For a Defect, Establish the Red Baseline**
    - Record the reported symptom, representative scenario, complete visible observation window, and independently
      justified acceptance criterion and bounds.
    - For a reported defect, reproduce the symptom before production implementation and capture time-resolved or static
      evidence that fails because of it. If reproduction is impossible, keep the issue unresolved and return the
      evidence gap to the invoking agent for a user decision.
2. **Choose the Most Representative Evidence Method**
    - Prefer the existing runtime scene or test harness when it reproduces the defect.
    - Select the least-intrusive evidence method that proves the claim over its complete observation window. Temporal
      methods may be a representative playable observation, deterministic video, sampled frame sequence, or synchronised
      trace. None requires new screenshot, GDScript, C#, or photobooth infrastructure unless that infrastructure is part
      of the selected method.
    - Use a photobooth only when controlled views or static comparisons add relevant evidence; it is not a mandatory
      replacement for an actual reproducer.
    - When using a photobooth:
        - Create it under `@game/tests/<feature>/` by inheriting an existing base scene.
        - Prefer a reusable inherited base such as
          `@game/assets/testing/photobooth/templates/full_body_5_cams.tscn`.
        - For character IK/pose features, use `full_body_5_cams.tscn` by default unless the spec says otherwise.
        - If no base fits, inherit `@game/assets/testing/photobooth/photobooth.tscn`, add required cameras and markers,
          and save reusable setups under `@game/assets/`.
        - Add reference markers through `add_marker`, `get_marker`, and `remove_marker` as needed.
        - Verify every required camera rig and marker before scenario capture by inspecting per-camera screenshots.
3. **Implement Against the Unchanged Regression**
    - Collect evidence through the selected representative method.
    - If that method uses a GDScript runner, keep its base name aligned with its scene and validate its scenario states.
    - If that method uses a C# integration test, load the representative fixture and retain relevant non-visual anomaly
      assertions as supporting evidence.
    - Rerun the original regression after meaningful production changes and on the final candidate.

    > **Critical: Pre-Capture Directional Sanity Check**
    > For scenarios with directional intent (forward/back/left/right, front-facing vs back-facing, or pole direction):
    > 1. Verify the marker transform orientation matches the scene's convention BEFORE capturing screenshots.
    > 2. If your feature spec or reference doc defines forward/back semantics, cross-check marker placement against it.
    > 3. If a marker name implies direction but its transform disagrees, treat this as a setup error and escalate.
    > 4. Do not rely on screenshots alone to validate direction; a wrong scene setup makes them misleading.

## Match Evidence To the Claim

### Static Appearance

For pose, placement, framing, or appearance at a moment in time, select evidence that exposes the claimed detail.
Screenshots from valid, representative cameras are one option. Apply the camera, marker, directional-sanity, and image
inspection rules below only when screenshots are selected.

### Temporal Motion

For sliding, unwanted animation, transition, cadence, or settling claims, collect time-resolved evidence across the
complete reported transition, including visible arrival and a settling period long enough to expose recurrence. A
representative playable observation, deterministic video, sampled frame sequence, or synchronised trace is sufficient
when it proves the claim; do not add screenshot or test infrastructure merely to satisfy this skill.

Isolated screenshots or endpoints cannot prove temporal correctness. Neither can state flags, actor-root stillness,
contact leases, endpoint tolerance, or other mechanism checks prove the absence of foot sliding, extra steps, or
residual animation. Treat them as supporting evidence only. Do not start observation after a software outcome when that
would omit the visible arrival or transition that the user reported.

## Screenshot Requirements

Apply this section only when screenshots are part of the selected evidence method.

Generate all visual verification artefacts under `@game/temp/`.

- **Preferred:** Run without `--output-dir`. Screenshots default to `@game/temp`.
- **If using `--output-dir`:** Use a relative game-directory path that resolves to `@game/temp/<subdirectory>`.

```bash
# Preferred: No --output-dir, uses @game/temp by default
godot-mono -d -s --xr-mode off --path game "tests/<feature>/<test_name>.gd"

# If needed: relative path from game directory (resolves to @game/temp/<run_name>)
godot-mono -d -s --xr-mode off --path game "tests/<feature>/<test_name>.gd" -- --output-dir "temp/<run_name>"
```

> **Important:** Do not use absolute paths outside the game directory. All artefacts must be under `@game/temp/`.

## Visual Evidence Gate

### Minimum Evidence

- Reported claim, representative scenario, complete visible observation window, criterion and independent bounds.
- For a reported defect, baseline evidence that fails because of the visible symptom.
- Selected evidence method, why it is representative, and its artefact or observation record.
- Final evidence over the same scenario, criterion, and observation window.
- For screenshots, include camera and marker sanity, the directory under `@game/temp/`, independent `read` inspection,
  and the required observation tables.
- For a selected runner or integration test, include its path, execution result, and relevant assertion summary.

### Gate Checks

1. For a reported defect, the original visible symptom has a genuine failing baseline before production implementation.
2. The selected evidence method covers the relevant scene wiring and complete visible observation window.
3. Temporal claims have time-resolved evidence across the complete transition and visible settling period. Static
   screenshots, endpoints, state flags, contact leases, and actor-root stillness cannot prove absence of motion.
4. If screenshots are selected, the photobooth uses an appropriate inherited base where applicable, and camera rigs and
   markers were verified before feature-level screenshot checks.
   - For directional markers, verify transform orientation against the scene convention before trusting screenshots.
   - Cross-check marker placement when an authoritative reference defines directional semantics.
5. Any selected screenshot-capture runner was executed **without `--headless`** and produced expected screenshot sets.
6. Selected screenshots were written under `@game/temp/`, not to an absolute path outside the game directory.
7. Selected representative screenshots were independently inspected via the `read` tool and confirmed to show
   the expected behaviour for each scenario. File existence alone is not sufficient. If screenshots cannot be
   loaded with the `read` tool, they must be shared with the user for manual verification before the gate can pass.
8. When screenshot scenarios should differ, they produce visibly distinct results.
   > **Critical:** Verify that each scenario name matches the visible result. Stop and escalate when they disagree.
9. If a test runner or integration test is selected, it executed successfully and its assertions support the same claim.
10. For a selected IK/pose integration test, anomaly guards address the visual risk where practical.
11. If the user reports a visual contradiction after a prior pass, reopen the gate as `FOLLOW-UP REQUIRED` until new
    evidence resolves the contradiction.
12. **Scene-Setup Gate:** If a selected screenshot or scene setup is wrong, the gate is `FOLLOW-UP REQUIRED` even if
    screenshots appear distinct. Do not accept visual evidence from a broken setup.

### Outcome States

- `READY`
- `FOLLOW-UP REQUIRED`
- `ESCALATE`

### Primary-Agent Mapping

- `READY` → classify the delegated result as `accepted`.
- `FOLLOW-UP REQUIRED` → classify as `follow-up` and redelegate with narrowed criteria.
- `ESCALATE` → classify as `escalated` and request user decision.

For final handoff, pass the selected evidence record, artefact paths, expected cues, and complete observation window to
the `reviewer`. When screenshots are selected, the reviewer must inspect them independently rather than relying on the
coder's interpretation.

### Required Visual Inspection Tables

When screenshots are selected, visual gate reports must include structured observations, not only a prose claim that
screenshots look correct.

For individual scenarios, include:

| Image | Expected Visible Cue | Observed Visible Cue | Confidence | Pass/Fail |
| ----- | -------------------- | -------------------- | ---------- | --------- |

For scenario comparisons where distinctness matters, include:

| Pair | Expected Difference | Observed Difference | Distinct? |
| ---- | ------------------- | ------------------- | --------- |

Use `clear`, `ambiguous`, or `not visible` for confidence. Any `ambiguous`, `not visible`, or `Distinct? = no` result
must make the gate `FOLLOW-UP REQUIRED` unless the uncertainty is escalated to the user.

## Image Analysis Rules

Apply these rules whenever screenshots are part of the selected evidence method.

### Never Fabricate Visual Observations

Always analyse screenshots using the `read` tool to load the image and then inspect the visual output. Never infer or
describe image content yourself; report only what is visible in the image. If the image cannot be loaded, or the visual
result is unclear, escalate to the user and explain that the visual result cannot be verified.

### Vision Tool Prompt Guidelines

When using the `read` tool to analyse screenshots:

1. **Use simple, objective visible cues.** For example: "left pupil is closer to the outer eye corner than neutral" or
   "hand is above shoulder height". Avoid relying only on terms like "natural" or "correct".
2. **Break complex checks into yes/no or either/or observations** and record the result in the required table.
3. **Compare expected-different images side by side in the conversation context.** Treat near-identical scenarios as a
   failure even when runtime parameters or assertions passed.
4. **Use multiple camera angles whenever available.** Apply the same cue across relevant views.
5. **If observations conflict or remain non-committal, escalate to the user** instead of guessing.

### Escalation Protocol

If vision tools produce unreliable or contradictory results:

1. Report honestly: "The vision tool's analysis may be inaccurate — results were contradictory across camera angles."
2. Share the raw screenshots with the user (by reading the image files so they appear in the conversation).
3. Ask the user to make the visual judgement.
4. Do not proceed with follow-up work that depends on the visual assessment being correct.

## Workflow Guides

- [Photobooth Workflow Guide](photobooth-workflow.md)
- [Photobooth Script Patterns](photobooth-script-patterns.md)

## Run Record Fields

- Spec path.
- Reported visible symptom and representative scenario.
- Complete observation window, acceptance criterion, independently justified bounds, and baseline failure.
- Selected evidence method and why it is representative.
- Evidence record or artefact paths for the complete observation window.
- For screenshots, camera and marker verification notes and the artefact directory under `@game/temp/`.
- For a selected runner or integration test, its path, execution result, and assertion summary.
- Final result against the unchanged regression and any unproved remainder.
- Gate outcome.
