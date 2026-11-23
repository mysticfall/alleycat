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
4. Callers can cut off active playback, halting both audio and lip-sync facial
   animation immediately.
5. Callers can observe when audible playback has finished.

## Technical Requirements

1. Run local Wav2Arkit ONNX inference and map output channels to ARKit-compatible
   blendshape names.
2. Input contract: input must be PCM 16-bit, mono `AudioStreamWav`, at any sample
   rate. The shared `LipSyncPlayer` base validates the format in the preparation
   path (`PreparePlaybackAsync`, off the audible latency path) and normalises a
   resampled inference copy to the backend's declared `BackendSampleRate` — for
   this backend, config `Preprocessing.SampleRate` under the strict 16000 Hz
   prototype guard — while the original stream is what plays audibly. A format
   or rate problem therefore fails lip-sync only (surfaced as `PlaybackError`);
   generation succeeds and the audible stream keeps the generator's original
   quality.
3. Expose `Play(AudioStreamWav speech)` for manual playback initiation.
4. Synchronise playback via `AudioStreamPlayer3D` and apply frame updates over time.
5. **Interruption contract**: If `Play` is called while playback is active, stop
   current audio and frame application immediately before starting new playback.
   The player is not required to cancel a synchronous inference in progress.
6. Validation runner verifies initialisation, mapping presence, and frame progression. It waits for initialisation to
   settle, because the shared model load completes asynchronously under TR9.
7. `LipSyncPlayer` must raise a playback-completed notification (typed C# event or
   Godot signal) when audible playback finishes, observed through its existing
   `IsAudioPlaying` polling in `_Process`.
8. `LipSyncPlayer` must expose a stop/cut capability that halts both audio playback
   and lip-sync frame application immediately, for callers that need to stop
   playback explicitly. In-flight synchronous inference need not be cancelled.
9. The Wav2Arkit inference session is a shared backend under
   [CORE-011](../../core/011-shared-model-services/index.md), keyed by the globalised model path. Each player routes
   inference through a per-character handle and keeps its own config, channel mapping and playback state; it does not
   construct a session. Shared inference already in flight is never interrupted; TR5 and TR8 apply unchanged.
   - **Load timing:** `_Ready`/initialisation requests the shared backend without blocking the scene thread.
   - **Initialised state:** `IsInitialised` becomes true only after config and mapping validation and the shared model
     load have both completed.
   - **Failure surface:** a load failure, including a cached failure returned by the registry, sets
     `InitialisationError` asynchronously and leaves `IsInitialised` false, exactly as a synchronous initialisation
     failure does.

## In Scope

- Prototype playback pipeline initialisation and frame application through
  `LipSyncPlayer`.
- Mapping inferred ARKit channels to available character mesh blendshape channels.
- Manual playback invocation via `LipSyncPlayer.Play(AudioStreamWav)`.
- Shared `LipSyncPlayer` playback-completed notification and stop/cut capability.
- Inference through a shared backend handle under CORE-011.
- Prototype validation scene and runner for initialisation, frame advance, and
  observable weight change.

## Out Of Scope

- Production runtime guarantees including latency budgets, stability guarantees,
  memory/performance tuning, and error handling observability beyond the existing `InitialisationError` and
  `PlaybackError` surfaces, which TR9 preserves. Model memory sharing is governed by
  [CORE-011](../../core/011-shared-model-services/index.md); tuning remains out of scope.
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
   playback initiation, and playback progression, awaiting asynchronous initialisation rather than assuming
   `IsInitialised` immediately after `_Ready`.
6. Technical requirement coverage: all nine requirements map to acceptance criteria.
7. Playback-completed notification contract is defined: raised when audible playback
   finishes, detected via `IsAudioPlaying` polling.
8. Stop/cut contract is defined: halts audio and lip-sync frame application
   immediately without cancelling in-progress inference.
9. Several players share one loaded Wav2Arkit session while keeping per-character mapping and playback state, verified
   under CORE-011-TAC-002 and CORE-011-TAC-007. Tests show that `_Ready` does not block on the model load, that
   `IsInitialised` stays false until validation and the load both complete, and that an injected or cached load failure
   sets `InitialisationError` and leaves `IsInitialised` false (Technical Requirement 9).

## References

- @specs/index.md
- [CORE-011: Shared Model Services](../../core/011-shared-model-services/index.md)
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
