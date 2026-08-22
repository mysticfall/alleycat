---
id: SPCH-002
title: Audio2Face LipSync Player
---

# Audio2Face LipSync Player

## Requirement

Drive character facial animation from speech audio using NVIDIA Audio2Face
blendshape inference served over HTTP. Replace local ONNX inference in
`LipSyncPlayer` with remote HTTP calls to the Audio2Face service. Resolved
regression mode streams inference output from the server so speech becomes
audible before the whole clip has been inferred.

## Goal

Evaluate Audio2Face quality for production use through a feasibility prototype.
The prototype demonstrates ARKit output with dedicated eye-rotation data for
accurate eye movement, and streaming playback that becomes audible as soon as
enough blendshape data has arrived rather than after full inference.
Contributors can validate server connectivity and playback with a repeatable
workflow when they opt in to startup probing.

## User Requirements

1. Playback produces observable facial animation from supported speech audio
   via remote Audio2Face inference.
2. Eye movement output is more accurate when eye-rotation translation is
   enabled.
3. Default development and test startup remains non-blocking when the
   Audio2Face backend is not running.
4. Startup must not emit a connection-refused initialisation error by default
   when the backend is absent.
5. Contributors can opt in to initialisation-time connectivity validation and
   playback checks with a repeatable workflow.
6. Playback is triggered manually via `LipSyncPlayer.Play(AudioStreamWav)`
   method.
7. Callers can cut off active playback, halting both audio and facial animation
   immediately.
8. Callers can observe when audible playback has finished.
9. Speech becomes audible as soon as enough streamed blendshape data has
   arrived — after a startup buffer, not after full inference — under
   regression-mode streaming playback.
10. Stopping speech promptly abandons the ongoing stream download instead of
    letting it run on in the background.
11. Temporary blendshape starvation during streaming playback must not distort
    or stall playback: the face holds its last pose while audio continues, and
    playback resumes when frames catch up. Diffusion-mode playback is
    unaffected by the streaming changes.

## Technical Requirements

1. Player converts the base-normalised inference input — PCM 16-bit, mono at the
   backend sample rate — to the float32 PCM Audio2Face API payload format.
2. Inference uses the Audio2Face HTTP endpoints with configurable server URI:
   the batch `/blendshapes` endpoint and, for resolved regression mode, the
   streaming `/blendshapes/stream` endpoint. Both take the same float32 mono
   16 kHz PCM body and query parameters.
3. Returned frames are mapped into the `LipSyncPlayer` base class with audio
   synchronisation.
4. Player exposes `Play(AudioStreamWav speech)` for manual playback initiation.
5. Interruption contract: If `Play` is called during active playback, stop
   current playback and begin new playback immediately. In-flight streaming
   inference downloads must be cancelled, with rapid re-play cancelling the
   previous session's read loop before a new session starts; ordinary
   non-streaming `/blendshapes` requests need not be cancelled.
6. Optional eye-rotation translation defines baseline subtraction, smoothing,
   directional mapping, and clamp rules.
7. Model/mode compatibility and health probing behaviour are explicitly
   defined.
8. Format enforcement is the shared `LipSyncPlayer` base's job: the base
   validates PCM 16-bit, mono input and normalises it to the backend's declared
   `BackendSampleRate` = 16000 in the preparation path. This backend adds no
   format checks of its own and receives inference input at that rate by
   contract.
9. `ProbeHealthOnInitialise` is an exported setting and defaults to `false`.
10. When `ProbeHealthOnInitialise` is `false`, initialisation must not call
    `/health`, block on backend availability, or log a connection-refused error
    because the backend is not running.
11. When `ProbeHealthOnInitialise` is `true`, initialisation performs `/health`
    probing and preserves the existing retry and failure semantics.
12. Inference endpoint behaviour is unchanged by startup health probing.
13. The shared `LipSyncPlayer` base must raise a playback-completed notification
    (typed C# event or Godot signal) when audible playback finishes, observed
    through its existing `IsAudioPlaying` polling in `_Process`.
14. The shared `LipSyncPlayer` base must expose a stop/cut capability that halts
    both audio playback and lip-sync frame application immediately, and that
    cancels the active streaming session's background download (session-linked
    cancellation aborts the read loop and the HTTP request). Mind interruption
    uses it to cut audible speech; ordinary non-streaming `/blendshapes`
    requests keep running to completion.
