---
id: SPCH-003
title: Transcriber Component
---

# Transcriber Component

## Requirement

Provide an abstract `Transcriber` component that captures microphone audio
via Godot's XR input system, triggers async transcription, emits completion
and failure signals, and surfaces results and errors to the player. Deliver one
concrete `OpenAITranscriber` implementation using the OpenAI .NET SDK.

## Goal

Enable voice input capture and transcription in the VR experience with a
concrete OpenAI-compatible implementation that can be extended to other STT
backends.

## User Requirements

1. Players initiate voice recording via a configurable XR input button
   (default: left trigger).
2. Recording auto-stops on button release or maximum duration.
3. Transcription results surface to the player via a UI notification.
4. Transcription failures surface as a user-friendly error message.
5. The system uses OpenAI-compatible API endpoints for transcription.

## Technical Requirements

1. Define abstract `Transcriber` as a `Node` or `Node3D` subclass under
   `@game/src/Speech/Transcription/Transcriber.cs`.
2. Bind recording initiation to a configurable XR action; default left
   controller trigger.
3. Use Godot microphone API to capture audio and produce `AudioStreamWav`.
4. Define abstract method `Transcribe(AudioStreamWav)` as `async Task<string>`.
5. Emit signal `TranscriptionCompleted(string text)` on success.
6. On failure: log raw error via `GD.PushError`, display a user-friendly
   message via `@game/src/UI/NotificationUIExtensions.cs`, and emit signal
   `TranscriptionFailed(string error)`.
7. Implement `OpenAITranscriber` using the official OpenAI .NET SDK.
8. Load configuration from the merged configuration API
   ([CORE-002](../../002-configuration-api/index.md)) [STT] section
   containing `Host`, optional `ApiKey`, and additional model/timeout
   settings.
9. Specify runtime integration: XR binding, microphone prerequisites,
   config contract, signal contract, and lifecycle.

## In Scope

- Abstract `Transcriber` class with XR input binding and microphone recording.
- Abstract `Transcribe(AudioStreamWav)` async method contract.
- Signal contract for transcription completion and failure.
- Error handling contract using `GD.PushError` and `NotificationUIExtensions`.
- `OpenAITranscriber` implementation using OpenAI .NET SDK.
- Configuration contract from merged configuration API.
- Implementation under `@game/src/Speech/Transcription/`.
- Integration tests under `@integration-tests/src/`.

## Out Of Scope

- Text-to-speech (TTS) capabilities.
- Real-time streaming transcription.
- Multiple simultaneous recording sessions.
- Local-only transcription without network connectivity.
- Non-OpenAI-compatible backend implementations beyond `OpenAITranscriber`.
- Audio preprocessing or custom voice activity detection beyond duration limits.

## Design Decisions

### Button Press/Release Recording Model

Recording starts when the XR action value exceeds 0.5 and stops on release
or timeout. This matches the natural hold-to-speak idiom common in VR voice
input and avoids extra confirmation steps.

### Error Dual-Channel Pattern

Failures emit both a raw `GD.PushError` (for diagnostics) and a
player-facing notification (for usability). Both are emitted; the notification
supplies a static user-friendly message while the signal carries the raw detail
for listeners that need it.

### No-Auth Backend Compatibility

`ApiKey` is optional in the [STT] config section. When omitted and the SDK
requires a non-empty value, a dummy credential is used only if the target
backend accepts unauthenticated requests. This avoids hard-coding credentials
for compatible services.

## Acceptance Criteria

1. UR-1–UR-5 covered: player can record, auto-stop works, success/failure
   messages reach the player.
2. TR-1–TR-9 covered: abstract class, XR binding, microphone capture, async
   contract, signals, dual-channel error handling, SDK implementation, config
   loading, and runtime integration specified.
3. `Out Of Scope` excludes only optional/unrelated work; no mandatory contract
   omitted.

**Traceability map:** UR-1–UR-5 → AC-1; TR-1–TR-9 → AC-2; OOS guard → AC-3.

## References

### Implementation

- `@game/src/Speech/Transcription/Transcriber.cs`
- `@game/src/Speech/Transcription/OpenAITranscriber.cs`
- `@game/src/UI/NotificationUIExtensions.cs`

### Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CORE-002: Configuration API](../../002-configuration-api/index.md)

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API
