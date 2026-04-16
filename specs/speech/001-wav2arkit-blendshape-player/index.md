---
id: SPEECH-001
title: Wav2Arkit Blendshape Player
---

# Wav2Arkit Blendshape Player

## Requirement

Generate lipsync animation from speech audio using the Wav2Arkit model running local ONNX
inference. This implementation runs on any GPU and provides basic ARKit blendshape output, but
does not include emotion inference or dedicated eye movement data.

## Goal

Prove end-to-end local playback (WAV input → ONNX inference → mapped mesh blendshape updates) in
Godot as a feasibility prototype.

## User Requirements

1. Prototype playback must produce observable lip-sync-style facial motion from supported speech audio input.
2. Prototype validation should provide contributors with a quick confidence check for mapping and frame progression.

## Technical Requirements

1. The prototype must run local Wav2Arkit ONNX inference and map output channels to ARKit-compatible blendshape names.
2. Input contract must enforce PCM 16-bit, 16 kHz, mono WAV via `AudioStreamWav`.
3. Playback must remain audio-synchronised through `AudioStreamPlayer3D` and apply frame updates over time.
4. Validation runner contracts must verify initialisation, mapping presence, and progression metrics.

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

## Acceptance Criteria

1. The specification defines both user-visible prototype outcomes and technical implementation contracts.
2. Prototype scope is explicitly bounded as feasibility work rather than production runtime guarantees.
3. Audio format and playback synchronisation contracts required for deterministic prototype behaviour are defined.
4. Validation workflow and criteria define reproducible checks for initialisation, mapping, and playback progression.

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