15. Mode routing is decided client-side before the request is sent: resolved
    regression mode (including auto-adjusted Mark, Claire, and James models)
    uses `/blendshapes/stream`; resolved diffusion mode (including v3
    auto-adjusted) falls back to the batch `/blendshapes` endpoint.
16. Streaming wire contract: `POST /blendshapes/stream` responds with chunked
    `application/x-ndjson` — one metadata record (`fps`, `blendshape_names`)
    first, then frame records carrying a sequential zero-based `index`,
    `weights`, and optional `eye_rotation`, terminated by a `complete` record
    (`frame_count`). The reader stops at the complete record; a stream that
    ends without one fails. Ordering violations, malformed records,
    channel-count mismatches, and declared-versus-received count mismatches
    fail with clear errors; unknown record types are skipped with one warning
    per type. The server receives the whole request body before inference
    begins, so the endpoint output-streams only.
17. First-frame playback gate: on the streaming path, `PreparePlaybackAsync`
    and manual `Play` complete once the metadata record and a startup buffer of
    frames have arrived (`StreamingStartupBufferSeconds`, default 0.1 s —
    about 6 frames at 60 fps; zero opens at the first frame). The HTTP read
    loop continues in the background, owned by the playback session, until the
    stream completes, fails, or is cancelled.
18. Starvation policy: when the playback cursor outpaces frame arrival, hold
    the last applied frame while audio continues and emit a rate-limited
    warning (2 s interval); resume applying frames once the download catches
    up. Audio completion still ends the session through the existing
    `IsAudioPlaying` polling; frames exhausted early clamp to the last frame;
    audio ending with an incomplete buffer logs a Warning summary. Each
    session logs Information diagnostics at completion — frames buffered at
    start, starvation episode count, and largest starved gap — so typical lag
    can be measured to tune the startup buffer.
19. Streaming failure semantics: failures before the gate opens (or between
    gate and hand-off) flow through the existing preparation error paths and
    surface as item-level failures in `AIVoice`; a mid-playback stream failure
    (for example truncation without a complete record) sets `PlaybackError`,
    raises `PlaybackCompleted` so AIVoice speaking windows cannot hang, and
    stops playback. Neither case crashes or hangs.
20. Timeout model: `RequestTimeoutSeconds` remains the overall deadline for
    the whole streaming download; `StreamingIdleTimeoutSeconds` (export,
    default 10 s) bounds stalled inter-record gaps. Both surface as clear
    `TimeoutException`-based errors.
21. Eye-rotation translation approximation: batch inference baselines eye
    rotations against the clip-wide mean; the streaming converter seeds the
    baseline from the first valid eye-rotation record and applies the same
    smoothing, inversion, and directional mapping per frame. This documented
    approximation only affects clips that start mid-gaze, and the affected
    eyeLook channels are stripped by the eyes-controlled filter in both paths,
    so the approximation never reaches playback directly.
22. Base-class opt-in: streaming inference is opt-in through
    `SupportsStreamingInference` on the shared `LipSyncPlayer` base;
    batch-oriented backends (Wav2Arkit, test stubs) keep the full-response
    behaviour unchanged. `PreparedPlayback.PreparedFrameCount` reports the
    full frame count for batch data or the frames buffered so far under
    streaming, which AIVoice latency diagnostics consume.

## In Scope

- HTTP integration with remote Audio2Face inference service.
- Streaming inference endpoint integration for regression mode with early
  audible playback.
- ARKit blendshape output with eye-rotation translation.
- Configurable server URI for inference and optional health probing.
- Optional `/health` probing at initialisation, disabled by default.
- Manual playback trigger via `Play(AudioStreamWav)` method.
- Shared-base audio format validation and inference-input normalisation.
- Interruption handling for active playback.
- Streaming playback gate, starvation hold, stream cancellation on stop, and
  timeout contracts.
- Per-session streaming playback diagnostics.
- Shared `LipSyncPlayer` playback-completed notification and stop/cut capability.

## Out Of Scope

- Production runtime guarantees (latency budgets, error recovery). Live-smoke
  timing assertions are generous feasibility bounds, not latency budgets.
