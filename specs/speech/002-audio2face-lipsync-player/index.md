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
9. Resolved regression-mode streaming makes speech audible after enough
    blendshape data has arrived to balance stable facial motion against a
    bounded startup delay, rather than after full inference.
10. Stopping speech promptly abandons the ongoing stream download instead of
    letting it run on in the background.
11. Temporary or residual blendshape starvation during streaming playback must
    not distort or stall playback: the face holds its last pose while audio
    continues, and playback resumes when frames catch up. Diffusion-mode
    playback is unaffected by the streaming changes.
12. Starting new speech during streamed playback cuts the predecessor's audio
    and facial animation immediately. The replacement starts only after the
    predecessor stream has settled; if it cannot settle within the bounded
    admission window, the new speech fails visibly and is not retried
    automatically.
13. Streaming diagnostics support contributor investigation without changing
    player-visible playback behaviour or exposing speech or player data.

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
5. Interruption and replacement-admission contract: If `Play` is called during
   active playback, cut current audio and facial-frame application immediately,
   then cancel its streaming download. Replacement admission is ordered and
   bounded: the client invariant is **prior `ReadLoop` settled before
   replacement request admission**. It waits only for a finite, configured
   settlement window; if the predecessor cannot settle, the replacement sets
   `PlaybackError`, fails through the visible caller/item failure path, makes no
   replacement request, and receives no automatic retry. Ordinary non-streaming
   `/blendshapes` requests need not be cancelled.
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
    uses `/blendshapes/stream` and requests the canonical `fps` rate, intended
    to default to 30 fps. This requested rate applies to Audio2Face regression
    streaming only; resolved diffusion mode (including v3 auto-adjusted) falls
    back to the batch `/blendshapes` endpoint.
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
17. Streaming metadata `fps` is the actual output rate. The player must use it,
    not the requested rate, to calculate the gate's frame count and synchronise
    frame consumption with audio.
18. First-frame playback gate: on the streaming path, `PreparePlaybackAsync`
    and manual `Play` complete only after metadata and the applicable frame gate
    have arrived. For a nonzero buffer setting, derive a conservative gate
    duration proportional to prepared speech duration, cap it at 5 seconds, and
    round the threshold up using the actual metadata fps to require at least one
    frame. A zero buffer setting opens only at the first valid frame after
    metadata. A completed short stream may open early with its available frames,
    but a zero-frame stream must not hand off playback. The HTTP read loop
    continues in the background, owned by the playback session, until the stream
    completes, fails, or is cancelled.
19. Starvation policy: when the playback cursor outpaces frame arrival, hold
    the last applied frame while audio continues, including residual starvation,
    and emit one correlated first-starvation diagnostic per episode. Resume
    applying frames once the download catches up and emit one correlated recovery
    diagnostic per episode. Audio completion still ends the session through the
    existing `IsAudioPlaying` polling; frames exhausted early clamp to the last
    frame; audio ending with an incomplete buffer logs a Warning summary. Each
    session logs Information diagnostics at completion — frames buffered at
    start, starvation episode count, and largest starved gap — so typical lag can
    be measured to tune the startup buffer.
20. After audio completion finalises a streaming session, later producer output
    is discarded. It must not create an artificial playback fault or change the
    completed outcome.
21. Streaming failure semantics: failures before the gate opens (or between
    gate and hand-off) flow through the existing preparation error paths and
    surface as item-level failures in `AIVoice`; a mid-playback stream failure
    (for example truncation without a complete record) sets `PlaybackError`,
    raises `PlaybackCompleted` so AIVoice speaking windows cannot hang, and
    stops playback. Neither case crashes or hangs.
22. Timeout model: `RequestTimeoutSeconds` remains the overall deadline for
    the whole streaming download; `StreamingIdleTimeoutSeconds` (export,
    default 10 s) bounds stalled inter-record gaps. Both surface as clear
    `TimeoutException`-based errors.
