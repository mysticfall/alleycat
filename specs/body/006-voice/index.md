---
id: BODY-006
title: Voice Component
---

# Voice Component

## Requirement

Provide an abstract `Voice` component that represents an identifiable 3D speech origin, concrete `AIVoice` and
`PlayerVoice` implementations, FIFO speech submission, and listener dispatch for generated voice events.

## Goal

Enable reliable character and player speech whose requests are admitted without waiting for playback, while preserving
serial generation, spatial attribution, lip-sync, and safe node-lifetime behaviour.

## User Requirements

1. Players must hear AI-generated speech output with synchronised lip-sync when valid speech is requested.
2. Speech requests made while AI voice generation is busy must queue in request order rather than being rejected.
3. A caller that successfully submits speech must not wait for generation or playback to finish.
4. Blank speech, disabled output, and missing required configuration must fail clearly rather than report success.
5. Cancellation before admission must cancel the request; cancellation after admission must not retract queued work.
6. One failed speech item must not block later queued items, and failures must be logged without crashing or
   desynchronising later playback.
7. Runtime toggling through `Enabled` must remain supported.
8. Speech events must expose a stable voice `Id` and world-space `Origin` to listeners. The ID supports configured,
   operational attribution but is not authenticated provenance.
9. Completed nonblank player transcription must trigger player voice output; blank transcription must be ignored.
10. Removing a voice from the scene must prevent queued or active work from accessing freed Godot nodes.
11. A manual test scene must allow testers to enter arbitrary speech and observe character speech output.
12. NPC Hearing receives completed speech as immutable sensory data; Mind later filters self speech and attributes
    speakers by ordinal voice-ID values.

## Technical Requirements

1. An abstract `[GlobalClass]` `Voice : Node3D` must be defined under `AlleyCat.Body.Voice` and implement `IVoice`.
2. `IVoice : IComponent, IIdentifiable` must expose mutable authored `string Id`, `Vector3 Origin`,
    `ValueTask SpeakAsync(string speech, CancellationToken cancellationToken = default)`, and compatibility
    `void Speak(string speech)`.
3. `SpeakAsync` must return no dispatch-result value. Successful completion means the request has been submitted or
   admitted to the FIFO queue; it does not mean generation, playback hand-off, or playback has completed.
4. `SpeakAsync` must throw `ArgumentException` for blank speech and `InvalidOperationException` when voice output is
   disabled or required configuration is unavailable.
5. Cancellation observed before admission must surface as cancellation and admit no work. Cancellation after admission
   must not retract or cancel the committed queue item.
6. `Speak` must remain a safe, deliberately lossy compatibility API. It must perform synchronous validation where
   possible, initiate `SpeakAsync`, and explicitly observe and log or signal asynchronous faults so no task exception is
   abandoned.
7. `Voice` must expose exported mutable authored `Id` and `Enabled`, resolve `Origin` from `GlobalPosition`, retain the
    `SpeechFailed(string error)` signal, and provide deferred Godot action-dispatch helpers.
8. `IVoice.Type` is exactly `voice`, and its canonical CORE-009 `FullId` is exactly `voice:<id>`. Semantic voice
   identity comparisons use ordinal `Id` or `FullId` values as appropriate, never object-reference equality.
9. `Voice` must define a protected virtual post-generation hook. The hook must query `IVoiceListener.GroupName`, filter
    `IVoiceListener` implementations, and invoke them with the speech and source `IVoice`.
10. `IVoiceListener.GroupName` must remain the global Godot group constant `"voice_listeners"`.
11. Runtime control state such as `Enabled` must remain on `Voice`, not on the `IVoice` capability contract.
12. `IHasVoice` must follow the component-holder trait pattern and expose `TryGetVoice(out IVoice? voice)` and
    `RequireVoice()` over `IComponentHolder`.
13. `AIVoice` must admit valid requests atomically into one FIFO queue and drain it serially. At most one generation,
    conversion, lip-sync preparation, and playback hand-off pipeline may run at a time.
14. Busy requests must queue in admission order. `AIVoice` must not reject a valid request merely because another item
    is active.
15. For each admitted item, `AIVoice` must:
    - generate audio through the configured `SpeechGenerator`;
    - convert generated `byte[]` to compatible `AudioStreamWav` data;
    - await `LipSyncPlayer.PreparePlaybackAsync(...)`;
    - invoke `LipSyncPlayer.PlayPrepared(...)` as the playback initiation boundary; and
    - invoke the post-generation hook only after successful playback hand-off.
