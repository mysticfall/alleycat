---
id: SPCH-005
title: Speech Voice Component
---

# Speech Voice Component

## Requirement

Provide an abstract `Voice` component that converts dialogue text to synchronised audio
playback via a coordinated `SpeechGenerator` and `LipSyncPlayer` pipeline. Deliver one
concrete implementation `AIVoice` that orchestrates speech generation and lip-synchronised
playback as an integrated unit.

## Goal

Enable AI-driven character speech with lip synchronisation in the AlleyCat VR experience
using a coordinated pipeline that ensures audio and blendshape playback remain
synchronised.

## User Requirements

1. Players must hear AI-generated speech output when dialog is triggered in the game.
2. Lip-sync animation must play in synchronisation with the audio playback.
3. The system must allow runtime toggling of voice output via an `Enabled` property.
4. Speech failures must be logged and handled gracefully without crashing or
   desynchronising playback.
5. A manual test scene must allow testers to type arbitrary dialogue and observe character
   speech output.

## Technical Requirements

1. An abstract `Voice` class must be defined as a `Node` or `Node3D` subclass in
   `@game/src/Speech/Voice/Voice.cs`.
2. An abstract method `Speak(string dialogue)` must be defined with the exact signature
   `void Speak(string dialogue)`.
3. An exported `Enabled` property must control whether speech is permitted.
4. The concrete `AIVoice` implementation must:
   - Accept a configured `SpeechGenerator` node via export for audio generation.
   - Accept a configured `LipSyncPlayer` node via export for synchronised playback.
   - Generate audio via `SpeechGenerator.Generate()`.
   - Convert generated `byte[]` to an `AudioStreamWav` compatible with
     `LipSyncPlayer.Play(AudioStreamWav)`.
   - Invoke `LipSyncPlayer.Play(AudioStreamWav)` as the single point of playback
     initiation — **not** a separate `AudioStreamPlayer3D` control.
5. **Audio compatibility contract**: Generated audio must be convertible to PCM
   16-bit, 16 kHz, mono WAV format consumable by `LipSyncPlayer.Play(AudioStreamWav)`.
   If conversion fails or the backend output is incompatible, the implementation
   must fail gracefully (log error, emit signal) rather than attempt desynchronised
   playback.
   - **Normalisation responsibility**: The `SpeechGenerator` is responsible for
     resampling audio to the target sample rate if `TargetSampleRate` is configured
     (see SPCH-004). The `AIVoice` must not independently resample audio; it should
     receive audio already normalised by `SpeechGenerator` (or use the original
     generated audio if no resampling was configured).
6. **Synchronisation contract**: All playback timing is governed by `LipSyncPlayer.Play()`.
   The `AIVoice` must not independently control audio timing — it must only provide
   the audio stream to `LipSyncPlayer` and let it manage the playback boundary.
7. A Godot signal `SpeechFailed(string error)` must be emitted when generation or
   conversion fails. (Note: playback completion notification is out of scope — it
   depends on LipSyncPlayer exposing such a signal, which is not currently defined.)
8. A manual test scene must be created using `@game/assets/characters/reference/
   female/reference_female.tscn` with a simple UI for typing dialogue.

## In Scope

- Abstract `Voice` class definition with `Enabled` property and `Speak(string dialogue)`
  method.
- Concrete `AIVoice` implementation coordinating `SpeechGenerator` and `LipSyncPlayer`.
- Audio compatibility contract for `AudioStreamWav` (PCM 16-bit, 16 kHz, mono).
- Synchronisation contract via `LipSyncPlayer.Play()` initiation path.
- Signal contract for generation/conversion failures.
- Error handling contract using `GD.PushError` for raw errors.
- Implementation under `@game/src/Speech/Voice/`.
- Manual test scene under `@game/tests/speech/voice_test.tscn`.
- Unit tests using mocks/fakes for `SpeechGenerator` and `LipSyncPlayer` dependencies.

## Out Of Scope

- Visual verification or runtime XR testing (requires backend access).
- Live microphone capture or real-time streaming input.
- Multiple simultaneous dialogue sessions.
- Non-AIVoice implementations beyond the provided concrete class.
- Audio preprocessing or post-processing beyond conversion to WAV format.
- Character animation beyond lip-sync via `LipSyncPlayer`.
- Playback completion notification (depends on LipSyncPlayer exposing a completion
  signal, which is not currently defined).

## Abstract Voice Contract

| Member | Type | Description |
|--------|------|-------------|
| `Enabled` | `bool` | Controls whether speech is permitted. Default: `true`. |
| `SpeechFailed(string error)` | Signal | Emitted when generation or conversion fails. |
| `Speak(string dialogue)` | `void` | Initiates speech. Exact signature: `void Speak(string dialogue)`. |

