---
id: SPEECH-001
title: Wav2Arkit Blendshape Feasibility Prototype
---

# Wav2Arkit Blendshape Feasibility Prototype

## Requirement

Keep implementation and specification aligned for the current wav2arkit-to-ARKit blendshape playback prototype.

## Goal

Document a feasibility slice that proves end-to-end local playback (WAV input → ONNX inference → mapped mesh blendshape
updates) in Godot.

## Prototype Status

This spec documents a **feasibility prototype** only. It does not define a production-ready speech animation feature.

## Current Implementation Constraints

- Audio input is provided as `AudioStreamWav` and must be PCM 16-bit, 16 kHz, mono.
- The prototype throws explicit initialisation errors when those audio assumptions are violated.
- Playback is audio-synchronised through `AudioStreamPlayer3D`; sampled validation windows require audio to be actively
  playing.
- Mesh targeting scans immediate `Skeleton3D` children and applies only to mesh nodes with known ARKit blendshape
  channels.

## In Scope

- Prototype playback pipeline initialisation and frame application through `BlendShapePlayer`.
- Mapping and applying inferred ARKit channels to available character mesh blendshape channels.
- Prototype validation scene and runner checks for initialisation, frame advance, and observable weight change.
- Validation-only execution path for fast local confidence checks.

## Out Of Scope

- Production runtime guarantees (latency budgets, stability guarantees, memory/performance tuning).
- Network/streaming, live microphone capture, and dialogue-system integration.
- Retargeting quality tuning, animation polish, and expressive-quality acceptance criteria.
- Full automated regression coverage beyond the prototype scene/runner checks.

## Implementation References

- `@game/src/Speech/BlendShapePlayer.cs`
- `@game/tests/speech/blendshape_playback_test.tscn`
- `@game/tests/speech/blendshape_playback_test.gd`
- `@game/models/wav2arkit_cpu/config.json`

## Validation Workflow

1. Run formatting verification:
   - `dotnet format --verify-no-changes AlleyCat.sln`
2. Run build verification:
   - `dotnet build AlleyCat.sln -warnaserror`
3. Run the prototype runner scene in validate-only mode:
   - `godot-mono --path game --script res://tests/speech/blendshape_playback_test.gd -- --validate-only`
4. Confirm mapping and playback assertions from the runner pass before any screenshot capture or manual review.

## Validation Criteria

- `BlendShapePlayer` initialises without error.
- Inferred frame count is greater than zero.
- Blendshape channel count equals 52 (config contract).
- At least one mesh and one channel are mapped for application.
- Audio playback is active during the sampled validation window.
- Applied frame count increases over time (playback advances).
- Weight change events increase over time (observable channel updates).

## Known Limitations

- Prototype depends on local model/config/sample assets and fixed ARKit channel assumptions.
- Validation is scene-runner based and is not yet a comprehensive cross-character or cross-audio test matrix.
- No production fallback behaviour is defined for missing mappings or degraded inference quality.

## Follow-Up Notes

- Add dedicated automated tests for mapping contract drift and configuration/schema integrity.
- Define production constraints (performance, error handling, observability, and integration boundaries) in a follow-up
  speech spec before feature hardening.
- Expand validation to additional characters/audio samples when moving beyond feasibility.

## References

- @specs/index.md
