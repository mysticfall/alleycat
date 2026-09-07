---
id: SPCH-003
title: Transcriber Component
---

# Transcriber Component

## Requirement

Provide an abstract `Transcriber` component that captures microphone audio via Godot's XR input system, triggers async
transcription, emits completion and failure signals, records STT pipeline latency diagnostics through the shared
pipeline diagnostic log (CORE-007), and can optionally surface transcript text as a debug-oriented UI notification.
Deliver one concrete, config-driven `OpenAITranscriber` backend against the deployed OpenAI-compatible WhisperLive
server, issuing one whole-recording REST transcription request per manual recording and per endpoint-delimited
automatic segment (SPCH-008) through a custom multipart form. The backend serves both the manual hold-to-speak path
and the automatic hands-free path (SPCH-008), settles automatic segment outcomes in speaking order per speech group,
and emits one notification-eligible STT dispatch marker under the Speech-owned `AlleyCat.Pipeline.STT` child
category (TR-27).

## Goal

Enable voice input capture and transcription in the VR experience with both manual hold-to-speak and automatic
hands-free input (SPCH-008) served by one deployed REST-only WhisperLive server and one config-driven transcriber,
using a concrete implementation that can be extended to other STT backends. Automatic speech is finalised
pause-delimited: each endpoint-closed segment is dispatched immediately as one whole-segment REST request, without
streaming transcription or per-word drafts.

## User Requirements

1. **UR-1:** Players initiate manual voice recording via a configurable XR input button (default: left trigger).
2. **UR-2:** Manual recording auto-stops on button release or maximum duration.
3. **UR-3:** Transcription results are delivered to gameplay through the completion signal.
4. **UR-4:** Transcript UI notifications are optional, debug-oriented, and disabled by default.
5. **UR-5:** Transcription failures are reported through diagnostics and the failure signal; downstream listeners decide
   whether to surface user-facing messaging.
6. **UR-6:** Both input paths are served by one deployed REST-only WhisperLive speech-to-text server through its
   OpenAI-compatible transcription endpoint, using one shared REST request contract.
7. **UR-7:** When transcript notifications are explicitly enabled, they appear promptly after transcription completes
   and are not delayed by downstream signal listeners, AI processing, or response generation.
8. **UR-8:** World and frame progression remain responsive while recording is finalised and throughout pending backend
   work, including synchronous backend setup before an asynchronous operation is returned.
9. **UR-9:** Captured manual speech retains its current sample rate and duration while mono retention and upload reduce
   the audio footprint. Speech transcription quality must remain suitable for the microphone input, without claiming
   equivalent results for arbitrary phase-misaligned stereo sources.
10. **UR-10:** Recording finalisation and upload preparation avoid a second recording-sized WAV payload, reducing peak
     production memory use without claiming that ordinary HTTP buffering or transport copies are eliminated.
11. **UR-11:** Recording start is observable to gameplay consumers such as the player's `Voice` component, so the
     player's speaking state is accurate from the first moment of capture and other speakers do not talk over the
     recording.
12. **UR-12:** When automatic input is enabled, the transcriber captures speech without a button press and finalises
     each endpoint-delimited segment with exactly one authoritative whole-segment REST request over mono 16 kHz audio,
     dispatched immediately at segment closure; the segment and speech-group lifecycle policy is owned by SPCH-008.
13. **UR-13:** Configured `Prompt` and `Hotwords` hints are delivered on every REST request, with configured wording
     preserved after trimming outer whitespace.
14. **UR-14:** Manual input takes precedence over automatic input per SPCH-008: a button press abandons any open
     automatic speech group — every in-flight segment request and unsettled outcome — without a public aggregate and
     suppresses automatic admission until the manual session finishes.
15. **UR-15:** Gameplay receives a speech group's automatic segment transcripts in speaking order, even when the
     backend completes requests out of order. Blank, failed, or cancelled segments never insert invented text and never
     discard their successful siblings.
