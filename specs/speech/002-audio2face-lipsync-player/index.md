---
id: SPCH-002
title: Audio2Face LipSync Player
---

# Audio2Face LipSync Player

## Requirement

Generate lipsync and facial expression animation from speech audio using NVIDIA Audio2Face
inference served over HTTP. This implementation produces higher-quality ARKit blendshape output
than Wav2Arkit (SPCH-001), with additional support for emotion inference and dedicated eye
rotation data for accurate eye movements. It requires an NVIDIA GPU on the inference server.

## Goal

Drive character facial animation from speech audio using NVIDIA Audio2Face blendshape inference
served over HTTP, with post-processing that translates dedicated eye-rotation output into accurate
ARKit eye-look blendshape channels.

## User Requirements

1. Prototype playback must produce observable facial animation from supported speech audio using remote Audio2Face
   inference.
2. Eye movement output should be more accurate when dedicated eye-rotation translation is enabled.
3. Contributors must be able to validate server connectivity and playback behaviour with a repeatable workflow.
4. Playback must be triggered manually by the user via the `LipSyncPlayer.Play(AudioStreamWav)` method.

## Technical Requirements

1. The player must convert supported `AudioStreamWav` PCM input to the API payload format expected by Audio2Face.
2. Inference integration must use HTTP endpoint contracts (`/health`, `/blendshapes`) with configurable server URI.
3. Returned frames must be mapped into the base `LipSyncPlayer` playback pipeline with audio synchronisation.
4. The player must expose `Play(AudioStreamWav speech)` for manual playback initiation.
5. **Interruption contract**: If `Play` is called while a previous playback is active, the player must stop the current playback immediately and begin the new playback without delay.

The implementation is not required to cancel an in-flight HTTP request in progress—only the active audio playback and frame application need to stop.
6. Optional eye-rotation translation must define baseline subtraction, smoothing, directional mapping, and clamp rules.
7. Model/mode compatibility handling and health probing behaviour must be explicitly defined.

## Manual Playback Contract

The player does **not** auto-start inference during `_Ready()`. Instead, playback is initiated manually:

```csharp
// Example invocation via user interaction (e.g., button press)
var player = GetNode<LipSyncPlayer>("LipSyncPlayer");
player.Play(speechAudioStream);
```

When `Play(AudioStreamWav speech)` is called:

1. If `AudioStreamPlayer3D` is currently playing, stop it immediately.
2. Stop current blendshape frame application.
3. Begin inference with the provided audio stream.
4. Start audio playback and apply blendshape frames synchronously.

## Interruption Contract

If `Play` is called while a previous playback is still active:

- Current audio playback must stop immediately.
- Current blendshape frame application must stop.
- New inference and playback must begin without queuing or delay.

This ensures responsive manual control during validation and testing. The player is not required to cancel an in-flight HTTP request in progress.

## Prototype Status

This spec documents a **feasibility prototype**. It does not define production-ready runtime
guarantees.

## Overview

`A2FLipSyncPlayer` is a concrete subclass of `LipSyncPlayer` that replaces local ONNX
inference with a remote NVIDIA Audio2Face blendshape service. The player:

1. Accepts a Godot `AudioStreamWav` (16-bit PCM, 16 kHz, mono).
2. Converts the PCM data to `float32` little-endian bytes.
3. POSTs the raw audio payload to an Audio2Face API server.
4. Parses the JSON response into named ARKit blendshape frames.
5. Optionally translates dedicated eye-rotation tensors into ARKit `eyeLook*` blendshape
   channels.
6. Delegates frame-to-mesh application and audio-synchronised playback to the base
   `LipSyncPlayer`.

All inference state is remote. The player itself is stateless after initialisation — it calls the
API once during `Play()`, stores the resulting frames, and then plays them back synchronised to
the `AudioStreamPlayer3D` timeline.

## Architecture

