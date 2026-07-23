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

1. **UR-1:** Players initiate voice recording via a configurable XR input button (default: left trigger).
2. **UR-2:** Recording auto-stops on button release or maximum duration.
3. **UR-3:** Transcription results are delivered to gameplay through the completion signal.
4. **UR-4:** Transcript UI notifications are optional, debug-oriented, and disabled by default.
5. **UR-5:** Transcription failures are reported through diagnostics and the failure signal; downstream listeners decide
   whether to surface user-facing messaging.
6. **UR-6:** The system uses OpenAI-compatible API endpoints for transcription.
7. **UR-7:** When transcript notifications are explicitly enabled, they appear promptly after transcription completes
   and are not delayed by downstream signal listeners, AI processing, or response generation.
8. **UR-8:** World and frame progression remain responsive while recording is finalised and throughout pending backend
   work, including synchronous backend setup before an asynchronous operation is returned.
9. **UR-9:** Captured speech retains its current sample rate and duration while mono retention and upload reduce the
   audio footprint. Speech transcription quality must remain suitable for the microphone input, without claiming
   equivalent results for arbitrary phase-misaligned stereo sources.
10. **UR-10:** Recording finalisation and upload preparation avoid a second recording-sized WAV payload, reducing peak
    production memory use without claiming that ordinary HTTP buffering or transport copies are eliminated.

## Technical Requirements

1. **TR-1:** Define abstract `Transcriber` as a `Node` or `Node3D` subclass under
   `@game/src/Speech/Transcription/Transcriber.cs`.
2. **TR-2:** Bind recording initiation to a configurable XR action; default to the left controller trigger.
3. **TR-3:** Capture microphone audio through an `AudioEffectCapture` ring buffer. While recording, each process
   callback drains no more than one bounded batch. Stopping first stops the player, then waits non-blockingly for an
   observed audio mix boundary before performing exactly one bounded final drain and clearing capture. A bounded
   fallback must prevent null or dummy audio drivers from stalling finalisation indefinitely. Batch size is a tunable
   implementation value; neither path may drain an arbitrary backlog or perform work proportional to the full recording
   during release.
4. **TR-4:** Define `Transcribe(RecordedAudioData)` as `Task<string>`. `RecordedAudioData` must own immutable managed
   PCM16 data and include its sample rate and channel count. It must reject payload lengths that are not aligned to a
   complete PCM16 frame for the declared channel count. No `AudioStreamWav` or other Godot `Resource` crosses the
   backend boundary.
5. **TR-5:** Emit signal `TranscriptionCompleted(string text)` on success.
6. **TR-6:** On failure, log raw diagnostic detail through `ILogger` and emit `TranscriptionFailed(string error)`
   without posting a transcriber-pipeline UI notification directly through `NotificationUIExtensions` or
   `PostNotification`.
7. **TR-7:** Implement `OpenAITranscriber` using the official OpenAI .NET SDK.
8. **TR-8:** Bind or read subsystem-owned STT options from CORE-006 `IConfiguration`, or build local custom-path JSON
   configuration when an explicit path is supplied. Options include `Host`, optional `ApiKey`, and additional model or
   timeout settings.
9. **TR-9:** Provide runtime integration for XR binding, microphone and audio-bus prerequisites, configuration, signals,
   and lifecycle.
10. **TR-10:** Export `TranscriptNotificationEnabled` on `Transcriber`; it defaults to `false` and controls only the
    successful transcript text notification.
11. **TR-11:** On successful transcription with `TranscriptNotificationEnabled` enabled, dispatch the transcript
    notification before completion signals or other downstream completion hooks.
12. **TR-12:** Dispatch the entire virtual `Transcribe(RecordedAudioData)` invocation through a worker boundary,
    including synchronous work performed before its `Task` is returned. The worker may read only managed recording data
    and backend state prepared on the Godot thread; it must not read a Godot `Resource`.