16. Generated audio must be PCM 16-bit, 16 kHz, mono WAV before lip-sync preparation. `SpeechGenerator` owns sample-rate
    normalisation; `AIVoice` must not resample.
17. Failure of an admitted item's generation, conversion, preparation, or hand-off must be logged and emit
    `SpeechFailed`. It must not notify listeners and must not prevent later FIFO items from running.
18. Voice or node teardown must settle active and queued submissions safely and prevent later callbacks from accessing
    freed Godot nodes. Expected teardown cancellation must not be reported as a generation failure.
19. `PlayerVoice` must subscribe once to its exported `Transcriber.TranscriptionCompleted` source during `_Ready()`,
    unsubscribe during `_ExitTree()`, and forward only nonblank transcript text through `Speak`.
20. The manual voice test scene must place `AIVoice` under the character's head attachment and keep playback audio
    spatially attached to the voice origin.
21. After CORE-005 target-scene precedence resolves the final `Character.Id`, generic character installation must assign
    every character-owned Voice local `Id` to that exact value. The resulting canonical voice identity is
    `voice:<character-id>`. Template placeholder voice IDs must be valid lower `snake_case`; installation replaces them
    before voice identity is exposed and validates the assigned voice identity at the final installation boundary.
    AI-001 may use the local ID for operational attribution; another source presenting the same ID is an accepted
    limitation rather than authenticated ownership.
22. `Voiceprint` is a listener-recognition key. Matching or possessing it does not prove that a voice is owned by a
    particular character.
23. Hearing is a Body component implementing `ISense` and `IVoiceListener`. It owns listener lifecycle and declares
    exactly `SpeechPercept`.
24. Hearing rejects only null, empty, or whitespace-only transport speech. For each accepted publication, it snapshots
    speech and the source's raw local `Id` into one immutable `SpeechPercept` and publishes it synchronously.
25. Hearing must not know its observer's voice, filter self speech, attribute a character, create an observation, or
    reference Mind. AI-006 assigns those interpretation responsibilities to `SpeechPerception`.

## In Scope

- Abstract `Voice` component identity, location, control, submission, compatibility, failure, and listener contracts.
- Non-result `IVoice.SpeakAsync(...)` and safe lossy `Speak(...)` compatibility.
- FIFO `AIVoice` admission and serial generation, preparation, and playback hand-off.
- Failure isolation and safe active and queued work settlement on teardown.
- `PlayerVoice` transcription integration.
- PCM 16-bit, 16 kHz, mono WAV compatibility and lip-sync synchronisation.
- Character-owned voice ID installation and spatial voice origins.
- Hearing-owned synchronous `SpeechPercept` acquisition for AI-006.
- Manual voice test scene and automated unit and integration coverage.

## Out Of Scope

- Visual verification or runtime XR testing, which requires backend access.
- Parallel speech generation or playback; admission queues, but production remains serial.
- Cancelling or reversing speech work after admission.
- New live microphone capture or real-time streaming beyond the existing `Transcriber` dependency.
- Additional speech-generation implementations beyond `AIVoice` and `PlayerVoice`.
- Audio processing beyond conversion to the required WAV format.
- Spatial hearing, acoustic propagation, distance attenuation, or directional perception filtering.
- Character animation beyond lip-sync and playback-completion notification.

## Voice Contract

| Member | Type | Description |
|--------|------|-------------|
| `Id` | `string` | Mutable authored local voice identifier. |
| `Type` | `string` | Read-only canonical type `voice`. |
| `FullId` | `string` | Canonical identifiable identity `voice:<id>`. |
| `Enabled` | `bool` | Controls whether speech is permitted. Default: `true`. |
| `Origin` | `Vector3` | World-space origin matching the voice node `GlobalPosition`. |
| `SpeakAsync(...)` | `ValueTask` | Cancellable FIFO submission; completion means admission only. |
| `Speak(string speech)` | `void` | Safe, lossy fire-and-forget compatibility initiator. |
| `SpeechFailed(string error)` | Signal | Reports an admitted item's asynchronous production failure. |
| Post-generation hook | Protected virtual method | Notifies grouped listeners after successful playback hand-off. |

## AIVoice Behaviour

1. Validate cancellation, text, enabled state, and required configuration before admission.
2. Atomically admit valid requests in FIFO order, including while another item is active.
3. Complete `SpeakAsync` when admission commits, without awaiting generation or playback.
4. Drain admitted items through one serial generation and playback-hand-off pipeline.
5. Isolate each item's failure, emit diagnostics, and continue with the next queued item.
6. Treat post-admission caller cancellation as non-retracting.
7. Settle the queue and active pipeline safely during node teardown.