16. **UR-16:** Developers can opt into a debug-oriented notification that fires when audio is dispatched to the STT
     backend. It appears immediately before the request, carries only non-sensitive operational metadata, and never
     contains transcript text, audio, prompts, hotwords, credentials, or endpoint details.

## Technical Requirements

1. **TR-1:** Define abstract `Transcriber` as a `Node` or `Node3D` subclass under
   `@game/src/Speech/Transcription/Transcriber.cs`.
2. **TR-2:** Bind manual recording initiation to a configurable XR action; default to the left controller trigger.
3. **TR-3:** Capture microphone audio through an `AudioEffectCapture` ring buffer. While recording, each process
   callback drains no more than one bounded batch. Stopping first stops the microphone playback synchronously
   (disconnecting the capture stream immediately), then stops the player, then waits non-blockingly for an observed
   audio mix boundary before performing exactly one bounded final drain and clearing capture. A bounded fallback must
   prevent null or dummy audio drivers from stalling finalisation indefinitely. Batch size is a tunable implementation
   value; neither path may drain an arbitrary backlog or perform work proportional to the full recording during
   release.
4. **TR-4:** Define `Transcribe(RecordedAudioData)` as `Task<string>`. `RecordedAudioData` must own immutable managed
   PCM16 data and include its sample rate and channel count. It must reject payload lengths that are not aligned to a
   complete PCM16 frame for the declared channel count. No `AudioStreamWav` or other Godot `Resource` crosses the
   backend boundary.
5. **TR-5:** Emit signal `TranscriptionCompleted(string text)` on success. For automatic segment requests, the
   completion and failure outcomes additionally carry the structured segment metadata defined in TR-25; manual
   outcomes remain plain ungrouped text.
6. **TR-6:** On failure, log raw diagnostic detail through `ILogger` and emit `TranscriptionFailed(string error)`
   without posting a transcriber-pipeline UI notification directly through `NotificationUIExtensions` or
   `PostNotification`.
7. **TR-7:** Implement one concrete backend, `OpenAITranscriber`, config-driven from the `STT` configuration section.
   It serves both manual and automatic recordings through the same REST request path; the `RecordedAudioData` /
   `WaveFileStream` contracts are identical for both.
8. **TR-8:** Bind or read subsystem-owned STT options from CORE-006 `IConfiguration`, or build local custom-path YAML
   configuration when an explicit path is supplied, from the `STT` section of `game/AlleyCat.yaml`. The section defines
   exactly: `Host`, optional `ApiKey`, `Model`, optional `Language`, optional `Prompt`, nullable `Hotwords`, and
   `Timeout`. Blank or null `Hotwords` disables the parameter.
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
    transcription-state updates back to that thread through a narrow Godot-safe boundary; do not run backend or AI
    setup in that dispatch. Recording, finalising, and transcribing state transitions, teardown resets, signal
    emission, Godot API calls, and user-visible callbacks must occur only on the Godot thread. Worker continuations
    may retain only managed immutable input, backend results or exceptions, and managed task/lifetime coordination.
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
     clear session data and state, cancel or abandon every affected automatic speech group — including all in-flight
     segment requests and unsettled outcomes — and suppress callbacks from backend work that completes later.
17. **TR-17:** The deferred Godot-thread dispatcher must atomically stop accepting work when teardown begins, settle all
     queued and subsequently rejected dispatch tasks as cancelled, and validate the originating node-lifetime generation
     immediately before executing an action. Teardown cancellation is an expected lifecycle outcome and must not emit a
     transcription failure. No stale action may mutate state or emit signals after exit.
18. **TR-18:** `WaveFileStream` must expose a read-only, seekable logical WAV comprising its owned canonical 44-byte
    header followed by the existing immutable `ReadOnlyMemory<byte>` PCM payload. Reads must work within and across
    both regions, and seeking or rewinding must support exact replay. The production request path must not allocate
    or copy a second full recording-sized WAV payload; this contract does not prohibit normal destination-buffer,
    multipart, HTTP, or transport copies.