## AIVoice Implementation Contract

| Member | Type | Description |
|--------|------|-------------|
| `SpeechGenerator` | `SpeechGenerator (Node)` | Export - the speech generation component. |
| `LipSyncPlayer` | `LipSyncPlayer (Node)` | Export - the lip-sync playback component. |

### Behaviour

1. **Validation**: If `Enabled` is `false`, `Speak()` returns early without error.
2. **Generation**: Call `SpeechGenerator.Generate(dialogue)` to obtain `byte[]` audio data.
3. **Conversion**: Convert `byte[]` to `AudioStreamWav` with PCM 16-bit, 16 kHz, mono
   format. If conversion fails, log error via `GD.PushError`, emit `SpeechFailed`,
   and return.
4. **Playback**: Invoke `LipSyncPlayer.Play(audioStream)` — this starts both audio
   playback and lip-sync frame application in a single coordinated call. Do **not**
   separately start or control `AudioStreamPlayer3D`.
5. **Error handling**: On any failure (generation, conversion), log via `GD.PushError`
   and emit `SpeechFailed` with relevant message.
6. **Completion notification**: The spec does not require playback completion
   notification. If `LipSyncPlayer` later exposes a playback-completed signal,
   `AIVoice` could forward it; but this is not a current requirement.

### Audio Compatibility Contract

The `AIVoice` implementation must ensure audio passed to `LipSyncPlayer.Play(AudioStreamWav)`
meets the LipSyncPlayer's requirements:

- Format: PCM 16-bit, 16 kHz, mono WAV
- If `SpeechGenerator` returns audio in a different format, `AIVoice` must convert it
  before passing to `LipSyncPlayer`.
- If conversion is not possible (e.g., unsupported encoding, malformed data), the
  implementation must:
  1. Log the error via `GD.PushError("Audio conversion failed: " + details)`
  2. Emit `SpeechFailed("Audio format incompatible")`
  3. **Not** attempt to play the incompatible audio

### Synchronisation Contract

- `AIVoice` must **not** independently control an `AudioStreamPlayer3D` node.
- All timing is derived from `LipSyncPlayer.Play()` — audio playback and blendshape updates
  are synchronised internally by `LipSyncPlayer`.
- If `LipSyncPlayer` is not configured or is `null`, `Speak()` must fail gracefully.

### Lifecycle

1. **Initialisation**: Load dependencies (`SpeechGenerator`, `LipSyncPlayer`) via exports.
2. **Speak(dialogue)**: Validate `Enabled` → Generate audio → Convert to WAV → Invoke
   `LipSyncPlayer.Play()`.
3. **Error**: Any failure in generation or conversion → Log + Emit `SpeechFailed`.

## Acceptance Criteria

1. The spec defines both user-visible speech output with lip-sync and technical
   implementation contracts.
2. The abstract `Voice` class defines `Enabled` property and `Speak(string dialogue)`
   method with exact signature `void Speak(string dialogue)`.
3. Audio compatibility contract is explicitly defined: generated audio must be convertible to
   PCM 16-bit, 16 kHz, mono WAV for `LipSyncPlayer.Play(AudioStreamWav)`.
4. **Synchronisation contract is explicitly defined**: all playback timing is governed by
   `LipSyncPlayer.Play()` — `AIVoice` must not independently control audio timing.
5. Error handling contract uses `GD.PushError` for raw errors and emits `SpeechFailed`
   signal.
6. Manual test scene using `@game/assets/characters/reference/female/
   reference_female.tscn` is specified.
7. Visual verification/testing is explicitly out of scope (requires backend access).
8. Implementation path `@game/src/Speech/Voice/` and test path `@game/tests/speech/` are
   specified.
9. The spec does not exclude any mandatory delivery contracts through `Out Of Scope`.
10. Playback completion notification is explicitly out of scope (depends on LipSyncPlayer
    changes not currently defined).
11. **Normalisation responsibility is explicitly defined**: `AIVoice` must not
    independently resample audio; it receives audio already normalised by `SpeechGenerator`
    via the `TargetSampleRate` configuration (see SPCH-004).

## Implementation References

- `@game/src/Speech/Voice/Voice.cs` - Abstract Voice class
- `@game/src/Speech/Voice/AIVoice.cs` - AIVoice implementation
- `@game/tests/speech/voice_test.tscn` - Manual test scene

## Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../002-audio2face-lipsync-player/index.md)
- [SPCH-004: Speech Generator Component](../004-speech-generation/index.md)
- [CORE-002: Configuration API](../../002-configuration-api/index.md)