## PlayerVoice Behaviour

1. Subscribe to the configured transcriber's completion event in `_Ready()` and unsubscribe in `_ExitTree()`.
2. Ignore null, empty, or whitespace-only transcript text.
3. Forward nonblank transcript text through `Speak(string speech)`.
4. Do not duplicate subscriptions or retain handlers after leaving the tree.

## Acceptance Criteria

1. Valid AI speech is heard with synchronised lip-sync and is attributable to the voice's stable `Id` and `Origin`.
2. `IVoice` exposes non-result `ValueTask SpeakAsync(...)` and compatibility `void Speak(...)`; no dispatch-result or
   busy-rejection contract remains.
3. Tests verify blank speech throws `ArgumentException`, while disabled or unconfigured speech throws
   `InvalidOperationException` before admission.
4. Tests verify cancellation before admission admits no work and surfaces as cancellation, while cancellation after
   admission does not retract the item.
5. Tests verify second and third busy submissions are admitted, processed FIFO, and never create concurrent generation
   pipelines.
6. Tests verify `SpeakAsync` completes at admission without awaiting generation, playback hand-off, or playback.
7. Tests verify one item's generation, conversion, preparation, or hand-off failure logs and emits `SpeechFailed`, does
   not notify listeners, and does not block the next item.
8. Tests verify compatibility `Speak` performs available synchronous validation and observes every asynchronous fault.
9. Tests verify teardown settles active and queued work without later access to freed nodes or misleading failure
   diagnostics.
10. Tests verify listener notification occurs only after successful playback hand-off and ignores grouped non-listeners.
11. Tests verify audio supplied to lip-sync is PCM 16-bit, 16 kHz, mono WAV and `AIVoice` does not resample it.
12. Tests verify `PlayerVoice` forwards one nonblank transcription, ignores blank transcription, honours `Enabled`, and
    disconnects on exit.
13. Generic installation replaces each valid lower-`snake_case` character-owned voice placeholder ID with the final
    exact `Character.Id` after target-scene precedence, validates `voice:<character-id>` before identity exposure, and
    supports configured attribution without claiming authenticated provenance or rejecting a source that presents the
    same ID.
14. Acceptance verifies both user-visible FIFO speech and failure isolation and the validation, admission,
    serialisation, cancellation, listener, and node-lifetime contracts.
15. Tests verify `IVoice : IComponent, IIdentifiable`, mutable authored local `Id`, exact Type `voice`, canonical
    `voice:<id>` `FullId`, and ordinal semantic identity comparison without object-reference equality.
16. Tests verify Hearing's Body composition and listener lifecycle, rejection of blank transport speech only, and one
    synchronous immutable speech/raw-source-ID percept for each accepted publication.
17. Tests verify Hearing has no observer-voice, attribution, observation, or Mind dependency; AI-006 speech perception
    owns ordinal self filtering and attribution.

## References

### Implementation

- `@game/src/Body/Voice/IVoice.cs`
- `@game/src/Body/Voice/IHasVoice.cs`
- `@game/src/Body/Voice/IVoiceListener.cs`
- `@game/src/Body/Voice/Voice.cs`
- `@game/src/Body/Voice/AIVoice.cs`
- `@game/src/Body/Voice/PlayerVoice.cs`
- `@game/src/Speech/Transcription/Transcriber.cs`
- `@game/tests/body/voice/voice_test.tscn`

### Related Specifications

- [AI-001: Mind Component](../../ai/001-mind/index.md)
- [AI-002: Agent Runtime](../../ai/002-agent-runtime/index.md)
- [AI-006: Percept-Based Sensing And Attention](../../ai/006-character-perception-and-attention/index.md)
- [SPCH-001: Wav2Arkit LipSync Player](../../speech/001-wav2arkit-lipsync-player/index.md)
- [SPCH-002: Audio2Face LipSync Player](../../speech/002-audio2face-lipsync-player/index.md)
- [SPCH-004: Speech Generator Component](../../speech/004-speech-generation/index.md)
- [CORE-002: Configuration API](../../core/002-configuration-api/index.md)
- [CORE-007: Microsoft Logging Integration](../../core/007-microsoft-logging-integration/index.md)
- [CHAR-002: Character Root](../../character/002-character-root/index.md)
- [SCN-001: Scene Context API](../../scene/001-scene-context-api/index.md)