19. **TR-19:** The REST request is a custom multipart form posted to the server's OpenAI-compatible
     `/v1/audio/transcriptions` endpoint, because WhisperLive's native `hotwords` extension cannot be expressed by the
     official SDK options. The form carries: the complete WAV `file`, `model`, optional `language`, optional `prompt`
     (from `Prompt`), optional `hotwords` (from `Hotwords`), and `response_format=json`. The response is plain JSON and
     its `text` field is the transcript. Retries must replay the complete seekable WAV so the file part of every attempt
     is byte-identical. `Timeout` bounds the request.
20. **TR-20:** Emit a public parameterless `RecordingStarted` signal on the Godot thread when a manual recording begins
     (the XR action value exceeds 0.5) and when a qualified automatic onset occurs (SPCH-008) — once per manual session
     and once per speech group, never per segment. For a qualified automatic onset, the structured
     `AutomaticGroupOpened(string speechGroupID)` signal — emitted once per group — is the authoritative source of the
     onset's real `(SpeechGroupID, SegmentIndex = 0)` identity (TR-25) and must be emitted immediately before the
     parameterless compatibility `RecordingStarted` event on the same Godot thread, so consumers observe the real
     segment identity before the public speaking window opens (AI-001 TR-47). `RecordingStarted` is the public
     exposure of the existing protected `OnRecordingStarted()` hook and must follow the same signal pattern as
     `TranscriptionCompleted`/`TranscriptionFailed`. `PlayerVoice` (SPCH-005) subscribes to it to open its speaking
     window; re-emission must not double-open an already open window.
21. **TR-21:** Deliver the automatic input path under `@game/src/Speech/Transcription/` through the same `Transcriber`
     base, coordinating automatic admission through the replaceable `IVoiceActivityDetector` boundary (implementation
     `SileroVoiceActivityDetector`, SPCH-008) and segment lifecycle through `AutomaticUtteranceCoordinator`, per
     SPCH-008 semantics. In automatic-enabled modes the capture pipeline runs continuously and segment retention —
     pre-roll included — is windowed from the bounded capture stream; retention stays bounded by SPCH-008's maximum
     automatic speech duration. Retained automatic audio is mono 16 kHz on the resampled sample timeline.
22. **TR-22:** After local segment closure (SPCH-008), transcribe the retained whole-segment audio through exactly one
     REST request per the shared contract in TR-19, dispatched immediately at closure. The REST result is authoritative
     for its segment; its failure settles that segment failed per SPCH-008 and TR-26 without discarding successful
     siblings.
23. **TR-23:** All Godot API, state, and signal work for the automatic path remains on the Godot thread through the
     existing generation-checked lifecycle boundary (TR-13, TR-17); REST network work runs off the Godot thread, and
     worker continuations retain only managed data.
24. **TR-24:** The automatic path must observe manual session start and implement SPCH-008 precedence: abandon any open
     automatic speech group — cancel or abandon every in-flight segment request and unsettled outcome, emit no public
     aggregate — and suppress automatic admission until the manual session finishes, so no overlapping public logical
     speaking windows occur.
25. **TR-25:** Structured automatic segment metadata. Automatic completion and failure outcomes carry: `SpeechGroupID`,
     a stable opaque string identifying the speech group (SPCH-008); `SegmentIndex`, the zero-based index of the segment
     within its group; and `Continued`, true exactly when `SegmentIndex > 0`. Manual outcomes carry no group metadata.
     The metadata is generic identity only — it implies no draft, speculative, or adoption semantics.
