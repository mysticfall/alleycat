---
id: SPCH-003
title: Transcriber Component
---

# Transcriber Component

## Requirement

Provide an abstract `Transcriber` component that records microphone audio via Godot's XR input system, triggers transcription through an abstract async method, emits a Godot signal on completion, and handles errors appropriately. Deliver one concrete implementation `OpenAITranscriber` using the OpenAI .NET SDK with configuration from a file.

## Goal

Enable voice input capture and transcription capabilities in the AlleyCat VR experience, with a concrete OpenAI-compatible implementation that can be extended to other STT backends.

## User Requirements

1. Players must be able to initiate voice recording using a configurable XR input button (default: left trigger).
2. Recording must automatically stop when the input button is released or when a maximum duration is reached.
3. Transcription results must be surfaced to the player via a UI notification upon completion.
4. Transcription failures must be communicated to the player through a user-friendly error message.
5. The system must support OpenAI-compatible API endpoints for transcription services.

## Technical Requirements

1. An abstract `Transcriber` class must be defined as a `Node` or `Node3D` subclass in `@game/src/Speech/Transcription/Transcriber.cs`.
2. Recording initiation must be bound to a configurable XR action, defaulting to the left controller trigger input.
3. Audio capture must use Godot's microphone recording API and produce an `AudioStreamWav` for transcription.
4. An abstract method `Transcribe(AudioStreamWav audio)` must be defined as `async Task<string>`.
5. A Godot signal `TranscriptionCompleted(string text)` must be emitted when transcription finishes successfully.
6. On transcription failure, raw errors must be logged via `GD.PushError`, and a user-friendly message must be displayed via `@game/src/UI/NotificationUIExtensions.cs`.
7. The concrete `OpenAITranscriber` implementation must use the official OpenAI .NET SDK.
8. Configuration must be loaded from a file (default: `res://AlleyCat.cfg`) containing:
   - `Host`: full endpoint URL including scheme and path (e.g., `https://api.openai.com/v1`)
   - `ApiKey`: optional API key for authenticated backends
   - Additional API-supported properties (model, language, etc.)
9. Runtime integration must specify:
   - XR input binding and microphone recording prerequisites
   - Config file contract and API contract
   - Notification/error handling flow
   - Signal contract
   - Lifecycle (initialisation, recording, transcription, cleanup)

## In Scope

- Abstract `Transcriber` class definition with XR input binding and microphone recording.
- Async abstract `Transcribe(AudioStreamWav)` method contract.
- Signal contract for transcription completion.
- Error handling contract using `GD.PushError` and `NotificationUIExtensions`.
- `OpenAITranscriber` concrete implementation using OpenAI .NET SDK.
- Configuration contract from `AlleyCat.cfg`.
- Implementation under `@game/src/Speech/Transcription/`.
- Integration tests under `@integration-tests/src/`.

## Out Of Scope

- Text-to-speech (TTS) capabilities.
- Real-time streaming transcription.
- Multiple simultaneous recording sessions.
- Local-only transcription without network connectivity.
- Non-OpenAI-compatible backend implementations beyond the provided `OpenAITranscriber`.
- Audio preprocessing or custom voice activity detection beyond basic duration limits.

## Contract

### Abstract Transcriber Contract

The `Transcriber` class must define the following:

| Member | Type | Description |
|--------|------|-------------|
| `RecordButton` | `String` | XR action name for record trigger. Default: `trigger_click` on left controller. |
| `MaxRecordingDuration` | `double` | Maximum recording length in seconds before auto-stop. |
| `TranscriptionCompleted(string)` | Signal | Emitted with transcribed text on success. |
| `TranscriptionFailed(string)` | Signal | Emitted with error message on failure. |
| `StartRecording()` | `void` | Begins microphone capture. |
| `StopRecording()` | `void` | Stops capture and initiates transcription. |
| `Transcribe(AudioStreamWav)` | `async Task<string>` | Abstract method for transcription. |

### XR Input Binding

- Default binding: Left controller trigger (`左手` / `trigger_click`).
- Configurable via exported `RecordButton` property.
- Recording starts on button press (value > 0.5) and stops on release or timeout.

