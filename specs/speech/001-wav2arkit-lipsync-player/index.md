---
id: SPCH-001
title: Wav2Arkit LipSync Player
---

# Wav2Arkit LipSync Player

## Requirement

Generate lipsync animation from speech audio using the Wav2Arkit model running
local ONNX inference. Deliver basic ARKit blendshape output on any GPU. Emotion
inference and dedicated eye movement data are out of scope.

## Goal

Prove end-to-end local playback (WAV input → ONNX inference → mapped mesh blendshape
updates) in Godot as a feasibility prototype.

## User Requirements

1. Prototype playback produces observable lip-sync facial motion from supported
   speech audio input.
2. Users can manually trigger playback via `LipSyncPlayer.Play(AudioStreamWav)`.
3. Calling `Play` while playback is active stops current playback and begins new
   playback immediately.

## Technical Requirements

1. Run local Wav2Arkit ONNX inference and map output channels to ARKit-compatible
   blendshape names.
2. Input contract: `AudioStreamWav` must be PCM 16-bit, 16 kHz, mono.
3. Expose `Play(AudioStreamWav speech)` for manual playback initiation.
4. Synchronise playback via `AudioStreamPlayer3D` and apply frame updates over time.
5. **Interruption contract**: If `Play` is called while playback is active, stop
   current audio and frame application immediately before starting new playback.
   The player is not required to cancel a synchronous inference in progress.
6. Validation runner verifies initialisation, mapping presence, and frame progression.

## In Scope

- Prototype playback pipeline initialisation and frame application through
  `LipSyncPlayer`.
- Mapping inferred ARKit channels to available character mesh blendshape channels.
- Manual playback invocation via `LipSyncPlayer.Play(AudioStreamWav)`.
- Prototype validation scene and runner for initialisation, frame advance, and
  observable weight change.

## Out Of Scope

- Production runtime guarantees including latency budgets, stability guarantees,
  memory/performance tuning, and error handling observability.
- Network/streaming, live microphone capture, and dialogue-system integration.
- Retargeting quality tuning, animation polish, and expressive-quality criteria.
- Emotion inference and dedicated eye movement data.

## Acceptance Criteria

1. Spec defines user-visible prototype outcomes and technical implementation contracts.
2. Audio format and playback synchronisation contracts are defined for deterministic
   prototype behaviour.
3. Manual playback contract is defined: playback triggered via `Play()`, not
   auto-started in `_Ready()`.
4. Interruption contract is defined: calling `Play` while playback is active restarts
   immediately without cancelling in-progress inference.
5. Validation workflow provides reproducible checks for initialisation, mapping,
   playback initiation, and playback progression.
6. Technical requirement coverage: all six requirements map to acceptance criteria.

## References

- @specs/index.md
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`