26. **TR-26:** Concurrent requests and ordered settlement. Segment REST requests of one group may run concurrently.
     Outcomes must settle ordered per group by `SegmentIndex`: store each outcome immediately when it arrives — through
     the Godot-thread boundary of TR-13 — then publish the group's outcomes consecutively in `SegmentIndex` order, so an
     outcome is published only once every lower index of its group has settled. Blank, failed, or cancelled segments
     advance this ordering gate without inventing text. One segment's failure must not discard successful siblings.
     Separate groups drain independently.
27. **TR-27:** STT dispatch notification. Emit exactly one structured notification-eligible dispatch marker per logical
      transcription request — one manual session or one automatic segment — immediately before the REST transcription
      dispatch (TR-19) in the transcriber:
      - The marker logs under the dedicated child category `AlleyCat.Pipeline.STT` at level `Debug` and carries
        `IUINotificationEntry` state; toast eligibility and routing run through the generic CORE-007 mechanism, which
        this spec cross-references rather than duplicates.
      - Speech owns the category, the marker entry type, its `Debug` gating, and the emission site: they live with the
        transcriber under `@game/src/Speech/Transcription/`, following the placement precedent of the
        automatic-voice-input unavailability entry, and production Core logging defines no STT category, entry type,
        or emission.
      - It records only non-sensitive operational metadata: the manual/automatic source, the audio duration, and the PCM
        byte count.
      - It must never carry transcript text, audio content, prompt, hotwords, credentials, endpoint, or request body.
      - SDK-internal retries must not duplicate the marker; it is emitted once per logical request.
      - The console line and toast render through one notification text — for example "Dispatching manual audio to STT
        (2.35 seconds, 75200 PCM bytes)" — whose category name and wording must not change.
      - YAML toggle semantics match the shipped `game/AlleyCat.yaml` wording: the child inherits its effective level
        from the longest matching configured prefix, so the active `AlleyCat.Pipeline: Debug` parent keeps the marker's
        console output and toast on by default while the commented `# AlleyCat.Pipeline.STT: Debug` line documents the
        toggle — set it to `None` to disable it. Setting the child to `None` or any level above `Debug` suppresses the
        entry entirely, console and toast alike; setting it to `Debug` or `Trace` enables it regardless of the parent.
      - All other STT diagnostics are ordinary log-only entries, including request preparation and the post-response
        latency milestone named "STT backend returned in".
      - The marker means the application is dispatching audio to the SDK pipeline, not that the server received it.
28. **TR-28:** Manual abandonment notification. When a manual session ends without a finalisation result — transcriber
    teardown (`_ExitTree`), transcriber replacement or signal disconnection, or a disabled/cancelled manual capture —
    emit exactly one terminal parameterless `RecordingAbandoned` signal on the Godot thread, so downstream holders of
    the synthetic manual token can settle the session textlessly (SPCH-005; AI-001 UR-14). A manual session that
    settles through an existing completion or failure outcome — including the empty-capture blank reported through
    `TranscriptionFailed` (TR-15) or a backend failure (TR-6) — must not also emit it: each manual session ends in
    exactly one terminal outcome. The notification is textless, never invents transcript content, and is the manual
    counterpart of automatic group abandonment (TR-16), following the same Godot-thread signal pattern as
    `TranscriptionCompleted`/`TranscriptionFailed`.

## In Scope

- Abstract `Transcriber` class with XR input binding and microphone recording.
- Incremental, bounded `AudioEffectCapture` draining and managed mono PCM16 accumulation.
- Immutable `RecordedAudioData` and `Transcribe(RecordedAudioData)` backend contract.
- Zero-copy WAV payload composition through a seekable `WaveFileStream` and multipart upload.
- Signal contract for transcription completion and failure.
- Public `RecordingStarted` signal exposing manual and automatic recording start to gameplay consumers such as
  `PlayerVoice`, preceded at qualified automatic onset by the authoritative `AutomaticGroupOpened` identity signal.
