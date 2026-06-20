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
playback with a repeatable workflow when they opt in to startup probing.

## User Requirements

1. Playback produces observable facial animation from supported speech audio
   via remote Audio2Face inference.
2. Eye movement output is more accurate when eye-rotation translation is
   enabled.
3. Default development and test startup remains non-blocking when the
   Audio2Face backend is not running.
4. Startup must not emit a connection-refused initialisation error by default
   when the backend is absent.
5. Contributors can opt in to initialisation-time connectivity validation and
   playback checks with a repeatable workflow.
6. Playback is triggered manually via `LipSyncPlayer.Play(AudioStreamWav)`
   method.

## Technical Requirements

1. Player converts supported `AudioStreamWav` PCM input (16-bit, 16 kHz, mono)
   to the Audio2Face API payload format.
2. Inference uses the `/blendshapes` HTTP endpoint with configurable server URI.
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
9. `ProbeHealthOnInitialise` is an exported setting and defaults to `false`.
10. When `ProbeHealthOnInitialise` is `false`, initialisation must not call
    `/health`, block on backend availability, or log a connection-refused error
    because the backend is not running.
11. When `ProbeHealthOnInitialise` is `true`, initialisation performs `/health`
    probing and preserves the existing retry and failure semantics.
12. Inference endpoint behaviour is unchanged by startup health probing.

## In Scope

- HTTP integration with remote Audio2Face inference service.
- ARKit blendshape output with eye-rotation translation.
- Configurable server URI for inference and optional health probing.
- Optional `/health` probing at initialisation, disabled by default.
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
7. Default startup succeeds without blocking or logging a connection-refused
   initialisation error when the Audio2Face backend is not running.
8. Health probing is opt-in through exported `ProbeHealthOnInitialise`, which
   defaults to `false`.
9. Opted-in health probing uses the existing `/health` retry and failure
   semantics.
10. Startup health-probe configuration does not change `/blendshapes` inference
    endpoint behaviour.
11. Out Of Scope does not exclude mandatory implementation requirements.

## References

- `@game/src/Speech/LipSync/A2FLipSyncPlayer.cs`
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
- `@game/tests/speech/a2f_lipsync_player_test.tscn`
