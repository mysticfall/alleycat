# Visual Probes: 3D Photobooth

Use this flow when you need screenshot artefacts of 3D content (characters, props, poses, IK setups, etc.).

The photobooth (`@game/assets/testing/photobooth/photobooth.tscn`) provides a studio HDRI environment,
a floor, a camera, and a `SubjectAnchor` for placing the subject.

Create it via `ProbeUtils.setup_photobooth(tree)`, then load a subject explicitly with `photobooth.load(subject_path)`.

## Inline Example: Portrait and Landscape Capture

```gdscript
extends SceneTree

const SUBJECT_PATH := "res://assets/characters/reference/reference_female.tscn"
const PORTRAIT_CAMERA_POSITION := Vector3(0.0, 1.45, 2.1)
const LANDSCAPE_CAMERA_POSITION := Vector3(1.9, 1.25, 1.9)


func _init() -> void:
	await run_probe()


func run_probe() -> void:
	var photobooth: ProbeUtils.Photobooth = ProbeUtils.setup_photobooth(self)
	if photobooth == null:
		return

	var subject: Node3D = photobooth.load(SUBJECT_PATH)
	if subject == null:
		return

	# Let transforms settle after attaching the subject.
	await ProbeUtils.wait_frames(self, 1)

	# Optional subject edits before capture.
	subject.rotation_degrees = Vector3(0.0, 18.0, 0.0)

	# Portrait capture using automatic look-at (subject AABB centre).
	photobooth.use_portrait()
	photobooth.set_perspective(38.0)
	photobooth.position_camera(PORTRAIT_CAMERA_POSITION)
	await ProbeUtils.wait_frames(self, 3)
	await photobooth.capture("3d_001_portrait.jpg")

	# Landscape capture with explicit look-at.
	photobooth.use_landscape()
	photobooth.position_camera(LANDSCAPE_CAMERA_POSITION, subject.global_position + Vector3(0.0, 1.0, 0.0))
	await ProbeUtils.wait_frames(self, 3)
	await photobooth.capture("3d_001_landscape.jpg")

	photobooth.dispose()
	quit(0)
```

## Run Command

Run visual probe scripts **without** `--headless` to enable the renderer:

```bash
godot-mono -d -s --xr-mode off --path game "temp/<probe_script>.gd"
```

## Probe Planning For 3D Subjects

Plan captures before writing the loop. Keep the plan generic so it works for characters, props, and environment objects.

Define these items per probe run:

1. **Critical Regions**: parts that must stay visible for review (for example head/neck joints, contact points, hinge
   area,
   silhouette edge).
2. **Capture List**: one entry per required view/state (`capture_id`, intent, pose/state, camera mode, expected file
   name).
3. **Framing Band**: explicit target for how much of the frame the subject should occupy.
4. **Fail Conditions**: what invalidates the run (missing shot, clipped critical region, unusable framing).

Keep this plan in a small manifest/checklist file next to the probe script or in the probe script constants.

## Framing Acceptance Criteria

Objective checks first: base qualitative judgement on measurable framing checks (coverage, centring, edge margins), then
use visual judgement for final quality calls.

Apply these checks to every capture unless the feature spec defines stricter values:

- **Critical Regions Fully Visible**: no required region touches or crosses image edges.
- **Subject Remains Centred**: subject centre stays near frame centre (avoid left/right or up/down drift between
  captures).
- **Useful Subject Coverage**: subject is large enough for review; avoid captures dominated by background or floor.
- **Consistent Series Framing**: multi-capture sets keep similar scale and centre unless a specific shot intentionally
  changes it.

Practical default thresholds (adjust only when the spec requires otherwise):

- Subject centre stays within the middle **40% x 40%** of the frame.
- Subject occupies roughly **35% to 85%** of frame height.
- Critical regions keep at least a small safety margin from each edge (for example about **3%** of frame width/height).

## Multi-Capture Verification Workflow

Use this workflow when a probe produces more than one screenshot.

1. **Create A Run-Isolated Output Directory**
    - Pass `--output-dir` with a unique run path (for example `game/temp/probes/<probe_name>/<run_id>`).
    - Do not mix runs in the same folder.
2. **Prepare A Capture Manifest**
    - List all expected captures with file names and review intent.
    - Include per-capture framing checks and any feature-specific checks.
3. **Execute Capture Loop**
    - For each manifest entry: apply state, wait required frames, capture, then record pass/fail against checks.
4. **Review Per Capture**
    - Confirm file exists and is readable.
    - Confirm framing checks pass (critical regions visible, centred, no excessive blank space).
    - Confirm feature-specific visual checks pass.
5. **Fail Fast On Any Broken Capture**
    - Exit non-zero if any expected file is missing, duplicate names overwrite another capture, or any review check
      fails.
6. **Summarise Run Output**
    - Report output directory, manifest/checklist used, total captures, failed capture IDs, and rerun advice.

## Framing Tuning Iteration Control

Use this when adjusting camera/framing values across runs:

1. **Keep Runs Isolated**
    - Write each iteration to its own output directory; never overwrite a previous iteration.
2. **Compare Iterations Explicitly**
    - Compare current vs previous run using the same objective framing checks and capture manifest.
    - Record whether framing is meaningfully better, unchanged, or worse.
3. **Stop After 3 Non-Improving Iterations**
    - If three consecutive iterations show no meaningful framing progress, stop tuning and escalate with run evidence.

## Screenshot Output

`ProbeUtils.capture_screenshot(...)` and `Photobooth.capture(...)` write JPG files under:

- `--output-dir <path>` / `--output-dir=<path>`, when provided
- otherwise `@game/temp` (resolves to `temp/` under the Godot project root)

Each successful capture prints:

- `Saved a screenshot: <absolute-path>`