- Terminal `RecordingAbandoned` notification for manual sessions that end without a finalisation result.
- Automatic capture alongside button capture: `IVoiceActivityDetector` boundary, `SileroVoiceActivityDetector`, and
  `AutomaticUtteranceCoordinator` segment lifecycle (SPCH-008).
- Single config-driven `OpenAITranscriber` backend and the `STT` configuration shape.
- Custom multipart REST transcription contract including `hotwords`, retry replay, and timeout.
- Whole-segment mono 16 kHz finalisation of automatic recordings, dispatched at segment closure (SPCH-008).
- Structured segment completion/failure metadata and ordered per-group settlement of concurrent segment requests.
- `Prompt` and `Hotwords` hint options on every REST request.
- One deployed REST-only WhisperLive server serving both paths.
- Error handling contract using `ILogger` and the failure signal.
- Optional transcript notification toggle for diagnostics and debug builds.
- STT pipeline stage and latency diagnostics through the shared pipeline diagnostic log (CORE-007), and Speech
  ownership of the `AlleyCat.Pipeline.STT` child category — its notification-eligible dispatch marker, `Debug`
  gating, notification text, and YAML toggle semantics (TR-27).
- Subsystem-owned configuration contract using CORE-006 `IConfiguration` or explicit custom-path YAML loading.
- Capture overflow, empty-audio, repeated-session, disable, and teardown behaviour, including group abandonment.
- Lifecycle-aware deferred dispatch with deterministic cancellation of stale work.
- Implementation under `@game/src/Speech/Transcription/`.
- Unit tests under `@tests/src/Speech/`.
- Integration tests under `@integration-tests/src/`.

## Out Of Scope

- Text-to-speech (TTS) capabilities.
- Independent concurrent microphone capture sessions; manual and automatic input share one capture under SPCH-008
  precedence, while concurrent in-flight automatic segment REST requests are in scope (TR-26).
- Local-only transcription without network connectivity.
- Additional STT backends beyond `OpenAITranscriber`.
- Streaming or partial transcription: WebSocket transcription, per-word drafts, and speculative sidecar adoption are
  not part of this component.
- General audio preprocessing such as noise suppression or dereverberation beyond bounded format conversion,
  resampling, and duration limits.
- Feature-level input-mode, segment-lifecycle, and failure policies, which SPCH-008 owns and this spec references
  normatively.

## Design Decisions

### Button Press/Release Recording Model

Recording starts when the XR action value exceeds 0.5 and stops on release
or timeout. This matches the natural hold-to-speak idiom common in VR voice
input and avoids extra confirmation steps. The automatic path (SPCH-008) adds
hands-free input alongside this model without changing it.

### Recording-Started Signal

The protected `OnRecordingStarted()` hook already marks the moment recording begins. Exposing it as a public
parameterless `RecordingStarted` signal lets gameplay consumers such as `PlayerVoice` open their speaking window at
the first moment of capture, so turn-taking gates cover the player's in-progress speech before any transcript exists.
Automatic group onsets open the same window through the same signal — once per group, never per segment — so consumers
need no path-specific handling and segment closures never re-open an already open window.

At a qualified automatic onset, `AutomaticGroupOpened(groupID)` precedes the compatibility signal so the real
`(SpeechGroupID, SegmentIndex = 0)` identity is observable before the public window opens. Downstream textless start
cues key off that identity (AI-001 TR-47), which is what makes suppression at speech onset and its deterministic
arbitration against NPC TTS admission (AI-002 TR-25–TR-26) decidable before any text exists.

### Manual-Abandonment Signal

Manual blank and failure completions already settle a manual session through public outcomes. The silent paths —
teardown (`_ExitTree`), transcriber replacement or disconnection, and disabled/cancelled capture — dispatch no
completion or failure, so the terminal `RecordingAbandoned` signal gives downstream holders of the synthetic manual
token exactly one textless settlement point (AI-001 UR-14), mirroring automatic group abandonment (TR-16).

### Error Diagnostics Pattern