### Microphone Recording Prerequisites

- Godot microphone subsystem must be initialised.
- `AudioStreamMicrophone` must be configured and started.
- Output must be convertible to `AudioStreamWav` for the transcription method.

### Config File Contract

The `AlleyCat.cfg` file (or custom path) must contain `Host` and may contain optional authentication and transcription settings:

```ini
[STT]
Host=https://api.openai.com/v1
ApiKey=sk-...
Model=whisper-1
```

| Property | Type | Required | Description |
|----------|------|----------|-------------|
| `Host` | string | Yes | Full endpoint URL including scheme and path (e.g., `https://api.openai.com/v1`) |
| `ApiKey` | string | No | API authentication key for backends that require one. When omitted, the implementation may use a dummy credential value if the OpenAI-compatible backend accepts unauthenticated requests. |
| `Model` | string | No | Transcription model (default: `whisper-1`) |
| `Language` | string | No | Optional language code |
| `Timeout` | int | No | Request timeout in seconds |

### API Contract

`OpenAITranscriber` must:

- Use `OpenAI.Audio.AudioClient` with `OpenAIClientOptions` configured from the config.
- Use the SDK's native async transcription path (`TranscribeAudioAsync`) rather than wrapping the synchronous API in `Task.Run`.
- Accept `AudioStreamWav` input and convert to the format expected by the API.
- Accept configuration where `ApiKey` is omitted for compatible no-auth backends, using a dummy credential only when the SDK requires a non-empty key value.
- Return the transcribed text string from the API response.
- Propagate errors as exceptions caught by the error handling contract.

### Notification/Error Handling

On transcription failure:

1. Raw exception message logged via `GD.PushError("Transcription failed: " + ex.Message)`.
2. User-friendly message posted via `this.PostNotification("Voice transcription failed. Please try again.")`.
3. `TranscriptionFailed(string)` signal emitted with the error message.

### Signal Contract

| Signal | Payload | Description |
|--------|---------|-------------|
| `TranscriptionCompleted(string text)` | Transcribed text | Emitted when transcription succeeds |
| `TranscriptionFailed(string error)` | Error message | Emitted when transcription fails |

### Lifecycle

1. **Initialisation**: Load config, initialise microphone subsystem, connect XR input signals.
2. **Recording**: User presses record button → `StartRecording()` → microphone captures audio.
3. **Stop**: User releases button OR `MaxRecordingDuration` reached → `StopRecording()`.
4. **Transcription**: `Transcribe(AudioStreamWav)` called → async API request → result or exception.
5. **Completion**: On success → emit `TranscriptionCompleted` + notification. On failure → emit `TranscriptionFailed` + error log + notification.
6. **Cleanup**: Release microphone resources, reset state for next recording.

## Acceptance Criteria

1. The spec defines both user-visible voice input outcomes and technical implementation contracts.
2. The abstract `Transcriber` class defines XR binding, recording, transcription method, and signal contracts explicitly.
3. Error handling contract uses both `GD.PushError` for raw errors and `NotificationUIExtensions` for player-facing messages.
4. `OpenAITranscriber` implementation uses the official OpenAI .NET SDK and loads configuration from `AlleyCat.cfg`.
5. Runtime integration boundaries (XR binding, microphone, config, API, signals, lifecycle) are explicitly defined.
6. Implementation path `@game/src/Speech/Transcription/` and test path `@integration-tests/src/` are specified.
7. The spec does not exclude any mandatory delivery contracts through `Out Of Scope`.

## References

### Implementation

- `@game/src/Speech/Transcription/Transcriber.cs` - Abstract Transcriber class
- `@game/src/Speech/Transcription/OpenAITranscriber.cs` - OpenAI-compatible implementation
- `@game/src/UI/NotificationUIExtensions.cs` - Notification helpers
- `@game/AlleyCat.cfg` - Configuration file

### Related Specs

- [SPCH-001: Wav2Arkit Blendshape Player](../../speech/001-wav2arkit-blendshape-player/index.md)
- [SPCH-002: Audio2Face BlendShape Player](../../speech/002-audio2face-blendshape-player/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API