23. Eye-rotation translation approximation: batch inference baselines eye
    rotations against the clip-wide mean; the streaming converter seeds the
    baseline from the first valid eye-rotation record and applies the same
    smoothing, inversion, and directional mapping per frame. This documented
    approximation only affects clips that start mid-gaze, and the affected
    eyeLook channels are stripped by the eyes-controlled filter in both paths,
    so the approximation never reaches playback directly.
24. Base-class opt-in: streaming inference is opt-in through
    `SupportsStreamingInference` on the shared `LipSyncPlayer` base;
    batch-oriented backends (Wav2Arkit, test stubs) keep the full-response
    behaviour unchanged. `PreparedPlayback.PreparedFrameCount` reports the
    full frame count for batch data or the frames buffered so far under
    streaming, which AIVoice latency diagnostics consume.
25. Streaming-server recovery boundary: the standalone
    `~/workspace/audio2face-api-server` service owns its request intake, NDJSON
    output, disconnect recovery, observability, and verification. AlleyCat relies
    on the published HTTP and NDJSON contracts and disconnect outcomes, but does
    not reimplement or prescribe the service's internal logging, configuration,
    or tests.
26. Warm-service responsiveness target: the standalone service owns delivery
    and measurement of this target. For the flagged live-container smoke
    workflow, the first NDJSON record should arrive in under 10 seconds from a
    warm Audio2Face service. This is a feasibility target, not a production
    latency guarantee.
27. Cross-repository integration boundary: AlleyCat owns the client behaviour
    in this specification, including request consumption, record reading,
    immediate cut, cancellation, and the invariant **prior `ReadLoop` settled
    before replacement request admission**. The standalone service owns HTTP and
    NDJSON response production in requirement 16 and the service conditions in
    requirements 25–26. The client must rely on, but must not reimplement or
    prescribe, service-owned behaviour. The current service repository is a
    normative integration dependency; the obsolete
    `~/workspace/Audio2Face-3D-SDK/audio2face-api-server` checkout is not a
    source of truth.
28. Streaming diagnostics are client-owned, observability-only, and low-volume.
    For each streaming request, AlleyCat sends an opaque
    `X-Client-Stream-Id` request header and includes that ID in its diagnostics.
    AlleyCat logs request and response, gate opening, reader terminal outcome,
    cancellation origin, starvation episode and recovery, and playback end.
    Client diagnostics must not log request or response payloads, waveforms,
    query parameters, player data, or other speech content, and must not alter
    player-visible behaviour, timing, request routing, cancellation, or playback
    outcomes. The standalone service owns any use of this header and its own
    observability; this specification does not prescribe service logging,
    configuration, or tests.

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
  timeout and bounded ordered replacement-admission contracts.
- Client-owned, low-volume streaming diagnostics, including the correlation
  header, required client events, and privacy boundaries.
- Standalone-service integration boundary and service-owned observability.
- Cross-repository integration and verification with the standalone
  `audio2face-api-server` service.
- Shared `LipSyncPlayer` playback-completed notification and stop/cut capability.

## Out Of Scope

- Production-grade latency budgets or recovery policies beyond the required
  bounded replacement-failure and disconnect-handling contracts. Live-smoke
  timing assertions are feasibility bounds, not latency budgets.
- Live microphone capture or real-time audio input pipelines.
- Dialogue system integration or runtime model switching.
- Animation polish and expressive-quality acceptance criteria.
- Fixing or claiming to fix a separate whole-game freeze; this specification
  governs Audio2Face lip-sync playback reliability only.
- Docker container lifecycle management.
- Automated regression beyond the required mock-backed recovery tests and the
  flagged, soft-skipping live-container smoke test. Diagnostics implementation
  and validation required by this specification remain in scope.

## Acceptance Criteria

1. Specification defines both prototype outcomes (user layer) and integration
   contracts (technical layer).
2. Prototype scope is bounded as feasibility work, not production runtime
   guarantees.
3. HTTP integration, audio-format contracts, and playback synchronisation
   (batch and streaming) are explicitly defined.
4. Manual playback contract: Playback triggers via `Play(AudioStreamWav)`, not
   auto-started in `_Ready()`.