Failures emit an `ILogger` error for diagnostics and `TranscriptionFailed` for runtime listeners. The transcriber
pipeline does not call `NotificationUIExtensions` or `PostNotification` for failures. If general logging configuration
routes error logs to a notification sink, that is outside the transcriber boundary and should not require
transcriber-specific logger category suppression.

### Optional Transcript Notification

Successful transcript notifications are debug-oriented and opt-in through `TranscriptNotificationEnabled`. When enabled,
the transcript notification is posted before completion signal listeners run so slow downstream AI processing cannot
delay visible diagnostic feedback.

### STT Dispatch Marker And Latency Diagnostics

STT stages and latencies are recorded through the shared pipeline diagnostic log (CORE-007), but the STT dispatch
diagnostics themselves are Speech-owned: the `AlleyCat.Pipeline.STT` child category, its marker entry type, `Debug`
gating, and notification formatting live with the transcriber under `@game/src/Speech/Transcription/` — the same
placement as the automatic-voice-input unavailability entry — so Core keeps only the generic routing machinery. The
single notification-eligible STT entry is the dispatch marker under the dedicated child category (TR-27): emitted
once per logical request immediately before the REST transcription dispatch, it marks that the application is
handing audio to the SDK pipeline — not that the server received it. Every other STT entry — recording stop, request
preparation, completion, failures, micro-stage latency measurements, and the post-response "STT backend returned in"
milestone — is log-only, preserving console coverage without toasts. The `Transcriber` hosts no
latency-notification control of its own — the `AlleyCat.Pipeline.STT` category's configured level is the only
switch, with CORE-007 defining the generic category mechanics.

### Transcription Worker Boundary

The worker boundary covers invocation of the virtual backend method, not only awaiting the `Task` it returns.
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
and exact replay for retries. The optimisation does not claim to remove copies performed by stream reads, multipart
serialisation, HTTP buffering, or the transport. The automatic path's segment finalisation reuses this contract
unchanged.

### No-Auth Backend Compatibility

`ApiKey` is optional in the `STT` config section. When omitted and the request path requires a non-empty value, a dummy
credential is used only if the target backend accepts unauthenticated requests. This avoids hard-coding credentials for
compatible services.

### WhisperLive Server And Conformance Evidence

Both paths target one deployed WhisperLive speech-to-text server, REST-only on port 8000. A Phase 0 conformance spike
verified the following REST behaviour, treated as normative evidence:

- The OpenAI-compatible `/v1/audio/transcriptions` multipart endpoint honours `prompt` and `hotwords`, returns plain
  JSON with `response_format=json`, and answers in 0.88–2.6 s warm; hotwords measurably fix unusual words.
- The SSE `stream=true` helper drops `hotwords` and must not be used.

### Custom Multipart Request

WhisperLive's native `hotwords` extension cannot be expressed through the official SDK options, so `OpenAITranscriber`
issues a custom multipart form instead of an SDK-options-driven request. The form keeps the request surface explicit —
file, model, optional language, optional prompt, optional hotwords, `response_format=json` — and pairs with the seekable
`WaveFileStream` so retries replay an identical file body without re-materialising the recording.

### Segment-Delimited Finalisation

Each endpoint-closed automatic segment is transcribed by exactly one REST request over its complete retained mono
16 kHz audio, dispatched immediately at closure (SPCH-008). Segment requests of one group run concurrently so later
segments are not delayed by earlier responses, but outcomes settle in `SegmentIndex` order (TR-26) so gameplay receives
a group's speech in speaking order. Manual recordings and automatic segments share one request contract; no streaming,
per-word, or draft mechanism exists on either path.

## Acceptance Criteria

