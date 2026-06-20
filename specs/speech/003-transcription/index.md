---
id: SPCH-003
title: Transcriber Component
---

# Transcriber Component

## Requirement

Provide an abstract `Transcriber` component that captures microphone audio via Godot's XR input system, triggers async
transcription, emits completion and failure signals, and can optionally surface transcript text as a debug-oriented UI
notification. Deliver one concrete `OpenAITranscriber` implementation using the OpenAI .NET SDK.

## Goal

Enable voice input capture and transcription in the VR experience with a concrete OpenAI-compatible implementation that
can be extended to other STT backends.

## User Requirements

1. Players initiate voice recording via a configurable XR input button
   (default: left trigger).
2. Recording auto-stops on button release or maximum duration.
3. Transcription results are delivered to gameplay through the completion signal.
4. Transcript UI notifications are optional/debug-oriented and disabled by default.
5. Transcription failures are reported through diagnostics and the failure signal; downstream listeners may decide
   whether to surface user-facing messaging.
6. The system uses OpenAI-compatible API endpoints for transcription.
7. When transcript notifications are explicitly enabled, they appear promptly after transcription completes and are not
   delayed by downstream signal listeners, AI processing, or response generation.

## Technical Requirements

1. Define abstract `Transcriber` as a `Node` or `Node3D` subclass under
   `@game/src/Speech/Transcription/Transcriber.cs`.
2. Bind recording initiation to a configurable XR action; default left
   controller trigger.
3. Use Godot microphone API to capture audio and produce `AudioStreamWav`.
4. Define abstract method `Transcribe(AudioStreamWav)` as `async Task<string>`.
5. Emit signal `TranscriptionCompleted(string text)` on success.
6. On failure: log raw diagnostic detail via `ILogger` and emit signal `TranscriptionFailed(string error)` without
   posting a transcriber-pipeline UI notification directly through `NotificationUIExtensions` or `PostNotification`.
7. Implement `OpenAITranscriber` using the official OpenAI .NET SDK.
8. Bind/read subsystem-owned STT options from CORE-006 `IConfiguration`, or build a local custom-path JSON
   configuration when an explicit path is supplied. Options include `Host`, optional `ApiKey`, and additional
   model/timeout settings.
9. Specify runtime integration: XR binding, microphone prerequisites,
    config contract, signal contract, and lifecycle.
10. Export `TranscriptNotificationEnabled` on `Transcriber`; it defaults to `false` and controls only the successful
    transcript text notification.
11. On successful transcription with `TranscriptNotificationEnabled` enabled, dispatch the transcript notification
    before emitting completion signals or invoking other downstream completion hooks.
12. Keep long-running transcription preparation, network calls, and other avoidable blocking work off frame-critical
    Godot execution paths where practical; completion dispatch should stay narrow and should not front-load AI or LLM
    setup ahead of the optional transcript notification.

## In Scope

- Abstract `Transcriber` class with XR input binding and microphone recording.
- Abstract `Transcribe(AudioStreamWav)` async method contract.
- Signal contract for transcription completion and failure.
- Error handling contract using `ILogger` and the failure signal.
- Optional transcript notification toggle for diagnostics and debug builds.
- `OpenAITranscriber` implementation using OpenAI .NET SDK.
- Subsystem-owned configuration contract using CORE-006 `IConfiguration` or explicit custom-path JSON loading.
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

### Error Diagnostics Pattern

Failures emit an `ILogger` error for diagnostics and `TranscriptionFailed` for runtime listeners. The transcriber
pipeline does not call `NotificationUIExtensions` or `PostNotification` for failures. If general logging configuration
routes error logs to a notification sink, that is outside the transcriber boundary and should not require
transcriber-specific logger category suppression.

### Optional Transcript Notification

Successful transcript notifications are debug-oriented and opt-in through `TranscriptNotificationEnabled`. When enabled,
the transcript notification is posted before completion signal listeners run so slow downstream AI processing cannot
delay visible diagnostic feedback.

### No-Auth Backend Compatibility

`ApiKey` is optional in the `STT` config section. When omitted and the SDK
requires a non-empty value, a dummy credential is used only if the target
backend accepts unauthenticated requests. This avoids hard-coding credentials
for compatible services.

## Acceptance Criteria

1. UR-1–UR-7 covered: player can record, auto-stop works, completion/failure signals deliver results, transcript
   notifications are off by default but can be enabled, failures use diagnostics and signals rather than direct UI
   notification calls, and enabled transcript notifications are not delayed by downstream processing.
2. TR-1–TR-12 covered: abstract class, XR binding, microphone capture, async contract, signals, logger-and-signal error
   handling, SDK implementation, subsystem-owned config loading, runtime integration, transcript notification toggle and
   conditional ordering, and non-blocking completion boundaries are specified.
3. `Out Of Scope` excludes only optional/unrelated work; no mandatory contract
   omitted.

**Traceability map:** UR-1–UR-7 → AC-1; TR-1–TR-12 → AC-2; OOS guard → AC-3.

## References

### Implementation

- `@game/src/Speech/Transcription/Transcriber.cs`
- `@game/src/Speech/Transcription/OpenAITranscriber.cs`
- `@game/src/UI/NotificationUIExtensions.cs`

### Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-006: Microsoft Configuration Integration](../../core/006-microsoft-configuration-integration/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)

### External Dependencies

- OpenAI .NET SDK (NuGet package)
- Godot XR Tools or native XR input API