```
AudioStreamWav (16-bit / 16 kHz / mono)
        │
        ▼
  PCM16 → float32 LE byte buffer
        │
        ▼
  HTTP POST /blendshapes
        │
        ▼
  Docker-hosted Audio2Face API Server (NVIDIA A2X SDK + TensorRT)
        │
        ▼
  JSON: { fps, blendshape_names, frames[], eye_rotation_frames[] }
        │
        ▼
  Eye rotation → blendshape translation (optional)
        │
        ▼
  LipSyncPlayer base class (frame storage + audio-synchronised playback)
        │
        ▼
  MeshInstance3D blendshape weight updates
```

## External Dependency: Audio2Face API Server

The player requires a running instance of the Audio2Face Docker API server, which wraps the
NVIDIA Audio2Face-3D SDK. Key facts about the server:

- The server exposes ARKit-compatible blendshape inference over HTTP so the game client does not
  need to link native CUDA/TensorRT libraries.
- It runs as a Docker container with GPU access (`--gpus all`), listening on a configurable port
  (default `8765`).
- Inside the container the server builds the Audio2X SDK from source, downloads models from
  Hugging Face, and converts them to TensorRT engines at startup.
- TensorRT engine generation happens at container start-up, not image build time, because
  `trtexec` requires an actual NVIDIA device.
- The server provides a `GET /health` endpoint that returns `{"status":"ok"}` and a
  `POST /blendshapes` endpoint that accepts raw `float32` PCM bytes and returns a JSON envelope
  containing named blendshape frames at 60 fps.
- The server supports multiple prepared models (`mark`, `claire`, `james` for regression; `v3`
  for diffusion) selectable per request via the `model` query parameter.
- Optional query parameters control inference mode, execution flags (skin/tongue/jaw/eyes), audio
  input strength, GPU solver usage, diffusion identity, emotion vector overrides, and emotion
  blending.
- See `../Audio2Face-3D-SDK/audio2face-api-server/README.md` for full API and deployment details.

## Model And Mode Handling

The Audio2Face server supports two inference modes — regression and diffusion — and not every
model is compatible with both. The player handles this transparently:

- Known model families are detected automatically. `mark`, `claire`, and `james` are regression
  models; `v3` is a diffusion model.
- When the user-selected mode does not match the selected model's family, the player
  auto-adjusts the mode to the correct one and emits a one-time warning (configurable).
- This behaviour can be disabled to allow the server to reject incompatible combinations.

## Eye Rotation Translation

When the `eyes` execution flag is active, the Audio2Face server returns a dedicated
`eye_rotation_frames` tensor alongside the standard blendshape frames. Each eye rotation frame
contains six floats representing per-eye horizontal/vertical rotation deltas (in radians).

The player can translate these rotation values into the ARKit `eyeLook(In|Out|Up|Down)(Left|Right)`
blendshape channels. This produces more accurate and symmetrical eye movements than the
PCA-derived eye-look weights the solver embeds in the standard blendshape output.

The translation pipeline:

1. **Baseline subtraction** — The SDK eye rotation values include constant offsets from
   `AnimatorEyesParams` and saccade seeds. The player computes the per-component mean across all
   frames and subtracts it, producing delta-from-neutral rotation suitable for blendshape
   mapping.
2. **Temporal smoothing** — An exponential moving average smooths per-frame jitter. A lower
   smoothing factor produces smoother motion at the cost of latency.
3. **Sign inversion** — Optional per-axis inversion to accommodate different coordinate
   conventions in character rigs.
4. **Scale and clamp** — The signed rotation value is scaled and written into directional
   blendshape pairs (e.g. positive horizontal drives `eyeLookOutRight` / `eyeLookInLeft`,
   negative drives the opposite channels). Output is clamped to `[0, 1]`.

If eye rotation frames are absent (e.g. the `eyes` execution flag was not included) or the
required ARKit eye-look channels are not present in the mesh, the translation is skipped with a
one-time warning.

## Health Probing

During initialisation the player can optionally probe the API server's `GET /health` endpoint
with configurable retries and delay. This is useful when the game and the Docker container start
simultaneously — the player will wait for the server to become ready before attempting inference.

The health URI is derived from the configured blendshape endpoint URL by stripping the
`/blendshapes` path segment and appending `/health`.

## Audio Format Contract

The player enforces the same audio format contract as the API server at initialisation time:

