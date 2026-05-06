---
id: SPCH-004
title: Speech Generator Component
---

# Speech Generator Component

## Requirement

Provide an abstract `SpeechGenerator` component that converts dialog text to audio using an abstract async method and emits a Godot signal on completion, with error handling and an `Enabled` property. Deliver one concrete implementation `OpenAISpeechGenerator` using the OpenAI .NET SDK with configuration from a file.

## Goal

Enable text-to-speech capabilities in the AlleyCat VR experience, with a concrete OpenAI-compatible implementation that can be extended to other TTS backends.

## User Requirements

1. Players must hear AI-generated speech output when dialog is triggered in the game.
2. The system must accept an optional instruction or prompt parameter for future backend extensibility.
3. Speech generation failures must be logged and handled gracefully without crashing.
4. The system must support OpenAI-compatible API endpoints for TTS services.
5. An `Enabled` property must allow TTS to be toggled on/off at runtime.
6. A `TargetSampleRate` property must allow configuration of the output sample rate for generated audio, enabling normalisation to downstream consumer requirements (e.g., 16000 Hz for lip-sync compatibility).

## Technical Requirements

1. An abstract `SpeechGenerator` class must be defined as a `Node` or `Node3D` subclass in `@game/src/Speech/Generation/SpeechGenerator.cs`.
2. An abstract method `Generate(string text, string? instruction = null)` must be defined as `async Task<byte[]>`, returning raw audio bytes. The `instruction` parameter is passed through the method signature for extensibility; backends that do not support a separate instruction field must silently ignore it rather than fail.
3. A Godot signal `SpeechGenerationCompleted(byte[] audio)` must be emitted when generation finishes successfully.
4. An exported `Enabled` property must control whether generation is permitted.
5. An exported `TargetSampleRate` property must allow configuration of the output sample rate (in Hz). When set to a value greater than 0, the generated audio must be resampled to the target rate before being emitted via the completion signal. Default: `0` (no resampling). Recommended value for lip-sync compatibility: `16000`.
6. On generation failure, raw errors must be logged via `GD.PushError`, and a fallback action must be taken.
7. On resampling failure (when `TargetSampleRate` > 0 and the operation cannot be completed), raw errors must be logged via `GD.PushError`, and the failure signal must be emitted instead of the completion signal.
8. The concrete `OpenAISpeechGenerator` implementation must use the official OpenAI .NET SDK.
9. Configuration must be loaded from the merged configuration API (see [CORE-002: Configuration API](../../002-configuration-api/index.md)), which combines project defaults from `res://AlleyCat.cfg` with user overrides from `user://AlleyCat.cfg`. The [TTS] section must contain:
   - `Host`: full endpoint URL including scheme and path (e.g., `https://api.openai.com/v1`)
   - `ApiKey`: optional API key for authenticated backends
   - Additional API-supported properties (model, voice, etc.)
10. Runtime integration must specify:
   - Config file contract and API contract
   - Signal contract
   - Lifecycle (initialisation, generation, cleanup)

## In Scope

- Abstract `SpeechGenerator` class definition with `Enabled` property.
- Async abstract `Generate(string, string?)` method contract returning `byte[]`.
- Signal contracts for generation completion and failure.
- Error handling contract using `GD.PushError`.
- `OpenAISpeechGenerator` concrete implementation using OpenAI .NET SDK.
- Configuration contract from merged configuration API (see [CORE-002: Configuration API](../../002-configuration-api/index.md)).
- Implementation under `@game/src/Speech/Generation/`.
- Integration tests under `@integration-tests/src/`.
- Audio resampling via `TargetSampleRate` property on `SpeechGenerator` node (property-level, not config-file).

## Out Of Scope

- Speech-to-text (STT) or transcription capabilities.
- Real-time streaming audio playback.
- Multiple simultaneous generation sessions.
- Local-only TTS without network connectivity.
- Non-OpenAI-compatible backend implementations beyond the provided `OpenAISpeechGenerator`.
- Audio preprocessing or custom voice synthesis beyond API parameters.
- Compressed audio format transcoding (e.g., MP3, Opus) — only WAV PCM resampling is in scope.

## Contract

### Abstract SpeechGenerator Contract

The `SpeechGenerator` class must define the following:

| Member | Type | Description |
|--------|------|-------------|
| `Enabled` | `bool` | Controls whether generation is permitted. Default: `true`. |
| `TargetSampleRate` | `int` | Target sample rate (Hz) for resampling generated audio. When > 0, audio is resampled to this rate before emission. Default: `0` (no resampling). Recommended for lip-sync: `16000`. |
| `SpeechGenerationCompleted(byte[])` | Signal | Emitted with audio data on success. |
| `SpeechGenerationFailed(string)` | Signal | Emitted with error message on failure. |
| `Generate(string text, string? instruction)` | `async Task<byte[]>` | Generates speech audio from text. |

