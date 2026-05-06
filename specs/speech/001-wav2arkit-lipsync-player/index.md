---
id: SPCH-001
title: Wav2Arkit LipSync Player
---

# Wav2Arkit LipSync Player

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
3. Playback must be triggered manually by the user via the `LipSyncPlayer.Play(AudioStreamWav)` method.

## Technical Requirements

1. The prototype must run local Wav2Arkit ONNX inference and map output channels to ARKit-compatible blendshape names.
2. Input contract must enforce PCM 16-bit, 16 kHz, mono WAV via `AudioStreamWav`.
3. The player must expose `Play(AudioStreamWav speech)` for manual playback initiation.
4. Playback must remain audio-synchronised through `AudioStreamPlayer3D` and apply frame updates over time.
5. **Interruption contract**: If `Play` is called while a previous playback is active, the player must stop the current playback immediately and begin the new playback without delay.

The implementation is not required to cancel a synchronous inference in progress—only the active audio playback and frame application need to stop.
6. Validation runner contracts must verify initialisation, mapping presence, and progression metrics.

## Manual Playback Contract

The player does **not** auto-start playback during `_Ready()`. Instead, playback is initiated manually:

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

This ensures responsive manual control during validation and testing. The player is not required to interrupt a synchronous inference call in progress.

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

- Prototype playback pipeline initialisation and frame application through `LipSyncPlayer`.
- Mapping and applying inferred ARKit channels to available character mesh blendshape channels.
- Manual playback invocation via `LipSyncPlayer.Play(AudioStreamWav)`.
- Prototype validation scene and runner checks for initialisation, frame advance, and observable weight change.
- Validation-only execution path for fast local confidence checks.

## Out Of Scope

- Production runtime guarantees (latency budgets, stability guarantees, memory/performance tuning).
- Network/streaming, live microphone capture, and dialogue-system integration.
- Retargeting quality tuning, animation polish, and expressive-quality acceptance criteria.
- Full automated regression coverage beyond the mock-backed integration tests.

## Acceptance Criteria

1. The specification defines both user-visible prototype outcomes and technical implementation contracts.
2. Prototype scope is explicitly bounded as feasibility work rather than production runtime guarantees.
3. Audio format and playback synchronisation contracts required for deterministic prototype behaviour are defined.
4. **Manual playback contract** is explicitly defined: playback must be triggered via `LipSyncPlayer.Play(AudioStreamWav)`, not auto-started in `_Ready()`.
5. **Interruption contract** is explicitly defined: calling `Play` while playback is active must stop current playback and begin new playback immediately. The player is not required to cancel a synchronous inference in progress.
6. Validation workflow and criteria define reproducible checks for initialisation, mapping, playback initiation, and playback progression.

## Implementation References

- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
- `@game/tests/speech/wav2arkit_lipsync_player_test.tscn`
- `@game/tests/speech/lipsync_test_control.gd`
- `@game/models/wav2arkit_cpu/config.json`

## Validation Workflow

1. Run formatting verification:
   - `dotnet format --verify-no-changes AlleyCat.sln`
2. Run build verification:
   - `dotnet build AlleyCat.sln -warnaserror`
3. Run the manual-button validation script (tests button routing and Play() call flow only — not runtime facial motion):
   - `godot-mono --path game --script res://tests/speech/lipsync_manual_button_validation.gd`
4. Run the integration test with mock inference backend (tests playback contract, interruption contract, and frame application):
    - `dotnet run --project integration-tests/AlleyCat.IntegrationTests.csproj -- --test-class AlleyCat.IntegrationTests.Speech.LipSyncManualPlaybackIntegrationTests`

**Note:** Visual/runtime verification of the prototype scene is NOT required for this refactor. Mock-backed unit and integration tests provide sufficient coverage for acceptance.

## Validation Criteria

**Automated Verification:**
- `LipSyncPlayer` is consistently defined (build/format verifies compilation and repository consistency).
- Manual-button validation script confirms Play() routing works correctly (button-routing evidence only).
- Integration test `LipSyncManualPlaybackIntegrationTests` with mock inference backend verifies the shared playback contract:
  - Playback initiates correctly with audio stream input
  - Blendshape frames are applied to mapped mesh channels
  - Calling `Play` while playback is active restarts playback immediately (interruption contract verified)
  - Frame progression advances over time during playback

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