13. **TR-13:** Keep capture, recording stop, and final drain on the Godot thread. Dispatch completion, failure, and
    transcription-state updates back to that thread through a narrow Godot-safe boundary; do not run backend or AI setup
    in that dispatch. Recording, finalising, and transcribing state transitions, teardown resets, signal emission, Godot
    API calls, and user-visible callbacks must occur only on the Godot thread. Worker continuations may retain only
    managed immutable input, backend results or exceptions, and managed task/lifetime coordination.
14. **TR-14:** Godot capture continues to provide stereo `Vector2` frames. For every retained frame,
    `PCMAudioAccumulator` must calculate `(left + right) * 0.5f`, clamp the result to `[-1, 1]`, and write one mono
    signed little-endian PCM16 sample. A `NaN` result, including symmetric opposite infinities, must become silence;
    positive and negative infinity results must saturate to the corresponding limit. Conversion must map `-1` to
    `-32768`; all other values must round `sample * 32767` to the nearest integer. The managed recording must preserve
    the current sample rate and captured frame count, declare one channel, and use exactly two payload bytes per
    retained frame. Storage must remain bounded by the configured maximum recording duration.
15. **TR-15:** Clear capture and managed recording data between sessions. Account for and report ring-buffer discarded
    frames. Reject an empty capture without invoking the backend, and report it through diagnostics and
    `TranscriptionFailed`.
16. **TR-16:** Repeated sessions must contain no stale audio. Disabling during an active recording must still allow
    release to stop and clear capture without starting transcription. A finalising state must reject overlapping starts
    until final drain and transcription hand-off are decided. Teardown must abandon pending finalisation, stop capture,
    clear session data and state, and suppress callbacks from backend work that completes later.
17. **TR-17:** The deferred Godot-thread dispatcher must atomically stop accepting work when teardown begins, settle all
    queued and subsequently rejected dispatch tasks as cancelled, and validate the originating node-lifetime generation
    immediately before executing an action. Teardown cancellation is an expected lifecycle outcome and must not emit a
    transcription failure. No stale action may mutate state or emit signals after exit.
18. **TR-18:** `WaveFileStream` must expose a read-only, seekable logical WAV comprising its owned canonical 44-byte
    header followed by the existing immutable `ReadOnlyMemory<byte>` PCM payload. Reads must work within and across both
    regions, and seeking or rewinding must support exact replay. The production request path must not allocate or copy a
    second full recording-sized WAV payload; this contract does not prohibit normal destination-buffer, multipart, HTTP,
    or transport copies.
19. **TR-19:** OpenAI .NET 2.10.0 multipart upload must consume `WaveFileStream` directly. Deterministic tests must
    verify the exact logical WAV bytes and prove that one SDK retry sends an identical multipart file body after
    replaying the stream.

## In Scope

- Abstract `Transcriber` class with XR input binding and microphone recording.
- Incremental, bounded `AudioEffectCapture` draining and managed mono PCM16 accumulation.
- Immutable `RecordedAudioData` and `Transcribe(RecordedAudioData)` backend contract.
- Zero-copy WAV payload composition through a seekable `WaveFileStream` and OpenAI .NET multipart upload.
- Signal contract for transcription completion and failure.
- Error handling contract using `ILogger` and the failure signal.
- Optional transcript notification toggle for diagnostics and debug builds.
- `OpenAITranscriber` implementation using OpenAI .NET SDK.
- Subsystem-owned configuration contract using CORE-006 `IConfiguration` or explicit custom-path JSON loading.
- Capture overflow, empty-audio, repeated-session, disable, and teardown behaviour.
- Lifecycle-aware deferred dispatch with deterministic cancellation of stale work.
- Implementation under `@game/src/Speech/Transcription/`.
- Unit tests under `@tests/src/Speech/`.
- Integration tests under `@integration-tests/src/`.

## Out Of Scope

- Text-to-speech (TTS) capabilities.
- Real-time streaming transcription.
- Multiple simultaneous recording sessions.
- Local-only transcription without network connectivity.
- Non-OpenAI-compatible backend implementations beyond `OpenAITranscriber`.
- Optional audio preprocessing or custom voice activity detection beyond bounded format conversion and duration limits.

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