1. **AC-1:** A configured XR action starts manual recording; release or maximum duration stops it; success and failure
   signals, opt-in transcript notification behaviour, server-backed operation through the deployed WhisperLive REST
   endpoint, and diagnostic failure handling satisfy UR-1–UR-7 and TR-1–TR-2, TR-5–TR-11.
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
   during capture, finalisation, or pending backend work clears state and data, cancels or abandons every affected
   automatic speech group, and suppresses late signals and finalisation callbacks; overlapping starts are rejected
   while finalising, satisfying TR-16.
7. **AC-7:** `Out Of Scope` excludes only optional or unrelated work and omits no implementation or validation contract.
8. **AC-8:** Deterministic integration coverage pauses a completed backend after its Godot action is queued, tears the
   node down before flush, and verifies that completion and failure signals and hooks do not run, state remains cleared,
   and the dispatch/worker task settles as lifecycle cancellation, satisfying TR-17.
9. **AC-9:** Unit tests compare the complete logical WAV with an independently specified exact byte sequence, including
   its mono PCM16 metadata and unchanged payload. Reads wholly within the header or PCM and across their boundary,
   varied read sizes, all seek origins, EOF rewind, and exact replay are verified. Production request preparation must
   prove that `WaveFileStream` retains the existing PCM backing rather than allocating a second full payload.
   Request-level tests verify that one retry replays the stream and sends a byte-identical multipart file body,
   satisfying UR-10 and TR-18–TR-19.
10. **AC-10:** Tests verify `RecordingStarted` is emitted exactly once when manual recording begins and once at a
      qualified automatic group onset — never per segment — on the Godot thread, that the qualified onset first emits
      `AutomaticGroupOpened(speechGroupID)` carrying the real `(SpeechGroupID, SegmentIndex = 0)` identity immediately
      before `RecordingStarted`, that re-emission does not double-open an open window, and that `PlayerVoice` opens
      its speaking window from it, satisfying UR-11 and TR-20 and cross-referencing SPCH-005.
11. **AC-11:** Configuration and request tests verify the `STT` section shape (`Host`, optional `ApiKey`, `Model`,
     optional `Language`, optional `Prompt`, nullable `Hotwords`, `Timeout`), the custom multipart form fields including
     `hotwords`, `response_format=json`, the JSON `text` response contract, hint wording preserved after trimming outer
     whitespace, blank `Hotwords` omitting the parameter, and the timeout bound, satisfying UR-6, UR-13 and TR-7–TR-8,
     TR-19.
12. **AC-12:** Automatic-path tests verify continuous capture with windowed pre-roll retention bounded by the maximum
     speech duration, mono 16 kHz retained audio, structured segment metadata on automatic outcomes with manual
     outcomes ungrouped (TR-25), and exactly one whole-segment REST request per endpoint-delimited segment dispatched
     immediately at closure — never per word or frame — with failure settling that segment without discarding
     successful siblings, satisfying UR-12 and TR-21–TR-22, TR-25.
13. **AC-13:** Precedence tests verify a manual press abandons the open automatic speech group — every in-flight segment
     and unsettled outcome — without a public aggregate and suppresses automatic admission until the manual session
     finishes, satisfying UR-14 and TR-24.
14. **AC-14:** Threading and lifecycle tests verify automatic-path Godot API, state, and signal work stays on the Godot
     thread through the generation-checked boundary while REST network work runs off it, including during pending
     requests and teardown, satisfying TR-23.
15. **AC-15:** Ordered-settlement tests verify outcomes arriving out of order are stored immediately and published
    consecutively per group in `SegmentIndex` order, that blank, failed, or cancelled segments advance the ordering
    gate without invented text, that one segment's failure never discards successful siblings, and that separate
    groups drain independently, satisfying UR-15 and TR-26.