### Config File Contract

Configuration is loaded through the merged configuration API (see [CORE-002: Configuration API](../../002-configuration-api/index.md)). The merged [TTS] section must contain `Host` and may contain optional authentication and TTS settings:

**Base file (`res://AlleyCat.cfg`):**
```ini
[TTS]
Host=https://api.openai.com/v1
ApiKey=
Model=tts-1
Voice=alloy
Format=wav
```

**User override file (`user://AlleyCat.cfg`) - optional:**
```ini
[TTS]
ApiKey=sk-...
```

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Host` | string | Yes | Full endpoint URL including scheme and path (e.g., `https://api.openai.com/v1`) |
| `ApiKey` | string | No | API authentication key for backends that require one. When omitted, the implementation may use a dummy credential value if the OpenAI-compatible backend accepts unauthenticated requests. |
| `Model` | string | No | TTS model (default: `tts-1`) |
| `Voice` | string | No | Voice to use (default: `alloy`) |
| `Format` | string | No | Audio output format (default: `wav`). **Must be `wav` to align with SPCH-005's audio compatibility contract** — the `LipSyncPlayer.Play(AudioStreamWav)` requires PCM 16-bit, 16 kHz, mono WAV format. |
| `Timeout` | int | No | Request timeout in seconds |

**Note:** Audio resampling is configured via the `TargetSampleRate` property on the `SpeechGenerator` node directly (see Contract table), not via the config file.

### API Contract

`OpenAISpeechGenerator` must:

- Use `OpenAI.Audio.AudioClient` with `OpenAIClientOptions` configured from the config.
- Use the SDK's native async speech generation path rather than wrapping the synchronous API in `Task.Run`.
- Accept the `instruction` parameter in the method signature but silently ignore it (do not pass it to the API) since OpenAI's TTS endpoint does not expose a separate instruction field.
- Return raw audio bytes (`byte[]`) from the API response.
- Propagate errors as exceptions caught by the error handling contract.
- Use the default `Format=wav` configuration to align with SPCH-005's audio compatibility contract — the `LipSyncPlayer.Play(AudioStreamWav)` requires PCM 16-bit, 16 kHz, mono WAV format.

### Error Handling

On generation failure:

1. Raw exception message logged via `GD.PushError("Speech generation failed: " + ex.Message)`.
2. `SpeechGenerationFailed(string)` signal emitted with the error message.
3. Fallback to silent failure or cached audio if appropriate.

On resampling failure (when `TargetSampleRate` > 0 and resampling cannot be applied):

1. Log the error via `GD.PushError("Audio resampling failed: " + details)`.
2. Emit `SpeechGenerationFailed("Audio resampling failed")` signal.
3. Do not emit the completion signal with potentially corrupted audio.

### Signal Contract

| Signal | Payload | Description |
|--------|---------|-------------|
| `SpeechGenerationCompleted(byte[] audio)` | Raw audio bytes | Emitted when generation succeeds |
| `SpeechGenerationFailed(string error)` | Error message | Emitted when generation fails |

### Lifecycle

1. **Initialisation**: Load config, initialise SDK client.
2. **Generation**: Call `Generate(text, instruction)` → async API request → audio bytes.
3. **Completion**: On success → emit `SpeechGenerationCompleted` with audio. On failure → emit `SpeechGenerationFailed` + error log.
4. **Cleanup**: Release resources.

## Acceptance Criteria

1. The spec defines both user-visible speech generation outcomes and technical implementation contracts.
2. The abstract `SpeechGenerator` class defines `Enabled` property, generation method, and signal contracts explicitly.
3. Error handling contract uses `GD.PushError` for raw errors.
4. `OpenAISpeechGenerator` implementation uses the official OpenAI .NET SDK and loads configuration from the merged configuration API (see [CORE-002: Configuration API](../../002-configuration-api/index.md)).
5. Runtime integration boundaries (config, API, signals, lifecycle) are explicitly defined.
6. Implementation path `@game/src/Speech/Generation/` and test path `@integration-tests/src/` are specified.
7. The spec does not exclude any mandatory delivery contracts through `Out Of Scope`.
8. The resample feature is explicitly defined with `TargetSampleRate` property contract, resampling applies before the completion signal, and failure handling is defined separately from generation failure handling.

## References

### Implementation

- `@game/src/Speech/Generation/SpeechGenerator.cs` - Abstract SpeechGenerator class
- `@game/src/Speech/Generation/OpenAISpeechGenerator.cs` - OpenAI-compatible implementation
- `@game/AlleyCat.cfg` - Configuration file

### Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)
- [SPCH-005: Speech Voice Component](../../speech/005-voice/index.md)
- [CORE-002: Configuration API](../../002-configuration-api/index.md)

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API