5. User interruption outcome: Calling `Play` during active streamed playback
   cuts current audio and facial animation immediately. The replacement begins
   only after bounded, ordered admission; if the predecessor cannot settle, the
   new speech fails visibly without an automatic retry.
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
14. Streaming gate: tests verify regression streaming requests the canonical
    default intended 30 fps, metadata fps drives the gate and playback timing,
    and preparation completes before the server sends the complete record. They
    verify a conservative duration-scaled nonzero gate with a 5-second cap,
    zero-buffer hand-off at the first valid frame, audible playback immediately
    after hand-off, and background download completion after playback starts,
    matching the declared frame count.
15. Starvation: tests verify slower-than-playback frame arrival holds the last
    applied frame (applied count and mesh values unchanged while starved), emits
    one correlated first-starvation diagnostic and one correlated recovery
    diagnostic per episode, resumes when frames catch up, and completes cleanly
    with the starvation window counted as one episode. Tests verify late producer
    output after audio completion is discarded without `PlaybackError` or an
    artificial failure. Final session counters and the incomplete-buffer summary
    remain available where applicable.
16. Streaming interruption and replacement admission: tests verify `Stop()`
    cancels the in-flight download; the read loop settles; the server observes
    the client abort; no `PlaybackCompleted` fires for the cut session; and the
    buffer never reports complete. Tests also verify rapid `Play()` calls cut
    the predecessor immediately, admit a replacement request only after the
    prior `ReadLoop` settles, and replay cleanly. When settlement exceeds the
    bounded admission window, tests verify a visible new-speech failure, no
    automatic retry, and no replacement request.
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
    the frame-buffer gate. They verify actual metadata fps consumption, the
    conservative duration-scaled nonzero threshold and 5-second cap, first-frame
    zero-buffer hand-off, short-stream handling, no zero-frame hand-off, producer
    failure before the gate, and consistency under parallel append/read.
21. Live smoke against the real local container (flagged, soft-skipped when
    unreachable) verifies the gate opens well before the full download time and
    the first NDJSON record arrives in under 10 seconds from a warm service.
    It also verifies the stream completes with the declared count of finite,
    consistently sized frames.
22. Acceptance verifies both layers: user-visible early audio, interruption,
    and starvation behaviour, and the endpoint, gate, mode-routing, timeout,
    failure, opt-in, replacement-admission, server-disconnect, warm-service, and
    client-diagnostics contracts.
23. Service-boundary validation confirms that AlleyCat consumes the published
    HTTP and NDJSON contracts without prescribing the current standalone
    `~/workspace/audio2face-api-server` service's internal observability,
    configuration, or test coverage. The obsolete SDK-nested server is not valid
    integration evidence.
24. AlleyCat validation verifies its client boundary independently: unit and
    integration tests prove immediate cut and the ordered-admission invariant,
    including no replacement request after failed predecessor settlement. The
    flagged live-container smoke test consumes the standalone service and
    verifies the warm-service first-record target; it does not substitute for
    standalone service verification owned by that service.
25. Diagnostics validation verifies both layers and code/spec alignment:
    diagnostics do not change player-visible behaviour and do not expose
    payloads, waveforms, query parameters, player data, or other speech content.
    AlleyCat implementation must align with technical requirement 28: every
    streaming request includes the opaque `X-Client-Stream-Id` header, and client
    diagnostics cover every required client event. This acceptance does not
    prescribe standalone-service logging, configuration, or test coverage.

## References

- `@game/src/Speech/LipSync/A2FLipSyncPlayer.cs`
- `@game/src/Speech/LipSync/LipSyncPlayer.cs`
- `@game/src/Speech/LipSync/StreamingFrameBuffer.cs`
- `@game/src/Speech/LipSync/A2fStreamingRecordReader.cs`
- `@game/src/Speech/LipSync/A2fEyeBlendshapeMapping.cs`
- `@game/tests/speech/a2f_lipsync_player_test.tscn`
- `@integration-tests/src/Speech/A2FStreamingLipSyncIntegrationTests.cs`
- Normative standalone service repository: `~/workspace/audio2face-api-server`
  (not `~/workspace/Audio2Face-3D-SDK/audio2face-api-server`).