16. **AC-16:** Speech-owned dispatch-notification tests verify exactly one structured marker per logical request —
      manual session or automatic segment — emitted immediately before the REST transcription dispatch under
      `AlleyCat.Pipeline.STT` at `Debug`, carrying only the source, duration, and PCM byte count metadata, never
      transcript, audio, prompt, hotwords, credentials, endpoint, or request body, and that SDK-internal retries do
      not duplicate it; all other STT entries, including the log-only "STT backend returned in" milestone, remain
      non-eligible; and toggle coverage matches the shipped YAML semantics — the child inheriting the parent's
      `Debug` keeps console output and toast on by default, child `None` or any level above `Debug` suppresses the
      entry entirely, and child `Debug` or `Trace` enables it regardless of the parent — satisfying UR-16 and TR-27
      and cross-referencing CORE-007's generic routing tests.
17. **AC-17:** Lifecycle-signal tests verify each silent manual terminal path — transcriber teardown, transcriber
      replacement or disconnection, and disabled/cancelled manual capture — emits exactly one `RecordingAbandoned`
      notification with no completion or failure for that session, and that ordinary manual blank and failure
      completions settle without any abandonment notification, satisfying TR-28 and cross-referencing AI-001 UR-14.

**Traceability Map:** UR-1–UR-7 and TR-1–TR-2, TR-5–TR-11 → AC-1; UR-8 and TR-3, TR-12–TR-13 → AC-2–AC-3;
UR-9 and TR-4, TR-14 → AC-4; TR-15–TR-16 → AC-5–AC-6; out-of-scope guard → AC-7; TR-17 → AC-8; UR-10 and
TR-18–TR-19 → AC-9; UR-11 and TR-20 → AC-10; UR-6, UR-13 and TR-7–TR-8, TR-19 → AC-11; UR-12 and TR-21–TR-22, TR-25 →
AC-12; UR-14 and TR-24 → AC-13; TR-23 → AC-14; UR-15 and TR-26 → AC-15; UR-16 and TR-27 → AC-16; TR-28 → AC-17.

## References

### Implementation

- `@game/src/Speech/Transcription/Transcriber.cs`
- `@game/src/Speech/Transcription/OpenAITranscriber.cs`
- `@game/src/Speech/Transcription/SttDispatchLog.cs`
- `@game/src/Speech/Transcription/SttDispatchEntry.cs`
- `@game/src/Speech/Transcription/STTOptions.cs`
- `@game/src/Speech/Transcription/SileroVoiceActivityDetector.cs`
- `@game/src/Speech/Transcription/AutomaticUtteranceCoordinator.cs`
- `@game/src/Speech/Transcription/AutomaticVoiceInputOptions.cs`
- `@game/src/Speech/Transcription/RecordedAudioData.cs`
- `@game/src/Speech/Transcription/PCMAudioAccumulator.cs`
- `@game/src/Speech/Transcription/WaveFileStream.cs`
- `@game/default_bus_layout.tres`
- `@game/src/UI/NotificationUIExtensions.cs`
- `@tests/src/Speech/PCMAudioAccumulatorTests.cs`
- `@tests/src/Speech/WaveFileStreamTests.cs`
- `@tests/src/Speech/OpenAITranscriberTests.cs`
- `@tests/src/Speech/SttDispatchLogTests.cs`
- `@tests/src/Speech/AutomaticVoiceDetectionTests.cs`
- `@tests/src/Core/Logging/PipelineDebugLogTests.cs`
- `@integration-tests/src/Speech/TranscriberIntegrationTests.cs`
- `@integration-tests/src/Core/Logging/PipelineNotificationRoutingIntegrationTests.cs`

### Related Specs

- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [SPCH-005: Voice Component](../../speech/005-voice/index.md)
- [SPCH-008: Automatic Voice Detection](../../speech/008-automatic-voice-detection/index.md)
- [XR-001: XRManager](../../xr/001-xr-manager/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-006: Microsoft Configuration Integration](../../core/006-microsoft-configuration-integration/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)
- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)

### External Dependencies

- WhisperLive speech-to-text server, deployed REST-only on port 8000 (OpenAI-compatible
  `/v1/audio/transcriptions`).
- Godot XR Tools or native XR input API