- Live microphone capture or real-time audio input pipelines.
- Dialogue system integration or runtime model switching.
- Animation polish and expressive-quality acceptance criteria.
- Docker container lifecycle management.
- Automated regression beyond mock-backed integration tests and the flagged,
  soft-skipping live-container smoke test.

## Acceptance Criteria

1. Specification defines both prototype outcomes (user layer) and integration
   contracts (technical layer).
2. Prototype scope is bounded as feasibility work, not production runtime
   guarantees.
3. HTTP integration, audio-format contracts, and playback synchronisation
   (batch and streaming) are explicitly defined.
4. Manual playback contract: Playback triggers via `Play(AudioStreamWav)`, not
   auto-started in `_Ready()`.
5. Interruption contract: Calling `Play` during active playback stops current
   playback and begins new playback immediately. In-flight streaming downloads
   are cancelled; ordinary non-streaming `/blendshapes` requests are not.
6. Eye-rotation translation behaviour and fallback handling are defined.
7. Default startup succeeds without blocking or logging a connection-refused
   initialisation error when the Audio2Face backend is not running.
8. Health probing is opt-in through exported `ProbeHealthOnInitialise`, which
   defaults to `false`.
9. Opted-in health probing uses the existing `/health` retry and failure
   semantics.
10. Startup health-probe configuration does not change inference endpoint
    behaviour (batch or streaming).
11. Out Of Scope does not exclude mandatory implementation requirements.
12. Shared-base playback-completed notification is defined: raised when audible
    playback finishes via `IsAudioPlaying` polling.
13. Shared-base stop/cut capability is defined: halts audio and lip-sync frame
    application immediately, cancels the active streaming download, and leaves
    ordinary non-streaming inference requests running.
14. Streaming gate: tests verify preparation completes before the server sends
    the complete record, that the startup buffer filled before the gate opened,
    that audible playback starts immediately after hand-off, and that the
    background download completes after playback started, matching the declared
    frame count.
15. Starvation: tests verify slower-than-playback frame arrival holds the last
    applied frame (applied count and mesh values unchanged while starved),
    warns through the rate limiter, resumes when frames catch up, and completes
    cleanly with the starvation window counted as one episode.
16. Interruption during streaming: tests verify `Stop()` cancels the in-flight
    download — the read loop settles, the server observes the client abort, no
    `PlaybackCompleted` fires for the cut session, the buffer never reports
    complete, and a fresh streaming session replays cleanly.
17. Mid-playback stream failure: tests verify a stream that closes without the
    complete record sets `PlaybackError` with a clear message, raises
    `PlaybackCompleted` once, and stops playback without hanging.
18. Mode routing: tests verify a diffusion-resolved model (v3 auto-adjusted)
    requests the batch `/blendshapes` endpoint and completes end-to-end, and
    that mode resolution stays unit-testable without a Godot runtime or live
    server.
19. Timeouts: tests verify a stream stalling past `RequestTimeoutSeconds` fails
    with a request-timeout error, and a record gap exceeding
    `StreamingIdleTimeoutSeconds` fails with an idle-timeout error; both are
    `TimeoutException`-based.
20. Unit tests verify the NDJSON record contract (metadata/frame/complete
    ordering, validation, error cases, and chunk-boundary line reassembly) and
    the frame-buffer gate (opens at the default startup buffer, at the first
    frame for a zero-second buffer, early on short streams, faulted when the
    producer fails before the gate, and consistent under parallel append/read).
21. Live smoke against the real local container (flagged, soft-skipped when
    unreachable) verifies the gate opens well before the full download time and
    the stream completes with the declared count of finite, consistently sized
    frames.
22. Acceptance verifies both layers: user-visible early audio, interruption,
    and starvation behaviour, and the endpoint, gate, mode-routing, timeout,
    failure, and opt-in contracts.

## References

- `@game/src/Speech/LipSync/A2FLipSyncPlayer.cs`
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
- `@game/src/Speech/LipSync/StreamingFrameBuffer.cs`
- `@game/src/Speech/LipSync/A2fStreamingRecordReader.cs`
- `@game/src/Speech/LipSync/A2fEyeBlendshapeMapping.cs`
- `@game/tests/speech/a2f_lipsync_player_test.tscn`
- `@integration-tests/src/Speech/A2FStreamingLipSyncIntegrationTests.cs`