- Format: 16-bit PCM (`AudioStreamWav.FormatEnum.Format16Bits`)
- Sample rate: 16 000 Hz
- Channels: mono

Violations produce explicit initialisation errors.

## In Scope

- HTTP-based Audio2Face inference integration as a `LipSyncPlayer` backend.
- Server health probing with retry during initialisation.
- Model/mode auto-adjustment for known Audio2Face model families.
- Eye rotation tensor → ARKit eye-look blendshape translation with baseline subtraction,
  temporal smoothing, and directional pair mapping.
- Manual playback invocation via `LipSyncPlayer.Play(AudioStreamWav)`.
- Audio-synchronised playback via the base `LipSyncPlayer`.

## Out Of Scope

- Production runtime guarantees (latency budgets, error recovery, streaming inference).
- Live microphone capture or streaming audio pipelines.
- Dialogue system integration or runtime model switching.
- Retargeting quality tuning, animation polish, and expressive-quality acceptance criteria.
- Docker container lifecycle management or orchestration.
- Automated regression coverage beyond the mock-backed integration tests.

## Acceptance Criteria

1. The specification defines both user-visible prototype outcomes and technical implementation contracts.
2. Prototype scope is explicitly bounded as feasibility work rather than production runtime guarantees.
3. HTTP integration, audio-format contracts, and playback synchronisation obligations are explicitly defined.
4. **Manual playback contract** is explicitly defined: playback must be triggered via `LipSyncPlayer.Play(AudioStreamWav)`, not auto-started in `_Ready()`.
5. **Interruption contract** is explicitly defined: calling `Play` while playback is active must stop current playback and begin new playback immediately. The player is not required to cancel an in-flight HTTP request.
6. Eye-rotation translation behaviour and fallback handling are explicitly defined.
7. Validation workflow defines reproducible checks for server readiness, frame generation, mapping, playback
   initiation, and playback progression.

## Implementation References

- `@game/src/Speech/LipSync/A2FLipSyncPlayer.cs`
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
- `@game/tests/speech/a2f_lipsync_player_test.tscn`

## Validation Workflow

1. Run formatting verification:
   - `dotnet format --verify-no-changes AlleyCat.sln`
2. Run build verification:
   - `dotnet build AlleyCat.sln -warnaserror`
3. Run the manual-button validation script (tests button routing and Play() call flow only — not runtime facial motion):
   - `godot-mono --path game --script res://tests/speech/lipsync_manual_button_validation.gd`
4. Run the integration test with mock inference backend (tests playback contract, interruption contract, and frame application):
    - `dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- --test-class AlleyCat.IntegrationTests.Speech.LipSyncManualPlaybackIntegrationTests`

**Note:** Visual/runtime verification of the prototype scene is NOT required for this refactor. Live Docker/server verification is NOT required. Mock-backed unit and integration tests provide sufficient coverage for acceptance.

## Validation Criteria

**Automated Verification:**
- `A2FLipSyncPlayer` is consistently defined (build verifies compilation and repository consistency).
- Manual-button validation script confirms Play() routing works correctly (button-routing evidence only).
- Integration test `LipSyncManualPlaybackIntegrationTests` with mock inference backend verifies the shared playback contract:
  - Playback initiates correctly with audio stream input
  - Blendshape frames are applied to mapped mesh channels
  - Calling `Play` while playback is active restarts playback immediately (interruption contract verified)
  - Frame progression advances over time during playback

## Known Limitations

- Inference is a single blocking HTTP call during `Play()`. Long audio clips will cause a
  noticeable pause.
- The player does not cache inference results across scene reloads.
- The Docker server must be running before the game starts (or within the health-probe retry
  window).
- Eye rotation translation accuracy depends on scale and smoothing tuning per character rig.

## Follow-Up Notes

- Evaluate asynchronous or chunked inference to reduce startup latency for long clips.
- Add dedicated automated tests for the eye rotation translation pipeline independent of the
   live server.
- Consider caching or pre-computing inference results for known dialogue clips.
- Define production constraints (error recovery, reconnection, observability) in a follow-up
  speech spec before feature hardening.

## References

- @specs/index.md
- @specs/speech/001-wav2arkit-lipsync-player/index.md
