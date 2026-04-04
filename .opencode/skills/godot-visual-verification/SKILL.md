---
name: godot-visual-verification
description: Use whenever implementation depends on visual correctness in Godot. Follow the photobooth-first workflow: create a test scene under @game/tests, verify camera and marker framing, run screenshot scenarios, then add C# integration tests.
---

# Godot Visual Verification

Use this skill whenever acceptance depends on how something looks or behaves on-screen.

## When To Load

Load this skill for any task that needs screenshot evidence (for example character pose/IK checks, item placement,
animation readability, or camera framing validation).

## Development Workflow

1. **Create A Test Scene (Photobooth)**
    - Create a photobooth scene under `@game/tests/<feature>/` by inheriting an existing base scene.
    - Prefer reusable inherited bases first (for example
      `@game/assets/characters/reference/female/photobooth/full_body_5_cams.tscn`).
    - For character IK/pose features, use `full_body_5_cams.tscn` as the default base unless the spec says otherwise.
    - If no suitable base exists, inherit `@game/assets/testing/photobooth/photobooth.tscn`, add the required
      cameras/markers, and save reusable setups under `@game/assets/`.
    - Add reference markers with Photobooth API (`add_marker`, `get_marker`, `remove_marker`) as needed.
    - Verify every required camera rig and relevant marker placement first by capturing per-camera screenshots
      (`CameraRig.capture_screenshot`) and reviewing them visually.
2. **Use The Test Scene To Implement The Feature**
    - Write a GDScript test runner with the same base name as the scene (`<name>.tscn` + `<name>.gd`).
    - Load the test scene, apply scenario states, and capture screenshots (`Photobooth.capture_screenshots`) for each
      visual checkpoint.
    - Iterate implementation until screenshots show expected appearance and behaviour.
3. **Write C# Integration Tests**
    - Add a C# integration test that loads the same test scene.
    - Verify behaviour using non-visual assertions (for example marker proximity, transform ranges, state flags,
      or node presence).

## Visual Evidence Gate

### Minimum Evidence

- Test scene path under `@game/tests/<feature>/`.
- Base scene used (or new reusable base path created under `@game/assets/`).
- Camera and marker verification summary per required camera rig.
- Test runner script path (`.gd`) with matching base name.
- Final screenshot artefact directory.
- Visual inspection summary confirming screenshots show expected behaviour (not just that files were generated).
- C# integration test path and non-visual assertion summary.

### Gate Checks

1. The photobooth test scene exists and is based on an appropriate inherited base.
2. Camera rigs and markers were verified before feature-level screenshot checks.
3. The runner was executed **without `--headless`** and produced expected screenshot sets.
4. Representative screenshots were visually inspected (directly or via vision-capable tool) and confirmed to show the
   expected behaviour for each scenario — file existence alone is not sufficient.
5. Distinct scenarios produce visually distinct results (for example different poses should look different in
   screenshots).
6. C# integration tests verify the same functionality non-visually.

### Outcome States

- `READY`
- `FOLLOW-UP REQUIRED`
- `ESCALATE`

### Primary-Agent Mapping

- `READY` → classify the delegated result as `accepted`.
- `FOLLOW-UP REQUIRED` → classify as `follow-up` and redelegate with narrowed criteria.
- `ESCALATE` → classify as `escalated` and request user decision.

## Workflow Guides

- [Photobooth Workflow Guide](photobooth-workflow.md)
- [Photobooth Script Patterns](photobooth-script-patterns.md)

## Run Record Fields

- Spec path.
- Test scene path.
- Test runner script path.
- Camera and marker verification notes.
- Final verification artefact directory.
- C# integration test path and assertion summary.
- Gate outcome.