### Transcription Worker Boundary

The worker boundary covers invocation of the virtual backend method, not only awaiting the `Task` it returns. Commit
`80d048bf` addressed synchronous backend setup, but did not remove synchronous audio materialisation from XR release.
Backend work consumes immutable managed audio and never reads a Godot `Resource`. Godot-owned capture and lifecycle work
remains on the Godot thread, with completion, failure, diagnostics that can resolve runtime services, and state changes
dispatched back to it. An internal test-only lifecycle transition observer records the executing managed thread ID; it
does not add a public runtime API.

### Incremental Capture Boundary

The freeze root cause was `AudioEffectRecord.GetRecording()`, which synchronously performed native float-to-WAV
conversion proportional to recording length during XR release. Deterministic equivalent boundary evidence measured
257.16 ms for release, including 253.19 ms for materialisation, with zero frames advancing during release.

`AudioEffectCapture` instead exposes a ring buffer that can be drained incrementally into duration-bounded managed PCM.
Per-frame and final drains are structurally bounded so a large native backlog cannot turn release into unbounded work.
The current regression reads at most 2,048 stereo frames per batch and uses a generous 500 ms test threshold; these are
regression evidence, not universal product tuning constants.

Stopping the microphone player and draining capture are separate lifecycle steps because audio mixing occurs
asynchronously. Finalisation observes at least one subsequent mix boundary before its single final drain, with a bounded
process-frame fallback for audio drivers that do not advance the mix clock. The finalising state prevents a new session
from clearing or reusing capture during that interval.

### Capture Ownership and Lifecycle

Each recording owns a fresh managed accumulator. Capture and managed state are cleared between sessions and on teardown.
Ring-buffer overflow is observable through discarded-frame diagnostics, while empty captures fail before backend
dispatch. Disabling prevents new transcription but does not prevent release from stopping and clearing an active
capture.

### Mono Capture Conversion

Godot's capture API continues to supply stereo frames, but microphone retention does not need two managed samples per
frame. Incremental averaging produces one mono sample while preserving the capture sample rate, frame count, and timing.
This halves retained PCM and uploaded PCM payload storage from four to two bytes per captured frame. Averaging can
cancel phase-opposed channels, so this decision targets microphone speech and does not assert semantic equivalence for
arbitrary stereo material.

Non-finite input handling is deterministic: a `NaN` downmix is silence, while an infinite downmix saturates after
clamping. This keeps malformed capture values from producing implementation-dependent PCM.

### Composite WAV Upload

`WaveFileStream` presents the canonical 44-byte WAV header and immutable managed PCM as one logical file. The stream
owns only its header and reuses the recording's PCM memory, eliminating the previous second recording-sized WAV
allocation and payload copy on the production request path. Read and seek behaviour supports multipart length discovery
and replay by OpenAI .NET 2.10.0. The optimisation does not claim to remove copies performed by stream reads, multipart
serialisation, HTTP buffering, or the transport.

### No-Auth Backend Compatibility

`ApiKey` is optional in the `STT` config section. When omitted and the SDK
requires a non-empty value, a dummy credential is used only if the target
backend accepts unauthenticated requests. This avoids hard-coding credentials
for compatible services.

## Acceptance Criteria

1. **AC-1:** A configured XR action starts recording; release or maximum duration stops it; success and failure signals,
   opt-in transcript notification behaviour, OpenAI-compatible operation, and diagnostic failure handling satisfy
   UR-1–UR-7 and TR-1–TR-2, TR-5–TR-11.
2. **AC-2:** With 1,000,000 frames reported available, stopping performs exactly one final bounded read and leaves the
   remaining backlog undrained. Current regression evidence limits that read to 2,048 stereo frames and verifies release
   and final drain below a generous 500 ms threshold. Deterministic integration coverage publishes capture frames only
   after stop, advances the observed mix boundary, and verifies those frames are included by exactly one final read.
