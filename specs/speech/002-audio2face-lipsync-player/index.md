---
id: SPCH-002
title: Audio2Face LipSync Player
---

# Audio2Face LipSync Player

## Requirement

Drive character facial animation from speech audio using NVIDIA Audio2Face
blendshape inference served over HTTP. Replace local ONNX inference in
`LipSyncPlayer` with remote HTTP calls to the Audio2Face service.

## Goal

Evaluate Audio2Face quality for production use through a feasibility prototype.
The prototype demonstrates ARKit output with dedicated eye-rotation data for
accurate eye movement. Contributors can validate server connectivity and
playback with a repeatable workflow.

## User Requirements

1. Playback produces observable facial animation from supported speech audio
   via remote Audio2Face inference.
2. Eye movement output is more accurate when eye-rotation translation is
   enabled.
3. Contributors can validate server connectivity and playback with a repeatable
   workflow.
4. Playback is triggered manually via `LipSyncPlayer.Play(AudioStreamWav)`
   method.

## Technical Requirements

1. Player converts supported `AudioStreamWav` PCM input (16-bit, 16 kHz, mono)
   to the Audio2Face API payload format.
2. Inference uses HTTP endpoints (`/health`, `/blendshapes`) with configurable
   server URI.
3. Returned frames are mapped into the `LipSyncPlayer` base class with audio
   synchronisation.
4. Player exposes `Play(AudioStreamWav speech)` for manual playback initiation.
5. Interruption contract: If `Play` is called during active playback, stop
   current playback and begin new playback immediately. In-flight HTTP requests
   need not be cancelled.
6. Optional eye-rotation translation defines baseline subtraction, smoothing,
   directional mapping, and clamp rules.
7. Model/mode compatibility and health probing behaviour are explicitly
   defined.
8. Audio format contract (16-bit PCM, 16 kHz, mono) is enforced at
   initialisation.

## In Scope

- HTTP integration with remote Audio2Face inference service.
- ARKit blendshape output with eye-rotation translation.
- Configurable server URI via health probe and inference endpoints.
- Manual playback trigger via `Play(AudioStreamWav)` method.
- Audio format validation at initialisation.
- Interruption handling for active playback.

## Out Of Scope

- Production runtime guarantees (latency budgets, error recovery, streaming
  inference).
- Live microphone capture or streaming audio pipelines.
- Dialogue system integration or runtime model switching.
- Animation polish and expressive-quality acceptance criteria.
- Docker container lifecycle management.
- Automated regression beyond mock-backed integration tests.

## Acceptance Criteria

1. Specification defines both prototype outcomes (user layer) and integration
   contracts (technical layer).
2. Prototype scope is bounded as feasibility work, not production runtime
   guarantees.
3. HTTP integration, audio-format contracts, and playback synchronisation are
   explicitly defined.
4. Manual playback contract: Playback triggers via `Play(AudioStreamWav)`, not
   auto-started in `_Ready()`.
5. Interruption contract: Calling `Play` during active playback stops current
   playback and begins new playback immediately. In-flight HTTP requests are
   not cancelled.
6. Eye-rotation translation behaviour and fallback handling are defined.
7. Out Of Scope does not exclude mandatory implementation requirements.

## References

- `@game/src/Speech/LipSync/A2FLipSyncPlayer.cs`
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
- `@game/tests/speech/a2f_lipsync_player_test.tscn`