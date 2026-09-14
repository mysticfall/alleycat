# Photobooth Workflow Guide

Use this guide when a photobooth is the selected evidence harness. The primary
[`godot-visual-verification`](SKILL.md) skill owns claim selection, regression-first sequencing, static versus temporal
evidence, conditional infrastructure, acceptance, and reporting. An actual reproducer may be more representative and
does not require a photobooth, screenshot runner, or integration test.

## Step 1: Create A Test Scene

Create a photobooth scene under `@game/tests/<feature>/`.

### Choose The Best Base Scene

1. Prefer an existing inherited photobooth that already matches the subject type.
    - Example: `@game/assets/testing/photobooth/templates/full_body_5_cams.tscn`.
    - For character pose/IK verification, treat this as the default base unless the spec explicitly requires a different
      setup.
2. If no suitable base exists, inherit the empty base:
    - `@game/assets/testing/photobooth/photobooth.tscn`.
3. If the camera/subject setup will be reused across features, save that reusable inherited setup under `@game/assets/`.

### Add And Configure Markers

Use Photobooth marker API:

- `add_marker(marker_name, label_text := "")`
- `get_marker(marker_name)`
- `remove_marker(marker_name)`

Markers are visible in captures, so use them as stable reference points for pose and placement checks.

### Verify Camera And Marker Positions First

Before feature verification:

1. Enable only markers relevant to the current check.
2. Capture from each required camera rig using `CameraRig.capture_screenshot`.
3. Review screenshots one-by-one using the `read` tool.
4. Confirm framing is suitable for the intended verification task.

Do not proceed to feature-level visual checks until this framing pass is valid.

### Save The Scene

Use `SceneUtils.save_scene(scene_root, output_path)` and keep the test scene in `@game/tests/<feature>/`.

## Test Scene Self-Containment Rule

Test scenes must be self-contained with all common setup configured directly in the scene. Test scripts should focus
only on the verification workflow: manipulating scene state between scenarios and capturing screenshots.

### What Belongs In The Scene

- IK nodes, modifiers, and their bone/target/pole configurations.
- Target markers (hand targets, head targets, pole targets) with initial transforms.
- All node-path bindings between IK solvers and their targets.
- Any node that must be a direct child of a specific parent (for example `SkeletonModifier3D` nodes under a
  `Skeleton3D`).

### What Belongs In The Script

- Iterating over pose scenarios and moving targets to pose marker positions.
- Controlling marker visibility per scenario.
- Capturing screenshots.
- Validate-only mode and command-line argument handling.

### Why

A self-contained test scene can be opened directly in the Godot editor for inspection and debugging. If all setup is
done in the script, the editor shows only the base scene with no IK nodes, making it impossible to debug configuration
issues (such as missing bone names or incorrect node paths) without running the script.

## Step 2: Establish the Visible Baseline

For a reported defect, use the scene before production implementation to reproduce the symptom and record evidence that
fails the criterion defined under the primary skill. For temporal claims, collect a frame sequence or other
time-resolved evidence over the complete reported transition and visible settling period; isolated checkpoints are
insufficient.

## Optional Scripted Capture Runner

If scripted capture is selected, write a runner with the same base name as the test scene and store it alongside the
scene:

- `@game/tests/<feature>/<test_name>.tscn`
- `@game/tests/<feature>/<test_name>.gd`

For defect work, wait until the symptom-red baseline exists. The runner should then:

1. Load/instantiate the test scene.
2. Apply scenario state A, then capture screenshots.
3. Apply scenario state B/C..., then capture screenshots.
4. Repeat until all required visual checkpoints are covered.

Use `Photobooth.capture_screenshots(file_name)` for full camera sets and `CameraRig.capture_screenshot(file_name)` when
single-camera captures are needed.

Rerun the unchanged visible regression after meaningful production changes and on the final candidate.

## Optional Supporting C# Integration Tests

Add a C# integration test only when it is selected as supporting evidence. When selected, load the same test scene and
validate behaviour using non-visual checks.

Examples:

- Distances to marker positions.
- Transform/rotation constraints.
- Node states and flags after scenario transitions.

This test should verify supporting functional correctness corresponding to the visible evidence. Non-visual assertions
do not replace time-resolved evidence for temporal claims.

## Run Commands

### Scene/Setup Scripts (No Rendering Required)

```bash
godot-mono -d -s --headless --xr-mode off --path game "temp/<asset_or_setup_script>.gd"
```

### Capture Scripts (Rendering Required)

**Never use `--headless` for scripts that capture screenshots.** Headless mode disables the renderer, causing all
viewport captures to fail with blank images or errors. This is the most common mistake in visual verification runs.

All visual verification artefacts must be generated under `@game/temp/`. This is a mandatory requirement.

- **Preferred:** Run without `--output-dir`. Screenshots default to `@game/temp`.
- **If using `--output-dir`:** The path must still resolve to `@game/temp/<subdirectory>`. Use a relative path from the
  game directory.

```bash
# Preferred: No --output-dir, uses @game/temp by default
godot-mono -d -s --xr-mode off --path game "tests/<feature>/<test_name>.gd"

# If needed: relative path from game directory (resolves to @game/temp/<run_name>)
godot-mono -d -s --xr-mode off --path game "tests/<feature>/<test_name>.gd" -- --output-dir "temp/<run_name>"
```

## Screenshot Review

Apply the image inspection, comparison-table, ambiguity, camera-sanity, and escalation rules in the primary
[`godot-visual-verification`](SKILL.md) skill. File generation alone is not evidence of visible correctness.

## Screenshot Output

`SceneUtils.capture_screenshot(...)`, `SceneUtils.capture_viewport_screenshot(...)`,
`CameraRig.capture_screenshot(...)`,
and `Photobooth.capture_screenshots(...)` write JPG files under:

- `--output-dir <path>`, when provided as a **relative path** that resolves to `@game/temp/<subdirectory>`.
- otherwise `@game/temp`.

Each successful capture prints:

- `Saved a screenshot: <absolute-path>`

## Common Pitfalls

- **Using `--headless` for capture runs** — causes blank/failed screenshots. Never pass `--headless` when the script
  captures screenshots.
- **Marking visual tasks complete without reviewing screenshots** — file existence does not prove visual correctness.
  Always inspect representative images.
- **Not comparing similar scenarios** — if two scenarios (for example "up" and "lean-back") produce nearly identical
  screenshots, the marker placement or scenario logic likely has an error.
- **Writing artefacts outside `@game/temp/`** — all screenshots must be under `@game/temp/`. Using absolute paths or
  omitting `--output-dir` and expecting files elsewhere will cause verification failures.