3. **AC-3:** At least three Godot frames advance while the backend remains pending. The existing synchronous
    backend-delay regression also verifies that the complete virtual invocation runs on a worker and completion returns
    to the Godot thread. Deterministic transition evidence verifies recording, finalising, and transcribing transitions,
    success/failure callbacks, and teardown resets execute on the Godot thread, satisfying UR-8 and TR-12–TR-13.
4. **AC-4:** Unit and integration tests verify that stereo frames `(-2, -2)`, `(2, 2)`, `(1, 0)`, and `(-1, 0)` produce
   mono PCM16 samples `-32768`, `32767`, `16384`, and `-16384` with exact little-endian bytes. Tests also verify silence
   for `NaN` and symmetric opposite infinities, saturation for infinite downmixes, one output frame per input frame, the
   unchanged current sample rate, one-channel metadata, and payload length `frame count * 2`. Immutable ownership,
   rejection of incomplete PCM16 frames, and duration-bounded storage must also be covered, satisfying UR-9 and TR-4,
   TR-14.
5. **AC-5:** Overflow reports discarded frames without exceeding duration-bounded storage; an empty capture logs and
   emits failure without invoking the backend; repeated sessions contain no prior-session audio, satisfying
   TR-15–TR-16.
6. **AC-6:** Disabling during recording still allows release to stop and clear capture without transcription. Teardown
    during capture or finalisation clears state and data without transcription or finalisation callbacks, overlapping
    starts are rejected while finalising, and teardown during backend work suppresses late signals, satisfying TR-16.
7. **AC-7:** `Out Of Scope` excludes only optional or unrelated work and omits no implementation or validation contract.
8. **AC-8:** Deterministic integration coverage pauses a completed backend after its Godot action is queued, tears the
   node down before flush, and verifies that completion and failure signals and hooks do not run, state remains cleared,
   and the dispatch/worker task settles as lifecycle cancellation, satisfying TR-17.
9. **AC-9:** Unit tests compare the complete logical WAV with an independently specified exact byte sequence, including
   its mono PCM16 metadata and unchanged payload. Reads wholly within the header or PCM and across their boundary,
   varied read sizes, all seek origins, EOF rewind, and exact replay are verified. Production request preparation must
   prove that `WaveFileStream` retains the existing PCM backing rather than allocating a second full payload. A real
   OpenAI .NET 2.10.0 multipart request receives one forced retry and verifies that both multipart file bodies are
   byte-identical to the expected WAV, satisfying UR-10 and TR-18–TR-19.

**Traceability Map:** UR-1–UR-7 and TR-1–TR-2, TR-5–TR-11 → AC-1; UR-8 and TR-3, TR-12–TR-13 → AC-2–AC-3;
UR-9 and TR-4, TR-14 → AC-4; TR-15–TR-16 → AC-5–AC-6; out-of-scope guard → AC-7; TR-17 → AC-8; UR-10 and
TR-18–TR-19 → AC-9.

## References

### Implementation

- `@game/src/Speech/Transcription/Transcriber.cs`
- `@game/src/Speech/Transcription/OpenAITranscriber.cs`
- `@game/src/Speech/Transcription/RecordedAudioData.cs`
- `@game/src/Speech/Transcription/PCMAudioAccumulator.cs`
- `@game/src/Speech/Transcription/WaveFileStream.cs`
- `@game/default_bus_layout.tres`
- `@game/src/UI/NotificationUIExtensions.cs`
- `@tests/src/Speech/PCMAudioAccumulatorTests.cs`
- `@tests/src/Speech/WaveFileStreamTests.cs`
- `@tests/src/Speech/OpenAITranscriberTests.cs`
- `@integration-tests/src/Speech/TranscriberIntegrationTests.cs`

### Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-006: Microsoft Configuration Integration](../../core/006-microsoft-configuration-integration/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)

### External Dependencies

- OpenAI .NET SDK 2.10.0 (NuGet package)
- Godot XR Tools or native XR input API
