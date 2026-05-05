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

## Technical Requirements

1. An abstract `SpeechGenerator` class must be defined as a `Node` or `Node3D` subclass in `@game/src/Speech/Generation/SpeechGenerator.cs`.
2. An abstract method `Generate(string text, string? instruction = null)` must be defined as `async Task<byte[]>`, returning raw audio bytes. The `instruction` parameter is passed through the method signature for extensibility; backends that do not support a separate instruction field must silently ignore it rather than fail.
3. A Godot signal `SpeechGenerationCompleted(byte[] audio)` must be emitted when generation finishes successfully.
4. An exported `Enabled` property must control whether generation is permitted.
5. On generation failure, raw errors must be logged via `GD.PushError`, and a fallback action must be taken.
6. The concrete `OpenAISpeechGenerator` implementation must use the official OpenAI .NET SDK.
7. Configuration must be loaded from a file (default: `res://AlleyCat.cfg`) containing:
   - `Host`: full endpoint URL including scheme and path (e.g., `https://api.openai.com/v1`)
   - `ApiKey`: optional API key for authenticated backends
   - Additional API-supported properties (model, voice, etc.)
8. Runtime integration must specify:
   - Config file contract and API contract
   - Signal contract
   - Lifecycle (initialisation, generation, cleanup)

## In Scope

- Abstract `SpeechGenerator` class definition with `Enabled` property.
- Async abstract `Generate(string, string?)` method contract returning `byte[]`.
- Signal contracts for generation completion and failure.
- Error handling contract using `GD.PushError`.
- `OpenAISpeechGenerator` concrete implementation using OpenAI .NET SDK.
- Configuration contract from `AlleyCat.cfg`.
- Implementation under `@game/src/Speech/Generation/`.
- Integration tests under `@integration-tests/src/`.

## Out Of Scope

- Speech-to-text (STT) or transcription capabilities.
- Real-time streaming audio playback.
- Multiple simultaneous generation sessions.
- Local-only TTS without network connectivity.
- Non-OpenAI-compatible backend implementations beyond the provided `OpenAISpeechGenerator`.
- Audio preprocessing or custom voice synthesis beyond API parameters.

## Contract

### Abstract SpeechGenerator Contract

The `SpeechGenerator` class must define the following:

| Member | Type | Description |
|--------|------|-------------|
| `Enabled` | `bool` | Controls whether generation is permitted. Default: `true`. |
| `SpeechGenerationCompleted(byte[])` | Signal | Emitted with audio data on success. |
| `SpeechGenerationFailed(string)` | Signal | Emitted with error message on failure. |
| `Generate(string text, string? instruction)` | `async Task<byte[]>` | Generates speech audio from text. |

### Config File Contract

The `AlleyCat.cfg` file (or custom path) must contain `Host` and may contain optional authentication and TTS settings:

```ini
[TTS]
Host=https://api.openai.com/v1
ApiKey=sk-...
Model=tts-1
Voice=alloy
```

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Host` | string | Yes | Full endpoint URL including scheme and path (e.g., `https://api.openai.com/v1`) |
| `ApiKey` | string | No | API authentication key for backends that require one. When omitted, the implementation may use a dummy credential value if the OpenAI-compatible backend accepts unauthenticated requests. |
| `Model` | string | No | TTS model (default: `tts-1`) |
| `Voice` | string | No | Voice to use (default: `alloy`) |
| `Timeout` | int | No | Request timeout in seconds |

### API Contract

`OpenAISpeechGenerator` must:

- Use `OpenAI.Audio.AudioClient` with `OpenAIClientOptions` configured from the config.
- Use the SDK's native async speech generation path rather than wrapping the synchronous API in `Task.Run`.
- Accept the `instruction` parameter in the method signature but silently ignore it (do not pass it to the API) since OpenAI's TTS endpoint does not expose a separate instruction field.
- Return raw audio bytes (`byte[]`) from the API response.
- Propagate errors as exceptions caught by the error handling contract.

### Error Handling

On generation failure:

1. Raw exception message logged via `GD.PushError("Speech generation failed: " + ex.Message)`.
2. `SpeechGenerationFailed(string)` signal emitted with the error message.
3. Fallback to silent failure or cached audio if appropriate.

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
4. `OpenAISpeechGenerator` implementation uses the official OpenAI .NET SDK and loads configuration from `AlleyCat.cfg`.
5. Runtime integration boundaries (config, API, signals, lifecycle) are explicitly defined.
6. Implementation path `@game/src/Speech/Generation/` and test path `@integration-tests/src/` are specified.
7. The spec does not exclude any mandatory delivery contracts through `Out Of Scope`.

## References

### Implementation

- `@game/src/Speech/Generation/SpeechGenerator.cs` - Abstract SpeechGenerator class
- `@game/src/Speech/Generation/OpenAISpeechGenerator.cs` - OpenAI-compatible implementation
- `@game/AlleyCat.cfg` - Configuration file

### Related Specs

- [SPCH-001: Wav2Arkit Blendshape Player](../../speech/001-wav2arkit-blendshape-player/index.md)
- [SPCH-002: Audio2Face BlendShape Player](../../speech/002-audio2face-blendshape-player/index.md)
- [SPCH-003: Transcriber Component](../../speech/003-transcription/index.md)

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API
